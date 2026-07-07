using System.Collections.Concurrent;

public class DeviceFingerprintManager
{
    private const int MaxDevicesPerUser = 50;

    private readonly ConcurrentDictionary<string, HashSet<string>> _devicesByUser = new();

    public bool IsKnownDevice(string username, string fingerprint, string userAgent)
    {
        var deviceKey = BuildDeviceKey(fingerprint, userAgent);

        if (!_devicesByUser.TryGetValue(username, out var devices))
            return false;

        lock (devices)
        {
            return devices.Contains(deviceKey);
        }
    }

    public void RegisterDevice(string username, string fingerprint, string userAgent, string ip)
    {
        var deviceKey = BuildDeviceKey(fingerprint, userAgent);
        var devices = _devicesByUser.GetOrAdd(username, _ => new HashSet<string>());

        lock (devices)
        {
            if (devices.Count < MaxDevicesPerUser)
                devices.Add(deviceKey);
        }
    }

    private static string BuildDeviceKey(string fingerprint, string userAgent)
    {
        return $"{fingerprint}|{userAgent}";
    }
}
