using System.Text.Json;

namespace VLMulti.Core.Settings;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"settings: load failed, using defaults: {ex.Message}");
        }

        settings ??= new AppSettings();
        settings.Normalize();
        return settings;
    }

    public static void Save(string path, AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Ghi ra file tạm rồi đổi tên, để tắt app giữa chừng không làm hỏng file cũ
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"settings: save failed: {ex.Message}");
        }
    }
}
