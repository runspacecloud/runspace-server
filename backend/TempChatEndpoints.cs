using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public record TempChatCreateRequest(
    int DurationMinutes,
    int MaxMembers
);

public record TempChatJoinRequest(
    string Code
);

public record TempChatSendRequest(
    string Text
);

public static class TempChatStore
{
    public static void EnsureDatabase()
    {
        using var db = DbHelpers.OpenDb();

        using var cmd = db.CreateCommand();

        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS TempChats
        (
            Id            TEXT PRIMARY KEY,
            InviteCode    TEXT NOT NULL UNIQUE,
            OwnerUsername TEXT NOT NULL,
            CreatedAt     TEXT NOT NULL,
            ExpiresAt     TEXT NOT NULL,
            MaxMembers    INTEGER NOT NULL DEFAULT 2,
            Closed        INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS TempChatMembers
        (
            Id       INTEGER PRIMARY KEY AUTOINCREMENT,
            RoomId   TEXT NOT NULL,
            Username TEXT NOT NULL,
            JoinedAt TEXT NOT NULL,
            UNIQUE(RoomId, Username)
        );

        CREATE TABLE IF NOT EXISTS TempChatMessages
        (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            RoomId    TEXT NOT NULL,
            FromUser  TEXT NOT NULL,
            Message   TEXT NOT NULL,
            CreatedAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS
            IX_TempChats_InviteCode
        ON TempChats(InviteCode);

        CREATE INDEX IF NOT EXISTS
            IX_TempChats_ExpiresAt
        ON TempChats(ExpiresAt);

        CREATE INDEX IF NOT EXISTS
            IX_TempChatMembers_Room
        ON TempChatMembers(RoomId);

        CREATE INDEX IF NOT EXISTS
            IX_TempChatMembers_User
        ON TempChatMembers(Username);

        CREATE INDEX IF NOT EXISTS
            IX_TempChatMessages_Room_Id
        ON TempChatMessages(RoomId, Id);
        """;

        cmd.ExecuteNonQuery();
    }

    public static void CleanupExpired()
    {
        using var db = DbHelpers.OpenDb();

        using var tx = db.BeginTransaction();

        var now = DateTime.UtcNow.ToString("o");

        foreach (var sql in new[]
        {
            """
            DELETE FROM TempChatMessages
            WHERE RoomId IN
            (
                SELECT Id
                FROM TempChats
                WHERE Closed=1
                   OR ExpiresAt <= $now
            )
            """,

            """
            DELETE FROM TempChatMembers
            WHERE RoomId IN
            (
                SELECT Id
                FROM TempChats
                WHERE Closed=1
                   OR ExpiresAt <= $now
            )
            """,

            """
            DELETE FROM TempChats
            WHERE Closed=1
               OR ExpiresAt <= $now
            """
        })
        {
            using var cmd = db.CreateCommand();

            cmd.Transaction = tx;

            cmd.CommandText = sql;

            cmd.Parameters.AddWithValue(
                "$now",
                now
            );

            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}

public sealed class TempChatCleanupService
    : BackgroundService
{
    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken
    )
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(1)
        );

        while (
            await timer.WaitForNextTickAsync(
                stoppingToken
            )
        )
        {
            try
            {
                TempChatStore.CleanupExpired();
            }
            catch
            {
                // Cleanup retries automatically next minute.
            }
        }
    }
}

public static class TempChatEndpoints
{
    private const string Alphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static void Register(
        WebApplication app
    )
    {
        TempChatStore.EnsureDatabase();

        app.MapPost(
            "/api/temp-chats/create",
            async (
                HttpContext ctx
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user = CurrentUser(ctx);

                if (
                    string.IsNullOrWhiteSpace(user)
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
                        "temp_chat_create",
                        5,
                        3600
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "För många skapade rum. Försök senare."
                        },
                        statusCode: 429
                    );
                }

                var request =
                    await ctx.Request
                        .ReadFromJsonAsync<
                            TempChatCreateRequest
                        >();

                if (request is null)
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "Inställningar saknas."
                        }
                    );
                }

                var durationMinutes =
                    Math.Clamp(
                        request.DurationMinutes,
                        15,
                        7 * 24 * 60
                    );

                var maxMembers =
                    Math.Clamp(
                        request.MaxMembers,
                        2,
                        25
                    );

                var roomId =
                    Convert
                        .ToHexString(
                            RandomNumberGenerator
                                .GetBytes(16)
                        )
                        .ToLowerInvariant();

                var inviteCode =
                    CreateUniqueInviteCode();

                var createdAt =
                    DateTime.UtcNow;

                var expiresAt =
                    createdAt.AddMinutes(
                        durationMinutes
                    );

                using var db =
                    DbHelpers.OpenDb();

                using var tx =
                    db.BeginTransaction();

                using (
                    var room =
                        db.CreateCommand()
                )
                {
                    room.Transaction = tx;

                    room.CommandText = """
                    INSERT INTO TempChats
                    (
                        Id,
                        InviteCode,
                        OwnerUsername,
                        CreatedAt,
                        ExpiresAt,
                        MaxMembers,
                        Closed
                    )
                    VALUES
                    (
                        $id,
                        $code,
                        $owner,
                        $created,
                        $expires,
                        $max,
                        0
                    )
                    """;

                    room.Parameters
                        .AddWithValue(
                            "$id",
                            roomId
                        );

                    room.Parameters
                        .AddWithValue(
                            "$code",
                            inviteCode
                        );

                    room.Parameters
                        .AddWithValue(
                            "$owner",
                            user
                        );

                    room.Parameters
                        .AddWithValue(
                            "$created",
                            createdAt.ToString("o")
                        );

                    room.Parameters
                        .AddWithValue(
                            "$expires",
                            expiresAt.ToString("o")
                        );

                    room.Parameters
                        .AddWithValue(
                            "$max",
                            maxMembers
                        );

                    room.ExecuteNonQuery();
                }

                using (
                    var member =
                        db.CreateCommand()
                )
                {
                    member.Transaction = tx;

                    member.CommandText = """
                    INSERT INTO TempChatMembers
                    (
                        RoomId,
                        Username,
                        JoinedAt
                    )
                    VALUES
                    (
                        $room,
                        $user,
                        $joined
                    )
                    """;

                    member.Parameters
                        .AddWithValue(
                            "$room",
                            roomId
                        );

                    member.Parameters
                        .AddWithValue(
                            "$user",
                            user
                        );

                    member.Parameters
                        .AddWithValue(
                            "$joined",
                            createdAt.ToString("o")
                        );

                    member.ExecuteNonQuery();
                }

                tx.Commit();

                return Results.Ok(
                    new
                    {
                        success = true,
                        roomId,
                        inviteCode,
                        owner = user,
                        maxMembers,
                        createdAt =
                            createdAt.ToString("o"),
                        expiresAt =
                            expiresAt.ToString("o")
                    }
                );
            }
        );

        app.MapPost(
            "/api/temp-chats/join",
            async (
                HttpContext ctx
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user = CurrentUser(ctx);

                if (
                    string.IsNullOrWhiteSpace(user)
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
                        "temp_chat_join",
                        10,
                        300
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "För många kodförsök. Vänta en stund."
                        },
                        statusCode: 429
                    );
                }

                var request =
                    await ctx.Request
                        .ReadFromJsonAsync<
                            TempChatJoinRequest
                        >();

                var inviteCode =
                    NormalizeCode(
                        request?.Code
                    );

                if (
                    string.IsNullOrWhiteSpace(
                        inviteCode
                    )
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "Ogiltig kod."
                        }
                    );
                }

                using var db =
                    DbHelpers.OpenDb();

                string roomId;
                string expiresAt;
                int maxMembers;

                using (
                    var find =
                        db.CreateCommand()
                )
                {
                    find.CommandText = """
                    SELECT
                        Id,
                        ExpiresAt,
                        MaxMembers
                    FROM TempChats
                    WHERE InviteCode=$code
                      AND Closed=0
                      AND ExpiresAt > $now
                    LIMIT 1
                    """;

                    find.Parameters
                        .AddWithValue(
                            "$code",
                            inviteCode
                        );

                    find.Parameters
                        .AddWithValue(
                            "$now",
                            DateTime.UtcNow
                                .ToString("o")
                        );

                    using var reader =
                        find.ExecuteReader();

                    if (!reader.Read())
                    {
                        return Results.NotFound(
                            new
                            {
                                error =
                                    "Koden finns inte eller har gått ut."
                            }
                        );
                    }

                    roomId =
                        reader.GetString(0);

                    expiresAt =
                        reader.GetString(1);

                    maxMembers =
                        reader.GetInt32(2);
                }

                using (
                    var existing =
                        db.CreateCommand()
                )
                {
                    existing.CommandText = """
                    SELECT COUNT(*)
                    FROM TempChatMembers
                    WHERE RoomId=$room
                      AND Username=$user
                    """;

                    existing.Parameters
                        .AddWithValue(
                            "$room",
                            roomId
                        );

                    existing.Parameters
                        .AddWithValue(
                            "$user",
                            user
                        );

                    if (
                        Convert.ToInt32(
                            existing.ExecuteScalar()
                        ) > 0
                    )
                    {
                        return Results.Ok(
                            new
                            {
                                success = true,
                                roomId,
                                expiresAt,
                                alreadyMember = true
                            }
                        );
                    }
                }

                using (
                    var count =
                        db.CreateCommand()
                )
                {
                    count.CommandText = """
                    SELECT COUNT(*)
                    FROM TempChatMembers
                    WHERE RoomId=$room
                    """;

                    count.Parameters
                        .AddWithValue(
                            "$room",
                            roomId
                        );

                    var currentMembers =
                        Convert.ToInt32(
                            count.ExecuteScalar()
                        );

                    if (
                        currentMembers >=
                        maxMembers
                    )
                    {
                        return Results.Json(
                            new
                            {
                                error =
                                    "Det tillfälliga rummet är fullt."
                            },
                            statusCode: 409
                        );
                    }
                }

                using (
                    var join =
                        db.CreateCommand()
                )
                {
                    join.CommandText = """
                    INSERT OR IGNORE
                    INTO TempChatMembers
                    (
                        RoomId,
                        Username,
                        JoinedAt
                    )
                    VALUES
                    (
                        $room,
                        $user,
                        $joined
                    )
                    """;

                    join.Parameters
                        .AddWithValue(
                            "$room",
                            roomId
                        );

                    join.Parameters
                        .AddWithValue(
                            "$user",
                            user
                        );

                    join.Parameters
                        .AddWithValue(
                            "$joined",
                            DateTime.UtcNow
                                .ToString("o")
                        );

                    join.ExecuteNonQuery();
                }

                return Results.Ok(
                    new
                    {
                        success = true,
                        roomId,
                        expiresAt
                    }
                );
            }
        );

        app.MapGet(
            "/api/temp-chats/mine",
            (
                HttpContext ctx
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user = CurrentUser(ctx);

                if (
                    string.IsNullOrWhiteSpace(user)
                )
                {
                    return Results.Unauthorized();
                }

                using var db =
                    DbHelpers.OpenDb();

                using var cmd =
                    db.CreateCommand();

                cmd.CommandText = """
                SELECT
                    r.Id,
                    r.InviteCode,
                    r.OwnerUsername,
                    r.CreatedAt,
                    r.ExpiresAt,
                    r.MaxMembers,
                    (
                        SELECT COUNT(*)
                        FROM TempChatMembers x
                        WHERE x.RoomId=r.Id
                    )
                FROM TempChats r
                INNER JOIN TempChatMembers m
                    ON m.RoomId=r.Id
                WHERE m.Username=$user
                  AND r.Closed=0
                  AND r.ExpiresAt > $now
                ORDER BY r.ExpiresAt ASC
                """;

                cmd.Parameters
                    .AddWithValue(
                        "$user",
                        user
                    );

                cmd.Parameters
                    .AddWithValue(
                        "$now",
                        DateTime.UtcNow
                            .ToString("o")
                    );

                using var reader =
                    cmd.ExecuteReader();

                var rooms =
                    new List<object>();

                while (reader.Read())
                {
                    rooms.Add(
                        new
                        {
                            roomId =
                                reader.GetString(0),

                            inviteCode =
                                reader.GetString(1),

                            owner =
                                reader.GetString(2),

                            createdAt =
                                reader.GetString(3),

                            expiresAt =
                                reader.GetString(4),

                            maxMembers =
                                reader.GetInt32(5),

                            memberCount =
                                reader.GetInt32(6)
                        }
                    );
                }

                return Results.Ok(
                    new
                    {
                        rooms
                    }
                );
            }
        );

        app.MapGet(
            "/api/temp-chats/{roomId}",
            (
                string roomId,
                HttpContext ctx
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user = CurrentUser(ctx);

                if (
                    string.IsNullOrWhiteSpace(user)
                )
                {
                    return Results.Unauthorized();
                }

                using var db =
                    DbHelpers.OpenDb();

                if (
                    !IsMember(
                        db,
                        roomId,
                        user
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Du är inte medlem i rummet."
                        },
                        statusCode: 403
                    );
                }

                using var cmd =
                    db.CreateCommand();

                cmd.CommandText = """
                SELECT
                    InviteCode,
                    OwnerUsername,
                    CreatedAt,
                    ExpiresAt,
                    MaxMembers
                FROM TempChats
                WHERE Id=$room
                  AND Closed=0
                  AND ExpiresAt > $now
                LIMIT 1
                """;

                cmd.Parameters
                    .AddWithValue(
                        "$room",
                        roomId
                    );

                cmd.Parameters
                    .AddWithValue(
                        "$now",
                        DateTime.UtcNow
                            .ToString("o")
                    );

                using var reader =
                    cmd.ExecuteReader();

                if (!reader.Read())
                {
                    return Results.NotFound(
                        new
                        {
                            error =
                                "Rummet finns inte längre."
                        }
                    );
                }

                var inviteCode =
                    reader.GetString(0);

                var owner =
                    reader.GetString(1);

                var createdAt =
                    reader.GetString(2);

                var expiresAt =
                    reader.GetString(3);

                var maxMembers =
                    reader.GetInt32(4);

                reader.Close();

                using var memberCmd =
                    db.CreateCommand();

                memberCmd.CommandText = """
                SELECT
                    Username,
                    JoinedAt
                FROM TempChatMembers
                WHERE RoomId=$room
                ORDER BY Id ASC
                """;

                memberCmd.Parameters
                    .AddWithValue(
                        "$room",
                        roomId
                    );

                using var memberReader =
                    memberCmd.ExecuteReader();

                var members =
                    new List<object>();

                while (
                    memberReader.Read()
                )
                {
                    members.Add(
                        new
                        {
                            username =
                                memberReader
                                    .GetString(0),

                            joinedAt =
                                memberReader
                                    .GetString(1)
                        }
                    );
                }

                return Results.Ok(
                    new
                    {
                        roomId,
                        inviteCode,
                        owner,
                        isOwner =
                            owner.Equals(
                                user,
                                StringComparison
                                    .OrdinalIgnoreCase
                            ),
                        createdAt,
                        expiresAt,
                        maxMembers,
                        memberCount =
                            members.Count,
                        members
                    }
                );
            }
        );

        app.MapGet(
            "/api/temp-chats/{roomId}/messages",
            (
                string roomId,
                long? after,
                HttpContext ctx
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user = CurrentUser(ctx);

                using var db =
                    DbHelpers.OpenDb();

                if (
                    string.IsNullOrWhiteSpace(user)
                    || !IsMember(
                        db,
                        roomId,
                        user
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Ingen åtkomst."
                        },
                        statusCode: 403
                    );
                }

                using var cmd =
                    db.CreateCommand();

                cmd.CommandText = """
                SELECT
                    Id,
                    FromUser,
                    Message,
                    CreatedAt
                FROM TempChatMessages
                WHERE RoomId=$room
                  AND Id > $after
                ORDER BY Id ASC
                LIMIT 500
                """;

                cmd.Parameters
                    .AddWithValue(
                        "$room",
                        roomId
                    );

                cmd.Parameters
                    .AddWithValue(
                        "$after",
                        Math.Max(
                            0,
                            after ?? 0
                        )
                    );

                using var reader =
                    cmd.ExecuteReader();

                var messages =
                    new List<object>();

                while (reader.Read())
                {
                    messages.Add(
                        new
                        {
                            id =
                                reader.GetInt64(0),

                            from =
                                reader.GetString(1),

                            text =
                                reader.GetString(2),

                            createdAt =
                                reader.GetString(3)
                        }
                    );
                }

                return Results.Ok(
                    new
                    {
                        messages
                    }
                );
            }
        );

        app.MapPost(
            "/api/temp-chats/{roomId}/messages",
            async (
                string roomId,
                HttpContext ctx
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user = CurrentUser(ctx);

                if (
                    string.IsNullOrWhiteSpace(user)
                )
                {
                    return Results.Unauthorized();
                }

                using var db =
                    DbHelpers.OpenDb();

                if (
                    !IsMember(
                        db,
                        roomId,
                        user
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Ingen åtkomst."
                        },
                        statusCode: 403
                    );
                }

                var limiter =
                    ctx.RequestServices
                        .GetRequiredService<RateLimiter>();

                if (
                    !limiter.IsAllowed(
                        user,
                        "temp_chat_send",
                        60,
                        60
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Du skickar för snabbt."
                        },
                        statusCode: 429
                    );
                }

                var request =
                    await ctx.Request
                        .ReadFromJsonAsync<
                            TempChatSendRequest
                        >();

                var text =
                    (
                        request?.Text
                        ?? ""
                    ).Trim();

                if (
                    string.IsNullOrWhiteSpace(
                        text
                    )
                    || text.Length > 4000
                )
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "Meddelandet är tomt eller för långt."
                        }
                    );
                }

                var createdAt =
                    DateTime.UtcNow
                        .ToString("o");

                long id;

                using (
                    var cmd =
                        db.CreateCommand()
                )
                {
                    cmd.CommandText = """
                    INSERT INTO TempChatMessages
                    (
                        RoomId,
                        FromUser,
                        Message,
                        CreatedAt
                    )
                    VALUES
                    (
                        $room,
                        $user,
                        $message,
                        $created
                    );

                    SELECT last_insert_rowid();
                    """;

                    cmd.Parameters
                        .AddWithValue(
                            "$room",
                            roomId
                        );

                    cmd.Parameters
                        .AddWithValue(
                            "$user",
                            user
                        );

                    cmd.Parameters
                        .AddWithValue(
                            "$message",
                            text
                        );

                    cmd.Parameters
                        .AddWithValue(
                            "$created",
                            createdAt
                        );

                    id =
                        Convert.ToInt64(
                            cmd.ExecuteScalar()
                        );
                }

                return Results.Ok(
                    new
                    {
                        success = true,
                        message =
                            new
                            {
                                id,
                                from = user,
                                text,
                                createdAt
                            }
                    }
                );
            }
        );

        app.MapPost(
            "/api/temp-chats/{roomId}/rotate-code",
            (
                string roomId,
                HttpContext ctx
            ) =>
            {
                TempChatStore.CleanupExpired();

                var user = CurrentUser(ctx);

                using var db =
                    DbHelpers.OpenDb();

                if (
                    !IsOwner(
                        db,
                        roomId,
                        user
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Endast ägaren kan byta kod."
                        },
                        statusCode: 403
                    );
                }

                var inviteCode =
                    CreateUniqueInviteCode();

                using var cmd =
                    db.CreateCommand();

                cmd.CommandText = """
                UPDATE TempChats
                SET InviteCode=$code
                WHERE Id=$room
                """;

                cmd.Parameters
                    .AddWithValue(
                        "$code",
                        inviteCode
                    );

                cmd.Parameters
                    .AddWithValue(
                        "$room",
                        roomId
                    );

                cmd.ExecuteNonQuery();

                return Results.Ok(
                    new
                    {
                        success = true,
                        inviteCode
                    }
                );
            }
        );

        app.MapPost(
            "/api/temp-chats/{roomId}/leave",
            (
                string roomId,
                HttpContext ctx
            ) =>
            {
                var user = CurrentUser(ctx);

                using var db =
                    DbHelpers.OpenDb();

                if (
                    IsOwner(
                        db,
                        roomId,
                        user
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Ägaren måste radera rummet."
                        },
                        statusCode: 409
                    );
                }

                using var cmd =
                    db.CreateCommand();

                cmd.CommandText = """
                DELETE FROM TempChatMembers
                WHERE RoomId=$room
                  AND Username=$user
                """;

                cmd.Parameters
                    .AddWithValue(
                        "$room",
                        roomId
                    );

                cmd.Parameters
                    .AddWithValue(
                        "$user",
                        user
                    );

                cmd.ExecuteNonQuery();

                return Results.Ok(
                    new
                    {
                        success = true
                    }
                );
            }
        );

        app.MapDelete(
            "/api/temp-chats/{roomId}",
            (
                string roomId,
                HttpContext ctx
            ) =>
            {
                var user = CurrentUser(ctx);

                using var db =
                    DbHelpers.OpenDb();

                if (
                    !IsOwner(
                        db,
                        roomId,
                        user
                    )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Endast ägaren kan radera rummet."
                        },
                        statusCode: 403
                    );
                }

                using var tx =
                    db.BeginTransaction();

                foreach (
                    var sql in new[]
                    {
                        """
                        DELETE FROM TempChatMessages
                        WHERE RoomId=$room
                        """,

                        """
                        DELETE FROM TempChatMembers
                        WHERE RoomId=$room
                        """,

                        """
                        DELETE FROM TempChats
                        WHERE Id=$room
                        """
                    }
                )
                {
                    using var cmd =
                        db.CreateCommand();

                    cmd.Transaction = tx;

                    cmd.CommandText = sql;

                    cmd.Parameters
                        .AddWithValue(
                            "$room",
                            roomId
                        );

                    cmd.ExecuteNonQuery();
                }

                tx.Commit();

                return Results.Ok(
                    new
                    {
                        success = true
                    }
                );
            }
        );
    }

    private static string CurrentUser(
        HttpContext ctx
    )
    {
        var raw =
            (
                ctx.User.Identity
                    ?.Name
                ?? ""
            )
            .Trim();

        if (
            string.IsNullOrWhiteSpace(
                raw
            )
        )
        {
            return "";
        }

        try
        {
            using var db =
                DbHelpers.OpenDb();

            using var cmd =
                db.CreateCommand();

            cmd.CommandText = """
            SELECT Username

            FROM AuthUsers

            WHERE
                LOWER(Username)
                = LOWER($username)

            LIMIT 1
            """;

            cmd.Parameters
                .AddWithValue(
                    "$username",
                    raw
                );

            var canonical =
                Convert.ToString(
                    cmd.ExecuteScalar()
                )
                ?.Trim()
                ?? "";

            return
                string.IsNullOrWhiteSpace(
                    canonical
                )
                    ? raw
                    : canonical;
        }
        catch
        {
            return raw;
        }
    }

    private static bool IsMember(
        Microsoft.Data.Sqlite.SqliteConnection db,
        string roomId,
        string username
    )
    {
        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
        SELECT COUNT(*)
        FROM TempChatMembers m
        INNER JOIN TempChats r
            ON r.Id=m.RoomId
        WHERE m.RoomId=$room
          AND m.Username=$user
          AND r.Closed=0
          AND r.ExpiresAt > $now
        """;

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        cmd.Parameters
            .AddWithValue(
                "$user",
                username
            );

        cmd.Parameters
            .AddWithValue(
                "$now",
                DateTime.UtcNow
                    .ToString("o")
            );

        return
            Convert.ToInt32(
                cmd.ExecuteScalar()
            ) > 0;
    }

    private static bool IsOwner(
        Microsoft.Data.Sqlite.SqliteConnection db,
        string roomId,
        string username
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                username
            )
        )
        {
            return false;
        }

        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
        SELECT COUNT(*)
        FROM TempChats
        WHERE Id=$room
          AND OwnerUsername=$user
          AND Closed=0
          AND ExpiresAt > $now
        """;

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        cmd.Parameters
            .AddWithValue(
                "$user",
                username
            );

        cmd.Parameters
            .AddWithValue(
                "$now",
                DateTime.UtcNow
                    .ToString("o")
            );

        return
            Convert.ToInt32(
                cmd.ExecuteScalar()
            ) > 0;
    }

    private static string CreateUniqueInviteCode()
    {
        for (
            var attempt = 0;
            attempt < 50;
            attempt++
        )
        {
            Span<char> value =
                stackalloc char[8];

            Span<byte> bytes =
                stackalloc byte[8];

            RandomNumberGenerator.Fill(
                bytes
            );

            for (
                var i = 0;
                i < value.Length;
                i++
            )
            {
                value[i] =
                    Alphabet[
                        bytes[i]
                        % Alphabet.Length
                    ];
            }

            var code =
                $"RS-{new string(value[..4])}-{new string(value[4..])}";

            using var db =
                DbHelpers.OpenDb();

            using var cmd =
                db.CreateCommand();

            cmd.CommandText = """
            SELECT COUNT(*)
            FROM TempChats
            WHERE InviteCode=$code
            """;

            cmd.Parameters
                .AddWithValue(
                    "$code",
                    code
                );

            if (
                Convert.ToInt32(
                    cmd.ExecuteScalar()
                ) == 0
            )
            {
                return code;
            }
        }

        throw new InvalidOperationException(
            "Could not create invite code."
        );
    }

    private static string NormalizeCode(
        string? value
    )
    {
        var compact =
            Regex.Replace(
                (
                    value
                    ?? ""
                )
                .Trim()
                .ToUpperInvariant(),
                "[^A-Z0-9]",
                ""
            );

        if (
            compact.StartsWith(
                "RS",
                StringComparison.Ordinal
            )
        )
        {
            compact =
                compact[2..];
        }

        if (
            compact.Length != 8
        )
        {
            return "";
        }

        return
            "RS-"
            + compact[..4]
            + "-"
            + compact[4..];
    }
}
