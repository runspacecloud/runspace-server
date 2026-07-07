using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

public record UnlockReq(string AccountKey, string? Passphrase = null, string? TotpCode = null);

public static class UnlockEndpoint
{
    public static void Register(WebApplication app)
    {
        app.MapPost("/api/auth/unlock", async (HttpContext ctx) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<UnlockReq>();

            if (req == null || string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { message = "Account key required." });

            var accountKey = AccountKeyHashing.Normalize(req.AccountKey);

            if (!DefensiveInput.IsSafeRouteId(accountKey, 128))
                return Results.BadRequest(new { message = "Invalid account key." });

            var accountKeyHash = AccountKeyHashing.Hash(accountKey);

            using var db = DbHelpers.OpenDb();
            using var cmd = db.CreateCommand();

            cmd.CommandText = @"
                SELECT Username, Status, TwoFactorEnabled, TwoFactorSecret
                FROM AuthUsers
                WHERE AccountKeyHash=$akh
                LIMIT 1;
            ";
            cmd.Parameters.AddWithValue("$akh", accountKeyHash);

            using var r = cmd.ExecuteReader();

            if (!r.Read())
            {
                Console.WriteLine("[AUTH] unlock invalid_account_key");
                return Results.Json(new { status = "error", message = "Invalid account key." }, statusCode: 401);
            }

            var username = r.IsDBNull(0) ? "" : r.GetString(0).Trim().ToLowerInvariant();
            var status = r.IsDBNull(1) ? "" : r.GetString(1);
            var twoFa = !r.IsDBNull(2) && r.GetInt32(2) == 1;
            var twoFaSecret = r.IsDBNull(3) ? "" : r.GetString(3);

            r.Close();

            if (string.IsNullOrWhiteSpace(username))
                return Results.Unauthorized();

            if (status.Equals("banned", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { message = "Banned." }, statusCode: 403);

            if (twoFa && !string.IsNullOrWhiteSpace(twoFaSecret))
            {
                var code = DefensiveInput.CleanString(req.TotpCode, 16).Trim();

                if (string.IsNullOrWhiteSpace(code))
                    return Results.Ok(new { status = "otp_required", otpType = "totp" });

                var validTotp = DefensiveInput.IsOtpCode(code) && TotpHelper.VerifyCode(twoFaSecret, code);
                var validBackupCode = TotpHelper.VerifyAndUseBackupCode(username, code);

                if (!validTotp && !validBackupCode)
                    return Results.Json(new { message = "Invalid 2FA code or backup code." }, statusCode: 401);
            }

            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var userAgent = DefensiveInput.CleanString(ctx.Request.Headers["User-Agent"].ToString(), 512);
            var deviceToken = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Token"].FirstOrDefault() ?? "", 256);

            var sessionId = ctx.RequestServices
                .GetRequiredService<SessionManager>()
                .CreateSession(username, ip, userAgent);

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, username),
                new(ClaimTypes.NameIdentifier, username),
                new("SessionId", sessionId)
            };

            var props = new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7),
                IssuedUtc = DateTimeOffset.UtcNow
            };

            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                props
            );

            // rs-unlock-persistent-session-log-v1
            using (var sess = db.CreateCommand())
            {
                sess.CommandText = @"
                    INSERT INTO PersistentSessions (Username, SessionId, Ip, UserAgent, CreatedAt, LastActivity)
                    VALUES ($u, $sid, $ip, $ua, $now, $now)
                    ON CONFLICT(Username, SessionId) DO UPDATE SET LastActivity=$now";
                sess.Parameters.AddWithValue("$u", username);
                sess.Parameters.AddWithValue("$sid", sessionId);
                sess.Parameters.AddWithValue("$ip", ip);
                sess.Parameters.AddWithValue("$ua", userAgent);
                sess.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
                sess.ExecuteNonQuery();
            }

            if (DefensiveInput.IsSafeToken(deviceToken))
            {
                ctx.Response.Cookies.Append("rs-dt", deviceToken, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/",
                    MaxAge = TimeSpan.FromDays(30)
                });
            }

            AppHelpers.LogActivity(username, "login_with_key", $"From {ip}");

            Console.WriteLine($"[AUTH] unlock account_key_ok for {username}");

            return Results.Ok(new
            {
                status = "ok",
                redirect = "/chatt",
                loginMode = "account_key",
                username,
                isAdmin = AppHelpers.IsAdmin(username)
            });
        });
    }
}
