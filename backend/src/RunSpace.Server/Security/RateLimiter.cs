using System.Collections.Concurrent;

public class RateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _windows = new();

    public bool IsAllowed(string identifier, string action, int maxRequests, int windowSeconds)
    {
        var key = $"{identifier}:{action}";
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-windowSeconds);

        var window = _windows.GetOrAdd(key, _ => new Queue<DateTime>());

        lock (window)
        {
            while (window.Count > 0 && window.Peek() < cutoff)
                window.Dequeue();

            if (window.Count >= maxRequests)
                return false;

            window.Enqueue(now);
            return true;
        }
    }
}
