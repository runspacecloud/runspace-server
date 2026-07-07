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
        using var getCmd = db.CreateCommand();
        getCmd.CommandText = @"
            SELECT Id, trust_identity, trust_behavior, trust_device, TrustLevel, IsSuspended
            FROM AuthUsers
            WHERE Username=$u
            LIMIT 1";
        getCmd.Parameters.AddWithValue("$u", username);

        int userId = 0;
        double identity = 50.0;
        double oldBehavior = 60.0;
        double device = 70.0;
        string oldLevel = "medium";
        bool suspended = false;

        using (var r = getCmd.ExecuteReader())
        {
            if (!r.Read()) return;

            userId = r.GetInt32(0);
            identity = r.IsDBNull(1) ? 50.0 : r.GetDouble(1);
            oldBehavior = r.IsDBNull(2) ? 60.0 : r.GetDouble(2);
            device = r.IsDBNull(3) ? 70.0 : r.GetDouble(3);
            oldLevel = r.IsDBNull(4) ? "medium" : r.GetString(4);
            suspended = !r.IsDBNull(5) && r.GetInt32(5) == 1;
        }

        if (suspended) return;

        // Dedupe: samma penalty-typ ska inte staplas medan den redan är aktiv.
        using (var dupCmd = db.CreateCommand())
        {
            dupCmd.CommandText = @"
                SELECT 1
                FROM TrustPenalties
                WHERE UserId=$uid
                  AND EventType=$evt
                  AND (ExpiresAt IS NULL OR ExpiresAt > datetime('now'))
                LIMIT 1";
            dupCmd.Parameters.AddWithValue("$uid", userId);
            dupCmd.Parameters.AddWithValue("$evt", reasonCode);

            if (dupCmd.ExecuteScalar() != null)
                return;
        }

        var newBehavior = TrustEngine.ApplyPenalty(oldBehavior, points);

        var prevLevel = TrustEngine.ParseLevel(oldLevel);
        var oldDims = new TrustDimensions { Identity = identity, Behavior = oldBehavior, Device = device };
        var newDims = new TrustDimensions { Identity = identity, Behavior = newBehavior, Device = device };

        var oldTrustLevel = TrustEngine.GetLevel(TrustEngine.CompositeScore(oldDims), prevLevel);
        var newTrustLevel = TrustEngine.GetLevel(TrustEngine.CompositeScore(newDims), oldTrustLevel);
        var newTrustScore = (int)Math.Round(TrustEngine.CompositeScore(newDims));

        using var updCmd = db.CreateCommand();
        updCmd.CommandText = @"
            UPDATE AuthUsers
            SET trust_behavior=$b,
                TrustLevel=$lvl,
                TrustScore=$score
            WHERE Id=$id";
        updCmd.Parameters.AddWithValue("$b", newBehavior);
        updCmd.Parameters.AddWithValue("$lvl", TrustEngine.LevelToString(newTrustLevel));
        updCmd.Parameters.AddWithValue("$score", newTrustScore);
        updCmd.Parameters.AddWithValue("$id", userId);
        updCmd.ExecuteNonQuery();

        using var penCmd = db.CreateCommand();
        penCmd.CommandText = @"
            INSERT INTO TrustPenalties (UserId, EventType, PenaltyPoints, ExpiresAt, CreatedAt)
            VALUES ($uid, $evt, $pts, datetime('now', $exp), datetime('now'))";
        penCmd.Parameters.AddWithValue("$uid", userId);
        penCmd.Parameters.AddWithValue("$evt", reasonCode);
        penCmd.Parameters.AddWithValue("$pts", points);
        penCmd.Parameters.AddWithValue("$exp", $"+{expiresHours} hours");
        penCmd.ExecuteNonQuery();

        using var histCmd = db.CreateCommand();
        histCmd.CommandText = @"
            INSERT INTO TrustHistory2 (UserId, Timestamp, ReasonCode, Dimension, OldScore, NewScore, Delta)
            VALUES ($uid, datetime('now'), $rc, 'behavior', $old, $new, $delta)";
        histCmd.Parameters.AddWithValue("$uid", userId);
        histCmd.Parameters.AddWithValue("$rc", reasonCode);
        histCmd.Parameters.AddWithValue("$old", oldBehavior);
        histCmd.Parameters.AddWithValue("$new", newBehavior);
        histCmd.Parameters.AddWithValue("$delta", newBehavior - oldBehavior);
        histCmd.ExecuteNonQuery();

        if (oldTrustLevel != newTrustLevel)
        {
            using var evtCmd = db.CreateCommand();
            evtCmd.CommandText = @"
                INSERT INTO SecurityEventLog (UserId, Timestamp, EventType, FromState, ToState, Details)
                VALUES ($uid, datetime('now'), 'LEVEL_DROP', $from, $to, $detail)";
            evtCmd.Parameters.AddWithValue("$uid", userId);
            evtCmd.Parameters.AddWithValue("$from", TrustEngine.LevelToString(oldTrustLevel));
            evtCmd.Parameters.AddWithValue("$to", TrustEngine.LevelToString(newTrustLevel));
            evtCmd.Parameters.AddWithValue("$detail", "{\"reason\":\"" + reasonCode + "\",\"points\":" + points + "}");
            evtCmd.ExecuteNonQuery();
        }
    }

    public static void OnDeviceRisk(SqliteConnection db, string username, int riskScore)
    {
        if (riskScore >= 20)
            ApplyPenalty(db, username, "DEVICE_RISK_HIGH", Math.Min(riskScore / 2, 15), expiresHours: 6);
    }

    public static void OnRateLimit(SqliteConnection db, string username)
        => ApplyPenalty(db, username, "RATE_LIMIT_VIOLATION", 5, expiresHours: 24);

    public static void OnSpam(SqliteConnection db, string username, int recipientCount)
        => ApplyPenalty(db, username, "CROSS_USER_SPAM", Math.Min(recipientCount * 3, 25), expiresHours: 48);

    public static void OnContentViolation(SqliteConnection db, string username)
        => ApplyPenalty(db, username, "CONTENT_VIOLATION", 15, expiresHours: 72);
}
