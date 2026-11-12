using System;
using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using Scorched.Combat;
using Scorched.Weapons;

namespace Scorched.Player
{
    /// <summary>
    /// Locomotion states matching the Animator Controller states.
    /// Synchronized across the network for seamless remote player animation.
    /// </summary>
    public enum LocomotionState : byte
    {
        Idle = 0,
        Jog = 1,
        Running = 2,
        CrouchIdle = 3,
        CrouchedRun = 4
    }

    /// <summary>
    /// One-shot action animations matching the Animator Controller states.
    /// Broadcast to remote clients via RPCs.
    /// </summary>
    public enum ActionAnimation : byte
    {
        None = 0,
        Jumping = 1,
        SprintJump = 2,
        FiringRifle = 3
    }

    /// <summary>
    /// Distinct player animation states used for weapon positioning and pose offsets.
    /// </summary>
    public enum WeaponPoseState : byte
    {
        Idle = 0,
        Jog = 1,
        Running = 2,
        Jumping = 3,
        SprintJump = 4,
        CrouchIdle = 5,
        CrouchedRun = 6,
        FiringRifle = 7
    }

    /// <summary>
    /// Production-ready multiplayer player movement controller for "Scorched".
    /// Features:
    /// - Auto-requires and self-configures all necessary components:
    ///   CharacterController, NetworkObject, NetworkTransform (Owner-Auth), and Animator.
    /// - Direct animation playback matching Animator state names:
    ///   Idle, Jog, Running, Jumping, Sprint Jump, Crouch Idle, Crouched Run, Firing Rifle
    /// - 2 Jump types: Stationary 'Jumping' vs moving 'Sprint Jump'
    /// - Default movement is 'Jog', holding Shift accelerates to 'Running'
    /// - Left-click triggers 'Firing Rifle' attack animation
    /// - Network-synchronized locomotion & action animations via NGO 2.x
    /// - Auto-generates local FPS camera if no camera is attached to prefab
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkTransform))]
    [RequireComponent(typeof(Animator))]
    [RequireComponent(typeof(PlayerHealthController))]
    [RequireComponent(typeof(PlayerWeaponHandler))]
    [RequireComponent(typeof(NetworkWeaponController))]
    public class PlayerMovement : NetworkBehaviour, IDamageable
    {
        #region Animation Hashes (Direct State Playback)
        private static readonly int AnimStateIdle = Animator.StringToHash("Idle");
        private static readonly int AnimStateJog = Animator.StringToHash("Jog");
        private static readonly int AnimStateRunning = Animator.StringToHash("Running");
        private static readonly int AnimStateJumping = Animator.StringToHash("Jumping");
        private static readonly int AnimStateSprintJump = Animator.StringToHash("Sprint Jump");
        private static readonly int AnimStateCrouchIdle = Animator.StringToHash("Crouch Idle");
        private static readonly int AnimStateCrouchedRun = Animator.StringToHash("Crouched Run");
        private static readonly int AnimStateDownIdle = Animator.StringToHash("Down Idle");
        private static readonly int AnimStateDownWalking = Animator.StringToHash("Down Walking");
        private static readonly int AnimStateDeath = Animator.StringToHash("Death");
        private static readonly int AnimStateFiringRifle = Animator.StringToHash("Firing Rifle");
        private static readonly int AnimStateEmpty = Animator.StringToHash("Empty");
        private static readonly int AnimParamSpeedMultiplier = Animator.StringToHash("MoveSpeedMultiplier");
        private static readonly int AnimParamIsFiring = Animator.StringToHash("IsFiring");
        #endregion

        #region Serialized Fields
        [Header("Movement Speeds (FPS Tuned)")]
        [Tooltip("Default movement speed (Jogging).")]
        [SerializeField] private float _jogSpeed = 4.5f;

        [Tooltip("Sprint speed while holding Left Shift.")]
        [SerializeField] private float _runSpeed = 7.5f;

        [Tooltip("Crouched movement speed.")]
        [SerializeField] private float _crouchSpeed = 2.5f;

        [Tooltip("Speed multiplier for backward and strafe movement.")]
        [SerializeField] private float _strafeMultiplier = 0.85f;

        [Header("Jump & Gravity")]
        [Tooltip("Maximum jump height in meters.")]
        [SerializeField] private float _jumpHeight = 1.25f;

        [Tooltip("Gravity acceleration (negative value).")]
        [SerializeField] private float _gravity = -19.62f;

        [Tooltip("Downward force applied when grounded to snap character to uneven terrain.")]
        [SerializeField] private float _groundStickForce = -2.0f;

        [Header("Crouch Configuration")]
        [Tooltip("Standing collider height.")]
        [SerializeField] private float _standingHeight = 1.8f;

        [Tooltip("Crouched collider height.")]
        [SerializeField] private float _crouchHeight = 1.2f;

        [Tooltip("Standing collider center offset.")]
        [SerializeField] private Vector3 _standingCenter = new Vector3(0, 0.9f, 0);

        [Tooltip("Crouched collider center offset.")]
        [SerializeField] private Vector3 _crouchCenter = new Vector3(0, 0.6f, 0);

        [Header("Look & Camera Control")]
        [Tooltip("Camera transform for vertical pitch (tilt). Auto-created if null.")]
        [SerializeField] private Transform _cameraTransform;

        [Tooltip("Eye-level offset used when auto-generating camera.")]
        [SerializeField] private Vector3 _autoCameraOffset = new Vector3(0f, 1.6f, 0.1f);

        [Tooltip("Mouse horizontal look sensitivity.")]
        [SerializeField] private float _mouseSensitivityX = 2.0f;

        [Tooltip("Mouse vertical look sensitivity.")]
        [SerializeField] private float _mouseSensitivityY = 2.0f;

        [Tooltip("Minimum vertical look angle (degrees).")]
        [SerializeField] private float _minPitchAngle = -85.0f;

        [Tooltip("Maximum vertical look angle (degrees).")]
        [SerializeField] private float _maxPitchAngle = 85.0f;

        [Header("True First-Person Dynamic Eye-Level Camera")]
        [Tooltip("Field of view for first-person perspective (80 degrees for standard Modern Warfare weapon framing).")]
        [SerializeField] private float _fieldOfView = 80.0f;

        [Tooltip("The character's head bone (head.x). Auto-detected if unassigned.")]
        [SerializeField] private Transform _headBone;

        [Tooltip("The character's neck bone (neck.x). Auto-detected if unassigned.")]
        [SerializeField] private Transform _neckBone;

        [Tooltip("Forward offset from the anchor bone pivot to place the camera right at eye level.")]
        [SerializeField] private float _eyeOffsetForward = 0.04f;

        [Tooltip("Upward offset from the anchor bone pivot to the eye line.")]
        [SerializeField] private float _eyeOffsetUp = 0.02f;

        [Tooltip("Horizontal / side offset (0 = centered, >0 = right).")]
        [SerializeField] private float _eyeOffsetRight = 0.01f;

        [Tooltip("Camera tracking smoothness (0 = instant 1:1 lock, prevents camera lag from trailing inside head during locomotion).")]
        [SerializeField] private float _cameraFollowSpeed = 0.0f;

        [Tooltip("Camera near clip plane to prevent rifle, sight, and facial clipping (default 0.015 = 1.5cm).")]
        [SerializeField] private float _nearClipPlane = 0.015f;

        private Vector3 _proceduralCameraOffset = Vector3.zero;
        private Vector3 _proceduralCameraRotationOffset = Vector3.zero;

        public Vector3 ProceduralCameraOffset
        {
            get => _proceduralCameraOffset;
            set => _proceduralCameraOffset = value;
        }

        public Vector3 ProceduralCameraRotationOffset
        {
            get => _proceduralCameraRotationOffset;
            set => _proceduralCameraRotationOffset = value;
        }

        public Transform HeadBone => _headBone;
        public Transform NeckBone => _neckBone;

        [Header("Combat / Firing")]
        [Tooltip("Duration of the rifle firing animation before returning to locomotion (seconds).")]
        [SerializeField] private float _fireAnimationDuration = 0.35f;

        [Tooltip("Cooldown between rifle shots (seconds).")]
        [SerializeField] private float _fireRate = 0.12f;

        [Header("Animation Blending")]
        [Tooltip("CrossFade transition duration between locomotion states (seconds).")]
        [SerializeField] private float _locomotionTransitionDuration = 0.15f;

        [Tooltip("CrossFade transition duration for jump initiation (seconds).")]
        [SerializeField] private float _jumpTransitionDuration = 0.08f;

        [Tooltip("CrossFade transition duration for firing rifle (seconds).")]
        [SerializeField] private float _fireTransitionDuration = 0.05f;

        [Header("Procedural Spine Pitch (Aim Tilting)")]
        [Tooltip("Smoothing speed for procedural torso aim pitch adjustments.")]
        [SerializeField] private float _spinePitchSmoothSpeed = 25.0f;
        [Header("Vitals (Health & Stamina)")]
        [Tooltip("Maximum player health points.")]
        [SerializeField] private float _maxHealth = 100f;

        [Tooltip("Current player health points.")]
        [SerializeField] private float _currentHealth = 100f;

        [Tooltip("Maximum stamina points.")]
        [SerializeField] private float _maxStamina = 100f;

        [Tooltip("Current stamina points.")]
        [SerializeField] private float _currentStamina = 100f;

        [Tooltip("Stamina drain rate while sprinting (points per second).")]
        [SerializeField] private float _staminaDrainRate = 22.0f;

        [Tooltip("Stamina drained per jump (points).")]
        [SerializeField] private float _jumpStaminaCost = 10.0f;

        [Tooltip("Automatic stamina regeneration disabled (Food-only recovery).")]
        [SerializeField] private bool _disableAutoStaminaRegen = true;

        [Tooltip("Legacy stamina recovery rate (0 when Food-only stamina is enabled).")]
        [SerializeField] private float _staminaRegenRate = 0f;
        #endregion

        #region Network State
        /// <summary>
        /// Owner-replicated locomotion state for all remote clients.
        /// Late-joining clients receive the current state immediately on spawn.
        /// </summary>
        private readonly NetworkVariable<byte> _networkLocomotionState = new NetworkVariable<byte>(
            (byte)LocomotionState.Idle,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        /// <summary>
        /// Replicates backward movement state to play running/jogging animations in reverse across the network.
        /// </summary>
        private readonly NetworkVariable<bool> _networkIsMovingBackward = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        /// <summary>
        /// Replicates horizontal aim yaw across network so remote clients see this character rotate to face targets.
        /// </summary>
        private readonly NetworkVariable<float> _networkAimYaw = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        /// <summary>
        /// Replicates vertical aim pitch across network so remote clients see this character aiming up/down.
        /// </summary>
        private readonly NetworkVariable<float> _networkAimPitch = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        #endregion

        #region Private Runtime State
        private CharacterController _characterController;
        private NetworkTransform _networkTransform;
        private Animator _animator;
        private PlayerHealthController _healthController;
        private PlayerWeaponHandler _weaponHandler;
        private NetworkWeaponController _networkWeaponController;
        private Scorched.Survival.PlayerThermalController _thermalController;
        private bool _isDuplicateSceneInstance = false;

        private Vector3 _prevWorldPosition;
        private Vector3 _smoothedWorldVelocity;
        private bool _hasPrevWorldPos;

        private Vector3 _velocity;
        private Vector3 _airborneVelocity;
        private float _cameraPitch = 0.0f;
        private bool _isGrounded;
        private bool _isCrouching;
        private bool _isAirborne;
        private bool _isSprintJumpActive;

        private float _attackTimer = 0.0f;
        private float _lastFireTime = 0.0f;

        private int _currentAnimationHash;
        private LocomotionState _currentLocomotion = LocomotionState.Idle;

        // Procedural spine aiming bones & layer state
        private Transform _spineBone;
        private Transform _chestBone;
        private Transform _upperChestBone;
        private float _smoothedAimPitch;
        private bool _hasUpperBodyLayer;
        private bool _hasIsFiringParam;
        private FPSPerspectiveController _perspectiveController;
        #endregion

        #region Public Properties & State Accessors
        /// <summary>
        /// Transform of the upper chest bone (spine_03.x) for torso-relative IK and aim calculations.
        /// </summary>
        public Transform UpperChestBone
        {
            get
            {
                EnsureSpineBones();
                return _upperChestBone != null ? _upperChestBone : _chestBone;
            }
        }

        /// <summary>
        /// Current active pose state for weapon offset blending and attachments.
        /// </summary>
        public WeaponPoseState CurrentWeaponPoseState
        {
            get
            {
                if (_attackTimer > 0f)
                {
                    return WeaponPoseState.FiringRifle;
                }

                if (_isAirborne)
                {
                    return _isSprintJumpActive ? WeaponPoseState.SprintJump : WeaponPoseState.Jumping;
                }

                return _currentLocomotion switch
                {
                    LocomotionState.Jog => WeaponPoseState.Jog,
                    LocomotionState.Running => WeaponPoseState.Running,
                    LocomotionState.CrouchIdle => WeaponPoseState.CrouchIdle,
                    LocomotionState.CrouchedRun => WeaponPoseState.CrouchedRun,
                    _ => WeaponPoseState.Idle
                };
            }
        }

        #region Camera Telemetry Accessors
        public float CameraPitch => _cameraPitch;
        public float FieldOfView => _fieldOfView;
        public Transform CameraTransform => _cameraTransform;
        public float MouseSensitivityX => _mouseSensitivityX;
        public float MouseSensitivityY => _mouseSensitivityY;
        public bool IsCursorLocked => Cursor.lockState == CursorLockMode.Locked;
        public bool IsSprinting => _currentLocomotion == LocomotionState.Running;
        public bool IsGrounded => _isGrounded;
        public bool IsCrouching => _isCrouching;
        public bool IsAirborne => _isAirborne;
        public bool IsSprintJumpActive => _isSprintJumpActive;
        public LocomotionState CurrentLocomotion => _currentLocomotion;
        public Vector3 WorldVelocity => _smoothedWorldVelocity;
        public float CurrentStamina => _currentStamina;
        public float MaxStamina => _maxStamina;
        public float CurrentHealth => _healthController != null ? _healthController.CurrentHealthValue : _currentHealth;
        public bool IsDead => _healthController != null ? _healthController.IsDead : (_currentHealth <= 0f);
        public PlayerHealthController HealthController => _healthController;
        public bool IsAttacking => _attackTimer > 0f;

        private FPSPerspectiveController EnsurePerspectiveController()
        {
            if (_perspectiveController == null)
            {
                _perspectiveController = GetComponent<FPSPerspectiveController>();
            }
            return _perspectiveController;
        }
        public float EyeOffsetForward { get => _eyeOffsetForward; set => _eyeOffsetForward = value; }
        public float EyeOffsetUp { get => _eyeOffsetUp; set => _eyeOffsetUp = value; }
        public float EyeOffsetRight { get => _eyeOffsetRight; set => _eyeOffsetRight = value; }
        public float NearClipPlane { get => _nearClipPlane; set => _nearClipPlane = value; }
        public float FieldOfViewProperty { get => _fieldOfView; set => _fieldOfView = value; }

        /// <summary>
        /// Dynamically updates runtime look sensitivity and camera field of view from settings.
        /// </summary>
        public void ApplySettings(float sensitivity, float fov)
        {
            _mouseSensitivityX = sensitivity;
            _mouseSensitivityY = sensitivity;
            _fieldOfView = fov;
            if (_cameraTransform != null)
            {
                var cam = _cameraTransform.GetComponent<Camera>();
                if (cam != null)
                {
                    cam.fieldOfView = fov;
                }
            }
        }
        #endregion

        /// <summary>
        /// Manually forces playback of a specific weapon state animation for editor preview and pose scrubbing.
        /// </summary>
        public void PreviewStateAnimation(WeaponPoseState state, float normalizedTime = 0.15f)
        {
            int hash = state switch
            {
                WeaponPoseState.Jog => AnimStateJog,
                WeaponPoseState.Running => AnimStateRunning,
                WeaponPoseState.Jumping => AnimStateJumping,
                WeaponPoseState.SprintJump => AnimStateSprintJump,
                WeaponPoseState.CrouchIdle => AnimStateCrouchIdle,
                WeaponPoseState.CrouchedRun => AnimStateCrouchedRun,
                WeaponPoseState.FiringRifle => AnimStateFiringRifle,
                _ => AnimStateIdle
            };

            if (_animator != null)
            {
                _currentAnimationHash = hash;
                _animator.Play(hash, 0, normalizedTime);
                _animator.Update(0.001f);
            }
        }
        #endregion

        #region Unity Lifecycle & Auto-Configuration
        private void Awake()
        {
            // Only the pre-placed scene Player runs Awake before NetworkManager starts listening;
            // newly spawned ScorchedPlayer prefab clones instantiate while IsListening == true.
            if (!IsSpawned && NetworkManager.Singleton != null &&
                NetworkManager.Singleton.NetworkConfig?.PlayerPrefab != null &&
                !NetworkManager.Singleton.IsListening)
            {
                NetworkManager.Singleton.OnServerStarted += OnNetworkStartedCleanupDuplicate;
                NetworkManager.Singleton.OnClientStarted += OnNetworkStartedCleanupDuplicate;
            }

            _characterController = GetComponent<CharacterController>();
            if (_characterController != null)
            {
                _characterController.enabled = true;
            }

            _networkTransform = GetComponent<NetworkTransform>();
            _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
            _healthController = GetComponent<PlayerHealthController>();
            _weaponHandler = GetComponent<PlayerWeaponHandler>();
            _networkWeaponController = GetComponent<NetworkWeaponController>();
            _thermalController = GetComponent<Scorched.Survival.PlayerThermalController>();
            _prevWorldPosition = transform.position;
            _hasPrevWorldPos = true;
            EnsureHeadBone();
            EnsureSpineBones();
            CheckUpperBodyLayer();

            // Ensure NetworkTransform is configured for client-authoritative FPS movement
            if (_networkTransform != null)
            {
                _networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            }

            if (_animator == null)
            {
                Debug.LogError("[PlayerMovement] Animator component not found on player or children!", this);
            }

            // Load saved input and view settings
            if (PlayerPrefs.HasKey("Scorched_MouseSensitivity"))
            {
                float sens = PlayerPrefs.GetFloat("Scorched_MouseSensitivity", 2.0f);
                _mouseSensitivityX = sens;
                _mouseSensitivityY = sens;
            }
            if (PlayerPrefs.HasKey("Scorched_FOV"))
            {
                _fieldOfView = PlayerPrefs.GetFloat("Scorched_FOV", 75.0f);
            }
        }

        private void Start()
        {
            if (_isDuplicateSceneInstance)
            {
                return;
            }

            // Do not activate an unspawned scene player if NetworkManager is already listening with PlayerPrefab
            if (!IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && NetworkManager.Singleton.NetworkConfig?.PlayerPrefab != null)
            {
                _isDuplicateSceneInstance = true;
                Destroy(gameObject);
                return;
            }

            // Auto-setup camera and controls whether running in an offline test scene or spawned via NGO
            SetupCamera();
            if (!Scorched.UI.MainMenuUIManager.IsMenuOpen)
            {
                LockCursor();
            }
            else
            {
                UnlockCursor();
            }

            if (_characterController != null)
            {
                _characterController.enabled = true;
            }

            if (_healthController == null || _healthController.IsAlive)
            {
                PlayAnimationDirect(AnimStateIdle, 0f);
            }

            // Initialize tactical HUD for local client
            if (!IsSpawned || IsOwner)
            {
                if (Scorched.UI.ScorchedHUDController.Instance == null && FindAnyObjectByType<Scorched.UI.ScorchedHUDController>() == null)
                {
                    var hudGo = new GameObject("Scorched_HUD");
                    hudGo.AddComponent<Scorched.UI.ScorchedHUDController>();
                }
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Automatically called when this script is added to a GameObject or reset in the Inspector.
        /// Configures optimal FPS defaults for CharacterController, NetworkTransform, and Animator.
        /// </summary>
        private void Reset()
        {
            // Configure CharacterController with responsive FPS capsule defaults
            var cc = GetComponent<CharacterController>();
            if (cc != null)
            {
                cc.height = _standingHeight;
                cc.center = _standingCenter;
                cc.radius = 0.35f;
                cc.stepOffset = 0.3f;
                cc.slopeLimit = 45f;
                cc.minMoveDistance = 0f;
            }

            // Configure NetworkTransform for Owner authority
            var nt = GetComponent<NetworkTransform>();
            if (nt != null)
            {
                nt.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            }

            // Automatically link the 01_Player.controller asset if unassigned
            var anim = GetComponent<Animator>();
            if (anim != null && anim.runtimeAnimatorController == null)
            {
                var controller = UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/Player/01_Player.controller");
                if (controller != null)
                {
                    anim.runtimeAnimatorController = controller;
                    Debug.Log("[PlayerMovement] Automatically assigned '01_Player.controller' to Animator.");
                }
            }
        }
#endif

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (_healthController == null)
            {
                _healthController = GetComponent<PlayerHealthController>();
            }

            // Eradicate pre-placed scene Player duplicate when Netcode uses PlayerPrefab spawning!
            if (NetworkManager.Singleton != null &&
                NetworkManager.Singleton.NetworkConfig?.PlayerPrefab != null &&
                NetworkObject != null &&
                !NetworkObject.IsPlayerObject)
            {
                DisableDuplicateScenePlayerImmediately();
                if (IsServer)
                {
                    StartCoroutine(DespawnDuplicateScenePlayerCoroutine());
                }
                return;
            }

            // Locate existing camera or auto-generate one for the owner
            SetupCamera();

            if (IsOwner)
            {
                // Local player owns input, camera, and physics simulation
                if (!Scorched.UI.MainMenuUIManager.IsMenuOpen)
                {
                    LockCursor();
                }
                else
                {
                    UnlockCursor();
                }

                if (_cameraTransform != null)
                {
                    var cam = _cameraTransform.GetComponent<Camera>();
                    if (cam != null) cam.enabled = true;

                    var listener = _cameraTransform.GetComponent<AudioListener>();
                    if (listener != null) listener.enabled = true;
                }

                if (_characterController != null)
                {
                    _characterController.enabled = true;
                }

                // Align to assigned tactical spawn position if available
                if (Scorched.Player.TacticalSpawnManager.Instance != null &&
                    Scorched.Player.TacticalSpawnManager.Instance.TryGetAssignedSpawn(OwnerClientId, out Vector3 spawnPos, out Quaternion spawnRot))
                {
                    if (_characterController != null)
                    {
                        _characterController.enabled = false;
                        transform.position = spawnPos;
                        transform.rotation = spawnRot;
                        _characterController.enabled = true;
                    }
                    else
                    {
                        transform.position = spawnPos;
                        transform.rotation = spawnRot;
                    }
                    Debug.Log($"[PlayerMovement] Client {OwnerClientId} aligned to tactical spawn point at {spawnPos}.");
                }

                // Play initial local idle
                if (_healthController == null || _healthController.IsAlive)
                {
                    PlayAnimationDirect(AnimStateIdle, 0f);
                }

                _networkAimYaw.Value = transform.eulerAngles.y;
                _networkAimPitch.Value = _cameraPitch;

                // Initialize tactical HUD for local client
                if (Scorched.UI.ScorchedHUDController.Instance == null && FindAnyObjectByType<Scorched.UI.ScorchedHUDController>() == null)
                {
                    var hudGo = new GameObject("Scorched_HUD");
                    hudGo.AddComponent<Scorched.UI.ScorchedHUDController>();
                }

                Scorched.UI.ScorchedHUDController.Instance?.UpdateHealth(CurrentHealth, _maxHealth);
                Scorched.UI.ScorchedHUDController.Instance?.UpdateStamina(_currentStamina, _maxStamina);
                Scorched.UI.L4DHUDManager.Instance?.UpdateHealth(CurrentHealth, _maxHealth);
                Scorched.UI.L4DHUDManager.Instance?.UpdateStamina(_currentStamina, _maxStamina);
            }
            else
            {
                // Remote proxy: disable local viewport and audio listener
                if (_cameraTransform != null)
                {
                    var cam = _cameraTransform.GetComponent<Camera>();
                    if (cam != null) cam.enabled = false;

                    var listener = _cameraTransform.GetComponent<AudioListener>();
                    if (listener != null) listener.enabled = false;
                }

                // Disable CharacterController on remote proxies to let NetworkTransform interpolate smoothly
                if (_characterController != null)
                {
                    _characterController.enabled = false;
                }

                // Align remote proxy transform immediately to owner aim yaw
                transform.rotation = Quaternion.Euler(0f, _networkAimYaw.Value, 0f);

                // Subscribe to network locomotion updates for remote visual sync
                _networkLocomotionState.OnValueChanged += HandleRemoteLocomotionChanged;
                _networkIsMovingBackward.OnValueChanged += HandleRemoteBackwardChanged;

                // Sync initial remote state
                if (_healthController == null || _healthController.IsAlive)
                {
                    ApplyLocomotionAnimation((LocomotionState)_networkLocomotionState.Value, 0.1f);
                }
                if (_animator != null)
                {
                    _animator.SetFloat(AnimParamSpeedMultiplier, _networkIsMovingBackward.Value ? -1.0f : 1.0f);
                }
            }
        }

        public override void OnNetworkDespawn()
        {
            base.OnNetworkDespawn();

            if (!IsOwner)
            {
                _networkLocomotionState.OnValueChanged -= HandleRemoteLocomotionChanged;
                _networkIsMovingBackward.OnValueChanged -= HandleRemoteBackwardChanged;
            }
        }

        public override void OnDestroy()
        {
            base.OnDestroy();

            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnServerStarted -= OnNetworkStartedCleanupDuplicate;
                NetworkManager.Singleton.OnClientStarted -= OnNetworkStartedCleanupDuplicate;
            }

            if (_headBone != null) _headBone.localScale = Vector3.one;
            if (_neckBone != null) _neckBone.localScale = Vector3.one;
        }

        private void OnNetworkStartedCleanupDuplicate()
        {
            if (this == null || gameObject == null)
            {
                return;
            }

            bool isSceneDuplicate = !IsSpawned || (NetworkObject != null && !NetworkObject.IsPlayerObject);
            if (!isSceneDuplicate)
            {
                return;
            }

            DisableDuplicateScenePlayerImmediately();
            if (!IsSpawned)
            {
                Debug.Log("[PlayerMovement] Disabling duplicate unspawned scene Player upon NetworkManager start.");
                gameObject.SetActive(false);
            }
            else if (IsServer)
            {
                StartCoroutine(DespawnDuplicateScenePlayerCoroutine());
            }
        }

        private void DisableDuplicateScenePlayerImmediately()
        {
            _isDuplicateSceneInstance = true;
            Debug.Log($"[PlayerMovement] Removing duplicate pre-placed scene Player '{gameObject.name}' (IsSpawned={IsSpawned}, IsPlayerObject={NetworkObject?.IsPlayerObject}).");

            if (_characterController != null)
            {
                _characterController.enabled = false;
            }
            if (_healthController != null)
            {
                _healthController.enabled = false;
            }
            if (_networkWeaponController != null)
            {
                _networkWeaponController.enabled = false;
            }
            if (_perspectiveController != null)
            {
                _perspectiveController.enabled = false;
            }

            foreach (var cam in GetComponentsInChildren<Camera>(true))
            {
                cam.enabled = false;
            }
            foreach (var listener in GetComponentsInChildren<AudioListener>(true))
            {
                listener.enabled = false;
            }
            foreach (var col in GetComponentsInChildren<Collider>(true))
            {
                col.enabled = false;
            }
            foreach (var rend in GetComponentsInChildren<Renderer>(true))
            {
                rend.enabled = false;
            }
        }

        private IEnumerator DespawnDuplicateScenePlayerCoroutine()
        {
            yield return null;
            if (this != null && gameObject != null)
            {
                if (NetworkObject != null && NetworkObject.IsSpawned)
                {
                    NetworkObject.Despawn(false);
                }
                gameObject.SetActive(false);
            }
        }

        private void EnsureHeadBone()
        {
            if (_headBone == null || _neckBone == null)
            {
                if (_animator != null && _animator.isHuman)
                {
                    if (_headBone == null) _headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
                    if (_neckBone == null) _neckBone = _animator.GetBoneTransform(HumanBodyBones.Neck);
                }

                if (_headBone == null)
                {
                    _headBone = FindDeepChild(transform, "head.x")
                             ?? FindDeepChild(transform, "head")
                             ?? FindDeepChild(transform, "mixamorig:Head")
                             ?? FindDeepChild(transform, "Head");
                }

                if (_neckBone == null)
                {
                    _neckBone = FindDeepChild(transform, "neck.x")
                             ?? FindDeepChild(transform, "neck")
                             ?? FindDeepChild(transform, "mixamorig:Neck")
                             ?? FindDeepChild(transform, "Neck");
                }
            }
        }

        private void EnsureSpineBones()
        {
            if (_spineBone != null && _chestBone != null)
            {
                return;
            }

            if (_animator != null && _animator.isHuman)
            {
                if (_spineBone == null) _spineBone = _animator.GetBoneTransform(HumanBodyBones.Spine);
                if (_chestBone == null) _chestBone = _animator.GetBoneTransform(HumanBodyBones.Chest);
                if (_upperChestBone == null) _upperChestBone = _animator.GetBoneTransform(HumanBodyBones.UpperChest);
            }

            if (_spineBone == null)
            {
                _spineBone = FindDeepChild(transform, "spine_01.x")
                          ?? FindDeepChild(transform, "spine_01")
                          ?? FindDeepChild(transform, "Spine");
            }
            if (_chestBone == null)
            {
                _chestBone = FindDeepChild(transform, "spine_02.x")
                          ?? FindDeepChild(transform, "spine_02")
                          ?? FindDeepChild(transform, "Chest");
            }
            if (_upperChestBone == null)
            {
                _upperChestBone = FindDeepChild(transform, "spine_03.x")
                               ?? FindDeepChild(transform, "spine_03")
                               ?? FindDeepChild(transform, "UpperChest");
            }
        }

        private void CheckUpperBodyLayer()
        {
            if (_animator != null && _animator.layerCount > 1)
            {
                _hasUpperBodyLayer = true;
                _animator.SetLayerWeight(1, 1.0f);
            }
            else
            {
                _hasUpperBodyLayer = false;
            }

            _hasIsFiringParam = false;
            if (_animator != null)
            {
                var parameters = _animator.parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].nameHash == AnimParamIsFiring)
                    {
                        _hasIsFiringParam = true;
                        break;
                    }
                }
            }
        }

        private void SetupCamera()
        {
            EnsureHeadBone();

            if (_cameraTransform == null)
            {
                // Check if any camera exists in children
                var existingCam = GetComponentInChildren<Camera>();
                if (existingCam != null)
                {
                    _cameraTransform = existingCam.transform;
                }
            }

            // If local owner (or running offline in test scene) and no camera exists, dynamically create a clean FPS eye-level camera
            if (_cameraTransform == null && (!IsSpawned || IsOwner))
            {
                var camGo = new GameObject("PlayerCamera_Auto");
                camGo.transform.SetParent(transform);
                camGo.transform.localPosition = _autoCameraOffset;
                camGo.transform.localRotation = Quaternion.identity;

                var newCam = camGo.AddComponent<Camera>();
                newCam.nearClipPlane = _nearClipPlane;
                camGo.AddComponent<AudioListener>();

                var camData = camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                camData.renderPostProcessing = true;
                camData.volumeLayerMask = ~0;

                _cameraTransform = camGo.transform;
                Debug.Log("[PlayerMovement] Auto-generated FPS camera at eye level.");
            }

            if (_cameraTransform != null)
            {
                var cam = _cameraTransform.GetComponent<Camera>();
                if (cam != null)
                {
                    cam.nearClipPlane = _nearClipPlane;
                    cam.fieldOfView = _fieldOfView;
                    var camData = cam.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>()
                                  ?? cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                    camData.renderPostProcessing = true;
                    camData.volumeLayerMask = ~0;
                }
                UpdateCameraPositionAndRotation();
            }
        }

        private void Update()
        {
            if (_isDuplicateSceneInstance)
            {
                return;
            }

            if (_hasPrevWorldPos && Time.deltaTime > 0.0001f)
            {
                Vector3 instantVel = (transform.position - _prevWorldPosition) / Time.deltaTime;
                instantVel.y = 0f;
                if (_characterController != null && _characterController.enabled && _characterController.velocity.sqrMagnitude > instantVel.sqrMagnitude)
                {
                    instantVel = new Vector3(_characterController.velocity.x, 0f, _characterController.velocity.z);
                }
                _smoothedWorldVelocity = Vector3.Lerp(_smoothedWorldVelocity, instantVel, Mathf.Clamp01(Time.deltaTime * 15f));
            }
            _prevWorldPosition = transform.position;
            _hasPrevWorldPos = true;

            // Remote proxies smoothly interpolate rotation to match the replicated aim yaw in real-time
            if (IsSpawned && !IsOwner)
            {
                if (_healthController == null || !_healthController.IsDead)
                {
                    float targetYaw = _networkAimYaw.Value;
                    float currentYaw = transform.eulerAngles.y;
                    float smoothYaw = Mathf.LerpAngle(currentYaw, targetYaw, Time.deltaTime * 30f);
                    transform.rotation = Quaternion.Euler(0f, smoothYaw, 0f);
                }

                if (_healthController != null && !_healthController.IsAlive)
                {
                    UpdateAnimationState();
                }
                return;
            }

            // Suppress movement, look, and attack while any tactical menu or pause overlay is open
            if (Scorched.UI.MainMenuUIManager.IsMenuOpen)
            {
                return;
            }

            // Lock character into selected preview pose when weapon scrubber / preview mode is enabled
            if (_weaponHandler != null && _weaponHandler.EnablePreviewMode)
            {
                return;
            }

            HandleCursorToggle();
            HandleLookInput();
            HandleMovementInput();
            HandleCombatInput();
            UpdateAnimationState();
        }

        private void LateUpdate()
        {
            if (_isDuplicateSceneInstance)
            {
                return;
            }

            // Apply procedural torso aim pitch for BOTH local owner and remote proxies (Alive state only)
            ApplyProceduralSpineAim();

            // On remote proxies, ensure body yaw matches aim yaw cleanly after animations evaluate
            if (IsSpawned && !IsOwner)
            {
                if (_healthController == null || !_healthController.IsDead)
                {
                    transform.rotation = Quaternion.Euler(0f, _networkAimYaw.Value, 0f);
                }
                return;
            }

            UpdateCameraPositionAndRotation();

            // Ensure local owner's yaw is kept in sync
            if (IsSpawned && IsOwner && (_healthController == null || !_healthController.IsDead))
            {
                if (Mathf.Abs(_networkAimYaw.Value - transform.eulerAngles.y) > 0.1f)
                {
                    _networkAimYaw.Value = transform.eulerAngles.y;
                }
            }

            // Solve Left Hand IK AFTER spine pitch, weapon socket, and camera have evaluated
            var perspectiveCtrl = GetComponent<FPSPerspectiveController>();
            if (perspectiveCtrl != null)
            {
                perspectiveCtrl.PostLateUpdateIK();
            }
        }

        /// <summary>
        /// Procedurally pitches the spine, chest, and upper chest bones to align the torso,
        /// arms, and gun directly with vertical aim (looking up into the sky or down at feet).
        /// Replicates across network so all players see correct aiming direction.
        /// </summary>
        private void ApplyProceduralSpineAim()
        {
            if (_healthController != null && !_healthController.IsAlive)
            {
                return;
            }

            EnsureSpineBones();
            if (_spineBone == null && _chestBone == null && _upperChestBone == null)
            {
                return;
            }

            float targetPitch;
            if (!IsSpawned || IsOwner)
            {
                targetPitch = _cameraPitch;
                if (IsSpawned && Mathf.Abs(_networkAimPitch.Value - _cameraPitch) > 0.25f)
                {
                    _networkAimPitch.Value = _cameraPitch;
                }
            }
            else
            {
                targetPitch = _networkAimPitch.Value;
            }

            float dt = Time.deltaTime;
            _smoothedAimPitch = dt > 0f ? Mathf.Lerp(_smoothedAimPitch, targetPitch, dt * _spinePitchSmoothSpeed) : targetPitch;

            // In Unity, rotating around character's horizontal right axis (transform.right) in world space:
            // Upper chest is child of chest, which is child of spine.
            // Distribute pitch smoothly across the spine curve:
            // 25% lower spine, 35% chest, 40% upper chest -> Total 100% on shoulders, arms, weapon & head.
            if (_upperChestBone != null && _chestBone != null && _spineBone != null)
            {
                _spineBone.rotation = Quaternion.AngleAxis(_smoothedAimPitch * 0.25f, transform.right) * _spineBone.rotation;
                _chestBone.rotation = Quaternion.AngleAxis(_smoothedAimPitch * 0.35f, transform.right) * _chestBone.rotation;
                _upperChestBone.rotation = Quaternion.AngleAxis(_smoothedAimPitch * 0.40f, transform.right) * _upperChestBone.rotation;
            }
            else if (_chestBone != null && _spineBone != null)
            {
                _spineBone.rotation = Quaternion.AngleAxis(_smoothedAimPitch * 0.40f, transform.right) * _spineBone.rotation;
                _chestBone.rotation = Quaternion.AngleAxis(_smoothedAimPitch * 0.60f, transform.right) * _chestBone.rotation;
            }
            else if (_chestBone != null)
            {
                _chestBone.rotation = Quaternion.AngleAxis(_smoothedAimPitch, transform.right) * _chestBone.rotation;
            }
            else if (_spineBone != null)
            {
                _spineBone.rotation = Quaternion.AngleAxis(_smoothedAimPitch, transform.right) * _spineBone.rotation;
            }
        }

        public void UpdateCameraPositionAndRotation()
        {
            if (_cameraTransform == null)
            {
                return;
            }

            EnsureHeadBone();

            var cam = _cameraTransform.GetComponent<Camera>();
            if (cam != null)
            {
                if (!Mathf.Approximately(cam.fieldOfView, _fieldOfView))
                {
                    cam.fieldOfView = _fieldOfView;
                }
                // Enforce tight near clip plane so nearby gun receivers/sights never clip through the lens
                float targetNearClip = Mathf.Min(_nearClipPlane, 0.015f);
                if (!Mathf.Approximately(cam.nearClipPlane, targetNearClip))
                {
                    cam.nearClipPlane = targetNearClip;
                }
            }

            // Head bone scaling:
            // For the local owner while Alive, scale ONLY the head bone to zero so the skull/face never clips,
            // while neck bone scale is strictly kept at (1,1,1) so collar/jacket vertices NEVER stretch or explode!
            // When Downed or Dead, restore head scale so the full body renders cleanly in third-person crawl/death camera.
            bool isLocalOwner = !IsSpawned || IsOwner;
            bool isAlive = _healthController == null || _healthController.IsAlive;
            if (isLocalOwner && isAlive)
            {
                if (_headBone != null && _headBone.localScale != Vector3.zero)
                {
                    _headBone.localScale = Vector3.zero;
                }
                if (_neckBone != null && _neckBone.localScale != Vector3.one)
                {
                    _neckBone.localScale = Vector3.one;
                }
            }
            else
            {
                if (_headBone != null && _headBone.localScale != Vector3.one)
                {
                    _headBone.localScale = Vector3.one;
                }
                if (_neckBone != null && _neckBone.localScale != Vector3.one)
                {
                    _neckBone.localScale = Vector3.one;
                }
            }

            // 1. Calculate 3D view orientation matching vertical aim pitch
            Quaternion viewRot = transform.rotation * Quaternion.Euler(_cameraPitch, 0f, 0f);
            Vector3 viewForward = viewRot * Vector3.forward;
            Vector3 viewUp = viewRot * Vector3.up;
            Vector3 viewRight = viewRot * Vector3.right;

            // 2. Calculate the ideal eye position (or 3rd-person orbit camera while Downed / Dead)
            Vector3 targetEyePos;
            if (!isAlive)
            {
                bool isDead = _healthController != null && _healthController.IsDead;
                float pivotHeight = isDead ? 0.55f : 0.70f;
                float camBackDist = isDead ? 2.40f : 1.75f;
                float camUpLift = isDead ? 0.75f : 0.45f;
                Vector3 pivot = transform.position + Vector3.up * pivotHeight;
                Vector3 desiredOffset = (-viewForward * camBackDist) + (Vector3.up * camUpLift);
                float desiredDist = desiredOffset.magnitude;
                Vector3 rayDir = desiredDist > 0.001f ? desiredOffset / desiredDist : -transform.forward;

                if (Physics.Raycast(pivot, rayDir, out RaycastHit camHit, desiredDist, ~0, QueryTriggerInteraction.Ignore) &&
                    camHit.collider != null && camHit.collider.transform.root != transform)
                {
                    targetEyePos = pivot + rayDir * Mathf.Max(0.35f, camHit.distance - 0.12f);
                }
                else
                {
                    targetEyePos = pivot + desiredOffset;
                }
            }
            else
            {
                Transform anchorBone = _neckBone != null ? _neckBone : _headBone;
                if (anchorBone != null)
                {
                    // Dynamic forward clearance based on animation state to compensate for sprint lean and recoil
                    float dynamicClearance = 0f;
                    if (_attackTimer > 0f)
                    {
                        dynamicClearance = 0.02f; // Rifle recoil clearance
                    }
                    else if (_currentLocomotion == LocomotionState.Running)
                    {
                        dynamicClearance = 0.04f; // Sprint lean clearance
                    }
                    else if (_currentLocomotion == LocomotionState.Jog)
                    {
                        dynamicClearance = 0.02f; // Jog lean clearance
                    }

                    // High-angle aiming adjustments:
                    // When aiming UP into the sky (_cameraPitch < 0), the neck tilts back and the weapon rises.
                    // Shift camera along viewUp and maintain eye relief distance so the camera is never inside the gun receiver.
                    float lookUpFactor = Mathf.Clamp01(-_cameraPitch / 80f);
                    float lookDownFactor = Mathf.Clamp01(_cameraPitch / 80f);

                    float forwardDist = _eyeOffsetForward + dynamicClearance + (lookDownFactor * 0.04f) - (lookUpFactor * 0.045f);
                    float upDist = _eyeOffsetUp + (lookUpFactor * 0.065f);

                    targetEyePos = anchorBone.position
                                 + viewForward * forwardDist
                                 + viewUp * upDist
                                 + viewRight * _eyeOffsetRight
                                 + _proceduralCameraOffset;
                }
                else
                {
                    // Fallback if no anchor bone: use crouch/standing height
                    float currentEyeHeight = _isCrouching ? _crouchHeight * 0.85f : _standingHeight * 0.9f;
                    targetEyePos = transform.position + Vector3.up * currentEyeHeight + viewForward * _eyeOffsetForward + _proceduralCameraOffset;
                }
            }

            // 3. Position tracking: 1:1 Instant lock (0 lag) eliminates camera trailing inside the skull
            if (_cameraFollowSpeed > 0f && Time.deltaTime > 0f && Application.isPlaying)
            {
                _cameraTransform.position = Vector3.Lerp(_cameraTransform.position, targetEyePos, Time.deltaTime * _cameraFollowSpeed);
            }
            else
            {
                _cameraTransform.position = targetEyePos;
            }

            // 4. Rotation: Pitch tilts vertically, Yaw matches body facing + procedural sway/kick
            _cameraTransform.localRotation = Quaternion.Euler(_cameraPitch, 0f, 0f) * Quaternion.Euler(_proceduralCameraRotationOffset);
        }
        #endregion

        #region Input & Movement Handling
        private void HandleCursorToggle()
        {
            if (Scorched.UI.MainMenuUIManager.IsMenuOpen)
            {
                return;
            }

            if (Scorched.Inventory.ScorchedInputGates.IsInventoryOpen ||
                Scorched.Inventory.ScorchedInputGates.LastUiClosedFrame == Time.frameCount)
            {
                if (Scorched.Inventory.ScorchedInputGates.IsInventoryOpen && (Cursor.lockState != CursorLockMode.None || !Cursor.visible))
                {
                    UnlockCursor();
                }
                return;
            }

            var keyboard = Keyboard.current ?? InputSystem.GetDevice<Keyboard>();
            var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                if (Scorched.UI.MainMenuUIManager.Instance != null)
                {
                    Scorched.UI.MainMenuUIManager.Instance.ShowPauseMenu();
                }
                else
                {
                    UnlockCursor();
                }
            }
            else if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame) && Cursor.lockState != CursorLockMode.Locked)
            {
                LockCursor();
            }
        }

        private void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void UnlockCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void HandleLookInput()
        {
            if (Scorched.Inventory.ScorchedInputGates.IsInventoryOpen)
            {
                return;
            }
            if (_healthController != null && _healthController.IsDead)
            {
                return;
            }

            var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();
            if (mouse == null)
            {
                return;
            }

            // Click to recapture mouse lock if lost
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                if (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)
                {
                    LockCursor();
                }
                return;
            }

            Vector2 mouseDelta = mouse.delta.ReadValue();
            float mouseX = mouseDelta.x * _mouseSensitivityX * 0.1f;
            float mouseY = mouseDelta.y * _mouseSensitivityY * 0.1f;

            // Horizontal yaw turns the entire character body
            transform.Rotate(Vector3.up * mouseX);

            // Vertical pitch tilts only the camera
            _cameraPitch -= mouseY;
            _cameraPitch = Mathf.Clamp(_cameraPitch, _minPitchAngle, _maxPitchAngle);

            if (_cameraTransform != null)
            {
                _cameraTransform.localRotation = Quaternion.Euler(_cameraPitch, 0f, 0f);
            }

            if (IsSpawned)
            {
                _networkAimYaw.Value = transform.eulerAngles.y;
                _networkAimPitch.Value = _cameraPitch;
            }
        }

        private void HandleMovementInput()
        {
            if (_characterController == null)
            {
                return;
            }

            _isGrounded = _characterController.isGrounded;
            if (_isGrounded && _velocity.y < 0)
            {
                _velocity.y = _groundStickForce;

                // Landed from air: clear airborne states and momentum
                if (_isAirborne)
                {
                    _isAirborne = false;
                    _isSprintJumpActive = false;
                    _airborneVelocity = Vector3.zero;
                }
            }

            PlayerLifeState lifeState = _healthController != null ? _healthController.LifeState : PlayerLifeState.Alive;
            if (lifeState == PlayerLifeState.Dead)
            {
                _isCrouching = true;
                AdjustColliderForCrouch(true);
                _velocity.y += _gravity * Time.deltaTime;
                if (_characterController.enabled)
                {
                    _characterController.Move(_velocity * Time.deltaTime);
                }
                _healthController?.SetDownedMovingState(false);
                return;
            }

            bool isDowned = lifeState == PlayerLifeState.Downed;

            // Direct hardcoded key polling (WASD, Arrows, Shift, Ctrl, Space)
            var keyboard = Keyboard.current ?? InputSystem.GetDevice<Keyboard>();
            float horizontal = 0f;
            float vertical = 0f;
            bool isSprintHeld = false;
            bool isCrouchHeld = isDowned;
            bool isJumpPressed = false;

            if (keyboard != null)
            {
                // W or Up Arrow -> Move Forward
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                {
                    vertical += 1f;
                }

                // S or Down Arrow -> Move Backward
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                {
                    vertical -= 1f;
                }

                // D or Right Arrow -> Strafe Right
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                {
                    horizontal += 1f;
                }

                // A or Left Arrow -> Strafe Left
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                {
                    horizontal -= 1f;
                }

                if (!isDowned)
                {
                    // Shift -> Sprint
                    isSprintHeld = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;

                    // Ctrl or C -> Crouch
                    isCrouchHeld = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed || keyboard.cKey.isPressed;

                    // Space -> Jump
                    isJumpPressed = keyboard.spaceKey.wasPressedThisFrame;
                }
            }

            _isCrouching = isCrouchHeld;
            AdjustColliderForCrouch(_isCrouching);

            Vector2 moveInput = new Vector2(horizontal, vertical);
            bool isMoving = moveInput.sqrMagnitude > 0.01f;

            if (isDowned)
            {
                _healthController?.SetDownedMovingState(isMoving);
            }
            else
            {
                _healthController?.SetDownedMovingState(false);
            }

            // Permanent Stamina (Hunger): sprints & jumps drain Stamina permanently until Food is eaten
            bool uiBlocked = Scorched.Inventory.ScorchedInputGates.IsInventoryOpen;
            if (uiBlocked)
            {
                isSprintHeld = false;
                isJumpPressed = false;
            }

            bool wantsSprint = !isDowned && isSprintHeld && isMoving && vertical > 0.1f && !_isCrouching;
            bool canSprint = wantsSprint && _currentStamina > 0.1f;

            if (canSprint)
            {
                _currentStamina = Mathf.Max(0f, _currentStamina - (_staminaDrainRate * Time.deltaTime));
            }
            else if (!_disableAutoStaminaRegen && _staminaRegenRate > 0f)
            {
                float thermalRegenMul = _thermalController != null ? _thermalController.StaminaRegenMultiplier : 1f;
                _currentStamina = Mathf.Min(_maxStamina, _currentStamina + (_staminaRegenRate * thermalRegenMul * Time.deltaTime));
            }

            if (IsOwner)
            {
                Scorched.UI.ScorchedHUDController.Instance?.UpdateStamina(_currentStamina, _maxStamina);
                Scorched.UI.L4DHUDManager.Instance?.UpdateStamina(_currentStamina, _maxStamina);
            }

            // Determine active target speed (Downed crawls at 25% of walk/jog speed)
            float currentSpeed;
            if (isDowned)
            {
                float downedMult = _healthController != null ? _healthController.DownedSpeedMultiplier : 0.25f;
                currentSpeed = _jogSpeed * downedMult;
            }
            else if (_isCrouching)
            {
                currentSpeed = _crouchSpeed;
            }
            else if (canSprint)
            {
                // Sprinting forward
                currentSpeed = _runSpeed;
            }
            else
            {
                // Default locomotion is Jogging
                currentSpeed = _jogSpeed;
            }

            // Strafe / backward speed penalty
            if (!isDowned && (vertical < -0.1f || (Mathf.Abs(horizontal) > 0.1f && Mathf.Abs(vertical) < 0.1f)))
            {
                currentSpeed *= _strafeMultiplier;
            }

            // Backward locomotion animation handling (S key plays Jog/Run/Down Walking in reverse)
            bool isMovingBackward = isMoving && vertical < -0.1f;
            float animSpeedMultiplier = isMovingBackward ? -1.0f : 1.0f;

            if (_animator != null && !_isAirborne)
            {
                _animator.SetFloat(AnimParamSpeedMultiplier, animSpeedMultiplier);
            }

            if (IsSpawned && _networkIsMovingBackward.Value != isMovingBackward)
            {
                _networkIsMovingBackward.Value = isMovingBackward;
            }

            // Calculate horizontal motion in world space
            Vector3 moveDirection = (transform.right * moveInput.x + transform.forward * moveInput.y).normalized;
            Vector3 horizontalMove;

            if (_isAirborne && !isDowned)
            {
                // Mid-air momentum: preserve jump momentum with slight air steering
                if (isMoving && moveDirection.sqrMagnitude > 0.001f)
                {
                    _airborneVelocity = Vector3.Lerp(_airborneVelocity, moveDirection * currentSpeed, Time.deltaTime * 3f);
                }
                horizontalMove = _airborneVelocity * Time.deltaTime;
            }
            else
            {
                horizontalMove = moveDirection * (currentSpeed * Time.deltaTime);
            }

            // Jump execution (locked out while Downed, Crouched, or exhausted with 0 Stamina)
            if (!isDowned && isJumpPressed && _isGrounded && !_isCrouching && _currentStamina > 0.5f)
            {
                _currentStamina = Mathf.Max(0f, _currentStamina - _jumpStaminaCost);
                if (IsOwner)
                {
                    Scorched.UI.ScorchedHUDController.Instance?.UpdateStamina(_currentStamina, _maxStamina);
                    Scorched.UI.L4DHUDManager.Instance?.UpdateStamina(_currentStamina, _maxStamina);
                }
                ExecuteJump(isMoving, moveDirection, currentSpeed);
            }

            // Apply gravity
            _velocity.y += _gravity * Time.deltaTime;

            // Execute CharacterController movement
            Vector3 finalMotion = horizontalMove + (_velocity * Time.deltaTime);
            if (_characterController.enabled)
            {
                _characterController.Move(finalMotion);
            }

            // Resolve locomotion state (only while Alive)
            if (!isDowned)
            {
                ResolveLocomotionState(isMoving, isSprintHeld && vertical > 0.1f);
            }
        }

        private void ExecuteJump(bool isMoving, Vector3 jumpDirection, float horizontalSpeed)
        {
            _velocity.y = Mathf.Sqrt(2f * _jumpHeight * Mathf.Abs(_gravity));
            _isAirborne = true;
            _isSprintJumpActive = isMoving;

            if (isMoving && jumpDirection.sqrMagnitude > 0.001f)
            {
                // Store horizontal jump momentum
                _airborneVelocity = jumpDirection * horizontalSpeed;

                // Adapt character orientation to face into the WASD movement direction
                transform.rotation = Quaternion.LookRotation(jumpDirection, Vector3.up);

                // Reset animation speed multiplier to 1 so the sprint jump plays forward along the leap
                if (_animator != null)
                {
                    _animator.SetFloat(AnimParamSpeedMultiplier, 1.0f);
                }

                // Jumping while jogging or running
                PlayAnimationDirect(AnimStateSprintJump, _jumpTransitionDuration);
                if (IsSpawned)
                {
                    PlayActionAnimationRpc(ActionAnimation.SprintJump);
                }
            }
            else
            {
                _airborneVelocity = Vector3.zero;

                // Stationary vertical jump
                PlayAnimationDirect(AnimStateJumping, _jumpTransitionDuration);
                if (IsSpawned)
                {
                    PlayActionAnimationRpc(ActionAnimation.Jumping);
                }
            }
        }

        private void AdjustColliderForCrouch(bool crouching)
        {
            if (_characterController == null)
            {
                return;
            }

            float targetHeight = crouching ? _crouchHeight : _standingHeight;
            Vector3 targetCenter = crouching ? _crouchCenter : _standingCenter;

            _characterController.height = Mathf.Lerp(_characterController.height, targetHeight, Time.deltaTime * 12f);
            _characterController.center = Vector3.Lerp(_characterController.center, targetCenter, Time.deltaTime * 12f);
        }

        private void HandleCombatInput()
        {
            if (Scorched.Inventory.ScorchedInputGates.IsInventoryOpen)
            {
                return;
            }
            if (_healthController != null && !_healthController.CanPerformCombatActions)
            {
                _attackTimer = 0f;
                if (_hasIsFiringParam && _animator != null)
                {
                    _animator.SetBool(AnimParamIsFiring, false);
                }
                if (_hasUpperBodyLayer && _animator != null && _animator.GetLayerWeight(1) > 0f)
                {
                    _animator.SetLayerWeight(1, 0f);
                }
                return;
            }

            var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();
            if (mouse == null || Cursor.lockState != CursorLockMode.Locked)
            {
                return;
            }

            // Lock out firing if match is in pre-combat lockout (e.g. waiting for equal teams)
            if (Scorched.Combat.NetworkMatchManager.Instance != null && !Scorched.Combat.NetworkMatchManager.Instance.IsCombatActive)
            {
                return;
            }

            bool wantsToFire = mouse.leftButton.isPressed;

            if (_networkWeaponController != null)
            {
                if (_networkWeaponController.TryFire(wantsToFire))
                {
                    _lastFireTime = Time.time;
                    _attackTimer = _fireAnimationDuration;

                    // Play firing recoil animation (upper body layer leaves legs running/jogging/crouching)
                    TriggerFireAnimation();
                    if (IsSpawned)
                    {
                        PlayActionAnimationRpc(ActionAnimation.FiringRifle);
                    }

                    // Pulse crosshair in HUD
                    Scorched.UI.ScorchedHUDController.Instance?.OnWeaponFired();
            Scorched.UI.L4DHUDManager.Instance?.OnWeaponFired();
                }
            }
            else
            {
                // Fallback if NetworkWeaponController is not yet attached
                if (wantsToFire && Time.time >= _lastFireTime + _fireRate)
                {
                    _lastFireTime = Time.time;
                    _attackTimer = _fireAnimationDuration;

                    // Play firing recoil animation
                    TriggerFireAnimation();
                    if (IsSpawned)
                    {
                        PlayActionAnimationRpc(ActionAnimation.FiringRifle);
                    }

                    // Raycast forward from camera center (crosshair aim target)
                    Vector3 targetPoint;
                    Transform camTrans = _cameraTransform != null ? _cameraTransform : transform;
                    Ray aimRay = new Ray(camTrans.position, camTrans.forward);
                    if (Physics.Raycast(aimRay, out RaycastHit hit, 500f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        targetPoint = hit.point;
                    }
                    else
                    {
                        targetPoint = aimRay.origin + aimRay.direction * 500f;
                    }

                    // Spawn ballistic bullet projectile towards crosshair target
                    if (_weaponHandler != null)
                    {
                        var persp = EnsurePerspectiveController();
                        persp?.PrepareViewmodelForFiring();
                        persp?.ApplyViewmodelRecoilKick();
                        var proj = _weaponHandler.SpawnBullet(targetPoint, _characterController);
                        if (Scorched.Weapons.BulletVisualPoolManager.Instance != null)
                        {
                            Transform muzTrans = persp?.ActiveMuzzlePoint ?? _weaponHandler.GetOrCreateMuzzlePoint();
                            Vector3 muzPos = muzTrans != null ? muzTrans.position : _weaponHandler.GetMuzzlePosition();
                            Scorched.Weapons.BulletVisualPoolManager.Instance.SpawnMuzzleFlash(muzPos, (targetPoint - muzPos).normalized, muzTrans);
                        }
                    }

                    // Pulse crosshair in HUD
                    Scorched.UI.ScorchedHUDController.Instance?.OnWeaponFired();
            Scorched.UI.L4DHUDManager.Instance?.OnWeaponFired();
                }
            }

            if (_attackTimer > 0f)
            {
                _attackTimer -= Time.deltaTime;
                if (_attackTimer <= 0f && _animator != null)
                {
                    if (_hasIsFiringParam)
                    {
                        _animator.SetBool(AnimParamIsFiring, false);
                    }
                    bool keepCombatUpperBody = EnsurePerspectiveController() != null &&
                                              _perspectiveController.CurrentMode == FPSPerspectiveMode.CombatReady_UpperBody;
                    if (_hasUpperBodyLayer && !keepCombatUpperBody)
                    {
                        _animator.CrossFadeInFixedTime(AnimStateEmpty, 0.12f, 1);
                    }
                }
            }
        }
        #endregion

        #region Animation State Management
        private void TriggerFireAnimation()
        {
            CheckUpperBodyLayer();
            if (_animator == null) return;

            // Signal animator parameter for Upper Body layer transitions
            if (_hasIsFiringParam)
            {
                _animator.SetBool(AnimParamIsFiring, true);
            }

            if (_hasUpperBodyLayer)
            {
                // Play firing recoil on Layer 1 (Upper Body) so legs continue running/jogging/crouching uninterrupted
                _animator.CrossFadeInFixedTime(AnimStateFiringRifle, _fireTransitionDuration, 1);
            }
            else
            {
                // Fallback for single-layer animators: only play full-body firing if stationary
                if (_currentLocomotion == LocomotionState.Idle || _currentLocomotion == LocomotionState.CrouchIdle)
                {
                    PlayAnimationDirect(AnimStateFiringRifle, _fireTransitionDuration);
                }
            }
        }

        private void ResolveLocomotionState(bool isMoving, bool isSprinting)
        {
            if (_isCrouching)
            {
                _currentLocomotion = isMoving ? LocomotionState.CrouchedRun : LocomotionState.CrouchIdle;
            }
            else
            {
                if (isMoving)
                {
                    _currentLocomotion = isSprinting ? LocomotionState.Running : LocomotionState.Jog;
                }
                else
                {
                    _currentLocomotion = LocomotionState.Idle;
                }
            }

            // Replicate state to remote clients via NetworkVariable (only when spawned)
            if (IsSpawned && _networkLocomotionState.Value != (byte)_currentLocomotion)
            {
                _networkLocomotionState.Value = (byte)_currentLocomotion;
            }
        }

        private void UpdateAnimationState()
        {
            // Phase 5 Direct State Guard: DBNO & Death states strictly override locomotion
            if (_healthController != null)
            {
                if (_healthController.IsDead)
                {
                    PlayAnimationDirect(AnimStateDeath, 0.15f);
                    return;
                }

                if (_healthController.IsDowned)
                {
                    int downStateHash = _healthController.DownedMoving ? AnimStateDownWalking : AnimStateDownIdle;
                    PlayAnimationDirect(downStateHash, 0.15f);
                    return;
                }
            }

            // If airborne, maintain the appropriate jump state until landing
            if (_isAirborne)
            {
                int airborneHash = _isSprintJumpActive ? AnimStateSprintJump : AnimStateJumping;
                PlayAnimationDirect(airborneHash, _jumpTransitionDuration);
            }
            else
            {
                // Grounded locomotion: Always drive Layer 0 so legs continuously run, jog, crouch, or crouch-run
                ApplyLocomotionAnimation(_currentLocomotion, _locomotionTransitionDuration);
            }

            // Fallback for single-layer animators: keep stationary firing pose in focus
            if (!_hasUpperBodyLayer && _attackTimer > 0f && (_currentLocomotion == LocomotionState.Idle || _currentLocomotion == LocomotionState.CrouchIdle))
            {
                PlayAnimationDirect(AnimStateFiringRifle, _fireTransitionDuration);
            }
        }

        public void NotifyLifeStateChanged(PlayerLifeState newState)
        {
            if (_animator == null)
            {
                _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
            }

            if (newState == PlayerLifeState.Dead)
            {
                _isAirborne = false;
                _isSprintJumpActive = false;
                _attackTimer = 0f;
                if (_hasUpperBodyLayer && _animator != null)
                {
                    _animator.SetLayerWeight(1, 0f);
                }
                PlayAnimationDirect(AnimStateDeath, 0.15f);
            }
            else if (newState == PlayerLifeState.Downed)
            {
                _isAirborne = false;
                _isSprintJumpActive = false;
                _attackTimer = 0f;
                _isCrouching = true;
                AdjustColliderForCrouch(true);
                if (_hasUpperBodyLayer && _animator != null)
                {
                    _animator.SetLayerWeight(1, 0f);
                }
                bool moving = _healthController != null && _healthController.DownedMoving;
                PlayAnimationDirect(moving ? AnimStateDownWalking : AnimStateDownIdle, 0.15f);
            }
            else
            {
                _isAirborne = false;
                _isSprintJumpActive = false;
                _attackTimer = 0f;
                _isCrouching = false;
                AdjustColliderForCrouch(false);
                _currentLocomotion = LocomotionState.Idle;
                _currentAnimationHash = 0;
                PlayAnimationDirect(AnimStateIdle, 0.15f);
            }
        }

        public void OnHealthControllerRespawned()
        {
            _velocity = Vector3.zero;
            _airborneVelocity = Vector3.zero;
            _isAirborne = false;
            _isSprintJumpActive = false;
            _isCrouching = false;
            _attackTimer = 0f;
            _currentHealth = _maxHealth;
            _currentStamina = _maxStamina;
            AdjustColliderForCrouch(false);
            _currentLocomotion = LocomotionState.Idle;
            _currentAnimationHash = 0;
            PlayAnimationDirect(AnimStateIdle, 0f);

            if (!IsSpawned || IsOwner)
            {
                Scorched.UI.ScorchedHUDController.Instance?.UpdateStamina(_currentStamina, _maxStamina);
                Scorched.UI.L4DHUDManager.Instance?.UpdateStamina(_currentStamina, _maxStamina);
            }
        }

        private void ApplyLocomotionAnimation(LocomotionState state, float transitionTime)
        {
            int targetHash = state switch
            {
                LocomotionState.Jog => AnimStateJog,
                LocomotionState.Running => AnimStateRunning,
                LocomotionState.CrouchIdle => AnimStateCrouchIdle,
                LocomotionState.CrouchedRun => AnimStateCrouchedRun,
                _ => AnimStateIdle
            };

            PlayAnimationDirect(targetHash, transitionTime);
        }

        /// <summary>
        /// Plays an animation state directly by hash using CrossFadeInFixedTime.
        /// Prevents redundant cross-fading when the state is already playing.
        /// </summary>
        private void PlayAnimationDirect(int stateHash, float transitionDuration)
        {
            if (_animator == null)
            {
                return;
            }

            if (_currentAnimationHash == stateHash)
            {
                return;
            }

            _currentAnimationHash = stateHash;

            if (transitionDuration <= 0f)
            {
                _animator.Play(stateHash, 0, 0f);
            }
            else
            {
                _animator.CrossFadeInFixedTime(stateHash, transitionDuration);
            }
        }
        #endregion

        #region Network RPCs (Remote Player Animation Sync)
        /// <summary>
        /// Invoked on remote clients when the owner's continuous locomotion state updates.
        /// </summary>
        private void HandleRemoteLocomotionChanged(byte previousValue, byte newValue)
        {
            if (IsOwner)
            {
                return;
            }

            _currentLocomotion = (LocomotionState)newValue;
            _isCrouching = _currentLocomotion == LocomotionState.CrouchIdle || _currentLocomotion == LocomotionState.CrouchedRun;

            if (_healthController != null && !_healthController.IsAlive)
            {
                return;
            }

            // Always update remote locomotion so legs never freeze during firing
            if (!_isAirborne)
            {
                ApplyLocomotionAnimation((LocomotionState)newValue, _locomotionTransitionDuration);
            }
        }

        /// <summary>
        /// Invoked on remote clients when the owner's backward movement state changes.
        /// </summary>
        private void HandleRemoteBackwardChanged(bool previousValue, bool isBackward)
        {
            if (IsOwner || _animator == null)
            {
                return;
            }

            _animator.SetFloat(AnimParamSpeedMultiplier, isBackward ? -1.0f : 1.0f);
        }

        public void RestoreStaminaAuthoritative(float amount)
        {
            if (amount <= 0f) return;

            if (!IsSpawned || IsOwner)
            {
                _currentStamina = Mathf.Min(_maxStamina, _currentStamina + amount);
                Scorched.UI.ScorchedHUDController.Instance?.UpdateStamina(_currentStamina, _maxStamina);
                Scorched.UI.L4DHUDManager.Instance?.UpdateStamina(_currentStamina, _maxStamina);
            }
            else if (IsServer)
            {
                RestoreStaminaOwnerRpc(amount);
            }
        }

        [Rpc(SendTo.Owner)]
        private void RestoreStaminaOwnerRpc(float amount)
        {
            _currentStamina = Mathf.Min(_maxStamina, _currentStamina + Mathf.Max(0f, amount));
            Scorched.UI.ScorchedHUDController.Instance?.UpdateStamina(_currentStamina, _maxStamina);
            Scorched.UI.L4DHUDManager.Instance?.UpdateStamina(_currentStamina, _maxStamina);
        }

        /// <summary>
        /// Broadcasts one-shot action animations (Jumping, Sprint Jump, Firing Rifle)
        /// from the owning client to all remote observers.
        /// </summary>
        [Rpc(SendTo.NotOwner)]
        private void PlayActionAnimationRpc(ActionAnimation action)
        {
            if (_healthController != null && !_healthController.IsAlive)
            {
                return;
            }

            switch (action)
            {
                case ActionAnimation.Jumping:
                    _isAirborne = true;
                    _isSprintJumpActive = false;
                    PlayAnimationDirect(AnimStateJumping, _jumpTransitionDuration);
                    break;

                case ActionAnimation.SprintJump:
                    _isAirborne = true;
                    _isSprintJumpActive = true;
                    PlayAnimationDirect(AnimStateSprintJump, _jumpTransitionDuration);
                    break;

                case ActionAnimation.FiringRifle:
                    _attackTimer = _fireAnimationDuration;
                    TriggerFireAnimation();
                    break;
            }
        }
        #endregion

        #region IDamageable Implementation
        /// <summary>
        /// Receives authoritative combat damage and forwards it directly to PlayerHealthController
        /// for body-part scaling, DBNO transitions, and delayed respawn.
        /// </summary>
        public void ApplyCombatHit(float healthDamage, float hydrationDamage, ulong attackerClientId, Vector3 hitPoint)
        {
            if (_healthController == null)
            {
                _healthController = GetComponent<PlayerHealthController>();
            }

            if (_healthController != null)
            {
                _healthController.ApplyCombatHit(healthDamage, hydrationDamage, attackerClientId, hitPoint);
            }
        }
        #endregion

        #region Helpers
        private static Transform FindDeepChild(Transform parent, string childName)
        {
            foreach (Transform child in parent)
            {
                if (string.Equals(child.name, childName, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                var result = FindDeepChild(child, childName);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
        #endregion
    }
}
