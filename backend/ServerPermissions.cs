// ═══════════════════════════════════════════════════════════════════════════
// ServerPermissions.cs  —  RunSpace permission system
// Central helper — all permission checks go through here.
// ═══════════════════════════════════════════════════════════════════════════

[Flags]
public enum ServerPerm : long
{
    None           = 0,
    ViewChannels   = 1 << 0,   // See channels and read messages
    SendMessages   = 1 << 1,   // Post messages in text channels
    AttachFiles    = 1 << 2,   // Upload files/images
    ManageMessages = 1 << 3,   // Delete/edit others' messages
    ManageChannels = 1 << 4,   // Create/rename/delete channels
    KickMembers    = 1 << 5,   // Remove members from server
    BanMembers     = 1 << 6,   // Ban/unban members
    ManageRoles    = 1 << 7,   // Create/edit roles below own position
    ManageServer   = 1 << 8,   // Edit server name, icon, description
    ViewAuditLog   = 1 << 9,   // Read audit log
    ManageInvites  = 1 << 10,  // Create and revoke invite links
    All            = ~0L
}

// ── Secure mode definition ───────────────────────────────────────────────────
// When enabled on a server:
//   - Only members with ViewChannels permission can read messages
//   - File uploads require AttachFiles AND verified email (EmailVerified=1)
//   - New members cannot send messages for 10 minutes after joining
//   - Invite links can only be created by ManageInvites holders
//   - All message deletions are logged to audit trail
public record SecureModeConfig(
    bool Enabled,
    bool RequireEmailVerified,   // uploads require verified email
    int  NewMemberCooldownSecs,  // seconds before new member can post (0 = disabled)
    bool LogAllDeletions         // force-log every delete to audit trail
);

public static class Perms
{
    // ── Core check ──────────────────────────────────────────────────────────

    /// Returns true if user has ALL of the required permissions on this server.
    /// Owner always returns true.
    public static bool Has(string serverId, string username, ServerPerm required)
    {
        if (IsOwner(serverId, username)) return true;
        var effective = GetEffective(serverId, username);
        return (effective & required) == required;
    }

    /// Returns true if user has ANY of the required permissions.
    public static bool HasAny(string serverId, string username, ServerPerm required)
    {
        if (IsOwner(serverId, username)) return true;
        var effective = GetEffective(serverId, username);
        return (effective & required) != ServerPerm.None;
    }

    // ── Owner ───────────────────────────────────────────────────────────────

    public static bool IsOwner(string serverId, string username)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM Groups WHERE GroupId=$g AND OwnerUsername=$u";
        c.Parameters.AddWithValue("$g", serverId);
        c.Parameters.AddWithValue("$u", username.ToLowerInvariant());
        return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }

    // ── Membership ──────────────────────────────────────────────────────────

    public static bool IsMember(string serverId, string username)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM GroupMembers WHERE GroupId=$g AND LOWER(Username)=$u";
        c.Parameters.AddWithValue("$g", serverId);
        c.Parameters.AddWithValue("$u", username.ToLowerInvariant());
        return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }

    public static bool IsBanned(string serverId, string username)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT COUNT(*) FROM ServerBans sb
            JOIN AuthUsers au ON sb.UserId = CAST(au.Id AS TEXT)
            WHERE sb.ServerId=$s AND LOWER(au.Username)=$u";
        c.Parameters.AddWithValue("$s", serverId);
        c.Parameters.AddWithValue("$u", username.ToLowerInvariant());
        return Convert.ToInt64(c.ExecuteScalar()) > 0;
    }

    // ── Effective permissions ────────────────────────────────────────────────

    /// Computes the union of all role permissions for a member.
    /// Includes the server's default role permissions.
    public static ServerPerm GetEffective(string serverId, string username)
    {
        if (IsOwner(serverId, username)) return ServerPerm.All;

        using var db = DbHelpers.OpenDb();
        long bits = 0;

        // Permissions from explicitly assigned roles
        using var rc = db.CreateCommand();
        rc.CommandText = @"
            SELECT COALESCE(SUM(sr.Permissions), 0)
            FROM ServerMemberRoles smr
            JOIN ServerRoles sr ON smr.RoleId = sr.RoleId AND smr.ServerId = sr.ServerId
            WHERE smr.ServerId=$s
              AND smr.UserId = (SELECT CAST(Id AS TEXT) FROM AuthUsers WHERE LOWER(Username)=$u LIMIT 1)";
        rc.Parameters.AddWithValue("$s", serverId);
        rc.Parameters.AddWithValue("$u", username.ToLowerInvariant());
        var raw = rc.ExecuteScalar();
        bits |= raw == null || raw == DBNull.Value ? 0 : Convert.ToInt64(raw);

        // Permissions from default roles (everyone gets these)
        using var dc = db.CreateCommand();
        dc.CommandText = @"
            SELECT COALESCE(SUM(Permissions), 0)
            FROM ServerRoles WHERE ServerId=$s AND IsDefault=1";
        dc.Parameters.AddWithValue("$s", serverId);
        var defRaw = dc.ExecuteScalar();
        bits |= defRaw == null || defRaw == DBNull.Value ? 0 : Convert.ToInt64(defRaw);

        return (ServerPerm)bits;
    }

    // ── Role hierarchy ───────────────────────────────────────────────────────

    /// Returns the highest role Position value (lowest number = highest rank)
    /// for a member across all their assigned roles.
    /// Owner returns -1 (above everything).
    public static int GetHighestRolePosition(string serverId, string username)
    {
        if (IsOwner(serverId, username)) return -1;

        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"
            SELECT COALESCE(MIN(sr.Position), 999)
            FROM ServerMemberRoles smr
            JOIN ServerRoles sr ON smr.RoleId = sr.RoleId AND smr.ServerId = sr.ServerId
            WHERE smr.ServerId=$s
              AND smr.UserId = (SELECT CAST(Id AS TEXT) FROM AuthUsers WHERE LOWER(Username)=$u LIMIT 1)";
        c.Parameters.AddWithValue("$s", serverId);
        c.Parameters.AddWithValue("$u", username.ToLowerInvariant());
        var raw = c.ExecuteScalar();
        return raw == null || raw == DBNull.Value ? 999 : Convert.ToInt32(raw);
    }

    /// Returns the Position of a specific role.
    public static int GetRolePosition(string serverId, string roleId)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT Position FROM ServerRoles WHERE ServerId=$s AND RoleId=$r LIMIT 1";
        c.Parameters.AddWithValue("$s", serverId);
        c.Parameters.AddWithValue("$r", roleId);
        var raw = c.ExecuteScalar();
        return raw == null || raw == DBNull.Value ? 999 : Convert.ToInt32(raw);
    }

    /// Returns true if the actor's highest role is strictly above the target's.
    /// Used to prevent: kick/ban/role-modify of peers or superiors.
    public static bool IsHierarchyAbove(string serverId, string actorUsername, string targetUsername)
    {
        if (IsOwner(serverId, actorUsername)) return true;
        if (IsOwner(serverId, targetUsername)) return false;   // can never act on owner
        int actorPos  = GetHighestRolePosition(serverId, actorUsername);
        int targetPos = GetHighestRolePosition(serverId, targetUsername);
        return actorPos < targetPos;  // lower number = higher rank
    }

    /// Returns true if the actor can manage the given roleId
    /// (their highest role must be above the role's position).
    public static bool CanManageRole(string serverId, string actorUsername, string roleId)
    {
        if (IsOwner(serverId, actorUsername)) return true;
        int actorPos = GetHighestRolePosition(serverId, actorUsername);
        int rolePos  = GetRolePosition(serverId, roleId);
        return actorPos < rolePos;
    }

    // ── Standard HTTP result helpers ─────────────────────────────────────────

    public static IResult RequireMember(string serverId, string username)
    {
        if (!IsMember(serverId, username))
            return Results.Json(new { message = "Inte medlem i servern." }, statusCode: 403);
        return Results.Ok(); // caller ignores this value — just checks for non-null
    }

    /// Call like:
    ///   if (Perms.Deny(sid, u, ServerPerm.KickMembers) is { } err) return err;
    public static IResult? Deny(string serverId, string username, ServerPerm required)
    {
        if (!IsMember(serverId, username))
            return Results.Json(new { message = "Inte medlem i servern." }, statusCode: 403);
        if (!Has(serverId, username, required))
            return Results.Json(new { message = $"Saknar behörighet: {required}." }, statusCode: 403);
        return null;
    }

    /// Same as Deny but also checks that actor is above target in hierarchy.
    public static IResult? DenyHierarchy(string serverId, string actor, string target, ServerPerm required)
    {
        if (Deny(serverId, actor, required) is { } err) return err;
        if (IsOwner(serverId, target))
            return Results.Json(new { message = "Kan inte utföra åtgärd på serverägaren." }, statusCode: 403);
        if (!IsHierarchyAbove(serverId, actor, target))
            return Results.Json(new { message = "Din roll är inte tillräckligt hög för denna åtgärd." }, statusCode: 403);
        return null;
    }

    // ── Secure mode ──────────────────────────────────────────────────────────

    public static SecureModeConfig GetSecureMode(string serverId)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT Enabled, RequireEmailVerified, NewMemberCooldownSecs, LogAllDeletions
            FROM ServerSecureMode WHERE ServerId=$s LIMIT 1";
        c.Parameters.AddWithValue("$s", serverId);
        using var r = c.ExecuteReader();
        if (!r.Read())
            return new SecureModeConfig(false, false, 0, false);
        return new SecureModeConfig(
            r.GetInt32(0) == 1,
            !r.IsDBNull(1) && r.GetInt32(1) == 1,
            r.IsDBNull(2) ? 0 : r.GetInt32(2),
            !r.IsDBNull(3) && r.GetInt32(3) == 1);
    }

    /// Checks if a new member is still in the posting cooldown window.
    public static bool IsInNewMemberCooldown(string serverId, string username)
    {
        var cfg = GetSecureMode(serverId);
        if (!cfg.Enabled || cfg.NewMemberCooldownSecs <= 0) return false;

        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT JoinedAt FROM GroupMembers
            WHERE GroupId=$g AND LOWER(Username)=$u LIMIT 1";
        c.Parameters.AddWithValue("$g", serverId);
        c.Parameters.AddWithValue("$u", username.ToLowerInvariant());
        var raw = c.ExecuteScalar() as string;
        if (raw == null) return false;
        if (!DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var joined))
            return false;
        return (DateTime.UtcNow - joined).TotalSeconds < cfg.NewMemberCooldownSecs;
    }

    // ── Permission name ↔ bits ───────────────────────────────────────────────

    public static long ParseBits(IEnumerable<string>? names)
    {
        if (names == null) return 0;
        long bits = 0;
        foreach (var n in names)
            if (Enum.TryParse<ServerPerm>(n, true, out var p))
                bits |= (long)p;
        return bits;
    }

    public static List<string> ToNames(long bits)
    {
        var p = (ServerPerm)bits;
        var list = new List<string>();
        foreach (ServerPerm flag in Enum.GetValues<ServerPerm>())
            if (flag != ServerPerm.None && flag != ServerPerm.All && (p & flag) == flag)
                list.Add(flag.ToString());
        return list;
    }

    public static List<object> AllPermissions() =>
        Enum.GetValues<ServerPerm>()
            .Where(p => p != ServerPerm.None && p != ServerPerm.All)
            .Select(p => (object)new { name = p.ToString(), bit = (long)p })
            .ToList();
}
