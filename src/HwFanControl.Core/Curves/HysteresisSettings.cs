namespace HwFanControl.Core.Curves;

/// <summary>
/// Independent step-up / step-down delay timers to prevent erratic fan revving.
/// </summary>
public sealed class HysteresisSettings
{
    public TimeSpan StepUpDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan StepDownDelay { get; init; } = TimeSpan.FromSeconds(5);
}
