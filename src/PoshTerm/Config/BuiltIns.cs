using System.Windows.Input;

namespace PoshTerm.Config;

public static class BuiltInSchemes
{
    private static ColorScheme S(string name, string bg, string fg, string palette, string? cursor = null, string? selection = null)
    {
        var p = palette.Split(' ');
        return new ColorScheme
        {
            Name = name, Background = bg, Foreground = fg, CursorColor = cursor, SelectionBackground = selection,
            Black = p[0], Red = p[1], Green = p[2], Yellow = p[3], Blue = p[4], Purple = p[5], Cyan = p[6], White = p[7],
            BrightBlack = p[8], BrightRed = p[9], BrightGreen = p[10], BrightYellow = p[11],
            BrightBlue = p[12], BrightPurple = p[13], BrightCyan = p[14], BrightWhite = p[15],
        };
    }

    private const string Campbell = "#0C0C0C #C50F1F #13A10E #C19C00 #0037DA #881798 #3A96DD #CCCCCC #767676 #E74856 #16C60C #F9F1A5 #3B78FF #B4009E #61D6D6 #F2F2F2";
    private const string Solar = "#002B36 #DC322F #859900 #B58900 #268BD2 #D33682 #2AA198 #EEE8D5 #073642 #CB4B16 #586E75 #657B83 #839496 #6C71C4 #93A1A1 #FDF6E3";

    public static readonly IReadOnlyList<ColorScheme> All = new[]
    {
        S("PoshTerm Night", "#101218", "#D4D8E0", "#1B1E27 #F0717A #8BD49C #E7C27D #6CA8F7 #C59BF0 #5CCFE6 #C9CED8 #5C6370 #FF8A92 #A6E9B4 #F5D59A #8EC0FF #D7B4FA #84E1F2 #F2F4F8", "#8EC0FF", "#3A4A6B"),
        S("Campbell", "#0C0C0C", "#CCCCCC", Campbell, "#FFFFFF"),
        S("Campbell Powershell", "#012456", "#CCCCCC", Campbell, "#FFFFFF"),
        S("One Half Dark", "#282C34", "#DCDFE4", "#282C34 #E06C75 #98C379 #E5C07B #61AFEF #C678DD #56B6C2 #DCDFE4 #5A6374 #E06C75 #98C379 #E5C07B #61AFEF #C678DD #56B6C2 #DCDFE4", "#FFFFFF"),
        S("One Half Light", "#FAFAFA", "#383A42", "#383A42 #E45649 #50A14F #C18301 #0184BC #A626A4 #0997B3 #FAFAFA #4F525D #DF6C75 #98C379 #E4C07A #61AFEF #C577DD #56B5C1 #FFFFFF", "#4F525D", "#BFCEFF"),
        S("Dracula", "#282A36", "#F8F8F2", "#21222C #FF5555 #50FA7B #F1FA8C #BD93F9 #FF79C6 #8BE9FD #F8F8F2 #6272A4 #FF6E6E #69FF94 #FFFFA5 #D6ACFF #FF92DF #A4FFFF #FFFFFF", "#F8F8F2", "#44475A"),
        S("Tokyo Night", "#1A1B26", "#C0CAF5", "#15161E #F7768E #9ECE6A #E0AF68 #7AA2F7 #BB9AF7 #7DCFFF #A9B1D6 #414868 #F7768E #9ECE6A #E0AF68 #7AA2F7 #BB9AF7 #7DCFFF #C0CAF5", "#C0CAF5", "#33467C"),
        S("Catppuccin Mocha", "#1E1E2E", "#CDD6F4", "#45475A #F38BA8 #A6E3A1 #F9E2AF #89B4FA #F5C2E7 #94E2D5 #BAC2DE #585B70 #F38BA8 #A6E3A1 #F9E2AF #89B4FA #F5C2E7 #94E2D5 #A6ADC8", "#F5E0DC", "#585B70"),
        S("Nord", "#2E3440", "#D8DEE9", "#3B4252 #BF616A #A3BE8C #EBCB8B #81A1C1 #B48EAD #88C0D0 #E5E9F0 #4C566A #BF616A #A3BE8C #EBCB8B #81A1C1 #B48EAD #8FBCBB #ECEFF4", "#D8DEE9", "#434C5E"),
        S("Gruvbox Dark", "#282828", "#EBDBB2", "#282828 #CC241D #98971A #D79921 #458588 #B16286 #689D6A #A89984 #928374 #FB4934 #B8BB26 #FABD2F #83A598 #D3869B #8EC07C #EBDBB2", "#EBDBB2", "#504945"),
        S("Monokai", "#272822", "#F8F8F2", "#272822 #F92672 #A6E22E #F4BF75 #66D9EF #AE81FF #A1EFE4 #F8F8F2 #75715E #F92672 #A6E22E #F4BF75 #66D9EF #AE81FF #A1EFE4 #F9F8F5", "#F8F8F0", "#49483E"),
        S("Solarized Dark", "#002B36", "#839496", Solar, "#93A1A1"),
        S("Solarized Light", "#FDF6E3", "#657B83", Solar, "#586E75", "#EEE8D5"),
        S("Tango Dark", "#000000", "#D3D7CF", "#000000 #CC0000 #4E9A06 #C4A000 #3465A4 #75507B #06989A #D3D7CF #555753 #EF2929 #8AE234 #FCE94F #729FCF #AD7FA8 #34E2E2 #EEEEEC", "#FFFFFF"),
        S("Matrix", "#020A02", "#3BFF6A", "#020A02 #1E8C3A #3BFF6A #9DFF5E #1F7A43 #2FBF71 #48E58C #B8FFC8 #0F4D20 #2FBF71 #6BFF8E #C2FF8A #33A35E #4FD98B #7DFFB0 #E6FFEC", "#3BFF6A", "#145A2A"),
    };
}

// A key combination like "ctrl+shift+t", normalized so settings.json spellings all compare equal.
public readonly record struct Chord(ModifierKeys Mods, Key Key)
{
    private static readonly Dictionary<string, Key> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = Key.OemPlus, ["plus"] = Key.OemPlus, ["-"] = Key.OemMinus, ["minus"] = Key.OemMinus, [","] = Key.OemComma, ["comma"] = Key.OemComma,
        ["."] = Key.OemPeriod, ["period"] = Key.OemPeriod, ["/"] = Key.OemQuestion, ["`"] = Key.OemTilde, ["backtick"] = Key.OemTilde,
        ["["] = Key.OemOpenBrackets, ["]"] = Key.OemCloseBrackets, [";"] = Key.OemSemicolon, ["'"] = Key.OemQuotes, ["\\"] = Key.OemPipe,
        ["esc"] = Key.Escape, ["escape"] = Key.Escape, ["enter"] = Key.Enter, ["return"] = Key.Enter, ["space"] = Key.Space, ["tab"] = Key.Tab,
        ["up"] = Key.Up, ["down"] = Key.Down, ["left"] = Key.Left, ["right"] = Key.Right, ["pageup"] = Key.PageUp, ["pgup"] = Key.PageUp,
        ["pagedown"] = Key.PageDown, ["pgdn"] = Key.PageDown, ["home"] = Key.Home, ["end"] = Key.End, ["insert"] = Key.Insert, ["ins"] = Key.Insert,
        ["delete"] = Key.Delete, ["del"] = Key.Delete, ["backspace"] = Key.Back,
    };

    public static bool TryParse(string text, out Chord chord)
    {
        chord = default;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Replace(" ", "").Split('+', StringSplitOptions.None))
        {
            var part = raw;
            if (part.Length == 0) { part = "+"; }   // "ctrl++"
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; continue;
                case "alt": case "option": mods |= ModifierKeys.Alt; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "win": case "super": case "meta": case "cmd": mods |= ModifierKeys.Windows; continue;
            }
            if (Names.TryGetValue(part, out var k)) key = k;
            else if (part.Length == 1 && char.IsLetter(part[0])) key = Enum.Parse<Key>(part.ToUpperInvariant());
            else if (part.Length == 1 && char.IsDigit(part[0])) key = Enum.Parse<Key>("D" + part);
            else if (Enum.TryParse<Key>(part, true, out var ek)) key = ek;
            else return false;
        }
        if (key is null) return false;
        chord = new Chord(mods, key.Value);
        return true;
    }

    // Normalizes keys that WPF reports differently (Add/Subtract on the numpad, system keys under Alt).
    public static Chord From(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Add) key = Key.OemPlus;
        if (key == Key.Subtract) key = Key.OemMinus;
        return new Chord(Keyboard.Modifiers, key);
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key switch
        {
            Key.OemPlus => "=", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".", Key.OemTilde => "`",
            Key.OemQuestion => "/", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemPipe => "\\", Key.Back => "Backspace", >= Key.D0 and <= Key.D9 => ((int)Key - (int)Key.D0).ToString(),
            _ => Key.ToString(),
        });
        return string.Join("+", parts);
    }
}

public static class DefaultKeys
{
    // action ids are resolved in Actions.cs; "goToTab:N" / "newTabProfile:N" take an argument.
    public static readonly IReadOnlyDictionary<string, string> Map = Build();

    private static Dictionary<string, string> Build()
    {
        var m = new Dictionary<string, string>
        {
            ["ctrl+shift+t"] = "newTab", ["ctrl+shift+w"] = "closePane", ["ctrl+tab"] = "nextTab", ["ctrl+shift+tab"] = "prevTab",
            ["ctrl+pagedown"] = "nextTab", ["ctrl+pageup"] = "prevTab", ["ctrl+shift+d"] = "duplicateTab", ["ctrl+shift+n"] = "newWindow",
            ["alt+shift+="] = "splitRight", ["alt+shift+-"] = "splitDown", ["alt+shift+d"] = "splitAuto", ["ctrl+shift+z"] = "togglePaneZoom",
            ["alt+left"] = "focusLeft", ["alt+right"] = "focusRight", ["alt+up"] = "focusUp", ["alt+down"] = "focusDown",
            ["alt+shift+left"] = "resizeLeft", ["alt+shift+right"] = "resizeRight", ["alt+shift+up"] = "resizeUp", ["alt+shift+down"] = "resizeDown",
            ["ctrl+c"] = "copy", ["ctrl+shift+c"] = "copy", ["ctrl+v"] = "paste", ["ctrl+shift+v"] = "paste", ["shift+insert"] = "paste",
            ["ctrl+shift+a"] = "selectAll", ["ctrl+shift+f"] = "find", ["ctrl+="] = "zoomIn", ["ctrl+-"] = "zoomOut", ["ctrl+0"] = "zoomReset",
            ["f11"] = "toggleFullscreen", ["alt+enter"] = "toggleFullscreen", ["ctrl+,"] = "openSettings", ["ctrl+shift+,"] = "openSettingsFile",
            ["ctrl+shift+p"] = "commandPalette", ["ctrl+shift+b"] = "toggleBackgroundImage", ["ctrl+shift+k"] = "clearBuffer",
            ["ctrl+shift+home"] = "scrollToTop", ["ctrl+shift+end"] = "scrollToBottom", ["ctrl+shift+pageup"] = "scrollPageUp",
            ["ctrl+shift+pagedown"] = "scrollPageDown", ["ctrl+shift+up"] = "scrollUp", ["ctrl+shift+down"] = "scrollDown",
            ["ctrl+shift+r"] = "renameTab", ["ctrl+shift+]"] = "increaseOpacity", ["ctrl+shift+["] = "decreaseOpacity",
        };
        for (int i = 1; i <= 9; i++) { m[$"ctrl+alt+{i}"] = $"goToTab:{i}"; m[$"ctrl+shift+{i}"] = $"newTabProfile:{i}"; }
        return m;
    }

    // Defaults overlaid with the user's bindings. Matching is by parsed chord, so "Ctrl+Shift+T" and
    // "ctrl+shift+t" are the same key, and "unbound" removes a default whatever its spelling.
    public static Dictionary<Chord, string> Resolve(IDictionary<string, string> user)
    {
        var res = new Dictionary<Chord, string>();
        foreach (var kv in Map) if (Chord.TryParse(kv.Key, out var c)) res[c] = kv.Value;
        foreach (var kv in user)
        {
            if (!Chord.TryParse(kv.Key, out var c)) continue;
            if (string.IsNullOrWhiteSpace(kv.Value) || kv.Value == "unbound") res.Remove(c);
            else res[c] = kv.Value;
        }
        return res;
    }
}
