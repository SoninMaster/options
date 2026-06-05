using System.Text.Json.Serialization;

namespace Laevitas.StandaloneClient.Models;

public sealed class SkewApiResponse
{
    [JsonPropertyName("items")]
    public List<SkewItem> Items { get; set; } = new();

    [JsonPropertyName("meta")]
    public Meta Meta { get; set; } = new();
}

public sealed class IndexApiResponse
{
    [JsonPropertyName("items")]
    public List<IndexItem> Items { get; set; } = new();

    [JsonPropertyName("meta")]
    public Meta Meta { get; set; } = new();
}

public sealed class Meta
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("items")] public int Items { get; set; }
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
}

public sealed class SkewItem
{
    [JsonPropertyName("1")] public double? D1 { get; set; }
    [JsonPropertyName("7")] public double? D7 { get; set; }
    [JsonPropertyName("14")] public double? D14 { get; set; }
    [JsonPropertyName("30")] public double? D30 { get; set; }
    [JsonPropertyName("60")] public double? D60 { get; set; }
    [JsonPropertyName("90")] public double? D90 { get; set; }
    [JsonPropertyName("180")] public double? D180 { get; set; }
    [JsonPropertyName("365")] public double? D365 { get; set; }

    [JsonPropertyName("date")]
    public long DateUnixMs { get; set; }
}

public sealed class IndexItem
{
    [JsonPropertyName("date")] public long DateUnixMs { get; set; }
    [JsonPropertyName("index")] public double? IndexPrice { get; set; }
}
