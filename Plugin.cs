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

        // ===== Training =====
        public static ConfigEntry<string> TrainingBehavior;

        // Helper properties
        public static bool IsExploreMode => TrainingBehavior?.Value == "Explore";
        public static bool IsTrainingNone => TrainingBehavior?.Value == "None";

        // ===== Customise =====
        public static ConfigEntry<string>[] BotNames = new ConfigEntry<string>[8];

        // ===== Patrol Locations =====
        public static List<(Vector3 pos, int nodeId)> CustomPatrolLocations = new List<(Vector3, int)>();

        // ===== Blacklist =====
        public static HashSet<int> BlacklistedWeaponNodes = new HashSet<int>();

        // ===== Debug =====
        // Single overlay toggle — covers nodes, edges, bot paths, markers, and info text.
        public static ConfigEntry<bool> ShowOverlay;

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
                if (NavGraphMode.Value == "Play") DisableTrainingSettings();
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

            // ================================================================
            //  TRAINING — Only active in Training mode. Locked in Play mode.
            // ================================================================
            TrainingBehavior = Config.Bind("Training", "Bot Behavior", "Explore",
                new ConfigDescription(
                    "NONE: Bots freeze in place. Train the map yourself.\n" +
                    "EXPLORE: Bots autonomously explore the map, discover routes.",
                    new AcceptableValueList<string>("None", "Explore")));
            TrainingBehavior.SettingChanged += (s, e) =>
            {
                if (NavGraphMode?.Value == "Play" && TrainingBehavior.Value != "None")
                    TrainingBehavior.Value = "None";
            };

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
            ShowOverlay = Config.Bind("Debug", "Show Overlay", true,
                "Draw navigation nodes, edges, bot paths, and bot info text. " +
                "Turn off for clean gameplay.");

            // ================================================================
            //  CUSTOMISE — Bot names and appearance
            // ================================================================
            string[] defaultNames = { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Ghost", "Havoc" };
            for (int i = 0; i < 8; i++)
            {
                BotNames[i] = Config.Bind("Customise", $"Bot {i + 1} Name", defaultNames[i],
                    $"Name for bot slot {i + 1}. Leave blank for default.");
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

        private static void DisableTrainingSettings()
        {
            if (TrainingBehavior != null) TrainingBehavior.Value = "None";
            Log.LogInfo("[Config] Switched to Play — bot exploration disabled");
        }

        private float _damageSyncDelay = 2f;
        private void Update()
        {
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
