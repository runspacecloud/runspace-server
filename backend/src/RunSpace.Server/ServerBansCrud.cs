// ═══════════════════════════════════════════════════════════════════════════
// ServerBans.cs  —  Ban management endpoints
// ═══════════════════════════════════════════════════════════════════════════
public record BanReq(string? Reason);

public static class ServerBans
{
    public static void Register(WebApplication app)
    {
        // GET /api/servers/{sid}/bans
        app.MapGet("/api/servers/{sid}/bans", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.BanMembers) is { } err) return err;
            return Results.Ok(ServerDb.GetBans(sid));
        });

        // POST /api/servers/{sid}/ban/{target}
        app.MapPost("/api/servers/{sid}/ban/{target}", async (string sid, string target, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            target = target.ToLowerInvariant();
            // Hierarchy check: cannot ban owner, cannot ban equal/higher rank
            if (Perms.DenyHierarchy(sid, u, target, ServerPerm.BanMembers) is { } err) return err;

            var req = await ctx.Request.ReadFromJsonAsync<BanReq>();
            var rawReason = req?.Reason ?? "";

            if (!DefensiveInput.IsSafeDescription(rawReason, 200))
                return Results.BadRequest(new { message = "Ogiltig ban reason." });

            var reason = InputSanitizer.SanitizeInput(rawReason, 200);

            using var db = DbHelpers.OpenDb();
            // Kick first (remove from server)
            using var kc = db.CreateCommand();
            kc.CommandText = "DELETE FROM GroupMembers WHERE GroupId=$g AND LOWER(Username)=$t";
            kc.Parameters.AddWithValue("$g", sid);
            kc.Parameters.AddWithValue("$t", target);
            kc.ExecuteNonQuery();

            // Remove roles
            using var rc = db.CreateCommand();
            rc.CommandText = @"DELETE FROM ServerMemberRoles WHERE ServerId=$s
                AND UserId=(SELECT CAST(Id AS TEXT) FROM AuthUsers WHERE LOWER(Username)=$t LIMIT 1)";
            rc.Parameters.AddWithValue("$s", sid);
            rc.Parameters.AddWithValue("$t", target);
            rc.ExecuteNonQuery();

            // Insert ban record
            using var bc = db.CreateCommand();
            bc.CommandText = @"INSERT OR REPLACE INTO ServerBans (ServerId, UserId, BannedBy, Reason, BannedAt)
                SELECT $s, CAST(Id AS TEXT), $by, $r, $ts
                FROM AuthUsers WHERE LOWER(Username)=$t LIMIT 1";
            bc.Parameters.AddWithValue("$s", sid);
            bc.Parameters.AddWithValue("$by", u);
            bc.Parameters.AddWithValue("$r", reason);
            bc.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            bc.Parameters.AddWithValue("$t", target);
            bc.ExecuteNonQuery();

            ServerDb.Audit(sid, u, "member_banned", target, reason);
            return Results.Ok(new { success = true });
        });

        // DELETE /api/servers/{sid}/ban/{target}  — unban
        app.MapDelete("/api/servers/{sid}/ban/{target}", (string sid, string target, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.BanMembers) is { } err) return err;
            target = target.ToLowerInvariant();

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"DELETE FROM ServerBans WHERE ServerId=$s
                AND UserId=(SELECT CAST(Id AS TEXT) FROM AuthUsers WHERE LOWER(Username)=$t LIMIT 1)";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$t", target);
            c.ExecuteNonQuery();

            ServerDb.Audit(sid, u, "member_unbanned", target);
            return Results.Ok(new { success = true });
        });
    }

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}


// ═══════════════════════════════════════════════════════════════════════════
// ServerCrud.cs  —  Server GET/PUT/DELETE + audit log + secure mode
// ═══════════════════════════════════════════════════════════════════════════
public record UpdateServerReq(string? Name, string? Description);
public record SecureModeReq(bool Enabled, bool RequireEmailVerified,
                            int NewMemberCooldownSecs, bool LogAllDeletions);

public static class ServerCrud
{
    public static void Register(WebApplication app)
    {
        // GET /api/servers/{sid}  — full server payload
        app.MapGet("/api/servers/{sid}", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (!Perms.IsMember(sid, u))
                return Results.Json(new { message = "Inte medlem i servern." }, statusCode: 403);

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"SELECT GroupId, Name, Description, OwnerUsername, CreatedAt
                FROM Groups WHERE GroupId=$g LIMIT 1";
            c.Parameters.AddWithValue("$g", sid);
            using var r = c.ExecuteReader();
            if (!r.Read()) return Results.NotFound(new { message = "Server ej hittad." });

            var effectivePerms = Perms.GetEffective(sid, u);
            var sm = Perms.GetSecureMode(sid);

            return Results.Ok(new
            {
                groupId = r.GetString(0),
                name = r.GetString(1),
                description = r.IsDBNull(2) ? "" : r.GetString(2),
                owner = r.GetString(3),
                createdAt = r.GetString(4),
                isOwner = Perms.IsOwner(sid, u),
                permissions = (long)effectivePerms,
                permNames = Perms.ToNames((long)effectivePerms),
                secureMode = sm,
                members = ServerDb.GetMembers(sid),
                roles = ServerDb.GetRoles(sid),
                channels = ServerChannels.GetChannels(sid),
                invites = Perms.Has(sid, u, ServerPerm.ManageInvites)
                              ? ServerDb.GetInvites(sid) : null
            });
        });

        // PUT /api/servers/{sid}
        app.MapPut("/api/servers/{sid}", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;

            var req = await ctx.Request.ReadFromJsonAsync<UpdateServerReq>();
            if (req == null) return Results.BadRequest(new { message = "Body saknas." });

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
            ServerDb.Audit(sid, u, "server_updated");
            return Results.Ok(new { success = true });
        });

        // DELETE /api/servers/{sid}  — only owner
        app.MapDelete("/api/servers/{sid}", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (!Perms.IsOwner(sid, u))
                return Results.Json(new { message = "Bara ägaren kan ta bort servern." }, statusCode: 403);

            using var db = DbHelpers.OpenDb();
            // Delete in dependency order
            foreach (var sql in new[]
            {
                "DELETE FROM BurnMessages       WHERE MessageId IN (SELECT Id FROM GroupMessages WHERE GroupId=$g)",
                "DELETE FROM ServerAuditLog     WHERE ServerId=$g",
                "DELETE FROM ServerBans         WHERE ServerId=$g",
                "DELETE FROM ServerInvites      WHERE ServerId=$g",
                "DELETE FROM ServerMemberRoles  WHERE ServerId=$g",
                "DELETE FROM ServerRoles        WHERE ServerId=$g",
                "DELETE FROM ServerSecureMode   WHERE ServerId=$g",
                "DELETE FROM GroupMessages      WHERE GroupId=$g",
                "DELETE FROM GroupChannels      WHERE GroupId=$g",
                "DELETE FROM GroupInvites       WHERE GroupId=$g",
                "DELETE FROM GroupMembers       WHERE GroupId=$g",
                "DELETE FROM Groups             WHERE GroupId=$g",
            })
            {
                using var c = db.CreateCommand();
                c.CommandText = sql;
                c.Parameters.AddWithValue("$g", sid);
                c.ExecuteNonQuery();
            }
            return Results.Ok(new { success = true });
        });

        // GET /api/servers/{sid}/audit-log?limit=50
        app.MapGet("/api/servers/{sid}/audit-log", (string sid, int? limit, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ViewAuditLog) is { } err) return err;

            int take = Math.Clamp(limit ?? 50, 1, 200);
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"SELECT ActorId, Action, TargetId, Detail, CreatedAt
                FROM ServerAuditLog WHERE ServerId=$s ORDER BY Id DESC LIMIT $lim";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$lim", take);
            var list = new List<object>();
            using var r = c.ExecuteReader();
            while (r.Read())
                list.Add(new
                {
                    actor = r.GetString(0),
                    action = r.GetString(1),
                    target = r.IsDBNull(2) ? "" : r.GetString(2),
                    detail = r.IsDBNull(3) ? "" : r.GetString(3),
                    at = r.GetString(4)
                });
            return Results.Ok(list);
        });

        // POST /api/servers/{sid}/secure-mode
        app.MapPost("/api/servers/{sid}/secure-mode", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageServer) is { } err) return err;

            var req = await ctx.Request.ReadFromJsonAsync<SecureModeReq>();
            if (req == null) return Results.BadRequest(new { message = "Body saknas." });

            int cooldown = Math.Clamp(req.NewMemberCooldownSecs, 0, 3600);

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"INSERT OR REPLACE INTO ServerSecureMode
                (ServerId, Enabled, RequireEmailVerified, NewMemberCooldownSecs, LogAllDeletions, SetAt)
                VALUES ($s,$e,$rev,$cool,$log,$ts)";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$e", req.Enabled ? 1 : 0);
            c.Parameters.AddWithValue("$rev", req.RequireEmailVerified ? 1 : 0);
            c.Parameters.AddWithValue("$cool", cooldown);
            c.Parameters.AddWithValue("$log", req.LogAllDeletions ? 1 : 0);
            c.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            c.ExecuteNonQuery();

            ServerDb.Audit(sid, u, "secure_mode_changed", "",
                $"enabled={req.Enabled} emailVerified={req.RequireEmailVerified} cooldown={cooldown}s logDeletions={req.LogAllDeletions}");

            return Results.Ok(new
            {
                success = true,
                enabled = req.Enabled,
                requireEmailVerified = req.RequireEmailVerified,
                newMemberCooldown = cooldown,
                logAllDeletions = req.LogAllDeletions
            });
        });
    }

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}
