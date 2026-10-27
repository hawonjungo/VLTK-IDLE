namespace VLMulti.Core.GameData;

// Lịch Boss Hoàng Kim của game: 12:00 · 15:30 · 19:30 · 22:00 giờ Việt Nam (GMT+7), mỗi đợt 30 phút.
// Trong đợt boss, game tự đưa nhân vật đi đánh rồi về lại tháp → app không được chen vào.
public static class BossHkSchedule
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly TimeSpan[] Starts =
    {
        new(12, 0, 0), new(15, 30, 0), new(19, 30, 0), new(22, 0, 0),
    };
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);
    // Đệm thêm hai đầu: đồng hồ máy có thể lệch, game cần thời gian đưa nhân vật về lại tháp
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(1), After = TimeSpan.FromMinutes(3);

    public static bool IsActive(DateTime utc)
    {
        var vn = (utc + VietnamOffset).TimeOfDay;
        return Starts.Any(s => vn >= s - Before && vn < s + Duration + After);
    }

    // Đếm ngược cho giao diện (không tính phần đệm): đang trong đợt → (true, giờ hết đợt); chưa tới → (false, giờ bắt đầu đợt sau).
    // Thời điểm trả về là UTC; `VnTime` là giờ Việt Nam để hiện đúng như game ghi ("19:30").
    public static (bool Active, DateTime AtUtc, TimeSpan VnTime) Next(DateTime utc)
    {
        var vnNow = utc + VietnamOffset;
        for (int day = 0; day < 2; day++)
            foreach (var s in Starts)
            {
                var start = vnNow.Date.AddDays(day) + s;
                if (vnNow >= start && vnNow < start + Duration) return (true, start + Duration - VietnamOffset, s);
                if (start > vnNow) return (false, start - VietnamOffset, s);
            }
        return (false, utc, Starts[0]); // không xảy ra
    }
}
