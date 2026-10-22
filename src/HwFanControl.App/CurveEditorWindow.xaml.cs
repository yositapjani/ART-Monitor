using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HwFanControl.Core.Curves;

namespace HwFanControl.App;

public partial class CurveEditorWindow : Window
{
    private readonly List<FanCurvePointDto> _points;
    private int _dragIndex = -1;
    private const double Pad = 28;

    public FanCurveDefinition ResultCurve { get; private set; }

    public CurveEditorWindow(FanCurveDefinition source)
    {
        InitializeComponent();
        ResultCurve = Clone(source);
        _points = ResultCurve.Points;
        KindBox.ItemsSource = Enum.GetValues<FanCurveKind>();
        KindBox.SelectedItem = ResultCurve.Kind;
        Loaded += (_, _) => Redraw();
    }

    private static FanCurveDefinition Clone(FanCurveDefinition s) => new()
    {
        Name = s.Name,
        Kind = s.Kind,
        Points = s.Points.Select(p => new FanCurvePointDto
        {
            TemperatureCelsius = p.TemperatureCelsius,
            PwmPercent = p.PwmPercent,
        }).ToList(),
    };

    private void KindBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (KindBox.SelectedItem is FanCurveKind kind)
            ResultCurve.Kind = kind;
        Redraw();
    }

    private void Plot_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        Plot.Children.Clear();
        double w = Plot.ActualWidth;
        double h = Plot.ActualHeight;
        if (w < 40 || h < 40)
            return;

        // Axes
        Plot.Children.Add(new Line
        {
            X1 = Pad, Y1 = h - Pad, X2 = w - Pad, Y2 = h - Pad,
            Stroke = Brushes.Gray, StrokeThickness = 1,
        });
        Plot.Children.Add(new Line
        {
            X1 = Pad, Y1 = Pad, X2 = Pad, Y2 = h - Pad,
            Stroke = Brushes.Gray, StrokeThickness = 1,
        });

        var ordered = _points.OrderBy(p => p.TemperatureCelsius).ToList();
        if (ordered.Count == 0)
            return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                var pt = ToCanvas(ordered[i], w, h);
                if (i == 0)
                    ctx.BeginFigure(pt, false, false);
                else
                    ctx.LineTo(pt, true, false);
            }
        }

        Plot.Children.Add(new Path
        {
            Data = geo,
            Stroke = (Brush)FindResource("AccentBrush"),
            StrokeThickness = 2,
        });

        for (int i = 0; i < _points.Count; i++)
        {
            var c = ToCanvas(_points[i], w, h);
            var ellipse = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = Brushes.White,
                Stroke = (Brush)FindResource("AccentBrush"),
                StrokeThickness = 2,
                Tag = i,
            };
            Canvas.SetLeft(ellipse, c.X - 6);
            Canvas.SetTop(ellipse, c.Y - 6);
            ellipse.MouseLeftButtonDown += Point_MouseDown;
            Plot.Children.Add(ellipse);

            var label = new TextBlock
            {
                Text = $"{_points[i].TemperatureCelsius:0}°/{_points[i].PwmPercent:0}%",
                FontSize = 10,
                Foreground = Brushes.LightGray,
            };
            Canvas.SetLeft(label, c.X + 8);
            Canvas.SetTop(label, c.Y - 14);
            Plot.Children.Add(label);
        }
    }

    private System.Windows.Point ToCanvas(FanCurvePointDto p, double w, double h)
    {
        double x = Pad + (p.TemperatureCelsius / 100.0) * (w - 2 * Pad);
        double y = (h - Pad) - (p.PwmPercent / 100.0) * (h - 2 * Pad);
        return new System.Windows.Point(x, y);
    }

    private FanCurvePointDto FromCanvas(System.Windows.Point pt, double w, double h)
    {
        double temp = (pt.X - Pad) / Math.Max(1, w - 2 * Pad) * 100.0;
        double pwm = ((h - Pad) - pt.Y) / Math.Max(1, h - 2 * Pad) * 100.0;
        return new FanCurvePointDto
        {
            TemperatureCelsius = (float)Math.Clamp(temp, 0, 100),
            PwmPercent = (float)Math.Clamp(pwm, 0, 100),
        };
    }

    private void Point_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Ellipse { Tag: int index })
        {
            _dragIndex = index;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }
    }

    private void Plot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // ignore empty canvas clicks
    }

    private void Plot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(Plot);
        var mapped = FromCanvas(pos, Plot.ActualWidth, Plot.ActualHeight);
        _points[_dragIndex] = mapped;
        Redraw();
    }

    private void Plot_MouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragIndex >= 0)
            Mouse.Capture(null);
        _dragIndex = -1;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var quiet = FanCurvePresets.DefaultQuiet();
        _points.Clear();
        _points.AddRange(quiet.Points);
        ResultCurve.Kind = FanCurveKind.Linear;
        KindBox.SelectedItem = FanCurveKind.Linear;
        Redraw();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ResultCurve.Points = _points.OrderBy(p => p.TemperatureCelsius).ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
