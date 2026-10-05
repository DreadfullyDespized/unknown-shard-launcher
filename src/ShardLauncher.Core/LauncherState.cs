using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShardLauncher.Core;

public sealed class LauncherState
{
    [JsonPropertyName("uo_path")] public string? UoPath { get; set; }
    [JsonPropertyName("highest_serial")] public long HighestSerial { get; set; }
    [JsonPropertyName("current_serial")] public long CurrentSerial { get; set; }
    [JsonPropertyName("last_good_serial")] public long LastGoodSerial { get; set; }
    [JsonPropertyName("previous_serial")] public long PreviousSerial { get; set; }
    [JsonPropertyName("bad_serial")] public long BadSerial { get; set; }
    [JsonPropertyName("trial_serial")] public long TrialSerial { get; set; }
    [JsonPropertyName("trial_status")] public string? TrialStatus { get; set; }
    [JsonPropertyName("trial_started_utc")] public DateTimeOffset? TrialStartedUtc { get; set; }

    public void ClearTrial() { TrialSerial = 0; TrialStatus = null; TrialStartedUtc = null; }

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
