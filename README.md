# PoshTerm

A tabbed terminal for Windows with split panes, themes, background images and its own built-in shell,
**posh**, a PowerShell-style object shell. It also hosts PowerShell, Command Prompt, Git Bash, WSL and any
other console program.

- **Terminal:** WPF on .NET 9 with a custom renderer on top of ConPTY.
- **Shell:** posh, written from scratch in Node.js and packed into a standalone executable (Node SEA).
- **Release build:** one self-contained file, `releases\PoshTerm.exe`. The posh shell is embedded in it.

## Using PoshTerm

1. Get `PoshTerm.exe` from the `releases` folder (or from the GitHub Releases page).
2. Double-click it. There is nothing to install and no .NET or Node runtime is needed. Windows 10 or 11, x64.
3. Windows SmartScreen may warn about an unsigned app: choose *More info*, then *Run anyway*.

What to know on first run:

| | |
|---|---|
| Default shell | posh. Other shells it finds (PowerShell, Command Prompt, Git Bash, WSL distros) appear as profiles. |
| posh location | The embedded `posh.exe` unpacks to `%TEMP%\PoshTerm\<version>\posh.exe` the first time you run it. |
| Settings | `%APPDATA%\PoshTerm\settings.json` (Settings screen, or edit the file while PoshTerm runs). |
| Crash details | `%APPDATA%\PoshTerm\crash.log` |

Common keys: new tab `Ctrl+Shift+T`, split `Alt+Shift+=` / `Alt+Shift+-`, move between panes `Alt+Arrows`,
full screen `F11`, command palette `Ctrl+Shift+P`.

The full guide, including profiles, per-profile startup scripts, the posh line editor and every shortcut,
is in [`docs/README.md`](docs/README.md).

## Compiling it

### What you need

| Tool | Version | Used for |
|---|---|---|
| Windows | 10 or 11, x64 | WPF only builds and runs on Windows |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/9.0) | 9.0 or newer | the terminal app and the tests |
| [Node.js](https://nodejs.org) (with npm) | 20+, 22 LTS recommended | building `posh.exe` |

Internet access is needed the first time (NuGet restore and npm install). Visual Studio is optional.

The list above lives in [`requirements.json`](requirements.json), and the build checks it for you
(`scripts\check-requirements.ps1`, run automatically by `build.cmd`):

- A tool that is missing or too old: the script offers to install it with `winget` (Windows asks for
  administrator approval for the .NET SDK). Answer `n`, or run on a machine without winget, and it prints the
  download link instead and stops.
- The posh build tools (`esbuild`, `postject`) are installed locally into `build\posh` with npm, no prompt.
- Anything it cannot fix is listed at the end with exactly what to install, then you run the build again.
  If a fresh install is not picked up, open a new terminal window so the updated PATH loads.

```powershell
.uild.cmd -CheckOnly      # only check, install and build nothing
.uild.cmd -Yes            # install missing tools without asking first
```

### One command

From a normal Command Prompt or PowerShell in this folder:

```powershell
.\build.cmd
```

(`build.cmd` just calls `scripts\build.ps1` with the execution policy bypassed. In PowerShell you can run
`.\scripts\build.ps1` directly.)

It checks the requirements (see above), runs the tests, builds `posh.exe`,
embeds it, publishes the single-file app, and copies the result to **`releases\PoshTerm.exe`**. A full build
takes a few minutes and the exe is around 220 MB because it carries both the .NET and Node runtimes.

Options:

```powershell
.\build.cmd -SkipTests     # skip the test runs
.\build.cmd -Run           # start PoshTerm.exe when the build finishes
```

### By hand

```powershell
# 1. build tools for the posh shell (once)
npm install --prefix build\posh esbuild@0.21.5 postject@1.0.0-alpha.6

# 2. tests
node posh\test.js                                     # posh shell, 152 checks
dotnet run --project tests\EngineTests                # terminal engine + launch lines, 53 checks

# 3. posh.exe: bundle + minify posh, inject it into a copy of node.exe
node scripts\build-posh.mjs dist\posh
copy dist\posh\posh.exe src\PoshTerm\Assets\posh.exe

# 4. the single-file app (picks up Assets\posh.exe and embeds it)
dotnet publish src\PoshTerm\PoshTerm.csproj -c Release -o dist\PoshTerm
```

The result is `dist\PoshTerm\PoshTerm.exe`.

### Developing

Open `PoshTerm.sln` in Visual Studio 2022 (17.12+), or:

```powershell
dotnet build src\PoshTerm\PoshTerm.csproj            # Debug build
dotnet run --project src\PoshTerm\PoshTerm.csproj
```

Debug builds do not embed `posh.exe`. The app falls back to `node posh\posh.js` when `node` is on PATH, so you
can edit the posh shell and test it without rebuilding the executable.

### Troubleshooting

| Problem | Fix |
|---|---|
| `Not ready to build` list at the end of the check | Do what each numbered line says, then run `.\build.cmd` again. |
| `npm install failed` with `Cannot find module` | Your npm install is damaged: reinstall Node.js (`winget install --id OpenJS.NodeJS.LTS -e --force`, or from nodejs.org). |
| `NETSDK` errors or `dotnet` not found | Install the .NET 9 SDK (a runtime alone is not enough). The check reports this. |
| `Access to the path ... PoshTerm.exe is denied` while building | Close any running PoshTerm, then build again. |
| Antivirus flags `posh.exe` or `PoshTerm.exe` | Both are unsigned and self-extracting (Node SEA, .NET single-file); allow-list the build folder. |
| Building from WSL | Don't. WPF needs the Windows toolchain: clone or copy the repo to a Windows path (not `\\wsl.localhost\...`). `scripts/win-sync.sh` is an optional helper for editing in WSL while building on Windows. |

## Repository layout

```
build.cmd             build wrapper (calls scripts\build.ps1)
PoshTerm.sln          Visual Studio solution
releases\             the built PoshTerm.exe goes here (see releases\README.md)
src\PoshTerm\         the WPF terminal (C#)
posh\                 the posh shell (JavaScript)
tests\EngineTests\    terminal engine tests
requirements.json     tools and versions the build needs (checked and installed by the build)
scripts\              build.ps1, check-requirements.ps1, build-posh.mjs, win-sync.sh (optional WSL helper)
docs\README.md        user guide
ARCHITECTURE.md       how the pieces fit together
```

The exe is above GitHub's 100 MB file limit, so `releases\*.exe` is git-ignored. Publish it as a GitHub
Release asset instead (`releases\README.md` has the command).
