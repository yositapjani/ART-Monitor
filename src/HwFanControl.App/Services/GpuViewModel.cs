using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using HwFanControl.Hardware.Gpu;

namespace HwFanControl.App.Services;

public sealed class GpuViewModel : ObservableObject
{
    public const int HistoryCapacity = 180;

    /// <summary>MHz → plot units so memory clocks (~2000) sit near mid-low of a °C axis (Argus-like).</summary>
    public const float ClockPlotScale = 80f;

    private static readonly Color ColorGpuTemp = Color.FromRgb(0xFF, 0x00, 0x00);
    private static readonly Color ColorHotSpot = Color.FromRgb(0x00, 0x00, 0xFF);
    private static readonly Color ColorLoad = Color.FromRgb(0x00, 0x80, 0x00);
    private static readonly Color ColorCoreClock = Color.FromRgb(0x80, 0x00, 0x00);
    private static readonly Color ColorMemoryClock = Color.FromRgb(0x80, 0x80, 0x00);
    private static readonly Color ColorFan = Color.FromRgb(0x00, 0x66, 0xCC);

    private readonly Dictionary<string, Queue<float>> _histories = new(StringComparer.Ordinal);
    private float _axisMin;
    private float _axisMax = 60;
    private float _maxGpuTemp = float.NaN;

    private string _gpuName = "GPU";
    private string _gpuTempLine = "GPU Temperature: —";
    private string _hotSpotLine = "Hot Spot Temperature: 0.0 °C";
    private string _loadLine = "Load: 0 %";
    private string _coreClockLine = "Core Clock: 0 MHz";
    private string _memoryClockLine = "Memory Clock: 0 MHz";
    private string _fanSpeedLine = "Fan Speed: 0 % (0 RPM)";
    private string _maxGpuTempText = "Maximum GPU Temperature: —";
    private Visibility _helpVisibility = Visibility.Collapsed;
    private string _helpText = "AMD GPU Fan Control Help";

    public ObservableCollection<GpuLegendUi> Legend { get; } = new();
    public float AxisMin => _axisMin;
    public float AxisMax => _axisMax;
    public IReadOnlyDictionary<string, Queue<float>> Histories => _histories;

    public string GpuName { get => _gpuName; private set => Set(ref _gpuName, value); }
    public string GpuTempLine { get => _gpuTempLine; private set => Set(ref _gpuTempLine, value); }
    public string HotSpotLine { get => _hotSpotLine; private set => Set(ref _hotSpotLine, value); }
    public string LoadLine { get => _loadLine; private set => Set(ref _loadLine, value); }
    public string CoreClockLine { get => _coreClockLine; private set => Set(ref _coreClockLine, value); }
    public string MemoryClockLine { get => _memoryClockLine; private set => Set(ref _memoryClockLine, value); }
    public string FanSpeedLine { get => _fanSpeedLine; private set => Set(ref _fanSpeedLine, value); }
    public string MaxGpuTempText { get => _maxGpuTempText; private set => Set(ref _maxGpuTempText, value); }
    public Visibility HelpVisibility { get => _helpVisibility; private set => Set(ref _helpVisibility, value); }
    public string HelpText { get => _helpText; private set => Set(ref _helpText, value); }

    public void Apply(GpuSnapshot snap)
    {
        GpuName = string.IsNullOrWhiteSpace(snap.GpuName) ? "GPU" : snap.GpuName;
        HelpVisibility = snap.IsAmd ? Visibility.Visible : Visibility.Collapsed;
        HelpText = snap.IsAmd ? "AMD GPU Fan Control Help" :
            snap.IsNvidia ? "NVIDIA GPU Fan Control Help" : "GPU Fan Control Help";

        float temp = snap.TemperatureCelsius;
        if (snap.HasTemperature && !float.IsNaN(temp) && temp > 0.01f)
        {
            if (float.IsNaN(_maxGpuTemp) || temp > _maxGpuTemp)
                _maxGpuTemp = temp;
            GpuTempLine = $"GPU Temperature: {temp:0.0} °C";
            MaxGpuTempText = $"Maximum GPU Temperature: {_maxGpuTemp:0.0} °C";
        }
        else
        {
            GpuTempLine = "GPU Temperature: —";
            MaxGpuTempText = float.IsNaN(_maxGpuTemp)
                ? "Maximum GPU Temperature: —"
                : $"Maximum GPU Temperature: {_maxGpuTemp:0.0} °C";
        }

        HotSpotLine = $"Hot Spot Temperature: {snap.HotSpotCelsius:0.0} °C";
        LoadLine = $"Load: {snap.LoadPercent:0} %";
        CoreClockLine = $"Core Clock: {snap.CoreClockMhz:0} MHz";
        MemoryClockLine = $"Memory Clock: {snap.MemoryClockMhz:0} MHz";
        FanSpeedLine = $"Fan Speed: {snap.FanPercent:0} % ({snap.FanRpm:0} RPM)";

        var series = BuildSeries(snap);
        while (Legend.Count > series.Count)
            Legend.RemoveAt(Legend.Count - 1);

        float plotMin = float.MaxValue, plotMax = float.MinValue;
        for (int i = 0; i < series.Count; i++)
        {
            var s = series[i];
            if (!_histories.TryGetValue(s.Key, out var q))
            {
                q = new Queue<float>(HistoryCapacity);
                _histories[s.Key] = q;
            }

            Enqueue(q, s.PlotValue);
            foreach (float v in q)
            {
                if (float.IsNaN(v)) continue;
                if (v < plotMin) plotMin = v;
                if (v > plotMax) plotMax = v;
            }

            var brush = new SolidColorBrush(s.Color);
            brush.Freeze();
            if (i < Legend.Count)
            {
                Legend[i].Key = s.Key;
                Legend[i].Text = s.LegendText;
                Legend[i].Brush = brush;
            }
            else
            {
                Legend.Add(new GpuLegendUi { Key = s.Key, Text = s.LegendText, Brush = brush });
            }
        }

        var keep = new HashSet<string>(series.Select(s => s.Key), StringComparer.Ordinal);
        foreach (var key in _histories.Keys.ToList())
        {
            if (!keep.Contains(key))
                _histories.Remove(key);
        }

        // Axis follows temperature spirit: floor at 0, headroom above peak plot value.
        if (plotMin == float.MaxValue)
        {
            _axisMin = 0;
            _axisMax = 60;
        }
        else
        {
            _axisMin = 0;
            float peak = MathF.Max(plotMax, snap.HasTemperature && !float.IsNaN(temp) ? temp : 0);
            _axisMax = MathF.Ceiling((peak + 5) / 10f) * 10f;
            if (_axisMax < 50) _axisMax = 50;
            if (_axisMax - _axisMin < 20) _axisMax = _axisMin + 20;
        }
    }

    private static List<GpuSeriesPoint> BuildSeries(GpuSnapshot snap)
    {
        var list = new List<GpuSeriesPoint>(6);

        float temp = snap.HasTemperature ? snap.TemperatureCelsius : float.NaN;
        list.Add(new(
            "GPU Temp",
            float.IsNaN(temp) ? float.NaN : temp,
            $"GPU 1 {(float.IsNaN(temp) ? "—" : $"{temp:0.0} °C")}",
            ColorGpuTemp));

        list.Add(new(
            "Hot Spot",
            snap.HotSpotCelsius,
            $"Hot Spot {snap.HotSpotCelsius:0.0} °C",
            ColorHotSpot));

        list.Add(new(
            "Load",
            snap.LoadPercent,
            $"Load {snap.LoadPercent:0} %",
            ColorLoad));

        list.Add(new(
            "Core Clock",
            snap.CoreClockMhz / ClockPlotScale,
            $"Core Clock {snap.CoreClockMhz:0} MHz",
            ColorCoreClock));

        list.Add(new(
            "Memory Clock",
            snap.MemoryClockMhz / ClockPlotScale,
            $"Memory Clock {snap.MemoryClockMhz:0} MHz",
            ColorMemoryClock));

        list.Add(new(
            "Fan Speed",
            snap.FanPercent,
            $"Fan Speed {snap.FanPercent:0} % ({snap.FanRpm:0} RPM)",
            ColorFan));

        return list;
    }

    private static void Enqueue(Queue<float> q, float v)
    {
        if (q.Count >= HistoryCapacity)
            q.Dequeue();
        q.Enqueue(v);
    }

    private readonly record struct GpuSeriesPoint(string Key, float PlotValue, string LegendText, Color Color);
}

public sealed class GpuLegendUi : ObservableObject
{
    private string _key = "";
    private string _text = "";
    private Brush _brush = Brushes.Black;

    public string Key { get => _key; set => Set(ref _key, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }
}
