using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using SD = System.Drawing;

namespace SnapTool;

/// <summary>
/// Scrolling capture: sends mouse-wheel notches to the window under the anchor point, grabs the region after
/// each scroll, works out how far the content moved by matching pixel rows, and stitches the new rows together.
/// Fixed headers/footers (rows identical in consecutive frames) are kept once, at the top/bottom.
/// Press Esc to stop early.
/// </summary>
internal static class ScrollCapture
{
    internal sealed class Frame
    {
        public int W, H, Stride;
        public byte[] Px = Array.Empty<byte>();
        public ulong[] Hash = Array.Empty<ulong>();
        public bool[] Uniform = Array.Empty<bool>();
    }

    public static BitmapSource Run(SD.Rectangle rect, SD.Point anchor, AppSettings s)
    {
        // The scrollbar changes on every step, so leave the right edge out of the row comparison.
        int ignoreRight = Math.Min(rect.Width / 4, 40);
        Native.GetCursorPos(out var oldCursor);
        try
        {
            Native.SetCursorPos(anchor.X, anchor.Y);
            Thread.Sleep(80);
            return Stitch(() => GrabFrame(rect, ignoreRight), Native.Wheel, Native.EscapePressed, s);
        }
        finally
        {
            Native.SetCursorPos(oldCursor.X, oldCursor.Y);
        }
    }

    /// <summary>The scroll-and-stitch loop. grab() returns the current view, wheel(n) scrolls (positive = up).</summary>
    internal static BitmapSource Stitch(Func<Frame> grab, Action<int> wheel, Func<bool> stop, AppSettings s)
    {
        if (s.ScrollToTopFirst)
        {
            var before = grab();
            for (int i = 0; i < 150 && !stop(); i++)
            {
                wheel(+10);
                var now = Settle(grab, s);
                if (Same(before, now)) break;
                before = now;
            }
        }

        var first = Settle(grab, s);
        int H = first.H;
        var strips = new List<byte[]>();
        int top = -1, bottom = 0, total = 0, notches = 3, failures = 0;
        double pxPerNotch = 0;
        Frame prev = first, last = first;

        for (int i = 0; i < s.ScrollMaxFrames && !stop(); i++)
        {
            wheel(-notches);
            var cur = Settle(grab, s);
            if (top < 0 && !Same(prev, cur)) (top, bottom) = StaticBands(prev, cur);

            int expected = pxPerNotch > 0 ? (int)(pxPerNotch * notches) : 0;
            int d = top < 0 ? 0 : FindShift(prev, cur, top, bottom, expected);
            if (d == 0)
            {
                // Nothing moved: slow repaint, or the end of the page. Look once more.
                Thread.Sleep(350);
                cur = grab();
                if (top < 0 && !Same(prev, cur)) (top, bottom) = StaticBands(prev, cur);
                d = top < 0 ? 0 : FindShift(prev, cur, top, bottom, expected);
                if (d == 0) break;
            }
            if (d < 0)
            {
                // Scrolled further than one screen of overlap: go back and take smaller steps.
                if (notches == 1 || ++failures > 4) break;
                wheel(+notches);
                Settle(grab, s);
                notches = Math.Max(1, notches / 2);
                continue;
            }

            strips.Add(CopyRows(cur, H - bottom - d, d));
            total += d;
            last = cur;
            prev = cur;
            pxPerNotch = d / (double)notches;
            int mid = H - top - bottom;
            notches = Math.Clamp((int)(0.6 * mid / Math.Max(1, pxPerNotch)), 1, 25);
            if (H + total >= s.ScrollMaxHeight) break;
        }

        // Compose: first frame down to the fixed footer, every new strip, then the footer from the last frame.
        int outH = H + total;
        int stride = first.Stride;
        var outPx = new byte[stride * outH];
        int y = 0;
        Buffer.BlockCopy(first.Px, 0, outPx, 0, (H - bottom) * stride);
        y += H - bottom;
        foreach (var strip in strips)
        {
            Buffer.BlockCopy(strip, 0, outPx, y * stride, strip.Length);
            y += strip.Length / stride;
        }
        Buffer.BlockCopy(last.Px, (H - bottom) * stride, outPx, y * stride, bottom * stride);
        for (int i = 3; i < outPx.Length; i += 4) outPx[i] = 255;

        var bmp = BitmapSource.Create(first.W, outH, 96, 96, PixelFormats.Bgra32, null, outPx, stride);
        bmp.Freeze();
        return bmp;
    }

    internal static Frame GrabFrame(SD.Rectangle r, int ignoreRight)
    {
        using var bmp = ScreenGrabber.Capture(r);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, r.Width, r.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = r.Width * 4;
        var px = new byte[stride * r.Height];
        for (int y = 0; y < r.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * stride, stride);
        bmp.UnlockBits(data);
        return MakeFrame(px, r.Width, r.Height, ignoreRight);
    }

    /// <summary>Wraps BGRA pixels and precomputes one hash per row (ignoring the rightmost columns).</summary>
    internal static Frame MakeFrame(byte[] px, int w, int h, int ignoreRight) =>
        Rehash(new Frame { W = w, H = h, Stride = w * 4, Px = px, Hash = new ulong[h], Uniform = new bool[h] }, ignoreRight);

    /// <summary>An empty w x h frame whose buffers can be filled again and again (see Rehash).</summary>
    internal static Frame NewFrame(int w, int h) =>
        new() { W = w, H = h, Stride = w * 4, Px = new byte[w * h * 4], Hash = new ulong[h], Uniform = new bool[h] };

    /// <summary>Recomputes the row hashes after the frame's pixels were replaced.</summary>
    internal static Frame Rehash(Frame f, int ignoreRight)
    {
        int w = f.W, h = f.H, cols = Math.Max(2, w - ignoreRight) & ~1; // even, so the row can be read as 64-bit words
        var px = f.Px;
        var pixels = MemoryMarshal.Cast<byte, uint>(px);
        for (int y = 0; y < h; y++)
        {
            var row = pixels.Slice(y * w, cols);
            ulong hash = 1469598103934665603UL;
            foreach (ulong word in MemoryMarshal.Cast<uint, ulong>(row))
                hash = (hash ^ word) * 1099511628211UL;
            f.Hash[y] = hash;
            f.Uniform[y] = row.IndexOfAnyExcept(row[0]) < 0;
        }
        return f;
    }

    /// <summary>Waits until the screen stops changing (smooth scrolling, lazy repaint).</summary>
    static Frame Settle(Func<Frame> grab, AppSettings s)
    {
        Thread.Sleep(s.ScrollSettleMs);
        var a = grab();
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(70);
            var b = grab();
            if (Same(a, b)) return b;
            a = b;
        }
        return a;
    }

    internal static bool Same(Frame a, Frame b)
    {
        for (int y = 0; y < a.H; y++) if (a.Hash[y] != b.Hash[y]) return false;
        return true;
    }

    /// <summary>Rows at the top and bottom that didn't change (title bars, sticky headers, footers).</summary>
    internal static (int top, int bottom) StaticBands(Frame a, Frame b)
    {
        int H = a.H, max = H * 45 / 100;
        int top = 0;
        for (int y = 0; y < max && a.Hash[y] == b.Hash[y]; y++)
            if (!a.Uniform[y]) top = y + 1; // don't count plain background rows as header
        int bottom = 0;
        for (int k = 0; k < max && a.Hash[H - 1 - k] == b.Hash[H - 1 - k]; k++)
            if (!a.Uniform[H - 1 - k]) bottom = k + 1;
        return (top, bottom);
    }

    /// <summary>
    /// How many pixels the content moved up between a and b (b[y] == a[y + d]).
    /// 0 = no movement, -1 = no reliable match.
    /// </summary>
    static int FindShift(Frame a, Frame b, int top, int bottom, int expected)
    {
        int end = a.H - bottom, mid = end - top;
        if (mid < 16) return -1;
        bool moved = false;
        for (int y = top; y < end; y++) if (a.Hash[y] != b.Hash[y]) { moved = true; break; }
        if (!moved) return 0;

        var pre = new int[mid + 1]; // non-uniform row counts in b, for quick totals
        for (int i = 0; i < mid; i++) pre[i + 1] = pre[i] + (b.Uniform[top + i] ? 0 : 1);

        int best = -1, bestTotal = 0;
        double bestScore = 0;
        for (int d = 1; d <= mid - 8; d++)
        {
            int total = pre[mid - d];
            if (total < 12) break;
            int allowed = total * 3 / 100 + 1, miss = 0;
            for (int y = top; y < end - d; y++)
            {
                if (b.Uniform[y]) continue;
                if (b.Hash[y] != a.Hash[y + d] && ++miss > allowed) break;
            }
            if (miss > allowed) continue;
            double score = 1 - miss / (double)total;
            bool better = score > bestScore + 0.002 ||
                          (Math.Abs(score - bestScore) <= 0.002 &&
                           (expected > 0 ? Math.Abs(d - expected) < Math.Abs(best - expected) : total > bestTotal));
            if (better) { best = d; bestScore = score; bestTotal = total; }
        }
        return best;
    }

    static byte[] CopyRows(Frame f, int startRow, int count)
    {
        var rows = new byte[count * f.Stride];
        Buffer.BlockCopy(f.Px, startRow * f.Stride, rows, 0, rows.Length);
        return rows;
    }
}
