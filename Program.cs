using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddMemoryCache();

// CWMS (USACE) client
builder.Services.AddHttpClient<CwmsClient>(http =>
{
    http.BaseAddress = new Uri("https://cwms-data.usace.army.mil/cwms-data/");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

// NWS client (weather.gov)
builder.Services.AddHttpClient<NwsClient>(http =>
{
    http.BaseAddress = new Uri("https://api.weather.gov/");
    http.DefaultRequestHeaders.Accept.ParseAdd("application/geo+json");
    http.DefaultRequestHeaders.UserAgent.ParseAdd("BullShoalsWidget/1.0 (you@example.com)");
});

builder.Services.AddSingleton<WidgetService>();

builder.Services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});
tst
var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", async (WidgetService svc) =>
{
    var status = await svc.GetStatusAsync();
    return Results.Json(status);
});

app.Run();

record TrendPoint(DateTimeOffset TimeUtc, string TimeCentral, double Value);
record Trend(TrendPoint Current, TrendPoint Previous)
{
    public double Delta => Math.Round(Current.Value - Previous.Value, 2);
}

record WeatherNow(string Summary, double TemperatureF);

record WidgetStatus(
    Trend LakeLevelFt,
    Trend TailwaterFt,
    WeatherNow Weather
);

sealed class WidgetService(
    IMemoryCache cache,
    CwmsClient cwms,
    NwsClient nws)
{
    // Bull Shoals office
    private const string Office = "SWL";
    private const string LakeTs = "Bull_Shoals_Dam-Headwater.Elev.Inst.1Hour.0.Decodes-rev";
    private const string TailTs = "Bull_Shoals_Dam-Tailwater.Elev-Downstream.Inst.1Hour.0.Decodes-rev";

    // Bull Shoals area coordinates
    private const double Lat = 36.3647;
    private const double Lon = -92.5781;

    public async Task<WidgetStatus> GetStatusAsync()
    {
        return await cache.GetOrCreateAsync("widget-status", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);

            var lake = await cwms.GetLastTwoAsync(Office, LakeTs, units: "ft");
            var tail = await cwms.GetLastTwoAsync(Office, TailTs, units: "ft");
            var weather = await nws.GetCurrentAsync(Lat, Lon);

            return new WidgetStatus(lake, tail, weather);
        }) ?? throw new Exception("Cache failure");
    }
}

sealed class CwmsClient(HttpClient http)
{
    private static readonly TimeZoneInfo CentralTz = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Central Standard Time" : "America/Chicago"
    );

    private static DateTimeOffset ToCentral(DateTimeOffset tUtc) =>
        TimeZoneInfo.ConvertTime(tUtc, CentralTz);

    private static DateTimeOffset FromUnixNumber(JsonElement n)
    {
        long raw = n.TryGetInt64(out var i64) ? i64 : (long)n.GetDouble();

        return raw > 1_000_000_000_000L
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeSeconds(raw);
    }

    public async Task<Trend> GetLastTwoAsync(string office, string name, string units)
    {
        var end = DateTimeOffset.UtcNow;
        var begin = end.AddHours(-12);

        var url =
            $"timeseries?office={Uri.EscapeDataString(office)}" +
            $"&name={Uri.EscapeDataString(name)}" +
            $"&begin={Uri.EscapeDataString(begin.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&end={Uri.EscapeDataString(end.ToString("O", CultureInfo.InvariantCulture))}" +
            $"&unit={Uri.EscapeDataString(units)}";

        using var resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var values = doc.RootElement.GetProperty("values");
        if (values.GetArrayLength() < 2)
            throw new Exception($"Not enough data points for {name}");

        TrendPoint? curr = null;
        TrendPoint? prev = null;

        for (int i = values.GetArrayLength() - 1; i >= 0; i--)
        {
            var row = values[i];

            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2)
                continue;

            if (row[1].ValueKind != JsonValueKind.Number)
                continue;

            if (curr is null) curr = ParsePoint(row);
            else { prev = ParsePoint(row); break; }
        }

        if (curr is null || prev is null)
            throw new Exception($"Not enough numeric points for {name}");

        return new Trend(curr, prev);
    }

    private static TrendPoint ParsePoint(JsonElement triple)
    {
        var tEl = triple[0];

        DateTimeOffset utc =
            tEl.ValueKind switch
            {
                JsonValueKind.String => DateTimeOffset.Parse(
                    tEl.GetString()!,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal),

                JsonValueKind.Number => FromUnixNumber(tEl),

                _ => throw new Exception($"Unsupported timestamp JSON type: {tEl.ValueKind}")
            };

        var central = ToCentral(utc);

        var vEl = triple[1];
        if (vEl.ValueKind != JsonValueKind.Number)
            throw new Exception($"Unexpected value type (expected number): {vEl.ValueKind}");

        var v = vEl.GetDouble();

        return new TrendPoint(
            TimeUtc: utc,
            TimeCentral: central.ToString("yyyy-MM-dd HH:mm zzz"),
            Value: v
        );
    }
}

sealed class NwsClient(HttpClient http)
{
    public async Task<WeatherNow> GetCurrentAsync(double lat, double lon)
    {
        using var pResp = await http.GetAsync($"points/{lat},{lon}");
        pResp.EnsureSuccessStatusCode();

        using var pStream = await pResp.Content.ReadAsStreamAsync();
        using var pDoc = await JsonDocument.ParseAsync(pStream);

        var forecastUrl = pDoc.RootElement
            .GetProperty("properties")
            .GetProperty("forecastHourly")
            .GetString();

        if (string.IsNullOrWhiteSpace(forecastUrl))
            throw new Exception("NWS forecastHourly URL missing.");

        using var fResp = await http.GetAsync(forecastUrl);
        fResp.EnsureSuccessStatusCode();

        using var fStream = await fResp.Content.ReadAsStreamAsync();
        using var fDoc = await JsonDocument.ParseAsync(fStream);

        var first = fDoc.RootElement
            .GetProperty("properties")
            .GetProperty("periods")[0];

        var tempF = first.GetProperty("temperature").GetDouble();
        var summary = first.GetProperty("shortForecast").GetString() ?? "Forecast";

        return new WeatherNow(summary, tempF);
    }
}
