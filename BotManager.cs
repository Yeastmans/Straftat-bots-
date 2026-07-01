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
        private List<BotController> _activeBots = new List<BotController>();
        private int _nextBotId;
        private GameObject _cachedPrefab;
        private float _onlyBotsAliveTimer;
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
                                Plugin.Log.LogInfo($"[BOT] Demo-needed edge near player at {mid:F1} — bots keep failing this jump, consider a Watch-Me demo");
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

            // Training mode: never end the round — bots need uninterrupted time
            bool trainingMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;
            if (trainingMode) return;

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
            BotData bot = BotData.CreateRandom(_nextBotId++);
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
                ApplyAllCosmetics(botObj, botData);
                if (HasVisibleHat(botObj)) yield break;
            }
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
        public void ReapplyCosmetics(BotData botData, GameObject botObj)
        {
            if (botObj == null) return;
            // Randomize cosmetics each round
            botData.RandomizeCosmetics();
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
            ReapplyCosmetics(botData, controller.gameObject);
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
                var setup = botObj.GetComponent<PlayerSetup>();
                if (botData != null)
                {
                    botData.HatIndex = -1; // Disable hats entirely.
                    // Disable cigs too: the instantiated cig prefab rendered as a white untextured
                    // slab on bots (especially after a training respawn). Bots wear suit-only now.
                    // The cig block below is gated on CigIndex >= 0, so this skips creation; the
                    // CleanupOldCosmetics call still removes any cig left over from a prior life.
                    botData.CigIndex = -1;
                }

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
                if (hatPos == null) return;
                // Bots wear suit-only. Keep the cosmetic mount DEACTIVATED so any hat/cig the
                // game's ChangeDress creates underneath it stays hidden (they render as white
                // untextured slabs on bots). A ChangeDress postfix also strips them at the source.
                hatPos.gameObject.SetActive(false);
                Transform hatMount = hatPos;
                if (setup != null) setup.cig = botData.CigIndex;

                // Destroy old hat/cig instances before creating new ones
                CleanupOldCosmetics(hatMount);
                if (setup != null) setup.hat = null;

                // Hats removed: keep suit + cig only.
                CleanupOrphanedCosmetics();
                bool usedNativeDress = false;

                int visualLayer = GetBotVisualLayer(botObj);

                // Cig/pipe/cigar — same as ChangeDress
                if (!usedNativeDress && CosmeticsManager.Instance?.cigs != null
                    && botData.CigIndex >= 0 && botData.CigIndex < CosmeticsManager.Instance.cigs.Length)
                {
                    GameObject cigPrefab = CosmeticsManager.Instance.cigs[botData.CigIndex];
                    if (cigPrefab != null)
                    {
                        var cigInst = Object.Instantiate(cigPrefab, hatMount.position, Quaternion.identity, hatMount);
                        cigInst.AddComponent<HatPosition>().reference = hatMount;
                        PrepareCosmeticInstance(cigInst, hatMount, false, visualLayer);
                        cigInst.SetActive(true);
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
                    if (r.gameObject.layer != 18) r.gameObject.layer = 18;
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

        private static void InvokeSyncSetter(PlayerSetup setup, string methodName, int value)
        {
            try
            {
                MethodInfo setter = typeof(PlayerSetup).GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(int), typeof(bool) }, null);
                if (setter != null)
                    setter.Invoke(setup, new object[] { value, true });
            }
            catch { }
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

        private static Transform ResolveHatAnchor(GameObject botObj, PlayerSetup setup, bool preferHeadAnchor)
        {
            if (setup != null && setup.hatToWearPosition != null && setup.hatToWearPosition.gameObject.activeInHierarchy)
                return setup.hatToWearPosition;

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
                        anchor.localPosition = new Vector3(0f, 0.06f, 0f);
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
            if (obj.transform.parent != hatPos)
                obj.transform.SetParent(hatPos, false);
            // Keep authored prefab local offsets/rotation/scale — many hats rely on these.
            if (isHat && obj.transform.localPosition.sqrMagnitude < 0.0004f)
            {
                // Some hat prefabs instantiate at origin and clip into the head on bot rigs.
                // Nudge upward so hats are visibly above the scalp.
                obj.transform.localPosition = new Vector3(0f, 0.11f, 0f);
            }

            int layerToUse = isHat ? 18 : visualLayer;
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
                int layerToUse = isHatObject ? 18 : visualLayer;
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
