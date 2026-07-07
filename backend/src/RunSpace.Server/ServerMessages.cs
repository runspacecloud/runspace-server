using Microsoft.AspNetCore.SignalR;
// ═══════════════════════════════════════════════════════════════════════════
// ServerMessages.cs  —  Channel message endpoints
// ═══════════════════════════════════════════════════════════════════════════
public record PostMessageReq(string Content);

public static class ServerMessages
{
    public static void Register(WebApplication app)
    {
        // GET /api/channels/{cid}/messages?limit=50&before=msgId
        app.MapGet("/api/channels/{cid}/messages",
            (string cid, int? limit, long? before, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();

            var (serverId, found) = GetServerId(cid);
            if (!found) return Results.NotFound(new { message = "Kanal ej hittad." });
            if (Perms.Deny(serverId!, u, ServerPerm.ViewChannels) is { } err) return err;

            int take = Math.Clamp(limit ?? 50, 1, 100);
            using var db = DbHelpers.OpenDb();
            using var mc = db.CreateCommand();
            if (before.HasValue)
            {
                mc.CommandText = @"SELECT Id, FromUser, Message, Timestamp
                    FROM GroupMessages
                    WHERE GroupId=$g AND ChannelId=$cid AND Deleted=0 AND Id < $before
                    ORDER BY Id DESC LIMIT $lim";
                mc.Parameters.AddWithValue("$before", before.Value);
            }
            else
            {
                mc.CommandText = @"SELECT Id, FromUser, Message, Timestamp
                    FROM GroupMessages
                    WHERE GroupId=$g AND ChannelId=$cid AND Deleted=0
                    ORDER BY Id DESC LIMIT $lim";
            }
            mc.Parameters.AddWithValue("$g", serverId!);
            mc.Parameters.AddWithValue("$cid", cid);
            mc.Parameters.AddWithValue("$lim", take);

            var msgs = new List<object>();
            using var mr = mc.ExecuteReader();
            while (mr.Read())
                msgs.Add(new
                {
                    id = mr.GetInt64(0),
                    from = mr.GetString(1),
                    content = mr.GetString(2),
                    timestamp = mr.GetString(3)
                });
            msgs.Reverse(); // return oldest-first
            return Results.Ok(msgs);
        });

        // POST /api/channels/{cid}/messages
        app.MapPost("/api/channels/{cid}/messages",
            async (string cid, HttpContext ctx,
                   Microsoft.AspNetCore.SignalR.IHubContext<ChatHub> hub) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();

            var (serverId, found) = GetServerId(cid);
            if (!found) return Results.NotFound(new { message = "Kanal ej hittad." });
            if (Perms.Deny(serverId!, u, ServerPerm.SendMessages) is { } err) return err;

            // Secure mode: new-member cooldown
            if (Perms.IsInNewMemberCooldown(serverId!, u))
                return Results.Json(new { message = "Säkert läge: du måste vänta lite innan du kan skicka meddelanden." }, statusCode: 429);

            var req = await ctx.Request.ReadFromJsonAsync<PostMessageReq>();
            var rawText = req?.Content ?? "";

            if (!DefensiveInput.IsSafeMessageText(rawText, 4000))
                return Results.BadRequest(new { message = "Ogiltigt meddelande." });

            var text = InputSanitizer.SanitizeInput(rawText, 4000).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { message = "Tomt meddelande." });

            var ts = DateTime.UtcNow.ToString("o");
            using var db = DbHelpers.OpenDb();
            using var mc = db.CreateCommand();
            mc.CommandText = @"INSERT INTO GroupMessages
                (GroupId, ChannelId, FromUser, Message, Timestamp, Deleted)
                VALUES ($g,$cid,$u,$m,$ts,0);
                SELECT last_insert_rowid();";
            mc.Parameters.AddWithValue("$g", serverId!);
            mc.Parameters.AddWithValue("$cid", cid);
            mc.Parameters.AddWithValue("$u", u);
            mc.Parameters.AddWithValue("$m", text);
            mc.Parameters.AddWithValue("$ts", ts);
            var id = (long)(mc.ExecuteScalar() ?? 0L);

            var payload = new { id, groupId = serverId, channelId = cid, from = u, content = text, timestamp = ts };
            // Broadcast to server group channel via SignalR
            await hub.Clients.Group($"channel:{cid}").SendAsync("ChannelMessage", payload);

            return Results.Ok(new { success = true, message = payload });
        });

        // DELETE /api/messages/{messageId}
        app.MapDelete("/api/messages/{messageId:long}", (long messageId, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();

            using var db = DbHelpers.OpenDb();
            using var gc = db.CreateCommand();
            gc.CommandText = "SELECT GroupId, ChannelId, FromUser FROM GroupMessages WHERE Id=$id LIMIT 1";
            gc.Parameters.AddWithValue("$id", messageId);
            using var gr = gc.ExecuteReader();
            if (!gr.Read()) return Results.NotFound(new { message = "Meddelande ej hittad." });
            var serverId = gr.GetString(0);
            var channelId = gr.GetString(1);
            var author = gr.GetString(2);
            gr.Close();

            bool isAuthor = author.ToLowerInvariant() == u;
            bool canManage = Perms.Has(serverId, u, ServerPerm.ManageMessages);
            if (!isAuthor && !canManage)
                return Results.Json(new { message = "Saknar behörighet att ta bort meddelandet." }, statusCode: 403);

            using var dc = db.CreateCommand();
            dc.CommandText = "UPDATE GroupMessages SET Deleted=1 WHERE Id=$id";
            dc.Parameters.AddWithValue("$id", messageId);
            dc.ExecuteNonQuery();

            // Audit if moderator deleted (secure mode or ManageMessages)
            var sm = Perms.GetSecureMode(serverId);
            if (!isAuthor || (sm.Enabled && sm.LogAllDeletions))
                ServerDb.Audit(serverId, u, "message_deleted", messageId.ToString(),
                               $"channel={channelId} author={author}");

            return Results.Ok(new { success = true });
        });

        // POST /api/messages/{messageId}/burn
        app.MapPost("/api/messages/{messageId:long}/burn", (long messageId, HttpContext ctx) =>
        {
            var u = Actor(ctx); if (u is null) return Results.Unauthorized();

            using var db = DbHelpers.OpenDb();
            using var gc = db.CreateCommand();
            gc.CommandText = "SELECT GroupId, FromUser FROM GroupMessages WHERE Id=$id AND Deleted=0 LIMIT 1";
            gc.Parameters.AddWithValue("$id", messageId);
            using var gr = gc.ExecuteReader();
            if (!gr.Read()) return Results.NotFound(new { message = "Meddelande ej hittad." });
            var serverId = gr.GetString(0);
            var author = gr.GetString(1);
            gr.Close();

            if (author.ToLowerInvariant() != u)
                return Results.Json(new { message = "Bara avsändaren kan markera burn." }, statusCode: 403);

            using var bc = db.CreateCommand();
            bc.CommandText = @"INSERT OR REPLACE INTO BurnMessages (MessageId, BurnAfterAt)
                VALUES ($mid, datetime('now', '+5 minutes'))";
            bc.Parameters.AddWithValue("$mid", messageId);
            bc.ExecuteNonQuery();

            return Results.Ok(new { success = true, burnAfterMinutes = 5 });
        });
    }

    static (string? ServerId, bool Found) GetServerId(string channelId)
    {
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT GroupId FROM GroupChannels WHERE ChannelId=$cid LIMIT 1";
        c.Parameters.AddWithValue("$cid", channelId);
        var raw = c.ExecuteScalar() as string;
        return (raw, raw != null);
    }

    static string? Actor(HttpContext ctx) =>
        ctx.User.Identity?.Name?.Trim().ToLowerInvariant() is { Length: > 0 } u ? u : null;
}
