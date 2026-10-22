namespace HwFanControl.Core.Curves;

/// <summary>Runtime configuration for one motherboard fan channel.</summary>
public sealed class FanChannelConfig
{
    public int ChannelIndex { get; set; }
    public string Name { get; set; } = "Fan";
    public bool Show { get; set; } = true;
    public FanChannelMode Mode { get; set; } = FanChannelMode.Disabled;
    public float ManualPwmPercent { get; set; } = 40f;
    public TemperatureSourceKind TemperatureSource { get; set; } = TemperatureSourceKind.CpuPackage;
    public int AveragingWindowSeconds { get; set; } = 5;
    public bool ApplyOnStartup { get; set; }
    public ChannelSafetyOptions Safety { get; set; } = new();

    /// <summary>
    /// Up to 4 independent controllers. For single-source mode, only [0] is used.
    /// For <see cref="TemperatureSourceKind.MultipleControllers"/>, enabled entries are max-mapped.
    /// </summary>
    public FanControllerSlot[] Controllers { get; set; } = CreateDefaultControllers();

    public static FanControllerSlot[] CreateDefaultControllers()
    {
        var slots = new FanControllerSlot[FanLogicLimits.MaxCurvesPerChannel];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new FanControllerSlot
            {
                Enabled = i == 0,
                TemperatureSource = TemperatureSourceKind.CpuPackage,
                Curve = FanCurvePresets.DefaultQuiet(),
            };
        }

        return slots;
    }
}

public sealed class FanControllerSlot
{
    public bool Enabled { get; set; }
    public TemperatureSourceKind TemperatureSource { get; set; } = TemperatureSourceKind.CpuPackage;
    public FanCurveDefinition Curve { get; set; } = FanCurvePresets.DefaultQuiet();
}

/// <summary>Serializable curve points (Temp °C → PWM %).</summary>
public sealed class FanCurveDefinition
{
    public string Name { get; set; } = "Default";
    public FanCurveKind Kind { get; set; } = FanCurveKind.Linear;
    public List<FanCurvePointDto> Points { get; set; } = new();
}

public sealed class FanCurvePointDto
{
    public float TemperatureCelsius { get; set; }
    public float PwmPercent { get; set; }
}

public static class FanLogicLimits
{
    public const int MaxCurvesPerChannel = 4;
    public const int MaxFanChannels = 8;
}

public static class FanCurvePresets
{
    public static FanCurveDefinition DefaultQuiet() => new()
    {
        Name = "Quiet",
        Kind = FanCurveKind.Linear,
        Points =
        [
            new FanCurvePointDto { TemperatureCelsius = 30, PwmPercent = 0 },
            new FanCurvePointDto { TemperatureCelsius = 45, PwmPercent = 40 },
            new FanCurvePointDto { TemperatureCelsius = 65, PwmPercent = 70 },
            new FanCurvePointDto { TemperatureCelsius = 80, PwmPercent = 100 },
        ],
    };
}
