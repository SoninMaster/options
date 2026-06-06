using System.Collections.Concurrent;
using Laevitas.StandaloneClient;
using Laevitas.StandaloneClient.Models;

namespace LaevitasWeb;

public sealed class JobStatus
{
    public bool Running { get; set; }
    public string Phase { get; set; } = "idle";
    public int Total { get; set; }
    public int Done { get; set; }
    public string? Error { get; set; }
    public string? CoverageMin { get; set; }
    public string? CoverageMax { get; set; }
    public int CoverageCount { get; set; }
}

/// <summary>
/// Fetches daily skew data from Laevitas and persists it in <see cref="SkewStore"/>.
/// Range requests are served synchronously; full-history (inception) loads run in the
/// background with progress reporting.
/// </summary>
public sealed class BackfillService
{
    private readonly SkewStore _store;
    private readonly ILoggerFactory _lf;
    private readonly ILogger<BackfillService> _logger;
    private readonly string? _apiKey;

    // Earliest date we will probe when looking for a token's inception.
    private static readonly DateTime ProbeFloor = new(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ConcurrentDictionary<string, JobStatus> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _startLock = new();

    // Politeness delay between day fetches to stay under the Laevitas rate limit.
    private static int DayDelayMs =>
        int.TryParse(Environment.GetEnvironmentVariable("SKEW_DAY_DELAY_MS"), out var v) && v >= 0 ? v : 150;
    private const int MaxPasses = 6;

    public BackfillService(SkewStore store, ILoggerFactory lf)
    {
        _store = store;
        _lf = lf;
        _logger = lf.CreateLogger<BackfillService>();
        _apiKey = Environment.GetEnvironmentVariable("LAEVITAS_API_KEY");
    }

    public JobStatus GetStatus(string token)
    {
        var status = _jobs.TryGetValue(token, out var s) ? s : new JobStatus();
        var (min, max, count) = _store.Coverage(token);
        status.CoverageMin = min;
        status.CoverageMax = max;
        status.CoverageCount = count;
        return status;
    }

    /// <summary>Synchronously ensures all days in [from, to] are fetched and stored.</summary>
    public async Task EnsureRangeAsync(string token, DateTime from, DateTime to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Server API key is not configured.");

        var today = DateTime.UtcNow.Date;
        if (to > today) to = today;

        var fetched = _store.GetFetchedDates(token);
        using var client = new LaevitasClient(_apiKey!, _lf.CreateLogger<LaevitasClient>()) { MaxRetries = 4 };

        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            if (fetched.Contains(d.ToString("yyyy-MM-dd"))) continue;
            await FetchAndStoreDayAsync(client, token, d, ct);
            if (DayDelayMs > 0) await Task.Delay(DayDelayMs, ct);
        }
    }

    /// <summary>Starts (or returns the running) background backfill from inception to today.</summary>
    public JobStatus StartInceptionLoad(string token)
    {
        lock (_startLock)
        {
            if (_jobs.TryGetValue(token, out var existing) && existing.Running)
                return GetStatus(token);

            var status = new JobStatus { Running = true, Phase = "probing inception", Done = 0, Total = 0 };
            _jobs[token] = status;
            _ = Task.Run(() => RunInceptionLoadAsync(token, status));
            return GetStatus(token);
        }
    }

    private async Task RunInceptionLoadAsync(string token, JobStatus status)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("Server API key is not configured.");

            var today = DateTime.UtcNow.Date;
            using var client = new LaevitasClient(_apiKey!, _lf.CreateLogger<LaevitasClient>()) { MaxRetries = 4 };

            // If probing is inconclusive, fall back to the floor and let the strict
            // backfill mark genuinely-empty pre-inception days (no permanent gaps).
            var inception = await FindInceptionAsync(client, token, today, CancellationToken.None)
                            ?? ProbeFloor;
            _logger.LogInformation("Inception for {Token}: {Date}", token, inception.ToString("yyyy-MM-dd"));

            var totalDays = (int)(today.Date - inception.Date).TotalDays + 1;
            status.Phase = "downloading history";
            status.Total = totalDays;
            status.Done = _store.Coverage(token).Count;

            // Multiple passes: rate-limited / failed days are not marked as fetched, so a
            // subsequent pass retries only the remaining gaps until everything is stored.
            for (int pass = 0; pass < MaxPasses; pass++)
            {
                var fetched = _store.GetFetchedDates(token);
                var pending = new List<DateTime>();
                for (var d = inception.Date; d <= today; d = d.AddDays(1))
                    if (!fetched.Contains(d.ToString("yyyy-MM-dd")))
                        pending.Add(d);

                if (pending.Count == 0) break;

                foreach (var d in pending)
                {
                    await FetchAndStoreDayAsync(client, token, d, CancellationToken.None);
                    status.Done = _store.Coverage(token).Count;
                    if (DayDelayMs > 0) await Task.Delay(DayDelayMs, CancellationToken.None);
                }
            }

            status.Done = _store.Coverage(token).Count;
            status.Phase = "done";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inception load failed for {Token}", token);
            status.Error = ex.Message;
            status.Phase = "error";
        }
        finally
        {
            status.Running = false;
        }
    }

    private async Task FetchAndStoreDayAsync(LaevitasClient client, string token, DateTime day, CancellationToken ct)
    {
        try
        {
            // strict: throws on a real HTTP failure, so an empty result genuinely means
            // "no data for this day" (safe to mark) rather than a failed fetch.
            var (_, daily) = await client.FetchDayWithAverageAsync(day, token, ct, throwOnFailure: true, fillMissing: false);
            if (daily is not null)
                _store.Upsert(token, daily);
            else
                _store.MarkFetched(token, day.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            // Real failure: leave the day pending so a later pass retries it (no gap).
            _logger.LogWarning("Failed to fetch {Token} {Date}: {Msg}", token, day.ToString("yyyy-MM-dd"), ex.Message);
        }
    }

    /// <summary>Binary-searches the earliest UTC date that returns data for the token.</summary>
    private async Task<DateTime?> FindInceptionAsync(LaevitasClient client, string token, DateTime today, CancellationToken ct)
    {
        // Reliable check: strict fetch (throws on HTTP failure) with a few retries.
        // Returns true/false definitively, or null if it could not be determined.
        async Task<bool?> HasData(DateTime d)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    var items = await client.Fetch5MinDataForDayAsync(d, token, ct, throwOnFailure: true);
                    return items.Count > 0;
                }
                catch
                {
                    await Task.Delay(1500 * (attempt + 1), ct);
                }
            }
            return null;
        }

        var hi = today.AddDays(-1);
        var hiHas = await HasData(hi);
        if (hiHas is null) return null;        // cannot probe reliably -> caller falls back
        if (hiHas == false) return null;       // no data at all

        var lo = ProbeFloor;
        var loHas = await HasData(lo);
        if (loHas == true) return lo;
        if (loHas is null) return ProbeFloor;  // uncertain at floor -> start from floor

        // Invariant: lo has no data, hi has data. Find first date with data.
        while ((hi - lo).TotalDays > 1)
        {
            var mid = lo.AddDays(Math.Floor((hi - lo).TotalDays / 2));
            var midHas = await HasData(mid);
            if (midHas is null) return lo.AddDays(1); // uncertain -> conservative start
            if (midHas == true) hi = mid;
            else lo = mid;
        }
        return hi;
    }

    /// <summary>Reads the full stored series and computes rolling metrics over it.</summary>
    public List<OptionSkewDailyData> LoadComputed(string token)
    {
        var list = _store.LoadAll(token);
        SkewCalculator.PopulateFactorsAndMovingAverages(list);
        return list;
    }
}
