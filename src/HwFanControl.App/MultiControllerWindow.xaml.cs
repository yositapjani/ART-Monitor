using System.Windows;
using System.Windows.Controls;
using HwFanControl.Core.Curves;

namespace HwFanControl.App;

public partial class MultiControllerWindow : Window
{
    private readonly FanChannelConfig _config;
    private readonly TemperatureSourceKind[] _sources =
    [
        TemperatureSourceKind.CpuPackage,
        TemperatureSourceKind.CpuCoreMax,
        TemperatureSourceKind.GpuEdge,
        TemperatureSourceKind.GpuJunction,
        TemperatureSourceKind.GpuHotspot,
        TemperatureSourceKind.Ambient,
        TemperatureSourceKind.AvgCpuGpu,
        TemperatureSourceKind.MaxCpuGpu,
        TemperatureSourceKind.AvgCpuGpuFiltered,
        TemperatureSourceKind.MaxCpuGpuFiltered,
    ];

    public MultiControllerWindow(FanChannelConfig config)
    {
        InitializeComponent();
        _config = config;
        BuildList();
    }

    private void BuildList()
    {
        var panel = new StackPanel();
        for (int i = 0; i < _config.Controllers.Length; i++)
        {
            var slot = _config.Controllers[i];
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var enabled = new CheckBox
            {
                Content = $"Controller {i + 1}",
                IsChecked = slot.Enabled,
                Width = 120,
                Tag = i,
            };
            DockPanel.SetDock(enabled, Dock.Left);

            var source = new ComboBox
            {
                Width = 180,
                ItemsSource = _sources,
                SelectedItem = slot.TemperatureSource,
                Tag = i,
                Margin = new Thickness(8, 0, 8, 0),
            };

            var curveBtn = new Button
            {
                Content = "Kurva…",
                Tag = i,
            };
            curveBtn.Click += CurveBtn_Click;

            row.Children.Add(enabled);
            row.Children.Add(curveBtn);
            row.Children.Add(source);
            panel.Children.Add(row);
        }

        ControllerList.Items.Clear();
        ControllerList.Items.Add(panel);
    }

    private void CurveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index })
            return;
        var dlg = new CurveEditorWindow(_config.Controllers[index].Curve) { Owner = this };
        if (dlg.ShowDialog() == true)
            _config.Controllers[index].Curve = dlg.ResultCurve;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ControllerList.Items[0] is not StackPanel panel)
        {
            DialogResult = false;
            return;
        }

        foreach (var child in panel.Children)
        {
            if (child is not DockPanel row)
                continue;

            CheckBox? enabled = null;
            ComboBox? source = null;
            foreach (var el in row.Children)
            {
                if (el is CheckBox cb)
                    enabled = cb;
                if (el is ComboBox combo)
                    source = combo;
            }

            if (enabled?.Tag is int idx)
            {
                _config.Controllers[idx].Enabled = enabled.IsChecked == true;
                if (source?.SelectedItem is TemperatureSourceKind kind)
                    _config.Controllers[idx].TemperatureSource = kind;
            }
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
