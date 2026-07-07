using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class AuraScoreService : BackgroundService
{
    private readonly ILogger<AuraScoreService> _logger;

    public AuraScoreService(ILogger<AuraScoreService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AuraScoreService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RecalculateAura();
                _logger.LogInformation("AuraScoreService recalculated aura");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AuraScoreService failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public static void RecalculateAura()
    {
        using var db = DbHelpers.OpenDb();

        EnsureSchema(db);
        BackfillDailyActivity(db);
        RecalculateScores(db);
    }

    private static void EnsureSchema(SqliteConnection db)
    {
        AddColumnIfMissing(db, "AuthUsers", "aura_score", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "AuthUsers", "aura_active_days", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(db, "AuthUsers", "aura_last_active_date", "TEXT");
        AddColumnIfMissing(db, "AuthUsers", "aura_updated_at", "TEXT");

        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS AuraDailyActivity (
  UserId INTEGER NOT NULL,
  ActivityDate TEXT NOT NULL,
  MessageCount INTEGER NOT NULL DEFAULT 0,
  ApiTouchCount INTEGER NOT NULL DEFAULT 0,
  CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
  UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
  PRIMARY KEY (UserId, ActivityDate)
);

CREATE TABLE IF NOT EXISTS AuraHistory (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  UserId INTEGER NOT NULL,
  OldScore REAL NOT NULL,
  NewScore REAL NOT NULL,
  Reason TEXT NOT NULL,
  CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_aura_daily_user_date
ON AuraDailyActivity(UserId, ActivityDate);

CREATE INDEX IF NOT EXISTS idx_aura_history_user_created
ON AuraHistory(UserId, CreatedAt);
";
        cmd.ExecuteNonQuery();
    }

    private static void AddColumnIfMissing(SqliteConnection db, string table, string column, string definition)
    {
        using var check = db.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";

        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase))
                return;
        }

        using var add = db.CreateCommand();
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        add.ExecuteNonQuery();
    }

    private static void BackfillDailyActivity(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
INSERT INTO AuraDailyActivity (UserId, ActivityDate, MessageCount, ApiTouchCount, CreatedAt, UpdatedAt)
SELECT
  UserId,
  date(CAST(WindowStart AS INTEGER), 'unixepoch') AS ActivityDate,
  SUM(Count) AS MessageCount,
  0 AS ApiTouchCount,
  datetime('now'),
  datetime('now')
FROM RateLimitEvents
WHERE ActionType='message'
GROUP BY UserId, date(CAST(WindowStart AS INTEGER), 'unixepoch')
ON CONFLICT(UserId, ActivityDate) DO UPDATE SET
  MessageCount = MAX(AuraDailyActivity.MessageCount, excluded.MessageCount),
  UpdatedAt = datetime('now');
";
        cmd.ExecuteNonQuery();
    }

    private static void RecalculateScores(SqliteConnection db)
    {
        using var history = db.CreateCommand();
        history.CommandText = @"
WITH base AS (
  SELECT
    au.Id AS UserId,
    au.TrustLevel AS TrustLevel,
    COALESCE(au.IsSuspended, 0) AS IsSuspended,
    COALESCE(au.EmailVerified, 0) AS EmailVerified,
    COALESCE(au.TwoFactorEnabled, 0) AS TwoFactorEnabled,
    COALESCE(au.trust_behavior, 60.0) AS Behavior,
    COALESCE(COUNT(DISTINCT ada.ActivityDate), 0) AS ActiveDays,
    MAX(ada.ActivityDate) AS LastActiveDate,
    MAX(
      MIN(
        julianday('now') - julianday(COALESCE(au.CreatedAt, datetime('now'))),
        180
      ),
      0
    ) AS AccountAgeDays
  FROM AuthUsers au
  LEFT JOIN AuraDailyActivity ada ON ada.UserId = au.Id
  GROUP BY au.Id
),
raw AS (
  SELECT
    UserId,
    TrustLevel,
    IsSuspended,
    ActiveDays,
    LastActiveDate,
    MIN(
      100.0,
      (MIN(ActiveDays, 30) / 30.0) * 45.0 +
      CASE
        WHEN ActiveDays > 0 THEN (MIN(AccountAgeDays, 180) / 180.0) * 10.0
        ELSE 0.0
      END +
      CASE
        WHEN ActiveDays > 0 THEN (Behavior / 100.0) * 20.0
        ELSE 0.0
      END +
      CASE
        WHEN EmailVerified = 1 AND TwoFactorEnabled = 1 THEN 10.0
        WHEN EmailVerified = 1 THEN 6.0
        WHEN TwoFactorEnabled = 1 THEN 4.0
        ELSE 0.0
      END +
      CASE
        WHEN LastActiveDate >= date('now', '-7 days') THEN 15.0
        WHEN LastActiveDate >= date('now', '-30 days') THEN 8.0
        WHEN LastActiveDate >= date('now', '-90 days') THEN 3.0
        ELSE 0.0
      END
    ) AS RawAura
  FROM base
),
final AS (
  SELECT
    UserId,
    ActiveDays,
    LastActiveDate,
    CASE
      WHEN IsSuspended = 1 OR TrustLevel = 'blocked' THEN 0.0
      WHEN ActiveDays = 0 THEN MIN(RawAura, 12.0)
      WHEN TrustLevel = 'low' THEN MIN(RawAura, 25.0)
      WHEN TrustLevel = 'medium' THEN MIN(RawAura, 60.0)
      WHEN ActiveDays = 1 THEN MIN(RawAura, 35.0)
      WHEN ActiveDays BETWEEN 2 AND 4 THEN MIN(RawAura, 50.0)
      ELSE RawAura
    END AS NewAura
  FROM raw
)
INSERT INTO AuraHistory (UserId, OldScore, NewScore, Reason, CreatedAt)
SELECT
  au.Id,
  COALESCE(au.aura_score, 0),
  ROUND(final.NewAura, 1),
  'aura_auto_recalculate',
  datetime('now')
FROM AuthUsers au
JOIN final ON final.UserId = au.Id
WHERE ABS(COALESCE(au.aura_score, 0) - ROUND(final.NewAura, 1)) >= 0.1;
";
        history.ExecuteNonQuery();

        using var update = db.CreateCommand();
        update.CommandText = @"
WITH base AS (
  SELECT
    au.Id AS UserId,
    au.TrustLevel AS TrustLevel,
    COALESCE(au.IsSuspended, 0) AS IsSuspended,
    COALESCE(au.EmailVerified, 0) AS EmailVerified,
    COALESCE(au.TwoFactorEnabled, 0) AS TwoFactorEnabled,
    COALESCE(au.trust_behavior, 60.0) AS Behavior,
    COALESCE(COUNT(DISTINCT ada.ActivityDate), 0) AS ActiveDays,
    MAX(ada.ActivityDate) AS LastActiveDate,
    MAX(
      MIN(
        julianday('now') - julianday(COALESCE(au.CreatedAt, datetime('now'))),
        180
      ),
      0
    ) AS AccountAgeDays
  FROM AuthUsers au
  LEFT JOIN AuraDailyActivity ada ON ada.UserId = au.Id
  GROUP BY au.Id
),
raw AS (
  SELECT
    UserId,
    TrustLevel,
    IsSuspended,
    ActiveDays,
    LastActiveDate,
    MIN(
      100.0,
      (MIN(ActiveDays, 30) / 30.0) * 45.0 +
      CASE
        WHEN ActiveDays > 0 THEN (MIN(AccountAgeDays, 180) / 180.0) * 10.0
        ELSE 0.0
      END +
      CASE
        WHEN ActiveDays > 0 THEN (Behavior / 100.0) * 20.0
        ELSE 0.0
      END +
      CASE
        WHEN EmailVerified = 1 AND TwoFactorEnabled = 1 THEN 10.0
        WHEN EmailVerified = 1 THEN 6.0
        WHEN TwoFactorEnabled = 1 THEN 4.0
        ELSE 0.0
      END +
      CASE
        WHEN LastActiveDate >= date('now', '-7 days') THEN 15.0
        WHEN LastActiveDate >= date('now', '-30 days') THEN 8.0
        WHEN LastActiveDate >= date('now', '-90 days') THEN 3.0
        ELSE 0.0
      END
    ) AS RawAura
  FROM base
),
final AS (
  SELECT
    UserId,
    ActiveDays,
    LastActiveDate,
    CASE
      WHEN IsSuspended = 1 OR TrustLevel = 'blocked' THEN 0.0
      WHEN ActiveDays = 0 THEN MIN(RawAura, 12.0)
      WHEN TrustLevel = 'low' THEN MIN(RawAura, 25.0)
      WHEN TrustLevel = 'medium' THEN MIN(RawAura, 60.0)
      WHEN ActiveDays = 1 THEN MIN(RawAura, 35.0)
      WHEN ActiveDays BETWEEN 2 AND 4 THEN MIN(RawAura, 50.0)
      ELSE RawAura
    END AS NewAura
  FROM raw
)
UPDATE AuthUsers
SET
  aura_score = ROUND((SELECT NewAura FROM final WHERE final.UserId = AuthUsers.Id), 1),
  aura_active_days = COALESCE((SELECT ActiveDays FROM final WHERE final.UserId = AuthUsers.Id), 0),
  aura_last_active_date = (SELECT LastActiveDate FROM final WHERE final.UserId = AuthUsers.Id),
  aura_updated_at = datetime('now')
WHERE Id IN (SELECT UserId FROM final);
";
        update.ExecuteNonQuery();
    }
}
