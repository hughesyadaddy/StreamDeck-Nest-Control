#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PROJECT="$ROOT/Aeroverra.StreamDeck.NestControl"
PLUGIN_DIR="${STREAMDECK_PLUGIN_DIR:-$HOME/Library/Application Support/com.elgato.StreamDeck/Plugins/aeroverra.streamdeck.nestcontrol.sdPlugin}"

ARCH="$(uname -m)"
case "$ARCH" in
  arm64) RID="osx-arm64" ;;
  x86_64) RID="osx-x64" ;;
  *)
    echo "Unsupported architecture: $ARCH"
    exit 1
    ;;
esac

STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

echo "Publishing Debug build ($RID)..."
dotnet publish "$PROJECT" -c Debug -r "$RID" --self-contained true -o "$STAGING"

if [[ ! -d "$PLUGIN_DIR" ]]; then
  echo "Plugin folder not found: $PLUGIN_DIR"
  echo "Install Nest Control once from the marketplace, then run this script again."
  exit 1
fi

echo "Deploying to: $PLUGIN_DIR"
rsync -a --delete \
  --exclude 'logs/' \
  "$STAGING/" "$PLUGIN_DIR/"

echo "Done. Quit Stream Deck completely (menu bar → Quit), reopen it, then open a Nest action's settings."
echo "Logs: $PLUGIN_DIR/logs/"
