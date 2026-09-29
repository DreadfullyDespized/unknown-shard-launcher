using System.Globalization;

namespace UnknownShard.Launcher.Core;

public sealed class LauncherLog(string logDir)
{
    private readonly object _gate = new();
    public event Action<string>? Line;

    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);

    private void Write(string level, string msg)
    {
        var now = DateTime.Now;
        var line = $"{now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {level} {msg}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, $"launcher-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log"), line + Environment.NewLine);
            }
            catch (IOException) { /* logging must never break the launcher */ }
            catch (UnauthorizedAccessException) { }
        }
        Line?.Invoke(line);
    }
}
