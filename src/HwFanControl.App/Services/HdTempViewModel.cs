using System.Collections.ObjectModel;
using System.Management;
using System.Windows.Media;
using HwFanControl.Hardware.HdTemp;

namespace HwFanControl.App.Services;

public sealed class HdTempViewModel : ObservableObject
{
    public const int HistoryCapacity = 180;

    // Argus-like distinct series colors (cycle if more drives than palette).
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xC0, 0x78, 0x28), // orange-brown
        Color.FromRgb(0xC0, 0x40, 0x80), // pink/magenta
        Color.FromRgb(0x2E, 0x8B, 0x57), // green
        Color.FromRgb(0x20, 0x60, 0xA0), // blue
        Color.FromRgb(0x6B, 0x30, 0x90), // purple
        Color.FromRgb(0xD0, 0x60, 0x40), // salmon
        Color.FromRgb(0x1A, 0x3A, 0x6E), // dark blue
        Color.FromRgb(0x7C, 0xB3, 0x42), // light green
        Color.FromRgb(0xE6, 0x7E, 0x22), // orange
        Color.FromRgb(0x16, 0xA0, 0x85), // teal
    ];

    private readonly Dictionary<string, Queue<float>> _histories = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _modelToLetter;
    private float _axisMin = 30;
    private float _axisMax = 50;
    private float _maxHddTemp = float.NaN;
    private string _maxHddTempText = "Maximum HDD Temperature: —";

    public ObservableCollection<HdTempLegendUi> Legend { get; } = new();
    public float AxisMin => _axisMin;
    public float AxisMax => _axisMax;
    public IReadOnlyDictionary<string, Queue<float>> Histories => _histories;
    public string MaxHddTempText { get => _maxHddTempText; private set => Set(ref _maxHddTempText, value); }

    public Color ColorForIndex(int index) => ThemeService.ForGraph(Palette[Math.Abs(index) % Palette.Length]);

    public void Apply(HdTempSnapshot snap)
    {
        EnsureDriveLetters();

        while (Legend.Count > snap.Drives.Count)
            Legend.RemoveAt(Legend.Count - 1);

        float min = float.MaxValue, max = float.MinValue;
        float sessionMax = float.NaN;

        for (int i = 0; i < snap.Drives.Count; i++)
        {
            var d = snap.Drives[i];
            string? letter = d.DriveLetter ?? LookupLetter(d.Name);
            float temp = d.HasTemperature ? d.TemperatureCelsius : float.NaN;

            if (!_histories.TryGetValue(d.Key, out var q))
            {
                q = new Queue<float>(HistoryCapacity);
                _histories[d.Key] = q;
            }

            Enqueue(q, temp);
            foreach (float v in q)
            {
                if (float.IsNaN(v) || v <= 0.01f) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            if (!float.IsNaN(temp) && temp > 0.01f)
            {
                if (float.IsNaN(sessionMax) || temp > sessionMax)
                    sessionMax = temp;
                if (float.IsNaN(_maxHddTemp) || temp > _maxHddTemp)
                    _maxHddTemp = temp;
            }

            string tempText = float.IsNaN(temp) || temp <= 0.01f ? "—" : $"{temp:0.0} °C";
            string letterPart = string.IsNullOrEmpty(letter) ? "" : $" ({letter})";
            string text = $"{d.Name}{letterPart} {tempText}";

            var brush = new SolidColorBrush(ColorForIndex(i));
            brush.Freeze();

            if (i < Legend.Count)
            {
                Legend[i].Key = d.Key;
                Legend[i].Text = text;
                Legend[i].Brush = brush;
            }
            else
            {
                Legend.Add(new HdTempLegendUi { Key = d.Key, Text = text, Brush = brush });
            }
        }

        var keep = new HashSet<string>(snap.Drives.Select(d => d.Key), StringComparer.Ordinal);
        foreach (var key in _histories.Keys.ToList())
        {
            if (!keep.Contains(key))
                _histories.Remove(key);
        }

        MaxHddTempText = float.IsNaN(_maxHddTemp)
            ? "Maximum HDD Temperature: —"
            : $"Maximum HDD Temperature: {_maxHddTemp:0} °C";

        if (min == float.MaxValue)
        {
            _axisMin = 30;
            _axisMax = 50;
        }
        else
        {
            _axisMin = MathF.Floor((min - 5) / 5f) * 5f;
            _axisMax = MathF.Ceiling((max + 5) / 5f) * 5f;
            if (_axisMax - _axisMin < 20)
                _axisMax = _axisMin + 20;
        }
    }

    private string? LookupLetter(string name)
    {
        if (_modelToLetter is null || _modelToLetter.Count == 0)
            return null;

        string norm = HdTempSensors.Normalize(name);
        if (_modelToLetter.TryGetValue(norm, out string? exact))
            return exact;

        string? best = null;
        int bestLen = 0;
        foreach (var kv in _modelToLetter)
        {
            if (norm.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Contains(norm, StringComparison.OrdinalIgnoreCase))
            {
                int len = Math.Min(norm.Length, kv.Key.Length);
                if (len > bestLen)
                {
                    bestLen = len;
                    best = kv.Value;
                }
            }
        }

        return best;
    }

    private void EnsureDriveLetters()
    {
        if (_modelToLetter is not null)
            return;

        _modelToLetter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var drives = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            foreach (ManagementObject drive in drives.Get())
            {
                string? model = drive["Model"]?.ToString()?.Trim();
                string? deviceId = drive["DeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(deviceId))
                    continue;

                string? letter = FirstLogicalLetter(deviceId);
                if (letter is null)
                    continue;

                string key = HdTempSensors.Normalize(model);
                if (!_modelToLetter.ContainsKey(key))
                    _modelToLetter[key] = letter;
            }
        }
        catch
        {
            // WMI unavailable — legend still shows model + temp without (X:).
        }
    }

    private static string? FirstLogicalLetter(string diskDeviceId)
    {
        try
        {
            string escaped = diskDeviceId.Replace("\\", "\\\\", StringComparison.Ordinal);
            using var partitions = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID=\"{escaped}\"}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
            foreach (ManagementObject partition in partitions.Get())
            {
                string? partId = partition["DeviceID"]?.ToString();
                if (string.IsNullOrWhiteSpace(partId))
                    continue;

                string partEscaped = partId.Replace("\\", "\\\\", StringComparison.Ordinal);
                using var logicals = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID=\"{partEscaped}\"}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                foreach (ManagementObject logical in logicals.Get())
                {
                    string? id = logical["DeviceID"]?.ToString()?.Trim(); // "C:"
                    if (string.IsNullOrWhiteSpace(id))
                        continue;
                    if (!id.EndsWith(":", StringComparison.Ordinal))
                        id += ":";
                    return id;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static void Enqueue(Queue<float> q, float v)
    {
        if (q.Count >= HistoryCapacity)
            q.Dequeue();
        q.Enqueue(v);
    }
}

public sealed class HdTempLegendUi : ObservableObject
{
    private string _key = "";
    private string _text = "";
    private Brush _brush = Brushes.Black;

    public string Key { get => _key; set => Set(ref _key, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public Brush Brush { get => _brush; set => Set(ref _brush, value); }
}
