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
        cfg.KiteDirection = KiteDirections.Alternate; // kiểu cũ: app ra lệnh chạy theo chu kỳ
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
    public void Line_kite_sends_config_to_the_page_with_per_account_overrides()
    {
        var (ctl, page, cfg) = Make();
        cfg.KiteDirection = KiteDirections.Line;
        cfg.KiteDistance = 250;
        cfg.KiteIntervalSeconds = 3;
        cfg.KiteFleeHp = 40;
        ctl.Account.KiteDistance = 350;   // riêng acc này
        ctl.Account.KiteFleeHp = 30;
        ctl.Account.KiteCrowd = 4;
        JsonElement? LastCfg() => page.Sent.LastOrDefault(m => m.GetProperty("t").GetString() == "kiteCfg") is { ValueKind: JsonValueKind.Object } m ? m.GetProperty("arg") : null;
        var t = Run(ctl, T0, 15, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 10"));
        Assert.Equal(JsonValueKind.Null, LastCfg()!.Value.ValueKind); // chưa bật
        ctl.SetKite(true, t);
        t = Run(ctl, t, 6, Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 10"));
        var c = LastCfg()!.Value;
        Assert.True(c.GetProperty("on").GetBoolean());
        Assert.Equal(350, c.GetProperty("step").GetInt32());
        Assert.Equal(500, c.GetProperty("pauseMs").GetInt32());  // chung: dừng 0,5 giây giữa 2 bước
        Assert.Equal(4, c.GetProperty("crowd").GetInt32());      // riêng: chạy khi hơn 4 con ở gần
        Assert.Equal(30, c.GetProperty("fleeHp").GetInt32());
        Assert.DoesNotContain("kite", page.Commands); // trang tự chạy, app không ra lệnh từng bước
        Run(ctl, t, 6, Status("game", mode: "farm"));
        Assert.Equal(JsonValueKind.Null, LastCfg()!.Value.ValueKind); // ra khỏi tháp → tắt
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
        Assert.Contains(ctl.Activity.Items, e => e.Text.Contains("Chết ở tầng 501") && e.Level == StatusLevel.Warn);
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
        var (ctl, _, cfg) = Make();
        cfg.AutoSellJunk = false; // có tự bán đồ thừa thì chỉ báo khi không còn gì để bán
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

    // Vào tháp, sống `lived` giây, chết ở tầng `floor` (game đẩy ra Luyện Công), chờ `after` giây
    private static DateTime DieInTower(AccountController ctl, DateTime t, int lived, int after = 30, int floor = 700)
    {
        t = Run(ctl, t, lived, Tower(floor));
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
    public void Only_three_deaths_on_the_same_floor_rest_the_tower()
    {
        var (ctl, _, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 15, Status("game", mode: "farm"));
        ctl.RequestMode("tower", t);
        t = t.AddSeconds(70);
        // Chết nhanh nhưng mỗi lần một tầng (vẫn đang lên) → không tính
        t = DieInTower(ctl, t, 60, floor: 700);
        t = DieInTower(ctl, t, 60, floor: 701);
        t = DieInTower(ctl, t, 60, floor: 702);
        t = DieInTower(ctl, t, 60, floor: 703);
        Assert.DoesNotContain("tower-fast-death", problems);
        // Chết 3 lần ở cùng tầng 703 (kể cả sống lâu) → nghỉ tháp
        t = DieInTower(ctl, t, 5 * 60, floor: 703);
        DieInTower(ctl, t, 60, floor: 703);
        Assert.Contains("tower-fast-death", problems);
        Assert.Equal(6, ctl.Tower.Falls);
    }

    [Fact]
    public void Rests_stay_at_the_setting_and_a_wall_floor_is_reported_once()
    {
        var (ctl, _, cfg) = Make();
        cfg.TowerRestMinutes = 10;
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var t = Run(ctl, T0, 15, Status("game", mode: "farm"));
        ctl.RequestMode("tower", t);
        t = t.AddSeconds(70);
        TimeSpan Rest(DateTime at) => TimeSpan.Parse(ctl.StatusText.Replace("Nghỉ tháp tới ", "")) - at.TimeOfDay;
        for (int i = 0; i < 3; i++) t = DieInTower(ctl, t, 20, floor: 900);
        Assert.InRange(Rest(t).TotalMinutes, 8, 11);
        for (int i = 0; i < 3; i++) t = DieInTower(ctl, t, 20, floor: 900);
        Assert.InRange(Rest(t).TotalMinutes, 8, 11);
        Assert.DoesNotContain("tower-wall", problems);
        for (int i = 0; i < 3; i++) t = DieInTower(ctl, t, 20, floor: 900);
        Assert.InRange(Rest(t).TotalMinutes, 8, 11);
        Assert.Contains("tower-wall", problems);
        // Qua được tầng đó, kẹt tầng khác → lại từ 10 phút
        for (int i = 0; i < 3; i++) t = DieInTower(ctl, t, 20, floor: 905);
        Assert.InRange(Rest(t).TotalMinutes, 8, 11);
    }

    [Fact]
    public void Exp_rising_in_the_field_is_not_idle()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        string St(double xp) => JsonSerializer.Serialize(new { t = "status", screen = "game", lv = "180", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm",
            zone = "Hoa Sơn", dt = "200/200", pb = "0/4", xp = $"{xp:0.0}%" });
        var t = T0;
        for (int i = 0; i < 40; i++) t = Run(ctl, t, 60, St(10 + i * 0.1)); // 40 phút, tiền không đổi, % kinh nghiệm tăng
        Assert.DoesNotContain("idle", problems);
        Assert.DoesNotContain(page.Sent, m => m.GetRawText().Contains("reload"));
    }

    [Fact]
    public void Page_busy_for_a_long_time_closes_windows_then_reloads()
    {
        var (ctl, page, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "farm"));
        for (int i = 0; i < 11; i++) { ctl.OnPageMessage("""{"t":"lockInnateResult","r":"busy"}""", t); t = t.AddMinutes(1); }
        Assert.Contains(page.Sent, m => m.GetProperty("t").GetString() == "unstuck");
        int reloads = page.Reloads;
        for (int i = 0; i < 5; i++) { ctl.OnPageMessage("""{"t":"lockInnateResult","r":"busy"}""", t); t = t.AddMinutes(1); }
        Assert.Equal(reloads + 1, page.Reloads);
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
    public void Auto_button_starts_auto_loop_and_other_modes_stop_it()
    {
        var (ctl, page, _) = Make();
        var t = Run(ctl, T0, 15, Status("game", mode: "farm", dt: "40/40", ldtHave: "60", ldtUsed: "100/160", ldtBuy: "40/100"));
        ctl.RequestMode("auto", t);
        Assert.True(ctl.Account.AutoMode);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🔁 Bắt đầu vòng tự động"));
        Assert.Single(BuyRequests(page)); // vòng bắt đầu bằng nhận quà + mua lệnh bài như trước
        ctl.RequestMode("farm", t.AddSeconds(5));
        Assert.False(ctl.Account.AutoMode);

        // Nhiệm Vụ = chỉ Nhiệm Vụ: vẫn nhận quà / mua lệnh bài rồi vào Nhiệm Vụ, nhưng tắt vòng tự động
        ctl.RequestMode("auto", t.AddSeconds(10));
        ctl.RequestMode("quest", t.AddSeconds(15));
        Assert.False(ctl.Account.AutoMode);
        Assert.Equal(3, BuyRequests(page).Count);
        ctl.OnPageMessage("""{"t":"ldtResult","r":"ok","bought":0,"spent":0,"left":0}""", t.AddSeconds(20));
        Assert.Equal("quest", ModeCommands(page).Last());
        Assert.Contains(ctl.Activity.Items, e => e.Text == "🔁 Dừng vòng tự động (bạn chọn Nhiệm Vụ)");
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
        ctl.RequestMode("auto", t);
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

        // Đợt boss: đang ở tháp → một lệnh Luyện Công (ở tháp game giật qua lại, không hạ được boss); sau đó game tự
        // đưa đi đánh → không tính là chết, không kéo về, không kéo quái, không báo
        boss = true;
        t = Run(ctl, t, 6, Tw("tower"));
        Assert.Equal(new[] { "farm" }, ModeCommands(page).Skip(ModeCommands(page).Count - 1));
        ctl.OnPageMessage("""{"t":"sysNote","text":"👑 Boss Hoàng Kim Hà Linh Phiêu xuất hiện — tạm rời Tháp tầng 900 đi săn Boss.","boss":true}""", t);
        t = Run(ctl, t, 10 * 60, Tw("farm"));
        Assert.Equal(sent + 1, page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd"));
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
    public void Teacher_event_exchange_option_is_passed_and_gifts_are_logged()
    {
        var (ctl, page, cfg) = Make();
        var st = Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900");
        var t = Run(ctl, T0, 120, st);
        var req = Assert.Single(page.Sent, m => m.GetProperty("t").GetString() == "teacher");
        Assert.True(req.GetProperty("arg").GetProperty("exchange").GetBoolean());
        ctl.OnPageMessage("""{"t":"teacherResult","r":"ok","gifted":0,"crafted":0,"today":40,"cap":40,"points":277,"bouquets":81,"exchanged":"Đại Hồng Bao|Tiên Thảo Lộ Đặc Biệt|Tiên Thảo Lộ Đặc Biệt|Hồng Bao"}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "🎁 Tri Ân Nhà Giáo: đổi nguyên liệu thừa lấy 1 Đại Hồng Bao, 2 Tiên Thảo Lộ Đặc Biệt, 1 Hồng Bao");

        var (ctl2, page2, cfg2) = Make();
        cfg2.EventTeacherExchange = false;
        Run(ctl2, T0, 120, st);
        Assert.False(Assert.Single(page2.Sent, m => m.GetProperty("t").GetString() == "teacher").GetProperty("arg").GetProperty("exchange").GetBoolean());
    }

    private static List<JsonElement> Sent(FakePage page, string type) =>
        page.Sent.Where(m => m.GetProperty("t").GetString() == type).ToList();

    [Fact]
    public void Knb_exchange_runs_once_a_day_and_logs()
    {
        var (ctl, page, cfg) = Make();
        cfg.EventTeacher = false; // việc sự kiện đang chờ trang trả lời thì việc đổi KNB đợi (không chen hai việc một lúc)
        var problems = new List<string>();
        ctl.Problem += (key, _) => problems.Add(key);
        var st = Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900");
        var t = Run(ctl, T0, 100, st);
        Assert.Empty(Sent(page, "knbBuy"));                   // chờ 2 phút sau khi vào game
        t = Run(ctl, t, 40, st);
        Assert.Single(Sent(page, "knbBuy"));

        ctl.OnPageMessage("""{"t":"knbResult","r":"busy"}""", t);
        t = Run(ctl, t, 6 * 60, st);
        Assert.Equal(2, Sent(page, "knbBuy").Count);          // đang bận → thử lại sau 5 phút
        ctl.OnPageMessage("""{"t":"knbResult","r":"ok","bought":1000,"spent":1000,"opened":10,"gained":4300,"done":1000,"left":0}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "💰 Đổi 1000 vạn → 1000 KNB (hôm nay 1000/1000)");
        Assert.Contains(ctl.Activity.Items, e => e.Text == "🧧 Thiếu vạn lượng: mở 10 Hồng Bao (+4300 vạn)");
        Run(ctl, t, 60 * 60, st);
        Assert.Equal(2, Sent(page, "knbBuy").Count);          // xong hôm nay
        Assert.DoesNotContain("knb-buy", problems);

        var (ctl2, page2, cfg2) = Make();
        cfg2.DailyKnbBuy = false;
        Run(ctl2, T0, 10 * 60, st);
        Assert.Empty(Sent(page2, "knbBuy"));
    }

    [Fact]
    public void Guild_knb_is_donated_only_when_switched_on_and_the_guild_log_is_polled()
    {
        // Tắt (mặc định): không bao giờ gửi lệnh góp KNB
        var (ctl, page, cfg) = Make();
        cfg.EventTeacher = false;
        cfg.DailyKnbBuy = false;
        var st = Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900");
        Run(ctl, T0, 10 * 60, st);
        Assert.Empty(Sent(page, "guildKnb"));

        // Bật: mỗi ngày một lần, tới mức chọn, chừa KnbKeep
        var (ctl2, page2, cfg2) = Make();
        cfg2.EventTeacher = false;
        cfg2.DailyKnbBuy = false;
        cfg2.GuildKnbOn = true;
        cfg2.GuildKnbAmount = 100;
        cfg2.KnbKeep = 1000;
        var t = Run(ctl2, T0, 170, st);
        var arg = Assert.Single(Sent(page2, "guildKnb")).GetProperty("arg");
        Assert.Equal(100, arg.GetProperty("amount").GetInt32());
        Assert.Equal(1000, arg.GetProperty("keep").GetInt32());
        // Chưa đủ KNB sau khi chừa → ghi lý do, 60 phút sau thử lại (không thử dồn dập)
        ctl2.OnPageMessage("""{"t":"guildKnbResult","r":"ok","given":0,"done":0,"have":1000,"reason":"low"}""", t);
        Assert.Contains(ctl2.Activity.Items, e => e.Text.StartsWith("💎 Chưa góp KNB vào bang: còn 1,000 KNB") || e.Text.StartsWith("💎 Chưa góp KNB vào bang: còn 1.000 KNB"));
        t = Run(ctl2, t, 50 * 60, st);
        Assert.Single(Sent(page2, "guildKnb"));
        t = Run(ctl2, t, 15 * 60, st);
        Assert.Equal(2, Sent(page2, "guildKnb").Count());
        ctl2.OnPageMessage("""{"t":"guildKnbResult","r":"ok","given":100,"done":100,"have":1050}""", t);
        Assert.Contains(ctl2.Activity.Items, e => e.Text.StartsWith("💎 Góp 100 KNB vào quỹ bang"));
        Assert.True(ctl2.GuildKnbDoneToday(t));
        t = Run(ctl2, t, 3 * 60 * 60, st);
        Assert.Equal(2, Sent(page2, "guildKnb").Count()); // xong hôm nay: thôi

        // Sổ KNB: chỉ đọc nhật ký bang khi app nghe kết quả, 3 phút một lần; không ở trong bang → nghỉ 60 phút
        var got = new List<JsonElement>();
        ctl2.GuildLogResult += m => got.Add(m);
        t = Run(ctl2, t, 7 * 60, st);
        int polls = Sent(page2, "guildLog").Count();
        Assert.InRange(polls, 1, 3);
        ctl2.OnPageMessage("""{"t":"guildLogResult","r":"ok","guild":{"id":1,"name":"X","cap":1},"log":[]}""", t);
        Assert.Single(got);
        ctl2.OnPageMessage("""{"t":"guildLogResult","r":"no-guild"}""", t);
        int before = Sent(page2, "guildLog").Count();
        Run(ctl2, t, 30 * 60, st);
        Assert.Equal(before, Sent(page2, "guildLog").Count());
    }

    [Fact]
    public void Guild_shop_runs_once_a_day_with_chosen_items_and_logs()
    {
        var (ctl, page, cfg) = Make();
        cfg.EventTeacher = false;
        cfg.DailyKnbBuy = false;
        cfg.GuildTtsp = false;
        cfg.GuildDonateCap = 800;
        var st = Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900");
        var t = Run(ctl, T0, 140, st);
        Assert.Empty(Sent(page, "guild"));                     // chờ 150 giây sau khi vào game
        t = Run(ctl, t, 20, st);
        var arg = Assert.Single(Sent(page, "guild")).GetProperty("arg");
        Assert.Equal(new[] { "ldt", "fdl1", "thbt" }, arg.GetProperty("items").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(800, arg.GetProperty("donateCap").GetInt32());

        ctl.OnPageMessage("""{"t":"guildResult","r":"ok","bought":"ldt:10,fdl1:10","spent":1200,"donated":500,"mails":1,"points":2053}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text ==
            "🏯 Cửa hàng bang: mua 10 Lệnh Bài Dã Tẩu, 10 Bình Phúc Duyên Lộ (−1200 điểm, còn 2053) · góp 500 vạn · nhận 1 thư");
        Run(ctl, t, 60 * 60, st);
        Assert.Single(Sent(page, "guild"));                    // xong hôm nay

        // Chưa vào bang: ghi một dòng, thôi hôm đó
        var (ctl2, page2, cfg2) = Make();
        cfg2.EventTeacher = false;
        cfg2.DailyKnbBuy = false;
        t = Run(ctl2, T0, 160, st);
        ctl2.OnPageMessage("""{"t":"guildResult","r":"no-guild","bought":""}""", t);
        Run(ctl2, t, 60 * 60, st);
        Assert.Single(Sent(page2, "guild"));
        Assert.Single(ctl2.Activity.Items, e => e.Text.StartsWith("🏯 Cửa hàng bang: acc chưa vào bang"));

        var (ctl3, page3, cfg3) = Make();
        cfg3.GuildShop = false;
        Run(ctl3, T0, 10 * 60, st);
        Assert.Empty(Sent(page3, "guild"));
    }

    [Fact]
    public void Buffs_are_kept_up_with_enabled_items_and_low_items_pause()
    {
        var (ctl, page, cfg) = Make();
        cfg.BuffTtl = false;
        var st = Status("game", mode: "tower", zone: "Thông Thiên Tháp · tầng 900");
        var t = Run(ctl, T0, 70, st);
        var req = Assert.Single(Sent(page, "buffs"));
        var arg = req.GetProperty("arg");
        Assert.Equal(new[] { "ttsp", "qht" }, arg.GetProperty("items").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(10, arg.GetProperty("keep").GetInt32());

        ctl.OnPageMessage("""{"t":"buffResult","r":"ok","used":"qht:66","low":"ttsp:3","failed":""}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text == "✨ Dùng 1 Quế Hoa Tửu (còn 66)");
        Assert.Contains(ctl.Activity.Items, e => e.Text == "✨ Tiên Thảo Lộ Đặc Biệt: còn 3, chừa 10 – không dùng thêm");
        t = Run(ctl, t, 3 * 60, st);
        var next = Sent(page, "buffs").Last().GetProperty("arg").GetProperty("items").EnumerateArray().Select(x => x.GetString());
        Assert.Equal(new[] { "qht" }, next);                 // món đã chạm mức chừa: thôi xét 60 phút

        // Đủ cả, không làm gì: không ghi nhật ký
        int notes = ctl.Activity.Items.Count;
        ctl.OnPageMessage("""{"t":"buffResult","r":"ok","used":"","low":""}""", t);
        Assert.Equal(notes, ctl.Activity.Items.Count);

        var (ctl2, page2, cfg2) = Make();
        cfg2.BuffTtl = cfg2.BuffTtlSpecial = cfg2.BuffQht = false;
        Run(ctl2, T0, 10 * 60, st);
        Assert.Empty(Sent(page2, "buffs"));
    }

    private static string St(string mode, string dt = "200/200") => JsonSerializer.Serialize(new
    {
        t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode,
        zone = mode == "tower" ? "Thông Thiên Tháp · tầng 900" : "Hoa Sơn",
        dt, pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60",
    });

    [Fact]
    public void Boss_wave_moves_tower_account_to_farm_once_and_never_back_to_tower()
    {
        var (ctl, page, _) = Make();
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        // 08/11 12:00: game kéo acc ra khỏi tháp để đi đánh boss – tool không được đưa ngược lại vào tháp.
        // 08/11 15:30: ở chế độ Leo Tháp game giật qua lại giữa bãi boss và tháp → đổi sang Luyện Công một lần
        var t = Run(ctl, T0, 15, St("tower"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 70, St("tower"));
        boss = true;
        t = Run(ctl, t, 30, St("tower"));
        t = Run(ctl, t, 25 * 60, St("farm"));
        Assert.Equal(new[] { "farm" }, ModeCommands(page));
        // Hết đợt → vòng chạy lại bình thường
        boss = false;
        RunUntilMode(ctl, page, t, St("farm"), "tower", 5 * 60);
    }

    [Fact]
    public void Auto_loop_moves_quest_account_to_farm_just_before_boss_wave()
    {
        var (ctl, page, _) = Make();
        var start = T0.AddMinutes(6);
        ctl.BossHkSchedule = now => now >= start && now < start.AddMinutes(34);
        // Đang làm Nhiệm Vụ → 2 phút trước đợt sang Luyện Công, trong đợt đứng yên
        var t = Run(ctl, T0, 15, St("quest", "20/200"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 3 * 60, St("quest", "20/200"));
        Assert.DoesNotContain("farm", ModeCommands(page));
        t = Run(ctl, t, 2 * 60 + 30, St("quest", "20/200"));
        Assert.Equal("farm", ModeCommands(page).Last());
        int n = ModeCommands(page).Count;
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("👑 Sắp tới Boss Hoàng Kim"));
        // Tới đợt boss ở Luyện Công: không đổi gì trong đợt
        t = Run(ctl, t, 10 * 60, St("farm", "20/200"));
        Assert.Equal(n, ModeCommands(page).Count);
    }

    [Fact]
    public void Boss_killed_flag_from_game_ends_the_wave_and_returns_to_tower()
    {
        var (ctl, page, _) = Make();
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        string Gb(string gbw) => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm",
            zone = "Phong Lăng độ", dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", gbw,
        });
        var t = Run(ctl, T0, 15, St("tower"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 70, St("tower"));
        boss = true;
        t = Run(ctl, t, 5, St("tower"));
        // Đang đánh boss của riêng acc (chưa hạ): đứng yên ở Luyện Công
        t = Run(ctl, t, 5 * 60, Gb("2026-11-08#1|0|40"));
        Assert.Equal(new[] { "farm" }, ModeCommands(page));
        // Game báo đã hạ (không có dòng kênh Hệ thống) → về tháp ngay, không chờ hết đợt, không cảnh báo khi hết đợt
        RunUntilMode(ctl, page, t, Gb("2026-11-08#1|1|0"), "tower", 3 * 60);
        Assert.True(boss);
    }

    private static int Count(FakePage page, string t) => page.Sent.Count(m => m.GetProperty("t").GetString() == t);

    [Fact]
    public void Boss_wave_uses_go_now_and_warns_when_boss_not_killed()
    {
        var (ctl, page, _) = Make();
        var problems = new List<(string Key, string Msg)>();
        ctl.Problem += (k, msg) => problems.Add((k, msg));
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        var t = Run(ctl, T0, 15, St("tower"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 70, St("tower"));
        boss = true;
        t = Run(ctl, t, 30, St("tower"));
        t = Run(ctl, t, 4 * 60, St("farm"));
        Assert.DoesNotContain("tower", ModeCommands(page));
        // Dự phòng: đọc thẻ boss, bấm "Đến ngay"
        Assert.True(Count(page, "bossHk") >= 1);
        Assert.True(page.Sent.Last(m => m.GetProperty("t").GetString() == "bossHk").GetProperty("arg").GetProperty("go").GetBoolean());
        ctl.OnPageMessage("""{"t":"bossHkResult","r":"ok","done":false,"alive":true,"went":true,"info":"Hà Linh Phiêu cấp 125 tại Trường Bạch sơn Bắc · còn 22 phút"}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text.Contains("Đến ngay"));
        t = Run(ctl, t, 30 * 60, St("farm"));
        Assert.True(Count(page, "bossHk") <= 3); // 2 lần "Đến ngay" + 1 lần đọc thẻ cuối đợt
        // Hết đợt, không thấy hạ boss → cảnh báo kèm phương án dự phòng
        boss = false;
        Run(ctl, t, 6, St("farm"));
        var alert = Assert.Single(problems, p => p.Key == "boss-hk");
        Assert.Contains("Không hạ được Boss Hoàng Kim", alert.Msg);
        Assert.Contains("Dự phòng", alert.Msg);
    }

    private static string Tw(string mode, int inTower) => JsonSerializer.Serialize(new
    {
        t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode, inTower,
        zone = inTower == 1 ? "Thông Thiên Tháp · tầng 900" : "Hoa Sơn",
        dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60",
    });

    [Fact]
    public void Tower_mode_but_not_in_the_tower_is_fixed_after_two_minutes()
    {
        // 08/11 22:07: chết trong tháp, game ra Luyện Công nhưng app vẫn thấy chế độ Leo Tháp
        var (ctl, page, _) = Make();
        ctl.BossHkSchedule = _ => false;
        var t = Run(ctl, T0, 15, Tw("tower", 1));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 70, Tw("tower", 1));
        int n = ModeCommands(page).Count;
        t = Run(ctl, t, 90, Tw("tower", 0));
        Assert.Equal(n, ModeCommands(page).Count); // chưa đủ 2 phút: chờ
        t = Run(ctl, t, 60, Tw("tower", 0));
        Assert.Equal("tower", ModeCommands(page).Last());
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🗼 Chế độ Leo Tháp mà nhân vật không ở trong tháp"));
        // Vào lại được → thôi
        int m = ModeCommands(page).Count;
        Run(ctl, t, 5 * 60, Tw("tower", 1));
        Assert.Equal(m, ModeCommands(page).Count);
    }

    [Fact]
    public void Boss_wave_presses_go_when_the_boss_does_not_appear_and_alerts_on_level_gate()
    {
        // 08/11 22:00 một nhân vật: "tới Phong Lăng độ" nhưng nhân vật cấp 118 không vào được bản đồ cấp 120
        var (ctl, page, _) = Make();
        var problems = new List<(string Key, string Msg)>();
        ctl.Problem += (k, msg) => problems.Add((k, msg));
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        var t = Run(ctl, T0, 15, St("farm"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 70, St("farm"));
        boss = true;
        t = Run(ctl, t, 6, St("farm"));
        ctl.OnPageMessage("""{"t":"sysNote","text":"🗺 Boss Hoàng Kim Huyền Nan Đại Sư: tới Phong Lăng độ","boss":true}""", t);
        t = Run(ctl, t, 45, St("farm"));
        Assert.Equal(0, Count(page, "bossHk")); // 1 phút đầu: chờ boss hiện
        t = Run(ctl, t, 30, St("farm"));
        Assert.Equal(1, Count(page, "bossHk")); // không thấy "ở ngay gần" → bấm "Đến ngay" ngay, không chờ 15 phút
        Assert.True(page.Sent.Last(m => m.GetProperty("t").GetString() == "bossHk").GetProperty("arg").GetProperty("go").GetBoolean());
        ctl.OnPageMessage("""{"t":"bossHkResult","r":"ok","done":false,"alive":true,"went":true,"info":"Huyền Nan Đại Sư cấp 135 tại Phong Lăng độ"}""", t);
        ctl.OnPageMessage("""{"t":"gameToast","text":"Cần đạt cấp 120 để vào Phong Lăng độ"}""", t);
        var alert = Assert.Single(problems, p => p.Key == "boss-level");
        Assert.Contains("Cần đạt cấp 120", alert.Msg);
        // Không bấm thêm vô ích
        Run(ctl, t, 10 * 60, St("farm"));
        Assert.True(Count(page, "bossHk") <= 2);
    }

    [Fact]
    public void Game_log_lines_are_kept_with_floor_and_rare_items_are_flagged()
    {
        var (ctl, _, _) = Make();
        var problems = new List<(string Key, string Msg)>();
        ctl.Problem += (k, msg) => problems.Add((k, msg));
        var written = new List<GameLog.Entry>();
        ctl.GameLogSink = (_, list) => written.AddRange(list);
        var t = Run(ctl, T0, 6, St("tower"));
        var at = new DateTimeOffset(T0).ToUnixTimeMilliseconds();
        ctl.OnPageMessage(JsonSerializer.Serialize(new
        {
            t = "gameLog",
            lines = new object[]
            {
                new { at, text = "Nhận được 1.234 điểm kinh nghiệm", place = "Tầng 900" },
                new { at, text = "🗼 Tháp: qua tầng 900 · nhận 1 Huyền Tinh cấp 7", place = "Tầng 900" },
                new { at, text = "Nhặt được Tỏa Tử Hoàng Kim giáp", place = "Tầng 901" },
                new { at, text = "🗺 Dã Tẩu: Săn 30 Thủy Tặc ở Phong Lăng độ: tới Phong Lăng độ", place = "Phong Lăng độ" },
                new { at, text = "🎁 Dã Tẩu #2491: 1 Rương Ngựa", place = "Phong Lăng độ" },
                new { at, text = "Nhặt được Nhẫn Vô Danh", place = "Phong Lăng độ" },
                new { at, text = "📢 Chúc mừng X nhận được Đồ phổ Y từ Boss Hoàng Kim!", place = "Tầng 901" },
            },
        }), t);
        Assert.Equal(5, written.Count); // bỏ dòng kinh nghiệm và thông báo về người chơi khác; giữ dòng đi làm Dã Tẩu (trạng thái)
        Assert.Contains(written, e => e.Kind == "quest" && e.Text.StartsWith("🗺 Dã Tẩu: Săn 30 Thủy Tặc"));
        Assert.Contains(written, e => e.Kind == "datau" && e.Text == "🎁 Dã Tẩu #2491 (Săn 30 Thủy Tặc): 1 Rương Ngựa");
        Assert.Equal(("milestone", "Tầng 900"), (written[0].Kind, written[0].Place)); // tầng chẵn 100 = mốc
        Assert.Equal("rare", written[1].Kind);
        Assert.DoesNotContain(written, e => e.Text.Contains("Chúc mừng X"));
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("💎 Nhặt được Tỏa Tử Hoàng Kim giáp (Tầng 901)"));
        var alert = Assert.Single(problems, p => p.Key == "rare-item");
        Assert.Contains("Nhẫn Vô Danh", alert.Msg);
        Assert.Contains("Phong Lăng độ", alert.Msg);
    }

    [Fact]
    public void Boss_kill_and_its_reward_are_logged_as_one_line()
    {
        var (ctl, _, _) = Make();
        var written = new List<GameLog.Entry>();
        ctl.GameLogSink = (_, list) => written.AddRange(list);
        var t = Run(ctl, T0, 6, St("farm"));
        var at = new DateTimeOffset(T0).ToUnixTimeMilliseconds();
        string Batch(params string[] texts) => JsonSerializer.Serialize(new { t = "gameLog", lines = texts.Select(x => new { at, text = x, place = "Trường Bạch sơn Nam" }) });
        ctl.OnPageMessage(Batch("🔥 Boss Hoàng Kim · Đường Bất Nhiễm nổi giận — sát thương ×2!", "👑 Hạ Boss Hoàng Kim Đường Bất Nhiễm!"), t);
        Assert.Empty(written); // chờ dòng phần thưởng
        ctl.OnPageMessage(Batch("🎁 6 Phúc Duyên, 1 Lam Thủy Tinh", "Hạ Boss Hoàng Kim · Đường Bất Nhiễm"), t.AddSeconds(1));
        var e = Assert.Single(written);
        Assert.Equal("boss", e.Kind);
        Assert.Equal("👑 Hạ Boss Hoàng Kim Đường Bất Nhiễm → 6 Phúc Duyên, 1 Lam Thủy Tinh", e.Text);
        // Hạ Sát Thủ không có dòng thưởng ngay sau: vẫn ghi (sau 5 giây)
        ctl.OnPageMessage(Batch("🗡 Hạ Sát Thủ · Bảo Triệt Sơn!"), t.AddSeconds(10));
        ctl.OnPageMessage(Batch("🗼 Dịch chuyển vào Thông Thiên Tháp, tầng 900."), t.AddSeconds(20));
        Assert.Contains(written, x => x.Kind == "assassin" && x.Text.StartsWith("🗡 Hạ Sát Thủ · Bảo Triệt Sơn"));
    }

    [Fact]
    public void Jewelry_is_swapped_by_activity_only_when_switched_on()
    {
        var (ctl, page, cfg) = Make();
        ctl.BossHkSchedule = _ => false;
        cfg.EventTeacher = false;
        cfg.DailyKnbBuy = false;
        List<string> Modes() => page.Sent.Where(m => m.GetProperty("t").GetString() == "jewel").Select(m => m.GetProperty("arg").GetProperty("mode").GetString()!).ToList();
        var t = Run(ctl, T0, 5 * 60, Tower(900));
        Assert.Empty(Modes()); // mặc định tắt

        cfg.JewelSwapOn = true;
        t = Run(ctl, t, 10, Tower(900));
        Assert.Equal(new[] { "power" }, Modes()); // tháp → sức mạnh ngay
        ctl.OnPageMessage("""{"t":"jewelResult","r":"ok","mode":"power","changed":"Băng Hàn Huyền Thiên Băng Hỏa Bội","exp":150}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("💍 Đổi trang sức Sức mạnh"));
        // Chết trong tháp, Luyện Công 30 giây rồi vào lại → không đổi
        t = Run(ctl, t, 30, Status("game", mode: "farm"));
        t = Run(ctl, t, 60, Tower(900));
        Assert.Single(Modes());
        // Ở Luyện Công 90 giây (chết rồi chờ vào lại) → chưa đổi; đủ 3 phút → Hồng Ảnh
        t = Run(ctl, t, 90, Status("game", mode: "farm"));
        Assert.Single(Modes());
        t = Run(ctl, t, 100, Status("game", mode: "farm"));
        Assert.Equal(new[] { "power", "exp" }, Modes());
        ctl.OnPageMessage("""{"t":"jewelResult","r":"ok","mode":"exp","changed":"Hồng ảnh Mục Túc","exp":182}""", t);
        Assert.Equal("exp", ctl.JewelMode);
        // Tắt riêng acc này → không đổi nữa
        ctl.Account.JewelSwap = false;
        t = Run(ctl, t, 60, Tower(901));
        Assert.Equal(2, Modes().Count);
        // Nhưng ô trang sức trống (trong túi có món đeo được) → vẫn đeo vào, không đổi món khác
        var empty = JsonSerializer.Serialize(new { t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "tower", modeText = "tower",
            zone = "Thông Thiên Tháp · tầng 901", dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", jewelEmpty = 1 });
        Run(ctl, t, 10, empty);
        Assert.Equal(3, Modes().Count);
        Assert.Equal("fill", Modes().Last());
    }

    [Fact]
    public void Guild_land_entered_for_campfire_and_left_before_boss()
    {
        var (ctl, page, cfg) = Make();
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        cfg.EventTeacher = false;
        cfg.DailyKnbBuy = false;
        ctl.SetAutoMode(true, T0);
        List<string> Ops() => page.Sent.Where(m => m.GetProperty("t").GetString() == "land").Select(m => m.GetProperty("arg").GetProperty("op").GetString()!).ToList();
        string St(string mode, string ld) => JsonSerializer.Serialize(new { t = "status", screen = "game", lv = "180", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode,
            zone = mode == "tower" ? "Thông Thiên Tháp · tầng 900" : "Hoa Sơn", dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", ld });
        Assert.Equal("land", GameLog.KindOf("🔥 Cơ Giáp 2 đã bị hạ! Lửa trại cháy (đã diệt 2 Cơ Giáp hôm nay)"));
        // Lửa trại đang cháy, chưa nhận: đang trong tháp → ra Luyện Công trước, rồi vào lãnh địa
        var t = Run(ctl, T0, 60, St("tower", "0|-1|1|0|100000000|2|5|0|1"));
        Assert.Contains(page.Sent, m => m.GetProperty("t").GetString() == "cmd" && m.GetRawText().Contains("farm"));
        t = Run(ctl, t, 40, St("farm", "0|-1|1|0|100000000|2|5|0|1"));
        Assert.Equal("vao", Ops().Last());
        // Trong lãnh địa: không bị vòng 🔁 kéo lại vào tháp
        int cmds = page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd");
        t = Run(ctl, t, 300, St("farm", "1|-1|1|50000000|100000000|2|5|0|1"));
        Assert.Equal(cmds, page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd"));
        // Sắp tới Boss Hoàng Kim → rời lãnh địa
        boss = true;
        t = Run(ctl, t, 5, St("farm", "1|-1|1|60000000|100000000|2|5|0|1"));
        Assert.Equal("ra", Ops().Last());
        int n = Ops().Count;
        // Ngoài giờ mở thì không vào
        boss = false;
        t = Run(ctl, t, 120, St("farm", "0|-1|1|60000000|100000000|2|5|0|0"));
        Assert.Equal(n, Ops().Count);
        // Lửa đã đủ, không Cơ Giáp, không có quyền gọi → không vào
        t = Run(ctl, t, 120, St("farm", "0|-1|1|100000000|100000000|5|5|0|1"));
        Assert.Equal(n, Ops().Count);
        // Có quyền gọi, bang chưa gọi đủ → vào và gọi Cơ Giáp
        t = Run(ctl, t, 40, St("farm", "0|-1|1|100000000|100000000|3|5|1|1"));
        Assert.Equal("vao", Ops().Last());
        Run(ctl, t, 60, St("farm", "1|-1|1|100000000|100000000|3|5|1|1"));
        Assert.Equal("cg", Ops().Last());
    }

    [Fact]
    public void Guild_land_wears_hong_anh_at_the_campfire_and_power_for_co_giap()
    {
        var (ctl, page, cfg) = Make();
        ctl.BossHkSchedule = _ => false;
        cfg.EventTeacher = false;
        cfg.DailyKnbBuy = false;
        ctl.SetAutoMode(true, T0);
        List<string> Modes() => page.Sent.Where(m => m.GetProperty("t").GetString() == "jewel").Select(m => m.GetProperty("arg").GetProperty("mode").GetString()!).ToList();
        string St(string ld) => JsonSerializer.Serialize(new { t = "status", screen = "game", lv = "180", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm",
            zone = "Hoa Sơn", dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60", ld });
        // 💍 tắt: trong lãnh địa đang có Cơ Giáp → trang sức sức mạnh để đánh
        var t = Run(ctl, T0, 60, St("1|60|1|0|100000000|3|5|0|1"));
        Assert.Equal(new[] { "power" }, Modes());
        ctl.OnPageMessage("""{"t":"jewelResult","r":"ok","mode":"power","changed":"","exp":100}""", t);
        Assert.Equal("🏯 Lãnh địa bang · ⚔️ Cơ Giáp 3/5 còn 60% máu", ctl.ModeText);
        // Cơ Giáp xong, ngồi Lửa trại → Hồng Ảnh ngay (không chờ 3 phút)
        t = Run(ctl, t, 5, St("1|-1|1|0|100000000|3|5|0|1"));
        Assert.Equal(new[] { "power", "exp" }, Modes());
        Assert.StartsWith("🏯 Lãnh địa bang · 🔥 Lửa trại", ctl.StatusText);
        ctl.OnPageMessage("""{"t":"jewelResult","r":"ok","mode":"exp","changed":"Hồng ảnh Mục Túc","exp":182}""", t);
        // Rời lãnh địa (acc tắt 💍) → đeo lại trang sức sức mạnh
        t = Run(ctl, t, 10, St("0|-1|1|100000000|100000000|5|5|0|1"));
        Assert.Equal("power", Modes().Last());
    }

    [Fact]
    public void Items_with_innate_skills_are_locked_before_selling_junk()
    {
        var (ctl, page, cfg) = Make();
        cfg.AutoSellJunk = true;
        cfg.AutoSellFreeSlots = 20;
        var written = new List<GameLog.Entry>();
        ctl.GameLogSink = (_, list) => written.AddRange(list);
        string Bag(int innate) => JsonSerializer.Serialize(new
        {
            t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode = "farm", modeText = "farm",
            zone = "Hoa Sơn", dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "0/60",
            bag = "175/180", innate,
        });
        var t = Run(ctl, T0, 200, Bag(1));
        Assert.True(Count(page, "lockInnate") >= 1);
        Assert.Equal(0, Count(page, "sellJunk")); // chưa khoá xong thì chưa Bán thừa
        ctl.OnPageMessage("""{"t":"lockInnateResult","r":"ok","locked":"Nhẫn Vô Danh (vốn có +1)","failed":""}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🔒 Đã khoá đồ có kỹ năng vốn có: Nhẫn Vô Danh"));
        Assert.Contains(written, e => e.Kind == "rare" && e.Text.Contains("Nhẫn Vô Danh"));
        Run(ctl, t, 400, Bag(0));
        Assert.True(Count(page, "sellJunk") >= 1);
    }

    private static string Hunt(string mode, string hunt) => JsonSerializer.Serialize(new
    {
        t = "status", screen = "game", lv = "139", gold = "1 vạn", knb = "1 🔒1", mode, modeText = mode,
        zone = mode == "tower" ? "Thông Thiên Tháp · tầng 900" : "Hoa Sơn",
        dt = "200/200", pb = "0/4", ldtHave = "0", ldtUsed = "160/160", ldtBuy = "100/100", st = "20/0", hunt,
    });

    [Fact]
    public void Pending_hunt_leaves_the_tower_and_keeps_going_until_killed()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (k, _) => problems.Add(k);
        var t = Run(ctl, T0, 15, Hunt("tower", "Boss Hoàng Kim (Lệnh Bài)|93"));
        ctl.SetAutoMode(true, t);
        // Truy sát dở ở tháp → ra Luyện Công trước, rồi "Đến chỗ Sát Thủ"
        t = Run(ctl, t, 90, Hunt("tower", "Boss Hoàng Kim (Lệnh Bài)|93"));
        Assert.Equal("farm", ModeCommands(page).Last());
        t = Run(ctl, t, 4 * 60, Hunt("farm", "Boss Hoàng Kim (Lệnh Bài)|80"));
        Assert.Contains(page.Sent, m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "assassinGo");
        // Không bao giờ gọi Sát Thủ mới khi còn truy sát dở
        Assert.DoesNotContain(page.Sent, m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "assassin");
        // Máu vẫn giảm → không báo kẹt
        for (int hp = 79; hp > 40; hp -= 3) t = Run(ctl, t, 60, Hunt("farm", $"Boss Hoàng Kim (Lệnh Bài)|{hp}"));
        Assert.DoesNotContain("assassin-stuck", problems);
        // Hạ xong → ghi nhật ký
        Run(ctl, t, 6, Hunt("farm", ""));
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🗡 Xong truy sát: Boss Hoàng Kim (Lệnh Bài)"));
    }

    [Fact]
    public void Hunt_whose_health_does_not_drop_is_paused_and_alerted_but_never_dropped()
    {
        var (ctl, page, _) = Make();
        var problems = new List<(string Key, string Msg)>();
        ctl.Problem += (k, m) => problems.Add((k, m));
        var t = Run(ctl, T0, 15, Hunt("farm", "Sát Thủ · Bảo Triệt Sơn|79|1"));
        ctl.SetAutoMode(true, t);
        Run(ctl, t, 17 * 60, Hunt("farm", "Sát Thủ · Bảo Triệt Sơn|79|1"));
        var alert = Assert.Single(problems, p => p.Key == "assassin-stuck");
        Assert.Contains("không bỏ truy sát", alert.Msg);
        Assert.DoesNotContain(page.Sent, m => m.ToString().Contains("stDrop") || m.ToString().Contains("Bỏ truy sát"));
    }

    [Fact]
    public void Token_gold_boss_that_does_not_appear_is_not_mistaken_for_the_wave_boss()
    {
        // 08/11 16:23: Boss Hoàng Kim từ Lệnh Bài, "tới Phong Lăng độ" nhưng boss không xuất hiện; app tưởng game đang
        // đánh boss đợt nên đứng yên 15 phút rồi báo "quá trâu"
        var (ctl, page, _) = Make();
        ctl.BossHkSchedule = _ => false;
        var problems = new List<(string Key, string Msg)>();
        ctl.Problem += (k, m) => problems.Add((k, m));
        const string H = "Boss Hoàng Kim (Lệnh Bài)|100|0";
        var t = Run(ctl, T0, 15, Hunt("farm", H));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 40, Hunt("farm", H));
        ctl.OnPageMessage("""{"t":"sysNote","text":"🗺 Boss Hoàng Kim Đường Phi Yến: tới Phong Lăng độ","boss":true}""", t);
        int Go() => page.Sent.Count(m => m.GetProperty("t").GetString() == "cmd" && m.GetProperty("name").GetString() == "assassinGo");
        int before = Go();
        t = Run(ctl, t, 5 * 60, Hunt("farm", H));
        Assert.True(Go() - before >= 4, $"bấm lại {Go() - before} lần");      // mỗi phút một lần
        Assert.DoesNotContain(ctl.Activity.Items, e => e.Text.StartsWith("👑 Boss Hoàng Kim: để game tự đi đánh"));
        t = Run(ctl, t, 3 * 60, Hunt("farm", H));
        var alert = Assert.Single(problems, p => p.Key == "hunt-absent");
        Assert.Contains("không xuất hiện", alert.Msg);
        // Chưa xuất hiện thì không tính là "quá trâu"
        t = Run(ctl, t, 10 * 60, Hunt("farm", H));
        Assert.DoesNotContain(problems, p => p.Key == "assassin-stuck");
        // Boss ra, máu giảm → bấm thưa lại (3 phút)
        t = Run(ctl, t, 30, Hunt("farm", "Boss Hoàng Kim (Lệnh Bài)|90|1"));
        before = Go();
        Run(ctl, t, 150, Hunt("farm", "Boss Hoàng Kim (Lệnh Bài)|60|1"));
        Assert.True(Go() - before <= 1);
        Assert.DoesNotContain(page.Sent, m => m.ToString().Contains("stDrop"));
    }

    [Fact]
    public void Boss_wave_killed_is_noted_not_alerted()
    {
        var (ctl, page, _) = Make();
        var problems = new List<string>();
        ctl.Problem += (k, _) => problems.Add(k);
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        var t = Run(ctl, T0, 15, St("tower"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 70, St("tower"));
        boss = true;
        t = Run(ctl, t, 30, St("tower"));
        ctl.OnPageMessage("""{"t":"sysNote","text":"👑 Boss Hoàng Kim Hà Linh Phiêu xuất hiện — tạm rời Tháp tầng 900 đi săn Boss.","boss":true}""", t);
        t = Run(ctl, t, 3, St("farm"));
        ctl.OnPageMessage("""{"t":"sysNote","text":"👑 Boss Hoàng Kim · Hà Linh Phiêu (cấp 125) ở ngay gần!","boss":true}""", t);
        t = Run(ctl, t, 5 * 60, St("farm"));
        Assert.Equal(0, Count(page, "bossHk")); // boss đã hiện, đang đánh: không cần dự phòng
        ctl.OnPageMessage("""{"t":"sysNote","text":"🎁 Hạ Boss Hoàng Kim: 15.00M kinh nghiệm, 1 Lam Thủy Tinh","boss":true}""", t);
        t = Run(ctl, t, 30 * 60, St("tower"));
        boss = false;
        Run(ctl, t, 6, St("tower"));
        Assert.DoesNotContain("boss-hk", problems);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("👑 Đã hạ Boss Hoàng Kim"));
    }

    [Fact]
    public void Sells_junk_when_bag_is_nearly_full()
    {
        var (ctl, page, cfg) = Make();
        string Bag(string bag) => Status("game", "tower", "Thông Thiên Tháp · tầng 900", bag: bag);
        var t = Run(ctl, T0, 30, Bag("150/180"));
        Assert.Equal(0, Count(page, "sellJunk")); // còn 30 ô
        t = Run(ctl, t, 30, Bag("161/180"));
        Assert.Equal(1, Count(page, "sellJunk"));
        ctl.OnPageMessage("""{"t":"sellJunkResult","r":"ok","sold":23,"gold":"1,2 vạn","before":"161/180","after":"138/180"}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("💰 Hành trang sắp đầy: bán 23 món đồ thừa"));
        // Tắt tuỳ chọn → không bán
        cfg.AutoSellJunk = false;
        Run(ctl, t, 10 * 60, Bag("175/180"));
        Assert.Equal(1, Count(page, "sellJunk"));
    }

    [Fact]
    public void Knb_sent_to_main_keeps_todays_guild_donation()
    {
        var (ctl, page, cfg) = Make();
        cfg.DailyKnbBuy = false;
        cfg.EventTeacher = false;
        cfg.Accounts[0].CharName = "A";
        cfg.Accounts[1].CharName = "Main";
        ctl.Account.CharName = "A";
        cfg.KnbMainName = "Main";
        cfg.KnbSendOn = true;
        cfg.GuildKnbOn = true;
        cfg.GuildKnbAmount = 100;
        cfg.KnbKeep = 1000;
        Run(ctl, T0, 8 * 60, Status("game", "tower", "Thông Thiên Tháp · tầng 900"));
        var send = page.Sent.Single(m => m.GetProperty("t").GetString() == "knbSend").GetProperty("arg");
        Assert.Equal(1100, send.GetProperty("keep").GetInt32()); // góp bang trước, gửi về acc chính sau
    }

    [Fact]
    public void Sends_knb_to_main_character_daily_and_main_collects()
    {
        var (ctl, page, cfg) = Make();
        cfg.DailyKnbBuy = false;
        cfg.Accounts[0].CharName = "A";
        cfg.Accounts[1].CharName = "Main";
        ctl.Account.CharName = "A";
        cfg.KnbMainName = "Main";
        // Chọn acc chính mà chưa bật → không gửi
        var t0 = Run(ctl, T0, 4 * 60, Status("game", "tower", "Thông Thiên Tháp · tầng 900"));
        Assert.DoesNotContain(page.Sent, m => m.GetProperty("t").GetString() == "knbSend");
        cfg.KnbSendOn = true;
        var t = Run(ctl, t0, 4 * 60, Status("game", "tower", "Thông Thiên Tháp · tầng 900"));
        var send = page.Sent.Single(m => m.GetProperty("t").GetString() == "knbSend").GetProperty("arg");
        Assert.Equal("send", send.GetProperty("op").GetString());
        Assert.Equal("Main", send.GetProperty("to").GetString());
        Assert.Equal(1000, send.GetProperty("keep").GetInt32()); // không góp KNB vào bang: chỉ chừa "Mỗi acc giữ lại"
        Assert.Equal(new[] { "A", "Main" }, send.GetProperty("allowed").EnumerateArray().Select(x => x.GetString()));
        ctl.OnPageMessage("""{"t":"knbSendResult","r":"ok","opened":3,"gained":1500,"sent":4200,"mails":0,"knb":1000}""", t);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("💰 Gửi 4,200 KNB cho Main") || e.Text.StartsWith("💰 Gửi 4.200 KNB cho Main"));
        Run(ctl, t, 30 * 60, Status("game", "tower", "Thông Thiên Tháp · tầng 900"));
        Assert.Single(page.Sent, m => m.GetProperty("t").GetString() == "knbSend"); // mỗi ngày một lần

        // Nhân vật chính: chỉ nhận thư, không gửi
        var (main, mainPage, mainCfg) = Make();
        mainCfg.Accounts[0].CharName = "A";
        mainCfg.Accounts[1].CharName = "Main";
        main.Account.CharName = "Main";
        mainCfg.KnbMainName = "Main";
        mainCfg.KnbSendOn = true;
        Run(main, T0, 4 * 60, Status("game", "tower", "Thông Thiên Tháp · tầng 900"));
        var collect = mainPage.Sent.Single(m => m.GetProperty("t").GetString() == "knbSend").GetProperty("arg");
        Assert.Equal("collect", collect.GetProperty("op").GetString());

        // Tên chính không thuộc danh sách acc → không làm gì
        var (other, otherPage, otherCfg) = Make();
        other.Account.CharName = "A";
        otherCfg.Accounts[0].CharName = "A";
        otherCfg.KnbMainName = "Người Lạ";
        otherCfg.KnbSendOn = true;
        Run(other, T0, 4 * 60, Status("game", "tower", "Thông Thiên Tháp · tầng 900"));
        Assert.DoesNotContain(otherPage.Sent, m => m.GetProperty("t").GetString() == "knbSend");
    }

    [Fact]
    public void Guild_week_shortfall_raises_an_alert_on_weekends_for_the_leader()
    {
        var (ctl, _, _) = Make();
        var problems = new List<(string Key, string Msg)>();
        ctl.Problem += (k, msg) => problems.Add((k, msg));
        var saturday = new DateTime(2026, 1, 3, 5, 0, 0, DateTimeKind.Utc).ToLocalTime(); // 12:00 thứ Bảy giờ VN
        string Result(string role, long exempt) => $$$"""{"t":"guildResult","r":"ok","bought":"","daily":20000,"burned":5,"burnPts":100,"kept":"Hồng ảnh Kiếm Bài","stashed":1,"approved":"B","week":{"name":"Bang Thử","cap":1,"fund":23000,"points":23000,"target":200000,"members":3,"active":3,"exemptUntil":{{{exempt}}},"warning":"","role":"{{{role}}}"}}""";
        ctl.OnPageMessage(Result("chung", 0), saturday);
        Assert.Empty(problems); // thành viên thường không báo
        ctl.OnPageMessage(Result("chu", 0), saturday);
        var alert = Assert.Single(problems);
        Assert.Equal("guild-week", alert.Key);
        Assert.Contains("mới 3/15 người", alert.Msg);
        Assert.Contains("điểm tuần còn thiếu", alert.Msg);
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🔥 Lò Đốt Đồ: góp 5 món"));
        Assert.Contains(ctl.Activity.Items, e => e.Text.StartsWith("🔥 Giữ lại (max %): Hồng ảnh Kiếm Bài"));
        // Đơn xin vào bang: bạn tự duyệt, tool không bấm
        Assert.DoesNotContain(ctl.Activity.Items, e => e.Text.StartsWith("🏯 Duyệt vào bang"));
        // Bang mới còn được miễn xét tuần này → không báo
        var (ctl2, _, _) = Make();
        var p2 = new List<string>();
        ctl2.Problem += (k, _) => p2.Add(k);
        long exemptMs = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        ctl2.OnPageMessage(Result("chu", exemptMs), saturday);
        Assert.Empty(p2);
    }

    [Fact]
    public void Boss_prep_does_not_pull_an_account_out_of_a_dungeon()
    {
        var (ctl, page, _) = Make();
        bool boss = false;
        ctl.BossHkSchedule = _ => boss;
        var t = Run(ctl, T0, 15, St("dg"));
        ctl.SetAutoMode(true, t);
        boss = true;
        Run(ctl, t, 5 * 60, St("dg"));
        Assert.DoesNotContain("tower", ModeCommands(page));
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

        // Đang đi thuyền: bấm Auto chỉ bật vòng, không đổi chế độ
        var t = Run(ctl, T0, 15, Ev("farm", "3/3"));
        ctl.SetAutoMode(true, t);
        t = Run(ctl, t, 60, Ev("farm", "3/3"));
        Assert.Contains("boat", page.Commands);
        int sent = Cmds();
        ctl.RequestMode("auto", t);
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
