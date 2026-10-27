namespace VLMulti.Core.GameData;

// Theo dõi Thông Thiên Tháp của một acc: tốc độ lên tầng (60 phút gần nhất) và số lần ngã tháp
public sealed class TowerTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan MinSpanForRate = TimeSpan.FromMinutes(5);

    private readonly Queue<(DateTime At, int Floor)> _samples = new();

    public int? Floor { get; private set; }
    public int Falls { get; private set; }
    public DateTime? LastFallAt { get; private set; }

    // Gọi mỗi lần đọc được tầng đang đứng (null = ngoài tháp). Trả về true nếu tầng đổi.
    public bool Update(DateTime now, int? floor)
    {
        if (floor == Floor) return false;
        if (floor is { } f)
        {
            // Tầng thấp hơn (vào lại tháp từ tầng đã lưu): mẫu cũ không còn dùng để tính tốc độ
            if (_samples.Count > 0 && f < _samples.Last().Floor) _samples.Clear();
            _samples.Enqueue((now, f));
            while (_samples.Count > 1 && now - _samples.Peek().At > Window) _samples.Dequeue();
        }
        Floor = floor;
        return true;
    }

    public void OnFall(DateTime now)
    {
        Falls++;
        LastFallAt = now;
    }

    public double? FloorsPerHour(DateTime now)
    {
        if (_samples.Count < 2) return null;
        var first = _samples.Peek();
        var span = now - first.At;
        return span < MinSpanForRate ? null : (_samples.Last().Floor - first.Floor) / span.TotalHours;
    }
}
