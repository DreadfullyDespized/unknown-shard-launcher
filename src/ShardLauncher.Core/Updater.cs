using System.Text;
using System.Text.Json;
using ShardLauncher.Patching;

namespace ShardLauncher.Core;

public enum UpdateOutcome { UpToDate, Updated, Disabled, KeptLastGood }

public sealed record UpdateProgress(string Stage, long Done, long Total);

public sealed record UpdateResult(
    UpdateOutcome Outcome,
    string Message,
    long LaunchSerial,
    string? OverrideFile,
    int ObjectsDownloaded,
    IReadOnlyList<string> Conflicts,
    bool LauncherUpdateRequired)
{
    /// <summary>Result of the full ClassicUO verify (hash index + Authenticode). Play requires Ok.</summary>
    public CuoCheck Cuo { get; init; } = new(false, "not checked");
}

/// <summary>fetch → verify signature → validate → diff → stage → verify → promote (plan §3).</summary>
public sealed class Updater(InstallLayout layout, IPatchSource source, IReadOnlyDictionary<string, string> trustedKeys,
    Version launcherVersion, LauncherLog log, IAuthenticodeVerifier? verifier = null)
{
    private readonly CuoInstaller _cuo = new(layout, verifier ?? Authenticode.ForPlatform(), log);

    public const int MaxPointerBytes = 4 * 1024;
    public const int MaxManifestBytes = 1024 * 1024;
    public const int MaxSigBytes = 4 * 1024;

    private readonly VersionStore _store = new(layout, log);

    /// <param name="repair">Repair button: rebuild the active set from hash-verified local copies + re-downloads,
    /// restore user-edited owned gumps, reinstall ClassicUO if it fails verification.</param>
    public async Task<UpdateResult> RunAsync(IProgress<UpdateProgress>? progress = null, CancellationToken ct = default, bool repair = false)
    {
        var r = await RunCoreAsync(progress, ct, repair).ConfigureAwait(false);
        var cuo = _cuo.VerifyInstalled();
        if (!cuo.Ok) log.Warn(cuo.Message);
        return r with { Cuo = cuo };
    }

    private async Task<UpdateResult> RunCoreAsync(IProgress<UpdateProgress>? progress, CancellationToken ct, bool repair)
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
        if (state.BadSerial != 0 && m.Serial == state.BadSerial)
            return LastGood(state, $"Release {m.Version} was rolled back on this PC; waiting for a newer release.", conflicts, downloads, false);
        if (state.BadSerial != 0 && m.Serial > state.BadSerial) state.BadSerial = 0;

        // 2. diff: the expected version index (art at root, gumps under gumps/)
        var skipNames = StockMismatches(m, uoPath);
        var gumpsDir = Path.Combine(uoPath, "Gumps");
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var entries = new List<(string Rel, ManifestFile F, string? Extra)>();
        foreach (var f in m.Files.Where(f => !f.Hold))
        {
            var t = DestRules.Parse(f.Dest);
            if (skipNames.Contains(t.FileName)) continue;
            var rel = t.Root == DestRoot.ShardArt ? t.FileName : VersionStore.GumpsSub + "/" + t.FileName;
            expected[rel] = f.Sha256;
            // An identical gump already in <UO>\Gumps (ours or not) can seed the version folder without a download.
            entries.Add((rel, f, t.Root == DestRoot.UoGumps ? Path.Combine(gumpsDir, t.FileName) : null));
        }

        bool promote = repair || state.CurrentSerial != m.Serial || !_store.IndexEquals(m.Serial, expected);
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var need = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
        if (promote)
            foreach (var (rel, f, extra) in entries)
            {
                if (local.ContainsKey(f.Sha256) || need.ContainsKey(f.Sha256)) continue;
                var hit = _store.FindLocal(f.Sha256, f.Size, extra is null || IsLink(extra) ? null : new[] { extra });
                if (hit is not null) local[f.Sha256] = hit; else need[f.Sha256] = f;
            }
        var cuoWant = m.Cuo;
        bool installCuo = cuoWant is not null && (!_cuo.IsCurrent(cuoWant) || (repair && !_cuo.VerifyInstalled().Ok));
        if (installCuo)
            need.TryAdd(cuoWant!.Sha256, new ManifestFile { Id = "classicuo", File = cuoWant.File, Sha256 = cuoWant.Sha256, Size = cuoWant.Size });

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
                local[f.Sha256] = staged;
            }
        }
        catch (Exception e) when (IsUpdateFailure(e, ct))
        {
            ResetStaging();
            return LastGood(state, "Update rejected: " + e.Message, conflicts, downloads, false);
        }

        // 4. promote: build the complete new set in a temp folder, swap it in, then flip the pointer
        if (promote)
        {
            var final = layout.VersionDir(m.Serial);
            var tmp = final + ".tmp-" + Guid.NewGuid().ToString("N");
            var old = final + ".old-" + Guid.NewGuid().ToString("N");
            try
            {
                progress?.Report(new("Installing", total, total));
                Directory.CreateDirectory(Path.Combine(tmp, VersionStore.GumpsSub));
                foreach (var (rel, f, _) in entries)
                    File.Copy(local[f.Sha256], CuoInstaller.SafeJoin(tmp, rel), overwrite: false);
                File.WriteAllBytes(Path.Combine(tmp, VersionStore.IndexName), JsonSerializer.SerializeToUtf8Bytes(expected));
                File.WriteAllText(Path.Combine(tmp, "uofiles-override.txt"),
                    VersionStore.BuildOverride(m, expected.Keys.Where(k => !k.Contains('/')), final), new UTF8Encoding(false));
                if (Directory.Exists(final)) Directory.Move(final, old); // same serial (repair / stale partial)
                try { Directory.Move(tmp, final); }
                catch { if (Directory.Exists(old) && !Directory.Exists(final)) Directory.Move(old, final); throw; }
                TryDelete(old);
                if (state.CurrentSerial != m.Serial)
                {
                    if (state.CurrentSerial > 0) state.PreviousSerial = state.CurrentSerial;
                    state.TrialSerial = m.Serial;
                    state.TrialStatus = "pending";
                    state.TrialStartedUtc = null;
                }
                state.CurrentSerial = m.Serial;
                state.Save(layout.StatePath); // atomic switch to the new set
                _store.Prune(state.CurrentSerial, state.PreviousSerial, state.LastGoodSerial);
                log.Info($"promoted version {m.Serial}{(repair ? " (repair)" : "")}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryDelete(tmp);
                ResetStaging();
                return LastGood(state, "Install failed, previous version kept: " + e.Message, conflicts, downloads, false);
            }
        }
        else state.Save(layout.StatePath);

        // 4b. pinned ClassicUO: a refused client never blocks art/gumps; the old client is kept.
        bool cuoChanged = false;
        if (installCuo)
        {
            progress?.Report(new("Installing ClassicUO", total, total));
            var c = _cuo.Install(local[cuoWant!.Sha256], cuoWant);
            cuoChanged = c.Ok;
            if (!c.Ok) conflicts.Add(c.Message);
        }

        // 5. Gumps: make <UO>\Gumps match the active version under the ledger rules
        bool gumpsChanged = _store.ApplyGumps(state.CurrentSerial, uoPath, repair, conflicts);
        ResetStaging();

        var outcome = downloads > 0 || promote || gumpsChanged || cuoChanged ? UpdateOutcome.Updated : UpdateOutcome.UpToDate;
        var msg = repair ? $"Repair finished ({m.Version}, {downloads} file(s) re-fetched)"
            : outcome == UpdateOutcome.Updated ? $"Updated to {m.Version}" : $"Up to date ({m.Version})";
        log.Info($"{msg}; {downloads} download(s), {conflicts.Count} conflict(s)");
        return new UpdateResult(outcome, msg, state.CurrentSerial, OverrideFor(state.CurrentSerial), downloads, conflicts, false);
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

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




    private void ResetStaging() => TryDelete(layout.Staging);

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
