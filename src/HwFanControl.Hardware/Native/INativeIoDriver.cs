namespace HwFanControl.Hardware.Native;

/// <summary>
/// Abstraction over a kernel helper driver (WinRing0-style) for MSR and SuperIO port I/O.
/// v1 ships <see cref="NullNativeIoDriver"/>; LibreHardwareMonitor covers sensors instead.
/// </summary>
public interface INativeIoDriver : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Opens the driver device (e.g. <c>\\.\WinRing0_1_2_0</c>).</summary>
    bool TryOpen(string devicePath);

    bool TryReadMsr(uint index, out ulong value);
    bool TryWriteMsr(uint index, ulong value);

    bool TryReadIoPortByte(ushort port, out byte value);
    bool TryWriteIoPortByte(ushort port, byte value);
}
