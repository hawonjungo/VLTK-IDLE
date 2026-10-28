using System.Text.Json;
using VLMulti.Core.Accounts;
using VLMulti.Core.Sessions;
using VLMulti.Core.Settings;

namespace VLMulti.Tests;

// Trang giả: ghi lại các lệnh AccountController gửi vào trang
internal sealed class FakePage : IGamePage
{
    public List<JsonElement> Sent { get; } = new();
    public int Reloads { get; private set; }

    public void Post(string json) => Sent.Add(JsonDocument.Parse(json).RootElement.Clone());
    public void Reload() => Reloads++;

    public List<string> Commands => Sent
        .Where(m => m.GetProperty("t").GetString() == "cmd")
        .Select(m => m.GetProperty("name").GetString()!)
        .ToList();
}

public class AccountControllerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private static (AccountController Ctl, FakePage Page, AppSettings Cfg) Make(bool withPassword = true)
    {
        var page = new FakePage();
        var cfg = new AppSettings();
        var acc = new AccountSlot { Id = "acc1", Name = "Acc 1" };
        var ctl = new AccountController(acc, page)
        {
            SettingsProvider = () => cfg,
            BossHkSchedule = _ => false, // không phụ thuộc giờ máy chạy test
            CredentialProvider = _ => withPassword ? ("user1", "secret") : null,
        };
        return (ctl, page, cfg);
    }

    private static string Status(string screen, string mode = "", string zone = "", string dt = "", string pb = "", string loginMsg = "",
                                 string lv = "50", string gold = "1,2 vạn", string bag = "",
                                 string ldtHave = "", string ldtUsed = "", string ldtBuy = "") =>
        JsonSerializer.Serialize(new { t = "status", screen, name = "", lv, gold, knb = "10 🔒2", mode, modeText = mode, zone, dt, pb, loginMsg, sect = "wudang", bag,
                                       ldtHave, ldtUsed, ldtBuy, dtMax = "200" });

    // Cho trang báo trạng thái và chạy bộ giám sát mỗi 3 giây trong `seconds` giây
    private static DateTime Run(AccountController ctl, DateTime from, int seconds, string status)
    {
        var t = from;
        for (int s = 0; s <= seconds; s += 3, t = from.AddSeconds(s))
        {
            ctl.OnPageMessage(status, t);
            ctl.Tick(t);
        }
        return t;
    }

    [Fact]
    public void Logs_in_after_settle_and_stagger()
    {
        var (ctl, page, _) = Make();
        ctl.LoginStagger = TimeSpan.FromSeconds(6);
        Run(ctl, T0, 6, Status("login"));
        Assert.DoesNotContain("login", page.Commands); // chưa đủ 3 + 6 giây
        Run(ctl, T0.AddSeconds(9), 3, Status("login"));
        Assert.Single(page.Commands, c => c == "login");
    }

    [Fact]
    public void No_login_without_saved_password()
    {
        var (ctl, page, _) = Make(withPassword: false);
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 60, Status("login"));
        Assert.DoesNotContain("login", page.Commands);
        // Không tự vào được → báo bạn, đèn đỏ
        Assert.Contains("no-password", problems);
        Assert.Equal(Core.Sessions.Health.Bad, ctl.HealthAt(t).Level);
    }

    [Fact]
    public void Network_error_is_retried_not_counted_as_wrong_password()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);

        // 1 lần đăng nhập, rồi trang báo "Failed to fetch" cho tới hết thời gian chờ (20 giây)
        var t = Run(ctl, T0, 6, Status("login"));
        t = Run(ctl, t, 24, Status("login", loginMsg: "Failed to fetch"));
        Assert.Contains("login-net", problems);
        Assert.DoesNotContain("login-fail", problems);

        // Phải chờ 60 giây mới thử lại
        int before = page.Commands.Count(c => c == "login");
        t = Run(ctl, t, 30, Status("login"));
        Assert.Equal(before, page.Commands.Count(c => c == "login"));
        Run(ctl, t, 45, Status("login"));
        Assert.True(page.Commands.Count(c => c == "login") > before);
    }

    [Fact]
    public void Stops_after_two_failed_logins()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        Run(ctl, T0, 120, Status("login", loginMsg: "Sai mật mã"));
        Assert.Contains("login-fail", problems);
        Assert.Equal(2, page.Commands.Count(c => c == "login"));
    }

    [Fact]
    public void Kicked_out_waits_a_minute_before_relogin()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 15, Status("game"));
        t = Run(ctl, t, 30, Status("login"));
        Assert.Contains("kicked", problems);
        Assert.DoesNotContain("login", page.Commands);
        Run(ctl, t, 40, Status("login"));
        Assert.Contains("login", page.Commands);
    }

    [Fact]
    public void Enters_game_from_character_select()
    {
        var (ctl, page, _) = Make();
        Run(ctl, T0, 6, Status("charselect"));
        Assert.Contains("enter", page.Commands);
    }

    [Fact]
    public void Kites_only_in_tower_and_only_when_enabled()
    {
        var (ctl, page, cfg) = Make();
        cfg.KiteIntervalSeconds = 5;
        var t = Run(ctl, T0, 30, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 10"));
        Assert.DoesNotContain("kite", page.Commands); // chưa bật 🏃

        ctl.SetKite(true, t);
        t = Run(ctl, t, 30, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 10"));
        int inTower = page.Commands.Count(c => c == "kite");
        Assert.True(inTower >= 4);
        Assert.Equal(10, ctl.TowerFloor);

        Run(ctl, t, 30, Status("game", mode: "farm"));
        Assert.Equal(inTower, page.Commands.Count(c => c == "kite")); // ra khỏi tháp → ngừng
    }

    [Fact]
    public void Auto_mode_switches_by_rules_after_warmup()
    {
        var (ctl, page, _) = Make();
        ctl.SetAutoMode(true, T0);
        Run(ctl, T0, 100, Status("game", mode: "farm", dt: "10/40", pb: "0/2"));
        var modes = page.Sent.Where(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "mode")
            .Select(m => m.GetProperty("arg").GetString()).ToList();
        // Dã Tẩu chưa đủ → Nhiệm Vụ: mua lệnh bài trước (một lần), mua xong mới vào
        Assert.Empty(modes);
        Assert.Single(page.Sent, m => m.GetProperty("t").GetString() == "buyLdt");
        ctl.OnPageMessage("""{"t":"ldtResult","r":"ok","bought":100,"spent":2000,"left":0,"boughtToday":"100/100","have":100}""", T0.AddSeconds(110));
        modes = page.Sent.Where(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "mode")
            .Select(m => m.GetProperty("arg").GetString()).ToList();
        Assert.Equal(new[] { "quest" }, modes);
    }

    [Fact]
    public void Reads_knb_sect_and_daily_counts()
    {
        var (ctl, _, _) = Make();
        Run(ctl, T0, 15, Status("game", mode: "quest", dt: "52/52 (+12 lệnh bài)"));
        Assert.Equal(10, ctl.Stats.Knb);
        Assert.Equal(2, ctl.KnbLocked);
        Assert.Equal("52/52", ctl.DaTauToday);
        Assert.Equal("Võ Đang phái", ctl.Account.Sect);
    }

    private static string Tower(int floor) => Status("game", mode: "tower", zone: $"Thông Thiên Tháp · tầng {floor}");

    [Fact]
    public void Activity_logs_level_up_and_tower_milestones()
    {
        var (ctl, _, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 18"));
        t = Run(ctl, t, 3, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 19", lv: "51"));
        t = Run(ctl, t, 3, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 21", lv: "51"));
        var texts = ctl.Activity.Items.Select(e => e.Text).ToList();
        Assert.Contains("⬆ Lên cấp 51", texts);
        Assert.Contains("🗼 Vào Thông Thiên Tháp, tầng 18", texts);
        Assert.Contains("🗼 Lên tầng 21", texts);       // qua mốc 20
        Assert.DoesNotContain("🗼 Lên tầng 19", texts); // không ghi từng tầng
        Assert.Equal("🗼 Lên tầng 21", texts[0]);        // mới nhất ở đầu
    }

    [Fact]
    public void Leaving_tower_without_rule_counts_as_fall()
    {
        var (ctl, _, _) = Make();
        var t = Run(ctl, T0, 15, Tower(500));
        t = Run(ctl, t, 6, Tower(501));
        Run(ctl, t, 6, Status("game", mode: "farm"));
        Assert.Equal(1, ctl.Tower.Falls);
        Assert.Contains(ctl.Activity.Items, e => e.Text.Contains("Chết nhanh ở tầng 501") && e.Level == StatusLevel.Warn);
        Assert.Null(ctl.TowerFloor);
    }

    [Fact]
    public void Tower_floor_rate_and_progress_counts_as_activity()
    {
        var (ctl, _, _) = Make();
        var t = Run(ctl, T0, 15, Tower(100));
        // 10 phút, mỗi phút lên 1 tầng; tiền và cấp không đổi
        for (int i = 1; i <= 10; i++) t = Run(ctl, t, 60, Tower(100 + i));
        Assert.InRange(ctl.Tower.FloorsPerHour(t)!.Value, 50, 62); // mỗi vòng Run dài hơn 60 giây một chút
        Assert.True(ctl.Stats.IdleFor(t) < TimeSpan.FromMinutes(2));
        Assert.Equal(Health.Good, ctl.HealthAt(t).Level);
    }

    [Fact]
    public void Bag_nearly_full_alerts_once()
    {
        var (ctl, _, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 15, Status("game", bag: "50/120"));
        Assert.False(ctl.BagNearlyFull);
        t = Run(ctl, t, 6, Status("game", bag: "116/120"));
        t = Run(ctl, t, 6, Status("game", bag: "117/120"));
        Assert.True(ctl.BagNearlyFull);
        Assert.Single(problems, p => p == "bag");
        Assert.Equal((Health.Warn, "Hòm đồ gần đầy 117/120"), ctl.HealthAt(t));
    }

    private static List<string?> ModeCommands(FakePage page) => page.Sent
        .Where(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "mode")
        .Select(m => m.GetProperty("arg").GetString()).ToList();

    // Vào tháp, sống `lived` giây, chết (game đẩy ra Luyện Công), chờ `after` giây
    private static DateTime DieInTower(AccountController ctl, DateTime t, int lived, int after = 30)
    {
        t = Run(ctl, t, lived, Tower(700));
        return Run(ctl, t, after, Status("game", mode: "farm"));
    }

    [Fact]
    public void Tower_death_returns_to_tower_when_you_chose_tower()
    {
        var (ctl, page, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "farm"));
        ctl.RequestMode("tower", t);
        Assert.True(ctl.Account.StayInTower);
        t = Run(ctl, t.AddSeconds(70), 10 * 60, Tower(700)); // sống 10 phút
        t = Run(ctl, t, 30, Status("game", mode: "farm"));    // chết
        Assert.Equal(new[] { "tower", "tower" }, ModeCommands(page)); // bạn bấm + tự vào lại
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("💀 Chết ở tầng 700"));
    }

    [Fact]
    public void Three_fast_deaths_rest_tower_and_alert()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 15, Status("game", mode: "farm"));
        ctl.RequestMode("tower", t);
        t = t.AddSeconds(70);
        for (int i = 0; i < 3; i++) t = DieInTower(ctl, t, 60); // mỗi lần sống 1 phút
        Assert.Contains("tower-fast-death", problems);
        int sent = ModeCommands(page).Count;
        t = Run(ctl, t, 30 * 60, Status("game", mode: "farm"));  // đang nghỉ tháp: không vào lại
        Assert.Equal(sent, ModeCommands(page).Count);
        Run(ctl, t.AddMinutes(95), 30, Status("game", mode: "farm")); // hết 2 giờ → vào lại
        Assert.Equal(sent + 1, ModeCommands(page).Count);
    }

    [Fact]
    public void Slow_death_resets_fast_death_count()
    {
        var (ctl, _, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 15, Status("game", mode: "farm"));
        ctl.RequestMode("tower", t);
        t = t.AddSeconds(70);
        t = DieInTower(ctl, t, 60);
        t = DieInTower(ctl, t, 60);
        t = DieInTower(ctl, t, 5 * 60); // sống lâu → đếm lại
        t = DieInTower(ctl, t, 60);
        DieInTower(ctl, t, 60);
        Assert.DoesNotContain("tower-fast-death", problems);
        Assert.Equal(5, ctl.Tower.Falls);
    }

    [Fact]
    public void Switching_out_of_tower_yourself_is_not_a_death()
    {
        var (ctl, _, _) = Make();
        var t = Run(ctl, T0, 15, Tower(300));
        ctl.RequestMode("farm", t);
        Run(ctl, t, 30, Status("game", mode: "farm"));
        Assert.Equal(0, ctl.Tower.Falls);
        Assert.False(ctl.Account.StayInTower);
    }

    private static List<JsonElement> BuyRequests(FakePage page) =>
        page.Sent.Where(m => m.GetProperty("t").GetString() == "buyLdt").ToList();

    [Fact]
    public void Quest_button_buys_tokens_first_then_enters_quest()
    {
        var (ctl, page, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "farm", dt: "40/40", ldtHave: "2", ldtUsed: "0/160", ldtBuy: "2/100"));
        ctl.RequestMode("quest", t);
        var buy = Assert.Single(BuyRequests(page));
        Assert.Equal(100, buy.GetProperty("arg").GetProperty("want").GetInt32()); // 160 − 0 − 2 = 158 → tối đa 100
        Assert.Empty(ModeCommands(page));                                         // chưa vào Nhiệm Vụ khi đang mua

        ctl.OnPageMessage("""{"t":"ldtResult","r":"ok","bought":98,"spent":1960,"converted":630,"opened":0,"gained":0,"left":0,"boughtToday":"100/100","have":100}""", t.AddSeconds(20));
        Assert.Equal(new[] { "quest" }, ModeCommands(page));
        Assert.Equal("100/100", ctl.LdtBuy);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🛒 Mua 98 lệnh bài") && e.Text.Contains("đổi 630 KNB"));
    }

    [Fact]
    public void Quest_button_skips_buying_when_bought_or_enough()
    {
        var (ctl, page, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "farm", dt: "40/40", ldtHave: "60", ldtUsed: "100/160", ldtBuy: "40/100"));
        ctl.RequestMode("quest", t); // đủ dùng: 160 − 100 − 60 = 0 → chỉ nhận quà, không mua
        Assert.Equal(0, Assert.Single(BuyRequests(page)).GetProperty("arg").GetProperty("want").GetInt32());
        ctl.OnPageMessage("""{"t":"ldtResult","r":"ok","bought":0,"spent":0,"left":0}""", t.AddSeconds(5));
        Assert.Equal(new[] { "quest" }, ModeCommands(page));
        Assert.DoesNotContain(ctl.Activity.Items, e => e.Text.StartsWith("🛒"));

        var (ctl2, page2, _) = Make();
        t = Run(ctl2, T0, 15, Status("game", mode: "farm", dt: "40/40", ldtHave: "0", ldtUsed: "0/160", ldtBuy: "100/100"));
        ctl2.RequestMode("quest", t); // hôm nay đã mua đủ 100
        Assert.Equal(0, Assert.Single(BuyRequests(page2)).GetProperty("arg").GetProperty("want").GetInt32());

        // Luật 🔁 (không phải bạn bấm): không cần mua thì vào thẳng Nhiệm Vụ, không mở bảng quà
        var (ctl3, page3, _) = Make();
        t = Run(ctl3, T0, 15, Status("game", mode: "farm", dt: "10/40", ldtHave: "60", ldtUsed: "100/160", ldtBuy: "40/100"));
        ctl3.SetAutoMode(true, t);
        Run(ctl3, t, 120, Status("game", mode: "farm", dt: "10/40", ldtHave: "60", ldtUsed: "100/160", ldtBuy: "40/100"));
        Assert.Empty(BuyRequests(page3));
        Assert.Equal("quest", ModeCommands(page3).First());
    }

    [Fact]
    public void Short_of_money_alerts_and_still_enters_quest()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 15, Status("game", mode: "farm", dt: "40/40", ldtHave: "0", ldtUsed: "0/160", ldtBuy: "0/100"));
        ctl.RequestMode("quest", t);
        ctl.OnPageMessage("""{"t":"ldtResult","r":"short","bought":64,"spent":1280,"converted":0,"opened":1,"gained":300,"left":36}""", t.AddSeconds(30));
        Assert.Contains("ldt-short", problems);
        Assert.Equal(new[] { "quest" }, ModeCommands(page));
    }

    [Fact]
    public void Buying_times_out_and_enters_quest()
    {
        var (ctl, page, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "farm", dt: "40/40", ldtHave: "0", ldtUsed: "0/160", ldtBuy: "0/100"));
        ctl.RequestMode("quest", t);
        Run(ctl, t, 200, Status("game", mode: "farm", dt: "40/40", ldtHave: "0", ldtUsed: "0/160", ldtBuy: "0/100"));
        Assert.Equal("quest", ModeCommands(page).First()); // hết 3 phút không thấy kết quả → vào Nhiệm Vụ luôn
    }

    [Fact]
    public void Quest_button_starts_auto_loop_and_other_modes_stop_it()
    {
        var (ctl, _, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "farm", dt: "40/40", ldtHave: "60", ldtUsed: "100/160", ldtBuy: "40/100"));
        ctl.RequestMode("quest", t);
        Assert.True(ctl.Account.AutoMode);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🔁 Bắt đầu vòng tự động"));
        ctl.RequestMode("farm", t.AddSeconds(5));
        Assert.False(ctl.Account.AutoMode);
    }

    // Chạy tới khi app ra lệnh đổi sang `mode` (trang giả không tự đổi chế độ như game thật)
    private static DateTime RunUntilMode(AccountController ctl, FakePage page, DateTime t, string status, string mode, int maxSeconds = 300)
    {
        int before = ModeCommands(page).Count;
        for (int s = 0; s <= maxSeconds; s += 3, t = t.AddSeconds(3))
        {
            ctl.OnPageMessage(status, t);
            ctl.Tick(t);
            if (ModeCommands(page).Count > before && ModeCommands(page).Last() == mode) return t;
        }
        throw new Xunit.Sdk.XunitException($"không thấy lệnh đổi sang {mode}");
    }

    [Fact]
    public void Auto_loop_moves_quest_dungeon_tower_and_shows_steps()
    {
        var (ctl, page, _) = Make();
        // Dã Tẩu xong, hết lệnh bài, đã mua đủ hôm nay → Phó Bản còn lượt → vào Phó Bản
        var done = Status("game", mode: "quest", dt: "200/200", pb: "2/4", ldtHave: "0", ldtUsed: "160/160", ldtBuy: "100/100");
        var t = Run(ctl, T0, 15, done);
        ctl.RequestMode("quest", t);
        t = RunUntilMode(ctl, page, t, done, "dg");
        t = Run(ctl, t.AddSeconds(3), 6, Status("game", mode: "dg", dt: "200/200", pb: "2/4", ldtHave: "0", ldtUsed: "160/160", ldtBuy: "100/100"));
        var steps = ctl.AutoChain(t);
        // Nhiệm Vụ, Phó Bản, Đi thuyền, Boss tuần, Sát Thủ, Leo Tháp, Luyện Công
        Assert.Equal(new[] { "done", "current", "todo", "todo", "todo", "todo", "todo" }, steps.Select(x => x.State));
        Assert.Contains("▶ Phó Bản (còn 2/4)", ctl.AutoChainText(t));

        // Hết lượt Phó bản → Leo Tháp
        RunUntilMode(ctl, page, t, Status("game", mode: "dg", dt: "200/200", pb: "0/4", ldtHave: "0", ldtUsed: "160/160", ldtBuy: "100/100"), "tower");
    }

    [Fact]
    public void Auto_loop_rests_tower_after_fast_deaths_then_returns()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        string Tw(string mode) => Status("game", mode: mode, zone: mode == "tower" ? "Thông Thiên Tháp · tầng 900" : "",
            dt: "200/200", pb: "0/4", ldtHave: "0", ldtUsed: "160/160", ldtBuy: "100/100");
        var t = Run(ctl, T0, 15, Tw("tower"));
        ctl.SetAutoMode(true, t);
        for (int i = 0; i < 3; i++)
        {
            t = Run(ctl, t, 60, Tw("tower"));  // sống 1 phút
            t = Run(ctl, t, 6, Tw("farm"));    // chết
            if (i < 2) t = RunUntilMode(ctl, page, t, Tw("farm"), "tower").AddSeconds(3); // luật đưa vào lại tháp
        }
        Assert.Contains("tower-fast-death", problems);
        var rest = ctl.AutoChain(t).Single(x => x.Mode == "tower");
        Assert.Equal("rest", rest.State);
        Assert.StartsWith("nghỉ tới", rest.Detail);
        int sent = ModeCommands(page).Count;
        t = Run(ctl, t, 30 * 60, Tw("farm"));          // đang nghỉ: ở Luyện Công
        Assert.Equal(sent, ModeCommands(page).Count);
        RunUntilMode(ctl, page, t.AddMinutes(95), Tw("farm"), "tower"); // hết 2 giờ → lại Leo Tháp
    }

    [Fact]
    public void Boss_hk_window_keeps_app_hands_off()
    {
        var (ctl, page, cfg) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        cfg.KiteIntervalSeconds = 5;
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        string Tw(string mode) => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode,
            zone = mode == "tower" ? "Thông Thiên Tháp · tầng 900" : "Hoa Sơn",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60",
            hk = "Đợt sau: 12:00",
        });
        var t = Run(ctl, T0, 15, Tw("tower"));
        ctl.RequestMode("tower", t);
        ctl.SetAutoMode(true, t);
        ctl.SetKite(true, t);
        t = Run(ctl, t.AddSeconds(70), 30, Tw("tower"));
        int sent = page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd");

        // Đợt boss: game tự đưa đi đánh (rời tháp) → không tính là chết, không kéo về, không kéo quái, không báo
        boss = true;
        t = Run(ctl, t, 6, Tw("tower"));
        ctl.OnPageMessage("""{"t":"sysNote","text":"👑 Boss Hoàng Kim Hà Linh Phiêu xuất hiện — tạm rời Tháp tầng 900 đi săn Boss.","boss":true}""", t);
        t = Run(ctl, t, 10 * 60, Tw("farm"));
        Assert.Equal(sent, page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd"));
        Assert.Equal(0, ctl.Tower.Falls);
        Assert.Empty(problems);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("👑 Boss Hoàng Kim: để game tự đi đánh"));

        // Hạ boss xong mà game không đưa về tháp → về tháp ngay, không chờ hết đợt
        ctl.OnPageMessage("""{"t":"sysNote","text":"🎁 Hạ Boss Hoàng Kim: 36.00M kinh nghiệm, 1 Lam Thủy Tinh","boss":true}""", t);
        RunUntilMode(ctl, page, t, Tw("farm"), "tower", 3 * 60);
        Assert.True(boss); // vẫn trong khung giờ boss
    }

    [Fact]
    public void Logging_in_inside_a_finished_dungeon_moves_on_quickly_or_hints_when_auto_is_off()
    {
        // Thoát game giữa phó bản: vào lại game vẫn ở chế độ Phó Bản dù đã hết lượt
        var dg = Status("game", mode: "dg", zone: "Tiến Cúc động", dt: "200/200", pb: "0/14", ldtHave: "0", ldtUsed: "160/160", ldtBuy: "100/100");
        var (ctl, page, _) = Make();
        ctl.SetAutoMode(true, T0);
        var t = RunUntilMode(ctl, page, T0, dg, "tower", 60);
        Assert.True(t - T0 < TimeSpan.FromSeconds(50));      // không chờ đủ 60 giây khởi động

        // Vòng tự động tắt (bạn tự chọn chế độ): không tự chuyển, chỉ nhắc một lần
        var (ctl2, page2, _) = Make();
        Run(ctl2, T0, 120, dg);
        Assert.Empty(ModeCommands(page2));
        Assert.Single(ctl2.Activity.Items, e => e.Text.StartsWith("🏯 Phó bản đã hết lượt"));
    }

    [Fact]
    public void Teacher_event_runs_once_a_day_with_spend_cap_and_logs_result()
    {
        var (ctl, page, cfg) = Make();
        cfg.EventTeacherSpendCap = 300;
        var st = Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900");
        List<JsonElement> Requests() => page.Sent.Where(m => m.GetProperty("t").GetString() == "teacher").ToList();

        var t = Run(ctl, T0, 60, st);
        Assert.Empty(Requests());                            // chờ 90 giây sau khi vào game
        t = Run(ctl, t, 60, st);
        var req = Assert.Single(Requests());
        Assert.Equal(40, req.GetProperty("arg").GetProperty("keep").GetInt32());
        Assert.Equal(300, req.GetProperty("arg").GetProperty("spendCap").GetInt32());

        ctl.OnPageMessage("""{"t":"teacherResult","r":"ok","gifted":40,"crafted":10,"spent":100,"today":40,"cap":40,"points":238,"bouquets":140}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "💐 Tri Ân Nhà Giáo: tặng 40 Bó Hoa (hôm nay 40/40, Điểm Tri Ân 238) · ghép 10 Bó Hoa (−100 vạn) · còn 140 Bó Hoa");
        Run(ctl, t, 30 * 60, st);
        Assert.Single(Requests());                           // xong hôm nay → không chạy lại

        // Tắt trong cài đặt → không chạy
        var (ctl2, page2, cfg2) = Make();
        cfg2.EventTeacher = false;
        Run(ctl2, T0, 5 * 60, st);
        Assert.DoesNotContain(page2.Sent, m => m.GetProperty("t").GetString() == "teacher");
    }

    [Fact]
    public void Account_not_pulled_into_boss_wave_returns_to_tower_after_grace()
    {
        var (ctl, page, _) = Make();
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        // Vừa chết trong tháp ra Luyện Công thì đợt boss bắt đầu, game không đưa đi đánh
        string Farm() => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm", zone = "Hoa Sơn",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60",
        });
        var t = Run(ctl, T0, 15, Farm());
        ctl.SetAutoMode(true, t);
        boss = true;
        t = Run(ctl, t, 2 * 60, Farm());
        Assert.Empty(ModeCommands(page));                 // 3 phút đầu đợt: chờ game kịp đưa đi
        var back = RunUntilMode(ctl, page, t, Farm(), "tower", 3 * 60);
        Assert.True(back - T0 < TimeSpan.FromMinutes(6));  // không chờ tới hết đợt (~33 phút)
    }

    [Fact]
    public void Boss_hk_schedule_uses_vietnam_time()
    {
        var vn = TimeSpan.FromHours(7);
        DateTime Utc(int h, int m) => new DateTime(2026, 1, 1, h, m, 0, DateTimeKind.Utc) - vn; // giờ Việt Nam → UTC
        Assert.True(Core.GameData.BossHkSchedule.IsActive(Utc(12, 10)));
        Assert.True(Core.GameData.BossHkSchedule.IsActive(Utc(22, 31)));  // đệm sau đợt
        Assert.True(Core.GameData.BossHkSchedule.IsActive(Utc(19, 29)));  // đệm trước đợt
        Assert.False(Core.GameData.BossHkSchedule.IsActive(Utc(13, 0)));
        Assert.False(Core.GameData.BossHkSchedule.IsActive(Utc(15, 0)));

        var next = Core.GameData.BossHkSchedule.Next(Utc(13, 0));
        Assert.Equal((false, Utc(15, 30), new TimeSpan(15, 30, 0)), next);
        Assert.Equal((true, Utc(22, 30), new TimeSpan(22, 0, 0)), Core.GameData.BossHkSchedule.Next(Utc(22, 10)));
        Assert.Equal((false, Utc(12, 0).AddDays(1), new TimeSpan(12, 0, 0)), Core.GameData.BossHkSchedule.Next(Utc(23, 0)));
    }

    [Fact]
    public void Gifts_and_page_problems_are_logged()
    {
        var (ctl, _, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        ctl.OnPageMessage("""{"t":"status","screen":"game","st":"10/60"}""", T0);
        ctl.OnPageMessage("""{"t":"gifts","claimed":"điểm danh hôm nay|mốc cấp 150"}""", T0);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "🎁 Nhận quà: mốc cấp 150");
        ctl.OnPageMessage("""{"t":"problem","key":"chest","msg":"Không đóng được cửa sổ Rương"}""", T0);
        Assert.Contains("chest", problems);
    }

    [Fact]
    public void Auto_loop_uses_assassin_tokens_in_farm_before_tower()
    {
        var (ctl, page, _) = Make();
        // Dã Tẩu xong, hết Phó bản, còn 2 Lệnh Bài Sát Thủ; đang ở tháp
        string St(string mode, string st, string trk = "") => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode,
            zone = mode == "tower" ? "Thông Thiên Tháp · tầng 900" : "Tiến Cúc động",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st, trk,
        });
        List<string?> Summons() => page.Sent.Where(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "assassin")
            .Select(m => m.GetProperty("name").GetString()).ToList();

        var t = Run(ctl, T0, 15, St("tower", "2/60"));
        ctl.SetAutoMode(true, t);
        t = RunUntilMode(ctl, page, t, St("tower", "2/60"), "farm"); // sang Luyện Công để gọi Sát Thủ
        t = Run(ctl, t.AddSeconds(3), 9, St("farm", "2/60"));
        var summon = Assert.Single(Summons());
        ctl.OnPageMessage("""{"t":"cmdResult","name":"assassin","arg":"","result":"ok"}""", t);

        // Đang đánh → không gọi thêm, không đổi chế độ
        t = Run(ctl, t, 120, St("farm", "1/60", "Sát Thủ · Truy sát mục tiêu"));
        Assert.Single(Summons());
        Assert.Equal("current", ctl.AutoChain(t).Single(x => x.Mode == "assassin").State);

        // Hạ xong con 1 → gọi con 2
        t = Run(ctl, t, 30, St("farm", "1/61"));
        Assert.Equal(2, Summons().Count);
        ctl.OnPageMessage("""{"t":"cmdResult","name":"assassin","arg":"","result":"ok"}""", t);
        t = Run(ctl, t, 60, St("farm", "0/61", "Sát Thủ · Truy sát mục tiêu"));

        // Hết lệnh bài → Leo Tháp
        RunUntilMode(ctl, page, t, St("farm", "0/62"), "tower");
        Assert.Equal(2, Summons().Count);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🗡 Gọi Sát Thủ"));
    }

    [Fact]
    public void Auto_loop_runs_boat_then_week_boss_in_farm_before_tower()
    {
        var (ctl, page, _) = Make();
        string Ev(string mode, string boat, string wb) => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode,
            zone = mode == "tower" ? "Thông Thiên Tháp · tầng 900" : "Hoa Sơn",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", boat, wb,
        });
        int Cmds(string name) => page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == name);
        void Result(string name, string result, DateTime at) =>
            ctl.OnPageMessage(JsonSerializer.Serialize(new { t = "cmdResult", name, arg = "", result }), at);

        var t = Run(ctl, T0, 15, Ev("tower", "2/3", "1/3"));
        ctl.SetAutoMode(true, t);
        t = RunUntilMode(ctl, page, t, Ev("tower", "2/3", "1/3"), "farm"); // nút mờ khi ở tháp → Luyện Công trước
        t = Run(ctl, t.AddSeconds(3), 9, Ev("farm", "2/3", "1/3"));
        Assert.Equal(1, Cmds("boat"));
        Result("boat", "ok: ⛵ Lên thuyền", t);
        Assert.Equal("current", ctl.AutoChain(t).Single(x => x.Mode == "boat").State);

        // Trên thuyền: game đổi chế độ / bản đồ → không khoá, không bấm thêm, không đổi chế độ
        int modes = ModeCommands(page).Count;
        t = Run(ctl, t, 4 * 60, Ev("quest", "2/3", "1/3"));
        Assert.Equal(1, Cmds("boat"));
        Assert.Equal(modes, ModeCommands(page).Count);

        // Chuyến thất bại (số lượt không giảm) → về Luyện Công, bấm lại
        t = Run(ctl, t, 4 * 60, Ev("farm", "2/3", "1/3"));
        Assert.Equal(2, Cmds("boat"));
        Assert.Contains(ctl.Activity.Items, e => e.Text.Contains("không thành"));
        Result("boat", "ok: ⛵ Lên thuyền", t);
        t = Run(ctl, t, 8 * 60, Ev("farm", "0/3", "1/3")); // xong (giả sử còn 0) → sang Boss tuần
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("⛵ Đi thuyền: đã tính 1 lượt"));
        Assert.Equal("done", ctl.AutoChain(t).Single(x => x.Mode == "boat").State);
        Assert.Equal(1, Cmds("weekBoss"));
        Result("weekBoss", "ok: Khiêu chiến", t);

        // Hết lượt Boss tuần, nút nhận rương sáng → nhận, rồi Leo Tháp
        t = Run(ctl, t, 5 * 60, Ev("farm", "0/3", "0/3 claim"));
        Assert.Equal(1, Cmds("weekClaim"));
        Result("weekClaim", "ok: Nhận rương tuần", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "🎁 Nhận rương Boss tuần");
        Run(ctl, t, 60, Ev("farm", "0/3", "0/3"));
        Assert.Equal("tower", ModeCommands(page).Last());
    }

    [Fact]
    public void Event_page_error_after_click_still_waits_and_counts_by_turns()
    {
        var (ctl, page, _) = Make();
        string Ev(string boat, string wb) => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm", zone = "Hoa Sơn",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", boat, wb,
        });
        int Cmds(string name) => page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == name);
        var t = Run(ctl, T0, 15, Ev("3/3", "3/3"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 60, Ev("3/3", "3/3"));
        Assert.Equal(1, Cmds("boat"));
        // Trang báo lỗi sau khi bấm (như bản trước), nhưng game đã trừ lượt
        ctl.OnPageMessage("""{"t":"cmdResult","name":"boat","arg":"","result":"error: Cannot read properties of null"}""", t);
        t = Run(ctl, t, 3 * 60, Ev("2/3", "3/3"));
        Assert.Equal(1, Cmds("boat"));                    // không bấm dồn khi chuyến chưa hết
        Assert.DoesNotContain(ctl.Activity.Items, e => e.Text.Contains("không thành"));
        ctl.OnPageMessage("""{"t":"eventReward","kind":"boat","text":"Qua sông: 9,5 vạn lượng"}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "🎁 Qua sông: 9,5 vạn lượng");
        Run(ctl, t, 3 * 60, Ev("2/3", "3/3"));
        Assert.Equal(2, Cmds("boat"));                    // hết chuyến → chuyến sau
        // Nút nhận rương tuần sáng từ lượt đầu: chưa hết lượt thì không nhận
        Assert.Equal(0, Cmds("weekClaim"));
    }

    [Fact]
    public void Maintenance_keeps_app_still_and_quest_button_does_not_pull_off_boat()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        string Ev(string mode, string boat, int bt = 0) => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode, zone = "Hoa Sơn",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", boat, wb = "0/3", bt,
        });
        int Cmds() => page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd");

        // Đang đi thuyền: bấm Nhiệm Vụ chỉ bật vòng, không đổi chế độ
        var t = Run(ctl, T0, 15, Ev("farm", "3/3"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 60, Ev("farm", "3/3"));
        Assert.Contains("boat", page.Commands);
        int sent = Cmds();
        ctl.RequestMode("quest", t);
        Assert.Equal(sent, Cmds());
        Assert.DoesNotContain(page.Sent, m => m.GetProperty("t").GetString() == "buyLdt");

        // Bảo trì: báo một lần, không làm gì trong game
        t = Run(ctl, t, 30, Ev("farm", "2/3", bt: 1));
        Assert.True(ctl.Maintenance);
        Assert.Contains("maintenance", problems);
        sent = Cmds();
        t = Run(ctl, t, 20 * 60, Ev("farm", "2/3", bt: 1));
        Assert.Equal(sent, Cmds());
        Assert.DoesNotContain("idle", problems);

        // Hết bảo trì → chạy tiếp (chuyến thuyền sau)
        Run(ctl, t, 2 * 60, Ev("farm", "2/3"));
        Assert.False(ctl.Maintenance);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🛠 Hết bảo trì"));
        Assert.Equal(2, page.Commands.Count(c => c == "boat"));
    }

    [Fact]
    public void Account_used_elsewhere_clicks_play_here_after_delay_and_backs_off_on_repeat()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        string St(int away) => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm", zone = "Hoa Sơn", away,
        });
        int PlayHere() => page.Commands.Count(c => c == "playHere");

        var t = Run(ctl, T0, 15, St(0));
        t = Run(ctl, t, 90, St(1));                 // bị đẩy ra: chưa bấm ngay (chờ 2 phút)
        Assert.True(ctl.Away);
        Assert.Equal(0, PlayHere());
        Assert.Equal(Health.Warn, ctl.HealthAt(t).Level);
        t = Run(ctl, t, 60, St(1));
        Assert.Equal(1, PlayHere());
        t = Run(ctl, t, 60, St(0));                 // vào lại được

        // Vừa vào lại đã bị đẩy ra lần nữa → có người chơi thật ở máy khác: ngừng hẳn, báo
        t = Run(ctl, t, 60 * 60, St(1));
        Assert.Contains("elsewhere", problems);
        Assert.Equal(1, PlayHere());
        // Bạn bấm Tải lại acc này → app vào lại ngay
        ctl.ManualReload();
        Run(ctl, t, 6, St(1));
        Assert.Equal(2, PlayHere());
    }

    [Fact]
    public void Stuck_account_is_reloaded_at_most_twice_an_hour_then_reported()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var still = Status("game", mode: "farm", zone: "Hoa Sơn");
        var t = Run(ctl, T0, 15, still);
        t = Run(ctl, t, 9 * 60, still);
        Assert.Equal(0, page.Reloads);
        t = Run(ctl, t, 2 * 60, still);             // quá 10 phút không tiến triển
        Assert.Equal(1, page.Reloads);
        ctl.Watched = true;                          // đang xem Live → không tải lại
        t = Run(ctl, t, 15 * 60, still);
        Assert.Equal(1, page.Reloads);
        ctl.Watched = false;
        t = Run(ctl, t, 25 * 60, still);
        Assert.Equal(2, page.Reloads);               // tối đa 2 lần / giờ
        Assert.Contains("stuck", problems);
    }

    [Fact]
    public void Boss_reward_lines_are_logged_and_live_view_releases_system_chat()
    {
        var (ctl, page, _) = Make();
        ctl.OnPageMessage("""{"t":"sysNote","text":"Phần thưởng Boss Hoàng Kim: 1 Tử Thủy Tinh","boss":true}""", T0);
        ctl.OnPageMessage("""{"t":"sysNote","text":"Nhặt được Lục Thủy Tinh","boss":false}""", T0);
        // Boss Hoàng Kim do Lệnh Bài Sát Thủ gọi ra: ngoài giờ Boss HK theo lịch vẫn ghi (mẫu chép từ game)
        ctl.OnPageMessage("""{"t":"sysNote","text":"🎁 Hạ Boss Hoàng Kim: 36.00M kinh nghiệm, 1 Lam Thủy Tinh","boss":false}""", T0);
        ctl.OnPageMessage("""{"t":"sysNote","text":"👑 Boss Hoàng Kim rơi: Kim Phong Đồng Tước Xuân Thâm","boss":false}""", T0);
        ctl.OnPageMessage("""{"t":"sysNote","text":"🎁 Hạ Sát Thủ cấp 90: 15,4 vạn lượng, 18.00M kinh nghiệm, 1 Lam Thủy Tinh","boss":false}""", T0);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "🎁 Hạ Boss Hoàng Kim: 36.00M kinh nghiệm, 1 Lam Thủy Tinh");
        Assert.Contains(ctl.Activity.Items, e => e.Text == "👑 Boss Hoàng Kim rơi: Kim Phong Đồng Tước Xuân Thâm");
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🎁 Hạ Sát Thủ cấp 90"));
        Assert.Contains(ctl.Activity.Items, e => e.Text == "👑 Phần thưởng Boss Hoàng Kim: 1 Tử Thủy Tinh");
        Assert.DoesNotContain(ctl.Activity.Items, e => e.Text.Contains("Lục Thủy Tinh"));

        ctl.Watched = true;   // đang xem Live → trả quyền chọn kênh chat cho bạn
        ctl.Watched = false;
        var keep = page.Sent.Where(m => m.GetProperty("t").GetString() == "keepSys").Select(m => m.GetProperty("on").GetBoolean()).ToList();
        Assert.Equal(new[] { false, true }, keep);
    }

    [Fact]
    public void Dungeon_step_with_frozen_count_is_skipped_after_30_minutes()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        // Game đã đi hết phó bản nhưng app đọc sai "còn 1 lượt" mãi
        var dg = Status("game", mode: "dg", zone: "Tiến Cúc động", dt: "200/200", pb: "1/10", ldtHave: "0", ldtUsed: "160/160", ldtBuy: "100/100");
        var t = Run(ctl, T0, 15, dg);
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 25 * 60, dg);
        Assert.Empty(ModeCommands(page));          // còn trong thời gian chờ
        t = RunUntilMode(ctl, page, t, dg, "tower", 10 * 60);
        Assert.Contains("stuck-dg", problems);
        t = Run(ctl, t, 6, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900", dt: "200/200", pb: "1/10",
            ldtHave: "0", ldtUsed: "160/160", ldtBuy: "100/100"));
        Assert.Equal("rest", ctl.AutoChain(t).Single(x => x.Mode == "dg").State); // bỏ qua tới hết ngày
    }

    [Fact]
    public void Unreadable_numbers_are_reported_once_a_day_and_appear_in_daily_summary()
    {
        var (ctl, _, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        ctl.OnPageMessage("""{"t":"unreadable","what":"knb","sample":"17.2x 🔒1113"}""", T0);
        ctl.OnPageMessage("""{"t":"unreadable","what":"knb","sample":"17.2x 🔒1113"}""", T0.AddHours(1));
        Assert.Equal(new[] { "unreadable-knb" }, problems);
        Assert.Contains(ctl.Activity.Items, e => e.Text.Contains("Không đọc được số KNB"));

        Run(ctl, T0.AddHours(1), 15, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900", dt: "200/200", pb: "0/12",
            ldtHave: "0", ldtUsed: "160/160", ldtBuy: "0/100"));
        var summary = ctl.DailySummary(DateOnly.FromDateTime(T0.ToUniversalTime().AddHours(7)));
        Assert.Contains("Dã Tẩu 200/200 ✅", summary);
        Assert.Contains("Lệnh bài mua 0/100 ❌", summary);
        Assert.Contains("Phó bản còn 0 ✅", summary);
        Assert.Contains("1 lỗi: Không đọc được số KNB", summary);
    }

    [Fact]
    public void Boat_waits_when_boss_hk_is_about_to_start()
    {
        var (ctl, page, _) = Make();
        ctl.BossHkSchedule = at => at >= T0.AddMinutes(3); // đợt Boss HK sắp bắt đầu
        string Ev() => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm", zone = "Hoa Sơn",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", boat = "3/3",
        });
        var t = Run(ctl, T0.AddMinutes(-2), 15, Ev());
        ctl.SetAutoMode(true, t);
        Run(ctl, t, 60, Ev());
        Assert.DoesNotContain("boat", page.Commands);
    }

    [Fact]
    public void Pending_assassin_gets_go_nudges_then_loop_moves_on()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        // Đăng nhập lại: còn nhiệm vụ Sát Thủ nhưng nhân vật đứng luyện công, hết lệnh bài
        string St() => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm", zone = "Hoa Sơn",
            dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", trk = "Sát Thủ · Truy sát mục tiêu",
        });
        int Gos() => page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "assassinGo");
        var t = Run(ctl, T0, 15, St());
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 5 * 60, St());
        Assert.InRange(Gos(), 2, 3);              // 30 giây đầu rồi 3 phút một lần
        Assert.Empty(ModeCommands(page));          // đang chờ Sát Thủ: chưa đổi chế độ
        // Quá 15 phút vẫn chưa hạ → báo và cho vòng chạy tiếp (sang tháp)
        RunUntilMode(ctl, page, t, St(), "tower", 15 * 60);
        Assert.Contains("assassin-stuck", problems);
    }

    [Fact]
    public void Tower_rewards_log_only_hundreds_and_golden_items()
    {
        var (ctl, _, _) = Make();
        void Reward(int floor, string reward) => ctl.OnPageMessage(
            JsonSerializer.Serialize(new { t = "towerReward", floor, reward }), T0);
        Reward(7190, "3 Phúc Duyên (chờ đóng bình), Lục Thủy Tinh");
        Reward(7200, "5 Phúc Duyên, Tử Thủy Tinh");
        Reward(7213, "Ngự Long Lượng Ngân Bảo Đao (Hoàng Kim)");
        var texts = ctl.Activity.Items.Select(e => e.Text).ToList();
        Assert.DoesNotContain(texts, t => t.Contains("7190"));
        Assert.Contains("🏆 Mốc tháp 7200: 5 Phúc Duyên, Tử Thủy Tinh", texts);
        Assert.Contains(texts, t => t.StartsWith("👑 Tháp tầng 7213"));
        Reward(0, "Nhặt được Hiệp Cốt Thiết Huyết Sam (Hoàng Kim)");
        Assert.Contains(ctl.Activity.Items, e => e.Text == "👑 Nhặt được Hiệp Cốt Thiết Huyết Sam (Hoàng Kim)");
    }

    [Fact]
    public void Health_reflects_idle_and_out_of_game()
    {
        var (ctl, _, _) = Make(withPassword: false);
        var t = Run(ctl, T0, 15, Status("game"));
        Assert.Equal(Health.Good, ctl.HealthAt(t).Level);
        t = Run(ctl, t, 6 * 60, Status("game"));        // tiền/cấp không đổi 6 phút
        Assert.Equal(Health.Warn, ctl.HealthAt(t).Level);
        t = Run(ctl, t, 60, Status("charselect"));
        Assert.Equal(Health.Warn, ctl.HealthAt(t).Level); // vừa ra, đang vào lại
        t = Run(ctl, t, 4 * 60, Status("charselect"));
        Assert.Equal(Health.Bad, ctl.HealthAt(t).Level);
        // Ở màn đăng nhập mà chưa lưu mật khẩu: đỏ ngay, nói rõ lý do
        t = Run(ctl, t, 6, Status("login"));
        Assert.Equal((Health.Bad, "Bị đăng xuất, chưa lưu mật khẩu game (🔑)"), ctl.HealthAt(t));
    }
}

public class AesKeyFileProtectorTests
{
    [Fact]
    public void Roundtrip_and_reuses_key_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vlmulti-test-" + Guid.NewGuid());
        try
        {
            var key = Path.Combine(dir, "secret.key");
            var store = new CredentialStore(Path.Combine(dir, "credentials.json"), new AesKeyFileProtector(key));
            store.Set("acc1", "user1", "mật khẩu 123");

            // Mở lại bằng khoá đã lưu → đọc được
            var again = new CredentialStore(Path.Combine(dir, "credentials.json"), new AesKeyFileProtector(key));
            Assert.Equal(("user1", "mật khẩu 123"), again.Get("acc1"));

            // Khoá khác → không đọc được (coi như chưa lưu), không văng lỗi
            var other = new CredentialStore(Path.Combine(dir, "credentials.json"), new AesKeyFileProtector(Path.Combine(dir, "other.key")));
            Assert.Null(other.Get("acc1"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
