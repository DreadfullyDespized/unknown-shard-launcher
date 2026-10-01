using System.Reflection;

namespace ShardLauncher.Core;

/// <summary>
/// Manifest-signing public keys compiled into the launcher at BUILD time
/// (MSBuild items <c>LauncherTrustedKey</c>, see <c>launcher.build.example.props</c>).
/// The keys are never read from a runtime file: a user-editable key file would let
/// anyone swap the key and defeat signature checks. Rotation = add the next key item
/// to the build config, ship a launcher, then sign with the new key.
/// </summary>
public static class TrustedKeys
{
    public const string ResourcePrefix = "launcher.keys.";
    public const string ResourceSuffix = ".pem";

    /// <summary>All embedded keys by key id. Empty if the build supplied none.</summary>
    public static IReadOnlyDictionary<string, string> LoadEmbedded(Assembly? assembly = null)
    {
        var asm = assembly ?? typeof(TrustedKeys).Assembly;
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(ResourceSuffix, StringComparison.Ordinal)) continue;
            var id = name[ResourcePrefix.Length..^ResourceSuffix.Length];
            using var s = asm.GetManifestResourceStream(name)!;
            d[id] = new StreamReader(s).ReadToEnd();
        }
        return d;
    }
}
