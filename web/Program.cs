using System.Globalization;
using Laevitas.StandaloneClient;
using Laevitas.StandaloneClient.Models;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/skew", async (SkewRequest req, ILoggerFactory lf, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ApiKey))
        return Results.BadRequest(new { error = "API key is required." });

    if (!TryParseDate(req.Start, out var start))
        return Results.BadRequest(new { error = "Invalid start date (use yyyy-MM-dd)." });

    var end = start;
    if (!string.IsNullOrWhiteSpace(req.End) && !TryParseDate(req.End, out end))
        return Results.BadRequest(new { error = "Invalid end date (use yyyy-MM-dd)." });

    if (end < start)
        return Results.BadRequest(new { error = "End date must be on or after start date." });

    if ((end - start).TotalDays > 120)
        return Results.BadRequest(new { error = "Range too large – max 120 days per request." });

    var token = string.IsNullOrWhiteSpace(req.Token) ? "BTC" : req.Token.Trim().ToUpperInvariant();

    var logger = lf.CreateLogger<LaevitasClient>();
    try
    {
        using var client = new LaevitasClient(req.ApiKey, logger);
        var daily = await client.FetchRangeAsync(start, end, token, cancellationToken: ct);

        var rows = daily.Select(r => new SkewRow(
            r.Time.ToString("yyyy-MM-dd"),
            r.TokenSymbol,
            r.Price,
            r.WeightedSum,
            r.Factor,
            r.SMA30,
            r.EMA30,
            r.ScaledFactor,
            r.ScaledSMA30,
            r.ScaledEMA30)).ToList();

        return Results.Ok(new { token, start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd"), rows });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Skew fetch failed");
        return Results.Json(new { error = ex.Message }, statusCode: 502);
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();

static bool TryParseDate(string? s, out DateTime date) =>
    DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

record SkewRequest(string? ApiKey, string? Token, string? Start, string? End);

record SkewRow(
    string Date,
    string Token,
    decimal? Price,
    decimal? WeightedSum,
    decimal? Factor,
    decimal? Sma30,
    decimal? Ema30,
    decimal? ScaledFactor,
    decimal? ScaledSma30,
    decimal? ScaledEma30);
