using System.Net;
using System.Text;
using ShardLauncher.Core;
using ShardLauncher.Patching;
using Xunit;

namespace ShardLauncher.Tests;

public class UpdaterTests
{
    private static string Read(string p) => File.ReadAllText(p);

    [Fact]
    public async Task First_run_installs_gump_and_art_then_second_run_downloads_nothing()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.Equal(2, r.ObjectsDownloaded);
        Assert.Equal(Harness.Gump3510, File.ReadAllBytes(Path.Combine(h.GumpsDir, "3510.gump")));
        Assert.Equal(Harness.Sha(Harness.Gump3510), GumpLedger.Load(h.GumpsDir).Files["3510.gump"]);
        Assert.Equal(1, h.State.CurrentSerial);
        var anim = Path.Combine(h.Layout.VersionDir(1), "anim.mul");
        Assert.Equal(Harness.AnimMul, File.ReadAllBytes(anim));
        Assert.Contains("anim.mul=" + anim, Read(r.OverrideFile!));
        Assert.False(Directory.Exists(h.Layout.Staging));

        var before = h.Source.ObjectDownloads;
        var r2 = await h.Run();
        Assert.Equal(UpdateOutcome.UpToDate, r2.Outcome);
        Assert.Equal(0, r2.ObjectsDownloaded);
        Assert.Equal(before, h.Source.ObjectDownloads);
    }

    [Fact]
    public async Task Unchanged_art_is_reused_across_serials()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        var g2 = Encoding.ASCII.GetBytes("gump-3510-v2");
        h.Extra.Add(g2);
        h.Publish(h.Manifest(2, g2));
        var r = await h.Run();
        Assert.Equal(1, r.ObjectsDownloaded); // only the changed gump
        Assert.Equal(2, h.State.CurrentSerial);
        Assert.Equal(g2, File.ReadAllBytes(Path.Combine(h.GumpsDir, "3510.gump"))); // owned -> updated
    }

    [Fact]
    public async Task Tampered_object_is_rejected_and_last_good_kept()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        var anim2 = Encoding.ASCII.GetBytes("anim-v2");
        h.Extra.Add(anim2);
        var m2 = h.Manifest(2);
        m2.Files.RemoveAll(f => f.Id == "anim-849-mul");
        m2.Files.Add(Harness.Entry("anim-849-mul", "shard:art/anim.mul", anim2));
        h.Publish(m2);
        h.Source.Files["objects/" + Harness.Sha(anim2)] = Encoding.ASCII.GetBytes("anim-v3"); // same size, wrong bytes
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Contains("failed verification", r.Message);
        Assert.Equal(1, h.State.CurrentSerial);
        Assert.Equal(1, r.LaunchSerial);
        Assert.Equal(Harness.AnimMul, File.ReadAllBytes(Path.Combine(h.Layout.VersionDir(1), "anim.mul")));
        Assert.False(Directory.Exists(h.Layout.VersionDir(2)));
        Assert.NotNull(r.OverrideFile);
    }

    [Fact]
    public async Task Bad_signature_keeps_last_good_with_warning()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        h.Publish(h.Manifest(2), tamperManifest: b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("\"v2\"", "\"v9\"")));
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Contains("signature", r.Message);
        Assert.Equal(1, r.LaunchSerial);
        Assert.Equal(1, h.State.HighestSerial); // unverified serial never recorded
    }

    [Fact]
    public async Task Untrusted_key_is_rejected()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        h.Trusted["test-key-a"] = other.ExportSubjectPublicKeyInfoPem();
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.False(File.Exists(Path.Combine(h.GumpsDir, "3510.gump")));
    }

    [Fact]
    public async Task Unreachable_server_keeps_last_good()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        h.Source.Unreachable = true;
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Contains("connection refused", r.Message);
        Assert.Equal(1, r.LaunchSerial);
        Assert.NotNull(r.OverrideFile);
    }

    [Fact]
    public async Task Downgrade_serial_is_rejected_even_when_validly_signed()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(5));
        await h.Run();
        h.Publish(h.Manifest(4)); // replay of an older, genuinely signed manifest
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Contains("replay", r.Message);
        Assert.Equal(5, h.State.CurrentSerial);
    }

    [Fact]
    public async Task Pointer_serial_mismatch_is_rejected()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(3));
        h.Source.Files["current.json"] = Encoding.ASCII.GetBytes("""{"serial":4,"manifest":"manifests/3.json","sig":"manifests/3.json.sig"}""");
        Assert.Equal(UpdateOutcome.KeptLastGood, (await h.Run()).Outcome);
    }

    [Theory]
    [InlineData("uo:Gumps/../art.mul")]
    [InlineData("uo:Gumps/..\\..\\evil.gump")]
    [InlineData("shard:art/../../../evil.dll")]
    [InlineData("uo:art.mul")]
    [InlineData("uo:Gumps/3510.gump:ads")]
    public async Task Signed_path_traversal_manifest_is_rejected(string dest)
    {
        using var h = new Harness();
        var m = h.Manifest(1, withArt: false);
        m.Files[0].Dest = dest;
        h.Publish(m, validate: false);
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Equal(0, h.Source.ObjectDownloads);
        Assert.Equal(new[] { "anim.idx", "art.mul" }, Directory.GetFiles(h.UoPath).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetFiles(h.GumpsDir));
    }

    [Fact]
    public async Task Foreign_gump_is_not_overwritten_and_other_files_untouched()
    {
        using var h = new Harness();
        var foreign = Path.Combine(h.GumpsDir, "3510.gump");
        File.WriteAllText(foreign, "someone else's gump");
        File.WriteAllText(Path.Combine(h.GumpsDir, "1000.gump"), "other shard gump");
        var stockBefore = Directory.GetFiles(h.UoPath).ToDictionary(p => p, p => Hashing.HashFile(p).Sha256);
        h.Publish(h.Manifest(1));
        var r = await h.Run();
        Assert.Equal("someone else's gump", Read(foreign));
        Assert.Equal("other shard gump", Read(Path.Combine(h.GumpsDir, "1000.gump")));
        Assert.Contains(r.Conflicts, c => c.Contains("3510.gump") && c.Contains("ConflictForeign"));
        Assert.False(File.Exists(Path.Combine(h.GumpsDir, GumpLedger.FileName)) && GumpLedger.Load(h.GumpsDir).Files.ContainsKey("3510.gump"));
        foreach (var (p, sha) in stockBefore) Assert.Equal(sha, Hashing.HashFile(p).Sha256); // stock hashes unchanged
        Assert.Equal(1, h.State.CurrentSerial); // art still promoted
    }

    [Fact]
    public async Task Ledger_owned_gump_modified_by_user_is_not_overwritten()
    {
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        var p = Path.Combine(h.GumpsDir, "3510.gump");
        File.WriteAllText(p, "user edited");
        var g2 = Encoding.ASCII.GetBytes("gump-3510-v2");
        h.Extra.Add(g2);
        h.Publish(h.Manifest(2, g2));
        var r = await h.Run();
        Assert.Equal("user edited", Read(p));
        Assert.Contains(r.Conflicts, c => c.Contains("ConflictModified"));
    }

    [Fact]
    public async Task Identical_foreign_gump_is_left_alone_and_not_claimed()
    {
        using var h = new Harness();
        File.WriteAllBytes(Path.Combine(h.GumpsDir, "3510.gump"), Harness.Gump3510);
        h.Publish(h.Manifest(1, withArt: false));
        var r = await h.Run();
        Assert.Empty(r.Conflicts);
        Assert.Equal(0, r.ObjectsDownloaded);
        Assert.False(File.Exists(Path.Combine(h.GumpsDir, GumpLedger.FileName)));
    }

    [Fact]
    public async Task Kill_switch_skips_updates()
    {
        using var h = new Harness();
        var m = h.Manifest(1); m.Disabled = true; m.Message = "maintenance";
        h.Publish(m);
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.Disabled, r.Outcome);
        Assert.Contains("maintenance", r.Message);
        Assert.Equal(0, h.Source.ObjectDownloads);
    }

    [Fact]
    public async Task Min_launcher_newer_than_us_keeps_last_good_and_flags_update()
    {
        using var h = new Harness();
        var m = h.Manifest(1); m.MinLauncher = "2.0.0";
        h.Publish(m);
        var r = await h.Run();
        Assert.True(r.LauncherUpdateRequired);
        Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
        Assert.Equal(0, h.Source.ObjectDownloads);
    }

    [Fact]
    public async Task Requires_stock_mismatch_skips_group_but_installs_gump()
    {
        using var h = new Harness();
        var m = h.Manifest(1);
        m.RequiresStock = new() { new StockRequirement { Name = "anim.idx", Sha256 = new string('0', 64), AppliesTo = new() { "anim.mul" } } };
        h.Publish(m);
        var r = await h.Run();
        Assert.Equal(UpdateOutcome.Updated, r.Outcome);
        Assert.True(File.Exists(Path.Combine(h.GumpsDir, "3510.gump")));
        Assert.False(File.Exists(Path.Combine(h.Layout.VersionDir(1), "anim.mul")));
        Assert.DoesNotContain("anim.mul=", Read(r.OverrideFile!));
    }

    [Fact]
    public async Task Held_entries_are_not_installed()
    {
        using var h = new Harness();
        var m = h.Manifest(1);
        m.Files.Single(f => f.Id == "anim-849-mul").Hold = true;
        h.Publish(m);
        var r = await h.Run();
        Assert.Equal(1, r.ObjectsDownloaded);
        Assert.False(File.Exists(Path.Combine(h.Layout.VersionDir(1), "anim.mul")));
    }

    [Fact]
    public async Task Promote_failure_leaves_previous_set_intact()
    {
        if (OperatingSystem.IsWindows()) return; // uses POSIX permissions to simulate disk-full / locked dir
        using var h = new Harness();
        h.Publish(h.Manifest(1));
        await h.Run();
        var anim2 = Encoding.ASCII.GetBytes("anim-v2");
        h.Extra.Add(anim2);
        var m2 = h.Manifest(2);
        m2.Files.RemoveAll(f => f.Id == "anim-849-mul");
        m2.Files.Add(Harness.Entry("anim-849-mul", "shard:art/anim.mul", anim2));
        h.Publish(m2);
        File.SetUnixFileMode(h.Layout.ArtVersions, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var r = await h.Run();
            Assert.Equal(UpdateOutcome.KeptLastGood, r.Outcome);
            Assert.Contains("previous version kept", r.Message);
            Assert.Equal(1, h.State.CurrentSerial);
            Assert.Equal(Harness.AnimMul, File.ReadAllBytes(Path.Combine(h.Layout.VersionDir(1), "anim.mul")));
            Assert.Equal(Harness.Gump3510, File.ReadAllBytes(Path.Combine(h.GumpsDir, "3510.gump")));
        }
        finally { Harness.MakeWritable(h.Layout.ArtVersions); }
    }
}

public class SourceAndLaunchTests
{
    [Fact]
    public void Https_only()
    {
        Assert.Throws<ArgumentException>(() => new HttpsPatchSource(new Uri("http://updates.example.invalid/files/")));
        Assert.Throws<ArgumentException>(() => new HttpsPatchSource(new Uri("https://updates.example.invalid/files")));
        _ = new HttpsPatchSource(TestConfig.PatchBase);
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("objects/../current.json")]
    [InlineData("https://evil/x")]
    [InlineData("objects/ABC")]
    [InlineData("manifests/1.json/../../x")]
    public void Server_paths_are_allowlisted(string rel) =>
        Assert.Throws<PatchSourceException>(() => PatchPaths.Check(rel));

    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        public Uri? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    [Fact]
    public async Task Download_enforces_exact_size_while_streaming()
    {
        var sha = new string('a', 64);
        var dir = Path.Combine(Path.GetTempPath(), "usl-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var handler = new StubHandler(new byte[100]);
        var src = new HttpsPatchSource(TestConfig.PatchBase, handler);
        await Assert.ThrowsAsync<PatchSourceException>(() => src.DownloadToFileAsync("objects/" + sha, Path.Combine(dir, "a"), 50, null, default));
        await Assert.ThrowsAsync<PatchSourceException>(() => src.DownloadToFileAsync("objects/" + sha, Path.Combine(dir, "b"), 150, null, default));
        await src.DownloadToFileAsync("objects/" + sha, Path.Combine(dir, "c"), 100, null, default);
        Assert.Equal(TestConfig.PatchBase + "objects/" + sha, handler.LastUri!.ToString());
        await Assert.ThrowsAsync<PatchSourceException>(() => src.GetBytesAsync("current.json", 10, default));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Launch_command_matches_plan_3_4()
    {
        var l = new InstallLayout(Path.Combine(Path.GetTempPath(), "US"));
        var psi = LaunchCommand.Build(l, Path.Combine(Path.GetTempPath(), "UO"), "/x/uofiles-override.txt", TestConfig.Host, TestConfig.Port);
        Assert.Equal(l.CuoExe, psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(new[]
        {
            "-settings", l.CuoSettings, "-uopath", Path.Combine(Path.GetTempPath(), "UO"),
            "-uofilesoverride", "/x/uofiles-override.txt", "-profilespath", l.Profiles,
            "-ip", TestConfig.Host, "-port", TestConfig.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, psi.ArgumentList);
        Assert.DoesNotContain(LaunchCommand.Build(l, "/uo", null, TestConfig.Host, TestConfig.Port).ArgumentList, a => a == "-uofilesoverride");
    }

    [Theory]
    [InlineData("", 1234)]
    [InlineData("bad host!", 1234)]
    [InlineData("game.example.invalid", 0)]
    [InlineData("game.example.invalid", 70000)]
    public void Launch_command_has_no_default_server(string host, int port) =>
        Assert.Throws<LauncherConfigException>(() => LaunchCommand.Build(new InstallLayout(Path.GetTempPath()), "/uo", null, host, port));
}

public class SigningRoundTripTests
{
    /// <summary>Replaces the old production-key test vector: a throwaway key is generated per run.</summary>
    [Fact]
    public void Throwaway_key_signs_and_verifies_a_manifest_and_tamper_is_rejected()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var pub = key.ExportSubjectPublicKeyInfoPem();
        var keys = new Dictionary<string, string> { ["test-key-a"] = pub };
        Assert.Equal(Hashing.Sha256Hex(key.ExportSubjectPublicKeyInfo()), ManifestSigner.Fingerprint(pub));

        var m = new PatchManifest { Shard = "example-shard", Serial = 1, Version = "v1", SigningKeyId = "test-key-a" };
        m.Files.Add(Harness.Entry("gump-3510", "uo:Gumps/3510.gump", Harness.Gump3510));
        var bytes = ManifestJson.Serialize(m);
        var sigFile = Convert.ToBase64String(ManifestSigner.Sign(bytes, key.ExportPkcs8PrivateKeyPem())) + "\n";
        var sig = ManifestSigner.DecodeSigFile(sigFile);
        Assert.Equal(1, ManifestSigner.VerifyAndParse(bytes, sig, keys).Serial);
        bytes[5] ^= 1;
        Assert.Throws<ManifestValidationException>(() => ManifestSigner.VerifyAndParse(bytes, sig, keys));
    }
}
