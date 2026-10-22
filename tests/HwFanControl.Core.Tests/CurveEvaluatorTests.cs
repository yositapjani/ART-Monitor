using HwFanControl.Core.Curves;

namespace HwFanControl.Core.Tests;

public class CurveEvaluatorTests
{
    [Fact]
    public void Linear_interpolates_between_points()
    {
        var curve = new FanCurveDefinition
        {
            Kind = FanCurveKind.Linear,
            Points =
            [
                new FanCurvePointDto { TemperatureCelsius = 40, PwmPercent = 20 },
                new FanCurvePointDto { TemperatureCelsius = 60, PwmPercent = 80 },
            ],
        };

        float pwm = CurveEvaluator.Evaluate(curve, 50);
        Assert.Equal(50f, pwm, precision: 1);
    }

    [Fact]
    public void Below_first_point_with_zero_pwm_keeps_fan_off()
    {
        var curve = FanCurvePresets.DefaultQuiet();
        float pwm = CurveEvaluator.Evaluate(curve, 20);
        Assert.Equal(0f, pwm);
    }

    [Fact]
    public void Stepwise_holds_lower_point()
    {
        var curve = new FanCurveDefinition
        {
            Kind = FanCurveKind.Stepwise,
            Points =
            [
                new FanCurvePointDto { TemperatureCelsius = 40, PwmPercent = 30 },
                new FanCurvePointDto { TemperatureCelsius = 60, PwmPercent = 90 },
            ],
        };

        Assert.Equal(30f, CurveEvaluator.Evaluate(curve, 55));
    }
}
