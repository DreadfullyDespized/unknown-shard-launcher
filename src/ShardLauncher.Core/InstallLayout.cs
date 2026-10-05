namespace ShardLauncher.Core;

public sealed class InstallLayout(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string StatePath => Path.Combine(Root, "launcher.json");
    public string Staging => Path.Combine(Root, "staging");
    public string ArtVersions => Path.Combine(Root, "art", "versions");
    public string VersionDir(long serial) => Path.Combine(ArtVersions, serial.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public string OverrideFile(long serial) => Path.Combine(VersionDir(serial), "uofiles-override.txt");
    public string Logs => Path.Combine(Root, "logs");
    public string Profiles => Path.Combine(Root, "profiles");
    public string CuoDir => Path.Combine(Root, "cuo");
    public string CuoExe => Path.Combine(CuoDir, "ClassicUO.exe");
    public string CuoSettings => Path.Combine(Root, "cuo-settings.json");
    public string ServerOverride => Path.Combine(Root, LauncherConfig.ServerOverrideFileName);

    public static InstallLayout Default(string dataDirName) =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dataDirName));
}
