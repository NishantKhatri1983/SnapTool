using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnapTool.Editor;

/// <summary>
/// Timeline of recent captures (newest first) along the bottom of the editor. Watches the save folder, so new
/// captures appear immediately. Click = open here, Ctrl+click = new window, drag = drop the file into another app,
/// right-click = more actions.
/// </summary>
internal sealed class RecentStrip : Border
{
    static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    static readonly SolidColorBrush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)));
    static readonly SolidColorBrush Idle = Freeze(new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0)));
    static readonly SolidColorBrush Hover = Freeze(new SolidColorBrush(Color.FromRgb(0x90, 0xB8, 0xE0)));
    const double ThumbHeight = 58;

    readonly StackPanel _items = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly ScrollViewer _scroll;
    readonly TextBlock _count = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)), Margin = new Thickness(0, 2, 0, 0) };
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Dictionary<string, (DateTime Stamp, BitmapSource Image)> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _paths = new();
    FileSystemWatcher? _watcher;
    string? _current;

    /// <summary>(path, openInNewWindow)</summary>
    public event Action<string, bool>? OpenRequested;
    public event Action<string>? Message;

    public int ItemCount => _paths.Count;

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public RecentStrip()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
        BorderThickness = new Thickness(0, 1, 0, 0);
        Height = 102;

        var folderLink = new TextBlock
        {
            Text = "Open folder", FontSize = 11, Foreground = Accent, Cursor = Cursors.Hand,
            TextDecorations = TextDecorations.Underline, Margin = new Thickness(0, 6, 0, 0),
        };
        folderLink.MouseLeftButtonUp += (_, _) => OpenFolder();
        var header = new StackPanel { Width = 84, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
        header.Children.Add(new TextBlock { Text = "Recent", FontWeight = FontWeights.SemiBold, FontSize = 13 });
        header.Children.Add(_count);
        header.Children.Add(folderLink);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _items,
        };
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
            e.Handled = true;
        };

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Left);
        dock.Children.Add(header);
        dock.Children.Add(_scroll);
        Child = dock;

        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        Loaded += (_, _) => { StartWatching(); Refresh(); };
        Unloaded += (_, _) => { _watcher?.Dispose(); _watcher = null; };
    }

    // ---------- data ----------

    void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        var folder = App.Settings.ResolvedSaveFolder;
        try
        {
            Directory.CreateDirectory(folder);
            _watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            _watcher.Created += changed;
            _watcher.Changed += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (s, e) => changed(s, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Folder unreachable (e.g. offline network drive): the strip just won't auto-refresh.
        }
    }

    public void Refresh()
    {
        var folder = App.Settings.ResolvedSaveFolder;
        List<FileInfo> files;
        try
        {
            files = Directory.Exists(folder)
                ? new DirectoryInfo(folder).EnumerateFiles()
                    .Where(f => Extensions.Contains(f.Extension.ToLowerInvariant()))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(Math.Max(1, App.Settings.RecentStripCount))
                    .ToList()
                : new List<FileInfo>();
        }
        catch (IOException) { files = new List<FileInfo>(); }

        _items.Children.Clear();
        _paths.Clear();
        foreach (var f in files)
        {
            _paths.Add(f.FullName);
            _items.Children.Add(MakeItem(f));
        }
        if (files.Count == 0)
            _items.Children.Add(new TextBlock { Text = "Captures you take will appear here.", Foreground = Brushes.Gray, Margin = new Thickness(8, 0, 0, 0) });
        _count.Text = files.Count == 0 ? "none yet" : files.Count == 1 ? "1 capture" : $"{files.Count} captures";
        Highlight();
    }

    public void SetCurrent(string? path)
    {
        _current = path;
        Highlight();
    }

    /// <summary>The capture next to the current one: +1 = older, -1 = newer.</summary>
    public string? Neighbor(int direction)
    {
        if (_paths.Count == 0) return null;
        int i = _current == null ? -1 : _paths.FindIndex(p => string.Equals(p, _current, StringComparison.OrdinalIgnoreCase));
        int next = i < 0 ? 0 : i + direction;
        return next >= 0 && next < _paths.Count ? _paths[next] : null;
    }

    void Highlight()
    {
        foreach (var child in _items.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is not string path || ((Panel)child).Children[0] is not Border frame) continue;
            bool current = string.Equals(path, _current, StringComparison.OrdinalIgnoreCase);
            frame.BorderBrush = current ? Accent : Idle;
            frame.BorderThickness = new Thickness(current ? 2.5 : 1);
            if (current) child.Dispatcher.BeginInvoke(() => child.BringIntoView(), DispatcherPriority.Loaded);
        }
    }

    // ---------- items ----------

    static string Caption(DateTime t)
    {
        var today = DateTime.Today;
        if (t.Date == today) return t.ToString("HH:mm");
        if (t.Date == today.AddDays(-1)) return "Yesterday " + t.ToString("HH:mm");
        return t.ToString("d MMM HH:mm");
    }

    FrameworkElement MakeItem(FileInfo f)
    {
        string path = f.FullName;
        var img = new Image { Height = ThumbHeight, MaxWidth = 140, MinWidth = 24, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        var frame = new Border
        {
            Child = img, Background = Brushes.White, CornerRadius = new CornerRadius(4), Padding = new Thickness(1),
            BorderBrush = Idle, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var item = new StackPanel
        {
            Tag = path, Margin = new Thickness(4, 0, 4, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = $"{f.Name}\n{f.LastWriteTime:g}   ·   {Math.Max(1, f.Length / 1024)} KB\n" +
                      "Click: open  ·  Ctrl+click: new window  ·  Drag: drop into another app",
        };
        item.Children.Add(frame);
        item.Children.Add(new TextBlock
        {
            Text = Caption(f.LastWriteTime), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 0),
        });

        item.MouseEnter += (_, _) => { if (frame.BorderBrush != Accent) frame.BorderBrush = Hover; };
        item.MouseLeave += (_, _) => { if (frame.BorderBrush != Accent) frame.BorderBrush = Idle; };

        Point? down = null;
        item.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(item); item.CaptureMouse(); e.Handled = true; };
        item.MouseMove += (_, e) =>
        {
            if (down is not { } d || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(item) - d).Length < 6) return;
            down = null;
            item.ReleaseMouseCapture();
            DragDrop.DoDragDrop(item, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
        };
        item.MouseLeftButtonUp += (_, e) =>
        {
            if (down == null) return;
            down = null;
            item.ReleaseMouseCapture();
            OpenRequested?.Invoke(path, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            e.Handled = true;
        };
        item.ContextMenu = MakeMenu(path);

        LoadThumbnail(f, img);
        return item;
    }

    ContextMenu MakeMenu(string path)
    {
        var menu = new ContextMenu();
        void Add(string header, Action action)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
        Add("Open", () => OpenRequested?.Invoke(path, false));
        Add("Open in new window", () => OpenRequested?.Invoke(path, true));
        Add("Copy image", () =>
        {
            try { OutputService.CopyToClipboard(Imaging.LoadFile(path)); Message?.Invoke($"Copied {Path.GetFileName(path)}."); }
            catch (Exception ex) { Message?.Invoke("Copy failed: " + ex.Message); }
        });
        Add("Show in folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }));
        menu.Items.Add(new Separator());
        Add("Delete (to Recycle Bin)", () => Delete(path));
        return menu;
    }

    void Delete(string path)
    {
        var owner = Window.GetWindow(this);
        if (MessageBox.Show(owner, $"Move {Path.GetFileName(path)} to the Recycle Bin?", "SnapTool",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            Refresh();
        }
        catch (Exception ex) { Message?.Invoke("Delete failed: " + ex.Message); }
    }

    async void LoadThumbnail(FileInfo f, Image target)
    {
        string path = f.FullName;
        var stamp = f.LastWriteTimeUtc;
        if (_thumbs.TryGetValue(path, out var cached) && cached.Stamp == stamp)
        {
            target.Source = cached.Image;
            return;
        }
        int decodeHeight = (int)Math.Ceiling(ThumbHeight * VisualTreeHelper.GetDpi(this).DpiScaleY);
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelHeight = decodeHeight;
                bi.StreamSource = fs;
                bi.EndInit();
                bi.Freeze();
                return (BitmapSource)bi;
            });
            _thumbs[path] = (stamp, bmp);
            target.Source = bmp;
        }
        catch (Exception)
        {
            target.Source = null; // still being written, or not an image; the next refresh retries
        }
    }

    static void OpenFolder()
    {
        var folder = App.Settings.ResolvedSaveFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
}
