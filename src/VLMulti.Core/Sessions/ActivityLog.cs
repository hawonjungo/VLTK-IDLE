namespace VLMulti.Core.Sessions;

public sealed record ActivityEntry(DateTime At, string Text, StatusLevel Level);

// Nhật ký hoạt động của một acc (chỉ acc của bạn): lên cấp, lên tầng tháp, ngã tháp, đổi chế độ,
// bị đẩy ra, đồ phổ… App tự ghi từ những gì quan sát được, vì nhật ký trong game chỉ cập nhật khi mở bảng.
public sealed class ActivityLog
{
    private const int MaxItems = 200;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    private readonly List<ActivityEntry> _items = new(); // mới nhất ở đầu

    public IReadOnlyList<ActivityEntry> Items => _items;

    // Cùng một dòng lặp lại trong 10 phút (ví dụ cảnh báo đứng yên) chỉ ghi một lần
    public bool Add(DateTime now, string text, StatusLevel level = StatusLevel.Info)
    {
        if (_items.Any(e => e.Text == text && now - e.At < RepeatWindow)) return false;
        _items.Insert(0, new ActivityEntry(now, text, level));
        if (_items.Count > MaxItems) _items.RemoveAt(_items.Count - 1);
        return true;
    }
}
