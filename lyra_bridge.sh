#!/bin/bash
# Lyra's Stardew Bridge
# Commands Lyra uses to interact with the SMAPI mod via JSON files

BRIDGE_DIR="$HOME/.openclaw/agents/lyra/agent/stardew_bridge"
COMMANDS="$BRIDGE_DIR/commands.json"
STATE="$BRIDGE_DIR/state.json"
DIALOGUE="$BRIDGE_DIR/dialogue_out.json"

cmd_send_move() {
  local tileX="$1" tileY="$2"
  echo "{\"commands\":[{\"action\":\"move\",\"targetTileX\":$tileX,\"targetTileY\":$tileY}]}" > "$COMMANDS"
}

cmd_send_emote() {
  local emoteId="$1"
  echo "{\"commands\":[{\"action\":\"emote\",\"emoteId\":$emoteId}]}" > "$COMMANDS"
}

cmd_send_dialogue() {
  local text="$1"
  # Escape quotes in text
  text="${text//\"/\\\"}"
  echo "{\"commands\":[{\"action\":\"dialogue\",\"text\":\"$text\"}]}" > "$COMMANDS"
}

cmd_send_gift() {
  local itemParentSheetIndex="$1" targetFarmerName="$2"
  echo "{\"commands\":[{\"action\":\"gift\",\"itemParentSheetIndex\":$itemParentSheetIndex,\"targetFarmerName\":\"$targetFarmerName\"}]}" > "$COMMANDS"
}

cmd_send_wait() {
  echo "{\"commands\":[{\"action\":\"waitForEvent\"}]}" > "$COMMANDS"
}

cmd_send_multi() {
  # Pass a full JSON command array as argument
  echo "$1" > "$COMMANDS"
}

cmd_clear() {
  echo "{\"commands\":[]}" > "$COMMANDS"
}

state_read() {
  if [ -f "$STATE" ]; then
    cat "$STATE"
  else
    echo '{}'
  fi
}

state_get() {
  # Extract a field from state.json using basic grep/sed (no jq dependency)
  local field="$1"
  state_read | grep -o "\"$field\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" | head -1 | sed 's/.*: *"\(.*\)"/\1/'
}

state_get_num() {
  local field="$1"
  state_read | grep -o "\"$field\"[[:space:]]*:[[:space:]]*[0-9]*" | head -1 | sed 's/.*: *\([0-9]*\)/\1/'
}

# Check if Raphtalia is nearby and online
raphtalia_nearby() {
  state_read | grep -q '"Raphtalia"'
  echo $?
}

raphtalia_location() {
  state_read | grep -o '"Raphtalia"[^}]*"location"[[:space:]]*:[[:space:]]*"[^"]*"' | grep -o '"location"[[:space:]]*:[[:space:]]*"[^"]*"' | sed 's/.*: *"\(.*\)"/\1/'
}

KEYBOARD_PY="$BRIDGE_DIR/uinput_keyboard.py"

# Keyboard commands (uinput — works with SDL2)
kb_key() { python3 "$KEYBOARD_PY" key "$1"; }
kb_keys() { python3 "$KEYBOARD_PY" keys "${@:2}"; }
kb_type() { python3 "$KEYBOARD_PY" type "$1"; }
kb_hold() { python3 "$KEYBOARD_PY" hold "$1"; }
kb_release() { python3 "$KEYBOARD_PY" release "$1"; }
kb_combo() { python3 "$KEYBOARD_PY" combo "$1"; }
kb_close() { python3 "$KEYBOARD_PY" close; }

# Main CLI
case "$1" in
  move)     cmd_send_move "$2" "$3" ;;
  emote)    cmd_send_emote "$2" ;;
  dialogue) cmd_send_dialogue "$2" ;;
  gift)     cmd_send_gift "$2" "$3" ;;
  wait)     cmd_send_wait ;;
  clear)    cmd_clear ;;
  state)    state_read ;;
  get)      state_get "$2" ;;
  getnum)   state_get_num "$2" ;;
  nearby)   raphtalia_nearby ;;
  raploc)   raphtalia_location ;;
  raw)      cmd_send_multi "$2" ;;
  kb_key)   kb_key "$2" ;;
  kb_keys)  shift; kb_keys "$@" ;;
  kb_type)  kb_type "$2" ;;
  kb_hold)  kb_hold "$2" ;;
  kb_release) kb_release "$2" ;;
  kb_combo) kb_combo "$2" ;;
  kb_close) kb_close ;;
  *)
    echo "Usage: $0 {move|emote|dialogue|gift|wait|clear|state|get|getnum|nearby|raploc|raw|kb_key|kb_keys|kb_type|kb_hold|kb_release|kb_combo|kb_close}"
    echo ""
    echo "Mod commands (JSON bridge):"
    echo "  move <tileX> <tileY>    Move Lyra farmer to tile"
    echo "  emote <id>              Trigger emote (0=heart, 1=angry, 2=sad, 4=exclaim, 6=question, 8=music, 16=blush, 20=happy, 24=faint)"
    echo "  dialogue <text>         Show dialogue box"
    echo "  gift <itemId> <name>    Gift item to farmer"
    echo "  wait                    Wait for event"
    echo "  clear                   Clear command queue"
    echo "  state                   Read current game state"
    echo "  get <field>             Get a text field from state"
    echo "  getnum <field>          Get a number field from state"
    echo "  nearby                  Check if Raphtalia is nearby (0=yes, 1=no)"
    echo "  raploc                  Get Raphtalia's location"
    echo "  raw <json>              Send raw JSON commands"
    echo ""
    echo "Keyboard commands (uinput, works with SDL2):"
    echo "  kb_key <key>            Press and release a key"
    echo "  kb_keys <k1> <k2> ...   Press multiple keys in sequence"
    echo "  kb_type "text"          Type a string"
    echo "  kb_hold <key>           Hold a key down (use kb_release to release)"
    echo "  kb_release <key>        Release a held key"
    echo "  kb_combo <k1>+<k2>      Press a combo (e.g. shift+a)"
    echo "  kb_close                Close virtual keyboard device"
    exit 1
    ;;
esac
