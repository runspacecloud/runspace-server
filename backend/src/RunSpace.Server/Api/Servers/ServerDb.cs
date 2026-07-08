using System.Text.Json;
using Microsoft.Data.Sqlite;

public static class ServerDb
{
    public static void EnsureSchema()
    {
        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ServerRoles (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId    TEXT    NOT NULL,
    RoleId      TEXT    NOT NULL UNIQUE,
    Name        TEXT    NOT NULL,
    Color       TEXT    NOT NULL DEFAULT '#94a3b8',
    Permissions INTEGER NOT NULL DEFAULT 0,
    Position    INTEGER NOT NULL DEFAULT 99,
    IsDefault   INTEGER NOT NULL DEFAULT 0,
    CreatedAt   TEXT    NOT NULL,
    FOREIGN KEY (ServerId) REFERENCES Groups(GroupId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ServerMemberRoles (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId TEXT NOT NULL,
    UserId   TEXT NOT NULL,
    RoleId   TEXT NOT NULL,
    UNIQUE(ServerId, UserId, RoleId),
    FOREIGN KEY (ServerId) REFERENCES Groups(GroupId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ServerInvites (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId  TEXT    NOT NULL,
    Code      TEXT    NOT NULL UNIQUE,
    CreatedBy TEXT    NOT NULL,
    MaxUses   INTEGER NOT NULL DEFAULT 0,
    Uses      INTEGER NOT NULL DEFAULT 0,
    ExpiresAt TEXT,
    CreatedAt TEXT    NOT NULL,
    FOREIGN KEY (ServerId) REFERENCES Groups(GroupId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ServerBans (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId TEXT NOT NULL,
    UserId   TEXT NOT NULL,
    BannedBy TEXT NOT NULL,
    Reason   TEXT NOT NULL DEFAULT '',
    BannedAt TEXT NOT NULL,
    UNIQUE(ServerId, UserId)
);

CREATE TABLE IF NOT EXISTS ServerAuditLog (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    ServerId  TEXT NOT NULL,
    ActorId   TEXT NOT NULL,
    Action    TEXT NOT NULL,
    TargetId  TEXT NOT NULL DEFAULT '',
    Detail    TEXT NOT NULL DEFAULT '',
    CreatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS BurnMessages (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    MessageId   INTEGER NOT NULL UNIQUE,
    BurnAfterAt TEXT    NOT NULL
);

CREATE TABLE IF NOT EXISTS ServerSecureMode (
    ServerId              TEXT    NOT NULL PRIMARY KEY,
    Enabled               INTEGER NOT NULL DEFAULT 0,
    RequireEmailVerified  INTEGER NOT NULL DEFAULT 0,
    NewMemberCooldownSecs INTEGER NOT NULL DEFAULT 0,
    LogAllDeletions       INTEGER NOT NULL DEFAULT 0,
    SetAt                 TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_ServerRoles_Server    ON ServerRoles(ServerId);
CREATE INDEX IF NOT EXISTS IX_MemberRoles_SM        ON ServerMemberRoles(ServerId, UserId);
CREATE INDEX IF NOT EXISTS IX_ServerInvites_Code    ON ServerInvites(Code);
CREATE INDEX IF NOT EXISTS IX_ServerBans_Server     ON ServerBans(ServerId);
CREATE INDEX IF NOT EXISTS IX_AuditLog_Server       ON ServerAuditLog(ServerId, Id);
";
        cmd.ExecuteNonQuery();

        // Safe column migrations on existing tables
        DbHelpers.EnsureColumn(db, "GroupMessages", "Deleted", "INTEGER NOT NULL DEFAULT 0");
        DbHelpers.EnsureColumn(db, "GroupMessages", "EditedAt", "TEXT");
    }

    public static void EnsureDefaultRoles(string serverId, string ownerUsername)
    {
        using var db = DbHelpers.OpenDb();
        var now = DateTime.UtcNow.ToString("o");

        var defaults = new (string RoleId, string Name, string Color, int Position, long Perms, bool IsDefault)[]
        {
            ("owner",     "Owner",     "#eab308", 0,  (long)ServerPerm.All, false),
            ("admin",     "Admin",     "#f97316", 1,
             (long)(ServerPerm.ManageServer | ServerPerm.ManageRoles | ServerPerm.ManageChannels |
                    ServerPerm.KickMembers  | ServerPerm.BanMembers  | ServerPerm.SendMessages  |
                    ServerPerm.AttachFiles  | ServerPerm.ViewChannels| ServerPerm.ManageMessages |
                    ServerPerm.ViewAuditLog | ServerPerm.ManageInvites),
             false),
            ("moderator", "Moderator", "#3b82f6", 2,
             (long)(ServerPerm.KickMembers | ServerPerm.ManageMessages | ServerPerm.SendMessages |
                    ServerPerm.ViewChannels| ServerPerm.ManageInvites),
             false),
            ("member",    "Member",    "#94a3b8", 99,
             (long)(ServerPerm.ViewChannels | ServerPerm.SendMessages | ServerPerm.AttachFiles),
             true),
        };

        foreach (var d in defaults)
        {
            using var c = db.CreateCommand();
            c.CommandText = @"INSERT OR IGNORE INTO ServerRoles
                (ServerId, RoleId, Name, Color, Permissions, Position, IsDefault, CreatedAt)
                VALUES ($s,$rid,$n,$col,$p,$pos,$def,$ts)";
            c.Parameters.AddWithValue("$s", serverId);
            c.Parameters.AddWithValue("$rid", d.RoleId);
            c.Parameters.AddWithValue("$n", d.Name);
            c.Parameters.AddWithValue("$col", d.Color);
            c.Parameters.AddWithValue("$p", d.Perms);
            c.Parameters.AddWithValue("$pos", d.Position);
            c.Parameters.AddWithValue("$def", d.IsDefault ? 1 : 0);
            c.Parameters.AddWithValue("$ts", now);
            c.ExecuteNonQuery();
        }

        // Assign owner role
        using var oc = db.CreateCommand();
        oc.CommandText = @"INSERT OR IGNORE INTO ServerMemberRoles (ServerId, UserId, RoleId)
            SELECT $s, CAST(Id AS TEXT), 'owner'
            FROM AuthUsers WHERE LOWER(Username)=$u";
        oc.Parameters.AddWithValue("$s", serverId);
        oc.Parameters.AddWithValue("$u", ownerUsername.ToLowerInvariant());
        oc.ExecuteNonQuery();
    }

    public static void Audit(string serverId, string actor,
                             string action, string target = "", string detail = "")
    {
        try
        {
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"INSERT INTO ServerAuditLog
                (ServerId, ActorId, Action, TargetId, Detail, CreatedAt)
                VALUES ($s,$a,$act,$t,$d,$ts)";
            c.Parameters.AddWithValue("$s", serverId);
            c.Parameters.AddWithValue("$a", actor);
            c.Parameters.AddWithValue("$act", action);
            c.Parameters.AddWithValue("$t", target);
            c.Parameters.AddWithValue("$d", detail.Length > 500 ? detail[..500] : detail);
            c.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            c.ExecuteNonQuery();
        }
        catch { }
    }

    public static List<object> GetMembers(string serverId)
    {
        var list = new List<object>();
        using var db = DbHelpers.OpenDb();

        using var mc = db.CreateCommand();
        mc.CommandText = @"
            SELECT gm.Username, gm.JoinedAt,
                   au.Id, au.AvatarUrl, au.Status, au.EmailVerified
            FROM GroupMembers gm
            JOIN AuthUsers au ON LOWER(au.Username) = LOWER(gm.Username)
            WHERE gm.GroupId=$g
            ORDER BY gm.JoinedAt";
        mc.Parameters.AddWithValue("$g", serverId);
        using var mr = mc.ExecuteReader();
        var rows = new List<(string Username, string JoinedAt, long UserId,
                             string Avatar, string Status, bool EmailVerified)>();
        while (mr.Read())
            rows.Add((mr.GetString(0), mr.GetString(1), mr.GetInt64(2),
                      mr.IsDBNull(3) ? "" : mr.GetString(3),
                      mr.IsDBNull(4) ? "verified" : mr.GetString(4),
                      !mr.IsDBNull(5) && mr.GetInt32(5) == 1));
        mr.Close();

        foreach (var row in rows)
        {
            // Roles for this member
            using var rc = db.CreateCommand();
            rc.CommandText = @"
                SELECT sr.RoleId, sr.Name, sr.Color, sr.Position, sr.Permissions
                FROM ServerMemberRoles smr
                JOIN ServerRoles sr ON smr.RoleId=sr.RoleId AND smr.ServerId=sr.ServerId
                WHERE smr.ServerId=$s AND smr.UserId=$uid
                ORDER BY sr.Position";
            rc.Parameters.AddWithValue("$s", serverId);
            rc.Parameters.AddWithValue("$uid", row.UserId.ToString());
            using var rr = rc.ExecuteReader();
            var roles = new List<object>();
            while (rr.Read())
                roles.Add(new
                {
                    roleId = rr.GetString(0),
                    name = rr.GetString(1),
                    color = rr.GetString(2),
                    position = rr.GetInt32(3),
                    permissions = rr.GetInt64(4)
                });
            rr.Close();

            // Highest role color for display
            var topRole = roles.Count > 0
                ? (dynamic)roles[0]
                : new { color = "#94a3b8", name = "Member" };

            list.Add(new
            {
                username = row.Username,
                userId = row.UserId.ToString(),
                joinedAt = row.JoinedAt,
                avatarUrl = row.Avatar,
                status = row.Status,
                emailVerified = row.EmailVerified,
                roles,
                displayColor = (string)topRole.color,
                displayRole = (string)topRole.name,
                isOwner = Perms.IsOwner(serverId, row.Username)
            });
        }
        return list;
    }

    public static List<object> GetRoles(string serverId)
    {
        var list = new List<object>();
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT RoleId, Name, Color, Permissions, Position, IsDefault
            FROM ServerRoles WHERE ServerId=$s ORDER BY Position, Id";
        c.Parameters.AddWithValue("$s", serverId);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var bits = r.GetInt64(3);
            list.Add(new
            {
                roleId = r.GetString(0),
                name = r.GetString(1),
                color = r.GetString(2),
                permissions = bits,
                permNames = Perms.ToNames(bits),
                position = r.GetInt32(4),
                isDefault = r.GetInt32(5) == 1
            });
        }
        return list;
    }

    public static List<object> GetBans(string serverId)
    {
        var list = new List<object>();
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT sb.UserId, au.Username, au.AvatarUrl,
            sb.BannedBy, sb.Reason, sb.BannedAt
            FROM ServerBans sb
            JOIN AuthUsers au ON CAST(au.Id AS TEXT)=sb.UserId
            WHERE sb.ServerId=$s ORDER BY sb.BannedAt DESC";
        c.Parameters.AddWithValue("$s", serverId);
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new
            {
                userId = r.GetString(0),
                username = r.GetString(1),
                avatarUrl = r.IsDBNull(2) ? "" : r.GetString(2),
                bannedBy = r.GetString(3),
                reason = r.IsDBNull(4) ? "" : r.GetString(4),
                bannedAt = r.GetString(5)
            });
        return list;
    }

    public static List<object> GetInvites(string serverId)
    {
        var list = new List<object>();
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT Code, CreatedBy, MaxUses, Uses, ExpiresAt, CreatedAt
            FROM ServerInvites WHERE ServerId=$s
            AND (ExpiresAt IS NULL OR ExpiresAt > datetime('now'))
            ORDER BY Id DESC";
        c.Parameters.AddWithValue("$s", serverId);
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new
            {
                code = r.GetString(0),
                createdBy = r.GetString(1),
                maxUses = r.GetInt32(2),
                uses = r.GetInt32(3),
                expiresAt = r.IsDBNull(4) ? "" : r.GetString(4),
                createdAt = r.GetString(5),
                link = $"https://runspace.cloud/invite/{r.GetString(0)}"
            });
        return list;
    }
    public static void EnsureMusicSchema()
    {
        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS MusicPlaylists (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Username  TEXT    NOT NULL,
    Name      TEXT    NOT NULL,
    CreatedAt TEXT    NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_musicplaylists_username ON MusicPlaylists(Username);
CREATE TABLE IF NOT EXISTS MusicTracks (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    PlaylistId INTEGER NOT NULL,
    Type       TEXT    NOT NULL,
    Title      TEXT,
    Artist     TEXT,
    Length     TEXT,
    VideoId    TEXT,
    EmbedUrl   TEXT,
    Thumb      TEXT,
    AddedAt    TEXT    NOT NULL,
    FOREIGN KEY (PlaylistId) REFERENCES MusicPlaylists(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_musictracks_playlist ON MusicTracks(PlaylistId);
CREATE TABLE IF NOT EXISTS MusicRecent (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    Username  TEXT    NOT NULL,
    TrackId   INTEGER NOT NULL,
    Title     TEXT,
    Type      TEXT    NOT NULL,
    Thumb     TEXT,
    VideoId   TEXT,
    PlayedAt  TEXT    NOT NULL,
    UNIQUE(Username, TrackId)
);
CREATE INDEX IF NOT EXISTS idx_musicrecent_username ON MusicRecent(Username);
";
        cmd.ExecuteNonQuery();
    }
}
