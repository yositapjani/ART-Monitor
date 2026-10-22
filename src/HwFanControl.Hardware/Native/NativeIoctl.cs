namespace HwFanControl.Hardware.Native;

/// <summary>
/// Documented IOCTL opcodes for a future WinRing0-style kernel driver.
/// v1 uses LibreHardwareMonitor; these constants reserve the contract for MSR / port I/O.
/// CTL_CODE(DeviceType, Function, Method, Access) layout matches Windows DDK.
/// </summary>
public static class NativeIoctl
{
    public const uint FileDeviceUnknown = 0x00000022;
    public const uint MethodBuffered = 0;
    public const uint FileAnyAccess = 0;

    /// <summary>OLS_TYPE-compatible base used by common ring-0 helper drivers.</summary>
    public const uint OlsType = 40000;

    public static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    public static readonly uint IoctlReadMsr = CtlCode(OlsType, 0x821, MethodBuffered, FileAnyAccess);
    public static readonly uint IoctlWriteMsr = CtlCode(OlsType, 0x822, MethodBuffered, FileAnyAccess);
    public static readonly uint IoctlReadIoPortByte = CtlCode(OlsType, 0x833, MethodBuffered, FileAnyAccess);
    public static readonly uint IoctlWriteIoPortByte = CtlCode(OlsType, 0x836, MethodBuffered, FileAnyAccess);
    public static readonly uint IoctlReadIoPortWord = CtlCode(OlsType, 0x834, MethodBuffered, FileAnyAccess);
    public static readonly uint IoctlWriteIoPortWord = CtlCode(OlsType, 0x837, MethodBuffered, FileAnyAccess);
    public static readonly uint IoctlReadIoPortDword = CtlCode(OlsType, 0x835, MethodBuffered, FileAnyAccess);
    public static readonly uint IoctlWriteIoPortDword = CtlCode(OlsType, 0x838, MethodBuffered, FileAnyAccess);
}
