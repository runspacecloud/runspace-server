using Microsoft.AspNetCore.Http;

public sealed class RequestGuardMiddleware
{
    private readonly RequestDelegate _next;

    private const int MaxPathLength = 2048;
    private const int MaxQueryLength = 4096;
    private const int MaxHeaderCount = 80;
    private const int MaxHeaderValueLength = 8192;
    private const int MaxPathSegmentLength = 160;
    private const int MaxQueryParameterCount = 80;
    private const int MaxQueryKeyLength = 80;
    private const int MaxQueryValueLength = 2048;

    private const long MaxJsonBodyBytes = 1_048_576;
    private const long MaxFormBodyBytes = 2_097_152;
    private const long MaxMultipartBodyBytes = 25_165_824;

    public RequestGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    private static bool IsSafeApiRouteSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        if (segment is "api") return true;

        foreach (var c in segment)
        {
            var ok =
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '-' ||
                c == '_' ||
                c == '.' ||
                c == ':';

            if (!ok) return false;
        }

        return true;
    }


    private static bool IsSegment(string[] parts, int index, string value)
    {
        return parts.Length > index && parts[index].Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ApplySemanticApiRouteGuards(HttpContext ctx, string path)
    {
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            return true;

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // /api/spaces/{publicId}/...
        if (parts.Length >= 3 && IsSegment(parts, 0, "api") && IsSegment(parts, 1, "spaces"))
        {
            var publicId = parts[2];
            if (!DefensiveInput.IsSafePublicId(publicId))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid publicId." });
                return false;
            }
        }

        // /api/invite/{code} and /api/invite/{code}/join
        if (parts.Length >= 3 && IsSegment(parts, 0, "api") && IsSegment(parts, 1, "invite"))
        {
            var code = parts[2];
            if (!DefensiveInput.IsSafeInviteCode(code))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid invite code." });
                return false;
            }
        }

        // /api/servers/{sid}/...
        if (parts.Length >= 3 && IsSegment(parts, 0, "api") && IsSegment(parts, 1, "servers"))
        {
            var sid = parts[2];
            if (!DefensiveInput.IsSafeRouteId(sid))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid server id." });
                return false;
            }
        }

        // /api/channels/{cid}/...
        if (parts.Length >= 3 && IsSegment(parts, 0, "api") && IsSegment(parts, 1, "channels"))
        {
            var cid = parts[2];
            if (!DefensiveInput.IsSafeRouteId(cid))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid channel id." });
                return false;
            }
        }

        // /api/groups/{groupId}/...
        if (parts.Length >= 3 && IsSegment(parts, 0, "api") && IsSegment(parts, 1, "groups") && !IsSegment(parts, 2, "invites"))
        {
            var groupId = parts[2];
            if (!DefensiveInput.IsSafeRouteId(groupId))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid group id." });
                return false;
            }
        }


        // Username / target username guards
        bool badUsername(string value) => !DefensiveInput.IsUsername(value);

        // /api/profile/public/{username}
        // /api/profile/follow/{username}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "profile") &&
            (IsSegment(parts, 2, "public") || IsSegment(parts, 2, "follow")))
        {
            if (badUsername(parts[3]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid username route parameter." });
                return false;
            }
        }

        // /api/e2ee/account-public-key/{username}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "e2ee") &&
            IsSegment(parts, 2, "account-public-key"))
        {
            if (badUsername(parts[3]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid username route parameter." });
                return false;
            }
        }

        // /api/chat/public-key/{username}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "chat") &&
            IsSegment(parts, 2, "public-key"))
        {
            if (badUsername(parts[3]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid username route parameter." });
                return false;
            }
        }

        // /api/encryption/public-key/{username}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "encryption") &&
            IsSegment(parts, 2, "public-key"))
        {
            if (badUsername(parts[3]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid username route parameter." });
                return false;
            }
        }

        // /api/admin/users/{username}/...
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "admin") &&
            IsSegment(parts, 2, "users"))
        {
            if (badUsername(parts[3]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid username route parameter." });
                return false;
            }
        }

        // /api/admin/security/user/{username}
        if (parts.Length >= 5 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "admin") &&
            IsSegment(parts, 2, "security") &&
            IsSegment(parts, 3, "user"))
        {
            if (badUsername(parts[4]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid username route parameter." });
                return false;
            }
        }

        // /api/spaces/{publicId}/kick/{targetUsername}
        if (parts.Length >= 5 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "spaces") &&
            IsSegment(parts, 3, "kick"))
        {
            if (badUsername(parts[4]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid target username." });
                return false;
            }
        }

        // /api/spaces/{publicId}/members/{targetUsername}/role
        if (parts.Length >= 6 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "spaces") &&
            IsSegment(parts, 3, "members") &&
            IsSegment(parts, 5, "role"))
        {
            if (badUsername(parts[4]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid target username." });
                return false;
            }
        }



        bool badPositiveLong(string value)
        {
            return !long.TryParse(value, out var id) || !DefensiveInput.IsPositiveId(id);
        }

        async Task<bool> rejectNumeric()
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { message = "Invalid numeric id route parameter." });
            return false;
        }

        // /api/chat/reactions/{messageId}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "chat") &&
            IsSegment(parts, 2, "reactions") &&
            badPositiveLong(parts[3]))
            return await rejectNumeric();

        // /api/chat/message/{id}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "chat") &&
            IsSegment(parts, 2, "message") &&
            badPositiveLong(parts[3]))
            return await rejectNumeric();

        // /api/messages/{messageId}/...
        if (parts.Length >= 3 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "messages") &&
            badPositiveLong(parts[2]))
            return await rejectNumeric();

        // /api/news/{id}
        if (parts.Length >= 3 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "news") &&
            badPositiveLong(parts[2]))
            return await rejectNumeric();

        // /api/sponsors/{id}
        if (parts.Length >= 3 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "sponsors") &&
            badPositiveLong(parts[2]))
            return await rejectNumeric();

        // /api/market/.../{id}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "market") &&
            (
                (IsSegment(parts, 2, "scripts") && badPositiveLong(parts[3])) ||
                (IsSegment(parts, 2, "download") && badPositiveLong(parts[3])) ||
                (IsSegment(parts, 2, "reviews") && badPositiveLong(parts[3]))
            ))
            return await rejectNumeric();

        // /api/market/admin/approve/{id} and /reject/{id}
        if (parts.Length >= 5 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "market") &&
            IsSegment(parts, 2, "admin") &&
            (IsSegment(parts, 3, "approve") || IsSegment(parts, 3, "reject")) &&
            badPositiveLong(parts[4]))
            return await rejectNumeric();

        // /api/developer/tokens/{id} and /api/developer/webhooks/{id}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "developer") &&
            (IsSegment(parts, 2, "tokens") || IsSegment(parts, 2, "webhooks")) &&
            badPositiveLong(parts[3]))
            return await rejectNumeric();

        // /api/music/playlists/{id}/... and /api/music/tracks/{id}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "music") &&
            (IsSegment(parts, 2, "playlists") || IsSegment(parts, 2, "tracks")) &&
            badPositiveLong(parts[3]))
            return await rejectNumeric();

        // /api/spaces/{publicId}/channels/{channelId}/...
        if (parts.Length >= 5 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "spaces") &&
            IsSegment(parts, 3, "channels") &&
            badPositiveLong(parts[4]))
            return await rejectNumeric();



        // /api/auth/sessions/{sessionId}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "auth") &&
            IsSegment(parts, 2, "sessions"))
        {
            if (!DefensiveInput.IsSafeRouteId(parts[3], 256))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid session id." });
                return false;
            }
        }

        // /api/me/devices/{deviceId}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "me") &&
            IsSegment(parts, 2, "devices"))
        {
            if (!DefensiveInput.IsSafeRouteId(parts[3], 256))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid device id." });
                return false;
            }
        }

        // /api/chat/device-key/{deviceId}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "chat") &&
            IsSegment(parts, 2, "device-key"))
        {
            if (!DefensiveInput.IsSafeRouteId(parts[3], 256))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid device id." });
                return false;
            }
        }

        // /api/chat/link-status/{code}
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "chat") &&
            IsSegment(parts, 2, "link-status"))
        {
            if (!DefensiveInput.IsSafeRouteId(parts[3], 120))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid link code." });
                return false;
            }
        }

        // /api/support/tickets/{ticketId}
        // /api/support/tickets/{ticketId}/reply
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "support") &&
            IsSegment(parts, 2, "tickets"))
        {
            if (!DefensiveInput.IsSafeTicketId(parts[3]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid support ticket id." });
                return false;
            }
        }

        // /api/support/live-call/{ticketId}/status
        if (parts.Length >= 4 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "support") &&
            IsSegment(parts, 2, "live-call"))
        {
            if (!DefensiveInput.IsSafeTicketId(parts[3]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid support ticket id." });
                return false;
            }
        }

        // /api/admin/support/tickets/{ticketId}/...
        if (parts.Length >= 5 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "admin") &&
            IsSegment(parts, 2, "support") &&
            IsSegment(parts, 3, "tickets"))
        {
            if (!DefensiveInput.IsSafeTicketId(parts[4]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid support ticket id." });
                return false;
            }
        }

        // /api/admin/support/ticket/{ticketId}/status
        if (parts.Length >= 5 &&
            IsSegment(parts, 0, "api") &&
            IsSegment(parts, 1, "admin") &&
            IsSegment(parts, 2, "support") &&
            IsSegment(parts, 3, "ticket"))
        {
            if (!DefensiveInput.IsSafeTicketId(parts[4]))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid support ticket id." });
                return false;
            }
        }


        return true;
    }

    public async Task Invoke(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var query = ctx.Request.QueryString.Value ?? "";

        if (path.Length > MaxPathLength)
        {
            ctx.Response.StatusCode = StatusCodes.Status414UriTooLong;
            return;
        }

        if (query.Length > MaxQueryLength)
        {
            ctx.Response.StatusCode = StatusCodes.Status414UriTooLong;
            return;
        }

        var lowerPath = path.ToLowerInvariant();
        if (path.Contains('\\') ||
            lowerPath.Contains("%00") ||
            lowerPath.Contains("../") ||
            lowerPath.Contains("..%2f") ||
            lowerPath.Contains("%2e%2e") ||
            lowerPath.Contains("%5c"))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { message = "Invalid path." });
            return;
        }

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length > MaxPathSegmentLength)
            {
                ctx.Response.StatusCode = StatusCodes.Status414UriTooLong;
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && !IsSafeApiRouteSegment(segment))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Invalid route segment." });
                return;
            }
        }

        if (ctx.Request.Query.Count > MaxQueryParameterCount)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { message = "Too many query parameters." });
            return;
        }

        foreach (var q in ctx.Request.Query)
        {
            if (q.Key.Length > MaxQueryKeyLength || q.Value.ToString().Length > MaxQueryValueLength)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Query parameter too large." });
                return;
            }
        }

        if (!await ApplySemanticApiRouteGuards(ctx, path))
            return;

        if (ctx.Request.Headers.Count > MaxHeaderCount)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { message = "Too many headers." });
            return;
        }

        foreach (var h in ctx.Request.Headers)
        {
            if (h.Value.ToString().Length > MaxHeaderValueLength)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { message = "Header too large." });
                return;
            }
        }

        if (ctx.Request.ContentLength is long len && len > 0)
        {
            var ct = ctx.Request.ContentType ?? "";
            var isApi = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);

            var allowedBodyType =
                ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
                ct.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
                ct.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
                ct.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);

            if (isApi && !allowedBodyType)
            {
                ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                await ctx.Response.WriteAsJsonAsync(new { message = "Unsupported content type." });
                return;
            }

            var max =
                ct.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) ? MaxMultipartBodyBytes :
                ct.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ? MaxFormBodyBytes :
                ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ? MaxJsonBodyBytes :
                ct.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ? MaxJsonBodyBytes :
                MaxJsonBodyBytes;

            if (len > max)
            {
                ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await ctx.Response.WriteAsJsonAsync(new { message = "Request body too large." });
                return;
            }
        }

        await _next(ctx);
    }
}
