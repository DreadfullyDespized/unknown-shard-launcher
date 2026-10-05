using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ShardLauncher.Patching;

namespace ShardLauncher.Core;

public sealed class PatchSourceException(string message) : Exception(message);

public interface IPatchSource
{
    Task<byte[]> GetBytesAsync(string relPath, int maxBytes, CancellationToken ct);
    Task DownloadToFileAsync(string relPath, string destPath, long expectedSize, Action<long>? onBytes, CancellationToken ct);
}

public static class PatchPaths
{
    private static readonly Regex Allowed = new(@"^(current\.json|manifests/[0-9]{1,12}\.json(\.sig)?|objects/[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    public static string Check(string relPath) =>
        Allowed.IsMatch(relPath) ? relPath : throw new PatchSourceException($"refusing server path '{relPath}'");
}

public sealed class PatchPointer
{
    [JsonPropertyName("serial")] public long Serial { get; set; }
    [JsonPropertyName("manifest")] public string Manifest { get; set; } = "";
    [JsonPropertyName("sig")] public string Sig { get; set; } = "";

    public static PatchPointer Parse(byte[] utf8)
    {
        PatchPointer? p;
        try { p = JsonSerializer.Deserialize<PatchPointer>(utf8); }
        catch (JsonException e) { throw new ManifestValidationException("current.json invalid: " + e.Message); }
        if (p is null || p.Serial <= 0) throw new ManifestValidationException("current.json: bad serial");
        var want = $"manifests/{p.Serial}.json";
        if (p.Manifest != want || p.Sig != want + ".sig") throw new ManifestValidationException("current.json: manifest/sig path must be manifests/<serial>.json(.sig)");
        return p;
    }
}

public sealed class HttpsPatchSource : IPatchSource
{
    private readonly Uri _base;
    private readonly HttpClient _http;

    public HttpsPatchSource(Uri baseUri, HttpMessageHandler? handler = null)
    {
        if (baseUri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("patch source must be https://");
        if (!baseUri.AbsolutePath.EndsWith('/')) throw new ArgumentException("patch base must end with '/'");
        _base = baseUri;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ShardLauncher/1.0");
    }

    private Uri Url(string rel)
    {
        var u = new Uri(_base, PatchPaths.Check(rel));
        if (u.Scheme != Uri.UriSchemeHttps || u.Host != _base.Host) throw new PatchSourceException("url escaped base");
        return u;
    }

    public async Task<byte[]> GetBytesAsync(string relPath, int maxBytes, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(Url(relPath), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new PatchSourceException($"{relPath}: HTTP {(int)resp.StatusCode}");
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > maxBytes) throw new PatchSourceException($"{relPath}: exceeds {maxBytes} bytes");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    public async Task DownloadToFileAsync(string relPath, string destPath, long expectedSize, Action<long>? onBytes, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(Url(relPath), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new PatchSourceException($"{relPath}: HTTP {(int)resp.StatusCode}");
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > expectedSize) throw new PatchSourceException($"{relPath}: larger than manifest size {expectedSize}");
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            onBytes?.Invoke(n);
        }
        if (total != expectedSize) throw new PatchSourceException($"{relPath}: got {total} bytes, manifest says {expectedSize}");
    }
}
