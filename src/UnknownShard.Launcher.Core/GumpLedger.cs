using System.Text.Json;
using System.Text.Json.Serialization;
using UnknownShard.Patching;

namespace UnknownShard.Launcher.Core;

public enum GumpAction { Create, UpdateOwned, AlreadyPresent, ConflictForeign, ConflictModified }

public sealed record UninstallReport(IReadOnlyList<string> Removed, IReadOnlyList<string> Kept);

/// <summary>
/// &lt;UO&gt;\Gumps\.unknownshard-owned.json: files WE created + their sha256 (plan §3.3 item 4, unknown-shard#193).
/// Rules:
/// - create a gump only if absent, or if ledger-owned and the bytes still match the ledger;
/// - never overwrite or delete a foreign file or a user-edited owned file (conflict: log + skip);
/// - only names matching ^\d{1,5}\.gump$ (0..65535), including names read back from the ledger file;
/// - refuse reparse points / junctions / symlinks (Gumps dir or file);
/// - write nothing else in the UO dir.
/// </summary>
public sealed class GumpLedger
{
    public const string FileName = ".unknownshard-owned.json";

    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    /// <summary>True if the Gumps folder did not exist and we created it (uninstall removes it again if empty).</summary>
    [JsonPropertyName("created_dir")] public bool CreatedDir { get; set; }
    [JsonPropertyName("files")] public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore] public string GumpsDir { get; private set; } = "";
    [JsonIgnore] public string? LoadWarning { get; private set; }

    public static bool IsGumpName(string name)
    {
        try { return DestRules.Parse(DestRules.UoGumpsPrefix + name).Root == DestRoot.UoGumps; }
        catch (ManifestValidationException) { return false; }
    }

    public static GumpLedger Load(string gumpsDir)
    {
        var p = Path.Combine(gumpsDir, FileName);
        GumpLedger l;
        string? warn = null;
        try
        {
            l = File.Exists(p) && !IsReparse(p) ? JsonSerializer.Deserialize<GumpLedger>(File.ReadAllBytes(p)) ?? new() : new();
        }
        catch (JsonException)
        {
            // Unreadable ledger: own nothing. Safe direction: we can no longer overwrite or delete anything.
            l = new();
            warn = "ledger unreadable; treating all gumps as foreign";
        }
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in l.Files)
        {
            if (IsGumpName(k) && ManifestValidator.IsSha256(v)) files[k] = v;
            else warn = $"ignored invalid ledger entry '{k}'";
        }
        l.Files = files;
        l.GumpsDir = gumpsDir;
        l.LoadWarning = warn;
        return l;
    }

    public void Save()
    {
        EnsureDirUsable();
        AtomicFile.WriteAllBytes(Path.Combine(GumpsDir, FileName), JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public GumpAction Decide(string name, string wantSha)
    {
        var path = PathFor(name);
        if (Directory.Exists(GumpsDir) && IsReparse(GumpsDir)) return GumpAction.ConflictForeign;
        if (!File.Exists(path) && !IsReparse(path)) return GumpAction.Create;
        if (IsReparse(path) || Directory.Exists(path)) return GumpAction.ConflictForeign;
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
        var path = PathFor(name);
        if (!Directory.Exists(GumpsDir))
        {
            if (Files.Count == 0) CreatedDir = true;
            Directory.CreateDirectory(GumpsDir);
        }
        EnsureDirUsable();
        var tmp = Path.Combine(GumpsDir, $".us-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(stagedPath, tmp, overwrite: false);
            if (Hashing.HashFile(tmp).Sha256 != sha) throw new IOException($"{name}: staged copy does not match {sha}");
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

    /// <summary>Delete an owned gump if its bytes still match the ledger. Returns false (file kept) otherwise.</summary>
    public bool TryRemoveOwned(string name)
    {
        if (!Files.TryGetValue(name, out var owned)) return false;
        var path = PathFor(name);
        if (Directory.Exists(GumpsDir) && IsReparse(GumpsDir)) return false;
        if (!File.Exists(path)) { Files.Remove(name); return true; }
        if (IsReparse(path) || Hashing.HashFile(path).Sha256 != owned)
        {
            Files.Remove(name); // user changed it: it is theirs now; never delete it
            return false;
        }
        File.Delete(path);
        Files.Remove(name);
        return true;
    }

    /// <summary>Remove owned gumps the current release no longer ships (hash-guarded).</summary>
    public IReadOnlyList<string> Retire(IEnumerable<string> keep)
    {
        var k = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        var retired = new List<string>();
        foreach (var name in Files.Keys.Where(n => !k.Contains(n)).ToList())
            if (TryRemoveOwned(name)) retired.Add(name);
        return retired;
    }

    /// <summary>Remove every owned gump whose hash still matches, then the ledger, then the Gumps dir if we created it and it is empty.</summary>
    public UninstallReport Uninstall()
    {
        var removed = new List<string>();
        var kept = new List<string>();
        foreach (var name in Files.Keys.ToList())
            (TryRemoveOwned(name) ? removed : kept).Add(name);
        var ledgerPath = Path.Combine(GumpsDir, FileName);
        if (Directory.Exists(GumpsDir) && !IsReparse(GumpsDir))
        {
            if (File.Exists(ledgerPath) && !IsReparse(ledgerPath)) File.Delete(ledgerPath);
            if (CreatedDir && !Directory.EnumerateFileSystemEntries(GumpsDir).Any()) Directory.Delete(GumpsDir);
        }
        return new UninstallReport(removed, kept);
    }

    private string PathFor(string name)
    {
        if (!IsGumpName(name)) throw new ManifestValidationException($"'{name}' is not a gump file name");
        return DestRules.Resolve(GumpsDir, name);
    }

    private void EnsureDirUsable()
    {
        if (Directory.Exists(GumpsDir) && IsReparse(GumpsDir))
            throw new IOException($"{GumpsDir} is a reparse point/junction; refusing to write");
    }

    private static bool IsReparse(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return (fi.Exists || Directory.Exists(path) || fi.LinkTarget is not null) && fi.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return true; }
    }
}
