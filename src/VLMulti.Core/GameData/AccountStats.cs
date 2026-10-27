namespace VLMulti.Core.GameData;

// Theo dõi tiền/cấp của một acc theo thời gian: tốc độ kiếm tiền và thời gian đứng yên
public sealed class AccountStats
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan MinSpanForRate = TimeSpan.FromMinutes(5);

    private readonly Queue<(DateTime At, long Gold)> _samples = new();

    public long? Gold { get; private set; }
    public long? Knb { get; private set; }
    public int? Level { get; private set; }
    public int? SessionStartLevel { get; private set; }
    public DateTime LastProgressAt { get; private set; }

    public void Update(DateTime now, long? gold, long? knb, int? level)
    {
        bool progressed = LastProgressAt == default;

        if (gold is { } g)
        {
            progressed |= Gold != g;
            Gold = g;
            _samples.Enqueue((now, g));
            while (_samples.Count > 1 && now - _samples.Peek().At > Window)
                _samples.Dequeue();
        }
        if (knb is { } k) Knb = k;
        if (level is { } lv)
        {
            progressed |= Level != lv;
            Level = lv;
            SessionStartLevel ??= lv;
        }

        if (progressed) LastProgressAt = now;
    }

    // Tiến triển khác ngoài tiền/cấp, ví dụ lên tầng tháp
    public void MarkProgress(DateTime now) => LastProgressAt = now;

    // Ra khỏi game (màn đăng nhập/chọn nhân vật): không tính thời gian đó là "đứng yên",
    // và bỏ mẫu cũ để tốc độ không bị méo
    public void ResetActivity(DateTime now)
    {
        LastProgressAt = now;
        _samples.Clear();
    }

    public double? GoldPerHour(DateTime now)
    {
        if (_samples.Count < 2 || Gold is null) return null;
        var first = _samples.Peek();
        var span = now - first.At;
        return span < MinSpanForRate ? null : (Gold.Value - first.Gold) / span.TotalHours;
    }

    public int LevelsGained => Level is { } lv && SessionStartLevel is { } start ? lv - start : 0;

    public TimeSpan IdleFor(DateTime now) => LastProgressAt == default ? TimeSpan.Zero : now - LastProgressAt;
}
