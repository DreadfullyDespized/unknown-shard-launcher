using System.Reflection;

namespace UnknownShard.Launcher.Core;

/// <summary>Public keys compiled into the launcher. Add the next key (us-2026b) here for rotation.</summary>
public static class TrustedKeys
{
    public static IReadOnlyDictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in new[] { "us-2026a" })
        {
            using var s = typeof(TrustedKeys).Assembly.GetManifestResourceStream($"keys.{id}.pem")
                          ?? throw new InvalidOperationException($"embedded key {id} missing");
            d[id] = new StreamReader(s).ReadToEnd();
        }
        return d;
    }
}
