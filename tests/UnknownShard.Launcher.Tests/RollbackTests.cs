using System.Text;
using UnknownShard.Launcher.Core;
using UnknownShard.Patching;
using Xunit;

namespace UnknownShard.Launcher.Tests;

/// <summary>unknown-shard#195: rollback, last-good, Repair, Use previous version.</summary>
public class RollbackTests
{
    private static readonly byte[] Anim2 = Encoding.ASCII.GetBytes("anim-v2-bytes");
    private static readonly byte[] Gump2 = Encoding.ASCII.GetBytes("gump-3510-v2-bytes");

    private static PatchManifest V2(Harness h, long serial = 2)
    {
        if (!h.Extra.Contains(Anim2)) { h.Extra.Add(Anim2); h.Extra.Add(Gump2); }
        var m = h.Manifest(serial, Gump2);
        m.Files.RemoveAll(f => f.Id == "anim-849-mul");
        m.Files.Add(Harness.Entry("anim-849-mul", "shard:art/anim.mul", Anim2));
        return m;
    }

    private static async Task<Harness> V1ConfirmedThenV2(Harness h)
    {
        h.Publish(h.Manifest(1));
        await h.Run();
        h.Rollback.OnLaunched(DateTimeOffset.UtcNow);
        h.Rollback.OnExited(TimeSpan.FromSeconds(90), 0); // > 60 s → confirmed
        h.Publish(V2(h));
        await h.Run();
        return h;
    }

    private static byte[] UoGump(Harness h) => File.ReadAllBytes(Path.Combine(h.GumpsDir, "3510.gump"));

    [Fact]
    public async Task Last_good_is_set_only_after_60_seconds()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        Assert.Equal(0, h.State.LastGoodSerial);
        Assert.Equal("pending", h.State.TrialStatus);
        h.Rollback.OnLaunched(DateTimeOffset.UtcNow);
        Assert.Equal("running", h.State.TrialStatus);
        h.Rollback.OnExited(TimeSpan.FromSeconds(20), 0); // clean early quit: neither confirmed nor crashed
        Assert.Equal(0, h.State.LastGoodSerial);
        Assert.Equal("pending", h.State.TrialStatus);
        h.Rollback.OnConfirmed();
        Assert.Equal(1, h.State.LastGoodSerial);
        Assert.Null(h.State.TrialStatus);
    }

    [Fact]
    public async Task Crash_within_60s_reverts_to_last_good_on_next_start_and_bad_release_is_not_repromoted()
    {
        using var h = await V1ConfirmedThenV2(new Harness());
        Assert.Equal(2, h.State.CurrentSerial);
        Assert.Equal(Gump2, UoGump(h));
        h.Rollback.OnLaunched(DateTimeOffset.UtcNow);
        h.Rollback.OnExited(TimeSpan.FromSeconds(5), -1073741819); // access violation
        Assert.Equal("crashed", h.State.TrialStatus);

        // "next start"
        var msg = h.Rollback.RecoverOnStartup();
        Assert.Contains("Reverted from version 2 to 1", msg);
        Assert.Equal(1, h.State.CurrentSerial);
        Assert.Equal(2, h.State.BadSerial);
        Assert.Equal(Harness.Gump3510, UoGump(h)); // gumps follow the active version
        Assert.Equal(Harness.Sha(Harness.Gump3510), GumpLedger.Load(h.GumpsDir).Files["3510.gump"]); // ledger consistent

        var r = await h.Run(); // server still offers serial 2
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Contains("rolled back", r.Message);
        Assert.Equal(1, r.LaunchSerial);
        Assert.Contains(Path.Combine(h.Layout.VersionDir(1), "anim.mul"), File.ReadAllText(r.OverrideFile!));

        h.Publish(h.Manifest(3)); // server-side rollback = git revert → higher serial with old content
        var r3 = await h.Run();
        Assert.Equal(3, h.State.CurrentSerial);
        Assert.Equal(0, h.State.BadSerial);
        Assert.Equal(0, r3.ObjectsDownloaded); // old objects reused locally
    }

    [Fact]
    public async Task Current_and_previous_versions_are_kept_older_pruned()
    {
        using var h = await V1ConfirmedThenV2(new Harness());
        Assert.True(Directory.Exists(h.Layout.VersionDir(1)));
        Assert.True(Directory.Exists(h.Layout.VersionDir(2)));
        h.Rollback.OnConfirmed(); // 2 good
        h.Publish(h.Manifest(3));
        await h.Run();
        h.Rollback.OnConfirmed(); // 3 good → keep {3, 2}
        Assert.Equal(new long[] { 2, 3 }, new VersionStore(h.Layout, new LauncherLog(h.Layout.Logs)).Serials().Order());
    }

    [Fact]
    public async Task Failed_local_verify_reverts_to_last_good()
    {
        using var h = await V1ConfirmedThenV2(new Harness());
        File.WriteAllText(Path.Combine(h.Layout.VersionDir(2), "anim.mul"), "bit rot");
        var msg = h.Rollback.RecoverOnStartup();
        Assert.Contains("failed verification", msg);
        Assert.Equal(1, h.State.CurrentSerial);
        Assert.Equal(0, h.State.BadSerial); // corruption is local, the release itself is not bad
        var r = await h.Run(); // server still at 2 → rebuilt, only the corrupted file re-fetched
        Assert.Equal(2, h.State.CurrentSerial);
        Assert.Equal(1, r.ObjectsDownloaded);
        Assert.True(new VersionStore(h.Layout, new LauncherLog(h.Layout.Logs)).Verify(2).Ok);
    }

    [Fact]
    public async Task Repair_reverifies_everything_and_refetches_only_bad_files()
    {
        using var h = new Harness();
        var m = h.Manifest(1);
        m.Cuo = h.CuoZip();
        h.Publish(m);
        await h.Run();
        File.WriteAllText(Path.Combine(h.Layout.VersionDir(1), "anim.mul"), "corrupt");
        File.WriteAllText(h.Layout.CuoExe, "SIGNED:SignPath Foundation\ntampered");
        File.WriteAllText(Path.Combine(h.GumpsDir, "3510.gump"), "user broke it");
        File.WriteAllText(Path.Combine(h.GumpsDir, "1000.gump"), "other shard");
        var before = h.Source.ObjectDownloads;

        var r = await h.Repair();
        Assert.Equal(2, r.ObjectsDownloaded); // anim.mul + cuo zip; gump restored from the version folder copy
        Assert.Equal(before + 2, h.Source.ObjectDownloads);
        Assert.True(new VersionStore(h.Layout, new LauncherLog(h.Layout.Logs)).Verify(1).Ok);
        Assert.True(r.Cuo.Ok, r.Cuo.Message);
        Assert.Equal(Harness.Gump3510, UoGump(h)); // owned file restored by explicit repair
        Assert.Equal("other shard", File.ReadAllText(Path.Combine(h.GumpsDir, "1000.gump"))); // foreign untouched

        var r2 = await h.Run(); // adjacent: back to a no-op
        Assert.Equal(0, r2.ObjectsDownloaded);
        Assert.Equal(UpdateOutcome.UpToDate, r2.Outcome);
    }

    [Fact]
    public async Task Repair_never_overwrites_foreign_gump()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.GumpsDir, "3510.gump"), "foreign");
        h.Publish(h.Manifest(1));
        await h.Run();
        var r = await h.Repair();
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(h.GumpsDir, "3510.gump")));
        Assert.Contains(r.Conflicts, c => c.Contains("ConflictForeign"));
    }

    [Fact]
    public async Task Use_previous_version_switches_and_keeps_ledger_consistent()
    {
        using var h = await V1ConfirmedThenV2(new Harness());
        h.Rollback.OnConfirmed(); // even a confirmed release can be backed out by the player
        var msg = h.Rollback.UsePrevious();
        Assert.Contains("Reverted from version 2 to 1", msg);
        Assert.Equal(1, h.State.CurrentSerial);
        Assert.Equal(Harness.Gump3510, UoGump(h));
        var ledger = GumpLedger.Load(h.GumpsDir);
        Assert.Equal(Hashing.HashFile(Path.Combine(h.GumpsDir, "3510.gump")).Sha256, ledger.Files["3510.gump"]);
        Assert.Equal(UpdateOutcome.KeptLastGood, (await h.Run()).Outcome); // 2 is not re-promoted
        Assert.Equal(1, h.State.CurrentSerial);
    }

    [Fact]
    public async Task No_fallback_available_keeps_current()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        Assert.Equal("No previous version is available on this PC.", h.Rollback.UsePrevious());
        Assert.Equal(1, h.State.CurrentSerial);
    }

    [Fact]
    public async Task Kill_switch_launches_last_good_with_message()
    {
        using var h = await V1ConfirmedThenV2(new Harness());
        h.Rollback.OnConfirmed();
        var m = h.Manifest(3); m.Disabled = true; m.Message = "maintenance until 18:00";
        h.Publish(m);
        var before = h.Source.ObjectDownloads;
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.Disabled, r.Outcome);
        Assert.Contains("maintenance until 18:00", r.Message);
        Assert.Equal(2, r.LaunchSerial);
        Assert.Equal(h.State.LastGoodSerial, r.LaunchSerial);
        Assert.NotNull(r.OverrideFile);
        Assert.Equal(before, h.Source.ObjectDownloads);
    }

    [Fact]
    public async Task Ledger_consistent_after_rollback_then_uninstall_restores_uo_folder()
    {
        using var h = new Harness();
        var before = Directory.GetFiles(h.UoPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Hashing.HashFile(p).Sha256);
        await V1ConfirmedThenV2(h);
        h.Rollback.UsePrevious();
        GumpLedger.Load(h.GumpsDir).Uninstall();
        var after = Directory.GetFiles(h.UoPath, "*", SearchOption.AllDirectories).ToDictionary(p => p, p => Hashing.HashFile(p).Sha256);
        Assert.Equal(before, after);
    }
}
