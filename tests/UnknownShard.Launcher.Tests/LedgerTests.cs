using System.Text;
using UnknownShard.Launcher.Core;
using UnknownShard.Patching;
using Xunit;

namespace UnknownShard.Launcher.Tests;

/// <summary>unknown-shard#193: ledger-guarded add-only Gumps writes.</summary>
public class LedgerTests
{
    private static SortedDictionary<string, string> Snapshot(string dir) => new(
        Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(dir, p),
            p => Directory.Exists(p) ? "<dir>" : Hashing.HashFile(p).Sha256), StringComparer.Ordinal);

    private static string Log(Harness h) => string.Concat(Directory.GetFiles(h.Layout.Logs).Select(File.ReadAllText));

    [Fact]
    public async Task Install_tracks_3510_and_writes_nothing_else_in_uo_dir()
    {
        using var h = new Harness();
        var before = Snapshot(h.UoPath);
        h.Publish(h.Manifest(1));
        await h.Run();
        var after = Snapshot(h.UoPath);
        var added = after.Keys.Except(before.Keys).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { Path.Combine("Gumps", GumpLedger.FileName), Path.Combine("Gumps", "3510.gump") }, added);
        foreach (var (k, v) in before) Assert.Equal(v, after[k]);
        Assert.Equal(Harness.Sha(Harness.Gump3510), GumpLedger.Load(h.GumpsDir).Files["3510.gump"]);
    }

    [Fact]
    public async Task Foreign_3510_is_untouched_and_conflict_logged()
    {
        using var h = new Harness();
        File.WriteAllText(Path.Combine(h.GumpsDir, "3510.gump"), "foreign");
        h.Publish(h.Manifest(1));
        await h.Run();
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(h.GumpsDir, "3510.gump")));
        Assert.Contains("conflict: Gumps\\3510.gump ConflictForeign", Log(h));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uninstall_restores_uo_folder_exactly(bool gumpsDirExisted)
    {
        using var h = new Harness();
        if (gumpsDirExisted) File.WriteAllText(Path.Combine(h.GumpsDir, "1000.gump"), "other shard");
        else Directory.Delete(h.GumpsDir);
        var before = Snapshot(h.UoPath);
        h.Publish(h.Manifest(1));
        await h.Run();
        Assert.True(File.Exists(Path.Combine(h.GumpsDir, "3510.gump")));
        var r = GumpLedger.Load(h.GumpsDir).Uninstall();
        Assert.Equal(new[] { "3510.gump" }, r.Removed);
        Assert.Equal(before, Snapshot(h.UoPath));
    }

    [Fact]
    public async Task Uninstall_keeps_user_edited_ledger_file()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        File.WriteAllText(Path.Combine(h.GumpsDir, "3510.gump"), "user edit");
        var r = GumpLedger.Load(h.GumpsDir).Uninstall();
        Assert.Equal(new[] { "3510.gump" }, r.Kept);
        Assert.Equal("user edit", File.ReadAllText(Path.Combine(h.GumpsDir, "3510.gump")));
    }

    [Fact]
    public async Task Gumps_dir_symlink_is_rejected()
    {
        using var h = new Harness();
        var elsewhere = Path.Combine(h.Dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.Delete(h.GumpsDir);
        Directory.CreateSymbolicLink(h.GumpsDir, elsewhere);
        h.Publish(h.Manifest(1));
        var r = await h.Run();
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        Assert.Contains(r.Conflicts, c => c.Contains("3510.gump"));
    }

    [Fact]
    public async Task Gump_file_symlink_is_not_followed_overwritten_or_deleted()
    {
        using var h = new Harness();
        var target = Path.Combine(h.Dir, "victim.bin");
        File.WriteAllText(target, "victim");
        File.CreateSymbolicLink(Path.Combine(h.GumpsDir, "3510.gump"), target);
        h.Publish(h.Manifest(1));
        var r = await h.Run();
        Assert.Contains(r.Conflicts, c => c.Contains("ConflictForeign"));
        // even a forged ledger entry must not make us delete through the link
        var l = GumpLedger.Load(h.GumpsDir);
        l.Files["3510.gump"] = Harness.Sha(Encoding.ASCII.GetBytes("victim"));
        Assert.False(l.TryRemoveOwned("3510.gump"));
        Assert.Equal("victim", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(Path.Combine(h.GumpsDir, "3510.gump")).LinkTarget);
    }

    [Fact]
    public void Forged_ledger_entries_outside_gump_names_are_ignored()
    {
        using var h = new Harness();
        var artSha = Hashing.HashFile(Path.Combine(h.UoPath, "art.mul")).Sha256;
        File.WriteAllText(Path.Combine(h.GumpsDir, GumpLedger.FileName),
            "{\"schema\":1,\"files\":{\"../art.mul\":\"" + artSha + "\",\"art.mul\":\"" + artSha + "\",\"70000.gump\":\"" + artSha + "\"}}");
        var l = GumpLedger.Load(h.GumpsDir);
        Assert.Empty(l.Files);
        Assert.NotNull(l.LoadWarning);
        l.Uninstall();
        Assert.Equal(artSha, Hashing.HashFile(Path.Combine(h.UoPath, "art.mul")).Sha256);
    }

    [Fact]
    public async Task Corrupt_ledger_owns_nothing_so_nothing_is_overwritten()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        File.WriteAllText(Path.Combine(h.GumpsDir, GumpLedger.FileName), "{not json");
        var g2 = Encoding.ASCII.GetBytes("gump-3510-v2");
        h.Extra.Add(g2);
        h.Publish(h.Manifest(2, g2));
        var r = await h.Run();
        Assert.Equal(Harness.Gump3510, File.ReadAllBytes(Path.Combine(h.GumpsDir, "3510.gump")));
        Assert.Contains(r.Conflicts, c => c.Contains("ConflictForeign"));
        Assert.Contains("ledger unreadable", Log(h));
    }

    [Fact]
    public async Task Gump_dropped_from_release_is_retired_only_if_owned()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        File.WriteAllText(Path.Combine(h.GumpsDir, "1000.gump"), "other shard");
        var m2 = h.Manifest(2);
        m2.Files.RemoveAll(f => f.Id == "gump-3510");
        h.Publish(m2);
        await h.Run();
        Assert.False(File.Exists(Path.Combine(h.GumpsDir, "3510.gump")));
        Assert.Equal("other shard", File.ReadAllText(Path.Combine(h.GumpsDir, "1000.gump")));
        Assert.Empty(GumpLedger.Load(h.GumpsDir).Files);
    }

    [Theory]
    [InlineData("../3510.gump")]
    [InlineData("art.mul")]
    [InlineData("65536.gump")]
    [InlineData("3510.gump:ads")]
    public void Ledger_api_rejects_non_gump_names(string name)
    {
        using var h = new Harness();
        var l = GumpLedger.Load(h.GumpsDir);
        Assert.Throws<ManifestValidationException>(() => l.Decide(name, new string('a', 64)));
        Assert.False(GumpLedger.IsGumpName(name));
    }
}
