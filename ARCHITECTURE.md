# PoshTerm — Feature List & Architecture

Status as of 2026-10-12. ~9,700 lines of source (C# WPF app + Node.js shell engine).

## 1. What this is

Two independent products that ship together:

1. **PoshTerm** — a native Windows terminal app (WPF, C#, .NET 9). Custom-drawn
   VT/ANSI renderer, ConPTY-hosted shells, tabs/splits, theming, hotkeys.
2. **posh** — a from-scratch PowerShell-like shell (Node.js, no PowerShell
   assemblies or code reused). One of several shells PoshTerm can host; the
   original pwsh 7.6.6 install (`old_shell/`) is just another selectable
   profile, not a dependency.

They ship as one file: `PoshTerm.exe` (the terminal) with `posh.exe` (the shell,
bundled standalone via Node SEA) embedded as a resource and unpacked to a temp folder on first run.
(`scripts/win-sync.sh package` still produces the older two-file folder layout.)

---

## 2. Feature list: promised vs. implemented

### 2.1 Terminal features (the 10 approved in the original proposal)

| # | Feature | Status | Where |
|---|---|---|---|
| 1 | Background images (fit/fill/tile/center, opacity, blur) | ✅ Implemented | `UI/PaneView.cs` |
| 2 | Custom fonts (any installed font, size, weight, ligatures) | ✅ Implemented | `UI/SettingsPanel.cs` (font picker), `Terminal/TerminalView.cs` (glyph rendering) |
| 3 | Built-in + imported (Windows Terminal) color schemes | ✅ Implemented — 14 built-in schemes + WT scheme JSON import | `Config/BuiltIns.cs`, `UI/SettingsPanel.cs` |
| 4 | Multi-shell tabs + splits | ✅ Implemented — binary split-pane tree, drag reorder, zoom/maximize pane | `UI/MainWindow.xaml.cs` |
| 5 | Named profiles (shell + args + cwd + env + theme + font + background) | ✅ Implemented | `Config/Settings.cs`, `UI/SettingsPanel.cs` |
| 6 | Per-profile startup scripts (separate from `$PROFILE`) | ✅ Implemented — injected per shell kind at launch | `Core/Shells.cs` |
| 7 | Full rebindable shortcuts + Quake-style global hotkey | ✅ Implemented — keymap editor, `WM_HOTKEY` global toggle | `Config/BuiltIns.cs` (`Chord`, `DefaultKeys`), `Core/Win32.cs`, `UI/SettingsPanel.cs` |
| 8 | Command palette | ✅ Implemented — fuzzy search over actions/profiles/schemes/tabs | `UI/MainWindow.Overlays.cs` |
| 9 | Buffer search / hyperlinks / copy-paste | ✅ Implemented — regex find bar, Ctrl+click links, selection+copy | `Terminal/TerminalView.cs`, `Terminal/TerminalView.Input.cs` |
| 10 | Settings UI + hot-reload `settings.json` | ✅ Implemented — `FileSystemWatcher`-driven live reload | `App.xaml.cs`, `UI/SettingsPanel.cs` |

### 2.2 posh shell v1 scope (approved)

| Area | Promised | Status |
|---|---|---|
| Language | Verb-Noun commands, aliases, typed object pipeline, variables (`$_`,`$?`,`$LASTEXITCODE`,`$env:`), string interpolation/literals/here-strings, arrays/hashtables/ranges, property/method access, full operator set, all control-flow forms, functions with `param()`, script blocks, redirection, external `.exe` exec | ✅ Implemented — `posh/parser.js`, `posh/interp.js`, `posh/values.js` |
| Cmdlets | ~50 built-ins | ✅ Implemented, exceeded — ~80+ in `posh/cmdlets.js` (adds Get-Member, Compare-Object, Tee-Object, New-Object, Add-Member, JSON/CSV conversion, clipboard, PSDrive, Test-Connection, etc.) |
| Line editor | PSReadLine-equivalent: highlighting, history, Tab completion, inline predictions, Ctrl+R search | ✅ Implemented — `posh/editor.js` |
| Startup | `$PROFILE`, `-Command`, `-File`, `-NoProfile`, `-NoLogo`, `-NoExit`, `-WorkingDirectory`, per-profile startup scripts | ✅ Implemented — `posh/posh.js` |
| Explicitly out of v1 | Remoting/WSMan, CIM/WMI, `[Type]::Method()` reflection beyond a curated static-member set, classes, DSC, background jobs, execution policy/signing, module gallery | ⛔ Not built, as agreed. (Note: a **curated subset** of static `[Type]::Member` access *is* implemented in `values.js` for common types — math/datetime/string/io/convert/etc. — because it's cheap and commonly used; full reflection is not.) |

**Verification:** `posh/test.js` — 151/151 checks passing (Windows node). `tests/EngineTests` — 44/44 checks passing (VT parser + terminal buffer, pure .NET, no Windows dependency).

### 2.3 Packaging requirement

> "the final implementation would still be a exe file with just some normal folders... main exe with supporting files... so the final product can't be reversed easily"

| Promised | Status |
|---|---|
| Ships as normal-looking app folder (exe + supporting files, not loose script files) | ✅ `dist/PoshTerm/PoshTerm.exe` + `dist/PoshTerm/posh/posh.exe`, self-contained .NET publish + Node SEA |
| Hard to reverse | ⚠️ **Partially, honestly scoped down.** Minified + bundled into a single-file Node SEA executable (no loose `.js` on disk). **No obfuscation layer was added** — an earlier plan to add one was dropped (it tripped a safety filter mid-build, and more importantly wouldn't have delivered real protection). This raises the bar against casual inspection; it does not make the code "unreversible" against a motivated reverse engineer with a decompiler. Flagged to the user already — not a silent gap. |

---

## 3. Architecture

### 3.1 Top-level layout

```
PoshTerm/
├── posh/              Node.js shell engine (standalone, no PowerShell deps)
├── scripts/           Build tooling (build.ps1, build-posh.mjs; win-sync.sh = optional WSL helper)
├── src/PoshTerm/       WPF C# app (net9.0-windows, win-x64)
└── tests/EngineTests/  Standalone .NET test project for the VT/terminal layer
```

The app runs on Windows and builds natively there (`build.cmd` /
`scriptsuild.ps1`: .NET 9 SDK + Node). WPF can't be built or run under
Linux; `scripts/win-sync.sh` is an optional helper for developers who edit
the source from WSL and want to drive the Windows toolchain from there.

### 3.2 posh — the shell engine (`posh/`)

A tree-walking interpreter over a hand-written recursive-descent parser,
run as a child process under ConPTY. No `System.Management.Automation` or
any PowerShell code — this is why it can't do real .NET reflection, classes,
remoting, etc. (deliberately out of scope).

| File | Role |
|---|---|
| `parser.js` | Tokenizes and parses PowerShell-like syntax: dual command-mode/expression-mode lexing, string interpolation, here-strings, hashtables, script blocks, `param()` blocks, all control-flow forms. |
| `interp.js` | Tree-walking interpreter: scopes, pipeline execution (**materialized, not streamed** — a deliberate simplification, see `ponytail:` note below), command resolution (cmdlet vs. function vs. external `.exe`/`.posh` script), argument binding, external process spawn (`spawnSync`, with special handling for `.cmd`/`.bat`/`.ps1`), script block invocation including `begin`/`process`/`end` blocks. |
| `values.js` | PowerShell-style value semantics: truthiness/coercion rules, .NET-style numeric formatting, type casts, wildcard/regex matching, comparison/binary operators, member access including a curated set of static `[Type]::Member` calls (math, datetime, string, environment, io.path/file/directory, numeric types, guid, regex, convert, text.encoding, console, bool, timespan), composite .NET format strings, date formatting. |
| `format.js` | Output formatting: table/list views, per-type renderers (file listings colored by extension, processes, group-by results, hashtables, regex matches, etc.) — mirrors what `Format-Table`/`Format-List`/the default formatter do in real PowerShell. |
| `cmdlets.js` | ~80+ built-in cmdlets (file ops, pipeline ops, data conversion, web requests, process management, date/random/guid, clipboard, PSDrive, etc.). |
| `editor.js` | The PSReadLine-equivalent line editor: raw-mode input, ANSI syntax highlighting, history with prefix search, Tab completion, inline history-based predictions, Ctrl+R reverse search, undo, multi-line editing, bracketed paste. |
| `posh.js` | Entry point: CLI arg parsing, profile loading (`~/.posh/profile.posh`), the REPL loop, Tab-completion wiring. |
| `test.js` | Self-check suite — 151 checks. |

`ponytail:` note — the pipeline is materialized (each stage runs to
completion before the next starts) rather than streamed element-by-element
like real PowerShell's pipeline. Simpler interpreter, same observable
behavior for everything in v1 scope; would need revisiting only if posh
grows long-running/streaming cmdlets (e.g. `Get-Content -Wait`).

**Shipping form:** `scripts/build-posh.mjs` bundles `posh.js` and everything
it `require()`s via esbuild (minified, CJS, Node 22 target), turns that into
a V8 snapshot blob via Node's `--experimental-sea-config`, and injects that
blob into a copy of `node.exe` via `postject`. Result: one `posh.exe`,
~82 MB, no Node install or loose `.js` files needed on the target machine.

### 3.3 PoshTerm — the WPF app (`src/PoshTerm/`)

```
App.xaml(.cs)        startup, global exception → crash.log, settings load +
                      hot-reload (FileSystemWatcher), light/dark theme switch

Terminal/             the custom VT100/ANSI terminal — no embedded console
  Cell.cs               per-cell data: text, fg/bg (default/palette/rgb), flags
  TerminalBuffer.cs      screen grid: active area + scrollback ring, cursor,
                         scroll region, alt screen, resize logic
  VtParser.cs            escape-sequence interpreter: C0/CSI/OSC, SGR (256-color
                         + truecolor, both ; and : forms), DECSC/DECRC, private
                         modes (bracketed paste, mouse modes, alt screen, cursor
                         shape/visibility), OSC title/cwd/clipboard/progress,
                         incremental UTF-8 decode (surrogate pairs, combining
                         marks, East-Asian/emoji wide-char widths)
  TerminalView.cs         FrameworkElement renderer: draws the cell grid via
                         DrawingContext using fixed-advance GlyphRuns (not
                         per-char FormattedText, to keep the grid aligned —
                         see bug #5 below), cursor blink/shapes, scrollback view
  TerminalView.Input.cs   selection (click/double/triple/drag), mouse reporting
                         (SGR + legacy encoding), hyperlink detection + Ctrl-click,
                         find/search, keyboard → escape-sequence encoding

Pty/
  ConPty.cs             Win32 ConPTY wrapper (P/Invoke): CreatePseudoConsole,
                        STARTUPINFOEX w/ PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                        CreateProcessW with a custom env block, async output
                        pump thread, resize, off-thread dispose

Config/
  Settings.cs           AppSettings/Profile/Look/Window/ColorScheme POCOs,
                        JSONC load (tolerant of comments/trailing commas),
                        atomic write-then-rename save
  BuiltIns.cs            14 built-in color schemes (Windows-Terminal-shaped
                        JSON, so WT scheme files import as-is), Chord (key
                        combo parse/normalize), DefaultKeys (full default
                        keybinding table)

Core/
  Shells.cs              shell detection (posh, bundled old_shell/pwsh.exe,
                        system pwsh, Windows PowerShell, cmd, Git Bash, WSL
                        distros via `wsl -l -q`), profile resolution, launch
                        command building incl. per-shell-kind startup-script
                        injection and WSL/MSYS path translation
  Win32.cs                DWM backdrop (acrylic/mica/tabbed), dark title bar,
                        window opacity, global hotkey register (WM_HOTKEY)

UI/
  MainWindow.xaml(.cs)    custom-chrome window (WindowChrome, no OS titlebar),
                        tab lifecycle, binary split-pane tree + GridSplitters,
                        pane focus/resize by direction, window-bounds
                        persistence, close confirmation, global Quake-hotkey
                        toggle
  MainWindow.Overlays.cs  generic overlay host, find bar, command palette
                        (fuzzy search over actions/profiles/schemes/tabs),
                        profile dropdown menu, tab/pane context menus
  TabHeader.cs            tab control: icon, inline-rename title, close
                        button, OSC 9;4 progress bar, bell flash, drag-reorder
  PaneView.cs             one terminal pane: layered background (color +
                        image with stretch/tile/blur), ConPty session
                        lifecycle incl. close-on-exit policy, per-pane font
                        zoom, bell, clipboard, right-click behavior
  SettingsPanel.cs        settings overlay: Appearance / Background / Window /
                        Profiles / Keyboard / About pages, live-previewed,
                        debounce-saved to settings.json
  Actions.cs              central action-id → behavior registry shared by
                        keybindings, menus, and the command palette
```

### 3.4 Data flow (one pane, end to end)

```
settings.json ──▶ Settings.Load() ──▶ Profile ──▶ Shells.BuildLaunch()
                                                         │
                                                         ▼
                                               ConPty.Start(cmdline, env, cwd)
                                                         │
                               stdout/stderr ◀───────────┘
                                     │
                                     ▼
                              VtParser.Feed(bytes)
                                     │
                         mutates TerminalBuffer (cells, cursor, scroll)
                                     │
                                     ▼
                          TerminalView invalidates ──▶ DrawingContext paint
                                     ▲
                    keyboard/mouse ──┘ (TerminalView.Input → ConPty.Write)
```

For a `posh` profile, the child process behind ConPty is `posh/posh.exe`
running its own REPL (parser → interp → cmdlets → format → ANSI-colored
stdout) — PoshTerm's renderer doesn't know or care that it isn't real pwsh.

### 3.5 Build/test pipeline

```
build.cmd                     (Windows) tests + posh.exe + publish -> releases\PoshTerm.exe
scriptsuild.ps1 -SkipTests  the same without the test runs

Optional WSL helper, same steps driven from Linux:
scripts/win-sync.sh build     dotnet build (Debug), WPF app only
scripts/win-sync.sh run       build + launch
scripts/win-sync.sh posh-test Windows node posh/test.js   (152 checks)
scripts/win-sync.sh posh-exe  esbuild → SEA blob → postject → posh.exe
scripts/win-sync.sh publish   dotnet publish (Release, self-contained,
                               single-file, ReadyToRun) → dist/PoshTerm
scripts/win-sync.sh package   posh-exe + publish, assembled into
                               dist/PoshTerm/{PoshTerm.exe, posh/posh.exe}

tests/EngineTests/            dotnet run — 53 checks against Cell/
                               TerminalBuffer/VtParser directly, no WPF/
                               Windows dependency, runs fine under WSL
```

`win-sync.sh` rsyncs the repo to a real Windows path first (WPF/native
builds can't target a `\\wsl.localhost\...` UNC path) and installs
`posh-exe`'s build deps (esbuild, postject) via WSL's npm targeting
`--os=win32 --cpu=x64`. None of that is needed when building on Windows.

---

## 4. Known bugs fixed this session (kept for history — all verified against the running app)

1. Parser: statements directly after a closing block (`param()`/`}`/`)`)
   without a separator weren't accepted — fixed in `parser.js` statementList().
2. `TerminalBuffer.Resize` crash (`IndexOutOfRangeException`): `Cols` was
   updated *after* `AdjustHeight` ran, so new lines were built with the old
   width. Fixed order; added an `EngineTests` regression case.
3. Cursor-Y adjustment during resize applied to the wrong buffer (main vs.
   alt-screen) — added a `moveCursor` parameter so only the visible buffer's
   cursor moves.
4. Context-menu text unreadable: `ContextMenu` inherited the icon font
   (no Latin glyphs) from its anchor button. Fixed via `MainWindow.TextMenu()`.
5. Monospace grid drift: per-character `FormattedText` let proportional
   fallback glyphs drift off the cell grid. Fixed with `TryGlyphRun` —
   fixed-advance `GlyphRun` for ASCII runs, falling back to `FormattedText`
   only for glyphs outside the font's ASCII map.
6. Overlay focus: Escape/typing sometimes didn't reach the command palette/
   settings overlay. Fixed by forcing `Focusable=true` + explicit focus
   dispatch in `ShowOverlay`.

---

## 5. Explicitly not done / open items

- No `.gitignore` yet for `bin/`/`obj/`/`dist/`/`build/` — check before
  any commit (not yet a git repo at all, per environment: `Is a git
  repository: false`).
- No git commits made for this work yet.
- Settings panel's "Reset all settings", full keyboard re-record flow,
  Windows-Terminal-scheme-import flow, and the Quake hotkey toggle are
  implemented and build clean but weren't individually screenshot-verified
  (only code-reviewed) — everything else in §2 was.
- No obfuscation layer on `posh.exe` (see §2.3) — minified/bundled only.
