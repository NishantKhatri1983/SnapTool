using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnapTool.Editor;

public static class Imaging
{
    /// <summary>Copies any bitmap into a frozen Bgra32 bitmap at 96 dpi, so 1 unit == 1 pixel everywhere.</summary>
    public static BitmapSource Normalize(BitmapSource src)
    {
        BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource LoadFile(string path)
    {
        using var fs = File.OpenRead(path); // stream, so the file isn't locked or cached by URI
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = fs;
        bi.EndInit();
        bi.Freeze();
        return Normalize(bi);
    }
}

/// <summary>
/// The drawing surface: shows the image plus vector annotations, handles the mouse for every tool,
/// owns undo/redo, crop, resize and flattening. Annotation coordinates are image pixels;
/// <see cref="Zoom"/> 1.0 means one image pixel per screen pixel.
/// </summary>
public sealed class EditorCanvas : FrameworkElement
{
    static readonly Color Accent = Color.FromRgb(0x1E, 0x88, 0xE5);

    public BitmapSource Image { get; private set; } = null!;
    public List<Annotation> Items { get; } = new();
    public Annotation? Selected { get; private set; }
    public double Zoom { get; private set; } = 1;
    public Func<string, ToolStyle> StyleProvider { get; set; } = _ => new ToolStyle();
    public Rect? CropRect => _crop;
    public bool IsEditingText => _editor != null;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event Action? ToolChanged, SelectionChanged, ContentChanged, ViewChanged;
    public event Action<Point>? PointerMoved;

    string _tool = "Arrow";
    double _dpi = 1;
    double K => Zoom / _dpi; // DIPs per image pixel

    sealed record Snapshot(BitmapSource Image, List<Annotation> Items);
    readonly Stack<Snapshot> _undo = new(), _redo = new();

    enum DragMode { None, Create, Move, Handle, CropNew, CropMove, CropHandle }
    DragMode _drag;
    Point _start, _last;
    int _handle;
    bool _undoPushed;
    Annotation? _creating;
    Rect? _crop;
    Rect _cropStart;

    TextBox? _editor;
    Annotation? _editing;
    bool _editingNew;
    string _editStartText = "";
    int _editUndoDepth;

    public EditorCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        Loaded += (_, _) => { _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; InvalidateMeasure(); ViewChanged?.Invoke(); };
    }

    // ---------- document ----------

    public void Load(BitmapSource image)
    {
        Image = image.Format == PixelFormats.Bgra32 && image.IsFrozen && image.DpiX == 96 ? image : Imaging.Normalize(image);
        Items.Clear();
        _undo.Clear();
        _redo.Clear();
        _crop = null;
        Select(null);
        InvalidateMeasure();
        InvalidateVisual();
    }

    public string Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            CommitEdit();
            if (_tool == "Crop") _crop = null;
            _tool = value;
            if (Selected != null && (value == "Crop" || (value != "Select" && Selected.Tool != value))) Select(null);
            Cursor = value == "Select" ? Cursors.Arrow : Cursors.Cross;
            ToolChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public void Select(Annotation? a)
    {
        if (Selected == a) return;
        Selected = a;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Call after changing the selected object's style from outside.</summary>
    public void Refresh()
    {
        if (_editing is CalloutAnnotation c) c.FitToText();
        UpdateEditorLook();
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ---------- undo ----------

    public void PushUndo()
    {
        _undo.Push(new Snapshot(Image, Items.Select(i => i.Clone()).ToList()));
        _redo.Clear();
    }

    public void Undo()
    {
        CommitEdit();
        if (_undo.Count == 0) return;
        _redo.Push(new Snapshot(Image, Items.Select(i => i.Clone()).ToList()));
        Restore(_undo.Pop());
    }

    public void Redo()
    {
        CommitEdit();
        if (_redo.Count == 0) return;
        _undo.Push(new Snapshot(Image, Items.Select(i => i.Clone()).ToList()));
        Restore(_redo.Pop());
    }

    void Restore(Snapshot s)
    {
        Image = s.Image;
        Items.Clear();
        Items.AddRange(s.Items.Select(i => i.Clone()));
        _crop = null;
        Select(null);
        InvalidateMeasure();
        InvalidateVisual();
        ContentChanged?.Invoke();
        ViewChanged?.Invoke();
    }

    // ---------- commands ----------

    public void DeleteSelected()
    {
        if (Selected == null) return;
        PushUndo();
        Items.Remove(Selected);
        Select(null);
        InvalidateVisual();
        ContentChanged?.Invoke();
    }

    public void Escape()
    {
        if (_editor != null) CommitEdit();
        else if (_tool == "Crop" && _crop != null) { _crop = null; InvalidateVisual(); ViewChanged?.Invoke(); }
        else if (_tool == "Crop") Tool = "Select";
        else Select(null);
    }

    public void Nudge(double dx, double dy)
    {
        if (_tool == "Crop" && _crop is { } c)
        {
            c.Offset(dx, dy);
            _crop = ClampInside(c);
            InvalidateVisual();
            ViewChanged?.Invoke();
        }
        else if (Selected != null)
        {
            PushUndo();
            Selected.Offset(new Vector(dx, dy));
            InvalidateVisual();
            ContentChanged?.Invoke();
        }
    }

    public void SelectWholeImageForCrop()
    {
        _crop = new Rect(0, 0, Image.PixelWidth, Image.PixelHeight);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public Int32Rect? CropPixels
    {
        get
        {
            if (_crop is not { } c) return null;
            int x0 = (int)Math.Round(Math.Max(0, c.Left)), y0 = (int)Math.Round(Math.Max(0, c.Top));
            int x1 = (int)Math.Round(Math.Min(Image.PixelWidth, c.Right)), y1 = (int)Math.Round(Math.Min(Image.PixelHeight, c.Bottom));
            return x1 - x0 >= 1 && y1 - y0 >= 1 ? new Int32Rect(x0, y0, x1 - x0, y1 - y0) : null;
        }
    }

    public void ApplyCrop()
    {
        if (CropPixels is not { } r) return;
        PushUndo();
        Image = Imaging.Normalize(new CroppedBitmap(Image, r));
        foreach (var a in Items) a.Offset(new Vector(-r.X, -r.Y));
        _crop = null;
        Tool = "Select";
        InvalidateMeasure();
        InvalidateVisual();
        ContentChanged?.Invoke();
        ViewChanged?.Invoke();
    }

    /// <summary>Resamples the image and scales every annotation to match.</summary>
    public void ResizeImage(int width, int height)
    {
        if (width < 1 || height < 1 || (width == Image.PixelWidth && height == Image.PixelHeight)) return;
        CommitEdit();
        PushUndo();
        double sx = width / (double)Image.PixelWidth, sy = height / (double)Image.PixelHeight;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(Image, new Rect(0, 0, width, height));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        Image = Imaging.Normalize(rtb);
        foreach (var a in Items) a.Scale(sx, sy);
        InvalidateMeasure();
        InvalidateVisual();
        ContentChanged?.Invoke();
        ViewChanged?.Invoke();
    }

    /// <summary>The image with all annotations burned in, at the image's native size.</summary>
    public BitmapSource Flatten()
    {
        CommitEdit();
        int w = Image.PixelWidth, h = Image.PixelHeight;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(Image, new Rect(0, 0, w, h));
            var ctx = new RenderContext { Image = Image };
            foreach (var a in Items) a.Render(dc, ctx);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Imaging.Normalize(rtb);
    }

    // ---------- zoom ----------

    public void SetZoom(double zoom)
    {
        Zoom = Math.Clamp(zoom, 0.05, 16);
        RenderOptions.SetBitmapScalingMode(this, Zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        UpdateEditorLook();
        InvalidateMeasure();
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Zoom that fits the image into the given viewport (DIPs), never above 100%.</summary>
    public double FitZoom(Size viewport)
    {
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double z = Math.Min(viewport.Width * _dpi / Image.PixelWidth, viewport.Height * _dpi / Image.PixelHeight);
        return Math.Min(1, z);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpi = newDpi.DpiScaleX;
        UpdateEditorLook();
        InvalidateMeasure();
        ViewChanged?.Invoke();
    }

    // ---------- layout & visuals ----------

    protected override int VisualChildrenCount => _editor == null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => _editor!;

    protected override Size MeasureOverride(Size available)
    {
        _editor?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Image == null ? new Size(0, 0) : new Size(Image.PixelWidth * K, Image.PixelHeight * K);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (_editor != null && _editing != null)
        {
            var origin = _editing is CalloutAnnotation c ? c.TextOrigin() : ((TextAnnotation)_editing).Pos;
            // TextBox draws its text 1px border + 2px internal margin in from its corner.
            _editor.Arrange(new Rect(new Point(origin.X * K - 3, origin.Y * K - 1), _editor.DesiredSize));
        }
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Image == null) return;
        double k = K;
        dc.PushTransform(new ScaleTransform(k, k));
        dc.DrawImage(Image, new Rect(0, 0, Image.PixelWidth, Image.PixelHeight));
        var ctx = new RenderContext { Image = Image, Editing = _editing };
        foreach (var a in Items) a.Render(dc, ctx);
        dc.Pop();

        if (_tool == "Crop") DrawCrop(dc, k);
        else if (Selected != null) DrawSelection(dc, Selected, k);
    }

    static Rect ToView(Rect r, double k) => new(r.X * k, r.Y * k, r.Width * k, r.Height * k);

    static void DrawHandle(DrawingContext dc, Point p, bool round = false)
    {
        var pen = new Pen(new SolidColorBrush(Accent), 1.3);
        if (round) dc.DrawEllipse(Brushes.White, pen, p, 5, 5);
        else dc.DrawRectangle(Brushes.White, pen, new Rect(p.X - 4.5, p.Y - 4.5, 9, 9));
    }

    void DrawSelection(DrawingContext dc, Annotation a, double k)
    {
        if (a is not LineAnnotation)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, Accent.R, Accent.G, Accent.B)), 1) { DashStyle = DashStyles.Dash };
            var r = ToView(a is CalloutAnnotation c ? c.R : a.Bounds, k);
            dc.DrawRectangle(null, pen, r);
        }
        var handles = a.Handles;
        for (int i = 0; i < handles.Length; i++)
            DrawHandle(dc, new Point(handles[i].X * k, handles[i].Y * k), a is LineAnnotation || (a is CalloutAnnotation && i == 8));
    }

    void DrawCrop(DrawingContext dc, double k)
    {
        var full = new Rect(0, 0, Image.PixelWidth * k, Image.PixelHeight * k);
        if (_crop is not { } c)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), null, full);
            return;
        }
        var cr = ToView(c, k);
        var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(cr));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, outside);
        var thirds = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
        for (int i = 1; i < 3; i++)
        {
            dc.DrawLine(thirds, new Point(cr.Left + cr.Width * i / 3, cr.Top), new Point(cr.Left + cr.Width * i / 3, cr.Bottom));
            dc.DrawLine(thirds, new Point(cr.Left, cr.Top + cr.Height * i / 3), new Point(cr.Right, cr.Top + cr.Height * i / 3));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), cr);
        foreach (var h in Draw.BoxHandles(cr)) DrawHandle(dc, h);

        if (CropPixels is { } px)
        {
            var ft = new FormattedText($"{px.Width} × {px.Height}", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var at = new Point(cr.Left, cr.Top - ft.Height - 6 < 0 ? cr.Top + 6 : cr.Top - ft.Height - 6);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(210, 20, 20, 20)), null,
                new Rect(at.X, at.Y - 2, ft.Width + 12, ft.Height + 4), 4, 4);
            dc.DrawText(ft, new Point(at.X + 6, at.Y));
        }
    }

    // ---------- hit testing ----------

    Point ToImage(Point view) => new(view.X / K, view.Y / K);
    double Tolerance => 6 / K;

    Annotation? HitTest(Point p)
    {
        for (int i = Items.Count - 1; i >= 0; i--)
            if (Items[i].Hit(p, Tolerance)) return Items[i];
        return null;
    }

    int HitHandle(Point[] handles, Point p)
    {
        double tol = 7 / K;
        for (int i = handles.Length - 1; i >= 0; i--)
            if (Math.Abs(handles[i].X - p.X) <= tol && Math.Abs(handles[i].Y - p.Y) <= tol) return i;
        return -1;
    }

    static Cursor HandleCursor(int i, bool line) => line ? Cursors.SizeAll : (i % 4) switch
    {
        0 => Cursors.SizeNWSE,
        1 => Cursors.SizeNS,
        2 => Cursors.SizeNESW,
        _ => Cursors.SizeWE,
    };

    Point ClampToImage(Point p) => new(Math.Clamp(p.X, 0, Image.PixelWidth), Math.Clamp(p.Y, 0, Image.PixelHeight));

    Rect ClampInside(Rect r)
    {
        r.Width = Math.Min(r.Width, Image.PixelWidth);
        r.Height = Math.Min(r.Height, Image.PixelHeight);
        r.X = Math.Clamp(r.X, 0, Image.PixelWidth - r.Width);
        r.Y = Math.Clamp(r.Y, 0, Image.PixelHeight - r.Height);
        return r;
    }

    // ---------- mouse ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        var p = ToImage(e.GetPosition(this));
        if (_editor != null)
        {
            if (_editor.IsMouseOver) return;
            CommitEdit();
            return;
        }
        Focus();
        _start = _last = p;
        _undoPushed = false;

        if (e.ClickCount == 2)
        {
            if (_tool == "Crop" && _crop != null) { ApplyCrop(); return; }
            if (HitTest(p) is TextAnnotation or CalloutAnnotation) { BeginEdit(HitTest(p)!, false); return; }
        }

        if (_tool == "Crop")
        {
            if (_crop is { } c)
            {
                int h = HitHandle(Draw.BoxHandles(c), p);
                if (h >= 0) { _drag = DragMode.CropHandle; _handle = h; _cropStart = c; CaptureMouse(); return; }
                if (c.Contains(p)) { _drag = DragMode.CropMove; _cropStart = c; CaptureMouse(); return; }
            }
            _start = ClampToImage(p);
            _crop = new Rect(_start, _start);
            _drag = DragMode.CropNew;
            CaptureMouse();
            return;
        }

        if (Selected != null)
        {
            int h = HitHandle(Selected.Handles, p);
            if (h >= 0)
            {
                _drag = DragMode.Handle;
                _handle = h;
                Selected.BeginHandleDrag();
                CaptureMouse();
                return;
            }
        }

        var hit = HitTest(p);
        if (_tool == "Select" || (hit != null && (hit == Selected || hit.Tool == _tool)))
        {
            Select(hit);
            if (hit != null) { _drag = DragMode.Move; CaptureMouse(); }
            return;
        }

        if (_tool == "Text")
        {
            PushUndo();
            var style = StyleProvider("Text").Clone();
            var t = new TextAnnotation { Style = style, Pos = new Point(p.X, p.Y - style.FontSize * 0.65) };
            Items.Add(t);
            BeginEdit(t, true, undoPushed: true);
            return;
        }

        PushUndo();
        _undoPushed = true;
        _creating = _tool switch
        {
            "Arrow" or "Line" => new LineAnnotation(_tool) { A = p, B = p },
            "Callout" => new CalloutAnnotation { P1 = p, P2 = p, Tip = p },
            _ => new BoxAnnotation(_tool) { P1 = p, P2 = p },
        };
        _creating.Style = StyleProvider(_tool).Clone();
        Items.Add(_creating);
        Select(_creating);
        _drag = DragMode.Create;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToImage(e.GetPosition(this));
        PointerMoved?.Invoke(p);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_drag)
        {
            case DragMode.Create when _creating != null:
                _creating.MoveHandle(_creating is LineAnnotation ? 1 : 4, p, shift);
                break;
            case DragMode.Move when Selected != null:
                if (!_undoPushed)
                {
                    if ((p - _start).Length * K < 2) return;
                    PushUndo();
                    _undoPushed = true;
                }
                Selected.Offset(p - _last);
                _last = p;
                break;
            case DragMode.Handle when Selected != null:
                if (!_undoPushed) { PushUndo(); _undoPushed = true; }
                Selected.MoveHandle(_handle, p, shift);
                break;
            case DragMode.CropNew:
                _crop = new Rect(_start, ClampToImage(p));
                ViewChanged?.Invoke();
                break;
            case DragMode.CropMove:
            {
                var r = _cropStart;
                r.Offset(p - _start);
                _crop = ClampInside(r);
                ViewChanged?.Invoke();
                break;
            }
            case DragMode.CropHandle:
            {
                Point a = _cropStart.TopLeft, b = _cropStart.BottomRight;
                Draw.MoveBoxHandle(ref a, ref b, _handle, ClampToImage(p), false);
                _crop = new Rect(a, b);
                ViewChanged?.Invoke();
                break;
            }
            default:
                UpdateHoverCursor(p);
                return;
        }
        InvalidateVisual();
    }

    void UpdateHoverCursor(Point p)
    {
        if (_tool == "Crop")
        {
            if (_crop is { } c)
            {
                int h = HitHandle(Draw.BoxHandles(c), p);
                Cursor = h >= 0 ? HandleCursor(h, false) : c.Contains(p) ? Cursors.SizeAll : Cursors.Cross;
            }
            else Cursor = Cursors.Cross;
            return;
        }
        if (Selected != null)
        {
            int h = HitHandle(Selected.Handles, p);
            if (h >= 0) { Cursor = Selected is CalloutAnnotation && h == 8 ? Cursors.Hand : HandleCursor(h, Selected is LineAnnotation); return; }
        }
        var hit = HitTest(p);
        if (hit != null && (_tool == "Select" || hit == Selected || hit.Tool == _tool)) Cursor = Cursors.SizeAll;
        else Cursor = _tool == "Select" ? Cursors.Arrow : _tool == "Text" ? Cursors.IBeam : Cursors.Cross;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var mode = _drag;
        _drag = DragMode.None;
        ReleaseMouseCapture();

        if (mode == DragMode.Create && _creating != null)
        {
            var created = _creating;
            _creating = null;
            if (created is CalloutAnnotation c)
            {
                PlaceCallout(c);
                BeginEdit(c, true, undoPushed: true);
                return;
            }
            if (created.IsDegenerate)
            {
                Items.Remove(created);
                _undo.Pop();
                Select(null);
            }
            else ContentChanged?.Invoke();
        }
        else if ((mode == DragMode.Move || mode == DragMode.Handle) && _undoPushed)
        {
            if (Selected is CalloutAnnotation c) c.FitToText();
            ContentChanged?.Invoke();
        }
        else if (mode == DragMode.CropNew && _crop is { } cr && (cr.Width < 2 || cr.Height < 2))
        {
            _crop = null;
            ViewChanged?.Invoke();
        }
        InvalidateVisual();
    }

    /// <summary>A click places a default bubble up-right of the point with the tail on it; a drag defines the body.</summary>
    void PlaceCallout(CalloutAnnotation c)
    {
        var r = c.R;
        if (r.Width < 12 || r.Height < 12)
        {
            double w = Math.Max(170, c.Style.FontSize * 10), h = Math.Max(56, c.Style.FontSize * 2.8);
            var tip = _start;
            double left = tip.X + 24, top = tip.Y - h - 40;
            if (left + w > Image.PixelWidth) left = Math.Max(0, tip.X - w - 24);
            if (top < 0) top = tip.Y + 40;
            c.P1 = new Point(left, top);
            c.P2 = new Point(left + w, top + h);
            c.Tip = tip;
        }
        else
        {
            c.P1 = r.TopLeft;
            c.P2 = r.BottomRight;
            double drop = Math.Max(28, r.Height * 0.6);
            c.Tip = r.Bottom + drop <= Image.PixelHeight
                ? new Point(r.Left + r.Width * 0.2, r.Bottom + drop)
                : new Point(r.Left + r.Width * 0.2, r.Top - drop);
        }
    }

    // ---------- in-place text editing ----------

    public void BeginEdit(Annotation a, bool isNew, bool undoPushed = false)
    {
        CommitEdit();
        if (!undoPushed) PushUndo();
        _editUndoDepth = _undo.Count;
        _editing = a;
        _editingNew = isNew;
        _editStartText = a is TextAnnotation t ? t.Text : ((CalloutAnnotation)a).Text;
        Select(a);

        var tb = new TextBox
        {
            Text = _editStartText,
            AcceptsReturn = true,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(160, Accent.R, Accent.G, Accent.B)),
            Padding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            MinWidth = 24,
        };
        _editor = tb;
        UpdateEditorLook();
        tb.TextChanged += (_, _) =>
        {
            if (a is TextAnnotation ta) ta.Text = tb.Text;
            else if (a is CalloutAnnotation ca) { ca.Text = tb.Text; ca.FitToText(); }
            InvalidateMeasure();
            InvalidateArrange();
            InvalidateVisual();
        };
        tb.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
            {
                e.Handled = true;
                CommitEdit();
            }
        };
        tb.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is DependencyObject d && IsInsideEditor(d)) return;
            Dispatcher.BeginInvoke(() => { if (_editor == tb && !tb.IsKeyboardFocusWithin) CommitEdit(); }, DispatcherPriority.Input);
        };
        AddVisualChild(tb);
        AddLogicalChild(tb);
        InvalidateMeasure();
        InvalidateVisual();
        Dispatcher.BeginInvoke(() => { tb.Focus(); Keyboard.Focus(tb); tb.CaretIndex = tb.Text.Length; }, DispatcherPriority.Input);
    }

    bool IsInsideEditor(DependencyObject d)
    {
        for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur))
            if (cur == _editor) return true;
        return false;
    }

    void UpdateEditorLook()
    {
        if (_editor == null || _editing == null) return;
        var st = _editing.Style;
        double k = K;
        _editor.FontSize = Math.Max(4, st.FontSize) * k;
        _editor.FontWeight = st.Bold ? FontWeights.Bold : FontWeights.Normal;
        _editor.Foreground = new SolidColorBrush(Draw.ParseColor(st.Color, Colors.Red));
        _editor.CaretBrush = _editor.Foreground;
        if (_editing is CalloutAnnotation c)
        {
            _editor.Width = Math.Max(10, c.R.Width - 2 * c.Pad) * k + 6;
            _editor.TextWrapping = TextWrapping.Wrap;
            _editor.TextAlignment = TextAlignment.Center;
            _editor.Background = Brushes.Transparent;
        }
        else
        {
            _editor.TextWrapping = TextWrapping.NoWrap;
            _editor.TextAlignment = TextAlignment.Left;
            _editor.Background = string.IsNullOrWhiteSpace(st.Fill) ? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) : Brushes.Transparent;
        }
        InvalidateMeasure();
        InvalidateArrange();
    }

    public void CommitEdit()
    {
        if (_editor == null || _editing == null) return;
        var tb = _editor;
        var a = _editing;
        _editor = null;
        _editing = null;
        RemoveLogicalChild(tb);
        RemoveVisualChild(tb);

        string text = tb.Text;
        if (a is TextAnnotation t) t.Text = text;
        else if (a is CalloutAnnotation c) { c.Text = text; c.FitToText(); }

        bool untouched = _undo.Count == _editUndoDepth;
        if (string.IsNullOrWhiteSpace(text))
        {
            Items.Remove(a);
            if (Selected == a) Select(null);
            if (_editingNew && untouched) _undo.Pop();
            else ContentChanged?.Invoke();
        }
        else if (_editingNew || text != _editStartText) ContentChanged?.Invoke();
        else if (untouched) _undo.Pop();

        InvalidateMeasure();
        InvalidateVisual();
        Focus();
    }
}
