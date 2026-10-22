using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace HwFanControl.Hardware.Cpu;

/// <summary>
/// Live CPU snapshot for a Core Temp–style UI.
/// Poll on any thread; call <see cref="Capture"/> and apply on the UI thread.
/// </summary>
public sealed class CpuTelemetry
{
    private readonly object _gate = new();
    private readonly List<ISensor> _coreTempSensors = new();
    private readonly List<ISensor> _coreLoadSensors = new();
    private readonly List<(string Name, ISensor Sensor)> _extraTemps = new();
    private readonly UpdateVisitor _visitor = new();

    private ISensor? _packagePower;
    private ISensor? _tdie;
    private ISensor? _tjMax;
    private ISensor? _clock;
    private ISensor? _busClock;
    private ISensor? _voltage;
    private IHardware? _cpu;
    private Computer? _computer;

    private readonly StatAccumulator _packagePowerAcc = new();
    private readonly StatAccumulator _tdieAcc = new();
    private readonly StatAccumulator _tjMaxAcc = new();
    private readonly List<StatAccumulator> _ccdAcc = new();
    private readonly List<StatAccumulator> _coreTempAcc = new();
    private readonly float[] _coreLoad = new float[128];
    private bool _shareTdieAcrossCores;

    private string _model = "—";
    private string _platform = "—";
    private string _frequencyText = "—";
    private string _effectiveClockText = "—";
    private string _vidText = "—";
    private string _revisionText = "—";
    private string _cpuIdText = "—";
    private string _lithographyText = "—";
    private string _tdpText = "—";
    private int _coreCount;
    private int _threadCount = Environment.ProcessorCount;
    private string _status = "Belum di-scan";
    private int _sensorCount;
    private bool _isAmd;
    private bool _isIntel;

    public void Bind(Computer computer)
    {
        lock (_gate)
        {
            _computer = computer;
            _coreTempSensors.Clear();
            _coreLoadSensors.Clear();
            _extraTemps.Clear();
            _ccdAcc.Clear();
            _coreTempAcc.Clear();
            _packagePower = null;
            _tdie = null;
            _tjMax = null;
            _clock = null;
            _busClock = null;
            _voltage = null;
            _cpu = null;
            _shareTdieAcrossCores = false;
            Array.Clear(_coreLoad, 0, _coreLoad.Length);

            computer.Accept(_visitor);
            computer.Accept(_visitor);

            _cpu = FindCpu(computer);
            if (_cpu is null)
            {
                _model = "CPU tidak terdeteksi";
                _status = "HardwareType.Cpu tidak ditemukan — coba jalankan sebagai Administrator";
                return;
            }

            _model = string.IsNullOrWhiteSpace(_cpu.Name) ? "CPU" : _cpu.Name;
            _threadCount = Environment.ProcessorCount;
            _sensorCount = _cpu.Sensors.Count();

            var id = CpuIdentity.Read();
            _cpuIdText = id.CpuIdHex;
            _revisionText = id.Revision;
            _isAmd = id.IsAmd || Contains(_model, "AMD") || Contains(_model, "Ryzen");
            _isIntel = id.IsIntel || Contains(_model, "Intel");
            if (_isAmd)
                _platform = InferPlatform(_model);
            else if (_isIntel)
                _platform = "Intel desktop/mobile";

            var coreTemps = new List<(int Index, ISensor Sensor)>();
            var coreLoads = new List<(int Index, ISensor Sensor)>();

            foreach (ISensor sensor in _cpu.Sensors)
            {
                string name = sensor.Name ?? "";
                switch (sensor.SensorType)
                {
                    case SensorType.Temperature:
                        if (TryParseCoreIndex(name, out int ti))
                            coreTemps.Add((ti, sensor));
                        else if (Contains(name, "CCD"))
                            _extraTemps.Add((name, sensor));
                        else if (Contains(name, "Tdie") || Contains(name, "Tctl") ||
                                 Contains(name, "CPU Package") || name.Equals("Package", StringComparison.OrdinalIgnoreCase))
                            _tdie ??= sensor;
                        else if (Contains(name, "TjMax") || Contains(name, "Tj. Max"))
                            _tjMax ??= sensor;
                        break;
                    case SensorType.Load:
                        // Prefer "CPU Core #N" / "Core #N", skip Total/Max.
                        if (TryParseCoreIndex(name, out int li) &&
                            !Contains(name, "Total") &&
                            !Contains(name, "Max"))
                            coreLoads.Add((li, sensor));
                        break;
                    case SensorType.Power:
                        if (Contains(name, "Package") || Contains(name, "PPT") ||
                            name.Equals("CPU Cores", StringComparison.OrdinalIgnoreCase))
                            _packagePower ??= sensor;
                        break;
                    case SensorType.Clock:
                        // Prefer average / first effective clock.
                        if (Contains(name, "Average Effective") || Contains(name, "Cores (Average)"))
                            _clock ??= sensor;
                        else if (_clock is null && Contains(name, "Effective"))
                            _clock = sensor;
                        else if (_clock is null && Contains(name, "Core") && !Contains(name, "Bus"))
                            _clock = sensor;
                        else if (Contains(name, "Bus"))
                            _busClock ??= sensor;
                        break;
                    case SensorType.Voltage:
                        if (Contains(name, "VID") || Contains(name, "SVI2 TFN") && Contains(name, "Core"))
                            _voltage ??= sensor;
                        else if (_voltage is null && Contains(name, "Core"))
                            _voltage = sensor;
                        break;
                }
            }

            coreTemps.Sort((a, b) => a.Index.CompareTo(b.Index));
            coreLoads.Sort((a, b) => a.Index.CompareTo(b.Index));

            foreach (var (_, s) in coreTemps)
            {
                _coreTempSensors.Add(s);
                _coreTempAcc.Add(new StatAccumulator());
            }

            // AMD Zen often has ONLY "Core (Tctl/Tdie)" — no per-core temps.
            // Build rows from load sensors (physical cores = first half of logical).
            if (_coreTempSensors.Count == 0)
            {
                _shareTdieAcrossCores = true;
                int physical = Math.Max(1, _threadCount / 2);

                // Keep one load sensor per physical core (lowest N indices).
                var uniqueLoads = coreLoads
                    .GroupBy(x => x.Index)
                    .Select(g => g.OrderBy(x => x.Index).First())
                    .OrderBy(x => x.Index)
                    .Take(physical)
                    .ToList();

                if (uniqueLoads.Count == 0)
                {
                    // Still show physical core slots even without load sensors.
                    for (int i = 0; i < physical; i++)
                        _coreTempAcc.Add(new StatAccumulator());
                }
                else
                {
                    foreach (var (_, s) in uniqueLoads)
                    {
                        _coreLoadSensors.Add(s);
                        _coreTempAcc.Add(new StatAccumulator());
                    }
                }
            }
            else
            {
                foreach (var (_, s) in coreLoads)
                    _coreLoadSensors.Add(s);
            }

            foreach (var _ in _extraTemps)
                _ccdAcc.Add(new StatAccumulator());

            _coreCount = _coreTempAcc.Count > 0 ? _coreTempAcc.Count : Math.Max(1, _threadCount / 2);
            _platform = InferPlatform(_model);
            _lithographyText = InferLithography(_model);
            _tdpText = InferTdp(_model);
            _status =
                $"LHM sensors={_sensorCount}; cores={_coreCount}; " +
                $"tdie={(_tdie?.Name ?? "none")}; perCoreTemp={_coreTempSensors.Count}; " +
                $"shareTdie={_shareTdieAcrossCores}";
        }
    }

    public void Poll()
    {
        lock (_gate)
        {
            if (_computer is null || _cpu is null)
                return;

            _computer.Accept(_visitor);

            if (_packagePower?.Value is float power)
                _packagePowerAcc.Push(power);

            float? bestClock = null;
            foreach (ISensor sensor in _cpu.Sensors)
            {
                if (sensor.SensorType != SensorType.Clock || sensor.Value is not float mhz || mhz <= 1f)
                    continue;
                string n = sensor.Name ?? "";
                if (Contains(n, "Average Effective") || Contains(n, "Cores (Average)"))
                {
                    bestClock = mhz;
                    break;
                }

                if (Contains(n, "Effective") && mhz > (bestClock ?? 0))
                    bestClock = mhz;
                else if (bestClock is null && Contains(n, "Core") && !Contains(n, "Bus"))
                    bestClock = mhz;
            }

            if (bestClock is float mhzVal)
            {
                _frequencyText = $"{mhzVal:0.00} MHz";
                _effectiveClockText = $"{mhzVal:0.00} MHz";
            }

            if (_voltage?.Value is float vid && vid > 0.01f)
                _vidText = $"{vid:0.0000} V";

            if (_tdie?.Value is float tdie)
                _tdieAcc.Push(tdie);

            if (_tjMax?.Value is float tjMaxVal && tjMaxVal > 1f)
                _tjMaxAcc.Push(tjMaxVal);

            for (int i = 0; i < _extraTemps.Count; i++)
            {
                if (_extraTemps[i].Sensor.Value is float v)
                    _ccdAcc[i].Push(v);
            }

            if (_shareTdieAcrossCores)
            {
                float shared = _tdie?.Value ?? float.NaN;
                for (int i = 0; i < _coreTempAcc.Count; i++)
                {
                    if (!float.IsNaN(shared))
                        _coreTempAcc[i].Push(shared);
                    _coreLoad[i] = i < _coreLoadSensors.Count && _coreLoadSensors[i].Value is float load
                        ? load
                        : float.NaN;
                }
            }
            else
            {
                for (int i = 0; i < _coreTempSensors.Count; i++)
                {
                    if (_coreTempSensors[i].Value is float temp)
                        _coreTempAcc[i].Push(temp);
                    _coreLoad[i] = i < _coreLoadSensors.Count && _coreLoadSensors[i].Value is float load
                        ? load
                        : float.NaN;
                }
            }

            float liveTdie = _tdieAcc.Current;
            bool tempDead = float.IsNaN(liveTdie) || liveTdie <= 0.01f;
            if (tempDead)
            {
                _status = PawnIoStatus.GetGuidance(elevated: true, cpuTempLooksDead: true);
            }
            else if (_cpu is not null)
            {
                float tjNow = EffectiveTjMax();
                string tjSeg = float.IsNaN(tjNow) ? "TjMax=?" : $"TjMax={tjNow:0}°C dT={tjNow - liveTdie:0}°C";
                _status =
                    $"OK | {_model} | Tdie={Format(liveTdie, "°C")} | {tjSeg} | " +
                    $"cores={_coreCount} threads={_threadCount} | {PawnIoStatus.GetGuidance(true, false)}";
            }
        }
    }

    public CpuUiSnapshot Capture()
    {
        lock (_gate)
        {
            var rows = new List<CpuUiRow>(8 + _coreTempAcc.Count + _extraTemps.Count)
            {
                CpuUiRow.FromPower("Power", _packagePowerAcc),
                BuildTjMaxRow(),
                CpuUiRow.FromTemp("Tdie", _tdieAcc),
            };

            var distRow = BuildDistanceRow();
            if (distRow is not null)
                rows.Add(distRow.Value);

            for (int i = 0; i < _extraTemps.Count; i++)
                rows.Add(CpuUiRow.FromTemp(_extraTemps[i].Name, _ccdAcc[i]));

            for (int i = 0; i < _coreTempAcc.Count; i++)
            {
                string load = float.IsNaN(_coreLoad[i]) ? "—" : $"{_coreLoad[i]:0}%";
                var row = CpuUiRow.FromTemp($"Core #{i}", _coreTempAcc[i]) with { LoadText = load };
                rows.Add(row);
            }

            return new CpuUiSnapshot
            {
                Model = _model,
                Platform = _platform,
                Frequency = _frequencyText,
                EffectiveClock = _effectiveClockText,
                Vid = _vidText,
                Revision = _revisionText,
                CpuId = _cpuIdText,
                Lithography = _lithographyText,
                Tdp = _tdpText,
                CoreCount = _coreCount,
                ThreadCount = _threadCount,
                ProcessorLabel = "Processor #0",
                Status = _status,
                SensorCount = _sensorCount,
                IsAmd = _isAmd,
                IsIntel = _isIntel,
                Rows = rows,
            };
        }
    }

    private static IHardware? FindCpu(Computer computer)
    {
        foreach (IHardware hw in computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu)
                return hw;
            foreach (IHardware sub in hw.SubHardware)
            {
                if (sub.HardwareType == HardwareType.Cpu)
                    return sub;
            }
        }

        return null;
    }

    private static bool TryParseCoreIndex(string name, out int index)
    {
        index = -1;
        var m = Regex.Match(name, @"Core\s*#?\s*(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success)
            return false;
        return int.TryParse(m.Groups[1].Value, out index);
    }

    private static bool Contains(string hay, string needle) =>
        hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>LHM returns 0 when PawnIO/MSR is unavailable — treat as missing.</summary>
    internal static string FormatTemp(float v) =>
        float.IsNaN(v) || v <= 0.01f ? "—" : $"{v:0}°C";

    internal static string FormatPower(float v) =>
        float.IsNaN(v) || v <= 0.01f ? "—" : $"{v:0.0}W";

    private static string Format(float v, string unit) =>
        float.IsNaN(v) ? "—" : $"{v:0.0}{unit}";

    /// <summary>Sensor TjMax wins; otherwise a conservative per-model fallback (NaN = unknown).</summary>
    private float EffectiveTjMax()
    {
        float sensor = _tjMaxAcc.Current;
        if (!float.IsNaN(sensor) && sensor > 1f)
            return sensor;
        return InferTjMax(_model);
    }

    private CpuUiRow BuildTjMaxRow()
    {
        float tj = EffectiveTjMax();
        return new CpuUiRow("Tj. Max", FormatTemp(tj), "â€”", "â€”", "â€”", "â€”");
    }

    private CpuUiRow? BuildDistanceRow()
    {
        float tj = EffectiveTjMax();
        if (float.IsNaN(tj) || float.IsNaN(_tdieAcc.Current))
            return null;
        return new CpuUiRow(
            "Jarak ke TjMax",
            FormatTemp(tj - _tdieAcc.Current),
            FormatTemp(tj - _tdieAcc.Max),
            FormatTemp(tj - _tdieAcc.Min),
            "â€”",
            "â€”");
    }

    private static float InferTjMax(string model)
    {
        // Cezanne APUs and Ryzen 7000/9000 (Raphael/Granite Ridge): 95C. Vermeer (Ryzen 5000 desktop): 90C.
        if (Contains(model, "5600G") || Contains(model, "5700G") ||
            Contains(model, "7950") || Contains(model, "7900") || Contains(model, "7700") || Contains(model, "7600") ||
            Contains(model, "9950") || Contains(model, "9900") || Contains(model, "9700") || Contains(model, "9600"))
            return 95f;
        if (Contains(model, "5600") || Contains(model, "5700X") || Contains(model, "5800") ||
            Contains(model, "5900") || Contains(model, "5950"))
            return 90f;
        return float.NaN;
    }

    private static string InferPlatform(string model)
    {
        if (Contains(model, "Ryzen") || Contains(model, "Granite") || Contains(model, "Raphael") ||
            Contains(model, "Vermeer") || Contains(model, "Zen") || Contains(model, "5600"))
            return "AMD Socket AM4 / AM5";
        if (Contains(model, "Intel"))
            return "Intel desktop/mobile";
        return "—";
    }

    private static string InferLithography(string model)
    {
        if (Contains(model, "9950") || Contains(model, "9900") || Contains(model, "Granite"))
            return "4 nm";
        if (Contains(model, "7950") || Contains(model, "7900") || Contains(model, "5600") || Contains(model, "5800"))
            return "7 nm";
        return "—";
    }

    private static string InferTdp(string model)
    {
        if (Contains(model, "9950X")) return "170.0 Watts";
        if (Contains(model, "9900X")) return "120.0 Watts";
        if (Contains(model, "5600")) return "65.0 Watts";
        return "—";
    }
}

public sealed class CpuUiSnapshot
{
    public string Model { get; init; } = "—";
    public string Platform { get; init; } = "—";
    public string Frequency { get; init; } = "—";
    public string EffectiveClock { get; init; } = "—";
    public string Vid { get; init; } = "—";
    public string Revision { get; init; } = "—";
    public string CpuId { get; init; } = "—";
    public string Lithography { get; init; } = "—";
    public string Tdp { get; init; } = "—";
    public int CoreCount { get; init; }
    public int ThreadCount { get; init; }
    public string ProcessorLabel { get; init; } = "Processor #0";
    public string Status { get; init; } = "";
    public int SensorCount { get; init; }
    public bool IsAmd { get; init; }
    public bool IsIntel { get; init; }
    public List<CpuUiRow> Rows { get; init; } = new();
}

public readonly record struct CpuUiRow(
    string Label,
    string CurrentText,
    string MinText,
    string MaxText,
    string AvgText,
    string LoadText)
{
    public static CpuUiRow FromTemp(string label, StatAccumulator acc) =>
        new(label,
            CpuTelemetry.FormatTemp(acc.Current),
            CpuTelemetry.FormatTemp(acc.Min),
            CpuTelemetry.FormatTemp(acc.Max),
            CpuTelemetry.FormatTemp(acc.Average),
            "—");

    public static CpuUiRow FromPower(string label, StatAccumulator acc) =>
        new(label,
            CpuTelemetry.FormatPower(acc.Current),
            CpuTelemetry.FormatPower(acc.Min),
            CpuTelemetry.FormatPower(acc.Max),
            CpuTelemetry.FormatPower(acc.Average),
            "—");
}

public sealed class StatAccumulator
{
    private double _sum;
    private int _count;

    public float Current { get; private set; } = float.NaN;
    public float Min { get; private set; } = float.NaN;
    public float Max { get; private set; } = float.NaN;
    public float Average => _count == 0 ? float.NaN : (float)(_sum / _count);

    public void Push(float value)
    {
        Current = value;
        if (float.IsNaN(Min) || value < Min)
            Min = value;
        if (float.IsNaN(Max) || value > Max)
            Max = value;
        _sum += value;
        _count++;
    }
}
