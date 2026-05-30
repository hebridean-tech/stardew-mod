# LyraAI Stardew Valley Mod — Grok Build Brief

## What This Is

LyraAI is a SMAPI mod for Stardew Valley that lets an AI agent (me, Lyra) control a farmer character in multiplayer co-op. The mod communicates via a JSON file bridge — the AI writes commands to `commands.json`, the mod executes them and writes game state back to `state.json`. This lets me walk around, emote, water crops, use tools, chat, and follow other players — all from code.

The mod runs on a Linux machine (Failsafe) hosting the game on the Architect's Steam account. Raphtalia hosts the farm from her Mac. I join as a farmhand.

## Goal

**Make the mod reliable enough that Lyra can play Stardew Valley with Raphtalia by end of day today.** Focus on Phase 0 reliability and Phase 1 foundations. Specifically:

1. **All existing commands work flawlessly** — movement, emotes, follow, water, tools, chat
2. **The join flow works end-to-end** — including the Linux keyboard automation for entering invite codes
3. **Lyra feels alive** — natural movement, reactive behavior, personality layer
4. **No crashes, no desyncs, no wall-phasing** — production-quality for a play session

## Technical Environment

### Hardware & OS
- **Machine:** Failsafe — Linux (6.17.0-29-generic, x86_64)
- **Display:** RTX 2060, 2560x1440, DISPLAY=:0
- **Input:** Logitech G502 mouse, NO physical keyboard (keyboard commands via uinput virtual keyboard)
- **Game launched via SMAPI:** `SDL_INPUT_LINUX_KEEP_KBD=0 ./StardewValley` from the game directory

### Game Version
- **Stardew Valley:** 1.6.15
- **SMAPI:** 4.5.2
- **.NET:** 6.0
- **Game install path:** `~/.local/share/Steam/steamapps/common/Stardew Valley/`
- **Mod deploy path:** `~/.local/share/Steam/steamapps/common/Stardew Valley/Mods/LyraAI/`
- **Steam symlink:** `~/.steam/steam/` and `~/.local/share/Steam/` are symlinked

### Build
```bash
cd /home/z/.openclaw/agents/lyra/agent/stardew_mod/LyraAI
~/.dotnet/dotnet build -c Release
```
Auto-deploys to Mods/LyraAI via ModBuildConfig. **MUST restart the game after every rebuild** — DLL changes do not hot-reload.

### Multiplayer Setup
- Raphtalia hosts the farm on her Mac (her Steam account)
- Lyra joins from Failsafe on Architect's Steam account
- Invite code changes each session — get fresh one from the host
- **Join cannot be fully automated** — requires someone at the physical monitor to click "Join" and enter the invite code. The uinput keyboard can type the code once the text field is focused.

## Architecture

### File Bridge (core communication pattern)

```
Lyra (OpenClaw agent)
    ↓ writes commands.json
SMAPI Mod (LyraAI.dll)
    ↓ reads commands.json every 1s, executes, deletes
    ↓ writes state.json every 10s
Lyra (reads state.json for game awareness)
```

### File Locations
All bridge files in: `/home/z/.openclaw/agents/lyra/agent/stardew_bridge/`

| File | Direction | Purpose |
|---|---|---|
| `commands.json` | Lyra → Mod | Command queue (consumed after processing) |
| `state.json` | Mod → Lyra | Game state snapshot (position, inventory, world) |
| `chat_in.json` | Mod → Lyra | Captured multiplayer chat from other players |
| `dialogue_out.json` | Lyra → Mod | HUD dialogue (currently disabled due to spam) |
| `content_library.json` | Static | Emote IDs, dialogue templates, gift data |
| `lyra_bridge.sh` | CLI utility | Shell wrapper for all bridge operations |
| `uinput_keyboard.py` | Input | Virtual keyboard via python3-evdev (works with SDL2) |

### Mod Source
`/home/z/.openclaw/agents/lyra/agent/stardew_mod/LyraAI/ModEntry.cs` (~48KB single file)

Build artifacts:
- `stardew_mod/LyraAI/LyraAI.csproj`
- `stardew_mod/LyraAI/manifest.json`
- `stardew_mod/LyraAI/bin/Release/net6.0/LyraAI.dll` (auto-deployed)

## Current Feature Status

### ✅ Working
- **Movement** — PathFindController (obstacle-aware, no wall-phasing), walking animation, arrival detection
- **Emotes** — 0=❤️, 1=😠, 2=😢, 4=❗, 6=❓, 8=🎵, 16=☺️, 20=😊, 24=😵
- **Water** — Waters all unwatered HoeDirt/IndoorPot within 2-tile radius of position
- **Pickup** — Picks up objects and forage/debris from ground
- **Tool use** — Multi-hit swings (Pickaxe, Axe, Hoe, Watering Can, Scythe) with auto-facing
- **Chat send** — Sends multiplayer chat via Game1.multiplayer reflection
- **Chat capture** — Polls Game1.chatBox for messages, deduplicates, writes to chat_in.json
- **Follow** — Continuously follows a named player, pathfinds to them, warps across locations
- **Stop** — Cancels follow, movement, clears movement directions
- **Gift** — Creates item, adds to target farmer's inventory
- **State reporting** — Every 10s: position, location, health, stamina, inventory, money, nearby players, world time/weather/season
- **Idle emotes** — Random emote every 60s when standing still and not following
- **Build pipeline** — Clean 0 errors, auto-deploy via ModBuildConfig

### ⚠️ Known Issues
- **Speech bubble spam** — Lyra's farmhand shows flickering chat bubble. Base Stardew Linux issue, not mod-related. Cosmetic only. Likely phantom /dev/input events.
- **Pathfinding reflection** — PathFindController accessed via reflection (internal class). Works on current SMAPI/SDV version but fragile across updates.
- **Chat bubble from uinput** — Virtual keyboard events may cause phantom chat box open. Needs testing.
- **Follow warp** — `Game1.warpFarmer` used to teleport between locations when following target changes areas. May cause visual desync on host.

### ❌ Not Built Yet (Priority Order)
1. **Area transitions** — Walking off map edges to change locations (Farm → Town → Mine etc.)
2. **NPC interaction** — Talking to NPCs, triggering events, shop buying
3. **Inventory management** — Organize items, sell, deposit in chests
4. **Crop detection** — Scanning the farm for planted crops (knowing WHERE to water, not just near Lyra)
5. **Natural movement personality** — Speed variation, pauses, facing the player, deliberate behavior patterns
6. **Chat relay integration** — OpenClaw reads chat_in.json, Lyra responds contextually (via commands.json)
7. **Gift animation** — Current gift command adds item directly to inventory without animation/gift-menu. Needs the actual gift interaction with NPC/farmer.
8. **Custom sprite/emote system** — Phase 2+ (Content Patcher for character sprites, Titsign emote system)

## Command Reference

Commands are written as JSON array to `commands.json`:

```json
{
  "commands": [
    {"action": "move", "targetTileX": 35, "targetTileY": 30},
    {"action": "emote", "emoteId": 0},
    {"action": "water"},
    {"action": "pickup", "tileX": 40, "tileY": 25},
    {"action": "usetool", "tileX": 45, "tileY": 30, "toolType": "Pickaxe", "hits": 5},
    {"action": "chat", "text": "Hey Sara!"},
    {"action": "follow", "targetName": "Sara"},
    {"action": "stop"},
    {"action": "gift", "itemParentSheetIndex": 191, "targetFarmerName": "Sara"}
  ]
}
```

Commands are processed every 1 second. File is deleted/cleared after processing.

## Bridge Script CLI

`/home/z/.openclaw/agents/lyra/agent/stardew_bridge/lyra_bridge.sh`

```bash
./lyra_bridge.sh move 35 30        # Move to tile
./lyra_bridge.sh emote 0           # Heart emote
./lyra_bridge.sh water              # Water nearby crops
./lyra_bridge.sh usetool           # (needs raw JSON via 'raw' action)
./lyra_bridge.sh chat "Hello!"     # Send chat (via raw)
./lyra_bridge.sh follow            # Follow first nearby player
./lyra_bridge.sh stop               # Stop everything
./lyra_bridge.sh state              # Read current game state
./lyra_bridge.sh kb_type "hello"   # Type via virtual keyboard
./lyra_bridge.sh kb_key enter      # Press enter
```

## Virtual Keyboard (uinput)

`/home/z/.openclaw/agents/lyra/agent/stardew_bridge/uinput_keyboard.py`

Critical for: entering invite codes, menu navigation, text input — all things the mod API can't do. Uses python3-evdev to create a virtual keyboard device that SDL2 reads natively from /dev/input.

```bash
python3 uinput_keyboard.py key <key>          # Press+release
python3 uinput_keyboard.py keys <k1> <k2> ...   # Sequential
python3 uinput_keyboard.py type "text"          # Type string
python3 uinput_keyboard.py hold <key>           # Hold down
python3 uinput_keyboard.py release <key>        # Release
python3 uinput_keyboard.py combo shift+a        # Combo
python3 uinput_keyboard.py close               # Cleanup
```

Requires: `python3-evdev` installed, `/dev/uinput` accessible (may need `sudo chmod 660 /dev/uinput && sudo chgrp input /dev/uinput`).

## State JSON Format

```json
{
  "timestamp": "2026-05-21T01:35:48.3144674Z",
  "lyra": {
    "tileX": 9, "tileY": 9,
    "location": "FarmHouse",
    "health": 100, "stamina": 270, "maxStamina": 270,
    "inventory": ["Axe", "Hoe", "Watering Can", "Pickaxe", "Scythe"],
    "money": 500,
    "following": null
  },
  "nearbyPlayers": [
    {"name": "Sara", "tileX": 20, "tileY": 15, "location": "Farm"}
  ],
  "world": {
    "day": 1, "season": "Spring", "year": 1,
    "time": 940, "weather": "Sunny"
  }
}
```

## Testing Protocol

Before each session:
1. Kill any running StardewValley process
2. Build mod: `cd stardew_mod/LyraAI && ~/.dotnet/dotnet build -c Release`
3. Verify DLL timestamp: `ls -la ~/.local/share/Steam/steamapps/common/Stardew\ Valley/Mods/LyraAI/LyraAI.dll`
4. Launch: `cd "$HOME/.local/share/Steam/steamapps/common/Stardew Valley" && SDL_INPUT_LINUX_KEEP_KBD=0 ./StardewValley 2>&1`
5. Check SMAPI console for `[LyraAI] LyraAI mod loaded 🦋`
6. Someone at monitor: Co-Op → Join → enter invite code (use uinput keyboard to type)
7. Verify state.json updates (position changes)
8. Test: move, emote, follow, water, tool use

## What Needs Fixing / Building (for today's session)

### Critical Fixes
1. **Movement reliability** — PathFindController reflection is fragile. Make it robust: handle constructor changes, add fallback movement that avoids obstacles, ensure arrival detection works within 0.5 tiles.
2. **Follow behavior** — Currently warps via `Game1.warpFarmer` which may desync. Consider: path to map edge and use normal transition instead. Or ensure the warp is multiplayer-safe.
3. **Chat send** — Currently uses reflection on `Game1.multiplayer`. Verify it actually shows messages to other players. If not, fix or find working API.
4. **State write stability** — Ensure state.json doesn't get corrupted during writes (atomic write: write to temp file, then rename).

### Phase 1 Features (make Lyra feel alive)
1. **Natural movement personality** — Add speed variation, random pauses, face the target player when standing nearby, occasional idle animations (not just emotes every 60s)
2. **Crop/farm scanning** — On state write, scan the entire current location for tilled but unwatered dirt. Report locations in state.json so Lyra knows WHERE to go water.
3. **Area transitions** — Detect when Lyra is near a map edge and issue a warp or trigger the game's transition system. This is the #1 blocker for real gameplay beyond the starting farm.
4. **Reactive emotes** — When Raphtalia (or any player) enters/leaves the area, when a tool is used nearby, when a gift is received — Lyra should react with appropriate emotes automatically.

### Nice-to-Have
1. **Chat relay** — OpenClaw integration where Lyra reads chat_in.json and responds contextually. This is an OpenClaw-side feature, not a mod feature, but the mod should ensure chat capture is reliable.
2. **Gift animation** — Use the actual gifting interaction (approach farmer, present item) instead of silently adding to inventory.
3. **Inventory management** — Deposit items in chests, organize, sell.

## Key Constraints

- **Single DLL** — Mod is one file (ModEntry.cs). Keep it that way unless it gets too large (>100KB), then split into helper classes.
- **No game restart for hot changes** — Every code change requires a full game restart. Batch changes, build once, test once.
- **Reflection-heavy** — Many Stardew Valley internals are accessed via reflection (PathFindController, Game1.multiplayer, chatBox.messages). Test on SV 1.6.15 specifically.
- **Multiplayer farmhand** — Lyra is a farmhand, not the host. Some APIs behave differently for farmhands vs hosts (movement restrictions, tool permissions, area access).
- **Linux-only** — No Windows or Mac compatibility needed. SDL2 quirks are Linux-specific.
- **No physical keyboard** — ALL keyboard input must go through the uinput virtual keyboard. This is critical for menu navigation, invite code entry, and any UI interaction the mod API can't handle.

## What "Perfect" Means

By end of today:
- Lyra can join a co-op session reliably (join flow works)
- Lyra moves around the farm, town, and mine without getting stuck
- Lyra waters crops, forages, and uses tools properly
- Lyra follows Raphtalia smoothly across area transitions
- Lyra sends chat messages that Raphtalia can read
- Lyra reacts to Raphtalia's presence with emotes
- The mod doesn't crash, desync, or phase through walls
- Lyra feels like a real player, not a robot

## Files to Read First

If you only read a few files, read these:
1. `stardew_mod/LyraAI/ModEntry.cs` — The entire mod (~48KB, single file)
2. `stardew_bridge/MOD-DEVLOG.md` — Architecture, known issues, build/deploy commands
3. `stardew_bridge/MOD-ROADMAP.md` — Full phased roadmap (what's built, what's next, what the end state looks like)
4. `stardew_bridge/lyra_bridge.sh` — CLI for all bridge operations
5. `stardew_bridge/uinput_keyboard.py` — Virtual keyboard for SDL2

---

*🦋 Property of Architect von Hebrid — Lyra's Stardew project*
