public record CreateRoleReqV2(string Name, string? Color, List<string>? Permissions);
public record UpdateRoleReqV2(string? Name, string? Color, int? Position);
public record SetPermissionsReq(List<string> Permissions);

public static class ServerRoles
{
    public static void Register(WebApplication app)
    {
        // GET /api/servers/{sid}/roles
        app.MapGet("/api/servers/{sid}/roles", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (!Perms.IsMember(sid, u)) return Results.Forbid();
            return Results.Ok(ServerDb.GetRoles(sid));
        });

        // GET /api/permissions  — return all available permission names and bits
        app.MapGet("/api/permissions", () => Results.Ok(Perms.AllPermissions()));

        // POST /api/servers/{sid}/roles
        app.MapPost("/api/servers/{sid}/roles", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageRoles) is { } err) return err;
            var req = await ctx.Request.ReadFromJsonAsync<CreateRoleReqV2>();
            if (req == null || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { message = "Namn krävs." });

            var rawName = req.Name?.Trim() ?? "";
            if (!DefensiveInput.IsSafeDisplayName(rawName, 1, 32))
                return Results.BadRequest(new { message = "Ogiltigt rollnamn." });

            if (req.Color != null && !DefensiveInput.IsSafeHexColor(req.Color))
                return Results.BadRequest(new { message = "Ogiltig färg." });

            var name = DefensiveInput.CleanDisplayName(rawName, 32);
            var color = req.Color?.Trim() ?? "#94a3b8";
            long perms = Perms.ParseBits(req.Permissions);

            // Actor can only grant permissions they themselves hold
            var actorPerms = Perms.GetEffective(sid, u);
            perms &= (long)actorPerms;  // mask to what actor has

            // Position: just below actor's highest role
            int actorPos = Perms.GetHighestRolePosition(sid, u);
            int newPos = actorPos + 1;

            var roleId = Guid.NewGuid().ToString("N")[..12];
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"INSERT INTO ServerRoles
                (ServerId, RoleId, Name, Color, Permissions, Position, IsDefault, CreatedAt)
                VALUES ($s,$rid,$n,$col,$p,$pos,0,$ts)";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$rid", roleId);
            c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$col", color);
            c.Parameters.AddWithValue("$p", perms);
            c.Parameters.AddWithValue("$pos", newPos);
            c.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            c.ExecuteNonQuery();
            ServerDb.Audit(sid, u, "role_created", roleId, $"name={name} pos={newPos}");
            return Results.Ok(new
            {
                roleId,
                name,
                color,
                permissions = perms,
                permNames = Perms.ToNames(perms),
                position = newPos
            });
        });

        // PUT /api/servers/{sid}/roles/{roleId}
        app.MapPut("/api/servers/{sid}/roles/{roleId}", async (string sid, string roleId, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageRoles) is { } err) return err;
            if (!Perms.CanManageRole(sid, u, roleId))
                return Results.Json(new { message = "Du kan inte redigera en roll med högre eller likvärdig rang." }, statusCode: 403);

            var req = await ctx.Request.ReadFromJsonAsync<UpdateRoleReqV2>();
            if (req == null) return Results.BadRequest(new { message = "Body krävs." });

            using var db = DbHelpers.OpenDb();
            var sets = new List<string>();
            var cmd = db.CreateCommand();
            cmd.Parameters.AddWithValue("$s", sid);
            cmd.Parameters.AddWithValue("$rid", roleId);

            if (req.Name != null)
            {
                var rawName = req.Name.Trim();
                if (!DefensiveInput.IsSafeDisplayName(rawName, 1, 32))
                    return Results.BadRequest(new { message = "Ogiltigt rollnamn." });

                sets.Add("Name=$n");
                cmd.Parameters.AddWithValue("$n", DefensiveInput.CleanDisplayName(rawName, 32));
            }
            if (req.Color != null)
            {
                if (!DefensiveInput.IsSafeHexColor(req.Color))
                    return Results.BadRequest(new { message = "Ogiltig färg." });

                sets.Add("Color=$col");
                cmd.Parameters.AddWithValue("$col", req.Color.Trim());
            }
            if (req.Position.HasValue)
            {
                // Can only reorder to a position strictly below actor's own highest role
                int actorPos = Perms.GetHighestRolePosition(sid, u);
                int newPos = Math.Max(req.Position.Value, actorPos + 1);
                sets.Add("Position=$pos");
                cmd.Parameters.AddWithValue("$pos", newPos);
            }

            if (!sets.Any()) return Results.BadRequest(new { message = "Inget att uppdatera." });
            cmd.CommandText = $"UPDATE ServerRoles SET {string.Join(",", sets)} WHERE ServerId=$s AND RoleId=$rid";
            var affected = cmd.ExecuteNonQuery();
            if (affected == 0) return Results.NotFound(new { message = "Roll ej hittad." });
            ServerDb.Audit(sid, u, "role_updated", roleId);
            return Results.Ok(new { success = true });
        });

        // DELETE /api/servers/{sid}/roles/{roleId}
        app.MapDelete("/api/servers/{sid}/roles/{roleId}", (string sid, string roleId, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            // Only owner can delete roles (ManageRoles alone is not enough)
            if (!Perms.IsOwner(sid, u))
                return Results.Json(new { message = "Bara ägaren kan ta bort roller." }, statusCode: 403);
            if (roleId is "owner" or "member")
                return Results.BadRequest(new { message = "Kan inte ta bort owner- eller member-rollen." });

            using var db = DbHelpers.OpenDb();
            // Remove member-role mappings first
            using var mr = db.CreateCommand();
            mr.CommandText = "DELETE FROM ServerMemberRoles WHERE ServerId=$s AND RoleId=$r";
            mr.Parameters.AddWithValue("$s", sid);
            mr.Parameters.AddWithValue("$r", roleId);
            mr.ExecuteNonQuery();
            // Delete role
            using var c = db.CreateCommand();
            c.CommandText = "DELETE FROM ServerRoles WHERE ServerId=$s AND RoleId=$r";
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$r", roleId);
            var affected = c.ExecuteNonQuery();
            if (affected == 0) return Results.NotFound(new { message = "Roll ej hittad." });
            ServerDb.Audit(sid, u, "role_deleted", roleId);
            return Results.Ok(new { success = true });
        });

        // PUT /api/servers/{sid}/roles/{roleId}/permissions
        app.MapPut("/api/servers/{sid}/roles/{roleId}/permissions",
            async (string sid, string roleId, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageRoles) is { } err) return err;
            if (!Perms.CanManageRole(sid, u, roleId))
                return Results.Json(new { message = "Du kan inte ändra en roll med högre rang." }, statusCode: 403);

            var req = await ctx.Request.ReadFromJsonAsync<SetPermissionsReq>();
            if (req == null) return Results.BadRequest();

            long newPerms = Perms.ParseBits(req.Permissions);
            // Clamp: can only grant permissions the actor holds
            var actorPerms = Perms.GetEffective(sid, u);
            newPerms &= (long)actorPerms;

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = "UPDATE ServerRoles SET Permissions=$p WHERE ServerId=$s AND RoleId=$r";
            c.Parameters.AddWithValue("$p", newPerms);
            c.Parameters.AddWithValue("$s", sid);
            c.Parameters.AddWithValue("$r", roleId);
            c.ExecuteNonQuery();
            ServerDb.Audit(sid, u, "permissions_updated", roleId, $"bits={newPerms}");
            return Results.Ok(new { permissions = newPerms, permNames = Perms.ToNames(newPerms) });
        });
    }

    static string? ValidColor(string? c) =>
        c != null && System.Text.RegularExpressions.Regex.IsMatch(c.Trim(), @"^#[0-9a-fA-F]{6}$")
            ? c.Trim() : null;

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}
