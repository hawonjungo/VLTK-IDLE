using VLMulti.Core.Settings;
using Xunit;

namespace VLMulti.Tests;

public class GuildSettingsTests
{
    [Fact]
    public void Old_daily_donation_switch_becomes_twenty_thousand()
    {
        var s = new AppSettings { GuildDonateDaily = true };
        s.Normalize();
        Assert.Equal(20000, s.GuildDailyAmount);
        Assert.True(s.GuildDonateDaily);
    }

    [Fact]
    public void Daily_amount_is_capped_and_zero_means_off()
    {
        var s = new AppSettings { GuildDailyAmount = 50000 };
        s.Normalize();
        Assert.Equal(20000, s.GuildDailyAmount);
        s.GuildDailyAmount = 0;
        s.GuildDonateDaily = false;
        s.Normalize();
        Assert.False(s.GuildDonateDaily);
        Assert.Equal(0, s.GuildDailyAmount);
    }

    [Fact]
    public void Knb_transfer_is_off_by_default()
    {
        var s = new AppSettings();
        s.Normalize();
        Assert.False(s.KnbSendOn);
        Assert.True(s.KnbOpenBig);
        Assert.Equal(1000, s.KnbKeep);
    }
}
