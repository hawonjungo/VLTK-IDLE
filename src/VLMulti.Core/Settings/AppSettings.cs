using System.Text.Json.Serialization;

namespace VLMulti.Core.Settings;

public sealed class AppSettings
{
    public const int AccountCount = 4;

    public string GameUrl { get; set; } = "https://volamidle.pages.dev/";
    public List<AccountSlot> Accounts { get; set; } = DefaultAccounts();

    // Thứ tự acc theo vị trí: [0] = khung lớn, [1..3] = khung nhỏ từ trên xuống
    public List<string> SlotOrder { get; set; } = new();

    // Tỉ lệ bề rộng cột khung nhỏ so với toàn vùng game
    public double SmallColumnRatio { get; set; } = 0.3;

    // Bề rộng logic (CSS px) của trang trong khung nhỏ. Mọi khung nhỏ dùng chung giá trị
    // này nên bố cục game giống hệt nhau, toạ độ click khớp 1:1 khi đồng bộ.
    public int SmallFrameCssWidth { get; set; } = 1280;

    public WindowBounds? Window { get; set; }

    // Tự tải lại khi crash/treo, tự đăng nhập (acc đã lưu mật khẩu) và tự bấm Vào Game
    public bool AutoRecover { get; set; } = true;

    public bool AlertSound { get; set; } = true;

    // Báo "đứng yên" khi đang trong game mà tiền và cấp không đổi quá số phút này
    public int IdleAlertMinutes { get; set; } = 15;

    public bool ShowDashboard { get; set; } = true;

    // Bản Desktop: cột bên phải (Tổng quan · Nhật ký · Cài đặt), bề rộng của nó, và thu xuống khay khi thu nhỏ cửa sổ
    public bool ShowSidePanel { get; set; } = true;
    public double SidePanelWidth { get; set; } = 330;
    public bool MinimizeToTray { get; set; }

    // Kéo quái khi Leo Tháp: mỗi KiteIntervalSeconds giây, cho nhân vật chạy KiteDistance px theo
    // KiteDirection (Auto vẫn bật). Bật riêng từng acc (AccountSlot.Kite).
    public int KiteDistance { get; set; } = 250;
    public int KiteIntervalSeconds { get; set; } = 8;
    public string KiteDirection { get; set; } = KiteDirections.Line;
    // Kiểu "Hàng dọc": máu dưới KiteFleeHp % thì chạy về đầu đường xa đám quái (0 = tắt)
    public int KiteFleeHp { get; set; } = 40;
    // Kiểu "Hàng dọc": chạy khi có hơn KiteCrowd con quái ở gần (0 = luôn chạy theo nhịp); dừng KitePauseMs ms giữa 2 bước
    public int KiteCrowd { get; set; } = 3;
    public int KitePauseMs { get; set; } = 500;

    // Luật tự đổi chế độ (bật riêng từng acc bằng AccountSlot.AutoMode) – xem GameData.ModeRules
    public bool AutoModeDungeon { get; set; } = true;
    public bool AutoModeQuest { get; set; } = true;
    public bool AutoModeAssassin { get; set; } = true; // dùng hết Lệnh Bài Sát Thủ trước khi leo tháp
    public bool AutoModeBoat { get; set; } = true;     // đi hết 3 chuyến thuyền mỗi ngày
    public bool AutoModeWeekBoss { get; set; } = true; // đánh hết 3 lượt Boss tuần, nhận rương tuần
    public string AutoModeFallback { get; set; } = "tower"; // "tower" hoặc "farm"

    // Sự kiện 🌹 Tri Ân Nhà Giáo (tới hết 20/11/2026): mỗi ngày tặng Thầy Đồ đủ 40 Bó Hoa, ghép thêm khi sắp hết.
    // Giấy Gói mua bằng lượng, tối đa EventTeacherSpendCap vạn mỗi acc mỗi ngày; không bao giờ tiêu KNB.
    public bool EventTeacher { get; set; } = true;
    public int EventTeacherSpendCap { get; set; } = 400;
    // Đổi nguyên liệu thừa lấy quà (Đại Hồng Bao → Tiên Thảo Lộ Đặc Biệt → Hồng Bao), chừa đủ để tặng Bó Hoa tới hết sự kiện
    public bool EventTeacherExchange { get; set; } = true;

    // Mỗi ngày đổi vạn lượng → KNB ở Kỳ Trân Các tới giới hạn của game (1.000 KNB); thiếu vạn thì mở Hồng Bao
    public bool DailyKnbBuy { get; set; } = true;

    // Giữ trạng thái có lợi: trạng thái không có / sắp hết → dùng 1 cái trong Hành trang, luôn chừa 10 cái mỗi loại
    public bool BuffTtl { get; set; } = true;        // 🌿 Tiên Thảo Lộ (+50% kinh nghiệm)
    public bool BuffTtlSpecial { get; set; } = true; // 🌺 Tiên Thảo Lộ Đặc Biệt (+100% kinh nghiệm)
    public bool BuffQht { get; set; } = true;        // 🍶 Quế Hoa Tửu (+20 may mắn)

    // 🏯 Cửa hàng bang: mỗi ngày mua các món đã chọn bằng điểm cống hiến (món tuần thì mua khi tuần còn lượt),
    // thiếu điểm thì góp vạn (1 vạn = 1 điểm) tối đa GuildDonateCap vạn mỗi ngày; acc chưa vào bang thì bỏ qua
    public bool GuildShop { get; set; } = true;
    public bool GuildLdt { get; set; } = true;        // Lệnh Bài Dã Tẩu (10/ngày)
    public bool GuildPhucDuyen { get; set; } = true;  // Bình Phúc Duyên Lộ (Tiểu) (10/ngày)
    public bool GuildTtsp { get; set; } = true;       // Tiên Thảo Lộ Đặc Biệt (5/tuần)
    public bool GuildThbt { get; set; } = true;       // Tinh Hồng Bảo Thạch (6/tuần)
    public int GuildDonateCap { get; set; } = 1500;
    // Cống hiến vạn mỗi ngày tới GuildDailyAmount vạn (0 = tắt; 5.000 / 10.000 / 20.000 – game cho tối đa 20.000/ngày):
    // thiếu vạn thì mở Hồng Bao, hết Hồng Bao thì chừa lại GuildDonateReserve vạn
    public int GuildDailyAmount { get; set; }
    // Bản cũ (bật = 20.000 vạn), chỉ để đọc cài đặt đã lưu
    public bool GuildDonateDaily { get; set; }
    public int GuildDonateReserve { get; set; } = 3000;
    // 🔥 Lò Đốt Đồ: mỗi ngày góp Hoàng Kim bộ chung trong Hành trang (trừ món khoá); giữ lại và cất vào Rương:
    // bộ Động Sát có May mắn đạt mức cao nhất, bộ Hồng Ảnh có Kinh nghiệm nhận được đạt mức cao nhất
    public bool GuildFurnace { get; set; }
    // Cảnh báo điều kiện tuần của bang (thứ Bảy, Chủ Nhật): điểm tuần, ≥ 15 người vào game, quỹ đủ phí duy trì
    public bool GuildWeekAlert { get; set; } = true;
    // 💰 Gửi KNB (thư tặng) về acc chính KnbMainName, mỗi acc chừa KnbKeep KNB; chỉ chạy khi KnbSendOn và đã chọn acc chính.
    // KnbOpenBig: mở hết Đại Hồng Bao trước khi gửi
    public bool KnbSendOn { get; set; }
    public string KnbMainName { get; set; } = "";
    public int KnbKeep { get; set; } = 1000;
    public bool KnbOpenBig { get; set; } = true;

    // 💰 Hành trang còn ≤ AutoSellFreeSlots ô trống → bấm "Bán thừa" của game (bán món không khớp bộ lọc ⚙ Nhặt đồ,
    // không khoá, không phải đồ Dã Tẩu). Đồ Hoàng Kim nằm ở hàng chờ khi túi đầy sẽ tự vào lại hành trang.
    public bool AutoSellJunk { get; set; } = true;
    public int AutoSellFreeSlots { get; set; } = 20;

    // Chết trong tháp (game đẩy ra Luyện Công): vào lại tháp ngay. Chết nhanh (sống dưới TowerFastDeathMinutes phút)
    // TowerFastDeathCount lần trong TowerFastDeathWindowMinutes phút → nghỉ tháp TowerRestMinutes phút, Luyện Công và báo.
    public int TowerFastDeathMinutes { get; set; } = 3;
    public int TowerFastDeathCount { get; set; } = 3;
    public int TowerFastDeathWindowMinutes { get; set; } = 15;
    public int TowerRestMinutes { get; set; } = 120;

    public void Normalize()
    {
        var ids = Accounts.Select(a => a.Id).ToList();
        if (Accounts.Count != AccountCount
            || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct().Count() != AccountCount)
        {
            Accounts = DefaultAccounts();
            ids = Accounts.Select(a => a.Id).ToList();
        }
        foreach (var a in Accounts.Where(a => string.IsNullOrWhiteSpace(a.Name)))
            a.Name = a.Id;
        // Luôn còn ít nhất 1 acc đang bật
        if (!Accounts.Any(a => a.Enabled))
            Accounts[0].Enabled = true;

        if (SlotOrder.Count != AccountCount || !SlotOrder.OrderBy(x => x).SequenceEqual(ids.OrderBy(x => x)))
            SlotOrder = ids.ToList();

        SmallColumnRatio = Math.Clamp(SmallColumnRatio, 0.15, 0.6);
        SmallFrameCssWidth = Math.Clamp(SmallFrameCssWidth, 800, 2560);
        SidePanelWidth = Math.Clamp(SidePanelWidth, 260, 600);
        IdleAlertMinutes = Math.Clamp(IdleAlertMinutes, 3, 240);
        KiteDistance = Math.Clamp(KiteDistance, 80, 600);
        KiteIntervalSeconds = Math.Clamp(KiteIntervalSeconds, 1, 120);
        if (!KiteDirections.All.Contains(KiteDirection)) KiteDirection = KiteDirections.Line;
        KiteFleeHp = Math.Clamp(KiteFleeHp, 0, 90);
        KiteCrowd = Math.Clamp(KiteCrowd, 0, 20);
        KitePauseMs = Math.Clamp(KitePauseMs, 100, 10000);
        foreach (var a in Accounts)
        {
            if (a.KiteCrowd is { } kc) a.KiteCrowd = Math.Clamp(kc, 0, 20);
            if (a.KitePauseMs is { } kp) a.KitePauseMs = Math.Clamp(kp, 100, 10000);
            if (a.KiteDirection is { } d && !KiteDirections.All.Contains(d)) a.KiteDirection = null;
            if (a.KiteDistance is { } kd) a.KiteDistance = Math.Clamp(kd, 80, 600);
            if (a.KiteInterval is { } ki) a.KiteInterval = Math.Clamp(ki, 1, 120);
            if (a.KiteFleeHp is { } kf) a.KiteFleeHp = Math.Clamp(kf, 0, 90);
        }
        if (AutoModeFallback is not ("tower" or "farm")) AutoModeFallback = "tower";
        TowerFastDeathMinutes = Math.Clamp(TowerFastDeathMinutes, 1, 60);
        TowerFastDeathCount = Math.Clamp(TowerFastDeathCount, 2, 10);
        TowerFastDeathWindowMinutes = Math.Clamp(TowerFastDeathWindowMinutes, TowerFastDeathMinutes, 240);
        TowerRestMinutes = Math.Clamp(TowerRestMinutes, 5, 720);
        EventTeacherSpendCap = Math.Clamp(EventTeacherSpendCap, 0, 5000);
        GuildDonateCap = Math.Clamp(GuildDonateCap, 0, 20000);
        GuildDonateReserve = Math.Clamp(GuildDonateReserve, 0, 1_000_000);
        if (GuildDonateDaily && GuildDailyAmount == 0) GuildDailyAmount = 20000; // cài đặt cũ
        GuildDailyAmount = Math.Clamp(GuildDailyAmount, 0, 20000);
        GuildDonateDaily = GuildDailyAmount > 0;
        KnbKeep = Math.Clamp(KnbKeep, 0, 1_000_000);
        KnbMainName = (KnbMainName ?? "").Trim();
        AutoSellFreeSlots = Math.Clamp(AutoSellFreeSlots, 1, 200);
    }

    // Mặc định 3 acc ở khung nhỏ nằm trong nhóm điều khiển, acc ở khung lớn thì không
    private static List<AccountSlot> DefaultAccounts() =>
        Enumerable.Range(1, AccountCount)
            .Select(i => new AccountSlot { Id = $"acc{i}", Name = $"Acc {i}", Selected = i > 1 })
            .ToList();
}

public sealed class AccountSlot
{
    // Id cũng là tên profile trình duyệt → không đổi sau khi tạo, nếu không sẽ mất phiên đăng nhập
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    // Tên nhân vật lấy từ game; nhớ lại để lần mở sau hiện ngay, trước khi game tải xong
    public string? CharName { get; set; }
    public string? Sect { get; set; }

    // Có nằm trong nhóm nhận lệnh/đồng bộ hay không
    public bool Selected { get; set; }

    // Tự đăng nhập khi đã lưu mật khẩu (tắt được riêng từng acc trong Quản lý acc)
    public bool AutoLogin { get; set; } = true;

    // Acc bị tắt: không mở khung game, không tốn RAM/CPU (bật lại bằng tab trên cùng)
    public bool Enabled { get; set; } = true;

    // Kéo quái khi Leo Tháp (mặc định tắt)
    public bool Kite { get; set; }
    // Thông số kéo quái riêng của acc này; null = dùng cài đặt chung (AppSettings.KiteDirection / KiteDistance /
    // KiteIntervalSeconds / KiteFleeHp)
    public string? KiteDirection { get; set; }
    public int? KiteDistance { get; set; }
    public int? KiteInterval { get; set; }
    public int? KiteFleeHp { get; set; }
    public int? KiteCrowd { get; set; }
    public int? KitePauseMs { get; set; }

    // Tự đổi chế độ theo luật (mặc định tắt)
    public bool AutoMode { get; set; }

    // Bạn đã chọn Leo Tháp cho acc này: chết trong tháp thì tự vào lại (khi không bật luật 🔁)
    public bool StayInTower { get; set; }

    // Đồ phổ nhặt được gần nhất (hiện ở cột "Đồ phổ" của bảng tổng quan)
    public string? LastDoPho { get; set; }
    public DateTime? LastDoPhoAt { get; set; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(CharName) ? Name : CharName;
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
