using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Il2CppInterop.Runtime;
using Il2Cpp;
using Il2CppAssets.Scripts.Actors.Enemies;
using Il2CppAssets.Scripts.Actors.Player;
using Il2CppAssets.Scripts.Inventory__Items__Pickups.Chests;
using Il2CppAssets.Scripts.Inventory__Items__Pickups.Interactables;
using Il2CppAssets.Scripts.Inventory__Items__Pickups.Items;
using Il2CppAssets.Scripts.Inventory__Items__Pickups.Stats;
using Il2CppAssets.Scripts.Menu.Shop;

[assembly: MelonInfo(typeof(MegabonkAI.Core), "MegabonkAI", "3.9.0", "eduardo")]
[assembly: MelonGame("Ved", "Megabonk")]

namespace MegabonkAI
{
    public class Core : MelonMod
    {
        // Toggle with F9. Starts disabled so a human is always in control until asked for.
        public static bool AiEnabled = false;

        public static float DesiredMoveHorizontal = 0f;
        public static float DesiredMoveVertical = 0f;

        // Where the camera should be looking - the direction we're travelling, so the route
        // ahead stays on screen. Consumed by RotationInputPatch.
        // Speedrun mode (F8): chase the same objectives, but travel using the game's movement
        // tech instead of walking.
        public static bool SpeedrunMode = false;

        // The chase camera is its own thing - toggled separately so the view can be changed
        // without altering how the bot plays, and vice versa.
        public static bool ChaseCamera = false;

        public static bool HasCameraHeading = false;
        public static float CameraYaw = 0f;
        public static float CameraPitch = 18f;
        public static bool CameraSnap = false;   // air-strafing needs the view exactly on target
        public const float CameraTurnRate = 3.5f;
        public const float CameraBasePitch = 18f;   // a little above the character, looking down

        // Jumps are issued as one-shot calls into the game's own Jump(), never by holding an
        // input flag - see the note in MovementInputPatch.
        private void TryJump()
        {
            if (Time.time < _nextJumpAllowed) return;
            if (_cachedMovement == null) return;

            try
            {
                if (!_cachedMovement.IsTouchingGround()) return;
                _cachedMovement.Jump();
                _nextJumpAllowed = Time.time + JumpCooldown;
            }
            catch { }
        }

        // --- combat tuning: safe, but always moving so it stays watchable ---
        private const float CriticalRadius = 4f;    // this close = drop everything and run
        private const float PanicRadiusBase = 6.5f;
        private const float PanicRadiusLowHp = 10f;
        private const float LowHpThreshold = 0.4f;
        private const float EngageRadius = 16f;
        private const float OrbitDistance = 9f;
        // Boss handling: fight it, but from range. Keep the boss inside weapon reach while
        // never letting it close to melee, and back right off when hurt.
        private const float BossEngageRadius = 32f;    // start duelling once this close
        private const float DefaultKiteRing = 11f;     // fallback when weapon range is unknown
        private const float BossRetreatHp = 0.4f;      // below this, widen the ring and recover
        private const float BossPanicRadius = 18f;     // used only when not duelling
        private float _kiteRing = DefaultKiteRing;
        private float _nextWeaponRangeCheck = 0f;
        private const float EnemyRefreshInterval = 0.15f;

        // --- looting ---
        private const float LootNoticeRadius = 95f;
        private const float LootRefreshInterval = 1.0f;
        private const float LootReachedRadius = 2.5f;
        private const float LootBlacklistTime = 25f;
        private const float ChargeHoldTimeout = 45f;
        private const float ChargeCriticalRadius = 2f;    // tolerate enemies much closer while charging
        private const float ChargeLowHpThreshold = 0.25f;

        // --- exploration ---
        private const float CellSize = 10f;
        private const float ExploreMinDist = 35f;
        private const float ExploreMaxDist = 90f;
        private const float ExploreReachedRadius = 9f;
        private const float ExploreTimeout = 22f;
        private const int ExploreCandidates = 28;

        // --- stuck detection ---
        private const float StuckCheckInterval = 0.3f;
        private const float StuckMoveThreshold = 0.28f;
        private const float StuckTriggerTime = 0.6f;
        private const float LoopWindow = 8f;        // if we barely displace over this window, we're circling
        private const float LoopNetDistance = 12f;

        private Enemy[] _cachedEnemies = Array.Empty<Enemy>();
        private float _nextEnemyScan = 0f;

        private readonly List<LootTarget> _cachedLoot = new List<LootTarget>();
        private float _nextLootScan = 0f;
        private readonly Dictionary<int, float> _lootBlacklist = new Dictionary<int, float>();
        private int _standingOnLootId = 0;
        private float _standingOnLootSince = 0f;
        private float _lastChargeHoldTime = -99f;
        private int _chargeLockId = 0;   // shrine we've committed to finishing

        // progress watchdog: if we can't actually get closer to a target, it's unreachable
        // (chest up a cliff, loot across a ravine) - drop it instead of orbiting forever
        private const float NoProgressTimeout = 6f;
        private const float UnreachableBlacklistTime = 120f;
        private int _progressTargetId = 0;
        private float _progressBestDist = float.MaxValue;
        private float _progressLastImprove = 0f;
        private int _partialTargetId = 0;
        private float _partialSince = 0f;

        // Strikes against a specific target. Blocking the offending ground wasn't enough on
        // its own: the bot kept the same goal and simply tried the next way round the mountain.
        private readonly Dictionary<int, int> _lootStrikes = new Dictionary<int, int>();
        private const int MaxLootStrikes = 2;

        // Target commitment - stops the bot flip-flopping between two comparable targets.
        private int _committedLootId = 0;
        private float _committedAt = 0f;
        private const float MinCommitTime = 2.5f;  // never switch within this of committing
        private const float SwitchMargin = 1.6f;   // a rival must be this much better to steal us
        private const float SpeedrunTargetLock = 30f; // speedrunning, finish what we started
        // Only genuine peaks should be demoted - hillside loot is perfectly reachable now that
        // the grid allows real slopes again.
        private const float HighClimbThreshold = 16f;

        private readonly HashSet<long> _visitedCells = new HashSet<long>();
        private Vector3 _exploreTarget;
        private bool _hasExploreTarget = false;
        private float _exploreTargetExpiry = 0f;

        private GameObject _cachedPlayerGO;
        private PlayerMovement _cachedMovement;
        private Il2Cpp.PlayerInput _cachedInput;
        private PlayerInventory _cachedInventory;

        // --- navigation / obstacle sensing ---
        private const float ProbeDistance = 3.2f;    // how far ahead we look for walls
        private const float CliffProbeDrop = 5f;     // a drop deeper than this counts as a hazard
        private const float MaxSafeStepDown = 3.5f;  // beyond this it's a fall, not a step
        private int _groundMask = 0;
        private int _groundOnlyMask = 0;
        private float _playerRadius = 0.5f;
        private float _maxSlopeAngle = 45f;          // read from the game's own movement limits
        private const float SteerCommitTime = 0.8f;  // hold a detour this long before rethinking
        private const float SteerFreeRadius = 3.5f;  // this close to the goal, walk straight at it
        private int _avoidSide = 1;                  // commit to one side so we don't jitter in corners
        private float _avoidSideUntil = 0f;
        private Vector3 _steerCommitDir = Vector3.zero;
        private float _steerCommitUntil = 0f;
        private Transform _navIgnoreTransform;       // current goal - never treat it as a wall
        private static readonly float[] ProbeAngles =
            { 0f, 18f, 36f, 55f, 75f, 95f, 120f, 150f, 180f };
        private int _skippedChestsNoGold = 0;
        private int _lastSkippedChestPrice = -1;
        private float _nextStatusLog = 0f;
        private string _currentMode = "init";
        private float _aiEnabledAt = -99f;
        private const float StartupSettleTime = 1.2f;
        private string _currentTargetLabel = "-";

        // --- A* pathfinding ---
        private readonly Pathfinder _pathfinder = new Pathfinder();
        private readonly List<Vector3> _path = new List<Vector3>();
        private int _pathIndex = 0;
        private Vector3 _pathGoal = Vector3.zero;
        private bool _hasPath = false;
        private float _nextPlanAllowed = 0f;
        private float _pathPlannedAt = 0f;
        private int _pathFailures = 0;
        private string _pathState = "none";
        private bool _goalProvenUnreachable = false;
        private const float PlanCooldown = 0.35f;    // don't thrash the search
        private const float SearchMillisPerFrame = 1.2f; // time-slice so long routes don't hitch
        private const float PathRefreshInterval = 1.5f;  // keep the drawn route close to reality
        private const float WaypointRadius = 2.2f;
        private const float GoalMovedTolerance = 3f;
        private bool _followingPath = false;
        private int _lastWaypointIndex = -1;
        private float _waypointEnteredAt = 0f;
        private Vector3 _smoothedMoveDir = Vector3.zero;
        private const float NormalTurnRate = 7f;   // radians/sec
        private const float EvadeTurnRate = 16f;   // snap round fast when something's on us

        // stuck state
        private Vector3 _stuckLastPos;
        private float _nextStuckCheck = 0f;
        private float _stuckTimer = 0f;
        private float _stuckRecoveryUntil = 0f;
        private Vector3 _stuckRecoveryDir = Vector3.zero;
        private int _consecutiveStucks = 0;
        private float _lastStuckTime = -99f;
        private float _nextJumpAllowed = 0f;
        private const float JumpCooldown = 1.2f;

        // loop detection ring buffer
        private readonly List<(float t, Vector3 pos)> _posHistory = new List<(float, Vector3)>();
        private float _nextPosSample = 0f;

        // UI handling
        private bool _handlingLevelUp = false;
        private float _nextLevelUpAttempt = 0f;
        private List<UpgradeButton> _levelUpCandidates = new List<UpgradeButton>();
        private int _levelUpCursor = 0;
        private int _levelUpFullPasses = 0;
        private float _nextChestWindowClick = 0f;
        private float _nextEncounterClick = 0f;
        private LevelupScreen _cachedLevelupScreen;
        private float _nextLevelupScreenLookup = 0f;
        private float _nextOfferButtonProbe = 0f;
        private bool _sawOfferButtons = false;

        // --- debug visuals (F10) ---
        private bool _debugVisuals = true;
        private GameObject _pathLineGO;
        private LineRenderer _pathLine;
        private GameObject _goalLineGO;
        private LineRenderer _goalLine;
        private const int DrawPointCount = 56;
        private readonly List<Vector3> _drawPoints = new List<Vector3>();
        private readonly List<Vector3> _rawPoints = new List<Vector3>();
        private readonly List<Vector3> _cornerCut = new List<Vector3>();
        private readonly List<Vector3> _resampled = new List<Vector3>();
        private Color _drawColor = Color.green;
        private float _pathLineHoldUntil = 0f;
        private Vector3 _smoothedGoal = Vector3.zero;

        private struct LootTarget
        {
            public Transform Tf;                  // always set
            public BaseInteractable Interactable; // null for pickups that aren't interactables
            public Vector3 Position;
            public float Value;
            public float DistWeight;   // low = worth walking a long way for
            public int Id;
            public string Kind;
            public bool LowPriority;   // only worth doing when nothing better exists
            public bool HoldToUse;     // charge shrines: stand still until they finish
        }

        public override void OnInitializeMelon()
        {
            var harmony = new HarmonyLib.Harmony("com.eduardo.megabonkai");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            LoggerInstance.Msg("MegabonkAI v3.9 - Num1/F9 AI, Num2/F8 speedrun, Num3/F10 visuals, Num4/F7 chase camera. Press F9 in-game to toggle AI control.");
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F8) || Input.GetKeyDown(KeyCode.Keypad2))
            {
                SpeedrunMode = !SpeedrunMode;
                LoggerInstance.Msg($"Speedrun mode {(SpeedrunMode ? "ON - bhop/air-strafe/slide" : "off")}");
                if (!SpeedrunMode)
                {
                    try { if (_cachedMovement != null) _cachedMovement.StopSlide(); } catch { }
                }
            }

            if (Input.GetKeyDown(KeyCode.F7) || Input.GetKeyDown(KeyCode.Keypad4))
            {
                ChaseCamera = !ChaseCamera;
                _chaseAnchorSet = false;
                LoggerInstance.Msg($"Chase camera {(ChaseCamera ? "on" : "off")}");
            }

            if (Input.GetKeyDown(KeyCode.F10) || Input.GetKeyDown(KeyCode.Keypad3))
            {
                _debugVisuals = !_debugVisuals;
                LoggerInstance.Msg($"Debug visuals {(_debugVisuals ? "on" : "off")}");
            }

            if (Input.GetKeyDown(KeyCode.F9) || Input.GetKeyDown(KeyCode.Keypad1))
            {
                AiEnabled = !AiEnabled;
                _aiEnabledAt = Time.time;
                LoggerInstance.Msg($"AI control {(AiEnabled ? "ENABLED" : "disabled")}");
                if (!AiEnabled)
                {
                    DesiredMoveHorizontal = 0f;
                    DesiredMoveVertical = 0f;
                    HasCameraHeading = false; // hand the camera back to the player
                }
            }

            if (!AiEnabled) return;

            try
            {
                HandleLevelUpIfNeeded();
                bool chestWindowOpen = HandleChestWindowIfNeeded();
                bool encounterOpen = HandleEncounterWindowIfNeeded();

                if (IsOfferWindowOpen() || chestWindowOpen || encounterOpen)
                {
                    DesiredMoveHorizontal = 0f;
                    DesiredMoveVertical = 0f;
                    return;
                }

                UpdateMovement();
                UpdateInteracting();
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"OnUpdate error: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // speedrun chase camera
        //
        // Air-strafing requires the *movement* facing to snap around at 200+ deg/sec, which is
        // unwatchable if the view is bolted to it. So the two are decoupled: the input's
        // rotation keeps snapping (that's what generates speed) while the rendered camera is
        // driven separately here, following heavily-damped travel direction instead.
        // PlayerCamera does its work in Update, so a LateUpdate override wins for the frame.
        // ------------------------------------------------------------------

        private Vector3 _chaseVel = Vector3.zero;
        private Vector3 _chasePosVel = Vector3.zero;
        private Vector3 _chaseDir = Vector3.zero;
        private Vector3 _chaseAnchor = Vector3.zero;   // heavily smoothed stand-in for the player
        private bool _chaseAnchorSet = false;
        private float _chaseYaw = 0f;

        private const float ChaseDistance = 10f;
        private const float ChaseHeight = 4.5f;
        private const float ChaseDirDamping = 0.9f;      // very slow: strafe wobble is ignored
        private const float ChaseAnchorSmoothXZ = 0.22f;
        private const float ChaseAnchorSmoothY = 0.75f;  // vertical is damped hard - hops bob a lot
        private const float ChaseYawDamping = 2.2f;
        private const float ChasePitch = 16f;

        private void DriveSpeedrunCamera()
        {
            try
            {
                var pc = PlayerCamera.Instance;
                if (pc == null) return;
                var cam = pc.camera;
                if (cam == null || _cachedPlayerGO == null) return;

                Transform t = cam.transform;
                Vector3 player = _cachedPlayerGO.transform.position;

                // Track a smoothed stand-in for the player rather than the player itself, with
                // vertical damped much harder than horizontal - bunny hopping makes the real
                // position jump every fraction of a second and following it directly is what
                // made the shot wobble.
                if (!_chaseAnchorSet)
                {
                    _chaseAnchor = player;
                    _chaseAnchorSet = true;
                }
                else
                {
                    Vector3 flatAnchor = new Vector3(
                        Mathf.SmoothDamp(_chaseAnchor.x, player.x, ref _chasePosVel.x, ChaseAnchorSmoothXZ),
                        Mathf.SmoothDamp(_chaseAnchor.y, player.y, ref _chasePosVel.y, ChaseAnchorSmoothY),
                        Mathf.SmoothDamp(_chaseAnchor.z, player.z, ref _chasePosVel.z, ChaseAnchorSmoothXZ));
                    _chaseAnchor = flatAnchor;
                }

                // Follow actual travel direction, slowly.
                Vector3 vel = Vector3.zero;
                try { if (_cachedMovement != null) vel = _cachedMovement.GetVelocity(); } catch { }
                Vector3 flat = new Vector3(vel.x, 0f, vel.z);

                if (flat.magnitude > 3f)
                {
                    float targetYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                    _chaseYaw += Mathf.DeltaAngle(_chaseYaw, targetYaw) *
                                 (1f - Mathf.Exp(-Time.deltaTime * ChaseDirDamping));
                }

                _chaseDir = Quaternion.Euler(0f, _chaseYaw, 0f) * Vector3.forward;

                Vector3 desired = _chaseAnchor + Vector3.up * ChaseHeight - _chaseDir * ChaseDistance;

                // don't let terrain get between the camera and the player
                if (_groundMask != 0)
                {
                    Vector3 from = _chaseAnchor + Vector3.up * 1.5f;
                    Vector3 delta = desired - from;
                    float dist = delta.magnitude;
                    if (dist > 0.1f &&
                        Physics.SphereCast(from, 0.4f, delta / dist, out RaycastHit hit, dist, _groundMask))
                    {
                        desired = from + (delta / dist) * Mathf.Max(hit.distance - 0.4f, 2f);
                    }
                }

                t.position = desired;

                // Fixed pitch on a smoothed yaw, rather than looking *at* the player: a look-at
                // re-aims every time the character rises or falls, which is exactly the wobble.
                t.rotation = Quaternion.Euler(ChasePitch, _chaseYaw, 0f);
            }
            catch { }
        }

        // Drive the camera after every Update has run, so nothing the game does later in the
        // frame overwrites us. Both rotation fields are written: whichever one the camera
        // actually consumes, it gets the same eased value, so the turn is smooth either way.
        public override void OnLateUpdate()
        {
            ApplyCameraRotationFields();

            if (AiEnabled && ChaseCamera)
            {
                DriveSpeedrunCamera();
            }
            else
            {
                _chaseDir = Vector3.zero;
                _chaseVel = Vector3.zero;
                _chasePosVel = Vector3.zero;
                _chaseAnchorSet = false;
            }
        }

        private void ApplyCameraRotationFields()
        {
            if (!AiEnabled || !HasCameraHeading || _cachedInput == null) return;

            try
            {
                Vector3 desired = _cachedInput.desiredCameraRotation;

                // frame-rate independent easing toward the heading; air-strafing bypasses it
                float k = CameraSnap ? 1f : 1f - Mathf.Exp(-Time.deltaTime * CameraTurnRate);
                float yaw = desired.y + Mathf.DeltaAngle(desired.y, CameraYaw) * k;

                // pitch eases more gently so terrain changes don't bob the view
                float kp = 1f - Mathf.Exp(-Time.deltaTime * CameraTurnRate * 0.6f);
                float pitch = desired.x + Mathf.DeltaAngle(desired.x, CameraPitch) * kp;

                desired.y = yaw;
                desired.x = pitch;
                _cachedInput.desiredCameraRotation = desired;

                Vector3 actual = _cachedInput.cameraRotation;
                actual.y = yaw;
                actual.x = pitch;
                _cachedInput.cameraRotation = actual;
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // player component plumbing
        // ------------------------------------------------------------------

        private void EnsurePlayerComponents(GameObject playerGO)
        {
            if (_cachedPlayerGO == playerGO && _cachedMovement != null) return;

            _cachedPlayerGO = playerGO;

            var mv = playerGO.GetComponent(Il2CppType.Of<PlayerMovement>());
            _cachedMovement = mv == null ? null : mv.Cast<PlayerMovement>();

            var inp = playerGO.GetComponent(Il2CppType.Of<Il2Cpp.PlayerInput>());
            _cachedInput = inp == null ? null : inp.Cast<Il2Cpp.PlayerInput>();

            // PlayerInventory is a plain Il2CppSystem.Object, NOT a component - GetComponent
            // silently returned null, so gold read 0, health always fell back to 100% and the
            // weapon range never loaded. It hangs off MyPlayer instead.
            _cachedInventory = null;
            try
            {
                var mp = MyPlayer.Instance;
                if (mp != null) _cachedInventory = mp.inventory;
            }
            catch { }

            LoggerInstance.Msg($"Inventory hooked: {(_cachedInventory != null ? "yes" : "NO")}");

            try
            {
                if (_cachedMovement != null)
                {
                    _groundMask = _cachedMovement.whatIsGround.value;
                    _groundOnlyMask = _cachedMovement.whatIsGroundOnly.value;
                    if (_groundOnlyMask == 0) _groundOnlyMask = _groundMask;

                    float r = _cachedMovement.GetPlayerRadius();
                    if (r > 0.05f) _playerRadius = r;

                    float slope = _cachedMovement.maxSlopeAngle;
                    if (slope > 5f && slope < 89f) _maxSlopeAngle = slope;

                    _pathfinder.GroundMask = _groundOnlyMask;
                    _pathfinder.ObstacleMask = _groundMask;
                    _pathfinder.MaxSlope = _maxSlopeAngle;
                    _pathfinder.PlayerRadius = _playerRadius;
                    _pathfinder.ConfigureFromSlope();
                    _pathfinder.ClearCache();
                    _hasPath = false;

                    LoggerInstance.Msg($"Nav ready: radius={_playerRadius:0.00} maxSlope={_maxSlopeAngle:0}deg " +
                                       $"stepUp={_pathfinder.MaxStepUp:0.00} " +
                                       $"groundMask={_groundMask} groundOnly={_groundOnlyMask}");
                }
            }
            catch { }

            // Fresh run (or first spawn): forget the old map knowledge and take the settle
            // period again, since masks and inventory are re-acquired from scratch.
            _aiEnabledAt = Time.time;
            _pathGoal = playerGO.transform.position;
            _visitedCells.Clear();
            _lootBlacklist.Clear();
            _lootStrikes.Clear();
            _hasExploreTarget = false;
            _posHistory.Clear();
        }

        // Longest reach among equipped weapons, so the kite ring keeps the boss in range of
        // whatever we're actually carrying rather than a hardcoded guess.
        private void RefreshKiteRing()
        {
            if (Time.time < _nextWeaponRangeCheck) return;
            _nextWeaponRangeCheck = Time.time + 2f;

            float best = 0f;
            try
            {
                var wi = _cachedInventory != null ? _cachedInventory.weaponInventory : null;
                var weapons = wi != null ? wi.weapons : null;
                if (weapons != null)
                {
                    var e = weapons.GetEnumerator();
                    while (e.MoveNext())
                    {
                        var wb = e.Current.Value;
                        var wd = wb != null ? wb.weaponData : null;
                        if (wd == null) continue;
                        best = Mathf.Max(best, wd.GetSpawnProjectileRange());
                    }
                }
            }
            catch { }

            // sit comfortably inside our reach, but never brawling distance
            _kiteRing = best > 1f ? Mathf.Clamp(best * 0.72f, 7.5f, 20f) : DefaultKiteRing;
        }

        // PlayerInventory exposes gold as both a float and an int property; read whichever is
        // actually populated rather than assuming.
        // The inventory is built slightly after the player spawns, so keep retrying until it
        // appears rather than caching a null forever.
        private void EnsureInventory()
        {
            if (_cachedInventory != null) return;
            if (Time.time < _nextInventoryRetry) return;
            _nextInventoryRetry = Time.time + 1f;

            try
            {
                var mp = MyPlayer.Instance;
                if (mp != null) _cachedInventory = mp.inventory;
                if (_cachedInventory != null) LoggerInstance.Msg("Inventory hooked (late).");
            }
            catch { }
        }

        private float _nextInventoryRetry = 0f;

        private float GetGold()
        {
            EnsureInventory();
            float g = 0f, gi = 0f;
            try { g = _cachedInventory != null ? _cachedInventory.gold : 0f; } catch { }
            try { gi = _cachedInventory != null ? _cachedInventory.goldInt : 0; } catch { }
            _lastGoldFloat = g;
            _lastGoldInt = gi;
            return Mathf.Max(g, gi);
        }

        private float _lastGoldFloat, _lastGoldInt;

        private float GetHealthPercent()
        {
            EnsureInventory();
            try
            {
                var ph = _cachedInventory != null ? _cachedInventory.playerHealth : null;
                if (ph == null || ph.maxHp <= 0) return 1f;
                return (float)ph.hp / ph.maxHp;
            }
            catch { return 1f; }
        }

        // Things the bot should never engage with: the shady guy's deals, the microwave,
        // and cursed shrines all trade away safety or hand out downsides.
        // Name fragments for things we never want to touch. There is no dedicated class for
        // the suspicious bush, so it's matched on its own label instead of by type.
        private static readonly string[] IgnoredNameFragments = { "bush", "suspicious" };

        private static bool IsIgnoredInteractable(BaseInteractable it)
        {
            try
            {
                if (it.TryCast<InteractableShadyGuy>() != null) return true;
                if (it.TryCast<InteractableMicrowave>() != null) return true;
                if (it.TryCast<InteractableShrineCursed>() != null) return true;
                if (it.TryCast<InteractableEgg>() != null) return true;

                string name = null;
                try { name = it.GetDebugName(); } catch { }
                if (string.IsNullOrEmpty(name))
                {
                    try { name = it.GetInteractString(); } catch { }
                }
                if (string.IsNullOrEmpty(name))
                {
                    try { name = it.gameObject.name; } catch { }
                }

                if (!string.IsNullOrEmpty(name))
                {
                    string lower = name.ToLowerInvariant();
                    foreach (var frag in IgnoredNameFragments)
                        if (lower.Contains(frag)) return true;
                }
            }
            catch { }
            return false;
        }

        private void UpdateInteracting()
        {
            if (_cachedInput == null) return;
            var detector = _cachedInput.detectInteractables;
            if (detector == null) return;
            try
            {
                // don't auto-press interact on something we deliberately avoid, even if we
                // happen to walk past it
                var current = detector.currentInteractable;
                if (current != null && IsIgnoredInteractable(current)) return;

                // Never poke a chest we can't pay for - that just throws "insufficient funds".
                if (current != null)
                {
                    var chest = current.TryCast<InteractableChest>();
                    if (chest != null)
                    {
                        bool afford = true;
                        try { afford = chest.CanAfford(); } catch { }
                        if (!afford) return;
                    }
                }

                if (detector.CanInteract()) detector.TryInteract();
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // loot scanning: chests (if affordable), pots/vases, and misc pickups
        // ------------------------------------------------------------------

        private void RefreshLoot(Vector3 playerPos)
        {
            _cachedLoot.Clear();

            var all = UnityEngine.Object.FindObjectsOfType<BaseInteractable>();
            float gold = GetGold();

            foreach (var it in all)
            {
                if (it == null) continue;

                if (IsIgnoredInteractable(it)) continue;

                int id = it.GetInstanceID();
                if (_lootBlacklist.TryGetValue(id, out float until) && Time.time < until) continue;

                Vector3 pos;
                try { pos = it.transform.position; } catch { continue; }

                float flatDist = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(playerPos.x, playerPos.z));
                if (flatDist > LootNoticeRadius) continue;

                float value = 0f;
                float distWeight = 1.5f;
                string kind = "misc";
                bool lowPriority = false;
                bool holdToUse = false;

                var charge = it.TryCast<ChargeShrine>();
                if (charge != null)
                {
                    bool completed = false;
                    try { completed = charge.completed; } catch { }
                    if (completed) continue;

                    bool golden = false;
                    try { golden = charge.isGolden; } catch { }

                    value = golden ? 150f : 120f;
                    distWeight = 0.35f;  // top of the shopping list, and worth a long walk
                    kind = golden ? "charge(gold)" : "charge";
                    holdToUse = true;

                    _cachedLoot.Add(new LootTarget
                    {
                        Interactable = it,
                        Tf = it.transform,
                        Position = pos,
                        Value = value,
                        DistWeight = distWeight,
                        Id = id,
                        Kind = kind,
                        HoldToUse = holdToUse,
                        LowPriority = pos.y - playerPos.y > HighClimbThreshold
                    });
                    continue;
                }

                bool isBossShrine = false;
                try
                {
                    isBossShrine = it.TryCast<InteractableBossSpawner>() != null
                                   || it.TryCast<InteractableBossSpawnerFinal>() != null
                                   || it.TryCast<InteractableSkeletonKingStatue>() != null;
                }
                catch { }

                if (isBossShrine)
                {
                    // Worth doing eventually, but never at the expense of loot or exploring.
                    _cachedLoot.Add(new LootTarget
                    {
                        Interactable = it,
                        Tf = it.transform,
                        Position = pos,
                        Value = 20f,
                        DistWeight = 1f,
                        Id = id,
                        Kind = "boss shrine",
                        LowPriority = true
                    });
                    continue;
                }

                var chest = it.TryCast<InteractableChest>();
                if (chest != null)
                {
                    bool opening = false;
                    try { opening = chest.opening; } catch { }
                    if (opening) continue;

                    int price = 0;
                    try { price = chest.GetPrice(); } catch { }

                    // Chests are the run-defining pickup: top priority, and distance barely
                    // matters compared to a vase underfoot.
                    //
                    // Unaffordable chests used to be dropped from the list entirely, which
                    // early on emptied it and left the bot with nothing to do but wander.
                    // Now they stay on the board at reduced priority - gold accumulates on
                    // the way there, and we usually can afford it by arrival.
                    if (price == 0)
                    {
                        value = 220f;
                        distWeight = 0.2f;
                        kind = "chest(free)";
                    }
                    else if (gold >= price)
                    {
                        value = 200f;
                        distWeight = 0.2f;
                        kind = $"chest({price}g)";
                    }
                    else if (gold >= price * 0.85f)
                    {
                        // Within a pot or two of affording it - the walk itself usually covers
                        // the difference.
                        value = 140f;
                        distWeight = 0.3f;
                        kind = $"chest({price}g soon)";
                    }
                    else
                    {
                        // Not close to affording it. Don't walk over just to be told we have
                        // insufficient funds - come back once the gold is there.
                        _skippedChestsNoGold++;
                        if (_lastSkippedChestPrice != price)
                        {
                            _lastSkippedChestPrice = price;
                            LoggerInstance.Msg($"Chest priced {price} skipped: gold={gold:0} " +
                                               $"(float={_lastGoldFloat:0} int={_lastGoldInt:0})");
                        }
                        continue;
                    }
                }
                else
                {
                    var pot = it.TryCast<InteractablePot>();
                    if (pot != null)
                    {
                        bool broken = false;
                        try { broken = pot.broken; } catch { }
                        if (broken) continue;

                        // Still collected, but only when they're genuinely on the way - they
                        // shouldn't compete with a chest or a shrine.
                        value = 5f;
                        try { if (pot.isBig) value += 2f; } catch { }
                        try { if (pot.isSilver) value += 1.5f; } catch { }
                        distWeight = 2.2f;
                        kind = "pot";
                    }
                    else
                    {
                        // other free interactables worth grabbing on the way (gifts, eggs, cages...)
                        bool canInteract = false;
                        try { canInteract = it.CanInteract(); } catch { }
                        bool isItemSource = false;
                        try { isItemSource = it.isItemSource; } catch { }

                        if (!canInteract && !isItemSource) continue;
                        value = isItemSource ? 30f : 5f;
                        distWeight = isItemSource ? 0.6f : 1.5f;
                        kind = isItemSource ? "itemsource" : "interactable";
                    }
                }

                // Loot perched well above us usually needs a long climb we can't find, and
                // chasing it wastes the run. Demote it to last-resort rather than banning it.
                float rise = pos.y - playerPos.y;
                if (rise > HighClimbThreshold) lowPriority = true;

                // Something high up but horizontally close is on top of a cliff we're standing
                // against - there is no direct route, only a long way round, and pressing at
                // the wall beneath it is exactly what the bot kept doing.
                if (rise > 6f && flatDist < rise * 1.6f) lowPriority = true;

                _cachedLoot.Add(new LootTarget
                {
                    Interactable = it,
                    Tf = it.transform,
                    Position = pos,
                    Value = value,
                    DistWeight = distWeight,
                    Id = id,
                    Kind = kind,
                    LowPriority = lowPriority,
                    HoldToUse = holdToUse
                });
            }

            // Chests dropped by bosses and elites are OpenChest components, not
            // BaseInteractables - they're collected by walking over them, so the scan above
            // never saw them at all. They're free loot from a fight we already won: top value.
            try
            {
                foreach (var drop in UnityEngine.Object.FindObjectsOfType<OpenChest>())
                {
                    if (drop == null || !drop.gameObject.activeInHierarchy) continue;

                    bool taken = false;
                    try { taken = drop.pickedup; } catch { }
                    if (taken) continue;

                    int id = drop.GetInstanceID();
                    if (_lootBlacklist.TryGetValue(id, out float until) && Time.time < until) continue;

                    Vector3 pos;
                    try { pos = drop.transform.position; } catch { continue; }

                    float flatDist = Vector2.Distance(new Vector2(pos.x, pos.z),
                                                      new Vector2(playerPos.x, playerPos.z));
                    if (flatDist > LootNoticeRadius) continue;

                    _cachedLoot.Add(new LootTarget
                    {
                        Interactable = null,
                        Tf = drop.transform,
                        Position = pos,
                        Value = 240f,
                        DistWeight = 0.18f,
                        Id = id,
                        Kind = "boss drop",
                        LowPriority = pos.y - playerPos.y > HighClimbThreshold
                    });
                }
            }
            catch { }
        }

        private bool TryGetBestLoot(Vector3 playerPos, out LootTarget best)
        {
            // A charge in progress owns the bot until it finishes. Scoring alone wasn't enough:
            // a chest coming into range could out-score the shrine we were standing on, and
            // stepping off cancels all the progress.
            if (_chargeLockId != 0)
            {
                foreach (var loot in _cachedLoot)
                {
                    if (loot.Id != _chargeLockId) continue;
                    best = loot;
                    return true;
                }
                _chargeLockId = 0; // shrine finished or vanished from the scan
            }

            // Low-priority targets (boss shrines) are only considered when nothing else is
            // on offer, so they never pull the bot off a chest or a charge shrine.
            bool any = TryGetBestLoot(playerPos, false, out best) ||
                       TryGetBestLoot(playerPos, true, out best);

            if (!any)
            {
                _committedLootId = 0;
                return false;
            }

            // Stick with the current target unless the alternative is clearly better.
            //
            // Scores are distance-based, so walking toward A makes B look relatively better,
            // which flips the choice, which walks back toward A... The bot ends up oscillating
            // between two targets and reaching neither. Commitment breaks that loop.
            if (_committedLootId != 0 && _committedLootId != best.Id &&
                TryFindLoot(_committedLootId, out LootTarget current))
            {
                // Speedrunning, a target is seen through to the end. Scores swing far too
                // quickly at speed for any margin to be stable, and abandoning a shrine
                // halfway is both slower and what made the routing look erratic.
                if (SpeedrunMode && Time.time - _committedAt < SpeedrunTargetLock)
                {
                    best = current;
                    return true;
                }

                float currentScore = LootScore(current, playerPos);
                float challengerScore = LootScore(best, playerPos);

                // At speed the bot covers ground fast, so distance-driven scores swing wildly
                // and targets flip constantly. Commit far harder while speedrunning.
                float dwell = SpeedrunMode ? MinCommitTime * 2.5f : MinCommitTime;
                float margin = SpeedrunMode ? SwitchMargin * 2f : SwitchMargin;

                bool withinDwell = Time.time - _committedAt < dwell;
                if (withinDwell || challengerScore < currentScore * margin)
                {
                    best = current;
                    return true;
                }

                LoggerInstance.Msg($"Switching target: {current.Kind} ({currentScore:0.0}) " +
                                   $"-> {best.Kind} ({challengerScore:0.0})");
            }

            if (_committedLootId != best.Id)
            {
                _committedLootId = best.Id;
                _committedAt = Time.time;
            }

            return true;
        }

        /// <summary>
        /// Records a failed attempt on a target. After a couple of strikes the target itself is
        /// retired for a while, rather than us endlessly finding new ways to fail to reach it.
        /// </summary>
        private void StrikeLoot(int id, string reason)
        {
            // Valuable targets get more patience and come back sooner - retiring a chest for
            // two minutes after a couple of bad approaches starved the list down to pots.
            float value = 0f;
            string kind = "target";
            foreach (var l in _cachedLoot)
            {
                if (l.Id != id) continue;
                value = l.Value;
                kind = l.Kind;
                break;
            }

            bool valuable = value >= 100f;
            int limit = valuable ? MaxLootStrikes + 2 : MaxLootStrikes;
            float cooloff = valuable ? 40f : UnreachableBlacklistTime;

            _lootStrikes.TryGetValue(id, out int strikes);
            strikes++;
            _lootStrikes[id] = strikes;

            if (strikes >= limit)
            {
                _lootBlacklist[id] = Time.time + cooloff;
                _lootStrikes.Remove(id);
                _progressTargetId = 0;
                _partialTargetId = 0;
                if (_committedLootId == id) _committedLootId = 0;
                LoggerInstance.Msg($"Parking {kind} after {strikes} failed attempts " +
                                   $"({reason}) for {cooloff:0}s.");
            }
        }

        // Value per unit of walking, weighted per kind so a vase underfoot never outranks a
        // chest across the field, and discounted for anything far above us.
        private float LootScore(LootTarget loot, Vector3 playerPos)
        {
            float dist = Vector3.Distance(new Vector3(playerPos.x, 0f, playerPos.z),
                                          new Vector3(loot.Position.x, 0f, loot.Position.z));

            // Height counts toward "how far away" - otherwise a shrine on a ledge directly
            // overhead scores as though it were underfoot.
            float verticalGap = Mathf.Abs(loot.Position.y - playerPos.y);
            if (verticalGap > 3f) dist = Mathf.Max(dist, verticalGap * 1.5f);

            float score = loot.Value / (dist * loot.DistWeight + 4f);

            float climb = loot.Position.y - playerPos.y;
            if (climb > 3f) score *= Mathf.Clamp(1f - (climb - 3f) / 20f, 0.15f, 1f);

            // Speedrunning, small pickups aren't worth breaking a line for - stopping and
            // rebuilding speed costs more than the pot is worth. Grab them only in passing.
            if (SpeedrunMode && loot.Value < 30f && dist > 12f) score *= 0.1f;

            return score;
        }

        private bool TryFindLoot(int id, out LootTarget found)
        {
            foreach (var loot in _cachedLoot)
            {
                if (loot.Id != id) continue;
                found = loot;
                return true;
            }
            found = default;
            return false;
        }

        private bool TryGetBestLoot(Vector3 playerPos, bool allowLowPriority, out LootTarget best)
        {
            best = default;
            float bestScore = float.MinValue;
            bool found = false;

            foreach (var loot in _cachedLoot)
            {
                if (loot.Tf == null) continue;
                if (loot.LowPriority != allowLowPriority) continue;

                float score = LootScore(loot, playerPos);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = loot;
                    found = true;
                }
            }

            return found;
        }

        // ------------------------------------------------------------------
        // exploration: remember where we've been, head for unseen ground
        // ------------------------------------------------------------------

        private static long CellKey(float x, float z)
        {
            int cx = Mathf.FloorToInt(x / CellSize);
            int cz = Mathf.FloorToInt(z / CellSize);
            return ((long)cx << 32) ^ (uint)cz;
        }

        private void MarkVisited(Vector3 pos)
        {
            _visitedCells.Add(CellKey(pos.x, pos.z));
        }

        private int UnvisitedNeighbours(Vector3 pos)
        {
            int count = 0;
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!_visitedCells.Contains(CellKey(pos.x + dx * CellSize, pos.z + dz * CellSize)))
                        count++;
                }
            }
            return count;
        }

        private void PickExploreTarget(Vector3 playerPos)
        {
            Vector3 bestPos = playerPos;
            float bestScore = float.MinValue;
            Vector3 fallbackPos = playerPos;
            int fallbackOpenness = -1;

            for (int i = 0; i < ExploreCandidates; i++)
            {
                float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float dist = UnityEngine.Random.Range(ExploreMinDist, ExploreMaxDist);
                Vector3 cand = playerPos + new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);

                // Reject anywhere that isn't open standable ground. Candidates were previously
                // just random points, so plenty landed inside cliffs or past the map edge and
                // the bot spent its time pressed against a wall trying to reach them.
                int openness = _pathfinder.OpennessAt(cand);
                if (openness > fallbackOpenness)
                {
                    fallbackOpenness = openness;
                    fallbackPos = cand;
                }
                if (openness < 7) continue;

                // Strongly prefer unexplored ground and reward genuine distance - a weak
                // distance term let it keep picking spots just outside the area it had
                // already combed, so it never actually left.
                float score = UnvisitedNeighbours(cand) * 3f + dist * 0.18f + openness * 0.5f;

                // and prefer somewhere we can actually set off towards, so we don't
                // commit to a target on the far side of a wall
                Vector3 dir = (cand - playerPos);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f && IsDirectionSafe(playerPos, dir.normalized, ProbeDistance))
                    score += 6f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPos = cand;
                }
            }

            // Nowhere passed the openness bar (tight terrain, corner of the map) - take the
            // most open thing we saw rather than defaulting to standing still.
            if (bestScore == float.MinValue) bestPos = fallbackPos;

            _exploreTarget = bestPos;
            _hasExploreTarget = true;
            _exploreTargetExpiry = Time.time + ExploreTimeout;
        }

        private void UpdateLoopDetection(Vector3 playerPos)
        {
            // Only meaningful while exploring. Orbiting a pack or working a cluster of pots
            // legitimately keeps us in one spot, and treating that as a loop threw the bot
            // off perfectly good objectives.
            if (_currentMode != "explore")
            {
                _posHistory.Clear();
                return;
            }

            if (Time.time < _nextPosSample) return;
            _nextPosSample = Time.time + 1f;

            _posHistory.Add((Time.time, playerPos));
            while (_posHistory.Count > 0 && Time.time - _posHistory[0].t > LoopWindow)
                _posHistory.RemoveAt(0);

            if (_posHistory.Count >= (int)LoopWindow)
            {
                float net = Vector3.Distance(_posHistory[0].pos, playerPos);
                if (net < LoopNetDistance)
                {
                    // running in circles - burn this area and commit to somewhere new
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                            _visitedCells.Add(CellKey(playerPos.x + dx * CellSize, playerPos.z + dz * CellSize));

                    _hasExploreTarget = false;
                    _posHistory.Clear();
                    LoggerInstance.Msg("Looping in place - marking area explored and rerouting.");
                }
            }
        }

        // ------------------------------------------------------------------
        // area attacks: telegraphed AoE circles, laser tubes and tornadoes
        // ------------------------------------------------------------------

        private struct Hazard
        {
            public Vector3 Position;
            public Vector3 End;      // for line hazards; equals Position for circular ones
            public float Radius;
            public bool IsLine;
        }

        private readonly List<Hazard> _hazards = new List<Hazard>();
        private float _nextHazardScan = 0f;
        private const float HazardRefreshInterval = 0.2f;
        private const float HazardMargin = 2.5f;   // clearance we want beyond the marked area

        private void RefreshHazards()
        {
            _hazards.Clear();

            try
            {
                foreach (var w in UnityEngine.Object.FindObjectsOfType<CircleWarning>())
                {
                    if (w == null || !w.gameObject.activeInHierarchy) continue;
                    Vector3 pos = w.transform.position;

                    float radius;
                    try
                    {
                        Vector3 s = w.desiredScale;
                        radius = Mathf.Max(Mathf.Max(s.x, s.z), w.transform.lossyScale.x) * 0.5f;
                    }
                    catch { radius = 3f; }
                    if (radius < 1f) radius = 3f;

                    _hazards.Add(new Hazard { Position = pos, End = pos, Radius = radius + HazardMargin });
                }
            }
            catch { }

            try
            {
                foreach (var w in UnityEngine.Object.FindObjectsOfType<TubeWarning>())
                {
                    if (w == null || !w.gameObject.activeInHierarchy) continue;
                    bool done = false;
                    try { done = w.done; } catch { }
                    if (done) continue;

                    Vector3 pos = w.transform.position;
                    Vector3 fwd = w.transform.forward;
                    Vector3 scale = w.transform.lossyScale;
                    float length = Mathf.Max(scale.z, 12f);
                    float radius = Mathf.Max(scale.x * 0.5f, 1.5f);

                    _hazards.Add(new Hazard
                    {
                        Position = pos,
                        End = pos + fwd * length,
                        Radius = radius + HazardMargin,
                        IsLine = true
                    });
                }
            }
            catch { }

            try
            {
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Tornado>())
                {
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    Vector3 pos = t.transform.position;
                    float radius = Mathf.Max(t.transform.lossyScale.x * 0.5f, 3f);
                    _hazards.Add(new Hazard { Position = pos, End = pos, Radius = radius + HazardMargin });
                }
            }
            catch { }
        }

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 0.0001f) return Vector3.Distance(p, a);
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSq);
            return Vector3.Distance(p, a + ab * t);
        }

        /// <summary>
        /// Push-away vector from every active area attack. Returns true if we're standing in one,
        /// which outranks every other consideration.
        /// </summary>
        private Vector3 ComputeHazardAvoidance(Vector3 playerPos, out bool insideHazard)
        {
            insideHazard = false;
            Vector3 push = Vector3.zero;

            foreach (var h in _hazards)
            {
                Vector3 flatPlayer = new Vector3(playerPos.x, 0f, playerPos.z);
                Vector3 away;
                float dist;

                if (h.IsLine)
                {
                    Vector3 a = new Vector3(h.Position.x, 0f, h.Position.z);
                    Vector3 b = new Vector3(h.End.x, 0f, h.End.z);
                    dist = DistanceToSegment(flatPlayer, a, b);

                    Vector3 ab = b - a;
                    float lenSq = ab.sqrMagnitude;
                    float t = lenSq < 0.0001f ? 0f : Mathf.Clamp01(Vector3.Dot(flatPlayer - a, ab) / lenSq);
                    Vector3 closestPoint = a + ab * t;
                    away = flatPlayer - closestPoint;
                }
                else
                {
                    Vector3 c = new Vector3(h.Position.x, 0f, h.Position.z);
                    dist = Vector3.Distance(flatPlayer, c);
                    away = flatPlayer - c;
                }

                if (dist > h.Radius) continue;

                insideHazard = true;

                if (away.sqrMagnitude < 0.0001f)
                {
                    // dead centre - any direction beats standing here
                    float ang = Time.time * 2f;
                    away = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                }

                float urgency = Mathf.Clamp01((h.Radius - dist) / Mathf.Max(h.Radius, 0.01f));
                push += away.normalized * (0.5f + urgency * 1.5f);
            }

            return push;
        }

        /// <summary>
        /// Picks the escape heading with the most room, rather than just summing repulsions.
        /// A summed vector points away from the crowd's centre of mass, which regularly aimed
        /// straight at a third enemy - this scores candidate directions by how much clearance
        /// they actually leave from every threat.
        /// </summary>
        /// <summary>
        /// How far we could travel in a direction before hitting something solid, capped.
        /// This is the term that stops the bot reversing into a wall, a tree or a rock.
        /// </summary>
        private float RunwayLength(Vector3 playerPos, Vector3 dir, float cap)
        {
            if (_groundMask == 0) return cap;
            try
            {
                Vector3 origin = playerPos + Vector3.up * 1.1f;
                if (Physics.SphereCast(origin, _playerRadius * 0.85f, dir, out RaycastHit hit,
                                       cap, _groundMask))
                    return hit.distance;
            }
            catch { }
            return cap;
        }

        /// <summary>
        /// Picks a direction with genuine room to move: open ground first, then distance from
        /// every threat, then alignment with what we'd prefer to do.
        ///
        /// Terrain used to be a pass/fail filter applied after enemy scoring, which meant a
        /// direction with a wall two metres away could still win as long as it pointed away
        /// from the mob - the bot would reverse into the wall and get pinned there. Runway
        /// length is now part of the score itself, so being boxed in is something it actively
        /// steers out of instead of into.
        /// </summary>
        private Vector3 ChooseOpenDirection(Vector3 playerPos, Vector3 preferred, float preferenceWeight)
        {
            const int samples = 24;
            const float runwayCap = 14f;
            const float threatProbe = 5f;

            Vector3 best = preferred;
            float bestScore = float.MinValue;
            float bestRunway = 0f;

            for (int i = 0; i < samples; i++)
            {
                float a = i * (Mathf.PI * 2f / samples);
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));

                // 1. room to actually run - the dominant term when cornered
                float runway = RunwayLength(playerPos, dir, runwayCap);
                float score = Mathf.Min(runway, runwayCap) * 2.2f;

                // walking off a ledge is not an escape
                if (!HasGroundAhead(playerPos, dir, Mathf.Min(runway, 3.5f))) score -= 60f;

                // 2. clearance from every threat where we'd end up
                Vector3 probe = playerPos + dir * Mathf.Min(threatProbe, Mathf.Max(runway, 1f));
                Vector2 probeFlat = new Vector2(probe.x, probe.z);

                float nearest = 40f;
                foreach (var enemy in _cachedEnemies)
                {
                    if (enemy == null) continue;
                    bool dead = true;
                    try { dead = enemy.IsDead(); } catch { }
                    if (dead) continue;

                    Vector3 ep;
                    try { ep = enemy.GetCenterPosition(); } catch { continue; }

                    float d = Vector2.Distance(probeFlat, new Vector2(ep.x, ep.z));
                    if (d < nearest) nearest = d;
                }
                score += Mathf.Min(nearest, 20f) * 1.5f;

                // 3. never into a telegraphed area attack
                foreach (var h in _hazards)
                {
                    float hd = h.IsLine
                        ? DistanceToSegment(new Vector3(probe.x, 0f, probe.z),
                                            new Vector3(h.Position.x, 0f, h.Position.z),
                                            new Vector3(h.End.x, 0f, h.End.z))
                        : Vector2.Distance(probeFlat, new Vector2(h.Position.x, h.Position.z));
                    if (hd < h.Radius) score -= 80f;
                }

                // 4. what we'd like to be doing, and a nudge to hold our heading
                if (preferred.sqrMagnitude > 0.0001f)
                    score += Vector3.Dot(dir, preferred.normalized) * preferenceWeight;
                score += Vector3.Dot(dir, _smoothedMoveDir) * 2f;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = dir;
                    bestRunway = runway;
                }
            }

            _lastRunway = bestRunway;
            return best;
        }

        private Vector3 ChooseEscapeDirection(Vector3 playerPos)
        {
            if (Time.time < _escapeValidUntil && _escapeDir.sqrMagnitude > 0.001f)
                return _escapeDir;

            // preference: straight away from the threats, but room to run outranks it
            Vector3 away = _lastRepel.sqrMagnitude > 0.0001f ? _lastRepel.normalized : Vector3.zero;
            _escapeDir = ChooseOpenDirection(playerPos, away, 6f);
            _escapeValidUntil = Time.time + 0.2f;
            return _escapeDir;
        }

        private Vector3 _escapeDir = Vector3.zero;
        private float _escapeValidUntil = 0f;
        private Vector3 _lastRepel = Vector3.zero;
        private float _lastRunway = 0f;
        private float _nextOrbitFlip = 0f;

        /// <summary>
        /// Kills horizontal momentum so a direction change takes effect immediately. Megabonk's
        /// movement carries a lot of speed, so without this the character keeps sliding into
        /// the very thing it decided to dodge.
        /// </summary>
        private void FastStop()
        {
            if (Time.time < _nextFastStop) return;
            if (_cachedMovement == null) return;

            try
            {
                var rb = _cachedMovement.rb;
                if (rb == null) return;

                Vector3 v = rb.velocity;
                rb.velocity = new Vector3(v.x * 0.15f, v.y, v.z * 0.15f);
                _nextFastStop = Time.time + 0.45f;
            }
            catch { }
        }

        private float _nextFastStop = 0f;

        // ------------------------------------------------------------------
        // speedrun mode (F8): Source-style movement tech to travel fast
        //
        // The movement code here is a Quake/Source derivative - ground friction
        // ("counterMovement") only applies while grounded, and air acceleration lets you gain
        // speed by strafing into a turn. So: never stay on the ground (bunny hop), turn only
        // while airborne (air-strafe), and slide down descents to carry momentum.
        // ------------------------------------------------------------------

        // Megabonk is NOT Quake air-strafing, despite the resemblance. Per the community
        // movement guide, speed comes from the angle between where the camera looks and where
        // the character is actually moving ("turn angle"):
        //   - under ~30 deg  : deadzone, no gain
        //   - around 45 deg  : sweet spot, maximum gain
        //   - beyond ~60 deg : you start losing speed
        // The camera merely has to be turning; which way it turns doesn't matter. Diagonal
        // input (forward + a strafe) is faster than straight, and bunny hopping only
        // *maintains* speed by dodging ground friction - it never adds any.
        private const float SpeedrunSweetSpot = 45f;
        private const float SpeedrunMaxTurnAngle = 60f;
        private const float SpeedrunSideSwapInterval = 0.9f;
        private float _nextSlideToggle = 0f;
        private float _nextSideSwap = 0f;
        private int _driftSide = 1;
        private float _driftYaw = 0f;
        private bool _driftYawValid = false;
        private const float SpeedrunSweepRate = 70f;   // deg/sec of continuous view rotation
        private bool _speedrunHopSafe = true;
        private bool _speedrunClimbing = false;
        private bool _speedrunThreatNear = false;
        private bool _speedrunRouteOpen = false;
        private const float SpeedrunSafeHopDistance = 14f;
        private const float SpeedrunLandingCheck = 22f;    // nothing in the landing arc, please
        private const float SpeedrunAbortDistance = 16f;   // drop the tech and manoeuvre normally
        private const float SpeedrunApproachDistance = 22f; // inside this, walk it in cleanly
        private const float SpeedrunMaxRouteTurn = 35f;     // arcing through corners loses the path

        /// <summary>Returns true while it is steering the camera itself (air-strafing).</summary>
        private bool ApplySpeedrunTech(Vector3 desiredWorldDir)
        {
            var mv = _cachedMovement;
            if (mv == null || desiredWorldDir.sqrMagnitude < 0.0001f) return false;

            // On a climb, drop all the tech and just run at the slope - jumping uphill converts
            // part of the jump backwards against you.
            if (_speedrunClimbing) { _driftYawValid = false; return false; }

            // Anything close by: full manual control, no tech. Committed high-speed
            // trajectories and enemies at arm's length don't mix.
            if (_speedrunThreatNear)
            {
                _driftYawValid = false;
                try { if (_cachedMovement.crouching) _cachedMovement.StopSlide(); } catch { }
                return false;
            }

            // Drift boosting travels on an arc, which is fine down a long straight but wrecks
            // navigation through a corner or on final approach - the bot sweeps past the
            // target, the route re-plans, and it looks lost. Only use the tech when there's
            // room for it, and walk normally otherwise.
            if (!_speedrunRouteOpen)
            {
                _driftYawValid = false;
                try { if (_cachedMovement.crouching) _cachedMovement.StopSlide(); } catch { }
                return false;
            }

            bool grounded = false, crouching = false, readyToJump = false;
            float slideThreshold = 8f;
            Vector3 vel = Vector3.zero;

            try
            {
                grounded = mv.grounded;
                crouching = mv.crouching;
                readyToJump = mv.readyToJump;
                slideThreshold = mv.slideThresholdSpeed;
                vel = mv.GetVelocity();
            }
            catch { return false; }

            Vector3 flatVel = new Vector3(vel.x, 0f, vel.z);
            float speed = flatVel.magnitude;

            if (grounded)
            {
                // Ground friction bleeds speed every frame we stay down, so leave immediately.
                // Bhop is disabled when a threat is close: airborne we can't change direction
                // much, and hopping blind is how it landed on an enemy's head and died.
                if (readyToJump && _speedrunHopSafe)
                {
                    try { mv.Jump(); } catch { }
                }

                // Sliding down a slope beats being airborne over it, and jumping off a downslope
                // converts part of the jump into forward speed. On the flat, sliding only adds
                // drag and cuts acceleration, so stand back up.
                if (Time.time >= _nextSlideToggle)
                {
                    bool descending = vel.y < -1.5f;
                    if (!crouching && descending && speed > slideThreshold * 0.6f)
                    {
                        try { mv.StartSlide(); _nextSlideToggle = Time.time + 0.25f; } catch { }
                    }
                    else if (crouching && !descending)
                    {
                        try { mv.StopSlide(); _nextSlideToggle = Time.time + 0.25f; } catch { }
                    }
                }

                // fall through - drift boosting works grounded too, just with friction
            }

            // Drift boost. Hold a diagonal (forward + strafe), and hold the camera at the
            // sweet-spot angle off the direction we are actually travelling. Speed accrues in
            // whichever direction the camera was turned, so the side we pick is also how we steer.
            Vector3 moveDir = speed > 2f ? flatVel.normalized : desiredWorldDir;
            float moveYaw = Mathf.Atan2(moveDir.x, moveDir.z) * Mathf.Rad2Deg;
            float wantYaw = Mathf.Atan2(desiredWorldDir.x, desiredWorldDir.z) * Mathf.Rad2Deg;
            float course = Mathf.DeltaAngle(moveYaw, wantYaw);

            // Drift boosting can only bend the course gently. Once we're badly off the route
            // it will never claw it back, so hand control to normal movement and re-acquire.
            if (Mathf.Abs(course) > 55f)
            {
                _driftYawValid = false;
                try { if (mv.crouching) mv.StopSlide(); } catch { }
                return false;
            }

            // Curve toward the target: positive course error means we need to bend right.
            // When we're already on course, alternate sides so the camera keeps turning -
            // a stationary turn angle earns nothing.
            int side;
            if (Mathf.Abs(course) > 8f)
            {
                side = course > 0f ? 1 : -1;
                _driftSide = side;
            }
            else
            {
                if (Time.time >= _nextSideSwap)
                {
                    _driftSide = -_driftSide;
                    _nextSideSwap = Time.time + SpeedrunSideSwapInterval;
                }
                side = _driftSide;
            }

            // The camera has to keep *turning* to earn speed - a fixed offset sits in the
            // sweet spot but generates nothing, which is why it was slow. Sweep the view
            // continuously in the drift direction and only let the movement catch up when the
            // turn angle reaches the top of the band, so we're always rotating within
            // roughly 30-58 degrees of our actual course.
            if (!_driftYawValid)
            {
                _driftYaw = moveYaw + SpeedrunSweetSpot * side;
                _driftYawValid = true;
            }

            float turnAngle = Mathf.DeltaAngle(moveYaw, _driftYaw);
            bool sweepingWithSide = Mathf.Sign(turnAngle) == side || Mathf.Abs(turnAngle) < 1f;

            // if the sweep ended up on the wrong side of our course, bring it back across
            float sweep = SpeedrunSweepRate * Time.deltaTime;
            if (!sweepingWithSide)
            {
                _driftYaw += side * sweep * 2f;
            }
            else if (Mathf.Abs(turnAngle) < SpeedrunMaxTurnAngle)
            {
                _driftYaw += side * sweep;
            }
            else
            {
                // at the edge of the band - hold and let the curving course close the gap,
                // which keeps the relative angle moving without overshooting into speed loss
                _driftYaw = moveYaw + SpeedrunMaxTurnAngle * side;
            }

            CameraYaw = _driftYaw;
            HasCameraHeading = true;

            // Apply the facing exactly rather than easing toward it. Acceleration is relative
            // to the camera, so a lagging camera means we accelerate somewhere other than
            // along our course - which is why it wandered off the route. The target yaw
            // tracks a smoothly-curving velocity, so applying it directly is still smooth;
            // use the chase camera (Num4) if you want the *view* decoupled from this.
            CameraSnap = true;

            DesiredMoveVertical = 1f;
            DesiredMoveHorizontal = -side;
            return true;
        }

        // ------------------------------------------------------------------
        // navigation: feel out walls, ledges and drops before walking into them
        // ------------------------------------------------------------------

        // True if something solid blocks a body-height move in this direction.
        // Whatever we're currently walking towards doesn't count as an obstacle - otherwise
        // the bot steers away from the very chest it's trying to reach.
        private bool IsBlocked(Vector3 playerPos, Vector3 dir, float distance)
        {
            if (_groundMask == 0) return false;
            try
            {
                Vector3 origin = playerPos + Vector3.up * 1.1f;
                if (!Physics.SphereCast(origin, _playerRadius * 0.85f, dir, out RaycastHit hit,
                                        distance, _groundMask))
                    return false;

                if (_navIgnoreTransform != null && hit.collider != null)
                {
                    var t = hit.collider.transform;
                    if (t == _navIgnoreTransform || t.IsChildOf(_navIgnoreTransform)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // True if there is floor to land on after stepping this way. Only a genuine void
        // counts - a step down or a slope must not read as a cliff, or the bot spins on
        // every bit of uneven ground.
        private bool HasGroundAhead(Vector3 playerPos, Vector3 dir, float distance)
        {
            if (_groundMask == 0) return true;
            try
            {
                // Sample the whole line, not just the ends. Checking only a near and a far
                // point meant a gap or a ledge in the middle of a long shortcut went unseen -
                // which is how corner-cutting an L-shaped route walked the bot off a drop.
                float stride = Mathf.Max(1.5f, distance / 8f);
                bool anyGround = false;

                for (float d = 1.2f; d <= distance + 0.01f; d += stride)
                {
                    Vector3 probe = playerPos + dir * d + Vector3.up * 1.0f;
                    if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit,
                                        1.0f + CliffProbeDrop, _groundMask))
                    {
                        anyGround = true;

                        // ground exists but far below us - that's a fall, not a route
                        if (hit.point.y < playerPos.y - MaxSafeStepDown) return false;
                    }
                    else if (d > 1.5f)
                    {
                        return false; // a hole partway along the line
                    }
                }

                return anyGround;
            }
            catch { return true; }
        }

        // The ground ahead can exist and still be unwalkable: Megabonk's maps are full of
        // mountain faces the player simply slides back down. Sample the surface normal and
        // reject anything steeper than the movement code itself allows.
        private bool IsSlopeWalkable(Vector3 playerPos, Vector3 dir, float distance)
        {
            if (_groundOnlyMask == 0) return true;
            try
            {
                // Stride scales with distance so a long line-of-sight check stays ~10 samples
                // instead of one per metre.
                float limit = _maxSlopeAngle + 4f;
                float stride = Mathf.Max(1f, distance / 10f);
                for (float d = 1.2f; d <= distance; d += stride)
                {
                    Vector3 probe = playerPos + dir * d + Vector3.up * 1.5f;
                    if (Physics.Raycast(probe, Vector3.down, out RaycastHit hit,
                                        1.5f + CliffProbeDrop, _groundOnlyMask))
                    {
                        if (Vector3.Angle(hit.normal, Vector3.up) > limit) return false;
                    }
                }
                return true;
            }
            catch { return true; }
        }

        private bool IsDirectionSafe(Vector3 playerPos, Vector3 dir, float distance)
            => IsDirectionSafe(playerPos, dir, distance, true);

        // terrainChecks=false when following a computed route: the grid already proved that
        // ground is walkable, and re-testing it here made the bot refuse its own path.
        private bool IsDirectionSafe(Vector3 playerPos, Vector3 dir, float distance, bool terrainChecks)
        {
            if (IsBlocked(playerPos, dir, distance)) return false;
            if (!terrainChecks) return true;
            if (!HasGroundAhead(playerPos, dir, distance)) return false;
            if (!IsSlopeWalkable(playerPos, dir, distance)) return false;
            return true;
        }

        // A knee-high obstruction with clear air above it is a ledge we can hop.
        // A wall is blocked at both heights - jumping there just scrapes the surface.
        private bool IsHoppableLedge(Vector3 playerPos, Vector3 dir)
        {
            if (_groundMask == 0) return false;
            try
            {
                Vector3 lowOrigin = playerPos + Vector3.up * 0.35f;
                Vector3 highOrigin = playerPos + Vector3.up * 1.7f;
                bool lowBlocked = Physics.SphereCast(lowOrigin, _playerRadius * 0.6f, dir,
                                                     out RaycastHit _, 1.5f, _groundMask);
                bool highBlocked = Physics.SphereCast(highOrigin, _playerRadius * 0.6f, dir,
                                                      out RaycastHit _, 1.8f, _groundMask);
                return lowBlocked && !highBlocked;
            }
            catch { return false; }
        }

        // Rotate away from whatever is in the way, preferring the smallest turn that clears.
        // Once a detour is chosen it is held for a short while: re-deciding every frame is
        // what made it pirouette next to walls and ledges.
        private Vector3 SteerAroundObstacles(Vector3 playerPos, Vector3 desiredDir)
        {
            if (desiredDir.sqrMagnitude < 0.0001f) return desiredDir;
            if (_groundMask == 0) return desiredDir;

            if (IsDirectionSafe(playerPos, desiredDir, ProbeDistance))
            {
                _steerCommitUntil = 0f; // path opened up, drop the detour
                return desiredDir;
            }

            // keep following the detour we already committed to, while it still works
            if (Time.time < _steerCommitUntil &&
                IsDirectionSafe(playerPos, _steerCommitDir, ProbeDistance))
            {
                return _steerCommitDir;
            }

            if (Time.time > _avoidSideUntil)
            {
                Vector3 probeSide = Quaternion.Euler(0f, 55f, 0f) * desiredDir;
                _avoidSide = IsDirectionSafe(playerPos, probeSide, ProbeDistance * 0.8f) ? 1 : -1;
                _avoidSideUntil = Time.time + 2.5f;
            }

            for (int i = 1; i < ProbeAngles.Length; i++)
            {
                float angle = ProbeAngles[i] * _avoidSide;

                Vector3 candidate = Quaternion.Euler(0f, angle, 0f) * desiredDir;
                if (IsDirectionSafe(playerPos, candidate, ProbeDistance))
                {
                    _steerCommitDir = candidate;
                    _steerCommitUntil = Time.time + SteerCommitTime;
                    return candidate;
                }

                Vector3 mirrored = Quaternion.Euler(0f, -angle, 0f) * desiredDir;
                if (IsDirectionSafe(playerPos, mirrored, ProbeDistance))
                {
                    _steerCommitDir = mirrored;
                    _steerCommitUntil = Time.time + SteerCommitTime;
                    _avoidSide = -_avoidSide;
                    _avoidSideUntil = Time.time + 2.5f;
                    return mirrored;
                }
            }

            // Fully boxed in. Commit to backing out for a beat rather than flip-flopping.
            _steerCommitDir = -desiredDir;
            _steerCommitUntil = Time.time + SteerCommitTime;
            return _steerCommitDir;
        }

        // ------------------------------------------------------------------
        // debug visuals: draw the computed route in-world, state readout on the HUD
        // ------------------------------------------------------------------

        private static Color PathStateColor(string state)
        {
            switch (state)
            {
                case "ok": return new Color(0.2f, 1f, 0.3f);
                case "partial": return new Color(1f, 0.85f, 0.15f);
                case "searching": return new Color(0.35f, 0.7f, 1f);
                case "direct": return new Color(0.4f, 1f, 1f);
                case "failed": return new Color(1f, 0.25f, 0.2f);
                default: return new Color(0.7f, 0.7f, 0.7f);
            }
        }

        private LineRenderer MakeLine(string name, float width, out GameObject go)
        {
            go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);

            var lr = go.AddComponent<LineRenderer>();
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Hidden/Internal-Colored");
            if (shader != null) lr.material = new Material(shader);

            lr.useWorldSpace = true;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCapVertices = 2;
            lr.positionCount = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }

        // Rebuilds the drawn route as a fixed-count, arc-length-resampled and rounded
        // polyline, then eases the rendered points toward it. Fixed count means every point
        // has a stable counterpart between frames, so a replan flows into place instead of
        // the whole line teleporting.
        private void BuildSmoothPathLine(Vector3 playerPos)
        {
            _rawPoints.Clear();
            _rawPoints.Add(playerPos + Vector3.up * 0.35f);
            for (int i = _pathIndex; i < _path.Count; i++)
                _rawPoints.Add(_path[i] + Vector3.up * 0.35f);

            if (_rawPoints.Count < 2)
            {
                _drawPoints.Clear();
                _pathLine.positionCount = 0;
                return;
            }

            // round off the grid's hard corners (Chaikin)
            for (int pass = 0; pass < 2; pass++)
            {
                _cornerCut.Clear();
                _cornerCut.Add(_rawPoints[0]);
                for (int i = 0; i < _rawPoints.Count - 1; i++)
                {
                    Vector3 a = _rawPoints[i], b = _rawPoints[i + 1];
                    _cornerCut.Add(Vector3.Lerp(a, b, 0.25f));
                    _cornerCut.Add(Vector3.Lerp(a, b, 0.75f));
                }
                _cornerCut.Add(_rawPoints[_rawPoints.Count - 1]);

                _rawPoints.Clear();
                _rawPoints.AddRange(_cornerCut);
            }

            // resample to a fixed number of evenly spaced points
            float total = 0f;
            for (int i = 0; i < _rawPoints.Count - 1; i++)
                total += Vector3.Distance(_rawPoints[i], _rawPoints[i + 1]);

            _resampled.Clear();
            if (total < 0.01f)
            {
                for (int i = 0; i < DrawPointCount; i++) _resampled.Add(_rawPoints[0]);
            }
            else
            {
                float step = total / (DrawPointCount - 1);
                int seg = 0;
                float segStart = 0f;

                for (int i = 0; i < DrawPointCount; i++)
                {
                    float want = Mathf.Min(step * i, total);
                    while (seg < _rawPoints.Count - 2)
                    {
                        float segLen = Vector3.Distance(_rawPoints[seg], _rawPoints[seg + 1]);
                        if (segStart + segLen >= want) break;
                        segStart += segLen;
                        seg++;
                    }

                    float len = Vector3.Distance(_rawPoints[seg], _rawPoints[seg + 1]);
                    float t = len < 0.0001f ? 0f : Mathf.Clamp01((want - segStart) / len);
                    _resampled.Add(Vector3.Lerp(_rawPoints[seg], _rawPoints[seg + 1], t));

                }
            }

            // ease the rendered points toward the new shape
            if (_drawPoints.Count != DrawPointCount)
            {
                _drawPoints.Clear();
                _drawPoints.AddRange(_resampled);
            }
            else
            {
                float k = 1f - Mathf.Exp(-Time.deltaTime * 12f); // frame-rate independent
                for (int i = 0; i < DrawPointCount; i++)
                    _drawPoints[i] = Vector3.Lerp(_drawPoints[i], _resampled[i], k);
            }

            // the head of the line should track the player exactly, not lag behind
            _drawPoints[0] = _resampled[0];

            _pathLine.positionCount = DrawPointCount;
            for (int i = 0; i < DrawPointCount; i++) _pathLine.SetPosition(i, _drawPoints[i]);
        }

        private void UpdateDebugVisuals(Vector3 playerPos)
        {
            try
            {
                if (!_debugVisuals)
                {
                    if (_pathLineGO != null) _pathLineGO.SetActive(false);
                    if (_goalLineGO != null) _goalLineGO.SetActive(false);
                    return;
                }

                if (_pathLine == null) _pathLine = MakeLine("MegabonkAI_Path", 0.18f, out _pathLineGO);
                if (_goalLine == null) _goalLine = MakeLine("MegabonkAI_Goal", 0.3f, out _goalLineGO);

                _pathLineGO.SetActive(true);
                _goalLineGO.SetActive(true);

                // ease the colour so state changes fade instead of snapping
                Color target = PathStateColor(_pathState);
                _drawColor = Color.Lerp(_drawColor, target, Time.deltaTime * 6f);
                _pathLine.startColor = _drawColor;
                _pathLine.endColor = _drawColor;

                if (_hasPath && _pathIndex < _path.Count)
                {
                    BuildSmoothPathLine(playerPos);
                    _pathLineHoldUntil = Time.time + 1.5f;
                }
                else if (Time.time > _pathLineHoldUntil)
                {
                    _drawPoints.Clear();
                    _pathLine.positionCount = 0;
                }
                // Otherwise keep the last route on screen briefly. Replans take a moment and
                // blanking the line every time made it flicker in and out.

                // goal: a vertical beam so it's visible over terrain
                if (_currentMode == "loot" || _currentMode == "explore")
                {
                    Vector3 goal = _pathGoal;
                    if (goal != Vector3.zero)
                    {
                        // glide to a new objective instead of snapping across the map
                        if (_smoothedGoal == Vector3.zero || Vector3.Distance(_smoothedGoal, goal) > 40f)
                            _smoothedGoal = goal;
                        else
                            _smoothedGoal = Vector3.Lerp(_smoothedGoal, goal, 1f - Mathf.Exp(-Time.deltaTime * 10f));

                        // gentle pulse so the marker reads as a beacon
                        float pulse = 3.5f + Mathf.Sin(Time.time * 3f) * 0.6f;

                        _goalLine.startColor = new Color(1f, 0.4f, 1f, 0.9f);
                        _goalLine.endColor = new Color(1f, 0.4f, 1f, 0.05f);
                        _goalLine.positionCount = 2;
                        _goalLine.SetPosition(0, _smoothedGoal);
                        _goalLine.SetPosition(1, _smoothedGoal + Vector3.up * pulse);
                    }
                    else _goalLine.positionCount = 0;
                }
                else
                {
                    _goalLine.positionCount = 0;
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Debug visuals failed, disabling: {ex.Message}");
                _debugVisuals = false;
            }
        }

        private Texture2D _pixel;

        private Texture2D Pixel
        {
            get
            {
                if (_pixel == null)
                {
                    _pixel = new Texture2D(1, 1);
                    _pixel.SetPixel(0, 0, Color.white);
                    _pixel.Apply();
                    _pixel.hideFlags = HideFlags.HideAndDontSave;
                }
                return _pixel;
            }
        }

        private void Fill(Rect r, Color c)
        {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Pixel);
            GUI.color = prev;
        }

        // Draws text with a dark offset copy behind it so it stays readable over bright
        // terrain, and sizes the font from the supplied pixel height.
        private void Text(Rect r, string s, float size, FontStyle weight, Color color, TextAnchor anchor)
        {
            if (string.IsNullOrEmpty(s)) return;

            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(9, Mathf.RoundToInt(size)),
                fontStyle = weight,
                alignment = anchor,
                richText = false,
                wordWrap = false
            };
            style.normal.textColor = new Color(0f, 0f, 0f, color.a * 0.75f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), s, style);

            style.normal.textColor = color;
            GUI.Label(r, s, style);
        }

        private static string ModeLabel(string mode)
        {
            switch (mode)
            {
                case "loot": return "LOOTING";
                case "explore": return "EXPLORING";
                case "charging": return "CHARGING SHRINE";
                case "evade": return "EVADING";
                case "evade-aoe": return "DODGING AOE";
                case "breakout": return "BREAKING OUT";
                case "boss-fight": return "FIGHTING BOSS";
                case "boss-retreat": return "BOSS — REGROUPING";
                default: return "STANDBY";
            }
        }

        public override void OnGUI()
        {
            if (!AiEnabled && !_debugVisuals) return;

            try
            {
                // Scale everything with the display, and give each line generous height -
                // fixed pixel sizes were tiny and clipped their own text on a big screen.
                float ui = Mathf.Clamp(Screen.height / 1080f, 0.9f, 2.4f);

                float pad = 18f * ui;
                float w = 400f * ui;
                float h = 174f * ui;   // room for the keybind row beneath the status row
                var panel = new Rect(22f * ui, Screen.height * 0.325f, w, h);

                Color accent = AiEnabled ? PathStateColor(_pathState) : new Color(0.62f, 0.62f, 0.68f);

                // near-opaque so text never fights the terrain behind it
                Fill(panel, new Color(0.03f, 0.04f, 0.06f, 0.93f));
                Fill(new Rect(panel.x, panel.y, panel.width, 1f * ui), new Color(1f, 1f, 1f, 0.16f));
                Fill(new Rect(panel.x, panel.yMax - 1f * ui, panel.width, 1f * ui), new Color(0f, 0f, 0f, 0.5f));
                Fill(new Rect(panel.x, panel.y, 4f * ui, panel.height), accent);

                float tx = panel.x + pad;
                float tw = panel.width - pad * 2f;
                float cy = panel.y + 12f * ui;

                // header
                float headerH = 20f * ui;
                Text(new Rect(tx, cy, tw, headerH),
                     SpeedrunMode ? "MEGABONK AI · SPEEDRUN" : "MEGABONK AI",
                     14f * ui, FontStyle.Bold,
                     SpeedrunMode ? new Color(1f, 0.8f, 0.3f) : new Color(1f, 1f, 1f, 0.7f),
                     TextAnchor.MiddleLeft);
                Text(new Rect(tx, cy, tw, headerH), AiEnabled ? "ACTIVE" : "PAUSED", 14f * ui, FontStyle.Bold,
                     AiEnabled ? new Color(0.45f, 1f, 0.55f) : new Color(1f, 0.5f, 0.5f), TextAnchor.MiddleRight);

                cy += headerH + 6f * ui;
                Fill(new Rect(tx, cy, tw, 1f * ui), new Color(1f, 1f, 1f, 0.14f));
                cy += 9f * ui;

                // current action
                float modeH = 30f * ui;
                Text(new Rect(tx, cy, tw, modeH), ModeLabel(_currentMode), 24f * ui, FontStyle.Bold,
                     Color.white, TextAnchor.MiddleLeft);
                cy += modeH + 2f * ui;

                float targetH = 22f * ui;
                Text(new Rect(tx, cy, tw, targetH), _currentTargetLabel, 15f * ui, FontStyle.Normal,
                     new Color(1f, 1f, 1f, 0.85f), TextAnchor.MiddleLeft);
                cy += targetH + 10f * ui;

                // route progress
                float barH = 5f * ui;
                Fill(new Rect(tx, cy, tw, barH), new Color(1f, 1f, 1f, 0.14f));
                if (_hasPath && _path.Count > 0)
                {
                    float frac = Mathf.Clamp01((float)_pathIndex / _path.Count);
                    Fill(new Rect(tx, cy, tw * frac, barH), accent);
                }
                cy += barH + 8f * ui;

                // Status and keybinds get their own rows - sharing one rect meant the
                // right-aligned keybind list printed straight over the left-aligned stats.
                float footH = 20f * ui;
                string route = _hasPath
                    ? $"{_pathState.ToUpperInvariant()} {_pathIndex}/{_path.Count}"
                    : _pathState.ToUpperInvariant();

                Text(new Rect(tx, cy, tw, footH),
                     $"{route}   AoE {_hazards.Count}   LOOT {_cachedLoot.Count}",
                     13f * ui, FontStyle.Normal, new Color(1f, 1f, 1f, 0.62f), TextAnchor.MiddleLeft);
                cy += footH;

                Text(new Rect(tx, cy, tw, footH), "NUM1 AI · NUM2 RUN · NUM3 VIS · NUM4 CAM",
                     12f * ui, FontStyle.Normal,
                     new Color(1f, 1f, 1f, 0.40f), TextAnchor.MiddleLeft);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // path following
        // ------------------------------------------------------------------

        // Drops the current route but deliberately leaves any in-flight search running.
        // Aborting here used to kill searches the moment a path was consumed or the bot
        // clipped something, so a new search was started and cancelled over and over - the
        // bot never had a route and just steered blindly (the stop-and-spin).
        private void InvalidatePath()
        {
            _hasPath = false;
            _path.Clear();
            _pathIndex = 0;
            _lastWaypointIndex = -1;
            _nextPlanAllowed = 0f; // let a fresh plan start immediately
        }

        private void AbortPathAndSearch()
        {
            InvalidatePath();
            if (_pathfinder.Status == SearchStatus.Searching) _pathfinder.Abort();
        }

        // Kicks off / advances the incremental search. The search spans several frames, so we
        // keep walking the previous route (or straight at the goal) while it thinks.
        private void UpdatePathSearch(Vector3 playerPos, Vector3 goal)
        {
            bool searching = _pathfinder.Status == SearchStatus.Searching;

            if (!searching)
            {
                bool needNew = !_hasPath
                               || Vector3.Distance(goal, _pathGoal) > GoalMovedTolerance
                               || Time.time - _pathPlannedAt > PathRefreshInterval
                               || _pathIndex >= _path.Count;

                if (needNew && Time.time >= _nextPlanAllowed)
                {
                    _nextPlanAllowed = Time.time + PlanCooldown;
                    _pathGoal = goal;
                    _goalProvenUnreachable = false;
                    _pathfinder.DiagnosticsRemaining = 3; // sample a few rejects per search
                    _pathfinder.BeginSearch(playerPos, goal);
                    if (_pathfinder.Status == SearchStatus.Failed) OnSearchFailed();
                    searching = _pathfinder.Status == SearchStatus.Searching;
                    if (searching) _pathState = "searching";
                }
            }

            if (!searching) return;

            var status = _pathfinder.StepSearch(SearchMillisPerFrame);
            if (status == SearchStatus.Succeeded)
            {
                _path.Clear();
                for (int i = 0; i < _pathfinder.ResultPath.Count; i++) _path.Add(_pathfinder.ResultPath[i]);
                _pathIndex = 0;
                _hasPath = _path.Count > 0;
                _pathPlannedAt = Time.time;
                _pathFailures = 0;
                _pathState = _pathfinder.ResultIsPartial ? "partial" : "ok";
                _goalProvenUnreachable = _pathfinder.ExhaustedWithoutGoal;
            }
            else if (status == SearchStatus.Failed)
            {
                _goalProvenUnreachable = _pathfinder.ExhaustedWithoutGoal;
                OnSearchFailed();
            }
        }

        private void OnSearchFailed()
        {
            _pathFailures++;
            _pathState = "failed";
            _hasPath = false;
            _path.Clear();
            _pathIndex = 0;
            _pathPlannedAt = Time.time;
        }

        /// <summary>
        /// Direction to travel to reach goal, routed around terrain. Falls back to a straight
        /// line (with reactive steering downstream) when no route can be computed.
        /// </summary>
        private Vector3 DirectionToGoal(Vector3 playerPos, Vector3 goal)
        {
            Vector3 direct = goal - playerPos;
            direct.y = 0f;
            float directDist = direct.magnitude;
            if (directDist < 0.05f) return Vector3.zero;
            direct /= directDist;

            // "Arrived" has to account for height. Measuring flat distance meant something on
            // a ledge directly overhead read as 1m away, so the bot declared arrival and stood
            // underneath it forever.
            float verticalGap = Mathf.Abs(goal.y - playerPos.y);
            if (verticalGap > 3f) directDist = Mathf.Max(directDist, verticalGap);

            // close enough to just walk at it
            if (directDist < 4f)
            {
                _pathState = "direct";
                AbortPathAndSearch(); // we've arrived; nothing left to plan
                return direct;
            }

            UpdatePathSearch(playerPos, goal);

            if (!_hasPath) return direct; // reactive steering carries us while the search runs

            // Snap to the nearest point on the route ahead of us, rather than only advancing
            // when a waypoint is "reached". Momentum carries the player past waypoints, and
            // chasing one that is now behind is what made it turn around and spin.
            Vector3 flatPlayer = new Vector3(playerPos.x, 0f, playerPos.z);

            // Waypoint tolerance grows with speed - at 20 m/s the bot crosses a 2m grid cell
            // in a tenth of a second and rounds corners wide.
            float travelSpeed = 0f;
            try { if (_cachedMovement != null) travelSpeed = _cachedMovement.GetSpeedHorizontal(); } catch { }
            float reachRadius = WaypointRadius + Mathf.Clamp(travelSpeed * 0.2f, 0f, 3f);

            // Walk the route in order, consuming waypoints we've reached or gone past.
            //
            // This used to snap to whichever of the next ten waypoints was geometrically
            // nearest, which is wrong precisely at corners: a waypoint on the far side of a
            // wall is often the closest in a straight line, so the index leapt across the
            // corner and the bot drove at the wall instead of going round it. Sequential
            // advancement can't skip the turn.
            int guard = 0;
            while (_pathIndex < _path.Count - 1 && guard++ < 12)
            {
                Vector3 wp = new Vector3(_path[_pathIndex].x, 0f, _path[_pathIndex].z);
                float d = Vector3.Distance(flatPlayer, wp);

                if (d <= reachRadius) { _pathIndex++; continue; }

                // also consume it if we've travelled beyond it along the route
                Vector3 next = new Vector3(_path[_pathIndex + 1].x, 0f, _path[_pathIndex + 1].z);
                Vector3 leg = next - wp;
                if (leg.sqrMagnitude > 0.01f && Vector3.Dot(flatPlayer - wp, leg.normalized) > 0f)
                {
                    _pathIndex++;
                    continue;
                }

                break;
            }

            // A waypoint that refuses to get closer shouldn't hold us hostage.
            if (_pathIndex != _lastWaypointIndex)
            {
                _lastWaypointIndex = _pathIndex;
                _waypointEnteredAt = Time.time;
            }
            else if (Time.time - _waypointEnteredAt > (SpeedrunMode ? 4f : 2f))
            {
                // Soft-stuck: we're still moving (sliding along a ridge or grinding a corner)
                // but the route isn't advancing. The geometry claimed this was walkable and it
                // isn't, so ban the spot and let the search find a way round.
                _pathfinder.BlockAround(_path[Mathf.Min(_pathIndex, _path.Count - 1)], 2.5f, 45f);
                _pathfinder.BlockAround(playerPos, 1.5f, 20f);
                TryJump(); // a lip we could hop still gets one attempt

                LoggerInstance.Msg($"Soft-stuck at waypoint {_pathIndex}/{_path.Count} - " +
                                   "blocking that ground and rerouting.");

                // count it against whatever we were heading for, so a target that keeps
                // stranding us gets dropped instead of retried from another angle
                if (_currentMode == "loot" && _progressTargetId != 0)
                    StrikeLoot(_progressTargetId, "soft-stuck en route");

                InvalidatePath();
                _lastWaypointIndex = -1;
                _waypointEnteredAt = Time.time;
                return direct;
            }

            if (_pathIndex >= _path.Count)
            {
                InvalidatePath();
                return direct;
            }

            // String-pulling: aim at the furthest waypoint with a genuinely clear shot. The
            // terrain checks matter here - skipping them let it cut corners straight into
            // sloped ground, which is where it kept getting hung up.
            // Look further ahead at speed so the line to follow is a long smooth one rather
            // than a series of short corrections the arc can't keep up with.
            int lookAhead = SpeedrunMode ? 6 : 4;  // each candidate now costs real raycasts
            int aim = _pathIndex;
            for (int i = _pathIndex + 1; i < Mathf.Min(_pathIndex + lookAhead, _path.Count); i++)
            {
                Vector3 candidate = _path[i] - playerPos;
                candidate.y = 0f;
                float d = candidate.magnitude;
                if (d < 0.1f) continue;

                // Probe the WHOLE way to the candidate. This used to be capped at a few metres,
                // so a waypoint 20m off was declared reachable after checking the first 5m -
                // the shortcut then cut the corner straight into the wall the route was
                // carefully going around, which is exactly the "turns too early" behaviour.
                if (IsDirectionSafe(playerPos, candidate / d, Mathf.Min(d, 30f), true))
                    aim = i;
                else
                    break; // once the line of sight is broken, further ones don't count
            }

            _followingPath = true;

            Vector3 dir = _path[aim] - playerPos;
            dir.y = 0f;
            return dir.sqrMagnitude > 0.0001f ? dir.normalized : direct;
        }

        // ------------------------------------------------------------------
        // movement
        // ------------------------------------------------------------------

        private void UpdateMovement()
        {
            var playerInstance = MyPlayer.Instance;
            if (playerInstance == null)
            {
                DesiredMoveHorizontal = 0f;
                DesiredMoveVertical = 0f;
                return;
            }

            var playerGO = playerInstance.gameObject;
            EnsurePlayerComponents(playerGO);

            var orientation = _cachedMovement != null ? _cachedMovement.orientation : null;
            if (orientation == null)
            {
                DesiredMoveHorizontal = 0f;
                DesiredMoveVertical = 0f;
                return;
            }

            Vector3 playerPos = playerGO.transform.position;

            // Settle before acting. On the first frames after a run starts the collision
            // masks, inventory and player transform are still coming up, so any objective
            // picked here is based on nothing - which is what sent it wandering in circles
            // with the camera pointed at the sky.
            if (_groundMask == 0 || Time.time - _aiEnabledAt < StartupSettleTime)
            {
                DesiredMoveHorizontal = 0f;
                DesiredMoveVertical = 0f;
                HasCameraHeading = false;
                _currentMode = "init";
                _currentTargetLabel = "waiting for world";
                InvalidatePath();
                _hasExploreTarget = false;
                _pathGoal = playerPos;
                UpdateDebugVisuals(playerPos);
                return;
            }

            MarkVisited(playerPos);
            UpdateLoopDetection(playerPos);

            if (Time.time >= _nextEnemyScan)
            {
                _cachedEnemies = UnityEngine.Object.FindObjectsOfType<Enemy>();
                _nextEnemyScan = Time.time + EnemyRefreshInterval;
            }

            if (Time.time >= _nextLootScan)
            {
                RefreshLoot(playerPos);
                _nextLootScan = Time.time + LootRefreshInterval;
            }

            float hpPct = GetHealthPercent();
            float panicRadius = hpPct < LowHpThreshold ? PanicRadiusLowHp : PanicRadiusBase;

            // --- threat field ---
            Vector3 repel = Vector3.zero;
            Vector3 nearSum = Vector3.zero;
            int nearCount = 0;
            float closest = float.MaxValue;

            float closestBoss = float.MaxValue;
            Vector3 bossPos = Vector3.zero;
            Vector3 bossRepel = Vector3.zero;

            foreach (var enemy in _cachedEnemies)
            {
                if (enemy == null) continue;
                bool dead = true;
                try { dead = enemy.IsDead(); } catch { }
                if (dead) continue;

                Vector3 enemyPos;
                try { enemyPos = enemy.GetCenterPosition(); } catch { continue; }

                Vector3 toPlayer = playerPos - enemyPos;
                toPlayer.y = 0f;
                float dist = toPlayer.magnitude;
                if (dist < 0.01f) dist = 0.01f;
                if (dist < closest) closest = dist;

                // Bosses and elites hit far harder than trash - give them a much wider berth
                // and a stronger shove away, so the bot kites them instead of brushing past.
                bool boss = false, elite = false;
                try { boss = enemy.IsBoss(); } catch { }
                if (!boss) { try { elite = enemy.IsElite(); } catch { } }

                if (boss)
                {
                    // tracked separately - when duelling, the kite ring governs our distance
                    // to the boss and a blanket repulsion would just push us out of range
                    if (dist < closestBoss)
                    {
                        closestBoss = dist;
                        bossPos = enemyPos;
                    }
                    if (dist < BossPanicRadius)
                    {
                        float bw = (BossPanicRadius - dist) / BossPanicRadius;
                        bossRepel += (toPlayer / dist) * bw * bw * 3f;
                    }
                    continue;
                }

                float radius = elite ? panicRadius * 1.6f : panicRadius;
                float strength = elite ? 1.8f : 1f;

                if (dist < radius)
                {
                    float weight = (radius - dist) / radius;
                    repel += (toPlayer / dist) * weight * weight * strength;
                }
                else if (dist < EngageRadius)
                {
                    nearSum += enemyPos;
                    nearCount++;
                }
            }

            if (Time.time >= _nextHazardScan)
            {
                RefreshHazards();
                _nextHazardScan = Time.time + HazardRefreshInterval;
            }

            Vector3 hazardPush = ComputeHazardAvoidance(playerPos, out bool insideHazard);
            _lastRepel = repel + bossRepel + hazardPush * 2f;

            // Evade is reserved for genuine emergencies. Ordinary enemy pressure is a steering
            // influence on top of the objective, not a mode that throws the route away.
            //
            // Mid-charge the bar is higher still: stepping out of a charge shrine's zone
            // cancels its progress, so normal enemy pressure must not break the hold.
            bool charging = Time.time - _lastChargeHoldTime < 0.5f;
            float criticalRadius = charging ? ChargeCriticalRadius : CriticalRadius;
            float lowHp = charging ? ChargeLowHpThreshold : LowHpThreshold;

            // Boss in range: duel it from a distance rather than run. Standing in an AoE is
            // still never acceptable, so hazards outrank the duel.
            RefreshKiteRing();
            bool duelBoss = closestBoss < BossEngageRadius && !insideHazard;

            // Duelling used to suppress evasion entirely, so the bot happily kited the boss
            // straight through a pack of trash. Trash still triggers an escape mid-duel, just
            // at a tighter radius so an ordinary swarm doesn't cancel the whole fight.
            float trashCritical = duelBoss ? criticalRadius * 0.7f : criticalRadius;

            bool evade = insideHazard
                         || closest < trashCritical
                         || (hpPct < lowHp && repel.sqrMagnitude > 0.0001f);

            Vector3 desiredWorldDir;
            _navIgnoreTransform = null;
            _followingPath = false;
            float goalDistance = float.MaxValue;

            if (!evade && duelBoss)
            {
                // Hold a ring around the boss at roughly weapon range: push out when it closes,
                // drift in when it drifts away, and circle constantly so we're never a
                // stationary target. Wounded, the ring widens until we've recovered.
                float ring = hpPct < BossRetreatHp ? _kiteRing * 1.8f : _kiteRing;
                float inner = ring * 0.8f;
                float outer = ring * 1.2f;

                Vector3 toBoss = bossPos - playerPos;
                toBoss.y = 0f;
                float bossDist = Mathf.Max(toBoss.magnitude, 0.01f);
                Vector3 dirToBoss = toBoss / bossDist;

                Vector3 tangent = new Vector3(-dirToBoss.z, 0f, dirToBoss.x) * (_avoidSide >= 0 ? 1f : -1f);

                float radial;
                if (bossDist < inner) radial = -Mathf.Clamp01((inner - bossDist) / inner) * 2f; // back off hard
                else if (bossDist > outer) radial = Mathf.Clamp01((bossDist - outer) / ring);   // ease in
                else radial = 0f;

                Vector3 steerBoss = dirToBoss * radial + tangent * 0.9f;

                // Trash and hazards carry real weight here - the ring keeps us off the boss,
                // but nothing else was stopping us backpedalling into a mob of adds.
                steerBoss += repel * 2.2f + hazardPush * 2.5f;

                // and if adds are gathering, favour circling toward open ground
                if (nearCount > 2) steerBoss += tangent * 0.4f;

                desiredWorldDir = steerBoss.sqrMagnitude > 0.0001f
                    ? steerBoss.normalized
                    : tangent;

                // Orbiting is a purely geometric circle, so it happily walks the bot into a
                // tree or a rock and holds it there while the boss catches up. If the way
                // round is obstructed, re-pick using clearance and reverse the orbit next
                // time so we don't grind the same obstacle again.
                if (RunwayLength(playerPos, desiredWorldDir, 4f) < 3.5f)
                {
                    desiredWorldDir = ChooseOpenDirection(playerPos, desiredWorldDir, 5f);
                    if (Time.time >= _nextOrbitFlip)
                    {
                        _avoidSide = -_avoidSide;
                        _nextOrbitFlip = Time.time + 2f;
                    }
                }

                _currentMode = hpPct < BossRetreatHp ? "boss-retreat" : "boss-fight";
                _currentTargetLabel = $"boss {bossDist:0}m / ring {ring:0}m";
                InvalidatePath();
            }
            else if (evade)
            {
                // Escape toward genuine open space, and keep the route we were on - it resumes
                // once we're clear instead of every scare cancelling the trip to a chest.
                Vector3 fleeDir = ChooseEscapeDirection(playerPos);
                if (fleeDir.sqrMagnitude < 0.0001f) fleeDir = Vector3.forward;

                // Cornered: every direction is short on room, so retreating just presses us
                // further into the wall while the horde closes. Break out through the widest
                // gap in the enemies instead, ignoring the instinct to back away, and hop if
                // it's something we can clear.
                if (_lastRunway < 3f)
                {
                    fleeDir = ChooseOpenDirection(playerPos, Vector3.zero, 0f);
                    if (IsHoppableLedge(playerPos, fleeDir)) TryJump();
                    _currentMode = "breakout";
                    _currentTargetLabel = "cornered";
                }

                // If the escape means reversing, dump momentum so it actually turns instead of
                // sliding onward into what it's trying to avoid.
                if (_smoothedMoveDir.sqrMagnitude > 0.0001f &&
                    Vector3.Dot(_smoothedMoveDir.normalized, fleeDir) < 0.2f)
                {
                    FastStop();
                }

                desiredWorldDir = fleeDir;

                if (insideHazard)
                {
                    _currentMode = "evade-aoe";
                    _currentTargetLabel = $"{_hazards.Count} area attacks";
                }
                else
                {
                    _currentMode = "evade";
                    _currentTargetLabel = "danger close";
                }
            }
            else
            {
                // Pursue the objective; threats bend the direction rather than replacing it.
                Vector3 objective = Vector3.zero;

                if (TryGetBestLoot(playerPos, out LootTarget loot))
                {
                    Vector3 toLoot = loot.Position - playerPos;
                    toLoot.y = 0f;

                    _currentMode = "loot";
                    _currentTargetLabel = $"{loot.Kind}@{toLoot.magnitude:0}m";
                    goalDistance = toLoot.magnitude;
                    try { _navIgnoreTransform = loot.Tf; } catch { }

                    // Are we actually making headway? Straight-line distance ignores mountains,
                    // so this is what stops it grinding against a cliff under a chest.
                    float trueDist = Vector3.Distance(playerPos, loot.Position);
                    if (loot.Id != _progressTargetId)
                    {
                        _progressTargetId = loot.Id;
                        _progressBestDist = trueDist;
                        _progressLastImprove = Time.time;
                    }
                    else if (trueDist < _progressBestDist - 0.5f)
                    {
                        _progressBestDist = trueDist;
                        _progressLastImprove = Time.time;
                    }
                    else if (Time.time - _progressLastImprove > NoProgressTimeout)
                    {
                        _progressTargetId = 0;
                        _hasExploreTarget = false;
                        LoggerInstance.Msg($"No progress toward {loot.Kind} for {NoProgressTimeout}s " +
                                           $"(stalled at {trueDist:0}m).");
                        StrikeLoot(loot.Id, "no progress");
                    }

                    // 3D reach test - something on a ledge above us is not "reached" just
                    // because we're standing at the foot of the cliff.
                    float reachDist = Vector3.Distance(playerPos, loot.Position);
                    if (reachDist <= LootReachedRadius)
                    {
                        if (_standingOnLootId != loot.Id)
                        {
                            _standingOnLootId = loot.Id;
                            _standingOnLootSince = Time.time;
                        }
                        else if (!loot.HoldToUse && Time.time - _standingOnLootSince > 2.5f)
                        {
                            // Ordinary loot should have reacted by now - it isn't grabbable.
                            _lootBlacklist[loot.Id] = Time.time + LootBlacklistTime;
                            if (_committedLootId == loot.Id) _committedLootId = 0;
                            _standingOnLootId = 0;
                            LoggerInstance.Msg("Loot didn't respond after 2.5s - skipping it.");
                        }
                    }
                    else if (_standingOnLootId == loot.Id)
                    {
                        _standingOnLootId = 0;
                    }

                    // A charge shrine only charges while we're inside its trigger zone, so hold
                    // position until it completes. Trust the shrine's own `charging` flag rather
                    // than our distance to its origin - the zone is offset from the transform,
                    // and standing "close enough" was parking the bot just outside it at 0%.
                    if (loot.HoldToUse)
                    {
                        var shrine = loot.Interactable != null ? loot.Interactable.TryCast<ChargeShrine>() : null;
                        bool done = false, inZone = false;
                        float progress = 0f;
                        try
                        {
                            if (shrine != null)
                            {
                                done = shrine.completed;
                                inZone = shrine.charging;
                                progress = shrine.chargeProgress;
                            }
                        }
                        catch { }

                        if (!done && inZone)
                        {
                            if (Time.time - _standingOnLootSince > ChargeHoldTimeout)
                            {
                                _lootBlacklist[loot.Id] = Time.time + LootBlacklistTime;
                            if (_committedLootId == loot.Id) _committedLootId = 0;
                                _chargeLockId = 0;
                                LoggerInstance.Msg("Charge shrine didn't complete in time - moving on.");
                            }
                            else
                            {
                                _currentMode = "charging";
                                _currentTargetLabel = $"{loot.Kind} {progress:P0}";
                                _lastChargeHoldTime = Time.time; // relaxes the evade threshold
                                _chargeLockId = loot.Id;         // nothing else may steal us now
                                DesiredMoveHorizontal = 0f;
                                DesiredMoveVertical = 0f;
                                UpdateDebugVisuals(playerPos);
                                return; // hold still; a real emergency above still pulls us out
                            }
                        }
                        else if (done)
                        {
                            // Announce once only: the loot list is rescanned once a second, so
                            // this branch is re-entered every frame until the shrine drops out.
                            if (!_lootBlacklist.ContainsKey(loot.Id))
                                LoggerInstance.Msg("Charge shrine complete.");

                            _lootBlacklist[loot.Id] = Time.time + 9999f; // done, never revisit
                            if (_committedLootId == loot.Id) _committedLootId = 0;
                            _chargeLockId = 0;
                        }
                        // Not done and not yet inside the zone: keep walking in. This branch
                        // used to be lumped in with "done", so every shrine was declared
                        // complete and blacklisted the moment we got near it - which is why
                        // the counter never moved despite the log saying otherwise.
                    }

                    objective = DirectionToGoal(playerPos, loot.Position);

                    // No route exists at all (island, peak with no ramp) - don't burn the
                    // watchdog's six seconds walking into it, drop it now.
                    if (_pathState == "failed" && _pathFailures >= 3)
                    {
                        _lootBlacklist[loot.Id] = Time.time + UnreachableBlacklistTime;
                            if (_committedLootId == loot.Id) _committedLootId = 0;
                        _pathFailures = 0;
                        _progressTargetId = 0;
                        LoggerInstance.Msg($"No path to {loot.Kind} at {toLoot.magnitude:0}m - skipping it.");
                    }
                    else if (_goalProvenUnreachable)
                    {
                        // The search walked every cell it could reach and never got there -
                        // that's a definitive no, typically loot on a peak with no ramp.
                        _lootBlacklist[loot.Id] = Time.time + UnreachableBlacklistTime;
                            if (_committedLootId == loot.Id) _committedLootId = 0;
                        _goalProvenUnreachable = false;
                        _progressTargetId = 0;
                        _partialTargetId = 0;
                        LoggerInstance.Msg($"{loot.Kind} at {toLoot.magnitude:0}m is cut off " +
                                           $"(no route: {_pathfinder.ReachableCells} cells reachable, " +
                                           $"sample: {_pathfinder.LastDiagnostic}) - skipping.");
                    }
                    else if (_pathState == "partial" && _hasPath && _path.Count > 0)
                    {
                        // A partial route that stops well short means the search could not
                        // connect to the target at all - typically loot on a peak. Walking the
                        // partial route just parks us at the foot of the mountain.
                        // Be conservative here: a long route can legitimately come back partial
                        // simply because the search ran out of budget that tick. Bailing after
                        // 4s / 12m was blacklisting perfectly reachable chests and leaving the
                        // bot with an empty loot list.
                        float endGap = Vector3.Distance(_path[_path.Count - 1], loot.Position);
                        if (endGap > 20f)
                        {
                            if (_partialTargetId != loot.Id)
                            {
                                _partialTargetId = loot.Id;
                                _partialSince = Time.time;
                            }
                            else if (Time.time - _partialSince > 9f)
                            {
                                _lootBlacklist[loot.Id] = Time.time + 45f;
                            if (_committedLootId == loot.Id) _committedLootId = 0;
                                _partialTargetId = 0;
                                _progressTargetId = 0;
                                LoggerInstance.Msg($"Route to {loot.Kind} only reaches within " +
                                                   $"{endGap:0}m after 9s - parking it for now.");
                            }
                        }
                        else _partialTargetId = 0;
                    }
                    else if (_partialTargetId == loot.Id)
                    {
                        _partialTargetId = 0;
                    }
                }
                else
                {
                    if (!_hasExploreTarget || Time.time > _exploreTargetExpiry ||
                        Vector3.Distance(new Vector3(playerPos.x, 0f, playerPos.z),
                                         new Vector3(_exploreTarget.x, 0f, _exploreTarget.z)) < ExploreReachedRadius)
                    {
                        PickExploreTarget(playerPos);
                    }

                    Vector3 toTarget = _exploreTarget - playerPos;
                    toTarget.y = 0f;
                    objective = DirectionToGoal(playerPos, _exploreTarget);

                    _currentMode = "explore";
                    _currentTargetLabel = $"{toTarget.magnitude:0}m away";

                    if (_goalProvenUnreachable || (_pathState == "failed" && _pathFailures >= 3))
                    {
                        _hasExploreTarget = false; // cut off from here, pick somewhere else
                        _goalProvenUnreachable = false;
                        _pathFailures = 0;
                    }
                }

                // Blend the objective with threat avoidance. Hazards outweigh enemies, and
                // enemies only bend the course - they no longer cancel it.
                Vector3 steer = objective + repel * 1.4f + bossRepel * 2f + hazardPush * 2.5f;

                // Circling flavour: while crossing open ground near a pack, drift sideways
                // around them instead of walking through. Keeps it safe and watchable without
                // abandoning where we were going.
                if (nearCount > 0 && objective.sqrMagnitude > 0.0001f)
                {
                    Vector3 centroid = nearSum / nearCount;
                    Vector3 toCentroid = centroid - playerPos;
                    toCentroid.y = 0f;

                    if (toCentroid.sqrMagnitude > 0.01f)
                    {
                        Vector3 dirToCentroid = toCentroid.normalized;
                        Vector3 tangent = new Vector3(-dirToCentroid.z, 0f, dirToCentroid.x);
                        if (Vector3.Dot(tangent, objective) < 0f) tangent = -tangent; // circle the way we're headed

                        float closeness = Mathf.Clamp01((EngageRadius - toCentroid.magnitude) / EngageRadius);
                        steer += tangent * closeness * 0.7f;
                    }
                }

                desiredWorldDir = steer;
                if (desiredWorldDir.sqrMagnitude > 0.0001f) desiredWorldDir.Normalize();
            }

            // Right on top of the goal, walk straight in - obstacle probes at this range are
            // mostly hitting the target itself and just make it circle the prize.
            //
            // While following a computed route, only intervene if something is genuinely in
            // the way at close range: the route is walkable by construction, and running the
            // full terrain probes over it made the bot argue with its own path and spin.
            if (goalDistance > SteerFreeRadius)
            {
                if (_followingPath)
                {
                    if (IsBlocked(playerPos, desiredWorldDir, ProbeDistance * 0.6f))
                        desiredWorldDir = SteerAroundObstacles(playerPos, desiredWorldDir);
                }
                else
                {
                    desiredWorldDir = SteerAroundObstacles(playerPos, desiredWorldDir);
                }
            }

            desiredWorldDir = ApplyStuckHandling(playerPos, desiredWorldDir);

            // Cap how fast the heading can swing. Without this a single frame of conflicting
            // steering flips the character 180 degrees, which is what reads as spinning.
            if (desiredWorldDir.sqrMagnitude > 0.0001f)
            {
                if (_smoothedMoveDir.sqrMagnitude < 0.0001f)
                {
                    _smoothedMoveDir = desiredWorldDir;
                }
                else
                {
                    float turnRate = evade ? EvadeTurnRate : NormalTurnRate;
                    _smoothedMoveDir = Vector3.RotateTowards(
                        _smoothedMoveDir.normalized,
                        desiredWorldDir.normalized,
                        turnRate * Time.deltaTime,
                        1f);
                }
                desiredWorldDir = _smoothedMoveDir.normalized;
            }

            if (Time.time >= _nextStatusLog)
            {
                _nextStatusLog = Time.time + 4f;
                float goldNow = GetGold();
                int blacklisted = 0;
                foreach (var kv in _lootBlacklist) if (Time.time < kv.Value) blacklisted++;

                // Show what the picker is actually weighing, so a wrong choice is visible
                // instead of inferred.
                var ranked = new List<(string kind, float dist, float score, bool low)>();
                foreach (var l in _cachedLoot)
                {
                    if (l.Tf == null) continue;
                    float d = Vector3.Distance(new Vector3(playerPos.x, 0f, playerPos.z),
                                               new Vector3(l.Position.x, 0f, l.Position.z));
                    float s = l.Value / (d * l.DistWeight + 4f);
                    float climb = l.Position.y - playerPos.y;
                    if (climb > 3f) s *= Mathf.Clamp(1f - (climb - 3f) / 20f, 0.15f, 1f);
                    ranked.Add((l.Kind, d, s, l.LowPriority));
                }
                ranked.Sort((a, b) => b.score.CompareTo(a.score));

                var top = new System.Text.StringBuilder();
                for (int i = 0; i < Mathf.Min(4, ranked.Count); i++)
                    top.Append($"{ranked[i].kind}@{ranked[i].dist:0}m={ranked[i].score:0.0}{(ranked[i].low ? "(low)" : "")}  ");
                if (ranked.Count > 0) LoggerInstance.Msg($"  candidates: {top}");

                LoggerInstance.Msg($"[{_currentMode}] target={_currentTargetLabel} path={_pathState}" +
                                   $"({_pathIndex}/{_path.Count}) nodes={_pathfinder.Expansions} " +
                                   $"cache={_pathfinder.CacheSize} loot={_cachedLoot.Count} " +
                                   $"blacklisted={blacklisted} gold={goldNow:0} " +
                                   $"costlyChests={_skippedChestsNoGold} hp={hpPct:P0}");
                _skippedChestsNoGold = 0;
            }

            if (desiredWorldDir.sqrMagnitude < 0.0001f)
            {
                DesiredMoveHorizontal = 0f;
                DesiredMoveVertical = 0f;
                return;
            }

            Vector3 fwd = orientation.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = orientation.right; right.y = 0f; right.Normalize();

            DesiredMoveHorizontal = Mathf.Clamp(Vector3.Dot(desiredWorldDir, right), -1f, 1f);
            DesiredMoveVertical = Mathf.Clamp(Vector3.Dot(desiredWorldDir, fwd), -1f, 1f);

            // Speedrun tech runs last so it can override the plain walking input with the
            // bhop / air-strafe pattern while still following the same computed direction.
            // Don't hop while anything is near enough to land on, or while escaping - in the
            // air the bot can barely change course, and a blind hop is what got it killed.
            //
            // Also don't hop up a climb: each jump kills forward progress against the slope,
            // so gradients are covered far faster by simply running up them.
            bool climbing = false;
            if (_hasPath && _pathIndex < _path.Count)
            {
                Vector3 ahead = _path[Mathf.Min(_pathIndex + 2, _path.Count - 1)];
                Vector3 flatAhead = ahead - playerPos;
                float horiz = new Vector2(flatAhead.x, flatAhead.z).magnitude;
                climbing = horiz > 1f && (flatAhead.y / horiz) > 0.18f; // steeper than ~10 degrees
            }

            // A hop commits us to a ballistic arc we cannot steer, so check the landing zone
            // specifically: anything roughly ahead of us within jump range is a collision.
            bool enemyAhead = false;
            if (desiredWorldDir.sqrMagnitude > 0.0001f)
            {
                Vector3 travel = desiredWorldDir.normalized;
                foreach (var enemy in _cachedEnemies)
                {
                    if (enemy == null) continue;
                    bool dead = true;
                    try { dead = enemy.IsDead(); } catch { }
                    if (dead) continue;

                    Vector3 ep;
                    try { ep = enemy.GetCenterPosition(); } catch { continue; }

                    Vector3 to = ep - playerPos;
                    to.y = 0f;
                    float d = to.magnitude;
                    if (d > SpeedrunLandingCheck || d < 0.01f) continue;
                    if (Vector3.Dot(to / d, travel) > 0.45f) { enemyAhead = true; break; }
                }
            }

            _speedrunHopSafe = !evade && !climbing && !enemyAhead
                               && closest > SpeedrunSafeHopDistance && !insideHazard;
            _speedrunClimbing = climbing;

            // At speed the bot commits to a trajectory it can barely alter, so anything within
            // this radius means dropping the tech entirely and moving normally - that's what
            // it was doing when it hopped onto an enemy and died.
            _speedrunThreatNear = closest < SpeedrunAbortDistance || evade || insideHazard;

            // The tech is only worth using on a long, straight, open run. Close to the target
            // or mid-corner, arcing costs more than the speed gains.
            float goalDist = _pathGoal != Vector3.zero
                ? Vector3.Distance(new Vector3(playerPos.x, 0f, playerPos.z),
                                   new Vector3(_pathGoal.x, 0f, _pathGoal.z))
                : 0f;

            float routeTurn = 0f;
            if (_pathGoal != Vector3.zero && desiredWorldDir.sqrMagnitude > 0.0001f)
            {
                Vector3 toGoal = _pathGoal - playerPos;
                toGoal.y = 0f;
                if (toGoal.sqrMagnitude > 1f)
                    routeTurn = Vector3.Angle(desiredWorldDir, toGoal.normalized);
            }

            _speedrunRouteOpen = goalDist > SpeedrunApproachDistance
                                 && routeTurn < SpeedrunMaxRouteTurn;

            CameraSnap = false;
            bool speedrunOwnsCamera = SpeedrunMode && ApplySpeedrunTech(desiredWorldDir);

            // Point the camera along the route. While duelling a boss, look at the boss
            // instead - watching the thing trying to kill us reads far better than watching
            // the strafe direction.
            Vector3 lookDir = desiredWorldDir;
            if (_currentMode == "boss-fight" || _currentMode == "boss-retreat")
            {
                Vector3 toBossFlat = bossPos - playerPos;
                toBossFlat.y = 0f;
                if (toBossFlat.sqrMagnitude > 0.01f) lookDir = toBossFlat.normalized;
            }

            if (lookDir.sqrMagnitude > 0.0001f && !speedrunOwnsCamera)
            {
                CameraYaw = Mathf.Atan2(lookDir.x, lookDir.z) * Mathf.Rad2Deg;

                // Pitch: sit slightly above the action by default, then tilt with the terrain -
                // look further down when the route drops away, lift when it climbs, so hills
                // and descents stay framed instead of the camera staring at a slope face.
                Vector3 lookAt = playerPos + lookDir * 12f;
                if (_hasPath && _pathIndex < _path.Count)
                    lookAt = _path[Mathf.Min(_pathIndex + 2, _path.Count - 1)];
                else if (_currentMode == "boss-fight" || _currentMode == "boss-retreat")
                    lookAt = bossPos;

                Vector3 flat = lookAt - playerPos;
                float horizontal = new Vector2(flat.x, flat.z).magnitude;

                // Ignore wild height differences. Before the nav grid is populated a goal can
                // still be sitting at its default, and tilting toward it aimed the camera at
                // the sky while the bot wandered in circles underneath.
                bool sanePitch = horizontal > 1f && Mathf.Abs(flat.y) < 25f;
                float slopePitch = sanePitch
                    ? Mathf.Atan2(-flat.y, horizontal) * Mathf.Rad2Deg
                    : 0f;

                CameraPitch = Mathf.Clamp(CameraBasePitch + slopePitch * 0.6f, -20f, 55f);
                HasCameraHeading = true;
            }

            UpdateDebugVisuals(playerPos);
        }

        // Detects "pushing into geometry" (walls, ledges, too-steep slopes) and escapes.
        // Escalates: first a jump + slight sidestep, then wider angles, then abandon the target.
        private Vector3 ApplyStuckHandling(Vector3 playerPos, Vector3 desiredWorldDir)
        {
            if (Time.time >= _nextStuckCheck)
            {
                float moved = Vector3.Distance(playerPos, _stuckLastPos);
                bool wantsToMove = desiredWorldDir.sqrMagnitude > 0.01f;

                // Mid-air we barely displace horizontally, which used to read as "stuck" and
                // trigger another hop - a loop that never stopped jumping. Only judge on foot.
                bool grounded = true;
                try { grounded = _cachedMovement.IsTouchingGround(); } catch { }

                if (grounded && wantsToMove && moved < StuckMoveThreshold) _stuckTimer += StuckCheckInterval;
                else _stuckTimer = 0f;

                _stuckLastPos = playerPos;
                _nextStuckCheck = Time.time + StuckCheckInterval;

                if (_stuckTimer >= StuckTriggerTime && Time.time >= _stuckRecoveryUntil)
                {
                    // repeated stucks in quick succession => escalate the escape angle
                    if (Time.time - _lastStuckTime < 4f) _consecutiveStucks++;
                    else _consecutiveStucks = 1;
                    _lastStuckTime = Time.time;

                    Vector3 baseDir = desiredWorldDir.sqrMagnitude > 0.0001f
                        ? desiredWorldDir
                        : new Vector3(Mathf.Cos(Time.time), 0f, Mathf.Sin(Time.time));

                    // Only hop if it's actually a ledge/step. Jumping at a flat wall does
                    // nothing but scrape up it, which is what the wall-climbing looked like.
                    bool hoppable = IsHoppableLedge(playerPos, baseDir) && Time.time >= _nextJumpAllowed;
                    if (hoppable) TryJump();

                    // 1st: sidestep ~70deg, 2nd: ~120deg, 3rd+: straight back the way we came
                    float angleDeg = _consecutiveStucks <= 1 ? 70f : (_consecutiveStucks == 2 ? 120f : 175f);
                    if (_avoidSide < 0) angleDeg = -angleDeg;

                    // prefer a turn that the sensors say is actually open
                    Vector3 recovery = Quaternion.Euler(0f, angleDeg, 0f) * baseDir;
                    if (!IsDirectionSafe(playerPos, recovery, ProbeDistance))
                    {
                        Vector3 mirrored = Quaternion.Euler(0f, -angleDeg, 0f) * baseDir;
                        if (IsDirectionSafe(playerPos, mirrored, ProbeDistance))
                        {
                            recovery = mirrored;
                            _avoidSide = -_avoidSide;
                            _avoidSideUntil = Time.time + 1.5f;
                        }
                    }

                    _stuckRecoveryDir = recovery;
                    _stuckRecoveryUntil = Time.time + (_consecutiveStucks >= 3 ? 1.2f : 0.7f);
                    _stuckTimer = 0f;

                    // The route we were following clearly doesn't work from here. Teach the
                    // grid about it so the next plan doesn't send us back into the same corner.
                    if (_consecutiveStucks >= 2)
                    {
                        Vector3 blockAt = playerPos + baseDir * 1.5f;
                        _pathfinder.BlockAround(blockAt, 2f, 45f);
                    }
                    InvalidatePath();

                    // if we were chasing something specific, give up on it for a while
                    if (_consecutiveStucks >= 2)
                    {
                        _hasExploreTarget = false;
                        if (TryGetBestLoot(playerPos, out LootTarget loot))
                            _lootBlacklist[loot.Id] = Time.time + LootBlacklistTime;
                            if (_committedLootId == loot.Id) _committedLootId = 0;
                    }

                    LoggerInstance.Msg($"Stuck (x{_consecutiveStucks}) - sidestep {angleDeg:0}deg{(hoppable ? " + hop" : " (wall, no hop)")}.");
                }
            }

            if (Time.time < _stuckRecoveryUntil) return _stuckRecoveryDir.normalized;
            return desiredWorldDir;
        }

        // ------------------------------------------------------------------
        // upgrade choice: score the actual stat modifiers, not just rarity
        // ------------------------------------------------------------------

        private static float StatWeight(EStat stat)
        {
            switch (stat)
            {
                // survivability first - this bot is built to last
                case EStat.MaxHealth: return 1.0f;
                case EStat.Armor: return 1.15f;
                case EStat.Evasion: return 1.05f;
                case EStat.HealthRegen: return 0.95f;
                case EStat.Lifesteal: return 1.25f;
                case EStat.Shield: return 0.8f;
                case EStat.DamageReductionMultiplier: return 1.25f;
                case EStat.HealingMultiplier: return 0.5f;
                case EStat.Overheal: return 0.4f;
                case EStat.Thorns: return 0.2f;
                case EStat.FallDamageReduction: return 0.1f;

                // mobility = survival in this game
                case EStat.MoveSpeedMultiplier: return 1.15f;
                case EStat.PickupRange: return 0.55f;
                case EStat.ExtraJumps: return 0.35f;
                case EStat.JumpHeight: return 0.15f;

                // offense - needed or the scaling eats you
                case EStat.Evolve: return 1.6f;
                case EStat.Projectiles: return 1.4f;
                case EStat.DamageMultiplier: return 1.3f;
                case EStat.AttackSpeed: return 1.2f;
                case EStat.CritChance: return 0.8f;
                case EStat.CritDamage: return 0.6f;
                case EStat.FreezeChance: return 0.6f;   // also defensive
                case EStat.ProjectileBounces: return 0.5f;
                case EStat.SizeMultiplier: return 0.5f;
                case EStat.LightningDamage: return 0.5f;
                case EStat.FireDamage: return 0.45f;
                case EStat.IceDamage: return 0.45f;
                case EStat.PoisonDamageMultiplier: return 0.4f;
                case EStat.DurationMultiplier: return 0.4f;
                case EStat.EliteDamageMultiplier: return 0.4f;
                case EStat.KnockbackMultiplier: return 0.35f;
                case EStat.BurnChance: return 0.3f;
                case EStat.WeaponBurstCooldown: return 0.3f;
                case EStat.ProjectileSpeedMultiplier: return 0.25f;
                case EStat.EffectDurationMultiplier: return 0.3f;
                case EStat.DamageCooldownMultiplier: return 0.6f;
                case EStat.Slam: return 0.2f;

                // economy / utility
                case EStat.Luck: return 0.6f;
                case EStat.XpIncreaseMultiplier: return 0.5f;
                case EStat.GoldIncreaseMultiplier: return 0.4f;   // gold buys chests
                case EStat.ChestIncreaseMultiplier: return 0.35f;
                case EStat.ShopPriceReduction: return 0.2f;
                case EStat.PowerupChance: return 0.3f;
                case EStat.PowerupBoostMultiplier: return 0.2f;
                case EStat.SilverIncreaseMultiplier: return 0.1f;
                case EStat.Holiness: return 0.1f;
                case EStat.Wickedness: return 0.1f;
                case EStat.ChestPriceMultiplier: return -0.4f;    // higher price = worse

                // things that make the run harder - avoid
                case EStat.Difficulty: return -1.5f;
                case EStat.EliteSpawnIncrease: return -0.8f;
                case EStat.EnemyAmountMultiplier: return -1.0f;
                case EStat.EnemySizeMultiplier: return -0.5f;
                case EStat.EnemySpeedMultiplier: return -1.0f;
                case EStat.EnemyHpMultiplier: return -0.8f;
                case EStat.EnemyDamageMultiplier: return -1.2f;
                case EStat.EnemyScalingMultiplier: return -1.0f;

                default: return 0.25f;
            }
        }

        // Flat/additive stats live on wildly different scales (+20 HP vs +1 projectile),
        // so bring them into the same ballpark as a +10% multiplier (=1.0).
        private static float FlatScale(EStat stat)
        {
            switch (stat)
            {
                case EStat.MaxHealth: return 0.03f;
                case EStat.Shield: return 0.05f;
                case EStat.PickupRange: return 0.05f;
                case EStat.Thorns: return 0.05f;
                case EStat.CritChance: return 0.10f;
                case EStat.Luck: return 0.10f;
                case EStat.Armor: return 0.15f;
                case EStat.Evasion: return 0.15f;
                case EStat.HealthRegen: return 0.30f;
                case EStat.Projectiles: return 1.0f;
                case EStat.ExtraJumps: return 1.0f;
                default: return 0.10f;
            }
        }

        // Item offers (Moai, chests, shady guy) carry EItemRarity on their ItemData, which is a
        // completely separate enum from the ERarity used by stat upgrades - and `btn.rarity` is
        // left at zero for them. Reading only btn.rarity made every item look Common, so the
        // sort was a no-op and it always took the first card.
        private int GetOfferRarity(UpgradeButton btn, out string source)
        {
            source = "upgrade";
            try
            {
                bool isItem = false;
                try { isItem = btn.isItem; } catch { }

                if (isItem)
                {
                    var data = btn.itemData;
                    if (data != null)
                    {
                        source = "item";
                        switch (data.rarity)
                        {
                            case EItemRarity.Common: return 1;
                            case EItemRarity.Rare: return 3;
                            case EItemRarity.Epic: return 4;
                            case EItemRarity.Legendary: return 5;
                            case EItemRarity.Corrupted: return 5;
                            case EItemRarity.Quest: return 3;
                        }
                    }
                }

                return (int)btn.rarity; // ERarity: New=0 .. Legendary=5
            }
            catch
            {
                source = "unknown";
                return 0;
            }
        }

        private float ScoreUpgradeButton(UpgradeButton btn, out string label)
        {
            label = "?";
            float score = 0f;
            int modCount = 0;

            try
            {
                var up = btn.upgradable;
                if (up != null)
                {
                    try { label = up.GetName(); } catch { }
                }

                var offer = btn.upgradeOffer;
                if (offer != null)
                {
                    for (int i = 0; i < offer.Count; i++)
                    {
                        var mod = offer[i];
                        if (mod == null) continue;
                        modCount++;

                        float magnitude = mod.modifyType == EStatModifyType.Multiplication
                            ? mod.modification * 10f
                            : mod.modification * FlatScale(mod.stat);

                        score += StatWeight(mod.stat) * magnitude;
                    }
                }

                // A brand new weapon/tome usually beats a small stat bump early on,
                // when we have few damage sources; later, stacking is better.
                if (up != null)
                {
                    int level = -1;
                    try { level = up.GetLevel(); } catch { }
                    if (level == 0)
                    {
                        int weapons = 99;
                        try
                        {
                            var wi = _cachedInventory != null ? _cachedInventory.weaponInventory : null;
                            if (wi != null) weapons = wi.GetNumWeapons();
                        }
                        catch { }

                        score += weapons < 3 ? 3.5f : 1.0f;
                    }
                }

                if (modCount == 0 && score == 0f) score = 1.0f; // unknown offer: neutral, not worthless

                score += GetOfferRarity(btn, out _) * 0.35f; // rarity is mostly baked into magnitudes
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Scoring offer failed: {ex.Message}");
            }

            return score;
        }

        private void BuildLevelUpCandidates()
        {
            _levelUpCandidates.Clear();
            var buttons = UnityEngine.Object.FindObjectsOfType<UpgradeButton>();

            var scored = new List<(UpgradeButton btn, int rarity, float score)>();
            foreach (var btn in buttons)
            {
                if (btn == null || !btn.gameObject.activeInHierarchy) continue;
                float score = ScoreUpgradeButton(btn, out string label);
                int rarity = GetOfferRarity(btn, out string raritySource);
                LoggerInstance.Msg($"  offer '{label}' rarity={rarity} ({raritySource}) score={score:0.00}");
                scored.Add((btn, rarity, score));
            }

            // Rarity always wins; the stat score only breaks ties between equal rarities.
            scored.Sort((a, b) =>
            {
                int byRarity = b.rarity.CompareTo(a.rarity);
                return byRarity != 0 ? byRarity : b.score.CompareTo(a.score);
            });
            foreach (var entry in scored) _levelUpCandidates.Add(entry.btn);

            if (_levelUpCandidates.Count > 0)
                LoggerInstance.Msg($"Level-up: {_levelUpCandidates.Count} offers scored, taking the best.");
        }

        // The offer window is LevelupScreen for level-ups AND for Moai / shady guy item offers.
        // isLevelingUp is only set for real level-ups, so detect the window itself instead.
        private bool IsOfferWindowOpen()
        {
            try
            {
                if (_cachedLevelupScreen == null && Time.time >= _nextLevelupScreenLookup)
                {
                    _nextLevelupScreenLookup = Time.time + 0.5f;
                    _cachedLevelupScreen = UnityEngine.Object.FindObjectOfType<LevelupScreen>();
                }

                if (_cachedLevelupScreen != null)
                {
                    var window = _cachedLevelupScreen.window;
                    if (window != null && window.activeInHierarchy) return true;
                }

                if (LevelupScreen.isLevelingUp) return true;

                // Fallback: charge-shrine rewards and other encounters put live offer buttons
                // on screen without necessarily flipping the flags above.
                if (Time.time >= _nextOfferButtonProbe)
                {
                    _nextOfferButtonProbe = Time.time + 0.25f;
                    _sawOfferButtons = false;
                    foreach (var btn in UnityEngine.Object.FindObjectsOfType<UpgradeButton>())
                    {
                        if (btn == null || !btn.gameObject.activeInHierarchy) continue;
                        if (btn.button == null || !btn.button.interactable) continue;
                        _sawOfferButtons = true;
                        break;
                    }
                }

                return _sawOfferButtons;
            }
            catch { }

            return LevelupScreen.isLevelingUp;
        }

        private void HandleLevelUpIfNeeded()
        {
            if (!IsOfferWindowOpen())
            {
                _handlingLevelUp = false;
                _levelUpCandidates.Clear();
                _levelUpCursor = 0;
                _levelUpFullPasses = 0;
                return;
            }

            if (!_handlingLevelUp)
            {
                _handlingLevelUp = true;
                _levelUpCursor = 0;
                _levelUpFullPasses = 0;
                _levelUpCandidates.Clear();
                _nextLevelUpAttempt = Time.time + 0.9f; // reads as a real decision on stream
            }

            if (Time.time < _nextLevelUpAttempt) return;

            if (_levelUpCandidates.Count == 0) BuildLevelUpCandidates();

            if (_levelUpCandidates.Count == 0)
            {
                _nextLevelUpAttempt = Time.time + 1f;
                return;
            }

            if (_levelUpCursor >= _levelUpCandidates.Count)
            {
                _levelUpFullPasses++;
                if (_levelUpFullPasses >= 2)
                {
                    LoggerInstance.Error("Level-up still open after clicking every offer twice - backing off 5s.");
                    _levelUpCandidates.Clear();
                    _levelUpCursor = 0;
                    _levelUpFullPasses = 0;
                    _nextLevelUpAttempt = Time.time + 5f;
                    return;
                }
                _levelUpCandidates.Clear();
                _levelUpCursor = 0;
                _nextLevelUpAttempt = Time.time + 0.5f;
                return;
            }

            var chosen = _levelUpCandidates[_levelUpCursor];
            try
            {
                var uiButton = chosen != null ? chosen.button : null;
                if (uiButton != null) uiButton.onClick.Invoke();
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Clicking upgrade button failed: {ex.Message}");
            }

            _levelUpCursor++;
            _nextLevelUpAttempt = Time.time + 0.6f;
        }

        // Moai, greed altars, shady guy, balance shrines and friends all route through
        // EncounterUi rather than the level-up screen, so they need their own handler.
        // Returns true while such a window is on screen.
        private bool HandleEncounterWindowIfNeeded()
        {
            EncounterUi ui = null;
            try { ui = UnityEngine.Object.FindObjectOfType<EncounterUi>(); }
            catch { return false; }

            if (ui == null || !ui.gameObject.activeInHierarchy) return false;

            EncounterButton best = null;
            int bestRarity = -1;
            bool anyVisible = false;

            try
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    var list = pass == 0 ? ui.rarityButtons : ui.genericButtons;
                    if (list == null) continue;

                    for (int i = 0; i < list.Count; i++)
                    {
                        var btn = list[i];
                        if (btn == null || !btn.gameObject.activeInHierarchy) continue;
                        anyVisible = true;

                        bool canAccept = true;
                        try { canAccept = btn.canAccept; } catch { }
                        if (!canAccept) continue;

                        int rarity = 0;
                        try
                        {
                            var offers = ui.offers;
                            if (offers != null && btn.index >= 0 && btn.index < offers.Length)
                                rarity = (int)offers[btn.index].rarity;
                        }
                        catch { }

                        if (rarity > bestRarity)
                        {
                            bestRarity = rarity;
                            best = btn;
                        }
                    }

                    if (best != null) break; // prefer the rarity offers over the generic row
                }
            }
            catch { }

            if (!anyVisible) return false;
            if (Time.time < _nextEncounterClick) return true;

            if (best == null)
            {
                // nothing acceptable (can't afford, maxed out) - wait, the window will time out
                _nextEncounterClick = Time.time + 1f;
                return true;
            }

            try
            {
                LoggerInstance.Msg($"Encounter: taking offer index {best.index} (rarity {bestRarity}).");
                best.button.onClick.Invoke();
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Encounter click failed: {ex.Message}");
            }

            _nextEncounterClick = Time.time + 0.8f;
            return true;
        }

        // Chests pop their own window: Open, then Take. Returns true while it's on screen.
        private bool HandleChestWindowIfNeeded()
        {
            ChestWindowUi window = null;
            try { window = UnityEngine.Object.FindObjectOfType<ChestWindowUi>(); }
            catch { return false; }

            if (window == null) return false;

            bool takeActive = false, openActive = false;
            MyButton take = null, open = null;

            try
            {
                take = window.b_take;
                open = window.b_open;
                takeActive = take != null && take.gameObject.activeInHierarchy;
                openActive = open != null && open.gameObject.activeInHierarchy;
            }
            catch { }

            if (!takeActive && !openActive) return false;
            if (Time.time < _nextChestWindowClick) return true;

            try
            {
                if (takeActive)
                {
                    take.button.onClick.Invoke();
                    LoggerInstance.Msg("Chest: taking item.");
                }
                else
                {
                    open.button.onClick.Invoke();
                    LoggerInstance.Msg("Chest: opening.");
                }
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Chest window click failed: {ex.Message}");
            }

            _nextChestWindowClick = Time.time + 0.8f;
            return true;
        }
    }

    [HarmonyPatch(typeof(Il2Cpp.PlayerInput), "MovementInput")]
    public static class MovementInputPatch
    {
        static void Postfix(Il2Cpp.PlayerInput __instance)
        {
            if (!Core.AiEnabled) return;
            __instance.moveHorizontal = Core.DesiredMoveHorizontal;
            __instance.moveVertical = Core.DesiredMoveVertical;

            // Never drive jumping through the input fields. Writing `jumping` kept refilling
            // the game's jump buffer and the character never stopped hopping; jumps are issued
            // by calling PlayerMovement.Jump() directly instead. Hold these low so no stale
            // buffered jump can survive.
            __instance.jumping = false;
            __instance.holdingJump = false;
            __instance.holdingWallrun = false;
        }
    }
}
