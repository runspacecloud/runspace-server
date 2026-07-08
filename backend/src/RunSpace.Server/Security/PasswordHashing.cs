public static class PasswordHashing
{
    public static string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password + AppConfig.PasswordPepper, workFactor: 12);
    }

    public static bool VerifyPassword(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password + AppConfig.PasswordPepper, hash);
        }
        catch
        {
            return false;
        }
    }

    public static bool VerifyLegacyPassword(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }
    public static bool VerifyPasswordWithPepper(string password, string hash, string pepper)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password + pepper, hash);
        }
        catch
        {
            return false;
        }
    }

    public static bool VerifyDefaultPepperPassword(string password, string hash)
    {
        const string defaultPepper = "RS_DEFAULT_PEPPER_CHANGE_ME_IN_PROD";

        if (AppConfig.PasswordPepper == defaultPepper)
            return false;

        return VerifyPasswordWithPepper(password, hash, defaultPepper);
    }

}
