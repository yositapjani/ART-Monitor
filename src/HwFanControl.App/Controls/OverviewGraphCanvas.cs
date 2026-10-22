using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HwFanControl.App.Services;

namespace HwFanControl.App.Controls;

public sealed class OverviewGraphCanvas : Canvas
{
    private OverviewViewModel? _model;
    private Point? _hover;

    public OverviewGraphCanvas()
    {
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            _hover = null;
            Redraw();
        };
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        Redraw();
    }

    public void Bind(OverviewViewModel model)
    {
        _model = model;
        Redraw();
    }

    public void Redraw()
    {
        Children.Clear();
        if (_model is null || ActualWidth < 40 || ActualHeight < 40)
            return;

        double w = ActualWidth;
        double h = ActualHeight;
        double padL = 32, padR = 6, padT = 18, padB = 6;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        float ymin = _model.AxisMin;
        float ymax = _model.AxisMax;
        if (ymax <= ymin) ymax = ymin + 20;

        Children.Add(new Rectangle { Width = w, Height = h, Fill = ThemeService.GraphBg });

        var title = new TextBlock { Text = "°C", FontSize = 11, Foreground = ThemeService.GraphInk, FontWeight = FontWeights.SemiBold };
        SetLeft(title, padL);
        SetTop(title, 2);
        Children.Add(title);

        int steps = Math.Max(4, (int)((ymax - ymin) / 5));
        for (int i = 0; i <= steps; i++)
        {
            float val = ymin + (ymax - ymin) * i / steps;
            double y = padT + plotH * (1.0 - (val - ymin) / (ymax - ymin));
            Children.Add(new Line
            {
                X1 = padL, Y1 = y, X2 = w - padR, Y2 = y,
                Stroke = ThemeService.GraphGrid,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });
            var label = new TextBlock { Text = $"{val:0}", FontSize = 10, Foreground = ThemeService.GraphInk };
            SetLeft(label, 4);
            SetTop(label, y - 7);
            Children.Add(label);
        }

        var border = new Rectangle
        {
            Width = plotW,
            Height = plotH,
            Stroke = ThemeService.GraphInk,
            StrokeThickness = 1,
            Fill = Brushes.Transparent,
        };
        SetLeft(border, padL);
        SetTop(border, padT);
        Children.Add(border);

        // Hover: which sample index is under the mouse?
        int hoverIdx = -1;
        Point hoverPos = default;
        if (_hover is { } hp && hp.X >= padL && hp.X <= w - padR && hp.Y >= padT && hp.Y <= padT + plotH)
        {
            hoverPos = hp;
            hoverIdx = (int)Math.Round((hp.X - padL) / plotW * (OverviewViewModel.HistoryCapacity - 1));
        }

        string? hoverName = null;
        Color hoverColor = Colors.Black;
        float hoverVal = 0;
        double hoverX = 0, hoverY = 0, hoverDist = double.MaxValue;
        int hoverAge = 0;

        int colorIdx = 0;
        foreach (var legend in _model.Legend)
        {
            if (!legend.IsVisible)
            {
                colorIdx++;
                continue;
            }

            if (!_model.Histories.TryGetValue(legend.Name, out var queue))
            {
                colorIdx++;
                continue;
            }

            var series = queue.ToArray();
            var color = _model.ColorForIndex(colorIdx);
            DrawSeries(series, color, padL, padT, plotW, plotH, ymin, ymax);

            if (hoverIdx >= 0 && series.Length > 0)
            {
                int idx = Math.Min(hoverIdx, series.Length - 1);
                float v = series[idx];
                if (!float.IsNaN(v) && v > 0.01f)
                {
                    double y = padT + plotH * (1.0 - (v - ymin) / (ymax - ymin));
                    double d = Math.Abs(y - hoverPos.Y);
                    if (d < hoverDist)
                    {
                        hoverDist = d;
                        hoverName = legend.Name;
                        hoverColor = color;
                        hoverVal = v;
                        hoverY = y;
                        hoverX = padL + plotW * idx / Math.Max(1, OverviewViewModel.HistoryCapacity - 1);
                        hoverAge = series.Length - 1 - idx;
                    }
                }
            }

            colorIdx++;
        }

        if (hoverName is not null && hoverDist <= 18)
        {
            var guide = new Line
            {
                X1 = hoverX, X2 = hoverX, Y1 = padT, Y2 = padT + plotH,
                Stroke = new SolidColorBrush(hoverColor) { Opacity = 0.45 },
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            };
            Children.Add(guide);

            var dot = new Ellipse
            {
                Width = 7, Height = 7,
                Fill = new SolidColorBrush(hoverColor),
                Stroke = ThemeService.GraphBg,
                StrokeThickness = 1.2,
            };
            SetLeft(dot, hoverX - 3.5);
            SetTop(dot, hoverY - 3.5);
            Children.Add(dot);

            string age = hoverAge <= 0 ? "now" : $"-{hoverAge}s";
            var tb = new TextBlock
            {
                Text = $"{hoverName}: {hoverVal:0} °C ({age})",
                FontSize = 11,
                Foreground = ThemeService.GraphInk,
            };
            var bd = new Border
            {
                Background = ThemeService.IsDark ? new SolidColorBrush(Color.FromArgb(0xEE, 0x22, 0x26, 0x2E)) : new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(hoverColor),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 2, 5, 2),
                Child = tb,
            };
            bd.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            double bw = bd.DesiredSize.Width;
            double bh = bd.DesiredSize.Height;
            double lx = Math.Min(Math.Max(hoverX + 8, padL), Math.Max(padL, w - padR - bw));
            double ly = hoverY - bh - 8;
            if (ly < 0) ly = hoverY + 8;
            SetLeft(bd, lx);
            SetTop(bd, ly);
            Children.Add(bd);
        }
    }

    private void DrawSeries(
        float[] series, Color color,
        double padL, double padT, double plotW, double plotH,
        float ymin, float ymax)
    {
        if (series.Length < 2) return;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool started = false;
            for (int i = 0; i < series.Length; i++)
            {
                float v = series[i];
                if (float.IsNaN(v) || v <= 0.01f) { started = false; continue; }
                double x = padL + plotW * i / Math.Max(1, OverviewViewModel.HistoryCapacity - 1);
                double y = padT + plotH * (1.0 - (v - ymin) / (ymax - ymin));
                var pt = new Point(x, y);
                if (!started) { ctx.BeginFigure(pt, false, false); started = true; }
                else ctx.LineTo(pt, true, false);
            }
        }
        geo.Freeze();
        Children.Add(new Path
        {
            Data = geo,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2.2,
        });
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }
}
