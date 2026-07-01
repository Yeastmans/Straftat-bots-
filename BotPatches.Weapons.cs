using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DG.Tweening;
using FishNet;
using TMPro;

namespace StraftatBots
{
    public static partial class BotPatches
    {
        private static readonly FieldInfo _kcEnemyField = typeof(KillCam).GetField("enemy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo _kcFreeCamField = typeof(KillCam).GetField("freeCam", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo _kcTimerField = typeof(KillCam).GetField("timer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly HashSet<int> _tabScoreSetupItems = new HashSet<int>();
        private static readonly Dictionary<int, TMP_Text> _tabScoreTexts = new Dictionary<int, TMP_Text>();
        private static readonly List<GameObject> _roundEndExtraObjects = new List<GameObject>();

        public static bool KillCamUpdate_Prefix(KillCam __instance)
        {
            try
            {
                if (!__instance.isDead) return true;

                Transform enemy = _kcEnemyField?.GetValue(__instance) as Transform;
                if (enemy == null) return true; // No enemy ref — let original handle it

                // Only intervene when killed BY a bot — force freecam since bot has no camera
                bool killerIsBot = enemy.GetComponent<BotController>() != null
                    || enemy.GetComponentInParent<BotController>() != null;

                if (killerIsBot)
                {
                    _kcFreeCamField?.SetValue(__instance, true);
                    if (_kcTimerField != null)
                    {
                        float timer = (float)_kcTimerField.GetValue(__instance);
                        if (timer > 0f) _kcTimerField.SetValue(__instance, -1f);
                    }
                }
                return true; // Always let original Update run
            }
            catch { return true; } // On ANY error, let original run — never block kill cam
        }

        // SceneMotor.Update — handled by finalizer (suppresses NRE), no copy-paste needed

        // Guard KillShockWave for bots
        public static bool KillShockWave_Prefix(FirstPersonController __instance)
        {
            if (__instance.GetComponent<BotController>() != null)
            {
                try { Settings.Instance.IncreaseKillsAmount(); } catch { }
                return false;
            }
            return true;
        }

        // Shared helper: get root object from PredictedProjectile, check if bot-owned
        private static readonly FieldInfo _ppRootField = typeof(PredictedProjectile).GetField("_rootObject", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo _ppWeaponField = typeof(PredictedProjectile).GetField("weapon", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static BotController GetProjectileOwnerBot(PredictedProjectile pp)
        {
            if (_ppRootField == null) return null;
            var rootObj = _ppRootField.GetValue(pp) as GameObject;
            return rootObj != null ? rootObj.GetComponent<BotController>() : null;
        }

        // Fix kill feed for bot-fired PredictedProjectile
        public static bool PredictedProjectile_SendKillLog_Prefix(PredictedProjectile __instance, PlayerHealth enemyHealth)
        {
            try
            {
                var rootObj = _ppRootField?.GetValue(__instance) as GameObject;
                if (rootObj == null) return true;
                var killerBot = rootObj.GetComponent<BotController>();
                var victimBot = enemyHealth.GetComponent<BotController>();
                if (killerBot == null && victimBot == null) return true;

                string weaponName = "Rocket";
                try
                {
                    var w = _ppWeaponField?.GetValue(__instance) as Weapon;
                    if (w != null) { var beh = w.GetComponent<ItemBehaviour>(); if (beh != null) weaponName = beh.weaponName; }
                }
                catch { }

                BotKillFeed.Write(enemyHealth, rootObj, killerBot != null ? killerBot.BotName : null, weaponName, "killed", true);
                return false;
            }
            catch { return true; }
        }

        // Skip HitMarker + KillShockWave for bot-fired PredictedProjectile
        public static bool PredictedProjectile_HitMarker_Prefix(PredictedProjectile __instance)
            => GetProjectileOwnerBot(__instance) == null;

        public static bool PredictedProjectile_KillShockWave_Prefix(PredictedProjectile __instance)
            => GetProjectileOwnerBot(__instance) == null;

        // Skip PhysicsGrenade.KillShockWave when the grenade was thrown by a bot.
        // The game accesses _rootObject.GetComponent<FirstPersonController>().lensDistortion
        // / .colorGrading — bots don't have post-processing volumes, so this NREs and aborts
        // HandleExplosion mid-execution (skipping damage to subsequent victims, ph.Explode,
        // screenshake, Destroy, explosionDecal, audio.Play). Result: bot grenades fail to
        // deal damage properly and their explosion VFX/SFX never play.
        private static readonly FieldInfo _pgRootField = typeof(PhysicsGrenade).GetField(
            "_rootObject", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static bool PhysicsGrenade_KillShockWave_Prefix(PhysicsGrenade __instance)
        {
            try
            {
                if (_pgRootField == null) return true;
                var rootObj = _pgRootField.GetValue(__instance) as GameObject;
                if (rootObj == null) return true;
                if (rootObj.GetComponent<BotController>() != null)
                {
                    // Still credit the kill stat like FirstPersonController.KillShockWave_Prefix does
                    try { Settings.Instance.IncreaseKillsAmount(); } catch { }
                    return false; // Skip original — prevents NRE on bot post-processing access
                }
            }
            catch { }
            return true;
        }

        // Game TaserEnemy RPC path assumes a real player client/camera stack and NREs on bots.
        // Route bot victims to bot-local stun handling instead.
        public static bool PlayerHealth_TaserEnemy_Prefix(PlayerHealth __instance, PlayerHealth enemyHealth, float stunTime)
        {
            try
            {
                var victim = enemyHealth != null ? enemyHealth : __instance;
                if (victim == null) return true;
                var bot = victim.GetComponent<BotController>() ?? victim.GetComponentInParent<BotController>();
                if (bot == null) return true;
                bot.ApplyStun(stunTime > 0f ? stunTime : 3f);
                return false;
            }
            catch { return false; }
        }

        private static void ApplyStunToVictim(PlayerHealth victim, float stunTime)
        {
            if (victim == null) return;
            var bot = victim.GetComponent<BotController>() ?? victim.GetComponentInParent<BotController>();
            if (bot != null)
            {
                bot.ApplyStun(stunTime > 0f ? stunTime : 3f);
                return;
            }
            try { victim.TaserEnemy(victim, stunTime > 0f ? stunTime : 3f); } catch { }
        }

        // Suppress Weapon.SendKillLog when the killer is a bot.
        // The original uses ClientInstance.PlayerNameTag (always the local host) as the killer name,
        // producing "Victim was killed by [HostName]". RegisterKill in BotController.Combat already
        // writes the correct "BotName killed Victim" message, so we just block the wrong one.
        public static bool Weapon_SendKillLog_Prefix(Weapon __instance, PlayerHealth enemyHealth)
        {
            try
            {
                if (__instance.rootObject != null && __instance.rootObject.GetComponent<BotController>() != null)
                    return false;
            }
            catch { }
            return true;
        }

        // Intercept KillServer RPC for bots — the original accesses client data that doesn't exist for bots
        public static bool KillServer_Prefix(Weapon __instance, PlayerHealth enemyHealth)
        {
            if (enemyHealth == null) return false;
            BotController bot = enemyHealth.GetComponent<BotController>();
            if (bot == null) return true; // Real player, let original run
            if (bot.IsDead || enemyHealth.isKilled) return false; // Already dead, skip (ragdoll hit)

            try
            {
                enemyHealth.health = -8f;
                enemyHealth.isKilled = true;
                enemyHealth.isShot = true;
                if (__instance.rootObject != null)
                    enemyHealth.killer = __instance.rootObject.transform;
                // Don't call PlayerDied here — bot's own Update→Die() handles it to avoid double call

                // Spawn ragdoll FIRST (reads bone positions from graphics), then disable physics
                try
                {
                    Vector3 dir = (__instance.rootObject != null)
                        ? (enemyHealth.transform.position - __instance.rootObject.transform.position).normalized
                        : -enemyHealth.transform.forward;
                    float force = 30f;
                    try { force = __instance.ragdollEjectForce; } catch { }
                    enemyHealth.ExplodeServer(false, true, "Torso", dir, force, enemyHealth.transform.position);
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"KillServer bot visual: {ex.Message}"); }

                // Disable all colliders + move to non-hittable layer so burst fire won't find this bot
                try
                {
                    // Layer 10 (ragdoll) — weapons don't CapsuleCast on this layer
                    enemyHealth.gameObject.layer = 10;
                    foreach (Transform child in enemyHealth.GetComponentsInChildren<Transform>(true))
                        child.gameObject.layer = 10;
                    var cc = enemyHealth.GetComponent<CharacterController>();
                    if (cc != null) cc.enabled = false;
                    foreach (var col in enemyHealth.GetComponentsInChildren<Collider>(true))
                        col.enabled = false;
                    if (enemyHealth.graphics != null) enemyHealth.graphics.SetActive(false);
                }
                catch { }
                try { enemyHealth.DisablePlayerObjectWhenKilled(); } catch { }

                // Reset burst state on the killer's weapon — BurstFire coroutine may have
                // been killed by the finalizer, leaving isBursting=true permanently
                try
                {
                    var burstField = GetField(__instance.GetType(), "isBursting");
                    if (burstField != null) burstField.SetValue(__instance, false);
                }
                catch { }

                // Trigger killer's kill effect and stats
                try
                {
                    if (__instance.rootObject != null)
                    {
                        var killerFpc = __instance.rootObject.GetComponent<FirstPersonController>();
                        if (killerFpc != null) killerFpc.KillShockWave();
                    }
                }
                catch { }

                // Write kill feed: player killed bot
                string killerName = "Player";
                if (__instance.rootObject != null)
                {
                    var killerPv = __instance.rootObject.GetComponent<PlayerValues>();
                    if (killerPv != null && killerPv.playerClient != null)
                        killerName = killerPv.playerClient.PlayerName;
                    // Check if killer is also a bot
                    var killerBot = __instance.rootObject.GetComponent<BotController>();
                    if (killerBot != null)
                        killerName = killerBot.BotName;
                }
                string weaponName = "weapon";
                var behaviour = __instance.GetComponent<ItemBehaviour>();
                if (behaviour != null) weaponName = behaviour.weaponName;

                // Bot-kills-bot: RegisterKill in BotController.Combat already wrote the kill feed.
                // Only write here for human-kills-bot (no RegisterKill on that path).
                var killerBotCheck = __instance.rootObject != null ? __instance.rootObject.GetComponent<BotController>() : null;
                if (killerBotCheck == null)
                {
                    try { BotKillFeed.Write(enemyHealth, __instance.rootObject, killerName, weaponName, "killed", true); } catch { }
                }

                // Call Die() explicitly — DisablePlayerObjectWhenKilled may deactivate the GameObject,
                // preventing Update→Die() from ever running
                Transform killerTransform = __instance.rootObject != null ? __instance.rootObject.transform : null;
                bot.Die(killerTransform);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"KillServer bot handler: {ex.Message}");
            }
            return false; // Skip original (would NRE)
        }

        // Handle PhysicsGrenade kills on bots — ensure death state is set, skip original (NREs)
        public static bool SendKillLog_Prefix(PhysicsGrenade __instance, PlayerHealth enemyHealth)
        {
            if (enemyHealth == null) return false;
            BotController victimBot = enemyHealth.GetComponent<BotController>();

            // Get rootObject (killer)
            GameObject rootObj = null;
            try
            {
                var field = GetField(typeof(PhysicsGrenade), "_rootObject");
                if (field != null) rootObj = field.GetValue(__instance) as GameObject;
            }
            catch { }

            BotController killerBot = rootObj != null ? rootObj.GetComponent<BotController>() : null;

            // If neither killer nor victim is a bot, let original run
            if (victimBot == null && killerBot == null) return true;

            // Set death state
            if (!enemyHealth.isKilled)
            {
                if (enemyHealth.health > 0f) enemyHealth.health = -8f;
                enemyHealth.isKilled = true;
                enemyHealth.isShot = true;
                if (rootObj != null) enemyHealth.killer = rootObj.transform;
            }

            // Kill feed
            try
            {
                WriteExplosiveKillFeed(rootObj, enemyHealth, ResolveExplosiveWeaponName(__instance));
            }
            catch { }

            return false; // Skip original (NRE on bot client data)
        }

        // Obus / Bubble KillShockWave — same NRE pattern as PhysicsGrenade.
        // Access `_rootObject.GetComponent<FirstPersonController>().lensDistortion` which is null
        // on bots (no post-processing volume), aborting HandleExplosion mid-loop and skipping
        // SendKillLog + ph.Explode + SetKiller for the victim.
        // _rootObject lives in a base class of Obus/Bubble — search the full hierarchy.
        private static FieldInfo FindFieldInHierarchy(System.Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField(name, flags);
                if (f != null) return f;
            }
            return null;
        }

        private static readonly FieldInfo _obusRootField  = FindFieldInHierarchy(typeof(Obus), "_rootObject");
        private static readonly FieldInfo _obusGunField   = FindFieldInHierarchy(typeof(Obus), "_gun");
        private static readonly FieldInfo _obusCharField  = FindFieldInHierarchy(typeof(Obus), "character");
        private static readonly FieldInfo _bubbleRootField = FindFieldInHierarchy(typeof(Bubble), "_rootObject");
        private static readonly FieldInfo _bubbleGunField  = FindFieldInHierarchy(typeof(Bubble), "_gun");
        private static readonly FieldInfo _bubbleCharField = FindFieldInHierarchy(typeof(Bubble), "character");
        private static readonly FieldInfo _bubbleDamageField = FindFieldInHierarchy(typeof(Bubble), "damage");
        private static readonly FieldInfo _bubbleRagdollForceField = FindFieldInHierarchy(typeof(Bubble), "ragdollEjectForce");
        private static readonly MethodInfo _bubbleHandleExplosionMethod = typeof(Bubble).GetMethod(
            "HandleExplosion", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new System.Type[] { typeof(Vector3) }, null);

        // Registry for bot-fired Obus/Bubble instances — FishNet clears _rootObject during
        // NetworkInitializeEarly, so we track ownership at fire time instead.
        internal static readonly System.Collections.Generic.Dictionary<object, BotController> BotProjectileOwners
            = new System.Collections.Generic.Dictionary<object, BotController>();
        private static readonly System.Collections.Generic.Dictionary<int, int> _recentBubbleHumanDamageFrame
            = new System.Collections.Generic.Dictionary<int, int>();

        internal static void RegisterBotProjectile(object projectile, BotController bot)
        {
            if (projectile == null || bot == null) return;
            BotProjectileOwners[projectile] = bot;
            if (projectile is Component comp && comp.gameObject != null)
            {
                BotProjectileOwners[comp.gameObject] = bot;
                foreach (var mb in comp.gameObject.GetComponents<MonoBehaviour>())
                {
                    if (mb != null) BotProjectileOwners[mb] = bot;
                }
            }
        }

        private static BotController GetProjectileOwnerBot(object projectile)
        {
            if (projectile == null) return null;
            BotProjectileOwners.TryGetValue(projectile, out var bot);
            return bot;
        }

        public static bool Obus_KillShockWave_Prefix(Obus __instance)
        {
            try
            {
                var rootObj = ResolveProjectileRoot(__instance, _obusRootField, _obusCharField);
                if (rootObj != null && rootObj.GetComponent<BotController>() != null)
                {
                    try { Settings.Instance.IncreaseKillsAmount(); } catch { }
                    return false; // Skip original (would NRE on bot lensDistortion access)
                }
            }
            catch { }
            return true;
        }

        public static bool Bubble_KillShockWave_Prefix(Bubble __instance)
        {
            try
            {
                var rootObj = ResolveProjectileRoot(__instance, _bubbleRootField, _bubbleCharField);
                if (rootObj != null && rootObj.GetComponent<BotController>() != null)
                {
                    try { Settings.Instance.IncreaseKillsAmount(); } catch { }
                    return false;
                }
            }
            catch { }
            return true;
        }

        // Obus / Bubble SendKillLog — game's version uses ClientInstance.Instance.PlayerNameTag
        // for killer, which is always the LOCAL human even when the Obus was fired by a bot.
        // Replace with bot-aware kill feed entry.
        public static bool Obus_SendKillLog_Prefix(Obus __instance, PlayerHealth enemyHealth)
        {
            GameObject rootObj = ResolveProjectileRoot(__instance, _obusRootField, _obusCharField);
            return WriteDualLauncherKillFeed(rootObj, _obusGunField?.GetValue(__instance) as GameObject, enemyHealth, "dual launcher");
        }

        public static bool Bubble_SendKillLog_Prefix(Bubble __instance, PlayerHealth enemyHealth)
        {
            GameObject rootObj = ResolveProjectileRoot(__instance, _bubbleRootField, _bubbleCharField);
            return WriteDualLauncherKillFeed(rootObj, _bubbleGunField?.GetValue(__instance) as GameObject, enemyHealth, "Bublee");
        }

        public static void ObusInitialize_Postfix(Obus __instance, GameObject rootObject)
        {
            RegisterProjectileOwnerFromRoot(__instance, rootObject);
        }

        public static void BubbleInitialize_Postfix(Bubble __instance, GameObject rootObject)
        {
            RegisterProjectileOwnerFromRoot(__instance, rootObject);
        }

        public static bool Bubble_OnCollisionEnter_Prefix(Bubble __instance, Collision other)
        {
            try
            {
                GameObject rootObj = ResolveProjectileRoot(__instance, _bubbleRootField, _bubbleCharField);
                if (rootObj == null) return true;
                PlayerHealth victim = other != null ? other.transform.GetComponentInParent<PlayerHealth>() : null;
                if (IsVictimRoot(rootObj, victim))
                    return false;
                if (TryApplyBotBubbleHumanHit(__instance, other, rootObj, victim))
                    return false;
            }
            catch { }
            return true;
        }

        public static void Bubble_Update_Postfix(Bubble __instance)
        {
            if (!FishNet.InstanceFinder.IsServer) return;
            try
            {
                GameObject rootObj = ResolveProjectileRoot(__instance, _bubbleRootField, _bubbleCharField);
                if (rootObj == null || rootObj.GetComponent<BotController>() == null) return;

                Collider[] hits = Physics.OverlapSphere(
                    __instance.transform.position, 2.25f, (1 << 11) | (1 << 16), QueryTriggerInteraction.Collide);

                var handled = new System.Collections.Generic.HashSet<PlayerHealth>();
                foreach (var hit in hits)
                {
                    PlayerHealth ph = hit != null ? hit.GetComponentInParent<PlayerHealth>() : null;
                    if (ph == null || handled.Contains(ph)) continue;
                    handled.Add(ph);
                    if (ph.GetComponent<BotController>() != null) continue;
                    if (IsVictimRoot(rootObj, ph)) continue;

                    if (TryApplyBotBubbleHumanHit(__instance, null, rootObj, ph))
                        return;
                }
            }
            catch { }
        }

        private static bool TryApplyBotBubbleHumanHit(Bubble bubble, Collision other, GameObject rootObj, PlayerHealth victim)
        {
            if (bubble == null || rootObj == null || victim == null) return false;

            BotController killerBot = rootObj.GetComponent<BotController>();
            if (killerBot == null || victim.GetComponent<BotController>() != null) return false;
            if (!FishNet.InstanceFinder.IsServer) return false;

            if (victim.isKilled || victim.health <= 0f)
            {
                InvokeBubbleExplosion(bubble);
                return true;
            }

            bubble.isOwner = true;

            float damage = ReadFloatFieldValue(_bubbleDamageField, bubble, 10f);
            float ragdollForce = ReadFloatFieldValue(_bubbleRagdollForceField, bubble, 30f);
            Vector3 hitPoint = GetCollisionPoint(other, bubble.transform.position);
            Vector3 direction = victim.transform.position - bubble.transform.position;
            if (direction.sqrMagnitude < 0.001f)
                direction = bubble.transform.forward;
            direction.Normalize();

            bool fatal = victim.health - damage <= 0f;
            if (!TryMarkBubbleHumanDamage(bubble, victim))
            {
                InvokeBubbleExplosion(bubble);
                return true;
            }

            try { victim.SetKiller(rootObj.transform); } catch { }
            try { victim.RemoveHealth(damage); } catch { }

            if (fatal || victim.health <= 0f)
            {
                try { victim.ChangeKilledState(true); } catch { }
                victim.isShot = true;
                try { Settings.Instance.IncreaseKillsAmount(); } catch { }

                string hitName = GetCollisionObjectName(other, victim);
                try { victim.ExplodeServer(false, true, hitName, direction, ragdollForce, hitPoint); } catch { }

                try { WriteExplosiveKillFeed(rootObj, victim, "Bublee"); } catch { }
                try
                {
                    BotDamageSync.SyncKill(victim, killerBot.BotName, "Bublee", false,
                        direction, ragdollForce, hitPoint, hitName);
                }
                catch { }
            }

            InvokeBubbleExplosion(bubble);
            return true;
        }

        private static void ApplyBotBubbleHumanExplosionDamage(MonoBehaviour projectile, GameObject rootObj, float damage, float ragdollForce, Collider[] hits)
        {
            if (!(projectile is Bubble) || rootObj == null || hits == null) return;

            BotController killerBot = rootObj.GetComponent<BotController>();
            if (killerBot == null) return;

            string weaponName = ResolveExplosiveWeaponName(projectile);
            var handled = new System.Collections.Generic.HashSet<PlayerHealth>();
            foreach (var hit in hits)
            {
                PlayerHealth ph = null;
                try { ph = hit != null ? hit.GetComponentInParent<PlayerHealth>() : null; } catch { }
                if (ph == null || handled.Contains(ph)) continue;
                handled.Add(ph);

                if (ph.GetComponent<BotController>() != null) continue;
                if (IsVictimRoot(rootObj, ph)) continue;
                if (ph.isKilled || ph.health <= 0f) continue;
                if (!TryMarkBubbleHumanDamage(projectile, ph)) continue;

                Vector3 direction = ph.transform.position - projectile.transform.position;
                if (direction.sqrMagnitude < 0.001f)
                    direction = projectile.transform.forward;
                direction.Normalize();

                Vector3 hitPoint = hit != null ? hit.ClosestPoint(projectile.transform.position) : projectile.transform.position;
                bool fatal = ph.health - damage <= 0f;

                try { ph.SetKiller(rootObj.transform); } catch { }
                try { ph.RemoveHealth(damage); } catch { }

                if (fatal || ph.health <= 0f)
                {
                    try { ph.ChangeKilledState(true); } catch { }
                    ph.isShot = true;
                    try { Settings.Instance.IncreaseKillsAmount(); } catch { }
                    try { ph.ExplodeServer(false, true, "Torso", direction, ragdollForce, hitPoint); } catch { }
                    try { WriteExplosiveKillFeed(rootObj, ph, weaponName); } catch { }
                    try
                    {
                        BotDamageSync.SyncKill(ph, killerBot.BotName, weaponName, false,
                            direction, ragdollForce, hitPoint, "Torso");
                    }
                    catch { }
                }
            }
        }

        private static bool TryMarkBubbleHumanDamage(MonoBehaviour projectile, PlayerHealth victim)
        {
            if (projectile == null || victim == null) return false;
            int key = (projectile.GetInstanceID() * 397) ^ victim.GetInstanceID();
            int frame = Time.frameCount;
            if (_recentBubbleHumanDamageFrame.TryGetValue(key, out int lastFrame) && frame - lastFrame < 3)
                return false;

            _recentBubbleHumanDamageFrame[key] = frame;
            if (_recentBubbleHumanDamageFrame.Count > 128)
            {
                var stale = new System.Collections.Generic.List<int>();
                foreach (var kv in _recentBubbleHumanDamageFrame)
                    if (frame - kv.Value > 120) stale.Add(kv.Key);
                foreach (var staleKey in stale) _recentBubbleHumanDamageFrame.Remove(staleKey);
            }
            return true;
        }

        private static float ReadFloatFieldValue(FieldInfo field, object instance, float fallback)
        {
            try
            {
                if (field != null)
                    return Convert.ToSingle(field.GetValue(instance));
            }
            catch { }
            return fallback;
        }

        private static Vector3 GetCollisionPoint(Collision collision, Vector3 fallback)
        {
            try
            {
                ContactPoint[] contacts = collision != null ? collision.contacts : null;
                if (contacts != null && contacts.Length > 0)
                    return contacts[0].point;
            }
            catch { }
            return fallback;
        }

        private static string GetCollisionObjectName(Collision collision, PlayerHealth victim)
        {
            try
            {
                if (collision != null && collision.collider != null && collision.collider.gameObject != null)
                    return collision.collider.gameObject.name;
            }
            catch { }
            return victim != null && victim.gameObject != null ? victim.gameObject.name : "Torso";
        }

        private static void InvokeBubbleExplosion(Bubble bubble)
        {
            if (bubble == null) return;
            try
            {
                if (_bubbleHandleExplosionMethod != null)
                {
                    _bubbleHandleExplosionMethod.Invoke(bubble, new object[] { bubble.transform.position });
                    return;
                }
            }
            catch { }

            try { UnityEngine.Object.Destroy(bubble.gameObject); } catch { }
        }

        private static void RegisterProjectileOwnerFromRoot(Component projectile, GameObject rootObject)
        {
            if (projectile == null || rootObject == null) return;
            var bot = rootObject.GetComponent<BotController>();
            if (bot != null) RegisterBotProjectile(projectile, bot);
        }

        private static GameObject ResolveProjectileRoot(object instance, FieldInfo rootField, FieldInfo charField)
        {
            // 1. Registry (set at fire time, before FishNet clears _rootObject)
            var reg = GetProjectileOwnerBot(instance);
            if (reg != null) return reg.gameObject;
            // 2. _rootObject field (may be null if FishNet cleared it)
            var root = rootField?.GetValue(instance) as GameObject;
            if (root != null) return root;
            // 3. character field fallback
            var charVal = charField?.GetValue(instance);
            if (charVal is Component comp) return comp.gameObject;
            if (charVal is GameObject go) return go;
            return null;
        }

        private static bool IsVictimRoot(GameObject rootObj, PlayerHealth victim)
        {
            if (rootObj == null || victim == null) return false;
            if (victim.gameObject == rootObj) return true;
            if (victim.transform != null && victim.transform.root == rootObj.transform) return true;
            var victimBot = victim.GetComponent<BotController>();
            return victimBot != null && victimBot.gameObject == rootObj;
        }

        private static bool IsOwnProjectileVictim(MonoBehaviour projectile, GameObject rootObj, PlayerHealth victim)
        {
            if (projectile == null || !IsVictimRoot(rootObj, victim)) return false;
            return projectile is Bubble
                || projectile is Obus
                || projectile is PhysicsGrenade
                || projectile is HandGrenade
                || projectile is HandGrenadeTwo;
        }

        private static bool WriteDualLauncherKillFeed(GameObject rootObj, GameObject gun, PlayerHealth enemyHealth, string defaultWeaponName)
        {
            try
            {
                if (enemyHealth == null) return false;
                BotController killerBot = rootObj != null ? rootObj.GetComponent<BotController>() : null;
                BotController victimBot = enemyHealth.GetComponent<BotController>();
                if (killerBot == null && victimBot == null) return true; // Let original run for human-only

                string weaponName = defaultWeaponName;
                try
                {
                    if (gun != null)
                    {
                        var beh = gun.GetComponent<ItemBehaviour>();
                        if (beh != null && !string.IsNullOrEmpty(beh.weaponName)) weaponName = beh.weaponName;
                    }
                }
                catch { }

                // Route through the shared dedup path so the postfix on HandleExplosion and
                // this prefix can't both emit a line for the same victim.
                WriteExplosiveKillFeed(rootObj, enemyHealth, weaponName);
            }
            catch { }
            return false; // Skip original
        }

        // Cached FieldInfo for explosive types — avoids per-explosion GetField calls
        private static readonly System.Collections.Generic.Dictionary<System.Type, FieldInfo[]> _explosiveFieldCache
            = new System.Collections.Generic.Dictionary<System.Type, FieldInfo[]>();

        private static FieldInfo[] GetExplosiveFields(System.Type type)
        {
            if (_explosiveFieldCache.TryGetValue(type, out var cached)) return cached;
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var fields = new FieldInfo[]
            {
                type.GetField("explosionRadius", bf),  // [0]
                type.GetField("damage", bf),            // [1]
                type.GetField("ragdollEjectForce", bf), // [2]
                type.GetField("_rootObject", bf),       // [3]
                type.GetField("rayLength", bf),         // [4]
            };
            _explosiveFieldCache[type] = fields;
            return fields;
        }

        public static void Explosion_Postfix(MonoBehaviour __instance)
        {
            if (!FishNet.InstanceFinder.IsServer) return;
            try
            {
                var fields = GetExplosiveFields(__instance.GetType());
                float radius = fields[0] != null ? (float)fields[0].GetValue(__instance) : 5f;
                float damage = fields[1] != null ? (float)fields[1].GetValue(__instance) : 10f;
                float ragdollForce = fields[2] != null ? (float)fields[2].GetValue(__instance) : 30f;
                GameObject rootObj = fields[3] != null ? fields[3].GetValue(__instance) as GameObject : null;
                if (rootObj == null && __instance is Bubble)
                    rootObj = ResolveProjectileRoot(__instance, _bubbleRootField, _bubbleCharField);
                else if (rootObj == null && __instance is Obus)
                    rootObj = ResolveProjectileRoot(__instance, _obusRootField, _obusCharField);
                else if (rootObj == null && __instance is PhysicsGrenade)
                    rootObj = ResolveProjectileRoot(__instance, _pgRootField, null);
                if (__instance is Bubble && rootObj != null && rootObj.GetComponent<BotController>() != null)
                    radius = Mathf.Max(radius, 4.5f);
                string resolvedWeaponName = ResolveExplosiveWeaponName(__instance);
                bool isGlandGrenade = !string.IsNullOrWhiteSpace(resolvedWeaponName)
                    && resolvedWeaponName.IndexOf("gland", StringComparison.OrdinalIgnoreCase) >= 0;
                bool isStunMine = IsStunMineInstance(__instance);
                bool isStunGrenade = IsStunGrenadeInstance(__instance);
                bool isStunExplosive = isStunMine || isStunGrenade;
                radius = Mathf.Clamp(radius, 0.5f, 8f);
                damage = Mathf.Clamp(damage, 0f, 200f);
                ragdollForce = Mathf.Clamp(ragdollForce, 1f, 120f);
                if (isGlandGrenade)
                {
                    radius = Mathf.Min(radius, 3.25f);
                    damage = Mathf.Min(damage, 65f);
                }
                // Claymore + Proximity Mine = instant kill. AP Mine (ProximityMine with low damage) = multi-hit at 50 dmg.
                bool isAPMine = IsApMineInstance(__instance);
                bool isInstantKill = __instance is Claymore;
                if (__instance is ProximityMine && !isAPMine && !isStunMine)
                    isInstantKill = true; // Proximity mine = instant kill
                if (isAPMine)
                    damage = 50f; // AP mine: 2 hits to kill (50 x 2 = 100)

                // Claymores use OverlapBox, not sphere — use larger sphere as approximation
                if (__instance is Claymore) radius = 6f;

                if (isInstantKill)
                {
                    // Claymores/mines: only kill the player who triggered it (raycast hit)
                    // The game's own HandleExplosion uses a raycast forward — find who it detected
                    PlayerHealth triggerVictim = null;

                    // Raycast forward (same as Claymore.Update detection)
                    float rayLen = fields[4] != null ? (float)fields[4].GetValue(__instance) : 5f;

                    if (Physics.Raycast(__instance.transform.position, __instance.transform.forward, out RaycastHit triggerHit, rayLen, (1 << 11)))
                    {
                        triggerVictim = triggerHit.collider.GetComponentInParent<PlayerHealth>();
                    }

                    // Fallback: closest player in radius if raycast missed
                    if (triggerVictim == null)
                    {
                        Collider[] hits = Physics.OverlapSphere(__instance.transform.position, radius, (1 << 11));
                        float closest = float.MaxValue;
                        foreach (var hit in hits)
                        {
                            var ph = hit.GetComponentInParent<PlayerHealth>();
                            if (ph == null || ph.isKilled || ph.health <= 0f) continue;
                            float d = Vector3.Distance(__instance.transform.position, ph.transform.position);
                            if (d < closest) { closest = d; triggerVictim = ph; }
                        }
                    }

                    if (triggerVictim != null && !triggerVictim.isKilled && triggerVictim.health > 0f)
                    {
                        Vector3 explosionDir = (triggerVictim.transform.position - __instance.transform.position).normalized;

                        var victimBot = triggerVictim.GetComponent<BotController>();

                        // Set death state
                        triggerVictim.health = -8f;
                        triggerVictim.isKilled = true;
                        triggerVictim.isShot = true;
                        if (rootObj != null) triggerVictim.killer = rootObj.transform;

                        if (victimBot != null)
                        {
                            // Bot victim — Die first (stops movement/AI), then ragdoll
                            // Die() disables the component and hides graphics
                            if (!victimBot.IsDead)
                                victimBot.Die(rootObj != null ? rootObj.transform : null);

                            // Ragdoll AFTER Die — ExplodeServer reads whatever bone state remains
                            // The graphics were just hidden by Die, but ExplodeServer spawns a separate
                            // ragdoll object from the prefab, it doesn't need the graphics visible
                            try { triggerVictim.ExplodeServer(false, true, "Torso", explosionDir, ragdollForce, __instance.transform.position); } catch { }
                        }
                        else
                        {
                            // Human victim — ExplodeServer handles ragdoll, game handles death via SyncVars
                            try { triggerVictim.ExplodeServer(false, true, "Torso", explosionDir, ragdollForce, __instance.transform.position); } catch { }
                        }

                        // Kill feed
                        try { WriteExplosiveKillFeed(rootObj, triggerVictim, ResolveExplosiveWeaponName(__instance)); }
                        catch { }
                    }
                }
                else
                {
                    // Non-instant explosions (grenades, AP mines, Bubble etc)
                    // Game's HandleExplosion has IsOwner checks that skip server-owned bots
                    // We apply damage to bots explicitly, then call Die() if killed
                    int hitMask = __instance is Bubble ? ((1 << 11) | (1 << 16)) : (1 << 11);
                    Collider[] hits = Physics.OverlapSphere(__instance.transform.position, radius, hitMask);
                    var handled = new System.Collections.Generic.HashSet<BotController>();

                    Vector3 blastOrigin = __instance.transform.position + Vector3.up * 0.1f;
                    foreach (var hit in hits)
                    {
                        BotController victimBot = hit.GetComponentInParent<BotController>();
                        if (victimBot == null || victimBot.IsDead || handled.Contains(victimBot)) continue;
                        handled.Add(victimBot);

                        var ph = victimBot.GetComponent<PlayerHealth>();
                        if (ph == null || ph.isKilled) continue;
                        if (IsOwnProjectileVictim(__instance, rootObj, ph)) continue;

                        Vector3 targetPoint = ph.transform.position + Vector3.up * 0.8f;
                        float dist = Vector3.Distance(blastOrigin, targetPoint);
                        if (dist > radius) continue;

                        if (isStunExplosive)
                        {
                            ApplyStunToVictim(ph, 3f);
                            continue;
                        }

                        // Apply damage to bot (game's IsOwner check skips server-owned bots)
                        if (ph.health > 0f && damage > 0f)
                        {
                            float falloff = 1f - Mathf.Clamp01(dist / radius);
                            float minEdgeDamageScale = isGlandGrenade ? 0.15f : 0.35f;
                            float appliedDamage = Mathf.Max(1f, damage * Mathf.Lerp(minEdgeDamageScale, 1f, falloff));
                            try { ph.RemoveHealth(appliedDamage); } catch { }
                            if (rootObj != null)
                                try { ph.SetKiller(rootObj.transform); } catch { }
                        }

                        // Check if bot was killed by this or accumulated damage
                        if (ph.isKilled || ph.health <= 0f)
                        {
                            if (!victimBot.IsDead)
                            {
                                victimBot.Die(rootObj != null ? rootObj.transform : null);

                                try
                                {
                                    Vector3 explosionDir = (ph.transform.position - __instance.transform.position).normalized;
                                    ph.ExplodeServer(false, true, "Torso", explosionDir, ragdollForce, __instance.transform.position);
                                }
                                catch { }
                            }

                            // Kill feed — game's SendKillLog never runs because KillShockWave NREs on bots
                            try { WriteExplosiveKillFeed(rootObj, ph, resolvedWeaponName); }
                            catch { }
                        }
                    }

                    ApplyBotBubbleHumanExplosionDamage(__instance, rootObj, damage, ragdollForce, hits);

                    // Kill feed for human victims killed by bot-thrown explosive.
                    // PhysicsGrenade.SendKillLog may be JIT-inlined, bypassing the Harmony prefix.
                    // This postfix catches those misses. WriteExplosiveKillFeed deduplicates if both fire.
                    try
                    {
                        BotController expKillerBot = rootObj != null ? rootObj.GetComponent<BotController>() : null;
                        if (expKillerBot != null)
                        {
                            string expWeaponName = resolvedWeaponName;
                            var humanHandled = new System.Collections.Generic.HashSet<PlayerHealth>();
                            foreach (var hit2 in hits)
                            {
                                var hph = hit2.GetComponentInParent<PlayerHealth>();
                                if (hph == null || humanHandled.Contains(hph)) continue;
                                if (hph.GetComponent<BotController>() != null) continue;
                                humanHandled.Add(hph);
                                if (hph.isKilled && hph.killer == rootObj.transform)
                                    WriteExplosiveKillFeed(rootObj, hph, expWeaponName);
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Resolve a user-facing weapon name from an explosive MonoBehaviour
        // Used by both Explosion_Postfix (non-instant branch) and ObusExplosion_Postfix for kill feed
        internal static string ResolveExplosiveWeaponName(MonoBehaviour instance)
        {
            if (instance == null) return "explosive";
            if (instance is Claymore) return "Claymore";
            if (instance is ProximityMine)
                return IsApMineInstance(instance) ? "AP Mine" : "Proximity Mine";
            if (instance is Obus) return "Serac";
            if (instance is Bubble) return "Bublee";
            if (instance is HandGrenade || instance is HandGrenadeTwo || instance is PhysicsGrenade)
            {
                string reflectedName = TryResolveWeaponNameFromInstance(instance);
                return !string.IsNullOrWhiteSpace(reflectedName) ? reflectedName : "grenade";
            }
            return TryResolveWeaponNameFromInstance(instance) ?? instance.GetType().Name;
        }

        private static string TryResolveWeaponNameFromInstance(MonoBehaviour instance)
        {
            if (instance == null) return null;
            try
            {
                string direct = TryResolveWeaponNameFromObject(instance);
                if (!string.IsNullOrWhiteSpace(direct)) return direct;

                foreach (string fieldName in new[] { "weaponName", "_gun", "gun", "weapon" })
                {
                    var field = GetField(instance.GetType(), fieldName);
                    object value = field != null ? field.GetValue(instance) : null;
                    string resolved = TryResolveWeaponNameFromObject(value);
                    if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
                }
            }
            catch { }
            return null;
        }

        private static string TryResolveWeaponNameFromObject(object value)
        {
            try
            {
                if (value == null) return null;
                if (value is string s)
                    return CleanWeaponName(s);
                if (value is ItemBehaviour item && !string.IsNullOrWhiteSpace(item.weaponName))
                    return CleanWeaponName(item.weaponName);
                if (value is Weapon weapon)
                {
                    if (weapon.behaviour != null && !string.IsNullOrWhiteSpace(weapon.behaviour.weaponName))
                        return CleanWeaponName(weapon.behaviour.weaponName);
                    var itemBehaviour = weapon.GetComponent<ItemBehaviour>();
                    if (itemBehaviour != null && !string.IsNullOrWhiteSpace(itemBehaviour.weaponName))
                        return CleanWeaponName(itemBehaviour.weaponName);
                }
                if (value is GameObject go)
                    return TryResolveWeaponNameFromObject(go.GetComponent<ItemBehaviour>())
                        ?? TryResolveWeaponNameFromObject(go.GetComponent<Weapon>());
                if (value is Component comp)
                    return TryResolveWeaponNameFromObject(comp.GetComponent<ItemBehaviour>())
                        ?? TryResolveWeaponNameFromObject(comp.GetComponent<Weapon>());
            }
            catch { }
            return null;
        }

        private static string CleanWeaponName(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName)) return null;
            return weaponName.Replace("(Clone)", "").Trim();
        }

        private static bool IsApMineInstance(MonoBehaviour instance)
        {
            if (!(instance is ProximityMine)) return false;

            try
            {
                var apDirect = GetField(instance.GetType(), "apmine");
                if (apDirect != null && apDirect.FieldType == typeof(bool) && (bool)apDirect.GetValue(instance))
                    return true;
            }
            catch { }

            try
            {
                if (ContainsApMineText(instance.name)) return true;
            }
            catch { }

            try
            {
                var weaponNameField = GetField(instance.GetType(), "weaponName");
                if (weaponNameField != null && ContainsApMineText(weaponNameField.GetValue(instance) as string))
                    return true;
            }
            catch { }

            try
            {
                var weaponField = GetField(instance.GetType(), "weapon");
                object weapon = weaponField != null ? weaponField.GetValue(instance) : null;
                if (weapon != null)
                {
                    var apField = GetField(weapon.GetType(), "apmine");
                    if (apField != null && apField.FieldType == typeof(bool) && (bool)apField.GetValue(weapon))
                        return true;
                }
            }
            catch { }

            try
            {
                var damageField = GetField(instance.GetType(), "damage");
                if (damageField != null && Convert.ToSingle(damageField.GetValue(instance)) >= 40f)
                    return true;
            }
            catch { }

            return false;
        }

        private static bool IsStunMineInstance(MonoBehaviour instance)
        {
            if (!(instance is ProximityMine)) return false;
            try
            {
                foreach (string fieldName in new[] { "stunMine", "isStunMine", "stunGrenade", "isStun" })
                {
                    var f = GetField(instance.GetType(), fieldName);
                    if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(instance))
                        return true;
                }
                foreach (string fieldName in new[] { "weaponName", "name" })
                {
                    var f = GetField(instance.GetType(), fieldName);
                    var s = f != null ? f.GetValue(instance) as string : null;
                    if (!string.IsNullOrWhiteSpace(s) && s.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsStunGrenadeInstance(MonoBehaviour instance)
        {
            if (!(instance is PhysicsGrenade)) return false;
            try
            {
                foreach (string fieldName in new[] { "stunGrenade", "isStunGrenade", "stun" })
                {
                    var f = GetField(instance.GetType(), fieldName);
                    if (f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(instance))
                        return true;
                }
                string weaponName = ResolveExplosiveWeaponName(instance);
                if (!string.IsNullOrWhiteSpace(weaponName)
                    && weaponName.IndexOf("stun", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            catch { }
            return false;
        }

        private static bool ContainsApMineText(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string lower = value.ToLowerInvariant();
            return lower.Contains("apmine")
                || lower.Contains("ap mine")
                || lower.Contains("ap-mine")
                || lower.Contains("ap_mine")
                || lower.Contains("anti-personnel");
        }

        // Dedup: prevent double kill-feed entries when both the postfix and
        // a prefix (or multiple hit colliders on the same victim) try to write.
        // Key is victim PlayerHealth.GetInstanceID; value is Time.frameCount of last write.
        private static readonly System.Collections.Generic.Dictionary<int, int> _recentKillFeedFrame
            = new System.Collections.Generic.Dictionary<int, int>();

        private static bool TryMarkKillFeedWritten(PlayerHealth victim)
        {
            if (victim == null) return false;
            int key = victim.GetInstanceID();
            int frame = Time.frameCount;
            if (_recentKillFeedFrame.TryGetValue(key, out int lastFrame) && (frame - lastFrame) < 10)
                return false;
            _recentKillFeedFrame[key] = frame;
            // Opportunistic cleanup: drop stale entries (>600 frames ~ 10s at 60fps)
            if (_recentKillFeedFrame.Count > 64)
            {
                var stale = new System.Collections.Generic.List<int>();
                foreach (var kv in _recentKillFeedFrame)
                    if (frame - kv.Value > 600) stale.Add(kv.Key);
                foreach (var k in stale) _recentKillFeedFrame.Remove(k);
            }
            return true;
        }

        // Write the canonical explosive kill-feed line, subject to dedup.
        private static void WriteExplosiveKillFeed(GameObject rootObj, PlayerHealth victim, string weaponName)
        {
            if (victim == null || PauseManager.Instance == null) return;
            if (!TryMarkKillFeedWritten(victim)) return;

            BotKillFeed.Write(victim, rootObj, null, weaponName, "killed", true);
        }

        // Obus (Serac) explosion — takes Vector3 position parameter.
        // The game's Obus.HandleExplosion already runs its OWN damage loop (ChangeKilledState +
        // RemoveHealth + Explode + SetKiller) when isOwner is true, which it is on the host for
        // bot-fired Obus. That loop's inline SendKillLog call gets JIT-inlined on the tiny
        // SendKillLog method, bypassing our Harmony prefix — so the kill feed never writes
        // from the prefix path. This postfix handles it instead: it runs after the damage
        // loop and detects victims killed by this explosion (killer transform matches rootObj)
        // and writes the feed for them. Dedup prevents double-print if the prefix DID fire.
        public static void ObusExplosion_Postfix(MonoBehaviour __instance, Vector3 position)
        {
            if (!FishNet.InstanceFinder.IsServer) return;
            try
            {
                float radius = 3f;
                float damage = 10f;
                GameObject rootObj = null;

                var radiusField = GetField(__instance.GetType(), "explosionRadius");
                if (radiusField != null) radius = (float)radiusField.GetValue(__instance);

                var rootField = GetField(__instance.GetType(), "_rootObject");
                if (rootField != null) rootObj = rootField.GetValue(__instance) as GameObject;
                if (rootObj == null)
                    rootObj = ResolveProjectileRoot(__instance, _obusRootField, _obusCharField);

                Transform rootTf = rootObj != null ? rootObj.transform : null;
                string weaponName = ResolveExplosiveWeaponName(__instance);

                Collider[] hits = Physics.OverlapSphere(position, radius, (1 << 11));
                var seen = new System.Collections.Generic.HashSet<PlayerHealth>();

                foreach (var hit in hits)
                {
                    var ph = hit.GetComponentInParent<PlayerHealth>();
                    if (ph == null || seen.Contains(ph)) continue;
                    seen.Add(ph);
                    if (IsOwnProjectileVictim(__instance, rootObj, ph)) continue;

                    bool killedByThis = ph.isKilled && rootTf != null && ph.killer == rootTf;

                    if (killedByThis)
                    {
                        // Game's damage loop already killed this victim and set killer to rootObj.
                        // Write the kill feed the inlined SendKillLog never wrote.
                        var victimBot = ph.GetComponent<BotController>();
                        if (victimBot != null && !victimBot.IsDead)
                        {
                            try
                            {
                                BotController.DisableBotPhysicsPublic(ph.gameObject);
                                ph.DisablePlayerObjectWhenKilled();
                            }
                            catch { }
                            victimBot.Die(rootTf);
                        }
                        WriteExplosiveKillFeed(rootObj, ph, weaponName);
                    }
                    else if (!ph.isKilled)
                    {
                        // Original's damage loop didn't reach this victim (e.g. isOwner was false
                        // for some reason, or the victim was outside its OverlapSphere). Apply
                        // damage ourselves — this is the legacy fallback path.
                        if ((ph.health - damage) <= 0f)
                        {
                            ph.health = -8f;
                            ph.isKilled = true;
                            ph.isShot = true;
                            if (rootTf != null) ph.killer = rootTf;

                            Vector3 dir = (ph.transform.position - position).normalized;
                            try { ph.ExplodeServer(false, true, "Torso", dir, 30f, position); } catch { }

                            var victimBot = ph.GetComponent<BotController>();
                            if (victimBot != null && !victimBot.IsDead)
                            {
                                try
                                {
                                    BotController.DisableBotPhysicsPublic(ph.gameObject);
                                    ph.DisablePlayerObjectWhenKilled();
                                }
                                catch { }
                                victimBot.Die(rootTf);
                            }
                            WriteExplosiveKillFeed(rootObj, ph, weaponName);
                        }
                        else
                        {
                            ph.RemoveHealth(damage);
                            if (rootTf != null) ph.SetKiller(rootTf);
                        }
                    }
                }
            }
            catch { }
        }

        // Suppress MeleeWeapon.HitServer NRE when the WEAPON belongs to a bot
        // Handle melee hits — skip if weapon belongs to bot, manually damage if target is bot
        public static bool MeleeHitServer_Prefix(MeleeWeapon __instance, PlayerHealth enemyHealth, Vector3 hitPosition, Vector3 hitNormal, string hitName)
        {
            if (enemyHealth == null) return false;
            if (__instance.rootObject != null && __instance.rootObject.GetComponent<BotController>() != null) return false;
            // Target is a bot — handle damage manually (original NREs on bot client data)
            var victimBot = enemyHealth.GetComponent<BotController>();
            if (victimBot != null)
            {
                try
                {
                    // Read actual damage from weapon
                    float damage = 1f;
                    bool secondAttack = false;
                    try
                    {
                        var secField = GetField(typeof(MeleeWeapon), "secondAttackPlaying");
                        if (secField != null) secondAttack = (bool)secField.GetValue(__instance);
                    }
                    catch { }

                    if (secondAttack)
                    {
                        var dmgField = GetField(typeof(MeleeWeapon), "secondAttackDamage");
                        if (dmgField != null) damage = (float)dmgField.GetValue(__instance);
                    }
                    else
                    {
                        var dmgField = GetField(typeof(MeleeWeapon), "baseAttackDamage");
                        if (dmgField != null) damage = (float)dmgField.GetValue(__instance);
                    }

                    // Headshot check
                    bool headshot = hitName == "Head_Col" || hitName == "Neck_1_Col";
                    if (headshot) damage *= __instance.headMultiplier;

                    // Blood VFX
                    try
                    {
                        if (__instance.bodyImpact != null)
                            UnityEngine.Object.Instantiate(__instance.bodyImpact, hitPosition, Quaternion.LookRotation(hitNormal));
                        if (__instance.bloodSplatter != null)
                            UnityEngine.Object.Instantiate(__instance.bloodSplatter, hitPosition, Quaternion.identity);
                    }
                    catch { }

                    // Hit sound
                    try
                    {
                        var localSoundMethod = GetMethod(typeof(MeleeWeapon), "LocalSound");
                        if (localSoundMethod != null)
                        {
                            localSoundMethod.Invoke(__instance, new object[] { 1 }); // body hit
                            if (headshot) localSoundMethod.Invoke(__instance, new object[] { 0 }); // head hit
                        }
                    }
                    catch { }

                    // Hit marker
                    try
                    {
                        var hitMarkerField = GetField(typeof(MeleeWeapon), "hitMarker");
                        if (hitMarkerField != null && Crosshair.Instance != null)
                        {
                            var hitMarkerPrefab = hitMarkerField.GetValue(__instance) as GameObject;
                            if (hitMarkerPrefab != null)
                            {
                                var marker = UnityEngine.Object.Instantiate(hitMarkerPrefab, Crosshair.Instance.transform.position, Quaternion.identity, PauseManager.Instance.transform);
                                marker.transform.DOPunchScale(headshot ? new Vector3(2.5f, 2.5f, 2.5f) : Vector3.one, 0.3f, 8, 2);
                                if (headshot) marker.GetComponent<UnityEngine.UI.Image>().color = Color.red;
                                UnityEngine.Object.Destroy(marker, 0.3f);
                            }
                        }
                    }
                    catch { }

                    enemyHealth.RemoveHealth(damage);
                    if (__instance.rootObject != null) enemyHealth.SetKiller(__instance.rootObject.transform);

                    if (enemyHealth.health <= 0f)
                    {
                        enemyHealth.ChangeKilledState(true);
                        enemyHealth.isShot = true;

                        // Kill sound
                        try
                        {
                            var localSoundMethod = typeof(MeleeWeapon).GetMethod("LocalSound",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (localSoundMethod != null) localSoundMethod.Invoke(__instance, new object[] { 2 });
                        }
                        catch { }

                        // Ragdoll
                        float ragdollForce = __instance.ragdollEjectForce;
                        try
                        {
                            var camField = GetField(typeof(MeleeWeapon), "cam");
                            Camera cam = camField?.GetValue(__instance) as Camera;
                            Vector3 ejectDir = cam != null ? cam.transform.forward : (__instance.rootObject.transform.forward);
                            enemyHealth.ExplodeServer(false, false, hitName, ejectDir, ragdollForce, hitPosition);
                        }
                        catch { }

                        // Kill feed
                        try
                        {
                            string weaponName = __instance.behaviour != null ? __instance.behaviour.weaponName : "Melee";
                            string killerTag = ClientInstance.Instance != null ? ClientInstance.Instance.PlayerNameTag : "unknown";
                            string action = headshot ? "beheaded" : "slain";
                            BotKillFeed.Write(enemyHealth, __instance.rootObject, killerTag, weaponName, action, true);
                        }
                        catch { }

                        // Kill shockwave (screen effect)
                        try { __instance.playerController.KillShockWave(); } catch { }

                        // Bot death
                        if (!victimBot.IsDead)
                            victimBot.Die(__instance.rootObject != null ? __instance.rootObject.transform : null);
                    }
                }
                catch { }
                return false;
            }
            return true;
        }

        // Suppress MeleeWeapon.BumpPlayerServer NRE for bots
        public static bool BumpPlayerServer_Prefix(MeleeWeapon __instance, PlayerHealth ph)
        {
            // Skip if weapon belongs to a bot (bot melee handled by BotController)
            if (__instance.rootObject != null && __instance.rootObject.GetComponent<BotController>() != null)
                return false;
            // Allow bumps/pushes ON bots (repulser, melee knockback)
            return true;
        }

        // Physics props can send this ServerRpc with null or bot-incomplete PlayerHealth.
        // Vanilla dereferences player-only components, which breaks FishNet packet parsing.
        public static bool PhysicsPropBumpPlayerServer_Prefix(Vector3 direction, float force, PlayerHealth ph)
        {
            if (ph == null || ph.gameObject == null) return false;

            var bot = ph.GetComponent<BotController>() ?? ph.GetComponentInParent<BotController>();
            if (bot != null)
            {
                if (!bot.IsDead)
                    bot.ApplyZoneImpulse(direction * Mathf.Clamp(force, 0f, 60f));
                return false;
            }

            // Human players should use the original path, but only when the expected
            // player movement component exists.
            return ph.GetComponent<FirstPersonController>() != null
                || ph.GetComponentInParent<FirstPersonController>() != null;
        }

        // Guard ItemBehaviour.OnCollisionEnter for bots.
        // For explosive items (grenades etc.) hitting a bot: block the collision damage entirely.
        // The fuse/HandleExplosion path handles bot deaths via Explosion_Postfix.
        // Letting the original run was destroying the grenade on impact and skipping the explosion.
        // For non-explosive weapons hitting a bot: let the original run (NRE suppressed by finalizer).
        public static bool ItemCollision_Prefix(ItemBehaviour __instance, Collision col)
        {
            if (col == null || col.gameObject == null) return false;
            if (col.gameObject.GetComponentInParent<BotController>() != null)
            {
                // Explosives: skip collision damage — explosion radius handles bot kills via Explosion_Postfix.
                // Letting the original run was destroying the grenade on impact before the fuse fired.
                var go = __instance.gameObject;
                if (go.GetComponent<PhysicsGrenade>() != null || go.GetComponent<HandGrenade>() != null
                    || go.GetComponent<HandGrenadeTwo>() != null || go.GetComponent<Obus>() != null
                    || go.GetComponent<Bubble>() != null)
                    return false;
                // Other items: let original run, finalizer suppresses any NRE
                try { return true; }
                catch { return false; }
            }
            return true;
        }

        // Finalizer for ItemBehaviour.OnCollisionEnter — suppress NREs from bot interactions
        public static Exception ItemCollision_Finalizer(Exception __exception)
        {
            return null; // Suppress any exception
        }

        // Set bot SteamIDs to local player's SteamID so avatar lookup doesn't crash
        public static void VictoryMenuUI_Prefix()
        {
            try
            {
                // Get local player's SteamID to use as fallback for bots
                ulong fallbackId = 0;
                if (ClientInstance.Instance != null)
                    fallbackId = ClientInstance.Instance.PlayerSteamID;

                if (BotManager.Instance == null) return;
                foreach (var bot in BotManager.Instance.LobbyBots)
                {
                    if (ClientInstance.playerInstances.TryGetValue(bot.PlayerId, out ClientInstance ci))
                    {
                        if (ci.PlayerSteamID == 0)
                            ci.PlayerSteamID = fallbackId;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"VictoryMenuUI prefix: {ex.Message}");
            }
        }

        // Suppress MatchPoitnsHUD crashes from bot team IDs exceeding array bounds
        // Intercept Claymore/ProximityMine SendKillLog — fix killer name when placed by bot
        public static bool ExplosiveSendKillLog_Prefix(MonoBehaviour __instance, PlayerHealth enemyHealth)
        {
            if (enemyHealth == null) return false;

            // Get rootObject
            GameObject rootObj = null;
            try
            {
                var field = GetField(__instance.GetType(), "_rootObject");
                if (field != null) rootObj = field.GetValue(__instance) as GameObject;
            }
            catch { }

            // If killer is a bot, write correct kill feed
            if (rootObj != null && rootObj.GetComponent<BotController>() != null)
            {
                try
                {
                    string weaponName = ResolveExplosiveWeaponName(__instance);
                    WriteExplosiveKillFeed(rootObj, enemyHealth, weaponName);
                }
                catch { }
                return false; // Skip original (would use host name)
            }

            // Bot victim of player-placed explosive — skip if bot (NREs on playerClient)
            if (enemyHealth.GetComponent<BotController>() != null)
            {
                try
                {
                    string weaponName = ResolveExplosiveWeaponName(__instance);
                    WriteExplosiveKillFeed(rootObj, enemyHealth, weaponName);
                }
                catch { }
                return false;
            }

            return true; // Human killer, human victim — let original run
        }

        public static bool RoundManagerNextRoundCall_Prefix(RoundManager __instance, int playerId, bool won, int winningTeamId)
        {
            try
            {
                if (__instance == null || ScoreManager.Instance == null || PauseManager.Instance == null
                    || ClientInstance.playerInstances == null || ClientInstance.playerInstances.Count == 0)
                    return true;

                if (__instance.InterfaceSetupCoroutine != null)
                {
                    try { __instance.StopCoroutine(__instance.InterfaceSetupCoroutine); } catch { }
                    __instance.InterfaceSetupCoroutine = null;
                }

                // Ensure bot client entries have a non-empty display name/tag used by round/take text.
                if (BotManager.Instance != null)
                {
                    foreach (var bot in BotManager.Instance.LobbyBots)
                    {
                        if (!ClientInstance.playerInstances.TryGetValue(bot.PlayerId, out var botCi) || botCi == null) continue;
                        if (string.IsNullOrWhiteSpace(botCi.PlayerName)
                            || botCi.PlayerName.StartsWith("Player ", StringComparison.OrdinalIgnoreCase))
                            botCi.PlayerName = bot.Name;
                        try
                        {
                            var tagField = GetField(typeof(ClientInstance), "PlayerNameTag");
                            if (tagField != null)
                            {
                                string cur = tagField.GetValue(botCi) as string;
                                if (string.IsNullOrWhiteSpace(cur)
                                    || cur.StartsWith("Player ", StringComparison.OrdinalIgnoreCase))
                                    tagField.SetValue(botCi, bot.Name);
                            }
                        }
                        catch { }
                    }
                }

                var activeIds = new List<int>();
                foreach (var kv in ClientInstance.playerInstances)
                {
                    if (kv.Value != null) activeIds.Add(kv.Key);
                }
                // Ensure active bots always appear on round-end scoreboard even if their
                // ClientInstance entry is temporarily null/missing at round transition time.
                try
                {
                    if (BotManager.Instance != null)
                    {
                        foreach (var bot in BotManager.Instance.LobbyBots)
                        {
                            if (bot == null || bot.PlayerId < 0) continue;
                            if (!activeIds.Contains(bot.PlayerId)) activeIds.Add(bot.PlayerId);
                        }
                    }

                    var bots = BotManager.ActiveBots;
                    if (bots != null)
                    {
                        for (int i = 0; i < bots.Count; i++)
                        {
                            var b = bots[i];
                            if (b == null) continue;
                            if (b.PlayerId < 0) continue;
                            if (!activeIds.Contains(b.PlayerId)) activeIds.Add(b.PlayerId);
                        }
                    }
                }
                catch { }
                if (activeIds.Count == 0) return true;
                activeIds.Sort();

                int maxPid = 0;
                foreach (var kv in ClientInstance.playerInstances)
                    if (kv.Key > maxPid) maxPid = kv.Key;
                for (int i = 0; i < activeIds.Count; i++)
                    if (activeIds[i] > maxPid) maxPid = activeIds[i];
                if (maxPid < 0) return true;

                int len = maxPid + 1;
                __instance.names = new string[len];
                __instance.scores = new int[len];
                __instance.players = new ClientInstance[len];

                foreach (var kv in ClientInstance.playerInstances)
                {
                    int pid = kv.Key;
                    var ci = kv.Value;
                    if (pid < 0 || pid >= len || ci == null) continue;
                    __instance.players[pid] = ci;
                    __instance.names[pid] = ResolveScoreboardName(pid, ci);

                    if (ScoreManager.Instance != null)
                    {
                        int teamId = ScoreManager.Instance.GetTeamId(pid);
                        if (teamId >= 0)
                            __instance.scores[pid] = ScoreManager.Instance.GetPoints(teamId);
                    }
                }

                // Fill any missing rows (usually bots) from active IDs so names are never blank.
                for (int i = 0; i < activeIds.Count; i++)
                {
                    int pid = activeIds[i];
                    if (pid < 0 || pid >= len) continue;
                    if (string.IsNullOrWhiteSpace(__instance.names[pid])
                        || __instance.names[pid].StartsWith("Player ", StringComparison.OrdinalIgnoreCase))
                    {
                        ClientInstance ci = null;
                        try { ClientInstance.playerInstances.TryGetValue(pid, out ci); } catch { }
                        __instance.names[pid] = ResolveScoreboardName(pid, ci);
                    }
                    if (ScoreManager.Instance != null)
                    {
                        int teamId = ScoreManager.Instance.GetTeamId(pid);
                        if (teamId >= 0)
                            __instance.scores[pid] = ScoreManager.Instance.GetPoints(teamId);
                    }
                }

                ResetRoundUiCaches();
                if (won) Settings.Instance.IncreaseRoundsWon();
                else Settings.Instance.IncreaseRoundsLost();

                __instance.InterfaceSetupCoroutine = ExtendedRoundInterfaceSetup(__instance, playerId, won, winningTeamId, activeIds);
                __instance.StartCoroutine(__instance.InterfaceSetupCoroutine);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"RoundManager.NextRoundCall prefix: {ex.Message}");
                return true;
            }
        }

        private static IEnumerator ExtendedRoundInterfaceSetup(RoundManager rm, int playerId, bool won, int winningTeamId, List<int> activeIds)
        {
            PauseManager.Instance.onEndRoundScreen = true;
            yield return null;

            foreach (var obj in _roundEndExtraObjects)
            {
                if (obj != null) UnityEngine.Object.Destroy(obj);
            }
            _roundEndExtraObjects.Clear();

            var container = GetField(typeof(RoundManager), "container")?.GetValue(rm) as GameObject;
            var background = GetField(typeof(RoundManager), "background")?.GetValue(rm) as GameObject;
            var winText = GetField(typeof(RoundManager), "winText")?.GetValue(rm) as TextMeshProUGUI;
            var selfPlusOne = GetField(typeof(RoundManager), "selfPlusOne")?.GetValue(rm) as GameObject;
            var enemyPlusOne = GetField(typeof(RoundManager), "enemyPlusOne")?.GetValue(rm) as GameObject[];
            float tweenTime = 0.75f;
            try { tweenTime = (float)GetField(typeof(RoundManager), "tweenTime")?.GetValue(rm); } catch { }

            if (selfPlusOne != null) selfPlusOne.transform.localScale = Vector3.zero;
            if (enemyPlusOne != null)
            {
                for (int i = 0; i < enemyPlusOne.Length; i++)
                    if (enemyPlusOne[i] != null) enemyPlusOne[i].transform.localScale = Vector3.zero;
            }

            var winClip = GetField(typeof(RoundManager), won ? "winClip" : "loseClip")?.GetValue(rm) as AudioClip;
            if (winClip != null) SoundManager.Instance.PlaySound(winClip);

            if (container != null) container.transform.localScale = Vector3.one;
            if (background != null) background.transform.localScale = Vector3.one;

            if (winText != null)
            {
                Color winColor = Color.white, loseColor = Color.white;
                try { winColor = (Color)GetField(typeof(RoundManager), "winColor")?.GetValue(rm); } catch { }
                try { loseColor = (Color)GetField(typeof(RoundManager), "loseColor")?.GetValue(rm); } catch { }
                winText.color = won ? winColor : loseColor;
                winText.text = won ? "victory" : "defeat";
            }

            var imgThree = GetField(typeof(RoundManager), "nextRoundImageThree")?.GetValue(rm) as Transform;
            var imgFour = GetField(typeof(RoundManager), "nextRoundImageFour")?.GetValue(rm) as Transform;
            var imgFive = GetField(typeof(RoundManager), "nextRoundImageFive")?.GetValue(rm) as Transform;
            var imgSix = GetField(typeof(RoundManager), "nextRoundImageSix")?.GetValue(rm) as Transform;
            if (imgThree != null) imgThree.localScale = Vector3.zero;
            if (imgFour != null) imgFour.localScale = Vector3.zero;
            if (imgFive != null) imgFive.localScale = Vector3.zero;
            if (imgSix != null) imgSix.localScale = Vector3.zero;

            var imgOne = GetField(typeof(RoundManager), "nextRoundImageOne")?.GetValue(rm) as Transform;
            var imgTwo = GetField(typeof(RoundManager), "nextRoundImageTwo")?.GetValue(rm) as Transform;
            var imgOneActive = GetField(typeof(RoundManager), "nextRoundImageOneActivePos")?.GetValue(rm) as Transform;
            var imgTwoActive = GetField(typeof(RoundManager), "nextRoundImageTwoActivePos")?.GetValue(rm) as Transform;
            var imgOneRest = GetField(typeof(RoundManager), "nextRoundImageOneRestPos")?.GetValue(rm) as Transform;
            var imgTwoRest = GetField(typeof(RoundManager), "nextRoundImageTwoRestPos")?.GetValue(rm) as Transform;

            var swoosh = GetField(typeof(RoundManager), "swooshClip")?.GetValue(rm) as AudioClip[];
            if (swoosh != null && swoosh.Length > 0 && swoosh[0] != null) SoundManager.Instance.PlaySound(swoosh[0]);
            if (imgOne != null && imgOneActive != null) imgOne.DOMove(imgOneActive.position, tweenTime).SetEase(Ease.OutSine);
            if (imgTwo != null && imgTwoActive != null) imgTwo.DOMove(imgTwoActive.position, tweenTime).SetEase(Ease.OutSine);

            int localPlayerId = playerId;
            if (LobbyController.Instance?.LocalPlayerController != null)
                localPlayerId = LobbyController.Instance.LocalPlayerController.PlayerId;

            int halfCount = (activeIds.Count + 1) / 2;
            var scoreTextMap = new Dictionary<int, TextMeshProUGUI>();
            try
            {
                Plugin.Log.LogInfo($"[BOT] RoundEnd UI entries={activeIds.Count} ids=[{string.Join(",", activeIds)}]");
            }
            catch { }

            if (container != null)
            {
                for (int idx = 0; idx < activeIds.Count; idx++)
                {
                    int pid = activeIds[idx];
                    if (pid < 0 || pid >= rm.names.Length || pid >= rm.scores.Length) continue;

                    int col = idx < halfCount ? 0 : 1;
                    int row = idx < halfCount ? idx : idx - halfCount;
                    float xBase = col == 0 ? -250f : 250f;
                    float yBase = 120f - row * 70f;

                    var nameGo = new GameObject($"RoundEndName_{pid}");
                    nameGo.transform.SetParent(container.transform, false);
                    var nameText = nameGo.AddComponent<TextMeshProUGUI>();
                    bool isLocal = pid == localPlayerId;
                    string displayName = ResolveScoreboardName(pid, rm.players[pid]);
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = rm.names[pid];
                    if (string.IsNullOrWhiteSpace(displayName)) displayName = $"Player {pid}";
                    nameText.text = isLocal ? displayName.ToLower() : displayName;
                    nameText.fontSize = isLocal ? 34f : 30f;
                    nameText.fontStyle = isLocal ? FontStyles.Bold : FontStyles.Normal;
                    nameText.alignment = TextAlignmentOptions.Center;
                    nameText.color = Color.white;
                    var nameRt = nameText.GetComponent<RectTransform>();
                    nameRt.anchorMin = nameRt.anchorMax = nameRt.pivot = new Vector2(0.5f, 0.5f);
                    nameRt.anchoredPosition = new Vector2(xBase, yBase);
                    nameRt.sizeDelta = new Vector2(400f, 40f);
                    _roundEndExtraObjects.Add(nameGo);

                    var scoreGo = new GameObject($"RoundEndScore_{pid}");
                    scoreGo.transform.SetParent(container.transform, false);
                    var scoreText = scoreGo.AddComponent<TextMeshProUGUI>();
                    bool isWinTeam = ScoreManager.Instance.GetTeamId(pid) == winningTeamId;
                    int preScore = isWinTeam ? Mathf.Max(0, rm.scores[pid] - 1) : rm.scores[pid];
                    scoreText.text = preScore.ToString();
                    scoreText.fontSize = 26f;
                    scoreText.alignment = TextAlignmentOptions.Center;
                    scoreText.color = new Color(0f, 1f, 1f);
                    var scoreRt = scoreText.GetComponent<RectTransform>();
                    scoreRt.anchorMin = scoreRt.anchorMax = scoreRt.pivot = new Vector2(0.5f, 0.5f);
                    scoreRt.anchoredPosition = new Vector2(xBase, yBase - 32f);
                    scoreRt.sizeDelta = new Vector2(100f, 30f);
                    scoreTextMap[pid] = scoreText;
                    _roundEndExtraObjects.Add(scoreGo);
                }
            }

            yield return new WaitForSeconds(0.75f);
            var plusOneClip = GetField(typeof(RoundManager), "plusOneClip")?.GetValue(rm) as AudioClip;
            if (plusOneClip != null) SoundManager.Instance.PlaySound(plusOneClip);

            yield return new WaitForSeconds(1.0f);
            foreach (var kv in scoreTextMap)
            {
                int pid = kv.Key;
                if (pid >= 0 && pid < rm.scores.Length && kv.Value != null)
                    kv.Value.text = rm.scores[pid].ToString();
            }

            yield return new WaitForSeconds(1.25f);
            if (swoosh != null && swoosh.Length > 1 && swoosh[1] != null) SoundManager.Instance.PlaySound(swoosh[1]);
            if (imgOne != null && imgOneRest != null) imgOne.DOMove(imgOneRest.position, tweenTime).SetEase(Ease.OutSine);
            if (imgTwo != null && imgTwoRest != null) imgTwo.DOMove(imgTwoRest.position, tweenTime).SetEase(Ease.OutSine);

            yield return new WaitForSeconds(1f);
            if (container != null) container.transform.localScale = Vector3.zero;
            if (background != null) background.transform.localScale = Vector3.zero;

            foreach (var obj in _roundEndExtraObjects)
                if (obj != null) UnityEngine.Object.Destroy(obj);
            _roundEndExtraObjects.Clear();

            try { SceneMotor.Instance.ShowLoadingScreen(); } catch { }
            PauseManager.Instance.onEndRoundScreen = false;
            rm.InterfaceSetupCoroutine = null;
            try { SceneMotor.Instance.ChangeNetworkScene(); } catch { }
        }

        private static void UpdateTabScoresBetweenRounds()
        {
            try
            {
                if (ScoreManager.Instance == null) return;
                var lc = LobbyController.Instance;
                if (lc == null) return;

                var tabScreenField = GetField(typeof(LobbyController), "tabScreen");
                var tabTransform = tabScreenField?.GetValue(lc) as Transform;
                if (tabTransform == null) return;

                var tabItems = tabTransform.GetComponentsInChildren<PlayerListItem>(true);
                if (tabItems == null || tabItems.Length == 0) return;

                foreach (var item in tabItems)
                {
                    if (item == null) continue;
                    int id = item.GetInstanceID();
                    if (!_tabScoreSetupItems.Contains(id))
                    {
                        _tabScoreSetupItems.Add(id);

                        if (item.PlayerReadyText != null)
                            item.PlayerReadyText.gameObject.SetActive(false);

                        var go = new GameObject("BotRoundScore");
                        go.transform.SetParent(item.transform, false);
                        var st = go.AddComponent<TextMeshProUGUI>();
                        st.fontSize = 30f;
                        st.fontStyle = FontStyles.Bold;
                        st.alignment = TextAlignmentOptions.Center;
                        st.color = new Color(0f, 1f, 1f);
                        var rt = st.GetComponent<RectTransform>();
                        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                        rt.anchoredPosition = new Vector2(0f, 34f);
                        rt.sizeDelta = new Vector2(150f, 40f);
                        _tabScoreTexts[id] = st;
                    }

                    if (_tabScoreTexts.TryGetValue(id, out var scoreText) && scoreText != null)
                    {
                        int teamId = ScoreManager.Instance.GetTeamId(item.PlayerIdNumber);
                        if (teamId >= 0)
                            scoreText.text = ScoreManager.Instance.GetPoints(teamId).ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"UpdateTabScoresBetweenRounds: {ex.Message}");
            }
        }

        public static void ResetRoundUiCaches()
        {
            foreach (var kv in _tabScoreTexts)
            {
                try
                {
                    if (kv.Value != null && kv.Value.gameObject != null)
                        UnityEngine.Object.Destroy(kv.Value.gameObject);
                }
                catch { }
            }
            _tabScoreSetupItems.Clear();
            _tabScoreTexts.Clear();
        }

        private static string GetClientDisplayName(ClientInstance ci)
        {
            if (ci == null) return "";
            try
            {
                var tagField = GetField(typeof(ClientInstance), "PlayerNameTag");
                if (tagField != null)
                {
                    string tag = tagField.GetValue(ci) as string;
                    if (!string.IsNullOrWhiteSpace(tag)) return tag;
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(ci.PlayerName)) return ci.PlayerName;
            return $"Player {ci.PlayerId}";
        }

        private static string ResolveScoreboardName(int playerId, ClientInstance ci)
        {
            try
            {
                string clientName = GetClientDisplayName(ci);
                if (!string.IsNullOrWhiteSpace(clientName)
                    && !clientName.StartsWith("Player ", StringComparison.OrdinalIgnoreCase))
                    return clientName;
            }
            catch { }

            try
            {
                if (BotManager.Instance != null)
                {
                    foreach (var bot in BotManager.Instance.LobbyBots)
                    {
                        if (bot == null) continue;
                        if (bot.PlayerId == playerId && !string.IsNullOrWhiteSpace(bot.Name))
                            return bot.Name;
                    }
                }
            }
            catch { }

            try
            {
                var bots = BotManager.ActiveBots;
                if (bots != null)
                {
                    for (int i = 0; i < bots.Count; i++)
                    {
                        var b = bots[i];
                        if (b == null) continue;
                        if (b.PlayerId == playerId && !string.IsNullOrWhiteSpace(b.BotName))
                            return b.BotName;
                    }
                }
            }
            catch { }

            try
            {
                string clientName = GetClientDisplayName(ci);
                if (!string.IsNullOrWhiteSpace(clientName))
                    return clientName;
            }
            catch { }

            return $"Player {playerId}";
        }

        public static bool MatchPointsHUD_UpdateVisuals_Prefix(MatchPoitnsHUD __instance, int winnerTeamId, Dictionary<int, int> roundScores)
        {
            try
            {
                if (__instance == null || ScoreManager.Instance == null) return false;

                var secondaryPointObjects = GetField(typeof(MatchPoitnsHUD), "secondaryPointObjects")?.GetValue(__instance) as MeshRenderer[];
                var pointsTextsObj = GetField(typeof(MatchPoitnsHUD), "pointsTexts")?.GetValue(__instance) as Array;
                var primaryPointMesh = GetField(typeof(MatchPoitnsHUD), "primaryPointMesh")?.GetValue(__instance) as MeshRenderer;
                var activeMaterials = GetField(typeof(MatchPoitnsHUD), "activeMaterials")?.GetValue(__instance) as Material[];
                var inactiveMaterial = GetField(typeof(MatchPoitnsHUD), "inactiveMaterial")?.GetValue(__instance) as Material;

                if (secondaryPointObjects == null || pointsTextsObj == null || primaryPointMesh == null
                    || activeMaterials == null || inactiveMaterial == null)
                    return false;

                int activeTeamCount = 0;
                foreach (var kv in ScoreManager.Instance.TeamIdToPlayerIds)
                {
                    bool hasConnected = false;
                    foreach (int pid in kv.Value)
                    {
                        if (ClientInstance.playerInstances.ContainsKey(pid))
                        {
                            hasConnected = true;
                            break;
                        }
                    }
                    if (hasConnected) activeTeamCount++;
                }

                int neededSecondary = Math.Max(0, activeTeamCount - 2);
                int activateSecondary = Math.Min(neededSecondary, secondaryPointObjects.Length);
                for (int i = 0; i < secondaryPointObjects.Length; i++)
                {
                    if (secondaryPointObjects[i] != null)
                        secondaryPointObjects[i].gameObject.SetActive(i < activateSecondary);
                }

                for (int i = 0; i < pointsTextsObj.Length; i++)
                    SetPointsText(pointsTextsObj, i, "");

                Material[] primaryMats = primaryPointMesh.materials;
                int slot = 0;
                foreach (var kv in ScoreManager.Instance.TeamIdToPlayerIds)
                {
                    int teamId = kv.Key;
                    bool hasConnected = false;
                    foreach (int pid in kv.Value)
                    {
                        if (ClientInstance.playerInstances.ContainsKey(pid))
                        {
                            hasConnected = true;
                            break;
                        }
                    }
                    if (!hasConnected) continue;

                    if (slot >= 2 + secondaryPointObjects.Length) break;
                    int roundScore = 0;
                    if (roundScores != null && roundScores.TryGetValue(teamId, out int rs))
                        roundScore = rs;
                    UpdateMatchPointSlotSafe(slot, teamId == winnerTeamId, roundScore, primaryMats,
                        secondaryPointObjects, pointsTextsObj, activeMaterials, inactiveMaterial);
                    slot++;
                }
                primaryPointMesh.materials = primaryMats;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"MatchPoitnsHUD prefix swallowed: {ex.Message}");
            }
            return false;
        }

        private static void SetPointsText(Array pointsTextsObj, int index, string value)
        {
            if (pointsTextsObj == null || index < 0 || index >= pointsTextsObj.Length) return;
            object textObj = pointsTextsObj.GetValue(index);
            if (textObj == null) return;
            try
            {
                var prop = textObj.GetType().GetProperty("text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite) { prop.SetValue(textObj, value, null); return; }

                var field = textObj.GetType().GetField("text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) field.SetValue(textObj, value);
            }
            catch { }
        }

        private static void UpdateMatchPointSlotSafe(int slot, bool isWinner, int roundScore,
            Material[] primaryMats, MeshRenderer[] secondaryPointObjects, Array pointsTextsObj,
            Material[] activeMaterials, Material inactiveMaterial)
        {
            Material mat = (roundScore > 0 && slot < activeMaterials.Length)
                ? activeMaterials[slot]
                : inactiveMaterial;

            if (slot < 2)
            {
                int idx = slot == 0 ? 1 : 3;
                if (primaryMats != null && idx < primaryMats.Length) primaryMats[idx] = mat;
            }
            else
            {
                int secIdx = slot - 2;
                if (secIdx < secondaryPointObjects.Length && secondaryPointObjects[secIdx] != null)
                {
                    var mats = secondaryPointObjects[secIdx].materials;
                    if (mats != null && mats.Length > 1)
                    {
                        mats[1] = mat;
                        secondaryPointObjects[secIdx].materials = mats;
                    }
                }
            }

            if ((roundScore == 2 && !isWinner) || roundScore > 2)
                SetPointsText(pointsTextsObj, slot, roundScore.ToString());

            if (isWinner && primaryMats != null && primaryMats.Length > 2)
                primaryMats[2] = mat;
        }

        public static Exception WaitForDraw_Finalizer(Exception __exception) => null;

        // Suppress NREs in HandleExplosion (game's suicide check NREs on bot data)
        public static Exception Explosion_Finalizer(Exception __exception) => null;
        public static Exception KillCamUpdate_Finalizer(Exception __exception) => null;

        public static Exception MatchPointsHUD_Finalizer(Exception __exception)
        {
            return null; // Suppress IndexOutOfRangeException so WaitForDraw coroutine continues
        }

        /// <summary>
        /// Drive debug visualizer OnGUI from FirstPersonController.OnGUI.
        /// BepInEx MonoBehaviour OnGUI doesn't fire reliably in STRAFTAT.
        /// </summary>
    }
}
