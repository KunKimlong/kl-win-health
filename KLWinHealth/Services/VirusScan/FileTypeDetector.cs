using System.IO;

namespace KLWinHealth.Services.VirusScan;

public enum RealType { Unknown, Executable, Pdf, Zip, Ole, Png, Jpeg, Gif, Bmp, Rar, SevenZip, Gzip, Rtf, Mp3, Mp4, Riff, Text, Iso, Cab, Lnk }

/// <summary>Detects the real file type from magic bytes and checks it against the extension.</summary>
public static class FileTypeDetector
{
    private static readonly HashSet<string> PeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".scr", ".com", ".ocx", ".cpl", ".drv", ".efi", ".mui", ".ax", ".node", ".pyd", ".winmd",
        ".tlb", ".acm", ".tsp", ".fon", ".msstyles", ".rll", ".mun", ".bin", ".dat", ".tmp", ".exe_", ".dl_", ".vxd", ".xll",
        ".wll", ".pif", ".msp", ".api", ".flt", ".ime", ".iec", ".spl", ".rs", ".sfx", ".so", ".old", ".bak", ".manifest"
    };

    public static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".com", ".pif", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".msi",
        ".dll", ".cpl", ".jar", ".lnk", ".reg", ".msc", ".sys", ".ocx", ".application", ".appref-ms", ".iso", ".img", ".vhd"
    };

    public static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1", ".psm1", ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".hta", ".wsf", ".wsh"
    };

    public static readonly HashSet<string> SignableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".sys", ".scr", ".ocx", ".cpl", ".efi", ".msix", ".appx", ".cab", ".cat", ".ps1"
    };

    /// <summary>Extensions where we know what the magic bytes should be — only these are checked for mismatches.</summary>
    private static readonly Dictionary<string, RealType[]> Expected = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new[] { RealType.Pdf },
        [".png"] = new[] { RealType.Png },
        [".jpg"] = new[] { RealType.Jpeg }, [".jpeg"] = new[] { RealType.Jpeg }, [".jfif"] = new[] { RealType.Jpeg },
        [".gif"] = new[] { RealType.Gif },
        [".bmp"] = new[] { RealType.Bmp },
        [".zip"] = new[] { RealType.Zip },
        [".docx"] = new[] { RealType.Zip }, [".xlsx"] = new[] { RealType.Zip }, [".pptx"] = new[] { RealType.Zip },
        [".docm"] = new[] { RealType.Zip }, [".xlsm"] = new[] { RealType.Zip }, [".pptm"] = new[] { RealType.Zip },
        [".doc"] = new[] { RealType.Ole, RealType.Rtf }, [".xls"] = new[] { RealType.Ole }, [".ppt"] = new[] { RealType.Ole },
        [".msi"] = new[] { RealType.Ole },
        [".rar"] = new[] { RealType.Rar },
        [".7z"] = new[] { RealType.SevenZip },
        [".gz"] = new[] { RealType.Gzip },
        [".rtf"] = new[] { RealType.Rtf },
        [".mp3"] = new[] { RealType.Mp3 },
        [".mp4"] = new[] { RealType.Mp4 }, [".m4a"] = new[] { RealType.Mp4 }, [".mov"] = new[] { RealType.Mp4 },
        [".wav"] = new[] { RealType.Riff }, [".avi"] = new[] { RealType.Riff }, [".webp"] = new[] { RealType.Riff },
        [".txt"] = new[] { RealType.Text, RealType.Unknown },
        [".exe"] = new[] { RealType.Executable }, [".dll"] = new[] { RealType.Executable }, [".scr"] = new[] { RealType.Executable },
        [".sys"] = new[] { RealType.Executable },
        [".lnk"] = new[] { RealType.Lnk },
        [".cab"] = new[] { RealType.Cab },
        [".iso"] = new[] { RealType.Iso, RealType.Unknown }
    };

    /// <summary>"Harmless-looking" extensions — an executable behind one of these is a disguise.</summary>
    private static readonly HashSet<string> DocumentLike = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".txt", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".rtf", ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".csv", ".zip", ".rar", ".7z", ".html", ".htm", ".odt", ".svg", ".ico", ".heic"
    };

    public static RealType Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 2 && h[0] == 'M' && h[1] == 'Z') return RealType.Executable;
        if (h.Length >= 4 && h[0] == '%' && h[1] == 'P' && h[2] == 'D' && h[3] == 'F') return RealType.Pdf;
        if (h.Length >= 4 && h[0] == 'P' && h[1] == 'K' && (h[2] == 3 || h[2] == 5 || h[2] == 7)) return RealType.Zip;
        if (h.Length >= 8 && h[0] == 0xD0 && h[1] == 0xCF && h[2] == 0x11 && h[3] == 0xE0 && h[4] == 0xA1 && h[5] == 0xB1 && h[6] == 0x1A && h[7] == 0xE1) return RealType.Ole;
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G') return RealType.Png;
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return RealType.Jpeg;
        if (h.Length >= 4 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8') return RealType.Gif;
        if (h.Length >= 2 && h[0] == 'B' && h[1] == 'M') return RealType.Bmp;
        if (h.Length >= 4 && h[0] == 'R' && h[1] == 'a' && h[2] == 'r' && h[3] == '!') return RealType.Rar;
        if (h.Length >= 6 && h[0] == '7' && h[1] == 'z' && h[2] == 0xBC && h[3] == 0xAF) return RealType.SevenZip;
        if (h.Length >= 2 && h[0] == 0x1F && h[1] == 0x8B) return RealType.Gzip;
        if (h.Length >= 5 && h[0] == '{' && h[1] == '\\' && h[2] == 'r' && h[3] == 't' && h[4] == 'f') return RealType.Rtf;
        if (h.Length >= 3 && ((h[0] == 'I' && h[1] == 'D' && h[2] == '3') || (h[0] == 0xFF && (h[1] & 0xE0) == 0xE0))) return RealType.Mp3;
        if (h.Length >= 8 && h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p') return RealType.Mp4;
        if (h.Length >= 4 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F') return RealType.Riff;
        if (h.Length >= 4 && h[0] == 'M' && h[1] == 'S' && h[2] == 'C' && h[3] == 'F') return RealType.Cab;
        if (h.Length >= 4 && h[0] == 0x4C && h[1] == 0 && h[2] == 0 && h[3] == 0) return RealType.Lnk;
        if (LooksLikeText(h)) return RealType.Text;
        return RealType.Unknown;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> h)
    {
        if (h.Length == 0) return false;
        int printable = 0;
        foreach (var b in h)
            if (b is 9 or 10 or 13 || (b >= 32 && b < 127) || b >= 0x80) printable++;
            else if (b == 0) return false;
        return printable >= h.Length * 0.95;
    }

    public static string Describe(RealType t, string ext) => t switch
    {
        RealType.Executable => "Windows executable",
        RealType.Pdf => "PDF document",
        RealType.Zip => ext.ToLowerInvariant() switch
        {
            ".docx" or ".docm" => "Word document",
            ".xlsx" or ".xlsm" => "Excel workbook",
            ".pptx" or ".pptm" => "PowerPoint",
            ".jar" => "Java archive",
            ".apk" => "Android package",
            _ => "ZIP archive"
        },
        RealType.Ole => ext.ToLowerInvariant() switch
        {
            ".doc" => "Word 97-2003", ".xls" => "Excel 97-2003", ".ppt" => "PowerPoint 97-2003", ".msi" => "Windows Installer",
            _ => "OLE compound file"
        },
        RealType.Png => "PNG image", RealType.Jpeg => "JPEG image", RealType.Gif => "GIF image", RealType.Bmp => "Bitmap image",
        RealType.Rar => "RAR archive", RealType.SevenZip => "7-Zip archive", RealType.Gzip => "GZip archive", RealType.Rtf => "RTF document",
        RealType.Mp3 => "Audio", RealType.Mp4 => "Video", RealType.Riff => "Media (RIFF)", RealType.Cab => "Cabinet archive",
        RealType.Lnk => "Shortcut",
        RealType.Text => ScriptExtensions.Contains(ext) ? "Script" : "Text",
        _ => string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant() + " file"
    };

    /// <summary>Returns a mismatch description (and score) or null.</summary>
    public static (string text, int score)? CheckMismatch(RealType real, string ext)
    {
        if (string.IsNullOrEmpty(ext)) return real == RealType.Executable ? ("Executable file without an extension", 25) : null;

        if (real == RealType.Executable && !PeExtensions.Contains(ext))
        {
            return DocumentLike.Contains(ext)
                ? ($"Executable disguised as {ext} (real type: Windows program)", 60)
                : ($"Windows program with an unusual extension ({ext})", 25);
        }

        if (!Expected.TryGetValue(ext, out var expected) || real == RealType.Unknown) return null;
        if (expected.Contains(real)) return null;
        if (real == RealType.Text && expected.Contains(RealType.Unknown)) return null;
        return ($"Extension {ext} doesn't match the real file type ({Describe(real, ext)})", 15);
    }

    public static byte[] ReadHeader(string path, int count = 64)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var buf = new byte[Math.Min(count, Math.Max(0, (int)Math.Min(fs.Length, count)))];
        var read = fs.Read(buf, 0, buf.Length);
        return buf[..read];
    }
}
