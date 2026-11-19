using System.Windows;
using System.Windows.Media;
using PoshTerm.Config;
using PoshTerm.Core;
using PoshTerm.UI;

namespace PoshTerm;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static string? SettingsError { get; private set; }
    private static readonly HashSet<string> _shownErrors = new();
    private static FileSystemWatcher? _watcher;
    private static System.Threading.Timer? _debounce;
    private static System.Threading.Timer? _saveTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            LogCrash(ex.Exception);
            if (_shownErrors.Add(ex.Exception.GetType().FullName + ex.Exception.Message))
                MessageBox.Show(ex.Exception.Message + "\n\nDetails were written to crash.log in " + SettingsStore.Dir, "PoshTerm", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        (Settings, SettingsError) = SettingsStore.Load();
        WatchSettings();
        var w = new MainWindow();
        w.Show();
    }

    public static void LogError(Exception e) => LogCrash(e);

    // Give memory back once things go quiet. Startup, the settings page (font lists, previews) and closed
    // panes leave garbage that the GC would otherwise keep committed until the next allocation-driven
    // collection, which for an idle terminal may never come. An aggressive collection compacts the heap and
    // decommits the freed segments, so the process really shrinks (this is not just a working-set trim).
    private static System.Windows.Threading.DispatcherTimer? _trimTimer;
    public static void TrimMemorySoon(int delayMs = 1500)
    {
        _trimTimer?.Stop();
        _trimTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer?.Stop(); _trimTimer = null;
            // WPF text/visual objects free their native (unmanaged) resources in finalizers: run those, then
            // compact and decommit what they released
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        };
        _trimTimer.Start();
    }

    private static void LogCrash(Exception e)
    {
        try { File.AppendAllText(Path.Combine(SettingsStore.Dir, "crash.log"), $"[{DateTime.Now:s}] {e}\n\n"); } catch { }
    }

    // All profiles after detection/hiding, as the UI sees them.
    public static List<Profile> Profiles => Shells.Resolve(Settings);

    public static ColorScheme Scheme(string? name) =>
        Settings.Schemes.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? BuiltInSchemes.All.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? BuiltInSchemes.All[0];

    // Scheme colors come from hand-edited or imported JSON; a typo must not throw on every repaint.
    public static Color ParseColor(string? hex, Color fallback = default)
    {
        try { return hex != null && ColorConverter.ConvertFromString(hex) is Color c ? c : fallback; } catch (FormatException) { return fallback; }
    }

    public static IEnumerable<ColorScheme> AllSchemes() =>
        Settings.Schemes.Concat(BuiltInSchemes.All.Where(b => Settings.Schemes.All(u => !u.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase))));

    // Persist (debounced so sliders don't hammer the disk) and re-apply everywhere.
    public static void SettingsChanged(bool save = true)
    {
        foreach (var w in Current.Windows.OfType<MainWindow>()) w.ApplySettings();
        if (!save) return;
        _saveTimer?.Dispose();
        _saveTimer = new System.Threading.Timer(_ => { try { SettingsStore.Save(Settings); } catch { } }, null, 300, System.Threading.Timeout.Infinite);
    }

    private static void WatchSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Dir);
            _watcher = new FileSystemWatcher(SettingsStore.Dir, "settings.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName, EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, _) =>
            {
                _debounce?.Dispose();
                _debounce = new System.Threading.Timer(_ => Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!SettingsStore.ChangedExternally()) return;
                    var (s, err) = SettingsStore.Load();
                    SettingsError = err;
                    if (err == null) { Settings = s; foreach (var w in Current.Windows.OfType<MainWindow>()) { w.ApplySettings(); w.Toast("Settings reloaded"); } }
                    else foreach (var w in Current.Windows.OfType<MainWindow>()) w.Toast(err, 5000);
                }), null, 200, System.Threading.Timeout.Infinite);
            };
            _watcher.Changed += h; _watcher.Created += h; _watcher.Renamed += (a, b) => h(a, b);
        }
        catch { /* hot reload is best-effort */ }
    }

    // Light/dark control theme follows the active color scheme.
    // Assigning ThemeMode reloads the whole Fluent theme (every control re-templates) even when the value is
    // unchanged. Doing that on every settings tweak killed slider drags mid-move and could throw
    // "Cyclic reference ... Style" exceptions, so only assign on an actual change.
    public static void ApplyControlTheme(bool dark)
    {
        var mode = dark ? ThemeMode.Dark : ThemeMode.Light;
        if (Current.ThemeMode != mode) Current.ThemeMode = mode;
    }
}
