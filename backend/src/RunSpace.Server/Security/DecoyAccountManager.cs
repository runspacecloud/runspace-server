public static class DecoyAccountManager
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin",
        "administrator",
        "root",
        "test",
        "testuser",
        "user",
        "demo",
        "guest",
        "superadmin",
        "sysadmin",
        "webadmin"
    };

    public static bool IsDecoy(string username)
    {
        return Names.Contains(username);
    }
}
