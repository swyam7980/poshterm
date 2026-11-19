# Checks everything the build needs (requirements.json), installs what it can, and tells you what to do
# yourself for the rest.
#
#   scripts\check-requirements.ps1               check; offer to install missing tools with winget
#   scripts\check-requirements.ps1 -Yes          install missing tools without asking
#   scripts\check-requirements.ps1 -CheckOnly    only report, change nothing
#   scripts\check-requirements.ps1 -DryRun       show the winget/npm commands instead of running them
#
# Exit code 0 = ready to build, 1 = something is still missing (the list says what and where to get it).
[CmdletBinding()]
param([switch]$CheckOnly, [switch]$Yes, [switch]$DryRun, [string]$Requirements)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Requirements) { $Requirements = Join-Path $root 'requirements.json' }
$req = Get-Content $Requirements -Raw | ConvertFrom-Json
$unresolved = New-Object System.Collections.Generic.List[string]

function Ok($m)   { Write-Host "  [ok]      $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [missing] $m" -ForegroundColor Yellow }
function Bad($m)  { Write-Host "  [fail]    $m" -ForegroundColor Red }

function Update-SessionPath {
    # a fresh winget install changes the machine/user PATH, not this process's copy of it
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

function Confirm-Action($question) {
    if ($Yes) { return $true }
    try { $a = Read-Host "$question [Y/n]" } catch { return $false }   # no console to ask: treat as "no"
    return ($a -eq '' -or $a -match '^[yY]')
}

# 'missing' | 'broken' | [version] (highest version the tool reports)
function Get-ToolVersion($t) {
    if (-not (Get-Command $t.command -ErrorAction SilentlyContinue)) { return 'missing' }
    try { $out = (& $t.command @($t.versionArgs) 2>&1 | Out-String) } catch { return 'broken' }
    if ($LASTEXITCODE -ne 0) { return 'broken' }
    $versions = @([regex]::Matches($out, '\d+(\.\d+)+') | ForEach-Object { try { [version]$_.Value } catch { } } | Where-Object { $_ })
    if ($versions.Count -eq 0) { return 'broken' }
    return ($versions | Sort-Object -Descending | Select-Object -First 1)
}

function Test-Tool($t) {
    $v = Get-ToolVersion $t
    if ($v -eq 'missing') { return @{ State = 'missing'; Text = "$($t.name) not found on PATH" } }
    if ($v -eq 'broken')  { return @{ State = 'broken';  Text = "$($t.name) is installed but does not run correctly" } }
    if ($v -lt [version]$t.min) { return @{ State = 'old'; Text = "$($t.name) $v found, $($t.min) or newer is required" } }
    return @{ State = 'ok'; Text = "$($t.name) $v" }
}

function Install-WithWinget($t) {
    if (-not $t.wingetId) { return $false }
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Write-Host '            (winget is not available on this machine)'; return $false }
    $wargs = @('install', '--id', $t.wingetId, '-e', '--accept-package-agreements', '--accept-source-agreements')
    if ($DryRun) { Write-Host "            dry run: winget $($wargs -join ' ')"; return $false }
    if (-not (Confirm-Action "            Install $($t.name) now with winget ($($t.wingetId))? Windows may ask for administrator approval.")) { return $false }
    & winget @wargs
    Update-SessionPath
    return $true
}

# ---- Windows -------------------------------------------------------------------------------------
Write-Host 'Windows'
if ($env:OS -ne 'Windows_NT') {
    Bad 'not running on Windows. PoshTerm is a WPF app and can only be built on Windows 10/11, not on Linux, macOS or WSL.'
    $unresolved.Add('Run the build from Windows PowerShell or Command Prompt, on a Windows path (not \\wsl.localhost\...).')
} elseif (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Build -lt $req.windows.minBuild) {
    Bad "$($req.windows.name) is required ($($req.windows.why)); this is build $([Environment]::OSVersion.Version.Build)."
    $unresolved.Add("Use $($req.windows.name).")
} else { Ok "Windows build $([Environment]::OSVersion.Version.Build)" }

# ---- tools ---------------------------------------------------------------------------------------
Write-Host 'Tools'
foreach ($t in $req.tools) {
    $r = Test-Tool $t
    if ($r.State -eq 'ok') { Ok $r.Text; continue }
    Warn "$($r.Text)  ($($t.why))"
    if (-not $CheckOnly -and (Install-WithWinget $t)) {
        $r = Test-Tool $t
        if ($r.State -eq 'ok') { Ok "$($r.Text) (just installed)"; continue }
        Write-Host '            installed, but not usable from this window yet: close it, open a new terminal and run the build again.'
    }
    $unresolved.Add("$($t.name) $($t.min)+ : install it from $($t.url)" + $(if ($t.wingetId) { "  (or: winget install --id $($t.wingetId) -e)" } else { '' }))
}

# ---- npm build tools -----------------------------------------------------------------------------
Write-Host 'posh build tools'
$n = $req.npm
$dir = Join-Path $root $n.installDir
function Get-Installed($pkg) {
    $f = Join-Path $dir "node_modules\$($pkg.name)\package.json"
    if (Test-Path $f) { return (Get-Content $f -Raw | ConvertFrom-Json).version } else { return $null }
}
$todo = @($n.packages | Where-Object { (Get-Installed $_) -ne $_.version })
foreach ($p in $n.packages) { if ($todo -notcontains $p) { Ok "$($p.name) $($p.version)" } }
if ($todo.Count -gt 0) {
    foreach ($p in $todo) { Warn "$($p.name) $($p.version) is not installed in $($n.installDir)" }
    if ($CheckOnly) {
        Write-Host '            the build installs these with npm'
    } else {
        $r = Test-Tool $n
        if ($r.State -ne 'ok') {
            Bad "$($r.Text)  ($($n.why))"
            $unresolved.Add("npm is needed to install $((($todo | ForEach-Object { $_.name }) -join ', ')): it ships with Node.js, so reinstall Node.js from $($n.url)")
        } else {
            $specs = @($todo | ForEach-Object { "$($_.name)@$($_.version)" })
            New-Item -ItemType Directory -Force $dir | Out-Null
            $pj = Join-Path $dir 'package.json'
            if (-not (Test-Path $pj)) { Set-Content $pj '{"name":"posh-build","private":true}' }
            if ($DryRun) { Write-Host "            dry run: npm install --prefix $($n.installDir) $($specs -join ' ')" }
            else {
                & $n.command install --prefix $dir --no-audit --no-fund @specs
                if ($LASTEXITCODE -ne 0) { $unresolved.Add("npm install failed. Run by hand and read the error: npm install --prefix $($n.installDir) $($specs -join ' ')  (if it says Cannot find module, your npm is damaged: reinstall Node.js from $($n.url))") }
                else { foreach ($p in $todo) { if ((Get-Installed $p) -eq $p.version) { Ok "$($p.name) $($p.version) (just installed)" } else { Bad "$($p.name) did not install"; $unresolved.Add("$($p.name) $($p.version) did not install into $($n.installDir)") } } }
            }
        }
    }
}

# ---- result --------------------------------------------------------------------------------------
if ($unresolved.Count -gt 0) {
    Write-Host "`nNot ready to build. Please do the following, then run the build again:" -ForegroundColor Red
    $i = 1; foreach ($u in $unresolved) { Write-Host "  $i. $u"; $i++ }
    exit 1
}
Write-Host "`nAll requirements are met." -ForegroundColor Green
exit 0
