using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using System;
using System.Collections.Generic;


public static class ChatReadRoutes
{
    public static void Register(
        WebApplication app
    )
    {
        EnsureTables();


        // Returns unread counts grouped by sender.
        app.MapGet(
            "/api/chat/unread-counts",

            (
                HttpContext ctx
            ) =>
            {
                var username =
                    Normalize(

                        ctx.User
                            .Identity
                            ?.Name
                    );


                if (
                    username.Length == 0
                )
                {
                    return Results.Unauthorized();
                }


                using var db =
                    DbHelpers.OpenDb();


                // Existing conversations become the initial baseline.
                // This prevents every old message from suddenly appearing unread.
                EnsureInitialReadState(
                    db,
                    username
                );


                var result =
                    new List<object>();


                using var command =
                    db.CreateCommand();


                command.CommandText = @"
                    SELECT
                        m.FromUser,
                        COUNT(*)

                    FROM ChatMessages m

                    LEFT JOIN ChatReadState r

                        ON r.Username = $username

                        AND r.Peer = m.FromUser

                    WHERE

                        m.ToUser = $username

                        AND m.FromUser <> $username

                        AND m.Id >
                            COALESCE(
                                r.LastReadMessageId,
                                0
                            )

                    GROUP BY
                        m.FromUser

                    ORDER BY
                        MAX(
                            m.Id
                        )
                        DESC
                ";


                command.Parameters
                    .AddWithValue(
                        "$username",
                        username
                    );


                using var reader =
                    command.ExecuteReader();


                while (
                    reader.Read()
                )
                {
                    var peer =
                        reader.GetString(
                            0
                        );


                    var unreadCount =
                        reader.GetInt64(
                            1
                        );


                    result.Add(

                        new
                        {
                            peer,
                            unreadCount
                        }
                    );
                }


                return Results.Ok(
                    result
                );
            }
        );


        // Marks all currently received messages from one peer as read.
        app.MapPost(
            "/api/chat/read/{peer}",

            (
                string peer,
                HttpContext ctx
            ) =>
            {
                var username =
                    Normalize(

                        ctx.User
                            .Identity
                            ?.Name
                    );


                var normalizedPeer =
                    Normalize(
                        peer
                    );


                if (
                    username.Length == 0
                )
                {
                    return Results.Unauthorized();
                }


                if (
                    normalizedPeer.Length == 0
                )
                {
                    return Results.BadRequest(

                        new
                        {
                            message =
                                "Invalid username."
                        }
                    );
                }


                using var db =
                    DbHelpers.OpenDb();


                long lastReadMessageId;


                using (
                    var latest =
                        db.CreateCommand()
                )
                {
                    latest.CommandText = @"
                        SELECT
                            COALESCE(
                                MAX(
                                    Id
                                ),
                                0
                            )

                        FROM ChatMessages

                        WHERE

                            FromUser = $peer

                            AND ToUser = $username
                    ";


                    latest.Parameters
                        .AddWithValue(
                            "$peer",
                            normalizedPeer
                        );


                    latest.Parameters
                        .AddWithValue(
                            "$username",
                            username
                        );


                    lastReadMessageId =
                        Convert.ToInt64(

                            latest.ExecuteScalar()
                            ?? 0L
                        );
                }


                using var update =
                    db.CreateCommand();


                update.CommandText = @"
                    INSERT INTO ChatReadState
                    (
                        Username,
                        Peer,
                        LastReadMessageId,
                        UpdatedAt
                    )

                    VALUES
                    (
                        $username,
                        $peer,
                        $lastReadMessageId,
                        $updatedAt
                    )

                    ON CONFLICT
                    (
                        Username,
                        Peer
                    )

                    DO UPDATE SET

                        LastReadMessageId =
                            MAX(
                                ChatReadState
                                    .LastReadMessageId,

                                excluded
                                    .LastReadMessageId
                            ),

                        UpdatedAt =
                            excluded.UpdatedAt
                ";


                update.Parameters
                    .AddWithValue(
                        "$username",
                        username
                    );


                update.Parameters
                    .AddWithValue(
                        "$peer",
                        normalizedPeer
                    );


                update.Parameters
                    .AddWithValue(
                        "$lastReadMessageId",
                        lastReadMessageId
                    );


                update.Parameters
                    .AddWithValue(

                        "$updatedAt",

                        DateTime.UtcNow
                            .ToString(
                                "o"
                            )
                    );


                update.ExecuteNonQuery();


                return Results.Ok(

                    new
                    {
                        success = true,

                        peer =
                            normalizedPeer,

                        lastReadMessageId
                    }
                );
            }
        );
    }


    private static void EnsureInitialReadState(
        Microsoft.Data.Sqlite.SqliteConnection db,
        string username
    )
    {
        using (
            var check =
                db.CreateCommand()
        )
        {
            check.CommandText = @"
                SELECT 1

                FROM ChatReadInitialization

                WHERE Username = $username

                LIMIT 1
            ";


            check.Parameters
                .AddWithValue(
                    "$username",
                    username
                );


            if (
                check.ExecuteScalar()
                != null
            )
            {
                return;
            }
        }


        using var transaction =
            db.BeginTransaction();


        try
        {
            var conversations =
                new List<(
                    string Peer,
                    long LastMessageId
                )>();


            using (
                var query =
                    db.CreateCommand()
            )
            {
                query.Transaction =
                    transaction;


                query.CommandText = @"
                    SELECT

                        FromUser,

                        MAX(
                            Id
                        )

                    FROM ChatMessages

                    WHERE

                        ToUser = $username

                        AND FromUser <> $username

                    GROUP BY
                        FromUser
                ";


                query.Parameters
                    .AddWithValue(
                        "$username",
                        username
                    );


                using var reader =
                    query.ExecuteReader();


                while (
                    reader.Read()
                )
                {
                    conversations.Add(

                        (
                            reader.GetString(
                                0
                            ),

                            reader.GetInt64(
                                1
                            )
                        )
                    );
                }
            }


            foreach (
                var conversation

                in conversations
            )
            {
                using var insert =
                    db.CreateCommand();


                insert.Transaction =
                    transaction;


                insert.CommandText = @"
                    INSERT OR IGNORE INTO ChatReadState
                    (
                        Username,
                        Peer,
                        LastReadMessageId,
                        UpdatedAt
                    )

                    VALUES
                    (
                        $username,
                        $peer,
                        $lastMessageId,
                        $updatedAt
                    )
                ";


                insert.Parameters
                    .AddWithValue(
                        "$username",
                        username
                    );


                insert.Parameters
                    .AddWithValue(
                        "$peer",
                        conversation.Peer
                    );


                insert.Parameters
                    .AddWithValue(

                        "$lastMessageId",

                        conversation
                            .LastMessageId
                    );


                insert.Parameters
                    .AddWithValue(

                        "$updatedAt",

                        DateTime.UtcNow
                            .ToString(
                                "o"
                            )
                    );


                insert.ExecuteNonQuery();
            }


            using var initialized =
                db.CreateCommand();


            initialized.Transaction =
                transaction;


            initialized.CommandText = @"
                INSERT OR IGNORE INTO
                    ChatReadInitialization
                (
                    Username,
                    InitializedAt
                )

                VALUES
                (
                    $username,
                    $initializedAt
                )
            ";


            initialized.Parameters
                .AddWithValue(
                    "$username",
                    username
                );


            initialized.Parameters
                .AddWithValue(

                    "$initializedAt",

                    DateTime.UtcNow
                        .ToString(
                            "o"
                        )
                );


            initialized.ExecuteNonQuery();


            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();

            throw;
        }
    }


    private static void EnsureTables()
    {
        using var db =
            DbHelpers.OpenDb();


        using var command =
            db.CreateCommand();


        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ChatReadInitialization
            (
                Username TEXT PRIMARY KEY,

                InitializedAt
                    TEXT
                    NOT NULL
            );


            CREATE TABLE IF NOT EXISTS ChatReadState
            (
                Username TEXT NOT NULL,

                Peer TEXT NOT NULL,

                LastReadMessageId
                    INTEGER
                    NOT NULL
                    DEFAULT 0,

                UpdatedAt
                    TEXT
                    NOT NULL,

                PRIMARY KEY
                (
                    Username,
                    Peer
                )
            );


            CREATE INDEX IF NOT EXISTS
                IX_ChatMessages_To_From_Id

            ON ChatMessages
            (
                ToUser,
                FromUser,
                Id
            );
        ";


        command.ExecuteNonQuery();
    }


    private static string Normalize(
        string? value
    )
    {
        return (
            value
            ?? ""
        )
            .Trim()
            .ToLowerInvariant();
    }
}
