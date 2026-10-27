using System.Text.Json;
using VLMulti.Core;

namespace VLMulti.Server;

// Cài đặt riêng của bản Server: data/server.json
public sealed class ServerOptions
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Địa chỉ trang web điều khiển. 0.0.0.0 = mọi mạng của máy chủ (cần mật khẩu, nên dùng kèm Tailscale)
    public string Listen { get; set; } = "http://0.0.0.0:8080";

    // Khung hình mỗi acc (px) và số khung hình tối đa mỗi giây: nhỏ hơn = ít CPU hơn
    public int ViewportWidth { get; set; } = 640;
    public int ViewportHeight { get; set; } = 360;
    public int MaxFps { get; set; } = 15;

    // Xem trực tiếp (📺) và ảnh nhanh (📷): chỉ một acc mỗi lúc, nên tạm phóng khung lên cho rõ
    // và bỏ bớt giới hạn khung hình cho mượt; xem xong trả về khung nhỏ ở trên.
    public int LiveWidth { get; set; } = 1280;
    public int LiveHeight { get; set; } = 720;
    public int LiveFps { get; set; } = 30;
    public int LiveQuality { get; set; } = 60;     // chất lượng JPEG 1..100
    public int LiveIdleMinutes { get; set; } = 3;  // không thao tác bấy nhiêu phút → tự tắt xem trực tiếp

    // false = hiện cửa sổ trình duyệt (để gỡ lỗi trên máy có màn hình)
    public bool Headless { get; set; } = true;

    // Mật khẩu trang web (PBKDF2), đặt bằng --set-password
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }

    public static string FilePath => Path.Combine(AppPaths.DataDir, "server.json");

    public static ServerOptions Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"server.json: load failed, using defaults: {ex.Message}");
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public void Normalize()
    {
        ViewportWidth = Math.Clamp(ViewportWidth, 320, 1920);
        ViewportHeight = Math.Clamp(ViewportHeight, 200, 1080);
        MaxFps = Math.Clamp(MaxFps, 0, 60);
        LiveWidth = Math.Clamp(LiveWidth, 320, 1920);
        LiveHeight = Math.Clamp(LiveHeight, 200, 1080);
        LiveFps = Math.Clamp(LiveFps, 5, 60);
        LiveQuality = Math.Clamp(LiveQuality, 20, 95);
        LiveIdleMinutes = Math.Clamp(LiveIdleMinutes, 1, 30);
    }
}
