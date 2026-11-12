using System;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;
using Scorched.Weapons;

namespace Scorched.Player
{
    public enum FPSPerspectiveMode
    {
        [InspectorName("Option 1: True First-Person (Eye-Level Tuned)")]
        TrueFirstPerson_EyeTuned = 0,

        [InspectorName("Option 2: Tactical Combat-Ready (Upper Body Layer)")]
        CombatReady_UpperBody = 1,

        [InspectorName("Option 3: Dedicated FPS Viewmodel (Modern Warfare Style)")]
        DedicatedViewmodel_ModernWarfare = 2
    }

    /// <summary>
    /// Master controller for toggling and comparing all 3 First-Person presentation modes:
    /// - Option 1: True First-Person (Eye-Tuned camera, 100% full-body, relaxed patrol carry).
    /// - Option 2: Tactical Combat-Ready (Upper Body layer raises arms & AK-47 to chest/shoulder via mocap clip).
    /// - Option 3: Dedicated FPS Viewmodel (Call of Duty / Modern Warfare style camera-anchored viewmodel).
    /// 
    /// - Bottom-middle screen framing for Hip Fire matching Call of Duty Modern Warfare.
    /// - ADS is strictly activated by holding Right Click; Firing (Left Click) shoots from hip without forcing ADS.
    /// - Molecular Two-Bone IK locks both hands firmly to the pistol grip and handguard without bone twisting.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerMovement))]
    public class FPSPerspectiveController : NetworkBehaviour
    {
        private static readonly int AnimStateFiringRifle = Animator.StringToHash("Firing Rifle");

        [Header("Active Perspective Mode")]
        [Tooltip("Select which FPS presentation mode to use.")]
        [SerializeField] private FPSPerspectiveMode _mode = FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare;

        [Header("Dynamic ADS Aiming")]
        [Tooltip("Transition speed between hip-fire position and center iron sightline.")]
        [SerializeField] private float _adsTransitionSpeed = 16.0f;

        [Header("Option 2 & 3: Tactical Hip-Fire Eye Offsets (Bottom-Middle Framing)")]
        [Tooltip("Forward eye-relief from neck bone.")]
        [SerializeField] private float _opt2EyeForward = -0.05f;

        [Tooltip("Vertical eye height from neck bone.")]
        [SerializeField] private float _opt2EyeUp = 0.00f;

        [Tooltip("Horizontal eye offset in hip-fire mode (keeps gun in bottom-middle).")]
        [SerializeField] private float _opt2EyeRight = 0.00f;

        [Tooltip("Field of View in hip-fire mode.")]
        [SerializeField] private float _opt2FOV = 80f;

        [Header("ADS Iron Sight Offsets (Hold Right Click)")]
        [Tooltip("Horizontal sightline offset for dead-center iron sight alignment.")]
        [SerializeField] private float _firingSightRightOffset = 0.068f;

        [Tooltip("Forward eye-relief offset when in ADS.")]
        [SerializeField] private float _firingEyeForwardOffset = 0.080f;

        [Tooltip("Vertical eye height when in ADS.")]
        [SerializeField] private float _firingEyeUpOffset = 0.038f;

        [Tooltip("Field of View when aimed down sights.")]
        [SerializeField] private float _adsFOV = 68f;

        [Header("Hotkey Controls")]
        [Tooltip("Keyboard hotkey to cycle between modes in Play Mode.")]
        [SerializeField] private Key _cycleModeKey = Key.F2;

        [Tooltip("Show on-screen debug notification banner when switching modes.")]
        [SerializeField] private bool _showOnScreenBanner = true;

        [Header("Option 1: True First-Person Eye Offsets")]
        [SerializeField] private float _opt1EyeForward = 0.05f;
        [SerializeField] private float _opt1EyeUp = 0.02f;
        [SerializeField] private float _opt1EyeRight = 0.015f;
        [SerializeField] private float _opt1FOV = 82f;

        [Header("Option 3: Modern Warfare Procedural Viewmodel")]
        [SerializeField] private Vector3 _vmHipPosition = new Vector3(0.17f, -0.27f, 0.34f);
        [SerializeField] private Vector3 _vmHipEuler = new Vector3(1.0f, -1.5f, 0.0f);
        [SerializeField] private Vector3 _vmAdsPosition = new Vector3(0.000f, -0.088f, 0.455f);
        [SerializeField] private Vector3 _vmAdsEuler = Vector3.zero;
        [SerializeField] private Vector3 _vmSprintPositionOffset = new Vector3(0.015f, -0.055f, -0.045f);
        [SerializeField] private Vector3 _vmSprintEuler = new Vector3(11f, -23f, -2f);
        [SerializeField] private Vector3 _vmCrouchPositionOffset = new Vector3(-0.008f, -0.012f, -0.010f);
        [SerializeField] private Vector3 _vmCrouchEuler = new Vector3(1.5f, -1.5f, 3.0f);
        [SerializeField] private Vector3 _vmJumpPositionOffset = new Vector3(-0.006f, -0.018f, -0.012f);
        [SerializeField] private Vector3 _vmJumpEuler = new Vector3(-2.0f, 1.5f, 2.5f);
        [SerializeField] private Vector3 _vmLeftShoulderAnchor = new Vector3(-0.02f, -0.58f, 0.28f);
        [SerializeField] private Vector3 _vmRightShoulderAnchor = new Vector3(0.16f, -0.58f, -0.12f);
        [SerializeField] private Vector3 _vmLeftElbowPole = new Vector3(0.05f, -0.68f, 0.25f);
        [SerializeField] private Vector3 _vmRightElbowPole = new Vector3(0.16f, -0.74f, -0.18f);
        [SerializeField] private float _idleBreathingAmp = 0.003f;
        [SerializeField] private float _idleBreathingHz = 1.8f;
        [SerializeField] private float _walkBobAmount = 0.006f;
        [SerializeField] private float _mouseSwayAmount = 1.2f;
        [SerializeField] private float _centerTransitionSpeed = 14.0f;

        [Header("First-Person Mesh Optimization")]
        [Tooltip("Arms-only mesh for local player to guarantee zero head/neck clipping or stretching.")]
        [SerializeField] private Mesh _fpsArmsMesh;
        [SerializeField] private Mesh _defaultBodyMesh;

        // Cached components
        private PlayerMovement _playerMovement;
        private PlayerHealthController _healthController;
        private PlayerWeaponHandler _weaponHandler;
        private Animator _animator;
        private SkinnedMeshRenderer _banditRenderer;

        // 3rd Person Left arm bones for 2-bone grip IK
        private Transform _armL;
        private Transform _forearmL;
        private Transform _handL;

        // Option 3 Viewmodel references
        [SerializeField] private GameObject _viewmodelRig;
        [SerializeField] private Transform _viewmodelWeapon;
        [SerializeField] private GameObject _viewmodelArms;

        // Viewmodel bone references
        private Transform _vmSpine03;
        private Transform _vmShoulderR, _vmArmR, _vmForearmR, _vmHandR, _vmTwistR;
        private Transform _vmShoulderL, _vmArmL, _vmForearmL, _vmHandL, _vmTwistL;
        private Transform _vmRGripTarget;
        private Transform _vmLGripTarget;
        private (Transform bone, Quaternion rot)[] _vmFingerPoseCache;

        // Runtime animation & viewmodel state
        private float _currentCenterWeight = 0f;
        private float _currentAdsWeight = 0f;
        private float _currentSprintWeight = 0f;
        private float _currentCrouchWeight = 0f;
        private float _currentJumpWeight = 0f;
        private float _bobTimer = 0f;
        private float _breathTimer = 0f;
        private float _bobAmpWeight = 0f;
        private Vector2 _currentMouseSway;
        private Vector3 _recoilOffset = Vector3.zero;
        private Vector3 _recoilRotOffset = Vector3.zero;
        private Vector3 _lastProcOffset = Vector3.zero;
        private Quaternion _lastProcRot = Quaternion.identity;
        private float _currentStrafeRoll = 0f;
        private string _bannerMessage = "";
        private float _bannerTimer = 0f;

        public FPSPerspectiveMode CurrentMode
        {
            get => _mode;
            set
            {
                if (_mode != value)
                {
                    _mode = value;
                    ApplyCurrentMode();
                }
            }
        }

        [SerializeField] private bool _centerOnFiring = false;

        public bool CenterOnFiring
        {
            get => _centerOnFiring;
            set => _centerOnFiring = value;
        }

        /// <summary>
        /// Returns the transform of the muzzle on the currently active visible weapon.
        /// </summary>
        public Transform ActiveMuzzlePoint
        {
            get
            {
                if (_mode == FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare && _viewmodelWeapon != null)
                {
                    Transform vmMuzzle = _viewmodelWeapon.Find("MuzzlePoint");
                    if (vmMuzzle != null) return vmMuzzle;
                }

                if (_weaponHandler != null && _weaponHandler.EquippedWeapon != null)
                {
                    Transform worldMuzzle = _weaponHandler.EquippedWeapon.Find("MuzzlePoint");
                    if (worldMuzzle == null)
                    {
                        worldMuzzle = EnsureMuzzlePointOnTransform(_weaponHandler.EquippedWeapon);
                    }
                    if (worldMuzzle != null) return worldMuzzle;
                }

                return null;
            }
        }

        public static Transform EnsureMuzzlePointOnTransform(Transform weapon)
        {
            if (weapon == null) return null;
            Transform muzzle = weapon.Find("MuzzlePoint");
            if (muzzle == null)
            {
                var go = new GameObject("MuzzlePoint");
                go.transform.SetParent(weapon, false);
                muzzle = go.transform;
            }

            // Calibrated barrel bore center at tip for AK-47 model in local space
            muzzle.localPosition = new Vector3(0.0f, 0.0367f, 0.524f);
            muzzle.localRotation = Quaternion.identity;
            muzzle.localScale = Vector3.one;
            return muzzle;
        }

        #region Left Hand Grip IK Solver
        public static Transform EnsureLeftHandGripTarget(Transform weapon)
        {
            if (weapon == null) return null;
            Transform grip = weapon.Find("LeftHandGripTarget");
            if (grip == null)
            {
                var go = new GameObject("LeftHandGripTarget");
                go.transform.SetParent(weapon, false);
                grip = go.transform;
            }

            // Calibrated Left Hand Under-Barrel Palm-Up Cradle directly underneath AK-47 wooden handguard
            // Note Bandit hand.l local axes: +Y = fingers, +Z = knuckles (so -Z = palm), -X = thumb/index edge
            Vector3 lUp = new Vector3(0.18f, 0.22f, 0.96f).normalized;
            Vector3 lFwd = new Vector3(-0.16f, -0.97f, 0.18f).normalized;
            Vector3 lRight = Vector3.Cross(lUp, lFwd).normalized;
            lUp = Vector3.Cross(lFwd, lRight).normalized;
            grip.localPosition = new Vector3(0.006f, -0.044f, 0.210f);
            grip.localRotation = Quaternion.LookRotation(lFwd, lUp);
            grip.localScale = Vector3.one;
            return grip;
        }

        public static Transform EnsureRightHandGripTarget(Transform weapon)
        {
            if (weapon == null) return null;
            Transform grip = weapon.Find("RightHandGripTarget");
            if (grip == null)
            {
                var go = new GameObject("RightHandGripTarget");
                go.transform.SetParent(weapon, false);
                grip = go.transform;
            }

            // Calibrated Right Hand wrapped cleanly around AK-47 pistol grip (below receiver/stock)
            // Note Bandit hand.r local axes: +Y = fingers, +Z = knuckles (-Z = palm), +X = thumb/index edge
            Vector3 rUp = new Vector3(-0.18f, 0.42f, 0.89f).normalized;
            Vector3 rFwd = new Vector3(0.95f, -0.18f, 0.25f).normalized;
            Vector3 rRight = Vector3.Cross(rUp, rFwd).normalized;
            rUp = Vector3.Cross(rFwd, rRight).normalized;
            grip.localPosition = new Vector3(-0.002f, -0.118f, -0.128f);
            grip.localRotation = Quaternion.LookRotation(rFwd, rUp);
            grip.localScale = Vector3.one;
            return grip;
        }

        public void SolveLeftHandIK()
        {
            if (_armL == null || _forearmL == null || _handL == null)
            {
                CacheComponents();
            }
            if (_armL == null || _forearmL == null || _handL == null) return;

            // Ensure hand.l is never dislocated from wrist
            if (_handL.localPosition != new Vector3(0f, 0.2648f, 0f))
            {
                _handL.localPosition = new Vector3(0f, 0.2648f, 0f);
            }

            Transform weapon = _weaponHandler != null ? _weaponHandler.EquippedWeapon : null;
            if (weapon == null) return;

            Transform gripTarget = weapon.Find("LeftHandGripTarget");
            if (gripTarget == null)
            {
                gripTarget = EnsureLeftHandGripTarget(weapon);
            }
            if (gripTarget == null) return;

            Transform torso = (_playerMovement != null && _playerMovement.UpperChestBone != null)
                ? _playerMovement.UpperChestBone
                : (_armL != null ? _armL.parent : transform);

            Vector3 pole = _armL.position - torso.up * 0.5f - torso.right * 0.4f;
            SolveTwoBoneIKInternal(_armL, _forearmL, _handL, gripTarget.position, gripTarget.rotation, pole);
        }

        public static void SolveTwoBoneIKInternal(Transform rootBone, Transform midBone, Transform tipBone, Vector3 targetPos, Quaternion targetRot, Vector3 polePos)
        {
            if (rootBone == null || midBone == null || tipBone == null) return;

            float upperLen = Vector3.Distance(rootBone.position, midBone.position);
            float lowerLen = Vector3.Distance(midBone.position, tipBone.position);
            float totalLen = upperLen + lowerLen;

            Vector3 toTarget = targetPos - rootBone.position;
            float dist = Mathf.Clamp(toTarget.magnitude, 0.001f, totalLen - 0.001f);

            // Law of cosines for interior angles
            float cosAngleRoot = Mathf.Clamp(((upperLen * upperLen) + (dist * dist) - (lowerLen * lowerLen)) / (2f * upperLen * dist), -1f, 1f);
            float angleRoot = Mathf.Acos(cosAngleRoot) * Mathf.Rad2Deg;

            Vector3 planeNormal = Vector3.Cross(toTarget.normalized, (polePos - rootBone.position).normalized).normalized;
            if (planeNormal.sqrMagnitude < 0.001f) planeNormal = Vector3.Cross(toTarget.normalized, Vector3.up).normalized;

            // Rotate root bone toward target + pole bend (+angleRoot bends elbow TOWARD polePos)
            Vector3 currentUpperDir = (midBone.position - rootBone.position).normalized;
            Quaternion aimRot = Quaternion.FromToRotation(currentUpperDir, toTarget.normalized) * rootBone.rotation;
            rootBone.rotation = Quaternion.AngleAxis(angleRoot, planeNormal) * aimRot;

            // Align rootBone's outer-elbow axis toward polePos so the elbow bends as a clean hinge (zero elbow twist)
            Vector3 finalUpperDir = (midBone.position - rootBone.position).normalized;
            Vector3 desiredElbowTipDir = Vector3.ProjectOnPlane(polePos - rootBone.position, finalUpperDir).normalized;
            if (desiredElbowTipDir.sqrMagnitude > 1e-4f)
            {
                bool isRightSide = rootBone.name.EndsWith(".r", StringComparison.OrdinalIgnoreCase)
                    || rootBone.name.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0;
                Vector3 currentElbowAxis = Vector3.ProjectOnPlane(isRightSide ? -rootBone.right : rootBone.right, finalUpperDir).normalized;
                if (currentElbowAxis.sqrMagnitude > 1e-4f)
                {
                    rootBone.rotation = Quaternion.FromToRotation(currentElbowAxis, desiredElbowTipDir) * rootBone.rotation;
                }
            }

            // Rotate mid bone (forearm) so tip bone (hand) reaches exact targetPos
            Vector3 currentLowerDir = (tipBone.position - midBone.position).normalized;
            Vector3 desiredLowerDir = (targetPos - midBone.position).normalized;
            midBone.rotation = Quaternion.FromToRotation(currentLowerDir, desiredLowerDir) * midBone.rotation;

            // Align midBone's outer-elbow axis with rootBone's hinge plane before distributing wrist twist
            if (desiredElbowTipDir.sqrMagnitude > 1e-4f)
            {
                bool isRightSide = rootBone.name.EndsWith(".r", StringComparison.OrdinalIgnoreCase)
                    || rootBone.name.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0;
                Vector3 targetMidElbow = Vector3.ProjectOnPlane(desiredElbowTipDir, desiredLowerDir).normalized;
                Vector3 currentMidElbow = Vector3.ProjectOnPlane(isRightSide ? -midBone.right : midBone.right, desiredLowerDir).normalized;
                if (targetMidElbow.sqrMagnitude > 1e-4f && currentMidElbow.sqrMagnitude > 1e-4f)
                {
                    midBone.rotation = Quaternion.FromToRotation(currentMidElbow, targetMidElbow) * midBone.rotation;
                }
            }

            // Distribute axial pronation/supination twist: 60% Forearm, <=25 deg Wrist cap (prevents candy-wrapper pinch)
            Vector3 boneAxis = tipBone.localPosition.sqrMagnitude > 1e-6f
                ? tipBone.localPosition.normalized
                : Vector3.up;
            Quaternion rel = Quaternion.Inverse(midBone.rotation) * targetRot;
            if (rel.w < 0f) rel = new Quaternion(-rel.x, -rel.y, -rel.z, -rel.w);

            Vector3 relVec = new Vector3(rel.x, rel.y, rel.z);
            Vector3 twistProj = Vector3.Project(relVec, boneAxis);
            Quaternion twist = new Quaternion(twistProj.x, twistProj.y, twistProj.z, rel.w);
            if (Quaternion.Dot(twist, twist) < 1e-6f) twist = Quaternion.identity;
            else twist.Normalize();
            if (twist.w < 0f) twist = new Quaternion(-twist.x, -twist.y, -twist.z, -twist.w);

            Quaternion swing = Quaternion.Inverse(twist) * rel;
            twist.ToAngleAxis(out float twistAngle, out Vector3 twistAxis);
            if (twistAngle > 180f) twistAngle -= 360f;
            float signedTwist = twistAngle * (Vector3.Dot(twistAxis, boneAxis) >= 0f ? 1f : -1f);

            float remTwist = Mathf.Clamp(signedTwist * 0.40f, -25f, 25f);
            float midTwist = Mathf.Clamp(signedTwist - remTwist, -75f, 75f);
            remTwist = Mathf.Clamp(signedTwist - midTwist, -25f, 25f);

            midBone.rotation = midBone.rotation * Quaternion.AngleAxis(midTwist, boneAxis);

            swing.ToAngleAxis(out float swingAngle, out Vector3 swingAxis);
            if (swingAngle > 180f) swingAngle -= 360f;
            if (Mathf.Abs(swingAngle) > 75f && swingAxis.sqrMagnitude > 1e-6f)
            {
                swing = Quaternion.AngleAxis(Mathf.Clamp(swingAngle, -75f, 75f), swingAxis.normalized);
            }

            tipBone.rotation = midBone.rotation * Quaternion.AngleAxis(remTwist, boneAxis) * swing;
        }

        /// <summary>
        /// Distributes wrist pronation/supination twist smoothly into forearm_twist to prevent cuff pinching.
        /// </summary>
        public static void ApplyForearmTwist(Transform forearmBone, Transform handBone, Transform twistBone)
        {
            if (forearmBone == null || handBone == null || twistBone == null) return;
            Quaternion rel = Quaternion.Inverse(forearmBone.rotation) * handBone.rotation;
            if (rel.w < 0f)
            {
                rel = new Quaternion(-rel.x, -rel.y, -rel.z, -rel.w);
            }
            Vector3 proj = Vector3.Project(new Vector3(rel.x, rel.y, rel.z), Vector3.up);
            Quaternion twistOnly = new Quaternion(proj.x, proj.y, proj.z, rel.w).normalized;
            if (twistOnly.w < 0f)
            {
                twistOnly = new Quaternion(-twistOnly.x, -twistOnly.y, -twistOnly.z, -twistOnly.w);
            }
            twistBone.rotation = forearmBone.rotation * Quaternion.Slerp(Quaternion.identity, twistOnly, 0.55f);
        }

        /// <summary>
        /// Called at the end of PlayerMovement.LateUpdate() AFTER the spine pitch, weapon socket,
        /// and camera position have all completely finished evaluating.
        /// </summary>
        public void PostLateUpdateIK()
        {
            if (IsSpawned && !IsOwner) return;
            if (_healthController != null && !_healthController.IsAlive) return;

            if (_mode == FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare)
            {
                var mouse = Mouse.current;
                bool isAiming = mouse != null && mouse.rightButton.isPressed;
                UpdateModernWarfareViewmodel(Time.deltaTime, isAiming);
            }
            else if (_mode == FPSPerspectiveMode.CombatReady_UpperBody || _mode == FPSPerspectiveMode.TrueFirstPerson_EyeTuned)
            {
                SolveLeftHandIK();
            }
        }
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            CacheComponents();
            EnsureViewmodelRig();
        }

        private void Start()
        {
            ApplyCurrentMode();
        }

        private void CacheComponents()
        {
            _playerMovement = GetComponent<PlayerMovement>();
            _healthController = GetComponent<PlayerHealthController>();
            _weaponHandler = GetComponent<PlayerWeaponHandler>();
            _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
            _banditRenderer = GetComponentInChildren<SkinnedMeshRenderer>();

            _armL = FindDeepChild(transform, "arm_stretch.l");
            _forearmL = FindDeepChild(transform, "forearm_stretch.l");
            _handL = FindDeepChild(transform, "hand.l");

            if (_banditRenderer != null && _defaultBodyMesh == null && _banditRenderer.sharedMesh != null && !_banditRenderer.sharedMesh.name.Contains("Arms"))
            {
                _defaultBodyMesh = _banditRenderer.sharedMesh;
            }
#if UNITY_EDITOR
            if (_fpsArmsMesh == null)
            {
                _fpsArmsMesh = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_PureArms.asset")
                            ?? UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Models/FPS_Bandit_Arms.asset");
            }
#endif
        }

        private void Update()
        {
            // Only local owner controls camera, input, and viewmodel
            if (IsSpawned && !IsOwner) return;

            // Update banner timer
            if (_bannerTimer > 0f)
            {
                _bannerTimer -= Time.deltaTime;
            }

            bool isAlive = _healthController == null || _healthController.IsAlive;
            if (!isAlive)
            {
                if (_animator != null && _animator.layerCount > 1 && _animator.GetLayerWeight(1) > 0f)
                {
                    _animator.SetLayerWeight(1, 0f);
                }
                _currentCenterWeight = 0f;
                return;
            }

            // Hotkey to cycle modes during gameplay
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                var keyControl = keyboard[_cycleModeKey];
                if (keyControl != null && keyControl.wasPressedThisFrame)
                {
                    CycleToNextMode();
                }
            }

            // ADS strictly requires holding Right Click! Firing (Left Click) does NOT auto-ADS!
            var mouse = Mouse.current;
            bool isAiming = mouse != null && mouse.rightButton.isPressed;

            float targetCenter = isAiming ? 1.0f : 0.0f;
            _currentCenterWeight = Mathf.MoveTowards(_currentCenterWeight, targetCenter, Time.deltaTime * _centerTransitionSpeed);

            // Mode-specific Update steps (Option 3 Modern Warfare viewmodel evaluates in PostLateUpdateIK
            // right AFTER PlayerMovement updates camera position/rotation so IK stays 100% locked!)
            switch (_mode)
            {
                case FPSPerspectiveMode.TrueFirstPerson_EyeTuned:
                    UpdateTrueFirstPersonMode(isAiming);
                    UpdateTrueFirstPersonCenter();
                    break;

                case FPSPerspectiveMode.CombatReady_UpperBody:
                    UpdateCombatReadyMode();
                    UpdateCombatReadyCenter();
                    break;

                case FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare:
                    break;
            }
        }

        private void LateUpdate()
        {
            // Mode 1/2 Left-hand IK and Mode 3 Modern Warfare viewmodel procedural pose + Two-Bone IK
            // are invoked in PostLateUpdateIK() directly at the end of PlayerMovement.LateUpdate().
        }

        private void OnValidate()
        {
            if (!gameObject.scene.IsValid()) return;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                if (_playerMovement == null) CacheComponents();
                ApplyCurrentMode();
            };
#else
            if (_playerMovement == null) CacheComponents();
            ApplyCurrentMode();
#endif
        }
        #endregion

        #region Mode Application Logic
        public void CycleToNextMode()
        {
            int next = ((int)_mode + 1) % 3;
            CurrentMode = (FPSPerspectiveMode)next;
        }

        public void HandleLifeStateChanged(PlayerLifeState newState)
        {
            if (_playerMovement == null) CacheComponents();
            if (_playerMovement == null) return;

            if (newState != PlayerLifeState.Alive)
            {
                ApplyNonAlivePresentation();
            }
            else
            {
                ApplyCurrentModeInternal(showBanner: false);
            }
        }

        private void ApplyNonAlivePresentation()
        {
            if (_animator != null && _animator.layerCount > 1)
            {
                _animator.SetLayerWeight(1, 0.0f);
            }

            if (_viewmodelRig != null)
            {
                _viewmodelRig.SetActive(false);
            }

            if (_banditRenderer != null)
            {
                _banditRenderer.enabled = true;
                _banditRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (_defaultBodyMesh != null) _banditRenderer.sharedMesh = _defaultBodyMesh;
            }

            if (_weaponHandler != null && _weaponHandler.EquippedWeapon != null)
            {
                _weaponHandler.EquippedWeapon.gameObject.SetActive(false);
            }
        }

        public void ApplyCurrentMode()
        {
            ApplyCurrentModeInternal(showBanner: true);
        }

        private void ApplyCurrentModeInternal(bool showBanner)
        {
            if (_playerMovement == null) CacheComponents();
            if (_playerMovement == null) return;

            if (_healthController != null && !_healthController.IsAlive)
            {
                ApplyNonAlivePresentation();
                return;
            }

            bool isLocalOwner = !IsSpawned || IsOwner;

            switch (_mode)
            {
                case FPSPerspectiveMode.TrueFirstPerson_EyeTuned:
                    ApplyTrueFirstPerson(isLocalOwner);
                    break;

                case FPSPerspectiveMode.CombatReady_UpperBody:
                    ApplyCombatReady(isLocalOwner);
                    break;

                case FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare:
                    ApplyModernWarfareViewmodel(isLocalOwner);
                    break;
            }

            if (showBanner)
            {
                ShowBanner($"[FPS Mode] {_mode.ToString().Replace('_', ' ')}");
            }
        }

        private void ApplyTrueFirstPerson(bool isLocalOwner)
        {
            _playerMovement.EyeOffsetForward = _opt1EyeForward;
            _playerMovement.EyeOffsetUp = _opt1EyeUp;
            _playerMovement.EyeOffsetRight = _opt1EyeRight;
            _playerMovement.FieldOfViewProperty = _opt1FOV;
            _playerMovement.NearClipPlane = 0.015f;

            if (_animator != null && _animator.layerCount > 1)
            {
                _animator.SetLayerWeight(1, 0.0f);
            }

            if (_weaponHandler != null && _weaponHandler.EquippedWeapon != null)
            {
                _weaponHandler.EquippedWeapon.gameObject.SetActive(true);
            }
            if (_banditRenderer != null)
            {
                _banditRenderer.enabled = true;
                _banditRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (_defaultBodyMesh != null) _banditRenderer.sharedMesh = _defaultBodyMesh;
            }

            if (_viewmodelRig != null)
            {
                _viewmodelRig.SetActive(false);
            }
        }

        private void UpdateTrueFirstPersonMode(bool isAiming)
        {
            if (_animator == null || _animator.layerCount <= 1) return;
            bool combatActive = isAiming || (_playerMovement != null && _playerMovement.IsAttacking);
            float targetWeight = combatActive ? 1.0f : 0.0f;
            float speed = combatActive ? 22.0f : 8.0f;
            float nextWeight = Mathf.MoveTowards(_animator.GetLayerWeight(1), targetWeight, Time.deltaTime * speed);
            _animator.SetLayerWeight(1, nextWeight);

            if (combatActive)
            {
                var stateInfo = _animator.GetCurrentAnimatorStateInfo(1);
                if (stateInfo.shortNameHash != AnimStateFiringRifle && !stateInfo.IsName("AK47_Reload"))
                {
                    _animator.CrossFadeInFixedTime(AnimStateFiringRifle, 0.06f, 1);
                }
            }
        }

        private void UpdateTrueFirstPersonCenter()
        {
            if (_playerMovement == null) return;
            _playerMovement.EyeOffsetRight = Mathf.Lerp(_opt1EyeRight, _firingSightRightOffset, _currentCenterWeight);
            _playerMovement.EyeOffsetForward = Mathf.Lerp(_opt1EyeForward, _firingEyeForwardOffset, _currentCenterWeight);
            _playerMovement.EyeOffsetUp = Mathf.Lerp(_opt1EyeUp, _firingEyeUpOffset, _currentCenterWeight);
            _playerMovement.FieldOfViewProperty = Mathf.Lerp(_opt1FOV, _adsFOV, _currentCenterWeight);
        }

        private void ApplyCombatReady(bool isLocalOwner)
        {
            _playerMovement.EyeOffsetForward = _opt2EyeForward;
            _playerMovement.EyeOffsetUp = _opt2EyeUp;
            _playerMovement.EyeOffsetRight = _opt2EyeRight;
            _playerMovement.FieldOfViewProperty = _opt2FOV;
            _playerMovement.NearClipPlane = 0.015f;

            if (_animator != null && _animator.layerCount > 1)
            {
                _animator.SetLayerWeight(1, 1.0f);
                _animator.CrossFadeInFixedTime(AnimStateFiringRifle, 0.2f, 1);
            }

            if (_weaponHandler != null && _weaponHandler.EquippedWeapon != null)
            {
                _weaponHandler.EquippedWeapon.gameObject.SetActive(true);
            }
            if (_banditRenderer != null)
            {
                _banditRenderer.enabled = true;
                _banditRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (_defaultBodyMesh != null) _banditRenderer.sharedMesh = _defaultBodyMesh;
            }

            if (_viewmodelRig != null)
            {
                _viewmodelRig.SetActive(false);
            }
        }

        private void UpdateCombatReadyMode()
        {
            if (_animator != null && _animator.layerCount > 1)
            {
                var stateInfo = _animator.GetCurrentAnimatorStateInfo(1);
                if (stateInfo.shortNameHash != AnimStateFiringRifle && !stateInfo.IsName("AK47_Reload"))
                {
                    _animator.CrossFadeInFixedTime(AnimStateFiringRifle, 0.2f, 1);
                }
            }
        }

        private void UpdateCombatReadyCenter()
        {
            if (_playerMovement == null) return;
            _playerMovement.EyeOffsetRight = Mathf.Lerp(_opt2EyeRight, _firingSightRightOffset, _currentCenterWeight);
            _playerMovement.EyeOffsetForward = Mathf.Lerp(_opt2EyeForward, _firingEyeForwardOffset, _currentCenterWeight);
            _playerMovement.EyeOffsetUp = Mathf.Lerp(_opt2EyeUp, _firingEyeUpOffset, _currentCenterWeight);
            _playerMovement.FieldOfViewProperty = Mathf.Lerp(_opt2FOV, _adsFOV, _currentCenterWeight);
        }

        private void ApplyModernWarfareViewmodel(bool isLocalOwner)
        {
            _playerMovement.EyeOffsetForward = 0.05f;
            _playerMovement.EyeOffsetUp = 0.02f;
            _playerMovement.EyeOffsetRight = 0f;
            _playerMovement.FieldOfViewProperty = _opt2FOV;
            _playerMovement.NearClipPlane = 0.015f;

            // Keep Upper Body Layer 1 active so 3rd-person shadow & remote observers play Firing Rifle
            if (_animator != null && _animator.layerCount > 1)
            {
                _animator.SetLayerWeight(1, 1.0f);
            }

            EnsureViewmodelRig();

            if (isLocalOwner)
            {
                if (_viewmodelRig != null)
                {
                    _viewmodelRig.SetActive(true);
                }

                // Hide 3rd-person full body for the local camera (keep shadows)
                if (_banditRenderer != null)
                {
                    _banditRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                }

                // Hide 3rd-person weapon for local player camera so viewmodel takes over
                if (_weaponHandler != null && _weaponHandler.EquippedWeapon != null)
                {
                    _weaponHandler.EquippedWeapon.gameObject.SetActive(false);
                }

                // Immediately sync rest pose so Editor/Inspector preview is exact
                UpdateModernWarfareViewmodel(0f, false);
            }
            else
            {
                if (_viewmodelRig != null)
                {
                    _viewmodelRig.SetActive(false);
                }
                if (_banditRenderer != null)
                {
                    _banditRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    if (_defaultBodyMesh != null) _banditRenderer.sharedMesh = _defaultBodyMesh;
                }
                if (_weaponHandler != null && _weaponHandler.EquippedWeapon != null)
                {
                    _weaponHandler.EquippedWeapon.gameObject.SetActive(true);
                }
            }
        }

        private static readonly (string name, Quaternion rot)[] HumanPoseFingerGrips = new (string, Quaternion)[]
        {
            // Exact Humanoid-muscle solved finger rotations generated via AIRiggingAndAnimationToolkit.ApplyHumanoidMusclePose:
            // Left Hand Cradle (thumb tucked snugly along upper-left handguard, 4 fingers curled smoothly around wood bottom)
            ("c_thumb1.l", new Quaternion(-0.03490f, -0.55666f, 0.55288f, 0.61906f)),
            ("c_thumb2.l", new Quaternion(-0.16189f, 0.06245f, -0.06522f, 0.98267f)),
            ("c_thumb3.l", new Quaternion(-0.22949f, -0.03232f, -0.07941f, 0.96953f)),
            ("c_index1.l", new Quaternion(-0.51219f, -0.05496f, 0.03037f, 0.85657f)),
            ("c_index2.l", new Quaternion(-0.51453f, 0.02301f, -0.06308f, 0.85484f)),
            ("c_index3.l", new Quaternion(-0.56951f, 0.01413f, -0.09807f, 0.81599f)),
            ("c_middle1.l", new Quaternion(-0.49911f, -0.03372f, -0.02287f, 0.86558f)),
            ("c_middle2.l", new Quaternion(-0.50608f, 0.00842f, -0.07393f, 0.85927f)),
            ("c_middle3.l", new Quaternion(-0.55515f, 0.06452f, -0.15736f, 0.81418f)),
            ("c_ring1.l", new Quaternion(-0.48919f, 0.01666f, -0.08710f, 0.86766f)),
            ("c_ring2.l", new Quaternion(-0.50453f, 0.01332f, -0.09460f, 0.85809f)),
            ("c_ring3.l", new Quaternion(-0.56223f, 0.00705f, -0.10494f, 0.82026f)),
            ("c_pinky1.l", new Quaternion(-0.48512f, 0.07747f, -0.12256f, 0.86234f)),
            ("c_pinky2.l", new Quaternion(-0.49531f, 0.01597f, -0.14401f, 0.85655f)),
            ("c_pinky3.l", new Quaternion(-0.54572f, -0.01372f, -0.09300f, 0.83268f)),
            // Right Hand Pistol Grip (index lightly curved at trigger, other 3 curled tightly around grip)
            ("c_thumb1.r", new Quaternion(-0.16457f, -0.46503f, 0.50182f, -0.71052f)),
            ("c_thumb2.r", new Quaternion(0.33637f, 0.05624f, -0.15195f, -0.92769f)),
            ("c_thumb3.r", new Quaternion(0.41894f, -0.02244f, -0.16049f, -0.89343f)),
            ("c_index1.r", new Quaternion(0.47351f, -0.08018f, -0.04256f, -0.87610f)),
            ("c_index2.r", new Quaternion(0.52118f, 0.02291f, -0.06387f, -0.85074f)),
            ("c_index3.r", new Quaternion(0.54391f, 0.01462f, -0.09402f, -0.83373f)),
            ("c_middle1.r", new Quaternion(0.61530f, -0.06347f, -0.10963f, -0.77805f)),
            ("c_middle2.r", new Quaternion(0.62361f, 0.00660f, -0.09281f, -0.77618f)),
            ("c_middle3.r", new Quaternion(0.64934f, 0.05702f, -0.18371f, -0.73577f)),
            ("c_ring1.r", new Quaternion(0.61351f, -0.00346f, -0.16803f, -0.77160f)),
            ("c_ring2.r", new Quaternion(0.62100f, 0.01096f, -0.11826f, -0.77476f)),
            ("c_ring3.r", new Quaternion(0.65783f, 0.00950f, -0.12791f, -0.74217f)),
            ("c_pinky1.r", new Quaternion(0.60827f, 0.07010f, -0.16621f, -0.77296f)),
            ("c_pinky2.r", new Quaternion(0.60930f, 0.01464f, -0.17676f, -0.77285f)),
            ("c_pinky3.r", new Quaternion(0.64181f, -0.00478f, -0.12080f, -0.75728f))
        };

        /// <summary>
        /// Pre-aligns the Modern Warfare viewmodel toward the Firing Pose before discharge reads MuzzlePoint,
        /// preventing the first shot out of Sprint, Crouch, or Jump from spawning off a canted barrel.
        /// </summary>
        public void PrepareViewmodelForFiring()
        {
            if (_mode != FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare) return;

            _currentSprintWeight = Mathf.Min(_currentSprintWeight, 0.12f);
            _currentCrouchWeight = Mathf.Min(_currentCrouchWeight, 0.12f);
            _currentJumpWeight = Mathf.Min(_currentJumpWeight, 0.12f);

            ApplyViewmodelWeaponPose(_lastProcOffset, _lastProcRot);
        }

        private void ApplyViewmodelWeaponPose(Vector3 procOffset, Quaternion procRot)
        {
            if (_viewmodelWeapon == null) return;

            Vector3 readyPos = _vmHipPosition
                + (_vmCrouchPositionOffset * _currentCrouchWeight)
                + (_vmJumpPositionOffset * _currentJumpWeight);
            Quaternion readyRot = Quaternion.Euler(_vmHipEuler)
                * Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(_vmCrouchEuler), _currentCrouchWeight)
                * Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(_vmJumpEuler), _currentJumpWeight);

            Vector3 stancePos = Vector3.Lerp(readyPos, _vmHipPosition + _vmSprintPositionOffset, _currentSprintWeight);
            Quaternion stanceRot = Quaternion.Slerp(readyRot, Quaternion.Euler(_vmSprintEuler), _currentSprintWeight);

            _viewmodelWeapon.localPosition = Vector3.Lerp(stancePos, _vmAdsPosition, _currentAdsWeight) + procOffset;
            _viewmodelWeapon.localRotation = Quaternion.Slerp(stanceRot, Quaternion.Euler(_vmAdsEuler), _currentAdsWeight) * procRot;
        }

        private void UpdateModernWarfareViewmodel(float dt, bool isAiming)
        {
            if (_playerMovement == null) return;
            if (_viewmodelRig == null || _viewmodelWeapon == null || _viewmodelArms == null || _vmSpine03 == null)
            {
                EnsureViewmodelRig();
            }
            if (_viewmodelRig == null || _viewmodelWeapon == null) return;

            // 1. Locomotion, Firing, & ADS Stance Blending
            // Firing (IsAttacking) or ADS (Right Click) immediately suppresses Sprint, Crouch-cant, and Jump-cant
            // so the weapon and hands transition into the crisp Firing Pose across every locomotion state.
            var loco = _playerMovement.CurrentLocomotion;
            bool isAirborne = _playerMovement.IsAirborne || !_playerMovement.IsGrounded;
            bool isGrounded = !isAirborne;
            bool isCrouching = _playerMovement.IsCrouching;
            bool isFiring = _playerMovement.IsAttacking;
            bool isSprintJump = isAirborne && _playerMovement.IsSprintJumpActive;

            bool isMovingOnGround = isGrounded && (loco == LocomotionState.Jog ||
                                                   loco == LocomotionState.Running ||
                                                   loco == LocomotionState.CrouchedRun);

            bool isSprinting = !isAiming && !isFiring && (_playerMovement.IsSprinting || isSprintJump);
            float targetAds = isAiming ? 1.0f : 0.0f;
            float targetSprint = isSprinting ? 1.0f : 0.0f;
            float targetCrouch = (!isAiming && !isFiring && isCrouching && isGrounded) ? 1.0f : 0.0f;
            float targetJump = (!isAiming && !isFiring && isAirborne && !isSprinting) ? 1.0f : 0.0f;

            if (dt > 0f)
            {
                float sprintSpeed = targetSprint < _currentSprintWeight ? ((isFiring || isAiming) ? 28.0f : 12.0f) : 9.0f;
                float crouchSpeed = targetCrouch < _currentCrouchWeight ? ((isFiring || isAiming) ? 26.0f : 10.0f) : 10.0f;
                float jumpSpeed = targetJump < _currentJumpWeight ? ((isFiring || isAiming) ? 26.0f : 12.0f) : 9.0f;

                _currentAdsWeight = Mathf.MoveTowards(_currentAdsWeight, targetAds, dt * _adsTransitionSpeed);
                _currentSprintWeight = Mathf.MoveTowards(_currentSprintWeight, targetSprint, dt * sprintSpeed);
                _currentCrouchWeight = Mathf.MoveTowards(_currentCrouchWeight, targetCrouch, dt * crouchSpeed);
                _currentJumpWeight = Mathf.MoveTowards(_currentJumpWeight, targetJump, dt * jumpSpeed);
            }
            else
            {
                _currentAdsWeight = targetAds;
                _currentSprintWeight = targetSprint;
                _currentCrouchWeight = targetCrouch;
                _currentJumpWeight = targetJump;
                _recoilOffset = Vector3.zero;
                _recoilRotOffset = Vector3.zero;
                _lastProcOffset = Vector3.zero;
                _lastProcRot = Quaternion.identity;
                _currentMouseSway = Vector2.zero;
                _currentStrafeRoll = 0f;
                _bobAmpWeight = 0f;
            }

            // ADS FOV Zoom: 80 deg (hip) -> 68 deg (iron sights)
            _playerMovement.FieldOfViewProperty = Mathf.Lerp(_opt2FOV, _adsFOV, _currentAdsWeight);

            float adsDamp = 1f - (_currentAdsWeight * 0.88f);
            float fireBobDamp = (isFiring && !isAiming) ? 0.45f : 1.0f;

            // 2. Idle Breathing (~1.8 Hz, 0.003m vertical sine + micro-rotation)
            _breathTimer += dt * (_idleBreathingHz * 2f * Mathf.PI);
            float breathY = Mathf.Sin(_breathTimer) * _idleBreathingAmp * adsDamp * (1f - _currentSprintWeight * 0.4f);
            float breathRotX = Mathf.Cos(_breathTimer) * 0.25f * adsDamp;

            // 3. Grounded Walk / Crouch-Run / Tactical Sprint Figure-8 Lissajous Sway (paused mid-air)
            float bobFreq = isSprinting ? 13.5f : (loco == LocomotionState.CrouchedRun ? 6.0f : 7.8f);
            if (dt > 0f && isMovingOnGround)
            {
                _bobTimer += dt * bobFreq;
            }
            float targetBobAmp = !isMovingOnGround ? 0f : (isSprinting ? 1.9f : (loco == LocomotionState.CrouchedRun ? 0.78f : 1.0f));
            _bobAmpWeight = dt > 0f
                ? Mathf.MoveTowards(_bobAmpWeight, targetBobAmp, dt * (isMovingOnGround ? 9f : 12f))
                : 0f;

            float bobX = Mathf.Sin(_bobTimer) * _walkBobAmount * _bobAmpWeight * adsDamp * fireBobDamp;
            float bobY = Mathf.Cos(_bobTimer * 2f) * (_walkBobAmount * 0.70f) * _bobAmpWeight * adsDamp * fireBobDamp;

            // 4. Mouse Look Sway & Strafe Roll Inertia
            var mouse = Mouse.current;
            if (dt > 0f && mouse != null)
            {
                Vector2 mouseDelta = mouse.delta.ReadValue();
                Vector2 targetSway = new Vector2(-mouseDelta.x, -mouseDelta.y) * 0.035f;
                targetSway.x = Mathf.Clamp(targetSway.x, -0.025f, 0.025f);
                targetSway.y = Mathf.Clamp(targetSway.y, -0.025f, 0.025f);
                _currentMouseSway = Vector2.Lerp(_currentMouseSway, targetSway, dt * 10f);
            }
            else if (dt > 0f)
            {
                _currentMouseSway = Vector2.Lerp(_currentMouseSway, Vector2.zero, dt * 10f);
            }

            var keyboard = Keyboard.current;
            float strafeInput = 0f;
            if (dt > 0f && keyboard != null)
            {
                if (keyboard.aKey.isPressed) strafeInput -= 1f;
                if (keyboard.dKey.isPressed) strafeInput += 1f;
            }
            _currentStrafeRoll = dt > 0f ? Mathf.Lerp(_currentStrafeRoll, -strafeInput * 2.0f, dt * 10f) : 0f;

            // 5. Firing Recoil Spring-Damper Recovery
            if (dt > 0f)
            {
                _recoilOffset = Vector3.Lerp(_recoilOffset, Vector3.zero, dt * 16f);
                _recoilRotOffset = Vector3.Lerp(_recoilRotOffset, Vector3.zero, dt * 16f);
            }

            Vector3 procOffset = new Vector3(
                bobX + (_currentMouseSway.x * 0.14f * adsDamp),
                breathY + bobY + (_currentMouseSway.y * 0.14f * adsDamp),
                0f
            ) + _recoilOffset;

            Quaternion procRot = Quaternion.Euler(
                breathRotX + _recoilRotOffset.x - (_currentMouseSway.y * _mouseSwayAmount * adsDamp),
                _recoilRotOffset.y + (_currentMouseSway.x * _mouseSwayAmount * adsDamp),
                _recoilRotOffset.z + (_currentStrafeRoll * adsDamp)
            );

            _lastProcOffset = procOffset;
            _lastProcRot = procRot;

            // 6. Pose Viewmodel AK-47 (Hip <-> Crouch/Jump <-> Tactical Sprint <-> ADS)
            ApplyViewmodelWeaponPose(procOffset, procRot);

            // 7. Position Viewmodel Shoulders & Solve Two-Bone IK AFTER weapon transform is finalized
            Transform camT = _viewmodelRig.transform;
            if (_viewmodelArms != null)
            {
                _viewmodelArms.transform.localPosition = new Vector3(0f, -1.66f, 0.05f);
                _viewmodelArms.transform.localRotation = Quaternion.identity;

                if (_vmSpine03 != null) _vmSpine03.rotation = camT.rotation;

                Vector3 baseLeftAnchor = Vector3.Lerp(_vmLeftShoulderAnchor, new Vector3(-0.11f, -0.68f, 0.21f), _currentSprintWeight);
                Vector3 adsLeftAnchor = new Vector3(-0.02f, -0.48f, 0.40f);
                Vector3 armLDest = camT.TransformPoint(Vector3.Lerp(baseLeftAnchor, adsLeftAnchor, _currentAdsWeight));
                if (_vmShoulderL != null && _vmArmL != null)
                {
                    _vmShoulderL.position += (armLDest - _vmArmL.position);
                }

                Vector3 baseRightAnchor = Vector3.Lerp(_vmRightShoulderAnchor, new Vector3(0.22f, -0.68f, -0.18f), _currentSprintWeight);
                Vector3 adsRightAnchor = new Vector3(0.12f, -0.56f, -0.01f);
                Vector3 armRDest = camT.TransformPoint(Vector3.Lerp(baseRightAnchor, adsRightAnchor, _currentAdsWeight));
                if (_vmShoulderR != null && _vmArmR != null)
                {
                    _vmShoulderR.position += (armRDest - _vmArmR.position);
                }
            }

            if (_vmArmR != null && _vmForearmR != null && _vmHandR != null && _vmRGripTarget != null)
            {
                Vector3 rPole = camT.TransformPoint(Vector3.Lerp(_vmRightElbowPole, new Vector3(0.22f, -0.84f, -0.24f), _currentSprintWeight * (1f - _currentAdsWeight)));
                SolveTwoBoneIKInternal(_vmArmR, _vmForearmR, _vmHandR, _vmRGripTarget.position, _vmRGripTarget.rotation, rPole);
                ApplyForearmTwist(_vmForearmR, _vmHandR, _vmTwistR);
            }

            if (_vmArmL != null && _vmForearmL != null && _vmHandL != null && _vmLGripTarget != null)
            {
                Vector3 sprintLeftPole = new Vector3(-0.10f, -0.74f, 0.23f);
                Vector3 baseLeftPole = Vector3.Lerp(_vmLeftElbowPole, sprintLeftPole, _currentSprintWeight);
                Vector3 adsLeftPole = new Vector3(0.03f, -0.78f, 0.40f);
                Vector3 lPole = camT.TransformPoint(Vector3.Lerp(baseLeftPole, adsLeftPole, _currentAdsWeight));
                SolveTwoBoneIKInternal(_vmArmL, _vmForearmL, _vmHandL, _vmLGripTarget.position, _vmLGripTarget.rotation, lPole);
                ApplyForearmTwist(_vmForearmL, _vmHandL, _vmTwistL);
            }

            ApplyViewmodelFingerPoses();
        }

        private void ApplyViewmodelFingerPoses()
        {
            if (_vmFingerPoseCache == null) return;
            for (int i = 0; i < _vmFingerPoseCache.Length; i++)
            {
                if (_vmFingerPoseCache[i].bone != null)
                {
                    _vmFingerPoseCache[i].bone.localRotation = _vmFingerPoseCache[i].rot;
                }
            }
        }

        public void ApplyViewmodelRecoilKick()
        {
            if (_mode == FPSPerspectiveMode.DedicatedViewmodel_ModernWarfare)
            {
                _recoilOffset += new Vector3(0f, 0.005f, -0.045f);
                _recoilOffset.z = Mathf.Max(_recoilOffset.z, -0.085f);
                _recoilOffset.y = Mathf.Min(_recoilOffset.y, 0.015f);
                _recoilRotOffset += new Vector3(-2.6f, UnityEngine.Random.Range(-0.45f, 0.45f), UnityEngine.Random.Range(-0.5f, 0.5f));
                _recoilRotOffset.x = Mathf.Max(_recoilRotOffset.x, -7.5f);
            }
        }
        #endregion

        #region Viewmodel Hierarchy Setup
        public void EnsureViewmodelRig()
        {
            if (_playerMovement == null) CacheComponents();
            Transform camTrans = _playerMovement != null ? _playerMovement.CameraTransform : null;
            if (camTrans == null)
            {
                camTrans = transform.Find("PlayerCamera_Auto") ?? transform.Find("Main Camera") ?? transform.Find("Camera");
            }
            if (camTrans == null) return;

            Transform existingRig = camTrans.Find("FPS_ViewmodelRig");
            if (existingRig == null)
            {
                var rigGo = new GameObject("FPS_ViewmodelRig");
                rigGo.transform.SetParent(camTrans, false);
                rigGo.transform.localPosition = Vector3.zero;
                rigGo.transform.localRotation = Quaternion.identity;
                existingRig = rigGo.transform;
            }

            _viewmodelRig = existingRig.gameObject;

            // 1. Ensure Weapon
            Transform vmRifle = existingRig.Find("Viewmodel_AK47");
            if (vmRifle == null)
            {
                GameObject sourcePrefab = null;
#if UNITY_EDITOR
                sourcePrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Models/Weapon/AK47NoSubdiv.fbx");
#endif
                if (sourcePrefab == null && _weaponHandler != null && _weaponHandler.EquippedWeapon != null)
                {
                    sourcePrefab = _weaponHandler.EquippedWeapon.gameObject;
                }

                if (sourcePrefab != null)
                {
                    var clone = Instantiate(sourcePrefab, existingRig);
                    clone.name = "Viewmodel_AK47";
                    clone.SetActive(true);
                    clone.transform.localPosition = _vmHipPosition;
                    clone.transform.localRotation = Quaternion.Euler(_vmHipEuler);
                    clone.transform.localScale = Vector3.one;

#if UNITY_EDITOR
                    var rifleMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Models/Weapon/Materials/RIfle.mat");
                    if (rifleMat != null)
                    {
                        foreach (var mr in clone.GetComponentsInChildren<MeshRenderer>()) mr.sharedMaterial = rifleMat;
                    }
#endif

                    foreach (var c in clone.GetComponentsInChildren<Collider>()) SafeDestroy(c);
                    foreach (var nb in clone.GetComponentsInChildren<NetworkBehaviour>()) SafeDestroy(nb);
                    foreach (var no in clone.GetComponentsInChildren<NetworkObject>()) SafeDestroy(no);
                    vmRifle = clone.transform;
                }
            }

            _viewmodelWeapon = vmRifle;
            if (_viewmodelWeapon != null)
            {
                EnsureMuzzlePointOnTransform(_viewmodelWeapon);
                _vmRGripTarget = EnsureRightHandGripTarget(_viewmodelWeapon);
                _vmLGripTarget = EnsureLeftHandGripTarget(_viewmodelWeapon);
            }

            // 2. Ensure Arms Character (Full UpperArm + Forearm + Gloves mesh, Animator disabled so IK & HumanPose fingers drive bones)
            Transform vmArms = existingRig.Find("Viewmodel_BanditArms");
            if (vmArms == null)
            {
                GameObject banditFbx = null;
#if UNITY_EDITOR
                banditFbx = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Abandoned World/MilitaryMercenaryBandit/Meshes/MilitaryMercenaryBandit.fbx");
#endif
                if (banditFbx != null)
                {
                    var armsGo = Instantiate(banditFbx, existingRig);
                    armsGo.name = "Viewmodel_BanditArms";
                    armsGo.transform.localPosition = new Vector3(0f, -1.66f, 0.05f);
                    armsGo.transform.localRotation = Quaternion.identity;
                    armsGo.transform.localScale = Vector3.one;
                    foreach (var c in armsGo.GetComponentsInChildren<Collider>()) SafeDestroy(c);
                    vmArms = armsGo.transform;
                }
            }

            if (vmArms != null)
            {
                vmArms.localPosition = new Vector3(0f, -1.66f, 0.05f);
                vmArms.localRotation = Quaternion.identity;

                var vmAnim = vmArms.GetComponent<Animator>();
                if (vmAnim != null) vmAnim.enabled = false;

                var smr = vmArms.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (smr != null)
                {
                    if (_fpsArmsMesh != null) smr.sharedMesh = _fpsArmsMesh;
                    smr.enabled = true;
                    smr.updateWhenOffscreen = true;
                    smr.localBounds = new Bounds(Vector3.zero, Vector3.one * 50f);
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                // Hide head/neck bones defensively in case a full-body mesh is ever assigned
                var headB = FindDeepChild(vmArms, "head.x");
                var neckB = FindDeepChild(vmArms, "neck.x");
                if (headB != null) headB.localScale = Vector3.zero;
                if (neckB != null) neckB.localScale = Vector3.zero;
            }

            _viewmodelArms = vmArms != null ? vmArms.gameObject : null;
            if (_viewmodelArms != null)
            {
                _vmSpine03 = FindDeepChild(_viewmodelArms.transform, "spine_03.x");

                _vmShoulderR = FindDeepChild(_viewmodelArms.transform, "shoulder.r");
                _vmArmR = FindDeepChild(_viewmodelArms.transform, "arm_stretch.r");
                _vmForearmR = FindDeepChild(_viewmodelArms.transform, "forearm_stretch.r");
                _vmHandR = FindDeepChild(_viewmodelArms.transform, "hand.r");
                _vmTwistR = FindDeepChild(_viewmodelArms.transform, "forearm_twist.r");

                _vmShoulderL = FindDeepChild(_viewmodelArms.transform, "shoulder.l");
                _vmArmL = FindDeepChild(_viewmodelArms.transform, "arm_stretch.l");
                _vmForearmL = FindDeepChild(_viewmodelArms.transform, "forearm_stretch.l");
                _vmHandL = FindDeepChild(_viewmodelArms.transform, "hand.l");
                _vmTwistL = FindDeepChild(_viewmodelArms.transform, "forearm_twist.l");

                _vmFingerPoseCache = new (Transform, Quaternion)[HumanPoseFingerGrips.Length];
                for (int i = 0; i < HumanPoseFingerGrips.Length; i++)
                {
                    _vmFingerPoseCache[i] = (
                        FindDeepChild(_viewmodelArms.transform, HumanPoseFingerGrips[i].name),
                        HumanPoseFingerGrips[i].rot
                    );
                }
            }
        }

        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }

        private static Transform FindDeepChild(Transform parent, string childName)
        {
            if (string.Equals(parent.name, childName, StringComparison.OrdinalIgnoreCase))
                return parent;

            for (int i = 0; i < parent.childCount; i++)
            {
                var result = FindDeepChild(parent.GetChild(i), childName);
                if (result != null) return result;
            }
            return null;
        }
        #endregion

        #region HUD Notification
        private void ShowBanner(string text)
        {
            _bannerMessage = text;
            _bannerTimer = 2.5f;
            Debug.Log($"[FPSPerspectiveController] {text}");
        }

        private void OnGUI()
        {
            if (!_showOnScreenBanner || _bannerTimer <= 0f) return;

            GUIStyle style = new GUIStyle(GUI.skin.box);
            style.fontSize = 15;
            style.fontStyle = FontStyle.Bold;
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = Color.yellow;

            float width = 450f;
            float height = 36f;
            float x = (Screen.width - width) * 0.5f;
            float y = 50f;

            GUI.Box(new Rect(x, y, width, height), $"{_bannerMessage}  (Press {_cycleModeKey} to Switch)", style);
        }
        #endregion
    }
}
