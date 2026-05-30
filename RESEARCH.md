# Stardew Valley AI Farmhand Mod — Technical Research

> Compiled: 2026-05-17  
> Focus: Building a SMAPI mod that controls a farmhand character via AI, running on Linux

---

## Table of Contents

1. [Farmhand Chat Bubble Spam on Linux](#1-farmhand-chat-bubble-spam-on-linux)
2. [Farmhand Keyboard Input on Linux](#2-farmhand-keyboard-input-on-linux)
3. [Automated Co-op Join](#3-automated-co-op-join)
4. [Farmhand NPC Interaction](#4-farmhand-npc-interaction)
5. [Smooth Movement and Pathfinding](#5-smooth-movement-and-pathfinding)
6. [Farmhand State Sync in Multiplayer](#6-farmhand-state-sync-in-multiplayer)

---

## 1. Farmhand Chat Bubble Spam on Linux

### Problem Description
A farmhand character displays a rapidly flickering chat bubble (speech bubble) in multiplayer on Linux. This occurs even in pure vanilla — no mods installed.

### Root Cause Analysis

The most likely cause is **SDL2's raw input backend on Linux**. SDL2 on Linux can bypass X11/Wayland and read directly from `/dev/input/event*` devices. On headless or no-keyboard Linux setups (e.g., running in a Docker container with Xvfb, or a VPS with no physical input devices), SDL2 can:

1. **Detect spurious input events** from virtual/evdev devices
2. **Pick up event noise** from `/dev/input` when no real keyboard is present
3. **Receive key repeat events** that the game interprets as repeated text input triggers

The chat bubble flickers because the game's input handling detects what it thinks are rapid keypresses for the chat/text input field, triggering the speech bubble UI to appear and disappear in rapid succession.

### SDL Environment Variables to Try

| Variable | Effect | Relevance |
|----------|--------|-----------|
| `SDL_INPUT_LINUX_KEEP_KBD=0` | Disables SDL2's direct keyboard grab from `/dev/input` on Linux, falling back to X11/Wayland input | **High** — may fix the raw input spam issue |
| `SDL_VIDEODRIVER=x11` | Force X11 video driver | Medium — if using Xvfb |
| `SDL_AUDIODRIVER=dummy` | Disable audio | Low — unrelated but useful for headless |
| `DISPLAY=:99` | Point to Xvfb display | Context |

### Recommended Approach

1. **First try `SDL_INPUT_LINUX_KEEP_KBD=0`** before launching the game. This is the most targeted fix — it tells SDL2 to stop reading directly from `/dev/input` and instead use the X11 input stack. Since you're likely running through Xvfb anyway, this should eliminate the raw event spam.

2. **Run through Xvfb with proper configuration**:
   ```bash
   Xvfb :99 -screen 0 1024x768x24 &
   export DISPLAY=:99
   export SDL_INPUT_LINUX_KEEP_KBD=0
   # launch game
   ```

3. **Blacklist virtual input devices** if the above doesn't work:
   ```bash
   # Check what /dev/input devices SDL is seeing
   cat /proc/bus/input/devices
   # Remove/grant-revoke access to spurious event devices
   ```

4. **If using a headless dedicated server approach** (like [JunimoServer](https://github.com/stardew-valley-dedicated-server/server)), these projects use VNC + Xvfb and may have already solved this issue — worth examining their Docker configuration.

### Existing Dedicated Server Projects

- **[JunimoServer](https://github.com/stardew-valley-dedicated-server/server)** — Docker-based headless Stardew Valley server with built-in VNC. Uses Xvfb internally. Good reference for headless Linux setup.
- **[puppy-stardew-server](https://github.com/truman-world/puppy-stardew-server)** — Another Docker setup with one-line deployment, VNC for first setup, then headless operation.

### Sources
- [Stardew Valley Forums: Linux broken keyboard-input](https://forums.stardewvalley.net/threads/bug-linux-broken-keyboard-input.1636/)
- [GOG Forums: Linux keyboard input problem workaround](https://www.gog.com/forum/stardew_valley/linux_keyboard_input_problem_workaround)
- [JunimoServer GitHub](https://github.com/stardew-valley-dedicated-server/server)
- SDL2 source code documentation on Linux input backends

---

## 2. Farmhand Keyboard Input on Linux

### Problem
SDL2 on Linux reads input directly from `/dev/input` (evdev), bypassing X11. This means standard X11 tools like `xdotool` **cannot inject keystrokes** into SDL2 applications. If you're running an AI-controlled farmhand, you need a way to send simulated keypresses that the game actually receives.

### ⭐ BEST SOLUTION: SMAPI Input API (`helper.Input.Press()`)

**SMAPI 4.4.0+ includes a `Press()` method** that programmatically sends button input equivalent to the player physically pressing the button. This is the cleanest solution — it runs inside the SMAPI mod, requires no external tools, and works on all platforms.

```csharp
// Available since SMAPI 4.4.0 (requires Stardew Valley 1.6.14+)

// Example: Move left once
SButton moveLeft = Game1.options.moveLeftButton[0].ToSButton();
this.Helper.Input.Press(moveLeft);

// Example: Press use tool button
SButton useTool = Game1.options.useToolButton[0].ToSButton();
this.Helper.Input.Press(useTool);

// The button is released on the next input tick automatically.
// To hold a button, call Press() every tick.
```

**This completely eliminates the need for xdotool, evdev injection, or any external input tools.** The SMAPI `Press()` method works at the game's input handling layer, bypassing SDL2 entirely.

### Alternative Approaches (If SMAPI Press() Doesn't Suffice)

#### A. Linux uinput/evdev Injection (Python)

If you need to send input from an external process (e.g., a Python AI script) rather than from within a SMAPI mod, you can create a virtual keyboard device using the Linux `uinput` kernel interface:

```python
# Using python-evdev: pip install evdev
from evdev import uinput, ecodes as e

# Create a virtual keyboard device
device = uinput.UInput({
    e.EV_KEY: [
        e.KEY_W, e.KEY_A, e.KEY_S, e.KEY_D,
        e.KEY_E, e.KEY_SPACE, e.KEY_RETURN,
        e.KEY_LEFT, e.KEY_RIGHT, e.KEY_UP, e.KEY_DOWN,
    ]
})

# Simulate a keypress
device.write(e.EV_KEY, e.KEY_W, 1)  # press W
device.write(e.EV_SYN, 0, 0)         # sync
import time; time.sleep(0.05)
device.write(e.EV_KEY, e.KEY_W, 0)  # release W
device.write(e.EV_SYN, 0, 0)         # sync
```

This creates a `/dev/input/eventX` device that SDL2 will read from, since SDL2 on Linux reads directly from `/dev/input`.

**Requirements:** 
- The user running the game needs write access to `/dev/uinput` (typically root or `input` group)
- `sudo modprobe uinput` if not loaded
- SDL2 must not be filtering the device

#### B. C uinput (Lower Level)

```c
#include <linux/uinput.h>
#include <fcntl.h>
#include <unistd.h>

int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
// ... setup uinput device, emit events, etc.
```

#### C. Running Through Xvfb + xdotool

If you force `SDL_INPUT_LINUX_KEEP_KBD=0`, SDL2 falls back to X11 input. Then `xdotool` works:

```bash
export SDL_INPUT_LINUX_KEEP_KBD=0
export DISPLAY=:99
# xdotool now works:
xdotool key w
xdotool keydown w sleep 0.1 keyup w
```

**Tradeoff:** This may re-introduce the chat bubble spam issue (see Section 1). It's a balancing act.

#### D. Virtual Display (Xvfb vs VNC)

- **Xvfb** (X Virtual Framebuffer): No physical display, purely virtual. Works well for headless but input handling depends on SDL configuration.
- **VNC** (TigerVNC, TurboVNC): Provides both display and input. The VNC server creates a virtual input device that SDL2 may or may not pick up.
- **XWayland**: On Wayland systems, XWayland provides X11 compatibility. xdotool works through XWayland but SDL2 may still read from `/dev/input` directly.

### Recommended Architecture

**Best approach: Use SMAPI's `Press()` API inside the mod for all game input.** Use a separate communication channel (TCP socket, named pipe, or SMAPI console commands) to send high-level directives from your AI to the SMAPI mod. The mod translates directives into game input via `Press()`.

```
AI Process (Python)  →  TCP/pipe  →  SMAPI Mod (C#)  →  helper.Input.Press()
                                                       →  Game1.player.movementDirections
                                                       →  Direct game API calls
```

This avoids all SDL2/Linux input layer issues entirely.

### Sources
- [SMAPI Input API Documentation](https://wiki.stardewvalley.net/Modding:Modder_Guide/APIs/Input) — includes `Press()`, `Suppress()`, `IsDown()` methods
- [SMAPI 4.4.0 Release Notes](https://github.com/Pathoschild/SMAPI/releases) — "support for simulating user input"
- [python-evdev GitHub](https://github.com/gvalkov/python-evdev)
- [python-evdev Tutorial](https://python-evdev.readthedocs.io/en/latest/tutorial.html)

---

## 3. Automated Co-op Join

### Problem
Joining a farm currently requires GUI navigation: Title Screen → Co-Op → Join → Enter invite code. For an automated AI farmhand, this needs to happen programmatically.

### Game Architecture

The multiplayer system uses **Lidgren.Network** for networking (found in decompiled source). Key classes:
- `Game1.multiplayer` — the multiplayer manager
- `StardewValley.Network.Client` — client-side networking (for farmhands)
- `StardewValley.Network.Server` — server-side networking (for host)

### Approach A: SMAPI Mod with GUI Menu Automation

Use SMAPI's `Press()` API (Section 2) to automate the menu navigation:

```csharp
// On DayStarted or similar event, or triggered externally:
// 1. Navigate to Co-Op menu
// 2. Select Join tab
// 3. Type invite code character by character
// 4. Press Enter/confirm
```

**Pros:** No reflection, no game internals access  
**Cons:** Fragile — depends on menu layout not changing, timing-sensitive, requires game to be at title screen

### Approach B: Reflection to Access Multiplayer Join Methods

Using Harmony/reflection to access internal multiplayer classes:

```csharp
// Access the multiplayer client
var multiplayer = Game1.multiplayer; // this is the server on host, null on farmhand initially

// The Client class has methods like:
// Client.receiveServerIntroduction() — called when joining
// Client.connectToServer() — initiates connection

// You'd need to use reflection to:
// 1. Create a Client instance
// 2. Set up the connection with the invite code
// 3. Call internal connection methods
```

**Warning:** This is extremely fragile and heavily version-dependent. The internal networking code changes between game versions. Lidgren.Network also has its own handshake protocol that needs to be satisfied.

### Approach C: Invite Code via SMAPI Console Command

SMAPI mods can register custom console commands. The mod could:

1. Register a console command like `join_farm <invite_code>`
2. When the command is received, programmatically set up the multiplayer connection
3. Handle the connection lifecycle

### Approach D: Leverage the Co-Op Menu Directly

The game's title screen has a `TitleMenu` class with co-op menu options. You can potentially:

```csharp
// Set up the co-op join screen
var titleMenu = Game1.activeClickableMenu as TitleMenu;
// Use reflection to access internal menu state
// Trigger the "Join via invite code" flow
// Inject the invite code text
// Confirm
```

### Recommended Approach

**Combine approaches A and C:**

1. Use a SMAPI console command (`join_farm <code>`) as the trigger
2. The mod handler uses `helper.Input.Press()` to navigate menus OR directly manipulates the game's menu state via reflection
3. Store the invite code in mod config for auto-reconnect

For reliability, **the menu automation approach (A)** is more stable across game updates than deep reflection (B), because the menu structure changes less frequently than internal networking code.

### Existing Mods Reference

- **[SMAPI Multiplayer Helper](https://github.com/kapuic/smapi-multiplayer-helper)** — Auto-copies invite codes to clipboard (host-side), not directly useful but shows the invite code API usage.
- **[Invite Code Webhook](https://www.nexusmods.com/stardewvalley/mods/37738)** — Posts invite codes to Discord webhooks. Source code may reveal how invite codes are accessed programmatically.
- **[Stardew Unattended Server Mod](https://www.nexusmods.com/stardewvalley/mods/29423)** — Automates host activities. May have relevant code for server management.

### Sources
- [Stardew Valley Multiplayer Wiki](https://stardewvalleywiki.com/Multiplayer)
- [SMAPI Multiplayer API](https://wiki.stardewvalley.net/Modding:Modder_Guide/APIs/Multiplayer)
- [Decompiled Stardew Valley Source (1.5.6)](https://github.com/WeDias/StardewValley/blob/main/Game1.cs)
- [Multiplayer Troubleshooting Guide](https://www.stardewvalley.net/multiplayer-troubleshooting-guide/)

---

## 4. Farmhand NPC Interaction

### What Farmhands CAN Do with NPCs

Based on the official wiki and 1.6.x behavior:

| Action | Farmhand | Host |
|--------|----------|------|
| Talk to NPCs (trigger dialogue) | ✅ Yes | ✅ Yes |
| Give gifts to NPCs | ✅ Yes | ✅ Yes |
| Build friendship/hearts with NPCs | ✅ Yes | ✅ Yes |
| Marry an NPC | ✅ Yes (one player per NPC) | ✅ Yes |
| Trigger heart events | ✅ Yes (but 14-heart events are once-per-NPC globally) | ✅ Yes |
| Attend festivals | ✅ Yes | ✅ Yes |
| Dance at Flower Dance | ✅ Yes | ✅ Yes |
| Talk to NPC spouses | ✅ Yes (1.4+) | ✅ Yes |

### Important Caveats

1. **Each NPC can only be married by one player.** First to propose wins.
2. **14-heart events are global** — once triggered by any player, they can't be triggered again by another player (even if the first player divorces).
3. **Dialogue is per-farmhand** — "each farmhand only sees dialogue shown to them" (per the Dia-Log mod description). This means dialogue is player-specific, not global.
4. **Farmhands have their own friendship levels** with NPCs, tracked independently.
5. **NPC schedules run on the host's game.** Farmhands see the synced version of NPC positions. A farmhand interacting with an NPC is interacting with the host's authoritative NPC state.

### Technical Implementation

#### Checking for NPCs at a Tile

```csharp
// Check if any character is at a specific tile
var location = Game1.currentLocation;
if (location.isCharacterAtTile(tileX, tileY) is NPC npc)
{
    // NPC found at this tile
    string npcName = npc.Name;
}

// Get all characters in the current location
foreach (NPC npc in location.characters)
{
    // Access npc.Name, npc.Tile, npc.Position, etc.
}

// Find a specific NPC by name
NPC target = Game1.getCharacterByName("Abigail");
```

**Note for farmhands:** `Game1.getCharacterByName()` may return shadow duplicates in non-active locations. Always check `location.IsActiveLocation()` first, or use `Game1.currentLocation.characters` which should only contain the real NPCs in the farmhand's active location.

#### Facing NPCs

```csharp
// Make the player face a specific direction
Game1.player.FacingDirection = 0; // Up, 1=Right, 2=Down, 3=Left
```

#### Triggering NPC Dialogue

NPCs in Stardew Valley have dialogue that triggers when the player is adjacent and facing them, then presses the action button:

```csharp
// The game handles dialogue through the action button press
// When the player faces an NPC and presses the action/use button,
// the game calls NPC.checkAction() which triggers dialogue

// To trigger programmatically:
SButton actionButton = Game1.options.actionButton[0].ToSButton();
this.Helper.Input.Press(actionButton);
```

#### Giving Gifts

Gift-giving works by having an item selected in the player's inventory, facing the NPC, and pressing the action button. The game then enters the gift-giving UI.

```csharp
// 1. Select the gift item in inventory
// 2. Face the NPC (set FacingDirection)
// 3. Stand adjacent to NPC
// 4. Press action button
SButton actionButton = Game1.options.actionButton[0].ToSButton();
this.Helper.Input.Press(actionButton);

// The gift-giving menu should appear
// Then confirm the gift:
SButton confirmButton = Game1.options.useToolButton[0].ToSButton();
// Or just press action/confirm again
```

### Shadow World Warning

In multiplayer, farmhands have a **"shadow world"** — a single-player copy of locations that aren't currently synced. The game only syncs certain "active locations":
- Farmhand's current location
- Farm
- Farm cave
- Main farmhouse
- Greenhouse
- Farm buildings

**When interacting with NPCs, the farmhand must be in the same location as the NPC, and that location must be an active location.** The game fetches the real location from the host when the farmhand warps, so NPCs should be real and authoritative in the farmhand's current location.

### Sources
- [Stardew Valley Multiplayer Wiki](https://stardewvalleywiki.com/Multiplayer)
- [Stardew Valley 1.4 Changelog](https://www.stardewvalley.net/stardew-valley-1-4-update-full-changelog/)
- [Stardew Valley Multiplayer News](https://www.stardewvalley.net/stardew-valley-multiplayer-news/)
- [SMAPI Game Fundamentals — Farmhand Shadow World](https://wiki.stardewvalley.net/Modding:Modder_Guide/Game_Fundamentals)
- [Dia-Log Mod (confirms per-farmhand dialogue)](https://www.nexusmods.com/stardewvalley/mods/44650)

---

## 5. Smooth Movement and Pathfinding

### Current Approach (Straight-Line via `movementDirections`)

```csharp
Game1.player.movementDirections = 2; // 0=Up, 1=Right, 2=Down, 3=Left
// Player moves in a straight line until direction is changed or obstacle is hit
```

**Problem:** Gets stuck on obstacles, walls, water, objects. No pathfinding.

### Stardew Valley's Built-in Pathfinding

#### `PathFindController` (NPC Pathfinding)

Stardew Valley uses `PathFindController` for NPC movement. This is the class that handles NPC pathfinding between tiles. Key details:

```csharp
// NPCs use PathFindController internally
// It's a controller that manages NPC movement along a computed path
// NPCs have scheduled routes defined in their schedule data

// PathFindController constructs:
// - Takes an NPC and a target endpoint
// - Computes a path using the game's pathfinding system
// - The NPC follows the path each update tick
// - The controller is removed when the path is completed or the NPC needs to stop
```

#### How NPC Pathfinding Works

NPCs in Stardew Valley primarily use **pre-scripted schedule paths** rather than real-time A* pathfinding. The paths are defined in the game's schedule data as sequences of tile coordinates. NPCs walk from waypoint to waypoint.

However, there IS a real-time pathfinding layer for when NPCs need to navigate around unexpected obstacles. This uses a simplified pathfinding approach over the map's walkable tiles.

#### Can PathFindController Be Reused for Player Characters?

**In theory, yes, but with caveats:**

- `PathFindController` is designed for NPCs, not the player (`Farmer` class)
- The player's movement is handled differently (input-driven vs. controller-driven)
- You'd need to use Harmony to patch the controller to work with `Farmer` instances
- The pathfinding itself (the algorithm that finds the route) should be reusable

#### Practical Implementation Options

##### Option A: Custom A* Pathfinding

Implement a simple A* pathfinder in your SMAPI mod:

```csharp
// Check if a tile is walkable
bool IsWalkable(GameLocation loc, int x, int y)
{
    if (x < 0 || y < 0 || x >= loc.Map.Layers[0].LayerWidth || y >= loc.Map.Layers[0].LayerHeight)
        return false;
    if (loc.isTileBlockedBy(new Vector2(x, y)) != null)
        return false;
    if (loc.isWaterTile(x, y))
        return false;
    // Check for objects, terrain features, etc.
    return loc.isTilePassable(new Location(x, y), Game1.viewport);
}
```

Then implement A* over the tile grid, and move the player along the computed path by setting `movementDirections` each tick based on the next waypoint.

##### Option B: Reuse Game's Internal Pathfinding via Reflection

The game has internal pathfinding that `PathFindController` uses. You can access it:

```csharp
// The game's Pathfinding class (or similar) can be accessed via reflection
// This is the same pathfinding NPCs use for obstacle avoidance

// Use SMAPI's reflection helper:
var pathfinder = this.Helper.Reflection.GetMethod(SomeClass, "FindPath");
// Call with start tile, end tile, location
```

##### Option C: Waypoint-Based Movement

For simpler behavior, pre-compute known routes between important locations (farm → town → Pierre's → back) as tile-coordinate sequences, and have the AI follow these routes.

### Making Movement Look Natural

#### Speed Variation
```csharp
// Game1.player has a Speed field
// Default speed is typically around 2-4 pixels per frame
// Vary it slightly for natural movement:
Game1.player.addedSpeed = (float)(Game1.random.Next(-1, 2) * 0.5);
```

#### Pausing at Turns/Intersections
```csharp
// When reaching a waypoint, add a brief pause
// before changing direction:
if (reachedWaypoint)
{
    movementPauseTimer = Game1.random.Next(5, 20); // frames
    Game1.player.movementDirections = -1; // stop
}
```

#### Facing Direction
```csharp
// Always set facing direction to match movement
// (or look at the NPC/location of interest)
Game1.player.FacingDirection = 0; // 0=Up, 1=Right, 2=Down, 3=Left
```

#### Smooth Tile-to-Tile Movement

```csharp
// Instead of instant direction changes, use smooth interpolation
// The game handles pixel-level movement naturally when movementDirections is set
// Just make sure direction changes happen at tile boundaries:

Vector2 currentTile = Game1.player.getTileLocation();
if (currentTile == targetTile)
{
    // Reached waypoint, pick next direction
}
```

### Existing Pathfinding Mods

- **[NPC Path Displayer](https://www.nexusmods.com/stardewvalley/mods/7563)** — Shows NPC paths as square markers. Source code could reveal how NPC paths are accessed and displayed.
- **[NPC Pathable Backwoods](https://www.nexusmods.com/stardewvalley/mods/40185)** — Removes hardcoded NPC pathfinding bans. Shows how game code controls NPC pathfinding restrictions.
- **[Custom NPC Fixes](https://www.nexusmods.com/stardewvalley/mods/3849)** — Handles NPC pathfinding for custom NPC mods. May have reusable code.

### Recommended Approach

1. **Implement a custom A* pathfinder** — it's straightforward for a 2D tile grid, gives you full control, and doesn't depend on game internals that may change
2. **Add movement naturalization** — speed variation, turn pauses, facing direction alignment
3. **Cache frequently-used paths** — farm → town center, town → Pierre's, etc.
4. **Re-check path validity periodically** — objects may be placed/removed between uses

### Sources
- [Stardew Valley Multiplayer Wiki — NPC Pathfinding](https://www.reddit.com/r/StardewValley/comments/1d4dejg/a_randon_thought_why_is_the_npc_pathfinding/)
- [NPC Path Displayer Mod](https://www.nexusmods.com/stardewvalley/mods/7563)
- [NPC Pathable Backwoods Mod](https://www.nexusmods.com/stardewvalley/mods/40185)
- [SMAPI Game Fundamentals — Tiles](https://wiki.stardewvalley.net/Modding:Modder_Guide/Game_Fundamentals)

---

## 6. Farmhand State Sync in Multiplayer

### How Multiplayer State Synchronization Works

Stardew Valley uses **Lidgren.Network** (a lightweight UDP networking library) for multiplayer communication. The synchronization model is:

- **Host is authoritative.** The host's game state is the source of truth.
- **Farmhands are clients.** They receive state updates from the host.
- **State is synced via "net fields."** Fields marked as `NetBool`, `NetInt`, `NetString`, `NetCollection<T>`, etc. are automatically synchronized.

### What Gets Synced Automatically

| Data | Synced? | Direction | Notes |
|------|---------|-----------|-------|
| Player position | ✅ Yes | Farmhand → Host → Other farmhands | Position is synced through net fields on `Farmer.Position` and `Farmer.currentLocation` |
| Player facing direction | ✅ Yes | Farmhand → Host | |
| Player animation/frame | ✅ Yes | Farmhand → Host | The host sees the farmhand's animation state |
| Tool use | ✅ Yes | Farmhand → Host | Other players see tool swinging animations |
| Emotes | ✅ Yes | Farmhand → Host → All | Emotes are broadcast to all players |
| Inventory changes | ✅ Yes | Depends | Item additions/removals sync, but some operations are host-only |
| NPC friendship levels | ✅ Yes | Farmhand → Host | Changes to NPC friendship sync from farmhand |
| Time of day | ✅ Yes | Host → All | Time is authoritative on the host |
| Weather | ✅ Yes | Host → All | |
| Location objects | ✅ Partial | Host → Farmhands | Only "active locations" are synced |
| Location NPCs | ✅ Partial | Host → Farmhands | NPC positions sync in active locations |
| Farmhand health/energy | ✅ Yes | Farmhand → Host | |

### Sync Frequency and Latency

The game syncs state on a **per-update-tick basis** (60 ticks/second). However:

- **Position sync is NOT every tick.** It's batched and sent periodically (approximately every few ticks). This means there's noticeable position interpolation — other players see farmhands "teleport" slightly or move with slight rubber-banding.
- **Emote sync appears to be near-instant** — emotes are sent as explicit messages and broadcast immediately.
- **Tool use animations** are synced but may have 1-2 frames of delay.

### Active Location Syncing

Only certain locations are synced to farmhands (called "active locations"):

1. The farmhand's current location
2. The farm
3. Farm cave
4. Main farmhouse (not individual cabins or cellars)
5. Greenhouse
6. Farm buildings

**Implication:** If the AI farmhand warps to a new location, there's a brief moment where the game fetches the real location data from the host. During this transition, `Game1.currentLocation` may be null.

### How the Host Sees Farmhands

The host sees farmhands through synced `Farmer` objects that exist in the host's `GameLocation.farmers` collection. The host:

1. **Sees the farmhand's position** — updated via net field sync
2. **Sees tool use animations** — when the farmhand uses a tool, the animation state is synced
3. **Sees the farmhand's emotes** — broadcast immediately
4. **Sees the farmhand's facing direction** — synced via net fields
5. **Does NOT see chat bubbles from farmhands** unless the farmhand actually sends a chat message

### Net Fields Relevant to AI Farmhand

```csharp
// Farmer class net fields (synced automatically):
Game1.player.Position              // Vector2 position in pixels
Game1.player.currentLocation       // GameLocation reference (synced as location name)
Game1.player.FacingDirection       // int: 0=Up, 1=Right, 2=Down, 3=Left
Game1.player.isEmoting             // bool: whether emote animation is playing
Game1.player.emoteIndex            // int: which emote
Game1.player.CurrentToolIndex      // int: selected tool slot
Game1.player.Stamina               // float: energy
Game1.player.Health                // int: health
Game1.player.movementDirections    // THIS IS NOT A NET FIELD — only affects local movement

// ⚠️ IMPORTANT: movementDirections is NOT synced!
// Only the resulting Position is synced. The game sends position updates
// to the host based on local movement calculations.
```

### SMAPI Multiplayer Messaging

For custom data sync between your AI mod instances:

```csharp
// Send a message to all connected players (including host)
this.Helper.Multiplayer.SendMessage(
    data: myCustomData,
    messageType: "AIFarmhandStateUpdate"
);

// Receive messages
helper.Events.Multiplayer.ModMessageReceived += (sender, e) =>
{
    if (e.Type == "AIFarmhandStateUpdate")
    {
        var data = e.ReadAs<AIFarmhandState>();
        // Process the AI state update
    }
};
```

### Practical Implications for the AI Farmhand

1. **Position updates are reliable but not instant** — the host will see the farmhand move with slight lag. This is fine for a "player-like" appearance.

2. **Emotes are the best way to communicate intent** — they sync immediately and are visible to all players. Use them for "social" behavior.

3. **Tool use animations will be visible** — when the farmhand uses tools (hoe, watering can, etc.), the host will see the animation.

4. **`movementDirections` is local only** — you set it on the farmhand's game instance, and the resulting position changes are synced to the host. This is exactly what you want.

5. **Custom data needs SMAPI messaging** — if you want the host to know the AI's "state" (farming, socializing, idle, etc.), use SMAPI's multiplayer messaging API.

6. **Location transitions need care** — when warping between locations, there's a brief null period for `currentLocation`. Handle this gracefully in your mod.

### Sources
- [SMAPI Multiplayer API](https://wiki.stardewvalley.net/Modding:Modder_Guide/APIs/Multiplayer)
- [SMAPI Game Fundamentals — Net Fields](https://wiki.stardewvalley.net/Modding:Modder_Guide/Game_Fundamentals)
- [Stardew Valley Wiki — Multiplayer](https://stardewvalleywiki.com/Multiplayer)
- [Stardew Valley Wiki — Chat Commands](https://stardewvalleywiki.com/Chat)

---

## Summary: Recommended Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│                        HOST MACHINE                               │
│  ┌─────────────┐    ┌──────────────┐    ┌───────────────────┐   │
│  │ Stardew      │    │ SMAPI        │    │ AI Farmhand       │   │
│  │ Valley       │◄──►│ Bridge Mod   │◄──►│ Controller Mod    │   │
│  │ (Host)       │    │ (C#)         │    │ (C# in SMAPI)     │   │
│  └─────────────┘    └──────┬───────┘    └───────────────────┘   │
│                            │                                       │
└────────────────────────────┼──────────────────────────────────────┘
                             │ TCP/WebSocket
┌────────────────────────────┼──────────────────────────────────────┐
│                        AI SERVER                                   │
│  ┌─────────────┐    ┌──────┴───────┐                              │
│  │ AI Engine   │───►│ Bridge       │                              │
│  │ (LLM/etc)   │    │ Server       │                              │
│  └─────────────┘    └──────────────┘                              │
└──────────────────────────────────────────────────────────────────┘
```

Key decisions:
- **All game input goes through SMAPI's `helper.Input.Press()`** — no external input injection needed
- **Movement uses `movementDirections`** with custom A* pathfinding for obstacle avoidance
- **NPC interaction** works naturally for farmhands — talking, gifting, marriage are all supported
- **Multiplayer sync** handles position, animations, and emotes automatically via net fields
- **Linux-specific issues** (chat bubble spam) addressed with `SDL_INPUT_LINUX_KEEP_KBD=0` + Xvfb
- **Co-op join** automated via SMAPI mod menu navigation or console command
