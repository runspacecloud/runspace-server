using System.Text.RegularExpressions;

public static class DefensiveInput
{
    private static readonly Regex UsernameRegex =
        new(@"^[a-zA-Z0-9_.\-]{3,32}$", RegexOptions.Compiled);

    private static readonly Regex UuidV4Regex =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-4[0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    private static readonly Regex EmailRegex =
        new(@"^[^\s@]{1,64}@[^\s@]{1,253}\.[^\s@]{2,63}$", RegexOptions.Compiled);

    private static readonly Regex OtpRegex =
        new(@"^[0-9]{6,8}$", RegexOptions.Compiled);

    private static readonly Regex SafeTokenRegex =
        new(@"^[a-zA-Z0-9@._:\-]{1,254}$", RegexOptions.Compiled);


    public static bool IsUsername(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return UsernameRegex.IsMatch(value.Trim());
    }

    public static bool IsUuidV4(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return UuidV4Regex.IsMatch(value.Trim());
    }

    public static string CleanString(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var s = value.Trim().Replace("\0", "");
        s = s.Replace("\r", "\n");

        while (s.Contains("\n\n\n"))
            s = s.Replace("\n\n\n", "\n\n");

        return s.Length > maxLen ? s[..maxLen] : s;
    }

    public static bool IsSafeChatText(string? value, int maxLen = 4000)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > maxLen) return false;
        if (value.Contains('\0')) return false;
        return true;
    }

    public static bool IsSafeOptionalText(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > maxLen) return false;
        if (value.Contains('\0')) return false;
        return true;
    }

    public static bool IsSafeBase64ish(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var s = value.Trim();
        if (s.Length > maxLen) return false;
        if (s.Contains('\0')) return false;

        return Regex.IsMatch(s, @"^[a-zA-Z0-9+/=_\-.:\s]+$");
    }
    public static string SafeFileName(string? value, int maxLen = 180)
    {
        if (string.IsNullOrWhiteSpace(value)) return "file";

        var name = value.Trim()
            .Replace("\0", "")
            .Replace("/", "_")
            .Replace("\\", "_");

        name = System.IO.Path.GetFileName(name);
        name = Regex.Replace(name, @"[^\w\.\-\s\(\)\[\]]", "_");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        name = Regex.Replace(name, @"_+", "_").Trim('.', ' ', '_');

        if (string.IsNullOrWhiteSpace(name)) name = "file";
        if (name.Length > maxLen) name = name[..maxLen];

        return name;
    }

    public static bool IsSupportTicketId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Regex.IsMatch(value.Trim(), @"^RS-[0-9]{5}-[0-9A-Fa-f]{4}$");
    }

    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length > 254) return false;
        if (s.Contains('\0')) return false;
        return EmailRegex.IsMatch(s);
    }

    public static bool IsOtpCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Contains('\0')) return false;
        return OtpRegex.IsMatch(s);
    }

    public static bool IsSafeToken(string? value, int maxLen = 254)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length > maxLen) return false;
        if (s.Contains('\0')) return false;
        return SafeTokenRegex.IsMatch(s);
    }

    public static bool IsSafePasswordInput(string? value, int maxLen = 512)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length > maxLen) return false;
        if (value.Contains('\0')) return false;
        return true;
    }

    public static bool IsSafeSessionId(string? value, int maxLen = 128)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length > maxLen) return false;
        if (s.Contains('\0')) return false;
        return Regex.IsMatch(s, @"^[a-zA-Z0-9._:\-]{1,128}$");
    }

    public static bool IsSafeDeviceId(string? value, int maxLen = 100)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length > maxLen) return false;
        if (s.Contains('\0')) return false;
        return Regex.IsMatch(s, @"^[a-zA-Z0-9._:\-]{1,100}$");
    }

    public static bool TryNormalizeRsaPublicKey(string? value, int maxLen, out string normalizedKey)
    {
        normalizedKey = "";
        if (string.IsNullOrWhiteSpace(value)) return false;

        var raw = value.Trim();
        if (raw.Length > maxLen) return false;
        if (raw.Contains('\0')) return false;

        var b64 = raw
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("\n", "")
            .Replace("\r", "")
            .Replace("\t", "")
            .Replace(" ", "")
            .Trim();

        if (string.IsNullOrWhiteSpace(b64)) return false;
        if (b64.Length > maxLen) return false;
        if (!Regex.IsMatch(b64, @"^[a-zA-Z0-9+/=]+$")) return false;

        try
        {
            var keyBytes = Convert.FromBase64String(b64);
            if (keyBytes.Length < 64 || keyBytes.Length > 4096) return false;

            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(keyBytes, out var bytesRead);
            if (bytesRead <= 0) return false;

            normalizedKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            return normalizedKey.Length > 0 && normalizedKey.Length <= maxLen;
        }
        catch
        {
            normalizedKey = "";
            return false;
        }
    }

    public static bool IsSafePublicId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length < 3 || s.Length > 80) return false;
        return Regex.IsMatch(s, @"^[a-zA-Z0-9_-]+$");
    }

    public static bool IsSafeInviteCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length < 4 || s.Length > 80) return false;
        return Regex.IsMatch(s, @"^[a-zA-Z0-9_-]+$");
    }

    public static bool IsSafeTicketId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length < 3 || s.Length > 100) return false;
        return Regex.IsMatch(s, @"^[a-zA-Z0-9_.:-]+$");
    }

    public static bool IsSafeRouteId(string? value, int maxLen = 80)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length < 1 || s.Length > maxLen) return false;
        return Regex.IsMatch(s, @"^[a-zA-Z0-9_.:-]+$");
    }

    public static bool IsPositiveId(long id)
    {
        return id > 0 && id < long.MaxValue;
    }

    public static string CleanDisplayName(string? value, int maxLen = 80)
    {
        var s = CleanString(value, maxLen);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }

    public static bool IsSafeDisplayName(string? value, int minLen = 1, int maxLen = 80)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = CleanDisplayName(value, maxLen);
        if (s.Length < minLen || s.Length > maxLen) return false;
        if (s.Contains('\0')) return false;
        return Regex.IsMatch(s, @"^[\p{L}\p{N} _.\-()\[\]#:+!?'&]{1,80}$");
    }

    public static bool IsSafeTitle(string? value, int maxLen = 120)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = CleanString(value, maxLen);
        if (s.Length < 1 || s.Length > maxLen) return false;
        if (s.Contains('\0')) return false;
        return true;
    }

    public static bool IsSafeDescription(string? value, int maxLen = 2000)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var s = CleanString(value, maxLen);
        if (s.Length > maxLen) return false;
        if (s.Contains('\0')) return false;
        return true;
    }

    public static bool IsSafeHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Regex.IsMatch(value.Trim(), @"^#[0-9a-fA-F]{6}$");
    }

    public static bool IsSafeHttpUrl(string? value, int maxLen = 500)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        var s = value.Trim();
        if (s.Length > maxLen) return false;
        if (s.Contains('\0') || s.Contains('\\')) return false;

        if (!System.Uri.TryCreate(s, System.UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != System.Uri.UriSchemeHttp && uri.Scheme != System.Uri.UriSchemeHttps)
            return false;

        var host = uri.Host.Trim().ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "::1") return false;
        if (host.EndsWith(".local")) return false;

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip)) return false;

            var b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] == 10) return false;
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
                if (b[0] == 192 && b[1] == 168) return false;
                if (b[0] == 169 && b[1] == 254) return false;
                if (b[0] == 0) return false;
            }
        }

        return true;
    }

    public static bool IsSafeIpLiteral(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = value.Trim();
        if (s.Length > 64) return false;
        return System.Net.IPAddress.TryParse(s, out _);
    }

    public static bool IsSafeMessageText(string? value, int maxLen = 4000)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > maxLen) return false;
        if (value.Contains('\0')) return false;
        return true;
    }

    public static string CleanSlug(string? value, int maxLen = 32)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var s = value.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"\s+", "-");
        s = Regex.Replace(s, @"[^a-z0-9_-]", "-");
        s = Regex.Replace(s, @"-+", "-").Trim('-');

        return s.Length > maxLen ? s[..maxLen] : s;
    }

    public static bool IsSafeSlug(string? value, int minLen = 2, int maxLen = 32)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var s = CleanSlug(value, maxLen);
        if (s.Length < minLen || s.Length > maxLen) return false;
        return Regex.IsMatch(s, @"^[a-z0-9][a-z0-9_-]{0,30}[a-z0-9]$|^[a-z0-9]{2}$");
    }

}
