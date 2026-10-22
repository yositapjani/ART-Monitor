using HwFanControl.Hardware.Cpu;
using LibreHardwareMonitor.Hardware;

namespace HwFanControl.Hardware.Smart;

/// <summary>Storage S.M.A.R.T. attributes and drive identity for the Argus-style SMART tab.</summary>
public sealed class SmartSensors
{
    private readonly UpdateVisitor _visitor = new();
    private readonly Dictionary<string, float> _sessionMaxTemps = new(StringComparer.Ordinal);
    private Computer? _computer;
    private readonly List<IHardware> _storage = new();

    public void Bind(Computer computer)
    {
        _computer = computer;
        _storage.Clear();

        computer.Accept(_visitor);
        foreach (IHardware hw in computer.Hardware)
            Visit(hw);
    }

    public void Poll() => _computer?.Accept(_visitor);

    public SmartSnapshot Capture()
    {
        var drives = new List<SmartDriveReading>();
        bool overallOk = true;

        foreach (IHardware hw in _storage)
        {
            try
            {
                // Refresh volatile SMART / space sensors on each capture.
                hw.Update();
            }
            catch
            {
                // continue with last values
            }

            SmartDriveReading? reading = SmartDriveInfo.FromHardware(hw, _sessionMaxTemps);
            if (reading is null)
                continue;

            drives.Add(reading);
            if (!reading.OverallHealthy)
                overallOk = false;
        }

        // Stable order: drive number then name.
        drives.Sort((a, b) =>
        {
            int cmp = a.DriveNumber.CompareTo(b.DriveNumber);
            return cmp != 0 ? cmp : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        if (drives.Count == 0)
            overallOk = true;

        return new SmartSnapshot
        {
            Drives = drives,
            OverallHealthy = overallOk,
        };
    }

    private void Visit(IHardware hw)
    {
        if (hw.HardwareType == HardwareType.Storage)
        {
            string key = hw.Identifier?.ToString() ?? hw.Name ?? "";
            if (!_storage.Any(s => string.Equals(s.Identifier?.ToString() ?? s.Name, key, StringComparison.Ordinal)))
                _storage.Add(hw);
        }

        foreach (IHardware sub in hw.SubHardware)
            Visit(sub);
    }
}
