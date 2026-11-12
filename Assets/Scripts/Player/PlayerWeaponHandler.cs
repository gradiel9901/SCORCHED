using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using Scorched.Weapons;

namespace Scorched.Player
{
    /// <summary>
    /// Local position/rotation offset of the WeaponSocket relative to the right hand bone.
    /// </summary>
    [System.Serializable]
    public struct WeaponPose
    {
        public Vector3 LocalPosition;
        public Vector3 LocalRotationEuler;

        public WeaponPose(Vector3 localPosition, Vector3 localRotationEuler)
        {
            LocalPosition = localPosition;
            LocalRotationEuler = localRotationEuler;
        }

        public Quaternion Rotation => Quaternion.Euler(LocalRotationEuler);
    }

    /// <summary>
    /// Weapon socket and weapon handling for "Scorched".
    /// - Guarantees WeaponSocket is parented to the right hand bone (hand.r).
    /// - Anchors the rifle mesh with its native model offset (Y: 39.832 deg, Pos: -0.047, -0.000, 0.026).
    /// - Uses hardcoded weapon poses calibrated from the exact user transforms for all animation states.
    /// - Seamlessly blends between poses during gameplay as movement animations transition.
    /// - Supports full Play-Mode tweaking and saving to disk/clipboard so adjustments are never lost.
    /// - Supports pickup (E) and drop (G) for weapons in the scene.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerMovement))]
    public class PlayerWeaponHandler : MonoBehaviour
    {
        #region Native Rifle Mesh Offset Inside Socket
        /// <summary>
        /// Native model offset of the AK-47 model within the WeaponSocket to align its grip with the hand.
        /// </summary>
        public static readonly Vector3 DefaultRiflePosition = new Vector3(-0.04708f, -0.00001f, 0.02651f);
        public static readonly Vector3 DefaultRifleEuler = new Vector3(0f, 39.832f, 0f);
        #endregion

        #region Hardcoded Weapon Poses
        /// <summary>
        /// Hardcoded WeaponSocket transforms per animation state, calibrated relative to hand.r.
        /// Index corresponds to WeaponPoseState enum:
        /// 0 = Idle, 1 = Jog, 2 = Running, 3 = Jumping, 4 = Sprint Jump, 5 = Crouch Idle, 6 = Crouched Run, 7 = Firing Rifle
        /// </summary>
        private static readonly WeaponPose[] DefaultPoses =
        {
            // Idle
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
            // Jog
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
            // Running
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
            // Jumping
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
            // Sprint Jump
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
            // Crouch Idle
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
            // Crouched Run
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
            // Firing Rifle
            new WeaponPose(new Vector3(0.035f, 0.085f, 0.015f), new Vector3(-55.0f, -178.0f, 90.0f)),
        };

        // Runtime overrides if saved by user during play mode
        private static WeaponPose[] s_RuntimeOverrides;

        /// <summary>
        /// Returns the active pose for a given state (checks runtime overrides first, then hardcoded defaults).
        /// </summary>
        public static WeaponPose GetPoseForState(WeaponPoseState state)
        {
            int index = (int)state;
            if (s_RuntimeOverrides != null && index >= 0 && index < s_RuntimeOverrides.Length)
            {
                if (s_RuntimeOverrides[index].Rotation != Quaternion.identity)
                {
                    return s_RuntimeOverrides[index];
                }
            }

            return index >= 0 && index < DefaultPoses.Length ? DefaultPoses[index] : DefaultPoses[0];
        }

        public static void SetRuntimePose(WeaponPoseState state, Vector3 pos, Vector3 rotEuler)
        {
            if (s_RuntimeOverrides == null)
            {
                s_RuntimeOverrides = (WeaponPose[])DefaultPoses.Clone();
            }

            int index = (int)state;
            if (index >= 0 && index < s_RuntimeOverrides.Length)
            {
                s_RuntimeOverrides[index] = new WeaponPose(pos, rotEuler);
            }
        }
        #endregion

        [Header("Bone & Socket Setup")]
        [Tooltip("The right hand bone (hand.r). Auto-detected if unassigned or invalid.")]
        [SerializeField] private Transform _rightHandBone;

        [Tooltip("The dedicated WeaponSocket transform parented under the hand bone.")]
        [SerializeField] private Transform _weaponSocket;

        [Tooltip("Currently equipped weapon transform in the socket.")]
        [SerializeField] private Transform _equippedWeapon;

        [Header("Ballistic Projectile & Muzzle")]
        [Tooltip("Bullet projectile prefab spawned when firing.")]
        [SerializeField] private GameObject _bulletPrefab;

        [Tooltip("Dedicated muzzle point at the tip of the rifle barrel.")]
        [SerializeField] private Transform _muzzlePoint;

        [Header("Blending")]
        [Tooltip("Transition speed when blending the socket between state poses.")]
        [SerializeField] private float _poseTransitionSpeed = 16.0f;

        [Header("Preview (Pose Inspection)")]
        [Tooltip("Freezes the character on the selected animation frame for fine-tuning.")]
        [SerializeField] private bool _enablePreviewMode = false;

        [SerializeField] private WeaponPoseState _previewState = WeaponPoseState.Idle;

        [Range(0f, 1f)]
        [Tooltip("Normalized clip time (0-1) to sample while previewing.")]
        [SerializeField] private float _previewNormalizedTime = 0.2f;

        [Header("Runtime Debug HUD")]
        [Tooltip("Show the on-screen preview HUD during Play Mode (F1 to toggle).")]
        [SerializeField] private bool _showDebugOverlay = false;

        private PlayerMovement _playerMovement;
        private Animator _animator;

        public Transform EquippedWeapon => _equippedWeapon;
        public Transform WeaponSocket => _weaponSocket;
        public Transform RightHandBone => _rightHandBone;

        public bool EnablePreviewMode
        {
            get => _enablePreviewMode;
            set
            {
                _enablePreviewMode = value;
                if (_enablePreviewMode)
                {
                    ApplyPreviewState(_previewState);
                }
            }
        }

        public WeaponPoseState PreviewState
        {
            get => _previewState;
            set => ApplyPreviewState(value);
        }

        public float PreviewNormalizedTime
        {
            get => _previewNormalizedTime;
            set
            {
                _previewNormalizedTime = Mathf.Clamp01(value);
                if (_enablePreviewMode)
                {
                    SamplePreviewAnimation();
                }
            }
        }

        #region Unity Lifecycle
        private void Awake()
        {
            CacheComponents();
            EnsureHandAndSocket();
            AutoDetectSceneWeaponIfNull();
            LoadSavedPosesFromFile();
            GetOrCreateMuzzlePoint();
            SyncWithNetworkWeaponController();
        }

        private void Start()
        {
            EnsureHandAndSocket();
            if (_equippedWeapon != null)
            {
                AttachWeaponToSocket(_equippedWeapon);
            }
            GetOrCreateMuzzlePoint();
            SyncWithNetworkWeaponController();
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            if (keyboard.eKey.wasPressedThisFrame && _equippedWeapon == null)
            {
                TryPickupNearbyOrSceneWeapon();
            }

            if (keyboard.gKey.wasPressedThisFrame && _equippedWeapon != null)
            {
                DropWeapon();
            }

            if (keyboard.f1Key.wasPressedThisFrame)
            {
                _showDebugOverlay = !_showDebugOverlay;
            }
        }

        private void LateUpdate()
        {
            if (_weaponSocket == null || _equippedWeapon == null)
            {
                return;
            }

            if (_enablePreviewMode)
            {
                // In preview mode: keep the animation frozen at the preview time
                SamplePreviewAnimation();
                return;
            }

            WeaponPoseState activeState;
            if (_animator != null && _animator.layerCount > 1 && _animator.GetLayerWeight(1) > 0.5f)
            {
                // When upper body layer is active (raised tactical carry / Modern Warfare), keep rifle locked in firing pose
                activeState = WeaponPoseState.FiringRifle;
            }
            else
            {
                activeState = _playerMovement != null
                    ? _playerMovement.CurrentWeaponPoseState
                    : WeaponPoseState.Idle;
            }

            WeaponPose target = GetPoseForState(activeState);
            Vector3 targetLocalPos = target.LocalPosition;

            float t = 1f - Mathf.Exp(-_poseTransitionSpeed * Time.deltaTime);

            _weaponSocket.localPosition = Vector3.Lerp(_weaponSocket.localPosition, targetLocalPos, t);
            _weaponSocket.localRotation = Quaternion.Slerp(_weaponSocket.localRotation, target.Rotation, t);
        }
        #endregion

        #region Setup & Socket Alignment
        private void CacheComponents()
        {
            _playerMovement = GetComponent<PlayerMovement>();
            _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
        }

        /// <summary>
        /// Locates the right hand bone (hand.r) and ensures WeaponSocket is parented directly to it.
        /// </summary>
        public void EnsureHandAndSocket()
        {
            if (_animator == null || _playerMovement == null)
            {
                CacheComponents();
            }

            // Verify or find hand.r
            if (_rightHandBone == null || !_rightHandBone.name.Equals("hand.r", StringComparison.OrdinalIgnoreCase))
            {
                _rightHandBone = FindDeepChild(transform, "hand.r")
                              ?? FindDeepChild(transform, "mixamorig:RightHand")
                              ?? FindDeepChild(transform, "RightHand");
            }

            if (_rightHandBone == null)
            {
                Debug.LogWarning("[PlayerWeaponHandler] Right hand bone (hand.r) not found in hierarchy.", this);
                return;
            }

            // Verify or find WeaponSocket
            if (_weaponSocket == null)
            {
                _weaponSocket = FindDeepChild(transform, "WeaponSocket");
                if (_weaponSocket == null)
                {
                    var socketGo = new GameObject("WeaponSocket");
                    socketGo.transform.SetParent(_rightHandBone, false);
                    _weaponSocket = socketGo.transform;
                }
            }

            // Ensure WeaponSocket is strictly parented under hand.r
            if (_weaponSocket.parent != _rightHandBone)
            {
                _weaponSocket.SetParent(_rightHandBone, false);
            }
        }

        /// <summary>
        /// Finds a weapon named 'rifle' / 'AK47NoSubdiv' in children or the scene.
        /// </summary>
        public void AutoDetectSceneWeaponIfNull()
        {
            if (_equippedWeapon != null)
            {
                return;
            }

            var found = FindDeepChild(transform, "rifle") ?? FindDeepChild(transform, "AK47NoSubdiv");
            if (found == null)
            {
                var sceneGo = GameObject.Find("rifle") ?? GameObject.Find("Rifle") ?? GameObject.Find("AK47NoSubdiv");
                found = sceneGo != null ? sceneGo.transform : null;
            }

            _equippedWeapon = found;
        }
        #endregion

        #region Pickup / Drop
        public void TryPickupNearbyOrSceneWeapon()
        {
            AutoDetectSceneWeaponIfNull();

            if (_equippedWeapon != null)
            {
                PickupWeapon(_equippedWeapon.gameObject);
            }
            else
            {
                Debug.LogWarning("[PlayerWeaponHandler] No weapon named 'rifle' or 'AK47NoSubdiv' found in scene.");
            }
        }

        public void PickupWeapon(GameObject weaponGo)
        {
            if (weaponGo == null)
            {
                return;
            }

            EnsureHandAndSocket();
            _equippedWeapon = weaponGo.transform;
            AttachWeaponToSocket(_equippedWeapon);

            if (weaponGo.TryGetComponent(out Rigidbody rb))
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }

            foreach (var c in weaponGo.GetComponentsInChildren<Collider>())
            {
                c.enabled = false;
            }
        }

        public void AttachWeaponToSocket(Transform weapon)
        {
            if (_weaponSocket == null || weapon == null)
            {
                return;
            }

            // Parent rifle to socket and apply its calibrated native model offset
            weapon.SetParent(_weaponSocket, false);
            weapon.localPosition = DefaultRiflePosition;
            weapon.localRotation = Quaternion.Euler(DefaultRifleEuler);
            weapon.localScale = Vector3.one;

            _muzzlePoint = null;
            GetOrCreateMuzzlePoint();
            SyncWithNetworkWeaponController();

            WeaponPoseState state = _enablePreviewMode || _playerMovement == null
                ? _previewState
                : _playerMovement.CurrentWeaponPoseState;
            SnapSocketToPose(state);
        }

        public void DropWeapon()
        {
            if (_equippedWeapon == null)
            {
                return;
            }

            var weapon = _equippedWeapon;
            _equippedWeapon = null;
            _muzzlePoint = null;
            SyncWithNetworkWeaponController();
            weapon.SetParent(null, true);

            if (weapon.TryGetComponent(out Rigidbody rb))
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
                rb.linearVelocity = transform.forward * 2.0f + Vector3.up * 1.5f;
            }

            foreach (var c in weapon.GetComponentsInChildren<Collider>())
            {
                c.enabled = true;
            }
        }

        public void SyncWithNetworkWeaponController()
        {
            if (TryGetComponent(out Weapons.NetworkWeaponController netWeap))
            {
                netWeap.MuzzlePoint = _muzzlePoint;
            }
        }
        #endregion

        #region Projectile Firing
        public Transform GetOrCreateMuzzlePoint()
        {
            if (_muzzlePoint != null) return _muzzlePoint;

            if (_equippedWeapon == null)
            {
                AutoDetectSceneWeaponIfNull();
            }

            if (_equippedWeapon != null)
            {
                _muzzlePoint = FindDeepChild(_equippedWeapon, "MuzzlePoint");
                if (_muzzlePoint == null)
                {
                    var muzzleGo = new GameObject("MuzzlePoint");
                    muzzleGo.transform.SetParent(_equippedWeapon, false);
                    _muzzlePoint = muzzleGo.transform;
                }
                // Calibrated barrel tip for AK-47 model in local space
                _muzzlePoint.localPosition = new Vector3(0.0f, 0.022f, 0.525f);
                _muzzlePoint.localRotation = Quaternion.identity;
                _muzzlePoint.localScale = Vector3.one;
            }
            return _muzzlePoint;
        }

        public Vector3 GetMuzzlePosition()
        {
            var perspective = GetComponent<FPSPerspectiveController>();
            if (perspective != null && perspective.ActiveMuzzlePoint != null)
            {
                return perspective.ActiveMuzzlePoint.position;
            }

            var muzzle = GetOrCreateMuzzlePoint();
            if (muzzle != null) return muzzle.position;

            if (_equippedWeapon != null)
            {
                return _equippedWeapon.position + _equippedWeapon.forward * 0.65f + _equippedWeapon.up * 0.045f;
            }

            return transform.position + transform.forward * 0.5f + Vector3.up * 1.5f;
        }

        public BulletProjectile SpawnBullet(Vector3 targetPoint, Collider shooterCollider)
        {
            if (_bulletPrefab == null)
            {
#if UNITY_EDITOR
                _bulletPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Bullet_Projectile.prefab");
#endif
            }

            Vector3 spawnPos = GetMuzzlePosition();
            Vector3 fireDirection = (targetPoint - spawnPos).normalized;
            if (fireDirection.sqrMagnitude < 0.001f)
            {
                fireDirection = transform.forward;
            }

            GameObject bulletGo = null;
            if (_bulletPrefab != null)
            {
                bulletGo = Instantiate(_bulletPrefab, spawnPos, Quaternion.LookRotation(fireDirection, Vector3.up));
            }
            else
            {
                bulletGo = new GameObject("Bullet_Runtime_Fallback");
                bulletGo.transform.position = spawnPos;
                bulletGo.transform.rotation = Quaternion.LookRotation(fireDirection, Vector3.up);
                bulletGo.AddComponent<BulletProjectile>();
            }

            var proj = bulletGo.GetComponent<BulletProjectile>();
            if (proj != null)
            {
                proj.Initialize(fireDirection, shooterCollider);
            }

            return proj;
        }
        #endregion

        #region Preview & Pose Snapping
        public void ApplyPreviewState(WeaponPoseState state)
        {
            _previewState = state;

            if (_animator == null || _playerMovement == null)
            {
                CacheComponents();
            }

            SamplePreviewAnimation();
            SnapSocketToPose(state);
        }

        public void SamplePreviewAnimation()
        {
            if (_playerMovement != null)
            {
                _playerMovement.PreviewStateAnimation(_previewState, _previewNormalizedTime);
            }
        }

        public void SnapSocketToPose(WeaponPoseState state)
        {
            EnsureHandAndSocket();
            if (_weaponSocket == null)
            {
                return;
            }

            WeaponPose pose = GetPoseForState(state);
            _weaponSocket.localPosition = pose.LocalPosition;
            _weaponSocket.localRotation = pose.Rotation;
        }

        public void LogCurrentSocketPose()
        {
            if (_weaponSocket == null)
            {
                return;
            }

            Vector3 p = _weaponSocket.localPosition;
            Vector3 r = _weaponSocket.localEulerAngles;
            r = new Vector3(NormalizeAngle(r.x), NormalizeAngle(r.y), NormalizeAngle(r.z));

            string code = $"new WeaponPose(new Vector3({p.x:F3}f, {p.y:F3}f, {p.z:F3}f), new Vector3({r.x:F3}f, {r.y:F3}f, {r.z:F3}f)), // {_previewState}";
            GUIUtility.systemCopyBuffer = code;

            Debug.Log($"[PlayerWeaponHandler] {_previewState} pose (COPIED TO CLIPBOARD):\n" +
                      $"Position: ({p.x:F3}, {p.y:F3}, {p.z:F3})  Rotation: ({r.x:F3}, {r.y:F3}, {r.z:F3})\n{code}", this);
        }

        public void SaveCurrentSocketPoseToState(WeaponPoseState state)
        {
            if (_weaponSocket == null)
            {
                return;
            }

            Vector3 p = _weaponSocket.localPosition;
            Vector3 r = _weaponSocket.localEulerAngles;
            r = new Vector3(NormalizeAngle(r.x), NormalizeAngle(r.y), NormalizeAngle(r.z));

            SetRuntimePose(state, p, r);
            SaveSavedPosesToFile();

            LogCurrentSocketPose();
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            return angle > 180f ? angle - 360f : angle;
        }
        #endregion

        #region Persistence (Save During Play Mode)
        [System.Serializable]
        private class SavedPoseData
        {
            public float px, py, pz;
            public float rx, ry, rz;
        }

        [System.Serializable]
        private class SavedPosesContainer
        {
            public SavedPoseData[] Poses;
        }

        private static string GetSavePath()
        {
            return System.IO.Path.Combine(Application.dataPath, "WeaponPoses_Saved.json");
        }

        public void SaveSavedPosesToFile()
        {
            try
            {
                var container = new SavedPosesContainer
                {
                    Poses = new SavedPoseData[8]
                };

                for (int i = 0; i < 8; i++)
                {
                    var pose = GetPoseForState((WeaponPoseState)i);
                    container.Poses[i] = new SavedPoseData
                    {
                        px = pose.LocalPosition.x,
                        py = pose.LocalPosition.y,
                        pz = pose.LocalPosition.z,
                        rx = pose.LocalRotationEuler.x,
                        ry = pose.LocalRotationEuler.y,
                        rz = pose.LocalRotationEuler.z
                    };
                }

                System.IO.File.WriteAllText(GetSavePath(), JsonUtility.ToJson(container, true));
                Debug.Log($"[PlayerWeaponHandler] Successfully saved weapon poses to {GetSavePath()}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerWeaponHandler] Failed to save poses: {ex.Message}");
            }
        }

        public void LoadSavedPosesFromFile()
        {
            try
            {
                string path = GetSavePath();
                if (!System.IO.File.Exists(path))
                {
                    return;
                }

                string json = System.IO.File.ReadAllText(path);
                var container = JsonUtility.FromJson<SavedPosesContainer>(json);
                if (container != null && container.Poses != null && container.Poses.Length == 8)
                {
                    s_RuntimeOverrides = new WeaponPose[8];
                    for (int i = 0; i < 8; i++)
                    {
                        var d = container.Poses[i];
                        s_RuntimeOverrides[i] = new WeaponPose(new Vector3(d.px, d.py, d.pz), new Vector3(d.rx, d.ry, d.rz));
                    }
                    Debug.Log("[PlayerWeaponHandler] Loaded saved weapon poses from disk.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PlayerWeaponHandler] Could not load saved poses: {ex.Message}");
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

        #region Runtime HUD
        private void OnGUI()
        {
            if (!_showDebugOverlay)
            {
                return;
            }

            GUILayout.BeginArea(new Rect(Screen.width - 320, 10, 310, 440), GUI.skin.box);
            GUILayout.Label("=== WEAPON POSE PREVIEW (F1 to Toggle) ===");
            GUILayout.Label($"Weapon: {(_equippedWeapon != null ? _equippedWeapon.name : "None (E to pick up)")}");
            GUILayout.Label($"Socket Parent: {(_weaponSocket != null && _weaponSocket.parent != null ? _weaponSocket.parent.name : "None")}");

            if (GUILayout.Button(_enablePreviewMode ? "Preview: ON (Click to Resume Gameplay)" : "Preview: OFF (Click to Inspect Poses)", GUILayout.Height(26)))
            {
                EnablePreviewMode = !_enablePreviewMode;
            }

            if (_enablePreviewMode)
            {
                GUILayout.Label($"Clip Frame: {_previewNormalizedTime * 100f:F0}%");
                float t = GUILayout.HorizontalSlider(_previewNormalizedTime, 0f, 1f);
                if (!Mathf.Approximately(t, _previewNormalizedTime))
                {
                    PreviewNormalizedTime = t;
                }
            }

            GUILayout.Space(4);
            GUILayout.Label("Select State Pose:");
            foreach (WeaponPoseState state in Enum.GetValues(typeof(WeaponPoseState)))
            {
                bool selected = _previewState == state;
                if (GUILayout.Button(selected ? $"> {state} <" : state.ToString(), GUILayout.Height(20)))
                {
                    _enablePreviewMode = true;
                    ApplyPreviewState(state);
                }
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Save Socket Transform to Current State", GUILayout.Height(26)))
            {
                SaveCurrentSocketPoseToState(_previewState);
            }

            if (GUILayout.Button("Copy C# Pose Line to Clipboard", GUILayout.Height(22)))
            {
                LogCurrentSocketPose();
            }

            GUILayout.EndArea();
        }
        #endregion
    }
}
