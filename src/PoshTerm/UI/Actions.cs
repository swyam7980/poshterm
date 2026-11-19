using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using PoshTerm.Config;

namespace PoshTerm.UI;

// Every action a key binding, menu or the command palette can trigger.
public static class Actions
{
    public sealed record Def(string Label, Func<MainWindow, bool> Run);

    private static bool Ok(Action a) { a(); return true; }

    public static readonly IReadOnlyDictionary<string, Def> Table = new Dictionary<string, Def>
    {
        ["newTab"] = new("New tab", w => Ok(() => w.NewTab())),
        ["duplicateTab"] = new("Duplicate tab", w => Ok(() => w.NewTab(w.ActivePane?.Profile.Id, w.ActivePane?.Cwd))),
        ["closePane"] = new("Close pane", w => { if (w.ActivePane == null) return false; w.ClosePane(w.ActivePane); return true; }),
        ["closeTab"] = new("Close tab", w => { if (w.ActiveTab == null) return false; w.CloseTab(w.ActiveTab); return true; }),
        ["closeOtherTabs"] = new("Close other tabs", w => Ok(() => { foreach (var t in w.Tabs.Where(t => t != w.ActiveTab).ToList()) w.CloseTab(t); })),
        ["nextTab"] = new("Next tab", w => Ok(() => Cycle(w, 1))),
        ["prevTab"] = new("Previous tab", w => Ok(() => Cycle(w, -1))),
        ["splitRight"] = new("Split pane right", w => Ok(() => w.Split(Orientation.Horizontal))),
        ["splitDown"] = new("Split pane down", w => Ok(() => w.Split(Orientation.Vertical))),
        ["splitAuto"] = new("Split pane (auto direction)", w => Ok(() => w.SplitAuto())),
        ["togglePaneZoom"] = new("Maximize / restore pane", w => Ok(() => w.ToggleZoom())),
        ["focusLeft"] = new("Focus pane left", w => Ok(() => w.FocusDirection("left"))),
        ["focusRight"] = new("Focus pane right", w => Ok(() => w.FocusDirection("right"))),
        ["focusUp"] = new("Focus pane above", w => Ok(() => w.FocusDirection("up"))),
        ["focusDown"] = new("Focus pane below", w => Ok(() => w.FocusDirection("down"))),
        ["resizeLeft"] = new("Move divider left", w => Ok(() => w.ResizeDirection("left"))),
        ["resizeRight"] = new("Move divider right", w => Ok(() => w.ResizeDirection("right"))),
        ["resizeUp"] = new("Move divider up", w => Ok(() => w.ResizeDirection("up"))),
        ["resizeDown"] = new("Move divider down", w => Ok(() => w.ResizeDirection("down"))),
        // Ctrl+C only copies when text is selected; otherwise it falls through to the shell as an interrupt.
        ["copy"] = new("Copy selection", w => { var p = w.ActivePane; if (p == null || !p.Term.HasSelection) return false; p.CopySelection(); p.Term.ClearSelection(); return true; }),
        ["paste"] = new("Paste", w => { if (w.ActivePane == null) return false; w.ActivePane.PasteClipboard(); return true; }),
        ["selectAll"] = new("Select all", w => Ok(() => w.ActivePane?.Term.SelectAll())),
        ["find"] = new("Find", w => Ok(w.OpenFind)),
        ["zoomIn"] = new("Increase font size", w => Ok(() => { w.ActivePane?.Zoom(1); w.Toast($"Font size {w.ActivePane?.CurrentFontSize:0.#}", 900); })),
        ["zoomOut"] = new("Decrease font size", w => Ok(() => { w.ActivePane?.Zoom(-1); w.Toast($"Font size {w.ActivePane?.CurrentFontSize:0.#}", 900); })),
        ["zoomReset"] = new("Reset font size", w => Ok(() => w.ActivePane?.Zoom(0, reset: true))),
        ["increaseOpacity"] = new("Increase background opacity", w => Ok(() => Opacity(w, 0.05))),
        ["decreaseOpacity"] = new("Decrease background opacity", w => Ok(() => Opacity(w, -0.05))),
        ["toggleBackgroundImage"] = new("Toggle background image", w => Ok(() => { var p = w.ActivePane; if (p == null) return; p.BackgroundImageHidden = !p.BackgroundImageHidden; p.ApplyAppearance(App.Settings, p.Profile); })),
        ["toggleFullscreen"] = new("Toggle full screen", w => Ok(w.ToggleFullscreen)),
        ["openSettings"] = new("Open settings", w => Ok(() => w.OpenSettings())),
        ["openSettingsFile"] = new("Open settings.json", w => Ok(() => OpenSettingsFile())),
        ["commandPalette"] = new("Command palette", w => Ok(w.OpenPalette)),
        ["newWindow"] = new("New window", w => Ok(() => new MainWindow().Show())),
        ["clearBuffer"] = new("Clear buffer", w => Ok(() => w.ActivePane?.Term.ClearBuffer())),
        ["scrollToTop"] = new("Scroll to top", w => Ok(() => w.ActivePane?.Term.ScrollToTop())),
        ["scrollToBottom"] = new("Scroll to bottom", w => Ok(() => w.ActivePane?.Term.ScrollToBottom())),
        ["scrollPageUp"] = new("Scroll page up", w => Ok(() => w.ActivePane?.Term.ScrollPages(1))),
        ["scrollPageDown"] = new("Scroll page down", w => Ok(() => w.ActivePane?.Term.ScrollPages(-1))),
        ["scrollUp"] = new("Scroll up", w => Ok(() => w.ActivePane?.Term.ScrollLines(3))),
        ["scrollDown"] = new("Scroll down", w => Ok(() => w.ActivePane?.Term.ScrollLines(-3))),
        ["renameTab"] = new("Rename tab", w => Ok(w.RenameActiveTab)),
        ["restartPane"] = new("Restart shell in pane", w => Ok(() => w.ActivePane?.Restart())),
        ["importWtSchemes"] = new("Import Windows Terminal color schemes", w => Ok(() => ImportWindowsTerminalSchemes(w))),
    };

    private static void Cycle(MainWindow w, int d)
    {
        if (w.Tabs.Count < 2 || w.ActiveTab == null) return;
        int i = (w.Tabs.ToList().IndexOf(w.ActiveTab) + d + w.Tabs.Count) % w.Tabs.Count;
        w.ActivateTab(w.Tabs[i]);
    }

    private static void Opacity(MainWindow w, double delta)
    {
        var v = Math.Round(Math.Clamp((App.Settings.Defaults.BackgroundOpacity ?? 0.88) + delta, 0, 1), 2);
        App.Settings.Defaults.BackgroundOpacity = v;
        App.SettingsChanged();
        w.Toast($"Background opacity {v * 100:0}%", 900);
    }

    public static void OpenSettingsFile()
    {
        try { SettingsStore.Save(App.Settings); Process.Start(new ProcessStartInfo(SettingsStore.FilePath) { UseShellExecute = true }); }
        catch { try { Process.Start("notepad.exe", SettingsStore.FilePath); } catch { } }
    }

    // Windows Terminal stores its schemes in the same property format, so they import as-is.
    public static void ImportWindowsTerminalSchemes(MainWindow w)
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
        var candidates = new[]
        {
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json"),
        };
        foreach (var f in candidates.Where(File.Exists))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f), new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!doc.RootElement.TryGetProperty("schemes", out var arr)) continue;
                int n = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    var sc = System.Text.Json.JsonSerializer.Deserialize<ColorScheme>(el.GetRawText(), SettingsStore.Json);
                    if (sc == null || string.IsNullOrEmpty(sc.Name)) continue;
                    App.Settings.Schemes.RemoveAll(s => s.Name.Equals(sc.Name, StringComparison.OrdinalIgnoreCase));
                    App.Settings.Schemes.Add(sc); n++;
                }
                App.SettingsChanged();
                w.Toast($"Imported {n} color schemes from Windows Terminal");
                return;
            }
            catch { }
        }
        w.Toast("No Windows Terminal color schemes were found");
    }

    // ---------- lookup incl. parameterized actions ----------
    public static Def? Find(MainWindow w, string action)
    {
        if (Table.TryGetValue(action, out var d)) return d;
        int i = action.IndexOf(':');
        if (i < 0) return null;
        string name = action[..i], arg = action[(i + 1)..];
        Profile? Prof() => int.TryParse(arg, out var n) ? (n >= 1 && n <= w.Profiles.Count ? w.Profiles[n - 1] : null) : w.Profiles.FirstOrDefault(p => p.Id == arg);
        switch (name)
        {
            case "goToTab":
                return new($"Go to tab {arg}", win => { if (int.TryParse(arg, out var n) && n >= 1 && n <= win.Tabs.Count) { win.ActivateTab(win.Tabs[n - 1]); return true; } return false; });
            case "newTabProfile":
                { var p = Prof(); return p == null ? null : new($"New tab: {p.Name}", win => Ok(() => win.NewTab(p.Id))); }
            case "splitProfile":
                { var p = Prof(); return p == null ? null : new($"Split with: {p.Name}", win => Ok(() => win.SplitAuto(p.Id))); }
            case "setScheme":
                return new($"Color scheme: {arg}", win => Ok(() => { App.Settings.Defaults.ColorScheme = arg; App.SettingsChanged(); }));
        }
        return null;
    }

    public static bool Run(MainWindow w, string action)
    {
        var d = Find(w, action);
        if (d == null || w.ActiveTab == null) return false;
        return d.Run(w);
    }
}
