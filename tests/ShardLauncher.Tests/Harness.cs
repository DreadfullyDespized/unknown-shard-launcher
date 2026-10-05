using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShardLauncher.Core;
using ShardLauncher.Patching;

namespace ShardLauncher.Tests;

public sealed class FakeVerifier : IAuthenticodeVerifier
{
    public AuthenticodeResult Verify(string path)
    {
        var text = System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path));
        if (!text.StartsWith("SIGNED:", StringComparison.Ordinal)) return new(false, null, "not signed");
        return new(true, text[7..text.IndexOf('\n')], "fake");
    }
}

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
        Layout = new InstallLayout(Path.Combine(Dir, "LauncherData"));
        UoPath = Path.Combine(Dir, "UO");
        Directory.CreateDirectory(GumpsDir);
        File.WriteAllBytes(Path.Combine(UoPath, "anim.idx"), StockAnimIdx);
        File.WriteAllBytes(Path.Combine(UoPath, "art.mul"), Encoding.ASCII.GetBytes("stock-art"));
        new LauncherState { UoPath = UoPath }.Save(Layout.StatePath);
        Trusted = new() { ["test-key-a"] = Key.ExportSubjectPublicKeyInfoPem() };
    }

    public static string Sha(byte[] b) => Hashing.Sha256Hex(b);

    public static ManifestFile Entry(string id, string dest, byte[] content) => new()
    {
        Id = id, Dest = dest, File = "objects/" + Sha(content), Sha256 = Sha(content), Size = content.Length,
        Mode = dest.StartsWith("uo:", StringComparison.Ordinal) ? "add-only" : "shard-owned",
    };

    public PatchManifest Manifest(long serial, byte[]? gump = null, bool withArt = true)
    {
        var m = new PatchManifest { Shard = "example-shard", Serial = serial, Version = $"v{serial}", SigningKeyId = "test-key-a" };
        m.Files.Add(Entry("gump-3510", "uo:Gumps/3510.gump", gump ?? Gump3510));
        if (withArt)
        {
            m.Files.Add(Entry("anim-849-mul", "shard:art/anim.mul", AnimMul));
            m.OverrideMap = new() { ["anim.mul"] = "shard:art/anim.mul" };
        }
        return m;
    }

    public byte[] Publish(PatchManifest m, bool validate = true, Func<byte[], byte[]>? tamperManifest = null)
    {
        if (validate) ManifestValidator.Validate(m);
        var all = new[] { Gump3510, AnimMul }.Concat(Extra).ToList();
        foreach (var f in m.Files)
            if (!Source.Files.ContainsKey(f.File))
                Source.Files[f.File] = all.First(b => Sha(b) == f.Sha256);
        if (m.Cuo is not null && !Source.Files.ContainsKey(m.Cuo.File))
            Source.Files[m.Cuo.File] = all.First(b => Sha(b) == m.Cuo.Sha256);
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
        new Updater(Layout, Source, Trusted, LauncherVersion, new LauncherLog(Layout.Logs), Verifier).RunAsync();

    public IAuthenticodeVerifier Verifier { get; set; } = new FakeVerifier();

    public CuoEntry CuoZip(string exeContent = "SIGNED:SignPath Foundation\nexe-v1", string subject = "SignPath Foundation",
        Action<System.IO.Compression.ZipArchive>? extra = null)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(content);
            }
            if (exeContent.Length > 0) Add("ClassicUO.exe", exeContent);
            Add("cuo.dll", "native");
            Add("SDL3.dll", "sdl");
            extra?.Invoke(z);
        }
        var bytes = ms.ToArray();
        Extra.Add(bytes);
        return new CuoEntry { File = "objects/" + Sha(bytes), Sha256 = Sha(bytes), Size = bytes.Length,
            Version = "ClassicUO-main-release@test", AuthenticodeSubject = subject };
    }

    public LauncherState State => LauncherState.Load(Layout.StatePath);

    public RollbackManager Rollback => new(Layout, new LauncherLog(Layout.Logs));

    public Task<UpdateResult> Repair() =>
        new Updater(Layout, Source, Trusted, LauncherVersion, new LauncherLog(Layout.Logs), Verifier).RunAsync(repair: true);

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
