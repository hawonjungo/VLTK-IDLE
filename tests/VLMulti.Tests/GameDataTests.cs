using VLMulti.Core.Alerts;
using VLMulti.Core.GameData;
using VLMulti.Core.Recovery;

namespace VLMulti.Tests;

public class MoneyParserTests
{
    [Theory]
    [InlineData("3.703", 3_703)]
    [InlineData("9.435", 9_435)]
    [InlineData("323", 323)]
    [InlineData("1,2 vạn", 12_000)]
    [InlineData("1,8 vạn", 18_000)]
    [InlineData("1.035 vạn", 10_350_000)]
    [InlineData("2 vạn", 20_000)]
    [InlineData("0", 0)]
    public void Parses_game_formats(string text, long expected) =>
        Assert.Equal(expected, MoneyParser.Parse(text));

    [Fact]
    public void Parses_decomposed_unicode() =>
        Assert.Equal(12_000, MoneyParser.Parse("1,2 vạn".Normalize(System.Text.NormalizationForm.FormD)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0🪙 1,2 vạn")]
    public void Rejects_unknown_text(string? text) => Assert.Null(MoneyParser.Parse(text));

    [Theory]
    [InlineData(3_703, "3.703")]
    [InlineData(12_000, "1,2 vạn")]
    [InlineData(10_350_000, "1.035 vạn")]
    [InlineData(-5_000, "-5.000")]
    public void Formats_like_the_game(long value, string expected) =>
        Assert.Equal(expected, MoneyParser.Format(value));

    [Theory]
    [InlineData("608 🔒20", 608L, 20L)]
    [InlineData("0", 0L, 0L)]
    [InlineData("1.226 🔒 1.035", 1226L, 1035L)]
    [InlineData("17.2k 🔒1113", 17200L, 1113L)]   // số lớn game viết tắt
    [InlineData("17,2k 🔒1,5k", 17200L, 1500L)]
    [InlineData("1.5m", 1500000L, 0L)]
    public void Parses_knb_with_locked(string text, long knb, long locked) =>
        Assert.Equal(((long?)knb, (long?)locked), MoneyParser.ParseKnb(text));

    [Fact]
    public void Knb_rejects_garbage() => Assert.Equal(((long?)null, (long?)null), MoneyParser.ParseKnb("abc"));

    [Fact]
    public void Format_roundtrips()
    {
        foreach (var v in new long[] { 0, 999, 9_999, 10_000, 123_000, 10_350_000 })
            Assert.Equal(v, MoneyParser.Parse(MoneyParser.Format(v)));
    }
}

public class FractionsTests
{
    [Fact]
    public void Sums_valid_values_and_skips_others() =>
        Assert.Equal("45/80", Fractions.Sum(new[] { "21/40", "24/40", "", null, "abc" }));

    [Fact]
    public void Empty_when_nothing_valid() => Assert.Equal("", Fractions.Sum(new[] { "", null }));

    [Fact]
    public void Ignores_token_suffix() =>
        Assert.Equal("99/99", Fractions.Sum(new[] { "52/52 (+12 lệnh bài)", "47/47 (+7 lệnh bài)" }));
}

public class KiteDirectionsTests
{
    [Fact]
    public void Alternate_switches_sides() =>
        Assert.Equal(new[] { (-250, 0), (250, 0), (-250, 0) },
            Enumerable.Range(0, 3).Select(i => VLMulti.Core.Settings.KiteDirections.Offset("alternate", 250, i)));

    [Fact]
    public void Square_returns_to_start_after_four_steps()
    {
        var steps = Enumerable.Range(0, 4).Select(i => VLMulti.Core.Settings.KiteDirections.Offset("square", 300, i)).ToList();
        Assert.Equal(new[] { (300, 0), (0, 300), (-300, 0), (0, -300) }, steps);
        Assert.Equal((0, 0), (steps.Sum(s => s.Dx), steps.Sum(s => s.Dy)));
        Assert.Equal(steps[0], VLMulti.Core.Settings.KiteDirections.Offset("square", 300, 4)); // vòng mới
    }

    [Fact]
    public void Fixed_direction() =>
        Assert.Equal((0, 200), VLMulti.Core.Settings.KiteDirections.Offset("down", 200, 5));
}

public class ModeRulesTests
{
    private static readonly VLMulti.Core.Settings.AppSettings Cfg = new();
    private static bool None(string _) => false;

    [Fact]
    public void Quest_first_when_da_tau_left() =>
        Assert.Equal("quest", ModeRules.Decide("10/40", "2/2", None, Cfg)?.Mode);

    [Fact]
    public void Dungeon_when_da_tau_done() =>
        Assert.Equal("dg", ModeRules.Decide("40/40", "2/2", None, Cfg)?.Mode);

    [Theory]
    [InlineData("52/52", "12", "40/160", "100/100", "200", true)]   // còn lệnh bài
    [InlineData("52/52", "0", "40/160", "55/100", "200", true)]    // còn mua được
    [InlineData("52/52", "0", "40/160", "100/100", "200", false)]  // hết lệnh bài, đã mua đủ
    [InlineData("200/200", "5", "160/160", "100/100", "200", false)] // đạt tối đa ngày
    [InlineData("52/52", "3", "160/160", "55/100", "200", false)]  // đã dùng hết lượt lệnh bài
    public void Quest_counts_tokens(string dt, string have, string used, string buy, string max, bool expected) =>
        Assert.Equal(expected, ModeRules.QuestLeft(new QuestCounts(dt, have, used, buy, max)) is not null);

    [Fact]
    public void Tower_when_all_done_including_token_suffix() =>
        Assert.Equal("tower", ModeRules.Decide("52/52 (+12 lệnh bài)", "0/2", None, Cfg)?.Mode);

    [Fact]
    public void Unknown_counts_are_skipped() =>
        Assert.Equal("tower", ModeRules.Decide("", "", None, Cfg)?.Mode);

    [Fact]
    public void Blocked_modes_are_skipped()
    {
        Assert.Equal("dg", ModeRules.Decide("10/40", "2/2", m => m == "quest", Cfg)?.Mode);
        Assert.Equal("farm", ModeRules.Decide("40/40", "0/2", m => m == "tower", Cfg)?.Mode);
    }

    [Fact]
    public void Disabled_rules_and_farm_fallback()
    {
        var cfg = new VLMulti.Core.Settings.AppSettings { AutoModeDungeon = false, AutoModeQuest = false, AutoModeFallback = "farm" };
        Assert.Equal("farm", ModeRules.Decide("10/40", "2/2", None, cfg)?.Mode);
    }
}

public class LootParserTests
{
    [Theory]
    [InlineData("17:37 📢 Hệ thống: Chúc mừng Hoàng Cái nhận được Đồ phổ Ma Sát Cử Hỏa Liêu Thiên Uyển khi vượt Tháp!", "Ma Sát Cử Hỏa Liêu Thiên Uyển")]
    [InlineData("19:41 📢 Hệ thống: Chúc mừng Mmmm nhận được Đồ phổ Ngự Long Lượng Ngân Bảo Đao khi vượt Tháp!", "Ngự Long Lượng Ngân Bảo Đao")]
    [InlineData("Hệ thống: Chúc mừng A nhận được Đồ phổ Kim Phong Đồng Khí tại Phó bản Dược Vương Cốc!", "Kim Phong Đồng Khí")]
    [InlineData("Hệ thống: Chúc mừng A nhận được Đồ phổ [Thiên Quang Song Bạo Hoàn] từ Phó bản", "Thiên Quang Song Bạo Hoàn")]
    [InlineData("Nhặt được Đồ phổ: Vô Gian Ỷ Thiên Kiếm", "Vô Gian Ỷ Thiên Kiếm")]
    public void Extracts_name(string text, string expected) => Assert.Equal(expected, LootParser.DoPhoNameOf(text));

    [Fact]
    public void No_name_without_keyword() => Assert.Null(LootParser.DoPhoNameOf("Chúc mừng A mở Hồng Bao nhận được 1000 vạn lượng!"));
}

public class SectNamesTests
{
    [Theory]
    [InlineData("wudu", "Ngũ Độc Giáo")]
    [InlineData("KUNLUN", "Côn Lôn phái")]
    [InlineData("newsect", "newsect")]
    public void Maps_icon_code(string code, string expected) => Assert.Equal(expected, SectNames.FromCode(code));

    [Fact]
    public void Empty_code_is_null() => Assert.Null(SectNames.FromCode(""));
}

public class AttemptLimiterTests
{
    [Fact]
    public void Blocks_after_max_and_recovers_after_window()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var limiter = new AttemptLimiter(3, TimeSpan.FromMinutes(30));
        Assert.True(limiter.TryAcquire(t0));
        Assert.True(limiter.TryAcquire(t0.AddMinutes(1)));
        Assert.True(limiter.TryAcquire(t0.AddMinutes(2)));
        Assert.False(limiter.TryAcquire(t0.AddMinutes(3)));
        Assert.True(limiter.TryAcquire(t0.AddMinutes(31)));
    }
}

public class AccountStatsTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void Rate_needs_five_minutes_of_data()
    {
        var s = new AccountStats();
        s.Update(T0, 10_000, 0, 20);
        s.Update(T0.AddMinutes(4), 11_000, 0, 20);
        Assert.Null(s.GoldPerHour(T0.AddMinutes(4)));

        s.Update(T0.AddMinutes(10), 12_000, 0, 20);
        Assert.Equal(12_000, s.GoldPerHour(T0.AddMinutes(10))!.Value, precision: 3);
    }

    [Fact]
    public void Idle_time_counts_from_last_change()
    {
        var s = new AccountStats();
        s.Update(T0, 100, 0, 5);
        s.Update(T0.AddMinutes(10), 100, 0, 5);
        Assert.Equal(TimeSpan.FromMinutes(20), s.IdleFor(T0.AddMinutes(20)));

        s.Update(T0.AddMinutes(21), 150, 0, 5);
        Assert.Equal(TimeSpan.FromMinutes(1), s.IdleFor(T0.AddMinutes(22)));
    }

    [Fact]
    public void Levels_gained_since_session_start()
    {
        var s = new AccountStats();
        s.Update(T0, 0, 0, 20);
        s.Update(T0.AddMinutes(30), 0, 0, 23);
        Assert.Equal(3, s.LevelsGained);
    }
}

public class AlertCenterTests
{
    [Fact]
    public void Same_alert_is_suppressed_during_cooldown()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        var center = new AlertCenter(TimeSpan.FromMinutes(10));
        Assert.True(center.Raise("idle", "acc1", "A", "x", t0));
        Assert.False(center.Raise("idle", "acc1", "A", "x", t0.AddMinutes(5)));
        Assert.True(center.Raise("idle", "acc2", "B", "x", t0.AddMinutes(5)));
        Assert.True(center.Raise("idle", "acc1", "A", "x", t0.AddMinutes(11)));
        Assert.Equal(3, center.Items.Count);
    }
}
