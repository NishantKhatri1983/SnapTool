using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace SnapTool;

/// <summary>Notification-area icon, its menu, and balloon/toast notifications.</summary>
/// <summary>"Start with Windows" via the per-user Run key.</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("SnapTool") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("SnapTool", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("SnapTool", false);
        }
    }
}

public sealed class TrayService : IDisposable
{
    readonly WF.NotifyIcon _icon;
    readonly WF.ToolStripMenuItem _captureItem, _scrollItem, _recentItem, _startupItem;
    Action? _balloonAction;

    public event Action? CaptureRequested, ScrollCaptureRequested, OpenLastRequested, OpenFileRequested, ExitRequested;
    public event Action<string>? OpenPathRequested;

    public TrayService()
    {
        var menu = new WF.ContextMenuStrip();
        _captureItem = new WF.ToolStripMenuItem("Capture", null, (_, _) => CaptureRequested?.Invoke()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        _scrollItem = new WF.ToolStripMenuItem("Scrolling capture", null, (_, _) => ScrollCaptureRequested?.Invoke());
        _recentItem = new WF.ToolStripMenuItem("Recent captures");
        _startupItem = new WF.ToolStripMenuItem("Start with Windows", null, (_, _) => AutoStart.Enabled = !AutoStart.Enabled);
        menu.Items.Add(_captureItem);
        menu.Items.Add(_scrollItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Open last capture in editor", null, (_, _) => OpenLastRequested?.Invoke());
        menu.Items.Add(_recentItem);
        menu.Items.Add("Open image in editor…", null, (_, _) => OpenFileRequested?.Invoke());
        menu.Items.Add("Open captures folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => SettingsWindow.ShowSingle());
        menu.Items.Add("Edit settings.json (advanced)…", null, (_, _) => OpenSettingsFile());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Exit SnapTool", null, (_, _) => ExitRequested?.Invoke());
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new WF.NotifyIcon
        {
            Icon = AppIcon.CreateTrayIcon(WF.SystemInformation.SmallIconSize.Width),
            Text = "SnapTool",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) CaptureRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => _balloonAction?.Invoke();
    }

    void RefreshMenu()
    {
        var s = App.Settings;
        _captureItem.ShortcutKeyDisplayString = s.CaptureHotkey;
        _scrollItem.ShortcutKeyDisplayString = s.ScrollCaptureHotkey;
        _startupItem.Checked = AutoStart.Enabled;

        _recentItem.DropDownItems.Clear();
        var folder = s.ResolvedSaveFolder;
        var files = Directory.Exists(folder)
            ? new DirectoryInfo(folder).EnumerateFiles()
                .Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp")
                .OrderByDescending(f => f.LastWriteTime).Take(12).ToList()
            : new();
        foreach (var f in files)
        {
            var path = f.FullName;
            _recentItem.DropDownItems.Add($"{f.Name}   ({f.LastWriteTime:g})", null, (_, _) => OpenPathRequested?.Invoke(path));
        }
        if (files.Count == 0) _recentItem.DropDownItems.Add(new WF.ToolStripMenuItem("(none yet)") { Enabled = false });
    }

    public void ShowBalloon(string title, string text, Action? onClick = null, bool error = false)
    {
        _balloonAction = onClick;
        _icon.ShowBalloonTip(4000, title, text, error ? WF.ToolTipIcon.Error : WF.ToolTipIcon.Info);
    }

    public void SetStatus(string? text) => _icon.Text = text == null ? "SnapTool" : ("SnapTool – " + text)[..Math.Min(63, ("SnapTool – " + text).Length)];

    static void OpenFolder()
    {
        var folder = App.Settings.ResolvedSaveFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public static void OpenSettingsFile()
    {
        App.Store.Save(); // make sure every current option is written out
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{App.Store.FilePath}\"") { UseShellExecute = true });
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
