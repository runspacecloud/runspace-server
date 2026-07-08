// ═══════════════════════════════════════════════════════════════════════════
// ServerChannels.cs  —  Channel management endpoints
// ═══════════════════════════════════════════════════════════════════════════
public record UpdateChannelReq(string Name);

public static class ServerChannels
{
    public static void Register(WebApplication app)
    {
        // GET /api/servers/{sid}/channels
        app.MapGet("/api/servers/{sid}/channels", (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ViewChannels) is { } err) return err;
            return Results.Ok(GetChannels(sid));
        });

        // POST /api/servers/{sid}/channels
        app.MapPost("/api/servers/{sid}/channels", async (string sid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageChannels) is { } err) return err;

            var req = await ctx.Request.ReadFromJsonAsync<CreateChannelReq>();
            var rawName = req?.Name ?? "";
            if (!DefensiveInput.IsSafeSlug(rawName, 2, 32))
                return Results.BadRequest(new { message = "Ogiltigt kanalnamn." });

            var name = DefensiveInput.CleanSlug(rawName, 32);
            if (name.Length < 2)
                return Results.BadRequest(new { message = "Kanalnamn måste vara minst 2 tecken." });
            var type = (req?.Type ?? "text").ToLowerInvariant();
            if (type != "text")
                return Results.BadRequest(new { message = "Typ måste vara 'text'." });

            var cid = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var now = DateTime.UtcNow.ToString("o");

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = @"INSERT INTO GroupChannels (GroupId, ChannelId, Name, Type, CreatedAt)
                VALUES ($g,$cid,$n,$t,$ts)";
            c.Parameters.AddWithValue("$g", sid);
            c.Parameters.AddWithValue("$cid", cid);
            c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$t", type);
            c.Parameters.AddWithValue("$ts", now);
            c.ExecuteNonQuery();

            ServerDb.Audit(sid, u, "channel_created", cid, $"name={name} type={type}");
            return Results.Ok(new { channelId = cid, name, type, createdAt = now });
        });

        // PUT /api/servers/{sid}/channels/{cid}
        app.MapPut("/api/servers/{sid}/channels/{cid}",
            async (string sid, string cid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageChannels) is { } err) return err;

            var req = await ctx.Request.ReadFromJsonAsync<UpdateChannelReq>();
            if (req == null || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { message = "Namn krävs." });

            var rawName = req.Name ?? "";
            if (!DefensiveInput.IsSafeSlug(rawName, 2, 32))
                return Results.BadRequest(new { message = "Ogiltigt kanalnamn." });

            var name = DefensiveInput.CleanSlug(rawName, 32);

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();
            c.CommandText = "UPDATE GroupChannels SET Name=$n WHERE GroupId=$g AND ChannelId=$cid";
            c.Parameters.AddWithValue("$n", name);
            c.Parameters.AddWithValue("$g", sid);
            c.Parameters.AddWithValue("$cid", cid);
            var affected = c.ExecuteNonQuery();
            if (affected == 0) return Results.NotFound(new { message = "Kanal ej hittad." });

            ServerDb.Audit(sid, u, "channel_renamed", cid, $"name={name}");
            return Results.Ok(new { success = true });
        });

        // DELETE /api/servers/{sid}/channels/{cid}
        app.MapDelete("/api/servers/{sid}/channels/{cid}", (string sid, string cid, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();
            if (Perms.Deny(sid, u, ServerPerm.ManageChannels) is { } err) return err;

            using var db = DbHelpers.OpenDb();

            // Verify channel belongs to this server
            using var vc = db.CreateCommand();
            vc.CommandText = "SELECT COUNT(*) FROM GroupChannels WHERE GroupId=$g AND ChannelId=$cid";
            vc.Parameters.AddWithValue("$g", sid); vc.Parameters.AddWithValue("$cid", cid);
            if (Convert.ToInt64(vc.ExecuteScalar()) == 0)
                return Results.NotFound(new { message = "Kanal ej hittad." });

            // Soft-delete messages in channel
            using var mc = db.CreateCommand();
            mc.CommandText = "UPDATE GroupMessages SET Deleted=1 WHERE GroupId=$g AND ChannelId=$cid";
            mc.Parameters.AddWithValue("$g", sid); mc.Parameters.AddWithValue("$cid", cid);
            mc.ExecuteNonQuery();

            using var c = db.CreateCommand();
            c.CommandText = "DELETE FROM GroupChannels WHERE GroupId=$g AND ChannelId=$cid";
            c.Parameters.AddWithValue("$g", sid); c.Parameters.AddWithValue("$cid", cid);
            c.ExecuteNonQuery();

            ServerDb.Audit(sid, u, "channel_deleted", cid);
            return Results.Ok(new { success = true });
        });
    }

    public static List<object> GetChannels(string serverId)
    {
        var list = new List<object>();
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT ChannelId, Name, Type, CreatedAt
            FROM GroupChannels WHERE GroupId=$g ORDER BY Id";
        c.Parameters.AddWithValue("$g", serverId);
        using var r = c.ExecuteReader();
        while (r.Read())
            list.Add(new
            {
                channelId = r.GetString(0),
                name = r.GetString(1),
                type = r.GetString(2),
                createdAt = r.GetString(3)
            });
        return list;
    }

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}
