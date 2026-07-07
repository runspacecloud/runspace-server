using System.Collections.Concurrent;
using System.Security.Cryptography;

public class SessionManager
{
    private const int MaxSessionsPerUser = 5;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SessionInfo>> _sessions = new();

    public string CreateSession(string username, string ip = "", string userAgent = "")
    {
        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var userSessions = _sessions.GetOrAdd(username, _ => new ConcurrentDictionary<string, SessionInfo>());

        if (userSessions.Count >= MaxSessionsPerUser)
        {
            var oldestSessionId = userSessions
                .OrderBy(x => x.Value.LastActivity)
                .First()
                .Key;

            userSessions.TryRemove(oldestSessionId, out _);
        }

        userSessions[sessionId] = new SessionInfo
        {
            SessionId = sessionId,
            Ip = ip,
            UserAgent = userAgent,
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };

        return sessionId;
    }

    public bool IsSessionValid(string username, string sessionId)
    {
        return _sessions.TryGetValue(username, out var userSessions)
            && userSessions.ContainsKey(sessionId);
    }

    public SessionInfo? GetSessionInfo(string username, string sessionId)
    {
        if (!_sessions.TryGetValue(username, out var userSessions))
            return null;

        userSessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public void TouchSession(string username, string sessionId, string ip)
    {
        if (!_sessions.TryGetValue(username, out var userSessions))
            return;

        if (!userSessions.TryGetValue(sessionId, out var session))
            return;

        session.LastActivity = DateTime.UtcNow;
        session.Ip = ip;
    }

    public void InvalidateSpecificSession(string username, string sessionId)
    {
        if (_sessions.TryGetValue(username, out var userSessions))
            userSessions.TryRemove(sessionId, out _);
    }

    public void InvalidateAllSessions(string username)
    {
        _sessions.TryRemove(username, out _);
    }

    public void InvalidateEverything()
    {
        _sessions.Clear();
    }

    public int GetActiveSessionCount(string username)
    {
        return _sessions.TryGetValue(username, out var userSessions)
            ? userSessions.Count
            : 0;
    }

    public int GetTotalSessionCount()
    {
        return _sessions.Values.Sum(userSessions => userSessions.Count);
    }

    public List<object> GetUserSessions(string username)
    {
        if (!_sessions.TryGetValue(username, out var userSessions))
            return new List<object>();

        return userSessions.Values
            .Select(session => (object)new
            {
                sessionId = session.SessionId[..16] + "...",
                ip = session.Ip,
                userAgent = session.UserAgent.Length > 100
                    ? session.UserAgent[..100] + "..."
                    : session.UserAgent,
                createdAt = session.CreatedAt.ToString("o"),
                lastActivity = session.LastActivity.ToString("o")
            })
            .ToList();
    }
}
