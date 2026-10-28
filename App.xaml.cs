using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using SnapTool.Editor;

namespace SnapTool;

public partial class App : Application
{
    public static SettingsStore Store { get; } = new();
    public static AppSettings Settings => Store.Current;

    Mutex? _mutex;
    bool _ownsMutex;
    TrayService? _tray;
    HotkeyService? _hotkeys;
    CaptureController? _capture;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Windows.Forms.Application.EnableVisualStyles();
        Store.Load();

        // Developer helper: SnapTool.exe --write-icon path.ico
        int iconArg = Array.IndexOf(e.Args, "--write-icon");
        if (iconArg >= 0 && iconArg + 1 < e.Args.Length)
        {
            AppIcon.SaveIco(e.Args[iconArg + 1]);
            Shutdown();
            return;
        }

        // SnapTool.exe --selftest [folder]: automated checks, no desktop input needed.
        int testArg = Array.IndexOf(e.Args, "--selftest");
        if (testArg >= 0)
        {
            string dir = testArg + 1 < e.Args.Length ? e.Args[testArg + 1] : Path.Combine(Path.GetTempPath(), "SnapToolSelfTest");
            Dispatcher.BeginInvoke(async () => Shutdown(await SelfTest.Run(dir) ? 0 : 1));
            return;
        }

        // "SnapTool.exe picture.png" opens just the editor (works while the tray app is running too).
        var file = e.Args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        if (file != null)
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            var editor = EditorWindow.OpenFile(file);
            if (editor == null) Shutdown(); else editor.Show();
            return;
        }

        _mutex = new Mutex(true, @"Local\SnapTool.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("SnapTool is already running – look for its icon in the notification area.", "SnapTool");
            Shutdown();
            return;
        }

        _tray = new TrayService();
        _capture = new CaptureController(_tray);
        _tray.CaptureRequested += () => _capture.Start(false, 250);
        _tray.ScrollCaptureRequested += () => _capture.Start(true, 250);
        _tray.OpenLastRequested += () => _capture.OpenLast();
        _tray.OpenPathRequested += path => EditorWindow.OpenFile(path)?.Show();
        _tray.OpenFileRequested += OpenFileDialog;
        _tray.ExitRequested += () => Shutdown();

        _hotkeys = new HotkeyService();
        var errors = RegisterHotkeys();

        Store.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            var errs = RegisterHotkeys();
            if (Store.LastError != null) errs.Insert(0, "settings.json has an error: " + Store.LastError);
            _tray.ShowBalloon(errs.Count == 0 ? "Settings reloaded" : "Settings problem",
                errs.Count == 0 ? $"Capture: {Settings.CaptureHotkey}" : string.Join("\n", errs), null, errs.Count > 0);
        });
        Store.Watch();

        if (Store.LastError != null) errors.Insert(0, "settings.json has an error: " + Store.LastError);
        if (errors.Count > 0) _tray.ShowBalloon("SnapTool started with problems", string.Join("\n", errors), null, true);
        else if (!e.Args.Contains("--quiet")) _tray.ShowBalloon("SnapTool is running", $"Press {Settings.CaptureHotkey} to capture.");

        if (e.Args.Contains("--capture")) _capture.Start(false, 300);
        MemoryTrim.Soon();
    }

    public static App Instance => (App)Current;

    /// <summary>True in the tray instance (the one that owns the global hotkeys).</summary>
    public bool OwnsHotkeys => _hotkeys != null;

    /// <summary>Stops the global hotkeys, e.g. while the user records a new one.</summary>
    public void SuspendHotkeys() => _hotkeys?.UnregisterAll();

    /// <summary>Registers the hotkeys from the current settings; returns problems, if any.</summary>
    public List<string> ResumeHotkeys() => _hotkeys == null ? new List<string>() : RegisterHotkeys();

    List<string> RegisterHotkeys()
    {
        var errors = new List<string>();
        _hotkeys!.UnregisterAll();
        if (!string.IsNullOrWhiteSpace(Settings.CaptureHotkey) &&
            !_hotkeys.Register(Settings.CaptureHotkey, () => _capture!.Start(false), out var e1)) errors.Add(e1);
        if (!string.IsNullOrWhiteSpace(Settings.ScrollCaptureHotkey) &&
            !_hotkeys.Register(Settings.ScrollCaptureHotkey, () => _capture!.Start(true), out var e2)) errors.Add(e2);
        return errors;
    }

    static void OpenFileDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            InitialDirectory = Directory.Exists(Settings.ResolvedSaveFolder) ? Settings.ResolvedSaveFolder : "",
        };
        if (dlg.ShowDialog() == true) EditorWindow.OpenFile(dlg.FileName)?.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
