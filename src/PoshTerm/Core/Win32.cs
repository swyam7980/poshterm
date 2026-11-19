using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PoshTerm.Core;

public static class Win32
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int WM_HOTKEY = 0x0312;

    public static bool BackdropSupported => Environment.OSVersion.Version.Build >= 22621;

    public static void SetDarkTitle(Window w, bool dark)
    {
        var h = new WindowInteropHelper(w).Handle;
        int v = dark ? 1 : 0;
        DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
    }

    // material: acrylic | mica | tabbed | none. Returns true when the backdrop is active (Windows 11 22H2+).
    public static bool SetBackdrop(Window w, string material)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero || !BackdropSupported) return false;
        int type = material switch { "mica" => 2, "acrylic" => 3, "tabbed" => 4, _ => 1 };
        int hr = DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        return hr == 0 && type != 1;
    }

    // Whole-window opacity (text included). 1.0 removes the layered style again.
    public static void SetWindowOpacity(Window w, double opacity)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        opacity = Math.Clamp(opacity, 0.2, 1.0);
        if (opacity >= 0.999)
        {
            if ((ex & WS_EX_LAYERED) != 0) SetWindowLong(h, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
            return;
        }
        if ((ex & WS_EX_LAYERED) == 0) SetWindowLong(h, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        SetLayeredWindowAttributes(h, 0, (byte)Math.Round(opacity * 255), 2);
    }

    public static void Activate(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        SetForegroundWindow(new WindowInteropHelper(w).Handle);
    }

    // ---------- global hotkey ----------
    private const int HotkeyId = 0x504F;   // "PO"

    public static bool RegisterGlobalHotkey(Window w, string text)
    {
        var h = new WindowInteropHelper(w).Handle;
        UnregisterHotKey(h, HotkeyId);
        if (string.IsNullOrWhiteSpace(text) || !PoshTerm.Config.Chord.TryParse(text, out var chord)) return false;
        uint mods = 0x4000;
        if (chord.Mods.HasFlag(ModifierKeys.Alt)) mods |= 1;
        if (chord.Mods.HasFlag(ModifierKeys.Control)) mods |= 2;
        if (chord.Mods.HasFlag(ModifierKeys.Shift)) mods |= 4;
        if (chord.Mods.HasFlag(ModifierKeys.Windows)) mods |= 8;
        return RegisterHotKey(h, HotkeyId, mods, (uint)KeyInterop.VirtualKeyFromKey(chord.Key));
    }

    public static void UnregisterGlobalHotkey(Window w) => UnregisterHotKey(new WindowInteropHelper(w).Handle, HotkeyId);

    // ---------- maximized / full-screen bounds ----------
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct APPBARDATA { public int Size; public IntPtr Hwnd; public uint CallbackMessage, Edge; public RECT Rect; public IntPtr LParam; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    public const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 2, ABM_GETSTATE = 4, ABM_GETAUTOHIDEBAREX = 0x0B, ABS_AUTOHIDE = 1;

    // A WindowChrome (borderless) window maximizes over the whole monitor, taskbar included. Clamp the
    // maximized rect to the monitor's work area, or to the full monitor in full-screen mode. An auto-hide
    // taskbar takes no work area, so leave a 2px strip on its edge for the mouse to summon it.
    public static void FitMaximized(IntPtr hwnd, IntPtr lParam, bool fullScreen)
    {
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref mi)) return;
        var r = fullScreen ? mi.Monitor : SpareAutoHideTaskbar(mi.Monitor, mi.Work);
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.MaxPosition = new POINT { X = r.Left - mi.Monitor.Left, Y = r.Top - mi.Monitor.Top };
        mmi.MaxSize = new POINT { X = r.Right - r.Left, Y = r.Bottom - r.Top };
        Marshal.StructureToPtr(mmi, lParam, false);
    }

    private static RECT SpareAutoHideTaskbar(RECT monitor, RECT work)
    {
        var state = new APPBARDATA { Size = Marshal.SizeOf<APPBARDATA>() };
        if (((uint)SHAppBarMessage(ABM_GETSTATE, ref state) & ABS_AUTOHIDE) == 0) return work;
        for (uint edge = 0; edge < 4; edge++)   // ABE_LEFT, ABE_TOP, ABE_RIGHT, ABE_BOTTOM
        {
            var q = new APPBARDATA { Size = Marshal.SizeOf<APPBARDATA>(), Edge = edge, Rect = monitor };
            if (SHAppBarMessage(ABM_GETAUTOHIDEBAREX, ref q) == UIntPtr.Zero) continue;   // no auto-hide bar on this edge
            switch (edge)
            {
                case 0: if (work.Left == monitor.Left) work.Left += 2; break;
                case 1: if (work.Top == monitor.Top) work.Top += 2; break;
                case 2: if (work.Right == monitor.Right) work.Right -= 2; break;
                default: if (work.Bottom == monitor.Bottom) work.Bottom -= 2; break;
            }
        }
        return work;
    }

    // ---------- fonts ----------
    private delegate int FontEnumProc(IntPtr logFont, IntPtr textMetric, uint fontType, IntPtr lParam);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int EnumFontFamiliesExW(IntPtr hdc, byte[] logFont, FontEnumProc proc, IntPtr lParam, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    // Family names GDI reports as fixed-pitch (cheap: no font files are loaded).
    public static HashSet<string> FixedPitchFontFamilies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            var lf = new byte[92];   // LOGFONTW
            lf[23] = 1;              // lfCharSet = DEFAULT_CHARSET: every family once per charset
            FontEnumProc cb = (p, _, _, _) =>
            {
                // LOGFONTW: lfPitchAndFamily at offset 27, lfFaceName (WCHAR[32]) at 28
                if ((Marshal.ReadByte(p, 27) & 3) == 1 && Marshal.PtrToStringUni(p + 28) is { Length: > 0 } name && name[0] != '@') set.Add(name);
                return 1;
            };
            EnumFontFamiliesExW(hdc, lf, cb, IntPtr.Zero, 0);
            GC.KeepAlive(cb);
        }
        catch { }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
        return set;
    }
}
