using System.Collections.Generic;
using MyceliumNetworking;
using UnityEngine;

namespace StraftatBots
{
    /// <summary>
    /// Syncs bot damage/kills to non-host clients via Mycelium (Steam networking).
    /// Host already works via RemoveHealth/ChangeKilledState RPCs.
    /// This sends the same info to non-host clients so they see damage and deaths.
    /// </summary>
    public class BotDamageSync : MonoBehaviour
    {
        public const uint MOD_ID = 83927462;
        private static BotDamageSync _instance;
        private static System.Reflection.FieldInfo _dlProjectileField;


        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            MyceliumNetwork.RegisterNetworkObject(this, MOD_ID);
        }

        void OnDestroy()
        {
            if (_instance == this)
            {
                try { MyceliumNetwork.DeregisterNetworkObject(this, MOD_ID); } catch { }
                _instance = null;
            }
        }

        /// <summary>
        /// Call from BotController after a human is killed.
        /// Sends ragdoll + kill feed to non-host clients.
        /// </summary>
        public static void SyncKill(int victimPlayerId, string killerName, string weaponName, bool headshot,
            Vector3 direction, float ragdollForce, Vector3 hitPoint, string boneName)
        {
            if (_instance == null) return;
            try
            {
                MyceliumNetwork.RPC(MOD_ID, nameof(RPC_PlayerKilled), ReliableType.Reliable,
                    victimPlayerId, killerName, weaponName, headshot,
                    direction.x, direction.y, direction.z,
                    ragdollForce, hitPoint.x, hitPoint.y, hitPoint.z, boneName);
            }
            catch { }
        }

        /// <summary>
        /// Call from BotController after shooting effects.
        /// Sends muzzle flash + sound to non-host clients.
        /// </summary>
        public static void SyncShootEffect(Vector3 muzzlePos, Vector3 forward, string weaponType)
        {
            if (_instance == null) return;
            try
            {
                MyceliumNetwork.RPC(MOD_ID, nameof(RPC_ShootEffect), ReliableType.Reliable,
                    muzzlePos.x, muzzlePos.y, muzzlePos.z,
                    forward.x, forward.y, forward.z, weaponType);
            }
            catch { }
        }

        /// <summary>
        /// Sync bullet trail to non-host clients.
        /// </summary>
        public static void SyncBulletTrail(Vector3 start, Vector3 end)
        {
            if (_instance == null) return;
            try
            {
                MyceliumNetwork.RPC(MOD_ID, nameof(RPC_BulletTrail), ReliableType.Reliable,
                    start.x, start.y, start.z, end.x, end.y, end.z);
            }
            catch { }
        }

        /// <summary>
        /// Sync projectile spawn (visual trail) to non-host clients.
        /// </summary>
        public static void SyncProjectile(Vector3 position, Vector3 direction, float speed, string weaponName)
        {
            if (_instance == null) return;
            try
            {
                MyceliumNetwork.RPC(MOD_ID, nameof(RPC_Projectile), ReliableType.Reliable,
                    position.x, position.y, position.z,
                    direction.x, direction.y, direction.z,
                    speed, weaponName);
            }
            catch { }
        }

        /// <summary>
        /// Sync projectile explosion VFX to non-host clients.
        /// </summary>
        public static void SyncExplosion(Vector3 position, float radius)
        {
            if (_instance == null) return;
            try
            {
                MyceliumNetwork.RPC(MOD_ID, nameof(RPC_Explosion), ReliableType.Reliable,
                    position.x, position.y, position.z, radius);
            }
            catch { }
        }

        /// <summary>
        /// Sync bot skin to non-host clients.
        /// </summary>
        public static void SyncSkin(int botNetId, int suitIndex, int hatIndex = -1, int cigIndex = 0)
        {
            if (_instance == null) return;
            try
            {
                MyceliumNetwork.RPC(MOD_ID, nameof(RPC_SyncSkin), ReliableType.Reliable,
                    botNetId, suitIndex, hatIndex, cigIndex);
            }
            catch { }
        }

        // ==================== NAVGRAPH SYNC ====================

        /// <summary>
        /// Host broadcasts NavGraph data to all clients on round end.
        /// Splits into chunks if needed (Mycelium has message size limits).
        /// </summary>
        public static void SyncNavGraph()
        {
            if (_instance == null || NavGraph.Instance == null) return;
            if (!FishNet.InstanceFinder.IsServer) return;

            try
            {
                byte[] data = NavGraph.Instance.SerializeToBytes();
                if (data == null || data.Length == 0) return;

                // Convert to base64 string for Mycelium (can't send raw byte arrays)
                string b64 = System.Convert.ToBase64String(data);

                // Split into chunks of 8KB (Mycelium message limit safety)
                const int CHUNK_SIZE = 8000;
                int totalChunks = (b64.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;
                string mapName = NavGraph.Instance.CurrentMap ?? "";

                for (int i = 0; i < totalChunks; i++)
                {
                    int start = i * CHUNK_SIZE;
                    int len = Mathf.Min(CHUNK_SIZE, b64.Length - start);
                    string chunk = b64.Substring(start, len);
                    MyceliumNetwork.RPC(MOD_ID, nameof(RPC_NavGraphChunk), ReliableType.Reliable,
                        mapName, i, totalChunks, chunk);
                }

                Plugin.Log.LogInfo($"[NavGraph] Synced {data.Length} bytes ({totalChunks} chunks) to all clients for {mapName}");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[NavGraph] Sync failed: {ex.Message}");
            }
        }

        // Accumulate chunks on client side
        private static Dictionary<string, string[]> _pendingChunks = new Dictionary<string, string[]>();

        [CustomRPC]
        public void RPC_NavGraphChunk(string mapName, int chunkIndex, int totalChunks, string chunkData)
        {
            if (FishNet.InstanceFinder.IsServer) return; // Host already has the data

            try
            {
                // Validate bounds
                if (totalChunks <= 0 || totalChunks > 1000 || chunkIndex < 0 || chunkIndex >= totalChunks)
                    return;

                if (!_pendingChunks.ContainsKey(mapName) || _pendingChunks[mapName].Length != totalChunks)
                    _pendingChunks[mapName] = new string[totalChunks];

                _pendingChunks[mapName][chunkIndex] = chunkData;

                // Check if all chunks received
                bool complete = true;
                foreach (var c in _pendingChunks[mapName])
                    if (c == null) { complete = false; break; }

                if (complete)
                {
                    string b64 = string.Join("", _pendingChunks[mapName]);
                    byte[] data = System.Convert.FromBase64String(b64);
                    _pendingChunks.Remove(mapName);

                    // Merge into local graph and save
                    NavGraph.Init();
                    if (NavGraph.Instance.CurrentMap != mapName)
                        NavGraph.Instance.LoadForMap(mapName);
                    NavGraph.Instance.MergeFromBytes(data);
                    NavGraph.Instance.Save();

                    Plugin.Log.LogInfo($"[NavGraph] Received and merged {data.Length} bytes for {mapName}");
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[NavGraph] RPC_NavGraphChunk error: {ex.Message}");
            }
        }

        // ==================== MYCELIUM RPCs ====================

        [CustomRPC]
        public void RPC_PlayerKilled(int victimNetId, string killerName, string weaponName, bool headshot,
            float dx, float dy, float dz, float force, float hx, float hy, float hz, string boneName)
        {
            // Skip on server — host already handled it
            if (FishNet.InstanceFinder.IsServer) return;

            // Find victim by NetworkObject ID (works on non-host where PlayerId lookup fails)
            var ph = FindPlayerHealthByNetId(victimNetId);
            if (ph == null || ph.isKilled) return;

            // Set death state on this client
            ph.isShot = true;
            ph.health = -8f;
            ph.isKilled = true;

            // Ragdoll
            try
            {
                ph.Explode(false, true, boneName,
                    new Vector3(dx, dy, dz), force, new Vector3(hx, hy, hz));
            }
            catch { }

            // Hide bot model — BotController doesn't exist on non-host so Die()/HideGraphicsDelayed never runs
            try
            {
                // Disable all animators (stops walking-in-place)
                foreach (var anim in ph.GetComponentsInChildren<Animator>(true))
                    anim.enabled = false;
                foreach (var netAnim in ph.GetComponentsInChildren<FishNet.Component.Animating.NetworkAnimator>(true))
                    netAnim.enabled = false;

                // Hide graphics
                if (ph.graphics != null)
                    ph.graphics.SetActive(false);

                // Disable renderers
                foreach (var r in ph.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    r.enabled = false;
                foreach (var r in ph.GetComponentsInChildren<MeshRenderer>(true))
                    r.enabled = false;

                // Disable colliders
                foreach (var col in ph.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
                var cc = ph.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
            }
            catch { }

            // Kill feed
            try
            {
                BotKillFeed.Write(ph, null, killerName, weaponName, headshot ? "headshot" : "killed", true);
            }
            catch { }
        }

        // Cache weapon prefabs on first use (avoid FindObjectsOfType every shot)
        private static System.Collections.Generic.Dictionary<string, Weapon> _weaponCache;
        private static LineRenderer _trailPrefabCache;
        private static bool _trailCached;

        private static Weapon FindWeaponByName(string name)
        {
            if (_weaponCache == null) _weaponCache = new System.Collections.Generic.Dictionary<string, Weapon>();
            if (_weaponCache.TryGetValue(name, out Weapon cached) && cached != null) return cached;
            foreach (var w in Object.FindObjectsOfType<Weapon>(true))
            {
                if (w.gameObject.name == name) { _weaponCache[name] = w; return w; }
            }
            return null;
        }

        private static LineRenderer GetTrailPrefab()
        {
            if (_trailCached) return _trailPrefabCache;
            _trailCached = true;
            foreach (var w in Object.FindObjectsOfType<Weapon>(true))
            {
                if (w.bulletTrailLocal != null) { _trailPrefabCache = w.bulletTrailLocal; return _trailPrefabCache; }
            }
            return null;
        }

        [CustomRPC]
        public void RPC_ShootEffect(float mx, float my, float mz, float fx, float fy, float fz, string weaponName)
        {
            if (FishNet.InstanceFinder.IsServer) return;

            Vector3 pos = new Vector3(mx, my, mz);
            Vector3 fwd = new Vector3(fx, fy, fz);

            try
            {
                var w = FindWeaponByName(weaponName);
                if (w != null)
                {
                    if (w.fireClip != null)
                        AudioSource.PlayClipAtPoint(w.fireClip, pos);
                    if (w.muzzleFlash != null)
                    {
                        var flash = Instantiate(w.muzzleFlash, pos, Quaternion.LookRotation(fwd));
                        foreach (Transform c in flash.GetComponentsInChildren<Transform>(true))
                            c.gameObject.layer = 0;
                        foreach (var fx2 in flash.GetComponentsInChildren<ParticleSystem>())
                            fx2.Play();
                        Destroy(flash, 2f);
                    }
                }
            }
            catch { }
        }

        [CustomRPC]
        public void RPC_BulletTrail(float sx, float sy, float sz, float ex, float ey, float ez)
        {
            if (FishNet.InstanceFinder.IsServer) return;

            Vector3 start = new Vector3(sx, sy, sz);
            Vector3 end = new Vector3(ex, ey, ez);

            try
            {
                var trailPrefab = GetTrailPrefab();
                if (trailPrefab != null)
                {
                    var trail = Instantiate(trailPrefab.gameObject, start, Quaternion.identity);
                    var lr = trail.GetComponent<LineRenderer>();
                    if (lr != null) { lr.SetPosition(0, start); lr.SetPosition(1, end); }
                    Destroy(trail, 0.4f);
                }
            }
            catch { }
        }

        [CustomRPC]
        public void RPC_SyncSkin(int botNetId, int suitIndex, int hatIndex, int cigIndex)
        {
            if (FishNet.InstanceFinder.IsServer) return;

            try
            {
                foreach (var nob in Object.FindObjectsOfType<FishNet.Object.NetworkObject>())
                {
                    if ((int)nob.ObjectId != botNetId) continue;

                    // Use the same direct instantiation as the host
                    var tempData = new BotData
                    {
                        SuitIndex = suitIndex,
                        HatIndex = hatIndex,
                        CigIndex = cigIndex
                    };
                    BotManager.ApplyAllCosmetics(nob.gameObject, tempData);
                    break;
                }
            }
            catch { }
        }

        [CustomRPC]
        public void RPC_Projectile(float px, float py, float pz, float dx, float dy, float dz, float speed, string weaponName)
        {
            if (FishNet.InstanceFinder.IsServer) return;

            Vector3 pos = new Vector3(px, py, pz);
            Vector3 dir = new Vector3(dx, dy, dz).normalized;

            try
            {
                // Find the actual projectile prefab from the weapon in the scene
                GameObject prefab = null;
                var w = FindWeaponByName(weaponName);
                if (w != null && w is DualLauncher)
                {
                    // Read _projectile from DualLauncher
                    if (_dlProjectileField == null)
                        _dlProjectileField = typeof(DualLauncher).GetField("_projectile",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    var projField = _dlProjectileField;
                    if (projField != null)
                    {
                        var proj = projField.GetValue(w) as Component;
                        if (proj != null) prefab = proj.gameObject;
                    }
                }

                if (prefab != null)
                {
                    // Instantiate the real rocket prefab — visual only (isOwner=false, no damage)
                    var projObj = Instantiate(prefab, pos, Quaternion.LookRotation(dir));

                    // Disable NetworkObject if present (don't register with FishNet)
                    var nob = projObj.GetComponent<FishNet.Object.NetworkObject>();
                    if (nob != null) nob.enabled = false;

                    // Initialize the PredictedProjectile for movement only
                    var pp = projObj.GetComponent<PredictedProjectile>();
                    if (pp != null)
                    {
                        pp.isOwner = false; // No damage on non-host
                        pp.Initialize(dir, speed, 0f, null, null);
                    }

                    Destroy(projObj, 10f);
                }
            }
            catch { }
        }

        [CustomRPC]
        public void RPC_Explosion(float px, float py, float pz, float radius)
        {
            if (FishNet.InstanceFinder.IsServer) return;

            Vector3 pos = new Vector3(px, py, pz);

            try
            {
                // Find any explosion VFX prefab from a weapon in the scene
                GameObject explosionPrefab = null;
                foreach (var w in Object.FindObjectsOfType<Weapon>(true))
                {
                    if (w.genericImpact != null) { explosionPrefab = w.genericImpact; break; }
                }

                if (explosionPrefab != null)
                {
                    var fx = Instantiate(explosionPrefab, pos, Quaternion.identity);
                    fx.transform.localScale = Vector3.one * Mathf.Max(1f, radius * 0.5f);
                    Destroy(fx, 3f);
                }

            }
            catch { }
        }

        private PlayerHealth FindPlayerHealthByNetId(int netId)
        {
            if (netId < 0) return null;
            foreach (var nob in Object.FindObjectsOfType<FishNet.Object.NetworkObject>())
            {
                if ((int)nob.ObjectId == netId)
                {
                    var ph = nob.GetComponent<PlayerHealth>();
                    if (ph != null) return ph;
                }
            }
            return null;
        }

    }
}
