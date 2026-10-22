namespace HwFanControl.Core.Curves;

/// <summary>Per-channel Argus-style advanced options (min PWM, kickstart, rate limit).</summary>
public sealed class ChannelSafetyOptions
{
    /// <summary>Minimum safe PWM when fan is running (0% still allowed to stop). Default 40%.</summary>
    public float MinimumPwmPercent { get; set; } = 40f;

    public float KickstartPwmPercent { get; set; } = 60f;
    public float KickstartDurationSeconds { get; set; } = 2f;

    /// <summary>Max PWM increase per second.</summary>
    public float RateLimitUpPercentPerSecond { get; set; } = 40f;

    /// <summary>Max PWM decrease per second.</summary>
    public float RateLimitDownPercentPerSecond { get; set; } = 20f;

    /// <summary>Ignore temperature swings smaller than this (°C) before changing PWM direction.</summary>
    public float TemperatureHysteresisCelsius { get; set; } = 1.5f;
}
