using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnknownShard.Launcher.Core;

/// <summary>launcher.json: UO path + replay guard + last-good pointer.</summary>
public sealed class LauncherState
{
    [JsonPropertyName("uo_path")] public string? UoPath { get; set; }
    /// <summary>Highest verified manifest serial ever seen (replay/downgrade guard).</summary>
    [JsonPropertyName("highest_serial")] public long HighestSerial { get; set; }
    /// <summary>Serial of the promoted (last-good) art version; 0 = none.</summary>
    [JsonPropertyName("current_serial")] public long CurrentSerial { get; set; }

    public static LauncherState Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<LauncherState>(File.ReadAllBytes(path)) ?? new() : new();

    public void Save(string path) => AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
}

public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
