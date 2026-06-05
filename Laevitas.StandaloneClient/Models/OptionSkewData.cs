namespace Laevitas.StandaloneClient.Models;

public abstract class OptionSkewBase
{
    public const decimal D7Weight = 0.4m;
    public const decimal M1Weight = 0.3m;
    public const decimal M3Weight = 0.2m;
    public const decimal M6Weight = 0.1m;
    public const int scalingValue = 8;
    public const int MovingAverageDays = 30;

    public DateTime Time { get; set; }
    public long Timestamp { get; set; }
    public decimal? D1 { get; set; }
    public decimal? D7 { get; set; }
    public decimal? D14 { get; set; }
    public decimal? M1 { get; set; }
    public decimal? M2 { get; set; }
    public decimal? M3 { get; set; }
    public decimal? M6 { get; set; }
    public decimal? Y1 { get; set; }
    public decimal? Price { get; set; }
    public virtual decimal? WeightedSum { get; set; }
    public decimal? Flip => -WeightedSum;
    public string TokenSymbol { get; set; } = "BTC";

    public void SetTime() =>
        Time = DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).UtcDateTime;
}

public class OptionSkew5MinData : OptionSkewBase
{
    // Bars with no skew tenors at all (e.g. an entry carrying only an unrelated tenor)
    // are "empty" and must be excluded from the daily average rather than counted as 0,
    // otherwise they drag the daily WeightedSum down and skew the Factor min/max window.
    public override decimal? WeightedSum =>
        (D7 is null && M1 is null && M3 is null && M6 is null)
            ? null
            : (D7.HasValue ? D7 * D7Weight : 0) +
              (M1.HasValue ? M1 * M1Weight : 0) +
              (M3.HasValue ? M3 * M3Weight : 0) +
              (M6.HasValue ? M6 * M6Weight : 0);
}

public class OptionSkewDailyData : OptionSkewBase
{
    public decimal? MinWieghted { get; set; }
    public decimal? MaxWieghted { get; set; }
    public decimal? Factor { get; set; }
    public decimal? SMA30 { get; set; }
    public decimal? EMA30 { get; set; }
    public decimal? ScaledWeightenedSum =>
        WeightedSum.HasValue
            ? WeightedSum > scalingValue ? scalingValue
                : (WeightedSum < -scalingValue ? -scalingValue : WeightedSum)
            : null;
    public decimal? ScaledFactor { get; set; }
    public decimal? ScaledSMA30 { get; set; }
    public decimal? ScaledEMA30 { get; set; }
    public decimal? ScaledFlip => -ScaledWeightenedSum;
}
