using Microsoft.AspNetCore.DataProtection;
using OtpNet;
using QRCoder;
using Stripe;
using Stripe.Checkout;
using Microsoft.AspNetCore.HttpOverrides;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Persist ASP.NET DataProtection keys so auth/session cookies survive restarts.
var dpKeysPath = builder.Configuration["DataProtection:KeysPath"] ?? "/var/lib/runspace/dpkeys";
Directory.CreateDirectory(dpKeysPath);

builder.Services.AddDataProtection()
    .SetApplicationName("RunSpace")
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath));

builder.WebHost.UseUrls("http://127.0.0.1:5000");

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 30 * 1024 * 1024;
    options.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
    options.Limits.MaxRequestLineSize = 8192;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
    options.Limits.MaxConcurrentConnections = 500;
    options.Limits.MaxConcurrentUpgradedConnections = 100;
});

Directory.CreateDirectory(AppConfig.DataDir);
Directory.CreateDirectory(AppConfig.UploadsDir);
Directory.CreateDirectory(AppConfig.AvatarUploadDir);
Directory.CreateDirectory(AppConfig.ChatUploadDir);

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 256 * 1024;
    options.EnableDetailedErrors = true;
    options.HandshakeTimeout = TimeSpan.FromSeconds(10);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

builder.Services.AddSingleton<IUserIdProvider, NameUserIdProvider>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<BruteForceProtection>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<SecurityAuditLogger>();
builder.Services.AddSingleton<GeoAnomalyDetector>();
builder.Services.AddSingleton<DeviceFingerprintManager>();
builder.Services.AddSingleton<RiskScoringEngine>();
builder.Services.AddSingleton<ThreatIntelligence>();
builder.Services.AddSingleton<HoneypotManager>();
builder.Services.AddSingleton<EmergencyKillSwitch>();
builder.Services.AddSingleton<PasswordBreachChecker>();
builder.Services.AddSingleton<NonceManager>();
builder.Services.AddSingleton<SessionAnomalyDetector>();
builder.Services.AddHostedService<BehaviorRecoveryService>();
builder.Services.AddHostedService<AuraScoreService>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddHttpClient();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "runspace_auth_v3";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; },
            OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; },

            // rs-cookie-security-changed-validator-v1
            OnValidatePrincipal = async ctx =>
            {
                var username = ctx.Principal?.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
                if (string.IsNullOrWhiteSpace(username))
                {
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    return;
                }

                try
                {
                    using var db = DbHelpers.OpenDb();
                    DbHelpers.EnsureColumn(db, "AuthUsers", "SecurityChangedAt", "TEXT NOT NULL DEFAULT ''");

                    using var cmd = db.CreateCommand();
                    cmd.CommandText = @"
                        SELECT SecurityChangedAt, Status, COALESCE(IsSuspended,0)
                        FROM AuthUsers
                        WHERE LOWER(Username)=LOWER($u)
                        LIMIT 1";
                    cmd.Parameters.AddWithValue("$u", username);

                    using var r = cmd.ExecuteReader();
                    if (!r.Read())
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }

                    var securityChangedAt = r.IsDBNull(0) ? "" : r.GetString(0);
                    var status = r.IsDBNull(1) ? "" : r.GetString(1);
                    var isSuspended = !r.IsDBNull(2) && r.GetInt32(2) == 1;

                    if (status.Equals("banned", StringComparison.OrdinalIgnoreCase) || isSuspended)
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(securityChangedAt)
                        && DateTime.TryParse(securityChangedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var changedAt))
                    {
                        var issuedAt = ctx.Properties?.IssuedUtc?.UtcDateTime;

                        if (issuedAt.HasValue && issuedAt.Value <= changedAt.ToUniversalTime())
                        {
                            ctx.RejectPrincipal();
                            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                            return;
                        }
                    }
                }
                catch
                {
                    // Fail closed for authenticated requests if auth validation itself breaks.
                    ctx.RejectPrincipal();
                    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                }
            }
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(AppConfig.AllowedOrigins).AllowCredentials()
              .WithMethods("GET", "POST", "PATCH", "DELETE")
              .WithHeaders("Content-Type", "X-CSRF-Token", "X-Request-Id", "X-Device-Fingerprint", "X-Device-Token", "X-Device-Name")
              .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
    });
});

var app = builder.Build();


// rs-safe-api-error-guard-v1
// Defensive API error handling: never leak raw exceptions or parser details to clients.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (System.Text.Json.JsonException ex) when ((ctx.Request.Path.Value ?? "").StartsWith("/api/"))
    {
        if (ctx.Response.HasStarted) throw;

        Console.WriteLine($"[API JSON ERROR] requestId={ctx.TraceIdentifier} path={ctx.Request.Path} type={ex.GetType().Name}");

        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"Invalid JSON request.\"}");
    }
    catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex) when ((ctx.Request.Path.Value ?? "").StartsWith("/api/"))
    {
        if (ctx.Response.HasStarted) throw;

        Console.WriteLine($"[API BAD REQUEST] requestId={ctx.TraceIdentifier} path={ctx.Request.Path} type={ex.GetType().Name}");

        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"Bad request.\"}");
    }
    catch (Exception ex) when ((ctx.Request.Path.Value ?? "").StartsWith("/api/"))
    {
        if (ctx.Response.HasStarted) throw;

        Console.WriteLine($"[API ERROR] requestId={ctx.TraceIdentifier} path={ctx.Request.Path} type={ex.GetType().Name}");

        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"Internal server error.\"}");
    }
});


// rs-defensive-request-guard-v1
// Keep normal JSON/API requests small. Larger bodies are only allowed for uploads and trusted webhooks.
app.Use(async (ctx, next) =>
{
    var path = (ctx.Request.Path.Value ?? "").ToLowerInvariant();
    var method = ctx.Request.Method.ToUpperInvariant();

    if (path.StartsWith("/api/") && method is "POST" or "PUT" or "PATCH")
    {
        var isLargeBodyEndpoint =
            path.Contains("/upload") ||
            path.Contains("/webhook") ||
            path.Contains("/stripe/") ||
            path.Contains("/billing/webhook") ||
            path.Contains("/icon");

        long maxBytes =
            isLargeBodyEndpoint ? 30L * 1024L * 1024L :
            path.StartsWith("/api/auth/") ? 16L * 1024L :
            128L * 1024L;

        var maxBodyFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (maxBodyFeature is { IsReadOnly: false })
            maxBodyFeature.MaxRequestBodySize = maxBytes;

        if (ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > maxBytes)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await ctx.Response.WriteAsync("{\"error\":\"Request body too large.\"}");
            return;
        }
    }

    await next();
});


app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto });
DbHelpers.EnsureDatabase();

// Account-level E2EE key envelope table.
// Server stores encrypted private keys only. Plain private keys never touch the server.
try
{
    using var _e2eeDb = DbHelpers.OpenDb();
    using var _e2eeCmd = _e2eeDb.CreateCommand();
    _e2eeCmd.CommandText = @"
CREATE TABLE IF NOT EXISTS AccountE2eeKeys (
  Username TEXT PRIMARY KEY,
  PublicKey TEXT NOT NULL DEFAULT '',
  EncryptedPrivateKey TEXT NOT NULL DEFAULT '',
  Salt TEXT NOT NULL DEFAULT '',
  Nonce TEXT NOT NULL DEFAULT '',
  Kdf TEXT NOT NULL DEFAULT 'PBKDF2-SHA256',
  Iterations INTEGER NOT NULL DEFAULT 310000,
  Version INTEGER NOT NULL DEFAULT 1,
  CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
  UpdatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS IX_AccountE2eeKeys_Username
ON AccountE2eeKeys(Username);
";
    _e2eeCmd.ExecuteNonQuery();
}
catch (Exception ex)
{
    Console.WriteLine("[startup] database init failed.");
}

ServerDb.EnsureMusicSchema();

// Security headers
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; connect-src 'self' wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self';";
    ctx.Response.Headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains; preload";
    ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    ctx.Response.Headers.Remove("Server"); ctx.Response.Headers.Remove("X-Powered-By");
    await next();
});

app.Use(async (ctx, next) =>
{
    ctx.Items["RequestId"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    ctx.Items["RequestStart"] = DateTime.UtcNow;
    ctx.Response.Headers["X-Request-Id"] = (string)ctx.Items["RequestId"];
    await next();
});

app.UseMiddleware<RequestGuardMiddleware>();

app.Use(async (ctx, next) =>
{
    var ks = ctx.RequestServices.GetRequiredService<EmergencyKillSwitch>();
    if (ks.IsActive && (ctx.Request.Path.Value ?? "").StartsWith("/api/auth/") && !(ctx.Request.Path.Value ?? "").Contains("admin"))
    { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsJsonAsync(new { message = "Underhåll." }); return; }
    await next();
});

app.Use(async (ctx, next) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!limiter.IsAllowed(ip, "global", 300, 60))
    { ctx.Response.StatusCode = 429; ctx.Response.Headers["Retry-After"] = "60"; await ctx.Response.WriteAsJsonAsync(new { message = "För många förfrågningar." }); return; }
    await next();
});

app.Use(async (ctx, next) =>
{
    var threat = ctx.RequestServices.GetRequiredService<ThreatIntelligence>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (threat.IsBlocked(ip)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { message = "Åtkomst nekad." }); return; }
    await next();
});

app.Use(async (ctx, next) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    // rs-user-agent-middleware-defensive-v1
    var ua = DefensiveInput.CleanString(ctx.Request.Headers.UserAgent.ToString(), 512);
    var bad = new[] { "headlesschrome", "phantomjs", "selenium", "puppeteer", "playwright", "httpclient", "python-requests", "curl/", "wget/", "scrapy", "bot", "crawler" };
    if (ctx.Request.Path.Value?.StartsWith("/api/auth/") == true && (string.IsNullOrWhiteSpace(ua) || bad.Any(s => ua.ToLowerInvariant().Contains(s))))
    { await Task.Delay(RandomNumberGenerator.GetInt32(2000, 5000)); }
    ctx.Items["ClientIp"] = ip; ctx.Items["UserAgent"] = ua;
    await next();
});

app.Use(async (ctx, next) =>
{
    var combined = (ctx.Request.Path.Value ?? "") + (ctx.Request.QueryString.Value ?? "");
    string[] patterns = { "../", "..\\", "%2e%2e", "%00", "<script", "javascript:", "UNION SELECT", "DROP TABLE", "cmd.exe", "/bin/sh", "/etc/passwd", "eval(", "${", "{{", ".env", "wp-admin", ".git/" };
    foreach (var p in patterns)
    {
        if (combined.Contains(p, StringComparison.OrdinalIgnoreCase))
        {
            var threat = ctx.RequestServices.GetRequiredService<ThreatIntelligence>();
            threat.RecordStrike(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", "attack_pattern");
            ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { message = "Ogiltig förfrågan." }); return;
        }
    }
    await next();
});

app.Use(async (ctx, next) =>
{
    var honeypot = ctx.RequestServices.GetRequiredService<HoneypotManager>();
    var path = ctx.Request.Path.Value ?? "";
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (honeypot.IsHoneypotPath(path))
    {
        ctx.RequestServices.GetRequiredService<ThreatIntelligence>().RecordStrike(ip, "honeypot");
        await Task.Delay(RandomNumberGenerator.GetInt32(1000, 3000));
        ctx.Response.StatusCode = 200; await ctx.Response.WriteAsJsonAsync(honeypot.GetFakeResponse(path)); return;
    }
    await next();
});

app.Use(async (ctx, next) =>
{
    if (ctx.Request.Method != "GET" && ctx.Request.Method != "HEAD" && ctx.Request.Method != "OPTIONS")
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin) && !AppConfig.AllowedOrigins.Any(o => origin.Equals(o, StringComparison.OrdinalIgnoreCase)) && !origin.Contains("localhost") && !origin.Contains("127.0.0.1"))
        { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { message = "Ogiltig origin." }); return; }
    }
    await next();
});

app.Use(async (ctx, next) =>
{
    var threat = ctx.RequestServices.GetRequiredService<ThreatIntelligence>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (threat.IsRequestFlooding(ip)) { ctx.Response.StatusCode = 429; await ctx.Response.WriteAsJsonAsync(new { message = "Too many requests." }); return; }
    await next();
});

app.UseCors();
app.UseDefaultFiles();

// Add no-cache headers for HTML files to prevent stale data
app.Use(async (context, next) =>
{
    await next();

    var path = context.Request.Path.Value?.ToLower() ?? "";
    if (path.EndsWith(".html") || path == "/" || path == "/index" || path == "/login")
    {
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        context.Response.Headers["Pragma"] = "no-cache";
        context.Response.Headers["Expires"] = "0";
    }
});

app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(AppConfig.UploadsDir),
    RequestPath = "/uploads",
    OnPrepareResponse = ctx =>
    {
        var ext = Path.GetExtension(ctx.File.Name).ToLowerInvariant();
        if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }.Contains(ext))
        { ctx.Context.Response.StatusCode = 403; ctx.Context.Response.ContentLength = 0; ctx.Context.Response.Body = Stream.Null; return; }
        ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=3600";
    }
});
app.UseAuthentication();
app.UseAuthorization();

// ═══════════════════════════════════════════════
// AUTH ENDPOINTS
// ═══════════════════════════════════════════════

app.MapGet("/api/auth/csrf", (HttpContext ctx) =>
{
    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    ctx.Response.Cookies.Append("__Host-csrf", token, new CookieOptions { HttpOnly = false, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromHours(4) });
    return Results.Ok(new { csrfToken = token });
});



app.MapPost("/api/presence/music", async (HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    var username = ctx.User?.Identity?.IsAuthenticated == true
        ? (ctx.User.Identity?.Name ?? "anonymous").Trim().ToLowerInvariant()
        : "anonymous";

    try
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var isPlaying = root.TryGetProperty("isPlaying", out var p) && p.GetBoolean();

        // Privacy: do not log, store, or broadcast paused/inactive music presence
        if (!isPlaying)
        {
            return Results.Ok(new { ok = true, ignored = true });
        }

        var title = root.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
        var artist = root.TryGetProperty("artist", out var a) ? (a.GetString() ?? "") : "";
        var source = root.TryGetProperty("source", out var so) ? (so.GetString() ?? "") : "";
        var url = root.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";

        title = title.Length > 120 ? title[..120] : title;
        artist = artist.Length > 80 ? artist[..80] : artist;
        source = source.Length > 30 ? source[..30] : source;
        url = url.Length > 300 ? url[..300] : url;

        Console.WriteLine($"[music-presence] {DateTime.UtcNow:o} user={username} isPlaying=true source={source}");

        await hub.Clients.All.SendAsync("MusicPresenceUpdate", new
        {
            username,
            isPlaying,
            title,
            artist,
            source,
            url,
            updatedAt = DateTime.UtcNow.ToString("o")
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine("[music-presence] broadcast failed.");
        return Results.BadRequest(new { error = "invalid_json" });
    }

    return Results.Ok(new { ok = true });
});

app.MapGet("/api/me", async (HttpContext ctx) =>
{
    Console.WriteLine($"[/api/me] Called. IsAuthenticated: {ctx.User.Identity?.IsAuthenticated}, Name: {ctx.User.Identity?.Name}");
    // Defensive logging rule: never log cookie values, auth tokens, secrets, private keys or passwords.

    if (ctx.User.Identity?.IsAuthenticated != true)
    {
        Console.WriteLine("[/api/me] NOT AUTHENTICATED - returning 401");
        return Results.Unauthorized();
    }
    var username = (ctx.User.Identity?.Name ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT Bio, AvatarUrl, CreatedAt, Status, Badges, TwoFactorEnabled, Nationality, Languages, Links, BannerUrl,
        EmailVerified, TrustLevel, TrustScore, IsSuspended, Email, PublicId,
        trust_identity, trust_behavior, trust_device, trust_disabled_features,
        IsPremium, PremiumPlan, PremiumSince, PremiumUntil,
        aura_score, aura_active_days, aura_last_active_date
        FROM AuthUsers WHERE Username = $u LIMIT 1";
    cmd.Parameters.AddWithValue("$u", username);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { await ctx.SignOutAsync(); return Results.Unauthorized(); }
    var createdAt = r.IsDBNull(2) ? "" : r.GetString(2);
    var auraScore = r.IsDBNull(24) ? 0.0 : Convert.ToDouble(r.GetValue(24));
    var auraActiveDays = r.IsDBNull(25) ? 0 : Convert.ToInt32(r.GetValue(25));
    var auraLastActiveDate = r.IsDBNull(26) ? "" : r.GetString(26);
    // --- Trust computation ---
    var emailVerified = !r.IsDBNull(10) && r.GetInt32(10) == 1;
    var twoFa = !r.IsDBNull(5) && r.GetInt32(5) == 1;
    var isSuspended = !r.IsDBNull(13) && r.GetInt32(13) == 1;
    var storedTrustLevel = r.IsDBNull(11) ? "medium" : r.GetString(11);
    var storedTrustScore = r.IsDBNull(12) ? 50 : r.GetInt32(12);
    var storedUser_trust_identity = r.IsDBNull(16) ? 50.0 : r.GetDouble(16);
    var storedUser_trust_behavior = r.IsDBNull(17) ? 60.0 : r.GetDouble(17);
    var storedUser_trust_device = r.IsDBNull(18) ? 70.0 : r.GetDouble(18);
    var storedUser_trust_disabled = r.IsDBNull(19) ? null : r.GetString(19);
    // Device check
    // Device token läses enbart från HttpOnly cookie – header accepteras inte
    // rs-device-token-legacy-defensive-v1
    var deviceToken = DefensiveInput.CleanString(ctx.Request.Cookies["rs-dt"], 256);
    if (!DefensiveInput.IsSafeToken(deviceToken))
        deviceToken = "";

    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    bool knownDevice = false;
    var _deviceRiskScore = 0;
    if (!string.IsNullOrWhiteSpace(deviceToken))
    {
        using var devCmd = db.CreateCommand();
        devCmd.CommandText = "SELECT COUNT(1) FROM DeviceTokens WHERE UserId = (SELECT Id FROM AuthUsers WHERE Username=$u) AND DeviceToken=$tok";
        devCmd.Parameters.AddWithValue("$u", username);
        devCmd.Parameters.AddWithValue("$tok", deviceToken);
        knownDevice = Convert.ToInt64(devCmd.ExecuteScalar()) > 0;
        // Record device visit
        // --- DEVICE INTELLIGENCE ---
        var _ipPrefix = string.IsNullOrWhiteSpace(ip) ? "" : string.Join(".", ip.Split('.').Take(3)) + ".*";
        // 1. Upsert device record
        using var upsertDev = db.CreateCommand();
        upsertDev.CommandText = @"INSERT OR IGNORE INTO DeviceTokens (UserId, DeviceToken, DeviceName, UserAgent,  IpPrefix, FirstSeenAt, LastSeenAt, IsTrusted, SessionCount)
            SELECT Id, $tok, $name, $ua,  $prefix, datetime('now'), datetime('now'), 0, 1 FROM AuthUsers WHERE Username=$u";
        var safeDeviceName = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Name"].FirstOrDefault() ?? "Web Browser", 80);
        if (string.IsNullOrWhiteSpace(safeDeviceName))
            safeDeviceName = "Web Browser";

        var safeUserAgent = DefensiveInput.CleanString(ctx.Request.Headers.UserAgent.ToString(), 512);

        upsertDev.Parameters.AddWithValue("$tok", deviceToken);
        upsertDev.Parameters.AddWithValue("$name", safeDeviceName);
        upsertDev.Parameters.AddWithValue("$ua", safeUserAgent);
        upsertDev.Parameters.AddWithValue("$ip", ip);
        upsertDev.Parameters.AddWithValue("$prefix", _ipPrefix);
        upsertDev.Parameters.AddWithValue("$u", username);
        upsertDev.ExecuteNonQuery();
        using var updateDev = db.CreateCommand();
        updateDev.CommandText = @"UPDATE DeviceTokens SET LastSeenAt=datetime('now'), IpAddress=$ip, IpPrefix=$prefix,
            SessionCount=SessionCount+1 WHERE UserId=(SELECT Id FROM AuthUsers WHERE Username=$u) AND DeviceToken=$tok";
        updateDev.Parameters.AddWithValue("$ip", ip);
        updateDev.Parameters.AddWithValue("$prefix", _ipPrefix);
        updateDev.Parameters.AddWithValue("$u", username);
        updateDev.Parameters.AddWithValue("$tok", deviceToken);
        updateDev.ExecuteNonQuery();
        // 2. Track IP history per device
        if (!string.IsNullOrWhiteSpace(ip))
        {
            using var ipHist = db.CreateCommand();
            ipHist.CommandText = @"INSERT OR IGNORE INTO DeviceIpHistory (DeviceToken, IpAddress, SeenAt)
                VALUES ($tok, $ip, datetime('now'))";
            ipHist.Parameters.AddWithValue("$tok", deviceToken);
            ipHist.Parameters.AddWithValue("$ip", ip);
            ipHist.ExecuteNonQuery();
            // Update seen IP count
            using var ipCount = db.CreateCommand();
            ipCount.CommandText = @"UPDATE DeviceTokens SET SeenIpCount=(
                SELECT COUNT(DISTINCT IpAddress) FROM DeviceIpHistory WHERE DeviceToken=$tok)
                WHERE DeviceToken=$tok";
            ipCount.Parameters.AddWithValue("$tok", deviceToken);
            ipCount.ExecuteNonQuery();
        }
        // 3. Track device-account links (multi-account detection)
        using var accLink = db.CreateCommand();
        accLink.CommandText = @"INSERT OR IGNORE INTO DeviceAccountLinks (DeviceToken, UserId, FirstSeenAt)
            SELECT $tok, Id, datetime('now') FROM AuthUsers WHERE Username=$u";
        accLink.Parameters.AddWithValue("$tok", deviceToken);
        accLink.Parameters.AddWithValue("$u", username);
        accLink.ExecuteNonQuery();
        // 4. Count accounts sharing this device
        using var acctCount = db.CreateCommand();
        acctCount.CommandText = "SELECT COUNT(DISTINCT UserId) FROM DeviceAccountLinks WHERE DeviceToken=$tok";
        acctCount.Parameters.AddWithValue("$tok", deviceToken);
        var _sharedAccounts = Convert.ToInt32(acctCount.ExecuteScalar());
        // 5. Count new devices in last 24h for this user
        using var newDevCmd = db.CreateCommand();
        newDevCmd.CommandText = @"SELECT COUNT(*) FROM DeviceTokens
            WHERE UserId=(SELECT Id FROM AuthUsers WHERE Username=$u)
            AND FirstSeenAt >= datetime('now', '-24 hours')";
        newDevCmd.Parameters.AddWithValue("$u", username);
        var _newDevices24h = Convert.ToInt32(newDevCmd.ExecuteScalar());
        // 6. Compute device risk flags
        var _deviceRiskFlags = new List<string>();
        if (_sharedAccounts > 1) { _deviceRiskFlags.Add("multi_account"); _deviceRiskScore += _sharedAccounts > 3 ? 40 : 20; }
        if (_newDevices24h > 3) { _deviceRiskFlags.Add("device_churn"); _deviceRiskScore += 25; }
        using var seenIpCmd = db.CreateCommand();
        seenIpCmd.CommandText = "SELECT SeenIpCount FROM DeviceTokens WHERE DeviceToken=$tok";
        seenIpCmd.Parameters.AddWithValue("$tok", deviceToken);
        var _seenIps = Convert.ToInt32(seenIpCmd.ExecuteScalar());
        if (_seenIps > 10) { _deviceRiskFlags.Add("ip_hopping"); _deviceRiskScore += 15; }
        // Check device maturity (mature after 7 days with no incidents)
        using var matCheck = db.CreateCommand();
        matCheck.CommandText = "SELECT FirstSeenAt, MaturedAt FROM DeviceTokens WHERE DeviceToken=$tok";
        matCheck.Parameters.AddWithValue("$tok", deviceToken);
        using var matR = matCheck.ExecuteReader();
        bool _deviceMature = false;
        if (matR.Read())
        {
            var _maturedAt = matR.IsDBNull(1) ? null : matR.GetString(1);
            var _firstSeen = matR.IsDBNull(0) ? DateTime.UtcNow : DateTime.Parse(matR.GetString(0));
            _deviceMature = _maturedAt != null || (DateTime.UtcNow - _firstSeen).TotalDays >= 7;
            if (!_deviceMature && _deviceRiskScore == 0 && (DateTime.UtcNow - _firstSeen).TotalDays >= 7)
            {
                matR.Close();
                using var matUpd = db.CreateCommand();
                matUpd.CommandText = "UPDATE DeviceTokens SET MaturedAt=datetime('now') WHERE DeviceToken=$tok";
                matUpd.Parameters.AddWithValue("$tok", deviceToken);
                matUpd.ExecuteNonQuery();
                _deviceMature = true;
            }
        }
        matR.Close();
        // 7. Persist device risk
        var _flagsJson = System.Text.Json.JsonSerializer.Serialize(_deviceRiskFlags);
        using var riskUpd = db.CreateCommand();
        riskUpd.CommandText = "UPDATE DeviceTokens SET RiskFlags=$flags, RiskScore=$score WHERE DeviceToken=$tok";
        riskUpd.Parameters.AddWithValue("$flags", _flagsJson);
        riskUpd.Parameters.AddWithValue("$score", _deviceRiskScore);
        riskUpd.Parameters.AddWithValue("$tok", deviceToken);
        riskUpd.ExecuteNonQuery();
        // 8. Device risk -> TrustEventService (skip for admins)
        if (_deviceRiskScore > 0 && !AppHelpers.IsAdmin(username))
            TrustEventService.OnDeviceRisk(db, username, _deviceRiskScore);
        // --- END DEVICE INTELLIGENCE ---
        // Uppdatera trust_device baserat på RiskScore (0 risk = 100, 100 risk = 0)
        var newDeviceScore = Math.Max(0.0, 100.0 - (_deviceRiskScore * 1.5));
        using var devDimUpd = db.CreateCommand();
        devDimUpd.CommandText = "UPDATE AuthUsers SET trust_device=$d WHERE Username=$u";
        devDimUpd.Parameters.AddWithValue("$d", newDeviceScore);
        devDimUpd.Parameters.AddWithValue("$u", username);
        devDimUpd.ExecuteNonQuery();
    }
    // --- TRUST ENGINE (read-only, no side effects) ---
    DateTime.TryParse(createdAt, out var created);
    var ageDays = (int)(DateTime.UtcNow - created).TotalDays;
    // Läs uppdaterad device-score från vad vi just satte (eller stored om ingen device)
    var effectiveDeviceScore = string.IsNullOrWhiteSpace(deviceToken)
        ? storedUser_trust_device
        : Math.Max(0.0, 100.0 - (_deviceRiskScore * 1.5));
    var dims = new TrustDimensions
    {
        Identity = storedUser_trust_identity,
        Behavior = storedUser_trust_behavior,
        Device = effectiveDeviceScore
    };
    // Recalculate identity dimension (cheap, stateless)
    dims.Identity = TrustEngine.CalculateIdentity(emailVerified, twoFa, createdAt);
    var composite = TrustEngine.CompositeScore(dims);
    var prevLevel = TrustEngine.ParseLevel(storedTrustLevel);
    var trustLevelEnum = isSuspended ? TrustLevel.Blocked : TrustEngine.GetLevel(composite, prevLevel);
    var trustLevel = TrustEngine.LevelToString(trustLevelEnum);
    var trustScore = (int)Math.Round(composite);
    var trustReasons = new List<string>();
    if (!emailVerified) trustReasons.Add("unverified_email");
    if (!knownDevice && !string.IsNullOrWhiteSpace(deviceToken)) trustReasons.Add("new_device");
    if (ageDays < 7) trustReasons.Add("new_account");
    if (isSuspended) trustReasons.Add("account_suspended");
    // Persist identity dimension update only (behavior/device updated via events)
    using var trustUpd = db.CreateCommand();
    trustUpd.CommandText = "UPDATE AuthUsers SET TrustLevel=$lvl, TrustScore=$score, trust_identity=$ti WHERE Username=$u";
    trustUpd.Parameters.AddWithValue("$lvl", trustLevel);
    trustUpd.Parameters.AddWithValue("$score", trustScore);
    trustUpd.Parameters.AddWithValue("$ti", dims.Identity);
    trustUpd.Parameters.AddWithValue("$u", username);
    trustUpd.ExecuteNonQuery();
    // Log level transitions only (not every poll)
    if (trustLevel != storedTrustLevel)
    {
        using var histCmd = db.CreateCommand();
        histCmd.CommandText = @"INSERT INTO TrustHistory2 (UserId, Timestamp, ReasonCode, Dimension, OldScore, NewScore, Delta)
            SELECT Id, datetime('now'), 'LEVEL_CHANGE', 'composite', $old, $new, $delta FROM AuthUsers WHERE Username=$u";
        histCmd.Parameters.AddWithValue("$old", storedTrustScore);
        histCmd.Parameters.AddWithValue("$new", trustScore);
        histCmd.Parameters.AddWithValue("$delta", trustScore - storedTrustScore);
        histCmd.Parameters.AddWithValue("$u", username);
        histCmd.ExecuteNonQuery();
        using var evtCmd = db.CreateCommand();
        evtCmd.CommandText = @"INSERT INTO SecurityEventLog (UserId, Timestamp, EventType, FromState, ToState, Details)
            SELECT Id, datetime('now'), 'LEVEL_DROP', $from, $to, $detail FROM AuthUsers WHERE Username=$u";
        evtCmd.Parameters.AddWithValue("$from", storedTrustLevel);
        evtCmd.Parameters.AddWithValue("$to", trustLevel);
        evtCmd.Parameters.AddWithValue("$detail", $"{{\"composite\":{composite:F1},\"reasons\":\"{string.Join(",", trustReasons)}\"}}");
        evtCmd.Parameters.AddWithValue("$u", username);
        evtCmd.ExecuteNonQuery();
    }
    return Results.Ok(new
    {
        username,
        isAdmin = AppHelpers.IsAdmin(username),
        bio = InputSanitizer.SanitizeOutput(r.IsDBNull(0) ? "" : r.GetString(0)),
        avatarUrl = InputSanitizer.SanitizeUrl(r.IsDBNull(1) ? "" : r.GetString(1)),
        createdAt,
        age = AppHelpers.GetAgeTextFromCreatedAt(createdAt),
        status = r.IsDBNull(3) ? "verified" : r.GetString(3),
        badges = AppHelpers.ParseBadges(r.IsDBNull(4) ? "[]" : r.GetString(4)),
        links = JsonSerializer.Deserialize<List<object>>(r.IsDBNull(8) ? "[]" : r.GetString(8)) ?? new List<object>(),
        bannerUrl = r.IsDBNull(9) ? "" : r.GetString(9),
        twoFactorEnabled = twoFa,
        activeSessions = ctx.RequestServices.GetRequiredService<SessionManager>().GetActiveSessionCount(username),
        nationality = r.IsDBNull(6) ? "" : r.GetString(6),
        languages = r.IsDBNull(7) ? "" : r.GetString(7),
        deviceKeys = ChatKeyHelpers.GetUserDeviceKeys(username),
        // --- Trust data for frontend ---
        emailVerified,
        email = r.IsDBNull(14) ? "" : r.GetString(14),
        knownDevice,
        accountAgeDays = ageDays,
        publicId = r.IsDBNull(15) ? "" : r.GetString(15),
        auraScore = Math.Round(auraScore, 1),
        auraActiveDays,
        auraLastActiveDate,
        aura = new
        {
            score = Math.Round(auraScore, 1),
            activeDays = auraActiveDays,
            lastActiveDate = auraLastActiveDate
        },
        trust = new
        {
            composite = Math.Round(composite, 1),
            level = trustLevel,
            score = trustScore,
            reasons = trustReasons,
            dimensions = new
            {
                identity = Math.Round(dims.Identity, 1),
                behavior = Math.Round(dims.Behavior, 1),
                device = Math.Round(dims.Device, 1)
            }
        },
        // Premium
        isPremium = !r.IsDBNull(20) && r.GetInt32(20) == 1,
        premiumPlan = r.IsDBNull(21) ? "" : r.GetString(21),
        premiumSince = r.IsDBNull(22) ? "" : r.GetString(22),
        premiumUntil = r.IsDBNull(23) ? "" : r.GetString(23),
        features = TrustEngine.EvaluateFeatures(dims, composite, isSuspended, storedUser_trust_disabled),
        // Legacy flat fields – behålls för bakåtkompatibilitet med klienter
        trustLevel,
        trustScore,
        canUploadFiles = dims.Behavior >= 55 && dims.Device >= 40 && composite >= 45,
        canSendLinks = dims.Behavior >= 45 && composite >= 40,
        firewallConfig = new
        {
            maxRepeatChars = 8,
            maxUrlsLowTrust = 0,
            maxUrlsMediumTrust = 2,
            rateLimitLowMs = 5000,
            rateLimitMediumMs = 1500
        }
    });
});

app.MapPost("/api/auth/register", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var threat = ctx.RequestServices.GetRequiredService<ThreatIntelligence>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!limiter.IsAllowed(ip, "register", 3, 3600))
    { threat.RecordStrike(ip, "register_abuse"); return Results.Json(new { message = "För många registreringsförsök. Vänta en timme." }, statusCode: 429); }
    var req = await ctx.Request.ReadFromJsonAsync<LoginReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { message = "Username and password required" });
    // Email honeypot removed — email now used for OTP
    // Captcha replaced by trust engine signal scoring

    // rs-auth-input-defensive-v1
    var username = DefensiveInput.CleanString(req.Username, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(username) || !AppHelpers.IsValidUsername(username))
        return Results.BadRequest(new { message = "Ogiltigt användarnamn" });

    if (!DefensiveInput.IsSafePasswordInput(req.Password))
        return Results.BadRequest(new { message = "Ogiltigt lösenord." });

    var email = DefensiveInput.CleanString(req.Email, 254).ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(email) && !DefensiveInput.IsEmail(email))
        return Results.BadRequest(new { message = "Ogiltig e-postadress." });

    if (ReservedNames.IsReserved(username)) return Results.BadRequest(new { message = "Reserverat." });
    if (ContentFilter.IsOffensive(username)) return Results.BadRequest(new { message = "Otillåtet användarnamn." });
    var pwCheck = PasswordPolicy.Validate(req.Password);
    if (!pwCheck.Valid) return Results.Json(new { status = "error", message = pwCheck.Message }, statusCode: 400);
    if (ctx.RequestServices.GetRequiredService<PasswordBreachChecker>().IsBreached(req.Password))
        return Results.BadRequest(new { message = "Lösenordet finns i dataläckor." });
    using var db = DbHelpers.OpenDb();
    using var exists = db.CreateCommand();
    exists.CommandText = "SELECT COUNT(*) FROM AuthUsers WHERE Username = $u"; exists.Parameters.AddWithValue("$u", username);
    if (Convert.ToInt32(exists.ExecuteScalar()) > 0) { await Task.Delay(RandomNumberGenerator.GetInt32(100, 300)); return Results.Json(new { status = "error", message = "Username already taken" }, statusCode: 409); }
    var hash = PasswordHashing.HashPassword(req.Password);
    var now = DateTime.UtcNow.ToString("o");
    using var ins = db.CreateCommand();
    ins.CommandText = @"INSERT INTO AuthUsers (Username,PasswordHash,Bio,AvatarUrl,CreatedAt,Status,Badges,PublicKey,TwoFactorEnabled,TwoFactorSecret,PasswordChangedAt,LoginCount,LastLoginAt,LastLoginIp,AccountLockedUntil,Email,PublicId)
        VALUES ($u,$p,'','', $t,'pending','[]','',0,'',$t,0,'','','',$e,lower(hex(randomblob(16))))";

    ins.Parameters.AddWithValue("$u", username); ins.Parameters.AddWithValue("$p", hash); ins.Parameters.AddWithValue("$t", now); ins.Parameters.AddWithValue("$e", email);
    ins.ExecuteNonQuery();
    PasswordHistory.Save(username, hash);
    AppHelpers.LogActivity(username, "register", $"From {ip}");
    if (!string.IsNullOrWhiteSpace(email))
    {
        var otpCode = OtpGenerator.GenerateCode();
        var cacheKey = $"reg_otp:{username}";
        OtpCache.Set(cacheKey, otpCode, TimeSpan.FromMinutes(15));
        try { await SmtpMailService.SendOtpAsync(email, username, otpCode, "verify"); } catch { Console.WriteLine("[SMTP] send failed."); }
        return Results.Ok(new { status = "otp_required", pendingToken = username, message = "Check your email for a verification code." });
    }
    return Results.Ok(new { status = "ok", redirect = "/chatt" });
});

UnlockEndpoint.Register(app);


// rs-legacy-password-auth-block-v1
var legacyPasswordAuthEndpoints = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
{
    "/api/auth/login",
    "/api/auth/forgot-password",
    "/api/auth/change-password",
    "/api/auth/login-with-key"
};

app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";

    if (ctx.Request.Method.Equals("POST", System.StringComparison.OrdinalIgnoreCase)
        && legacyPasswordAuthEndpoints.Contains(path))
    {
        ctx.Response.StatusCode = 410;
        await ctx.Response.WriteAsJsonAsync(new
        {
            status = "disabled",
            message = "Legacy password authentication is disabled. Use Account Key unlock or Account Recovery instead."
        });
        return;
    }

    await next();
});

app.MapPost("/api/auth/login", async (HttpContext ctx) =>
{
    var brute = ctx.RequestServices.GetRequiredService<BruteForceProtection>();
    var sessionMgr = ctx.RequestServices.GetRequiredService<SessionManager>();
    var risk = ctx.RequestServices.GetRequiredService<RiskScoringEngine>();
    var geo = ctx.RequestServices.GetRequiredService<GeoAnomalyDetector>();
    var devMgr = ctx.RequestServices.GetRequiredService<DeviceFingerprintManager>();
    var threat = ctx.RequestServices.GetRequiredService<ThreatIntelligence>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var ua = DefensiveInput.CleanString(ctx.Request.Headers.UserAgent.ToString(), 512);
    var deviceFp = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Fingerprint"].ToString(), 256);
    var req = await ctx.Request.ReadFromJsonAsync<LoginReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { message = "Krävs" });

    // rs-login-input-defensive-v1
    var username = DefensiveInput.CleanString(req.Username, 32).ToLowerInvariant();
    var password = req.Password;

    if (!DefensiveInput.IsUsername(username) || !AppHelpers.IsValidUsername(username))
        return Results.BadRequest(new { message = "Ogiltigt användarnamn." });

    if (!DefensiveInput.IsSafePasswordInput(password))
        return Results.BadRequest(new { message = "Ogiltigt lösenord." });

    if (brute.IsIpLocked(ip)) return Results.Json(new { message = "IP låst." }, statusCode: 429);
    threat.RecordLoginAttempt(ip, username);
    if (DecoyAccountManager.IsDecoy(username)) { threat.RecordStrike(ip, "decoy"); BCrypt.Net.BCrypt.HashPassword("timing"); return Results.Unauthorized(); }
    if (brute.IsAccountLocked(username)) { await Task.Delay(brute.GetProgressiveDelay(username)); return Results.Unauthorized(); }
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT PasswordHash,Status,AccountLockedUntil,TwoFactorEnabled,TwoFactorSecret,LoginCount,LastLoginIp FROM AuthUsers WHERE Username=$u LIMIT 1";
    cmd.Parameters.AddWithValue("$u", username);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) { BCrypt.Net.BCrypt.HashPassword("timing"); brute.RecordFailedAttempt(ip, username); return Results.Unauthorized(); }
    var hash = r.IsDBNull(0) ? "" : r.GetString(0);
    var status = r.IsDBNull(1) ? "" : r.GetString(1);
    var lockedStr = r.IsDBNull(2) ? "" : r.GetString(2);
    var twoFa = !r.IsDBNull(3) && r.GetInt32(3) == 1;
    var twoFaSecret = r.IsDBNull(4) ? "" : r.GetString(4);
    var lastIp = r.IsDBNull(6) ? "" : r.GetString(6);
    r.Close();
    if (!string.IsNullOrWhiteSpace(lockedStr) && DateTime.TryParse(lockedStr, null, DateTimeStyles.RoundtripKind, out var locked) && locked > DateTime.UtcNow)
        return Results.Json(new { message = "Kontot låst." }, statusCode: 423);
    if (status?.Trim().Equals("banned", StringComparison.OrdinalIgnoreCase) == true)
        return Results.Json(new { message = "Spärrat." }, statusCode: 403);
    var passwordOk = false;
    var shouldUpgradePasswordHash = false;

    if (!string.IsNullOrWhiteSpace(hash))
    {
        passwordOk = PasswordHashing.VerifyPassword(password, hash);

        // Temporary migration bridge:
        // old accounts may have been hashed with the old default pepper or without pepper.
        if (!passwordOk && PasswordHashing.VerifyDefaultPepperPassword(password, hash))
        {
            passwordOk = true;
            shouldUpgradePasswordHash = true;
            Console.WriteLine($"[AUTH] password_pipeline_v2 matched default-pepper legacy hash for {username}");
        }

        if (!passwordOk && PasswordHashing.VerifyLegacyPassword(password, hash))
        {
            passwordOk = true;
            shouldUpgradePasswordHash = true;
            Console.WriteLine($"[AUTH] password_pipeline_v2 matched no-pepper legacy hash for {username}");
        }
    }

    if (!passwordOk)
    {
        Console.WriteLine("[AUTH] password_pipeline_v2 wrong_password");
        brute.RecordFailedAttempt(ip, username); threat.RecordStrike(ip, "wrong_password");
        if (brute.GetFailCount(username) >= 10) { using var lck = db.CreateCommand(); lck.CommandText = "UPDATE AuthUsers SET AccountLockedUntil=$l WHERE Username=$u"; lck.Parameters.AddWithValue("$l", DateTime.UtcNow.AddHours(1).ToString("o")); lck.Parameters.AddWithValue("$u", username); lck.ExecuteNonQuery(); }
        return Results.Json(new { status = "error", message = "Incorrect credentials" }, statusCode: 401);
    }

    if (shouldUpgradePasswordHash)
    {
        using var uph = db.CreateCommand();
        uph.CommandText = "UPDATE AuthUsers SET PasswordHash=$h, PasswordChangedAt=$ts WHERE Username=$u";
        uph.Parameters.AddWithValue("$h", PasswordHashing.HashPassword(password));
        uph.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
        uph.Parameters.AddWithValue("$u", username);
        uph.ExecuteNonQuery();
        Console.WriteLine($"[AUTH] password_pipeline_v2 upgraded legacy hash for {username}");
    }

    brute.ClearAttempts(ip, username);
    var riskScore = risk.CalculateScore(username, ip, ua, deviceFp, lastIp);
    geo.RecordLogin(username, ip);
    if (geo.IsImpossibleTravel(username, ip)) riskScore += 15;
    var isNewDevice = !devMgr.IsKnownDevice(username, deviceFp, ua);
    if (isNewDevice) { riskScore += 10; devMgr.RegisterDevice(username, deviceFp, ua, ip); }
    if (riskScore >= 95) return Results.Json(new { status = "blocked", message = "Access temporarily restricted", retryAfter = 300 }, statusCode: 403);
    if (twoFa && !string.IsNullOrWhiteSpace(twoFaSecret))
    {
        var code = DefensiveInput.CleanString(req.TotpCode, 8);
        if (string.IsNullOrWhiteSpace(code)) return Results.Ok(new { status = "otp_required", otpType = "totp", pendingToken = "", message = "Open your authenticator app and enter the code." });
        if (!DefensiveInput.IsOtpCode(code)) return Results.Json(new { message = "Ogiltig 2FA-kod." }, statusCode: 401);
        if (!TotpHelper.VerifyCode(twoFaSecret, code)) return Results.Json(new { message = "Ogiltig 2FA-kod." }, statusCode: 401);
    }
    // Create claims without SessionId first
    var claims = new List<Claim> { new(ClaimTypes.Name, username), new(ClaimTypes.NameIdentifier, username) };
    var props = new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7), IssuedUtc = DateTimeOffset.UtcNow };
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), props);

    // Sätt HttpOnly device-trust cookie – backend är ensam källa till trusted status
    var deviceToken = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Token"].FirstOrDefault(), 256);
    if (!string.IsNullOrWhiteSpace(deviceToken) && DefensiveInput.IsSafeToken(deviceToken, 256))
    {
        ctx.Response.Cookies.Append("rs-dt", deviceToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromDays(30)
        });
    }
    return Results.Ok(new { status = "ok", redirect = "/chatt", isAdmin = AppHelpers.IsAdmin(username), isNewDevice, riskScore });
});

app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    var sessionMgr = ctx.RequestServices.GetRequiredService<SessionManager>();
    var sid = ctx.User.FindFirst("SessionId")?.Value;
    if (!string.IsNullOrWhiteSpace(username)) { if (!string.IsNullOrWhiteSpace(sid)) sessionMgr.InvalidateSpecificSession(username, sid); else sessionMgr.InvalidateAllSessions(username); }
    await ctx.SignOutAsync(); return Results.Ok(new { success = true });
});

app.MapPost("/api/auth/logout-all", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(u);
    await ctx.SignOutAsync(); return Results.Ok(new { status = "ok" });
});



app.MapGet("/api/me/devices", (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    // rs-me-devices-defensive-v1
    var currentToken = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Token"].FirstOrDefault(), 256);
    if (!DefensiveInput.IsSafeToken(currentToken, 256))
        currentToken = "";

    var devices = new List<object>();

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();

    cmd.CommandText = @"
        SELECT
            dt.DeviceToken,
            dt.DeviceName,
            dt.UserAgent,
            dt.FirstSeenAt,
            dt.LastSeenAt,
            dt.IsTrusted,
            dt.SeenIpCount,
            dt.SessionCount,
            dt.RiskScore,
            dt.MaturedAt
        FROM DeviceTokens dt
        JOIN AuthUsers u ON u.Id = dt.UserId
        WHERE u.Username = $u
        ORDER BY datetime(dt.LastSeenAt) DESC
        LIMIT 50;
    ";

    cmd.Parameters.AddWithValue("$u", username);

    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var token = r.IsDBNull(0) ? "" : r.GetString(0);
        var name = r.IsDBNull(1) ? "Unknown device" : r.GetString(1);
        var ua = r.IsDBNull(2) ? "" : r.GetString(2);
        var firstSeen = r.IsDBNull(3) ? "" : r.GetString(3);
        var lastSeen = r.IsDBNull(4) ? "" : r.GetString(4);
        var trusted = !r.IsDBNull(5) && r.GetInt32(5) == 1;
        var seenIpCount = r.IsDBNull(6) ? 0 : r.GetInt32(6);
        var sessionCount = r.IsDBNull(7) ? 0 : r.GetInt32(7);
        var riskScore = r.IsDBNull(8) ? 0 : r.GetInt32(8);
        var maturedAt = r.IsDBNull(9) ? "" : r.GetString(9);

        devices.Add(new
        {
            token,
            tokenPreview = token.Length > 12 ? token.Substring(0, 12) + "…" : token,
            name,
            userAgent = ua,
            firstSeenAt = firstSeen,
            lastSeenAt = lastSeen,
            current = !string.IsNullOrWhiteSpace(currentToken) && token == currentToken,
            trusted,
            seenIpCount,
            sessionCount,
            riskScore,
            matured = !string.IsNullOrWhiteSpace(maturedAt),
            maturedAt
        });
    }

    return Results.Ok(new
    {
        ok = true,
        currentDeviceToken = currentToken,
        count = devices.Count,
        devices
    });
});


app.MapGet("/api/auth/device-status", (HttpContext ctx) =>
{
    // Kontrollerar om device-token cookien matchar en känd enhet i DB
    // Används av login.html för att visa trusted-badge utan localStorage
    // rs-device-status-defensive-v1
    var deviceToken = DefensiveInput.CleanString(ctx.Request.Cookies["rs-dt"], 256);
    if (string.IsNullOrWhiteSpace(deviceToken) || !DefensiveInput.IsSafeToken(deviceToken, 256))
        return Results.Ok(new { knownDevice = false });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT COUNT(1) FROM DeviceTokens WHERE DeviceToken=$tok";
    cmd.Parameters.AddWithValue("$tok", deviceToken);
    var known = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    return Results.Ok(new { knownDevice = known });
});

app.MapGet("/api/auth/validate", (HttpContext ctx) =>
{
    if (ctx.User?.Identity?.IsAuthenticated == true)
        return Results.Ok(new { valid = true, username = ctx.User.Identity.Name });
    return Results.Unauthorized();
});

app.MapGet("/api/auth/check-username", async (string username, HttpContext ctx) =>
{
    if (string.IsNullOrWhiteSpace(username) || username.Length < 3)
        return Results.Ok(new { available = false, reason = "too_short" });
    if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"^[a-zA-Z0-9_.\-]+$"))
        return Results.Ok(new { available = false, reason = "invalid_chars" });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM AuthUsers WHERE Username=$u COLLATE NOCASE";
    cmd.Parameters.AddWithValue("$u", username.Trim().ToLowerInvariant());
    var count = Convert.ToInt32(cmd.ExecuteScalar());
    return Results.Ok(new { available = count == 0 });
});

app.MapPost("/api/auth/forgot-password", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<ForgotPasswordReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Email)) return Results.Ok(new { status = "ok" });

    // rs-forgot-password-input-defensive-v1
    var emailInput = DefensiveInput.CleanString(req.Email, 254).ToLowerInvariant();
    if (!DefensiveInput.IsEmail(emailInput)) return Results.Ok(new { status = "ok" });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Username, Email FROM AuthUsers WHERE Email = @e COLLATE NOCASE LIMIT 1";
    cmd.Parameters.AddWithValue("@e", emailInput);
    using var r = cmd.ExecuteReader();
    if (r.Read())
    {
        var username = r.GetString(0);
        var email = r.GetString(1);
        r.Close();
        var code = OtpGenerator.GenerateCode();
        try { await SmtpMailService.SendOtpAsync(email, username, code, "reset"); } catch { Console.WriteLine("[SMTP] send failed."); }
    }
    return Results.Ok(new { status = "ok", message = "If an account exists, a reset link has been sent" });
});

app.MapPost("/api/auth/resend-otp", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<ResendOtpReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.PendingToken)) return Results.BadRequest(new { status = "error" });

    // rs-resend-otp-input-defensive-v1
    var pendingToken = DefensiveInput.CleanString(req.PendingToken, 254).ToLowerInvariant();
    if (!DefensiveInput.IsSafeToken(pendingToken))
        return Results.BadRequest(new { status = "error" });

    return Results.Ok(new { status = "ok", message = "New code sent" });
});

app.MapPost("/api/auth/verify-otp", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<VerifyOtpReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.PendingToken) || string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { status = "error", message = "Ogiltig förfrågan." });

    // rs-verify-otp-input-defensive-v1
    var username = DefensiveInput.CleanString(req.PendingToken, 254).ToLowerInvariant();
    var otpCode = DefensiveInput.CleanString(req.Code, 8);
    if (!DefensiveInput.IsSafeToken(username) || !DefensiveInput.IsOtpCode(otpCode))
        return Results.BadRequest(new { status = "error", message = "Ogiltig förfrågan." });

    var cacheKey = $"reg_otp:{username}";
    if (!OtpCache.TryGet(cacheKey, out var stored) || stored != otpCode)
        return Results.Json(new { status = "error", message = "Felaktig eller utgången kod." }, statusCode: 400);
    OtpCache.Remove(cacheKey);
    using var db = DbHelpers.OpenDb();
    using var upd = db.CreateCommand();
    upd.CommandText = "UPDATE AuthUsers SET Status='verified', Badges='[\"verified\"]', EmailVerified=1 WHERE Username=$u AND Status='pending'";
    upd.Parameters.AddWithValue("$u", username);
    upd.ExecuteNonQuery();
    var identity = new System.Security.Claims.ClaimsIdentity(new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username) }, "cookie");
    var principal = new System.Security.Claims.ClaimsPrincipal(identity);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

    // Create PersistentSession after OTP verification
    // Wait a bit for cookie to be set by SignInAsync
    await Task.Delay(100);
    var sessionId = ctx.Request.Cookies[".AspNetCore.Cookies"] ?? ctx.Request.Cookies["runspace_auth_v3"] ?? Guid.NewGuid().ToString("N");
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var userAgent = DefensiveInput.CleanString(ctx.Request.Headers["User-Agent"].ToString(), 512);

    using (var sessConn = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db"))
    {
        await sessConn.OpenAsync();
        var sessCmd = sessConn.CreateCommand();
        sessCmd.CommandText = @"
            INSERT INTO PersistentSessions (Username, SessionId, Ip, UserAgent, CreatedAt, LastActivity)
            VALUES (@u, @sid, @ip, @ua, @now, @now)
            ON CONFLICT(Username, SessionId) DO UPDATE SET LastActivity = @now";
        sessCmd.Parameters.AddWithValue("@u", username);
        sessCmd.Parameters.AddWithValue("@sid", sessionId);
        sessCmd.Parameters.AddWithValue("@ip", ip);
        sessCmd.Parameters.AddWithValue("@ua", userAgent);
        sessCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        await sessCmd.ExecuteNonQueryAsync();
    }

    AppHelpers.LogActivity(username, "verify_email", "OTP verified");
    return Results.Ok(new { status = "ok", redirect = "/chatt" });
});




app.MapPost("/api/auth/email/send-code", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    // rs-email-send-code-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "email_send_code", 5, 3600))
        return Results.Json(new { error = "För många försök. Vänta en timme." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<EmailCodeReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "Ange en e-postadress." });

    var email = DefensiveInput.CleanString(req.Email, 254).ToLowerInvariant();
    if (!DefensiveInput.IsEmail(email)) return Results.BadRequest(new { error = "Ogiltig e-postadress." });

    var code = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    OtpCache.Set($"email_otp:{u}", code, TimeSpan.FromMinutes(10));
    OtpCache.Set($"email_otp_addr:{u}", email, TimeSpan.FromMinutes(10));
    try { await SmtpMailService.SendOtpAsync(email, u, code, "verify"); }
    catch { Console.WriteLine("[SMTP] send failed."); return Results.Json(new { error = "Kunde inte skicka e-post." }, statusCode: 500); }

    return Results.Ok(new { message = "Kod skickad." });
});


app.MapPost("/api/auth/email/verify", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    // rs-email-verify-defensive-v1
    var req = await ctx.Request.ReadFromJsonAsync<EmailVerifyReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Code)) return Results.BadRequest(new { error = "Ange kod." });

    var code = DefensiveInput.CleanString(req.Code, 8);
    if (!DefensiveInput.IsOtpCode(code)) return Results.BadRequest(new { error = "Felaktig eller utgången kod." });

    if (!OtpCache.TryGet($"email_otp:{u}", out var stored) || stored != code)
        return Results.Json(new { error = "Felaktig eller utgången kod." }, statusCode: 400);

    if (!OtpCache.TryGet($"email_otp_addr:{u}", out var pendingEmail))
        return Results.Json(new { error = "Sessionen har gått ut." }, statusCode: 400);

    pendingEmail = DefensiveInput.CleanString(pendingEmail, 254).ToLowerInvariant();
    if (!DefensiveInput.IsEmail(pendingEmail))
        return Results.Json(new { error = "Sessionen har gått ut." }, statusCode: 400);

    OtpCache.Remove($"email_otp:{u}");
    OtpCache.Remove($"email_otp_addr:{u}");

    using var db = DbHelpers.OpenDb();
    using var upd = db.CreateCommand();
    upd.CommandText = "UPDATE AuthUsers SET Email=$e, EmailVerified=1 WHERE Username=$u";
    upd.Parameters.AddWithValue("$e", pendingEmail);
    upd.Parameters.AddWithValue("$u", u);
    upd.ExecuteNonQuery();

    AppHelpers.LogActivity(u, "email_verified", "Email verified via settings");
    return Results.Ok(new { message = "E-postadressen har verifierats." });
});


app.MapPost("/api/auth/change-password", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "change_password", 3, 3600)) return Results.Json(new { message = "Vänta." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<ChangeReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.OldPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
        return Results.BadRequest(new { message = "Alla fält krävs" });

    // rs-change-password-defensive-v1
    // Passwords must not be cleaned or trimmed. Only bound length/null bytes before BCrypt.
    var oldPassword = req.OldPassword;
    var newPassword = req.NewPassword;

    if (!DefensiveInput.IsSafePasswordInput(oldPassword))
        return Results.BadRequest(new { message = "Ogiltigt lösenord." });

    if (!DefensiveInput.IsSafePasswordInput(newPassword))
        return Results.BadRequest(new { message = "Ogiltigt lösenord." });

    var check = PasswordPolicy.Validate(newPassword);
    if (!check.Valid) return Results.BadRequest(new { message = check.Message });

    if (oldPassword == newPassword) return Results.BadRequest(new { message = "Måste skilja sig." });

    using var db = DbHelpers.OpenDb();
    using var get = db.CreateCommand();
    get.CommandText = "SELECT PasswordHash FROM AuthUsers WHERE Username=$u LIMIT 1";
    get.Parameters.AddWithValue("$u", u);
    var cur = get.ExecuteScalar() as string;

    if (string.IsNullOrWhiteSpace(cur) || !PasswordHashing.VerifyPassword(oldPassword, cur))
        return Results.BadRequest(new { message = "Gammalt lösenord fel" });

    if (PasswordHistory.WasUsedBefore(u, newPassword + AppConfig.PasswordPepper))
        return Results.BadRequest(new { message = "Använt tidigare." });

    var newHash = PasswordHashing.HashPassword(newPassword);

    using var upd = db.CreateCommand();
    upd.CommandText = "UPDATE AuthUsers SET PasswordHash=$p, PasswordChangedAt=$t WHERE Username=$u";
    upd.Parameters.AddWithValue("$p", newHash);
    upd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    upd.Parameters.AddWithValue("$u", u);
    upd.ExecuteNonQuery();

    PasswordHistory.Save(u, newHash);
    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(u);

    return Results.Ok(new { success = true, message = "Lösenordet ändrat." });
});


app.MapPost("/api/auth/2fa/setup", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    // rs-2fa-setup-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "2fa_setup", 5, 3600))
        return Results.Json(new { message = "För många försök. Vänta en stund." }, statusCode: 429);

    var secret = TotpHelper.GenerateSecret();

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET TwoFactorSecret=$s WHERE Username=$u";
    cmd.Parameters.AddWithValue("$s", secret);
    cmd.Parameters.AddWithValue("$u", u);
    cmd.ExecuteNonQuery();

    var codes = Enumerable.Range(0, 8)
        .Select(_ => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)))
        .ToArray();

    TotpHelper.SaveBackupCodes(u, codes);

    var otpauthUrl = $"otpauth://totp/RunSpace:{u}?secret={secret}&issuer=RunSpace&digits=6&period=30";

    using var qrGenerator = new QRCodeGenerator();
    using var qrCodeData = qrGenerator.CreateQrCode(otpauthUrl, QRCodeGenerator.ECCLevel.Q);

    var pngQrCode = new PngByteQRCode(qrCodeData);
    var qrBytes = pngQrCode.GetGraphic(8);
    var qrPngBase64 = Convert.ToBase64String(qrBytes);

    return Results.Ok(new
    {
        secret,
        otpauthUrl,
        qrPngBase64,
        backupCodes = codes
    });
});


app.MapPost("/api/auth/2fa/verify", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    // rs-2fa-verify-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "2fa_verify", 10, 900))
        return Results.Json(new { message = "För många försök. Vänta en stund." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<TwoFactorVerifyReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { message = "Kod krävs." });

    var code = DefensiveInput.CleanString(req.Code, 8);
    if (!DefensiveInput.IsOtpCode(code))
        return Results.BadRequest(new { message = "Ogiltig kod." });

    using var db = DbHelpers.OpenDb();
    using var get = db.CreateCommand();
    get.CommandText = "SELECT TwoFactorSecret FROM AuthUsers WHERE Username=$u LIMIT 1";
    get.Parameters.AddWithValue("$u", u);
    var secret = get.ExecuteScalar() as string ?? "";

    if (string.IsNullOrWhiteSpace(secret))
        return Results.BadRequest(new { message = "2FA är inte konfigurerat." });

    if (!TotpHelper.VerifyCode(secret, code))
        return Results.BadRequest(new { message = "Ogiltig kod." });

    DbHelpers.EnsureColumn(db, "AuthUsers", "SecurityChangedAt", "TEXT NOT NULL DEFAULT ''");

    using var en = db.CreateCommand();
    en.CommandText = "UPDATE AuthUsers SET TwoFactorEnabled=1, SecurityChangedAt=$sc WHERE Username=$u";
    en.Parameters.AddWithValue("$sc", DateTime.UtcNow.ToString("o"));
    en.Parameters.AddWithValue("$u", u);
    en.ExecuteNonQuery();

    // rs-2fa-enable-kill-sessions-v1
    using (var delSessions = db.CreateCommand())
    {
        delSessions.CommandText = "DELETE FROM PersistentSessions WHERE LOWER(Username)=LOWER($u)";
        delSessions.Parameters.AddWithValue("$u", u);
        delSessions.ExecuteNonQuery();
    }

    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(u);

    await ctx.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Cookies.Delete("runspace_auth_v3");
    ctx.Response.Cookies.Delete("runspace_auth_v2");
    ctx.Response.Cookies.Delete("runspace_auth");
    ctx.Response.Cookies.Delete("rs-dt");

    AppHelpers.LogActivity(u, "2fa_enabled", $"From {ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}");

    return Results.Ok(new
    {
        success = true,
        loggedOut = true,
        message = "2FA enabled. Existing sessions were logged out."
    });
});


app.MapPost("/api/auth/2fa/disable", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    // rs-2fa-disable-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "2fa_disable", 5, 3600))
        return Results.Json(new { message = "För många försök. Vänta en stund." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<TwoFactorVerifyReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { message = "Kod krävs." });

    var code = DefensiveInput.CleanString(req.Code, 8);
    if (!DefensiveInput.IsOtpCode(code))
        return Results.BadRequest(new { message = "Ogiltig kod." });

    using var db = DbHelpers.OpenDb();
    using var get = db.CreateCommand();
    get.CommandText = "SELECT TwoFactorSecret FROM AuthUsers WHERE Username=$u LIMIT 1";
    get.Parameters.AddWithValue("$u", u);
    var secret = get.ExecuteScalar() as string ?? "";

    if (string.IsNullOrWhiteSpace(secret))
        return Results.BadRequest(new { message = "2FA är inte konfigurerat." });

    if (!TotpHelper.VerifyCode(secret, code))
        return Results.BadRequest(new { message = "Ogiltig kod." });

    DbHelpers.EnsureColumn(db, "AuthUsers", "SecurityChangedAt", "TEXT NOT NULL DEFAULT ''");

    using var dis = db.CreateCommand();
    dis.CommandText = "UPDATE AuthUsers SET TwoFactorEnabled=0, TwoFactorSecret='', SecurityChangedAt=$sc WHERE Username=$u";
    dis.Parameters.AddWithValue("$sc", DateTime.UtcNow.ToString("o"));
    dis.Parameters.AddWithValue("$u", u);
    dis.ExecuteNonQuery();

    // rs-2fa-disable-kill-sessions-v1
    using (var delCodes = db.CreateCommand())
    {
        delCodes.CommandText = "DELETE FROM BackupCodes WHERE LOWER(Username)=LOWER($u)";
        delCodes.Parameters.AddWithValue("$u", u);
        delCodes.ExecuteNonQuery();
    }

    using (var delSessions = db.CreateCommand())
    {
        delSessions.CommandText = "DELETE FROM PersistentSessions WHERE LOWER(Username)=LOWER($u)";
        delSessions.Parameters.AddWithValue("$u", u);
        delSessions.ExecuteNonQuery();
    }

    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(u);

    await ctx.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Cookies.Delete("runspace_auth_v3");
    ctx.Response.Cookies.Delete("runspace_auth_v2");
    ctx.Response.Cookies.Delete("runspace_auth");
    ctx.Response.Cookies.Delete("rs-dt");

    AppHelpers.LogActivity(u, "2fa_disabled", $"From {ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}");

    return Results.Ok(new
    {
        success = true,
        loggedOut = true,
        message = "2FA disabled. Existing sessions were logged out."
    });
});

app.MapGet("/api/auth/login-history", (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    return string.IsNullOrWhiteSpace(u) ? Results.Unauthorized() : Results.Ok(LoginHistory.GetForUser(u));
});

app.MapGet("/api/auth/sessions", (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    return string.IsNullOrWhiteSpace(u) ? Results.Unauthorized() : Results.Ok(ctx.RequestServices.GetRequiredService<SessionManager>().GetUserSessions(u));
});


app.MapDelete("/api/auth/sessions/{sessionId}", (string sessionId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();

    // rs-session-delete-defensive-v1
    var sid = DefensiveInput.CleanString(sessionId, 128);
    if (!DefensiveInput.IsSafeSessionId(sid))
        return Results.BadRequest(new { message = "Invalid session id." });

    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateSpecificSession(u, sid);
    return Results.Ok(new { success = true });
});


app.MapPost("/api/auth/freeze", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();

    // rs-freeze-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "account_freeze", 3, 3600))
        return Results.Json(new { message = "För många försök. Vänta en stund." }, statusCode: 429);

    FreezeReq? req;
    try { req = await ctx.Request.ReadFromJsonAsync<FreezeReq>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }

    var hours = Math.Clamp(req?.Hours ?? 24, 1, 720);

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET AccountLockedUntil=$l WHERE Username=$u";
    cmd.Parameters.AddWithValue("$l", DateTime.UtcNow.AddHours(hours).ToString("o"));
    cmd.Parameters.AddWithValue("$u", u);
    cmd.ExecuteNonQuery();

    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(u);
    await ctx.SignOutAsync();

    return Results.Ok(new { success = true, message = $"Fryst i {hours}h." });
});

app.MapGet("/api/profile", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) { await ctx.SignOutAsync(); return Results.Unauthorized(); }
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Bio,AvatarUrl,CreatedAt,Status,Badges,Links,BannerUrl FROM AuthUsers WHERE Username=$u LIMIT 1"; cmd.Parameters.AddWithValue("$u", u);
    using var r = cmd.ExecuteReader(); if (!r.Read()) { await ctx.SignOutAsync(); return Results.Unauthorized(); }
    var ca = r.IsDBNull(2) ? "" : r.GetString(2);
    return Results.Ok(new { bio = InputSanitizer.SanitizeOutput(r.IsDBNull(0) ? "" : r.GetString(0)), avatarUrl = InputSanitizer.SanitizeUrl(r.IsDBNull(1) ? "" : r.GetString(1)), createdAt = ca, age = AppHelpers.GetAgeTextFromCreatedAt(ca), status = r.IsDBNull(3) ? "verified" : r.GetString(3), badges = AppHelpers.ParseBadges(r.IsDBNull(4) ? "[]" : r.GetString(4)), links = JsonSerializer.Deserialize<List<object>>(r.IsDBNull(5) ? "[]" : r.GetString(5)) ?? new List<object>() });
});

app.MapGet("/api/profile/public/{username}", (string username, HttpContext ctx) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(ctx.Connection.RemoteIpAddress?.ToString() ?? "", "public_profile", 300, 60)) return Results.Json(new { message = "Rate limit." }, statusCode: 429);
    var t = (username ?? "").Trim().ToLowerInvariant();
    if (!AppHelpers.IsValidUsername(t)) return Results.BadRequest(new { message = "Ogiltigt." });
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Username,Bio,AvatarUrl,CreatedAt,Status,Badges,Links,BannerUrl,IsSuspended,IsPremium,PremiumPlan,aura_score,aura_active_days,aura_last_active_date FROM AuthUsers WHERE Username=$u LIMIT 1"; cmd.Parameters.AddWithValue("$u", t);
    using var r = cmd.ExecuteReader(); if (!r.Read()) return Results.NotFound(new { message = "Ej hittad" });
    var ca = r.IsDBNull(3) ? "" : r.GetString(3);
    var _isSusp = !r.IsDBNull(8) && r.GetInt32(8) == 1;
    var _uname = r.GetString(0); var _bio = r.IsDBNull(1) ? "" : r.GetString(1); var _av = r.IsDBNull(2) ? "" : r.GetString(2); var _stat = r.IsDBNull(4) ? "verified" : r.GetString(4); var _badges = r.IsDBNull(5) ? "[]" : r.GetString(5); var _links = r.IsDBNull(6) ? "[]" : r.GetString(6); var _banner = r.IsDBNull(7) ? "" : r.GetString(7);
    var _premiumState = PremiumAccess.GetForUsername(_uname);
    var _auraScore = r.IsDBNull(11) ? 0.0 : Convert.ToDouble(r.GetValue(11));
    var _auraActiveDays = r.IsDBNull(12) ? 0 : Convert.ToInt32(r.GetValue(12));
    var _auraLastActiveDate = r.IsDBNull(13) ? "" : r.GetString(13);
    r.Close();
    // Check trust level separately
    using var trustCmd = db.CreateCommand();
    trustCmd.CommandText = "SELECT TrustLevel FROM AuthUsers WHERE Username=$u2 LIMIT 1";
    trustCmd.Parameters.AddWithValue("$u2", t);
    var _trustLevel = trustCmd.ExecuteScalar() as string ?? "medium";
    var _isRestricted = _isSusp || _trustLevel == "blocked";
    // Fetch publicId
    string _publicId = "";
    using (var pidCmd = db.CreateCommand())
    {
        pidCmd.CommandText = "SELECT PublicId FROM AuthUsers WHERE Username=$u3 LIMIT 1";
        pidCmd.Parameters.AddWithValue("$u3", t);
        _publicId = pidCmd.ExecuteScalar() as string ?? "";
    }
    // Read premium fields
    using var premCmd = db.CreateCommand();
    premCmd.CommandText = "SELECT IsPremium, PremiumPlan FROM AuthUsers WHERE Username=$up LIMIT 1";
    premCmd.Parameters.AddWithValue("$up", t);
    using var premR = premCmd.ExecuteReader();
    var _isPremium = false;
    var _premiumPlan = "";
    if (premR.Read())
    {
        _isPremium = !premR.IsDBNull(0) && premR.GetInt32(0) == 1;
        _premiumPlan = premR.IsDBNull(1) ? "" : premR.GetString(1);
    }
    premR.Close();
    long _followersCount = 0;
    long _followingCount = 0;
    bool _isFollowing = false;

    using (var fc = db.CreateCommand())
    {
        fc.CommandText = "SELECT COUNT(*) FROM ProfileFollows WHERE TargetUserId=(SELECT Id FROM AuthUsers WHERE Username=$u LIMIT 1)";
        fc.Parameters.AddWithValue("$u", t);
        _followersCount = (long)(fc.ExecuteScalar() ?? 0L);
    }

    using (var fg = db.CreateCommand())
    {
        fg.CommandText = "SELECT COUNT(*) FROM ProfileFollows WHERE FollowerUserId=(SELECT Id FROM AuthUsers WHERE Username=$u LIMIT 1)";
        fg.Parameters.AddWithValue("$u", t);
        _followingCount = (long)(fg.ExecuteScalar() ?? 0L);
    }

    var _viewer = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!string.IsNullOrWhiteSpace(_viewer))
    {
        using var ifc = db.CreateCommand();
        ifc.CommandText = @"
            SELECT COUNT(*)
            FROM ProfileFollows
            WHERE FollowerUserId=(SELECT Id FROM AuthUsers WHERE Username=$viewer LIMIT 1)
              AND TargetUserId=(SELECT Id FROM AuthUsers WHERE Username=$target LIMIT 1)";
        ifc.Parameters.AddWithValue("$viewer", _viewer);
        ifc.Parameters.AddWithValue("$target", t);
        _isFollowing = Convert.ToInt64(ifc.ExecuteScalar() ?? 0L) > 0;
    }

    return Results.Ok(new { username = _uname, bio = InputSanitizer.SanitizeOutput(_bio), avatarUrl = InputSanitizer.SanitizeUrl(_av), createdAt = ca, age = AppHelpers.GetAgeTextFromCreatedAt(ca), status = _stat, badges = AppHelpers.ParseBadges(_badges), links = _links, bannerUrl = _banner, isSuspended = _isSusp, isRestricted = _isRestricted, publicId = _publicId, isPremium = _premiumState.IsPremium, premiumPlan = _premiumState.Plan, followersCount = _followersCount, followingCount = _followingCount, isFollowing = _isFollowing, auraScore = Math.Round(_auraScore, 1), auraActiveDays = _auraActiveDays, auraLastActiveDate = _auraLastActiveDate, aura = new { score = Math.Round(_auraScore, 1), activeDays = _auraActiveDays, lastActiveDate = _auraLastActiveDate } });
});


app.MapPost("/api/profile/follow/{username}", (string username, HttpContext ctx) =>
{
    var viewer = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(viewer) || !AppHelpers.UserExists(viewer)) return Results.Unauthorized();

    var target = (username ?? "").Trim().ToLowerInvariant();
    if (!AppHelpers.IsValidUsername(target)) return Results.BadRequest(new { message = "Ogiltigt användarnamn." });
    if (viewer == target) return Results.BadRequest(new { message = "Du kan inte följa dig själv." });

    using var db = DbHelpers.OpenDb();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        INSERT OR IGNORE INTO ProfileFollows (FollowerUserId, TargetUserId)
        SELECT f.Id, t.Id
        FROM AuthUsers f, AuthUsers t
        WHERE f.Username=$viewer AND t.Username=$target";
    cmd.Parameters.AddWithValue("$viewer", viewer);
    cmd.Parameters.AddWithValue("$target", target);
    cmd.ExecuteNonQuery();

    using var count = db.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM ProfileFollows WHERE TargetUserId=(SELECT Id FROM AuthUsers WHERE Username=$target LIMIT 1)";
    count.Parameters.AddWithValue("$target", target);
    var followersCount = (long)(count.ExecuteScalar() ?? 0L);

    return Results.Ok(new { ok = true, isFollowing = true, followersCount });
});

app.MapDelete("/api/profile/follow/{username}", (string username, HttpContext ctx) =>
{
    var viewer = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(viewer) || !AppHelpers.UserExists(viewer)) return Results.Unauthorized();

    var target = (username ?? "").Trim().ToLowerInvariant();
    if (!AppHelpers.IsValidUsername(target)) return Results.BadRequest(new { message = "Ogiltigt användarnamn." });

    using var db = DbHelpers.OpenDb();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        DELETE FROM ProfileFollows
        WHERE FollowerUserId=(SELECT Id FROM AuthUsers WHERE Username=$viewer LIMIT 1)
          AND TargetUserId=(SELECT Id FROM AuthUsers WHERE Username=$target LIMIT 1)";
    cmd.Parameters.AddWithValue("$viewer", viewer);
    cmd.Parameters.AddWithValue("$target", target);
    cmd.ExecuteNonQuery();

    using var count = db.CreateCommand();
    count.CommandText = "SELECT COUNT(*) FROM ProfileFollows WHERE TargetUserId=(SELECT Id FROM AuthUsers WHERE Username=$target LIMIT 1)";
    count.Parameters.AddWithValue("$target", target);
    var followersCount = (long)(count.ExecuteScalar() ?? 0L);

    return Results.Ok(new { ok = true, isFollowing = false, followersCount });
});

app.MapPost("/api/profile/update", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    // rs-profile-update-defensive-v1
    // Keep profile fields small, predictable, and safe before storage.
    // rs-profile-update-json-guard-v1
    System.Text.Json.JsonElement profileRoot;
    try
    {
        using var profileDoc = await JsonDocument.ParseAsync(ctx.Request.Body);
        profileRoot = profileDoc.RootElement.Clone();
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    if (profileRoot.ValueKind != JsonValueKind.Object)
        return Results.BadRequest(new { message = "Invalid profile body." });

    string GetProfileString(string name, int maxLen)
    {
        if (!profileRoot.TryGetProperty(name, out var el)) return "";
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return "";
        if (el.ValueKind != JsonValueKind.String) return "";
        return DefensiveInput.CleanString(el.GetString(), maxLen);
    }

    string NormalizeProfileLinks()
    {
        if (!profileRoot.TryGetProperty("links", out var linksEl))
            return "[]";

        string raw;

        if (linksEl.ValueKind == JsonValueKind.String)
            raw = linksEl.GetString() ?? "[]";
        else if (linksEl.ValueKind == JsonValueKind.Array)
            raw = linksEl.GetRawText();
        else
            return "[]";

        raw = raw.Trim();

        if (raw.Length == 0) return "[]";
        if (raw.Length > 4000) return "[]";
        if (raw.Contains('\0')) return "[]";

        var lowered = raw.ToLowerInvariant();
        if (lowered.Contains("javascript:") || lowered.Contains("data:") || lowered.Contains("vbscript:"))
            return "[]";

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "[]";

            if (doc.RootElement.GetArrayLength() > 8)
                return "[]";

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var itemRaw = item.GetRawText();
                if (itemRaw.Length > 700 || itemRaw.Contains('\0'))
                    return "[]";

                var itemLower = itemRaw.ToLowerInvariant();
                if (itemLower.Contains("javascript:") || itemLower.Contains("data:") || itemLower.Contains("vbscript:"))
                    return "[]";
            }

            return doc.RootElement.GetRawText();
        }
        catch
        {
            return "[]";
        }
    }

    var bio = GetProfileString("bio", 500);
    if (ContentFilter.IsOffensive(bio)) return Results.BadRequest(new { message = "Otillåtet." });

    var nat = GetProfileString("nationality", 4);
    var langs = GetProfileString("languages", 50);
    var linksJson = NormalizeProfileLinks();

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET Bio=$b, Nationality=$n, Languages=$l, Links=$links WHERE Username=$u";
    cmd.Parameters.AddWithValue("$b", bio);
    cmd.Parameters.AddWithValue("$n", nat);
    cmd.Parameters.AddWithValue("$l", langs);
    cmd.Parameters.AddWithValue("$links", linksJson);
    cmd.Parameters.AddWithValue("$u", u);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { success = true });
});

app.MapPost("/api/profile/avatar/upload", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "avatar_upload", 5, 300)) return Results.Json(new { error = "Rate limit." }, statusCode: 429);
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "form-data krävs" });
    var form = await ctx.Request.ReadFormAsync(); var file = form.Files["avatar"];
    if (file == null || file.Length == 0 || file.Length > 5 * 1024 * 1024) return Results.BadRequest(new { error = "Fil saknas eller för stor." });

    // rs-upload-filename-defensive-v2-avatar
    var originalName = DefensiveInput.SafeFileName(file.FileName);
    var ext = Path.GetExtension(originalName).ToLowerInvariant();
    if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(ext)) return Results.BadRequest(new { error = "Ogiltigt format" });
    if (!await FileValidator.IsValidImageAsync(file)) return Results.BadRequest(new { error = "Ej giltig bild." });
    var fn = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}{ext}";
    var path = Path.Combine(AppConfig.AvatarUploadDir, fn);
    await using (var s = System.IO.File.Create(path)) await file.CopyToAsync(s);
    var url = $"/uploads/avatars/{fn}";
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET AvatarUrl=$p WHERE Username=$u"; cmd.Parameters.AddWithValue("$p", url); cmd.Parameters.AddWithValue("$u", u); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true, avatarUrl = url });
}).DisableAntiforgery();


app.MapPost("/api/profile/banner/upload", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "form-data krävs" });
    if (!PremiumAccess.HasFeature(u, "profile_banner"))
        return Results.Json(new
        {
            error = "premium_required",
            message = "Profile banners require RunSpace Premium.",
            requiredPlan = "premium"
        }, statusCode: 403);

    var form = await ctx.Request.ReadFormAsync(); var file = form.Files["banner"];
    if (file == null || file.Length == 0 || file.Length > 8 * 1024 * 1024) return Results.BadRequest(new { error = "Fil saknas eller för stor (max 8MB)." });

    // rs-upload-filename-defensive-v2-banner
    var originalName = DefensiveInput.SafeFileName(file.FileName);
    var ext = Path.GetExtension(originalName).ToLowerInvariant();
    if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(ext)) return Results.BadRequest(new { error = "Ogiltigt format" });
    var fn = $"banner_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}{ext}";
    var path = Path.Combine(AppConfig.AvatarUploadDir, fn);
    await using (var s = System.IO.File.Create(path)) await file.CopyToAsync(s);
    var url = $"/uploads/avatars/{fn}";
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET BannerUrl=$p WHERE Username=$u"; cmd.Parameters.AddWithValue("$p", url); cmd.Parameters.AddWithValue("$u", u); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true, bannerUrl = url });
}).DisableAntiforgery();
app.MapGet("/api/users/search", (string? q, HttpContext ctx) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(ctx.Connection.RemoteIpAddress?.ToString() ?? "", "user_search", 30, 60)) return Results.Json(new { message = "Rate limit." }, statusCode: 429);
    var query = InputSanitizer.SanitizeSearchQuery((q ?? "").Trim().ToLowerInvariant());
    if (query.Length < 2) return Results.Ok(Array.Empty<object>());
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Username,Bio,AvatarUrl,CreatedAt,Status,Badges FROM AuthUsers WHERE LOWER(Username) LIKE $q ORDER BY Username LIMIT 25";
    cmd.Parameters.AddWithValue("$q", "%" + query + "%");
    var list = new List<object>(); using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { username = r.GetString(0), bio = InputSanitizer.SanitizeOutput(r.IsDBNull(1) ? "" : r.GetString(1)), avatarUrl = InputSanitizer.SanitizeUrl(r.IsDBNull(2) ? "" : r.GetString(2)), createdAt = r.IsDBNull(3) ? "" : r.GetString(3), status = r.IsDBNull(4) ? "verified" : r.GetString(4), badges = AppHelpers.ParseBadges(r.IsDBNull(5) ? "[]" : r.GetString(5)) });
    return Results.Ok(list);
});

// ═══════════════════════════════════════════════
// CHAT (DM) REACTIONS
// ═══════════════════════════════════════════════
app.MapPost("/api/chat/reaction", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<ReactionReq>();
    if (req == null || req.MessageId <= 0 || string.IsNullOrWhiteSpace(req.Emoji)) return Results.BadRequest(new { message = "Ogiltigt." });
    var emoji = req.Emoji.Trim(); if (emoji.Length > 4) return Results.BadRequest(new { message = "Ogiltig emoji." });
    using var db = DbHelpers.OpenDb();
    using var check = db.CreateCommand();
    check.CommandText = "SELECT Id FROM ChatReactions WHERE MessageId=$mid AND Username=$u AND Emoji=$e LIMIT 1";
    check.Parameters.AddWithValue("$mid", req.MessageId); check.Parameters.AddWithValue("$u", u); check.Parameters.AddWithValue("$e", emoji);
    var existing = check.ExecuteScalar();
    if (existing != null) { using var del = db.CreateCommand(); del.CommandText = "DELETE FROM ChatReactions WHERE Id=$id"; del.Parameters.AddWithValue("$id", existing); del.ExecuteNonQuery(); return Results.Ok(new { success = true, action = "removed" }); }
    using var ins = db.CreateCommand();
    ins.CommandText = "INSERT INTO ChatReactions (MessageId,Username,Emoji,CreatedAt) VALUES ($mid,$u,$e,$t)";
    ins.Parameters.AddWithValue("$mid", req.MessageId); ins.Parameters.AddWithValue("$u", u); ins.Parameters.AddWithValue("$e", emoji); ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    ins.ExecuteNonQuery(); return Results.Ok(new { success = true, action = "added" });
});


// ═══════════════════════════════════════════════
app.MapGet("/api/chat/reactions/{messageId}", (long messageId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Username, Emoji FROM ChatReactions WHERE MessageId=$mid";
    cmd.Parameters.AddWithValue("$mid", messageId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read()) list.Add(new { user = r.GetString(0), emoji = r.GetString(1) });
    return Results.Ok(list);
});
app.MapGet("/api/chat/reactions/batch", (string ids, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var idList = (ids ?? "").Split(',').Select(s => long.TryParse(s.Trim(), out var n) ? n : 0).Where(n => n > 0).Take(500).ToList();
    if (!idList.Any()) return Results.Ok(new Dictionary<string, object>());
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();

    var placeholders = idList.Select((_, i) => "$id" + i).ToList();
    cmd.CommandText = $"SELECT MessageId, Username, Emoji FROM ChatReactions WHERE MessageId IN ({string.Join(",", placeholders)})";

    for (var i = 0; i < idList.Count; i++)
        cmd.Parameters.AddWithValue("$id" + i, idList[i]);

    using var r = cmd.ExecuteReader();
    var result = new Dictionary<string, List<object>>();
    while (r.Read())
    {
        var mid = r.GetInt64(0).ToString();
        if (!result.ContainsKey(mid)) result[mid] = new List<object>();
        result[mid].Add(new { user = r.GetString(1), emoji = r.GetString(2) });
    }
    return Results.Ok(result);
});
app.MapGet("/api/admin/reports", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.IsAdmin(u)) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Id,ReportedBy,ReportedUser,Reason,Details,CreatedAt FROM UserReports ORDER BY Id DESC LIMIT 200";
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read()) rows.Add(new
    {
        id = r.GetInt64(0),
        reportedBy = r.GetString(1),
        reportedUser = r.GetString(2),
        reason = r.GetString(3),
        details = r.IsDBNull(4) ? "" : r.GetString(4),
        createdAt = r.GetString(5)
    });
    return Results.Ok(rows);
});

app.MapPost("/api/reports", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<UserReportReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.ReportedUser) || string.IsNullOrWhiteSpace(req.Reason))
        return Results.BadRequest(new { message = "Ogiltigt." });
    var target = req.ReportedUser.Trim().ToLowerInvariant();
    if (target == u) return Results.BadRequest(new { message = "Du kan inte rapportera dig sjalv." });
    if (!AppHelpers.UserExists(target)) return Results.NotFound(new { message = "Anvandaren finns inte." });
    var validReasons = new[] { "spam", "trakasseri", "hot", "olagligt", "annat", "harassment", "inappropriate", "threat", "other" };
    if (!validReasons.Contains(req.Reason.Trim().ToLowerInvariant())) return Results.BadRequest(new { message = "Ogiltig anledning." });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO UserReports (ReportedBy,ReportedUser,Reason,Details,MessageId,CreatedAt) VALUES ($by,$user,$reason,$details,$mid,$t)";
    cmd.Parameters.AddWithValue("$by", u);
    cmd.Parameters.AddWithValue("$user", target);
    cmd.Parameters.AddWithValue("$reason", req.Reason.Trim().ToLowerInvariant());
    var det = (req.Details ?? "").Trim();
    cmd.Parameters.AddWithValue("$details", det.Length > 500 ? det[..500] : det);
    cmd.Parameters.AddWithValue("$mid", req.MessageId > 0 ? (object)req.MessageId : System.DBNull.Value);
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
    // Notify admins via SignalR
    var hub = ctx.RequestServices.GetRequiredService<IHubContext<ChatHub>>();
    var alertText = $"[REPORT] {u} reported @{target} — {req.Reason}{(string.IsNullOrWhiteSpace(det) ? "" : " — " + det)}";
    var adminList1 = new List<string>(); using (var _admc = db.CreateCommand()) { _admc.CommandText = "SELECT Username FROM AuthUsers WHERE IsAdmin=1"; using var _admr = _admc.ExecuteReader(); while (_admr.Read()) adminList1.Add(_admr.GetString(0)); }
    foreach (var admin in adminList1)
    {
        var ts = DateTime.UtcNow.ToString("o");
        using var ins = db.CreateCommand();
        ins.CommandText = "INSERT INTO ChatMessages (FromUser,ToUser,Message,Timestamp,Encrypted,Iv,Algorithm,EncryptedKey,SenderEncryptedKey,RecipientKeysJson,SenderKeysJson,ReplyToId) VALUES ($from,$to,$msg,$ts,0,'','plain','','','[]','[]',0); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$from", target);
        ins.Parameters.AddWithValue("$to", admin);
        ins.Parameters.AddWithValue("$msg", alertText);
        ins.Parameters.AddWithValue("$ts", ts);
        var msgId = (long)(ins.ExecuteScalar() ?? 0L);
        var payload = ChatHelpers.BuildPayload(msgId, target, admin, alertText, ts, false, "", "plain", "", "", "[]", "[]", 0);
        await hub.Clients.User(admin).SendAsync("ReceiveMessage", payload);
    }
    return Results.Ok(new { success = true, message = "Rapport skickad." });
});
// ═══════════════════════════════════════════════

// CHAT (DM) MESSAGING + KEYS
// ═══════════════════════════════════════════════

// ACCOUNT E2EE KEYS
app.MapE2eeRoutes();

app.MapPost("/api/chat/public-key", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u))
        return Results.Unauthorized();

    // rs-chat-public-key-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "chat_public_key", 20, 3600))
        return Results.Json(new { message = "Rate limit." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<PublicKeyReq>();
    if (req == null)
        return Results.BadRequest(new { message = "Invalid request." });

    var did = string.IsNullOrWhiteSpace(req.DeviceId)
        ? "legacy-default"
        : DefensiveInput.CleanString(req.DeviceId, 100);

    if (!DefensiveInput.IsSafeDeviceId(did))
        return Results.BadRequest(new { message = "Invalid device id." });

    var dn = string.IsNullOrWhiteSpace(req.DeviceName)
        ? "Legacy Client"
        : DefensiveInput.CleanString(req.DeviceName, 80);

    if (dn.Length == 0)
        dn = "Legacy Client";

    if (!DefensiveInput.TryNormalizeRsaPublicKey(req.PublicKey, 8192, out var normalizedKey))
        return Results.BadRequest(new { message = "Invalid public key." });

    var now = DateTime.UtcNow.ToString("o");

    using var db = DbHelpers.OpenDb();
    using var upsert = db.CreateCommand();

    upsert.CommandText = @"INSERT INTO UserDeviceKeys
        (Username, DeviceId, DeviceName, PublicKey, CreatedAt, LastUsedAt)
        VALUES ($u, $did, $dn, $key, $c, $l)
        ON CONFLICT(Username, DeviceId) DO UPDATE SET
            DeviceName = excluded.DeviceName,
            PublicKey = excluded.PublicKey,
            LastUsedAt = excluded.LastUsedAt";

    upsert.Parameters.AddWithValue("$u", u);
    upsert.Parameters.AddWithValue("$did", did);
    upsert.Parameters.AddWithValue("$dn", dn);
    upsert.Parameters.AddWithValue("$key", normalizedKey);
    upsert.Parameters.AddWithValue("$c", now);
    upsert.Parameters.AddWithValue("$l", now);
    upsert.ExecuteNonQuery();

    using var leg = db.CreateCommand();
    leg.CommandText = "UPDATE AuthUsers SET PublicKey=$k WHERE Username=$u";
    leg.Parameters.AddWithValue("$k", normalizedKey);
    leg.Parameters.AddWithValue("$u", u);
    leg.ExecuteNonQuery();

    return Results.Ok(new { success = true, deviceId = did, deviceName = dn });
});

app.MapPost("/api/chat/device-key", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u))
        return Results.Unauthorized();

    // rs-chat-device-key-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "chat_device_key", 30, 3600))
        return Results.Json(new { message = "Rate limit." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<DeviceKeyUpsertReq>();
    if (req == null)
        return Results.BadRequest(new { message = "Invalid request." });

    var did = DefensiveInput.CleanString(req.DeviceId, 100);
    if (!DefensiveInput.IsSafeDeviceId(did))
        return Results.BadRequest(new { message = "Invalid device id." });

    var dn = string.IsNullOrWhiteSpace(req.DeviceName)
        ? "Unnamed"
        : DefensiveInput.CleanString(req.DeviceName, 80);

    if (dn.Length == 0)
        dn = "Unnamed";

    if (!DefensiveInput.TryNormalizeRsaPublicKey(req.PublicKey, 8192, out var normalizedKey))
        return Results.BadRequest(new { message = "Invalid public key." });

    var now = DateTime.UtcNow.ToString("o");

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();

    cmd.CommandText = @"INSERT INTO UserDeviceKeys
        (Username, DeviceId, DeviceName, PublicKey, CreatedAt, LastUsedAt)
        VALUES ($u, $did, $dn, $key, $c, $l)
        ON CONFLICT(Username, DeviceId) DO UPDATE SET
            DeviceName = excluded.DeviceName,
            PublicKey = excluded.PublicKey,
            LastUsedAt = excluded.LastUsedAt";

    cmd.Parameters.AddWithValue("$u", u);
    cmd.Parameters.AddWithValue("$did", did);
    cmd.Parameters.AddWithValue("$dn", dn);
    cmd.Parameters.AddWithValue("$key", normalizedKey);
    cmd.Parameters.AddWithValue("$c", now);
    cmd.Parameters.AddWithValue("$l", now);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { success = true, deviceId = did, deviceName = dn });
});

app.MapGet("/api/chat/device-keys/me", (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    return Results.Ok(new { username = u, keys = ChatKeyHelpers.GetUserDeviceKeys(u) });
});


app.MapDelete("/api/chat/device-key/{deviceId}", (string deviceId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u))
        return Results.Unauthorized();

    // rs-chat-device-key-delete-defensive-v1
    var did = DefensiveInput.CleanString(deviceId, 100);
    if (!DefensiveInput.IsSafeDeviceId(did))
        return Results.BadRequest(new { message = "Invalid device id." });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();

    cmd.CommandText = "DELETE FROM UserDeviceKeys WHERE Username=$u AND DeviceId=$did";
    cmd.Parameters.AddWithValue("$u", u);
    cmd.Parameters.AddWithValue("$did", did);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { success = true });
});


app.MapGet("/api/chat/public-key/{username}", (string username, HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    // rs-chat-public-key-get-defensive-v1
    var t = DefensiveInput.CleanString(username, 32).ToLowerInvariant();

    if (!DefensiveInput.IsUsername(t) || !AppHelpers.IsValidUsername(t))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(t))
        return Results.NotFound(new { message = "Not found." });

    using var db = DbHelpers.OpenDb();

    string legacy = "";
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT PublicKey FROM AuthUsers WHERE Username=$u LIMIT 1";
        cmd.Parameters.AddWithValue("$u", t);
        legacy = cmd.ExecuteScalar() as string ?? "";
    }

    var keys = ChatKeyHelpers.GetUserDeviceKeys(t);

    return Results.Ok(new { username = t, publicKey = legacy, keys });
});

app.MapPost("/api/chat/upload-image", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    var _ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    // rs-chat-upload-device-token-defensive-v1
    var _deviceToken = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Token"].FirstOrDefault() ?? "", 256);
    if (!DefensiveInput.IsSafeToken(_deviceToken))
        _deviceToken = "";
    // Trust gate — server-side, cannot be bypassed
    string _trustLevel = "medium";
    using (var _db = DbHelpers.OpenDb())
    {
        using var _tc = _db.CreateCommand();
        _tc.CommandText = "SELECT TrustLevel, IsSuspended FROM AuthUsers WHERE Username=$u";
        _tc.Parameters.AddWithValue("$u", u);
        using var _tr = _tc.ExecuteReader();
        if (_tr.Read()) _trustLevel = (!_tr.IsDBNull(1) && _tr.GetInt32(1) == 1) ? "blocked" : (_tr.IsDBNull(0) ? "medium" : _tr.GetString(0));
    }
    // Rate limit by trust
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    int _uploadLimit = _trustLevel == "high" ? 20 : 5;
    bool _isAdmin; using (var _adb = DbHelpers.OpenDb()) { using var _ac = _adb.CreateCommand(); _ac.CommandText = "SELECT IsAdmin FROM AuthUsers WHERE Username=$u"; _ac.Parameters.AddWithValue("$u", u); var _ar = _ac.ExecuteScalar(); _isAdmin = _ar != null && Convert.ToInt32(_ar) == 1; }
    if (!_isAdmin && !limiter.IsAllowed(u, "chat_upload", _uploadLimit, 3600)) return Results.Json(new { error = "Rate limit för uppladdningar." }, statusCode: 429);
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "form-data" });
    var form = await ctx.Request.ReadFormAsync(); var file = form.Files["image"];
    if (file == null || file.Length == 0) return Results.BadRequest(new { error = "Fil saknas." });

    // rs-safe-filename-chat-upload-image-v1
    // Never store or echo raw client-provided filenames.
    var originalName = DefensiveInput.SafeFileName(file.FileName);
    long _maxSize = _trustLevel == "high" ? 10 * 1024 * 1024 : 5 * 1024 * 1024;
    if (file.Length > _maxSize) return Results.Json(new { error = $"Max {_maxSize / 1024 / 1024} MB för din trust-nivå." }, statusCode: 422);
    var ext = Path.GetExtension(originalName).ToLowerInvariant();
    if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(ext)) return Results.Json(new { error = "Endast bilder tillåtna (png, jpg, gif, webp)." }, statusCode: 422);
    if (!await FileValidator.IsValidImageAsync(file))
    {
        using var _db3 = DbHelpers.OpenDb();
        using var _evc2 = _db3.CreateCommand();
        _evc2.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail,  CreatedAt)
            SELECT Id, 'upload_magic_mismatch', 'alert', $d,  datetime('now') FROM AuthUsers WHERE Username=$u";
        _evc2.Parameters.AddWithValue("$d", $"File={originalName} Ext={ext} claimed image");
        _evc2.Parameters.AddWithValue("$ip", _ip); _evc2.Parameters.AddWithValue("$u", u);
        _evc2.ExecuteNonQuery();
        return Results.Json(new { error = "Filinnehållet matchar inte bildformatet." }, statusCode: 422);
    }
    var fn = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}{ext}";
    var path = Path.Combine(AppConfig.ChatUploadDir, fn);
    await using (var s = System.IO.File.Create(path)) await file.CopyToAsync(s);
    // Log successful upload
    using (var _db4 = DbHelpers.OpenDb())
    {
        using var _ulc = _db4.CreateCommand();
        _ulc.CommandText = @"INSERT INTO FileUploads (UserId, OriginalName, StoredName, ContentType, FileSizeBytes, Extension, Status,  UploadedAt)
            SELECT Id, $orig, $stored, $ct, $size, $ext, 'ok',  datetime('now') FROM AuthUsers WHERE Username=$u";
        _ulc.Parameters.AddWithValue("$orig", originalName.Length > 255 ? originalName[..255] : originalName);
        _ulc.Parameters.AddWithValue("$stored", fn); _ulc.Parameters.AddWithValue("$ct", file.ContentType ?? "image");
        _ulc.Parameters.AddWithValue("$size", file.Length); _ulc.Parameters.AddWithValue("$ext", ext);
        _ulc.Parameters.AddWithValue("$ip", _ip); _ulc.Parameters.AddWithValue("$u", u);
        _ulc.ExecuteNonQuery();
    }
    return Results.Ok(new { success = true, imageUrl = $"/uploads/chat/{fn}", fileName = fn, size = file.Length });
}).DisableAntiforgery();

app.MapPost("/api/chat/upload-file", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    var _ip2 = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    // Trust gate
    string _trustLevel2 = "medium";
    using (var _db = DbHelpers.OpenDb())
    {
        using var _tc = _db.CreateCommand();
        _tc.CommandText = "SELECT TrustLevel, IsSuspended FROM AuthUsers WHERE Username=$u";
        _tc.Parameters.AddWithValue("$u", u);
        using var _tr = _tc.ExecuteReader();
        if (_tr.Read()) _trustLevel2 = (!_tr.IsDBNull(1) && _tr.GetInt32(1) == 1) ? "blocked" : (_tr.IsDBNull(0) ? "medium" : _tr.GetString(0));
    }
    var limiter2 = ctx.RequestServices.GetRequiredService<RateLimiter>();
    int _uploadLimit2 = _trustLevel2 == "high" ? 20 : 5;
    bool _isAdmin2; using (var _adb2 = DbHelpers.OpenDb()) { using var _ac2 = _adb2.CreateCommand(); _ac2.CommandText = "SELECT IsAdmin FROM AuthUsers WHERE Username=$u"; _ac2.Parameters.AddWithValue("$u", u); var _ar2 = _ac2.ExecuteScalar(); _isAdmin2 = _ar2 != null && Convert.ToInt32(_ar2) == 1; }
    if (!_isAdmin2 && !limiter2.IsAllowed(u, "chat_upload_file", _uploadLimit2, 3600)) return Results.Json(new { error = "Rate limit för uppladdningar." }, statusCode: 429);
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "form-data" });
    var form = await ctx.Request.ReadFormAsync(); var file = form.Files["file"];
    if (file == null || file.Length == 0) return Results.BadRequest(new { error = "Fil saknas." });

    // rs-safe-filename-chat-upload-file-v1
    // Never store or echo raw client-provided filenames.
    var originalName = DefensiveInput.SafeFileName(file.FileName);
    long _maxSize2 = _trustLevel2 == "high" ? 50 * 1024 * 1024 : 10 * 1024 * 1024;
    if (file.Length > _maxSize2) return Results.Json(new { error = $"Max {_maxSize2 / 1024 / 1024} MB för din trust-nivå." }, statusCode: 422);
    var ext2 = Path.GetExtension(originalName).ToLowerInvariant();
    // Comprehensive block list
    var blocked2 = new HashSet<string> { ".exe",".bat",".cmd",".msi",".scr",".ps1",".psm1",".vbs",".js",".jsx",
        ".ts",".tsx",".mjs",".cjs",".php",".py",".rb",".pl",".jar",".dll",".so",".dylib",
        ".app",".apk",".ipa",".deb",".rpm",".html",".htm",".svg",".xml",".xhtml",
        ".lnk",".scr",".pif",".com",".hta",".sh",".bash",".zsh",".fish" };
    // Whitelist approach — only allow known safe types
    var allowed2 = new HashSet<string> { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".txt", ".md", ".zip", ".mp4", ".mp3", ".wav", ".ogg" };
    if (!allowed2.Contains(ext2))
    {
        using var _db3 = DbHelpers.OpenDb();
        using var _evc2 = _db3.CreateCommand();
        _evc2.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail,  CreatedAt)
            SELECT Id, 'upload_blocked_extension', 'alert', $d,  datetime('now') FROM AuthUsers WHERE Username=$u";
        _evc2.Parameters.AddWithValue("$d", $"Blocked ext={ext2} File={originalName}");
        _evc2.Parameters.AddWithValue("$ip", _ip2); _evc2.Parameters.AddWithValue("$u", u);
        _evc2.ExecuteNonQuery();
        return Results.Json(new { error = $"Filtypen '{ext2}' är inte tillåten." }, statusCode: 422);
    }
    var fn2 = $"{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}{ext2}";
    var path2 = Path.Combine(AppConfig.ChatUploadDir, fn2);
    await using (var s = System.IO.File.Create(path2)) await file.CopyToAsync(s);
    // Log upload
    using (var _db4 = DbHelpers.OpenDb())
    {
        using var _ulc = _db4.CreateCommand();
        _ulc.CommandText = @"INSERT INTO FileUploads (UserId, OriginalName, StoredName, ContentType, FileSizeBytes, Extension, Status,  UploadedAt)
            SELECT Id, $orig, $stored, $ct, $size, $ext, 'ok',  datetime('now') FROM AuthUsers WHERE Username=$u";
        _ulc.Parameters.AddWithValue("$orig", originalName.Length > 255 ? originalName[..255] : originalName);
        _ulc.Parameters.AddWithValue("$stored", fn2); _ulc.Parameters.AddWithValue("$ct", file.ContentType ?? "application/octet-stream");
        _ulc.Parameters.AddWithValue("$size", file.Length); _ulc.Parameters.AddWithValue("$ext", ext2);
        _ulc.Parameters.AddWithValue("$ip", _ip2); _ulc.Parameters.AddWithValue("$u", u);
        _ulc.ExecuteNonQuery();
    }
    return Results.Ok(new { success = true, fileUrl = $"/uploads/chat/{fn2}", fileName = originalName, size = file.Length, ext = ext2 });
}).DisableAntiforgery();


// rs-friend-requests-endpoints-v1
string RsFriendCurrentUser(HttpContext ctx)
{
    return (
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
        ?? ctx.User.Identity?.Name
        ?? ""
    ).Trim().ToLowerInvariant();
}

string RsFriendNormalize(string? value)
{
    return (value ?? "").Trim().TrimStart('@').ToLowerInvariant();
}

bool RsFriendInvalidUsername(string username)
{
    if (string.IsNullOrWhiteSpace(username) || username.Length < 2 || username.Length > 32) return true;

    foreach (var ch in username)
    {
        if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')) return true;
    }

    return false;
}

async Task<System.Collections.Generic.Dictionary<string, string>> RsFriendReadBodyAsync(HttpContext ctx)
{
    var map = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

    if (ctx.Request.ContentLength == 0) return map;

    try
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return map;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            map[prop.Name] = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                ? (prop.Value.GetString() ?? "")
                : prop.Value.ToString();
        }
    }
    catch
    {
    }

    return map;
}

string RsFriendBodyValue(System.Collections.Generic.Dictionary<string, string> body, params string[] names)
{
    foreach (var name in names)
    {
        if (body.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
    }

    return "";
}

string? RsFriendFindCanonicalUser(string username)
{
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Username FROM AuthUsers WHERE lower(Username)=lower($u) LIMIT 1";
    cmd.Parameters.AddWithValue("$u", username);
    var value = cmd.ExecuteScalar();
    return value == null ? null : Convert.ToString(value)?.Trim().ToLowerInvariant();
}

bool RsFriendAreFriends(string a, string b)
{
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT 1
        FROM Friendships
        WHERE
          (lower(UserA)=lower($a) AND lower(UserB)=lower($b))
          OR
          (lower(UserA)=lower($b) AND lower(UserB)=lower($a))
        LIMIT 1";
    cmd.Parameters.AddWithValue("$a", a);
    cmd.Parameters.AddWithValue("$b", b);
    return cmd.ExecuteScalar() != null;
}

bool RsFriendBlockedEitherWay(string a, string b)
{
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT 1
        FROM FriendRequests
        WHERE Status='blocked'
          AND (
            (lower(FromUser)=lower($a) AND lower(ToUser)=lower($b))
            OR
            (lower(FromUser)=lower($b) AND lower(ToUser)=lower($a))
          )
        LIMIT 1";
    cmd.Parameters.AddWithValue("$a", a);
    cmd.Parameters.AddWithValue("$b", b);
    return cmd.ExecuteScalar() != null;
}

app.MapGet("/api/friends", (HttpContext ctx) =>
{
    var me = RsFriendCurrentUser(ctx);
    if (string.IsNullOrWhiteSpace(me)) return Results.Unauthorized();

    var friends = new List<object>();

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT
          CASE WHEN lower(UserA)=lower($me) THEN UserB ELSE UserA END AS Friend,
          CreatedAt
        FROM Friendships
        WHERE lower(UserA)=lower($me) OR lower(UserB)=lower($me)
        ORDER BY CreatedAt DESC
        LIMIT 200";
    cmd.Parameters.AddWithValue("$me", me);

    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        friends.Add(new
        {
            username = r.IsDBNull(0) ? "" : r.GetString(0),
            createdAt = r.IsDBNull(1) ? "" : r.GetString(1)
        });
    }

    return Results.Ok(new { ok = true, friends });
}).RequireAuthorization();

app.MapGet("/api/friends/requests", (HttpContext ctx) =>
{
    var me = RsFriendCurrentUser(ctx);
    if (string.IsNullOrWhiteSpace(me)) return Results.Unauthorized();

    var incoming = new List<object>();
    var outgoing = new List<object>();

    using var db = DbHelpers.OpenDb();

    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT Id, FromUser, Message, CreatedAt
            FROM FriendRequests
            WHERE lower(ToUser)=lower($me) AND Status='pending'
            ORDER BY Id DESC
            LIMIT 100";
        cmd.Parameters.AddWithValue("$me", me);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            incoming.Add(new
            {
                id = r.GetInt64(0),
                from = r.IsDBNull(1) ? "" : r.GetString(1),
                message = r.IsDBNull(2) ? "" : r.GetString(2),
                createdAt = r.IsDBNull(3) ? "" : r.GetString(3)
            });
        }
    }

    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = @"
            SELECT Id, ToUser, Message, CreatedAt
            FROM FriendRequests
            WHERE lower(FromUser)=lower($me) AND Status='pending'
            ORDER BY Id DESC
            LIMIT 100";
        cmd.Parameters.AddWithValue("$me", me);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            outgoing.Add(new
            {
                id = r.GetInt64(0),
                to = r.IsDBNull(1) ? "" : r.GetString(1),
                message = r.IsDBNull(2) ? "" : r.GetString(2),
                createdAt = r.IsDBNull(3) ? "" : r.GetString(3)
            });
        }
    }

    return Results.Ok(new { ok = true, incoming, outgoing });
}).RequireAuthorization();

app.MapPost("/api/friends/request", async (HttpContext ctx) =>
{
    var me = RsFriendCurrentUser(ctx);
    if (string.IsNullOrWhiteSpace(me)) return Results.Unauthorized();

    var body = await RsFriendReadBodyAsync(ctx);
    var targetRaw = RsFriendBodyValue(body, "username", "to", "target");
    var target = RsFriendNormalize(targetRaw);
    var message = RsFriendBodyValue(body, "message", "note");

    if (RsFriendInvalidUsername(target))
        return Results.BadRequest(new { ok = false, code = "invalid_username", message = "Invalid username." });

    var canonical = RsFriendFindCanonicalUser(target);
    if (string.IsNullOrWhiteSpace(canonical))
        return Results.NotFound(new { ok = false, code = "user_not_found", message = "User not found." });

    target = canonical;

    if (target == me)
        return Results.BadRequest(new { ok = false, code = "cannot_add_self", message = "You cannot add yourself." });

    if (RsFriendBlockedEitherWay(me, target))
        return Results.Json(new { ok = false, code = "blocked", message = "Friend request cannot be sent." }, statusCode: StatusCodes.Status403Forbidden);

    if (RsFriendAreFriends(me, target))
        return Results.Ok(new { ok = true, status = "already_friends", username = target });

    using var db = DbHelpers.OpenDb();

    // rs-friend-request-antispam-v1
    var since10m = DateTime.UtcNow.AddMinutes(-10).ToString("o");
    var since24h = DateTime.UtcNow.AddHours(-24).ToString("o");

    using (var rate10 = db.CreateCommand())
    {
        rate10.CommandText = @"
            SELECT COUNT(*)
            FROM FriendRequests
            WHERE lower(FromUser)=lower($me)
              AND CreatedAt >= $since";
        rate10.Parameters.AddWithValue("$me", me);
        rate10.Parameters.AddWithValue("$since", since10m);

        var count = Convert.ToInt32(rate10.ExecuteScalar() ?? 0);
        if (count >= 5)
            return Results.Json(new
            {
                ok = false,
                code = "friend_request_rate_limited",
                message = "Too many friend requests. Please wait before sending more."
            }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    using (var rate24 = db.CreateCommand())
    {
        rate24.CommandText = @"
            SELECT COUNT(*)
            FROM FriendRequests
            WHERE lower(FromUser)=lower($me)
              AND CreatedAt >= $since";
        rate24.Parameters.AddWithValue("$me", me);
        rate24.Parameters.AddWithValue("$since", since24h);

        var count = Convert.ToInt32(rate24.ExecuteScalar() ?? 0);
        if (count >= 30)
            return Results.Json(new
            {
                ok = false,
                code = "friend_request_daily_limit",
                message = "Daily friend request limit reached."
            }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    using (var pendingOut = db.CreateCommand())
    {
        pendingOut.CommandText = @"
            SELECT COUNT(*)
            FROM FriendRequests
            WHERE lower(FromUser)=lower($me)
              AND Status='pending'";
        pendingOut.Parameters.AddWithValue("$me", me);

        var count = Convert.ToInt32(pendingOut.ExecuteScalar() ?? 0);
        if (count >= 50)
            return Results.Json(new
            {
                ok = false,
                code = "too_many_pending_requests",
                message = "You have too many pending friend requests."
            }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    using (var pendingIn = db.CreateCommand())
    {
        pendingIn.CommandText = @"
            SELECT COUNT(*)
            FROM FriendRequests
            WHERE lower(ToUser)=lower($target)
              AND Status='pending'";
        pendingIn.Parameters.AddWithValue("$target", target);

        var count = Convert.ToInt32(pendingIn.ExecuteScalar() ?? 0);
        if (count >= 100)
            return Results.Json(new
            {
                ok = false,
                code = "target_request_queue_full",
                message = "This user cannot receive more friend requests right now."
            }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    using (var cooldown = db.CreateCommand())
    {
        cooldown.CommandText = @"
            SELECT Status
            FROM FriendRequests
            WHERE lower(FromUser)=lower($me)
              AND lower(ToUser)=lower($target)
              AND Status IN ('ignored', 'declined')
              AND UpdatedAt >= $since
            ORDER BY Id DESC
            LIMIT 1";
        cooldown.Parameters.AddWithValue("$me", me);
        cooldown.Parameters.AddWithValue("$target", target);
        cooldown.Parameters.AddWithValue("$since", since24h);

        var status = Convert.ToString(cooldown.ExecuteScalar() ?? "");
        if (!string.IsNullOrWhiteSpace(status))
            return Results.Json(new
            {
                ok = false,
                code = "friend_request_cooldown",
                message = "You must wait before sending another request to this user."
            }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    using (var incoming = db.CreateCommand())
    {
        incoming.CommandText = @"
            SELECT Id
            FROM FriendRequests
            WHERE lower(FromUser)=lower($target)
              AND lower(ToUser)=lower($me)
              AND Status='pending'
            ORDER BY Id DESC
            LIMIT 1";
        incoming.Parameters.AddWithValue("$target", target);
        incoming.Parameters.AddWithValue("$me", me);

        if (incoming.ExecuteScalar() != null)
            return Results.Json(new { ok = false, code = "incoming_request_exists", message = "This user already sent you a friend request." }, statusCode: StatusCodes.Status409Conflict);
    }

    using (var outgoing = db.CreateCommand())
    {
        outgoing.CommandText = @"
            SELECT Id
            FROM FriendRequests
            WHERE lower(FromUser)=lower($me)
              AND lower(ToUser)=lower($target)
              AND Status='pending'
            ORDER BY Id DESC
            LIMIT 1";
        outgoing.Parameters.AddWithValue("$me", me);
        outgoing.Parameters.AddWithValue("$target", target);

        var existing = outgoing.ExecuteScalar();
        if (existing != null)
            return Results.Ok(new { ok = true, status = "pending", requestId = Convert.ToInt64(existing), username = target });
    }

    var now = DateTime.UtcNow.ToString("o");

    using (var ins = db.CreateCommand())
    {
        ins.CommandText = @"
            INSERT INTO FriendRequests (FromUser, ToUser, Status, Message, CreatedAt, UpdatedAt)
            VALUES ($from, $to, 'pending', $message, $now, $now);
            SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$from", me);
        ins.Parameters.AddWithValue("$to", target);
        ins.Parameters.AddWithValue("$message", message.Length > 250 ? message.Substring(0, 250) : message);
        ins.Parameters.AddWithValue("$now", now);

        var id = Convert.ToInt64(ins.ExecuteScalar() ?? 0L);
        return Results.Ok(new { ok = true, status = "pending", requestId = id, username = target });
    }
}).RequireAuthorization();

app.MapPost("/api/friends/accept", async (HttpContext ctx) =>
{
    var me = RsFriendCurrentUser(ctx);
    if (string.IsNullOrWhiteSpace(me)) return Results.Unauthorized();

    var body = await RsFriendReadBodyAsync(ctx);
    var from = RsFriendNormalize(RsFriendBodyValue(body, "username", "from", "target"));

    if (RsFriendInvalidUsername(from))
        return Results.BadRequest(new { ok = false, code = "invalid_username", message = "Invalid username." });

    using var db = DbHelpers.OpenDb();
    using var tx = db.BeginTransaction();

    long requestId = 0;
    using (var get = db.CreateCommand())
    {
        get.Transaction = tx;
        get.CommandText = @"
            SELECT Id
            FROM FriendRequests
            WHERE lower(FromUser)=lower($from)
              AND lower(ToUser)=lower($me)
              AND Status='pending'
            ORDER BY Id DESC
            LIMIT 1";
        get.Parameters.AddWithValue("$from", from);
        get.Parameters.AddWithValue("$me", me);
        var raw = get.ExecuteScalar();
        if (raw == null)
        {
            tx.Rollback();
            return Results.NotFound(new { ok = false, code = "request_not_found", message = "Friend request not found." });
        }
        requestId = Convert.ToInt64(raw);
    }

    var now = DateTime.UtcNow.ToString("o");

    using (var upd = db.CreateCommand())
    {
        upd.Transaction = tx;
        upd.CommandText = "UPDATE FriendRequests SET Status='accepted', UpdatedAt=$now, DecidedAt=$now WHERE Id=$id";
        upd.Parameters.AddWithValue("$now", now);
        upd.Parameters.AddWithValue("$id", requestId);
        upd.ExecuteNonQuery();
    }

    using (var ins = db.CreateCommand())
    {
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT OR IGNORE INTO Friendships (UserA, UserB, CreatedAt, CreatedFromRequestId)
            VALUES ($a, $b, $now, $rid)";
        ins.Parameters.AddWithValue("$a", me);
        ins.Parameters.AddWithValue("$b", from);
        ins.Parameters.AddWithValue("$now", now);
        ins.Parameters.AddWithValue("$rid", requestId);
        ins.ExecuteNonQuery();
    }

    tx.Commit();

    return Results.Ok(new { ok = true, status = "accepted", username = from });
}).RequireAuthorization();

app.MapPost("/api/friends/ignore", async (HttpContext ctx) =>
{
    var me = RsFriendCurrentUser(ctx);
    if (string.IsNullOrWhiteSpace(me)) return Results.Unauthorized();

    var body = await RsFriendReadBodyAsync(ctx);
    var from = RsFriendNormalize(RsFriendBodyValue(body, "username", "from", "target"));

    if (RsFriendInvalidUsername(from))
        return Results.BadRequest(new { ok = false, code = "invalid_username", message = "Invalid username." });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();

    cmd.CommandText = @"
        UPDATE FriendRequests
        SET Status='ignored', UpdatedAt=$now, DecidedAt=$now
        WHERE lower(FromUser)=lower($from)
          AND lower(ToUser)=lower($me)
          AND Status='pending'";
    cmd.Parameters.AddWithValue("$from", from);
    cmd.Parameters.AddWithValue("$me", me);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));

    var rows = cmd.ExecuteNonQuery();

    if (rows == 0)
        return Results.NotFound(new { ok = false, code = "request_not_found", message = "Friend request not found." });

    return Results.Ok(new { ok = true, status = "ignored", username = from });
}).RequireAuthorization();

app.MapPost("/api/friends/decline", async (HttpContext ctx) =>
{
    var me = RsFriendCurrentUser(ctx);
    if (string.IsNullOrWhiteSpace(me)) return Results.Unauthorized();

    var body = await RsFriendReadBodyAsync(ctx);
    var from = RsFriendNormalize(RsFriendBodyValue(body, "username", "from", "target"));

    if (RsFriendInvalidUsername(from))
        return Results.BadRequest(new { ok = false, code = "invalid_username", message = "Invalid username." });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();

    cmd.CommandText = @"
        UPDATE FriendRequests
        SET Status='declined', UpdatedAt=$now, DecidedAt=$now
        WHERE lower(FromUser)=lower($from)
          AND lower(ToUser)=lower($me)
          AND Status='pending'";
    cmd.Parameters.AddWithValue("$from", from);
    cmd.Parameters.AddWithValue("$me", me);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));

    var rows = cmd.ExecuteNonQuery();

    if (rows == 0)
        return Results.NotFound(new { ok = false, code = "request_not_found", message = "Friend request not found." });

    return Results.Ok(new { ok = true, status = "declined", username = from });
}).RequireAuthorization();

app.MapPost("/api/friends/block", async (HttpContext ctx) =>
{
    var me = RsFriendCurrentUser(ctx);
    if (string.IsNullOrWhiteSpace(me)) return Results.Unauthorized();

    var body = await RsFriendReadBodyAsync(ctx);
    var target = RsFriendNormalize(RsFriendBodyValue(body, "username", "target", "user"));

    if (RsFriendInvalidUsername(target))
        return Results.BadRequest(new { ok = false, code = "invalid_username", message = "Invalid username." });

    var canonical = RsFriendFindCanonicalUser(target);
    if (string.IsNullOrWhiteSpace(canonical))
        return Results.NotFound(new { ok = false, code = "user_not_found", message = "User not found." });

    target = canonical;

    if (target == me)
        return Results.BadRequest(new { ok = false, code = "cannot_block_self", message = "You cannot block yourself." });

    using var db = DbHelpers.OpenDb();
    using var tx = db.BeginTransaction();

    var now = DateTime.UtcNow.ToString("o");

    using (var del = db.CreateCommand())
    {
        del.Transaction = tx;
        del.CommandText = @"
            DELETE FROM Friendships
            WHERE
              (lower(UserA)=lower($me) AND lower(UserB)=lower($target))
              OR
              (lower(UserA)=lower($target) AND lower(UserB)=lower($me))";
        del.Parameters.AddWithValue("$me", me);
        del.Parameters.AddWithValue("$target", target);
        del.ExecuteNonQuery();
    }

    using (var upd = db.CreateCommand())
    {
        upd.Transaction = tx;
        upd.CommandText = @"
            UPDATE FriendRequests
            SET Status='blocked', UpdatedAt=$now, DecidedAt=$now
            WHERE
              (lower(FromUser)=lower($me) AND lower(ToUser)=lower($target))
              OR
              (lower(FromUser)=lower($target) AND lower(ToUser)=lower($me))";
        upd.Parameters.AddWithValue("$me", me);
        upd.Parameters.AddWithValue("$target", target);
        upd.Parameters.AddWithValue("$now", now);

        var rows = upd.ExecuteNonQuery();

        if (rows == 0)
        {
            using var ins = db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"
                INSERT INTO FriendRequests (FromUser, ToUser, Status, Message, CreatedAt, UpdatedAt, DecidedAt)
                VALUES ($from, $to, 'blocked', '', $now, $now, $now)";
            ins.Parameters.AddWithValue("$from", me);
            ins.Parameters.AddWithValue("$to", target);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }
    }

    tx.Commit();

    return Results.Ok(new { ok = true, status = "blocked", username = target });
}).RequireAuthorization();


app.MapPost("/api/chat/send", async (HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var from = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(from) || !AppHelpers.UserExists(from)) return Results.Unauthorized();
    string _apiTrustLevel = "medium";
    using (var _apiTrustDb = DbHelpers.OpenDb())
    {
        using var _apiTrustCmd = _apiTrustDb.CreateCommand();
        _apiTrustCmd.CommandText = "SELECT TrustLevel, IsSuspended FROM AuthUsers WHERE Username=$u LIMIT 1";
        _apiTrustCmd.Parameters.AddWithValue("$u", from);
        using var _apiTrustReader = _apiTrustCmd.ExecuteReader();
        if (_apiTrustReader.Read())
        {
            var _apiSuspended = !_apiTrustReader.IsDBNull(1) && _apiTrustReader.GetInt32(1) == 1;
            _apiTrustLevel = _apiSuspended ? "blocked" : (_apiTrustReader.IsDBNull(0) ? "medium" : _apiTrustReader.GetString(0));
        }
    }

    if (_apiTrustLevel == "blocked")
        return Results.Json(new { message = "Din session är blockerad." }, statusCode: 403);

    int _apiRateLimit = _apiTrustLevel == "high" ? 60 : _apiTrustLevel == "medium" ? 30 : 15;

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(from, "chat_send", _apiRateLimit, 60))
    {
        if (_apiTrustLevel == "low")
        {
            using var _apiPenaltyDb = DbHelpers.OpenDb();
            TrustEventService.OnRateLimit(_apiPenaltyDb, from);
        }
        return Results.Json(new { message = "För snabbt.", limit = _apiRateLimit, trustLevel = _apiTrustLevel }, statusCode: 429);
    }
    var req = await ctx.Request.ReadFromJsonAsync<ChatMessageReq>();
    if (req == null) return Results.BadRequest(new { message = "Body saknas" });

    // rs-chat-send-validation-v1
    // Defensive field validation before deeper chat validation/storage.
    if (!DefensiveInput.IsUsername(req.To))
        return Results.BadRequest(new { message = "Ogiltig mottagare." });

    var algorithm = (req.Algorithm ?? "plain").Trim().ToLowerInvariant();
    if (!new[] { "plain", "e2ee", "aes-gcm", "rsa-oaep-aes-gcm" }.Contains(algorithm))
        return Results.BadRequest(new { message = "Ogiltig krypteringsalgoritm." });

    if (req.ReplyToId.HasValue && req.ReplyToId.Value < 0)
        return Results.BadRequest(new { message = "Ogiltigt svar-id." });

    if (!DefensiveInput.IsSafeChatText(req.Text, req.Encrypted == 1 ? 65536 : 4000))
        return Results.BadRequest(new { message = "Ogiltigt meddelande." });

    if (!DefensiveInput.IsSafeBase64ish(req.Iv, 4096) ||
        !DefensiveInput.IsSafeBase64ish(req.EncryptedKey, 65536) ||
        !DefensiveInput.IsSafeBase64ish(req.SenderEncryptedKey, 65536))
        return Results.BadRequest(new { message = "Ogiltig krypteringsmetadata." });

    var v = ChatHelpers.ValidateOutgoingMessage(req, from);
    if (!v.Ok) return Results.Json(new { message = v.Message }, statusCode: v.StatusCode);
    var ts = DateTime.UtcNow.ToString("o");
    long id; using (var db = DbHelpers.OpenDb())
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"INSERT INTO ChatMessages (FromUser,ToUser,Message,Timestamp,Encrypted,Iv,Algorithm,EncryptedKey,SenderEncryptedKey,RecipientKeysJson,SenderKeysJson,ReplyToId) VALUES ($from,$to,$msg,$ts,$enc,$iv,$alg,$ek,$sek,$rk,$sk,$rid); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", v.ToUser); cmd.Parameters.AddWithValue("$msg", v.CipherText); cmd.Parameters.AddWithValue("$ts", ts);
        cmd.Parameters.AddWithValue("$enc", v.Encrypted ? 1 : 0); cmd.Parameters.AddWithValue("$iv", v.Iv); cmd.Parameters.AddWithValue("$alg", v.Algorithm);
        cmd.Parameters.AddWithValue("$ek", v.EncryptedKey); cmd.Parameters.AddWithValue("$sek", v.SenderEncryptedKey);
        cmd.Parameters.AddWithValue("$rk", v.RecipientKeysJson); cmd.Parameters.AddWithValue("$sk", v.SenderKeysJson);
        cmd.Parameters.AddWithValue("$rid", req.ReplyToId ?? 0);
        try
        {
            id = (long)(cmd.ExecuteScalar() ?? 0L);
        }
        catch (Exception ex) when (
            ex.Message.Contains("new_direct_messages_require_friend_request", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("direct_messages_blocked", StringComparison.OrdinalIgnoreCase)
        )
        {
            var blocked = ex.Message.Contains("direct_messages_blocked", StringComparison.OrdinalIgnoreCase);

            return Results.Json(new
            {
                ok = false,
                code = blocked ? "user_blocked" : "friend_request_required",
                message = blocked
                    ? "You cannot message this user."
                    : "Direct messages require an accepted friend request."
            }, statusCode: StatusCodes.Status403Forbidden);
        }
    }
    var payload = ChatHelpers.BuildPayload(id, from, v.ToUser, v.CipherText, ts, v.Encrypted, v.Iv, v.Algorithm, v.EncryptedKey, v.SenderEncryptedKey, v.RecipientKeysJson, v.SenderKeysJson, req.ReplyToId ?? 0);
    await hub.Clients.User(v.ToUser).SendAsync("ReceiveMessage", payload);
    await hub.Clients.User(from).SendAsync("ReceiveMessage", payload);
    return Results.Ok(new { success = true, message = payload });
});


app.MapDelete("/api/chat/message/{id}", async (long id, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    // Kolla att meddelandet tillhör användaren
    using var check = db.CreateCommand();
    check.CommandText = "SELECT FromUser FROM ChatMessages WHERE Id=$id LIMIT 1";
    check.Parameters.AddWithValue("$id", id);
    var owner = check.ExecuteScalar() as string;
    if (owner == null) return Results.NotFound();
    if (!owner.Equals(username, StringComparison.OrdinalIgnoreCase) && !AppHelpers.IsAdmin(username))
        return Results.Forbid();
    using var del = db.CreateCommand();
    del.CommandText = "DELETE FROM ChatMessages WHERE Id=$id";
    del.Parameters.AddWithValue("$id", id);
    del.ExecuteNonQuery();
    AppHelpers.LogActivity(username, "delete_message", $"MsgId={id}");
    return Results.Ok(new { deleted = true });
}).RequireAuthorization();

app.MapGet("/api/chat/history/{peer}", (string peer, HttpContext ctx) =>
{
    var me = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(me) || !AppHelpers.UserExists(me)) return Results.Unauthorized();
    var t = (peer ?? "").Trim().ToLowerInvariant();
    if (!AppHelpers.IsValidUsername(t) || !AppHelpers.UserExists(t)) return Results.BadRequest(new { message = "Ogiltig" });
    return Results.Ok(ChatHelpers.LoadHistory(me, t));
});

app.MapGet("/api/chat/conversations", (HttpContext ctx) =>
{
    var me = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(me) || !AppHelpers.UserExists(me)) return Results.Unauthorized();
    return Results.Ok(ChatHelpers.LoadConversations(me));
});

app.MapPost("/api/chat/execute", async (HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Production")
        return Results.NotFound();

    var me = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(me) || !AppHelpers.UserExists(me)) return Results.Unauthorized();

    var req = await ctx.Request.ReadFromJsonAsync<CodeExecReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Language))
        return Results.BadRequest(new { message = "Kod och språk krävs." });

    var lang = req.Language.Trim().ToLower();
    if (lang != "python" && lang != "javascript")
        return Results.BadRequest(new { message = "Stöds ej. Använd python eller javascript." });

    using var db = DbHelpers.OpenDb();
    using var ts = db.CreateCommand();
    ts.CommandText = "SELECT CreatedAt, TrustScoreOverride FROM AuthUsers WHERE Username=$u LIMIT 1";
    ts.Parameters.AddWithValue("$u", me);
    using var tr = ts.ExecuteReader();
    int trustScore = 70;
    if (tr.Read())
    {
        var created = DateTime.TryParse(tr.IsDBNull(0) ? "" : tr.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var cdt) ? cdt : DateTime.UtcNow;
        int? ov = tr.IsDBNull(1) ? null : tr.GetInt32(1);
        trustScore = TrustScoreService.GetTrustScore(ov, created);
    }
    tr.Close();

    if (trustScore < 70) return Results.Json(new { message = "Trust score för låg för att köra kod." }, statusCode: 403);

    var timeoutSecs = trustScore >= 85 ? 8 : 4;
    var maxOutput = 8192;

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var rateKey = trustScore >= 85 ? "exec_high" : "exec_low";
    var rateMax = trustScore >= 85 ? 10 : 3;
    if (!limiter.IsAllowed(me, rateKey, rateMax, 600))
        return Results.Json(new { message = "För många körningar. Vänta lite." }, statusCode: 429);

    var execId = Guid.NewGuid().ToString("N")[..12];
    var code = req.Code.Length > 16000 ? req.Code[..16000] : req.Code;

    _ = Task.Run(async () =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string stdout = "", stderr = "";
        int exitCode = -1;
        try
        {
            string runner, arg; if (lang == "python") { runner = "python3"; arg = "-c"; } else { runner = "node"; arg = "-e"; }
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "bwrap",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--ro-bind"); psi.ArgumentList.Add("/usr"); psi.ArgumentList.Add("/usr");
            psi.ArgumentList.Add("--ro-bind"); psi.ArgumentList.Add("/lib"); psi.ArgumentList.Add("/lib");
            if (Directory.Exists("/lib64")) { psi.ArgumentList.Add("--ro-bind"); psi.ArgumentList.Add("/lib64"); psi.ArgumentList.Add("/lib64"); }
            psi.ArgumentList.Add("--tmpfs"); psi.ArgumentList.Add("/tmp");
            psi.ArgumentList.Add("--proc"); psi.ArgumentList.Add("/proc");
            psi.ArgumentList.Add("--dev"); psi.ArgumentList.Add("/dev");
            psi.ArgumentList.Add("--unshare-all");
            psi.ArgumentList.Add("--die-with-parent");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(runner);
            psi.ArgumentList.Add(arg == $"-c {System.Diagnostics.Process.GetCurrentProcess().Id}" ? "-c" : arg);
            psi.ArgumentList.Add(code);

            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSecs));
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(cts.Token);
            stdout = await outTask;
            stderr = await errTask;
            exitCode = proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            stderr = $"Timeout: processen avbröts efter {timeoutSecs}s.";
            exitCode = 124;
        }
        catch (Exception ex)
        {
            stderr = "Internt fel.";
            exitCode = 1;
        }
        sw.Stop();
        if (stdout.Length > maxOutput) stdout = stdout[..maxOutput] + "\n[output trunkerad]";
        if (stderr.Length > maxOutput) stderr = stderr[..maxOutput] + "\n[output trunkerad]";
        await hub.Clients.User(me).SendAsync("ExecutionResult", new
        {
            execId,
            messageId = req.MessageId,
            stdout,
            stderr,
            exitCode,
            durationMs = (int)sw.ElapsedMilliseconds,
            language = lang,
        });
    });

    return Results.Ok(new { execId, status = "running" });
});


// ═══════════════════════════════════════════════
// GROUP ENDPOINTS
// ═══════════════════════════════════════════════

app.MapPost("/api/groups", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "create_group", 5, 3600)) return Results.Json(new { message = "Vänta." }, statusCode: 429);
    var req = await ctx.Request.ReadFromJsonAsync<CreateGroupReq>();
    var name = InputSanitizer.SanitizeInput(req?.Name ?? "", 32).Trim();
    if (name.Length < 2) return Results.BadRequest(new { message = "Namn krävs (minst 2 tecken)." });
    if (ContentFilter.IsOffensive(name)) return Results.BadRequest(new { message = "Otillåtet namn." });
    var desc = InputSanitizer.SanitizeInput(req?.Description ?? "", 200);
    var gid = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    var now = DateTime.UtcNow.ToString("o");
    using var db = DbHelpers.OpenDb();
    using var ins = db.CreateCommand();
    ins.CommandText = "INSERT INTO Groups (GroupId,Name,Description,OwnerUsername,CreatedAt) VALUES ($gid,$n,$d,$o,$t)";
    ins.Parameters.AddWithValue("$gid", gid); ins.Parameters.AddWithValue("$n", name); ins.Parameters.AddWithValue("$d", desc);
    ins.Parameters.AddWithValue("$o", u); ins.Parameters.AddWithValue("$t", now); ins.ExecuteNonQuery();
    using var mem = db.CreateCommand();
    mem.CommandText = "INSERT INTO GroupMembers (GroupId,Username,Role,JoinedAt) VALUES ($gid,$u,'owner',$t)";
    mem.Parameters.AddWithValue("$gid", gid); mem.Parameters.AddWithValue("$u", u); mem.Parameters.AddWithValue("$t", now); mem.ExecuteNonQuery();

    // Auto-create default roles for new group
    var defaultRoles = new[] {
        ("owner", "Owner", "#eab308", 0, "{\"all\":true}"),
        ("admin", "Admin", "#f97316", 1, "{\"manage_members\":true,\"manage_channels\":true,\"manage_roles\":true,\"kick\":true,\"invite\":true,\"send_messages\":true,\"read_messages\":true}"),
        ("moderator", "Moderator", "#3b82f6", 2, "{\"kick\":true,\"send_messages\":true,\"read_messages\":true,\"pin_messages\":true}"),
        ("member", "Member", "#94a3b8", 99, "{\"send_messages\":true,\"read_messages\":true}")
    };
    foreach (var (rid, rname, rcolor, rpos, rperm) in defaultRoles)
    {
        using var rc = db.CreateCommand();
        rc.CommandText = "INSERT OR IGNORE INTO GroupRoles (GroupId,RoleId,Name,Color,Position,Permissions,IsDefault,CreatedAt) VALUES ($gid,$rid,$n,$c,$p,$perm,$def,$t)";
        rc.Parameters.AddWithValue("$gid", gid);
        rc.Parameters.AddWithValue("$rid", rid);
        rc.Parameters.AddWithValue("$n", rname);
        rc.Parameters.AddWithValue("$c", rcolor);
        rc.Parameters.AddWithValue("$p", rpos);
        rc.Parameters.AddWithValue("$perm", rperm);
        rc.Parameters.AddWithValue("$def", rid == "member" ? 1 : 0);
        rc.Parameters.AddWithValue("$t", now);
        rc.ExecuteNonQuery();
    }
    // Default channels
    ServerDb.EnsureDefaultRoles(gid, u);
    foreach (var ch in new[] { ("allmänt", "text") })
    {
        using var chCmd = db.CreateCommand();
        chCmd.CommandText = "INSERT INTO GroupChannels (GroupId,ChannelId,Name,Type,CreatedAt) VALUES ($gid,$cid,$cn,$ct,$t)";
        chCmd.Parameters.AddWithValue("$gid", gid); chCmd.Parameters.AddWithValue("$cid", Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        chCmd.Parameters.AddWithValue("$cn", ch.Item1); chCmd.Parameters.AddWithValue("$ct", ch.Item2); chCmd.Parameters.AddWithValue("$t", now); chCmd.ExecuteNonQuery();
    }
    return Results.Ok(new { success = true, groupId = gid, name });
});

app.MapGet("/api/groups", (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT g.GroupId,g.Name,g.Description,g.OwnerUsername,g.CreatedAt FROM Groups g INNER JOIN GroupMembers m ON g.GroupId=m.GroupId WHERE m.Username=$u ORDER BY g.Name";
    cmd.Parameters.AddWithValue("$u", u);
    var list = new List<object>(); using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { groupId = r.GetString(0), name = r.GetString(1), description = r.IsDBNull(2) ? "" : r.GetString(2), owner = r.GetString(3), createdAt = r.GetString(4) });
    return Results.Ok(list);
});

app.MapGet("/api/groups/{groupId}", (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsMember(gid, u)) return Results.Forbid();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT GroupId,Name,Description,OwnerUsername,CreatedAt FROM Groups WHERE GroupId=$gid LIMIT 1"; cmd.Parameters.AddWithValue("$gid", gid);
    using var r = cmd.ExecuteReader(); if (!r.Read()) return Results.NotFound(new { message = "Grupp ej hittad." });
    return Results.Ok(new { groupId = r.GetString(0), name = r.GetString(1), description = r.IsDBNull(2) ? "" : r.GetString(2), owner = r.GetString(3), createdAt = r.GetString(4), channels = GroupHelpers.GetChannels(gid), members = GroupHelpers.GetMembersWithRoles(gid), roles = GroupHelpers.GetGroupRoles(gid) });
});

app.MapDelete("/api/groups/{groupId}", (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwner(gid, u)) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    foreach (var sql in new[] { "DELETE FROM GroupMessages WHERE GroupId=$g", "DELETE FROM GroupChannels WHERE GroupId=$g", "DELETE FROM GroupInvites WHERE GroupId=$g", "DELETE FROM GroupMembers WHERE GroupId=$g", "DELETE FROM Groups WHERE GroupId=$g" })
    { using var c = db.CreateCommand(); c.CommandText = sql; c.Parameters.AddWithValue("$g", gid); c.ExecuteNonQuery(); }
    return Results.Ok(new { success = true });
});

app.MapPost("/api/groups/{groupId}/channels", async (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwnerOrAdmin(gid, u)) return Results.Forbid();
    var req = await ctx.Request.ReadFromJsonAsync<CreateChannelReq>();
    var name = InputSanitizer.SanitizeInput(req?.Name ?? "", 32).Trim().ToLowerInvariant();
    if (name.Length < 2) return Results.BadRequest(new { message = "Namn krävs." });
    var type = (req?.Type ?? "text").Trim().ToLowerInvariant();
    if (type != "text") return Results.BadRequest(new { message = "Typ måste vara text." });
    var cid = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO GroupChannels (GroupId,ChannelId,Name,Type,CreatedAt) VALUES ($gid,$cid,$n,$t,$ts)";
    cmd.Parameters.AddWithValue("$gid", gid); cmd.Parameters.AddWithValue("$cid", cid); cmd.Parameters.AddWithValue("$n", name);
    cmd.Parameters.AddWithValue("$t", type); cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o")); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true, channelId = cid, name, type });
});

app.MapDelete("/api/groups/{groupId}/channels/{channelId}", (string groupId, string channelId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    if (!GroupHelpers.IsOwnerOrAdmin((groupId ?? "").Trim().ToLowerInvariant(), u)) return Results.Forbid();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM GroupChannels WHERE GroupId=$gid AND ChannelId=$cid";
    cmd.Parameters.AddWithValue("$gid", (groupId ?? "").Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$cid", (channelId ?? "").Trim().ToLowerInvariant()); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

app.MapPost("/api/groups/{groupId}/invite", async (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsMember(gid, u)) return Results.Forbid();
    var req = await ctx.Request.ReadFromJsonAsync<InviteReq>();
    var target = (req?.Username ?? "").Trim().ToLowerInvariant();
    if (!AppHelpers.IsValidUsername(target) || !AppHelpers.UserExists(target)) return Results.BadRequest(new { message = "Användaren finns inte." });
    if (GroupHelpers.IsMember(gid, target)) return Results.BadRequest(new { message = "Redan medlem." });
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT OR IGNORE INTO GroupInvites (GroupId,InvitedBy,InvitedUser,CreatedAt) VALUES ($gid,$by,$to,$t)";
    cmd.Parameters.AddWithValue("$gid", gid); cmd.Parameters.AddWithValue("$by", u); cmd.Parameters.AddWithValue("$to", target);
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o")); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

app.MapGet("/api/groups/invites/mine", (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT i.Id,i.GroupId,g.Name,i.InvitedBy,i.CreatedAt FROM GroupInvites i INNER JOIN Groups g ON i.GroupId=g.GroupId WHERE i.InvitedUser=$u ORDER BY i.Id DESC LIMIT 50";
    cmd.Parameters.AddWithValue("$u", u);
    var list = new List<object>(); using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { id = r.GetInt64(0), groupId = r.GetString(1), groupName = r.GetString(2), invitedBy = r.GetString(3), createdAt = r.GetString(4) });
    return Results.Ok(list);
});

app.MapPost("/api/groups/invites/{inviteId}/accept", (long inviteId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb(); using var get = db.CreateCommand();
    get.CommandText = "SELECT GroupId FROM GroupInvites WHERE Id=$id AND InvitedUser=$u LIMIT 1"; get.Parameters.AddWithValue("$id", inviteId); get.Parameters.AddWithValue("$u", u);
    var gid = get.ExecuteScalar() as string;
    if (string.IsNullOrWhiteSpace(gid)) return Results.NotFound(new { message = "Inbjudan ej hittad." });
    using var mem = db.CreateCommand();
    mem.CommandText = "INSERT OR IGNORE INTO GroupMembers (GroupId,Username,Role,JoinedAt) VALUES ($gid,$u,'member',$t)";
    mem.Parameters.AddWithValue("$gid", gid); mem.Parameters.AddWithValue("$u", u); mem.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o")); mem.ExecuteNonQuery();
    using var del = db.CreateCommand(); del.CommandText = "DELETE FROM GroupInvites WHERE Id=$id"; del.Parameters.AddWithValue("$id", inviteId); del.ExecuteNonQuery();
    return Results.Ok(new { success = true, groupId = gid });
});

app.MapPost("/api/groups/invites/{inviteId}/decline", (long inviteId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM GroupInvites WHERE Id=$id AND InvitedUser=$u"; cmd.Parameters.AddWithValue("$id", inviteId); cmd.Parameters.AddWithValue("$u", u); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

app.MapPost("/api/groups/{groupId}/kick/{target}", (string groupId, string target, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    var t = (target ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwnerOrAdmin(gid, u)) return Results.Forbid();
    if (GroupHelpers.IsOwner(gid, t)) return Results.BadRequest(new { message = "Kan inte sparka ägaren." });
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM GroupMembers WHERE GroupId=$gid AND Username=$t"; cmd.Parameters.AddWithValue("$gid", gid); cmd.Parameters.AddWithValue("$t", t); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

app.MapPost("/api/groups/{groupId}/leave", (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (GroupHelpers.IsOwner(gid, u)) return Results.BadRequest(new { message = "Ägaren kan inte lämna. Ta bort gruppen istället." });
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM GroupMembers WHERE GroupId=$gid AND Username=$u"; cmd.Parameters.AddWithValue("$gid", gid); cmd.Parameters.AddWithValue("$u", u); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

app.MapPost("/api/groups/{groupId}/channels/{channelId}/send", async (string groupId, string channelId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    var cid = (channelId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsMember(gid, u)) return Results.Forbid();
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "group_send", 60, 60)) return Results.Json(new { message = "För snabbt." }, statusCode: 429);
    var req = await ctx.Request.ReadFromJsonAsync<GroupMessageReq>();
    var text = InputSanitizer.SanitizeInput(req?.Text ?? "", 2000).Trim();
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { message = "Tomt meddelande." });
    var ts = DateTime.UtcNow.ToString("o");
    long id; using (var db = DbHelpers.OpenDb())
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO GroupMessages (GroupId,ChannelId,FromUser,Message,Timestamp) VALUES ($gid,$cid,$u,$m,$t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$gid", gid); cmd.Parameters.AddWithValue("$cid", cid); cmd.Parameters.AddWithValue("$u", u);
        cmd.Parameters.AddWithValue("$m", text); cmd.Parameters.AddWithValue("$t", ts); id = (long)(cmd.ExecuteScalar() ?? 0L);
    }
    var payload = new { id, groupId = gid, channelId = cid, from = u, text, ts };
    await hub.Clients.Group($"group:{gid}:{cid}").SendAsync("ReceiveGroupMessage", payload);
    return Results.Ok(new { success = true, message = payload });
});

app.MapGet("/api/groups/{groupId}/channels/{channelId}/history", (string groupId, string channelId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    var cid = (channelId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsMember(gid, u)) return Results.Forbid();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Id,FromUser,Message,Timestamp FROM GroupMessages WHERE GroupId=$gid AND ChannelId=$cid ORDER BY Id ASC LIMIT 500";
    cmd.Parameters.AddWithValue("$gid", gid); cmd.Parameters.AddWithValue("$cid", cid);
    var list = new List<object>(); using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { id = r.GetInt64(0), from = r.GetString(1), text = r.GetString(2), ts = r.GetString(3) });
    return Results.Ok(list);
});

app.MapPatch("/api/groups/{groupId}", async (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwner(gid, u)) return Results.Forbid();
    var req = await ctx.Request.ReadFromJsonAsync<UpdateGroupReq>();
    using var db = DbHelpers.OpenDb();
    if (!string.IsNullOrWhiteSpace(req?.Name)) { var n = InputSanitizer.SanitizeInput(req.Name, 32); using var c = db.CreateCommand(); c.CommandText = "UPDATE Groups SET Name=$n WHERE GroupId=$g"; c.Parameters.AddWithValue("$n", n); c.Parameters.AddWithValue("$g", gid); c.ExecuteNonQuery(); }
    if (req?.Description != null) { var d = InputSanitizer.SanitizeInput(req.Description, 200); using var c = db.CreateCommand(); c.CommandText = "UPDATE Groups SET Description=$d WHERE GroupId=$g"; c.Parameters.AddWithValue("$d", d); c.Parameters.AddWithValue("$g", gid); c.ExecuteNonQuery(); }
    return Results.Ok(new { success = true });
});

// ═══════════════════════════════════════════════
// ADMIN
// ═══════════════════════════════════════════════
app.MapPost("/api/admin/alert", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    var hub = ctx.RequestServices.GetRequiredService<IHubContext<ChatHub>>();
    using var db = DbHelpers.OpenDb();
    var adminList2 = new List<string>(); using (var _admc2 = db.CreateCommand()) { _admc2.CommandText = "SELECT Username FROM AuthUsers WHERE IsAdmin=1"; using var _admr2 = _admc2.ExecuteReader(); while (_admr2.Read()) adminList2.Add(_admr2.GetString(0)); }
    var alertText = $"[ADMIN ALERT] {u} used /admin at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
    foreach (var admin in adminList2)
    {
        if (admin == u) continue;
        var ts = DateTime.UtcNow.ToString("o");
        using var ins = db.CreateCommand();
        ins.CommandText = "INSERT INTO ChatMessages (FromUser,ToUser,Message,Timestamp,Encrypted,Iv,Algorithm,EncryptedKey,SenderEncryptedKey,RecipientKeysJson,SenderKeysJson,ReplyToId) VALUES ('system',$to,$msg,$ts,0,'','plain','','','[]','[]',0); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$to", admin);
        ins.Parameters.AddWithValue("$msg", alertText);
        ins.Parameters.AddWithValue("$ts", ts);
        var msgId = (long)(ins.ExecuteScalar() ?? 0L);
        var payload = ChatHelpers.BuildPayload(msgId, "system", admin, alertText, ts, false, "", "plain", "", "", "[]", "[]", 0);
        await hub.Clients.User(admin).SendAsync("ReceiveMessage", payload);
    }
    return Results.Ok(new { success = true });
});

app.MapPost("/api/admin/ban/{target}", (string target, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(u)) return Results.Forbid();
    var t = (target ?? "").Trim().ToLowerInvariant();
    if (!AppHelpers.UserExists(t) || AppHelpers.IsAdmin(t)) return Results.BadRequest(new { message = "Kan inte." });
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET Status='banned' WHERE Username=$u"; cmd.Parameters.AddWithValue("$u", t); cmd.ExecuteNonQuery();
    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(t);
    return Results.Ok(new { success = true });
});

app.MapPost("/api/admin/unban/{target}", (string target, HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET Status='verified' WHERE Username=$u"; cmd.Parameters.AddWithValue("$u", (target ?? "").Trim().ToLowerInvariant()); cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

app.MapPost("/api/admin/kill-switch/{action}", (string action, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(u)) return Results.Forbid();
    var ks = ctx.RequestServices.GetRequiredService<EmergencyKillSwitch>();
    if (action == "activate")
    {
        ks.Activate(u!); return Results.Ok(new { success = true }

);
    }
    if (action == "deactivate") { ks.Deactivate(u!); return Results.Ok(new { success = true }); }
    return Results.BadRequest(new { message = "activate/deactivate" });
});

app.MapPost("/api/admin/force-logout-all", (HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateEverything();
    return Results.Ok(new { success = true });
});

app.MapGet("/api/admin/dashboard", (HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    long total = 0, banned = 0;
    using (var c = db.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM AuthUsers"; total = (long)(c.ExecuteScalar() ?? 0L); }
    using (var c = db.CreateCommand()) { c.CommandText = "SELECT COUNT(*) FROM AuthUsers WHERE Status='banned'"; banned = (long)(c.ExecuteScalar() ?? 0L); }
    return Results.Ok(new { killSwitchActive = ctx.RequestServices.GetRequiredService<EmergencyKillSwitch>().IsActive, totalUsers = total, bannedUsers = banned, totalActiveSessions = ctx.RequestServices.GetRequiredService<SessionManager>().GetTotalSessionCount() });
});


// ═══════════════════════════════════════════════
// NEWS API
// ═══════════════════════════════════════════════

app.MapGet("/api/news", () =>
{
    var rows = new List<object>();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Id,Author,Tag,TitleSv,TitleEn,TitleRu,TitleFr,BodySv,BodyEn,BodyRu,BodyFr,CreatedAt,COALESCE(ImageUrls,'[]') FROM NewsArticles ORDER BY Id DESC LIMIT 50";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var imgJson = r.IsDBNull(12) ? "[]" : r.GetString(12);
        List<string> imgs;
        try { imgs = System.Text.Json.JsonSerializer.Deserialize<List<string>>(imgJson) ?? new List<string>(); }
        catch { imgs = new List<string>(); }
        rows.Add(new
        {
            id = r.GetInt64(0),
            author = r.GetString(1),
            tag = r.GetString(2),
            titleSv = r.GetString(3),
            titleEn = r.IsDBNull(4) ? "" : r.GetString(4),
            titleRu = r.IsDBNull(5) ? "" : r.GetString(5),
            titleFr = r.IsDBNull(6) ? "" : r.GetString(6),
            bodySv = r.GetString(7),
            bodyEn = r.IsDBNull(8) ? "" : r.GetString(8),
            bodyRu = r.IsDBNull(9) ? "" : r.GetString(9),
            bodyFr = r.IsDBNull(10) ? "" : r.GetString(10),
            createdAt = r.IsDBNull(11) ? "" : r.GetString(11),
            imageUrls = imgs
        });
    }
    return Results.Ok(rows);
});

app.MapPost("/api/news", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !AppHelpers.IsAdmin(username))
        return Results.Json(new { message = "Forbidden" }, statusCode: 403);

    var req = await ctx.Request.ReadFromJsonAsync<NewsCreateReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.TitleSv) || string.IsNullOrWhiteSpace(req.BodySv))
        return Results.BadRequest(new { message = "TitleSv and BodySv required" });

    var tag = (req.Tag ?? "update").Trim().ToLowerInvariant();
    var allowed = new[] { "launch", "update", "security", "fix", "improvement" };
    if (!allowed.Contains(tag)) tag = "update";

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"INSERT INTO NewsArticles (Author,Tag,TitleSv,TitleEn,TitleRu,TitleFr,BodySv,BodyEn,BodyRu,BodyFr,CreatedAt,ImageUrls)
        VALUES ($author,$tag,$tsv,$ten,$tru,$tfr,$bsv,$ben,$bru,$bfr,$ts,$imgs)";
    cmd.Parameters.AddWithValue("$author", username);
    cmd.Parameters.AddWithValue("$tag", tag);
    cmd.Parameters.AddWithValue("$tsv", InputSanitizer.SanitizeInput(req.TitleSv, 200));
    cmd.Parameters.AddWithValue("$ten", InputSanitizer.SanitizeInput(req.TitleEn ?? "", 200));
    cmd.Parameters.AddWithValue("$tru", InputSanitizer.SanitizeInput(req.TitleRu ?? "", 200));
    cmd.Parameters.AddWithValue("$tfr", InputSanitizer.SanitizeInput(req.TitleFr ?? "", 200));
    cmd.Parameters.AddWithValue("$bsv", InputSanitizer.SanitizeInput(req.BodySv, 2000));
    cmd.Parameters.AddWithValue("$ben", InputSanitizer.SanitizeInput(req.BodyEn ?? "", 2000));
    cmd.Parameters.AddWithValue("$bru", InputSanitizer.SanitizeInput(req.BodyRu ?? "", 2000));
    cmd.Parameters.AddWithValue("$bfr", InputSanitizer.SanitizeInput(req.BodyFr ?? "", 2000));
    cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
    var safeImgs = (req.ImageUrls ?? new List<string>())
        .Where(u => !string.IsNullOrWhiteSpace(u) && u.StartsWith("/uploads/news/"))
        .Take(10).ToList();
    cmd.Parameters.AddWithValue("$imgs", System.Text.Json.JsonSerializer.Serialize(safeImgs));
    cmd.ExecuteNonQuery();

    AppHelpers.LogActivity(username, "news_create", req.TitleSv);
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/news/{id}", (long id, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !AppHelpers.IsAdmin(username))
        return Results.Json(new { message = "Forbidden" }, statusCode: 403);

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM NewsArticles WHERE Id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    var affected = cmd.ExecuteNonQuery();

    AppHelpers.LogActivity(username, "news_delete", $"id={id}");
    return affected > 0 ? Results.Ok(new { success = true }) : Results.NotFound(new { message = "Not found" });
});

app.MapPost("/api/news/image", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !AppHelpers.IsAdmin(username))
        return Results.Json(new { error = "Forbidden" }, statusCode: 403);
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "news_image_upload", 30, 300))
        return Results.Json(new { error = "Rate limit." }, statusCode: 429);
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "form-data krävs" });
    var form = await ctx.Request.ReadFormAsync(); var file = form.Files["image"];
    if (file == null || file.Length == 0 || file.Length > 8 * 1024 * 1024)
        return Results.BadRequest(new { error = "Fil saknas eller för stor (max 8MB)." });

    // rs-upload-filename-defensive-v2-news
    var originalName = DefensiveInput.SafeFileName(file.FileName);
    var ext = Path.GetExtension(originalName).ToLowerInvariant();
    if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(ext))
        return Results.BadRequest(new { error = "Ogiltigt format" });
    if (!await FileValidator.IsValidImageAsync(file))
        return Results.BadRequest(new { error = "Ej giltig bild." });
    var newsDir = Path.Combine(AppConfig.AvatarUploadDir, "..", "news");
    Directory.CreateDirectory(newsDir);
    var fn = $"news_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}{ext}";
    var savePath = Path.Combine(newsDir, fn);
    await using (var s = System.IO.File.Create(savePath)) await file.CopyToAsync(s);
    var url = $"/uploads/news/{fn}";
    return Results.Ok(new { success = true, url = url });
}).DisableAntiforgery();

// ═══════════════════════════════════════════════
// KEY LINK / DEVICE SYNC API
// ═══════════════════════════════════════════════

// New device creates a link request with a 6-digit code

app.MapPost("/api/chat/link-request", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    // rs-link-request-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "chat_link_request", 10, 900))
        return Results.Json(new { message = "Rate limit." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<LinkRequestReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.DeviceId))
        return Results.BadRequest(new { message = "DeviceId required." });

    var did = DefensiveInput.CleanString(req.DeviceId, 100);
    if (!DefensiveInput.IsSafeDeviceId(did))
        return Results.BadRequest(new { message = "Invalid device id." });

    var deviceName = string.IsNullOrWhiteSpace(req.DeviceName)
        ? "Unknown"
        : DefensiveInput.CleanString(req.DeviceName, 80);

    if (deviceName.Length == 0)
        deviceName = "Unknown";

    var code = RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    var now = DateTime.UtcNow;
    var expires = now.AddMinutes(5);

    using var db = DbHelpers.OpenDb();

    using (var del = db.CreateCommand())
    {
        del.CommandText = "DELETE FROM KeyLinkRequests WHERE ExpiresAt < $now OR (Username = $u AND RequestingDeviceId = $did)";
        del.Parameters.AddWithValue("$now", now.ToString("o"));
        del.Parameters.AddWithValue("$u", username);
        del.Parameters.AddWithValue("$did", did);
        del.ExecuteNonQuery();
    }

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"INSERT INTO KeyLinkRequests
        (Username, RequestingDeviceId, RequestingDeviceName, LinkCode, Status, CreatedAt, ExpiresAt)
        VALUES ($u, $did, $dn, $code, 'pending', $c, $e)";

    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$did", did);
    cmd.Parameters.AddWithValue("$dn", deviceName);
    cmd.Parameters.AddWithValue("$code", code);
    cmd.Parameters.AddWithValue("$c", now.ToString("o"));
    cmd.Parameters.AddWithValue("$e", expires.ToString("o"));
    cmd.ExecuteNonQuery();

    var didPreview = did.Length > 12 ? did.Substring(0, 12) + "..." : did;
    AppHelpers.LogActivity(username, "link_request", "device=" + didPreview);

    return Results.Ok(new { code, expiresAt = expires.ToString("o") });
});

app.MapGet("/api/chat/link-requests", (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();

    var now = DateTime.UtcNow.ToString("o");
    using var db = DbHelpers.OpenDb();

    // Clean expired
    using (var del = db.CreateCommand())
    {
        del.CommandText = "DELETE FROM KeyLinkRequests WHERE ExpiresAt < $now";
        del.Parameters.AddWithValue("$now", now);
        del.ExecuteNonQuery();
    }

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Id,RequestingDeviceId,RequestingDeviceName,LinkCode,CreatedAt,ExpiresAt FROM KeyLinkRequests WHERE Username=$u AND Status='pending' ORDER BY Id DESC LIMIT 10";
    cmd.Parameters.AddWithValue("$u", username);
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read())
    {
        rows.Add(new
        {
            id = r.GetInt64(0),
            deviceId = r.GetString(1),
            deviceName = r.IsDBNull(2) ? "" : r.GetString(2),
            code = r.GetString(3),
            createdAt = r.IsDBNull(4) ? "" : r.GetString(4),
            expiresAt = r.IsDBNull(5) ? "" : r.GetString(5)
        });
    }
    return Results.Ok(rows);
});

// Existing device approves a link request: sends encrypted private key
app.MapPost("/api/chat/link-approve", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();

    // rs-link-approve-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "chat_link_approve", 20, 900))
        return Results.Json(new { message = "Rate limit." }, statusCode: 429);

    var req = await ctx.Request.ReadFromJsonAsync<LinkApproveReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.EncryptedPrivateKey))
        return Results.BadRequest(new { message = "Code and EncryptedPrivateKey required" });

    var code = DefensiveInput.CleanString(req.Code, 8);
    if (!DefensiveInput.IsOtpCode(code))
        return Results.BadRequest(new { message = "Invalid code." });

    if (!DefensiveInput.IsSafeBase64ish(req.EncryptedPrivateKey, 65536))
        return Results.BadRequest(new { message = "Invalid encrypted private key." });

    var encryptedPrivateKey = req.EncryptedPrivateKey.Trim();

    using var db = DbHelpers.OpenDb();

    // Find the pending request with this code
    using var find = db.CreateCommand();
    find.CommandText = "SELECT Id,RequestingDeviceId FROM KeyLinkRequests WHERE Username=$u AND LinkCode=$code AND Status='pending' AND ExpiresAt > $now LIMIT 1";
    find.Parameters.AddWithValue("$u", username);
    find.Parameters.AddWithValue("$code", code);
    find.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    using var r = find.ExecuteReader();

    if (!r.Read())
        return Results.BadRequest(new { message = "Invalid or expired code" });

    var requestId = r.GetInt64(0);
    var deviceId = r.GetString(1);
    r.Close();

    // Update the request with the encrypted private key
    using var upd = db.CreateCommand();
    upd.CommandText = "UPDATE KeyLinkRequests SET EncryptedPrivateKey=$epk, Status='approved' WHERE Id=$id";
    upd.Parameters.AddWithValue("$epk", encryptedPrivateKey);
    upd.Parameters.AddWithValue("$id", requestId);
    upd.ExecuteNonQuery();

    var did = DefensiveInput.CleanString(deviceId, 100);
    var didPreview = did.Length > 12 ? did.Substring(0, 12) + "..." : did;
    AppHelpers.LogActivity(username, "link_approve", "device=" + didPreview);
    return Results.Ok(new { success = true, deviceId });
});

// New device polls for approved link (gets the encrypted private key)

app.MapGet("/api/chat/link-status/{code}", (string code, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    // rs-link-status-defensive-v1
    var safeCode = DefensiveInput.CleanString(code, 8);
    if (!DefensiveInput.IsOtpCode(safeCode))
        return Results.BadRequest(new { message = "Invalid code." });

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "chat_link_status", 60, 300))
        return Results.Json(new { message = "Rate limit." }, statusCode: 429);

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();

    cmd.CommandText = "SELECT Status,EncryptedPrivateKey FROM KeyLinkRequests WHERE Username=$u AND LinkCode=$code LIMIT 1";
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$code", safeCode);

    using var r = cmd.ExecuteReader();
    if (!r.Read())
        return Results.Ok(new { status = "pending", encryptedPrivateKey = "" });

    var status = r.IsDBNull(0) ? "pending" : r.GetString(0);
    var epk = r.IsDBNull(1) ? "" : r.GetString(1);
    r.Close();

    if (status == "approved" && !string.IsNullOrWhiteSpace(epk))
    {
        using var del = db.CreateCommand();
        del.CommandText = "DELETE FROM KeyLinkRequests WHERE Username=$u AND LinkCode=$code";
        del.Parameters.AddWithValue("$u", username);
        del.Parameters.AddWithValue("$code", safeCode);
        del.ExecuteNonQuery();

        return Results.Ok(new { status = "approved", encryptedPrivateKey = epk });
    }

    return Results.Ok(new { status, encryptedPrivateKey = "" });
});

app.MapPost("/api/groups/{groupId}/members/{target}/role", async (string groupId, string target, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    var gid = groupId.Trim();
    var t = target.Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwnerOrAdmin(gid, username))
        return Results.Json(new { message = "Forbidden" }, statusCode: 403);
    if (GroupHelpers.IsOwner(gid, t))
        return Results.BadRequest(new { message = "Cannot change owner role" });
    var req = await ctx.Request.ReadFromJsonAsync<ChangeRoleReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Role))
        return Results.BadRequest(new { message = "Role required" });
    var role = req.Role.Trim().ToLowerInvariant();
    var allowed = new[] { "member", "admin", "moderator" };
    if (!allowed.Contains(role))
        return Results.BadRequest(new { message = "Invalid role. Allowed: member, admin, moderator" });
    if (role == "admin" && !GroupHelpers.IsOwner(gid, username))
        return Results.Json(new { message = "Only owner can promote to admin" }, statusCode: 403);
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE GroupMembers SET Role=$r WHERE GroupId=$gid AND Username=$t";
    cmd.Parameters.AddWithValue("$r", role);
    cmd.Parameters.AddWithValue("$gid", gid);
    cmd.Parameters.AddWithValue("$t", t);
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) return Results.NotFound(new { message = "Member not found" });
    AppHelpers.LogActivity(username, "group_role_change", gid + ": " + t + " -> " + role);
    return Results.Ok(new { success = true, username = t, role });
});


// ═══════════════════════════════════════════════
// GROUP ROLES API
// ═══════════════════════════════════════════════

app.MapGet("/api/groups/{groupId}/roles", (string groupId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    var gid = groupId.Trim();
    if (!GroupHelpers.IsMember(gid, username)) return Results.Json(new { message = "Not a member" }, statusCode: 403);
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT RoleId,Name,Color,Position,Permissions,IsDefault FROM GroupRoles WHERE GroupId=$gid ORDER BY Position ASC";
    cmd.Parameters.AddWithValue("$gid", gid);
    using var r = cmd.ExecuteReader();
    var roles = new List<object>();
    while (r.Read())
    {
        roles.Add(new
        {
            roleId = r.GetString(0),
            name = r.GetString(1),
            color = r.GetString(2),
            position = r.GetInt32(3),
            permissions = r.IsDBNull(4) ? "{}" : r.GetString(4),
            isDefault = r.GetInt32(5) == 1
        });
    }
    return Results.Ok(roles);
});

app.MapPost("/api/groups/{groupId}/roles", async (string groupId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    var gid = groupId.Trim();
    if (!GroupHelpers.IsOwnerOrAdmin(gid, username))
        return Results.Json(new { message = "Forbidden" }, statusCode: 403);
    var req = await ctx.Request.ReadFromJsonAsync<CreateRoleReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Name))
        return Results.BadRequest(new { message = "Name required" });
    var name = InputSanitizer.SanitizeInput(req.Name.Trim(), 32);
    var color = (req.Color ?? "#94a3b8").Trim();
    if (!System.Text.RegularExpressions.Regex.IsMatch(color, @"^#[0-9a-fA-F]{6}$")) color = "#94a3b8";
    var permissions = req.Permissions ?? "{\"send_messages\":true,\"read_messages\":true}";
    var roleId = Guid.NewGuid().ToString("N")[..12];
    using var db = DbHelpers.OpenDb();
    using var maxPos = db.CreateCommand();
    maxPos.CommandText = "SELECT COALESCE(MAX(Position),0) FROM GroupRoles WHERE GroupId=$gid AND Position < 99";
    maxPos.Parameters.AddWithValue("$gid", gid);
    var pos = Convert.ToInt32(maxPos.ExecuteScalar()) + 1;
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO GroupRoles (GroupId,RoleId,Name,Color,Position,Permissions,IsDefault,CreatedAt) VALUES ($gid,$rid,$n,$c,$p,$perm,0,$t)";
    cmd.Parameters.AddWithValue("$gid", gid);
    cmd.Parameters.AddWithValue("$rid", roleId);
    cmd.Parameters.AddWithValue("$n", name);
    cmd.Parameters.AddWithValue("$c", color);
    cmd.Parameters.AddWithValue("$p", pos);
    cmd.Parameters.AddWithValue("$perm", permissions);
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
    AppHelpers.LogActivity(username, "role_create", $"{gid}: {name}");
    return Results.Ok(new { success = true, roleId, name, color, position = pos });
});

app.MapPatch("/api/groups/{groupId}/roles/{roleId}", async (string groupId, string roleId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    var gid = groupId.Trim();
    var rid = roleId.Trim();
    if (!GroupHelpers.IsOwnerOrAdmin(gid, username))
        return Results.Json(new { message = "Forbidden" }, statusCode: 403);
    if (rid == "owner")
        return Results.BadRequest(new { message = "Cannot edit owner role" });
    var req = await ctx.Request.ReadFromJsonAsync<UpdateRoleReq>();
    if (req == null) return Results.BadRequest(new { message = "Body required" });
    using var db = DbHelpers.OpenDb();
    var sets = new List<string>();
    var cmd = db.CreateCommand();
    cmd.Parameters.AddWithValue("$gid", gid);
    cmd.Parameters.AddWithValue("$rid", rid);
    if (req.Name != null)
    {
        sets.Add("Name=$n");
        cmd.Parameters.AddWithValue("$n", InputSanitizer.SanitizeInput(req.Name.Trim(), 32));
    }
    if (req.Color != null)
    {
        var c = req.Color.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(c, @"^#[0-9a-fA-F]{6}$")) c = "#94a3b8";
        sets.Add("Color=$c");
        cmd.Parameters.AddWithValue("$c", c);
    }
    if (req.Position.HasValue)
    {
        sets.Add("Position=$p");
        cmd.Parameters.AddWithValue("$p", req.Position.Value);
    }
    if (req.Permissions != null)
    {
        sets.Add("Permissions=$perm");
        cmd.Parameters.AddWithValue("$perm", req.Permissions);
    }
    if (!sets.Any()) return Results.BadRequest(new { message = "Nothing to update" });
    cmd.CommandText = $"UPDATE GroupRoles SET {string.Join(",", sets)} WHERE GroupId=$gid AND RoleId=$rid";
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) return Results.NotFound(new { message = "Role not found" });
    AppHelpers.LogActivity(username, "role_update", $"{gid}: {rid}");
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/groups/{groupId}/roles/{roleId}", (string groupId, string roleId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    var gid = groupId.Trim();
    var rid = roleId.Trim();
    if (!GroupHelpers.IsOwner(gid, username))
        return Results.Json(new { message = "Only owner can delete roles" }, statusCode: 403);
    if (rid == "owner" || rid == "member")
        return Results.BadRequest(new { message = "Cannot delete owner or default member role" });
    using var db = DbHelpers.OpenDb();
    using var fallback = db.CreateCommand();
    fallback.CommandText = "UPDATE GroupMembers SET Role='member' WHERE GroupId=$gid AND Role=$rid";
    fallback.Parameters.AddWithValue("$gid", gid);
    fallback.Parameters.AddWithValue("$rid", rid);
    fallback.ExecuteNonQuery();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM GroupRoles WHERE GroupId=$gid AND RoleId=$rid";
    cmd.Parameters.AddWithValue("$gid", gid);
    cmd.Parameters.AddWithValue("$rid", rid);
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) return Results.NotFound(new { message = "Role not found" });
    AppHelpers.LogActivity(username, "role_delete", $"{gid}: {rid}");
    return Results.Ok(new { success = true });
});

app.MapPost("/api/groups/{groupId}/roles/reorder", async (string groupId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    var gid = groupId.Trim();
    if (!GroupHelpers.IsOwnerOrAdmin(gid, username))
        return Results.Json(new { message = "Forbidden" }, statusCode: 403);
    var req = await ctx.Request.ReadFromJsonAsync<ReorderRolesReq>();
    if (req == null || req.Order == null || !req.Order.Any())
        return Results.BadRequest(new { message = "Order required" });
    using var db = DbHelpers.OpenDb();
    for (int i = 0; i < req.Order.Count; i++)
    {
        var rid = req.Order[i].Trim();
        if (rid == "owner") continue;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE GroupRoles SET Position=$p WHERE GroupId=$gid AND RoleId=$rid";
        cmd.Parameters.AddWithValue("$p", i + 1);
        cmd.Parameters.AddWithValue("$gid", gid);
        cmd.Parameters.AddWithValue("$rid", rid);
        cmd.ExecuteNonQuery();
    }
    return Results.Ok(new { success = true });
});


// ═══════════════════════════════════════════════
// KEY-BASED AUTH ENDPOINTS
// ═══════════════════════════════════════════════


app.MapPost("/api/auth/register-email", async (HttpContext ctx) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!limiter.IsAllowed(ip, "register_email", 5, 3600))
        return Results.Json(new { message = "Too many attempts." }, statusCode: 429);

    // rs-register-email-defensive-v1
    var req = await ctx.Request.ReadFromJsonAsync<RegisterEmailReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Email))
        return Results.BadRequest(new { status = "error", message = "Valid email required." });

    var email = DefensiveInput.CleanString(req.Email, 254).ToLowerInvariant();
    if (!DefensiveInput.IsEmail(email))
        return Results.BadRequest(new { status = "error", message = "Valid email required." });

    if (!limiter.IsAllowed(email, "register_email_addr", 3, 3600))
        return Results.Json(new { message = "Too many attempts." }, statusCode: 429);

    var code = OtpGenerator.GenerateCode();
    var cacheKey = $"reg_email_otp:{email}";
    OtpCache.Set(cacheKey, code, TimeSpan.FromMinutes(15));

    try { await SmtpMailService.SendOtpAsync(email, email.Split('@')[0], code, "verify"); }
    catch { Console.WriteLine("[SMTP] Send failed"); }

    return Results.Ok(new { status = "otp_required", pendingToken = email, message = "Code sent." });
});


app.MapPost("/api/auth/verify-email-otp", async (HttpContext ctx) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<VerifyOtpReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.PendingToken) || string.IsNullOrWhiteSpace(req.Code))
        return Results.BadRequest(new { status = "error", message = "Invalid request." });

    // rs-verify-email-otp-defensive-v1
    var email = DefensiveInput.CleanString(req.PendingToken, 254).ToLowerInvariant();
    var code = DefensiveInput.CleanString(req.Code, 8);

    if (!DefensiveInput.IsEmail(email) || !DefensiveInput.IsOtpCode(code))
        return Results.BadRequest(new { status = "error", message = "Invalid request." });

    var cacheKey = $"reg_email_otp:{email}";
    if (!OtpCache.TryGet(cacheKey, out var stored) || stored != code)
        return Results.Json(new { status = "error", message = "Invalid or expired code." }, statusCode: 400);

    OtpCache.Remove(cacheKey);

    // Store verified email in cache so register-with-key can use it
    OtpCache.Set($"verified_email:{email}", email, TimeSpan.FromMinutes(30));

    return Results.Ok(new { status = "ok", message = "Email verified." });
});

app.MapPost("/api/auth/register-with-key", async (HttpContext ctx) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var threat = ctx.RequestServices.GetRequiredService<ThreatIntelligence>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!limiter.IsAllowed(ip, "register_key", 5, 3600))
    { threat.RecordStrike(ip, "register_abuse"); return Results.Json(new { message = "Too many attempts." }, statusCode: 429); }
    var req = await ctx.Request.ReadFromJsonAsync<RegisterWithKeyReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.AccountKey))
        return Results.BadRequest(new { status = "error", message = "Username and account key required." });
    var username = req.Username.Trim().ToLowerInvariant();
    var accountKey = AccountKeyHashing.Normalize(req.AccountKey);
    var accountKeyHash = AccountKeyHashing.Hash(accountKey);
    if (!AppHelpers.IsValidUsername(username))
        return Results.BadRequest(new { status = "error", message = "Invalid username." });
    if (ReservedNames.IsReserved(username))
        return Results.BadRequest(new { status = "error", message = "Username reserved." });
    if (ContentFilter.IsOffensive(username))
        return Results.BadRequest(new { status = "error", message = "Username not allowed." });
    // rs-key-auth-defensive-v1
    // Validate UUID v4 format before touching storage.
    if (!DefensiveInput.IsUuidV4(accountKey))
        return Results.BadRequest(new { status = "error", message = "Invalid account key format." });
    // Check if key already in use
    using var db = DbHelpers.OpenDb();
    using var keyCheck = db.CreateCommand();
    keyCheck.CommandText = "SELECT COUNT(*) FROM AuthUsers WHERE AccountKeyHash=$kh OR AccountKey=$k";
    keyCheck.Parameters.AddWithValue("$kh", accountKeyHash);
    keyCheck.Parameters.AddWithValue("$k", accountKey);
    if (Convert.ToInt32(keyCheck.ExecuteScalar()) > 0)
        return Results.Json(new { status = "error", message = "Account key already in use." }, statusCode: 409);
    using var exists = db.CreateCommand();
    exists.CommandText = "SELECT COUNT(*) FROM AuthUsers WHERE Username=$u";
    exists.Parameters.AddWithValue("$u", username);
    if (Convert.ToInt32(exists.ExecuteScalar()) > 0)
        return Results.Json(new { status = "error", message = "Username already taken." }, statusCode: 409);
    // Check if email was verified
    var pendingToken = (req.PendingToken ?? "").Trim().ToLowerInvariant();
    var emailVerified = false;
    var email = "";
    if (!string.IsNullOrWhiteSpace(pendingToken) && pendingToken.Contains("@"))
    {
        if (OtpCache.TryGet($"verified_email:{pendingToken}", out _))
        {
            emailVerified = true;
            email = pendingToken;
            OtpCache.Remove($"verified_email:{pendingToken}");
        }
    }
    var now = DateTime.UtcNow.ToString("o");
    // Insert user with empty password hash — key is the auth method
    using var ins = db.CreateCommand();
    ins.CommandText = @"INSERT INTO AuthUsers
        (Username, PasswordHash, CreatedAt, Status, Badges, Email, EmailVerified, AccountKey, AccountKeyHash, PublicId)
        VALUES ($u, '', $t, $status, '[]', $e, $ev, $key, $keyHash, lower(hex(randomblob(16))))";
    ins.Parameters.AddWithValue("$u", username);
    ins.Parameters.AddWithValue("$t", now);
    ins.Parameters.AddWithValue("$status", emailVerified ? "verified" : "pending");
    ins.Parameters.AddWithValue("$e", email);
    ins.Parameters.AddWithValue("$ev", emailVerified ? 1 : 0);
    ins.Parameters.AddWithValue("$key", accountKey);
    ins.Parameters.AddWithValue("$keyHash", accountKeyHash);
    ins.ExecuteNonQuery();
    AppHelpers.LogActivity(username, "register_with_key", $"From {ip} emailVerified={emailVerified}");
    // Sign in immediately
    var identity = new System.Security.Claims.ClaimsIdentity(
        new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username) }, "cookie");
    var principal = new System.Security.Claims.ClaimsPrincipal(identity);
    await ctx.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    // rs-key-auth-device-token-cookie-defensive-v1
    var deviceToken = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Token"].FirstOrDefault() ?? "", 256);
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
    return Results.Ok(new { status = "ok", redirect = "/app", username });
});

app.MapPost("/api/auth/login-with-key", async (HttpContext ctx) =>
{
    var brute = ctx.RequestServices.GetRequiredService<BruteForceProtection>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (brute.IsIpLocked(ip)) return Results.Json(new { message = "IP locked." }, statusCode: 429);
    var req = await ctx.Request.ReadFromJsonAsync<LoginWithKeyReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.AccountKey))
        return Results.BadRequest(new { status = "error", message = "Account key required." });
    var accountKey = AccountKeyHashing.Normalize(req.AccountKey);
    var accountKeyHash = AccountKeyHashing.Hash(accountKey);

    // rs-login-key-defensive-v1
    // Reject malformed keys before DB lookup.
    if (!DefensiveInput.IsUuidV4(accountKey))
        return Results.BadRequest(new { status = "error", message = "Invalid account key format." });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Username, Status, AccountLockedUntil, IsSuspended FROM AuthUsers WHERE AccountKeyHash=$kh LIMIT 1";
    cmd.Parameters.AddWithValue("$kh", accountKeyHash);
    using var r = cmd.ExecuteReader();
    if (!r.Read())
    {
        brute.RecordFailedAttempt(ip, "key_login");
        await Task.Delay(500);
        return Results.Json(new { status = "error", message = "Invalid account key." }, statusCode: 401);
    }
    var username = r.GetString(0);
    var status = r.IsDBNull(1) ? "" : r.GetString(1);
    var lockedStr = r.IsDBNull(2) ? "" : r.GetString(2);
    var isSuspended = !r.IsDBNull(3) && r.GetInt32(3) == 1;
    r.Close();
    if (status == "banned") return Results.Json(new { message = "Account banned." }, statusCode: 403);
    if (isSuspended) return Results.Json(new { message = "Account suspended." }, statusCode: 403);
    if (!string.IsNullOrWhiteSpace(lockedStr) && DateTime.TryParse(lockedStr, out var locked) && locked > DateTime.UtcNow)
        return Results.Json(new { message = "Account locked." }, statusCode: 423);
    brute.ClearAttempts(ip, username);
    var identity = new System.Security.Claims.ClaimsIdentity(
        new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, username) }, "cookie");
    var principal = new System.Security.Claims.ClaimsPrincipal(identity);
    await ctx.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    // rs-key-auth-device-token-cookie-defensive-v1
    var deviceToken = DefensiveInput.CleanString(ctx.Request.Headers["X-Device-Token"].FirstOrDefault() ?? "", 256);
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
    return Results.Ok(new { status = "ok", redirect = "/app", username, isAdmin = AppHelpers.IsAdmin(username) });
});



app.MapPost("/api/auth/rotate-account-key", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();

    if (string.IsNullOrWhiteSpace(username))
        return Results.Json(new { status = "error", message = "Not signed in." }, statusCode: 401);

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    if (!limiter.IsAllowed(ip + ":" + username, "rotate_account_key", 5, 3600))
        return Results.Json(new { status = "error", message = "Too many key changes. Try again later." }, statusCode: 429);

    System.Text.Json.JsonElement body;

    try
    {
        body = await ctx.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }
    catch
    {
        return Results.BadRequest(new { status = "error", message = "Invalid request body." });
    }

    string accountKey = "";

    if (body.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        if (body.TryGetProperty("accountKey", out var k1))
            accountKey = k1.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(accountKey) && body.TryGetProperty("AccountKey", out var k2))
            accountKey = k2.GetString() ?? "";
    }

    accountKey = AccountKeyHashing.Normalize(accountKey);
    var accountKeyHash = AccountKeyHashing.Hash(accountKey);

    if (string.IsNullOrWhiteSpace(accountKey))
        return Results.BadRequest(new { status = "error", message = "Account key required." });

    if (!System.Text.RegularExpressions.Regex.IsMatch(
        accountKey,
        @"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        return Results.BadRequest(new { status = "error", message = "Invalid account key format." });
    }

    using var db = DbHelpers.OpenDb();

    using var exists = db.CreateCommand();
    exists.CommandText = @"
        SELECT COUNT(*)
        FROM AuthUsers
        WHERE (AccountKeyHash=$kh OR AccountKey=$k)
          AND LOWER(Username) <> LOWER($u)";
    exists.Parameters.AddWithValue("$kh", accountKeyHash);
    exists.Parameters.AddWithValue("$k", accountKey);
    exists.Parameters.AddWithValue("$u", username);

    if (Convert.ToInt32(exists.ExecuteScalar()) > 0)
        return Results.Json(new { status = "error", message = "Account key already in use." }, statusCode: 409);

    DbHelpers.EnsureColumn(db, "AuthUsers", "SecurityChangedAt", "TEXT NOT NULL DEFAULT ''");

    using var upd = db.CreateCommand();
    upd.CommandText = @"
        UPDATE AuthUsers
        SET AccountKey=$k, AccountKeyHash=$kh, SecurityChangedAt=$sc
        WHERE LOWER(Username)=LOWER($u)";
    upd.Parameters.AddWithValue("$k", accountKey);
    upd.Parameters.AddWithValue("$kh", accountKeyHash);
    upd.Parameters.AddWithValue("$sc", DateTime.UtcNow.ToString("o"));
    upd.Parameters.AddWithValue("$u", username);

    var changed = upd.ExecuteNonQuery();

    if (changed <= 0)
        return Results.Json(new { status = "error", message = "Account not found." }, statusCode: 404);

    // rs-rotate-account-key-kill-sessions-v1
    using (var delSessions = db.CreateCommand())
    {
        delSessions.CommandText = "DELETE FROM PersistentSessions WHERE LOWER(Username)=LOWER($u)";
        delSessions.Parameters.AddWithValue("$u", username);
        delSessions.ExecuteNonQuery();
    }

    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(username);

    await ctx.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Cookies.Delete("runspace_auth_v3");
    ctx.Response.Cookies.Delete("runspace_auth_v2");
    ctx.Response.Cookies.Delete("runspace_auth");
    ctx.Response.Cookies.Delete("rs-dt");

    AppHelpers.LogActivity(username, "rotate_account_key", $"From {ip}");

    return Results.Ok(new
    {
        status = "ok",
        message = "Account key rotated. Existing sessions were logged out.",
        loggedOut = true
    });
});


app.MapPost("/api/auth/change-username", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "change_username", 2, 3600))
        return Results.Json(new { message = "Too many attempts." }, statusCode: 429);
    // rs-change-username-defensive-v1
    ChangeUsernameReq? req;
    try
    {
        req = await ctx.Request.ReadFromJsonAsync<ChangeUsernameReq>();
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    if (req == null || string.IsNullOrWhiteSpace(req.NewUsername))
        return Results.BadRequest(new { message = "Username required." });

    var newUsername = DefensiveInput.CleanString(req.NewUsername, 32).ToLowerInvariant();
    if (!AppHelpers.IsValidUsername(newUsername))
        return Results.BadRequest(new { message = "Invalid username." });
    if (ReservedNames.IsReserved(newUsername))
        return Results.BadRequest(new { message = "Username reserved." });
    if (ContentFilter.IsOffensive(newUsername))
        return Results.BadRequest(new { message = "Username not allowed." });
    if (newUsername == u)
        return Results.BadRequest(new { message = "That is already your username." });
    using var db = DbHelpers.OpenDb();
    // Check cooldown
    DbHelpers.EnsureColumn(db, "AuthUsers", "UsernameChangedAt", "TEXT NOT NULL DEFAULT ''");
    using var cdCmd = db.CreateCommand();
    cdCmd.CommandText = "SELECT UsernameChangedAt FROM AuthUsers WHERE Username=$u LIMIT 1";
    cdCmd.Parameters.AddWithValue("$u", u);
    var lastChanged = cdCmd.ExecuteScalar() as string ?? "";
    if (!string.IsNullOrWhiteSpace(lastChanged) && DateTime.TryParse(lastChanged, out var last))
    {
        var daysSince = (DateTime.UtcNow - last).TotalDays;
        if (daysSince < 3)
        {
            var daysLeft = Math.Ceiling(3 - daysSince);
            return Results.Json(new { message = $"You can change your username in {daysLeft} day(s)." }, statusCode: 429);
        }
    }
    // Check availability
    using var checkCmd = db.CreateCommand();
    checkCmd.CommandText = "SELECT COUNT(*) FROM AuthUsers WHERE Username=$n COLLATE NOCASE";
    checkCmd.Parameters.AddWithValue("$n", newUsername);
    if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
        return Results.Json(new { message = "Username already taken." }, statusCode: 409);
    // Update
    using var updCmd = db.CreateCommand();
    updCmd.CommandText = "UPDATE AuthUsers SET Username=$n, UsernameChangedAt=$t WHERE Username=$u";
    updCmd.Parameters.AddWithValue("$n", newUsername);
    updCmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    updCmd.Parameters.AddWithValue("$u", u);
    updCmd.ExecuteNonQuery();
    AppHelpers.LogActivity(newUsername, "change_username", $"From {u}");
    // Re-sign in with new username
    var identity = new System.Security.Claims.ClaimsIdentity(
        new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, newUsername) }, "cookie");
    var principal = new System.Security.Claims.ClaimsPrincipal(identity);
    await ctx.SignInAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });
    return Results.Ok(new { status = "ok", username = newUsername });
});

app.MapHub<ChatHub>("/ws/chat");

// ── Sponsors API ──
app.MapGet("/api/sponsors", () =>
{
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Id, Name, Description, Url, LogoPath, CreatedAt, Links, DescriptionEn FROM Sponsors ORDER BY Id DESC";

    using var reader = cmd.ExecuteReader();
    var sponsors = new List<object>();

    while (reader.Read())
    {
        sponsors.Add(new
        {
            id = reader.GetInt32(0),
            name = DefensiveInput.CleanString(reader.IsDBNull(1) ? "" : reader.GetString(1), 120),
            description = DefensiveInput.CleanString(reader.IsDBNull(2) ? "" : reader.GetString(2), 1000),
            url = DefensiveInput.CleanString(reader.IsDBNull(3) ? "" : reader.GetString(3), 500),
            logoPath = DefensiveInput.CleanString(reader.IsDBNull(4) ? "" : reader.GetString(4), 300),
            createdAt = DefensiveInput.CleanString(reader.IsDBNull(5) ? "" : reader.GetString(5), 80),
            links = DefensiveInput.CleanString(reader.IsDBNull(6) ? "[]" : reader.GetString(6), 4000),
            descriptionEn = DefensiveInput.CleanString(reader.IsDBNull(7) ? "" : reader.GetString(7), 1000)
        });
    }

    return Results.Ok(sponsors);
});

app.MapPost("/api/sponsors", async (HttpContext ctx, HttpRequest request) =>
{
    // rs-sponsors-defensive-v1
    var admin = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value?.Trim().ToLowerInvariant() ?? "";
    if (ctx.User.Identity?.IsAuthenticated != true || !AppHelpers.IsAdmin(admin))
        return Results.Unauthorized();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_sponsors_write", 30, 300))
        return Results.Json(new { error = "Too many sponsor updates" }, statusCode: 429);

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart form data" });

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync();
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid form data" });
    }

    var name = DefensiveInput.CleanString(form["name"].ToString(), 120);
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Name is required" });

    var description = DefensiveInput.CleanString(form["description"].ToString(), 1000);
    var descriptionEn = DefensiveInput.CleanString(form["descriptionEn"].ToString(), 1000);
    var url = DefensiveInput.CleanString(form["url"].ToString(), 500);
    var links = DefensiveInput.CleanString(form["links"].ToString(), 4000);

    if (string.IsNullOrWhiteSpace(links))
        links = "[]";

    if (!string.IsNullOrWhiteSpace(url) &&
        (!Uri.TryCreate(url, UriKind.Absolute, out var sponsorUri) ||
         sponsorUri.Scheme is not ("https" or "http")))
        return Results.BadRequest(new { error = "Invalid sponsor URL" });

    try
    {
        using var linksDoc = JsonDocument.Parse(links);
        if (linksDoc.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            return Results.BadRequest(new { error = "Invalid links JSON" });
        links = JsonSerializer.Serialize(linksDoc.RootElement);
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid links JSON" });
    }

    var logoPath = "";
    var logoFile = form.Files.GetFile("logo");

    if (logoFile != null && logoFile.Length > 0)
    {
        if (logoFile.Length > 2_000_000)
            return Results.Json(new { error = "Logo too large" }, statusCode: 413);

        var uploadsDir = "/var/lib/runspace/data/uploads/sponsors";
        Directory.CreateDirectory(uploadsDir);

        var logoOriginalName = DefensiveInput.SafeFileName(logoFile.FileName);
        var ext = Path.GetExtension(logoOriginalName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".png";

        var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        if (!allowed.Contains(ext))
            return Results.BadRequest(new { error = "Invalid image format" });

        var filename = $"sponsor_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        var fullPath = Path.Combine(uploadsDir, filename);

        using (var stream = new FileStream(fullPath, FileMode.CreateNew))
        {
            await logoFile.CopyToAsync(stream);
        }

        logoPath = $"/uploads/sponsors/{filename}";
    }

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO Sponsors (Name, Description, Url, LogoPath, Links, DescriptionEn) VALUES (@name, @desc, @url, @logo, @links, @descEn); SELECT last_insert_rowid();";
    cmd.Parameters.AddWithValue("@name", name);
    cmd.Parameters.AddWithValue("@desc", description);
    cmd.Parameters.AddWithValue("@url", url);
    cmd.Parameters.AddWithValue("@logo", logoPath);
    cmd.Parameters.AddWithValue("@links", links);
    cmd.Parameters.AddWithValue("@descEn", descriptionEn);

    var newId = Convert.ToInt32(cmd.ExecuteScalar());

    AppHelpers.LogActivity(admin, "admin_sponsor_create", "Id=" + newId);

    return Results.Ok(new { id = newId, name, description, url, logoPath, links, descriptionEn });
}).DisableAntiforgery();

app.MapPut("/api/sponsors/{id:int}", async (int id, HttpContext ctx, HttpRequest request) =>
{
    var admin = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value?.Trim().ToLowerInvariant() ?? "";
    if (ctx.User.Identity?.IsAuthenticated != true || !AppHelpers.IsAdmin(admin))
        return Results.Unauthorized();

    if (id <= 0)
        return Results.BadRequest(new { error = "Invalid sponsor id" });

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_sponsors_write", 30, 300))
        return Results.Json(new { error = "Too many sponsor updates" }, statusCode: 429);

    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart form data" });

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync();
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid form data" });
    }

    var name = DefensiveInput.CleanString(form["name"].ToString(), 120);
    if (string.IsNullOrWhiteSpace(name))
        return Results.BadRequest(new { error = "Name is required" });

    var description = DefensiveInput.CleanString(form["description"].ToString(), 1000);
    var descriptionEn = DefensiveInput.CleanString(form["descriptionEn"].ToString(), 1000);
    var url = DefensiveInput.CleanString(form["url"].ToString(), 500);
    var links = DefensiveInput.CleanString(form["links"].ToString(), 4000);
    var logoPath = DefensiveInput.CleanString(form["existingLogo"].ToString(), 300);

    if (string.IsNullOrWhiteSpace(links))
        links = "[]";

    if (!string.IsNullOrWhiteSpace(url) &&
        (!Uri.TryCreate(url, UriKind.Absolute, out var sponsorUri) ||
         sponsorUri.Scheme is not ("https" or "http")))
        return Results.BadRequest(new { error = "Invalid sponsor URL" });

    try
    {
        using var linksDoc = JsonDocument.Parse(links);
        if (linksDoc.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            return Results.BadRequest(new { error = "Invalid links JSON" });
        links = JsonSerializer.Serialize(linksDoc.RootElement);
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid links JSON" });
    }

    if (!string.IsNullOrWhiteSpace(logoPath) &&
        !System.Text.RegularExpressions.Regex.IsMatch(logoPath, @"^/uploads/sponsors/sponsor_[0-9]+\.(png|jpg|jpeg|gif|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        return Results.BadRequest(new { error = "Invalid existing logo path" });

    var logoFile = form.Files.GetFile("logo");

    if (logoFile != null && logoFile.Length > 0)
    {
        if (logoFile.Length > 2_000_000)
            return Results.Json(new { error = "Logo too large" }, statusCode: 413);

        var uploadsDir = "/var/lib/runspace/data/uploads/sponsors";
        Directory.CreateDirectory(uploadsDir);

        var logoOriginalName = DefensiveInput.SafeFileName(logoFile.FileName);
        var ext = Path.GetExtension(logoOriginalName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) ext = ".png";

        var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        if (!allowed.Contains(ext))
            return Results.BadRequest(new { error = "Invalid image format" });

        var filename = $"sponsor_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        var fullPath = Path.Combine(uploadsDir, filename);

        using (var stream = new FileStream(fullPath, FileMode.CreateNew))
        {
            await logoFile.CopyToAsync(stream);
        }

        logoPath = $"/uploads/sponsors/{filename}";
    }

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE Sponsors SET Name=@name, Description=@desc, Url=@url, LogoPath=@logo, Links=@links, DescriptionEn=@descEn WHERE Id=@id";
    cmd.Parameters.AddWithValue("@name", name);
    cmd.Parameters.AddWithValue("@desc", description);
    cmd.Parameters.AddWithValue("@url", url);
    cmd.Parameters.AddWithValue("@logo", logoPath);
    cmd.Parameters.AddWithValue("@links", links);
    cmd.Parameters.AddWithValue("@descEn", descriptionEn);
    cmd.Parameters.AddWithValue("@id", id);

    var rows = cmd.ExecuteNonQuery();

    if (rows == 0)
        return Results.NotFound(new { error = "Sponsor not found" });

    AppHelpers.LogActivity(admin, "admin_sponsor_update", "Id=" + id);

    return Results.Ok(new { id, name, description, url, logoPath, links, descriptionEn });
}).DisableAntiforgery();

app.MapDelete("/api/sponsors/{id:int}", (int id, HttpContext ctx) =>
{
    var admin = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value?.Trim().ToLowerInvariant() ?? "";
    if (ctx.User.Identity?.IsAuthenticated != true || !AppHelpers.IsAdmin(admin))
        return Results.Unauthorized();

    if (id <= 0)
        return Results.BadRequest(new { error = "Invalid sponsor id" });

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_sponsors_write", 30, 300))
        return Results.Json(new { error = "Too many sponsor updates" }, statusCode: 429);

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    conn.Open();

    using var getCmd = conn.CreateCommand();
    getCmd.CommandText = "SELECT LogoPath FROM Sponsors WHERE Id = @id";
    getCmd.Parameters.AddWithValue("@id", id);

    var logoPath = DefensiveInput.CleanString(getCmd.ExecuteScalar() as string, 300);

    using var delCmd = conn.CreateCommand();
    delCmd.CommandText = "DELETE FROM Sponsors WHERE Id = @id";
    delCmd.Parameters.AddWithValue("@id", id);

    var rows = delCmd.ExecuteNonQuery();

    if (rows == 0)
        return Results.NotFound(new { error = "Sponsor not found" });

    if (!string.IsNullOrWhiteSpace(logoPath) &&
        System.Text.RegularExpressions.Regex.IsMatch(logoPath, @"^/uploads/sponsors/sponsor_[0-9]+\.(png|jpg|jpeg|gif|webp)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        var uploadsDir = "/var/lib/runspace/data/uploads/sponsors";
        var fileName = Path.GetFileName(logoPath);
        var fullPath = Path.GetFullPath(Path.Combine(uploadsDir, fileName));
        var safeRoot = Path.GetFullPath(uploadsDir) + Path.DirectorySeparatorChar;

        if (fullPath.StartsWith(safeRoot, StringComparison.Ordinal) && System.IO.File.Exists(fullPath))
            System.IO.File.Delete(fullPath);
    }

    AppHelpers.LogActivity(admin, "admin_sponsor_delete", "Id=" + id);

    return Results.Ok(new { deleted = true, id });
});


app.MapPost("/api/admin/badges/{target}", async (string target, HttpContext ctx) =>
{
    // rs-admin-badges-status-patchnotes-defensive-v1
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_profile_write", 60, 300))
        return Results.Json(new { message = "Too many admin profile actions." }, statusCode: 429);

    var tgt = DefensiveInput.CleanString(target, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(tgt) || !AppHelpers.IsValidUsername(tgt))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(tgt))
        return Results.NotFound(new { message = "User not found." });

    JsonDocument body;
    try
    {
        body = await JsonDocument.ParseAsync(ctx.Request.Body);
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    using (body)
    {
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("badges", out var badgesEl) ||
            badgesEl.ValueKind != JsonValueKind.Array)
            return Results.BadRequest(new { message = "Invalid badges payload." });

        var badges = badgesEl.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => DefensiveInput.CleanString(x.GetString(), 32).ToLowerInvariant())
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x, "^[a-z0-9_-]{1,32}$"))
            .Distinct()
            .Take(20)
            .ToList();

        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE AuthUsers SET Badges = $b WHERE Username = $u";
        cmd.Parameters.AddWithValue("$b", JsonSerializer.Serialize(badges));
        cmd.Parameters.AddWithValue("$u", tgt);
        cmd.ExecuteNonQuery();

        AppHelpers.LogActivity(admin, "admin_badges", $"{tgt}: {string.Join(", ", badges)}");
        return Results.Ok(new { success = true, username = tgt, badges });
    }
});

app.MapPost("/api/admin/status/{target}", async (string target, HttpContext ctx) =>
{
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_profile_write", 60, 300))
        return Results.Json(new { message = "Too many admin profile actions." }, statusCode: 429);

    var tgt = DefensiveInput.CleanString(target, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(tgt) || !AppHelpers.IsValidUsername(tgt))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(tgt))
        return Results.NotFound(new { message = "User not found." });

    if (AppHelpers.IsAdmin(tgt))
        return Results.BadRequest(new { message = "Cannot modify admin." });

    JsonDocument body;
    try
    {
        body = await JsonDocument.ParseAsync(ctx.Request.Body);
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    using (body)
    {
        if (body.RootElement.ValueKind != JsonValueKind.Object ||
            !body.RootElement.TryGetProperty("status", out var statusEl) ||
            statusEl.ValueKind != JsonValueKind.String)
            return Results.BadRequest(new { message = "Invalid status payload." });

        var status = DefensiveInput.CleanString(statusEl.GetString(), 32).ToLowerInvariant();
        var allowed = new[] { "verified", "banned", "suspended", "unverified" };
        if (!allowed.Contains(status))
            return Results.BadRequest(new { message = "Invalid status." });

        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE AuthUsers SET Status = $s WHERE Username = $u";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$u", tgt);
        cmd.ExecuteNonQuery();

        AppHelpers.LogActivity(admin, "admin_status", $"{tgt}: {status}");
        return Results.Ok(new { success = true, username = tgt, status });
    }
});

app.MapPost("/api/admin/patch-notes", async (HttpContext ctx) =>
{
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_patch_notes", 30, 300))
        return Results.Json(new { message = "Too many patch note updates." }, statusCode: 429);

    using var reader = new StreamReader(ctx.Request.Body);
    var raw = await reader.ReadToEndAsync();

    if (raw.Length > 128_000)
        return Results.Json(new { message = "Patch notes payload too large." }, statusCode: 413);

    if (raw.IndexOf('\0') >= 0)
        return Results.BadRequest(new { message = "Invalid patch notes payload." });

    JsonDocument doc;
    try
    {
        doc = JsonDocument.Parse(raw);
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    using (doc)
    {
        if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return Results.BadRequest(new { message = "Patch notes must be a JSON object or array." });

        var json = System.Text.Json.JsonSerializer.Serialize(doc.RootElement);

        Directory.CreateDirectory(AppConfig.DataDir);
        var path = Path.Combine(AppConfig.DataDir, "patch-notes.json");
        var tmp = path + ".tmp";

        await System.IO.File.WriteAllTextAsync(tmp, json);
        System.IO.File.Move(tmp, path, true);

        AppHelpers.LogActivity(admin, "admin_patch_notes", "updated patch-notes.json");
        return Results.Ok(new { success = true });
    }
});

app.MapGet("/api/admin/patch-notes", async (HttpContext ctx) =>
{
    var path = Path.Combine(AppConfig.DataDir, "patch-notes.json");
    if (!System.IO.File.Exists(path))
        return Results.Ok(new { notes = new List<object>() });

    var json = await System.IO.File.ReadAllTextAsync(path);

    if (json.Length > 256_000)
        return Results.Json(new { notes = new List<object>() });

    try
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return Results.Json(new { notes = new List<object>() });
    }
    catch
    {
        return Results.Json(new { notes = new List<object>() });
    }

    return Results.Content(json, "application/json");
});


app.MapPost("/api/admin/users/{username}/trust", async (string username, HttpContext ctx) =>
{
    // rs-admin-trust-override-defensive-v1
    var caller = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (string.IsNullOrWhiteSpace(caller) || !AppHelpers.IsAdmin(caller))
        return Results.Json(new { message = "Ej behörig." }, statusCode: 403);

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(caller, "admin_trust_override", 30, 300))
        return Results.Json(new { message = "Too many admin trust actions." }, statusCode: 429);

    TrustOverrideReq? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<TrustOverrideReq>();
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    if (body == null)
        return Results.BadRequest(new { message = "Invalid request." });

    var target = DefensiveInput.CleanString(username, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(target) || !AppHelpers.IsValidUsername(target))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(target))
        return Results.NotFound(new { message = "Användaren hittades inte." });

    if (body.Score.HasValue && (body.Score.Value < 0 || body.Score.Value > 100))
        return Results.BadRequest(new { message = "Score måste vara 0-100." });

    if (body.MinTrust.HasValue && (body.MinTrust.Value < 0 || body.MinTrust.Value > 100))
        return Results.BadRequest(new { message = "MinTrust måste vara 0-100." });

    if (body.MaxTrust.HasValue && (body.MaxTrust.Value < 0 || body.MaxTrust.Value > 100))
        return Results.BadRequest(new { message = "MaxTrust måste vara 0-100." });

    if (body.MinTrust.HasValue && body.MaxTrust.HasValue && body.MinTrust.Value > body.MaxTrust.Value)
        return Results.BadRequest(new { message = "MinTrust kan inte vara större än MaxTrust." });

    var validFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "files", "links", "exec", "mention", "profile"
    };

    var disabledFeatures = (body.DisableFeatures ?? new List<string>())
        .Select(f => DefensiveInput.CleanString(f, 32).ToLowerInvariant())
        .Where(f => !string.IsNullOrWhiteSpace(f))
        .Distinct()
        .Take(20)
        .ToList();

    if (disabledFeatures.Any(f => !validFeatures.Contains(f)))
        return Results.BadRequest(new { message = "Ogiltiga feature-namn." });

    using var db = DbHelpers.OpenDb();

    using var upd = db.CreateCommand();
    upd.CommandText = @"UPDATE AuthUsers SET
        trust_override_min = $min,
        trust_override_max = $max,
        trust_force_block  = $block,
        trust_disabled_features = $features
        WHERE Username=$u";
    upd.Parameters.AddWithValue("$min", body.MinTrust.HasValue ? (object)body.MinTrust.Value : DBNull.Value);
    upd.Parameters.AddWithValue("$max", body.MaxTrust.HasValue ? (object)body.MaxTrust.Value : DBNull.Value);
    upd.Parameters.AddWithValue("$block", body.ForceBlock == true ? 1 : 0);
    upd.Parameters.AddWithValue("$features", disabledFeatures.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(disabledFeatures) : DBNull.Value);
    upd.Parameters.AddWithValue("$u", target);
    upd.ExecuteNonQuery();

    var detailJson = System.Text.Json.JsonSerializer.Serialize(new
    {
        min = body.MinTrust,
        max = body.MaxTrust,
        score = body.Score,
        forceBlock = body.ForceBlock == true,
        disableFeatures = disabledFeatures
    });

    using var evtCmd = db.CreateCommand();
    evtCmd.CommandText = @"INSERT INTO SecurityEventLog (UserId, Timestamp, EventType, FromState, ToState, Details)
        SELECT Id, datetime('now'), 'OVERRIDE_SET', 'admin', $caller, $detail FROM AuthUsers WHERE Username=$u";
    evtCmd.Parameters.AddWithValue("$caller", caller);
    evtCmd.Parameters.AddWithValue("$detail", detailJson);
    evtCmd.Parameters.AddWithValue("$u", target);
    evtCmd.ExecuteNonQuery();

    AppHelpers.LogActivity(caller, "admin_trust_override", "Target=" + target);

    return Results.Ok(new
    {
        username = target,
        setBy = caller,
        setAt = DateTime.UtcNow.ToString("o"),
        overrides = new
        {
            minTrust = body.MinTrust,
            maxTrust = body.MaxTrust,
            forceBlock = body.ForceBlock ?? false,
            disabledFeatures = disabledFeatures
        }
    });
});

app.MapGet("/api/admin/users/{username}/trust", (string username, HttpContext ctx) =>
{
    var caller = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(caller ?? "")) return Results.Json(new { message = "Ej behörig." }, statusCode: 403);
    var target = DefensiveInput.CleanString(username, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(target) || !AppHelpers.IsValidUsername(target))
        return Results.BadRequest(new { message = "Invalid username." });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT CreatedAt, trust_identity, trust_behavior, trust_device,
        trust_override_min, trust_override_max, trust_force_block, trust_disabled_features, TrustLevel
        FROM AuthUsers WHERE Username=$u LIMIT 1";
    cmd.Parameters.AddWithValue("$u", target);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.NotFound();
    var createdAt = DateTime.TryParse(r.IsDBNull(0) ? "" : r.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.UtcNow;
    var dims = new TrustDimensions
    {
        Identity = r.IsDBNull(1) ? 50.0 : r.GetDouble(1),
        Behavior = r.IsDBNull(2) ? 60.0 : r.GetDouble(2),
        Device = r.IsDBNull(3) ? 70.0 : r.GetDouble(3)
    };
    var composite = TrustEngine.CompositeScore(dims);
    var currentLevel = TrustEngine.ParseLevel(r.IsDBNull(8) ? "medium" : r.GetString(8));
    var trustLevel = TrustEngine.GetLevel(composite, currentLevel);

    // rs-admin-security-queries-defensive-v1
    var overrideMin = r.IsDBNull(4) ? (int?)null : r.GetInt32(4);
    var overrideMax = r.IsDBNull(5) ? (int?)null : r.GetInt32(5);
    var forceBlock = !r.IsDBNull(6) && r.GetInt32(6) == 1;
    var disabledRaw = r.IsDBNull(7) ? null : r.GetString(7);

    List<string> disabledFeatures;
    try
    {
        disabledFeatures = string.IsNullOrWhiteSpace(disabledRaw)
            ? new List<string>()
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(disabledRaw) ?? new List<string>();
    }
    catch
    {
        disabledFeatures = new List<string>();
    }

    r.Close();

    return Results.Ok(new
    {
        username = target,
        accountAgeDays = (int)(DateTime.UtcNow - createdAt).TotalDays,
        trust = new
        {
            composite = Math.Round(composite, 1),
            level = TrustEngine.LevelToString(trustLevel),
            dimensions = new
            {
                identity = Math.Round(dims.Identity, 1),
                behavior = Math.Round(dims.Behavior, 1),
                device = Math.Round(dims.Device, 1)
            }
        },
        overrides = new
        {
            minTrust = overrideMin,
            maxTrust = overrideMax,
            forceBlock = forceBlock,
            disabledFeatures = disabledFeatures
        }
    });
});


// ═══════════════════════════════════════════════
// SECURITY CENTER API
// ═══════════════════════════════════════════════

app.MapGet("/api/admin/security/user/{username}", (string username, HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    var target = (username ?? "").Trim().ToLowerInvariant();
    using var db = DbHelpers.OpenDb();
    // User base info
    using var uCmd = db.CreateCommand();
    uCmd.CommandText = @"SELECT Id, Username, Email, Status, CreatedAt, LastLoginAt, LastLoginIp,
        EmailVerified, TwoFactorEnabled, TrustLevel, TrustScore, IsSuspended, SuspendedReason
        FROM AuthUsers WHERE Username=$u LIMIT 1";
    uCmd.Parameters.AddWithValue("$u", target);
    using var uR = uCmd.ExecuteReader();
    if (!uR.Read()) return Results.NotFound(new { message = "Användaren hittades inte." });
    var userId = uR.GetInt32(0);
    var userInfo = new
    {
        id = userId,
        username = uR.GetString(1),
        email = uR.IsDBNull(2) ? "" : uR.GetString(2),
        status = uR.IsDBNull(3) ? "" : uR.GetString(3),
        createdAt = uR.IsDBNull(4) ? "" : uR.GetString(4),
        lastLoginAt = uR.IsDBNull(5) ? "" : uR.GetString(5),
        lastLoginIp = uR.IsDBNull(6) ? "" : uR.GetString(6),
        emailVerified = !uR.IsDBNull(7) && uR.GetInt32(7) == 1,
        twoFactorEnabled = !uR.IsDBNull(8) && uR.GetInt32(8) == 1,
        trustLevel = uR.IsDBNull(9) ? "medium" : uR.GetString(9),
        trustScore = uR.IsDBNull(10) ? 50 : uR.GetInt32(10),
        isSuspended = !uR.IsDBNull(11) && uR.GetInt32(11) == 1,
        suspendedReason = uR.IsDBNull(12) ? "" : uR.GetString(12),
    };
    uR.Close();
    // Trust history
    var trustHistory = new List<object>();
    using var thCmd = db.CreateCommand();
    thCmd.CommandText = "SELECT TrustLevel, TrustScore, Reason, CreatedAt FROM TrustHistory WHERE UserId=$uid ORDER BY Id DESC LIMIT 20";
    thCmd.Parameters.AddWithValue("$uid", userId);
    using var thR = thCmd.ExecuteReader();
    while (thR.Read()) trustHistory.Add(new { level = thR.GetString(0), score = thR.GetInt32(1), reason = thR.IsDBNull(2) ? "" : thR.GetString(2), at = thR.GetString(3) });
    thR.Close();
    // Active penalties
    var penalties = new List<object>();
    using var pCmd = db.CreateCommand();
    pCmd.CommandText = "SELECT EventType, PenaltyPoints, ExpiresAt, CreatedAt FROM TrustPenalties WHERE UserId=$uid AND (ExpiresAt IS NULL OR ExpiresAt > datetime('now')) ORDER BY Id DESC LIMIT 10";
    pCmd.Parameters.AddWithValue("$uid", userId);
    using var pR = pCmd.ExecuteReader();
    while (pR.Read()) penalties.Add(new { type = pR.GetString(0), points = pR.GetInt32(1), expiresAt = pR.IsDBNull(2) ? "" : pR.GetString(2), createdAt = pR.GetString(3) });
    pR.Close();
    // Security events
    var events = new List<object>();
    using var evCmd = db.CreateCommand();
    evCmd.CommandText = "SELECT EventType, Severity, Detail, IpAddress, CreatedAt FROM SecurityEvents WHERE UserId=$uid ORDER BY Id DESC LIMIT 20";
    evCmd.Parameters.AddWithValue("$uid", userId);
    using var evR = evCmd.ExecuteReader();
    while (evR.Read()) events.Add(new { type = evR.GetString(0), severity = evR.GetString(1), detail = evR.IsDBNull(2) ? "" : evR.GetString(2), ip = evR.IsDBNull(3) ? "" : evR.GetString(3), at = evR.GetString(4) });
    evR.Close();
    // Firewall blocks
    var fwBlocks = new List<object>();
    using var fwCmd = db.CreateCommand();
    fwCmd.CommandText = "SELECT RuleMatched, TrustLevel, IpAddress, BlockedAt FROM FirewallBlocks WHERE UserId=$uid ORDER BY Id DESC LIMIT 20";
    fwCmd.Parameters.AddWithValue("$uid", userId);
    using var fwR = fwCmd.ExecuteReader();
    while (fwR.Read()) fwBlocks.Add(new { rule = fwR.GetString(0), trust = fwR.GetString(1), ip = fwR.IsDBNull(2) ? "" : fwR.GetString(2), at = fwR.GetString(3) });
    fwR.Close();
    // File uploads
    var uploads = new List<object>();
    using var upCmd = db.CreateCommand();
    upCmd.CommandText = "SELECT OriginalName, Extension, FileSizeBytes, Status, RejectionReason, IpAddress, UploadedAt FROM FileUploads WHERE UserId=$uid ORDER BY Id DESC LIMIT 20";
    upCmd.Parameters.AddWithValue("$uid", userId);
    using var upR = upCmd.ExecuteReader();
    while (upR.Read()) uploads.Add(new { name = upR.GetString(0), ext = upR.GetString(1), size = upR.GetInt64(2), status = upR.GetString(3), reason = upR.IsDBNull(4) ? "" : upR.GetString(4), ip = upR.IsDBNull(5) ? "" : upR.GetString(5), at = upR.GetString(6) });
    upR.Close();
    // Devices
    var devices = new List<object>();
    using var dvCmd = db.CreateCommand();
    dvCmd.CommandText = @"SELECT dt.DeviceToken, dt.DeviceName, dt.UserAgent, dt.IpAddress, dt.FirstSeenAt, dt.LastSeenAt, dt.IsTrusted, dt.RiskFlags, dt.RiskScore, dt.SeenIpCount, dt.SessionCount, dt.MaturedAt,
        (SELECT COUNT(DISTINCT UserId) FROM DeviceAccountLinks WHERE DeviceToken=dt.DeviceToken) as SharedAccounts
        FROM DeviceTokens dt WHERE dt.UserId=$uid ORDER BY dt.LastSeenAt DESC LIMIT 10";
    dvCmd.Parameters.AddWithValue("$uid", userId);
    using var dvR = dvCmd.ExecuteReader();
    while (dvR.Read()) devices.Add(new
    {
        token = dvR.GetString(0).Length > 12 ? dvR.GetString(0)[..12] + "…" : dvR.GetString(0),
        name = dvR.GetString(1),
        ua = dvR.IsDBNull(2) ? "" : dvR.GetString(2),
        ip = dvR.IsDBNull(3) ? "" : dvR.GetString(3),
        firstSeen = dvR.GetString(4),
        lastSeen = dvR.GetString(5),
        trusted = dvR.GetInt32(6) == 1,
        riskFlags = dvR.IsDBNull(7) ? "[]" : dvR.GetString(7),
        riskScore = dvR.IsDBNull(8) ? 0 : dvR.GetInt32(8),
        seenIpCount = dvR.IsDBNull(9) ? 1 : dvR.GetInt32(9),
        sessionCount = dvR.IsDBNull(10) ? 1 : dvR.GetInt32(10),
        maturedAt = dvR.IsDBNull(11) ? null : dvR.GetString(11),
        sharedAccounts = dvR.IsDBNull(12) ? 1 : dvR.GetInt32(12)
    });
    dvR.Close();
    return Results.Ok(new { user = userInfo, trustHistory, penalties, events, firewallBlocks = fwBlocks, uploads, devices });
});

app.MapGet("/api/admin/security/events", (HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    var events = new List<object>();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT se.EventType, se.Severity, se.Detail, se.IpAddress, se.CreatedAt, au.Username
        FROM SecurityEvents se LEFT JOIN AuthUsers au ON se.UserId = au.Id
        ORDER BY se.Id DESC LIMIT 50";
    using var r = cmd.ExecuteReader();
    while (r.Read()) events.Add(new { type = r.GetString(0), severity = r.GetString(1), detail = r.IsDBNull(2) ? "" : r.GetString(2), ip = r.IsDBNull(3) ? "" : r.GetString(3), at = r.GetString(4), username = r.IsDBNull(5) ? "" : r.GetString(5) });
    return Results.Ok(events);
});

app.MapGet("/api/admin/security/firewall", (HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    var blocks = new List<object>();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT fb.RuleMatched, fb.TrustLevel, fb.IpAddress, fb.BlockedAt, au.Username
        FROM FirewallBlocks fb LEFT JOIN AuthUsers au ON fb.UserId = au.Id
        ORDER BY fb.Id DESC LIMIT 50";
    using var r = cmd.ExecuteReader();
    while (r.Read()) blocks.Add(new { rule = r.GetString(0), trust = r.GetString(1), ip = r.IsDBNull(2) ? "" : r.GetString(2), at = r.GetString(3), username = r.IsDBNull(4) ? "" : r.GetString(4) });
    return Results.Ok(blocks);
});

app.MapPost("/api/admin/security/suspend/{target}", async (string target, HttpContext ctx) =>
{
    // rs-admin-security-write-defensive-v1
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_security_write", 60, 300))
        return Results.Json(new { message = "Too many admin actions." }, statusCode: 429);

    SuspendReq? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<SuspendReq>();
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    var t = DefensiveInput.CleanString(target, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(t) || !AppHelpers.IsValidUsername(t))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(t)) return Results.NotFound();

    var reason = DefensiveInput.CleanString(body?.Reason, 500);
    if (string.IsNullOrWhiteSpace(reason))
        reason = "Admin suspension";

    using var db = DbHelpers.OpenDb();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET IsSuspended=1, SuspendedReason=$r WHERE Username=$u";
    cmd.Parameters.AddWithValue("$r", reason);
    cmd.Parameters.AddWithValue("$u", t);
    cmd.ExecuteNonQuery();

    using var evtCmd = db.CreateCommand();
    evtCmd.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail, CreatedAt)
        SELECT Id, 'admin_suspend', 'alert', $d, datetime('now') FROM AuthUsers WHERE Username=$u";
    evtCmd.Parameters.AddWithValue("$d", "Suspended by admin. Reason: " + reason);
    evtCmd.Parameters.AddWithValue("$u", t);
    evtCmd.ExecuteNonQuery();

    AppHelpers.LogActivity(admin, "admin_security_suspend", "Target=" + t);

    return Results.Ok(new { status = "suspended", username = t });
});

app.MapPost("/api/admin/security/unsuspend/{target}", (string target, HttpContext ctx) =>
{
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_security_write", 60, 300))
        return Results.Json(new { message = "Too many admin actions." }, statusCode: 429);

    var t = DefensiveInput.CleanString(target, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(t) || !AppHelpers.IsValidUsername(t))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(t)) return Results.NotFound();

    using var db = DbHelpers.OpenDb();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET IsSuspended=0, SuspendedReason=NULL WHERE Username=$u";
    cmd.Parameters.AddWithValue("$u", t);
    var changed = cmd.ExecuteNonQuery();

    using var evtCmd = db.CreateCommand();
    evtCmd.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail, CreatedAt)
        SELECT Id, 'admin_unsuspend', 'info', $d, datetime('now') FROM AuthUsers WHERE Username=$u";
    evtCmd.Parameters.AddWithValue("$d", "Unsuspended by admin.");
    evtCmd.Parameters.AddWithValue("$u", t);
    evtCmd.ExecuteNonQuery();

    AppHelpers.LogActivity(admin, "admin_security_unsuspend", "Target=" + t);

    return Results.Ok(new { status = "unsuspended", username = t, changed });
});

app.MapPost("/api/admin/security/set-trust/{target}", async (string target, HttpContext ctx) =>
{
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_security_write", 60, 300))
        return Results.Json(new { message = "Too many admin actions." }, statusCode: 429);

    SetTrustReq? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<SetTrustReq>();
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON request." });
    }

    var t = DefensiveInput.CleanString(target, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(t) || !AppHelpers.IsValidUsername(t))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(t)) return Results.NotFound();

    var level = DefensiveInput.CleanString(body?.Level, 20).ToLowerInvariant();
    if (level is not ("low" or "medium" or "high" or "blocked"))
        level = "medium";

    var score = Math.Clamp(body?.Score ?? 50, 0, 100);

    using var db = DbHelpers.OpenDb();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET TrustLevel=$lvl, TrustScore=$score WHERE Username=$u";
    cmd.Parameters.AddWithValue("$lvl", level);
    cmd.Parameters.AddWithValue("$score", score);
    cmd.Parameters.AddWithValue("$u", t);
    cmd.ExecuteNonQuery();

    using var histCmd = db.CreateCommand();
    histCmd.CommandText = @"INSERT INTO TrustHistory (UserId, TrustLevel, TrustScore, Reason, CreatedAt)
        SELECT Id, $lvl, $score, 'admin_override', datetime('now') FROM AuthUsers WHERE Username=$u";
    histCmd.Parameters.AddWithValue("$lvl", level);
    histCmd.Parameters.AddWithValue("$score", score);
    histCmd.Parameters.AddWithValue("$u", t);
    histCmd.ExecuteNonQuery();

    AppHelpers.LogActivity(admin, "admin_security_set_trust", $"Target={t} Level={level} Score={score}");

    return Results.Ok(new { status = "ok", username = t, level, score });
});

app.MapPost("/api/admin/security/clear-penalties/{target}", (string target, HttpContext ctx) =>
{
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_security_write", 60, 300))
        return Results.Json(new { message = "Too many admin actions." }, statusCode: 429);

    var t = DefensiveInput.CleanString(target, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(t) || !AppHelpers.IsValidUsername(t))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(t)) return Results.NotFound();

    using var db = DbHelpers.OpenDb();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM TrustPenalties WHERE UserId=(SELECT Id FROM AuthUsers WHERE Username=$u)";
    cmd.Parameters.AddWithValue("$u", t);
    var deleted = cmd.ExecuteNonQuery();

    AppHelpers.LogActivity(admin, "admin_security_clear_penalties", "Target=" + t);

    return Results.Ok(new { status = "ok", username = t, deletedPenalties = deleted });
});

app.MapPost("/api/admin/security/reset-devices/{target}", (string target, HttpContext ctx) =>
{
    var admin = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    if (!AppHelpers.IsAdmin(admin)) return Results.Forbid();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(admin, "admin_security_write", 60, 300))
        return Results.Json(new { message = "Too many admin actions." }, statusCode: 429);

    var t = DefensiveInput.CleanString(target, 32).ToLowerInvariant();
    if (!DefensiveInput.IsUsername(t) || !AppHelpers.IsValidUsername(t))
        return Results.BadRequest(new { message = "Invalid username." });

    if (!AppHelpers.UserExists(t)) return Results.NotFound();

    using var db = DbHelpers.OpenDb();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM DeviceTokens WHERE UserId=(SELECT Id FROM AuthUsers WHERE Username=$u)";
    cmd.Parameters.AddWithValue("$u", t);
    var deleted = cmd.ExecuteNonQuery();

    AppHelpers.LogActivity(admin, "admin_security_reset_devices", "Target=" + t);

    return Results.Ok(new { status = "ok", username = t, deletedDevices = deleted });
});


// ═══════════════════════════════════════════════
// SUPPORT TICKETS
// ═══════════════════════════════════════════════

app.MapPost("/api/support/ticket", async (HttpContext ctx) =>
{
    var body = await ctx.Request.ReadFromJsonAsync<SupportTicketReq>();
    if (body == null) return Results.BadRequest(new { message = "Invalid request." });

    // rs-support-ticket-defensive-v1
    // Public endpoint: keep all user-provided text bounded and predictable.
    var authUser = ctx.User.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
    var username = DefensiveInput.CleanString(body.Username, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        username = string.IsNullOrWhiteSpace(authUser) ? "guest" : authUser;
    if (username != "guest" && !DefensiveInput.IsUsername(username))
        username = "guest";

    var category = DefensiveInput.CleanString(body.Category, 50);
    var subject = DefensiveInput.CleanString(body.Subject, 160);
    var desc = DefensiveInput.CleanString(body.Description, 2000);

    if (category.Length == 0) category = "General";
    if (category.Contains('\0') || subject.Contains('\0') || desc.Contains('\0'))
        return Results.BadRequest(new { message = "Invalid request." });

    var isLiveCall = category.Equals("Live call", StringComparison.OrdinalIgnoreCase);

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    if (isLiveCall)
    {
        if (!limiter.IsAllowed(ip, "support_live_call", 2, 3600))
            return Results.Json(new { message = "Too many live support requests. Please wait an hour." }, statusCode: 429);
    }
    else
    {
        if (!limiter.IsAllowed(ip, "support_ticket", 3, 3600))
            return Results.Json(new { message = "Too many tickets. Please wait an hour." }, statusCode: 429);
    }

    if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(desc))
        return Results.BadRequest(new { message = "Subject and description required." });

    if (ContentFilter.IsOffensive(subject) || ContentFilter.IsOffensive(desc))
        return Results.BadRequest(new { message = "Otillåtet innehåll." });

    var ticketId = "RS-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()[^5..] +
                   "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2));

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"INSERT INTO SupportTickets (TicketId, Username, Category, Subject, Description, Status, CreatedAt)
        VALUES ($tid, $u, $cat, $sub, $desc, 'open', datetime('now'))";
    cmd.Parameters.AddWithValue("$tid", ticketId);
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$cat", category);
    cmd.Parameters.AddWithValue("$sub", subject);
    cmd.Parameters.AddWithValue("$desc", desc);
    cmd.ExecuteNonQuery();

    // Notify admins via SignalR
    var hubContext = ctx.RequestServices.GetRequiredService<IHubContext<ChatHub>>();
    var ticketAdmins = new List<string>();
    using (var _tdb = DbHelpers.OpenDb()) { using var _tc2 = _tdb.CreateCommand(); _tc2.CommandText = "SELECT Username FROM AuthUsers WHERE IsAdmin=1"; using var _tr2 = _tc2.ExecuteReader(); while (_tr2.Read()) ticketAdmins.Add(_tr2.GetString(0)); }
    foreach (var admin in ticketAdmins)
    {
        await hubContext.Clients.User(admin).SendAsync("ReceiveMessage", new
        {
            fromUser = "system",
            message = isLiveCall
                ? $"Incoming live support request from {username} ({ticketId})"
                : $"New support ticket: {subject}",
            category = category,
            ticketId = ticketId,
            supportType = isLiveCall ? "live_call" : "ticket",
            createdAt = DateTime.UtcNow.ToString("o")
        });
    }
    return Results.Ok(new { ticketId, status = "open", message = "Ticket created." });
});


// LIVE SUPPORT CALL STATUS
app.MapPost("/api/support/live-call/{ticketId}/status", async (string ticketId, HttpContext ctx) =>
{
    if (ctx.User?.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var me = ctx.User.Identity?.Name ?? "";
    using var db = DbHelpers.OpenDb();

    using (var ac = db.CreateCommand())
    {
        ac.CommandText = "SELECT IsAdmin FROM AuthUsers WHERE lower(Username)=lower($u) LIMIT 1";
        ac.Parameters.AddWithValue("$u", me);
        var isAdmin = Convert.ToInt32(ac.ExecuteScalar() ?? 0) == 1;
        if (!isAdmin) return Results.Forbid();
    }

    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var status = body != null && body.TryGetValue("status", out var st) ? (st ?? "").Trim().ToLowerInvariant() : "";

    var allowed = new HashSet<string> { "open", "in_call", "ended", "declined" };
    if (!allowed.Contains(status))
        return Results.BadRequest(new { message = "Invalid status." });

    string ticketUser = "";
    using (var getUser = db.CreateCommand())
    {
        getUser.CommandText = "SELECT Username FROM SupportTickets WHERE TicketId=$tid AND Category='Live call' LIMIT 1";
        getUser.Parameters.AddWithValue("$tid", ticketId);
        ticketUser = Convert.ToString(getUser.ExecuteScalar() ?? "") ?? "";
    }

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        UPDATE SupportTickets
        SET Status=$status
        WHERE TicketId=$tid
          AND Category='Live call'";
    cmd.Parameters.AddWithValue("$status", status);
    cmd.Parameters.AddWithValue("$tid", ticketId);
    var rows = cmd.ExecuteNonQuery();

    if (rows == 0)
        return Results.NotFound(new { message = "Live call ticket not found." });

    var hubContext = ctx.RequestServices.GetRequiredService<IHubContext<ChatHub>>();
    if (!string.IsNullOrWhiteSpace(ticketUser))
    {
        await hubContext.Clients.User(ticketUser).SendAsync("LiveCallStatus", new
        {
            ticketId = ticketId,
            status = status,
            agent = me,
            message = status == "in_call"
                ? $"Support accepted your live call ({ticketId})"
                : $"Live support call status changed to {status}"
        });
    }

    return Results.Ok(new { ok = true, ticketId, status, user = ticketUser });
});


// ═══════════════════════════════════════════════
// DISCORD SUPPORT — sends DM to support team
// ═══════════════════════════════════════════════
app.MapPost("/api/support/discord-send", async (HttpContext ctx, IHttpClientFactory httpFactory) =>
{
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    if (!limiter.IsAllowed(ip, "discord_support", 5, 3600))
        return Results.Json(new { message = "Too many requests. Please wait a while." }, statusCode: 429);

    DiscordSupportDto? body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<DiscordSupportDto>();
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid request body." });
    }

    if (body == null || string.IsNullOrWhiteSpace(body.Username))
        return Results.BadRequest(new { message = "Username is required." });

    var username = InputSanitizer.SanitizeInput(body.Username.Trim(), 50);
    var category = InputSanitizer.SanitizeInput(body.Category?.Trim() ?? "", 100);
    var description = InputSanitizer.SanitizeInput(body.Description?.Trim() ?? "", 1500);

    if (string.IsNullOrEmpty(category) && string.IsNullOrEmpty(description))
        return Results.BadRequest(new { message = "Category or description is required." });

    username = System.Text.RegularExpressions.Regex.Replace(username, @"@everyone|@here|<@[!&]?\d+>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    description = System.Text.RegularExpressions.Regex.Replace(description, @"@everyone|@here|<@[!&]?\d+>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    var submittedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
    var messageContent =
        $"\ud83c\udfab **New RunSpace Support Request**\n" +
        $"**Username:** {username}\n" +
        (string.IsNullOrEmpty(category) ? "" : $"**Category:** {category}\n") +
        $"**Submitted:** {submittedAt}\n" +
        (string.IsNullOrEmpty(description) ? "" : $"\n**Issue:**\n{description}");

    var botToken = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
    if (string.IsNullOrWhiteSpace(botToken))
    {
        Console.WriteLine("[Discord Support] integration not configured.");
        return Results.Json(new { message = "Discord support is not configured." }, statusCode: 503);
    }

    var supportUsers = new[]
    {
        "1474602196409651230", // Nulligit (primary)
        "1424751033162399827"  // Solumverum (fallback)
    };

    var http = httpFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", botToken);
    http.DefaultRequestHeaders.UserAgent.ParseAdd("RunSpace (https://runspace.cloud, 1.0)");

    async Task<string?> OpenDmChannelAsync(string userId)
    {
        try
        {
            var dmPayload = new { recipient_id = userId };
            var dmJson = JsonSerializer.Serialize(dmPayload);
            var dmContent = new StringContent(dmJson, Encoding.UTF8, "application/json");
            using var dmResp = await http.PostAsync("https://discord.com/api/v10/users/@me/channels", dmContent);
            if (!dmResp.IsSuccessStatusCode)
            {
                var err = await dmResp.Content.ReadAsStringAsync();
                Console.WriteLine($"[Discord Support] Cannot open DM with {userId}: {(int)dmResp.StatusCode} {err}");
                return null;
            }
            var dmBody = await dmResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(dmBody);
            return doc.RootElement.GetProperty("id").GetString();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Discord Support] OpenDM exception.");
            return null;
        }
    }

    async Task<bool> SendMessageAsync(string channelId, string messageText)
    {
        try
        {
            var payload = new
            {
                content = messageText,
                allowed_mentions = new { parse = Array.Empty<string>() }
            };
            var json = JsonSerializer.Serialize(payload);
            var messageBody = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync($"https://discord.com/api/v10/channels/{channelId}/messages", messageBody);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                Console.WriteLine($"[Discord Support] Send failed: {(int)resp.StatusCode} {err}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Discord Support] Send exception.");
            return false;
        }
    }

    foreach (var userId in supportUsers)
    {
        var channelId = await OpenDmChannelAsync(userId);
        if (channelId == null) continue;

        var sent = await SendMessageAsync(channelId, messageContent);
        if (sent)
        {
            Console.WriteLine($"[Discord Support] Delivered to {userId} from @{username}");
            AppHelpers.LogActivity(username, "discord_support_sent", $"Delivered to {userId}");
            return Results.Ok(new { success = true });
        }
    }

    Console.WriteLine($"[Discord Support] All support users unavailable");
    return Results.Json(new { message = "Could not deliver message. Please try again later." }, statusCode: 502);
});

// ── SUPPORT INBOX ──
app.MapGet("/api/support/my-tickets", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Unauthorized();
    var tickets = new List<object>();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT TicketId, Category, Subject, Status, Priority, CreatedAt, UpdatedAt, UnreadByUser FROM SupportTickets WHERE LOWER(Username) = $u ORDER BY COALESCE(UpdatedAt, CreatedAt) DESC";
    cmd.Parameters.AddWithValue("$u", user);
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        tickets.Add(new { ticketId = r.GetString(0), category = r.GetString(1), subject = r.GetString(2), status = r.GetString(3), priority = r.IsDBNull(4) ? "normal" : r.GetString(4), createdAt = r.GetString(5), updatedAt = r.IsDBNull(6) ? r.GetString(5) : r.GetString(6), unread = r.GetInt32(7) });
    }
    return Results.Ok(new { tickets });
});

app.MapGet("/api/support/unread-count", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Ok(new { count = 0 });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT COALESCE(SUM(UnreadByUser), 0) FROM SupportTickets WHERE LOWER(Username) = $u";
    cmd.Parameters.AddWithValue("$u", user);
    return Results.Ok(new { count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0) });
});

app.MapGet("/api/support/tickets/{ticketId}", (string ticketId, HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    object? ticket = null;
    string? ownerUsername = null;
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = @"SELECT TicketId, Username, Category, Subject, Description, Status, Priority, CreatedAt, UpdatedAt, AssignedTo FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", ticketId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Results.NotFound(new { message = "Ticket not found." });
        ownerUsername = r.GetString(1).ToLowerInvariant();
        if (ownerUsername != user && !AppHelpers.IsAdmin(user)) return Results.Forbid();
        ticket = new { ticketId = r.GetString(0), username = r.GetString(1), category = r.GetString(2), subject = r.GetString(3), description = r.GetString(4), status = r.GetString(5), priority = r.IsDBNull(6) ? "normal" : r.GetString(6), createdAt = r.GetString(7), updatedAt = r.IsDBNull(8) ? r.GetString(7) : r.GetString(8), assignedTo = r.IsDBNull(9) ? null : r.GetString(9) };
    }
    var messages = new List<object>();
    using (var cmd2 = db.CreateCommand())
    {
        cmd2.CommandText = @"SELECT Id, FromUsername, Message, IsAdminReply, CreatedAt FROM SupportMessages WHERE TicketId = $t ORDER BY CreatedAt ASC";
        cmd2.Parameters.AddWithValue("$t", ticketId);
        using var r2 = cmd2.ExecuteReader();
        while (r2.Read())
        {
            messages.Add(new { id = r2.GetInt64(0), fromUsername = r2.GetString(1), message = r2.GetString(2), isAdminReply = r2.GetInt32(3) == 1, createdAt = r2.GetString(4) });
        }
    }
    using (var upd = db.CreateCommand())
    {
        if (ownerUsername == user) upd.CommandText = "UPDATE SupportTickets SET UnreadByUser = 0 WHERE TicketId = $t";
        else upd.CommandText = "UPDATE SupportTickets SET UnreadByAdmin = 0 WHERE TicketId = $t";
        upd.Parameters.AddWithValue("$t", ticketId);
        upd.ExecuteNonQuery();
    }
    return Results.Ok(new { ticket, messages });
});

app.MapPost("/api/support/tickets/{ticketId}/reply", async (string ticketId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user)) return Results.Unauthorized();

    // rs-support-user-reply-defensive-v1
    if (!DefensiveInput.IsSupportTicketId(ticketId))
        return Results.BadRequest(new { message = "Invalid ticket id." });
    ticketId = ticketId.Trim().ToUpperInvariant();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(user, "support_reply", 20, 3600))
        return Results.Json(new { message = "Too many replies. Please wait a while." }, statusCode: 429);
    SupportReplyDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportReplyDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }
    var msg = DefensiveInput.CleanString(body?.Message, 4000);
    if (string.IsNullOrWhiteSpace(msg)) return Results.BadRequest(new { message = "Message is required." });
    using var db = DbHelpers.OpenDb();
    string? owner = null; string? currentStatus = null;
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT LOWER(Username), Status FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", ticketId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return Results.NotFound(new { message = "Ticket not found." });
        owner = r.GetString(0); currentStatus = r.GetString(1);
    }
    if (owner != user) return Results.Forbid();
    if (currentStatus == "closed") return Results.BadRequest(new { message = "This ticket is closed." });
    var now = DateTime.UtcNow.ToString("o");
    using (var ins = db.CreateCommand())
    {
        ins.CommandText = "INSERT INTO SupportMessages (TicketId, FromUsername, Message, IsAdminReply, CreatedAt) VALUES ($t, $u, $m, 0, $c)";
        ins.Parameters.AddWithValue("$t", ticketId);
        ins.Parameters.AddWithValue("$u", user);
        ins.Parameters.AddWithValue("$m", msg);
        ins.Parameters.AddWithValue("$c", now);
        ins.ExecuteNonQuery();
    }
    using (var upd = db.CreateCommand())
    {
        upd.CommandText = @"UPDATE SupportTickets SET UnreadByAdmin = 1, UpdatedAt = $now, Status = CASE WHEN Status = 'waiting_for_user' THEN 'in_progress' ELSE Status END WHERE TicketId = $t";
        upd.Parameters.AddWithValue("$now", now);
        upd.Parameters.AddWithValue("$t", ticketId);
        upd.ExecuteNonQuery();
    }
    var adminList = new List<string>();
    using (var ac = db.CreateCommand())
    {
        ac.CommandText = "SELECT LOWER(Username) FROM AuthUsers WHERE IsAdmin = 1";
        using var ar = ac.ExecuteReader();
        while (ar.Read()) adminList.Add(ar.GetString(0));
    }
    var payload = new { ticketId, fromUsername = user, isAdminReply = false, createdAt = now };
    foreach (var admin in adminList)
    {
        try { await hub.Clients.User(admin).SendAsync("SupportAdminUpdate", payload); } catch { }
    }
    AppHelpers.LogActivity(user, "support_reply", $"Replied to ticket {ticketId}");
    return Results.Ok(new { success = true });
});

app.MapGet("/api/admin/support/inbox", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();
    var status = ctx.Request.Query["status"].ToString()?.Trim().ToLowerInvariant() ?? "";
    var assignedTo = ctx.Request.Query["assignedTo"].ToString()?.Trim().ToLowerInvariant() ?? "";
    var search = ctx.Request.Query["search"].ToString()?.Trim() ?? "";
    var tickets = new List<object>();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    var sql = "SELECT TicketId, Username, Category, Subject, Status, Priority, CreatedAt, UpdatedAt, AssignedTo, UnreadByAdmin FROM SupportTickets WHERE 1=1";
    if (!string.IsNullOrEmpty(status) && status != "all") { sql += " AND Status = $status"; cmd.Parameters.AddWithValue("$status", status); }
    if (!string.IsNullOrEmpty(assignedTo))
    {
        if (assignedTo == "unassigned") sql += " AND (AssignedTo IS NULL OR AssignedTo = '')";
        else if (assignedTo == "me") { sql += " AND LOWER(AssignedTo) = $me"; cmd.Parameters.AddWithValue("$me", user); }
        else { sql += " AND LOWER(AssignedTo) = $assignedTo"; cmd.Parameters.AddWithValue("$assignedTo", assignedTo); }
    }
    if (!string.IsNullOrEmpty(search))
    {
        sql += " AND (LOWER(Username) LIKE $s OR LOWER(Subject) LIKE $s OR LOWER(TicketId) LIKE $s)";
        cmd.Parameters.AddWithValue("$s", "%" + search.ToLowerInvariant() + "%");
    }
    sql += " ORDER BY UnreadByAdmin DESC, COALESCE(UpdatedAt, CreatedAt) DESC LIMIT 200";
    cmd.CommandText = sql;
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        tickets.Add(new { ticketId = r.GetString(0), username = r.GetString(1), category = r.GetString(2), subject = r.GetString(3), status = r.GetString(4), priority = r.IsDBNull(5) ? "normal" : r.GetString(5), createdAt = r.GetString(6), updatedAt = r.IsDBNull(7) ? r.GetString(6) : r.GetString(7), assignedTo = r.IsDBNull(8) ? null : r.GetString(8), unread = r.GetInt32(9) });
    }
    return Results.Ok(new { tickets });
});

app.MapGet("/api/admin/support/stats", (HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT COUNT(*) FILTER (WHERE Status = 'open'), COUNT(*) FILTER (WHERE Status = 'in_progress'), COUNT(*) FILTER (WHERE Status = 'waiting_for_user'), COUNT(*) FILTER (WHERE Status = 'resolved'), COUNT(*) FILTER (WHERE Status = 'closed'), COALESCE(SUM(UnreadByAdmin),0), COUNT(*) FILTER (WHERE AssignedTo IS NULL OR AssignedTo = ''), COUNT(*) FILTER (WHERE LOWER(AssignedTo) = $me) FROM SupportTickets";
    cmd.Parameters.AddWithValue("$me", user);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.Ok(new { });
    return Results.Ok(new { open = r.GetInt32(0), inProgress = r.GetInt32(1), waiting = r.GetInt32(2), resolved = r.GetInt32(3), closed = r.GetInt32(4), unread = r.GetInt32(5), unassigned = r.GetInt32(6), mine = r.GetInt32(7) });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/reply", async (string ticketId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    // rs-support-admin-reply-defensive-v1
    if (!DefensiveInput.IsSupportTicketId(ticketId))
        return Results.BadRequest(new { message = "Invalid ticket id." });
    ticketId = ticketId.Trim().ToUpperInvariant();

    SupportReplyDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportReplyDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }
    var msg = DefensiveInput.CleanString(body?.Message, 4000);
    if (string.IsNullOrWhiteSpace(msg)) return Results.BadRequest(new { message = "Message is required." });
    using var db = DbHelpers.OpenDb();
    string? targetUser = null;
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "SELECT LOWER(Username) FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd.Parameters.AddWithValue("$t", ticketId);
        var u = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(u)) return Results.NotFound(new { message = "Ticket not found." });
        targetUser = u;
    }
    var now = DateTime.UtcNow.ToString("o");
    using (var ins = db.CreateCommand())
    {
        ins.CommandText = "INSERT INTO SupportMessages (TicketId, FromUsername, Message, IsAdminReply, CreatedAt) VALUES ($t, $u, $m, 1, $c)";
        ins.Parameters.AddWithValue("$t", ticketId);
        ins.Parameters.AddWithValue("$u", user!);
        ins.Parameters.AddWithValue("$m", msg);
        ins.Parameters.AddWithValue("$c", now);
        ins.ExecuteNonQuery();
    }
    using (var upd = db.CreateCommand())
    {
        upd.CommandText = @"UPDATE SupportTickets SET UnreadByUser = UnreadByUser + 1, UpdatedAt = $now, Status = CASE WHEN Status = 'open' THEN 'in_progress' ELSE Status END WHERE TicketId = $t";
        upd.Parameters.AddWithValue("$now", now);
        upd.Parameters.AddWithValue("$t", ticketId);
        upd.ExecuteNonQuery();
    }
    var payload = new { ticketId, fromUsername = user, isAdminReply = true, createdAt = now };
    try { await hub.Clients.User(targetUser).SendAsync("SupportTicketUpdate", payload); } catch { }
    AppHelpers.LogActivity(user!, "support_admin_reply", $"Replied to ticket {ticketId}");
    return Results.Ok(new { success = true });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/status", async (string ticketId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    // rs-support-status-defensive-v1
    if (!DefensiveInput.IsSupportTicketId(ticketId))
        return Results.BadRequest(new { message = "Invalid ticket id." });
    ticketId = ticketId.Trim().ToUpperInvariant();

    SupportStatusDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportStatusDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }
    var status = DefensiveInput.CleanString(body?.Status, 40).ToLowerInvariant();
    var allowed = new[] { "open", "in_progress", "waiting_for_user", "resolved", "closed" };
    if (!allowed.Contains(status)) return Results.BadRequest(new { message = "Invalid status." });
    var now = DateTime.UtcNow.ToString("o");
    string? targetUser = null;
    using var db = DbHelpers.OpenDb();
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "UPDATE SupportTickets SET Status = $s, UpdatedAt = $now, UnreadByUser = UnreadByUser + 1 WHERE TicketId = $t";
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$t", ticketId);
        cmd.ExecuteNonQuery();
    }
    using (var cmd2 = db.CreateCommand())
    {
        cmd2.CommandText = "SELECT LOWER(Username) FROM SupportTickets WHERE TicketId = $t LIMIT 1";
        cmd2.Parameters.AddWithValue("$t", ticketId);
        targetUser = cmd2.ExecuteScalar() as string;
    }
    if (targetUser == null) return Results.NotFound(new { message = "Ticket not found." });
    try { await hub.Clients.User(targetUser).SendAsync("SupportTicketUpdate", new { ticketId, statusChanged = true, newStatus = status }); } catch { }
    AppHelpers.LogActivity(user!, "support_status_change", $"Ticket {ticketId} -> {status}");
    return Results.Ok(new { success = true, status });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/assign", async (string ticketId, HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    // rs-support-assign-defensive-v1
    if (!DefensiveInput.IsSupportTicketId(ticketId))
        return Results.BadRequest(new { message = "Invalid ticket id." });
    ticketId = ticketId.Trim().ToUpperInvariant();

    SupportAssignDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportAssignDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }
    var target = DefensiveInput.CleanString(body?.AssignedTo, 32).ToLowerInvariant();
    if (target == "me") target = user!;
    if (!string.IsNullOrWhiteSpace(target) && !DefensiveInput.IsUsername(target))
        return Results.BadRequest(new { message = "Invalid assignee." });
    if (!string.IsNullOrEmpty(target) && !AppHelpers.IsAdmin(target))
        return Results.BadRequest(new { message = "Can only assign to admins." });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE SupportTickets SET AssignedTo = $a, UpdatedAt = $now WHERE TicketId = $t";
    cmd.Parameters.AddWithValue("$a", string.IsNullOrEmpty(target) ? (object)DBNull.Value : target);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$t", ticketId);
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) return Results.NotFound(new { message = "Ticket not found." });
    AppHelpers.LogActivity(user!, "support_assign", $"Ticket {ticketId} -> {target}");
    return Results.Ok(new { success = true, assignedTo = string.IsNullOrEmpty(target) ? null : target });
});

app.MapPost("/api/admin/support/tickets/{ticketId}/priority", async (string ticketId, HttpContext ctx) =>
{
    var user = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(user)) return Results.Forbid();

    // rs-support-priority-defensive-v1
    if (!DefensiveInput.IsSupportTicketId(ticketId))
        return Results.BadRequest(new { message = "Invalid ticket id." });
    ticketId = ticketId.Trim().ToUpperInvariant();

    SupportPriorityDto? body;
    try { body = await ctx.Request.ReadFromJsonAsync<SupportPriorityDto>(); }
    catch { return Results.BadRequest(new { message = "Invalid request." }); }
    var priority = DefensiveInput.CleanString(body?.Priority, 20).ToLowerInvariant();
    var allowed = new[] { "low", "normal", "high", "urgent" };
    if (!allowed.Contains(priority)) return Results.BadRequest(new { message = "Invalid priority." });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE SupportTickets SET Priority = $p, UpdatedAt = $now WHERE TicketId = $t";
    cmd.Parameters.AddWithValue("$p", priority);
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$t", ticketId);
    var affected = cmd.ExecuteNonQuery();
    if (affected == 0) return Results.NotFound(new { message = "Ticket not found." });
    AppHelpers.LogActivity(user!, "support_priority", $"Ticket {ticketId} -> {priority}");
    return Results.Ok(new { success = true, priority });
});

app.MapGet("/api/admin/support/tickets", (HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT TicketId, Username, Category, Subject, Status, CreatedAt FROM SupportTickets ORDER BY Id DESC LIMIT 100";
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read()) rows.Add(new
    {
        ticketId = r.GetString(0),
        username = r.GetString(1),
        category = r.GetString(2),
        subject = r.GetString(3),
        status = r.GetString(4),
        createdAt = r.GetString(5)
    });
    return Results.Ok(rows);
});

// Update ticket status
app.MapPatch("/api/admin/support/ticket/{ticketId}/status", async (string ticketId, HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var status = body?.GetValueOrDefault("status", "open") ?? "open";
    if (!new[] { "open", "pending", "closed" }.Contains(status)) return Results.BadRequest(new { message = "Invalid status" });

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE SupportTickets SET Status=$s WHERE TicketId=$tid";
    cmd.Parameters.AddWithValue("$s", status);
    cmd.Parameters.AddWithValue("$tid", ticketId);
    var rows = cmd.ExecuteNonQuery();
    if (rows == 0) return Results.NotFound(new { message = "Ticket not found" });
    return Results.Ok(new { ticketId, status });
});


// ═══════════════════════════════════════════════
// BOT API
// ═══════════════════════════════════════════════

// Generate or get bot token for a server
app.MapPost("/api/bot/groups/{groupId}/generate-token", (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwner(gid, u)) return Results.Json(new { message = "Endast ägaren kan generera bot-token." }, statusCode: 403);
    using var db = DbHelpers.OpenDb();
    // Ensure BotTokens table exists
    using (var ct = db.CreateCommand())
    {
        ct.CommandText = @"CREATE TABLE IF NOT EXISTS BotTokens (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            GroupId TEXT NOT NULL UNIQUE,
            Token TEXT NOT NULL,
            BotName TEXT NOT NULL DEFAULT 'Min Bot',
            CreatedAt TEXT NOT NULL,
            LastUsedAt TEXT
        )";
        ct.ExecuteNonQuery();
    }
    var token = "rs_bot_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var now = DateTime.UtcNow.ToString("o");
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"INSERT INTO BotTokens (GroupId, Token, BotName, CreatedAt)
        VALUES ($gid, $tok, 'Min Bot', $now)
        ON CONFLICT(GroupId) DO UPDATE SET Token=excluded.Token, CreatedAt=excluded.CreatedAt";
    cmd.Parameters.AddWithValue("$gid", gid);
    cmd.Parameters.AddWithValue("$tok", token);
    cmd.Parameters.AddWithValue("$now", now);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { token, createdAt = now });
});

// Get current bot token info
app.MapGet("/api/bot/groups/{groupId}/token", (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwner(gid, u)) return Results.Json(new { message = "Endast ägaren kan se bot-token." }, statusCode: 403);
    using var db = DbHelpers.OpenDb();
    using (var ct = db.CreateCommand())
    {
        ct.CommandText = @"CREATE TABLE IF NOT EXISTS BotTokens (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            GroupId TEXT NOT NULL UNIQUE,
            Token TEXT NOT NULL,
            BotName TEXT NOT NULL DEFAULT 'Min Bot',
            CreatedAt TEXT NOT NULL,
            LastUsedAt TEXT
        )";
        ct.ExecuteNonQuery();
    }
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Token, BotName, CreatedAt, LastUsedAt FROM BotTokens WHERE GroupId=$gid LIMIT 1";
    cmd.Parameters.AddWithValue("$gid", gid);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.Ok(new { hasToken = false });
    return Results.Ok(new
    {
        hasToken = true,
        token = r.GetString(0),
        botName = r.IsDBNull(1) ? "Min Bot" : r.GetString(1),
        createdAt = r.IsDBNull(2) ? "" : r.GetString(2),
        lastUsedAt = r.IsDBNull(3) ? "" : r.GetString(3)
    });
});

// Verify bot token (called by SDK on startup)
app.MapGet("/api/bot/verify", (HttpContext ctx) =>
{
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bot ")) return Results.Unauthorized();
    var token = authHeader.Substring(4).Trim();
    using var db = DbHelpers.OpenDb();
    using (var ct = db.CreateCommand())
    {
        ct.CommandText = @"CREATE TABLE IF NOT EXISTS BotTokens (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            GroupId TEXT NOT NULL UNIQUE,
            Token TEXT NOT NULL,
            BotName TEXT NOT NULL DEFAULT 'Min Bot',
            CreatedAt TEXT NOT NULL,
            LastUsedAt TEXT
        )";
        ct.ExecuteNonQuery();
    }
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT GroupId, BotName FROM BotTokens WHERE Token=$tok LIMIT 1";
    cmd.Parameters.AddWithValue("$tok", token);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.Unauthorized();
    var gid = r.GetString(0);
    var name = r.IsDBNull(1) ? "Min Bot" : r.GetString(1);
    r.Close();
    // Update last used
    using var upd = db.CreateCommand();
    upd.CommandText = "UPDATE BotTokens SET LastUsedAt=$now WHERE Token=$tok";
    upd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    upd.Parameters.AddWithValue("$tok", token);
    upd.ExecuteNonQuery();
    return Results.Ok(new { valid = true, groupId = gid, botName = name });
});

// Bot sends message to channel
app.MapPost("/api/bot/groups/{groupId}/channels/{channelId}/send", async (string groupId, string channelId, HttpContext ctx, IHubContext<ChatHub> hub) =>
{
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bot ")) return Results.Unauthorized();
    var token = authHeader.Substring(4).Trim();
    using var db = DbHelpers.OpenDb();
    using var verify = db.CreateCommand();
    verify.CommandText = "SELECT GroupId FROM BotTokens WHERE Token=$tok LIMIT 1";
    verify.Parameters.AddWithValue("$tok", token);
    var dbGid = verify.ExecuteScalar() as string;
    if (dbGid == null || dbGid != groupId.Trim().ToLowerInvariant()) return Results.Unauthorized();
    var req = await ctx.Request.ReadFromJsonAsync<GroupMessageReq>();
    var text = InputSanitizer.SanitizeInput(req?.Text ?? "", 2000).Trim();
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { message = "Text krävs." });
    var gid = groupId.Trim().ToLowerInvariant();
    var cid = channelId.Trim().ToLowerInvariant();
    var ts = DateTime.UtcNow.ToString("o");
    long id;
    using (var cmd = db.CreateCommand())
    {
        cmd.CommandText = "INSERT INTO GroupMessages (GroupId,ChannelId,FromUser,Message,Timestamp) VALUES ($g,$c,'[bot]',$m,$t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$g", gid);
        cmd.Parameters.AddWithValue("$c", cid);
        cmd.Parameters.AddWithValue("$m", text);
        cmd.Parameters.AddWithValue("$t", ts);
        id = (long)(cmd.ExecuteScalar() ?? 0L);
    }
    var payload = new { id, groupId = gid, channelId = cid, from = "[bot]", text, ts };
    await hub.Clients.Group($"group:{gid}:{cid}").SendAsync("ReceiveGroupMessage", payload);
    return Results.Ok(new { success = true, id });
});

// Poll events (for bot SDK polling)
app.MapGet("/api/bot/groups/{groupId}/events", (string groupId, long? after, HttpContext ctx) =>
{
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bot ")) return Results.Unauthorized();
    var token = authHeader.Substring(4).Trim();
    using var db = DbHelpers.OpenDb();
    using var verify = db.CreateCommand();
    verify.CommandText = "SELECT GroupId FROM BotTokens WHERE Token=$tok LIMIT 1";
    verify.Parameters.AddWithValue("$tok", token);
    var dbGid = verify.ExecuteScalar() as string;
    if (dbGid == null || dbGid != groupId.Trim().ToLowerInvariant()) return Results.Unauthorized();
    var gid = groupId.Trim().ToLowerInvariant();
    var afterId = after ?? 0;
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT Id, ChannelId, FromUser, Message, Timestamp FROM GroupMessages
        WHERE GroupId=$gid AND Id > $after ORDER BY Id ASC LIMIT 50";
    cmd.Parameters.AddWithValue("$gid", gid);
    cmd.Parameters.AddWithValue("$after", afterId);
    var events = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        events.Add(new
        {
            id = r.GetInt64(0),
            type = "message",
            channelId = r.GetString(1),
            from = r.GetString(2),
            text = r.GetString(3),
            ts = r.GetString(4)
        });
    }
    return Results.Ok(events);
});

// IP blacklist
app.MapPost("/api/bot/groups/{groupId}/blacklist/ip", async (string groupId, HttpContext ctx) =>
{
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bot ")) return Results.Unauthorized();
    var token = authHeader.Substring(4).Trim();
    using var db = DbHelpers.OpenDb();
    using var verify = db.CreateCommand();
    verify.CommandText = "SELECT GroupId FROM BotTokens WHERE Token=$tok LIMIT 1";
    verify.Parameters.AddWithValue("$tok", token);
    var dbGid = verify.ExecuteScalar() as string;
    if (dbGid == null || dbGid != groupId.Trim().ToLowerInvariant()) return Results.Unauthorized();
    using (var ct = db.CreateCommand())
    {
        ct.CommandText = "CREATE TABLE IF NOT EXISTS BotIpBlacklist (Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupId TEXT NOT NULL, IpAddress TEXT NOT NULL, CreatedAt TEXT NOT NULL, UNIQUE(GroupId, IpAddress))";
        ct.ExecuteNonQuery();
    }
    var req = await ctx.Request.ReadFromJsonAsync<IpBlacklistReq>();
    if (string.IsNullOrWhiteSpace(req?.Ip)) return Results.BadRequest(new { message = "IP krävs." });
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT OR IGNORE INTO BotIpBlacklist (GroupId,  CreatedAt) VALUES ($gid,  $now)";
    cmd.Parameters.AddWithValue("$gid", groupId.Trim().ToLowerInvariant());
    cmd.Parameters.AddWithValue("$ip", req.Ip.Trim());
    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

app.MapDelete("/api/bot/groups/{groupId}/blacklist/ip/{ip}", (string groupId, string ip, HttpContext ctx) =>
{
    var authHeader = ctx.Request.Headers.Authorization.ToString();
    if (!authHeader.StartsWith("Bot ")) return Results.Unauthorized();
    var token = authHeader.Substring(4).Trim();
    using var db = DbHelpers.OpenDb();
    using var verify = db.CreateCommand();
    verify.CommandText = "SELECT GroupId FROM BotTokens WHERE Token=$tok LIMIT 1";
    verify.Parameters.AddWithValue("$tok", token);
    var dbGid = verify.ExecuteScalar() as string;
    if (dbGid == null || dbGid != groupId.Trim().ToLowerInvariant()) return Results.Unauthorized();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM BotIpBlacklist WHERE GroupId=$gid AND IpAddress=$ip";
    cmd.Parameters.AddWithValue("$gid", groupId.Trim().ToLowerInvariant());
    cmd.Parameters.AddWithValue("$ip", Uri.UnescapeDataString(ip));
    cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});

// Server icon upload
app.MapPost("/api/groups/{groupId}/icon", async (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    if (!GroupHelpers.IsOwnerOrAdmin(gid, u)) return Results.Forbid();
    if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "form-data krävs" });
    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files["icon"];
    if (file == null || file.Length == 0 || file.Length > 5 * 1024 * 1024)
        return Results.BadRequest(new { error = "Fil saknas eller för stor (max 5MB)." });

    // rs-upload-filename-defensive-v2-group-icon
    var originalName = DefensiveInput.SafeFileName(file.FileName);
    var ext = Path.GetExtension(originalName).ToLowerInvariant();
    if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" }.Contains(ext))
        return Results.BadRequest(new { error = "Ogiltigt format." });
    if (!await FileValidator.IsValidImageAsync(file))
        return Results.BadRequest(new { error = "Ogiltig bildfil." });
    var fn = $"group_{gid}_{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8))}{ext}";
    var path = Path.Combine(AppConfig.AvatarUploadDir, fn);
    await using (var s = System.IO.File.Create(path)) await file.CopyToAsync(s);
    var iconUrl = $"/uploads/avatars/{fn}";
    using var db = DbHelpers.OpenDb();
    // Ensure IconUrl column exists
    DbHelpers.EnsureColumn(db, "Groups", "IconUrl", "TEXT NOT NULL DEFAULT ''");
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE Groups SET IconUrl=$url WHERE GroupId=$gid";
    cmd.Parameters.AddWithValue("$url", iconUrl);
    cmd.Parameters.AddWithValue("$gid", gid);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true, iconUrl });
}).DisableAntiforgery();

// Get server icon
app.MapGet("/api/groups/{groupId}/icon", (string groupId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u)) return Results.Unauthorized();
    var gid = (groupId ?? "").Trim().ToLowerInvariant();
    using var db = DbHelpers.OpenDb();
    DbHelpers.EnsureColumn(db, "Groups", "IconUrl", "TEXT NOT NULL DEFAULT ''");
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT IconUrl FROM Groups WHERE GroupId=$gid LIMIT 1";
    cmd.Parameters.AddWithValue("$gid", gid);
    var icon = cmd.ExecuteScalar() as string ?? "";
    return Results.Ok(new { iconUrl = icon });
});


// ── Admin: rensa gamla krypterade meddelanden ──
app.MapPost("/api/admin/clear-encrypted-messages", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (!AppHelpers.IsAdmin(u)) return Results.Forbid();

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM ChatMessages WHERE Encrypted = 1";
    var deleted = cmd.ExecuteNonQuery();

    AppHelpers.LogActivity(u!, "clear_encrypted_messages", $"Deleted {deleted} messages");
    return Results.Ok(new { success = true, deletedCount = deleted });
});

ServerApi.Register(app);

// ═══════════════════════════════════════════════════════════════
// STRIPE CONFIGURATION
// ═══════════════════════════════════════════════════════════════
StripeConfiguration.ApiKey = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");

app.MapSystemEndpoints();

// ═══════════════════════════════════════════════
// STRIPE PAYMENT ENDPOINTS
// ═══════════════════════════════════════════════

app.MapPost("/api/stripe/checkout/spacerium", async (HttpContext ctx) =>
{
    var user = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(user) || !AppHelpers.UserExists(user))
        return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var plan = "monthly";
    try
    {
        var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
        plan = json.TryGetProperty("plan", out var p) ? p.GetString() ?? "monthly" : "monthly";
    }
    catch { }

    var priceAmount = plan == "yearly" ? 7900L : 900L;
    var interval = plan == "yearly" ? "year" : "month";

    var options = new SessionCreateOptions
    {
        PaymentMethodTypes = new List<string> { "card" },
        LineItems = new List<SessionLineItemOptions>
        {
            new SessionLineItemOptions
            {
                PriceData = new SessionLineItemPriceDataOptions
                {
                    UnitAmount = priceAmount,
                    Currency = "usd",
                    Recurring = new SessionLineItemPriceDataRecurringOptions { Interval = interval },
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = "Spacerium Premium",
                        Description = "Privacy + Power. Zero-log chat, dev sandbox, AI assistant, and pro badge."
                    }
                },
                Quantity = 1
            }
        },
        Mode = "subscription",
        Locale = "en",
        SuccessUrl = "https://runspace.cloud/home?premium=success",
        CancelUrl = "https://runspace.cloud/manage-your-subscription?cancelled=1",
        Metadata = new Dictionary<string, string>
        {
            { "type", "spacerium" },
            { "username", user },
            { "plan", plan }
        }
    };

    var service = new SessionService();
    var session = await service.CreateAsync(options);
    return Results.Ok(new { url = session.Url });
});

app.MapPost("/api/stripe/checkout/script", async (HttpContext ctx) =>
{
    var marketUser = MarketHelpers.GetMarketUser(ctx);
    if (marketUser == null) return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    long scriptId = 0;
    try
    {
        var json = System.Text.Json.JsonDocument.Parse(body).RootElement;
        scriptId = json.TryGetProperty("scriptId", out var s) ? s.GetInt64() : 0;
    }
    catch { }

    if (scriptId == 0) return Results.BadRequest(new { error = "scriptId required" });

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Name, Price, CreatorUsername FROM MarketScripts WHERE Id=$id AND Status='approved'";
    cmd.Parameters.AddWithValue("$id", scriptId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.NotFound(new { error = "Script not found" });

    var name = r.GetString(0);
    var price = r.GetInt32(1);
    var creator = r.GetString(2);
    r.Close();

    if (price == 0) return Results.BadRequest(new { error = "Script is free" });

    var options = new SessionCreateOptions
    {
        PaymentMethodTypes = new List<string> { "card" },
        LineItems = new List<SessionLineItemOptions>
        {
            new SessionLineItemOptions
            {
                PriceData = new SessionLineItemPriceDataOptions
                {
                    UnitAmount = price * 100,
                    Currency = "usd",
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = name,
                        Description = "Script by @" + creator
                    }
                },
                Quantity = 1
            }
        },
        Mode = "payment",
        Locale = "en",
        SuccessUrl = "https://market.runspace.cloud/?purchased=" + scriptId,
        CancelUrl = "https://market.runspace.cloud/?cancelled=1",
        Metadata = new Dictionary<string, string>
        {
            { "type", "script" },
            { "scriptId", scriptId.ToString() },
            { "buyerId", marketUser.Value.id },
            { "buyerUsername", marketUser.Value.username }
        }
    };

    var service = new SessionService();
    var session = await service.CreateAsync(options);
    return Results.Ok(new { url = session.Url });
});

app.MapPost("/api/stripe/webhook", async (HttpContext ctx) =>
{
    var json = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
    var webhookSecret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET");

    Event stripeEvent;
    try
    {
        stripeEvent = EventUtility.ConstructEvent(json, ctx.Request.Headers["Stripe-Signature"], webhookSecret);
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid signature" });
    }

    if (stripeEvent.Type == "checkout.session.completed")
    {
        var session = stripeEvent.Data.Object as Session;
        if (session?.Metadata == null) return Results.Ok();
        var type = session.Metadata.GetValueOrDefault("type", "");

        if (type == "spacerium")
        {
            var username = session.Metadata["username"];
            var plan = session.Metadata["plan"];
            using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE AuthUsers SET IsPremium=1, PremiumPlan=$plan, PremiumSince=$t, PremiumUntil=$until, StripeSubscriptionId=$subId WHERE LOWER(Username)=$u";
            cmd.Parameters.AddWithValue("$plan", plan);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("$until", plan == "yearly" ? DateTime.UtcNow.AddYears(1).ToString("o") : DateTime.UtcNow.AddMonths(1).ToString("o"));
            cmd.Parameters.AddWithValue("$subId", session.SubscriptionId ?? "");
            cmd.Parameters.AddWithValue("$u", username);
            cmd.ExecuteNonQuery();
        }
    }
    return Results.Ok();
});



// ═══════════════════════════════════════════════
// SPACES (SERVERS) ENDPOINTS
// ═══════════════════════════════════════════════

// Create a new Space
app.MapPost("/api/spaces", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !AppHelpers.UserExists(username)) return Results.Unauthorized();

    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var name = body?.GetValueOrDefault("name", "")?.Trim() ?? "";
    var description = body?.GetValueOrDefault("description", "")?.Trim() ?? "";

    if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 50)
        return Results.BadRequest(new { error = "Name must be 2-50 characters" });

    var publicId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLower();

    using var db = DbHelpers.OpenDb();

    using var userCmd = db.CreateCommand();
    userCmd.CommandText = "SELECT Id FROM AuthUsers WHERE Username=$u";
    userCmd.Parameters.AddWithValue("$u", username);
    var userId = Convert.ToInt64(userCmd.ExecuteScalar());

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"INSERT INTO Spaces (PublicId, Name, Description, OwnerId, CreatedAt, MemberCount)
        VALUES ($pid, $name, $desc, $uid, datetime('now'), 1)";
    cmd.Parameters.AddWithValue("$pid", publicId);
    cmd.Parameters.AddWithValue("$name", InputSanitizer.SanitizeInput(name, 50));
    cmd.Parameters.AddWithValue("$desc", InputSanitizer.SanitizeInput(description, 200));
    cmd.Parameters.AddWithValue("$uid", userId);
    cmd.ExecuteNonQuery();

    var spaceId = Convert.ToInt64(new SqliteCommand("SELECT last_insert_rowid()", db).ExecuteScalar());

    using var memCmd = db.CreateCommand();
    memCmd.CommandText = "INSERT INTO SpaceMembers (SpaceId, UserId, Role, JoinedAt) VALUES ($sid, $uid, 'owner', datetime('now'))";
    memCmd.Parameters.AddWithValue("$sid", spaceId);
    memCmd.Parameters.AddWithValue("$uid", userId);
    memCmd.ExecuteNonQuery();

    using var chCmd = db.CreateCommand();
    chCmd.CommandText = "INSERT INTO SpaceChannels (SpaceId, Name, Type, Position, CreatedAt) VALUES ($sid, 'general', 'text', 0, datetime('now'))";
    chCmd.Parameters.AddWithValue("$sid", spaceId);
    chCmd.ExecuteNonQuery();

    return Results.Ok(new { id = spaceId, publicId, name, message = "Space created!" });
});

// Get user's spaces
app.MapGet("/api/spaces", (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !AppHelpers.UserExists(username)) return Results.Unauthorized();

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT s.Id, s.PublicId, s.Name, s.Description, s.IconUrl, s.MemberCount, sm.Role, s.OwnerId,
        (SELECT Username FROM AuthUsers WHERE Id = s.OwnerId) as OwnerName
        FROM Spaces s
        JOIN SpaceMembers sm ON sm.SpaceId = s.Id
        JOIN AuthUsers u ON u.Id = sm.UserId
        WHERE u.Username = $u
        ORDER BY s.Name";
    cmd.Parameters.AddWithValue("$u", username);

    using var r = cmd.ExecuteReader();
    var spaces = new List<object>();
    while (r.Read())
    {
        spaces.Add(new
        {
            id = r.GetInt64(0),
            publicId = r.GetString(1),
            name = r.GetString(2),
            description = r.IsDBNull(3) ? "" : r.GetString(3),
            iconUrl = r.IsDBNull(4) ? "" : r.GetString(4),
            memberCount = r.GetInt32(5),
            role = r.GetString(6),
            ownerId = r.GetInt64(7),
            ownerName = r.GetString(8)
        });
    }
    return Results.Ok(spaces);
});

// Get single space
app.MapGet("/api/spaces/{publicId}", (string publicId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();

    using var db = DbHelpers.OpenDb();
    using var memCheck = db.CreateCommand();
    memCheck.CommandText = @"SELECT sm.Role FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    memCheck.Parameters.AddWithValue("$pid", publicId);
    memCheck.Parameters.AddWithValue("$u", username);
    var role = memCheck.ExecuteScalar() as string;
    if (role == null) return Results.Json(new { error = "Not a member" }, statusCode: 403);

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT s.Id, s.PublicId, s.Name, s.Description, s.IconUrl, s.MemberCount, s.OwnerId, s.CreatedAt, (SELECT Username FROM AuthUsers WHERE Id = s.OwnerId) FROM Spaces s WHERE s.PublicId = $pid";
    cmd.Parameters.AddWithValue("$pid", publicId);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.NotFound();
    var space = new { id = r.GetInt64(0), publicId = r.GetString(1), name = r.GetString(2), description = r.IsDBNull(3) ? "" : r.GetString(3), iconUrl = r.IsDBNull(4) ? "" : r.GetString(4), memberCount = r.GetInt32(5), ownerId = r.GetInt64(6), createdAt = r.GetString(7), ownerName = r.GetString(8), myRole = role };
    r.Close();

    using var chCmd = db.CreateCommand();
    chCmd.CommandText = "SELECT Id, Name, Type, Position FROM SpaceChannels WHERE SpaceId = (SELECT Id FROM Spaces WHERE PublicId=$pid) ORDER BY Position";
    chCmd.Parameters.AddWithValue("$pid", publicId);
    using var chR = chCmd.ExecuteReader();
    var channels = new List<object>();
    while (chR.Read()) channels.Add(new { id = chR.GetInt64(0), name = chR.GetString(1), type = chR.GetString(2), position = chR.GetInt32(3) });
    return Results.Ok(new { space, channels });
});

// Create channel
app.MapPost("/api/spaces/{publicId}/channels", async (string publicId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var roleCheck = db.CreateCommand();
    roleCheck.CommandText = @"SELECT sm.Role, s.Id FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    roleCheck.Parameters.AddWithValue("$pid", publicId);
    roleCheck.Parameters.AddWithValue("$u", username);
    using var roleR = roleCheck.ExecuteReader();
    if (!roleR.Read()) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var role = roleR.GetString(0); var spaceId = roleR.GetInt64(1); roleR.Close();
    if (role != "owner" && role != "admin") return Results.Json(new { error = "No permission" }, statusCode: 403);
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var name = body?.GetValueOrDefault("name", "")?.Trim().ToLower().Replace(" ", "-") ?? "";
    if (string.IsNullOrWhiteSpace(name) || name.Length > 30) return Results.BadRequest(new { error = "Invalid name" });
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO SpaceChannels (SpaceId, Name, Type, Position, CreatedAt) VALUES ($sid, $name, 'text', (SELECT COALESCE(MAX(Position),0)+1 FROM SpaceChannels WHERE SpaceId=$sid), datetime('now'))";
    cmd.Parameters.AddWithValue("$sid", spaceId);
    cmd.Parameters.AddWithValue("$name", name);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { id = Convert.ToInt64(new SqliteCommand("SELECT last_insert_rowid()", db).ExecuteScalar()), name, type = "text" });
});

// Create invite
app.MapPost("/api/spaces/{publicId}/invites", async (string publicId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var memCheck = db.CreateCommand();
    memCheck.CommandText = @"SELECT s.Id, u.Id FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    memCheck.Parameters.AddWithValue("$pid", publicId);
    memCheck.Parameters.AddWithValue("$u", username);
    using var memR = memCheck.ExecuteReader();
    if (!memR.Read()) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var spaceId = memR.GetInt64(0); var userId = memR.GetInt64(1); memR.Close();
    var code = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLower();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO SpaceInvites (SpaceId, Code, CreatedBy, CreatedAt) VALUES ($sid, $code, $uid, datetime('now'))";
    cmd.Parameters.AddWithValue("$sid", spaceId);
    cmd.Parameters.AddWithValue("$code", code);
    cmd.Parameters.AddWithValue("$uid", userId);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { code, url = "https://runspace.cloud/invite/" + code });
});

// Join via invite
app.MapPost("/api/invite/{code}", async (string code, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !AppHelpers.UserExists(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var userCmd = db.CreateCommand();
    userCmd.CommandText = "SELECT Id FROM AuthUsers WHERE Username=$u";
    userCmd.Parameters.AddWithValue("$u", username);
    var userId = Convert.ToInt64(userCmd.ExecuteScalar());
    using var invCmd = db.CreateCommand();
    invCmd.CommandText = "SELECT i.SpaceId, i.MaxUses, i.Uses, s.Name, s.PublicId FROM SpaceInvites i JOIN Spaces s ON s.Id = i.SpaceId WHERE i.Code = $c";
    invCmd.Parameters.AddWithValue("$c", code.ToLower());
    using var invR = invCmd.ExecuteReader();
    if (!invR.Read()) return Results.NotFound(new { error = "Invalid invite" });
    var spaceId = invR.GetInt64(0); var maxUses = invR.GetInt32(1); var uses = invR.GetInt32(2); var spaceName = invR.GetString(3); var spacePublicId = invR.GetString(4); invR.Close();
    if (maxUses > 0 && uses >= maxUses) return Results.BadRequest(new { error = "Invite expired" });
    using var memCheck = db.CreateCommand();
    memCheck.CommandText = "SELECT COUNT(*) FROM SpaceMembers WHERE SpaceId=$sid AND UserId=$uid";
    memCheck.Parameters.AddWithValue("$sid", spaceId);
    memCheck.Parameters.AddWithValue("$uid", userId);
    if (Convert.ToInt64(memCheck.ExecuteScalar()) > 0) return Results.Ok(new { message = "Already a member", publicId = spacePublicId, name = spaceName });
    using var joinCmd = db.CreateCommand();
    joinCmd.CommandText = "INSERT INTO SpaceMembers (SpaceId, UserId, Role, JoinedAt) VALUES ($sid, $uid, 'member', datetime('now'))";
    joinCmd.Parameters.AddWithValue("$sid", spaceId);
    joinCmd.Parameters.AddWithValue("$uid", userId);
    joinCmd.ExecuteNonQuery();
    using var updCmd = db.CreateCommand();
    updCmd.CommandText = "UPDATE Spaces SET MemberCount = MemberCount + 1 WHERE Id = $sid";
    updCmd.Parameters.AddWithValue("$sid", spaceId);
    updCmd.ExecuteNonQuery();
    using var invUpd = db.CreateCommand();
    invUpd.CommandText = "UPDATE SpaceInvites SET Uses = Uses + 1 WHERE Code = $c";
    invUpd.Parameters.AddWithValue("$c", code.ToLower());
    invUpd.ExecuteNonQuery();
    return Results.Ok(new { message = "Joined!", publicId = spacePublicId, name = spaceName });
});

// Get channel messages
app.MapGet("/api/spaces/{publicId}/channels/{channelId}/messages", (string publicId, long channelId, HttpContext ctx, int? before, int? limit) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var memCheck = db.CreateCommand();
    memCheck.CommandText = "SELECT 1 FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    memCheck.Parameters.AddWithValue("$pid", publicId);
    memCheck.Parameters.AddWithValue("$u", username);
    if (memCheck.ExecuteScalar() == null) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var take = Math.Min(limit ?? 50, 100);
    using var cmd = db.CreateCommand();
    cmd.CommandText = before.HasValue
        ? "SELECT m.Id, m.Content, m.CreatedAt, m.EditedAt, u.Username, u.AvatarUrl FROM SpaceMessages m JOIN AuthUsers u ON u.Id = m.UserId WHERE m.ChannelId = $cid AND m.Id < $before ORDER BY m.Id DESC LIMIT $limit"
        : "SELECT m.Id, m.Content, m.CreatedAt, m.EditedAt, u.Username, u.AvatarUrl FROM SpaceMessages m JOIN AuthUsers u ON u.Id = m.UserId WHERE m.ChannelId = $cid ORDER BY m.Id DESC LIMIT $limit";
    cmd.Parameters.AddWithValue("$cid", channelId);
    cmd.Parameters.AddWithValue("$limit", take);
    if (before.HasValue) cmd.Parameters.AddWithValue("$before", before.Value);
    using var r = cmd.ExecuteReader();
    var messages = new List<object>();
    while (r.Read()) messages.Add(new { id = r.GetInt64(0), content = r.GetString(1), createdAt = r.GetString(2), editedAt = r.IsDBNull(3) ? null : r.GetString(3), username = r.GetString(4), avatarUrl = r.IsDBNull(5) ? "" : r.GetString(5) });
    messages.Reverse();
    return Results.Ok(messages);
});

// Send message
app.MapPost("/api/spaces/{publicId}/channels/{channelId}/messages", async (string publicId, long channelId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var memCheck = db.CreateCommand();
    memCheck.CommandText = "SELECT u.Id FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    memCheck.Parameters.AddWithValue("$pid", publicId);
    memCheck.Parameters.AddWithValue("$u", username);
    var userIdObj = memCheck.ExecuteScalar();
    if (userIdObj == null) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var userId = Convert.ToInt64(userIdObj);
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var content = body?.GetValueOrDefault("content", "")?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(content) || content.Length > 2000) return Results.BadRequest(new { error = "Message must be 1-2000 chars" });
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO SpaceMessages (ChannelId, UserId, Content, CreatedAt) VALUES ($cid, $uid, $content, datetime('now'))";
    cmd.Parameters.AddWithValue("$cid", channelId);
    cmd.Parameters.AddWithValue("$uid", userId);
    cmd.Parameters.AddWithValue("$content", content);
    cmd.ExecuteNonQuery();
    var msgId = Convert.ToInt64(new SqliteCommand("SELECT last_insert_rowid()", db).ExecuteScalar());
    var hubContext = ctx.RequestServices.GetRequiredService<IHubContext<ChatHub>>();
    await hubContext.Clients.Group("space:" + publicId).SendAsync("SpaceMessage", new { id = msgId, channelId, content, username, createdAt = DateTime.UtcNow.ToString("o") });
    return Results.Ok(new { id = msgId });
});


// Update space settings
app.MapPatch("/api/spaces/{publicId}", async (string publicId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var roleCheck = db.CreateCommand();
    roleCheck.CommandText = "SELECT sm.Role, s.Id FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    roleCheck.Parameters.AddWithValue("$pid", publicId);
    roleCheck.Parameters.AddWithValue("$u", username);
    using var roleR = roleCheck.ExecuteReader();
    if (!roleR.Read()) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var role = roleR.GetString(0); var spaceId = roleR.GetInt64(1); roleR.Close();
    if (role != "owner" && role != "admin") return Results.Json(new { error = "No permission" }, statusCode: 403);
    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var name = body?.GetValueOrDefault("name", "")?.Trim() ?? "";
    var description = body?.GetValueOrDefault("description", "")?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(name) || name.Length > 50) return Results.BadRequest(new { error = "Invalid name" });
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE Spaces SET Name=$name, Description=$desc WHERE Id=$sid";
    cmd.Parameters.AddWithValue("$name", InputSanitizer.SanitizeInput(name, 50));
    cmd.Parameters.AddWithValue("$desc", InputSanitizer.SanitizeInput(description, 200));
    cmd.Parameters.AddWithValue("$sid", spaceId);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { message = "Updated" });
});

// Leave space
app.MapPost("/api/spaces/{publicId}/leave", (string publicId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var memCheck = db.CreateCommand();
    memCheck.CommandText = "SELECT sm.Role, sm.Id, s.Id FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    memCheck.Parameters.AddWithValue("$pid", publicId);
    memCheck.Parameters.AddWithValue("$u", username);
    using var memR = memCheck.ExecuteReader();
    if (!memR.Read()) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var role = memR.GetString(0); var memberId = memR.GetInt64(1); var spaceId = memR.GetInt64(2); memR.Close();
    if (role == "owner") return Results.BadRequest(new { error = "Owner cannot leave. Delete the space or transfer ownership." });
    using var delCmd = db.CreateCommand();
    delCmd.CommandText = "DELETE FROM SpaceMembers WHERE Id=$mid";
    delCmd.Parameters.AddWithValue("$mid", memberId);
    delCmd.ExecuteNonQuery();
    using var updCmd = db.CreateCommand();
    updCmd.CommandText = "UPDATE Spaces SET MemberCount = MemberCount - 1 WHERE Id=$sid";
    updCmd.Parameters.AddWithValue("$sid", spaceId);
    updCmd.ExecuteNonQuery();
    return Results.Ok(new { message = "Left space" });
});

// Delete space
app.MapDelete("/api/spaces/{publicId}", (string publicId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var roleCheck = db.CreateCommand();
    roleCheck.CommandText = "SELECT sm.Role, s.Id FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    roleCheck.Parameters.AddWithValue("$pid", publicId);
    roleCheck.Parameters.AddWithValue("$u", username);
    using var roleR = roleCheck.ExecuteReader();
    if (!roleR.Read()) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var role = roleR.GetString(0); var spaceId = roleR.GetInt64(1); roleR.Close();
    if (role != "owner") return Results.Json(new { error = "Only owner can delete" }, statusCode: 403);
    // Delete all related data
    using var delMsg = db.CreateCommand();
    delMsg.CommandText = "DELETE FROM SpaceMessages WHERE ChannelId IN (SELECT Id FROM SpaceChannels WHERE SpaceId=$sid)";
    delMsg.Parameters.AddWithValue("$sid", spaceId);
    delMsg.ExecuteNonQuery();
    using var delCh = db.CreateCommand();
    delCh.CommandText = "DELETE FROM SpaceChannels WHERE SpaceId=$sid";
    delCh.Parameters.AddWithValue("$sid", spaceId);
    delCh.ExecuteNonQuery();
    using var delMem = db.CreateCommand();
    delMem.CommandText = "DELETE FROM SpaceMembers WHERE SpaceId=$sid";
    delMem.Parameters.AddWithValue("$sid", spaceId);
    delMem.ExecuteNonQuery();
    using var delInv = db.CreateCommand();
    delInv.CommandText = "DELETE FROM SpaceInvites WHERE SpaceId=$sid";
    delInv.Parameters.AddWithValue("$sid", spaceId);
    delInv.ExecuteNonQuery();
    using var delSpace = db.CreateCommand();
    delSpace.CommandText = "DELETE FROM Spaces WHERE Id=$sid";
    delSpace.Parameters.AddWithValue("$sid", spaceId);
    delSpace.ExecuteNonQuery();
    return Results.Ok(new { message = "Space deleted" });
});


// Delete channel
app.MapDelete("/api/spaces/{publicId}/channels/{channelId}", (string publicId, long channelId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();

    // Check permission (owner/admin only)
    using var roleCheck = db.CreateCommand();
    roleCheck.CommandText = @"SELECT sm.Role FROM SpaceMembers sm 
        JOIN Spaces s ON s.Id = sm.SpaceId 
        JOIN AuthUsers u ON u.Id = sm.UserId 
        WHERE s.PublicId = $pid AND u.Username = $u";
    roleCheck.Parameters.AddWithValue("$pid", publicId);
    roleCheck.Parameters.AddWithValue("$u", username);
    var role = roleCheck.ExecuteScalar() as string;
    if (role != "owner" && role != "admin") return Results.Json(new { error = "No permission" }, statusCode: 403);

    // Check channel count (can't delete last channel)
    using var countCmd = db.CreateCommand();
    countCmd.CommandText = "SELECT COUNT(*) FROM SpaceChannels WHERE SpaceId = (SELECT Id FROM Spaces WHERE PublicId = $pid)";
    countCmd.Parameters.AddWithValue("$pid", publicId);
    if (Convert.ToInt64(countCmd.ExecuteScalar()) <= 1)
        return Results.BadRequest(new { error = "Cannot delete the last channel" });

    // Delete messages first
    using var delMsgs = db.CreateCommand();
    delMsgs.CommandText = "DELETE FROM SpaceMessages WHERE ChannelId = $cid";
    delMsgs.Parameters.AddWithValue("$cid", channelId);
    delMsgs.ExecuteNonQuery();

    // Delete channel
    using var delCh = db.CreateCommand();
    delCh.CommandText = "DELETE FROM SpaceChannels WHERE Id = $cid";
    delCh.Parameters.AddWithValue("$cid", channelId);
    var rows = delCh.ExecuteNonQuery();

    if (rows == 0) return Results.NotFound(new { error = "Channel not found" });
    return Results.Ok(new { message = "Channel deleted" });
});

// Kick member from space
app.MapPost("/api/spaces/{publicId}/kick/{targetUsername}", (string publicId, string targetUsername, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    var target = targetUsername?.Trim().ToLowerInvariant() ?? "";
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(target)) return Results.BadRequest(new { error = "Invalid username" });
    if (username == target) return Results.BadRequest(new { error = "Cannot kick yourself" });

    using var db = DbHelpers.OpenDb();

    // Check kicker's role
    using var roleCheck = db.CreateCommand();
    roleCheck.CommandText = @"SELECT sm.Role, s.Id FROM SpaceMembers sm 
        JOIN Spaces s ON s.Id = sm.SpaceId 
        JOIN AuthUsers u ON u.Id = sm.UserId 
        WHERE s.PublicId = $pid AND u.Username = $u";
    roleCheck.Parameters.AddWithValue("$pid", publicId);
    roleCheck.Parameters.AddWithValue("$u", username);
    using var roleR = roleCheck.ExecuteReader();
    if (!roleR.Read()) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var kickerRole = roleR.GetString(0);
    var spaceId = roleR.GetInt64(1);
    roleR.Close();

    if (kickerRole != "owner" && kickerRole != "admin")
        return Results.Json(new { error = "No permission" }, statusCode: 403);

    // Check target's role
    using var targetCheck = db.CreateCommand();
    targetCheck.CommandText = @"SELECT sm.Role, sm.Id FROM SpaceMembers sm 
        JOIN AuthUsers u ON u.Id = sm.UserId 
        WHERE sm.SpaceId = $sid AND u.Username = $t";
    targetCheck.Parameters.AddWithValue("$sid", spaceId);
    targetCheck.Parameters.AddWithValue("$t", target);
    using var targetR = targetCheck.ExecuteReader();
    if (!targetR.Read()) return Results.NotFound(new { error = "User not in space" });
    var targetRole = targetR.GetString(0);
    var targetMemberId = targetR.GetInt64(1);
    targetR.Close();

    // Cannot kick owner, admin can't kick other admins
    if (targetRole == "owner") return Results.BadRequest(new { error = "Cannot kick the owner" });
    if (targetRole == "admin" && kickerRole != "owner")
        return Results.BadRequest(new { error = "Only owner can kick admins" });

    // Remove member
    using var delCmd = db.CreateCommand();
    delCmd.CommandText = "DELETE FROM SpaceMembers WHERE Id = $mid";
    delCmd.Parameters.AddWithValue("$mid", targetMemberId);
    delCmd.ExecuteNonQuery();

    // Update member count
    using var updCmd = db.CreateCommand();
    updCmd.CommandText = "UPDATE Spaces SET MemberCount = MemberCount - 1 WHERE Id = $sid";
    updCmd.Parameters.AddWithValue("$sid", spaceId);
    updCmd.ExecuteNonQuery();

    return Results.Ok(new { message = "Member kicked" });
});

// Promote/demote member
app.MapPatch("/api/spaces/{publicId}/members/{targetUsername}/role", async (string publicId, string targetUsername, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    var target = targetUsername?.Trim().ToLowerInvariant() ?? "";
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();

    var body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, string>>();
    var newRole = body?.GetValueOrDefault("role", "member") ?? "member";
    if (!new[] { "admin", "member" }.Contains(newRole))
        return Results.BadRequest(new { error = "Invalid role" });

    using var db = DbHelpers.OpenDb();

    // Only owner can change roles
    using var roleCheck = db.CreateCommand();
    roleCheck.CommandText = @"SELECT sm.Role, s.Id FROM SpaceMembers sm 
        JOIN Spaces s ON s.Id = sm.SpaceId 
        JOIN AuthUsers u ON u.Id = sm.UserId 
        WHERE s.PublicId = $pid AND u.Username = $u";
    roleCheck.Parameters.AddWithValue("$pid", publicId);
    roleCheck.Parameters.AddWithValue("$u", username);
    using var roleR = roleCheck.ExecuteReader();
    if (!roleR.Read()) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    var myRole = roleR.GetString(0);
    var spaceId = roleR.GetInt64(1);
    roleR.Close();

    if (myRole != "owner") return Results.Json(new { error = "Only owner can change roles" }, statusCode: 403);

    // Cannot change owner's role
    using var targetCheck = db.CreateCommand();
    targetCheck.CommandText = @"SELECT sm.Role FROM SpaceMembers sm 
        JOIN AuthUsers u ON u.Id = sm.UserId 
        WHERE sm.SpaceId = $sid AND u.Username = $t";
    targetCheck.Parameters.AddWithValue("$sid", spaceId);
    targetCheck.Parameters.AddWithValue("$t", target);
    var targetRole = targetCheck.ExecuteScalar() as string;
    if (targetRole == null) return Results.NotFound(new { error = "User not in space" });
    if (targetRole == "owner") return Results.BadRequest(new { error = "Cannot change owner role" });

    // Update role
    using var updCmd = db.CreateCommand();
    updCmd.CommandText = @"UPDATE SpaceMembers SET Role = $role 
        WHERE SpaceId = $sid AND UserId = (SELECT Id FROM AuthUsers WHERE Username = $t)";
    updCmd.Parameters.AddWithValue("$role", newRole);
    updCmd.Parameters.AddWithValue("$sid", spaceId);
    updCmd.Parameters.AddWithValue("$t", target);
    updCmd.ExecuteNonQuery();

    return Results.Ok(new { message = "Role updated", role = newRole });
});

// Get members
app.MapGet("/api/spaces/{publicId}/members", (string publicId, HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username)) return Results.Unauthorized();
    using var db = DbHelpers.OpenDb();
    using var memCheck = db.CreateCommand();
    memCheck.CommandText = "SELECT 1 FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u";
    memCheck.Parameters.AddWithValue("$pid", publicId);
    memCheck.Parameters.AddWithValue("$u", username);
    if (memCheck.ExecuteScalar() == null) return Results.Json(new { error = "Not a member" }, statusCode: 403);
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT u.Username, u.AvatarUrl, sm.Role, sm.JoinedAt FROM SpaceMembers sm JOIN AuthUsers u ON u.Id = sm.UserId JOIN Spaces s ON s.Id = sm.SpaceId WHERE s.PublicId = $pid ORDER BY CASE sm.Role WHEN 'owner' THEN 0 WHEN 'admin' THEN 1 ELSE 2 END, u.Username";
    cmd.Parameters.AddWithValue("$pid", publicId);
    using var r = cmd.ExecuteReader();
    var members = new List<object>();
    while (r.Read()) members.Add(new { username = r.GetString(0), avatarUrl = r.IsDBNull(1) ? "" : r.GetString(1), role = r.GetString(2), joinedAt = r.GetString(3) });
    return Results.Ok(members);
});

// ═══════════════════════════════════════════════
// MARKET ENDPOINTS
// ═══════════════════════════════════════════════

// ── Market Auth: Register (standalone) ──
app.MapPost("/api/market/auth/register", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body).RootElement;

    var username = json.TryGetProperty("username", out var u) ? u.GetString()?.Trim().ToLowerInvariant() : null;
    var email = json.TryGetProperty("email", out var e) ? e.GetString()?.Trim().ToLowerInvariant() : null;
    var password = json.TryGetProperty("password", out var p) ? p.GetString() : null;

    if (string.IsNullOrWhiteSpace(username) || username.Length < 3 || username.Length > 24)
        return Results.Json(new { error = "Username must be 3-24 characters." }, statusCode: 400);
    if (!System.Text.RegularExpressions.Regex.IsMatch(username, @"^[a-z0-9_]+$"))
        return Results.Json(new { error = "Username can only contain a-z, 0-9, underscore." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(email) || !email.Contains("@"))
        return Results.Json(new { error = "Valid email required." }, statusCode: 400);
    if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        return Results.Json(new { error = "Password must be at least 8 characters." }, statusCode: 400);

    using var db = new SqliteConnection($"Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var chkUser = db.CreateCommand();
    chkUser.CommandText = "SELECT COUNT(*) FROM MarketUsers WHERE Username=$u";
    chkUser.Parameters.AddWithValue("$u", username);
    if (Convert.ToInt64(chkUser.ExecuteScalar()) > 0)
        return Results.Json(new { error = "Username already taken." }, statusCode: 409);

    using var chkEmail = db.CreateCommand();
    chkEmail.CommandText = "SELECT COUNT(*) FROM MarketUsers WHERE Email=$e";
    chkEmail.Parameters.AddWithValue("$e", email);
    if (Convert.ToInt64(chkEmail.ExecuteScalar()) > 0)
        return Results.Json(new { error = "Email already registered." }, statusCode: 409);

    var id = Guid.NewGuid().ToString("N");
    var hash = BCrypt.Net.BCrypt.HashPassword(password);

    using var ins = db.CreateCommand();
    ins.CommandText = @"INSERT INTO MarketUsers (Id, Username, Email, PasswordHash, CreatedAt)
                        VALUES ($id, $u, $e, $h, $t)";
    ins.Parameters.AddWithValue("$id", id);
    ins.Parameters.AddWithValue("$u", username);
    ins.Parameters.AddWithValue("$e", email);
    ins.Parameters.AddWithValue("$h", hash);
    ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    ins.ExecuteNonQuery();

    return Results.Ok(new { success = true, userId = id, username });
});

// ── Market Auth: Link RunSpace account ──
app.MapPost("/api/market/auth/link-runspace", (HttpContext ctx) =>
{
    var authUser = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(authUser) || !AppHelpers.UserExists(authUser))
        return Results.Unauthorized();

    using var db = new SqliteConnection($"Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var getAuth = db.CreateCommand();
    getAuth.CommandText = "SELECT Id FROM AuthUsers WHERE LOWER(Username)=$u LIMIT 1";
    getAuth.Parameters.AddWithValue("$u", authUser);
    var authId = getAuth.ExecuteScalar();
    if (authId == null) return Results.Unauthorized();

    using var chkLink = db.CreateCommand();
    chkLink.CommandText = "SELECT Id, Username FROM MarketUsers WHERE LinkedAuthUserId=$aid LIMIT 1";
    chkLink.Parameters.AddWithValue("$aid", Convert.ToInt64(authId));
    using var rdr = chkLink.ExecuteReader();
    if (rdr.Read())
        return Results.Ok(new { success = true, alreadyLinked = true, userId = rdr.GetString(0), username = rdr.GetString(1) });
    rdr.Close();

    using var chkUser = db.CreateCommand();
    chkUser.CommandText = "SELECT COUNT(*) FROM MarketUsers WHERE Username=$u";
    chkUser.Parameters.AddWithValue("$u", authUser);
    if (Convert.ToInt64(chkUser.ExecuteScalar()) > 0)
        return Results.Json(new { error = "Username already exists in Market. Contact support." }, statusCode: 409);

    var id = Guid.NewGuid().ToString("N");

    using var ins = db.CreateCommand();
    ins.CommandText = @"INSERT INTO MarketUsers (Id, Username, LinkedAuthUserId, CreatedAt)
                        VALUES ($id, $u, $aid, $t)";
    ins.Parameters.AddWithValue("$id", id);
    ins.Parameters.AddWithValue("$u", authUser);
    ins.Parameters.AddWithValue("$aid", Convert.ToInt64(authId));
    ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    ins.ExecuteNonQuery();

    return Results.Ok(new { success = true, userId = id, username = authUser });
});

// ── Market Auth: Login (standalone) ──
app.MapPost("/api/market/auth/login", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body).RootElement;

    var email = json.TryGetProperty("email", out var e) ? e.GetString()?.Trim().ToLowerInvariant() : null;
    var password = json.TryGetProperty("password", out var p) ? p.GetString() : null;

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return Results.Json(new { error = "Email and password required." }, statusCode: 400);

    using var db = new SqliteConnection($"Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Id, Username, PasswordHash, IsBanned, BanReason FROM MarketUsers WHERE Email=$e LIMIT 1";
    cmd.Parameters.AddWithValue("$e", email);
    using var rdr = cmd.ExecuteReader();

    if (!rdr.Read())
        return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);

    var id = rdr.GetString(0);
    var username = rdr.GetString(1);
    var hash = rdr.IsDBNull(2) ? null : rdr.GetString(2);
    var isBanned = rdr.GetInt64(3) == 1;
    var banReason = rdr.IsDBNull(4) ? null : rdr.GetString(4);
    rdr.Close();

    if (hash == null)
        return Results.Json(new { error = "This account uses RunSpace login." }, statusCode: 400);

    if (!BCrypt.Net.BCrypt.Verify(password, hash))
        return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);

    if (isBanned)
        return Results.Json(new { error = $"Account banned: {banReason ?? "No reason given"}" }, statusCode: 403);

    using var upd = db.CreateCommand();
    upd.CommandText = "UPDATE MarketUsers SET LastLoginAt=$t WHERE Id=$id";
    upd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    upd.Parameters.AddWithValue("$id", id);
    upd.ExecuteNonQuery();

    ctx.Response.Cookies.Append("market_session", $"{id}:{username}", new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromDays(30),
        Domain = ".runspace.cloud"
    });

    return Results.Ok(new { success = true, userId = id, username });
});

// ── Market Auth: Current user ──
app.MapGet("/api/market/auth/me", (HttpContext ctx) =>
{
    var marketUser = MarketHelpers.GetMarketUser(ctx);
    if (marketUser == null) return Results.Json(new { loggedIn = false });
    return Results.Ok(new { loggedIn = true, userId = marketUser.Value.id, username = marketUser.Value.username });
});

// ── Market Auth: Logout ──
app.MapPost("/api/market/auth/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete("market_session");
    return Results.Ok(new { success = true });
});

// ── Market Profile: Get public profile ──
app.MapGet("/api/market/profile/{userId}", (string userId, HttpContext ctx) =>
{
    using var db = new SqliteConnection($"Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT Username, Bio, AvatarUrl, TotalSales, TotalEarnings, CreatedAt, IsBanned
                        FROM MarketUsers WHERE Id=$id LIMIT 1";
    cmd.Parameters.AddWithValue("$id", userId);
    using var rdr = cmd.ExecuteReader();

    if (!rdr.Read())
        return Results.NotFound(new { error = "User not found." });

    if (rdr.GetInt64(6) == 1)
        return Results.Json(new { error = "This account has been suspended." }, statusCode: 403);

    var username = rdr.GetString(0);
    var bio = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
    var avatarUrl = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
    var totalSales = rdr.GetInt64(3);
    var totalEarnings = rdr.GetInt64(4);
    var memberSince = rdr.GetString(5);
    rdr.Close();

    using var scriptsCmd = db.CreateCommand();
    scriptsCmd.CommandText = @"SELECT Id, Name, Slug, Description, Category, Price, TotalSales, IsVerified
                               FROM MarketScripts 
                               WHERE CreatorUsername=$u AND Status='approved'
                               ORDER BY TotalSales DESC";
    scriptsCmd.Parameters.AddWithValue("$u", username);
    var scripts = new List<object>();
    using var sr = scriptsCmd.ExecuteReader();
    while (sr.Read())
    {
        scripts.Add(new
        {
            id = sr.GetInt64(0),
            name = sr.GetString(1),
            slug = sr.GetString(2),
            description = sr.GetString(3),
            category = sr.GetString(4),
            price = sr.GetInt64(5),
            totalSales = sr.GetInt64(6),
            isVerified = sr.GetInt64(7) == 1
        });
    }
    sr.Close();

    using var fbCmd = db.CreateCommand();
    fbCmd.CommandText = @"SELECT sf.Rating, sf.Comment, sf.PricePaid, sf.CreatedAt, 
                                 mu.Username, ms.Name as ScriptName
                          FROM SellerFeedback sf
                          JOIN MarketUsers mu ON mu.Id = sf.BuyerId
                          JOIN MarketScripts ms ON ms.Id = sf.ScriptId
                          WHERE sf.SellerId=$sid
                          ORDER BY sf.CreatedAt DESC LIMIT 20";
    fbCmd.Parameters.AddWithValue("$sid", userId);
    var feedback = new List<object>();
    using var fr = fbCmd.ExecuteReader();
    while (fr.Read())
    {
        feedback.Add(new
        {
            rating = fr.GetInt64(0),
            comment = fr.GetString(1),
            pricePaid = fr.GetInt64(2),
            createdAt = fr.GetString(3),
            buyerUsername = fr.GetString(4),
            scriptName = fr.GetString(5)
        });
    }

    double avgRating = feedback.Count > 0 ? feedback.Average(f => (double)((dynamic)f).rating) : 0;

    return Results.Ok(new
    {
        profile = new { id = userId, username, bio, avatarUrl, totalSales, totalEarnings, memberSince },
        scripts,
        feedback,
        averageRating = Math.Round(avgRating, 1),
        feedbackCount = feedback.Count
    });
});

// ── Market Profile: Update own profile ──
app.MapPut("/api/market/profile", async (HttpContext ctx) =>
{
    var marketUser = MarketHelpers.GetMarketUser(ctx);
    if (marketUser == null) return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body).RootElement;

    var bio = json.TryGetProperty("bio", out var b) ? b.GetString() ?? "" : "";
    if (bio.Length > 500) bio = bio.Substring(0, 500);

    using var db = new SqliteConnection($"Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE MarketUsers SET Bio=$bio WHERE Id=$id";
    cmd.Parameters.AddWithValue("$bio", bio);
    cmd.Parameters.AddWithValue("$id", marketUser.Value.id);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { success = true });
});

// ── Seller Feedback: Submit ──
app.MapPost("/api/market/feedback/{sellerId}", async (string sellerId, HttpContext ctx) =>
{
    var marketUser = MarketHelpers.GetMarketUser(ctx);
    if (marketUser == null) return Results.Unauthorized();

    if (marketUser.Value.id == sellerId)
        return Results.Json(new { error = "You cannot review yourself." }, statusCode: 400);

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var json = System.Text.Json.JsonDocument.Parse(body).RootElement;

    var scriptId = json.TryGetProperty("scriptId", out var s) ? s.GetInt64() : 0;
    var rating = json.TryGetProperty("rating", out var r) ? r.GetInt32() : 0;
    var comment = json.TryGetProperty("comment", out var c) ? c.GetString() ?? "" : "";

    if (scriptId == 0 || rating < 1 || rating > 5)
        return Results.Json(new { error = "Valid scriptId and rating (1-5) required." }, statusCode: 400);
    if (comment.Length > 1000) comment = comment.Substring(0, 1000);

    using var db = new SqliteConnection($"Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var chkPurchase = db.CreateCommand();
    chkPurchase.CommandText = @"SELECT up.Id, ms.Price FROM UserPurchases up
                                JOIN MarketScripts ms ON ms.Id = up.ScriptId
                                JOIN MarketUsers mu ON mu.LinkedAuthUserId = up.UserId
                                WHERE up.ScriptId=$sid AND mu.Id=$mid LIMIT 1";
    chkPurchase.Parameters.AddWithValue("$sid", scriptId);
    chkPurchase.Parameters.AddWithValue("$mid", marketUser.Value.id);
    using var pr = chkPurchase.ExecuteReader();
    if (!pr.Read())
        return Results.Json(new { error = "You must purchase this script to leave feedback." }, statusCode: 403);
    var pricePaid = pr.GetInt64(1);
    pr.Close();

    using var chkReview = db.CreateCommand();
    chkReview.CommandText = "SELECT COUNT(*) FROM SellerFeedback WHERE BuyerId=$bid AND ScriptId=$sid";
    chkReview.Parameters.AddWithValue("$bid", marketUser.Value.id);
    chkReview.Parameters.AddWithValue("$sid", scriptId);
    if (Convert.ToInt64(chkReview.ExecuteScalar()) > 0)
        return Results.Json(new { error = "You already left feedback for this script." }, statusCode: 409);

    using var ins = db.CreateCommand();
    ins.CommandText = @"INSERT INTO SellerFeedback (SellerId, BuyerId, ScriptId, PricePaid, Rating, Comment, CreatedAt)
                        VALUES ($seller, $buyer, $script, $price, $rating, $comment, $t)";
    ins.Parameters.AddWithValue("$seller", sellerId);
    ins.Parameters.AddWithValue("$buyer", marketUser.Value.id);
    ins.Parameters.AddWithValue("$script", scriptId);
    ins.Parameters.AddWithValue("$price", pricePaid);
    ins.Parameters.AddWithValue("$rating", rating);
    ins.Parameters.AddWithValue("$comment", comment);
    ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    ins.ExecuteNonQuery();

    return Results.Ok(new { success = true });
});


// ── Steg 2: Upload + ZipScanner ──
app.MapPost("/api/market/upload", async (HttpContext ctx) =>
{
    var marketUser = MarketHelpers.GetMarketUser(ctx);
    if (marketUser == null) return Results.Unauthorized();
    var u = marketUser.Value.username;

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(u, "market_upload", 5, 3600))
        return Results.Json(new { error = "Max 5 uploads per timme." }, statusCode: 429);

    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data krävs" });

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files["zip"];
    if (file == null || file.Length == 0)
        return Results.BadRequest(new { error = "ZIP-fil saknas." });
    if (file.Length > 50 * 1024 * 1024)
        return Results.Json(new { error = "Max 50 MB." }, statusCode: 413);

    // Metadata
    var name = InputSanitizer.SanitizeInput(form["name"].ToString(), 80).Trim();
    var description = InputSanitizer.SanitizeInput(form["description"].ToString(), 2000).Trim();
    var category = InputSanitizer.SanitizeInput(form["category"].ToString(), 40).Trim().ToLowerInvariant();
    var language = InputSanitizer.SanitizeInput(form["language"].ToString(), 40).Trim();
    var requirements = InputSanitizer.SanitizeInput(form["requirements"].ToString(), 500).Trim();
    var howToRun = InputSanitizer.SanitizeInput(form["howToRun"].ToString(), 500).Trim();
    var supportInfo = InputSanitizer.SanitizeInput(form["supportInfo"].ToString(), 300).Trim();
    var version = InputSanitizer.SanitizeInput(form["version"].ToString(), 20).Trim();
    var releaseNotes = InputSanitizer.SanitizeInput(form["releaseNotes"].ToString(), 500).Trim();
    var priceStr = form["price"].ToString().Trim();

    if (string.IsNullOrWhiteSpace(name) || name.Length < 3)
        return Results.BadRequest(new { error = "Namn krävs (minst 3 tecken)." });
    if (string.IsNullOrWhiteSpace(description))
        return Results.BadRequest(new { error = "Beskrivning krävs." });
    if (string.IsNullOrWhiteSpace(version))
        version = "1.0.0";

    var validCats = new[] { "automation", "dev tools", "privacy", "media", "utilities", "other" };
    if (!validCats.Contains(category)) category = "other";

    if (!int.TryParse(priceStr, out var price) || price < 0)
        price = 0;

    // Slug
    var slug = System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    var slugBase = slug;
    using var dbSlug = DbHelpers.OpenDb();
    using var slugCheck = dbSlug.CreateCommand();
    slugCheck.CommandText = "SELECT COUNT(*) FROM MarketScripts WHERE Slug LIKE $s";
    slugCheck.Parameters.AddWithValue("$s", slugBase + "%");
    var slugCount = Convert.ToInt32(slugCheck.ExecuteScalar());
    if (slugCount > 0) slug = slugBase + "-" + slugCount;

    // ── ZipScanner ──
    var scanResult = await MarketZipScanner.ScanAsync(file);
    if (!scanResult.IsValidZip)
        return Results.Json(new { error = "Filen är inte en giltig ZIP." }, statusCode: 422);
    if (scanResult.BlockedFiles.Any())
        return Results.Json(new { error = $"Otillåtna filtyper: {string.Join(", ", scanResult.BlockedFiles)}" }, statusCode: 422);

    // Spara ZIP
    var now = DateTime.UtcNow.ToString("o");
    var scriptGuid = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
    var scriptDir = Path.Combine(AppConfig.MarketDir, scriptGuid);
    Directory.CreateDirectory(scriptDir);
    var zipPath = Path.Combine(scriptDir, $"v{version}.zip");
    await using (var fs = System.IO.File.Create(zipPath)) await file.OpenReadStream().CopyToAsync(fs);

    // SHA-256
    var sha256 = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(await System.IO.File.ReadAllBytesAsync(zipPath))
    ).ToLowerInvariant();

    // Skriv till DB
    using var db = DbHelpers.OpenDb();

    using var insScript = db.CreateCommand();
    insScript.CommandText = @"INSERT INTO MarketScripts
        (CreatorUsername,Name,Slug,Description,Category,Language,Requirements,HowToRun,SupportInfo,Price,Status,TrustScore,IsVerified,TotalSales,CreatedAt,UpdatedAt)
        VALUES ($u,$n,$sl,$d,$cat,$lang,$req,$run,$sup,$p,'pending',0,0,0,$t,$t);
        SELECT last_insert_rowid();";
    insScript.Parameters.AddWithValue("$u", u);
    insScript.Parameters.AddWithValue("$n", name);
    insScript.Parameters.AddWithValue("$sl", slug);
    insScript.Parameters.AddWithValue("$d", description);
    insScript.Parameters.AddWithValue("$cat", category);
    insScript.Parameters.AddWithValue("$lang", language);
    insScript.Parameters.AddWithValue("$req", requirements);
    insScript.Parameters.AddWithValue("$run", howToRun);
    insScript.Parameters.AddWithValue("$sup", supportInfo);
    insScript.Parameters.AddWithValue("$p", price);
    insScript.Parameters.AddWithValue("$t", now);
    var scriptId = (long)(insScript.ExecuteScalar() ?? 0L);

    using var insVer = db.CreateCommand();
    insVer.CommandText = @"INSERT INTO ScriptVersions
        (ScriptId,Version,FilePath,Sha256Hash,FileSize,ReleaseNotes,IsCurrent,CreatedAt)
        VALUES ($sid,$v,$fp,$sha,$sz,$rn,1,$t);
        SELECT last_insert_rowid();";
    insVer.Parameters.AddWithValue("$sid", scriptId);
    insVer.Parameters.AddWithValue("$v", version);
    insVer.Parameters.AddWithValue("$fp", zipPath);
    insVer.Parameters.AddWithValue("$sha", sha256);
    insVer.Parameters.AddWithValue("$sz", file.Length);
    insVer.Parameters.AddWithValue("$rn", releaseNotes);
    insVer.Parameters.AddWithValue("$t", now);
    var versionId = (long)(insVer.ExecuteScalar() ?? 0L);

    using var insScan = db.CreateCommand();
    insScan.CommandText = @"INSERT INTO ScriptScanResults (VersionId,Passed,Flags,FileList,ScannedAt)
        VALUES ($vid,$p,$f,$fl,$t)";
    insScan.Parameters.AddWithValue("$vid", versionId);
    insScan.Parameters.AddWithValue("$p", scanResult.BlockedFiles.Count == 0 ? 1 : 0);
    insScan.Parameters.AddWithValue("$f", System.Text.Json.JsonSerializer.Serialize(scanResult.Flags));
    insScan.Parameters.AddWithValue("$fl", System.Text.Json.JsonSerializer.Serialize(scanResult.FileList));
    insScan.Parameters.AddWithValue("$t", now);
    insScan.ExecuteNonQuery();

    AppHelpers.LogActivity(u, "market_upload", $"script={scriptId} v={version}");

    return Results.Ok(new
    {
        scriptId,
        versionId,
        slug,
        status = "pending",
        sha256,
        fileList = scanResult.FileList,
        flags = scanResult.Flags,
        message = "Uppladdning klar. Väntar på granskning."
    });
}).DisableAntiforgery();

// ── Steg 3: Admin approve/reject ──
app.MapPost("/api/market/admin/approve/{id:long}", (long id, HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE MarketScripts SET Status='approved', UpdatedAt=$t WHERE Id=$id";
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$id", id);
    var rows = cmd.ExecuteNonQuery();
    return rows > 0 ? Results.Ok(new { success = true }) : Results.NotFound();
});

app.MapPost("/api/market/admin/reject/{id:long}", async (long id, HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    var body = await ctx.Request.ReadFromJsonAsync<MarketRejectReq>();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE MarketScripts SET Status='rejected', UpdatedAt=$t WHERE Id=$id";
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    cmd.Parameters.AddWithValue("$id", id);
    cmd.ExecuteNonQuery();
    return Results.Ok(new { success = true, reason = body?.Reason ?? "" });
});

app.MapGet("/api/market/admin/pending", (HttpContext ctx) =>
{
    if (!AppHelpers.IsAdmin(ctx.User.Identity?.Name?.Trim().ToLowerInvariant())) return Results.Forbid();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT ms.Id, ms.Name, ms.CreatorUsername, ms.Category, ms.Price, ms.CreatedAt,
               sv.Version, sv.Sha256Hash, sv.FileSize,
               sr.Passed, sr.Flags, sr.FileList
        FROM MarketScripts ms
        JOIN ScriptVersions sv ON sv.ScriptId=ms.Id AND sv.IsCurrent=1
        LEFT JOIN ScriptScanResults sr ON sr.VersionId=sv.Id
        WHERE ms.Status='pending'
        ORDER BY ms.Id ASC";
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read()) rows.Add(new
    {
        id = r.GetInt64(0),
        name = r.GetString(1),
        creator = r.GetString(2),
        category = r.GetString(3),
        price = r.GetInt32(4),
        createdAt = r.GetString(5),
        version = r.GetString(6),
        sha256 = r.GetString(7),
        fileSize = r.GetInt64(8),
        scanPassed = !r.IsDBNull(9) && r.GetInt32(9) == 1,
        scanFlags = r.IsDBNull(10) ? "[]" : r.GetString(10),
        fileList = r.IsDBNull(11) ? "[]" : r.GetString(11),
    });
    return Results.Ok(rows);
});

// ── Steg 4: Public listing ──
app.MapGet("/api/market/scripts", (HttpContext ctx, string? category, string? q, string? sort) =>
{
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    var where = new List<string> { "ms.Status='approved'" };
    if (!string.IsNullOrWhiteSpace(category)) { where.Add("ms.Category=$cat"); cmd.Parameters.AddWithValue("$cat", category.Trim().ToLowerInvariant()); }
    if (!string.IsNullOrWhiteSpace(q)) { where.Add("(ms.Name LIKE $q OR ms.Description LIKE $q OR ms.CreatorUsername LIKE $q)"); cmd.Parameters.AddWithValue("$q", "%" + InputSanitizer.SanitizeSearchQuery(q.Trim()) + "%"); }
    var orderBy = sort switch { "newest" => "ms.Id DESC", "rating" => "avgRating DESC", _ => "ms.TotalSales DESC, ms.Id DESC" };
    cmd.CommandText = $@"
        SELECT ms.Id, ms.Name, ms.Slug, ms.CreatorUsername, ms.Category, ms.Language,
               ms.Price, ms.TrustScore, ms.IsVerified, ms.TotalSales, ms.CreatedAt,
               ms.Description, sv.Version,
               COALESCE(AVG(rv.Rating),0) as avgRating, COUNT(rv.Id) as reviewCount
        FROM MarketScripts ms
        JOIN ScriptVersions sv ON sv.ScriptId=ms.Id AND sv.IsCurrent=1
        LEFT JOIN ScriptReviews rv ON rv.ScriptId=ms.Id
        WHERE {string.Join(" AND ", where)}
        GROUP BY ms.Id
        ORDER BY {orderBy}
        LIMIT 100";
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read()) rows.Add(new
    {
        id = r.GetInt64(0),
        name = r.GetString(1),
        slug = r.GetString(2),
        creator = r.GetString(3),
        category = r.GetString(4),
        language = r.GetString(5),
        price = r.GetInt32(6),
        trustScore = r.GetInt32(7),
        verified = r.GetInt32(8) == 1,
        totalSales = r.GetInt32(9),
        createdAt = r.GetString(10),
        description = r.GetString(11),
        version = r.GetString(12),
        rating = Math.Round(r.GetDouble(13), 1),
        reviewCount = r.GetInt32(14),
    });
    return Results.Ok(rows);
});

app.MapGet("/api/market/scripts/{id:long}", (long id, HttpContext ctx) =>
{
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT ms.Id, ms.Name, ms.Slug, ms.CreatorUsername, ms.Category, ms.Language,
               ms.Requirements, ms.HowToRun, ms.SupportInfo,
               ms.Price, ms.TrustScore, ms.IsVerified, ms.TotalSales,
               ms.Description, ms.CreatedAt, ms.UpdatedAt,
               sv.Version, sv.Sha256Hash, sv.FileSize, sv.ReleaseNotes,
               sr.FileList,
               COALESCE(AVG(rv.Rating),0), COUNT(rv.Id)
        FROM MarketScripts ms
        JOIN ScriptVersions sv ON sv.ScriptId=ms.Id AND sv.IsCurrent=1
        LEFT JOIN ScriptScanResults sr ON sr.VersionId=sv.Id
        LEFT JOIN ScriptReviews rv ON rv.ScriptId=ms.Id
        WHERE ms.Id=$id AND ms.Status='approved'
        GROUP BY ms.Id";
    cmd.Parameters.AddWithValue("$id", id);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return Results.NotFound(new { message = "Scriptet hittades inte." });

    // Versionshistorik
    var scriptId = r.GetInt64(0);
    var fileList = r.IsDBNull(20) ? "[]" : r.GetString(20);
    var rating = Math.Round(r.GetDouble(21), 1);
    var reviews = r.GetInt32(22);
    var result = new
    {
        id = scriptId,
        name = r.GetString(1),
        slug = r.GetString(2),
        creator = r.GetString(3),
        category = r.GetString(4),
        language = r.GetString(5),
        requirements = r.GetString(6),
        howToRun = r.GetString(7),
        supportInfo = r.GetString(8),
        price = r.GetInt32(9),
        trustScore = r.GetInt32(10),
        verified = r.GetInt32(11) == 1,
        totalSales = r.GetInt32(12),
        description = r.GetString(13),
        createdAt = r.GetString(14),
        updatedAt = r.GetString(15),
        version = r.GetString(16),
        sha256 = r.GetString(17),
        fileSize = r.GetInt64(18),
        releaseNotes = r.GetString(19),
        fileList = System.Text.Json.JsonSerializer.Deserialize<List<string>>(fileList) ?? new(),
        rating,
        reviewCount = reviews,
    };
    r.Close();
    return Results.Ok(result);
});

// ── Steg 5: Download (auth + ownership check) ──
app.MapGet("/api/market/download/{scriptId:long}", async (long scriptId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    using var db = DbHelpers.OpenDb();

    // Är det gratis eller äger användaren det?
    using var ownerCheck = db.CreateCommand();
    ownerCheck.CommandText = @"
        SELECT ms.Price, ms.CreatorUsername,
               (SELECT COUNT(*) FROM UserPurchases up
                JOIN AuthUsers au ON au.Id=up.UserId
                WHERE up.ScriptId=ms.Id AND au.Username=$u) as owned
        FROM MarketScripts ms WHERE ms.Id=$sid AND ms.Status='approved' LIMIT 1";
    ownerCheck.Parameters.AddWithValue("$u", u);
    ownerCheck.Parameters.AddWithValue("$sid", scriptId);
    using var or = ownerCheck.ExecuteReader();
    if (!or.Read()) return Results.NotFound(new { message = "Scriptet hittades inte." });

    var price = or.GetInt32(0);
    var creator = or.GetString(1);
    var owned = or.GetInt32(2) > 0;
    or.Close();

    var isCreator = creator == u;
    var isAdmin = AppHelpers.IsAdmin(u);

    if (price > 0 && !owned && !isCreator && !isAdmin)
        return Results.Json(new { message = "Du äger inte detta script." }, statusCode: 403);

    // Hämta filsökväg
    using var fileCmd = db.CreateCommand();
    fileCmd.CommandText = "SELECT FilePath, Version FROM ScriptVersions WHERE ScriptId=$sid AND IsCurrent=1 LIMIT 1";
    fileCmd.Parameters.AddWithValue("$sid", scriptId);
    using var fr = fileCmd.ExecuteReader();
    if (!fr.Read()) return Results.NotFound(new { message = "Ingen version hittad." });
    var filePath = fr.GetString(0);
    var version = fr.GetString(1);
    fr.Close();

    if (!System.IO.File.Exists(filePath))
        return Results.Problem("Filen saknas på servern.", statusCode: 500);

    // Hämta scriptnamn för filnamnet
    using var nameCmd = db.CreateCommand();
    nameCmd.CommandText = "SELECT Name FROM MarketScripts WHERE Id=$sid LIMIT 1";
    nameCmd.Parameters.AddWithValue("$sid", scriptId);
    var scriptName = (nameCmd.ExecuteScalar() as string ?? "script").ToLowerInvariant();
    scriptName = System.Text.RegularExpressions.Regex.Replace(scriptName, @"[^a-z0-9]+", "-");

    AppHelpers.LogActivity(u, "market_download", $"script={scriptId} v={version}");

    var fileName = $"{scriptName}-v{version}.zip";
    var stream = System.IO.File.OpenRead(filePath);
    return Results.File(stream, "application/zip", fileName);
});

// ── Steg 6: Purchase (intern, utan Stripe) ──
app.MapPost("/api/market/purchase", async (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    var req = await ctx.Request.ReadFromJsonAsync<MarketPurchaseReq>();
    if (req == null || req.ScriptId <= 0) return Results.BadRequest(new { error = "scriptId krävs." });

    using var db = DbHelpers.OpenDb();

    // Kolla att scriptet finns och är approved
    using var scriptCmd = db.CreateCommand();
    scriptCmd.CommandText = "SELECT Price, Name FROM MarketScripts WHERE Id=$sid AND Status='approved' LIMIT 1";
    scriptCmd.Parameters.AddWithValue("$sid", req.ScriptId);
    using var sr = scriptCmd.ExecuteReader();
    if (!sr.Read()) return Results.NotFound(new { error = "Scriptet hittades inte." });
    var price = sr.GetInt32(0);
    var name = sr.GetString(1);
    sr.Close();

    // Hämta version
    using var verCmd = db.CreateCommand();
    verCmd.CommandText = "SELECT Version FROM ScriptVersions WHERE ScriptId=$sid AND IsCurrent=1 LIMIT 1";
    verCmd.Parameters.AddWithValue("$sid", req.ScriptId);
    var currentVersion = verCmd.ExecuteScalar() as string ?? "";

    // Redan köpt?
    using var existsCmd = db.CreateCommand();
    existsCmd.CommandText = @"SELECT COUNT(*) FROM UserPurchases up
        JOIN AuthUsers au ON au.Id=up.UserId
        WHERE up.ScriptId=$sid AND au.Username=$u";
    existsCmd.Parameters.AddWithValue("$sid", req.ScriptId);
    existsCmd.Parameters.AddWithValue("$u", u);
    if (Convert.ToInt32(existsCmd.ExecuteScalar()) > 0)
        return Results.Ok(new { success = true, alreadyOwned = true, message = "Du äger redan detta script." });

    if (price > 0)
        return Results.Json(new { error = "Betald köp ej aktiverat än. Kontakta support." }, statusCode: 402);

    // Gratis — spara köp direkt
    using var insCmd = db.CreateCommand();
    insCmd.CommandText = @"INSERT INTO UserPurchases (UserId, ScriptId, VersionAtPurchase, PurchasedAt)
        SELECT au.Id, $sid, $ver, $t FROM AuthUsers au WHERE au.Username=$u";
    insCmd.Parameters.AddWithValue("$sid", req.ScriptId);
    insCmd.Parameters.AddWithValue("$ver", currentVersion);
    insCmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    insCmd.Parameters.AddWithValue("$u", u);
    insCmd.ExecuteNonQuery();

    // Öka TotalSales
    using var salesCmd = db.CreateCommand();
    salesCmd.CommandText = "UPDATE MarketScripts SET TotalSales=TotalSales+1 WHERE Id=$sid";
    salesCmd.Parameters.AddWithValue("$sid", req.ScriptId);
    salesCmd.ExecuteNonQuery();

    AppHelpers.LogActivity(u, "market_purchase", $"script={req.ScriptId} name={name}");
    return Results.Ok(new { success = true, message = $"Du äger nu {name}. Ladda ner när du vill." });
});

// ── Library: användarens köpta scripts ──
app.MapGet("/api/market/library", (HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT ms.Id, ms.Name, ms.Slug, ms.Category, ms.Language, ms.Price,
               up.PurchasedAt, up.VersionAtPurchase, sv.Version
        FROM UserPurchases up
        JOIN AuthUsers au ON au.Id=up.UserId
        JOIN MarketScripts ms ON ms.Id=up.ScriptId
        JOIN ScriptVersions sv ON sv.ScriptId=ms.Id AND sv.IsCurrent=1
        WHERE au.Username=$u
        ORDER BY up.Id DESC";
    cmd.Parameters.AddWithValue("$u", u);
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read()) rows.Add(new
    {
        id = r.GetInt64(0),
        name = r.GetString(1),
        slug = r.GetString(2),
        category = r.GetString(3),
        language = r.GetString(4),
        price = r.GetInt32(5),
        purchasedAt = r.GetString(6),
        versionAtPurchase = r.GetString(7),
        currentVersion = r.GetString(8),
    });
    return Results.Ok(rows);
});

// ── Reviews ──
app.MapGet("/api/market/reviews/{scriptId:long}", (long scriptId, HttpContext ctx) =>
{
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT au.Username, rv.Rating, rv.ReviewText, rv.CreatedAt
        FROM ScriptReviews rv JOIN AuthUsers au ON au.Id=rv.UserId
        WHERE rv.ScriptId=$sid ORDER BY rv.Id DESC LIMIT 50";
    cmd.Parameters.AddWithValue("$sid", scriptId);
    using var r = cmd.ExecuteReader();
    var rows = new List<object>();
    while (r.Read()) rows.Add(new
    {
        username = r.GetString(0),
        rating = r.GetInt32(1),
        reviewText = r.GetString(2),
        createdAt = r.GetString(3),
    });
    return Results.Ok(rows);
});

app.MapPost("/api/market/reviews/{scriptId:long}", async (long scriptId, HttpContext ctx) =>
{
    var u = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(u) || !AppHelpers.UserExists(u)) return Results.Unauthorized();

    var req = await ctx.Request.ReadFromJsonAsync<MarketReviewReq>();
    if (req == null || req.Rating < 1 || req.Rating > 5)
        return Results.BadRequest(new { error = "Rating 1–5 krävs." });

    using var db = DbHelpers.OpenDb();

    // Måste äga scriptet för att recensera
    using var ownCheck = db.CreateCommand();
    ownCheck.CommandText = @"SELECT COUNT(*) FROM UserPurchases up
        JOIN AuthUsers au ON au.Id=up.UserId WHERE up.ScriptId=$sid AND au.Username=$u";
    ownCheck.Parameters.AddWithValue("$sid", scriptId);
    ownCheck.Parameters.AddWithValue("$u", u);
    if (Convert.ToInt32(ownCheck.ExecuteScalar()) == 0)
        return Results.Json(new { error = "Du måste äga scriptet för att recensera." }, statusCode: 403);

    using var ins = db.CreateCommand();
    ins.CommandText = @"INSERT INTO ScriptReviews (ScriptId,UserId,Rating,ReviewText,CreatedAt)
        SELECT $sid, au.Id, $r, $txt, $t FROM AuthUsers au WHERE au.Username=$u
        ON CONFLICT(ScriptId,UserId) DO UPDATE SET Rating=excluded.Rating, ReviewText=excluded.ReviewText";
    ins.Parameters.AddWithValue("$sid", scriptId);
    ins.Parameters.AddWithValue("$r", req.Rating);
    ins.Parameters.AddWithValue("$txt", InputSanitizer.SanitizeInput(req.ReviewText ?? "", 500));
    ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    ins.Parameters.AddWithValue("$u", u);
    ins.ExecuteNonQuery();
    return Results.Ok(new { success = true });
});


// ── Auto-updater endpoint ─────────────────────────────────────────────

// ── YouTube audio proxy ──
app.MapGet("/api/music/yt-audio", async (string v, HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(v) || !System.Text.RegularExpressions.Regex.IsMatch(v, @"^[a-zA-Z0-9_-]{11}$"))
        return Results.BadRequest(new { error = "Invalid video ID" });
    var psi = new System.Diagnostics.ProcessStartInfo("yt-dlp")
    {
        Arguments = $"--get-url -f bestaudio/best --no-playlist -- {v}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    using var proc = System.Diagnostics.Process.Start(psi)!;
    var url = (await proc.StandardOutput.ReadToEndAsync()).Trim().Split("\n")[0].Trim();
    await proc.WaitForExitAsync();
    if (string.IsNullOrEmpty(url) || proc.ExitCode != 0)
        return Results.Problem("yt-dlp failed");
    return Results.Ok(new { url });
});

app.MapGet("/api/desktop/version", async () =>
{
    const string versionFile = "/var/www/runspace/desktop-version.json";
    if (!System.IO.File.Exists(versionFile))
        return Results.NotFound(new { error = "No version info configured" });
    var json = await System.IO.File.ReadAllTextAsync(versionFile);
    return Results.Content(json, "application/json");
});


// ═══════════════════════════════════════════════════════════════
// ENCRYPTION KEY MANAGEMENT (Server-side per-user keys)
// ═══════════════════════════════════════════════════════════════

// POST /api/encryption/register-keys - Register or update user's encryption keys

app.MapPost("/api/encryption/register-keys", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    // rs-encryption-register-keys-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "encryption_register_keys", 10, 3600))
        return Results.Json(new { message = "Rate limit." }, statusCode: 429);

    JsonElement data;
    try
    {
        var parsed = await ctx.Request.ReadFromJsonAsync<JsonElement>();
        data = parsed;
    }
    catch
    {
        return Results.BadRequest(new { message = "Invalid JSON." });
    }

    if (!data.TryGetProperty("publicKey", out var pubKeyEl) ||
        !data.TryGetProperty("encryptedPrivateKey", out var privKeyEl))
        return Results.BadRequest(new { message = "Public and private keys are required." });

    var rawPublicKey = pubKeyEl.GetString();
    var rawEncryptedPrivateKey = privKeyEl.GetString();

    if (!DefensiveInput.TryNormalizeRsaPublicKey(rawPublicKey, 8192, out var publicKey))
        return Results.BadRequest(new { message = "Invalid public key." });

    if (!DefensiveInput.IsSafeBase64ish(rawEncryptedPrivateKey, 65536))
        return Results.BadRequest(new { message = "Invalid encrypted private key." });

    var encryptedPrivateKey = rawEncryptedPrivateKey!.Trim();

    using var conn = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await conn.OpenAsync();
    var userIdCmd = conn.CreateCommand();
    userIdCmd.CommandText = "SELECT Id FROM AuthUsers WHERE LOWER(Username)=LOWER(@username) LIMIT 1";
    userIdCmd.Parameters.AddWithValue("@username", username);
    var userIdObj = await userIdCmd.ExecuteScalarAsync();
    if (userIdObj == null || userIdObj == DBNull.Value)
        return Results.Unauthorized();
    var userId = Convert.ToInt64(userIdObj);


    var checkCmd = conn.CreateCommand();
    checkCmd.CommandText = "SELECT COUNT(*) FROM UserEncryptionKeys WHERE UserId = @userId";
    checkCmd.Parameters.AddWithValue("@userId", userId);
    var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

    if (exists)
    {
        var updateCmd = conn.CreateCommand();
        updateCmd.CommandText = @"
            UPDATE UserEncryptionKeys
            SET PublicKey = @pubKey,
                EncryptedPrivateKey = @privKey,
                KeyVersion = KeyVersion + 1,
                UpdatedAt = @now
            WHERE UserId = @userId";
        updateCmd.Parameters.AddWithValue("@userId", userId);
        updateCmd.Parameters.AddWithValue("@pubKey", publicKey);
        updateCmd.Parameters.AddWithValue("@privKey", encryptedPrivateKey);
        updateCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        await updateCmd.ExecuteNonQueryAsync();
    }
    else
    {
        var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO UserEncryptionKeys
            (UserId, PublicKey, EncryptedPrivateKey, KeyVersion, CreatedAt, UpdatedAt)
            VALUES (@userId, @pubKey, @privKey, 1, @now, @now)";
        insertCmd.Parameters.AddWithValue("@userId", userId);
        insertCmd.Parameters.AddWithValue("@pubKey", publicKey);
        insertCmd.Parameters.AddWithValue("@privKey", encryptedPrivateKey);
        insertCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        await insertCmd.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { message = "Keys registered successfully" });
}).RequireAuthorization();

app.MapGet("/api/encryption/my-keys", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var conn = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await conn.OpenAsync();
    var userIdCmd = conn.CreateCommand();
    userIdCmd.CommandText = "SELECT Id FROM AuthUsers WHERE LOWER(Username)=LOWER(@username) LIMIT 1";
    userIdCmd.Parameters.AddWithValue("@username", username);
    var userIdObj = await userIdCmd.ExecuteScalarAsync();
    if (userIdObj == null || userIdObj == DBNull.Value)
        return Results.Unauthorized();
    var userId = Convert.ToInt64(userIdObj);


    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT PublicKey, EncryptedPrivateKey, KeyVersion 
        FROM UserEncryptionKeys 
        WHERE UserId = @userId";
    cmd.Parameters.AddWithValue("@userId", userId);

    using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        return Results.Ok(new
        {
            publicKey = reader.GetString(0),
            encryptedPrivateKey = reader.GetString(1),
            keyVersion = reader.GetInt32(2)
        });
    }

    return Results.NotFound(new { message = "No keys found" });
}).RequireAuthorization();

// GET /api/encryption/public-key/{username} - Get another user's public key

app.MapGet("/api/encryption/public-key/{username}", async (string username) =>
{
    // rs-encryption-public-key-defensive-v1
    var target = DefensiveInput.CleanString(username, 32).ToLowerInvariant();

    if (!DefensiveInput.IsUsername(target) || !AppHelpers.IsValidUsername(target))
        return Results.BadRequest(new { message = "Invalid username." });

    using var conn = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await conn.OpenAsync();

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT uek.PublicKey, uek.KeyVersion
        FROM UserEncryptionKeys uek
        INNER JOIN AuthUsers u ON u.Id = uek.UserId
        WHERE LOWER(u.Username) = @username";
    cmd.Parameters.AddWithValue("@username", target);

    using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        return Results.Ok(new
        {
            publicKey = reader.GetString(0),
            keyVersion = reader.GetInt32(1)
        });
    }

    return Results.NotFound(new { message = "User has no encryption keys" });
}).RequireAuthorization();

app.MapPost("/api/auth/logout-all-devices", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(username))
        return Results.Unauthorized();

    using var conn = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await conn.OpenAsync();

    // Delete all sessions for this user
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM PersistentSessions WHERE Username = @username";
    cmd.Parameters.AddWithValue("@username", username);
    var deleted = await cmd.ExecuteNonQueryAsync();

    // Sign out current session
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    return Results.Ok(new { message = $"Logged out from {deleted} device(s)", devicesLoggedOut = deleted });
}).RequireAuthorization();

// GET /api/auth/active-sessions - List active sessions
app.MapGet("/api/auth/active-sessions", async (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(username))
        return Results.Unauthorized();

    using var conn = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await conn.OpenAsync();

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT SessionId, Ip, UserAgent, CreatedAt, LastActivity 
        FROM PersistentSessions 
        WHERE Username = @username 
        ORDER BY LastActivity DESC";
    cmd.Parameters.AddWithValue("@username", username);

    var sessions = new List<object>();
    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var currentSessionId = ctx.Request.Cookies["runspace_auth_v3"];
        var isCurrentSession = reader.GetString(0) == currentSessionId;

        sessions.Add(new
        {
            sessionId = reader.GetString(0).Substring(0, 8) + "...",
            ip = reader.GetString(1),
            userAgent = reader.GetString(2),
            createdAt = reader.GetString(3),
            lastActivity = reader.GetString(4),
            isCurrent = isCurrentSession
        });
    }

    return Results.Ok(new { sessions });
}).RequireAuthorization();



app.MapGet("/api/gif/search", async (string? q, HttpContext ctx) =>
{
    if (!ctx.User.Identity?.IsAuthenticated ?? true) return Results.Unauthorized();
    var apiKey = Environment.GetEnvironmentVariable("GIPHY_API_KEY") ?? "";
    var url = string.IsNullOrEmpty(q)
        ? $"https://api.giphy.com/v1/gifs/trending?api_key={apiKey}&limit=12&rating=g"
        : $"https://api.giphy.com/v1/gifs/search?api_key={apiKey}&q={Uri.EscapeDataString(q)}&limit=12&rating=g";
    using var http = new HttpClient();
    var resp = await http.GetStringAsync(url);
    return Results.Content(resp, "application/json");
});
















// ── Account Recovery Codes ──
// These are for lost Account Key recovery, not 2FA backup codes.
app.MapGet("/api/auth/account-recovery/status", (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var db = DbHelpers.OpenDb();

    using (var migrate = db.CreateCommand())
    {
        migrate.CommandText = @"
            CREATE TABLE IF NOT EXISTS AccountRecoveryCodes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL,
                CodeHash TEXT NOT NULL,
                UsedAt TEXT,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );";
        migrate.ExecuteNonQuery();
    }

    using (var idx = db.CreateCommand())
    {
        idx.CommandText = "CREATE INDEX IF NOT EXISTS IX_AccountRecoveryCodes_Username ON AccountRecoveryCodes(Username);";
        idx.ExecuteNonQuery();
    }

    using var count = db.CreateCommand();
    count.CommandText = @"
        SELECT COUNT(*)
        FROM AccountRecoveryCodes
        WHERE LOWER(Username)=LOWER($u)
          AND UsedAt IS NULL";
    count.Parameters.AddWithValue("$u", username);

    var remaining = Convert.ToInt32(count.ExecuteScalar() ?? 0);

    return Results.Ok(new
    {
        ok = true,
        remaining
    });
});

app.MapPost("/api/auth/account-recovery/generate", (HttpContext ctx) =>
{
    var username = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    if (!limiter.IsAllowed(ip + ":" + username, "account_recovery_generate", 3, 3600))
        return Results.Json(new { ok = false, message = "Too many recovery code generations. Try again later." }, statusCode: 429);

    static string HashAccountRecoveryCode(string code)
    {
        var normalized = (code ?? "").Trim().ToLowerInvariant();
        var material = "runspace:account-recovery:v1:" + normalized;

        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(AppConfig.PasswordPepper)
        );

        return Convert.ToHexString(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(material))
        ).ToLowerInvariant();
    }

    static string NewRecoveryCode()
    {
        var raw = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return "rsr-" + raw.Substring(0, 4) + "-" + raw.Substring(4, 4) + "-" + raw.Substring(8, 4) + "-" + raw.Substring(12, 4);
    }

    using var db = DbHelpers.OpenDb();

    using (var migrate = db.CreateCommand())
    {
        migrate.CommandText = @"
            CREATE TABLE IF NOT EXISTS AccountRecoveryCodes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL,
                CodeHash TEXT NOT NULL,
                UsedAt TEXT,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );";
        migrate.ExecuteNonQuery();
    }

    using (var idx = db.CreateCommand())
    {
        idx.CommandText = "CREATE INDEX IF NOT EXISTS IX_AccountRecoveryCodes_Username ON AccountRecoveryCodes(Username);";
        idx.ExecuteNonQuery();
    }

    var codes = new List<string>();

    using var tx = db.BeginTransaction();

    using (var del = db.CreateCommand())
    {
        del.Transaction = tx;
        del.CommandText = "DELETE FROM AccountRecoveryCodes WHERE LOWER(Username)=LOWER($u)";
        del.Parameters.AddWithValue("$u", username);
        del.ExecuteNonQuery();
    }

    for (var i = 0; i < 10; i++)
    {
        var code = NewRecoveryCode();
        codes.Add(code);

        using var ins = db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
            INSERT INTO AccountRecoveryCodes (Username, CodeHash, CreatedAt)
            VALUES ($u, $h, datetime('now'))";
        ins.Parameters.AddWithValue("$u", username);
        ins.Parameters.AddWithValue("$h", HashAccountRecoveryCode(code));
        ins.ExecuteNonQuery();
    }

    tx.Commit();

    AppHelpers.LogActivity(username, "account_recovery_codes_generated", $"From {ip}");

    return Results.Ok(new
    {
        ok = true,
        codes,
        remaining = codes.Count,
        message = "Recovery codes generated. Store them safely. They are shown only once."
    });
});


app.MapPost("/api/auth/account-recovery/redeem", async (HttpContext ctx) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    System.Text.Json.JsonElement body;
    try
    {
        body = await ctx.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
    }
    catch
    {
        return Results.BadRequest(new { ok = false, message = "Invalid request body." });
    }

    string username = "";
    string recoveryCode = "";
    string newAccountKey = "";

    if (body.ValueKind == System.Text.Json.JsonValueKind.Object)
    {
        if (body.TryGetProperty("username", out var u1)) username = u1.GetString() ?? "";
        if (body.TryGetProperty("Username", out var u2) && string.IsNullOrWhiteSpace(username)) username = u2.GetString() ?? "";

        if (body.TryGetProperty("recoveryCode", out var c1)) recoveryCode = c1.GetString() ?? "";
        if (body.TryGetProperty("RecoveryCode", out var c2) && string.IsNullOrWhiteSpace(recoveryCode)) recoveryCode = c2.GetString() ?? "";

        if (body.TryGetProperty("accountKey", out var k1)) newAccountKey = k1.GetString() ?? "";
        if (body.TryGetProperty("AccountKey", out var k2) && string.IsNullOrWhiteSpace(newAccountKey)) newAccountKey = k2.GetString() ?? "";
    }

    username = DefensiveInput.CleanString(username, 32).Trim().ToLowerInvariant();
    recoveryCode = DefensiveInput.CleanString(recoveryCode, 64).Trim().ToLowerInvariant();
    newAccountKey = AccountKeyHashing.Normalize(newAccountKey);

    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(recoveryCode) || string.IsNullOrWhiteSpace(newAccountKey))
        return Results.BadRequest(new { ok = false, message = "Username, recovery code and new Account Key are required." });

    if (!DefensiveInput.IsUsername(username) || !AppHelpers.IsValidUsername(username))
        return Results.Json(new { ok = false, message = "Invalid username or recovery code." }, statusCode: 401);

    if (!System.Text.RegularExpressions.Regex.IsMatch(
        recoveryCode,
        @"^rsr-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        return Results.Json(new { ok = false, message = "Invalid username or recovery code." }, statusCode: 401);
    }

    if (!System.Text.RegularExpressions.Regex.IsMatch(
        newAccountKey,
        @"^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
    {
        return Results.BadRequest(new { ok = false, message = "Invalid new Account Key format." });
    }

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(ip + ":" + username, "account_recovery_redeem", 5, 3600))
        return Results.Json(new { ok = false, message = "Too many recovery attempts. Try again later." }, statusCode: 429);

    static string HashAccountRecoveryCode(string code)
    {
        var normalized = (code ?? "").Trim().ToLowerInvariant();
        var material = "runspace:account-recovery:v1:" + normalized;

        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(AppConfig.PasswordPepper)
        );

        return Convert.ToHexString(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(material))
        ).ToLowerInvariant();
    }

    var recoveryHash = HashAccountRecoveryCode(recoveryCode);
    var newAccountKeyHash = AccountKeyHashing.Hash(newAccountKey);

    using var db = DbHelpers.OpenDb();

    using (var migrate = db.CreateCommand())
    {
        migrate.CommandText = @"
            CREATE TABLE IF NOT EXISTS AccountRecoveryCodes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL,
                CodeHash TEXT NOT NULL,
                UsedAt TEXT,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );";
        migrate.ExecuteNonQuery();
    }

    using var tx = db.BeginTransaction();

    long codeId = 0;

    using (var find = db.CreateCommand())
    {
        find.Transaction = tx;
        find.CommandText = @"
            SELECT Id
            FROM AccountRecoveryCodes
            WHERE LOWER(Username)=LOWER($u)
              AND CodeHash=$h
              AND UsedAt IS NULL
            LIMIT 1";
        find.Parameters.AddWithValue("$u", username);
        find.Parameters.AddWithValue("$h", recoveryHash);

        var obj = find.ExecuteScalar();
        if (obj == null || obj == DBNull.Value)
        {
            tx.Rollback();
            return Results.Json(new { ok = false, message = "Invalid username or recovery code." }, statusCode: 401);
        }

        codeId = Convert.ToInt64(obj);
    }

    using (var dup = db.CreateCommand())
    {
        dup.Transaction = tx;
        dup.CommandText = @"
            SELECT COUNT(*)
            FROM AuthUsers
            WHERE (AccountKeyHash=$kh OR AccountKey=$k)
              AND LOWER(Username) <> LOWER($u)";
        dup.Parameters.AddWithValue("$kh", newAccountKeyHash);
        dup.Parameters.AddWithValue("$k", newAccountKey);
        dup.Parameters.AddWithValue("$u", username);

        if (Convert.ToInt32(dup.ExecuteScalar() ?? 0) > 0)
        {
            tx.Rollback();
            return Results.Json(new { ok = false, message = "New Account Key already in use." }, statusCode: 409);
        }
    }

    using (var updUser = db.CreateCommand())
    {
        updUser.Transaction = tx;
        updUser.CommandText = @"
            UPDATE AuthUsers
            SET AccountKey=$k,
                AccountKeyHash=$kh
            WHERE LOWER(Username)=LOWER($u)";
        updUser.Parameters.AddWithValue("$k", newAccountKey);
        updUser.Parameters.AddWithValue("$kh", newAccountKeyHash);
        updUser.Parameters.AddWithValue("$u", username);

        var changed = updUser.ExecuteNonQuery();
        if (changed <= 0)
        {
            tx.Rollback();
            return Results.Json(new { ok = false, message = "Invalid username or recovery code." }, statusCode: 401);
        }
    }

    using (var mark = db.CreateCommand())
    {
        mark.Transaction = tx;
        mark.CommandText = @"
            UPDATE AccountRecoveryCodes
            SET UsedAt=datetime('now')
            WHERE LOWER(Username)=LOWER($u)
              AND UsedAt IS NULL";
        mark.Parameters.AddWithValue("$u", username);
        mark.ExecuteNonQuery();
    }

    using (var delSessions = db.CreateCommand())
    {
        delSessions.Transaction = tx;
        delSessions.CommandText = "DELETE FROM PersistentSessions WHERE LOWER(Username)=LOWER($u)";
        delSessions.Parameters.AddWithValue("$u", username);
        delSessions.ExecuteNonQuery();
    }

    tx.Commit();

    ctx.RequestServices.GetRequiredService<SessionManager>().InvalidateAllSessions(username);
    AppHelpers.LogActivity(username, "account_recovery_redeemed", $"CodeId={codeId} From {ip}");

    return Results.Ok(new
    {
        ok = true,
        message = "Account recovered. New Account Key activated. Existing sessions were logged out.",
        preview = newAccountKey.Substring(0, 8) + "..."
    });
});

// ── Key Recovery Request ──

app.MapPost("/api/auth/request-key-reset", async (HttpContext ctx) =>
{
    // rs-key-reset-request-defensive-v1
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(ip, "key_reset_request", 5, 3600))
        return Results.Json(new { ok = true, message = "If the account exists and the email is verified, a recovery request has been created." });

    KeyResetReq? req;
    try
    {
        req = await ctx.Request.ReadFromJsonAsync<KeyResetReq>();
    }
    catch
    {
        return Results.Json(new { ok = true, message = "If the account exists and the email is verified, a recovery request has been created." });
    }

    if (req == null)
        return Results.Json(new { ok = true, message = "If the account exists and the email is verified, a recovery request has been created." });

    var username = DefensiveInput.CleanString(req.Username, 32).ToLowerInvariant();
    var email = DefensiveInput.CleanString(req.Email, 254).ToLowerInvariant();
    var reason = DefensiveInput.CleanString(req.Reason, 500);
    var ua = DefensiveInput.CleanString(ctx.Request.Headers.UserAgent.ToString(), 512);

    if (!DefensiveInput.IsUsername(username) || !AppHelpers.IsValidUsername(username) || !DefensiveInput.IsEmail(email))
        return Results.Json(new { ok = true, message = "If the account exists and the email is verified, a recovery request has been created." });

    using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Username, Email
        FROM AuthUsers
        WHERE LOWER(Username) = $u
          AND LOWER(Email) = $e
          AND EmailVerified = 1
        LIMIT 1";
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$e", email);

    using var r = await cmd.ExecuteReaderAsync();

    if (await r.ReadAsync())
    {
        var userId = r.GetInt64(0);
        var realUsername = r.GetString(1);
        var realEmail = r.GetString(2);

        using var ins = db.CreateCommand();
        ins.CommandText = @"
            INSERT INTO KeyResetRequests
            (UserId, Username, Email, Reason, Status, RequestedIp, RequestedUserAgent)
            VALUES
            ($uid, $u, $e, $reason, 'pending', $ip, $ua)";
        ins.Parameters.AddWithValue("$uid", userId);
        ins.Parameters.AddWithValue("$u", realUsername);
        ins.Parameters.AddWithValue("$e", realEmail);
        ins.Parameters.AddWithValue("$reason", reason);
        ins.Parameters.AddWithValue("$ip", ip);
        ins.Parameters.AddWithValue("$ua", ua);
        await ins.ExecuteNonQueryAsync();

        Console.WriteLine("[security] recovery started.");
    }

    return Results.Json(new
    {
        ok = true,
        message = "If the account exists and the email is verified, a recovery request has been created."
    });
});

app.MapGet("/api/admin/key-reset-requests", async (HttpContext ctx) =>
{
    var adminUser = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(adminUser) || !AppHelpers.IsAdmin(adminUser))
        return Results.Forbid();

    using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, UserId, Username, Email, Status, Reason, EmailConfirmed, RequestedIp, RequestedUserAgent, CreatedAt, ApprovedAt, RejectedAt, AdminNote
        FROM KeyResetRequests
        ORDER BY Id DESC
        LIMIT 100";

    var list = new List<object>();
    using var r = await cmd.ExecuteReaderAsync();

    while (await r.ReadAsync())
    {
        list.Add(new
        {
            id = r.GetInt64(0),
            userId = r.IsDBNull(1) ? 0 : r.GetInt64(1),
            username = r.GetString(2),
            email = r.GetString(3),
            status = r.GetString(4),
            reason = r.IsDBNull(5) ? "" : r.GetString(5),
            emailConfirmed = !r.IsDBNull(6) && r.GetInt32(6) == 1,
            requestedIp = r.IsDBNull(7) ? "" : r.GetString(7),
            requestedUserAgent = r.IsDBNull(8) ? "" : r.GetString(8),
            createdAt = r.GetString(9),
            approvedAt = r.IsDBNull(10) ? "" : r.GetString(10),
            rejectedAt = r.IsDBNull(11) ? "" : r.GetString(11),
            adminNote = r.IsDBNull(12) ? "" : r.GetString(12)
        });
    }

    return Results.Json(new { ok = true, requests = list });
});


app.MapPost("/api/admin/key-reset-reject", async (HttpContext ctx) =>
{
    var adminUser = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(adminUser) || !AppHelpers.IsAdmin(adminUser))
        return Results.Forbid();

    // rs-key-reset-reject-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(adminUser, "key_reset_reject", 30, 900))
        return Results.Json(new { error = "Rate limit." }, statusCode: 429);

    KeyResetRejectReq? req;
    try
    {
        req = await ctx.Request.ReadFromJsonAsync<KeyResetRejectReq>();
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid request." });
    }

    if (req == null || req.RequestId <= 0)
        return Results.BadRequest(new { error = "Invalid request." });

    var reason = DefensiveInput.CleanString(req.Reason, 500);

    using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        UPDATE KeyResetRequests
        SET Status='rejected',
            RejectedAt=CURRENT_TIMESTAMP,
            RejectedBy='admin',
            AdminNote=$reason
        WHERE Id=$id AND Status='pending'";
    cmd.Parameters.AddWithValue("$id", req.RequestId);
    cmd.Parameters.AddWithValue("$reason", reason);

    var changed = await cmd.ExecuteNonQueryAsync();
    return Results.Json(new { ok = changed > 0 });
});

app.MapPost("/api/admin/key-reset-approve", async (HttpContext ctx) =>
{
    var adminUser = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(adminUser) || !AppHelpers.IsAdmin(adminUser))
        return Results.Forbid();

    // rs-key-reset-approve-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(adminUser, "key_reset_approve", 10, 900))
        return Results.Json(new { error = "Rate limit." }, statusCode: 429);

    KeyResetApproveReq? req;
    try
    {
        req = await ctx.Request.ReadFromJsonAsync<KeyResetApproveReq>();
    }
    catch
    {
        return Results.BadRequest(new { error = "Invalid request." });
    }

    if (req == null || req.RequestId <= 0 || string.IsNullOrWhiteSpace(req.Passphrase))
        return Results.BadRequest(new { error = "RequestId and passphrase required." });

    var passphrase = req.Passphrase;
    if (passphrase.Length < 8 || passphrase.Length > 512 || passphrase.Contains('\0'))
        return Results.BadRequest(new { error = "Invalid passphrase." });

    using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    using var get = db.CreateCommand();
    get.CommandText = @"
        SELECT Id, UserId, Username, Email, Status
        FROM KeyResetRequests
        WHERE Id=$id
        LIMIT 1";
    get.Parameters.AddWithValue("$id", req.RequestId);

    using var r = await get.ExecuteReaderAsync();
    if (!await r.ReadAsync())
        return Results.NotFound(new { error = "Request not found." });

    var userId = r.GetInt64(1);
    var username = r.GetString(2);
    var email = r.GetString(3);
    var status = r.GetString(4);
    r.Close();

    if (status != "pending")
        return Results.BadRequest(new { error = "Request is not pending." });

    var newKey = Guid.NewGuid().ToString();

    var header = new byte[] { 0x52, 0x53, 0x4B, 0x31 };
    var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    var iv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);

    var keyMaterial = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
        System.Text.Encoding.UTF8.GetBytes(passphrase),
        salt,
        100000,
        System.Security.Cryptography.HashAlgorithmName.SHA256,
        32
    );

    using var aes = new System.Security.Cryptography.AesGcm(keyMaterial, 16);
    var plaintext = System.Text.Encoding.UTF8.GetBytes(newKey);
    var ciphertext = new byte[plaintext.Length];
    var tag = new byte[16];

    aes.Encrypt(iv, plaintext, ciphertext, tag);

    var keyFileBytes = header
        .Concat(salt)
        .Concat(iv)
        .Concat(ciphertext)
        .Concat(tag)
        .ToArray();

    using var updUser = db.CreateCommand();
    updUser.CommandText = "UPDATE AuthUsers SET AccountKey=$key, AccountKeyHash=$keyHash WHERE Id=$uid";
    updUser.Parameters.AddWithValue("$key", newKey);
    updUser.Parameters.AddWithValue("$keyHash", AccountKeyHashing.Hash(newKey));
    updUser.Parameters.AddWithValue("$uid", userId);
    await updUser.ExecuteNonQueryAsync();

    using var updReq = db.CreateCommand();
    updReq.CommandText = @"
        UPDATE KeyResetRequests
        SET Status='completed',
            ApprovedAt=CURRENT_TIMESTAMP,
            ApprovedBy='admin'
        WHERE Id=$id";
    updReq.Parameters.AddWithValue("$id", req.RequestId);
    await updReq.ExecuteNonQueryAsync();

    await SmtpMailService.SendKeyFileAsync(email, username, keyFileBytes, newKey);

    Console.WriteLine("[security] recovery approved.");

    return Results.Json(new { ok = true, message = "New key generated and sent." });
});




app.MapPost("/api/key-change/validate", async (HttpContext ctx) =>
{
    // rs-key-change-validate-defensive-v1
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(ip, "key_change_validate", 60, 900))
        return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: 429);

    JsonElement root;
    try
    {
        root = await ctx.Request.ReadFromJsonAsync<JsonElement>();
    }
    catch
    {
        return Results.BadRequest(new { ok = false, error = "invalid_body" });
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("token", out var tokenEl) ||
        tokenEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { ok = false, error = "invalid_token" });

    var token = tokenEl.GetString()?.Trim() ?? "";

    if (token.Length < 20 ||
        token.Length > 512 ||
        token.Contains('\0') ||
        !System.Text.RegularExpressions.Regex.IsMatch(token, @"^[A-Za-z0-9._~+/=-]{20,512}$"))
        return Results.BadRequest(new { ok = false, error = "invalid_token" });

    var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    var tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await conn.OpenAsync();

    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT Username, ExpiresAt, UsedAt
        FROM KeyChangeTokens
        WHERE TokenHash = $h
        LIMIT 1
    """;
    cmd.Parameters.AddWithValue("$h", tokenHash);

    using var r = await cmd.ExecuteReaderAsync();

    if (!await r.ReadAsync())
        return Results.Ok(new { ok = false, error = "not_found" });

    var username = r.GetString(0);
    var expiresAtRaw = r.GetString(1);
    var usedAt = r.IsDBNull(2) ? "" : r.GetString(2);

    if (!string.IsNullOrWhiteSpace(usedAt))
        return Results.Ok(new { ok = false, error = "already_used" });

    if (!DateTimeOffset.TryParse(expiresAtRaw, out var expiresAt))
        return Results.Ok(new { ok = false, error = "bad_expiry" });

    if (expiresAt < DateTimeOffset.UtcNow)
        return Results.Ok(new { ok = false, error = "expired" });

    return Results.Ok(new { ok = true, username });
});

app.MapPost("/api/key-change/consume", async (HttpContext ctx) =>
{
    // rs-key-change-consume-defensive-v1
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(ip, "key_change_consume", 60, 900))
        return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: 429);

    JsonElement root;
    try
    {
        root = await ctx.Request.ReadFromJsonAsync<JsonElement>();
    }
    catch
    {
        return Results.BadRequest(new { ok = false, error = "invalid_body" });
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("token", out var tokenEl) ||
        tokenEl.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { ok = false, error = "invalid_token" });

    var token = tokenEl.GetString()?.Trim() ?? "";

    if (token.Length < 20 ||
        token.Length > 512 ||
        token.Contains('\0') ||
        !System.Text.RegularExpressions.Regex.IsMatch(token, @"^[A-Za-z0-9._~+/=-]{20,512}$"))
        return Results.BadRequest(new { ok = false, error = "invalid_token" });

    var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    var tokenHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

    using var conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await conn.OpenAsync();

    var now = DateTimeOffset.UtcNow.ToString("O");

    var cmd = conn.CreateCommand();
    cmd.CommandText = """
        UPDATE KeyChangeTokens
        SET UsedAt = $now
        WHERE TokenHash = $h
          AND UsedAt IS NULL
          AND datetime(ExpiresAt) > datetime('now')
    """;
    cmd.Parameters.AddWithValue("$now", now);
    cmd.Parameters.AddWithValue("$h", tokenHash);

    var changed = await cmd.ExecuteNonQueryAsync();

    if (changed <= 0)
        return Results.Ok(new { ok = false, error = "invalid_expired_or_used" });

    return Results.Ok(new { ok = true });
});



app.MapDelete("/api/me/devices/{deviceId}", (string deviceId, HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    // rs-me-device-delete-defensive-v1
    var did = DefensiveInput.CleanString(deviceId, 256);
    if (!DefensiveInput.IsSafeToken(did, 256))
        return Results.BadRequest(new { ok = false, error = "invalid_device_id" });

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();

    cmd.CommandText = @"
        DELETE FROM DeviceTokens
        WHERE DeviceToken = $did
          AND UserId = (SELECT Id FROM AuthUsers WHERE Username = $u)
    ";

    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$did", did);

    var deleted = cmd.ExecuteNonQuery();

    var didPreview = did.Length > 12 ? did.Substring(0, 12) + "..." : did;
    AppHelpers.LogActivity(username, "device_delete", "DeviceId=" + didPreview);

    return Results.Ok(new { ok = true, deleted });
});

app.MapDelete("/api/account/delete", (HttpContext ctx) =>
{
    var username = DefensiveInput.CleanString(ctx.User?.Identity?.Name, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !DefensiveInput.IsUsername(username))
        return Results.Unauthorized();

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var tx = db.BeginTransaction();

    string[] sqls =
    {
        "DELETE FROM PersistentSessions WHERE Username=$u",
        "DELETE FROM UserDeviceKeys WHERE Username=$u",
        "DELETE FROM DeviceTokens WHERE UserId=(SELECT Id FROM AuthUsers WHERE Username=$u)",
        "DELETE FROM SupportTickets WHERE Username=$u",
        "DELETE FROM ChatMessages WHERE FromUser=$u OR ToUser=$u",
        "DELETE FROM AuthUsers WHERE Username=$u"
    };

    foreach (var sql in sqls)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$u", username);
        cmd.ExecuteNonQuery();
    }

    tx.Commit();

    ctx.Response.Cookies.Delete("runspace_auth_v3");
    ctx.Response.Cookies.Delete("runspace_auth_v2");
    ctx.Response.Cookies.Delete("runspace_auth");
    ctx.Response.Cookies.Delete("rs-dt");

    return Results.Ok(new { ok = true, deleted = username });
});


app.MapGet("/api/settings/privacy", (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT PrivacyJson FROM AuthUsers WHERE Username=$u";
    cmd.Parameters.AddWithValue("$u", username);

    var json = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrWhiteSpace(json)) json = "{}";

    return Results.Content(json, "application/json");
});

app.MapPost("/api/settings/privacy", async (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body))
        body = "{}";

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET PrivacyJson=$json WHERE Username=$u";
    cmd.Parameters.AddWithValue("$json", body);
    cmd.Parameters.AddWithValue("$u", username);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});


app.MapGet("/api/settings/notifications", (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT NotificationsJson FROM AuthUsers WHERE Username=$u";
    cmd.Parameters.AddWithValue("$u", username);

    var json = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrWhiteSpace(json)) json = "{}";

    return Results.Content(json, "application/json");
});

app.MapPost("/api/settings/notifications", async (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) body = "{}";

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET NotificationsJson=$json WHERE Username=$u";
    cmd.Parameters.AddWithValue("$json", body);
    cmd.Parameters.AddWithValue("$u", username);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});


app.MapGet("/api/settings/appearance", (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT AppearanceJson FROM AuthUsers WHERE Username=$u";
    cmd.Parameters.AddWithValue("$u", username);

    var json = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrWhiteSpace(json)) json = "{}";

    return Results.Content(json, "application/json");
});

app.MapPost("/api/settings/appearance", async (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(body)) body = "{}";

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET AppearanceJson=$json WHERE Username=$u";
    cmd.Parameters.AddWithValue("$json", body);
    cmd.Parameters.AddWithValue("$u", username);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});


app.MapGet("/api/settings/developer", (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT DeveloperJson FROM AuthUsers WHERE Username=$u";
    cmd.Parameters.AddWithValue("$u", username);

    var json = cmd.ExecuteScalar()?.ToString();
    if (string.IsNullOrWhiteSpace(json)) json = "{}";

    return Results.Content(json, "application/json");
});

app.MapPost("/api/settings/developer", async (HttpContext ctx) =>
{
    // rs-dev-settings-defensive-v1
    var username = DefensiveInput.CleanString(ctx.User?.Identity?.Name, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !DefensiveInput.IsUsername(username))
        return Results.Unauthorized();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "developer_settings", 30, 3600))
        return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: 429);

    string body = "{}";

    if (ctx.Request.ContentLength is null || ctx.Request.ContentLength.Value > 0)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { ok = false, error = "invalid_json" });

            body = JsonSerializer.Serialize(doc.RootElement);

            if (body.Length > 8000)
                return Results.BadRequest(new { ok = false, error = "settings_too_large" });
        }
        catch
        {
            return Results.BadRequest(new { ok = false, error = "invalid_json" });
        }
    }

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE AuthUsers SET DeveloperJson=$json WHERE Username=$u";
    cmd.Parameters.AddWithValue("$json", body);
    cmd.Parameters.AddWithValue("$u", username);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});


app.MapGet("/api/developer/tokens", (HttpContext ctx) =>
{
    var username = ctx.User?.Identity?.Name;
    if (string.IsNullOrWhiteSpace(username))
        return Results.Unauthorized();

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Name, TokenPreview, ScopesJson, CreatedAt, LastUsedAt, RevokedAt
        FROM DeveloperTokens
        WHERE Username=$u
        ORDER BY Id DESC
    ";
    cmd.Parameters.AddWithValue("$u", username);

    var list = new List<object>();
    using var r = cmd.ExecuteReader();

    while (r.Read())
    {
        list.Add(new
        {
            id = r.GetInt64(0),
            name = r.IsDBNull(1) ? "API token" : r.GetString(1),
            tokenPreview = r.IsDBNull(2) ? "" : r.GetString(2),
            scopes = r.IsDBNull(3) ? "[]" : r.GetString(3),
            createdAt = r.IsDBNull(4) ? "" : r.GetString(4),
            lastUsedAt = r.IsDBNull(5) ? "" : r.GetString(5),
            revokedAt = r.IsDBNull(6) ? "" : r.GetString(6)
        });
    }

    return Results.Ok(new { ok = true, tokens = list });
});

app.MapPost("/api/developer/tokens", async (HttpContext ctx) =>
{
    // rs-dev-tokens-mini-v1
    var username = DefensiveInput.CleanString(ctx.User?.Identity?.Name, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !DefensiveInput.IsUsername(username))
        return Results.Unauthorized();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "developer_tokens", 20, 3600))
        return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: 429);

    // rs-dev-tokens-json-defensive-v1
    string name = "API token";

    if (ctx.Request.ContentLength.GetValueOrDefault() > 0)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { ok = false, error = "invalid_json" });

            if (doc.RootElement.TryGetProperty("name", out var n))
            {
                if (n.ValueKind != JsonValueKind.String && n.ValueKind != JsonValueKind.Null)
                    return Results.BadRequest(new { ok = false, error = "invalid_name" });

                var raw = DefensiveInput.CleanString(n.GetString(), 80);
                if (!string.IsNullOrWhiteSpace(raw))
                    name = raw;
            }
        }
        catch
        {
            return Results.BadRequest(new { ok = false, error = "invalid_json" });
        }
    }

    var bytes = RandomNumberGenerator.GetBytes(32);
    var token = "rs_" + Convert.ToBase64String(bytes)
        .Replace("+", "")
        .Replace("/", "")
        .Replace("=", "");

    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    var preview = token.Substring(0, Math.Min(10, token.Length)) + "…";

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO DeveloperTokens (Username, Name, TokenHash, TokenPreview, ScopesJson)
        VALUES ($u, $name, $hash, $preview, '[]')
    ";
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$hash", hash);
    cmd.Parameters.AddWithValue("$preview", preview);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true, token, tokenPreview = preview });
});

app.MapDelete("/api/developer/tokens/{id:long}", (long id, HttpContext ctx) =>
{
    // rs-dev-token-delete-mini-v1
    var username = DefensiveInput.CleanString(ctx.User?.Identity?.Name, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !DefensiveInput.IsUsername(username))
        return Results.Unauthorized();

    if (id <= 0)
        return Results.BadRequest(new { ok = false, error = "invalid_token_id" });

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "developer_tokens", 20, 3600))
        return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: 429);

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        UPDATE DeveloperTokens
        SET RevokedAt=datetime('now')
        WHERE Id=$id AND Username=$u
    ";
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$u", username);

    var updated = cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true, revoked = updated });
});


app.MapGet("/api/developer/webhooks", (HttpContext ctx) =>
{
    // rs-dev-webhooks-get-defensive-v1
    var username = DefensiveInput.CleanString(ctx.User?.Identity?.Name, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !DefensiveInput.IsUsername(username))
        return Results.Unauthorized();

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        SELECT Id, Url, EventsJson, IsActive, CreatedAt, LastDeliveryAt
        FROM DeveloperWebhooks
        WHERE Username=$u
        ORDER BY Id DESC
    ";
    cmd.Parameters.AddWithValue("$u", username);

    var list = new List<object>();
    using var r = cmd.ExecuteReader();

    while (r.Read())
    {
        list.Add(new
        {
            id = r.GetInt64(0),
            url = r.IsDBNull(1) ? "" : r.GetString(1),
            events = r.IsDBNull(2) ? "[]" : r.GetString(2),
            isActive = !r.IsDBNull(3) && r.GetInt32(3) == 1,
            createdAt = r.IsDBNull(4) ? "" : r.GetString(4),
            lastDeliveryAt = r.IsDBNull(5) ? "" : r.GetString(5)
        });
    }

    return Results.Ok(new { ok = true, webhooks = list });
});

app.MapPost("/api/developer/webhooks", async (HttpContext ctx) =>
{
    // rs-dev-webhooks-post-defensive-v1
    var username = DefensiveInput.CleanString(ctx.User?.Identity?.Name, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !DefensiveInput.IsUsername(username))
        return Results.Unauthorized();

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "developer_webhooks", 20, 3600))
        return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: 429);

    string url = "";
    string eventsJson = "[\"message.created\"]";

    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return Results.BadRequest(new { ok = false, error = "invalid_json" });

        if (doc.RootElement.TryGetProperty("url", out var u))
            url = DefensiveInput.CleanString(u.GetString(), 500);

        if (doc.RootElement.TryGetProperty("events", out var e) && e.ValueKind == JsonValueKind.Array)
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "message.created",
                "message.updated",
                "message.deleted"
            };

            var events = new List<string>();
            foreach (var item in e.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var ev = DefensiveInput.CleanString(item.GetString(), 80);
                if (allowed.Contains(ev) && !events.Contains(ev))
                    events.Add(ev);
                if (events.Count >= 10) break;
            }

            if (events.Count > 0)
                eventsJson = JsonSerializer.Serialize(events);
        }
    }
    catch
    {
        return Results.BadRequest(new { ok = false, error = "invalid_json" });
    }

    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "https" && uri.Scheme != "http"))
        return Results.BadRequest(new { ok = false, error = "invalid_url" });

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO DeveloperWebhooks (Username, Url, EventsJson)
        VALUES ($u, $url, $events)
    ";
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$url", url);
    cmd.Parameters.AddWithValue("$events", eventsJson);
    cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true });
});

app.MapDelete("/api/developer/webhooks/{id:long}", (long id, HttpContext ctx) =>
{
    // rs-dev-webhooks-delete-defensive-v1
    var username = DefensiveInput.CleanString(ctx.User?.Identity?.Name, 32).ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(username) || !DefensiveInput.IsUsername(username))
        return Results.Unauthorized();

    if (id <= 0)
        return Results.BadRequest(new { ok = false, error = "invalid_webhook_id" });

    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(username, "developer_webhooks", 20, 3600))
        return Results.Json(new { ok = false, error = "rate_limited" }, statusCode: 429);

    using var db = new SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    db.Open();

    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM DeveloperWebhooks WHERE Id=$id AND Username=$u";
    cmd.Parameters.AddWithValue("$id", id);
    cmd.Parameters.AddWithValue("$u", username);

    var deleted = cmd.ExecuteNonQuery();

    return Results.Ok(new { ok = true, deleted });
});


// ═══════════════════════════════════════════════════════════════════════════
// Music API
// ═══════════════════════════════════════════════════════════════════════════

app.MapGet("/api/music/playlists", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"SELECT p.Id, p.Name, p.CreatedAt, COUNT(t.Id) AS TrackCount
        FROM MusicPlaylists p LEFT JOIN MusicTracks t ON t.PlaylistId = p.Id
        WHERE p.Username = $u GROUP BY p.Id ORDER BY p.CreatedAt ASC";
    cmd.Parameters.AddWithValue("$u", username);
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { id = r.GetInt64(0), name = r.GetString(1), createdAt = r.GetString(2), trackCount = r.GetInt64(3) });
    return Results.Ok(list);
});

app.MapPost("/api/music/playlists", async (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); } catch { return Results.BadRequest(); }
    var name = body.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
    if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return Results.BadRequest(new { error = "Name required" });
    using var db = DbHelpers.OpenDb();
    using var cc = db.CreateCommand();
    cc.CommandText = "SELECT COUNT(*) FROM MusicPlaylists WHERE Username = $u";
    cc.Parameters.AddWithValue("$u", username);
    if ((long)(cc.ExecuteScalar() ?? 0L) >= 50) return Results.BadRequest(new { error = "Limit reached" });
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO MusicPlaylists (Username, Name, CreatedAt) VALUES ($u, $n, $at); SELECT last_insert_rowid();";
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$n", name);
    cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
    var id = (long)(cmd.ExecuteScalar() ?? 0L);
    return Results.Ok(new { id, name, trackCount = 0 });
});

app.MapPut("/api/music/playlists/{id}", async (long id, HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); } catch { return Results.BadRequest(); }
    var name = body.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
    if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return Results.BadRequest(new { error = "Name required" });
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "UPDATE MusicPlaylists SET Name = $n WHERE Id = $id AND Username = $u";
    cmd.Parameters.AddWithValue("$n", name); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$u", username);
    return cmd.ExecuteNonQuery() > 0 ? Results.Ok(new { id, name }) : Results.NotFound();
});

app.MapDelete("/api/music/playlists/{id}", (long id, HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM MusicPlaylists WHERE Id = $id AND Username = $u";
    cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$u", username);
    return cmd.ExecuteNonQuery() > 0 ? Results.Ok() : Results.NotFound();
});

app.MapGet("/api/music/playlists/{id}/tracks", (long id, HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    using var db = DbHelpers.OpenDb();
    using var own = db.CreateCommand();
    own.CommandText = "SELECT COUNT(*) FROM MusicPlaylists WHERE Id = $id AND Username = $u";
    own.Parameters.AddWithValue("$id", id); own.Parameters.AddWithValue("$u", username);
    if ((long)(own.ExecuteScalar() ?? 0L) == 0) return Results.NotFound();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Id, Type, Title, Artist, Length, VideoId, EmbedUrl, Thumb, AddedAt FROM MusicTracks WHERE PlaylistId = $pid ORDER BY AddedAt ASC";
    cmd.Parameters.AddWithValue("$pid", id);
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { id = r.GetInt64(0), type = r.GetString(1), title = r.IsDBNull(2) ? null : r.GetString(2), artist = r.IsDBNull(3) ? null : r.GetString(3), length = r.IsDBNull(4) ? null : r.GetString(4), videoId = r.IsDBNull(5) ? null : r.GetString(5), embedUrl = r.IsDBNull(6) ? null : r.GetString(6), thumb = r.IsDBNull(7) ? null : r.GetString(7), addedAt = r.GetString(8) });
    return Results.Ok(list);
});

app.MapPost("/api/music/playlists/{id}/tracks", async (long id, HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); } catch { return Results.BadRequest(); }
    var type = body.TryGetProperty("type", out var t) ? t.GetString()?.Trim() : null;
    var title = body.TryGetProperty("title", out var ti) ? ti.GetString()?.Trim() : null;
    var artist = body.TryGetProperty("artist", out var ar) ? ar.GetString()?.Trim() : null;
    var length = body.TryGetProperty("length", out var le) ? le.GetString()?.Trim() : null;
    var videoId = body.TryGetProperty("videoId", out var vi) ? vi.GetString()?.Trim() : null;
    var embedUrl = body.TryGetProperty("embedUrl", out var eu) ? eu.GetString()?.Trim() : null;
    var thumb = body.TryGetProperty("thumb", out var th) ? th.GetString()?.Trim() : null;
    if (string.IsNullOrWhiteSpace(type) || type is not ("youtube" or "spotify" or "local")) return Results.BadRequest(new { error = "Invalid type" });
    using var db = DbHelpers.OpenDb();
    using var own = db.CreateCommand();
    own.CommandText = "SELECT COUNT(*) FROM MusicPlaylists WHERE Id = $id AND Username = $u";
    own.Parameters.AddWithValue("$id", id); own.Parameters.AddWithValue("$u", username);
    if ((long)(own.ExecuteScalar() ?? 0L) == 0) return Results.NotFound();
    using var cc = db.CreateCommand();
    cc.CommandText = "SELECT COUNT(*) FROM MusicTracks WHERE PlaylistId = $pid";
    cc.Parameters.AddWithValue("$pid", id);
    if ((long)(cc.ExecuteScalar() ?? 0L) >= 500) return Results.BadRequest(new { error = "Limit reached" });
    using var cmd = db.CreateCommand();
    cmd.CommandText = "INSERT INTO MusicTracks (PlaylistId, Type, Title, Artist, Length, VideoId, EmbedUrl, Thumb, AddedAt) VALUES ($pid, $type, $title, $artist, $length, $videoId, $embedUrl, $thumb, $at); SELECT last_insert_rowid();";
    cmd.Parameters.AddWithValue("$pid", id);
    cmd.Parameters.AddWithValue("$type", type);
    cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$artist", (object?)artist ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$length", (object?)length ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$videoId", (object?)videoId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$embedUrl", (object?)embedUrl ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$thumb", (object?)thumb ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
    var newId = (long)(cmd.ExecuteScalar() ?? 0L);
    return Results.Ok(new { id = newId, type, title, artist, length, videoId, embedUrl, thumb });
});

app.MapDelete("/api/music/tracks/{id}", (long id, HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "DELETE FROM MusicTracks WHERE Id = $id AND PlaylistId IN (SELECT Id FROM MusicPlaylists WHERE Username = $u)";
    cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$u", username);
    return cmd.ExecuteNonQuery() > 0 ? Results.Ok() : Results.NotFound();
});

app.MapGet("/api/music/recent", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT TrackId, Title, Type, Thumb, VideoId, PlayedAt FROM MusicRecent WHERE Username = $u ORDER BY PlayedAt DESC LIMIT 20";
    cmd.Parameters.AddWithValue("$u", username);
    var list = new List<object>();
    using var r = cmd.ExecuteReader();
    while (r.Read()) list.Add(new { id = r.GetInt64(0), title = r.IsDBNull(1) ? null : r.GetString(1), type = r.GetString(2), thumb = r.IsDBNull(3) ? null : r.GetString(3), videoId = r.IsDBNull(4) ? null : r.GetString(4), playedAt = r.GetString(5) });
    return Results.Ok(list);
});

app.MapPost("/api/music/recent", async (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var username = (ctx.User.Identity.Name ?? "").Trim().ToLowerInvariant();
    JsonElement body;
    try { body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body); } catch { return Results.BadRequest(); }
    var trackId = body.TryGetProperty("id", out var i) ? (long?)i.GetInt64() : null;
    var title = body.TryGetProperty("title", out var ti) ? ti.GetString()?.Trim() : null;
    var type = body.TryGetProperty("type", out var ty) ? ty.GetString()?.Trim() : null;
    var thumb = body.TryGetProperty("thumb", out var th) ? th.GetString()?.Trim() : null;
    var videoId = body.TryGetProperty("videoId", out var vi) ? vi.GetString()?.Trim() : null;
    if (trackId == null || string.IsNullOrWhiteSpace(type)) return Results.BadRequest();
    using var db = DbHelpers.OpenDb();
    using var cmd = db.CreateCommand();
    cmd.CommandText = @"INSERT INTO MusicRecent (Username, TrackId, Title, Type, Thumb, VideoId, PlayedAt)
        VALUES ($u, $tid, $title, $type, $thumb, $vid, $at)
        ON CONFLICT(Username, TrackId) DO UPDATE SET PlayedAt = excluded.PlayedAt;
        DELETE FROM MusicRecent WHERE Username = $u AND PlayedAt NOT IN
        (SELECT PlayedAt FROM MusicRecent WHERE Username = $u ORDER BY PlayedAt DESC LIMIT 20);";
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$tid", trackId.Value);
    cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$type", type);
    cmd.Parameters.AddWithValue("$thumb", (object?)thumb ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$vid", (object?)videoId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
    return Results.Ok();
});


// RunSpace Premium billing
BillingEndpoints.EnsureBillingTables();
BillingEndpoints.Map(app);


// RunSpace Premium feature access
PremiumFeatureEndpoints.Map(app);




app.MapPost("/api/contact", async (HttpContext http) =>
{
    // rs-contact-defensive-v1
    // rs-contact-log-sanitize-v1
    static string CleanLogValue(string? value, int max)
    {
        // rs-contact-log-sanitize-fix-v1
        value = DefensiveInput.CleanString(value ?? "", max);
        value = new string(value.Where(ch => !char.IsControl(ch)).ToArray());
        value = value.Trim();
        return value.Length > max ? value[..max] : value;
    }

    var rawIp =
        http.Request.Headers["CF-Connecting-IP"].FirstOrDefault()
        ?? "";

    rawIp = CleanLogValue(rawIp, 64);

    var remoteIp = http.Connection.RemoteIpAddress?.ToString() ?? "";
    var ip = System.Net.IPAddress.TryParse(rawIp, out _)
        ? rawIp
        : CleanLogValue(remoteIp, 64);

    if (!System.Net.IPAddress.TryParse(ip, out _))
        ip = "unknown";

    var limiter = http.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(ip, "contact_form", 5, 3600))
        return Results.Json(new { ok = false, error = "Too many requests." }, statusCode: 429);

    System.Text.Json.JsonElement body;

    try
    {
        body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(http.Request.Body);
    }
    catch
    {
        return Results.BadRequest(new { ok = false, error = "Invalid JSON request." });
    }

    if (body.ValueKind != System.Text.Json.JsonValueKind.Object)
        return Results.BadRequest(new { ok = false, error = "Invalid JSON request." });

    static string Clean(System.Text.Json.JsonElement data, string key, int max)
    {
        if (!data.TryGetProperty(key, out var value))
            return "";

        var raw = value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ToString();

        return DefensiveInput.CleanString(raw, max);
    }

    var name = Clean(body, "name", 80);
    var contact = Clean(body, "contact", 120);
    var topic = Clean(body, "topic", 60);
    var message = Clean(body, "message", 3000);

    if (string.IsNullOrWhiteSpace(name) ||
        string.IsNullOrWhiteSpace(contact) ||
        string.IsNullOrWhiteSpace(topic) ||
        string.IsNullOrWhiteSpace(message))
    {
        return Results.BadRequest(new { ok = false, error = "Missing required fields." });
    }

    Directory.CreateDirectory("/var/lib/runspace");
    var path = "/var/lib/runspace/contact_messages.jsonl";

    var row = new
    {
        created_at = DateTimeOffset.UtcNow.ToString("O"),
        ip,
        user_agent = CleanLogValue(http.Request.Headers.UserAgent.ToString(), 512),
        name,
        contact,
        topic,
        message
    };

    await System.IO.File.AppendAllTextAsync(
        path,
        System.Text.Json.JsonSerializer.Serialize(row) + Environment.NewLine
    );

    return Results.Ok(new { ok = true });
});



// ======================================================
// RunSpace Devices API
// Used by RunSpace QT Linux Settings → Devices
// ======================================================

static async Task<long?> RsGetCurrentUserIdAsync(HttpContext ctx)
{
    if (ctx.User?.Identity?.IsAuthenticated != true)
        return null;

    var username =
        ctx.User.Identity?.Name ??
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ??
        ctx.User.FindFirst("username")?.Value ??
        "";

    if (string.IsNullOrWhiteSpace(username))
        return null;

    await using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    await using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Id FROM AuthUsers WHERE LOWER(Username)=LOWER($u) LIMIT 1;";
    cmd.Parameters.AddWithValue("$u", username);

    var result = await cmd.ExecuteScalarAsync();
    if (result == null || result == DBNull.Value)
        return null;

    if (long.TryParse(result.ToString(), out var userId))
        return userId;

    return null;
}

static string RsIpPrefix(HttpContext ctx)
{
    // rs-ip-prefix-defensive-v1
    var rawCfIp = DefensiveInput.CleanString(ctx.Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? "", 64);
    var remoteIp = DefensiveInput.CleanString(ctx.Connection.RemoteIpAddress?.ToString() ?? "", 64);

    var ip = System.Net.IPAddress.TryParse(rawCfIp, out _)
        ? rawCfIp
        : remoteIp;

    if (!System.Net.IPAddress.TryParse(ip, out _))
        return "";

    if (ip.Contains('.'))
    {
        var parts = ip.Split('.');
        if (parts.Length >= 3)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.x";
    }

    if (ip.Contains(':'))
    {
        var parts = ip.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4)
            return string.Join(":", parts.Take(4)) + "::";
    }

    return "";
}

static string RsGuessLocation(HttpContext ctx)
{
    var country = ctx.Request.Headers["CF-IPCountry"].FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(country) && country != "XX")
        return country;

    return "Unknown";
}

app.MapGet("/api/devices/trusted", async (HttpContext ctx) =>
{
    var userId = await RsGetCurrentUserIdAsync(ctx);

    if (userId == null)
        return Results.Json(new { success = false, error = "Not authenticated" }, statusCode: 401);

    await using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    await using (var create = db.CreateCommand())
    {
        create.CommandText = """
        CREATE TABLE IF NOT EXISTS TrustedDevices (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            DeviceId TEXT NOT NULL,
            DeviceName TEXT NOT NULL,
            Platform TEXT NOT NULL DEFAULT '',
            Location TEXT NOT NULL DEFAULT '',
            IpPrefix TEXT NOT NULL DEFAULT '',
            UserAgent TEXT NOT NULL DEFAULT '',
            FirstSeenAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            LastSeenAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            IsTrusted INTEGER NOT NULL DEFAULT 1,
            IsCurrent INTEGER NOT NULL DEFAULT 0,
            UNIQUE(UserId, DeviceId)
        );

        CREATE INDEX IF NOT EXISTS IX_TrustedDevices_UserId
        ON TrustedDevices(UserId);
        """;

        await create.ExecuteNonQueryAsync();
    }

    // rs-devices-trusted-defensive-v1
    var rawDeviceId = ctx.Request.Headers["X-RunSpace-Device-Id"].ToString();

    if (string.IsNullOrWhiteSpace(rawDeviceId))
        rawDeviceId = ctx.Request.Cookies["rs-dt"] ?? "";

    var deviceId = DefensiveInput.CleanString(rawDeviceId, 100);
    if (!DefensiveInput.IsSafeDeviceId(deviceId))
        deviceId = "unknown-device";

    var deviceName = DefensiveInput.CleanString(ctx.Request.Headers["X-RunSpace-Device-Name"].ToString(), 80);
    if (string.IsNullOrWhiteSpace(deviceName))
        deviceName = "RunSpace QT Linux";

    var userAgent = DefensiveInput.CleanString(ctx.Request.Headers.UserAgent.ToString(), 512);
    var ipPrefix = RsIpPrefix(ctx);
    var location = RsGuessLocation(ctx);
    var now = DateTime.UtcNow.ToString("O");

    await using (var clearCurrent = db.CreateCommand())
    {
        clearCurrent.CommandText = """
        UPDATE TrustedDevices
        SET IsCurrent = 0
        WHERE UserId = $uid;
        """;
        clearCurrent.Parameters.AddWithValue("$uid", userId.Value);
        await clearCurrent.ExecuteNonQueryAsync();
    }

    await using (var upsert = db.CreateCommand())
    {
        upsert.CommandText = """
        INSERT INTO TrustedDevices
            (UserId, DeviceId, DeviceName, Platform, Location, IpPrefix, UserAgent, LastSeenAt, IsTrusted, IsCurrent)
        VALUES
            ($uid, $deviceId, $deviceName, $platform, $location, $ipPrefix, $userAgent, $lastSeen, 1, 1)
        ON CONFLICT(UserId, DeviceId) DO UPDATE SET
            DeviceName = excluded.DeviceName,
            Platform = excluded.Platform,
            Location = excluded.Location,
            IpPrefix = excluded.IpPrefix,
            UserAgent = excluded.UserAgent,
            LastSeenAt = excluded.LastSeenAt,
            IsTrusted = 1,
            IsCurrent = 1;
        """;

        upsert.Parameters.AddWithValue("$uid", userId.Value);
        upsert.Parameters.AddWithValue("$deviceId", deviceId);
        upsert.Parameters.AddWithValue("$deviceName", deviceName);
        upsert.Parameters.AddWithValue("$platform", "Linux");
        upsert.Parameters.AddWithValue("$location", location);
        upsert.Parameters.AddWithValue("$ipPrefix", ipPrefix);
        upsert.Parameters.AddWithValue("$userAgent", userAgent);
        upsert.Parameters.AddWithValue("$lastSeen", now);

        await upsert.ExecuteNonQueryAsync();
    }

    var devices = new List<object>();

    await using (var list = db.CreateCommand())
    {
        list.CommandText = """
        SELECT DeviceId, DeviceName, Platform, Location, IpPrefix, LastSeenAt, IsTrusted, IsCurrent
        FROM TrustedDevices
        WHERE UserId = $uid AND IsTrusted = 1
        ORDER BY IsCurrent DESC, LastSeenAt DESC;
        """;
        list.Parameters.AddWithValue("$uid", userId.Value);

        await using var reader = await list.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            devices.Add(new
            {
                deviceId = reader.GetString(0),
                deviceName = reader.GetString(1),
                platform = reader.GetString(2),
                location = reader.GetString(3),
                ipPrefix = reader.GetString(4),
                lastSeenUtc = reader.GetString(5),
                isTrusted = reader.GetInt32(6) == 1,
                isCurrent = reader.GetInt32(7) == 1
            });
        }
    }

    return Results.Json(new
    {
        success = true,
        currentDevice = deviceId,
        keyValidUntil = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd"),
        devices
    });
});


app.MapPost("/api/devices/revoke", async (HttpContext ctx) =>
{
    var userId = await RsGetCurrentUserIdAsync(ctx);

    if (userId == null)
        return Results.Json(new { success = false, error = "Not authenticated" }, statusCode: 401);

    // rs-devices-revoke-defensive-v1
    var limiter = ctx.RequestServices.GetRequiredService<RateLimiter>();
    if (!limiter.IsAllowed(userId.Value.ToString(), "devices_revoke", 30, 900))
        return Results.Json(new { success = false, error = "Rate limit." }, statusCode: 429);

    JsonElement root;
    try
    {
        root = await ctx.Request.ReadFromJsonAsync<JsonElement>();
    }
    catch
    {
        return Results.BadRequest(new { success = false, error = "Invalid request" });
    }

    if (root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty("deviceId", out var deviceIdElement) ||
        deviceIdElement.ValueKind != JsonValueKind.String)
        return Results.BadRequest(new { success = false, error = "Missing deviceId" });

    var deviceId = DefensiveInput.CleanString(deviceIdElement.GetString(), 100);

    if (!DefensiveInput.IsSafeDeviceId(deviceId))
        return Results.BadRequest(new { success = false, error = "Invalid deviceId" });

    var currentDevice = DefensiveInput.CleanString(ctx.Request.Headers["X-RunSpace-Device-Id"].ToString(), 100);

    if (DefensiveInput.IsSafeDeviceId(currentDevice) && currentDevice == deviceId)
        return Results.BadRequest(new { success = false, error = "Cannot revoke current device" });

    await using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    await using var cmd = db.CreateCommand();
    cmd.CommandText = """
    UPDATE TrustedDevices
    SET IsTrusted = 0
    WHERE UserId = $uid AND DeviceId = $deviceId;
    """;
    cmd.Parameters.AddWithValue("$uid", userId.Value);
    cmd.Parameters.AddWithValue("$deviceId", deviceId);

    var rows = await cmd.ExecuteNonQueryAsync();

    return Results.Json(new
    {
        success = true,
        revoked = rows > 0,
        deviceId
    });
});

app.MapPost("/api/devices/rotate-keys", async (HttpContext ctx) =>
{
    var userId = await RsGetCurrentUserIdAsync(ctx);

    if (userId == null)
        return Results.Json(new { success = false, error = "Not authenticated" }, statusCode: 401);

    return Results.Json(new
    {
        success = true,
        message = "Key rotation requested",
        keyValidUntil = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd")
    });
});




// ======================================================
// RunSpace Account Login History API
// Used by RunSpace QT Linux Settings → Account
// ======================================================

app.MapGet("/api/account/login-history", async (HttpContext ctx) =>
{
    var userId = await RsGetCurrentUserIdAsync(ctx);

    if (userId == null)
        return Results.Json(new { success = false, error = "Not authenticated" }, statusCode: 401);

    await using var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=/var/lib/runspace/runspace.db");
    await db.OpenAsync();

    async Task<bool> TableExists(string tableName)
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name;";
        cmd.Parameters.AddWithValue("$name", tableName);
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(result ?? 0) > 0;
    }

    var entries = new List<object>();

    if (await TableExists("DeviceTokens"))
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
        SELECT
            DeviceToken,
            COALESCE(DeviceName, ''),
            COALESCE(UserAgent, ''),
            COALESCE(IpPrefix, ''),
            COALESCE(FirstSeenAt, ''),
            COALESCE(LastSeenAt, ''),
            COALESCE(IsTrusted, 0),
            COALESCE(SessionCount, 0)
        FROM DeviceTokens
        WHERE UserId = $uid
        ORDER BY LastSeenAt DESC
        LIMIT 12;
        """;
        cmd.Parameters.AddWithValue("$uid", userId.Value);

        try
        {
            await using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())
            {
                var token = r.GetString(0);
                var deviceName = r.GetString(1);
                var userAgent = r.GetString(2);
                var ipPrefix = r.GetString(3);
                var firstSeen = r.GetString(4);
                var lastSeen = r.GetString(5);
                var trusted = r.GetInt32(6) == 1;
                var sessionCount = r.GetInt32(7);

                entries.Add(new
                {
                    timestamp = string.IsNullOrWhiteSpace(lastSeen) ? firstSeen : lastSeen,
                    deviceName = string.IsNullOrWhiteSpace(deviceName) ? "Unknown device" : deviceName,
                    userAgent,
                    ipPrefix,
                    location = "Unknown",
                    status = trusted ? "Trusted" : "Seen",
                    riskScore = 0,
                    sessionCount,
                    deviceId = token
                });
            }
        }
        catch
        {
            // Older DB schema may differ. Fallback below handles it.
        }
    }

    if (entries.Count == 0 && await TableExists("TrustedDevices"))
    {
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
        SELECT
            DeviceId,
            COALESCE(DeviceName, ''),
            COALESCE(Platform, ''),
            COALESCE(Location, ''),
            COALESCE(IpPrefix, ''),
            COALESCE(FirstSeenAt, ''),
            COALESCE(LastSeenAt, ''),
            COALESCE(IsCurrent, 0)
        FROM TrustedDevices
        WHERE UserId = $uid AND IsTrusted = 1
        ORDER BY LastSeenAt DESC
        LIMIT 12;
        """;
        cmd.Parameters.AddWithValue("$uid", userId.Value);

        try
        {
            await using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())
            {
                var deviceId = r.GetString(0);
                var deviceName = r.GetString(1);
                var platform = r.GetString(2);
                var location = r.GetString(3);
                var ipPrefix = r.GetString(4);
                var firstSeen = r.GetString(5);
                var lastSeen = r.GetString(6);
                var isCurrent = r.GetInt32(7) == 1;

                entries.Add(new
                {
                    timestamp = string.IsNullOrWhiteSpace(lastSeen) ? firstSeen : lastSeen,
                    deviceName = string.IsNullOrWhiteSpace(deviceName) ? "RunSpace device" : deviceName,
                    platform,
                    location,
                    ipPrefix,
                    status = isCurrent ? "Active" : "Trusted",
                    riskScore = 0,
                    deviceId
                });
            }
        }
        catch
        {
            // Ignore and use fallback below.
        }
    }

    if (entries.Count == 0)
    {
        // rs-account-devices-fallback-defensive-v1
        var rawDeviceId = ctx.Request.Headers["X-RunSpace-Device-Id"].ToString();

        if (string.IsNullOrWhiteSpace(rawDeviceId))
            rawDeviceId = ctx.Request.Cookies["rs-dt"] ?? "";

        var deviceId = DefensiveInput.CleanString(rawDeviceId, 100);
        if (!DefensiveInput.IsSafeDeviceId(deviceId))
            deviceId = "current-session";

        var deviceName = DefensiveInput.CleanString(ctx.Request.Headers["X-RunSpace-Device-Name"].ToString(), 80);

        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = "RunSpace QT Linux";

        entries.Add(new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            deviceName,
            location = "Current session",
            ipPrefix = "",
            status = "Active",
            riskScore = 0,
            deviceId
        });
    }

    return Results.Json(new
    {
        success = true,
        loginHistory = entries
    });
});


app.Run();

// ═══════════════════════════════════════════════
// CONFIGURATION
// ═══════════════════════════════════════════════

public record SuspendReq(string? Reason);
public record IpBlacklistReq(string? Ip);
public record SupportTicketReq(string? Username, string? Category, string? Subject, string? Description);
public record SupportReplyDto(string? Message);
public record SupportStatusDto(string? Status);
public record SupportAssignDto(string? AssignedTo);
public record SupportPriorityDto(string? Priority);
public record DiscordSupportDto(string? Username, string? Category, string? Description);

public record SetTrustReq(string? Level, int? Score);
public static class AppConfig
{
    public const string AdminUsername = "mx403";
    public const string DataDir = "/var/lib/runspace/data";
    public const string DbPath = "/var/lib/runspace/runspace.db";
    public const string ConnectionString = "Data Source=/var/lib/runspace/runspace.db";
    public const string UploadsDir = "/var/lib/runspace/data/uploads";
    public const string AvatarUploadDir = "/var/lib/runspace/data/uploads/avatars";
    public const string ChatUploadDir = "/var/lib/runspace/data/uploads/chat";
    public const string MarketDir = "/var/lib/runspace/data/market";
    public const int MaxCipherPayloadLength = 50000;
    public static string PasswordPepper => Environment.GetEnvironmentVariable("RUNSPACE_PEPPER") ?? "RS_DEFAULT_PEPPER_CHANGE_ME_IN_PROD";
    public static string[] AllowedOrigins => (Environment.GetEnvironmentVariable("RUNSPACE_ORIGINS") ?? "https://runspace.cloud").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public static bool IsDevMode => string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
}


// ═══════════════════════════════════════════════
// ═══════════════════════════════════════════════

// CHAT HUB
public class NameUserIdProvider : IUserIdProvider { public string? GetUserId(HubConnectionContext c) => c.User?.Identity?.Name?.Trim().ToLowerInvariant(); }

public class ChatHub : Hub
{
    // DM
    public async Task JoinPrivate(string otherUser)
    {
        var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant() ?? "";
        var target = (otherUser ?? "").Trim().ToLowerInvariant();

        var reason =
            string.IsNullOrWhiteSpace(me) ? "me_null" :
            !AppHelpers.UserExists(me) ? "me_notfound" :
            string.IsNullOrWhiteSpace(target) ? "target_empty" :
            target == me ? "self" :
            !AppHelpers.UserExists(target) ? "target_notfound" :
            "";

        if (!string.IsNullOrWhiteSpace(reason))
            throw new HubException("Ogiltigt:" + reason);

        await Groups.AddToGroupAsync(Context.ConnectionId, ChatHelpers.BuildGroupName(me, target));
    }
    public async Task LeavePrivate(string otherUser) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); var target = (otherUser ?? "").Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me) && !string.IsNullOrWhiteSpace(target)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatHelpers.BuildGroupName(me, target)); }
    public async Task SendMessage(ChatMessageReq req)
    {
        var from = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(from) || !AppHelpers.UserExists(from)) throw new HubException("Inte inloggad.");
        var toCheck = (req.To ?? "").Trim().ToLowerInvariant();
        // --- TRUST + FIREWALL + RATE LIMIT ENFORCEMENT ---
        var _ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "";
        // rs-signalr-sendmessage-device-token-defensive-v1
        var _deviceToken = DefensiveInput.CleanString(Context.GetHttpContext()?.Request.Headers["X-Device-Token"].FirstOrDefault() ?? "", 256);
        if (!DefensiveInput.IsSafeToken(_deviceToken))
            _deviceToken = "";
        var _msgText = req.Text ?? "";
        // 1. Load trust level from DB
        string _trustLevel = "medium";
        using (var _db = DbHelpers.OpenDb())
        {
            using var _tc = _db.CreateCommand();
            _tc.CommandText = "SELECT TrustLevel, IsSuspended FROM AuthUsers WHERE Username=$u";
            _tc.Parameters.AddWithValue("$u", from);
            using var _tr = _tc.ExecuteReader();
            if (_tr.Read())
            {
                var _susp = !_tr.IsDBNull(1) && _tr.GetInt32(1) == 1;
                _trustLevel = _susp ? "blocked" : (_tr.IsDBNull(0) ? "medium" : _tr.GetString(0));
            }
        }
        // 2. Blocked accounts cannot send
        if (_trustLevel == "blocked")
        {
            using var _db2 = DbHelpers.OpenDb();
            using var _evc = _db2.CreateCommand();
            _evc.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail,  DeviceToken, CreatedAt)
                SELECT Id, 'message_blocked_trust', 'alert', 'Blocked account attempted SendMessage',  $tok, datetime('now') FROM AuthUsers WHERE Username=$u";
            _evc.Parameters.AddWithValue("$ip", _ip); _evc.Parameters.AddWithValue("$tok", _deviceToken); _evc.Parameters.AddWithValue("$u", from);
            _evc.ExecuteNonQuery();
            throw new HubException("Din session är blockerad.");
        }
        // 2b. Enforce active cooldown before counting a new message.
        using (var _coolDb = DbHelpers.OpenDb())
        {
            using var _cool = _coolDb.CreateCommand();
            _cool.CommandText = @"
                SELECT ce.CooldownUntil
                FROM CooldownEscalations ce
                JOIN AuthUsers au ON au.Id = ce.UserId
                WHERE au.Username=$u
                  AND ce.ActionType='message'
                  AND ce.CooldownUntil > datetime('now')
                LIMIT 1";
            _cool.Parameters.AddWithValue("$u", from);
            var _coolUntil = _cool.ExecuteScalar();
            if (_coolUntil != null)
            {
                throw new HubException("För många meddelanden. Vänta lite.");
            }
        }

        // 3. Rate limit (per 60s window)
        int _rateLimit = _trustLevel == "high" ? 60 : _trustLevel == "medium" ? 30 : 15;
        var _window = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60) * 60;
        using (var _db3 = DbHelpers.OpenDb())
        {
            using var _rlc = _db3.CreateCommand();
            _rlc.CommandText = @"INSERT INTO RateLimitEvents (UserId, ActionType, WindowStart, Count)
                SELECT Id, 'message', $win, 1 FROM AuthUsers WHERE Username=$u
                ON CONFLICT(UserId, ActionType, WindowStart) DO UPDATE SET Count=Count+1
                RETURNING Count";
            _rlc.Parameters.AddWithValue("$win", _window.ToString()); _rlc.Parameters.AddWithValue("$u", from);
            var _cnt = Convert.ToInt32(_rlc.ExecuteScalar());
            if (_cnt > _rateLimit)
            {
                using var _rle = _db3.CreateCommand();
                _rle.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail,  CreatedAt)
                    SELECT Id, 'message_rate_limited', 'warn', $d,  datetime('now') FROM AuthUsers WHERE Username=$u";
                _rle.Parameters.AddWithValue("$d", $"Count={_cnt} Limit={_rateLimit} Trust={_trustLevel}");
                _rle.Parameters.AddWithValue("$ip", _ip); _rle.Parameters.AddWithValue("$u", from);
                _rle.ExecuteNonQuery();
                // Escalating cooldown + trust penalty
                using var _esc = _db3.CreateCommand();
                _esc.CommandText = @"INSERT INTO CooldownEscalations (UserId, ActionType, ViolationCount, CooldownUntil, CreatedAt)
                    SELECT Id, 'message', 1, datetime('now', '+2 minutes'), datetime('now') FROM AuthUsers WHERE Username=$u
                    ON CONFLICT(UserId, ActionType) DO UPDATE SET
                        ViolationCount = ViolationCount + 1,
                        CooldownUntil = datetime('now', '+' || MIN(ViolationCount * 2, 60) || ' minutes')";
                _esc.Parameters.AddWithValue("$u", from);
                _esc.ExecuteNonQuery();
                // Add trust penalty only for low trust users
                if (_trustLevel == "low")
                {
                    TrustEventService.OnRateLimit(_db3, from);
                }
                throw new HubException("För många meddelanden. Vänta lite.");
            }
        }
        // 3b. Cross-user spam detection
        using (var _spamDb = DbHelpers.OpenDb())
        {
            // Track recent recipients for this user (last 5 seconds)
            using var _spamEnsure = _spamDb.CreateCommand();
            _spamEnsure.CommandText = @"CREATE TABLE IF NOT EXISTS SpamTracking (
                UserId INTEGER NOT NULL, ToUser TEXT NOT NULL, MessageHash TEXT NOT NULL,
                SentAt TEXT NOT NULL)";
            _spamEnsure.ExecuteNonQuery();

            // Clean old entries
            using var _spamClean = _spamDb.CreateCommand();
            _spamClean.CommandText = "DELETE FROM SpamTracking WHERE SentAt < datetime('now', '-10 seconds')";
            _spamClean.ExecuteNonQuery();

            var _msgHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(_msgText ?? ""))).Substring(0, 16);

            // Count unique recipients in last 2 seconds with same message hash
            using var _spamCount = _spamDb.CreateCommand();
            _spamCount.CommandText = @"SELECT COUNT(DISTINCT ToUser) FROM SpamTracking
                WHERE UserId=(SELECT Id FROM AuthUsers WHERE Username=$u)
                AND MessageHash=$hash
                AND SentAt > datetime('now', '-2 seconds')";
            _spamCount.Parameters.AddWithValue("$u", from);
            _spamCount.Parameters.AddWithValue("$hash", _msgHash);
            var _uniqueRecipients = Convert.ToInt32(_spamCount.ExecuteScalar());

            if (_uniqueRecipients >= 3)
            {
                TrustEventService.OnSpam(_spamDb, from, _uniqueRecipients);
                throw new HubException("Spambeteende detekterat. Du är tillfälligt blockerad.");
            }

            // Count unique recipients in last 5 seconds (more lenient)
            using var _spamCount2 = _spamDb.CreateCommand();
            _spamCount2.CommandText = @"SELECT COUNT(DISTINCT ToUser) FROM SpamTracking
                WHERE UserId=(SELECT Id FROM AuthUsers WHERE Username=$u)
                AND SentAt > datetime('now', '-5 seconds')";
            _spamCount2.Parameters.AddWithValue("$u", from);
            var _totalRecipients5s = Convert.ToInt32(_spamCount2.ExecuteScalar());

            if (_totalRecipients5s >= 5)
            {
                TrustEventService.OnSpam(_spamDb, from, _totalRecipients5s);
                throw new HubException("Du skickar meddelanden till för många användare för snabbt. Vänta lite.");
            }

            // Record this message
            using var _spamInsert = _spamDb.CreateCommand();
            _spamInsert.CommandText = @"INSERT INTO SpamTracking (UserId, ToUser, MessageHash, SentAt)
                SELECT Id, $to, $hash, datetime('now') FROM AuthUsers WHERE Username=$u";
            _spamInsert.Parameters.AddWithValue("$to", (req.To ?? "").Trim().ToLowerInvariant());
            _spamInsert.Parameters.AddWithValue("$hash", _msgHash);
            _spamInsert.Parameters.AddWithValue("$u", from);
            _spamInsert.ExecuteNonQuery();
        }

        // 4. Firewall — normalize and check (DB-driven rules + static rules)
        if (!string.IsNullOrWhiteSpace(_msgText))
        {
            var _norm = _msgText.ToLowerInvariant()
                .Replace("0", "o").Replace("1", "i").Replace("3", "e").Replace("4", "a")
                .Replace("5", "s").Replace("$", "s").Replace("@", "a").Replace("!", "i").Replace("|", "i");
            string? _fwBlock = null;
            string? _fwAction = "block";
            string? _fwCategory = null;
            int _fwPenaltyOverride = -1;
            if (_msgText.Length > 2000) { _fwBlock = "length_exceeded"; _fwAction = "block"; }
            // Static rules (always checked regardless of DB)
            else if (System.Text.RegularExpressions.Regex.IsMatch(_norm, "csam|child porn|kiddie porn|cp link")) { _fwBlock = "csam_absolute"; _fwAction = "block"; _fwPenaltyOverride = 100; }
            else if (System.Text.RegularExpressions.Regex.IsMatch(_norm, "buy now|free money|get rich|make money fast|you have been selected")) { _fwBlock = "spam_solicitation"; _fwAction = "block"; }
            else if (System.Text.RegularExpressions.Regex.IsMatch(_norm, "verify your account|confirm your password|update your billing")) { _fwBlock = "phishing_pattern"; _fwAction = "block"; }
            else if (System.Text.RegularExpressions.Regex.IsMatch(_msgText, @"(.){8,}")) { _fwBlock = "repeat_chars"; _fwAction = "block"; }
            else if (System.Text.RegularExpressions.Regex.IsMatch(_norm, "discord.gg/|t.me/|telegram.me/")) { _fwBlock = "invite_spam"; _fwAction = "block"; }
            else
            {
                // DB-driven content filter rules
                using var _ruleDb = DbHelpers.OpenDb();
                using var _ruleCmd = _ruleDb.CreateCommand();
                _ruleCmd.CommandText = "SELECT Pattern, Category, Action, Severity, PenaltyPts FROM ContentFilterRules WHERE IsActive=1";
                using var _ruleR = _ruleCmd.ExecuteReader();
                while (_ruleR.Read() && _fwBlock == null)
                {
                    var _pattern = _ruleR.GetString(0);
                    try
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(_norm, _pattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            _fwBlock = _ruleR.GetString(1);
                            _fwAction = _ruleR.GetString(2);
                            _fwCategory = _ruleR.GetString(1);
                            _fwPenaltyOverride = _ruleR.GetInt32(4);
                        }
                    }
                    catch { /* invalid regex — skip */ }
                }
                _ruleR.Close();
                // URL rules
                if (_fwBlock == null)
                {
                    var _urlCount = System.Text.RegularExpressions.Regex.Matches(_msgText, @"https?://[^\s]+|www\.[^\s]+\.[a-z]{2,}").Count;
                    if (_urlCount > 0 && _trustLevel == "low") { _fwBlock = "link_low_trust"; _fwAction = "block"; }
                    else if (_urlCount > 2 && _trustLevel == "medium") { _fwBlock = "link_count_exceeded"; _fwAction = "block"; }
                }
            }
            // Soft moderation: "flag" action — queue message for review instead of blocking
            if (_fwBlock != null && _fwAction == "flag")
            {
                using var _sqDb = DbHelpers.OpenDb();
                using var _sqCmd = _sqDb.CreateCommand();
                _sqCmd.CommandText = @"INSERT INTO SoftModerationQueue (UserId, ToUser, MessageText, RuleMatched, Action, Status, CreatedAt)
                    SELECT Id, $to, $msg, $rule, 'flag', 'pending', datetime('now') FROM AuthUsers WHERE Username=$u";
                _sqCmd.Parameters.AddWithValue("$to", (req.To ?? "").Trim().ToLowerInvariant());
                _sqCmd.Parameters.AddWithValue("$msg", _msgText.Length > 500 ? _msgText[..500] : _msgText);
                _sqCmd.Parameters.AddWithValue("$rule", _fwBlock);
                _sqCmd.Parameters.AddWithValue("$u", from);
                _sqCmd.ExecuteNonQuery();
                // Log event but don't block — message goes through with delay signal
                using var _sqEvt = _sqDb.CreateCommand();
                _sqEvt.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail,  CreatedAt)
                    SELECT Id, 'message_flagged', 'warn', $d,  datetime('now') FROM AuthUsers WHERE Username=$u";
                _sqEvt.Parameters.AddWithValue("$d", $"Rule={_fwBlock} Category={_fwCategory}");
                _sqEvt.Parameters.AddWithValue("$ip", _ip); _sqEvt.Parameters.AddWithValue("$u", from);
                _sqEvt.ExecuteNonQuery();
                _fwBlock = null; // Allow through but flagged
            }
            if (_fwBlock != null)
            {
                using var _db4 = DbHelpers.OpenDb();
                using var _fwc = _db4.CreateCommand();
                _fwc.CommandText = @"INSERT INTO FirewallBlocks (UserId, RuleMatched, NormalizedText, TrustLevel,  BlockedAt)
                    SELECT Id, $rule, $norm, $trust,  datetime('now') FROM AuthUsers WHERE Username=$u";
                _fwc.Parameters.AddWithValue("$rule", _fwBlock);
                _fwc.Parameters.AddWithValue("$norm", _norm.Length > 200 ? _norm[..200] : _norm);
                _fwc.Parameters.AddWithValue("$trust", _trustLevel);
                _fwc.Parameters.AddWithValue("$ip", _ip); _fwc.Parameters.AddWithValue("$u", from);
                _fwc.ExecuteNonQuery();
                using var _fwe = _db4.CreateCommand();
                _fwe.CommandText = @"INSERT INTO SecurityEvents (UserId, EventType, Severity, Detail,  CreatedAt)
                    SELECT Id, 'message_firewall_block', 'warn', $d,  datetime('now') FROM AuthUsers WHERE Username=$u";
                _fwe.Parameters.AddWithValue("$d", $"Rule={_fwBlock} Trust={_trustLevel}");
                _fwe.Parameters.AddWithValue("$ip", _ip); _fwe.Parameters.AddWithValue("$u", from);
                _fwe.ExecuteNonQuery();
                // Trust penalty for firewall hit (severity depends on rule)
                int _fwPenalty = _fwPenaltyOverride >= 0 ? _fwPenaltyOverride : (_fwBlock == "csam_absolute" ? 100 : _fwBlock == "phishing_pattern" ? 20 : 10);
                string _fwExpiry = _fwPenalty >= 50 ? "'+30 days'" : _fwPenalty >= 25 ? "'+7 days'" : "'+24 hours'";
                using var _fwpen = _db4.CreateCommand();
                _fwpen.CommandText = $@"INSERT INTO TrustPenalties (UserId, EventType, PenaltyPoints, ExpiresAt, CreatedAt)
                    SELECT Id, 'firewall_block', {_fwPenalty}, datetime('now', {_fwExpiry}), datetime('now') FROM AuthUsers WHERE Username=$u";
                _fwpen.Parameters.AddWithValue("$u", from);
                _fwpen.ExecuteNonQuery();
                throw new HubException("Meddelandet blockerades av säkerhetsreglerna.");
            }
        }
        // --- END ENFORCEMENT ---
        var v = ChatHelpers.ValidateOutgoingMessage(req, from); if (!v.Ok) throw new HubException(v.Message); var ts = DateTime.UtcNow.ToString("o"); long id; using (var db = DbHelpers.OpenDb()) { using var cmd = db.CreateCommand(); cmd.CommandText = @"INSERT INTO ChatMessages (FromUser,ToUser,Message,Timestamp,Encrypted,Iv,Algorithm,EncryptedKey,SenderEncryptedKey,RecipientKeysJson,SenderKeysJson,ReplyToId) VALUES ($from,$to,$msg,$ts,$enc,$iv,$alg,$ek,$sek,$rk,$sk,$rid); SELECT last_insert_rowid();"; cmd.Parameters.AddWithValue("$from", from); cmd.Parameters.AddWithValue("$to", v.ToUser); cmd.Parameters.AddWithValue("$msg", v.CipherText); cmd.Parameters.AddWithValue("$ts", ts); cmd.Parameters.AddWithValue("$enc", v.Encrypted ? 1 : 0); cmd.Parameters.AddWithValue("$iv", v.Iv); cmd.Parameters.AddWithValue("$alg", v.Algorithm); cmd.Parameters.AddWithValue("$ek", v.EncryptedKey); cmd.Parameters.AddWithValue("$sek", v.SenderEncryptedKey); cmd.Parameters.AddWithValue("$rk", v.RecipientKeysJson); cmd.Parameters.AddWithValue("$sk", v.SenderKeysJson); cmd.Parameters.AddWithValue("$rid", req.ReplyToId ?? 0); id = (long)(cmd.ExecuteScalar() ?? 0L); }
        var payload = ChatHelpers.BuildPayload(id, from, v.ToUser, v.CipherText, ts, v.Encrypted, v.Iv, v.Algorithm, v.EncryptedKey, v.SenderEncryptedKey, v.RecipientKeysJson, v.SenderKeysJson, req.ReplyToId ?? 0); await Clients.User(v.ToUser).SendAsync("ReceiveMessage", payload); if (v.ToUser == from) await Clients.User(from).SendAsync("ReceiveMessage", payload);
    }
    public async Task SendTyping(string toUser) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me) && !string.IsNullOrWhiteSpace(toUser)) await Clients.User(toUser.Trim().ToLowerInvariant()).SendAsync("ReceiveTyping", me); }
    public async Task SendReaction(string peer, string messageId, string emoji) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me) && !string.IsNullOrWhiteSpace(peer) && !string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(emoji)) { await Clients.User(peer.Trim().ToLowerInvariant()).SendAsync("ReceiveReaction", messageId, emoji, me); await Clients.User(me).SendAsync("ReceiveReaction", messageId, emoji, me); } }

    // Group channels
    public async Task JoinGroupChannel(string groupId, string channelId) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) throw new HubException("Inte inloggad."); var gid = (groupId ?? "").Trim().ToLowerInvariant(); var cid = (channelId ?? "").Trim().ToLowerInvariant(); if (!GroupHelpers.IsMember(gid, me)) throw new HubException("Inte medlem."); await Groups.AddToGroupAsync(Context.ConnectionId, $"group:{gid}:{cid}"); }
    public async Task LeaveGroupChannel(string groupId, string channelId) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me)) await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"group:{(groupId ?? "").Trim().ToLowerInvariant()}:{(channelId ?? "").Trim().ToLowerInvariant()}"); }
    public async Task SendGroupMessage(string groupId, string channelId, string text) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) throw new HubException("Inte inloggad."); var gid = (groupId ?? "").Trim().ToLowerInvariant(); var cid = (channelId ?? "").Trim().ToLowerInvariant(); if (!GroupHelpers.IsMember(gid, me)) throw new HubException("Inte medlem."); var msg = InputSanitizer.SanitizeInput(text ?? "", 2000).Trim(); if (string.IsNullOrWhiteSpace(msg)) throw new HubException("Tomt."); var ts = DateTime.UtcNow.ToString("o"); long id; using (var db = DbHelpers.OpenDb()) { using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO GroupMessages (GroupId,ChannelId,FromUser,Message,Timestamp) VALUES ($g,$c,$u,$m,$t); SELECT last_insert_rowid();"; cmd.Parameters.AddWithValue("$g", gid); cmd.Parameters.AddWithValue("$c", cid); cmd.Parameters.AddWithValue("$u", me); cmd.Parameters.AddWithValue("$m", msg); cmd.Parameters.AddWithValue("$t", ts); id = (long)(cmd.ExecuteScalar() ?? 0L); } var payload = new { id, groupId = gid, channelId = cid, from = me, text = msg, ts }; await Clients.Group($"group:{gid}:{cid}").SendAsync("ReceiveGroupMessage", payload); }
    public async Task SendGroupTyping(string groupId, string channelId) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me)) await Clients.Group($"group:{(groupId ?? "").Trim().ToLowerInvariant()}:{(channelId ?? "").Trim().ToLowerInvariant()}").SendAsync("ReceiveGroupTyping", me); }


    // Private Calls
    public async Task CallUser(string toUser) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) return; var target = (toUser ?? "").Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(target) || target == me) return; await Clients.User(target).SendAsync("IncomingCall", me); }
    public async Task AcceptCall(string fromUser) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) return; var target = (fromUser ?? "").Trim().ToLowerInvariant(); await Clients.User(target).SendAsync("CallAccepted", me); }
    public async Task RejectCall(string fromUser) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) return; var target = (fromUser ?? "").Trim().ToLowerInvariant(); await Clients.User(target).SendAsync("CallRejected", me); }
    public async Task EndCall(string otherUser) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) return; var target = (otherUser ?? "").Trim().ToLowerInvariant(); await Clients.User(target).SendAsync("CallEnded", me); }
    public async Task SendCallOffer(string toUser, string sdp) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me)) await Clients.User(toUser.Trim().ToLowerInvariant()).SendAsync("CallOffer", me, sdp); }
    public async Task SendCallAnswer(string toUser, string sdp) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me)) await Clients.User(toUser.Trim().ToLowerInvariant()).SendAsync("CallAnswer", me, sdp); }
    public async Task SendCallIce(string toUser, string candidate) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me)) await Clients.User(toUser.Trim().ToLowerInvariant()).SendAsync("CallIce", me, candidate); }

    // Presence
    public override async Task OnConnectedAsync() { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (!string.IsNullOrWhiteSpace(me)) { var presence = Context.GetHttpContext()!.RequestServices.GetRequiredService<PresenceTracker>(); var wasOnline = presence.IsOnline(me); presence.AddConnection(me, Context.ConnectionId); if (!wasOnline) await Clients.All.SendAsync("PresenceUpdate", me, "online"); } await base.OnConnectedAsync(); }
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var username = Context.User?.Identity?.Name?.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(username))
        {
            var presence = Context.GetHttpContext()!
                .RequestServices
                .GetRequiredService<PresenceTracker>();

            presence.RemoveConnection(username, Context.ConnectionId);

            if (!presence.IsOnline(username))
                await Clients.All.SendAsync("PresenceUpdate", username, "offline");
        }

        await base.OnDisconnectedAsync(exception);
    }
    // ══════════════════════════════════════
    // SPACES REAL-TIME
    // ══════════════════════════════════════
    public async Task JoinSpace(string publicId) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) throw new HubException("Not logged in"); using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT 1 FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u"; cmd.Parameters.AddWithValue("$pid", publicId); cmd.Parameters.AddWithValue("$u", me); if (cmd.ExecuteScalar() == null) throw new HubException("Not a member"); await Groups.AddToGroupAsync(Context.ConnectionId, "space:" + publicId); }
    public async Task LeaveSpace(string publicId) { await Groups.RemoveFromGroupAsync(Context.ConnectionId, "space:" + publicId); }
    public async Task SendSpaceMessage(string publicId, long channelId, string text) { var me = Context.User?.Identity?.Name?.Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(me)) throw new HubException("Not logged in"); if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) throw new HubException("Invalid message"); using var db = DbHelpers.OpenDb(); using var memCmd = db.CreateCommand(); memCmd.CommandText = "SELECT u.Id, u.AvatarUrl FROM SpaceMembers sm JOIN Spaces s ON s.Id = sm.SpaceId JOIN AuthUsers u ON u.Id = sm.UserId WHERE s.PublicId = $pid AND u.Username = $u"; memCmd.Parameters.AddWithValue("$pid", publicId); memCmd.Parameters.AddWithValue("$u", me); using var memR = memCmd.ExecuteReader(); if (!memR.Read()) throw new HubException("Not a member"); var userId = memR.GetInt64(0); var avatarUrl = memR.IsDBNull(1) ? "" : memR.GetString(1); memR.Close(); using var insCmd = db.CreateCommand(); insCmd.CommandText = "INSERT INTO SpaceMessages (ChannelId, UserId, Content, CreatedAt) VALUES ($cid, $uid, $txt, datetime('now'))"; insCmd.Parameters.AddWithValue("$cid", channelId); insCmd.Parameters.AddWithValue("$uid", userId); insCmd.Parameters.AddWithValue("$txt", text); insCmd.ExecuteNonQuery(); var msgId = Convert.ToInt64(new Microsoft.Data.Sqlite.SqliteCommand("SELECT last_insert_rowid()", db).ExecuteScalar()); var ts = DateTime.UtcNow.ToString("o"); await Clients.Group("space:" + publicId).SendAsync("SpaceMessage", new { id = msgId, channelId = channelId, content = text, username = me, avatarUrl = avatarUrl, createdAt = ts }); }


}
// ═══════════════════════════════════════════════
// TOTP / LOGIN HISTORY / PASSWORD HISTORY
// ═══════════════════════════════════════════════
public static class TotpHelper
{
    public static string GenerateSecret()
    {
        return Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
    }

    public static bool VerifyCode(string secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            var totp = new Totp(Base32Encoding.ToBytes(secret.Trim()));
            return totp.VerifyTotp(
                code.Trim(),
                out _,
                new VerificationWindow(previous: 1, future: 1)
            );
        }
        catch
        {
            return false;
        }
    }

    public static void SaveBackupCodes(string u, string[] codes)
    {
        try
        {
            using var db = DbHelpers.OpenDb();

            foreach (var c in codes)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT INTO BackupCodes (Username,CodeHash,Used,CreatedAt) VALUES ($u,$h,0,$t)";
                cmd.Parameters.AddWithValue("$u", u);
                cmd.Parameters.AddWithValue("$h", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c))));
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
        }
    }

    public static bool VerifyAndUseBackupCode(string u, string code)
    {
        if (string.IsNullOrWhiteSpace(u) || string.IsNullOrWhiteSpace(code))
            return false;

        var normalized = code.Trim().Replace(" ", "").ToUpperInvariant();

        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, "^[0-9A-F]{8}$"))
            return false;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

        try
        {
            using var db = DbHelpers.OpenDb();
            using var tx = db.BeginTransaction();

            long id = 0;

            using (var find = db.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = @"
                    SELECT Id
                    FROM BackupCodes
                    WHERE LOWER(Username)=LOWER($u)
                      AND CodeHash=$h
                      AND Used=0
                    LIMIT 1";
                find.Parameters.AddWithValue("$u", u);
                find.Parameters.AddWithValue("$h", hash);

                var obj = find.ExecuteScalar();
                if (obj == null || obj == DBNull.Value)
                {
                    tx.Rollback();
                    return false;
                }

                id = Convert.ToInt64(obj);
            }

            using (var mark = db.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = "UPDATE BackupCodes SET Used=1 WHERE Id=$id AND Used=0";
                mark.Parameters.AddWithValue("$id", id);

                if (mark.ExecuteNonQuery() <= 0)
                {
                    tx.Rollback();
                    return false;
                }
            }

            tx.Commit();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class LoginHistory
{
    public static void Record(string u, string ip, string ua, string fp, bool ok)
    {
        try
        {
            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();

            c.CommandText = "INSERT INTO LoginHistory (Username,UserAgent,DeviceFingerprint,Success,Timestamp) VALUES ($u,$ua,$fp,$s,$ts)";
            c.Parameters.AddWithValue("$u", DefensiveInput.CleanString(u, 32));
            c.Parameters.AddWithValue("$ua", DefensiveInput.CleanString(ua, 300));
            c.Parameters.AddWithValue("$fp", DefensiveInput.CleanString(fp, 200));
            c.Parameters.AddWithValue("$s", ok ? 1 : 0);
            c.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));

            c.ExecuteNonQuery();
        }
        catch { }
    }

    public static List<object> GetForUser(string u)
    {
        // rs-login-history-from-activitylog-v1
        var rows = new List<object>();

        try
        {
            var username = DefensiveInput.CleanString(u, 32).ToLowerInvariant();
            if (!DefensiveInput.IsUsername(username))
                return rows;

            using var db = DbHelpers.OpenDb();
            using var c = db.CreateCommand();

            c.CommandText = @"
                SELECT Action, Details, Timestamp
                FROM ActivityLog
                WHERE LOWER(Username)=LOWER($u)
                  AND Action IN (
                    'login_with_key',
                    'register_with_key'
                  )
                ORDER BY Timestamp DESC
                LIMIT 50";
            c.Parameters.AddWithValue("$u", username);

            using var r = c.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new
                {
                    action = r.IsDBNull(0) ? "security_event" : DefensiveInput.CleanString(r.GetString(0), 80),
                    details = r.IsDBNull(1) ? "" : DefensiveInput.CleanString(r.GetString(1), 300),
                    timestamp = r.IsDBNull(2) ? "" : DefensiveInput.CleanString(r.GetString(2), 64),
                    success = true
                });
            }
        }
        catch { }

        return rows;
    }
}

public static class PasswordHistory { public static void Save(string u, string h) { try { using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "INSERT INTO PasswordHistory (Username,PasswordHash,CreatedAt) VALUES ($u,$h,$t)"; c.Parameters.AddWithValue("$u", u); c.Parameters.AddWithValue("$h", h); c.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o")); c.ExecuteNonQuery(); } catch { } } public static bool WasUsedBefore(string u, string pw) { try { using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT PasswordHash FROM PasswordHistory WHERE Username=$u ORDER BY Id DESC LIMIT 10"; c.Parameters.AddWithValue("$u", u); using var r = c.ExecuteReader(); while (r.Read()) { var h = r.IsDBNull(0) ? "" : r.GetString(0); if (!string.IsNullOrWhiteSpace(h) && BCrypt.Net.BCrypt.Verify(pw, h)) return true; } } catch { } return false; } }

public static class MarketHelpers
{
    public static (string id, string username)? GetMarketUser(HttpContext ctx)
    {
        // First check market_session cookie
        if (ctx.Request.Cookies.TryGetValue("market_session", out var session) && !string.IsNullOrEmpty(session))
        {
            var parts = session.Split(':');
            if (parts.Length == 2) return (parts[0], parts[1]);
        }

        // Fallback: check if RunSpace user is linked
        var authUser = ctx.User.Identity?.Name?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(authUser) && AppHelpers.UserExists(authUser))
        {
            using var db = new SqliteConnection($"Data Source=/var/lib/runspace/runspace.db");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT mu.Id, mu.Username FROM MarketUsers mu
                                JOIN AuthUsers au ON au.Id = mu.LinkedAuthUserId
                                WHERE LOWER(au.Username)=$u LIMIT 1";
            cmd.Parameters.AddWithValue("$u", authUser);
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read()) return (rdr.GetString(0), rdr.GetString(1));
        }

        return null;
    }
}

// ═══════════════════════════════════════════════
// DB HELPERS + SCHEMA
// ═══════════════════════════════════════════════
public static class DbHelpers
{
    public static SqliteConnection OpenDb()
    {
        var db = new SqliteConnection(AppConfig.ConnectionString); db.Open();
        using var p = db.CreateCommand(); p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; p.ExecuteNonQuery();
        return db;
    }

    public static void EnsureDatabase()
    {
        using var db = OpenDb();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS AuthUsers (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL UNIQUE, PasswordHash TEXT NOT NULL,
    Bio TEXT NOT NULL DEFAULT '', AvatarUrl TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT '',
    Status TEXT NOT NULL DEFAULT 'verified', Badges TEXT NOT NULL DEFAULT '[]', PublicKey TEXT NOT NULL DEFAULT '',
    TwoFactorEnabled INTEGER NOT NULL DEFAULT 0, TwoFactorSecret TEXT NOT NULL DEFAULT '',
    PasswordChangedAt TEXT NOT NULL DEFAULT '', LoginCount INTEGER NOT NULL DEFAULT 0,
    LastLoginAt TEXT NOT NULL DEFAULT '', LastLoginIp TEXT NOT NULL DEFAULT '', AccountLockedUntil TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS UserDeviceKeys (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, DeviceId TEXT NOT NULL,
    DeviceName TEXT NOT NULL DEFAULT '', PublicKey TEXT NOT NULL, CreatedAt TEXT NOT NULL, LastUsedAt TEXT NOT NULL,
    UNIQUE(Username, DeviceId)
);
CREATE TABLE IF NOT EXISTS ChatMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, FromUser TEXT NOT NULL, ToUser TEXT NOT NULL,
    Message TEXT NOT NULL, Timestamp TEXT NOT NULL, Encrypted INTEGER NOT NULL DEFAULT 0,
    Iv TEXT NOT NULL DEFAULT '', Algorithm TEXT NOT NULL DEFAULT '', EncryptedKey TEXT NOT NULL DEFAULT '',
    SenderEncryptedKey TEXT NOT NULL DEFAULT '', RecipientKeysJson TEXT NOT NULL DEFAULT '[]',
    SenderKeysJson TEXT NOT NULL DEFAULT '[]', ReplyToId INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS ChatReactions (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, MessageId INTEGER NOT NULL, Username TEXT NOT NULL,
    Emoji TEXT NOT NULL, CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS ActivityLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, Action TEXT NOT NULL,
    Details TEXT NOT NULL, Timestamp TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS SupportTickets (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, TicketId TEXT NOT NULL, Username TEXT NOT NULL,
    Category TEXT NOT NULL, Subject TEXT NOT NULL, Description TEXT NOT NULL,
    Status TEXT NOT NULL DEFAULT 'open', CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS SecurityAuditLog (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, EventType TEXT NOT NULL, Username TEXT NOT NULL DEFAULT '',
    IpAddress TEXT NOT NULL DEFAULT '', Details TEXT NOT NULL DEFAULT '', Success INTEGER NOT NULL DEFAULT 0, Timestamp TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS LoginHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, IpAddress TEXT NOT NULL DEFAULT '',
    UserAgent TEXT NOT NULL DEFAULT '', DeviceFingerprint TEXT NOT NULL DEFAULT '', Success INTEGER NOT NULL DEFAULT 0, Timestamp TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS PasswordHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, PasswordHash TEXT NOT NULL, CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS BackupCodes (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, Username TEXT NOT NULL, CodeHash TEXT NOT NULL, Used INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL
);
-- Group tables
CREATE TABLE IF NOT EXISTS Groups (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupId TEXT NOT NULL UNIQUE, Name TEXT NOT NULL,
    Description TEXT NOT NULL DEFAULT '', OwnerUsername TEXT NOT NULL, CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS GroupMembers (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupId TEXT NOT NULL, Username TEXT NOT NULL,
    Role TEXT NOT NULL DEFAULT 'member', JoinedAt TEXT NOT NULL,
    UNIQUE(GroupId, Username)
);
CREATE TABLE IF NOT EXISTS GroupChannels (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupId TEXT NOT NULL, ChannelId TEXT NOT NULL UNIQUE,
    Name TEXT NOT NULL, Type TEXT NOT NULL DEFAULT 'text', CreatedAt TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS GroupMessages (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupId TEXT NOT NULL, ChannelId TEXT NOT NULL,
    FromUser TEXT NOT NULL, Message TEXT NOT NULL, Timestamp TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS GroupInvites (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, GroupId TEXT NOT NULL, InvitedBy TEXT NOT NULL,
    InvitedUser TEXT NOT NULL, CreatedAt TEXT NOT NULL,
    UNIQUE(GroupId, InvitedUser)
);

CREATE INDEX IF NOT EXISTS IX_ChatMessages_Users ON ChatMessages (FromUser, ToUser, Id);
CREATE INDEX IF NOT EXISTS IX_ChatMessages_To ON ChatMessages (ToUser, Id);
CREATE INDEX IF NOT EXISTS IX_ChatReactions_MsgId ON ChatReactions (MessageId);
CREATE INDEX IF NOT EXISTS IX_ChatReactions_User ON ChatReactions (Username);
CREATE INDEX IF NOT EXISTS IX_AuthUsers_Username ON AuthUsers (Username);
CREATE INDEX IF NOT EXISTS IX_UserDeviceKeys_User ON UserDeviceKeys (Username);
CREATE INDEX IF NOT EXISTS IX_SecurityAuditLog_Ts ON SecurityAuditLog (Timestamp);
CREATE INDEX IF NOT EXISTS IX_LoginHistory_User ON LoginHistory (Username);
CREATE INDEX IF NOT EXISTS IX_Groups_GroupId ON Groups (GroupId);
CREATE INDEX IF NOT EXISTS IX_GroupMembers_Group ON GroupMembers (GroupId);
CREATE INDEX IF NOT EXISTS IX_GroupMembers_User ON GroupMembers (Username);
CREATE INDEX IF NOT EXISTS IX_GroupChannels_Group ON GroupChannels (GroupId);
CREATE INDEX IF NOT EXISTS IX_GroupMessages_Channel ON GroupMessages (GroupId, ChannelId, Id);
CREATE INDEX IF NOT EXISTS IX_GroupInvites_User ON GroupInvites (InvitedUser);";
        cmd.ExecuteNonQuery();

        EnsureColumn(db, "ChatMessages", "ReplyToId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "AuthUsers", "TwoFactorEnabled", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "AuthUsers", "TwoFactorSecret", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "AuthUsers", "PasswordChangedAt", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "AuthUsers", "LoginCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "AuthUsers", "LastLoginAt", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "AuthUsers", "LastLoginIp", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "AuthUsers", "AccountLockedUntil", "TEXT NOT NULL DEFAULT ''");
    }

    public static void EnsureColumn(SqliteConnection db, string table, string col, string def)
    {
        using var check = db.CreateCommand(); check.CommandText = $"PRAGMA table_info({table})";
        bool exists = false; using (var r = check.ExecuteReader()) { while (r.Read()) { if (string.Equals(r.IsDBNull(1) ? "" : r.GetString(1), col, StringComparison.OrdinalIgnoreCase)) { exists = true; break; } } }
        if (!exists) { using var alter = db.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {col} {def}"; alter.ExecuteNonQuery(); }
    }
}

// ═══════════════════════════════════════════════
// CHAT HELPERS (DM)
// ═══════════════════════════════════════════════
public static class ChatHelpers
{
    public static string BuildGroupName(string a, string b) { var p = new[] { a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant() }; Array.Sort(p, StringComparer.Ordinal); return $"private:{p[0]}:{p[1]}"; }
    public static OutgoingValidationResult ValidateOutgoingMessage(ChatMessageReq req, string from)
    {
        var to = (req.To ?? "").Trim().ToLowerInvariant(); var text = (req.Text ?? "").Trim(); var iv = (req.Iv ?? "").Trim(); var ek = (req.EncryptedKey ?? "").Trim(); var sek = (req.SenderEncryptedKey ?? "").Trim(); var alg = string.IsNullOrWhiteSpace(req.Algorithm) ? "plain" : req.Algorithm.Trim(); var enc = req.Encrypted == 1;
        /* TEMP QT beta: allow plain messages until desktop E2EE exists.
           Restore E2EE check before production desktop release. */
        var rk = JsonHelpers.NormalizeJsonArray(req.RecipientKeys); var sk = JsonHelpers.NormalizeJsonArray(req.SenderKeys); if (string.IsNullOrWhiteSpace(to) || !AppHelpers.IsValidUsername(to) || to == from) return OutgoingValidationResult.Fail("Ogiltig mottagare.", 400); if (!AppHelpers.UserExists(to)) return OutgoingValidationResult.Fail("Finns inte.", 404); if (AppHelpers.IsUserBanned(from) || AppHelpers.IsUserBanned(to)) return OutgoingValidationResult.Fail("Spärrad.", 403); if (string.IsNullOrWhiteSpace(text) || text.Length > AppConfig.MaxCipherPayloadLength) return OutgoingValidationResult.Fail("Ogiltigt meddelande.", 400); if (enc && (string.IsNullOrWhiteSpace(iv) || !string.Equals(alg, "AES-GCM", StringComparison.OrdinalIgnoreCase))) return OutgoingValidationResult.Fail("Krypteringsfel.", 400); return OutgoingValidationResult.Success(to, text, iv, ek, sek, alg, enc, rk, sk);
    }
    public static object BuildPayload(long id, string from, string to, string text, string ts, bool enc, string iv, string alg, string ek, string sek, string rk, string sk, long replyTo) => new { id, from, to, text, ts, encrypted = enc, iv, algorithm = alg, encryptedKey = ek, senderEncryptedKey = sek, recipientKeys = JsonHelpers.DeserializeDeviceCipherArray(rk), senderKeys = JsonHelpers.DeserializeDeviceCipherArray(sk), replyTo = replyTo > 0 ? replyTo : (long?)null };
    static object ReadPayload(SqliteDataReader r) => BuildPayload(r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4), !r.IsDBNull(5) && r.GetInt32(5) == 1, r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(7) ? "plain" : r.GetString(7), r.IsDBNull(8) ? "" : r.GetString(8), r.IsDBNull(9) ? "" : r.GetString(9), r.IsDBNull(10) ? "[]" : r.GetString(10), r.IsDBNull(11) ? "[]" : r.GetString(11), r.IsDBNull(12) ? 0 : r.GetInt64(12));
    public static List<object> LoadHistory(string me, string peer) { var rows = new List<object>(); using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Id,FromUser,ToUser,Message,Timestamp,Encrypted,Iv,Algorithm,EncryptedKey,SenderEncryptedKey,RecipientKeysJson,SenderKeysJson,ReplyToId FROM ChatMessages WHERE (FromUser=$me AND ToUser=$p) OR (FromUser=$p AND ToUser=$me) ORDER BY Id ASC LIMIT 500"; cmd.Parameters.AddWithValue("$me", me); cmd.Parameters.AddWithValue("$p", peer); using var r = cmd.ExecuteReader(); while (r.Read()) rows.Add(ReadPayload(r)); return rows; }
    public static List<object> LoadConversations(string me) { var rows = new List<object>(); using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand(); cmd.CommandText = @"WITH ranked AS (SELECT Id, CASE WHEN FromUser=$me THEN ToUser ELSE FromUser END AS Peer, FromUser,ToUser,Message,Timestamp,Encrypted,Iv,Algorithm,EncryptedKey,SenderEncryptedKey,RecipientKeysJson,SenderKeysJson,ReplyToId, ROW_NUMBER() OVER (PARTITION BY CASE WHEN FromUser=$me THEN ToUser ELSE FromUser END ORDER BY Id DESC) AS rn FROM ChatMessages WHERE FromUser=$me OR ToUser=$me) SELECT Id,Peer,FromUser,ToUser,Message,Timestamp,Encrypted,Iv,Algorithm,EncryptedKey,SenderEncryptedKey,RecipientKeysJson,SenderKeysJson,ReplyToId FROM ranked WHERE rn=1 ORDER BY Id DESC LIMIT 100"; cmd.Parameters.AddWithValue("$me", me); using var r = cmd.ExecuteReader(); while (r.Read()) rows.Add(new { id = r.GetInt64(0), peer = r.IsDBNull(1) ? "" : r.GetString(1), from = r.IsDBNull(2) ? "" : r.GetString(2), to = r.IsDBNull(3) ? "" : r.GetString(3), text = r.IsDBNull(4) ? "" : r.GetString(4), ts = r.IsDBNull(5) ? "" : r.GetString(5), encrypted = !r.IsDBNull(6) && r.GetInt32(6) == 1, iv = r.IsDBNull(7) ? "" : r.GetString(7), algorithm = r.IsDBNull(8) ? "plain" : r.GetString(8), encryptedKey = r.IsDBNull(9) ? "" : r.GetString(9), senderEncryptedKey = r.IsDBNull(10) ? "" : r.GetString(10), recipientKeys = JsonHelpers.DeserializeDeviceCipherArray(r.IsDBNull(11) ? "[]" : r.GetString(11)), senderKeys = JsonHelpers.DeserializeDeviceCipherArray(r.IsDBNull(12) ? "[]" : r.GetString(12)), replyTo = r.IsDBNull(13) ? 0 : r.GetInt64(13) }); return rows; }
}

public static class ChatKeyHelpers { public static List<object> GetUserDeviceKeys(string u) { var rows = new List<object>(); using var db = DbHelpers.OpenDb(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT DeviceId,DeviceName,PublicKey,CreatedAt,LastUsedAt FROM UserDeviceKeys WHERE Username=$u ORDER BY LastUsedAt DESC LIMIT 10"; cmd.Parameters.AddWithValue("$u", u); using var r = cmd.ExecuteReader(); while (r.Read()) rows.Add(new { deviceId = r.GetString(0), deviceName = r.IsDBNull(1) ? "" : r.GetString(1), publicKey = r.GetString(2), createdAt = r.IsDBNull(3) ? "" : r.GetString(3), lastUsedAt = r.IsDBNull(4) ? "" : r.GetString(4) }); return rows; } }

public static class JsonHelpers { public static string NormalizeJsonArray(List<DeviceCipherEntry>? v) { if (v == null || v.Count == 0 || v.Count > 20) return "[]"; var f = v.Where(x => !string.IsNullOrWhiteSpace(x.DeviceId) && !string.IsNullOrWhiteSpace(x.EncryptedKey)).Select(x => new DeviceCipherEntry(x.DeviceId.Trim(), x.EncryptedKey.Trim())).ToList(); return f.Count == 0 ? "[]" : JsonSerializer.Serialize(f); } public static object[] DeserializeDeviceCipherArray(string? json) { if (string.IsNullOrWhiteSpace(json)) return Array.Empty<object>(); try { var p = JsonSerializer.Deserialize<List<DeviceCipherEntry>>(json); return p?.Where(x => !string.IsNullOrWhiteSpace(x.DeviceId)).Select(x => (object)new { deviceId = x.DeviceId.Trim(), encryptedKey = x.EncryptedKey.Trim() }).ToArray() ?? Array.Empty<object>(); } catch { return Array.Empty<object>(); } } }

// ═══════════════════════════════════════════════
// GROUP HELPERS
// ═══════════════════════════════════════════════
public static class GroupHelpers
{
    public static bool IsMember(string gid, string u) { using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM GroupMembers WHERE GroupId=$g AND Username=$u"; c.Parameters.AddWithValue("$g", gid); c.Parameters.AddWithValue("$u", u); return Convert.ToInt32(c.ExecuteScalar()) > 0; }
    public static bool IsOwner(string gid, string u) { using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM Groups WHERE GroupId=$g AND OwnerUsername=$u"; c.Parameters.AddWithValue("$g", gid); c.Parameters.AddWithValue("$u", u); return Convert.ToInt32(c.ExecuteScalar()) > 0; }
    public static bool IsOwnerOrAdmin(string gid, string u) { if (IsOwner(gid, u)) return true; using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT Role FROM GroupMembers WHERE GroupId=$g AND Username=$u LIMIT 1"; c.Parameters.AddWithValue("$g", gid); c.Parameters.AddWithValue("$u", u); var role = c.ExecuteScalar() as string ?? ""; return role == "admin" || role == "owner"; }
    public static List<object> GetChannels(string gid) { var list = new List<object>(); using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT ChannelId,Name,Type,CreatedAt FROM GroupChannels WHERE GroupId=$g ORDER BY Id"; c.Parameters.AddWithValue("$g", gid); using var r = c.ExecuteReader(); while (r.Read()) list.Add(new { channelId = r.GetString(0), name = r.GetString(1), type = r.GetString(2), createdAt = r.GetString(3) }); return list; }
    public static List<object> GetMembers(string gid) { var list = new List<object>(); using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT Username,Role,JoinedAt FROM GroupMembers WHERE GroupId=$g ORDER BY Id"; c.Parameters.AddWithValue("$g", gid); using var r = c.ExecuteReader(); while (r.Read()) list.Add(new { username = r.GetString(0), role = r.GetString(1), joinedAt = r.GetString(2) }); return list; }
    public static List<object> GetGroupRoles(string gid)
    {
        var list = new List<object>();
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = "SELECT RoleId,Name,Color,Position,Permissions,IsDefault FROM GroupRoles WHERE GroupId=$g ORDER BY Position ASC";
        c.Parameters.AddWithValue("$g", gid);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(new
        {
            roleId = r.GetString(0),
            name = r.GetString(1),
            color = r.GetString(2),
            position = r.GetInt32(3),
            permissions = r.IsDBNull(4) ? "{}" : r.GetString(4),
            isDefault = r.GetInt32(5) == 1
        });
        return list;
    }


    public static List<object> GetMembersWithRoles(string gid)
    {
        var list = new List<object>();
        using var db = DbHelpers.OpenDb();
        using var c = db.CreateCommand();
        c.CommandText = @"SELECT m.Username, m.Role, m.JoinedAt, COALESCE(r.Name, m.Role) AS RoleName, COALESCE(r.Color, '#94a3b8') AS RoleColor, COALESCE(r.Position, 99) AS RolePosition
            FROM GroupMembers m LEFT JOIN GroupRoles r ON m.GroupId = r.GroupId AND m.Role = r.RoleId
            WHERE m.GroupId=$g ORDER BY COALESCE(r.Position, 99), m.Username";
        c.Parameters.AddWithValue("$g", gid);
        using var r2 = c.ExecuteReader();
        while (r2.Read()) list.Add(new
        {
            username = r2.GetString(0),
            role = r2.GetString(1),
            joinedAt = r2.GetString(2),
            roleName = r2.GetString(3),
            roleColor = r2.GetString(4),
            rolePosition = r2.GetInt32(5)
        });
        return list;
    }

}

// ═══════════════════════════════════════════════
// HELPERS + VALIDATORS + FILTERS (unchanged)
// ═══════════════════════════════════════════════
public static class AppHelpers { public static bool IsAdmin(string? u) { var t = (u ?? "").Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(t)) return false; try { using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT IsAdmin FROM AuthUsers WHERE LOWER(Username) = $u LIMIT 1"; c.Parameters.AddWithValue("$u", t); var result = c.ExecuteScalar(); if (result != null && result != System.DBNull.Value) return System.Convert.ToInt32(result) == 1; } catch { } return t == "mx403" || t == "mxssy"; } public static bool IsValidUsername(string u) => Regex.IsMatch(u, @"^[a-zA-Z0-9_\-]{3,32}$"); public static string GetAgeTextFromCreatedAt(string? c) { if (string.IsNullOrWhiteSpace(c) || !DateTime.TryParse(c, null, DateTimeStyles.RoundtripKind, out var d)) return ""; var diff = DateTime.UtcNow - d.ToUniversalTime(); if (diff.TotalSeconds < 60) return "just now"; if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m"; if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h"; if (diff.TotalDays < 30) return $"{(int)diff.TotalDays}d"; if (diff.TotalDays < 365) return $"{(int)(diff.TotalDays / 30)}mo"; return $"{(int)(diff.TotalDays / 365)}y"; } public static List<string> ParseBadges(string? raw) { try { return JsonSerializer.Deserialize<List<string>>(raw ?? "[]")?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).Distinct().Take(20).ToList() ?? new(); } catch { return new(); } } public static bool UserExists(string? u) { var t = (u ?? "").Trim().ToLowerInvariant(); if (string.IsNullOrWhiteSpace(t)) return false; using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT COUNT(*) FROM AuthUsers WHERE Username=$u"; c.Parameters.AddWithValue("$u", t); return Convert.ToInt32(c.ExecuteScalar()) > 0; } public static bool IsUserBanned(string? u) => string.Equals(GetUserStatus(u), "banned", StringComparison.OrdinalIgnoreCase); public static string GetUserStatus(string? u) { var t = (u ?? "").Trim().ToLowerInvariant(); using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "SELECT Status FROM AuthUsers WHERE Username=$u LIMIT 1"; c.Parameters.AddWithValue("$u", t); return (c.ExecuteScalar() as string)?.Trim().ToLowerInvariant() ?? ""; } public static void LogActivity(string u, string a, string d) { try { using var db = DbHelpers.OpenDb(); using var c = db.CreateCommand(); c.CommandText = "INSERT INTO ActivityLog (Username,Action,Details,Timestamp) VALUES ($u,$a,$d,$t)"; c.Parameters.AddWithValue("$u", u); c.Parameters.AddWithValue("$a", a); c.Parameters.AddWithValue("$d", (d ?? "").Length > 500 ? d![..500] : d ?? ""); c.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o")); c.ExecuteNonQuery(); } catch { } } }
public static class PasswordPolicy { public static (bool Valid, string Message) Validate(string pw) { if (string.IsNullOrWhiteSpace(pw)) return (false, "Lösenord krävs."); if (pw.Length < 8) return (false, "Minst 8 tecken."); if (pw.Length > 128) return (false, "Max 128 tecken."); if (!Regex.IsMatch(pw, @"[A-Z]")) return (false, "Minst en versal."); if (!Regex.IsMatch(pw, @"[a-z]")) return (false, "Minst en gemen."); if (!Regex.IsMatch(pw, @"\d")) return (false, "Minst en siffra."); return (true, ""); } }
public static class ReservedNames { static readonly HashSet<string> R = new(StringComparer.OrdinalIgnoreCase) { "admin", "administrator", "root", "system", "support", "moderator", "runspace", "api", "www", "null", "undefined", "test", "demo", "abuse", "postmaster", "webmaster", "security", "noreply", "bot", "official", "staff", "team" }; public static bool IsReserved(string u) => R.Contains(u); }
public static class ContentFilter
{
    static readonly string[] Bad = {
        "nigger","nigga","n1gger","chink","ch1nk","gook","spic","sp1c",
        "kike","k1ke","coon","c00n","wetback","raghead","towelhead",
        "beaner","jap","j4p","jew","j3w","jewboy",
        "faggot","f4ggot","fagot","dyke","tranny",
        "nazi","n4zi","heil","kkk","whitepower","1488",
        "killblacks","killwhites","killjews",
        "mx403","mxssy","runspace_admin","runspaceadmin","admin_official",
        "official_admin","support_team","runspace_team","runspace_mod",
        "pedophile","pedo","p3do","nonce","rapist",
        "kill yourself","killyourself","kys"
    };
    static string Normalize(string s) => s
        .Replace("0", "o").Replace("1", "i").Replace("3", "e").Replace("4", "a")
        .Replace("5", "s").Replace("@", "a").Replace("$", "s").Replace("!", "i");
    public static bool IsOffensive(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var raw = input.ToLowerInvariant();
        var norm = Normalize(raw);
        return Bad.Any(b => raw.Contains(b) || norm.Contains(b));
    }
}
public static class InputSanitizer { public static string SanitizeInput(string i, int max = 1000) { if (string.IsNullOrEmpty(i)) return ""; var r = i.Trim(); return r.Length > max ? r[..max] : r; } public static string SanitizeOutput(string o) => string.IsNullOrEmpty(o) ? "" : o.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#x27;"); public static string SanitizeUrl(string u) { if (string.IsNullOrEmpty(u)) return ""; var t = u.Trim(); return t.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "" : t; } public static string SanitizeSearchQuery(string q) => Regex.Replace(q, @"[;'""\\]", ""); public static bool IsValidAvatarUrl(string url) { if (string.IsNullOrWhiteSpace(url)) return false; var t = url.Trim(); if (t.StartsWith("/uploads/avatars/")) return true; if (t.StartsWith("https://") && Uri.TryCreate(t, UriKind.Absolute, out _)) return true; return false; } }
public static class FileValidator { static readonly Dictionary<string, byte[][]> Magic = new() { [".png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47 } }, [".jpg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } }, [".jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } }, [".gif"] = new[] { new byte[] { 0x47, 0x49, 0x46, 0x38 } }, [".webp"] = new[] { new byte[] { 0x52, 0x49, 0x46, 0x46 } } }; public static async Task<bool> IsValidImageAsync(IFormFile f) { var ext = Path.GetExtension(DefensiveInput.SafeFileName(f.FileName)).ToLowerInvariant(); if (!Magic.TryGetValue(ext, out var sigs)) return false; using var s = f.OpenReadStream(); var buf = new byte[8]; var read = await s.ReadAsync(buf); return read >= 3 && sigs.Any(sig => buf.Take(sig.Length).SequenceEqual(sig)); } }
public record LoginReq(string Username, string Password, string? CaptchaToken = null, string? Email = null, string? TotpCode = null);
public record NewsCreateReq(string TitleSv, string? TitleEn, string? TitleRu, string? TitleFr, string BodySv, string? BodyEn, string? BodyRu, string? BodyFr, string? Tag, List<string>? ImageUrls = null);
public record ChangeRoleReq(string Role);
public record CreateRoleReq(string Name, string? Color, string? Permissions);
public record UpdateRoleReq(string? Name, string? Color, int? Position, string? Permissions);
public record ReorderRolesReq(List<string> Order);
public record LinkRequestReq(string DeviceId, string? DeviceName);
public record LinkApproveReq(string Code, string EncryptedPrivateKey);
public record ChangeReq(string OldPassword, string NewPassword);
public record ChangeUsernameReq(string NewUsername);
public record CodeExecReq(string Language, string Code, string? MessageId = null);
public record TrustOverrideReq(
    int? Score,          // legacy – ignoreras om nya fält används
    int? MinTrust,       // clamp undre gräns
    int? MaxTrust,       // clamp övre gräns
    bool? ForceBlock,    // tvångsblockera oavsett score
    List<string>? DisableFeatures  // ["files","links","exec","mention"]
);
public record ForgotPasswordReq(string Email);
public record ResendOtpReq(string PendingToken);
public record VerifyOtpReq(string PendingToken, string Code);
public record ProfileReq(string? Bio, string? Nationality, string? Languages, string? Links);
public record AvatarUrlReq(string AvatarUrl);
public record PublicKeyReq(string PublicKey, string? DeviceId = null, string? DeviceName = null);
public record DeviceKeyUpsertReq(string DeviceId, string? DeviceName, string PublicKey);
public record DeviceCipherEntry(string DeviceId, string EncryptedKey);
public record TicketStatusReq(string Status);
public record TwoFactorVerifyReq(string Code);
public record FreezeReq(int Hours);
public record ReactionReq(long MessageId, string Emoji);
public record ChatMessageReq(string To, string Text, string Iv, string EncryptedKey, string SenderEncryptedKey, string Algorithm, int Encrypted, List<DeviceCipherEntry>? RecipientKeys = null, List<DeviceCipherEntry>? SenderKeys = null, long? ReplyToId = null);
public record OutgoingValidationResult(bool Ok, string Message, int StatusCode, string ToUser, string CipherText, string Iv, string EncryptedKey, string SenderEncryptedKey, string Algorithm, bool Encrypted, string RecipientKeysJson, string SenderKeysJson) { public static OutgoingValidationResult Fail(string m, int s) => new(false, m, s, "", "", "", "", "", "plain", false, "[]", "[]"); public static OutgoingValidationResult Success(string to, string ct, string iv, string ek, string sek, string alg, bool enc, string rk, string sk) => new(true, "", 200, to, ct, iv, ek, sek, alg, enc, rk, sk); }
public record CreateGroupReq(string Name, string? Description = null);
public record UpdateGroupReq(string? Name = null, string? Description = null);
public record CreateChannelReq(string Name, string? Type = "text");
public record InviteReq(string Username);
public record GroupMessageReq(string Text);
public record UserReportReq(string ReportedUser, string Reason, string? Details, long MessageId);
public record AiChatReq(List<AiMessage> Messages);
public record AiMessage(string Role, string Content);
public record EmailCodeReq(string Email);
public record EmailVerifyReq(string Email, string Code);
public static class OtpCache
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Code, DateTime Expires)> _cache = new();
    public static void Set(string key, string code, TimeSpan ttl) => _cache[key] = (code, DateTime.UtcNow + ttl);
    public static bool TryGet(string key, out string code)
    {
        code = "";
        if (!_cache.TryGetValue(key, out var entry)) return false;
        if (entry.Expires < DateTime.UtcNow) { _cache.TryRemove(key, out _); return false; }
        code = entry.Code; return true;
    }
    public static void Remove(string key) => _cache.TryRemove(key, out _);
}

public static class OtpGenerator
{
    public static string GenerateCode()
    {
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return (Math.Abs(BitConverter.ToInt32(bytes)) % 1_000_000).ToString("D6");
    }
}
public static class TrustScoreService
{
    public static int GetTrustScore(int? overrideScore, DateTime createdAt)
    {
        if (overrideScore.HasValue) return Math.Clamp(overrideScore.Value, 0, 100);
        var days = (DateTime.UtcNow - createdAt).TotalDays;
        return (int)Math.Clamp(70.0 + (30.0 * Math.Min(days, 90.0) / 90.0), 70, 100);
    }
}

public class SmtpMailService
{
    public static async Task SendOtpAsync(string toEmail, string toUsername, string code, string action)
    {

        var apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY") ?? "";
        var actionText = action == "verify" ? "verify your new account" : action == "reset" ? "reset your password" : "complete your sign in";
        var subject = action == "welcome" ? "Welcome to RunSpace!" : $"Your RunSpace code: {code}";
        var html = $"<!DOCTYPE html><html><body style='margin:0;padding:0;background:#07070d;font-family:sans-serif'><div style='max-width:480px;margin:40px auto;background:#0e0e18;border:1px solid #ffffff18;border-radius:16px;overflow:hidden'><div style='background:#4f6ef7;padding:24px;text-align:center'><div style='font-size:22px;font-weight:700;color:white'>RunSpace</div></div><div style='padding:32px 28px'><p style='color:#dde0f0;font-size:15px;margin:0 0 8px'>Hi {toUsername},</p><p style='color:#787b99;font-size:13px;margin:0 0 28px'>Use the code below to {actionText}:</p><div style='background:#13131e;border:1px solid #ffffff18;border-radius:12px;padding:24px;text-align:center;margin-bottom:28px'><div style='font-family:monospace;font-size:36px;font-weight:700;color:#ffffff;letter-spacing:8px'>{code}</div><div style='color:#787b99;font-size:12px;margin-top:10px'>Expires in 15 minutes</div></div><p style='color:#4e5068;font-size:12px;margin:0'>If you did not request this, ignore this email.</p></div></div></body></html>";

        var resendApiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY");
        if (!string.IsNullOrWhiteSpace(resendApiKey))
        {
            var resendFrom = Environment.GetEnvironmentVariable("RESEND_FROM");
            if (string.IsNullOrWhiteSpace(resendFrom))
                resendFrom = "RunSpace Support <support@runspace.cloud>";

            using var resendHttp = new System.Net.Http.HttpClient();
            resendHttp.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", resendApiKey);
            resendHttp.DefaultRequestHeaders.UserAgent.ParseAdd("RunSpace/1.0");

            var resendPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                from = resendFrom,
                to = new[] { toEmail },
                subject = subject,
                html = html
            });

            using var resendContent = new System.Net.Http.StringContent(
                resendPayload,
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var resendResponse = await resendHttp.PostAsync("https://api.resend.com/emails", resendContent);

            Console.WriteLine($"[RESEND] Status: {(int)resendResponse.StatusCode} {resendResponse.StatusCode}");

            if (!resendResponse.IsSuccessStatusCode)
            {
                Console.WriteLine("[RESEND] send failed.");
                throw new Exception("Resend send failed: " + resendResponse.StatusCode);
            }

            return;
        }
        var client = new SendGrid.SendGridClient(apiKey);
        var from = new SendGrid.Helpers.Mail.EmailAddress("support@runspace.cloud", "RunSpace");
        var to = new SendGrid.Helpers.Mail.EmailAddress(toEmail, toUsername);
        var msg = SendGrid.Helpers.Mail.MailHelper.CreateSingleEmail(from, to, subject, "", html);
        var response = await client.SendEmailAsync(msg);
        Console.WriteLine($"[SENDGRID] Status: {(int)response.StatusCode}");
    }

    public static async Task SendKeyFileAsync(string toEmail, string toUsername, byte[] keyBytes, string accountKey)
    {
        var apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY") ?? "";
        var html = $"<html><body style='background:#07070d;font-family:sans-serif'><div style='max-width:480px;margin:40px auto;background:#0e0e18;border:1px solid #ffffff18;border-radius:16px;overflow:hidden'><div style='background:#4f6ef7;padding:24px;text-align:center'><div style='font-size:22px;font-weight:700;color:white'>RunSpace</div></div><div style='padding:32px 28px'><p style='color:#dde0f0'>Hi {toUsername},</p><p style='color:#787b99'>Your new key file is attached. Your account key (UUID): <span style='color:#4f6ef7;font-family:monospace'>{accountKey}</span></p><p style='color:#787b99;font-size:12px'>Upload the .key file at runspace.cloud/unlock with your passphrase.</p></div></div></body></html>";
        var client = new SendGrid.SendGridClient(apiKey);
        var from = new SendGrid.Helpers.Mail.EmailAddress("support@runspace.cloud", "RunSpace");
        var to = new SendGrid.Helpers.Mail.EmailAddress(toEmail, toUsername);
        var msg = SendGrid.Helpers.Mail.MailHelper.CreateSingleEmail(from, to, "Your new RunSpace key file", "", html);
        var attachment = new SendGrid.Helpers.Mail.Attachment
        {
            Content = Convert.ToBase64String(keyBytes),
            Filename = $"{toUsername}.key",
            Type = "application/octet-stream",
            Disposition = "attachment"
        };
        msg.AddAttachment(attachment);
        await client.SendEmailAsync(msg);
    }

}

// ── Market Records ──
public record MarketPurchaseReq(long ScriptId);
public record MarketRejectReq(string? Reason);
public record MarketReviewReq(int Rating, string? ReviewText);

public record RegisterEmailReq(string Email);
public record RegisterWithKeyReq(string Username, string AccountKey, string? PendingToken = null, object? Signals = null);
public record LoginWithKeyReq(string AccountKey);
public record KeyResetReq(string Username, string Email, string Reason);

public record KeyResetApproveReq(long RequestId, string Passphrase);
public record KeyResetRejectReq(long RequestId, string Reason);
