namespace HwFanControl.Core.Curves;

/// <summary>Evaluates Temp→PWM characteristic curves (linear or stepwise).</summary>
public static class CurveEvaluator
{
    public static float Evaluate(FanCurveDefinition curve, float temperatureCelsius)
    {
        ArgumentNullException.ThrowIfNull(curve);
        var points = curve.Points;
        if (points is null || points.Count == 0)
            return 0f;

        // Ensure ordered by temperature without allocating when already sorted.
        EnsureSorted(points);

        var first = points[0];
        var last = points[^1];

        if (temperatureCelsius <= first.TemperatureCelsius)
            return ClampPwm(first.PwmPercent);

        if (temperatureCelsius >= last.TemperatureCelsius)
            return ClampPwm(last.PwmPercent);

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (temperatureCelsius > b.TemperatureCelsius)
                continue;

            if (curve.Kind == FanCurveKind.Stepwise)
                return ClampPwm(a.PwmPercent);

            float span = b.TemperatureCelsius - a.TemperatureCelsius;
            if (span <= 0.0001f)
                return ClampPwm(b.PwmPercent);

            float t = (temperatureCelsius - a.TemperatureCelsius) / span;
            float pwm = a.PwmPercent + t * (b.PwmPercent - a.PwmPercent);
            return ClampPwm(pwm);
        }

        return ClampPwm(last.PwmPercent);
    }

    private static void EnsureSorted(List<FanCurvePointDto> points)
    {
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].TemperatureCelsius >= points[i - 1].TemperatureCelsius)
                continue;

            points.Sort(static (x, y) => x.TemperatureCelsius.CompareTo(y.TemperatureCelsius));
            return;
        }
    }

    private static float ClampPwm(float pwm) => System.Math.Clamp(pwm, 0f, 100f);
}
