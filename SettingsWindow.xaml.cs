using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SnapTool.Editor;

namespace SnapTool;

/// <summary>GUI for the everyday settings. Everything else stays editable in settings.json.</summary>
public partial class SettingsWindow : Window
{
    static SettingsWindow? _open;

    static readonly (string Tool, string Label)[] Tools =
    {
        ("Select", "Select"), ("Arrow", "Arrow"), ("Line", "Line"), ("Rectangle", "Shape"), ("Highlight", "Highlight"),
        ("Text", "Text"), ("Callout", "Callout"), ("Blur", "Blur"), ("Crop", "Crop"),
    };
    static readonly int[] RecentCounts = { 10, 20, 40, 80, 150, 300 };
    static readonly int[] MaxHeights = { 5000, 10000, 20000, 40000, 60000 };

    static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

    readonly Dictionary<HotkeyBox, bool> _valid = new();
    string _accent = "#FF1E88E5";
    bool _loading;

    /// <summary>Opens the settings window, or brings the open one to the front.</summary>
    public static void ShowSingle(Window? owner = null)
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        if (owner != null) { _open.Owner = owner; _open.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        _open.Show();
        _open.Activate();
    }

    public SettingsWindow()
    {
        InitializeComponent();
        Icon = AppIcon.WindowIcon;

        foreach (var (tool, label) in Tools) DefaultToolBox.Items.Add(new ComboBoxItem { Content = label, Tag = tool });
        foreach (var n in RecentCounts) RecentCountBox.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        foreach (var h in MaxHeights) MaxHeightBox.Items.Add(new ComboBoxItem { Content = $"{h:N0} px", Tag = h });

        CaptureKey.GestureChanged += ValidateHotkeys;
        ScrollKey.GestureChanged += ValidateHotkeys;
        CaptureKey.LostKeyboardFocus += (_, _) => ValidateHotkeys();
        ScrollKey.LostKeyboardFocus += (_, _) => ValidateHotkeys();
        CaptureClear.Click += (_, _) => { CaptureKey.Gesture = ""; ValidateHotkeys(); };
        ScrollClear.Click += (_, _) => { ScrollKey.Gesture = ""; ValidateHotkeys(); };
        KeyboardSettingsLink.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-keyboard") { UseShellExecute = true });

        BrowseButton.Click += (_, _) => BrowseFolder();
        FolderBox.TextChanged += (_, _) => UpdatePreview();
        TemplateBox.TextChanged += (_, _) => UpdatePreview();
        FormatBox.SelectionChanged += (_, _) => { UpdatePreview(); QualitySlider.IsEnabled = SelectedTag(FormatBox) as string == "jpg"; };
        QualitySlider.ValueChanged += (_, _) => QualityValue.Text = $"{(int)QualitySlider.Value}";
        DimSlider.ValueChanged += (_, _) => DimValue.Text = $"{(int)DimSlider.Value} %";
        SettleSlider.ValueChanged += (_, _) => SettleValue.Text = $"{(int)SettleSlider.Value} ms";
        AccentButton.Click += (_, _) => PickAccent();
        RecentBox.Click += (_, _) => RecentCountBox.IsEnabled = RecentBox.IsChecked == true;

        SaveButton.Click += (_, _) => SaveAndClose();
        ResetButton.Click += (_, _) => Fill(new AppSettings(), AutoStart.Enabled);
        AdvancedButton.Click += (_, _) => TrayService.OpenSettingsFile();

        Fill(App.Settings, AutoStart.Enabled);
        SettingsPath.Text = $"Settings are stored in {App.Store.FilePath}";

        // Pause our own hotkeys so pressing the current one records it instead of starting a capture.
        App.Instance.SuspendHotkeys();
        Closed += (_, _) =>
        {
            _open = null;
            MemoryTrim.Soon();
            var problems = App.Instance.ResumeHotkeys();
            if (problems.Count > 0)
                MessageBox.Show(string.Join("\n", problems), "SnapTool hotkeys", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
    }

    // ---------- load / save ----------

    void Fill(AppSettings s, bool startup)
    {
        _loading = true;
        CaptureKey.Gesture = s.CaptureHotkey;
        ScrollKey.Gesture = s.ScrollCaptureHotkey;

        CopyBox.IsChecked = s.CopyToClipboard;
        AutoSaveBox.IsChecked = s.AutoSave;
        OpenEditorBox.IsChecked = s.OpenEditorAfterCapture;
        NotifyBox.IsChecked = s.ShowNotification;

        FolderBox.Text = s.SaveFolder;
        TemplateBox.Text = s.FileNameTemplate;
        SelectTag(FormatBox, s.ImageFormat.Trim().ToLowerInvariant() is "jpg" or "jpeg" ? "jpg" : "png");
        QualitySlider.Value = Math.Clamp(s.JpegQuality, 40, 100);
        QualitySlider.IsEnabled = SelectedTag(FormatBox) as string == "jpg";

        SelectTag(DefaultToolBox, Tools.Any(t => t.Tool == s.DefaultTool) ? s.DefaultTool : "Arrow");
        OverwriteBox.IsChecked = s.EditorSaveOverwrites;
        ConfirmBox.IsChecked = s.ConfirmCloseUnsaved;
        RememberBox.IsChecked = s.RememberLastToolStyle;
        RecentBox.IsChecked = s.ShowRecentStrip;
        SelectNearest(RecentCountBox, RecentCounts, s.RecentStripCount);
        RecentCountBox.IsEnabled = s.ShowRecentStrip;

        MagnifierBox.IsChecked = s.ShowMagnifier;
        DimSlider.Value = Math.Clamp(s.OverlayDimPercent, 0, 80);
        SetAccent(s.OverlayAccentColor);

        SelectTag(ScrollModeBox, string.Equals(s.ScrollMode, "Auto", StringComparison.OrdinalIgnoreCase) ? "Auto" : "Manual");
        ScrollTopBox.IsChecked = s.ScrollToTopFirst;
        SelectNearest(MaxHeightBox, MaxHeights, s.ScrollMaxHeight);
        SettleSlider.Value = Math.Clamp(s.ScrollSettleMs, 60, 1000);

        StartupBox.IsChecked = startup;
        _loading = false;
        ValidateHotkeys();
        UpdatePreview();
    }

    void SaveAndClose()
    {
        ValidateHotkeys();
        if (_valid.ContainsValue(false))
        {
            MessageBox.Show(this, "One of the hotkeys can't be used (see the red message). Pick another combination or clear it.",
                "SnapTool Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string folder = FolderBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(Environment.ExpandEnvironmentVariables(folder));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Can't use that save folder:\n{ex.Message}", "SnapTool Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var s = App.Settings;
        s.CaptureHotkey = CaptureKey.Gesture;
        s.ScrollCaptureHotkey = ScrollKey.Gesture;
        s.CopyToClipboard = CopyBox.IsChecked == true;
        s.AutoSave = AutoSaveBox.IsChecked == true;
        s.OpenEditorAfterCapture = OpenEditorBox.IsChecked == true;
        s.ShowNotification = NotifyBox.IsChecked == true;
        s.SaveFolder = folder;
        s.FileNameTemplate = string.IsNullOrWhiteSpace(TemplateBox.Text) ? new AppSettings().FileNameTemplate : TemplateBox.Text.Trim();
        s.ImageFormat = SelectedTag(FormatBox) as string ?? "png";
        s.JpegQuality = (int)QualitySlider.Value;
        s.DefaultTool = SelectedTag(DefaultToolBox) as string ?? "Arrow";
        s.EditorSaveOverwrites = OverwriteBox.IsChecked == true;
        s.ConfirmCloseUnsaved = ConfirmBox.IsChecked == true;
        s.RememberLastToolStyle = RememberBox.IsChecked == true;
        s.ShowRecentStrip = RecentBox.IsChecked == true;
        s.RecentStripCount = SelectedTag(RecentCountBox) is int n ? n : 40;
        s.ShowMagnifier = MagnifierBox.IsChecked == true;
        s.OverlayDimPercent = (int)DimSlider.Value;
        s.OverlayAccentColor = _accent;
        s.ScrollMode = SelectedTag(ScrollModeBox) as string ?? "Manual";
        s.ScrollToTopFirst = ScrollTopBox.IsChecked == true;
        s.ScrollMaxHeight = SelectedTag(MaxHeightBox) is int h ? h : 20000;
        s.ScrollSettleMs = (int)SettleSlider.Value;
        s.Normalize();

        try
        {
            App.Store.Save();
            if (AutoStart.Enabled != (StartupBox.IsChecked == true)) AutoStart.Enabled = StartupBox.IsChecked == true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the settings:\n{ex.Message}", "SnapTool Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Close(); // Closed re-registers the hotkeys from the new settings
    }

    // ---------- hotkeys ----------

    void ValidateHotkeys()
    {
        if (_loading) return;
        Validate(CaptureKey, CaptureStatus, ScrollKey, App.Settings.CaptureHotkey);
        Validate(ScrollKey, ScrollStatus, CaptureKey, App.Settings.ScrollCaptureHotkey);
        bool printScreen = CaptureKey.Gesture.Contains("PrintScreen", StringComparison.OrdinalIgnoreCase) ||
                           ScrollKey.Gesture.Contains("PrintScreen", StringComparison.OrdinalIgnoreCase);
        PrintScreenHint.Visibility = printScreen ? Visibility.Visible : Visibility.Collapsed;
    }

    void Validate(HotkeyBox box, TextBlock status, HotkeyBox other, string saved)
    {
        string g = box.Gesture;
        void Set(string text, Brush brush, bool valid) { status.Text = text; status.Foreground = brush; _valid[box] = valid; }

        if (g.Length == 0) { Set("Off", Neutral, true); return; }
        if (!HotkeyService.TryParse(g, out uint mods, out _)) { Set("That key can't be used as a hotkey.", Bad, false); return; }
        if (mods == 0 && !HotkeyBox.WorksAlone(g))
        {
            Set("Add Ctrl, Alt, Shift or Win – a plain key would stop working while you type.", Bad, false);
            return;
        }
        if (string.Equals(g, other.Gesture, StringComparison.OrdinalIgnoreCase)) { Set("Same as the other hotkey.", Bad, false); return; }

        bool ours = !App.Instance.OwnsHotkeys && string.Equals(g, saved, StringComparison.OrdinalIgnoreCase);
        if (ours) Set("✓ Current SnapTool hotkey", Ok, true);
        else if (HotkeyService.IsAvailable(g)) Set("✓ Available", Ok, true);
        else Set("Already used by another app – pick a different combination.", Bad, false);
    }

    // ---------- saving ----------

    void BrowseFolder()
    {
        var current = Environment.ExpandEnvironmentVariables(FolderBox.Text.Trim());
        var dlg = new OpenFolderDialog { Title = "Choose where captures are saved", InitialDirectory = Directory.Exists(current) ? current : "" };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    void UpdatePreview()
    {
        if (_loading) return;
        var folder = Environment.ExpandEnvironmentVariables(FolderBox.Text.Trim());
        var name = OutputService.PreviewFileName(TemplateBox.Text, SelectedTag(FormatBox) as string ?? "png", folder);
        TemplatePreview.Text = $"Next file: {name}";
    }

    // ---------- overlay color ----------

    void SetAccent(string hex)
    {
        var c = Draw.ParseColor(hex, Color.FromRgb(0x1E, 0x88, 0xE5));
        _accent = Draw.ToHex(c);
        AccentSwatch.Background = new SolidColorBrush(c);
    }

    void PickAccent()
    {
        var cur = Draw.ParseColor(_accent, Colors.DodgerBlue);
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(cur.R, cur.G, cur.B) };
        if (dlg.ShowDialog(new Win32Owner(new WindowInteropHelper(this).Handle)) == System.Windows.Forms.DialogResult.OK)
            SetAccent(Draw.ToHex(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B)));
    }

    sealed class Win32Owner : System.Windows.Forms.IWin32Window
    {
        public Win32Owner(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    // ---------- combo helpers ----------

    static object? SelectedTag(ComboBox cb) => (cb.SelectedItem as ComboBoxItem)?.Tag;

    static void SelectTag(ComboBox cb, object tag)
    {
        foreach (ComboBoxItem item in cb.Items)
            if (Equals(item.Tag, tag)) { cb.SelectedItem = item; return; }
    }

    static void SelectNearest(ComboBox cb, int[] values, int value) =>
        SelectTag(cb, values.OrderBy(v => Math.Abs(v - value)).First());
}
