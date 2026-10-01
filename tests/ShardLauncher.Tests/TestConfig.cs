using System.Security.Cryptography;
using ShardLauncher.Core;
using Xunit;

namespace ShardLauncher.Tests;

/// <summary>Placeholder server values for tests only (RFC 6761 .invalid names; never resolvable).</summary>
public static class TestConfig
{
    public const string Host = "game.example.invalid";
    public const int Port = 7775;
    public static readonly Uri PatchBase = new("https://updates.example.invalid/files/");

    public static string NewPublicKeyPem()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return k.ExportSubjectPublicKeyInfoPem();
    }

    public static LauncherConfig Complete() => LauncherConfig.Create("Example Shard", "ExampleShard", Host, "7775", PatchBase.ToString(),
        new Dictionary<string, string> { ["test-key-a"] = NewPublicKeyPem() });
}

public class LauncherConfigTests
{
    [Fact]
    public void Unconfigured_build_fails_clearly_listing_every_missing_value()
    {
        var c = LauncherConfig.Create("", "", "", "", "", new Dictionary<string, string>());
        Assert.False(c.IsComplete);
        Assert.Equal(LauncherConfig.DefaultDisplayName, c.DisplayName);
        Assert.Equal(LauncherConfig.DefaultDataDirName, c.DataDirName);
        var e = Assert.Throws<LauncherConfigException>(c.EnsureComplete);
        Assert.Contains("not configured", e.Message);
        foreach (var name in new[] { "LauncherServerHost", "LauncherServerPort", "LauncherPatchBaseUrl", "LauncherTrustedKey" })
            Assert.Contains(name, e.Message);
    }

    [Fact]
    public void Example_file_placeholders_are_rejected()
    {
        // launcher.build.example.props ships port 0000 and a <PUBLIC_KEY_PEM> placeholder.
        var c = LauncherConfig.Create("Example Shard", "ExampleShard", "game.example.invalid", "0000", "https://updates.example.invalid/files/",
            new Dictionary<string, string> { ["example-key-1"] = "<PUBLIC_KEY_PEM>" });
        Assert.Contains(c.Problems, p => p.Contains("port"));
        Assert.Contains(c.Problems, p => p.Contains("example-key-1"));
    }

    [Fact]
    public void Complete_config_passes()
    {
        var c = TestConfig.Complete();
        Assert.Empty(c.Problems);
        c.EnsureComplete();
        Assert.Equal(TestConfig.Host, c.ServerHost);
        Assert.Equal(TestConfig.Port, c.ServerPort);
    }

    [Theory]
    [InlineData("http://updates.example.invalid/files/")]
    [InlineData("https://updates.example.invalid/files")]
    [InlineData("not a url")]
    public void Patch_base_must_be_https_with_trailing_slash(string url)
    {
        var c = TestConfig.Complete() with { PatchBase = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : null };
        Assert.Contains(c.Problems, p => p.Contains("LauncherPatchBaseUrl"));
    }

    [Fact]
    public void Private_key_is_refused()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = TestConfig.Complete() with { TrustedKeys = new Dictionary<string, string> { ["k"] = k.ExportPkcs8PrivateKeyPem() } };
        Assert.Contains(c.Problems, p => p.Contains("PRIVATE"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    public void Unsafe_data_dir_name_falls_back_to_default(string name) =>
        Assert.Equal(LauncherConfig.DefaultDataDirName, LauncherConfig.Create("x", name, "h", "1", "https://a/", new Dictionary<string, string>()).DataDirName);

    [Fact]
    public void Build_config_never_embeds_a_private_key()
    {
        foreach (var pem in TrustedKeys.LoadEmbedded().Values) Assert.DoesNotContain("PRIVATE", pem);
        _ = LauncherConfig.FromBuild(); // must not throw, configured or not
    }

    private static string TempFile(string content)
    {
        var p = Path.Combine(Path.GetTempPath(), "slcfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Runtime_override_changes_server_address_only()
    {
        var c = TestConfig.Complete();
        var p = TempFile("{\"host\":\"127.0.0.1\",\"port\":5000}");
        var o = c.WithServerOverride(p);
        Assert.Equal("127.0.0.1", o.ServerHost);
        Assert.Equal(5000, o.ServerPort);
        Assert.Equal(p, o.ServerOverrideSource);
        Assert.Equal(c.PatchBase, o.PatchBase);
        Assert.Same(c.TrustedKeys, o.TrustedKeys);
        Assert.Same(c, c.WithServerOverride(p + ".missing"));
        File.Delete(p);
    }

    [Theory]
    [InlineData("{\"host\":\"h.example.invalid\",\"trusted_keys\":{}}")]
    [InlineData("{\"patch_base\":\"https://evil.example.invalid/\"}")]
    [InlineData("{\"port\":0}")]
    [InlineData("{\"port\":\"5000\"}")]
    [InlineData("{\"host\":\"bad host!\"}")]
    [InlineData("[1]")]
    [InlineData("{not json")]
    public void Runtime_override_rejects_anything_else_loudly(string json)
    {
        var p = TempFile(json);
        Assert.Throws<LauncherConfigException>(() => TestConfig.Complete().WithServerOverride(p));
        File.Delete(p);
    }
}
