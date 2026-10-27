using System.Text.RegularExpressions;
using VLMulti.Core.Settings;

namespace VLMulti.Core.GameData;

// Số liệu Dã Tẩu đọc từ game: "đã làm/giới hạn hôm nay" ("52/52 (+12 lệnh bài)"), lệnh bài đang có ("21"),
// đã dùng hôm nay ("35/160"), đã mua hôm nay ("55/100"), số nhiệm vụ tối đa mỗi ngày ("200")
public sealed record QuestCounts(string DaTau, string LdtHave = "", string LdtUsed = "", string LdtBuy = "", string MaxPerDay = "");

// Luật tự đổi chế độ: chọn chế độ nên chạy cho một acc, theo thứ tự ưu tiên
//   1. Dã Tẩu còn làm được hôm nay → Nhiệm Vụ   2. còn lượt Phó bản → Phó Bản
//   3. còn chuyến thuyền → Đi thuyền   4. còn lượt Boss tuần → Boss tuần (cả hai: ở Luyện Công rồi bấm nút trong bảng Nhiệm vụ)
//   5. còn Lệnh Bài Sát Thủ → Sát Thủ (ở Luyện Công, gọi từng con tới khi hết)   6. còn lại → Leo Tháp / Luyện Công
// Chế độ đang bị tạm khoá (ví dụ tháp sau 3 lần chết nhanh) bị bỏ qua – xem `isBlocked`.
public static partial class ModeRules
{
    public const string Farm = "farm", Dungeon = "dg", Quest = "quest", Tower = "tower";
    // Không phải chế độ của game: "ở Luyện Công rồi gọi Boss Sát Thủ / lên thuyền / khiêu chiến Boss tuần"
    public const string Assassin = "assassin", Boat = "boat", WeekBoss = "weekboss";

    [GeneratedRegex(@"^\s*(\d+)\s*/\s*(\d+)")]
    private static partial Regex FractionPattern();

    public static (string Mode, string Reason)? Decide(QuestCounts quest, string phoBan, Func<string, bool> isBlocked, AppSettings cfg,
                                                       int assassinTokens = 0, int boatLeft = 0, int weekBossLeft = 0)
    {
        if (cfg.AutoModeQuest && !isBlocked(Quest) && QuestLeft(quest) is { } why)
            return (Quest, why);

        if (cfg.AutoModeDungeon && !isBlocked(Dungeon) && TryFraction(phoBan, out var left, out _) && left > 0)
            return (Dungeon, $"còn {left} lượt Phó bản");

        if (cfg.AutoModeBoat && !isBlocked(Boat) && boatLeft > 0)
            return (Boat, $"còn {boatLeft} chuyến thuyền");

        if (cfg.AutoModeWeekBoss && !isBlocked(WeekBoss) && weekBossLeft > 0)
            return (WeekBoss, $"còn {weekBossLeft} lượt Boss tuần");

        if (cfg.AutoModeAssassin && !isBlocked(Assassin) && assassinTokens > 0)
            return (Assassin, $"còn {assassinTokens} Lệnh Bài Sát Thủ");

        if (cfg.AutoModeFallback == Tower && !isBlocked(Tower))
            return (Tower, "đã xong Dã Tẩu và Phó bản");

        return (Farm, cfg.AutoModeFallback == Tower ? "tháp đang tạm nghỉ" : "đã xong Dã Tẩu và Phó bản");
    }

    public static (string Mode, string Reason)? Decide(string daTau, string phoBan, Func<string, bool> isBlocked, AppSettings cfg) =>
        Decide(new QuestCounts(daTau), phoBan, isBlocked, cfg);

    // Hôm nay còn làm Dã Tẩu được không: còn lượt thường, hoặc còn lệnh bài / còn mua được lệnh bài
    // (chưa dùng hết lượt lệnh bài trong ngày). Trả về lý do, null nếu đã xong.
    public static string? QuestLeft(QuestCounts q)
    {
        if (!TryFraction(q.DaTau, out var done, out var limit)) return null;
        if (int.TryParse(q.MaxPerDay, out var max) && max > 0 && done >= max) return null;
        if (done < limit) return $"Dã Tẩu {done}/{limit}";

        bool tokensLeftToday = !TryFraction(q.LdtUsed, out var used, out var usedMax) || used < usedMax;
        if (!tokensLeftToday) return null;
        if (int.TryParse(q.LdtHave, out var have) && have > 0) return $"còn {have} lệnh bài";
        if (TryFraction(q.LdtBuy, out var bought, out var buyMax) && bought < buyMax) return "mua thêm lệnh bài";
        return null;
    }

    public static bool TryFraction(string? text, out int a, out int b)
    {
        var m = FractionPattern().Match(text ?? "");
        a = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        b = m.Success ? int.Parse(m.Groups[2].Value) : 0;
        return m.Success;
    }

    public static string Label(string mode) => mode switch
    {
        Dungeon => "Phó Bản",
        Quest => "Nhiệm Vụ",
        Tower => "Leo Tháp",
        Assassin => "Sát Thủ",
        Boat => "Đi thuyền",
        WeekBoss => "Boss tuần",
        _ => "Luyện Công",
    };
}
