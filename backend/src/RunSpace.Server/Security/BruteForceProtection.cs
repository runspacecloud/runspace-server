using System.Collections.Concurrent;

public class BruteForceProtection
{
    private const int MaxIpFailures = 20;
    private const int MaxAccountFailures = 5;
    private const int LockWindowMinutes = 30;

    private readonly ConcurrentDictionary<string, List<DateTime>> _ipFailures = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _accountFailures = new();

    public bool IsIpLocked(string ip)
    {
        return IsLocked(_ipFailures, ip, MaxIpFailures);
    }

    public bool IsAccountLocked(string username)
    {
        return IsLocked(_accountFailures, username, MaxAccountFailures);
    }

    public void RecordFailedAttempt(string ip, string username)
    {
        Record(_ipFailures, ip);
        Record(_accountFailures, username);
    }

    public void ClearAttempts(string ip, string username)
    {
        _ipFailures.TryRemove(ip, out _);
        _accountFailures.TryRemove(username, out _);
    }

    public int GetFailCount(string username)
    {
        if (!_accountFailures.TryGetValue(username, out var attempts))
            return 0;

        lock (attempts)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-LockWindowMinutes);
            return attempts.Count(x => x > cutoff);
        }
    }

    public int GetProgressiveDelay(string username)
    {
        return Math.Min(GetFailCount(username) * 1000, 15000);
    }

    private static bool IsLocked(
        ConcurrentDictionary<string, List<DateTime>> store,
        string key,
        int maxAttempts)
    {
        if (!store.TryGetValue(key, out var attempts))
            return false;

        lock (attempts)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-LockWindowMinutes);
            attempts.RemoveAll(x => x < cutoff);

            return attempts.Count >= maxAttempts;
        }
    }

    private static void Record(
        ConcurrentDictionary<string, List<DateTime>> store,
        string key)
    {
        var attempts = store.GetOrAdd(key, _ => new List<DateTime>());

        lock (attempts)
        {
            attempts.Add(DateTime.UtcNow);
        }
    }
}
