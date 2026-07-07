public record CreateInviteReq(int MaxUses, int ExpiresIn);  // ExpiresIn = seconds

public static class ServerInvites
{
    public static void Register(WebApplication app)
    {
        // GET /api/servers/{sid}/invites
        app.MapGet("/api/servers/{sid}/invites", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageInvites) is { } err) return err;
            return Results.Ok(ServerDb.GetInvites(sid));
        });

        // POST /api/servers/{sid}/invites
        app.MapPost("/api/servers/{sid}/invites", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageInvites) is { } err) return err;

            var req = await ctx.Request.ReadFromJsonAsync<CreateInviteReq>();
            int maxUses = Math.Clamp(req?.MaxUses ?? 0, 0, 10000);
            int expireSec = Math.Clamp(req?.ExpiresIn ?? 86400, 60, 30 * 86400);

            var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();
            var expiresAt = DateTime.UtcNow.AddSeconds(expireSec).ToString("o");
            var now = DateTime.UtcNow.ToString("o");

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"INSERT INTO ServerInvites
                (ServerId, Code, CreatedBy, MaxUses, Uses, ExpiresAt, CreatedAt)
                VALUES ($s,$code,$by,$max,0,$exp,$ts)";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$code", code);
            c.Parameters.AddWithValue("$by", u);
            c.Parameters.AddWithValue("$max", maxUses);
            c.Parameters.AddWithValue("$exp", expiresAt);
            c.Parameters.AddWithValue("$ts", now);
            c.ExecuteNonQuery();

            ServerDb.Audit(sid, u, "invite_created", code,
                $"maxUses={maxUses} expires={expiresAt}");

            return Results.Ok(new
            {
                code,
                link = $"https://runspace.cloud/invite/{code}",
                maxUses,
                expiresAt,
                createdAt = now
            });
        });

        // DELETE /api/servers/{sid}/invites/{code}  — revoke
        app.MapDelete("/api/servers/{sid}/invites/{code}", (string sid, string code, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageInvites) is { } err) return err;
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = "DELETE FROM ServerInvites WHERE ServerId=$s AND Code=$code";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$code", code.ToLowerInvariant());
            c.ExecuteNonQuery();
            ServerDb.Audit(sid, u, "invite_revoked", code);
            return Results.Ok(new { success = true });
        });

        // GET /api/invite/{code}  — public, no auth needed
        app.MapGet("/api/invite/{code}", (string code) =>
        {
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"
                SELECT si.ServerId, si.CreatedBy, si.MaxUses, si.Uses, si.ExpiresAt,
                       g.Name,
                       (SELECT COUNT(*) FROM GroupMembers WHERE GroupId=si.ServerId) AS MemberCount
                FROM ServerInvites si
                JOIN Groups g ON si.ServerId=g.GroupId
                WHERE si.Code=$code
                  AND (si.ExpiresAt IS NULL OR si.ExpiresAt > datetime('now'))
                  AND (si.MaxUses=0 OR si.Uses < si.MaxUses)
                LIMIT 1";
            c.Parameters.AddWithValue("$code", code.ToLowerInvariant());
            using var r = c.ExecuteReader();
            if (!r.Read())
                return Results.NotFound(new { message = "Ogiltig eller utgången inbjudan." });
            return Results.Ok(new
            {
                code,
                serverId = r.GetString(0),
                serverName = r.GetString(5),
                memberCount = r.GetInt64(6),
                createdBy = r.GetString(1),
                maxUses = r.GetInt32(2),
                uses = r.GetInt32(3),
                expiresAt = r.IsDBNull(4) ? "" : r.GetString(4)
            });
        });

        // POST /api/invite/{code}/join
        app.MapPost("/api/invite/{code}/join", (string code, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            code = code.ToLowerInvariant();

            using var db = DbHelpers.OpenDb();
            using var find = db.CreateCommand();
            find.CommandText = @"SELECT Id, ServerId, MaxUses, Uses FROM ServerInvites
                WHERE Code=$code
                  AND (ExpiresAt IS NULL OR ExpiresAt > datetime('now'))
                  AND (MaxUses=0 OR Uses < MaxUses)
                LIMIT 1";
            find.Parameters.AddWithValue("$code", code);
            using var fr = find.ExecuteReader();
            if (!fr.Read())
                return Results.NotFound(new { message = "Ogiltig eller utgången inbjudan." });
            var inviteId = fr.GetInt64(0);
            var serverId = fr.GetString(1);
            fr.Close();

            if (Perms.IsBanned(serverId, u))
                return Results.Json(new { message = "Du är bannad från denna server." }, statusCode: 403);
            if (Perms.IsMember(serverId, u))
                return Results.BadRequest(new { message = "Du är redan medlem." });

            // Check secure mode: require verified email to join via invite
            var sm = Perms.GetSecureMode(serverId);
            if (sm.Enabled && sm.RequireEmailVerified)
            {
                using var vc = db.CreateCommand();
                vc.CommandText = "SELECT EmailVerified FROM AuthUsers WHERE LOWER(Username)=$u LIMIT 1";
                vc.Parameters.AddWithValue("$u", u);
                var verified = Convert.ToInt32(vc.ExecuteScalar());
                if (verified != 1)
                    return Results.Json(new { message = "Säkert läge aktiverat: verifierad e-post krävs för att gå med." }, statusCode: 403);
            }

            // Add member
            using var mc = db.CreateCommand();
            mc.CommandText = @"INSERT OR IGNORE INTO GroupMembers (GroupId, Username, Role, JoinedAt)
                VALUES ($g,$u,'member',$ts)";
            mc.Parameters.AddWithValue("$g", serverId);
            mc.Parameters.AddWithValue("$u", u);
            mc.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            mc.ExecuteNonQuery();

            // Increment uses
            using var upd = db.CreateCommand();
            upd.CommandText = "UPDATE ServerInvites SET Uses=Uses+1 WHERE Id=$id";
            upd.Parameters.AddWithValue("$id", inviteId);
            upd.ExecuteNonQuery();

            ServerDb.Audit(serverId, u, "invite_join", code);
            return Results.Ok(new { success = true, serverId });
        });
    }

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}
