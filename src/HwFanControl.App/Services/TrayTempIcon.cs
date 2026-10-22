using System.Runtime.InteropServices;
using Gdi = System.Drawing;
using Gdi2D = System.Drawing.Drawing2D;

namespace HwFanControl.App.Services;

/// <summary>
/// Renders a Core Temp-style numeric tray icon: temperature digits, color-banded
/// (white under 60, gold under 75, orange under 85, red above).
/// </summary>
public static class TrayTempIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Gdi.Icon Create(int tempC)
    {
        string text = tempC > 99 ? "99" : tempC.ToString();
        Gdi.Color color = tempC switch
        {
            < 60 => Gdi.Color.White,
            < 75 => Gdi.Color.FromArgb(255, 210, 63),
            < 85 => Gdi.Color.FromArgb(255, 140, 0),
            _ => Gdi.Color.FromArgb(255, 70, 70),
        };

        using var bmp = new Gdi.Bitmap(32, 32);
        using (Gdi.Graphics g = Gdi.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Gdi2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Gdi.Color.Transparent);
            using var font = new Gdi.Font("Segoe UI", text.Length > 1 ? 17f : 21f, Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
            using var brush = new Gdi.SolidBrush(color);
            Gdi.SizeF size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (32f - size.Width) / 2f, (32f - size.Height) / 2f);
        }

        IntPtr hicon = bmp.GetHicon();
        try
        {
            using Gdi.Icon tmp = Gdi.Icon.FromHandle(hicon);
            return (Gdi.Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }
}
