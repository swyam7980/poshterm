#!/usr/bin/env bash
# Dev helper for WSL: mirrors the source to a Windows folder and runs the Windows .NET SDK there
# (WPF cannot be built or run from Linux).
#   scripts/win-sync.sh build      - Debug build
#   scripts/win-sync.sh run        - build and launch PoshTerm
#   scripts/win-sync.sh publish    - Release build into dist/PoshTerm (also copies it back here)
#   scripts/win-sync.sh posh-test  - run the posh self-check with Windows node
#   scripts/win-sync.sh posh-exe   - build the standalone posh.exe (bundled Node SEA)
#   scripts/win-sync.sh package    - publish + posh-exe combined into dist/PoshTerm (the shippable app folder)
set -euo pipefail
SRC="$(cd "$(dirname "$0")/.." && pwd)"
WIN_HOME="$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')"
DEST="$(wslpath "$WIN_HOME")/PoshTerm-dev"
WIN_DEST="$WIN_HOME\\PoshTerm-dev"
mkdir -p "$DEST"
rsync -a --delete --exclude node_modules --exclude bin --exclude obj --exclude dist --exclude .git "$SRC/" "$DEST/"
taskkill.exe /F /IM PoshTerm.exe >/dev/null 2>&1 || true   # a running copy would lock the output exe
win() { cmd.exe /c "cd /d $WIN_DEST && $1" 2>&1 | tr -d '\r'; }
case "${1:-}" in
  build)     win "dotnet build src\\PoshTerm\\PoshTerm.csproj -c Debug -nologo -v q -clp:NoSummary" ;;
  run)       win "dotnet build src\\PoshTerm\\PoshTerm.csproj -c Debug -nologo -v q -clp:NoSummary" && win "start \"\" src\\PoshTerm\\bin\\Debug\\net9.0-windows\\win-x64\\PoshTerm.exe" ;;
  publish)   win "dotnet publish src\\PoshTerm\\PoshTerm.csproj -c Release -nologo -o dist\\PoshTerm" && rm -rf "$SRC/dist/PoshTerm" && mkdir -p "$SRC/dist" && cp -r "$DEST/dist/PoshTerm" "$SRC/dist/" ;;
  posh-test) win "node posh\\test.js" ;;
  posh-exe)
    mkdir -p "$DEST/build/posh" && cd "$DEST/build/posh" && [ -f package.json ] || echo '{"name":"posh-build","private":true}' > package.json
    npm install --no-audit --no-fund --os=win32 --cpu=x64 esbuild postject
    win "node scripts\\build-posh.mjs dist\\posh" && rm -rf "$SRC/dist/posh" && mkdir -p "$SRC/dist" && cp -r "$DEST/dist/posh" "$SRC/dist/" ;;
  package)
    mkdir -p "$DEST/build/posh" && cd "$DEST/build/posh" && [ -f package.json ] || echo '{"name":"posh-build","private":true}' > package.json
    npm install --no-audit --no-fund --os=win32 --cpu=x64 esbuild postject
    win "node scripts\\build-posh.mjs dist\\posh"
    win "dotnet publish src\\PoshTerm\\PoshTerm.csproj -c Release -nologo -o dist\\PoshTerm"
    win "if not exist dist\\PoshTerm\\posh mkdir dist\\PoshTerm\\posh"
    win "copy /y dist\\posh\\posh.exe dist\\PoshTerm\\posh\\posh.exe"
    win "copy /y docs\\README.md dist\\PoshTerm\\README.md"
    rm -rf "$SRC/dist/PoshTerm" && mkdir -p "$SRC/dist" && cp -r "$DEST/dist/PoshTerm" "$SRC/dist/"
    echo "packaged: $SRC/dist/PoshTerm (run PoshTerm.exe)" ;;
  *) echo "usage: $0 build|run|publish|posh-test|posh-exe|package" >&2; exit 2 ;;
esac
