@echo off
rem Builds releases\PoshTerm.exe. Wrapper so the build also works where PowerShell scripts are blocked by execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build.ps1" %*
