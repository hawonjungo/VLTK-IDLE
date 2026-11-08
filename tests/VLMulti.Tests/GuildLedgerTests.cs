using System.Text.Json;
using VLMulti.Core.Sessions;
using Xunit;

namespace VLMulti.Tests;

public class GuildLedgerTests
{
    // Thứ Hai 09/11/2026 08:00 giờ VN = 01:00 UTC
    private static readonly DateTime Mon = new(2026, 11, 9, 1, 0, 0, DateTimeKind.Utc);
    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    private static JsonElement Poll(params (DateTime At, string Msg)[] log) => JsonSerializer.SerializeToElement(new
    {
        r = "ok",
        guild = new { id = 47, name = "Bang Thử", cap = 1 },
        fund = new { knb = 500, van = 400000, needKnb = 20000, needVan = 300000 },
        members = new object[]
        {
            new { name = "A", chuc = "chu", lvl = 120, week = 1000, total = 2000, on = Ms(Mon) },
            new { name = "B", chuc = "chung", lvl = 99, week = 0, total = 10, on = Ms(Mon) },
        },
        log = log.Select(x => new { at = Ms(x.At), msg = x.Msg }).ToArray(),
    });

    private static JsonElement Week(GuildLedger g, DateTime now) => JsonSerializer.SerializeToElement(g.Week(DateOnly.FromDateTime(now.AddHours(7)), 100, now));

    [Fact]
    public void Knb_lines_are_counted_once_per_member_and_day()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vl-ledger-" + Guid.NewGuid().ToString("N"));
        var g = new GuildLedger(dir);
        var p = Poll((Mon, "A góp 100 KNB"), (Mon.AddMinutes(1), "A góp 20000 vạn lượng"), (Mon.AddMinutes(2), "B góp 1.000 vạn lượng"));
        g.Ingest(p, Mon.AddMinutes(3));
        g.Ingest(p, Mon.AddMinutes(4)); // acc khác đọc cùng nhật ký → không cộng trùng
        g.Ingest(Poll((Mon.AddDays(1), "A góp 100 KNB")), Mon.AddDays(1).AddMinutes(1));

        var w = Week(g, Mon.AddDays(1).AddMinutes(2));
        Assert.Equal("2026-11-09", w.GetProperty("week").GetString());
        var a = w.GetProperty("members").EnumerateArray().First(m => m.GetProperty("name").GetString() == "A");
        Assert.Equal(100, a.GetProperty("days")[0].GetInt64());
        Assert.Equal(100, a.GetProperty("days")[1].GetInt64());
        Assert.Equal(200, a.GetProperty("total").GetInt64());
        Assert.Equal(20000, a.GetProperty("van").GetInt64());
        Assert.Equal("ok", a.GetProperty("status").GetString()); // đủ theo tiến độ 2 ngày
        var b = w.GetProperty("members").EnumerateArray().First(m => m.GetProperty("name").GetString() == "B");
        Assert.Equal("none", b.GetProperty("status").GetString());
        Assert.Equal(200, b.GetProperty("owed").GetInt64());
        Assert.Equal(20000, w.GetProperty("fund").GetProperty("NeedKnb").GetInt64());

        // Đọc lại từ file (khởi động lại server)
        var again = Week(new GuildLedger(dir), Mon.AddDays(1).AddMinutes(2));
        Assert.Equal(200, again.GetProperty("members").EnumerateArray().First(m => m.GetProperty("name").GetString() == "A").GetProperty("total").GetInt64());
        Directory.Delete(dir, true);
    }

    [Fact]
    public void A_gap_is_recorded_when_the_log_scrolled_past_the_last_read()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vl-ledger-" + Guid.NewGuid().ToString("N"));
        var g = new GuildLedger(dir);
        g.Ingest(Poll((Mon, "A góp 100 KNB")), Mon.AddMinutes(1));
        // Lần đọc sau không còn thấy dòng mới nhất cũ (đã trôi quá 30 dòng)
        g.Ingest(Poll((Mon.AddHours(2), "B góp 100 KNB")), Mon.AddHours(2).AddMinutes(1));
        var w = Week(g, Mon.AddHours(3));
        Assert.Single(w.GetProperty("gaps").EnumerateArray());
        // Còn thấy dòng cũ → không có khoảng trống mới
        g.Ingest(Poll((Mon.AddHours(2), "B góp 100 KNB"), (Mon.AddHours(3), "A góp 100 KNB")), Mon.AddHours(3).AddMinutes(1));
        Assert.Single(Week(g, Mon.AddHours(4)).GetProperty("gaps").EnumerateArray());
        Directory.Delete(dir, true);
    }
}
