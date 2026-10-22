using System.Text.Json;
using System.Text.Json.Serialization;
using HwFanControl.Core.Curves;

namespace HwFanControl.Core.Config;

public sealed class FanProfile
{
    public int Version { get; set; } = 1;
    public bool ApplyOnStartupGlobal { get; set; } = true;
    public List<FanChannelConfig> Channels { get; set; } = CreateDefaultChannels();
    public List<NamedTemperatureChannel> TemperatureChannels { get; set; } = CreateDefaultTemps();
    public GpuFanSettings GpuFan { get; set; } = new();
    public LogSettings Logging { get; set; } = new();
    public OverviewSettings Overview { get; set; } = new();

    public static FanProfile CreateDefault() => new();

    public static List<FanChannelConfig> CreateDefaultChannels()
    {
        var list = new List<FanChannelConfig>(FanLogicLimits.MaxFanChannels);
        for (int i = 0; i < FanLogicLimits.MaxFanChannels; i++)
        {
            list.Add(new FanChannelConfig
            {
                ChannelIndex = i,
                Name = $"Fan {i + 1}",
                Show = true,
                Mode = FanChannelMode.Disabled,
                Controllers = FanChannelConfig.CreateDefaultControllers(),
            });
        }

        return list;
    }

    public static List<NamedTemperatureChannel> CreateDefaultTemps() =>
    [
        new() { Id = "cpu", DisplayName = "CPU Package", Source = TemperatureSourceKind.CpuPackage, Show = true },
        new() { Id = "cpu_core", DisplayName = "CPU Core Max", Source = TemperatureSourceKind.CpuCoreMax, Show = true },
        new() { Id = "gpu", DisplayName = "GPU Edge", Source = TemperatureSourceKind.GpuEdge, Show = true },
        new() { Id = "gpu_j", DisplayName = "GPU Junction", Source = TemperatureSourceKind.GpuJunction, Show = true },
        new() { Id = "gpu_hs", DisplayName = "GPU Hotspot", Source = TemperatureSourceKind.GpuHotspot, Show = false },
        new() { Id = "ambient", DisplayName = "Ambient", Source = TemperatureSourceKind.Ambient, Show = true },
    ];
}

public sealed class OverviewSettings
{
    public List<string> HiddenSeries { get; set; } = new();
    public bool DefaultsSeeded { get; set; }
}

public sealed class LogSettings
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 10;
}

public sealed class GpuFanSettings
{
    public bool SoftwareControl { get; set; }
    public float MinPwmPercent { get; set; } = 25f;
    public FanCurveDefinition Curve { get; set; } = CreateDefaultCurve();

    public static FanCurveDefinition CreateDefaultCurve() => new()
    {
        Name = "GPU",
        Kind = FanCurveKind.Linear,
        Points = new List<FanCurvePointDto>
        {
            new() { TemperatureCelsius = 35f, PwmPercent = 25f },
            new() { TemperatureCelsius = 55f, PwmPercent = 40f },
            new() { TemperatureCelsius = 70f, PwmPercent = 65f },
            new() { TemperatureCelsius = 82f, PwmPercent = 100f },
        },
    };
}

public sealed class NamedTemperatureChannel
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public TemperatureSourceKind Source { get; set; }
    public bool Show { get; set; } = true;
}

public static class FanProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HwFanControl",
            "profile.json");

    public static FanProfile LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<FanProfile>(json, JsonOptions);
                if (profile is not null)
                {
                    Normalize(profile);
                    return profile;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("FanProfile load failed: {0}", ex.Message);
        }

        var created = FanProfile.CreateDefault();
        Save(created, path);
        return created;
    }

    public static void Save(FanProfile profile, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(profile, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static void Normalize(FanProfile profile)
    {
        while (profile.Channels.Count < FanLogicLimits.MaxFanChannels)
        {
            int i = profile.Channels.Count;
            profile.Channels.Add(new FanChannelConfig
            {
                ChannelIndex = i,
                Name = $"Fan {i + 1}",
                Controllers = FanChannelConfig.CreateDefaultControllers(),
            });
        }

        for (int i = 0; i < profile.Channels.Count; i++)
        {
            var ch = profile.Channels[i];
            ch.ChannelIndex = i;
            if (ch.Controllers is null || ch.Controllers.Length == 0)
                ch.Controllers = FanChannelConfig.CreateDefaultControllers();
            ch.Safety ??= new ChannelSafetyOptions();
        }

        if (profile.TemperatureChannels is null || profile.TemperatureChannels.Count == 0)
            profile.TemperatureChannels = FanProfile.CreateDefaultTemps();
    }
}
