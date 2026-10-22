using HwFanControl.Core.Sensors;

namespace HwFanControl.Hardware.Sensors;

/// <summary>
/// Hardware-layer view over a reusable <see cref="TelemetryBuffer"/>.
/// Prefer mutating via <see cref="Buffer"/> rather than allocating new snapshots each tick.
/// </summary>
public struct SensorSnapshot
{
    public TelemetryBuffer Buffer { get; set; }
    public bool IsValid => Buffer is { IsStale: false };
    public long TimestampUtcTicks => Buffer?.TimestampUtcTicks ?? 0;

    public static SensorSnapshot Create() => new() { Buffer = new TelemetryBuffer() };
}
