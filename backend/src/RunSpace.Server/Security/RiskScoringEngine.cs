using System.Collections.Concurrent;

public class RiskScoringEngine
{
    private readonly ConcurrentDictionary<string, List<DateTime>> _recentChecks = new();

    public int CalculateScore(string username, string ip, string userAgent, string fingerprint, string lastIp)
    {
        var score = 0;

        if (string.IsNullOrWhiteSpace(userAgent))
            score += 35;

        if (string.IsNullOrWhiteSpace(fingerprint))
            score += 10;

        if (!string.IsNullOrWhiteSpace(lastIp) && ip != lastIp)
            score += 15;

        if (!string.IsNullOrWhiteSpace(userAgent) && userAgent.Length < 20)
            score += 15;

        var checks = _recentChecks.GetOrAdd(username, _ => new List<DateTime>());

        lock (checks)
        {
            checks.Add(DateTime.UtcNow);
            checks.RemoveAll(x => x < DateTime.UtcNow.AddMinutes(-10));

            if (checks.Count > 5)
                score += 20;
        }

        return Math.Min(score, 100);
    }
}
