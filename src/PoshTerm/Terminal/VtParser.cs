using System.Globalization;
using System.Text;

namespace PoshTerm.Terminal;

// Mouse reporting modes requested by the running program.
public enum MouseMode { None, Click, Drag, Any }

// ANSI/VT escape-sequence interpreter. Feed it the child process output; it mutates a
// TerminalBuffer and raises events for things the host must act on (title, bell, replies, OSC).
public sealed class VtParser
{
    private enum State { Ground, Escape, EscapeIntermediate, CsiEntry, CsiParam, CsiIntermediate, OscString, DcsString, StString }

    private readonly TerminalBuffer _buf;
    private State _state = State.Ground;
    private readonly List<int> _params = new();
    private int _curParam = -1;
    private bool _subParam;
    private char _csiPrefix;       // '?', '>', '=' or '\0'
    private char _intermediate;
    private readonly StringBuilder _osc = new();
    private bool _escInOsc;

    // current pen
    private TermColor _fg = TermColor.Default, _bg = TermColor.Default;
    private CellFlags _flags;

    // saved cursor (DECSC)
    private (int x, int y, TermColor fg, TermColor bg, CellFlags flags, bool origin)? _saved;

    // UTF-8 decoding for the incoming byte stream
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _chars = new char[8192];
    private char _pendingHigh;

    // modes
    public bool BracketedPaste { get; private set; }
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public bool FocusReporting { get; private set; }
    public MouseMode MouseMode { get; private set; }
    public bool MouseSgr { get; private set; }
    public bool InsertMode { get; private set; }
    public bool ReverseVideo { get; private set; }
    public int CursorShape { get; private set; } = -1; // DECSCUSR value, -1 = unset

    private bool _lastWasPrintable;

    public event Action<string>? TitleChanged;
    public event Action? Bell;
    public event Action<string>? Reply;                  // bytes to send back to the child (DSR, DA, ...)
    public event Action<int, int>? Progress;              // OSC 9;4 state, percent
    public event Action<string>? WorkingDirectory;        // OSC 7 / OSC 9;9
    public event Action<string>? ClipboardSet;            // OSC 52
    public event Action? ModesChanged;

    public VtParser(TerminalBuffer buffer) { _buf = buffer; }

    public TermColor CurrentBg => _bg;

    // Entry point for raw bytes from the pty.
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            int take = Math.Min(bytes.Length, 4096);
            int n = _decoder.GetChars(bytes[..take], _chars, flush: false);
            Feed(_chars.AsSpan(0, n));
            bytes = bytes[take..];
        }
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            // surrogate pairs become a single code point
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    Process(char.ConvertToUtf32(c, text[i + 1]));
                    i++;
                }
                else { _pendingHigh = c; }
                continue;
            }
            if (_pendingHigh != 0 && char.IsLowSurrogate(c))
            {
                Process(char.ConvertToUtf32(_pendingHigh, c));
                _pendingHigh = '\0';
                continue;
            }
            _pendingHigh = '\0';
            Process(c);
        }
        _buf.Touch();
    }

    private void Process(int cp)
    {
        // C0 controls act in every state except inside strings
        if (cp < 0x20 || cp == 0x7f)
        {
            if (_state == State.OscString)
            {
                if (cp == 0x07) { EndOsc(); return; }
                if (cp == 0x1b) { _escInOsc = true; _state = State.StString; return; }
                if (cp == 0x18 || cp == 0x1a) { _state = State.Ground; return; }
                return;
            }
            if (_state == State.DcsString || _state == State.StString)
            {
                if (cp == 0x1b) { _state = State.Escape; return; }
                if (cp == 0x07) { EndString(); return; }
                return;
            }
            if (cp == 0x1b) { _state = State.Escape; _intermediate = '\0'; return; }
            if (cp == 0x18 || cp == 0x1a) { _state = State.Ground; return; }
            Execute(cp);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                Print(cp);
                break;
            case State.Escape:
                EscDispatch((char)cp);
                break;
            case State.EscapeIntermediate:
                // ESC ( B etc: designate charset - ignored (we're UTF-8)
                _state = State.Ground;
                break;
            case State.CsiEntry:
            case State.CsiParam:
                CsiByte((char)cp);
                break;
            case State.CsiIntermediate:
                if (cp >= 0x40 && cp <= 0x7e) { CsiDispatch((char)cp); _state = State.Ground; }
                else _intermediate = (char)cp;
                break;
            case State.OscString:
                _osc.Append(char.ConvertFromUtf32(cp));
                if (_osc.Length > 1 << 20) { _osc.Clear(); _state = State.Ground; }
                break;
            case State.StString:
                // ESC \ terminates; any other char after ESC aborts the string
                if (cp == '\\') { if (_escInOsc) EndOsc(); else EndString(); }
                else { _state = State.Escape; EscDispatch((char)cp); }
                _escInOsc = false;
                break;
            case State.DcsString:
                break;
        }
    }

    private void Execute(int cp)
    {
        switch (cp)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08: _buf.MoveCursor(-1, 0); break;               // BS
            case 0x09: Tab(1); break;                                // HT
            case 0x0a: case 0x0b: case 0x0c: _buf.LineFeed(); break; // LF VT FF
            case 0x0d: _buf.CarriageReturn(); break;                 // CR
        }
    }

    private void Tab(int n)
    {
        for (int i = 0; i < n; i++)
        {
            int x = _buf.CursorX + 1;
            while (x < _buf.Cols - 1 && x % 8 != 0) x++;
            _buf.CursorX = Math.Min(x, _buf.Cols - 1);
        }
        _buf.ResetWrapPending();
    }

    private void Print(int cp)
    {
        int width = CharWidth.Of(cp);
        if (width == 0)
        {
            // combining mark: attach to the previous cell
            AppendToPrevious(char.ConvertFromUtf32(cp));
            return;
        }
        if (InsertMode) _buf.InsertChars(width, _bg);
        _buf.Put(Glyph(cp), width, _fg, _bg, _flags);
        _lastWasPrintable = true;
    }

    // One shared string per common code point (Latin, Greek, Cyrillic, symbols, box drawing...). Every cell
    // holds its text as a string, and a fresh 1-char string per printed character roughly doubled the
    // memory of a full scrollback. Strings are immutable, so sharing is safe (combining marks build new ones).
    private static readonly string?[] GlyphCache = new string?[0x3000];
    private static string Glyph(int cp) =>
        cp < GlyphCache.Length ? GlyphCache[cp] ??= char.ConvertFromUtf32(cp) : char.ConvertFromUtf32(cp);

    private void AppendToPrevious(string s)
    {
        int x = _buf.CursorX;
        int y = _buf.CursorY;
        var line = _buf.LineAt(y);
        // cursor may sit past the glyph when a wrap is pending
        if (x > 0 || line.Cells[x].Text != null)
        {
            int px = line.Cells[x].Text == null || x == 0 ? Math.Max(0, x - 1) : x;
            if (line.Cells[px].Text == null && px > 0 && (line.Cells[px].Flags & CellFlags.WideRight) != 0) px--;
            if (line.Cells[px].Text != null) line.Cells[px].Text += s;
        }
    }

    private void EscDispatch(char c)
    {
        _state = State.Ground;
        switch (c)
        {
            case '[': _state = State.CsiEntry; _params.Clear(); _curParam = -1; _csiPrefix = '\0'; _intermediate = '\0'; _subParam = false; break;
            case ']': _state = State.OscString; _osc.Clear(); _escInOsc = false; break;
            case 'P': case 'X': case '^': case '_': _state = State.DcsString; break;
            case '(': case ')': case '*': case '+': case '#': case '%': case ' ': _state = State.EscapeIntermediate; break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': _buf.LineFeed(); break;
            case 'E': _buf.CarriageReturn(); _buf.LineFeed(); break;
            case 'M': _buf.ReverseLineFeed(); break;
            case 'c': FullReset(); break;
            case '=': ApplicationKeypad = true; ModesChanged?.Invoke(); break;
            case '>': ApplicationKeypad = false; ModesChanged?.Invoke(); break;
            case '\\': break; // stray ST
        }
    }

    private void EndOsc()
    {
        _state = State.Ground;
        _escInOsc = false;
        HandleOsc(_osc.ToString());
        _osc.Clear();
    }

    private void EndString() { _state = State.Ground; _escInOsc = false; }

    private void HandleOsc(string s)
    {
        int semi = s.IndexOf(';');
        string cmd = semi < 0 ? s : s[..semi];
        string arg = semi < 0 ? "" : s[(semi + 1)..];
        switch (cmd)
        {
            case "0": case "2": TitleChanged?.Invoke(arg); break;
            case "7":
                if (Uri.TryCreate(arg, UriKind.Absolute, out var u) && u.IsFile)
                    WorkingDirectory?.Invoke(u.LocalPath);
                break;
            case "9":
                if (arg.StartsWith("9;")) WorkingDirectory?.Invoke(arg[2..].Trim('"'));
                else if (arg.StartsWith("4;"))
                {
                    var p = arg[2..].Split(';');
                    int st = int.TryParse(p[0], out var a) ? a : 0;
                    int pct = p.Length > 1 && int.TryParse(p[1], out var b) ? b : 0;
                    Progress?.Invoke(st, pct);
                }
                break;
            case "52":
                {
                    int sc = arg.IndexOf(';');
                    if (sc >= 0)
                    {
                        string data = arg[(sc + 1)..];
                        if (data != "?")
                        {
                            try { ClipboardSet?.Invoke(Encoding.UTF8.GetString(Convert.FromBase64String(data))); } catch (FormatException) { }
                        }
                    }
                    break;
                }
        }
    }

    // ---------- CSI ----------
    private void CsiByte(char c)
    {
        if (c >= '0' && c <= '9')
        {
            _state = State.CsiParam;
            _curParam = (_curParam < 0 ? 0 : _curParam) * 10 + (c - '0');
            if (_curParam > 65535) _curParam = 65535;
        }
        else if (c == ';' || c == ':')
        {
            _state = State.CsiParam;
            if (c == ':') _subParam = true;
            _params.Add(_curParam < 0 ? 0 : _curParam);
            _curParam = -1;
        }
        else if (c == '?' || c == '>' || c == '=' || c == '<')
        {
            if (_state == State.CsiEntry) _csiPrefix = c;
        }
        else if (c >= 0x20 && c <= 0x2f) { _state = State.CsiIntermediate; _intermediate = c; }
        else if (c >= 0x40 && c <= 0x7e)
        {
            if (_curParam >= 0 || _params.Count > 0) _params.Add(_curParam < 0 ? 0 : _curParam);
            CsiDispatch(c);
            _state = State.Ground;
        }
        else _state = State.Ground;
    }

    private int P(int i, int def = 1) => i < _params.Count && _params[i] > 0 ? _params[i] : def;
    private int P0(int i) => i < _params.Count ? _params[i] : 0;

    private void CsiDispatch(char c)
    {
        if (_intermediate == ' ' && c == 'q') { CursorShape = P0(0); ModesChanged?.Invoke(); return; }
        if (_intermediate == '!' && c == 'p') { SoftReset(); return; }
        if (_intermediate != '\0') return;

        if (_csiPrefix == '?') { PrivateMode(c); return; }
        if (_csiPrefix == '>')
        {
            if (c == 'c') Reply?.Invoke("\x1b[>0;10;1c");   // secondary DA
            return;
        }
        if (_csiPrefix != '\0') return;

        var b = _buf;
        switch (c)
        {
            case 'A': b.MoveCursor(0, -P(0)); break;
            case 'B': case 'e': b.MoveCursor(0, P(0)); break;
            case 'C': case 'a': b.MoveCursor(P(0), 0); break;
            case 'D': b.MoveCursor(-P(0), 0); break;
            case 'E': b.MoveCursor(0, P(0)); b.CarriageReturn(); break;
            case 'F': b.MoveCursor(0, -P(0)); b.CarriageReturn(); break;
            case 'G': case '`': b.SetCursor(P(0) - 1, b.CursorY - (b.OriginMode ? b.ScrollTop : 0)); break;
            case 'H': case 'f': b.SetCursor(P(1) - 1, P(0) - 1); break;
            case 'd': b.SetCursor(b.CursorX, P(0) - 1); break;
            case 'I': Tab(P(0)); break;
            case 'J': b.EraseInDisplay(P0(0), _bg); break;
            case 'K': b.EraseInLine(P0(0), _bg); break;
            case 'L': b.InsertLines(P(0), _bg); break;
            case 'M': b.DeleteLines(P(0), _bg); break;
            case '@': b.InsertChars(P(0), _bg); break;
            case 'P': b.DeleteChars(P(0), _bg); break;
            case 'X': b.EraseChars(P(0), _bg); break;
            case 'S': b.ScrollUp(P(0)); break;
            case 'T': b.ScrollDown(P(0)); break;
            case 'b': if (_lastWasPrintable) { var cell = b.LineAt(b.CursorY).Cells[Math.Max(0, b.CursorX - 1)]; for (int i = 0; i < P(0); i++) b.Put(cell.Text ?? " ", 1, _fg, _bg, _flags); } break;
            case 'r':
                {
                    int top = P(0) - 1, bot = (_params.Count > 1 && _params[1] > 0 ? _params[1] : b.Rows) - 1;
                    if (top < bot && bot < b.Rows) { b.ScrollTop = top; b.ScrollBottom = bot; b.SetCursor(0, 0); }
                    break;
                }
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 'm': Sgr(); break;
            case 'h': SetAnsiMode(true); break;
            case 'l': SetAnsiMode(false); break;
            case 'n':
                if (P0(0) == 5) Reply?.Invoke("\x1b[0n");
                else if (P0(0) == 6) Reply?.Invoke($"\x1b[{b.CursorY - (b.OriginMode ? b.ScrollTop : 0) + 1};{b.CursorX + 1}R");
                break;
            case 'c': if (P0(0) == 0) Reply?.Invoke("\x1b[?62;22c"); break;
            case 't':
                if (P0(0) == 18) Reply?.Invoke($"\x1b[8;{b.Rows};{b.Cols}t");
                break;
        }
        _lastWasPrintable = c == 'b' && _lastWasPrintable;
    }

    private void SetAnsiMode(bool on)
    {
        foreach (var p in _params.DefaultIfEmpty(0))
            if (p == 4) InsertMode = on;
    }

    private void PrivateMode(char c)
    {
        if (c != 'h' && c != 'l') return;
        bool on = c == 'h';
        foreach (var p in _params)
        {
            switch (p)
            {
                case 1: ApplicationCursorKeys = on; break;
                case 5: ReverseVideo = on; break;
                case 6: _buf.OriginMode = on; _buf.SetCursor(0, 0); break;
                case 7: _buf.AutoWrap = on; break;
                case 12: break;                                   // cursor blink: host decides
                case 25: _buf.CursorVisible = on; break;
                case 47: case 1047: _buf.SwitchAlternate(on, _bg); break;
                case 1048: if (on) SaveCursor(); else RestoreCursor(); break;
                case 1049:
                    if (on) { SaveCursor(); _buf.SwitchAlternate(true, _bg); _buf.EraseInDisplay(2, _bg); _buf.SetCursor(0, 0); }
                    else { _buf.SwitchAlternate(false, _bg); RestoreCursor(); }
                    break;
                case 9: case 1000: MouseMode = on ? MouseMode.Click : MouseMode.None; break;
                case 1002: MouseMode = on ? MouseMode.Drag : MouseMode.None; break;
                case 1003: MouseMode = on ? MouseMode.Any : MouseMode.None; break;
                case 1004: FocusReporting = on; break;
                case 1006: MouseSgr = on; break;
                case 2004: BracketedPaste = on; break;
            }
        }
        ModesChanged?.Invoke();
        _buf.Touch();
    }

    private void SaveCursor() => _saved = (_buf.CursorX, _buf.CursorY, _fg, _bg, _flags, _buf.OriginMode);

    private void RestoreCursor()
    {
        if (_saved is { } s)
        {
            _buf.OriginMode = s.origin;
            _buf.CursorX = Math.Min(s.x, _buf.Cols - 1);
            _buf.CursorY = Math.Min(s.y, _buf.Rows - 1);
            _fg = s.fg; _bg = s.bg; _flags = s.flags;
            _buf.ResetWrapPending();
        }
    }

    private void SoftReset()
    {
        _fg = TermColor.Default; _bg = TermColor.Default; _flags = CellFlags.None;
        _buf.OriginMode = false; _buf.AutoWrap = true; _buf.CursorVisible = true;
        _buf.ScrollTop = 0; _buf.ScrollBottom = _buf.Rows - 1;
        InsertMode = false; ApplicationCursorKeys = false;
    }

    private void FullReset()
    {
        SoftReset();
        _buf.SwitchAlternate(false, TermColor.Default);
        _buf.EraseInDisplay(2, TermColor.Default);
        _buf.SetCursor(0, 0);
        BracketedPaste = false; MouseMode = MouseMode.None; MouseSgr = false; ApplicationKeypad = false; FocusReporting = false;
        ModesChanged?.Invoke();
    }

    // ---------- SGR ----------
    private void Sgr()
    {
        if (_params.Count == 0) { ResetPen(); return; }
        for (int i = 0; i < _params.Count; i++)
        {
            int p = _params[i];
            switch (p)
            {
                case 0: ResetPen(); break;
                case 1: _flags |= CellFlags.Bold; break;
                case 2: _flags |= CellFlags.Faint; break;
                case 3: _flags |= CellFlags.Italic; break;
                case 4: _flags |= CellFlags.Underline; break;
                case 5: case 6: _flags |= CellFlags.Blink; break;
                case 7: _flags |= CellFlags.Inverse; break;
                case 8: _flags |= CellFlags.Invisible; break;
                case 9: _flags |= CellFlags.Strikethrough; break;
                case 21: _flags |= CellFlags.DoubleUnderline; break;
                case 22: _flags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                case 23: _flags &= ~CellFlags.Italic; break;
                case 24: _flags &= ~(CellFlags.Underline | CellFlags.DoubleUnderline); break;
                case 25: _flags &= ~CellFlags.Blink; break;
                case 27: _flags &= ~CellFlags.Inverse; break;
                case 28: _flags &= ~CellFlags.Invisible; break;
                case 29: _flags &= ~CellFlags.Strikethrough; break;
                case 53: _flags |= CellFlags.Overline; break;
                case 55: _flags &= ~CellFlags.Overline; break;
                case >= 30 and <= 37: _fg = TermColor.Palette(p - 30); break;
                case 38: i = ExtendedColor(i, true); break;
                case 39: _fg = TermColor.Default; break;
                case >= 40 and <= 47: _bg = TermColor.Palette(p - 40); break;
                case 48: i = ExtendedColor(i, false); break;
                case 49: _bg = TermColor.Default; break;
                case >= 90 and <= 97: _fg = TermColor.Palette(p - 90 + 8); break;
                case >= 100 and <= 107: _bg = TermColor.Palette(p - 100 + 8); break;
            }
        }
    }

    private int ExtendedColor(int i, bool fg)
    {
        if (i + 1 >= _params.Count) return i;
        int mode = _params[i + 1];
        if (mode == 5 && i + 2 < _params.Count)
        {
            var c = TermColor.Palette(_params[i + 2]);
            if (fg) _fg = c; else _bg = c;
            return i + 2;
        }
        if (mode == 2 && i + 4 < _params.Count)
        {
            // tolerate the colon form's empty colorspace id (38:2::r:g:b)
            int o = i + 2;
            if (_params.Count - o >= 4 && _subParam) o++;
            var c = TermColor.Rgb((byte)_params[o], (byte)_params[o + 1], (byte)_params[o + 2]);
            if (fg) _fg = c; else _bg = c;
            return o + 2;
        }
        return i + 1;
    }

    private void ResetPen() { _fg = TermColor.Default; _bg = TermColor.Default; _flags = CellFlags.None; }
}

// Display width of a code point: 0 for combining marks, 2 for East-Asian wide and emoji, else 1.
public static class CharWidth
{
    public static int Of(int cp)
    {
        if (cp < 0x300) return 1;
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return cp == 0x00AD ? 1 : 0;
        if (cp == 0x200B || cp == 0xFE0F || cp == 0xFE0E) return 0;
        if (IsWide(cp)) return 2;
        return 1;
    }

    private static bool IsWide(int c) =>
        (c >= 0x1100 && c <= 0x115F) || c == 0x2329 || c == 0x232A ||
        (c >= 0x2E80 && c <= 0x303E) || (c >= 0x3041 && c <= 0x33FF) || (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xA000 && c <= 0xA4CF) || (c >= 0xAC00 && c <= 0xD7A3) ||
        (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE6F) || (c >= 0xFF00 && c <= 0xFF60) ||
        (c >= 0xFFE0 && c <= 0xFFE6) || (c >= 0x1F300 && c <= 0x1F64F) || (c >= 0x1F900 && c <= 0x1F9FF) ||
        (c >= 0x1F680 && c <= 0x1F6FF) || (c >= 0x20000 && c <= 0x3FFFD) ||
        c == 0x231A || c == 0x231B || (c >= 0x23E9 && c <= 0x23EC) || c == 0x23F0 || c == 0x23F3 ||
        c == 0x25FD || c == 0x25FE || c == 0x2614 || c == 0x2615 || (c >= 0x2648 && c <= 0x2653) ||
        c == 0x267F || c == 0x2693 || c == 0x26A1 || c == 0x26AA || c == 0x26AB || c == 0x26BD || c == 0x26BE ||
        c == 0x26C4 || c == 0x26C5 || c == 0x26CE || c == 0x26D4 || c == 0x26EA || c == 0x26F2 || c == 0x26F3 ||
        c == 0x26F5 || c == 0x26FA || c == 0x26FD || c == 0x2705 || c == 0x270A || c == 0x270B || c == 0x2728 ||
        c == 0x274C || c == 0x274E || (c >= 0x2753 && c <= 0x2755) || c == 0x2757 || (c >= 0x2795 && c <= 0x2797) ||
        c == 0x27B0 || c == 0x27BF || c == 0x2B1B || c == 0x2B1C || c == 0x2B50 || c == 0x2B55;
}
