namespace VLMulti.Core.GameData;

// Game hiện chỉ cho biết môn phái qua tên file biểu tượng phái ("img/pl/wudang.png" → "wudang")
public static class SectNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // Đã thấy trong game
        ["wudu"] = "Ngũ Độc Giáo",
        ["kunlun"] = "Côn Lôn phái",
        ["wudang"] = "Võ Đang phái",
        // Theo cách đặt tên của game cho 7 phái còn lại (chưa gặp trong game)
        ["emei"] = "Nga My phái",
        ["tangmen"] = "Đường Môn",
        ["shaolin"] = "Thiếu Lâm phái",
        ["tianwang"] = "Thiên Vương Bang",
        ["cuiyan"] = "Thúy Yên môn",
        ["gaibang"] = "Cái Bang",
        ["tianren"] = "Thiên Nhẫn Giáo",
    };

    // Mã lạ (game thêm phái mới…) thì hiện nguyên mã để vẫn phân biệt được
    public static string? FromCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Names.TryGetValue(code, out var name) ? name : code;
}
