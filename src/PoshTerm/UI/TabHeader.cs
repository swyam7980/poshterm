using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace PoshTerm.UI;

// The clickable tab in the title bar.
public sealed class TabHeader : Border
{
    private readonly TextBlock _icon = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Width = 16, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 4, 0) };
    private readonly Button _close = new() { Content = "", FontSize = 9, Width = 22, Height = 22, Opacity = 0, Style = (Style)Application.Current.Resources["FlatButton"], ToolTip = "Close tab" };
    private readonly Rectangle _progress = new() { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed, RadiusX = 1, RadiusY = 1 };
    private readonly Grid _grid = new();
    private bool _active, _dragging;
    private Point _dragStart;
    private double _progressPct;

    public event Action? Activate, CloseRequested, RenameRequested, ContextRequested;
    public event Action<int, int>? Moved;

    public string Title { get => _title.Text; set { _title.Text = value; ToolTip = value; } }
    public string? IconGlyph { set => _icon.Text = string.IsNullOrEmpty(value) ? "" : value; }

    public TabHeader()
    {
        Height = 32; MinWidth = 96; MaxWidth = 210;
        Margin = new Thickness(0, 6, 2, 0);
        CornerRadius = new CornerRadius(8, 8, 0, 0);
        Background = Brushes.Transparent;
        Padding = new Thickness(11, 0, 4, 0);
        _icon.FontFamily = (FontFamily)Application.Current.Resources["IconFont"];
        _close.FontFamily = (FontFamily)Application.Current.Resources["IconFont"];
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 1); Grid.SetColumn(_close, 2);
        _grid.Children.Add(_icon); _grid.Children.Add(_title); _grid.Children.Add(_close);
        var overlay = new Grid();
        overlay.Children.Add(_grid);
        overlay.Children.Add(_progress);
        Child = overlay;
        _icon.Text = "";

        MouseEnter += (_, _) => { _close.Opacity = 0.8; if (!_active) Background = (Brush)Application.Current.Resources["RaisedBrush"]; };
        MouseLeave += (_, _) => { if (!_active) { _close.Opacity = 0; Background = Brushes.Transparent; } };
        _close.Click += (_, e) => CloseRequested?.Invoke();
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };
        MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseRequested?.Invoke(); };
        MouseRightButtonUp += (_, e) => { ContextRequested?.Invoke(); e.Handled = true; };
        SizeChanged += (_, _) => UpdateProgressWidth();
        WindowChrome.SetIsHitTestVisibleInChrome(this, true);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInside(d, _close)) return;
        Activate?.Invoke();
        if (e.ClickCount == 2) { RenameRequested?.Invoke(); return; }
        _dragStart = e.GetPosition(Parent as IInputElement);
        _dragging = true;
        CaptureMouse();
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var c = d; c != null; c = VisualTreeHelper.GetParent(c)) if (ReferenceEquals(c, ancestor)) return true;
        return false;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || Parent is not Panel strip) return;
        var p = e.GetPosition(strip);
        if (Math.Abs(p.X - _dragStart.X) < 6 && strip.Children.IndexOf(this) >= 0 && Math.Abs(p.X - _dragStart.X) < 6) return;
        int from = strip.Children.IndexOf(this), to = from;
        for (int i = 0; i < strip.Children.Count; i++)
        {
            if (strip.Children[i] is not FrameworkElement el || ReferenceEquals(el, this)) continue;
            double mid = el.TranslatePoint(new Point(el.ActualWidth / 2, 0), strip).X;
            if (i < from && p.X < mid) { to = i; break; }
            if (i > from && p.X > mid) to = i;
        }
        if (to != from) Moved?.Invoke(from, to);
    }

    public void Refresh(bool active)
    {
        _active = active;
        var r = Application.Current.Resources;
        Background = active ? (Brush)r["ChromeSolidBrush"] : Brushes.Transparent;
        _title.Foreground = active ? (Brush)r["FgBrush"] : (Brush)r["MutedBrush"];
        _icon.Foreground = active ? (Brush)r["AccentBrush"] : (Brush)r["MutedBrush"];
        _close.Opacity = active ? 0.8 : 0;
        _progress.Fill = (Brush)r["AccentBrush"];
        if (active) BeginAnimation(OpacityProperty, null);
    }

    public void BringIntoView_(ScrollViewer sv)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            var x = TranslatePoint(new Point(0, 0), sv).X;
            if (x < 0) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + x);
            else if (x + ActualWidth > sv.ViewportWidth) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + x + ActualWidth - sv.ViewportWidth);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // OSC 9;4 states: 0 clear, 1 value, 2 error, 3 indeterminate, 4 warning
    public void SetProgress(int state, int pct)
    {
        _progress.BeginAnimation(OpacityProperty, null);
        _progress.Visibility = state == 0 ? Visibility.Collapsed : Visibility.Visible;
        _progress.Fill = state == 2 ? (Brush)Application.Current.Resources["DangerBrush"] : (Brush)Application.Current.Resources["AccentBrush"];
        if (state == 3)
        {
            _progressPct = 35;
            _progress.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else _progressPct = Math.Clamp(pct, 0, 100);
        UpdateProgressWidth();
    }

    private void UpdateProgressWidth() => _progress.Width = Math.Max(0, (ActualWidth - 20) * _progressPct / 100) + (_progress.Visibility == Visibility.Visible ? 2 : 0);

    public void Bell()
    {
        _icon.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(250)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) });
    }

    public void BeginRename(string initial, Action<string?> done)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(2, 0, 2, 0), MinHeight = 22 };
        Grid.SetColumn(box, 1);
        _title.Visibility = Visibility.Collapsed;
        _grid.Children.Add(box);
        bool finished = false;
        void Finish(bool ok)
        {
            if (finished) return;
            finished = true;
            _grid.Children.Remove(box);
            _title.Visibility = Visibility.Visible;
            done(ok ? box.Text : null);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Finish(true); e.Handled = true; } else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; } };
        box.LostKeyboardFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
