using System;
using Microsoft.Data.Sqlite;

/// <summary>
/// Centraliserad hantering av trust-events och penalties.
/// Alla penalties ska gå via denna service, aldrig skrivas inline.
/// </summary>
public static class TrustEventService
{
    public static void ApplyPenalty(SqliteConnection db, string username, string reasonCode, int points, int expiresHours = 24)
    {
        // 1. Hämta nuvarande behavior och id
        using var getCmd = db.CreateCommand();
        getCmd.CommandText = "SELECT Id, trust_behavior, TrustLevel, IsSuspended FROM AuthUsers WHERE Username=$u LIMIT 1";
        getCmd.Parameters.AddWithValue("$u", username);
        int userId = 0; double oldBehavior = 60; string oldLevel = "medium"; bool suspended = false;
        using (var r = getCmd.ExecuteReader()) {
            if (!r.Read()) return;
            userId = r.GetInt32(0);
            oldBehavior = r.IsDBNull(1) ? 60.0 : r.GetDouble(1);
            oldLevel = r.IsDBNull(2) ? "medium" : r.GetString(2);
            suspended = !r.IsDBNull(3) && r.GetInt32(3) == 1;
        }
        if (suspended) return; // suspended hanteras separat

        // 2. Applicera penalty på behavior-dimension
        var newBehavior = TrustEngine.ApplyPenalty(oldBehavior, points);

        // 3. Uppdatera behavior i DB
        using var updCmd = db.CreateCommand();
        updCmd.CommandText = "UPDATE AuthUsers SET trust_behavior=$b WHERE Id=$id";
        updCmd.Parameters.AddWithValue("$b", newBehavior);
        updCmd.Parameters.AddWithValue("$id", userId);
        updCmd.ExecuteNonQuery();

        // 4. Skriv till TrustPenalties (för bakåtkompatibilitet med admin-sidan)
        using var penCmd = db.CreateCommand();
        penCmd.CommandText = @"INSERT INTO TrustPenalties (UserId, EventType, PenaltyPoints, ExpiresAt, CreatedAt)
            VALUES ($uid, $evt, $pts, datetime('now', $exp), datetime('now'))";
        penCmd.Parameters.AddWithValue("$uid", userId);
        penCmd.Parameters.AddWithValue("$evt", reasonCode);
        penCmd.Parameters.AddWithValue("$pts", points);
        penCmd.Parameters.AddWithValue("$exp", $"+{expiresHours} hours");
        penCmd.ExecuteNonQuery();

        // 5. Logga i TrustHistory2
        using var histCmd = db.CreateCommand();
        histCmd.CommandText = @"INSERT INTO TrustHistory2 (UserId, Timestamp, ReasonCode, Dimension, OldScore, NewScore, Delta)
            VALUES ($uid, datetime('now'), $rc, 'behavior', $old, $new, $delta)";
        histCmd.Parameters.AddWithValue("$uid", userId);
        histCmd.Parameters.AddWithValue("$rc", reasonCode);
        histCmd.Parameters.AddWithValue("$old", oldBehavior);
        histCmd.Parameters.AddWithValue("$new", newBehavior);
        histCmd.Parameters.AddWithValue("$delta", newBehavior - oldBehavior);
        histCmd.ExecuteNonQuery();

        // 6. Kolla om nivan skiftat – logga bara da
        var oldDims = new TrustDimensions { Identity = 55, Behavior = oldBehavior, Device = 70 };
        var newDims = new TrustDimensions { Identity = 55, Behavior = newBehavior, Device = 70 };
        var prevLevel = TrustEngine.ParseLevel(oldLevel);
        var oldTrustLevel = TrustEngine.GetLevel(TrustEngine.CompositeScore(oldDims), prevLevel);
        var newTrustLevel = TrustEngine.GetLevel(TrustEngine.CompositeScore(newDims), oldTrustLevel);

        if (oldTrustLevel != newTrustLevel)
        {
            using var evtCmd = db.CreateCommand();
            evtCmd.CommandText = @"INSERT INTO SecurityEventLog (UserId, Timestamp, EventType, FromState, ToState, Details)
                VALUES ($uid, datetime('now'), 'LEVEL_DROP', $from, $to, $detail)";
            evtCmd.Parameters.AddWithValue("$uid", userId);
            evtCmd.Parameters.AddWithValue("$from", TrustEngine.LevelToString(oldTrustLevel));
            evtCmd.Parameters.AddWithValue("$to", TrustEngine.LevelToString(newTrustLevel));
            evtCmd.Parameters.AddWithValue("$detail", "{\"reason\":\"" + reasonCode + "\",\"points\":" + points + "}");
            evtCmd.ExecuteNonQuery();

            // Uppdatera TrustLevel i AuthUsers
            using var lvlCmd = db.CreateCommand();
            lvlCmd.CommandText = "UPDATE AuthUsers SET TrustLevel=$lvl WHERE Id=$id";
            lvlCmd.Parameters.AddWithValue("$lvl", TrustEngine.LevelToString(newTrustLevel));
            lvlCmd.Parameters.AddWithValue("$id", userId);
            lvlCmd.ExecuteNonQuery();
        }
    }

    // Convenience-metoder per event-typ
    public static void OnDeviceRisk(SqliteConnection db, string username, int riskScore)
    {
        // Device-risk påverkar behavior svagt – direkt penalty bara vid hög risk
        if (riskScore >= 20)
            ApplyPenalty(db, username, "DEVICE_RISK_HIGH", Math.Min(riskScore / 2, 15), expiresHours: 6);
        // Lägre risk: ingen penalty, bara flagga (hanteras av device-dimensionen)
    }

    public static void OnRateLimit(SqliteConnection db, string username)
        => ApplyPenalty(db, username, "RATE_LIMIT_VIOLATION", 5, expiresHours: 24);

    public static void OnSpam(SqliteConnection db, string username, int recipientCount)
        => ApplyPenalty(db, username, "CROSS_USER_SPAM", Math.Min(recipientCount * 3, 25), expiresHours: 48);

    public static void OnContentViolation(SqliteConnection db, string username)
        => ApplyPenalty(db, username, "CONTENT_VIOLATION", 15, expiresHours: 72);
}
