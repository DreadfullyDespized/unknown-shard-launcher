using System.Diagnostics;

namespace ShardLauncher.Core;

/// <summary>Builds the §3.4 ClassicUO command line. Passwords never go on the command line.</summary>
public static class LaunchCommand
{
    /// <summary>Host/port come from <see cref="LauncherConfig"/>; there is deliberately no default server.</summary>
    public static ProcessStartInfo Build(InstallLayout l, string uoPath, string? overrideFile, string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new LauncherConfigException("no valid game server host configured");
        if (port is < 1 or > 65535) throw new LauncherConfigException("no valid game server port configured");
        var psi = new ProcessStartInfo(l.CuoExe) { UseShellExecute = false, WorkingDirectory = l.CuoDir };
        void Add(string k, string v) { psi.ArgumentList.Add(k); psi.ArgumentList.Add(v); }
        Add("-settings", l.CuoSettings);
        Add("-uopath", Path.GetFullPath(uoPath));
        if (overrideFile is not null) Add("-uofilesoverride", overrideFile);
        Add("-profilespath", l.Profiles);
        Add("-ip", host);
        Add("-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return psi;
    }
}
