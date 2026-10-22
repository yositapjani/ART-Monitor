using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace HwFanControl.Hardware.Native;

/// <summary>
/// P/Invoke-backed IOCTL client for a WinRing0-compatible driver.
/// Not used by default in v1; kept ready for Phase 2+ SuperIO/MSR direct access.
/// </summary>
public sealed class WinRing0NativeIoDriver : INativeIoDriver
{
    private SafeFileHandle? _handle;
    private bool _disposed;

    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };

    public bool TryOpen(string devicePath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);

        CloseHandleOnly();

        var handle = Win32Native.CreateFile(
            devicePath,
            Win32Native.GenericRead | Win32Native.GenericWrite,
            Win32Native.FileShareRead | Win32Native.FileShareWrite,
            IntPtr.Zero,
            Win32Native.OpenExisting,
            Win32Native.FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return false;
        }

        _handle = handle;
        return true;
    }

    public bool TryReadMsr(uint index, out ulong value)
    {
        value = 0;
        if (!IsOpen)
            return false;

        try
        {
            Span<byte> input = stackalloc byte[4];
            Span<byte> output = stackalloc byte[8];
            BitConverter.TryWriteBytes(input, index);

            if (!DeviceIoControl(NativeIoctl.IoctlReadMsr, input, output, out _))
                return false;

            value = BitConverter.ToUInt64(output);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public bool TryWriteMsr(uint index, ulong value)
    {
        if (!IsOpen)
            return false;

        try
        {
            Span<byte> input = stackalloc byte[12];
            BitConverter.TryWriteBytes(input[..4], index);
            BitConverter.TryWriteBytes(input[4..], value);
            Span<byte> output = stackalloc byte[1];
            return DeviceIoControl(NativeIoctl.IoctlWriteMsr, input, output, out _);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public bool TryReadIoPortByte(ushort port, out byte value)
    {
        value = 0;
        if (!IsOpen)
            return false;

        try
        {
            Span<byte> input = stackalloc byte[4];
            BitConverter.TryWriteBytes(input, (uint)port);
            Span<byte> output = stackalloc byte[4];

            if (!DeviceIoControl(NativeIoctl.IoctlReadIoPortByte, input, output, out _))
                return false;

            value = output[0];
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public bool TryWriteIoPortByte(ushort port, byte value)
    {
        if (!IsOpen)
            return false;

        try
        {
            Span<byte> input = stackalloc byte[5];
            BitConverter.TryWriteBytes(input[..4], (uint)port);
            input[4] = value;
            Span<byte> output = stackalloc byte[1];
            return DeviceIoControl(NativeIoctl.IoctlWriteIoPortByte, input, output, out _);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    private bool DeviceIoControl(uint code, Span<byte> input, Span<byte> output, out uint bytesReturned)
    {
        bytesReturned = 0;
        if (_handle is null || _handle.IsInvalid)
            return false;

        unsafe
        {
            fixed (byte* inPtr = input)
            fixed (byte* outPtr = output)
            {
                bool ok = Win32Native.DeviceIoControl(
                    _handle,
                    code,
                    (IntPtr)inPtr,
                    (uint)input.Length,
                    (IntPtr)outPtr,
                    (uint)output.Length,
                    out bytesReturned,
                    IntPtr.Zero);

                return ok;
            }
        }
    }

    private void CloseHandleOnly()
    {
        if (_handle is null)
            return;

        _handle.Dispose();
        _handle = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CloseHandleOnly();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
