using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VLMulti.Core.Sessions;

// 🏯 Sổ KNB của bang: các acc của bạn trong bang đọc Nhật ký bang của game ~3 phút một lần (game chỉ giữ 30 dòng gần
// nhất) và danh sách thành viên; sổ gom dòng "X góp N KNB" / "X góp N vạn lượng" (bỏ trùng giữa các acc), ghi lại
// khoảng thời gian có thể sót dòng (nhật ký trôi quá 30 dòng giữa 2 lần đọc), và mỗi ngày một bản danh sách thành viên.
// Lưu ở data/guild/guild-<id>.json trên máy chạy app (chứa tên thành viên – không đưa lên GitHub).
public sealed class GuildLedger
{
    public sealed record Entry(long At, string Name, string Kind, long N, string Msg);
    public sealed record Gap(long From, long To);
    public sealed record Member(string Name, string Chuc, int Lvl, long Week, long Total, long On);
    public sealed record Fund(long Knb, long Van, long NeedKnb, long NeedVan, int Cap);

    private sealed class Data
    {
        public long GuildId { get; set; }
        public string GuildName { get; set; } = "";
        public List<Entry> Entries { get; set; } = new();
        public List<Gap> Gaps { get; set; } = new();
        public Dictionary<string, List<Member>> Members { get; set; } = new(); // theo ngày (giờ VN) "yyyy-MM-dd"
        public long NewestAt { get; set; }
        public string NewestKey { get; set; } = "";
        public Fund? Fund { get; set; }
        public long PolledAt { get; set; }
    }

    private static readonly Regex KnbLine = new(@"^(?<name>.+?) góp (?<n>[\d.,]+) KNB", RegexOptions.Compiled);
    private static readonly Regex VanLine = new(@"^(?<name>.+?) góp (?<n>[\d.,]+) vạn lượng", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly TimeSpan Keep = TimeSpan.FromDays(70);

    private readonly object _gate = new();
    private readonly string _dir;
    private Data? _data;

    public GuildLedger(string? dir = null) => _dir = dir ?? Path.Combine(AppPaths.DataDir, "guild");

    public static DateOnly VnDay(long ms) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.AddHours(7));
    private static string Key(long at, string msg) => $"{at}|{msg}";
    private static long Num(string s) => long.TryParse(s.Replace(".", "").Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    // Kết quả "guildLogResult" của một acc: {guild:{id,name,cap}, fund:{knb,van,needKnb,needVan}, members:[…], log:[{at,msg}]}
    public void Ingest(JsonElement m, DateTime now)
    {
        if (!m.TryGetProperty("guild", out var g) || g.ValueKind != JsonValueKind.Object) return;
        long id = g.TryGetProperty("id", out var gi) && gi.TryGetInt64(out var gid) ? gid : 0;
        string name = g.TryGetProperty("name", out var gn) ? gn.GetString() ?? "" : "";
        int cap = g.TryGetProperty("cap", out var gc) && gc.TryGetInt32(out var c) ? c : 0;
        if (id <= 0) return;
        var log = new List<(long At, string Msg)>();
        if (m.TryGetProperty("log", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var x in l.EnumerateArray())
            {
                long at = x.TryGetProperty("at", out var a) && a.TryGetInt64(out var av) ? av : 0;
                var msg = x.TryGetProperty("msg", out var mm) ? mm.GetString() ?? "" : "";
                if (at > 0 && msg.Length > 0) log.Add((at, msg));
            }
        var members = new List<Member>();
        if (m.TryGetProperty("members", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var x in ms.EnumerateArray())
            {
                string S(string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                long L(string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
                if (S("name").Length > 0) members.Add(new Member(S("name"), S("chuc"), (int)L("lvl"), L("week"), L("total"), L("on")));
            }
        Fund? fund = null;
        if (m.TryGetProperty("fund", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            long F(string k) => f.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
            fund = new Fund(F("knb"), F("van"), F("needKnb"), F("needVan"), cap);
        }

        lock (_gate)
        {
            var d = Load(id);
            d.GuildName = name;
            var keys = d.Entries.Select(e => Key(e.At, e.Msg)).ToHashSet();
            var polled = log.OrderBy(x => x.At).ToList();
            // Nhật ký đã trôi quá 30 dòng từ lần đọc trước → khoảng giữa có thể sót dòng
            if (d.NewestAt > 0 && polled.Count > 0 && polled[0].At > d.NewestAt && !polled.Any(x => Key(x.At, x.Msg) == d.NewestKey))
                d.Gaps.Add(new Gap(d.NewestAt, polled[0].At));
            foreach (var (at, msg) in polled)
            {
                if (!keys.Add(Key(at, msg))) continue;
                if (KnbLine.Match(msg) is { Success: true } k) d.Entries.Add(new Entry(at, k.Groups["name"].Value.Trim(), "knb", Num(k.Groups["n"].Value), msg));
                else if (VanLine.Match(msg) is { Success: true } v) d.Entries.Add(new Entry(at, v.Groups["name"].Value.Trim(), "van", Num(v.Groups["n"].Value), msg));
            }
            if (polled.Count > 0 && polled[^1].At >= d.NewestAt) { d.NewestAt = polled[^1].At; d.NewestKey = Key(polled[^1].At, polled[^1].Msg); }
            if (members.Count > 0) d.Members[VnDay(new DateTimeOffset(now).ToUnixTimeMilliseconds()).ToString("yyyy-MM-dd")] = members;
            if (fund is not null) d.Fund = fund;
            d.PolledAt = new DateTimeOffset(now).ToUnixTimeMilliseconds();
            // Giữ 70 ngày
            long cut = new DateTimeOffset(now - Keep).ToUnixTimeMilliseconds();
            d.Entries.RemoveAll(e => e.At < cut);
            d.Gaps.RemoveAll(x => x.To < cut);
            var cutDay = DateOnly.FromDateTime(now - Keep).ToString("yyyy-MM-dd");
            foreach (var day in d.Members.Keys.Where(k => string.CompareOrdinal(k, cutDay) < 0).ToList()) d.Members.Remove(day);
            Save(d);
        }
    }

    // Bảng tuần (thứ Hai → Chủ Nhật, giờ VN) chứa ngày `day`: mỗi thành viên KNB từng ngày, tổng, mức cần theo tiến độ
    public object Week(DateOnly day, int dailyTarget, DateTime now)
    {
        lock (_gate)
        {
            var d = LoadLatest();
            var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
            var days = Enumerable.Range(0, 7).Select(i => monday.AddDays(i)).ToArray();
            var today = VnDay(new DateTimeOffset(now).ToUnixTimeMilliseconds());
            int elapsed = today < monday ? 0 : today > days[6] ? 7 : today.DayNumber - monday.DayNumber + 1;
            if (d is null)
                return new { guild = (string?)null, week = monday.ToString("yyyy-MM-dd"), days = days.Select(x => x.ToString("yyyy-MM-dd")), target = dailyTarget, elapsed, members = Array.Empty<object>(), gaps = Array.Empty<object>(), fund = (Fund?)null, polledAt = 0L };
            var weekEntries = d.Entries.Where(e => VnDay(e.At) >= monday && VnDay(e.At) <= days[6]).ToList();
            // Danh sách thành viên: bản mới nhất trong tuần (hoặc bản mới nhất có)
            var snapKey = d.Members.Keys.Where(k => DateOnly.TryParse(k, out var kd) && kd <= days[6]).OrderByDescending(k => k).FirstOrDefault()
                          ?? d.Members.Keys.OrderByDescending(k => k).FirstOrDefault();
            var roster = snapKey is null ? new List<Member>() : d.Members[snapKey];
            var names = roster.Select(r => r.Name).ToList();
            foreach (var n in weekEntries.Select(e => e.Name).Distinct()) if (!names.Contains(n)) names.Add(n);
            var rows = names.Select(n =>
            {
                var r = roster.FirstOrDefault(x => x.Name == n);
                var perDay = days.Select(x => weekEntries.Where(e => e.Name == n && e.Kind == "knb" && VnDay(e.At) == x).Sum(e => e.N)).ToArray();
                long total = perDay.Sum(), van = weekEntries.Where(e => e.Name == n && e.Kind == "van").Sum(e => e.N);
                long need = (long)dailyTarget * elapsed;
                string status = total >= dailyTarget * 7L ? "full" : total >= need ? "ok" : total > 0 ? "short" : "none";
                return new { name = n, chuc = r?.Chuc ?? "", lvl = r?.Lvl ?? 0, on = r?.On ?? 0, left = r is null, days = perDay, total, van, owed = Math.Max(0, need - total), status };
            }).ToList();
            return new
            {
                guild = d.GuildName, week = monday.ToString("yyyy-MM-dd"), days = days.Select(x => x.ToString("yyyy-MM-dd")),
                target = dailyTarget, elapsed, members = rows,
                gaps = d.Gaps.Where(x => VnDay(x.To) >= monday && VnDay(x.From) <= days[6]).Select(x => new { from = x.From, to = x.To }),
                fund = d.Fund, polledAt = d.PolledAt,
            };
        }
    }

    private string FileOf(long id) => Path.Combine(_dir, $"guild-{id}.json");

    private Data Load(long id)
    {
        if (_data is { } cur && cur.GuildId == id) return cur;
        Data d;
        try { d = File.Exists(FileOf(id)) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FileOf(id))) ?? new Data() : new Data(); }
        catch (Exception ex) when (ex is IOException or JsonException) { d = new Data(); }
        d.GuildId = id;
        return _data = d;
    }

    // Bang đọc gần nhất (một server thường chỉ theo một bang)
    private Data? LoadLatest()
    {
        if (_data is not null) return _data;
        if (!Directory.Exists(_dir)) return null;
        var f = Directory.GetFiles(_dir, "guild-*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (f is null || !long.TryParse(Path.GetFileNameWithoutExtension(f)["guild-".Length..], out var id)) return null;
        return Load(id);
    }

    private void Save(Data d)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = FileOf(d.GuildId) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, Json));
            File.Move(tmp, FileOf(d.GuildId), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
