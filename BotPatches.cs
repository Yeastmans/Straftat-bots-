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
        private static readonly Dictionary<string, float> _recentKillFeedVictims = new Dictionary<string, float>();
        private static int _allowNextBotKillFeedLines;
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
                try { PatchPrefix(typeof(PauseManager), "WriteLog", nameof(PauseManagerWriteLog_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch PauseManager.WriteLog"); }
                try
                {
                    var replaceNames = typeof(ClientInstance).GetMethod(
                        "ReplaceAllPlayerNameTags",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(string) },
                        null);
                    if (replaceNames != null)
                    {
                        var post = typeof(BotPatches).GetMethod(nameof(ReplaceAllPlayerNameTags_Postfix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(replaceNames, postfix: new HarmonyMethod(post));
                        Plugin.Log.LogInfo("  Patched: ClientInstance.ReplaceAllPlayerNameTags");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("  Could not find ClientInstance.ReplaceAllPlayerNameTags");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"  Could not patch ClientInstance.ReplaceAllPlayerNameTags: {e.Message}");
                }

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
                PatchPostfix(typeof(GameManager), "ProgressToNextTake", nameof(ProgressToNextTake_Postfix));
                try { PatchPrefix(typeof(RoundManager), "NextRoundCall", nameof(RoundManagerNextRoundCall_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch RoundManager.NextRoundCall"); }
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
                try
                {
                    var setLeftObjMethod = typeof(PlayerPickup).GetMethod("SetObjectInLeftHandObserver",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (setLeftObjMethod != null)
                    {
                        var postfix = typeof(BotPatches).GetMethod(nameof(SetObjectInHand_Postfix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(setLeftObjMethod, postfix: new HarmonyMethod(postfix));
                        Plugin.Log.LogInfo("  Patched: PlayerPickup.SetObjectInLeftHandObserver (postfix)");
                    }
                }
                catch { }

                // Strip hat + cig from bots. The game's ChangeDress ALWAYS instantiates a hat and a
                // cig (PlayerSetup.cs:232/242) that render as white untextured slabs on bots. Keep
                // the suit (applied in the same method); drop the cosmetics. Runs on every dress,
                // including respawn, so it's the authoritative fix.
                try
                {
                    var changeDress = typeof(PlayerSetup).GetMethod("ChangeDress",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                        null, new[] { typeof(GameObject), typeof(GameObject), typeof(Vector3) }, null);
                    if (changeDress != null)
                    {
                        var postfix = typeof(BotPatches).GetMethod(nameof(ChangeDress_Postfix), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(changeDress, postfix: new HarmonyMethod(postfix));
                        Plugin.Log.LogInfo("  Patched: PlayerSetup.ChangeDress (strip bot hat/cig)");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning($"  ChangeDress patch failed: {e.Message}"); }

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

                // Bot-held guns are fired by BotController manually. If vanilla Gun.Update ever
                // gets re-enabled by observer hand sync, ShootServer dereferences player-only
                // objects and spams NREs.
                try
                {
                    var shootServer = typeof(Gun).GetMethod("ShootServer",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (shootServer != null)
                    {
                        var pre = typeof(BotPatches).GetMethod(nameof(GunShootServer_Prefix), BindingFlags.Public | BindingFlags.Static);
                        var fin = typeof(BotPatches).GetMethod(nameof(GunShootServer_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(shootServer, prefix: new HarmonyMethod(pre), finalizer: new HarmonyMethod(fin));
                        Plugin.Log.LogInfo("  Patched: Gun.ShootServer (bot guard)");
                    }
                }
                catch { Plugin.Log.LogWarning("  Could not patch Gun.ShootServer"); }

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
                try { PatchPrefix(typeof(PlayerHealth), "TaserEnemy", nameof(PlayerHealth_TaserEnemy_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch PlayerHealth.TaserEnemy"); }
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

                // Patch PhysicsProp.BumpPlayerServer for null/bot PlayerHealth RPC crashes
                try { PatchPrefix(typeof(PhysicsProp), "RpcLogic___BumpPlayerServer_1076951378", nameof(PhysicsPropBumpPlayerServer_Prefix)); }
                catch { Plugin.Log.LogWarning("  Could not patch PhysicsProp.BumpPlayerServer"); }

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
                        var prefixPatch = typeof(BotPatches).GetMethod(nameof(MatchPointsHUD_UpdateVisuals_Prefix), BindingFlags.Public | BindingFlags.Static);
                        var finalizerPatch = typeof(BotPatches).GetMethod(nameof(MatchPointsHUD_Finalizer), BindingFlags.Public | BindingFlags.Static);
                        _harmony.Patch(hudMethod, prefix: new HarmonyMethod(prefixPatch), finalizer: new HarmonyMethod(finalizerPatch));
                        Plugin.Log.LogInfo("  Patched (prefix+finalizer): MatchPoitnsHUD.UpdateVisuals");
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

        /// <summary>Strip the hat + cig the game's ChangeDress instantiates on bots (they render
        /// as white untextured slabs). Suit is applied earlier in ChangeDress and is left intact.</summary>
        public static void ChangeDress_Postfix(PlayerSetup __instance)
        {
            try
            {
                if (__instance == null || __instance.GetComponent<BotController>() == null) return;
                if (__instance.hat != null) { UnityEngine.Object.Destroy(__instance.hat); __instance.hat = null; }
                var mount = __instance.hatToWearPosition;
                if (mount != null)
                {
                    for (int i = mount.childCount - 1; i >= 0; i--)
                    {
                        var child = mount.GetChild(i);
                        if (child.GetComponent<HatPosition>() != null)
                            UnityEngine.Object.Destroy(child.gameObject);
                    }
                    mount.gameObject.SetActive(false);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"ChangeDress_Postfix: {e.Message}"); }
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

        public static bool PauseManagerWriteLog_Prefix(ref string __0)
        {
            try
            {
                if (_allowNextBotKillFeedLines > 0)
                {
                    _allowNextBotKillFeedLines--;
                    string plain = StripRichText(__0).Trim();
                    if (LooksLikeKillFeedLine(plain))
                    {
                        string key = NormalizeKillFeedVictimKey(ExtractKillFeedVictim(plain));
                        if (!string.IsNullOrWhiteSpace(key))
                            _recentKillFeedVictims[key] = Time.time;
                    }
                    RewriteBlankWinnerLine(ref __0);
                    return true;
                }

                if (ShouldSuppressKillFeedDuplicate(__0))
                    return false;
                RewriteBlankWinnerLine(ref __0);
            }
            catch { }
            return true;
        }

        internal static void AllowNextKillFeedLine()
        {
            _allowNextBotKillFeedLines = Math.Min(_allowNextBotKillFeedLines + 1, 4);
        }

        public static void ReplaceAllPlayerNameTags_Postfix(string __0, ref string __result)
        {
            try
            {
                RewriteBlankWinnerLine(ref __result);
            }
            catch { }
        }

        private static void RewriteBlankWinnerLine(ref string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string plain = StripRichText(text).Trim();
            bool takeMsg = plain.EndsWith("won the take", StringComparison.OrdinalIgnoreCase);
            bool roundMsg = plain.EndsWith("won the round", StringComparison.OrdinalIgnoreCase);
            if (!takeMsg && !roundMsg) return;

            // Only rewrite broken winner lines; keep normal player messages untouched.
            bool blankWinner = plain.StartsWith("blank ", StringComparison.OrdinalIgnoreCase)
                || plain.StartsWith("won the ", StringComparison.OrdinalIgnoreCase)
                || plain.StartsWith(" won the ", StringComparison.OrdinalIgnoreCase)
                || plain.Contains("  won the ");
            if (!blankWinner) return;

            string winnerName = ResolveLikelyWinnerName();
            if (string.IsNullOrWhiteSpace(winnerName)) return;
            string winnerLabel = BuildWinnerLabel(winnerName);

            text = takeMsg
                ? $"{winnerLabel} won the take"
                : $"{winnerLabel} won the round";
        }

        private static bool ShouldSuppressBrokenUnknownKillLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string plain = StripRichText(text).Trim().ToLowerInvariant();
            if (!plain.Contains("was killed")) return false;
            return plain.Contains("was killed by unknown")
                || plain.EndsWith(" by unknown", StringComparison.OrdinalIgnoreCase);
        }

        internal static void MarkKillFeedVictim(string victimName)
        {
            string key = NormalizeKillFeedVictimKey(victimName);
            if (string.IsNullOrWhiteSpace(key)) return;
            _recentKillFeedVictims[key] = Time.time;
        }

        private static bool ShouldSuppressKillFeedDuplicate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (ShouldSuppressBrokenUnknownKillLine(text)) return true;

            string plain = StripRichText(text).Trim();
            if (!LooksLikeKillFeedLine(plain)) return false;

            string victim = ExtractKillFeedVictim(plain);
            string key = NormalizeKillFeedVictimKey(victim);
            if (string.IsNullOrWhiteSpace(key)) return false;

            float now = Time.time;
            if (_recentKillFeedVictims.TryGetValue(key, out float last) && now - last < 1.25f)
                return true;

            _recentKillFeedVictims[key] = now;
            if (_recentKillFeedVictims.Count > 64)
            {
                var stale = new List<string>();
                foreach (var kv in _recentKillFeedVictims)
                    if (now - kv.Value > 8f) stale.Add(kv.Key);
                foreach (var k in stale) _recentKillFeedVictims.Remove(k);
            }
            return false;
        }

        private static bool LooksLikeKillFeedLine(string plain)
        {
            if (string.IsNullOrWhiteSpace(plain)) return false;
            string lower = plain.ToLowerInvariant();
            return lower.Contains(" was killed")
                || lower.Contains(" was headshot")
                || lower.Contains(" was beheaded")
                || lower.Contains(" was slain");
        }

        private static string ExtractKillFeedVictim(string plain)
        {
            if (string.IsNullOrWhiteSpace(plain)) return "";
            string lower = plain.ToLowerInvariant();
            foreach (string marker in new[] { " was killed", " was headshot", " was beheaded", " was slain" })
            {
                int idx = lower.IndexOf(marker, StringComparison.Ordinal);
                if (idx > 0) return plain.Substring(0, idx).Trim();
            }
            return "";
        }

        private static string NormalizeKillFeedVictimKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            value = StripRichText(value).Trim().ToLowerInvariant();
            if (value.StartsWith("blank ")) value = value.Substring(6).Trim();
            return value;
        }

        private static string BuildWinnerLabel(string winnerName)
        {
            if (string.IsNullOrWhiteSpace(winnerName)) return "<b>Unknown</b>";
            string safe = winnerName;
            bool isBot = false;
            try
            {
                if (BotManager.Instance != null)
                {
                    foreach (var bot in BotManager.Instance.LobbyBots)
                    {
                        if (bot != null && string.Equals(bot.Name, winnerName, StringComparison.OrdinalIgnoreCase))
                        {
                            isBot = true;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (isBot) return $"<b><color=#6CD4FF>{safe}</color></b>";

            string playerColor = "FFFFFF";
            try
            {
                if (PauseManager.Instance != null && !string.IsNullOrWhiteSpace(PauseManager.Instance.selfNameLogColor))
                    playerColor = PauseManager.Instance.selfNameLogColor;
            }
            catch { }
            return $"<b><color=#{playerColor}>{safe}</color></b>";
        }

        private static string ResolveLikelyWinnerName()
        {
            try
            {
                if (BotManager.Instance != null)
                {
                    int aliveCount = 0;
                    BotController aliveBot = null;
                    var bots = BotManager.Instance.GetActiveBots();
                    for (int i = 0; i < bots.Count; i++)
                    {
                        var b = bots[i];
                        if (b == null || b.IsDead) continue;
                        aliveCount++;
                        aliveBot = b;
                    }
                    if (aliveCount == 1 && aliveBot != null && !string.IsNullOrWhiteSpace(aliveBot.BotName))
                        return aliveBot.BotName;
                }
            }
            catch { }

            // Fallback: pick the bot whose team currently leads the take.
            try
            {
                if (BotManager.Instance != null && ScoreManager.Instance != null)
                {
                    var bots = BotManager.Instance.GetActiveBots();
                    int bestScore = int.MinValue;
                    string bestName = null;
                    for (int i = 0; i < bots.Count; i++)
                    {
                        var b = bots[i];
                        if (b == null || string.IsNullOrWhiteSpace(b.BotName)) continue;
                        int teamId = ScoreManager.Instance.GetTeamId(b.PlayerId);
                        int score = ScoreManager.Instance.GetRoundScore(teamId);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestName = b.BotName;
                        }
                    }
                    if (bestScore >= 0 && !string.IsNullOrWhiteSpace(bestName))
                        return bestName;
                }
            }
            catch { }

            // Last fallback: match leader by game points in case round score has already reset.
            try
            {
                if (BotManager.Instance != null && ScoreManager.Instance != null)
                {
                    var bots = BotManager.Instance.GetActiveBots();
                    int bestPoints = int.MinValue;
                    string bestName = null;
                    for (int i = 0; i < bots.Count; i++)
                    {
                        var b = bots[i];
                        if (b == null || string.IsNullOrWhiteSpace(b.BotName)) continue;
                        int teamId = ScoreManager.Instance.GetTeamId(b.PlayerId);
                        int points = ScoreManager.Instance.GetPoints(teamId);
                        if (points > bestPoints)
                        {
                            bestPoints = points;
                            bestName = b.BotName;
                        }
                    }
                    if (bestPoints >= 0 && !string.IsNullOrWhiteSpace(bestName))
                        return bestName;
                }
            }
            catch { }

            return null;
        }

        private static string StripRichText(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            var chars = new char[input.Length];
            int w = 0;
            bool inTag = false;
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) chars[w++] = c;
            }
            return new string(chars, 0, w);
        }

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

        private static bool IsBotOwnedWeapon(Weapon weapon)
        {
            if (weapon == null) return false;
            try
            {
                if (weapon.rootObject != null
                    && (weapon.rootObject.GetComponent<BotController>() != null
                        || weapon.rootObject.GetComponentInParent<BotController>() != null))
                    return true;
            }
            catch { }

            try
            {
                var item = weapon.GetComponent<ItemBehaviour>();
                if (item != null)
                {
                    if (item.rootObject != null
                        && (item.rootObject.GetComponent<BotController>() != null
                            || item.rootObject.GetComponentInParent<BotController>() != null))
                        return true;
                    if (item.lastPlayerHolder != null
                        && (item.lastPlayerHolder.GetComponent<BotController>() != null
                            || item.lastPlayerHolder.GetComponentInParent<BotController>() != null))
                        return true;
                }
            }
            catch { }

            try
            {
                return weapon.GetComponentInParent<BotController>() != null;
            }
            catch { return false; }
        }

        public static bool GunShootServer_Prefix(Gun __instance)
        {
            return !IsBotOwnedWeapon(__instance);
        }

        public static Exception GunShootServer_Finalizer(Gun __instance, Exception __exception)
        {
            if (__exception == null) return null;
            return IsBotOwnedWeapon(__instance) ? null : __exception;
        }

        private static void DisableBotHeldWeaponScripts(GameObject obj, GameObject player)
        {
            if (obj == null || player == null) return;

            var item = obj.GetComponent<ItemBehaviour>();
            if (item != null)
            {
                item.rootObject = player;
                item.lastPlayerHolder = player;
                item.enabled = false;
            }

            var weapon = obj.GetComponent<Weapon>();
            if (weapon != null)
            {
                weapon.rootObject = player;
                var pv = player.GetComponent<PlayerValues>();
                if (pv != null) weapon.playerValues = pv;
            }

            foreach (var wb in obj.GetComponents<MonoBehaviour>())
            {
                if (wb is Gun || wb is Shotgun || wb is ChargeGun || wb is Minigun
                    || wb is BeamGun || wb is LargeRaycastGun || wb is BumpGun
                    || wb is DualLauncher || wb is WeaponHandSpawner || wb is MeleeWeapon)
                    wb.enabled = false;
            }

            foreach (var mcc in obj.GetComponentsInChildren<MeleeChildCollision>(true))
                mcc.enabled = false;
        }

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
            DisableBotHeldWeaponScripts(obj, player);
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
