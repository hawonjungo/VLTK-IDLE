using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VLMulti.Core;
using VLMulti.Core.GameData;
using VLMulti.Core.Page;
using VLMulti.Core.Sessions;
using VLMulti.Core.Settings;

namespace VLMulti.Desktop.Controls;

// Khung game của bản Desktop: WebView2 + thanh tiêu đề (nút, trạng thái, kéo thả).
// Toàn bộ logic (tự đăng nhập, tự hồi phục, kéo quái, luật chế độ…) nằm ở AccountController (Core),
// dùng chung với bản Server.
public partial class GameFrame : UserControl, IGamePage
{
    // Viền khung đang đồng bộ thao tác
    private static readonly Brush SyncBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));

    private readonly DispatcherTimer _supervisor;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _isBig;
    private int _smallCssWidth = 1280;
    private double _bigZoom = 1.0;
    private bool _syncHighlight;
    private string _gameOrigin = "";
    // Đã ép vẽ lại sau lần tải trang đầu chưa (chữa lỗi khung trống đen)
    private bool _renderNudged;

    public AccountController Controller { get; }
    public AccountSlot Account => Controller.Account;

    // Thông tin đọc từ game (xem AccountController)
    public AccountStats Stats => Controller.Stats;
    public string Screen => Controller.Screen;
    public string ScreenLabel => Controller.ScreenLabel;
    public string ModeText => Controller.ModeText;
    public string ModeCode => Controller.ModeCode;
    public string DaTau => Controller.DaTau;
    public string DaTauToday => Controller.DaTauToday;
    public string PhoBan => Controller.PhoBan;
    public string LdtHave => Controller.LdtHave;
    public string LdtUsed => Controller.LdtUsed;
    public string LdtBuy => Controller.LdtBuy;
    public string DaTauMaxPerDay => Controller.DaTauMaxPerDay;
    public long? KnbLocked => Controller.KnbLocked;
    public int? TowerFloor => Controller.TowerFloor;
    public string StatusLabel => Controller.StatusText;

    // Do MainWindow cấp
    public bool AutoRecover { get => Controller.AutoRecover; set => Controller.AutoRecover = value; }
    public TimeSpan IdleAlertAfter { get => Controller.IdleAlertAfter; set => Controller.IdleAlertAfter = value; }
    public Func<string, (string User, string Password)?>? CredentialProvider { get => Controller.CredentialProvider; set => Controller.CredentialProvider = value; }
    public TimeSpan LoginStagger { get => Controller.LoginStagger; set => Controller.LoginStagger = value; }
    public Func<AppSettings>? SettingsProvider { get => Controller.SettingsProvider; set => Controller.SettingsProvider = value; }

    // (khung bị kéo, khung được thả lên)
    public event Action<GameFrame, GameFrame>? SwapRequested;
    // Thao tác thật của người chơi trong khung này (chỉ khi đang bật bắt thao tác)
    public event Action<GameFrame, JsonElement>? InputCaptured;
    public event Action<GameFrame>? SelectionChanged;
    // Tên/cấp/tiền/chế độ/màn hình thay đổi
    public event Action<GameFrame>? InfoChanged;
    // (khung, mã loại, nội dung) – MainWindow đưa vào trung tâm cảnh báo
    public event Action<GameFrame, string, string>? Problem;
    // Bạn tự đăng nhập thành công → MainWindow hỏi có lưu mật khẩu không
    public event Action<GameFrame, string, string>? ManualLoginSucceeded;
    // Bấm ✕ trên thanh tiêu đề → MainWindow tắt acc này
    public event Action<GameFrame>? CloseRequested;
    // Bật/tắt kéo quái hoặc luật tự đổi chế độ → MainWindow lưu cài đặt
    public event Action<GameFrame>? KiteChanged;
    // Trang thấy thông báo Đồ phổ: (khung, nguồn "system"/"pickup", nội dung)
    public event Action<GameFrame, string, string>? LootFound;
    // Bấm đúp thanh tiêu đề → đưa acc này lên khung lớn
    public event Action<GameFrame>? BigRequested;

    public GameFrame(AccountSlot account)
    {
        InitializeComponent();
        Controller = new AccountController(account, this);
        Controller.InfoChanged += () =>
        {
            UpdateTitle();
            UpdateInfoText();
            UpdateAutoModeButton(); // bấm 🔁 Auto bật vòng tự động, chế độ khác tắt
            InfoChanged?.Invoke(this);
        };
        Controller.StatusChanged += () => SetStatus(Controller.StatusText, Controller.StatusLevel);
        Controller.Problem += (key, message) => Problem?.Invoke(this, key, message);
        Controller.InputCaptured += ev => InputCaptured?.Invoke(this, ev);
        Controller.LootFound += (src, text) => LootFound?.Invoke(this, src, text);
        // Hỏi lưu mật khẩu bằng hộp thoại → không mở ngay trong sự kiện của WebView2
        Controller.ManualLoginSucceeded += (user, pass) => Dispatcher.BeginInvoke(() => ManualLoginSucceeded?.Invoke(this, user, pass));

        SelectBox.IsChecked = account.Selected;
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E);
        Web.SizeChanged += (_, _) => ApplyZoom();
        Web.ZoomFactorChanged += (_, _) =>
        {
            // Ghi nhớ mức zoom bạn tự chỉnh ở khung lớn để trả lại khi acc quay về khung lớn
            if (_isBig) _bigZoom = Web.ZoomFactor;
        };
        _supervisor = new DispatcherTimer { Interval = AccountController.SuperviseInterval };
        _supervisor.Tick += (_, _) => { if (Web.CoreWebView2 is not null) Controller.Tick(DateTime.Now); };
        UpdateTitle();
        UpdateKiteButton();
        UpdateAutoModeButton();
    }

    public async Task InitializeAsync(CoreWebView2Environment env, string url)
    {
        try
        {
            _gameOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);

            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = Account.Id; // cookie/localStorage riêng → phiên đăng nhập riêng
            await Web.EnsureCoreWebView2Async(env, options);

            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.__vlAccount = '{Account.Id}';");
            await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScripts.Agent);

            core.WebMessageReceived += (_, e) =>
            {
                // Chỉ nhận tin từ trang game, không nhận từ trang lạ nếu khung bị điều hướng đi nơi khác
                if (e.Source.StartsWith(_gameOrigin, StringComparison.OrdinalIgnoreCase))
                    Controller.OnPageMessage(e.WebMessageAsJson, DateTime.Now);
            };
            core.NavigationStarting += (_, e) => Controller.OnNavigationStarting(e.Uri, DateTime.Now);
            core.NavigationCompleted += (_, e) =>
            {
                Controller.OnNavigationCompleted(e.IsSuccess, e.WebErrorStatus.ToString(), DateTime.Now);
                if (e.IsSuccess && !_renderNudged)
                {
                    _renderNudged = true;
                    NudgeRender();
                }
            };
            core.ProcessFailed += (_, e) => Controller.OnProcessFailed(e.ProcessFailedKind.ToString(), DateTime.Now);            // Link mở cửa sổ mới (Discord, Facebook…) → mở bằng trình duyệt mặc định, game không bị rời trang
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            };

            ApplyZoom();
            core.Navigate(url);
            _supervisor.Start();
        }
        catch (Exception ex)
        {
            Controller.OnInitFailed(ex.ToString());
        }
    }

    // ---------- IGamePage (AccountController gọi) ----------

    public void Post(string json) => Web.CoreWebView2?.PostWebMessageAsJson(json);

    public void Reload() => Web.CoreWebView2?.Reload();

    // ---------- Lệnh từ MainWindow ----------

    public void SetRole(bool isBig, int smallCssWidth)
    {
        bool changed = isBig != _isBig;
        _isBig = isBig;
        _smallCssWidth = smallCssWidth;
        UpdateTitle();
        SetDropHighlight(false); // khung lớn viền xanh dương
        // Khung nhỏ hẹp: bỏ dòng trạng thái (xem ở cột Tổng quan / rê chuột), công tắc chỉ còn biểu tượng
        InfoText.Visibility = isBig ? Visibility.Visible : Visibility.Collapsed;
        SetChipLabel(AutoModeButton, isBig ? "Tự động" : "");
        SetChipLabel(KiteButton, isBig ? "Kéo quái" : "");
        TitleText.MaxWidth = isBig ? double.PositiveInfinity : 110;
        // Khung lớn là khung bạn đang chơi: để bạn tự chọn kênh chat; khung nhỏ giữ thẻ Hệ thống để app đọc phần thưởng
        Controller.KeepSystemChat = !isBig;
        ApplyZoom();
        if (changed) NudgeRender();
    }

    // Đóng hẳn trình duyệt của khung (giải phóng RAM/CPU). Khung không dùng lại được sau đó.
    public void Shutdown()
    {
        _supervisor.Stop();
        Web.Dispose();
        Log.Write($"[{Account.Id}] frame closed");
    }

    public void SetMuted(bool muted)
    {
        if (Web.CoreWebView2 is not null)
            Web.CoreWebView2.IsMuted = muted;
    }

    // Bật/tắt việc gửi thao tác thật của khung này về app (khi đồng bộ đang bật và acc được chọn)
    public void SetCapture(bool on)
    {
        _syncHighlight = on;
        SetDropHighlight(false);
        Controller.SetCapture(on);
    }

    public void Replay(JsonElement ev) => Controller.Replay(ev);

    public void RunCommand(string name, object? arg = null) => Controller.RunCommand(name, arg);

    public void ResetLoginState() => Controller.ResetLoginState();

    // ---------- Hiển thị ----------

    // Có lúc khung WebView2 tải trang bình thường nhưng không hiện lên (trống đen) – đã gặp 2 lần
    // ở acc1. Ẩn rồi hiện lại khung buộc WebView2 vẽ lại vào đúng chỗ.
    private void NudgeRender()
    {
        if (Web.CoreWebView2 is null) return;
        Web.Visibility = Visibility.Hidden;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => Web.Visibility = Visibility.Visible);
    }

    // Khung nhỏ: thu nhỏ trang sao cho bề rộng logic luôn = SmallFrameCssWidth,
    // nhờ đó 3 khung nhỏ có bố cục game giống hệt nhau.
    private void ApplyZoom()
    {
        if (Web.CoreWebView2 is null || Web.ActualWidth <= 0) return;

        double zoom = _isBig ? _bigZoom : Math.Clamp(Web.ActualWidth / _smallCssWidth, 0.25, 1.0);
        if (Math.Abs(Web.ZoomFactor - zoom) > 0.001)
            Web.ZoomFactor = zoom;
        // Không cho Ctrl+cuộn chuột đổi zoom ở khung nhỏ, nếu không bố cục sẽ lệch nhau
        Web.CoreWebView2.Settings.IsZoomControlEnabled = _isBig;
    }

    private void UpdateTitle()
    {
        TitleText.Text = Stats.Level is { } lv ? $"{Account.DisplayName} · Lv {lv}" : Account.DisplayName;
        TitleText.ToolTip = string.IsNullOrWhiteSpace(Account.Sect) ? Account.DisplayName : $"{Account.DisplayName} · {Account.Sect}";
        Header.Background = _isBig ? (Brush)FindResource("Card2Bg") : (Brush)FindResource("HeaderBg");
        TitleText.FontSize = _isBig ? 13 : 12;
    }

    // Dòng trạng thái của app ("Đã nhận lệnh", "🛒 Nhận quà…"), màu theo mức; rê chuột xem đủ thông tin acc
    private void SetStatus(string text, StatusLevel level)
    {
        InfoText.Text = text;
        InfoText.Foreground = LevelBrush(level);
        InfoText.ToolTip = $"{text}\n{Controller.InfoLine}";
    }

    public static Brush LevelBrush(StatusLevel level) => level switch
    {
        StatusLevel.Ok => (Brush)Application.Current.FindResource("OkFg"),
        StatusLevel.Warn or StatusLevel.Notice => (Brush)Application.Current.FindResource("WarnFg"),
        StatusLevel.Error => (Brush)Application.Current.FindResource("ErrorFg"),
        _ => (Brush)Application.Current.FindResource("TextDim"),
    };

    public static Brush HealthBrush(Health health) => health switch
    {
        Health.Good => (Brush)Application.Current.FindResource("OkFg"),
        Health.Warn => (Brush)Application.Current.FindResource("WarnFg"),
        _ => (Brush)Application.Current.FindResource("ErrorFg"),
    };

    // Bước đang chạy để hiện trên ô nhỏ: bảo trì / ngoài game / bước của vòng tự động / chế độ đang chạy.
    // Kind: "maint" (vàng) · "out" (ngoài game) · "auto" (xanh, vòng tự động) · "mode" (xám)
    public static (string Label, string Kind) StepOf(AccountController c, DateTime now)
    {
        if (c.Maintenance) return ("Bảo trì", "maint");
        if (c.Screen != "game") return (c.ScreenLabel, "out");
        if (c.Account.AutoMode && c.AutoChain(now).FirstOrDefault(s => s.State == "current") is { } step)
            return (step.Label, "auto");
        // "🗼 Leo Tháp" → "Leo Tháp"
        var mode = c.ModeText.Trim();
        int space = mode.IndexOf(' ');
        if (space > 0 && !char.IsLetterOrDigit(mode[0])) mode = mode[(space + 1)..];
        return (mode.Length > 0 ? mode : "—", "mode");
    }

    public static void PaintStep(Border pill, TextBlock text, (string Label, string Kind) step)
    {
        text.Text = step.Label;
        (pill.Background, text.Foreground) = step.Kind switch
        {
            "maint" => ((Brush)new SolidColorBrush(Color.FromRgb(0x3A, 0x2F, 0x12)), (Brush)Application.Current.FindResource("GoldFg")),
            "auto" => (new SolidColorBrush(Color.FromRgb(0x17, 0x3A, 0x22)), new SolidColorBrush(Color.FromRgb(0x7E, 0xE2, 0xA0))),
            "out" => ((Brush)Application.Current.FindResource("Card2Bg"), (Brush)Application.Current.FindResource("WarnFg")),
            _ => ((Brush)Application.Current.FindResource("Card2Bg"), (Brush)Application.Current.FindResource("TextDim")),
        };
    }

    // Đèn sức khoẻ và bước đang chạy (MainWindow gọi mỗi 2 giây)
    public void RefreshLive(DateTime now)
    {
        var (health, reason) = Controller.HealthAt(now);
        HealthDot.Fill = HealthBrush(health);
        HealthDot.ToolTip = reason;
        var step = StepOf(Controller, now);
        PaintStep(StepPill, StepText, step);
        var current = Account.AutoMode && Screen == "game"
            ? Controller.AutoChain(now).FirstOrDefault(s => s.State == "current") : null;
        StepPill.ToolTip = current is { Detail.Length: > 0 } ? $"{current.Label}: {current.Detail}" : null;
    }

    private void SetDropHighlight(bool on) =>
        Outer.BorderBrush = on ? (Brush)FindResource("Accent")
            : _syncHighlight ? SyncBrush : _isBig ? (Brush)FindResource("Accent") : (Brush)FindResource("FrameBorder");

    private void UpdateInfoText()
    {
        UpdateTitle();
        InfoText.ToolTip = $"{Controller.StatusText}\n{Controller.InfoLine}";
        RefreshLive(DateTime.Now);
    }

    private static void SetChipLabel(Button chip, string label)
    {
        bool icon = label.Length == 1;
        chip.Content = label;
        if (icon) chip.FontFamily = (FontFamily)Application.Current.FindResource("IconFont");
        else chip.ClearValue(FontFamilyProperty);
        chip.Padding = icon ? new Thickness(6, 2, 6, 2) : new Thickness(7, 1, 7, 1);
    }

    private static void PaintChip(Button chip, bool on)
    {
        chip.Background = on ? (Brush)Application.Current.FindResource("OnBg") : (Brush)Application.Current.FindResource("Card2Bg");
        chip.Foreground = on ? Brushes.White : (Brush)Application.Current.FindResource("TextDim");
    }

    private void UpdateAutoModeButton()
    {
        PaintChip(AutoModeButton, Account.AutoMode);
        AutoModeButton.ToolTip = Account.AutoMode
            ? "Vòng tự động: BẬT (Nhiệm Vụ → Phó Bản → Đi thuyền → Boss tuần → Sát Thủ → Leo Tháp → Luyện Công) – bấm để tắt"
            : "Vòng tự động: TẮT – bấm để bật (cài đặt ở thẻ Cài đặt bên phải)";
    }

    private void UpdateKiteButton()
    {
        PaintChip(KiteButton, Account.Kite);
        KiteButton.ToolTip = Account.Kite
            ? "Kéo quái: BẬT (chỉ chạy khi Leo Tháp) – bấm để tắt"
            : "Kéo quái: TẮT – bấm để bật (chỉ chạy khi Leo Tháp)";
    }

    // ---------- Chuột: chọn nhóm, nút, kéo thả ----------

    // Nút "Chọn tất cả" của cửa sổ chính
    public void SetSelected(bool on)
    {
        Account.Selected = on;
        SelectBox.IsChecked = on;
    }

    private void SelectBox_Click(object sender, RoutedEventArgs e)
    {
        Account.Selected = SelectBox.IsChecked == true;
        SelectionChanged?.Invoke(this);
    }

    private void AutoMode_Click(object sender, RoutedEventArgs e)
    {
        Controller.SetAutoMode(!Account.AutoMode, DateTime.Now);
        UpdateAutoModeButton();
        KiteChanged?.Invoke(this);
    }

    private void Kite_Click(object sender, RoutedEventArgs e)
    {
        Controller.SetKite(!Account.Kite, DateTime.Now);
        UpdateKiteButton();
        KiteChanged?.Invoke(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this);

    private void Reload_Click(object sender, RoutedEventArgs e) => Controller.ManualReload();

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _dragArmed = false;
            BigRequested?.Invoke(this);
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragArmed = true;
    }

    private void Header_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _dragArmed = false;
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        DragDrop.DoDragDrop(Header, new DataObject(typeof(GameFrame), this), DragDropEffects.Move);
    }

    private GameFrame? DraggedFrame(DragEventArgs e) =>
        e.Data.GetData(typeof(GameFrame)) is GameFrame f && f != this ? f : null;

    private void Header_DragOver(object sender, DragEventArgs e)
    {
        var src = DraggedFrame(e);
        e.Effects = src is null ? DragDropEffects.None : DragDropEffects.Move;
        SetDropHighlight(src is not null);
        e.Handled = true;
    }

    private void Header_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    private void Header_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        if (DraggedFrame(e) is { } src)
            SwapRequested?.Invoke(src, this);
    }
}
