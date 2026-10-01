using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace ShardLauncher.Core;

public sealed record AuthenticodeResult(bool Valid, string? SignerName, string Detail);

public interface IAuthenticodeVerifier
{
    AuthenticodeResult Verify(string path);
}

public static class Authenticode
{
    /// <summary>Signer CNs we accept for ClassicUO.exe, compiled in. The signed manifest must also name the same subject.</summary>
    public static readonly IReadOnlySet<string> PinnedSubjects = new HashSet<string>(StringComparer.Ordinal) { "SignPath Foundation" };

    public static IAuthenticodeVerifier ForPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsAuthenticodeVerifier() : new UnsupportedPlatformVerifier();

    /// <summary>Valid signature AND signer CN equals the manifest subject AND that subject is pinned.</summary>
    public static (bool Ok, string Why) Check(IAuthenticodeVerifier v, string path, string expectedSubject)
    {
        if (!PinnedSubjects.Contains(expectedSubject)) return (false, $"subject '{expectedSubject}' is not pinned in this launcher");
        var r = v.Verify(path);
        if (!r.Valid) return (false, "Authenticode: " + r.Detail);
        if (r.SignerName != expectedSubject) return (false, $"signed by '{r.SignerName}', expected '{expectedSubject}'");
        return (true, "ok");
    }
}

/// <summary>Fails closed: no Authenticode on this OS means ClassicUO is never considered verified.</summary>
public sealed class UnsupportedPlatformVerifier : IAuthenticodeVerifier
{
    public AuthenticodeResult Verify(string path) => new(false, null, "Authenticode verification requires Windows");
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2, WtdRevokeNone = 0, WtdChoiceFile = 1, WtdCacheOnlyUrlRetrieval = 0x1000;

    public AuthenticodeResult Verify(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = Path.GetFullPath(path),
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                // Revocation is not checked online so offline players can still play; the hash pin in the signed manifest is the primary control.
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = pFile,
                dwProvFlags = WtdCacheOnlyUrlRetrieval,
            };
            var hr = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
            if (hr != 0) return new(false, null, $"WinVerifyTrust failed 0x{hr:X8}");
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return new(true, cert.GetNameInfo(X509NameType.SimpleName, false), cert.Subject);
        }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            return new(false, null, "no signer certificate: " + e.Message);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WintrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
