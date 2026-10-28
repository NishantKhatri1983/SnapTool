using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace SnapTool.Editor;

/// <summary>The markup editor: toolbar, style bar, canvas, save/copy/print.</summary>
public partial class EditorWindow : Window
{
    static readonly (string Tool, string Label, string Icon, string Hint)[] ToolDefs =
    {
        ("Select", "Select", Icons.Select, "Click an object to select it. Drag to move, drag handles to resize, Delete removes it, arrows nudge."),
        ("Arrow", "Arrow", Icons.Arrow, "Drag to draw an arrow. Shift snaps to 45°. Pick the line ends, width and color below."),
        ("Line", "Line", Icons.Line, "Drag to draw a line. Shift snaps to 45°. Ends can be none, arrow, dot or square."),
        ("Rectangle", "Shape", Icons.Rectangle, "Drag to draw a rectangle, rounded box or ellipse. Shift makes it square. Fill is optional."),
        ("Highlight", "Highlight", Icons.Highlight, "Drag over an area to highlight it. Shape can be rectangle, rounded or ellipse (round highlight)."),
        ("Text", "Text", Icons.Text, "Click to type. Ctrl+Enter or Esc finishes. Double-click text later to edit it."),
        ("Callout", "Callout", Icons.Callout, "Click where the tail should point (or drag the bubble), then type. Drag the round handle to move the tail."),
        ("Blur", "Blur", Icons.Blur, "Drag over sensitive information to pixelate it."),
        ("Crop", "Crop", Icons.Crop, "Drag the area to keep, adjust the handles or move it (arrows nudge), then press Enter or double-click."),
    };

    static class Icons
    {
        public const string Open = "M3,7 V19 H21 V9 H11 L9,7 Z";
        public const string Save = "M5,4 H16 L20,8 V20 H4 V4 Z M8,4 V9 H15 V4 M7,14 H17 V20 H7 Z";
        public const string SaveAs = "M12,20 H4 V4 H14 L18,8 V11 M8,4 V9 H13 V4 M14,21 L15,17 L21,11 L23,13 L17,19 Z";
        public const string Copy = "M9,9 H20 V20 H9 Z M5,15 H4 V4 H15 V5";
        public const string Print = "M7,9 V3 H17 V9 M7,17 H3 V9 H21 V17 H17 M7,13 H17 V21 H7 Z";
        public const string Undo = "M9,5 L4,10 L9,15 M4,10 H14 A5,5 0 0 1 14,20 H9";
        public const string Redo = "M15,5 L20,10 L15,15 M20,10 H10 A5,5 0 0 0 10,20 H15";
        public const string Select = "M6,3 L6,19 L10,15 L13,21 L15.5,20 L12.5,14 L18,14 Z";
        public const string Arrow = "M5,19 L19,5 M10,5 H19 V14";
        public const string Line = "M4,20 L20,4";
        public const string Rectangle = "M4,6 H20 V18 H4 Z";
        public const string Highlight = "M4,21 H20 M7,17 L9,12 L16,4 L20,8 L12,15 Z M9,12 L12,15";
        public const string Text = "M5,8 V5 H19 V8 M12,5 V20 M9,20 H15";
        public const string Callout = "M4,4 H20 V15 H11 L6,20 V15 H4 Z";
        public const string Blur = "M4,4 H8 V8 H4 Z M10,4 H14 V8 H10 Z M16,4 H20 V8 H16 Z M4,10 H8 V14 H4 Z M10,10 H14 V14 H10 Z M16,10 H20 V14 H16 Z M4,16 H8 V20 H4 Z M10,16 H14 V20 H10 Z M16,16 H20 V20 H16 Z";
        public const string Crop = "M7,2 V17 H22 M2,7 H17 V22";
        public const string Resize = "M14,4 H20 V10 M20,4 L13,11 M10,20 H4 V14 M4,20 L11,13";
        public const string ZoomIn = "M4,10 A6,6 0 1 1 16,10 A6,6 0 1 1 4,10 Z M14.5,14.5 L20,20 M7,10 H13 M10,7 V13";
        public const string ZoomOut = "M4,10 A6,6 0 1 1 16,10 A6,6 0 1 1 4,10 Z M14.5,14.5 L20,20 M7,10 H13";
        public const string Actual = "M3,4 H21 V20 H3 Z M7,9 L9,8 V16 M15,9 L17,8 V16 M12,10 V10.5 M12,14 V14.5";
        public const string Fit = "M4,9 V4 H9 M15,4 H20 V9 M20,15 V20 H15 M9,20 H4 V15";
        public const string Settings = "M8.5,12 A3.5,3.5 0 1 1 15.5,12 A3.5,3.5 0 1 1 8.5,12 Z M12,2.5 V5.5 M12,18.5 V21.5 M2.5,12 H5.5 M18.5,12 H21.5 M5.3,5.3 L7.4,7.4 M16.6,16.6 L18.7,18.7 M5.3,18.7 L7.4,16.6 M16.6,7.4 L18.7,5.3";
    }

    static readonly Color AccentColor = Color.FromRgb(0x1E, 0x88, 0xE5);

    string? _path;
    bool _dirty, _ui, _stylesChanged, _fillTarget;
    Point _pointer;
    readonly Dictionary<string, ToolStyle> _styles = new();
    readonly Dictionary<string, RadioButton> _toolButtons = new();
    readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly RecentStrip _recent = new();

    Button _undoButton = null!, _redoButton = null!;
    FrameworkElement _gColor = null!, _gFillWell = null!, _gWidth = null!, _gCaps = null!, _gShape = null!, _gFont = null!, _gBlur = null!, _gCrop = null!, _gSelect = null!;
    ColorWell _wellColor = null!, _wellFill = null!;
    ComboBox _width = null!, _startCap = null!, _endCap = null!, _shape = null!, _fontSize = null!;
    CheckBox _dashed = null!;
    ToggleButton _bold = null!;
    Slider _blur = null!;
    TextBlock _blurValue = null!, _cropInfo = null!;

    public EditorWindow(BitmapSource image, string? path)
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;
        _path = path;
        foreach (var def in ToolDefs)
            if (def.Tool is not ("Select" or "Crop")) _styles[def.Tool] = App.Settings.GetToolStyle(def.Tool);

        Board.StyleProvider = tool => _styles.TryGetValue(tool, out var s) ? s : new ToolStyle();
        Board.Load(image);
        BuildMainBar();
        BuildPropBar();

        Board.ToolChanged += OnToolChanged;
        Board.SelectionChanged += UpdatePropBar;
        Board.ContentChanged += () => { _dirty = true; UpdateTitle(); UpdateUndoButtons(); };
        Board.ViewChanged += UpdateStatus;
        Board.PointerMoved += p => { _pointer = p; UpdateStatus(); };
        PreviewKeyDown += OnKey;
        Scroller.PreviewMouseWheel += OnWheel;
        Closing += OnClosing;
        Closed += (_, _) => MemoryTrim.Soon();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); ShowToolHint(); };

        RecentHost.Content = _recent;
        _recent.SetCurrent(_path);
        _recent.OpenRequested += (file, newWindow) => { if (newWindow) OpenFile(file)?.Show(); else LoadDocument(file); };
        _recent.Message += ShowMessage;
        RecentToggle.IsChecked = App.Settings.ShowRecentStrip;
        RecentHost.Visibility = App.Settings.ShowRecentStrip ? Visibility.Visible : Visibility.Collapsed;
        RecentToggle.Click += (_, _) =>
        {
            bool show = RecentToggle.IsChecked == true;
            RecentHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            App.Settings.ShowRecentStrip = show;
            try { App.Store.Save(); } catch (Exception) { /* remembered for this session only */ }
        };

        var wa = SystemParameters.WorkArea;
        Width = Math.Clamp(Board.Image.PixelWidth + 120, Math.Min(1180, wa.Width * 0.95), wa.Width * 0.95);
        Height = Math.Clamp(Board.Image.PixelHeight + 330, 700, wa.Height * 0.92);

        Loaded += (_, _) => Dispatcher.BeginInvoke(() => { Fit(); Board.Focus(); MemoryTrim.Soon(); }, DispatcherPriority.Loaded);

        string startTool = _toolButtons.ContainsKey(App.Settings.DefaultTool) ? App.Settings.DefaultTool : "Arrow";
        if (Board.Tool == startTool) OnToolChanged(); else Board.Tool = startTool;
        UpdateTitle();
        UpdateUndoButtons();
    }

    public static EditorWindow? OpenFile(string path)
    {
        try
        {
            return new EditorWindow(Imaging.LoadFile(path), path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open {path}:\n{ex.Message}", "SnapTool", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    /// <summary>Forget unsaved changes (used by the self-test).</summary>
    internal void MarkClean() => _dirty = false;

    internal int RecentCount => _recent.ItemCount;

    // ---------- toolbar ----------

    static UIElement MakeIcon(string data)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = 20, Height = 20, Child = canvas, HorizontalAlignment = HorizontalAlignment.Center };
    }

    static StackPanel IconLabel(string icon, string label)
    {
        var sp = new StackPanel();
        sp.Children.Add(MakeIcon(icon));
        sp.Children.Add(new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    Button AddButton(string? label, string icon, string tip, Action click)
    {
        var b = new Button { Style = (Style)FindResource("BarButton"), Content = label == null ? MakeIcon(icon) : IconLabel(icon, label), ToolTip = tip };
        if (label == null) { b.MinWidth = 34; b.Padding = new Thickness(5); }
        b.Click += (_, _) => click();
        MainBar.Children.Add(b);
        return b;
    }

    void AddSeparator() => MainBar.Children.Add(new Border
    {
        Width = 1, Margin = new Thickness(4, 4, 4, 4), Background = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
    });

    void BuildMainBar()
    {
        AddButton("Open", Icons.Open, "Open an image (Ctrl+O)", OpenImage);
        AddButton("Save", Icons.Save, "Save (Ctrl+S)", () => Save());
        AddButton("Save as", Icons.SaveAs, "Save as… (F12)", () => SaveAs());
        AddButton("Copy", Icons.Copy, "Copy the image with markup to the clipboard (Ctrl+C)", CopyImage);
        AddButton("Print", Icons.Print, "Print (Ctrl+P)", PrintImage);
        AddSeparator();
        _undoButton = AddButton("Undo", Icons.Undo, "Undo (Ctrl+Z)", () => { Board.Undo(); UpdateUndoButtons(); });
        _redoButton = AddButton("Redo", Icons.Redo, "Redo (Ctrl+Y)", () => { Board.Redo(); UpdateUndoButtons(); });
        AddSeparator();
        foreach (var def in ToolDefs)
        {
            App.Settings.ToolShortcuts.TryGetValue(def.Tool, out var key);
            var rb = new RadioButton
            {
                Style = (Style)FindResource("ToolButton"),
                Content = IconLabel(def.Icon, def.Label),
                ToolTip = string.IsNullOrEmpty(key) ? def.Label : $"{def.Label} ({key})",
            };
            string tool = def.Tool;
            rb.Checked += (_, _) => Board.Tool = tool;
            _toolButtons[tool] = rb;
            MainBar.Children.Add(rb);
        }
        AddSeparator();
        AddButton("Resize", Icons.Resize, "Resize the image (Ctrl+R)", ResizeImage);
        AddSeparator();
        AddButton(null, Icons.ZoomOut, "Zoom out (Ctrl+-  or Ctrl+wheel)", () => ZoomBy(1 / 1.25));
        AddButton(null, Icons.ZoomIn, "Zoom in (Ctrl+=  or Ctrl+wheel)", () => ZoomBy(1.25));
        AddButton(null, Icons.Actual, "Actual pixels (Ctrl+1)", () => Board.SetZoom(1));
        AddButton(null, Icons.Fit, "Fit in window (Ctrl+0)", Fit);
        AddSeparator();
        AddButton(null, Icons.Settings, "SnapTool settings: hotkeys, save folder, file names…", () => SettingsWindow.ShowSingle(this));
    }

    // ---------- style bar ----------

    static TextBlock Label(string text, double left = 0) =>
        new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)), Margin = new Thickness(left, 0, 4, 0) };

    static StackPanel Group(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    ComboBox Combo(IEnumerable<(object Value, string Text)> items, Action<object> changed, double width = 72)
    {
        var cb = new ComboBox { Width = width };
        foreach (var (value, text) in items) cb.Items.Add(new ComboBoxItem { Content = text, Tag = value });
        cb.SelectionChanged += (_, _) =>
        {
            if (!_ui && cb.SelectedItem is ComboBoxItem { Tag: { } v }) changed(v);
        };
        return cb;
    }

    static void SelectTag(ComboBox cb, object value)
    {
        foreach (ComboBoxItem item in cb.Items)
            if (Equals(item.Tag, value)) { cb.SelectedItem = item; return; }
        // A value from settings.json that isn't in the list: add it so it shows.
        var extra = new ComboBoxItem { Content = value.ToString(), Tag = value };
        cb.Items.Add(extra);
        cb.SelectedItem = extra;
    }

    Border Swatch(string? hex)
    {
        var b = new Border
        {
            Width = 18, Height = 18, Margin = new Thickness(2, 0, 2, 0), CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Cursor = Cursors.Hand, ToolTip = hex ?? "No fill",
            Background = hex == null ? Brushes.White : new SolidColorBrush(Draw.ParseColor(hex, Colors.Black)),
        };
        if (hex == null)
            b.Child = new System.Windows.Shapes.Line { X1 = 2, Y1 = 14, X2 = 14, Y2 = 2, Stroke = Brushes.Red, StrokeThickness = 1.6 };
        b.MouseLeftButtonUp += (_, _) => ApplyColor(hex);
        return b;
    }

    void BuildPropBar()
    {
        _wellColor = new ColorWell { ToolTip = "Line / text color – click, then pick a swatch" };
        _wellFill = new ColorWell { ToolTip = "Fill / background – click, then pick a swatch (the crossed one = none)" };
        _wellColor.MouseLeftButtonUp += (_, _) => { _fillTarget = false; UpdatePropBar(); };
        _wellFill.MouseLeftButtonUp += (_, _) => { _fillTarget = true; UpdatePropBar(); };
        _gFillWell = Group(Label("Fill", 6), _wellFill);
        ((StackPanel)_gFillWell).Margin = new Thickness(0);

        var swatches = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        swatches.Children.Add(Swatch(null));
        foreach (var hex in App.Settings.Palette) swatches.Children.Add(Swatch(hex));
        var custom = new Border
        {
            Width = 22, Height = 18, Margin = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Background = Brushes.White, Cursor = Cursors.Hand, ToolTip = "Custom color…",
            Child = new TextBlock { Text = "+", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(0, -2, 0, 0) },
        };
        custom.MouseLeftButtonUp += (_, _) => PickCustomColor();
        swatches.Children.Add(custom);
        _gColor = Group(Label("Color"), _wellColor, _gFillWell, swatches);

        _width = Combo(new[] { 0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24 }.Select(v => ((object)v, $"{v} px")),
            v => ChangeStyle(s => s.Width = (int)v), 70);
        _gWidth = Group(Label("Width"), _width);

        var caps = new (object, string)[] { ("None", "—  None"), ("Arrow", "➤  Arrow"), ("Dot", "●  Dot"), ("Square", "■  Square") };
        _startCap = Combo(caps, v => ChangeStyle(s => s.StartCap = (string)v), 96);
        _endCap = Combo(caps, v => ChangeStyle(s => s.EndCap = (string)v), 96);
        _dashed = new CheckBox { Content = "Dashed", Focusable = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
        _dashed.Click += (_, _) => ChangeStyle(s => s.Dashed = _dashed.IsChecked == true);
        _gCaps = Group(Label("Start"), _startCap, Label("End"), _endCap, _dashed);

        _shape = Combo(new (object, string)[] { ("Rectangle", "▭  Rectangle"), ("Rounded", "▢  Rounded"), ("Ellipse", "◯  Ellipse") },
            v => ChangeStyle(s => s.Shape = (string)v), 112);
        _gShape = Group(Label("Shape"), _shape);

        _fontSize = Combo(new[] { 10, 12, 14, 16, 18, 20, 22, 24, 28, 32, 36, 40, 48, 60, 72 }.Select(v => ((object)v, $"{v} pt")),
            v => ChangeStyle(s => s.FontSize = (int)v), 70);
        _bold = new ToggleButton { Content = "B", FontWeight = FontWeights.Bold, Width = 26, Height = 22, Focusable = false, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Bold" };
        _bold.Click += (_, _) => ChangeStyle(s => s.Bold = _bold.IsChecked == true);
        _gFont = Group(Label("Text size"), _fontSize, _bold);

        _blur = new Slider { Minimum = 4, Maximum = 40, Width = 150, VerticalAlignment = VerticalAlignment.Center, Focusable = false, IsSnapToTickEnabled = true, TickFrequency = 2 };
        _blurValue = Label("", 4);
        _blur.ValueChanged += (_, _) => { _blurValue.Text = $"{(int)_blur.Value} px blocks"; ChangeStyle(s => s.BlurBlock = (int)_blur.Value); };
        _gBlur = Group(Label("Strength"), _blur, _blurValue);

        _cropInfo = Label("Drag on the image to choose the area to keep.");
        _cropInfo.MinWidth = 260;
        _gCrop = Group(_cropInfo, TextButton("Apply crop (Enter)", () => Board.ApplyCrop()),
            TextButton("Whole image", () => Board.SelectWholeImageForCrop()), TextButton("Cancel (Esc)", () => Board.Escape()));

        _gSelect = Group(Label("Select an object to change its color, width or style. Delete removes it."));

        foreach (var g in new[] { _gColor, _gWidth, _gCaps, _gShape, _gFont, _gBlur, _gCrop, _gSelect }) PropBar.Children.Add(g);
    }

    Button TextButton(string text, Action click)
    {
        var b = new Button
        {
            Style = (Style)FindResource("BarButton"), Content = text, Padding = new Thickness(10, 3, 10, 3),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), Background = Brushes.White, Margin = new Thickness(4, 0, 0, 0),
        };
        b.Click += (_, _) => click();
        return b;
    }

    string StyleTool => Board.Selected?.Tool ?? Board.Tool;

    ToolStyle? CurrentStyle() =>
        Board.Selected?.Style ?? (_styles.TryGetValue(Board.Tool, out var s) ? s : null);

    void ChangeStyle(Action<ToolStyle> change)
    {
        if (_ui) return;
        var sel = Board.Selected;
        if (sel != null)
        {
            Board.PushUndo();
            change(sel.Style);
            if (_styles.TryGetValue(sel.Tool, out var remembered)) change(remembered);
            Board.Refresh();
            _dirty = true;
            UpdateTitle();
            UpdateUndoButtons();
        }
        else if (_styles.TryGetValue(Board.Tool, out var s))
        {
            change(s);
        }
        _stylesChanged = true;
        UpdatePropBar();
    }

    void ApplyColor(string? hex)
    {
        bool fillVisible = _gFillWell.Visibility == Visibility.Visible;
        if (_fillTarget && fillVisible) ChangeStyle(s => s.Fill = hex);
        else if (hex != null) ChangeStyle(s => s.Color = hex);
    }

    void PickCustomColor()
    {
        var st = CurrentStyle();
        var cur = Draw.ParseColor(_fillTarget ? st?.Fill : st?.Color, Colors.Red);
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B) };
        if (dlg.ShowDialog(new Win32Owner(new WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK)
            ApplyColor(Draw.ToHex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)));
    }

    sealed class Win32Owner : System.Windows.Forms.IWin32Window
    {
        public Win32Owner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    static void Show(FrameworkElement e, bool visible) => e.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    void UpdatePropBar()
    {
        _ui = true;
        try
        {
            string tool = StyleTool;
            var st = CurrentStyle();
            bool styled = st != null && tool is not ("Select" or "Crop");
            bool hasFill = tool is "Rectangle" or "Text" or "Callout";
            if (!hasFill) _fillTarget = false;

            Show(_gColor, styled && tool != "Blur");
            Show(_gFillWell, hasFill);
            Show(_gWidth, styled && tool is "Arrow" or "Line" or "Rectangle" or "Callout");
            Show(_gCaps, styled && tool is "Arrow" or "Line");
            Show(_gShape, styled && tool is "Rectangle" or "Highlight" or "Blur");
            Show(_gFont, styled && tool is "Text" or "Callout");
            Show(_gBlur, styled && tool == "Blur");
            Show(_gCrop, tool == "Crop");
            Show(_gSelect, tool == "Select");

            if (st != null)
            {
                _wellColor.SetColor(st.Color);
                _wellFill.SetColor(st.Fill);
                _wellColor.Active = !_fillTarget;
                _wellFill.Active = _fillTarget;
                SelectTag(_width, (int)Math.Round(st.Width));
                SelectTag(_startCap, st.StartCap);
                SelectTag(_endCap, st.EndCap);
                SelectTag(_shape, st.Shape);
                SelectTag(_fontSize, (int)Math.Round(st.FontSize));
                _dashed.IsChecked = st.Dashed;
                _bold.IsChecked = st.Bold;
                _blur.Value = st.BlurBlock;
                _blurValue.Text = $"{st.BlurBlock} px blocks";
            }
        }
        finally
        {
            _ui = false;
        }
        UpdateStatus();
    }

    void OnToolChanged()
    {
        if (_toolButtons.TryGetValue(Board.Tool, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
        ShowToolHint();
        UpdatePropBar();
    }

    void ShowToolHint()
    {
        foreach (var def in ToolDefs)
            if (def.Tool == Board.Tool) StatusLeft.Text = def.Hint;
    }

    void ShowMessage(string text)
    {
        StatusLeft.Text = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    void UpdateStatus()
    {
        var img = Board.Image;
        StatusRight.Text = $"{img.PixelWidth} × {img.PixelHeight} px     {Board.Zoom * 100:0}%     {(int)_pointer.X}, {(int)_pointer.Y}";
        if (_cropInfo != null && Board.Tool == "Crop")
            _cropInfo.Text = Board.CropPixels is { } c
                ? $"Keep {c.Width} × {c.Height} px at ({c.X}, {c.Y})"
                : "Drag on the image to choose the area to keep.";
    }

    void UpdateTitle() =>
        Title = $"{(_path != null ? IOPath.GetFileName(_path) : "Capture")}{(_dirty ? " •" : "")}  –  SnapTool";

    void UpdateUndoButtons()
    {
        _undoButton.IsEnabled = Board.CanUndo;
        _redoButton.IsEnabled = Board.CanRedo;
    }

    // ---------- keyboard & zoom ----------

    void OnKey(object sender, KeyEventArgs e)
    {
        if (Board.IsEditingText || Keyboard.FocusedElement is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        e.Handled = true;

        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when shift: Board.Redo(); UpdateUndoButtons(); return;
                case Key.Z: Board.Undo(); UpdateUndoButtons(); return;
                case Key.Y: Board.Redo(); UpdateUndoButtons(); return;
                case Key.C: CopyImage(); return;
                case Key.S when shift: SaveAs(); return;
                case Key.S: Save(); return;
                case Key.P: PrintImage(); return;
                case Key.O: OpenImage(); return;
                case Key.R: ResizeImage(); return;
                case Key.D0 or Key.NumPad0: Fit(); return;
                case Key.D1 or Key.NumPad1: Board.SetZoom(1); return;
                case Key.OemPlus or Key.Add: ZoomBy(1.25); return;
                case Key.OemMinus or Key.Subtract: ZoomBy(1 / 1.25); return;
            }
            e.Handled = false;
            return;
        }

        double step = shift ? 10 : 1;
        switch (key)
        {
            case Key.F12: SaveAs(); return;
            case Key.PageDown when _recent.Neighbor(+1) is { } older: LoadDocument(older); return;
            case Key.PageUp when _recent.Neighbor(-1) is { } newer: LoadDocument(newer); return;
            case Key.Delete or Key.Back: Board.DeleteSelected(); return;
            case Key.Escape: Board.Escape(); UpdatePropBar(); return;
            case Key.Enter when Board.Tool == "Crop": Board.ApplyCrop(); return;
            case Key.Left: Board.Nudge(-step, 0); return;
            case Key.Right: Board.Nudge(step, 0); return;
            case Key.Up: Board.Nudge(0, -step); return;
            case Key.Down: Board.Nudge(0, step); return;
        }
        if (mods == ModifierKeys.None)
        {
            foreach (var (tool, shortcut) in App.Settings.ToolShortcuts)
            {
                if (string.IsNullOrWhiteSpace(shortcut) || !_toolButtons.ContainsKey(tool)) continue;
                var name = shortcut.Length == 1 && char.IsDigit(shortcut[0]) ? "D" + shortcut : shortcut;
                if (Enum.TryParse<Key>(name, true, out var k) && k == key) { Board.Tool = tool; return; }
            }
        }
        e.Handled = false;
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        ZoomBy(e.Delta > 0 ? 1.25 : 1 / 1.25);
        e.Handled = true;
    }

    void ZoomBy(double factor) => Board.SetZoom(Board.Zoom * factor);

    void Fit()
    {
        var viewport = new Size(Math.Max(100, Scroller.ActualWidth - 70), Math.Max(100, Scroller.ActualHeight - 70));
        Board.SetZoom(Board.FitZoom(viewport));
    }

    // ---------- file, clipboard, print ----------

    void OpenImage()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            InitialDirectory = _path != null ? IOPath.GetDirectoryName(_path) : App.Settings.ResolvedSaveFolder,
        };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName)?.Show();
    }

    bool Save()
    {
        if (_path == null) return SaveAs();
        return SaveTo(App.Settings.EditorSaveOverwrites ? _path : EditedPath(_path));
    }

    static string EditedPath(string path)
    {
        string name = IOPath.GetFileNameWithoutExtension(path);
        if (name.EndsWith("_edited", StringComparison.OrdinalIgnoreCase)) return path;
        return IOPath.Combine(IOPath.GetDirectoryName(path)!, name + "_edited" + IOPath.GetExtension(path));
    }

    bool SaveAs()
    {
        string ext = _path != null ? IOPath.GetExtension(_path).ToLowerInvariant() : (App.Settings.ImageFormat == "jpg" ? ".jpg" : ".png");
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp",
            FilterIndex = ext is ".jpg" or ".jpeg" ? 2 : ext == ".bmp" ? 3 : 1,
            FileName = _path != null ? IOPath.GetFileNameWithoutExtension(_path) : IOPath.GetFileNameWithoutExtension(OutputService.NextCapturePath(App.Settings)),
            InitialDirectory = _path != null ? IOPath.GetDirectoryName(_path) : App.Settings.ResolvedSaveFolder,
            AddExtension = true,
        };
        return dlg.ShowDialog(this) == true && SaveTo(dlg.FileName);
    }

    /// <summary>Replaces the image in this window (used by the recent-captures timeline).</summary>
    void LoadDocument(string path)
    {
        Board.CommitEdit();
        if (string.Equals(path, _path, StringComparison.OrdinalIgnoreCase) && !_dirty) return;
        if (!ConfirmDiscard()) return;
        BitmapSource image;
        try { image = Imaging.LoadFile(path); }
        catch (Exception ex) { ShowMessage($"Couldn't open {IOPath.GetFileName(path)}: {ex.Message}"); return; }
        Board.Load(image);
        _path = path;
        _dirty = false;
        UpdateTitle();
        UpdateUndoButtons();
        UpdatePropBar();
        Fit();
        _recent.SetCurrent(path);
        ShowMessage($"Opened {IOPath.GetFileName(path)}  ({image.PixelWidth} × {image.PixelHeight})   PageUp / PageDown: newer / older capture");
    }

    /// <summary>Asks to save unsaved markup. False = the user cancelled.</summary>
    bool ConfirmDiscard()
    {
        if (!_dirty || !App.Settings.ConfirmCloseUnsaved) return true;
        var answer = MessageBox.Show(this, "Save your changes to this image?", "SnapTool",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && Save());
    }

    bool SaveTo(string path)
    {
        try
        {
            OutputService.SaveImage(Board.Flatten(), path, App.Settings.JpegQuality);
            _path = path;
            _dirty = false;
            UpdateTitle();
            _recent.SetCurrent(path);
            ShowMessage($"Saved to {path}");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    void CopyImage()
    {
        try
        {
            OutputService.CopyToClipboard(Board.Flatten());
            _dirty = false; // the result has been used; don't nag on close
            UpdateTitle();
            ShowMessage("Copied to the clipboard.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void PrintImage()
    {
        var img = Board.Flatten();
        var pd = new PrintDialog();
        try
        {
            if (img.PixelWidth > img.PixelHeight) pd.PrintTicket.PageOrientation = PageOrientation.Landscape;
        }
        catch (Exception) { /* no default printer; the dialog will say so */ }
        if (pd.ShowDialog() != true) return;

        double pw = pd.PrintableAreaWidth, ph = pd.PrintableAreaHeight, margin = 36;
        double scale = Math.Min(1, Math.Min((pw - 2 * margin) / img.PixelWidth, (ph - 2 * margin) / img.PixelHeight));
        double w = img.PixelWidth * scale, h = img.PixelHeight * scale;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(img, new Rect((pw - w) / 2, margin, w, h));
        pd.PrintVisual(dv, Title);
        ShowMessage("Sent to the printer.");
    }

    void ResizeImage()
    {
        Board.CommitEdit();
        var dlg = new ResizeDialog(Board.Image.PixelWidth, Board.Image.PixelHeight) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Board.ResizeImage(dlg.ResultWidth, dlg.ResultHeight);
            UpdateUndoButtons();
            ShowMessage($"Resized to {dlg.ResultWidth} × {dlg.ResultHeight} px.");
        }
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        Board.CommitEdit();
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }
        if (_stylesChanged && App.Settings.RememberLastToolStyle)
        {
            foreach (var (tool, style) in _styles) App.Settings.ToolDefaults[tool] = style.Clone();
            try { App.Store.Save(); } catch (Exception) { /* settings file busy; styles just aren't remembered */ }
        }
    }
}

/// <summary>A small color box in the style bar; the active one gets an accent border.</summary>
internal sealed class ColorWell : Border
{
    readonly Border _inner = new() { CornerRadius = new CornerRadius(3), Margin = new Thickness(2) };
    readonly System.Windows.Shapes.Line _none = new() { X1 = 4, Y1 = 16, X2 = 22, Y2 = 2, Stroke = Brushes.Red, StrokeThickness = 1.8 };

    public ColorWell()
    {
        Width = 30;
        Height = 24;
        CornerRadius = new CornerRadius(5);
        Background = Brushes.White;
        Cursor = Cursors.Hand;
        Margin = new Thickness(2, 0, 2, 0);
        var grid = new Grid();
        grid.Children.Add(_inner);
        grid.Children.Add(_none);
        Child = grid;
        Active = false;
    }

    public void SetColor(string? hex)
    {
        bool none = string.IsNullOrWhiteSpace(hex);
        _inner.Background = none ? Brushes.White : new SolidColorBrush(Draw.ParseColor(hex, Colors.Black));
        _none.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool Active
    {
        set
        {
            BorderBrush = new SolidColorBrush(value ? Color.FromRgb(0x1E, 0x88, 0xE5) : Color.FromRgb(0x99, 0x99, 0x99));
            BorderThickness = new Thickness(value ? 2 : 1);
        }
    }
}
