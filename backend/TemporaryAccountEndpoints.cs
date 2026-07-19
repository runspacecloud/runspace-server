using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

public record TemporaryAccountCreateRequest(
    int DurationHours
);

public record TemporaryAccountCreated(
    string Username,
    string PublicId,
    string AccountKey,
    int DurationHours,
    DateTime ExpiresAt
);

public static class TemporaryAccountEndpoints
{
    public static void Register(
        WebApplication app
    )
    {
        TemporaryAccountStore
            .EnsureDatabase();

        app.MapPost(
            "/api/auth/register-temporary",
            async (
                HttpContext ctx
            ) =>
            {
                if (
                    ctx.User.Identity
                        ?.IsAuthenticated
                    == true
                )
                {
                    return Results.Conflict(
                        new
                        {
                            error =
                                "Sign out before creating a temporary account."
                        }
                    );
                }

                TemporaryAccountCreateRequest?
                    request = null;

                if (
                    ctx.Request.ContentLength
                    is > 0
                )
                {
                    request =
                        await ctx.Request
                            .ReadFromJsonAsync<
                                TemporaryAccountCreateRequest
                            >();
                }

                var durationHours =
                    TemporaryAccountStore
                        .NormalizeDuration(
                            request
                                ?.DurationHours
                            ?? 24
                        );

                var ip =
                    ctx.Connection
                        .RemoteIpAddress
                        ?.ToString()
                    ?? "unknown";

                var ipHash =
                    TemporaryAccountStore
                        .HashIp(ip);

                if (
                    !TemporaryAccountStore
                        .CanCreate(
                            ipHash
                        )
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Temporary account creation limit reached. Try again later."
                        },
                        statusCode: 429
                    );
                }

                TemporaryAccountCreated
                    account;

                try
                {
                    account =
                        TemporaryAccountStore
                            .Create(
                                ip,
                                ipHash,
                                durationHours
                            );
                }
                catch (
                    Exception
                )
                {
                    return Results.Json(
                        new
                        {
                            error =
                                "Could not create the temporary account."
                        },
                        statusCode: 500
                    );
                }

                // Use the exact same cookie identity format
                // as RunSpace register-with-key.
                var identity =
                    new System.Security.Claims.ClaimsIdentity(
                        new[]
                        {
                            new System.Security.Claims.Claim(
                                System.Security.Claims.ClaimTypes.Name,
                                account.Username
                            ),

                            new System.Security.Claims.Claim(
                                "temporary",
                                "true"
                            ),

                            new System.Security.Claims.Claim(
                                "temporary_expires_at",
                                account.ExpiresAt.ToString("o")
                            )
                        },
                        "cookie"
                    );

                var principal =
                    new System.Security.Claims.ClaimsPrincipal(
                        identity
                    );

                var issuedAt =
                    DateTimeOffset.UtcNow;

                var expiresAt =
                    new DateTimeOffset(
                        account.ExpiresAt
                    );

                await ctx.SignInAsync(
                    Microsoft.AspNetCore.Authentication.Cookies
                        .CookieAuthenticationDefaults
                        .AuthenticationScheme,

                    principal,

                    new Microsoft.AspNetCore.Authentication
                        .AuthenticationProperties
                    {
                        IsPersistent = true,
                        IssuedUtc = issuedAt,
                        ExpiresUtc = expiresAt,
                        AllowRefresh = false
                    }
                );

                // Make the authenticated user available
                // during the remainder of this request too.
                ctx.User =
                    principal;

                return Results.Ok(
                    new
                    {
                        success = true,

                        username =
                            account.Username,

                        publicId =
                            account.PublicId,

                        accountKey =
                            account.AccountKey,

                        durationHours =
                            account.DurationHours,

                        temporary = true,

                        uuidVersion = 6,

                        expiresAt =
                            account.ExpiresAt
                                .ToString("o")
                    }
                );
            }
        );

        app.MapGet(
            "/api/account/temporary-status",
            (
                HttpContext ctx
            ) =>
            {
                var username =
                    (
                        ctx.User.Identity
                            ?.Name
                        ?? ""
                    )
                    .Trim();

                if (
                    string.IsNullOrWhiteSpace(
                        username
                    )
                )
                {
                    return Results
                        .Unauthorized();
                }

                using var db =
                    DbHelpers.OpenDb();

                using var cmd =
                    db.CreateCommand();

                cmd.CommandText = """
                SELECT
                    IsTemporary,
                    TemporaryExpiresAt
                FROM AuthUsers
                WHERE LOWER(Username)
                    = LOWER($username)
                LIMIT 1
                """;

                cmd.Parameters
                    .AddWithValue(
                        "$username",
                        username
                    );

                using var reader =
                    cmd.ExecuteReader();

                if (!reader.Read())
                {
                    return Results.NotFound();
                }

                var isTemporary =
                    !reader.IsDBNull(0)
                    && reader.GetInt32(0)
                        == 1;

                var expiresAt =
                    reader.IsDBNull(1)
                        ? ""
                        : reader.GetString(1);

                return Results.Ok(
                    new
                    {
                        username,

                        temporary =
                            isTemporary,

                        expiresAt
                    }
                );
            }
        );
    }
}

public static class TemporaryAccountStore
{
    private const int
        MaximumCreationsPerDay = 20;

    private static readonly
        DateTime UuidEpoch =
            new(
                1582,
                10,
                15,
                0,
                0,
                0,
                DateTimeKind.Utc
            );

    private static readonly
        HashSet<string>
        UsernameColumns =
            new(
                StringComparer
                    .OrdinalIgnoreCase
            )
            {
                "Username",
                "FromUser",
                "ToUser",
                "UserA",
                "UserB",
                "OwnerUsername",
                "InvitedBy",
                "InvitedUser",
                "CreatedBy",
                "CreatedByUsername",
                "CreatorUsername",
                "TargetUser",
                "TargetUsername",
                "RequesterUsername",
                "RecipientUsername",
                "SenderUsername",
                "MemberUsername",
                "BlockedUser",
                "BlockerUser"
            };

    public static void EnsureDatabase()
    {
        using var db =
            DbHelpers.OpenDb();

        EnsureColumn(
            db,
            "IsTemporary",
            "INTEGER NOT NULL DEFAULT 0"
        );

        EnsureColumn(
            db,
            "TemporaryExpiresAt",
            "TEXT NOT NULL DEFAULT ''"
        );

        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS
        TemporaryAccountCreationLog
        (
            Id INTEGER
                PRIMARY KEY
                AUTOINCREMENT,

            IpHash TEXT
                NOT NULL,

            CreatedAt TEXT
                NOT NULL
        );

        CREATE INDEX IF NOT EXISTS
        IX_TemporaryAccountCreationLog_Ip
        ON TemporaryAccountCreationLog
        (
            IpHash,
            CreatedAt
        );

        CREATE INDEX IF NOT EXISTS
        IX_AuthUsers_TemporaryExpiry
        ON AuthUsers
        (
            IsTemporary,
            TemporaryExpiresAt
        );

        CREATE TRIGGER IF NOT EXISTS
        TR_AuthUsers_LockTemporaryUsername

        BEFORE UPDATE OF Username
        ON AuthUsers

        WHEN
            OLD.IsTemporary = 1

            AND

            LOWER(
                NEW.Username
            )
            <>
            LOWER(
                OLD.Username
            )

        BEGIN

            SELECT RAISE(
                ABORT,
                'temporary_username_locked'
            );

        END;
        """;

        cmd.ExecuteNonQuery();
    }

    public static string HashIp(
        string ip
    )
    {
        var pepper =
            Environment
                .GetEnvironmentVariable(
                    "RUNSPACE_PEPPER"
                )
            ?? "runspace-temporary-account";

        var bytes =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    pepper
                    + "|"
                    + ip
                )
            );

        return Convert
            .ToHexString(bytes)
            .ToLowerInvariant();
    }

    public static bool CanCreate(
        string ipHash
    )
    {
        EnsureDatabase();

        using var db =
            DbHelpers.OpenDb();

        var cutoff =
            DateTime.UtcNow
                .AddDays(-1)
                .ToString("o");

        using (
            var cleanup =
                db.CreateCommand()
        )
        {
            cleanup.CommandText = """
            DELETE FROM
                TemporaryAccountCreationLog

            WHERE CreatedAt < $cutoff
            """;

            cleanup.Parameters
                .AddWithValue(
                    "$cutoff",
                    cutoff
                );

            cleanup.ExecuteNonQuery();
        }

        using var count =
            db.CreateCommand();

        count.CommandText = """
        SELECT COUNT(*)

        FROM
            TemporaryAccountCreationLog

        WHERE
            IpHash = $ip

            AND

            CreatedAt >= $cutoff
        """;

        count.Parameters
            .AddWithValue(
                "$ip",
                ipHash
            );

        count.Parameters
            .AddWithValue(
                "$cutoff",
                cutoff
            );

        var created =
            Convert.ToInt32(
                count.ExecuteScalar()
            );

        return created
            < MaximumCreationsPerDay;
    }

    public static
        TemporaryAccountCreated
        Create(
            string ip,
            string ipHash,
            int durationHours
        )
    {
        EnsureDatabase();

        using var db =
            DbHelpers.OpenDb();

        string username = "";
        string publicId = "";
        string accountKey = "";
        string accountKeyHash = "";

        for (
            var attempt = 0;
            attempt < 30;
            attempt++
        )
        {
            username =
                CreateBurnerUsername();

            publicId =
                CreateUuidV6();

            accountKey =
                Guid.NewGuid()
                    .ToString("D")
                    .ToLowerInvariant();

            accountKey =
                AccountKeyHashing
                    .Normalize(
                        accountKey
                    );

            accountKeyHash =
                AccountKeyHashing
                    .Hash(
                        accountKey
                    );

            using var duplicate =
                db.CreateCommand();

            duplicate.CommandText = """
            SELECT COUNT(*)

            FROM AuthUsers

            WHERE
                LOWER(Username)
                = LOWER($username)

                OR

                AccountKeyHash
                = $keyHash
            """;

            duplicate.Parameters
                .AddWithValue(
                    "$username",
                    username
                );

            duplicate.Parameters
                .AddWithValue(
                    "$keyHash",
                    accountKeyHash
                );

            var exists =
                Convert.ToInt32(
                    duplicate
                        .ExecuteScalar()
                ) > 0;

            if (!exists)
            {
                break;
            }

            username = "";
        }

        if (
            string.IsNullOrWhiteSpace(
                username
            )
        )
        {
            throw new InvalidOperationException(
                "Could not generate a unique account."
            );
        }

        var now =
            DateTime.UtcNow;

        durationHours =
            NormalizeDuration(
                durationHours
            );

        var expiresAt =
            now.AddHours(
                durationHours
            );

        var hiddenPassword =
            Convert.ToHexString(
                RandomNumberGenerator
                    .GetBytes(32)
            );

        var passwordHash =
            PasswordHashing
                .HashPassword(
                    hiddenPassword
                );

        using (
            var insert =
                db.CreateCommand()
        )
        {
            insert.CommandText = """
            INSERT INTO AuthUsers
            (
                Username,
                PasswordHash,
                Bio,
                AvatarUrl,
                CreatedAt,
                Status,
                Badges,
                PublicKey,
                TwoFactorEnabled,
                TwoFactorSecret,
                PasswordChangedAt,
                LoginCount,
                LastLoginAt,
                LastLoginIp,
                AccountLockedUntil,
                Email,
                EmailVerified,
                AccountKey,
                AccountKeyHash,
                PublicId,
                IsTemporary,
                TemporaryExpiresAt
            )
            VALUES
            (
                $username,
                $passwordHash,
                '',
                '',
                $createdAt,
                'verified',
                '["temporary"]',
                '',
                0,
                '',
                '',
                1,
                $createdAt,
                '',
                '',
                '',
                0,
                $accountKey,
                $accountKeyHash,
                $publicId,
                1,
                $expiresAt
            )
            """;

            insert.Parameters
                .AddWithValue(
                    "$username",
                    username
                );

            insert.Parameters
                .AddWithValue(
                    "$passwordHash",
                    passwordHash
                );

            insert.Parameters
                .AddWithValue(
                    "$createdAt",
                    now.ToString("o")
                );

            insert.Parameters
                .AddWithValue(
                    "$accountKey",
                    accountKey
                );

            insert.Parameters
                .AddWithValue(
                    "$accountKeyHash",
                    accountKeyHash
                );

            insert.Parameters
                .AddWithValue(
                    "$publicId",
                    publicId
                );

            insert.Parameters
                .AddWithValue(
                    "$expiresAt",
                    expiresAt
                        .ToString("o")
                );

            insert.ExecuteNonQuery();
        }

        using (
            var creation =
                db.CreateCommand()
        )
        {
            creation.CommandText = """
            INSERT INTO
                TemporaryAccountCreationLog
            (
                IpHash,
                CreatedAt
            )
            VALUES
            (
                $ip,
                $createdAt
            )
            """;

            creation.Parameters
                .AddWithValue(
                    "$ip",
                    ipHash
                );

            creation.Parameters
                .AddWithValue(
                    "$createdAt",
                    now.ToString("o")
                );

            creation.ExecuteNonQuery();
        }

        return new(
            username,
            publicId,
            accountKey,
            durationHours,
            expiresAt
        );
    }

    public static int DeleteExpired()
    {
        EnsureDatabase();

        using var db =
            DbHelpers.OpenDb();

        var expiredUsers =
            new List<string>();

        using (
            var find =
                db.CreateCommand()
        )
        {
            find.CommandText = """
            SELECT Username

            FROM AuthUsers

            WHERE
                IsTemporary = 1

                AND

                TemporaryExpiresAt
                    <> ''

                AND

                TemporaryExpiresAt
                    <= $now
            """;

            find.Parameters
                .AddWithValue(
                    "$now",
                    DateTime.UtcNow
                        .ToString("o")
                );

            using var reader =
                find.ExecuteReader();

            while (
                reader.Read()
            )
            {
                if (
                    !reader.IsDBNull(0)
                )
                {
                    expiredUsers.Add(
                        reader.GetString(0)
                    );
                }
            }
        }

        foreach (
            var username
            in expiredUsers
        )
        {
            DeleteAccountData(
                db,
                username
            );
        }

        return expiredUsers.Count;
    }

    private static void
        DeleteAccountData(
            SqliteConnection db,
            string username
        )
    {
        DeleteReactionsForMessages(
            db,
            username
        );

        DeleteOwnedGroups(
            db,
            username
        );

        DeleteOwnedTempRooms(
            db,
            username
        );

        var tables =
            GetTables(db);

        foreach (
            var table
            in tables
        )
        {
            if (
                table.Equals(
                    "AuthUsers",
                    StringComparison
                        .OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var columns =
                GetColumns(
                    db,
                    table
                );

            var matching =
                columns
                    .Where(
                        column =>
                            UsernameColumns
                                .Contains(
                                    column
                                )
                    )
                    .ToList();

            if (
                matching.Count
                == 0
            )
            {
                continue;
            }

            var conditions =
                matching
                    .Select(
                        column =>
                            "LOWER(CAST("
                            + Quote(column)
                            + " AS TEXT))"
                            + " = "
                            + "LOWER($username)"
                    );

            using var delete =
                db.CreateCommand();

            delete.CommandText =
                "DELETE FROM "
                + Quote(table)
                + " WHERE "
                + string.Join(
                    " OR ",
                    conditions
                );

            delete.Parameters
                .AddWithValue(
                    "$username",
                    username
                );

            delete.ExecuteNonQuery();
        }

        using var userDelete =
            db.CreateCommand();

        userDelete.CommandText = """
        DELETE FROM AuthUsers

        WHERE
            IsTemporary = 1

            AND

            LOWER(Username)
                = LOWER($username)
        """;

        userDelete.Parameters
            .AddWithValue(
                "$username",
                username
            );

        userDelete.ExecuteNonQuery();
    }

    private static void
        DeleteReactionsForMessages(
            SqliteConnection db,
            string username
        )
    {
        if (
            !TableExists(
                db,
                "ChatMessages"
            )
            ||
            !TableExists(
                db,
                "ChatReactions"
            )
        )
        {
            return;
        }

        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
        DELETE FROM ChatReactions

        WHERE MessageId IN
        (
            SELECT Id

            FROM ChatMessages

            WHERE
                LOWER(FromUser)
                    = LOWER($username)

                OR

                LOWER(ToUser)
                    = LOWER($username)
        )
        """;

        cmd.Parameters
            .AddWithValue(
                "$username",
                username
            );

        cmd.ExecuteNonQuery();
    }

    private static void
        DeleteOwnedGroups(
            SqliteConnection db,
            string username
        )
    {
        if (
            !TableExists(
                db,
                "Groups"
            )
            ||
            !ColumnExists(
                db,
                "Groups",
                "GroupId"
            )
            ||
            !ColumnExists(
                db,
                "Groups",
                "OwnerUsername"
            )
        )
        {
            return;
        }

        var groupIds =
            new List<string>();

        using (
            var find =
                db.CreateCommand()
        )
        {
            find.CommandText = """
            SELECT GroupId

            FROM Groups

            WHERE
                LOWER(OwnerUsername)
                = LOWER($username)
            """;

            find.Parameters
                .AddWithValue(
                    "$username",
                    username
                );

            using var reader =
                find.ExecuteReader();

            while (
                reader.Read()
            )
            {
                groupIds.Add(
                    Convert.ToString(
                        reader.GetValue(0)
                    )
                    ?? ""
                );
            }
        }

        var children =
            new[]
            {
                "GroupMessages",
                "GroupChannels",
                "GroupInvites",
                "GroupMembers"
            };

        foreach (
            var groupId
            in groupIds
        )
        {
            foreach (
                var child
                in children
            )
            {
                if (
                    !TableExists(
                        db,
                        child
                    )
                    ||
                    !ColumnExists(
                        db,
                        child,
                        "GroupId"
                    )
                )
                {
                    continue;
                }

                using var deleteChild =
                    db.CreateCommand();

                deleteChild.CommandText =
                    "DELETE FROM "
                    + Quote(child)
                    + " WHERE GroupId"
                    + " = $groupId";

                deleteChild.Parameters
                    .AddWithValue(
                        "$groupId",
                        groupId
                    );

                deleteChild
                    .ExecuteNonQuery();
            }
        }
    }

    private static void
        DeleteOwnedTempRooms(
            SqliteConnection db,
            string username
        )
    {
        if (
            !TableExists(
                db,
                "TempChats"
            )
        )
        {
            return;
        }

        var columns =
            GetColumns(
                db,
                "TempChats"
            );

        var ownerColumn =
            new[]
            {
                "OwnerUsername",
                "CreatedBy",
                "CreatorUsername",
                "Username"
            }
            .FirstOrDefault(
                columns.Contains
            );

        var idColumn =
            new[]
            {
                "Id",
                "RoomId"
            }
            .FirstOrDefault(
                columns.Contains
            );

        if (
            string.IsNullOrWhiteSpace(
                ownerColumn
            )
            ||
            string.IsNullOrWhiteSpace(
                idColumn
            )
        )
        {
            return;
        }

        var roomIds =
            new List<string>();

        using (
            var find =
                db.CreateCommand()
        )
        {
            find.CommandText =
                "SELECT "
                + Quote(idColumn)
                + " FROM TempChats"
                + " WHERE LOWER("
                + Quote(ownerColumn)
                + ") = LOWER($username)";

            find.Parameters
                .AddWithValue(
                    "$username",
                    username
                );

            using var reader =
                find.ExecuteReader();

            while (
                reader.Read()
            )
            {
                roomIds.Add(
                    Convert.ToString(
                        reader.GetValue(0)
                    )
                    ?? ""
                );
            }
        }

        var tempTables =
            GetTables(db)
                .Where(
                    name =>
                        name.StartsWith(
                            "TempChat",
                            StringComparison
                                .OrdinalIgnoreCase
                        )
                        &&
                        !name.Equals(
                            "TempChats",
                            StringComparison
                                .OrdinalIgnoreCase
                        )
                )
                .ToList();

        foreach (
            var roomId
            in roomIds
        )
        {
            foreach (
                var table
                in tempTables
            )
            {
                if (
                    !ColumnExists(
                        db,
                        table,
                        "RoomId"
                    )
                )
                {
                    continue;
                }

                using var deleteChild =
                    db.CreateCommand();

                deleteChild.CommandText =
                    "DELETE FROM "
                    + Quote(table)
                    + " WHERE RoomId"
                    + " = $roomId";

                deleteChild.Parameters
                    .AddWithValue(
                        "$roomId",
                        roomId
                    );

                deleteChild
                    .ExecuteNonQuery();
            }
        }
    }

    private static readonly
        HashSet<int>
        AllowedDurationHours =
            new()
            {
                1,
                6,
                24,
                72,
                168
            };

    private static readonly
        string[]
        BurnerAdjectives =
        {
            "Amber",
            "Blue",
            "Bold",
            "Calm",
            "Clever",
            "Copper",
            "Crimson",
            "Frosty",
            "Golden",
            "Green",
            "Hidden",
            "Indigo",
            "Ivory",
            "Jade",
            "Lunar",
            "Misty",
            "Neon",
            "Quiet",
            "Rapid",
            "Red",
            "Silver",
            "Solar",
            "Swift",
            "Violet",
            "Wild",
            "Yellow"
        };

    private static readonly
        string[]
        BurnerAnimals =
        {
            "Badger",
            "Cobra",
            "Eagle",
            "Falcon",
            "Fox",
            "Gecko",
            "Hawk",
            "Heron",
            "Koala",
            "Lynx",
            "Mantis",
            "Otter",
            "Owl",
            "Panda",
            "Puma",
            "Raven",
            "Robin",
            "Shark",
            "Tiger",
            "Wolf"
        };

    public static int
        NormalizeDuration(
            int durationHours
        )
    {
        return AllowedDurationHours
            .Contains(
                durationHours
            )
                ? durationHours
                : 24;
    }

    private static string
        CreateBurnerUsername()
    {
        var adjective =
            BurnerAdjectives[
                RandomNumberGenerator
                    .GetInt32(
                        BurnerAdjectives
                            .Length
                    )
            ];

        var animal =
            BurnerAnimals[
                RandomNumberGenerator
                    .GetInt32(
                        BurnerAnimals
                            .Length
                    )
            ];

        var number =
            RandomNumberGenerator
                .GetInt32(
                    1000,
                    10000
                );

        return adjective
            + animal
            + number;
    }

    private static string
        CreateUuidV6()
    {
        var timestamp =
            (ulong)
            (
                DateTime.UtcNow.Ticks
                -
                UuidEpoch.Ticks
            );

        var timeHigh =
            (uint)
            (
                timestamp
                >> 28
            );

        var timeMid =
            (ushort)
            (
                (
                    timestamp
                    >> 12
                )
                & 0xffff
            );

        var timeLow =
            (ushort)
            (
                timestamp
                & 0x0fff
            );

        var versionField =
            (ushort)
            (
                0x6000
                |
                timeLow
            );

        var random =
            RandomNumberGenerator
                .GetBytes(8);

        var sequence =
            (ushort)
            (
                (
                    (
                        random[0]
                        << 8
                    )
                    |
                    random[1]
                )
                & 0x3fff
            );

        var sequenceHigh =
            (byte)
            (
                0x80
                |
                (
                    sequence
                    >> 8
                )
            );

        var sequenceLow =
            (byte)
            sequence;

        return string.Format(
            "{0:x8}-{1:x4}-{2:x4}-"
            + "{3:x2}{4:x2}-"
            + "{5:x2}{6:x2}{7:x2}"
            + "{8:x2}{9:x2}{10:x2}",

            timeHigh,
            timeMid,
            versionField,
            sequenceHigh,
            sequenceLow,
            random[2],
            random[3],
            random[4],
            random[5],
            random[6],
            random[7]
        );
    }

    private static void
        EnsureColumn(
            SqliteConnection db,
            string column,
            string definition
        )
    {
        if (
            ColumnExists(
                db,
                "AuthUsers",
                column
            )
        )
        {
            return;
        }

        using var cmd =
            db.CreateCommand();

        cmd.CommandText =
            "ALTER TABLE AuthUsers "
            + "ADD COLUMN "
            + Quote(column)
            + " "
            + definition;

        cmd.ExecuteNonQuery();
    }

    private static bool
        TableExists(
            SqliteConnection db,
            string table
        )
    {
        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
        SELECT COUNT(*)

        FROM sqlite_master

        WHERE
            type = 'table'

            AND

            LOWER(name)
                = LOWER($table)
        """;

        cmd.Parameters
            .AddWithValue(
                "$table",
                table
            );

        return Convert.ToInt32(
            cmd.ExecuteScalar()
        ) > 0;
    }

    private static bool
        ColumnExists(
            SqliteConnection db,
            string table,
            string column
        )
    {
        return GetColumns(
            db,
            table
        )
        .Contains(
            column
        );
    }

    private static
        HashSet<string>
        GetColumns(
            SqliteConnection db,
            string table
        )
    {
        var columns =
            new HashSet<string>(
                StringComparer
                    .OrdinalIgnoreCase
            );

        using var cmd =
            db.CreateCommand();

        cmd.CommandText =
            "PRAGMA table_info("
            + Quote(table)
            + ")";

        using var reader =
            cmd.ExecuteReader();

        while (
            reader.Read()
        )
        {
            if (
                !reader.IsDBNull(1)
            )
            {
                columns.Add(
                    reader.GetString(1)
                );
            }
        }

        return columns;
    }

    private static
        List<string>
        GetTables(
            SqliteConnection db
        )
    {
        var tables =
            new List<string>();

        using var cmd =
            db.CreateCommand();

        cmd.CommandText = """
        SELECT name

        FROM sqlite_master

        WHERE
            type = 'table'

            AND

            name NOT LIKE
                'sqlite_%'
        """;

        using var reader =
            cmd.ExecuteReader();

        while (
            reader.Read()
        )
        {
            if (
                !reader.IsDBNull(0)
            )
            {
                tables.Add(
                    reader.GetString(0)
                );
            }
        }

        return tables;
    }

    private static string Quote(
        string identifier
    )
    {
        return "\""
            + identifier.Replace(
                "\"",
                "\"\""
            )
            + "\"";
    }
}

public sealed class
    TemporaryAccountCleanupService
    : BackgroundService
{
    private readonly
        ILogger<
            TemporaryAccountCleanupService
        >
        logger;

    public TemporaryAccountCleanupService(
        ILogger<
            TemporaryAccountCleanupService
        >
        logger
    )
    {
        this.logger =
            logger;
    }

    protected override async Task
        ExecuteAsync(
            CancellationToken
                stoppingToken
        )
    {
        while (
            !stoppingToken
                .IsCancellationRequested
        )
        {
            try
            {
                var deleted =
                    TemporaryAccountStore
                        .DeleteExpired();

                if (
                    deleted > 0
                )
                {
                    logger.LogInformation(
                        "Deleted {Count} expired temporary accounts.",
                        deleted
                    );
                }
            }
            catch (
                Exception ex
            )
            {
                logger.LogError(
                    ex,
                    "Temporary account cleanup failed."
                );
            }

            try
            {
                await Task.Delay(
                    TimeSpan
                        .FromMinutes(1),

                    stoppingToken
                );
            }
            catch (
                OperationCanceledException
            )
            {
                break;
            }
        }
    }
}


public sealed class
    TemporaryAccountRestrictionMiddleware
{
    private readonly
        RequestDelegate next;

    public TemporaryAccountRestrictionMiddleware(
        RequestDelegate next
    )
    {
        this.next =
            next;
    }

    public async Task InvokeAsync(
        HttpContext ctx
    )
    {
        if (
            ctx.User.Identity
                ?.IsAuthenticated
            != true
        )
        {
            await next(ctx);

            return;
        }

        var path =
            ctx.Request.Path.Value
            ?? "";

        if (
            !path.StartsWith(
                "/api/",
                StringComparison
                    .OrdinalIgnoreCase
            )
        )
        {
            await next(ctx);

            return;
        }

        var username =
            (
                ctx.User.Identity
                    ?.Name
                ?? ""
            )
            .Trim();

        if (
            string.IsNullOrWhiteSpace(
                username
            )
        )
        {
            await next(ctx);

            return;
        }

        var claimTemporary =
            (
                ctx.User
                    .FindFirst(
                        "temporary"
                    )
                    ?.Value
                ?? ""
            )
            .Equals(
                "true",
                StringComparison.OrdinalIgnoreCase
            );

        var isTemporary =
            claimTemporary;

        var expiresText =
            ctx.User
                .FindFirst(
                    "temporary_expires_at"
                )
                ?.Value
            ?? "";

        using (
            var db =
                DbHelpers.OpenDb()
        )
        {
            using var cmd =
                db.CreateCommand();

            cmd.CommandText = """
            SELECT
                IsTemporary,
                TemporaryExpiresAt

            FROM AuthUsers

            WHERE
                LOWER(Username)
                = LOWER($username)

            LIMIT 1
            """;

            cmd.Parameters
                .AddWithValue(
                    "$username",
                    username
                );

            using var reader =
                cmd.ExecuteReader();

            if (
                reader.Read()
            )
            {
                var databaseTemporary =
                    !reader.IsDBNull(0)
                    &&
                    reader.GetInt32(0)
                    == 1;

                isTemporary =
                    claimTemporary
                    ||
                    databaseTemporary;

                if (
                    string.IsNullOrWhiteSpace(
                        expiresText
                    )
                )
                {
                    expiresText =
                        reader.IsDBNull(1)
                            ? ""
                            : reader.GetString(1);
                }
            }
        }

        if (!isTemporary)
        {
            await next(ctx);

            return;
        }

        if (
            DateTime.TryParse(
                expiresText,
                out var expiresAt
            )
            &&
            expiresAt.ToUniversalTime()
                <= DateTime.UtcNow
        )
        {
            TemporaryAccountStore
                .DeleteExpired();

            await ctx.SignOutAsync(
                CookieAuthenticationDefaults
                    .AuthenticationScheme
            );

            ctx.Response.StatusCode =
                StatusCodes
                    .Status401Unauthorized;

            await ctx.Response
                .WriteAsJsonAsync(
                    new
                    {
                        error =
                            "This temporary account has expired.",

                        code =
                            "temporary_account_expired"
                    }
                );

            return;
        }

        var allowed =
            path.StartsWith(
                "/api/temp-chats",
                StringComparison
                    .OrdinalIgnoreCase
            )

            ||

            path.Equals(
                "/api/me",
                StringComparison
                    .OrdinalIgnoreCase
            )

            ||

            path.Equals(
                "/api/account/temporary-status",
                StringComparison
                    .OrdinalIgnoreCase
            )

            ||

            path.Equals(
                "/api/auth/logout",
                StringComparison
                    .OrdinalIgnoreCase
            )

            ||

            path.Equals(
                "/api/auth/csrf",
                StringComparison
                    .OrdinalIgnoreCase
            );

        if (!allowed)
        {
            ctx.Response.StatusCode =
                StatusCodes
                    .Status403Forbidden;

            await ctx.Response
                .WriteAsJsonAsync(
                    new
                    {
                        error =
                            "Temporary accounts can only use Temp Chats.",

                        code =
                            "temporary_account_restricted",

                        redirect =
                            "/chatt/temp/"
                    }
                );

            return;
        }

        await next(ctx);
    }
}
