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
            foreach (var bot in _activeBots)
            {
                if (bot != null && !bot.IsDead)
                    anyBotAlive = true;
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

            // Training mode: never end the round — bots need uninterrupted time
            bool trainingMode = NavGraph.Instance != null && NavGraph.Instance.Mode == NavMode.Training;
            if (trainingMode) return;

            // Nobody alive at all — round is stuck, force progression
            if (!anyHumanAlive && !anyBotAlive)
            {
                _stuckRoundTimer += 0.5f;
                if (_stuckRoundTimer >= 5f)
                {
                    _stuckRoundTimer = 0f;
                    Plugin.Log.LogInfo("[BOT] No one alive for 5s, forcing round end");
                    try
                    {
                        // Credit score to host team
                        int hostTeamId = ScoreManager.Instance.GetTeamId(0);
                        ScoreManager.Instance.AddRoundScore(hostTeamId);

                        // Clear stuck waitForDrawCoroutine
                        if (_wfdFieldCache == null) _wfdFieldCache = typeof(GameManager).GetField("waitForDrawCoroutine",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (_wfdFieldCache != null) _wfdFieldCache.SetValue(GameManager.Instance, null);

                        // Repopulate alivePlayers
                        GameManager.Instance.alivePlayers.Clear();
                        foreach (var ci in ClientInstance.playerInstances.Values)
                            GameManager.Instance.alivePlayers.Add(ci.PlayerId);

                        bool isRoundWon = ScoreManager.Instance.CheckForRoundWin(out int winningTeamId);
                        if (isRoundWon)
                        {
                            ScoreManager.Instance.ResetRound();
                            ScoreManager.Instance.AddPoints(winningTeamId);
                            SceneMotor.Instance.ChangeNetworkScene();
                        }
                        else
                        {
                            GameManager.Instance.ProgressToNextTake();
                        }
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"[BOT] Force round end error: {e.Message}");
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

                // Apply all cosmetics (suit + hat + cig) directly — no RPC needed on host
                ApplyAllCosmetics(botObj, botData);

                // Set name tag
                SetNameTag(botObj, botData.Name);

                // Sync skin + hat + cig to non-host clients via Mycelium
                if (nob != null)
                    StartCoroutine(DelaySkinSync((int)nob.ObjectId, botData.SuitIndex, botData.HatIndex, botData.CigIndex));

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

        private System.Collections.IEnumerator DelaySkinSync(int netId, int suitIndex, int hatIndex = -1, int cigIndex = 0)
        {
            // Wait for bot to exist on non-host clients, then send + retry
            yield return new WaitForSeconds(1.5f);
            BotDamageSync.SyncSkin(netId, suitIndex, hatIndex, cigIndex);
            yield return new WaitForSeconds(3f);
            BotDamageSync.SyncSkin(netId, suitIndex, hatIndex, cigIndex); // Retry in case first was too early
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
            if (nob != null)
                StartCoroutine(DelaySkinSync((int)nob.ObjectId, botData.SuitIndex, botData.HatIndex, botData.CigIndex));
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

                if (setup == null || setup.hatToWearPosition == null) return;
                Transform hatPos = setup.hatToWearPosition;
                hatPos.gameObject.SetActive(true);
                hatPos.gameObject.layer = 0; // Ensure hat position is on default layer
                setup.cig = botData.CigIndex;

                // Destroy old hat/cig instances before creating new ones
                CleanupOldCosmetics(hatPos);
                setup.hat = null;

                GameObject hatPrefab = ResolveHatPrefab(botData);

                // Hat — direct local cosmetic only. Do not call PlayerSetup.ChangeDress for bots:
                // that path is an ObserversRpc and can leave detached world cosmetics behind.
                if (hatPrefab != null)
                {
                    var hatInst = Object.Instantiate(hatPrefab, hatPos.position, Quaternion.identity, hatPos);
                    hatInst.AddComponent<HatPosition>().reference = hatPos;
                    hatInst.tag = "Hat";
                    PrepareCosmeticInstance(hatInst, hatPos, true);
                    hatInst.transform.forward = botObj.transform.forward;
                    hatInst.SetActive(true);
                    setup.hat = hatInst;
                }

                // Cig/pipe/cigar — same as ChangeDress
                if (CosmeticsManager.Instance?.cigs != null
                    && botData.CigIndex >= 0 && botData.CigIndex < CosmeticsManager.Instance.cigs.Length)
                {
                    GameObject cigPrefab = CosmeticsManager.Instance.cigs[botData.CigIndex];
                    if (cigPrefab != null)
                    {
                        var cigInst = Object.Instantiate(cigPrefab, hatPos.position, Quaternion.identity, hatPos);
                        cigInst.AddComponent<HatPosition>().reference = hatPos;
                        PrepareCosmeticInstance(cigInst, hatPos, false);
                        cigInst.SetActive(true);
                    }
                }

                FixCosmeticVisibility(hatPos);
                Plugin.Log.LogInfo($"Cosmetics applied: suit={botData.SuitIndex} hat={botData.HatIndex} ({(hatPrefab != null ? hatPrefab.name : "none")}) cig={botData.CigIndex}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"ApplyAllCosmetics: {e.Message}"); }
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

        private static GameObject FindUsableHatPrefab(GameObject[] hats, BotData botData)
        {
            if (hats == null || hats.Length == 0) return null;

            int start = botData != null && botData.HatIndex >= 0 ? botData.HatIndex + 1 : 0;
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

            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer != null) return true;
            }
            return false;
        }

        private static void PrepareCosmeticInstance(GameObject obj, Transform hatPos, bool isHat)
        {
            if (obj == null || hatPos == null) return;
            obj.transform.SetParent(hatPos, false);
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;

            if (isHat)
            {
                // Bot cosmetics render with the bot body. Layer 18 hides some hat roots
                // from the spectator/freecam path, especially when the renderer is on root.
                foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = 11;
            }
            else
            {
                foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
                    child.gameObject.layer = 6;
            }
            foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;
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

        private static void FixCosmeticVisibility(Transform hatPos)
        {
            if (hatPos == null) return;
            foreach (var hp in hatPos.GetComponentsInChildren<HatPosition>(true))
            {
                if (hp == null) continue;
                hp.reference = hatPos;
                GameObject obj = hp.gameObject;
                obj.SetActive(true);
                bool isHat = obj.CompareTag("Hat");
                if (isHat)
                {
                    foreach (Transform child in obj.GetComponentsInChildren<Transform>(true))
                        child.gameObject.layer = 11;
                }
                foreach (var renderer in obj.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = true;
                foreach (var col in obj.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
            }
        }

        /// <summary>Destroy old hat/cig children on hatToWearPosition before applying new ones.</summary>
        private static void CleanupOldCosmetics(Transform hatPos)
        {
            CleanupOrphanedCosmetics();
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
                bot.Respawn(GetDistributedSpawnPosition(spawns, idx));
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

        public void ResetDrawTimer() => _onlyBotsAliveTimer = 0f;

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
