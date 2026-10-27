namespace VLMulti.Core;

public static class AppPaths
{
    private static string? _dataDirOverride;

    // Chạy từ source (exe nằm trong bin\…): dữ liệu để cạnh file .sln, không mất khi clean bin\.
    // Bản đóng gói: dữ liệu để cạnh file chạy, chép cả thư mục sang máy/ổ khác vẫn chạy.
    public static string Root { get; } = FindRoot();

    public static string DataDir => _dataDirOverride ?? Path.Combine(Root, "data");
    public static string WebViewDir => Path.Combine(DataDir, "webview");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string CredentialsFile => Path.Combine(DataDir, "credentials.json");
    public static string LogFile => Path.Combine(DataDir, "vlmulti.log");

    // Bản Server dùng thư mục dữ liệu riêng (tham số --data hoặc biến VLMULTI_DATA),
    // để không lẫn với dữ liệu của bản Desktop khi chạy cùng máy
    public static void UseDataDir(string dir) => _dataDirOverride = Path.GetFullPath(dir);

    private static string FindRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        if (!baseDir.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            return baseDir;

        var dir = new DirectoryInfo(baseDir);
        while (dir is not null && !dir.EnumerateFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
