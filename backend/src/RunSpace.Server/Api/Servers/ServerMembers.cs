public static class ServerMembers
{
    public static void Register(WebApplication app)
    {
        // GET /api/servers/{sid}/members
        app.MapGet("/api/servers/{sid}/members", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ViewChannels) is { } err) return err;
            return Results.Ok(ServerDb.GetMembers(sid));
        });

        // DELETE /api/servers/{sid}/members/{target}  — kick
        app.MapDelete("/api/servers/{sid}/members/{target}", (string sid, string target, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            target = target.ToLowerInvariant();
            if (Perms.DenyHierarchy(sid, u, target, ServerPerm.KickMembers) is { } err) return err;
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = "DELETE FROM GroupMembers WHERE GroupId=$g AND LOWER(Username)=$t";
            c.Parameters.AddWithValue("$g", sid);
            c.Parameters.AddWithValue("$t", target);
            c.ExecuteNonQuery();
            // Remove assigned roles
            using var rc = db.CreateCommand();
            rc.CommandText = @"DELETE FROM ServerMemberRoles WHERE ServerId=$s
                AND UserId=(SELECT CAST(Id AS TEXT) FROM AuthUsers WHERE LOWER(Username)=$t LIMIT 1)";
            rc.Parameters.AddWithValue("$s", sid);
            rc.Parameters.AddWithValue("$t", target);
            rc.ExecuteNonQuery();
            ServerDb.Audit(sid, u, "member_kicked", target);
            return Results.Ok(new { success = true });
        });

        // POST /api/servers/{sid}/members/{target}/roles/{roleId}  — assign role
        app.MapPost("/api/servers/{sid}/members/{target}/roles/{roleId}",
            (string sid, string target, string roleId, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageRoles) is { } err) return err;
            // Actor must be above the role they're assigning
            if (!Perms.CanManageRole(sid, u, roleId))
                return Results.Json(new { message = "Din roll är inte tillräckligt hög för att tilldela den rollen." }, statusCode: 403);
            // Actor must also be above target
            if (!Perms.IsHierarchyAbove(sid, u, target.ToLowerInvariant()))
                return Results.Json(new { message = "Du kan inte tilldela roller till en person med högre rang." }, statusCode: 403);
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"INSERT OR IGNORE INTO ServerMemberRoles (ServerId, UserId, RoleId)
                SELECT $s, CAST(Id AS TEXT), $r FROM AuthUsers WHERE LOWER(Username)=$t LIMIT 1";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$r", roleId);
            c.Parameters.AddWithValue("$t", target.ToLowerInvariant());
            c.ExecuteNonQuery();
            ServerDb.Audit(sid, u, "role_assigned", target, $"role={roleId}");
            return Results.Ok(new { success = true });
        });

        // DELETE /api/servers/{sid}/members/{target}/roles/{roleId}  — remove role
        app.MapDelete("/api/servers/{sid}/members/{target}/roles/{roleId}",
            (string sid, string target, string roleId, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageRoles) is { } err) return err;
            if (!Perms.CanManageRole(sid, u, roleId))
                return Results.Json(new { message = "Du kan inte ta bort en roll med högre rang än din." }, statusCode: 403);
            if (!Perms.IsHierarchyAbove(sid, u, target.ToLowerInvariant()))
                return Results.Json(new { message = "Kan inte ändra roller på en person med högre rang." }, statusCode: 403);
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"DELETE FROM ServerMemberRoles WHERE ServerId=$s AND RoleId=$r
                AND UserId=(SELECT CAST(Id AS TEXT) FROM AuthUsers WHERE LOWER(Username)=$t LIMIT 1)";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$r", roleId);
            c.Parameters.AddWithValue("$t", target.ToLowerInvariant());
            c.ExecuteNonQuery();
            ServerDb.Audit(sid, u, "role_removed", target, $"role={roleId}");
            return Results.Ok(new { success = true });
        });
    }

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}
