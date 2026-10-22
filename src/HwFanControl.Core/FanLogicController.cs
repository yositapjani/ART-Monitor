using HwFanControl.Core.Curves;
using HwFanControl.Core.Math;
using HwFanControl.Core.Safety;
using HwFanControl.Core.Sensors;
using HwFanControl.Core.Synthetic;

namespace HwFanControl.Core;

/// <summary>
/// Fan curve execution engine: Manual/Software modes, synthetic temps, rolling averages,
/// hysteresis, and multi-controller max-mapping (up to 4 curves per channel).
/// </summary>
public sealed class FanLogicController
{
    public const int MaxCurvesPerChannel = FanLogicLimits.MaxCurvesPerChannel;
    public const int MaxFanChannels = FanLogicLimits.MaxFanChannels;

    private readonly SyntheticSensorEngine _synthetic = new();
    private readonly FanChannelConfig[] _channels = new FanChannelConfig[MaxFanChannels];
    private readonly RollingAverageBuffer?[] _avgCpuGpu = new RollingAverageBuffer[MaxFanChannels];
    private readonly RollingAverageBuffer?[] _maxCpuGpu = new RollingAverageBuffer[MaxFanChannels];
    private readonly PwmSafetyFilter _safety = new();
    private readonly float[] _rawPwm = new float[MaxFanChannels];
    private readonly float[] _heldTemp = new float[MaxFanChannels];
    private readonly bool[] _hasHeldTemp = new bool[MaxFanChannels];
    private DateTimeOffset _lastEvaluateUtc = DateTimeOffset.MinValue;

    public FanLogicController()
    {
        for (int i = 0; i < MaxFanChannels; i++)
        {
            _channels[i] = new FanChannelConfig { ChannelIndex = i, Name = $"Fan {i + 1}" };
            _heldTemp[i] = 0f;
        }
    }

    public SyntheticSensorEngine Synthetic => _synthetic;
    public PwmSafetyFilter Safety => _safety;
    public IReadOnlyList<FanChannelConfig> Channels => _channels;

    public FanChannelConfig GetChannel(int index)
    {
        if ((uint)index >= MaxFanChannels)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _channels[index];
    }

    public void SetChannelConfig(FanChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if ((uint)config.ChannelIndex >= MaxFanChannels)
            throw new ArgumentOutOfRangeException(nameof(config), "ChannelIndex out of range.");

        _channels[config.ChannelIndex] = config;
        EnsureAveragers(config.ChannelIndex, config.AveragingWindowSeconds);
        _safety.ResetChannel(config.ChannelIndex);
        _hasHeldTemp[config.ChannelIndex] = false;
    }

    public void LoadChannels(IEnumerable<FanChannelConfig> configs)
    {
        foreach (var cfg in configs)
            SetChannelConfig(cfg);
    }

    /// <summary>
    /// Evaluates all channels. Writes guarded PWM into <paramref name="pwmOut"/>.
    /// Returns count of channels written.
    /// </summary>
    public int Evaluate(TelemetryBuffer telemetry, Span<float> pwmOut, DateTimeOffset nowUtc)
    {
        float dt = 1f;
        if (_lastEvaluateUtc != DateTimeOffset.MinValue)
            dt = (float)System.Math.Clamp((nowUtc - _lastEvaluateUtc).TotalSeconds, 0.05, 5.0);
        _lastEvaluateUtc = nowUtc;

        int written = 0;
        for (int ch = 0; ch < MaxFanChannels && ch < pwmOut.Length; ch++)
        {
            var cfg = _channels[ch];
            float raw = EvaluateChannelRaw(cfg, telemetry, ch);
            _rawPwm[ch] = raw;

            float guarded = cfg.Mode == FanChannelMode.Disabled
                ? _safety.GetLastOutput(ch)
                : _safety.Apply(ch, raw, cfg.Safety, nowUtc, dt);

            // Disabled: do not change output (caller skips Write). Keep last for UI.
            if (cfg.Mode == FanChannelMode.Disabled)
                guarded = float.NaN;

            pwmOut[ch] = guarded;
            written++;
        }

        return written;
    }

    private float EvaluateChannelRaw(FanChannelConfig cfg, TelemetryBuffer telemetry, int channel)
    {
        switch (cfg.Mode)
        {
            case FanChannelMode.Disabled:
                return 0f;
            case FanChannelMode.Manual:
                return System.Math.Clamp(cfg.ManualPwmPercent, 0f, 100f);
            case FanChannelMode.Software:
                return EvaluateSoftware(cfg, telemetry, channel);
            default:
                return 0f;
        }
    }

    private float EvaluateSoftware(FanChannelConfig cfg, TelemetryBuffer telemetry, int channel)
    {
        if (cfg.TemperatureSource == TemperatureSourceKind.MultipleControllers)
        {
            float maxPwm = 0f;
            bool any = false;
            foreach (var slot in cfg.Controllers)
            {
                if (slot is null || !slot.Enabled)
                    continue;

                if (!TryResolveTemperature(slot.TemperatureSource, telemetry, channel, cfg, out float temp))
                    continue;

                temp = ApplyTempHysteresis(channel, temp, cfg.Safety.TemperatureHysteresisCelsius);
                float pwm = CurveEvaluator.Evaluate(slot.Curve, temp);
                if (pwm > maxPwm)
                    maxPwm = pwm;
                any = true;
            }

            return any ? maxPwm : 0f;
        }

        // Single controller: use controllers[0] curve + channel TemperatureSource.
        var primary = cfg.Controllers.Length > 0 ? cfg.Controllers[0] : null;
        var curve = primary?.Curve ?? FanCurvePresets.DefaultQuiet();
        if (!TryResolveTemperature(cfg.TemperatureSource, telemetry, channel, cfg, out float temperature))
            return 0f;

        temperature = ApplyTempHysteresis(channel, temperature, cfg.Safety.TemperatureHysteresisCelsius);
        return CurveEvaluator.Evaluate(curve, temperature);
    }

    private float ApplyTempHysteresis(int channel, float temperature, float hysteresisCelsius)
    {
        if (hysteresisCelsius <= 0f)
        {
            _heldTemp[channel] = temperature;
            _hasHeldTemp[channel] = true;
            return temperature;
        }

        if (!_hasHeldTemp[channel])
        {
            _heldTemp[channel] = temperature;
            _hasHeldTemp[channel] = true;
            return temperature;
        }

        float held = _heldTemp[channel];
        if (System.Math.Abs(temperature - held) >= hysteresisCelsius)
        {
            _heldTemp[channel] = temperature;
            return temperature;
        }

        return held;
    }

    private bool TryResolveTemperature(
        TemperatureSourceKind source,
        TelemetryBuffer telemetry,
        int channel,
        FanChannelConfig cfg,
        out float temperature)
    {
        temperature = 0f;
        EnsureAveragers(channel, cfg.AveragingWindowSeconds);

        switch (source)
        {
            case TemperatureSourceKind.CpuPackage:
                return telemetry.TryGet(SensorSlot.CpuPackageTemp, out temperature)
                       || telemetry.TryGet(SensorSlot.CpuCoreTempMax, out temperature);
            case TemperatureSourceKind.CpuCoreMax:
                return telemetry.TryGet(SensorSlot.CpuCoreTempMax, out temperature)
                       || telemetry.TryGet(SensorSlot.CpuPackageTemp, out temperature);
            case TemperatureSourceKind.GpuEdge:
                return telemetry.TryGet(SensorSlot.GpuEdgeTemp, out temperature);
            case TemperatureSourceKind.GpuJunction:
                return telemetry.TryGet(SensorSlot.GpuJunctionTemp, out temperature)
                       || telemetry.TryGet(SensorSlot.GpuEdgeTemp, out temperature);
            case TemperatureSourceKind.GpuHotspot:
                return telemetry.TryGet(SensorSlot.GpuHotspotTemp, out temperature)
                       || telemetry.TryGet(SensorSlot.GpuJunctionTemp, out temperature)
                       || telemetry.TryGet(SensorSlot.GpuEdgeTemp, out temperature);
            case TemperatureSourceKind.Ambient:
                return telemetry.TryGet(SensorSlot.AmbientTemp, out temperature);
            case TemperatureSourceKind.AvgCpuGpu:
            {
                if (!TryGetCpu(telemetry, out float cpu) || !TryGetGpu(telemetry, out float gpu))
                    return false;
                temperature = (cpu + gpu) * 0.5f;
                return true;
            }
            case TemperatureSourceKind.MaxCpuGpu:
            {
                if (!TryGetCpu(telemetry, out float cpu) || !TryGetGpu(telemetry, out float gpu))
                    return false;
                temperature = System.Math.Max(cpu, gpu);
                return true;
            }
            case TemperatureSourceKind.AvgCpuGpuFiltered:
            {
                if (!TryGetCpu(telemetry, out float cpu) || !TryGetGpu(telemetry, out float gpu))
                    return false;
                float avg = (cpu + gpu) * 0.5f;
                _avgCpuGpu[channel]!.Push(avg);
                temperature = _avgCpuGpu[channel]!.Average;
                return true;
            }
            case TemperatureSourceKind.MaxCpuGpuFiltered:
            {
                if (!TryGetCpu(telemetry, out float cpu) || !TryGetGpu(telemetry, out float gpu))
                    return false;
                float max = System.Math.Max(cpu, gpu);
                _maxCpuGpu[channel]!.Push(max);
                temperature = _maxCpuGpu[channel]!.Average;
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryGetCpu(TelemetryBuffer t, out float v) =>
        t.TryGet(SensorSlot.CpuPackageTemp, out v) || t.TryGet(SensorSlot.CpuCoreTempMax, out v);

    private static bool TryGetGpu(TelemetryBuffer t, out float v) =>
        t.TryGet(SensorSlot.GpuEdgeTemp, out v)
        || t.TryGet(SensorSlot.GpuJunctionTemp, out v)
        || t.TryGet(SensorSlot.GpuHotspotTemp, out v);

    private void EnsureAveragers(int channel, int windowSeconds)
    {
        int capacity = System.Math.Clamp(windowSeconds, 5, 30);
        if (_avgCpuGpu[channel] is null || _avgCpuGpu[channel]!.Capacity != capacity)
            _avgCpuGpu[channel] = new RollingAverageBuffer(capacity);
        if (_maxCpuGpu[channel] is null || _maxCpuGpu[channel]!.Capacity != capacity)
            _maxCpuGpu[channel] = new RollingAverageBuffer(capacity);
    }

    // ---- Legacy scaffold API kept for Host compatibility ----

    public void ConfigureChannel(
        int channel,
        ReadOnlySpan<FanCurve> curves,
        int averagingWindowSeconds = 5,
        HysteresisSettings? hysteresis = null)
    {
        if ((uint)channel >= MaxFanChannels)
            throw new ArgumentOutOfRangeException(nameof(channel));

        var cfg = _channels[channel];
        cfg.Mode = FanChannelMode.Software;
        cfg.AveragingWindowSeconds = averagingWindowSeconds;
        cfg.TemperatureSource = curves.Length > 1
            ? TemperatureSourceKind.MultipleControllers
            : TemperatureSourceKind.CpuPackage;

        if (hysteresis is not null)
        {
            cfg.Safety.TemperatureHysteresisCelsius = 1.5f;
        }

        for (int i = 0; i < MaxCurvesPerChannel; i++)
        {
            cfg.Controllers[i].Enabled = i < curves.Length;
            if (i < curves.Length)
            {
                var c = curves[i];
                cfg.Controllers[i].TemperatureSource = MapSlotToSource(c.TemperatureSource);
                cfg.Controllers[i].Curve = new FanCurveDefinition
                {
                    Name = c.Name,
                    Kind = c.Kind,
                    Points = c.Points.Select(p => new FanCurvePointDto
                    {
                        TemperatureCelsius = p.TemperatureCelsius,
                        PwmPercent = p.PwmPercent,
                    }).ToList(),
                };
            }
        }

        SetChannelConfig(cfg);
    }

    private static TemperatureSourceKind MapSlotToSource(SensorSlot slot) => slot switch
    {
        SensorSlot.CpuPackageTemp => TemperatureSourceKind.CpuPackage,
        SensorSlot.CpuCoreTempMax => TemperatureSourceKind.CpuCoreMax,
        SensorSlot.GpuEdgeTemp => TemperatureSourceKind.GpuEdge,
        SensorSlot.GpuJunctionTemp => TemperatureSourceKind.GpuJunction,
        SensorSlot.GpuHotspotTemp => TemperatureSourceKind.GpuHotspot,
        SensorSlot.AmbientTemp => TemperatureSourceKind.Ambient,
        _ => TemperatureSourceKind.CpuPackage,
    };
}
