using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using PoshTerm.Config;
using PoshTerm.Core;

namespace PoshTerm.UI;

// A split node: two children separated by a draggable splitter.
public sealed class SplitGrid : Grid
{
    public Orientation Dir { get; }
    public UIElement A { get; set; }
    public UIElement B { get; set; }

    public SplitGrid(Orientation dir, UIElement a, UIElement b)
    {
        Dir = dir; A = a; B = b;
        bool side = dir == Orientation.Horizontal;   // Horizontal = panes side by side
        var splitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext, ResizeDirection = side ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            Background = (Brush)Application.Current.Resources["BorderBrush2"], Focusable = false,
            Cursor = side ? Cursors.SizeWE : Cursors.SizeNS,
        };
        if (side)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });
            SetColumn(splitter, 1); SetColumn(b, 2);
        }
        else
        {
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 40 });
            SetRow(splitter, 1); SetRow(b, 2);
        }
        Children.Add(a); Children.Add(splitter); Children.Add(b);
    }

    public void Slot(UIElement old, UIElement @new)
    {
        int col = GetColumn(old), row = GetRow(old);
        Children.Remove(old);
        SetColumn(@new, col); SetRow(@new, row);
        Children.Add(@new);
        if (ReferenceEquals(A, old)) A = @new; else B = @new;
    }

    // Move the divider by a fraction of the total size.
    public void Nudge(double delta)
    {
        if (Dir == Orientation.Horizontal)
        {
            double a = ColumnDefinitions[0].ActualWidth, b = ColumnDefinitions[2].ActualWidth, t = Math.Max(1, a + b);
            double na = Math.Clamp(a + delta * t, 60, t - 60);
            ColumnDefinitions[0].Width = new GridLength(na, GridUnitType.Star); ColumnDefinitions[2].Width = new GridLength(t - na, GridUnitType.Star);
        }
        else
        {
            double a = RowDefinitions[0].ActualHeight, b = RowDefinitions[2].ActualHeight, t = Math.Max(1, a + b);
            double na = Math.Clamp(a + delta * t, 40, t - 40);
            RowDefinitions[0].Height = new GridLength(na, GridUnitType.Star); RowDefinitions[2].Height = new GridLength(t - na, GridUnitType.Star);
        }
    }
}

public sealed class TabModel
{
    public Border Host { get; } = new() { Visibility = Visibility.Collapsed };
    public TabHeader Header { get; }
    public PaneView? Active { get; set; }
    public string? CustomTitle { get; set; }

    // maximized-pane state: the real layout tree is parked while one pane fills the tab
    public PaneView? Zoomed { get; set; }
    public Border? ZoomHolder { get; set; }
    public UIElement? ParkedRoot { get; set; }

    private readonly List<PaneView> _all = new();
    public IReadOnlyList<PaneView> AllPanes => _all;
    public void Track(PaneView p) => _all.Add(p);
    public void Untrack(PaneView p) => _all.Remove(p);

    public TabModel(TabHeader header) { Header = header; }
}

public sealed partial class MainWindow : Window
{
    public static MainWindow? Current { get; private set; }
    private static bool _hotkeyOwnerAssigned;
    private bool _ownsHotkey;

    private readonly List<TabModel> _tabs = new();
    private TabModel? _active;
    private Dictionary<Chord, string> _keymap = new();
    private List<Profile> _profiles = new();
    private bool _backdropActive;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Current = this;
        InstallBrushes();
        _profiles = App.Profiles;
        RestoreBounds_();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            ApplySettings();
            if (App.SettingsError != null) Toast(App.SettingsError, 6000);
            NewTab(App.Settings.DefaultProfile);
            App.TrimMemorySoon(4000);   // after the first shell has started and drawn its prompt
        };
        Activated += (_, _) => Current = this;
        PreviewKeyDown += OnWindowKey;
        StateChanged += (_, _) => OnStateChanged();
        Closing += OnClosing;
        Closed += (_, _) => { foreach (var t in _tabs.ToList()) foreach (var p in t.AllPanes.ToList()) p.Close(); if (_ownsHotkey) Win32.UnregisterGlobalHotkey(this); };

        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        NewTabButton.Click += (_, _) => NewTab();
        TabMenuButton.Click += (_, _) => ShowProfileMenu(TabMenuButton);
        TitleBar.MouseDown += (_, e) => { if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left && e.OriginalSource is not Button) MaxButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
        InitOverlays();
    }

    // ---------- chrome / theming ----------
    private void InstallBrushes()
    {
        var r = Application.Current.Resources;
        SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        r["FgBrush"] = B(Color.FromRgb(212, 216, 224)); r["MutedBrush"] = B(Color.FromArgb(160, 212, 216, 224));
        r["AccentBrush"] = B(Color.FromRgb(108, 168, 247)); r["ChromeBrush"] = B(Color.FromArgb(160, 16, 18, 24));
        r["ChromeSolidBrush"] = B(Color.FromRgb(24, 27, 35)); r["RaisedBrush"] = B(Color.FromArgb(18, 255, 255, 255));
        r["Raised2Brush"] = B(Color.FromArgb(34, 255, 255, 255)); r["BorderBrush2"] = B(Color.FromArgb(40, 255, 255, 255));
        r["DangerBrush"] = B(Color.FromRgb(240, 113, 122));
    }

    private static Color Parse(string hex) { try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Colors.Black; } }
    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
    private static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public void ApplySettings()
    {
        var s = App.Settings;
        _profiles = App.Profiles;
        _keymap = DefaultKeys.Resolve(s.Keybindings);

        foreach (var t in _tabs)
            foreach (var p in t.AllPanes)
                p.ApplyAppearance(s, _profiles.FirstOrDefault(x => x.Id == p.Profile.Id) ?? p.Profile);

        var scheme = App.Scheme(_active?.Active?.Profile.ColorScheme ?? s.Defaults.ColorScheme);
        if (_active?.Active != null) scheme = App.Scheme(_active.Active.Profile.Over(s.Defaults).ColorScheme);
        var bg = Parse(scheme.Background);
        bool dark = Luma(bg) < 0.55;
        App.ApplyControlTheme(dark);

        var fg = Parse(scheme.Foreground);
        var r = Application.Current.Resources;
        SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        // replacing an app resource re-resolves every DynamicResource in every window; skip it when nothing changed
        void Set(string key, Color c) { if (r[key] is not SolidColorBrush old || old.Color != c) r[key] = B(c); }
        var tint = dark ? Colors.White : Colors.Black;
        bool wantBackdrop = s.Window.Material != "none";
        if (IsLoaded || new WindowInteropHelper(this).Handle != IntPtr.Zero)
        {
            _backdropActive = wantBackdrop && Win32.SetBackdrop(this, s.Window.Material);
            if (!wantBackdrop) Win32.SetBackdrop(this, "none");
            Win32.SetDarkTitle(this, dark);
            Win32.SetWindowOpacity(this, s.Window.Opacity);
        }
        var winBg = _backdropActive ? Colors.Transparent : bg;
        if (Background is not SolidColorBrush cur || cur.Color != winBg) Background = _backdropActive ? Brushes.Transparent : B(bg);
        Set("FgBrush", fg); Set("MutedBrush", Color.FromArgb(165, fg.R, fg.G, fg.B));
        Set("AccentBrush", Parse(scheme.BrightBlue));
        var chrome = Mix(bg, tint, 0.04);
        Set("ChromeBrush", Color.FromArgb((byte)(_backdropActive ? 150 : 255), chrome.R, chrome.G, chrome.B));
        Set("ChromeSolidBrush", Mix(bg, tint, 0.06));
        Set("RaisedBrush", Color.FromArgb(dark ? (byte)18 : (byte)14, tint.R, tint.G, tint.B));
        Set("Raised2Brush", Color.FromArgb(dark ? (byte)36 : (byte)26, tint.R, tint.G, tint.B));
        Set("BorderBrush2", Color.FromArgb(dark ? (byte)46 : (byte)40, tint.R, tint.G, tint.B));
        Set("DangerBrush", Parse(scheme.BrightRed));
        foreach (var t in _tabs) t.Header.Refresh(t == _active);
        if (_ownsHotkey || !_hotkeyOwnerAssigned) TryRegisterHotkey();
        UpdateTitle();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;   // let the DWM backdrop show through
        src.AddHook(WndProc);
        TryRegisterHotkey();
    }

    private void TryRegisterHotkey()
    {
        if (_hotkeyOwnerAssigned && !_ownsHotkey) return;
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        bool ok = Win32.RegisterGlobalHotkey(this, App.Settings.Window.QuakeHotkey);
        _ownsHotkey = ok || (_ownsHotkey);
        if (ok) _hotkeyOwnerAssigned = true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY) { ToggleVisibility(); handled = true; }
        // not marked handled: WPF still fills in the min/max track sizes from MinWidth/MinHeight
        else if (msg == Win32.WM_GETMINMAXINFO) Win32.FitMaximized(hwnd, lParam, _fullscreen);
        return IntPtr.Zero;
    }

    private void ToggleVisibility()
    {
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) Hide();
        else Win32.Activate(this);
    }

    private void OnStateChanged()
    {
        // restored some other way (Win+Down, taskbar, double-click) while full screen: leave full-screen mode
        if (_fullscreen && !_fullscreenSwitching && WindowState == WindowState.Normal) SetFullscreen(false, restoreState: false);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    // ---------- persisted window bounds ----------
    private static string StateFile => Path.Combine(SettingsStore.Dir, "window-state.json");
    private void RestoreBounds_()
    {
        try
        {
            var st = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(StateFile));
            if (st == null) return;
            var area = SystemParameters.WorkArea;
            if (st["w"] >= 420 && st["h"] >= 240 && st["w"] <= area.Width + 20 && st["h"] <= area.Height + 20)
            {
                Width = st["w"]; Height = st["h"];
                if (st["x"] > area.Left - 50 && st["x"] < area.Right - 100 && st["y"] > area.Top - 20 && st["y"] < area.Bottom - 100)
                { WindowStartupLocation = WindowStartupLocation.Manual; Left = st["x"]; Top = st["y"]; }
            }
            if (st.TryGetValue("max", out var m) && m > 0) Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
        catch { /* first run */ }
        if (App.Settings.Window.StartMaximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        try
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            Directory.CreateDirectory(SettingsStore.Dir);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new Dictionary<string, double> { ["x"] = b.X, ["y"] = b.Y, ["w"] = b.Width, ["h"] = b.Height, ["max"] = WindowState == WindowState.Maximized ? 1 : 0 }));
        }
        catch { }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        int shells = _tabs.Sum(t => t.AllPanes.Count);
        if (shells > 1 && App.Settings.Window.ConfirmCloseMultiple &&
            MessageBox.Show(this, $"Close {_tabs.Count} tab(s) with {shells} running shells?", "PoshTerm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        _closing = true;
        SaveBounds();
    }

    // ---------- tabs ----------
    public Profile ProfileById(string? id) =>
        _profiles.FirstOrDefault(p => p.Id == id) ?? _profiles.FirstOrDefault(p => p.Id == App.Settings.DefaultProfile) ?? _profiles[0];

    public TabModel NewTab(string? profileId = null, string? cwd = null)
    {
        var header = new TabHeader();
        var tab = new TabModel(header);
        header.Activate += () => ActivateTab(tab);
        header.CloseRequested += () => CloseTab(tab);
        header.RenameRequested += () => RenameTab(tab);
        header.Moved += (from, to) => ReorderTab(tab, to);
        header.ContextRequested += () => ShowTabMenu(tab, header);
        var pane = CreatePane(tab, ProfileById(profileId), cwd);
        tab.Host.Child = pane;
        tab.Active = pane;
        _tabs.Add(tab);
        TabStrip.Children.Add(header);
        ContentHost.Children.Add(tab.Host);
        ActivateTab(tab);
        return tab;
    }

    private PaneView CreatePane(TabModel tab, Profile profile, string? cwd)
    {
        var pane = new PaneView(profile, cwd);
        pane.ApplyAppearance(App.Settings, profile);
        tab.Track(pane);
        pane.Focused += p => { tab.Active = p; foreach (var x in tab.AllPanes) x.SetActive(x == p, tab.AllPanes.Count > 1); if (_active == tab) UpdateTitle(); tab.Header.Refresh(_active == tab); };
        pane.TitleChanged += _ => { tab.Header.Title = tab.CustomTitle ?? DisplayTitle(tab); if (_active == tab) UpdateTitle(); };
        pane.ExitedAndClose += p => ClosePane(p);
        pane.ProgressChanged += (p, st, pct) => { if (tab.Active == p) tab.Header.SetProgress(st, pct); };
        pane.BellRang += p => { if (_active != tab) tab.Header.Bell(); };
        return pane;
    }

    private string DisplayTitle(TabModel t)
    {
        var p = t.Active;
        if (p == null) return "PoshTerm";
        return !string.IsNullOrWhiteSpace(p.Title) ? p.Title : p.Profile.Name;
    }

    public void ActivateTab(TabModel tab)
    {
        _active = tab;
        foreach (var t in _tabs) { t.Host.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed; t.Header.Refresh(t == tab); }
        foreach (var x in tab.AllPanes) x.SetActive(x == tab.Active, tab.AllPanes.Count > 1);
        tab.Header.BringIntoView_(TabScroller);
        UpdateTitle();
        ApplyChromeForActive();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => tab.Active?.Term.Focus());
    }

    private void ApplyChromeForActive()
    {
        // recolor the chrome when the active tab uses a different scheme
        var scheme = _active?.Active != null ? App.Scheme(_active.Active.Profile.Over(App.Settings.Defaults).ColorScheme) : null;
        if (scheme == null) return;
        if (_lastChromeScheme == scheme.Name) return;
        _lastChromeScheme = scheme.Name;
        ApplySettings();
    }
    private string? _lastChromeScheme;

    private void UpdateTitle()
    {
        if (_active == null) return;
        var t = _active.CustomTitle ?? DisplayTitle(_active);
        _active.Header.Title = t;
        _active.Header.IconGlyph = _active.Active?.Profile.Icon;
        Title = t + " — PoshTerm";
    }

    public void CloseTab(TabModel tab)
    {
        foreach (var p in tab.AllPanes.ToList()) p.Close();
        int i = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.Header);
        ContentHost.Children.Remove(tab.Host);
        tab.Host.Child = null;
        if (_tabs.Count == 0) { Close(); return; }
        App.TrimMemorySoon();   // a closed tab can drop megabytes of scrollback
        if (_active == tab) ActivateTab(_tabs[Math.Min(i, _tabs.Count - 1)]);
    }

    private void ReorderTab(TabModel tab, int newIndex)
    {
        newIndex = Math.Clamp(newIndex, 0, _tabs.Count - 1);
        _tabs.Remove(tab); _tabs.Insert(newIndex, tab);
        TabStrip.Children.Remove(tab.Header); TabStrip.Children.Insert(newIndex, tab.Header);
    }

    private void RenameTab(TabModel tab) => tab.Header.BeginRename(tab.CustomTitle ?? tab.Header.Title, name =>
    {
        tab.CustomTitle = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        UpdateTitle(); tab.Header.Title = tab.CustomTitle ?? DisplayTitle(tab);
        tab.Active?.Term.Focus();
    });

    // ---------- panes ----------
    private static Panel? ParentPanel(UIElement e) => e is FrameworkElement fe ? fe.Parent as Panel : null;

    private void Replace(TabModel tab, UIElement old, UIElement @new)
    {
        var parent = (old as FrameworkElement)?.Parent;
        if (parent is SplitGrid g) g.Slot(old, @new);
        else if (parent is Border b) { b.Child = null; b.Child = @new; }
    }

    public void Split(Orientation dir, string? profileId = null)
    {
        var tab = _active; var target = tab?.Active;
        if (tab == null || target == null) return;
        if (tab.Zoomed != null) ToggleZoom();
        var np = CreatePane(tab, profileId == null ? target.Profile : ProfileById(profileId), target.Cwd);
        var holder = new Border();                       // temporary slot while re-parenting
        Replace(tab, target, holder);
        var split = new SplitGrid(dir, target, np);
        Replace(tab, holder, split);
        tab.Active = np;
        foreach (var x in tab.AllPanes) x.SetActive(x == np, true);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => np.Term.Focus());
    }

    public void SplitAuto(string? profileId = null)
    {
        var p = _active?.Active; if (p == null) return;
        Split(p.ActualWidth >= p.ActualHeight * 1.6 ? Orientation.Horizontal : Orientation.Vertical, profileId);
    }

    public void ClosePane(PaneView pane)
    {
        var tab = _tabs.FirstOrDefault(t => t.AllPanes.Contains(pane));
        if (tab == null) return;
        if (tab.Zoomed != null) ToggleZoom(tab);
        if (tab.AllPanes.Count == 1) { CloseTab(tab); return; }
        var parent = pane.Parent as SplitGrid;
        if (parent == null) { CloseTab(tab); return; }
        var sibling = ReferenceEquals(parent.A, pane) ? parent.B : parent.A;
        parent.Children.Remove(sibling);
        parent.Children.Remove(pane);
        Replace(tab, parent, sibling);
        pane.Close();
        tab.Untrack(pane);
        App.TrimMemorySoon();
        var next = FirstPane(sibling);
        tab.Active = next;
        foreach (var x in tab.AllPanes) x.SetActive(x == next, tab.AllPanes.Count > 1);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => next?.Term.Focus());
    }

    private static PaneView? FirstPane(UIElement e) => e is PaneView p ? p : e is SplitGrid g ? FirstPane(g.A) : null;

    public void ToggleZoom(TabModel? t = null)
    {
        var tab = t ?? _active; if (tab == null) return;
        if (tab.Zoomed is { } z)
        {
            tab.Host.Child = null;                                   // detach the zoomed pane
            Replace(tab, tab.ZoomHolder!, z);                        // back into its slot in the parked tree
            tab.Host.Child = tab.ParkedRoot;
            tab.Zoomed = null; tab.ZoomHolder = null; tab.ParkedRoot = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => z.Term.Focus());
            return;
        }
        if (tab.AllPanes.Count < 2 || tab.Active == null) return;
        var pane = tab.Active;
        var holder = new Border();
        Replace(tab, pane, holder);
        var root = tab.Host.Child;
        tab.Host.Child = null;
        tab.Host.Child = pane;
        tab.Zoomed = pane; tab.ZoomHolder = holder; tab.ParkedRoot = root;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => pane.Term.Focus());
    }

    public void FocusDirection(string dir)
    {
        var tab = _active; var cur = tab?.Active; if (tab == null || cur == null || tab.Zoomed != null) return;
        Rect R(PaneView p) => new(p.TranslatePoint(new Point(0, 0), ContentHost), new Size(p.ActualWidth, p.ActualHeight));
        var c = R(cur); var cc = new Point(c.X + c.Width / 2, c.Y + c.Height / 2);
        PaneView? best = null; double bestScore = double.MaxValue;
        foreach (var p in tab.AllPanes)
        {
            if (p == cur) continue;
            var r = R(p); var pc = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            bool ok = dir switch { "left" => r.Right <= c.Left + 4, "right" => r.Left >= c.Right - 4, "up" => r.Bottom <= c.Top + 4, _ => r.Top >= c.Bottom - 4 };
            if (!ok) continue;
            bool horiz = dir is "left" or "right";
            double score = (horiz ? Math.Abs(pc.X - cc.X) : Math.Abs(pc.Y - cc.Y)) + 2 * (horiz ? Math.Abs(pc.Y - cc.Y) : Math.Abs(pc.X - cc.X));
            if (score < bestScore) { bestScore = score; best = p; }
        }
        if (best != null) best.Term.Focus();
    }

    public void ResizeDirection(string dir)
    {
        var tab = _active; if (tab?.Active == null) return;
        var want = dir is "left" or "right" ? Orientation.Horizontal : Orientation.Vertical;
        FrameworkElement node = tab.Active;
        while (node.Parent is SplitGrid g)
        {
            if (g.Dir == want) { g.Nudge(dir is "right" or "down" ? 0.05 : -0.05); return; }
            node = g;
        }
    }

    // ---------- keyboard ----------
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (OverlayHost.Visibility == Visibility.Visible)
        {
            // overlays own their input; Escape dismisses them (unless the keyboard page is recording a binding)
            if (e.Key == Key.Escape && !SettingsPanel.RecordingKeys) { HideOverlay(); e.Handled = true; }
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        // any text box (find bar, tab rename) keeps its own keys: Ctrl+V, Ctrl+C, Ctrl+A... must not hit the terminal
        if (Keyboard.FocusedElement is TextBox) return;
        var chord = Chord.From(e);
        if (_keymap.TryGetValue(chord, out var action) && Run(action)) e.Handled = true;
    }

    public bool Run(string action) => Actions.Run(this, action);

    // exposed for Actions
    public IReadOnlyList<TabModel> Tabs => _tabs;
    public TabModel? ActiveTab => _active;
    public PaneView? ActivePane => _active?.Active;
    public IReadOnlyList<Profile> Profiles => _profiles;
    public Dictionary<Chord, string> Keymap => _keymap;
    public void RenameActiveTab() { if (_active != null) RenameTab(_active); }

    public void ToggleFullscreen() => SetFullscreen(!_fullscreen, restoreState: true);

    // Full screen = maximized over the whole monitor (WM_GETMINMAXINFO switches from the work area to the
    // monitor rect) with the title bar row removed, so the terminal grid grows into the freed space.
    private void SetFullscreen(bool on, bool restoreState)
    {
        if (on == _fullscreen) return;
        if (on) _prevState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _fullscreen = on;
        TitleBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        // no caption drag area or resize border over the terminal while full screen
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = on ? 0 : 38, ResizeBorderThickness = new Thickness(on ? 0 : 6), GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false, NonClientFrameEdges = NonClientFrameEdges.None,
        });
        if (!restoreState) return;
        // re-maximize so Windows asks for the new maximized bounds
        _fullscreenSwitching = true;
        try
        {
            WindowState = WindowState.Normal;
            WindowState = on || _prevState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
        finally { _fullscreenSwitching = false; }
    }
    private bool _fullscreen, _fullscreenSwitching; private WindowState _prevState;
}
