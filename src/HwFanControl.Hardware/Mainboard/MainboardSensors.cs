using System.Management;
using HwFanControl.Hardware.Cpu;
using LibreHardwareMonitor.Hardware;

namespace HwFanControl.Hardware.Mainboard;

/// <summary>Motherboard / SuperIO temperature and fan sensors for the Argus-style Mainboard tab.</summary>
public sealed class MainboardSensors
{
    private const int MaxTemps = 16;
    private const int MaxFans = 12;

    private readonly UpdateVisitor _visitor = new();
    private Computer? _computer;
    private readonly List<(string Name, ISensor Sensor)> _temps = new();
    private readonly List<(string Name, ISensor Sensor)> _fans = new();
    private string _boardName = "Motherboard";
    private string _chipName = "—";

    public void Bind(Computer computer)
    {
        _computer = computer;
        _temps.Clear();
        _fans.Clear();
        _chipName = "—";
        _boardName = ReadBoardName();

        computer.Accept(_visitor);
        foreach (IHardware hw in computer.Hardware)
            Visit(hw);

        if (_chipName == "—" && _temps.Count > 0)
            _chipName = "Super I/O";
    }

    public void Poll() => _computer?.Accept(_visitor);

    public MainboardSnapshot Capture()
    {
        var temps = new List<MainboardSensorReading>(_temps.Count);
        for (int i = 0; i < _temps.Count; i++)
        {
            var (name, sensor) = _temps[i];
            float v = sensor.Value ?? float.NaN;
            temps.Add(new MainboardSensorReading(name, v, ColorIndex: i));
        }

        var fans = new List<MainboardFanReading>(_fans.Count);
        for (int i = 0; i < _fans.Count; i++)
        {
            var (name, sensor) = _fans[i];
            float rpm = sensor.Value ?? float.NaN;
            fans.Add(new MainboardFanReading(name, rpm));
        }

        return new MainboardSnapshot
        {
            BoardName = _boardName,
            ChipName = _chipName,
            Temperatures = temps,
            Fans = fans,
        };
    }

    private void Visit(IHardware hw)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.Motherboard:
                // Prefer WMI product string; fall back to LHM motherboard name.
                if (_boardName is "Motherboard" or "—" or "")
                {
                    string? n = hw.Name?.Trim();
                    if (!string.IsNullOrWhiteSpace(n))
                        _boardName = n;
                }

                foreach (ISensor s in hw.Sensors)
                    BindSensor(s);
                break;

            case HardwareType.SuperIO:
                if (_chipName == "—")
                    _chipName = CleanChipName(hw.Name);

                foreach (ISensor s in hw.Sensors)
                    BindSensor(s);
                break;
        }

        foreach (IHardware sub in hw.SubHardware)
            Visit(sub);
    }

    private void BindSensor(ISensor sensor)
    {
        switch (sensor.SensorType)
        {
            case SensorType.Temperature when _temps.Count < MaxTemps:
            {
                string name = DisplayTempName(sensor.Name, _temps.Count);
                if (_temps.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    name = $"{name} ({_temps.Count + 1})";
                _temps.Add((name, sensor));
                break;
            }
            case SensorType.Fan when _fans.Count < MaxFans:
            {
                string name = DisplayFanName(sensor.Name, _fans.Count);
                _fans.Add((name, sensor));
                break;
            }
        }
    }

    /// <summary>
    /// Prefer Argus-like SuperIO labels when LHM exposes chip-register names;
    /// map "Temperature #N" → "Temp. N+1"; keep other LHM names cleaned.
    /// </summary>
    private static string DisplayTempName(string? raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return $"Temp. {index + 1}";

        string n = raw.Trim();

        // Already chip-register style (CPUTIN, AUXTIN0, SMBUSMST1, …).
        if (LooksLikeChipLabel(n))
            return n.ToUpperInvariant();

        // LHM often uses "Temperature #0" / "Temperature #1".
        if (n.StartsWith("Temperature", StringComparison.OrdinalIgnoreCase))
        {
            string digits = new string(n.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int num))
                return $"Temp. {num + 1}";
            return $"Temp. {index + 1}";
        }

        // Common LHM friendly names → Argus vocabulary (first match only via duplicate guard).
        if (n.Equals("CPU", StringComparison.OrdinalIgnoreCase))
            return "CPUTIN";
        if (n.Equals("System", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("SYS", StringComparison.OrdinalIgnoreCase))
            return "SYSTIN";

        return n.Length > 22 ? n[..22] + "…" : n;
    }

    private static string DisplayFanName(string? raw, int index)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return $"Fan #{index + 1}";

        string n = raw.Trim();
        if (n.StartsWith("Fan", StringComparison.OrdinalIgnoreCase))
        {
            string digits = new string(n.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int num))
                return $"Fan #{num + 1}";
        }

        return n.Length > 22 ? n[..22] + "…" : n;
    }

    private static bool LooksLikeChipLabel(string n)
    {
        // e.g. CPUTIN, SYSTIN, AUXTIN0, SMBUSMST1, PECI_0
        if (n.Length < 4 || n.Length > 16)
            return false;
        bool hasLetter = false, hasDigitOrUnderscore = false;
        foreach (char c in n)
        {
            if (char.IsLetter(c)) hasLetter = true;
            else if (char.IsDigit(c) || c is '_' or '.') hasDigitOrUnderscore = true;
            else return false;
        }

        return hasLetter && (hasDigitOrUnderscore || n.EndsWith("TIN", StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanChipName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "—";
        return name.Trim();
    }

    private static string ReadBoardName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                string mfr = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                string product = obj["Product"]?.ToString()?.Trim() ?? "";

                // Drop generic OEM placeholders.
                if (IsPlaceholder(mfr)) mfr = "";
                if (IsPlaceholder(product)) product = "";

                if (!string.IsNullOrEmpty(mfr) && !string.IsNullOrEmpty(product))
                {
                    // Avoid "ASRock ASRock X570 …" duplication.
                    if (product.StartsWith(mfr, StringComparison.OrdinalIgnoreCase))
                        return product;
                    return $"{mfr} {product}".Trim();
                }

                if (!string.IsNullOrEmpty(product))
                    return product;
                if (!string.IsNullOrEmpty(mfr))
                    return mfr;
            }
        }
        catch
        {
            // WMI optional
        }

        return "Motherboard";
    }

    private static bool IsPlaceholder(string s) =>
        string.IsNullOrWhiteSpace(s) ||
        s.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Default string", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("System Product Name", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase);
}

public sealed class MainboardSnapshot
{
    public string BoardName { get; init; } = "Motherboard";
    public string ChipName { get; init; } = "—";
    public List<MainboardSensorReading> Temperatures { get; init; } = new();
    public List<MainboardFanReading> Fans { get; init; } = new();
}

public readonly record struct MainboardSensorReading(string Name, float ValueCelsius, int ColorIndex);

public readonly record struct MainboardFanReading(string Name, float Rpm);
