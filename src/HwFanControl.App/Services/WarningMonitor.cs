using System.Diagnostics;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Threading;

namespace HwFanControl.App.Services;

/// <summary>
/// Evaluates temperature + fan-failure warnings on the UI-thread poll tick.
/// MessageBox is shown non-blocking (BeginInvoke); other actions run inline with cooldowns.
/// </summary>
public sealed class WarningMonitor : IDisposable
{
    private sealed class Channel
    {
        public bool AlarmActive;
        public bool BoxVisible;
        public DateTime LastBoxUtc = DateTime.MinValue;
        public DateTime LastLogUtc = DateTime.MinValue;
        public DateTime LastSoundUtc = DateTime.MinValue;
        public DateTime LastEmailUtc = DateTime.MinValue;
        public DateTime LastProgramUtc = DateTime.MinValue;
    }

    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private WarningSettings _settings;
    private readonly Channel _cpu = new();
    private readonly Channel _gpu = new();
    private readonly Channel _drives = new();
    private readonly Channel _fans = new();
    private readonly Channel _cores = new();
    private readonly Channel _mb = new();
    private readonly Dictionary<string, int> _stallTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fansSeenSpinning = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _fansFired = new(StringComparer.OrdinalIgnoreCase);
    private SoundPlayer? _soundPlayer;
    private bool _disposed;

    public WarningMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _settings = WarningSettingsStore.LoadOrCreate();
    }

    public WarningSettings Settings
    {
        get
        {
            lock (_gate)
                return _settings;
        }
    }

    public string SettingsPath => WarningSettingsStore.DefaultPath;

    public void ReplaceSettings(WarningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WarningSettingsStore.Save(settings);
        lock (_gate)
        {
            _settings = settings.Clone();
            // Fresh config resets alarms so hysteresis re-arms cleanly.
            _cpu.AlarmActive = false;
            _gpu.AlarmActive = false;
            _drives.AlarmActive = false;
            _cores.AlarmActive = false;
            _mb.AlarmActive = false;
            _stallTicks.Clear();
            _fansFired.Clear();
        }
    }

    private WarningSettings CurrentSettings
    {
        get
        {
            lock (_gate)
                return _settings;
        }
    }

    /// <summary>
    /// Call once per UI poll with package/max-core temperature (°C). NaN skips evaluation.
    /// </summary>
    public void Tick(float cpuTempCelsius)
    {
        if (_disposed || float.IsNaN(cpuTempCelsius))
            return;
        var settings = CurrentSettings;
        Evaluate("CPU", cpuTempCelsius, settings.Cpu, _cpu, settings);
    }

    /// <summary>Call once per UI poll with the hottest GPU temperature (°C). NaN skips evaluation.</summary>
    public void TickGpu(float gpuTempCelsius)
    {
        if (_disposed || float.IsNaN(gpuTempCelsius))
            return;
        var settings = CurrentSettings;
        Evaluate("GPU", gpuTempCelsius, settings.Gpu, _gpu, settings);
    }

    /// <summary>Call once per UI poll with the hottest drive temperature (°C). NaN skips evaluation.</summary>
    public void TickDrives(float driveTempCelsius)
    {
        if (_disposed || float.IsNaN(driveTempCelsius))
            return;
        var settings = CurrentSettings;
        Evaluate("Drive", driveTempCelsius, settings.Drives, _drives, settings);
    }

    /// <summary>Call once per UI poll with the hottest single-core temperature (°C). NaN skips evaluation.</summary>
    public void TickCpuCores(float coreTempCelsius)
    {
        if (_disposed || float.IsNaN(coreTempCelsius))
            return;
        var settings = CurrentSettings;
        Evaluate("CPU Core", coreTempCelsius, settings.CpuCores, _cores, settings);
    }

    /// <summary>Call once per UI poll with the hottest mainboard temperature (°C). NaN skips evaluation.</summary>
    public void TickMainboard(float mbTempCelsius)
    {
        if (_disposed || float.IsNaN(mbTempCelsius))
            return;
        var settings = CurrentSettings;
        Evaluate("Mainboard", mbTempCelsius, settings.MbTemp, _mb, settings);
    }

    /// <summary>
    /// Call once per UI poll (1s) with every fan channel's name, commanded PWM % and measured RPM.
    /// A fan counts as failed when it has spun before, is commanded to at least 25%,
    /// and reports 0 RPM for FanFailure.StallSeconds consecutive polls.
    /// </summary>
    public void TickFans(IReadOnlyList<(string Name, float PwmPercent, float Rpm, bool HasRpm)> fans)
    {
        if (_disposed)
            return;

        var settings = CurrentSettings;
        var ff = settings.FanFailure;
        if (!ff.Enabled)
        {
            _stallTicks.Clear();
            return;
        }

        string? failed = null;
        foreach (var fan in fans)
        {
            if (!fan.HasRpm || float.IsNaN(fan.Rpm))
                continue;

            if (fan.Rpm > 0)
            {
                _fansSeenSpinning.Add(fan.Name);
                _stallTicks.Remove(fan.Name);
                _fansFired.Remove(fan.Name);
                continue;
            }

            // 0 RPM: only suspicious for a fan that has spun before and is being driven.
            if (!_fansSeenSpinning.Contains(fan.Name) || fan.PwmPercent < 25f)
            {
                _stallTicks.Remove(fan.Name);
                continue;
            }

            int ticks = _stallTicks.TryGetValue(fan.Name, out int t) ? t + 1 : 1;
            _stallTicks[fan.Name] = ticks;
            if (ticks >= Math.Max(1, ff.StallSeconds) && !_fansFired.Contains(fan.Name))
            {
                _fansFired.Add(fan.Name);
                failed = fan.Name;
            }
        }

        if (failed is null)
            return;

        DateTime now = DateTime.UtcNow;
        string msg = $"Fan failure: {failed} is not spinning (0 RPM).";
        if (ff.DisplayMessageBox)
            TryMessageBox(_fans, msg, "HwFanControl — Fan Failure Warning", settings.Repeat.MessageBoxMinutes, now);
        if (ff.WriteToLogfile)
            TryLog(_fans, msg, settings.Repeat.WriteLogfileMinutes, now);
        if (ff.PlaySound)
            TrySound(_fans, settings.Cpu, settings.Repeat.PlaySoundMinutes, now);
    }

    private void Evaluate(string label, float tempCelsius, SensorWarningConfig cfg, Channel ch, WarningSettings settings)
    {
        float clearBelow = cfg.HysteresisEnabled
            ? cfg.ThresholdCelsius - cfg.HysteresisCelsius
            : cfg.ThresholdCelsius;

        bool nowActive = ch.AlarmActive
            ? tempCelsius >= clearBelow
            : tempCelsius >= cfg.ThresholdCelsius;
        ch.AlarmActive = nowActive;

        if (!nowActive)
            return;

        DateTime now = DateTime.UtcNow;
        if (cfg.DisplayMessageBox)
            TryMessageBox(ch,
                $"{label} temperature {tempCelsius:0.0} °C exceeds warning threshold {cfg.ThresholdCelsius:0.0} °C.",
                $"HwFanControl — {label} Temperature Warning",
                settings.Repeat.MessageBoxMinutes, now);
        if (cfg.WriteToLogfile)
            TryLog(ch, $"{label} {tempCelsius:0.0} °C >= threshold {cfg.ThresholdCelsius:0.0} °C",
                settings.Repeat.WriteLogfileMinutes, now);
        if (cfg.PlaySound)
            TrySound(ch, cfg, settings.Repeat.PlaySoundMinutes, now);
        if (cfg.SendEmail)
            TryEmail(ch, cfg, label, tempCelsius, settings.Repeat.SendEmailMinutes, now);
        if (cfg.RunProgram)
            TryRunProgram(ch, cfg, settings.Repeat.RunProgramMinutes, now);
    }

    private void TryMessageBox(Channel ch, string message, string title, int intervalMinutes, DateTime now)
    {
        if (ch.BoxVisible)
            return;
        if (!CooldownElapsed(ch.LastBoxUtc, intervalMinutes, now))
            return;

        ch.LastBoxUtc = now;
        ch.BoxVisible = true;
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                MessageBox.Show(
                    message,
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                ch.BoxVisible = false;
            }
        });
    }

    private void TryLog(Channel ch, string message, int intervalMinutes, DateTime now)
    {
        if (!CooldownElapsed(ch.LastLogUtc, intervalMinutes, now))
            return;

        ch.LastLogUtc = now;
        try
        {
            string path = WarningSettingsStore.DefaultLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Warning log write failed: {0}", ex.Message);
        }
    }

    private void TrySound(Channel ch, SensorWarningConfig cfg, int intervalMinutes, DateTime now)
    {
        if (!CooldownElapsed(ch.LastSoundUtc, intervalMinutes, now))
            return;
        if (string.IsNullOrWhiteSpace(cfg.SoundPath) || !File.Exists(cfg.SoundPath))
            return;

        ch.LastSoundUtc = now;
        try
        {
            _soundPlayer?.Dispose();
            _soundPlayer = new SoundPlayer(cfg.SoundPath);
            _soundPlayer.Play();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Warning sound failed: {0}", ex.Message);
        }
    }

    private void TryEmail(Channel ch, SensorWarningConfig cfg, string label, float temp, int intervalMinutes, DateTime now)
    {
        if (!CooldownElapsed(ch.LastEmailUtc, intervalMinutes, now))
            return;
        if (string.IsNullOrWhiteSpace(cfg.EmailTo))
            return;

        ch.LastEmailUtc = now;
        // SMTP not configured — queue intent to logfile instead of sending.
        try
        {
            string path = WarningSettingsStore.DefaultLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [email stub] would send to {cfg.EmailTo}: " +
                $"{label} {temp:0.0} °C (TODO: SMTP){Environment.NewLine}";
            File.AppendAllText(path, line);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Warning email stub failed: {0}", ex.Message);
        }
    }

    private void TryRunProgram(Channel ch, SensorWarningConfig cfg, int intervalMinutes, DateTime now)
    {
        if (!CooldownElapsed(ch.LastProgramUtc, intervalMinutes, now))
            return;
        if (string.IsNullOrWhiteSpace(cfg.ProgramPath) || !File.Exists(cfg.ProgramPath))
            return;

        ch.LastProgramUtc = now;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cfg.ProgramPath,
                Arguments = cfg.ProgramArguments ?? "",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Warning RunProgram failed: {0}", ex.Message);
        }
    }

    private static bool CooldownElapsed(DateTime lastUtc, int intervalMinutes, DateTime now)
    {
        if (lastUtc == DateTime.MinValue)
            return true;
        int minutes = Math.Max(0, intervalMinutes);
        return now - lastUtc >= TimeSpan.FromMinutes(minutes);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _soundPlayer?.Dispose();
        _soundPlayer = null;
    }
}
