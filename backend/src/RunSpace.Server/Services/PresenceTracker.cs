using System.Collections.Concurrent;

public class PresenceTracker
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _connections = new();

    public void AddConnection(string username, string connectionId)
    {
        var connections = _connections.GetOrAdd(username, _ => new HashSet<string>());

        lock (connections)
        {
            connections.Add(connectionId);
        }
    }

    public void RemoveConnection(string username, string connectionId)
    {
        if (!_connections.TryGetValue(username, out var connections))
            return;

        lock (connections)
        {
            connections.Remove(connectionId);

            if (connections.Count == 0)
                _connections.TryRemove(username, out _);
        }
    }

    public bool IsOnline(string username)
    {
        return _connections.ContainsKey(username);
    }

    public List<string> GetOnlineUsers()
    {
        return _connections.Keys.ToList();
    }
}
