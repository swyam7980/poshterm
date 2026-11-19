using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PoshTerm.Config;

namespace PoshTerm.UI;

public sealed partial class MainWindow
{
    private DispatcherTimer? _toastTimer;
    private Action? _overlayClosed;

    private void InitOverlays()
    {
        OverlayHost.MouseDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, OverlayHost)) HideOverlay(); };
        OverlayHost.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !SettingsPanel.RecordingKeys) { HideOverlay(); e.Handled = true; } };

        FindBox.TextChanged += (_, _) => RunFind();
        FindBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
        };
        FindNext.Click += (_, _) => FindStep(1);
        FindPrev.Click += (_, _) => FindStep(-1);
        FindClose.Click += (_, _) => CloseFind();
        FindCase.Click += (_, _) => RunFind();
        FindRegex.Click += (_, _) => RunFind();
    }

    public void Toast(string text, int ms = 2200)
    {
        ToastText.Text = text;
        ToastHost.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastHost.Visibility = Visibility.Collapsed; };
        _toastTimer.Start();
    }

    // ---------- generic overlay ----------
    public void ShowOverlay(UIElement content, Action? onClosed = null, VerticalAlignment v = VerticalAlignment.Top)
    {
        OverlayHost.Children.Clear();
        if (content is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = v; }
        OverlayHost.Children.Add(content);
        _overlayClosed = onClosed;
        OverlayHost.Visibility = Visibility.Visible;
        // move keyboard focus off the terminal so typing and Escape reach the overlay
        if (content is FrameworkElement el)
        {
            el.Focusable = true;
            // Tab cycles inside the overlay instead of escaping to the terminal behind it (typing would reach the shell)
            KeyboardNavigation.SetTabNavigation(el, KeyboardNavigationMode.Cycle);
            KeyboardNavigation.SetControlTabNavigation(el, KeyboardNavigationMode.Cycle);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (!el.IsKeyboardFocusWithin) el.Focus(); });
        }
    }

    public void HideOverlay()
    {
        if (OverlayHost.Visibility != Visibility.Visible) return;
        OverlayHost.Visibility = Visibility.Collapsed;
        OverlayHost.Children.Clear();
        var cb = _overlayClosed; _overlayClosed = null;
        cb?.Invoke();
        ActivePane?.Term.Focus();
        App.TrimMemorySoon();
    }

    // ---------- find ----------
    public void OpenFind()
    {
        FindBar.Visibility = Visibility.Visible;
        var sel = ActivePane?.Term.GetSelectedText();
        if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n')) FindBox.Text = sel;
        FindBox.Focus(); FindBox.SelectAll();
        RunFind();
    }

    public void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        foreach (var t in _tabs) foreach (var p in t.AllPanes) p.Term.ClearFind();
        ActivePane?.Term.Focus();
    }

    private void RunFind()
    {
        var term = ActivePane?.Term; if (term == null) return;
        int n = term.Find(FindBox.Text, FindCase.IsChecked == true, FindRegex.IsChecked == true);
        FindCount.Text = string.IsNullOrEmpty(FindBox.Text) ? "" : n == 0 ? "No results" : $"{term.CurrentMatch + 1} of {n}";
    }

    private void FindStep(int dir)
    {
        var term = ActivePane?.Term; if (term == null) return;
        term.FindStep(dir);
        FindCount.Text = term.MatchCount == 0 ? "No results" : $"{term.CurrentMatch + 1} of {term.MatchCount}";
    }

    // ---------- command palette ----------
    private sealed record PaletteItem(string Label, string Hint, Func<bool> Run);

    private static double Fuzzy(string q, string text)
    {
        if (q.Length == 0) return 1;
        q = q.ToLowerInvariant(); text = text.ToLowerInvariant();
        int idx = text.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 1000 - idx;
        int ti = 0; double score = 0;
        foreach (var c in q)
        {
            int f = text.IndexOf(c, ti);
            if (f < 0) return 0;
            score += f == ti ? 5 : 1;
            ti = f + 1;
        }
        return score;
    }

    public void OpenPalette()
    {
        var items = new List<PaletteItem>();
        foreach (var (id, def) in Actions.Table)
            items.Add(new(def.Label, _keymap.FirstOrDefault(k => k.Value == id).Key is { Key: not Key.None } c ? c.ToString() : "", () => def.Run(this)));
        foreach (var p in _profiles)
        {
            items.Add(new($"New tab: {p.Name}", "profile", () => { NewTab(p.Id); return true; }));
            items.Add(new($"Split with: {p.Name}", "profile", () => { SplitAuto(p.Id); return true; }));
        }
        foreach (var s in App.AllSchemes()) items.Add(new($"Color scheme: {s.Name}", "scheme", () => { App.Settings.Defaults.ColorScheme = s.Name; App.SettingsChanged(); return true; }));
        for (int i = 0; i < _tabs.Count; i++) { var t = _tabs[i]; items.Add(new($"Switch to tab {i + 1}: {t.Header.Title}", "tab", () => { ActivateTab(t); return true; })); }

        var input = new TextBox { FontSize = 15, Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        var list = new ListBox { MaxHeight = 380, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var shown = new List<PaletteItem>();
        void Render()
        {
            var q = input.Text.Trim();
            shown = items.Select(i => (i, s: Fuzzy(q, i.Label))).Where(x => x.s > 0).OrderByDescending(x => x.s).Take(80).Select(x => x.i).ToList();
            list.Items.Clear();
            foreach (var it in shown)
            {
                var row = new Grid { Margin = new Thickness(6, 3, 6, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new TextBlock { Text = it.Label, TextTrimming = TextTrimming.CharacterEllipsis });
                var hint = new TextBlock { Text = it.Hint, Foreground = (Brush)Application.Current.Resources["MutedBrush"], FontSize = 12, Margin = new Thickness(16, 0, 0, 0) };
                Grid.SetColumn(hint, 1); row.Children.Add(hint);
                list.Items.Add(row);
            }
            if (shown.Count > 0) list.SelectedIndex = 0;
        }
        void Exec()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count) return;
            var it = shown[list.SelectedIndex];
            HideOverlay();
            it.Run();
        }
        input.TextChanged += (_, _) => Render();
        input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { list.SelectedIndex = Math.Min(shown.Count - 1, list.SelectedIndex + 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.Up) { list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1); list.ScrollIntoView(list.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.Enter) { Exec(); e.Handled = true; }
        };
        list.MouseDoubleClick += (_, _) => Exec();
        // only a click on an item runs it (releasing the mouse on the list's scrollbar used to run the selection)
        list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != list; d = VisualTreeHelper.GetParent(d))
                if (d is ListBoxItem) { Exec(); return; }
        };

        var panel = new StackPanel();
        panel.Children.Add(input);
        panel.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["BorderBrush2"] });
        panel.Children.Add(list);
        var card = Card(panel, 640);
        card.Margin = new Thickness(0, 70, 0, 0);
        ShowOverlay(card);
        Render();
        input.Focus();
    }

    // A rounded, bordered card for overlays.
    public static Border Card(UIElement child, double width) => new()
    {
        Width = width, MaxWidth = 2000, Child = child, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
        Background = (Brush)Application.Current.Resources["ChromeSolidBrush"], BorderBrush = (Brush)Application.Current.Resources["BorderBrush2"],
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.45, Direction = 270 },
    };

    // ---------- menus ----------
    private void ShowProfileMenu(FrameworkElement anchor)
    {
        var menu = TextMenu();
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        for (int i = 0; i < _profiles.Count; i++)
        {
            var p = _profiles[i];
            var mi = new MenuItem { Header = p.Name, Icon = new TextBlock { Text = p.Icon ?? "", FontFamily = (FontFamily)Application.Current.Resources["IconFont"] } };
            var chord = _keymap.FirstOrDefault(k => k.Value == $"newTabProfile:{i + 1}").Key;
            if (chord.Key != Key.None) mi.InputGestureText = chord.ToString();
            mi.Click += (_, _) => NewTab(p.Id);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        MenuItem Item(string h, string glyph, Action a, string? gesture = null)
        {
            var m = new MenuItem { Header = h, Icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"] }, InputGestureText = gesture ?? "" };
            m.Click += (_, _) => a(); return m;
        }
        menu.Items.Add(Item("Settings", "", OpenSettings, "Ctrl+,"));
        menu.Items.Add(Item("Command palette", "", OpenPalette, "Ctrl+Shift+P"));
        menu.Items.Add(Item("New window", "", () => new MainWindow().Show(), "Ctrl+Shift+N"));
        menu.IsOpen = true;
    }

    private void ShowTabMenu(TabModel tab, FrameworkElement anchor)
    {
        ActivateTab(tab);
        var menu = TextMenu();
        MenuItem Item(string h, Action a) { var m = new MenuItem { Header = h }; m.Click += (_, _) => a(); return m; }
        menu.Items.Add(Item("Rename tab", () => RenameTab(tab)));
        menu.Items.Add(Item("Duplicate tab", () => NewTab(tab.Active?.Profile.Id, tab.Active?.Cwd)));
        menu.Items.Add(Item("Split right", () => Split(Orientation.Horizontal)));
        menu.Items.Add(Item("Split down", () => Split(Orientation.Vertical)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close other tabs", () => { foreach (var t in _tabs.Where(t => t != tab).ToList()) CloseTab(t); }));
        menu.Items.Add(Item("Close tab", () => CloseTab(tab)));
        menu.PlacementTarget = anchor; menu.IsOpen = true;
    }

    // A context menu inherits its font from the element it opens from; the + / dropdown buttons use the
    // icon font, which has no letters. Always give menus an explicit text font.
    public static ContextMenu TextMenu() => new()
    {
        FontFamily = SystemFonts.MessageFontFamily, FontSize = 13, FontWeight = FontWeights.Normal, FontStyle = FontStyles.Normal,
    };

    public void OpenSettings() => SettingsPanel.Show(this);
}
