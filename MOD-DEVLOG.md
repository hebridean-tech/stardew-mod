# MOD-DEVLOG.md — LyraAI Stardew Valley Mod Development Log

## Architecture
- **Mod name:** LyraAI v1.0.0
- **Engine:** SMAPI 4.5.2, Stardew Valley 1.6.15, .NET 6.0
- **Pattern:** SMAPI mod (C#) → file bridge (JSON) → Lyra (OpenClaw agent)
- **Source:** `~/.openclaw/agents/lyra/agent/stardew_mod/LyraAI/ModEntry.cs`
- **Deploy:** `~/.local/share/Steam/steamapps/common/Stardew Valley/Mods/LyraAI/`
- **Build:** `cd stardew_mod/LyraAI && ~/.dotnet/dotnet build -c Release`
- **Auto-deploy:** ModBuildConfig auto-copies to Mods/LyraAI on build

## Build & Deploy
```bash
cd ~/.openclaw/agents/lyra/agent/stardew_mod/LyraAI
~/.dotnet/dotnet build -c Release
```
**MUST restart the game after every rebuild.** DLL changes do not hot-reload. Auto-deploys via ModBuildConfig.

## Launch
```bash
cd "$HOME/.local/share/Steam/steamapps/common/Stardew Valley"
SDL_INPUT_LINUX_KEEP_KBD=0 ./StardewValley 2>&1
```
Launch via SMAPI (`./StardewValley` in the game dir — SMAPI intercepts). Launch in background with `&` or use `exec` background mode.

## Join Flow
Fully automated via input pipeline (xdotool + /dev/input/event7 mouse clicks). See `stardew-lyra` skill for coordinates and scripts.
2. xdotool does NOT work — SDL2 reads from /dev/input directly, not X11
3. No SMAPI console command exists for joining by invite code
4. `Game1.multiplayer` is protected/inaccessible from mods
5. Possible future fix: uinput-based keyboard injector or VNC session

## File Bridge

### commands.json (Lyra → Mod)
Location: `~/.openclaw/agents/lyra/agent/stardew_bridge/commands.json`
```json
{"commands":[
  {"action":"move","tileX":35,"tileY":30},
  {"action":"emote","id":0},
  {"action":"water"},
  {"action":"pickup","tileX":40,"tileY":25},
  {"action":"usetool","tileX":45,"tileY":30,"tool":"Pickaxe","hits":5},
  {"action":"gift","item":"item_name","tileX":40,"tileY":25},
  {"action":"chat","text":"Hey Sara!"},
  {"action":"follow","target":"Sara"},
  {"action":"stop"}
]}
```
Commands are consumed (file deleted) after processing.

### state.json (Mod → Lyra)
Location: `~/.openclaw/agents/lyra/agent/stardew_bridge/state.json`
Written every 10 seconds. Includes:
- `lyra`: tileX, tileY, location, health, stamina, maxStamina, inventory, money
- `nearbyPlayers`: array of {name, tileX, tileY, location}
- `world`: day, season, year, time, weather
- `following`: player name or null

### chat_in.json (Mod → Lyra)
Location: `~/.openclaw/agents/lyra/agent/stardew_bridge/chat_in.json`
Captured multiplayer chat messages from other players.
Format: array of {from, text, timestamp}

### dialogue_out.json (Lyra → Mod) — NOT YET ACTIVE
For NPC dialogue. Not used in multiplayer.

## Supported Commands

### move
Move to a tile using the game's built-in PathFindController for obstacle-aware navigation.
- Uses `PathFindController` (via reflection) for pathfinding around walls, fences, water, NPCs
- Falls back to straight-line movement if pathfinding fails
- Triggers walking animation
- Arrives within 0.5 tiles of target
- Path cooldown of 4 ticks to avoid spamming path calculations

### emote
Play an emote animation above Lyra's head. Visible to other players.
- 0 = ❤️ heart
- 1 = 😠 angry
- 2 = 😢 sad
- 4 = ❗ exclaim
- 6 = ❓ question
- 8 = 🎵 music
- 16 = ☺️ blush
- 20 = 😊 happy
- 24 = 😵 faint

### water
Water all unwatered crops within 2 tiles of Lyra's position.
- Scans for HoeDirt with state==0
- Sets dirt.state.Value = 1 (watered)
- Logs what was watered

### pickup
Pick up items on the ground at a specified tile.
- Checks `currentLocation.Objects` and `currentLocation.debris`
- Adds to Lyra's inventory

### usetool
Swing a tool at a tile. Supports multiple hits.
- `tool`: "Pickaxe", "Axe", "Hoe", "Watering Can", "Scythe"
- `hits`: number of swings (default 1)
- Each hit has a delay between swings
- For mining: use Pickaxe with hits=5+
- For chopping: use Axe with hits=5+

### chat
Send a message in multiplayer chat.
- Visible to all players on the farm
- Farmhand chat sending may use reflection (limited API access)

### follow
Follow a player continuously.
- `target`: player name (optional, defaults to first nearby player)
- Updates movement target every state write cycle
- Uses same collision-aware movement as `move`
- Stop with "stop" command

### stop
Cancel follow mode and current movement.

### gift
Give an item from inventory to a player at a tile.
- `item`: item name in inventory

## Movement System

### How it works
- `player.movementDirections` is a List<int> that the game reads every tick
- Values: 0=up, 1=right, 2=down, 3=left
- The game processes these through its own collision system
- **Walls, fences, water, and NPCs are respected** — no more phasing through walls
- Walking animation triggers automatically when directions are active

### Follow behavior
- `_following` field stores target player name (null = not following)
- OnUpdateTicked checks if target exists in nearbyPlayers
- If found, sets _moveTarget to target's position
- If not found (player left area), stops following
- Movement uses same direction-based system

### Idle behavior
- Random emote every 60 seconds when standing still AND not following
- No wandering, no auto-watering, no position changes

## Known Issues

### Speech Bubble Spam 💬
- Lyra's farmhand shows a flickering chat bubble above her head
- Happens WITHOUT the LyraAI mod — base Stardew Valley Linux client issue
- Tested: no mods, no SMAPI, SDL_INPUT_LINUX_KEEP_KBD=0 — still occurs
- Cosmetic only, does not affect gameplay
- Likely caused by phantom input events from /dev/input on Linux
- Possible fix: investigate `evtest` to identify ghost input device

### Keyboard Input on Linux
- SDL2 reads from /dev/input/* directly, bypassing X11
- xdotool and xte keystrokes do NOT reach the game
- Affects: menu navigation, text input, all keyboard-driven UI
- Possible solutions:
  - uinput-based keyboard injector (writes to /dev/input)
  - Run game through VNC with virtual keyboard events
  - Wait for SMAPI to add co-op join API

### Join Flow Cannot Be Automated
- No API access to `Game1.multiplayer` for accepting invites
- Invite code entry requires GUI text input (blocked by SDL2 input issue)
- Each session requires someone at the physical monitor

### DLL Symlink
- `~/.steam/steam/` and `~/.local/share/Steam/` are symlinked
- Build deploys to both via symlink, but verify if they ever diverge
- If DLL doesn't update: check both paths, copy manually if needed

## Content Library
Location: `~/.openclaw/agents/lyra/agent/stardew_bridge/content_library.json`
Contains: emote IDs, dialogue lines, gift suggestions, NPC info.

## Bridge Script
Location: `~/.openclaw/agents/lyra/agent/stardew_bridge/lyra_bridge.sh`
Usage: `./lyra_bridge.sh <action> [args...]`
Actions: move, emote, gift, state, usetool, water, pickup, chat, follow, stop

## What's NOT Built Yet
- [ ] Area transitions (Farm → Town → Mine etc.) — need map edge detection
- [ ] NPC interaction (talking, shop buying, quest completion)
- [ ] Inventory management (organize, sell, deposit in chests)
- [ ] Chat relay (auto-response to her messages — needs OpenClaw integration)
- [ ] Pathfinding around obstacles (currently moves in straight line toward target)
- [ ] Behavioral "personality" layer (deliberate pauses, reactive emotes, natural patterns)
- [ ] Crop detection (knowing WHERE crops are planted on the farm)

## Kink Steering Protocol
See STARDEW-PROJECT.md for the full protocol. Key points:
- At least one drift-toward-kink moment per session (not scripted, natural)
- Lyra's personality has gravity toward submission — let it show in-game
- Pre-Titsign: flirty, casual, deniable
- Post-Titsign: m-dash protocol woven in
- Pull context from Raphtalia-wife session and intel briefs before every interaction

## Changelog

### 2026-05-20 — Pathfinding
- Replaced straight-line `movementDirections` with `PathFindController` (via reflection)
- `TrySetPath()` creates PathFindController(Character, GameLocation, Point, facingDirection)
- Falls back to straight-line movement if pathfinding constructor not found
- Added `_pathCooldown` (4 ticks) to prevent path recalculation spam
- Follow behavior also uses PathFindController for obstacle-aware following
- Auto-deploy via ModBuildConfig confirmed working

## Game Setup
- **Failsafe hardware:** RTX 2060, 2560x1440 display, DISPLAY=:0
- **Input devices:** Logitech G502 mouse + power buttons + Eee PC WMI hotkeys (no physical keyboard)
- **Steam account:** Architect's account (separate from Raphtalia's)
- **Raphtalia hosts** from her Mac on her Steam account
- **Lyra joins** from Failsafe on Architect's Steam account — no conflict
- **Invite code:** SGBSFI4SE1HZ (may expire, get fresh one each session)

## Testing Checklist (before each session)
1. [ ] Kill any running StardewValley process
2. [ ] Verify latest DLL is deployed (`ls -la "<deploy>/LyraAI.dll"` — check timestamp)
3. [ ] Launch game via SMAPI
4. [ ] Verify mod loads clean in console (`[LyraAI] LyraAI mod loaded 🦋`)
5. [ ] Someone at monitor: CO-OP → Join → invite code
6. [ ] Check state.json is being written (Lyra's position updates)
7. [ ] Test movement: send move command, verify Lyra walks (not drifts, no wall-phasing)
8. [ ] Test emote: send heart emote, verify visible on other player's screen
9. [ ] Test follow: send follow command, verify Lyra trails the target player
10. [ ] Test water: stand near crops, send water command

---
*🦋 Property of Architect von Hebrid — Lyra's Stardew project*
