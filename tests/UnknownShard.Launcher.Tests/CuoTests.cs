using System.IO.Compression;
using UnknownShard.Launcher.Core;
using UnknownShard.Patching;
using Xunit;

namespace UnknownShard.Launcher.Tests;

/// <summary>unknown-shard#194: pinned ClassicUO mirror + Authenticode.</summary>
public class CuoTests
{
    private static PatchManifest WithCuo(Harness h, long serial, CuoEntry cuo)
    {
        var m = h.Manifest(serial);
        m.Cuo = cuo;
        return m;
    }

    [Fact]
    public async Task First_run_installs_verified_cuo_and_second_run_downloads_nothing()
    {
        using var h = new Harness();
        h.Publish(WithCuo(h, 1, h.CuoZip()));
        var r = await h.Run();
        Assert.True(r.Cuo.Ok, r.Cuo.Message);
        Assert.Equal(3, r.ObjectsDownloaded); // gump + art + cuo zip
        Assert.True(File.Exists(h.Layout.CuoExe));
        Assert.Contains("BSD 2-Clause", File.ReadAllText(Path.Combine(h.Layout.CuoDir, CuoInstaller.NoticesName)));
        Assert.Equal(h.Layout.CuoExe, LaunchCommand.Build(h.Layout, h.UoPath, r.OverrideFile).FileName);

        var r2 = await h.Run();
        Assert.Equal(UpdateOutcome.UpToDate, r2.Outcome);
        Assert.Equal(0, r2.ObjectsDownloaded);
        Assert.True(r2.Cuo.Ok);
    }

    [Fact]
    public async Task Altered_ClassicUO_exe_after_install_is_refused()
    {
        using var h = new Harness();
        h.Publish(WithCuo(h, 1, h.CuoZip()));
        await h.Run();
        // still "validly signed" per the fake, but bytes differ from the pinned zip: hash index catches it
        File.WriteAllText(h.Layout.CuoExe, "SIGNED:SignPath Foundation\npatched");
        var r = await h.Run();
        Assert.False(r.Cuo.Ok);
        Assert.Contains("altered", r.Cuo.Message);
    }

    [Fact]
    public async Task Unsigned_new_client_is_refused_and_old_client_kept()
    {
        using var h = new Harness();
        h.Publish(WithCuo(h, 1, h.CuoZip()));
        await h.Run();
        var exeBefore = File.ReadAllText(h.Layout.CuoExe);
        h.Publish(WithCuo(h, 2, h.CuoZip(exeContent: "MZ unsigned-v2")));
        var r = await h.Run();
        Assert.Contains(r.Conflicts, c => c.Contains("ClassicUO.exe refused"));
        Assert.Equal(exeBefore, File.ReadAllText(h.Layout.CuoExe));
        Assert.True(r.Cuo.Ok); // old verified client still launchable
        Assert.Equal(2, h.State.CurrentSerial); // art/gumps still updated
        Assert.DoesNotContain(Directory.GetDirectories(h.Layout.Root), d => d.Contains(".tmp-") || d.Contains(".old-"));
    }

    [Theory]
    [InlineData("SIGNED:Evil Corp\nexe", "SignPath Foundation")] // wrong signer
    [InlineData("SIGNED:Evil Corp\nexe", "Evil Corp")]           // manifest names an unpinned signer
    public async Task Wrong_or_unpinned_signer_is_refused(string exe, string manifestSubject)
    {
        using var h = new Harness();
        h.Publish(WithCuo(h, 1, h.CuoZip(exeContent: exe, subject: manifestSubject)));
        var r = await h.Run();
        Assert.False(r.Cuo.Ok);
        Assert.False(Directory.Exists(h.Layout.CuoDir));
    }

    [Fact]
    public async Task Tampered_cuo_zip_object_keeps_last_good()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        var cuo = h.CuoZip();
        h.Publish(WithCuo(h, 2, cuo));
        var bad = (byte[])h.Source.Files[cuo.File].Clone();
        bad[^5] ^= 0xFF;
        h.Source.Files[cuo.File] = bad;
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Equal(1, h.State.CurrentSerial);
        Assert.False(Directory.Exists(h.Layout.CuoDir));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("/abs.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("ClassicUO.exe:ads")]
    public async Task Zip_slip_entries_are_refused(string entry)
    {
        using var h = new Harness();
        h.Publish(WithCuo(h, 1, h.CuoZip(extra: z => { using var w = new StreamWriter(z.CreateEntry(entry).Open()); w.Write("x"); })));
        var r = await h.Run();
        Assert.Contains(r.Conflicts, c => c.Contains("unsafe zip path") || c.Contains("escapes"));
        Assert.False(Directory.Exists(h.Layout.CuoDir));
        Assert.False(File.Exists(Path.Combine(h.Layout.Root, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(h.Dir, "evil.dll")));
    }

    [Fact]
    public async Task Symlink_zip_entry_is_refused()
    {
        using var h = new Harness();
        h.Publish(WithCuo(h, 1, h.CuoZip(extra: z =>
        {
            var e = z.CreateEntry("link.dll");
            e.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
            using var w = new StreamWriter(e.Open()); w.Write("/etc/passwd");
        })));
        var r = await h.Run();
        Assert.Contains(r.Conflicts, c => c.Contains("symlink"));
    }

    [Fact]
    public void Expansion_cap_and_missing_exe_are_refused()
    {
        using var h = new Harness();
        var log = new LauncherLog(h.Layout.Logs);
        var cuo = h.CuoZip(extra: z => { using var w = new StreamWriter(z.CreateEntry("big.bin").Open()); w.Write(new string('A', 10_000)); });
        var zip = Path.Combine(h.Dir, "z.zip");
        File.WriteAllBytes(zip, h.Extra[^1]);
        var capped = new CuoInstaller(h.Layout, new FakeVerifier(), log) { MaxUncompressedBytes = 5_000 };
        Assert.Contains("expands beyond", capped.Install(zip, cuo).Message);

        var noExe = h.CuoZip(exeContent: "");
        File.WriteAllBytes(zip, h.Extra[^1]);
        Assert.Contains("no ClassicUO.exe", new CuoInstaller(h.Layout, new FakeVerifier(), log).Install(zip, noExe).Message);
        Assert.False(Directory.Exists(h.Layout.CuoDir));
    }

    [Fact]
    public void Non_windows_verifier_fails_closed()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.IsType<UnsupportedPlatformVerifier>(Authenticode.ForPlatform());
        Assert.False(Authenticode.Check(Authenticode.ForPlatform(), "/bin/sh", "SignPath Foundation").Ok);
    }

    [Fact]
    public async Task Players_own_classicuo_install_is_untouched()
    {
        using var h = new Harness();
        var theirs = Path.Combine(h.Dir, "ClassicUOLauncher", "ClassicUO");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "ClassicUO.exe"), "their client");
        File.WriteAllText(Path.Combine(theirs, "settings.json"), "{\"ip\":\"other.shard\"}");
        h.Publish(WithCuo(h, 1, h.CuoZip()));
        await h.Run();
        Assert.Equal("their client", File.ReadAllText(Path.Combine(theirs, "ClassicUO.exe")));
        Assert.Equal("{\"ip\":\"other.shard\"}", File.ReadAllText(Path.Combine(theirs, "settings.json")));
        Assert.Equal(Path.Combine(h.Layout.Root, "cuo-settings.json"), LaunchCommand.Build(h.Layout, h.UoPath, null).ArgumentList[1]);
    }
}
