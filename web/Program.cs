using System.Globalization;
using Laevitas.StandaloneClient.Models;
using LaevitasWeb;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var dbPath = Environment.GetEnvironmentVariable("SKEW_DB_PATH") ?? "/data/skew.db";
builder.Services.AddSingleton(new SkewStore(dbPath));
builder.Services.AddSingleton<BackfillService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Range query: ensures the requested range (+ warm-up) is cached, then returns computed rows.
app.MapPost("/api/skew", async (SkewRequest req, BackfillService svc, ILoggerFactory lf, CancellationToken ct) =>
{
    if (!TryParseDate(req.Start, out var start))
        return Results.BadRequest(new { error = "Invalid start date (use yyyy-MM-dd)." });

    var end = start;
    if (!string.IsNullOrWhiteSpace(req.End) && !TryParseDate(req.End, out end))
        return Results.BadRequest(new { error = "Invalid end date (use yyyy-MM-dd)." });

    if (end < start)
        return Results.BadRequest(new { error = "End date must be on or after start date." });

    if ((end - start).TotalDays > 366)
        return Results.BadRequest(new { error = "Range too large – use 'Load full history' for long spans." });

    var token = NormalizeToken(req.Token);
    try
    {
        await svc.EnsureRangeAsync(token, start.AddDays(-WarmupDays()), end, ct);
        var rows = svc.LoadComputed(token)
            .Where(r => r.Time.Date >= start.Date && r.Time.Date <= end.Date)
            .Select(ToRow).ToList();
        return Results.Ok(new { token, start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd"), warmupDays = WarmupDays(), rows });
    }
    catch (Exception ex)
    {
        lf.CreateLogger("api").LogError(ex, "skew range failed");
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

// Starts (or resumes) a background full-history backfill from inception.
app.MapPost("/api/load", (LoadRequest req, BackfillService svc) =>
{
    var token = NormalizeToken(req.Token);
    var status = svc.StartInceptionLoad(token);
    return Results.Ok(status);
});

// Progress + coverage for the background load.
app.MapGet("/api/status", (string? token, BackfillService svc) =>
    Results.Ok(svc.GetStatus(NormalizeToken(token))));

// Returns the full cached + computed series for a token.
app.MapGet("/api/data", (string? token, BackfillService svc) =>
{
    var t = NormalizeToken(token);
    var rows = svc.LoadComputed(t).Select(ToRow).ToList();
    var first = rows.Count > 0 ? rows[0].Date : null;
    var last = rows.Count > 0 ? rows[^1].Date : null;
    return Results.Ok(new { token = t, start = first, end = last, rows });
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// The source panel displays the weighted sum and its daily extremes as "flip" (negated).
static decimal? Neg(decimal? v) => v.HasValue ? -v.Value : null;

static SkewRow ToRow(OptionSkewDailyData r) => new(
    r.Time.ToString("yyyy-MM-dd"),
    r.TokenSymbol,
    r.Price,
    Neg(r.MaxWieghted),   // daily minimum of the (negated) weighted sum
    Neg(r.MinWieghted),   // daily maximum of the (negated) weighted sum
    Neg(r.WeightedSum),   // daily weighted sum, shown negative like the source
    r.Factor,
    r.SMA30,
    r.EMA30,
    r.ScaledFactor,
    r.ScaledSMA30,
    r.ScaledEMA30);

static string NormalizeToken(string? t) =>
    string.IsNullOrWhiteSpace(t) ? "BTC" : t.Trim().ToUpperInvariant();

// History fetched before a requested range to warm up the rolling 30-day metrics.
static int WarmupDays()
    => int.TryParse(Environment.GetEnvironmentVariable("SKEW_WARMUP_DAYS"), out var d) && d >= 0 ? d : 60;

static bool TryParseDate(string? s, out DateTime date) =>
    DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

record SkewRequest(string? Token, string? Start, string? End);
record LoadRequest(string? Token);

record SkewRow(
    string Date,
    string Token,
    decimal? Price,
    decimal? MinWeighted,
    decimal? MaxWeighted,
    decimal? WeightedSum,
    decimal? Factor,
    decimal? Sma30,
    decimal? Ema30,
    decimal? ScaledFactor,
    decimal? ScaledSma30,
    decimal? ScaledEma30);
