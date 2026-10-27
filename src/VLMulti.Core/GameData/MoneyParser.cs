using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VLMulti.Core.GameData;

// Game hiển thị tiền kiểu Việt: "3.703" (dấu chấm ngăn nghìn), "1,2 vạn" (dấu phẩy thập phân, 1 vạn = 10.000)
public static partial class MoneyParser
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    [GeneratedRegex(@"^([\d.,]+)\s*(vạn|ức)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public static long? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = Pattern().Match(text.Normalize(NormalizationForm.FormC).Trim());
        if (!m.Success) return null;

        var number = m.Groups[1].Value.Replace(".", "").Replace(',', '.');
        if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;

        long unit = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "vạn" => 10_000,
            "ức" => 100_000_000,
            _ => 1,
        };
        return (long)Math.Round(value * unit);
    }

    [GeneratedRegex(@"^\s*([\d.,]+\s*[kKmM]?)\s*(?:🔒\s*([\d.,]+\s*[kKmM]?))?\s*$")]
    private static partial Regex KnbPattern();

    // Ô KNB của game: "608 🔒20" = 608 KNB và 20 KNB khóa; bản cũ chỉ có một số ("0").
    // Số lớn game viết tắt: "17.2k" = 17.200 (sai lệch tối đa ±50), "1.5m" = 1.500.000
    public static (long? Knb, long? Locked) ParseKnb(string? text)
    {
        var m = KnbPattern().Match(text?.Normalize(NormalizationForm.FormC) ?? "");
        if (!m.Success) return (null, null);
        return (ParseShort(m.Groups[1].Value), m.Groups[2].Success ? ParseShort(m.Groups[2].Value) : 0);
    }

    // "17.2k" / "17,2k" → 17200; không có đuôi k/m thì đọc như số thường ("3.703" = 3703)
    private static long? ParseShort(string text)
    {
        text = text.Trim();
        long unit = text.EndsWith('k') || text.EndsWith('K') ? 1_000 : text.EndsWith('m') || text.EndsWith('M') ? 1_000_000 : 1;
        if (unit == 1) return Parse(text);
        var number = text[..^1].Trim().Replace(',', '.');
        return decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
            ? (long)Math.Round(v * unit) : null;
    }

    public static string Format(long value) =>
        Math.Abs(value) >= 10_000
            ? $"{(value / 10_000m).ToString("#,0.#", Vi)} vạn"
            : value.ToString("#,0", Vi);
}
