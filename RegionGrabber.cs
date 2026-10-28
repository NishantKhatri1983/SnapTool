using System;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace SnapTool;

/// <summary>
/// Repeatedly copies one screen area into a reusable top-down 32-bit DIB (GDI BitBlt). Much cheaper per frame than
/// creating a new bitmap with Graphics.CopyFromScreen, which matters for scrolling capture at 20+ frames a second.
/// </summary>
internal sealed class RegionGrabber : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    const uint SRCCOPY = 0x00CC0020;

    readonly SD.Rectangle _area;
    readonly IntPtr _screenDc, _memDc, _dib, _oldBitmap, _bits;

    public int Width => _area.Width;
    public int Height => _area.Height;

    public RegionGrabber(SD.Rectangle area)
    {
        _area = area;
        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = area.Width,
            biHeight = -area.Height, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = CreateDIBSection(_memDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero) throw new InvalidOperationException("Couldn't create the screen buffer.");
        _oldBitmap = SelectObject(_memDc, _dib);
    }

    /// <summary>The area's current pixels as BGRA bytes (alpha is always 255).</summary>
    public byte[] Grab()
    {
        var px = new byte[_area.Width * _area.Height * 4];
        Grab(px);
        return px;
    }

    /// <summary>Grabs into an existing buffer of Width * Height * 4 bytes, so repeated grabs allocate nothing.</summary>
    public void Grab(byte[] px)
    {
        BitBlt(_memDc, 0, 0, _area.Width, _area.Height, _screenDc, _area.X, _area.Y, SRCCOPY);
        GdiFlush();
        Marshal.Copy(_bits, px, 0, _area.Width * _area.Height * 4);
        // GDI leaves the alpha byte at 0; make it opaque (a uint OR over the buffer is fast).
        var words = MemoryMarshal.Cast<byte, uint>(px);
        for (int i = 0; i < words.Length; i++) words[i] |= 0xFF000000;
    }

    public void Dispose()
    {
        SelectObject(_memDc, _oldBitmap);
        DeleteObject(_dib);
        DeleteDC(_memDc);
        ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
