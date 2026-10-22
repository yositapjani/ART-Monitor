using System.Diagnostics;
using System.Management;
using System.Windows.Media;

namespace HwFanControl.App.Services;

public enum SystemSubView
{
    CpuUsage,
    Ram,
    Network,
    DiskIo,
    SynthTemp,
}

/// <summary>Argus-style System tab: CPU Usage / RAM / Network / Disk I/O / Synth. Temp.</summary>
public sealed class SystemViewModel : ObservableObject, IDisposable
{
    public const int HistoryCapacity = 180;

    public static readonly Color LineColor = Color.FromRgb(0xB2, 0x22, 0x22); // firebrick

    private readonly Queue<float> _cpuHistory = new(HistoryCapacity);
    private readonly Queue<float> _ramHistory = new(HistoryCapacity);
    private readonly Queue<float> _netHistory = new(HistoryCapacity);
    private readonly Queue<float> _diskHistory = new(HistoryCapacity);

    private readonly List<PerformanceCounter> _netCounters = new();
    private PerformanceCounter? _diskCounter;
    private bool _countersReady;
    private bool _disposed;

    private SystemSubView _active = SystemSubView.CpuUsage;
    private float _cpuValue = float.NaN;
    private float _ramValue = float.NaN;
    private float _netMbps = float.NaN;
    private float _diskValue = float.NaN;
    private float _netAxisMax = 10f;
    private string _valueLabel = "CPU Usage (Total) —";
    private bool _isPlaceholder;

    public SystemSubView ActiveView
    {
        get => _active;
        private set => Set(ref _active, value);
    }

    public string ValueLabel
    {
        get => _valueLabel;
        private set => Set(ref _valueLabel, value);
    }

    public bool IsPlaceholder
    {
        get => _isPlaceholder;
        private set => Set(ref _isPlaceholder, value);
    }

    public string AxisUnit { get; private set; } = "%";
    public float AxisMin { get; private set; }
    public float AxisMax { get; private set; } = 100f;
    public float CurrentValue { get; private set; } = float.NaN;

    public void SelectView(SystemSubView view)
    {
        ActiveView = view;
        RefreshPresentation();
    }

    /// <summary>
    /// Push one poll sample. <paramref name="cpuLoadPercent"/> is total CPU %
    /// (core-load average from hardware readings, or NaN to fall back).
    /// </summary>
    public void Push(float cpuLoadPercent)
    {
        EnsureCounters();

        _cpuValue = float.IsNaN(cpuLoadPercent) ? float.NaN : Math.Clamp(cpuLoadPercent, 0f, 100f);
        _ramValue = ReadRamPercent();
        _netMbps = ReadNetworkMbps();
        _diskValue = ReadDiskPercent();

        Enqueue(_cpuHistory, _cpuValue);
        Enqueue(_ramHistory, _ramValue);
        Enqueue(_netHistory, _netMbps);
        Enqueue(_diskHistory, _diskValue);

        if (!float.IsNaN(_netMbps) && _netMbps > _netAxisMax * 0.85f)
            _netAxisMax = MathF.Ceiling(Math.Max(10f, _netMbps * 1.25f) / 5f) * 5f;

        RefreshPresentation();
    }

    public IReadOnlyList<float> GetActiveSeries() =>
        ActiveView switch
        {
            SystemSubView.CpuUsage => _cpuHistory.ToArray(),
            SystemSubView.Ram => _ramHistory.ToArray(),
            SystemSubView.Network => _netHistory.ToArray(),
            SystemSubView.DiskIo => _diskHistory.ToArray(),
            _ => Array.Empty<float>(),
        };

    private void RefreshPresentation()
    {
        IsPlaceholder = ActiveView == SystemSubView.SynthTemp;

        switch (ActiveView)
        {
            case SystemSubView.CpuUsage:
                AxisUnit = "%";
                AxisMin = 0;
                AxisMax = 100;
                CurrentValue = _cpuValue;
                ValueLabel = float.IsNaN(_cpuValue)
                    ? "CPU Usage (Total) —"
                    : $"CPU Usage (Total) {_cpuValue:0.0} %";
                break;

            case SystemSubView.Ram:
                AxisUnit = "%";
                AxisMin = 0;
                AxisMax = 100;
                CurrentValue = _ramValue;
                ValueLabel = float.IsNaN(_ramValue)
                    ? "RAM Usage —"
                    : $"RAM Usage {_ramValue:0.0} %";
                break;

            case SystemSubView.Network:
                AxisUnit = "Mbps";
                AxisMin = 0;
                AxisMax = Math.Max(10f, _netAxisMax);
                CurrentValue = _netMbps;
                ValueLabel = float.IsNaN(_netMbps)
                    ? "Network —"
                    : $"Network {_netMbps:0.0} Mbps";
                break;

            case SystemSubView.DiskIo:
                AxisUnit = "%";
                AxisMin = 0;
                AxisMax = 100;
                CurrentValue = _diskValue;
                ValueLabel = float.IsNaN(_diskValue)
                    ? "Disk I/O —"
                    : $"Disk I/O {_diskValue:0.0} %";
                break;

            default:
                AxisUnit = "°C";
                AxisMin = 0;
                AxisMax = 100;
                CurrentValue = float.NaN;
                ValueLabel = "Synth. Temp. — coming soon";
                break;
        }
    }

    private void EnsureCounters()
    {
        if (_countersReady || _disposed)
            return;

        _countersReady = true;
        try
        {
            // Discard first sample (always 0 for rate counters).
            _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total");
            _ = _diskCounter.NextValue();

            var cat = new PerformanceCounterCategory("Network Interface");
            foreach (string name in cat.GetInstanceNames())
            {
                if (name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("isatap", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Teredo", StringComparison.OrdinalIgnoreCase))
                    continue;

                var c = new PerformanceCounter("Network Interface", "Bytes Total/sec", name);
                _ = c.NextValue();
                _netCounters.Add(c);
            }
        }
        catch
        {
            DisposeCounters();
        }
    }

    private float ReadRamPercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                double totalKb = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                double freeKb = Convert.ToDouble(obj["FreePhysicalMemory"]);
                if (totalKb <= 0)
                    return float.NaN;
                return (float)((totalKb - freeKb) / totalKb * 100.0);
            }
        }
        catch
        {
            // ignore
        }

        return float.NaN;
    }

    private float ReadNetworkMbps()
    {
        if (_netCounters.Count == 0)
            return float.NaN;

        try
        {
            double bytesPerSec = 0;
            foreach (var c in _netCounters)
                bytesPerSec += c.NextValue();
            return (float)(bytesPerSec * 8.0 / 1_000_000.0);
        }
        catch
        {
            return float.NaN;
        }
    }

    private float ReadDiskPercent()
    {
        if (_diskCounter is null)
            return float.NaN;

        try
        {
            return Math.Clamp(_diskCounter.NextValue(), 0f, 100f);
        }
        catch
        {
            return float.NaN;
        }
    }

    private static void Enqueue(Queue<float> q, float value)
    {
        if (q.Count >= HistoryCapacity)
            q.Dequeue();
        q.Enqueue(value);
    }

    private void DisposeCounters()
    {
        _diskCounter?.Dispose();
        _diskCounter = null;
        foreach (var c in _netCounters)
            c.Dispose();
        _netCounters.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeCounters();
        GC.SuppressFinalize(this);
    }
}
