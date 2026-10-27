namespace VLMulti.Core.Recovery;

// Giới hạn số lần thử trong một khoảng thời gian (đăng nhập lại, tải lại trang…),
// để app không thử liên tục khi có lỗi thật – ví dụ sai mật khẩu hay acc bị đăng nhập nơi khác
public sealed class AttemptLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _attempts = new();

    public AttemptLimiter(int max, TimeSpan window)
    {
        _max = max;
        _window = window;
    }

    public bool TryAcquire(DateTime now)
    {
        while (_attempts.Count > 0 && now - _attempts.Peek() > _window)
            _attempts.Dequeue();
        if (_attempts.Count >= _max) return false;
        _attempts.Enqueue(now);
        return true;
    }

    // Còn được thử không (không tính thêm một lần thử)
    public bool CanAcquire(DateTime now)
    {
        while (_attempts.Count > 0 && now - _attempts.Peek() > _window)
            _attempts.Dequeue();
        return _attempts.Count < _max;
    }

    public void Reset() => _attempts.Clear();
}
