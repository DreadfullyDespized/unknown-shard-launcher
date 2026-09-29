using System.Text;
using System.Text.Json;
using UnknownShard.Patching;

namespace UnknownShard.Launcher.Core;

public enum UpdateOutcome { UpToDate, Updated, Disabled, KeptLastGood }

public sealed record UpdateProgress(string Stage, long Done, long Total);

public sealed record UpdateResult(
    UpdateOutcome Outcome,
    string Message,
    long LaunchSerial,
    string? OverrideFile,
    int ObjectsDownloaded,
    IReadOnlyList<string> Conflicts,
    bool LauncherUpdateRequired);

/// <summary>fetch → verify signature → validate → diff → stage → verify → promote (plan §3, unknown-shard#192).</summary>
public sealed class Updater(InstallLayout layout, IPatchSource source, IReadOnlyDictionary<string, string> trustedKeys,
    Version launcherVersion, LauncherLog log)
{
    public const int MaxPointerBytes = 4 * 1024;
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxSigBytes = 4 * 1024;
    private const string VersionIndex = ".files.json";

    public async Task<UpdateResult> RunAsync(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        var state = LauncherState.Load(layout.StatePath);
        var uoPath = state.UoPath ?? throw new InvalidOperationException("UO data folder not configured");
        ResetStaging();
        var conflicts = new List<string>();
        int downloads = 0;

        // 1. fetch + verify
        progress?.Report(new("Checking for updates", 0, 0));
        PatchManifest m;
        try
        {
            var ptr = PatchPointer.Parse(await source.GetBytesAsync("current.json", MaxPointerBytes, ct).ConfigureAwait(false));
            var mb = await source.GetBytesAsync(ptr.Manifest, MaxManifestBytes, ct).ConfigureAwait(false);
            var sb = await source.GetBytesAsync(ptr.Sig, MaxSigBytes, ct).ConfigureAwait(false);
            m = ManifestSigner.VerifyAndParse(mb, ManifestSigner.DecodeSigFile(Encoding.UTF8.GetString(sb)), trustedKeys);
            ManifestValidator.Validate(m);
            if (m.Serial != ptr.Serial) throw new ManifestValidationException($"current.json serial {ptr.Serial} != manifest serial {m.Serial}");
            SerialGuard.EnsureNotReplay(state.HighestSerial, m.Serial);
        }
        catch (Exception e) when (IsUpdateFailure(e, ct))
        {
            return LastGood(state, "Update check failed: " + e.Message, conflicts, downloads, false);
        }
        if (m.Serial > state.HighestSerial) { state.HighestSerial = m.Serial; state.Save(layout.StatePath); }
        log.Info($"manifest serial {m.Serial} ({m.Version}) verified with {m.SigningKeyId}");

        if (m.Disabled)
            return new UpdateResult(UpdateOutcome.Disabled, "Updates paused by the shard: " + m.Message, state.CurrentSerial,
                OverrideFor(state.CurrentSerial), 0, conflicts, false);
        if (Version.Parse(m.MinLauncher) > launcherVersion)
            return LastGood(state, $"This launcher ({launcherVersion}) is too old; {m.MinLauncher} required.", conflicts, downloads, true);

        // 2. diff
        var skipNames = StockMismatches(m, uoPath);
        var wanted = m.Files.Where(f => !f.Hold && !skipNames.Contains(DestRules.Parse(f.Dest).FileName)).ToList();
        var art = wanted.Where(f => DestRules.Parse(f.Dest).Root == DestRoot.ShardArt).ToList();
        var gumpFiles = wanted.Where(f => DestRules.Parse(f.Dest).Root == DestRoot.UoGumps).ToList();

        var ledger = GumpLedger.Load(Path.Combine(uoPath, "Gumps"));
        var gumpPlan = new List<(ManifestFile F, string Name, GumpAction Action)>();
        foreach (var f in gumpFiles)
        {
            var name = DestRules.Parse(f.Dest).FileName;
            var a = ledger.Decide(name, f.Sha256);
            if (a is GumpAction.ConflictForeign or GumpAction.ConflictModified)
            {
                conflicts.Add($"Gumps\\{name}: {a}, left untouched");
                log.Warn($"conflict: Gumps\\{name} {a}; not overwriting");
            }
            gumpPlan.Add((f, name, a));
        }

        bool promoteArt = state.CurrentSerial != m.Serial || !Directory.Exists(layout.VersionDir(m.Serial));
        var prevIndex = ReadIndex(state.CurrentSerial);
        var need = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
        if (promoteArt)
            foreach (var f in art)
                if (LocalCopy(prevIndex, state.CurrentSerial, f) is null) need.TryAdd(f.Sha256, f);
        foreach (var g in gumpPlan)
            if (g.Action is GumpAction.Create or GumpAction.UpdateOwned) need.TryAdd(g.F.Sha256, g.F);

        // 3. stage + verify every object before touching anything live
        long total = need.Values.Sum(f => f.Size), done = 0;
        try
        {
            Directory.CreateDirectory(layout.Staging);
            foreach (var f in need.Values)
            {
                var staged = Path.Combine(layout.Staging, f.Sha256);
                progress?.Report(new($"Downloading {f.Id}", done, total));
                await source.DownloadToFileAsync(f.File, staged, f.Size,
                    n => { done += n; progress?.Report(new($"Downloading {f.Id}", done, total)); }, ct).ConfigureAwait(false);
                downloads++;
                var (sha, size) = Hashing.HashFile(staged);
                if (sha != f.Sha256 || size != f.Size)
                    throw new ManifestValidationException($"object for '{f.Id}' failed verification (sha/size mismatch)");
            }
        }
        catch (Exception e) when (IsUpdateFailure(e, ct))
        {
            ResetStaging();
            return LastGood(state, "Update rejected: " + e.Message, conflicts, downloads, false);
        }

        // 4. promote shard art: build the new version folder, then flip the pointer
        if (promoteArt)
        {
            var final = layout.VersionDir(m.Serial);
            var tmp = final + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                progress?.Report(new("Installing", total, total));
                Directory.CreateDirectory(tmp);
                var index = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var f in art)
                {
                    var name = DestRules.Parse(f.Dest).FileName;
                    var src = LocalCopy(prevIndex, state.CurrentSerial, f) ?? Path.Combine(layout.Staging, f.Sha256);
                    File.Copy(src, DestRules.Resolve(tmp, name), overwrite: false);
                    index[name] = f.Sha256;
                }
                File.WriteAllBytes(Path.Combine(tmp, VersionIndex), JsonSerializer.SerializeToUtf8Bytes(index));
                File.WriteAllText(Path.Combine(tmp, "uofiles-override.txt"), BuildOverride(m, index.Keys, final), new UTF8Encoding(false));
                if (Directory.Exists(final)) Directory.Delete(final, recursive: true); // stale partial from a crash; ours
                Directory.Move(tmp, final);
                state.CurrentSerial = m.Serial;
                state.Save(layout.StatePath); // atomic switch to the new set
                log.Info($"promoted art version {m.Serial}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryDelete(tmp);
                ResetStaging();
                return LastGood(state, "Install failed, previous version kept: " + e.Message, conflicts, downloads, false);
            }
        }

        // 5. Gumps: add-only / ledger-owned only
        bool gumpsChanged = false;
        foreach (var (f, name, action) in gumpPlan)
        {
            if (action is not (GumpAction.Create or GumpAction.UpdateOwned)) continue;
            try
            {
                ledger.Install(name, Path.Combine(layout.Staging, f.Sha256), f.Sha256, action);
                gumpsChanged = true;
                log.Info($"installed Gumps\\{name} ({action})");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                conflicts.Add($"Gumps\\{name}: {e.Message}");
                log.Warn($"Gumps\\{name} not installed: {e.Message}");
            }
        }
        if (gumpsChanged)
        {
            try { ledger.Save(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Warn("ledger save failed: " + e.Message); }
        }
        ResetStaging();

        var outcome = downloads > 0 || promoteArt || gumpsChanged ? UpdateOutcome.Updated : UpdateOutcome.UpToDate;
        var msg = outcome == UpdateOutcome.Updated ? $"Updated to {m.Version}" : $"Up to date ({m.Version})";
        log.Info($"{msg}; {downloads} download(s), {conflicts.Count} conflict(s)");
        return new UpdateResult(outcome, msg, state.CurrentSerial, OverrideFor(state.CurrentSerial), downloads, conflicts, false);
    }

    private static bool IsUpdateFailure(Exception e, CancellationToken ct) =>
        e is ManifestValidationException or PatchSourceException or HttpRequestException or IOException
            or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
        || (e is TaskCanceledException && !ct.IsCancellationRequested); // HttpClient timeout

    private UpdateResult LastGood(LauncherState s, string msg, List<string> conflicts, int downloads, bool needLauncher)
    {
        log.Warn(msg + (s.CurrentSerial > 0 ? $"; launching last-good {s.CurrentSerial}" : "; no local art yet"));
        return new UpdateResult(UpdateOutcome.KeptLastGood, msg, s.CurrentSerial, OverrideFor(s.CurrentSerial), downloads, conflicts, needLauncher);
    }

    private string? OverrideFor(long serial) =>
        serial > 0 && File.Exists(layout.OverrideFile(serial)) ? layout.OverrideFile(serial) : null;

    private HashSet<string> StockMismatches(PatchManifest m, string uoPath)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in m.RequiresStock ?? new())
        {
            var p = DestRules.Resolve(uoPath, r.Name);
            var ok = File.Exists(p) && Hashing.HashFile(p).Sha256 == r.Sha256; // read-only
            if (ok) continue;
            log.Warn($"stock {r.Name} does not match; skipping {string.Join(", ", r.AppliesTo)}");
            foreach (var a in r.AppliesTo) skip.Add(a);
        }
        return skip;
    }

    private Dictionary<string, string> ReadIndex(long serial)
    {
        var p = Path.Combine(layout.VersionDir(serial), VersionIndex);
        if (serial <= 0 || !File.Exists(p)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(p)) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Reuse an unchanged file from the current version instead of downloading it again.</summary>
    private string? LocalCopy(Dictionary<string, string> prevIndex, long prevSerial, ManifestFile f)
    {
        var name = DestRules.Parse(f.Dest).FileName;
        if (!prevIndex.TryGetValue(name, out var sha) || sha != f.Sha256) return null;
        var p = Path.Combine(layout.VersionDir(prevSerial), name);
        return File.Exists(p) && new FileInfo(p).Length == f.Size ? p : null;
    }

    /// <summary>UOFilesOverrideMap format: name=absolute path per line; '#' comments.</summary>
    private static string BuildOverride(PatchManifest m, IEnumerable<string> present, string finalDir)
    {
        var have = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder("# generated by UnknownShardLauncher; do not edit\n");
        foreach (var (k, v) in m.OverrideMap ?? new())
        {
            var name = DestRules.Parse(v).FileName;
            if (have.Contains(name)) sb.Append(k).Append('=').Append(Path.Combine(finalDir, name)).Append('\n');
        }
        return sb.ToString();
    }

    private void ResetStaging() => TryDelete(layout.Staging);

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
