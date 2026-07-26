using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace StraftatBots
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("kestrel.straftat.modmenu", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.modder.straftatbots";
        public const string PluginName = "Straftat Bots";
        public const string PluginVersion = "2.0.0";

        internal static ManualLogSource Log;

        // ===== Bots =====
        public static ConfigEntry<int> MaxBots;
        public static ConfigEntry<int> DrawTimer;
        public static ConfigEntry<string> NavGraphMode;

        // ===== Gameplay =====
        public static ConfigEntry<bool> LockGraph;
        public static ConfigEntry<bool> UseNavMesh;

        // ===== Training =====
        // Behavior is STAGE-DRIVEN now (Explore for stages 1-2, Validate for stage 3,
        // None in Play or when paused) — no user knob, so it is deliberately NOT a
        // ConfigEntry and never appears in the mod menu.
        public class BehaviorSetting { public string Value = "Explore"; }
        public static readonly BehaviorSetting TrainingBehavior = new BehaviorSetting();

        // Helper properties
        public static bool IsExploreMode => TrainingBehavior?.Value == "Explore";
        public static bool IsValidateMode => TrainingBehavior?.Value == "Validate";
        public static bool IsTrainingNone => TrainingBehavior?.Value == "None";
        // Pause flag — the one manual control left in training (walk the map yourself)
        public static bool TrainingPaused;

        // ===== Customise =====
        public static ConfigEntry<string>[] BotNames = new ConfigEntry<string>[8];
        // Per-bot skill 1-10: scales aim error, reaction time, lock-on speed,
        // detection range, fire discipline and dodge rate. 5 = the old fixed tuning.
        public static ConfigEntry<int>[] BotSkills = new ConfigEntry<int>[8];

        public static int GetBotSkill(int slot)
        {
            slot = ((slot % 8) + 8) % 8;
            return BotSkills[slot]?.Value ?? 5;
        }

        // ===== Patrol Locations =====
        public static List<(Vector3 pos, int nodeId)> CustomPatrolLocations = new List<(Vector3, int)>();

        // ===== Blacklist =====
        public static HashSet<int> BlacklistedWeaponNodes = new HashSet<int>();

        // ===== Debug =====
        // Single overlay toggle — covers nodes, edges, bot paths, markers, and info text.
        public static ConfigEntry<bool> ShowOverlay;
        public static ConfigEntry<bool> ShowCoverageMap;
        public static ConfigEntry<bool> ShowMeshDebug;
        // One-line perf summary every 10s (fps, bot CPU ms/frame, GC pressure).
        public static ConfigEntry<bool> LogPerfStats;
        // Intentionally not bound to config anymore; optional diagnostic logs stay quiet by default.
        public static ConfigEntry<bool> EnableReliabilityLogs;

        // ===== Hardcoded defaults (formerly user-facing) =====
        // The old sliders/toggles were removed in favor of "works all the time" defaults.
        public const float NODE_DENSITY_MULT = 1f;      // was NodeDensity slider level 5
        public const float SCAN_RADIUS       = 8f;      // was ScanRadius slider
        public const int   MAX_NODES         = 5000;    // was MaxNodes slider
        public const float AUTO_SAVE_SEC     = 30f;     // was AutoSaveInterval slider
        public const float MAX_JUMP_DIST     = 12f;     // physics-derived sprint-jump + margin

        private void Awake()
        {
            Log = Logger;

            // ================================================================
            //  BOTS — Core bot settings, always available
            // ================================================================
            MaxBots = Config.Bind("Bots", "Bot Count", 3,
                new ConfigDescription("How many AI bots to spawn into matches.",
                    new AcceptableValueRange<int>(0, 8)));

            DrawTimer = Config.Bind("Bots", "Draw Timeout", 25,
                "When only bots remain alive, end the round after this many seconds (1-900).");

            NavGraphMode = Config.Bind("Bots", "Mode", "Play",
                new ConfigDescription(
                    "TRAINING: Walk around to teach bots the map. Records your movement as paths.\n" +
                    "PLAY: Bots use trained paths. Normal gameplay.",
                    new AcceptableValueList<string>("Training", "Play")));
            NavGraphMode.SettingChanged += (s, e) =>
            {
                // Apply the mode IMMEDIATELY. It used to be set only at map load
                // (SpawnBotsDelayed), so a mid-map switch left everything on the stale
                // mode: bots sat in TRAIN IDLE after switching to Play (Training mode +
                // behavior None), recorders kept Play gating during training, and the
                // UI/overlay disagreed with the buttons.
                if (NavGraph.Instance != null)
                    NavGraph.Instance.Mode = NavGraphMode.Value == "Play" ? NavMode.Play : NavMode.Training;

                if (NavGraphMode.Value == "Play")
                {
                    // Play is a warning, not a block. Let the user enter Play even on an
                    // undertrained map; bots keep learning as they go (LearnInPlay).
                    string warning = NavGraph.Instance?.GetPlayModeWarning();
                    if (!string.IsNullOrWhiteSpace(warning))
                        Log.LogWarning($"[Certification] {warning} (Play allowed — bots will keep learning the map as they go.)");
                    DisableTrainingSettings();
                    // The overlay is a training/debug aid — switching to Play means
                    // normal gameplay, so switch it off with the mode.
                    if (ShowOverlay != null && ShowOverlay.Value)
                    {
                        ShowOverlay.Value = false;
                        Log.LogInfo("[BOT] Overlay switched off with Play mode");
                    }
                    // Clean slate: kill all bots + player and start a real round on this
                    // map (training suppressed rounds, so the current one is stale).
                    BotManager.Instance?.StartFreshPlayRound();
                }
                else if (TrainingBehavior != null && TrainingBehavior.Value == "None")
                {
                    // Training is automatic: entering it always starts bots working
                    // (the old flow left them frozen on "None" after a Play round-trip).
                    TrainingBehavior.Value = "Validate";
                }
            };

            Config.Bind("Bots", "--- Spawn Bots Now ---", false,
                "Despawn all current bots and spawn the selected number fresh. Use mid-match to reset bots.")
                .SettingChanged += (s, e) =>
            {
                var entry = s as ConfigEntry<bool>;
                if (entry != null && entry.Value)
                {
                    entry.Value = false;
                    if (BotManager.Instance != null)
                    {
                        BotManager.Instance.DespawnAllBots();
                        // Re-populate lobby bots from current config count
                        BotManager.Instance.LobbyBots.Clear();
                        int count = MaxBots?.Value ?? 3;
                        for (int bi = 0; bi < count; bi++)
                            BotManager.Instance.AddBot();
                        BotManager.Instance.SpawnAllBots();
                        Log.LogInfo($"[BOT] Respawned {count} bots");
                    }
                    else
                    {
                        Log.LogWarning("[BOT] BotManager not ready — join a match first");
                    }
                }
            };

            // ================================================================
            //  GAMEPLAY — How bots behave during matches
            // ================================================================
            LockGraph = Config.Bind("Gameplay", "Freeze Map Data", false,
                "Freeze the navigation graph. Nothing is created, deleted, or modified. " +
                "Use when you're happy with the trained data and want to preserve it exactly.");
            UseNavMesh = Config.Bind("Gameplay", "Auto Ground Navigation", true,
                "Automatically generate a walkable navigation mesh for every map at load. " +
                "Bots can walk anywhere immediately with no training; learned data is still " +
                "used for jumps, ladders and special routes. Disable to use only learned paths.");
            // ================================================================
            //  TRAINING — behavior is stage-driven (no Bot Behavior knob anymore).
            // ================================================================

            // ---- Training Buttons (blocked in Play mode) ----
            Config.Bind("Training", "--- Clear All Map Data ---", false,
                "DELETE all navigation data for this map. Start fresh. Cannot undo.")
                .SettingChanged += (s, e) =>
            {
                var entry = s as ConfigEntry<bool>;
                if (entry != null && entry.Value)
                {
                    entry.Value = false;
                    if (NavGraphMode?.Value == "Play") { Log.LogInfo("[Config] Blocked — switch to Training first"); return; }
                    if (NavGraph.Instance != null && !string.IsNullOrEmpty(NavGraph.Instance.CurrentMap))
                    {
                        string map = NavGraph.Instance.CurrentMap;
                        string pluginDir = System.IO.Path.GetDirectoryName(
                            System.Reflection.Assembly.GetExecutingAssembly().Location);
                        string path = System.IO.Path.Combine(pluginDir, "NavData", $"{map}.bin");
                        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                        catch (System.Exception ex) { Log.LogWarning($"Delete failed: {ex.Message}"); }
                        CustomPatrolLocations.Clear();
                        NavGraph.Instance.LoadForMap(map);
                        NavGraph.Instance.RegisterMapLocations();
                        Log.LogInfo($"[NavGraph] Cleared all data for {map} — weapon nodes restored");
                    }
                }
            };

            // ================================================================
            //  DEBUG — Single visual overlay toggle
            // ================================================================
            ShowOverlay = Config.Bind("Debug", "Show Overlay", false,
                "Draw navigation nodes, edges, bot paths, and bot info text. " +
                "Costs real frame time — leave off except when debugging bots.");

            ShowCoverageMap = Config.Bind("Debug", "Coverage Map", true,
                "During training: tint walked ground green and unwalked ground orange " +
                "so you can see exactly what the bots still have to cover. " +
                "Also gates the REACH THIS WEAPON world markers.");

            ShowMeshDebug = Config.Bind("Debug", "Mesh Debug", false,
                "Draw just the baked navmesh wireframe (without the full bot overlay).");

            LogPerfStats = Config.Bind("Debug", "Log Perf Stats", true,
                "Write a one-line performance summary to the log every 10 seconds " +
                "(frame rate, bot CPU cost, GC). Cheap to leave on.");

            // ================================================================
            //  CUSTOMISE — Bot names and appearance
            // ================================================================
            string[] defaultNames = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Ghost", "Havoc" };
            for (int i = 0; i < 8; i++)
            {
                BotNames[i] = Config.Bind("Customise", $"Bot {i + 1} Name", defaultNames[i],
                    $"Name for bot slot {i + 1}. Leave blank for default.");
                BotSkills[i] = Config.Bind("Customise", $"Bot {i + 1} Skill", 5,
                    new ConfigDescription(
                        $"Difficulty for bot slot {i + 1}. 1 = easy (slow reactions, wild aim), " +
                        "5 = normal, 10 = deadly (snap aim, instant reactions). Applies live.",
                        new AcceptableValueRange<int>(1, 10)));
            }

            BotPatches.Apply();
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded!");
        }

        // ================================================================
        //  Helpers — hardcoded; all previous user knobs are now constants above.
        // ================================================================
        public static float GetPlayerDensityMultiplier() => NODE_DENSITY_MULT;
        public static float GetBotDensityMultiplier() => NODE_DENSITY_MULT;
        public static float GetScanRadius() => SCAN_RADIUS;
        public static float GetMaxJumpDist() => MAX_JUMP_DIST;
        public static float GetAutoSaveInterval() => AUTO_SAVE_SEC;
        // Recovery aggression knob removed — normal (medium) pacing always.
        public static bool IsFastRecovery => false;
        public static bool IsMediumRecovery => true;
        private static void DisableTrainingSettings()
        {
            if (TrainingBehavior != null) TrainingBehavior.Value = "None";
            Log.LogInfo("[Config] Switched to Play — bot exploration disabled");
        }

        private float _damageSyncDelay = 2f;
        private float _behaviorSyncTimer;
        private float _graphLinkSyncTimer;
        private void Update()
        {
            BotPerfStats.FrameTick(Time.unscaledDeltaTime);

            // Single-stage training: bots always run the Validate handler — it takes
            // pending special-edge routes when they exist and falls back to the
            // unified wander (coverage → weapons → player paths) otherwise.
            // None in Play mode or while the user paused the bots.
            _behaviorSyncTimer -= Time.deltaTime;
            if (_behaviorSyncTimer <= 0f)
            {
                _behaviorSyncTimer = 1f;
                string want;
                if (NavGraphMode == null || NavGraphMode.Value == "Play" || TrainingPaused)
                    want = "None";
                else
                    want = "Validate";
                if (TrainingBehavior.Value != want) TrainingBehavior.Value = want;

                // Keep trusted jump/fall/teleporter edges mirrored as NavMesh links.
                // No-op unless the trusted set changed; full sync ~every 10s.
                _graphLinkSyncTimer -= 1f;
                if (_graphLinkSyncTimer <= 0f)
                {
                    _graphLinkSyncTimer = 10f;
                    try { BotNavMesh.SyncGraphLinks(); } catch { }
                }
            }

            if (_damageSyncDelay > 0f)
            {
                _damageSyncDelay -= Time.deltaTime;
                if (_damageSyncDelay <= 0f)
                {
                    if (FindObjectOfType<BotDamageSync>() == null)
                    {
                        new GameObject("BotDamageSync").AddComponent<BotDamageSync>();
                        Log.LogInfo("[BOT] BotDamageSync created");
                    }
                    if (FindObjectOfType<TrainingUIBehaviour>() == null)
                    {
                        var uiObj = new GameObject("BotTrainingUI");
                        DontDestroyOnLoad(uiObj);
                        uiObj.AddComponent<TrainingUIBehaviour>();
                        Log.LogInfo("[BOT] TrainingUI created");
                    }
                }
            }
        }
    }
}
