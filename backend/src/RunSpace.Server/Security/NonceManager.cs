using System.Collections.Concurrent;

public class NonceManager
{
    private readonly ConcurrentDictionary<string, DateTime> _usedNonces = new();

    public bool Consume(string? nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce))
            return false;

        return _usedNonces.TryAdd(nonce, DateTime.UtcNow);
    }
}
