using System.Security.Cryptography;
using System.Text;

public static class AccountKeyHashing
{
    public static string Normalize(string accountKey)
        => (accountKey ?? "").Trim().ToLowerInvariant();

    public static string Hash(string accountKey)
    {
        var normalized = Normalize(accountKey);
        var pepper = AppConfig.PasswordPepper;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(pepper));
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
