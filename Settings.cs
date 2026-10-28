using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace SnapTool;

/// <summary>Style of one markup tool. Colors are #AARRGGBB (or #RRGGBB) strings.</summary>
public sealed class ToolStyle
{
    public string Color { get; set; } = "#FFE53935";
    /// <summary>Fill / background color, or null for none.</summary>
    public string? Fill { get; set; }
    public double Width { get; set; } = 4;
    public double FontSize { get; set; } = 22;
    public bool Bold { get; set; } = true;
    /// <summary>None | Arrow | Dot | Square</summary>
    public string StartCap { get; set; } = "None";
    /// <summary>None | Arrow | Dot | Square</summary>
    public string EndCap { get; set; } = "None";
    /// <summary>Rectangle | Rounded | Ellipse</summary>
    public string Shape { get; set; } = "Rectangle";
    public bool Dashed { get; set; }
    /// <summary>Pixel block size for the Blur tool.</summary>
    public int BlurBlock { get; set; } = 12;

    public ToolStyle Clone() => (ToolStyle)MemberwiseClone();
}

/// <summary>Everything the user can customize. Stored as %APPDATA%\SnapTool\settings.json.</summary>
public sealed class AppSettings
{
    // Hotkeys ("Ctrl+Shift+S", "PrintScreen", "Win+Shift+Q", ... ; empty = disabled)
    public string CaptureHotkey { get; set; } = "Ctrl+Shift+S";
    public string ScrollCaptureHotkey { get; set; } = "Ctrl+Shift+Alt+S";

    // What happens after a capture
    public bool CopyToClipboard { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public bool OpenEditorAfterCapture { get; set; } = false;
    public bool ShowNotification { get; set; } = true;

    // Saving
    public string SaveFolder { get; set; } = "%USERPROFILE%\\Pictures\\SnapTool";
    /// <summary>{...} is a .NET date format, {n} / {n:000} is a running number.</summary>
    public string FileNameTemplate { get; set; } = "Snap_{yyyy-MM-dd_HH-mm-ss}";
    /// <summary>png | jpg</summary>
    public string ImageFormat { get; set; } = "png";
    public int JpegQuality { get; set; } = 92;
    /// <summary>Editor Ctrl+S overwrites the opened file; false saves "name_edited.png" next to it.</summary>
    public bool EditorSaveOverwrites { get; set; } = true;
    public bool ConfirmCloseUnsaved { get; set; } = true;
    public bool RememberLastToolStyle { get; set; } = true;
    public string DefaultTool { get; set; } = "Arrow";
    /// <summary>Thumbnail timeline of recent captures at the bottom of the editor.</summary>
    public bool ShowRecentStrip { get; set; } = true;
    public int RecentStripCount { get; set; } = 40;

    // Capture overlay
    public bool ShowMagnifier { get; set; } = true;
    public string OverlayAccentColor { get; set; } = "#FF1E88E5";
    public int OverlayDimPercent { get; set; } = 45;

    // Scrolling capture
    /// <summary>Manual = you scroll and press Enter/End to finish; Auto = SnapTool scrolls with the mouse wheel.</summary>
    public string ScrollMode { get; set; } = "Manual";
    public bool ScrollToTopFirst { get; set; } = true;
    public int ScrollMaxFrames { get; set; } = 80;
    public int ScrollMaxHeight { get; set; } = 20000;
    public int ScrollSettleMs { get; set; } = 180;

    public List<string> Palette { get; set; } = new()
    {
        "#FFE53935", "#FFFB8C00", "#FFFDD835", "#FF43A047", "#FF00ACC1", "#FF1E88E5",
        "#FF8E24AA", "#FFD81B60", "#FFFFFFFF", "#FF9E9E9E", "#FF424242", "#FF000000",
    };

    public Dictionary<string, string> ToolShortcuts { get; set; } = new()
    {
        ["Select"] = "V", ["Arrow"] = "A", ["Line"] = "L", ["Rectangle"] = "R", ["Highlight"] = "H",
        ["Text"] = "T", ["Callout"] = "C", ["Blur"] = "B", ["Crop"] = "K",
    };

    public Dictionary<string, ToolStyle> ToolDefaults { get; set; } = BuiltInStyles();

    public static Dictionary<string, ToolStyle> BuiltInStyles() => new()
    {
        ["Arrow"] = new ToolStyle { Color = "#FFE53935", Width = 5, EndCap = "Arrow" },
        ["Line"] = new ToolStyle { Color = "#FFE53935", Width = 4 },
        ["Rectangle"] = new ToolStyle { Color = "#FFE53935", Width = 4, Shape = "Rectangle" },
        ["Highlight"] = new ToolStyle { Color = "#FFFFEB3B", Shape = "Rectangle" },
        ["Text"] = new ToolStyle { Color = "#FFE53935", FontSize = 22, Bold = true },
        ["Callout"] = new ToolStyle { Color = "#FFFFFFFF", Fill = "#FFE53935", Width = 0, FontSize = 18, Bold = true },
        ["Blur"] = new ToolStyle { BlurBlock = 12 },
    };

    public ToolStyle GetToolStyle(string tool)
    {
        if (ToolDefaults.TryGetValue(tool, out var s) && s != null) return s.Clone();
        return BuiltInStyles().TryGetValue(tool, out var b) ? b : new ToolStyle();
    }

    public string ResolvedSaveFolder =>
        Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(SaveFolder)
            ? "%USERPROFILE%\\Pictures\\SnapTool" : SaveFolder);

    public void Normalize()
    {
        Palette ??= new();
        if (Palette.Count == 0) Palette = new AppSettings().Palette;
        ToolShortcuts ??= new AppSettings().ToolShortcuts;
        ToolDefaults ??= BuiltInStyles();
        JpegQuality = Math.Clamp(JpegQuality, 10, 100);
        OverlayDimPercent = Math.Clamp(OverlayDimPercent, 0, 90);
        ScrollMaxFrames = Math.Clamp(ScrollMaxFrames, 2, 500);
        ScrollMaxHeight = Math.Clamp(ScrollMaxHeight, 1000, 60000);
        ScrollSettleMs = Math.Clamp(ScrollSettleMs, 30, 3000);
        RecentStripCount = Math.Clamp(RecentStripCount, 1, 300);
    }
}

/// <summary>Loads/saves settings.json and reloads it when it is edited by hand.</summary>
public sealed class SettingsStore
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapTool");

    public string FilePath => Path.Combine(Folder, "settings.json");
    public AppSettings Current { get; private set; } = new();
    public string? LastError { get; private set; }
    public event Action? Changed;

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "Ctrl+Shift+S" readable
    };

    FileSystemWatcher? _watcher;
    Timer? _debounce;
    DateTime _lastSelfWrite;

    public void Load()
    {
        Directory.CreateDirectory(Folder);
        if (!File.Exists(FilePath))
        {
            Current = new AppSettings();
            Save();
            return;
        }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
                loaded.Normalize();
                Current = loaded;
                LastError = null;
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100); // file is still being written by an editor
            }
            catch (Exception ex)
            {
                LastError = ex.Message; // keep the previous settings
                return;
            }
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        _lastSelfWrite = DateTime.UtcNow;
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
    }

    public void Watch()
    {
        _watcher = new FileSystemWatcher(Folder, "settings.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if ((DateTime.UtcNow - _lastSelfWrite).TotalMilliseconds < 1500) return;
        _debounce?.Dispose();
        _debounce = new Timer(_ => { Load(); Changed?.Invoke(); }, null, 400, Timeout.Infinite);
    }
}
