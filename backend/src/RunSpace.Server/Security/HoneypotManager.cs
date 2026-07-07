public class HoneypotManager
{
    private static readonly string[] Paths =
    {
        "/admin",
        "/wp-admin",
        "/wp-login.php",
        "/phpmyadmin",
        "/.env",
        "/.git/config",
        "/actuator/health"
    };

    public bool IsHoneypotPath(string path)
    {
        return Paths.Any(knownPath =>
            path.Equals(knownPath, StringComparison.OrdinalIgnoreCase));
    }

    public object GetFakeResponse(string path)
    {
        return new
        {
            status = "ok",
            version = "2.1.4"
        };
    }
}
