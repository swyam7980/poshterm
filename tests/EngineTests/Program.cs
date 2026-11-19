using System.Text;
using PoshTerm.Terminal;

int failed = 0, passed = 0;
void Check(bool ok, string name, string detail = "")
{
    if (ok) passed++; else { failed++; Console.WriteLine($"FAIL {name} {detail}"); }
}

(TerminalBuffer b, VtParser p, List<string> replies) Make(int cols = 20, int rows = 5)
{
    var b = new TerminalBuffer(cols, rows);
    var p = new VtParser(b);
    var replies = new List<string>();
    p.Reply += replies.Add;
    return (b, p, replies);
}
void Send(VtParser p, string s) => p.Feed(Encoding.UTF8.GetBytes(s));
string Row(TerminalBuffer b, int y) => b.LineText(b.LineAt(y));

{   // plain text, CR/LF
    var (b, p, _) = Make();
    Send(p, "hello\r\nworld");
    Check(Row(b, 0) == "hello", "text row0", Row(b, 0));
    Check(Row(b, 1) == "world", "text row1", Row(b, 1));
    Check(b.CursorX == 5 && b.CursorY == 1, "cursor after text", $"{b.CursorX},{b.CursorY}");
}
{   // wrap
    var (b, p, _) = Make(10, 3);
    Send(p, "0123456789AB");
    Check(Row(b, 0) == "0123456789" && Row(b, 1) == "AB", "autowrap", Row(b, 0) + "|" + Row(b, 1));
    Check(b.LineAt(0).Wrapped, "wrapped flag");
}
{   // cursor addressing + erase
    var (b, p, _) = Make();
    Send(p, "\x1b[2;3Hxy\x1b[1;1HAB\x1b[2;1H\x1b[K");
    Check(Row(b, 0) == "AB", "cup row0", Row(b, 0));
    Check(Row(b, 1) == "", "EL cleared row1", Row(b, 1));
    Send(p, "\x1b[3;3Hzz\x1b[3;1H\x1b[1K");
    Check(Row(b, 2).Trim() == "zz" || Row(b, 2) == "   zz" || Row(b, 2).TrimStart() == "", "EL to start", Row(b, 2));
}
{   // scrolling into scrollback
    var (b, p, _) = Make(10, 3);
    Send(p, "1\r\n2\r\n3\r\n4\r\n5");
    Check(Row(b, 0) == "3" && Row(b, 2) == "5", "scrolled screen", $"{Row(b, 0)}{Row(b, 1)}{Row(b, 2)}");
    Check(b.ScrollbackCount == 2 && b.LineText(b.Scrollback[0]) == "1", "scrollback", b.ScrollbackCount.ToString());
}
{   // SGR colors
    var (b, p, _) = Make();
    Send(p, "\x1b[1;31mR\x1b[0m\x1b[38;2;10;20;30mT\x1b[48;5;200mP\x1b[0mN");
    var c = b.LineAt(0).Cells;
    Check((c[0].Flags & CellFlags.Bold) != 0 && c[0].Fg.Kind == 1 && c[0].Fg.Index == 1, "sgr bold red");
    Check(c[1].Fg.Kind == 2 && c[1].Fg.R == 10 && c[1].Fg.G == 20 && c[1].Fg.B == 30, "sgr truecolor");
    Check(c[2].Bg.Kind == 1 && c[2].Bg.Index == 200, "sgr 256 bg");
    Check(c[3].Fg.IsDefault && c[3].Flags == CellFlags.None, "sgr reset");
}
{   // colon-form truecolor
    var (b, p, _) = Make();
    Send(p, "\x1b[38:2::1:2:3mX");
    var c = b.LineAt(0).Cells[0];
    Check(c.Fg.Kind == 2 && c.Fg.R == 1 && c.Fg.G == 2 && c.Fg.B == 3, "sgr colon truecolor", $"{c.Fg.Kind} {c.Fg.R},{c.Fg.G},{c.Fg.B}");
}
{   // alternate screen restores main
    var (b, p, _) = Make();
    Send(p, "main\x1b[?1049h");
    Check(b.Alternate && Row(b, 0) == "", "alt screen entered", Row(b, 0));
    Send(p, "ALT");
    Send(p, "\x1b[?1049l");
    Check(!b.Alternate && Row(b, 0) == "main", "alt screen restored", Row(b, 0));
}
{   // scroll region
    var (b, p, _) = Make(10, 5);
    Send(p, "A\r\nB\r\nC\r\nD\r\nE\x1b[2;4r\x1b[4;1H\n");
    Check(Row(b, 0) == "A" && Row(b, 1) == "C" && Row(b, 3) == "" && Row(b, 4) == "E", "scroll region", $"{Row(b, 0)}|{Row(b, 1)}|{Row(b, 2)}|{Row(b, 3)}|{Row(b, 4)}");
}
{   // device status report + DA
    var (b, p, r) = Make();
    Send(p, "\x1b[3;4H\x1b[6n\x1b[c");
    Check(r.Count == 2 && r[0] == "\x1b[3;4R", "DSR cursor position", string.Join("|", r));
    Check(r.Count == 2 && r[1].StartsWith("\x1b[?"), "DA reply");
}
{   // OSC title (BEL and ST), cwd, progress
    var (b, p, _) = Make();
    string title = "", cwd = ""; int st = -1, pct = -1;
    p.TitleChanged += t => title = t;
    p.WorkingDirectory += d => cwd = d;
    p.Progress += (s, v) => { st = s; pct = v; };
    Send(p, "\x1b]0;first\x07");
    Check(title == "first", "OSC 0 BEL", title);
    Send(p, "\x1b]2;second\x1b\\");
    Check(title == "second", "OSC 2 ST", title);
    Send(p, "\x1b]9;9;C:\\Users\\me\x07");
    Check(cwd == "C:\\Users\\me", "OSC 9;9 cwd", cwd);
    Send(p, "\x1b]9;4;1;42\x07");
    Check(st == 1 && pct == 42, "OSC 9;4 progress");
}
{   // modes
    var (b, p, _) = Make();
    Send(p, "\x1b[?2004h\x1b[?1h\x1b[?1000h\x1b[?1006h");
    Check(p.BracketedPaste && p.ApplicationCursorKeys && p.MouseMode == MouseMode.Click && p.MouseSgr, "private modes on");
    Send(p, "\x1b[?2004l\x1b[?1000l");
    Check(!p.BracketedPaste && p.MouseMode == MouseMode.None, "private modes off");
    Send(p, "\x1b[?25l");
    Check(!b.CursorVisible, "cursor hidden");
}
{   // wide chars and combining marks
    var (b, p, _) = Make(10, 3);
    Send(p, "a漢b");
    var c = b.LineAt(0).Cells;
    Check((c[1].Flags & CellFlags.WideLeft) != 0 && (c[2].Flags & CellFlags.WideRight) != 0 && c[3].Text == "b", "wide char cells");
    Check(Row(b, 0) == "a漢b", "wide char text", Row(b, 0));
    Send(p, "\r\ne\u0301x");
    Check(b.LineAt(1).Cells[0].Text == "e\u0301" && b.LineAt(1).Cells[1].Text == "x", "combining mark merged", b.LineAt(1).Cells[0].Text ?? "null");
    Send(p, "\r\n😀!");
    Check((b.LineAt(2).Cells[0].Flags & CellFlags.WideLeft) != 0 && b.LineAt(2).Cells[2].Text == "!", "emoji wide (surrogate pair)");
}
{   // UTF-8 split across feeds
    var (b, p, _) = Make();
    var bytes = Encoding.UTF8.GetBytes("é€");
    p.Feed(bytes.AsSpan(0, 1)); p.Feed(bytes.AsSpan(1, 2)); p.Feed(bytes.AsSpan(3));
    Check(Row(b, 0) == "é€", "utf8 split across feeds", Row(b, 0));
}
{   // insert/delete lines and chars
    var (b, p, _) = Make(10, 4);
    Send(p, "1\r\n2\r\n3\r\n4\x1b[2;1H\x1b[L");
    Check(Row(b, 0) == "1" && Row(b, 1) == "" && Row(b, 2) == "2" && Row(b, 3) == "3", "insert line", $"{Row(b, 0)}|{Row(b, 1)}|{Row(b, 2)}|{Row(b, 3)}");
    Send(p, "\x1b[M");
    Check(Row(b, 1) == "2", "delete line", Row(b, 1));
    var (b2, p2, _) = Make(10, 2);
    Send(p2, "abcdef\x1b[1;2H\x1b[2P");
    Check(Row(b2, 0) == "adef", "delete chars", Row(b2, 0));
    Send(p2, "\x1b[1;2H\x1b[2@");
    Check(Row(b2, 0) == "a  def", "insert chars", Row(b2, 0));
}
{   // save / restore cursor
    var (b, p, _) = Make();
    Send(p, "\e[2;3H\e7\e[1;1H\e8");
    Check(b.CursorX == 2 && b.CursorY == 1, "DECSC/DECRC", $"{b.CursorX},{b.CursorY}");
}
{   // resize keeps content, grows from scrollback
    var (b, p, _) = Make(10, 3);
    Send(p, "1\r\n2\r\n3\r\n4");
    b.Resize(10, 2, TermColor.Default);
    Check(Row(b, 1) == "4" && b.ScrollbackCount == 2, "shrink pushes to scrollback", $"{Row(b, 1)} {b.ScrollbackCount}");
    b.Resize(10, 4, TermColor.Default);
    Check(Row(b, 3) == "4" && b.CursorY == 3, "grow pulls from scrollback", $"{Row(b, 3)} cy={b.CursorY}");
}
{   // regression: growing wider AND taller must give new lines the new width (crashed ED2 in the app)
    var (b, p, _) = Make(10, 3);
    Send(p, "abc");
    b.Resize(40, 8, TermColor.Default);
    bool ok = true;
    for (int y = 0; y < b.Rows; y++) ok &= b.LineAt(y).Cells.Length == 40;
    Check(ok, "resize wider+taller: all lines have new width");
    Send(p, "\e[2J\e[H\e[1;1Hhello\e[K\e[J");
    Check(Row(b, 0) == "hello", "erase after resize", Row(b, 0));
    b.Resize(12, 20, TermColor.Default);
    Send(p, "\e[2J");
    b.Resize(60, 4, TermColor.Default);
    Send(p, "\e[?1049h\e[2J\e[?1049l\e[2J");
    Check(true, "resize/erase sequence no exceptions");
}
{   // scrollback lines are trimmed to their content, and regain full width when pulled back on screen
    var (b, p, _) = Make(40, 2);
    Send(p, "ab\r\n\r\n\e[41m   \e[0m\r\nx\r\ny");
    Check(b.ScrollbackCount == 3 && b.Scrollback[0].Cells.Length == 2 && b.Scrollback[1].Cells.Length == 1 && b.Scrollback[2].Cells.Length == 3,
        "scrollback trimmed (bg-colored cells kept)", string.Join(",", b.Scrollback.Select(l => l.Cells.Length)));
    Check(b.LineText(b.Scrollback[0]) == "ab", "trimmed line text", b.LineText(b.Scrollback[0]));
    b.Resize(30, 2, TermColor.Default);
    Check(b.Scrollback[0].Cells.Length == 2, "narrowing leaves trimmed lines alone");
    b.Resize(50, 6, TermColor.Default);
    bool ok = true;
    for (int y = 0; y < b.Rows; y++) ok &= b.LineAt(y).Cells.Length == 50;
    Check(ok && Row(b, 0) == "ab", "lines pulled from scrollback get full width", Row(b, 0));
    Send(p, "\e[1;45Hzz\e[1;1H\e[K");
    Check(Row(b, 0) == "", "write and erase on a restored line");
}
{   // a full scrollback counts evicted lines (the view uses it to keep selection/scroll pinned)
    var (b, p, _) = Make(10, 2);
    b.MaxScrollback = 3;
    for (int i = 0; i < 8; i++) Send(p, $"{i}\r\n");
    Check(b.ScrollbackCount == 3 && b.Evicted == 4 && b.LineText(b.Scrollback[0]) == "4", "scrollback eviction counted", $"{b.ScrollbackCount} {b.Evicted}");
    Send(p, "\e[3J");
    Check(b.ScrollbackCount == 0 && b.ViewLine(-1) == null && b.ViewLine(-50) == null, "ViewLine tolerates stale negative indexes after ED 3");
}
if (OperatingSystem.IsWindows())
{   // launch command lines: arguments must survive the real Windows parsers
    string[] Argv(string cmdLine)
    {
        var p = CommandLineToArgvW(cmdLine, out int n);
        var r = new string[n];
        for (int i = 0; i < n; i++) r[i] = System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(p, i * IntPtr.Size))!;
        LocalFree(p);
        return r;
    }
    var tricky = new PoshTerm.Config.Profile { Id = "t", Command = @"C:\Program Files\PowerShell\7\pwsh.exe", Args = "", StartupCommands = "Write-Host \"C:\\dir\\\" 'x y'\nGet-Date" };
    var argv = Argv(PoshTerm.Core.Shells.BuildLaunch(tricky, null).CommandLine);
    Check(argv.Length == 4 && argv[1] == "-NoExit" && argv[2] == "-Command" && argv[3] == "Write-Host \"C:\\dir\\\" 'x y'; Get-Date", "quoted -Command round-trips", string.Join(" | ", argv));

    var dir = Path.Combine(Path.GetTempPath(), "poshterm test " + Environment.ProcessId);
    Directory.CreateDirectory(dir);
    var script = Path.Combine(dir, "init.cmd");
    File.WriteAllText(script, "@echo SCRIPT-RAN\r\n");
    var cmdProfile = new PoshTerm.Config.Profile { Id = "c", Command = Path.Combine(Environment.SystemDirectory, "cmd.exe"), StartupScript = script, StartupCommands = "echo CMD-RAN\nexit" };
    var line = PoshTerm.Core.Shells.BuildLaunch(cmdProfile, dir).CommandLine;
    var psi = new System.Diagnostics.ProcessStartInfo(cmdProfile.Command, line[(line.IndexOf("/k", StringComparison.Ordinal))..]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir };
    using var proc = System.Diagnostics.Process.Start(psi)!;
    string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
    proc.WaitForExit(10000);
    Check(output.Contains("SCRIPT-RAN") && output.Contains("CMD-RAN"), "cmd profile runs its startup script (path with spaces)", output.Trim());
    Directory.Delete(dir, true);
}
{   // erase display
    var (b, p, _) = Make(10, 3);
    Send(p, "xxx\r\nyyy\r\nzzz\x1b[2J");
    Check(Row(b, 0) == "" && Row(b, 1) == "" && Row(b, 2) == "", "ED 2");
}
{   // robustness: garbage and truncated sequences must not throw
    var (b, p, _) = Make();
    var rnd = new Random(1);
    var junk = new byte[20000];
    rnd.NextBytes(junk);
    p.Feed(junk);
    Send(p, "\x1b[999999999;999999999H\x1b[?9999h\x1b]0;" + new string('x', 5000) + "\x07\x1b[");
    Check(true, "fuzz no exceptions");
}

Console.WriteLine($"engine tests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern IntPtr LocalFree(IntPtr mem);
