namespace HwFanControl.Core.Curves;

/// <summary>Temperature inputs available for software fan control (Argus-style).</summary>
public enum TemperatureSourceKind
{
    CpuPackage = 0,
    CpuCoreMax = 1,
    GpuEdge = 2,
    GpuJunction = 3,
    GpuHotspot = 4,
    Ambient = 5,
    AvgCpuGpu = 10,
    MaxCpuGpu = 11,
    AvgCpuGpuFiltered = 12,
    MaxCpuGpuFiltered = 13,
    MultipleControllers = 20,
}
