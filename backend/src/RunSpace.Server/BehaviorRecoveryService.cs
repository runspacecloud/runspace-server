using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

public class BehaviorRecoveryService : BackgroundService
{
    private readonly ILogger<BehaviorRecoveryService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public BehaviorRecoveryService(ILogger<BehaviorRecoveryService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BehaviorRecoveryService started");
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { RunRecovery(); }
            catch (Exception ex) { _logger.LogError(ex, "BehaviorRecoveryService fel"); }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void RunRecovery()
    {
        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();

        cmd.CommandText = @"
            SELECT a.Id, a.Username, a.trust_identity, a.trust_behavior, a.trust_device, a.TrustLevel
            FROM AuthUsers a
            WHERE a.trust_behavior < 98.0
              AND a.IsSuspended = 0
              AND NOT EXISTS (
                  SELECT 1
                  FROM TrustPenalties p
                  WHERE p.UserId = a.Id
                    AND (p.ExpiresAt IS NULL OR p.ExpiresAt > datetime('now'))
              )
            LIMIT 500";

        var toRecover = new System.Collections.Generic.List<(int id, string username, double identity, double behavior, double device, string level)>();

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            toRecover.Add((
                r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? 50.0 : r.GetDouble(2),
                r.IsDBNull(3) ? 60.0 : r.GetDouble(3),
                r.IsDBNull(4) ? 70.0 : r.GetDouble(4),
                r.IsDBNull(5) ? "medium" : r.GetString(5)
            ));
        }
        r.Close();

        int count = 0;

        foreach (var (id, username, identity, behavior, device, level) in toRecover)
        {
            var newBehavior = Math.Min(100.0, behavior + 1.5);
            if (newBehavior - behavior < 0.1) continue;

            var oldDims = new TrustDimensions { Identity = identity, Behavior = behavior, Device = device };
            var newDims = new TrustDimensions { Identity = identity, Behavior = newBehavior, Device = device };

            var oldLevel = TrustEngine.GetLevel(TrustEngine.CompositeScore(oldDims), TrustEngine.ParseLevel(level));
            var newLevel = TrustEngine.GetLevel(TrustEngine.CompositeScore(newDims), oldLevel);
            var newTrustScore = (int)Math.Round(TrustEngine.CompositeScore(newDims));

            using var upd = db.CreateCommand();
            upd.CommandText = @"
                UPDATE AuthUsers
                SET trust_behavior=$b,
                    TrustLevel=$lvl,
                    TrustScore=$score
                WHERE Id=$id";
            upd.Parameters.AddWithValue("$b", newBehavior);
            upd.Parameters.AddWithValue("$lvl", TrustEngine.LevelToString(newLevel));
            upd.Parameters.AddWithValue("$score", newTrustScore);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();

            if (oldLevel != newLevel)
            {
                using var hist = db.CreateCommand();
                hist.CommandText = @"
                    INSERT INTO TrustHistory2 (UserId, Timestamp, ReasonCode, Dimension, OldScore, NewScore, Delta)
                    VALUES ($uid, datetime('now'), 'BEHAVIOR_RECOVERY_LEVELUP', 'behavior', $old, $new, $delta)";
                hist.Parameters.AddWithValue("$uid", id);
                hist.Parameters.AddWithValue("$old", behavior);
                hist.Parameters.AddWithValue("$new", newBehavior);
                hist.Parameters.AddWithValue("$delta", newBehavior - behavior);
                hist.ExecuteNonQuery();

                using var evt = db.CreateCommand();
                evt.CommandText = @"
                    INSERT INTO SecurityEventLog (UserId, Timestamp, EventType, FromState, ToState, Details)
                    VALUES ($uid, datetime('now'), 'LEVEL_UP', $from, $to, 'behavior_recovery')";
                evt.Parameters.AddWithValue("$uid", id);
                evt.Parameters.AddWithValue("$from", TrustEngine.LevelToString(oldLevel));
                evt.Parameters.AddWithValue("$to", TrustEngine.LevelToString(newLevel));
                evt.ExecuteNonQuery();
            }

            count++;
        }

        if (count > 0)
            _logger.LogInformation("BehaviorRecovery: {Count} users recovered", count);
    }
}
