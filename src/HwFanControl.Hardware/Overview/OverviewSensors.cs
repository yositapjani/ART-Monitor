using HwFanControl.Hardware.Cpu;
using LibreHardwareMonitor.Hardware;

namespace HwFanControl.Hardware.Overview;

/// <summary>GPU + storage temperatures for the Argus-style Overview tab.</summary>
public sealed class OverviewSensors
{
    private readonly UpdateVisitor _visitor = new();
    private Computer? _computer;
    private ISensor? _gpuTemp;
    private ISensor? _gpuTemp2;
    private ISensor? _gpuFanRpm;
    private ISensor? _gpuFanPct;
    private ISensor? _gpuMemUsed;
    private ISensor? _gpuMemTotal;
    private ISensor? _gpuLoad;
    private string _gpuName = "GPU";
    private readonly List<(string Name, ISensor Sensor)> _disks = new();

    private readonly GpuStat _edgeAcc = new();
    private readonly GpuStat _hotAcc = new();
    private readonly GpuStat _fanAcc = new();
    private readonly GpuStat _memAcc = new();

    private long _netPrevRx;
    private long _netPrevTx;
    private long _netPrevTick;
    private bool _netInit;
    private long _sessRx;
    private long _sessTx;

    public void Bind(Computer computer)
    {
        _computer = computer;
        _gpuTemp = null;
        _gpuTemp2 = null;
        _gpuFanRpm = null;
        _gpuFanPct = null;
        _gpuMemUsed = null;
        _gpuMemTotal = null;
        _gpuLoad = null;
        _disks.Clear();
        _gpuName = "GPU";
        _edgeAcc.Reset();
        _hotAcc.Reset();
        _fanAcc.Reset();
        _memAcc.Reset();

        computer.Accept(_visitor);
        foreach (IHardware hw in computer.Hardware)
            Visit(hw);
    }

    public void Poll()
    {
        _computer?.Accept(_visitor);
    }

    /// <summary>Total throughput over all up, non-loopback interfaces, in MB/s.</summary>
    private (float In, float Out) SampleNetworkMbps()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;
                var st = ni.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch
        {
            return (0f, 0f);
        }

        long now = Environment.TickCount64;
        float inMbps = 0f, outMbps = 0f;
        if (_netInit && now > _netPrevTick)
        {
            double dt = (now - _netPrevTick) / 1000.0;
            inMbps = (float)System.Math.Max(0.0, (rx - _netPrevRx) / dt / 1048576.0);
            outMbps = (float)System.Math.Max(0.0, (tx - _netPrevTx) / dt / 1048576.0);
        }

        if (_netInit)
        {
            if (rx > _netPrevRx) _sessRx += rx - _netPrevRx;
            if (tx > _netPrevTx) _sessTx += tx - _netPrevTx;
        }

        _netInit = true;
        _netPrevRx = rx;
        _netPrevTx = tx;
        _netPrevTick = now;
        return (inMbps, outMbps);
    }

    public OverviewSnapshot Capture(IReadOnlyList<float> coreTempsCelsius)
    {
        var pairs = new List<OverviewPairRow>();

        // Core pairs: 1/2, 3/4, ...
        for (int i = 0; i < coreTempsCelsius.Count; i += 2)
        {
            float a = coreTempsCelsius[i];
            float b = i + 1 < coreTempsCelsius.Count ? coreTempsCelsius[i + 1] : float.NaN;
            pairs.Add(new OverviewPairRow($"Core {i + 1}/{i + 2}", a, b));
        }

        float g1 = _gpuTemp?.Value ?? float.NaN;
        float g2 = _gpuTemp2?.Value ?? float.NaN;
        pairs.Add(new OverviewPairRow("GPU 1/2", g1, g2));

        for (int i = 0; i < _disks.Count; i += 2)
        {
            float a = _disks[i].Sensor.Value ?? float.NaN;
            float b = i + 1 < _disks.Count ? (_disks[i + 1].Sensor.Value ?? float.NaN) : float.NaN;
            int n1 = i + 1, n2 = i + 2;
            pairs.Add(new OverviewPairRow($"HDD {n1}/{n2}", a, b));
        }

        var series = new List<OverviewSeries>
        {
            new("CPU Cores", AverageValid(coreTempsCelsius), ColorIndex: 0),
        };

        for (int i = 0; i < coreTempsCelsius.Count; i++)
            series.Add(new($"Core #{i}", coreTempsCelsius[i], ColorIndex: 3 + (i % 6)));

        series.Add(new($"GPU 1 ({_gpuName})", g1, ColorIndex: 1));

        for (int i = 0; i < _disks.Count; i++)
            series.Add(new(_disks[i].Name, _disks[i].Sensor.Value ?? float.NaN, ColorIndex: 2 + (i % 8)));

        var (netIn, netOut) = SampleNetworkMbps();
        series.Add(new("Network In", netIn, ColorIndex: 9));
        series.Add(new("Network Out", netOut, ColorIndex: 10));

        // GPU readings table (Core Temp-style stats: current / min / max / avg / load).
        _edgeAcc.Add(g1, allowZero: false);
        _hotAcc.Add(g2, allowZero: false);
        float fanRpm = _gpuFanRpm?.Value ?? float.NaN;
        _fanAcc.Add(fanRpm, allowZero: true);
        float fanPct = _gpuFanPct?.Value ?? float.NaN;
        float gpuLoad = _gpuLoad?.Value ?? float.NaN;

        float memPct = float.NaN;
        if (_gpuMemUsed?.Value is float muRaw)
        {
            float totRaw = _gpuMemTotal?.Value ?? float.NaN;
            // LHM reports VRAM either in MB (SmallData / AMD) or GB (Data / Nvidia).
            // Decide the scale from the total when known, else from the used value.
            float scale;
            if (!float.IsNaN(totRaw) && totRaw > 0f)
                scale = totRaw > 100f ? 1024f : 1f;
            else
                scale = muRaw > 100f ? 1024f : 1f;
            float usedGb = muRaw / scale;
            _memAcc.Add(usedGb, allowZero: true);
            if (!float.IsNaN(totRaw) && totRaw > 0f)
                memPct = usedGb / (totRaw / scale) * 100f;
        }

        var gpuReadings = new List<OverviewReadingRow>();
        if (_gpuTemp is not null)
            gpuReadings.Add(new("Edge",
                FormatStatTemp(_edgeAcc.Current), FormatStatTemp(_edgeAcc.Min),
                FormatStatTemp(_edgeAcc.Max), FormatStatTemp(_edgeAcc.Avg),
                FormatPct(gpuLoad)));
        if (_gpuTemp2 is not null)
            gpuReadings.Add(new("Hot Spot",
                FormatStatTemp(_hotAcc.Current), FormatStatTemp(_hotAcc.Min),
                FormatStatTemp(_hotAcc.Max), FormatStatTemp(_hotAcc.Avg),
                FormatPct(gpuLoad)));
        if (_gpuFanRpm is not null || _gpuFanPct is not null)
            gpuReadings.Add(new("Fan (RPM)",
                FormatStatRpm(_fanAcc.Current), FormatStatRpm(_fanAcc.Min),
                FormatStatRpm(_fanAcc.Max), FormatStatRpm(_fanAcc.Avg),
                FormatPct(fanPct)));
        if (_gpuMemUsed is not null)
            gpuReadings.Add(new("Memory",
                FormatStatGb(_memAcc.Current), FormatStatGb(_memAcc.Min),
                FormatStatGb(_memAcc.Max), FormatStatGb(_memAcc.Avg),
                FormatPct(memPct)));

        var diskRows = new List<OverviewValueRow>();
        foreach (var d in _disks)
            diskRows.Add(new(d.Name, d.Sensor.Value ?? float.NaN));

        return new OverviewSnapshot
        {
            Pairs = pairs,
            Series = series,
            GpuName = _gpuName,
            GpuReadings = gpuReadings,
            DiskRows = diskRows,
            NetSessionRxBytes = _sessRx,
            NetSessionTxBytes = _sessTx,
        };
    }

    private void Visit(IHardware hw)
    {
        switch (hw.HardwareType)
        {
            case HardwareType.GpuAmd:
            case HardwareType.GpuNvidia:
            case HardwareType.GpuIntel:
                _gpuName = ShortGpuName(hw.Name);
                ISensor? junction = null;
                foreach (ISensor s in hw.Sensors)
                {
                    string n = s.Name ?? "";
                    switch (s.SensorType)
                    {
                        case SensorType.Temperature:
                            if (n.Contains("Hot", StringComparison.OrdinalIgnoreCase))
                                _gpuTemp2 ??= s;
                            else if (n.Contains("Junction", StringComparison.OrdinalIgnoreCase))
                                junction ??= s;
                            else if (n.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                     n.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                                _gpuTemp ??= s;
                            break;
                        case SensorType.Fan:
                            _gpuFanRpm ??= s;
                            break;
                        case SensorType.Control:
                            if (n.Contains("Fan", StringComparison.OrdinalIgnoreCase))
                                _gpuFanPct ??= s;
                            break;
                        case SensorType.Load:
                            if (n.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                _gpuLoad ??= s;
                            break;
                        case SensorType.Data:
                        case SensorType.SmallData:
                            if (n.Contains("Memory", StringComparison.OrdinalIgnoreCase) &&
                                n.Contains("Used", StringComparison.OrdinalIgnoreCase))
                                _gpuMemUsed ??= s;
                            else if (n.Contains("Memory", StringComparison.OrdinalIgnoreCase) &&
                                     n.Contains("Total", StringComparison.OrdinalIgnoreCase))
                                _gpuMemTotal ??= s;
                            break;
                    }
                }

                _gpuTemp2 ??= junction;
                _gpuTemp ??= hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature);
                _gpuLoad ??= hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load);
                break;

            case HardwareType.Storage:
                ISensor? primary = null;
                foreach (ISensor s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Temperature)
                        continue;
                    primary ??= s;
                    if (string.Equals(s.Name, "Temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        primary = s;
                        break;
                    }
                }

                if (primary is not null && _disks.Count < 12
                    && !_disks.Any(d => ReferenceEquals(d.Sensor, primary)))
                    _disks.Add((UniqueDiskName(ShortDiskName(hw.Name)), primary));
                break;
        }

        foreach (IHardware sub in hw.SubHardware)
            Visit(sub);
    }

    private static string FormatStatTemp(float v) =>
        float.IsNaN(v) ? "---" : $"{v:0}°C";

    private static string FormatStatRpm(float v) =>
        float.IsNaN(v) ? "---" : $"{v:0}";

    private static string FormatStatGb(float v) =>
        float.IsNaN(v) ? "---" : $"{v:0.0} GB";

    private static string FormatPct(float v) =>
        float.IsNaN(v) ? "" : $"{v:0}%";

    private static string ShortGpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "GPU";
        return name.Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase)
                   .Trim();
    }

    private string UniqueDiskName(string baseName)
    {
        int n = 1;
        string candidate = baseName;
        while (_disks.Any(d => string.Equals(d.Name, candidate, StringComparison.Ordinal)))
        {
            n++;
            candidate = $"{baseName} #{n}";
        }

        return candidate;
    }

    private static string ShortDiskName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Disk";
        return name.Length > 28 ? name[..28] + "." : name;
    }

    private static float AverageValid(IReadOnlyList<float> values)
    {
        float sum = 0;
        int n = 0;
        foreach (float v in values)
        {
            if (float.IsNaN(v) || v <= 0.01f)
                continue;
            sum += v;
            n++;
        }

        return n == 0 ? float.NaN : sum / n;
    }

    /// <summary>Running current/min/max/average for one GPU metric.</summary>
    private sealed class GpuStat
    {
        public float Current = float.NaN;
        public float Min = float.NaN;
        public float Max = float.NaN;
        private double _sum;
        private int _count;

        public float Avg => _count == 0 ? float.NaN : (float)(_sum / _count);

        public void Reset()
        {
            Current = float.NaN;
            Min = float.NaN;
            Max = float.NaN;
            _sum = 0;
            _count = 0;
        }

        public void Add(float v, bool allowZero)
        {
            if (float.IsNaN(v) || (!allowZero && v <= 0.01f))
                return;
            Current = v;
            if (float.IsNaN(Min) || v < Min) Min = v;
            if (float.IsNaN(Max) || v > Max) Max = v;
            _sum += v;
            _count++;
        }
    }
}

public sealed class OverviewSnapshot
{
    public List<OverviewPairRow> Pairs { get; init; } = new();
    public List<OverviewSeries> Series { get; init; } = new();
    public string GpuName { get; init; } = "GPU";
    public List<OverviewReadingRow> GpuReadings { get; init; } = new();
    public List<OverviewValueRow> DiskRows { get; init; } = new();
    public long NetSessionRxBytes { get; init; }
    public long NetSessionTxBytes { get; init; }
}

public readonly record struct OverviewPairRow(string Label, float Left, float Right);

public readonly record struct OverviewSeries(string Name, float ValueCelsius, int ColorIndex);

public readonly record struct OverviewValueRow(string Label, float ValueCelsius);

public readonly record struct OverviewTextRow(string Label, string Value);

public readonly record struct OverviewReadingRow(string Label, string CurrentText, string MinText, string MaxText, string AvgText, string LoadText);
