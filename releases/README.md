# Releases

`PoshTerm.exe` is the single-file build (terminal + embedded posh shell). `scripts\build.ps1` writes it here.

The exe is over 200 MB, so it is not committed to git (GitHub rejects files above 100 MB).
Publish it with a GitHub Release instead:

```powershell
gh release create v1.0.0 releases\PoshTerm.exe --title "PoshTerm 1.0.0" --notes "Single-file Windows x64 build."
```
