using System.Threading.Channels;
using Microsoft.Playwright;
using VLMulti.Core;
using VLMulti.Core.Sessions;
using VLMulti.Core.Settings;

namespace VLMulti.Server;

// Một acc ở bản Server: một "context" Playwright (cookie/localStorage riêng) + AccountController.
// Mọi thao tác lên trang chạy tuần tự qua một hàng đợi; mọi lời gọi vào AccountController đều nằm trong
// khoá Gate vì Playwright gọi lại từ nhiều luồng.
public sealed class GameSession : IGamePage, IAsyncDisposable
{
    private readonly Channel<Func<Task>> _ops = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly ServerOptions _options;
    private readonly string _statePath;
    private IBrowserContext? _context;
    private IPage? _page;
    private Task? _worker;
    // Khung hình hiện tại của trang (đổi lớn hơn khi xem trực tiếp / chụp ảnh nhanh)
    private int _viewW, _viewH;
    private ICDPSession? _cdp;
    private bool _live;

    public GameSession(AccountSlot account, ServerOptions options)
    {
        _options = options;
        // Dữ liệu trình duyệt của acc (cookie, localStorage). Game lưu tiến trình ở đây trước rồi mới gửi
        // lên máy chủ, nên phải giữ lại giữa các lần chạy, nếu không sẽ mất vài tầng tháp chưa kịp lưu.
        _statePath = Path.Combine(AppPaths.DataDir, "browser", account.Id + ".json");
        Controller = new AccountController(account, this);
    }

    public object Gate { get; } = new();
    public AccountController Controller { get; }
    public AccountSlot Account => Controller.Account;
    public bool IsLive => _live;

    public async Task StartAsync(IBrowser browser, string url, string initScript)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        (_viewW, _viewH) = (_options.ViewportWidth, _options.ViewportHeight);
        _context = await browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = _options.ViewportWidth, Height = _options.ViewportHeight },
            StorageStatePath = File.Exists(_statePath) ? _statePath : null,
        });
        await _context.ExposeFunctionAsync<string>("__vlPost", json => Locked(c => c.OnPageMessage(json, DateTime.Now)));
        await _context.AddInitScriptAsync(initScript.Replace("__VL_ACCOUNT__", Account.Id));

        _page = await _context.NewPageAsync();
        _page.FrameNavigated += (_, frame) =>
        {
            if (frame == _page.MainFrame) Locked(c => c.OnNavigationStarting(frame.Url, DateTime.Now));
        };
        _page.DOMContentLoaded += (_, _) =>
        {
            Locked(c => c.OnNavigationCompleted(true, "", DateTime.Now));
            if (_live) Enqueue(SetFpsCapAsync); // trang tải lại trong lúc đang xem: script đầu trang đặt lại giới hạn cũ
        };
        _page.Crash += (_, _) => Locked(c => c.OnProcessFailed("crash", DateTime.Now));

        _worker = Task.Run(RunOpsAsync);
        Enqueue(() => NavigateAsync(p => p.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 })));
    }

    // ---------- IGamePage (AccountController gọi, đang trong khoá Gate → chỉ xếp hàng, không chờ) ----------

    public void Post(string json) => Enqueue(async () =>
    {
        try
        {
            await _page!.EvaluateAsync("s => window.__vlDispatch && window.__vlDispatch(JSON.parse(s))", json);
        }
        catch (PlaywrightException)
        {
            // Trang đang tải lại: tin nhắn này bỏ qua, trang mới sẽ gửi "hello" và nhận lại trạng thái
        }
    });

    public void Reload() => Enqueue(async () =>
    {
        await SaveStateAsync();
        await NavigateAsync(p => p.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }));
    });

    // ---------- Từ trang web điều khiển ----------

    // Chạm vào ảnh game (toạ độ 0..1) → bấm chuột thật vào đúng chỗ đó, ví dụ cho nhân vật chạy tới
    public void Click(double x, double y) => Enqueue(async () =>
    {
        if (_page is null) return;
        var (px, py) = ToPixels(x, y);
        await _page.Mouse.ClickAsync(px, py);
    });

    // Cuộn chuột tại (x, y) – để cuộn danh sách trong hòm đồ, cửa hàng…
    public void Wheel(double x, double y, double dy) => Enqueue(async () =>
    {
        if (_page is null) return;
        var (px, py) = ToPixels(x, y);
        await _page.Mouse.MoveAsync(px, py);
        await _page.Mouse.WheelAsync(0, (float)Math.Clamp(dy, -2000, 2000));
    });

    // Vài phím cần khi thao tác (đóng bảng, xác nhận)
    private static readonly HashSet<string> AllowedKeys = new() { "Escape", "Enter", "Tab", "Backspace" };
    public void Key(string key) => Enqueue(async () =>
    {
        if (_page is null || !AllowedKeys.Contains(key)) return;
        await _page.Keyboard.PressAsync(key);
    });

    private (float X, float Y) ToPixels(double x, double y) =>
        ((float)(Math.Clamp(x, 0, 1) * _viewW), (float)(Math.Clamp(y, 0, 1) * _viewH));

    public void Locked(Action<AccountController> action)
    {
        lock (Gate)
        {
            try { action(Controller); }
            catch (Exception ex) { Log.Write($"[{Account.Id}] controller error: {ex}"); }
        }
    }

    // 📷 Ảnh nhanh: phóng khung lên cỡ xem trực tiếp cho rõ, chờ game vẽ lại, chụp, rồi trả khung về như cũ
    public Task<byte[]?> SnapshotAsync()
    {
        var done = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Enqueue(async () =>
        {
            if (_page is null) { done.TrySetResult(null); return; }
            bool resize = !_live;
            try
            {
                if (resize)
                {
                    await SetViewportAsync(_options.LiveWidth, _options.LiveHeight);
                    await Task.Delay(800);
                }
                done.TrySetResult(await _page.ScreenshotAsync(new() { Type = ScreenshotType.Jpeg, Quality = 80, Timeout = 10000 }));
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                done.TrySetResult(null); // trang đang tải lại hoặc bận
            }
            finally
            {
                if (resize) await SetViewportAsync(_options.ViewportWidth, _options.ViewportHeight);
            }
        });
        if (!queued) done.TrySetResult(null);
        return done.Task;
    }

    // ---------- 📺 Xem trực tiếp: Chrome gửi từng khung hình JPEG (CDP Page.startScreencast) ----------

    // onFrame gửi khung hình tới trình duyệt người xem; khung kế tiếp chỉ tới sau khi gửi xong
    // (screencastFrameAck), nên mạng chậm thì tự giảm số khung hình chứ không dồn ứ.
    public Task StartLiveAsync(Func<byte[], Task> onFrame) => RunAsync(async () =>
    {
        if (_page is null || _context is null) throw new InvalidOperationException("Trang chưa sẵn sàng");
        _live = true;
        await SetViewportAsync(_options.LiveWidth, _options.LiveHeight);
        await SetFpsCapAsync();
        _cdp = await _context.NewCDPSessionAsync(_page);
        var cdp = _cdp;
        cdp.Event("Page.screencastFrame").OnEvent += (_, e) =>
        {
            if (e is not { } f) return;
            var data = Convert.FromBase64String(f.GetProperty("data").GetString() ?? "");
            var id = f.GetProperty("sessionId").GetInt32();
            _ = Task.Run(async () =>
            {
                try
                {
                    await onFrame(data);
                    await cdp.SendAsync("Page.screencastFrameAck", new() { ["sessionId"] = id });
                }
                catch (Exception)
                {
                    // Người xem đã đóng hoặc đã dừng xem: bỏ khung này
                }
            });
        };
        await cdp.SendAsync("Page.startScreencast", new()
        {
            ["format"] = "jpeg",
            ["quality"] = _options.LiveQuality,
            ["maxWidth"] = _options.LiveWidth,
            ["maxHeight"] = _options.LiveHeight,
        });
        Log.Write($"[{Account.Id}] live view started");
    });

    public Task StopLiveAsync() => RunAsync(async () =>
    {
        if (!_live) return;
        _live = false;
        if (_cdp is { } cdp)
        {
            _cdp = null;
            try
            {
                await cdp.SendAsync("Page.stopScreencast");
                await cdp.DetachAsync();
            }
            catch (PlaywrightException) { }
        }
        await SetFpsCapAsync();
        await SetViewportAsync(_options.ViewportWidth, _options.ViewportHeight);
        Log.Write($"[{Account.Id}] live view stopped");
    });

    private async Task SetViewportAsync(int w, int h)
    {
        if (_page is null || (w == _viewW && h == _viewH)) return;
        try
        {
            await _page.SetViewportSizeAsync(w, h);
            (_viewW, _viewH) = (w, h);
        }
        catch (PlaywrightException) { }
    }

    // Đang xem: bỏ bớt giới hạn khung hình cho mượt; xem xong trả về mức tiết kiệm CPU
    private async Task SetFpsCapAsync()
    {
        if (_page is null || _options.MaxFps <= 0) return;
        var fps = _live ? Math.Max(_options.LiveFps, _options.MaxFps) : _options.MaxFps;
        try { await _page.EvaluateAsync("f => { if (window.__vlFpsCap) window.__vlFpsCap = f; }", fps); }
        catch (PlaywrightException) { }
    }

    // Chạy op trong hàng đợi của trang và chờ nó xong (lỗi được ném lại cho bên gọi)
    private Task RunAsync(Func<Task> op)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = Enqueue(async () =>
        {
            try { await op(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        if (!queued) done.TrySetException(new InvalidOperationException("Acc đang tắt"));
        return done.Task;
    }

    public void RequestSaveState() => Enqueue(SaveStateAsync);

    public async Task SaveStateAsync()
    {
        if (_context is null) return;
        try
        {
            var tmp = _statePath + ".tmp";
            await _context.StorageStateAsync(new() { Path = tmp, IndexedDB = true });
            File.Move(tmp, _statePath, overwrite: true);
        }
        catch (Exception ex) when (ex is PlaywrightException or IOException or TimeoutException)
        {
            Log.Write($"[{Account.Id}] save browser state failed: {ex.Message}");
        }
    }

    private async Task NavigateAsync(Func<IPage, Task> navigate)
    {
        try
        {
            await navigate(_page!);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            Locked(c => c.OnNavigationCompleted(false, ex.Message.Split('\n')[0], DateTime.Now));
        }
    }

    private bool Enqueue(Func<Task> op) => _ops.Writer.TryWrite(op);

    private async Task RunOpsAsync()
    {
        await foreach (var op in _ops.Reader.ReadAllAsync())
        {
            try { await op(); }
            catch (Exception ex) { Log.Write($"[{Account.Id}] page operation failed: {ex.Message}"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ops.Writer.TryComplete();
        if (_worker is not null) await Task.WhenAny(_worker, Task.Delay(5000));
        await SaveStateAsync();
        if (_context is not null)
        {
            try { await _context.CloseAsync(); } catch (PlaywrightException) { }
        }
        Log.Write($"[{Account.Id}] session closed");
    }
}
