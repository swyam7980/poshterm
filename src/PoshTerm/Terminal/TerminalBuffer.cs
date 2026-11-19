using System.Text;

namespace PoshTerm.Terminal;

// A line of cells plus a wrapped flag (so reflow and selection can rejoin soft-wrapped lines).
public sealed class Line
{
    public Cell[] Cells;
    public bool Wrapped;
    public Line(int cols) { Cells = new Cell[cols]; }

    public void Resize(int cols, TermColor bg)
    {
        if (cols == Cells.Length) return;
        var n = new Cell[cols];
        Array.Copy(Cells, n, Math.Min(cols, Cells.Length));
        for (int i = Cells.Length; i < cols; i++) n[i].Clear(bg);
        Cells = n;
    }

    // Drop trailing blank cells (keeps at least one). Used for lines entering scrollback, which are never
    // written again: most output is far narrower than the window, and a full 10k-line scrollback of
    // full-width rows in a maximized window would otherwise cost ~50 MB per pane.
    public Line Compact()
    {
        int n = Cells.Length;
        while (n > 1 && Cells[n - 1].IsBlank) n--;
        if (n < Cells.Length) Array.Resize(ref Cells, n);
        return this;
    }
}

// The screen grid: an active area of Rows x Cols, a scrollback ring above it, cursor, scroll
// region and an alternate screen (used by full-screen apps like vim). The VtParser drives it.
public sealed class TerminalBuffer
{
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorX, CursorY;           // 0-based, within the active area
    public int ScrollTop, ScrollBottom;    // active-area margins (inclusive)
    public bool CursorVisible = true;
    public bool AutoWrap = true;
    public bool OriginMode;
    public bool Alternate { get; private set; }

    public readonly List<Line> Scrollback = new();
    public int MaxScrollback;

    private List<Line> _lines;             // active area, Rows entries
    private List<Line> _mainLines = null!;
    private List<Line> _altLines = null!;
    private bool _pendingWrap;             // cursor past the last column, waiting to wrap

    public long Revision { get; private set; }  // bumped on any visible change, for repaint throttling

    public TerminalBuffer(int cols, int rows, int maxScrollback = 10000)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        MaxScrollback = maxScrollback;
        _mainLines = NewLines(Rows);
        _altLines = NewLines(Rows);
        _lines = _mainLines;
        ScrollBottom = Rows - 1;
    }

    private List<Line> NewLines(int n)
    {
        var l = new List<Line>(n);
        for (int i = 0; i < n; i++) l.Add(new Line(Cols));
        return l;
    }

    public Line LineAt(int y) => _lines[y];
    public void Touch() => Revision++;

    // Total scrollback rows currently retained.
    public int ScrollbackCount => Scrollback.Count;

    // Lines dropped off the top of a full scrollback, ever. View indexes (selection, search hits, scroll
    // position) shift down by one for each, so the view uses this to keep them pinned to their text.
    public long Evicted { get; private set; }

    private void TrimScrollback()
    {
        int extra = Scrollback.Count - MaxScrollback;
        if (extra <= 0) return;
        Scrollback.RemoveRange(0, extra);
        Evicted += extra;
    }

    // A view row index (0 = top of scrollback) mapped to a line, for rendering with scroll offset.
    // Scrollback lines may be narrower than Cols (trailing blanks are trimmed); readers must use Cells.Length.
    public Line? ViewLine(int index)
    {
        if (index < 0) return null;
        if (index < Scrollback.Count) return Scrollback[index];
        int a = index - Scrollback.Count;
        return a >= 0 && a < _lines.Count ? _lines[a] : null;
    }

    public void SetCursor(int x, int y)
    {
        int top = OriginMode ? ScrollTop : 0;
        int bottom = OriginMode ? ScrollBottom : Rows - 1;
        CursorY = Math.Clamp(y + (OriginMode ? ScrollTop : 0), top, bottom);
        CursorX = Math.Clamp(x, 0, Cols - 1);
        _pendingWrap = false;
    }

    public void MoveCursor(int dx, int dy)
    {
        CursorX = Math.Clamp(CursorX + dx, 0, Cols - 1);
        int top = OriginMode ? ScrollTop : 0, bottom = OriginMode ? ScrollBottom : Rows - 1;
        CursorY = Math.Clamp(CursorY + dy, top, bottom);
        _pendingWrap = false;
    }

    public void CarriageReturn() { CursorX = 0; _pendingWrap = false; }

    public void LineFeed()
    {
        _pendingWrap = false;
        if (CursorY == ScrollBottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    public void ReverseLineFeed()
    {
        if (CursorY == ScrollTop) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    // Write a printable grapheme at the cursor, handling wrap and double-width.
    public void Put(string grapheme, int width, TermColor fg, TermColor bg, CellFlags flags)
    {
        if (_pendingWrap && AutoWrap)
        {
            _lines[CursorY].Wrapped = true;
            CarriageReturn();
            LineFeed();
        }
        if (width == 2 && CursorX == Cols - 1)
        {
            // no room for a wide glyph at the edge: pad and wrap
            _lines[CursorY].Cells[CursorX].Clear(bg);
            if (AutoWrap) { _lines[CursorY].Wrapped = true; CarriageReturn(); LineFeed(); }
        }
        var line = _lines[CursorY];
        ref var cell = ref line.Cells[CursorX];
        cell.Text = grapheme;
        cell.Fg = fg; cell.Bg = bg;
        cell.Flags = flags | (width == 2 ? CellFlags.WideLeft : CellFlags.None);
        if (width == 2 && CursorX + 1 < Cols)
        {
            ref var right = ref line.Cells[CursorX + 1];
            right.Text = null; right.Fg = fg; right.Bg = bg; right.Flags = flags | CellFlags.WideRight;
        }
        int adv = Math.Max(1, width);
        if (CursorX + adv >= Cols) { CursorX = Cols - 1; _pendingWrap = true; }
        else CursorX += adv;
    }

    public void ScrollUp(int n)
    {
        n = Math.Min(n, ScrollBottom - ScrollTop + 1);
        bool wholeScreen = ScrollTop == 0 && ScrollBottom == Rows - 1;
        for (int i = 0; i < n; i++)
        {
            var top = _lines[ScrollTop];
            if (wholeScreen && !Alternate)
            {
                Scrollback.Add(top.Compact());
                TrimScrollback();
                _lines.RemoveAt(ScrollTop);
                _lines.Insert(ScrollBottom, new Line(Cols));
            }
            else
            {
                _lines.RemoveAt(ScrollTop);
                _lines.Insert(ScrollBottom, new Line(Cols));
            }
        }
        Touch();
    }

    public void ScrollDown(int n)
    {
        n = Math.Min(n, ScrollBottom - ScrollTop + 1);
        for (int i = 0; i < n; i++)
        {
            _lines.RemoveAt(ScrollBottom);
            _lines.Insert(ScrollTop, new Line(Cols));
        }
        Touch();
    }

    // Erase helpers (ED/EL). mode: 0 = to end, 1 = to start, 2 = all.
    public void EraseInLine(int mode, TermColor bg)
    {
        var cells = _lines[CursorY].Cells;
        int from = mode == 1 ? 0 : CursorX;
        int to = mode == 0 ? Cols - 1 : CursorX;
        if (mode == 2) { from = 0; to = Cols - 1; }
        for (int x = from; x <= to && x < Cols; x++) cells[x].Clear(bg);
        if (mode == 2 || (mode == 0 && from == 0)) _lines[CursorY].Wrapped = false;
        Touch();
    }

    public void EraseInDisplay(int mode, TermColor bg)
    {
        if (mode == 0) { EraseInLine(0, bg); for (int y = CursorY + 1; y < Rows; y++) ClearLine(y, bg); }
        else if (mode == 1) { EraseInLine(1, bg); for (int y = 0; y < CursorY; y++) ClearLine(y, bg); }
        else if (mode == 2 || mode == 3)
        {
            for (int y = 0; y < Rows; y++) ClearLine(y, bg);
            if (mode == 3) Scrollback.Clear();
        }
        Touch();
    }

    private void ClearLine(int y, TermColor bg)
    {
        var line = _lines[y];
        line.Wrapped = false;
        for (int x = 0; x < Cols; x++) line.Cells[x].Clear(bg);
    }

    public void InsertLines(int n, TermColor bg)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom) return;
        n = Math.Min(n, ScrollBottom - CursorY + 1);
        for (int i = 0; i < n; i++) { _lines.RemoveAt(ScrollBottom); _lines.Insert(CursorY, new Line(Cols)); }
        Touch();
    }

    public void DeleteLines(int n, TermColor bg)
    {
        if (CursorY < ScrollTop || CursorY > ScrollBottom) return;
        n = Math.Min(n, ScrollBottom - CursorY + 1);
        for (int i = 0; i < n; i++) { _lines.RemoveAt(CursorY); _lines.Insert(ScrollBottom, new Line(Cols)); }
        Touch();
    }

    public void InsertChars(int n, TermColor bg)
    {
        var cells = _lines[CursorY].Cells;
        n = Math.Min(n, Cols - CursorX);
        for (int x = Cols - 1; x >= CursorX + n; x--) cells[x] = cells[x - n];
        for (int x = CursorX; x < CursorX + n; x++) cells[x].Clear(bg);
        Touch();
    }

    public void DeleteChars(int n, TermColor bg)
    {
        var cells = _lines[CursorY].Cells;
        n = Math.Min(n, Cols - CursorX);
        for (int x = CursorX; x < Cols - n; x++) cells[x] = cells[x + n];
        for (int x = Cols - n; x < Cols; x++) cells[x].Clear(bg);
        Touch();
    }

    public void EraseChars(int n, TermColor bg)
    {
        var cells = _lines[CursorY].Cells;
        for (int x = CursorX; x < Math.Min(Cols, CursorX + n); x++) cells[x].Clear(bg);
        Touch();
    }

    public void SwitchAlternate(bool on, TermColor bg)
    {
        if (on == Alternate) return;
        Alternate = on;
        _lines = on ? _altLines : _mainLines;
        if (on) foreach (var l in _altLines) { l.Wrapped = false; for (int x = 0; x < l.Cells.Length; x++) l.Cells[x].Clear(bg); }
        Touch();
    }

    public void Resize(int cols, int rows, TermColor bg)
    {
        cols = Math.Max(1, cols); rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;

        if (cols != Cols)
        {
            foreach (var l in _mainLines) l.Resize(cols, bg);
            foreach (var l in _altLines) l.Resize(cols, bg);
            foreach (var l in Scrollback) if (l.Cells.Length > cols) l.Resize(cols, bg);   // trimmed lines stay trimmed
        }

        // Grow/shrink the main buffer by trading lines with scrollback (keeps output visible on resize).
        // Cols must be updated first: AdjustHeight creates new lines at the current width.
        Cols = cols;
        // only the buffer currently on screen owns the cursor
        AdjustHeight(_mainLines, rows, bg, useScrollback: !Alternate, moveCursor: !Alternate);
        AdjustHeight(_altLines, rows, bg, useScrollback: false, moveCursor: Alternate);

        Rows = rows;
        ScrollTop = 0; ScrollBottom = rows - 1;
        CursorX = Math.Clamp(CursorX, 0, cols - 1);
        CursorY = Math.Clamp(CursorY, 0, rows - 1);
        _pendingWrap = false;
        Touch();
    }

    private void AdjustHeight(List<Line> lines, int rows, TermColor bg, bool useScrollback, bool moveCursor)
    {
        while (lines.Count > rows)
        {
            if (useScrollback) { Scrollback.Add(lines[0].Compact()); TrimScrollback(); }
            lines.RemoveAt(0);
            if (moveCursor && CursorY > 0) CursorY--;
        }
        while (lines.Count < rows)
        {
            if (useScrollback && Scrollback.Count > 0)
            {
                var back = Scrollback[^1];
                back.Resize(Cols, bg);   // back on screen: full width again
                lines.Insert(0, back);
                Scrollback.RemoveAt(Scrollback.Count - 1);
                if (moveCursor) CursorY++;
            }
            else lines.Add(new Line(Cols));
        }
    }

    // Plain text of a line (for selection / copy), honoring soft-wrap when joining.
    public string LineText(Line line)
    {
        var sb = new StringBuilder();
        int last = -1;
        for (int x = 0; x < line.Cells.Length; x++)
            if (!string.IsNullOrEmpty(line.Cells[x].Text) || line.Cells[x].Bg.Kind != 0) last = x;
        for (int x = 0; x <= last; x++)
        {
            if ((line.Cells[x].Flags & CellFlags.WideRight) != 0) continue;
            sb.Append(line.Cells[x].Text ?? " ");
        }
        return sb.ToString();
    }

    public void ResetWrapPending() => _pendingWrap = false;
}
