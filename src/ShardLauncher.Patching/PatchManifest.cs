using System.Text.Json.Serialization;

namespace ShardLauncher.Patching;

/// <summary>Signed patch manifest, schema 1 (distribution-plan.md §3.2).</summary>
public sealed class PatchManifest
{
    [JsonPropertyName("schema"), JsonPropertyOrder(0)] public int Schema { get; set; } = 1;
    [JsonPropertyName("shard"), JsonPropertyOrder(1)] public string Shard { get; set; } = "";
    [JsonPropertyName("serial"), JsonPropertyOrder(2)] public long Serial { get; set; }
    [JsonPropertyName("version"), JsonPropertyOrder(3)] public string Version { get; set; } = "";
    [JsonPropertyName("published_utc"), JsonPropertyOrder(4)] public string PublishedUtc { get; set; } = "";
    [JsonPropertyName("git_commit"), JsonPropertyOrder(5)] public string GitCommit { get; set; } = "";
    [JsonPropertyName("min_launcher"), JsonPropertyOrder(6)] public string MinLauncher { get; set; } = "1.0.0";
    [JsonPropertyName("disabled"), JsonPropertyOrder(7)] public bool Disabled { get; set; }
    [JsonPropertyName("message"), JsonPropertyOrder(8)] public string Message { get; set; } = "";
    [JsonPropertyName("signing_key_id"), JsonPropertyOrder(9)] public string SigningKeyId { get; set; } = "";
    [JsonPropertyName("cuo"), JsonPropertyOrder(10)] public CuoEntry? Cuo { get; set; }
    [JsonPropertyName("requires_stock"), JsonPropertyOrder(11)] public List<StockRequirement>? RequiresStock { get; set; }
    [JsonPropertyName("files"), JsonPropertyOrder(12)] public List<ManifestFile> Files { get; set; } = new();
    [JsonPropertyName("override_map"), JsonPropertyOrder(13)] public SortedDictionary<string, string>? OverrideMap { get; set; }
}

public sealed class ManifestFile
{
    [JsonPropertyName("id"), JsonPropertyOrder(0)] public string Id { get; set; } = "";
    [JsonPropertyName("dest"), JsonPropertyOrder(1)] public string Dest { get; set; } = "";
    [JsonPropertyName("file"), JsonPropertyOrder(2)] public string File { get; set; } = "";
    [JsonPropertyName("sha256"), JsonPropertyOrder(3)] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size"), JsonPropertyOrder(4)] public long Size { get; set; }
    [JsonPropertyName("mode"), JsonPropertyOrder(5)] public string Mode { get; set; } = "";
    [JsonPropertyName("hold"), JsonPropertyOrder(6)] public bool Hold { get; set; }
}

public sealed class StockRequirement
{
    [JsonPropertyName("name"), JsonPropertyOrder(0)] public string Name { get; set; } = "";
    [JsonPropertyName("sha256"), JsonPropertyOrder(1)] public string Sha256 { get; set; } = "";
    [JsonPropertyName("applies_to"), JsonPropertyOrder(2)] public List<string> AppliesTo { get; set; } = new();
}

public sealed class CuoEntry
{
    [JsonPropertyName("file"), JsonPropertyOrder(0)] public string File { get; set; } = "";
    [JsonPropertyName("sha256"), JsonPropertyOrder(1)] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size"), JsonPropertyOrder(2)] public long Size { get; set; }
    [JsonPropertyName("version"), JsonPropertyOrder(3)] public string Version { get; set; } = "";
    [JsonPropertyName("authenticode_subject"), JsonPropertyOrder(4)] public string AuthenticodeSubject { get; set; } = "";
}
