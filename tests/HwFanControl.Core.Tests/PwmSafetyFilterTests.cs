using HwFanControl.Core.Curves;
using HwFanControl.Core.Safety;

namespace HwFanControl.Core.Tests;

public class PwmSafetyFilterTests
{
    [Fact]
    public void Enforces_minimum_floor_when_running()
    {
        var filter = new PwmSafetyFilter();
        var opt = new ChannelSafetyOptions
        {
            MinimumPwmPercent = 40,
            KickstartPwmPercent = 40,
            KickstartDurationSeconds = 0.1f,
            RateLimitUpPercentPerSecond = 1000,
            RateLimitDownPercentPerSecond = 1000,
        };

        // Prime from stopped with kickstart done via high rate + second tick
        var t0 = DateTimeOffset.UtcNow;
        filter.Apply(0, 50, opt, t0, 1f);
        float pwm = filter.Apply(0, 20, opt, t0.AddSeconds(2), 1f);
        Assert.Equal(40f, pwm);
    }

    [Fact]
    public void Kickstarts_from_standstill()
    {
        var filter = new PwmSafetyFilter();
        var opt = new ChannelSafetyOptions
        {
            MinimumPwmPercent = 40,
            KickstartPwmPercent = 60,
            KickstartDurationSeconds = 2,
            RateLimitUpPercentPerSecond = 1000,
            RateLimitDownPercentPerSecond = 1000,
        };

        float pwm = filter.Apply(0, 40, opt, DateTimeOffset.UtcNow, 1f);
        Assert.Equal(60f, pwm);
    }

    [Fact]
    public void Zero_percent_allowed()
    {
        var filter = new PwmSafetyFilter();
        var opt = new ChannelSafetyOptions { RateLimitUpPercentPerSecond = 1000, RateLimitDownPercentPerSecond = 1000 };
        filter.Apply(0, 50, opt, DateTimeOffset.UtcNow, 1f);
        float pwm = filter.Apply(0, 0, opt, DateTimeOffset.UtcNow.AddSeconds(1), 1f);
        Assert.Equal(0f, pwm);
    }
}
