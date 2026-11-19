using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PoshTerm.Config;

namespace PoshTerm.Terminal;

// Draws a TerminalBuffer. Owns the buffer, the VT parser, selection, scrolling and search.
// All buffer access happens on the UI thread; pty output is queued from the reader thread.
public sealed partial class TerminalView : FrameworkElement
{
    public TerminalBuffer Buffer { get; private set; }
    public VtParser Parser { get; private set; }

    private Look _look = Look.Defaults();
    private ColorScheme _scheme = BuiltInSchemes.All[0];
    private Brush[] _palette = new Brush[256];
    private Color[] _paletteColors = new Color[256];
    private readonly Dictionary<uint, Brush> _brushCache = new();
    private readonly Dictionary<(int, bool, bool), Typeface> _typefaces = new();
    private FontFamily _family = new("Consolas");
    private double _fontSize = 14;
    private double _cellW = 8, _cellH = 16, _baseline = 12, _ftBaseline = 12;
    private double _padding = 8;
    public double FontSizeAdjust { get; set; }          // per-pane zoom, in points

    // view state: 0 = bottom (live); N = scrolled up N lines into history
    private int _viewOffset;

    private bool _cursorOn = true;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(530) };
    private long _lastRevision = -1;
    private bool _dirty = true;

    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private int _drainScheduled;

    public int Cols => Buffer.Cols;
    public int Rows => Buffer.Rows;
    public double CellWidth => _cellW;
    public double CellHeight => _cellH;
    public bool IsAtBottom => _viewOffset == 0;

    public event Action<int, int>? GridResized;          // cols, rows
    public event Action<byte[]>? SendToChild;            // bytes for the pty
    public event Action<string>? TitleChanged;
    public event Action? BellRang;
    public event Action<int, int>? ProgressChanged;
    public event Action<string>? CwdChanged;
    public event Action? OutputActivity;

    public TerminalView()
    {
        Buffer = new TerminalBuffer(80, 24);
        Parser = new VtParser(Buffer);
        WireParser();
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        _blink.Tick += (_, _) => { _cursorOn = !_cursorOn; Invalidate(); };
        Loaded += (_, _) => { if (_look.CursorBlink == true) _blink.Start(); CompositionTarget.Rendering += OnFrame; };
        Unloaded += (_, _) => { _blink.Stop(); CompositionTarget.Rendering -= OnFrame; };
        GotKeyboardFocus += (_, _) => { _cursorOn = true; if (Parser.FocusReporting) SendToChild?.Invoke("\x1b[I"u8.ToArray()); Invalidate(); };
        LostKeyboardFocus += (_, _) => { if (Parser.FocusReporting) SendToChild?.Invoke("\x1b[O"u8.ToArray()); Invalidate(); };
        ApplyLook(_look, _scheme);
    }

    private void WireParser()
    {
        Parser.TitleChanged += t => TitleChanged?.Invoke(t);
        Parser.Bell += () => BellRang?.Invoke();
        Parser.Reply += r => SendToChild?.Invoke(System.Text.Encoding.UTF8.GetBytes(r));
        Parser.Progress += (s, p) => ProgressChanged?.Invoke(s, p);
        Parser.WorkingDirectory += d => CwdChanged?.Invoke(d);
        Parser.ClipboardSet += t => { try { Clipboard.SetText(t); } catch { } };
    }

    // ---------- output from the child ----------
    // Thread-safe: called from the pty reader thread.
    public void EnqueueOutput(byte[] data, int length)
    {
        var copy = new byte[length];
        Array.Copy(data, copy, length);
        _incoming.Enqueue(copy);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Send, Drain);
    }

    private void Drain()
    {
        // bounded slice so a flood of output can't starve input; the rest is rescheduled
        int budget = 256 * 1024;
        int before = Buffer.ScrollbackCount;
        long evictedBefore = Buffer.Evicted;
        while (budget > 0 && _incoming.TryDequeue(out var chunk))
        {
            try { Parser.Feed(chunk); }
            catch (Exception ex) { App.LogError(ex); }   // a bad escape sequence must never take the UI down
            budget -= chunk.Length;
        }
        // lines that dropped off a full scrollback still count as growth, and shift every view index
        int evicted = (int)Math.Min(int.MaxValue, Buffer.Evicted - evictedBefore);
        if (_viewOffset > 0)
        {
            int grew = Buffer.ScrollbackCount - before + evicted;
            if (grew > 0) _viewOffset = Math.Min(_viewOffset + grew, Buffer.ScrollbackCount);
        }
        if (evicted > 0) ShiftViewRows(evicted);
        if (Buffer.ScrollbackCount < before)
        {
            // scrollback was cleared (ED 3, e.g. cls / Clear-Host): old view indexes point at nothing now
            _viewOffset = Math.Min(_viewOffset, Buffer.ScrollbackCount);
            ClearSelection(); ClearFind();
        }
        Interlocked.Exchange(ref _drainScheduled, 0);
        if (!_incoming.IsEmpty && Interlocked.Exchange(ref _drainScheduled, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, Drain);
        _cursorOn = true;
        OutputActivity?.Invoke();
        Invalidate();
    }

    // ---------- appearance ----------
    public void ApplyLook(Look look, ColorScheme scheme)
    {
        _look = look;
        _scheme = scheme;
        _padding = look.Padding ?? 8;
        BuildPalette();
        _family = new FontFamily(string.IsNullOrWhiteSpace(look.FontFamily) ? "Consolas" : look.FontFamily);
        _fontSize = Math.Max(6, (look.FontSize ?? 14) + FontSizeAdjust);
        _typefaces.Clear();
        MeasureCell();
        Buffer.MaxScrollback = Math.Max(100, look.Scrollback ?? 10000);
        if (look.CursorBlink == true && IsLoaded) _blink.Start(); else { _blink.Stop(); _cursorOn = true; }
        UpdateGrid();
        Invalidate();
    }

    private static Color Hex(string s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s); } catch { return Colors.Magenta; }
    }

    private Brush BrushFor(Color c)
    {
        uint key = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
        if (!_brushCache.TryGetValue(key, out var b)) { b = new SolidColorBrush(c); b.Freeze(); _brushCache[key] = b; }
        return b;
    }

    private void BuildPalette()
    {
        _brushCache.Clear();
        var p16 = _scheme.Palette16();
        for (int i = 0; i < 16; i++) _paletteColors[i] = Hex(p16[i]);
        for (int i = 16; i < 232; i++)
        {
            int n = i - 16, r = n / 36, g = n / 6 % 6, b = n % 6;
            byte Lv(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            _paletteColors[i] = Color.FromRgb(Lv(r), Lv(g), Lv(b));
        }
        for (int i = 232; i < 256; i++) { byte v = (byte)(8 + (i - 232) * 10); _paletteColors[i] = Color.FromRgb(v, v, v); }
        for (int i = 0; i < 256; i++) _palette[i] = BrushFor(_paletteColors[i]);
    }

    private Typeface Face(bool bold, bool italic)
    {
        int w = bold ? 700 : ParseWeight(_look.FontWeight);
        var key = (w, bold, italic);
        if (!_typefaces.TryGetValue(key, out var tf))
        {
            tf = new Typeface(_family, italic ? FontStyles.Italic : FontStyles.Normal, FontWeight.FromOpenTypeWeight(w), FontStretches.Normal);
            _typefaces[key] = tf;
        }
        return tf;
    }

    private static int ParseWeight(string? s) => (s ?? "normal").ToLowerInvariant() switch
    {
        "normal" or "regular" => 400, "bold" => 700, "light" => 300, "semibold" => 600, "thin" => 100,
        var n when int.TryParse(n, out var v) => Math.Clamp(v, 1, 999),
        _ => 400,
    };

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private void MeasureCell()
    {
        var ft = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face(false, false), _fontSize, Brushes.White, Dpi);
        double w = ft.WidthIncludingTrailingWhitespace / 10.0 + (_look.LetterSpacing ?? 0);
        double dpi = Dpi;
        _cellW = Math.Max(1, Math.Round(w * dpi) / dpi);
        _cellH = Math.Max(1, Math.Ceiling(ft.Height * (_look.LineHeight ?? 1.0) * dpi) / dpi);
        _baseline = (_cellH - ft.Height) / 2;
        _ftBaseline = ft.Baseline;
    }

    // ---------- layout ----------
    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); UpdateGrid(); }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); MeasureCell(); UpdateGrid(); Invalidate(); }

    public void UpdateGrid()
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        int cols = Math.Max(2, (int)((ActualWidth - _padding * 2) / _cellW));
        int rows = Math.Max(1, (int)((ActualHeight - _padding * 2) / _cellH));
        if (cols == Buffer.Cols && rows == Buffer.Rows) return;
        int before = Buffer.ScrollbackCount;
        Buffer.Resize(cols, rows, TermColor.Default);
        _viewOffset = Math.Min(_viewOffset, Buffer.ScrollbackCount);
        _ = before;
        ClearSelection();
        GridResized?.Invoke(cols, rows);
        Invalidate();
    }

    public void Invalidate() => _dirty = true;

    // Repaint at most once per frame, and only if something changed.
    private void OnFrame(object? s, EventArgs e)
    {
        if (!_dirty && _lastRevision == Buffer.Revision) return;
        _dirty = false;
        _lastRevision = Buffer.Revision;
        InvalidateVisual();
    }

    // ---------- scrolling ----------
    public void ScrollLines(int n)
    {
        _viewOffset = Math.Clamp(_viewOffset + n, 0, Buffer.ScrollbackCount);
        Invalidate();
    }
    public void ScrollPages(int n) => ScrollLines(n * Math.Max(1, Buffer.Rows - 1));
    public void ScrollToTop() { _viewOffset = Buffer.ScrollbackCount; Invalidate(); }
    public void ScrollToBottom() { _viewOffset = 0; Invalidate(); }

    // The view line at screen row y (0..Rows-1), accounting for scroll offset.
    private int ViewIndexOf(int y) => Buffer.ScrollbackCount - _viewOffset + y;

    // ---------- rendering ----------
    private Color ResolveFg(TermColor c, CellFlags f)
    {
        if (c.Kind == 0) return Hex(_scheme.Foreground);
        if (c.Kind == 1) return _paletteColors[c.Index];
        return Color.FromRgb(c.R, c.G, c.B);
    }
    private Color ResolveBg(TermColor c)
    {
        if (c.Kind == 0) return Hex(_scheme.Background);
        if (c.Kind == 1) return _paletteColors[c.Index];
        return Color.FromRgb(c.R, c.G, c.B);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // transparent hit-testable surface so the mouse works over the empty area
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Buffer.Cols < 1) return;

        double dpi = Dpi;
        var fgDefault = Hex(_scheme.Foreground);
        var bgDefault = Hex(_scheme.Background);
        var selBrush = BrushFor(SelectionColor());
        var matchBrush = BrushFor(Color.FromArgb(110, 255, 210, 60));
        var curMatchBrush = BrushFor(Color.FromArgb(190, 255, 150, 0));
        bool reverse = Parser.ReverseVideo;
        var sb = new System.Text.StringBuilder();

        for (int y = 0; y < Buffer.Rows; y++)
        {
            int vi = ViewIndexOf(y);
            var line = Buffer.ViewLine(vi);
            if (line == null) continue;
            double top = _padding + y * _cellH;
            var cells = line.Cells;
            int n = Math.Min(cells.Length, Buffer.Cols);

            // pass 1: backgrounds (merge neighbouring cells of the same color into one rect)
            int x = 0;
            while (x < n)
            {
                var (fg, bg, _) = Colors2(cells[x], fgDefault, bgDefault, reverse);
                bool hasBg = bg.HasValue;
                int start = x;
                while (x < n && Colors2(cells[x], fgDefault, bgDefault, reverse).bg == bg) x++;
                if (hasBg) dc.DrawRectangle(BrushFor(bg!.Value), null, new Rect(_padding + start * _cellW, top, (x - start) * _cellW + 0.5, _cellH));
            }

            // search highlights and selection sit above backgrounds, below text
            DrawMatchesForRow(dc, vi, top, matchBrush, curMatchBrush);
            DrawSelectionForRow(dc, vi, top, Buffer.Cols, selBrush);   // full width, even over a trimmed scrollback line

            // pass 2: text. ASCII runs with identical attributes become one FormattedText;
            // everything else is positioned per cell so wide glyphs and fallback fonts can't drift.
            x = 0;
            while (x < n)
            {
                var cell = cells[x];
                if ((cell.Flags & CellFlags.WideRight) != 0 || cell.Text == null || cell.Text == " " || (cell.Flags & CellFlags.Invisible) != 0) { x++; continue; }
                var (fg, _, _) = Colors2(cell, fgDefault, bgDefault, reverse);
                var key = cell.Flags & ~(CellFlags.WideLeft | CellFlags.WideRight | CellFlags.Inverse);
                bool simple = cell.Text.Length == 1 && cell.Text[0] >= 0x20 && cell.Text[0] < 0x7f;
                int start = x;
                sb.Clear();
                if (simple)
                {
                    while (x < n)
                    {
                        var c2 = cells[x];
                        bool s2 = c2.Text is { Length: 1 } t && t[0] >= 0x20 && t[0] < 0x7f;
                        if (!s2 && !(c2.Text == null && (c2.Flags & CellFlags.WideRight) == 0)) break;
                        if ((c2.Flags & ~(CellFlags.WideLeft | CellFlags.WideRight | CellFlags.Inverse)) != key && c2.Text != null) break;
                        if (c2.Text != null && Colors2(c2, fgDefault, bgDefault, reverse).fg != fg) break;
                        if ((c2.Flags & CellFlags.Invisible) != 0) break;
                        sb.Append(c2.Text ?? " ");
                        x++;
                    }
                    // trailing spaces add nothing visible
                    DrawText(dc, sb.ToString().TrimEnd(), fg, cell.Flags, _padding + start * _cellW, top, dpi);
                }
                else
                {
                    DrawText(dc, cell.Text, fg, cell.Flags, _padding + x * _cellW, top, dpi);
                    x++;
                }
                if ((cell.Flags & CellFlags.Overline) != 0 && simple)
                    dc.DrawRectangle(BrushFor(fg), null, new Rect(_padding + start * _cellW, top, (x - start) * _cellW, 1));
            }

            DrawLinkUnderline(dc, vi, top);
        }

        DrawCursor(dc, fgDefault);
    }

    private (Color fg, Color? bg, bool inv) Colors2(Cell c, Color fgDef, Color bgDef, bool reverseAll)
    {
        bool inv = ((c.Flags & CellFlags.Inverse) != 0) ^ reverseAll;
        Color fg = c.Fg.Kind == 0 ? fgDef : ResolveFg(c.Fg, c.Flags);
        Color? bg = c.Bg.Kind == 0 ? null : ResolveBg(c.Bg);
        if ((c.Flags & CellFlags.Faint) != 0) fg = Color.FromArgb(150, fg.R, fg.G, fg.B);
        if (inv) { var nbg = fg; var nfg = bg ?? bgDef; fg = Color.FromRgb(nfg.R, nfg.G, nfg.B); bg = Color.FromRgb(nbg.R, nbg.G, nbg.B); }
        return (fg, bg, inv);
    }

    // ASCII text is drawn as a glyph run whose every glyph advances exactly one cell, so runs can never
    // drift off the grid. Anything the primary font can't map falls back to FormattedText.
    private bool TryGlyphRun(DrawingContext dc, string text, Color fg, CellFlags flags, double x, double top, double dpi)
    {
        var face = Face((flags & CellFlags.Bold) != 0, (flags & CellFlags.Italic) != 0);
        if (!face.TryGetGlyphTypeface(out var gt)) return false;
        var idx = new ushort[text.Length];
        var adv = new double[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (!gt.CharacterToGlyphMap.TryGetValue(text[i], out var g)) return false;
            idx[i] = g; adv[i] = _cellW;
        }
        double baselineY = top + _baseline + _ftBaseline;
        var brush = BrushFor(fg);
        var run = new GlyphRun(gt, 0, false, _fontSize, (float)dpi, idx, new Point(x, baselineY), adv, null, null, null, null, null, null);
        dc.DrawGlyphRun(brush, run);
        double w = text.Length * _cellW;
        if ((flags & (CellFlags.Underline | CellFlags.DoubleUnderline)) != 0)
        {
            dc.DrawRectangle(brush, null, new Rect(x, baselineY + 1.5, w, 1));
            if ((flags & CellFlags.DoubleUnderline) != 0) dc.DrawRectangle(brush, null, new Rect(x, baselineY + 3.5, w, 1));
        }
        if ((flags & CellFlags.Strikethrough) != 0) dc.DrawRectangle(brush, null, new Rect(x, baselineY - _fontSize * 0.3, w, 1));
        return true;
    }

    private void DrawText(DrawingContext dc, string text, Color fg, CellFlags flags, double x, double top, double dpi)
    {
        if (text.Length == 0) return;
        if (text.All(c => c >= 0x20 && c < 0x7f) && TryGlyphRun(dc, text, fg, flags, x, top, dpi)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Face((flags & CellFlags.Bold) != 0, (flags & CellFlags.Italic) != 0), _fontSize, BrushFor(fg), dpi);
        var deco = new TextDecorationCollection();
        if ((flags & (CellFlags.Underline | CellFlags.DoubleUnderline)) != 0) foreach (var d in TextDecorations.Underline) deco.Add(d);
        if ((flags & CellFlags.Strikethrough) != 0) foreach (var d in TextDecorations.Strikethrough) deco.Add(d);
        if (deco.Count > 0) ft.SetTextDecorations(deco);
        dc.DrawText(ft, new Point(x, top + _baseline));
    }

    private void DrawCursor(DrawingContext dc, Color fgDefault)
    {
        if (!Buffer.CursorVisible || _viewOffset > 0) return;
        double x = _padding + Buffer.CursorX * _cellW, y = _padding + Buffer.CursorY * _cellH;
        var cur = _scheme.CursorColor is { } cc ? Hex(cc) : fgDefault;
        var brush = BrushFor(cur);
        if (!IsKeyboardFocused)
        {
            dc.DrawRectangle(null, new Pen(brush, 1), new Rect(x + 0.5, y + 0.5, _cellW - 1, _cellH - 1));
            return;
        }
        if (_look.CursorBlink == true && !_cursorOn) return;
        string shape = Parser.CursorShape switch
        {
            1 or 2 => "block", 3 or 4 => "underline", 5 or 6 => "bar", _ => (_look.CursorStyle ?? "bar"),
        };
        switch (shape)
        {
            case "block":
                {
                    dc.DrawRectangle(Brush2(brush, 0.8), null, new Rect(x, y, _cellW, _cellH));
                    var cell = Buffer.LineAt(Math.Min(Buffer.CursorY, Buffer.Rows - 1)).Cells[Math.Min(Buffer.CursorX, Buffer.Cols - 1)];
                    if (cell.Text is { } t && t != " " && (cell.Flags & CellFlags.WideRight) == 0)
                        DrawText(dc, t, Hex(_scheme.Background), cell.Flags, x, y, Dpi);
                    break;
                }
            case "underline": dc.DrawRectangle(brush, null, new Rect(x, y + _cellH - 2, _cellW, 2)); break;
            default: dc.DrawRectangle(brush, null, new Rect(x, y, 2, _cellH)); break;
        }
    }

    private Brush Brush2(Brush b, double opacity)
    {
        if (b is SolidColorBrush s) { var c = s.Color; return BrushFor(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B)); }
        return b;
    }

    private Color SelectionColor()
    {
        if (_scheme.SelectionBackground is { } s) { var c = Hex(s); return Color.FromArgb(170, c.R, c.G, c.B); }
        var f = Hex(_scheme.Foreground);
        return Color.FromArgb(80, f.R, f.G, f.B);
    }
}
