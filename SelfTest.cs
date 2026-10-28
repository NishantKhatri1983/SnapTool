using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnapTool.Editor;

namespace SnapTool;

/// <summary>
/// "SnapTool.exe --selftest folder": checks the scroll stitcher against a simulated scrolling window and
/// drives the editor programmatically (every markup tool, flatten, crop, resize), writing images and a log.
/// No mouse or keyboard input is used, so it runs without touching the desktop.
/// </summary>
internal static class SelfTest
{
    public static async Task<bool> Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new List<string>();
        bool ok = true;
        void Check(bool cond, string what) { log.Add((cond ? "PASS  " : "FAIL  ") + what); ok &= cond; }

        try
        {
            TestStitching(outDir, Check, log);
            TestManualScroll(outDir, Check, log);
            TestManualScrollNoisy(outDir, Check, log);
            TestScrollSpeed(Check, log);
            TimeScreenGrab(log);
            await TestEditor(outDir, Check);
            await TestSettings(outDir, Check);
        }
        catch (Exception ex)
        {
            Check(false, "exception: " + ex);
        }
        File.WriteAllLines(Path.Combine(outDir, "selftest.log"), log);
        return ok;
    }

    // ---------- scrolling capture ----------

    static BitmapSource RenderDocument(int w, int h)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            var face = new Typeface("Segoe UI");
            var rnd = new Random(7);
            for (int y = 10, line = 1; y < h - 30; y += 26, line++)
            {
                if (line % 9 == 0) { y += 20; continue; } // blank gaps, like paragraphs
                string words = new string('x', rnd.Next(5, 40)).Replace("x", "lorem ipsum ");
                string text = $"Line {line}: " + words.Substring(0, Math.Min(words.Length, rnd.Next(20, 70)));
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 15,
                    line % 7 == 0 ? Brushes.DarkBlue : Brushes.Black, 1.0);
                dc.DrawText(ft, new Point(16, y));
                if (line % 13 == 0) dc.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(w - 220, y, 160, 20));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Imaging.Normalize(rtb);
    }

    static void TestStitching(string outDir, Action<bool, string> check, List<string> log)
    {
        const int W = 640, docH = 3000, header = 64, footer = 40, view = 420, notchPx = 53, scrollbar = 16;
        var doc = RenderDocument(W - scrollbar, docH);
        var docPx = new byte[(W - scrollbar) * 4 * docH];
        doc.CopyPixels(docPx, (W - scrollbar) * 4, 0);
        int offset = 900; // start mid-page: the capture must scroll to the top first
        int frameH = header + view + footer;

        ScrollCapture.Frame Grab()
        {
            var px = new byte[W * 4 * frameH];
            for (int y = 0; y < frameH; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                byte b, g, r;
                if (y < header) { b = 0x60; g = 0x40; r = 0x20; if (x > 20 && x < 300 && y > 20 && y < 44) b = g = r = 0xEE; }
                else if (y >= header + view) { b = g = r = (byte)(x % 50 < 25 ? 0xCC : 0x99); }
                else if (x >= W - scrollbar)
                {
                    int thumbTop = header + (int)((double)offset / (docH - view) * (view - 60));
                    b = g = r = (byte)(y >= thumbTop && y < thumbTop + 60 ? 0x80 : 0xE8);
                }
                else
                {
                    int j = ((offset + y - header) * (W - scrollbar) + x) * 4;
                    b = docPx[j]; g = docPx[j + 1]; r = docPx[j + 2];
                }
                px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
            }
            return ScrollCapture.MakeFrame(px, W, frameH, Math.Min(W / 4, 40));
        }

        int wheelCalls = 0;
        void Wheel(int notches)
        {
            wheelCalls++;
            offset = Math.Clamp(offset - notches * notchPx, 0, docH - view);
        }

        var settings = new AppSettings { ScrollSettleMs = 30, ScrollToTopFirst = true };
        settings.Normalize();
        var started = DateTime.Now;
        var result = ScrollCapture.Stitch(Grab, Wheel, () => false, settings);
        log.Add($"      stitched {result.PixelWidth}x{result.PixelHeight} from {wheelCalls} scroll steps in {(DateTime.Now - started).TotalSeconds:0.0}s");
        OutputService.SaveImage(result, Path.Combine(outDir, "scroll_stitched.png"), 90);

        int expectedH = header + docH + footer;
        check(result.PixelWidth == W && result.PixelHeight == expectedH, $"stitched size {result.PixelWidth}x{result.PixelHeight} == {W}x{expectedH}");

        // The document body must come out pixel-identical (scrollbar column excluded).
        var outPx = new byte[W * 4 * result.PixelHeight];
        result.CopyPixels(outPx, W * 4, 0);
        int bad = 0;
        if (result.PixelHeight == expectedH)
            for (int y = 0; y < docH; y++)
            for (int x = 0; x < W - scrollbar; x++)
            {
                int o = ((header + y) * W + x) * 4, d = (y * (W - scrollbar) + x) * 4;
                if (outPx[o] != docPx[d] || outPx[o + 1] != docPx[d + 1] || outPx[o + 2] != docPx[d + 2]) bad++;
            }
        check(result.PixelHeight == expectedH && bad == 0, $"stitched document matches source pixel-for-pixel ({bad} differing pixels)");
    }

    /// <summary>One view of a simulated scrolling window: fixed header, document at offset, scrollbar, fixed footer.</summary>
    static ScrollCapture.Frame ViewFrame(byte[] docPx, int docW, int docH, int offset, bool hoverNoise, int jitter = 0, int seed = 0)
    {
        const int header = 64, view = 420, footer = 40, scrollbar = 16;
        int W = docW + scrollbar, frameH = header + view + footer;
        var px = new byte[W * 4 * frameH];
        var rnd = new Random(seed);
        for (int y = 0; y < frameH; y++)
        for (int x = 0; x < W; x++)
        {
            int i = (y * W + x) * 4;
            byte b, g, r;
            if (y < header) { b = 0x60; g = 0x40; r = 0x20; if (x > 20 && x < 300 && y > 20 && y < 44) b = g = r = 0xEE; }
            else if (y >= header + view) { b = g = r = (byte)(x % 50 < 25 ? 0xCC : 0x99); }
            else if (x >= docW)
            {
                int thumbTop = header + (int)((double)offset / (docH - view) * (view - 60));
                b = g = r = (byte)(y >= thumbTop && y < thumbTop + 60 ? 0x80 : 0xE8);
            }
            else if (hoverNoise && y >= header + 200 && y < header + 206 && x < 200) { b = 0xFF; g = 0xE0; r = 0xC0; }
            else
            {
                int j = ((offset + y - header) * docW + x) * 4;
                b = docPx[j]; g = docPx[j + 1]; r = docPx[j + 2];
                if (jitter > 0)
                {
                    // Small random differences on every frame, like re-rendered text or video compression in a browser.
                    int n = rnd.Next(-jitter, jitter + 1);
                    b = (byte)Math.Clamp(b + n, 0, 255); g = (byte)Math.Clamp(g + n, 0, 255); r = (byte)Math.Clamp(r + n, 0, 255);
                }
            }
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return ScrollCapture.MakeFrame(px, W, frameH, Math.Min(W / 4, 40));
    }

    static void TestManualScroll(string outDir, Action<bool, string> check, List<string> log)
    {
        const int docW = 624, docH = 3000, header = 64, footer = 40, view = 420;
        var doc = RenderDocument(docW, docH);
        var docPx = new byte[docW * 4 * docH];
        doc.CopyPixels(docPx, docW * 4, 0);

        // The user's scrolling: small and big steps, a hover change, a jump too far (tracking lost, then
        // recovered), the end of the page, a jump back, and finally above where they started.
        var path = new (int Offset, bool Noise)[]
        {
            (300, false), (300, false), (340, false), (420, false), (700, false), (700, true), (1000, false),
            (1700, false), (1720, false), (1740, false), (1300, false), (1680, false), (2000, false), (2300, false), (2580, false), (2580, false), (1500, false), (200, false),
        };
        var session = new ManualScrollSession(docW + 16, header + view + footer, 20000);
        bool lostReported = false;
        session.Progress += (text, warn) => lostReported |= warn && text.Contains("Lost track");
        var started = DateTime.Now;
        string lastStatus = "";
        session.Progress += (text, warn) => lastStatus = text;
        foreach (var (offset, noise) in path)
        {
            session.Process(ViewFrame(docPx, docW, docH, offset, noise));
            log.Add($"        view at {offset,4}{(noise ? " (hover)" : "")}: height {session.CapturedHeight}  {lastStatus}");
        }
        var result = session.Compose();
        log.Add($"      manual: {path.Length} views stitched into {result.PixelWidth}x{result.PixelHeight} in {(DateTime.Now - started).TotalMilliseconds:0} ms");
        OutputService.SaveImage(result, Path.Combine(outDir, "manual_scroll_stitched.png"), 90);

        int expectedH = header + (docH - 200) + footer;
        check(result.PixelHeight == expectedH, $"manual scroll result height {result.PixelHeight} == {expectedH} (document rows 200–3000 + fixed header/footer)");
        check(lostReported, "jumping too far reports 'lost track' and capture recovers after scrolling back");

        int W = docW + 16, bad = 0;
        var outPx = new byte[W * 4 * result.PixelHeight];
        result.CopyPixels(outPx, W * 4, 0);
        if (result.PixelHeight == expectedH)
            for (int y = 0; y < docH - 200; y++)
            for (int x = 0; x < docW; x++)
            {
                int o = ((header + y) * W + x) * 4, d = ((200 + y) * docW + x) * 4;
                if (outPx[o] != docPx[d] || outPx[o + 1] != docPx[d + 1] || outPx[o + 2] != docPx[d + 2]) bad++;
            }
        check(result.PixelHeight == expectedH && bad == 0, $"manual scroll result matches the document pixel-for-pixel ({bad} differing pixels)");
    }

    /// <summary>The same scroll path, but every frame is slightly different (noise), so no row ever matches exactly.</summary>
    static void TestManualScrollNoisy(string outDir, Action<bool, string> check, List<string> log)
    {
        const int docW = 624, docH = 3000, header = 64, footer = 40, view = 420;
        var doc = RenderDocument(docW, docH);
        var docPx = new byte[docW * 4 * docH];
        doc.CopyPixels(docPx, docW * 4, 0);
        int[] path = { 300, 300, 330, 410, 520, 700, 760, 1000, 1180, 1300, 1680, 2000, 2300, 2580, 2580, 1500, 200 };
        var session = new ManualScrollSession(docW + 16, header + view + footer, 20000);
        int lost = 0;
        session.Progress += (text, warn) => { if (warn) lost++; };
        var started = DateTime.Now;
        for (int i = 0; i < path.Length; i++) session.Process(ViewFrame(docPx, docW, docH, path[i], false, jitter: 6, seed: i + 1));
        var result = session.Compose();
        double ms = (DateTime.Now - started).TotalMilliseconds;
        log.Add($"      noisy manual: {path.Length} views -> {result.PixelWidth}x{result.PixelHeight} in {ms:0} ms ({ms / path.Length:0.0} ms per frame incl. test rendering)");
        OutputService.SaveImage(result, Path.Combine(outDir, "manual_scroll_noisy.png"), 90);

        int expectedH = header + (docH - 200) + footer;
        check(result.PixelHeight == expectedH && lost == 0, $"noisy frames (no exact row matches): height {result.PixelHeight} == {expectedH}, lost-track warnings: {lost}");

        int W = docW + 16, misplaced = 0;
        var outPx = new byte[W * 4 * result.PixelHeight];
        result.CopyPixels(outPx, W * 4, 0);
        if (result.PixelHeight == expectedH)
            for (int y = 0; y < docH - 200; y++)
            {
                long diff = 0;
                for (int x = 0; x < docW; x++)
                {
                    int o = ((header + y) * W + x) * 4, d = ((200 + y) * docW + x) * 4;
                    diff += Math.Abs(outPx[o] - docPx[d]) + Math.Abs(outPx[o + 1] - docPx[d + 1]) + Math.Abs(outPx[o + 2] - docPx[d + 2]);
                }
                if (diff / (3.0 * docW) > 8) misplaced++; // noise alone averages ~3; a row from the wrong place is far off
            }
        check(result.PixelHeight == expectedH && misplaced == 0, $"noisy result: every document row in the right place ({misplaced} misplaced rows)");
    }

    /// <summary>Time per frame for a full 2560x1600 screen, the size of the user's monitor.</summary>
    static void TestScrollSpeed(Action<bool, string> check, List<string> log)
    {
        const int W = 2560, H = 1600, header = 120, footer = 80, view = H - header - footer, docH = 5000;
        var doc = RenderDocument(W, docH);
        var docPx = new byte[W * 4 * docH];
        doc.CopyPixels(docPx, W * 4, 0);

        ScrollCapture.Frame Frame(int offset)
        {
            var px = new byte[W * 4 * H];
            for (int i = 0; i < header * W * 4; i += 4) { px[i] = 0x60; px[i + 1] = 0x40; px[i + 2] = 0x20; px[i + 3] = 255; }
            Buffer.BlockCopy(docPx, offset * W * 4, px, header * W * 4, view * W * 4);
            for (int i = (header + view) * W * 4; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 0xCC; px[i + 3] = 255; }
            return ScrollCapture.MakeFrame(px, W, H, 40);
        }

        int[] path = { 0, 0, 250, 600, 1000, 1500, 2000, 2400, 2900, 3400 };
        var frames = path.Select(Frame).ToList();
        var session = new ManualScrollSession(W, H, 20000);
        var sw = new System.Diagnostics.Stopwatch();
        var times = new List<double>();
        foreach (var f in frames)
        {
            sw.Restart();
            session.Process(f);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var result = session.Compose();
        double avg = times.Skip(2).Average(), worst = times.Max();
        log.Add($"      speed at 2560x1600: {avg:0} ms per frame on average, worst {worst:0} ms (matching only, screen grab not included)");
        int expectedH = header + (path[^1] + view) + footer;
        check(result.PixelHeight == expectedH, $"full-screen scroll stitched correctly ({result.PixelHeight} == {expectedH})");
        check(avg < 60, $"full-screen matching is fast enough for live use ({avg:0} ms per frame, target < 60)");
    }

    /// <summary>Breaks down where a live frame's time goes on this machine (read-only, nothing is clicked).</summary>
    static void TimeScreenGrab(List<string> log)
    {
        var area = ScreenGrabber.VirtualScreen;
        using var grabber = new RegionGrabber(area);
        var sw = new System.Diagnostics.Stopwatch();
        double grab = 0, hash = 0, gdiPlus = 0;
        const int n = 15;
        for (int i = 0; i < n; i++)
        {
            sw.Restart();
            var px = grabber.Grab();
            grab += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            ScrollCapture.MakeFrame(px, area.Width, area.Height, 40);
            hash += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            using (var bmp = ScreenGrabber.Capture(area)) { }
            gdiPlus += sw.Elapsed.TotalMilliseconds;
        }
        log.Add($"      live screen {area.Width}x{area.Height}: BitBlt+copy {grab / n:0} ms, row hashing {hash / n:0} ms, " +
                $"CopyFromScreen (old way) {gdiPlus / n:0} ms");
    }

    // ---------- editor ----------

    static BitmapSource RenderSampleApp()
    {
        const int w = 900, h = 560;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var face = new Typeface("Segoe UI");
            FormattedText T(string s, double size, Brush b) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, b, 1.0);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF5, 0xF6, 0xF8)), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), null, new Rect(0, 0, w, 48));
            dc.DrawText(T("Contoso Portal – Account settings", 18, Brushes.White), new Point(20, 12));
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(40, 80, 520, 420));
            dc.DrawText(T("Profile", 20, Brushes.Black), new Point(64, 100));
            string[] labels = { "Name", "Email", "Phone", "API key" };
            string[] values = { "Alex Example", "alex@example.com", "+1 555 0100", "sk-test-1234-5678-ABCD-EFGH" };
            for (int i = 0; i < labels.Length; i++)
            {
                double y = 150 + i * 70;
                dc.DrawText(T(labels[i], 13, Brushes.DimGray), new Point(64, y));
                dc.DrawRectangle(Brushes.White, new Pen(Brushes.Silver, 1), new Rect(64, y + 20, 460, 32));
                dc.DrawText(T(values[i], 14, Brushes.Black), new Point(74, y + 26));
            }
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x88, 0xE5)), null, new Rect(64, 440, 120, 36), 4, 4);
            dc.DrawText(T("Save changes", 14, Brushes.White), new Point(80, 448));
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.LightGray, 1), new Rect(600, 80, 260, 420));
            dc.DrawText(T("Notifications", 16, Brushes.Black), new Point(620, 100));
            for (int i = 0; i < 6; i++) dc.DrawText(T($"• Weekly report #{i + 1}", 13, Brushes.DimGray), new Point(620, 140 + i * 28));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Imaging.Normalize(rtb);
    }

    static async Task TestEditor(string outDir, Action<bool, string> check)
    {
        var sample = RenderSampleApp();
        OutputService.SaveImage(sample, Path.Combine(outDir, "sample.png"), 90);
        var win = new EditorWindow(sample, Path.Combine(outDir, "sample.png"));
        win.Show();
        await Task.Delay(700);
        var b = win.Board;

        ToolStyle S(string tool, Action<ToolStyle>? tweak = null) { var s = App.Settings.GetToolStyle(tool); tweak?.Invoke(s); return s; }
        b.Items.Add(new LineAnnotation("Arrow") { A = new Point(330, 520), B = new Point(190, 462), Style = S("Arrow") });
        b.Items.Add(new LineAnnotation("Line") { A = new Point(600, 60), B = new Point(860, 60), Style = S("Line", s => { s.StartCap = "Dot"; s.EndCap = "Arrow"; s.Dashed = true; s.Color = "#FF1E88E5"; }) });
        b.Items.Add(new BoxAnnotation("Rectangle") { P1 = new Point(56, 160), P2 = new Point(532, 232), Style = S("Rectangle", s => s.Shape = "Rounded") });
        b.Items.Add(new BoxAnnotation("Highlight") { P1 = new Point(612, 132), P2 = new Point(800, 162), Style = S("Highlight") });
        b.Items.Add(new BoxAnnotation("Highlight") { P1 = new Point(600, 205), P2 = new Point(800, 255), Style = S("Highlight", s => { s.Shape = "Ellipse"; s.Color = "#FF43A047"; }) });
        b.Items.Add(new BoxAnnotation("Blur") { P1 = new Point(66, 380), P2 = new Point(522, 410), Style = S("Blur") });
        b.Items.Add(new TextAnnotation { Pos = new Point(200, 450), Text = "Click here to save", Style = S("Text") });
        b.Items.Add(new TextAnnotation { Pos = new Point(600, 470), Text = "White box text", Style = S("Text", s => { s.Fill = "#FFFFFFFF"; s.Color = "#FF000000"; s.FontSize = 16; s.Bold = false; }) });
        var callout = new CalloutAnnotation { P1 = new Point(620, 330), P2 = new Point(860, 390), Tip = new Point(560, 300), Text = "Personal details are editable here", Style = S("Callout") };
        callout.FitToText();
        b.Items.Add(callout);
        b.Tool = "Select";
        b.Select(callout);
        b.Refresh();
        await Task.Delay(500);

        SaveVisual(win, Path.Combine(outDir, "editor_window.png"));
        int images = Directory.Exists(App.Settings.ResolvedSaveFolder)
            ? Directory.GetFiles(App.Settings.ResolvedSaveFolder).Count(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            : 0;
        int expected = Math.Min(images, App.Settings.RecentStripCount);
        check(win.RecentCount == expected, $"recent timeline lists the {expected} newest captures ({win.RecentCount})");
        var flat = b.Flatten();
        OutputService.SaveImage(flat, Path.Combine(outDir, "annotated.png"), 90);
        check(flat.PixelWidth == 900 && flat.PixelHeight == 560, $"flattened image keeps native size ({flat.PixelWidth}x{flat.PixelHeight})");

        // Pixelation must actually destroy the secret text: every 12px block in the blurred area is one color.
        var px = new byte[4 * 12 * 12];
        flat.CopyPixels(new Int32Rect(66 + 12, 380 + 12, 12, 12), px, 48, 0);
        bool uniform = true;
        for (int i = 4; i < px.Length; i += 4) uniform &= px[i] == px[0] && px[i + 1] == px[1] && px[i + 2] == px[2];
        check(uniform, "blur pixelates the area into solid blocks");

        // Crop: keep the left form, annotations move with the image.
        b.Tool = "Crop";
        typeof(EditorCanvas).GetField("_crop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(b, (Rect?)new Rect(30, 60, 560, 470));
        b.ApplyCrop();
        check(b.Image.PixelWidth == 560 && b.Image.PixelHeight == 470, $"crop gives 560x470 ({b.Image.PixelWidth}x{b.Image.PixelHeight})");
        var arrow = (LineAnnotation)b.Items[0];
        check(arrow.B == new Point(160, 402), $"annotations shift with the crop (arrow tip at {arrow.B})");

        b.ResizeImage(280, 235);
        check(b.Image.PixelWidth == 280 && b.Image.PixelHeight == 235, "resize to 50% gives 280x235");
        check(Math.Abs(arrow.Style.Width - 2.5) < 0.01, $"annotation widths scale with resize ({arrow.Style.Width})");

        b.Undo();
        b.Undo();
        check(b.Image.PixelWidth == 900 && b.Items.Count == 9, $"two undos restore the original ({b.Image.PixelWidth} px, {b.Items.Count} objects)");
        b.Redo();
        check(b.Image.PixelWidth == 560, "redo re-applies the crop");

        await Task.Delay(300);
        SaveVisual(win, Path.Combine(outDir, "editor_after_crop.png"));
        win.MarkClean();
        win.Close();
    }

    static async Task TestSettings(string outDir, Action<bool, string> check)
    {
        var win = new SettingsWindow();
        win.Show();
        await Task.Delay(600);
        check(win.CaptureKey.Gesture == App.Settings.CaptureHotkey, $"settings window shows the capture hotkey ({win.CaptureKey.Gesture})");

        // Record F8 through the real key handler, then clear it with Backspace.
        win.CaptureKey.Focus();
        void Press(Key key)
        {
            var source = PresentationSource.FromVisual(win.CaptureKey)!;
            win.CaptureKey.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        }
        Press(Key.F8);
        await Task.Delay(100);
        check(win.CaptureKey.Gesture == "F8", $"hotkey box records a key press ({win.CaptureKey.Gesture})");
        check(win.CaptureStatus.Text.Contains("Available") || win.CaptureStatus.Text.Contains("another app"), $"hotkey is checked for conflicts (\"{win.CaptureStatus.Text}\")");
        Press(Key.Back);
        check(win.CaptureKey.Gesture == "", "Backspace clears the hotkey");
        check(HotkeyService.TryParse("Ctrl+Shift+S", out _, out _) && HotkeyService.TryParse("PrintScreen", out _, out _) && !HotkeyService.TryParse("Ctrl+Nope", out _, out _),
            "hotkey text parsing");

        win.CaptureKey.Gesture = App.Settings.CaptureHotkey;
        win.ScrollKey.Focus();
        await Task.Delay(300);
        SaveVisual(win, Path.Combine(outDir, "settings_window.png"));
        win.Close(); // cancel: nothing is written
    }

    static void SaveVisual(Window win, string path)
    {
        var root = (FrameworkElement)win.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(root);
        OutputService.SaveImage(Imaging.Normalize(rtb), path, 90);
    }
}
