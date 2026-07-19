using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using System;
using System.Security.Cryptography;


public static class GroupInviteLinks
{
    private const string PublicBaseUrl =
        "https://runspace.cloud/group-invite/";


    public static void Register(
        WebApplication app
    )
    {
        EnsureTable();


        // Get current active invite link.
        app.MapGet(
            "/api/groups/{groupId}/invite-link",

            (
                string groupId,
                HttpContext ctx
            ) =>
            {
                var username =
                    ctx.User
                        .Identity
                        ?.Name
                        ?.Trim()
                        .ToLowerInvariant();


                if (
                    string.IsNullOrWhiteSpace(
                        username
                    )
                )
                {
                    return Results.Unauthorized();
                }


                var gid =
                    (
                        groupId
                        ?? ""
                    )
                    .Trim()
                    .ToLowerInvariant();


                if (
                    !GroupHelpers.IsOwnerOrAdmin(
                        gid,
                        username
                    )
                )
                {
                    return Results.Forbid();
                }


                using var db =
                    DbHelpers.OpenDb();


                using var command =
                    db.CreateCommand();


                command.CommandText = @"
                    SELECT Code
                    FROM GroupInviteLinks
                    WHERE GroupId = $gid
                      AND Revoked = 0
                    ORDER BY Id DESC
                    LIMIT 1
                ";


                command.Parameters
                    .AddWithValue(
                        "$gid",
                        gid
                    );


                var code =
                    command.ExecuteScalar()
                    as string
                    ?? "";


                if (
                    string.IsNullOrWhiteSpace(
                        code
                    )
                )
                {
                    return Results.Ok(
                        new
                        {
                            exists = false
                        }
                    );
                }


                return Results.Ok(
                    new
                    {
                        exists = true,
                        code,
                        url =
                            PublicBaseUrl
                            + code
                    }
                );
            }
        );


        // Create or return an active invite link.
        app.MapPost(
            "/api/groups/{groupId}/invite-link",

            (
                string groupId,
                HttpContext ctx
            ) =>
            {
                var username =
                    ctx.User
                        .Identity
                        ?.Name
                        ?.Trim()
                        .ToLowerInvariant();


                if (
                    string.IsNullOrWhiteSpace(
                        username
                    )
                )
                {
                    return Results.Unauthorized();
                }


                var gid =
                    (
                        groupId
                        ?? ""
                    )
                    .Trim()
                    .ToLowerInvariant();


                if (
                    !GroupHelpers.IsOwnerOrAdmin(
                        gid,
                        username
                    )
                )
                {
                    return Results.Forbid();
                }


                using var db =
                    DbHelpers.OpenDb();


                using (
                    var existing =
                        db.CreateCommand()
                )
                {
                    existing.CommandText = @"
                        SELECT Code
                        FROM GroupInviteLinks
                        WHERE GroupId = $gid
                          AND Revoked = 0
                        ORDER BY Id DESC
                        LIMIT 1
                    ";


                    existing.Parameters
                        .AddWithValue(
                            "$gid",
                            gid
                        );


                    var currentCode =
                        existing.ExecuteScalar()
                        as string;


                    if (
                        !string.IsNullOrWhiteSpace(
                            currentCode
                        )
                    )
                    {
                        return Results.Ok(
                            new
                            {
                                success = true,
                                code = currentCode,
                                url =
                                    PublicBaseUrl
                                    + currentCode
                            }
                        );
                    }
                }


                var code =
                    CreateCode();


                using var insert =
                    db.CreateCommand();


                insert.CommandText = @"
                    INSERT INTO GroupInviteLinks
                    (
                        GroupId,
                        Code,
                        CreatedBy,
                        CreatedAt,
                        Revoked
                    )
                    VALUES
                    (
                        $gid,
                        $code,
                        $createdBy,
                        $createdAt,
                        0
                    )
                ";


                insert.Parameters
                    .AddWithValue(
                        "$gid",
                        gid
                    );


                insert.Parameters
                    .AddWithValue(
                        "$code",
                        code
                    );


                insert.Parameters
                    .AddWithValue(
                        "$createdBy",
                        username
                    );


                insert.Parameters
                    .AddWithValue(
                        "$createdAt",
                        DateTime.UtcNow
                            .ToString(
                                "o"
                            )
                    );


                insert.ExecuteNonQuery();


                return Results.Ok(
                    new
                    {
                        success = true,
                        code,
                        url =
                            PublicBaseUrl
                            + code
                    }
                );
            }
        );


        // Revoke the active link.
        app.MapDelete(
            "/api/groups/{groupId}/invite-link",

            (
                string groupId,
                HttpContext ctx
            ) =>
            {
                var username =
                    ctx.User
                        .Identity
                        ?.Name
                        ?.Trim()
                        .ToLowerInvariant();


                if (
                    string.IsNullOrWhiteSpace(
                        username
                    )
                )
                {
                    return Results.Unauthorized();
                }


                var gid =
                    (
                        groupId
                        ?? ""
                    )
                    .Trim()
                    .ToLowerInvariant();


                if (
                    !GroupHelpers.IsOwnerOrAdmin(
                        gid,
                        username
                    )
                )
                {
                    return Results.Forbid();
                }


                using var db =
                    DbHelpers.OpenDb();


                using var command =
                    db.CreateCommand();


                command.CommandText = @"
                    UPDATE GroupInviteLinks
                    SET Revoked = 1
                    WHERE GroupId = $gid
                      AND Revoked = 0
                ";


                command.Parameters
                    .AddWithValue(
                        "$gid",
                        gid
                    );


                var changed =
                    command.ExecuteNonQuery();


                return Results.Ok(
                    new
                    {
                        success = true,
                        revoked = changed
                    }
                );
            }
        );


        // Public invite preview.
        app.MapGet(
            "/api/group-invites/{code}",

            (
                string code
            ) =>
            {
                var inviteCode =
                    (
                        code
                        ?? ""
                    )
                    .Trim()
                    .ToLowerInvariant();


                if (
                    inviteCode.Length
                    < 16
                )
                {
                    return Results.NotFound(
                        new
                        {
                            message =
                                "Invite not found."
                        }
                    );
                }


                using var db =
                    DbHelpers.OpenDb();


                using var invite =
                    db.CreateCommand();


                invite.CommandText = @"
                    SELECT GroupId
                    FROM GroupInviteLinks
                    WHERE Code = $code
                      AND Revoked = 0
                    LIMIT 1
                ";


                invite.Parameters
                    .AddWithValue(
                        "$code",
                        inviteCode
                    );


                var gid =
                    invite.ExecuteScalar()
                    as string;


                if (
                    string.IsNullOrWhiteSpace(
                        gid
                    )
                )
                {
                    return Results.NotFound(
                        new
                        {
                            message =
                                "Invite not found."
                        }
                    );
                }


                using var group =
                    db.CreateCommand();


                group.CommandText = @"
                    SELECT
                        Name,
                        Description
                    FROM Groups
                    WHERE GroupId = $gid
                    LIMIT 1
                ";


                group.Parameters
                    .AddWithValue(
                        "$gid",
                        gid
                    );


                using var reader =
                    group.ExecuteReader();


                if (
                    !reader.Read()
                )
                {
                    return Results.NotFound(
                        new
                        {
                            message =
                                "Group not found."
                        }
                    );
                }


                var name =
                    reader.GetString(
                        0
                    );


                var description =
                    reader.IsDBNull(
                        1
                    )

                    ? ""

                    : reader.GetString(
                        1
                    );


                reader.Close();


                using var count =
                    db.CreateCommand();


                count.CommandText = @"
                    SELECT COUNT(*)
                    FROM GroupMembers
                    WHERE GroupId = $gid
                ";


                count.Parameters
                    .AddWithValue(
                        "$gid",
                        gid
                    );


                var memberCount =
                    Convert.ToInt32(
                        count.ExecuteScalar()
                    );


                return Results.Ok(
                    new
                    {
                        code =
                            inviteCode,

                        groupId =
                            gid,

                        name,

                        description,

                        memberCount
                    }
                );
            }
        );


        // Join through invite link.
        app.MapPost(
            "/api/group-invites/{code}/join",

            (
                string code,
                HttpContext ctx
            ) =>
            {
                var username =
                    ctx.User
                        .Identity
                        ?.Name
                        ?.Trim()
                        .ToLowerInvariant();


                if (
                    string.IsNullOrWhiteSpace(
                        username
                    )
                )
                {
                    return Results.Unauthorized();
                }


                var inviteCode =
                    (
                        code
                        ?? ""
                    )
                    .Trim()
                    .ToLowerInvariant();


                using var db =
                    DbHelpers.OpenDb();


                using var invite =
                    db.CreateCommand();


                invite.CommandText = @"
                    SELECT GroupId
                    FROM GroupInviteLinks
                    WHERE Code = $code
                      AND Revoked = 0
                    LIMIT 1
                ";


                invite.Parameters
                    .AddWithValue(
                        "$code",
                        inviteCode
                    );


                var gid =
                    invite.ExecuteScalar()
                    as string;


                if (
                    string.IsNullOrWhiteSpace(
                        gid
                    )
                )
                {
                    return Results.NotFound(
                        new
                        {
                            message =
                                "Invite not found."
                        }
                    );
                }


                using var insert =
                    db.CreateCommand();


                insert.CommandText = @"
                    INSERT OR IGNORE INTO GroupMembers
                    (
                        GroupId,
                        Username,
                        Role,
                        JoinedAt
                    )
                    VALUES
                    (
                        $gid,
                        $username,
                        'member',
                        $joinedAt
                    )
                ";


                insert.Parameters
                    .AddWithValue(
                        "$gid",
                        gid
                    );


                insert.Parameters
                    .AddWithValue(
                        "$username",
                        username
                    );


                insert.Parameters
                    .AddWithValue(
                        "$joinedAt",
                        DateTime.UtcNow
                            .ToString(
                                "o"
                            )
                    );


                var added =
                    insert.ExecuteNonQuery();


                using var group =
                    db.CreateCommand();


                group.CommandText = @"
                    SELECT Name
                    FROM Groups
                    WHERE GroupId = $gid
                    LIMIT 1
                ";


                group.Parameters
                    .AddWithValue(
                        "$gid",
                        gid
                    );


                var groupName =
                    group.ExecuteScalar()
                    as string
                    ?? "Group";


                return Results.Ok(
                    new
                    {
                        success = true,

                        alreadyMember =
                            added == 0,

                        groupId =
                            gid,

                        name =
                            groupName
                    }
                );
            }
        );
    }


    private static void EnsureTable()
    {
        using var db =
            DbHelpers.OpenDb();


        using var command =
            db.CreateCommand();


        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS GroupInviteLinks
            (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,

                GroupId TEXT NOT NULL,

                Code TEXT NOT NULL UNIQUE,

                CreatedBy TEXT NOT NULL,

                CreatedAt TEXT NOT NULL,

                Revoked INTEGER NOT NULL DEFAULT 0
            );


            CREATE INDEX IF NOT EXISTS
                IX_GroupInviteLinks_Group

            ON GroupInviteLinks
            (
                GroupId,
                Revoked
            );


            CREATE INDEX IF NOT EXISTS
                IX_GroupInviteLinks_Code

            ON GroupInviteLinks
            (
                Code
            );
        ";


        command.ExecuteNonQuery();
    }


    private static string CreateCode()
    {
        return Convert
            .ToHexString(
                RandomNumberGenerator
                    .GetBytes(
                        12
                    )
            )
            .ToLowerInvariant();
    }
}
