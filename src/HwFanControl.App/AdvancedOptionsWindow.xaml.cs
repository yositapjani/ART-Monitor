using System.Globalization;
using System.Windows;
using HwFanControl.Core.Curves;

namespace HwFanControl.App;

public partial class AdvancedOptionsWindow : Window
{
    private readonly FanChannelConfig _config;

    public AdvancedOptionsWindow(FanChannelConfig config)
    {
        InitializeComponent();
        _config = config;
        NameBox.Text = config.Name;
        MinPwm.Text = config.Safety.MinimumPwmPercent.ToString("0.#", CultureInfo.InvariantCulture);
        KickPwm.Text = config.Safety.KickstartPwmPercent.ToString("0.#", CultureInfo.InvariantCulture);
        KickDur.Text = config.Safety.KickstartDurationSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        RateUp.Text = config.Safety.RateLimitUpPercentPerSecond.ToString("0.#", CultureInfo.InvariantCulture);
        RateDown.Text = config.Safety.RateLimitDownPercentPerSecond.ToString("0.#", CultureInfo.InvariantCulture);
        Hyst.Text = config.Safety.TemperatureHysteresisCelsius.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _config.Name = NameBox.Text.Trim();
        if (float.TryParse(MinPwm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float min))
            _config.Safety.MinimumPwmPercent = min;
        if (float.TryParse(KickPwm.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float kick))
            _config.Safety.KickstartPwmPercent = kick;
        if (float.TryParse(KickDur.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float dur))
            _config.Safety.KickstartDurationSeconds = dur;
        if (float.TryParse(RateUp.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float up))
            _config.Safety.RateLimitUpPercentPerSecond = up;
        if (float.TryParse(RateDown.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float down))
            _config.Safety.RateLimitDownPercentPerSecond = down;
        if (float.TryParse(Hyst.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float hyst))
            _config.Safety.TemperatureHysteresisCelsius = hyst;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
