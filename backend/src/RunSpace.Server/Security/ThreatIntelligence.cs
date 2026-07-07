using System.Collections.Concurrent;

public class ThreatIntelligence
{
    private const int NormalStrikeThreshold = 10;
    private const int AttackModeStrikeThreshold = 5;
    private const int MaxUsersPerIp = 10;

    private readonly ConcurrentDictionary<string, int> _strikesByIp = new();
    private readonly ConcurrentDictionary<string, DateTime> _blockedUntilByIp = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _usersByIp = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _requestTimesByIp = new();

    private int _strikeThreshold = NormalStrikeThreshold;
    private bool _underAttack;

    public bool IsUnderAttack => _underAttack;

    public bool IsBlocked(string ip)
    {
        if (!_blockedUntilByIp.TryGetValue(ip, out var blockedUntil))
            return false;

        if (blockedUntil > DateTime.UtcNow)
            return true;

        _blockedUntilByIp.TryRemove(ip, out _);
        return false;
    }

    public void RecordStrike(string ip, string reason)
    {
        var strikes = _strikesByIp.AddOrUpdate(ip, 1, (_, current) => current + 1);

        if (strikes >= _strikeThreshold)
        {
            var blockHours = _underAttack ? 48 : 24;
            _blockedUntilByIp[ip] = DateTime.UtcNow.AddHours(blockHours);
        }
    }

    public void RecordLoginAttempt(string ip, string username)
    {
        var users = _usersByIp.GetOrAdd(ip, _ => new HashSet<string>());

        lock (users)
        {
            users.Add(username);

            if (users.Count > MaxUsersPerIp)
            {
                _blockedUntilByIp[ip] = DateTime.UtcNow.AddHours(24);
                users.Clear();
            }
        }
    }

    public bool IsRequestFlooding(string ip, int maxPerSecond = 20)
    {
        var requestTimes = _requestTimesByIp.GetOrAdd(ip, _ => new List<DateTime>());

        lock (requestTimes)
        {
            var now = DateTime.UtcNow;

            requestTimes.Add(now);
            requestTimes.RemoveAll(x => x < now.AddSeconds(-1));

            return requestTimes.Count > maxPerSecond;
        }
    }

    public void BlockIp(string ip)
    {
        _blockedUntilByIp[ip] = DateTime.UtcNow.AddDays(30);
    }

    public void UnblockIp(string ip)
    {
        _blockedUntilByIp.TryRemove(ip, out _);
        _strikesByIp.TryRemove(ip, out _);
    }

    public void EnableAttackMode()
    {
        _underAttack = true;
        _strikeThreshold = AttackModeStrikeThreshold;
    }

    public void DisableAttackMode()
    {
        _underAttack = false;
        _strikeThreshold = NormalStrikeThreshold;
    }

    public int GetBlockedIpCount()
    {
        return _blockedUntilByIp.Count(x => x.Value > DateTime.UtcNow);
    }
}
