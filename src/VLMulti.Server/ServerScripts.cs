using VLMulti.Core.Page;

namespace VLMulti.Server;

// Script nạp vào trang game ở bản Server, chạy trước agent.js
public static class ServerScripts
{
    // Thay cho window.chrome.webview của WebView2: agent.js gửi tin qua hàm __vlPost (Playwright
    // expose), app gửi tin vào trang bằng __vlDispatch(m). Nhờ vậy agent.js dùng nguyên như bản Desktop.
    private const string Bridge = """
        (() => {
          if (window.chrome && window.chrome.webview) return;
          const listeners = [];
          window.chrome = window.chrome || {};
          window.chrome.webview = {
            postMessage: (m) => { try { window.__vlPost(JSON.stringify(m)); } catch (_) {} },
            addEventListener: (type, fn) => { if (type === 'message') listeners.push(fn); },
          };
          window.__vlDispatch = (m) => { for (const fn of listeners) { try { fn({ data: m }); } catch (_) {} } };
        })();
        """;

    // Giới hạn số khung hình: máy chủ không có card đồ hoạ, CPU phải tự vẽ canvas của game.
    // 15 khung/giây đủ để game chạy bình thường mà tốn ít CPU hơn nhiều (đã đo: ~6 nhân → ~2 nhân cho 4 acc).
    // Lúc xem trực tiếp, server đặt window.__vlFpsCap cao hơn cho mượt.
    private static string FpsCap(int fps) => fps <= 0 ? "" : $$"""
        (() => {
          window.__vlFpsCap = {{fps}};
          window.requestAnimationFrame = (cb) => setTimeout(() => cb(performance.now()), 1000 / window.__vlFpsCap);
          window.cancelAnimationFrame = (id) => clearTimeout(id);
        })();
        """;

    // __VL_ACCOUNT__ được thay bằng mã acc khi tạo trang
    public static string Build(int maxFps) =>
        $"{FpsCap(maxFps)}\n{Bridge}\nwindow.__vlAccount = '__VL_ACCOUNT__';\n{PageScripts.Agent}";
}
