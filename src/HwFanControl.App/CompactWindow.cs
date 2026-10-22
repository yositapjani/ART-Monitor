using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HwFanControl.App;

/// <summary>
/// Floating mini window (Core Temp style): big CPU temperature + TjMax headroom.
/// Drag to move, double-click to restore the main window, right-click to close.
/// </summary>
public sealed class CompactWindow : Window
{
    private readonly MainWindow _main;
    private readonly TextBlock _tempText;
    private readonly TextBlock _subText;

    public CompactWindow(MainWindow main)
    {
        _main = main;
        Title = "ART-Monitor Mini";
        Width = 196;
        Height = 98;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(228, 22, 22, 26));
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Left = SystemParameters.WorkArea.Right - Width - 16;
        Top = SystemParameters.WorkArea.Bottom - Height - 16;

        _tempText = new TextBlock
        {
            FontSize = 40,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        _subText = new TextBlock
        {
            FontSize = 12,
            Foreground = Brushes.LightGray,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        var panel = new StackPanel { VerticalAlignment = System.Windows.VerticalAlignment.Center };
        panel.Children.Add(_tempText);
        panel.Children.Add(_subText);
        Content = panel;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                _main.RestoreFromCompact();
                Close();
            }
            else
            {
                DragMove();
            }
        };
        MouseRightButtonUp += (_, _) => Close();
        RefreshData();
    }

    public void RefreshData()
    {
        float t = _main.CurrentCpuTemp();
        if (float.IsNaN(t))
        {
            _tempText.Text = "--";
            _subText.Text = "CPU";
            return;
        }

        _tempText.Text = $"{t:0}°C";
        _tempText.Foreground = t switch
        {
            < 60 => Brushes.White,
            < 75 => Brushes.Gold,
            < 85 => Brushes.Orange,
            _ => Brushes.OrangeRed,
        };
        string? d = _main.CurrentDistanceText();
        _subText.Text = d is null ? "CPU" : $"CPU • Δ TjMax {d}";
    }
}
