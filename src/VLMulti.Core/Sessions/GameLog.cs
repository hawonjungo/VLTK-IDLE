using System.Text.RegularExpressions;

namespace VLMulti.Core.Sessions;

// 📜 Nhật ký game: mọi dòng game ghi (hàm log của game → R.logs), mỗi acc mỗi ngày (giờ Việt Nam) một file
//   data/gamelog/<acc>-<yyyy-MM-dd>.log, mỗi dòng: "HH:mm:ss\t<loại>\t<nơi>\t<tên nhân vật>\t<nội dung>"
// <nơi> = "Tầng 11763" khi ở Thông Thiên Tháp, ngược lại tên bản đồ. Bỏ dòng lặt vặt (kinh nghiệm, lượng, KNB khoá…).
public static class GameLog
{
    public sealed record Entry(DateTime At, string Kind, string Place, string Char, string Text);

    private static readonly object Gate = new();
    public static string Dir => Path.Combine(AppPaths.DataDir, "gamelog");

    // Dòng lặt vặt không ghi
    private static readonly Regex Noise = new(
        @"^(Nhận được [\d.,]+ điểm kinh nghiệm|Nhặt được [\d.,]+ (vạn )?lượng|💰 \+\d+ KNB khóa|Tự mua |Tiếp tục hành tẩu" +
        // Dã Tẩu: dòng đi làm nhiệm vụ / dùng lệnh bài (chỉ giữ dòng phần thưởng, xem DaTauTask)
        @"|🗺 (Dã Tẩu|Nhiệm vụ): |📜 Dùng Lệnh Bài Dã Tẩu|📜 Chế độ Nhiệm Vụ: dịch chuyển)",
        RegexOptions.Compiled);
    // Đồ hiếm: Hoàng Kim, Đồ phổ, Thần Mã, bộ Vô Danh, trang bị có kỹ năng vốn có
    private static readonly Regex Rare = new(@"Hoàng Kim|Đồ phổ|Thần Mã|Vô Danh|kỹ năng vốn có", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsNoise(string text) => text.Length == 0 || Noise.IsMatch(text);

    // 📜 Dã Tẩu: "🗺 Dã Tẩu: Săn 30 Thủy Tặc ở Phong Lăng độ: tới Phong Lăng độ" → nhiệm vụ "Săn 30 Thủy Tặc"; dòng
    // thưởng sau đó "🎁 Dã Tẩu #2491: 1 Rương Ngựa" ghi thành "🎁 Dã Tẩu #2491 (Săn 30 Thủy Tặc): 1 Rương Ngựa"
    private static readonly Regex DaTauTaskLine = new(@"^🗺 Dã Tẩu: (?!tới )(?<task>.+?)(?: ở [^:]+)?: tới ", RegexOptions.Compiled);
    private static readonly Regex DaTauReward = new(@"^🎁 Dã Tẩu #(?<n>\d+): (?<reward>.+)$", RegexOptions.Compiled);
    public static string? DaTauTask(string text) => DaTauTaskLine.Match(text) is { Success: true } m ? m.Groups["task"].Value.Trim() : null;
    public static string WithDaTauTask(string text, string? task) =>
        task is { Length: > 0 } && DaTauReward.Match(text) is { Success: true } m ? $"🎁 Dã Tẩu #{m.Groups["n"].Value} ({task}): {m.Groups["reward"].Value}" : text;
    static bool IsDaTau(string text) => Regex.IsMatch(text, @"^(🎁 (Mốc )?Dã Tẩu|📜 Dã Tẩu \d)");

    // Chỉ ghi chuyện của chính nhân vật: bỏ thông báo chung của server ("📢 Chúc mừng X …"), kênh thế giới / bang / tổ
    // đội – trừ khi có tên chính nhân vật này
    private static readonly Regex Others = new(@"Chúc mừng|📢|Hệ thống\s*:|Thế giới|[Tt]ổ đội|gia nhập bang|rời bang|\[Bang\]|\[Đội\]", RegexOptions.Compiled);
    public static bool AboutOthers(string text, string? ownName) =>
        Others.IsMatch(text) && !(ownName is { Length: > 0 } && text.Contains(ownName));

    // Loại dòng (để lọc trên trang web)
    public static string Classify(string text)
    {
        bool boss = text.Contains("Boss Hoàng Kim") || text.Contains("Boss tuần");
        if (Rare.IsMatch(text) && (text.Contains("Chúc mừng") || text.StartsWith("📢"))) return "rare"; // thông báo về chính mình
        if (Rare.IsMatch(text) && !boss && Regex.IsMatch(text, @"Nhặt được|[Nn]hận|🎁|📕")) return "rare";
        if (boss) return Rare.IsMatch(text) && text.Contains("🎁") ? "rare" : "boss";
        if (IsDaTau(text)) return "datau";
        if (text.Contains("Sát Thủ") || text.Contains("truy sát")) return "assassin";
        if (Regex.IsMatch(text, @"Gục|trọng thương|gục ngã")) return "death";
        if (text.Contains("🗼") || text.Contains("Tháp")) return "tower";
        if (Regex.IsMatch(text, @"^Nhặt được|🎁|Nhận ")) return "loot";
        return "other";
    }

    public static string FileOf(string accId, DateOnly day) => Path.Combine(Dir, $"{accId}-{day:yyyy-MM-dd}.log");
    public static DateOnly VnDay(DateTime at) => DateOnly.FromDateTime(at.ToUniversalTime().AddHours(7));

    public static void Append(string accId, IEnumerable<Entry> entries)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                foreach (var group in entries.GroupBy(e => VnDay(e.At)))
                    File.AppendAllLines(FileOf(accId, group.Key), group.Select(e =>
                        $"{e.At.ToUniversalTime().AddHours(7):HH:mm:ss}\t{e.Kind}\t{Clean(e.Place)}\t{Clean(e.Char)}\t{Clean(e.Text)}"));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Đọc lại (mới nhất trước), lọc theo loại; kinds rỗng = mọi loại
    public static List<Entry> Read(string accId, DateOnly day, IReadOnlyCollection<string>? kinds = null, int max = 500)
    {
        var path = FileOf(accId, day);
        var list = new List<Entry>();
        lock (Gate)
        {
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path).Reverse())
            {
                var p = line.Split('\t');
                if (p.Length < 5 || !TimeOnly.TryParse(p[0], out var t)) continue;
                if (kinds is { Count: > 0 } && !kinds.Contains(p[1])) continue;
                var at = day.ToDateTime(t, DateTimeKind.Utc).AddHours(-7);
                list.Add(new Entry(at, p[1], p[2], p[3], p[4]));
                if (list.Count >= max) break;
            }
        }
        return list;
    }

    // Mọi acc trong ngày (bảng "Đồ hiếm"): gộp, mới nhất trước; dòng "📢 Chúc mừng" của server trùng giữa các acc → giữ 1
    public static List<Entry> ReadAll(DateOnly day, IReadOnlyCollection<string> kinds, int max = 500)
    {
        var all = new List<Entry>();
        if (!Directory.Exists(Dir)) return all;
        foreach (var f in Directory.GetFiles(Dir, $"*-{day:yyyy-MM-dd}.log"))
        {
            var name = Path.GetFileName(f);
            all.AddRange(Read(name[..^($"-{day:yyyy-MM-dd}.log".Length)], day, kinds, max));
        }
        return all
            .GroupBy(e => $"{e.Char}|{e.At:O}|{e.Text}")
            .Select(g => g.OrderBy(e => e.At).First())
            .OrderByDescending(e => e.At).Take(max).ToList();
    }

    // Tổng kết Dã Tẩu trong ngày của một acc: số nhiệm vụ có thưởng và cộng dồn từng món (bỏ kinh nghiệm)
    private static readonly Regex Item = new(@"^(?<n>\d+) (?<name>.+)$", RegexOptions.Compiled);
    public static object DaTauSummary(string accId, DateOnly day) => DaTauSummary(Read(accId, day, new[] { "datau", "rare" }, 5000));
    public static object DaTauSummary(IEnumerable<Entry> entries)
    {
        var rows = entries.Where(e => e.Text.StartsWith("🎁 Dã Tẩu #") || e.Text.StartsWith("🎁 Mốc Dã Tẩu")).ToList();
        var items = new Dictionary<string, long>();
        int quests = 0;
        foreach (var e in rows)
        {
            if (e.Text.StartsWith("🎁 Dã Tẩu #")) quests++;
            var i = e.Text.IndexOf("): ", StringComparison.Ordinal) is var p and >= 0 ? p + 3 : e.Text.IndexOf(": ", StringComparison.Ordinal) + 2;
            if (i < 2) continue;
            foreach (var part in e.Text[i..].Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Contains("kinh nghiệm")) continue;
                var m = Item.Match(part);
                var (name, n) = m.Success ? (m.Groups["name"].Value, long.Parse(m.Groups["n"].Value)) : (part, 1L);
                if (Regex.IsMatch(name, @"%|\(chờ")) name = Regex.Replace(name, @"\s*(\+?\d+%.*|\(chờ.*\))$", "").Trim(); // "Tiên Thảo Lộ +20% trong 30 phút" → "Tiên Thảo Lộ"
                if (name.Length == 0) continue;
                items[name] = items.GetValueOrDefault(name) + n;
            }
        }
        return new { quests, items = items.OrderByDescending(x => x.Value).Select(x => new { name = x.Key, n = x.Value }) };
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
