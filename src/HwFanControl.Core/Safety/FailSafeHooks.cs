using System.Runtime.InteropServices;

namespace HwFanControl.Core.Safety;

/// <summary>
/// Registers process-lifetime hooks that invoke BIOS/auto restore when the app exits unexpectedly.
/// </summary>
public sealed class FailSafeHooks : IDisposable
{
    private readonly Action _restoreBios;
    private readonly object _gate = new();
    private bool _registered;
    private bool _restored;
    private bool _disposed;
    private ConsoleCtrlDelegate? _ctrlHandler;
    private bool _useConsoleEvents;

    public FailSafeHooks(Action restoreBios)
    {
        _restoreBios = restoreBios ?? throw new ArgumentNullException(nameof(restoreBios));
    }

    public void Register(bool attachConsoleEvents = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
            return;

        _ctrlHandler = OnConsoleCtrl;
        SetConsoleCtrlHandler(_ctrlHandler, add: true);

        _useConsoleEvents = attachConsoleEvents;
        if (_useConsoleEvents)
        {
            try
            {
                Console.CancelKeyPress += OnCancelKeyPress;
            }
            catch
            {
                _useConsoleEvents = false;
            }
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        _registered = true;
    }

    public void EmergencyRestore()
    {
        lock (_gate)
        {
            if (_restored)
                return;

            try
            {
                _restoreBios();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("FailSafeHooks restore failed: {0}", ex.Message);
            }
            finally
            {
                _restored = true;
            }
        }
    }

    /// <summary>Allows a new control session after intentional restore+reopen.</summary>
    public void ResetRestoredFlag()
    {
        lock (_gate)
            _restored = false;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        EmergencyRestore();
    }

    private void OnProcessExit(object? sender, EventArgs e) => EmergencyRestore();

    private bool OnConsoleCtrl(CtrlType ctrlType)
    {
        switch (ctrlType)
        {
            case CtrlType.CtrlCEvent:
            case CtrlType.CtrlBreakEvent:
            case CtrlType.CtrlCloseEvent:
            case CtrlType.CtrlLogoffEvent:
            case CtrlType.CtrlShutdownEvent:
                EmergencyRestore();
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_registered)
        {
            if (_useConsoleEvents)
            {
                try { Console.CancelKeyPress -= OnCancelKeyPress; } catch { /* ignore */ }
            }

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            if (_ctrlHandler is not null)
                SetConsoleCtrlHandler(_ctrlHandler, add: false);
        }

        EmergencyRestore();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private enum CtrlType : uint
    {
        CtrlCEvent = 0,
        CtrlBreakEvent = 1,
        CtrlCloseEvent = 2,
        CtrlLogoffEvent = 5,
        CtrlShutdownEvent = 6,
    }

    private delegate bool ConsoleCtrlDelegate(CtrlType ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, bool add);
}
