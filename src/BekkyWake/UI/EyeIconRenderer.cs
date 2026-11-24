using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BekkyWake.UI;

/// <summary>
/// Renders Insomnia's vector eye geometry (matching docs/assets/eye-open.svg)
/// into crisp DPI-friendly System.Drawing.Icon handles for the Windows tray and windows.
/// </summary>
public static class EyeIconRenderer
{
    public static readonly Color Midnight = Color.FromArgb(0x30, 0x33, 0x36);
    public static readonly Color MoonWhite = Color.FromArgb(0xE6, 0xE3, 0xDD);
    public static readonly Color SilverViolet = Color.FromArgb(0xA6, 0xBB, 0xC3);
    public static readonly Color WarningAmber = Color.FromArgb(0xF5, 0x9E, 0x0B);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static bool IsSystemTaskbarLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int v)
            {
                return v == 1;
            }
        }
        catch
        {
            // Default to dark taskbar assumptions.
        }
        return false;
    }

    public static Icon CreateTrayIcon(
        bool isOpen,
        bool hasWarning = false,
        double? ringProgress = null,
        int size = 32)
    {
        var lightTaskbar = IsSystemTaskbarLight();
        var strokeColor = lightTaskbar ? Midnight : MoonWhite;

        using var bmp = new Bitmap(size, size, global::System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            if (ringProgress is double progress && progress > 0)
            {
                var clamped = Math.Clamp(progress, 0.0, 1.0);
                var pad = size * 0.08f;
                var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
                using var trackPen = new Pen(Color.FromArgb(65, strokeColor), Math.Max(2f, size * 0.075f));
                g.DrawEllipse(trackPen, rect);

                using var progPen = new Pen(SilverViolet, Math.Max(2f, size * 0.085f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                };
                g.DrawArc(progPen, rect, -90f, (float)(360.0 * clamped));
            }

            var scale = (ringProgress.HasValue ? size * 0.66f : size * 0.90f) / 24.0f;
            var offsetX = (size - 24.0f * scale) / 2f;
            var offsetY = (size - 24.0f * scale) / 2f;

            g.TranslateTransform(offsetX, offsetY);
            g.ScaleTransform(scale, scale);

            if (isOpen)
            {
                DrawOpenEye(g, strokeColor);
            }
            else
            {
                DrawClosedEye(g, strokeColor);
            }

            g.ResetTransform();

            if (hasWarning)
            {
                var dotSize = size * 0.28f;
                var dotRect = new RectangleF(size - dotSize - 1, size - dotSize - 1, dotSize, dotSize);
                using var bgBrush = new SolidBrush(WarningAmber);
                using var borderPen = new Pen(lightTaskbar ? Color.White : Midnight, 1.5f);
                g.FillEllipse(bgBrush, dotRect);
                g.DrawEllipse(borderPen, dotRect);
            }
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static Icon CreateAppWindowIcon(int size = 64)
    {
        using var bmp = new Bitmap(size, size, global::System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var pad = size * 0.06f;
            var tile = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            var radius = tile.Width * 0.2237f;
            using var tilePath = CreateRoundedRect(tile, radius);
            using var tileBrush = new SolidBrush(Midnight);
            g.FillPath(tileBrush, tilePath);

            var scale = (tile.Width * 0.76f) / 24f;
            var ox = (size - 24f * scale) / 2f;
            var oy = (size - 24f * scale) / 2f;
            g.TranslateTransform(ox, oy);
            g.ScaleTransform(scale, scale);
            DrawOpenEye(g, MoonWhite);
            g.ResetTransform();
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = Icon.FromHandle(hIcon);
            return (Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawOpenEye(Graphics g, Color color)
    {
        using var pen = new Pen(color, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        // Almond outline from eye-open.svg: M1.5 12 C7 4.5 17 4.5 22.5 12 C17 19.5 7 19.5 1.5 12 Z
        using var lens = new GraphicsPath();
        lens.AddBezier(1.5f, 12f, 7f, 4.5f, 17f, 4.5f, 22.5f, 12f);
        lens.AddBezier(22.5f, 12f, 17f, 19.5f, 7f, 19.5f, 1.5f, 12f);
        lens.CloseFigure();
        g.DrawPath(pen, lens);

        // Pupil with upper-right highlight notch.
        using var pupilPath = new GraphicsPath();
        pupilPath.AddEllipse(8.0f, 8.0f, 8.0f, 8.0f);
        using var pupilRegion = new Region(pupilPath);
        using var notchPath = new GraphicsPath();
        notchPath.AddEllipse(12.6f, 8.5f, 2.8f, 2.8f);
        pupilRegion.Exclude(notchPath);
        using var fillBrush = new SolidBrush(color);
        g.FillRegion(fillBrush, pupilRegion);

        // Five upper lashes from eye-open.svg
        g.DrawLine(pen, 4.5499f, 6.1977f, 3.1442f, 3.8341f);
        g.DrawLine(pen, 8.2636f, 4.6218f, 7.5468f, 1.9668f);
        g.DrawLine(pen, 12f, 4.125f, 12f, 1.375f);
        g.DrawLine(pen, 15.7364f, 4.6218f, 16.4532f, 1.9668f);
        g.DrawLine(pen, 19.4501f, 6.1977f, 20.8558f, 3.8341f);
    }

    private static void DrawClosedEye(Graphics g, Color color)
    {
        using var pen = new Pen(color, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        using var lid = new GraphicsPath();
        lid.AddBezier(2.0f, 11.5f, 7.2f, 17.2f, 16.8f, 17.2f, 22.0f, 11.5f);
        g.DrawPath(pen, lid);

        // Three gentle downward lashes
        g.DrawLine(pen, 6.4f, 14.9f, 5.2f, 17.7f);
        g.DrawLine(pen, 12.0f, 15.8f, 12.0f, 18.9f);
        g.DrawLine(pen, 17.6f, 14.9f, 18.8f, 17.7f);
    }

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
