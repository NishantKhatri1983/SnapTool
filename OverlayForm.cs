using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace SnapTool;

/// <summary>Where the user pointed. Rect and Anchor are in screen pixels.</summary>
public sealed record OverlayResult(SD.Rectangle Rect, SD.Point Anchor, bool Scroll);

/// <summary>
/// Full-desktop overlay over a frozen screenshot. Drag = region, click = window under the cursor,
/// F/Enter = whole monitor, S = toggle scrolling capture, arrows = nudge cursor, Esc/right-click = cancel.
/// WinForms is used here because it works in raw physical pixels, which keeps mixed-DPI monitors simple.
/// </summary>
internal sealed class OverlayForm : WF.Form
{
    readonly SD.Bitmap _img, _dim;
    readonly SD.Rectangle _vs;
    readonly List<WinInfo> _windows;
    readonly AppSettings _settings;
    readonly SD.Color _accent;
    bool _scroll;

    SD.Point _mouse;
    bool _down, _dragging;
    SD.Point _downPt;
    SD.Rectangle? _hover;
    List<SD.Rectangle> _lastDirty = new();

    float _scale = 1;
    SD.Font? _font, _fontBold;

    public OverlayResult? Result { get; private set; }

    public OverlayForm(SD.Bitmap shot, SD.Rectangle virtualScreen, List<WinInfo> windows, AppSettings settings, bool scroll)
    {
        _vs = virtualScreen;
        _windows = windows;
        _settings = settings;
        _scroll = scroll;
        _accent = ParseColor(settings.OverlayAccentColor, SD.Color.FromArgb(30, 136, 229));

        _img = shot.Clone(new SD.Rectangle(0, 0, shot.Width, shot.Height), PixelFormat.Format32bppPArgb);
        _dim = _img.Clone(new SD.Rectangle(0, 0, _img.Width, _img.Height), PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(_dim))
        using (var shade = new SD.SolidBrush(SD.Color.FromArgb(settings.OverlayDimPercent * 255 / 100, 0, 0, 0)))
            g.FillRectangle(shade, 0, 0, _dim.Width, _dim.Height);

        Text = "SnapTool capture";
        FormBorderStyle = WF.FormBorderStyle.None;
        StartPosition = WF.FormStartPosition.Manual;
        AutoScaleMode = WF.AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        Cursor = WF.Cursors.Cross;
        BackColor = SD.Color.Black;
        Bounds = _vs;
        SetStyle(WF.ControlStyles.AllPaintingInWmPaint | WF.ControlStyles.OptimizedDoubleBuffer |
                 WF.ControlStyles.UserPaint | WF.ControlStyles.Opaque, true);
    }

    static SD.Color ParseColor(string hex, SD.Color fallback)
    {
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length == 6) h = "FF" + h;
            return SD.Color.FromArgb(Convert.ToInt32(h, 16));
        }
        catch { return fallback; }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Bounds = _vs;
        _scale = DeviceDpi / 96f;
        _font = new SD.Font("Segoe UI", 13 * _scale, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        _fontBold = new SD.Font("Segoe UI Semibold", 13 * _scale, SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Native.SetForegroundWindow(Handle);
        _mouse = PointToClient(WF.Cursor.Position);
        UpdateHover();
        Invalidate();
    }

    protected override void WndProc(ref WF.Message m)
    {
        // Keep covering the whole virtual desktop even if Windows says the DPI changed.
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    // ---------- input ----------

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        _mouse = e.Location;
        if (_down && !_dragging && (Math.Abs(_mouse.X - _downPt.X) > 3 || Math.Abs(_mouse.Y - _downPt.Y) > 3))
            _dragging = true;
        if (!_dragging) UpdateHover();
        RefreshDecor();
    }

    protected override void OnMouseDown(WF.MouseEventArgs e)
    {
        if (e.Button == WF.MouseButtons.Left) { _down = true; _downPt = e.Location; }
        else if (e.Button == WF.MouseButtons.Right) Cancel();
    }

    protected override void OnMouseUp(WF.MouseEventArgs e)
    {
        if (e.Button != WF.MouseButtons.Left || !_down) return;
        _down = false;
        if (_dragging)
        {
            _dragging = false;
            var r = Normalize(_downPt, e.Location);
            if (r.Width >= 2 && r.Height >= 2)
                Finish(r, new SD.Point(r.X + r.Width / 2, r.Y + r.Height / 2));
            else RefreshDecor();
        }
        else if (_hover is { } h)
        {
            Finish(h, e.Location);
        }
    }

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case WF.Keys.Escape: Cancel(); break;
            case WF.Keys.F:
            case WF.Keys.Enter:
                Finish(MonitorAt(_mouse), _mouse);
                break;
            case WF.Keys.S:
                _scroll = !_scroll;
                Invalidate();
                break;
            case WF.Keys.Left: Nudge(-1, 0, e.Shift); break;
            case WF.Keys.Right: Nudge(1, 0, e.Shift); break;
            case WF.Keys.Up: Nudge(0, -1, e.Shift); break;
            case WF.Keys.Down: Nudge(0, 1, e.Shift); break;
        }
    }

    static void Nudge(int dx, int dy, bool fast)
    {
        int step = fast ? 10 : 1;
        var p = WF.Cursor.Position;
        WF.Cursor.Position = new SD.Point(p.X + dx * step, p.Y + dy * step);
    }

    void Cancel()
    {
        Result = null;
        Close();
    }

    void Finish(SD.Rectangle clientRect, SD.Point clientAnchor)
    {
        var r = clientRect;
        r.Offset(_vs.Location);
        Result = new OverlayResult(r, new SD.Point(clientAnchor.X + _vs.X, clientAnchor.Y + _vs.Y), _scroll);
        Close();
    }

    void UpdateHover()
    {
        var sp = new SD.Point(_mouse.X + _vs.X, _mouse.Y + _vs.Y);
        foreach (var w in _windows)
        {
            if (!w.Bounds.Contains(sp)) continue;
            var r = SD.Rectangle.Intersect(w.Bounds, _vs);
            r.Offset(-_vs.X, -_vs.Y);
            _hover = r;
            return;
        }
        _hover = MonitorAt(_mouse);
    }

    SD.Rectangle MonitorAt(SD.Point client)
    {
        var b = WF.Screen.FromPoint(new SD.Point(client.X + _vs.X, client.Y + _vs.Y)).Bounds;
        b.Offset(-_vs.X, -_vs.Y);
        return b;
    }

    static SD.Rectangle Normalize(SD.Point a, SD.Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);

    SD.Rectangle? Selection => _dragging ? Normalize(_downPt, _mouse) : _hover;

    // ---------- painting ----------

    /// <summary>Rectangles of everything drawn on top of the dimmed screenshot (for partial repaints).</summary>
    List<SD.Rectangle> DecorRects()
    {
        var list = new List<SD.Rectangle>
        {
            new(0, _mouse.Y - 2, Width, 5),
            new(_mouse.X - 2, 0, 5, Height),
        };
        if (Selection is { } sel)
        {
            var r = sel;
            r.Inflate((int)(4 * _scale) + 2, (int)(4 * _scale) + 2);
            list.Add(r);
            list.Add(LabelRect(sel));
        }
        if (_settings.ShowMagnifier) list.Add(LoupeRect());
        list.Add(HintRect());
        return list;
    }

    void RefreshDecor()
    {
        var now = DecorRects();
        foreach (var r in _lastDirty) Invalidate(r);
        foreach (var r in now) Invalidate(r);
        _lastDirty = now;
    }

    SD.Size Measure(string text, SD.Font font)
    {
        var s = WF.TextRenderer.MeasureText(text, font);
        return new SD.Size(s.Width, s.Height);
    }

    string LabelText(SD.Rectangle sel) => $"{sel.Width} × {sel.Height}" + (_scroll ? "   ⇣ scroll" : "");

    SD.Rectangle LabelRect(SD.Rectangle sel)
    {
        var size = Measure(LabelText(sel), _fontBold ?? Font);
        int pad = (int)(6 * _scale);
        int w = size.Width + pad * 2, h = size.Height + pad;
        int y = sel.Y - h - (int)(4 * _scale);
        if (y < 0) y = sel.Y + (int)(4 * _scale);
        return new SD.Rectangle(sel.X, y, w, h);
    }

    const int LoupeZoom = 8;

    SD.Rectangle LoupeRect()
    {
        int size = (int)(152 * _scale), info = (int)(30 * _scale), off = (int)(22 * _scale);
        var mon = MonitorAt(_mouse);
        int x = _mouse.X + off, y = _mouse.Y + off;
        if (x + size > mon.Right) x = _mouse.X - off - size;
        if (y + size + info > mon.Bottom) y = _mouse.Y - off - size - info;
        return new SD.Rectangle(x, y, size, size + info);
    }

    string HintText => _scroll
        ? (string.Equals(_settings.ScrollMode, "Auto", StringComparison.OrdinalIgnoreCase)
            ? "SCROLLING CAPTURE   Click a window or drag a region – it will auto-scroll   •   S: normal capture   •   Esc: cancel"
            : "SCROLLING CAPTURE   Click a window or drag the area, then scroll it yourself and press Enter / End   •   S: normal   •   Esc: cancel")
        : "Drag: region   •   Click: window   •   F / Enter: full screen   •   S: scrolling capture   •   Esc: cancel";

    SD.Rectangle HintRect()
    {
        var mon = MonitorAt(_mouse);
        var size = Measure(HintText, _font ?? Font);
        int pad = (int)(10 * _scale);
        int w = size.Width + pad * 2, h = size.Height + pad;
        return new SD.Rectangle(mon.X + (mon.Width - w) / 2, mon.Y + (int)(18 * _scale), w, h);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(_dim, clip, clip, SD.GraphicsUnit.Pixel);

        var sel = Selection;
        if (sel is { } s)
        {
            var bright = SD.Rectangle.Intersect(s, clip);
            if (!bright.IsEmpty) g.DrawImage(_img, bright, bright, SD.GraphicsUnit.Pixel);
        }

        g.CompositingMode = CompositingMode.SourceOver;
        using (var cross = new SD.Pen(SD.Color.FromArgb(150, _accent), 1))
        {
            g.DrawLine(cross, 0, _mouse.Y, Width, _mouse.Y);
            g.DrawLine(cross, _mouse.X, 0, _mouse.X, Height);
        }

        if (sel is { } r)
        {
            using var pen = new SD.Pen(_accent, Math.Max(2, 2 * _scale)) { Alignment = PenAlignment.Inset };
            g.DrawRectangle(pen, r);
            DrawLabel(g, LabelRect(r), LabelText(r), _fontBold ?? Font, SD.Color.FromArgb(230, 20, 20, 20));
        }

        if (_settings.ShowMagnifier) DrawLoupe(g);
        DrawLabel(g, HintRect(), HintText, _font ?? Font, _scroll ? SD.Color.FromArgb(235, 200, 90, 0) : SD.Color.FromArgb(225, 25, 25, 25));
    }

    void DrawLabel(SD.Graphics g, SD.Rectangle r, string text, SD.Font font, SD.Color back)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Rounded(r, (int)(6 * _scale)))
        using (var b = new SD.SolidBrush(back))
            g.FillPath(b, path);
        g.SmoothingMode = SmoothingMode.None;
        WF.TextRenderer.DrawText(g, text, font, r, SD.Color.White, back,
            WF.TextFormatFlags.HorizontalCenter | WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.NoPadding);
    }

    static GraphicsPath Rounded(SD.Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void DrawLoupe(SD.Graphics g)
    {
        var box = LoupeRect();
        int size = box.Width;
        var dest = new SD.Rectangle(box.X, box.Y, size, size);
        int src = size / LoupeZoom;
        var srcRect = new SD.Rectangle(_mouse.X - src / 2, _mouse.Y - src / 2, src, src);

        using (var bg = new SD.SolidBrush(SD.Color.Black)) g.FillRectangle(bg, dest);
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(_img, dest, srcRect, SD.GraphicsUnit.Pixel);
        g.Restore(state);

        float cell = size / (float)src;
        float cx = dest.X + (_mouse.X - srcRect.X) * cell, cy = dest.Y + (_mouse.Y - srcRect.Y) * cell;
        using (var pen = new SD.Pen(_accent, 1)) g.DrawRectangle(pen, cx, cy, cell, cell);
        using (var border = new SD.Pen(SD.Color.White, Math.Max(1, _scale))) g.DrawRectangle(border, dest);

        var c = (uint)_mouse.X < _img.Width && (uint)_mouse.Y < _img.Height ? _img.GetPixel(_mouse.X, _mouse.Y) : SD.Color.Black;
        string info = $"{_mouse.X + _vs.X}, {_mouse.Y + _vs.Y}   #{c.R:X2}{c.G:X2}{c.B:X2}";
        var infoRect = new SD.Rectangle(box.X, box.Y + size, size, box.Height - size);
        using (var ib = new SD.SolidBrush(SD.Color.FromArgb(230, 20, 20, 20))) g.FillRectangle(ib, infoRect);
        using (var swatch = new SD.SolidBrush(c))
            g.FillRectangle(swatch, infoRect.X + 4 * _scale, infoRect.Y + infoRect.Height / 2f - 6 * _scale, 12 * _scale, 12 * _scale);
        var textRect = infoRect;
        textRect.X += (int)(20 * _scale);
        textRect.Width -= (int)(20 * _scale);
        WF.TextRenderer.DrawText(g, info, _font ?? Font, textRect, SD.Color.White,
            WF.TextFormatFlags.VerticalCenter | WF.TextFormatFlags.Left | WF.TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _img.Dispose();
            _dim.Dispose();
            _font?.Dispose();
            _fontBold?.Dispose();
        }
        base.Dispose(disposing);
    }
}
