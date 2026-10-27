using System.ComponentModel;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using VLMulti.Core;
using VLMulti.Core.Accounts;
using VLMulti.Core.Alerts;
using VLMulti.Core.GameData;
using VLMulti.Core.Sessions;
using VLMulti.Core.Settings;
using VLMulti.Desktop.Controls;
using VLMulti.Desktop.Dialogs;

namespace VLMulti.Desktop;

public partial class MainWindow : Window
{
    // Game idle phải chạy đều kể cả khi khung bị che hoặc app nằm dưới cửa sổ khác
    private const string BrowserArgs =
        "--disable-background-timer-throttling --disable-backgrounding-occluded-windows --disable-renderer-backgrounding";

    private readonly AppSettings _settings;
    private readonly CredentialStore _credentials = new(AppPaths.CredentialsFile, new DpapiProtector());
    private readonly AlertCenter _alerts = new(TimeSpan.FromMinutes(10));
    private readonly Dictionary<string, GameFrame> _frames = new();
    private readonly Dictionary<string, Button> _tabs = new();
    private readonly Dictionary<string, TextBlock[]> _dashRows = new();
    private readonly HashSet<string> _declinedPasswordSave = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TextBlock[] _dashTotal = Array.Empty<TextBlock>();
    private CoreWebView2Environment? _env;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _muted;
    // Luôn tắt khi mở app, tránh vô tình lặp thao tác khi chưa để ý
    private bool _syncOn;
    // Danh sách acc / nhật ký chỉ dựng lại khi nội dung đổi (dựng lại giữa lúc bấm chuột sẽ làm mất cú bấm)
    private string _homeSig = "", _logSig = "";
    private string _logKind = "all";

    private Brush Res(string key) => (Brush)FindResource(key);

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load(AppPaths.SettingsFile);
        RestoreWindowBounds();
        _alerts.Raised += OnAlert;
        _alerts.Cleared += UpdateAlertButton;
        _refreshTimer.Tick += (_, _) => RefreshAll();
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
    }

    private IEnumerable<GameFrame> SelectedFrames => _frames.Values.Where(f => f.Account.Selected);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CoreWebView2Environment env;
        try
        {
            var args = BrowserArgs;
            // Chỉ dùng khi phát triển: mở cổng DevTools (chỉ nghe trên 127.0.0.1) để soi giao diện game
            if (int.TryParse(Environment.GetEnvironmentVariable("VLMULTI_DEBUG_PORT"), out var debugPort))
                args += $" --remote-debugging-port={debugPort}";

            // Một môi trường dùng chung cho cả 4 khung (chung tiến trình chính, GPU, network);
            // mỗi acc tách bằng ProfileName bên trong GameFrame
            env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppPaths.WebViewDir,
                options: new CoreWebView2EnvironmentOptions(args));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Máy chưa có Microsoft Edge WebView2 Runtime.\nHãy cài từ trang của Microsoft rồi mở lại app.",
                "VLMulti", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        _env = env;
        foreach (var acc in _settings.Accounts)
        {
            if (acc.Enabled) AddFrame(acc);

            var tab = new Button { Style = (Style)FindResource("BarButton"), Padding = new Thickness(10, 3, 10, 3) };
            tab.Click += (_, _) => OnTabClick(acc);
            _tabs[acc.Id] = tab;
            BigTabs.Children.Add(tab);
        }

        BuildDashboard();
        BuildLogFilters();
        BuildSettings();
        SetupTray();
        ApplyLayout();
        ApplyPanel();
        UpdateGroup();
        UpdateAlertButton();
        RefreshAll();
        _refreshTimer.Start();
        Log.Write("app started");
        await Task.WhenAll(_frames.Values.Select(f => f.InitializeAsync(env, _settings.GameUrl)));
    }

    private GameFrame AddFrame(AccountSlot acc)
    {
        var frame = new GameFrame(acc)
        {
            AutoRecover = _settings.AutoRecover,
            IdleAlertAfter = TimeSpan.FromMinutes(_settings.IdleAlertMinutes),
            CredentialProvider = id => _credentials.Get(id),
            // Mỗi acc đăng nhập lệch nhau 6 giây
            LoginStagger = TimeSpan.FromSeconds(_frames.Count * 6),
            SettingsProvider = () => _settings,
        };
        frame.KiteChanged += _ => SaveSettings();
        frame.LootFound += OnLootFound;
        frame.SwapRequested += Swap;
        frame.BigRequested += f => ShowInBig(f.Account.Id);
        frame.InputCaptured += OnInputCaptured;
        frame.SelectionChanged += _ => { UpdateGroup(); SaveSettings(); };
        frame.InfoChanged += OnFrameInfoChanged;
        frame.Problem += (f, key, message) => _alerts.Raise(key, f.Account.Id, f.Account.DisplayName, message, DateTime.Now);
        frame.ManualLoginSucceeded += OfferToSavePassword;
        frame.CloseRequested += f => DisableAccount(f.Account);
        _frames[acc.Id] = frame;
        FrameGrid.Children.Add(frame);
        return frame;
    }

    private void RefreshAll()
    {
        var now = DateTime.Now;
        foreach (var f in _frames.Values) f.RefreshLive(now);
        RefreshDashboard();
        RefreshHome(now);
        if (LogView.Visibility == Visibility.Visible) RefreshLog();
        RefreshBossHk();
        UpdateTabs();
    }

    // ---------- Tắt / bật acc ----------

    private void OnTabClick(AccountSlot acc)
    {
        if (_frames.ContainsKey(acc.Id))
        {
            ShowInBig(acc.Id);
            return;
        }

        var answer = MessageBox.Show(this, $"Bật lại {acc.DisplayName}?", "Bật acc",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes && !_frames.ContainsKey(acc.Id)) EnableAccount(acc);
    }

    private void DisableAccount(AccountSlot acc)
    {
        if (!_frames.TryGetValue(acc.Id, out var frame)) return;
        if (_frames.Count <= 1)
        {
            MessageBox.Show(this, "Phải còn ít nhất 1 acc đang chạy.", "Tắt acc", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Tắt {acc.DisplayName}?\n\nGame của acc này sẽ đóng hẳn (không tốn RAM/CPU) cho tới khi bạn bấm vào acc đó (thanh trên cùng hoặc cột Tổng quan) để bật lại.",
            "Tắt acc", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        // Trong lúc hộp thoại mở, có thể acc đã bị tắt hoặc acc khác vừa bị tắt → kiểm tra lại
        if (!_frames.ContainsKey(acc.Id) || _frames.Count <= 1) return;

        frame.Shutdown();
        FrameGrid.Children.Remove(frame);
        _frames.Remove(acc.Id);
        acc.Enabled = false;
        Log.Write($"[{acc.Id}] disabled");

        ApplyLayout();
        UpdateGroup();
        RefreshAll();
        SaveSettings();
    }

    private void EnableAccount(AccountSlot acc)
    {
        if (_env is null || _frames.ContainsKey(acc.Id)) return;

        acc.Enabled = true;
        var frame = AddFrame(acc);
        frame.LoginStagger = TimeSpan.Zero; // bật lẻ một acc → không cần chờ
        Log.Write($"[{acc.Id}] enabled");

        ApplyLayout();
        UpdateGroup();
        SaveSettings();
        _ = frame.InitializeAsync(_env, _settings.GameUrl);
    }

    // ---------- Bố cục ----------

    // Acc đang bật theo thứ tự vị trí: [0] = khung lớn, còn lại là khung nhỏ
    private List<string> ActiveOrder => _settings.SlotOrder.Where(_frames.ContainsKey).ToList();

    private string BigId => ActiveOrder[0];

    private void ApplyLayout()
    {
        var order = ActiveOrder;
        int smallCount = order.Count - 1;
        int rows = Math.Max(1, smallCount);

        // Số hàng khung nhỏ = số acc nhỏ đang bật, để các khung nhỏ luôn chia đều chiều cao
        FrameGrid.RowDefinitions.Clear();
        for (int i = 0; i < rows; i++) FrameGrid.RowDefinitions.Add(new RowDefinition());
        Grid.SetRowSpan(Splitter, rows);

        if (smallCount == 0)
        {
            // Chỉ còn 1 acc: khung lớn chiếm hết, ẩn cột nhỏ
            if (SmallColumn.ActualWidth > 0) RememberSmallColumnRatio();
            SmallColumn.MinWidth = 0;
            SmallColumn.Width = new GridLength(0);
            Splitter.Visibility = Visibility.Collapsed;
        }
        else if (Splitter.Visibility != Visibility.Visible)
        {
            SmallColumn.MinWidth = 220;
            BigColumn.Width = new GridLength(1 - _settings.SmallColumnRatio, GridUnitType.Star);
            SmallColumn.Width = new GridLength(_settings.SmallColumnRatio, GridUnitType.Star);
            Splitter.Visibility = Visibility.Visible;
        }

        for (int slot = 0; slot < order.Count; slot++)
        {
            var frame = _frames[order[slot]];
            bool big = slot == 0;
            Grid.SetColumn(frame, big ? 0 : 2);
            Grid.SetRow(frame, big ? 0 : slot - 1);
            Grid.SetRowSpan(frame, big ? rows : 1);
            frame.SetRole(big, _settings.SmallFrameCssWidth);
        }
        _homeSig = ""; // acc ở khung lớn đổi → dựng lại phần chi tiết
        UpdateTabs();
    }

    // Thanh acc trên cùng: chấm sức khoẻ + tên (cấp); acc ở khung lớn nền xanh dương; acc tắt mờ
    private void UpdateTabs()
    {
        var now = DateTime.Now;
        for (int i = 0; i < _settings.Accounts.Count; i++)
        {
            var acc = _settings.Accounts[i];
            if (!_tabs.TryGetValue(acc.Id, out var tab)) continue;
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            tab.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label } };

            if (!_frames.TryGetValue(acc.Id, out var frame))
            {
                dot.Fill = Res("TextFaint");
                label.Text = $"{acc.DisplayName} (tắt)";
                label.Foreground = Res("TextFaint");
                tab.ToolTip = "Acc đang tắt – bấm để bật lại";
                tab.ClearValue(BackgroundProperty);
                tab.ClearValue(BorderBrushProperty);
                continue;
            }

            dot.Fill = GameFrame.HealthBrush(frame.Controller.HealthAt(now).Level);
            label.Text = frame.Stats.Level is { } lv ? $"{acc.DisplayName} · {lv}" : acc.DisplayName;
            tab.ToolTip = $"Đưa lên khung lớn (Ctrl+{i + 1})";
            if (acc.Id == BigId)
            {
                tab.Background = Res("Accent");
                tab.BorderBrush = Res("Accent");
                label.Foreground = Brushes.White;
            }
            else
            {
                tab.ClearValue(BackgroundProperty);
                tab.ClearValue(BorderBrushProperty);
            }
        }
    }

    private void RememberSmallColumnRatio()
    {
        double sum = BigColumn.ActualWidth + SmallColumn.ActualWidth;
        if (sum > 0 && SmallColumn.ActualWidth > 0) _settings.SmallColumnRatio = SmallColumn.ActualWidth / sum;
    }

    private void Swap(GameFrame a, GameFrame b)
    {
        var order = _settings.SlotOrder;
        int ia = order.IndexOf(a.Account.Id), ib = order.IndexOf(b.Account.Id);
        if (ia < 0 || ib < 0 || ia == ib) return;

        (order[ia], order[ib]) = (order[ib], order[ia]);
        Log.Write($"swap {a.Account.Id} <-> {b.Account.Id}, big = {BigId}");
        ApplyLayout();
        RefreshHome(DateTime.Now);
        SaveSettings();
    }

    private void ShowInBig(string accountId)
    {
        if (_frames.ContainsKey(accountId) && BigId != accountId)
            Swap(_frames[accountId], _frames[BigId]);
    }

    private void OnFrameInfoChanged(GameFrame frame)
    {
        UpdateTabs();
        SaveSettings(); // lưu tên nhân vật mới
    }

    // Ctrl+1..4: đưa acc thứ n lên khung lớn (acc đang tắt thì hỏi bật lại)
    private void GoToAccount_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!int.TryParse(e.Parameter as string, out var n) || n < 1 || n > _settings.Accounts.Count) return;
        OnTabClick(_settings.Accounts[n - 1]);
    }

    // ---------- Cột bên phải ----------

    private void ApplyPanel()
    {
        bool show = _settings.ShowSidePanel;
        PanelColumn.Width = show ? new GridLength(_settings.SidePanelWidth) : new GridLength(0);
        PanelColumn.MinWidth = show ? 260 : 0;
        PanelSplitColumn.Width = new GridLength(show ? 5 : 0);
        SidePanel.Visibility = PanelSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PanelButton.Foreground = show ? Res("TextMain") : Res("TextDim");
    }

    private void TogglePanel_Click(object sender, RoutedEventArgs e) => TogglePanel();

    private void TogglePanel_Executed(object sender, ExecutedRoutedEventArgs e) => TogglePanel();

    private void TogglePanel()
    {
        if (_settings.ShowSidePanel && PanelColumn.ActualWidth > 0) _settings.SidePanelWidth = PanelColumn.ActualWidth;
        _settings.ShowSidePanel = !_settings.ShowSidePanel;
        ApplyPanel();
        SaveSettings();
    }

    private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _settings.SidePanelWidth = PanelColumn.ActualWidth;
        SaveSettings();
    }

    private void SideTab_Checked(object sender, RoutedEventArgs e)
    {
        if (HomeView is null) return; // đang dựng XAML
        HomeView.Visibility = TabHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LogView.Visibility = TabLog.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = TabSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (TabLog.IsChecked == true) { _logSig = ""; RefreshLog(); }
        if (TabSettings.IsChecked == true) LoadSettingsForm();
    }

    // ---------- Tổng quan ----------

    private static TextBlock Text(string text, Brush? fg = null, double size = 13, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = fg,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Border Tile(string key, string value, Brush? valueFg = null) => new()
    {
        Background = Res("CardBg"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(9, 6, 9, 6),
        Margin = new Thickness(0, 0, 6, 0),
        Child = new StackPanel
        {
            Children =
            {
                Text(key, Res("TextDim"), 11),
                Text(value, valueFg ?? Res("TextMain"), 14, bold: true),
            },
        },
    };

    private void RefreshHome(DateTime now)
    {
        if (!IsLoaded || SidePanel.Visibility != Visibility.Visible) return;

        // Chữ hiển thị của mọi acc → chữ ký để biết có cần dựng lại không
        var rows = _settings.Accounts.Select(acc => (acc, frame: _frames.GetValueOrDefault(acc.Id))).ToList();
        string Sig((AccountSlot acc, GameFrame? f) r)
        {
            if (r.f is not { } f) return $"{r.acc.Id}|off|{r.acc.DisplayName}";
            var c = f.Controller;
            var (health, reason) = c.HealthAt(now);
            var big = r.acc.Id == BigId;
            var chain = big && r.acc.AutoMode ? string.Join(",", c.AutoChain(now).Select(s => s.State + s.Detail)) : "";
            var log = big && c.Activity.Items.Count > 0 ? c.Activity.Items[0].Text + c.Activity.Items[0].At : "";
            return string.Join("|", r.acc.Id, big, health, reason, r.acc.DisplayName, f.Stats.Level, GameFrame.StepOf(c, now),
                f.TowerFloor, f.Stats.GoldPerHour(now) is { } g ? (long)(g / 1000) : 0, chain, log, c.DoPhoHeld.Count,
                big ? c.InfoLine + c.Boat + c.WeekBoss + c.Assassin + c.Exp + c.BagUsed : "");
        }
        long gold = 0, knb = 0, knbLocked = 0;
        int doPho = 0;
        foreach (var f in _frames.Values)
        {
            gold += f.Stats.Gold ?? 0;
            knb += f.Stats.Knb ?? 0;
            knbLocked += f.KnbLocked ?? 0;
            doPho += f.Controller.DoPhoHeld.Count;
        }
        var sig = string.Join("\n", rows.Select(Sig)) + $"\n{gold}|{knb}|{knbLocked}|{doPho}";
        if (sig == _homeSig) return;
        _homeSig = sig;

        TotalsGrid.Children.Clear();
        TotalsGrid.Children.Add(Tile("Ngân lượng", MoneyParser.Format(gold)));
        TotalsGrid.Children.Add(Tile("KNB · khóa", $"{Short(knb)} · {Short(knbLocked)}"));
        TotalsGrid.Children.Add(Tile("Đồ phổ", doPho.ToString(), doPho > 0 ? Res("GoldFg") : null));

        AccList.Children.Clear();
        foreach (var (acc, frame) in rows) AccList.Children.Add(AccCard(acc, frame, now));
    }

    private static string Short(long n) => n >= 10_000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    private Border AccCard(AccountSlot acc, GameFrame? frame, DateTime now)
    {
        bool big = frame is not null && acc.Id == BigId;
        var card = new Border
        {
            Background = Res("CardBg"),
            BorderBrush = big ? Res("Accent") : Res("FrameBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Cursor = Cursors.Hand,
            ToolTip = frame is null ? "Acc đang tắt – bấm để bật lại" : big ? null : "Bấm để đưa lên khung lớn",
        };
        card.MouseLeftButtonUp += (_, _) => OnTabClick(acc);
        var body = new StackPanel();
        card.Child = body;

        // Dòng 1: chấm sức khoẻ · tên · cấp ............ bước đang chạy
        var head = new DockPanel();
        var dot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(dot, Dock.Left);
        head.Children.Add(dot);
        var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(8, 0, 0, 0) };
        var pillText = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        pill.Child = pillText;
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        body.Children.Add(head);

        if (frame is null)
        {
            dot.Fill = Res("TextFaint");
            head.Children.Add(Text(acc.DisplayName, Res("TextDim"), 13, bold: true));
            GameFrame.PaintStep(pill, pillText, ("Đã tắt", "mode"));
            body.Children.Add(Text("Bấm để bật lại", Res("TextFaint"), 12));
            card.Opacity = 0.75;
            return card;
        }

        var c = frame.Controller;
        var (health, reason) = c.HealthAt(now);
        dot.Fill = GameFrame.HealthBrush(health);
        head.Children.Add(Text(frame.Stats.Level is { } lv ? $"{acc.DisplayName} · {lv}" : acc.DisplayName, Res("TextMain"), 13, bold: true));
        GameFrame.PaintStep(pill, pillText, GameFrame.StepOf(c, now));

        // Dòng 2: lý do khi có vấn đề, còn lại là môn phái · tầng · vạn/giờ
        var sub = health != Health.Good
            ? reason
            : string.Join(" · ", new[]
            {
                acc.Sect,
                frame.TowerFloor is { } floor ? $"tầng {floor}" : null,
                frame.Stats.GoldPerHour(now) is { } rate ? $"{MoneyParser.Format((long)rate)}/giờ" : null,
            }.Where(x => !string.IsNullOrEmpty(x)));
        body.Children.Add(Text(sub.Length > 0 ? sub : c.ScreenLabel,
            health == Health.Good ? Res("TextDim") : GameFrame.HealthBrush(health), 12));

        if (big) AddBigDetails(body, frame, now);
        return card;
    }

    // Chi tiết của acc đang ở khung lớn: số liệu, việc hôm nay, Đồ phổ, nhật ký gần nhất
    private void AddBigDetails(StackPanel body, GameFrame frame, DateTime now)
    {
        var c = frame.Controller;
        var stats = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        void Chip(string k, string? v)
        {
            if (string.IsNullOrEmpty(v)) return;
            stats.Children.Add(new Border
            {
                Background = Res("Card2Bg"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 5, 5),
                Child = new TextBlock
                {
                    FontSize = 12,
                    Inlines =
                    {
                        new System.Windows.Documents.Run(k + " ") { Foreground = Res("TextDim") },
                        new System.Windows.Documents.Run(v) { Foreground = Res("TextMain"), FontWeight = FontWeights.SemiBold },
                    },
                },
            });
        }
        Chip("Ngân lượng", frame.Stats.Gold is { } g ? MoneyParser.Format(g) : null);
        Chip("KNB", frame.Stats.Knb is { } k ? (frame.KnbLocked is { } kl ? $"{k} · 🔒{kl}" : k.ToString()) : null);
        Chip("Tầng/giờ", c.Tower.FloorsPerHour(now) is { } fr ? $"{fr:0}" : null);
        Chip("Hòm đồ", c.BagUsed is { } u ? $"{u}/{c.BagMax}" : null);
        Chip("Dã Tẩu", frame.DaTauToday);
        Chip("Phó bản", frame.PhoBan);
        Chip("Thuyền", c.Boat);
        Chip("Boss tuần", c.WeekBoss.Replace(" claim", ""));
        Chip("LB Sát Thủ", c.Assassin.Length > 0 ? c.Assassin.Split('/')[0] : null);
        Chip("Kinh nghiệm", c.Exp);
        body.Children.Add(stats);

        // Việc hôm nay (vòng tự động): 2 cột, mỗi bước một dòng
        if (frame.Account.AutoMode && c.Screen == "game")
        {
            body.Children.Add(Text("Việc hôm nay", Res("TextDim"), 11));
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 3, 0, 4) };
            foreach (var step in c.AutoChain(now))
            {
                var (glyph, fg) = step.State switch
                {
                    "done" => ("", Res("OkFg")),
                    "current" => ("", Res("Accent")),
                    "rest" => ("", Res("WarnFg")),
                    "off" => ("", Res("TextFaint")),
                    _ => ("", Res("TextFaint")),
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 6, 1) };
                row.Children.Add(new TextBlock
                {
                    Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = fg,
                    Width = 18, VerticalAlignment = VerticalAlignment.Center,
                });
                var label = Text(step.Label, step.State is "done" or "current" ? Res("TextMain") : Res("TextDim"), 12,
                    bold: step.State == "current");
                row.Children.Add(label);
                if (step.Detail.Length > 0) row.ToolTip = step.Detail;
                grid.Children.Add(row);
            }
            body.Children.Add(grid);
        }

        if (c.DoPhoHeld.Count > 0)
        {
            var box = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };
            box.Children.Add(Text($"Đồ phổ đang giữ: {c.DoPhoHeld.Count}", Res("GoldFg"), 12, bold: true));
            foreach (var item in c.DoPhoHeld) box.Children.Add(Text(item, Res("GoldFg"), 12));
            body.Children.Add(box);
        }

        foreach (var entry in c.Activity.Items.Take(3))
        {
            var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            line.Inlines.Add(new System.Windows.Documents.Run($"{entry.At:HH:mm}  ") { Foreground = Res("TextFaint") });
            line.Inlines.Add(new System.Windows.Documents.Run(entry.Text) { Foreground = GameFrame.LevelBrush(entry.Level) == Res("TextDim") ? Res("TextMain") : GameFrame.LevelBrush(entry.Level) });
            body.Children.Add(line);
        }
    }

    // ---------- Nhật ký chung ----------

    private static readonly (string Id, string Label)[] LogKindList =
    {
        ("all", "Tất cả"), ("tower", "Tháp"), ("dopho", "Đồ phổ"), ("warn", "Cảnh báo"), ("shop", "Mua & quà"),
    };

    private static string KindOf(ActivityEntry e)
    {
        var t = e.Text;
        if (t.Contains("Đồ phổ")) return "dopho";
        if (e.Level is StatusLevel.Warn or StatusLevel.Error || t.StartsWith("⚠") || t.StartsWith("💀")) return "warn";
        if (t.StartsWith("🛒") || t.StartsWith("🎁") || t.Contains("lệnh bài Dã Tẩu") || t.Contains("Nhận quà")) return "shop";
        if (t.StartsWith("🏆") || t.StartsWith("🗼") || t.Contains("tháp", StringComparison.OrdinalIgnoreCase)) return "tower";
        return "other";
    }

    private void BuildLogFilters()
    {
        foreach (var (id, label) in LogKindList)
        {
            var chip = new RadioButton { Style = (Style)FindResource("FilterChip"), Content = label, GroupName = "logkind", IsChecked = id == "all" };
            chip.Checked += (_, _) => { _logKind = id; _logSig = ""; RefreshLog(); };
            LogKinds.Children.Add(chip);
        }
        LogAccBox.Items.Add(new ComboBoxItem { Content = "Tất cả acc", Tag = "all" });
        foreach (var acc in _settings.Accounts) LogAccBox.Items.Add(new ComboBoxItem { Content = acc.DisplayName, Tag = acc.Id });
        LogAccBox.SelectedIndex = 0;
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        _logSig = "";
        if (IsLoaded) RefreshLog();
    }

    private void RefreshLog()
    {
        var accFilter = (LogAccBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var items = _frames.Values
            .Where(f => accFilter == "all" || f.Account.Id == accFilter)
            .SelectMany(f => f.Controller.Activity.Items.Select(e => (Entry: e, Acc: f.Account.DisplayName)))
            .Where(x => _logKind == "all" || KindOf(x.Entry) == _logKind)
            .OrderByDescending(x => x.Entry.At)
            .Take(300)
            .ToList();
        var sig = $"{_logKind}|{accFilter}|{items.Count}|{(items.Count > 0 ? items[0].Entry.At.Ticks : 0)}";
        if (sig == _logSig) return;
        _logSig = sig;

        LogList.Children.Clear();
        if (items.Count == 0)
        {
            LogList.Children.Add(Text("Chưa có hoạt động nào khớp bộ lọc.", Res("TextDim"), 12));
            return;
        }
        var today = DateTime.Today;
        DateTime? lastDay = null;
        foreach (var (entry, acc) in items)
        {
            if (entry.At.Date != lastDay)
            {
                lastDay = entry.At.Date;
                var label = lastDay == today ? "Hôm nay" : lastDay == today.AddDays(-1) ? "Hôm qua" : $"{lastDay:dd/MM}";
                var head = Text(label, Res("TextDim"), 11, bold: true);
                head.Margin = new Thickness(0, LogList.Children.Count == 0 ? 0 : 10, 0, 4);
                LogList.Children.Add(head);
            }
            var kind = KindOf(entry);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            var dot = new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(0, 6, 9, 0), VerticalAlignment = VerticalAlignment.Top,
                Fill = kind switch
                {
                    "dopho" => Res("GoldFg"),
                    "warn" => Res("WarnFg"),
                    "shop" => Res("Accent"),
                    "tower" => Res("OkFg"),
                    _ => Res("TextFaint"),
                },
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = Res("TextMain") },
                    new TextBlock { Text = $"{acc} · {entry.At:HH:mm}", FontSize = 11, Foreground = Res("TextFaint") },
                },
            });
            LogList.Children.Add(row);
        }
    }

    // ---------- Cài đặt ----------

    private CheckBox _setRecover = null!, _setSound = null!, _setTray = null!;
    private CheckBox _setQuest = null!, _setDungeon = null!, _setBoat = null!, _setWeekBoss = null!, _setAssassin = null!;
    private ComboBox _setFallback = null!, _setKiteDir = null!;
    private TextBox _setRest = null!, _setKiteDist = null!, _setKiteInterval = null!;
    private bool _loadingForm;

    private void BuildSettings()
    {
        TextBlock Section(string title) => new()
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Res("TextMain"),
            Margin = new Thickness(0, SettingsBody.Children.Count == 0 ? 0 : 18, 0, 6),
        };
        TextBlock Note(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Res("TextDim"), Margin = new Thickness(0, 0, 0, 8),
        };
        CheckBox Box(string text)
        {
            var box = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 3, 0, 3) };
            box.Click += (_, _) => SaveSettingsForm();
            return box;
        }
        Grid FormRow(string label, Control input)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) });
            Grid.SetColumn(input, 1);
            g.Children.Add(input);
            return g;
        }
        TextBox Number(string tip)
        {
            var t = new TextBox { ToolTip = tip };
            t.LostFocus += (_, _) => SaveSettingsForm();
            t.KeyDown += (_, e) => { if (e.Key == Key.Enter) SaveSettingsForm(); };
            return t;
        }

        var body = SettingsBody.Children;
        body.Add(Section("Chung"));
        body.Add(_setRecover = Box("Tự hồi phục: tải lại khi treo/crash, tự đăng nhập (acc đã lưu mật khẩu), tự bấm Vào Game"));
        body.Add(_setSound = Box("Kêu khi có cảnh báo mới"));
        body.Add(_setTray = Box("Thu nhỏ cửa sổ thì ẩn xuống khay hệ thống (góc đồng hồ); bấm đúp biểu tượng con heo để mở lại"));
        var accounts = new Button { Style = (Style)FindResource("BarButton"), Content = "Tài khoản & mật khẩu game…", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        accounts.Click += Accounts_Click;
        body.Add(accounts);

        body.Add(Section("Vòng tự động"));
        body.Add(Note("Bấm Nhiệm Vụ để bắt đầu vòng (hoặc bật \"Tự động\" trên khung). Mỗi 30 giây app chọn việc theo thứ tự dưới đây. Chết nhanh liền 3 lần trong 15 phút thì Luyện Công, nghỉ tháp theo số phút bên dưới."));
        body.Add(_setQuest = Box("1. Dã Tẩu còn làm được → Nhiệm Vụ (tự mua Lệnh Bài Dã Tẩu)"));
        body.Add(_setDungeon = Box("2. Còn lượt Phó bản → Phó Bản"));
        body.Add(_setBoat = Box("3. Còn chuyến thuyền → Đi thuyền"));
        body.Add(_setWeekBoss = Box("4. Còn lượt Boss tuần → Khiêu chiến, hết lượt thì nhận rương tuần"));
        body.Add(_setAssassin = Box("5. Còn Lệnh Bài Sát Thủ → gọi Sát Thủ"));
        _setFallback = new ComboBox { Items = { new ComboBoxItem { Content = "Leo Tháp", Tag = "tower" }, new ComboBoxItem { Content = "Luyện Công", Tag = "farm" } } };
        _setFallback.SelectionChanged += (_, _) => SaveSettingsForm();
        body.Add(FormRow("6. Xong hết thì", _setFallback));
        body.Add(FormRow("Nghỉ tháp sau 3 lần chết nhanh (phút)", _setRest = Number("5–720 phút")));

        body.Add(Section("Kéo quái khi Leo Tháp"));
        body.Add(Note("Bật riêng từng acc bằng nút \"Kéo quái\" trên khung. Mỗi chu kỳ nhân vật chạy một đoạn cho quái dồn về một phía rồi đánh tiếp. Bạn tự tắt Auto (chơi tay) thì app không kéo."));
        _setKiteDir = new ComboBox();
        foreach (var d in KiteDirections.All) _setKiteDir.Items.Add(new ComboBoxItem { Content = KiteDirections.Label(d), Tag = d });
        _setKiteDir.SelectionChanged += (_, _) => SaveSettingsForm();
        body.Add(FormRow("Hướng chạy", _setKiteDir));
        body.Add(FormRow("Khoảng cách (px)", _setKiteDist = Number("80–600, tính từ giữa màn hình")));
        body.Add(FormRow("Chu kỳ (giây)", _setKiteInterval = Number("3–120 giây")));

        body.Add(Section("Phím tắt"));
        body.Add(Note("Ctrl+1 … Ctrl+4: đưa acc lên khung lớn · Ctrl+B: ẩn/hiện cột này · bấm đúp thanh tiêu đề khung: lên khung lớn · kéo thả thanh tiêu đề: đổi chỗ hai khung."));
        LoadSettingsForm();
    }

    private void LoadSettingsForm()
    {
        _loadingForm = true;
        _setRecover.IsChecked = _settings.AutoRecover;
        _setSound.IsChecked = _settings.AlertSound;
        _setTray.IsChecked = _settings.MinimizeToTray;
        _setQuest.IsChecked = _settings.AutoModeQuest;
        _setDungeon.IsChecked = _settings.AutoModeDungeon;
        _setBoat.IsChecked = _settings.AutoModeBoat;
        _setWeekBoss.IsChecked = _settings.AutoModeWeekBoss;
        _setAssassin.IsChecked = _settings.AutoModeAssassin;
        _setFallback.SelectedIndex = _settings.AutoModeFallback == "farm" ? 1 : 0;
        _setRest.Text = _settings.TowerRestMinutes.ToString();
        _setKiteDir.SelectedItem = _setKiteDir.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.KiteDirection);
        _setKiteDist.Text = _settings.KiteDistance.ToString();
        _setKiteInterval.Text = _settings.KiteIntervalSeconds.ToString();
        _loadingForm = false;
    }

    private void SaveSettingsForm()
    {
        if (_loadingForm) return;
        bool recover = _setRecover.IsChecked == true;
        if (recover != _settings.AutoRecover)
        {
            _settings.AutoRecover = recover;
            foreach (var f in _frames.Values) f.AutoRecover = recover;
        }
        _settings.AlertSound = _setSound.IsChecked == true;
        _settings.MinimizeToTray = _setTray.IsChecked == true;
        _settings.AutoModeQuest = _setQuest.IsChecked == true;
        _settings.AutoModeDungeon = _setDungeon.IsChecked == true;
        _settings.AutoModeBoat = _setBoat.IsChecked == true;
        _settings.AutoModeWeekBoss = _setWeekBoss.IsChecked == true;
        _settings.AutoModeAssassin = _setAssassin.IsChecked == true;
        _settings.AutoModeFallback = _setFallback.SelectedIndex == 1 ? "farm" : "tower";
        if (int.TryParse(_setRest.Text, out var rest)) _settings.TowerRestMinutes = rest;
        if (_setKiteDir.SelectedItem is ComboBoxItem { Tag: string dir }) _settings.KiteDirection = dir;
        if (int.TryParse(_setKiteDist.Text, out var dist)) _settings.KiteDistance = dist;
        if (int.TryParse(_setKiteInterval.Text, out var interval)) _settings.KiteIntervalSeconds = interval;
        SaveSettings(); // Normalize giới hạn các giá trị về khoảng hợp lệ
        LoadSettingsForm(); // hiện lại giá trị đã giới hạn
        Log.Write($"settings: recover={_settings.AutoRecover} quest={_settings.AutoModeQuest} dg={_settings.AutoModeDungeon} boat={_settings.AutoModeBoat} " +
                  $"week={_settings.AutoModeWeekBoss} st={_settings.AutoModeAssassin} fallback={_settings.AutoModeFallback} rest={_settings.TowerRestMinutes}m " +
                  $"kite={_settings.KiteDirection}/{_settings.KiteDistance}px/{_settings.KiteIntervalSeconds}s tray={_settings.MinimizeToTray}");
    }

    // ---------- Boss Hoàng Kim: đếm ngược ----------

    private void RefreshBossHk()
    {
        var (active, atUtc, vnTime) = BossHkSchedule.Next(DateTime.UtcNow);
        var left = atUtc - DateTime.UtcNow;
        string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} giờ {t.Minutes:00} phút" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} phút";
        if (active)
        {
            BossHkText.Text = $"Boss HK đang diễn ra · còn {Dur(left)}";
            BossHkText.Foreground = Res("GoldFg");
        }
        else
        {
            BossHkText.Text = $"Boss HK {vnTime:hh\\:mm} · còn {Dur(left)}";
            BossHkText.Foreground = left.TotalMinutes <= 10 ? Res("WarnFg") : Res("TextDim");
        }
    }

    // ---------- Nhóm điều khiển & đồng bộ ----------

    private void UpdateGroup()
    {
        int count = SelectedFrames.Count();
        GroupLabel.Text = $"Đã chọn {count}/{_frames.Count} acc:";

        foreach (var f in _frames.Values)
            f.SetCapture(_syncOn && f.Account.Selected);

        SyncButton.Content = _syncOn ? "Đồng bộ thao tác: Bật" : "Đồng bộ thao tác: Tắt";
        if (_syncOn)
        {
            SyncButton.Background = Res("OnBg");
            SyncButton.Foreground = Brushes.White;
        }
        else
        {
            SyncButton.ClearValue(BackgroundProperty);
            SyncButton.ClearValue(ForegroundProperty);
        }
    }

    private void OnInputCaptured(GameFrame source, JsonElement ev)
    {
        if (!_syncOn || !source.Account.Selected) return;
        foreach (var f in SelectedFrames)
            if (f != source)
                f.Replay(ev);
    }

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        _syncOn = !_syncOn;
        Log.Write($"sync {(_syncOn ? "on" : "off")}");
        UpdateGroup();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool all = _frames.Values.All(f => f.Account.Selected);
        foreach (var f in _frames.Values) f.SetSelected(!all);
        UpdateGroup();
        SaveSettings();
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var code = (string)button.Tag;
        var targets = SelectedFrames.ToList();
        if (targets.Count == 0)
        {
            CommandFeedback.Text = "Chưa chọn acc nào – tick ô ☑ trên thanh tiêu đề khung, hoặc bấm \"Chọn tất cả\"";
            CommandFeedback.Foreground = Res("WarnFg");
            return;
        }

        foreach (var f in targets)
            f.Controller.RequestMode(code, DateTime.Now);
        SaveSettings(); // nhớ vòng tự động / Leo Tháp đã chọn cho từng acc
        CommandFeedback.Foreground = Res("TextDim");
        CommandFeedback.Text = code == "quest"
            ? $"Đã bắt đầu vòng tự động cho {targets.Count} acc"
            : $"Đã chuyển {targets.Count} acc sang {button.Content} (tắt vòng tự động)";
        RefreshAll();
    }

    private void EnterAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _frames.Values)
            f.RunCommand("enter");
    }

    private void ReloadAll_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Tải lại cả 4 khung game?", "Tải lại", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var f in _frames.Values) f.Reload();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        foreach (var f in _frames.Values) f.SetMuted(_muted);
        MuteButton.Content = _muted ? "" : "";
        MuteButton.ToolTip = _muted ? "Bật tiếng game" : "Tắt tiếng game";
        MuteButton.Foreground = _muted ? Res("WarnFg") : Res("TextDim");
    }

    // ---------- Tài khoản ----------

    private void Accounts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AccountManagerWindow(_settings.Accounts, _credentials) { Owner = this };
        dialog.ShowDialog();
        foreach (var id in dialog.ChangedAccounts.Distinct())
            if (_frames.TryGetValue(id, out var frame)) frame.ResetLoginState();
        SaveSettings();
    }

    private void OfferToSavePassword(GameFrame frame, string user, string password)
    {
        var id = frame.Account.Id;
        if (_credentials.Get(id) is { } saved && saved.User == user && saved.Password == password) return;
        if (!_declinedPasswordSave.Add(id + "/" + user)) return; // đã hỏi và bạn chọn Không trong phiên này

        var answer = MessageBox.Show(this,
            $"Lưu mật khẩu của tài khoản \"{user}\" ({frame.Account.DisplayName}) để lần sau app tự đăng nhập?\n\n" +
            "Mật khẩu được mã hoá bằng Windows, chỉ dùng được trên máy này.",
            "Lưu mật khẩu", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _credentials.Set(id, user, password);
        _declinedPasswordSave.Remove(id + "/" + user);
        frame.ResetLoginState();
        Log.Write($"[{id}] password saved for {user}");
    }

    // ---------- Cảnh báo ----------

    private void OnAlert(Alert alert)
    {
        Log.Write($"ALERT [{alert.AccountId}] {alert.Message}");
        UpdateAlertButton();
        if (_settings.AlertSound) SystemSounds.Exclamation.Play();
        if (!IsActive) NativeMethods.FlashTaskbar(this);
        // Cửa sổ đang ẩn / thu nhỏ / nằm dưới cửa sổ khác → thông báo của Windows
        if (!IsActive || !IsVisible || WindowState == WindowState.Minimized)
            _tray?.ShowBalloonTip(5000, $"VLMulti · {alert.AccountName}", alert.Message, System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void UpdateAlertButton()
    {
        int n = _alerts.Items.Count;
        AlertBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertCount.Text = n > 99 ? "99+" : n.ToString();
        AlertButton.ToolTip = n == 0 ? "Không có cảnh báo" : $"{n} cảnh báo – bấm để xem";
    }

    private void Alerts_Click(object sender, RoutedEventArgs e)
    {
        AlertList.Items.Clear();
        if (_alerts.Items.Count == 0)
            AlertList.Items.Add(Text("Không có cảnh báo.", Res("TextDim")));
        foreach (var a in _alerts.Items)
        {
            AlertList.Items.Add(new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 8),
                Children =
                {
                    new TextBlock { Text = a.Message, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextMain") },
                    new TextBlock { Text = $"{a.AccountName} · {a.At:HH:mm dd/MM}", FontSize = 11, Foreground = Res("TextFaint") },
                },
            });
        }
        AlertPopup.IsOpen = true;
    }

    private void ClearAlerts_Click(object sender, RoutedEventArgs e)
    {
        _alerts.Clear();
        AlertPopup.IsOpen = false;
    }

    // ---------- Khay hệ thống ----------

    private void SetupTray()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/vl.ico"))?.Stream;
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = stream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(stream),
                Text = "VLMulti",
                Visible = true,
                ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip(),
            };
            _tray.ContextMenuStrip.Items.Add("Mở VLMulti", null, (_, _) => RestoreFromTray());
            _tray.ContextMenuStrip.Items.Add("Thoát", null, (_, _) => Close());
            _tray.DoubleClick += (_, _) => RestoreFromTray();
            _tray.BalloonTipClicked += (_, _) => RestoreFromTray();
        }
        catch (Exception ex)
        {
            Log.Write($"tray icon failed: {ex.Message}");
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && _tray is not null)
        {
            Hide();
            _tray.ShowBalloonTip(3000, "VLMulti vẫn đang chạy", "Bấm đúp biểu tượng con heo ở khay hệ thống để mở lại.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _settings.Window?.Maximized == true ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    // ---------- Bảng chi tiết ----------

    // Thứ tự cột của bảng chi tiết
    private const int ColName = 0, ColSect = 1, ColLevel = 2, ColGold = 3, ColKnb = 4, ColDaTau = 5, ColLdtHave = 6,
        ColLdtUsed = 7, ColLdtBuy = 8, ColPhoBan = 9, ColBoat = 10, ColWeekBoss = 11, ColMode = 12, ColScreen = 13,
        ColRate = 14, ColLevels = 15, ColTower = 16, ColDoPho = 17;

    private static readonly string[] DashHeaders =
    {
        "Nhân vật", "Môn phái", "Cấp", "Ngân lượng", "KNB (🔒 khóa)", "Dã Tẩu hôm nay", "Lệnh bài có",
        "LB đã dùng", "Mua LB hôm nay", "Phó bản", "Thuyền", "Boss tuần", "Chế độ", "Trạng thái", "Vạn/giờ",
        "Lên cấp", "Tầng tháp", "Đồ phổ",
    };

    private void BuildDashboard()
    {
        // Mỗi tiêu đề một cột tự co theo nội dung, cột cuối giãn hết phần còn lại
        DashboardGrid.ColumnDefinitions.Clear();
        foreach (var _ in DashHeaders) DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DashboardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dim = Res("TextDim");
        AddDashRow(DashHeaders.Select(h => new TextBlock { Text = h, Foreground = dim }).ToArray());
        foreach (var id in _settings.Accounts.Select(a => a.Id))
            _dashRows[id] = AddDashRow(DashHeaders.Select(_ => new TextBlock()).ToArray());
        _dashTotal = AddDashRow(DashHeaders.Select(_ => new TextBlock { FontWeight = FontWeights.SemiBold }).ToArray());
        _dashTotal[0].Text = "Tổng";

        DashboardPanel.Visibility = _settings.ShowDashboard ? Visibility.Visible : Visibility.Collapsed;
        DashboardButton.Foreground = _settings.ShowDashboard ? Res("TextMain") : Res("TextDim");
    }

    private TextBlock[] AddDashRow(TextBlock[] cells)
    {
        int r = DashboardGrid.RowDefinitions.Count;
        DashboardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < cells.Length; c++)
        {
            cells[c].Margin = new Thickness(0, 1, 16, 1);
            Grid.SetRow(cells[c], r);
            Grid.SetColumn(cells[c], c);
            DashboardGrid.Children.Add(cells[c]);
        }
        return cells;
    }

    private void RefreshDashboard()
    {
        if (DashboardPanel.Visibility != Visibility.Visible) return;
        var now = DateTime.Now;
        long totalGold = 0, totalKnb = 0, totalKnbLocked = 0, totalLdt = 0;
        double totalRate = 0;
        bool anyRate = false, anyLdt = false;
        int totalLevels = 0;

        foreach (var (id, cells) in _dashRows)
        {
            if (!_frames.TryGetValue(id, out var f))
            {
                var acc = _settings.Accounts.First(a => a.Id == id);
                foreach (var c in cells) c.Text = "–";
                cells[ColName].Text = acc.DisplayName;
                cells[ColSect].Text = acc.Sect ?? "–";
                cells[ColScreen].Text = "Đã tắt";
                cells[ColScreen].Foreground = Res("TextDim");
                ShowDoPho(cells[ColDoPho], acc, Array.Empty<string>());
                continue;
            }

            var s = f.Stats;
            var ctl = f.Controller;
            bool inGame = f.Screen == "game";
            var rate = inGame ? s.GoldPerHour(now) : null;

            cells[ColName].Text = f.Account.DisplayName;
            cells[ColSect].Text = f.Account.Sect ?? "–";
            cells[ColLevel].Text = s.Level?.ToString() ?? "–";
            cells[ColGold].Text = s.Gold is { } g ? MoneyParser.Format(g) : "–";
            cells[ColKnb].Text = s.Knb is { } k ? (f.KnbLocked is { } kl ? $"{k} · 🔒{kl}" : k.ToString()) : "–";
            cells[ColDaTau].Text = string.IsNullOrEmpty(f.DaTauToday) ? "–"
                : string.IsNullOrEmpty(f.DaTauMaxPerDay) ? f.DaTauToday : $"{f.DaTauToday} · tối đa {f.DaTauMaxPerDay}";
            cells[ColLdtHave].Text = OrDash(f.LdtHave);
            cells[ColLdtUsed].Text = OrDash(f.LdtUsed);
            cells[ColLdtBuy].Text = OrDash(f.LdtBuy);
            cells[ColPhoBan].Text = OrDash(f.PhoBan);
            cells[ColBoat].Text = OrDash(ctl.Boat);
            cells[ColWeekBoss].Text = OrDash(ctl.WeekBoss.Replace(" claim", ""));
            // Vòng tự động đang bật: hiện cả vòng để biết acc đang ở bước nào
            cells[ColMode].Text = !inGame ? "–" : f.Account.AutoMode || ctl.Maintenance ? ctl.AutoChainText(now) : f.ModeText;
            cells[ColMode].Foreground = ctl.Maintenance ? Res("GoldFg") : inGame && f.Account.AutoMode ? Res("OkFg") : Res("TextMain");
            cells[ColScreen].Text = f.ScreenLabel;
            cells[ColScreen].Foreground = inGame ? Res("OkFg") : Res("WarnFg");
            cells[ColRate].Text = rate is { } r ? $"{MoneyParser.Format((long)r)}/giờ" : "–";
            cells[ColLevels].Text = s.LevelsGained > 0 ? $"+{s.LevelsGained}" : "–";
            cells[ColTower].Text = f.TowerFloor?.ToString() ?? "–";
            ShowDoPho(cells[ColDoPho], f.Account, ctl.DoPhoHeld);

            totalGold += s.Gold ?? 0;
            totalKnb += s.Knb ?? 0;
            totalKnbLocked += f.KnbLocked ?? 0;
            if (long.TryParse(f.LdtHave, out var have)) { totalLdt += have; anyLdt = true; }
            if (rate is { } rr) { totalRate += rr; anyRate = true; }
            totalLevels += s.LevelsGained;
        }

        _dashTotal[ColGold].Text = MoneyParser.Format(totalGold);
        _dashTotal[ColKnb].Text = $"{totalKnb} · 🔒{totalKnbLocked}";
        _dashTotal[ColDaTau].Text = OrDash(Fractions.Sum(_frames.Values.Select(f => f.DaTauToday)));
        _dashTotal[ColLdtHave].Text = anyLdt ? totalLdt.ToString() : "–";
        _dashTotal[ColLdtUsed].Text = OrDash(Fractions.Sum(_frames.Values.Select(f => f.LdtUsed)));
        _dashTotal[ColLdtBuy].Text = OrDash(Fractions.Sum(_frames.Values.Select(f => f.LdtBuy)));
        _dashTotal[ColPhoBan].Text = OrDash(Fractions.Sum(_frames.Values.Select(f => f.PhoBan)));
        _dashTotal[ColRate].Text = anyRate ? $"{MoneyParser.Format((long)totalRate)}/giờ" : "–";
        _dashTotal[ColLevels].Text = totalLevels > 0 ? $"+{totalLevels}" : "–";
    }

    private static string OrDash(string? s) => string.IsNullOrEmpty(s) ? "–" : s;

    // Đồ phổ gần nhất: hiện đủ tên kèm giờ nhặt
    // "Đang giữ 2: A (rương), B (hành trang) · mới nhặt: C (14:05 26/10)"
    private void ShowDoPho(TextBlock cell, AccountSlot acc, IReadOnlyList<string> held)
    {
        var parts = new List<string>();
        if (held.Count > 0) parts.Add($"Đang giữ {held.Count}: {string.Join(", ", held)}");
        if (!string.IsNullOrEmpty(acc.LastDoPho)) parts.Add($"mới nhặt: {acc.LastDoPho} ({acc.LastDoPhoAt:HH:mm dd/MM})");
        if (parts.Count == 0)
        {
            cell.Text = "–";
            cell.ClearValue(TextBlock.ForegroundProperty);
            return;
        }
        cell.Text = string.Join(" · ", parts);
        cell.Foreground = Res("GoldFg");
    }

    private void Dashboard_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowDashboard = !_settings.ShowDashboard;
        DashboardPanel.Visibility = _settings.ShowDashboard ? Visibility.Visible : Visibility.Collapsed;
        DashboardButton.Foreground = _settings.ShowDashboard ? Res("TextMain") : Res("TextDim");
        RefreshDashboard();
        SaveSettings();
    }

    // ---------- Đồ phổ ----------

    // Dòng nhặt đồ thuộc chính acc đó. Dòng "Hệ thống" là thông báo chung cả server (mọi khung đều thấy)
    // → chỉ báo khi có tên một nhân vật của bạn; cùng dòng từ nhiều khung chỉ báo một lần (AlertCenter).
    private void OnLootFound(GameFrame source, string src, string text)
    {
        var owner = src == "pickup"
            ? source.Account
            : _settings.Accounts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.CharName) &&
                System.Text.RegularExpressions.Regex.IsMatch(text,
                    $@"(?<![\p{{L}}\p{{N}}]){System.Text.RegularExpressions.Regex.Escape(a.CharName!)}(?![\p{{L}}\p{{N}}])"));
        if (owner is null) return; // đồ của người chơi khác

        Log.Write($"LOOT [{owner.Id}] {text}");
        var name = LootParser.DoPhoNameOf(text) ?? text;
        if (!_alerts.Raise("loot:" + text, owner.Id, owner.DisplayName, $"🎉 Đồ phổ: {name}", DateTime.Now))
            return; // cùng thông báo đã nhận từ khung khác

        owner.LastDoPho = name;
        owner.LastDoPhoAt = DateTime.Now;
        SaveSettings();
        RefreshDashboard();
    }

    // ---------- Lưu / khôi phục ----------

    private void RestoreWindowBounds()
    {
        BigColumn.Width = new GridLength(1 - _settings.SmallColumnRatio, GridUnitType.Star);
        SmallColumn.Width = new GridLength(_settings.SmallColumnRatio, GridUnitType.Star);

        if (_settings.Window is not { } w) return;

        // Bỏ qua vị trí cũ nếu nó nằm ngoài màn hình hiện tại (ví dụ đã rút màn hình phụ)
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (w.Width < MinWidth || w.Height < MinHeight || !screen.IntersectsWith(new Rect(w.Left, w.Top, w.Width, w.Height)))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = w.Left;
        Top = w.Top;
        Width = w.Width;
        Height = w.Height;
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.Window = new WindowBounds
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };

        RememberSmallColumnRatio(); // bỏ qua khi cột nhỏ đang ẩn (chỉ còn 1 acc)
        if (_settings.ShowSidePanel && PanelColumn.ActualWidth > 0) _settings.SidePanelWidth = PanelColumn.ActualWidth;

        SaveSettings();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Log.Write("app closed");
    }

    private void SaveSettings()
    {
        _settings.Normalize();
        SettingsStore.Save(AppPaths.SettingsFile, _settings);
    }
}
