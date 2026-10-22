using HwFanControl.Core.Sensors;

namespace HwFanControl.Core.Synthetic;

/// <summary>
/// User-defined virtual sensor (e.g. Max(GPU Hotspot, CPU Package) or Delta T = GPU − Ambient).
/// Phase 2 evaluates these against a <see cref="TelemetryBuffer"/>.
/// </summary>
public sealed class SyntheticNode
{
    public required string Name { get; init; }
    public required SyntheticOperation Operation { get; init; }
    public SensorSlot Primary { get; init; }
    public SensorSlot Secondary { get; init; }
    public SensorSlot[]? Sources { get; init; }
}
