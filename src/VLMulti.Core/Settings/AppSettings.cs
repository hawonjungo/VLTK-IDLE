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
    public string KiteDirection { get; set; } = KiteDirections.Alternate;

    // Luật tự đổi chế độ (bật riêng từng acc bằng AccountSlot.AutoMode) – xem GameData.ModeRules
    public bool AutoModeDungeon { get; set; } = true;
    public bool AutoModeQuest { get; set; } = true;
    public bool AutoModeAssassin { get; set; } = true; // dùng hết Lệnh Bài Sát Thủ trước khi leo tháp
    public bool AutoModeBoat { get; set; } = true;     // đi hết 3 chuyến thuyền mỗi ngày
    public bool AutoModeWeekBoss { get; set; } = true; // đánh hết 3 lượt Boss tuần, nhận rương tuần
    public string AutoModeFallback { get; set; } = "tower"; // "tower" hoặc "farm"

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
        KiteIntervalSeconds = Math.Clamp(KiteIntervalSeconds, 3, 120);
        if (!KiteDirections.All.Contains(KiteDirection)) KiteDirection = KiteDirections.Alternate;
        if (AutoModeFallback is not ("tower" or "farm")) AutoModeFallback = "tower";
        TowerFastDeathMinutes = Math.Clamp(TowerFastDeathMinutes, 1, 60);
        TowerFastDeathCount = Math.Clamp(TowerFastDeathCount, 2, 10);
        TowerFastDeathWindowMinutes = Math.Clamp(TowerFastDeathWindowMinutes, TowerFastDeathMinutes, 240);
        TowerRestMinutes = Math.Clamp(TowerRestMinutes, 5, 720);
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
