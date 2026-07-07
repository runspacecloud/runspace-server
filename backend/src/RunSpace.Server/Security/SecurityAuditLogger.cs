public class SecurityAuditLogger
{
    public void LogAuthEvent(string username, string ip, string eventType, bool success, string details = "")
    {
        try
        {
            using var db = DbHelpers.OpenDb();
            using var cmd = db.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO SecurityAuditLog (EventType, Username, Details, Success, Timestamp)
                VALUES ($t, $u, $d, $s, $ts)";

            cmd.Parameters.AddWithValue("$t", eventType);
            cmd.Parameters.AddWithValue("$u", username ?? "");
            cmd.Parameters.AddWithValue("$d", TrimDetails(details));
            cmd.Parameters.AddWithValue("$s", success ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));

            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }

    public void LogSuspiciousActivity(string ip, string eventType, string details)
    {
        LogAuthEvent("", ip, eventType, false, details);
    }

    private static string TrimDetails(string? details)
    {
        details ??= "";
        return details.Length > 500 ? details[..500] : details;
    }
}
