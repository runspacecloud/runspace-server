using System;
using System.Collections.Generic;

public enum TrustLevel { Blocked, Low, Medium, High }

public class TrustDimensions
{
    public double Identity { get; set; }
    public double Behavior { get; set; }
    public double Device   { get; set; }
}

public static class TrustEngine
{
    public static double CompositeScore(TrustDimensions d)
    {
        double raw = (d.Identity * 0.30) + (d.Behavior * 0.50) + (d.Device * 0.20);
        double deviceCap = d.Device * 3.0;
        return Math.Min(raw, deviceCap);
    }

    public static TrustLevel GetLevel(double composite, TrustLevel current)
    {
        return current switch
        {
            TrustLevel.Blocked  => composite >= 30 ? TrustLevel.Low    : TrustLevel.Blocked,
            TrustLevel.Low      => composite >= 42 ? TrustLevel.Medium : TrustLevel.Low,
            TrustLevel.Medium   => composite >= 62 ? TrustLevel.High
                                 : composite <  35 ? TrustLevel.Low
                                 : TrustLevel.Medium,
            TrustLevel.High     => composite <  55 ? TrustLevel.Medium : TrustLevel.High,
            _                   => TrustLevel.Medium
        };
    }

    public static TrustLevel ParseLevel(string s) => s switch
    {
        "high"    => TrustLevel.High,
        "low"     => TrustLevel.Low,
        "blocked" => TrustLevel.Blocked,
        _         => TrustLevel.Medium
    };

    public static string LevelToString(TrustLevel l) => l switch
    {
        TrustLevel.High    => "high",
        TrustLevel.Low     => "low",
        TrustLevel.Blocked => "blocked",
        _                  => "medium"
    };

    // Identitet – räknas om bara när något faktiskt ändrats (email, 2fa, ålder)
    public static double CalculateIdentity(bool emailVerified, bool twoFa, string createdAt)
    {
        double score = 40.0;
        if (emailVerified)  score += 15;
        if (twoFa)          score += 10;
        DateTime.TryParse(createdAt, out var created);
        var ageDays = (DateTime.UtcNow - created).TotalDays;
        score += Math.Min(ageDays / 10.0, 25.0); // max +25 efter ~8 månader
        return Math.Clamp(score, 0, 100);
    }

    // Behavior – applicera penalty, kallas från events, INTE från /api/me
    public static double ApplyPenalty(double current, int points)
        => Math.Max(0, current - points);

    // Feature gates baserade på dimensioner
    public static object EvaluateFeatures(TrustDimensions d, double composite, bool isSuspended, string? disabledFeatures)
    {
        var disabled = string.IsNullOrEmpty(disabledFeatures)
            ? new System.Collections.Generic.HashSet<string>()
            : new System.Collections.Generic.HashSet<string>(
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(disabledFeatures) ?? new());

        bool Allow(string feature, bool condition)
            => !isSuspended && !disabled.Contains(feature) && condition;

        return new
        {
            canSendLinks    = Allow("links",     d.Behavior >= 45 && composite >= 40),
            canUploadFiles  = Allow("files",     d.Behavior >= 55 && d.Device >= 40 && composite >= 45),
            canExecuteCode  = Allow("exec",      d.Behavior >= 65 && d.Device >= 50 && d.Identity >= 55),
            canMentionAll   = Allow("mention",   d.Behavior >= 70),
            canCustomProfile= Allow("profile",   d.Identity >= 45)
        };
    }
}
