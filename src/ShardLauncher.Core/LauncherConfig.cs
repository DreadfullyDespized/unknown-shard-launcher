using System.Text.Json;
using ShardLauncher.Patching;

namespace ShardLauncher.Core;

public sealed class LauncherConfigException(string message) : Exception(message);

/// <summary>
/// Server-specific settings. Everything here is injected at BUILD time from a gitignored
/// <c>launcher.build.props</c> (see <c>launcher.build.example.props</c> and the README);
/// nothing server-specific is committed. Only the game server host/port may be overridden
/// at runtime (<see cref="WithServerOverride"/>); the patch URL and trusted keys never can.
/// </summary>
public sealed record LauncherConfig
{
    public const string DefaultDisplayName = "Shard Launcher";
    public const string DefaultDataDirName = "ShardLauncher";
    public const string ServerOverrideFileName = "server.json";

    public string DisplayName { get; init; } = DefaultDisplayName;
    public string DataDirName { get; init; } = DefaultDataDirName;
    public string ServerHost { get; init; } = "";
    public int ServerPort { get; init; }
    public Uri? PatchBase { get; init; }
    public IReadOnlyDictionary<string, string> TrustedKeys { get; init; } = new Dictionary<string, string>();
    /// <summary>Path of the runtime override that replaced host/port, if any.</summary>
    public string? ServerOverrideSource { get; init; }

    /// <summary>The values compiled into this build (generated <c>BuildSettings</c> + embedded keys).</summary>
    public static LauncherConfig FromBuild() => Create(BuildSettings.DisplayName, BuildSettings.DataDirName, BuildSettings.ServerHost,
        BuildSettings.ServerPort, BuildSettings.PatchBaseUrl, Core.TrustedKeys.LoadEmbedded());

    /// <summary>Lenient parse: bad values become empty and are reported by <see cref="Problems"/>.</summary>
    public static LauncherConfig Create(string? displayName, string? dataDirName, string? host, string? port, string? patchBaseUrl,
        IReadOnlyDictionary<string, string> trustedKeys) => new()
    {
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName.Trim(),
        DataDirName = IsSafeDirName(dataDirName) ? dataDirName!.Trim() : DefaultDataDirName,
        ServerHost = host?.Trim() ?? "",
        ServerPort = int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0,
        PatchBase = Uri.TryCreate(patchBaseUrl?.Trim(), UriKind.Absolute, out var u) ? u : null,
        TrustedKeys = trustedKeys,
    };

    /// <summary>Everything that prevents this config from being used. Empty = usable.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            var p = new List<string>();
            if (!IsValidHost(ServerHost)) p.Add("no valid game server host (LauncherServerHost)");
            if (ServerPort is < 1 or > 65535) p.Add("no valid game server port (LauncherServerPort)");
            if (PatchBase is null) p.Add("no patch base URL (LauncherPatchBaseUrl)");
            else if (PatchBase.Scheme != Uri.UriSchemeHttps || !PatchBase.AbsolutePath.EndsWith('/') || !string.IsNullOrEmpty(PatchBase.Query))
                p.Add("patch base URL must be https:// and end with '/' (LauncherPatchBaseUrl)");
            if (TrustedKeys.Count == 0) p.Add("no trusted manifest-signing public key (LauncherTrustedKey)");
            foreach (var (id, pem) in TrustedKeys)
            {
                if (pem.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase)) { p.Add($"key '{id}' is a PRIVATE key; embed only the public key"); continue; }
                try { _ = ManifestSigner.Fingerprint(pem); }
                catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
                { p.Add($"key '{id}' is not an ECDSA P-256 public key PEM"); }
            }
            return p;
        }
    }

    public bool IsComplete => Problems.Count == 0;

    public void EnsureComplete()
    {
        var p = Problems;
        if (p.Count > 0)
            throw new LauncherConfigException("This launcher build is not configured for a server:\n- " + string.Join("\n- ", p) +
                "\nBuild it with a launcher.build.props (see README, \"Configuring a build\").");
    }

    /// <summary>
    /// Optional runtime override of the game server address ONLY: <c>{"host": "...", "port": 1234}</c>.
    /// Missing file = unchanged. Any other property, or an invalid value, is a clear error (never silently ignored).
    /// </summary>
    public LauncherConfig WithServerOverride(string path)
    {
        if (!File.Exists(path)) return this;
        string? host = null;
        int? port = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new LauncherConfigException($"{path}: must be a JSON object");
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "host" when prop.Value.ValueKind == JsonValueKind.String: host = prop.Value.GetString()!.Trim(); break;
                    case "port" when prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var p): port = p; break;
                    case "host" or "port": throw new LauncherConfigException($"{path}: '{prop.Name}' has the wrong type");
                    default: throw new LauncherConfigException($"{path}: unknown setting '{prop.Name}' (only \"host\" and \"port\" can be overridden)");
                }
            }
        }
        catch (JsonException e) { throw new LauncherConfigException($"{path}: invalid JSON: {e.Message}"); }
        if (host is not null && !IsValidHost(host)) throw new LauncherConfigException($"{path}: invalid host '{host}'");
        if (port is < 1 or > 65535) throw new LauncherConfigException($"{path}: port must be 1-65535");
        if (host is null && port is null) return this;
        return this with { ServerHost = host ?? ServerHost, ServerPort = port ?? ServerPort, ServerOverrideSource = path };
    }

    private static bool IsValidHost(string? h) => !string.IsNullOrWhiteSpace(h) && Uri.CheckHostName(h) != UriHostNameType.Unknown;

    private static bool IsSafeDirName(string? n) =>
        !string.IsNullOrWhiteSpace(n) && n.Trim() is var t && t != "." && t != ".." &&
        t.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && t.IndexOfAny(['/', '\\', ':']) < 0;
}
