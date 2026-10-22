using System.IO;
using System.Windows;
using System.Windows.Media;

namespace HwFanControl.App.Services;

/// <summary>
/// Light/dark theme for the main window. The choice is persisted to
/// %AppData%\HwFanControl\theme.txt and applied by swapping the palette
/// brushes in the window resources (all themed XAML uses DynamicResource).
/// Graph canvases read the static brushes below on every redraw.
/// </summary>
public static class ThemeService
{
    public static bool IsDark { get; private set; }

    private static string ThemeFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HwFanControl", "theme.txt");

    // ---- Graph canvas brushes (frozen, shared) ----
    private static readonly Brush LightGraphBg = Frozen(new SolidColorBrush(Colors.White));
    private static readonly Brush DarkGraphBg = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D)));
    private static readonly Brush LightGraphInk = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Brush DarkGraphInk = Frozen(new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE3)));
    private static readonly Brush LightGraphGrid = Frozen(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)));
    private static readonly Brush DarkGraphGrid = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x40, 0x48)));

    /// <summary>Brightens dark series colors so they stay visible on a dark graph.</summary>
    public static Color ForGraph(Color c)
    {
        if (!IsDark)
            return c;
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        if (lum >= 0.38)
            return c;
        const double f = 0.55;
        return Color.FromRgb(
            (byte)(c.R + (255 - c.R) * f),
            (byte)(c.G + (255 - c.G) * f),
            (byte)(c.B + (255 - c.B) * f));
    }

    public static Brush GraphBg => IsDark ? DarkGraphBg : LightGraphBg;
    public static Brush GraphInk => IsDark ? DarkGraphInk : LightGraphInk;
    public static Brush GraphGrid => IsDark ? DarkGraphGrid : LightGraphGrid;

    private static T Frozen<T>(T brush) where T : Brush
    {
        brush.Freeze();
        return brush;
    }

    public static void LoadSaved()
    {
        try
        {
            IsDark = File.Exists(ThemeFile) &&
                     File.ReadAllText(ThemeFile).Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            IsDark = false;
        }
    }

    public static void SetDark(bool dark, Window window)
    {
        IsDark = dark;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeFile)!);
            File.WriteAllText(ThemeFile, dark ? "dark" : "light");
        }
        catch
        {
            // Persistence is best-effort; the theme still applies live.
        }

        ApplyTo(window);
    }

    public static void ApplyTo(Window window)
    {
        var res = window.Resources;
        if (IsDark)
        {
            res["PageBrush"] = Gradient(0x14, 0x16, 0x1A, 0x1E, 0x21, 0x26, 0x26, 0x2A, 0x31);
            res["TabBrush"] = Gradient(0x23, 0x26, 0x2C, 0x2E, 0x32, 0x38);
            res["BoxBrush"] = Solid(0x1E, 0x21, 0x26);
            res["BoxTextBrush"] = Solid(0xE8, 0xE8, 0xE8);
            res["WindowBrush"] = Solid(0x1B, 0x1D, 0x21);
            res["TextBrush"] = Solid(0xE8, 0xE8, 0xE8);
            res["AltRowBrush"] = Solid(0x23, 0x26, 0x2B);
        }
        else
        {
            res["PageBrush"] = Gradient(0x2B, 0x5A, 0xA0, 0x6F, 0xA0, 0xD6, 0xA8, 0xC8, 0xE8);
            res["TabBrush"] = Gradient(0x1B, 0x3A, 0x6E, 0x2B, 0x5A, 0xA0);
            res["BoxBrush"] = Solid(0xFF, 0xFF, 0xFF);
            res["BoxTextBrush"] = Solid(0x11, 0x11, 0x11);
            res["WindowBrush"] = Solid(0xF0, 0xF0, 0xF0);
            res["TextBrush"] = Solid(0x00, 0x00, 0x00);
            res["AltRowBrush"] = Solid(0xF0, 0xF0, 0xF0);
        }
    }

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(r1, g1, b1), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(r2, g2, b2), 1));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(
        byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, byte r3, byte g3, byte b3)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(r1, g1, b1), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(r2, g2, b2), 0.55));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(r3, g3, b3), 1));
        brush.Freeze();
        return brush;
    }
}
