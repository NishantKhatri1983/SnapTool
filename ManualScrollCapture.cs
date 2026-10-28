using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using WF = System.Windows.Forms;
using Frame = SnapTool.ScrollCapture.Frame;

namespace SnapTool;

/// <summary>
/// Manual scrolling capture: the user scrolls the chosen area however they like (wheel, scrollbar, keys, up or down)
/// while SnapTool watches it and adds every newly revealed row. Enter / End / Done finishes, Esc cancels.
/// </summary>
internal static class ManualScrollCapture
{
    public static async Task<BitmapSource?> RunAsync(SD.Rectangle rect, AppSettings s)
    {
        int ignoreRight = Math.Min(rect.Width / 4, 40); // scrollbars change on every step
        var session = new ManualScrollSession(rect.Width, rect.Height, s.ScrollMaxHeight, ignoreRight);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accent = ParseColor(s.OverlayAccentColor);

        using var border = new ScrollBorderForm(rect, accent);
        using var panel = new ScrollPanelForm(rect, accent);
        panel.Done += () => finished.TrySetResult(true);
        panel.Cancelled += () => finished.TrySetResult(false);
        border.Show();
        panel.Show();

        using var keys = new HotkeyService();
        keys.Register("Enter", () => finished.TrySetResult(true), out _);
        keys.Register("End", () => finished.TrySetResult(true), out _);
        keys.Register("Escape", () => finished.TrySetResult(false), out _);

        int warnings = 0;
        session.Progress += (text, warn) =>
        {
            if (warn) Interlocked.Increment(ref warnings);
            if (panel.IsHandleCreated && !panel.IsDisposed) panel.BeginInvoke(() => panel.SetStatus(text, warn));
        };

        using var stop = new CancellationTokenSource();
        int frames = 0;
        double busyMs = 0;
        var worker = Task.Run(() =>
        {
            var sw = new System.Diagnostics.Stopwatch();
            using var grabber = new RegionGrabber(rect);
            // A few reusable screen buffers: the session keeps at most three frames, so grabbing never allocates.
            var pool = new List<Frame>();
            Frame NextFree()
            {
                foreach (var f in pool) if (!session.Holds(f)) return f;
                var n = ScrollCapture.NewFrame(rect.Width, rect.Height);
                pool.Add(n);
                return n;
            }
            while (!stop.IsCancellationRequested)
            {
                sw.Restart();
                var frame = NextFree();
                grabber.Grab(frame.Px);
                session.Process(ScrollCapture.Rehash(frame, ignoreRight));
                busyMs += sw.Elapsed.TotalMilliseconds;
                frames++;
                // While the content moves, grab again at once (smaller steps between frames = more overlap to match);
                // while it's still, rest a little so an idle session costs next to nothing.
                if (!session.Moving) Thread.Sleep(50);
            }
        });

        bool done = await finished.Task;
        stop.Cancel();
        await worker;
        Log.Write($"Manual scroll capture {(done ? "finished" : "cancelled")}: area {rect.Width}x{rect.Height}, " +
                  $"{frames} frames, {busyMs / Math.Max(1, frames):0} ms per frame, height {session.CapturedHeight}, warnings {warnings}");
        panel.Close();
        border.Close();
        return done ? session.Compose() : null;
    }

    static SD.Color ParseColor(string hex)
    {
        try
        {
            var h = hex.TrimStart('#');
            if (h.Length == 6) h = "FF" + h;
            return SD.Color.FromArgb(Convert.ToInt32(h, 16));
        }
        catch { return SD.Color.FromArgb(30, 136, 229); }
    }
}

/// <summary>
/// Turns a stream of frames of one screen area into one tall image. Rows that never move (title bars, sticky headers,
/// footers) are found at the first real scroll and kept once. The moving middle is placed by comparing brightness
/// profiles of its rows against everything captured so far: a coarse profile finds candidate offsets quickly, a fine
/// profile decides. Small pixel differences (smooth scrolling, sub-pixel text, hover effects, animations) don't break
/// it, an ambiguous fit is skipped rather than guessed, and it recovers after a jump once the view overlaps again.
/// </summary>
internal sealed class ManualScrollSession
{
    const int CoarseBins = 32;
    const double MaxCost = 10;      // mean fine-profile difference (brightness levels) that still counts as a fit
    const double NoiseLevel = 4;    // fits this good are "the same content" even if repeated elsewhere
    const double Distinct = 0.55;   // best fit must be this much better than any fit 6+ rows away
    const int MinOverlap = 16;      // rows that must overlap captured content to place a frame
    const int LostAfter = 3;        // consecutive misses before telling the user

    readonly int _w, _h, _maxHeight, _usable, _fineBins;
    Frame? _prev, _headerSource, _footerSource;
    Profiles? _prevProfiles, _spareProfiles;
    int _top = -1, _bottom;
    readonly List<byte[]> _rows = new();
    readonly List<byte[]> _rowCoarse = new();
    readonly List<byte[]> _rowFine = new();
    readonly List<bool> _rowTextured = new();
    int _firstPos, _pos;
    int _confirmedTop, _confirmedBottom; // fixed rows that stayed identical in every frame so far
    int _footerPos;                      // content position of the footer-source frame
    int _misses;
    int _velocity;                       // rows moved between the last two placed frames
    bool _prevPlaced;                    // whether the previous frame's position is known
    bool _limitHit;

    sealed class Profiles
    {
        public byte[] Coarse = Array.Empty<byte>(); // H * CoarseBins
        public byte[] Fine = Array.Empty<byte>();   // H * fineBins
        public bool[] Textured = Array.Empty<bool>();
    }

    /// <summary>(status text, isWarning)</summary>
    public event Action<string, bool>? Progress;

    public ManualScrollSession(int width, int height, int maxHeight, int ignoreRight = -1)
    {
        _w = width;
        _h = height;
        _maxHeight = maxHeight;
        int ignore = ignoreRight >= 0 ? ignoreRight : Math.Min(width / 4, 40);
        _usable = Math.Max(CoarseBins, _w - ignore);
        _fineBins = Math.Clamp(_usable / 4, CoarseBins, 256) / 16 * 16; // multiple of 16 for the SIMD compare
    }

    /// <summary>Whether the last frame differed from the one before (the user is scrolling).</summary>
    public bool Moving { get; private set; }

    /// <summary>True while the session still needs this frame (its pixels must not be overwritten).</summary>
    public bool Holds(Frame f) => ReferenceEquals(f, _prev) || ReferenceEquals(f, _headerSource) || ReferenceEquals(f, _footerSource);

    int Mid => _h - _top - _bottom;
    int LastPos => _firstPos + _rows.Count;
    public int CapturedHeight => _top < 0 ? _h : _top + _rows.Count + _bottom;

    public void Process(Frame cur)
    {
        if (_prev == null)
        {
            _prev = _headerSource = _footerSource = cur;
            _prevProfiles = MakeProfiles(cur);
            Report("Scroll the area now (wheel, scrollbar or keys). Enter / End = done, Esc = cancel.", false);
            return;
        }
        if (ScrollCapture.Same(cur, _prev)) { _velocity = 0; Moving = false; return; } // scrolling has stopped
        Moving = true;
        var profiles = MakeProfiles(cur, _spareProfiles);
        _spareProfiles = null;

        if (_top < 0)
        {
            // Not scrolled yet: wait for the first real movement to learn which rows are fixed.
            var (top, bottom) = IdenticalBands(_prev, cur);
            if (_h - top - bottom < 24) top = bottom = 0;
            _top = top;
            _bottom = bottom;
            _firstPos = 0;
            for (int y = _top; y < _h - _bottom; y++) AddRow(_prev, _prevProfiles!, y, append: true);
            var (pos, ok) = Search(profiles, -Mid + MinOverlap, Mid - MinOverlap, 0);
            if (!ok || pos == 0)
            {
                // Changed in place (hover, animation) or unclear: start over from this frame.
                _rows.Clear();
                _rowCoarse.Clear();
                _rowFine.Clear();
                _rowTextured.Clear();
                _top = -1;
                _prev = _headerSource = _footerSource = cur;
                KeepProfiles(profiles);
                return;
            }
            _confirmedTop = _top;
            _confirmedBottom = _bottom;
            _headerSource = _prev;
            _pos = 0;
            _prevPlaced = true;
        }

        var prevProfiles = _prevProfiles;
        bool prevPlaced = _prevPlaced;
        _prev = cur;
        KeepProfiles(profiles);
        _prevPlaced = Place(cur, profiles, prevPlaced ? prevProfiles : null);
    }

    /// <summary>Makes p the previous frame's profiles; the old ones are reused for the next frame (no per-frame garbage).</summary>
    void KeepProfiles(Profiles p)
    {
        if (_prevProfiles != null && !ReferenceEquals(_prevProfiles, p)) _spareProfiles = _prevProfiles;
        _prevProfiles = p;
    }

    /// <summary>
    /// Rows at the top and bottom that are identical in both frames (at most 45% each). Plain rows in the page's own
    /// background colour at the inner edge of a band are given back to the content: a blank stretch of the page looks
    /// identical after a small scroll, but it moves with the page (a solid-coloured header bar does not).
    /// </summary>
    static (int top, int bottom) IdenticalBands(Frame a, Frame b)
    {
        int h = a.H, max = h * 45 / 100, top = 0, bottom = 0;
        while (top < max && a.Hash[top] == b.Hash[top]) top++;
        while (bottom < max && a.Hash[h - 1 - bottom] == b.Hash[h - 1 - bottom]) bottom++;
        if (BackgroundColor(a, b) is uint bg)
        {
            while (top > 0 && a.Uniform[top - 1] && RowColor(a, top - 1) == bg) top--;
            while (bottom > 0 && a.Uniform[h - bottom] && RowColor(a, h - bottom) == bg) bottom--;
        }
        return (top, bottom);
    }

    static uint RowColor(Frame f, int y) => BitConverter.ToUInt32(f.Px, y * f.Stride) | 0xFF000000;

    /// <summary>The most common colour of plain rows among the rows that moved (the page background), if any.</summary>
    static uint? BackgroundColor(Frame a, Frame b)
    {
        var counts = new Dictionary<uint, int>();
        for (int y = 0; y < a.H; y++)
        {
            if (a.Hash[y] == b.Hash[y]) continue;
            if (a.Uniform[y]) counts[RowColor(a, y)] = counts.GetValueOrDefault(RowColor(a, y)) + 1;
            if (b.Uniform[y]) counts[RowColor(b, y)] = counts.GetValueOrDefault(RowColor(b, y)) + 1;
        }
        return counts.Count == 0 ? null : counts.MaxBy(c => c.Value).Key;
    }

    /// <summary>
    /// Per row, the mean brightness of narrow cells (fine) and of wide cells (coarse), leaving out the scrollbar strip,
    /// plus whether the row has enough contrast to say anything (plain background rows don't).
    /// </summary>
    Profiles MakeProfiles(Frame f, Profiles? reuse = null)
    {
        var p = reuse ?? new Profiles { Coarse = new byte[f.H * CoarseBins], Fine = new byte[f.H * _fineBins], Textured = new bool[f.H] };
        for (int y = 0; y < f.H; y++)
        {
            int rowStart = y * f.Stride, min = 255, max = 0;
            for (int b = 0; b < _fineBins; b++)
            {
                int x0 = b * _usable / _fineBins, x1 = (b + 1) * _usable / _fineBins, sum = 0, n = 0;
                for (int x = x0; x < x1; x++)
                {
                    int i = rowStart + x * 4;
                    sum += (f.Px[i] + f.Px[i + 1] * 5 + f.Px[i + 2] * 2) >> 3; // B, G, R -> approximate brightness
                    n++;
                }
                int v = sum / Math.Max(1, n);
                p.Fine[y * _fineBins + b] = (byte)v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            for (int c = 0; c < CoarseBins; c++)
            {
                int sum = 0, from = c * _fineBins / CoarseBins, to = (c + 1) * _fineBins / CoarseBins;
                for (int b = from; b < to; b++) sum += p.Fine[y * _fineBins + b];
                p.Coarse[y * CoarseBins + c] = (byte)(sum / Math.Max(1, to - from));
            }
            p.Textured[y] = max - min > 10;
        }
        return p;
    }

    /// <summary>
    /// Mean profile difference between the frame's middle rows and a reference if the frame sits at pos. The
    /// reference is the captured rows (pos = content position), or, when `prev` is given, the previous frame
    /// (pos = how far the content moved since it).
    /// </summary>
    double Cost(Profiles p, bool fine, int pos, int rowStep, out int counted, Profiles? prev = null)
    {
        int bins = fine ? _fineBins : CoarseBins;
        var frameCells = fine ? p.Fine : p.Coarse;
        double sum = 0;
        counted = 0;
        if (prev != null)
        {
            // Frame row y shows what the previous frame showed at row y + pos.
            var prevCells = fine ? prev.Fine : prev.Coarse;
            int y0 = Math.Max(_top, _top - pos), y1 = Math.Min(_h - _bottom, _h - _bottom - pos);
            for (int y = y0; y < y1; y += rowStep)
            {
                int py = y + pos;
                if (!p.Textured[y] && !prev.Textured[py]) continue;
                int diff = Sad(frameCells.AsSpan(y * bins, bins), prevCells.AsSpan(py * bins, bins));
                sum += Math.Min(diff / (double)bins, 40);
                counted++;
            }
            return counted == 0 ? double.MaxValue : sum / counted;
        }
        var rowCells = fine ? _rowFine : _rowCoarse;
        int from = Math.Max(pos, _firstPos), to = Math.Min(pos + Mid, LastPos);
        for (int c = from; c < to; c += rowStep)
        {
            int y = _top + (c - pos);
            int ci = c - _firstPos;
            if (!p.Textured[y] && !_rowTextured[ci]) continue; // plain background tells nothing
            int diff = Sad(frameCells.AsSpan(y * bins, bins), rowCells[ci]);
            sum += Math.Min(diff / (double)bins, 40); // capped so a few changed rows (hover) can't dominate
            counted++;
        }
        return counted == 0 ? double.MaxValue : sum / counted;
    }

    /// <summary>Sum of absolute differences of two byte rows, 16 bytes per instruction where the CPU supports it.</summary>
    static int Sad(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int sum = 0, i = 0;
        if (Sse2.IsSupported)
        {
            ref byte ra = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(a);
            ref byte rb = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(b);
            var acc = Vector128<ulong>.Zero;
            for (; i + 16 <= a.Length; i += 16)
            {
                var va = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref ra, i));
                var vb = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref rb, i));
                acc = Sse2.Add(acc, Sse2.SumAbsoluteDifferences(va, vb).AsUInt64());
            }
            sum = (int)(acc.GetElement(0) + acc.GetElement(1));
        }
        for (; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    /// <summary>
    /// Best position in [lo, hi]: coarse pass over all offsets, fine pass around the best candidates. When several
    /// offsets fit about equally well (blank stretches, repeated rows), the one nearest the predicted position wins if
    /// it is close to it. A strict search (re-finding the place after losing it) only accepts a clear, well-textured fit.
    /// </summary>
    (int pos, bool ok) Search(Profiles p, int lo, int hi, int predicted, bool strict = false, Profiles? prev = null)
    {
        // Against the captured rows, positions are content rows; against the previous frame, they're movements, and
        // consecutive frames must share at least a third of the view (a sliver of overlap proves nothing).
        lo = Math.Max(lo, prev == null ? _firstPos - Mid + MinOverlap : -Mid * 2 / 3);
        hi = Math.Min(hi, prev == null ? LastPos - MinOverlap : Mid * 2 / 3);
        if (lo > hi) return (0, false);

        var coarse = new List<(double cost, int pos)>();
        int coarseRows = Math.Max(3, Mid / 120); // ~120 sampled rows per offset, whatever the area height
        for (int pos = lo; pos <= hi; pos += 2)
        {
            double c = Cost(p, false, pos, coarseRows, out int n, prev);
            if (n >= 4) coarse.Add((c, pos));
        }
        if (coarse.Count == 0) return (0, false);

        // Candidates: the best coarse offsets, at most one per neighbourhood, so look-alikes can't crowd out the real one.
        var candidates = new List<int>();
        foreach (var (_, pos) in coarse.OrderBy(c => c.cost))
        {
            if (candidates.Any(c => Math.Abs(c - pos) <= 4)) continue;
            candidates.Add(pos);
            if (candidates.Count == 16) break;
        }

        // In the prediction's neighbourhood every offset gets a fine look, so a plausible fit there can't be missed.
        if (!strict && predicted >= lo && predicted <= hi) candidates.Add(predicted);

        // Rows that actually carry texture in the compared overlap (sampled every Mid/400 rows).
        int rowStep = Math.Max(1, Mid / 400);
        int minRows = strict ? Math.Max(24, Mid / rowStep / 10) : 8;
        var fine = new Dictionary<int, double>();
        foreach (var center in candidates)
            for (int pos = Math.Max(lo, center - 2); pos <= Math.Min(hi, center + 2); pos++)
            {
                if (fine.ContainsKey(pos)) continue;
                double c = Cost(p, true, pos, rowStep, out int n, prev);
                if (n >= minRows) fine[pos] = c;
            }
        if (fine.Count == 0) return (0, false);

        var best = fine.MinBy(f => f.Value);
        if (best.Value > (strict ? MaxCost / 2 : MaxCost)) return (0, false);
        var far = fine.Where(f => Math.Abs(f.Key - best.Key) >= 6).Select(f => f.Value).DefaultIfEmpty(double.MaxValue).Min();
        // Clearly the best: better by a factor (and frame to frame, also by a real margin: two perfect fits, 0 and 0,
        // are a tie there, not a winner - inside identical rows the next frame fits at every row height).
        if (best.Value <= far * (strict ? Distinct * 0.75 : Distinct) && (prev == null || far - best.Value >= 1.0))
            return (best.Key, true);
        // No guessing in a strict search, nor frame to frame: a guess there would compound from frame to frame,
        // while a guess against the captured rows stays anchored to what was already placed.
        if (strict || prev != null) return (0, false);

        // Not clearly unique: blank stretches and repeated rows fit at several offsets, and fit them almost perfectly.
        // Among those near-perfect fits take the one nearest where the scrolling was heading, if it's near enough to
        // trust. A mediocre fit is a look-alike (e.g. after scrolling past a whole screen at once): don't guess.
        double good = Math.Min(Math.Max(best.Value + 1.0, best.Value * 1.3), NoiseLevel);
        var near = fine.Where(f => f.Value <= good).ToList();
        if (near.Count == 0) return (0, false);
        var chosen = near.MinBy(f => Math.Abs(f.Key - predicted));
        return Math.Abs(chosen.Key - predicted) <= Math.Max(40, Mid / 6) ? (chosen.Key, true) : (0, false);
    }

    /// <summary>Finds where the frame sits and adds the rows it newly reveals. False if it couldn't be placed.</summary>
    bool Place(Frame cur, Profiles profiles, Profiles? prevProfiles)
    {
        int mid = Mid;
        // First near the last position in the captured rows, predicted to carry on at the recent speed. If that fails,
        // how far the content moved since the previous frame (if that one was placed): frames a fraction of a second
        // apart agree even when the page changed after its rows were captured (a sidebar re-rendered, images loaded
        // late). Last, everywhere (after a jump) - but only a clear fit there, so look-alikes can't scramble the order.
        int predicted = _misses == 0 ? _pos + _velocity : _pos;
        var (pos, ok) = Search(profiles, _pos - mid, _pos + mid, predicted);
        if (!ok && prevProfiles != null)
        {
            var (moved, okMoved) = Search(profiles, -mid, mid, _velocity, prev: prevProfiles);
            if (okMoved) (pos, ok) = (_pos + moved, true);
        }
        if (!ok) (pos, ok) = Search(profiles, int.MinValue / 2, int.MaxValue / 2, _pos, strict: true);
        if (!ok)
        {
            if (++_misses >= LostAfter) Report("Lost track – scrolled too far at once. Scroll back a little.", true);
            return false;
        }
        _velocity = _misses == 0 ? Math.Clamp(pos - _pos, -mid / 2, mid / 2) : 0;
        _misses = 0;
        _pos = pos;

        _footerSource = cur;
        _footerPos = pos;
        // A band row that ever changes was really content that happened to look the same in the first two frames.
        var hs = _headerSource!;
        int k = 0;
        while (k < _confirmedTop && cur.Hash[k] == hs.Hash[k]) k++;
        _confirmedTop = k;
        k = 0;
        while (k < _confirmedBottom && cur.Hash[_h - 1 - k] == hs.Hash[_h - 1 - k]) k++;
        _confirmedBottom = k;

        bool room() => _top + _rows.Count + _bottom < _maxHeight;
        // Newly revealed rows below...
        for (int c = LastPos; c < pos + mid && room(); c++) AddRow(cur, profiles, _top + (c - pos), append: true);
        // ...and above, if the user scrolled up past the starting point.
        for (int c = _firstPos - 1; c >= pos && room(); c--) AddRow(cur, profiles, _top + (c - pos), append: false);

        if (!room() && !_limitHit)
        {
            _limitHit = true;
            Report($"Height limit reached ({_maxHeight:N0} px). Press Enter to finish.", true);
            return true;
        }
        if (!_limitHit) Report($"{_w} × {CapturedHeight:N0} px captured – keep scrolling, Enter / End when done.", false);
        return true;
    }

    void AddRow(Frame f, Profiles p, int y, bool append)
    {
        var row = new byte[f.Stride];
        Buffer.BlockCopy(f.Px, y * f.Stride, row, 0, f.Stride);
        var coarse = new byte[CoarseBins];
        Buffer.BlockCopy(p.Coarse, y * CoarseBins, coarse, 0, CoarseBins);
        var fine = new byte[_fineBins];
        Buffer.BlockCopy(p.Fine, y * _fineBins, fine, 0, _fineBins);
        if (append)
        {
            _rows.Add(row);
            _rowCoarse.Add(coarse);
            _rowFine.Add(fine);
            _rowTextured.Add(p.Textured[y]);
        }
        else
        {
            _rows.Insert(0, row);
            _rowCoarse.Insert(0, coarse);
            _rowFine.Insert(0, fine);
            _rowTextured.Insert(0, p.Textured[y]);
            _firstPos--;
        }
    }

    void Report(string text, bool warn) => Progress?.Invoke(text, warn);

    /// <summary>Header (fixed top) + everything captured + footer (fixed bottom, from the latest frame).</summary>
    public BitmapSource Compose()
    {
        // Rows are written straight into the bitmap's own buffer, so the tall image exists only once in memory.
        // (Frames come from RegionGrabber with opaque alpha already.)
        int stride = _w * 4;
        WriteableBitmap bmp;
        if (_top < 0)
        {
            // Never scrolled: just the area as it looks now.
            var f = _footerSource ?? _prev!;
            bmp = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, _w, _h), f.Px, stride, 0);
        }
        else
        {
            // Header: truly fixed rows, then rows of the first frame that sit above everything captured.
            // Rows the user later scrolled back over come from the captured content instead.
            var head = _headerSource!;
            var foot = _footerSource!;
            int headEnd = Math.Max(_confirmedTop, Math.Min(_top, _top + _firstPos));
            // Footer: rows of the last frame below everything captured, then the truly fixed rows.
            int tailStart = Math.Max(_h - _bottom, _top + (LastPos - _footerPos));
            int tailEnd = _h - _confirmedBottom;
            int tail = Math.Max(0, tailEnd - tailStart);
            int height = headEnd + _rows.Count + tail + _confirmedBottom;
            bmp = new WriteableBitmap(_w, height, 96, 96, PixelFormats.Bgra32, null);
            bmp.Lock();
            IntPtr dst = bmp.BackBuffer;
            int dstStride = bmp.BackBufferStride, y = 0;
            void Copy(byte[] src, int srcRow, int rows)
            {
                for (int r = 0; r < rows; r++, y++)
                    System.Runtime.InteropServices.Marshal.Copy(src, (srcRow + r) * stride, dst + y * dstStride, stride);
            }
            Copy(head.Px, 0, headEnd);
            foreach (var row in _rows) Copy(row, 0, 1);
            if (tail > 0) Copy(foot.Px, tailStart, tail);
            Copy(foot.Px, _h - _confirmedBottom, _confirmedBottom);
            bmp.AddDirtyRect(new System.Windows.Int32Rect(0, 0, _w, height));
            bmp.Unlock();
        }
        bmp.Freeze();
        return bmp;
    }
}

/// <summary>A thin click-through frame drawn just outside the capture area.</summary>
internal sealed class ScrollBorderForm : WF.Form
{
    const int Thickness = 3;
    readonly SD.Color _accent;

    public ScrollBorderForm(SD.Rectangle area, SD.Color accent)
    {
        _accent = accent;
        FormBorderStyle = WF.FormBorderStyle.None;
        StartPosition = WF.FormStartPosition.Manual;
        AutoScaleMode = WF.AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = SD.Color.Magenta;
        TransparencyKey = SD.Color.Magenta;
        var r = area;
        r.Inflate(Thickness, Thickness);
        Bounds = r;
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x20 | 0x80 | 0x08000000; // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void WndProc(ref WF.Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        using var b = new SD.SolidBrush(_accent);
        int w = ClientSize.Width, h = ClientSize.Height;
        e.Graphics.FillRectangle(b, 0, 0, w, Thickness);
        e.Graphics.FillRectangle(b, 0, h - Thickness, w, Thickness);
        e.Graphics.FillRectangle(b, 0, 0, Thickness, h);
        e.Graphics.FillRectangle(b, w - Thickness, 0, Thickness, h);
    }
}

/// <summary>Small always-on-top panel with live status and Done / Cancel. Never takes focus from the scrolled app.</summary>
internal sealed class ScrollPanelForm : WF.Form
{
    readonly SD.Rectangle _area;
    readonly SD.Color _accent;
    readonly WF.Label _status = new() { AutoSize = false, ForeColor = SD.Color.White, TextAlign = SD.ContentAlignment.MiddleLeft };
    readonly WF.Button _done = new() { Text = "Done (Enter)", FlatStyle = WF.FlatStyle.Flat, ForeColor = SD.Color.White, Cursor = WF.Cursors.Hand };
    readonly WF.Button _cancel = new() { Text = "Cancel (Esc)", FlatStyle = WF.FlatStyle.Flat, ForeColor = SD.Color.White, Cursor = WF.Cursors.Hand };

    public event Action? Done, Cancelled;

    public ScrollPanelForm(SD.Rectangle area, SD.Color accent)
    {
        _area = area;
        _accent = accent;
        FormBorderStyle = WF.FormBorderStyle.None;
        StartPosition = WF.FormStartPosition.Manual;
        AutoScaleMode = WF.AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = SD.Color.FromArgb(32, 32, 32);
        _done.BackColor = accent;
        _done.FlatAppearance.BorderSize = 0;
        _cancel.BackColor = SD.Color.FromArgb(70, 70, 70);
        _cancel.FlatAppearance.BorderSize = 0;
        _done.Click += (_, _) => Done?.Invoke();
        _cancel.Click += (_, _) => Cancelled?.Invoke();
        Controls.Add(_status);
        Controls.Add(_done);
        Controls.Add(_cancel);
        _status.Text = "Starting…";
    }

    protected override bool ShowWithoutActivation => true;

    protected override WF.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); // never appears in the capture
    }

    protected override void WndProc(ref WF.Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float s = DeviceDpi / 96f;
        int pad = (int)(10 * s), bw = (int)(110 * s), bh = (int)(30 * s), sw = (int)(430 * s);
        var font = new SD.Font("Segoe UI", 13 * s, SD.GraphicsUnit.Pixel);
        _status.Font = font;
        _done.Font = new SD.Font("Segoe UI Semibold", 13 * s, SD.GraphicsUnit.Pixel);
        _cancel.Font = font;
        _status.SetBounds(pad, pad, sw, bh);
        _done.SetBounds(pad * 2 + sw, pad, bw, bh);
        _cancel.SetBounds(pad * 3 + sw + bw, pad, bw, bh);
        var size = new SD.Size(pad * 4 + sw + bw * 2, pad * 2 + bh);

        // Below the area if there's room, else above, else inside its bottom-right corner (excluded from capture anyway).
        var work = WF.Screen.FromRectangle(_area).WorkingArea;
        int x = Math.Clamp(_area.Left, work.Left, Math.Max(work.Left, work.Right - size.Width));
        int gap = (int)(8 * s);
        int y = _area.Bottom + gap + size.Height <= work.Bottom ? _area.Bottom + gap
              : _area.Top - gap - size.Height >= work.Top ? _area.Top - gap - size.Height
              : _area.Bottom - size.Height - gap * 2;
        if (y == _area.Bottom - size.Height - gap * 2) x = Math.Max(work.Left, _area.Right - size.Width - gap * 2);
        Bounds = new SD.Rectangle(x, y, size.Width, size.Height);
    }

    public void SetStatus(string text, bool warn)
    {
        _status.Text = text;
        _status.ForeColor = warn ? SD.Color.FromArgb(255, 183, 77) : SD.Color.White;
    }
}
