using HwFanControl.Core;
using HwFanControl.Core.Curves;
using HwFanControl.Core.Sensors;

namespace HwFanControl.Core.Tests;

public class FanLogicControllerTests
{
    [Fact]
    public void Manual_mode_outputs_fixed_pwm_with_floor()
    {
        var logic = new FanLogicController();
        var cfg = logic.GetChannel(0);
        cfg.Mode = FanChannelMode.Manual;
        cfg.ManualPwmPercent = 50;
        cfg.Safety.MinimumPwmPercent = 40;
        cfg.Safety.KickstartPwmPercent = 40;
        cfg.Safety.KickstartDurationSeconds = 0.01f;
        cfg.Safety.RateLimitUpPercentPerSecond = 1000;
        cfg.Safety.RateLimitDownPercentPerSecond = 1000;
        logic.SetChannelConfig(cfg);

        var buf = new TelemetryBuffer();
        float[] outPwm = new float[FanLogicController.MaxFanChannels];
        var t0 = DateTimeOffset.UtcNow;
        logic.Evaluate(buf, outPwm, t0);
        logic.Evaluate(buf, outPwm, t0.AddSeconds(1));

        Assert.True(outPwm[0] >= 40f);
        Assert.True(outPwm[0] <= 50f);
    }

    [Fact]
    public void Software_curve_follows_cpu_temp()
    {
        var logic = new FanLogicController();
        var cfg = logic.GetChannel(0);
        cfg.Mode = FanChannelMode.Software;
        cfg.TemperatureSource = TemperatureSourceKind.CpuPackage;
        cfg.Safety.MinimumPwmPercent = 0;
        cfg.Safety.KickstartPwmPercent = 0;
        cfg.Safety.KickstartDurationSeconds = 0;
        cfg.Safety.RateLimitUpPercentPerSecond = 1000;
        cfg.Safety.RateLimitDownPercentPerSecond = 1000;
        cfg.Safety.TemperatureHysteresisCelsius = 0;
        cfg.Controllers[0].Enabled = true;
        cfg.Controllers[0].Curve = new FanCurveDefinition
        {
            Kind = FanCurveKind.Linear,
            Points =
            [
                new FanCurvePointDto { TemperatureCelsius = 40, PwmPercent = 20 },
                new FanCurvePointDto { TemperatureCelsius = 60, PwmPercent = 80 },
            ],
        };
        logic.SetChannelConfig(cfg);

        var buf = new TelemetryBuffer();
        buf.Set(SensorSlot.CpuPackageTemp, 50);
        float[] outPwm = new float[FanLogicController.MaxFanChannels];
        logic.Evaluate(buf, outPwm, DateTimeOffset.UtcNow);

        Assert.Equal(50f, outPwm[0], precision: 1);
    }

    [Fact]
    public void Multi_controller_takes_max_pwm()
    {
        var logic = new FanLogicController();
        var cfg = logic.GetChannel(0);
        cfg.Mode = FanChannelMode.Software;
        cfg.TemperatureSource = TemperatureSourceKind.MultipleControllers;
        cfg.Safety.MinimumPwmPercent = 0;
        cfg.Safety.KickstartPwmPercent = 0;
        cfg.Safety.KickstartDurationSeconds = 0;
        cfg.Safety.RateLimitUpPercentPerSecond = 1000;
        cfg.Safety.RateLimitDownPercentPerSecond = 1000;
        cfg.Safety.TemperatureHysteresisCelsius = 0;

        cfg.Controllers[0].Enabled = true;
        cfg.Controllers[0].TemperatureSource = TemperatureSourceKind.CpuPackage;
        cfg.Controllers[0].Curve = Flat(30);
        cfg.Controllers[1].Enabled = true;
        cfg.Controllers[1].TemperatureSource = TemperatureSourceKind.GpuEdge;
        cfg.Controllers[1].Curve = Flat(70);
        cfg.Controllers[2].Enabled = false;
        cfg.Controllers[3].Enabled = false;
        logic.SetChannelConfig(cfg);

        var buf = new TelemetryBuffer();
        buf.Set(SensorSlot.CpuPackageTemp, 50);
        buf.Set(SensorSlot.GpuEdgeTemp, 50);
        float[] outPwm = new float[FanLogicController.MaxFanChannels];
        logic.Evaluate(buf, outPwm, DateTimeOffset.UtcNow);

        Assert.Equal(70f, outPwm[0], precision: 1);
    }

    private static FanCurveDefinition Flat(float pwm) => new()
    {
        Kind = FanCurveKind.Linear,
        Points =
        [
            new FanCurvePointDto { TemperatureCelsius = 0, PwmPercent = pwm },
            new FanCurvePointDto { TemperatureCelsius = 100, PwmPercent = pwm },
        ],
    };
}
