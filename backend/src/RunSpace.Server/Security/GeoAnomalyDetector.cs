using System.Collections.Concurrent;

public class GeoAnomalyDetector
{
    private readonly ConcurrentDictionary<string, (string Ip, DateTime When)> _lastLoginByUser = new();

    public void RecordLogin(string username, string ip)
    {
        _lastLoginByUser[username] = (ip, DateTime.UtcNow);
    }

    public bool IsImpossibleTravel(string username, string ip)
    {
        if (!_lastLoginByUser.TryGetValue(username, out var lastLogin))
            return false;

        return lastLogin.Ip != ip
            && (DateTime.UtcNow - lastLogin.When).TotalSeconds < 30;
    }
}
