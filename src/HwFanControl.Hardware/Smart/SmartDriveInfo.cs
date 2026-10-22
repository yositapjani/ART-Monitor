using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using DiskInfoToolkit;
using DiskInfoToolkit.Enums.Interop;
using DiskInfoToolkit.Interop.Enums;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;
using LhmSmartAttribute = LibreHardwareMonitor.Hardware.Storage.SmartAttribute;

namespace HwFanControl.Hardware.Smart;

/// <summary>
/// Builds Argus-style S.M.A.R.T. drive summaries from LibreHardwareMonitor storage devices,
/// with WMI / DriveInfo supplements when fields are missing.
/// </summary>
public static class SmartDriveInfo
{
    public static SmartDriveReading? FromHardware(IHardware hardware, Dictionary<string, float> sessionMaxTemps)
    {
        if (hardware.HardwareType != HardwareType.Storage)
            return null;

        if (hardware is not StorageDevice storageDevice)
            return FromSensorsOnly(hardware, sessionMaxTemps);

        Storage? storage = storageDevice.Storage;
        if (storage is null)
            return FromSensorsOnly(hardware, sessionMaxTemps);

        string key = hardware.Identifier?.ToString() ?? storage.DeviceID ?? storage.Model ?? Guid.NewGuid().ToString("N");
        string model = Clean(storage.Model) ?? Clean(hardware.Name) ?? "Disk";
        string serial = Clean(storage.SerialNumber) ?? "—";
        string firmware = Clean(storage.Firmware) ?? Clean(storage.FirmwareRev) ?? "—";

        string driveType = storage.IsSSD || storage.IsNVMe ? "SSD" : "HDD";
        string iface = FormatInterface(storage);
        long sizeBytes = storage.TotalSize > 0 ? (long)storage.TotalSize : 0;

        var letters = CollectDriveLetters(storage);
        EnrichFromWmi(ref model, ref serial, ref firmware, ref driveType, ref iface, ref sizeBytes, letters);

        var (freeBytes, capacityBytes, usedPercent) = ResolveSpace(storage, letters, sizeBytes);

        float tempC = ReadTemperature(storage, hardware);
        float maxTemp = tempC;
        if (!float.IsNaN(tempC) && tempC > 0.01f)
        {
            if (sessionMaxTemps.TryGetValue(key, out float prev) && prev > maxTemp)
                maxTemp = prev;
            sessionMaxTemps[key] = maxTemp;
        }
        else if (sessionMaxTemps.TryGetValue(key, out float prev))
        {
            maxTemp = prev;
        }
        else
        {
            maxTemp = float.NaN;
        }

        ulong powerOnHours = storage.Smart?.MeasuredPowerOnHours
            ?? storage.Smart?.DetectedPowerOnHours
            ?? 0;
        if (powerOnHours == 0)
            powerOnHours = TryPowerOnHoursFromAttributes(storageDevice.Attributes);

        var attributes = BuildAttributes(storageDevice, storage, hardware);
        bool healthy = IsHealthy(storage, attributes);

        int driveNumber = storage.DriveNumber > 0 ? storage.DriveNumber : GuessDriveIndex(key);
        string displayName = FormatDisplayName(driveNumber, letters);

        return new SmartDriveReading
        {
            Key = key,
            DisplayName = displayName,
            DriveNumber = driveNumber,
            DriveLetters = letters,
            DriveType = driveType,
            Model = model,
            SizeText = sizeBytes > 0 ? FormatSize(sizeBytes) : "—",
            Interface = iface,
            TemperatureText = FormatTemperature(tempC, maxTemp),
            UptimeText = FormatUptime(powerOnHours),
            Serial = string.IsNullOrWhiteSpace(serial) ? "—" : serial,
            Firmware = string.IsNullOrWhiteSpace(firmware) ? "—" : firmware,
            Buffer = "—",
            FreeSpaceText = FormatFreeSpace(freeBytes, capacityBytes),
            UsedPercent = usedPercent,
            HasSpace = capacityBytes > 0,
            OverallHealthy = healthy,
            Attributes = attributes,
        };
    }

    private static SmartDriveReading FromSensorsOnly(IHardware hardware, Dictionary<string, float> sessionMaxTemps)
    {
        string key = hardware.Identifier?.ToString() ?? hardware.Name ?? Guid.NewGuid().ToString("N");
        string model = Clean(hardware.Name) ?? "Disk";
        float tempC = hardware.Sensors
            .Where(s => s.SensorType == SensorType.Temperature)
            .Select(s => s.Value ?? float.NaN)
            .FirstOrDefault(v => !float.IsNaN(v));

        float maxTemp = tempC;
        if (!float.IsNaN(tempC) && tempC > 0.01f)
        {
            if (sessionMaxTemps.TryGetValue(key, out float prev) && prev > maxTemp)
                maxTemp = prev;
            sessionMaxTemps[key] = maxTemp;
        }
        else
        {
            maxTemp = sessionMaxTemps.TryGetValue(key, out float prev) ? prev : float.NaN;
        }

        var attributes = new List<SmartAttributeReading>();
        int id = 1;
        foreach (ISensor sensor in hardware.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
        {
            string data = FormatSensorValue(sensor);
            attributes.Add(new SmartAttributeReading(id++, sensor.Name ?? sensor.SensorType.ToString(), data, "OK", true));
        }

        if (attributes.Count == 0)
        {
            foreach (string name in CanonicalNvmeNames())
                attributes.Add(new SmartAttributeReading(attributes.Count + 1, name, "—", "—", null));
        }

        return new SmartDriveReading
        {
            Key = key,
            DisplayName = model,
            DriveNumber = 0,
            DriveLetters = Array.Empty<string>(),
            DriveType = "SSD",
            Model = model,
            SizeText = "—",
            Interface = "—",
            TemperatureText = FormatTemperature(tempC, maxTemp),
            UptimeText = "—",
            Serial = "—",
            Firmware = "—",
            Buffer = "—",
            FreeSpaceText = "—",
            UsedPercent = 0,
            HasSpace = false,
            OverallHealthy = true,
            Attributes = attributes,
        };
    }

    private static List<SmartAttributeReading> BuildAttributes(
        StorageDevice storageDevice,
        Storage storage,
        IHardware hardware)
    {
        var rows = new List<SmartAttributeReading>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<LhmSmartAttribute> attrs = storageDevice.Attributes;
        if (attrs is { Count: > 0 })
        {
            int index = 1;
            foreach (LhmSmartAttribute attr in attrs)
            {
                string name = attr.Name;
                if (string.IsNullOrWhiteSpace(name) && attr.Attribute?.Info is not null)
                    name = attr.Attribute.Info.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = $"Attribute {attr.Id}";

                if (!seen.Add(name))
                    continue;

                string data = FormatAttributeData(attr, storage.IsNVMe);
                bool? ok = EvaluateAttributeHealth(attr, storage);
                string status = ok switch
                {
                    true => "OK",
                    false => "FAIL",
                    null => "—",
                };
                rows.Add(new SmartAttributeReading(index++, name, data, status, ok));
            }
        }

        // Best-effort NVMe canonical rows when LHM attributes are sparse.
        if (storage.IsNVMe && rows.Count < 6)
        {
            EnsureRow(rows, seen, "Critical Warning", FormatCriticalWarning(storage));
            EnsureRow(rows, seen, "Available Reserved Space", FormatPercent(storage.Smart?.Life));
            EnsureRow(rows, seen, "Percentage Used", FormatPercentUsed(storage));
            EnsureRow(rows, seen, "Power-On Hours", FormatHours(storage.Smart?.MeasuredPowerOnHours ?? storage.Smart?.DetectedPowerOnHours));
            EnsureRow(rows, seen, "Power Cycles", storage.Smart is { PowerOnCount: > 0 } s ? s.PowerOnCount.ToString(CultureInfo.InvariantCulture) : "—");
            EnsureRow(rows, seen, "Data Units Read", FormatHostIo(storage.Smart?.HostReads));
            EnsureRow(rows, seen, "Data Units Written", FormatHostIo(storage.Smart?.HostWrites));
            EnsureRow(rows, seen, "Temperature", FormatSingleTemp(storage.Smart?.Temperature ?? ReadTemperature(storage, hardware)));
        }

        // Include live LHM sensors not already represented (extra temp sensors, etc.).
        foreach (ISensor sensor in hardware.Sensors)
        {
            string name = sensor.Name ?? sensor.SensorType.ToString();
            if (!seen.Add(name))
                continue;
            rows.Add(new SmartAttributeReading(rows.Count + 1, name, FormatSensorValue(sensor), "OK", true));
        }

        if (rows.Count == 0)
        {
            foreach (string name in CanonicalNvmeNames())
                rows.Add(new SmartAttributeReading(rows.Count + 1, name, "—", "—", null));
        }

        // Re-number sequentially for Argus-style ID column.
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            rows[i] = r with { Id = i + 1 };
        }

        return rows;
    }

    private static void EnsureRow(
        List<SmartAttributeReading> rows,
        HashSet<string> seen,
        string name,
        string data)
    {
        if (!seen.Add(name))
            return;
        bool available = data != "—";
        rows.Add(new SmartAttributeReading(rows.Count + 1, name, data, available ? "OK" : "—", available ? true : null));
    }

    private static string FormatAttributeData(LhmSmartAttribute attr, bool isNvme)
    {
        SmartAttributeType type = attr.Attribute?.Info?.Type ?? SmartAttributeType.Unknown;
        float value = attr.Value;

        // Prefer raw ulong when present (counters / data units).
        ulong? raw = null;
        try
        {
            if (attr.Attribute?.Attribute.RawValue is { Length: > 0 })
                raw = attr.Attribute.Attribute.RawValueULong;
        }
        catch
        {
            // ignore malformed raw payloads
        }

        switch (type)
        {
            case SmartAttributeType.CriticalWarning:
                return raw.HasValue
                    ? raw.Value.ToString(CultureInfo.InvariantCulture)
                    : (!float.IsNaN(value) ? ((int)value).ToString(CultureInfo.InvariantCulture) : "—");

            case SmartAttributeType.PercentageUsed:
            case SmartAttributeType.AvailableSpare:
            case SmartAttributeType.AvailableSpareThreshold:
            case SmartAttributeType.AvailableReservedSpace:
            case SmartAttributeType.RemainingLife:
            case SmartAttributeType.MediaWearoutIndicator:
            case SmartAttributeType.LifetimeUsed:
                if (!float.IsNaN(value))
                    return $"{value:0} %";
                if (raw.HasValue)
                    return $"{raw.Value} %";
                return "—";

            case SmartAttributeType.CompositeTemperature:
            case SmartAttributeType.Temperature:
            case SmartAttributeType.AirflowTemperature:
            case SmartAttributeType.TemperatureSensor1:
            case SmartAttributeType.TemperatureSensor2:
            case SmartAttributeType.TemperatureSensor3:
            case SmartAttributeType.TemperatureSensor4:
            case SmartAttributeType.TemperatureSensor5:
            case SmartAttributeType.TemperatureSensor6:
            case SmartAttributeType.TemperatureSensor7:
            case SmartAttributeType.TemperatureSensor8:
            case SmartAttributeType.NANDTemperature:
                return FormatSingleTemp(NormalizeTemp(value, raw));

            case SmartAttributeType.HostDataRead:
            case SmartAttributeType.HostDataWritten:
            case SmartAttributeType.NandDataWritten:
            case SmartAttributeType.NandDataRead:
            case SmartAttributeType.TotalBytesRead:
            case SmartAttributeType.GigabytesErased:
                if (raw.HasValue)
                    return FormatDataUnits(raw.Value, isNvme);
                if (!float.IsNaN(value) && value > 0)
                    return FormatBytes((ulong)value);
                return "—";

            case SmartAttributeType.PowerOnHours:
                if (raw.HasValue)
                    return raw.Value.ToString(CultureInfo.InvariantCulture);
                if (!float.IsNaN(value))
                    return ((ulong)value).ToString(CultureInfo.InvariantCulture);
                return "—";

            default:
                if (raw.HasValue && raw.Value > 0)
                {
                    // Large NVMe counters look better in TB.
                    if (isNvme && (type is SmartAttributeType.HostReadCommands or SmartAttributeType.HostWriteCommands))
                        return raw.Value.ToString("N0", CultureInfo.InvariantCulture);
                    if (raw.Value >= 1_000_000UL)
                        return FormatDataUnits(raw.Value, isNvme);
                    return raw.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (!float.IsNaN(value))
                {
                    if (Math.Abs(value - MathF.Round(value)) < 0.001f)
                        return ((long)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);
                    return value.ToString("0.##", CultureInfo.InvariantCulture);
                }

                // ATA normalized current value as fallback.
                try
                {
                    byte current = attr.Attribute?.Attribute.CurrentValue ?? 0;
                    if (current > 0)
                        return current.ToString(CultureInfo.InvariantCulture);
                }
                catch { /* ignore */ }

                return "—";
        }
    }

    private static bool? EvaluateAttributeHealth(LhmSmartAttribute attr, Storage storage)
    {
        SmartAttributeType type = attr.Attribute?.Info?.Type ?? SmartAttributeType.Unknown;

        if (type == SmartAttributeType.CriticalWarning)
        {
            float v = attr.Value;
            if (!float.IsNaN(v))
                return v <= 0.01f;
            try
            {
                ulong raw = attr.Attribute?.Attribute.RawValueULong ?? 0;
                return raw == 0;
            }
            catch
            {
                return true;
            }
        }

        // ATA-style: current value at or below threshold is failure (threshold 0 means unused).
        try
        {
            byte threshold = attr.Threshold;
            if (threshold == 0 && attr.Attribute is not null)
                threshold = attr.Attribute.Attribute.Threshold;
            byte current = attr.Attribute?.Attribute.CurrentValue ?? 0;
            if (threshold > 0 && current > 0)
                return current > threshold;
        }
        catch
        {
            // ignore
        }

        if (storage.Smart?.DiskStatus is DiskStatus.Bad)
            return false;
        if (storage.Smart?.DiskStatus is DiskStatus.Caution)
            return true; // still show OK per-attribute; overall reflects caution via DiskStatus

        if (float.IsNaN(attr.Value) && attr.Attribute is null)
            return null;

        return true;
    }

    private static bool IsHealthy(Storage storage, List<SmartAttributeReading> attributes)
    {
        if (storage.Smart?.DiskStatus is DiskStatus.Bad)
            return false;
        if (attributes.Any(a => a.IsHealthy == false))
            return false;
        return true;
    }

    private static float ReadTemperature(Storage storage, IHardware hardware)
    {
        if (storage.Smart?.Temperature is { } tempRaw)
        {
            float t = Convert.ToSingle(tempRaw, CultureInfo.InvariantCulture);
            if (t > 0.01f)
            {
                // DiskInfoToolkit temperatures are usually °C already; guard Kelvin.
                if (t > 200)
                    t -= 273.15f;
                return t;
            }
        }

        foreach (ISensor s in hardware.Sensors)
        {
            if (s.SensorType == SensorType.Temperature && s.Value is float v && v > 0.01f)
                return v;
        }

        return float.NaN;
    }

    private static float NormalizeTemp(float value, ulong? raw)
    {
        if (!float.IsNaN(value) && value > 0.01f)
        {
            if (value > 200) // Kelvin
                return value - 273.15f;
            return value;
        }

        if (raw is ulong r && r > 0)
        {
            if (r > 200)
                return r - 273.15f;
            return r;
        }

        return float.NaN;
    }

    private static string FormatCriticalWarning(Storage storage)
    {
        // Prefer attribute if present; otherwise unknown.
        _ = storage;
        return "—";
    }

    private static string FormatPercent(float? life)
    {
        if (life is float v && !float.IsNaN(v))
            return $"{v:0} %";
        return "—";
    }

    private static string FormatPercentUsed(Storage storage)
    {
        // Life remaining → used ≈ 100 - life when life looks like remaining %.
        if (storage.Smart?.Life is { } lifeRaw)
        {
            float life = Convert.ToSingle(lifeRaw, CultureInfo.InvariantCulture);
            if (!float.IsNaN(life) && life is >= 0 and <= 100)
                return $"{Math.Clamp(100f - life, 0, 100):0} %";
        }

        return "—";
    }

    private static string FormatHours(ulong? hours) =>
        hours is > 0 ? hours.Value.ToString(CultureInfo.InvariantCulture) : "—";

    private static string FormatHostIo(ulong? value)
    {
        if (value is not > 0)
            return "—";
        // DiskInfoToolkit reports host read/write as gigabytes when available.
        double tb = value.Value / 1024.0;
        if (tb >= 0.01)
            return $"{tb.ToString("0.00", CultureInfo.InvariantCulture)} TB";
        return $"{value.Value.ToString("0", CultureInfo.InvariantCulture)} GB";
    }

    private static string FormatDataUnits(ulong raw, bool isNvme)
    {
        // NVMe data units are typically 1000 * 512-byte units.
        ulong bytes = isNvme ? raw * 1000UL * 512UL : raw;
        if (!isNvme && raw < 1_000_000UL)
            return raw.ToString(CultureInfo.InvariantCulture);
        return FormatBytes(bytes);
    }

    private static string FormatBytes(ulong bytes)
    {
        double tb = bytes / (1024.0 * 1024.0 * 1024.0 * 1024.0);
        if (tb >= 0.01)
            return $"{tb.ToString("0.00", CultureInfo.InvariantCulture)} TB";
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        if (gb >= 0.1)
            return $"{gb.ToString("0.0", CultureInfo.InvariantCulture)} GB";
        double mb = bytes / (1024.0 * 1024.0);
        return $"{mb.ToString("0.0", CultureInfo.InvariantCulture)} MB";
    }

    private static string FormatSensorValue(ISensor sensor)
    {
        if (sensor.Value is not float v || float.IsNaN(v))
            return "—";

        return sensor.SensorType switch
        {
            SensorType.Temperature => $"{v:0} °C",
            SensorType.Load or SensorType.Level or SensorType.Control => $"{v:0} %",
            SensorType.Data => FormatBytes((ulong)Math.Max(0, v)),
            SensorType.Throughput => $"{v:0.##}",
            _ => Math.Abs(v - MathF.Round(v)) < 0.001f
                ? ((long)MathF.Round(v)).ToString(CultureInfo.InvariantCulture)
                : v.ToString("0.##", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatSingleTemp(float tempC) =>
        float.IsNaN(tempC) || tempC <= 0.01f ? "—" : $"{tempC:0} °C";

    private static string FormatTemperature(float current, float max)
    {
        bool hasC = !float.IsNaN(current) && current > 0.01f;
        bool hasM = !float.IsNaN(max) && max > 0.01f;
        if (hasC && hasM)
            return $"{current:0} / {max:0} °C";
        if (hasC)
            return $"{current:0} °C";
        return "—";
    }

    private static string FormatUptime(ulong hours)
    {
        if (hours == 0)
            return "—";
        ulong days = hours / 24;
        ulong rem = hours % 24;
        if (days == 0)
            return rem == 1 ? "1 hour" : $"{rem} hours";
        string dayPart = days == 1 ? "1 day" : $"{days} days";
        string hourPart = rem == 1 ? "1 hour" : $"{rem} hours";
        return $"{dayPart}, {hourPart}";
    }

    private static string FormatSize(long bytes)
    {
        double gb = bytes / (1024.0 * 1024.0 * 1024.0);
        if (gb >= 1000)
        {
            double tb = gb / 1024.0;
            return $"{tb.ToString("0.#", CultureInfo.InvariantCulture)} TB";
        }

        return $"{Math.Round(gb):0} GB";
    }

    private static string FormatFreeSpace(long freeBytes, long capacityBytes)
    {
        if (capacityBytes <= 0)
            return "—";
        double freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
        double totalGb = capacityBytes / (1024.0 * 1024.0 * 1024.0);
        return $"{freeGb.ToString("0.0", CultureInfo.InvariantCulture)} GB free of {totalGb.ToString("0.0", CultureInfo.InvariantCulture)} GB";
    }

    private static string FormatInterface(Storage storage)
    {
        if (storage.IsNVMe || storage.BusType == StorageBusType.BusTypeNvme || storage.BusType == StorageBusType.BusTypeNvmeof)
            return "NVMe";
        return storage.BusType switch
        {
            StorageBusType.BusTypeSata => "SATA",
            StorageBusType.BusTypeUsb => "USB",
            StorageBusType.BusTypeSas => "SAS",
            StorageBusType.BusTypeScsi => "SCSI",
            StorageBusType.BusTypeRAID => "RAID",
            StorageBusType.BusTypeAta => "ATA",
            _ => storage.BusType == StorageBusType.BusTypeUnknown ? "—" : storage.BusType.ToString().Replace("BusType", "", StringComparison.Ordinal),
        };
    }

    private static string FormatDisplayName(int driveNumber, IReadOnlyList<string> letters)
    {
        string letterPart = letters.Count > 0
            ? string.Join(", ", letters.Select(l => l.EndsWith(':') ? l : l + ":"))
            : "";
        if (driveNumber > 0 && letterPart.Length > 0)
            return $"Drive {driveNumber} ({letterPart})";
        if (driveNumber > 0)
            return $"Drive {driveNumber}";
        if (letterPart.Length > 0)
            return $"Drive ({letterPart})";
        return "Drive";
    }

    private static List<string> CollectDriveLetters(Storage storage)
    {
        var letters = new List<string>();
        if (storage.Partitions is null)
            return letters;

        foreach (var part in storage.Partitions)
        {
            if (part.DriveLetter is char c && char.IsLetter(c))
            {
                string letter = $"{char.ToUpperInvariant(c)}:";
                if (!letters.Contains(letter, StringComparer.OrdinalIgnoreCase))
                    letters.Add(letter);
            }
        }

        return letters;
    }

    private static (long FreeBytes, long CapacityBytes, double UsedPercent) ResolveSpace(
        Storage storage,
        IReadOnlyList<string> letters,
        long sizeBytes)
    {
        long free = 0;
        long capacity = 0;
        bool any = false;

        foreach (string letter in letters)
        {
            try
            {
                var di = new DriveInfo(letter.TrimEnd(':') + ":\\");
                if (!di.IsReady)
                    continue;
                free += di.TotalFreeSpace;
                capacity += di.TotalSize;
                any = true;
            }
            catch
            {
                // ignore inaccessible volumes
            }
        }

        if (!any && storage.TotalFreeSize is ulong tf && sizeBytes > 0)
        {
            free = (long)tf;
            capacity = sizeBytes;
            any = true;
        }

        if (!any && sizeBytes > 0)
        {
            capacity = sizeBytes;
            free = 0;
        }

        double used = capacity > 0 ? (capacity - free) * 100.0 / capacity : 0;
        return (free, capacity, used);
    }

    private static void EnrichFromWmi(
        ref string model,
        ref string serial,
        ref string firmware,
        ref string driveType,
        ref string iface,
        ref long sizeBytes,
        IReadOnlyList<string> letters)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model, SerialNumber, FirmwareRevision, InterfaceType, MediaType, Size, PNPDeviceID FROM Win32_DiskDrive");
            foreach (ManagementObject obj in searcher.Get())
            {
                string wmiModel = Clean(obj["Model"]?.ToString()) ?? "";
                string wmiSerial = Clean(obj["SerialNumber"]?.ToString()) ?? "";
                bool modelMatch = !string.IsNullOrEmpty(wmiModel) &&
                                  (model.Contains(wmiModel, StringComparison.OrdinalIgnoreCase) ||
                                   wmiModel.Contains(model, StringComparison.OrdinalIgnoreCase));
                bool serialMatch = serial != "—" && !string.IsNullOrEmpty(wmiSerial) &&
                                   serial.Contains(wmiSerial, StringComparison.OrdinalIgnoreCase);

                if (!modelMatch && !serialMatch)
                    continue;

                if ((model is "Disk" or "—" || string.IsNullOrWhiteSpace(model)) && !string.IsNullOrEmpty(wmiModel))
                    model = wmiModel;
                if (serial is "—" && !string.IsNullOrEmpty(wmiSerial))
                    serial = wmiSerial;
                if (firmware is "—")
                {
                    string fw = Clean(obj["FirmwareRevision"]?.ToString()) ?? "";
                    if (!string.IsNullOrEmpty(fw))
                        firmware = fw;
                }

                if (iface is "—")
                {
                    string it = obj["InterfaceType"]?.ToString() ?? "";
                    if (!string.IsNullOrWhiteSpace(it))
                        iface = it;
                }

                string media = obj["MediaType"]?.ToString() ?? "";
                if (media.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                    media.Contains("Solid", StringComparison.OrdinalIgnoreCase))
                    driveType = "SSD";

                if (sizeBytes <= 0 && obj["Size"] is not null &&
                    ulong.TryParse(obj["Size"].ToString(), out ulong sz))
                    sizeBytes = (long)sz;

                return;
            }
        }
        catch
        {
            // WMI may be restricted; ignore.
        }

        // Map logical letters → physical disk via Win32_LogicalDiskToPartition when still missing size.
        if (sizeBytes <= 0 && letters.Count > 0)
        {
            try
            {
                foreach (string letter in letters)
                {
                    var di = new DriveInfo(letter.TrimEnd(':') + ":\\");
                    if (di.IsReady && di.TotalSize > 0)
                    {
                        sizeBytes = di.TotalSize;
                        break;
                    }
                }
            }
            catch { /* ignore */ }
        }
    }

    private static ulong TryPowerOnHoursFromAttributes(IReadOnlyList<LhmSmartAttribute>? attrs)
    {
        if (attrs is null)
            return 0;
        foreach (var attr in attrs)
        {
            var type = attr.Attribute?.Info?.Type ?? SmartAttributeType.Unknown;
            if (type != SmartAttributeType.PowerOnHours)
                continue;
            try
            {
                ulong raw = attr.Attribute?.Attribute.RawValueULong ?? 0;
                if (raw > 0)
                    return raw;
            }
            catch { /* ignore */ }
            if (!float.IsNaN(attr.Value) && attr.Value > 0)
                return (ulong)attr.Value;
        }

        return 0;
    }

    private static int GuessDriveIndex(string key)
    {
        // Identifier often contains an index digit near the end.
        for (int i = key.Length - 1; i >= 0; i--)
        {
            if (char.IsDigit(key[i]))
            {
                int end = i;
                while (i >= 0 && char.IsDigit(key[i]))
                    i--;
                if (int.TryParse(key.AsSpan(i + 1, end - i), out int n))
                    return n;
                break;
            }
        }

        return 0;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Trim())
        {
            if (c is >= ' ' and not '\u007f')
                sb.Append(c);
        }

        string s = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static IEnumerable<string> CanonicalNvmeNames() =>
    [
        "Critical Warning",
        "Available Reserved Space",
        "Percentage Used",
        "Power-On Hours",
        "Power Cycles",
        "Unsafe Shutdowns",
        "Data Units Read",
        "Data Units Written",
        "Host Reads",
        "Host Writes",
        "Controller Busy Time",
        "Temperature",
    ];
}

public sealed class SmartSnapshot
{
    public List<SmartDriveReading> Drives { get; init; } = new();
    public bool OverallHealthy { get; init; } = true;
}

public sealed class SmartDriveReading
{
    public string Key { get; init; } = "";
    public string DisplayName { get; init; } = "Drive";
    public int DriveNumber { get; init; }
    public IReadOnlyList<string> DriveLetters { get; init; } = Array.Empty<string>();
    public string DriveType { get; init; } = "—";
    public string Model { get; init; } = "—";
    public string SizeText { get; init; } = "—";
    public string Interface { get; init; } = "—";
    public string TemperatureText { get; init; } = "—";
    public string UptimeText { get; init; } = "—";
    public string Serial { get; init; } = "—";
    public string Firmware { get; init; } = "—";
    public string Buffer { get; init; } = "—";
    public string FreeSpaceText { get; init; } = "—";
    public double UsedPercent { get; init; }
    public bool HasSpace { get; init; }
    public bool OverallHealthy { get; init; } = true;
    public List<SmartAttributeReading> Attributes { get; init; } = new();
}

public readonly record struct SmartAttributeReading(
    int Id,
    string Name,
    string Data,
    string Status,
    bool? IsHealthy);
