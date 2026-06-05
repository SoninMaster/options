using Microsoft.Data.Sqlite;
using Laevitas.StandaloneClient.Models;

namespace LaevitasWeb;

/// <summary>
/// SQLite-backed cache for daily option-skew base data.
/// We only persist the daily averages (price + weighted-sum components); the rolling
/// metrics (Factor / SMA30 / EMA30) are recomputed on read over the full series so they
/// always use a complete window. A separate `day_fetched` table records which days we have
/// already attempted, so empty days are not re-fetched on every request.
/// </summary>
public sealed class SkewStore
{
    private readonly string _connStr;
    private readonly object _writeLock = new();

    public SkewStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connStr = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Init();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connStr);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Init()
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS skew_daily(
  token TEXT NOT NULL, date TEXT NOT NULL, ts INTEGER NOT NULL,
  d1 REAL, d7 REAL, d14 REAL, m1 REAL, m2 REAL, m3 REAL, m6 REAL, y1 REAL,
  price REAL, weighted_sum REAL, min_weighted REAL, max_weighted REAL,
  PRIMARY KEY(token, date));
CREATE TABLE IF NOT EXISTS day_fetched(
  token TEXT NOT NULL, date TEXT NOT NULL,
  PRIMARY KEY(token, date));";
            cmd.ExecuteNonQuery();
        }
    }

    public HashSet<string> GetFetchedDates(string token)
    {
        var set = new HashSet<string>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT date FROM day_fetched WHERE token = $t";
        cmd.Parameters.AddWithValue("$t", token);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public (string? Min, string? Max, int Count) Coverage(string token)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MIN(date), MAX(date), COUNT(*) FROM skew_daily WHERE token = $t";
        cmd.Parameters.AddWithValue("$t", token);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            var min = r.IsDBNull(0) ? null : r.GetString(0);
            var max = r.IsDBNull(1) ? null : r.GetString(1);
            var count = r.GetInt32(2);
            return (min, max, count);
        }
        return (null, null, 0);
    }

    public void MarkFetched(string token, string date)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO day_fetched(token, date) VALUES($t, $d)";
            cmd.Parameters.AddWithValue("$t", token);
            cmd.Parameters.AddWithValue("$d", date);
            cmd.ExecuteNonQuery();
        }
    }

    public void Upsert(string token, OptionSkewDailyData d)
    {
        var date = d.Time.ToString("yyyy-MM-dd");
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = @"
INSERT INTO skew_daily(token,date,ts,d1,d7,d14,m1,m2,m3,m6,y1,price,weighted_sum,min_weighted,max_weighted)
VALUES($token,$date,$ts,$d1,$d7,$d14,$m1,$m2,$m3,$m6,$y1,$price,$ws,$minw,$maxw)
ON CONFLICT(token,date) DO UPDATE SET
  ts=excluded.ts, d1=excluded.d1, d7=excluded.d7, d14=excluded.d14,
  m1=excluded.m1, m2=excluded.m2, m3=excluded.m3, m6=excluded.m6, y1=excluded.y1,
  price=excluded.price, weighted_sum=excluded.weighted_sum,
  min_weighted=excluded.min_weighted, max_weighted=excluded.max_weighted;";
            cmd.Parameters.AddWithValue("$token", token);
            cmd.Parameters.AddWithValue("$date", date);
            cmd.Parameters.AddWithValue("$ts", d.Timestamp);
            cmd.Parameters.AddWithValue("$d1", Box(d.D1));
            cmd.Parameters.AddWithValue("$d7", Box(d.D7));
            cmd.Parameters.AddWithValue("$d14", Box(d.D14));
            cmd.Parameters.AddWithValue("$m1", Box(d.M1));
            cmd.Parameters.AddWithValue("$m2", Box(d.M2));
            cmd.Parameters.AddWithValue("$m3", Box(d.M3));
            cmd.Parameters.AddWithValue("$m6", Box(d.M6));
            cmd.Parameters.AddWithValue("$y1", Box(d.Y1));
            cmd.Parameters.AddWithValue("$price", Box(d.Price));
            cmd.Parameters.AddWithValue("$ws", Box(d.WeightedSum));
            cmd.Parameters.AddWithValue("$minw", Box(d.MinWieghted));
            cmd.Parameters.AddWithValue("$maxw", Box(d.MaxWieghted));
            cmd.ExecuteNonQuery();

            using var mark = c.CreateCommand();
            mark.CommandText = "INSERT OR IGNORE INTO day_fetched(token, date) VALUES($t, $d)";
            mark.Parameters.AddWithValue("$t", token);
            mark.Parameters.AddWithValue("$d", date);
            mark.ExecuteNonQuery();
        }
    }

    /// <summary>Loads the full stored series for a token, ordered by date ascending.</summary>
    public List<OptionSkewDailyData> LoadAll(string token)
    {
        var list = new List<OptionSkewDailyData>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = @"
SELECT date, ts, d1,d7,d14,m1,m2,m3,m6,y1, price, weighted_sum, min_weighted, max_weighted
FROM skew_daily WHERE token = $t ORDER BY date ASC";
        cmd.Parameters.AddWithValue("$t", token);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var d = new OptionSkewDailyData
            {
                Time = DateTime.SpecifyKind(DateTime.ParseExact(r.GetString(0), "yyyy-MM-dd", null), DateTimeKind.Utc),
                Timestamp = r.GetInt64(1),
                D1 = Dec(r, 2),
                D7 = Dec(r, 3),
                D14 = Dec(r, 4),
                M1 = Dec(r, 5),
                M2 = Dec(r, 6),
                M3 = Dec(r, 7),
                M6 = Dec(r, 8),
                Y1 = Dec(r, 9),
                Price = Dec(r, 10),
                WeightedSum = Dec(r, 11),
                MinWieghted = Dec(r, 12),
                MaxWieghted = Dec(r, 13),
                TokenSymbol = token,
            };
            list.Add(d);
        }
        return list;
    }

    private static object Box(decimal? v) => v.HasValue ? (double)v.Value : DBNull.Value;
    private static decimal? Dec(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (decimal)r.GetDouble(i);
}
