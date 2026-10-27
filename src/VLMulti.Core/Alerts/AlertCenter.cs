namespace VLMulti.Core.Alerts;

public sealed record Alert(DateTime At, string AccountId, string AccountName, string Message);

public sealed class AlertCenter
{
    private const int MaxItems = 200;

    private readonly TimeSpan _cooldown;
    private readonly Dictionary<(string AccountId, string Key), DateTime> _lastRaised = new();
    private readonly List<Alert> _items = new();

    public AlertCenter(TimeSpan cooldown) => _cooldown = cooldown;

    public IReadOnlyList<Alert> Items => _items;

    public event Action<Alert>? Raised;
    public event Action? Cleared;

    // Cùng một loại cảnh báo cho cùng acc chỉ báo lại sau khoảng cooldown, tránh kêu liên tục
    public bool Raise(string key, string accountId, string accountName, string message, DateTime now)
    {
        if (_lastRaised.TryGetValue((accountId, key), out var last) && now - last < _cooldown)
            return false;

        _lastRaised[(accountId, key)] = now;
        var alert = new Alert(now, accountId, accountName, message);
        _items.Insert(0, alert);
        if (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);

        Raised?.Invoke(alert);
        return true;
    }

    public void Clear()
    {
        _items.Clear();
        Cleared?.Invoke();
    }
}
