namespace ShardLauncher.Core;

public sealed class RollbackManager(InstallLayout layout, LauncherLog log)
{
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromSeconds(60);
    private readonly VersionStore _store = new(layout, log);

    private LauncherState Load() => LauncherState.Load(layout.StatePath);
    private void Save(LauncherState s) => s.Save(layout.StatePath);

    public string? RecoverOnStartup()
    {
        var s = Load();
        if (s.CurrentSerial <= 0) return null;
        if (s.TrialSerial == s.CurrentSerial && s.TrialStatus == "crashed")
            return Revert(s, markBad: true, $"ClassicUO crashed within {ConfirmAfter.TotalSeconds:0} s on release {s.CurrentSerial}");
        var v = _store.Verify(s.CurrentSerial);
        if (!v.Ok) return Revert(s, markBad: false, "local files failed verification (" + v.Message + ")");
        return null;
    }

    public string UsePrevious()
    {
        var s = Load();
        return Revert(s, markBad: true, "switched to the previous version on request", allowBad: true)
               ?? "No previous version is available on this PC.";
    }

    public long? FallbackFor(LauncherState s, bool allowBad = false)
    {
        foreach (var cand in new[] { s.LastGoodSerial, s.PreviousSerial })
            if (cand > 0 && cand != s.CurrentSerial && (allowBad || cand != s.BadSerial) && _store.Verify(cand).Ok) return cand;
        return null;
    }

    private string? Revert(LauncherState s, bool markBad, string why, bool allowBad = false)
    {
        var target = FallbackFor(s, allowBad);
        if (target is null)
        {
            log.Warn($"{why}; no earlier verified version to revert to");
            if (markBad) { s.BadSerial = s.CurrentSerial; s.ClearTrial(); Save(s); }
            return null;
        }
        var from = s.CurrentSerial;
        if (markBad) s.BadSerial = from;
        s.PreviousSerial = from;
        s.CurrentSerial = target.Value;
        s.ClearTrial();
        Save(s);
        var conflicts = new List<string>();
        if (s.UoPath is not null) _store.ApplyGumps(target.Value, s.UoPath, repair: false, conflicts);
        var msg = $"Reverted from version {from} to {target.Value}: {why}.";
        log.Warn(msg + (conflicts.Count > 0 ? " Conflicts: " + string.Join("; ", conflicts) : ""));
        return msg;
    }

    public void OnLaunched(DateTimeOffset nowUtc)
    {
        var s = Load();
        if (s.TrialSerial != s.CurrentSerial || s.TrialSerial == 0) return;
        s.TrialStatus = "running";
        s.TrialStartedUtc = nowUtc;
        Save(s);
    }

    public void OnConfirmed()
    {
        var s = Load();
        if (s.CurrentSerial <= 0) return;
        if (s.LastGoodSerial != s.CurrentSerial) log.Info($"version {s.CurrentSerial} confirmed good");
        s.LastGoodSerial = s.CurrentSerial;
        if (s.TrialSerial == s.CurrentSerial) s.ClearTrial();
        Save(s);
        _store.Prune(s.CurrentSerial, s.PreviousSerial, s.LastGoodSerial);
    }

    public void OnExited(TimeSpan runtime, int exitCode)
    {
        if (runtime >= ConfirmAfter) { OnConfirmed(); return; }
        var s = Load();
        if (s.TrialSerial != s.CurrentSerial || s.TrialSerial == 0) return;
        s.TrialStatus = exitCode != 0 ? "crashed" : "pending";
        Save(s);
        if (exitCode != 0) log.Warn($"ClassicUO exited with code {exitCode} after {runtime.TotalSeconds:0} s on trial version {s.CurrentSerial}");
    }
}
