using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ShardLauncher.Patching;

namespace ShardLauncher.Core;

/// <summary>cuo\.cuo-install.json: which mirrored zip is extracted + hash of every extracted file.</summary>
public sealed class CuoIndex
{
    [JsonPropertyName("zip_sha256")] public string ZipSha256 { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("authenticode_subject")] public string AuthenticodeSubject { get; set; } = "";
    [JsonPropertyName("files")] public SortedDictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed record CuoCheck(bool Ok, string Message);

/// <summary>
/// Pinned ClassicUO. The zip is an unmodified official release mirrored at objects/&lt;sha&gt;
/// and pinned by sha256+size in the signed manifest. Install = safe extract to a temp dir, Authenticode-check
/// ClassicUO.exe against the pinned signer, write notices + index, then swap into cuo\. Any failure keeps the old install.
/// </summary>
public sealed class CuoInstaller(InstallLayout layout, IAuthenticodeVerifier verifier, LauncherLog log)
{
    public const string ExeName = "ClassicUO.exe";
    public const string IndexName = ".cuo-install.json";
    public const string NoticesName = "ClassicUO-THIRD-PARTY-NOTICES.txt";
    public int MaxEntries { get; init; } = 2000;
    public long MaxUncompressedBytes { get; init; } = 1L << 30;

    private static readonly Regex SegmentRx = new(@"^[A-Za-z0-9._+-]{1,128}$", RegexOptions.CultureInvariant);

    public CuoIndex? ReadIndex()
    {
        var p = Path.Combine(layout.CuoDir, IndexName);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<CuoIndex>(File.ReadAllBytes(p)) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Cheap check used by the updater: is this exact zip already installed?</summary>
    public bool IsCurrent(CuoEntry want) => ReadIndex()?.ZipSha256 == want.Sha256;

    /// <summary>Full check before every launch and on Repair: every extracted file matches its hash, and ClassicUO.exe is signed by the pinned subject.</summary>
    public CuoCheck VerifyInstalled()
    {
        var idx = ReadIndex();
        if (idx is null) return new(false, "ClassicUO is not installed");
        if (!idx.Files.ContainsKey(ExeName)) return new(false, "install index has no ClassicUO.exe");
        foreach (var (rel, sha) in idx.Files)
        {
            var p = SafeJoin(layout.CuoDir, rel);
            if (!File.Exists(p)) return new(false, $"{rel} is missing");
            if (Hashing.HashFile(p).Sha256 != sha) return new(false, $"{rel} was altered (hash mismatch)");
        }
        var (ok, why) = Authenticode.Check(verifier, layout.CuoExe, idx.AuthenticodeSubject);
        return ok ? new(true, $"ClassicUO {idx.Version} verified") : new(false, "ClassicUO.exe refused: " + why);
    }

    /// <summary>Install a zip whose sha256/size the caller has already verified against the signed manifest.</summary>
    public CuoCheck Install(string verifiedZip, CuoEntry want)
    {
        var tmp = layout.CuoDir + ".tmp-" + Guid.NewGuid().ToString("N");
        var old = layout.CuoDir + ".old-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!Authenticode.PinnedSubjects.Contains(want.AuthenticodeSubject))
                return Fail($"manifest subject '{want.AuthenticodeSubject}' is not pinned in this launcher");
            Directory.CreateDirectory(tmp);
            var index = new CuoIndex { ZipSha256 = want.Sha256, Version = want.Version, AuthenticodeSubject = want.AuthenticodeSubject };
            Extract(verifiedZip, tmp, index);
            if (!index.Files.ContainsKey(ExeName)) return Fail("zip has no ClassicUO.exe at its root");
            var (ok, why) = Authenticode.Check(verifier, Path.Combine(tmp, ExeName), want.AuthenticodeSubject);
            if (!ok) return Fail("ClassicUO.exe refused: " + why);
            using (var n = typeof(CuoInstaller).Assembly.GetManifestResourceStream("notices.cuo.txt")!)
            using (var f = File.Create(Path.Combine(tmp, NoticesName))) n.CopyTo(f);
            File.WriteAllBytes(Path.Combine(tmp, IndexName), JsonSerializer.SerializeToUtf8Bytes(index, new JsonSerializerOptions { WriteIndented = true }));

            if (Directory.Exists(layout.CuoDir)) Directory.Move(layout.CuoDir, old); // fails if CUO is running: old install kept
            try { Directory.Move(tmp, layout.CuoDir); }
            catch { if (Directory.Exists(old) && !Directory.Exists(layout.CuoDir)) Directory.Move(old, layout.CuoDir); throw; }
            TryDelete(old);
            log.Info($"installed ClassicUO {want.Version} ({want.Sha256[..12]}…)");
            return new(true, $"ClassicUO {want.Version} installed");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ManifestValidationException)
        {
            return Fail(e.Message);
        }
        finally { TryDelete(tmp); }

        CuoCheck Fail(string why)
        {
            log.Warn("ClassicUO install refused, keeping previous: " + why);
            return new(false, "ClassicUO update refused: " + why);
        }
    }

    private void Extract(string zipPath, string root, CuoIndex index)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException($"zip has {zip.Entries.Count} entries (cap {MaxEntries})");
        long total = 0;
        var buf = new byte[81920];
        foreach (var e in zip.Entries)
        {
            var rel = e.FullName.Replace('\\', '/');
            if ((e.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException($"zip entry '{rel}' is a symlink");
            if (rel.EndsWith('/')) { Directory.CreateDirectory(SafeJoin(root, rel.TrimEnd('/'))); continue; }
            var dest = SafeJoin(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            using (var src = e.Open())
            using (var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write))
            {
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n; // counted while inflating; header sizes are not trusted
                    if (total > MaxUncompressedBytes) throw new InvalidDataException($"zip expands beyond {MaxUncompressedBytes} bytes");
                    dst.Write(buf, 0, n);
                    hash.AppendData(buf, 0, n);
                }
            }
            index.Files[rel] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    /// <summary>Zip-slip guard: relative, '/'-separated, safe segments only, and must resolve under root.</summary>
    public static string SafeJoin(string root, string rel)
    {
        var segs = rel.Split('/');
        if (rel.Length == 0 || rel.Contains(':') || segs.Any(s => s is "" or "." or ".." || !SegmentRx.IsMatch(s)))
            throw new InvalidDataException($"unsafe zip path '{rel}'");
        var full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segs).ToArray()));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"zip path '{rel}' escapes root");
        return full;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
