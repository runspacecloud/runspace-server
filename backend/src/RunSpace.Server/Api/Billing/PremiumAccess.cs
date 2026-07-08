using Microsoft.Data.Sqlite;

public sealed class PremiumState
{
    public string Plan { get; set; } = "free";
    public string Status { get; set; } = "inactive";
    public string CurrentPeriodEnd { get; set; } = "";
    public bool IsPremium { get; set; }
    public bool IsPlus { get; set; }
    public Dictionary<string, bool> Features { get; set; } = new();
}

public static class PremiumAccess
{
    public static PremiumState GetForUsername(string? username)
    {
        var state = new PremiumState();

        if (string.IsNullOrWhiteSpace(username))
        {
            ApplyFeatures(state);
            return state;
        }

        using var db = DbHelpers.OpenDb();
        using var cmd = db.CreateCommand();

        cmd.CommandText = @"
SELECT Plan, Status, CurrentPeriodEnd
FROM UserSubscriptions
WHERE LOWER(Username)=LOWER($u)
LIMIT 1";
        cmd.Parameters.AddWithValue("$u", username.Trim());

        using var r = cmd.ExecuteReader();

        if (!r.Read())
        {
            ApplyFeatures(state);
            return state;
        }

        var plan = r.IsDBNull(0) ? "free" : r.GetString(0).Trim().ToLowerInvariant();
        var status = r.IsDBNull(1) ? "inactive" : r.GetString(1).Trim().ToLowerInvariant();
        var currentPeriodEnd = r.IsDBNull(2) ? "" : r.GetString(2);

        var activeStatus = status is "active" or "trialing";
        var paidPlan = plan is "premium" or "plus";

        var notExpired = true;
        if (DateTime.TryParse(currentPeriodEnd, out var periodEnd))
            notExpired = periodEnd.ToUniversalTime() > DateTime.UtcNow;

        state.Plan = activeStatus && paidPlan && notExpired ? plan : "free";
        state.Status = status;
        state.CurrentPeriodEnd = currentPeriodEnd;
        state.IsPremium = state.Plan is "premium" or "plus";
        state.IsPlus = state.Plan == "plus";

        ApplyFeatures(state);
        return state;
    }

    public static bool HasFeature(string? username, string feature)
    {
        var state = GetForUsername(username);

        return state.Features.TryGetValue(feature, out var allowed) && allowed;
    }

    private static void ApplyFeatures(PremiumState state)
    {
        var premium = state.IsPremium;
        var plus = state.IsPlus;

        state.Features = new Dictionary<string, bool>
        {
            ["profile_banner"] = premium,
            ["profile_badge"] = premium,
            ["advanced_stats"] = premium,
            ["custom_theme"] = premium,
            ["early_access"] = premium,

            ["larger_uploads"] = plus,
            ["priority_support"] = plus,
            ["premium_plus_badge"] = plus,
            ["profile_boost"] = plus,
            ["beta_features"] = plus
        };
    }
}
