using System.Text.Json;
// ═══════════════════════════════════════════════════════════════════════════
// ServerSettings.cs  —  Full server settings system for RunSpace
// Handles: general, security, moderation, privacy, storage, community, audit
// Add ServerSettings.Register(app); to ServerApi.Register()
// ═══════════════════════════════════════════════════════════════════════════

// ── Request records ──────────────────────────────────────────────────────────
public record ServerGeneralReq(
    string?  Name,
    string?  Description,
    string?  Category,       // gaming / dev / social / other
    string?  Tags,           // comma-separated
    bool?    IsPublic,
    string?  AccentColor,    // hex color
    string?  WelcomeMessage,
    string?  SystemChannelId,
    string?  DefaultChannelId,
    string?  RulesChannelId
);

public record ServerSecurityReq(
    bool?    InvitesOpenToAll,      // false = only admins can create invites
    string?  DefaultRoleId,         // role assigned on join
    bool?    RequireEmailVerified,
    bool?    Require2FA,
    int?     RateLimitPerMinute,    // messages per user per minute (0 = off)
    int?     MaxMentionsPerMessage, // 0 = off
    bool?    RaidProtectionEnabled, // temporary lockdown
    int?     RaidProtectionMinutes
);

public record ServerModerationReq(
    string?  AutoModLevel,          // off / low / medium / high
    string?  ForbiddenWords,        // newline-separated
    bool?    BlockFlaggedMessages,  // false = log only
    int?     SlowModeSeconds,       // global fallback (0 = off)
    bool?    FilterLinks,
    bool?    FilterInvites
);

public record ServerPrivacyReq(
    bool?    SecureMode,
    bool?    NoMessageLogging,
    int?     AutoDeleteMessagesDays, // 0 = never
    bool?    AllowFileUploads,
    bool?    DisableLinkPreviews,
    bool?    DisableEmbeds
);

public record ServerStorageReq(
    int?     MaxFileSizeMb,         // per upload
    string?  AllowedFileTypes,      // comma-separated extensions, empty = all
    int?     TotalStorageLimitMb,   // 0 = unlimited
    int?     AutoCleanupDays        // delete files after X days (0 = off)
);

public record ServerAuditSettingsReq(
    bool?    AuditLogEnabled,
    bool?    LogMessages,
    bool?    LogJoinsLeaves,
    bool?    LogBans,
    bool?    LogEdits,
    bool?    LogFileUploads,
    int?     RetentionDays          // 0 = forever
);

public record ServerRoleDefaultsReq(
    bool?    UsersCanCreateInvites,
    bool?    UsersCanCreateChannels,
    bool?    UsersCanSendFiles,
    bool?    UsersCanPingEveryone
);

// ── Settings DB schema & helpers ─────────────────────────────────────────────
public static class ServerSettingsDb
{
    public static void EnsureSchema()
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"
CREATE TABLE IF NOT EXISTS ServerSettings (
    ServerId                 TEXT NOT NULL PRIMARY KEY,
    -- General
    Category                 TEXT NOT NULL DEFAULT 'other',
    Tags                     TEXT NOT NULL DEFAULT '',
    IsPublic                 INTEGER NOT NULL DEFAULT 0,
    AccentColor              TEXT NOT NULL DEFAULT '#3b8beb',
    WelcomeMessage           TEXT NOT NULL DEFAULT '',
    SystemChannelId          TEXT NOT NULL DEFAULT '',
    DefaultChannelId         TEXT NOT NULL DEFAULT '',
    RulesChannelId           TEXT NOT NULL DEFAULT '',
    -- Security
    InvitesOpenToAll         INTEGER NOT NULL DEFAULT 1,
    DefaultRoleId            TEXT NOT NULL DEFAULT 'member',
    RequireEmailVerified     INTEGER NOT NULL DEFAULT 0,
    Require2FA               INTEGER NOT NULL DEFAULT 0,
    RateLimitPerMinute       INTEGER NOT NULL DEFAULT 0,
    MaxMentionsPerMessage    INTEGER NOT NULL DEFAULT 0,
    RaidProtectionEnabled    INTEGER NOT NULL DEFAULT 0,
    RaidProtectionUntil      TEXT NOT NULL DEFAULT '',
    -- Moderation
    AutoModLevel             TEXT NOT NULL DEFAULT 'off',
    ForbiddenWords           TEXT NOT NULL DEFAULT '',
    BlockFlaggedMessages     INTEGER NOT NULL DEFAULT 1,
    SlowModeSeconds          INTEGER NOT NULL DEFAULT 0,
    FilterLinks              INTEGER NOT NULL DEFAULT 0,
    FilterInvites            INTEGER NOT NULL DEFAULT 0,
    -- Privacy
    NoMessageLogging         INTEGER NOT NULL DEFAULT 0,
    AutoDeleteMessagesDays   INTEGER NOT NULL DEFAULT 0,
    AllowFileUploads         INTEGER NOT NULL DEFAULT 1,
    DisableLinkPreviews      INTEGER NOT NULL DEFAULT 0,
    DisableEmbeds            INTEGER NOT NULL DEFAULT 0,
    -- Storage
    MaxFileSizeMb            INTEGER NOT NULL DEFAULT 50,
    AllowedFileTypes         TEXT NOT NULL DEFAULT '',
    TotalStorageLimitMb      INTEGER NOT NULL DEFAULT 0,
    AutoCleanupDays          INTEGER NOT NULL DEFAULT 0,
    -- Audit
    AuditLogEnabled          INTEGER NOT NULL DEFAULT 1,
    LogMessages              INTEGER NOT NULL DEFAULT 0,
    LogJoinsLeaves           INTEGER NOT NULL DEFAULT 1,
    LogBans                  INTEGER NOT NULL DEFAULT 1,
    LogEdits                 INTEGER NOT NULL DEFAULT 1,
    LogFileUploads           INTEGER NOT NULL DEFAULT 0,
    RetentionDays            INTEGER NOT NULL DEFAULT 0,
    -- Role defaults
    UsersCanCreateInvites    INTEGER NOT NULL DEFAULT 1,
    UsersCanCreateChannels   INTEGER NOT NULL DEFAULT 0,
    UsersCanSendFiles        INTEGER NOT NULL DEFAULT 1,
    UsersCanPingEveryone     INTEGER NOT NULL DEFAULT 0,
    UpdatedAt                TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS ServerCustomEmojis (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId  TEXT NOT NULL,
    Name      TEXT NOT NULL,
    Url       TEXT NOT NULL,
    AddedBy   TEXT NOT NULL,
    AddedAt   TEXT NOT NULL,
    UNIQUE(ServerId, Name)
);

CREATE TABLE IF NOT EXISTS ServerForbiddenWords (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId TEXT NOT NULL,
    Word     TEXT NOT NULL,
    AddedBy  TEXT NOT NULL,
    AddedAt  TEXT NOT NULL,
    UNIQUE(ServerId, Word)
);
";
        c.ExecuteNonQuery();
    }

    public static void EnsureDefaults(string serverId)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"INSERT OR IGNORE INTO ServerSettings (ServerId, UpdatedAt)
            VALUES ($s, $ts)";
        c.Parameters.AddWithValue("$s",  serverId);
        c.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        c.ExecuteNonQuery();
    }

    public static object? GetAll(string serverId)
    {
        EnsureDefaults(serverId);
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT * FROM ServerSettings WHERE ServerId=$s LIMIT 1";
        c.Parameters.AddWithValue("$s", serverId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;

        // Also get server base info
        using var db2 = DbHelpers.OpenDb();
        using var bc = db2.CreateCommand();
        bc.CommandText = "SELECT Name, Description, OwnerUsername FROM Groups WHERE GroupId=$g LIMIT 1";
        bc.Parameters.AddWithValue("$g", serverId);
        using var br = bc.ExecuteReader();
        string name = "", desc = "", owner = "";
        if (br.Read()) { name = br.GetString(0); desc = br.IsDBNull(1)?"":br.GetString(1); owner = br.GetString(2); }
        br.Close();

        return new
        {
            serverId,
            name, description = desc, owner,
            general = new {
                category        = r["Category"],
                tags            = r["Tags"],
                isPublic        = (long)r["IsPublic"] == 1,
                accentColor     = r["AccentColor"],
                welcomeMessage  = r["WelcomeMessage"],
                systemChannelId = r["SystemChannelId"],
                defaultChannelId= r["DefaultChannelId"],
                rulesChannelId  = r["RulesChannelId"],
            },
            security = new {
                invitesOpenToAll      = (long)r["InvitesOpenToAll"] == 1,
                defaultRoleId         = r["DefaultRoleId"],
                requireEmailVerified  = (long)r["RequireEmailVerified"] == 1,
                require2FA            = (long)r["Require2FA"] == 1,
                rateLimitPerMinute    = (long)r["RateLimitPerMinute"],
                maxMentionsPerMessage = (long)r["MaxMentionsPerMessage"],
                raidProtectionEnabled = (long)r["RaidProtectionEnabled"] == 1,
                raidProtectionUntil   = r["RaidProtectionUntil"],
            },
            moderation = new {
                autoModLevel         = r["AutoModLevel"],
                forbiddenWords       = r["ForbiddenWords"],
                blockFlaggedMessages = (long)r["BlockFlaggedMessages"] == 1,
                slowModeSeconds      = (long)r["SlowModeSeconds"],
                filterLinks          = (long)r["FilterLinks"] == 1,
                filterInvites        = (long)r["FilterInvites"] == 1,
            },
            privacy = new {
                secureMode             = (long)r["NoMessageLogging"] == 1, // maps to secure mode
                noMessageLogging       = (long)r["NoMessageLogging"] == 1,
                autoDeleteMessagesDays = (long)r["AutoDeleteMessagesDays"],
                allowFileUploads       = (long)r["AllowFileUploads"] == 1,
                disableLinkPreviews    = (long)r["DisableLinkPreviews"] == 1,
                disableEmbeds          = (long)r["DisableEmbeds"] == 1,
            },
            storage = new {
                maxFileSizeMb       = (long)r["MaxFileSizeMb"],
                allowedFileTypes    = r["AllowedFileTypes"],
                totalStorageLimitMb = (long)r["TotalStorageLimitMb"],
                autoCleanupDays     = (long)r["AutoCleanupDays"],
            },
            audit = new {
                auditLogEnabled  = (long)r["AuditLogEnabled"] == 1,
                logMessages      = (long)r["LogMessages"] == 1,
                logJoinsLeaves   = (long)r["LogJoinsLeaves"] == 1,
                logBans          = (long)r["LogBans"] == 1,
                logEdits         = (long)r["LogEdits"] == 1,
                logFileUploads   = (long)r["LogFileUploads"] == 1,
                retentionDays    = (long)r["RetentionDays"],
            },
            roleDefaults = new {
                usersCanCreateInvites  = (long)r["UsersCanCreateInvites"] == 1,
                usersCanCreateChannels = (long)r["UsersCanCreateChannels"] == 1,
                usersCanSendFiles      = (long)r["UsersCanSendFiles"] == 1,
                usersCanPingEveryone   = (long)r["UsersCanPingEveryone"] == 1,
            },
            updatedAt = r["UpdatedAt"],
        };
    }

    static void SetCols(string serverId, Dictionary<string, object> cols)
    {
        if (!cols.Any()) return;

        // rs-sql-defensive-v2-allowed-settings-columns
        // Defensive SQL rule: dictionary keys may only become SQL column names
        // if they are from this hardcoded allowlist.
        var allowedColumns = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            "AccentColor",
            "AllowFileUploads",
            "AllowedFileTypes",
            "AuditLogEnabled",
            "AutoCleanupDays",
            "AutoDeleteMessagesDays",
            "AutoModLevel",
            "BlockFlaggedMessages",
            "Category",
            "DefaultChannelId",
            "DefaultRoleId",
            "DisableEmbeds",
            "DisableLinkPreviews",
            "FilterInvites",
            "FilterLinks",
            "ForbiddenWords",
            "InvitesOpenToAll",
            "IsPublic",
            "LogBans",
            "LogEdits",
            "LogFileUploads",
            "LogJoinsLeaves",
            "LogMessages",
            "MaxFileSizeMb",
            "MaxMentionsPerMessage",
            "NoMessageLogging",
            "RaidProtectionEnabled",
            "RaidProtectionUntil",
            "RateLimitPerMinute",
            "Require2FA",
            "RequireEmailVerified",
            "RetentionDays",
            "RulesChannelId",
            "SlowModeSeconds",
            "SystemChannelId",
            "Tags",
            "TotalStorageLimitMb",
            "UsersCanCreateChannels",
            "UsersCanCreateInvites",
            "UsersCanPingEveryone",
            "UsersCanSendFiles",
            "WelcomeMessage"
        };

        foreach (var key in cols.Keys)
        {
            if (!allowedColumns.Contains(key))
                throw new InvalidOperationException("Invalid server settings column.");
        }

        EnsureDefaults(serverId);
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        var sets = cols.Keys.Select((k, i) => $"{k}=${i}").ToList();
        sets.Add("UpdatedAt=$ts");
        c.CommandText = $"UPDATE ServerSettings SET {string.Join(",", sets)} WHERE ServerId=$s";
        int i2 = 0;
        foreach (var kv in cols) { c.Parameters.AddWithValue($"${i2++}", kv.Value); }
        c.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        c.Parameters.AddWithValue("$s", serverId);
        c.ExecuteNonQuery();
    }

    public static void UpdateGeneral(string serverId, ServerGeneralReq r)
    {
        var cols = new Dictionary<string, object>();
        if (r.Category        != null) cols["Category"]         = r.Category;
        if (r.Tags            != null) cols["Tags"]             = r.Tags;
        if (r.IsPublic        != null) cols["IsPublic"]         = r.IsPublic.Value ? 1 : 0;
        if (r.AccentColor     != null) cols["AccentColor"]      = ValidColor(r.AccentColor) ?? "#3b8beb";
        if (r.WelcomeMessage  != null) cols["WelcomeMessage"]   = r.WelcomeMessage[..Math.Min(r.WelcomeMessage.Length, 500)];
        if (r.SystemChannelId != null) cols["SystemChannelId"]  = r.SystemChannelId;
        if (r.DefaultChannelId!= null) cols["DefaultChannelId"] = r.DefaultChannelId;
        if (r.RulesChannelId  != null) cols["RulesChannelId"]   = r.RulesChannelId;
        SetCols(serverId, cols);
    }

    public static void UpdateSecurity(string serverId, ServerSecurityReq r)
    {
        var cols = new Dictionary<string, object>();
        if (r.InvitesOpenToAll     != null) cols["InvitesOpenToAll"]     = r.InvitesOpenToAll.Value ? 1 : 0;
        if (r.DefaultRoleId        != null) cols["DefaultRoleId"]        = r.DefaultRoleId;
        if (r.RequireEmailVerified != null) cols["RequireEmailVerified"]  = r.RequireEmailVerified.Value ? 1 : 0;
        if (r.Require2FA           != null) cols["Require2FA"]           = r.Require2FA.Value ? 1 : 0;
        if (r.RateLimitPerMinute   != null) cols["RateLimitPerMinute"]   = Math.Clamp(r.RateLimitPerMinute.Value, 0, 600);
        if (r.MaxMentionsPerMessage!= null) cols["MaxMentionsPerMessage"]= Math.Clamp(r.MaxMentionsPerMessage.Value, 0, 50);
        if (r.RaidProtectionEnabled!= null)
        {
            cols["RaidProtectionEnabled"] = r.RaidProtectionEnabled.Value ? 1 : 0;
            if (r.RaidProtectionEnabled.Value)
            {
                int mins = Math.Clamp(r.RaidProtectionMinutes ?? 30, 1, 1440);
                cols["RaidProtectionUntil"] = DateTime.UtcNow.AddMinutes(mins).ToString("o");
            }
            else cols["RaidProtectionUntil"] = "";
        }
        SetCols(serverId, cols);
    }

    public static void UpdateModeration(string serverId, ServerModerationReq r)
    {
        var cols = new Dictionary<string, object>();
        var validLevels = new[] { "off", "low", "medium", "high" };
        if (r.AutoModLevel        != null) cols["AutoModLevel"]        = validLevels.Contains(r.AutoModLevel) ? r.AutoModLevel : "off";
        if (r.ForbiddenWords      != null) cols["ForbiddenWords"]      = r.ForbiddenWords[..Math.Min(r.ForbiddenWords.Length, 5000)];
        if (r.BlockFlaggedMessages!= null) cols["BlockFlaggedMessages"]= r.BlockFlaggedMessages.Value ? 1 : 0;
        if (r.SlowModeSeconds     != null) cols["SlowModeSeconds"]     = Math.Clamp(r.SlowModeSeconds.Value, 0, 21600);
        if (r.FilterLinks         != null) cols["FilterLinks"]         = r.FilterLinks.Value ? 1 : 0;
        if (r.FilterInvites       != null) cols["FilterInvites"]       = r.FilterInvites.Value ? 1 : 0;
        SetCols(serverId, cols);
    }

    public static void UpdatePrivacy(string serverId, ServerPrivacyReq r)
    {
        var cols = new Dictionary<string, object>();
        if (r.NoMessageLogging      != null) cols["NoMessageLogging"]      = r.NoMessageLogging.Value ? 1 : 0;
        if (r.SecureMode            != null) cols["NoMessageLogging"]      = r.SecureMode.Value ? 1 : 0; // alias
        if (r.AutoDeleteMessagesDays!= null) cols["AutoDeleteMessagesDays"]= Math.Clamp(r.AutoDeleteMessagesDays.Value, 0, 365);
        if (r.AllowFileUploads      != null) cols["AllowFileUploads"]      = r.AllowFileUploads.Value ? 1 : 0;
        if (r.DisableLinkPreviews   != null) cols["DisableLinkPreviews"]   = r.DisableLinkPreviews.Value ? 1 : 0;
        if (r.DisableEmbeds         != null) cols["DisableEmbeds"]         = r.DisableEmbeds.Value ? 1 : 0;
        SetCols(serverId, cols);
    }

    public static void UpdateStorage(string serverId, ServerStorageReq r)
    {
        var cols = new Dictionary<string, object>();
        if (r.MaxFileSizeMb      != null) cols["MaxFileSizeMb"]      = Math.Clamp(r.MaxFileSizeMb.Value, 1, 500);
        if (r.AllowedFileTypes   != null) cols["AllowedFileTypes"]   = r.AllowedFileTypes[..Math.Min(r.AllowedFileTypes.Length, 500)];
        if (r.TotalStorageLimitMb!= null) cols["TotalStorageLimitMb"]= Math.Clamp(r.TotalStorageLimitMb.Value, 0, 100000);
        if (r.AutoCleanupDays    != null) cols["AutoCleanupDays"]    = Math.Clamp(r.AutoCleanupDays.Value, 0, 365);
        SetCols(serverId, cols);
    }

    public static void UpdateAudit(string serverId, ServerAuditSettingsReq r)
    {
        var cols = new Dictionary<string, object>();
        if (r.AuditLogEnabled != null) cols["AuditLogEnabled"]  = r.AuditLogEnabled.Value ? 1 : 0;
        if (r.LogMessages     != null) cols["LogMessages"]       = r.LogMessages.Value ? 1 : 0;
        if (r.LogJoinsLeaves  != null) cols["LogJoinsLeaves"]    = r.LogJoinsLeaves.Value ? 1 : 0;
        if (r.LogBans         != null) cols["LogBans"]           = r.LogBans.Value ? 1 : 0;
        if (r.LogEdits        != null) cols["LogEdits"]          = r.LogEdits.Value ? 1 : 0;
        if (r.LogFileUploads  != null) cols["LogFileUploads"]    = r.LogFileUploads.Value ? 1 : 0;
        if (r.RetentionDays   != null) cols["RetentionDays"]     = Math.Clamp(r.RetentionDays.Value, 0, 365);
        SetCols(serverId, cols);
    }

    public static void UpdateRoleDefaults(string serverId, ServerRoleDefaultsReq r)
    {
        var cols = new Dictionary<string, object>();
        if (r.UsersCanCreateInvites  != null) cols["UsersCanCreateInvites"]  = r.UsersCanCreateInvites.Value ? 1 : 0;
        if (r.UsersCanCreateChannels != null) cols["UsersCanCreateChannels"] = r.UsersCanCreateChannels.Value ? 1 : 0;
        if (r.UsersCanSendFiles      != null) cols["UsersCanSendFiles"]      = r.UsersCanSendFiles.Value ? 1 : 0;
        if (r.UsersCanPingEveryone   != null) cols["UsersCanPingEveryone"]   = r.UsersCanPingEveryone.Value ? 1 : 0;
        SetCols(serverId, cols);
    }

    static string? ValidColor(string? c) =>
        c != null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim(), @"^#[0-9a-fA-F]{6}$")
            ? c.Trim() : null;

    // ── Moderation helpers (called from message posting) ───────────────────
    public static (bool Block, string Reason) CheckMessage(string serverId, string content, int mentionCount)
    {
        EnsureDefaults(serverId);
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT AutoModLevel, ForbiddenWords, BlockFlaggedMessages,
            MaxMentionsPerMessage, FilterLinks, FilterInvites, RaidProtectionEnabled, RaidProtectionUntil
            FROM ServerSettings WHERE ServerId=$s LIMIT 1";
        c.Parameters.AddWithValue("$s", serverId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return (false, "");

        // Raid protection
        var raidUntil = r["RaidProtectionUntil"] as string ?? "";
        if ((long)r["RaidProtectionEnabled"] == 1 && !string.IsNullOrEmpty(raidUntil))
        {
            if (DateTime.TryParse(raidUntil, out var until) && DateTime.UtcNow < until)
                return (true, "Servern är i raid-skyddsläge. Inga meddelanden tillåts.");
        }

        // Mention limit
        int maxMentions = (int)(long)r["MaxMentionsPerMessage"];
        if (maxMentions > 0 && mentionCount > maxMentions)
            return (true, $"För många omnämnanden (max {maxMentions}).");

        // Forbidden words
        var words = (r["ForbiddenWords"] as string ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lower = content.ToLowerInvariant();
        foreach (var word in words)
        {
            if (!string.IsNullOrWhiteSpace(word) && lower.Contains(word.Trim().ToLowerInvariant()))
            {
                bool block = (long)r["BlockFlaggedMessages"] == 1;
                return (block, $"Meddelandet innehåller ett förbjudet ord.");
            }
        }

        // Filter invite links
        if ((long)r["FilterInvites"] == 1 &&
            (lower.Contains("discord.gg/") || lower.Contains("runspace.cloud/invite/")))
            return ((long)r["BlockFlaggedMessages"] == 1, "Inbjudningslänkar är inte tillåtna.");

        // Link filter (basic)
        if ((long)r["FilterLinks"] == 1 &&
            System.Text.RegularExpressions.Regex.IsMatch(lower, @"https?://"))
            return ((long)r["BlockFlaggedMessages"] == 1, "Länkar är inte tillåtna på denna server.");

        return (false, "");
    }

    public static int GetSlowModeSeconds(string serverId)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT SlowModeSeconds FROM ServerSettings WHERE ServerId=$s LIMIT 1";
        c.Parameters.AddWithValue("$s", serverId);
        var raw = c.ExecuteScalar();
        return raw == null || raw == DBNull.Value ? 0 : Convert.ToInt32(raw);
    }
}

// ── Endpoints ─────────────────────────────────────────────────────────────────
public static class ServerSettings
{
    public static void Register(WebApplication app)
    {
        ServerSettingsDb.EnsureSchema();

        // GET /api/servers/{sid}/settings  — full settings object
        app.MapGet("/api/servers/{sid}/settings", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            ServerSettingsDb.EnsureDefaults(sid);
            var settings = ServerSettingsDb.GetAll(sid);
            return settings is null ? Results.NotFound() : Results.Ok(settings);
        });

        // PATCH /api/servers/{sid}/settings/general
        app.MapPatch("/api/servers/{sid}/settings/general", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<ServerGeneralReq>();
            if (req is null) return Results.BadRequest();

            // Update name/description in Groups table
            if (!string.IsNullOrWhiteSpace(req.Name) || req.Description != null)
            {
                using var db = DbHelpers.OpenDb();
                if (!string.IsNullOrWhiteSpace(req.Name))
                {
                    using var c = db.CreateCommand();
                    c.CommandText = "UPDATE Groups SET Name=$n WHERE GroupId=$g";
                    c.Parameters.AddWithValue("$n", InputSanitizer.SanitizeInput(req.Name.Trim(), 32));
                    c.Parameters.AddWithValue("$g", sid);
                    c.ExecuteNonQuery();
                }
                if (req.Description != null)
                {
                    using var c = db.CreateCommand();
                    c.CommandText = "UPDATE Groups SET Description=$d WHERE GroupId=$g";
                    c.Parameters.AddWithValue("$d", InputSanitizer.SanitizeInput(req.Description, 500));
                    c.Parameters.AddWithValue("$g", sid);
                    c.ExecuteNonQuery();
                }
            }
            ServerSettingsDb.UpdateGeneral(sid, req);
            ServerDb.Audit(sid, u, "settings_general_updated");
            return Results.Ok(new { success = true });
        });

        // PATCH /api/servers/{sid}/settings/security
        app.MapPatch("/api/servers/{sid}/settings/security", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<ServerSecurityReq>();
            if (req is null) return Results.BadRequest();
            ServerSettingsDb.UpdateSecurity(sid, req);
            ServerDb.Audit(sid, u, "settings_security_updated",
                detail: $"raidProtection={req.RaidProtectionEnabled} require2FA={req.Require2FA}");
            return Results.Ok(new { success = true });
        });

        // PATCH /api/servers/{sid}/settings/moderation
        app.MapPatch("/api/servers/{sid}/settings/moderation", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<ServerModerationReq>();
            if (req is null) return Results.BadRequest();
            ServerSettingsDb.UpdateModeration(sid, req);
            ServerDb.Audit(sid, u, "settings_moderation_updated",
                detail: $"autoMod={req.AutoModLevel} slowMode={req.SlowModeSeconds}");
            return Results.Ok(new { success = true });
        });

        // PATCH /api/servers/{sid}/settings/privacy
        app.MapPatch("/api/servers/{sid}/settings/privacy", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<ServerPrivacyReq>();
            if (req is null) return Results.BadRequest();
            ServerSettingsDb.UpdatePrivacy(sid, req);
            ServerDb.Audit(sid, u, "settings_privacy_updated",
                detail: $"secureMode={req.SecureMode} autoDelete={req.AutoDeleteMessagesDays}d");
            return Results.Ok(new { success = true });
        });

        // PATCH /api/servers/{sid}/settings/storage
        app.MapPatch("/api/servers/{sid}/settings/storage", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<ServerStorageReq>();
            if (req is null) return Results.BadRequest();
            ServerSettingsDb.UpdateStorage(sid, req);
            ServerDb.Audit(sid, u, "settings_storage_updated");
            return Results.Ok(new { success = true });
        });

        // PATCH /api/servers/{sid}/settings/audit
        app.MapPatch("/api/servers/{sid}/settings/audit", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<ServerAuditSettingsReq>();
            if (req is null) return Results.BadRequest();
            ServerSettingsDb.UpdateAudit(sid, req);
            ServerDb.Audit(sid, u, "settings_audit_updated");
            return Results.Ok(new { success = true });
        });

        // PATCH /api/servers/{sid}/settings/role-defaults
        app.MapPatch("/api/servers/{sid}/settings/role-defaults", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<ServerRoleDefaultsReq>();
            if (req is null) return Results.BadRequest();
            ServerSettingsDb.UpdateRoleDefaults(sid, req);
            ServerDb.Audit(sid, u, "settings_role_defaults_updated");
            return Results.Ok(new { success = true });
        });

        // POST /api/servers/{sid}/settings/raid-protection  — quick toggle
        app.MapPost("/api/servers/{sid}/settings/raid-protection", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
            bool enable = req?.ContainsKey("enable") == true && req["enable"].GetBoolean();
            int mins = req?.ContainsKey("minutes") == true ? req["minutes"].GetInt32() : 30;
            ServerSettingsDb.UpdateSecurity(sid, new ServerSecurityReq(
                null, null, null, null, null, null,
                RaidProtectionEnabled: enable,
                RaidProtectionMinutes: mins));
            ServerDb.Audit(sid, u, enable ? "raid_protection_on" : "raid_protection_off",
                detail: enable ? $"duration={mins}min" : "");
            return Results.Ok(new { success = true, enabled = enable,
                until = enable ? DateTime.UtcNow.AddMinutes(mins).ToString("o") : "" });
        });

        // GET /api/servers/{sid}/stats  — online count, message count, storage used
        app.MapGet("/api/servers/{sid}/stats", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (!Perms.IsMember(sid, u)) return Results.Forbid();
            using var db = DbHelpers.OpenDb();

            long memberCount = Count(db, "SELECT COUNT(*) FROM GroupMembers WHERE GroupId=$g", sid);
            long msgCount    = Count(db, "SELECT COUNT(*) FROM GroupMessages WHERE GroupId=$g AND Deleted=0", sid);
            long channelCount= Count(db, "SELECT COUNT(*) FROM GroupChannels WHERE GroupId=$g", sid);
            long banCount    = Count(db, "SELECT COUNT(*) FROM ServerBans WHERE ServerId=$g", sid);
            long roleCount   = Count(db, "SELECT COUNT(*) FROM ServerRoles WHERE ServerId=$g", sid);

            return Results.Ok(new {
                memberCount, msgCount, channelCount, banCount, roleCount,
                generatedAt = DateTime.UtcNow.ToString("o")
            });
        });
    }

    static long Count(Microsoft.Data.Sqlite.SqliteConnection db, string sql, string sid)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$g", sid);
        return Convert.ToInt64(c.ExecuteScalar());
    }

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}
