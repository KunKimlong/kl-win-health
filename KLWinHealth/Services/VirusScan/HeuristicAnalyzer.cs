using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using KLWinHealth.Models;
using Serilog;

namespace KLWinHealth.Services.VirusScan;

/// <summary>
/// Local heuristics: tricky file names, extension vs. real type, Office macros, risky PDF features,
/// risky script patterns, entropy (packing/obfuscation) and Authenticode signature.
/// </summary>
public static class HeuristicAnalyzer
{
    private const long MaxContentScan = 32L * 1024 * 1024;

    /// <summary>Patterns for shell scripts (PowerShell, batch, VBScript, HTA, WSF) — NOT applied to .js, where they misfire on web code.</summary>
    private static readonly (Regex rx, int score, string text)[] ShellOnlyPatterns =
    {
        (new(@"(?:powershell|pwsh)[^\r\n]{0,60}\s-(?:e|ec|en|enc|enco|encodedcommand)\s+[A-Za-z0-9+/=]{16,}", RegexOptions.IgnoreCase), 30, "Runs an encoded PowerShell command (-EncodedCommand)"),
        (new(@"\bIEX\s*[\(\$]|Invoke-Expression", RegexOptions.IgnoreCase), 25, "Executes dynamic code (IEX / Invoke-Expression)"),
        (new(@"\.DownloadString\s*\(", RegexOptions.IgnoreCase), 25, "Downloads and runs code (DownloadString)"),
        (new(@"\.DownloadFile\s*\(", RegexOptions.IgnoreCase), 20, "Downloads files (DownloadFile)"),
        (new(@"Net\.WebClient", RegexOptions.IgnoreCase), 15, "Uses Net.WebClient"),
        (new(@"FromBase64String", RegexOptions.IgnoreCase), 15, "Decodes Base64 payloads (FromBase64String)"),
    };

    /// <summary>Windows Script Host tricks in .js/.jse files (browser and Node code never uses these).</summary>
    private static readonly (Regex rx, int score, string text)[] WshJsPatterns =
    {
        (new(@"new\s+ActiveXObject\s*\(", RegexOptions.IgnoreCase), 15, "Creates ActiveX objects (Windows Script Host)"),
        (new(@"ADODB\.Stream", RegexOptions.IgnoreCase), 20, "Writes binary files (ADODB.Stream)"),
        (new(@"Scripting\.FileSystemObject", RegexOptions.IgnoreCase), 10, "Accesses the file system (FileSystemObject)"),
        (new(@"MSXML2\.(?:Server)?XMLHTTP", RegexOptions.IgnoreCase), 15, "Downloads with MSXML2.XMLHTTP"),
    };

    /// <summary>Patterns for every script type.</summary>
    private static readonly (Regex rx, int score, string text)[] ScriptPatterns =
    {
        (new(@"WScript\.Shell", RegexOptions.IgnoreCase), 15, "Uses WScript.Shell to run commands"),
        (new(@"certutil(?:\.exe)?[^\r\n]{0,80}-urlcache", RegexOptions.IgnoreCase), 35, "Abuses certutil to download (-urlcache)"),
        (new(@"bitsadmin(?:\.exe)?[^\r\n]{0,80}/transfer", RegexOptions.IgnoreCase), 30, "Abuses bitsadmin to download (/transfer)"),
        (new(@"mshta(?:\.exe)?\s+[""']?https?:", RegexOptions.IgnoreCase), 35, "Runs remote HTA (mshta http…)"),
        (new(@"regsvr32(?:\.exe)?[^\r\n]{0,80}/i:\s*https?:", RegexOptions.IgnoreCase), 40, "Squiblydoo: regsvr32 /i:http"),
        (new(@"vssadmin(?:\.exe)?[^\r\n]{0,40}delete\s+shadows", RegexOptions.IgnoreCase), 50, "Deletes shadow copies (ransomware behaviour)"),
        (new(@"Set-MpPreference[^\r\n]{0,120}-DisableRealtimeMonitoring", RegexOptions.IgnoreCase), 50, "Tries to disable Windows Defender"),
        (new(@"(?:reg(?:\.exe)?\s+add|New-ItemProperty|Set-ItemProperty|RegWrite)[^\r\n]{0,160}\\CurrentVersion\\Run(?:Once)?\b", RegexOptions.IgnoreCase), 25, "Writes a Run key (starts itself at login)"),
        (new(@"schtasks(?:\.exe)?[^\r\n]{0,40}/create", RegexOptions.IgnoreCase), 20, "Creates a scheduled task (schtasks /create)"),
    };

    /// <summary>Extensions Windows runs directly on double-click (DLLs and libraries don't).</summary>
    private static readonly HashSet<string> RunsOnDoubleClick = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".hta",
        ".lnk", ".msi", ".ps1", ".cpl", ".jar", ".reg", ".application"
    };

    /// <summary>Extensions attackers use to make a program look harmless.</summary>
    private static readonly HashSet<string> DecoyExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".docm", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".txt", ".rtf", ".odt", ".csv",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv",
        ".zip", ".rar", ".7z", ".html", ".htm", ".invoice", ".scan"
    };

    private static readonly Regex ManySpacesBeforeExt = new(@"[\s\u00A0_]{5,}\.[A-Za-z0-9]{1,5}$", RegexOptions.Compiled);
    private static readonly Regex PdfNameEscape = new(@"#([0-9A-Fa-f]{2})", RegexOptions.Compiled);

    public record Analysis(string FileType, RealType RealType, string Sha256, long Size, string Signer);

    public static async Task<Analysis> AnalyzeAsync(string path, ScanResult result, CancellationToken ct)
    {
        var fi = new FileInfo(path);
        var size = fi.Length;
        var ext = fi.Extension;
        var findings = new List<Finding>();

        // 1. File-name tricks
        CheckName(fi.Name, findings);

        // 2. Real type vs extension
        var header = FileTypeDetector.ReadHeader(path, 64);
        var real = FileTypeDetector.Detect(header);
        var mismatch = FileTypeDetector.CheckMismatch(real, ext);
        if (mismatch is { } m) findings.Add(new Finding(m.text, m.score, "Type"));
        var fileType = FileTypeDetector.Describe(real, ext);

        // 3. Hash + entropy in one streaming pass
        var (sha, entropy) = await HashAndEntropyAsync(path, ct);

        // 4. Content checks
        try
        {
            if (real == RealType.Zip && IsOfficeOpenXml(ext))
                CheckOoxmlMacros(path, findings);
            else if (real == RealType.Ole)
                CheckOleMacros(path, size, findings);
            else if (real == RealType.Pdf)
                CheckPdf(path, size, findings);

            bool isScript = FileTypeDetector.ScriptExtensions.Contains(ext);
            if (isScript && real is RealType.Text or RealType.Unknown)
                CheckScript(path, size, findings);

            if (real == RealType.Executable && entropy > 7.2)
                findings.Add(new Finding($"High entropy {entropy:0.00} — packed or encrypted program", 20, "Entropy"));
            // Normal minified JavaScript reaches ~6.1 bits/byte and Base64 text tops out at 6.0, so only text that is
            // denser than that (binary data stuffed into a script) is unusual.
            else if (isScript && entropy > 6.3)
                findings.Add(new Finding($"Very high entropy {entropy:0.00} — script contains packed/binary data", 15, "Entropy"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Content check failed for {Path}", path);
            findings.Add(new Finding("Content could not be fully inspected (file locked or damaged)", 0, "Info"));
        }

        // 5. Signature
        string signer = "";
        if (real == RealType.Executable || FileTypeDetector.SignableExtensions.Contains(ext) && ext != ".ps1")
        {
            var sig = SignatureVerifier.Verify(path);
            switch (sig.Status)
            {
                case SignatureStatus.Valid:
                    signer = sig.Signer ?? "Signed";
                    findings.Add(new Finding($"Digitally signed by {signer}{(sig.IsCatalog ? " (catalog)" : "")}", -10, "Signature"));
                    break;
                case SignatureStatus.Unsigned:
                    signer = "Unsigned";
                    // Unsigned libraries (.dll, node/NuGet packages) are normal; only programs you can launch get points.
                    findings.Add(new Finding("Not digitally signed", RunsOnDoubleClick.Contains(ext) ? 10 : 0, "Signature"));
                    break;
                case SignatureStatus.Invalid:
                    signer = "Invalid signature";
                    findings.Add(new Finding("Signature is invalid or the file was modified after signing: " + sig.Detail, 40, "Signature"));
                    break;
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        void Apply()
        {
            result.Findings.Clear();
            foreach (var f in findings) result.Findings.Add(f);
            result.Size = size;
            result.FileType = fileType;
            result.Sha256 = sha;
            result.Signer = signer;
            result.Recalculate();
        }
        if (dispatcher == null || dispatcher.CheckAccess()) Apply(); else await dispatcher.InvokeAsync(Apply);

        return new Analysis(fileType, real, sha, size, signer);
    }

    private static bool IsOfficeOpenXml(string ext) =>
        ext.ToLowerInvariant() is ".docx" or ".docm" or ".dotm" or ".xlsx" or ".xlsm" or ".xltm" or ".xlsb" or ".pptx" or ".pptm" or ".potm" or ".ppsm";

    /// <summary>File-name checks only (used when the file content can't be read).</summary>
    public static List<Finding> NameFindings(string name)
    {
        var list = new List<Finding>();
        CheckName(name, list);
        return list;
    }

    private static void CheckName(string name, List<Finding> findings)
    {
        if (name.Contains('\u202E'))
            findings.Add(new Finding("Right-to-left override character hides the real extension", 60, "Name"));
        else if (name.IndexOfAny(new[] { '\u202D', '\u2066', '\u2067', '\u2068', '\u200F' }) >= 0)
            findings.Add(new Finding("Hidden text-direction characters in the file name", 25, "Name"));

        // Double extension: only a real decoy type (document/media) followed by something that RUNS when
        // double-clicked counts — e.g. "invoice.pdf.exe". Library names such as "jquery.min.js" or
        // "H.NotifyIcon.Wpf.dll" are normal and must not be flagged.
        var parts = name.Split('.');
        if (parts.Length >= 3)
        {
            var last = "." + parts[^1];
            var prev = "." + parts[^2].Trim();
            if (RunsOnDoubleClick.Contains(last) && DecoyExtensions.Contains(prev))
                findings.Add(new Finding($"Double extension ({prev}{last}) — looks like a document but runs as a program", 45, "Name"));
        }

        if (ManySpacesBeforeExt.IsMatch(name))
            findings.Add(new Finding("Many spaces before the extension (hides the real extension)", 35, "Name"));
    }

    private static void CheckOoxmlMacros(string path, List<Finding> findings)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
            findings.Add(new Finding("Contains VBA macros (vbaProject.bin)", 35, "Macro"));
        if (zip.Entries.Any(e => e.FullName.Contains("/embeddings/", StringComparison.OrdinalIgnoreCase) &&
                                 e.FullName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
            findings.Add(new Finding("Contains embedded OLE objects", 10, "Macro"));
    }

    private static void CheckOleMacros(string path, long size, List<Finding> findings)
    {
        var bytes = ReadUpTo(path, Math.Min(size, MaxContentScan));
        var vba = Encoding.Unicode.GetBytes("_VBA_PROJECT");
        if (bytes.AsSpan().IndexOf(vba) >= 0)
            findings.Add(new Finding("Contains VBA macros (OLE VBA streams)", 35, "Macro"));
    }

    private static void CheckPdf(string path, long size, List<Finding> findings)
    {
        var text = Encoding.Latin1.GetString(ReadUpTo(path, Math.Min(size, MaxContentScan)));
        text = PdfNameEscape.Replace(text, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        bool js = Regex.IsMatch(text, @"/(JavaScript|JS)\b");
        bool open = text.Contains("/OpenAction", StringComparison.Ordinal);
        if (js) findings.Add(new Finding("PDF contains JavaScript (/JavaScript, /JS)", 30, "PDF"));
        if (open) findings.Add(new Finding("PDF runs an action when opened (/OpenAction)", js ? 20 : 5, "PDF"));
        if (text.Contains("/Launch", StringComparison.Ordinal)) findings.Add(new Finding("PDF can launch programs (/Launch)", 40, "PDF"));
        if (text.Contains("/EmbeddedFile", StringComparison.Ordinal)) findings.Add(new Finding("PDF has embedded files (/EmbeddedFile)", 20, "PDF"));
    }

    private static void CheckScript(string path, long size, List<Finding> findings)
    {
        var bytes = ReadUpTo(path, Math.Min(size, 8L * 1024 * 1024));
        string text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        int total = 0;
        var ext = Path.GetExtension(path);
        var isJs = ext.Equals(".js", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jse", StringComparison.OrdinalIgnoreCase);
        foreach (var (rx, score, desc) in ScriptPatterns.Concat(isJs ? WshJsPatterns : ShellOnlyPatterns))
        {
            if (!rx.IsMatch(text)) continue;
            var s = Math.Min(score, Math.Max(0, 80 - total));
            total += s;
            findings.Add(new Finding(desc, s, "Script"));
        }
    }

    private static byte[] ReadUpTo(string path, long count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[count];
        int read = 0, n;
        while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
        return read == buf.Length ? buf : buf[..read];
    }

    /// <summary>SHA-256 of the whole file and Shannon entropy (bits/byte) over the first 32 MB.</summary>
    private static async Task<(string sha, double entropy)> HashAndEntropyAsync(string path, CancellationToken ct)
    {
        var counts = new long[256];
        long counted = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            if (counted < MaxContentScan)
            {
                for (int i = 0; i < n; i++) counts[buf[i]]++;
                counted += n;
            }
        }
        double entropy = 0;
        if (counted > 0)
        {
            foreach (var c in counts)
            {
                if (c == 0) continue;
                var p = (double)c / counted;
                entropy -= p * Math.Log2(p);
            }
        }
        return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), entropy);
    }
}
