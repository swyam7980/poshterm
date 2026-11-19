using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PoshTerm.Pty;

// A child process attached to a Windows pseudo-console (ConPTY). Output is pumped on a
// background thread and delivered through OutputReceived; input goes through Write.
public sealed class ConPty : IDisposable
{
    public event Action<byte[], int>? OutputReceived;   // buffer is reused: copy if you keep it
    public event Action<int>? Exited;

    private IntPtr _hPC;
    private SafeFileHandle? _inWrite;      // we write keystrokes here
    private SafeFileHandle? _outRead;      // we read terminal output here
    private IntPtr _hProcess, _hThread;
    private IntPtr _attrList;
    private FileStream? _writer;
    private Thread? _reader;
    private volatile bool _disposed;
    private readonly object _lock = new();

    public int ProcessId { get; private set; }

    public static ConPty Start(string commandLine, string? cwd, IDictionary<string, string>? env, short cols, short rows)
    {
        var p = new ConPty();
        // a failed launch must still close the pseudo-console: each retry of a broken profile used to leave
        // an orphaned conhost.exe running
        try { p.Launch(commandLine, cwd, env, cols, rows); }
        catch { p.Dispose(); throw; }
        return p;
    }

    private void Launch(string commandLine, string? cwd, IDictionary<string, string>? env, short cols, short rows)
    {
        if (!CreatePipe(out var inRead, out _inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out _outRead, out var outWrite, IntPtr.Zero, 0)) throw new Win32Exception();

        int hr = CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out _hPC);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed");
        // the pseudo-console owns its ends now
        inRead.Dispose();
        outWrite.Dispose();

        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        _attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref size)) throw new Win32Exception();
        if (!UpdateProcThreadAttribute(_attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();
        si.lpAttributeList = _attrList;

        IntPtr envBlock = IntPtr.Zero;
        try
        {
            if (env != null) envBlock = Marshal.StringToHGlobalUni(BuildEnvBlock(env));
            var cmd = new StringBuilder(commandLine);
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, envBlock, cwd, ref si, out var pi))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Could not start: {commandLine}");
            }
            _hProcess = pi.hProcess; _hThread = pi.hThread; ProcessId = pi.dwProcessId;
        }
        finally { if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock); }

        _writer = new FileStream(_inWrite!, FileAccess.Write, 1, false);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPty reader" };
        _reader.Start();
        new Thread(WaitLoop) { IsBackground = true, Name = "ConPty wait" }.Start();
    }

    // Environment block: sorted, KEY=VALUE\0 ... \0\0 (CreateProcess requires sorted order on some systems)
    private static string BuildEnvBlock(IDictionary<string, string> env)
    {
        var sb = new StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    private void ReadLoop()
    {
        var buf = new byte[32768];
        using var stream = new FileStream(_outRead!, FileAccess.Read, 1, false);
        try
        {
            int n;
            while (!_disposed && (n = stream.Read(buf, 0, buf.Length)) > 0) OutputReceived?.Invoke(buf, n);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_hProcess, 0xFFFFFFFF);
        GetExitCodeProcess(_hProcess, out uint code);
        // give the reader a moment to drain the last output before we report the exit
        _reader?.Join(300);
        if (!_disposed) Exited?.Invoke((int)code);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || _writer == null) return;
        lock (_lock)
        {
            try { _writer.Write(data); _writer.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void Resize(short cols, short rows)
    {
        if (_disposed || _hPC == IntPtr.Zero || cols <= 0 || rows <= 0) return;
        ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
    }

    public void Kill()
    {
        if (_hProcess != IntPtr.Zero) TerminateProcess(_hProcess, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        // ClosePseudoConsole can block while output is pending, so do it off-thread
        var hpc = _hPC; _hPC = IntPtr.Zero;
        var hp = _hProcess; var ht = _hThread; var attr = _attrList;
        var w = _writer; var iw = _inWrite;
        var orphanRead = _reader == null ? _outRead : null;   // otherwise the reader thread's stream owns it
        Task.Run(() =>
        {
            if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
            try { w?.Dispose(); iw?.Dispose(); orphanRead?.Dispose(); } catch { }
            if (hp != IntPtr.Zero) CloseHandle(hp);
            if (ht != IntPtr.Zero) CloseHandle(ht);
            if (attr != IntPtr.Zero) { DeleteProcThreadAttributeList(attr); Marshal.FreeHGlobal(attr); }
        });
    }

    // ---------- native ----------
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attrs, int size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr hPC);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
