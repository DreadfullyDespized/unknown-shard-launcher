using System.Diagnostics;

namespace UnknownShard.Launcher.Core;

/// <summary>Builds the §3.4 ClassicUO command line. Passwords never go on the command line.</summary>
public static class LaunchCommand
{
    public const string DefaultHost = "unknownshard.ddns.net";
    public const int DefaultPort = 2593;

    public static ProcessStartInfo Build(InstallLayout l, string uoPath, string? overrideFile, string host = DefaultHost, int port = DefaultPort)
    {
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
