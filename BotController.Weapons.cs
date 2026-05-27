using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StraftatBots
{
    public partial class BotController
    {
        // ===================== FIND WEAPON =====================

        private void HandleFindWeapon()
        {
            _isShooting = false;

            // If we already have a weapon target, keep going toward it
            if (_weaponTarget != null && _targetItem != null && !_targetItem.isTaken)
            {
                MoveToward(_weaponTarget.position, _sprintSpeed);
                if (HorizontalDist(transform.position, _weaponTarget.position) <= _pickupRange)
                {
                    State = BotState.PickUpWeapon;
                    return;
                }
                // Only re-check for better weapons every 5s
                _searchTimer += Time.deltaTime;
                if (_searchTimer < 5f) return;
                _searchTimer = 0f;
                // Check if a much closer weapon appeared
                var closer = FindNearestWeapon();
                if (closer != null && closer != _targetItem)
                {
                    float curDist = Vector3.Distance(transform.position, _targetItem.transform.position);
                    float newDist = Vector3.Distance(transform.position, closer.transform.position);
                    if (newDist < curDist * 0.4f)
                    {
                        _targetItem = closer;
                        _weaponTarget = closer.transform;
                        _graphPath.Clear();
                        _repathTimer = 0f;
                    }
                }
                return;
            }

            // No weapon target — wander while searching
            Wander();

            _searchTimer += Time.deltaTime;
            if (_searchTimer < _searchInterval) return;
            _searchTimer = 0f;

            // Priority 1: find a weapon with an unbroken path to it
            if (NavGraph.Instance != null && NavGraph.Instance.HasData)
            {
                var (locPos, locLabel, locPath) = NavGraph.Instance.FindReachableMapLocation(transform.position);
                if (locPath.Count > 0 && locLabel != "Spawn" && locLabel != "Spawner")
                {
                    // Found a weapon with a connected path — go directly
                    _graphPath = locPath;
                    _graphPathIndex = 0;
                    _lastReachedNode = null;
                    _prevReachedNode = null;
                    // Find the actual ItemBehaviour at that location
                    ItemBehaviour[] allItems = GetCachedItems();
                    foreach (var item in allItems)
                    {
                        if (item != null && !item.isTaken && Vector3.Distance(item.transform.position, locPos) < 3f)
                        {
                            _targetItem = item;
                            _weaponTarget = item.transform;
                            State = BotState.GoToWeapon;
                            return;
                        }
                    }
                }
            }

            // Priority 2: nearest weapon (may not have connected path)
            ItemBehaviour closest = FindNearestWeapon();
            if (closest != null)
            {
                _targetItem = closest;
                _weaponTarget = closest.transform;
                State = BotState.GoToWeapon;
            }
        }

        private void HandleGoToWeapon()
        {
            _isShooting = false;
            if (_targetItem == null || _targetItem.isTaken)
            {
                State = BotState.FindWeapon;
                _weaponTarget = null;
                _targetItem = null;
                _weaponPursuitTimer = 0f;
                return;
            }

            // No timeout — bot keeps trying via graph pathfinding
            // Stuck detection + blacklisting handles unreachable targets instead
            _weaponPursuitTimer += Time.deltaTime;

            // Only switch targets if stuck recovery already redirected us
            if (_weaponPursuitTimer > 60f && _targetItem != null)
            {
                // After 60s, check if a closer weapon appeared
                var closer = FindNearestWeapon();
                if (closer != null && closer != _targetItem)
                {
                    float curDist = Vector3.Distance(transform.position, _targetItem.transform.position);
                    float newDist = Vector3.Distance(transform.position, closer.transform.position);
                    if (newDist < curDist * 0.5f) // Only switch if significantly closer
                    {
                        _targetItem = closer;
                        _weaponTarget = closer.transform;
                        _weaponPursuitTimer = 0f;
                        _graphPath.Clear();
                    }
                }
                _weaponPursuitTimer = 0f; // Reset check timer
            }

            MoveToward(_weaponTarget.position, _sprintSpeed);

            if (HorizontalDist(transform.position, _weaponTarget.position) <= _pickupRange)
            {
                _weaponPursuitTimer = 0f;
                State = BotState.PickUpWeapon;
            }
        }

        // ===================== PICK UP =====================

        private void HandlePickUpWeapon()
        {
            if (_targetItem == null || _targetItem.isTaken)
            {
                _targetItem = null;
                State = BotState.FindWeapon;
                return;
            }

            // Race condition check: if rootObject is already set, someone grabbed it
            if (_targetItem.rootObject != null)
            {
                _targetItem = null;
                State = BotState.FindWeapon;
                return;
            }

            // If weapon layer changed from 7 (ground), someone picked it up
            if (_targetItem.gameObject.layer != 7)
            {
                _targetItem = null;
                State = BotState.FindWeapon;
                return;
            }

            // Double-check: if a player's hand is near this weapon, back off
            foreach (var ph in GetCachedPlayers())
            {
                if (ph == null || IsBot(ph)) continue;
                float playerDist = Vector3.Distance(ph.transform.position, _targetItem.transform.position);
                if (playerDist < 3f)
                {
                    _blacklistedWeapons[_targetItem] = Time.time;
                    _targetItem = null;
                    State = BotState.FindWeapon;
                    return;
                }
            }

            _heldWeaponObj = _targetItem.gameObject;
            _weaponSource = _heldWeaponObj;
            _heldWeapon = _heldWeaponObj.GetComponent<Weapon>();
            _heldBehaviour = _targetItem;

            if (_heldWeapon == null)
            {
                _heldWeaponObj = null;
                State = BotState.FindWeapon;
                return;
            }

            // Skip weapons bots can't use (repulsor is now allowed)
            string wname = _heldWeaponObj.name.ToLower();
            if (_heldWeaponObj.GetComponent<Taser>() != null ||
                _heldWeaponObj.GetComponent<FlashLight>() != null ||
                wname.Contains("taser") ||
                wname.Contains("stunmine") || wname.Contains("stun mine") ||
                wname.Contains("flashlight") || wname.Contains("flash light"))
            {
                _heldWeaponObj = null;
                _heldWeapon = null;
                _heldBehaviour = null;
                _targetItem = null;
                State = BotState.FindWeapon;
                return;
            }

            Plugin.Log.LogInfo($"[{BotName}] Picked up {_heldWeaponObj.name}");

            // Mark as taken
            _heldBehaviour.isTaken = true;
            _heldBehaviour.rootObject = gameObject;
            _heldBehaviour.lastPlayerHolder = gameObject;
            _heldBehaviour.playerController = _fpc;
            _heldBehaviour.cam = _botCam;
            if (_playerPickup != null)
                _heldBehaviour.playerPickup = _playerPickup;

            _heldWeapon.inRightHand = true;
            _heldWeapon.inLeftHand = false;
            _heldWeapon.heldOnce = true;
            if (_playerValues != null)
                _heldWeapon.playerValues = _playerValues;
            SetField(_heldWeapon, "playerController", _fpc);
            SetField(_heldWeapon, "cam", _botCam);

            // Disable collider so players can't interact with it
            Collider weaponCol = _heldWeaponObj.GetComponent<Collider>();
            if (weaponCol != null) weaponCol.enabled = false;

            // Sync weapon to all clients via game's native ObserversRpc
            try
            {
                if (_playerPickup != null)
                {
                    var method = typeof(PlayerPickup).GetMethod("SetObjectInHandObserver",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (method != null)
                    {
                        _playerPickup.enabled = true;
                        method.Invoke(_playerPickup, new object[] {
                            _heldWeaponObj,
                            _heldWeaponObj.transform.position,
                            _heldWeaponObj.transform.rotation,
                            gameObject,
                            true
                        });
                        _playerPickup.enabled = false;
                    }
                }
            }
            catch { }

            PositionWeaponAtHand();
            _heldWeaponObj.transform.localScale = Vector3.one;

            // Set anim from weapon
            if (_heldBehaviour != null && !string.IsNullOrEmpty(_heldBehaviour.rightHandAnim) && _bodyAnimator != null)
            {
                TrySet(_bodyAnimator, "TwoHanded", false);
                TrySet(_bodyAnimator, "DoubleHanded", false);
                TrySet(_bodyAnimator, "RightHanded", false);
                TrySet(_bodyAnimator, _heldBehaviour.rightHandAnim, true);
            }

            // Set weapon references so game's damage code can identify the bot
            _heldWeapon.rootObject = gameObject;
            var pv = GetComponent<PlayerValues>();
            if (pv != null) _heldWeapon.playerValues = pv;

            // Disable weapon scripts to prevent RPC calls
            if (_heldBehaviour != null) _heldBehaviour.enabled = false;
            foreach (var wb in _heldWeaponObj.GetComponents<MonoBehaviour>())
            {
                if (wb is Gun || wb is Shotgun || wb is ChargeGun || wb is Minigun ||
                    wb is BeamGun || wb is LargeRaycastGun || wb is BumpGun ||
                    wb is DualLauncher || wb is WeaponHandSpawner)
                    wb.enabled = false;
            }
            foreach (var mcc in _heldWeaponObj.GetComponentsInChildren<MeleeChildCollision>(true))
                mcc.enabled = false;
            var bumpGun = _heldWeaponObj.GetComponent<BumpGun>();
            if (bumpGun != null) bumpGun.enabled = false;
            var dualLauncher = _heldWeaponObj.GetComponent<DualLauncher>();
            if (dualLauncher != null) dualLauncher.enabled = false;

            // Disable MeleeChildCollision to prevent NRE spam from melee collision triggers
            foreach (var mcc in _heldWeaponObj.GetComponentsInChildren<MeleeChildCollision>(true))
                mcc.enabled = false;

            // Detect if this is a projectile weapon
            _isProjectileWeapon = false;
            _projectilePrefab = null;
            _launchForce = 12f;

            // DualLauncher: read trickShot.template or _projectile fields
            var dl = _heldWeaponObj.GetComponent<DualLauncher>();
            if (dl != null)
            {
                // Read from source weapon (not clone) if available
                var dlSource = (_weaponSource != null ? _weaponSource.GetComponent<DualLauncher>() : null) ?? dl;
                var forceField = typeof(DualLauncher).GetField("launchForce",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (forceField != null) _launchForce = (float)forceField.GetValue(dlSource);

                // Check mode flags to determine which projectile prefab to use
                bool isGrenadeLauncher = ReadBoolField(dlSource, "grenade");
                bool isObus = ReadBoolField(dlSource, "obus");
                bool isBubble = ReadBoolField(dlSource, "bubble");
                bool isRebond = ReadBoolField(dlSource, "rebond");
                bool isShrapnel = ReadBoolField(dlSource, "shrapnel");
                bool usesTrickShot = isGrenadeLauncher || isObus || isBubble;

                if (usesTrickShot)
                {
                    // Grenade/obus/bubble: use trickShot.template
                    var trickField = typeof(DualLauncher).GetField("trickShot",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (trickField != null)
                    {
                        var trickShot = trickField.GetValue(dlSource);
                        if (trickShot != null)
                        {
                            var templateField = trickShot.GetType().GetField("template",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (templateField != null)
                            {
                                var template = templateField.GetValue(trickShot) as Component;
                                if (template != null)
                                {
                                    _isProjectileWeapon = true;
                                    _projectilePrefab = template;
                                    Plugin.Log.LogInfo($"[{BotName}] DualLauncher projectile (trickShot): prefab={template.name} force={_launchForce}");
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Standard launcher (rocket, etc): use _projectile field
                    var projField = typeof(DualLauncher).GetField("_projectile",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (projField != null)
                    {
                        var val = projField.GetValue(dlSource);
                        if (val is Component comp && comp != null)
                        {
                            _isProjectileWeapon = true;
                            _projectilePrefab = comp;
                            Plugin.Log.LogInfo($"[{BotName}] DualLauncher projectile (_projectile): prefab={comp.name} force={_launchForce}");
                        }
                    }
                    // Also check _projectile2 (rebond) and shrapnelProj
                    if (!_isProjectileWeapon && isRebond)
                    {
                        var p2Field = typeof(DualLauncher).GetField("_projectile2",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (p2Field != null)
                        {
                            var val = p2Field.GetValue(dlSource);
                            if (val is Component comp2 && comp2 != null)
                            {
                                _isProjectileWeapon = true;
                                _projectilePrefab = comp2;
                                Plugin.Log.LogInfo($"[{BotName}] DualLauncher projectile (rebond): prefab={comp2.name} force={_launchForce}");
                            }
                        }
                    }
                }
            }

            // Non-DualLauncher: check for generic _projectile field
            if (!_isProjectileWeapon)
            {
                foreach (var mb in _heldWeaponObj.GetComponents<MonoBehaviour>())
                {
                    var field = mb.GetType().GetField("_projectile",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (field != null)
                    {
                        var val = field.GetValue(mb);
                        if (val is Component comp && comp != null)
                        {
                            _isProjectileWeapon = true;
                            _projectilePrefab = comp;
                            var ff = mb.GetType().GetField("launchForce",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (ff != null) _launchForce = (float)ff.GetValue(mb);
                            Plugin.Log.LogInfo($"[{BotName}] Projectile weapon: prefab={comp.name} force={_launchForce}");
                            break;
                        }
                    }
                }
            }

            _weaponTarget = null;
            _targetItem = null;
            _equipTimer = 0.2f;

            // Reset weapon state machine
            _fireTimer = 0f;
            _isBurstFiring = false;
            _burstShotsRemaining = 0;
            _isReloading = false;
            _isChargingWeapon = false;
            _isSpinningUp = false;
            _minigunSpunUp = false;
            _recoilAccumulated = 0f;
            _shotsSinceRest = 0f;

            // Initialize chargedBullets for reload weapons (Gun.Start() doesn't run since script is disabled)
            if (ReadBoolField(_heldWeapon, "reloadWeapon"))
            {
                int ammoCharge = ReadIntField(_heldWeapon, "ammoCharge", 1);
                int charged = (int)ReadFloatField(_heldWeapon, "chargedBullets", 0f);
                if (charged <= 0 && _heldWeapon.currentAmmo > 0)
                {
                    int toCharge = Mathf.Min(ammoCharge, _heldWeapon.currentAmmo);
                    SetFloatField(_heldWeapon, "chargedBullets", (float)toCharge);
                    _heldWeapon.currentAmmo -= toCharge;
                    Plugin.Log.LogInfo($"[{BotName}] Initialized chargedBullets={toCharge} for reload weapon (ammoCharge={ammoCharge})");
                }
            }

            // Cache weapon type flags (avoids 6+ GetComponent calls every frame in HandleHunt)
            CacheWeaponTypes();

            State = BotState.Hunt;
        }

        private void CacheWeaponTypes()
        {
            if (_heldWeaponObj == null)
            {
                _cachedIsMelee = _cachedIsPlaceable = _cachedIsDualLauncher = _cachedIsBubbleLauncher = _cachedIsGrenade = _cachedIsShotgun = _cachedIsExplosiveWeapon = _cachedIsMinigun = _cachedIsChargeGun = _cachedIsBeamGun = _cachedIsLargeRaycast = false;
                _cachedShotgun = null; _cachedMinigun = null; _cachedChargeGun = null; _cachedBeamGun = null; _cachedMeleeWeapon = null; _cachedLargeRaycast = null;
                return;
            }
            _cachedIsMelee = _heldWeaponObj.GetComponent<MeleeWeapon>() != null;
            _cachedIsPlaceable = _heldWeaponObj.GetComponent<WeaponHandSpawner>() != null;
            _cachedIsDualLauncher = _heldWeaponObj.GetComponent<DualLauncher>() != null;
            _cachedShotgun = _heldWeaponObj.GetComponent<Shotgun>();
            _cachedMinigun = _heldWeaponObj.GetComponent<Minigun>();
            _cachedChargeGun = _heldWeaponObj.GetComponent<ChargeGun>();
            _cachedBeamGun = _heldWeaponObj.GetComponent<BeamGun>();
            _cachedMeleeWeapon = _heldWeaponObj.GetComponent<MeleeWeapon>();
            _cachedIsShotgun = _cachedShotgun != null;
            _cachedIsMinigun = _cachedMinigun != null;
            _cachedIsChargeGun = _cachedChargeGun != null;
            _cachedIsBeamGun = _cachedBeamGun != null;
            _cachedLargeRaycast = _heldWeaponObj.GetComponent<LargeRaycastGun>();
            _cachedIsLargeRaycast = _cachedLargeRaycast != null;
            var dlForFlags = (_weaponSource != null ? _weaponSource.GetComponent<DualLauncher>() : null)
                ?? _heldWeaponObj.GetComponent<DualLauncher>();
            _cachedIsBubbleLauncher = _cachedIsDualLauncher && ReadBoolField(dlForFlags, "bubble");
            bool isDualLauncherGrenade = _cachedIsDualLauncher && ReadBoolField(dlForFlags, "grenade");
            _cachedIsGrenade = isDualLauncherGrenade || (
                !_cachedIsDualLauncher && (
                _heldWeaponObj.GetComponent<HandGrenade>() != null ||
                _heldWeaponObj.GetComponent<HandGrenadeTwo>() != null ||
                _heldWeaponObj.GetComponent<PhysicsGrenade>() != null ||
                _heldWeaponObj.name.ToLower().Contains("grenade")));
            _cachedIsExplosiveWeapon = _isProjectileWeapon || _cachedIsDualLauncher;
            _cachedIsRepulsive = _heldWeaponObj.GetComponent<RepulsiveGun>() != null;
            _cachedPropeller = _heldWeaponObj.GetComponent<Propeller>();
            _cachedIsPropeller = _cachedPropeller != null;
        }

        // ===================== WEAPON =====================

        private void DropWeapon()
        {
            if (_heldWeaponObj == null) return;


            // Re-enable weapon scripts
            foreach (var wb in _heldWeaponObj.GetComponents<MonoBehaviour>())
            {
                if (wb is Gun || wb is Shotgun || wb is ChargeGun || wb is Minigun ||
                    wb is BeamGun || wb is LargeRaycastGun || wb is BumpGun ||
                    wb is DualLauncher || wb is WeaponHandSpawner || wb is MeleeWeapon)
                    wb.enabled = true;
            }
            foreach (var mcc in _heldWeaponObj.GetComponentsInChildren<MeleeChildCollision>(true))
                mcc.enabled = true;
            if (_heldBehaviour != null) _heldBehaviour.enabled = true;

            // Clear bot references
            if (_heldBehaviour != null)
            {
                _heldBehaviour.rootObject = null;
                _heldBehaviour.playerController = null;
                _heldBehaviour.cam = null;
                _heldBehaviour.playerPickup = null;
            }
            if (_heldWeapon != null)
            {
                _heldWeapon.inRightHand = false;
                _heldWeapon.inLeftHand = false;
                SetField(_heldWeapon, "playerController", null);
                SetField(_heldWeapon, "cam", null);
            }

            // Unparent and re-enable collider
            _heldWeaponObj.transform.SetParent(null);
            Collider col = _heldWeaponObj.GetComponent<Collider>();
            if (col != null) col.enabled = true;
            _heldWeaponObj.transform.position = transform.position + transform.forward * 0.5f + Vector3.up * 0.5f;
            _heldWeaponObj.layer = 7;

            Rigidbody rb = _heldWeaponObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.AddForce(transform.forward * 2f + Vector3.up * 3f, ForceMode.Impulse);
            }

            _heldWeaponObj = null;
            _heldWeapon = null;
            _heldBehaviour = null;
            _weaponSource = null;
        }

        private void DestroyHeldWeapon()
        {
            if (_heldWeaponObj != null)
            {
                // Detach from bot hierarchy first
                _heldWeaponObj.transform.SetParent(null);
                Object.Destroy(_heldWeaponObj);
            }
            _heldWeaponObj = null;
            _heldWeapon = null;
            _heldBehaviour = null;
        }
    }
}
