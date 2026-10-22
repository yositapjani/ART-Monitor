namespace HwFanControl.Core.Sensors;

/// <summary>
/// Stable slot indices resolved once at scan time. Hot-path code indexes by these constants
/// rather than allocating strings or dictionaries per poll.
/// </summary>
public enum SensorSlot : int
{
    CpuPackageTemp = 0,
    CpuCoreTempMax = 1,
    GpuEdgeTemp = 2,
    GpuJunctionTemp = 3,
    GpuHotspotTemp = 4,
    AmbientTemp = 5,

    FanRpm0 = 16,
    FanRpm1 = 17,
    FanRpm2 = 18,
    FanRpm3 = 19,
    FanRpm4 = 20,
    FanRpm5 = 21,
    FanRpm6 = 22,
    FanRpm7 = 23,

    FanPwm0 = 32,
    FanPwm1 = 33,
    FanPwm2 = 34,
    FanPwm3 = 35,
    FanPwm4 = 36,
    FanPwm5 = 37,
    FanPwm6 = 38,
    FanPwm7 = 39,

    /// <summary>Total fixed slots in a <see cref="TelemetryBuffer"/>.</summary>
    Capacity = 48,
}
