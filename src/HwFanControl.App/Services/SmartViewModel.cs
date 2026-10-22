using System.Collections.ObjectModel;
using System.Windows.Media;
using HwFanControl.Hardware.Smart;

namespace HwFanControl.App.Services;

public sealed class SmartViewModel : ObservableObject
{
    private static readonly Brush OkBrush = CreateBrush(0x00, 0xC0, 0x00);
    private static readonly Brush FailBrush = CreateBrush(0xE0, 0x20, 0x20);
    private static readonly Brush NeutralBrush = CreateBrush(0x33, 0x33, 0x33);

    private string? _selectedKey;
    private SmartDriveUi? _selectedDrive;
    private string _overallStatusText = "S.M.A.R.T. —";
    private Brush _overallStatusBrush = NeutralBrush;
    private bool _overallHealthy = true;

    public ObservableCollection<SmartDriveUi> Drives { get; } = new();
    public ObservableCollection<SmartAttributeUi> Attributes { get; } = new();

    public SmartDriveUi? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (!Set(ref _selectedDrive, value))
                return;
            _selectedKey = value?.Key;
            LoadAttributes(value);
        }
    }

    public string OverallStatusText
    {
        get => _overallStatusText;
        private set => Set(ref _overallStatusText, value);
    }

    public Brush OverallStatusBrush
    {
        get => _overallStatusBrush;
        private set => Set(ref _overallStatusBrush, value);
    }

    public bool OverallHealthy
    {
        get => _overallHealthy;
        private set => Set(ref _overallHealthy, value);
    }

    public void Apply(SmartSnapshot snap)
    {
        OverallHealthy = snap.OverallHealthy;
        OverallStatusText = snap.Drives.Count == 0
            ? "S.M.A.R.T. —"
            : snap.OverallHealthy ? "S.M.A.R.T. OK" : "S.M.A.R.T. FAIL";
        OverallStatusBrush = snap.Drives.Count == 0
            ? NeutralBrush
            : snap.OverallHealthy ? OkBrush : FailBrush;

        var keys = new HashSet<string>(snap.Drives.Select(d => d.Key), StringComparer.Ordinal);
        for (int i = Drives.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(Drives[i].Key))
                Drives.RemoveAt(i);
        }

        for (int i = 0; i < snap.Drives.Count; i++)
        {
            SmartDriveReading src = snap.Drives[i];
            SmartDriveUi? existing = Drives.FirstOrDefault(d => d.Key == src.Key);
            if (existing is null)
            {
                existing = new SmartDriveUi(src.Key);
                // Insert in sorted position.
                int insertAt = Drives.Count;
                for (int j = 0; j < Drives.Count; j++)
                {
                    if (src.DriveNumber < Drives[j].DriveNumber ||
                        (src.DriveNumber == Drives[j].DriveNumber &&
                         string.Compare(src.DisplayName, Drives[j].DisplayName, StringComparison.OrdinalIgnoreCase) < 0))
                    {
                        insertAt = j;
                        break;
                    }
                }

                Drives.Insert(insertAt, existing);
            }

            existing.Apply(src);
        }

        if (SelectedDrive is null || !keys.Contains(SelectedDrive.Key))
        {
            SelectedDrive = Drives.FirstOrDefault();
        }
        else
        {
            // Refresh attribute rows for the current selection.
            var current = Drives.FirstOrDefault(d => d.Key == _selectedKey);
            if (current is not null)
            {
                if (!ReferenceEquals(SelectedDrive, current))
                    SelectedDrive = current;
                else
                    LoadAttributes(current);
            }
        }
    }

    private void LoadAttributes(SmartDriveUi? drive)
    {
        Attributes.Clear();
        if (drive?.AttributeSource is null)
            return;

        foreach (var row in drive.AttributeSource)
        {
            Brush brush = row.IsHealthy switch
            {
                true => OkBrush,
                false => FailBrush,
                null => NeutralBrush,
            };
            Attributes.Add(new SmartAttributeUi
            {
                Id = row.Id,
                Attribute = row.Name,
                Data = row.Data,
                Status = row.Status,
                StatusBrush = brush,
            });
        }
    }

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class SmartDriveUi : ObservableObject
{
    private string _displayName = "Drive";
    private int _driveNumber;
    private string _driveType = "—";
    private string _model = "—";
    private string _sizeText = "—";
    private string _interface = "—";
    private string _temperatureText = "—";
    private string _uptimeText = "—";
    private string _serial = "—";
    private string _firmware = "—";
    private string _buffer = "—";
    private string _freeSpaceText = "—";
    private double _usedPercent;
    private string _usedPercentText = "—";
    private bool _hasSpace;
    private bool _healthy = true;

    public SmartDriveUi(string key) => Key = key;

    public string Key { get; }
    public List<SmartAttributeReading>? AttributeSource { get; private set; }

    public string DisplayName { get => _displayName; private set => Set(ref _displayName, value); }
    public int DriveNumber { get => _driveNumber; private set => Set(ref _driveNumber, value); }
    public string DriveType { get => _driveType; private set => Set(ref _driveType, value); }
    public string Model { get => _model; private set => Set(ref _model, value); }
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public string Interface { get => _interface; private set => Set(ref _interface, value); }
    public string TemperatureText { get => _temperatureText; private set => Set(ref _temperatureText, value); }
    public string UptimeText { get => _uptimeText; private set => Set(ref _uptimeText, value); }
    public string Serial { get => _serial; private set => Set(ref _serial, value); }
    public string Firmware { get => _firmware; private set => Set(ref _firmware, value); }
    public string Buffer { get => _buffer; private set => Set(ref _buffer, value); }
    public string FreeSpaceText { get => _freeSpaceText; private set => Set(ref _freeSpaceText, value); }
    public double UsedPercent { get => _usedPercent; private set => Set(ref _usedPercent, value); }
    public string UsedPercentText { get => _usedPercentText; private set => Set(ref _usedPercentText, value); }
    public bool HasSpace { get => _hasSpace; private set => Set(ref _hasSpace, value); }
    public bool Healthy { get => _healthy; private set => Set(ref _healthy, value); }

    public void Apply(SmartDriveReading src)
    {
        DisplayName = src.DisplayName;
        DriveNumber = src.DriveNumber;
        DriveType = src.DriveType;
        Model = src.Model;
        SizeText = src.SizeText;
        Interface = src.Interface;
        TemperatureText = src.TemperatureText;
        UptimeText = src.UptimeText;
        Serial = src.Serial;
        Firmware = src.Firmware;
        Buffer = src.Buffer;
        FreeSpaceText = src.FreeSpaceText;
        UsedPercent = src.UsedPercent;
        HasSpace = src.HasSpace;
        UsedPercentText = src.HasSpace ? $"{src.UsedPercent:0.0}%" : "—";
        Healthy = src.OverallHealthy;
        AttributeSource = src.Attributes;
    }
}

public sealed class SmartAttributeUi : ObservableObject
{
    private int _id;
    private string _attribute = "";
    private string _data = "—";
    private string _status = "—";
    private Brush _statusBrush = Brushes.Black;

    public int Id { get => _id; set => Set(ref _id, value); }
    public string Attribute { get => _attribute; set => Set(ref _attribute, value); }
    public string Data { get => _data; set => Set(ref _data, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }
}
