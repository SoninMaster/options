using System.Globalization;
using System.Text;
using Laevitas.StandaloneClient;
using Laevitas.StandaloneClient.Models;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== Laevitas Standalone Client ===");
Console.WriteLine("Fetches option-skew data from the Laevitas API and computes derived metrics.");
Console.WriteLine();

string? apiKey = Environment.GetEnvironmentVariable("LAEVITAS_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Write("Laevitas API key: ");
    apiKey = ReadSecret();
}

if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("API key is required.");
    return 1;
}

Console.Write("Token symbol [BTC]: ");
var token = Console.ReadLine();
if (string.IsNullOrWhiteSpace(token)) token = "BTC";

Console.Write("Start date (yyyy-MM-dd, UTC) [today]: ");
var startStr = Console.ReadLine();
var startDate = string.IsNullOrWhiteSpace(startStr)
    ? DateTime.UtcNow.Date
    : DateTime.ParseExact(startStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);

Console.Write($"End date (yyyy-MM-dd, UTC) [{startDate:yyyy-MM-dd}]: ");
var endStr = Console.ReadLine();
var endDate = string.IsNullOrWhiteSpace(endStr)
    ? startDate
    : DateTime.ParseExact(endStr, "yyyy-MM-dd", CultureInfo.InvariantCulture);

Console.Write("Output CSV file path (leave empty to print to console): ");
var csvPath = Console.ReadLine();

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
var logger = loggerFactory.CreateLogger<LaevitasClient>();

using var client = new LaevitasClient(apiKey, logger);

try
{
    var daily = await client.FetchRangeAsync(startDate, endDate, token);

    if (!string.IsNullOrWhiteSpace(csvPath))
    {
        WriteCsv(csvPath!, daily);
        Console.WriteLine($"Wrote {daily.Count} daily rows to {csvPath}");
    }
    else
    {
        PrintTable(daily);
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 2;
}

static string ReadSecret()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString(); }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) sb.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
        }
    }
}

static void PrintTable(IReadOnlyList<OptionSkewDailyData> rows)
{
    Console.WriteLine();
    Console.WriteLine("Date       | Token | WeightedSum | Factor   | SMA30    | EMA30    | Price");
    Console.WriteLine(new string('-', 80));
    foreach (var r in rows)
    {
        Console.WriteLine(
            $"{r.Time:yyyy-MM-dd} | {r.TokenSymbol,-5} | {Fmt(r.WeightedSum),11} | {Fmt(r.Factor),8} | {Fmt(r.SMA30),8} | {Fmt(r.EMA30),8} | {Fmt(r.Price)}");
    }
}

static string Fmt(decimal? v) =>
    v.HasValue ? v.Value.ToString("F4", CultureInfo.InvariantCulture) : "-";

static void WriteCsv(string path, IReadOnlyList<OptionSkewDailyData> rows)
{
    using var sw = new StreamWriter(path, append: false, Encoding.UTF8);
    sw.WriteLine("Date,Token,D1,D7,D14,M1,M2,M3,M6,Y1,Price,WeightedSum,MinWeighted,MaxWeighted,Factor,SMA30,EMA30,ScaledFactor,ScaledSMA30,ScaledEMA30");
    foreach (var r in rows)
    {
        sw.Write($"{r.Time:yyyy-MM-dd},{r.TokenSymbol},");
        sw.Write($"{C(r.D1)},{C(r.D7)},{C(r.D14)},{C(r.M1)},{C(r.M2)},{C(r.M3)},{C(r.M6)},{C(r.Y1)},");
        sw.Write($"{C(r.Price)},{C(r.WeightedSum)},{C(r.MinWieghted)},{C(r.MaxWieghted)},");
        sw.WriteLine($"{C(r.Factor)},{C(r.SMA30)},{C(r.EMA30)},{C(r.ScaledFactor)},{C(r.ScaledSMA30)},{C(r.ScaledEMA30)}");
    }
}

static string C(decimal? v) =>
    v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "";
