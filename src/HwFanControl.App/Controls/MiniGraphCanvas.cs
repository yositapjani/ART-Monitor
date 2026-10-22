using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HwFanControl.App.Services;

namespace HwFanControl.App.Controls;

/// <summary>
/// OCCT-style mini graph: one series per chart, own autoscale, filled area.
/// The data source is a provider delegate returning the current frame
/// (samples + color + capacity), so any view model can feed it.
/// </summary>
public sealed class MiniGraphCanvas : Canvas
{
    public readonly record struct Frame(IReadOnlyList<float> Data, Color Color, int Capacity);

    private Func<Frame>? _provider;

    public void Bind(Func<Frame> provider)
    {
        _provider = provider;
    }

    public void Redraw()
    {
        Children.Clear();
        if (_provider is null || ActualWidth < 30 || ActualHeight < 30)
            return;

        double w = ActualWidth;
        double h = ActualHeight;

        Children.Add(new Rectangle { Width = w, Height = h, Fill = ThemeService.GraphBg });

        var frame = _provider();
        var data = frame.Data;
        if (data is null || data.Count == 0)
            return;

        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in data)
        {
            if (float.IsNaN(v) || v <= 0.01f)
                continue;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (min == float.MaxValue)
            return;

        double span = max - min;
        double pad = Math.Max(span * 0.08, Math.Abs(max) * 0.01);
        if (span < 0.0001)
            pad = Math.Max(1.0, Math.Abs(max) * 0.05);
        min -= (float)pad;
        max += (float)pad;

        double padL = 30, padR = 4, padT = 4, padB = 4;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;

        // Dashed gridlines + min/mid/max labels.
        for (int i = 0; i <= 2; i++)
        {
            double frac = i / 2.0;
            double y = padT + plotH * (1.0 - frac);
            Children.Add(new Line
            {
                X1 = padL, Y1 = y, X2 = w - padR, Y2 = y,
                Stroke = ThemeService.GraphGrid,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
            });
            float val = (float)(min + (max - min) * frac);
            var label = new TextBlock
            {
                Text = val.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 9,
                Foreground = ThemeService.GraphInk,
            };
            SetLeft(label, 2);
            SetTop(label, y - 6);
            Children.Add(label);
        }

        int capacity = Math.Max(2, frame.Capacity);

        Point Map(int i, float v)
        {
            double x = padL + plotW * i / (capacity - 1);
            double y = padT + plotH * (1.0 - (v - min) / (max - min));
            return new Point(x, y);
        }

        // Filled area under the line (OCCT look).
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            bool started = false;
            Point last = default;
            for (int i = 0; i < data.Count; i++)
            {
                float v = data[i];
                if (float.IsNaN(v) || v <= 0.01f)
                {
                    started = false;
                    continue;
                }

                var pt = Map(i, v);
                if (!started)
                {
                    ctx.BeginFigure(new Point(pt.X, padT + plotH), true, true);
                    ctx.LineTo(pt, true, false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(pt, true, false);
                }

                last = pt;
            }

            if (started)
                ctx.LineTo(new Point(last.X, padT + plotH), true, false);
        }

        area.Freeze();
        Children.Add(new Path
        {
            Data = area,
            Fill = new SolidColorBrush(frame.Color) { Opacity = 0.28 },
        });

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            bool started = false;
            for (int i = 0; i < data.Count; i++)
            {
                float v = data[i];
                if (float.IsNaN(v) || v <= 0.01f)
                {
                    started = false;
                    continue;
                }

                var pt = Map(i, v);
                if (!started)
                {
                    ctx.BeginFigure(pt, false, false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(pt, true, false);
                }
            }
        }

        line.Freeze();
        Children.Add(new Path
        {
            Data = line,
            Stroke = new SolidColorBrush(frame.Color),
            StrokeThickness = 1.6,
            Fill = Brushes.Transparent,
        });
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Redraw();
    }
}
