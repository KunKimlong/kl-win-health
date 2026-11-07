using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace KLWinHealth.Services.VirusScan;

public enum SignatureStatus { NotApplicable, Valid, Unsigned, Invalid }

public record SignatureInfo(SignatureStatus Status, string? Signer, bool IsCatalog, string Detail);

/// <summary>
/// Authenticode verification with WinVerifyTrust (P/Invoke). Checks the embedded signature first, then
/// Windows security catalogs (how most Windows system files are signed). No network: revocation not checked.
/// </summary>
public static class SignatureVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const int CRYPT_E_FILE_ERROR = unchecked((int)0x80092003);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, SafeFileHandle hFile, ref uint pcbHash, [In, Out] byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, ref IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    public static SignatureInfo Verify(string path)
    {
        try
        {
            var rc = VerifyEmbedded(path);
            if (rc == 0)
                return new SignatureInfo(SignatureStatus.Valid, GetSigner(path), false, "Valid embedded signature");

            if (rc is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN or CRYPT_E_FILE_ERROR)
            {
                var cat = VerifyCatalog(path);
                if (cat != null) return cat;
                return new SignatureInfo(SignatureStatus.Unsigned, null, false, "No signature");
            }

            return new SignatureInfo(SignatureStatus.Invalid, GetSigner(path), false, Describe(rc));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Signature check failed for {Path}", path);
            return new SignatureInfo(SignatureStatus.NotApplicable, null, false, ex.Message);
        }
    }

    private static int VerifyEmbedded(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pathPtr };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = NewData(WTD_CHOICE_FILE, fileInfoPtr);
            var rc = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            return rc;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    private static SignatureInfo? VerifyCatalog(string path)
    {
        if (!CryptCATAdminAcquireContext2(out var hCatAdmin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0)) return null;
        IntPtr hCatInfo = IntPtr.Zero;
        try
        {
            byte[] hash;
            using (var fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, fh, ref size, null, 0);
                if (size == 0) return null;
                hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, fh, ref size, hash, 0)) return null;
            }

            IntPtr prev = IntPtr.Zero;
            hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, (uint)hash.Length, 0, ref prev);
            if (hCatInfo == IntPtr.Zero) return null;

            var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>(), wszCatalogFile = "" };
            if (!CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0)) return null;

            var catPath = Marshal.StringToHGlobalUni(ci.wszCatalogFile);
            var tag = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
            var member = Marshal.StringToHGlobalUni(path);
            var hashPtr = Marshal.AllocHGlobal(hash.Length);
            var infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
            try
            {
                Marshal.Copy(hash, 0, hashPtr, hash.Length);
                var info = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                    pcwszCatalogFilePath = catPath,
                    pcwszMemberTag = tag,
                    pcwszMemberFilePath = member,
                    pbCalculatedFileHash = hashPtr,
                    cbCalculatedFileHash = (uint)hash.Length,
                    hCatAdmin = hCatAdmin
                };
                Marshal.StructureToPtr(info, infoPtr, false);
                var data = NewData(WTD_CHOICE_CATALOG, infoPtr);
                var rc = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
                return rc == 0
                    ? new SignatureInfo(SignatureStatus.Valid, GetSigner(ci.wszCatalogFile) ?? "Microsoft Windows", true, "Signed via Windows catalog")
                    : new SignatureInfo(SignatureStatus.Invalid, null, true, Describe(rc));
            }
            finally
            {
                Marshal.FreeHGlobal(catPath);
                Marshal.FreeHGlobal(tag);
                Marshal.FreeHGlobal(member);
                Marshal.FreeHGlobal(hashPtr);
                Marshal.FreeHGlobal(infoPtr);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Catalog check failed for {Path}", path);
            return null;
        }
        finally
        {
            if (hCatInfo != IntPtr.Zero) CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
            CryptCATAdminReleaseContext(hCatAdmin, 0);
        }
    }

    private static WINTRUST_DATA NewData(uint unionChoice, IntPtr info) => new()
    {
        cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
        dwUIChoice = WTD_UI_NONE,
        fdwRevocationChecks = WTD_REVOKE_NONE,
        dwUnionChoice = unionChoice,
        pInfo = info,
        dwStateAction = WTD_STATEACTION_VERIFY,
        dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
    };

    private static string? GetSigner(string signedFile)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFile));
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? cert.Subject : name;
        }
        catch { return null; }
    }

    private static string Describe(int rc) => rc switch
    {
        unchecked((int)0x80096010) => "the file was changed after it was signed (bad digest)",
        unchecked((int)0x800B0109) => "certificate chain ends in an untrusted root",
        unchecked((int)0x800B0101) => "certificate expired",
        unchecked((int)0x800B010C) => "certificate was revoked",
        unchecked((int)0x800B0111) => "publisher is explicitly distrusted",
        unchecked((int)0x80096019) => "basic constraints check failed",
        unchecked((int)0x800B010A) => "certificate chain could not be built",
        _ => $"WinVerifyTrust error 0x{rc:X8}"
    };
}
