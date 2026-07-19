using System;
using System.Collections.Generic;
using System.Linq;


public static class PrivateDmVoiceSecurity
{
    public static string NormalizeUser(
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


    public static string PairKey(
        string first,
        string second
    )
    {
        first =
            NormalizeUser(
                first
            );

        second =
            NormalizeUser(
                second
            );

        return string.CompareOrdinal(
            first,
            second
        ) <= 0
            ? first + "\n" + second
            : second + "\n" + first;
    }


    public static bool AreFriends(
        string first,
        string second
    )
    {
        first =
            NormalizeUser(
                first
            );

        second =
            NormalizeUser(
                second
            );

        if (
            string.IsNullOrWhiteSpace(
                first
            )
            ||
            string.IsNullOrWhiteSpace(
                second
            )
            ||
            first.Equals(
                second,
                StringComparison.OrdinalIgnoreCase
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
                FriendRequests

            WHERE
                LOWER(
                    COALESCE(
                        Status,
                        ''
                    )
                ) = 'accepted'

                AND

                (
                    (
                        LOWER(FromUser) = $first
                        AND
                        LOWER(ToUser) = $second
                    )

                    OR

                    (
                        LOWER(FromUser) = $second
                        AND
                        LOWER(ToUser) = $first
                    )
                )
            """;

        cmd.Parameters
            .AddWithValue(
                "$first",
                first
            );

        cmd.Parameters
            .AddWithValue(
                "$second",
                second
            );

        return Convert.ToInt32(
            cmd.ExecuteScalar()
            ?? 0
        ) > 0;
    }
}


public sealed class PrivateDmVoiceManager
{
    private sealed class Session
    {
        public required string Key
        {
            get;
            init;
        }

        public required string Caller
        {
            get;
            init;
        }

        public required string Callee
        {
            get;
            init;
        }

        public DateTime CreatedAt
        {
            get;
            init;
        } =
            DateTime.UtcNow;

        public Dictionary<
            string,
            string
        > Connections
        {
            get;
        } =
            new(
                StringComparer
                    .OrdinalIgnoreCase
            );
    }


    private readonly object _gate =
        new();

    private readonly Dictionary<
        string,
        Session
    > _sessions =
        new(
            StringComparer.Ordinal
        );

    private readonly Dictionary<
        string,
        string
    > _sessionByUser =
        new(
            StringComparer.OrdinalIgnoreCase
        );

    private readonly Dictionary<
        string,
        string
    > _sessionByConnection =
        new(
            StringComparer.Ordinal
        );


    public bool TryStart(
        string caller,
        string callee,
        string connectionId,
        out string error
    )
    {
        lock (
            _gate
        )
        {
            PruneNoLock();

            caller =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        caller
                    );

            callee =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        callee
                    );

            if (
                _sessionByUser
                    .ContainsKey(
                        caller
                    )
            )
            {
                error =
                    "Leave your current call first.";

                return false;
            }

            if (
                _sessionByUser
                    .ContainsKey(
                        callee
                    )
            )
            {
                error =
                    "This user is already in another call.";

                return false;
            }

            var key =
                PrivateDmVoiceSecurity
                    .PairKey(
                        caller,
                        callee
                    );

            var session =
                new Session
                {
                    Key =
                        key,

                    Caller =
                        caller,

                    Callee =
                        callee
                };

            session
                .Connections[
                    caller
                ] =
                    connectionId;

            _sessions[
                key
            ] =
                session;

            _sessionByUser[
                caller
            ] =
                key;

            _sessionByUser[
                callee
            ] =
                key;

            _sessionByConnection[
                connectionId
            ] =
                key;

            error =
                "";

            return true;
        }
    }


    public bool TryAccept(
        string callee,
        string caller,
        string connectionId,
        out List<string> users,
        out string error
    )
    {
        lock (
            _gate
        )
        {
            PruneNoLock();

            callee =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        callee
                    );

            caller =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        caller
                    );

            var key =
                PrivateDmVoiceSecurity
                    .PairKey(
                        caller,
                        callee
                    );

            if (
                !_sessions
                    .TryGetValue(
                        key,
                        out var session
                    )
                ||
                !session.Caller.Equals(
                    caller,
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                !session.Callee.Equals(
                    callee,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                users =
                    new();

                error =
                    "This call is no longer available.";

                return false;
            }

            if (
                !session.Connections
                    .ContainsKey(
                        caller
                    )
            )
            {
                RemoveSessionNoLock(
                    key
                );

                users =
                    new();

                error =
                    "The caller disconnected.";

                return false;
            }

            session
                .Connections[
                    callee
                ] =
                    connectionId;

            _sessionByConnection[
                connectionId
            ] =
                key;

            users =
                session
                    .Connections
                    .Keys
                    .OrderBy(
                        value =>
                            value
                    )
                    .ToList();

            error =
                "";

            return true;
        }
    }


    public bool TryEndPair(
        string requester,
        string peer,
        out string otherUser
    )
    {
        lock (
            _gate
        )
        {
            requester =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        requester
                    );

            peer =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        peer
                    );

            var key =
                PrivateDmVoiceSecurity
                    .PairKey(
                        requester,
                        peer
                    );

            if (
                !_sessions
                    .TryGetValue(
                        key,
                        out var session
                    )
            )
            {
                otherUser =
                    peer;

                return false;
            }

            if (
                !session.Caller.Equals(
                    requester,
                    StringComparison.OrdinalIgnoreCase
                )
                &&
                !session.Callee.Equals(
                    requester,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                otherUser =
                    "";

                return false;
            }

            otherUser =
                session.Caller.Equals(
                    requester,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? session.Callee
                    : session.Caller;

            RemoveSessionNoLock(
                key
            );

            return true;
        }
    }


    public bool TryEndConnection(
        string connectionId,
        out string username,
        out string otherUser
    )
    {
        lock (
            _gate
        )
        {
            if (
                !_sessionByConnection
                    .TryGetValue(
                        connectionId,
                        out var key
                    )
                ||
                !_sessions
                    .TryGetValue(
                        key,
                        out var session
                    )
            )
            {
                username =
                    "";

                otherUser =
                    "";

                return false;
            }

            username =
                session
                    .Connections
                    .FirstOrDefault(
                        item =>
                            item.Value.Equals(
                                connectionId,
                                StringComparison.Ordinal
                            )
                    )
                    .Key
                ??
                "";

            if (
                string.IsNullOrWhiteSpace(
                    username
                )
            )
            {
                username =
                    session.Caller;
            }

            otherUser =
                session.Caller.Equals(
                    username,
                    StringComparison.OrdinalIgnoreCase
                )
                    ? session.Callee
                    : session.Caller;

            RemoveSessionNoLock(
                key
            );

            return true;
        }
    }


    public bool IsConnected(
        string first,
        string second,
        string username
    )
    {
        lock (
            _gate
        )
        {
            var key =
                PrivateDmVoiceSecurity
                    .PairKey(
                        first,
                        second
                    );

            username =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        username
                    );

            return
                _sessions
                    .TryGetValue(
                        key,
                        out var session
                    )
                &&
                session
                    .Connections
                    .ContainsKey(
                        username
                    );
        }
    }


    public bool TryGetConnection(
        string first,
        string second,
        string username,
        out string connectionId
    )
    {
        lock (
            _gate
        )
        {
            var key =
                PrivateDmVoiceSecurity
                    .PairKey(
                        first,
                        second
                    );

            username =
                PrivateDmVoiceSecurity
                    .NormalizeUser(
                        username
                    );

            if (
                _sessions
                    .TryGetValue(
                        key,
                        out var session
                    )
                &&
                session
                    .Connections
                    .TryGetValue(
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


    private void PruneNoLock()
    {
        var expired =
            _sessions
                .Where(
                    item =>
                        item.Value
                            .CreatedAt
                        <
                        DateTime.UtcNow
                            .AddMinutes(
                                -2
                            )
                        &&
                        item.Value
                            .Connections
                            .Count
                        <
                        2
                )
                .Select(
                    item =>
                        item.Key
                )
                .ToList();

        foreach (
            var key
            in expired
        )
        {
            RemoveSessionNoLock(
                key
            );
        }
    }


    private void RemoveSessionNoLock(
        string key
    )
    {
        if (
            !_sessions
                .TryGetValue(
                    key,
                    out var session
                )
        )
        {
            return;
        }

        _sessions.Remove(
            key
        );

        _sessionByUser.Remove(
            session.Caller
        );

        _sessionByUser.Remove(
            session.Callee
        );

        foreach (
            var connection
            in session
                .Connections
                .Values
        )
        {
            _sessionByConnection.Remove(
                connection
            );
        }
    }
}
