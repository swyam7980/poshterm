using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PoshTerm.Config;
using PoshTerm.Core;

namespace PoshTerm.UI;

// The settings overlay (Ctrl+,). Each control writes into App.Settings and re-applies immediately,
// so changes preview live; the file is saved a moment later.
public sealed class SettingsPanel
{
    private readonly MainWindow _win;
    private readonly ContentControl _body = new();
    private readonly StackPanel _nav = new() { Margin = new Thickness(8, 14, 8, 8) };
    private readonly Dictionary<string, Button> _navButtons = new();
    private string _page = "appearance";
    private string? _profileId;
    private PaneView? _preview;
    private (string? oldChord, string action)? _recording;
    private static SettingsPanel? _open;

    // true while the keyboard page waits for a new key combination (Escape then cancels the recording only)
    public static bool RecordingKeys => _open?._recording != null;

    private static readonly (string id, string label, string glyph)[] Pages =
    {
        ("appearance", "Appearance", "\uE790"), ("background", "Background", "\uEB9F"), ("window", "Window", "\uE737"),
        ("profiles", "Profiles", "\uE77B"), ("keyboard", "Keyboard", "\uE765"), ("about", "About", "\uE946"),
    };

    private SettingsPanel(MainWindow win) { _win = win; }

    public static void Show(MainWindow win)
    {
        var p = new SettingsPanel(win);
        _open = p;
        var root = new Grid();
        var navCol = new ColumnDefinition { Width = new GridLength(190) };
        root.ColumnDefinitions.Add(navCol);
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var navHost = new Border { Child = p._nav, BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = (Brush)Application.Current.Resources["BorderBrush2"] };
        var navGrid = new DockPanel { LastChildFill = false };
        var title = new TextBlock { Text = "Settings", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 12) };
        p._nav.Children.Add(title);
        foreach (var (id, label, glyph) in Pages)
        {
            var b = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], Width = 24, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = label });
            b.Content = sp;
            b.Click += (_, _) => { p._page = id; p._recording = null; p.Render(); };
            p._navButtons[id] = b; p._nav.Children.Add(b);
        }
        p._nav.Children.Add(new Border { Height = 20 });
        var open = new Button { Content = "Open settings.json", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2, 0, 2, 4) };
        open.Click += (_, _) => Actions.OpenSettingsFile();
        var close = new Button { Content = "Close (Esc)", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2, 0, 2, 0) };
        close.Click += (_, _) => win.HideOverlay();
        p._nav.Children.Add(open); p._nav.Children.Add(close);

        // content column: a slim top bar holding the close (X) button, the scrolling page below it
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        content.RowDefinitions.Add(new RowDefinition());
        var x = new Button
        {
            Content = "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = 11, Width = 36, Height = 36,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 6, 0),
            Style = (Style)Application.Current.Resources["FlatButton"], ToolTip = "Close (Esc)",
        };
        x.Click += (_, _) => win.HideOverlay();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = p._body, Padding = new Thickness(0, 0, 8, 0) };
        Grid.SetRow(scroll, 1);
        content.Children.Add(scroll); content.Children.Add(x);
        Grid.SetColumn(content, 1);
        p._body.Margin = new Thickness(26, 0, 22, 28);
        root.Children.Add(navHost); root.Children.Add(content);

        var card = MainWindow.Card(root, 960);
        card.PreviewKeyDown += p.OnKey;
        // size the card from the window, and keep doing so while it is open (resize, maximize, full screen)
        void Fit()
        {
            double w = win.OverlayHost.ActualWidth, h = win.OverlayHost.ActualHeight;
            if (w < 1 || h < 1) { w = win.ActualWidth; h = win.ActualHeight; }
            double top = Math.Clamp(h * 0.05, 8, 36);
            card.Margin = new Thickness(0, top, 0, 0);
            card.Width = Math.Clamp(w - 32, 300, 960);
            card.Height = Math.Clamp(h - top - 16, 180, 760);
            navCol.Width = new GridLength(card.Width < 680 ? 150 : 190);
        }
        SizeChangedEventHandler onSize = (_, _) => Fit();
        Fit();
        win.OverlayHost.SizeChanged += onSize;
        win.ShowOverlay(card, () => { _open = null; win.OverlayHost.SizeChanged -= onSize; });
        p.Render();
    }

    // ---------- shell ----------
    private void Render()
    {
        foreach (var (id, b) in _navButtons)
            b.Background = id == _page ? (Brush)Application.Current.Resources["Raised2Brush"] : Brushes.Transparent;
        var stack = new StackPanel();
        _preview = null;
        switch (_page)
        {
            case "appearance": AppearancePage(stack); break;
            case "background": BackgroundPage(stack); break;
            case "window": WindowPage(stack); break;
            case "profiles": ProfilesPage(stack); break;
            case "keyboard": KeyboardPage(stack); break;
            default: AboutPage(stack); break;
        }
        _body.Content = stack;
    }

    private void Changed() { App.SettingsChanged(); RefreshPreview(); }

    private void RefreshPreview()
    {
        if (_preview == null) return;
        var d = new Profile { Id = "preview", Name = "preview" };
        _preview.ApplyAppearance(App.Settings, d);
    }

    // ---------- control helpers ----------
    private static FrameworkElement Header(string text, string? sub = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeights.SemiBold });
        if (sub != null) sp.Children.Add(new TextBlock { Text = sub, Foreground = (Brush)Application.Current.Resources["MutedBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        return sp;
    }

    private static FrameworkElement Section(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 6),
        Foreground = (Brush)Application.Current.Resources["MutedBrush"],
    };

    private static Grid Row(string label, FrameworkElement control, string? hint = null)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        // label column shrinks with the panel (up to 200px) so narrow windows keep room for the controls
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 110, MaxWidth = 200 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(l); g.Children.Add(control);
        if (hint != null)
        {
            g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(l, 0); Grid.SetRow(control, 0);
            var h = new TextBlock { Text = hint, FontSize = 12, Foreground = (Brush)Application.Current.Resources["MutedBrush"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            Grid.SetRow(h, 1); Grid.SetColumn(h, 1); g.Children.Add(h);
        }
        return g;
    }

    private static ComboBox Combo(IEnumerable<(string value, string label)> options, string? current, Action<string> set)
    {
        var cb = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
        var list = options.ToList();
        foreach (var o in list) cb.Items.Add(new ComboBoxItem { Content = o.label, Tag = o.value });
        cb.SelectedIndex = Math.Max(0, list.FindIndex(o => o.value == current));
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem it && (string?)it.Tag != current) { current = (string?)it.Tag; set(current!); } };
        return cb;
    }

    private static TextBox Text(string? value, Action<string> set, string? placeholder = null, double width = 0)
    {
        var tb = new TextBox { Text = value ?? "", MinWidth = 200, ToolTip = placeholder };
        if (width > 0) tb.Width = width;
        void Commit() { if (tb.Text != (value ?? "")) { value = tb.Text; set(tb.Text); } }
        tb.LostFocus += (_, _) => Commit();
        tb.Unloaded += (_, _) => Commit();   // closing the panel with Esc / X doesn't move focus first
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        if (!string.IsNullOrEmpty(placeholder)) tb.Tag = placeholder;
        return tb;
    }

    private static TextBox Number(double? value, Action<double?> set, double min, double max, string fmt = "0.##")
    {
        var tb = new TextBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Text = value?.ToString(fmt, CultureInfo.InvariantCulture) ?? "" };
        string committed = tb.Text;
        void Commit()
        {
            if (tb.Text == committed) return;
            if (tb.Text.Trim().Length == 0) { committed = tb.Text; set(null); return; }
            if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { v = Math.Clamp(v, min, max); tb.Text = committed = v.ToString(fmt, CultureInfo.InvariantCulture); set(v); }
            else tb.Text = committed;
        }
        tb.LostFocus += (_, _) => Commit();
        tb.Unloaded += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return tb;
    }

    private static FrameworkElement Slider_(double value, double min, double max, double step, Action<double> set, Func<double, string> fmt)
    {
        // the track takes the free width up to 364px, so the slider shrinks with the panel but stays left-aligned
        var g = new Grid { MinWidth = 180 };
        g.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 364 }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        var s = new Slider
        {
            Minimum = min, Maximum = max, Value = value, SmallChange = step, LargeChange = step * 5, TickFrequency = step, IsSnapToTickEnabled = true,
            IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center,
        };
        var l = new TextBlock { Text = fmt(value), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["MutedBrush"] };
        s.ValueChanged += (_, _) => { l.Text = fmt(s.Value); set(Math.Round(s.Value, 3)); };
        Grid.SetColumn(l, 1);
        g.Children.Add(s); g.Children.Add(l);
        return g;
    }

    private static CheckBox Check(bool value, Action<bool> set)
    {
        var c = new CheckBox { IsChecked = value };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    private static Button Btn(string text, Action a, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        if (primary) b.Style = (Style)Application.Current.FindResource("AccentButtonStyle");
        b.Click += (_, _) => a();
        return b;
    }

    private static FrameworkElement FilePicker(string? value, string filter, Action<string> set, string? placeholder = null)
    {
        var g = new Grid { MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Stretch };
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = Text(value, set, placeholder);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = filter, CheckFileExists = true };
            if (dlg.ShowDialog() == true) { tb.Text = dlg.FileName; set(dlg.FileName); }
        };
        var clear = new Button { Content = "Clear", Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += (_, _) => { tb.Text = ""; set(""); };
        Grid.SetColumn(browse, 1); Grid.SetColumn(clear, 2);
        g.Children.Add(tb); g.Children.Add(browse); g.Children.Add(clear);
        return g;
    }

    private const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff|All files|*.*";
    private const string ScriptFilter = "Scripts|*.posh;*.ps1;*.cmd;*.bat;*.sh|All files|*.*";
    private const string ExeFilter = "Programs|*.exe;*.cmd;*.bat|All files|*.*";

    private FrameworkElement Preview()
    {
        var pane = new PaneView(new Profile { Id = "preview", Name = "preview" }, null, preview: true) { Height = 150, Margin = new Thickness(0, 6, 0, 0) };
        pane.ApplyAppearance(App.Settings, pane.Profile);
        _preview = pane;
        const string e = "\x1b";
        string sample = $"{e}[36mposh{e}[0m C:\\Users\\you> {e}[93mGet-ChildItem{e}[0m {e}[90m-Filter{e}[0m {e}[36m\"*.posh\"{e}[0m | {e}[93mSort-Object{e}[0m Length\r\n" +
                        $"{e}[92mMode        LastWriteTime  Length Name{e}[0m\r\n-a---   10/12/2026  6:40 PM    2048 {e}[94mbuild.posh{e}[0m\r\n" +
                        $"{e}[31merror:{e}[0m something failed  {e}[32m✓ ok{e}[0m  {e}[35m-> => != ==={e}[0m  {e}[1mbold{e}[0m {e}[3mitalic{e}[0m {e}[4munder{e}[0m\r\n" +
                        $"{e}[40m  {e}[41m  {e}[42m  {e}[43m  {e}[44m  {e}[45m  {e}[46m  {e}[47m  {e}[0m {e}[100m  {e}[101m  {e}[102m  {e}[103m  {e}[104m  {e}[105m  {e}[106m  {e}[107m  {e}[0m";
        var bytes = Encoding.UTF8.GetBytes(sample);
        bool fed = false;
        pane.Loaded += (_, _) => { if (fed) return; fed = true; pane.Term.EnqueueOutput(bytes, bytes.Length); };
        var border = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["BorderBrush2"], Child = pane, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 6) };
        border.Background = _win.Background;
        return border;
    }

    // Installed font families, monospaced first. Pitch comes from GDI's font enumeration: asking WPF for each
    // family's GlyphTypeface would load and cache every font file on the system (tens of MB, kept for good).
    private static List<string>? _fontNames;
    private static IEnumerable<string> FontNames() => _fontNames ??= ScanFonts();

    private static List<string> ScanFonts()
    {
        var fixedPitch = Win32.FixedPitchFontFamilies();
        var mono = new List<string>(); var other = new List<string>();
        foreach (var name in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            (fixedPitch.Contains(name) ? mono : other).Add(name);
        return mono.Concat(other).ToList();
    }

    // Fills the available width up to `max`, left-aligned (Stretch + MaxWidth alone would center it).
    private static Grid Capped(FrameworkElement e, double max)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = max });
        g.Children.Add(e);
        return g;
    }

    private ComboBox FontCombo(string? current, Action<string> set)
    {
        var cb = new ComboBox { IsEditable = true, MinWidth = 200, Text = current ?? "" };
        foreach (var n in FontNames()) cb.Items.Add(n);
        string? last = current;
        void Commit() { if (cb.Text != last) { last = cb.Text; set(cb.Text); } }
        cb.LostFocus += (_, _) => Commit();
        cb.DropDownClosed += (_, _) => Commit();
        cb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        return cb;
    }

    // ---------- pages ----------
    private void AppearancePage(StackPanel s)
    {
        var d = App.Settings.Defaults;
        s.Children.Add(Header("Appearance", "Defaults for every profile. A profile can override any of these."));
        s.Children.Add(Preview());
        s.Children.Add(Section("Colors"));
        var schemeRow = new StackPanel { Orientation = Orientation.Horizontal };
        var sw = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        void Swatches() { sw.Children.Clear(); foreach (var c in App.Scheme(App.Settings.Defaults.ColorScheme).Palette16()) sw.Children.Add(new Border { Width = 12, Height = 15, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(App.ParseColor(c)) }); }
        schemeRow.Children.Add(Combo(App.AllSchemes().Select(x => (x.Name, x.Name)), d.ColorScheme, v => { d.ColorScheme = v; Swatches(); Changed(); }));
        schemeRow.Children.Add(sw); Swatches();
        s.Children.Add(Row("Color scheme", schemeRow));
        s.Children.Add(Section("Text"));
        s.Children.Add(Row("Font face", Capped(FontCombo(d.FontFamily, v => { d.FontFamily = v; Changed(); }), 320), "Pick an installed font (monospaced ones first) or type a comma-separated list. Nerd Fonts and Cascadia Code work well."));
        s.Children.Add(Row("Font size", Number(d.FontSize, v => { d.FontSize = v ?? 14; Changed(); }, 6, 72), "Ctrl+= / Ctrl+- zoom a single pane."));
        s.Children.Add(Row("Font weight", Combo(new[] { "normal", "bold", "light", "semibold", "300", "400", "500", "600", "700" }.Select(x => (x, x)), d.FontWeight, v => { d.FontWeight = v; Changed(); })));
        s.Children.Add(Row("Line height", Number(d.LineHeight, v => { d.LineHeight = v ?? 1; Changed(); }, 0.8, 3), "1.0 is tight, 1.2 is airy."));
        s.Children.Add(Row("Letter spacing", Number(d.LetterSpacing, v => { d.LetterSpacing = v ?? 0; Changed(); }, -3, 10)));
        s.Children.Add(Section("Cursor"));
        s.Children.Add(Row("Cursor shape", Combo(new[] { ("bar", "Bar  ▏"), ("block", "Block  █"), ("underline", "Underline  ▁") }, d.CursorStyle, v => { d.CursorStyle = v; Changed(); })));
        s.Children.Add(Row("Blinking cursor", Check(d.CursorBlink == true, v => { d.CursorBlink = v; Changed(); })));
        s.Children.Add(Section("Behavior"));
        s.Children.Add(Row("Padding (px)", Number(d.Padding, v => { d.Padding = v ?? 8; Changed(); }, 0, 60)));
        s.Children.Add(Row("Scrollback lines", Number(d.Scrollback, v => { d.Scrollback = (int)(v ?? 10000); Changed(); }, 100, 200000, "0")));
        s.Children.Add(Row("Copy on select", Check(d.CopyOnSelect == true, v => { d.CopyOnSelect = v; Changed(); })));
        s.Children.Add(Row("Right-click copy / paste", Check(d.RightClickPaste == true, v => { d.RightClickPaste = v; Changed(); }), "Off shows a context menu instead."));
        s.Children.Add(Row("When the shell exits", Combo(new[] { ("graceful", "Close when it exits cleanly"), ("always", "Always close"), ("never", "Keep the pane open") }, d.CloseOnExit, v => { d.CloseOnExit = v; Changed(); })));
        s.Children.Add(Row("Bell", Combo(new[] { ("visual", "Flash"), ("audible", "Sound"), ("all", "Flash and sound"), ("none", "Off") }, d.BellStyle, v => { d.BellStyle = v; Changed(); })));
        var imp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        imp.Children.Add(Btn("Import Windows Terminal color schemes", () => Actions.ImportWindowsTerminalSchemes(_win)));
        s.Children.Add(imp);
    }

    private void BackgroundPage(StackPanel s)
    {
        var d = App.Settings.Defaults;
        s.Children.Add(Header("Background", "Image, transparency and blur behind the text. Ctrl+Shift+B toggles the image; Ctrl+Shift+[ and ] adjust opacity."));
        s.Children.Add(Preview());
        s.Children.Add(Section("Image"));
        s.Children.Add(Row("Background image", FilePicker(d.BackgroundImage, ImageFilter, v => { d.BackgroundImage = v; Changed(); }, @"C:\path\to\wallpaper.jpg or https://…")));
        s.Children.Add(Row("Image sizing", Combo(new[] { ("uniformToFill", "Fill (crop to fit)"), ("uniform", "Fit (letterbox)"), ("fill", "Stretch"), ("none", "Original size"), ("tile", "Tile") }, d.BackgroundImageStretch, v => { d.BackgroundImageStretch = v; Changed(); })));
        s.Children.Add(Row("Image alignment", Combo(new[] { ("center", "Center"), ("top", "Top"), ("bottom", "Bottom"), ("left", "Left"), ("right", "Right"), ("topLeft", "Top left"), ("topRight", "Top right"), ("bottomLeft", "Bottom left"), ("bottomRight", "Bottom right") }, d.BackgroundImageAlignment, v => { d.BackgroundImageAlignment = v; Changed(); })));
        s.Children.Add(Row("Image opacity", Slider_(d.BackgroundImageOpacity ?? 0.25, 0, 1, 0.01, v => { d.BackgroundImageOpacity = v; Changed(); }, v => $"{v * 100:0}%")));
        s.Children.Add(Row("Image blur", Slider_(d.BackgroundImageBlur ?? 0, 0, 30, 1, v => { d.BackgroundImageBlur = v; Changed(); }, v => $"{v:0}px")));
        s.Children.Add(Section("Transparency"));
        s.Children.Add(Row("Background color opacity", Slider_(d.BackgroundOpacity ?? 0.88, 0, 1, 0.01, v => { d.BackgroundOpacity = v; Changed(); }, v => $"{v * 100:0}%"), "Below 100% lets the window material (acrylic / mica) or your desktop show through."));
    }

    private void WindowPage(StackPanel s)
    {
        var w = App.Settings.Window;
        s.Children.Add(Header("Window"));
        s.Children.Add(Row("Window opacity", Slider_(w.Opacity, 0.2, 1, 0.01, v => { w.Opacity = v; App.SettingsChanged(); }, v => $"{v * 100:0}%"), "Fades the whole window, text included."));
        s.Children.Add(Row("Window material", Combo(new[] { ("acrylic", "Acrylic — blurred glass"), ("mica", "Mica"), ("tabbed", "Mica Alt"), ("none", "None — solid") }, w.Material, v => { w.Material = v; App.SettingsChanged(); }),
            Win32.BackdropSupported ? "Lower the background opacity (Background page) to see the material." : "Acrylic and Mica need Windows 11 22H2 or newer; this PC will use a solid background."));
        s.Children.Add(Row("Quake hotkey", Text(w.QuakeHotkey, v => { w.QuakeHotkey = v.Trim(); App.SettingsChanged(); }, "Ctrl+`"), "A global hotkey that shows or hides PoshTerm from anywhere, e.g. Ctrl+`, Win+`, Ctrl+Alt+T. Leave empty to disable."));
        s.Children.Add(Row("Start maximized", Check(w.StartMaximized, v => { w.StartMaximized = v; App.SettingsChanged(); })));
        s.Children.Add(Row("Confirm closing several shells", Check(w.ConfirmCloseMultiple, v => { w.ConfirmCloseMultiple = v; App.SettingsChanged(); })));
        s.Children.Add(Row("Default profile", Combo(_win.Profiles.Select(p => (p.Id, p.Name)), App.Settings.DefaultProfile, v => { App.Settings.DefaultProfile = v; App.SettingsChanged(); }), "Opened by the + button and Ctrl+Shift+T."));
    }

    private void ProfilesPage(StackPanel s)
    {
        var profiles = _win.Profiles.ToList();
        if (_profileId == null || profiles.All(p => p.Id != _profileId)) _profileId = profiles.FirstOrDefault()?.Id;
        s.Children.Add(Header("Profiles", "Each profile is a shell with its own startup script, folder and look. Detected shells can be edited too."));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120, MaxWidth = 190 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var list = new StackPanel();
        foreach (var p in profiles)
        {
            var b = new Button { HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(0, 1, 6, 1), BorderThickness = new Thickness(0), Background = p.Id == _profileId ? (Brush)Application.Current.Resources["Raised2Brush"] : Brushes.Transparent };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = p.Icon ?? "\uE756", FontFamily = (FontFamily)Application.Current.Resources["IconFont"], Width = 22 });
            sp.Children.Add(new TextBlock { Text = p.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120 });
            if (p.Id == App.Settings.DefaultProfile) sp.Children.Add(new TextBlock { Text = "  default", FontSize = 10, Foreground = (Brush)Application.Current.Resources["AccentBrush"], VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
            var id = p.Id;
            b.Click += (_, _) => { _profileId = id; Render(); };
            list.Children.Add(b);
        }
        list.Children.Add(new Border { Height = 8 });
        list.Children.Add(Btn("+ New profile", () =>
        {
            var id = "custom-" + DateTime.Now.Ticks.ToString("x");
            App.Settings.Profiles.Add(new Profile { Id = id, Name = "New profile", Command = "posh", Icon = "\uE734", Source = "custom" });
            _profileId = id; App.SettingsChanged(); Render();
        }));
        var editor = new StackPanel();
        Grid.SetColumn(editor, 1);
        var cur = profiles.FirstOrDefault(p => p.Id == _profileId);
        if (cur != null) ProfileEditor(editor, cur);
        grid.Children.Add(list); grid.Children.Add(editor);
        s.Children.Add(grid);
    }

    private Profile Override(string id)
    {
        var e = App.Settings.Profiles.FirstOrDefault(p => p.Id == id);
        if (e == null) { e = new Profile { Id = id, Name = "", Command = "" }; App.Settings.Profiles.Add(e); }
        return e;
    }

    private void ProfileEditor(StackPanel ed, Profile cur)
    {
        // read-only view of the user's override; one is only created when something is actually changed
        // (just opening a profile used to write an empty override entry into settings.json)
        var o = App.Settings.Profiles.FirstOrDefault(p => p.Id == cur.Id) ?? new Profile();
        void Set(Action<Profile> a) { a(Override(cur.Id)); App.SettingsChanged(); }
        void SetLook(Action<Profile> a) { a(Override(cur.Id)); Changed(); }
        bool detected = cur.Source is "built-in" or "detected" or "old_shell";

        ed.Children.Add(Section("General"));
        ed.Children.Add(Row("Name", Text(cur.Name, v => { Set(p => p.Name = v); Render(); })));
        ed.Children.Add(Row("Icon", Text(cur.Icon, v => { Set(p => p.Icon = v); }, "any emoji or character"), "Segoe Fluent Icons glyphs or an emoji."));
        ed.Children.Add(Row("Command", cur.Command == "posh" ? Text("posh", v => Set(p => p.Command = v)) : FilePicker(cur.Command, ExeFilter, v => Set(p => p.Command = v)), "\"posh\" is the built-in shell; or point at pwsh.exe, cmd.exe, bash.exe, wsl.exe, …"));
        ed.Children.Add(Row("Arguments", Text(cur.Args, v => Set(p => p.Args = v), "-NoLogo")));
        ed.Children.Add(Row("Starting directory", Text(cur.Cwd, v => Set(p => p.Cwd = v), @"~  or  %USERPROFILE%\Projects")));
        ed.Children.Add(Section("Startup"));
        ed.Children.Add(Row("Startup script", FilePicker(cur.StartupScript, ScriptFilter, v => Set(p => p.StartupScript = v), @"C:\scripts\init.posh"), "Runs inside the shell each time the profile starts: dot-sourced in posh and PowerShell, `call` in cmd, `source` in bash and WSL."));
        var cmds = new TextBox { Text = cur.StartupCommands ?? "", AcceptsReturn = true, MinHeight = 74, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void CommitCmds() { if (cmds.Text != (cur.StartupCommands ?? "")) Set(p => p.StartupCommands = cmds.Text); }
        cmds.LostFocus += (_, _) => CommitCmds();
        cmds.Unloaded += (_, _) => CommitCmds();
        ed.Children.Add(Row("Startup commands", cmds, "Run after the startup script, one per line."));
        var env = new TextBox { Text = string.Join("\n", (cur.Env ?? new()).Select(kv => $"{kv.Key}={kv.Value}")), AcceptsReturn = true, MinHeight = 60, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var envText = env.Text;
        void CommitEnv()
        {
            if (env.Text == envText) return;
            envText = env.Text;
            var dict = new Dictionary<string, string>();
            foreach (var line in env.Text.Split('\n')) { var i = line.IndexOf('='); if (i > 0) dict[line[..i].Trim()] = line[(i + 1)..].TrimEnd('\r'); }
            Set(p => p.Env = dict.Count > 0 ? dict : null);
        }
        env.LostFocus += (_, _) => CommitEnv();
        env.Unloaded += (_, _) => CommitEnv();
        ed.Children.Add(Row("Environment variables", env, "KEY=VALUE per line; %VAR% references are expanded."));

        ed.Children.Add(Section("Look — leave empty to use the defaults"));
        var def = App.Settings.Defaults;
        ed.Children.Add(Row("Color scheme", Combo(new[] { ("", "(default)") }.Concat(App.AllSchemes().Select(x => (x.Name, x.Name))), cur.ColorScheme ?? "", v => SetLook(p => p.ColorScheme = v.Length == 0 ? null : v))));
        ed.Children.Add(Row("Font face", Capped(FontCombo(o.FontFamily ?? "", v => SetLook(p => p.FontFamily = v.Length == 0 ? null : v)), 320)));
        ed.Children.Add(Row("Font size", Number(o.FontSize, v => SetLook(p => p.FontSize = v), 6, 72)));
        ed.Children.Add(Row("Cursor shape", Combo(new[] { ("", "(default)"), ("bar", "Bar"), ("block", "Block"), ("underline", "Underline") }, o.CursorStyle ?? "", v => SetLook(p => p.CursorStyle = v.Length == 0 ? null : v))));
        ed.Children.Add(Row("Background image", FilePicker(o.BackgroundImage, ImageFilter, v => SetLook(p => p.BackgroundImage = v.Length == 0 ? null : v))));
        ed.Children.Add(Row("Background opacity", Number(o.BackgroundOpacity, v => SetLook(p => p.BackgroundOpacity = v), 0, 1)));
        ed.Children.Add(Row("Image opacity", Number(o.BackgroundImageOpacity, v => SetLook(p => p.BackgroundImageOpacity = v), 0, 1)));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        row.Children.Add(Btn("Open in new tab", () => { _win.HideOverlay(); _win.NewTab(cur.Id); }, true));
        row.Children.Add(Btn("Set as default", () => { App.Settings.DefaultProfile = cur.Id; App.SettingsChanged(); Render(); }));
        row.Children.Add(Btn("Duplicate", () =>
        {
            var id = "custom-" + DateTime.Now.Ticks.ToString("x");
            var copy = new Profile { Id = id, Name = cur.Name + " (copy)", Command = cur.Command, Args = cur.Args, Cwd = cur.Cwd, Icon = cur.Icon, StartupScript = cur.StartupScript, StartupCommands = cur.StartupCommands, Env = cur.Env == null ? null : new(cur.Env), Source = "custom" };
            App.Settings.Profiles.Add(copy); _profileId = id; App.SettingsChanged(); Render();
        }));
        row.Children.Add(Btn(detected ? "Hide" : "Delete", () =>
        {
            if (_win.Profiles.Count <= 1) { _win.Toast("Keep at least one profile"); return; }
            App.Settings.Profiles.RemoveAll(p => p.Id == cur.Id);
            if (detected) App.Settings.HiddenProfiles.Add(cur.Id);
            if (App.Settings.DefaultProfile == cur.Id) App.Settings.DefaultProfile = _win.Profiles.First(p => p.Id != cur.Id).Id;
            _profileId = null; App.SettingsChanged(); Render();
        }));
        ed.Children.Add(row);
        if (App.Settings.HiddenProfiles.Count > 0)
            ed.Children.Add(new StackPanel { Margin = new Thickness(0, 8, 0, 0), Children = { Btn("Show hidden shells", () => { App.Settings.HiddenProfiles.Clear(); App.SettingsChanged(); Render(); }) } });
    }

    // ---------- keyboard page ----------
    private void KeyboardPage(StackPanel s)
    {
        s.Children.Add(Header("Keyboard", "Every action can be bound to any key combination. Ctrl+C only copies when text is selected; otherwise it reaches the shell."));
        if (_recording != null) s.Children.Add(new TextBlock { Text = "Press the new key combination…  (Esc to cancel)", Foreground = (Brush)Application.Current.Resources["DangerBrush"], Margin = new Thickness(0, 4, 0, 8), FontWeight = FontWeights.SemiBold });
        var all = new List<(string id, string label)>();
        foreach (var (id, def) in Actions.Table) all.Add((id, def.Label));
        for (int i = 0; i < _win.Profiles.Count; i++) { var p = _win.Profiles[i]; all.Add(($"newTabProfile:{p.Id}", $"New tab: {p.Name}")); all.Add(($"splitProfile:{p.Id}", $"Split with: {p.Name}")); }
        foreach (var sc in App.AllSchemes()) all.Add(($"setScheme:{sc.Name}", $"Color scheme: {sc.Name}"));

        var table = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        table.ColumnDefinitions.Add(new ColumnDefinition()); table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) }); table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int r = 0;
        foreach (var kv in _win.Keymap.OrderBy(k => Actions.Find(_win, k.Value)?.Label ?? k.Value))
        {
            table.RowDefinitions.Add(new RowDefinition());
            var label = new TextBlock { Text = Actions.Find(_win, kv.Value)?.Label ?? kv.Value + " (unknown action)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 0, 5) };
            var chordText = kv.Key.ToString();
            var kbd = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["BorderBrush2"], Background = (Brush)Application.Current.Resources["RaisedBrush"], Child = new TextBlock { Text = chordText, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 } };
            if (_recording?.oldChord == chordText) kbd.BorderBrush = (Brush)Application.Current.Resources["AccentBrush"];
            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            var chordCopy = kv.Key; var action = kv.Value;
            btns.Children.Add(Btn("Change", () => { _recording = (chordCopy.ToString(), action); Render(); }));
            btns.Children.Add(Btn("Remove", () => RemoveBinding(chordCopy)));
            Grid.SetRow(label, r); Grid.SetRow(kbd, r); Grid.SetRow(btns, r);
            Grid.SetColumn(kbd, 1); Grid.SetColumn(btns, 2);
            table.Children.Add(label); table.Children.Add(kbd); table.Children.Add(btns);
            r++;
        }
        var add = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var pick = Combo(all.Select(a => (a.id, a.label)), "newTab", _ => { });
        pick.MinWidth = 320;
        add.Children.Add(pick);
        add.Children.Add(new Border { Width = 10 });
        add.Children.Add(Btn("Add binding…", () => { if (pick.SelectedItem is ComboBoxItem it) { _recording = (null, (string)it.Tag); Render(); } }, true));
        add.Children.Add(Btn("Reset all to defaults", () => { App.Settings.Keybindings.Clear(); App.SettingsChanged(); Render(); }));
        s.Children.Add(add);
        s.Children.Add(table);
    }

    private void RemoveBinding(Chord c)
    {
        App.Settings.Keybindings.Remove(c.ToString());
        foreach (var k in App.Settings.Keybindings.Keys.ToList()) if (Chord.TryParse(k, out var x) && x == c) App.Settings.Keybindings.Remove(k);
        App.Settings.Keybindings[c.ToString()] = "unbound";
        App.SettingsChanged(); Render();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_recording is not { } rec) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { _recording = null; Render(); return; }
        var chord = Chord.From(e);
        if (rec.oldChord != null && Chord.TryParse(rec.oldChord, out var old) && old != chord)
        {
            foreach (var k in App.Settings.Keybindings.Keys.ToList()) if (Chord.TryParse(k, out var x) && x == old) App.Settings.Keybindings.Remove(k);
            App.Settings.Keybindings[old.ToString()] = "unbound";
        }
        App.Settings.Keybindings[chord.ToString()] = rec.action;
        _recording = null;
        App.SettingsChanged();
        _win.Toast($"{chord} → {Actions.Find(_win, rec.action)?.Label ?? rec.action}");
        Render();
    }

    // ---------- about ----------
    private void AboutPage(StackPanel s)
    {
        s.Children.Add(Header("PoshTerm 1.0.0", "A multi-shell terminal for Windows, with its own built-in shell: posh."));
        s.Children.Add(Row("Settings file", new TextBox { Text = SettingsStore.FilePath, IsReadOnly = true, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left }));
        s.Children.Add(Row("Shells found", new TextBlock { Text = string.Join(", ", _win.Profiles.Select(p => p.Name)), TextWrapping = TextWrapping.Wrap }));
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        row.Children.Add(Btn("Open settings.json", Actions.OpenSettingsFile));
        row.Children.Add(Btn("Open data folder", () => Process.Start(new ProcessStartInfo(SettingsStore.Dir) { UseShellExecute = true })));
        row.Children.Add(Btn("Re-detect shells", () => { Shells.Detect(refresh: true); App.SettingsChanged(false); _win.Toast("Shells re-detected"); Render(); }));
        row.Children.Add(Btn("Import Windows Terminal color schemes", () => Actions.ImportWindowsTerminalSchemes(_win)));
        row.Children.Add(Btn("Reset all settings", () =>
        {
            if (MessageBox.Show(_win, "Reset all PoshTerm settings to their defaults?", "PoshTerm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var fresh = new AppSettings();
            foreach (var pr in typeof(AppSettings).GetProperties().Where(p => p.CanWrite)) pr.SetValue(App.Settings, pr.GetValue(fresh));
            App.SettingsChanged(); Render();
        }));
        s.Children.Add(row);
        s.Children.Add(Section("posh quick reference"));
        s.Children.Add(new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Text = "Get-ChildItem | Where-Object Length -gt 1mb | Sort-Object Length -Descending\n" +
                   "Get-Process | Sort-Object WorkingSet -Descending | Select-Object -First 5\n" +
                   "irm https://api.github.com/repos/PowerShell/PowerShell | Select-Object name, stargazers_count\n" +
                   "$PROFILE   →  ~/.posh/profile.posh   (runs at every posh start)\n" +
                   "help   Get-Command   Get-Help <command>",
        });
    }
}
