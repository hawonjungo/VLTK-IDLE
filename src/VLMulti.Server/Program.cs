using System.Security.Cryptography;
using System.Text.Json;
using VLMulti.Core;
using VLMulti.Core.Sessions;
using VLMulti.Server;

// VLMulti.Server – chạy các acc bằng Chromium ẩn (Linux hoặc Windows) và mở trang web điều khiển.
//   VLMulti.Server                         chạy server
//   VLMulti.Server --install-browser       tải Chromium (thêm --with-deps trên Linux để cài thư viện hệ thống, cần root)
//   VLMulti.Server --set-password <mk>     đặt mật khẩu trang web
//   VLMulti.Server --import-credentials    nhập tài khoản game từ stdin: [{"id":"acc1","user":"…","pass":"…"}]
//   --data <thư mục>                       thư mục dữ liệu (mặc định: data cạnh file chạy; hoặc biến VLMULTI_DATA)

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if ((Arg("--data") ?? Environment.GetEnvironmentVariable("VLMULTI_DATA")) is { Length: > 0 } dataDir)
    AppPaths.UseDataDir(dataDir);
else if (!AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
    AppPaths.UseDataDir(Path.Combine(AppContext.BaseDirectory, "data"));
else
    AppPaths.UseDataDir(Path.Combine(AppPaths.Root, "data-server")); // chạy từ source: tách khỏi dữ liệu bản Desktop

if (args.Contains("--install-browser"))
{
    var install = args.Contains("--with-deps") ? new[] { "install", "--with-deps", "chromium" } : new[] { "install", "chromium" };
    return Microsoft.Playwright.Program.Main(install);
}

var options = ServerOptions.Load();
options.Normalize();

if (Arg("--set-password") is { } newPassword)
{
    WebAuth.SetPassword(options, newPassword);
    Console.WriteLine("Đã đặt mật khẩu trang web.");
    return 0;
}

if (args.Contains("--import-credentials"))
{
    var items = JsonSerializer.Deserialize<List<CredentialImport>>(Console.In.ReadToEnd().TrimStart('﻿')) ?? new();
    var n = SessionManager.ImportCredentials(items.Select(i => (i.id, i.user, i.pass)));
    Console.WriteLine($"Đã nhập {n} tài khoản.");
    return 0;
}

// Lần đầu chạy chưa có mật khẩu: tạo ngẫu nhiên và in ra (đổi bằng --set-password)
if (options.PasswordHash is null)
{
    var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', 'x').Replace('/', 'y');
    WebAuth.SetPassword(options, generated);
    Console.WriteLine($"Mật khẩu trang web (lần đầu, hãy đổi bằng --set-password): {generated}");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.WebHost.UseUrls(options.Listen);
builder.Logging.ClearProviders().AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var manager = new SessionManager(options);
var auth = new WebAuth(options);
var app = builder.Build();

// Trang chính: gắn phiên bản vào app.js / app.css (theo giờ sửa file) để trình duyệt / app cài trên điện thoại không
// dùng lại bản cũ sau khi cập nhật (không có Cache-Control, Chrome tự đoán "còn mới" và giữ app.js cũ hàng giờ:
// nút 🐴 hiện ra mà bấm không có gì, 07/11/2026). Các file khác: luôn hỏi lại server (ETag, không đổi thì 304).
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
string Versioned(string html)
{
    foreach (var f in new[] { "app.js", "app.css" })
    {
        var v = File.GetLastWriteTimeUtc(Path.Combine(webRoot, f)).Ticks.ToString("x");
        html = html.Replace($"\"{f}\"", $"\"{f}?v={v}\"");
    }
    return html;
}
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (HttpMethods.IsGet(ctx.Request.Method) && path is "/" or "/index.html")
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync(Versioned(await File.ReadAllTextAsync(Path.Combine(webRoot, "index.html"))));
        return;
    }
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseWebSockets();

// Mọi /api/ (trừ đăng nhập) cần cookie đăng nhập
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (path.StartsWith("/api/") && path != "/api/login" && !auth.IsValid(ctx.Request.Cookies[WebAuth.CookieName]))
    {
        if (ctx.WebSockets.IsWebSocketRequest) Log.Write($"live view rejected: chưa đăng nhập trang web ({path})");
        ctx.Response.StatusCode = 401;
        return;
    }
    await next();
});

app.MapPost("/api/login", (LoginRequest req, HttpContext ctx) =>
{
    if (auth.TooManyFailures) return Results.Json(new { error = "Sai quá nhiều lần, thử lại sau 10 phút" }, statusCode: 429);
    if (auth.Login(req.Password) is not { } token) return Results.Json(new { error = "Sai mật khẩu" }, statusCode: 401);
    ctx.Response.Cookies.Append(WebAuth.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = ctx.Request.IsHttps,
        MaxAge = TimeSpan.FromDays(30),
    });
    return Results.Ok();
});
app.MapPost("/api/logout", (HttpContext ctx) => { ctx.Response.Cookies.Delete(WebAuth.CookieName); return Results.Ok(); });

app.MapGet("/api/state", () => Results.Json(manager.Snapshot()));
app.MapGet("/api/acc/{id}/activity", (string id) => manager.ActivityOf(id) is { } items ? Results.Json(items) : Results.NotFound());
// 📷 Ảnh nhanh: chụp ngay lúc bấm
app.MapGet("/api/acc/{id}/shot", async (string id) =>
    await manager.SnapshotOfAsync(id) is { } jpg ? Results.File(jpg, "image/jpeg") : Results.NotFound());
// 📺 Xem trực tiếp qua WebSocket: server gửi khung hình JPEG, trình duyệt gửi thao tác (chạm, cuộn, phím)
app.Map("/api/acc/{id}/live", async (string id, HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    // Chỉ nhận kết nối mở từ chính trang điều khiển (chặn trang web lạ mở WebSocket bằng cookie của bạn)
    // (qua proxy như "tailscale serve" thì so với X-Forwarded-Host)
    var origin = ctx.Request.Headers.Origin.ToString();
    var hosts = new[] { ctx.Request.Host.Value, ctx.Request.Headers["X-Forwarded-Host"].ToString() };
    if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !hosts.Contains(o.Authority, StringComparer.OrdinalIgnoreCase)))
    {
        Log.Write($"[{id}] live view rejected: origin {origin} khác host {string.Join(" / ", hosts.Where(h => h.Length > 0))}");
        ctx.Response.StatusCode = 403;
        return;
    }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await manager.RunLiveAsync(id, ws, ctx.RequestAborted);
});

// 📜 Nhật ký game theo ngày (giờ VN), lọc theo loại: ?day=2026-11-08&kinds=boss,rare
static DateOnly LogDay(string? day) => DateOnly.TryParse(day, out var d) ? d : GameLog.VnDay(DateTime.Now);
static string[] LogKinds(string? kinds) => (kinds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
static object LogJson(GameLog.Entry e) => new { at = e.At.ToUniversalTime().AddHours(7).ToString("HH:mm:ss"), kind = e.Kind, place = e.Place, @char = e.Char, text = e.Text };
app.MapGet("/api/acc/{id}/gamelog", (string id, string? day, string? kinds) =>
    id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
        ? Results.Json(GameLog.Read(id, LogDay(day), LogKinds(kinds)).Select(LogJson))
        : Results.NotFound());
app.MapGet("/api/gamelog/rare", (string? day) =>
    Results.Json(GameLog.ReadAll(LogDay(day), new[] { "rare" }).Select(LogJson)));
app.MapPost("/api/acc/{id}/click", (string id, ClickRequest req) => manager.Click(id, req.X, req.Y) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/acc/{id}/reload", (string id) => manager.Reload(id) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/acc/{id}/kite", (string id) => manager.ToggleKite(id) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/acc/{id}/kite-config", (string id, KiteConfigRequest req) => manager.SetKiteConfig(id, req) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/acc/{id}/automode", (string id) => manager.ToggleAutoMode(id) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/acc/{id}/select", (string id, ToggleRequest req) => manager.SetSelected(id, req.On) ? Results.Ok() : Results.NotFound());
app.MapPost("/api/acc/{id}/enabled", async (string id, ToggleRequest req) =>
    await manager.SetEnabledAsync(id, req.On) is { } error ? Results.Json(new { error }, statusCode: 400) : Results.Ok());
app.MapPost("/api/acc/{id}/play", async (string id) =>
    await manager.PlayAsync(id) is { } error ? Results.Json(new { error }, statusCode: 400) : Results.Ok());
app.MapPost("/api/acc/{id}/return", async (string id) =>
    await manager.ReturnAsync(id) is { } error ? Results.Json(new { error }, statusCode: 400) : Results.Ok());
app.MapPost("/api/acc/{id}/credentials", (string id, CredentialRequest req) =>
    manager.SetCredentials(id, req.User, req.Password) ? Results.Ok() : Results.Json(new { error = "Thiếu tài khoản hoặc mật khẩu" }, statusCode: 400));

app.MapPost("/api/mode", (ModeRequest req) => Results.Json(new { count = manager.Mode(req.Code) }));
app.MapPost("/api/enter-all", () => { manager.EnterAll(); return Results.Ok(); });
app.MapPost("/api/pause", async (ToggleRequest req) => { await manager.SetPausedAsync(req.On); return Results.Ok(); });
app.MapPost("/api/auto-recover",(ToggleRequest req) => { manager.SetAutoRecover(req.On); return Results.Ok(); });
// 🐴 Mã Trường: quét (mở rương, cất loài mới, lập danh sách chuyển) → bạn xem → chuyển
app.MapGet("/api/horses", () => Results.Json(manager.HorseSnapshot()));
app.MapPost("/api/horses/scan", (ToggleRequest req) =>
    manager.HorseScan(req.On) is { } error ? Results.Json(new { error }, statusCode: 400) : Results.Ok());
app.MapPost("/api/horses/send", () =>
    manager.HorseSend() is { } error ? Results.Json(new { error }, statusCode: 400) : Results.Ok());
app.MapPost("/api/horses/cancel", () => { manager.HorseCancel(); return Results.Ok(); });
app.MapPost("/api/alerts/clear",() => { manager.ClearAlerts(); return Results.Ok(); });
app.MapPost("/api/settings", (SettingsUpdate req) => { manager.UpdateSettings(req); return Results.Ok(); });

await manager.StartAsync();
// Tắt server (Ctrl+C, systemd stop): lưu dữ liệu trình duyệt của từng acc rồi mới thoát
app.Lifetime.ApplicationStopping.Register(() => manager.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(25)));
Console.WriteLine($"VLMulti.Server đang chạy: {options.Listen}  (dữ liệu: {AppPaths.DataDir})");
await app.RunAsync();
return 0;

record LoginRequest(string Password);
record ClickRequest(double X, double Y);
record ToggleRequest(bool On);
record ModeRequest(string Code);
record CredentialRequest(string User, string Password);
record CredentialImport(string id, string user, string pass);
