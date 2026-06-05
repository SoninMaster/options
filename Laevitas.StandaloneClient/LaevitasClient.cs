using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Laevitas.StandaloneClient.Models;
using Microsoft.Extensions.Logging;

namespace Laevitas.StandaloneClient;

/// <summary>
/// Standalone client for the Laevitas options-skew API.
/// No database dependency – caller decides what to do with the returned data
/// (save to file, DB, memory, etc.).
/// </summary>
public sealed class LaevitasClient : IDisposable
{
    private const string SkewUrlTemplate =
        "https://api.laevitas.ch/historical/options/type/skew/DERIBIT/{0}/25D?start={1}&end={2}&granularity=5m&page={3}&limit=288";

    private const string IndexUrlTemplate =
        "https://api.laevitas.ch/historical/options/dvol/DERIBIT/{0}?start={1}&end={2}&page={3}&limit=288";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<LaevitasClient> _logger;
    private readonly bool _ownsHttpClient;

    public int MaxRetries { get; set; } = 3;
    public int DelayMsBetweenPages { get; set; } = 500;

    public LaevitasClient(string apiKey, ILogger<LaevitasClient>? logger = null, HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key must be provided.", nameof(apiKey));

        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LaevitasClient>.Instance;

        if (httpClient is null)
        {
            _httpClient = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 8
            });
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        _httpClient.DefaultRequestHeaders.Remove("apiKey");
        _httpClient.DefaultRequestHeaders.Add("apiKey", apiKey);
    }

    /// <summary>
    /// Fetches 5-minute skew data + index prices for a single UTC date.
    /// Missing 5-minute intervals are filled by duplicating the previous valid entry
    /// (matches the original LaevitasService behavior).
    /// </summary>
    public async Task<IReadOnlyList<OptionSkew5MinData>> Fetch5MinDataForDayAsync(
        DateTime date,
        string tokenSymbol = "BTC",
        CancellationToken cancellationToken = default)
    {
        var startDateStr = date.ToString("yyyy-MM-dd");
        var endDateStr = startDateStr;

        var skewTask = FetchPageWithRetryAsync<SkewApiResponse>(
            string.Format(SkewUrlTemplate, tokenSymbol, startDateStr, endDateStr, 1), cancellationToken);
        var indexTask = FetchPageWithRetryAsync<IndexApiResponse>(
            string.Format(IndexUrlTemplate, tokenSymbol, startDateStr, endDateStr, 1), cancellationToken);

        await Task.WhenAll(skewTask, indexTask);

        var skewResponse = skewTask.Result;
        var indexResponse = indexTask.Result;

        if (skewResponse?.Items is null || indexResponse?.Items is null)
        {
            _logger.LogWarning("Failed to fetch initial page data for date {Date}", startDateStr);
            return Array.Empty<OptionSkew5MinData>();
        }

        var allSkewItems = new List<SkewItem>(skewResponse.Items);
        var priceByTimestamp = new Dictionary<long, double?>();
        foreach (var item in indexResponse.Items)
            priceByTimestamp[item.DateUnixMs] = item.IndexPrice;

        var totalPages = skewResponse.Meta.TotalPages;
        for (int page = 2; page <= totalPages; page++)
        {
            var skewPageTask = FetchPageWithRetryAsync<SkewApiResponse>(
                string.Format(SkewUrlTemplate, tokenSymbol, startDateStr, endDateStr, page), cancellationToken);
            var indexPageTask = FetchPageWithRetryAsync<IndexApiResponse>(
                string.Format(IndexUrlTemplate, tokenSymbol, startDateStr, endDateStr, page), cancellationToken);

            await Task.WhenAll(skewPageTask, indexPageTask);

            var skewPage = skewPageTask.Result;
            var indexPage = indexPageTask.Result;

            if (skewPage?.Items is null || indexPage?.Items is null)
            {
                _logger.LogWarning("Failed to fetch page {Page} data for date {Date}", page, startDateStr);
                continue;
            }

            foreach (var item in indexPage.Items)
                priceByTimestamp[item.DateUnixMs] = item.IndexPrice;
            allSkewItems.AddRange(skewPage.Items);

            await Task.Delay(DelayMsBetweenPages, cancellationToken);
        }

        var completeSkewItems = FillMissing5MinuteIntervals(allSkewItems, startDateStr);
        var result = new List<OptionSkew5MinData>(completeSkewItems.Count);
        foreach (var skewItem in completeSkewItems)
            result.Add(MapTo5MinData(skewItem, priceByTimestamp, tokenSymbol));

        return result;
    }

    /// <summary>
    /// Fetches 5-minute data and computes a single daily-average record for the given date.
    /// </summary>
    public async Task<(IReadOnlyList<OptionSkew5MinData> FiveMin, OptionSkewDailyData? Daily)> FetchDayWithAverageAsync(
        DateTime date,
        string tokenSymbol = "BTC",
        CancellationToken cancellationToken = default)
    {
        var fiveMin = await Fetch5MinDataForDayAsync(date, tokenSymbol, cancellationToken);
        var daily = SkewCalculator.CalculateDailyAverage(fiveMin, date);
        return (fiveMin, daily);
    }

    /// <summary>
    /// Fetches data for a range of UTC dates (inclusive). Returns a daily-average list
    /// with Factor / SMA30 / EMA30 / Scaled* fields computed.
    /// </summary>
    public async Task<IReadOnlyList<OptionSkewDailyData>> FetchRangeAsync(
        DateTime startDateInclusive,
        DateTime endDateInclusive,
        string tokenSymbol = "BTC",
        IProgress<(DateTime Date, int Index, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dailyList = new List<OptionSkewDailyData>();
        var dates = new List<DateTime>();
        for (var d = startDateInclusive.Date; d <= endDateInclusive.Date; d = d.AddDays(1))
            dates.Add(d);

        int i = 0;
        foreach (var date in dates)
        {
            i++;
            progress?.Report((date, i, dates.Count));
            _logger.LogInformation("Processing {Index}/{Total}: {Date}", i, dates.Count, date.ToString("yyyy-MM-dd"));

            try
            {
                var (_, daily) = await FetchDayWithAverageAsync(date, tokenSymbol, cancellationToken);
                if (daily is not null)
                    dailyList.Add(daily);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process {Date}", date.ToString("yyyy-MM-dd"));
            }

            if (i < dates.Count)
                await Task.Delay(50, cancellationToken);
        }

        SkewCalculator.PopulateFactorsAndMovingAverages(dailyList);
        return dailyList;
    }

    private static OptionSkew5MinData MapTo5MinData(SkewItem skewItem, Dictionary<long, double?> priceByTimestamp, string tokenSymbol)
    {
        var data = new OptionSkew5MinData
        {
            Timestamp = skewItem.DateUnixMs,
            D1 = (decimal?)skewItem.D1,
            D7 = (decimal?)skewItem.D7,
            D14 = (decimal?)skewItem.D14,
            M1 = (decimal?)skewItem.D30,
            M2 = (decimal?)skewItem.D60,
            M3 = (decimal?)skewItem.D90,
            M6 = (decimal?)skewItem.D180,
            Y1 = (decimal?)skewItem.D365,
            TokenSymbol = tokenSymbol
        };
        data.SetTime();

        if (priceByTimestamp.TryGetValue(skewItem.DateUnixMs, out var price) && price.HasValue)
            data.Price = (decimal)price.Value;

        return data;
    }

    private List<SkewItem> FillMissing5MinuteIntervals(List<SkewItem> apiItems, string dateStr)
    {
        if (apiItems.Count == 0)
        {
            _logger.LogWarning("No API items to process for date {Date}", dateStr);
            return apiItems;
        }

        var sorted = apiItems.OrderBy(x => x.DateUnixMs).ToList();
        var firstTs = sorted[0].DateUnixMs;
        var lastTs = sorted[^1].DateUnixMs;

        var expected = GenerateExpectedTimestampsInRange(firstTs, lastTs);
        var existing = sorted.ToDictionary(x => x.DateUnixMs, x => x);

        var complete = new List<SkewItem>(expected.Count);
        SkewItem? lastValid = null;
        int missing = 0;

        foreach (var ts in expected)
        {
            if (existing.TryGetValue(ts, out var existingItem))
            {
                complete.Add(existingItem);
                lastValid = existingItem;
            }
            else if (lastValid is not null)
            {
                complete.Add(new SkewItem
                {
                    DateUnixMs = ts,
                    D1 = lastValid.D1,
                    D7 = lastValid.D7,
                    D14 = lastValid.D14,
                    D30 = lastValid.D30,
                    D60 = lastValid.D60,
                    D90 = lastValid.D90,
                    D180 = lastValid.D180,
                    D365 = lastValid.D365
                });
                missing++;
            }
        }

        if (missing > 0)
            _logger.LogWarning("Filled {Count} missing 5-min intervals for {Date}", missing, dateStr);

        return complete;
    }

    private static List<long> GenerateExpectedTimestampsInRange(long startTs, long endTs)
    {
        var timestamps = new List<long>();
        var start = DateTimeOffset.FromUnixTimeMilliseconds(startTs);
        var rounded = new DateTimeOffset(start.Year, start.Month, start.Day,
            start.Hour, (start.Minute / 5) * 5, 0, start.Offset);

        var current = rounded;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(endTs);
        while (current <= end)
        {
            timestamps.Add(current.ToUnixTimeMilliseconds());
            current = current.AddMinutes(5);
        }
        return timestamps;
    }

    private async Task<T?> FetchPageWithRetryAsync<T>(string url, CancellationToken cancellationToken)
    {
        int attempt = 0;
        int delay = 1000;
        while (true)
        {
            attempt++;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var res = await _httpClient.SendAsync(req, cancellationToken);
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (attempt < MaxRetries)
            {
                _logger.LogWarning("Fetch {Url}: attempt {Attempt} failed ({Type}: {Msg}). Retrying in {Delay} ms...",
                    url, attempt, ex.GetType().Name, ex.Message, delay);
                await Task.Delay(delay, cancellationToken);
                delay = Math.Min(delay * 2, 8000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetch {Url} failed after {Attempt} attempts.", url, attempt);
                return default;
            }
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
