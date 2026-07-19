using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

public sealed record GroupDmCreateRequest(
    string Name,
    List<string>? Members = null
);

public sealed record GroupDmAddMemberRequest(
    string Username
);

public sealed record GroupDmSendRequest(
    string? Text = null,
    string? AttachmentUrl = null,
    string? AttachmentName = null,
    string? AttachmentMime = null,
    long? AttachmentSize = null,
    long? ReplyToId = null
);

public sealed record GroupDmUpdateRequest(
    string? Name = null,
    string? AvatarUrl = null
);

public sealed record GroupDmTransferOwnerRequest(
    string Username
);

public static class GroupDmStore
{
    public const int MaxMembers = 15;

    public static void EnsureDatabase()
    {
        using var db = DbHelpers.OpenDb();

        using var cmd = db.CreateCommand();

        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS GroupDmRooms
        (
            RoomId        TEXT PRIMARY KEY,
            Name          TEXT NOT NULL,
            OwnerUsername TEXT NOT NULL,
            AvatarUrl     TEXT NOT NULL DEFAULT '',
            CreatedAt     TEXT NOT NULL,
            UpdatedAt     TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS GroupDmMembers
        (
            Id                    INTEGER PRIMARY KEY AUTOINCREMENT,
            RoomId                TEXT NOT NULL,
            Username              TEXT NOT NULL,
            JoinedAt              TEXT NOT NULL,
            LastReadMessageId     INTEGER NOT NULL DEFAULT 0,
            VisibleAfterMessageId INTEGER NOT NULL DEFAULT 0,

            UNIQUE(RoomId, Username),

            FOREIGN KEY(RoomId)
                REFERENCES GroupDmRooms(RoomId)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS GroupDmMessages
        (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            RoomId         TEXT NOT NULL,
            FromUser       TEXT NOT NULL,
            Message        TEXT NOT NULL DEFAULT '',
            MessageType    TEXT NOT NULL DEFAULT 'text',
            AttachmentUrl  TEXT NOT NULL DEFAULT '',
            AttachmentName TEXT NOT NULL DEFAULT '',
            AttachmentMime TEXT NOT NULL DEFAULT '',
            AttachmentSize INTEGER NOT NULL DEFAULT 0,
            CreatedAt      TEXT NOT NULL,
            ReplyToId      INTEGER NOT NULL DEFAULT 0,

            FOREIGN KEY(RoomId)
                REFERENCES GroupDmRooms(RoomId)
                ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS
            IX_GroupDmMembers_User
        ON GroupDmMembers
        (
            Username,
            RoomId
        );

        CREATE INDEX IF NOT EXISTS
            IX_GroupDmMembers_Room
        ON GroupDmMembers
        (
            RoomId,
            Id
        );

        CREATE INDEX IF NOT EXISTS
            IX_GroupDmMessages_Room_Id
        ON GroupDmMessages
        (
            RoomId,
            Id
        );

        CREATE INDEX IF NOT EXISTS
            IX_GroupDmRooms_Updated
        ON GroupDmRooms
        (
            UpdatedAt
        );
        """;

        cmd.ExecuteNonQuery();
    }
}

public static class GroupDmEndpoints
{
    private sealed record RoomInfo(
        string RoomId,
        string Name,
        string Owner,
        string AvatarUrl,
        string CreatedAt,
        string UpdatedAt
    );

    private sealed record MembershipInfo(
        long LastReadMessageId,
        long VisibleAfterMessageId
    );

    private sealed record RoomListRow(
        RoomInfo Room,
        long LastReadMessageId,
        long VisibleAfterMessageId
    );

    public static void Register(
        WebApplication app
    )
    {
        GroupDmStore.EnsureDatabase();

        var routes =
            app.MapGroup(
                "/api/group-dms"
            )
            .RequireAuthorization();

        routes.MapPost(
            "/create",
            CreateRoom
        );

        routes.MapGet(
            "/mine",
            GetMyRooms
        );

        routes.MapGet(
            "/{roomId}",
            GetRoom
        );

        routes.MapPatch(
            "/{roomId}",
            UpdateRoom
        );

        routes.MapDelete(
            "/{roomId}",
            DeleteRoom
        );

        routes.MapGet(
            "/{roomId}/messages",
            GetMessages
        );

        routes.MapPost(
            "/{roomId}/messages",
            SendMessage
        );

        routes.MapPost(
            "/{roomId}/read",
            MarkRead
        );

        routes.MapPost(
            "/{roomId}/members",
            AddMember
        );

        routes.MapDelete(
            "/{roomId}/members/{username}",
            RemoveMember
        );

        routes.MapPost(
            "/{roomId}/leave",
            LeaveRoom
        );

        routes.MapPost(
            "/{roomId}/transfer-owner",
            TransferOwner
        );
    }

    private static async Task<IResult> CreateRoom(
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        var limiter =
            ctx.RequestServices
                .GetRequiredService<RateLimiter>();

        if (
            !limiter.IsAllowed(
                me,
                "group_dm_create",
                5,
                3600
            )
        )
        {
            return Results.Json(
                new
                {
                    message =
                        "Du skapar grupp-DMs för snabbt."
                },
                statusCode:
                    StatusCodes
                        .Status429TooManyRequests
            );
        }

        var req =
            await ctx.Request
                .ReadFromJsonAsync<GroupDmCreateRequest>();

        if (
            req == null
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Body saknas."
                }
            );
        }

        var name =
            CleanRoomName(
                req.Name
            );

        if (
            name.Length < 2
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Gruppnamnet måste innehålla minst 2 tecken."
                }
            );
        }

        if (
            ContentFilter
                .IsOffensive(
                    name
                )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Gruppnamnet är inte tillåtet."
                }
            );
        }

        var requested =
            (
                req.Members
                ?? new List<string>()
            )
            .Select(
                NormalizeUsername
            )
            .Where(
                username =>
                    !string.IsNullOrWhiteSpace(
                        username
                    )
                    &&
                    !username.Equals(
                        me,
                        StringComparison
                            .OrdinalIgnoreCase
                    )
            )
            .Distinct(
                StringComparer
                    .OrdinalIgnoreCase
            )
            .ToList();

        if (
            requested.Count < 1
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Välj minst en annan medlem."
                }
            );
        }

        if (
            requested.Count + 1
            >
            GroupDmStore.MaxMembers
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        $"En grupp-DM får ha högst {GroupDmStore.MaxMembers} medlemmar inklusive ägaren."
                }
            );
        }

        foreach (
            var target
            in requested
        )
        {
            if (
                !AppHelpers
                    .IsValidUsername(
                        target
                    )
                ||
                !AppHelpers
                    .UserExists(
                        target
                    )
            )
            {
                return Results.BadRequest(
                    new
                    {
                        message =
                            $"Användaren '{target}' finns inte."
                    }
                );
            }
        }

        var roomId =
            Convert
                .ToHexString(
                    RandomNumberGenerator
                        .GetBytes(
                            16
                        )
                )
                .ToLowerInvariant();

        var now =
            DateTime.UtcNow
                .ToString(
                    "o"
                );

        var allMembers =
            new List<string>
            {
                me
            };

        allMembers.AddRange(
            requested
        );

        long systemMessageId;

        using (
            var db =
                DbHelpers.OpenDb()
        )
        using (
            var tx =
                db.BeginTransaction()
        )
        {
            using (
                var room =
                    NewCommand(
                        db,
                        tx,
                        """
                        INSERT INTO GroupDmRooms
                        (
                            RoomId,
                            Name,
                            OwnerUsername,
                            AvatarUrl,
                            CreatedAt,
                            UpdatedAt
                        )
                        VALUES
                        (
                            $room,
                            $name,
                            $owner,
                            '',
                            $created,
                            $updated
                        )
                        """
                    )
            )
            {
                room.Parameters
                    .AddWithValue(
                        "$room",
                        roomId
                    );

                room.Parameters
                    .AddWithValue(
                        "$name",
                        name
                    );

                room.Parameters
                    .AddWithValue(
                        "$owner",
                        me
                    );

                room.Parameters
                    .AddWithValue(
                        "$created",
                        now
                    );

                room.Parameters
                    .AddWithValue(
                        "$updated",
                        now
                    );

                room.ExecuteNonQuery();
            }

            foreach (
                var username
                in allMembers
            )
            {
                InsertMember(
                    db,
                    tx,
                    roomId,
                    username,
                    now,
                    0,
                    0
                );
            }

            systemMessageId =
                InsertSystemMessage(
                    db,
                    tx,
                    roomId,
                    me,
                    $"{me} created the group.",
                    now
                );

            using (
                var read =
                    NewCommand(
                        db,
                        tx,
                        """
                        UPDATE GroupDmMembers

                        SET
                            LastReadMessageId =
                                $message

                        WHERE
                            RoomId = $room

                            AND

                            Username = $user
                        """
                    )
            )
            {
                read.Parameters
                    .AddWithValue(
                        "$message",
                        systemMessageId
                    );

                read.Parameters
                    .AddWithValue(
                        "$room",
                        roomId
                    );

                read.Parameters
                    .AddWithValue(
                        "$user",
                        me
                    );

                read.ExecuteNonQuery();
            }

            tx.Commit();
        }

        var systemPayload =
            BuildMessagePayload(
                systemMessageId,
                roomId,
                me,
                $"{me} created the group.",
                "system",
                "",
                "",
                "",
                0,
                now,
                0
            );

        foreach (
            var member
            in allMembers
        )
        {
            await hub.Clients
                .User(
                    member
                )
                .SendAsync(
                    "GroupDmAdded",
                    new
                    {
                        roomId,
                        name,
                        owner = me,
                        memberCount =
                            allMembers.Count,
                        maxMembers =
                            GroupDmStore
                                .MaxMembers
                    }
                );
        }

        await NotifyMessage(
            hub,
            allMembers,
            systemPayload
        );

        await NotifyUpdated(
            hub,
            allMembers,
            roomId,
            "created"
        );

        return Results.Ok(
            new
            {
                success = true,
                roomId,
                name,
                owner = me,
                avatarUrl = "",
                members =
                    allMembers,
                memberCount =
                    allMembers.Count,
                maxMembers =
                    GroupDmStore
                        .MaxMembers,
                createdAt =
                    now
            }
        );
    }

    private static IResult GetMyRooms(
        HttpContext ctx
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        using var db =
            DbHelpers.OpenDb();

        var rows =
            new List<RoomListRow>();

        using (
            var cmd =
                db.CreateCommand()
        )
        {
            cmd.CommandText =
                """
                SELECT
                    room.RoomId,
                    room.Name,
                    room.OwnerUsername,
                    room.AvatarUrl,
                    room.CreatedAt,
                    room.UpdatedAt,
                    member.LastReadMessageId,
                    member.VisibleAfterMessageId

                FROM
                    GroupDmRooms room

                INNER JOIN
                    GroupDmMembers member

                    ON

                    member.RoomId =
                        room.RoomId

                WHERE
                    member.Username =
                        $user

                ORDER BY
                    room.UpdatedAt DESC
                """;

            cmd.Parameters
                .AddWithValue(
                    "$user",
                    me
                );

            using var reader =
                cmd.ExecuteReader();

            while (
                reader.Read()
            )
            {
                rows.Add(
                    new RoomListRow(
                        new RoomInfo(
                            reader.GetString(
                                0
                            ),
                            reader.GetString(
                                1
                            ),
                            reader.GetString(
                                2
                            ),
                            reader.IsDBNull(
                                3
                            )
                                ? ""
                                : reader.GetString(
                                    3
                                ),
                            reader.GetString(
                                4
                            ),
                            reader.GetString(
                                5
                            )
                        ),
                        reader.IsDBNull(
                            6
                        )
                            ? 0
                            : reader.GetInt64(
                                6
                            ),
                        reader.IsDBNull(
                            7
                        )
                            ? 0
                            : reader.GetInt64(
                                7
                            )
                    )
                );
            }
        }

        var result =
            new List<object>();

        foreach (
            var row
            in rows
        )
        {
            var memberCount =
                CountMembers(
                    db,
                    null,
                    row.Room.RoomId
                );

            var unreadCount =
                CountUnread(
                    db,
                    row.Room.RoomId,
                    me,
                    row.LastReadMessageId,
                    row.VisibleAfterMessageId
                );

            object? lastMessage =
                null;

            using (
                var latest =
                    db.CreateCommand()
            )
            {
                latest.CommandText =
                    """
                    SELECT
                        Id,
                        RoomId,
                        FromUser,
                        Message,
                        MessageType,
                        AttachmentUrl,
                        AttachmentName,
                        AttachmentMime,
                        AttachmentSize,
                        CreatedAt,
                        ReplyToId

                    FROM
                        GroupDmMessages

                    WHERE
                        RoomId =
                            $room

                        AND

                        Id >
                            $visible

                    ORDER BY
                        Id DESC

                    LIMIT 1
                    """;

                latest.Parameters
                    .AddWithValue(
                        "$room",
                        row.Room.RoomId
                    );

                latest.Parameters
                    .AddWithValue(
                        "$visible",
                        row.VisibleAfterMessageId
                    );

                using var reader =
                    latest.ExecuteReader();

                if (
                    reader.Read()
                )
                {
                    lastMessage =
                        ReadMessagePayload(
                            reader
                        );
                }
            }

            result.Add(
                new
                {
                    kind =
                        "group_dm",
                    roomId =
                        row.Room.RoomId,
                    name =
                        row.Room.Name,
                    owner =
                        row.Room.Owner,
                    avatarUrl =
                        row.Room.AvatarUrl,
                    createdAt =
                        row.Room.CreatedAt,
                    updatedAt =
                        row.Room.UpdatedAt,
                    memberCount,
                    maxMembers =
                        GroupDmStore
                            .MaxMembers,
                    unreadCount,
                    lastMessage
                }
            );
        }

        return Results.Ok(
            result
        );
    }

    private static IResult GetRoom(
        string roomId,
        HttpContext ctx
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        using var db =
            DbHelpers.OpenDb();

        var membership =
            GetMembership(
                db,
                null,
                normalizedRoom,
                me
            );

        if (
            membership == null
        )
        {
            return Results.Forbid();
        }

        var room =
            GetRoomInfo(
                db,
                null,
                normalizedRoom
            );

        if (
            room == null
        )
        {
            return Results.NotFound(
                new
                {
                    message =
                        "Grupp-DM hittades inte."
                }
            );
        }

        return Results.Ok(
            new
            {
                kind =
                    "group_dm",
                roomId =
                    room.RoomId,
                name =
                    room.Name,
                owner =
                    room.Owner,
                avatarUrl =
                    room.AvatarUrl,
                createdAt =
                    room.CreatedAt,
                updatedAt =
                    room.UpdatedAt,
                members =
                    GetDetailedMembers(
                        db,
                        normalizedRoom,
                        room.Owner
                    ),
                memberCount =
                    CountMembers(
                        db,
                        null,
                        normalizedRoom
                    ),
                maxMembers =
                    GroupDmStore
                        .MaxMembers
            }
        );
    }

    private static async Task<IResult> UpdateRoom(
        string roomId,
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        var req =
            await ctx.Request
                .ReadFromJsonAsync<GroupDmUpdateRequest>();

        if (
            req == null
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Body saknas."
                }
            );
        }

        var changesName =
            req.Name != null;

        var changesAvatar =
            req.AvatarUrl != null;

        if (
            !changesName
            &&
            !changesAvatar
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Inga ändringar skickades."
                }
            );
        }

        var cleanName =
            "";

        if (
            changesName
        )
        {
            cleanName =
                CleanRoomName(
                    req.Name
                    ?? ""
                );

            if (
                cleanName.Length < 2
            )
            {
                return Results.BadRequest(
                    new
                    {
                        message =
                            "Gruppnamnet måste innehålla minst 2 tecken."
                    }
                );
            }

            if (
                ContentFilter
                    .IsOffensive(
                        cleanName
                    )
            )
            {
                return Results.BadRequest(
                    new
                    {
                        message =
                            "Gruppnamnet är inte tillåtet."
                    }
                );
            }
        }

        var cleanAvatar =
            (
                req.AvatarUrl
                ?? ""
            )
            .Trim();

        if (
            changesAvatar
            &&
            cleanAvatar.Length > 0
            &&
            !IsSafeUploadUrl(
                cleanAvatar,
                true
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltig gruppbild."
                }
            );
        }

        var now =
            DateTime.UtcNow
                .ToString(
                    "o"
                );

        long systemId;

        List<string> members;

        using (
            var db =
                DbHelpers.OpenDb()
        )
        using (
            var tx =
                db.BeginTransaction()
        )
        {
            var room =
                GetRoomInfo(
                    db,
                    tx,
                    normalizedRoom
                );

            if (
                room == null
            )
            {
                return Results.NotFound(
                    new
                    {
                        message =
                            "Grupp-DM hittades inte."
                    }
                );
            }

            if (
                !room.Owner.Equals(
                    me,
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return Results.Forbid();
            }

            if (
                changesName
            )
            {
                using var updateName =
                    NewCommand(
                        db,
                        tx,
                        """
                        UPDATE GroupDmRooms

                        SET
                            Name =
                                $name

                        WHERE
                            RoomId =
                                $room
                        """
                    );

                updateName.Parameters
                    .AddWithValue(
                        "$name",
                        cleanName
                    );

                updateName.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                updateName.ExecuteNonQuery();
            }

            if (
                changesAvatar
            )
            {
                using var updateAvatar =
                    NewCommand(
                        db,
                        tx,
                        """
                        UPDATE GroupDmRooms

                        SET
                            AvatarUrl =
                                $avatar

                        WHERE
                            RoomId =
                                $room
                        """
                    );

                updateAvatar.Parameters
                    .AddWithValue(
                        "$avatar",
                        cleanAvatar
                    );

                updateAvatar.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                updateAvatar.ExecuteNonQuery();
            }

            TouchRoom(
                db,
                tx,
                normalizedRoom,
                now
            );

            systemId =
                InsertSystemMessage(
                    db,
                    tx,
                    normalizedRoom,
                    me,
                    changesName
                        ? $"{me} renamed the group to {cleanName}."
                        : $"{me} updated the group image.",
                    now
                );

            members =
                GetMemberNames(
                    db,
                    tx,
                    normalizedRoom
                );

            tx.Commit();
        }

        var text =
            changesName
                ? $"{me} renamed the group to {cleanName}."
                : $"{me} updated the group image.";

        var payload =
            BuildMessagePayload(
                systemId,
                normalizedRoom,
                me,
                text,
                "system",
                "",
                "",
                "",
                0,
                now,
                0
            );

        await NotifyMessage(
            hub,
            members,
            payload
        );

        await NotifyUpdated(
            hub,
            members,
            normalizedRoom,
            "details_updated"
        );

        return Results.Ok(
            new
            {
                success = true,
                roomId =
                    normalizedRoom,
                name =
                    changesName
                        ? cleanName
                        : null,
                avatarUrl =
                    changesAvatar
                        ? cleanAvatar
                        : null
            }
        );
    }

    private static async Task<IResult> DeleteRoom(
        string roomId,
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        List<string> members;

        using (
            var db =
                DbHelpers.OpenDb()
        )
        {
            var room =
                GetRoomInfo(
                    db,
                    null,
                    normalizedRoom
                );

            if (
                room == null
            )
            {
                return Results.NotFound(
                    new
                    {
                        message =
                            "Grupp-DM hittades inte."
                    }
                );
            }

            if (
                !room.Owner.Equals(
                    me,
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return Results.Forbid();
            }

            members =
                GetMemberNames(
                    db,
                    null,
                    normalizedRoom
                );

            using var delete =
                db.CreateCommand();

            delete.CommandText =
                """
                DELETE FROM
                    GroupDmRooms

                WHERE
                    RoomId =
                        $room
                """;

            delete.Parameters
                .AddWithValue(
                    "$room",
                    normalizedRoom
                );

            delete.ExecuteNonQuery();
        }

        foreach (
            var member
            in members
        )
        {
            await hub.Clients
                .User(
                    member
                )
                .SendAsync(
                    "GroupDmDeleted",
                    new
                    {
                        roomId =
                            normalizedRoom,
                        deletedBy =
                            me
                    }
                );
        }

        return Results.Ok(
            new
            {
                success = true,
                deleted = true,
                roomId =
                    normalizedRoom
            }
        );
    }

    private static IResult GetMessages(
        string roomId,
        HttpContext ctx,
        long? before,
        int? limit
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        using var db =
            DbHelpers.OpenDb();

        var membership =
            GetMembership(
                db,
                null,
                normalizedRoom,
                me
            );

        if (
            membership == null
        )
        {
            return Results.Forbid();
        }

        var take =
            Math.Clamp(
                limit
                ?? 100,
                1,
                200
            );

        var messages =
            new List<object>();

        using (
            var cmd =
                db.CreateCommand()
        )
        {
            cmd.CommandText =
                """
                SELECT
                    Id,
                    RoomId,
                    FromUser,
                    Message,
                    MessageType,
                    AttachmentUrl,
                    AttachmentName,
                    AttachmentMime,
                    AttachmentSize,
                    CreatedAt,
                    ReplyToId

                FROM
                    GroupDmMessages

                WHERE
                    RoomId =
                        $room

                    AND

                    Id >
                        $visible
                """
                +
                (
                    before.HasValue
                    &&
                    before.Value > 0

                    ? """

                        AND

                        Id <
                            $before
                      """

                    : ""
                )
                +
                """

                ORDER BY
                    Id DESC

                LIMIT
                    $limit
                """;

            cmd.Parameters
                .AddWithValue(
                    "$room",
                    normalizedRoom
                );

            cmd.Parameters
                .AddWithValue(
                    "$visible",
                    membership
                        .VisibleAfterMessageId
                );

            if (
                before.HasValue
                &&
                before.Value > 0
            )
            {
                cmd.Parameters
                    .AddWithValue(
                        "$before",
                        before.Value
                    );
            }

            cmd.Parameters
                .AddWithValue(
                    "$limit",
                    take
                );

            using var reader =
                cmd.ExecuteReader();

            while (
                reader.Read()
            )
            {
                messages.Add(
                    ReadMessagePayload(
                        reader
                    )
                );
            }
        }

        messages.Reverse();

        return Results.Ok(
            new
            {
                roomId =
                    normalizedRoom,
                messages,
                count =
                    messages.Count
            }
        );
    }

    private static async Task<IResult> SendMessage(
        string roomId,
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        var limiter =
            ctx.RequestServices
                .GetRequiredService<RateLimiter>();

        if (
            !limiter.IsAllowed(
                me,
                "group_dm_send",
                60,
                60
            )
        )
        {
            return Results.Json(
                new
                {
                    message =
                        "Du skickar meddelanden för snabbt."
                },
                statusCode:
                    StatusCodes
                        .Status429TooManyRequests
            );
        }

        var req =
            await ctx.Request
                .ReadFromJsonAsync<GroupDmSendRequest>();

        if (
            req == null
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Body saknas."
                }
            );
        }

        var text =
            InputSanitizer
                .SanitizeInput(
                    req.Text
                    ?? "",
                    4000
                )
                .Trim();

        if (
            text.Length > 0
            &&
            !DefensiveInput
                .IsSafeChatText(
                    text,
                    4000
                )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt meddelande."
                }
            );
        }

        var attachmentUrl =
            (
                req.AttachmentUrl
                ?? ""
            )
            .Trim();

        if (
            attachmentUrl.Length > 0
            &&
            !IsSafeUploadUrl(
                attachmentUrl,
                false
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltig bilaga."
                }
            );
        }

        var attachmentName =
            CleanSingleLine(
                req.AttachmentName,
                180
            );

        var attachmentMime =
            CleanSingleLine(
                req.AttachmentMime,
                100
            );

        var attachmentSize =
            req.AttachmentSize
            ?? 0;

        if (
            attachmentSize < 0
            ||
            attachmentSize
            >
            30L
            *
            1024L
            *
            1024L
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltig filstorlek."
                }
            );
        }

        if (
            string.IsNullOrWhiteSpace(
                text
            )
            &&
            string.IsNullOrWhiteSpace(
                attachmentUrl
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Meddelandet är tomt."
                }
            );
        }

        var replyTo =
            Math.Max(
                req.ReplyToId
                ?? 0,
                0
            );

        var messageType =
            attachmentUrl.Length > 0
                ? "attachment"
                : "text";

        var now =
            DateTime.UtcNow
                .ToString(
                    "o"
                );

        long messageId;

        List<string> members;

        using (
            var db =
                DbHelpers.OpenDb()
        )
        using (
            var tx =
                db.BeginTransaction()
        )
        {
            var membership =
                GetMembership(
                    db,
                    tx,
                    normalizedRoom,
                    me
                );

            if (
                membership == null
            )
            {
                return Results.Forbid();
            }

            if (
                replyTo > 0
                &&
                !MessageExists(
                    db,
                    tx,
                    normalizedRoom,
                    replyTo
                )
            )
            {
                return Results.BadRequest(
                    new
                    {
                        message =
                            "Meddelandet du svarar på finns inte i gruppen."
                    }
                );
            }

            using (
                var insert =
                    NewCommand(
                        db,
                        tx,
                        """
                        INSERT INTO GroupDmMessages
                        (
                            RoomId,
                            FromUser,
                            Message,
                            MessageType,
                            AttachmentUrl,
                            AttachmentName,
                            AttachmentMime,
                            AttachmentSize,
                            CreatedAt,
                            ReplyToId
                        )
                        VALUES
                        (
                            $room,
                            $from,
                            $message,
                            $type,
                            $url,
                            $name,
                            $mime,
                            $size,
                            $created,
                            $reply
                        );

                        SELECT
                            last_insert_rowid();
                        """
                    )
            )
            {
                insert.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                insert.Parameters
                    .AddWithValue(
                        "$from",
                        me
                    );

                insert.Parameters
                    .AddWithValue(
                        "$message",
                        text
                    );

                insert.Parameters
                    .AddWithValue(
                        "$type",
                        messageType
                    );

                insert.Parameters
                    .AddWithValue(
                        "$url",
                        attachmentUrl
                    );

                insert.Parameters
                    .AddWithValue(
                        "$name",
                        attachmentName
                    );

                insert.Parameters
                    .AddWithValue(
                        "$mime",
                        attachmentMime
                    );

                insert.Parameters
                    .AddWithValue(
                        "$size",
                        attachmentSize
                    );

                insert.Parameters
                    .AddWithValue(
                        "$created",
                        now
                    );

                insert.Parameters
                    .AddWithValue(
                        "$reply",
                        replyTo
                    );

                messageId =
                    Convert.ToInt64(
                        insert.ExecuteScalar()
                        ?? 0L
                    );
            }

            TouchRoom(
                db,
                tx,
                normalizedRoom,
                now
            );

            using (
                var mark =
                    NewCommand(
                        db,
                        tx,
                        """
                        UPDATE GroupDmMembers

                        SET
                            LastReadMessageId =
                                $message

                        WHERE
                            RoomId =
                                $room

                            AND

                            Username =
                                $user
                        """
                    )
            )
            {
                mark.Parameters
                    .AddWithValue(
                        "$message",
                        messageId
                    );

                mark.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                mark.Parameters
                    .AddWithValue(
                        "$user",
                        me
                    );

                mark.ExecuteNonQuery();
            }

            members =
                GetMemberNames(
                    db,
                    tx,
                    normalizedRoom
                );

            tx.Commit();
        }

        var payload =
            BuildMessagePayload(
                messageId,
                normalizedRoom,
                me,
                text,
                messageType,
                attachmentUrl,
                attachmentName,
                attachmentMime,
                attachmentSize,
                now,
                replyTo
            );

        await NotifyMessage(
            hub,
            members,
            payload
        );

        await NotifyUpdated(
            hub,
            members,
            normalizedRoom,
            "new_message"
        );

        return Results.Ok(
            new
            {
                success = true,
                message =
                    payload
            }
        );
    }

    private static IResult MarkRead(
        string roomId,
        HttpContext ctx
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        using var db =
            DbHelpers.OpenDb();

        var membership =
            GetMembership(
                db,
                null,
                normalizedRoom,
                me
            );

        if (
            membership == null
        )
        {
            return Results.Forbid();
        }

        var latest =
            GetLatestMessageId(
                db,
                null,
                normalizedRoom
            );

        using var cmd =
            db.CreateCommand();

        cmd.CommandText =
            """
            UPDATE GroupDmMembers

            SET
                LastReadMessageId =
                    CASE

                        WHEN
                            LastReadMessageId
                            <
                            $message

                        THEN
                            $message

                        ELSE
                            LastReadMessageId

                    END

            WHERE
                RoomId =
                    $room

                AND

                Username =
                    $user
            """;

        cmd.Parameters
            .AddWithValue(
                "$message",
                latest
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                normalizedRoom
            );

        cmd.Parameters
            .AddWithValue(
                "$user",
                me
            );

        cmd.ExecuteNonQuery();

        return Results.Ok(
            new
            {
                success = true,
                roomId =
                    normalizedRoom,
                lastReadMessageId =
                    latest
            }
        );
    }

    private static async Task<IResult> AddMember(
        string roomId,
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        var limiter =
            ctx.RequestServices
                .GetRequiredService<RateLimiter>();

        if (
            !limiter.IsAllowed(
                me,
                "group_dm_add_member",
                30,
                3600
            )
        )
        {
            return Results.Json(
                new
                {
                    message =
                        "Du lägger till medlemmar för snabbt."
                },
                statusCode:
                    StatusCodes
                        .Status429TooManyRequests
            );
        }

        var req =
            await ctx.Request
                .ReadFromJsonAsync<GroupDmAddMemberRequest>();

        var target =
            NormalizeUsername(
                req?.Username
                ?? ""
            );

        if (
            !AppHelpers
                .IsValidUsername(
                    target
                )
            ||
            !AppHelpers
                .UserExists(
                    target
                )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Användaren finns inte."
                }
            );
        }

        var now =
            DateTime.UtcNow
                .ToString(
                    "o"
                );

        long systemId;

        List<string> members;

        string roomName;

        using (
            var db =
                DbHelpers.OpenDb()
        )
        using (
            var tx =
                db.BeginTransaction()
        )
        {
            var room =
                GetRoomInfo(
                    db,
                    tx,
                    normalizedRoom
                );

            if (
                room == null
            )
            {
                return Results.NotFound(
                    new
                    {
                        message =
                            "Grupp-DM hittades inte."
                    }
                );
            }

            if (
                !room.Owner.Equals(
                    me,
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return Results.Forbid();
            }

            roomName =
                room.Name;

            if (
                GetMembership(
                    db,
                    tx,
                    normalizedRoom,
                    target
                )
                != null
            )
            {
                return Results.Conflict(
                    new
                    {
                        message =
                            "Användaren är redan medlem."
                    }
                );
            }

            var memberCount =
                CountMembers(
                    db,
                    tx,
                    normalizedRoom
                );

            if (
                memberCount
                >=
                GroupDmStore.MaxMembers
            )
            {
                return Results.Conflict(
                    new
                    {
                        message =
                            $"Grupp-DM:n har redan {GroupDmStore.MaxMembers} medlemmar."
                    }
                );
            }

            var visibleAfter =
                GetLatestMessageId(
                    db,
                    tx,
                    normalizedRoom
                );

            InsertMember(
                db,
                tx,
                normalizedRoom,
                target,
                now,
                visibleAfter,
                visibleAfter
            );

            var text =
                $"{me} added {target}.";

            systemId =
                InsertSystemMessage(
                    db,
                    tx,
                    normalizedRoom,
                    me,
                    text,
                    now
                );

            TouchRoom(
                db,
                tx,
                normalizedRoom,
                now
            );

            members =
                GetMemberNames(
                    db,
                    tx,
                    normalizedRoom
                );

            tx.Commit();
        }

        var systemText =
            $"{me} added {target}.";

        var payload =
            BuildMessagePayload(
                systemId,
                normalizedRoom,
                me,
                systemText,
                "system",
                "",
                "",
                "",
                0,
                now,
                0
            );

        await hub.Clients
            .User(
                target
            )
            .SendAsync(
                "GroupDmAdded",
                new
                {
                    roomId =
                        normalizedRoom,
                    name =
                        roomName,
                    owner =
                        me
                }
            );

        await NotifyMessage(
            hub,
            members,
            payload
        );

        await NotifyUpdated(
            hub,
            members,
            normalizedRoom,
            "member_added"
        );

        return Results.Ok(
            new
            {
                success = true,
                roomId =
                    normalizedRoom,
                added =
                    target,
                memberCount =
                    members.Count,
                maxMembers =
                    GroupDmStore
                        .MaxMembers
            }
        );
    }

    private static async Task<IResult> RemoveMember(
        string roomId,
        string username,
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        var target =
            NormalizeUsername(
                username
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        if (
            target.Equals(
                me,
                StringComparison
                    .OrdinalIgnoreCase
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Använd leave-endpointen för att lämna gruppen."
                }
            );
        }

        var now =
            DateTime.UtcNow
                .ToString(
                    "o"
                );

        long systemId;

        List<string> remaining;

        using (
            var db =
                DbHelpers.OpenDb()
        )
        using (
            var tx =
                db.BeginTransaction()
        )
        {
            var room =
                GetRoomInfo(
                    db,
                    tx,
                    normalizedRoom
                );

            if (
                room == null
            )
            {
                return Results.NotFound(
                    new
                    {
                        message =
                            "Grupp-DM hittades inte."
                    }
                );
            }

            if (
                !room.Owner.Equals(
                    me,
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return Results.Forbid();
            }

            if (
                room.Owner.Equals(
                    target,
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return Results.BadRequest(
                    new
                    {
                        message =
                            "Ägaren kan inte tas bort."
                    }
                );
            }

            if (
                GetMembership(
                    db,
                    tx,
                    normalizedRoom,
                    target
                )
                == null
            )
            {
                return Results.NotFound(
                    new
                    {
                        message =
                            "Användaren är inte medlem."
                    }
                );
            }

            using (
                var remove =
                    NewCommand(
                        db,
                        tx,
                        """
                        DELETE FROM
                            GroupDmMembers

                        WHERE
                            RoomId =
                                $room

                            AND

                            Username =
                                $user
                        """
                    )
            )
            {
                remove.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                remove.Parameters
                    .AddWithValue(
                        "$user",
                        target
                    );

                remove.ExecuteNonQuery();
            }

            var text =
                $"{me} removed {target}.";

            systemId =
                InsertSystemMessage(
                    db,
                    tx,
                    normalizedRoom,
                    me,
                    text,
                    now
                );

            TouchRoom(
                db,
                tx,
                normalizedRoom,
                now
            );

            remaining =
                GetMemberNames(
                    db,
                    tx,
                    normalizedRoom
                );

            tx.Commit();
        }

        var payload =
            BuildMessagePayload(
                systemId,
                normalizedRoom,
                me,
                $"{me} removed {target}.",
                "system",
                "",
                "",
                "",
                0,
                now,
                0
            );

        await hub.Clients
            .User(
                target
            )
            .SendAsync(
                "GroupDmRemoved",
                new
                {
                    roomId =
                        normalizedRoom,
                    reason =
                        "removed",
                    removedBy =
                        me
                }
            );

        await NotifyMessage(
            hub,
            remaining,
            payload
        );

        await NotifyUpdated(
            hub,
            remaining,
            normalizedRoom,
            "member_removed"
        );

        return Results.Ok(
            new
            {
                success = true,
                removed =
                    target,
                memberCount =
                    remaining.Count
            }
        );
    }

    private static async Task<IResult> LeaveRoom(
        string roomId,
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        var now =
            DateTime.UtcNow
                .ToString(
                    "o"
                );

        var deletedRoom =
            false;

        string? newOwner =
            null;

        long systemId =
            0;

        var remaining =
            new List<string>();

        string systemText =
            "";

        using (
            var db =
                DbHelpers.OpenDb()
        )
        using (
            var tx =
                db.BeginTransaction()
        )
        {
            var room =
                GetRoomInfo(
                    db,
                    tx,
                    normalizedRoom
                );

            if (
                room == null
            )
            {
                return Results.NotFound(
                    new
                    {
                        message =
                            "Grupp-DM hittades inte."
                    }
                );
            }

            if (
                GetMembership(
                    db,
                    tx,
                    normalizedRoom,
                    me
                )
                == null
            )
            {
                return Results.Forbid();
            }

            using (
                var remove =
                    NewCommand(
                        db,
                        tx,
                        """
                        DELETE FROM
                            GroupDmMembers

                        WHERE
                            RoomId =
                                $room

                            AND

                            Username =
                                $user
                        """
                    )
            )
            {
                remove.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                remove.Parameters
                    .AddWithValue(
                        "$user",
                        me
                    );

                remove.ExecuteNonQuery();
            }

            remaining =
                GetMemberNames(
                    db,
                    tx,
                    normalizedRoom
                );

            if (
                remaining.Count == 0
            )
            {
                using var delete =
                    NewCommand(
                        db,
                        tx,
                        """
                        DELETE FROM
                            GroupDmRooms

                        WHERE
                            RoomId =
                                $room
                        """
                    );

                delete.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                delete.ExecuteNonQuery();

                deletedRoom =
                    true;

                tx.Commit();
            }
            else
            {
                if (
                    room.Owner.Equals(
                        me,
                        StringComparison
                            .OrdinalIgnoreCase
                    )
                )
                {
                    newOwner =
                        GetFirstMember(
                            db,
                            tx,
                            normalizedRoom
                        );

                    if (
                        string.IsNullOrWhiteSpace(
                            newOwner
                        )
                    )
                    {
                        return Results.Json(
                            new
                            {
                                message =
                                    "Kunde inte välja en ny gruppägare."
                            },
                            statusCode:
                                StatusCodes
                                    .Status500InternalServerError
                        );
                    }

                    using (
                        var owner =
                            NewCommand(
                                db,
                                tx,
                                """
                                UPDATE GroupDmRooms

                                SET
                                    OwnerUsername =
                                        $owner

                                WHERE
                                    RoomId =
                                        $room
                                """
                            )
                    )
                    {
                        owner.Parameters
                            .AddWithValue(
                                "$owner",
                                newOwner
                            );

                        owner.Parameters
                            .AddWithValue(
                                "$room",
                                normalizedRoom
                            );

                        owner.ExecuteNonQuery();
                    }

                    systemText =
                        $"{me} left the group. {newOwner} is now the owner.";
                }
                else
                {
                    systemText =
                        $"{me} left the group.";
                }

                systemId =
                    InsertSystemMessage(
                        db,
                        tx,
                        normalizedRoom,
                        me,
                        systemText,
                        now
                    );

                TouchRoom(
                    db,
                    tx,
                    normalizedRoom,
                    now
                );

                tx.Commit();
            }
        }

        await hub.Clients
            .User(
                me
            )
            .SendAsync(
                "GroupDmRemoved",
                new
                {
                    roomId =
                        normalizedRoom,
                    reason =
                        "left"
                }
            );

        if (
            !deletedRoom
        )
        {
            var payload =
                BuildMessagePayload(
                    systemId,
                    normalizedRoom,
                    me,
                    systemText,
                    "system",
                    "",
                    "",
                    "",
                    0,
                    now,
                    0
                );

            await NotifyMessage(
                hub,
                remaining,
                payload
            );

            await NotifyUpdated(
                hub,
                remaining,
                normalizedRoom,
                "member_left"
            );
        }

        return Results.Ok(
            new
            {
                success = true,
                left = true,
                roomDeleted =
                    deletedRoom,
                newOwner
            }
        );
    }

    private static async Task<IResult> TransferOwner(
        string roomId,
        HttpContext ctx,
        IHubContext<ChatHub> hub
    )
    {
        var me =
            CurrentUser(
                ctx
            );

        var normalizedRoom =
            NormalizeRoomId(
                roomId
            );

        if (
            !ValidSignedInUser(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        if (
            !IsValidRoomId(
                normalizedRoom
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ogiltigt grupp-id."
                }
            );
        }

        var req =
            await ctx.Request
                .ReadFromJsonAsync<GroupDmTransferOwnerRequest>();

        var target =
            NormalizeUsername(
                req?.Username
                ?? ""
            );

        if (
            string.IsNullOrWhiteSpace(
                target
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Ny ägare saknas."
                }
            );
        }

        if (
            target.Equals(
                me,
                StringComparison
                    .OrdinalIgnoreCase
            )
        )
        {
            return Results.BadRequest(
                new
                {
                    message =
                        "Du äger redan gruppen."
                }
            );
        }

        var now =
            DateTime.UtcNow
                .ToString(
                    "o"
                );

        long systemId;

        List<string> members;

        using (
            var db =
                DbHelpers.OpenDb()
        )
        using (
            var tx =
                db.BeginTransaction()
        )
        {
            var room =
                GetRoomInfo(
                    db,
                    tx,
                    normalizedRoom
                );

            if (
                room == null
            )
            {
                return Results.NotFound(
                    new
                    {
                        message =
                            "Grupp-DM hittades inte."
                    }
                );
            }

            if (
                !room.Owner.Equals(
                    me,
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return Results.Forbid();
            }

            if (
                GetMembership(
                    db,
                    tx,
                    normalizedRoom,
                    target
                )
                == null
            )
            {
                return Results.BadRequest(
                    new
                    {
                        message =
                            "Den nya ägaren måste redan vara medlem."
                    }
                );
            }

            using (
                var update =
                    NewCommand(
                        db,
                        tx,
                        """
                        UPDATE GroupDmRooms

                        SET
                            OwnerUsername =
                                $owner

                        WHERE
                            RoomId =
                                $room
                        """
                    )
            )
            {
                update.Parameters
                    .AddWithValue(
                        "$owner",
                        target
                    );

                update.Parameters
                    .AddWithValue(
                        "$room",
                        normalizedRoom
                    );

                update.ExecuteNonQuery();
            }

            var text =
                $"{me} transferred ownership to {target}.";

            systemId =
                InsertSystemMessage(
                    db,
                    tx,
                    normalizedRoom,
                    me,
                    text,
                    now
                );

            TouchRoom(
                db,
                tx,
                normalizedRoom,
                now
            );

            members =
                GetMemberNames(
                    db,
                    tx,
                    normalizedRoom
                );

            tx.Commit();
        }

        var payload =
            BuildMessagePayload(
                systemId,
                normalizedRoom,
                me,
                $"{me} transferred ownership to {target}.",
                "system",
                "",
                "",
                "",
                0,
                now,
                0
            );

        await NotifyMessage(
            hub,
            members,
            payload
        );

        await NotifyUpdated(
            hub,
            members,
            normalizedRoom,
            "owner_transferred"
        );

        return Results.Ok(
            new
            {
                success = true,
                owner =
                    target
            }
        );
    }

    private static string CurrentUser(
        HttpContext ctx
    )
    {
        return
            ctx.User
                .Identity
                ?.Name
                ?.Trim()
                .ToLowerInvariant()
            ?? "";
    }

    private static bool ValidSignedInUser(
        string username
    )
    {
        return
            !string.IsNullOrWhiteSpace(
                username
            )
            &&
            AppHelpers.UserExists(
                username
            );
    }

    private static string NormalizeUsername(
        string? value
    )
    {
        return
            (
                value
                ?? ""
            )
            .Trim()
            .ToLowerInvariant();
    }

    private static string NormalizeRoomId(
        string? value
    )
    {
        return
            (
                value
                ?? ""
            )
            .Trim()
            .ToLowerInvariant();
    }

    private static bool IsValidRoomId(
        string roomId
    )
    {
        return
            roomId.Length == 32
            &&
            roomId.All(
                Uri.IsHexDigit
            );
    }

    private static string CleanRoomName(
        string? value
    )
    {
        return
            InputSanitizer
                .SanitizeInput(
                    value
                    ?? "",
                    48
                )
                .Trim();
    }

    private static string CleanSingleLine(
        string? value,
        int maxLength
    )
    {
        var clean =
            (
                value
                ?? ""
            )
            .Replace(
                "\0",
                ""
            )
            .Replace(
                "\r",
                " "
            )
            .Replace(
                "\n",
                " "
            )
            .Trim();

        if (
            clean.Length
            >
            maxLength
        )
        {
            clean =
                clean[
                    ..maxLength
                ];
        }

        return clean;
    }

    private static bool IsSafeUploadUrl(
        string value,
        bool imageOnly
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                value
            )
        )
        {
            return true;
        }

        if (
            value.Length > 800
            ||
            value.Contains(
                "..",
                StringComparison.Ordinal
            )
            ||
            value.Contains(
                "\\",
                StringComparison.Ordinal
            )
        )
        {
            return false;
        }

        string path;

        if (
            Uri.TryCreate(
                value,
                UriKind.Absolute,
                out var absolute
            )
        )
        {
            if (
                !absolute.Scheme.Equals(
                    "https",
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return false;
            }

            if (
                !absolute.Host.Equals(
                    "runspace.cloud",
                    StringComparison
                        .OrdinalIgnoreCase
                )
                &&
                !absolute.Host.Equals(
                    "www.runspace.cloud",
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                return false;
            }

            path =
                absolute.AbsolutePath;
        }
        else
        {
            path =
                value.Split(
                    '?',
                    '#'
                )[0];
        }

        var allowedPath =
            path.StartsWith(
                "/uploads/chat/",
                StringComparison
                    .OrdinalIgnoreCase
            )
            ||
            path.StartsWith(
                "/uploads/avatars/",
                StringComparison
                    .OrdinalIgnoreCase
            );

        if (
            !allowedPath
        )
        {
            return false;
        }

        if (
            !imageOnly
        )
        {
            return true;
        }

        var extension =
            System.IO.Path
                .GetExtension(
                    path
                )
                .ToLowerInvariant();

        return
            extension
            is ".png"
            or ".jpg"
            or ".jpeg"
            or ".webp"
            or ".gif";
    }

    private static SqliteCommand NewCommand(
        SqliteConnection db,
        SqliteTransaction? tx,
        string sql
    )
    {
        var cmd =
            db.CreateCommand();

        cmd.Transaction =
            tx;

        cmd.CommandText =
            sql;

        return cmd;
    }

    private static RoomInfo? GetRoomInfo(
        SqliteConnection db,
        SqliteTransaction? tx,
        string roomId
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                SELECT
                    RoomId,
                    Name,
                    OwnerUsername,
                    AvatarUrl,
                    CreatedAt,
                    UpdatedAt

                FROM
                    GroupDmRooms

                WHERE
                    RoomId =
                        $room

                LIMIT 1
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        using var reader =
            cmd.ExecuteReader();

        if (
            !reader.Read()
        )
        {
            return null;
        }

        return new RoomInfo(
            reader.GetString(
                0
            ),
            reader.GetString(
                1
            ),
            reader.GetString(
                2
            ),
            reader.IsDBNull(
                3
            )
                ? ""
                : reader.GetString(
                    3
                ),
            reader.GetString(
                4
            ),
            reader.GetString(
                5
            )
        );
    }

    private static MembershipInfo? GetMembership(
        SqliteConnection db,
        SqliteTransaction? tx,
        string roomId,
        string username
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                SELECT
                    LastReadMessageId,
                    VisibleAfterMessageId

                FROM
                    GroupDmMembers

                WHERE
                    RoomId =
                        $room

                    AND

                    Username =
                        $user

                LIMIT 1
                """
            );

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

        using var reader =
            cmd.ExecuteReader();

        if (
            !reader.Read()
        )
        {
            return null;
        }

        return new MembershipInfo(
            reader.IsDBNull(
                0
            )
                ? 0
                : reader.GetInt64(
                    0
                ),
            reader.IsDBNull(
                1
            )
                ? 0
                : reader.GetInt64(
                    1
                )
        );
    }

    private static void InsertMember(
        SqliteConnection db,
        SqliteTransaction tx,
        string roomId,
        string username,
        string joinedAt,
        long lastReadMessageId,
        long visibleAfterMessageId
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                INSERT INTO GroupDmMembers
                (
                    RoomId,
                    Username,
                    JoinedAt,
                    LastReadMessageId,
                    VisibleAfterMessageId
                )
                VALUES
                (
                    $room,
                    $user,
                    $joined,
                    $read,
                    $visible
                )
                """
            );

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
                "$joined",
                joinedAt
            );

        cmd.Parameters
            .AddWithValue(
                "$read",
                lastReadMessageId
            );

        cmd.Parameters
            .AddWithValue(
                "$visible",
                visibleAfterMessageId
            );

        cmd.ExecuteNonQuery();
    }

    private static long InsertSystemMessage(
        SqliteConnection db,
        SqliteTransaction tx,
        string roomId,
        string from,
        string text,
        string createdAt
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                INSERT INTO GroupDmMessages
                (
                    RoomId,
                    FromUser,
                    Message,
                    MessageType,
                    AttachmentUrl,
                    AttachmentName,
                    AttachmentMime,
                    AttachmentSize,
                    CreatedAt,
                    ReplyToId
                )
                VALUES
                (
                    $room,
                    $from,
                    $message,
                    'system',
                    '',
                    '',
                    '',
                    0,
                    $created,
                    0
                );

                SELECT
                    last_insert_rowid();
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        cmd.Parameters
            .AddWithValue(
                "$from",
                from
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

        return Convert.ToInt64(
            cmd.ExecuteScalar()
            ?? 0L
        );
    }

    private static void TouchRoom(
        SqliteConnection db,
        SqliteTransaction tx,
        string roomId,
        string updatedAt
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                UPDATE GroupDmRooms

                SET
                    UpdatedAt =
                        $updated

                WHERE
                    RoomId =
                        $room
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$updated",
                updatedAt
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        cmd.ExecuteNonQuery();
    }

    private static int CountMembers(
        SqliteConnection db,
        SqliteTransaction? tx,
        string roomId
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                SELECT
                    COUNT(*)

                FROM
                    GroupDmMembers

                WHERE
                    RoomId =
                        $room
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        return Convert.ToInt32(
            cmd.ExecuteScalar()
            ?? 0
        );
    }

    private static int CountUnread(
        SqliteConnection db,
        string roomId,
        string username,
        long lastReadMessageId,
        long visibleAfterMessageId
    )
    {
        var after =
            Math.Max(
                lastReadMessageId,
                visibleAfterMessageId
            );

        using var cmd =
            db.CreateCommand();

        cmd.CommandText =
            """
            SELECT
                COUNT(*)

            FROM
                GroupDmMessages

            WHERE
                RoomId =
                    $room

                AND

                Id >
                    $after

                AND

                FromUser
                    <>
                $user
            """;

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        cmd.Parameters
            .AddWithValue(
                "$after",
                after
            );

        cmd.Parameters
            .AddWithValue(
                "$user",
                username
            );

        return Convert.ToInt32(
            cmd.ExecuteScalar()
            ?? 0
        );
    }

    private static long GetLatestMessageId(
        SqliteConnection db,
        SqliteTransaction? tx,
        string roomId
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                SELECT
                    COALESCE(
                        MAX(Id),
                        0
                    )

                FROM
                    GroupDmMessages

                WHERE
                    RoomId =
                        $room
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        return Convert.ToInt64(
            cmd.ExecuteScalar()
            ?? 0L
        );
    }

    private static bool MessageExists(
        SqliteConnection db,
        SqliteTransaction tx,
        string roomId,
        long messageId
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                SELECT
                    COUNT(*)

                FROM
                    GroupDmMessages

                WHERE
                    RoomId =
                        $room

                    AND

                    Id =
                        $message
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        cmd.Parameters
            .AddWithValue(
                "$message",
                messageId
            );

        return
            Convert.ToInt32(
                cmd.ExecuteScalar()
                ?? 0
            )
            >
            0;
    }

    private static List<string> GetMemberNames(
        SqliteConnection db,
        SqliteTransaction? tx,
        string roomId
    )
    {
        var result =
            new List<string>();

        using var cmd =
            NewCommand(
                db,
                tx,
                """
                SELECT
                    Username

                FROM
                    GroupDmMembers

                WHERE
                    RoomId =
                        $room

                ORDER BY
                    Id ASC
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        using var reader =
            cmd.ExecuteReader();

        while (
            reader.Read()
        )
        {
            result.Add(
                reader.GetString(
                    0
                )
            );
        }

        return result;
    }

    private static string? GetFirstMember(
        SqliteConnection db,
        SqliteTransaction tx,
        string roomId
    )
    {
        using var cmd =
            NewCommand(
                db,
                tx,
                """
                SELECT
                    Username

                FROM
                    GroupDmMembers

                WHERE
                    RoomId =
                        $room

                ORDER BY
                    JoinedAt ASC,
                    Id ASC

                LIMIT 1
                """
            );

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        return
            cmd.ExecuteScalar()
            as string;
    }

    private static List<object> GetDetailedMembers(
        SqliteConnection db,
        string roomId,
        string owner
    )
    {
        var result =
            new List<object>();

        using var cmd =
            db.CreateCommand();

        cmd.CommandText =
            """
            SELECT
                member.Username,
                member.JoinedAt,
                COALESCE(
                    auth.AvatarUrl,
                    ''
                )

            FROM
                GroupDmMembers member

            LEFT JOIN
                AuthUsers auth

                ON

                LOWER(
                    auth.Username
                )
                =
                LOWER(
                    member.Username
                )

            WHERE
                member.RoomId =
                    $room

            ORDER BY
                member.Id ASC
            """;

        cmd.Parameters
            .AddWithValue(
                "$room",
                roomId
            );

        using var reader =
            cmd.ExecuteReader();

        while (
            reader.Read()
        )
        {
            var username =
                reader.GetString(
                    0
                );

            result.Add(
                new
                {
                    username,
                    joinedAt =
                        reader.GetString(
                            1
                        ),
                    avatarUrl =
                        reader.IsDBNull(
                            2
                        )
                            ? ""
                            : reader.GetString(
                                2
                            ),
                    isOwner =
                        username.Equals(
                            owner,
                            StringComparison
                                .OrdinalIgnoreCase
                        )
                }
            );
        }

        return result;
    }

    private static object ReadMessagePayload(
        SqliteDataReader reader
    )
    {
        return BuildMessagePayload(
            reader.GetInt64(
                0
            ),
            reader.GetString(
                1
            ),
            reader.GetString(
                2
            ),
            reader.IsDBNull(
                3
            )
                ? ""
                : reader.GetString(
                    3
                ),
            reader.IsDBNull(
                4
            )
                ? "text"
                : reader.GetString(
                    4
                ),
            reader.IsDBNull(
                5
            )
                ? ""
                : reader.GetString(
                    5
                ),
            reader.IsDBNull(
                6
            )
                ? ""
                : reader.GetString(
                    6
                ),
            reader.IsDBNull(
                7
            )
                ? ""
                : reader.GetString(
                    7
                ),
            reader.IsDBNull(
                8
            )
                ? 0
                : reader.GetInt64(
                    8
                ),
            reader.GetString(
                9
            ),
            reader.IsDBNull(
                10
            )
                ? 0
                : reader.GetInt64(
                    10
                )
        );
    }

    private static object BuildMessagePayload(
        long id,
        string roomId,
        string from,
        string text,
        string type,
        string attachmentUrl,
        string attachmentName,
        string attachmentMime,
        long attachmentSize,
        string createdAt,
        long replyToId
    )
    {
        return new
        {
            id,
            roomId,
            from,
            text,
            type,
            attachmentUrl,
            attachmentName,
            attachmentMime,
            attachmentSize,
            createdAt,
            ts =
                createdAt,
            replyToId
        };
    }

    private static async Task NotifyMessage(
        IHubContext<ChatHub> hub,
        IEnumerable<string> users,
        object payload
    )
    {
        foreach (
            var user
            in users.Distinct(
                StringComparer
                    .OrdinalIgnoreCase
            )
        )
        {
            await hub.Clients
                .User(
                    user
                )
                .SendAsync(
                    "ReceiveGroupDmMessage",
                    payload
                );
        }
    }

    private static async Task NotifyUpdated(
        IHubContext<ChatHub> hub,
        IEnumerable<string> users,
        string roomId,
        string reason
    )
    {
        foreach (
            var user
            in users.Distinct(
                StringComparer
                    .OrdinalIgnoreCase
            )
        )
        {
            await hub.Clients
                .User(
                    user
                )
                .SendAsync(
                    "GroupDmUpdated",
                    new
                    {
                        roomId,
                        reason
                    }
                );
        }
    }
}
