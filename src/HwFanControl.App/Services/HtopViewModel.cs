using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HwFanControl.App.Services;

/// <summary>
/// Linux htop-style monitor: per-thread CPU meters, Mem/Swp meters and a
/// live process table (CPU% / MEM% / RES / TIME+), filterable and sortable.
/// Refresh() is called once per second while the HTOP tab is selected.
/// </summary>
public sealed class HtopViewModel : ObservableObject
{
    private const int BarWidth = 26;

    private PerformanceCounter[]? _threadCounters;
    private PerformanceCounter? _totalCounter;
    private readonly Dictionary<(int Pid, long StartTicks), (TimeSpan Cpu, DateTime When)> _prev = new();
    private readonly Dictionary<(int Pid, long StartTicks), string> _owners = new();
    private readonly Dictionary<(int Pid, long StartTicks), HtopProcessRow> _rows = new();

    public ObservableCollection<HtopMeter> CpuMetersLeft { get; } = new();
    public ObservableCollection<HtopMeter> CpuMetersRight { get; } = new();
    public HtopMeter MemMeter { get; } = new("Mem");
    public HtopMeter SwpMeter { get; } = new("Swp");
    public ObservableCollection<HtopProcessRow> Processes { get; } = new();

    private string _headerText = "";
    public string HeaderText { get => _headerText; private set => Set(ref _headerText, value); }

    public string Filter { get; set; } = "";

    private bool _sortByCpu = true;
    private string _sortLabel = "F6 Sort: CPU%";
    public bool SortByCpu
    {
        get => _sortByCpu;
        set
        {
            _sortByCpu = value;
            SortLabel = value ? "F6 Sort: CPU%" : "F6 Sort: MEM%";
        }
    }
    public string SortLabel { get => _sortLabel; private set => Set(ref _sortLabel, value); }

    public void Refresh()
    {
        EnsureCounters();
        UpdateMeters();
        UpdateProcesses();
    }

    private void EnsureCounters()
    {
        if (_threadCounters is not null)
            return;
        int n = Environment.ProcessorCount;
        _threadCounters = new PerformanceCounter[n];
        for (int i = 0; i < n; i++)
        {
            var c = new PerformanceCounter("Processor", "% Processor Time", i.ToString());
            _ = c.NextValue(); // prime (first sample is always 0)
            _threadCounters[i] = c;
            var meter = new HtopMeter($"CPU{i}");
            if (i < (n + 1) / 2)
                CpuMetersLeft.Add(meter);
            else
                CpuMetersRight.Add(meter);
        }

        _totalCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ = _totalCounter.NextValue();
    }

    private void UpdateMeters()
    {
        if (_threadCounters is null)
            return;
        int n = _threadCounters.Length;
        for (int i = 0; i < n; i++)
        {
            float v;
            try { v = _threadCounters[i].NextValue(); }
            catch { v = 0; }
            var meter = i < (n + 1) / 2 ? CpuMetersLeft[i] : CpuMetersRight[i - (n + 1) / 2];
            meter.SetValue(v, $"{v:0.0}%");
        }

        // Memory + pagefile via WMI (same source as the status bar).
        try
        {
            using var os = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in os.Get())
            {
                double totalGb = Convert.ToDouble(obj["TotalVisibleMemorySize"]) / (1024.0 * 1024.0);
                double freeGb = Convert.ToDouble(obj["FreePhysicalMemory"]) / (1024.0 * 1024.0);
                double usedGb = totalGb - freeGb;
                double pct = totalGb > 0 ? usedGb / totalGb * 100.0 : 0;
                MemMeter.SetValue(pct, $"{usedGb:0.0}G/{totalGb:0.0}G");
                break;
            }
        }
        catch { /* meters keep last values */ }

        try
        {
            double totalMb = 0, usedMb = 0;
            using var pf = new ManagementObjectSearcher(
                "SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
            foreach (ManagementObject obj in pf.Get())
            {
                totalMb += Convert.ToDouble(obj["AllocatedBaseSize"]);
                usedMb += Convert.ToDouble(obj["CurrentUsage"]);
            }

            double pct = totalMb > 0 ? usedMb / totalMb * 100.0 : 0;
            SwpMeter.SetValue(pct, $"{usedMb / 1024.0:0.0}G/{totalMb / 1024.0:0.0}G");
        }
        catch { /* ignore */ }
    }

    private void UpdateProcesses()
    {
        var now = DateTime.UtcNow;
        int logical = Environment.ProcessorCount;
        var data = new List<HtopRowData>();
        var alive = new HashSet<(int, long)>();
        long totalRam = TotalRamBytes();
        int threadTotal = 0;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    int pid = p.Id;
                    long startTicks = 0;
                    TimeSpan cpu = TimeSpan.Zero;
                    try { startTicks = p.StartTime.ToUniversalTime().Ticks; } catch { }
                    try { cpu = p.TotalProcessorTime; } catch { }
                    var key = (pid, startTicks);
                    alive.Add(key);

                    double cpuPct = 0;
                    if (_prev.TryGetValue(key, out var prev))
                    {
                        double dt = (now - prev.When).TotalSeconds;
                        if (dt > 0.05)
                            cpuPct = (cpu - prev.Cpu).TotalSeconds / dt / logical * 100.0;
                    }
                    _prev[key] = (cpu, now);

                    long ws = 0;
                    int threads = 0, pri = 0;
                    try { ws = p.WorkingSet64; } catch { }
                    try { threads = p.Threads.Count; } catch { }
                    try { pri = p.BasePriority; } catch { }
                    threadTotal += threads;

                    if (!_owners.TryGetValue(key, out string? user))
                    {
                        user = ResolveOwner(p);
                        _owners[key] = user;
                    }

                    data.Add(new HtopRowData(
                        key, p.ProcessName, user, pri,
                        Math.Clamp(cpuPct, 0, 100 * logical),
                        totalRam > 0 ? (double)ws / totalRam * 100.0 : 0,
                        ws, threads, cpu.TotalSeconds));
                }
                catch
                {
                    // Process exited mid-scan or access denied; skip it.
                }
            }
        }

        // Prune dead samples/caches.
        foreach (var k in _prev.Keys.Where(k => !alive.Contains(k)).ToList())
            _prev.Remove(k);
        foreach (var k in _owners.Keys.Where(k => !alive.Contains(k)).ToList())
            _owners.Remove(k);

        float totalCpu = 0;
        try { totalCpu = _totalCounter?.NextValue() ?? 0; } catch { }
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        string upText = up.Days > 0
            ? $"{up.Days}d {up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}"
            : $"{up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}";
        HeaderText = $"Tasks: {data.Count} total, {threadTotal} threads    CPU: {totalCpu:0.0}%    Uptime: {upText}";

        // Filter + sort.
        string f = Filter.Trim();
        var desired = data
            .Where(d => f.Length == 0 || d.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => SortByCpu ? d.CpuPct : d.MemPct)
            .ThenBy(d => d.Key.Pid)
            .ToList();
        var desiredKeys = new HashSet<(int, long)>(desired.Select(d => d.Key));

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(Processes[i].Key))
            {
                _rows.Remove(Processes[i].Key);
                Processes.RemoveAt(i);
            }
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            if (_rows.TryGetValue(d.Key, out var row))
            {
                int idx = Processes.IndexOf(row);
                if (idx != i)
                    Processes.Move(idx, i);
                row.Update(d);
            }
            else
            {
                var created = new HtopProcessRow(d);
                _rows[d.Key] = created;
                Processes.Insert(i, created);
            }
        }
    }

    private static long _totalRamBytes = -1;

    private static long TotalRamBytes()
    {
        if (_totalRamBytes > 0)
            return _totalRamBytes;
        try
        {
            using var os = new ManagementObjectSearcher(
                "SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in os.Get())
            {
                _totalRamBytes = Convert.ToInt64(obj["TotalVisibleMemorySize"]) * 1024L;
                break;
            }
        }
        catch { _totalRamBytes = 0; }

        return _totalRamBytes;
    }

    // ----- Process owner via access token (fast, no per-process WMI) -----
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int len, out int retLen);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static string ResolveOwner(Process p)
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(p.Handle, 0x0008 /* TOKEN_QUERY */, out token))
                return "";
            GetTokenInformation(token, 1 /* TokenUser */, IntPtr.Zero, 0, out int len);
            if (len == 0)
                return "";
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, 1, buf, len, out _))
                    return "";
                IntPtr sid = Marshal.ReadIntPtr(buf);
                try
                {
                    string nt = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
                    int slash = nt.IndexOf('\\');
                    return slash >= 0 ? nt[(slash + 1)..] : nt;
                }
                catch
                {
                    return new SecurityIdentifier(sid).ToString();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return "";
        }
        finally
        {
            if (token != IntPtr.Zero)
                CloseHandle(token);
        }
    }

    internal static (string Filled, string Empty) Bar(double pct)
    {
        int filled = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100.0 * BarWidth);
        return (new string('|', filled), new string(' ', BarWidth - filled));
    }
}

public readonly record struct HtopRowData(
    (int Pid, long StartTicks) Key,
    string Name,
    string User,
    int Pri,
    double CpuPct,
    double MemPct,
    long ResBytes,
    int Threads,
    double CpuTimeSeconds);

public sealed class HtopMeter : ObservableObject
{
    private string _text = "";
    private string _barFilled = "";
    private string _barEmpty = new(' ', 26);

    public HtopMeter(string label)
    {
        Label = label.PadRight(5);
    }

    public string Label { get; }
    public string Text { get => _text; private set => Set(ref _text, value); }
    public string BarFilled { get => _barFilled; private set => Set(ref _barFilled, value); }
    public string BarEmpty { get => _barEmpty; private set => Set(ref _barEmpty, value); }

    public void SetValue(double pct, string text)
    {
        var (filled, empty) = HtopViewModel.Bar(pct);
        BarFilled = filled;
        BarEmpty = empty;
        Text = text;
    }
}

public sealed class HtopProcessRow : ObservableObject
{
    private string _name;
    private string _user;
    private int _pri;
    private double _cpuPct;
    private double _memPct;
    private long _resBytes;
    private string _resText;
    private int _threads;
    private double _cpuTimeSeconds;
    private string _timeText;

    public HtopProcessRow(HtopRowData d)
    {
        Key = d.Key;
        Pid = d.Key.Pid;
        _name = d.Name;
        _user = d.User;
        _pri = d.Pri;
        _cpuPct = d.CpuPct;
        _memPct = d.MemPct;
        _resBytes = d.ResBytes;
        _resText = FormatRes(d.ResBytes);
        _threads = d.Threads;
        _cpuTimeSeconds = d.CpuTimeSeconds;
        _timeText = FormatTime(d.CpuTimeSeconds);
    }

    public (int Pid, long StartTicks) Key { get; }
    public int Pid { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string User { get => _user; private set => Set(ref _user, value); }
    public int Pri { get => _pri; private set => Set(ref _pri, value); }
    public double CpuPct { get => _cpuPct; private set => Set(ref _cpuPct, value); }
    public double MemPct { get => _memPct; private set => Set(ref _memPct, value); }
    public long ResBytes { get => _resBytes; private set => Set(ref _resBytes, value); }
    public string ResText { get => _resText; private set => Set(ref _resText, value); }
    public int Threads { get => _threads; private set => Set(ref _threads, value); }
    public double CpuTimeSeconds { get => _cpuTimeSeconds; private set => Set(ref _cpuTimeSeconds, value); }
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }

    public void Update(HtopRowData d)
    {
        Name = d.Name;
        User = d.User;
        Pri = d.Pri;
        CpuPct = d.CpuPct;
        MemPct = d.MemPct;
        ResBytes = d.ResBytes;
        ResText = FormatRes(d.ResBytes);
        Threads = d.Threads;
        CpuTimeSeconds = d.CpuTimeSeconds;
        TimeText = FormatTime(d.CpuTimeSeconds);
    }

    private static string FormatRes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return mb >= 1024 ? $"{mb / 1024.0:0.00}G" : $"{mb:0.0}M";
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        long hours = (long)t.TotalHours;
        return $"{hours}:{t.Minutes:00}:{t.Seconds:00}";
    }
}
