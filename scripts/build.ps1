# Builds PoshTerm on Windows into ONE file: releases\PoshTerm.exe (the posh shell is embedded in it).
#
#   build.cmd                  check requirements, run the tests, build everything
#   build.cmd -SkipTests       same, without the test runs
#   build.cmd -Run             start PoshTerm.exe when the build finishes
#   build.cmd -Yes             install missing tools (winget) without asking first
#   build.cmd -CheckOnly       only check requirements.json, build nothing
#
# Steps:
#   1. scripts\check-requirements.ps1: reads requirements.json, checks the .NET SDK, Node and npm, offers to
#      install what is missing (winget) or tells you what to install yourself, and installs the posh build
#      tools (esbuild, postject) into build\posh\node_modules
#   2. node posh\test.js and the .NET engine tests                    (unless -SkipTests)
#   3. node scripts\build-posh.mjs   -> dist\posh\posh.exe            (posh bundled into a Node single-exe)
#   4. copy posh.exe to src\PoshTerm\Assets\ so the Release build embeds it
#   5. dotnet publish (self-contained, single-file, ReadyToRun)       -> dist\PoshTerm\PoshTerm.exe
#   6. copy it to releases\PoshTerm.exe
[CmdletBinding()]
param([switch]$SkipTests, [switch]$Run, [switch]$Yes, [switch]$CheckOnly)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Run([scriptblock]$block, [string]$what) {
    & $block
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

Step 'Checking requirements'
& "$PSScriptRoot\check-requirements.ps1" -Yes:$Yes -CheckOnly:$CheckOnly
if ($LASTEXITCODE -ne 0) { exit 1 }
if ($CheckOnly) { exit 0 }

if (-not $SkipTests) {
    Step 'posh self-check'
    Run { node posh\test.js } 'posh self-check'
    Step 'Engine tests'
    Run { dotnet run --project tests\EngineTests -c Release --nologo } 'engine tests'
}

Step 'Building posh.exe'
Run { node scripts\build-posh.mjs dist\posh } 'posh build'
Copy-Item dist\posh\posh.exe src\PoshTerm\Assets\posh.exe -Force

Step 'Publishing PoshTerm.exe'
if (Test-Path dist\PoshTerm) { Remove-Item dist\PoshTerm -Recurse -Force }
Run { dotnet publish src\PoshTerm\PoshTerm.csproj -c Release --nologo -o dist\PoshTerm } 'dotnet publish'

Step 'Copying to releases\'
New-Item -ItemType Directory -Force releases | Out-Null
Copy-Item dist\PoshTerm\PoshTerm.exe releases\PoshTerm.exe -Force
$f = Get-Item releases\PoshTerm.exe
Write-Host ("done: {0} ({1:N0} MB)" -f $f.FullName, ($f.Length / 1MB)) -ForegroundColor Green

if ($Run) { Start-Process $f.FullName }
