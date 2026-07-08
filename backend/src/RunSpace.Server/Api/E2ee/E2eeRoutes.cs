using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public static class E2eeRoutes
{
    public static IEndpointRouteBuilder MapE2eeRoutes(this IEndpointRouteBuilder app)
    {
        // ACCOUNT E2EE KEYS
        // Stores encrypted account private-key envelopes for cross-device cloud sync.
        // The server stores ciphertext only.
        app.MapGet("/api/e2ee/account-key", (HttpContext ctx) =>
        {
            var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

            using var db = DbHelpers.OpenDb();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
        SELECT PublicKey, EncryptedPrivateKey, Salt, Nonce, Kdf, Iterations, Version, CreatedAt, UpdatedAt
        FROM AccountE2eeKeys
        WHERE Username=$u
        LIMIT 1";
            cmd.Parameters.AddWithValue("$u", u);

            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return Results.Ok(new { exists = false, username = u });
            }

            return Results.Ok(new
            {
                exists = true,
                username = u,
                publicKey = r.GetString(0),
                encryptedPrivateKey = r.GetString(1),
                salt = r.GetString(2),
                nonce = r.GetString(3),
                kdf = r.GetString(4),
                iterations = r.GetInt32(5),
                version = r.GetInt32(6),
                createdAt = r.GetString(7),
                updatedAt = r.GetString(8)
            });
        });

        app.MapGet("/api/e2ee/account-public-key/{username}", (string username, HttpContext ctx) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();

            var t = (username ?? "").Trim().ToLowerInvariant();
            if (!AppHelpers.IsValidUsername(t) || !AppHelpers.UserExists(t))
                return Results.NotFound(new { exists = false, message = "User not found" });

            using var db = DbHelpers.OpenDb();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
        SELECT PublicKey, UpdatedAt
        FROM AccountE2eeKeys
        WHERE Username=$u
        LIMIT 1";
            cmd.Parameters.AddWithValue("$u", t);

            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return Results.Ok(new { exists = false, username = t, publicKey = "" });
            }

            return Results.Ok(new
            {
                exists = true,
                username = t,
                publicKey = r.GetString(0),
                updatedAt = r.GetString(1)
            });
        });

        app.MapPost("/api/e2ee/account-key", async (HttpContext ctx) =>
        {
            var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

            System.Text.Json.JsonElement body;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
                body = doc.RootElement.Clone();
            }
            catch
            {
                return Results.BadRequest(new { message = "Invalid JSON" });
            }

            if (body.ValueKind != System.Text.Json.JsonValueKind.Object)
                return Results.BadRequest(new { message = "Invalid JSON" });

            string ReadString(string name)
            {
                if (!body.TryGetProperty(name, out var p)) return "";
                if (p.ValueKind != System.Text.Json.JsonValueKind.String) return "";
                return (p.GetString() ?? "").Trim();
            }

            int ReadInt(string name, int fallback)
            {
                if (!body.TryGetProperty(name, out var p)) return fallback;
                if (p.ValueKind != System.Text.Json.JsonValueKind.Number) return fallback;
                return p.TryGetInt32(out var n) ? n : fallback;
            }

            var publicKey = ReadString("publicKey");
            var encryptedPrivateKey = ReadString("encryptedPrivateKey");
            var salt = ReadString("salt");
            var nonce = ReadString("nonce");
            var kdf = ReadString("kdf");
            var iterations = ReadInt("iterations", 310000);
            var version = ReadInt("version", 1);

            var isReset = false;
            if (body.TryGetProperty("reset", out var resetProp) &&
                resetProp.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                isReset = true;
            }

            if (string.IsNullOrWhiteSpace(kdf)) kdf = "PBKDF2-SHA256";

            // rs-e2ee-account-key-defensive-v1
            // Store ciphertext only, but still reject malformed/oversized envelopes before DB.
            var allowedKdfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PBKDF2-SHA256",
                "ARGON2ID",
                "SCRYPT"
            };

            if (publicKey.Length < 64 || publicKey.Length > 8192 || publicKey.Contains('\0'))
                return Results.BadRequest(new { message = "Invalid publicKey" });

            if (encryptedPrivateKey.Length < 64 || encryptedPrivateKey.Length > 65536 || encryptedPrivateKey.Contains('\0'))
                return Results.BadRequest(new { message = "Invalid encryptedPrivateKey" });

            if (!DefensiveInput.IsSafeBase64ish(salt, 2048) || salt.Length < 8)
                return Results.BadRequest(new { message = "Invalid salt" });

            if (!DefensiveInput.IsSafeBase64ish(nonce, 2048) || nonce.Length < 8)
                return Results.BadRequest(new { message = "Invalid nonce" });

            if (kdf.Length > 64 || kdf.Contains('\0') || !allowedKdfs.Contains(kdf))
                return Results.BadRequest(new { message = "Invalid kdf" });

            if (iterations < 100000 || iterations > 2000000)
                return Results.BadRequest(new { message = "Invalid iterations" });

            if (version < 1 || version > 20)
                return Results.BadRequest(new { message = "Invalid version" });

            var now = DateTime.UtcNow.ToString("o");

            using var db = DbHelpers.OpenDb();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
        INSERT INTO AccountE2eeKeys
          (Username, PublicKey, EncryptedPrivateKey, Salt, Nonce, Kdf, Iterations, Version, CreatedAt, UpdatedAt)
        VALUES
          ($u, $pub, $priv, $salt, $nonce, $kdf, $iter, $ver, $now, $now)
        ON CONFLICT(Username) DO UPDATE SET
          PublicKey=excluded.PublicKey,
          EncryptedPrivateKey=excluded.EncryptedPrivateKey,
          Salt=excluded.Salt,
          Nonce=excluded.Nonce,
          Kdf=excluded.Kdf,
          Iterations=excluded.Iterations,
          Version=excluded.Version,
          UpdatedAt=excluded.UpdatedAt";
            cmd.Parameters.AddWithValue("$u", u);
            cmd.Parameters.AddWithValue("$pub", publicKey);
            cmd.Parameters.AddWithValue("$priv", encryptedPrivateKey);
            cmd.Parameters.AddWithValue("$salt", salt);
            cmd.Parameters.AddWithValue("$nonce", nonce);
            cmd.Parameters.AddWithValue("$kdf", kdf);
            cmd.Parameters.AddWithValue("$iter", iterations);
            cmd.Parameters.AddWithValue("$ver", version);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();

            if (isReset)
            {
                DbHelpers.EnsureColumn(db, "AuthUsers", "SecurityChangedAt", "TEXT NOT NULL DEFAULT ''");

                using var sec = db.CreateCommand();
                sec.CommandText = "UPDATE AuthUsers SET SecurityChangedAt=$sc WHERE Username=$u";
                sec.Parameters.AddWithValue("$sc", now);
                sec.Parameters.AddWithValue("$u", u);
                sec.ExecuteNonQuery();

                using var del = db.CreateCommand();
                del.CommandText = "DELETE FROM UserDeviceKeys WHERE Username=$u";
                del.Parameters.AddWithValue("$u", u);
                del.ExecuteNonQuery();

                using var ps = db.CreateCommand();
                ps.CommandText = "DELETE FROM PersistentSessions WHERE LOWER(Username)=LOWER($u)";
                ps.Parameters.AddWithValue("$u", u);
                ps.ExecuteNonQuery();

                try { ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(u); } catch { }

                try { await ctx.SignOutAsync(); } catch { }

                ctx.Response.Cookies.Delete("runspace_auth_v3");
                ctx.Response.Cookies.Delete("runspace_auth_v2");
                ctx.Response.Cookies.Delete("runspace_auth");
                ctx.Response.Cookies.Delete("rs-dt");

                AppHelpers.LogActivity(u, "e2ee_passphrase_reset", $"From {ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}");

                return Results.Ok(new { success = true, username = u, updatedAt = now, reset = true, signedOut = true });
            }

            return Results.Ok(new { success = true, username = u, updatedAt = now });
        });

        return app;
    }
}
