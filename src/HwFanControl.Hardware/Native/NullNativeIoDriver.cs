namespace HwFanControl.Hardware.Native;

/// <summary>No-op driver used when no ring-0 helper is installed (default for v1).</summary>
public sealed class NullNativeIoDriver : INativeIoDriver
{
    public bool IsOpen => false;

    public bool TryOpen(string devicePath) => false;

    public bool TryReadMsr(uint index, out ulong value)
    {
        value = 0;
        return false;
    }

    public bool TryWriteMsr(uint index, ulong value) => false;

    public bool TryReadIoPortByte(ushort port, out byte value)
    {
        value = 0;
        return false;
    }

    public bool TryWriteIoPortByte(ushort port, byte value) => false;

    public void Dispose()
    {
        // Nothing to release.
    }
}
