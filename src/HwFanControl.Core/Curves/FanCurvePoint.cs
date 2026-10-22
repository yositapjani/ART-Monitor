namespace HwFanControl.Core.Curves;

/// <summary>Maps temperature (°C) to PWM duty (0–100%).</summary>
public readonly struct FanCurvePoint
{
    public FanCurvePoint(float temperatureCelsius, float pwmPercent)
    {
        TemperatureCelsius = temperatureCelsius;
        PwmPercent = pwmPercent;
    }

    public float TemperatureCelsius { get; }
    public float PwmPercent { get; }
}
