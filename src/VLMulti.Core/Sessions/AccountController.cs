using System.Text.Json;
using System.Text.RegularExpressions;
using VLMulti.Core.GameData;
using VLMulti.Core.Recovery;
using VLMulti.Core.Settings;

namespace VLMulti.Core.Sessions;

// Trình duyệt đang chạy trang game của một acc (WebView2 ở bản Desktop, Playwright ở bản Server)
public interface IGamePage
{
    // Gửi tin (JSON) vào trang cho agent.js
    void Post(string json);
    void Reload();
}

public enum StatusLevel { Info, Ok, Warn, Error, Notice }

public enum Health { Good, Warn, Bad }

// "Bộ não" của một acc, dùng chung cho bản Desktop và bản Server: đọc trạng thái từ agent.js,
// tự đăng nhập/vào game/tự hồi phục, kéo quái, luật tự đổi chế độ, cảnh báo.
// Không an toàn đa luồng: bên gọi phải gọi tuần tự (Desktop: luồng giao diện; Server: khoá theo acc).
public sealed class AccountController
{
    // Ngưỡng của bộ giám sát
    public static readonly TimeSpan SuperviseInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ScreenSettle = TimeSpan.FromSeconds(3);      // chờ màn hình ổn định rồi mới hành động
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(20);     // sau bấm đăng nhập mà vẫn ở màn đăng nhập → coi là thất bại
    private static readonly TimeSpan KickedLoginDelay = TimeSpan.FromSeconds(60); // bị đẩy ra: chờ trước khi đăng nhập lại
    // Trang không báo trạng thái quá lâu → coi là treo. Để rộng tay vì tải lại là phải đăng nhập lại.
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CrashReloadDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NavFailRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ManualLoginWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StatsSettle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NetErrorRetryDelay = TimeSpan.FromSeconds(60);
    // Thông báo của game cho thấy lỗi kết nối (không phải sai mật khẩu)
    private static readonly Regex NetErrorPattern = new(@"fetch|network|mạng|kết nối|time ?out|50[234]", RegexOptions.IgnoreCase);
    private static readonly Regex TowerFloorPattern = new(@"Tháp.*?tầng\s*(\d+)");
    private static readonly Regex BagPattern = new(@"^(\d+)\s*/\s*(\d+)$");

    // Đèn sức khoẻ trên trang web
    private static readonly TimeSpan HealthOutOfGame = TimeSpan.FromMinutes(3); // ngoài game lâu hơn → đỏ
    private static readonly TimeSpan HealthNoStatus = TimeSpan.FromSeconds(60); // trang im lặng lâu hơn → đỏ
    private static readonly TimeSpan HealthIdle = TimeSpan.FromMinutes(5);      // không tiến triển lâu hơn → vàng
    private static readonly TimeSpan NoPasswordAlertAfter = TimeSpan.FromSeconds(30);
    private const int BagWarnFree = 5;    // hòm còn ≤ 5 ô trống → cảnh báo
    private const int TowerLogEvery = 10; // ghi nhật ký mỗi 10 tầng

    // Luật tự đổi chế độ
    private static readonly TimeSpan AutoModeCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AutoModeMinGap = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AutoModeWarmup = TimeSpan.FromSeconds(60);   // chờ đọc xong số lượt
    private static readonly TimeSpan ModeBlockDefault = TimeSpan.FromMinutes(60); // game tự đẩy ra → tạm bỏ qua
    private static readonly TimeSpan UserModeWindow = TimeSpan.FromSeconds(60);   // đổi chế độ trong lúc này coi là do bạn bấm
    private static readonly TimeSpan TowerReturnDelay = TimeSpan.FromSeconds(20); // chết trong tháp → chờ hồi sinh rồi vào lại
    private static readonly TimeSpan TowerCmdGap = TimeSpan.FromSeconds(60);

    private readonly IGamePage _page;
    private readonly AttemptLimiter _loginLimiter = new(6, TimeSpan.FromMinutes(30));
    private readonly AttemptLimiter _enterLimiter = new(3, TimeSpan.FromMinutes(10));
    private readonly AttemptLimiter _reloadLimiter = new(3, TimeSpan.FromMinutes(15));
    private readonly Dictionary<string, DateTime> _modeBlockedUntil = new();

    private bool _capture;

    // Trạng thái trang
    private bool _pageLoaded;
    private DateTime _lastStatusAt;
    private DateTime _screenSince;
    private DateTime? _crashedAt;
    private DateTime? _navFailedAt;

    // Đăng nhập tự động
    private DateTime? _loginSentAt;
    private DateTime _loginNotBefore;
    private int _loginFails;
    private bool _loginBlocked;
    private string _loginMsg = "";
    private (string User, string Pass, DateTime At)? _manualLogin;

    // Luật tự đổi chế độ
    private DateTime _lastAutoModeCheck;
    private DateTime _lastAutoModeCmd;
    private string? _pendingAutoMode;
    private DateTime _userModeAt;

    // Leo tháp: lúc vào tháp (để biết sống được bao lâu), các lần chết nhanh gần đây, lúc vào lại
    private DateTime _towerEnteredAt;
    private readonly List<DateTime> _fastDeaths = new();
    private DateTime _towerReturnAt;
    private DateTime _lastTowerCmd;

    // Mua Lệnh Bài Dã Tẩu trước khi vào Nhiệm Vụ
    private const int LdtDailyBuyMax = 100;
    private static readonly TimeSpan LdtBuyTimeout = TimeSpan.FromMinutes(3);
    private DateTime? _ldtBuyingSince;
    private DateTime? _ldtTriedDay;
    private bool _questByRule;

    // Kéo quái
    private DateTime _lastKiteAt;
    private int _kiteStep;
    private int _kiteTries;
    private string _lastKiteResult = "";

    // Lần gần nhất có sự kiện (dùng cho cảnh báo không có thời điểm riêng)
    private DateTime _lastNow;
    private int? _lastTowerFloor;
    private bool _doPhoRead; // tầng cuối cùng còn đứng trong tháp (để ghi "ngã ở tầng N")

    public AccountController(AccountSlot account, IGamePage page)
    {
        Account = account;
        _page = page;
    }

    public AccountSlot Account { get; }
    public AccountStats Stats { get; } = new();
    public string Screen { get; private set; } = "loading";
    public string ModeText { get; private set; } = "";
    // Mã chế độ đang chạy: farm / dg / quest / tower
    public string ModeCode { get; private set; } = "";
    // Số lượt đọc từ bảng Nhiệm vụ của game: Dã Tẩu "đã làm/giới hạn hôm nay", Phó bản "còn/tối đa"
    public string DaTau { get; private set; } = "";
    public string PhoBan { get; private set; } = "";
    // Lệnh Bài Dã Tẩu: đang có, đã dùng hôm nay ("35/160"), đã mua hôm nay ở Kỳ Trân Các ("55/100");
    // và số nhiệm vụ Dã Tẩu tối đa mỗi ngày (200)
    public string LdtHave { get; private set; } = "";
    public string LdtUsed { get; private set; } = "";
    public string LdtBuy { get; private set; } = "";
    public string DaTauMaxPerDay { get; private set; } = "";
    public long? KnbLocked { get; private set; }
    // Dã Tẩu chỉ phần "đã làm/giới hạn hôm nay", bỏ ghi chú "(+34 lệnh bài)" của game cho khỏi hiểu nhầm
    public string DaTauToday => ModeRules.TryFraction(DaTau, out var a, out var b) ? $"{a}/{b}" : "";
    // Tầng Thông Thiên Tháp đang đứng (đọc từ tên bản đồ "Thông Thiên Tháp · tầng 632"), null khi ngoài tháp
    public int? TowerFloor => Tower.Floor;
    public TowerTracker Tower { get; } = new();
    // Hòm đồ: số ô đã dùng / tổng số ô (null khi chưa đọc được)
    public int? BagUsed { get; private set; }
    public int? BagMax { get; private set; }
    public bool BagNearlyFull => BagUsed is { } u && BagMax is { } m && m - u <= BagWarnFree;
    // Kinh nghiệm của cấp hiện tại ("92.5%") và nhiệm vụ đang theo dấu ("Dã Tẩu chuỗi 237 · Săn 32 … 0/32")
    public string Exp { get; private set; } = "";
    public string Tracker { get; private set; } = "";
    public ActivityLog Activity { get; } = new();
    // Boss Hoàng Kim: dòng trạng thái của game ("Đã hạ Boss đợt này. Đợt sau: 22:00"); đang xuất hiện = không phải dòng chờ
    public string BossHk { get; private set; } = "";
    // Lệnh Bài Sát Thủ đang có / số Sát Thủ đã hạ, ví dụ "10/60"
    public string Assassin { get; private set; } = "";
    // Chuyến thuyền còn hôm nay "2/3"; lượt Boss tuần còn "3/3" (+ " claim" khi nút Nhận rương tuần sáng)
    public string Boat { get; private set; } = "";
    public string WeekBoss { get; private set; } = "";
    public int BoatLeft => ModeRules.TryFraction(Boat, out var left, out _) ? left : 0;
    public int WeekBossLeft => ModeRules.TryFraction(WeekBoss, out var left, out _) ? left : 0;
    public bool WeekClaimable => WeekBoss.EndsWith(" claim");
    // Đồ phổ đang giữ trong Hành trang và Rương, ví dụ "Ngự Long Lượng Ngân Bảo Đao (rương)"
    public IReadOnlyList<string> DoPhoHeld { get; private set; } = Array.Empty<string>();

    // Dòng trạng thái ngắn hiện trên khung / trang web
    public string StatusText { get; private set; } = "Đang khởi tạo";
    public StatusLevel StatusLevel { get; private set; } = StatusLevel.Info;

    // Do bên chạy cấp: có bật tự hồi phục không, ngưỡng đứng yên, mật khẩu đã lưu, cài đặt chung
    public bool AutoRecover { get; set; } = true;
    public TimeSpan IdleAlertAfter { get; set; } = TimeSpan.FromMinutes(15);
    public Func<string, (string User, string Password)?>? CredentialProvider { get; set; }
    // Mỗi acc chờ thêm một khoảng khác nhau trước khi tự đăng nhập, tránh nhiều acc gửi cùng lúc
    public TimeSpan LoginStagger { get; set; }
    public Func<AppSettings>? SettingsProvider { get; set; }

    // Tên/cấp/tiền/chế độ/màn hình thay đổi
    public event Action? InfoChanged;
    public event Action? StatusChanged;
    // (mã loại, nội dung) – đưa vào trung tâm cảnh báo
    public event Action<string, string>? Problem;
    // Bạn tự đăng nhập thành công → hỏi có lưu mật khẩu không. Bên chạy tự chuyển sang luồng phù hợp.
    public event Action<string, string>? ManualLoginSucceeded;
    // Thao tác thật của người chơi (chỉ khi đang bật bắt thao tác để đồng bộ)
    public event Action<JsonElement>? InputCaptured;
    // Thông báo Đồ phổ: (nguồn "system"/"pickup", nội dung)
    public event Action<string, string>? LootFound;
    // Có dòng mới trong nhật ký hoạt động
    public event Action<ActivityEntry>? ActivityAdded;

    public string ScreenLabel => Screen switch
    {
        "login" => "Màn đăng nhập",
        "charselect" => "Màn chọn nhân vật",
        "game" => "Trong game",
        _ => "Đang tải",
    };

    // Dòng thông tin ngắn: "Lv 36 · 2,9 vạn · 608 KNB · Dã Tẩu 83/84 · PB 2/2 · Tháp tầng 1067 · 🗼 Leo Tháp"
    public string InfoLine
    {
        get
        {
            if (Screen != "game") return ScreenLabel;
            var parts = new List<string>();
            if (Stats.Level is { } lv) parts.Add($"Lv {lv}");
            if (Stats.Gold is { } gold) parts.Add(MoneyParser.Format(gold));
            if (Stats.Knb is { } knb) parts.Add($"{knb} KNB");
            if (!string.IsNullOrEmpty(DaTauToday)) parts.Add($"Dã Tẩu {DaTauToday}");
            if (!string.IsNullOrEmpty(PhoBan)) parts.Add($"PB {PhoBan}");
            if (TowerFloor is { } floor) parts.Add($"Tháp tầng {floor}");
            if (!string.IsNullOrEmpty(ModeText)) parts.Add(ModeText);
            return string.Join(" · ", parts);
        }
    }

    // Lần cuối acc có tiến triển (tiền, cấp, tầng tháp đổi)
    public DateTime LastActivityAt => Stats.LastProgressAt;

    // Đèn sức khoẻ: Bad (đỏ) = cần bạn xem ngay, Warn (vàng) = đáng để ý, Good (xanh) = đang chạy bình thường
    public (Health Level, string Reason) HealthAt(DateTime now)
    {
        if (_loginBlocked) return (Health.Bad, "Tự đăng nhập thất bại – kiểm tra mật khẩu");
        if (Screen == "login" && Account.AutoLogin && CredentialProvider?.Invoke(Account.Id) is null)
            return (Health.Bad, "Bị đăng xuất, chưa lưu mật khẩu game (🔑)");
        if (_pageLoaded && _lastStatusAt != default && now - _lastStatusAt > HealthNoStatus) return (Health.Bad, "Trang game không phản hồi");
        if (Away) return (Health.Warn, "📱 Tài khoản đang mở ở nơi khác");
        if (Screen != "game")
            return StatusLevel == StatusLevel.Error ? (Health.Bad, StatusText)
                : now - _screenSince > HealthOutOfGame
                ? (Health.Bad, $"Không vào được game ({ScreenLabel})")
                : (Health.Warn, $"Đang vào lại game ({ScreenLabel})");
        if (Maintenance) return (Health.Warn, "🛠 Máy chủ game đang bảo trì – app tạm đứng yên");
        if (Stats.IdleFor(now) > HealthIdle) return (Health.Warn, $"Đứng yên {Stats.IdleFor(now).TotalMinutes:0} phút");
        if (BagNearlyFull) return (Health.Warn, $"Hòm đồ gần đầy {BagUsed}/{BagMax}");
        return (Health.Good, "Đang chạy");
    }

    // Ghi một dòng vào nhật ký hoạt động (bên chạy cũng dùng, ví dụ khi xác định được đồ phổ là của acc này)
    public void Note(DateTime now, string text, StatusLevel level = StatusLevel.Info)
    {
        if (Activity.Add(now, text, level)) ActivityAdded?.Invoke(Activity.Items[0]);
    }

    // ---------- Lệnh gửi vào trang ----------

    // Lệnh thực hiện trong trang: "mode" (arg = farm/dg/quest/tower), "enter", "login", "kite"
    public void RunCommand(string name, object? arg = null) => Post(new { t = "cmd", name, arg });

    // Bạn bấm nút chế độ (Luyện Công / Phó Bản / Nhiệm Vụ / Leo Tháp) cho acc này.
    // Nhiệm Vụ = bắt đầu vòng tự động: mua lệnh bài → Nhiệm Vụ → Phó Bản → Leo Tháp → (chết nhanh) Luyện Công → lại Tháp.
    // Chế độ khác = bạn tự chọn → tắt vòng tự động để app không đổi đi mất.
    public void RequestMode(string code, DateTime now)
    {
        _userModeAt = now;
        ResumeAway();
        Account.StayInTower = code == ModeRules.Tower;
        if (code == ModeRules.Tower) { _fastDeaths.Clear(); _modeBlockedUntil.Remove(ModeRules.Tower); }
        _modeBlockedUntil.Remove(code);
        if (code == ModeRules.Quest)
        {
            if (!Account.AutoMode)
            {
                SetAutoMode(true, now);
                Note(now, "🔁 Bắt đầu vòng tự động: Nhiệm Vụ → Phó Bản → Đi thuyền → Boss tuần → Sát Thủ → Leo Tháp → Luyện Công");
            }
            // Đang trên thuyền / đang đánh Boss tuần / đang bảo trì: không đổi chế độ (sẽ kéo nhân vật ra giữa chừng);
            // vòng tự động đã bật sẽ tự đi tiếp khi xong
            if (EventRunning(now) || _eventCheck is not null || Maintenance)
            {
                var why = Maintenance ? "máy chủ game đang bảo trì" : $"đang {ModeRules.Label(_eventCheck?.Mode ?? _eventPhase ?? ModeRules.Boat)}";
                Note(now, $"🔁 Nhiệm Vụ: {why} – chờ xong rồi vòng tự chạy tiếp");
                return;
            }
            StartQuest(now, byYou: true);
        }
        else
        {
            if (Account.AutoMode)
            {
                SetAutoMode(false, now);
                Note(now, $"🔁 Dừng vòng tự động (bạn chọn {ModeRules.Label(code)})");
            }
            RunCommand("mode", code);
        }
    }

    public QuestCounts QuestCounts => new(DaTau, LdtHave, LdtUsed, LdtBuy, DaTauMaxPerDay);

    // ---------- Vòng tự động: từng bước đang ở đâu (hiện trên khung / trang web) ----------

    // State: "done" xong hôm nay · "current" đang chạy · "todo" chưa tới · "rest" tạm nghỉ (Detail = tới mấy giờ) · "off" tắt trong cài đặt
    public sealed record ChainStep(string Mode, string Label, string State, string Detail);

    public IReadOnlyList<ChainStep> AutoChain(DateTime now)
    {
        var cfg = SettingsProvider?.Invoke() ?? new AppSettings();
        var quest = _ldtTriedDay == now.Date ? QuestCounts with { LdtBuy = "" } : QuestCounts;
        string State(string mode, bool enabled, bool done) =>
            !enabled ? "off" : ModeCode == mode ? "current" : IsModeBlocked(mode, now) ? "rest" : done ? "done" : "todo";
        string Until(string mode) => _modeBlockedUntil.TryGetValue(mode, out var t) && t > now ? $"nghỉ tới {t:HH:mm}" : "";

        bool questDone = ModeRules.TryFraction(DaTau, out _, out _) && ModeRules.QuestLeft(quest) is null;
        var questState = State(ModeRules.Quest, cfg.AutoModeQuest, questDone);
        var questDetail = _ldtBuyingSince is not null ? "đang mua lệnh bài"
            : questState == "rest" ? Until(ModeRules.Quest) : DaTauToday;

        bool dgDone = ModeRules.TryFraction(PhoBan, out var dgLeft, out _) && dgLeft == 0;
        var dgState = State(ModeRules.Dungeon, cfg.AutoModeDungeon, dgDone);
        var dgDetail = dgState == "rest" ? Until(ModeRules.Dungeon) : PhoBan.Length > 0 ? $"còn {PhoBan}" : "";

        ChainStep EventStep(string mode, bool enabled, string counts, int left)
        {
            var state = !enabled ? "off"
                : _eventPhase == mode || _eventCheck?.Mode == mode ? "current"
                : IsModeBlocked(mode, now) ? "rest"
                : counts.Length > 0 && left == 0 ? "done" : "todo";
            var detail = state == "rest" ? Until(mode)
                : state == "current" && EventRunning(now) ? (mode == ModeRules.Boat ? "đang trên thuyền" : "đang đánh")
                : counts.Length > 0 ? $"còn {counts.Replace(" claim", "")}" : "";
            return new ChainStep(mode, ModeRules.Label(mode), state, detail);
        }
        var boatStep = EventStep(ModeRules.Boat, cfg.AutoModeBoat, Boat, BoatLeft);
        var weekStep = EventStep(ModeRules.WeekBoss, cfg.AutoModeWeekBoss, WeekBoss, WeekBossLeft);

        var stState = !cfg.AutoModeAssassin ? "off"
            : _assassinPhase || AssassinActive ? "current"
            : IsModeBlocked(ModeRules.Assassin, now) ? "rest"
            : Assassin.Length > 0 && AssassinTokens == 0 ? "done" : "todo";
        var stDetail = stState == "rest" ? Until(ModeRules.Assassin)
            : AssassinActive ? $"đang đánh · còn {AssassinTokens} lệnh bài"
            : Assassin.Length > 0 ? $"còn {AssassinTokens} lệnh bài" : "";

        var towerState = State(ModeRules.Tower, cfg.AutoModeFallback == ModeRules.Tower, false);
        var towerDetail = towerState == "rest" ? Until(ModeRules.Tower)
            : towerState == "current" ? string.Join(" · ", new[]
                {
                    TowerFloor is { } f ? $"tầng {f}" : "",
                    _fastDeaths.Count > 0 ? $"chết nhanh {_fastDeaths.Count}/{cfg.TowerFastDeathCount}" : "",
                }.Where(x => x.Length > 0))
            : "";

        var farmState = ModeCode == ModeRules.Farm && stState != "current" && boatStep.State != "current"
                        && weekStep.State != "current" ? "current" : "todo";
        return new[]
        {
            new ChainStep(ModeRules.Quest, "Nhiệm Vụ", questState, questDetail),
            new ChainStep(ModeRules.Dungeon, "Phó Bản", dgState, dgDetail),
            boatStep,
            weekStep,
            new ChainStep(ModeRules.Assassin, "Sát Thủ", stState, stDetail),
            new ChainStep(ModeRules.Tower, "Leo Tháp", towerState, towerDetail),
            new ChainStep(ModeRules.Farm, "Luyện Công", farmState, ""),
        };
    }

    // Một dòng ngắn: "🔁 ✓ Nhiệm Vụ → ▶ Phó Bản (còn 2/4) → · Leo Tháp → · Luyện Công"
    public string AutoChainText(DateTime now) => Maintenance ? "🛠 Máy chủ game đang bảo trì – app tạm đứng yên"
        : "🔁 " + string.Join(" → ", AutoChain(now).Select(s =>
        $"{ChainIcon(s.State)} {s.Label}{(s.Detail.Length > 0 && s.State is "current" or "rest" ? $" ({s.Detail})" : "")}"));

    public static string ChainIcon(string state) => state switch
    {
        "done" => "✓", "current" => "▶", "rest" => "💤", "off" => "✕", _ => "·",
    };

    // Lần cuối vòng tự động xét chế độ (mỗi 30 giây) – để biết vòng vẫn đang chạy
    public DateTime LastAutoCheckAt => _lastAutoModeCheck;

    public void Replay(JsonElement ev) => Post(new { t = "replay", ev });

    // Bật/tắt việc gửi thao tác thật của trang này về app (đồng bộ thao tác)
    public void SetCapture(bool on)
    {
        _capture = on;
        Post(new { t = "capture", on });
    }

    private void Post(object message) => _page.Post(JsonSerializer.Serialize(message));

    // Bạn tự bấm tải lại → cho bộ giám sát thử tải lại từ đầu
    public void ManualReload()
    {
        ResumeAway();
        _reloadLimiter.Reset();
        _page.Reload();
    }

    // Gọi sau khi đổi mật khẩu đã lưu: cho phép tự đăng nhập thử lại từ đầu
    public void ResetLoginState()
    {
        _loginFails = 0;
        _loginBlocked = false;
        _loginSentAt = null;
        _loginNotBefore = default;
        _loginLimiter.Reset();
    }

    public void SetKite(bool on, DateTime now)
    {
        Account.Kite = on;
        _lastKiteAt = now; // bật xong chờ một chu kỳ rồi mới kéo lần đầu
        Log.Write($"[{Account.Id}] kite {(on ? "on" : "off")}");
    }

    public void SetAutoMode(bool on, DateTime now)
    {
        Account.AutoMode = on;
        _lastAutoModeCheck = now; // bật xong chờ một nhịp rồi mới xét
        if (!on) { _assassinPhase = false; _eventPhase = null; }
        Log.Write($"[{Account.Id}] auto mode {(on ? "on" : "off")}");
        InfoChanged?.Invoke();
    }

    // ---------- Sự kiện trình duyệt ----------

    public void OnNavigationStarting(string uri, DateTime now)
    {
        _lastNow = now;
        Log.Write($"[{Account.Id}] navigate {uri}");
        _pageLoaded = false;
        SetScreen("loading", now);
        SetStatus("Đang tải", StatusLevel.Warn);
    }

    public void OnNavigationCompleted(bool success, string error, DateTime now)
    {
        _lastNow = now;
        if (success)
        {
            _pageLoaded = true;
            _navFailedAt = null;
            _lastStatusAt = now;
            SetStatus("Đã tải", StatusLevel.Ok);
        }
        else
        {
            _navFailedAt = now;
            SetStatus($"Lỗi tải ({error})", StatusLevel.Error);
        }
    }

    public void OnProcessFailed(string kind, DateTime now)
    {
        _lastNow = now;
        Log.Write($"[{Account.Id}] process failed: {kind}");
        _crashedAt = now;
        _pageLoaded = false;
        SetStatus("Bị crash", StatusLevel.Error);
        Raise("crash", "Trang game bị crash");
    }

    public void OnInitFailed(string error)
    {
        Log.Write($"[{Account.Id}] init failed: {error}");
        SetStatus("Không khởi tạo được", StatusLevel.Error);
        Raise("init", "Không khởi tạo được trình duyệt");
    }

    // ---------- Tin nhắn từ trang ----------

    public void OnPageMessage(string json, DateTime now)
    {
        _lastNow = now;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "hello":
                    // Trang vừa tải (hoặc tải lại) → gửi lại trạng thái bắt thao tác
                    Post(new { t = "capture", on = _capture });
                    break;
                case "status":
                    ApplyStatus(m, now);
                    break;
                case "input":
                    if (_capture) InputCaptured?.Invoke(m.GetProperty("ev").Clone());
                    break;
                case "replayMiss":
                    var what = m.GetProperty("what").GetString();
                    Log.Write($"[{Account.Id}] replay miss: {what} ({m.GetProperty("path").GetString()}) {m.GetProperty("reason").GetString()}");
                    SetStatus($"⚠ Không làm theo được: {Shorten(what, 24)}", StatusLevel.Notice);
                    break;
                case "cmdResult":
                    OnCommandResult(m);
                    break;
                case "problem":
                    Raise(Str(m, "key"), Str(m, "msg"));
                    break;
                case "unreadable":
                    OnUnreadable(Str(m, "what"), Str(m, "sample"), now);
                    break;
                case "sysNote":
                    OnSysNote(Str(m, "text"), m.TryGetProperty("boss", out var bossFlag) && bossFlag.ValueKind == JsonValueKind.True, now);
                    break;
                case "eventReward":
                    OnEventReward(Str(m, "kind"), Str(m, "text"), now);
                    break;
                case "towerReward":
                    OnTowerReward(m.TryGetProperty("floor", out var fl) && fl.TryGetInt32(out var floorNo) ? floorNo : 0, Str(m, "reward"), now);
                    break;
                case "tabLock":
                    Log.Write($"[{Account.Id}] game save conflict → Tải bản mới");
                    Note(now, "🔄 Game báo nhân vật đã được lưu ở nơi khác → tự bấm \"Tải bản mới\"");
                    break;
                case "gifts":
                    foreach (var g in Str(m, "claimed").Split('|', StringSplitOptions.RemoveEmptyEntries))
                        Note(now, $"🎁 Nhận quà: {g}", StatusLevel.Ok);
                    break;
                case "ldtResult":
                    OnLdtResult(m, now);
                    break;
                case "loot":
                    LootFound?.Invoke(m.GetProperty("src").GetString() ?? "", m.GetProperty("text").GetString() ?? "");
                    break;
                case "manualLogin":
                    _manualLogin = (m.GetProperty("user").GetString() ?? "", m.GetProperty("pass").GetString() ?? "", now);
                    Log.Write($"[{Account.Id}] manual login by {_manualLogin.Value.User}");
                    break;
                case "log":
                    Log.Write($"[{Account.Id}] {m.GetProperty("msg").GetString()}");
                    break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write($"[{Account.Id}] bad page message: {ex.Message}");
        }
    }

    private void ApplyStatus(JsonElement s, DateTime now)
    {
        _lastStatusAt = now;
        var screen = Str(s, "screen") is { Length: > 0 } sc ? sc : "loading";
        bool changed = false;

        var loginMsg = Str(s, "loginMsg");
        if (loginMsg != _loginMsg && loginMsg.Length > 0)
            Log.Write($"[{Account.Id}] login message: {loginMsg}");
        _loginMsg = loginMsg;

        var name = Str(s, "name");
        if (!string.IsNullOrWhiteSpace(name) && name != Account.CharName)
        {
            Log.Write($"[{Account.Id}] character name: {name}");
            Account.CharName = name;
            changed = true;
        }

        changed |= SetScreen(screen, now);
        changed |= ApplyAway(s.TryGetProperty("away", out var away) && away.ValueKind == JsonValueKind.Number && away.GetInt32() == 1, now);

        // Vài giây đầu sau khi vào game, ô cấp/tiền còn hiện giá trị tạm ("1", "0") trước khi
        // game tải xong dữ liệu → bỏ qua, nếu không sẽ lấy nhầm làm mốc "cấp đầu phiên"
        if (screen == "game" && now - _screenSince >= StatsSettle)
        {
            int? level = int.TryParse(Str(s, "lv"), out var lv) ? lv : null;
            var before = (Stats.Level, Stats.Gold, Stats.Knb, KnbLocked);
            var (knb, knbLocked) = MoneyParser.ParseKnb(Str(s, "knb"));
            Stats.Update(now, MoneyParser.Parse(Str(s, "gold")), knb, level);
            if (knbLocked is { } kl) KnbLocked = kl;
            if (before.Level is { } oldLv && Stats.Level is { } newLv && newLv > oldLv)
                Note(now, $"⬆ Lên cấp {newLv}", StatusLevel.Ok);
            var mode = Str(s, "modeText");
            var daTau = Str(s, "dt");
            var phoBan = Str(s, "pb");
            var tokens = (Have: Str(s, "ldtHave"), Used: Str(s, "ldtUsed"), Buy: Str(s, "ldtBuy"), Max: Str(s, "dtMax"));
            changed |= before != (Stats.Level, Stats.Gold, Stats.Knb, KnbLocked) || mode != ModeText
                       || daTau != DaTau || phoBan != PhoBan || tokens != (LdtHave, LdtUsed, LdtBuy, DaTauMaxPerDay);
            (LdtHave, LdtUsed, LdtBuy, DaTauMaxPerDay) = tokens;
            ModeText = mode;

            var floorMatch = TowerFloorPattern.Match(Str(s, "zone"));
            int? floor = floorMatch.Success ? int.Parse(floorMatch.Groups[1].Value) : null;
            var previousFloor = TowerFloor;
            if (Tower.Update(now, floor))
            {
                changed = true;
                if (floor is { } f)
                {
                    if (previousFloor is null) Note(now, $"🗼 Vào Thông Thiên Tháp, tầng {f}");
                    else if (f > previousFloor)
                    {
                        Stats.MarkProgress(now);
                        if (f / TowerLogEvery > previousFloor.Value / TowerLogEvery) Note(now, $"🗼 Lên tầng {f}");
                    }
                    _lastTowerFloor = f;
                }
            }

            changed |= ApplyBag(Str(s, "bag"), now);
            if (s.TryGetProperty("dp", out var dpEl) && dpEl.ValueKind == JsonValueKind.String)
            {
                // Trang chỉ gửi "dp" sau lần quét đầu; lần đầu chỉ ghi nhận, không báo là món mới
                var held = (dpEl.GetString() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (!held.SequenceEqual(DoPhoHeld))
                {
                    if (_doPhoRead)
                        foreach (var item in held.Except(DoPhoHeld)) Note(now, $"📕 Đang giữ Đồ phổ: {item}", StatusLevel.Ok);
                    DoPhoHeld = held;
                    changed = true;
                }
                _doPhoRead = true;
            }
            ApplyBossHk(Str(s, "hk"), now);
            var assassin = Str(s, "st");
            if (assassin.Length > 0 && assassin != Assassin) { Assassin = assassin; changed = true; }
            changed |= ApplyEvents(Str(s, "boat"), Str(s, "wb"), now);
            changed |= ApplyMaintenance(s.TryGetProperty("bt", out var bt) && bt.ValueKind == JsonValueKind.Number && bt.GetInt32() == 1, now);
            var exp = Str(s, "xp");
            var tracker = Str(s, "trk");
            changed |= exp != Exp || tracker != Tracker;
            (Exp, Tracker) = (exp, tracker);

            var modeCode = Str(s, "mode");
            if (modeCode == "tower" && ModeCode != "tower")
            {
                _towerEnteredAt = now;
                // Vừa vào tháp: vòng chạy (ví dụ hình vuông) bắt đầu lại từ bước đầu,
                // và chờ một chu kỳ rồi mới kéo lần đầu
                _kiteStep = 0;
                _kiteTries = 0;
                _lastKiteAt = now;
            }
            if (modeCode != ModeCode && ModeCode != "" && modeCode != "")
                OnModeChanged(ModeCode, modeCode, now);
            ModeCode = modeCode;
            DaTau = daTau;
            PhoBan = phoBan;

            var sect = SectNames.FromCode(Str(s, "sect"));
            if (!string.IsNullOrWhiteSpace(sect) && sect != Account.Sect)
            {
                Account.Sect = sect;
                changed = true;
            }
        }
        if (changed) InfoChanged?.Invoke();
    }

    // Hòm đồ "18/120" (đọc định kỳ bằng cách mở/đóng bảng Hòm đồ). Trả về true nếu đổi.
    private bool ApplyBag(string bag, DateTime now)
    {
        var m = BagPattern.Match(bag);
        if (!m.Success) return false;
        int used = int.Parse(m.Groups[1].Value), max = int.Parse(m.Groups[2].Value);
        if (used == BagUsed && max == BagMax) return false;
        bool wasFull = BagNearlyFull;
        (BagUsed, BagMax) = (used, max);
        if (BagNearlyFull && !wasFull) Raise("bag", $"Hòm đồ gần đầy: {used}/{max}");
        return true;
    }

    // Boss Hoàng Kim: ghi lại mọi lần dòng trạng thái đổi (để biết game hiện gì khi boss ra), báo khi boss xuất hiện
    private void ApplyBossHk(string hk, DateTime now)
    {
        // Chỉ ghi log (để biết game hiện gì), không báo: game tự đưa nhân vật đi đánh, trang web có đếm ngược
        if (hk.Length == 0 || hk == BossHk) return;
        BossHk = hk;
        Log.Write($"[{Account.Id}] boss HK: {hk}");
    }

    // Máy chủ game bảo trì (trang game tự hỏi máy chủ, agent nghe câu trả lời). Game vẫn chạy trên trang nhưng không
    // lưu / đồng bộ được → app đứng yên: không đổi chế độ, không mua, không đi thuyền / Boss tuần, không kéo quái.
    public bool Maintenance { get; private set; }

    private bool ApplyMaintenance(bool on, DateTime now)
    {
        if (on == Maintenance) return false;
        Maintenance = on;
        if (on)
        {
            Raise("maintenance", "Máy chủ game đang bảo trì – app tạm đứng yên");
            SetStatus("🛠 Máy chủ game đang bảo trì", StatusLevel.Warn);
        }
        else
        {
            Note(now, "🛠 Hết bảo trì: app chạy lại bình thường", StatusLevel.Ok);
            Stats.ResetActivity(now); // thời gian đứng yên trong lúc bảo trì không tính
            // Lượt thuyền / Boss tuần dở dang trước bảo trì: không chấm là hỏng, xét lại từ đầu
            (_eventCheck, _eventDone, _eventBusyUntil) = (null, false, now);
        }
        return true;
    }

    // Đang trong đợt Boss Hoàng Kim (theo lịch giờ Việt Nam, hoặc đang đánh Boss Hoàng Kim do Sát Thủ gọi ra).
    // Trong lúc này game tự đổi chế độ để đưa nhân vật đi / về: không tính là chết, không khoá gì (OnModeChanged).
    private bool _bossTime;
    public bool BossHkTime(DateTime now) => BossHkSchedule(now) || Tracker.Contains("Hoàng Kim");

    // App có nên đứng yên vì Boss Hoàng Kim không:
    //  - đang đánh: góc nhiệm vụ có "Hoàng Kim", hoặc kênh Hệ thống vừa báo boss xuất hiện / đưa tới mà chưa báo hạ xong
    //    (tối đa BossFightMax, phòng khi không thấy dòng báo xong);
    //  - BossGrace đầu mỗi đợt theo lịch: chờ game kịp đưa nhân vật đi.
    // Ngoài hai trường hợp đó (không dính boss, hoặc đã hạ xong) → vòng tự động chạy bình thường, về tháp ngay.
    private static readonly TimeSpan BossGrace = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan BossFightMax = TimeSpan.FromMinutes(15);
    private bool _inBossWindow;
    private DateTime _bossWindowSince;
    private DateTime? _bossEngagedAt, _bossDoneAt;

    private bool BossHandsOff(DateTime now)
    {
        bool window = BossHkSchedule(now);
        if (window && !_inBossWindow) _bossWindowSince = now;
        _inBossWindow = window;
        if (Tracker.Contains("Hoàng Kim")) return true;
        bool engaged = _bossEngagedAt is { } e && now - e < BossFightMax && !(_bossDoneAt is { } d && d >= e);
        return engaged || (window && now - _bossWindowSince < BossGrace);
    }

    // Dòng kênh Hệ thống về boss của chính acc (chép từ game):
    //   "👑 Boss Hoàng Kim Hà Linh Phiêu xuất hiện — tạm rời Tháp tầng 4288 đi săn Boss."  "🗺 Boss Hoàng Kim …: tới …"
    //   "🎁 Hạ Boss Hoàng Kim: 36.00M kinh nghiệm, …"  "Hạ Boss Hoàng Kim · Hà Linh Phiêu"  "🗼 Xong Boss Hoàng Kim — quay lại Tháp …"
    private static readonly System.Text.RegularExpressions.Regex BossEngagedPattern =
        new(@"Boss Hoàng Kim\b.*(xuất hiện|:\s*tới\b|ở ngay gần)");
    private static readonly System.Text.RegularExpressions.Regex BossDonePattern =
        new(@"(Hạ Boss Hoàng Kim|Xong Boss Hoàng Kim)");

    private void TrackBossLine(string text, DateTime now)
    {
        if (text.Contains("Chúc mừng")) return; // thông báo chung của server
        if (BossDonePattern.IsMatch(text)) _bossDoneAt = now;
        else if (BossEngagedPattern.IsMatch(text)) _bossEngagedAt = now;
    }
    // Lịch đợt boss (test thay bằng hàm khác để không phụ thuộc múi giờ máy chạy test)
    public Func<DateTime, bool> BossHkSchedule { get; set; } = now => GameData.BossHkSchedule.IsActive(now.ToUniversalTime());

    // Trả về true nếu màn hình đổi
    private bool SetScreen(string screen, DateTime now)
    {
        if (screen == Screen) return false;
        var previous = Screen;
        Screen = screen;
        _screenSince = now;
        Log.Write($"[{Account.Id}] screen {previous} -> {screen}");

        if (previous == "game" && screen == "login")
        {
            // Bị đẩy ra (mất phiên, đăng nhập nơi khác…): chờ một lúc rồi mới tự đăng nhập lại,
            // tránh giành phiên qua lại nếu acc đang được dùng ở chỗ khác
            _loginNotBefore = now + KickedLoginDelay;
            Raise("kicked", "Bị đẩy ra màn đăng nhập");
        }

        if (screen is "charselect" or "game")
        {
            if (_loginSentAt is not null)
            {
                Log.Write($"[{Account.Id}] auto login ok");
                Note(now, "🔑 Tự đăng nhập thành công", StatusLevel.Ok);
            }
            _loginSentAt = null;
            _loginFails = 0;
            if (_manualLogin is { } ml && now - ml.At < ManualLoginWindow && !string.IsNullOrEmpty(ml.Pass))
                ManualLoginSucceeded?.Invoke(ml.User, ml.Pass);
            _manualLogin = null;
        }

        if (screen == "game")
        {
            Stats.ResetActivity(now);
            Note(now, "▶ Vào game");
            // Trang vừa tải lại thì quên cài đặt (mặc định giữ thẻ Hệ thống) → gửi lại
            if (!_keepSystemChat) Post(new { t = "keepSys", on = false });
        }
        InfoChanged?.Invoke();
        return true;
    }

    private void OnCommandResult(JsonElement m)
    {
        var name = Str(m, "name");
        var result = Str(m, "result");

        if (name == "kite")
        {
            OnKiteResult(result);
            return;
        }
        if (name == "assassin")
        {
            OnAssassinResult(result, _lastNow == default ? DateTime.Now : _lastNow);
            return;
        }
        if (name is "boat" or "weekBoss" or "weekClaim")
        {
            OnEventResult(name, result, _lastNow == default ? DateTime.Now : _lastNow);
            return;
        }
        if (name == "assassinGo")
        {
            Log.Write($"[{Account.Id}] assassin go: {result}");
            if (result.StartsWith("ok")) Note(_lastNow == default ? DateTime.Now : _lastNow, "🗡 Đi đến Boss Sát Thủ");
            return;
        }

        Log.Write($"[{Account.Id}] cmd {name} {(m.TryGetProperty("arg", out var a) ? a.ToString() : "")}: {result}");
        switch (name, result)
        {
            case ("login", "ok"): SetStatus("Đang đăng nhập…", StatusLevel.Warn); break;
            case ("enter", "ok"): SetStatus("Đang vào game…", StatusLevel.Warn); break;
            case (_, "ok"): SetStatus("Đã nhận lệnh", StatusLevel.Ok); break;
            case (_, "already"): SetStatus("Đã ở sẵn", StatusLevel.Ok); break;
            case (_, "not-in-game"): SetStatus("Chưa vào game", StatusLevel.Warn); break;
            case (_, "not-ready"): SetStatus("Chưa sẵn sàng", StatusLevel.Warn); break;
            default: SetStatus($"Lệnh lỗi ({result})", StatusLevel.Error); break;
        }
    }

    // ---------- Giám sát & tự hồi phục (gọi mỗi SuperviseInterval) ----------

    public void Tick(DateTime now)
    {
        _lastNow = now;
        // Bảo trì: chỉ giữ phần tự hồi phục (trang treo / bị đẩy ra), không làm gì trong game
        SuperviseDailySummary(now);
        if (Maintenance && Screen == "game") { _stepWatch = null; SuperviseRecovery(now); return; }
        SuperviseAway(now); // cả trong đợt Boss Hoàng Kim
        if (Screen == "game" && !Away && Stats.IdleFor(now) > IdleAlertAfter)
            Raise("idle", $"Đứng yên hơn {IdleAlertAfter.TotalMinutes:0} phút (tiền, cấp và tầng tháp không đổi)");

        // Boss Hoàng Kim: game tự đưa nhân vật đi đánh rồi (thường) về lại tháp → app đứng yên, không chen vào.
        // Chỉ đứng yên khi acc thật sự đang bận với boss (xem BossHandsOff), không đứng yên cả khung giờ: acc không
        // dính boss (vừa chết ra Luyện Công, game không đưa đi…) hoặc đánh xong mà game không đưa về tháp thì
        // trước đây đứng ở Luyện Công tới hết đợt (~30 phút).
        bool handsOff = BossHandsOff(now);
        if (handsOff != _bossTime)
        {
            _bossTime = handsOff;
            Note(now, handsOff ? "👑 Boss Hoàng Kim: để game tự đi đánh, app tạm đứng yên"
                               : "👑 Xong Boss Hoàng Kim: app chạy lại bình thường");
            if (!handsOff) _towerReturnAt = now; // game chưa đưa về tháp thì vòng tự đưa về
        }
        if (handsOff)
        {
            _stepWatch = null; // game tự đưa đi đánh boss: thời gian này không tính là kẹt
            SuperviseRecovery(now);
            return;
        }

        SuperviseKite(now);
        if (_ldtBuyingSince is { } since && now - since > LdtBuyTimeout)
        {
            // Trang không trả lời (tải lại giữa chừng…): bỏ qua việc mua, vào Nhiệm Vụ luôn
            _ldtBuyingSince = null;
            Raise("ldt-error", "Mua lệnh bài không phản hồi, vào Nhiệm Vụ luôn");
            GoQuest(now);
        }
        SuperviseAutoMode(now);
        HintDungeonDone(now);
        SuperviseTowerReturn(now);
        SuperviseAssassin(now);
        SuperviseEvents(now);
        SuperviseStuck(now);
        SuperviseRecovery(now);
    }

    // ---------- "Tài khoản đang ở nơi khác" ----------
    // Game đẩy ra với 2 nút "Chơi ở đây" / "Đăng xuất". Chờ AwayClickDelay (người dùng ở máy khác kịp chơi) rồi bấm
    // "Chơi ở đây". Vừa bấm mà trong AwayRepeatWindow lại bị đẩy ra → có người đang dùng thật: ngừng AwayPause và báo.

    private static readonly TimeSpan AwayClickDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AwayRetryGap = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AwayRepeatWindow = TimeSpan.FromMinutes(10);
    private DateTime? _awaySince;
    private DateTime _lastPlayHereAt, _awayPausedUntil;

    public bool Away { get; private set; }

    private bool ApplyAway(bool away, DateTime now)
    {
        if (away == Away) return false;
        Away = away;
        if (!away)
        {
            _awaySince = null;
            Stats.ResetActivity(now); // thời gian nằm ở hộp "nơi khác" không tính là đứng yên
            Log.Write($"[{Account.Id}] away dialog gone");
            return true;
        }
        _awaySince = now;
        if (_lastPlayHereAt != default && now - _lastPlayHereAt < AwayRepeatWindow)
        {
            // Có người đang chơi thật ở máy khác (thường là bạn, bằng bản PC): thôi hẳn, không giành lại nữa
            _awayPausedUntil = DateTime.MaxValue;
            Raise("elsewhere", "Acc đang được chơi ở nơi khác (vừa bấm \"Chơi ở đây\" lại bị đẩy ra) – app ngừng tự vào lại. " +
                               "Muốn server chơi tiếp: bấm Tải lại acc này hoặc bấm một chế độ");
        }
        else
        {
            Note(now, $"📱 Tài khoản đang mở ở nơi khác – {AwayClickDelay.TotalMinutes:0} phút nữa app bấm \"Chơi ở đây\"", StatusLevel.Warn);
        }
        SetStatus("📱 Tài khoản đang mở ở nơi khác", StatusLevel.Warn);
        return true;
    }

    // Bạn bấm Tải lại / chọn chế độ cho acc này: cho app tự vào lại như bình thường (bấm "Chơi ở đây" ngay nếu đang bị đẩy ra)
    private void ResumeAway()
    {
        _awayPausedUntil = default;
        _lastPlayHereAt = default;
        if (Away) _awaySince = DateTime.MinValue;
    }

    private void SuperviseAway(DateTime now)
    {
        if (!Away || !AutoRecover || _awaySince is not { } since) return;
        if (now < _awayPausedUntil || now - since < AwayClickDelay || now - _lastPlayHereAt < AwayRetryGap) return;
        _lastPlayHereAt = now;
        Note(now, "📱 Bấm \"Chơi ở đây\" để vào lại", StatusLevel.Ok);
        RunCommand("playHere");
    }

    // ---------- Đứng yên quá lâu: tải lại trang ----------
    // Lưới an toàn cho mọi trường hợp kẹt (mất kết nối, hộp thoại lạ, trang đơ). Không tải lại trong lúc game tự lo
    // (Boss HK, bảo trì, đi thuyền / Boss tuần), khi đang có hộp "nơi khác", hoặc khi bạn đang xem Live acc đó.
    // Tối đa 2 lần / giờ; vẫn đứng yên thì chỉ báo.

    public static readonly TimeSpan StuckReloadAfter = TimeSpan.FromMinutes(10);
    private readonly AttemptLimiter _stuckReloadLimiter = new(2, TimeSpan.FromHours(1));
    private bool _stuckNoted;

    // Bản Server: đang có người xem Live acc này (khi đó để bạn tự chọn kênh chat)
    private bool _watched;
    public bool Watched
    {
        get => _watched;
        set { _watched = value; KeepSystemChat = !value; }
    }

    // Giữ thẻ chat ở "Hệ thống" để nghe dòng phần thưởng (tháp, Boss Hoàng Kim). Tắt khi bạn đang xem / dùng khung đó:
    // Server khi xem Live, Desktop ở khung lớn.
    private bool _keepSystemChat = true;
    public bool KeepSystemChat
    {
        get => _keepSystemChat;
        set
        {
            if (value == _keepSystemChat) return;
            _keepSystemChat = value;
            Post(new { t = "keepSys", on = value });
        }
    }

    // Dòng kênh Hệ thống có từ khoá phần thưởng mà trang chưa có mẫu riêng. Trong đợt Boss Hoàng Kim: thưởng boss
    // → ghi vào nhật ký. Mọi dòng đều ghi vào file log để học mẫu chính xác.
    private void OnSysNote(string text, bool bossTime, DateTime now)
    {
        Log.Write($"[{Account.Id}] sys: {text}");
        TrackBossLine(text, now);
        // Mẫu đã biết (chép từ game): ghi vào nhật ký bất kể giờ nào – Boss Hoàng Kim do Lệnh Bài Sát Thủ gọi ra
        // xuất hiện ngoài giờ Boss HK theo lịch
        //   "🎁 Hạ Boss Hoàng Kim: 36.00M kinh nghiệm, 1 Lam Thủy Tinh"   "👑 Boss Hoàng Kim rơi: Kim Phong Đồng Tước Xuân Thâm"
        //   "🎁 Hạ Sát Thủ cấp 90: 15,4 vạn lượng, …"                     "👑 May mắn! Lệnh Bài Sát Thủ gọi ra Boss Hoàng Kim …"
        if (KnownRewardPattern.IsMatch(text)) Note(now, text, StatusLevel.Ok);
        else if (bossTime && BossRewardPattern.IsMatch(text)) Note(now, $"👑 {text}", StatusLevel.Ok);
    }

    private static readonly System.Text.RegularExpressions.Regex KnownRewardPattern =
        new(@"^\W*(Hạ Boss Hoàng Kim\s*:|Boss Hoàng Kim rơi\s*:|Hạ Sát Thủ\b|May mắn!.*gọi ra Boss Hoàng Kim)");
    private static readonly System.Text.RegularExpressions.Regex BossRewardPattern =
        new(@"thưởng|Boss|Rương|Hoàng Kim", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void SuperviseStuck(DateTime now)
    {
        if (!AutoRecover || Screen != "game" || Away || Maintenance || Watched || EventRunning(now)) return;
        var idle = Stats.IdleFor(now);
        if (idle < StuckReloadAfter) { _stuckNoted = false; return; }
        if (_stuckReloadLimiter.TryAcquire(now))
        {
            Note(now, $"🔄 Đứng yên {idle.TotalMinutes:0} phút – tự tải lại game", StatusLevel.Warn);
            Log.Write($"[{Account.Id}] stuck {idle.TotalMinutes:0} min → reload");
            Stats.ResetActivity(now); // tính lại từ lúc tải lại
            _page.Reload();
        }
        else if (!_stuckNoted)
        {
            _stuckNoted = true;
            Raise("stuck", $"Đứng yên {idle.TotalMinutes:0} phút, đã tự tải lại 2 lần trong 1 giờ mà chưa chạy lại – hãy xem acc này");
        }
    }

    // Tự hồi phục: tải lại khi crash/treo, tự đăng nhập, tự vào game (chạy cả trong đợt Boss Hoàng Kim)
    private void SuperviseRecovery(DateTime now)
    {
        if (!AutoRecover) return;

        if (_crashedAt is { } crashed && now - crashed > CrashReloadDelay)
        {
            _crashedAt = null;
            TryReload(now, "sau khi crash");
            return;
        }
        if (_navFailedAt is { } failed && now - failed > NavFailRetryDelay)
        {
            _navFailedAt = null;
            TryReload(now, "sau lỗi tải trang");
            return;
        }
        if (_pageLoaded && now - _lastStatusAt > HeartbeatTimeout)
        {
            Raise("frozen", "Trang không phản hồi, đang tải lại");
            _lastStatusAt = now;
            TryReload(now, "trang treo");
            return;
        }

        if (now - _screenSince < ScreenSettle) return;

        if (Screen == "login") SuperviseLogin(now);
        else if (Screen == "charselect" && _enterLimiter.TryAcquire(now)) RunCommand("enter");
    }

    private void SuperviseLogin(DateTime now)
    {
        if (_loginBlocked || !Account.AutoLogin || now < _loginNotBefore) return;
        if (now - _screenSince < ScreenSettle + LoginStagger) return;
        if (CredentialProvider?.Invoke(Account.Id) is not { } cred)
        {
            // Đứng ở màn đăng nhập mà chưa có mật khẩu: không tự vào được → báo để bạn nhập ở 🔑 (hoặc tự đăng nhập)
            if (now - _screenSince > NoPasswordAlertAfter)
                Raise("no-password", "Đang ở màn đăng nhập nhưng chưa lưu mật khẩu game – không tự đăng nhập được (nhập ở 🔑 Quản lý acc)");
            return;
        }

        if (_loginSentAt is { } sent)
        {
            if (now - sent < LoginTimeout) return; // đang chờ kết quả lần trước
            _loginSentAt = null;

            if (NetErrorPattern.IsMatch(_loginMsg))
            {
                // Máy chủ không phản hồi: không phải lỗi mật khẩu → chờ rồi thử lại, không tính thất bại
                Log.Write($"[{Account.Id}] auto login: network error ({_loginMsg}), retry later");
                _loginNotBefore = now + NetErrorRetryDelay;
                SetStatus("Máy chủ không phản hồi – thử lại sau 1 phút", StatusLevel.Warn);
                Raise("login-net", "Máy chủ đăng nhập không phản hồi, sẽ tự thử lại");
                return;
            }

            _loginFails++;
            Log.Write($"[{Account.Id}] auto login attempt failed ({_loginFails}): {_loginMsg}");
            if (_loginFails >= 2)
            {
                _loginBlocked = true;
                SetStatus("Tự đăng nhập thất bại", StatusLevel.Error);
                Raise("login-fail", "Tự đăng nhập thất bại 2 lần – kiểm tra mật khẩu trong Quản lý acc");
                return;
            }
        }

        if (!_loginLimiter.TryAcquire(now))
        {
            Raise("login-limit", "Đã tự đăng nhập quá nhiều lần trong 30 phút, tạm dừng");
            return;
        }

        // Thông báo cũ (ví dụ lỗi mạng lần trước) không được tính cho lần thử mới
        _loginMsg = "";
        _loginSentAt = now;
        RunCommand("login", new { user = cred.User, pass = cred.Password });
    }

    private void TryReload(DateTime now, string reason)
    {
        if (_reloadLimiter.TryAcquire(now))
        {
            Log.Write($"[{Account.Id}] auto reload: {reason}");
            _page.Reload();
        }
        else
        {
            Raise("reload-limit", "Trang lỗi liên tục, đã ngừng tự tải lại – bấm ⟳ để thử tay");
        }
    }

    // ---------- Luật tự đổi chế độ (acc có bật 🔁) ----------

    private bool IsModeBlocked(string mode, DateTime now) =>
        _modeBlockedUntil.TryGetValue(mode, out var until) && now < until;

    // Chế độ đổi mà không phải do luật yêu cầu:
    //  - Tháp → Luyện Công mà bạn không vừa bấm đổi: chết trong tháp (game đẩy ra Luyện Công) → xem OnTowerDeath
    //  - Bạn vừa bấm đổi chế độ: làm theo, không khoá gì
    //  - Còn lại (game tự đẩy ra khi hết lượt…): tạm không đưa acc vào lại chế độ cũ, tránh đổi qua đổi lại
    private void OnModeChanged(string from, string to, DateTime now)
    {
        if (to == _pendingAutoMode)
        {
            _pendingAutoMode = null;
            Note(now, $"🔁 {ModeRules.Label(from)} → {ModeRules.Label(to)}");
            return;
        }

        bool byYou = now - _userModeAt < UserModeWindow;
        if (BossHkTime(now))
        {
            // Game tự đổi chế độ để đưa nhân vật đi đánh / về sau Boss Hoàng Kim: không phải chết, không khoá gì
            Log.Write($"[{Account.Id}] mode {from} -> {to} trong đợt Boss Hoàng Kim");
            return;
        }
        if (EventRunning(now))
        {
            // Đang đi thuyền / đánh Boss tuần: game tự đưa nhân vật đi rồi về, không phải chết, không khoá gì
            Log.Write($"[{Account.Id}] mode {from} -> {to} khi đang {ModeRules.Label(_eventPhase ?? "")}");
            return;
        }
        if (from == ModeRules.Tower && to == ModeRules.Farm && !byYou)
        {
            OnTowerDeath(now);
            return;
        }

        Note(now, $"Chế độ: {ModeRules.Label(from)} → {ModeRules.Label(to)}");
        if (byYou) return;
        _modeBlockedUntil[from] = now + ModeBlockDefault;
        Log.Write($"[{Account.Id}] mode {from} -> {to} (không do luật), tạm bỏ qua {ModeRules.Label(from)} {ModeBlockDefault.TotalMinutes:0} phút");
    }

    // Chết trong tháp: vào lại ngay. Chết nhanh nhiều lần liền trong thời gian ngắn → nghỉ tháp, Luyện Công, báo bạn.
    private void OnTowerDeath(DateTime now)
    {
        var cfg = SettingsProvider?.Invoke() ?? new AppSettings();
        var lived = now - _towerEnteredAt;
        var floor = _lastTowerFloor?.ToString() ?? "?";
        Tower.OnFall(now);
        Log.Write($"[{Account.Id}] tower death at floor {floor} after {lived.TotalMinutes:0.0} min");

        if (lived >= TimeSpan.FromMinutes(cfg.TowerFastDeathMinutes))
        {
            _fastDeaths.Clear(); // sống lâu → đếm lại từ đầu
            Note(now, $"💀 Chết ở tầng {floor} (sống {Minutes(lived)}) – vào lại tháp", StatusLevel.Warn);
        }
        else
        {
            var window = TimeSpan.FromMinutes(cfg.TowerFastDeathWindowMinutes);
            _fastDeaths.Add(now);
            _fastDeaths.RemoveAll(t => now - t > window);
            if (_fastDeaths.Count >= cfg.TowerFastDeathCount)
            {
                _fastDeaths.Clear();
                var rest = TimeSpan.FromMinutes(cfg.TowerRestMinutes);
                _modeBlockedUntil[ModeRules.Tower] = now + rest;
                Raise("tower-fast-death", $"Chết nhanh {cfg.TowerFastDeathCount} lần ở tầng {floor} – Luyện Công, {Minutes(rest)} nữa vào lại tháp");
                SetStatus($"Nghỉ tháp tới {(now + rest):HH:mm}", StatusLevel.Warn);
                InfoChanged?.Invoke();
                return;
            }
            Note(now, $"💀 Chết nhanh ở tầng {floor} (sống {Minutes(lived)}, lần {_fastDeaths.Count}/{cfg.TowerFastDeathCount}) – vào lại tháp", StatusLevel.Warn);
        }
        _towerReturnAt = now + TowerReturnDelay;
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} giây"
        : t.TotalHours < 1 ? $"{t.TotalMinutes:0} phút" : $"{t.TotalHours:0.#} giờ";

    // Acc bạn đã chọn Leo Tháp (không bật luật 🔁): đang Luyện Công vì chết/nghỉ tháp → hết giờ chờ thì vào lại tháp.
    // Acc bật luật 🔁 thì luật tự đưa vào lại (tháp đứng trước Luyện Công).
    private void SuperviseTowerReturn(DateTime now)
    {
        if (!Account.StayInTower || Account.AutoMode || Screen != "game" || ModeCode != ModeRules.Farm) return;
        if (now < _towerReturnAt || IsModeBlocked(ModeRules.Tower, now) || now - _lastTowerCmd < TowerCmdGap) return;
        _lastTowerCmd = now;
        _pendingAutoMode = ModeRules.Tower;
        _lastAutoModeCmd = now;
        Log.Write($"[{Account.Id}] back to tower");
        SetStatus("🗼 Vào lại tháp", StatusLevel.Ok);
        RunCommand("mode", ModeRules.Tower);
    }

    private void SuperviseAutoMode(DateTime now)
    {
        if (!Account.AutoMode || Screen != "game" || ModeCode == "" || _ldtBuyingSince is not null) return;

        // Đã ra lệnh mà sau 60 giây game vẫn không chuyển → game không cho vào chế độ đó lúc này
        if (_pendingAutoMode is { } pending && ModeCode != pending && now - _lastAutoModeCmd > TimeSpan.FromSeconds(60))
        {
            _modeBlockedUntil[pending] = now + ModeBlockDefault;
            Log.Write($"[{Account.Id}] auto mode: game không chuyển sang {pending}, tạm bỏ qua 60 phút");
            _pendingAutoMode = null;
        }

        // Vừa vào game: chờ đọc xong số lượt. Riêng trường hợp game khôi phục chế độ Phó Bản mà đã đọc được 0 lượt
        // (thoát game giữa phó bản) thì không cần chờ đủ – chuyển đi luôn
        bool dgDone = ModeCode == ModeRules.Dungeon && ModeRules.TryFraction(PhoBan, out var dgLeftNow, out _) && dgLeftNow == 0;
        var warmup = dgDone ? TimeSpan.FromSeconds(15) : AutoModeWarmup;
        if (now - _screenSince < warmup || now - _lastAutoModeCheck < AutoModeCheckInterval) return;
        _lastAutoModeCheck = now;
        if (now - _lastAutoModeCmd < AutoModeMinGap) return;
        if (SettingsProvider?.Invoke() is not { } s) return;

        // Đang đánh Sát Thủ: để yên cho đánh xong, không đổi chế độ.
        // Boss Hoàng Kim xuất hiện chỉ báo nhắc (🔔), vòng vẫn chạy bình thường.
        if (AssassinActive && !AssassinStuck(now)) return;
        // Đang trên thuyền / đang đánh Boss tuần / chờ xem số lượt có giảm: để yên ở Luyện Công
        // (nút Nhận rương tuần chỉ sáng khi không ở tháp)
        if (EventRunning(now) || _eventCheck is not null) return;

        WatchStepProgress(now);

        // Hôm nay đã thử mua lệnh bài rồi: không tính "còn mua được" nữa, tránh quay lại Nhiệm Vụ khi đã hết tiền
        var quest = _ldtTriedDay == now.Date ? QuestCounts with { LdtBuy = "" } : QuestCounts;
        if (ModeRules.Decide(quest, PhoBan, m => IsModeBlocked(m, now) || EventTooCloseToBossHk(m, now), s,
                             AssassinTokens, BoatLeft, WeekBossLeft) is not { } want) return;

        // Bước Sát Thủ / Đi thuyền / Boss tuần = ở Luyện Công rồi bấm nút trong bảng Nhiệm vụ
        // (SuperviseAssassin / SuperviseEvents); chưa ở Luyện Công thì chuyển sang trước
        bool wasAssassin = _assassinPhase;
        _assassinPhase = want.Mode == ModeRules.Assassin;
        if (_assassinPhase && !wasAssassin) Note(now, $"🗡 Sát Thủ: {want.Reason}");
        var wasEvent = _eventPhase;
        _eventPhase = want.Mode is ModeRules.Boat or ModeRules.WeekBoss ? want.Mode : null;
        if (_eventPhase is { } ev && ev != wasEvent) Note(now, $"{EventIcon(ev)} {ModeRules.Label(ev)}: {want.Reason}");
        var gameMode = _assassinPhase || _eventPhase is not null ? ModeRules.Farm : want.Mode;
        if (gameMode == ModeCode) return;

        _lastAutoModeCmd = now;
        _pendingAutoMode = gameMode;
        Log.Write($"[{Account.Id}] auto mode {ModeCode} -> {want.Mode} ({want.Reason})");
        SetStatus($"🔁 → {ModeRules.Label(want.Mode)} ({want.Reason})", StatusLevel.Ok);
        if (gameMode == ModeRules.Quest) StartQuest(now, byYou: false);
        else RunCommand("mode", gameMode);
    }

    // Vòng tự động đang TẮT (bạn tự chọn chế độ, ví dụ ở bản PC) mà acc đứng ở Phó Bản đã hết lượt: app không tự
    // chuyển, nhưng nhắc một lần để không lặng lẽ đứng mãi (game cho nhân vật đứng luyện công trong chế độ Phó Bản).
    private bool _dgDoneHinted;

    private void HintDungeonDone(DateTime now)
    {
        bool done = !Account.AutoMode && Screen == "game" && ModeCode == ModeRules.Dungeon
                    && ModeRules.TryFraction(PhoBan, out var left, out _) && left == 0;
        if (!done) { _dgDoneHinted = false; return; }
        if (_dgDoneHinted) return;
        _dgDoneHinted = true;
        Note(now, "🏯 Phó bản đã hết lượt – bấm Leo Tháp hoặc bật Tự động để đi tiếp", StatusLevel.Warn);
        SetStatus("🏯 Phó bản xong – bấm Leo Tháp hoặc bật Tự động", StatusLevel.Warn);
    }

    // ---------- Lưới an toàn: một bước kéo dài mà số lượt không đổi → bỏ qua bước đó ----------
    // Ví dụ game đổi chữ trên bảng (đi hết phó bản thì ghi "Hết lượt" chứ không phải "Vào 0/2") khiến app đọc sai
    // "còn lượt" mãi, giữ acc ở Phó Bản hàng giờ trong khi game đã cho nhân vật đứng luyện công.
    // Phó Bản: mỗi phó bản vài phút → 30 phút không giảm lượt = kẹt, bỏ tới hết ngày. Nhiệm Vụ: bỏ 60 phút.

    public static readonly TimeSpan StepStuckAfter = TimeSpan.FromMinutes(30);
    private (string Mode, string Counter, DateTime Since)? _stepWatch;

    private void WatchStepProgress(DateTime now)
    {
        if (!Account.AutoMode || ModeCode is not (ModeRules.Dungeon or ModeRules.Quest))
        {
            _stepWatch = null;
            return;
        }
        var counter = ModeCode == ModeRules.Dungeon ? PhoBan : DaTau;
        if (_stepWatch is not { } w || w.Mode != ModeCode || w.Counter != counter)
        {
            _stepWatch = (ModeCode, counter, now);
            return;
        }
        if (now - w.Since < StepStuckAfter || IsModeBlocked(w.Mode, now)) return;

        _stepWatch = null;
        var until = w.Mode == ModeRules.Dungeon ? NextVnMidnight(now) : now + ModeBlockDefault;
        _modeBlockedUntil[w.Mode] = until;
        Raise($"stuck-{w.Mode}", $"{ModeRules.Label(w.Mode)}: {StepStuckAfter.TotalMinutes:0} phút số lượt không đổi ({(counter.Length > 0 ? counter : "?")}) – " +
                                 $"bỏ qua tới {until:HH:mm}, vòng chạy tiếp");
    }

    // 0 giờ (giờ Việt Nam) hôm sau, theo giờ máy chạy app
    private static DateTime NextVnMidnight(DateTime now)
    {
        var until = DateTime.SpecifyKind(now.ToUniversalTime().AddHours(7).Date.AddDays(1).AddHours(-7), DateTimeKind.Utc).ToLocalTime();
        return until > now ? until : now + ModeBlockDefault;
    }

    // ---------- Bước Sát Thủ: ở Luyện Công, gọi từng con tới khi hết Lệnh Bài Sát Thủ ----------

    private static readonly TimeSpan AssassinSummonGap = TimeSpan.FromSeconds(20);
    private const int AssassinMaxFailures = 3;
    private bool _assassinPhase;
    private DateTime _lastSummonAt;
    private bool _summonPending;
    private int _summonFailures;

    // Lệnh Bài Sát Thủ đang có (đọc từ bảng Nhiệm vụ, "10/60" = có 10, đã hạ 60)
    public int AssassinTokens => ModeRules.TryFraction(Assassin, out var have, out _) ? have
        : int.TryParse(Assassin.Split('/')[0], out var h) ? h : 0;

    // Đang đánh Sát Thủ: góc nhiệm vụ theo dấu có dòng "Sát Thủ · Truy sát mục tiêu"
    public bool AssassinActive => Tracker.Contains("Sát Thủ");

    // Nhiệm vụ Sát Thủ đang mở mà nhân vật không tự tới (vd. sau khi đăng nhập lại) → bấm "Đi đến" vài phút một lần.
    // Quá AssassinStuckAfter vẫn chưa hạ → thôi chờ, cho vòng chạy tiếp (boss vẫn được game giữ lại).
    private static readonly TimeSpan AssassinGoFirst = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AssassinGoEvery = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AssassinStuckAfter = TimeSpan.FromMinutes(15);
    private DateTime? _assassinActiveSince;
    private DateTime _lastGoAt;
    private bool _assassinStuckNoted;

    private bool AssassinStuck(DateTime now) => _assassinActiveSince is { } since && now - since > AssassinStuckAfter;

    private void SuperviseAssassinGo(DateTime now)
    {
        if (!AssassinActive) { _assassinActiveSince = null; _assassinStuckNoted = false; return; }
        _assassinActiveSince ??= now;
        if (!Account.AutoMode || Screen != "game") return;
        if (AssassinStuck(now))
        {
            if (_assassinStuckNoted) return;
            _assassinStuckNoted = true;
            _assassinPhase = false;
            _modeBlockedUntil[ModeRules.Assassin] = now + ModeBlockDefault;
            Raise("assassin-stuck", $"Sát Thủ chưa hạ sau {AssassinStuckAfter.TotalMinutes:0} phút – tạm bỏ qua, vòng chạy tiếp");
            return;
        }
        if (now - _assassinActiveSince < AssassinGoFirst || now - _lastGoAt < AssassinGoEvery) return;
        _lastGoAt = now;
        RunCommand("assassinGo");
    }

    private void SuperviseAssassin(DateTime now)
    {
        SuperviseAssassinGo(now);
        if (!_assassinPhase || !Account.AutoMode || Screen != "game" || ModeCode != ModeRules.Farm) return;
        if (_summonPending || AssassinActive || AssassinTokens <= 0 || now - _lastSummonAt < AssassinSummonGap) return;
        _lastSummonAt = now;
        _summonPending = true;
        RunCommand("assassin");
    }

    private void OnAssassinResult(string result, DateTime now)
    {
        _summonPending = false;
        _lastSummonAt = now;
        Log.Write($"[{Account.Id}] assassin summon: {result}");
        switch (result)
        {
            case var r when r == "ok" || r.StartsWith("ok-nogo"):
                // ok-nogo: gọi được nhưng không thấy nút "Đi đến" (log ghi chữ các nút để sửa) → SuperviseAssassinGo thử lại
                _summonFailures = 0;
                Note(now, $"🗡 Gọi Sát Thủ (còn {Math.Max(0, AssassinTokens - 1)} lệnh bài)");
                SetStatus("🗡 Đang đánh Sát Thủ", StatusLevel.Ok);
                break;
            case "already" or "busy":
                break; // đang đánh / đang mở bảng → lần sau
            case "no-token":
                Assassin = "0/" + (Assassin.Split('/').ElementAtOrDefault(1) ?? "?");
                break;
            case "need-farm":
                RunCommand("mode", ModeRules.Farm);
                break;
            default:
                if (++_summonFailures >= AssassinMaxFailures)
                {
                    _summonFailures = 0;
                    _modeBlockedUntil[ModeRules.Assassin] = now + ModeBlockDefault;
                    _assassinPhase = false;
                    Raise("assassin-fail", $"Gọi Sát Thủ không được {AssassinMaxFailures} lần ({result}) – tạm bỏ qua 60 phút");
                }
                break;
        }
    }

    // ---------- Đi thuyền (3 chuyến / ngày) và Boss tuần (3 lượt / tuần): ở Luyện Công, bấm nút trong bảng Nhiệm vụ ----------
    // Đang Leo Tháp thì game làm mờ cả hai nút → luật chuyển về Luyện Công trước. Bấm xong game tự đưa nhân vật đi
    // và trừ lượt ngay lúc bắt đầu. Từ lúc bấm, app luôn đứng yên đủ một lượt (EventRunning) dù trang trả lời thế nào,
    // không bấm dồn. Thành công = số lượt giảm hoặc kênh Hệ thống báo phần thưởng; hết giờ chờ mà không thấy dấu hiệu
    // nào thì tính là hỏng, hỏng EventMaxFailures lần liền thì bỏ qua (thuyền: tới hết ngày; Boss tuần: 60 phút).

    private static readonly TimeSpan BoatRunTime = TimeSpan.FromMinutes(5);     // chuyến 4 phút + chuyển bản đồ
    private static readonly TimeSpan WeekBossRunTime = TimeSpan.FromMinutes(2); // lượt 75 giây
    private static readonly TimeSpan EventCheckGrace = TimeSpan.FromMinutes(2); // chờ trang đọc lại bảng Nhiệm vụ (mỗi 60 giây)
    private static readonly TimeSpan EventCmdGap = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EventBossHkMargin = TimeSpan.FromMinutes(5); // không bắt đầu ngay trước giờ Boss Hoàng Kim
    private static readonly TimeSpan WeekClaimGap = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EventResultTimeout = TimeSpan.FromMinutes(1);
    private const int EventMaxFailures = 3;
    private string? _eventPhase;
    private bool _eventPending;
    private DateTime _eventBusyUntil, _lastEventCmdAt, _lastClaimAt;
    private (string Mode, int LeftBefore)? _eventCheck; // đã bấm, đang chờ lượt đó xong
    private bool _eventDone;                           // lượt đang chờ đã có dấu hiệu thành công
    private int _eventFailures;

    private bool EventRunning(DateTime now) => now < _eventBusyUntil;

    private bool EventTooCloseToBossHk(string mode, DateTime now) =>
        mode is ModeRules.Boat or ModeRules.WeekBoss && BossHkSchedule(now + EventBossHkMargin);

    private static string EventIcon(string mode) => mode == ModeRules.Boat ? "⛵" : "👹";

    private int EventLeft(string mode) => mode == ModeRules.Boat ? BoatLeft : WeekBossLeft;

    // Trả về true nếu đổi
    private bool ApplyEvents(string boat, string weekBoss, DateTime now)
    {
        bool changed = false;
        if (boat.Length > 0 && boat != Boat)
        {
            if (Boat.Length > 0) Log.Write($"[{Account.Id}] boat {Boat} -> {boat}");
            Boat = boat;
            changed = true;
        }
        if (weekBoss.Length > 0 && weekBoss != WeekBoss)
        {
            if (WeekBoss.Length > 0) Log.Write($"[{Account.Id}] week boss {WeekBoss} -> {weekBoss}");
            WeekBoss = weekBoss;
            changed = true;
        }
        if (_eventCheck is { } chk && !_eventDone && EventLeft(chk.Mode) < chk.LeftBefore)
            EventSucceeded(chk.Mode, now);
        return changed;
    }

    // Kênh Hệ thống báo phần thưởng sau chuyến thuyền / lượt Boss tuần
    private void OnEventReward(string kind, string text, DateTime now)
    {
        var mode = kind == "boat" ? ModeRules.Boat : ModeRules.WeekBoss;
        Note(now, $"🎁 {text}", StatusLevel.Ok);
        if (_eventCheck?.Mode == mode && !_eventDone) EventSucceeded(mode, now);
    }

    private void EventSucceeded(string mode, DateTime now)
    {
        _eventDone = true;
        _eventFailures = 0;
        Note(now, $"{EventIcon(mode)} {ModeRules.Label(mode)}: đã tính 1 lượt (còn {EventLeft(mode)})", StatusLevel.Ok);
    }

    private void SuperviseEvents(DateTime now)
    {
        if (!Account.AutoMode || Screen != "game") return;

        // Trang không trả lời lệnh (tải lại giữa chừng…): vẫn chờ hết lượt như đã bấm, số lượt sẽ cho biết kết quả
        if (_eventPending && now - _lastEventCmdAt > EventResultTimeout) _eventPending = false;
        if (_eventPending) return;

        // Lượt đã thành công: chờ hết thời gian của lượt rồi mới sang lượt sau.
        // Chưa thấy dấu hiệu thành công: chờ thêm cho trang đọc lại số lượt, vẫn không có thì tính là hỏng.
        if (_eventCheck is { } chk && (_eventDone ? !EventRunning(now) : now > _eventBusyUntil + EventCheckGrace))
        {
            _eventCheck = null;
            if (!_eventDone) EventFailed(chk.Mode, "số lượt không giảm – gục ngã hoặc hết giờ", now);
            _eventDone = false;
        }
        if (EventRunning(now) || _eventCheck is not null) return;

        // Rương tuần: chỉ nhận khi đã đánh hết 3 lượt (nút sáng từ lượt đầu, nhận sớm thì theo hạng lúc đó)
        var cfg = SettingsProvider?.Invoke() ?? new AppSettings();
        if (cfg.AutoModeWeekBoss && WeekClaimable && WeekBossLeft == 0 && now - _lastClaimAt > WeekClaimGap)
        {
            _lastClaimAt = _lastEventCmdAt = now;
            _eventPending = true;
            RunCommand("weekClaim");
            return;
        }

        if (_eventPhase is not { } phase || ModeCode != ModeRules.Farm || now - _lastEventCmdAt < EventCmdGap) return;
        if (EventTooCloseToBossHk(phase, now) || EventLeft(phase) <= 0) return;
        _lastEventCmdAt = now;
        _eventPending = true;
        _eventCheck = (phase, EventLeft(phase));
        _eventDone = false;
        // Tính giờ từ lúc bấm (không đợi trang trả lời) → không bao giờ bấm lượt sau khi lượt này chưa hết
        _eventBusyUntil = now + (phase == ModeRules.Boat ? BoatRunTime : WeekBossRunTime);
        RunCommand(phase == ModeRules.Boat ? "boat" : "weekBoss");
    }

    private void OnEventResult(string name, string result, DateTime now)
    {
        _eventPending = false;
        Log.Write($"[{Account.Id}] {name}: {result}");
        if (name == "weekClaim")
        {
            if (result.StartsWith("ok"))
            {
                WeekBoss = WeekBoss.Replace(" claim", "");
                Note(now, "🎁 Nhận rương Boss tuần", StatusLevel.Ok);
            }
            return;
        }

        var mode = name == "boat" ? ModeRules.Boat : ModeRules.WeekBoss;
        if (result.StartsWith("ok"))
        {
            Note(now, mode == ModeRules.Boat ? "⛵ Lên thuyền" : "👹 Khiêu chiến Boss tuần", StatusLevel.Ok);
            SetStatus(mode == ModeRules.Boat ? "⛵ Đang đi thuyền" : "👹 Đang đánh Boss tuần", StatusLevel.Ok);
            return;
        }
        // Bảng không có nút "Lên thuyền": nhân vật đang ở trên thuyền → cứ chờ như đã bấm.
        // Lỗi khác sau khi bấm: chờ hết lượt, số lượt sẽ cho biết có đi được không.
        if (result == "no-button" || result.StartsWith("error")) return;

        // Chưa bấm được gì: huỷ thời gian chờ
        _eventCheck = null;
        _eventBusyUntil = now;
        if (result is "busy" or "boss-hk" or "not-ready") return; // đang mở bảng / giờ Boss HK → lần sau
        EventFailed(mode, result, now); // nút mờ, không có tab…
    }

    private void EventFailed(string mode, string why, DateTime now)
    {
        var label = ModeRules.Label(mode);
        if (++_eventFailures < EventMaxFailures)
        {
            Note(now, $"{EventIcon(mode)} {label} không thành ({why}) – thử lại", StatusLevel.Warn);
            return;
        }
        _eventFailures = 0;
        _eventPhase = null;
        // Thuyền: tới 0 giờ (giờ Việt Nam) hôm sau; Boss tuần: 60 phút
        var until = mode == ModeRules.Boat ? NextVnMidnight(now) : now + ModeBlockDefault;
        _modeBlockedUntil[mode] = until;
        Note(now, $"{EventIcon(mode)} {label} hỏng {EventMaxFailures} lần liền ({why}) – bỏ qua tới {until:HH:mm}", StatusLevel.Warn);
    }

    // ---------- Nhiệm Vụ: mua Lệnh Bài Dã Tẩu trước rồi mới vào ----------

    // Số lệnh bài cần mua để hôm nay dùng hết lượt lệnh bài: (tối đa dùng − đã dùng) − đang có.
    // Chưa đọc được số liệu → 100 (trang tự giới hạn theo số còn mua được hôm nay).
    public int TokensWanted()
    {
        if (!ModeRules.TryFraction(LdtUsed, out var used, out var usedMax) || !int.TryParse(LdtHave, out var have))
            return LdtDailyBuyMax;
        return Math.Clamp(usedMax - used - have, 0, LdtDailyBuyMax);
    }

    private bool BoughtFullToday => ModeRules.TryFraction(LdtBuy, out var bought, out var max) && bought >= max;

    // Bạn bấm Nhiệm Vụ: luôn kiểm tra và mua nếu còn thiếu. Luật 🔁: chỉ thử mua một lần mỗi ngày.
    private void StartQuest(DateTime now, bool byYou)
    {
        var want = BoughtFullToday ? 0 : TokensWanted();
        bool tried = _ldtTriedDay == now.Date;
        // Bạn bấm: luôn chạy lượt chuẩn bị (nhận quà + mua nếu thiếu). Luật 🔁: chỉ khi cần mua và hôm nay chưa thử.
        if (Screen != "game" || (!byYou && (want <= 0 || tried)))
        {
            RunCommand("mode", ModeRules.Quest);
            return;
        }
        if (want > 0) _ldtTriedDay = now.Date;
        _ldtBuyingSince = now;
        _questByRule = !byYou;
        Log.Write($"[{Account.Id}] quest prep: gifts + buy {want} Dã Tẩu tokens");
        SetStatus(want > 0 ? $"🛒 Nhận quà, mua {want} lệnh bài…" : "🎁 Đang nhận quà…", StatusLevel.Warn);
        Post(new { t = "buyLdt", arg = new { want, keepKnb = 0 } });
    }

    private void OnLdtResult(JsonElement m, DateTime now)
    {
        _ldtBuyingSince = null;
        int Int(string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var r = Str(m, "r");
        int bought = Int("bought"), spent = Int("spent"), converted = Int("converted"), opened = Int("opened"), gained = Int("gained"), left = Int("left");
        if (Str(m, "boughtToday") is { Length: > 0 } bt) LdtBuy = bt;
        if (m.TryGetProperty("have", out var hv) && hv.ValueKind == JsonValueKind.Number) LdtHave = hv.GetInt32().ToString();
        Log.Write($"[{Account.Id}] buy tokens: {r} bought={bought} spent={spent} converted={converted} opened={opened} gained={gained} left={left}");

        var parts = new List<string>();
        if (opened > 0) parts.Add($"mở {opened} Đại Hồng Bao (+{gained} KNB)");
        if (converted > 0) parts.Add($"đổi {converted} KNB → KNB khóa");
        var extra = parts.Count > 0 ? " · " + string.Join(" · ", parts) : "";
        if (bought > 0) Note(now, $"🛒 Mua {bought} lệnh bài Dã Tẩu (−{spent} KNB khóa){extra}", StatusLevel.Ok);
        switch (r)
        {
            case "ok": break;
            case "short": Raise("ldt-short", $"Chỉ mua được {bought} lệnh bài, còn thiếu {left} – hết KNB và Đại Hồng Bao"); break;
            case "busy": Note(now, "🛒 Chưa mua được lệnh bài: đang mở bảng trong game, sẽ thử lại lần bấm Nhiệm Vụ sau"); _ldtTriedDay = null; break;
            default: Raise("ldt-error", $"Mua lệnh bài bị lỗi ({r}), đã mua {bought}"); break;
        }
        InfoChanged?.Invoke();
        GoQuest(now);
    }

    private void GoQuest(DateTime now)
    {
        if (_questByRule)
        {
            _pendingAutoMode = ModeRules.Quest;
            _lastAutoModeCmd = now;
        }
        else
        {
            _userModeAt = now;
        }
        RunCommand("mode", ModeRules.Quest);
    }

    // ---------- Kéo quái (chỉ khi Leo Tháp, acc có bật 🏃) ----------

    private void SuperviseKite(DateTime now)
    {
        if (!Account.Kite || Screen != "game" || ModeCode != "tower") return;
        if (SettingsProvider?.Invoke() is not { } s) return;
        if (now - _lastKiteAt < TimeSpan.FromSeconds(s.KiteIntervalSeconds)) return;

        _lastKiteAt = now;
        var (dx, dy) = KiteDirections.Offset(s.KiteDirection, s.KiteDistance, _kiteStep);
        if (s.KiteDirection == KiteDirections.Square)
        {
            // Hình vuông theo minimap: trang tự quyết định đã tới góc chưa (xem OnKiteResult)
            RunCommand("kite", new { mode = "square", side = s.KiteDistance, step = _kiteStep, dx, dy });
        }
        else
        {
            _kiteStep++;
            RunCommand("kite", new { mode = "relative", dx, dy });
        }
    }

    private void OnKiteResult(string result)
    {
        // Chỉ ghi log khi kết quả đổi, không ghi mỗi lần kéo
        if (result != _lastKiteResult) Log.Write($"[{Account.Id}] kite: {result}");
        _lastKiteResult = result;

        // Hình vuông: tới góc rồi → góc kế tiếp; nhắm 3 lần chưa tới (bị Auto kéo đi) → bỏ qua góc đó
        if (result == "ok-next" || result == "ok-rel") { _kiteStep++; _kiteTries = 0; }
        else if (result == "ok" && ++_kiteTries >= 3) { _kiteStep++; _kiteTries = 0; }

        switch (result)
        {
            case "ok" or "ok-next": SetStatus("🏃 Đang kéo quái", StatusLevel.Ok); break;
            case "ok-rel": SetStatus("🏃 Kéo quái (không đọc được minimap)", StatusLevel.Warn); break;
            case "manual": SetStatus("Kéo quái tạm dừng: Auto đang tắt", StatusLevel.Warn); break;
            case "not-tower": break; // vừa rời tháp, lần sau bộ giám sát tự bỏ qua
            case "busy": break;      // đang mở bảng hoặc đang chạy dở, đợi lần sau
            default: SetStatus($"Kéo quái lỗi ({result})", StatusLevel.Error); break;
        }
    }

    // Phần thưởng tháp (kênh Hệ thống của game): chỉ ghi log mốc 100 tầng, và mọi lần có đồ Hoàng Kim. Không thông báo.
    private void OnTowerReward(int floor, string reward, DateTime now)
    {
        if (reward.Length == 0) return;
        bool milestone = floor > 0 && floor % 100 == 0, golden = reward.Contains("Hoàng Kim");
        // Dòng nhặt đồ Hoàng Kim không ghi tầng → lấy tầng đang đứng (nếu đang trong tháp)
        if (floor <= 0 && golden)
        {
            Log.Write($"[{Account.Id}] golden item: {reward}");
            Note(now, TowerFloor is { } f ? $"👑 {reward} (tháp tầng {f})" : $"👑 {reward}", StatusLevel.Ok);
            return;
        }
        if (!milestone && !golden) return;
        Log.Write($"[{Account.Id}] tower reward {floor}: {reward}");
        Note(now, golden ? $"👑 Tháp tầng {floor}: {reward}" : $"🏆 Mốc tháp {floor}: {reward}", StatusLevel.Ok);
    }

    // ---------- Tiện ích ----------

    // Báo vấn đề cho trung tâm cảnh báo và ghi vào nhật ký hoạt động
    private void Raise(string key, string message)
    {
        var now = _lastNow == default ? DateTime.Now : _lastNow;
        Note(now, "⚠ " + message, StatusLevel.Warn);
        // Ghi vào báo cáo cuối ngày (bỏ qua cảnh báo thường gặp, không phải việc hỏng)
        if (key is not ("idle" or "bag" or "kicked" or "login-net"))
        {
            if (_problemsDay != VnDate(now)) { _problemsToday.Clear(); _problemsDay = VnDate(now); }
            _problemsToday.Add(Shorten(message, 70) ?? message);
        }
        Problem?.Invoke(key, message);
    }

    // ---------- Không đọc được số liệu từ game ----------
    // Trang thấy bảng nhưng chữ không đúng mẫu (game vừa đổi giao diện…). Báo một lần mỗi ngày cho mỗi loại,
    // ghi đoạn chữ vào log để sửa. Bước liên quan tự bỏ qua (số liệu "chưa biết" thì luật không chọn bước đó).

    private static readonly Dictionary<string, string> UnreadableLabels = new()
    {
        ["knb"] = "số KNB", ["daTau"] = "số Dã Tẩu", ["dungeons"] = "số lượt Phó bản", ["boat"] = "số chuyến thuyền",
        ["weekBoss"] = "số lượt Boss tuần", ["assassin"] = "số Lệnh Bài Sát Thủ", ["shop"] = "Kỳ Trân Các (lệnh bài Dã Tẩu)",
    };
    private readonly Dictionary<string, DateOnly> _unreadableNoted = new();

    private void OnUnreadable(string what, string sample, DateTime now)
    {
        Log.Write($"[{Account.Id}] unreadable {what}: {sample}");
        var day = VnDate(now);
        if (_unreadableNoted.TryGetValue(what, out var d) && d == day) return;
        _unreadableNoted[what] = day;
        var label = UnreadableLabels.GetValueOrDefault(what, what);
        Raise($"unreadable-{what}", $"Không đọc được {label} từ game – có thể game vừa đổi giao diện; bước liên quan tạm bỏ qua");
    }

    // ---------- Báo cáo cuối ngày (23:55 giờ Việt Nam, trước khi game đặt lại lượt) ----------

    private static readonly TimeSpan SummaryAt = new(23, 55, 0);
    private readonly List<string> _problemsToday = new();
    private DateOnly _problemsDay, _summaryDay, _floorDay;
    private int? _dayStartFloor;

    private static DateOnly VnDate(DateTime now) => DateOnly.FromDateTime(now.ToUniversalTime().AddHours(7));

    private void SuperviseDailySummary(DateTime now)
    {
        var vn = now.ToUniversalTime().AddHours(7);
        var day = DateOnly.FromDateTime(vn);
        if (TowerFloor is { } f && _floorDay != day) { _floorDay = day; _dayStartFloor = f; }
        if (Screen != "game" || vn.TimeOfDay < SummaryAt || _summaryDay == day) return;
        _summaryDay = day;
        Note(now, DailySummary(day), _problemsDay == day && _problemsToday.Count > 0 ? StatusLevel.Warn : StatusLevel.Ok);
    }

    // "📋 Tổng kết 28/10: Dã Tẩu 200/200 ✅ · Lệnh bài mua 100/100 ✅ · Phó bản 0/12 ✅ · Thuyền 0/3 ✅ · … · Tháp +85 tầng · 1 lỗi: …"
    public string DailySummary(DateOnly day)
    {
        string Mark(bool ok) => ok ? "✅" : "❌";
        var parts = new List<string>();
        if (ModeRules.TryFraction(DaTau, out var dtDone, out var dtMax)) parts.Add($"Dã Tẩu {dtDone}/{dtMax} {Mark(dtDone >= dtMax)}");
        if (ModeRules.TryFraction(LdtBuy, out var bought, out var buyMax)) parts.Add($"Lệnh bài mua {bought}/{buyMax} {Mark(bought >= buyMax)}");
        if (ModeRules.TryFraction(PhoBan, out var dgLeft, out _)) parts.Add($"Phó bản còn {dgLeft} {Mark(dgLeft == 0)}");
        if (Boat.Length > 0) parts.Add($"Thuyền còn {BoatLeft} {Mark(BoatLeft == 0)}");
        if (WeekBoss.Length > 0) parts.Add($"Boss tuần còn {WeekBossLeft}");
        if (Assassin.Length > 0) parts.Add($"LB Sát Thủ còn {AssassinTokens}");
        if (TowerFloor is { } floor && _dayStartFloor is { } start && floor > start) parts.Add($"Tháp +{floor - start} tầng (tới {floor})");
        var problems = _problemsDay == day ? _problemsToday.Distinct().ToList() : new List<string>();
        if (problems.Count > 0) parts.Add($"{problems.Count} lỗi: {string.Join("; ", problems.Take(3))}");
        return $"📋 Tổng kết {day.ToString("dd'/'MM")}: " + (parts.Count > 0 ? string.Join(" · ", parts) : "chưa đọc được số liệu");
    }

    private void SetStatus(string text, StatusLevel level)
    {
        StatusText = text;
        StatusLevel = level;
        StatusChanged?.Invoke();
    }

    // Trường chữ trong tin trạng thái; thiếu (trang chạy bản script cũ) thì coi là rỗng
    private static string Str(JsonElement s, string name) =>
        s.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Shorten(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
