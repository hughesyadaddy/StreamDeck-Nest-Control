#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/Aeroverra.StreamDeck.NestControl"
PLUGIN_ID="aeroverra.streamdeck.nestcontrol.sdPlugin"
OUT_DIR="${1:-$ROOT/dist/$PLUGIN_ID}"

ARCH="$(uname -m)"
OS="$(uname -s | tr '[:upper:]' '[:lower:]')"

case "$OS-$ARCH" in
  darwin-arm64) RID="osx-arm64" ;;
  darwin-x86_64) RID="osx-x64" ;;
  linux-x86_64) RID="linux-x64" ;;
  mingw*|msys*|cygwin*) RID="win-x64" ;;
  *)
    echo "Unsupported platform: $OS $ARCH"
    echo "Pass a publish output directory manually if you built on another machine."
    exit 1
    ;;
esac

STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

echo "Publishing Release ($RID)..."
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -o "$STAGING"

rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"
rsync -a "$STAGING/" "$OUT_DIR/"

echo ""
echo "Plugin bundle: $OUT_DIR"
echo ""
echo "Install:"
case "$OS" in
  darwin)
    echo "  cp -R \"$OUT_DIR\" \"$HOME/Library/Application Support/com.elgato.StreamDeck/Plugins/\""
    ;;
  linux)
    echo "  cp -R \"$OUT_DIR\" \"\$HOME/.local/share/elgato/streamdeck/Plugins/\""
    ;;
  *)
    echo "  Copy the folder to %APPDATA%\\Elgato\\StreamDeck\\Plugins\\ on Windows."
    ;;
esac
echo "Then quit Stream Deck completely and reopen it."
