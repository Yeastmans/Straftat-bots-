using System.Collections.Generic;
using System.Reflection;
using FishNet;
using FishNet.Object;
using UnityEngine;

namespace StraftatBots
{
    public class BotManager : MonoBehaviour
    {
        public static BotManager Instance { get; private set; }

        public List<BotData> LobbyBots = new List<BotData>();
        // GameObject instance IDs of every bot object ever spawned this session — filled
        // at Instantiate time, before any component or network state exists.
        internal static readonly HashSet<int> BotObjectIds = new HashSet<int>();
        internal static bool IsBotObject(GameObject go) => go != null && BotObjectIds.Contains(go.GetInstanceID());
        private List<BotController> _activeBots = new List<BotController>();
        private int _nextBotId;
        private GameObject _cachedPrefab;
        private float _onlyBotsAliveTimer;
        private float _trainingPlayerDeadTimer;
        private float _cosmeticSweepTimer;
        private float _stuckRoundTimer;
        private float _botWinConfirmTimer;
        private bool _takeAdvancePending;
        private float _takeAdvancePendingTimer;

        // Cached reflection
        private static FieldInfo _wfdFieldCache;
        private static FieldInfo _charPrefabFieldCache;
        private static FieldInfo _visNameFieldCache;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Plugin.Log.LogInfo("BotManager.Awake - Instance set");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private float _drawCheckTimer;
        private float _demoNeededCheckTimer;
        private long _lastDemoNeededLogged = -1L;

        private void Update()
        {
            if (!FishNet.InstanceFinder.IsServer) return;
            if (_activeBots.Count == 0 || GameManager.Instance == null) return;

            // Deferred explosive kill-feed watch: writes bot-on-human lines whose
            // isKilled flipped after the explosion postfix ran (RPC ordering race).
            BotPatches.ProcessPendingExplosiveFeeds();

            if (_takeAdvancePending)
            {
                _takeAdvancePendingTimer -= Time.deltaTime;
                if (_takeAdvancePendingTimer <= 0f)
                    _takeAdvancePending = false;
            }

            // NavGraph periodic validation — self-throttled (2s interval, 40-node batch).
            // Safe to call every frame; also runs in training mode since we want the
            // graph pruned while bots are recording/exploring.
            if (NavGraph.Instance != null)
            {
                NavGraph.Instance.TickValidation();
                // Training budget — self-throttled to 60s. Halves explore aggression once
                // the graph node count stops growing, so bots naturally ease off.
                NavGraph.Instance.TickTrainingBudget(NavGraph.Instance.NodeCount);
            }

            // Demo-needed proximity check — every 2s, if at least one human player is
            // close to a flagged edge, emit a one-shot hint log the UI can surface.
            // Uses the ACTIVE bot roster to infer which PlayerHealth instances are human.
            _demoNeededCheckTimer -= Time.deltaTime;
            if (_demoNeededCheckTimer <= 0f && NavGraph.Instance != null
                && NavGraph.Instance.DemoNeededCount > 0)
            {
                _demoNeededCheckTimer = 2f;

                // Humans = any PlayerHealth in scene whose GameObject is NOT one of our bots.
                var phs = Object.FindObjectsOfType<PlayerHealth>();
                if (phs != null && phs.Length > 0)
                {
                    foreach (var (fromPos, toPos) in NavGraph.Instance.DemoNeededEdgePositions())
                    {
                        Vector3 mid = (fromPos + toPos) * 0.5f;
                        foreach (var ph in phs)
                        {
                            if (ph == null || ph.isKilled) continue;
                            bool isBot = false;
                            foreach (var bot in _activeBots)
                            {
                                if (bot != null && bot.gameObject == ph.gameObject) { isBot = true; break; }
                            }
                            if (isBot) continue;
                            if (Vector3.Distance(ph.transform.position, mid) > 5f) continue;

                            long key = ((long)Mathf.FloorToInt(mid.x) << 32) ^ (uint)Mathf.FloorToInt(mid.z);
                            if (key != _lastDemoNeededLogged)
                            {
                                _lastDemoNeededLogged = key;
                                Plugin.Log.LogInfo($"[BOT] Bots keep failing a route near {mid:F1} — walking it yourself once will teach it");
                            }
                            goto demoNeededFound;
                        }
                    }
                    demoNeededFound: ;
                }
            }

            // Only check every 0.5s to avoid expensive lookups every frame
            _drawCheckTimer -= Time.deltaTime;
            if (_drawCheckTimer > 0f) return;
            _drawCheckTimer = 0.5f;

            // Sweep stray cosmetics: hats the game detached from dying bots render as giant
            // white untextured slabs frozen in the world. A player's own thrown hat keeps a
            // live reference to their mount and is left alone. Bots WEAR cosmetics again
            // (mod-applied, parented to their mount) — those stay; only DETACHED bot
            // cosmetics (thrown into the world) or reference-less orphans are junk.
            _cosmeticSweepTimer -= 0.5f;
            if (_cosmeticSweepTimer <= 0f)
            {
                _cosmeticSweepTimer = 5f;
                try
                {
                    foreach (var hp in FindObjectsOfType<HatPosition>())
                    {
                        if (hp == null) continue;
                        bool orphan = hp.reference == null;
                        bool detachedBotCosmetic = !orphan && hp.transform.parent == null
                            && hp.reference.GetComponentInParent<BotController>() != null;
                        if (orphan || detachedBotCosmetic) Destroy(hp.gameObject);
                    }
                }
                catch { }
            }

            // Draw timer: if only bots are alive (all humans dead), force a draw after 25 seconds
            bool anyHumanAlive = false;
            bool anyBotAlive = false;
            int aliveBotCount = 0;
            BotController lastAliveBot = null;
            foreach (var bot in _activeBots)
            {
                if (bot != null && !bot.IsDead)
                {
                    anyBotAlive = true;
                    aliveBotCount++;
                    lastAliveBot = bot;
                }
            }
            // Check alive players via GameManager instead of FindObjectsOfType
            if (GameManager.Instance.alivePlayers.Count > 0)
            {
                foreach (int pid in GameManager.Instance.alivePlayers)
                {
                    bool isBot = false;
                    foreach (var bot in _activeBots)
                    {
                        if (bot != null && bot.PlayerId == pid) { isBot = true; break; }
                    }
                    if (!isBot) { anyHumanAlive = true; break; }
                }
            }
            if (anyHumanAlive && !HasLivingHumanPlayerHealth())
                anyHumanAlive = false;

            // Training mode: never end the round — bots need uninterrupted time.
            // But rounds never ending means a dead PLAYER would spectate forever, so
            // bring them back through the game's own round-spawn flow (same path the
            // game uses at round start: fresh object, camera, HUD all handled).
            bool trainingMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;
            if (trainingMode)
            {
                if (!anyHumanAlive)
                {
                    _trainingPlayerDeadTimer += 0.5f;
                    if (_trainingPlayerDeadTimer >= 2.5f)
                    {
                        _trainingPlayerDeadTimer = 0f;
                        try
                        {
                            var pm = ClientInstance.Instance?.PlayerSpawner;
                            if (pm != null)
                            {
                                Plugin.Log.LogInfo("[BOT] Training: respawning dead player");
                                pm.RoundSpawn();
                            }
                        }
                        catch (System.Exception e)
                        {
                            Plugin.Log.LogWarning($"[BOT] Training player respawn failed: {e.Message}");
                        }
                    }
                }
                else
                {
                    _trainingPlayerDeadTimer = 0f;
                }
                return;
            }

            if (!anyHumanAlive && aliveBotCount == 1)
            {
                _onlyBotsAliveTimer = 0f;
                _stuckRoundTimer = 0f;
                _botWinConfirmTimer += 0.5f;
                // Let the normal round-end flow run first; only force progression
                // if the game stalls with one surviving bot for several seconds.
                if (_botWinConfirmTimer >= 5f)
                {
                    _botWinConfirmTimer = 0f;
                    ProgressTakeForBotWinner(lastAliveBot);
                }
                return;
            }

            _botWinConfirmTimer = 0f;

            // Nobody alive at all — round is stuck, force progression
            if (!anyHumanAlive && !anyBotAlive)
            {
                _stuckRoundTimer += 0.5f;
                if (_stuckRoundTimer >= 5f)
                {
                    _stuckRoundTimer = 0f;
                    Plugin.Log.LogInfo("[BOT] No one alive for 5s, forcing draw resolution");
                    try
                    {
                        // True draw behavior: no score award, let WaitForDraw decide.
                        GameManager.Instance.alivePlayers.Clear();
                        StartFreshWaitForDraw();
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"[BOT] Force draw resolve error: {e.Message}");
                        try { GameManager.Instance.ProgressToNextTake(); } catch { }
                    }
                }
            }
            else if (!anyHumanAlive && anyBotAlive)
            {
                _stuckRoundTimer = 0f;
                _onlyBotsAliveTimer += 0.5f; // Accumulate the check interval, not frame delta
                int drawTimeout = Mathf.Clamp(Plugin.DrawTimer?.Value ?? 25, 1, 900);
                if (_onlyBotsAliveTimer >= drawTimeout)
                {
                    _onlyBotsAliveTimer = 0f;
                    ForceKillAllBots();
                }
            }
            else
            {
                _onlyBotsAliveTimer = 0f;
                _stuckRoundTimer = 0f;
            }
        }

        /// <summary>
        /// Entering Play from training: wipe every bot and player, then run the game's
        /// own round-start flow on the CURRENT map. RoundSpawn respawns the player fresh
        /// at a spawn point (with the round-start countdown), resets round items, and the
        /// RoundSpawn postfix brings fresh bots in ~1.5s after it.
        /// </summary>
        public void StartFreshPlayRound()
        {
            if (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServer) return;

            ResetDrawTimer();
            _trainingPlayerDeadTimer = 0f;
            DespawnAllBots();

            try
            {
                var pm = ClientInstance.Instance?.PlayerSpawner;
                if (pm != null)
                {
                    Plugin.Log.LogInfo("[BOT] Play mode: starting fresh round on this map");
                    pm.RoundSpawn();
                }
                else
                {
                    Plugin.Log.LogWarning("[BOT] Play round start: no PlayerSpawner — not in a match?");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BOT] Play round start failed: {e.Message}");
            }
        }

        /// <summary>
        /// Kill all alive bots to force a draw when no humans remain.
        /// </summary>
        public void ForceKillAllBots()
        {
            Plugin.Log.LogInfo("[BOT] Force-killing all bots (draw timer expired)");

            // Give each alive bot's team a point so the round/take progresses
            // Without this, bot-only draws award no points and the map stalls
            try
            {
                if (ScoreManager.Instance != null)
                {
                    var aliveBots = new List<BotController>(_activeBots);
                    foreach (var bot in aliveBots)
                    {
                        if (bot == null || bot.IsDead) continue;
                        int teamId = ScoreManager.Instance.GetTeamId(bot.PlayerId);
                        ScoreManager.Instance.AddRoundScore(teamId);
                        Plugin.Log.LogInfo($"[BOT] Draw point for team {teamId} (bot {bot.BotName})");
                        break; // One point is enough to progress
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BOT] Draw score failed: {e.Message}"); }

            var botsCopy = new List<BotController>(_activeBots);
            foreach (var bot in botsCopy)
            {
                if (bot == null || bot.IsDead) continue;
                var ph = bot.GetComponent<PlayerHealth>();
                if (ph != null)
                {
                    ph.health = -8f;
                    ph.isKilled = true;
                    ph.isShot = true;
                }
                // Spawn ragdoll FIRST, then disable physics
                try { if (ph != null) ph.ExplodeServer(false, false, "", -bot.transform.forward, 30f, bot.transform.position + Vector3.up * 2f); } catch { }
                BotController.DisableBotPhysicsPublic(bot.gameObject);
                try { if (ph != null) ph.DisablePlayerObjectWhenKilled(); } catch { }
                // Die() handles: drop weapon, destroy camera, disable component, call PlayerDied
                bot.Die(null);
            }
            try
            {
                if (PauseManager.Instance != null)
                    PauseManager.Instance.WriteLog("<b>Bots timed out — forcing draw</b>");
            }
            catch { }

            // Force round to progress — start WaitForDraw directly via reflection
            try
            {
                if (GameManager.Instance != null)
                {
                    if (_wfdFieldCache == null) _wfdFieldCache = typeof(GameManager).GetField("waitForDrawCoroutine",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var coroutineField = _wfdFieldCache;

                    // Stop any existing WaitForDraw
                    if (coroutineField != null)
                    {
                        var existing = coroutineField.GetValue(GameManager.Instance) as Coroutine;
                        if (existing != null)
                            GameManager.Instance.StopCoroutine(existing);
                        coroutineField.SetValue(GameManager.Instance, null);
                    }

                    // Start a fresh WaitForDraw — alivePlayers is empty so it processes as a draw
                    var waitMethod = typeof(GameManager).GetMethod("WaitForDraw",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (waitMethod != null)
                    {
                        var coroutine = GameManager.Instance.StartCoroutine(
                            (System.Collections.IEnumerator)waitMethod.Invoke(GameManager.Instance, null));
                        if (coroutineField != null)
                            coroutineField.SetValue(GameManager.Instance, coroutine);
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"Force draw: {e.Message}"); }
        }

        public void AddBot()
        {
            if (LobbyBots.Count >= Plugin.MaxBots.Value) return;
            // Slot = lobby position, so "Bot 1 Name/Skill" always means the first bot.
            BotData bot = BotData.CreateRandom(_nextBotId++, LobbyBots.Count % 8);
            LobbyBots.Add(bot);
            Plugin.Log.LogInfo($"Added {bot.Name} to lobby (suit {bot.SuitIndex})");
        }

        public void RemoveLastBot()
        {
            if (LobbyBots.Count == 0) return;
            LobbyBots.RemoveAt(LobbyBots.Count - 1);
        }

        public void SpawnAllBots()
        {
            if (LobbyBots.Count == 0) return;

            SpawnPoint[] spawns = FindSpawnPoints();
            if (spawns.Length == 0)
            {
                Plugin.Log.LogWarning("No spawn points found");
                return;
            }

            // Assign player IDs to bots (use slots not taken by real players)
            AssignPlayerIds();

            Plugin.Log.LogInfo($"[BOT] Spawning {LobbyBots.Count} bots across {spawns.Length} player spawn points");

            for (int i = 0; i < LobbyBots.Count; i++)
            {
                BotData botData = LobbyBots[i];
                Vector3 spawnPos = GetDistributedSpawnPosition(spawns, i);

                GameObject botObj = CreateBot(botData, spawnPos);
                if (botObj == null) continue;

                BotController controller = botObj.GetComponent<BotController>();
                if (controller == null)
                    controller = botObj.AddComponent<BotController>();

                controller.BotId = botData.BotId;
                controller.SkillSlot = botData.SlotIndex;
                controller.BotName = botData.Name;
                controller.PlayerId = botData.PlayerId;
                botData.Controller = controller;
                botData.PlayerObject = botObj;

                _activeBots.Add(controller);

                // Register bot as a player in game systems
                RegisterBotAsPlayer(botData, botObj);

                Plugin.Log.LogInfo($"Spawned {botData.Name} (PlayerId={botData.PlayerId}) at {spawnPos}");
            }
        }

        /// <summary>
        /// Assign real player slot IDs (0-7) to bots, avoiding slots used by real players.
        /// </summary>
        private void AssignPlayerIds()
        {
            HashSet<int> takenIds = new HashSet<int>();

            // Collect IDs used by real players
            foreach (var kvp in ClientInstance.playerInstances)
                takenIds.Add(kvp.Key);

            // Also check already-assigned bots
            foreach (var bot in LobbyBots)
                if (bot.PlayerId >= 0) takenIds.Add(bot.PlayerId);

            // Start at 11 so bot IDs never collide with real player IDs (0-7)
            int nextId = 11;
            foreach (var bot in LobbyBots)
            {
                if (bot.PlayerId >= 0) continue; // already assigned

                while (takenIds.Contains(nextId)) nextId++;
                bot.PlayerId = nextId;
                takenIds.Add(nextId);
                nextId++;
            }
        }

        /// <summary>
        /// Register bot in all game systems so it's treated as a real player.
        /// Creates a fake ClientInstance entry and adds to scoring/alive tracking.
        /// </summary>
        private void RegisterBotAsPlayer(BotData botData, GameObject botObj)
        {
            // 1. Set up ClientInstance on the bot object
            ClientInstance ci = botObj.GetComponent<ClientInstance>();
            if (ci != null)
            {
                ci.PlayerId = botData.PlayerId;
                ci.PlayerName = botData.Name;
                try
                {
                    // Several vanilla UI paths use PlayerNameTag (not PlayerName).
                    // If this is empty, round/take winner text can show blank for bots.
                    var tagField = typeof(ClientInstance).GetField("PlayerNameTag",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    if (tagField != null) tagField.SetValue(ci, botData.Name);
                }
                catch { }
                ci.ConnectionID = -1 - botData.BotId; // Fake negative connection IDs

                // Add to the global player registry
                if (!ClientInstance.playerInstances.ContainsKey(botData.PlayerId))
                    ClientInstance.playerInstances.Add(botData.PlayerId, ci);

                // Add to SteamLobby players list
                NetworkObject nob = botObj.GetComponent<NetworkObject>();
                if (nob != null && SteamLobby.Instance != null)
                {
                    if (!SteamLobby.Instance.players.Contains(nob))
                        SteamLobby.Instance.players.Add(nob);
                }

                Plugin.Log.LogInfo($"[{botData.Name}] Registered as PlayerId={botData.PlayerId}");
            }

            // 2. Set PlayerValues.playerClient so game can look up bot identity
            PlayerValues pv = botObj.GetComponent<PlayerValues>();
            if (pv != null && ci != null)
            {
                pv.playerClient = ci;
                pv.enabled = false; // Still disable Update (NREs)
            }

            // 3. Add to alive players
            if (GameManager.Instance != null)
                GameManager.Instance.alivePlayers.Add(botData.PlayerId);

            // 4. Set team — each bot gets its own team (FFA style) so round doesn't end early
            int teamId = botData.TeamId >= 0 ? botData.TeamId : botData.PlayerId;
            if (ScoreManager.Instance != null)
                ScoreManager.Instance.SetTeamId(botData.PlayerId, teamId);
        }

        private SpawnPoint[] FindSpawnPoints()
        {
            GameObject spawn4v4 = GameObject.FindGameObjectWithTag("Spawnpoints4Player");
            if (spawn4v4 != null)
            {
                SpawnPoint[] points = spawn4v4.GetComponentsInChildren<SpawnPoint>();
                if (points.Length > 0) return points;
            }

            GameObject spawn1v1 = GameObject.FindGameObjectWithTag("Spawnpoints");
            if (spawn1v1 != null)
                return spawn1v1.GetComponentsInChildren<SpawnPoint>();

            return new SpawnPoint[0];
        }

        private Vector3 GetDistributedSpawnPosition(SpawnPoint[] spawns, int botIndex)
        {
            if (spawns == null || spawns.Length == 0) return Vector3.zero;

            SpawnPoint spawn = spawns[botIndex % spawns.Length];
            int stackIndex = botIndex / spawns.Length;
            Vector3 offset = stackIndex > 0 ? GetSpawnStackOffset(spawn.transform, stackIndex) : Vector3.zero;
            return spawn.transform.position + Vector3.up * 1f + offset;
        }

        private Vector3 GetSpawnStackOffset(Transform spawn, int stackIndex)
        {
            Vector3[] dirs =
            {
                Vector3.right,
                Vector3.left,
                Vector3.forward,
                Vector3.back,
                (Vector3.right + Vector3.forward).normalized,
                (Vector3.left + Vector3.forward).normalized,
                (Vector3.right + Vector3.back).normalized,
                (Vector3.left + Vector3.back).normalized
            };

            int slot = (stackIndex - 1) % dirs.Length;
            int ring = ((stackIndex - 1) / dirs.Length) + 1;
            Vector3 dir = spawn != null ? spawn.TransformDirection(dirs[slot]) : dirs[slot];
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = dirs[slot];
            return dir.normalized * (0.85f * ring);
        }

        private GameObject GetCharacterPrefab()
        {
            // Always re-fetch to ensure we get a clean prefab reference
            // Caching can cause issues if the prefab reference becomes stale between takes

            PlayerManager pm = FindObjectOfType<PlayerManager>();
            if (pm == null) return null;

            if (_charPrefabFieldCache == null)
                _charPrefabFieldCache = typeof(PlayerManager).GetField("characterPrefab", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo field = _charPrefabFieldCache;
            if (field == null) return null;

            _cachedPrefab = field.GetValue(pm) as GameObject;
            Plugin.Log.LogInfo($"Got characterPrefab: {(_cachedPrefab != null ? _cachedPrefab.name : "null")}");
            return _cachedPrefab;
        }

        private GameObject CreateBot(BotData botData, Vector3 position)
        {
            try
            {
                GameObject prefab = GetCharacterPrefab();
                if (prefab == null)
                {
                    Plugin.Log.LogError("No character prefab found!");
                    return null;
                }

                // Clone inactive so Awake doesn't fire mid-setup
                bool wasActive = prefab.activeSelf;
                prefab.SetActive(false);
                GameObject botObj = Instantiate(prefab, position, Quaternion.identity);
                prefab.SetActive(wasActive);

                botObj.name = $"Bot_{botData.Name}";
                // Register BEFORE activation/network spawn: the game's ChangeDress RPC can
                // fire before BotController is attached, and a component check alone let it
                // create the hat that later detached as a white slab. The registry has no
                // such race — the prefix skip works from frame zero.
                BotObjectIds.Add(botObj.GetInstanceID());

                // Set cosmetics before activation
                PlayerSetup setup = botObj.GetComponent<PlayerSetup>();
                botData.EnsureCosmeticsValid();
                if (setup != null)
                {
                    setup.mat = botData.SuitIndex;
                    setup.cig = botData.CigIndex;
                }

                // Activate
                botObj.SetActive(true);

                var controller = botObj.GetComponent<BotController>();
                if (controller == null)
                    controller = botObj.AddComponent<BotController>();
                controller.BotId = botData.BotId;
                controller.SkillSlot = botData.SlotIndex;
                controller.BotName = botData.Name;
                controller.PlayerId = botData.PlayerId;
                controller.RefreshVisualSerial();

                // Disable components that need a real player client
                DisableBotComponents(botObj);

                // FishNet spawn as server-owned
                NetworkObject nob = botObj.GetComponent<NetworkObject>();
                if (nob != null)
                {
                    InstanceFinder.ServerManager.Spawn(nob);
                    Plugin.Log.LogInfo($"FishNet spawned: {botData.Name}");
                }

                // Root stays layer 6 (like real players) so CharacterController collides with environment.
                // Children (body colliders/graphics) go on layer 11 so player weapons can hit bots.
                // Do this before applying cosmetics so hats/cigs keep the same layers PlayerSetup.ChangeDress uses.
                botObj.layer = 3; // Layer 3 so ItemBehaviour.Update positions weapons at hand bones on all clients
                foreach (Transform child in botObj.transform)
                    SetLayerRecursive(child.gameObject, 11);

                // Apply cosmetics (suit + cig) directly — no RPC needed on host
                ApplyAllCosmetics(botObj, botData);

                // Set name tag
                SetNameTag(botObj, botData.Name);

                // Sync skin + cig to non-host clients via Mycelium (hats disabled).
                if (nob != null)
                    StartCoroutine(DelaySkinSync((int)nob.ObjectId, controller.VisualSerial, botData.SuitIndex, -1, botData.CigIndex));

                // Hats re-enabled: retry attachment if the first pass didn't stick
                // (cosmetics can race prefab/animator init on spawn).
                StartCoroutine(RetryApplyCosmetics(botData, botObj));
                StartCoroutine(HatStateProbe(botData, botObj));

                // Ensure graphics are enabled
                var ph = botObj.GetComponent<PlayerHealth>();
                if (ph != null && ph.graphics != null)
                    ph.graphics.SetActive(true);

                return botObj;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"Bot creation failed: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        private void DisableBotComponents(GameObject botObj)
        {
            // Disable FPC input (don't steal player controls)
            var fpc = botObj.GetComponent<FirstPersonController>();
            if (fpc != null)
            {
                try
                {
                    if (fpc.move != null) fpc.move.Disable();
                    if (fpc.moveUp != null) fpc.moveUp.Disable();
                    if (fpc.jump != null) fpc.jump.Disable();
                    if (fpc.run != null) fpc.run.Disable();
                    if (fpc.lookX != null) fpc.lookX.Disable();
                    if (fpc.lookY != null) fpc.lookY.Disable();
                    if (fpc.crouch != null) fpc.crouch.Disable();
                    if (fpc.leanLeft != null) fpc.leanLeft.Disable();
                    if (fpc.leanRight != null) fpc.leanRight.Disable();

                    fpc.jump.performed -= fpc.Jump;
                    fpc.crouch.performed -= fpc.Slide;
                    fpc.crouch.started -= fpc.SetCrouch;
                    fpc.crouch.canceled -= fpc.SetCrouch;
                    fpc.crouch.canceled -= fpc.SlideEnd;
                }
                catch { }
                fpc.enabled = false;
            }

            // Disable PlayerPickup (bot handles weapons)
            var pp = botObj.GetComponent<PlayerPickup>();
            if (pp != null) pp.enabled = false;

            // Disable PlayerShoot
            var ps = botObj.GetComponent<PlayerShoot>();
            if (ps != null) ps.enabled = false;

            // Disable HUD components
            foreach (var c in botObj.GetComponentsInChildren<HUDTween>(true)) c.enabled = false;
            foreach (var c in botObj.GetComponentsInChildren<HUD>(true)) c.enabled = false;
            foreach (var c in botObj.GetComponentsInChildren<HealthTween>(true)) c.enabled = false;

            // Disable MatchChat
            var mc = botObj.GetComponent<MatchChat>();
            if (mc != null) mc.enabled = false;

            // Disable KillCam
            foreach (var kc in botObj.GetComponentsInChildren<KillCam>(true)) kc.enabled = false;
        }

        private System.Collections.IEnumerator DelaySkinSync(int netId, int visualSerial, int suitIndex, int hatIndex = -1, int cigIndex = 0)
        {
            // Wait for bot to exist on non-host clients, then send + retry
            yield return new WaitForSeconds(1.5f);
            BotDamageSync.SyncSkin(netId, suitIndex, hatIndex, cigIndex, visualSerial);
            yield return new WaitForSeconds(3f);
            BotDamageSync.SyncSkin(netId, suitIndex, hatIndex, cigIndex, visualSerial); // Retry in case first was too early
        }

        private System.Collections.IEnumerator RetryApplyCosmetics(BotData botData, GameObject botObj)
        {
            if (botObj == null || botData == null) yield break;
            float[] delays = { 0.35f, 1.0f, 2.0f, 5.0f, 10.0f, 20.0f };
            for (int i = 0; i < delays.Length; i++)
            {
                yield return new WaitForSeconds(delays[i]);
                if (botObj == null) yield break;
                if (HasVisibleHat(botObj)) yield break;
                ProbeHatState(botData, botObj, i, "retry: visibility check failed");
                ApplyAllCosmetics(botObj, botData);
                if (HasVisibleHat(botObj)) yield break;
            }
        }

        /// <summary>Runtime hat diagnostics. Every spawn-time line reads healthy while
        /// hats still don't show in game, so this samples the RENDER-time state the
        /// spawn log can't capture: renderer.isVisible, world scale, drift from the
        /// head bone, material/shader. Logs for a window after each dress pass and any
        /// time something is anomalous.</summary>
        private System.Collections.IEnumerator HatStateProbe(BotData botData, GameObject botObj)
        {
            var wait = new WaitForSeconds(4f);
            int sample = 0;
            while (botObj != null)
            {
                yield return wait;
                if (botObj == null) yield break;
                sample++;
                try { ProbeHatState(botData, botObj, sample, null); } catch { }
            }
        }

        private static void ProbeHatState(BotData botData, GameObject botObj, int sample, string context)
        {
            if (botObj == null) return;
            string name = botData?.Name ?? botObj.name;
            bool recentDress = botData != null && (Time.time - botData.LastDressTime) < 12f;

            var setup = botObj.GetComponent<PlayerSetup>();
            Transform pivot = setup != null ? setup.hatToWearPosition : null;
            string pivotState;
            bool anomaly = false;
            if (pivot == null) { pivotState = "MISSING"; anomaly = true; }
            else if (!pivot.gameObject.activeInHierarchy)
            {
                Transform guilty = pivot;
                while (guilty != null && guilty.gameObject.activeSelf) guilty = guilty.parent;
                pivotState = $"INACTIVE(via '{(guilty != null ? guilty.name : "?")}')";
                anomaly = true;
            }
            else pivotState = "active";

            Transform head = null;
            var anim = botObj.GetComponentInChildren<Animator>(true);
            if (anim != null && anim.isHuman) head = anim.GetBoneTransform(HumanBodyBones.Head);

            int cosmeticCount = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var hp in botObj.GetComponentsInChildren<HatPosition>(true))
            {
                if (hp == null || hp.gameObject == null) continue;
                string n = hp.gameObject.name;
                bool isBotCosmetic = n.StartsWith("BOT_HAT_", System.StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("BOT_CIG_", System.StringComparison.OrdinalIgnoreCase);
                if (!isBotCosmetic) continue;
                cosmeticCount++;
                var go = hp.gameObject;
                var r = go.GetComponentInChildren<Renderer>(true);
                float dHead = head != null ? Vector3.Distance(go.transform.position, head.position) : -1f;
                float scl = go.transform.lossyScale.x;
                float bnd = r != null ? Mathf.Max(r.bounds.size.x, Mathf.Max(r.bounds.size.y, r.bounds.size.z)) : -1f;
                bool act = go.activeInHierarchy;
                bool en = r != null && r.enabled;
                bool vis = r != null && r.isVisible;
                string mat = "none", shader = "none";
                if (r != null && r.sharedMaterial != null)
                {
                    mat = r.sharedMaterial.name;
                    shader = r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?";
                }
                if (!act || r == null || !en || !vis || scl < 0.05f || (dHead >= 0f && dHead > 1.5f))
                    anomaly = true;
                sb.Append($" | {n}: act={act} layer={go.layer} scl={scl:F2} bnd={bnd:F2} dHead={dHead:F2} " +
                    $"rendEnabled={en} isVisible={vis} refSet={hp.reference != null} mat='{mat}' shader='{shader}'");
            }
            if (cosmeticCount == 0)
            {
                anomaly = true;
                sb.Append(" | NO BOT_HAT/BOT_CIG instances under bot (destroyed?)");
            }

            if (anomaly || recentDress || context != null)
                Plugin.Log.LogInfo($"[HatProbe] {name} s{sample}{(context != null ? $" ({context})" : "")}" +
                    $"{(anomaly ? " ANOMALY" : "")}: pivot={pivotState}{sb}");
        }

        private bool HasLivingHumanPlayerHealth()
        {
            try
            {
                var players = Object.FindObjectsOfType<PlayerHealth>();
                foreach (var ph in players)
                {
                    if (ph == null || ph.isKilled) continue;
                    if (ph.GetComponent<BotController>() == null)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private void ProgressTakeForBotWinner(BotController winner)
        {
            if (_takeAdvancePending) return;
            if (winner == null || winner.IsDead) return;
            if (ScoreManager.Instance == null || GameManager.Instance == null) return;

            _takeAdvancePending = true;
            _takeAdvancePendingTimer = 4f;

            try
            {
                int teamId = ScoreManager.Instance.GetTeamId(winner.PlayerId);
                if (teamId < 0) teamId = winner.PlayerId;

                Plugin.Log.LogInfo($"[BOT] Take stalled with winner={winner.BotName} team={teamId} -> advancing");

                if (_wfdFieldCache == null)
                {
                    _wfdFieldCache = typeof(GameManager).GetField("waitForDrawCoroutine",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                if (_wfdFieldCache != null)
                {
                    var existing = _wfdFieldCache.GetValue(GameManager.Instance) as Coroutine;
                    if (existing != null) GameManager.Instance.StopCoroutine(existing);
                    _wfdFieldCache.SetValue(GameManager.Instance, null);
                }

                ScoreManager.Instance.AddRoundScore(teamId);

                bool isRoundWon = ScoreManager.Instance.CheckForRoundWin(out int winningTeamId);
                if (isRoundWon)
                {
                    if (winningTeamId < 0) winningTeamId = teamId;
                    try { PauseManager.Instance?.WriteLog($"{FormatBotWinnerLabel(winner.BotName)} won the round"); } catch { }
                    try
                    {
                        RoundManager.Instance?.CmdEndRound(winningTeamId);
                    }
                    catch (System.Exception endEx)
                    {
                        Plugin.Log.LogWarning($"[BOT] CmdEndRound failed for team {winningTeamId}: {endEx.Message}");
                    }
                    // If vanilla end-round flow did not attach, force the same draw-resolution
                    // coroutine path so scene progression still occurs.
                    try
                    {
                        var rm = RoundManager.Instance;
                        if (rm == null || rm.InterfaceSetupCoroutine == null)
                            StartFreshWaitForDraw();
                    }
                    catch { StartFreshWaitForDraw(); }
                }
                else
                {
                    try { PauseManager.Instance?.WriteLog($"{FormatBotWinnerLabel(winner.BotName)} won the take"); } catch { }
                    GameManager.Instance.ProgressToNextTake();
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BOT] Bot winner progression failed: {e.Message}");
                _takeAdvancePending = false;
                try { GameManager.Instance.ProgressToNextTake(); } catch { }
            }
        }

        private string FormatBotWinnerLabel(string botName)
        {
            if (string.IsNullOrWhiteSpace(botName)) botName = "Bot";
            return $"<b><color=#6CD4FF>{botName}</color></b>";
        }

        private void StartFreshWaitForDraw()
        {
            if (GameManager.Instance == null) return;
            try
            {
                if (_wfdFieldCache == null)
                {
                    _wfdFieldCache = typeof(GameManager).GetField("waitForDrawCoroutine",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                }
                if (_wfdFieldCache != null)
                {
                    var existing = _wfdFieldCache.GetValue(GameManager.Instance) as Coroutine;
                    if (existing != null) GameManager.Instance.StopCoroutine(existing);
                    _wfdFieldCache.SetValue(GameManager.Instance, null);
                }

                var waitMethod = typeof(GameManager).GetMethod("WaitForDraw",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (waitMethod == null) return;
                var coroutine = GameManager.Instance.StartCoroutine(
                    (System.Collections.IEnumerator)waitMethod.Invoke(GameManager.Instance, null));
                if (_wfdFieldCache != null)
                    _wfdFieldCache.SetValue(GameManager.Instance, coroutine);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BOT] StartFreshWaitForDraw failed: {e.Message}");
            }
        }

        private void SetNameTag(GameObject botObj, string botName)
        {
            try
            {
                VisualInfo vi = botObj.GetComponentInChildren<VisualInfo>(true);
                if (vi != null)
                {
                    if (_visNameFieldCache == null)
                        _visNameFieldCache = typeof(VisualInfo).GetField("name", BindingFlags.Public | BindingFlags.Instance);
                    var nameField = _visNameFieldCache;
                    if (nameField != null)
                    {
                        var tmp = nameField.GetValue(vi) as TMPro.TextMeshProUGUI;
                        if (tmp != null) tmp.text = botName;
                    }
                }
            }
            catch { }
        }

        /// <summary>Re-apply suit + hat + cig to a bot. Called on respawn and for late joiners.</summary>
        public void ReapplyCosmetics(BotData botData, GameObject botObj, bool randomize = true)
        {
            if (botObj == null) return;
            // Randomize each round; mid-round (training) respawns keep the same suit so a
            // reapply can never look like a "glitched" material change.
            if (randomize) botData.RandomizeCosmetics();
            ApplyAllCosmetics(botObj, botData);

            // Sync to non-host via Mycelium
            var nob = botObj.GetComponent<FishNet.Object.NetworkObject>();
            var controller = botObj.GetComponent<BotController>();
            if (nob != null)
                StartCoroutine(DelaySkinSync((int)nob.ObjectId, controller != null ? controller.VisualSerial : 0,
                    botData.SuitIndex, -1, botData.CigIndex));
        }

        public void ReapplyCosmeticsForBot(BotController controller)
        {
            if (controller == null || controller.gameObject == null) return;
            var botData = LobbyBots.Find(b => b.Controller == controller
                || b.PlayerObject == controller.gameObject
                || b.BotId == controller.BotId);
            if (botData == null) return;
            ReapplyCosmetics(botData, controller.gameObject, randomize: false);
        }

        /// <summary>
        /// Apply suit material + instantiate hat + cig directly on the bot.
        /// Matches what PlayerSetup.ChangeDress does: instantiate, parent to hatToWearPosition, add HatPosition tracker.
        /// </summary>
        public static void ApplyAllCosmetics(GameObject botObj, BotData botData)
        {
            if (botObj == null) return;
            try
            {
                if (botData != null) botData.LastDressTime = Time.time;
                var setup = botObj.GetComponent<PlayerSetup>();
                // Bots wear a random hat + cig again. The old white-slab bug had two
                // ingredients, both fixed at the source: (1) the game's ChangeDress racing
                // in before BotController attached (blocked by prefix via the instance-id
                // registry), and (2) the death path throwing setup.hat into the world
                // (we keep setup.hat null and Die() destroys all HatPosition children).
                // A size sanity guard below discards anything slab-shaped regardless.

                // Suit material
                if (CosmeticsManager.Instance?.mats != null &&
                    botData.SuitIndex >= 0 && botData.SuitIndex < CosmeticsManager.Instance.mats.Length)
                {
                    Material mat = CosmeticsManager.Instance.mats[botData.SuitIndex];
                    if (mat != null)
                    {
                        if (setup != null)
                        {
                            setup.normalMat = mat;
                            setup.mat = botData.SuitIndex;
                            if (setup.meshesToChange != null)
                            {
                                foreach (var obj in setup.meshesToChange)
                                {
                                    if (obj != null)
                                    {
                                        var smr = obj.GetComponent<SkinnedMeshRenderer>();
                                        if (smr != null) smr.material = mat;
                                    }
                                }
                            }
                        }
                        // Fallback: hit all SkinnedMeshRenderers
                        else
                        {
                            foreach (var smr in botObj.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                                try { smr.material = mat; } catch { }
                        }
                    }
                }

                Transform hatPos = ResolveHatAnchor(botObj, setup, true);
                if (hatPos == null)
                {
                    Plugin.Log.LogWarning($"[BotCosmetics] {botData?.Name}: no hat anchor found (hatToWearPosition null, no head bone) — skipping cosmetics");
                    return;
                }
                Plugin.Log.LogInfo($"[BotCosmetics] {botData?.Name}: anchor='{hatPos.name}' " +
                    $"(game pivot: {(setup != null && setup.hatToWearPosition == hatPos)}) hatIdx={botData?.HatIndex} cigIdx={botData?.CigIndex}");
                // The mount must be ACTIVE for cosmetics to render — the game deactivates
                // it for the LOCAL player only (you don't see your own hat), and the old
                // suit-only bot code deactivated it too. ChangeDress stays blocked for
                // bots, so nothing renegade can spawn under it.
                hatPos.gameObject.SetActive(true);
                Transform hatMount = hatPos;
                if (setup != null) setup.cig = botData.CigIndex;

                // Destroy old hat/cig instances before creating new ones — sweep the WHOLE
                // bot, not just the resolved mount: the game's ChangeDress RPC may have
                // parented cosmetics under a different anchor before BotController existed.
                CleanupOldCosmetics(hatMount);
                foreach (var hp in botObj.GetComponentsInChildren<HatPosition>(true))
                    if (hp != null) Object.Destroy(hp.gameObject);
                if (setup != null) setup.hat = null;
                CleanupOrphanedCosmetics();

                int visualLayer = GetBotVisualLayer(botObj);

                // Hat — same construction PlayerSetup.ChangeDress uses, from the same
                // prefab list players wear. setup.hat stays null ON PURPOSE: the game's
                // death code throws setup.hat into the world (the old white-slab source);
                // bot cosmetics are destroyed with the body in Die() instead.
                if (botData.HatIndex >= 0)
                {
                    GameObject hatPrefab = ResolveHatPrefab(botData);
                    if (hatPrefab == null)
                        Plugin.Log.LogWarning($"[BotCosmetics] {botData?.Name}: no usable hat prefab (hats array: {GetHatPrefabs(CosmeticsManager.Instance)?.Length ?? -1})");
                    if (hatPrefab != null)
                    {
                        var hatInst = Object.Instantiate(hatPrefab, hatMount.position, Quaternion.identity, hatMount);
                        hatInst.name = "BOT_HAT_" + hatPrefab.name;
                        hatInst.AddComponent<HatPosition>().reference = hatMount;
                        PrepareCosmeticInstance(hatInst, hatMount, true, visualLayer);
                        hatInst.transform.forward = botObj.transform.forward;
                        hatInst.SetActive(true);
                        SanityCheckCosmeticSize(hatInst, "hat");
                        var hatRs = hatInst.GetComponentsInChildren<Renderer>(true);
                        Plugin.Log.LogInfo($"[BotCosmetics] {botData?.Name}: hat '{hatPrefab.name}' spawned — " +
                            $"activeInHierarchy={hatInst.activeInHierarchy} renderers={hatRs.Length} " +
                            $"pivotScale={hatMount.lossyScale.x:F3} pos={hatInst.transform.position} layer={hatInst.layer}");
                    }
                }

                // Cig/pipe/cigar — same as ChangeDress, but picked through a filter that
                // skips placeholder/renderer-less prefabs (the likely old white-slab cig).
                if (botData.CigIndex >= 0)
                {
                    GameObject cigPrefab = ResolveCigPrefab(botData);
                    if (cigPrefab == null)
                        Plugin.Log.LogWarning($"[BotCosmetics] {botData?.Name}: no usable cig prefab (cigs array: {CosmeticsManager.Instance?.cigs?.Length ?? -1})");
                    if (cigPrefab != null)
                    {
                        var cigInst = Object.Instantiate(cigPrefab, hatMount.position, Quaternion.identity, hatMount);
                        cigInst.name = "BOT_CIG_" + cigPrefab.name;
                        cigInst.AddComponent<HatPosition>().reference = hatMount;
                        PrepareCosmeticInstance(cigInst, hatMount, false, visualLayer);
                        cigInst.SetActive(true);
                        SanityCheckCosmeticSize(cigInst, "cig");
                    }
                }

                FixCosmeticVisibility(hatMount, visualLayer);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"ApplyAllCosmetics: {e.Message}"); }
        }

        private static int GetBotVisualLayer(GameObject botObj)
        {
            if (botObj == null) return 0;
            // Bot root can be a networking/weapon helper layer; prefer actual rendered body layer.
            var smr = botObj.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null) return smr.gameObject.layer;
            var mr = botObj.GetComponentInChildren<MeshRenderer>(true);
            if (mr != null) return mr.gameObject.layer;
            if (botObj.layer >= 0) return botObj.layer;
            return 0;
        }

        private static bool ValidateHatAttachment(GameObject botObj, Transform hatPos, out int renderersFound, out int activeRenderers, out int hatLayer)
        {
            renderersFound = 0;
            activeRenderers = 0;
            hatLayer = -1;
            if (botObj == null || hatPos == null) return false;
            var hats = hatPos.GetComponentsInChildren<HatPosition>(true);
            if (hats == null || hats.Length == 0) return false;
            var setup = botObj.GetComponent<PlayerSetup>();
            int visualLayer = GetBotVisualLayer(botObj);
            bool anyVisible = false;
            for (int i = 0; i < hats.Length; i++)
            {
                var hp = hats[i];
                if (hp == null || hp.gameObject == null) continue;
                bool isHatObject = (setup != null && setup.hat == hp.gameObject)
                    || hp.gameObject.name.StartsWith("BOT_HAT_", System.StringComparison.OrdinalIgnoreCase);
                if (!isHatObject) continue;
                hatLayer = hp.gameObject.layer;
                if (hp.reference != hatPos) hp.reference = hatPos;
                foreach (var r in hp.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    renderersFound++;
                    if (!r.enabled) r.enabled = true;
                    if (r.gameObject.layer != visualLayer) r.gameObject.layer = visualLayer;
                    if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                    r.forceRenderingOff = false;
                    if (r.enabled && r.gameObject.activeInHierarchy) { activeRenderers++; anyVisible = true; }
                }
            }
            return anyVisible;
        }

        private static bool HasVisibleHat(GameObject botObj)
        {
            if (botObj == null) return false;
            var setup = botObj.GetComponent<PlayerSetup>();
            Transform anchor = ResolveHatAnchor(botObj, setup, true);
            if (anchor == null) return false;
            bool visible = ValidateHatAttachment(botObj, anchor, out _, out _, out _);
            if (!visible && setup != null && setup.hat != null)
            {
                foreach (var r in setup.hat.GetComponentsInChildren<Renderer>(true))
                {
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy) return true;
                }
            }
            return visible;
        }

        private static GameObject ResolveHatPrefab(BotData botData)
        {
            try
            {
                var cosmetics = CosmeticsManager.Instance;
                if (cosmetics == null) return null;

                GameObject[] hats = GetHatPrefabs(cosmetics);
                // Brute-force reliability mode: pick a clearly visible hat prefab first.
                // This guarantees "hat visible" over per-bot cosmetic variety.
                GameObject forcedVisible = FindUsableHatPrefab(hats, botData);
                if (forcedVisible != null) return forcedVisible;

                if (botData != null && hats != null
                    && botData.HatIndex >= 0 && botData.HatIndex < hats.Length)
                {
                    GameObject selected = hats[botData.HatIndex];
                    if (IsUsableHatPrefab(selected))
                        return selected;
                }

                GameObject fallback = FindUsableHatPrefab(hats, botData);
                if (fallback != null) return fallback;

                FieldInfo currentHatField = typeof(CosmeticsManager).GetField("currenthat",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                GameObject current = currentHatField?.GetValue(cosmetics) as GameObject;
                if (IsUsableHatPrefab(current)) return current;

                foreach (var cosmetic in Object.FindObjectsOfType<CosmeticInstance>(true))
                {
                    if (cosmetic == null || !cosmetic.isHat || cosmetic.hat == null) continue;
                    if (!IsUsableHatPrefab(cosmetic.hat)) continue;
                    if (botData != null) botData.HatIndex = cosmetic.index;
                    return cosmetic.hat;
                }
            }
            catch { return null; }
            return null;
        }

        /// <summary>Pick the bot's cig prefab, skipping placeholder entries ("nothing"
        /// options) and prefabs with no renderers. Advances CigIndex to the used one.</summary>
        private static GameObject ResolveCigPrefab(BotData botData)
        {
            try
            {
                var cigs = CosmeticsManager.Instance != null ? CosmeticsManager.Instance.cigs : null;
                if (cigs == null || cigs.Length == 0 || botData == null) return null;

                int start = Mathf.Clamp(botData.CigIndex, 0, cigs.Length - 1);
                for (int offset = 0; offset < cigs.Length; offset++)
                {
                    int index = (start + offset) % cigs.Length;
                    GameObject candidate = cigs[index];
                    if (candidate == null) continue;
                    string n = candidate.name != null ? candidate.name.ToLowerInvariant() : "";
                    if (n.Contains("nothing") || n.Contains("none") || n.Contains("empty")) continue;
                    bool hasRenderer = false;
                    foreach (var r in candidate.GetComponentsInChildren<Renderer>(true))
                        if (r != null) { hasRenderer = true; break; }
                    if (!hasRenderer) continue;
                    botData.CigIndex = index;
                    return candidate;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Guard against "giant white slab" cosmetics — but NORMALIZE instead of
        /// destroy, and only after skinning/transforms settle. The old same-frame destroy
        /// false-positived on the game's own pipe/cigar prefabs (bounds read 13-21m at
        /// the instantiation frame) and silently deleted every cig.</summary>
        private static bool SanityCheckCosmeticSize(GameObject inst, string kind)
        {
            if (inst == null) return false;
            try
            {
                var renderers = inst.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0)
                {
                    Plugin.Log.LogWarning($"[BOT] Discarded {kind} cosmetic '{inst.name}' — no renderers");
                    Object.Destroy(inst);
                    return false;
                }
                if (Instance != null)
                    Instance.StartCoroutine(NormalizeCosmeticSizeDeferred(inst, kind));
            }
            catch { }
            return true;
        }

        private static System.Collections.IEnumerator NormalizeCosmeticSizeDeferred(GameObject inst, string kind)
        {
            // Two frames: lets SkinnedMeshRenderer bounds and the transform hierarchy
            // settle before measuring — same-frame bounds are garbage for these prefabs.
            yield return null;
            yield return null;
            if (inst == null) yield break;
            float budget = kind == "hat" ? 0.9f : 0.55f;
            try
            {
                var renderers = inst.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0) yield break;
                Bounds b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                    if (renderers[i] != null) b.Encapsulate(renderers[i].bounds);
                float largest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (largest > budget && largest > 0.001f)
                {
                    float k = budget / largest;
                    inst.transform.localScale *= k;
                    Plugin.Log.LogInfo($"[BotCosmetics] Normalized {kind} '{inst.name}' " +
                        $"from {b.size.x:F1}x{b.size.y:F1}x{b.size.z:F1} (scale x{k:F3})");
                }
            }
            finally { }
        }

        private static Transform ResolveHatAnchor(GameObject botObj, PlayerSetup setup, bool preferHeadAnchor)
        {
            // The game's own pivot ("HatPivot" on the IK prefab, hatToWearPosition) is
            // authoritative — hats/cigs snap to it every frame via HatPosition. It can
            // ship DEACTIVATED (the game hides the local player's own hat that way), so
            // accept it inactive and switch it on. Requiring activeInHierarchy here
            // silently rejected the real pivot and dumped bot cosmetics onto a guessed
            // head-bone anchor at the wrong offset — buried inside the skull.
            if (setup != null && setup.hatToWearPosition != null)
            {
                var pivot = setup.hatToWearPosition;
                if (!pivot.gameObject.activeSelf) pivot.gameObject.SetActive(true);
                // An INACTIVE ANCESTOR still makes everything under the pivot invisible
                // no matter what we activate here — fall back to the head-bone anchor
                // (always in the active rig) and say which ancestor was the problem.
                if (!pivot.gameObject.activeInHierarchy)
                {
                    Transform t = pivot.parent;
                    while (t != null && t.gameObject.activeSelf) t = t.parent;
                    Plugin.Log.LogWarning($"[BotCosmetics] game pivot '{pivot.name}' is under inactive ancestor " +
                        $"'{(t != null ? t.name : "?")}' — using head-bone anchor instead");
                }
                else
                {
                    return pivot;
                }
            }

            if (botObj != null && preferHeadAnchor)
            {
                Transform headAnchor = GetOrCreateHeadHatAnchor(botObj);
                if (headAnchor != null)
                    return headAnchor;
            }

            if (botObj != null && !preferHeadAnchor)
            {
                Transform headAnchor = GetOrCreateHeadHatAnchor(botObj);
                if (headAnchor != null)
                    return headAnchor;
            }

            if (botObj == null) return null;
            foreach (var t in botObj.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                string n = t.name != null ? t.name.ToLowerInvariant() : "";
                if (n.Contains("hattowear") || n.Contains("hat_to_wear") || n == "hat" || n.Contains("headhat"))
                    return t;
            }
            return null;
        }

        private static Transform GetOrCreateHeadHatAnchor(GameObject botObj)
        {
            if (botObj == null) return null;
            var animator = botObj.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                if (head != null)
                {
                    var anchor = head.Find("BotHatAnchor");
                    if (anchor == null)
                    {
                        var go = new GameObject("BotHatAnchor");
                        anchor = go.transform;
                        anchor.SetParent(head, false);
                        // Game's HatPivot local offset on the head bone (PlayerIK.prefab)
                        // — the old (0, 0.06, 0) sat inside the skull.
                        anchor.localPosition = new Vector3(-0.0106f, 0.4386f, 0.0407f);
                        anchor.localRotation = Quaternion.identity;
                    }
                    return anchor;
                }
            }

            // Non-humanoid rigs: find a likely head transform by name and attach there.
            Transform namedHead = FindNamedHeadTransform(botObj);
            if (namedHead != null)
            {
                var anchor = namedHead.Find("BotHatAnchor");
                if (anchor == null)
                {
                    var go = new GameObject("BotHatAnchor");
                    anchor = go.transform;
                    anchor.SetParent(namedHead, false);
                    anchor.localPosition = new Vector3(0f, 0.05f, 0f);
                    anchor.localRotation = Quaternion.identity;
                }
                return anchor;
            }
            return null;
        }

        private static Transform FindNamedHeadTransform(GameObject botObj)
        {
            if (botObj == null) return null;
            Transform best = null;
            float bestScore = float.MinValue;
            Vector3 rootPos = botObj.transform.position;
            foreach (var t in botObj.GetComponentsInChildren<Transform>(true))
            {
                if (t == null) continue;
                string n = t.name != null ? t.name.ToLowerInvariant() : "";
                if (!n.Contains("head")) continue;
                if (n.Contains("camera")) continue;
                float upScore = t.position.y - rootPos.y;
                float forwardScore = Vector3.Dot(botObj.transform.forward, (t.position - rootPos).normalized) * 0.1f;
                float score = upScore + forwardScore;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = t;
                }
            }
            return best;
        }


        private static GameObject[] GetHatPrefabs(CosmeticsManager cosmetics)
        {
            if (cosmetics == null) return null;
            if (cosmetics.hats != null && cosmetics.hats.Length > 0)
                return cosmetics.hats;

            try
            {
                CosmeticInstance[] children = null;

                FieldInfo hatsChildrenField = typeof(CosmeticsManager).GetField("hatsChildren",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (hatsChildrenField != null)
                    children = hatsChildrenField.GetValue(cosmetics) as CosmeticInstance[];

                if (children == null || children.Length == 0)
                {
                    FieldInfo hatsParentField = typeof(CosmeticsManager).GetField("hatsParent",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    Transform hatsParent = hatsParentField?.GetValue(cosmetics) as Transform;
                    if (hatsParent != null)
                        children = hatsParent.GetComponentsInChildren<CosmeticInstance>(true);
                }

                if (children == null || children.Length == 0) return cosmetics.hats;

                var hats = new List<GameObject>();
                for (int i = 0; i < children.Length; i++)
                {
                    var cosmetic = children[i];
                    if (cosmetic == null || !cosmetic.isHat || cosmetic.hat == null) continue;
                    cosmetic.index = hats.Count;
                    hats.Add(cosmetic.hat);
                }

                if (hats.Count == 0) return cosmetics.hats;
                cosmetics.hats = hats.ToArray();
                return cosmetics.hats;
            }
            catch
            {
                return cosmetics.hats;
            }
        }

        /// <summary>
        /// Return the number of usable hat prefabs currently discoverable.
        /// Uses the same discovery path as ApplyAllCosmetics so randomization
        /// doesn't end up with HatIndex = -1 on maps/lobbies where hats are
        /// only available via children/reflection.
        /// </summary>
        public static int GetAvailableHatCount()
        {
            try
            {
                var cosmetics = CosmeticsManager.Instance;
                if (cosmetics == null) return 0;
                var hats = GetHatPrefabs(cosmetics);
                if (hats == null || hats.Length == 0) return 0;

                int count = 0;
                for (int i = 0; i < hats.Length; i++)
                {
                    if (IsUsableHatPrefab(hats[i])) count++;
                }
                return count;
            }
            catch { return 0; }
        }

        private static GameObject FindUsableHatPrefab(GameObject[] hats, BotData botData)
        {
            if (hats == null || hats.Length == 0) return null;

            int start = botData != null && botData.HatIndex >= 0 ? botData.HatIndex + 1 : 0;
            // Pass 1: strongly prefer obvious hat/cap style prefabs (avoid subtle head replacements).
            for (int offset = 0; offset < hats.Length; offset++)
            {
                int index = (start + offset) % hats.Length;
                GameObject candidate = hats[index];
                if (!IsUsableHatPrefab(candidate)) continue;
                if (!IsPreferredVisibleHat(candidate)) continue;
                if (botData != null) botData.HatIndex = index;
                return candidate;
            }

            // Pass 2: fallback to any usable prefab.
            for (int offset = 0; offset < hats.Length; offset++)
            {
                int index = (start + offset) % hats.Length;
                GameObject candidate = hats[index];
                if (!IsUsableHatPrefab(candidate)) continue;
                if (botData != null) botData.HatIndex = index;
                return candidate;
            }
            return null;
        }

        private static bool IsUsableHatPrefab(GameObject prefab)
        {
            if (prefab == null) return false;
            string name = prefab.name != null ? prefab.name.ToLowerInvariant() : "";
            if (name.Contains("nothing")) return false;
            if (name.Contains("_head") || name.EndsWith("head")) return false;

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null) return true;
            }
            return false;
        }

        private static bool IsPreferredVisibleHat(GameObject prefab)
        {
            if (prefab == null) return false;
            string n = prefab.name != null ? prefab.name.ToLowerInvariant() : "";
            if (n.Contains("_head") || n.EndsWith("head")) return false;
            return n.Contains("hat")
                || n.Contains("cap")
                || n.Contains("helmet")
                || n.Contains("crown")
                || n.Contains("band")
                || n.Contains("docker")
                || n.Contains("spike")
                || n.Contains("cow")
                || n.Contains("bunny");
        }

        private static void PrepareCosmeticInstance(GameObject obj, Transform hatPos, bool isHat, int visualLayer)
        {
            if (obj == null || hatPos == null) return;
            // ROOT CAUSE of invisible hats: hat prefabs carry a FishNet NetworkObject
            // (verified in PF_BeerHat_00.prefab); FishNet deactivates an instantiated
            // but never-spawned NetworkObject moments later — the probe showed every
            // hat flip to activeInHierarchy=False within 0.35s of each dress while
            // NetworkObject-less cigs on the same pivot survived. The game's own
            // preview does exactly this (AboubiPreview.ChangeDress).
            var netObj = obj.GetComponent<FishNet.Object.NetworkObject>();
            if (netObj != null) Object.Destroy(netObj);
            if (obj.transform.parent != hatPos)
                obj.transform.SetParent(hatPos, false);
            // Keep authored prefab local offsets/rotation/scale — many hats rely on these.
            if (isHat && obj.transform.localPosition.sqrMagnitude < 0.0004f)
            {
                // Some hat prefabs instantiate at origin and clip into the head on bot rigs.
                // Nudge upward so hats are visibly above the scalp.
                obj.transform.localPosition = new Vector3(0f, 0.11f, 0f);
            }

            // Hats share the bot's rendered body layer instead of the game's hat layer
            // (18): every spawn-time diagnostic read healthy on 18 yet hats never showed,
            // while cigs on the body layer take the identical path. Colliders are
            // disabled, so body-layer raycasts can't hit them.
            int layerToUse = visualLayer;
            foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = layerToUse;
            if (isHat)
                obj.tag = "Hat";
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
            {
                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                if (renderer is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
            }
            foreach (var col in obj.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
            foreach (var rb in obj.GetComponentsInChildren<Rigidbody>(true))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        private static void FixCosmeticVisibility(Transform hatPos, int visualLayer)
        {
            if (hatPos == null) return;
            foreach (var hp in hatPos.GetComponentsInChildren<HatPosition>(true))
            {
                if (hp == null) continue;
                hp.reference = hatPos;
                GameObject obj = hp.gameObject;
                obj.SetActive(true);
                bool isHatObject = obj.name.StartsWith("BOT_HAT_", System.StringComparison.OrdinalIgnoreCase);
                int layerToUse = visualLayer; // hats render on the body layer now, same as cigs
                if (isHatObject) obj.tag = "Hat";
                foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = layerToUse;
                foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
                {
                    renderer.enabled = true;
                    renderer.forceRenderingOff = false;
                    if (renderer is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                }
                foreach (var col in obj.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
            }
        }

        /// <summary>Destroy old hat/cig children on hatToWearPosition before applying new ones.</summary>
        private static void CleanupOldCosmetics(Transform hatPos)
        {
            for (int i = hatPos.childCount - 1; i >= 0; i--)
            {
                var child = hatPos.GetChild(i);
                if (child.GetComponent<HatPosition>() != null)
                    Object.Destroy(child.gameObject);
            }
        }

        private static void CleanupOrphanedCosmetics()
        {
            try
            {
                foreach (var hp in Object.FindObjectsOfType<HatPosition>(true))
                {
                    if (hp == null) continue;
                    if (hp.transform.parent != null && hp.reference != null && hp.transform.IsChildOf(hp.reference)) continue;
                    Object.Destroy(hp.gameObject);
                }
            }
            catch { }
        }

        private static void TryApplyHatViaPlayerSetup(PlayerSetup setup, int hatIndex)
        {
            if (setup == null || hatIndex < 0) return;
            try
            {
                var t = typeof(PlayerSetup);
                MethodInfo setHat = t.GetMethod("SetHat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(bool) }, null);
                if (setHat != null)
                {
                    setHat.Invoke(setup, new object[] { hatIndex, true });
                    if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                        Plugin.Log.LogInfo($"[HAT] Native SetHat(int,bool) invoked hatIndex={hatIndex}");
                    return;
                }
                MethodInfo setHatOneArg = t.GetMethod("SetHat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
                if (setHatOneArg != null)
                {
                    setHatOneArg.Invoke(setup, new object[] { hatIndex });
                    if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                        Plugin.Log.LogInfo($"[HAT] Native SetHat(int) invoked hatIndex={hatIndex}");
                    return;
                }

                MethodInfo changeHat = t.GetMethod("ChangeHat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(bool) }, null);
                if (changeHat != null)
                {
                    changeHat.Invoke(setup, new object[] { hatIndex, true });
                    if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                        Plugin.Log.LogInfo($"[HAT] Native ChangeHat(int,bool) invoked hatIndex={hatIndex}");
                    return;
                }
                MethodInfo changeHatOneArg = t.GetMethod("ChangeHat", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
                if (changeHatOneArg != null)
                {
                    changeHatOneArg.Invoke(setup, new object[] { hatIndex });
                    if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                        Plugin.Log.LogInfo($"[HAT] Native ChangeHat(int) invoked hatIndex={hatIndex}");
                    return;
                }

                MethodInfo changeDress = t.GetMethod("ChangeDress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (changeDress != null && changeDress.GetParameters().Length == 0)
                {
                    changeDress.Invoke(setup, null);
                    if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                        Plugin.Log.LogInfo("[HAT] Native ChangeDress() invoked");
                    return;
                }

                if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                    Plugin.Log.LogWarning("[HAT] No native PlayerSetup hat method found");
            }
            catch (System.Exception e)
            {
                if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                    Plugin.Log.LogWarning($"[HAT] Native hat apply exception: {e.Message}");
            }
        }

        private static bool TryApplyChangeDressDirect(PlayerSetup setup, GameObject playerObj, GameObject hatPrefab)
        {
            if (setup == null || playerObj == null) return false;
            try
            {
                MethodInfo m = typeof(PlayerSetup).GetMethod("ChangeDress",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(GameObject), typeof(GameObject), typeof(Vector3) },
                    null);
                if (m == null) return false;
                m.Invoke(setup, new object[] { playerObj, hatPrefab, playerObj.transform.forward });
                if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                    Plugin.Log.LogInfo("[HAT] Native ChangeDress(player,hat,direction) invoked");
                return true;
            }
            catch (System.Exception e)
            {
                if (Plugin.EnableReliabilityLogs != null && Plugin.EnableReliabilityLogs.Value)
                    Plugin.Log.LogWarning($"[HAT] Native ChangeDress(player,hat,direction) failed: {e.Message}");
                return false;
            }
        }

        private static Material GetSuitMaterial(int suitIndex)
        {
            if (CosmeticsManager.Instance == null) return null;
            if (CosmeticsManager.Instance.mats == null) return null;
            if (suitIndex < 0 || suitIndex >= CosmeticsManager.Instance.mats.Length) return null;
            return CosmeticsManager.Instance.mats[suitIndex];
        }

        public void RespawnAllBots()
        {
            SpawnPoint[] spawns = FindSpawnPoints();
            if (spawns.Length == 0) return;

            int idx = 0;
            foreach (var bot in _activeBots)
            {
                if (bot == null) continue;
                bot.Respawn(GetDistributedSpawnPosition(spawns, idx), reapplyCosmetics: false);
                var botData = LobbyBots.Find(b => b.Controller == bot);
                if (botData != null) ReapplyCosmetics(botData, bot.gameObject);
                idx++;
            }
        }

        /// <summary>
        /// Training respawn: replace a dead bot with a FRESH object — the same path the
        /// round-start / "Spawn Bots Now" flows use, which always produces clean bots.
        /// In-place resurrection of the dead object kept leaking death state (white
        /// slabs, animator/material remnants) no matter how much of it we reset.
        /// </summary>
        public void RespawnBotFresh(BotController oldBot, Vector3 position)
        {
            if (oldBot == null) return;
            var botData = LobbyBots.Find(b => b.Controller == oldBot
                || b.PlayerObject == oldBot.gameObject
                || b.BotId == oldBot.BotId);

            // Tear the old object down exactly like DespawnAllBots does for one bot
            int pid = oldBot.PlayerId;
            if (ClientInstance.playerInstances.ContainsKey(pid))
                ClientInstance.playerInstances.Remove(pid);
            NetworkObject oldNob = oldBot.GetComponent<NetworkObject>();
            if (oldNob != null && SteamLobby.Instance != null)
                SteamLobby.Instance.players.Remove(oldNob);
            if (GameManager.Instance != null)
                GameManager.Instance.alivePlayers.Remove(pid);
            _activeBots.Remove(oldBot);
            if (oldNob != null && oldNob.IsSpawned)
            {
                try { InstanceFinder.ServerManager.Despawn(oldNob); }
                catch { Destroy(oldBot.gameObject); }
            }
            else
            {
                Destroy(oldBot.gameObject);
            }

            if (botData == null)
            {
                Plugin.Log.LogWarning("[BOT] RespawnBotFresh: no BotData found for dead bot");
                return;
            }
            botData.Controller = null;
            botData.PlayerObject = null;

            // Fresh spawn — identical sequence to SpawnAllBots
            GameObject botObj = CreateBot(botData, position);
            if (botObj == null) return;

            BotController controller = botObj.GetComponent<BotController>();
            if (controller == null)
                controller = botObj.AddComponent<BotController>();
            controller.BotId = botData.BotId;
            controller.SkillSlot = botData.SlotIndex;
            controller.BotName = botData.Name;
            controller.PlayerId = botData.PlayerId;
            botData.Controller = controller;
            botData.PlayerObject = botObj;
            _activeBots.Add(controller);
            RegisterBotAsPlayer(botData, botObj);
            Plugin.Log.LogInfo($"[BOT] Fresh training respawn: {botData.Name} at {position}");
        }

        public void DespawnAllBots()
        {
            foreach (var bot in _activeBots)
            {
                if (bot == null || bot.gameObject == null) continue;

                // Clean up from game systems
                int pid = bot.PlayerId;
                if (ClientInstance.playerInstances.ContainsKey(pid))
                    ClientInstance.playerInstances.Remove(pid);

                NetworkObject nob = bot.GetComponent<NetworkObject>();
                if (nob != null && SteamLobby.Instance != null)
                    SteamLobby.Instance.players.Remove(nob);

                if (GameManager.Instance != null)
                    GameManager.Instance.alivePlayers.Remove(pid);

                // Despawn
                if (nob != null && nob.IsSpawned)
                {
                    try { InstanceFinder.ServerManager.Despawn(nob); }
                    catch { Destroy(bot.gameObject); }
                }
                else
                    Destroy(bot.gameObject);
            }
            _activeBots.Clear();

            // Reset PlayerId assignments
            foreach (var bot in LobbyBots)
                bot.PlayerId = -1;
        }

        public void ResetDrawTimer()
        {
            _onlyBotsAliveTimer = 0f;
            _stuckRoundTimer = 0f;
            _botWinConfirmTimer = 0f;
            _takeAdvancePending = false;
            _takeAdvancePendingTimer = 0f;
        }

        public List<BotController> GetActiveBots() => _activeBots;
        public static List<BotController> ActiveBots => Instance?._activeBots;

        private void SetLayerRecursive(GameObject obj, int layer)
        {
            obj.layer = layer;
            foreach (Transform child in obj.transform)
                SetLayerRecursive(child.gameObject, layer);
        }
    }
}
