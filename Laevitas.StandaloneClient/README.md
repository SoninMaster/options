# Laevitas Standalone Client

A self-contained .NET 9 console application that fetches options skew data
from the [Laevitas](https://laevitas.ch/) API and computes derived metrics
(weighted sum, Factor, SMA30, EMA30 and their scaled variants).

The project has **no database dependency** – it simply prints results to the
console or writes them to a CSV file. You are free to consume `LaevitasClient`
as a library and persist the returned objects wherever you want
(file, database, in-memory cache, etc.).

## Build & run

```bash
dotnet build
dotnet run --project Laevitas.StandaloneClient
```

You will be prompted for:

| Input        | Description                                              |
|--------------|----------------------------------------------------------|
| API key      | Your personal Laevitas API key (kept in memory only).    |
| Token symbol | Defaults to `BTC`. Any symbol supported by Laevitas.     |
| Start date   | UTC date (`yyyy-MM-dd`).                                 |
| End date     | UTC date (`yyyy-MM-dd`). Defaults to start date.         |
| CSV path     | Optional. If empty, results are printed to the console.  |

You can also set the API key via the `LAEVITAS_API_KEY` environment variable
to skip the interactive prompt.

## Using the client as a library

```csharp
using var client = new LaevitasClient("YOUR_API_KEY");

// Single day with daily average
var (fiveMin, daily) = await client.FetchDayWithAverageAsync(DateTime.UtcNow.Date);

// Multi-day range with Factor / SMA30 / EMA30
var dailySeries = await client.FetchRangeAsync(
    new DateTime(2025, 9, 1),
    new DateTime(2025, 9, 30),
    tokenSymbol: "BTC");
```

`OptionSkew5MinData` and `OptionSkewDailyData` are plain POCOs – serialize them
to JSON, push them into Entity Framework, or store them in any other backend
of your choice.
