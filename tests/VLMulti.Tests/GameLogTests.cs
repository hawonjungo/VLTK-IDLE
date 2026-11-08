using System.Text.Json;
using VLMulti.Core.Sessions;
using Xunit;

namespace VLMulti.Tests;

public class GameLogTests
{
    [Fact]
    public void Da_tau_travel_lines_are_dropped_and_rewards_are_their_own_kind()
    {
        Assert.True(GameLog.IsNoise("🗺 Dã Tẩu: tới Vi sơn đảo"));
        Assert.True(GameLog.IsNoise("🗺 Nhiệm vụ: tới Mạc Bắc Thảo Nguyên"));
        Assert.True(GameLog.IsNoise("🗺 Dã Tẩu: Săn 30 Thủy Tặc ở Phong Lăng độ: tới Phong Lăng độ"));
        Assert.True(GameLog.IsNoise("📜 Dùng Lệnh Bài Dã Tẩu: +1 nhiệm vụ hôm nay (120/155)."));
        Assert.False(GameLog.IsNoise("🎁 Dã Tẩu #2491: 1 Rương Ngựa"));
        Assert.Equal("datau", GameLog.Classify("🎁 Dã Tẩu #2491: 1 Rương Ngựa"));
        Assert.Equal("datau", GameLog.Classify("🎁 Mốc Dã Tẩu 2500: Tiên Thảo Lộ +20% trong 30 phút"));
        Assert.Equal("datau", GameLog.Classify("📜 Dã Tẩu 10 nhiệm vụ: nhận 1 Lệnh Bài Sát Thủ")); // không còn xếp vào Sát Thủ
        Assert.Equal("loot", GameLog.Classify("🏮 Kỳ Trân Các: mua 10 × Lệnh Bài Dã Tẩu · Nhận 10 Lệnh Bài Dã Tẩu (−20 KNB khóa)"));

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
