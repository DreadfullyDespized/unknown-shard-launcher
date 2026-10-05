using System.Text.RegularExpressions;

namespace ShardLauncher.Patching;

public sealed class ManifestValidationException(string message) : Exception(message);

public static class ManifestValidator
{
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;
    private static readonly Regex ShaRx = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex MapKeyRx = new("^[a-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    public static bool IsSha256(string? s) => s is not null && ShaRx.IsMatch(s);

    public static void Validate(PatchManifest m, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        if (m.Schema != 1) Fail($"unsupported schema {m.Schema}");
        if (string.IsNullOrWhiteSpace(m.Shard)) Fail("shard missing");
        if (m.Serial <= 0) Fail("serial must be > 0");
        if (string.IsNullOrWhiteSpace(m.Version)) Fail("version missing");
        if (!System.Version.TryParse(m.MinLauncher, out _)) Fail("min_launcher not a version");
        if (m.Files is null) Fail("files missing");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var dests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var f in m.Files!)
        {
            if (f is null) Fail("null file entry");
            if (!IdRx.IsMatch(f!.Id ?? "")) Fail($"bad id '{f.Id}'");
            if (!ids.Add(f.Id!)) Fail($"duplicate id '{f.Id}'");
            var t = DestRules.Parse(f.Dest);
            if (!dests.Add(f.Dest)) Fail($"duplicate dest '{f.Dest}'");
            var wantMode = t.Root == DestRoot.UoGumps ? DestRules.ModeAddOnly : DestRules.ModeShardOwned;
            if (f.Mode != wantMode) Fail($"'{f.Id}': mode must be {wantMode}");
            CheckObject(f.Id!, f.File, f.Sha256, f.Size);
            if (!f.Hold) total = checked(total + f.Size);
        }
        if (total > maxTotalBytes) Fail($"total size {total} exceeds cap {maxTotalBytes}");

        if (m.RequiresStock is not null)
            foreach (var r in m.RequiresStock)
            {
                if (!DestRules.IsSafeFileName(r.Name)) Fail($"requires_stock bad name '{r.Name}'");
                if (!IsSha256(r.Sha256)) Fail($"requires_stock '{r.Name}' bad sha256");
                foreach (var a in r.AppliesTo) if (!DestRules.IsSafeFileName(a)) Fail($"applies_to bad name '{a}'");
            }

        if (m.OverrideMap is not null)
            foreach (var (k, v) in m.OverrideMap)
            {
                if (!MapKeyRx.IsMatch(k)) Fail($"override_map bad key '{k}'");
                if (DestRules.Parse(v).Root != DestRoot.ShardArt) Fail($"override_map '{k}' must point at shard:art/");
                if (!dests.Contains(v)) Fail($"override_map '{k}' points at unlisted dest '{v}'");
            }

        if (m.Cuo is not null) CheckObject("cuo", m.Cuo.File, m.Cuo.Sha256, m.Cuo.Size);
    }

    private static void CheckObject(string id, string file, string sha, long size)
    {
        if (!IsSha256(sha)) Fail($"'{id}': sha256 must be 64 lowercase hex");
        if (file != "objects/" + sha) Fail($"'{id}': file must be objects/<sha256>");
        if (size < 0) Fail($"'{id}': negative size");
    }

    private static void Fail(string msg) => throw new ManifestValidationException(msg);
}

public static class SerialGuard
{
    public static void EnsureNewer(long previous, long next)
    {
        if (next <= previous) throw new ManifestValidationException($"serial {next} is not greater than previous {previous}");
    }

    public static void EnsureNotReplay(long highestSeen, long next)
    {
        if (next < highestSeen) throw new ManifestValidationException($"serial {next} is below highest seen {highestSeen} (replay/downgrade)");
    }
}
