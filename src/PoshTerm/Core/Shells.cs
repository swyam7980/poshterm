using System.Diagnostics;
using System.Text;
using PoshTerm.Config;

namespace PoshTerm.Core;

public enum ShellKind { Posh, PowerShell, Cmd, Wsl, Bash, Other }

public sealed record Launch(string CommandLine, string WorkingDirectory, Dictionary<string, string> Env, string? TypeIn);

// Finds installed shells and turns a Profile into a concrete process launch.
public static class Shells
{
    private static List<Profile>? _detected;

    public static string? FindOnPath(string exe)
    {
        if (Path.IsPathRooted(exe)) return File.Exists(exe) ? exe : null;
        var names = Path.HasExtension(exe) ? new[] { exe } : new[] { exe + ".exe", exe + ".cmd", exe + ".bat" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
                try { var p = Path.Combine(dir.Trim('"'), n); if (File.Exists(p)) return p; } catch { }
        return null;
    }

    // Walk up from the app folder looking for a relative path (dev layout: repo/old_shell/pwsh.exe).
    private static string? FindUp(string rel, int depth = 8)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < depth && d != null; i++, d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // The built-in shell: a packaged posh.exe next to the app, else the copy embedded inside this single
    // exe (extracted to a temp folder on first use), else (development) node + posh.js.
    public static (string file, string[] args)? ResolvePosh()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "posh", "posh.exe");
        if (File.Exists(exe)) return (exe, Array.Empty<string>());
        var bundled = ExtractBundled();
        if (bundled != null && File.Exists(bundled)) return (bundled, Array.Empty<string>());
        var js = FindUp(Path.Combine("posh", "posh.js"));
        var node = FindOnPath("node");
        if (js != null && node != null) return (node, new[] { js });
        return null;
    }

    // Extracts the embedded posh.exe into a per-version temp folder, so the shipped product is a single
    // self-contained PoshTerm.exe. Returns the posh.exe path.
    private static string? _extractedPosh;
    public static string? ExtractBundled()
    {
        if (_extractedPosh != null) return _extractedPosh;
        var asm = typeof(Shells).Assembly;
        if (asm.GetManifestResourceInfo("posh.exe") == null) return null;   // dev/Debug build: not embedded
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "PoshTerm", asm.GetName().Version?.ToString() ?? "1");
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "posh.exe");
            Extract(asm, "posh.exe", exe);
            if (File.Exists(exe)) return _extractedPosh = exe;
        }
        catch (Exception e) { App.LogError(e); }
        return null;
    }

    private static void Extract(System.Reflection.Assembly asm, string resource, string dest)
    {
        using var s = asm.GetManifestResourceStream(resource);
        if (s == null) return;
        // already extracted and current (same size) — leave it (it may be running and locked)
        if (File.Exists(dest) && new FileInfo(dest).Length == s.Length) return;
        var tmp = dest + "." + Environment.ProcessId + ".tmp";
        using (var f = File.Create(tmp)) s.CopyTo(f);
        try { File.Move(tmp, dest, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { } }   // another instance won the race; its copy is fine
    }

    public static List<Profile> Detect(bool refresh = false)
    {
        if (_detected != null && !refresh) return _detected;
        var list = new List<Profile>
        {
            new() { Id = "posh", Name = "posh", Command = "posh", Icon = "", Source = "built-in" },
        };
        var oldPwsh = FindUp(Path.Combine("old_shell", "pwsh.exe")) ?? FindUp(Path.Combine("pwsh", "pwsh.exe"));
        if (oldPwsh != null) list.Add(new() { Id = "pwsh-7.6", Name = "PowerShell 7.6 (bundled)", Command = oldPwsh, Args = "-NoLogo", Icon = "", Source = "old_shell" });
        var sysPwsh = FindOnPath("pwsh.exe");
        if (sysPwsh != null && (oldPwsh == null || !string.Equals(Path.GetFullPath(sysPwsh), Path.GetFullPath(oldPwsh), StringComparison.OrdinalIgnoreCase)))
            list.Add(new() { Id = "pwsh", Name = "PowerShell", Command = sysPwsh, Args = "-NoLogo", Icon = "", Source = "detected" });
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var winPs = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPs)) list.Add(new() { Id = "windows-powershell", Name = "Windows PowerShell", Command = winPs, Args = "-NoLogo", Icon = "", Source = "detected" });
        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(sys, "cmd.exe");
        list.Add(new() { Id = "cmd", Name = "Command Prompt", Command = cmd, Icon = "", Source = "detected" });

        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Programs") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var gb = Path.Combine(root, "Git", "bin", "bash.exe");
            if (File.Exists(gb)) { list.Add(new() { Id = "git-bash", Name = "Git Bash", Command = gb, Args = "--login -i", Icon = "", Source = "detected" }); break; }
        }

        var wsl = Path.Combine(sys, "wsl.exe");
        if (File.Exists(wsl))
        {
            foreach (var distro in ListWslDistros(wsl))
                list.Add(new() { Id = "wsl-" + new string(distro.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '-').ToArray()), Name = distro, Command = wsl, Args = $"-d {distro} --cd ~", Icon = "", Source = "detected" });
        }
        return _detected = list;
    }

    private static IEnumerable<string> ListWslDistros(string wsl)
    {
        try
        {
            var psi = new ProcessStartInfo(wsl, "-l -q") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.Unicode };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) { try { p.Kill(); } catch { } }
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("\0", "").Trim()).Where(s => s.Length > 0 && !s.Contains("docker-desktop")).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    // User-defined profiles layered over detected ones; hidden ones removed.
    public static List<Profile> Resolve(AppSettings s)
    {
        var result = new List<Profile>();
        if (s.AutoDetectProfiles)
            foreach (var d in Detect())
            {
                if (s.HiddenProfiles.Contains(d.Id)) continue;
                var user = s.Profiles.FirstOrDefault(p => p.Id == d.Id);
                result.Add(user == null ? d : Merge(d, user));
            }
        // a bare override (no name, no command) whose detected shell is gone (uninstalled, WSL distro removed) is
        // not a profile of its own; it used to show up as a blank, unlaunchable entry
        foreach (var u in s.Profiles)
            if (u.Id.Length > 0 && !(string.IsNullOrWhiteSpace(u.Name) && string.IsNullOrWhiteSpace(u.Command)) && result.All(p => p.Id != u.Id))
            { u.Source ??= "custom"; result.Add(u); }
        if (result.Count == 0) result.Add(Detect()[0]);
        return result;
    }

    private static Profile Merge(Profile detected, Profile user)
    {
        var merged = new Profile { Id = detected.Id, Source = detected.Source };
        foreach (var prop in typeof(Profile).GetProperties().Where(p => p.CanWrite && p.Name != nameof(Profile.Source)))
        {
            var v = prop.GetValue(user);
            prop.SetValue(merged, v is string { Length: 0 } ? prop.GetValue(detected) : v ?? prop.GetValue(detected));
        }
        return merged;
    }

    public static ShellKind KindOf(Profile p)
    {
        if (p.Command.Equals("posh", StringComparison.OrdinalIgnoreCase)) return ShellKind.Posh;
        var name = Path.GetFileNameWithoutExtension(p.Command).ToLowerInvariant();
        return name switch
        {
            "pwsh" or "powershell" => ShellKind.PowerShell,
            "cmd" => ShellKind.Cmd,
            "wsl" => ShellKind.Wsl,
            "bash" or "zsh" or "sh" or "fish" => ShellKind.Bash,
            _ => ShellKind.Other,
        };
    }

    public static string Expand(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = Environment.ExpandEnvironmentVariables(s);
        if (s == "~" || s.StartsWith("~\\") || s.StartsWith("~/")) s = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s[1..];
        return s;
    }

    // Quotes one argument for the standard Windows argv rules (CommandLineToArgvW / the C runtime): backslashes
    // are literal except right before a quote, where they must be doubled. The old version only escaped the
    // quotes, so an argument ending in '\' (a folder path) swallowed the closing quote.
    private static string Q(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { slashes++; continue; }
            sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";
    private static string ToWsl(string p) => System.Text.RegularExpressions.Regex.Replace(p, @"^([A-Za-z]):[\\/]", m => $"/mnt/{char.ToLowerInvariant(m.Groups[1].Value[0])}/").Replace('\\', '/');
    private static string ToMsys(string p) => System.Text.RegularExpressions.Regex.Replace(p, @"^([A-Za-z]):[\\/]", m => $"/{char.ToLowerInvariant(m.Groups[1].Value[0])}/").Replace('\\', '/');

    public static Launch BuildLaunch(Profile prof, string? cwdOverride)
    {
        var kind = KindOf(prof);
        string file; var args = new List<string>();
        if (kind == ShellKind.Posh)
        {
            var posh = ResolvePosh() ?? throw new FileNotFoundException("The built-in posh shell was not found (posh\\posh.exe).");
            file = posh.file; args.AddRange(posh.args);
        }
        else file = Expand(prof.Command);

        if (!string.IsNullOrWhiteSpace(prof.Args)) args.AddRange(SplitArgs(Expand(prof.Args)));

        string? typeIn = null, rawTail = null;
        var script = Expand(prof.StartupScript);
        var cmds = (prof.StartupCommands ?? "").Trim();
        if (script.Length > 0 || cmds.Length > 0)
        {
            switch (kind)
            {
                case ShellKind.Posh:
                case ShellKind.PowerShell:
                    {
                        var parts = new List<string>();
                        if (script.Length > 0) parts.Add(". " + PsQuote(script));
                        if (cmds.Length > 0) parts.Add(cmds.Replace("\r\n", "; ").Replace("\n", "; "));
                        args.AddRange(new[] { "-NoExit", "-Command", string.Join("; ", parts) });
                        break;
                    }
                case ShellKind.Cmd:
                    {
                        var parts = new List<string>();
                        if (script.Length > 0) parts.Add($"call \"{script}\"");
                        if (cmds.Length > 0) parts.Add(cmds.Replace("\r\n", " & ").Replace("\n", " & "));
                        // cmd reads everything after /k verbatim and has no \" escape, so this must not go through Q()
                        rawTail = "/k " + string.Join(" & ", parts);
                        break;
                    }
                default:
                    {
                        var lines = new List<string>();
                        if (script.Length > 0) lines.Add(kind == ShellKind.Wsl ? $"source '{ToWsl(script)}'" : kind == ShellKind.Bash ? $"source '{ToMsys(script)}'" : script);
                        if (cmds.Length > 0) lines.AddRange(cmds.Split('\n').Select(l => l.TrimEnd('\r')));
                        typeIn = string.Join("\r", lines) + "\r";
                        break;
                    }
            }
        }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value ?? "";
        env["TERM"] = "xterm-256color"; env["COLORTERM"] = "truecolor"; env["TERM_PROGRAM"] = "PoshTerm"; env["TERM_PROGRAM_VERSION"] = "1.0.0";
        if (prof.Env != null) foreach (var kv in prof.Env) env[kv.Key] = Expand(kv.Value);

        var dir = Expand(cwdOverride ?? prof.Cwd);
        if (dir.Length == 0 || !Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var line = Q(file) + (args.Count > 0 ? " " + string.Join(" ", args.Select(Q)) : "") + (rawTail != null ? " " + rawTail : "");
        return new Launch(line, dir, env, typeIn);
    }

    public static IEnumerable<string> SplitArgs(string s)
    {
        var sb = new StringBuilder(); bool inQ = false, any = false;
        foreach (var c in s)
        {
            if (c == '"') { inQ = !inQ; any = true; }
            else if (char.IsWhiteSpace(c) && !inQ) { if (any || sb.Length > 0) yield return sb.ToString(); sb.Clear(); any = false; }
            else sb.Append(c);
        }
        if (any || sb.Length > 0) yield return sb.ToString();
    }
}
