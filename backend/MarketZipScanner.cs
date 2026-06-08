using System.IO.Compression;
using System.Security.Cryptography;

public static class MarketZipScanner
{
    static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",".dll",".bat",".cmd",".msi",".ps1",".psm1",".psd1",
        ".vbs",".hta",".scr",".pif",".com",".lnk",".jar",".app",
        ".apk",".ipa",".deb",".rpm",".so",".dylib",".sys",".drv"
    };

    static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py",".js",".ts",".mjs",".cjs",".jsx",".tsx",
        ".sh",".bash",".zsh",".fish",
        ".rb",".php",".pl",".lua",".r",".go",".rs",".cs",".java",".kt",".swift",
        ".c",".cpp",".h",".hpp",
        ".html",".css",".scss",".sass",".less",
        ".json",".yaml",".yml",".toml",".ini",".cfg",".conf",".env.example",
        ".md",".txt",".rst",".csv",".xml",".svg",
        ".png",".jpg",".jpeg",".gif",".webp",".ico",
        ".pdf",".zip",
        ".gitignore",".editorconfig",".prettierrc",".eslintrc",
        ""  // filer utan extension (Makefile, Dockerfile etc)
    };

    public static async Task<ScanResult> ScanAsync(IFormFile file)
    {
        var result = new ScanResult();

        // Magic bytes check — ZIP börjar alltid med PK\x03\x04
        using (var stream = file.OpenReadStream())
        {
            var magic = new byte[4];
            var read = await stream.ReadAsync(magic, 0, 4);
            if (read < 4 || magic[0] != 0x50 || magic[1] != 0x4B || magic[2] != 0x03 || magic[3] != 0x04)
            {
                result.IsValidZip = false;
                result.Flags.Add("invalid_magic_bytes");
                return result;
            }
        }

        result.IsValidZip = true;

        // Packa upp temporärt
        var tmpDir = Path.Combine(Path.GetTempPath(), "market_scan_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmpDir);
            using (var stream = file.OpenReadStream())
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 500)
                {
                    result.Flags.Add("too_many_files");
                    return result;
                }

                long totalUncompressed = 0;
                foreach (var entry in zip.Entries)
                {
                    // Path traversal-skydd
                    var entryName = entry.FullName.Replace('\\', '/');
                    if (entryName.Contains("..") || entryName.StartsWith("/"))
                    {
                        result.Flags.Add("path_traversal_attempt");
                        result.BlockedFiles.Add(entryName);
                        continue;
                    }

                    totalUncompressed += entry.Length;
                    if (totalUncompressed > 200 * 1024 * 1024)
                    {
                        result.Flags.Add("zip_bomb_suspected");
                        return result;
                    }

                    if (string.IsNullOrWhiteSpace(entry.Name)) continue; // katalog

                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    result.FileList.Add(entryName);

                    if (Blocked.Contains(ext))
                    {
                        result.BlockedFiles.Add(entryName);
                        result.Flags.Add($"blocked_extension:{ext}");
                        continue;
                    }

                    if (!Allowed.Contains(ext))
                    {
                        result.Flags.Add($"unknown_extension:{ext}");
                    }

                    // Obfuskering-heuristik för JS/TS
                    if ((ext == ".js" || ext == ".ts" || ext == ".mjs") && entry.Length < 500_000)
                    {
                        using var reader = new StreamReader(entry.Open());
                        var content = await reader.ReadToEndAsync();
                        if (IsObfuscated(content))
                        {
                            result.Flags.Add($"possibly_obfuscated:{entryName}");
                        }
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            result.IsValidZip = false;
            result.Flags.Add("corrupt_zip");
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }

        return result;
    }

    static bool IsObfuscated(string content)
    {
        if (content.Length < 100) return false;
        // Lång rad utan whitespace = troligen minifierad/obfuskerad
        var lines = content.Split('\n');
        if (lines.Any(l => l.Length > 2000)) return true;
        // eval( med base64-liknande argument
        if (System.Text.RegularExpressions.Regex.IsMatch(content, @"eval\s*\(\s*[a-zA-Z]*\s*\(")) return true;
        // Hög densitet av hex-escape
        var hexMatches = System.Text.RegularExpressions.Regex.Matches(content, @"\\x[0-9a-fA-F]{2}").Count;
        if (hexMatches > 50) return true;
        return false;
    }
}

public class ScanResult
{
    public bool IsValidZip    { get; set; } = true;
    public List<string> FileList     { get; set; } = new();
    public List<string> BlockedFiles { get; set; } = new();
    public List<string> Flags        { get; set; } = new();
}
