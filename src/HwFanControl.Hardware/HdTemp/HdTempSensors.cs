using HwFanControl.Hardware.Cpu;
using LibreHardwareMonitor.Hardware;

namespace HwFanControl.Hardware.HdTemp;

/// <summary>Per-drive storage temperatures for the Argus-style HD Temp. tab.</summary>
public sealed class HdTempSensors
{
    private const int MaxDrives = 16;

    private readonly UpdateVisitor _visitor = new();
    private Computer? _computer;
    private readonly List<BoundDrive> _drives = new();

    public void Bind(Computer computer)
    {
        _computer = computer;
        _drives.Clear();

        computer.Accept(_visitor);
        foreach (IHardware hw in computer.Hardware)
            Visit(hw);
    }

    public void Poll() => _computer?.Accept(_visitor);

    public HdTempSnapshot Capture()
    {
        var drives = new List<HdTempDriveReading>(_drives.Count);
        foreach (var d in _drives)
        {
            float temp = d.Sensor?.Value ?? float.NaN;
            drives.Add(new HdTempDriveReading(
                d.Key,
                d.Name,
                d.DriveLetter,
                temp,
                d.Sensor is not null && !float.IsNaN(temp) && temp > 0.01f));
        }

        return new HdTempSnapshot { Drives = drives };
    }

    private void Visit(IHardware hw)
    {
        if (hw.HardwareType == HardwareType.Storage && _drives.Count < MaxDrives)
        {
            ISensor? temp = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
            string name = CleanName(hw.Name);
            string key = hw.Identifier?.ToString() ?? $"{name}|{_drives.Count}";
            // Avoid duplicate bindings when LHM exposes the same disk twice.
            if (!_drives.Any(d => d.Key == key))
                _drives.Add(new BoundDrive(key, name, DriveLetter: null, temp));
        }

        foreach (IHardware sub in hw.SubHardware)
            Visit(sub);
    }

    private static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Disk";
        return name.Trim();
    }

    public static string Normalize(string name) =>
        name.Trim().Replace("  ", " ", StringComparison.Ordinal);

    private readonly record struct BoundDrive(string Key, string Name, string? DriveLetter, ISensor? Sensor);
}

public sealed class HdTempSnapshot
{
    public List<HdTempDriveReading> Drives { get; init; } = new();
}

public readonly record struct HdTempDriveReading(
    string Key,
    string Name,
    string? DriveLetter,
    float TemperatureCelsius,
    bool HasTemperature);
