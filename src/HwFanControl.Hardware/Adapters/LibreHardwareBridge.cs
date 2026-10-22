using HwFanControl.Core.Sensors;
using HwFanControl.Hardware.Cpu;
using LibreHardwareMonitor.Hardware;

namespace HwFanControl.Hardware.Adapters;

/// <summary>
/// Maps LibreHardwareMonitor sensors into stable <see cref="SensorSlot"/> indices once at scan time.
/// Hot-path polling only reads the cached <see cref="ISensor"/> / <see cref="IControl"/> references.
/// </summary>
internal sealed class LibreHardwareBridge
{
    private readonly ISensor?[] _sensors = new ISensor?[(int)SensorSlot.Capacity];
    private readonly IControl?[] _controls = new IControl?[8];
    private readonly UpdateVisitor _visitor = new();
    private int _fanChannelCount;
    private int _controlCount;

    public int FanChannelCount => _fanChannelCount;
    public int ControlCount => _controlCount;

    public void Bind(Computer computer)
    {
        Array.Clear(_sensors, 0, _sensors.Length);
        Array.Clear(_controls, 0, _controls.Length);
        _fanChannelCount = 0;
        _controlCount = 0;

        computer.Accept(_visitor);
        computer.Accept(_visitor);

        foreach (IHardware hardware in computer.Hardware)
            VisitHardware(hardware);
    }

    private void VisitHardware(IHardware hardware)
    {
        hardware.Update();

        switch (hardware.HardwareType)
        {
            case HardwareType.Cpu:
                BindCpu(hardware);
                break;
            case HardwareType.GpuNvidia:
            case HardwareType.GpuAmd:
            case HardwareType.GpuIntel:
                BindGpu(hardware);
                break;
            case HardwareType.Motherboard:
            case HardwareType.SuperIO:
                BindMotherboardOrSuperIo(hardware);
                break;
        }

        foreach (IHardware sub in hardware.SubHardware)
            VisitHardware(sub);
    }

    private void BindCpu(IHardware hardware)
    {
        ISensor? package = null;
        float coreMax = float.MinValue;
        ISensor? coreMaxSensor = null;

        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature)
                continue;

            string name = sensor.Name;
            if (ContainsIgnoreCase(name, "Package") || ContainsIgnoreCase(name, "Tctl") || ContainsIgnoreCase(name, "CCD"))
            {
                package ??= sensor;
                if (ContainsIgnoreCase(name, "Package") || ContainsIgnoreCase(name, "Tctl"))
                    package = sensor;
            }

            if (ContainsIgnoreCase(name, "Core") && sensor.Value is float v && v > coreMax)
            {
                coreMax = v;
                coreMaxSensor = sensor;
            }
        }

        SetSensor(SensorSlot.CpuPackageTemp, package ?? coreMaxSensor);
        SetSensor(SensorSlot.CpuCoreTempMax, coreMaxSensor ?? package);
    }

    private void BindGpu(IHardware hardware)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature)
                continue;

            string name = sensor.Name;
            if (ContainsIgnoreCase(name, "Hot Spot") || ContainsIgnoreCase(name, "Hotspot"))
                SetSensor(SensorSlot.GpuHotspotTemp, sensor);
            else if (ContainsIgnoreCase(name, "Junction") || ContainsIgnoreCase(name, "Tj"))
                SetSensor(SensorSlot.GpuJunctionTemp, sensor);
            else if (ContainsIgnoreCase(name, "Core") || ContainsIgnoreCase(name, "Edge") || ContainsIgnoreCase(name, "GPU"))
                SetIfEmpty(SensorSlot.GpuEdgeTemp, sensor);
        }

        // Fallback: first GPU temperature if edge not found.
        if (_sensors[(int)SensorSlot.GpuEdgeTemp] is null)
        {
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature)
                {
                    SetSensor(SensorSlot.GpuEdgeTemp, sensor);
                    break;
                }
            }
        }
    }

    private void BindMotherboardOrSuperIo(IHardware hardware)
    {
        foreach (ISensor sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature &&
                (ContainsIgnoreCase(sensor.Name, "Ambient") || ContainsIgnoreCase(sensor.Name, "AUXTIN")))
            {
                SetIfEmpty(SensorSlot.AmbientTemp, sensor);
            }

            if (sensor.SensorType == SensorType.Fan && _fanChannelCount < 8)
            {
                var slot = SensorSlot.FanRpm0 + _fanChannelCount;
                SetSensor(slot, sensor);
                _fanChannelCount++;
            }

            if (sensor.SensorType == SensorType.Control && sensor.Control is not null && _controlCount < 8)
            {
                _controls[_controlCount] = sensor.Control;
                var pwmSlot = SensorSlot.FanPwm0 + _controlCount;
                SetSensor(pwmSlot, sensor);
                _controlCount++;
            }
        }

        foreach (IHardware sub in hardware.SubHardware)
            BindMotherboardOrSuperIo(sub);
    }

    public void PollInto(TelemetryBuffer buffer)
    {
        for (int i = 0; i < _sensors.Length; i++)
        {
            ISensor? sensor = _sensors[i];
            if (sensor is null)
                continue;

            if (sensor.Value is float value)
                buffer.Set((SensorSlot)i, value);
        }
    }

    public void UpdateBoundHardware(Computer computer) => computer.Accept(_visitor);

    public bool TrySetFanPwm(int channel, float percent)
    {
        if ((uint)channel >= (uint)_controlCount)
            return false;

        IControl? control = _controls[channel];
        if (control is null)
            return false;

        float clamped = Math.Clamp(percent, 0f, 100f);
        control.SetSoftware(clamped);
        return true;
    }

    public void ReleaseAllControlsToDefault()
    {
        for (int i = 0; i < _controlCount; i++)
        {
            try
            {
                _controls[i]?.SetDefault();
            }
            catch
            {
                // Best-effort BIOS restore; never throw from fail-safe path.
            }
        }
    }

    private void SetSensor(SensorSlot slot, ISensor? sensor)
    {
        if (sensor is null)
            return;
        _sensors[(int)slot] = sensor;
    }

    private void SetIfEmpty(SensorSlot slot, ISensor sensor)
    {
        if (_sensors[(int)slot] is null)
            _sensors[(int)slot] = sensor;
    }

    private static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
