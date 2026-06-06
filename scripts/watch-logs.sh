#!/usr/bin/env bash
LOG_DIR="${STREAMDECK_PLUGIN_DIR:-$HOME/Library/Application Support/com.elgato.StreamDeck/Plugins/aeroverra.streamdeck.nestcontrol.sdPlugin}/logs"

if [[ ! -d "$LOG_DIR" ]]; then
  echo "Log directory not found: $LOG_DIR"
  exit 1
fi

echo "Tailing Nest Control logs in $LOG_DIR"
echo "Press Ctrl+C to stop."
tail -n 50 -F "$LOG_DIR"/log*.txt
