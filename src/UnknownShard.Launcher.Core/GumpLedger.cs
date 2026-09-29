using System.Text.Json;
using System.Text.Json.Serialization;
using UnknownShard.Patching;

namespace UnknownShard.Launcher.Core;

public enum GumpAction { Create, UpdateOwned, AlreadyPresent, ConflictForeign, ConflictModified }

/// <summary>
/// &lt;UO&gt;\Gumps\.unknownshard-owned.json: files WE created + their sha256 (plan §3.3 item 4).
/// Minimal version for #192; uninstall/repair lands in unknown-shard#193.
/// Rule: never overwrite or delete a file that is not in the ledger, or whose bytes no longer match the ledger.
/// </summary>
public sealed class GumpLedger
{
    public const string FileName = ".unknownshard-owned.json";

    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("files")] public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public string GumpsDir { get; private set; } = "";

    public static GumpLedger Load(string gumpsDir)
    {
        var p = Path.Combine(gumpsDir, FileName);
        var l = File.Exists(p) ? JsonSerializer.Deserialize<GumpLedger>(File.ReadAllBytes(p)) ?? new() : new();
        l.Files = new SortedDictionary<string, string>(l.Files, StringComparer.OrdinalIgnoreCase);
        l.GumpsDir = gumpsDir;
        return l;
    }

    public void Save()
    {
        EnsureNotReparse(GumpsDir);
        AtomicFile.WriteAllBytes(Path.Combine(GumpsDir, FileName), JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public GumpAction Decide(string name, string wantSha)
    {
        var path = DestRules.Resolve(GumpsDir, name);
        if (!File.Exists(path)) return GumpAction.Create;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return GumpAction.ConflictForeign;
        var actual = Hashing.HashFile(path).Sha256;
        if (Files.TryGetValue(name, out var owned))
        {
            if (owned != actual) return GumpAction.ConflictModified;
            return actual == wantSha ? GumpAction.AlreadyPresent : GumpAction.UpdateOwned;
        }
        return actual == wantSha ? GumpAction.AlreadyPresent : GumpAction.ConflictForeign;
    }

    /// <summary>Install a verified staged file. Create never overwrites; UpdateOwned re-checks ownership first.</summary>
    public void Install(string name, string stagedPath, string sha, GumpAction action)
    {
        if (action is not (GumpAction.Create or GumpAction.UpdateOwned)) throw new InvalidOperationException($"not writable: {action}");
        Directory.CreateDirectory(GumpsDir);
        EnsureNotReparse(GumpsDir);
        var path = DestRules.Resolve(GumpsDir, name);
        var tmp = Path.Combine(GumpsDir, $".us-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(stagedPath, tmp, overwrite: false);
            if (action == GumpAction.Create)
                File.Move(tmp, path, overwrite: false); // throws if something appeared meanwhile
            else
            {
                if (Decide(name, sha) != GumpAction.UpdateOwned) throw new IOException($"{name}: ownership changed, not overwriting");
                File.Move(tmp, path, overwrite: true);
            }
            Files[name] = sha;
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static void EnsureNotReparse(string dir)
    {
        if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{dir} is a reparse point; refusing to write");
    }
}
