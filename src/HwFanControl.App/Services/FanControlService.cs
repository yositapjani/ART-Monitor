using System.Collections.ObjectModel;
using HwFanControl.Core;
using HwFanControl.Core.Config;
using HwFanControl.Core.Curves;
using HwFanControl.Core.Safety;
using HwFanControl.Core.Sensors;
using HwFanControl.Hardware;
using HwFanControl.Hardware.Cpu;
using HwFanControl.Hardware.Gpu;
using HwFanControl.Hardware.Overview;
using HwFanControl.Hardware.Sensors;
using HwFanControl.Hardware.Smart;

namespace HwFanControl.App.Services;

public sealed class FanControlService : IDisposable
{
    public const int PollIntervalMs = 1000;

    private readonly HardwareMonitor _monitor = new();
    private readonly FanLogicController _logic = new();
    private readonly FailSafeHooks _hooks;
    private readonly float[] _pwmOut = new float[FanLogicController.MaxFanChannels];
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private FanProfile _profile;
    private SensorSnapshot _snapshot = SensorSnapshot.Create();
    private bool _opened;
    private bool _disposed;
    private string? _lastError;

    public FanControlService()
    {
        _hooks = new FailSafeHooks(() =>
        {
            try { _monitor.ReleaseControlToBios(); } catch { /* ignore */ }
        });
        _profile = FanProfile.CreateDefault();
    }

    public FanLogicController Logic => _logic;
    public FanProfile Profile => _profile;
    public bool IsHardwareOpen => _opened;
    public bool IsElevated { get; private set; }
    public string? LastError => _lastError;
    public int FanControlCount => _monitor.FanControlChannelCount;
    public int FanRpmCount => _monitor.FanRpmChannelCount;
    public CpuTelemetry Cpu => _monitor.Cpu;
    public CpuInfoViewModel CpuInfo { get; } = new();
    public OverviewViewModel Overview { get; } = new();
    public GpuViewModel Gpu { get; } = new();
    public HdTempViewModel HdTemp { get; } = new();
    public SmartViewModel Smart { get; } = new();
    public MainboardViewModel Mainboard { get; } = new();
    public SystemViewModel SystemVm { get; } = new();

    public ObservableCollection<TempRow> Temperatures { get; } = new();
    public ObservableCollection<FanRow> Fans { get; } = new();

    public event Action? UiTick;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsElevated = Elevation.IsProcessElevated();
        _hooks.Register(attachConsoleEvents: false);
        _hooks.ResetRestoredFlag();

        _profile = FanProfileStore.LoadOrCreate();
        _logic.LoadChannels(_profile.Channels);
        if (!_profile.Overview.DefaultsSeeded)
        {
            // Per-core graph lines start hidden; the "All CPU Cores" master toggle reveals them.
            for (int i = 0; i < 32; i++)
            {
                string n = $"Core #{i}";
                if (!_profile.Overview.HiddenSeries.Contains(n))
                    _profile.Overview.HiddenSeries.Add(n);
            }

            _profile.Overview.DefaultsSeeded = true;
            SaveProfile();
        }

        Overview.SetHiddenSeries(_profile.Overview.HiddenSeries);
        BuildDriveLetterMap();
        RebuildUiRows();

        try
        {
            _monitor.Open();
            _opened = true;
            _lastError = null;

            int visible = Math.Max(_monitor.FanControlChannelCount, _monitor.FanRpmChannelCount);
            if (visible <= 0)
                visible = FanLogicController.MaxFanChannels;

            for (int i = 0; i < FanLogicController.MaxFanChannels; i++)
            {
                var ch = _logic.GetChannel(i);
                ch.Show = i < visible;

                // If global/channel apply-on-startup is off, leave channel disabled until user enables.
                if (!_profile.ApplyOnStartupGlobal || !ch.ApplyOnStartup)
                    ch.Mode = FanChannelMode.Disabled;
            }

            RebuildUiRows();
        }
        catch (Exception ex)
        {
            _opened = false;
            _lastError = ex.Message;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void SaveProfile()
    {
        SyncProfileFromLogic();
        FanProfileStore.Save(_profile);
    }

    public void ApplyChannel(FanChannelConfig config)
    {
        _logic.SetChannelConfig(config);
        SyncProfileFromLogic();
        FanProfileStore.Save(_profile);
    }

    public void EmergencyRestore() => _hooks.EmergencyRestore();

    private async Task PollLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                TickOnce();
                UiTick?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private void TickOnce()
    {
        if (!_opened)
            return;

        lock (_gate)
        {
            _monitor.Poll(ref _snapshot);
            if (_snapshot.Buffer.IsStale)
                return;

            _logic.Evaluate(_snapshot.Buffer, _pwmOut.AsSpan(), DateTimeOffset.UtcNow);

            for (int ch = 0; ch < FanLogicController.MaxFanChannels; ch++)
            {
                var cfg = _logic.GetChannel(ch);
                if (cfg.Mode == FanChannelMode.Disabled)
                    continue;
                if (ch >= _monitor.FanControlChannelCount)
                    continue;

                float pwm = _pwmOut[ch];
                if (float.IsNaN(pwm))
                    continue;

                _monitor.TrySetFanPwm(ch, pwm);
            }

            UpdateRows(_snapshot.Buffer, _pwmOut);
            MaybeWriteLog(_snapshot.Buffer);
            // CPU snapshot is applied on the UI thread via UiTick → ApplyCpuToUi().
        }
    }

    public void ApplyCpuToUi()
    {
        if (!_opened)
            return;
        CpuInfo.ApplySnapshot(_monitor.Cpu.Capture());
    }

    public void ApplyOverviewToUi()
    {
        if (!_opened)
            return;

        var cores = new List<float>();
        foreach (var row in CpuInfo.Readings)
        {
            if (!row.Label.StartsWith("Core #", StringComparison.OrdinalIgnoreCase))
                continue;
            cores.Add(ParseTempC(row.CurrentText));
        }

        Overview.Apply(_monitor.Overview.Capture(cores));

        // Core Temp-style readings table for the Overview left panel.
        var coreRows = new Dictionary<int, (string Cur, string Min, string Max, string Avg)>();
        var coreLoads = new Dictionary<int, string>();
        string tjMaxText = "";
        (string Cur, string Min, string Max, string Avg)? tdieRow = null;
        (string Cur, string Min, string Max, string Avg)? ccdRow = null;
        (string Cur, string Min, string Max, string Avg)? powerRow = null;
        foreach (var row in CpuInfo.Readings)
        {
            if (row.Label.Equals("Tj. Max", StringComparison.OrdinalIgnoreCase))
            {
                tjMaxText = row.CurrentText;
                continue;
            }
            if (row.Label.Equals("Tdie", StringComparison.OrdinalIgnoreCase))
            {
                tdieRow = (row.CurrentText, row.MinText, row.MaxText, row.AvgText);
                continue;
            }
            if (row.Label.Contains("CCD", StringComparison.OrdinalIgnoreCase))
            {
                ccdRow ??= (row.CurrentText, row.MinText, row.MaxText, row.AvgText);
                continue;
            }
            if (row.Label.Equals("Power", StringComparison.OrdinalIgnoreCase)
                && row.CurrentText.EndsWith("W", StringComparison.OrdinalIgnoreCase))
            {
                powerRow = (row.CurrentText, row.MinText, row.MaxText, row.AvgText);
                continue;
            }
            if (!row.Label.StartsWith("Core #", StringComparison.OrdinalIgnoreCase))
                continue;
            string digits = new string(row.Label.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int idx))
                continue;
            if (row.CurrentText.Contains("°C", StringComparison.OrdinalIgnoreCase))
                coreRows[idx] = (row.CurrentText, row.MinText, row.MaxText, row.AvgText);
            if (row.LoadText is not null && row.LoadText != "—")
            {
                string s = row.LoadText.Replace("%", "").Trim();
                if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float lv))
                    coreLoads[idx] = $"{lv:0}%";
            }
        }

        var readingRows = new List<(string Label, string Value, string Min, string Max, string Avg, string Load)>();
        if (powerRow is { } pr)
            readingRows.Add(("Power", pr.Cur, pr.Min, pr.Max, pr.Avg, ""));
        if (tjMaxText.Length > 0)
            readingRows.Add(("Tj. Max", tjMaxText, "", "", "", ""));
        if (tdieRow is { } td)
            readingRows.Add(("Tdie", td.Cur, td.Min, td.Max, td.Avg, ""));
        if (ccdRow is { } cc)
            readingRows.Add(("Tccd #0", cc.Cur, cc.Min, cc.Max, cc.Avg, ""));
        foreach (var kv in coreRows.OrderBy(kv => kv.Key))
        {
            string load = coreLoads.TryGetValue(kv.Key, out var l) ? l : "";
            readingRows.Add(($"Core #{kv.Key}", kv.Value.Cur, kv.Value.Min, kv.Value.Max, kv.Value.Avg, load));
        }
        Overview.ApplyReadings(readingRows);
    }

    public void ApplyGpuToUi()
    {
        if (!_opened)
            return;
        Gpu.Apply(_monitor.Gpu.Capture());
    }

    public void ApplyHdTempToUi()
    {
        if (!_opened)
            return;
        HdTemp.Apply(_monitor.HdTemp.Capture());
    }

    private float _lastGpuPwm = float.NaN;

    public void SetGpuFanSoftwareControl(bool software)
    {
        _profile.GpuFan.SoftwareControl = software;
        if (!software)
        {
            _lastGpuPwm = float.NaN;
            try { _monitor.Gpu.ReleaseFan(); } catch { /* ignore */ }
        }
        SaveProfile();
    }

    /// <summary>Called on the UI tick: drives the GPU fan from its curve when software control is on.</summary>
    public void ApplyGpuFanControl()
    {
        if (!_opened || !_profile.GpuFan.SoftwareControl)
            return;

        var snap = _monitor.Gpu.Capture();
        if (!snap.HasFan || float.IsNaN(snap.TemperatureCelsius))
            return;

        float pwm = HwFanControl.Core.Curves.CurveEvaluator.Evaluate(_profile.GpuFan.Curve, snap.TemperatureCelsius);
        pwm = Math.Max(pwm, _profile.GpuFan.MinPwmPercent);
        pwm = Math.Clamp(pwm, 0f, 100f);
        if (!float.IsNaN(_lastGpuPwm) && Math.Abs(pwm - _lastGpuPwm) < 1f)
            return;

        if (_monitor.Gpu.TrySetFanPercent(pwm))
            _lastGpuPwm = pwm;
    }

    private DateTime _lastLogTime = DateTime.MinValue;

    private void MaybeWriteLog(HwFanControl.Core.Sensors.TelemetryBuffer buffer)
    {
        var cfg = _profile.Logging;
        if (!cfg.Enabled)
            return;

        DateTime now = DateTime.Now;
        if ((now - _lastLogTime).TotalSeconds < Math.Max(1, cfg.IntervalSeconds))
            return;
        _lastLogTime = now;

        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HwFanControl", "Logs");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "art-monitor-" + now.ToString("yyyy-MM-dd") + ".csv");
            bool newFile = !System.IO.File.Exists(path) || new System.IO.FileInfo(path).Length == 0;

            string F(HwFanControl.Core.Sensors.SensorSlot slot) =>
                buffer.TryGet(slot, out float v)
                    ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    : "";
            string R(int i) => F((HwFanControl.Core.Sensors.SensorSlot)(16 + i));
            string P(int i) => F((HwFanControl.Core.Sensors.SensorSlot)(32 + i));

            var sb = new System.Text.StringBuilder();
            if (newFile)
                sb.AppendLine("Time,CpuPackageC,CpuCoreMaxC,GpuEdgeC,GpuJunctionC,AmbientC,Fan1Rpm,Fan1Pwm,Fan2Rpm,Fan2Pwm,Fan3Rpm,Fan3Pwm,Fan4Rpm,Fan4Pwm,Fan5Rpm,Fan5Pwm,Fan6Rpm,Fan6Pwm");
            sb.Append(now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(',').Append(F(HwFanControl.Core.Sensors.SensorSlot.CpuPackageTemp));
            sb.Append(',').Append(F(HwFanControl.Core.Sensors.SensorSlot.CpuCoreTempMax));
            sb.Append(',').Append(F(HwFanControl.Core.Sensors.SensorSlot.GpuEdgeTemp));
            sb.Append(',').Append(F(HwFanControl.Core.Sensors.SensorSlot.GpuJunctionTemp));
            sb.Append(',').Append(F(HwFanControl.Core.Sensors.SensorSlot.AmbientTemp));
            for (int i = 0; i < 6; i++)
            {
                sb.Append(',').Append(R(i));
                sb.Append(',').Append(P(i));
            }
            System.IO.File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            // Logging must never break the fan-control loop.
        }
    }

    public void SetOverviewGroupVisible(string group, bool visible)
    {
        foreach (var item in Overview.Legend)
        {
            if (OverviewViewModel.GroupOf(item.Name) != group)
                continue;
            if (visible)
                _profile.Overview.HiddenSeries.Remove(item.Name);
            else if (!_profile.Overview.HiddenSeries.Contains(item.Name))
                _profile.Overview.HiddenSeries.Add(item.Name);
        }

        Overview.SetHiddenSeries(_profile.Overview.HiddenSeries);
        SaveProfile();
    }

    private void BuildDriveLetterMap()
    {
        try
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, Model, Size FROM Win32_DiskDrive");
            foreach (System.Management.ManagementObject drive in searcher.Get())
            {
                string model = drive["Model"]?.ToString()?.Trim() ?? "";
                if (model.Length == 0)
                    continue;
                if (drive["Size"] is { } sizeObj)
                    totalBytes += Convert.ToInt64(sizeObj);
                var letters = new List<string>();
                foreach (System.Management.ManagementObject part in drive.GetRelated("Win32_DiskPartition"))
                {
                    foreach (System.Management.ManagementObject ld in part.GetRelated("Win32_LogicalDisk"))
                    {
                        string letter = ld["DeviceID"]?.ToString() ?? "";
                        if (letter.Length > 0)
                            letters.Add(letter);
                    }
                }

                if (letters.Count > 0)
                    map[model] = string.Join(", ", letters);
            }

            Overview.DriveLetters = map;
            if (totalBytes > 0)
                Overview.StorageTitle = $"Storage (HDD/SSD/NVMe) - Total {FormatTotalSize(totalBytes)}";
        }
        catch
        {
            // Drive letters are cosmetic; ignore WMI failures.
        }
    }

    private static string FormatTotalSize(long bytes)
    {
        double tb = bytes / 1_000_000_000_000.0;
        if (tb >= 1.0)
            return $"{tb:0.00} TB";
        double gb = bytes / 1_000_000_000.0;
        return $"{gb:0} GB";
    }

    public void SetOverviewSeriesVisible(string name, bool visible)
    {
        if (visible)
            _profile.Overview.HiddenSeries.Remove(name);
        else if (!_profile.Overview.HiddenSeries.Contains(name))
            _profile.Overview.HiddenSeries.Add(name);
        Overview.SetHiddenSeries(_profile.Overview.HiddenSeries);
        SaveProfile();
    }

    public void ApplySmartToUi()
    {
        if (!_opened)
            return;
        Smart.Apply(_monitor.Smart.Capture());
    }

    public void ApplyMainboardToUi()
    {
        if (!_opened)
            return;
        Mainboard.Apply(_monitor.Mainboard.Capture());
    }

    public void ApplySystemToUi()
    {
        SystemVm.Push(AverageCoreLoadPercent());
    }

    private float AverageCoreLoadPercent()
    {
        float sum = 0;
        int n = 0;
        foreach (var row in CpuInfo.Readings)
        {
            if (!row.Label.StartsWith("Core #", StringComparison.OrdinalIgnoreCase))
                continue;
            if (row.LoadText is null || row.LoadText == "—")
                continue;
            string s = row.LoadText.Replace("%", "").Trim();
            if (float.TryParse(s, global::System.Globalization.NumberStyles.Float,
                    global::System.Globalization.CultureInfo.InvariantCulture, out float v))
            {
                sum += v;
                n++;
            }
        }

        return n == 0 ? float.NaN : sum / n;
    }

    private static float ParseTempC(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text is "—" or "---")
            return float.NaN;
        string num = text.Replace("°C", "", StringComparison.OrdinalIgnoreCase).Trim();
        return float.TryParse(num, global::System.Globalization.NumberStyles.Float,
            global::System.Globalization.CultureInfo.InvariantCulture, out float v)
            ? v
            : float.NaN;
    }

    private void RebuildUiRows()
    {
        Temperatures.Clear();
        foreach (var t in _profile.TemperatureChannels.Where(x => x.Show))
            Temperatures.Add(new TempRow { Id = t.Id, Name = t.DisplayName, Source = t.Source });

        Fans.Clear();
        foreach (var ch in _logic.Channels.Where(c => c.Show))
        {
            Fans.Add(new FanRow
            {
                ChannelIndex = ch.ChannelIndex,
                Name = ch.Name,
                Mode = ch.Mode,
                ManualPwm = ch.ManualPwmPercent,
                Source = ch.TemperatureSource,
                ApplyOnStartup = ch.ApplyOnStartup,
            });
        }
    }

    private void UpdateRows(TelemetryBuffer buf, float[] pwm)
    {
        foreach (var row in Temperatures)
        {
            row.ValueCelsius = ResolveTemp(buf, row.Source);
            row.HasValue = !float.IsNaN(row.ValueCelsius);
        }

        foreach (var row in Fans)
        {
            int i = row.ChannelIndex;
            var cfg = _logic.GetChannel(i);
            row.Name = cfg.Name;
            row.Mode = cfg.Mode;
            row.ManualPwm = cfg.ManualPwmPercent;
            row.Source = cfg.TemperatureSource;
            row.ApplyOnStartup = cfg.ApplyOnStartup;
            row.OutputPwm = float.IsNaN(pwm[i]) ? 0 : pwm[i];

            var rpmSlot = SensorSlot.FanRpm0 + i;
            if (buf.TryGet(rpmSlot, out float rpm))
            {
                row.Rpm = rpm;
                row.HasRpm = true;
            }
            else
            {
                row.HasRpm = false;
            }
        }
    }

    private static float ResolveTemp(TelemetryBuffer buf, TemperatureSourceKind source)
    {
        return source switch
        {
            TemperatureSourceKind.CpuPackage => Get(buf, SensorSlot.CpuPackageTemp, SensorSlot.CpuCoreTempMax),
            TemperatureSourceKind.CpuCoreMax => Get(buf, SensorSlot.CpuCoreTempMax, SensorSlot.CpuPackageTemp),
            TemperatureSourceKind.GpuEdge => Get(buf, SensorSlot.GpuEdgeTemp),
            TemperatureSourceKind.GpuJunction => Get(buf, SensorSlot.GpuJunctionTemp, SensorSlot.GpuEdgeTemp),
            TemperatureSourceKind.GpuHotspot => Get(buf, SensorSlot.GpuHotspotTemp, SensorSlot.GpuJunctionTemp, SensorSlot.GpuEdgeTemp),
            TemperatureSourceKind.Ambient => Get(buf, SensorSlot.AmbientTemp),
            TemperatureSourceKind.AvgCpuGpu => Avg(Get(buf, SensorSlot.CpuPackageTemp, SensorSlot.CpuCoreTempMax), Get(buf, SensorSlot.GpuEdgeTemp)),
            TemperatureSourceKind.MaxCpuGpu => Max(Get(buf, SensorSlot.CpuPackageTemp, SensorSlot.CpuCoreTempMax), Get(buf, SensorSlot.GpuEdgeTemp)),
            _ => float.NaN,
        };
    }

    private static float Get(TelemetryBuffer buf, params SensorSlot[] slots)
    {
        foreach (var s in slots)
        {
            if (buf.TryGet(s, out float v))
                return v;
        }

        return float.NaN;
    }

    private static float Avg(float a, float b) =>
        float.IsNaN(a) || float.IsNaN(b) ? float.NaN : (a + b) * 0.5f;

    private static float Max(float a, float b) =>
        float.IsNaN(a) || float.IsNaN(b) ? float.NaN : global::System.Math.Max(a, b);

    private void SyncProfileFromLogic()
    {
        _profile.Channels = _logic.Channels.Select(CloneChannel).ToList();
    }

    private static FanChannelConfig CloneChannel(FanChannelConfig c)
    {
        // Profile holds same references as logic for simplicity; ensure list is current.
        return c;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _cts?.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* ignore */ }

        try { SaveProfile(); } catch { /* ignore */ }

        _hooks.Dispose();
        SystemVm.Dispose();
        _monitor.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

public static class Elevation
{
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class TempRow : ObservableObject
{
    private string _name = "";
    private float _value;
    private bool _hasValue;
    private string _display = "—";

    public string Id { get; set; } = "";
    public TemperatureSourceKind Source { get; set; }

    public string Name { get => _name; set => Set(ref _name, value); }

    public float ValueCelsius
    {
        get => _value;
        set
        {
            if (Set(ref _value, value))
                DisplayValue = HasValue ? $"{_value:0.0} °C" : "—";
        }
    }

    public bool HasValue
    {
        get => _hasValue;
        set
        {
            if (Set(ref _hasValue, value))
                DisplayValue = value ? $"{_value:0.0} °C" : "—";
        }
    }

    public string DisplayValue { get => _display; private set => Set(ref _display, value); }
}

public sealed class FanRow : ObservableObject
{
    private string _name = "";
    private FanChannelMode _mode;
    private float _manual;
    private float _output;
    private float _rpm;
    private bool _hasRpm;
    private TemperatureSourceKind _source;
    private bool _applyOnStartup;
    private string _rpmText = "—";
    private string _outputText = "0%";

    public int ChannelIndex { get; set; }

    public string Name { get => _name; set => Set(ref _name, value); }
    public FanChannelMode Mode { get => _mode; set => Set(ref _mode, value); }
    public float ManualPwm { get => _manual; set => Set(ref _manual, value); }
    public TemperatureSourceKind Source { get => _source; set => Set(ref _source, value); }
    public bool ApplyOnStartup { get => _applyOnStartup; set => Set(ref _applyOnStartup, value); }

    public float OutputPwm
    {
        get => _output;
        set
        {
            if (Set(ref _output, value))
                OutputText = $"{value:0}%";
        }
    }

    public float Rpm
    {
        get => _rpm;
        set
        {
            if (Set(ref _rpm, value))
                RpmText = HasRpm ? $"{value:0} RPM" : "—";
        }
    }

    public bool HasRpm
    {
        get => _hasRpm;
        set
        {
            if (Set(ref _hasRpm, value))
                RpmText = value ? $"{_rpm:0} RPM" : "—";
        }
    }

    public string RpmText { get => _rpmText; private set => Set(ref _rpmText, value); }
    public string OutputText { get => _outputText; private set => Set(ref _outputText, value); }
}

public abstract class ObservableObject : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        return true;
    }
}
