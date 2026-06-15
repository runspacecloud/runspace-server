using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Stripe;
using Stripe.Checkout;

public static class BillingEndpoints
{
    public static void EnsureBillingTables()
    {
        using var db = DbHelpers.OpenDb();

        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS UserSubscriptions (
  UserId INTEGER NOT NULL UNIQUE,
  Username TEXT NOT NULL,
  StripeCustomerId TEXT,
  StripeSubscriptionId TEXT,
  Plan TEXT NOT NULL DEFAULT 'free',
  Status TEXT NOT NULL DEFAULT 'inactive',
  CurrentPeriodEnd TEXT,
  UpdatedAt TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_UserSubscriptions_Customer
ON UserSubscriptions(StripeCustomerId);

CREATE INDEX IF NOT EXISTS IX_UserSubscriptions_Subscription
ON UserSubscriptions(StripeSubscriptionId);
";
        cmd.ExecuteNonQuery();
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/billing/status", (Func<HttpContext, IResult>)(ctx => GetStatus(ctx)));

        app.MapPost("/api/billing/checkout", (Func<HttpContext, Task<IResult>>)(async ctx =>
        {
            if (!IsLoggedIn(ctx))
                return Results.Unauthorized();

            return await CreateCheckout(ctx);
        }));

        app.MapPost("/api/billing/portal", (Func<HttpContext, Task<IResult>>)(async ctx =>
        {
            if (!IsLoggedIn(ctx))
                return Results.Unauthorized();

            return await CreatePortal(ctx);
        }));

        app.MapPost("/api/billing/webhook", (Func<HttpContext, Task<IResult>>)(async ctx =>
        {
            return await HandleWebhook(ctx);
        }));
    }

    private static bool IsLoggedIn(HttpContext ctx)
    {
        var hasAuthCookie =
            ctx.Request.Cookies.ContainsKey("runspace_auth") ||
            ctx.Request.Cookies.ContainsKey(".AspNetCore.Cookies");

        return hasAuthCookie && ctx.User?.Identity?.IsAuthenticated == true;
    }

    private static string? CurrentUsername(HttpContext ctx)
    {
        return ctx.User.FindFirst(ClaimTypes.Name)?.Value
            ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? ctx.User.Identity?.Name;
    }

    private static long GetUserIdByUsername(string username)
    {
        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();

        cmd.CommandText = "SELECT Id FROM AuthUsers WHERE LOWER(Username)=LOWER($u) LIMIT 1";
        cmd.Parameters.AddWithValue("$u", username);

        var result = cmd.ExecuteScalar();

        if (result == null || result == DBNull.Value)
            throw new Exception("User not found");

        return Convert.ToInt64(result);
    }

    private static string PublicUrl()
    {
        return Environment.GetEnvironmentVariable("RUNSPACE_PUBLIC_URL")
            ?? "https://runspace.cloud";
    }

    private static string StripeSecret()
    {
        var key = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY");

        if (string.IsNullOrWhiteSpace(key))
            throw new Exception("STRIPE_SECRET_KEY missing");

        return key;
    }

    private static string PriceIdFor(string plan, string billing)
    {
        plan = (plan ?? "").Trim().ToLowerInvariant();
        billing = (billing ?? "").Trim().ToLowerInvariant();

        var envName = (plan, billing) switch
        {
            ("premium", "monthly") => "STRIPE_PREMIUM_MONTHLY_PRICE_ID",
            ("premium", "yearly") => "STRIPE_PREMIUM_YEARLY_PRICE_ID",
            ("plus", "monthly") => "STRIPE_PREMIUM_PLUS_MONTHLY_PRICE_ID",
            ("plus", "yearly") => "STRIPE_PREMIUM_PLUS_YEARLY_PRICE_ID",
            ("premiumplus", "monthly") => "STRIPE_PREMIUM_PLUS_MONTHLY_PRICE_ID",
            ("premiumplus", "yearly") => "STRIPE_PREMIUM_PLUS_YEARLY_PRICE_ID",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(envName))
            throw new Exception("Invalid plan or billing period");

        var priceId = Environment.GetEnvironmentVariable(envName);

        if (string.IsNullOrWhiteSpace(priceId))
            throw new Exception($"{envName} missing");

        return priceId;
    }

    private static string PlanFromPriceId(string? priceId)
    {
        if (string.IsNullOrWhiteSpace(priceId))
            return "unknown";

        if (priceId == Environment.GetEnvironmentVariable("STRIPE_PREMIUM_MONTHLY_PRICE_ID"))
            return "premium";

        if (priceId == Environment.GetEnvironmentVariable("STRIPE_PREMIUM_YEARLY_PRICE_ID"))
            return "premium";

        if (priceId == Environment.GetEnvironmentVariable("STRIPE_PREMIUM_PLUS_MONTHLY_PRICE_ID"))
            return "plus";

        if (priceId == Environment.GetEnvironmentVariable("STRIPE_PREMIUM_PLUS_YEARLY_PRICE_ID"))
            return "plus";

        return "unknown";
    }

    private static string? GetCustomerId(long userId)
    {
        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();

        cmd.CommandText = "SELECT StripeCustomerId FROM UserSubscriptions WHERE UserId=$uid LIMIT 1";
        cmd.Parameters.AddWithValue("$uid", userId);

        return cmd.ExecuteScalar() as string;
    }

    private static IResult GetStatus(HttpContext ctx)
    {
        if (!IsLoggedIn(ctx))
            return Results.Unauthorized();

        var username = CurrentUsername(ctx);

        if (string.IsNullOrWhiteSpace(username))
            return Results.Unauthorized();

        var userId = GetUserIdByUsername(username);

        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();

        cmd.CommandText = @"
SELECT Plan, Status, CurrentPeriodEnd
FROM UserSubscriptions
WHERE UserId=$uid
LIMIT 1";
        cmd.Parameters.AddWithValue("$uid", userId);

        using var r = cmd.ExecuteReader();

        if (!r.Read())
        {
            return Results.Ok(new
            {
                plan = "free",
                status = "inactive",
                isPremium = false,
                currentPeriodEnd = ""
            });
        }

        var plan = r.IsDBNull(0) ? "free" : r.GetString(0);
        var status = r.IsDBNull(1) ? "inactive" : r.GetString(1);
        var currentPeriodEnd = r.IsDBNull(2) ? "" : r.GetString(2);

        var activeStatus = status is "active" or "trialing";
        var paidPlan = plan is "premium" or "plus";
        var notExpired = true;

        if (DateTime.TryParse(currentPeriodEnd, out var periodEnd))
            notExpired = periodEnd.ToUniversalTime() > DateTime.UtcNow;

        return Results.Ok(new
        {
            plan,
            status,
            isPremium = activeStatus && paidPlan && notExpired,
            currentPeriodEnd
        });
    }

    private static async Task<IResult> CreateCheckout(HttpContext ctx)
    {
        if (!IsLoggedIn(ctx))
            return Results.Unauthorized();

        var username = CurrentUsername(ctx);

        if (string.IsNullOrWhiteSpace(username))
            return Results.Unauthorized();

        var req = await ctx.Request.ReadFromJsonAsync<CheckoutRequest>() ?? new CheckoutRequest();

        var plan = (req.Plan ?? "").Trim().ToLowerInvariant();
        var billing = (req.Billing ?? "monthly").Trim().ToLowerInvariant();

        if (plan == "premium+")
            plan = "plus";

        if (plan is not ("premium" or "plus"))
            return Results.BadRequest(new { error = "Invalid plan" });

        if (billing is not ("monthly" or "yearly"))
            return Results.BadRequest(new { error = "Invalid billing" });

        var userId = GetUserIdByUsername(username);
        var priceId = PriceIdFor(plan, billing);

        StripeConfiguration.ApiKey = StripeSecret();

        var metadata = new Dictionary<string, string>
        {
            ["runspace_user_id"] = userId.ToString(),
            ["runspace_username"] = username,
            ["runspace_plan"] = plan,
            ["runspace_billing"] = billing
        };

        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            Locale = "en",
            SuccessUrl = $"{PublicUrl().TrimEnd('/')}/premium/?success=1",
            CancelUrl = $"{PublicUrl().TrimEnd('/')}/premium/?canceled=1",
            ClientReferenceId = userId.ToString(),
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = metadata
            },
            LineItems = new List<SessionLineItemOptions>
            {
                new()
                {
                    Price = priceId,
                    Quantity = 1
                }
            }
        };

        var existingCustomerId = GetCustomerId(userId);

        if (!string.IsNullOrWhiteSpace(existingCustomerId))
            options.Customer = existingCustomerId;

        var service = new SessionService();
        var session = await service.CreateAsync(options);

        return Results.Ok(new { url = session.Url });
    }

    private static async Task<IResult> CreatePortal(HttpContext ctx)
    {
        if (!IsLoggedIn(ctx))
            return Results.Unauthorized();

        var username = CurrentUsername(ctx);

        if (string.IsNullOrWhiteSpace(username))
            return Results.Unauthorized();

        var userId = GetUserIdByUsername(username);
        var customerId = GetCustomerId(userId);

        if (string.IsNullOrWhiteSpace(customerId))
            return Results.BadRequest(new { error = "No Stripe customer found" });

        StripeConfiguration.ApiKey = StripeSecret();

        var options = new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = $"{PublicUrl().TrimEnd('/')}/premium/"
        };

        var service = new Stripe.BillingPortal.SessionService();
        var session = await service.CreateAsync(options);

        return Results.Ok(new { url = session.Url });
    }

    private static async Task<IResult> HandleWebhook(HttpContext ctx)
    {
        var webhookSecret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET");

        if (string.IsNullOrWhiteSpace(webhookSecret))
            return Results.BadRequest(new { error = "STRIPE_WEBHOOK_SECRET missing" });

        var json = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
        var signature = ctx.Request.Headers["Stripe-Signature"].ToString();

        Stripe.Event stripeEvent;

        try
        {
            stripeEvent = EventUtility.ConstructEvent(json, signature, webhookSecret);
        }
        catch
        {
            return Results.BadRequest(new { error = "Invalid Stripe signature" });
        }

        if (stripeEvent.Type == "checkout.session.completed")
        {
            var session = stripeEvent.Data.Object as Session;

            if (session != null && session.Mode == "subscription")
            {
                if (long.TryParse(session.ClientReferenceId, out var userId))
                {
                    var username = session.Metadata != null && session.Metadata.TryGetValue("runspace_username", out var u)
                        ? u
                        : "";

                    var plan = session.Metadata != null && session.Metadata.TryGetValue("runspace_plan", out var p)
                        ? p
                        : "premium";

                    UpsertSubscription(
                        userId,
                        username,
                        session.CustomerId,
                        session.SubscriptionId,
                        plan,
                        "active",
                        ""
                    );
                }
            }
        }
        else if (stripeEvent.Type == "customer.subscription.updated" ||
                 stripeEvent.Type == "customer.subscription.deleted")
        {
            var subscription = stripeEvent.Data.Object as Subscription;

            if (subscription != null)
            {
                var priceId = subscription.Items?.Data?.FirstOrDefault()?.Price?.Id;
                var plan = PlanFromPriceId(priceId);

                var status = subscription.Status ?? "inactive";
                var customerId = subscription.CustomerId ?? "";
                var subscriptionId = subscription.Id ?? "";

                var userIdText = subscription.Metadata != null && subscription.Metadata.TryGetValue("runspace_user_id", out var uid)
                    ? uid
                    : "";

                var username = subscription.Metadata != null && subscription.Metadata.TryGetValue("runspace_username", out var un)
                    ? un
                    : "";

                if (long.TryParse(userIdText, out var userId))
                {
                    var periodEndDate =
                        TryGetStripeDate(subscription, "CurrentPeriodEnd")
                        ?? TryGetStripeDate(subscription, "EndedAt")
                        ?? TryGetStripeDate(subscription, "CanceledAt");

                    var periodEnd = periodEndDate.HasValue
                        ? periodEndDate.Value.ToUniversalTime().ToString("o")
                        : "";

                    UpsertSubscription(
                        userId,
                        username,
                        customerId,
                        subscriptionId,
                        plan,
                        status,
                        periodEnd
                    );
                }
            }
        }

        return Results.Ok(new { received = true });
    }


    private static DateTime? TryGetStripeDate(object obj, string propertyName)
    {
        var prop = obj.GetType().GetProperty(propertyName);

        if (prop == null)
            return null;

        var value = prop.GetValue(obj);

        if (value is DateTime dt)
            return dt;

        if (value is DateTimeOffset dto)
            return dto.UtcDateTime;

        return null;
    }

    private static void UpsertSubscription(
        long userId,
        string username,
        string? customerId,
        string? subscriptionId,
        string plan,
        string status,
        string? currentPeriodEnd)
    {
        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();

        cmd.CommandText = @"
INSERT INTO UserSubscriptions
(UserId, Username, StripeCustomerId, StripeSubscriptionId, Plan, Status, CurrentPeriodEnd, UpdatedAt)
VALUES
($uid, $username, $customer, $subscription, $plan, $status, $periodEnd, $updatedAt)
ON CONFLICT(UserId) DO UPDATE SET
  Username=excluded.Username,
  StripeCustomerId=excluded.StripeCustomerId,
  StripeSubscriptionId=excluded.StripeSubscriptionId,
  Plan=excluded.Plan,
  Status=excluded.Status,
  CurrentPeriodEnd=excluded.CurrentPeriodEnd,
  UpdatedAt=excluded.UpdatedAt;
";

        cmd.Parameters.AddWithValue("$uid", userId);
        cmd.Parameters.AddWithValue("$username", username ?? "");
        cmd.Parameters.AddWithValue("$customer", customerId ?? "");
        cmd.Parameters.AddWithValue("$subscription", subscriptionId ?? "");
        cmd.Parameters.AddWithValue("$plan", plan ?? "unknown");
        cmd.Parameters.AddWithValue("$status", status ?? "inactive");
        cmd.Parameters.AddWithValue("$periodEnd", currentPeriodEnd ?? "");
        cmd.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o"));

        cmd.ExecuteNonQuery();
    }

    public sealed class CheckoutRequest
    {
        public string? Plan { get; set; }
        public string? Billing { get; set; }
    }
}
