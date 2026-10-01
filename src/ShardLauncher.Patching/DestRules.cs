using System.Globalization;
using System.Text.RegularExpressions;

namespace ShardLauncher.Patching;

public enum DestRoot { UoGumps, ShardArt }

public readonly record struct DestTarget(DestRoot Root, string FileName);

/// <summary>Path allowlist (distribution-plan.md §3.3 item 3). Shared by ManifestTool and the launcher.</summary>
public static class DestRules
{
    public const string UoGumpsPrefix = "uo:Gumps/";
    public const string ShardArtPrefix = "shard:art/";
    public const string ModeAddOnly = "add-only";
    public const string ModeShardOwned = "shard-owned";

    private static readonly Regex NameRx = new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex GumpRx = new(@"^\d{1,5}\.gump$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static DestTarget Parse(string? dest)
    {
        if (string.IsNullOrEmpty(dest)) throw Bad(dest, "empty");
        DestRoot root;
        string name;
        if (dest.StartsWith(UoGumpsPrefix, StringComparison.Ordinal)) { root = DestRoot.UoGumps; name = dest[UoGumpsPrefix.Length..]; }
        else if (dest.StartsWith(ShardArtPrefix, StringComparison.Ordinal)) { root = DestRoot.ShardArt; name = dest[ShardArtPrefix.Length..]; }
        else throw Bad(dest, "target outside allowlist (uo:Gumps/ or shard:art/)");

        CheckName(dest, name);
        if (root == DestRoot.UoGumps)
        {
            if (!GumpRx.IsMatch(name)) throw Bad(dest, "gump name must be <0..65535>.gump");
            var id = int.Parse(name[..^5], NumberStyles.None, CultureInfo.InvariantCulture);
            if (id > 65535) throw Bad(dest, "gump id > 65535");
        }
        return new DestTarget(root, name);
    }

    public static bool IsSafeFileName(string name)
    {
        try { CheckName("", name); return true; } catch (ManifestValidationException) { return false; }
    }

    private static void CheckName(string dest, string name)
    {
        // Covers '..', '/', '\', rooted paths, drive letters and NTFS ADS (':').
        if (name is "." or ".." || name.Contains("..", StringComparison.Ordinal)) throw Bad(dest, "path traversal");
        if (!NameRx.IsMatch(name)) throw Bad(dest, "name must match ^[A-Za-z0-9._-]+$ (no separators, no ':')");
        if (name.EndsWith('.')) throw Bad(dest, "trailing dot");
        var stem = name.Split('.')[0];
        if (Reserved.Contains(stem)) throw Bad(dest, "reserved device name");
    }

    /// <summary>Resolve to an absolute path and prove it stays directly under <paramref name="rootDir"/>.</summary>
    public static string Resolve(string rootDir, string fileName)
    {
        if (!IsSafeFileName(fileName)) throw Bad(fileName, "unsafe file name");
        var root = Path.GetFullPath(rootDir);
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw Bad(fileName, "escapes root");
        return full;
    }

    private static ManifestValidationException Bad(string? dest, string why) => new($"dest '{dest}' rejected: {why}");
}
