using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static class PremiumFeatureEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/premium/features", (Func<HttpContext, IResult>)(ctx =>
        {
            if (!IsLoggedIn(ctx))
                return Results.Unauthorized();

            var username =
                ctx.User.FindFirst(ClaimTypes.Name)?.Value
                ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? ctx.User.Identity?.Name;

            if (string.IsNullOrWhiteSpace(username))
                return Results.Unauthorized();

            var state = PremiumAccess.GetForUsername(username);

            return Results.Ok(new
            {
                username,
                plan = state.Plan,
                status = state.Status,
                isPremium = state.IsPremium,
                isPlus = state.IsPlus,
                currentPeriodEnd = state.CurrentPeriodEnd,
                features = state.Features
            });
        }));
    }

    private static bool IsLoggedIn(HttpContext ctx)
    {
        var hasAuthCookie =
            ctx.Request.Cookies.ContainsKey("runspace_auth_v3") ||
            ctx.Request.Cookies.ContainsKey(".AspNetCore.Cookies");

        return hasAuthCookie && ctx.User?.Identity?.IsAuthenticated == true;
    }
}
