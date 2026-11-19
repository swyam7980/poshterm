# PoshTerm — user guide

PoshTerm is a tabbed terminal for Windows with split panes, themes, background images and its own
built-in shell, **posh** (a PowerShell-style object shell). It can also host PowerShell, Command
Prompt, Git Bash, WSL and any other console program.

```
PoshTerm.exe        the terminal and the built-in posh shell, in one file: double-click to start
README.md           this guide
```

No installation is needed. The posh shell is embedded in `PoshTerm.exe` and unpacks itself to
`%TEMP%\PoshTerm\<version>\posh.exe` on first run (it also works on its own from any console). Settings live in `%APPDATA%\PoshTerm\` (see "Files" at the end).

---

## 1. Startup features

### 1.1 Profiles

A *profile* is a shell plus everything about how it starts and looks. PoshTerm detects posh,
PowerShell 7, Windows PowerShell, Command Prompt, Git Bash and every WSL distribution automatically.
Edit them, or add your own, in **Settings (Ctrl+,) → Profiles**:

| Field | What it does |
|---|---|
| Command | The program to run. `posh` means the built-in shell; otherwise a path such as `C:\Program Files\PowerShell\7\pwsh.exe` or `wsl.exe`. |
| Arguments | Extra command-line arguments, e.g. `-NoLogo` or `-d Ubuntu`. |
| Starting directory | Folder the shell opens in. `~` and `%VARIABLES%` work. Splits and duplicated tabs open in the current pane's folder instead, when its shell reports it (posh does). |
| Startup script | A script file run **inside** the shell every time the profile starts (see 1.2). |
| Startup commands | Commands run after the startup script, one per line. |
| Environment variables | `KEY=VALUE`, one per line. `%VAR%` references are expanded. |
| Look | Per-profile color scheme, font, cursor and background; empty fields use the defaults. |

The **default profile** (Settings → Window, or "Set as default" on a profile) is what the `+` button and
Ctrl+Shift+T open. `Ctrl+Shift+1` … `Ctrl+Shift+9` open the 1st … 9th profile; the `˅` button next to
`+` lists them all.

### 1.2 Per-profile startup scripts and commands

These are separate from a shell's own profile (`$PROFILE`, `.bashrc`, …) and only apply to the
PoshTerm profile they are set on. How they run depends on the shell:

| Shell | Startup script | Startup commands |
|---|---|---|
| posh, PowerShell (`pwsh`, `powershell`) | dot-sourced: `. 'C:\path\script.ps1'`, so functions and variables it defines stay available | joined with `;` and run with `-NoExit -Command …` |
| Command Prompt (`cmd`) | `call "C:\path\script.cmd"` | joined with `&` and run with `/k …` |
| Git Bash, bash, zsh, fish | `source '/c/path/script.sh'` (path converted) | typed into the shell once it shows its first prompt |
| WSL | `source '/mnt/c/path/script.sh'` (path converted) | typed into the shell once it shows its first prompt |
| anything else | the script path is typed in and run | typed in |

Example (Settings → Profiles → posh):

```
Startup script     C:\Users\me\scripts\work.posh
Startup commands   Set-Location D:\projects
                   Get-ChildItem | Sort-Object LastWriteTime -Descending | Select-Object -First 5
```

The same thing in `settings.json`:

```jsonc
"profiles": [
  {
    "id": "posh",
    "startupScript": "C:\\Users\\me\\scripts\\work.posh",
    "startupCommands": "Set-Location D:\\projects\nGet-ChildItem | Select-Object -First 5",
    "env": { "PROJECT": "demo" }
  }
]
```

If a profile fails to start, the pane shows the error; fix the profile and press **Enter** in the pane
to retry.

### 1.3 posh's own startup

posh always looks for a **profile script** at `~\.posh\profile.posh` (the path is in `$PROFILE`) and runs
it at every start, before any startup script or command. Create it with:

```powershell
New-Item -ItemType File -Force $PROFILE
notepad $PROFILE
```

posh command-line switches (usable in a profile's *Arguments*, or when running the unpacked `posh.exe` directly):

| Switch | Meaning |
|---|---|
| `-NoProfile` | don't run `~\.posh\profile.posh` |
| `-NoLogo` | hide the startup banner |
| `-NoExit` | stay interactive after `-Command` / `-File` |
| `-Command <text>` | run the rest of the command line as posh code, then exit |
| `-EncodedCommand <base64>` | like `-Command`, base64 of UTF-16LE text (PowerShell-compatible) |
| `-File <script.posh> [args…]` | run a script with arguments, then exit (`$args` holds the arguments) |
| `-WorkingDirectory <dir>` | start in this folder |
| `-NonInteractive` | never prompt (`Read-Host` fails instead) |
| `-Version`, `-Help` | print the version / the usage text |

Switches can be shortened (`-nop`, `-c`, `-wd`) and are case-insensitive. A first argument that isn't a
switch is treated as a script file: `posh build.posh release`.

Command history is kept in `~\.posh\history.jsonl`.

---

## 2. Tabs and split panes

* **New tab:** `+` button or Ctrl+Shift+T (default profile); Ctrl+Shift+1…9 for a specific profile.
* **Split the current pane:** Alt+Shift+= (side by side), Alt+Shift+- (one above the other), Alt+Shift+D
  (picks the direction from the pane's shape). Also on the right-click menu of a pane or tab. The new
  pane uses the same profile and folder; the command palette has "Split with: <profile>" for others.
* **Move between panes:** Alt+←/→/↑/↓, or click a pane. The active pane gets a thin outline.
* **Resize panes:** drag the divider, or Alt+Shift+←/→/↑/↓.
* **Maximize one pane** (and restore the layout): Ctrl+Shift+Z.
* **Close a pane:** Ctrl+Shift+W, or exit the shell. Closing the last pane closes the tab.
* **Tabs:** drag to reorder, double-click to rename (Ctrl+Shift+R), middle-click to close, right-click
  for Duplicate / Split / Close other tabs. Ctrl+Tab and Ctrl+Shift+Tab cycle; Ctrl+Alt+1…9 jump.
* **Window:** Ctrl+Shift+N opens a second window. F11 or Alt+Enter toggles full screen.

## 3. Default keyboard shortcuts

Every shortcut can be changed, removed or added in **Settings → Keyboard** (or in `settings.json` under
`"keybindings"`, e.g. `"ctrl+shift+t": "newTab"`, or `"unbound"` to remove one).

| Keys | Action |
|---|---|
| Ctrl+Shift+T | New tab |
| Ctrl+Shift+1 … 9 | New tab with profile 1 … 9 |
| Ctrl+Shift+D | Duplicate tab |
| Ctrl+Shift+W | Close pane |
| Ctrl+Tab / Ctrl+PageDown | Next tab |
| Ctrl+Shift+Tab / Ctrl+PageUp | Previous tab |
| Ctrl+Alt+1 … 9 | Go to tab 1 … 9 |
| Ctrl+Shift+R | Rename tab |
| Ctrl+Shift+N | New window |
| Alt+Shift+= / Alt+Shift+- / Alt+Shift+D | Split right / down / auto |
| Alt+Arrow | Focus pane in that direction |
| Alt+Shift+Arrow | Move the divider |
| Ctrl+Shift+Z | Maximize / restore pane |
| Ctrl+C / Ctrl+Shift+C | Copy (Ctrl+C copies only when text is selected; otherwise it interrupts the shell) |
| Ctrl+V / Ctrl+Shift+V / Shift+Insert | Paste |
| Ctrl+Shift+A | Select all |
| Ctrl+Shift+F | Find (Enter / Shift+Enter: next / previous; Aa = match case; .* = regex) |
| Ctrl+Shift+K | Clear the buffer and scrollback |
| Ctrl+Shift+Up / Down, PageUp / PageDown, Home / End | Scroll lines / pages / to top or bottom |
| Ctrl+= / Ctrl+- / Ctrl+0 | Bigger / smaller / reset font in this pane |
| Ctrl+Shift+] / Ctrl+Shift+[ | More / less background opacity |
| Ctrl+Shift+B | Show / hide the background image |
| F11 / Alt+Enter | Full screen |
| Ctrl+Shift+P | Command palette (search every action, profile, color scheme and tab) |
| Ctrl+, | Settings (Esc or the × button closes it) |
| Ctrl+Shift+, | Open settings.json |
| Ctrl+` | Quake hotkey: show / hide PoshTerm from anywhere (Settings → Window) |

**Mouse:** drag to select (double-click = word, triple-click = line). Right-click copies the selection or
pastes (or shows a menu, if "Right-click copy / paste" is off). Middle-click pastes. Ctrl+click opens a
link. When a program uses the mouse itself (vim, htop…), hold Shift to select text anyway.

## 4. The posh line editor

| Keys | Action |
|---|---|
| Tab / Shift+Tab | complete commands, -Parameters, parameter values, paths and $variables |
| Ctrl+Space | list all completions |
| → or End at the end of the line | accept the grey history prediction (Ctrl+→ accepts one word) |
| ↑ / ↓ | history (prefix search when something is typed) |
| Ctrl+R | search history; Ctrl+R again for older matches, Enter runs the match, Esc puts it on the line for editing, Ctrl+G cancels |
| Shift+Enter | insert a new line (multi-line input); unfinished blocks continue automatically |
| Ctrl+←/→, Ctrl+Backspace / Ctrl+Delete | move / delete by word |
| Home / End, Ctrl+A / Ctrl+E | start / end of line |
| Ctrl+U / Ctrl+K | delete to start / end of line |
| Ctrl+Z | undo |
| Esc | clear the line |
| Ctrl+C | cancel the line, or stop the running command |
| Ctrl+L | clear the screen |

Type `help` for the language overview, `Get-Command` for every command, and `Get-Help <command>` for
details.

## 5. Appearance

Settings → **Appearance**: color scheme (14 built in; "Import Windows Terminal color schemes" copies
yours from Windows Terminal), font, size, weight, line height, cursor, padding, scrollback length.
Settings → **Background**: an image (file or URL) with sizing, alignment, opacity and blur, plus the
opacity of the background color. Settings → **Window**: window opacity, Acrylic / Mica material
(Windows 11 22H2+; lower the background opacity to see it), Quake hotkey, start maximized.

Every change previews live and is saved automatically.

## 6. Files

| File | Purpose |
|---|---|
| `%APPDATA%\PoshTerm\settings.json` | all settings. Edits made in a text editor are picked up while PoshTerm runs; comments and trailing commas are allowed. Settings → About → "Reset all settings" restores defaults. |
| `%APPDATA%\PoshTerm\window-state.json` | last window size and position |
| `%APPDATA%\PoshTerm\crash.log` | details of any error PoshTerm reports |
| `%TEMP%\PoshTerm\<version>\posh.exe` | the embedded posh shell, unpacked on first run |
| `~\.posh\profile.posh` | posh profile script |
| `~\.posh\history.jsonl` | posh command history |
