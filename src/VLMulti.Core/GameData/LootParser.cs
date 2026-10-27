using System.Text.RegularExpressions;

namespace VLMulti.Core.GameData;

// Tách tên đồ phổ từ thông báo Hệ thống của game, ví dụ
//   "17:37 📢 Hệ thống: Chúc mừng Tiêu Dao nhận được Đồ phổ Ma Sát Cử Hỏa Liêu Thiên Uyển khi vượt Tháp!"
// Thông báo Phó bản chưa thấy mẫu thật → lấy phần chữ sau "Đồ phổ" tới " khi/tại/từ/ở …" hoặc dấu câu.
public static partial class LootParser
{
    [GeneratedRegex(@"Đồ\s*phổ\s*[:\-]?\s*(.+?)(?:\s+(?:khi|tại|từ|ở|trong)\s|[!.,;(]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DoPhoName();

    // Dòng nhặt đồ ("pickup") thuộc chính acc đó. Dòng "Hệ thống" là thông báo chung cả server
    // → chỉ tính khi có tên một nhân vật của bạn; không có thì là đồ của người chơi khác (null).
    public static Settings.AccountSlot? OwnerOf(IEnumerable<Settings.AccountSlot> accounts, Settings.AccountSlot source, string src, string text) =>
        src == "pickup"
            ? source
            : accounts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.CharName) &&
                Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(a.CharName!)}(?![\p{{L}}\p{{N}}])"));

    public static string? DoPhoNameOf(string? text)
    {
        var m = DoPhoName().Match(text ?? "");
        var name = m.Success ? m.Groups[1].Value.Trim().Trim('[', ']', '"', '“', '”') : "";
        return name.Length == 0 ? null : name;
    }
}
