namespace VLMulti.Core;

// Log đơn giản ra file để tra lỗi; không bao giờ làm app dừng vì lỗi ghi log
public static class Log
{
    private static readonly object Gate = new();

    // Tắt khi chạy test, để test không ghi vào file log thật
    public static bool Enabled { get; set; } = true;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
