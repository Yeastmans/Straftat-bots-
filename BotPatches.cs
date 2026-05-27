using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using FishNet;
using FishNet.Managing.Scened;

namespace StraftatBots
{
    public static partial class BotPatches
    {
        private static Harmony _harmony;

        // Cached reflection — avoid GetField in hot paths
        private static readonly Dictionary<(Type, string), FieldInfo> _patchFieldCache = new Dictionary<(Type, string), FieldInfo>();
        private static readonly Dictionary<(Type, string), MethodInfo> _patchMethodCache = new Dictionary<(Type, string), MethodInfo>();
        private static readonly BindingFlags _allFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo GetField(Type type, string name)
        {
            var key = (type, name);
            if (!_patchFieldCache.TryGetValue(key, out var f))
            { f = type.GetField(name, _allFlags); _patchFieldCache[key] = f; }
            return f;
        }
        private static MethodInfo GetMethod(Type type, string name)
        {
            var key = (type, name);
            if (!_patchMethodCache.TryGetValue(key, out var m))
            { m = type.GetMethod(name, _allFlags); _patchMethodCache[key] = m; }
            return m;
        }

        public static void Apply()
        {
            if (_harmony != null) return;
            _harmony = new Harmony("com.modder.straftatbots.patches");

            try
            {
                // Solo play: skip player count checks
                PatchPostfix(typeof(LobbyController), "HasEnoughPlayers", nameof(HasEnoughPlayers_Postfix));
                PatchPrefix(typeof(PauseManager), "HandleServerStateWhenOnePlayerIsLeft", nameof(HandleOnePlayer_Prefix));

                // Suppress round/take progression in training mode (transpilers)
                try
                {
                    var progressMethod = typeof(GameManager).GetMethod("ProgressToNextTake",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (progressMethod != null)
                    {
                        var transpiler = typeof(BotPatches).GetMethod(nameof(TrainingGuard_Transpiler), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(progressMethod, transpiler: new HarmonyMethod(transpiler));
                        Plugin.Log.LogInfo("  Patched (transpiler): GameManager.ProgressToNextTake");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  Failed transpiler ProgressToNextTake: {e.Message}"); }

                try
                {
                    var roundWinMethod = typeof(ScoreManager).GetMethod("CheckForRoundWin",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (roundWinMethod != null)
                    {
                        var transpiler = typeof(BotPatches).GetMethod(nameof(TrainingGuardBool_Transpiler), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(roundWinMethod, transpiler: new HarmonyMethod(transpiler));
                        Plugin.Log.LogInfo("  Patched (transpiler): ScoreManager.CheckForRoundWin");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  Failed transpiler CheckForRoundWin: {e.Message}"); }

                // Bot lifecycle
                PatchPostfix(typeof(PlayerManager), "OnLoadSceneEnd", nameof(OnLoadSceneEnd_Postfix));
                PatchPostfix(typeof(PlayerManager), "RoundSpawn", nameof(RoundSpawn_Postfix));
                PatchPostfix(typeof(SteamLobby), "LeaveMatch", nameof(LeaveMatch_Postfix));

                // Player position recording for NavGraph (host only)
                PatchPostfix(typeof(FirstPersonController), "Update", nameof(FPCUpdate_Postfix));

                // Skip PlayerHealth.Update for bots (game's death logic needs IsOwner)
                PatchPrefix(typeof(PlayerHealth), "Update", nameof(PlayerHealthUpdate_Prefix));

                // PlayerHealth.Awake NREs on bots but the exception is harmless —
                // it's caught by CreateBot's try/catch. Do NOT patch Awake with a finalizer
                // because it would suppress exceptions for REAL players too, breaking spawns.

                // Skip PlayerHealth.Explode for bot victims — ExplodeForAll accesses
                // GetComponent<PlayerSetup>().mat / .hat which NRE on bots (no PlayerSetup
                // component). That NRE propagates out of ph.Explode() called inside
                // Obus.HandleExplosion / Bubble.HandleExplosion, aborting them mid-loop
                // before SetKiller, before explosion VFX / audio / decal spawn, and before
                // the Destroy(gameObject, 3) that despawns the projectile. Result: bots die
                // silently, no explosion, projectile persists. BotController.Die() + the
                // BotDamageSync path already handle bot ragdoll + graphics hiding.
                PatchPrefix(typeof(PlayerHealth), "Explode", nameof(PlayerHealth_Explode_Prefix));

                // Track player deaths for fall-death NavGraph feedback
                PatchPostfix(typeof(PlayerHealth), "ChangeKilledState", nameof(PlayerDeath_Postfix));

                // Patch launch/force zones to also affect bots (zones check for FPC, bots don't have one)
                try
                {
                    // StraftatTriggerZone handles ImpulseZone, ForceZone, GravityZone, etc.
                    PatchPostfix(typeof(StraftatTriggerZone), "OnTriggerEnter", nameof(TriggerZone_Enter_Postfix));
                    PatchPostfix(typeof(StraftatTriggerZone), "OnTriggerStay", nameof(TriggerZone_Stay_Postfix));
                    PatchPostfix(typeof(StraftatTriggerZone), "OnTriggerExit", nameof(TriggerZone_Exit_Postfix));
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  Failed to patch StraftatTriggerZone: {e.Message}"); }

                // FlingTrigger is separate (not a StraftatTriggerZone subclass)
                try { PatchPostfix(typeof(FlingTrigger), "OnTriggerEnter", nameof(FlingTrigger_Enter_Postfix)); }
                catch (Exception e) { Plugin.Log.LogWarning($"  Failed to patch FlingTrigger: {e.Message}"); }

                // Skip PlayerValues.Update for bots (NRE spam) — prefix + finalizer
                PatchPrefix(typeof(PlayerValues), "Update", nameof(PlayerValuesUpdate_Prefix));
                try
                {
                    var pvUpdate = typeof(PlayerValues).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (pvUpdate != null)
                    {
                        var fin = typeof(BotPatches).GetMethod(nameof(PlayerValuesUpdate_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(pvUpdate, finalizer: new HarmonyMethod(fin));
                    }
                }
                catch { }

                // After SetObjectInHandObserver runs on host for bot weapons, undo the FP arms mess
                try
                {
                    var setObjMethod = typeof(PlayerPickup).GetMethod("SetObjectInHandObserver",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (setObjMethod != null)
                    {
                        var postfix = typeof(BotPatches).GetMethod(nameof(SetObjectInHand_Postfix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(setObjMethod, postfix: new HarmonyMethod(postfix));
                        Plugin.Log.LogInfo("  Patched: PlayerPickup.SetObjectInHandObserver (postfix)");
                    }
                }
                catch { }

                // Suppress SceneMotor.Update NRE
                // SceneMotor.Update — finalizer to suppress NRE (no copy-paste of original)
                try
                {
                    var smUpdate = typeof(SceneMotor).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (smUpdate != null)
                    {
                        var fin = typeof(BotPatches).GetMethod(nameof(Explosion_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(smUpdate, finalizer: new HarmonyMethod(fin));
                        Plugin.Log.LogInfo("  Patched (finalizer): SceneMotor.Update");
                    }
                }
                catch { }

                // Suppress WaitForDraw crash (bot PlayerId not in playerInstances dict)
                try
                {
                    var waitForDrawType = typeof(GameManager).GetNestedTypes(BindingFlags.NonPublic)
                        .FirstOrDefault(t => t.Name.Contains("WaitForDraw"));
                    if (waitForDrawType != null)
                    {
                        var moveNext = waitForDrawType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (moveNext != null)
                        {
                            var fin = typeof(BotPatches).GetMethod(nameof(WaitForDraw_Finalizer), BindingFlags.Public | BindingFlags.Static);
                            _harmony.Patch(moveNext, finalizer: new HarmonyMethod(fin));
                            Plugin.Log.LogInfo("  Patched (finalizer): GameManager.WaitForDraw");
                        }
                    }
                }
                catch { }

                // Guard KillShockWave for bots (no post-processing volumes)
                PatchPrefix(typeof(FirstPersonController), "KillShockWave", nameof(KillShockWave_Prefix));

                // Suppress KillCam.Update NRE on bot objects
                PatchPrefix(typeof(KillCam), "Update", nameof(KillCamUpdate_Prefix));
                try
                {
                    var kcUpdate = typeof(KillCam).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (kcUpdate != null)
                    {
                        var fin = typeof(BotPatches).GetMethod(nameof(KillCamUpdate_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(kcUpdate, finalizer: new HarmonyMethod(fin));
                    }
                }
                catch { }

                // Finalizer on Gun.BurstFire coroutine — prevents NRE from killing the coroutine
                // and permanently breaking the player's weapon after killing a bot
                try
                {
                    var burstFireType = typeof(Gun).GetNestedTypes(BindingFlags.NonPublic)
                        .FirstOrDefault(t => t.Name.Contains("BurstFire"));
                    if (burstFireType != null)
                    {
                        var moveNext = burstFireType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (moveNext != null)
                        {
                            var fin = typeof(BotPatches).GetMethod(nameof(Explosion_Finalizer), BindingFlags.Public | BindingFlags.Static);
                            _harmony.Patch(moveNext, finalizer: new HarmonyMethod(fin));
                            Plugin.Log.LogInfo("  Patched (finalizer): Gun.BurstFire coroutine");
                        }
                    }
                }
                catch { }

                // Suppress SendKillLog on every weapon type when killer is a bot (uses host name otherwise).
                // Not on Weapon base class — patched per subclass.
                try
                {
                    var pre = typeof(BotPatches).GetMethod(nameof(Weapon_SendKillLog_Prefix), BindingFlags.Public | BindingFlags.Static);
                    Type[] sklTypes = new Type[] { typeof(Gun), typeof(Shotgun), typeof(BeamGun), typeof(ChargeGun), typeof(Minigun), typeof(LargeRaycastGun), typeof(MeleeWeapon) };
                    foreach (var t in sklTypes)
                    {
                        try
                        {
                            var m = t.GetMethod("SendKillLog", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (m != null) { _harmony.Patch(m, prefix: new HarmonyMethod(pre)); Plugin.Log.LogInfo($"  Patched: {t.Name}.SendKillLog"); }
                        }
                        catch { }
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  Could not patch weapon SendKillLog: {e.Message}"); }

                // Patch KillServer RPC logic on all weapon types to handle bot kills
                string killServerMethod = "RpcLogic___KillServer_1722911636";
                Type[] weaponTypes = new Type[]
                {
                    typeof(Gun), typeof(Shotgun), typeof(BeamGun), typeof(ChargeGun),
                    typeof(Minigun), typeof(LargeRaycastGun), typeof(MeleeWeapon)
                };
                foreach (var wType in weaponTypes)
                {
                    try { PatchPrefix(wType, killServerMethod, nameof(KillServer_Prefix)); }
                    catch { Plugin.Log.LogWarning($"  Could not patch {wType.Name}.{killServerMethod}"); }
                }

                // Patch PhysicsGrenade.SendKillLog
                PatchPrefix(typeof(PhysicsGrenade), "SendKillLog", nameof(SendKillLog_Prefix));
                // Patch PhysicsGrenade.KillShockWave — skip for bot-thrown grenades.
                // Prevents an NRE (bots lack post-processing volumes) that aborts HandleExplosion
                // mid-run and kills damage/VFX/audio for bot frag + regular grenades.
                try { PatchPrefix(typeof(PhysicsGrenade), "KillShockWave", nameof(PhysicsGrenade_KillShockWave_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch PhysicsGrenade.KillShockWave"); }
                try { PatchPrefix(typeof(Claymore), "SendKillLog", nameof(ExplosiveSendKillLog_Prefix)); } catch { }
                try { PatchPrefix(typeof(ProximityMine), "SendKillLog", nameof(ExplosiveSendKillLog_Prefix)); } catch { }

                // Obus (DualLauncher rocket) + Bubble (Bubblegun) — same NRE pattern as PhysicsGrenade.
                // KillShockWave accesses _rootObject.lensDistortion (null on bots), aborting HandleExplosion
                // mid-loop → no SendKillLog, no Explode VFX, no SetKiller. SendKillLog uses
                // ClientInstance.Instance (always the local human) as the killer name even when the
                // Obus was fired by a bot. Both patched to be bot-aware.
                // Obus + Bubble KillShockWave: prefix to skip for bots + finalizer to swallow any
                // NRE that slips through (e.g. when _rootObject is in a base class the prefix
                // can't find via simple GetField). Without the finalizer, HandleExplosion aborts
                // before reaching SendKillLog, so no kill feed and no victim Die() call.
                try
                {
                    var fin = typeof(BotPatches).GetMethod(nameof(Explosion_Finalizer), BindingFlags.Public | BindingFlags.Static);
                    foreach (var t in new[] { typeof(Obus), typeof(Bubble) })
                    {
                        string prefixName = t == typeof(Obus) ? nameof(Obus_KillShockWave_Prefix) : nameof(Bubble_KillShockWave_Prefix);
                        var ksw = t.GetMethod("KillShockWave", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (ksw == null) { Plugin.Log.LogWarning($"  Could not find {t.Name}.KillShockWave"); continue; }
                        var pre = typeof(BotPatches).GetMethod(prefixName, BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(ksw, prefix: new HarmonyMethod(pre), finalizer: new HarmonyMethod(fin));
                        Plugin.Log.LogInfo($"  Patched (prefix+finalizer): {t.Name}.KillShockWave");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  Could not patch Obus/Bubble.KillShockWave: {e.Message}"); }
                try { PatchPrefix(typeof(Obus), "SendKillLog", nameof(Obus_SendKillLog_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch Obus.SendKillLog"); }
                try { PatchPrefix(typeof(Bubble), "SendKillLog", nameof(Bubble_SendKillLog_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch Bubble.SendKillLog"); }
                try { PatchPostfix(typeof(Obus), "Initialize", nameof(ObusInitialize_Postfix)); }
                catch { Plugin.Log.LogWarning("  Could not patch Obus.Initialize"); }
                try { PatchPostfix(typeof(Bubble), "Initialize", nameof(BubbleInitialize_Postfix)); }
                catch { Plugin.Log.LogWarning("  Could not patch Bubble.Initialize"); }
                try { PatchPrefix(typeof(Bubble), "OnCollisionEnter", nameof(Bubble_OnCollisionEnter_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch Bubble.OnCollisionEnter"); }
                try { PatchPostfix(typeof(Bubble), "Update", nameof(Bubble_Update_Postfix)); }
                catch { Plugin.Log.LogWarning("  Could not patch Bubble.Update"); }

                // Patch explosions to also damage bots (HandleExplosion has IsOwner check that skips server)
                try
                {
                    var physExplode = typeof(PhysicsGrenade).GetMethod("HandleExplosion",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (physExplode != null)
                    {
                        var post = typeof(BotPatches).GetMethod(nameof(Explosion_Postfix), BindingFlags.Public | BindingFlags.Static);
                        var fin = typeof(BotPatches).GetMethod(nameof(Explosion_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(physExplode, postfix: new HarmonyMethod(post), finalizer: new HarmonyMethod(fin));
                        Plugin.Log.LogInfo("  Patched: PhysicsGrenade.HandleExplosion (postfix+finalizer)");
                    }
                }
                catch { }
                try
                {
                    var mineExplode = typeof(ProximityMine).GetMethod("HandleExplosion",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (mineExplode != null)
                    {
                        var post = typeof(BotPatches).GetMethod(nameof(Explosion_Postfix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(mineExplode, postfix: new HarmonyMethod(post));
                        Plugin.Log.LogInfo("  Patched: ProximityMine.HandleExplosion (postfix)");
                    }
                }
                catch { }
                try
                {
                    var clayExplode = typeof(Claymore).GetMethod("HandleExplosion",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (clayExplode != null)
                    {
                        var post = typeof(BotPatches).GetMethod(nameof(Explosion_Postfix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(clayExplode, postfix: new HarmonyMethod(post));
                        Plugin.Log.LogInfo("  Patched: Claymore.HandleExplosion (postfix)");
                    }
                }
                catch { }

                // Patch ALL explosive types HandleExplosion for bot damage
                System.Type[] explosiveTypes = new System.Type[]
                {
                    typeof(HandGrenade), typeof(HandGrenadeTwo), typeof(Bubble)
                };
                foreach (var eType in explosiveTypes)
                {
                    try
                    {
                        var method = eType.GetMethod("HandleExplosion",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (method != null)
                        {
                            var post = typeof(BotPatches).GetMethod(nameof(Explosion_Postfix), BindingFlags.Public | BindingFlags.Static);
                            var fin = typeof(BotPatches).GetMethod(nameof(Explosion_Finalizer), BindingFlags.Public | BindingFlags.Static);
                            _harmony.Patch(method, postfix: new HarmonyMethod(post), finalizer: new HarmonyMethod(fin));
                            Plugin.Log.LogInfo($"  Patched: {eType.Name}.HandleExplosion (postfix+finalizer)");
                        }
                    }
                    catch { }
                }

                // Patch Obus.HandleExplosion for bot damage
                try
                {
                    var obusExplode = typeof(Obus).GetMethod("HandleExplosion",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (obusExplode != null)
                    {
                        var post = typeof(BotPatches).GetMethod(nameof(ObusExplosion_Postfix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(obusExplode, postfix: new HarmonyMethod(post));
                        Plugin.Log.LogInfo("  Patched: Obus.HandleExplosion (postfix)");
                    }
                }
                catch { }

                // Patch PredictedProjectile.SendKillLog — fix kill feed for bot-fired rockets
                try
                {
                    var sendKillLog = typeof(PredictedProjectile).GetMethod("SendKillLog",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (sendKillLog != null)
                    {
                        var pre = typeof(BotPatches).GetMethod(nameof(PredictedProjectile_SendKillLog_Prefix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(sendKillLog, prefix: new HarmonyMethod(pre));
                        Plugin.Log.LogInfo("  Patched: PredictedProjectile.SendKillLog");
                    }
                }
                catch { }

                // Patch PredictedProjectile.KillShockWave — skip for bots (NRE on lensDistortion)
                try
                {
                    var killShock = typeof(PredictedProjectile).GetMethod("KillShockWave",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (killShock != null)
                    {
                        var pre = typeof(BotPatches).GetMethod(nameof(PredictedProjectile_KillShockWave_Prefix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(killShock, prefix: new HarmonyMethod(pre));
                        Plugin.Log.LogInfo("  Patched: PredictedProjectile.KillShockWave");
                    }
                }
                catch { }

                // Patch PredictedProjectile.HitMarker — skip for bot-fired rockets (shows on host crosshair)
                try
                {
                    var hitMarker = typeof(PredictedProjectile).GetMethod("HitMarker",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (hitMarker != null)
                    {
                        var pre = typeof(BotPatches).GetMethod(nameof(PredictedProjectile_HitMarker_Prefix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(hitMarker, prefix: new HarmonyMethod(pre));
                        Plugin.Log.LogInfo("  Patched: PredictedProjectile.HitMarker");
                    }
                }
                catch { }

                // Patch MeleeWeapon.HitServer for bots (NRE from MeleeChildCollision)
                try { PatchPrefix(typeof(MeleeWeapon), "HitServer", nameof(MeleeHitServer_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch MeleeWeapon.HitServer"); }

                // Patch MeleeWeapon.BumpPlayerServer for bots
                try { PatchPrefix(typeof(MeleeWeapon), "RpcLogic___BumpPlayerServer_1076951378", nameof(BumpPlayerServer_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch MeleeWeapon.BumpPlayerServer"); }

                // Patch ItemBehaviour.OnCollisionEnter for bots (prefix + finalizer)
                try
                {
                    PatchPrefix(typeof(ItemBehaviour), "OnCollisionEnter", nameof(ItemCollision_Prefix));
                    var itemColMethod = typeof(ItemBehaviour).GetMethod("OnCollisionEnter",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (itemColMethod != null)
                    {
                        var finalizerPatch = typeof(BotPatches).GetMethod(nameof(ItemCollision_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(itemColMethod, finalizer: new HarmonyMethod(finalizerPatch));
                    }
                }
                catch { Plugin.Log.LogWarning("  Could not patch ItemBehaviour.OnCollisionEnter"); }

                // Patch VictoryMenuUI.Start to handle bots (Steam avatar crash on SteamID 0)
                try { PatchPrefix(typeof(VictoryMenuUI), "Start", nameof(VictoryMenuUI_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch VictoryMenuUI.Start"); }

                // Suppress MatchPoitnsHUD.UpdateVisuals crash (bot team IDs exceed HUD array bounds)
                try
                {
                    var hudMethod = typeof(MatchPoitnsHUD).GetMethod("UpdateVisuals",
                        BindingFlags.Instance | BindingFlags.Public,
                        null, new Type[] { typeof(int), typeof(Dictionary<int, int>) }, null);
                    if (hudMethod != null)
                    {
                        var finalizerPatch = typeof(BotPatches).GetMethod(nameof(MatchPointsHUD_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(hudMethod, finalizer: new HarmonyMethod(finalizerPatch));
                        Plugin.Log.LogInfo("  Patched (finalizer): MatchPoitnsHUD.UpdateVisuals");
                    }
                }
                catch { Plugin.Log.LogWarning("  Could not patch MatchPoitnsHUD.UpdateVisuals"); }

                // Debug visualizer — static GL callback, text proxy auto-attaches to camera
                BotDebugVisualizer.Register();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Patch error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void PatchPrefix(Type type, string methodName, string patchName)
        {
            var target = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (target == null) { Plugin.Log.LogWarning($"  Could not find {type.Name}.{methodName}"); return; }
            var patch = typeof(BotPatches).GetMethod(patchName, BindingFlags.Public | BindingFlags.Static);
            _harmony.Patch(target, prefix: new HarmonyMethod(patch));
            Plugin.Log.LogInfo($"  Patched: {type.Name}.{methodName}");
        }

        private static void PatchPostfix(Type type, string methodName, string patchName)
        {
            var target = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (target == null) { Plugin.Log.LogWarning($"  Could not find {type.Name}.{methodName}"); return; }
            var patch = typeof(BotPatches).GetMethod(patchName, BindingFlags.Public | BindingFlags.Static);
            _harmony.Patch(target, postfix: new HarmonyMethod(patch));
            Plugin.Log.LogInfo($"  Patched: {type.Name}.{methodName}");
        }

        private static void EnsureBotManager()
        {
            if (BotManager.Instance == null)
            {
                GameObject managerObj = new GameObject("BotManager");
                managerObj.AddComponent<BotManager>();
                Plugin.Log.LogInfo("[BOT] BotManager created lazily");
            }
        }

        // ============ PATCHES ============

        // Skip solo kick
        public static bool HandleOnePlayer_Prefix() => false;

        /// <summary>Returns true if training mode is active — used by transpilers to guard method entry.</summary>
        public static bool IsTrainingMode()
        {
            // Suppress rounds in ALL training modes — explore, connect, and follow all need time.
            bool configIsTraining = Plugin.NavGraphMode?.Value != "Play";
            return configIsTraining;
        }

        /// <summary>
        /// Transpiler: injects "if (IsTrainingMode()) return;" at the start of void methods.
        /// Skips the entire method body in training mode.
        /// </summary>
        public static IEnumerable<CodeInstruction> TrainingGuard_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var skipLabel = new Label();
            var codes = new List<CodeInstruction>(instructions);

            // Insert at beginning: if (IsTrainingMode()) return;
            var check = typeof(BotPatches).GetMethod(nameof(IsTrainingMode), BindingFlags.Public | BindingFlags.Static);
            codes.Insert(0, new CodeInstruction(OpCodes.Call, check));
            codes.Insert(1, new CodeInstruction(OpCodes.Brfalse_S, skipLabel));
            codes.Insert(2, new CodeInstruction(OpCodes.Ret));
            codes[3].labels.Add(skipLabel); // Original first instruction

            return codes;
        }

        /// <summary>
        /// Transpiler: injects "if (IsTrainingMode()) { result = false; return false; }" for bool methods with out param.
        /// </summary>
        public static IEnumerable<CodeInstruction> TrainingGuardBool_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var skipLabel = new Label();
            var codes = new List<CodeInstruction>(instructions);

            var check = typeof(BotPatches).GetMethod(nameof(IsTrainingMode), BindingFlags.Public | BindingFlags.Static);
            codes.Insert(0, new CodeInstruction(OpCodes.Call, check));
            codes.Insert(1, new CodeInstruction(OpCodes.Brfalse_S, skipLabel));
            codes.Insert(2, new CodeInstruction(OpCodes.Ldc_I4_0)); // Push false
            codes.Insert(3, new CodeInstruction(OpCodes.Ret));       // Return false
            codes[4].labels.Add(skipLabel);

            return codes;
        }

        // Force enough players
        public static void HasEnoughPlayers_Postfix(ref bool __result) => __result = true;

        // After SetObjectInHandObserver runs on host for bot weapons, undo the host-side mess
        // The RPC sends to clients normally — we just fix the host afterward
        public static void SetObjectInHand_Postfix(PlayerPickup __instance, GameObject obj, GameObject player)
        {
            if (!FishNet.InstanceFinder.IsServer) return;
            if (player == null) return;
            var bot = player.GetComponent<BotController>();
            if (bot == null) return;
            if (obj == null) return;

            // Undo EVERYTHING the observer code did on host:
            // 1. Unparent from FP arms
            obj.transform.SetParent(null);
            // 2. Re-disable ItemBehaviour (observer re-enabled it)
            var beh = obj.GetComponent<ItemBehaviour>();
            if (beh != null) beh.enabled = false;
            // 3. Force layer 0 (Default) — visible to all cameras, no see-through-walls
            obj.layer = 0;
            foreach (Transform child in obj.transform)
                child.gameObject.layer = 0;
            // 4. Disable all renderers the observer may have enabled on wrong layers
            // Then re-enable them on correct layer
            foreach (var r in obj.GetComponentsInChildren<Renderer>(true))
            {
                r.gameObject.layer = 0;
            }
            // 5. Position at hand
            bot.PositionWeaponAtHandPublic();
        }

        // Skip PlayerHealth.Update for bots (game checks IsOwner, fails for bots)
        public static bool PlayerHealthUpdate_Prefix(PlayerHealth __instance)
        {
            return __instance.GetComponent<BotController>() == null;
        }

        // Skip PlayerHealth.Explode for bot victims — the downstream ExplodeForAll dereferences
        // GetComponent<PlayerSetup>() which returns null on bots, NREs, and aborts the caller
        // (Obus/Bubble HandleExplosion) before SetKiller + VFX/audio + Destroy can run.
        public static bool PlayerHealth_Explode_Prefix(PlayerHealth __instance)
        {
            if (__instance != null && __instance.GetComponent<BotController>() != null)
                return false;
            return true;
        }

        // Track player deaths — report to PlayerRecorder for fall-death NavGraph feedback
        public static void PlayerDeath_Postfix(PlayerHealth __instance, bool tempBool)
        {
            if (!tempBool) return;
            // Only report for real players, not bots (bots report in Die())
            if (__instance.GetComponent<BotController>() != null) return;
            var fpc = __instance.GetComponent<FirstPersonController>();
            if (fpc == null) return;
            PlayerRecorder.ReportDeath(fpc.GetInstanceID(), __instance.transform.position);
        }

        // ============ LAUNCH/FORCE ZONE PATCHES ============
        // These fire after the zone's own OnTriggerEnter/Stay/Exit which only handles FPC players.
        // We check for BotController and apply the equivalent force.

        private static BotController ResolveBotFromCollider(Collider other)
        {
            if (other == null) return null;

            var bot = other.GetComponent<BotController>();
            if (bot != null) return bot;

            bot = other.GetComponentInParent<BotController>();
            if (bot != null) return bot;

            var attached = other.attachedRigidbody;
            return attached != null ? attached.GetComponentInParent<BotController>() : null;
        }

        private static float GetGravityZoneMultiplier(GravityZone zone)
        {
            if (zone == null) return 1f;
            try
            {
                var field = GetField(typeof(GravityZone), "gravityMultiplier");
                if (field == null) return 1f;
                object value = field.GetValue(zone);
                if (value is float f) return f;
                return Convert.ToSingle(value);
            }
            catch
            {
                return 1f;
            }
        }

        public static void TriggerZone_Enter_Postfix(StraftatTriggerZone __instance, Collider other)
        {
            try
            {
                var bot = ResolveBotFromCollider(other);
                if (bot == null) return;

                Plugin.Log.LogInfo($"[BOT] {bot.BotName} entered trigger zone: {__instance.GetType().Name}");

                var impulse = __instance as ImpulseZone;
                if (impulse != null)
                {
                    // Match FPC exactly: ImpulseZone adds force once on enter.
                    Plugin.Log.LogInfo($"[BOT] Applying impulse: {impulse.force}");
                    bot.EnterImpulseZone(impulse);
                    return;
                }

                var forceZone = __instance as ForceZone;
                if (forceZone != null)
                {
                    // Match FPC exactly: ForceZone has NO enter-time kick; it applies force in its
                    // own Update loop every frame while the player is inside. We register the zone
                    // and drive force from the bot's own Update — OnTriggerStay is unreliable on
                    // CharacterController-only bots (no Rigidbody) and was the root cause of the
                    // "barely launches / doesn't launch" bug.
                    Plugin.Log.LogInfo($"[BOT] Registering force zone: {forceZone.force}");
                    bot.RegisterForceZone(forceZone);
                    return;
                }

                var gravityZone = __instance as GravityZone;
                if (gravityZone != null)
                {
                    float multiplier = GetGravityZoneMultiplier(gravityZone);
                    Plugin.Log.LogInfo($"[BOT] Registering gravity zone: x{multiplier}");
                    bot.RegisterGravityZone(gravityZone, multiplier);
                    return;
                }
            }
            catch { }
        }

        public static void TriggerZone_Stay_Postfix(StraftatTriggerZone __instance, Collider other)
        {
            try
            {
                var bot = ResolveBotFromCollider(other);
                if (bot == null) return;

                var forceZone = __instance as ForceZone;
                if (forceZone != null)
                {
                    bot.RegisterForceZone(forceZone);
                    return;
                }

                // If OnTriggerEnter was missed, recover once here without refreshing every frame.
                var impulse = __instance as ImpulseZone;
                if (impulse != null)
                {
                    bot.EnterImpulseZone(impulse);
                    return;
                }

                var gravityZone = __instance as GravityZone;
                if (gravityZone != null)
                {
                    bot.RegisterGravityZone(gravityZone, GetGravityZoneMultiplier(gravityZone));
                }
            }
            catch { }
        }

        public static void TriggerZone_Exit_Postfix(StraftatTriggerZone __instance, Collider other)
        {
            try
            {
                var bot = ResolveBotFromCollider(other);
                if (bot == null) return;

                var forceZone = __instance as ForceZone;
                if (forceZone != null)
                {
                    bot.UnregisterForceZone(forceZone);
                    return;
                }

                var impulse = __instance as ImpulseZone;
                if (impulse != null)
                {
                    bot.ExitImpulseZone(impulse);
                    return;
                }

                var gravityZone = __instance as GravityZone;
                if (gravityZone != null)
                {
                    bot.UnregisterGravityZone(gravityZone);
                }
            }
            catch { }
        }

        // FlingTrigger is not a StraftatTriggerZone — separate MonoBehaviour with its own trigger
        public static void FlingTrigger_Enter_Postfix(FlingTrigger __instance, Collider other)
        {
            try
            {
                var bot = ResolveBotFromCollider(other);
                if (bot == null) return;
                // FlingTrigger applies AddVerticalForce(Vector3.up, 25f) — we match that
                bot.ApplyZoneImpulse(Vector3.up * 25f);
            }
            catch { }
        }

        // Skip PlayerValues.Update for bots and any object with missing client data
        public static bool PlayerValuesUpdate_Prefix(PlayerValues __instance)
        {
            try
            {
                if (__instance.playerClient == null) return false;
                if (__instance.GetComponent<BotController>() != null) return false;
            }
            catch { return false; }
            return true;
        }

        public static Exception PlayerValuesUpdate_Finalizer(Exception __exception) => null;

        // Guard KillCam.Update — cached reflection fields
    }
}
