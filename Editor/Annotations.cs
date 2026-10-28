using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapTool.Editor;

public sealed class RenderContext
{
    public BitmapSource Image = null!;
    /// <summary>The object whose text is currently in the edit box (its own text is not drawn).</summary>
    public Annotation? Editing;
}

/// <summary>Drawing helpers shared by the annotation types. All coordinates are image pixels.</summary>
public static class Draw
{
    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static Pen Pen(Color c, double width, bool dashed, PenLineCap cap = PenLineCap.Round, PenLineJoin join = PenLineJoin.Round)
    {
        var p = new Pen(Brush(c), Math.Max(0.5, width)) { StartLineCap = cap, EndLineCap = cap, LineJoin = join };
        if (dashed)
        {
            p.DashStyle = new DashStyle(new[] { 2.5, 2.0 }, 0);
            p.DashCap = PenLineCap.Flat;
            p.StartLineCap = p.EndLineCap = PenLineCap.Flat;
        }
        p.Freeze();
        return p;
    }

    public static FormattedText Text(string text, ToolStyle style, Color color)
    {
        var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
            style.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face,
            Math.Max(4, style.FontSize), Brush(color), 1.0);
    }

    public static Geometry Shape(Rect r, string shape)
    {
        Geometry g = shape switch
        {
            "Ellipse" => new EllipseGeometry(r),
            "Rounded" => new RectangleGeometry(r, CornerRadius(r), CornerRadius(r)),
            _ => new RectangleGeometry(r),
        };
        g.Freeze();
        return g;
    }

    public static double CornerRadius(Rect r) => Math.Min(14, Math.Min(r.Width, r.Height) / 3);

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + t * ab)).Length;
    }

    public static Rect Normalize(Point a, Point b) => new(a, b);

    /// <summary>Handle positions for a box: TL, T, TR, R, BR, B, BL, L.</summary>
    public static Point[] BoxHandles(Rect r) => new[]
    {
        r.TopLeft, new Point(r.Left + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Top + r.Height / 2),
        r.BottomRight, new Point(r.Left + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Top + r.Height / 2),
    };

    /// <summary>Moves one box handle. p1 must be the top-left and p2 the bottom-right when a drag starts.</summary>
    public static void MoveBoxHandle(ref Point p1, ref Point p2, int i, Point p, bool square)
    {
        switch (i)
        {
            case 0: p1 = p; break;
            case 1: p1.Y = p.Y; break;
            case 2: p1.Y = p.Y; p2.X = p.X; break;
            case 3: p2.X = p.X; break;
            case 4: p2 = p; break;
            case 5: p2.Y = p.Y; break;
            case 6: p1.X = p.X; p2.Y = p.Y; break;
            case 7: p1.X = p.X; break;
        }
        if (square && i == 4)
        {
            var d = p2 - p1;
            double s = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            p2 = p1 + new Vector(d.X < 0 ? -s : s, d.Y < 0 ? -s : s);
        }
    }

    /// <summary>Copies the region and averages it into blocks (irreversible, unlike a light blur).</summary>
    public static BitmapSource Pixelate(BitmapSource src, Int32Rect r, int block)
    {
        block = Math.Max(2, block);
        int w = r.Width, h = r.Height, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(r, px, stride, 0);
        for (int by = 0; by < h; by += block)
        for (int bx = 0; bx < w; bx += block)
        {
            int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
            long b = 0, g = 0, rr = 0;
            for (int y = by; y < by + bh; y++)
            for (int x = bx; x < bx + bw; x++)
            {
                int i = y * stride + x * 4;
                b += px[i]; g += px[i + 1]; rr += px[i + 2];
            }
            int n = bw * bh;
            byte B = (byte)(b / n), G = (byte)(g / n), R = (byte)(rr / n);
            for (int y = by; y < by + bh; y++)
            for (int x = bx; x < bx + bw; x++)
            {
                int i = y * stride + x * 4;
                px[i] = B; px[i + 1] = G; px[i + 2] = R; px[i + 3] = 255;
            }
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>One markup object. It stays editable (move, resize, restyle) until the image is exported.</summary>
public abstract class Annotation
{
    public ToolStyle Style { get; set; } = new();
    public abstract string Tool { get; }
    public abstract Rect Bounds { get; }
    public abstract void Render(DrawingContext dc, RenderContext ctx);
    public virtual bool Hit(Point p, double tolerance)
    {
        var b = Bounds;
        b.Inflate(tolerance, tolerance);
        return b.Contains(p);
    }
    public abstract void Offset(Vector v);
    public abstract void Scale(double sx, double sy);
    public virtual Point[] Handles => Array.Empty<Point>();
    public virtual void BeginHandleDrag() { }
    public virtual void MoveHandle(int index, Point p, bool constrain) { }
    public virtual bool IsDegenerate => false;

    public Annotation Clone()
    {
        var c = (Annotation)MemberwiseClone();
        c.Style = Style.Clone();
        return c;
    }

    protected Color StrokeColor => Draw.ParseColor(Style.Color, Colors.Red);
    protected Color? FillColor => string.IsNullOrWhiteSpace(Style.Fill) ? null : Draw.ParseColor(Style.Fill, Colors.Transparent);

    protected void ScaleStyle(double sx, double sy)
    {
        double k = (sx + sy) / 2;
        Style.Width *= k;
        Style.FontSize = Math.Max(4, Style.FontSize * k);
        Style.BlurBlock = Math.Max(2, (int)Math.Round(Style.BlurBlock * k));
    }

    protected static Point ScalePoint(Point p, double sx, double sy) => new(p.X * sx, p.Y * sy);
}

/// <summary>Arrow and Line: a segment with a choice of end caps (none, arrow head, dot, square).</summary>
public sealed class LineAnnotation : Annotation
{
    readonly string _tool;
    public Point A, B;

    public LineAnnotation(string tool) => _tool = tool;
    public override string Tool => _tool;

    double HeadLength => Math.Max(10, Style.Width * 3.2);
    double HeadHalfWidth => Math.Max(6, Style.Width * 1.7);

    public override Rect Bounds
    {
        get
        {
            var r = Draw.Normalize(A, B);
            double pad = Math.Max(HeadHalfWidth, Style.Width);
            r.Inflate(pad, pad);
            return r;
        }
    }

    public override bool Hit(Point p, double tolerance) =>
        Draw.DistanceToSegment(p, A, B) <= Math.Max(Style.Width / 2, 3) + tolerance;

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var v = B - A;
        double len = v.Length;
        if (len < 0.01) return;
        var dir = v / len;
        var color = StrokeColor;
        bool startArrow = Style.StartCap == "Arrow", endArrow = Style.EndCap == "Arrow";
        double head = Math.Min(HeadLength, len * (startArrow && endArrow ? 0.45 : 0.7));

        Point a = A, b = B;
        if (endArrow) b = B - dir * head * 0.7;
        if (startArrow) a = A + dir * head * 0.7;
        dc.DrawLine(Draw.Pen(color, Style.Width, Style.Dashed), a, b);
        DrawCap(dc, Style.EndCap, B, dir, head, color);
        DrawCap(dc, Style.StartCap, A, -dir, head, color);
    }

    void DrawCap(DrawingContext dc, string cap, Point tip, Vector dir, double head, Color color)
    {
        var brush = Draw.Brush(color);
        switch (cap)
        {
            case "Arrow":
            {
                double half = HeadHalfWidth * head / HeadLength;
                var perp = new Vector(-dir.Y, dir.X);
                var basePt = tip - dir * head;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(tip, true, true);
                    ctx.LineTo(basePt + perp * half, true, true);
                    ctx.LineTo(tip - dir * head * 0.78, true, true);
                    ctx.LineTo(basePt - perp * half, true, true);
                }
                g.Freeze();
                dc.DrawGeometry(brush, Draw.Pen(color, 1, false), g);
                break;
            }
            case "Dot":
            {
                double r = Math.Max(3.5, Style.Width * 1.5);
                dc.DrawEllipse(brush, null, tip, r, r);
                break;
            }
            case "Square":
            {
                double s = Math.Max(7, Style.Width * 2.8);
                dc.DrawRectangle(brush, null, new Rect(tip.X - s / 2, tip.Y - s / 2, s, s));
                break;
            }
        }
    }

    public override void Offset(Vector v) { A += v; B += v; }

    public override void Scale(double sx, double sy)
    {
        A = ScalePoint(A, sx, sy);
        B = ScalePoint(B, sx, sy);
        ScaleStyle(sx, sy);
    }

    public override Point[] Handles => new[] { A, B };

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 0) A = constrain ? Snap45(B, p) : p;
        else B = constrain ? Snap45(A, p) : p;
    }

    static Point Snap45(Point origin, Point p)
    {
        var v = p - origin;
        double step = Math.PI / 4, angle = Math.Round(Math.Atan2(v.Y, v.X) / step) * step;
        return origin + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    public override bool IsDegenerate => (B - A).Length < 3;
}

/// <summary>Rectangle (frame or filled), Highlight (translucent) and Blur (pixelate) — all box-shaped.</summary>
public sealed class BoxAnnotation : Annotation
{
    readonly string _tool;
    public Point P1, P2;

    BitmapSource? _patch, _patchSource;
    Int32Rect _patchRect;
    int _patchBlock;

    public BoxAnnotation(string tool) => _tool = tool;
    public override string Tool => _tool;
    public Rect R => Draw.Normalize(P1, P2);

    public override Rect Bounds
    {
        get
        {
            var r = R;
            if (_tool == "Rectangle") r.Inflate(Style.Width / 2, Style.Width / 2);
            return r;
        }
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var r = R;
        if (r.Width < 0.5 || r.Height < 0.5) return;
        switch (_tool)
        {
            case "Highlight":
            {
                var c = StrokeColor;
                if (c.A == 255) c.A = 0x73; // ~45%: see-through like a marker
                dc.DrawGeometry(Draw.Brush(c), null, Draw.Shape(r, Style.Shape));
                break;
            }
            case "Blur":
            {
                var ir = PixelRect(r, ctx.Image);
                if (ir.Width < 1 || ir.Height < 1) return;
                var area = new Rect(ir.X, ir.Y, ir.Width, ir.Height);
                dc.PushClip(Draw.Shape(area, Style.Shape));
                dc.DrawImage(Patch(ctx.Image, ir), area);
                dc.Pop();
                break;
            }
            default:
            {
                Brush? fill = FillColor is { } f ? Draw.Brush(f) : null;
                var join = Style.Shape == "Rectangle" ? PenLineJoin.Miter : PenLineJoin.Round;
                var pen = Style.Width > 0.1 ? Draw.Pen(StrokeColor, Style.Width, Style.Dashed, PenLineCap.Flat, join) : null;
                dc.DrawGeometry(fill, pen, Draw.Shape(r, Style.Shape));
                break;
            }
        }
    }

    static Int32Rect PixelRect(Rect r, BitmapSource img)
    {
        int x0 = (int)Math.Floor(Math.Max(0, r.Left)), y0 = (int)Math.Floor(Math.Max(0, r.Top));
        int x1 = (int)Math.Ceiling(Math.Min(img.PixelWidth, r.Right)), y1 = (int)Math.Ceiling(Math.Min(img.PixelHeight, r.Bottom));
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    BitmapSource Patch(BitmapSource img, Int32Rect ir)
    {
        if (_patch == null || _patchSource != img || _patchRect != ir || _patchBlock != Style.BlurBlock)
        {
            _patch = Draw.Pixelate(img, ir, Style.BlurBlock);
            _patchSource = img;
            _patchRect = ir;
            _patchBlock = Style.BlurBlock;
        }
        return _patch;
    }

    public override void Offset(Vector v) { P1 += v; P2 += v; }

    public override void Scale(double sx, double sy)
    {
        P1 = ScalePoint(P1, sx, sy);
        P2 = ScalePoint(P2, sx, sy);
        ScaleStyle(sx, sy);
    }

    public override Point[] Handles => Draw.BoxHandles(R);

    public override void BeginHandleDrag()
    {
        var r = R;
        P1 = r.TopLeft;
        P2 = r.BottomRight;
    }

    public override void MoveHandle(int index, Point p, bool constrain) => Draw.MoveBoxHandle(ref P1, ref P2, index, p, constrain);

    public override bool IsDegenerate => R.Width < 3 || R.Height < 3;
}

/// <summary>Free text with an optional background box.</summary>
public sealed class TextAnnotation : Annotation
{
    public Point Pos;
    public string Text = "";

    public override string Tool => "Text";
    double Pad => Math.Max(3, Style.FontSize * 0.25);

    FormattedText Layout() => Draw.Text(Text.Length == 0 ? " " : Text, Style, StrokeColor);

    public override Rect Bounds
    {
        get
        {
            var ft = Layout();
            var r = new Rect(Pos.X, Pos.Y, Math.Max(ft.WidthIncludingTrailingWhitespace, Style.FontSize * 0.5), ft.Height);
            r.Inflate(Pad, Pad * 0.5);
            return r;
        }
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        if (FillColor is { } f) dc.DrawRoundedRectangle(Draw.Brush(f), null, Bounds, 4, 4);
        if (ctx.Editing != this && Text.Length > 0) dc.DrawText(Layout(), Pos);
    }

    public override void Offset(Vector v) => Pos += v;

    public override void Scale(double sx, double sy)
    {
        Pos = ScalePoint(Pos, sx, sy);
        ScaleStyle(sx, sy);
    }

    public override bool IsDegenerate => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Speech-bubble callout: rounded body with wrapped, centered text and a draggable tail.</summary>
public sealed class CalloutAnnotation : Annotation
{
    public Point P1, P2, Tip;
    public string Text = "";

    public override string Tool => "Callout";
    public Rect R => Draw.Normalize(P1, P2);
    public double Pad => Math.Max(8, Style.FontSize * 0.5);

    public FormattedText Layout()
    {
        var ft = Draw.Text(Text.Length == 0 ? " " : Text, Style, StrokeColor);
        ft.MaxTextWidth = Math.Max(10, R.Width - 2 * Pad);
        ft.TextAlignment = TextAlignment.Center;
        return ft;
    }

    /// <summary>Top-left of the text block (also where the edit box goes).</summary>
    public Point TextOrigin()
    {
        var r = R;
        var ft = Layout();
        return new Point(r.Left + Pad, r.Top + Math.Max(Pad * 0.5, (r.Height - ft.Height) / 2));
    }

    public override Rect Bounds
    {
        get
        {
            var r = R;
            r.Union(Tip);
            r.Inflate(Style.Width, Style.Width);
            return r;
        }
    }

    public override bool Hit(Point p, double tolerance)
    {
        var r = R;
        r.Inflate(tolerance, tolerance);
        if (r.Contains(p)) return true;
        var c = new Point(R.Left + R.Width / 2, R.Top + R.Height / 2);
        return !R.Contains(Tip) && Draw.DistanceToSegment(p, c, Tip) <= tolerance + 6;
    }

    Geometry BuildGeometry()
    {
        var r = R;
        double rad = Draw.CornerRadius(r);
        Geometry body = new RectangleGeometry(r, rad, rad);
        if (r.Contains(Tip)) return body;

        double half = Math.Clamp(Math.Min(r.Width, r.Height) * 0.18, 6, 22);
        double cx = Math.Clamp(Tip.X, r.Left, r.Right), cy = Math.Clamp(Tip.Y, r.Top, r.Bottom);
        double dx = Tip.X - cx, dy = Tip.Y - cy;
        Point b1, b2;
        if (Math.Abs(dy) >= Math.Abs(dx))
        {
            double y = dy > 0 ? r.Bottom - 1 : r.Top + 1;
            double margin = Math.Min(rad + half, r.Width / 2);
            double x = Math.Clamp(Tip.X, r.Left + margin, r.Right - margin);
            b1 = new Point(x - half, y);
            b2 = new Point(x + half, y);
        }
        else
        {
            double x = dx > 0 ? r.Right - 1 : r.Left + 1;
            double margin = Math.Min(rad + half, r.Height / 2);
            double y = Math.Clamp(Tip.Y, r.Top + margin, r.Bottom - margin);
            b1 = new Point(x, y - half);
            b2 = new Point(x, y + half);
        }
        var tail = new StreamGeometry();
        using (var ctx = tail.Open())
        {
            ctx.BeginFigure(b1, true, true);
            ctx.LineTo(Tip, true, true);
            ctx.LineTo(b2, true, true);
        }
        var g = new CombinedGeometry(GeometryCombineMode.Union, body, tail);
        g.Freeze();
        return g;
    }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var r = R;
        if (r.Width < 1 || r.Height < 1) return;
        Brush? fill = FillColor is { } f ? Draw.Brush(f) : null;
        var pen = Style.Width > 0.1 ? Draw.Pen(StrokeColor, Style.Width, Style.Dashed) : null;
        dc.DrawGeometry(fill, pen, BuildGeometry());
        if (ctx.Editing != this && Text.Length > 0) dc.DrawText(Layout(), TextOrigin());
    }

    /// <summary>Grows the body so the text always fits.</summary>
    public void FitToText()
    {
        var r = R;
        P1 = r.TopLeft;
        P2 = r.BottomRight;
        double need = Layout().Height + Pad * 1.5;
        if (r.Height < need) P2 = new Point(P2.X, P1.Y + need);
    }

    public override void Offset(Vector v) { P1 += v; P2 += v; Tip += v; }

    public override void Scale(double sx, double sy)
    {
        P1 = ScalePoint(P1, sx, sy);
        P2 = ScalePoint(P2, sx, sy);
        Tip = ScalePoint(Tip, sx, sy);
        ScaleStyle(sx, sy);
    }

    public override Point[] Handles
    {
        get
        {
            var list = new List<Point>(Draw.BoxHandles(R)) { Tip };
            return list.ToArray();
        }
    }

    public override void BeginHandleDrag()
    {
        var r = R;
        P1 = r.TopLeft;
        P2 = r.BottomRight;
    }

    public override void MoveHandle(int index, Point p, bool constrain)
    {
        if (index == 8) Tip = p;
        else Draw.MoveBoxHandle(ref P1, ref P2, index, p, false);
    }

    public override bool IsDegenerate => R.Width < 3 || R.Height < 3;
}
