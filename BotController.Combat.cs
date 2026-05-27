using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace StraftatBots
{
    public partial class BotController
    {
        // ===================== OPPORTUNISTIC =====================

        private float _opportunisticTimer;

        /// <summary>
        /// Cross-state awareness: grab nearby weapons while hunting, shoot nearby enemies while searching.
        /// Runs before the main state handler.
        /// </summary>
        private void HandleOpportunistic()
        {
            _opportunisticTimer -= Time.deltaTime;
            if (_opportunisticTimer > 0f) return;
            _opportunisticTimer = 0.5f;

            // While hunting: pick up nearby weapons if low ammo OR grab second one-handed weapon
            if (State == BotState.Hunt && _heldWeapon != null)
            {
                bool lowAmmo = _heldWeapon.needsAmmo && _heldWeapon.currentAmmo <= 2;
                bool canDualWield = !_heldWeapon.requireBothHands && _playerPickup != null
                    && !_playerPickup.hasObjectInLeftHand;

                if (lowAmmo || canDualWield)
                {
                    ItemBehaviour[] items = GetCachedItems();
                    foreach (var item in items)
                    {
                        if (item == null || item.isTaken) continue;
                        if (item.rootObject != null || item.gameObject.layer != 7) continue;
                        float dist = Vector3.Distance(transform.position, item.transform.position);
                        if (dist < 3f)
                        {
                            var w = item.GetComponent<Weapon>();
                            if (w == null) continue;

                            if (lowAmmo)
                            {
                                // Low ammo — swap to this weapon
                                _targetItem = item;
                                _weaponTarget = item.transform;
                                State = BotState.PickUpWeapon;
                                return;
                            }
                            else if (canDualWield && !w.requireBothHands)
                            {
                                // Dual wield — pick up in left hand
                                try
                                {
                                    var method = typeof(PlayerPickup).GetMethod("SetObjectInLeftHandObserver",
                                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                    if (method != null)
                                    {
                                        method.Invoke(_playerPickup, new object[] { item.gameObject, gameObject });
                                        Plugin.Log.LogInfo($"[{BotName}] Dual wield: picked up {item.weaponName} in left hand");
                                    }
                                }
                                catch { }
                                return;
                            }
                        }
                    }
                }
            }

            // While searching for weapons: if an enemy is very close and we have a weapon, fight
            if ((State == BotState.FindWeapon || State == BotState.GoToWeapon) && _heldWeapon != null)
            {
                Transform nearEnemy = FindNearestPlayer();
                if (nearEnemy != null)
                {
                    float dist = Vector3.Distance(transform.position, nearEnemy.position);
                    if (dist < 15f)
                    {
                        _playerTarget = nearEnemy;
                        State = BotState.Hunt;
                        return;
                    }
                }
            }

            // While hunting with no weapon (shouldn't happen but does after drops): find weapon
            if (State == BotState.Hunt && _heldWeapon == null)
            {
                State = BotState.FindWeapon;
            }
        }

        // ===================== HUNT =====================

        private void HandleHunt()
        {
            if (_heldWeapon == null || _heldWeaponObj == null)
            {
                DropWeapon();
                State = BotState.FindWeapon;
                return;
            }

            // Propeller: don't fight — keep looking for a real weapon
            if (_cachedIsPropeller)
            {
                State = BotState.FindWeapon;
                return;
            }

            if (_heldWeapon.needsAmmo && _heldWeapon.currentAmmo <= 0)
            {
                DropWeapon();
                _hasWanderTarget = false;
                _searchTimer = _searchInterval;
                State = BotState.FindWeapon;
                return;
            }

            // Immediate target switch: someone just damaged us
            if (_playerHealth != null && _playerHealth.killer != null
                && _playerHealth.killer != transform && _playerHealth.killer != _playerTarget)
            {
                var attackerPH = _playerHealth.killer.GetComponent<PlayerHealth>();
                if (attackerPH != null && !attackerPH.isKilled && attackerPH.health > 0f)
                {
                    _playerTarget = _playerHealth.killer;
                    _combatStaleTimer = 0f;
                    _searchTimer = 0f;
                }
            }

            _searchTimer += Time.deltaTime;
            float searchRate = _playerTarget == null ? 0f : 2f;
            if (_searchTimer >= searchRate)
            {
                _searchTimer = 0f;
                var nearest = FindNearestPlayer();
                if (_playerTarget == null)
                {
                    _playerTarget = nearest;
                }
                else
                {
                    var curPH = _playerTarget.GetComponentInParent<PlayerHealth>();
                    if (curPH == null || curPH.isKilled || curPH.health <= 0f)
                    {
                        _playerTarget = nearest;
                        _combatStaleTimer = 0f;
                    }
                    else if (nearest != null && nearest != _playerTarget)
                    {
                        float curDist = Vector3.Distance(transform.position, _playerTarget.position);
                        float newDist = Vector3.Distance(transform.position, nearest.position);

                        // Switch if closer AND has line of sight
                        if (newDist < curDist * 0.6f)
                        {
                            Vector3 toNew = nearest.position - transform.position;
                            bool canSee = !Physics.Raycast(transform.position + Vector3.up,
                                toNew.normalized, toNew.magnitude, WALL_MASK, QueryTriggerInteraction.Ignore);
                            if (canSee)
                                _playerTarget = nearest;
                        }
                    }
                }
            }

            if (_playerTarget == null)
            {
                _isShooting = false;
                StopLean();
                StopCrouch();
                Wander();
                return;
            }

            float dist = HorizontalDist(transform.position, _playerTarget.position);
            float heightDiff = Mathf.Abs(transform.position.y - _playerTarget.position.y);
            bool onDifferentLevel = heightDiff > 3f;
            bool targetAbove = _playerTarget.position.y > transform.position.y + 3f;
            bool targetBelow = _playerTarget.position.y < transform.position.y - 3f;

            // ---- Cross-level shooting: always shoot if we have line of sight ----
            if (onDifferentLevel && HasLineOfSight())
            {
                LookAtTarget(_playerTarget.position);
                AimCamAtTarget();
                TryShoot();
            }

            // Multi-level: graph handles this via jump/ladder edges — just MoveToward target
            if (onDifferentLevel)
            {
                MoveToward(_playerTarget.position, _sprintSpeed);
                return;
            }

            // Always face the target smoothly
            LookAtTarget(_playerTarget.position);
            AimCamAtTarget();
            UpdateCombatBehavior(dist);

            // Use cached weapon type flags (set in CacheWeaponTypes on pickup)
            bool isMelee = _cachedIsMelee;
            bool isPlaceable = _cachedIsPlaceable;
            bool isGrenade = _cachedIsGrenade;
            _speedChangeCooldown -= Time.deltaTime;
            _equipTimer -= Time.deltaTime;

            // Still equipping — wander, don't approach target (especially with placeables)
            if (_equipTimer > 0f)
            {
                SetShooting(false);
                Wander();
                return;
            }

            // Placeable items: AVOID enemies, find placement spot
            if (isPlaceable)
            {
                SetShooting(false);
                StopLean();

                // Always run AWAY from enemies when holding a placeable
                if (dist < 15f)
                {
                    Vector3 away = (transform.position - _playerTarget.position);
                    away.y = 0; if (away.sqrMagnitude > 0.01f) away.Normalize(); else away = transform.forward;
                    MoveToward(transform.position + away * 10f, _sprintSpeed);
                }

                int envMask = GROUND_MASK;
                string wn = _heldWeaponObj != null ? _heldWeaponObj.name.ToLower() : "";
                bool isAPMine = wn.Contains("apmine") || wn.Contains("ap mine");
                bool isProxMine = !isAPMine && (wn.Contains("proximit") || wn.Contains("proxy"));
                bool isClaymore = wn.Contains("claymore");
                // Fallback: check spawner flags if name didn't match
                if (!isAPMine && !isProxMine && !isClaymore && _heldWeaponObj != null)
                {
                    var sp = _heldWeaponObj.GetComponent<WeaponHandSpawner>();
                    if (sp != null)
                    {
                        isAPMine = ReadBoolField(sp, "apmine");
                        isProxMine = !isAPMine && ReadBoolField(sp, "proximityMine");
                        isClaymore = !isAPMine && !isProxMine && ReadBoolField(sp, "claymore");
                    }
                }

                if (isAPMine)
                {
                    // AP MINE: wall OR floor, 4 ammo — place all then drop
                    // Prefer ground (~70%), wall (~30%) for variety
                    bool placed = false;
                    bool tryWallFirst = Random.value < 0.3f;

                    if (tryWallFirst)
                    {
                        // Wall placement: mine's UP axis = wall normal (facing outward)
                        Vector3 camPos = _botCam != null ? _botCam.transform.position : (transform.position + Vector3.up * 1.5f);
                        Vector3[] dirs = { transform.forward, Quaternion.Euler(0, 45, 0) * transform.forward,
                            Quaternion.Euler(0, -45, 0) * transform.forward, Quaternion.Euler(0, 90, 0) * transform.forward,
                            Quaternion.Euler(0, -90, 0) * transform.forward };
                        foreach (var d in dirs)
                        {
                            if (Physics.Raycast(camPos, d, out RaycastHit wallHit, 3f, envMask, QueryTriggerInteraction.Ignore))
                            {
                                float angle = Vector3.Angle(wallHit.normal, Vector3.up);
                                if (angle > 45f)
                                {
                                    // Game uses transform.up = normal — mine faces outward from wall
                                    Quaternion placeRot = Quaternion.FromToRotation(Vector3.up, wallHit.normal);
                                    PlaceArmedItemAt(wallHit.point, placeRot);
                                    placed = true;
                                    break;
                                }
                            }
                        }
                    }

                    // Ground placement (primary)
                    if (!placed)
                    {
                        Vector3 groundPos = transform.position + transform.forward * 1.5f;
                        if (Physics.Raycast(groundPos + Vector3.up, Vector3.down, out RaycastHit groundHit, 5f, envMask, QueryTriggerInteraction.Ignore))
                        {
                            PlaceArmedItemAt(groundHit.point + Vector3.up * 0.02f, Quaternion.FromToRotation(Vector3.up, groundHit.normal));
                            placed = true;
                        }
                    }

                    if (placed && _heldWeapon != null && _heldWeapon.currentAmmo > 0)
                    {
                        // More ammo left — wander to spread mines, keep placing
                        _wanderChangeTimer = 0f;
                        _hasWanderTarget = false;
                        return;
                    }
                    if (placed) State = BotState.FindWeapon;
                    return;
                }
                else if (isProxMine)
                {
                    // PROXIMITY MINE: ground only, 1 ammo — place and drop
                    Vector3 groundPos = transform.position + transform.forward * 1.5f;
                    if (Physics.Raycast(groundPos + Vector3.up, Vector3.down, out RaycastHit groundHit, 5f, envMask, QueryTriggerInteraction.Ignore))
                    {
                        PlaceArmedItemAt(groundHit.point + Vector3.up * 0.02f, Quaternion.FromToRotation(Vector3.up, groundHit.normal));
                        State = BotState.FindWeapon;
                    }
                    return;
                }
                else
                {
                    // CLAYMORE: wall only (>70°), 1 ammo — place and drop
                    Vector3 camPos = _botCam != null ? _botCam.transform.position : (transform.position + Vector3.up * 1.5f);
                    _claymoreAimDirs[0] = transform.forward;
                    _claymoreAimDirs[1] = Quaternion.Euler(0, 30, 0) * transform.forward;
                    _claymoreAimDirs[2] = Quaternion.Euler(0, -30, 0) * transform.forward;
                    _claymoreAimDirs[3] = Quaternion.Euler(0, 60, 0) * transform.forward;
                    _claymoreAimDirs[4] = Quaternion.Euler(0, -60, 0) * transform.forward;
                    _claymoreAimDirs[5] = Quaternion.Euler(0, 90, 0) * transform.forward;
                    _claymoreAimDirs[6] = Quaternion.Euler(0, -90, 0) * transform.forward;
                    _claymoreAimDirs[7] = -transform.forward;

                    foreach (var aimDir in _claymoreAimDirs)
                    {
                        if (Physics.Raycast(camPos, aimDir, out RaycastHit wallHit, 3f, envMask, QueryTriggerInteraction.Ignore))
                        {
                            float wallAngle = Vector3.Angle(wallHit.normal, Vector3.up);
                            if (wallAngle > 70f)
                            {
                                PlaceArmedItemAt(wallHit.point, Quaternion.LookRotation(wallHit.normal));
                                State = BotState.FindWeapon;
                                return;
                            }
                        }
                    }
                }

                // No placement spot found — wander to find one
                if (dist >= 15f)
                    Wander();

                return;
            }

            // Grenades: throw live grenade toward enemy, find new weapon
            if (isGrenade)
            {
                SetShooting(false);
                StopLean();
                if (dist > 25f)
                    MoveToward(_playerTarget.position, _sprintSpeed);
                else
                {
                    LookAtTarget(_playerTarget.position);
                    AimCamAtTarget();
                    ThrowGrenade();
                    State = BotState.FindWeapon;
                }
                return;
            }

            if (_cachedIsRepulsive)
            {
                // Repulsor: rush toward enemy and fire to push them (ideally off edges)
                StopLean();
                if (dist > 5f)
                {
                    SetShooting(false);
                    MoveToward(_playerTarget.position, _sprintSpeed);
                }
                else
                {
                    // In range — face target and fire
                    SetShooting(true);
                    LookAtTarget(_playerTarget.position);
                    TryShoot();
                    // Strafe while pushing
                    CombatMovement(dist);
                }
                return;
            }

            if (isMelee)
            {
                SetShooting(false);
                StopLean();
                const float meleeCloseDistance = 1.15f;
                float attackStartRange = Mathf.Min(_meleeRange, 2.1f);

                if (dist > meleeCloseDistance)
                {
                    // Keep closing even while swinging. The old _meleeRange gate made bots
                    // stop at the edge of sword range and whiff in place.
                    MoveToward(_playerTarget.position, _sprintSpeed);
                    if (dist < _meleeRange * 3f && !_isSliding && _cc != null && _cc.isGrounded)
                        StartSprintSlide(0.5f);
                }

                if (dist <= attackStartRange)
                    TryMeleeAttack();

                return;
            }
            else
            {
                // Weapon-specific effective ranges
                float effectiveRange = GetWeaponEffectiveRange();
                float enterRange = Mathf.Min(_attackRange, effectiveRange);
                float exitRange = enterRange + 3f;
                float minDist = _minRangedDist;

                if (_cachedIsShotgun) minDist = 2f;
                if (_cachedIsExplosiveWeapon && !_cachedIsBubbleLauncher) minDist = 15f;
                if (_cachedIsBubbleLauncher) minDist = 0f;

                // Hysteresis band on minDist prevents back-up/strafe flicker near boundary
                float minDistExit = minDist + 1.5f;

                // Track staleness
                if (_isShooting)
                    _combatStaleTimer += Time.deltaTime;
                bool stalemate = _combatStaleTimer > 4f && Time.time - _lastHitTime > 4f;

                // Determine sub-state with hysteresis: -1=back up, 0=strafe, 1=advance.
                // Holding the current sub-state for a short debounce prevents per-frame flipping
                // when the distance hovers near a threshold.
                _huntSubStateHold -= Time.deltaTime;
                float curSub = _huntSubState;
                float newSub = curSub;
                if (curSub > 0.5f)
                {
                    // Currently advancing — stay advancing until inside exitRange (hysteresis)
                    if (dist < minDist) newSub = -1f;
                    else if (dist <= exitRange) newSub = 0f;
                }
                else if (curSub < -0.5f)
                {
                    // Currently backing up — stay until past minDistExit
                    if (dist > enterRange) newSub = 1f;
                    else if (dist >= minDistExit) newSub = 0f;
                }
                else
                {
                    // Currently strafing — switch only on clear threshold break
                    if (dist < minDist) newSub = -1f;
                    else if (dist > enterRange) newSub = 1f;
                }
                if (newSub != curSub && _huntSubStateHold <= 0f)
                {
                    _huntSubState = newSub;
                    _huntSubStateHold = 0.35f; // debounce window
                }

                bool backingUp = _huntSubState < -0.5f;
                bool advancing = _huntSubState > 0.5f;

                if (backingUp)
                {
                    // Too close — back up while shooting
                    StopLean();
                    Vector3 away = (transform.position - _playerTarget.position);
                    away.y = 0; if (away.sqrMagnitude > 0.01f) away.Normalize(); else away = transform.forward;
                    if (_cachedIsExplosiveWeapon && !_cachedIsBubbleLauncher)
                    {
                        SetShooting(false);
                        MoveToward(transform.position + away * 10f, _sprintSpeed);
                    }
                    else
                    {
                        SetShooting(true);
                        MoveToward(transform.position + away * 3f, _walkSpeed);
                        TryShoot();
                    }
                }
                else if (advancing)
                {
                    // Out of range — move toward while shooting if within 2x range
                    MoveToward(_playerTarget.position, _sprintSpeed);
                    if (dist < enterRange * 2f)
                    {
                        LookAtTarget(_playerTarget.position);
                        TryShoot(); // Shoot while running toward
                    }
                    else
                    {
                        SetShooting(false);
                        StopLean();
                    }
                }
                else if (stalemate)
                {
                    // In range but not hitting — push closer while shooting
                    SetShooting(true);
                    MoveToward(_playerTarget.position, _sprintSpeed);
                    TryShoot();
                    if (_combatStaleTimer > 6f) _combatStaleTimer = 0f;
                }
                else
                {
                    // In range, hitting — strafe and shoot
                    SetShooting(true);
                    CombatMovement(dist);
                    TryShoot();
                }
            }
        }

        /// <summary>
        /// Combat movement: strafing, dodging, crouching, leaning. All hazard-aware.
        /// </summary>
        private void CombatMovement(float dist)
        {
            if (_cc == null || !_cc.enabled) return;

            // Zone launch override — ride the force, don't strafe
            if (_zoneForceDuration > 0f)
            {
                bool landedAfterLaunch = _zoneLaunchInAir && _cc.isGrounded && _verticalVelocity <= 0f;
                if (landedAfterLaunch)
                {
                    _zoneForceDuration = 0f;
                    _zoneForce = Vector3.zero;
                    _zoneLaunchInAir = false;
                }
                else
                {
                    if (!_cc.isGrounded) _zoneLaunchInAir = true;
                    Vector3 zoneMove = _zoneForce;
                    zoneMove.y = _verticalVelocity;
                    _zoneForce *= Mathf.Max(0f, 1f - 2f * Time.deltaTime);
                    _zoneForceDuration -= Time.deltaTime;
                    if (_zoneForceDuration <= 0f)
                    {
                        _zoneForce = Vector3.zero;
                        _zoneForceDuration = 0f;
                        _zoneLaunchInAir = false;
                    }
                    DoMove(zoneMove * Time.deltaTime);
                    return;
                }
            }

            // While sliding — maintain locked direction, don't strafe
            // Use low base speed — slide force impulse handles the momentum
            if (_isSliding && _slideLockedDir.sqrMagnitude > 0.01f)
            {
                _lastMoveDir = _slideLockedDir;
                DoMove((_slideLockedDir * 0.5f + Vector3.up * _verticalVelocity) * Time.deltaTime);
                return;
            }

            Vector3 toTarget = (_playerTarget.position - transform.position);
            toTarget.y = 0; toTarget.Normalize();
            Vector3 right = Quaternion.Euler(0, 90, 0) * toTarget;

            // Dodge
            if (_isDodging)
            {
                _dodgeTimer -= Time.deltaTime;
                if (_dodgeTimer <= 0f) _isDodging = false;
                else
                {
                    Vector3 dodgeMove = _dodgeDir;
                    // Edge check on dodge direction
                    if (_cc.isGrounded && IsEdgeAhead(dodgeMove, 1.5f))
                        dodgeMove = -dodgeMove;
                    DoMove((dodgeMove * _sprintSpeed * 1.5f + Vector3.up * _verticalVelocity) * Time.deltaTime);
                    return;
                }
            }

            // Strafe direction — switch less frequently to avoid visible flip-flopping
            _strafeSwitchTimer -= Time.deltaTime;
            if (_strafeSwitchTimer <= 0f)
            {
                _strafeDir = -_strafeDir;
                _strafeSwitchTimer = Random.Range(3.5f, 6.5f);
            }

            // Smooth approach factor: continuous function of distance instead of 3 discrete steps.
            // Pushes back when too close, holds ground around ~10m, leans in when far.
            // dist<=6 -> -0.3, dist==10 -> +0.15, dist>=18 -> +0.7 (monotonic, smooth).
            float approachTarget;
            if (dist <= 6f) approachTarget = -0.3f;
            else if (dist >= 18f) approachTarget = 0.7f;
            else if (dist < 10f) approachTarget = Mathf.Lerp(-0.3f, 0.15f, (dist - 6f) / 4f);
            else approachTarget = Mathf.Lerp(0.15f, 0.7f, (dist - 10f) / 8f);
            // Low-pass the approach factor so tiny distance wobble can't flip the sign between frames
            _smoothedApproach = Mathf.Lerp(_smoothedApproach, approachTarget, 1f - Mathf.Exp(-7.5f * Time.deltaTime));

            Vector3 strafeDir = (right * _strafeDir * 0.3f + toTarget * _smoothedApproach).normalized;

            // Wall slide via collision feedback
            _collisionTimer -= Time.deltaTime;
            if (_collisionTimer > 0f && _lastCollisionNormal.sqrMagnitude > 0.01f)
            {
                Vector3 colN = _lastCollisionNormal; colN.y = 0; colN.Normalize();
                float dot = Vector3.Dot(strafeDir, -colN);
                if (dot > 0.3f)
                {
                    Vector3 slideDir = strafeDir - Vector3.Dot(strafeDir, -colN) * -colN;
                    if (slideDir.sqrMagnitude > 0.01f)
                        strafeDir = slideDir.normalized;
                }
            }

            // Edge check — try flip, approach, retreat, then stop
            if (_cc.isGrounded && IsEdgeAhead(strafeDir, 1.5f))
            {
                _strafeDir = -_strafeDir;
                strafeDir = (right * _strafeDir * 0.3f + toTarget * _smoothedApproach).normalized;
                if (IsEdgeAhead(strafeDir, 1.5f))
                {
                    // Both strafe sides blocked — try approaching enemy
                    strafeDir = toTarget.normalized;
                    if (IsEdgeAhead(strafeDir, 1.5f))
                    {
                        // Approach also blocked — try retreating
                        strafeDir = -toTarget.normalized;
                        if (IsEdgeAhead(strafeDir, 1.5f))
                            strafeDir = Vector3.zero; // Surrounded by edges — stand still
                    }
                }
            }

            // Emergency 0.5m check on final direction
            if (_cc.isGrounded && strafeDir.sqrMagnitude > 0.01f && IsEdgeAhead(strafeDir, 0.5f))
            {
                // Try opposite
                Vector3 reversed = -strafeDir;
                if (!IsEdgeAhead(reversed, 0.5f))
                    strafeDir = reversed;
                else
                    strafeDir = Vector3.zero; // Both directions are edges — don't move
            }

            // Jump/slide when stuck against wall during combat
            if (_cc.isGrounded && !_isSliding && _stuckTimer > 0.5f && strafeDir.sqrMagnitude > 0.01f)
            {
                var cObs = CheckObstructions(strafeDir, 1.2f);

                if (cObs.WaistBlocked && cObs.CrouchClear)
                {
                    // Slide under
                    InitSlide(strafeDir, duration: 1f);
                    _stuckTimer = 0f;
                }
                else if (cObs.WaistBlocked)
                {
                    // Jump over
                    if (TryJump(JumpReason.CombatStrafe, strafeDir))
                        _stuckTimer = 0f;
                }
            }

            // Open doors during combat too
            TryOpenDoor(strafeDir.sqrMagnitude > 0.01f ? strafeDir : toTarget);

            UpdateLean(toTarget, right);
            UpdateCombatCrouch();

            float speed = _isCrouching ? _walkSpeed : _walkSpeed * 1.2f;

            // Smooth the final move vector between frames. Time-constant ~0.1s — fast enough
            // to stay responsive to dodges/edge checks but enough to kill single-frame direction
            // jitter that made bots look like they were vibrating during combat strafe.
            // When strafe is zero (stand still), snap to zero to avoid coasting past stops.
            if (strafeDir.sqrMagnitude < 0.0001f)
            {
                _smoothedStrafeDir = Vector3.zero;
            }
            else
            {
                float t = 1f - Mathf.Exp(-15f * Time.deltaTime);
                _smoothedStrafeDir = Vector3.Lerp(_smoothedStrafeDir, strafeDir, t);
            }

            Vector3 moveDir = _smoothedStrafeDir;
            if (moveDir.sqrMagnitude > 0.0001f)
                _lastMoveDir = moveDir.normalized;
            DoMove((moveDir * speed + Vector3.up * _verticalVelocity) * Time.deltaTime);
        }

        /// <summary>
        /// Periodically decide to crouch (makes bot harder to hit), dodge, etc.
        /// </summary>
        private void UpdateCombatBehavior(float dist)
        {
            // Trigger dodge when health drops suddenly
            if (_playerHealth != null && !_isDodging)
            {
                float healthPct = _playerHealth.health / _playerHealth.fullHealth;
                if (healthPct < 0.6f && Random.value < 0.02f)
                {
                    _isDodging = true;
                    _dodgeTimer = 0.3f;
                    Vector3 toTarget = (_playerTarget.position - transform.position).normalized;
                    Vector3 right = Quaternion.Euler(0, 90, 0) * toTarget;
                    _dodgeDir = right * (Random.value > 0.5f ? 1f : -1f);
                }
            }
        }

        private void UpdateLean(Vector3 forward, Vector3 right)
        {
            // Only lean when shooting, stationary, and very close to cover (0.6-1.2m)
            if (!_isShooting) { StopLean(); return; }

            // Must be nearly stationary to lean
            float moveSpeed = _cc != null ? Mathf.Sqrt(_cc.velocity.x * _cc.velocity.x + _cc.velocity.z * _cc.velocity.z) : 0f;
            if (moveSpeed > _walkSpeed * 0.3f) { StopLean(); return; }

            int envMask = WALL_MASK;
            Vector3 mid = transform.position + Vector3.up * 1f;

            // Check for cover walls very close (0.5-1.2m) — must be actual cover
            bool wallLeft = Physics.Raycast(mid, -right, 1.2f, envMask, QueryTriggerInteraction.Ignore)
                         && !Physics.Raycast(mid, -right, 0.5f, envMask, QueryTriggerInteraction.Ignore);
            bool wallRight = Physics.Raycast(mid, right, 1.2f, envMask, QueryTriggerInteraction.Ignore)
                          && !Physics.Raycast(mid, right, 0.5f, envMask, QueryTriggerInteraction.Ignore);

            if (wallLeft && !wallRight)
                SetLean(1f);
            else if (wallRight && !wallLeft)
                SetLean(-1f);
            else
                StopLean();
        }

        private void SetLean(float dir)
        {
            // Lean always enabled
            if (_isLeaning && Mathf.Approximately(_leanDir, dir)) return;
            _isLeaning = true;
            _leanDir = dir;
            if (_bodyAnimator != null)
            {
                TrySet(_bodyAnimator, "LeanLeft", dir < 0);
                TrySet(_bodyAnimator, "LeanRight", dir > 0);
            }
            if (_globalAnimator != null)
            {
                TrySet(_globalAnimator, "LeanLeft", dir < 0);
                TrySet(_globalAnimator, "LeanRight", dir > 0);
            }
        }

        private void StopLean()
        {
            if (!_isLeaning) return;
            _isLeaning = false;
            _leanDir = 0f;
            if (_bodyAnimator != null)
            {
                TrySet(_bodyAnimator, "LeanLeft", false);
                TrySet(_bodyAnimator, "LeanRight", false);
            }
            if (_globalAnimator != null)
            {
                TrySet(_globalAnimator, "LeanLeft", false);
                TrySet(_globalAnimator, "LeanRight", false);
            }
        }

        private void UpdateCombatCrouch()
        {
            _crouchTimer -= Time.deltaTime;
            if (_crouchTimer <= 0f)
            {
                if (_isCrouching)
                    StopCrouch();
                else if (Random.value < 0.3f) // 30% chance to crouch
                    StartCrouch(Random.Range(1f, 3f));
                _crouchTimer = Random.Range(2f, 5f);
            }
        }

        private void StartCrouch(float duration)
        {
            if (_isCrouching || _isSliding) return;
            _isCrouching = true;
            _crouchTimer = duration;
            if (_cc != null)
            {
                _cc.height = 1f;
                _cc.center = new Vector3(0, 0.5f, 0);
            }
            if (_bodyAnimator != null) TrySet(_bodyAnimator, "Crouch", true);
            if (_globalAnimator != null) TrySet(_globalAnimator, "Crouch", true);
        }

        private void StopCrouch()
        {
            if (!_isCrouching) return;

            // Check if there's room to stand
            int envMask = GROUND_MASK;
            if (Physics.Raycast(transform.position + Vector3.up * 0.5f, Vector3.up, 1.5f, envMask, QueryTriggerInteraction.Ignore))
                return; // Can't stand yet — stay crouched

            _isCrouching = false;
            if (_cc != null)
            {
                _cc.Move(Vector3.up * 0.5f); // Push up to prevent ground clipping
                _cc.height = STAND_HEIGHT;
                _cc.center = new Vector3(0, STAND_CENTER_Y, 0);
            }
            if (_bodyAnimator != null) TrySet(_bodyAnimator, "Crouch", false);
            if (_globalAnimator != null) TrySet(_globalAnimator, "Crouch", false);
        }

        private void SetShooting(bool shooting)
        {
            if (shooting == _isShooting) return;
            if (_speedChangeCooldown > 0f) return;
            _isShooting = shooting;
            // Reset weapon states when stopping fire
            if (!shooting)
            {
                _minigunSpunUp = false;
                _isSpinningUp = false;
                _isBurstFiring = false;
                _isChargingWeapon = false;
            }
            _speedChangeCooldown = 0.8f; // Lock state for 0.8s
        }

        // ===================== AIMING =====================

        private float _reactionTimer; // Delay before bot starts tracking a new target
        private float _aimSmoothing = 0f; // 0 = starting to aim, 1 = fully locked on
        private Transform _lastAimTarget;

        private void AimCamAtTarget()
        {
            if (_playerTarget == null || _botCam == null) return;
            Vector3 origin = transform.position + Vector3.up * 1.5f;
            Vector3 targetPos = _playerTarget.position + Vector3.up * 1f;

            if (_cachedIsBubbleLauncher)
            {
                _lastAimTarget = _playerTarget;
                _reactionTimer = 0f;
                _aimSmoothing = 1f;
                _aimOffset = Vector3.zero;
                _aimOffsetTimer = 0.2f;

                targetPos = GetTargetAimPoint(origin, leadMovingTarget: true);
                _botCam.transform.position = origin;
                Vector3 aimDir = targetPos - origin;
                if (aimDir.sqrMagnitude < 0.001f) aimDir = transform.forward;
                Quaternion bubbleAimRot = Quaternion.LookRotation(aimDir.normalized);
                _botCam.transform.rotation = Quaternion.Slerp(_botCam.transform.rotation, bubbleAimRot, 28f * Time.deltaTime);
                return;
            }

            // Reaction time — when switching to a new target, delay before aiming
            if (_playerTarget != _lastAimTarget)
            {
                _lastAimTarget = _playerTarget;
                _reactionTimer = Random.Range(0.15f, 0.4f); // Human reaction time
                _aimSmoothing = 0f;
            }
            if (_reactionTimer > 0f)
            {
                _reactionTimer -= Time.deltaTime;
                // During reaction, aim drifts randomly (not locked on yet)
                targetPos += Random.insideUnitSphere * 3f;
            }
            else
            {
                // Gradually improve aim over time (ramp up accuracy)
                _aimSmoothing = Mathf.MoveTowards(_aimSmoothing, 1f, Time.deltaTime * 1.5f);
            }

            // Persistent aim drift — human-like sustained inaccuracy
            _aimOffsetTimer -= Time.deltaTime;
            if (_aimOffsetTimer <= 0f)
            {
                float dist = Vector3.Distance(origin, targetPos);
                // Base inaccuracy from config, scaled by distance AND aim smoothing
                float inaccuracy = _aimInaccuracy * Mathf.Clamp01(dist / _attackRange);
                // Less accurate when bot is moving
                float moveSpeed = _cc != null ? Mathf.Sqrt(_cc.velocity.x * _cc.velocity.x + _cc.velocity.z * _cc.velocity.z) : 0f;
                if (moveSpeed > _walkSpeed * 0.5f) inaccuracy *= 1.5f;
                if (moveSpeed > _sprintSpeed * 0.5f) inaccuracy *= 2f;
                // Reduce inaccuracy as aim locks on
                inaccuracy *= Mathf.Lerp(2f, 0.5f, _aimSmoothing);

                _aimOffset = new Vector3(
                    Random.Range(-inaccuracy, inaccuracy),
                    Random.Range(-inaccuracy * 0.4f, inaccuracy * 0.4f),
                    Random.Range(-inaccuracy, inaccuracy)
                );
                _aimOffsetTimer = Random.Range(0.4f, 0.8f); // Slower offset changes = more sustained drift
            }

            targetPos += _aimOffset;

            _botCam.transform.position = origin;
            // Smooth aim transition — fast enough to track target but not instant
            Quaternion desiredRot = Quaternion.LookRotation((targetPos - origin).normalized);
            _botCam.transform.rotation = Quaternion.Slerp(_botCam.transform.rotation, desiredRot, 10f * Time.deltaTime);
        }

        private Vector3 GetTargetAimPoint(Vector3 origin, bool leadMovingTarget)
        {
            if (_playerTarget == null) return origin + transform.forward * 10f;

            Vector3 point = _playerTarget.position + Vector3.up * 1f;
            try
            {
                var ph = _playerTarget.GetComponentInParent<PlayerHealth>();
                if (ph == null) ph = _playerTarget.GetComponentInChildren<PlayerHealth>();
                if (ph != null)
                {
                    bool hasBounds = false;
                    Bounds bounds = new Bounds(ph.transform.position, Vector3.zero);
                    foreach (var col in ph.GetComponentsInChildren<Collider>(true))
                    {
                        if (col == null || !col.enabled || col.isTrigger) continue;
                        int layer = col.gameObject.layer;
                        if (layer != 11 && layer != 16) continue;
                        if (!hasBounds)
                        {
                            bounds = col.bounds;
                            hasBounds = true;
                        }
                        else bounds.Encapsulate(col.bounds);
                    }
                    if (hasBounds) point = bounds.center;
                }

                if (leadMovingTarget)
                {
                    Vector3 velocity = Vector3.zero;
                    var cc = _playerTarget.GetComponentInParent<CharacterController>();
                    if (cc != null) velocity = cc.velocity;
                    else
                    {
                        var rb = _playerTarget.GetComponentInParent<Rigidbody>();
                        if (rb != null) velocity = rb.velocity;
                    }

                    float dist = Vector3.Distance(origin, point);
                    float lead = Mathf.Clamp(dist / 65f, 0f, 0.28f);
                    point += velocity * lead;
                }
            }
            catch { }

            return point;
        }

        private bool HasLineOfSight()
        {
            if (_playerTarget == null) return false;
            Vector3 origin = transform.position + Vector3.up * 1.5f;
            Vector3 targetPos = _playerTarget.position + Vector3.up * 1f;
            Vector3 dir = targetPos - origin;
            float dist = dir.magnitude;

            // Offset origin slightly toward target to avoid hitting our own collider
            Vector3 startPos = origin + dir.normalized * 0.6f;
            float checkDist = dist - 0.6f;
            if (checkDist <= 0f) return true; // Very close, assume LOS

            // Only check against environment layers (Default=0, plus common env layers)
            // Exclude player(11), ragdoll(10), weapons(7), triggers, UI etc.
            int envMask = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9) | (1 << 12) | (1 << 13) | (1 << 15) | (1 << 16);
            if (Physics.Raycast(startPos, dir.normalized, out RaycastHit hit, checkDist, envMask, QueryTriggerInteraction.Ignore))
            {
                // Check if what we hit is actually the target (or part of target)
                if (hit.collider.transform.root == _playerTarget.root)
                    return true;
                return false;
            }
            return true;
        }

        // ===================== SHOOTING =====================

        /// <summary>
        /// Main shooting entry point. Handles all weapon types with proper timing:
        /// burst fire, reload, charge, spin-up, recoil bloom, per-weapon spread.
        /// </summary>
        private void TryShoot()
        {
            if (_heldWeapon == null || _heldWeaponObj == null || _playerTarget == null || _botCam == null) return;
            if (_isSliding) return; // Can't aim properly while sliding

            // Decay recoil over time (simulates human re-centering aim between shots)
            _recoilAccumulated = Mathf.Max(0f, _recoilAccumulated - Time.deltaTime * 2f);
            if (_fireTimer > _heldWeapon.timeBetweenFire * 3f)
            {
                _shotsSinceRest = Mathf.Max(0f, _shotsSinceRest - Time.deltaTime * 5f);
            }

            // --- RELOAD STATE ---
            if (_isReloading)
            {
                _reloadTimer -= Time.deltaTime;
                if (_reloadTimer <= 0f)
                {
                    _isReloading = false;
                    // Refill chargedBullets from currentAmmo
                    try
                    {
                        int ammoCharge = ReadIntField(_heldWeapon, "ammoCharge", 1);
                        if (_heldWeapon.currentAmmo >= ammoCharge)
                        {
                            _heldWeapon.currentAmmo -= ammoCharge;
                            SetFloatField(_heldWeapon, "chargedBullets", (float)ammoCharge);
                        }
                        else
                        {
                            SetFloatField(_heldWeapon, "chargedBullets", (float)_heldWeapon.currentAmmo);
                            _heldWeapon.currentAmmo = 0;
                        }
                    }
                    catch { }
                }
                return;
            }

            // --- BURST FIRE STATE ---
            if (_isBurstFiring)
            {
                _burstShotTimer -= Time.deltaTime;
                if (_burstShotTimer <= 0f && _burstShotsRemaining > 0)
                {
                    _burstShotsRemaining--;
                    _burstShotTimer = _burstShotDelay;
                    AimCamAtTarget();
                    FireOnce();
                    if (_burstShotsRemaining <= 0)
                    {
                        _isBurstFiring = false;
                        _fireTimer = 0f; // Reset for timeBetweenFire cooldown
                    }
                }
                return;
            }

            // --- CHARGE STATE ---
            if (_isChargingWeapon)
            {
                _chargeTimer += Time.deltaTime;
                AimCamAtTarget(); // Keep tracking while charging
                if (_chargeTimer >= _chargeTimeRequired)
                {
                    _isChargingWeapon = false;
                    // Set accumulatedPower for the weapon's own effects
                    try
                    {
                        var cg = _cachedChargeGun;
                        if (cg != null) SetFloatField(cg, "accumulatedPower", _chargeTimeRequired);
                        var bg = _cachedBeamGun;
                        if (bg != null) SetFloatField(bg, "accumulatedPower", _chargeTimeRequired);
                    }
                    catch { }
                    FireOnce();
                    _fireTimer = 0f;
                }
                return;
            }

            // --- SPIN-UP STATE (Minigun) ---
            if (_isSpinningUp)
            {
                _spinUpTimer += Time.deltaTime;
                AimCamAtTarget();
                if (_spinUpTimer >= _spinUpTimeRequired)
                {
                    _isSpinningUp = false;
                    _minigunSpunUp = true;
                    FireOnce();
                    _fireTimer = 0f;
                }
                return;
            }

            // --- FULL-AUTO BURST PAUSE (skip for minigun — continuous fire) ---
            if (_autoPauseTimer > 0f && !_minigunSpunUp)
            {
                _autoPauseTimer -= Time.deltaTime;
                return;
            }

            // --- FIRE TIMER ---
            _fireTimer += Time.deltaTime;
            float delay = _heldWeapon.timeBetweenFire;
            // Semi-auto weapons (onePressShoot) need click-release-click — humans can't click faster than ~0.5s
            if (ReadBoolField(_heldWeapon, "onePressShoot"))
                delay = Mathf.Max(delay, 0.55f);
            if (_cachedIsBubbleLauncher)
                delay = Mathf.Min(delay, 0.18f);
            if (delay < 0.05f) delay = 0.05f; // Absolute floor to prevent frame-rate shooting
            if (_fireTimer < delay) return;

            // --- AMMO CHECK ---
            bool isReloadWeapon = ReadBoolField(_heldWeapon, "reloadWeapon");
            if (isReloadWeapon)
            {
                float chargedBullets = ReadFloatField(_heldWeapon, "chargedBullets", 0f);
                if (chargedBullets <= 0f && _heldWeapon.currentAmmo <= 0)
                {
                    DropWeapon();
                    State = BotState.FindWeapon;
                    return;
                }
                if (chargedBullets <= 0f && _heldWeapon.currentAmmo > 0)
                {
                    _isReloading = true;
                    _reloadTimer = ReadFloatField(_heldWeapon, "reloadTime", 1f);
                    if (_reloadTimer < 0.1f) _reloadTimer = 0.5f;
                    return;
                }
            }
            else if (_heldWeapon.needsAmmo && _heldWeapon.currentAmmo <= 0)
            {
                DropWeapon();
                State = BotState.FindWeapon;
                return;
            }

            if (!HasLineOfSight()) return;
            AimCamAtTarget();

            // --- WEAPON TYPE DISPATCH ---

            // Charge weapons: start charging
            float chargeTime = 0f;
            if (_cachedChargeGun != null)
                chargeTime = ReadFloatField(_cachedChargeGun, "maxChargeTime", 1f);
            else if (_cachedBeamGun != null)
                chargeTime = ReadFloatField(_cachedBeamGun, "maxChargeTime", 1f);

            if (chargeTime > 0f)
            {
                _isChargingWeapon = true;
                _chargeTimer = 0f;
                _chargeTimeRequired = chargeTime;
                return;
            }

            // Minigun: start spin-up (first shot only)
            if (_cachedMinigun != null && !_minigunSpunUp)
            {
                _isSpinningUp = true;
                _spinUpTimer = 0f;
                _spinUpTimeRequired = ReadFloatField(_cachedMinigun, "timeBeforeShooting", 0.6f);
                return;
            }

            // Burst fire
            bool isBurst = ReadBoolField(_heldWeapon, "burstGun");
            int burstCount = ReadIntField(_heldWeapon, "bulletsAmount", 0);
            float burstDelay = ReadFloatField(_heldWeapon, "timeBetweenBullets", 0.05f);
            if (isBurst && burstCount > 1)
            {
                _isBurstFiring = true;
                _burstShotsRemaining = burstCount;
                _burstShotDelay = burstDelay;
                _burstShotTimer = 0f;
                return;
            }

            // Projectile weapons (rockets, launchers) — don't fire at close walls (self-damage)
            if ((_isProjectileWeapon || _cachedIsDualLauncher) && !_cachedIsBubbleLauncher)
            {
                int envMask = WALL_MASK;
                if (Physics.Raycast(_botCam.transform.position, _botCam.transform.forward, 8f, envMask, QueryTriggerInteraction.Ignore))
                {
                    // Wall within 4m — don't fire, move instead
                    return;
                }
            }

            // Standard single shot
            FireOnce();
            _fireTimer = 0f;

            // Single-use weapons — drop immediately after firing
            if (_heldWeapon != null && _heldWeapon.needsAmmo && _heldWeapon.currentAmmo <= 0)
            {
                DropWeapon();
                State = BotState.FindWeapon;
                return;
            }

            // Full-auto burst tracking — pause after 3-5 shots (skip for minigun — continuous fire)
            if (!ReadBoolField(_heldWeapon, "onePressShoot") && !_minigunSpunUp)
            {
                _autoShotsFired++;
                if (_autoShotsFired >= Random.Range(3, 6))
                {
                    _autoShotsFired = 0;
                    _autoPauseTimer = Random.Range(0.4f, 0.8f);
                }
            }
        }

        /// <summary>
        /// Fire a single shot/pellet group. Handles ammo, effects, raycast, and recoil.
        /// Called once for single-shot, multiple times for burst.
        /// </summary>
        private void FireOnce()
        {
            if (_heldWeapon == null || _heldWeaponObj == null) return;

            // Deduct ammo
            bool isReloadWeapon = ReadBoolField(_heldWeapon, "reloadWeapon");
            if (isReloadWeapon)
            {
                float charged = ReadFloatField(_heldWeapon, "chargedBullets", 0f);
                if (charged > 0f)
                {
                    // Shotguns: game calls RemoveAmmo per pellet, consuming 1 chargedBullet each
                    // So one shot consumes bulletAmount worth of chargedBullets
                    var sg = _cachedShotgun;
                    if (sg != null)
                    {
                        int pellets = ReadIntField(sg, "bulletAmount", 1);
                        SetFloatField(_heldWeapon, "chargedBullets", Mathf.Max(0f, charged - pellets));
                    }
                    else
                    {
                        SetFloatField(_heldWeapon, "chargedBullets", charged - 1f);
                    }
                }
            }
            else if (_heldWeapon.needsAmmo)
            {
                _heldWeapon.currentAmmo--;
            }

            // Effects — spawn muzzle flash locally on host + sound, sync to clients via Mycelium
            // Do NOT call ShootServerEffect/ShootObserversEffect — those are RPCs that trigger
            // hitmarkers and HUD effects on the host as if the host fired the weapon
            Vector3 muzzlePos = _heldWeapon.muzzleFlashPoint != null
                ? _heldWeapon.muzzleFlashPoint.position
                : (transform.position + transform.forward * 0.5f + Vector3.up * 1.3f);
            Vector3 muzzleFwd = _heldWeapon.shootPoint != null
                ? _heldWeapon.shootPoint.forward : transform.forward;

            // Muzzle flash on host — parent to weapon for ChargeGun (handcannon stays attached until fired)
            try
            {
                if (_heldWeapon.muzzleFlash != null)
                {
                    Transform parent = _heldWeaponObj != null ? _heldWeaponObj.transform : null;
                    var flash = Object.Instantiate(_heldWeapon.muzzleFlash, muzzlePos, Quaternion.LookRotation(muzzleFwd), parent);
                    foreach (Transform c in flash.GetComponentsInChildren<Transform>(true))
                        c.gameObject.layer = 0;
                    foreach (var fx in flash.GetComponentsInChildren<ParticleSystem>())
                        fx.Play();
                    // Unparent after a short delay so it detaches naturally
                    if (parent != null)
                        StartCoroutine(UnparentAfterDelay(flash.transform, 0.3f));
                    Object.Destroy(flash, 2f);
                }
            }
            catch { }

            // Sound on host
            try
            {
                if (_heldWeapon.fireClip != null)
                {
                    if (_botAudio != null) _botAudio.PlayOneShot(_heldWeapon.fireClip);
                    else AudioSource.PlayClipAtPoint(_heldWeapon.fireClip, transform.position);
                }
            }
            catch { }

            // Sync to non-host clients via Mycelium
            try
            {
                BotDamageSync.SyncShootEffect(muzzlePos, muzzleFwd, _heldWeaponObj.name);
            }
            catch { }

            // Fire raycast or projectile
            if (_isProjectileWeapon && _projectilePrefab != null)
                FireProjectile();
            else
                FireRaycast();

            // Accumulate recoil (simulates camera kick — increases spread over sustained fire)
            _recoilAccumulated += 0.3f;
            _shotsSinceRest += 1f;
        }

        // ===================== REFLECTION HELPERS =====================

        // Reflection field cache — avoids GetField on every call
        private static readonly Dictionary<(System.Type, string), FieldInfo> _fieldCache = new Dictionary<(System.Type, string), FieldInfo>();
        private static readonly BindingFlags _allFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo GetCachedField(System.Type type, string name)
        {
            var key = (type, name);
            if (!_fieldCache.TryGetValue(key, out var field))
            {
                field = type.GetField(name, _allFlags);
                _fieldCache[key] = field;
            }
            return field;
        }

        private float ReadFloatField(object obj, string name, float fallback = 0f)
        {
            try
            {
                var f = GetCachedField(obj.GetType(), name);
                if (f != null) return (float)f.GetValue(obj);
            }
            catch { }
            return fallback;
        }

        private Vector3 ReadVector3Field(object obj, string name, Vector3 fallback)
        {
            try
            {
                var f = GetCachedField(obj.GetType(), name);
                if (f != null) return (Vector3)f.GetValue(obj);
            }
            catch { }
            return fallback;
        }

        private int ReadIntField(object obj, string name, int fallback = 0)
        {
            try
            {
                var f = GetCachedField(obj.GetType(), name);
                if (f != null) return (int)f.GetValue(obj);
            }
            catch { }
            return fallback;
        }

        private bool ReadBoolField(object obj, string name)
        {
            try
            {
                var f = GetCachedField(obj.GetType(), name);
                if (f != null) return (bool)f.GetValue(obj);
            }
            catch { }
            return false;
        }

        private void SetFloatField(object obj, string name, float value)
        {
            try
            {
                var f = GetCachedField(obj.GetType(), name);
                if (f != null) f.SetValue(obj, value);
            }
            catch { }
        }

        /// <summary>
        /// Effective range per weapon type — bots won't shoot beyond this distance.
        /// </summary>
        private float GetWeaponEffectiveRange()
        {
            if (_heldWeaponObj == null) return _attackRange;

            // Use cached weapon type flags instead of GetComponent every call
            if (_cachedIsShotgun) return 12f;
            if (_cachedIsMinigun) return 25f;
            if (_cachedIsChargeGun) return 50f;
            if (_cachedIsBeamGun) return 50f;
            if (_cachedIsLargeRaycast) return 45f;

            return _attackRange;
        }

        private void FireProjectile()
        {
            Plugin.Log.LogInfo($"[{BotName}] FireProjectile called! prefab={(_projectilePrefab != null ? _projectilePrefab.name : "null")} force={_launchForce}");
            try
            {
                Vector3 spawnPos = _heldWeapon.shootPoint != null
                    ? _heldWeapon.shootPoint.position
                    : (_heldWeapon.muzzleFlashPoint != null
                        ? _heldWeapon.muzzleFlashPoint.position
                        : _botCam.transform.position);

                // Aim directly at target for projectile weapons (small random offset for realism)
                Vector3 dir = _botCam.transform.forward;
                if (_playerTarget != null)
                {
                    Vector3 aimPoint = _cachedIsBubbleLauncher
                        ? GetTargetAimPoint(spawnPos, leadMovingTarget: true)
                        : _playerTarget.position + Vector3.up * 1f;
                    Vector3 toTarget = (aimPoint - spawnPos).normalized;
                    // Small random offset for most projectiles; Bubblee needs exact path data.
                    if (!_cachedIsBubbleLauncher)
                        toTarget += Random.insideUnitSphere * 0.05f;
                    dir = toTarget.normalized;
                }
                Quaternion rot = Quaternion.LookRotation(dir);

                if (_cachedIsBubbleLauncher && TryFireBubbleLikePlayer(spawnPos, dir))
                {
                    Plugin.Log.LogInfo($"[{BotName}] Fired Bubblee through DualLauncher SpawnProjectile path");
                    return;
                }

                GameObject projObj = Object.Instantiate(_projectilePrefab.gameObject, spawnPos, rot);

                // Check if this is a self-moving projectile (PredictedProjectile, RebondBalle)
                // vs a NetworkBehaviour projectile (PhysicsGrenade, Obus, Bubble)
                bool hasNetObj = projObj.GetComponent<FishNet.Object.NetworkObject>() != null;
                var predictedProj = projObj.GetComponent<PredictedProjectile>();

                if (predictedProj != null)
                {
                    // Check if spawn point 5m ahead is clear — if wall in the way, don't fire
                    int envCheck = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9);
                    if (Physics.Raycast(spawnPos, dir, 6f, envCheck, QueryTriggerInteraction.Ignore))
                    {
                        // Wall within 6m — destroy the projectile, don't fire
                        Object.Destroy(projObj);
                        Plugin.Log.LogInfo($"[{BotName}] Cancelled rocket — wall within 6m");
                        return;
                    }

                    // Spawn 5m ahead to clear bot's body
                    projObj.transform.position = spawnPos + dir * 5f;

                    // Ignore collision between rocket and bot's own colliders
                    foreach (var botCol in GetComponentsInChildren<Collider>(true))
                    {
                        foreach (var projCol in projObj.GetComponentsInChildren<Collider>(true))
                            Physics.IgnoreCollision(botCol, projCol, true);
                    }

                    predictedProj.Initialize(dir, _launchForce, 0f, gameObject, _heldWeaponObj);
                    predictedProj.isOwner = true;
                    predictedProj.weapon = _heldWeapon;
                    Plugin.Log.LogInfo($"[{BotName}] Spawned PredictedProjectile 5m ahead, isOwner=true");

                    // Sync projectile visual + explosion to non-host clients
                    float explosionRadius = ReadFloatField(predictedProj, "explosionRadius", 1f);
                    BotDamageSync.SyncProjectile(spawnPos, dir, _launchForce, _heldWeaponObj != null ? _heldWeaponObj.name : "");
                    StartCoroutine(WatchProjectileExplosion(projObj, explosionRadius));
                }
                else
                {
                    // NetworkBehaviour projectile (Obus, PhysicsGrenade, Bubble, etc.)
                    //
                    // CRITICAL order of operations (this was wrong in an earlier version and
                    // caused Obus.KillShockWave NRE → HandleExplosion abort → no kill feed,
                    // no explosion VFX, no Obus destroy):
                    //
                    //   1) Resolve projComp + read fuse.
                    //   2) Neutralize BallisticPathFollow / Syncer so their FixedUpdate can't
                    //      NPE on a null path list (they're the "Shoot() rpc populates path"
                    //      system we don't call).
                    //   3) Wall raycast + push spawn 5m ahead + IgnoreCollision with bot.
                    //   4) SPAWN FIRST — Initialize is an ObserversRpc; RPCs require the
                    //      NetworkObject to be spawned or they silently no-op.
                    //   5) Invoke Initialize AFTER spawn, then SetField _rootObject/_gun/weapon
                    //      again as belt-and-braces (Initialize could have been vetoed).
                    //   6) Set isOwner=true on all MBs.
                    //   7) AddForce to launch.
                    Component projComp = projObj.GetComponent(_projectilePrefab.GetType());
                    if (projComp == null)
                        projComp = projObj.GetComponentInChildren(_projectilePrefab.GetType());

                    var physGrenade = projObj.GetComponent<PhysicsGrenade>();
                    bool isLaunchedGrenade = physGrenade != null;
                    bool useBallisticPathLaunch = projObj.GetComponent<Bubble>() != null;

                    // ---- (2) Neutralize BallisticPathFollow + Syncer ----
                    // DualLauncher grenade/obus/bubble prefabs use BallisticPathFollow +
                    // BallisticPathFollowSyncer. The game's Shoot() rpc feeds them a prediction
                    // path we don't have. Without it the syncer NPEs every FixedUpdate on
                    // Linq.Skip(). BallisticPathFollowSyncer has [RequireComponent] on
                    // BallisticPathFollow, so naive iteration can try to destroy the required
                    // component first and Unity logs "Can't remove BallisticPathFollow because
                    // BallisticPathFollowSyncer depends on it". Order: Syncer + TrickShot FIRST,
                    // then empty BallisticPathFollow.path + destroy it.
                    try
                    {
                        if (!useBallisticPathLaunch)
                        {
                            foreach (var mb in projObj.GetComponentsInChildren<MonoBehaviour>(true))
                            {
                                if (mb == null) continue;
                                string tn = mb.GetType().Name;
                                if (tn == "BallisticPathFollowSyncer" || tn == "TrickShot")
                                    Object.Destroy(mb);
                            }
                            foreach (var mb in projObj.GetComponentsInChildren<MonoBehaviour>(true))
                            {
                                if (mb == null) continue;
                                string tn = mb.GetType().Name;
                                if (tn == "BallisticPathFollow" || tn == "BallisticPathFollow2D")
                                {
                                    try
                                    {
                                        var pathField = mb.GetType().GetField("path",
                                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                        if (pathField != null)
                                        {
                                            var empty = System.Activator.CreateInstance(pathField.FieldType);
                                            pathField.SetValue(mb, empty);
                                        }
                                    }
                                    catch { }
                                    Object.Destroy(mb);
                                }
                            }
                        }
                    }
                    catch (System.Exception pathEx)
                    {
                        Plugin.Log.LogWarning($"[{BotName}] Ballistic path neutralize: {pathEx.Message}");
                    }

                    // ---- (3) Wall raycast + spawn offset + collider ignore ----
                    {
                        int envCheck = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9);
                        if (!useBallisticPathLaunch && Physics.Raycast(spawnPos, dir, 6f, envCheck, QueryTriggerInteraction.Ignore))
                        {
                            Object.Destroy(projObj);
                            Plugin.Log.LogInfo($"[{BotName}] Cancelled launched projectile — wall within 6m");
                            return;
                        }

                        projObj.transform.position = useBallisticPathLaunch
                            ? (_botCam != null ? _botCam.transform.position : spawnPos)
                            : spawnPos + dir * 5f;

                        foreach (var botCol in GetComponentsInChildren<Collider>(true))
                        {
                            foreach (var projCol in projObj.GetComponentsInChildren<Collider>(true))
                                if (botCol != null && projCol != null)
                                    Physics.IgnoreCollision(botCol, projCol, true);
                        }
                    }

                    // ---- (4) SPAWN FIRST ----
                    // Must happen before Initialize; Initialize is an ObserversRpc.
                    if (hasNetObj)
                    {
                        try
                        {
                            var hostConn = FishNet.InstanceFinder.ClientManager != null
                                ? FishNet.InstanceFinder.ClientManager.Connection : null;
                            if (hostConn != null)
                                FishNet.InstanceFinder.ServerManager.Spawn(projObj, hostConn);
                            else
                                FishNet.InstanceFinder.ServerManager.Spawn(projObj);
                        }
                        catch
                        {
                            FishNet.InstanceFinder.ServerManager.Spawn(projObj);
                        }
                    }

                    // ---- (5) Initialize + SetField AFTER spawn ----
                    if (projComp != null)
                    {
                        // PhysicsGrenade: Initialize(rootObject, gun, passedTime, grenadeOpenSince)
                        var pgInit = projComp.GetType().GetMethod("Initialize",
                            new[] { typeof(GameObject), typeof(GameObject), typeof(float), typeof(float) });
                        if (pgInit != null)
                        {
                            float grenadeTimer = 2f;
                            try
                            {
                                var fuseF = projComp.GetType().GetField("timeBeforeExplosion",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (fuseF != null) grenadeTimer = (float)fuseF.GetValue(projComp);
                            }
                            catch { }
                            try { pgInit.Invoke(projComp, new object[] { gameObject, _heldWeaponObj, 0f, grenadeTimer }); }
                            catch (System.Exception ie) { Plugin.Log.LogWarning($"[{BotName}] Init4 failed: {ie.Message}"); }
                        }
                        else
                        {
                            // HandGrenade-style (5 params) or Obus/Bubble-style (3 params)
                            var init5 = projComp.GetType().GetMethod("Initialize",
                                new[] { typeof(Vector3), typeof(float), typeof(float), typeof(GameObject), typeof(GameObject) });
                            if (init5 != null)
                            {
                                try { init5.Invoke(projComp, new object[] { dir, _launchForce, 0f, gameObject, _heldWeaponObj }); }
                                catch (System.Exception ie) { Plugin.Log.LogWarning($"[{BotName}] Init5 failed: {ie.Message}"); }
                            }
                            else
                            {
                                var init3 = projComp.GetType().GetMethod("Initialize",
                                    new[] { typeof(GameObject), typeof(GameObject), typeof(float) });
                                if (init3 != null)
                                {
                                    try { init3.Invoke(projComp, new object[] { gameObject, _heldWeaponObj, 0f }); }
                                    catch (System.Exception ie) { Plugin.Log.LogWarning($"[{BotName}] Init3 failed: {ie.Message}"); }
                                }
                            }
                        }

                        // Belt-and-braces: write the fields locally. If Initialize's RPC was
                        // vetoed or the RunLocally body didn't touch them, this ensures the
                        // server-side Obus instance that runs HandleExplosion + KillShockWave
                        // sees _rootObject = this bot → our KillShockWave prefix skips the NRE-prone
                        // original → HandleExplosion completes → SetKiller + VFX + destroy fire.
                        SetField(projComp, "weapon", _heldWeapon);
                        SetField(projComp, "_rootObject", gameObject);
                        SetField(projComp, "_gun", _heldWeaponObj);
                    }

                    // ---- (6) Set isOwner = true on all MBs ----
                    foreach (var mb in projObj.GetComponents<MonoBehaviour>())
                    {
                        var ownerField = mb.GetType().GetField("isOwner",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (ownerField != null && ownerField.FieldType == typeof(bool))
                            ownerField.SetValue(mb, true);
                    }

                    // Rigidbody launch for physics-based projectiles
                    var rb = projObj.GetComponent<Rigidbody>();
                    if (rb != null && !useBallisticPathLaunch)
                        rb.AddForce(dir * Mathf.Min(_launchForce, 50f), ForceMode.VelocityChange);

                    if (useBallisticPathLaunch)
                    {
                        bool pathStarted = TryLaunchBallisticPathProjectile(projObj, dir);
                        if (!pathStarted && rb != null)
                            rb.AddForce(dir * Mathf.Min(_launchForce, 50f), ForceMode.VelocityChange);
                        Plugin.Log.LogInfo($"[{BotName}] Bubblee ballistic path launch: {pathStarted}");
                    }

                    if (isLaunchedGrenade)
                    {
                        // Schedule a ForceGrenadeExplosion backup just like hand grenades —
                        // if the game's IsOwner-gated Update path fails to call ServerHandlerExplosion
                        // on the host bot, this still kills victims + spawns VFX + syncs to clients.
                        float fuseTime = 2f;
                        try
                        {
                            var fuseF = typeof(PhysicsGrenade).GetField("timeBeforeExplosion",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (fuseF != null) fuseTime = (float)fuseF.GetValue(physGrenade);
                        }
                        catch { }
                        string projectileWeaponName = _heldBehaviour != null ? _heldBehaviour.weaponName : (_heldWeaponObj != null ? _heldWeaponObj.name : "grenade");
                        foreach (var comp in projObj.GetComponents<MonoBehaviour>())
                            StraftatBots.BotPatches.RegisterBotProjectile(comp, this);
                        StartCoroutine(ForceGrenadeExplosion(projObj, fuseTime + 0.1f, projectileWeaponName));
                        Plugin.Log.LogInfo($"[{BotName}] Launched grenade (PhysicsGrenade), fuse={fuseTime}s, backup scheduled");
                    }
                    else
                    {
                        // Obus/Bubble — game's own explosion handles damage via isOwner=true (set above).
                        // Register at fire time so the kill feed can find the owner even if
                        // FishNet's NetworkInitializeEarly clears _rootObject on the projectile.
                        foreach (var comp in projObj.GetComponents<MonoBehaviour>())
                            StraftatBots.BotPatches.RegisterBotProjectile(comp, this);
                        Plugin.Log.LogInfo($"[{BotName}] Spawned NetworkBehaviour projectile, game handles explosion");
                    }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[{BotName}] Projectile fire error: {e.Message}");
            }
        }

        // ===================== PLACEABLE ITEMS =====================

        /// <summary>
        /// Spawn an armed claymore on the nearest wall, or proximity mine on the ground.
        /// Uses the weapon's objToSpawn prefab and FishNet spawns it like WeaponHandSpawner does.
        /// </summary>
        private void PlaceArmedItemAt(Vector3 placePos, Quaternion placeRot)
        {
            if (_heldWeaponObj == null) return;

            try
            {
                var spawner = _heldWeaponObj.GetComponent<WeaponHandSpawner>();
                if (spawner == null)
                {
                    DropWeapon();
                    return;
                }

                var prefabField = typeof(WeaponHandSpawner).GetField("objToSpawn",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                GameObject prefab = prefabField?.GetValue(spawner) as GameObject;
                if (prefab == null)
                {
                    DropWeapon();
                    return;
                }

                // Spawn with host ownership so IsOwner checks pass in HandleExplosion
                GameObject spawned = Object.Instantiate(prefab, placePos, placeRot);
                FishNet.InstanceFinder.ServerManager.Spawn(spawned, FishNet.InstanceFinder.ClientManager.Connection);

                // Set rootObject and weapon reference (same as game)
                bool isClaymore = ReadBoolField(spawner, "claymore");
                bool isMine = ReadBoolField(spawner, "proximityMine") || ReadBoolField(spawner, "apmine");

                if (isMine)
                {
                    var pm = spawned.GetComponent<ProximityMine>();
                    if (pm != null)
                    {
                        pm._rootObject = gameObject;
                        SetField(pm, "weapon", spawner);
                    }
                }
                else if (isClaymore)
                {
                    var cl = spawned.GetComponent<Claymore>();
                    if (cl != null)
                    {
                        cl._rootObject = gameObject;
                        SetField(cl, "weapon", spawner);
                    }
                }

                Plugin.Log.LogInfo($"[{BotName}] Placed {prefab.name} at {placePos} (ammo left: {(_heldWeapon != null ? _heldWeapon.currentAmmo - 1 : 0)})");
                _placedClaymorePositions.Add((placePos, Time.time));

                // Blacklist nearby nodes so this bot avoids its own mines
                if (NavGraph.Instance != null)
                    NavGraph.Instance.BlacklistNearby(placePos, 4f);

                // Consume ammo
                if (_heldWeapon != null && _heldWeapon.needsAmmo)
                    _heldWeapon.currentAmmo--;

                // Only drop weapon when out of ammo
                if (_heldWeapon == null || _heldWeapon.currentAmmo <= 0)
                    DropWeapon();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[{BotName}] Place item error: {e.Message}");
                DropWeapon();
            }
        }

        // ===================== GRENADE THROWING =====================

        /// <summary>
        /// Throw a live grenade toward the target player.
        /// </summary>
        private void ThrowGrenade()
        {
            if (_heldWeaponObj == null || _playerTarget == null) return;

            try
            {
                Vector3 origin = transform.position + Vector3.up * 1.5f;
                Vector3 targetPos = _playerTarget.position + Vector3.up * 1f;
                Vector3 dir = (targetPos - origin).normalized;
                float throwForce = 12f;

                // Try to find projectile prefab from DualLauncher or weapon fields
                GameObject prefab = null;
                Component prefabComp = null;

                // Check DualLauncher's trickShot.template — read from ORIGINAL, not clone
                GameObject grenadeSource = _weaponSource != null ? _weaponSource : _heldWeaponObj;
                var dualLauncher = grenadeSource.GetComponent<DualLauncher>();
                if (dualLauncher != null)
                {
                    var trickField = typeof(DualLauncher).GetField("trickShot",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (trickField != null)
                    {
                        var trickShot = trickField.GetValue(dualLauncher);
                        if (trickShot != null)
                        {
                            var templateField = trickShot.GetType().GetField("template",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (templateField != null)
                            {
                                var template = templateField.GetValue(trickShot) as Component;
                                if (template != null) { prefabComp = template; prefab = template.gameObject; }
                            }
                        }
                    }
                    var forceField = typeof(DualLauncher).GetField("launchForce",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (forceField != null) throwForce = (float)forceField.GetValue(dualLauncher);
                }

                // Fallback: check _projectile field (used by FireProjectile)
                if (prefab == null && _isProjectileWeapon && _projectilePrefab != null)
                {
                    prefab = _projectilePrefab.gameObject;
                    prefabComp = _projectilePrefab;
                    throwForce = _launchForce;
                }

                if (prefab == null)
                {
                    Plugin.Log.LogWarning($"[{BotName}] No grenade prefab found");
                    DropWeapon();
                    return;
                }

                // DualLauncher-sourced grenades have a BallisticPathFollowSyncer and spawn
                // expecting to be launched forward. If we spawn at bot's head, the kinematic
                // colliders overlap the bot and the grenade detonates on the bot on fuse timeout.
                // Wall-check + offset forward 5m so the grenade leaves the bot cleanly.
                bool isDualLauncherSourced = dualLauncher != null;
                if (isDualLauncherSourced)
                {
                    int envCheck = (1 << 0) | (1 << 1) | (1 << 2) | (1 << 4) | (1 << 8) | (1 << 9);
                    if (Physics.Raycast(origin, dir, 6f, envCheck, QueryTriggerInteraction.Ignore))
                    {
                        Plugin.Log.LogInfo($"[{BotName}] Cancelled grenade throw — wall within 6m");
                        DropWeapon();
                        return;
                    }
                    origin = origin + dir * 5f;
                }

                // Instantiate grenade
                Quaternion rot = Quaternion.LookRotation(dir);
                GameObject grenadeObj = Object.Instantiate(prefab, origin, rot);

                // Ignore collision between bot and grenade colliders so the grenade can't damage
                // the bot from the OverlapSphere on detonation (belt-and-suspenders with 5m offset).
                foreach (var botCol in GetComponentsInChildren<Collider>(true))
                {
                    foreach (var gCol in grenadeObj.GetComponentsInChildren<Collider>(true))
                        Physics.IgnoreCollision(botCol, gCol, true);
                }

                // FishNet spawn if it's a NetworkBehaviour — MUST pass host's connection as
                // owner, otherwise base.IsOwner is false in Update() and ExplodeServer never
                // fires. Matches DualLauncher.SpawnProjectile player-side code.
                var nob = grenadeObj.GetComponent<FishNet.Object.NetworkObject>();
                if (nob != null)
                    FishNet.InstanceFinder.ServerManager.Spawn(grenadeObj, FishNet.InstanceFinder.ClientManager.Connection);

                // Set isOwner = true on projectiles that check it (Obus, Bubble)
                // Without this, OnCollisionEnter and Update timer checks fail
                foreach (var mb in grenadeObj.GetComponents<MonoBehaviour>())
                {
                    var ownerField = mb.GetType().GetField("isOwner",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (ownerField != null && ownerField.FieldType == typeof(bool))
                        ownerField.SetValue(mb, true);
                }

                // Initialize and force-explode ALL throwable types
                // Every type has an IsOwner check that fails for bot-spawned projectiles
                float fuseTime = 3f;

                var physGrenade = grenadeObj.GetComponent<PhysicsGrenade>();
                if (physGrenade != null)
                {
                    // Read fuse FIRST so we can pass it as grenadeOpenSince (4th arg).
                    // In PhysicsGrenade.RpcLogic___Initialize, explosionTimer = grenadeOpenSince,
                    // so passing 0 makes it explode instantly (but only if base.IsOwner, which
                    // used to be false — that's why it just sat there forever).
                    try
                    {
                        var f = typeof(PhysicsGrenade).GetField("timeBeforeExplosion",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (f != null) fuseTime = (float)f.GetValue(physGrenade);
                    }
                    catch { }
                    physGrenade.Initialize(gameObject, _heldWeaponObj, 0f, fuseTime);
                    SetField(physGrenade, "_rootObject", gameObject);
                    SetField(physGrenade, "_gun", _heldWeaponObj);
                    SetField(physGrenade, "explosionTimer", fuseTime);
                    SetField(physGrenade, "exploded", false);
                }

                var handGrenade = grenadeObj.GetComponent<HandGrenade>();
                if (handGrenade != null)
                {
                    var init = typeof(HandGrenade).GetMethod("Initialize",
                        new[] { typeof(Vector3), typeof(float), typeof(float), typeof(GameObject), typeof(GameObject) });
                    if (init != null)
                        init.Invoke(handGrenade, new object[] { dir, throwForce, 0f, gameObject, _heldWeaponObj });
                    try
                    {
                        var f = typeof(HandGrenade).GetField("timeBeforeExplosion",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (f != null) fuseTime = (float)f.GetValue(handGrenade);
                    }
                    catch { }
                }

                var handGrenade2 = grenadeObj.GetComponent<HandGrenadeTwo>();
                if (handGrenade2 != null)
                {
                    var init = typeof(HandGrenadeTwo).GetMethod("Initialize",
                        new[] { typeof(Vector3), typeof(float), typeof(float), typeof(GameObject), typeof(GameObject) });
                    if (init != null)
                        init.Invoke(handGrenade2, new object[] { dir, throwForce, 0f, gameObject, _heldWeaponObj });
                    try
                    {
                        var f = typeof(HandGrenadeTwo).GetField("timeBeforeExplosion",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (f != null) fuseTime = (float)f.GetValue(handGrenade2);
                    }
                    catch { }
                }

                // Strip BallisticPathFollowSyncer — if the prefab came from DualLauncher.trickShot.template
                // it has this component expecting Shoot(trickShotData, id) to seed its prediction list.
                // We move via Rigidbody instead, so without stripping, its FixedUpdate NREs every
                // physics frame on Linq.Skip(null). That also stops the grenade from updating normally.
                StripBallisticPathSyncer(grenadeObj);

                // Add launch force
                var rb = grenadeObj.GetComponent<Rigidbody>();
                if (rb != null)
                    rb.AddForce(dir * throwForce, ForceMode.Impulse);

                string grenadeWeaponName = _heldBehaviour != null ? _heldBehaviour.weaponName : prefab.name;
                foreach (var comp in grenadeObj.GetComponents<MonoBehaviour>())
                    StraftatBots.BotPatches.RegisterBotProjectile(comp, this);

                // Force-explode after fuse as backup — game's HandleExplosion NREs on bot data
                // and the finalizer swallows it, so Destroy+VFX never run
                StartCoroutine(ForceGrenadeExplosion(grenadeObj, fuseTime + 0.1f, grenadeWeaponName));

                Plugin.Log.LogInfo($"[{BotName}] Threw grenade toward {_playerTarget.name}, fuse={fuseTime}s");

                // Destroy the held weapon (consumed on throw)
                DestroyHeldWeapon();
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[{BotName}] Grenade throw error: {e.Message}");
                DropWeapon();
            }
        }

        /// <summary>
        /// Strip BallisticPathFollowSyncer from a bot-spawned grenade. The game's normal fire
        /// path calls `.Shoot(trickShotData, id)` on this component to populate the prediction
        /// IEnumerable — we bypass that because the bot uses Rigidbody.AddForce to move the
        /// grenade. Without stripping, the syncer's FixedUpdate runs Linq.Skip on a null
        /// source and throws ArgumentNullException every physics frame, which floods logs and
        /// prevents the grenade from moving normally.
        /// </summary>
        private static void StripBallisticPathSyncer(GameObject obj)
        {
            if (obj == null) return;
            try
            {
                foreach (var mb in obj.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null) continue;
                    var tName = mb.GetType().Name;
                    if (tName == "BallisticPathFollowSyncer" || tName.Contains("BallisticPath") || tName.Contains("PathFollow"))
                    {
                        // IMPORTANT: disable FIRST. Object.Destroy is deferred to end of frame,
                        // so FixedUpdate can still fire once or more before the component is
                        // actually gone. Setting enabled=false stops FixedUpdate immediately.
                        try { mb.enabled = false; } catch { }

                        // Also try to seed the prediction list via reflection so if the component
                        // survives (or the Destroy is blocked by FishNet's sync requirements),
                        // FixedUpdate has a non-null IEnumerable to call Skip() on.
                        try
                        {
                            foreach (var f in mb.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                            {
                                if (f.FieldType == typeof(List<Vector3>) && f.GetValue(mb) == null)
                                    f.SetValue(mb, new List<Vector3>());
                                else if (typeof(System.Collections.IList).IsAssignableFrom(f.FieldType) && f.GetValue(mb) == null)
                                {
                                    try { f.SetValue(mb, System.Activator.CreateInstance(f.FieldType)); } catch { }
                                }
                            }
                        }
                        catch { }

                        Object.Destroy(mb);
                    }
                }
            }
            catch { }
        }

        private bool TryFireBubbleLikePlayer(Vector3 spawnPos, Vector3 dir)
        {
            try
            {
                var dl = _heldWeaponObj != null ? _heldWeaponObj.GetComponent<DualLauncher>() : null;
                if (dl == null || !ReadBoolField(dl, "bubble")) return false;
                try { dl.rootObject = gameObject; } catch { }
                try { if (_heldWeapon != null) _heldWeapon.rootObject = gameObject; } catch { }

                var spawnProjectile = typeof(DualLauncher).GetMethod("SpawnProjectile",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (spawnProjectile == null) return false;

                var parameters = spawnProjectile.GetParameters();
                if (parameters.Length != 6) return false;
                if (!TryBuildTrickShotData(parameters[3].ParameterType, dir, out object data))
                    return false;

                object owner = null;
                try
                {
                    owner = FishNet.InstanceFinder.ClientManager != null
                        ? FishNet.InstanceFinder.ClientManager.Connection
                        : null;
                }
                catch { }

                int id = UnityEngine.Random.Range(1, int.MaxValue);
                spawnProjectile.Invoke(dl, new object[] { spawnPos, dir, 0f, data, owner, id });
                return true;
            }
            catch (System.Exception e)
            {
                var tie = e as TargetInvocationException;
                Plugin.Log.LogWarning($"[{BotName}] Bubblee player-style fire failed: {(tie?.InnerException ?? e).Message}");
                return false;
            }
        }

        private bool TryLaunchBallisticPathProjectile(GameObject projObj, Vector3 dir)
        {
            if (projObj == null) return false;
            try
            {
                MonoBehaviour syncer = null;
                foreach (var mb in projObj.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb != null && mb.GetType().Name == "BallisticPathFollowSyncer")
                    {
                        syncer = mb;
                        break;
                    }
                }
                if (syncer == null) return false;

                var shootMethod = FindShootMethod(syncer.GetType());
                if (shootMethod == null) return false;

                if (!TryBuildTrickShotData(shootMethod.GetParameters()[0].ParameterType, dir, out object data))
                    return false;

                int id = UnityEngine.Random.Range(1, int.MaxValue);
                shootMethod.Invoke(syncer, new object[] { data, id });
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[{BotName}] Bubblee path launch failed: {e.Message}");
                return false;
            }
        }

        private bool TryBuildTrickShotData(System.Type dataType, Vector3 dir, out object data)
        {
            data = null;
            try
            {
                object trickShot = GetCurrentDualLauncherTrickShot();
                if (trickShot == null || dataType == null) return false;

                Transform aim = _botCam != null ? _botCam.transform : transform;
                Quaternion oldRot = aim.rotation;
                try
                {
                    if (dir.sqrMagnitude > 0.001f)
                        aim.rotation = Quaternion.LookRotation(dir);

                    var selfField = trickShot.GetType().GetField("selfTransform",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (selfField != null) selfField.SetValue(trickShot, aim);

                    var predict = trickShot.GetType().GetMethod("Predict",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    predict?.Invoke(trickShot, null);
                }
                finally
                {
                    aim.rotation = oldRot;
                }

                float speed = ReadFloatField(trickShot, "speed", _launchForce);
                if (_cachedIsBubbleLauncher)
                    speed *= 1.75f;
                float radius = ReadFloatField(trickShot, "radius", 0.25f);
                object prediction = GetCachedField(trickShot.GetType(), "prediction")?.GetValue(trickShot);

                data = System.Activator.CreateInstance(dataType);
                dataType.GetField("forward")?.SetValue(data, dir);
                dataType.GetField("speed")?.SetValue(data, speed);
                dataType.GetField("radius")?.SetValue(data, radius);

                var predictionField = dataType.GetField("prediction");
                if (predictionField == null) return false;
                System.Array predictionArray = ToTypedArray(prediction, predictionField.FieldType);
                if (predictionArray == null || predictionArray.Length == 0) return false;
                predictionField.SetValue(data, predictionArray);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[{BotName}] TrickShot data build failed: {e.Message}");
                data = null;
                return false;
            }
        }

        private object GetCurrentDualLauncherTrickShot()
        {
            var dl = _heldWeaponObj != null ? _heldWeaponObj.GetComponent<DualLauncher>() : null;
            var source = _weaponSource != null ? _weaponSource.GetComponent<DualLauncher>() : null;
            foreach (var candidate in new[] { dl, source })
            {
                if (candidate == null) continue;
                var trickField = typeof(DualLauncher).GetField("trickShot",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                object trickShot = trickField != null ? trickField.GetValue(candidate) : null;
                if (trickShot != null) return trickShot;
            }
            return null;
        }

        private static MethodInfo FindShootMethod(System.Type type)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            for (int i = 0; i < methods.Length; i++)
            {
                var ps = methods[i].GetParameters();
                if (methods[i].Name == "Shoot" && ps.Length == 2 && ps[1].ParameterType == typeof(int))
                    return methods[i];
            }
            return null;
        }

        private static System.Array ToTypedArray(object source, System.Type arrayType)
        {
            if (arrayType == null || !arrayType.IsArray) return null;
            var elementType = arrayType.GetElementType();
            if (elementType == null) return null;
            if (source is System.Array sourceArray)
            {
                var result = System.Array.CreateInstance(elementType, sourceArray.Length);
                for (int i = 0; i < sourceArray.Length; i++)
                    result.SetValue(sourceArray.GetValue(i), i);
                return result;
            }
            if (source is System.Collections.IList list)
            {
                var result = System.Array.CreateInstance(elementType, list.Count);
                for (int i = 0; i < list.Count; i++)
                    result.SetValue(list[i], i);
                return result;
            }
            return null;
        }

        /// <summary>
        /// Backup explosion for bot-thrown grenades. The game's HandleExplosion NREs on bot data
        /// (Settings.Instance calls, _rootObject checks) and the Explosion_Finalizer swallows
        /// the exception — so Destroy() and VFX instantiation never run. This coroutine waits
        /// for the fuse and forces the explosion if the grenade still exists.
        /// </summary>
        private System.Collections.IEnumerator UnparentAfterDelay(Transform t, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (t != null) t.SetParent(null);
        }

        private System.Collections.IEnumerator ForceGrenadeExplosion(GameObject grenadeObj, float delay, string fallbackWeaponName)
        {
            yield return new WaitForSeconds(delay);
            if (grenadeObj == null) yield break; // Already exploded and cleaned up normally

            bool isStunGrenade = false;
            float stunTime = 3f;
            Component grenade = null;

            try
            {
                var hg = grenadeObj.GetComponent<HandGrenade>();
                var hg2 = grenadeObj.GetComponent<HandGrenadeTwo>();
                var pg = grenadeObj.GetComponent<PhysicsGrenade>();
                grenade = (Component)hg ?? (Component)hg2 ?? (Component)pg;
                if (pg != null)
                {
                    isStunGrenade = ReadBoolField(pg, "stunGrenade");
                    stunTime = ReadFloatField(pg, "stunTime", stunTime);
                }
            }
            catch { }

            Vector3 pos = grenadeObj.transform.position;
            float explosionRadius = 3f;
            float damage = 10f;
            float ragdollForce = 30f;
            string weaponName = string.IsNullOrWhiteSpace(fallbackWeaponName) ? "grenade" : fallbackWeaponName;

            try
            {
                if (grenade != null)
                {
                    var radiusField = grenade.GetType().GetField("explosionRadius",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (radiusField != null) explosionRadius = (float)radiusField.GetValue(grenade);
                    var damageField = grenade.GetType().GetField("damage",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (damageField != null) damage = (float)damageField.GetValue(grenade);
                    var forceField = grenade.GetType().GetField("ragdollEjectForce",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (forceField != null) ragdollForce = (float)forceField.GetValue(grenade);
                    if (grenade is MonoBehaviour mb)
                        weaponName = StraftatBots.BotPatches.ResolveExplosiveWeaponName(mb);
                }
            }
            catch { }

            Plugin.Log.LogInfo($"[{BotName}] ForceGrenadeExplosion at {pos}, radius={explosionRadius}");

            int bodyLayer = (1 << 11);
            Collider[] hits = Physics.OverlapSphere(pos, explosionRadius, bodyLayer);
            var handled = new HashSet<PlayerHealth>();
            foreach (var col in hits)
            {
                PlayerHealth ph = col.GetComponentInParent<PlayerHealth>();
                if (ph == null || ph.isKilled || handled.Contains(ph)) continue;
                handled.Add(ph);

                if (isStunGrenade)
                {
                    try { ph.TaserEnemy(ph, stunTime); } catch { }
                    continue;
                }

                try { ph.SetKiller(transform); } catch { ph.killer = transform; }
                float previousHealth = ph.health;
                try { ph.RemoveHealth(damage); } catch { ph.health -= damage; }
                if (previousHealth - damage > 0f && ph.health > 0f) continue;

                ph.isKilled = true;
                ph.isShot = true;
                Vector3 killDir = ph.transform.position - pos;
                if (killDir.sqrMagnitude < 0.001f) killDir = transform.forward;
                killDir.Normalize();

                try { ph.ExplodeServer(false, true, ph.gameObject.name, killDir, ragdollForce, pos); } catch { }
                try { ph.ChangeKilledState(true); } catch { }
                try { Settings.Instance.IncreaseKillsAmount(); } catch { }
                try { BotKillFeed.Write(ph, gameObject, BotName, weaponName, "killed", true); } catch { }

                var victimBot = ph.GetComponent<BotController>();
                if (victimBot != null && !victimBot.IsDead)
                {
                    victimBot.Die(transform);
                }
                else
                {
                    try
                    {
                        int pid = ph.playerValues.playerClient.PlayerId;
                        BotDamageSync.SyncKill(pid, BotName, weaponName, false,
                            killDir, ragdollForce, pos, ph.gameObject.name);
                    }
                    catch { }
                }
            }

            // Spawn VFX on host — try grenade's own prefab first, then fallback to generic
            GameObject vfxPrefab = null;
            AudioClip explosionClip = null;
            try
            {
                foreach (var mb in grenadeObj.GetComponents<MonoBehaviour>())
                {
                    if (vfxPrefab != null) break;
                    var vfxField = mb.GetType().GetField("explosionVfx",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (vfxField != null)
                        vfxPrefab = vfxField.GetValue(mb) as GameObject;
                    var clipField = mb.GetType().GetField("explosionClip",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (clipField != null)
                        explosionClip = clipField.GetValue(mb) as AudioClip;
                }
            }
            catch { }

            if (vfxPrefab == null)
            {
                try
                {
                    foreach (var w in Object.FindObjectsOfType<Weapon>(true))
                    {
                        if (w.genericImpact != null) { vfxPrefab = w.genericImpact; break; }
                    }
                }
                catch { }
            }

            if (vfxPrefab != null)
            {
                var fx = Object.Instantiate(vfxPrefab, pos, Quaternion.identity);
                Object.Destroy(fx, 5f);
            }
            if (explosionClip != null)
                AudioSource.PlayClipAtPoint(explosionClip, pos);

            BotDamageSync.SyncExplosion(pos, explosionRadius);

            var nob = grenadeObj.GetComponent<FishNet.Object.NetworkObject>();
            if (nob != null && nob.IsSpawned)
                FishNet.InstanceFinder.ServerManager.Despawn(grenadeObj);
            else
                Object.Destroy(grenadeObj);
        }

        /// <summary>
        /// Watch a PredictedProjectile and sync its explosion to non-host clients when it hits.
        /// </summary>
        private System.Collections.IEnumerator WatchProjectileExplosion(GameObject projectile, float radius)
        {
            Vector3 lastPos = projectile != null ? projectile.transform.position : Vector3.zero;
            float timeout = 10f;

            while (projectile != null && timeout > 0f)
            {
                lastPos = projectile.transform.position;
                timeout -= Time.deltaTime;
                yield return null;
            }

            // Projectile destroyed (hit something) — sync explosion at last known position
            if (timeout > 0f) // Only if it didn't time out
            {
                BotDamageSync.SyncExplosion(lastPos, radius);
            }
        }

        private void FireRepulsiveGun(Vector3 origin, Vector3 dir)
        {
            var repulsive = _heldWeaponObj != null ? _heldWeaponObj.GetComponent<RepulsiveGun>() : null;
            if (repulsive == null || dir.sqrMagnitude < 0.001f) return;

            dir.Normalize();

            float repulseForce = ReadFloatField(repulsive, "repulseForce", 3f);
            float playerKnockback = ReadFloatField(repulsive, "playerKnockback", 0f);
            Vector3 boxDims = ReadVector3Field(repulsive, "boxdimensions", new Vector3(1f, 1f, 5f));
            if (boxDims.sqrMagnitude < 0.001f) boxDims = new Vector3(1f, 1f, 5f);

            if (Mathf.Abs(playerKnockback) > 0.001f)
                ApplyRepulsorImpulse(this, -dir, playerKnockback);

            Vector3 boxCenter = origin + dir * (boxDims.z / 2f);
            Quaternion boxRot = Quaternion.LookRotation(dir);
            int playerMask = _heldWeapon != null ? _heldWeapon.playerLayer.value : 0;
            Collider[] hits = playerMask != 0
                ? Physics.OverlapBox(boxCenter, boxDims, boxRot, playerMask, QueryTriggerInteraction.Collide)
                : Physics.OverlapBox(boxCenter, boxDims, boxRot, -1, QueryTriggerInteraction.Collide);

            PlayerHealth enemyHealth = null;
            BotController enemyBot = null;
            foreach (var hit in hits)
            {
                if (hit == null) continue;
                var ph = hit.GetComponentInParent<PlayerHealth>();
                if (ph == null || ph.isKilled) continue;
                if (ph.gameObject == gameObject || ph.transform.root == transform) continue;

                var hitBot = ph.GetComponent<BotController>();
                bool targetIsBot = hitBot != null && hitBot != this;
                if (targetIsBot && !AllHumansDead()) continue;

                enemyHealth = ph;
                enemyBot = hitBot;
                break;
            }

            if (enemyHealth == null) return;

            Vector3 bumpDir = dir + Vector3.up * 2f;
            if (enemyBot != null)
                ApplyRepulsorImpulse(enemyBot, bumpDir, repulseForce);
            else
                BumpHumanPlayer(repulsive, enemyHealth, bumpDir, repulseForce);

            _lastHitTime = Time.time;
            _combatStaleTimer = 0f;
            Plugin.Log.LogInfo($"[{BotName}] Repulsor bumped {(enemyBot != null ? enemyBot.BotName : "player")} force={repulseForce}");
        }

        private void ApplyRepulsorImpulse(BotController bot, Vector3 direction, float force)
        {
            if (bot == null || bot.IsDead || direction.sqrMagnitude < 0.001f) return;
            bot.ApplyZoneImpulse(direction.normalized * Mathf.Max(0f, force / 3f));
        }

        private void BumpHumanPlayer(RepulsiveGun repulsive, PlayerHealth ph, Vector3 direction, float force)
        {
            if (ph == null || ph.isKilled) return;
            try
            {
                var bumpTarget = repulsive.GetType().GetMethod("BumpPlayer",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var pv = ph.playerValues;
                var pc = pv != null ? pv.playerClient : null;
                var no = pc != null ? pc.transform.GetComponent<FishNet.Object.NetworkObject>() : null;
                if (bumpTarget != null && no != null)
                {
                    bumpTarget.Invoke(repulsive, new object[] { no.Owner, ph, force, direction });
                    return;
                }
            }
            catch { }

            try
            {
                ph.bounceDirection = direction;
                ph.bounceForce = force;
                ph.shouldBounce = true;
                ph.BumpPlayer(direction, force);
            }
            catch { }
        }

        private void FireRaycast()
        {
            // === SPREAD CALCULATION (matches game's exact per-state formula) ===
            float moveSpeed = _cc != null ? Mathf.Sqrt(_cc.velocity.x * _cc.velocity.x + _cc.velocity.z * _cc.velocity.z) : 0f;
            bool grounded = _cc != null && _cc.isGrounded;
            float accuracySpread;

            if (!grounded || moveSpeed > _sprintSpeed * 0.7f)
                accuracySpread = Mathf.Lerp(_heldWeapon.maxSpread, _heldWeapon.minSpread, _heldWeapon.sprintAccuracy);
            else if (_isCrouching)
                accuracySpread = Mathf.Lerp(_heldWeapon.maxSpread, _heldWeapon.minSpread, _heldWeapon.standingAccuracy);
            else if (moveSpeed > _walkSpeed * 0.3f)
                accuracySpread = Mathf.Lerp(_heldWeapon.maxSpread, _heldWeapon.minSpread, _heldWeapon.walkAccuracy);
            else
                accuracySpread = Mathf.Lerp(_heldWeapon.maxSpread, _heldWeapon.minSpread, _heldWeapon.standingAccuracy);

            // Recoil bloom — builds with sustained fire like a human's aim drifting during spray
            float recoilSpread = _recoilAccumulated * 0.02f;
            // First-shot bonus — if resting, first shot is tighter
            if (_shotsSinceRest < 1f) recoilSpread = 0f;

            // Bot-specific inaccuracy (aim lock-on ramp)
            float botSpread = _aimInaccuracy * 0.02f * Mathf.Lerp(1.5f, 0.3f, _aimSmoothing);

            float spread = accuracySpread + recoilSpread + botSpread;

            Vector3 baseDir = _botCam.transform.forward;
            Vector3 origin = _botCam.transform.position;

            if (_cachedIsRepulsive)
            {
                FireRepulsiveGun(origin, baseDir.normalized);
                return;
            }

            // === SHOTGUN PELLETS ===
            int pellets = 1;
            float pelletSpread = 0f;
            Shotgun sg = _cachedShotgun;
            if (sg != null)
            {
                pellets = ReadIntField(_cachedShotgun, "bulletAmount", 8);
                pelletSpread = ReadFloatField(_cachedShotgun, "spread", 0.1f);
                // Shotgun accuracy spread is applied to the BASE direction,
                // pellet spread is applied per-pellet ON TOP of that
            }

            Vector3 shootOrigin = _heldWeapon.shootPoint != null
                ? _heldWeapon.shootPoint.position : origin;

            // Boxcast weapons (BlankState etc.) — wide area hit detection like the game does
            var lrg = _cachedLargeRaycast;
            bool isBoxcast = lrg != null && ReadBoolField(lrg, "boxcast");
            if (isBoxcast)
            {
                Vector3 boxDims = new Vector3(1f, 0.7f, 16f);
                try
                {
                    var boxField = typeof(LargeRaycastGun).GetField("boxdimensions",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (boxField != null) boxDims = (Vector3)boxField.GetValue(lrg);
                }
                catch { }

                // Aim directly at target for wide-beam weapons (box is forgiving)
                Vector3 boxDir = baseDir;
                if (_playerTarget != null)
                {
                    Vector3 toTarget = (_playerTarget.position + Vector3.up * 1f - origin).normalized;
                    boxDir = toTarget;
                }
                Vector3 boxCenter = origin + boxDir * (boxDims.z / 2f);
                Collider[] boxHits = Physics.OverlapBox(boxCenter, boxDims, Quaternion.LookRotation(boxDir));
                Plugin.Log.LogInfo($"[{BotName}] Boxcast: origin={origin} dir={boxDir} dims={boxDims} center={boxCenter} hits={boxHits.Length} target={(_playerTarget != null ? _playerTarget.position.ToString() : "null")}");

                foreach (var bh in boxHits)
                {
                    PlayerHealth ph = bh.GetComponentInParent<PlayerHealth>();
                    if (ph == null || ph.isKilled) continue;
                    if (ph.transform.root == transform) continue; // Don't hit self

                    BotController hitBot = ph.GetComponent<BotController>();
                    bool targetIsBot = hitBot != null && hitBot != this;
                    if (targetIsBot && !AllHumansDead()) continue;

                    float dmg = _heldWeapon.damage;
                    bool headshot = bh.name.Contains("Head") || bh.name.Contains("Neck");
                    if (headshot) dmg *= _heldWeapon.headMultiplier;

                    ph.RemoveHealth(dmg);
                    ph.SetKiller(transform);
                    _lastHitTime = Time.time;
                    _combatStaleTimer = 0f;

                    if (ph.health <= 0f)
                    {
                        ph.ChangeKilledState(true);
                        ph.isShot = true;

                        if (hitBot != null && !hitBot.IsDead)
                        {
                            try { ph.ExplodeServer(false, true, bh.gameObject.name, boxDir, _heldWeapon.ragdollEjectForce, bh.transform.position); } catch { }
                            DisableBotPhysics(ph.gameObject);
                            try { ph.DisablePlayerObjectWhenKilled(); } catch { }
                            hitBot.Die(transform);
                        }
                        else
                        {
                            try
                            {
                                int pid = ph.playerValues.playerClient.PlayerId;
                                string wpn = _heldBehaviour != null ? _heldBehaviour.weaponName : "weapon";
                                BotDamageSync.SyncKill(pid, BotName, wpn, headshot,
                                    boxDir, _heldWeapon.ragdollEjectForce, bh.transform.position, bh.gameObject.name);
                            }
                            catch { }
                        }
                        RegisterKill(ph, headshot);
                    }
                    break; // Box weapons hit one target
                }

                // Bullet trail for boxcast
                try
                {
                    Vector3 trailEnd = origin + boxDir * boxDims.z;
                    var trailMethod = lrg.GetType().GetMethod("SpawnBulletTrailServer",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (trailMethod != null) trailMethod.Invoke(lrg, new object[] { trailEnd });
                    BotDamageSync.SyncBulletTrail(shootOrigin, trailEnd);
                }
                catch { }

                return; // Skip normal raycast path
            }

            // Apply accuracy spread to base direction (shared for all pellets)
            Vector3 spreadBaseDir = baseDir
                + _botCam.transform.right * Random.Range(-spread, spread)
                + _botCam.transform.up * Random.Range(-spread, spread);

            for (int p = 0; p < pellets; p++)
            {
                // Per-pellet scatter (shotgun only — each pellet deviates from the shared spread direction)
                Vector3 dir;
                if (pellets > 1)
                    dir = spreadBaseDir
                        + _botCam.transform.right * Random.Range(-pelletSpread, pelletSpread)
                        + _botCam.transform.up * Random.Range(-pelletSpread, pelletSpread);
                else
                    dir = spreadBaseDir;
                dir.Normalize();

                // Raycast on environment + player + glass layers (19=environment interaction/glass)
                int allMask = (1 << 0) | (1 << 3) | (1 << 10) | (1 << 11) | (1 << 16) | (1 << 19);
                Vector3 hitPoint = origin + dir * 200f; // default far endpoint
                bool didHitAnything = Physics.Raycast(origin, dir, out RaycastHit hit, 200f, allMask, QueryTriggerInteraction.Collide);

                if (didHitAnything)
                {
                    hitPoint = hit.point;

                    if (hit.collider.transform.root != transform)
                    {
                        // Check if target is a bot — skip damage if humans still alive
                        BotController hitBot = hit.collider.GetComponentInParent<BotController>();
                        bool targetIsBot = hitBot != null && hitBot != this;
                        bool canDamage = !targetIsBot || AllHumansDead();

                        PlayerHealth ph = canDamage ? hit.collider.GetComponentInParent<PlayerHealth>() : null;
                        if (ph != null && !ph.isKilled)
                        {
                            float dmg = _heldWeapon.damage;
                            bool headshot = hit.collider.name.Contains("Head") || hit.collider.name.Contains("Neck");
                            if (headshot) dmg *= _heldWeapon.headMultiplier;

                            // Use game's RPCs so SyncVars properly sync to victim's client
                            ph.RemoveHealth(dmg);
                            ph.SetKiller(transform);

                            // Spawn body impact effect — broadcast via ObserversRpc
                            try
                            {
                                GameObject impactPrefab = headshot ? _heldWeapon.headImpact : _heldWeapon.bodyImpact;
                                if (impactPrefab == null) impactPrefab = _heldWeapon.genericBodyImpact;
                                Quaternion impactRot = Quaternion.LookRotation(hit.normal);
                                if (impactPrefab != null)
                                {
                                    var fx = Object.Instantiate(impactPrefab, hit.point, impactRot);
                                    Object.Destroy(fx, 2f);
                                }
                                if (_heldWeapon.bloodSplatter != null)
                                {
                                    var blood = Object.Instantiate(_heldWeapon.bloodSplatter, hit.point, impactRot);
                                    Object.Destroy(blood, 2f);
                                }
                            }
                            catch { }

                            if (ph.health <= 0f)
                            {
                                BotController victimBot = ph.GetComponent<BotController>();
                                ph.ChangeKilledState(true);
                                ph.isShot = true;

                                if (victimBot != null)
                                {
                                    // Bot victim — explode BEFORE disabling physics (ragdoll reads bones)
                                    try
                                    {
                                        ph.ExplodeServer(false, true, hit.collider.gameObject.name,
                                            dir, _heldWeapon.ragdollEjectForce, hit.point);
                                        DisableBotPhysics(ph.gameObject);
                                        ph.DisablePlayerObjectWhenKilled();
                                    }
                                    catch (System.Exception e)
                                    {
                                        Plugin.Log.LogWarning($"[{BotName}] Kill: {e.Message}");
                                    }
                                    victimBot.Die(transform);
                                }
                                else
                                {
                                    // Human victim — host already handled via FishNet RPCs.
                                    // Sync to non-host clients via Mycelium.
                                    try
                                    {
                                        int pid = ph.playerValues.playerClient.PlayerId;
                                        string wpn = _heldBehaviour != null ? _heldBehaviour.weaponName : "weapon";
                                        BotDamageSync.SyncKill(pid, BotName, wpn, headshot,
                                            dir, _heldWeapon.ragdollEjectForce, hit.point, hit.collider.gameObject.name);
                                    }
                                    catch { }
                                }
                                RegisterKill(ph, headshot);
                            }
                        }

                        // Spawn generic impact for non-player hits (walls, etc)
                        if (ph == null)
                        {
                            try
                            {
                                if (_heldWeapon.genericImpact != null)
                                {
                                    var fx = Object.Instantiate(_heldWeapon.genericImpact, hit.point, Quaternion.LookRotation(hit.normal));
                                    Object.Destroy(fx, 2f);
                                }
                            }
                            catch { }
                        }

                        // Break glass — check the hit object AND nearby objects
                        // Glass may be on a child/parent, or the hit might be the frame
                        try
                        {
                            GameObject glassObj = null;
                            if (hit.collider.CompareTag("ShatterableGlass"))
                                glassObj = hit.collider.gameObject;
                            else if (hit.collider.GetComponent<ShatterableGlass>() != null)
                                glassObj = hit.collider.gameObject;
                            else if (hit.collider.GetComponentInParent<ShatterableGlass>() != null)
                                glassObj = hit.collider.GetComponentInParent<ShatterableGlass>().gameObject;
                            // Also check: did we pass through glass to hit this? (glass is thin)
                            if (glassObj == null)
                            {
                                // Small sphere check at hit point for nearby glass
                                int gc = Physics.OverlapSphereNonAlloc(hit.point, 0.3f, _overlapBuffer, (1 << 19), QueryTriggerInteraction.Collide);
                                for (int gi = 0; gi < gc; gi++)
                                {
                                    if (_overlapBuffer[gi].CompareTag("ShatterableGlass") ||
                                        _overlapBuffer[gi].GetComponent<ShatterableGlass>() != null)
                                    {
                                        glassObj = _overlapBuffer[gi].gameObject;
                                        break;
                                    }
                                }
                            }
                            if (glassObj != null)
                                _heldWeapon.BreakGlassServer(hit.point, dir, glassObj);
                        }
                        catch { }
                    }
                }

                // Spawn bullet trail via weapon's ObserversRpc (syncs to all clients)
                try
                {
                    bool trailSynced = false;
                    foreach (var comp in _heldWeaponObj.GetComponents<MonoBehaviour>())
                    {
                        if (comp is Gun || comp is Shotgun || comp is LargeRaycastGun ||
                            comp is Minigun || comp is ChargeGun || comp is BeamGun)
                        {
                            var trailMethod = comp.GetType().GetMethod("SpawnBulletTrailServer",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (trailMethod != null)
                            {
                                try { trailMethod.Invoke(comp, new object[] { hitPoint }); trailSynced = true; }
                                catch { }
                            }
                            break;
                        }
                    }

                    // Sync trail to non-host clients via Mycelium
                    Vector3 trailStart = (_heldWeapon.shootPoint != null)
                        ? _heldWeapon.shootPoint.position
                        : (transform.position + transform.forward * 0.5f + Vector3.up * 1.3f);
                    BotDamageSync.SyncBulletTrail(trailStart, hitPoint);

                    // Fallback: local trail if weapon RPC failed
                    if (!trailSynced && _heldWeapon.bulletTrailLocal != null)
                    {
                        Vector3 trailOrigin = (_heldWeapon.shootPoint != null)
                            ? _heldWeapon.shootPoint.position
                            : (transform.position + transform.forward * 0.5f + Vector3.up * 1.3f);
                        GameObject trailObj = Object.Instantiate(_heldWeapon.bulletTrailLocal.gameObject,
                            trailOrigin, Quaternion.identity);
                        LineRenderer lr = trailObj.GetComponent<LineRenderer>();
                        if (lr != null)
                        {
                            lr.SetPosition(0, trailOrigin);
                            lr.SetPosition(1, hitPoint);
                        }
                        Object.Destroy(trailObj, 0.4f);
                    }
                }
                catch { }
            }
        }

        // ===================== MELEE =====================

        private bool _meleeSwingPending;
        private float _meleeSwingTimer;

        private void TryMeleeAttack()
        {
            if (_heldWeapon == null || _playerTarget == null) return;

            // Waiting for attack delay (swing animation playing, damage not yet applied)
            if (_meleeSwingPending)
            {
                _meleeSwingTimer -= Time.deltaTime;
                if (_meleeSwingTimer <= 0f)
                {
                    _meleeSwingPending = false;
                    ApplyMeleeDamage();
                }
                return;
            }

            _fireTimer += Time.deltaTime;
            float meleeDelay = ReadFloatField(_cachedMeleeWeapon, "timeBetweenBaseAttack", 0.7f);
            if (_fireTimer < meleeDelay * 1f) return;
            _fireTimer = 0f;

            if (Vector3.Distance(transform.position, _playerTarget.position) > _meleeRange) return;

            // Play melee attack animation on ALL animators
            try
            {
                var meleeWeapon = _cachedMeleeWeapon;
                if (meleeWeapon != null)
                {
                    string animName = null;
                    var baseAnimField = typeof(MeleeWeapon).GetField("baseAttackAnim",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (baseAnimField != null)
                        animName = baseAnimField.GetValue(meleeWeapon) as string;
                    if (string.IsNullOrEmpty(animName)) animName = "BaseAttack";

                    // Bot body animators — local
                    if (_bodyAnimator != null)
                        try { _bodyAnimator.SetTrigger(animName); } catch { }
                    if (_globalAnimator != null)
                        try { _globalAnimator.SetTrigger(animName); } catch { }

                    // FPC's NetworkAnimator — syncs body/arm swing to all clients
                    if (_fpc != null)
                    {
                        try
                        {
                            var fpcNetAnimField = typeof(FirstPersonController).GetField("networkAnimator",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (fpcNetAnimField != null)
                            {
                                var fpcNetAnim = fpcNetAnimField.GetValue(_fpc) as FishNet.Component.Animating.NetworkAnimator;
                                if (fpcNetAnim != null)
                                    fpcNetAnim.SetTrigger(animName);
                            }
                        }
                        catch { }
                    }


                    // Also trigger on ALL animators on the weapon object (catches any we missed)
                    foreach (var anim in _heldWeaponObj.GetComponentsInChildren<Animator>(true))
                    {
                        try { anim.SetTrigger(animName); } catch { }
                    }
                }

                // Play attack sound
                if (_heldWeapon.fireClip != null)
                {
                    if (_botAudio != null) _botAudio.PlayOneShot(_heldWeapon.fireClip);
                    AudioSource.PlayClipAtPoint(_heldWeapon.fireClip, transform.position);
                }
                else
                {
                    // Try the firstAttackStartClip from MeleeWeapon
                    var clipField = typeof(MeleeWeapon).GetField("firstAttackStartClip",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (clipField != null && meleeWeapon != null)
                    {
                        var clip = clipField.GetValue(meleeWeapon) as AudioClip;
                        if (clip != null)
                        {
                            if (_botAudio != null) _botAudio.PlayOneShot(clip);
                            AudioSource.PlayClipAtPoint(clip, transform.position);
                        }
                    }
                }
            }
            catch { }

            // Start swing — damage applies after firstAttackDelay
            float attackDelay = ReadFloatField(_cachedMeleeWeapon, "firstAttackDelay", 0f);
            if (attackDelay > 0.01f)
            {
                _meleeSwingPending = true;
                _meleeSwingTimer = attackDelay;
            }
            else
            {
                ApplyMeleeDamage();
            }
        }

        private void ApplyMeleeDamage()
        {
            if (_heldWeapon == null || _playerTarget == null) return;
            if (Vector3.Distance(transform.position, _playerTarget.position) > _meleeRange + 1f) return;

            PlayerHealth ph = _playerTarget.GetComponentInParent<PlayerHealth>();
            if (ph != null && !ph.isKilled)
            {
                float meleeDmg = GetMeleeDamage();
                ph.RemoveHealth(meleeDmg);
                _lastHitTime = Time.time;
                _combatStaleTimer = 0f;
                ph.SetKiller(transform);
                if (ph.health <= 0f)
                {
                    ph.ChangeKilledState(true);
                    ph.isShot = true;

                    BotController victimBot = ph.GetComponent<BotController>();
                    if (victimBot == null)
                    {
                        // Human victim — sync to non-host clients via Mycelium
                        try
                        {
                            int pid = ph.playerValues.playerClient.PlayerId;
                            Vector3 meleeDir = (ph.transform.position - transform.position).normalized;
                            string wpn = _heldBehaviour != null ? _heldBehaviour.weaponName : "melee";
                            BotDamageSync.SyncKill(pid, BotName, wpn, false,
                                meleeDir, 30f, ph.transform.position, "Torso");
                        }
                        catch { }
                    }
                    else
                    {
                        // Bot victim — explode BEFORE disabling physics (ragdoll reads bones)
                        try
                        {
                            Vector3 meleeDir = (ph.transform.position - transform.position).normalized;
                            ph.ExplodeServer(false, true, "Torso", meleeDir, 30f, ph.transform.position);
                            DisableBotPhysics(ph.gameObject);
                            ph.DisablePlayerObjectWhenKilled();
                        }
                        catch { }
                        victimBot.Die(transform);
                    }
                    RegisterKill(ph, false);
                }
            }
        }

        private float GetMeleeDamage()
        {
            if (_heldWeapon == null) return 1f;
            var meleeWeapon = _heldWeapon as MeleeWeapon;
            if (meleeWeapon == null) return _heldWeapon.damage;

            // Read the serialized baseAttackDamage field
            var field = typeof(MeleeWeapon).GetField("baseAttackDamage",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                float dmg = (float)field.GetValue(meleeWeapon);
                if (dmg > 0f) return dmg;
            }
            return 1f; // Fallback
        }

        // ===================== SCORING =====================

        private void RegisterKill(PlayerHealth victim, bool headshot)
        {
            try
            {
                // Get victim identity
                int victimId = -1;
                string victimName = "Player";
                BotController victimBot = victim.GetComponent<BotController>();
                if (victimBot != null)
                {
                    victimId = victimBot.PlayerId;
                    victimName = victimBot.BotName;
                }
                else
                {
                    PlayerValues pv = victim.GetComponent<PlayerValues>();
                    if (pv != null && pv.playerClient != null)
                    {
                        victimId = pv.playerClient.PlayerId;
                        victimName = pv.playerClient.PlayerName;
                    }
                }

                // Don't call GameManager.PlayerDied here:
                // - Bot victims: handled by BotController.Die()
                // - Human victims: handled by game's PlayerHealth.Update

                // Write kill feed
                string weaponName = _heldBehaviour != null ? _heldBehaviour.weaponName : "weapon";
                string action = _cachedIsMelee ? (headshot ? "beheaded" : "slain") : (headshot ? "headshot" : "killed");
                BotKillFeed.Write(victim, gameObject, BotName, weaponName, action, true);

                Plugin.Log.LogInfo($"[{BotName}] Killed {victimName} ({action}) with {weaponName}");

                // Taunt after kill — play a random taunt sound via the FPC's ObserversRpc
                try
                {
                    if (_fpc != null)
                    {
                        var tauntClips = typeof(FirstPersonController).GetField("tauntClip",
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (tauntClips != null)
                        {
                            var clips = tauntClips.GetValue(_fpc) as AudioClip[];
                            if (clips != null && clips.Length > 0)
                            {
                                int clipIndex = Random.Range(0, clips.Length);
                                // Call AboubiPlayObservers directly (ObserversRpc — syncs to all clients)
                                var method = typeof(FirstPersonController).GetMethod("AboubiPlayObservers",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                                if (method != null)
                                    method.Invoke(_fpc, new object[] { clipIndex });
                                // Also play locally for host
                                AudioSource.PlayClipAtPoint(clips[clipIndex], transform.position);
                            }
                        }
                    }
                }
                catch { }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[{BotName}] RegisterKill error: {e.Message}");
            }
        }
    }
}
