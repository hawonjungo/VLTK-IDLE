using System.Text.RegularExpressions;

namespace VLMulti.Core.GameData;

// Cộng các giá trị dạng "a/b" của nhiều acc, ví dụ Dã Tẩu "21/40" + "52/52 (+12 lệnh bài)" → "73/92"
public static partial class Fractions
{
    [GeneratedRegex(@"^\s*(\d+)\s*/\s*(\d+)")]
    private static partial Regex Leading();

    public static string Sum(IEnumerable<string?> values)
    {
        long top = 0, bottom = 0;
        bool any = false;
        foreach (var v in values)
        {
            var m = Leading().Match(v ?? "");
            if (!m.Success) continue;
            top += long.Parse(m.Groups[1].Value);
            bottom += long.Parse(m.Groups[2].Value);
            any = true;
        }
        return any ? $"{top}/{bottom}" : "";
    }
}
