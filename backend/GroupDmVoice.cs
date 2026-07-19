using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static class GroupDmVoiceSecurity
{
    public const int MaxParticipants = 6;

    public static string NormalizeRoomId(
        string? roomId
    )
    {
        return
            (
                roomId
                ?? ""
            )
            .Trim()
            .ToLowerInvariant();
    }

    public static string NormalizeUsername(
        string? username
    )
    {
        return
            (
                username
                ?? ""
            )
            .Trim()
            .ToLowerInvariant();
    }

    public static bool IsValidRoomId(
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

    public static bool IsMember(
        string roomId,
        string username
    )
    {
        roomId =
            NormalizeRoomId(
                roomId
            );

        username =
            NormalizeUsername(
                username
            );

        if (
            !IsValidRoomId(
                roomId
            )
            ||
            string.IsNullOrWhiteSpace(
                username
            )
        )
        {
            return false;
        }

        using var db =
            DbHelpers.OpenDb();

        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
            SELECT
                COUNT(*)

            FROM
                GroupDmMembers

            WHERE
                RoomId =
                    $room

                AND

                Username =
                    $user
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

        return
            Convert.ToInt32(
                cmd.ExecuteScalar()
                ?? 0
            )
            >
            0;
    }

    public static List<string> GetMembers(
        string roomId
    )
    {
        var result =
            new List<string>();

        roomId =
            NormalizeRoomId(
                roomId
            );

        if (
            !IsValidRoomId(
                roomId
            )
        )
        {
            return result;
        }

        using var db =
            DbHelpers.OpenDb();

        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
            SELECT
                Username

            FROM
                GroupDmMembers

            WHERE
                RoomId =
                    $room

            ORDER BY
                Id ASC
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
            result.Add(
                reader.GetString(
                    0
                )
            );
        }

        return result;
    }

    public static string SignalRGroup(
        string roomId
    )
    {
        return
            "group-dm-voice:"
            +
            NormalizeRoomId(
                roomId
            );
    }
}


public sealed class GroupDmVoiceManager
{
    private readonly object _gate =
        new();

    private readonly Dictionary<
        string,
        Dictionary<string, string>
    > _rooms =
        new(
            StringComparer.OrdinalIgnoreCase
        );

    private readonly Dictionary<
        string,
        (
            string RoomId,
            string Username
        )
    > _connections =
        new(
            StringComparer.Ordinal
        );

    private readonly Dictionary<
        string,
        string
    > _userConnections =
        new(
            StringComparer.OrdinalIgnoreCase
        );


    public bool TryJoin(
        string roomId,
        string username,
        string connectionId,
        out List<string> users
    )
    {
        lock (
            _gate
        )
        {
            roomId =
                GroupDmVoiceSecurity
                    .NormalizeRoomId(
                        roomId
                    );

            username =
                GroupDmVoiceSecurity
                    .NormalizeUsername(
                        username
                    );

            if (
                _userConnections
                    .TryGetValue(
                        username,
                        out var oldConnection
                    )
            )
            {
                RemoveConnectionNoLock(
                    oldConnection
                );
            }

            if (
                _connections
                    .ContainsKey(
                        connectionId
                    )
            )
            {
                RemoveConnectionNoLock(
                    connectionId
                );
            }

            if (
                !_rooms
                    .TryGetValue(
                        roomId,
                        out var room
                    )
            )
            {
                room =
                    new Dictionary<
                        string,
                        string
                    >(
                        StringComparer
                            .OrdinalIgnoreCase
                    );

                _rooms[
                    roomId
                ] =
                    room;
            }

            if (
                !room.ContainsKey(
                    username
                )
                &&
                room.Count
                >=
                GroupDmVoiceSecurity
                    .MaxParticipants
            )
            {
                users =
                    room.Keys
                        .OrderBy(
                            value =>
                                value
                        )
                        .ToList();

                return false;
            }

            room[
                username
            ] =
                connectionId;

            _connections[
                connectionId
            ] =
                (
                    roomId,
                    username
                );

            _userConnections[
                username
            ] =
                connectionId;

            users =
                room.Keys
                    .OrderBy(
                        value =>
                            value
                    )
                    .ToList();

            return true;
        }
    }


    public bool TryLeaveConnection(
        string connectionId,
        out string roomId,
        out string username,
        out List<string> remainingUsers
    )
    {
        lock (
            _gate
        )
        {
            if (
                !_connections
                    .TryGetValue(
                        connectionId,
                        out var current
                    )
            )
            {
                roomId =
                    "";

                username =
                    "";

                remainingUsers =
                    new List<string>();

                return false;
            }

            roomId =
                current.RoomId;

            username =
                current.Username;

            RemoveConnectionNoLock(
                connectionId
            );

            remainingUsers =
                GetUsersNoLock(
                    roomId
                );

            return true;
        }
    }


    public bool IsParticipant(
        string roomId,
        string username
    )
    {
        lock (
            _gate
        )
        {
            roomId =
                GroupDmVoiceSecurity
                    .NormalizeRoomId(
                        roomId
                    );

            username =
                GroupDmVoiceSecurity
                    .NormalizeUsername(
                        username
                    );

            return
                _rooms
                    .TryGetValue(
                        roomId,
                        out var room
                    )
                &&
                room.ContainsKey(
                    username
                );
        }
    }


    public bool TryGetConnection(
        string roomId,
        string username,
        out string connectionId
    )
    {
        lock (
            _gate
        )
        {
            roomId =
                GroupDmVoiceSecurity
                    .NormalizeRoomId(
                        roomId
                    );

            username =
                GroupDmVoiceSecurity
                    .NormalizeUsername(
                        username
                    );

            if (
                _rooms
                    .TryGetValue(
                        roomId,
                        out var room
                    )
                &&
                room.TryGetValue(
                    username,
                    out var found
                )
            )
            {
                connectionId =
                    found;

                return true;
            }

            connectionId =
                "";

            return false;
        }
    }


    public List<string> GetUsers(
        string roomId
    )
    {
        lock (
            _gate
        )
        {
            return
                GetUsersNoLock(
                    GroupDmVoiceSecurity
                        .NormalizeRoomId(
                            roomId
                        )
                );
        }
    }


    private List<string> GetUsersNoLock(
        string roomId
    )
    {
        if (
            !_rooms
                .TryGetValue(
                    roomId,
                    out var room
                )
        )
        {
            return
                new List<string>();
        }

        return
            room.Keys
                .OrderBy(
                    value =>
                        value
                )
                .ToList();
    }


    private void RemoveConnectionNoLock(
        string connectionId
    )
    {
        if (
            !_connections
                .TryGetValue(
                    connectionId,
                    out var current
                )
        )
        {
            return;
        }

        _connections.Remove(
            connectionId
        );

        if (
            _userConnections
                .TryGetValue(
                    current.Username,
                    out var mappedConnection
                )
            &&
            mappedConnection.Equals(
                connectionId,
                StringComparison.Ordinal
            )
        )
        {
            _userConnections.Remove(
                current.Username
            );
        }

        if (
            _rooms
                .TryGetValue(
                    current.RoomId,
                    out var room
                )
        )
        {
            if (
                room.TryGetValue(
                    current.Username,
                    out var roomConnection
                )
                &&
                roomConnection.Equals(
                    connectionId,
                    StringComparison.Ordinal
                )
            )
            {
                room.Remove(
                    current.Username
                );
            }

            if (
                room.Count == 0
            )
            {
                _rooms.Remove(
                    current.RoomId
                );
            }
        }
    }
}


public static class GroupDmVoiceEndpoints
{
    public static void Register(
        WebApplication app
    )
    {
        app.MapGet(
            "/api/group-dms/voice/ice",
            GetIceServers
        )
        .RequireAuthorization();

        app.MapGet(
            "/api/voice/ice",
            GetIceServers
        )
        .RequireAuthorization();

    }


    private static IResult GetIceServers(
        HttpContext ctx
    )
    {
        var me =
            ctx.User
                .Identity
                ?.Name
                ?.Trim()
                .ToLowerInvariant()
            ?? "";

        if (
            string.IsNullOrWhiteSpace(
                me
            )
            ||
            !AppHelpers.UserExists(
                me
            )
        )
        {
            return Results.Unauthorized();
        }

        var stunUrls =
            SplitUrls(
                Environment
                    .GetEnvironmentVariable(
                        "RUNSPACE_STUN_URLS"
                    )
                ??
                "stun:runspace.cloud:3478"
            );

        var turnUrls =
            SplitUrls(
                Environment
                    .GetEnvironmentVariable(
                        "RUNSPACE_TURN_URLS"
                    )
                ??
                ""
            );

        var iceServers =
            new List<object>();

        if (
            stunUrls.Length > 0
        )
        {
            iceServers.Add(
                new
                {
                    urls =
                        stunUrls
                }
            );
        }

        var secret =
            DecodeEnvironmentBase64(
                "RUNSPACE_TURN_SECRET_B64"
            );

        var staticUsername =
            DecodeEnvironmentBase64(
                "RUNSPACE_TURN_USERNAME_B64"
            );

        var staticCredential =
            DecodeEnvironmentBase64(
                "RUNSPACE_TURN_CREDENTIAL_B64"
            );

        var turnConfigured =
            false;

        var ttlSeconds =
            3600;

        if (
            turnUrls.Length > 0
            &&
            !string.IsNullOrWhiteSpace(
                secret
            )
        )
        {
            var expires =
                DateTimeOffset
                    .UtcNow
                    .AddSeconds(
                        ttlSeconds
                    )
                    .ToUnixTimeSeconds();

            var username =
                expires
                    .ToString()
                +
                ":"
                +
                me;

            using var hmac =
                new HMACSHA1(
                    Encoding.UTF8
                        .GetBytes(
                            secret
                        )
                );

            var credential =
                Convert
                    .ToBase64String(
                        hmac.ComputeHash(
                            Encoding.UTF8
                                .GetBytes(
                                    username
                                )
                        )
                    );

            iceServers.Add(
                new
                {
                    urls =
                        turnUrls,

                    username,

                    credential
                }
            );

            turnConfigured =
                true;
        }
        else if (
            turnUrls.Length > 0
            &&
            !string.IsNullOrWhiteSpace(
                staticUsername
            )
            &&
            !string.IsNullOrWhiteSpace(
                staticCredential
            )
        )
        {
            iceServers.Add(
                new
                {
                    urls =
                        turnUrls,

                    username =
                        staticUsername,

                    credential =
                        staticCredential
                }
            );

            turnConfigured =
                true;
        }

        return Results.Ok(
            new
            {
                iceServers,

                turnConfigured,

                ttlSeconds,

                maxParticipants =
                    GroupDmVoiceSecurity
                        .MaxParticipants
            }
        );
    }


    private static string[] SplitUrls(
        string value
    )
    {
        return
            value
                .Split(
                    ',',
                    StringSplitOptions
                        .RemoveEmptyEntries
                    |
                    StringSplitOptions
                        .TrimEntries
                )
                .Where(
                    item =>
                        item.StartsWith(
                            "stun:",
                            StringComparison
                                .OrdinalIgnoreCase
                        )
                        ||
                        item.StartsWith(
                            "turn:",
                            StringComparison
                                .OrdinalIgnoreCase
                        )
                        ||
                        item.StartsWith(
                            "turns:",
                            StringComparison
                                .OrdinalIgnoreCase
                        )
                )
                .Distinct(
                    StringComparer
                        .OrdinalIgnoreCase
                )
                .ToArray();
    }


    private static string DecodeEnvironmentBase64(
        string variable
    )
    {
        var value =
            Environment
                .GetEnvironmentVariable(
                    variable
                )
            ??
            "";

        if (
            string.IsNullOrWhiteSpace(
                value
            )
        )
        {
            return "";
        }

        try
        {
            return
                Encoding.UTF8
                    .GetString(
                        Convert
                            .FromBase64String(
                                value
                            )
                    );
        }
        catch
        {
            return "";
        }
    }
}
