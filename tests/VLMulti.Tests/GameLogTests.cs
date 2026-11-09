using System.Text.Json;
using VLMulti.Core.Sessions;
using Xunit;

namespace VLMulti.Tests;

public class GameLogTests
{
    [Fact]
    public void Status_lines_are_kept_and_noise_is_dropped()
    {
        // Trạng thái: đi làm nhiệm vụ, qua tầng, mốc 100 tầng, gục ngã
        Assert.Equal("quest", GameLog.KindOf("🗺 Dã Tẩu: tới Vi sơn đảo"));
        Assert.Equal("quest", GameLog.KindOf("🗺 Nhiệm vụ: tới Mạc Bắc Thảo Nguyên"));
        Assert.Equal("tower", GameLog.KindOf("🗼 Tháp: qua tầng 14041 · nhận 1 Huyền Tinh cấp 9"));
        Assert.Equal("milestone", GameLog.KindOf("🗼 Tháp: qua tầng 14100 · nhận 1 Tử Thủy Tinh, 1 Thần Bí Khoáng Thạch"));
        Assert.Equal("death", GameLog.KindOf("Gục ở tháp thử thách tầng 14062. Kỷ lục: 14061."));
        Assert.Equal("tower", GameLog.KindOf("🗼 Chế độ Leo Tháp: tự vào Thông Thiên Tháp, gục ngã thì chuyển về Luyện Công."));
        // Rác: nhặt đồ thường, dùng lệnh bài, dòng lặp của boss, mua ở Kỳ Trân Các
        Assert.True(GameLog.IsNoise("Nhặt được Quan Âm Phát Quán"));
        Assert.True(GameLog.IsNoise("📜 Dùng Lệnh Bài Dã Tẩu: +1 nhiệm vụ hôm nay (120/155)."));
        Assert.True(GameLog.IsNoise("🎁 Tháp tầng 14100"));
        Assert.True(GameLog.IsNoise("🔥 Boss Hoàng Kim · Đoan Mộc Duệ nổi giận — sát thương ×2!"));
        Assert.True(GameLog.IsNoise("Hạ Boss Hoàng Kim · Đoan Mộc Duệ"));
        Assert.True(GameLog.IsNoise("🏮 Kỳ Trân Các: mua 10 × Lệnh Bài Dã Tẩu · Nhận 10 Lệnh Bài Dã Tẩu (−20 KNB khóa)"));
        // Đồ hiếm
        Assert.Equal("rare", GameLog.KindOf("Nhặt được Tỏa Tử Hoàng Kim giáp"));
        Assert.Equal("rare", GameLog.KindOf("💎 HK Môn Phái: Băng Hàn Huyền Y Thúc Giáp ← Mốc tầng 14100"));
        Assert.Equal("rare", GameLog.KindOf("🎁 Dã Tẩu #2435: 1 Võ Lâm Mật Tịch"));
        Assert.Equal("boss", GameLog.KindOf("👑 Hạ Boss Hoàng Kim Đoan Mộc Duệ!")); // "Boss Hoàng Kim" không phải món đồ
        Assert.False(GameLog.IsNoise("🎁 Dã Tẩu #2491: 1 Rương Ngựa"));
        Assert.Equal("datau", GameLog.Classify("🎁 Dã Tẩu #2491: 1 Rương Ngựa"));
        Assert.Equal("datau", GameLog.Classify("🎁 Mốc Dã Tẩu 2500: Tiên Thảo Lộ +20% trong 30 phút"));
        Assert.Equal("datau", GameLog.Classify("📜 Dã Tẩu 10 nhiệm vụ: nhận 1 Lệnh Bài Sát Thủ")); // không còn xếp vào Sát Thủ

        Assert.Equal("Săn 30 Thủy Tặc", GameLog.DaTauTask("🗺 Dã Tẩu: Săn 30 Thủy Tặc ở Phong Lăng độ: tới Phong Lăng độ"));
        Assert.Null(GameLog.DaTauTask("🗺 Dã Tẩu: tới Vi sơn đảo"));
        Assert.Equal("🎁 Dã Tẩu #2491 (Săn 30 Thủy Tặc): 1 Rương Ngựa", GameLog.WithDaTauTask("🎁 Dã Tẩu #2491: 1 Rương Ngựa", "Săn 30 Thủy Tặc"));
    }

    [Fact]
    public void Da_tau_summary_adds_up_items_and_skips_experience()
    {
        var t = DateTime.UtcNow;
        GameLog.Entry E(string text) => new(t, "datau", "", "", text);
        var s = JsonSerializer.SerializeToElement(GameLog.DaTauSummary(new[]
        {
            E("🎁 Dã Tẩu #1 (Săn 30 Thủy Tặc): 1 Rương Ngựa"),
            E("🎁 Dã Tẩu #2: 3% kinh nghiệm"),
            E("🎁 Dã Tẩu #3: 2 Hồng Bao"),
            E("🎁 Dã Tẩu #4: 1 Rương Ngựa, 1 Lục Thủy Tinh"),
            E("🎁 Dã Tẩu #5: Tiên Thảo Lộ +20% trong 30 phút"),
            E("🎁 Mốc Dã Tẩu 2500: 1 Tẩy Tủy Kinh"),
        }));
        Assert.Equal(5, s.GetProperty("quests").GetInt32());
        var items = s.GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("n").GetInt64());
        Assert.Equal(2, items["Rương Ngựa"]);
        Assert.Equal(2, items["Hồng Bao"]);
        Assert.Equal(1, items["Lục Thủy Tinh"]);
        Assert.Equal(1, items["Tiên Thảo Lộ"]);
        Assert.Equal(1, items["Tẩy Tủy Kinh"]);
        Assert.DoesNotContain(items.Keys, k => k.Contains("kinh nghiệm"));
    }
}
