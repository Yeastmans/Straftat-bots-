using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using FishNet;
using FishNet.Object;
using FishNet.Managing.Scened;

namespace StraftatBots
{
    public static partial class BotPatches
    {
        // ============ BOT LIFECYCLE ============

        private static string _lastSpawnedScene;

        // Spawn bots when match loads
        public static void OnLoadSceneEnd_Postfix(PlayerManager __instance, SceneLoadEndEventArgs args)
        {
            Plugin.Log.LogInfo("[BOT] OnLoadSceneEnd fired");
            BotController.ClearStaticCaches(); // Clear stale references from previous scene
            if (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServer) return;

            EnsureBotManager();

            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            Plugin.Log.LogInfo($"[BOT] Scene: {sceneName}");
            if (sceneName == "MainMenu" || sceneName == "EmptyScene" || sceneName == "EndGame")
            {
                _lastSpawnedScene = null; // Reset so bots spawn on next map
                return;
            }

            // Only spawn bots once per scene load — ignore late joiners triggering OnLoadSceneEnd again
            if (_lastSpawnedScene == sceneName && BotManager.Instance.GetActiveBots().Count > 0)
            {
                Plugin.Log.LogInfo("[BOT] Bots already active, skipping (late joiner?)");
                return;
            }
            _lastSpawnedScene = sceneName;

            BotManager.Instance.DespawnAllBots();

            if (BotManager.Instance.LobbyBots.Count == 0)
            {
                int count = Plugin.MaxBots.Value;
                for (int i = 0; i < count; i++)
                    BotManager.Instance.AddBot();
                Plugin.Log.LogInfo($"[BOT] Auto-added {count} bots");
            }

            if (!_spawnPending)
            {
                _spawnPending = true;
                __instance.StartCoroutine(SpawnBotsDelayed());
            }
        }

        private static bool _spawnPending;

        private static IEnumerator SpawnBotsDelayed()
        {
            // Wait for player to actually spawn — some maps load slower
            float waited = 0f;
            while (waited < 5f)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
                // Check if any player has spawned (PlayerManager has active players)
                if (ClientInstance.Instance?.PlayerSpawner?.player != null)
                    break;
            }
            // Extra small buffer after player spawns
            yield return new WaitForSeconds(0.3f);

            // Reset per-map caches
            BotController.ResetLadderCache();

            // Load NavGraph for this map, apply mode from config
            NavGraph.Init();
            string modeStr = Plugin.NavGraphMode?.Value ?? "Training";
            bool isPlayMode = modeStr.Equals("Play", System.StringComparison.OrdinalIgnoreCase);
            NavGraph.Instance.Mode = isPlayMode ? NavMode.Play : NavMode.Training;

            // Training mode: bots walk through each other (and players) so they don't block pathing
            bool noClip = !isPlayMode;
            Physics.IgnoreLayerCollision(11, 11, noClip); // Bot body vs bot body
            Physics.IgnoreLayerCollision(3, 3, noClip);   // Bot CC vs bot CC (root layer)
            Physics.IgnoreLayerCollision(3, 6, noClip);   // Bot CC vs player CC
            string mapName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            NavGraph.Instance.LoadForMap(mapName);

            // Register spawn points and weapon spawners as fixed nodes
            NavGraph.Instance.RegisterMapLocations();

            // Re-add custom patrol locations (cleared by RegisterMapLocations)
            foreach (var (cpos, cid) in Plugin.CustomPatrolLocations)
            {
                var node = NavGraph.Instance.FindNearestNode(cpos, 3f);
                if (node == null)
                    node = NavGraph.Instance.AddPosition(cpos, isPlayer: true, force: true);
                if (node != null)
                {
                    node.Confidence = 1f;
                    NavGraph.Instance.MapLocations.Add((cpos, "PatrolPoint", node.Id));
                }
            }

            if (NavGraph.Instance.NodeCount > 20)
            {
                // Existing graph — validate and clean up
                NavGraph.Instance.ValidateAllNodes();
                NavGraph.Instance.DetectJumpEdges();
            }

            PlayerRecorder.Enable();

            Plugin.Log.LogInfo("[BOT] Spawning bots now");
            BotManager.Instance?.SpawnAllBots();
            _spawnPending = false;

            // Cache routes between key locations for fast pathing
            if (NavGraph.Instance != null && NavGraph.Instance.HasData)
                NavGraph.Instance.CacheKeyRoutes();
        }

        // New round — despawn old bots and spawn fresh ones
        public static void RoundSpawn_Postfix(PlayerManager __instance)
        {
            Plugin.Log.LogInfo("[BOT] RoundSpawn fired");
            if (InstanceFinder.NetworkManager == null || !InstanceFinder.NetworkManager.IsServer) return;
            if (BotManager.Instance == null) return;
            if (_spawnPending) return; // Prevent overlap with OnLoadSceneEnd spawn
            _spawnPending = true;
            __instance.StartCoroutine(RespawnBotsNewRound());
        }

        private static IEnumerator RespawnBotsNewRound()
        {
            // Save NavGraph between rounds and sync to all clients
            NavGraph.Instance?.Save();
            BotDamageSync.SyncNavGraph();

            // Safety: reset waitForDrawCoroutine in case it got stuck from a previous round
            try
            {
                var field = GetField(typeof(GameManager), "waitForDrawCoroutine");
                if (field != null && GameManager.Instance != null)
                    field.SetValue(GameManager.Instance, null);
            }
            catch { }

            // Reset the only-bots-alive draw timer
            if (BotManager.Instance != null)
                BotManager.Instance.ResetDrawTimer();

            // Despawn IMMEDIATELY — don't let old bots shoot into the new take
            if (BotManager.Instance != null)
                BotManager.Instance.DespawnAllBots();

            // Wait for players to spawn, then spawn fresh bots
            yield return new WaitForSeconds(1.5f);
            if (BotManager.Instance != null)
                BotManager.Instance.SpawnAllBots();
            _spawnPending = false;

            if (NavGraph.Instance != null && NavGraph.Instance.HasData)
                NavGraph.Instance.CacheKeyRoutes();
        }

        // Clean up on leave
        public static void LeaveMatch_Postfix()
        {
            Plugin.Log.LogInfo("[BOT] LeaveMatch fired");
            NavGraph.Instance?.Save();
            PlayerRecorder.Disable();
            BotManager.Instance?.DespawnAllBots();
        }

        // ==================== FPC RECORDING ====================

        /// <summary>
        /// Postfix on FirstPersonController.Update — records player positions for NavGraph.
        /// Only runs on host (server).
        /// </summary>
        public static void FPCUpdate_Postfix(FirstPersonController __instance)
        {
            try
            {
                // Skip bots — they're recorded separately via PlayerRecorder.RecordBot
                if (__instance.GetComponent<BotController>() != null) return;
                PlayerRecorder.RecordPlayer(__instance);
            }
            catch { }
        }

        // ==================== TRIGGER ZONES ====================

        // Helper to read private fields
        private static T ReadField<T>(object obj, string fieldName)
        {
            var field = obj.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field != null ? (T)field.GetValue(obj) : default;
        }
    }
}
