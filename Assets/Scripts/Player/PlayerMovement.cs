using System;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.InputSystem;

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
    [RequireComponent(typeof(PlayerWeaponHandler))]
    public class PlayerMovement : NetworkBehaviour
    {
        #region Animation Hashes (Direct State Playback)
        private static readonly int AnimStateIdle = Animator.StringToHash("Idle");
        private static readonly int AnimStateJog = Animator.StringToHash("Jog");
        private static readonly int AnimStateRunning = Animator.StringToHash("Running");
        private static readonly int AnimStateJumping = Animator.StringToHash("Jumping");
        private static readonly int AnimStateSprintJump = Animator.StringToHash("Sprint Jump");
        private static readonly int AnimStateCrouchIdle = Animator.StringToHash("Crouch Idle");
        private static readonly int AnimStateCrouchedRun = Animator.StringToHash("Crouched Run");
        private static readonly int AnimStateFiringRifle = Animator.StringToHash("Firing Rifle");
        private static readonly int AnimParamSpeedMultiplier = Animator.StringToHash("MoveSpeedMultiplier");
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
        [Tooltip("Field of view for first-person perspective (75-80 degrees for tactical FPS weapon framing).")]
        [SerializeField] private float _fieldOfView = 75.0f;

        [Tooltip("The character's head bone (head.x). Auto-detected if unassigned.")]
        [SerializeField] private Transform _headBone;

        [Tooltip("Forward offset from the head bone pivot to place the camera right at the eyes/goggles (outside the skull).")]
        [SerializeField] private float _eyeOffsetForward = 0.11f;

        [Tooltip("Upward offset from the head bone pivot to the eye line.")]
        [SerializeField] private float _eyeOffsetUp = 0.04f;

        [Tooltip("Horizontal / side offset (0 = centered).")]
        [SerializeField] private float _eyeOffsetRight = 0.0f;

        [Tooltip("Camera tracking smoothness (higher = snappier, 0 = instant 1:1 lock).")]
        [SerializeField] private float _cameraFollowSpeed = 35.0f;

        [Tooltip("Camera near clip plane to prevent rifle, sight, and facial clipping (default 0.02 = 2cm).")]
        [SerializeField] private float _nearClipPlane = 0.02f;

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
        #endregion

        #region Private Runtime State
        private CharacterController _characterController;
        private NetworkTransform _networkTransform;
        private Animator _animator;
        private PlayerWeaponHandler _weaponHandler;

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
        #endregion

        #region Public Properties & State Accessors
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
            _characterController = GetComponent<CharacterController>();
            if (_characterController != null)
            {
                _characterController.enabled = true;
            }

            _networkTransform = GetComponent<NetworkTransform>();
            _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
            _weaponHandler = GetComponent<PlayerWeaponHandler>();
            EnsureHeadBone();

            // Ensure NetworkTransform is configured for client-authoritative FPS movement
            if (_networkTransform != null)
            {
                _networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;
            }

            if (_animator == null)
            {
                Debug.LogError("[PlayerMovement] Animator component not found on player or children!", this);
            }
        }

        private void Start()
        {
            // Auto-setup camera and controls whether running in an offline test scene or spawned via NGO
            SetupCamera();
            LockCursor();

            if (_characterController != null)
            {
                _characterController.enabled = true;
            }

            PlayAnimationDirect(AnimStateIdle, 0f);
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

            // Locate existing camera or auto-generate one for the owner
            SetupCamera();

            if (IsOwner)
            {
                // Local player owns input, camera, and physics simulation
                LockCursor();

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

                // Play initial local idle
                PlayAnimationDirect(AnimStateIdle, 0f);
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

                // Subscribe to network locomotion updates for remote visual sync
                _networkLocomotionState.OnValueChanged += HandleRemoteLocomotionChanged;
                _networkIsMovingBackward.OnValueChanged += HandleRemoteBackwardChanged;

                // Sync initial remote state
                ApplyLocomotionAnimation((LocomotionState)_networkLocomotionState.Value, 0.1f);
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

        private void EnsureHeadBone()
        {
            if (_headBone == null)
            {
                if (_animator != null && _animator.isHuman)
                {
                    _headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
                }

                if (_headBone == null)
                {
                    _headBone = FindDeepChild(transform, "head.x")
                             ?? FindDeepChild(transform, "head")
                             ?? FindDeepChild(transform, "mixamorig:Head")
                             ?? FindDeepChild(transform, "Head");
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
                }
                UpdateCameraPositionAndRotation();
            }
        }

        private void Update()
        {
            // If spawned in a multiplayer session, only the network owner controls this instance
            if (IsSpawned && !IsOwner)
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
            // Only local player controls their camera view
            if (IsSpawned && !IsOwner)
            {
                return;
            }

            UpdateCameraPositionAndRotation();
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
                if (!Mathf.Approximately(cam.nearClipPlane, _nearClipPlane))
                {
                    cam.nearClipPlane = _nearClipPlane;
                }
            }

            // 1. Calculate the ideal eye position from the animated head bone
            Vector3 targetEyePos;
            if (_headBone != null)
            {
                // When looking down, slightly shift forward to simulate natural neck bend
                float lookDownNudge = Mathf.Clamp01(_cameraPitch / 85f) * 0.04f;

                targetEyePos = _headBone.position
                             + transform.forward * (_eyeOffsetForward + lookDownNudge)
                             + transform.up * _eyeOffsetUp
                             + transform.right * _eyeOffsetRight;
            }
            else
            {
                // Fallback if no head bone: use crouch/standing height
                float currentEyeHeight = _isCrouching ? _crouchHeight * 0.85f : _standingHeight * 0.9f;
                targetEyePos = transform.position + Vector3.up * currentEyeHeight + transform.forward * _eyeOffsetForward;
            }

            // 2. Position tracking: smooth interpolation prevents jitter while tracking head exactly
            if (_cameraFollowSpeed > 0f && Time.deltaTime > 0f && Application.isPlaying)
            {
                _cameraTransform.position = Vector3.Lerp(_cameraTransform.position, targetEyePos, Time.deltaTime * _cameraFollowSpeed);
            }
            else
            {
                _cameraTransform.position = targetEyePos;
            }

            // 3. Rotation: Pitch tilts vertically, Yaw matches body facing
            _cameraTransform.localRotation = Quaternion.Euler(_cameraPitch, 0f, 0f);

            // Ensure head bone scale is always intact (full original head)
            if (_headBone != null && _headBone.localScale != Vector3.one)
            {
                _headBone.localScale = Vector3.one;
            }
        }
        #endregion

        #region Input & Movement Handling
        private void HandleCursorToggle()
        {
            var keyboard = Keyboard.current ?? InputSystem.GetDevice<Keyboard>();
            var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                UnlockCursor();
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

            // Direct hardcoded key polling (WASD, Arrows, Shift, Ctrl, Space)
            var keyboard = Keyboard.current ?? InputSystem.GetDevice<Keyboard>();
            float horizontal = 0f;
            float vertical = 0f;
            bool isSprintHeld = false;
            bool isCrouchHeld = false;
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

                // Shift -> Sprint
                isSprintHeld = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;

                // Ctrl or C -> Crouch
                isCrouchHeld = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed || keyboard.cKey.isPressed;

                // Space -> Jump
                isJumpPressed = keyboard.spaceKey.wasPressedThisFrame;
            }

            _isCrouching = isCrouchHeld;
            AdjustColliderForCrouch(_isCrouching);

            Vector2 moveInput = new Vector2(horizontal, vertical);
            bool isMoving = moveInput.sqrMagnitude > 0.01f;

            // Determine active target speed
            float currentSpeed;
            if (_isCrouching)
            {
                currentSpeed = _crouchSpeed;
            }
            else if (isSprintHeld && isMoving && vertical > 0.1f)
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
            if (vertical < -0.1f || (Mathf.Abs(horizontal) > 0.1f && Mathf.Abs(vertical) < 0.1f))
            {
                currentSpeed *= _strafeMultiplier;
            }

            // Backward locomotion animation handling (S key plays Jog/Run in reverse)
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

            if (_isAirborne)
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

            // Jump execution (adapts facing to WASD direction when sprint jumping)
            if (isJumpPressed && _isGrounded && !_isCrouching)
            {
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

            // Resolve locomotion state
            ResolveLocomotionState(isMoving, isSprintHeld && vertical > 0.1f);
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
            var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();
            if (mouse == null || Cursor.lockState != CursorLockMode.Locked)
            {
                return;
            }

            // Left-click to fire rifle
            if (mouse.leftButton.isPressed && Time.time >= _lastFireTime + _fireRate)
            {
                _lastFireTime = Time.time;
                _attackTimer = _fireAnimationDuration;

                // Play firing animation directly
                PlayAnimationDirect(AnimStateFiringRifle, _fireTransitionDuration);
                if (IsSpawned)
                {
                    PlayActionAnimationRpc(ActionAnimation.FiringRifle);
                }
            }

            if (_attackTimer > 0f)
            {
                _attackTimer -= Time.deltaTime;
            }
        }
        #endregion

        #region Animation State Management
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
            // If actively firing rifle, keep attack animation in focus
            if (_attackTimer > 0f)
            {
                return;
            }

            // If airborne, maintain the appropriate jump state until landing
            if (_isAirborne)
            {
                int airborneHash = _isSprintJumpActive ? AnimStateSprintJump : AnimStateJumping;
                PlayAnimationDirect(airborneHash, _jumpTransitionDuration);
                return;
            }

            // Grounded locomotion
            ApplyLocomotionAnimation(_currentLocomotion, _locomotionTransitionDuration);
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

            // Don't interrupt an active remote one-shot action (e.g. jump or firing)
            if (_attackTimer <= 0f && !_isAirborne)
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

        /// <summary>
        /// Broadcasts one-shot action animations (Jumping, Sprint Jump, Firing Rifle)
        /// from the owning client to all remote observers.
        /// </summary>
        [Rpc(SendTo.NotOwner)]
        private void PlayActionAnimationRpc(ActionAnimation action)
        {
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
                    PlayAnimationDirect(AnimStateFiringRifle, _fireTransitionDuration);
                    break;
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
