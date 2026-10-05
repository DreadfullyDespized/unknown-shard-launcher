using System.Diagnostics;

namespace ShardLauncher.Core;

public static class LaunchCommand
{
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
