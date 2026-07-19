using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;

public record TempChatFriendInviteRequest(
    List<string>? Usernames
);

public static class TempChatFriendInvites
{
    public static void Register(
        WebApplication app
    )
    {
        app.MapPost(
            "/api/temp-chats/{roomId}/invite-friends",
            async (
                string roomId,
                HttpContext ctx,
                IHubContext<ChatHub> hub
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user =
                    (
                        ctx.User.Identity?.Name
                        ?? ""
                    )
                    .Trim()
                    .ToLowerInvariant();

                if (
                    string.IsNullOrWhiteSpace(user)
                    || !AppHelpers.UserExists(user)
                )
                {
                    return Results.Unauthorized();
                }

                var limiter =
                    ctx.RequestServices
                        .GetRequiredService<RateLimiter>();

                if (
                    !limiter.IsAllowed(
                        user,
                        "temp_chat_friend_invite",
                        20,
                        3600
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Too many invites. Try again later."
                        },
                        statusCode: 429
                    );
                }

                var request =
                    await ctx.Request
                        .ReadFromJsonAsync<
                            TempChatFriendInviteRequest
                        >();

                var targets =
                    (
                        request?.Usernames
                        ?? new List<string>()
                    )
                    .Select(
                        x =>
                            (
                                x
                                ?? ""
                            )
                            .Trim()
                            .ToLowerInvariant()
                    )
                    .Where(
                        x =>
                            !string.IsNullOrWhiteSpace(x)
                            && x != user
                    )
                    .Distinct(
                        StringComparer
                            .OrdinalIgnoreCase
                    )
                    .Take(25)
                    .ToList();

                if (
                    targets.Count == 0
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "Select at least one friend."
                        }
                    );
                }

                using var db =
                    DbHelpers.OpenDb();

                string inviteCode;
                string expiresAt;

                using (
                    var room =
                        db.CreateCommand()
                )
                {
                    room.CommandText = """
                    SELECT
                        r.InviteCode,
                        r.ExpiresAt
                    FROM TempChats r
                    INNER JOIN TempChatMembers m
                        ON m.RoomId=r.Id
                    WHERE r.Id=$room
                      AND m.Username=$user
                      AND r.Closed=0
                      AND r.ExpiresAt > $now
                    LIMIT 1
                    """;

                    room.Parameters
                        .AddWithValue(
                            "$room",
                            roomId
                        );

                    room.Parameters
                        .AddWithValue(
                            "$user",
                            user
                        );

                    room.Parameters
                        .AddWithValue(
                            "$now",
                            DateTime.UtcNow
                                .ToString("o")
                        );

                    using var reader =
                        room.ExecuteReader();

                    if (!reader.Read())
                    {
                        return Results.Json(
                            new
                            {
                                error =
                                    "The Temp Chat no longer exists or you do not have access."
                            },
                            statusCode: 403
                        );
                    }

                    inviteCode =
                        reader.GetString(0);

                    expiresAt =
                        reader.GetString(1);
                }

                var invited =
                    new List<string>();

                var skipped =
                    new List<string>();

                foreach (
                    var target
                    in targets
                )
                {
                    if (
                        !AppHelpers.UserExists(
                            target
                        )
                    )
                    {
                        skipped.Add(
                            target
                        );

                        continue;
                    }

                    bool areFriends;

                    using (
                        var friend =
                            db.CreateCommand()
                    )
                    {
                        friend.CommandText = """
                        SELECT COUNT(*)
                        FROM Friendships
                        WHERE
                        (
                            LOWER(UserA)=$me
                            AND
                            LOWER(UserB)=$target
                        )
                        OR
                        (
                            LOWER(UserA)=$target
                            AND
                            LOWER(UserB)=$me
                        )
                        """;

                        friend.Parameters
                            .AddWithValue(
                                "$me",
                                user
                            );

                        friend.Parameters
                            .AddWithValue(
                                "$target",
                                target
                            );

                        areFriends =
                            Convert.ToInt32(
                                friend.ExecuteScalar()
                            ) > 0;
                    }

                    if (!areFriends)
                    {
                        skipped.Add(
                            target
                        );

                        continue;
                    }

                    var message =
                        "[temp-invite]"
                        + inviteCode
                        + "|"
                        + expiresAt
                        + "[/temp-invite]";

                    var timestamp =
                        DateTime.UtcNow
                            .ToString("o");

                    long id;

                    using (
                        var insert =
                            db.CreateCommand()
                    )
                    {
                        insert.CommandText = """
                        INSERT INTO ChatMessages
                        (
                            FromUser,
                            ToUser,
                            Message,
                            Timestamp,
                            Encrypted,
                            Iv,
                            Algorithm,
                            EncryptedKey,
                            SenderEncryptedKey,
                            RecipientKeysJson,
                            SenderKeysJson,
                            ReplyToId
                        )
                        VALUES
                        (
                            $from,
                            $to,
                            $message,
                            $timestamp,
                            0,
                            '',
                            'plain',
                            '',
                            '',
                            '[]',
                            '[]',
                            0
                        );

                        SELECT last_insert_rowid();
                        """;

                        insert.Parameters
                            .AddWithValue(
                                "$from",
                                user
                            );

                        insert.Parameters
                            .AddWithValue(
                                "$to",
                                target
                            );

                        insert.Parameters
                            .AddWithValue(
                                "$message",
                                message
                            );

                        insert.Parameters
                            .AddWithValue(
                                "$timestamp",
                                timestamp
                            );

                        id =
                            Convert.ToInt64(
                                insert.ExecuteScalar()
                            );
                    }

                    var payload =
                        ChatHelpers.BuildPayload(
                            id,
                            user,
                            target,
                            message,
                            timestamp,
                            false,
                            "",
                            "plain",
                            "",
                            "",
                            "[]",
                            "[]",
                            0
                        );

                    await hub
                        .Clients
                        .User(target)
                        .SendAsync(
                            "ReceiveMessage",
                            payload
                        );

                    await hub
                        .Clients
                        .User(user)
                        .SendAsync(
                            "ReceiveMessage",
                            payload
                        );

                    invited.Add(
                        target
                    );
                }

                return Results.Ok(
                    new
                    {
                        success = true,
                        invited,
                        skipped,
                        count =
                            invited.Count
                    }
                );
            }
        );
    }
}
