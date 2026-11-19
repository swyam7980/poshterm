using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PoshTerm.Terminal;

public sealed partial class TerminalView
{
    // ---------- selection (coordinates are view-line indexes: 0 = oldest scrollback line) ----------
    private bool _selecting;
    private (int row, int col) _selAnchor, _selActive;
    private bool _hasSelection;
    private int _clickCount;
    private DateTime _lastClick;
    private Point _lastClickPos;

    public bool HasSelection => _hasSelection && _selAnchor != _selActive;

    public void ClearSelection() { if (_hasSelection) { _hasSelection = false; Invalidate(); } }

    // n lines fell off the top of the scrollback: move selection and search hits with their text
    private void ShiftViewRows(int n)
    {
        if (_hasSelection)
        {
            _selAnchor.row -= n; _selActive.row -= n;
            if (_selAnchor.row < 0 && _selActive.row < 0) _hasSelection = false;
            else { if (_selAnchor.row < 0) _selAnchor = (0, 0); if (_selActive.row < 0) _selActive = (0, 0); }
        }
        if (_matches.Count > 0)
        {
            int gone = _matches.FindIndex(m => m.row - n >= 0);
            if (gone < 0) gone = _matches.Count;
            _matches.RemoveRange(0, gone);
            for (int i = 0; i < _matches.Count; i++) { var m = _matches[i]; _matches[i] = (m.row - n, m.c0, m.c1); }
            _matchIdx = _matches.Count == 0 ? -1 : Math.Max(0, _matchIdx - gone);
        }
        _hoverLink = null;
    }

    public void SelectAll()
    {
        _selAnchor = (0, 0);
        _selActive = (Buffer.ScrollbackCount + Buffer.Rows - 1, Buffer.Cols);
        _hasSelection = true;
        Invalidate();
    }

    private ((int row, int col) a, (int row, int col) b) SelectionOrdered()
    {
        var s = _selAnchor; var e = _selActive;
        return (s.row < e.row || (s.row == e.row && s.col <= e.col)) ? (s, e) : (e, s);
    }

    private void DrawSelectionForRow(System.Windows.Media.DrawingContext dc, int vi, double top, int n, Brush brush)
    {
        if (!HasSelection) return;
        var (a, b) = SelectionOrdered();
        if (vi < a.row || vi > b.row) return;
        int c0 = vi == a.row ? a.col : 0;
        int c1 = vi == b.row ? b.col : n;
        c1 = Math.Min(c1, n);
        if (c1 <= c0) return;
        dc.DrawRectangle(brush, null, new Rect(_padding + c0 * _cellW, top, (c1 - c0) * _cellW, _cellH));
    }

    public string GetSelectedText()
    {
        if (!HasSelection) return "";
        var (a, b) = SelectionOrdered();
        var sb = new StringBuilder();
        for (int vi = a.row; vi <= b.row; vi++)
        {
            var line = Buffer.ViewLine(vi);
            if (line == null) continue;
            int c0 = vi == a.row ? a.col : 0;
            int c1 = vi == b.row ? Math.Min(b.col, line.Cells.Length) : line.Cells.Length;
            var seg = new StringBuilder();
            for (int x = c0; x < c1 && x < line.Cells.Length; x++)
            {
                var cell = line.Cells[x];
                if ((cell.Flags & CellFlags.WideRight) != 0) continue;
                seg.Append(cell.Text ?? " ");
            }
            string text = seg.ToString();
            // a soft-wrapped line continues on the next one: keep it joined; otherwise trim padding
            if (!line.Wrapped || vi == b.row) text = text.TrimEnd();
            sb.Append(text);
            if (vi < b.row && !line.Wrapped) sb.Append('\n');
        }
        return sb.ToString();
    }

    // ---------- hit testing ----------
    private (int row, int col, int screenRow) HitTest(Point p)
    {
        int sr = Math.Clamp((int)Math.Floor((p.Y - _padding) / _cellH), 0, Buffer.Rows - 1);
        int col = Math.Clamp((int)Math.Floor((p.X - _padding) / _cellW), 0, Buffer.Cols);
        return (ViewIndexOf(sr), col, sr);
    }

    private static bool IsWordChar(string? t) => t != null && t.Length > 0 && (char.IsLetterOrDigit(t[0]) || "_-./\\:~@%+=#?&".Contains(t[0]));

    private void SelectWordAt(int row, int col)
    {
        var line = Buffer.ViewLine(row);
        if (line == null) return;
        col = Math.Min(col, line.Cells.Length - 1);
        if (!IsWordChar(line.Cells[col].Text)) { _selAnchor = (row, col); _selActive = (row, col + 1); _hasSelection = true; return; }
        int s = col, e = col;
        while (s > 0 && IsWordChar(line.Cells[s - 1].Text)) s--;
        while (e < line.Cells.Length - 1 && IsWordChar(line.Cells[e + 1].Text)) e++;
        _selAnchor = (row, s); _selActive = (row, e + 1); _hasSelection = true;
    }

    private void SelectLineAt(int row)
    {
        _selAnchor = (row, 0); _selActive = (row, Buffer.Cols); _hasSelection = true;
    }

    // ---------- mouse ----------
    private bool MouseReporting => Parser.MouseMode != MouseMode.None && (Keyboard.Modifiers & ModifierKeys.Shift) == 0;

    private void ReportMouse(int button, int col, int row, bool release, bool motion = false)
    {
        int b = button;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Shift) != 0) b |= 4;
        if ((m & ModifierKeys.Alt) != 0) b |= 8;
        if ((m & ModifierKeys.Control) != 0) b |= 16;
        if (motion) b |= 32;
        int cx = Math.Clamp(col, 0, Buffer.Cols - 1) + 1, cy = Math.Clamp(row, 0, Buffer.Rows - 1) + 1;
        string seq;
        if (Parser.MouseSgr) seq = $"\x1b[<{b};{cx};{cy}{(release ? 'm' : 'M')}";
        else
        {
            if (release) b = (b & ~3) | 3;
            seq = "\x1b[M" + (char)(32 + b) + (char)Math.Min(255, 32 + cx) + (char)Math.Min(255, 32 + cy);
        }
        SendToChild?.Invoke(Encoding.UTF8.GetBytes(seq));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var pos = e.GetPosition(this);
        var (row, col, sr) = HitTest(pos);

        if (MouseReporting && e.ChangedButton != MouseButton.Right)
        {
            int btn = e.ChangedButton == MouseButton.Left ? 0 : e.ChangedButton == MouseButton.Middle ? 1 : 2;
            ReportMouse(btn, Math.Min(col, Buffer.Cols - 1), sr, false);
            CaptureMouse();
            _reportingButton = btn;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && _hoverLink is { } link)
            {
                OpenLink(link.url);
                e.Handled = true;
                return;
            }
            var now = DateTime.UtcNow;
            _clickCount = (now - _lastClick).TotalMilliseconds < 450 && (pos - _lastClickPos).Length < 5 ? _clickCount + 1 : 1;
            _lastClick = now; _lastClickPos = pos;
            if (_clickCount == 2) SelectWordAt(row, col);
            else if (_clickCount >= 3) { SelectLineAt(row); _clickCount = 3; }
            else
            {
                _selAnchor = _selActive = (row, col);
                _hasSelection = true;
                _selecting = true;
            }
            CaptureMouse();
            Invalidate();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            PasteRequested?.Invoke();
            e.Handled = true;
        }
    }

    private int _reportingButton = -1;
    public event Action? PasteRequested;
    public event Action? SelectionFinished;        // for copy-on-select

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        var (row, col, sr) = HitTest(pos);

        if (_reportingButton >= 0 && MouseReporting && Parser.MouseMode >= MouseMode.Drag)
        {
            ReportMouse(_reportingButton, Math.Min(col, Buffer.Cols - 1), sr, false, motion: true);
            return;
        }
        if (MouseReporting && Parser.MouseMode == MouseMode.Any && _reportingButton < 0)
        {
            ReportMouse(3, Math.Min(col, Buffer.Cols - 1), sr, false, motion: true);
            return;
        }
        if (_selecting)
        {
            _selActive = (row, col);
            // dragging above/below the view scrolls it
            if (pos.Y < 0) ScrollLines(1); else if (pos.Y > ActualHeight) ScrollLines(-1);
            Invalidate();
            return;
        }
        UpdateHoverLink(row, col, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_reportingButton >= 0)
        {
            var (_, col, sr) = HitTest(e.GetPosition(this));
            ReportMouse(_reportingButton, Math.Min(col, Buffer.Cols - 1), sr, true);
            _reportingButton = -1;
            ReleaseMouseCapture();
            return;
        }
        if (_selecting || e.ChangedButton == MouseButton.Left)
        {
            bool had = _selecting;
            _selecting = false;
            ReleaseMouseCapture();
            if (HasSelection) SelectionFinished?.Invoke();
            else if (had) _hasSelection = false;
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        // precision touchpads send many small deltas; treating each as a full notch scrolled 3 lines per event
        _wheelAccum += e.Delta;
        int notches = _wheelAccum / 120;
        if (notches == 0) return;
        _wheelAccum -= notches * 120;
        if (MouseReporting)
        {
            var (_, col, sr) = HitTest(e.GetPosition(this));
            for (int i = 0; i < Math.Abs(notches); i++) ReportMouse(notches > 0 ? 64 : 65, col, sr, false);
        }
        else if (Buffer.Alternate)
        {
            // full-screen apps without mouse mode (less, man): wheel acts as arrow keys
            string k = Parser.ApplicationCursorKeys ? (notches > 0 ? "\x1bOA" : "\x1bOB") : (notches > 0 ? "\x1b[A" : "\x1b[B");
            for (int i = 0; i < Math.Abs(notches) * 3; i++) SendToChild?.Invoke(Encoding.ASCII.GetBytes(k));
        }
        else ScrollLines(notches * 3);
    }
    private int _wheelAccum;

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); if (_hoverLink != null) { _hoverLink = null; Cursor = Cursors.IBeam; Invalidate(); } }

    // ---------- links ----------
    private static readonly Regex UrlRe = new(@"(https?://[^\s<>""'`]+|file:///[^\s<>""'`]+)", RegexOptions.Compiled);
    private (int row, int c0, int c1, string url)? _hoverLink;

    private void UpdateHoverLink(int row, int col, bool ctrl)
    {
        (int, int, int, string)? found = null;
        if (ctrl)
        {
            var line = Buffer.ViewLine(row);
            if (line != null)
            {
                var (text, map) = RowText(line);
                foreach (Match m in UrlRe.Matches(text))
                {
                    string url = m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '!', '?');
                    int s = map[m.Index], e = map[Math.Min(m.Index + url.Length - 1, map.Length - 1)] + 1;
                    if (col >= s && col < e) { found = (row, s, e, url); break; }
                }
            }
        }
        var f = found is { } t ? ((int, int, int, string)?)t : null;
        if (f?.Item4 != _hoverLink?.url || f?.Item2 != _hoverLink?.c0)
        {
            _hoverLink = found is { } v ? (v.Item1, v.Item2, v.Item3, v.Item4) : null;
            Cursor = _hoverLink != null ? Cursors.Hand : Cursors.IBeam;
            Invalidate();
        }
    }

    private void DrawLinkUnderline(DrawingContext dc, int vi, double top)
    {
        if (_hoverLink is not { } l || l.row != vi) return;
        var brush = BrushFor(Hex(_scheme.BrightBlue));
        dc.DrawRectangle(brush, null, new Rect(_padding + l.c0 * _cellW, top + _cellH - 2, (l.c1 - l.c0) * _cellW, 1));
    }

    private static void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https" && u.Scheme != "file")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    // text of a row plus, for each char, the cell column it came from
    private (string text, int[] map) RowText(Line line)
    {
        var sb = new StringBuilder();
        var map = new List<int>();
        for (int x = 0; x < line.Cells.Length; x++)
        {
            var c = line.Cells[x];
            if ((c.Flags & CellFlags.WideRight) != 0) continue;
            string t = c.Text ?? " ";
            sb.Append(t);
            for (int i = 0; i < t.Length; i++) map.Add(x);
        }
        return (sb.ToString(), map.ToArray());
    }

    // ---------- search ----------
    private readonly List<(int row, int c0, int c1)> _matches = new();
    private int _matchIdx = -1;

    public int MatchCount => _matches.Count;
    public int CurrentMatch => _matchIdx;

    public int Find(string query, bool caseSensitive, bool regex)
    {
        _matches.Clear();
        _matchIdx = -1;
        if (!string.IsNullOrEmpty(query))
        {
            Regex? re = null;
            try { re = regex ? new Regex(query, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) : null; }
            catch (ArgumentException) { Invalidate(); return 0; }
            int total = Buffer.ScrollbackCount + Buffer.Rows;
            for (int vi = 0; vi < total && _matches.Count < 5000; vi++)
            {
                var line = Buffer.ViewLine(vi);
                if (line == null) continue;
                var (text, map) = RowText(line);
                if (text.Length == 0) continue;
                if (re != null)
                {
                    foreach (Match m in re.Matches(text))
                        if (m.Length > 0) _matches.Add((vi, map[m.Index], map[Math.Min(m.Index + m.Length - 1, map.Length - 1)] + 1));
                }
                else
                {
                    var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    for (int i = text.IndexOf(query, cmp); i >= 0; i = text.IndexOf(query, i + query.Length, cmp))
                        _matches.Add((vi, map[i], map[Math.Min(i + query.Length - 1, map.Length - 1)] + 1));
                }
            }
            if (_matches.Count > 0)
            {
                // start at the match nearest the bottom of the current view
                int bottomVi = ViewIndexOf(Buffer.Rows - 1);
                _matchIdx = _matches.FindLastIndex(m => m.row <= bottomVi);
                if (_matchIdx < 0) _matchIdx = 0;
                RevealMatch();
            }
        }
        Invalidate();
        return _matches.Count;
    }

    public void FindStep(int dir)
    {
        if (_matches.Count == 0) return;
        _matchIdx = (_matchIdx + dir + _matches.Count) % _matches.Count;
        RevealMatch();
        Invalidate();
    }

    public void ClearFind() { _matches.Clear(); _matchIdx = -1; Invalidate(); }

    private void RevealMatch()
    {
        if (_matchIdx < 0) return;
        int vi = _matches[_matchIdx].row;
        int top = Buffer.ScrollbackCount - _viewOffset;
        if (vi >= top && vi < top + Buffer.Rows) return;
        _viewOffset = Math.Clamp(Buffer.ScrollbackCount - vi + Buffer.Rows / 2, 0, Buffer.ScrollbackCount);
    }

    private void DrawMatchesForRow(DrawingContext dc, int vi, double top, Brush normal, Brush current)
    {
        if (_matches.Count == 0) return;
        for (int i = 0; i < _matches.Count; i++)
        {
            var m = _matches[i];
            if (m.row != vi) continue;
            dc.DrawRectangle(i == _matchIdx ? current : normal, null, new Rect(_padding + m.c0 * _cellW, top, (m.c1 - m.c0) * _cellW, _cellH));
        }
    }

    // ---------- keyboard ----------
    // What Shift+Enter sends. Terminals have no standard sequence for it (plain CR by default); the pane sets
    // ESC CR for posh, whose line editor reads it as "insert a new line" (Alt+Enter is taken by full screen).
    public string? ShiftEnter { get; set; }

    public void SendText(string text) => SendToChild?.Invoke(Encoding.UTF8.GetBytes(text));

    public void Paste(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (Parser.BracketedPaste) text = "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~";
        SendText(text);
        ScrollToBottom();
    }

    public void ClearBuffer()
    {
        Buffer.EraseInDisplay(3, TermColor.Default);
        _viewOffset = 0; ClearSelection(); ClearFind();
        Invalidate();
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        string t = e.Text;
        if (string.IsNullOrEmpty(t) || t.All(c => c < 0x20)) return;
        if (_viewOffset > 0) ScrollToBottom();
        ClearSelection();
        _cursorOn = true;
        SendText(t);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        // AltGr arrives as Ctrl+Alt and produces text through OnTextInput
        if ((mods & ModifierKeys.Control) != 0 && (mods & ModifierKeys.Alt) != 0) return;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        string? seq = EncodeKey(key, mods);
        if (seq == null) return;
        if (_viewOffset > 0) ScrollToBottom();
        ClearSelection();
        _cursorOn = true;
        SendText(seq);
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && _hoverLink != null) { _hoverLink = null; Cursor = Cursors.IBeam; Invalidate(); }
    }

    private string? EncodeKey(Key key, ModifierKeys mods)
    {
        bool ctrl = (mods & ModifierKeys.Control) != 0, alt = (mods & ModifierKeys.Alt) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        int m = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);
        bool app = Parser.ApplicationCursorKeys;
        string Csi(char final) => m > 1 ? $"\x1b[1;{m}{final}" : app ? $"\x1bO{final}" : $"\x1b[{final}";
        string Tilde(int n) => m > 1 ? $"\x1b[{n};{m}~" : $"\x1b[{n}~";

        switch (key)
        {
            case Key.Up: return Csi('A');
            case Key.Down: return Csi('B');
            case Key.Right: return Csi('C');
            case Key.Left: return Csi('D');
            case Key.Home: return Csi('H');
            case Key.End: return Csi('F');
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.Insert: return Tilde(2);
            case Key.Delete: return Tilde(3);
            case Key.F1: return m > 1 ? $"\x1b[1;{m}P" : "\x1bOP";
            case Key.F2: return m > 1 ? $"\x1b[1;{m}Q" : "\x1bOQ";
            case Key.F3: return m > 1 ? $"\x1b[1;{m}R" : "\x1bOR";
            case Key.F4: return m > 1 ? $"\x1b[1;{m}S" : "\x1bOS";
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
            case Key.Enter: return alt ? "\x1b\r" : shift && ShiftEnter != null ? ShiftEnter : "\r";
            case Key.Tab: return shift ? "\x1b[Z" : "\t";
            case Key.Escape: return "\x1b";
            case Key.Back: return ctrl ? "\x17" : alt ? "\x1b\x7f" : "\x7f";
            case Key.Space: return ctrl ? "\0" : alt ? "\x1b " : null;
        }
        if (ctrl)
        {
            if (key >= Key.A && key <= Key.Z) return ((char)(key - Key.A + 1)).ToString() is var c && alt ? "\x1b" + c : c;
            switch (key)
            {
                case Key.OemOpenBrackets: return "\x1b";
                case Key.OemPipe or Key.OemBackslash: return "\x1c";
                case Key.OemCloseBrackets: return "\x1d";
                case Key.OemQuestion: return "\x1f";
                case Key.D2: return "\0";
                case Key.D6: return "\x1e";
            }
            return null;
        }
        if (alt)
        {
            // Alt+key sends ESC followed by the character (Meta convention)
            if (key >= Key.A && key <= Key.Z) { char c = (char)('a' + (key - Key.A)); return "\x1b" + (shift ? char.ToUpperInvariant(c) : c); }
            if (key >= Key.D0 && key <= Key.D9) return "\x1b" + (char)('0' + (key - Key.D0));
            return null;
        }
        return null;
    }
}
