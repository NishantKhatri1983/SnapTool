using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using SD = System.Drawing;

namespace SnapTool;

public sealed record WinInfo(IntPtr Handle, SD.Rectangle Bounds, string Title);

/// <summary>Screen pixels in, bitmaps out. All coordinates are physical pixels (the process is PerMonitorV2).</summary>
public static class ScreenGrabber
{
    public static SD.Rectangle VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    public static SD.Bitmap Capture(SD.Rectangle r)
    {
        var bmp = new SD.Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = SD.Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, SD.CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>Converts to a frozen, opaque Bgra32 BitmapSource at 96 dpi (1 unit = 1 pixel).</summary>
    public static BitmapSource ToBitmapSource(SD.Bitmap bmp)
    {
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = bmp.Width * 4;
            var px = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, px, y * stride, stride);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255; // GDI leaves alpha undefined
            var bs = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null, px, stride);
            bs.Freeze();
            return bs;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Visible top-level windows, front to back.</summary>
    public static List<WinInfo> GetWindows()
    {
        var list = new List<WinInfo>();
        Native.EnumWindows((h, lp) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;

            long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TRANSPARENT) != 0) return true; // click-through overlays
            if ((ex & Native.WS_EX_LAYERED) != 0 &&
                Native.GetLayeredWindowAttributes(h, out _, out byte alpha, out uint flags) &&
                (flags & Native.LWA_ALPHA) != 0 && alpha == 0) return true;

            var cls = Native.ClassName(h);
            if (cls is "Progman" or "WorkerW") return true; // desktop -> falls back to the monitor

            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r,
                    Marshal.SizeOf<Native.RECT>()) != 0)
                Native.GetWindowRect(h, out r);
            var rect = SD.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (rect.Width < 8 || rect.Height < 8) return true;

            list.Add(new WinInfo(h, rect, Native.WindowText(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
