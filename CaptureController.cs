using System;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using SnapTool.Editor;
using SD = System.Drawing;

namespace SnapTool;

/// <summary>Hotkey -> overlay -> (scroll) capture -> clipboard / file / editor.</summary>
public sealed class CaptureController
{
    readonly TrayService _tray;
    bool _busy;
    BitmapSource? _lastImage;
    string? _lastPath;

    public CaptureController(TrayService tray) => _tray = tray;

    public async void Start(bool scrollMode, int delayMs = 0)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs); // let a menu or flyout disappear first
            var s = App.Settings;
            var vs = ScreenGrabber.VirtualScreen;
            OverlayResult? result;
            BitmapSource? image = null;
            using (var shot = ScreenGrabber.Capture(vs))
            {
                var windows = ScreenGrabber.GetWindows();
                using (var overlay = new OverlayForm(shot, vs, windows, s, scrollMode))
                {
                    overlay.ShowDialog();
                    result = overlay.Result;
                }
                if (result == null) return;
                if (!result.Scroll)
                {
                    var local = result.Rect;
                    local.Offset(-vs.X, -vs.Y);
                    local.Intersect(new SD.Rectangle(0, 0, shot.Width, shot.Height));
                    if (local.Width < 1 || local.Height < 1) return;
                    using var crop = shot.Clone(local, PixelFormat.Format32bppArgb);
                    image = ScreenGrabber.ToBitmapSource(crop);
                }
            }

            if (result.Scroll)
            {
                await Task.Delay(150); // overlay fully gone
                bool manual = !string.Equals(s.ScrollMode, "Auto", StringComparison.OrdinalIgnoreCase);
                _tray.SetStatus(manual ? "scroll, then Enter / End (Esc cancels)" : "scrolling capture… (Esc stops)");
                try
                {
                    image = manual
                        ? await ManualScrollCapture.RunAsync(result.Rect, s)
                        : await Task.Run(() => ScrollCapture.Run(result.Rect, result.Anchor, s));
                }
                finally { _tray.SetStatus(null); }
            }
            if (image != null) Deliver(image, result.Scroll);
        }
        catch (Exception ex)
        {
            Log.Write("Capture failed: " + ex);
            _tray.ShowBalloon("Capture failed", ex.Message, null, error: true);
        }
        finally
        {
            _busy = false;
            MemoryTrim.Soon();
        }
    }

    void Deliver(BitmapSource image, bool scrolled)
    {
        var s = App.Settings;
        _lastImage = image;
        _lastPath = null;
        string? saveError = null, copyError = null;

        // Encode the PNG once and use it for both the file and the clipboard.
        byte[]? png = null;
        bool savePng = s.AutoSave && !(s.ImageFormat.Trim().ToLowerInvariant() is "jpg" or "jpeg");
        try { if (savePng || s.CopyToClipboard) png = OutputService.EncodePng(image); } catch { png = null; }

        if (s.AutoSave)
        {
            try
            {
                _lastPath = OutputService.NextCapturePath(s);
                OutputService.SaveImage(image, _lastPath, s.JpegQuality, png);
            }
            catch (Exception ex) { saveError = ex.Message; _lastPath = null; }
        }
        if (s.CopyToClipboard)
        {
            try { OutputService.CopyToClipboard(image, png); }
            catch (Exception ex) { copyError = ex.Message; }
        }
        if (s.OpenEditorAfterCapture) OpenLast();
        // Once it's on disk the editor can open the file, so don't keep a possibly huge image in memory while idle.
        if (_lastPath != null) _lastImage = null;

        if (saveError != null || copyError != null)
        {
            _tray.ShowBalloon("SnapTool: problem after capture",
                (saveError != null ? "Save failed: " + saveError + "\n" : "") + (copyError != null ? "Copy failed: " + copyError : ""),
                OpenLast, error: true);
        }
        else if (s.ShowNotification)
        {
            string what = s.CopyToClipboard && _lastPath != null ? "Copied and saved" : s.CopyToClipboard ? "Copied" : _lastPath != null ? "Saved" : "Captured";
            string detail = $"{image.PixelWidth} × {image.PixelHeight}{(scrolled ? " (scrolling)" : "")}" +
                            (_lastPath != null ? $" → {Path.GetFileName(_lastPath)}" : "");
            _tray.ShowBalloon($"{what} – click to edit", detail, OpenLast);
        }
    }

    public void OpenLast()
    {
        if (_lastImage != null)
        {
            new EditorWindow(_lastImage, _lastPath).Show();
            return;
        }
        if (_lastPath != null && File.Exists(_lastPath)) { EditorWindow.OpenFile(_lastPath)?.Show(); return; }
        // Nothing captured this session: open the newest file in the save folder.
        var folder = App.Settings.ResolvedSaveFolder;
        if (Directory.Exists(folder))
        {
            var newest = new DirectoryInfo(folder).GetFiles("*.png");
            Array.Sort(newest, (a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            if (newest.Length > 0) { EditorWindow.OpenFile(newest[0].FullName)?.Show(); return; }
        }
        _tray.ShowBalloon("SnapTool", "No captures yet.");
    }
}
