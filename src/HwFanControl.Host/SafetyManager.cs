using HwFanControl.Core.Safety;
using HwFanControl.Hardware;

namespace HwFanControl.Host;

/// <summary>
/// Host wrapper: process fail-safe hooks + optional convenience over <see cref="PwmSafetyFilter"/>.
/// Core logic lives in HwFanControl.Core.Safety.
/// </summary>
public sealed class SafetyManager : IDisposable
{
    private readonly HardwareMonitor _monitor;
    private readonly FailSafeHooks _hooks;
    private bool _disposed;

    public SafetyManager(HardwareMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _hooks = new FailSafeHooks(_monitor.ReleaseControlToBios);
    }

    public void RegisterFailSafeHooks() => _hooks.Register(attachConsoleEvents: true);

    public void EmergencyRestoreBiosControl() => _hooks.EmergencyRestore();

    public void Dispose()
    {
        if (_disposed)
            return;
        _hooks.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
