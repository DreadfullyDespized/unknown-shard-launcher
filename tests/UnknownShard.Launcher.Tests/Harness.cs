using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnknownShard.Launcher.Core;
using UnknownShard.Patching;

namespace UnknownShard.Launcher.Tests;

/// <summary>In-memory patch server. Counts object downloads.</summary>
public sealed class FakeSource : IPatchSource
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public bool Unreachable { get; set; }
    public int ObjectDownloads { get; private set; }

    public Task<byte[]> GetBytesAsync(string relPath, int maxBytes, CancellationToken ct)
    {
        PatchPaths.Check(relPath);
        if (Unreachable) throw new HttpRequestException("connection refused");
        if (!Files.TryGetValue(relPath, out var b)) throw new PatchSourceException($"{relPath}: HTTP 404");
        if (b.Length > maxBytes) throw new PatchSourceException("too big");
        return Task.FromResult(b);
    }

    public Task DownloadToFileAsync(string relPath, string destPath, long expectedSize, Action<long>? onBytes, CancellationToken ct)
    {
        PatchPaths.Check(relPath);
        if (Unreachable) throw new HttpRequestException("connection refused");
        ObjectDownloads++;
        var b = Files[relPath];
        File.WriteAllBytes(destPath, b);
        onBytes?.Invoke(b.Length);
        return Task.CompletedTask;
    }
}

public sealed class Harness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "usl-" + Guid.NewGuid().ToString("N"));
    public InstallLayout Layout { get; }
    public string UoPath { get; }
    public string GumpsDir => Path.Combine(UoPath, "Gumps");
    public FakeSource Source { get; } = new();
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public Dictionary<string, string> Trusted { get; }
    public Version LauncherVersion { get; set; } = new(1, 0, 0);

    public static readonly byte[] Gump3510 = Encoding.ASCII.GetBytes("gump-3510-v1-bytes");
    public static readonly byte[] AnimMul = Encoding.ASCII.GetBytes("anim-mul-849");
    public static readonly byte[] StockAnimIdx = Encoding.ASCII.GetBytes("stock-anim-idx");

    public Harness()
    {
        Layout = new InstallLayout(Path.Combine(Dir, "UnknownShard"));
        UoPath = Path.Combine(Dir, "UO");
        Directory.CreateDirectory(GumpsDir);
        File.WriteAllBytes(Path.Combine(UoPath, "anim.idx"), StockAnimIdx);
        File.WriteAllBytes(Path.Combine(UoPath, "art.mul"), Encoding.ASCII.GetBytes("stock-art"));
        new LauncherState { UoPath = UoPath }.Save(Layout.StatePath);
        Trusted = new() { ["us-2026a"] = Key.ExportSubjectPublicKeyInfoPem() };
    }

    public static string Sha(byte[] b) => Hashing.Sha256Hex(b);

    public static ManifestFile Entry(string id, string dest, byte[] content) => new()
    {
        Id = id, Dest = dest, File = "objects/" + Sha(content), Sha256 = Sha(content), Size = content.Length,
        Mode = dest.StartsWith("uo:", StringComparison.Ordinal) ? "add-only" : "shard-owned",
    };

    public PatchManifest Manifest(long serial, byte[]? gump = null, bool withArt = true)
    {
        var m = new PatchManifest { Shard = "unknown-shard", Serial = serial, Version = $"v{serial}", SigningKeyId = "us-2026a" };
        m.Files.Add(Entry("gump-3510", "uo:Gumps/3510.gump", gump ?? Gump3510));
        if (withArt)
        {
            m.Files.Add(Entry("anim-849-mul", "shard:art/anim.mul", AnimMul));
            m.OverrideMap = new() { ["anim.mul"] = "shard:art/anim.mul" };
        }
        return m;
    }

    /// <summary>Publish objects + signed manifest + pointer. Returns manifest bytes.</summary>
    public byte[] Publish(PatchManifest m, bool validate = true, Func<byte[], byte[]>? tamperManifest = null)
    {
        if (validate) ManifestValidator.Validate(m);
        foreach (var f in m.Files)
            if (!Source.Files.ContainsKey(f.File))
                Source.Files[f.File] = new[] { Gump3510, AnimMul }.Concat(Extra).First(b => Sha(b) == f.Sha256);
        var bytes = ManifestJson.Serialize(m);
        var sig = Key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var path = $"manifests/{m.Serial}.json";
        Source.Files[path] = tamperManifest?.Invoke(bytes) ?? bytes;
        Source.Files[path + ".sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(sig));
        Source.Files["current.json"] = JsonSerializer.SerializeToUtf8Bytes(new { serial = m.Serial, manifest = path, sig = path + ".sig" });
        return bytes;
    }

    public List<byte[]> Extra { get; } = new();

    public Task<UpdateResult> Run() =>
        new Updater(Layout, Source, Trusted, LauncherVersion, new LauncherLog(Layout.Logs)).RunAsync();

    public LauncherState State => LauncherState.Load(Layout.StatePath);

    public void Dispose()
    {
        Key.Dispose();
        try { MakeWritable(Dir); Directory.Delete(Dir, true); } catch (IOException) { }
    }

    public static void MakeWritable(string dir)
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(dir))
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Append(dir))
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
