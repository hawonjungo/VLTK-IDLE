using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using VLMulti.Core;
using VLMulti.Core.Accounts;
using VLMulti.Core.Alerts;
using VLMulti.Core.GameData;
using VLMulti.Core.Sessions;
using VLMulti.Core.Settings;

namespace VLMulti.Server;

// Toàn bộ bản Server: trình duyệt, các acc, bộ giám sát, cảnh báo, cài đặt.
// Thứ tự khoá: GameSession.Gate rồi mới tới _gate (sự kiện của acc chạy trong Gate và gọi vào đây).
public sealed class SessionManager : IAsyncDisposable
{
    private static readonly TimeSpan SaveStateEvery = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly ServerOptions _options;
    private readonly AppSettings _settings;
    private readonly CredentialStore _credentials;
    private readonly AlertCenter _alerts = new(TimeSpan.FromMinutes(10));
    private readonly Dictionary<string, GameSession> _sessions = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1); // bật/tắt acc lần lượt
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private string _initScript = "";
    private bool _settingsDirty;
    private DateTime _lastStateSave;
    // Xem trực tiếp: chỉ một acc mỗi lúc. Mở acc khác thì phiên đang xem bị dừng.
    private readonly SemaphoreSlim _liveLock = new(1, 1);
    private CancellationTokenSource? _liveCts;
    private string? _liveId;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public SessionManager(ServerOptions options)
    {
        _options = options;
        _settings = SettingsStore.Load(AppPaths.SettingsFile);
        _credentials = new CredentialStore(AppPaths.CredentialsFile, new AesKeyFileProtector(Path.Combine(AppPaths.DataDir, "secret.key")));
        _alerts.Raised += a => Log.Write($"ALERT [{a.AccountId}] {a.Message}");
    }

    public ServerOptions Options => _options;

    public async Task StartAsync()
    {
        _playwright = await Playwright.CreateAsync();
        // Gỡ lỗi: đặt biến môi trường VLMULTI_DEBUG_PORT (ví dụ 9333) để xem trang game qua DevTools (chỉ máy này truy cập)
        var debugArgs = int.TryParse(Environment.GetEnvironmentVariable("VLMULTI_DEBUG_PORT"), out var debugPort)
            ? new[] { $"--remote-debugging-port={debugPort}", "--remote-debugging-address=127.0.0.1" }
            : Array.Empty<string>();
        _browser = await _playwright.Chromium.LaunchAsync(new() { Headless = _options.Headless, Args = debugArgs });
        _initScript = ServerScripts.Build(_options.MaxFps);
        Log.Write($"server started: {_options.ViewportWidth}x{_options.ViewportHeight}, fps {_options.MaxFps}");

        if (_options.Paused) Log.Write("server paused: game sessions not started");
        else
            foreach (var acc in _settings.Accounts.Where(a => a.Enabled))
                await StartSessionAsync(acc, TimeSpan.FromSeconds(Sessions.Count * 6));

        _loopCts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    private List<GameSession> Sessions
    {
        get { lock (_gate) return _sessions.Values.ToList(); }
    }

    private async Task StartSessionAsync(AccountSlot acc, TimeSpan loginStagger)
    {
        var session = new GameSession(acc, _options);
        var c = session.Controller;
        c.AutoRecover = _settings.AutoRecover;
        c.IdleAlertAfter = TimeSpan.FromMinutes(_settings.IdleAlertMinutes);
        c.LoginStagger = loginStagger;
        c.SettingsProvider = () => _settings;
        c.CredentialProvider = id => { lock (_gate) return _credentials.Get(id); };
        c.Problem += (key, message) => Raise(key, acc, message);
        // Tên nhân vật, môn phái mới → lưu cài đặt (tiền/cấp đổi liên tục thì không cần lưu)
        var saved = (acc.CharName, acc.Sect);
        c.InfoChanged += () =>
        {
            if (saved == (acc.CharName, acc.Sect)) return;
            saved = (acc.CharName, acc.Sect);
            lock (_gate) _settingsDirty = true;
        };
        c.LootFound += (src, text) => OnLoot(acc, src, text);

        lock (_gate) _sessions[acc.Id] = session;
        try
        {
            await session.StartAsync(_browser!, _settings.GameUrl, _initScript);
        }
        catch (Exception ex)
        {
            session.Locked(ctl => ctl.OnInitFailed(ex.Message));
        }
    }

    // ---------- Vòng lặp nền: giám sát mỗi 3 giây, lưu dữ liệu ----------

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Core.Sessions.AccountController.SuperviseInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var now = DateTime.Now;
            var sessions = Sessions;
            foreach (var s in sessions) s.Locked(c => c.Tick(now));

            if (now - _lastStateSave > SaveStateEvery)
            {
                _lastStateSave = now;
                foreach (var s in sessions) s.RequestSaveState();
            }

            bool save;
            lock (_gate) { save = _settingsDirty; _settingsDirty = false; }
            if (save) SaveSettings();
        }
    }

    // ---------- Cảnh báo & đồ phổ ----------

    private void Raise(string key, AccountSlot acc, string message)
    {
        lock (_gate) _alerts.Raise(key, acc.Id, acc.DisplayName, message, DateTime.Now);
    }

    private void OnLoot(AccountSlot source, string src, string text)
    {
        AccountSlot? owner;
        string name;
        lock (_gate)
        {
            owner = LootParser.OwnerOf(_settings.Accounts, source, src, text);
            if (owner is null) return; // đồ của người chơi khác
            name = LootParser.DoPhoNameOf(text) ?? text;
            // Cùng một thông báo đến từ nhiều acc → chỉ tính một lần
            if (!_alerts.Raise("loot:" + text, owner.Id, owner.DisplayName, $"🎉 Đồ phổ: {name}", DateTime.Now)) return;
            Log.Write($"LOOT [{owner.Id}] {text}");
            owner.LastDoPho = name;
            owner.LastDoPhoAt = DateTime.Now;
            _settingsDirty = true;
        }
        // Ghi vào nhật ký của acc nhặt được. Chạy riêng: đang trong khoá của acc báo tin, khoá thêm acc khác
        // ngay tại đây có thể kẹt nhau khi nhiều acc cùng báo một thông báo hệ thống.
        if (Find(owner.Id) is { } session)
            _ = Task.Run(() => session.Locked(c => c.Note(DateTime.Now, $"🎉 Đồ phổ: {name}", StatusLevel.Ok)));
    }

    // ---------- Trạng thái cho trang web ----------

    public object Snapshot()
    {
        var now = DateTime.Now;
        string? liveId;
        lock (_gate) liveId = _liveId;
        var sessions = Sessions.ToDictionary(s => s.Account.Id);
        var rows = _settings.Accounts.Select(acc =>
        {
            if (!sessions.TryGetValue(acc.Id, out var s))
                return (object)new { id = acc.Id, name = acc.DisplayName, sect = acc.Sect, enabled = false, lastDoPho = acc.LastDoPho, lastDoPhoAt = acc.LastDoPhoAt };

            lock (s.Gate)
            {
                var c = s.Controller;
                var st = c.Stats;
                var (health, healthReason) = c.HealthAt(now);
                return new
                {
                    id = acc.Id,
                    name = acc.DisplayName,
                    sect = acc.Sect,
                    enabled = true,
                    selected = acc.Selected,
                    kite = acc.Kite,
                    autoMode = acc.AutoMode,
                    hasPassword = HasPassword(acc.Id),
                    savedUser = SavedUser(acc.Id),
                    screen = c.Screen,
                    screenLabel = c.ScreenLabel,
                    status = c.StatusText,
                    statusLevel = c.StatusLevel.ToString().ToLowerInvariant(),
                    info = c.InfoLine,
                    level = st.Level,
                    gold = st.Gold is { } g ? MoneyParser.Format(g) : null,
                    goldValue = st.Gold,
                    knb = st.Knb,
                    knbLocked = c.KnbLocked,
                    daTau = c.DaTauToday,
                    daTauMax = c.DaTauMaxPerDay,
                    ldtHave = c.LdtHave,
                    ldtUsed = c.LdtUsed,
                    ldtBuy = c.LdtBuy,
                    phoBan = c.PhoBan,
                    mode = c.Maintenance ? "🛠 Bảo trì" : c.ModeText,
                    modeCode = c.ModeCode,
                    tower = c.TowerFloor,
                    ratePerHour = st.GoldPerHour(now) is { } r ? MoneyParser.Format((long)r) : null,
                    floorsPerHour = c.Tower.FloorsPerHour(now) is { } fr ? (int?)Math.Round(fr) : null,
                    towerFalls = c.Tower.Falls,
                    health = health.ToString().ToLowerInvariant(),
                    healthReason,
                    idleSeconds = c.LastActivityAt == default ? (int?)null : (int)(now - c.LastActivityAt).TotalSeconds,
                    bagUsed = c.BagUsed,
                    bagMax = c.BagMax,
                    bagFull = c.BagNearlyFull,
                    exp = c.Exp,
                    doPho = c.DoPhoHeld,
                    bossHk = c.BossHk,
                    assassin = c.Assassin,
                    maintenance = c.Maintenance,
                    boat = c.Boat,
                    weekBoss = c.WeekBoss.Replace(" claim", ""),
                    autoChain = acc.AutoMode && c.Screen == "game"
                        ? c.AutoChain(now).Select(x => new { mode = x.Mode, label = x.Label, state = x.State, detail = x.Detail }).ToList()
                        : null,
                    autoCheckedAgo = c.LastAutoCheckAt == default ? (int?)null : (int)(now - c.LastAutoCheckAt).TotalSeconds,
                    tracker = c.Tracker,
                    activity = c.Activity.Items.Take(8).Select(ActivityJson).ToList(),
                    live = liveId == acc.Id,
                    levelsGained = st.LevelsGained,
                    lastDoPho = acc.LastDoPho,
                    lastDoPhoAt = acc.LastDoPhoAt,
                };
            }
        }).ToList();

        lock (_gate)
        {
            return new
            {
                accounts = rows,
                autoRecover = _settings.AutoRecover,
                paused = _options.Paused,
                alerts = _alerts.Items.Take(50).Select(a => new { at = a.At, account = a.AccountName, message = a.Message }),
                live = liveId,
                settings = new
                {
                    kiteDirection = _settings.KiteDirection,
                    kiteDistance = _settings.KiteDistance,
                    kiteInterval = _settings.KiteIntervalSeconds,
                    kiteDirections = KiteDirections.All.Select(d => new { id = d, label = KiteDirections.Label(d) }),
                    autoDungeon = _settings.AutoModeDungeon,
                    autoQuest = _settings.AutoModeQuest,
                    autoAssassin = _settings.AutoModeAssassin,
                    autoBoat = _settings.AutoModeBoat,
                    autoWeekBoss = _settings.AutoModeWeekBoss,
                    autoFallback = _settings.AutoModeFallback,
                    towerRest = _settings.TowerRestMinutes,
                },
                server = new { viewport = $"{_options.ViewportWidth}x{_options.ViewportHeight}", fps = _options.MaxFps },
            };
        }
    }

    private bool HasPassword(string id)
    {
        lock (_gate) return _credentials.Has(id);
    }

    // Tên tài khoản game đã lưu (không giải mã mật khẩu), để hộp 🔑 hiện rõ acc nào đã lưu
    private string? SavedUser(string id)
    {
        lock (_gate) return _credentials.UserOf(id);
    }

    private static object ActivityJson(ActivityEntry e) =>
        new { at = e.At, text = e.Text, level = e.Level.ToString().ToLowerInvariant() };

    // Toàn bộ nhật ký hoạt động của một acc (200 dòng gần nhất)
    public object? ActivityOf(string id)
    {
        if (Find(id) is not { } s) return null;
        lock (s.Gate) return s.Controller.Activity.Items.Select(ActivityJson).ToList();
    }

    public async Task<byte[]?> SnapshotOfAsync(string id) =>
        Find(id) is { } s ? await s.SnapshotAsync() : null;

    // ---------- Lệnh từ trang web ----------

    private GameSession? Find(string id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id);
    }

    private AccountSlot? AccountOf(string id) => _settings.Accounts.FirstOrDefault(a => a.Id == id);

    public bool Click(string id, double x, double y)
    {
        if (Find(id) is not { } s) return false;
        s.Click(x, y);
        return true;
    }

    // ---------- 📺 Xem trực tiếp ----------

    // Tin từ trình duyệt người xem: {"t":"click","x":0.5,"y":0.5} | {"t":"wheel","x","y","dy"} | {"t":"key","key":"Escape"} | {"t":"ping"}
    private sealed record LiveInput(string T, double X, double Y, double Dy, string? Key);
    private static readonly JsonSerializerOptions LiveJson = new() { PropertyNameCaseInsensitive = true };

    public async Task RunLiveAsync(string id, WebSocket ws, CancellationToken aborted)
    {
        if (Find(id) is not { } session)
        {
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Acc không chạy", CancellationToken.None);
            return;
        }

        // Dừng phiên xem đang có (acc khác hoặc tab khác) rồi mới bắt đầu
        var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        lock (_gate)
        {
            _liveCts?.Cancel();
            _liveCts = cts;
        }
        await _liveLock.WaitAsync(CancellationToken.None);
        var idleLimit = TimeSpan.FromMinutes(_options.LiveIdleMinutes);
        using var idle = new CancellationTokenSource(idleLimit);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, idle.Token);
        var send = new SemaphoreSlim(1, 1); // WebSocket chỉ cho một lần gửi mỗi lúc
        string endReason = "Đã đóng";
        try
        {
            if (cts.IsCancellationRequested) return;
            lock (_gate) _liveId = id;
            lock (session.Gate) session.Controller.Watched = true; // đang xem: không tự tải lại acc này

            async Task SendAsync(ArraySegment<byte> data, WebSocketMessageType type)
            {
                await send.WaitAsync(stop.Token);
                try { await ws.SendAsync(data, type, true, stop.Token); }
                finally { send.Release(); }
            }

            await session.StartLiveAsync(frame => SendAsync(frame, WebSocketMessageType.Binary));
            var info = JsonSerializer.Serialize(new { t = "info", width = _options.LiveWidth, height = _options.LiveHeight, idleMinutes = _options.LiveIdleMinutes });
            await SendAsync(Encoding.UTF8.GetBytes(info), WebSocketMessageType.Text);

            var buffer = new byte[4096];
            while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, stop.Token);
                if (r.MessageType == WebSocketMessageType.Close) { endReason = "Trình duyệt đóng kết nối"; break; }
                if (r.MessageType != WebSocketMessageType.Text || !r.EndOfMessage) continue;
                LiveInput? input;
                try { input = JsonSerializer.Deserialize<LiveInput>(buffer.AsSpan(0, r.Count), LiveJson); }
                catch (JsonException) { continue; }
                if (input is null) continue;
                if (input.T != "ping") idle.CancelAfter(idleLimit); // có thao tác → tính lại thời gian chờ
                switch (input.T)
                {
                    case "click": session.Click(input.X, input.Y); break;
                    case "wheel": session.Wheel(input.X, input.Y, input.Dy); break;
                    case "key" when input.Key is { } key: session.Key(key); break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            endReason = idle.IsCancellationRequested ? $"Tự tắt sau {_options.LiveIdleMinutes} phút không thao tác"
                : aborted.IsCancellationRequested ? "Đã đóng" : "Đang xem acc khác";
        }
        catch (Exception ex)
        {
            endReason = "Lỗi: " + ex.Message.Split('\n')[0];
            Log.Write($"[{id}] live view error: {ex}");
        }
        finally
        {
            lock (_gate)
            {
                if (_liveId == id) _liveId = null;
                if (_liveCts == cts) _liveCts = null;
            }
            lock (session.Gate) session.Controller.Watched = false;
            Log.Write($"[{id}] live view ended: {endReason} (kết nối: {ws.State})");
            try { await session.StopLiveAsync(); } catch (InvalidOperationException) { }
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    var end = JsonSerializer.Serialize(new { t = "end", reason = endReason });
                    await ws.SendAsync(Encoding.UTF8.GetBytes(end), WebSocketMessageType.Text, true, CancellationToken.None);
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch (WebSocketException) { }
            }
            cts.Dispose();
            _liveLock.Release();
        }
    }

    public bool Reload(string id)
    {
        if (Find(id) is not { } s) return false;
        s.Locked(c => c.ManualReload());
        return true;
    }

    public bool ToggleKite(string id)
    {
        if (Find(id) is not { } s) return false;
        s.Locked(c => c.SetKite(!c.Account.Kite, DateTime.Now));
        SaveSettings();
        return true;
    }

    public bool ToggleAutoMode(string id)
    {
        if (Find(id) is not { } s) return false;
        s.Locked(c => c.SetAutoMode(!c.Account.AutoMode, DateTime.Now));
        SaveSettings();
        return true;
    }

    public bool SetSelected(string id, bool selected)
    {
        if (AccountOf(id) is not { } acc) return false;
        acc.Selected = selected;
        SaveSettings();
        return true;
    }

    // Đổi chế độ cho các acc đang được chọn (☑)
    public int Mode(string code)
    {
        var targets = Sessions.Where(s => s.Account.Selected).ToList();
        foreach (var s in targets) s.Locked(c => c.RequestMode(code, DateTime.Now));
        SaveSettings(); // nhớ acc nào bạn chọn Leo Tháp
        return targets.Count;
    }

    public void EnterAll()
    {
        foreach (var s in Sessions) s.Locked(c => c.RunCommand("enter"));
    }

    public void SetAutoRecover(bool on)
    {
        _settings.AutoRecover = on;
        foreach (var s in Sessions) s.Locked(c => c.AutoRecover = on);
        SaveSettings();
    }

    public bool Paused => _options.Paused;

    // Tạm dừng: đóng game của mọi acc (lưu phiên đăng nhập trước khi đóng) để nhường acc cho máy khác.
    // Chạy lại: mở lại các acc đang bật, đăng nhập lệch nhau 6 giây như lúc khởi động.
    public async Task SetPausedAsync(bool paused)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (paused == _options.Paused) return;
            _options.Paused = paused;
            _options.Save();
            if (paused)
            {
                List<GameSession> closing;
                lock (_gate)
                {
                    closing = _sessions.Values.ToList();
                    _sessions.Clear();
                    _liveCts?.Cancel();
                }
                foreach (var s in closing) await s.DisposeAsync();
                Log.Write("server paused: all game sessions closed");
            }
            else
            {
                foreach (var acc in _settings.Accounts.Where(a => a.Enabled))
                    await StartSessionAsync(acc, TimeSpan.FromSeconds(Sessions.Count * 6));
                Log.Write("server resumed");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<string?> SetEnabledAsync(string id, bool enabled)
    {
        if (AccountOf(id) is not { } acc) return "Không có acc này";
        await _lifecycle.WaitAsync();
        try
        {
            if (_options.Paused) return "Server đang tạm dừng – bấm Chạy lại trước";
            if (enabled)
            {
                if (Find(id) is not null) return null;
                acc.Enabled = true;
                SaveSettings();
                await StartSessionAsync(acc, TimeSpan.Zero);
                Log.Write($"[{id}] enabled");
                return null;
            }

            GameSession? session;
            lock (_gate)
            {
                if (_sessions.Count <= 1) return "Phải còn ít nhất 1 acc đang chạy";
                if (!_sessions.Remove(id, out session)) return null;
            }
            acc.Enabled = false;
            SaveSettings();
            await session.DisposeAsync();
            Log.Write($"[{id}] disabled");
            return null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public bool SetCredentials(string id, string user, string password)
    {
        if (AccountOf(id) is null || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) return false;
        lock (_gate) _credentials.Set(id, user.Trim(), password);
        Find(id)?.Locked(c => c.ResetLoginState());
        Log.Write($"[{id}] password saved for {user.Trim()}");
        return true;
    }

    public void ClearAlerts()
    {
        lock (_gate) _alerts.Clear();
    }

    public void UpdateSettings(SettingsUpdate u)
    {
        lock (_gate)
        {
            if (u.KiteDirection is { } d) _settings.KiteDirection = d;
            if (u.KiteDistance is { } dist) _settings.KiteDistance = dist;
            if (u.KiteInterval is { } iv) _settings.KiteIntervalSeconds = iv;
            if (u.AutoDungeon is { } ad) _settings.AutoModeDungeon = ad;
            if (u.AutoQuest is { } aq) _settings.AutoModeQuest = aq;
            if (u.AutoAssassin is { } aa) _settings.AutoModeAssassin = aa;
            if (u.AutoBoat is { } ab) _settings.AutoModeBoat = ab;
            if (u.AutoWeekBoss is { } aw) _settings.AutoModeWeekBoss = aw;
            if (u.AutoFallback is { } af) _settings.AutoModeFallback = af;
            if (u.TowerRest is { } tr) _settings.TowerRestMinutes = tr;
        }
        SaveSettings();
    }

    private void SaveSettings()
    {
        lock (_gate)
        {
            _settings.Normalize();
            SettingsStore.Save(AppPaths.SettingsFile, _settings);
        }
    }

    // Nhập mật khẩu hàng loạt (lệnh --import-credentials), dùng khi chuyển từ bản Desktop sang
    public static int ImportCredentials(IEnumerable<(string Id, string User, string Password)> items)
    {
        var store = new CredentialStore(AppPaths.CredentialsFile, new AesKeyFileProtector(Path.Combine(AppPaths.DataDir, "secret.key")));
        int n = 0;
        foreach (var (id, user, pass) in items)
        {
            store.Set(id, user, pass);
            n++;
        }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        foreach (var s in Sessions) await s.DisposeAsync(); // lưu dữ liệu trình duyệt rồi đóng
        SaveSettings();
        if (_browser is not null) await _browser.CloseAsync();
        _playwright?.Dispose();
        Log.Write("server stopped");
    }
}

public sealed record SettingsUpdate(
    string? KiteDirection, int? KiteDistance, int? KiteInterval,
    bool? AutoDungeon, bool? AutoQuest, string? AutoFallback, int? TowerRest, bool? AutoAssassin = null,
    bool? AutoBoat = null, bool? AutoWeekBoss = null);
