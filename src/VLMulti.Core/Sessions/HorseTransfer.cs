using System.Text.Json;

namespace VLMulti.Core.Sessions;

// 🐴 Mã Trường: dùng chung cho bản Desktop và bản Server.
// Bấm nút → mỗi acc (đang trong game) nhận thư ngựa còn chờ, mở Rương Ngựa, cất loài mới, đọc bộ sưu tập và ngựa thừa
// → lập kế hoạch: acc nào thiếu loài mà acc khác có con thừa thì chuyển → bạn xem và bấm xác nhận
// → acc cho mở khoá (nếu cần) và gửi thư tặng (0 KNB) → acc nhận lấy thư rồi cất vào Mã Trường.
// Chỉ gửi giữa các nhân vật của bạn (trang cũng tự kiểm tra lại tên người nhận).

public sealed record HorseSpare(string Uid, string Key, string Name, string Where, bool Locked);

public sealed record HorseAccount(
    string Id, string CharName, string Result,
    int Have, int Total, int ThanHave, int ThanTotal,
    IReadOnlyList<string> Missing, IReadOnlyList<HorseSpare> Spares,
    int Opened, IReadOnlyList<string> Stored, int Mails, bool StableFull, bool BagFull)
{
    public static HorseAccount From(string id, string charName, JsonElement m)
    {
        static int Int(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        static List<string> Strings(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();

        var coll = m.TryGetProperty("coll", out var c) ? c : default;
        var spares = new List<HorseSpare>();
        if (m.TryGetProperty("spares", out var sp) && sp.ValueKind == JsonValueKind.Array)
            foreach (var s in sp.EnumerateArray())
            {
                var uid = Str(s, "uid");
                if (uid.Length == 0) continue;
                spares.Add(new HorseSpare(uid, Str(s, "key"), Str(s, "name"), Str(s, "where"), Bool(s, "locked")));
            }
        var stored = Str(m, "stored").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        return new HorseAccount(id, charName, Str(m, "r"),
            Int(coll, "have"), Int(coll, "total"), Int(coll, "thanHave"), Int(coll, "thanTotal"),
            Strings(coll, "missing"), spares,
            Int(m, "opened"), stored, Int(m, "mails"), Bool(m, "stableFull"), Bool(m, "bagFull"));
    }
}

public sealed record HorseMove(string FromId, string FromName, string ToId, string ToName, HorseSpare Horse);

public static class HorsePlanner
{
    // Mỗi loài acc nhận còn thiếu → lấy một con thừa của acc khác (ưu tiên con đang ở Hành trang, rồi con không khoá).
    // Mỗi con thừa chỉ dùng một lần; mỗi acc nhận mỗi loài một con.
    public static List<HorseMove> Plan(IReadOnlyList<HorseAccount> accounts)
    {
        var moves = new List<HorseMove>();
        var used = new HashSet<(string, string)>();
        foreach (var to in accounts)
        {
            if (string.IsNullOrWhiteSpace(to.CharName) || to.Result != "ok") continue;
            foreach (var key in to.Missing.Distinct())
            {
                var pick = accounts
                    .Where(a => a.Id != to.Id && a.Result == "ok")
                    .SelectMany(a => a.Spares.Where(s => s.Key == key && !used.Contains((a.Id, s.Uid))).Select(s => (From: a, Horse: s)))
                    .OrderBy(x => x.Horse.Where == "st")
                    .ThenBy(x => x.Horse.Locked)
                    .FirstOrDefault();
                if (pick.From is null) continue;
                used.Add((pick.From.Id, pick.Horse.Uid));
                moves.Add(new HorseMove(pick.From.Id, pick.From.CharName, to.Id, to.CharName, pick.Horse));
            }
        }
        return moves;
    }
}

// Một acc tham gia: gửi tin vào trang của acc đó (bên gọi lo khoá), ghi nhật ký của acc
public sealed record HorseMember(string Id, string CharName, Action<object> Post, Action<string> Note);

public sealed class HorseCoordinator
{
    public static TimeSpan StepTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public static TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    private const int BusyRetries = 12;

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _waits = new();
    private List<HorseAccount> _accounts = new();
    private List<HorseMove> _plan = new();
    private readonly List<string> _log = new();

    // idle · scanning · ready (có kế hoạch, chờ bạn xác nhận) · sending · done
    public string Phase { get; private set; } = "idle";
    public DateTime? UpdatedAt { get; private set; }

    public IReadOnlyList<HorseAccount> Accounts { get { lock (_gate) return _accounts.ToList(); } }
    public IReadOnlyList<HorseMove> Plan { get { lock (_gate) return _plan.ToList(); } }
    public IReadOnlyList<string> Lines { get { lock (_gate) return _log.ToList(); } }
    public bool Busy => Phase is "scanning" or "sending";

    public event Action? Changed;

    // Trang báo kết quả (gọi từ AccountController.HorseResult)
    public void OnResult(string id, JsonElement m)
    {
        TaskCompletionSource<JsonElement>? tcs;
        lock (_gate) { _waits.Remove(id, out tcs); }
        tcs?.TrySetResult(m.Clone());
    }

    // Quét tất cả acc rồi lập kế hoạch. Trả về false nếu đang bận.
    public async Task<bool> ScanAsync(IReadOnlyList<HorseMember> members, IReadOnlyList<string> ownNames, bool openChests)
    {
        if (!Begin("scanning")) return false;
        try
        {
            AddLine($"Quét {members.Count} acc{(openChests ? ", mở Rương Ngựa" : "")}…");
            var from = ownNames.ToArray();
            var results = await Task.WhenAll(members.Select(async mb =>
            {
                var m = await AskAsync(mb, new { op = "scan", openChests, from });
                var acc = m is { } e ? HorseAccount.From(mb.Id, mb.CharName, e) : Failed(mb, "timeout");
                ReportScan(mb, acc);
                return acc;
            }));
            var plan = HorsePlanner.Plan(results);
            lock (_gate) { _accounts = results.ToList(); _plan = plan; }
            AddLine(plan.Count == 0 ? "Không có ngựa nào để chuyển." : $"Có thể chuyển {plan.Count} con – xem danh sách rồi bấm Chuyển.");
            End(plan.Count > 0 ? "ready" : "done");
        }
        catch (Exception ex)
        {
            AddLine("Lỗi: " + ex.Message);
            End("done");
        }
        return true;
    }

    // Thực hiện kế hoạch đã quét (bạn đã xác nhận). Trả về false nếu không có kế hoạch hoặc đang bận.
    public async Task<bool> SendAsync(IReadOnlyList<HorseMember> members, IReadOnlyList<string> ownNames)
    {
        List<HorseMove> plan;
        lock (_gate)
        {
            if (Phase != "ready" || _plan.Count == 0) return false;
            plan = _plan.ToList();
        }
        if (!Begin("sending")) return false;
        try
        {
            var byId = members.ToDictionary(m => m.Id);
            var allowed = ownNames.ToArray();
            var sentTo = new HashSet<string>();
            // Mỗi acc cho chạy song song (trang riêng), trong một acc thì lần lượt từng người nhận
            await Task.WhenAll(plan.GroupBy(p => p.FromId).Select(async g =>
            {
                if (!byId.TryGetValue(g.Key, out var donor)) { AddLine($"{g.First().FromName}: không còn trong game – bỏ qua"); return; }
                foreach (var to in g.GroupBy(p => p.ToName))
                {
                    var uids = to.Select(p => p.Horse.Uid).ToArray();
                    var m = await AskAsync(donor, new { op = "send", to = to.Key, uids, allowed });
                    var sent = m is { } e && e.TryGetProperty("sent", out var s) ? (s.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() : new HashSet<string>();
                    var failed = m is { } e2 && e2.TryGetProperty("failed", out var f) ? f.GetString() ?? "" : "timeout";
                    var names = to.Where(p => sent.Contains(p.Horse.Uid)).Select(p => p.Horse.Name).ToList();
                    if (names.Count > 0)
                    {
                        lock (_gate) sentTo.Add(to.First().ToId);
                        var line = $"📨 {donor.CharName} → {to.Key}: {string.Join(", ", names)}";
                        AddLine(line);
                        donor.Note($"🐴 Gửi ngựa cho {to.Key}: {string.Join(", ", names)}");
                    }
                    if (sent.Count < uids.Length)
                    {
                        var miss = to.Where(p => !sent.Contains(p.Horse.Uid)).Select(p => p.Horse.Name);
                        AddLine($"⚠ {donor.CharName} → {to.Key}: không gửi được {string.Join(", ", miss)} ({Shorten(failed)})");
                        Log.Write($"[{donor.Id}] horse send to {to.Key}: failed={failed}");
                    }
                }
            }));

            // Người nhận lấy thư rồi cất vào Mã Trường
            List<string> receivers;
            lock (_gate) receivers = sentTo.ToList();
            await Task.WhenAll(receivers.Where(byId.ContainsKey).Select(async id =>
            {
                var mb = byId[id];
                var m = await AskAsync(mb, new { op = "take", from = allowed });
                var acc = m is { } e ? HorseAccount.From(mb.Id, mb.CharName, e) : Failed(mb, "timeout");
                var text = $"📥 {mb.CharName}: nhận {acc.Mails} thư";
                if (acc.Stored.Count > 0) text += $", cất {acc.Stored.Count} loài mới ({string.Join(", ", acc.Stored)})";
                if (acc.Total > 0) text += $" · bộ sưu tập {acc.Have}/{acc.Total}";
                if (acc.BagFull) text += " · ⚠ Hành trang đầy, thư còn chờ – bấm 🐴 lần sau để nhận";
                AddLine(text);
                if (acc.Mails > 0 || acc.Stored.Count > 0) mb.Note($"🐴 Nhận {acc.Mails} thư ngựa" + (acc.Stored.Count > 0 ? $", cất {string.Join(", ", acc.Stored)}" : ""));
                lock (_gate)
                {
                    var i = _accounts.FindIndex(a => a.Id == id);
                    if (i >= 0 && acc.Total > 0) _accounts[i] = _accounts[i] with { Have = acc.Have, Total = acc.Total, ThanHave = acc.ThanHave };
                }
            }));
            lock (_gate) _plan = new();
            End("done");
        }
        catch (Exception ex)
        {
            AddLine("Lỗi: " + ex.Message);
            End("done");
        }
        return true;
    }

    // Bỏ kế hoạch đang chờ
    public void Cancel()
    {
        lock (_gate)
        {
            if (Busy) return;
            _plan = new();
            Phase = "idle";
        }
        Changed?.Invoke();
    }

    private bool Begin(string phase)
    {
        lock (_gate)
        {
            if (Busy) return false;
            Phase = phase;
            if (phase == "scanning") { _log.Clear(); _accounts = new(); _plan = new(); }
            UpdatedAt = DateTime.Now;
        }
        Changed?.Invoke();
        return true;
    }

    private void End(string phase)
    {
        lock (_gate) { Phase = phase; UpdatedAt = DateTime.Now; }
        Changed?.Invoke();
    }

    private void AddLine(string line)
    {
        lock (_gate) { _log.Add(line); UpdatedAt = DateTime.Now; }
        Log.Write("horse: " + line);
        Changed?.Invoke();
    }

    private void ReportScan(HorseMember mb, HorseAccount acc)
    {
        if (acc.Result != "ok")
        {
            AddLine($"⚠ {Name(mb)}: không đọc được Mã Trường ({acc.Result})");
            return;
        }
        var text = $"{Name(mb)}: bộ sưu tập {acc.Have}/{acc.Total}, thừa {acc.Spares.Count} con";
        if (acc.Opened > 0) text += $" · mở {acc.Opened} Rương Ngựa";
        if (acc.Stored.Count > 0) text += $" · cất loài mới: {string.Join(", ", acc.Stored)}";
        if (acc.Mails > 0) text += $" · nhận {acc.Mails} thư ngựa";
        if (acc.StableFull) text += " · ⚠ Mã Trường đầy";
        if (acc.BagFull) text += " · ⚠ Hành trang đầy";
        AddLine(text);
        if (acc.Opened > 0 || acc.Stored.Count > 0 || acc.Mails > 0)
            mb.Note("🐴 " + string.Join(" · ", new[]
            {
                acc.Opened > 0 ? $"mở {acc.Opened} Rương Ngựa" : null,
                acc.Stored.Count > 0 ? $"cất {string.Join(", ", acc.Stored)} vào Mã Trường" : null,
                acc.Mails > 0 ? $"nhận {acc.Mails} thư ngựa" : null,
            }.Where(x => x is not null)));
    }

    private static string Name(HorseMember mb) => string.IsNullOrWhiteSpace(mb.CharName) ? mb.Id : mb.CharName;

    private static HorseAccount Failed(HorseMember mb, string r) =>
        new(mb.Id, mb.CharName, r, 0, 0, 0, 0, Array.Empty<string>(), Array.Empty<HorseSpare>(), 0, Array.Empty<string>(), 0, false, false);

    private static string Shorten(string s) => s.Length <= 120 ? s : s[..120] + "…";

    // Gửi một việc cho trang và chờ kết quả; trang đang bận việc khác thì chờ rồi thử lại
    private async Task<JsonElement?> AskAsync(HorseMember mb, object arg)
    {
        for (var i = 0; i < BusyRetries; i++)
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _waits[mb.Id] = tcs;
            mb.Post(new { t = "horse", arg });
            var done = await Task.WhenAny(tcs.Task, Task.Delay(StepTimeout));
            if (done != tcs.Task)
            {
                lock (_gate) _waits.Remove(mb.Id);
                return null;
            }
            var m = tcs.Task.Result;
            var r = m.TryGetProperty("r", out var rv) ? rv.GetString() : null;
            if (r is "busy") { await Task.Delay(BusyRetryDelay); continue; }
            return m;
        }
        return null;
    }
}
