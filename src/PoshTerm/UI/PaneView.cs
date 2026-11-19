using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using PoshTerm.Config;
using PoshTerm.Core;
using PoshTerm.Pty;
using PoshTerm.Terminal;

namespace PoshTerm.UI;

// One terminal pane: layered background (color, image) under a TerminalView, plus its shell process.
public sealed class PaneView : Grid
{
    public TerminalView Term { get; } = new();
    public Profile Profile { get; private set; }
    public string? Cwd { get; private set; }
    public string Title { get; private set; } = "";
    public bool BackgroundImageHidden { get; set; }

    public event Action<PaneView>? Focused;
    public event Action<PaneView>? TitleChanged;
    public event Action<PaneView>? ExitedAndClose;           // shell ended and settings say close
    public event Action<PaneView, int, int>? ProgressChanged;
    public event Action<PaneView>? BellRang;

    private readonly Rectangle _bgColor = new() { IsHitTestVisible = false };
    private readonly Rectangle _bgImage = new() { IsHitTestVisible = false };
    private readonly Rectangle _flash = new() { IsHitTestVisible = false, Fill = Brushes.White, Opacity = 0 };
    private readonly Border _activeOutline = new() { IsHitTestVisible = false, BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };

    private ConPty? _pty;
    private Look _look = Look.Defaults();
    private string? _imageLoaded;
    private BitmapImage? _bitmap;
    private bool _exited;
    private string? _typeIn;
    private bool _started;
    private double _zoom;
    private readonly string? _initialCwd;
    private readonly bool _preview;
    // debugging aid: set POSHTERM_PTY_LOG=<file> to record the raw bytes each shell sends
    private static readonly string? PtyLog = Environment.GetEnvironmentVariable("POSHTERM_PTY_LOG");

    public PaneView(Profile profile, string? cwd, bool preview = false)
    {
        _preview = preview;
        Profile = profile;
        _initialCwd = cwd;
        Cwd = cwd;
        Children.Add(_bgColor);
        Children.Add(_bgImage);
        Children.Add(Term);
        Children.Add(_flash);
        Children.Add(_activeOutline);
        ClipToBounds = true;

        Term.GridResized += (c, r) => _pty?.Resize((short)c, (short)r);
        Term.SendToChild += bytes =>
        {
            if (_exited) { if (bytes.Contains((byte)'\r')) Restart(); return; }
            _pty?.Write(bytes);
        };
        Term.TitleChanged += t => { Title = t; TitleChanged?.Invoke(this); };
        Term.CwdChanged += d => Cwd = d;
        Term.ProgressChanged += (s, p) => ProgressChanged?.Invoke(this, s, p);
        Term.BellRang += OnBell;
        Term.GotKeyboardFocus += (_, _) => Focused?.Invoke(this);
        Term.OutputActivity += OnFirstOutput;
        MouseDown += (_, _) => Focused?.Invoke(this);
        Term.SelectionFinished += () => { if (_look.CopyOnSelect == true) CopySelection(); };
        Term.PasteRequested += PasteClipboard;
        Term.MouseRightButtonUp += OnRightClick;
        Loaded += (_, _) => TryStart();
        Term.SizeChanged += (_, _) => TryStart();
    }

    // ---------- appearance ----------
    public void ApplyAppearance(AppSettings settings, Profile profile)
    {
        Profile = profile;
        _look = profile.Over(settings.Defaults);
        var scheme = settings.Schemes.FirstOrDefault(s => s.Name.Equals(_look.ColorScheme, StringComparison.OrdinalIgnoreCase))
                     ?? BuiltInSchemes.All.FirstOrDefault(s => s.Name.Equals(_look.ColorScheme, StringComparison.OrdinalIgnoreCase))
                     ?? BuiltInSchemes.All[0];
        Term.FontSizeAdjust = _zoom;
        Term.ApplyLook(_look, scheme);
        Term.ShiftEnter = Shells.KindOf(profile) == ShellKind.Posh ? "\x1b\r" : null;

        var bg = App.ParseColor(scheme.Background, Colors.Black);
        byte a = (byte)Math.Round(Math.Clamp(_look.BackgroundOpacity ?? 0.88, 0, 1) * 255);
        _bgColor.Fill = new SolidColorBrush(Color.FromArgb(a, bg.R, bg.G, bg.B));
        var sel = App.ParseColor(scheme.BrightBlue, Colors.SteelBlue);
        _activeOutline.BorderBrush = new SolidColorBrush(Color.FromArgb(150, sel.R, sel.G, sel.B));
        ApplyImage();
    }

    private void ApplyImage()
    {
        string path = BackgroundImageHidden ? "" : (_look.BackgroundImage ?? "");
        if (path != _imageLoaded)
        {
            _imageLoaded = path;
            _bitmap = null;
            if (path.Length > 0)
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = Uri.TryCreate(path, UriKind.Absolute, out var u) ? u : new Uri(Path.GetFullPath(Shells.Expand(path)));
                    bi.EndInit();
                    if (bi.CanFreeze) bi.Freeze();
                    _bitmap = bi;
                }
                catch { _bitmap = null; }
            }
        }
        if (_bitmap == null) { _bgImage.Fill = null; _bgImage.Effect = null; return; }

        var brush = new ImageBrush(_bitmap) { Opacity = Math.Clamp(_look.BackgroundImageOpacity ?? 0.25, 0, 1) };
        switch ((_look.BackgroundImageStretch ?? "uniformToFill").ToLowerInvariant())
        {
            case "uniform": brush.Stretch = Stretch.Uniform; break;
            case "fill": brush.Stretch = Stretch.Fill; break;
            case "none": brush.Stretch = Stretch.None; break;
            case "tile":
                brush.TileMode = TileMode.Tile;
                brush.ViewportUnits = BrushMappingMode.Absolute;
                brush.Viewport = new Rect(0, 0, _bitmap.PixelWidth * 96.0 / _bitmap.DpiX, _bitmap.PixelHeight * 96.0 / _bitmap.DpiY);
                brush.Stretch = Stretch.Fill;
                break;
            default: brush.Stretch = Stretch.UniformToFill; break;
        }
        var al = (_look.BackgroundImageAlignment ?? "center").ToLowerInvariant();
        brush.AlignmentX = al.Contains("left") ? AlignmentX.Left : al.Contains("right") ? AlignmentX.Right : AlignmentX.Center;
        brush.AlignmentY = al.Contains("top") ? AlignmentY.Top : al.Contains("bottom") ? AlignmentY.Bottom : AlignmentY.Center;
        _bgImage.Fill = brush;
        double blur = _look.BackgroundImageBlur ?? 0;
        _bgImage.Effect = blur > 0 ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } : null;
    }

    public void SetActive(bool active, bool multiPane) =>
        _activeOutline.Visibility = active && multiPane ? Visibility.Visible : Visibility.Collapsed;

    public void Zoom(double delta, bool reset = false)
    {
        _zoom = reset ? 0 : Math.Clamp(_zoom + delta, -8, 40);
        Term.FontSizeAdjust = _zoom;
        Term.ApplyLook(_look, Term_Scheme());
    }

    private ColorScheme Term_Scheme()
    {
        var s = App.Settings;
        return s.Schemes.FirstOrDefault(x => x.Name.Equals(_look.ColorScheme, StringComparison.OrdinalIgnoreCase))
               ?? BuiltInSchemes.All.FirstOrDefault(x => x.Name.Equals(_look.ColorScheme, StringComparison.OrdinalIgnoreCase)) ?? BuiltInSchemes.All[0];
    }

    public double CurrentFontSize => (_look.FontSize ?? 14) + _zoom;

    // ---------- session ----------
    private void TryStart()
    {
        if (_preview || _started || Term.ActualWidth < 20 || Term.ActualHeight < 20) return;
        _started = true;
        Term.UpdateGrid();
        StartSession();
    }

    private void StartSession()
    {
        _exited = false;
        try
        {
            var launch = Shells.BuildLaunch(Profile, _initialCwd ?? Cwd);
            _typeIn = launch.TypeIn;
            _pty = ConPty.Start(launch.CommandLine, launch.WorkingDirectory, launch.Env, (short)Term.Cols, (short)Term.Rows);
            var pty = _pty;
            pty.OutputReceived += (buf, n) =>
            {
                Term.EnqueueOutput(buf, n);
                if (PtyLog != null) try { using var f = new FileStream(PtyLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite); f.Write(buf, 0, n); } catch { }
            };
            pty.Exited += code => Dispatcher.BeginInvoke(() => OnExit(pty, code));
        }
        catch (Exception e)
        {
            _exited = true;
            var msg = Encoding.UTF8.GetBytes($"\x1b[31mCould not start \"{Profile.Name}\": {e.Message}\x1b[0m\r\n\x1b[90mFix the profile in Settings (Ctrl+,). Press Enter to retry.\x1b[0m\r\n");
            Term.EnqueueOutput(msg, msg.Length);
        }
    }

    private void OnFirstOutput()
    {
        if (_typeIn == null) return;
        var text = _typeIn; _typeIn = null;
        // wait for the shell to draw its first prompt before typing the startup commands
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        t.Tick += (_, _) => { t.Stop(); _pty?.Write(text); };
        t.Start();
    }

    private void OnExit(ConPty pty, int code)
    {
        if (!ReferenceEquals(pty, _pty)) return;
        _pty?.Dispose(); _pty = null;
        string mode = _look.CloseOnExit ?? "graceful";
        if (mode == "always" || (mode == "graceful" && code == 0)) { ExitedAndClose?.Invoke(this); return; }
        _exited = true;
        var msg = Encoding.UTF8.GetBytes($"\r\n\x1b[90m[process exited with code {code} — press Enter to restart, Ctrl+Shift+W to close]\x1b[0m\r\n");
        Term.EnqueueOutput(msg, msg.Length);
    }

    public void Restart()
    {
        _pty?.Dispose(); _pty = null;
        Term.Parser.Feed("\x1bc"u8);
        StartSession();
    }

    public void Close()
    {
        var p = _pty; _pty = null;
        p?.Dispose();
    }

    // ---------- clipboard & effects ----------
    public void CopySelection()
    {
        var t = Term.GetSelectedText();
        if (t.Length == 0) return;
        try { Clipboard.SetText(t); } catch { }
    }

    public void PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsText()) Term.Paste(Clipboard.GetText());
        }
        catch { }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_look.RightClickPaste == true)
        {
            if (Term.HasSelection) { CopySelection(); Term.ClearSelection(); }
            else PasteClipboard();
        }
        else ShowContextMenu();
        e.Handled = true;
    }

    private void ShowContextMenu()
    {
        var menu = MainWindow.TextMenu();
        MenuItem Item(string header, Action a) { var m = new MenuItem { Header = header }; m.Click += (_, _) => a(); return m; }
        menu.Items.Add(Item("Copy", CopySelection));
        menu.Items.Add(Item("Paste", PasteClipboard));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Split right", () => MainWindow.Current?.Run("splitRight")));
        menu.Items.Add(Item("Split down", () => MainWindow.Current?.Run("splitDown")));
        menu.Items.Add(Item("Close pane", () => MainWindow.Current?.Run("closePane")));
        menu.IsOpen = true;
    }

    private void OnBell()
    {
        string style = _look.BellStyle ?? "visual";
        if (style == "none") return;
        if (style is "audible" or "all") System.Media.SystemSounds.Beep.Play();
        if (style is "visual" or "all")
        {
            var anim = new DoubleAnimation(0.12, 0, TimeSpan.FromMilliseconds(220));
            _flash.BeginAnimation(OpacityProperty, anim);
        }
        BellRang?.Invoke(this);
    }
}
