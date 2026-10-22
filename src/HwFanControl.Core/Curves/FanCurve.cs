using HwFanControl.Core.Sensors;

namespace HwFanControl.Core.Curves;

/// <summary>
/// Multi-point fan curve bound to a temperature source. Phase 2 evaluates Linear/Stepwise
/// interpolation and feeds hysteresis / max-mapping.
/// </summary>
public sealed class FanCurve
{
    public required string Name { get; init; }
    public required FanCurveKind Kind { get; init; }
    public required SensorSlot TemperatureSource { get; init; }
    public required FanCurvePoint[] Points { get; init; }

    /// <summary>Optional synthetic name when TemperatureSource is not a physical slot.</summary>
    public string? SyntheticSourceName { get; init; }
}
