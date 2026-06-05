using Laevitas.StandaloneClient.Models;

namespace Laevitas.StandaloneClient;

/// <summary>
/// Pure calculations extracted from the original LaevitasService:
/// daily average, Factor / ScaledFactor, SMA30 / EMA30 (also Scaled variants).
/// </summary>
public static class SkewCalculator
{
    public static OptionSkewDailyData? CalculateDailyAverage(IReadOnlyList<OptionSkew5MinData> dayData, DateTime date)
    {
        // Only average over bars that actually carry skew data. Empty bars (no tenors)
        // have a null WeightedSum and are dropped, matching the source tool's behavior.
        var valid = dayData.Where(x => x.WeightedSum.HasValue).ToList();
        if (valid.Count == 0) return null;

        return new OptionSkewDailyData
        {
            Time = date.Date,
            Timestamp = new DateTimeOffset(date.Date, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            D1 = Avg(valid, x => x.D1),
            D7 = Avg(valid, x => x.D7),
            D14 = Avg(valid, x => x.D14),
            M1 = Avg(valid, x => x.M1),
            M2 = Avg(valid, x => x.M2),
            M3 = Avg(valid, x => x.M3),
            M6 = Avg(valid, x => x.M6),
            Y1 = Avg(valid, x => x.Y1),
            Price = Avg(valid, x => x.Price),
            WeightedSum = Avg(valid, x => x.WeightedSum),
            MinWieghted = valid.Min(x => x.WeightedSum),
            MaxWieghted = valid.Max(x => x.WeightedSum),
            TokenSymbol = valid[0].TokenSymbol,
        };
    }

    public static void PopulateFactorsAndMovingAverages(List<OptionSkewDailyData> entries)
    {
        if (entries.Count == 0) return;
        entries.Sort((a, b) => a.Time.CompareTo(b.Time));

        var flipCache = new decimal?[entries.Count];
        var scaledFlipCache = new decimal?[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            flipCache[i] = entries[i].Flip;
            scaledFlipCache[i] = entries[i].ScaledFlip;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            entries[i].Factor = CalculateFactor(flipCache, i);
            entries[i].ScaledFactor = CalculateFactor(scaledFlipCache, i);

            entries[i].SMA30 = MovingAverage(entries, i, e => e.Factor);
            entries[i].ScaledSMA30 = MovingAverage(entries, i, e => e.ScaledFactor);

            entries[i].EMA30 = Ema(entries, i, e => e.Factor, e => e.EMA30,
                (lst, idx) => MovingAverage(lst, idx, e => e.Factor));
            entries[i].ScaledEMA30 = Ema(entries, i, e => e.ScaledFactor, e => e.ScaledEMA30,
                (lst, idx) => MovingAverage(lst, idx, e => e.ScaledFactor));
        }
    }

    private static decimal? Avg<T>(IEnumerable<T> source, Func<T, decimal?> selector)
    {
        var values = source.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? null : values.Average();
    }

    private static decimal? CalculateFactor(decimal?[] flipCache, int currentIndex)
    {
        var current = flipCache[currentIndex];
        if (current is null) return null;
        if (currentIndex == 0) return 0m;

        var startIndex = Math.Max(0, currentIndex - OptionSkewBase.MovingAverageDays + 1);
        var valid = new List<decimal>(currentIndex - startIndex + 1);
        for (int i = startIndex; i <= currentIndex; i++)
            if (flipCache[i].HasValue) valid.Add(flipCache[i]!.Value);

        if (valid.Count == 0) return null;

        var min = valid[0];
        var max = valid[0];
        for (int i = 1; i < valid.Count; i++)
        {
            if (valid[i] < min) min = valid[i];
            if (valid[i] > max) max = valid[i];
        }

        var denominator = max - min;
        if (denominator == 0m) denominator = 0.0001m;

        var scalingValue = OptionSkewBase.scalingValue;
        return -scalingValue + (current.Value - min) * (scalingValue * 2) / denominator;
    }

    private static decimal? MovingAverage(
        List<OptionSkewDailyData> entries, int currentIndex, Func<OptionSkewDailyData, decimal?> selector)
    {
        var startIndex = Math.Max(0, currentIndex - OptionSkewBase.MovingAverageDays + 1);
        decimal sum = 0;
        int count = 0;
        for (int i = startIndex; i <= currentIndex; i++)
        {
            var v = selector(entries[i]);
            if (v.HasValue) { sum += v.Value; count++; }
        }
        return count == 0 ? null : sum / count;
    }

    private static decimal? Ema(
        List<OptionSkewDailyData> entries, int currentIndex,
        Func<OptionSkewDailyData, decimal?> valueSelector,
        Func<OptionSkewDailyData, decimal?> previousEmaSelector,
        Func<List<OptionSkewDailyData>, int, decimal?> smaCalculator)
    {
        var currentValue = valueSelector(entries[currentIndex]);
        if (currentIndex == 0 || currentValue is null) return currentValue;

        var alpha = 2m / (OptionSkewBase.MovingAverageDays + 1);
        var previousEma = previousEmaSelector(entries[currentIndex - 1]) ?? smaCalculator(entries, currentIndex - 1);
        if (previousEma is null) return currentValue;
        return alpha * currentValue + (1 - alpha) * previousEma.Value;
    }
}
