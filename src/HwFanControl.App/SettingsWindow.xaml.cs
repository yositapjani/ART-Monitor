using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using HwFanControl.App.Services;
using Microsoft.Win32;

namespace HwFanControl.App;

/// <summary>
/// Argus-style settings window. Warning pages (CPU / GPU / Drives / Fans) are fully
/// wired to WarningSettings; the remaining pages are placeholders for now.
/// </summary>
public partial class SettingsWindow : Window
{
    public WarningSettings? Result { get; private set; }

    private readonly AppSettingsSnapshot? _app;

    public AppSettingsSnapshot? AppResult { get; private set; }

    public SettingsWindow(WarningSettings settings)
        : this(settings, null)
    {
    }

    public SettingsWindow(WarningSettings settings, AppSettingsSnapshot? app)
    {
        InitializeComponent();
        _app = app;
        Result = settings.Clone();
        LoadPages(Result);
        if (app is not null)
            LoadAppPages(app);
        ShowPage("warn.cpu", "Sensors: CPU");
        NavCpu.IsSelected = true;
    }

    // ---------- load / save ----------

    private void LoadPages(WarningSettings s)
    {
        LoadSensor(s.Cpu,
            CpuThresholdBox, ChkMessageBox, ChkLogfile, ChkSound, SoundPathBox,
            ChkEmail, EmailBox, ChkRunProgram, ProgramPathBox, ProgramArgsBox,
            ChkHysteresis, HysteresisBox);
        LoadSensor(s.Gpu,
            GpuThresholdBox, GpuChkMessageBox, GpuChkLogfile, GpuChkSound, GpuSoundPathBox,
            GpuChkEmail, GpuEmailBox, GpuChkRunProgram, GpuProgramPathBox, GpuProgramArgsBox,
            GpuChkHysteresis, GpuHysteresisBox);
        LoadSensor(s.Drives,
            DrvThresholdBox, DrvChkMessageBox, DrvChkLogfile, DrvChkSound, DrvSoundPathBox,
            DrvChkEmail, DrvEmailBox, DrvChkRunProgram, DrvProgramPathBox, DrvProgramArgsBox,
            DrvChkHysteresis, DrvHysteresisBox);

        LoadSensor(s.CpuCores,
            CorThresholdBox, CorChkMessageBox, CorChkLogfile, CorChkSound, CorSoundPathBox,
            CorChkEmail, CorEmailBox, CorChkRunProgram, CorProgramPathBox, CorProgramArgsBox,
            CorChkHysteresis, CorHysteresisBox);
        LoadSensor(s.MbTemp,
            MbThresholdBox, MbChkMessageBox, MbChkLogfile, MbChkSound, MbSoundPathBox,
            MbChkEmail, MbEmailBox, MbChkRunProgram, MbProgramPathBox, MbProgramArgsBox,
            MbChkHysteresis, MbHysteresisBox);

        ChkFanEnabled.IsChecked = s.FanFailure.Enabled;
        FanStallBox.Text = s.FanFailure.StallSeconds.ToString(CultureInfo.InvariantCulture);
        ChkFanBox.IsChecked = s.FanFailure.DisplayMessageBox;
        ChkFanLog.IsChecked = s.FanFailure.WriteToLogfile;
        ChkFanSound.IsChecked = s.FanFailure.PlaySound;

        RepeatMessageBox.Text = s.Repeat.MessageBoxMinutes.ToString(CultureInfo.InvariantCulture);
        RepeatLogfile.Text = s.Repeat.WriteLogfileMinutes.ToString(CultureInfo.InvariantCulture);
        RepeatSound.Text = s.Repeat.PlaySoundMinutes.ToString(CultureInfo.InvariantCulture);
        RepeatEmail.Text = s.Repeat.SendEmailMinutes.ToString(CultureInfo.InvariantCulture);
        RepeatProgram.Text = s.Repeat.RunProgramMinutes.ToString(CultureInfo.InvariantCulture);
    }

    private void SavePages(WarningSettings s)
    {
        SaveSensor(s.Cpu,
            CpuThresholdBox, ChkMessageBox, ChkLogfile, ChkSound, SoundPathBox,
            ChkEmail, EmailBox, ChkRunProgram, ProgramPathBox, ProgramArgsBox,
            ChkHysteresis, HysteresisBox);
        SaveSensor(s.Gpu,
            GpuThresholdBox, GpuChkMessageBox, GpuChkLogfile, GpuChkSound, GpuSoundPathBox,
            GpuChkEmail, GpuEmailBox, GpuChkRunProgram, GpuProgramPathBox, GpuProgramArgsBox,
            GpuChkHysteresis, GpuHysteresisBox);
        SaveSensor(s.Drives,
            DrvThresholdBox, DrvChkMessageBox, DrvChkLogfile, DrvChkSound, DrvSoundPathBox,
            DrvChkEmail, DrvEmailBox, DrvChkRunProgram, DrvProgramPathBox, DrvProgramArgsBox,
            DrvChkHysteresis, DrvHysteresisBox);

        SaveSensor(s.CpuCores,
            CorThresholdBox, CorChkMessageBox, CorChkLogfile, CorChkSound, CorSoundPathBox,
            CorChkEmail, CorEmailBox, CorChkRunProgram, CorProgramPathBox, CorProgramArgsBox,
            CorChkHysteresis, CorHysteresisBox);
        SaveSensor(s.MbTemp,
            MbThresholdBox, MbChkMessageBox, MbChkLogfile, MbChkSound, MbSoundPathBox,
            MbChkEmail, MbEmailBox, MbChkRunProgram, MbProgramPathBox, MbProgramArgsBox,
            MbChkHysteresis, MbHysteresisBox);

        s.FanFailure.Enabled = ChkFanEnabled.IsChecked == true;
        s.FanFailure.StallSeconds = ParseInt(FanStallBox.Text, s.FanFailure.StallSeconds);
        s.FanFailure.DisplayMessageBox = ChkFanBox.IsChecked == true;
        s.FanFailure.WriteToLogfile = ChkFanLog.IsChecked == true;
        s.FanFailure.PlaySound = ChkFanSound.IsChecked == true;

        s.Repeat.MessageBoxMinutes = ParseInt(RepeatMessageBox.Text, s.Repeat.MessageBoxMinutes);
        s.Repeat.WriteLogfileMinutes = ParseInt(RepeatLogfile.Text, s.Repeat.WriteLogfileMinutes);
        s.Repeat.PlaySoundMinutes = ParseInt(RepeatSound.Text, s.Repeat.PlaySoundMinutes);
        s.Repeat.SendEmailMinutes = ParseInt(RepeatEmail.Text, s.Repeat.SendEmailMinutes);
        s.Repeat.RunProgramMinutes = ParseInt(RepeatProgram.Text, s.Repeat.RunProgramMinutes);
    }

    private static void LoadSensor(SensorWarningConfig cfg,
        System.Windows.Controls.TextBox threshold, System.Windows.Controls.CheckBox chkBox, System.Windows.Controls.CheckBox chkLog, System.Windows.Controls.CheckBox chkSound, System.Windows.Controls.TextBox soundPath,
        System.Windows.Controls.CheckBox chkEmail, System.Windows.Controls.TextBox email, System.Windows.Controls.CheckBox chkRun, System.Windows.Controls.TextBox progPath, System.Windows.Controls.TextBox progArgs,
        System.Windows.Controls.CheckBox chkHyst, System.Windows.Controls.TextBox hyst)
    {
        threshold.Text = cfg.ThresholdCelsius.ToString("0", CultureInfo.InvariantCulture);
        chkBox.IsChecked = cfg.DisplayMessageBox;
        chkLog.IsChecked = cfg.WriteToLogfile;
        chkSound.IsChecked = cfg.PlaySound;
        soundPath.Text = cfg.SoundPath;
        chkEmail.IsChecked = cfg.SendEmail;
        email.Text = cfg.EmailTo;
        chkRun.IsChecked = cfg.RunProgram;
        progPath.Text = cfg.ProgramPath;
        progArgs.Text = cfg.ProgramArguments;
        chkHyst.IsChecked = cfg.HysteresisEnabled;
        hyst.Text = cfg.HysteresisCelsius.ToString("0", CultureInfo.InvariantCulture);
    }

    private static void SaveSensor(SensorWarningConfig cfg,
        System.Windows.Controls.TextBox threshold, System.Windows.Controls.CheckBox chkBox, System.Windows.Controls.CheckBox chkLog, System.Windows.Controls.CheckBox chkSound, System.Windows.Controls.TextBox soundPath,
        System.Windows.Controls.CheckBox chkEmail, System.Windows.Controls.TextBox email, System.Windows.Controls.CheckBox chkRun, System.Windows.Controls.TextBox progPath, System.Windows.Controls.TextBox progArgs,
        System.Windows.Controls.CheckBox chkHyst, System.Windows.Controls.TextBox hyst)
    {
        cfg.ThresholdCelsius = ParseFloat(threshold.Text, cfg.ThresholdCelsius);
        cfg.DisplayMessageBox = chkBox.IsChecked == true;
        cfg.WriteToLogfile = chkLog.IsChecked == true;
        cfg.PlaySound = chkSound.IsChecked == true;
        cfg.SoundPath = soundPath.Text.Trim();
        cfg.SendEmail = chkEmail.IsChecked == true;
        cfg.EmailTo = email.Text.Trim();
        cfg.RunProgram = chkRun.IsChecked == true;
        cfg.ProgramPath = progPath.Text.Trim();
        cfg.ProgramArguments = progArgs.Text;
        cfg.HysteresisEnabled = chkHyst.IsChecked == true;
        cfg.HysteresisCelsius = ParseFloat(hyst.Text, cfg.HysteresisCelsius);
    }

    private static float ParseFloat(string text, float fallback) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    // ---------- app settings pages ----------

    private void LoadAppPages(AppSettingsSnapshot app)
    {
        LangEnRadio.IsChecked = app.Language == AppLanguage.English;
        LangIdRadio.IsChecked = app.Language == AppLanguage.Indonesian;
        ChkAutoStart.IsChecked = app.AutoStartEnabled;
        ChkCsvLog.IsChecked = app.LoggingEnabled;
        CsvIntervalBox.Text = app.LoggingIntervalSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private AppSettingsSnapshot SaveAppPages()
    {
        return new AppSettingsSnapshot
        {
            Language = LangIdRadio.IsChecked == true ? AppLanguage.Indonesian : AppLanguage.English,
            AutoStartEnabled = ChkAutoStart.IsChecked == true,
            LoggingEnabled = ChkCsvLog.IsChecked == true,
            LoggingIntervalSeconds = ParseInt(CsvIntervalBox.Text, _app?.LoggingIntervalSeconds ?? 10),
        };
    }

    // ---------- navigation ----------

    private void ShowPage(string tag, string header)
    {
        PagePlaceholder.Visibility = Visibility.Collapsed;
        PageCpuWarning.Visibility = Visibility.Collapsed;
        PageGpuWarning.Visibility = Visibility.Collapsed;
        PageDrivesWarning.Visibility = Visibility.Collapsed;
        PageFansWarning.Visibility = Visibility.Collapsed;
        PageCoresWarning.Visibility = Visibility.Collapsed;
        PageMbWarning.Visibility = Visibility.Collapsed;
        PageGeneral.Visibility = Visibility.Collapsed;
        PageLogging.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "warn.cpu":
                PageCpuWarning.Visibility = Visibility.Visible;
                break;
            case "warn.gpu":
                PageGpuWarning.Visibility = Visibility.Visible;
                break;
            case "warn.drives":
                PageDrivesWarning.Visibility = Visibility.Visible;
                break;
            case "warn.mb_fans":
                PageFansWarning.Visibility = Visibility.Visible;
                break;
            case "warn.cpu_cores":
                PageCoresWarning.Visibility = Visibility.Visible;
                break;
            case "warn.mb_temp":
                PageMbWarning.Visibility = Visibility.Visible;
                break;
            case "settings.general":
                PageGeneral.Visibility = Visibility.Visible;
                break;
            case "settings.logging":
                PageLogging.Visibility = Visibility.Visible;
                break;
            default:
                PlaceholderTitle.Text = header;
                PagePlaceholder.Visibility = Visibility.Visible;
                break;
        }
    }

    private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem item)
            return;
        ShowPage(item.Tag as string ?? "", item.Header as string ?? "");
    }

    // ---------- buttons ----------

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result ??= WarningSettings.CreateDefault();
        SavePages(Result);
        if (_app is not null)
            AppResult = SaveAppPages();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WAV files (*.wav)|*.wav|All files (*.*)|*.*",
            Title = "Select warning sound",
        };
        if (dlg.ShowDialog(this) == true)
            SoundPathBox.Text = dlg.FileName;
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(SoundPathBox.Text))
            {
                using var player = new SoundPlayer(SoundPathBox.Text);
                player.Play();
            }
        }
        catch
        {
            // Preview is best-effort.
        }
    }

    private void BrowseProgram_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Programs (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|All files (*.*)|*.*",
            Title = "Select program to run",
        };
        if (dlg.ShowDialog(this) == true)
            ProgramPathBox.Text = dlg.FileName;
    }
}

/// <summary>Non-warning app settings edited by the same dialog (owned by MainWindow).</summary>
public sealed class AppSettingsSnapshot
{
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public bool AutoStartEnabled { get; set; }
    public bool LoggingEnabled { get; set; }
    public int LoggingIntervalSeconds { get; set; } = 10;
}
