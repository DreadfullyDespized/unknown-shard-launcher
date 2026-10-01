using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShardLauncher.Patching;

/// <summary>
/// Deterministic (canonical) manifest bytes. Compact JSON (no platform newlines),
/// fixed property order, files sorted by id, maps sorted ordinally. The signature
/// is over these exact bytes; verifiers never re-serialize before verifying.
/// </summary>
public static class ManifestJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static byte[] Serialize(PatchManifest m)
    {
        m.Files = m.Files.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        if (m.RequiresStock is not null)
        {
            m.RequiresStock = m.RequiresStock.OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
            foreach (var r in m.RequiresStock) r.AppliesTo = r.AppliesTo.OrderBy(a => a, StringComparer.Ordinal).ToList();
        }
        if (m.OverrideMap is not null)
            m.OverrideMap = new SortedDictionary<string, string>(m.OverrideMap, StringComparer.Ordinal);
        return JsonSerializer.SerializeToUtf8Bytes(m, WriteOptions);
    }

    public static PatchManifest Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonSerializer.Deserialize<PatchManifest>(utf8, ReadOptions)
                   ?? throw new ManifestValidationException("manifest is null");
        }
        catch (JsonException e)
        {
            throw new ManifestValidationException("manifest JSON invalid: " + e.Message);
        }
    }
}
