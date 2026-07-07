using System.Security.Cryptography;
using System.Text;

public class PasswordBreachChecker
{
    private static readonly HashSet<string> KnownWeakPasswordHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        HashPrefix("password"),
        HashPrefix("123456"),
        HashPrefix("12345678"),
        HashPrefix("qwerty"),
        HashPrefix("abc123"),
        HashPrefix("monkey"),
        HashPrefix("letmein"),
        HashPrefix("dragon"),
        HashPrefix("trustno1"),
        HashPrefix("baseball"),
        HashPrefix("iloveyou"),
        HashPrefix("master"),
        HashPrefix("sunshine"),
        HashPrefix("shadow"),
        HashPrefix("123123"),
        HashPrefix("superman"),
        HashPrefix("password1"),
        HashPrefix("password123"),
        HashPrefix("admin123"),
        HashPrefix("welcome"),
        HashPrefix("starwars"),
        HashPrefix("passw0rd")
    };

    public bool IsBreached(string password)
    {
        return KnownWeakPasswordHashes.Contains(HashPrefix(password));
    }

    private static string HashPrefix(string password)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(bytes)[..10];
    }
}
