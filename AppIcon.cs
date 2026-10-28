using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using SD = System.Drawing;

namespace SnapTool;

/// <summary>The app icon, drawn in code: a blue rounded square with white viewfinder corners and a red dot.</summary>
internal static class AppIcon
{
    static SD.Bitmap Render(int size)
    {
        var bmp = new SD.Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(SD.Color.Transparent);
        float s = size / 32f;

        using (var path = RoundedRect(new SD.RectangleF(1 * s, 1 * s, 30 * s, 30 * s), 7 * s))
        using (var bg = new SD.SolidBrush(SD.Color.FromArgb(255, 30, 136, 229)))
            g.FillPath(bg, path);

        using (var pen = new SD.Pen(SD.Color.White, Math.Max(1.5f, 2.6f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float a = 8 * s, b = 24 * s, l = 5.5f * s;
            g.DrawLines(pen, new[] { new SD.PointF(a, a + l), new SD.PointF(a, a), new SD.PointF(a + l, a) });
            g.DrawLines(pen, new[] { new SD.PointF(b - l, a), new SD.PointF(b, a), new SD.PointF(b, a + l) });
            g.DrawLines(pen, new[] { new SD.PointF(b, b - l), new SD.PointF(b, b), new SD.PointF(b - l, b) });
            g.DrawLines(pen, new[] { new SD.PointF(a + l, b), new SD.PointF(a, b), new SD.PointF(a, b - l) });
        }
        using (var dot = new SD.SolidBrush(SD.Color.FromArgb(255, 255, 82, 82)))
            g.FillEllipse(dot, 12.5f * s, 12.5f * s, 7 * s, 7 * s);
        return bmp;
    }

    static GraphicsPath RoundedRect(SD.RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static SD.Icon CreateTrayIcon(int size)
    {
        using var bmp = Render(Math.Max(16, size));
        return SD.Icon.FromHandle(bmp.GetHicon());
    }

    static ImageSource? _windowIcon;

    public static ImageSource WindowIcon => _windowIcon ??= CreateWindowIcon();

    static ImageSource CreateWindowIcon()
    {
        using var bmp = Render(64);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, 64, 64), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bs = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * 64, data.Stride);
            bs.Freeze();
            return bs;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Writes a multi-size PNG .ico (used once to produce the exe icon).</summary>
    public static void SaveIco(string path)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            using var bmp = Render(size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            images.Add(ms.ToArray());
        }
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length); w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }
}
