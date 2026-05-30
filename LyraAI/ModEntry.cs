using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;
using StardewValley.Network;
using StardewValley.ItemTypeDefinitions;

namespace LyraAI
{
    public class ModEntry : Mod
    {
        private readonly string BridgeDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw", "agents", "lyra", "agent", "stardew_bridge"
        );

        private string CommandsPath => Path.Combine(BridgeDir, "commands.json");
        private string StatePath => Path.Combine(BridgeDir, "state.json");
        private string DialoguePath => Path.Combine(BridgeDir, "dialogue_out.json");
        private string ChatInPath => Path.Combine(BridgeDir, "chat_in.json");

        private CancellationTokenSource _cts;
        private DateTime _lastCommandTime = DateTime.UtcNow;
        private DateTime _lastIdleEmote = DateTime.UtcNow;
        private bool _isMoving = false;
        private Vector2 _moveTarget = Vector2.Zero;
        private int _pathCooldown = 0;
        private bool _pathFailed = false;

        // Natural movement personality (Phase 1)
        private readonly Random _rand = new();
        private int _pauseTicks = 0;           // >0 means pause movement for natural "hesitation"
        private DateTime _lastPersonalityEmote = DateTime.UtcNow;
        private readonly HashSet<string> _prevNearbyNames = new(); // for reactive emotes on player enter/leave

        // Area transition support (Phase 1.5) — deepened
        private Dictionary<string, List<dynamic>> _exitsByLocation = new(); // Better per-location caching
        private List<dynamic> _cachedExits = new(); // Convenience reference to current location's exits
        private string _lastLocationWithExits = "";
        private int _transitionCooldown = 0; // Prevents rapid repeated exit pathing attempts

        // Stuck detection for transitions (high priority reliability work)
        private Vector2 _lastTransitionPosition = Vector2.Zero;
        private int _ticksWithoutTransitionProgress = 0;
        private const int STUCK_THRESHOLD_TICKS = 60; // ~4 seconds at 15 updates/sec (every 4th tick)
        private bool _isAttemptingTransition = false;

        // Follow command state
        private string _following = null; // target farmer name, null = not following

        // Multi-hit tool use state
        private CancellationTokenSource _multiHitCts = null;

        // Chat capture
        private readonly List<string> _capturedChatBuffer = new();
        private DateTime _lastChatFlush = DateTime.UtcNow;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public override void Entry(IModHelper helper)
        {
            Monitor.Log("LyraAI mod loaded 🦋", LogLevel.Alert);

            // Ensure bridge directory exists
            Directory.CreateDirectory(BridgeDir);
            EnsureFile(CommandsPath, "{\"commands\":[]}");
            EnsureFile(StatePath, "{}");
            EnsureFile(DialoguePath, "{\"text\":\"\"}");
            EnsureFile(ChatInPath, "[]");

            _cts = new CancellationTokenSource();

            // Start background tasks
            Task.Run(() => PollCommandsLoop(_cts.Token), _cts.Token);
            Task.Run(() => WriteStateLoop(_cts.Token), _cts.Token);
            Task.Run(() => PollDialogueLoop(_cts.Token), _cts.Token);
            Task.Run(() => FlushChatBufferLoop(_cts.Token), _cts.Token);

            // Hook into update tick for smooth movement and idle behavior
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            // Hook into multiplayer chat for capture
            helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
            helper.Events.GameLoop.DayStarted += OnDayStarted;

            // Debug/test console commands (use in SMAPI console during play to verify functionality)
            helper.ConsoleCommands.Add("lyra_test_chat", "Send test chat message using the mod's SendChatMessage (verify it appears for host)", (cmd, args) =>
            {
                string msg = args.Length > 0 ? string.Join(" ", args) : "Lyra test chat 🦋";
                SendChatMessage(msg);
                Monitor.Log($"[lyra_test_chat] Sent: {msg}", LogLevel.Alert);
            });
            helper.ConsoleCommands.Add("lyra_status", "Print current LyraAI internal state (following, moving, path status)", (cmd, args) =>
            {
                Monitor.Log($"[lyra_status] following={_following ?? "none"} isMoving={_isMoving} pathFailed={_pathFailed} pathCooldown={_pathCooldown} loc={Game1.currentLocation?.Name}", LogLevel.Alert);
            });
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            _capturedChatBuffer.Clear();
            _seenChatKeys.Clear();
            _following = null;
            _isMoving = false;
            _pathCooldown = 0;
            _pathFailed = false;
            _pauseTicks = 0;
            _lastPersonalityEmote = DateTime.UtcNow;
            _prevNearbyNames.Clear();
            _exitsByLocation.Clear();
            _cachedExits.Clear();
            _lastLocationWithExits = "";
            _transitionCooldown = 0;
            _ticksWithoutTransitionProgress = 0;
            _isAttemptingTransition = false;
        }

        private void EnsureFile(string path, string defaultContent)
        {
            if (!File.Exists(path))
            {
                try { File.WriteAllText(path, defaultContent); }
                catch (Exception ex) { Monitor.Log($"Failed to create {path}: {ex.Message}", LogLevel.Warn); }
            }
        }

        /// <summary>
        /// Atomic write: write to temp file then rename. Prevents partial/corrupt state.json on crash or interrupt.
        /// </summary>
        private void AtomicWriteFile(string path, string content)
        {
            string tmpPath = path + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, content);
                File.Move(tmpPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Monitor.Log($"AtomicWrite failed for {path}: {ex.Message}", LogLevel.Warn);
                // Fallback to direct write
                try { File.WriteAllText(path, content); } catch { }
            }
        }

        // ─── Chat Capture ───
        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            // Capture chat messages from other players via SMAPI messages
            try
            {
                if (e.FromModID != "SMAPI" && e.Type != "ChatMessage")
                    return;
            }
            catch { }
        }

        /// <summary>
        /// Called from the update tick to poll the game's chatBox for new messages.
        /// This is the primary chat capture mechanism.
        /// </summary>
        private void CaptureChatMessages()
        {
            try
            {
                if (Game1.chatBox == null || Game1.chatBox.messages == null)
                    return;

                var chatBox = Game1.chatBox;
                var messagesField = chatBox.GetType().GetField("messages",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (messagesField == null)
                {
                    // Try _messages
                    messagesField = chatBox.GetType().GetField("_messages",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }

                if (messagesField == null) return;

                var messages = messagesField.GetValue(chatBox) as System.Collections.IList;
                if (messages == null || messages.Count == 0) return;

                // Get the last message to check for new ones
                // We'll iterate and look for messages we haven't seen
                for (int i = 0; i < messages.Count; i++)
                {
                    var msg = messages[i];
                    if (msg == null) continue;

                    var msgType = msg.GetType();
                    string text = "";
                    long? timestamp = null;

                    // Try to get the message text - varies by SMAPI version
                    var textProp = msgType.GetProperty("message",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? msgType.GetProperty("Text",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    var timeProp = msgType.GetProperty("timestamp",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (textProp != null)
                        text = textProp.GetValue(msg)?.ToString() ?? "";

                    if (timeProp != null)
                        timestamp = Convert.ToInt64(timeProp.GetValue(msg));

                    if (string.IsNullOrWhiteSpace(text)) continue;

                    // Parse out the sender - chat messages are usually formatted as "Name: text"
                    string from = "Unknown";
                    string content = text;

                    if (text.Contains(":"))
                    {
                        int colonIdx = text.IndexOf(':');
                        from = text.Substring(0, colonIdx).Trim();
                        content = text.Substring(colonIdx + 1).Trim();
                    }

                    // Create a unique key to avoid duplicates
                    string key = $"{from}:{content}";
                    if (!_seenChatKeys.Contains(key))
                    {
                        _seenChatKeys.Add(key);
                        // Keep the set from growing unbounded
                        if (_seenChatKeys.Count > 500)
                        {
                            _seenChatKeys.Remove(_seenChatKeys.First());
                        }

                        _capturedChatBuffer.Add(JsonSerializer.Serialize(new
                        {
                            from,
                            text = content,
                            timestamp = timestamp ?? DateTime.UtcNow.Ticks,
                            time = DateTime.UtcNow.ToString("o")
                        }));
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Chat capture error: {ex.Message}", LogLevel.Trace);
            }
        }

        private HashSet<string> _seenChatKeys = new();

        private async Task FlushChatBufferLoop(CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_capturedChatBuffer.Count > 0)
                    {
                        lock (_capturedChatBuffer)
                        {
                            if (_capturedChatBuffer.Count > 0)
                            {
                                // Read existing chat_in.json and append
                                List<string> existing = new();
                                try
                                {
                                    string existingRaw = File.ReadAllText(ChatInPath);
                                    if (!string.IsNullOrWhiteSpace(existingRaw))
                                    {
                                        using var doc = JsonDocument.Parse(existingRaw);
                                        foreach (var elem in doc.RootElement.EnumerateArray())
                                            existing.Add(elem.GetRawText());
                                    }
                                }
                                catch { }

                                existing.AddRange(_capturedChatBuffer);
                                _capturedChatBuffer.Clear();

                                // Keep only last 200 messages
                                if (existing.Count > 200)
                                    existing = existing.TakeLast(200).ToList();

                                AtomicWriteFile(ChatInPath, "[" + string.Join(",", existing) + "]");
                            }
                        }
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { Monitor.Log($"Chat flush error: {ex.Message}", LogLevel.Warn); }

                try { await Task.Delay(2000, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        // ─── Chat Send ───
        private void SendChatMessage(string message)
        {
            try
            {
                var player = Game1.player;
                if (player == null) return;

                // Access Game1.multiplayer via reflection (it's internal in 1.6.x)
                var multiplayerField = typeof(Game1).GetField("multiplayer",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                object multiplayer = multiplayerField?.GetValue(null);
                if (multiplayer == null)
                {
                    Monitor.Log("Cannot access Game1.multiplayer for chat send", LogLevel.Warn);
                    return;
                }
                var mpType = multiplayer.GetType();

                // Try SendChatMessage(long, string) - most common in 1.6.x
                var sendChat = mpType.GetMethod("SendChatMessage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(long), typeof(string) }, null);

                if (sendChat != null)
                {
                    sendChat.Invoke(multiplayer, new object[] { player.UniqueMultiplayerID, message });
                    Monitor.Log($"Sent chat: {message}", LogLevel.Debug);
                    return;
                }

                // Try SendChatMessage(string)
                sendChat = mpType.GetMethod("SendChatMessage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);

                if (sendChat != null)
                {
                    sendChat.Invoke(multiplayer, new object[] { message });
                    Monitor.Log($"Sent chat: {message}", LogLevel.Debug);
                    return;
                }

                // Try lowercase sendChatMessage
                sendChat = mpType.GetMethod("sendChatMessage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (sendChat != null)
                {
                    var parameters = sendChat.GetParameters();
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(long))
                        sendChat.Invoke(multiplayer, new object[] { player.UniqueMultiplayerID, message });
                    else
                        sendChat.Invoke(multiplayer, new object[] { message });
                    Monitor.Log($"Sent chat: {message}", LogLevel.Debug);
                    return;
                }

                Monitor.Log("Could not find chat send method on multiplayer", LogLevel.Warn);

                // Last-ditch fallback: try adding directly to the local chat box (visible at least locally)
                if (Game1.chatBox != null)
                {
                    try
                    {
                        Game1.chatBox.addMessage(message, Color.White);
                        Monitor.Log($"Chat (local fallback): {message}", LogLevel.Debug);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"Chat send error: {ex.Message}", LogLevel.Warn);
            }
        }

        // ─── Command Polling (every 1s) ───
        private async Task PollCommandsLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    ProcessCommands();
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex) { Monitor.Log($"Command poll error: {ex.Message}", LogLevel.Warn); }
            }
        }

        private void ProcessCommands()
        {
            try
            {
                if (!File.Exists(CommandsPath)) return;
                string raw = File.ReadAllText(CommandsPath);
                if (string.IsNullOrWhiteSpace(raw)) return;

                var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("commands", out var cmdsElem)) return;

                var commands = new List<JsonElement>();
                foreach (var c in cmdsElem.EnumerateArray())
                    commands.Add(c);

                if (commands.Count == 0) return;

                // Clear the file to avoid re-processing (atomic for safety)
                AtomicWriteFile(CommandsPath, "{\"commands\":[]}");
                _lastCommandTime = DateTime.UtcNow;

                foreach (var cmd in commands)
                {
                    if (!cmd.TryGetProperty("action", out var actionElem)) continue;
                    string action = actionElem.GetString()?.ToLowerInvariant() ?? "";

                    try
                    {
                        switch (action)
                        {
                            case "move":
                                HandleMove(cmd);
                                break;
                            case "emote":
                                HandleEmote(cmd);
                                break;
                            case "gift":
                                HandleGift(cmd);
                                break;
                            case "dialogue":
                                HandleDialogue(cmd);
                                break;
                            case "usetool":
                                HandleUseTool(cmd);
                                break;
                            case "water":
                                HandleWater(cmd);
                                break;
                            case "pickup":
                                HandlePickup(cmd);
                                break;
                            case "chat":
                                HandleChat(cmd);
                                break;
                            case "follow":
                                HandleFollow(cmd);
                                break;
                            case "stop":
                                HandleStop(cmd);
                                break;
                            case "transition":
                                HandleTransition(cmd);
                                break;
                            case "chest_deposit":
                                HandleChestDeposit(cmd);
                                break;
                            case "chest_withdraw":
                                HandleChestWithdraw(cmd);
                                break;
                            case "forage":
                                HandleForage(cmd);
                                break;
                            case "autonomous_water":
                                HandleAutonomousWater(cmd);
                                break;
                            case "start_autonomy":
                                HandleStartAutonomy(cmd);
                                break;
                            case "stop_autonomy":
                                HandleStopAutonomy(cmd);
                                break;
                            case "waitforevent":
                                // No-op: just resets idle timer
                                break;
                            default:
                                Monitor.Log($"Unknown command action: {action}", LogLevel.Warn);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"Error executing command '{action}': {ex.Message}", LogLevel.Warn);
                    }
                }
            }
            catch (Exception ex)
            {
                Monitor.Log($"ProcessCommands error: {ex.Message}", LogLevel.Warn);
            }
        }

        private void HandleMove(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null || Game1.currentLocation == null) return;

            int tx = cmd.TryGetProperty("targetTileX", out var txE) ? txE.GetInt32() : -1;
            int ty = cmd.TryGetProperty("targetTileY", out var tyE) ? tyE.GetInt32() : -1;
            if (tx < 0 || ty < 0) return;

            _following = null; // Stop following if explicitly moving

            // Phase 1.5: If the target tile is far outside current map, try to use an exit instead
            var location = Game1.currentLocation;
            if (location?.map != null && _cachedExits.Count > 0)
            {
                int mapWidth = location.map.Layers[0].LayerWidth;
                int mapHeight = location.map.Layers[0].LayerHeight;

                bool targetIsOffMap = tx < -2 || ty < -2 || tx > mapWidth + 2 || ty > mapHeight + 2;

                if (targetIsOffMap)
                {
                    // Try to find any reasonable exit to help the player leave the map
                    if (TryPathTowardExitForLocation("")) // empty string = any exit
                    {
                        Monitor.Log($"[MOVE] Target ({tx},{ty}) is off-map. Using exit pathing instead.", LogLevel.Debug);
                        return;
                    }
                }
            }

            _moveTarget = new Vector2(tx, ty);
            _isMoving = true;
            _pathFailed = false;

            // Use PathFindController for obstacle-aware navigation
            TrySetPath(new Point(tx, ty));
            Monitor.Log($"Pathfinding to tile ({tx}, {ty})", LogLevel.Debug);
        }

        /// <summary>
        /// Assigns a PathFindController to the player for obstacle-aware navigation (robust reflection across SDV 1.6.x builds).
        /// Tries multiple constructor signatures (4-param, 5-param, any viable). Clears stale controller first.
        /// Falls back to straight-line movement (in OnUpdateTicked) if reflection fails.
        /// Arrival detection: stops within 0.5 tiles of target.
        /// </summary>
        private bool TrySetPath(Point target)
        {
            if (_pathCooldown > 0) return false;

            var player = Game1.player;
            var location = Game1.currentLocation;
            if (player == null || location == null) return false;

            // Always clear existing controller before assigning new path (prevents stale paths)
            try { player.controller = null; } catch { }

            try
            {
                // Reflection: find the type (handles internal/non-public classes in SDV 1.6.x)
                var pfcType = typeof(Game1).Assembly.GetType("StardewValley.PathFindController")
                    ?? typeof(Game1).Assembly.GetType("StardewValley.GamePathFinder.PathFindController");

                if (pfcType != null)
                {
                    var ctors = pfcType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    // Try common 4-param: (Character, GameLocation, Point, int facing)
                    var ctor4 = ctors.FirstOrDefault(c =>
                    {
                        var p = c.GetParameters();
                        return p.Length == 4
                            && p[0].ParameterType == typeof(Character)
                            && p[1].ParameterType == typeof(GameLocation)
                            && p[2].ParameterType == typeof(Point)
                            && (p[3].ParameterType == typeof(int) || p[3].ParameterType == typeof(int));
                    });

                    if (ctor4 != null)
                    {
                        var pfc = ctor4.Invoke(new object[] { player, location, target, 2 });
                        var ctrlProp = typeof(Character).GetProperty("controller", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        ctrlProp?.SetValue(player, pfc);
                        _pathCooldown = 4;
                        _pathFailed = false;
                        Monitor.Log($"Pathfinding (refl-4) to ({target.X}, {target.Y}) via {pfcType.FullName}", LogLevel.Debug);
                        return true;
                    }

                    // Try 5-param variants seen in some decompiles: (Character, GameLocation, Point, int, bool finalFacing or something)
                    var ctor5 = ctors.FirstOrDefault(c =>
                    {
                        var p = c.GetParameters();
                        return p.Length == 5
                            && p[0].ParameterType == typeof(Character)
                            && p[1].ParameterType == typeof(GameLocation)
                            && p[2].ParameterType == typeof(Point);
                    });

                    if (ctor5 != null)
                    {
                        var pfc = ctor5.Invoke(new object[] { player, location, target, 2, false });
                        var ctrlProp = typeof(Character).GetProperty("controller", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        ctrlProp?.SetValue(player, pfc);
                        _pathCooldown = 4;
                        _pathFailed = false;
                        Monitor.Log($"Pathfinding (refl-5) to ({target.X}, {target.Y})", LogLevel.Debug);
                        return true;
                    }

                    // Fallback: any ctor with at least Character + GameLocation + Point, pad with defaults
                    var ctorAny = ctors.FirstOrDefault(c =>
                    {
                        var p = c.GetParameters();
                        return p.Length >= 3
                            && p[0].ParameterType == typeof(Character)
                            && p[1].ParameterType == typeof(GameLocation)
                            && p[2].ParameterType == typeof(Point);
                    });

                    if (ctorAny != null)
                    {
                        var parms = ctorAny.GetParameters();
                        var args = new object[parms.Length];
                        args[0] = player;
                        args[1] = location;
                        args[2] = target;
                        for (int i = 3; i < parms.Length; i++)
                        {
                            if (parms[i].ParameterType == typeof(int)) args[i] = 2;
                            else if (parms[i].ParameterType == typeof(bool)) args[i] = false;
                            else args[i] = parms[i].DefaultValue != DBNull.Value ? parms[i].DefaultValue : null;
                        }
                        var pfc = ctorAny.Invoke(args);
                        var ctrlProp = typeof(Character).GetProperty("controller", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        ctrlProp?.SetValue(player, pfc);
                        _pathCooldown = 4;
                        _pathFailed = false;
                        Monitor.Log($"Pathfinding (refl-any) to ({target.X}, {target.Y})", LogLevel.Debug);
                        return true;
                    }
                }

                // All reflection attempts failed — fall back to straight-line in movement tick
                Monitor.Log($"PathFindController not found/constructable — straight-line fallback active for ({target.X},{target.Y})", LogLevel.Warn);
                _pathFailed = true;
                _pathCooldown = 4;
                return false;
            }
            catch (Exception ex)
            {
                Monitor.Log($"Pathfinding error to ({target.X}, {target.Y}): {ex.Message}", LogLevel.Warn);
                _pathFailed = true;
                try { player.controller = null; } catch { }
                _pathCooldown = 4;
                return false;
            }
        }

        private void HandleEmote(JsonElement cmd)
        {
            int emoteId = cmd.TryGetProperty("emoteId", out var eE) ? eE.GetInt32() : 0;
            if (Context.IsWorldReady && Game1.player != null)
            {
                Game1.player.doEmote(emoteId);
                Monitor.Log($"Emote: {emoteId}", LogLevel.Debug);
            }
        }

        private void HandleGift(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null) return;

            int itemIndex = cmd.TryGetProperty("itemParentSheetIndex", out var iE) ? iE.GetInt32() : -1;
            string targetName = cmd.TryGetProperty("targetFarmerName", out var tE) ? tE.GetString() : null;

            if (itemIndex < 0 || string.IsNullOrEmpty(targetName)) return;

            // Find the target farmer
            Farmer target = null;
            foreach (var farmer in Game1.currentLocation.farmers)
            {
                if (farmer.Name?.Equals(targetName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    target = farmer;
                    break;
                }
            }

            if (target == null)
            {
                Monitor.Log($"Gift target not found: {targetName}", LogLevel.Warn);
                return;
            }

            // Create the item
            var item = ItemRegistry.Create("(O)" + itemIndex);
            if (item == null)
            {
                Monitor.Log($"Could not create item with index {itemIndex}", LogLevel.Warn);
                return;
            }

            // Try to gift it - add to target's inventory
            if (target.couldInventoryAcceptThisItem(item))
            {
                target.addItemToInventory(item);
                Game1.addHUDMessage(new HUDMessage($"Lyra gave {target.Name} a {item.DisplayName}!", 3));
                Monitor.Log($"Gifted {item.DisplayName} to {target.Name}", LogLevel.Debug);
            }
            else
            {
                Monitor.Log($"Target {target.Name} inventory full", LogLevel.Warn);
            }
        }

        private void HandleDialogue(JsonElement cmd)
        {
            string text = cmd.TryGetProperty("text", out var tE) ? tE.GetString() : null;
            if (string.IsNullOrEmpty(text) || !Context.IsWorldReady) return;

            // Show dialogue using HUD message for non-blocking display
            Game1.addHUDMessage(new HUDMessage(text, 2));
            Monitor.Log($"Dialogue: {text}", LogLevel.Debug);
        }

        // ─── Water Command ───
        private void HandleWater(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null || Game1.currentLocation == null) return;

            var player = Game1.player;
            var location = Game1.currentLocation;

            // Switch to watering can (tool index 2 in starter inventory)
            int wateringCanIndex = -1;
            for (int i = 0; i < player.Items.Count; i++)
            {
                if (player.Items[i] is WateringCan)
                {
                    wateringCanIndex = i;
                    break;
                }
            }

            if (wateringCanIndex < 0)
            {
                Monitor.Log("No watering can found in inventory", LogLevel.Warn);
                return;
            }

            player.CurrentToolIndex = wateringCanIndex;

            // Find player tile position
            int px = (int)(player.Position.X / Game1.tileSize);
            int py = (int)(player.Position.Y / Game1.tileSize);

            // Allow optional center override from command
            int centerX = cmd.TryGetProperty("centerX", out var cxE) ? cxE.GetInt32() : px;
            int centerY = cmd.TryGetProperty("centerY", out var cyE) ? cyE.GetInt32() : py;

            int wateredCount = 0;

            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    int tx = centerX + dx;
                    int ty = centerY + dy;
                    var tilePos = new Vector2(tx, ty);

                    // Check terrainFeatures for HoeDirt
                    if (location.terrainFeatures.TryGetValue(tilePos, out var feature))
                    {
                        if (feature is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.state.Value == 0)
                        {
                            dirt.state.Value = 1; // set to watered
                            wateredCount++;
                        }
                    }

                    // Also check indoorPot dirt (Greenhouse, etc.)
                    if (location.Objects.TryGetValue(tilePos, out var obj))
                    {
                        if (obj is StardewValley.Objects.IndoorPot pot && pot.hoeDirt.Value != null
                            && pot.hoeDirt.Value.state.Value == 0)
                        {
                            pot.hoeDirt.Value.state.Value = 1;
                            wateredCount++;
                        }
                    }
                }
            }

            Monitor.Log($"Watered {wateredCount} tile(s) around ({centerX}, {centerY})", LogLevel.Debug);
        }

        // ─── Pickup Command ───
        private void HandlePickup(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null || Game1.currentLocation == null) return;

            var player = Game1.player;
            var location = Game1.currentLocation;

            // Get target tile (default: player's current tile)
            int tx = cmd.TryGetProperty("tileX", out var txE) ? txE.GetInt32() : (int)(player.Position.X / Game1.tileSize);
            int ty = cmd.TryGetProperty("tileY", out var tyE) ? tyE.GetInt32() : (int)(player.Position.Y / Game1.tileSize);
            var tilePos = new Vector2(tx, ty);

            int pickedUp = 0;

            // Check for objects on the ground at this tile
            if (location.Objects.TryGetValue(tilePos, out var obj) && obj != null)
            {
                if (player.couldInventoryAcceptThisItem(obj))
                {
                    // Copy reference before removing (Remove returns bool in 1.6.x)
                    var itemRef = obj;
                    location.Objects.Remove(tilePos);
                    player.addItemToInventory(itemRef);
                    pickedUp++;
                    Monitor.Log($"Picked up {itemRef.DisplayName} from ({tx}, {ty})", LogLevel.Debug);
                }
            }

            // Check for forage/debris items using reflection (Debris fields are internal)
            var debrisField = typeof(GameLocation).GetField("debris",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var debrisList = debrisField?.GetValue(location) as System.Collections.IList;
            if (debrisList != null)
            {
                for (int i = debrisList.Count - 1; i >= 0; i--)
                {
                    var debris = debrisList[i];
                    if (debris == null) continue;

                    var debrisType = debris.GetType();

                    // Get position via reflection
                    var posProp = debrisType.GetProperty("Position",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (posProp == null)
                        posProp = debrisType.GetProperty("position",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (posProp == null) continue;

                    var debrisPos = posProp.GetValue(debris);
                    if (debrisPos == null) continue;

                    int debrisTileX = (int)(((Vector2)debrisPos).X / Game1.tileSize);
                    int debrisTileY = (int)(((Vector2)debrisPos).Y / Game1.tileSize);

                    // Check within 1 tile of target
                    if (Math.Abs(debrisTileX - tx) <= 1 && Math.Abs(debrisTileY - ty) <= 1)
                    {
                        // Get debris item
                        var itemField = debrisType.GetField("item",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var debrisItem = itemField?.GetValue(debris) as Item;

                        if (debrisItem != null && player.couldInventoryAcceptThisItem(debrisItem))
                        {
                            player.addItemToInventory(debrisItem);
                            itemField.SetValue(debris, null);
                            debrisList.RemoveAt(i);
                            pickedUp++;
                            Monitor.Log($"Picked up forage debris near ({tx}, {ty})", LogLevel.Debug);
                        }
                    }
                }
            }

            if (pickedUp == 0)
            {
                Monitor.Log($"Nothing to pick up at ({tx}, {ty})", LogLevel.Trace);
            }
        }

        // ─── Multi-hit Tool Use ───
        private void HandleUseTool(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null) return;

            int toolIndex = cmd.TryGetProperty("toolIndex", out var tE) ? tE.GetInt32() : -1;
            int tileX = cmd.TryGetProperty("tileX", out var txE) ? txE.GetInt32() : -1;
            int tileY = cmd.TryGetProperty("tileY", out var tyE) ? tyE.GetInt32() : -1;
            int hits = cmd.TryGetProperty("hits", out var hE) ? Math.Max(1, hE.GetInt32()) : 3;

            string toolType = cmd.TryGetProperty("toolType", out var ttE) ? ttE.GetString()?.ToLowerInvariant() : null;

            // Auto-detect tool index from toolType if toolIndex not specified
            if (toolIndex < 0 && !string.IsNullOrEmpty(toolType))
            {
                for (int i = 0; i < Game1.player.Items.Count; i++)
                {
                    var item = Game1.player.Items[i];
                    if (item == null) continue;

                    switch (toolType)
                    {
                        case "pickaxe" when item is Pickaxe:
                        case "axe" when item is Axe:
                        case "hoe" when item is Hoe:
                        case "wateringcan" when item is WateringCan:
                            toolIndex = i;
                            break;
                    }
                    if (toolIndex >= 0) break;
                }
            }

            if (toolIndex < 0 || toolIndex >= Game1.player.Items.Count) return;
            if (Game1.player.Items[toolIndex] is not Tool tool) return;
            if (tileX < 0 || tileY < 0) return;

            // Cancel any previous multi-hit
            _multiHitCts?.Cancel();
            _multiHitCts?.Dispose();

            var cts = new CancellationTokenSource();
            _multiHitCts = cts;

            _ = MultiHitToolUse(toolIndex, tileX, tileY, hits, cts.Token);
        }

        private async Task MultiHitToolUse(int toolIndex, int tileX, int tileY, int hits, CancellationToken ct)
        {
            var player = Game1.player;
            if (player == null) return;

            player.CurrentToolIndex = toolIndex;
            var targetTile = new Vector2(tileX, tileY);

            for (int i = 0; i < hits; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (!Context.IsWorldReady || Game1.player == null) break;

                try
                {
                    // Face the target tile
                    var playerTile = new Vector2(
                        Game1.player.Position.X / Game1.tileSize,
                        Game1.player.Position.Y / Game1.tileSize
                    );
                    var dir = targetTile - playerTile;
                    // Set facing direction via reflection
                    int faceDir;
                    if (Math.Abs(dir.X) > Math.Abs(dir.Y))
                        faceDir = dir.X > 0 ? 1 : 3; // right : left
                    else
                        faceDir = dir.Y > 0 ? 2 : 0; // down : up
                    try
                    {
                        var faceField = typeof(Character).GetField("facingDirection",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (faceField != null)
                            faceField.SetValue(Game1.player, faceDir);
                    }
                    catch (Exception faceEx)
                    {
                        Monitor.Log($"Could not set facing direction: {faceEx.Message}", LogLevel.Trace);
                    }

                    // Set the tool's last user via reflection (readonly field)
                    var tool = Game1.player.CurrentTool;
                    if (tool != null)
                    {
                        try
                        {
                            var luField = typeof(Tool).GetField("lastUser",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            luField?.SetValue(tool, Game1.player);
                        }
                        catch { }
                    }

                    // Press use tool button to swing
                    Game1.pressUseToolButton();
                    Monitor.Log($"Tool swing {i + 1}/{hits} at ({tileX}, {tileY})", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Multi-hit swing error: {ex.Message}", LogLevel.Warn);
                }

                // Delay between swings (game ticks ~= 16ms per tick, 30 ticks = ~0.5s)
                await Task.Delay(500, ct);
            }
        }

        // ─── Chat Command ───
        private void HandleChat(JsonElement cmd)
        {
            string message = cmd.TryGetProperty("text", out var tE) ? tE.GetString() : null;
            if (string.IsNullOrWhiteSpace(message)) return;

            message = message.Trim();
            SendChatMessage(message);
        }

        // ─── Follow Command ───
        private void HandleFollow(JsonElement cmd)
        {
            string targetName = cmd.TryGetProperty("targetName", out var tE) ? tE.GetString() : null;

            if (string.IsNullOrEmpty(targetName))
            {
                // Default: find first nearby player
                if (Game1.currentLocation?.farmers != null)
                {
                    foreach (var farmer in Game1.currentLocation.farmers)
                    {
                        if (farmer != null && farmer != Game1.player)
                        {
                            targetName = farmer.Name;
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(targetName))
            {
                Monitor.Log("No target to follow", LogLevel.Warn);
                return;
            }

            _following = targetName;
            _isMoving = false; // Let the follow logic handle movement
            Monitor.Log($"Now following: {_following}", LogLevel.Debug);
        }

        // ─── Stop Command ───
        private void HandleStop(JsonElement cmd)
        {
            string stopWhat = cmd.TryGetProperty("stop", out var sE) ? sE.GetString()?.ToLowerInvariant() : null;

            if (stopWhat == "follow" || stopWhat == null)
            {
                _following = null;
            }

            _isMoving = false;
            _pathFailed = false;
            _isAttemptingTransition = false;
            _ticksWithoutTransitionProgress = 0;
            if (Game1.player != null)
            {
                Game1.player.movementDirections?.Clear();
                Game1.player.controller = null;
            }
            _multiHitCts?.Cancel();

            Monitor.Log("Stopped", LogLevel.Debug);
        }

        // ─── Transition Command (Phase 1.5) ───
        private void HandleTransition(JsonElement cmd)
        {
            string targetLocation = cmd.TryGetProperty("targetLocation", out var tE) ? tE.GetString() ?? "" : "";
            int exitIndex = cmd.TryGetProperty("exitIndex", out var eE) ? eE.GetInt32() : -1;

            if (string.IsNullOrEmpty(targetLocation) && exitIndex < 0)
            {
                Monitor.Log("Transition command requires either targetLocation or exitIndex", LogLevel.Warn);
                return;
            }

            if (_cachedExits == null || _cachedExits.Count == 0)
            {
                Monitor.Log("No cached exits available for transition", LogLevel.Warn);
                return;
            }

            dynamic? chosenExit = null;

            if (exitIndex >= 0 && exitIndex < _cachedExits.Count)
            {
                chosenExit = _cachedExits[exitIndex];
            }
            else if (!string.IsNullOrEmpty(targetLocation))
            {
                // Find best matching exit
                foreach (var ex in _cachedExits)
                {
                    if ((ex.targetLocation?.ToString() ?? "").Equals(targetLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        chosenExit = ex;
                        break;
                    }
                }

                // Fallback to first exit if no perfect match
                if (chosenExit == null) chosenExit = _cachedExits[0];
            }

            if (chosenExit != null)
            {
                int tx = (int)chosenExit.x;
                int ty = (int)chosenExit.y;

                _following = null;
                _moveTarget = new Vector2(tx, ty);
                _isMoving = true;
                _pathFailed = false;

                TrySetPath(new Point(tx, ty));
                Monitor.Log($"Transition command: Pathing to exit ({tx},{ty}) → {chosenExit.targetLocation}", LogLevel.Debug);
            }
        }

        // ─── Chest Interaction (Phase 3 - Agency with minimal token cost) ───
        private void HandleChestDeposit(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null) return;

            string itemName = cmd.TryGetProperty("itemName", out var iE) ? iE.GetString() : null;
            int chestX = cmd.TryGetProperty("chestX", out var cxE) ? cxE.GetInt32() : -1;
            int chestY = cmd.TryGetProperty("chestY", out var cyE) ? cyE.GetInt32() : -1;

            if (string.IsNullOrEmpty(itemName) || chestX < 0 || chestY < 0) return;

            var player = Game1.player;
            var location = Game1.currentLocation;
            var tilePos = new Vector2(chestX, chestY);

            if (!location.Objects.TryGetValue(tilePos, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                Monitor.Log($"No chest found at ({chestX},{chestY})", LogLevel.Warn);
                return;
            }

            // Find item in player inventory
            for (int i = 0; i < player.Items.Count; i++)
            {
                var item = player.Items[i];
                if (item != null && item.DisplayName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                {
                    if (chest.addItem(item) != null)
                    {
                        Monitor.Log($"Chest at ({chestX},{chestY}) is full or rejected item", LogLevel.Warn);
                        return;
                    }

                    player.Items[i] = null; // Remove from player
                    Monitor.Log($"Deposited {itemName} into chest at ({chestX},{chestY})", LogLevel.Debug);
                    return;
                }
            }

            Monitor.Log($"Item '{itemName}' not found in inventory", LogLevel.Warn);
        }

        private void HandleChestWithdraw(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null) return;

            string itemName = cmd.TryGetProperty("itemName", out var iE) ? iE.GetString() : null;
            int chestX = cmd.TryGetProperty("chestX", out var cxE) ? cxE.GetInt32() : -1;
            int chestY = cmd.TryGetProperty("chestY", out var cyE) ? cyE.GetInt32() : -1;

            if (string.IsNullOrEmpty(itemName) || chestX < 0 || chestY < 0) return;

            var player = Game1.player;
            var location = Game1.currentLocation;
            var tilePos = new Vector2(chestX, chestY);

            if (!location.Objects.TryGetValue(tilePos, out var obj) || obj is not StardewValley.Objects.Chest chest)
            {
                Monitor.Log($"No chest found at ({chestX},{chestY})", LogLevel.Warn);
                return;
            }

            if (chest.Items == null) return;

            for (int i = 0; i < chest.Items.Count; i++)
            {
                var item = chest.Items[i];
                if (item != null && item.DisplayName.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                {
                    if (player.addItemToInventoryBool(item))
                    {
                        chest.Items[i] = null;
                        Monitor.Log($"Withdrew {itemName} from chest at ({chestX},{chestY})", LogLevel.Debug);
                    }
                    else
                    {
                        Monitor.Log("Player inventory full", LogLevel.Warn);
                    }
                    return;
                }
            }

            Monitor.Log($"Item '{itemName}' not found in chest", LogLevel.Warn);
        }

        // ─── Forage Command (Phase 3 - Agency) ───
        private void HandleForage(JsonElement cmd)
        {
            if (!Context.IsWorldReady || Game1.player == null || Game1.currentLocation == null) return;

            var player = Game1.player;
            var location = Game1.currentLocation;
            int radius = cmd.TryGetProperty("radius", out var rE) ? Math.Max(1, rE.GetInt32()) : 5;

            int tx = (int)(player.Position.X / Game1.tileSize);
            int ty = (int)(player.Position.Y / Game1.tileSize);

            int pickedUp = 0;

            // Scan a square area
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int cx = tx + dx;
                    int cy = ty + dy;
                    var tilePos = new Vector2(cx, cy);

                    // Try picking up objects
                    if (location.Objects.TryGetValue(tilePos, out var obj) && obj != null)
                    {
                        if (player.couldInventoryAcceptThisItem(obj))
                        {
                            location.Objects.Remove(tilePos);
                            player.addItemToInventory(obj);
                            pickedUp++;
                        }
                    }

                    // Try picking up debris (forage)
                    var debrisField = typeof(GameLocation).GetField("debris",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var debrisList = debrisField?.GetValue(location) as System.Collections.IList;

                    if (debrisList != null)
                    {
                        for (int i = debrisList.Count - 1; i >= 0; i--)
                        {
                            var debris = debrisList[i];
                            if (debris == null) continue;

                            var debrisType = debris.GetType();
                            var posProp = debrisType.GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                         ?? debrisType.GetProperty("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (posProp == null) continue;

                            var pos = posProp.GetValue(debris) as Vector2?;
                            if (!pos.HasValue) continue;

                            int dtx = (int)(pos.Value.X / Game1.tileSize);
                            int dty = (int)(pos.Value.Y / Game1.tileSize);

                            if (Math.Abs(dtx - cx) <= 1 && Math.Abs(dty - cy) <= 1)
                            {
                                var itemField = debrisType.GetField("item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                var debrisItem = itemField?.GetValue(debris) as Item;

                                if (debrisItem != null && player.couldInventoryAcceptThisItem(debrisItem))
                                {
                                    player.addItemToInventory(debrisItem);
                                    itemField.SetValue(debris, null);
                                    debrisList.RemoveAt(i);
                                    pickedUp++;
                                }
                            }
                        }
                    }
                }
            }

            if (pickedUp > 0)
                Monitor.Log($"Foraged {pickedUp} items in radius {radius}", LogLevel.Debug);
            else
                Monitor.Log("Nothing to forage nearby", LogLevel.Trace);
        }

        // ─── Autonomy Layer (Phase 2 - Token Light) ───
        private void HandleAutonomousWater(JsonElement cmd)
        {
            // High-level command: "just water whatever is nearby"
            // For now, re-use the existing water logic but in a wider area
            if (!Context.IsWorldReady || Game1.player == null) return;

            var player = Game1.player;
            var location = Game1.currentLocation;

            int watered = 0;
            int px = (int)(player.Position.X / Game1.tileSize);
            int py = (int)(player.Position.Y / Game1.tileSize);

            for (int dx = -6; dx <= 6; dx++)
            {
                for (int dy = -6; dy <= 6; dy++)
                {
                    int tx = px + dx;
                    int ty = py + dy;
                    var tilePos = new Vector2(tx, ty);

                    if (location.terrainFeatures.TryGetValue(tilePos, out var feature) &&
                        feature is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.state.Value == 0)
                    {
                        dirt.state.Value = 1;
                        watered++;
                    }
                }
            }

            Monitor.Log($"[Autonomy] Auto-watered {watered} tiles", LogLevel.Debug);
        }

        private void HandleStartAutonomy(JsonElement cmd)
        {
            // The real autonomy logic will live in Python.
            // The mod just sets a flag so the state reflects "I'm in autonomous mode".
            _autonomyMode = true;
            Monitor.Log("[Autonomy] Autonomous mode enabled (Python layer will drive decisions)", LogLevel.Debug);
        }

        private void HandleStopAutonomy(JsonElement cmd)
        {
            _autonomyMode = false;
            Monitor.Log("[Autonomy] Autonomous mode disabled", LogLevel.Debug);
        }

        // Simple flag the Python side can read
        private bool _autonomyMode = false;

        // ─── Dialogue Polling (every 2s) ───
        private async Task PollDialogueLoop(CancellationToken ct)
        {
            // Disabled: dialogue polling was causing HUD spam
            // Re-enable once dialogue system is properly throttled
            await Task.CompletedTask;
        }

        private void ProcessDialogue()
        {
            try
            {
                if (!File.Exists(DialoguePath) || !Context.IsWorldReady) return;
                string raw = File.ReadAllText(DialoguePath);
                if (string.IsNullOrWhiteSpace(raw)) return;

                var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("text", out var textElem)) return;
                string text = textElem.GetString()?.Trim() ?? "";
                if (string.IsNullOrEmpty(text)) return; // Don't show blank messages

                // Clear the file FIRST so we don't re-trigger
                File.WriteAllText(DialoguePath, "{\"text\":\"\"}");

                // Show as HUD message
                Game1.addHUDMessage(new HUDMessage(text, 3));
                Monitor.Log($"Bridge dialogue: {text}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Monitor.Log($"ProcessDialogue error: {ex.Message}", LogLevel.Warn);
            }
        }

        // ─── State Reporting (every 10s) ───
        private async Task WriteStateLoop(CancellationToken ct)
        {
            await Task.Delay(5000, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    WriteState();
                }
                catch (Exception ex) { Monitor.Log($"State write error: {ex.Message}", LogLevel.Warn); }
                try { await Task.Delay(10000, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        private void WriteState()
        {
            if (!Context.IsWorldReady || Game1.player == null) return;

            try
            {
                var player = Game1.player;
                var location = Game1.currentLocation;

                // Build inventory list
                var inventory = new List<string>();
                if (player.Items != null)
                {
                    foreach (var item in player.Items)
                    {
                        if (item != null)
                            inventory.Add(item.DisplayName);
                    }
                }

                // Find nearby players
                var nearbyPlayers = new List<object>();
                if (location?.farmers != null)
                {
                    foreach (var farmer in location.farmers)
                    {
                        if (farmer != null && farmer != player)
                        {
                            nearbyPlayers.Add(new
                            {
                                name = farmer.Name,
                                tileX = (int)(farmer.Position.X / Game1.tileSize),
                                tileY = (int)(farmer.Position.Y / Game1.tileSize),
                                location = farmer.currentLocation?.Name ?? "Unknown"
                            });
                        }
                    }
                }

                // Determine weather
                string weather = "Sunny";
                if (Game1.isRaining) weather = "Rainy";
                else if (Game1.isLightning) weather = "Stormy";
                else if (Game1.isSnowing) weather = "Snowy";
                else if (Game1.weatherIcon == 12) weather = "Windy";

                // ── Phase 1: Crop/farm scanning (unwatered HoeDirt + IndoorPots) ──
                // Lets Lyra know WHERE crops need water, not just "near me"
                var unwateredCrops = new List<object>();
                var exits = new List<object>();
                try
                {
                    if (location != null)
                    {
                        int scanLimit = 25;
                        var playerTile = player.Position / Game1.tileSize;

                        // Scan terrainFeatures (most crops)
                        foreach (var kv in location.terrainFeatures.Pairs)
                        {
                            if (kv.Value is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.state.Value == 0)
                            {
                                int cx = (int)kv.Key.X;
                                int cy = (int)kv.Key.Y;
                                unwateredCrops.Add(new { x = cx, y = cy });
                                if (unwateredCrops.Count >= scanLimit) break;
                            }
                        }

                        // Also scan IndoorPots (greenhouse, sheds, etc.)
                        if (unwateredCrops.Count < scanLimit)
                        {
                            foreach (var kv in location.Objects.Pairs)
                            {
                                if (kv.Value is StardewValley.Objects.IndoorPot pot &&
                                    pot.hoeDirt.Value != null && pot.hoeDirt.Value.state.Value == 0)
                                {
                                    int cx = (int)kv.Key.X;
                                    int cy = (int)kv.Key.Y;
                                    unwateredCrops.Add(new { x = cx, y = cy });
                                    if (unwateredCrops.Count >= scanLimit) break;
                                }
                            }
                        }

                        // ── Basic area transition support: report exits/warps (Phase 1 foundation) ──
                        // AI can path to an exit tile then issue move/warp or use edge detection
                        try
                        {
                            if (location.warps != null)
                            {
                                var freshExits = new List<dynamic>();
                                foreach (var w in location.warps)
                                {
                                    var exitInfo = new
                                    {
                                        x = w.X,
                                        y = w.Y,
                                        targetLocation = w.TargetName,
                                        targetX = w.TargetX,
                                        targetY = w.TargetY
                                    };
                                    freshExits.Add(exitInfo);
                                    exits.Add(exitInfo);
                                    if (exits.Count >= 8) break;
                                }

                                // Cache for use in movement logic (so we can act on exits without waiting for next state write)
                                if (freshExits.Count > 0)
                                {
                                    string locName = location.Name ?? "";
                                    _exitsByLocation[locName] = freshExits;
                                    _cachedExits = freshExits;
                                    _lastLocationWithExits = locName;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception scanEx)
                {
                    Monitor.Log($"Crop/exit scan error: {scanEx.Message}", LogLevel.Trace);
                }

                var state = new
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    lyra = new
                    {
                        tileX = (int)(player.Position.X / Game1.tileSize),
                        tileY = (int)(player.Position.Y / Game1.tileSize),
                        location = location?.Name ?? "Unknown",
                        health = player.health,
                        stamina = player.Stamina,
                        maxStamina = player.MaxStamina,
                        inventory,
                        money = player.Money,
                        following = _following
                    },
                    nearbyPlayers,
                    world = new
                    {
                        day = Game1.dayOfMonth,
                        season = Game1.Date.Season.ToString(),
                        year = Game1.year,
                        time = Game1.timeOfDay,
                        weather
                    },
                    farm = new
                    {
                        unwateredCrops,
                        exits,  // warp/exit points for area transitions (Phase 1)
                        scannedLocation = location?.Name ?? "Unknown",
                        cropCount = unwateredCrops.Count,
                        exitCount = exits.Count,
                        // New transition awareness fields (Phase 1.5)
                        nearestExit = _cachedExits.Count > 0 ? _cachedExits[0] : null,
                        isNearMapEdge = _cachedExits.Count > 0 && IsNearAnyExit(player.Position / Game1.tileSize),
                        distanceToNearestExit = _cachedExits.Count > 0 ? CalculateDistanceToNearestExit(player.Position / Game1.tileSize) : -1,
                        transitionStatus = _transitionCooldown > 0 ? "on_cooldown" : (_cachedExits.Count > 0 ? "exits_available" : "no_exits"),
                        isStuckOnTransition = _isAttemptingTransition && _ticksWithoutTransitionProgress > 20,
                        transitionStuckTicks = _ticksWithoutTransitionProgress,
                        // New: Very lightweight chest info for agency (minimal token impact)
                        nearbyChests = GetNearbyChestsSummary(location, player.Position / Game1.tileSize),
                        // New: Lightweight foraging info (for agency with minimal tokens)
                        forage = GetForageSummary(location, player.Position / Game1.tileSize),
                        // Autonomy opportunities (very lightweight for token efficiency)
                        autonomy = GetAutonomyOpportunities(location, player.Position / Game1.tileSize, player)
                    }
                };

                string json = JsonSerializer.Serialize(state, JsonOpts);
                AtomicWriteFile(StatePath, json);
            }
            catch (Exception ex)
            {
                Monitor.Log($"WriteState error: {ex.Message}", LogLevel.Warn);
            }
        }

        // ─── Update Tick: Movement + Follow + Idle Behavior ───
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player == null || !e.IsMultipleOf(4))
                return;

            var player = Game1.player;

            // ── Follow behavior ──
            if (_following != null)
            {
                Farmer target = null;

                // Search all locations for the target farmer
                foreach (var loc in Game1.locations)
                {
                    if (loc?.farmers != null)
                    {
                        foreach (var farmer in loc.farmers)
                        {
                            if (farmer != null && farmer.Name?.Equals(_following, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                target = farmer;
                                break;
                            }
                        }
                    }
                    if (target != null) break;
                }

                if (target != null && target.currentLocation != null)
                {
                    // Move to target's location if different
                    if (player.currentLocation != target.currentLocation)
                    {
                        string targetLocName = target.currentLocation.Name ?? "";

                        // Mark that we are now attempting a cross-location transition
                        _isAttemptingTransition = true;
                        _lastTransitionPosition = player.Position / Game1.tileSize;
                        _ticksWithoutTransitionProgress = 0;

                        // Strongly prefer exit pathing over warping
                        bool usedExitPathing = TryPathTowardExitForLocation(targetLocName);

                        // If no perfect match was found, try the closest exit as a last non-warp attempt
                        if (!usedExitPathing && _cachedExits.Count > 0)
                        {
                            usedExitPathing = TryPathTowardClosestExit();
                        }

                        if (!usedExitPathing)
                        {
                            // Last resort warp (we really tried to avoid this)
                            Monitor.Log($"[FOLLOW] All exit pathing attempts failed - LAST RESORT warpFarmer to {targetLocName} (desync risk)", LogLevel.Warn);
                            try
                            {
                                Game1.warpFarmer(
                                    targetLocName,
                                    (int)(target.Position.X / Game1.tileSize),
                                    (int)(target.Position.Y / Game1.tileSize),
                                    false
                                );
                                player.movementDirections?.Clear();
                                player.controller = null;
                                _isMoving = false;
                                _pathCooldown = 8;
                                _isAttemptingTransition = false;
                            }
                            catch (Exception warpEx)
                            {
                                Monitor.Log($"Follow warp failed: {warpEx.Message}", LogLevel.Warn);
                            }
                        }
                        else
                        {
                            Monitor.Log($"[FOLLOW] Using exit pathing to reach {targetLocName}", LogLevel.Debug);
                        }
                    }

                    // Move toward target
                    var targetTile = new Vector2(
                        target.Position.X / Game1.tileSize,
                        target.Position.Y / Game1.tileSize
                    );
                    var currentTile = player.Position / Game1.tileSize;
                    float dist = Vector2.Distance(currentTile, targetTile);

                    if (dist > 1.5f)
                    {
                        _moveTarget = targetTile;
                        _isMoving = true;
                        _pathFailed = false;

                        // Use pathfinding for follow movement
                        int followX = (int)targetTile.X + (targetTile.X > currentTile.X ? -1 : targetTile.X < currentTile.X ? 1 : 0);
                        int followY = (int)targetTile.Y;
                        TrySetPath(new Point(followX, followY));
                    }
                    else
                    {
                        _isMoving = false;
                        player.movementDirections.Clear();
                        player.controller = null;
                        _isAttemptingTransition = false; // successfully reached target area
                        // Natural: face the player when standing nearby (personality)
                        try
                        {
                            int faceDir = GetDirectionTo(player, target);
                            player.faceDirection(faceDir);
                        }
                        catch { }
                        // Occasional friendly emote when close and idle
                        if ((DateTime.UtcNow - _lastPersonalityEmote).TotalSeconds > 45 && _rand.Next(3) == 0)
                        {
                            Game1.player.doEmote(20); // happy
                            _lastPersonalityEmote = DateTime.UtcNow;
                        }
                    }
                }
                else
                {
                    // Target not found - stop following
                    Monitor.Log($"Follow target '{_following}' not found, stopping", LogLevel.Warn);
                    _following = null;
                    _isMoving = false;
                    player.movementDirections?.Clear();
                }
            }

            // ── Movement (follow or explicit move command) + natural personality ──
            if (_isMoving)
            {
                // Phase 1.5: Proactive edge detection — if we're moving and getting close to map edge, consider switching to exit pathing
                if (_transitionCooldown == 0 && _cachedExits.Count > 0)
                {
                    Vector2 currentTile = player.Position / Game1.tileSize;
                    var location = Game1.currentLocation;
                    if (location?.map != null)
                    {
                        int w = location.map.Layers[0].LayerWidth;
                        int h = location.map.Layers[0].LayerHeight;

                        bool nearEdge = currentTile.X < 3 || currentTile.Y < 3 || currentTile.X > w - 4 || currentTile.Y > h - 4;

                        if (nearEdge)
                        {
                            // Mark transition attempt for stuck detection
                            if (!_isAttemptingTransition)
                            {
                                _isAttemptingTransition = true;
                                _lastTransitionPosition = currentTile;
                                _ticksWithoutTransitionProgress = 0;
                            }

                            // Try to switch to a good exit instead of walking into the void
                            if (TryPathTowardExitForLocation(""))
                            {
                                Monitor.Log("[TRANSITION] Detected near map edge while moving — switched to exit pathing", LogLevel.Debug);
                            }
                        }
                    }
                }

                // Natural pause/hesitation (Phase 1 personality): occasionally stop for a few ticks
                if (_pauseTicks > 0)
                {
                    _pauseTicks--;
                    player.movementDirections.Clear();
                    player.controller = null;
                }
                else
                {
                    Vector2 currentTile = player.Position / Game1.tileSize;
                    float dist = Vector2.Distance(currentTile, _moveTarget);

                    if (dist < 0.5f)
                    {
                        // Arrived at destination
                        _isMoving = false;
                        player.movementDirections.Clear();
                        player.controller = null;
                        // Face "forward" or last direction naturally
                        if (_rand.Next(4) == 0) player.faceDirection(_rand.Next(4));
                    }
                    else if (player.controller == null && !_pathFailed)
                    {
                        // PathFindController completed or was cleared — retry pathfinding
                        TrySetPath(new Point((int)_moveTarget.X, (int)_moveTarget.Y));
                    }
                    else if (_pathFailed && player.controller == null)
                    {
                        // Fallback: straight-line movement when pathfinding fails
                        Vector2 dir = _moveTarget - currentTile;
                        float dx = dir.X;
                        float dy = dir.Y;

                        player.movementDirections.Clear();
                        if (dx > 0.3f) player.movementDirections.Add(1);
                        else if (dx < -0.3f) player.movementDirections.Add(3);
                        if (dy > 0.3f) player.movementDirections.Add(2);
                        else if (dy < -0.3f) player.movementDirections.Add(0);

                        // Occasional micro-pause while traveling (makes movement feel less robotic)
                        if (_rand.Next(100) < 4) // ~4% chance per update tick
                        {
                            _pauseTicks = _rand.Next(6, 14); // 6-14 ticks pause (~0.4-0.9s)
                        }
                    }
                }
            }

            // Decrement pathfinding cooldown
            if (_pathCooldown > 0) _pathCooldown--;

            // Decrement transition cooldown (prevents spamming exit pathing)
            if (_transitionCooldown > 0) _transitionCooldown--;

            // Run stuck detection for transitions (crucial for reliability)
            UpdateTransitionStuckDetection();

            // ── Idle behavior: random emote every 60 seconds ──
            if (!_isMoving && _following == null)
            {
                if ((DateTime.UtcNow - _lastIdleEmote).TotalSeconds >= 60)
                {
                    RandomEmote();
                    _lastIdleEmote = DateTime.UtcNow;
                }
            }

            // ── Reactive emotes (Phase 1): react to players entering/leaving area ──
            try
            {
                var currentNearby = new HashSet<string>();
                if (Game1.currentLocation?.farmers != null)
                {
                    foreach (var f in Game1.currentLocation.farmers)
                    {
                        if (f != null && f != Game1.player && !string.IsNullOrEmpty(f.Name))
                            currentNearby.Add(f.Name);
                    }
                }

                // New player(s) arrived
                foreach (var name in currentNearby)
                {
                    if (!_prevNearbyNames.Contains(name))
                    {
                        // Friendly reactive emote on join (heart or happy)
                        if (Game1.player != null)
                        {
                            int em = (name.Contains("Raphtalia", StringComparison.OrdinalIgnoreCase) || _rand.Next(2)==0) ? 0 : 20;
                            Game1.player.doEmote(em);
                            _lastPersonalityEmote = DateTime.UtcNow;
                        }
                        Monitor.Log($"[REACTIVE] {name} entered area — emote", LogLevel.Debug);
                    }
                }

                // Player left (optional sad emote, low freq)
                foreach (var old in _prevNearbyNames)
                {
                    if (!currentNearby.Contains(old) && _rand.Next(4) == 0)
                    {
                        if (Game1.player != null) Game1.player.doEmote(2); // sad
                        Monitor.Log($"[REACTIVE] {old} left area", LogLevel.Debug);
                    }
                }

                _prevNearbyNames.Clear();
                foreach (var n in currentNearby) _prevNearbyNames.Add(n);
            }
            catch { }

            // ── Chat capture on update tick ──
            CaptureChatMessages();
        }

        private void RandomEmote()
        {
            if (Game1.player == null) return;
            Random rnd = new Random();
            // Common emote IDs: 0=heart, 1=angry, 2=sad, 4=exclamation, 6=question, 8=music note, 16=blush, 20=happy, 24=faint
            int[] emotes = { 0, 4, 8, 16, 20, 32 };
            int emote = emotes[rnd.Next(emotes.Length)];
            Game1.player.doEmote(emote);
        }

        /// <summary>
        /// Compute facing direction (0=up,1=right,2=down,3=left) from player toward target.
        /// Used for natural "face the player" behavior.
        /// </summary>
        private int GetDirectionTo(Farmer from, Farmer to)
        {
            if (from == null || to == null) return 2;
            Vector2 delta = to.Position - from.Position;
            if (Math.Abs(delta.X) > Math.Abs(delta.Y))
                return delta.X > 0 ? 1 : 3;
            return delta.Y > 0 ? 2 : 0;
        }

        /// <summary>
        /// Phase 1.5 Area Transitions: Attempts to path toward a good exit that leads toward the target location.
        /// Returns true if it successfully started pathing toward an exit (or handled being near one).
        /// </summary>
        private bool TryPathTowardExitForLocation(string targetLocationName)
        {
            if (_transitionCooldown > 0) return false; // Respect cooldown to avoid spam

            if (string.IsNullOrEmpty(targetLocationName)) return false;

            // Prefer exits from the dictionary for the current location if available
            var location = Game1.currentLocation;
            string currentLoc = location?.Name ?? "";
            if (_exitsByLocation.ContainsKey(currentLoc) && _exitsByLocation[currentLoc].Count > 0)
            {
                _cachedExits = _exitsByLocation[currentLoc];
            }

            if (_cachedExits == null || _cachedExits.Count == 0) return false;

            var player = Game1.player;
            if (player == null) return false;

            dynamic? bestExit = null;
            double bestScore = double.MaxValue;

            Vector2 currentTile = player.Position / Game1.tileSize;

            foreach (var ex in _cachedExits)
            {
                string targetLoc = ex.targetLocation?.ToString() ?? "";
                int exX = (int)ex.x;
                int exY = (int)ex.y;

                double distance = Math.Sqrt(Math.Pow(currentTile.X - exX, 2) + Math.Pow(currentTile.Y - exY, 2));

                // Strongly prefer exits that match the exact target location
                double score = distance;
                if (!string.IsNullOrEmpty(targetLoc) && targetLoc.Equals(targetLocationName, StringComparison.OrdinalIgnoreCase))
                {
                    score = distance * 0.25; // very strong preference for correct destination
                }
                else if (distance < 3.0)
                {
                    // Any exit we're already close to is attractive as a fallback
                    score = distance * 0.6;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestExit = ex;
                }
            }

            if (bestExit != null)
            {
                int tx = (int)bestExit.x;
                int ty = (int)bestExit.y;
                string exitTarget = bestExit.targetLocation?.ToString() ?? "unknown";

                float distToExit = Vector2.Distance(currentTile, new Vector2(tx, ty));

                // If we're very close to a good exit, stop and let the game naturally transition
                if (distToExit < 1.8f)
                {
                    Monitor.Log($"[TRANSITION] Close to exit ({tx},{ty}) → {exitTarget}. Releasing control for natural transition.", LogLevel.Debug);
                    _isMoving = false;
                    player.movementDirections?.Clear();
                    player.controller = null;

                    // Gentle nudge in the direction of the exit to help trigger the transition
                    if (distToExit > 0.6f)
                    {
                        Vector2 dir = new Vector2(tx, ty) - currentTile;
                        player.movementDirections.Clear();
                        if (Math.Abs(dir.X) > Math.Abs(dir.Y))
                            player.movementDirections.Add(dir.X > 0 ? 1 : 3);
                        else
                            player.movementDirections.Add(dir.Y > 0 ? 2 : 0);
                    }

                    _transitionCooldown = 12; // short cooldown after handling near-exit
                    return true;
                }

                // Otherwise, path toward the exit
                _moveTarget = new Vector2(tx, ty);
                _isMoving = true;
                _pathFailed = false;
                TrySetPath(new Point(tx, ty));

                Monitor.Log($"[TRANSITION] Pathing toward exit ({tx},{ty}) → {exitTarget} (score {bestScore:F1})", LogLevel.Debug);
                _transitionCooldown = 8; // brief cooldown after starting exit pathing
                return true;
            }

            return false;
        }

        /// <summary>
        /// Phase 1.5 Hardening: Path toward the single closest exit, regardless of destination.
        /// Used as a last-ditch attempt before falling back to warpFarmer.
        /// </summary>
        private bool TryPathTowardClosestExit()
        {
            if (_transitionCooldown > 0) return false;
            if (_cachedExits == null || _cachedExits.Count == 0) return false;

            var player = Game1.player;
            if (player == null) return false;

            Vector2 currentTile = player.Position / Game1.tileSize;
            dynamic? closest = null;
            double closestDist = double.MaxValue;

            foreach (var ex in _cachedExits)
            {
                int exX = (int)ex.x;
                int exY = (int)ex.y;
                double dist = Math.Sqrt(Math.Pow(currentTile.X - exX, 2) + Math.Pow(currentTile.Y - exY, 2));

                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = ex;
                }
            }

            if (closest != null)
            {
                int tx = (int)closest.x;
                int ty = (int)closest.y;

                // If already very close, help the transition instead of pathing
                if (closestDist < 2.0f)
                {
                    Monitor.Log($"[TRANSITION] Very close to nearest exit ({tx},{ty}) → {closest.targetLocation}. Helping cross.", LogLevel.Debug);
                    _isMoving = false;
                    player.movementDirections?.Clear();
                    player.controller = null;

                    // Stronger nudge toward the exit
                    Vector2 dir = new Vector2(tx, ty) - currentTile;
                    if (Math.Abs(dir.X) > Math.Abs(dir.Y))
                        player.movementDirections.Add(dir.X > 0 ? 1 : 3);
                    else
                        player.movementDirections.Add(dir.Y > 0 ? 2 : 0);

                    _transitionCooldown = 10;
                    return true;
                }

                _moveTarget = new Vector2(tx, ty);
                _isMoving = true;
                _pathFailed = false;
                TrySetPath(new Point(tx, ty));

                Monitor.Log($"[TRANSITION] Falling back to closest exit at ({tx},{ty}) → {closest.targetLocation}", LogLevel.Debug);
                _transitionCooldown = 6;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Tracks whether we are making progress toward an exit/transition.
        /// Called regularly when we are in "trying to cross" mode.
        /// </summary>
        private void UpdateTransitionStuckDetection()
        {
            if (!_isAttemptingTransition) return;

            var player = Game1.player;
            if (player == null) return;

            Vector2 currentTile = player.Position / Game1.tileSize;

            float distanceMoved = Vector2.Distance(currentTile, _lastTransitionPosition);

            if (distanceMoved > 0.8f) // made decent progress
            {
                _ticksWithoutTransitionProgress = 0;
                _lastTransitionPosition = currentTile;
            }
            else
            {
                _ticksWithoutTransitionProgress++;
            }

            // Stuck detection triggered
            if (_ticksWithoutTransitionProgress >= STUCK_THRESHOLD_TICKS)
            {
                Monitor.Log($"[STUCK] No meaningful progress toward exit for {STUCK_THRESHOLD_TICKS / 15f:F1}s. Triggering recovery.", LogLevel.Warn);

                HandleStuckTransitionRecovery();
                _ticksWithoutTransitionProgress = 0; // reset after recovery attempt
            }
        }

        /// <summary>
        /// Recovery behavior when stuck trying to reach an exit while following or moving.
        /// </summary>
        private void HandleStuckTransitionRecovery()
        {
            var player = Game1.player;
            if (player == null) return;

            // Strategy 1: If we have multiple exits, try switching to a completely different one
            if (_cachedExits.Count >= 2)
            {
                // Pick a different exit than the current target
                Vector2 currentTarget = _moveTarget;

                dynamic? alternative = null;
                foreach (var ex in _cachedExits)
                {
                    int exX = (int)ex.x;
                    int exY = (int)ex.y;
                    if (Math.Abs(exX - currentTarget.X) > 1 || Math.Abs(exY - currentTarget.Y) > 1)
                    {
                        alternative = ex;
                        break;
                    }
                }

                if (alternative != null)
                {
                    int tx = (int)alternative.x;
                    int ty = (int)alternative.y;
                    _moveTarget = new Vector2(tx, ty);
                    TrySetPath(new Point(tx, ty));
                    Monitor.Log($"[STUCK RECOVERY] Switching to alternative exit ({tx},{ty})", LogLevel.Warn);
                    _transitionCooldown = 4;
                    return;
                }
            }

            // Strategy 2: Clear everything and try the absolute closest exit with more aggressive behavior
            if (_cachedExits.Count > 0)
            {
                TryPathTowardClosestExit();
                _transitionCooldown = 3;
                Monitor.Log("[STUCK RECOVERY] Forcing closest exit pathing with fresh attempt", LogLevel.Warn);
                return;
            }

            // Strategy 3: Last resort - if truly stuck with no exits, clear state and let higher level decide
            Monitor.Log("[STUCK RECOVERY] No viable exits found. Clearing movement. AI should issue new commands or allow warp.", LogLevel.Warn);
            _isMoving = false;
            player.movementDirections?.Clear();
            player.controller = null;
            _isAttemptingTransition = false;
        }

        /// <summary>
        /// Returns true if the player is close enough to any cached exit that a transition is likely.
        /// </summary>
        private bool IsNearAnyExit(Vector2 playerTile)
        {
            if (_cachedExits == null || _cachedExits.Count == 0) return false;

            foreach (var ex in _cachedExits)
            {
                int exX = (int)ex.x;
                int exY = (int)ex.y;
                float dist = Vector2.Distance(playerTile, new Vector2(exX, exY));
                if (dist <= 2.5f) // within ~2.5 tiles of an exit
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the distance (in tiles) to the nearest cached exit, or -1 if none available.
        /// </summary>
        private double CalculateDistanceToNearestExit(Vector2 playerTile)
        {
            if (_cachedExits == null || _cachedExits.Count == 0) return -1;

            double minDist = double.MaxValue;

            foreach (var ex in _cachedExits)
            {
                int exX = (int)ex.x;
                int exY = (int)ex.y;
                double dist = Math.Sqrt(Math.Pow(playerTile.X - exX, 2) + Math.Pow(playerTile.Y - exY, 2));
                if (dist < minDist) minDist = dist;
            }

            return minDist;
        }

        /// <summary>
        /// Returns very lightweight summary of nearby chests for agency (designed for token efficiency).
        /// Only reports position + high-level category. Full details go through Python summarizer.
        /// </summary>
        private List<object> GetNearbyChestsSummary(GameLocation location, Vector2 playerTile)
        {
            var result = new List<object>();
            if (location?.Objects == null) return result;

            try
            {
                foreach (var kv in location.Objects.Pairs)
                {
                    if (kv.Value is StardewValley.Objects.Chest chest)
                    {
                        int cx = (int)kv.Key.X;
                        int cy = (int)kv.Key.Y;
                        float dist = Vector2.Distance(playerTile, new Vector2(cx, cy));

                        if (dist <= 8) // Only nearby chests
                        {
                            // Very high-level categorization to keep tokens low
                            string summary = "mixed";
                            int itemCount = chest.Items?.Count ?? 0;

                            if (itemCount == 0) summary = "empty";
                            else
                            {
                                bool hasTools = false, hasCrops = false, hasSeeds = false;
                                foreach (var item in chest.Items)
                                {
                                    if (item == null) continue;
                                    string name = item.DisplayName.ToLower();
                                    if (name.Contains("axe") || name.Contains("hoe") || name.Contains("pickaxe") || name.Contains("watering"))
                                        hasTools = true;
                                    if (name.Contains("parsnip") || name.Contains("potato") || name.Contains("cauliflower") || name.Contains("crop"))
                                        hasCrops = true;
                                    if (name.Contains("seed"))
                                        hasSeeds = true;
                                }
                                if (hasTools) summary = "tools";
                                else if (hasSeeds) summary = "seeds";
                                else if (hasCrops) summary = "crops";
                            }

                            result.Add(new
                            {
                                x = cx,
                                y = cy,
                                distance = Math.Round(dist, 1),
                                summary
                            });
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// Returns very lightweight foraging summary for agency (designed for token efficiency).
        /// Only count + rough type. No full lists of positions.
        /// </summary>
        private object GetForageSummary(GameLocation location, Vector2 playerTile)
        {
            if (location == null) return new { count = 0, summary = "none" };

            int count = 0;
            var types = new HashSet<string>();

            try
            {
                // Check ground objects (some forageables appear as objects)
                foreach (var kv in location.Objects.Pairs)
                {
                    var obj = kv.Value;
                    if (obj == null) continue;

                    float dist = Vector2.Distance(playerTile, kv.Key);
                    if (dist > 6) continue; // small radius for token reasons

                    // Forageables are often not tools/weapons and can be picked up
                    if (!(obj is Tool) && !(obj is StardewValley.Objects.Chest) && !(obj is StardewValley.Objects.IndoorPot))
                    {
                        count++;
                        string name = obj.DisplayName?.ToLower() ?? "unknown";
                        if (name.Contains("daffodil") || name.Contains("dandelion")) types.Add("flowers");
                        else if (name.Contains("leek") || name.Contains("horseradish") || name.Contains("spring onion")) types.Add("spring veggies");
                        else if (name.Contains("morel") || name.Contains("mushroom")) types.Add("mushrooms");
                        else types.Add("misc");
                    }
                }

                // Check debris (main source of forage)
                var debrisField = typeof(GameLocation).GetField("debris",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var debrisList = debrisField?.GetValue(location) as System.Collections.IList;

                if (debrisList != null)
                {
                    foreach (var d in debrisList)
                    {
                        if (d == null) continue;

                        var debrisType = d.GetType();
                        var posProp = debrisType.GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                     ?? debrisType.GetProperty("position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (posProp == null) continue;

                        var pos = posProp.GetValue(d) as Vector2?;
                        if (!pos.HasValue) continue;

                        float dist = Vector2.Distance(playerTile, pos.Value / Game1.tileSize);
                        if (dist > 6) continue;

                        var itemField = debrisType.GetField("item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var item = itemField?.GetValue(d) as Item;
                        if (item != null)
                        {
                            count++;
                            string name = item.DisplayName?.ToLower() ?? "forage";
                            if (name.Contains("daffodil") || name.Contains("dandelion")) types.Add("flowers");
                            else if (name.Contains("leek") || name.Contains("horseradish") || name.Contains("spring onion")) types.Add("spring veggies");
                            else if (name.Contains("morel") || name.Contains("mushroom")) types.Add("mushrooms");
                            else if (name.Contains("wild")) types.Add("wild");
                            else types.Add("misc");
                        }
                    }
                }
            }
            catch { }

            string summary = types.Count > 0 ? string.Join(", ", types) : "none";

            return new
            {
                count,
                summary = count > 0 ? $"{count} forageables ({summary})" : "none nearby"
            };
        }

        /// <summary>
        /// Returns very lightweight autonomy opportunities.
        /// Designed to be tiny in state.json — real decision logic lives in Python.
        /// </summary>
        private object GetAutonomyOpportunities(GameLocation location, Vector2 playerTile, Farmer player)
        {
            var opportunities = new List<string>();

            // Watering opportunity (reuses existing crop scan data)
            int unwateredNearby = 0;
            try
            {
                if (location != null)
                {
                    foreach (var kv in location.terrainFeatures.Pairs)
                    {
                        if (kv.Value is StardewValley.TerrainFeatures.HoeDirt dirt && dirt.state.Value == 0)
                        {
                            float dist = Vector2.Distance(playerTile, kv.Key);
                            if (dist <= 8) unwateredNearby++;
                        }
                    }
                }
            }
            catch { }

            if (unwateredNearby >= 3)
                opportunities.Add("water_crops");

            // Foraging opportunity (reuses the forage summary we already compute)
            // We can check the existing forage data, but for simplicity we approximate here
            if (opportunities.Count < 3) // keep it small
            {
                // Quick debris scan
                try
                {
                    var debrisField = typeof(GameLocation).GetField("debris",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var debrisList = debrisField?.GetValue(location) as System.Collections.IList;
                    int forageCount = 0;

                    if (debrisList != null)
                    {
                        foreach (var d in debrisList)
                        {
                            // Simplified check — real logic is in GetForageSummary
                            forageCount++;
                            if (forageCount >= 4) break;
                        }
                    }
                    if (forageCount >= 3)
                        opportunities.Add("forage");
                }
                catch { }
            }

            // Chest organization opportunity (very rough)
            if (player.Items != null)
            {
                int itemCount = player.Items.Count(i => i != null);
                if (itemCount > 8)
                    opportunities.Add("organize_inventory");
            }

            string primary = opportunities.Count > 0 ? opportunities[0] : "idle";

            return new
            {
                opportunities,
                primary_suggestion = primary,
                idle_level = opportunities.Count == 0 ? "high" : "medium",
                autonomy_mode = _autonomyMode
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _multiHitCts?.Cancel();
                _multiHitCts?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
