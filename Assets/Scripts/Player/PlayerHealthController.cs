using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.InputSystem;
using Scorched.Combat;
using Scorched.Weapons;

namespace Scorched.Player
{
    /// <summary>
    /// Three-stage Battle Royale player lifecycle state.
    /// </summary>
    public enum PlayerLifeState : byte
    {
        Alive = 0,
        Downed = 1,
        Dead = 2
    }

    /// <summary>
    /// Server-authoritative player health, DBNO (Down But Not Out), Self/Teammate Revive,
    /// Dead-state lock, and delayed in-place Respawn controller.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerHealthController : NetworkBehaviour, IDamageable
    {
        private static readonly List<PlayerHealthController> s_activeInstances = new List<PlayerHealthController>();

        [Header("Alive & Downed Vitals")]
        [SerializeField] private float _maxHealth = 100f;
        [SerializeField] private float _maxDownedHealth = 100f;
        [SerializeField] private float _downedBleedRate = 3.5f;
        [SerializeField] private float _downedGraceDuration = 1.25f;
        [Range(0.1f, 1.0f)]
        [SerializeField] private float _downedDamageMultiplier = 0.55f;
        [SerializeField] private float _downedSpeedMultiplier = 0.25f;

        [Header("Revive & Respawn Timing")]
        [SerializeField] private float _reviveDuration = 5.0f;
        [SerializeField] private float _reviveHealth = 50.0f;
        [SerializeField] private float _reviveRange = 2.5f;
        [SerializeField] private float _respawnDelay = 4.0f;

        #region Network State
        public readonly NetworkVariable<PlayerLifeState> CurrentState = new NetworkVariable<PlayerLifeState>(
            PlayerLifeState.Alive,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> Health = new NetworkVariable<float>(
            100f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> DownedHealth = new NetworkVariable<float>(
            100f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<float> ReviveProgress = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> IsDownedMoving = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);
        #endregion

        #region Standalone / Offline Fallback & Server Runtime State
        private PlayerLifeState _localState = PlayerLifeState.Alive;
        private float _localHealth = 100f;
        private float _localDownedHealth = 100f;
        private float _localReviveProgress = 0f;
        private bool _localIsDownedMoving = false;

        private float _downedGraceTimer = 0f;
        private float _reviveTimer = 0f;
        private bool _isRespawning = false;
        private Coroutine _respawnCoroutine;

        // Per-shot / per-frame duplicate hit protection
        private int _lastProcessedShotId = int.MinValue;
        private ulong _lastProcessedShotAttacker = ulong.MaxValue;
        private int _lastProcessedFrame = -1;
        private ulong _lastProcessedFrameAttacker = ulong.MaxValue;

        // Input tracking (Owner -> Server)
        private bool _clientHoldingSelfRevive = false;
        private bool _serverHoldingSelfRevive = false;
        private ulong _clientTeammateReviveTargetId = ulong.MaxValue;
        private ulong _serverTeammateReviveTargetId = ulong.MaxValue;
        #endregion

        private BodyPartHitbox[] _hitboxes;
        private PlayerMovement _playerMovement;
        private FPSPerspectiveController _perspectiveController;
        private NetworkWeaponController _weaponController;
        private CharacterController _characterController;
        private NetworkTransform _networkTransform;
        private bool _hudSubscribed = false;

        public event Action<PlayerLifeState, PlayerLifeState> OnLifeStateChanged;

        #region Public Properties
        public PlayerLifeState LifeState => IsSpawned ? CurrentState.Value : _localState;
        public float CurrentHealthValue => IsSpawned ? Health.Value : _localHealth;
        public float CurrentDownedHealthValue => IsSpawned ? DownedHealth.Value : _localDownedHealth;
        public float CurrentReviveProgress => IsSpawned ? ReviveProgress.Value : _localReviveProgress;
        public bool DownedMoving => IsSpawned ? IsDownedMoving.Value : _localIsDownedMoving;
        public float MaxHealth => _maxHealth;
        public float MaxDownedHealth => _maxDownedHealth;
        public float DownedSpeedMultiplier => _downedSpeedMultiplier;
        public float DownedGraceRemaining => _downedGraceTimer;
        public bool IsAlive => LifeState == PlayerLifeState.Alive;
        public bool IsDowned => LifeState == PlayerLifeState.Downed;
        public bool IsDead => LifeState == PlayerLifeState.Dead;
        public bool CanMove => LifeState != PlayerLifeState.Dead;
        public bool CanPerformCombatActions => LifeState == PlayerLifeState.Alive;
        public bool IsRespawning => _isRespawning;
        public static IReadOnlyList<PlayerHealthController> ActiveInstances => s_activeInstances;
        #endregion

        private void Awake()
        {
            _localHealth = _maxHealth;
            _localDownedHealth = _maxDownedHealth;
            _localState = PlayerLifeState.Alive;

            _playerMovement = GetComponent<PlayerMovement>();
            _perspectiveController = GetComponent<FPSPerspectiveController>();
            _weaponController = GetComponent<NetworkWeaponController>();
            _characterController = GetComponent<CharacterController>();
            _networkTransform = GetComponent<NetworkTransform>();

            RefreshHitboxes();
        }

        private void OnEnable()
        {
            if (!s_activeInstances.Contains(this))
            {
                s_activeInstances.Add(this);
            }
        }

        private void OnDisable()
        {
            s_activeInstances.Remove(this);
            if (_respawnCoroutine != null)
            {
                StopCoroutine(_respawnCoroutine);
                _respawnCoroutine = null;
                _isRespawning = false;
            }
        }

        public void RefreshHitboxes()
        {
            _hitboxes = GetComponentsInChildren<BodyPartHitbox>(true);
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            RefreshHitboxes();

            CurrentState.OnValueChanged += HandleStateChanged;
            Health.OnValueChanged += HandleHealthValueChanged;
            DownedHealth.OnValueChanged += HandleDownedHealthChanged;
            ReviveProgress.OnValueChanged += HandleReviveProgressChanged;

            if (IsServer)
            {
                _localHealth = _maxHealth;
                _localDownedHealth = _maxDownedHealth;
                _localReviveProgress = 0f;
                _localState = PlayerLifeState.Alive;
                _downedGraceTimer = 0f;
                _reviveTimer = 0f;
                _isRespawning = false;
                _serverHoldingSelfRevive = false;
                _serverTeammateReviveTargetId = ulong.MaxValue;

                Health.Value = _maxHealth;
                DownedHealth.Value = _maxDownedHealth;
                ReviveProgress.Value = 0f;
                CurrentState.Value = PlayerLifeState.Alive;
            }

            ApplyLifeStateEffects(LifeState);
            PushHudUpdate();
        }

        public override void OnNetworkDespawn()
        {
            CurrentState.OnValueChanged -= HandleStateChanged;
            Health.OnValueChanged -= HandleHealthValueChanged;
            DownedHealth.OnValueChanged -= HandleDownedHealthChanged;
            ReviveProgress.OnValueChanged -= HandleReviveProgressChanged;

            if (_respawnCoroutine != null)
            {
                StopCoroutine(_respawnCoroutine);
                _respawnCoroutine = null;
                _isRespawning = false;
            }

            base.OnNetworkDespawn();
        }

        private void Update()
        {
            bool isLocalOwner = !IsSpawned || IsOwner;
            if (isLocalOwner)
            {
                if (!_hudSubscribed && Scorched.UI.L4DHUDManager.Instance != null)
                {
                    _hudSubscribed = true;
                    PushHudUpdate();
                }

                HandleLocalReviveInput();
            }

            if (IsServer || !IsSpawned)
            {
                UpdateServerLifecycle(Time.deltaTime);
            }
        }

        public void SetDownedMovingState(bool isMoving)
        {
            if (IsSpawned)
            {
                if (IsOwner && IsDownedMoving.Value != isMoving)
                {
                    IsDownedMoving.Value = isMoving;
                }
            }
            else
            {
                _localIsDownedMoving = isMoving;
            }
        }

        /// <summary>
        /// Allows Owner input or automated tests to drive Self-Revive cleanly.
        /// </summary>
        public void SetSelfReviveHeld(bool held)
        {
            if (!IsSpawned)
            {
                _clientHoldingSelfRevive = held;
                _serverHoldingSelfRevive = held;
                return;
            }

            if (!IsOwner)
            {
                return;
            }

            if (_clientHoldingSelfRevive != held)
            {
                _clientHoldingSelfRevive = held;
                SetSelfReviveHeldServerRpc(held);
            }
        }

        private void HandleLocalReviveInput()
        {
            if (Scorched.UI.MainMenuUIManager.IsMenuOpen)
            {
                SetSelfReviveHeld(false);
                UpdateTeammateReviveTarget(ulong.MaxValue);
                return;
            }

            Keyboard kb = Keyboard.current;
            bool fHeld = kb != null && kb.fKey != null && kb.fKey.isPressed;
            bool eHeld = kb != null && kb.eKey != null && kb.eKey.isPressed;

            if (IsDowned)
            {
                SetSelfReviveHeld(fHeld || eHeld);
                UpdateTeammateReviveTarget(ulong.MaxValue);
            }
            else if (IsAlive && eHeld)
            {
                SetSelfReviveHeld(false);
                PlayerHealthController target = FindRevivableTeammateInRange();
                UpdateTeammateReviveTarget(target != null ? target.OwnerClientId : ulong.MaxValue);
            }
            else
            {
                SetSelfReviveHeld(false);
                UpdateTeammateReviveTarget(ulong.MaxValue);
            }
        }

        private void UpdateTeammateReviveTarget(ulong targetClientId)
        {
            if (_clientTeammateReviveTargetId == targetClientId)
            {
                return;
            }

            _clientTeammateReviveTargetId = targetClientId;
            if (IsSpawned)
            {
                SetTeammateReviveTargetServerRpc(targetClientId);
            }
            else
            {
                _serverTeammateReviveTargetId = targetClientId;
            }
        }

        private PlayerHealthController FindRevivableTeammateInRange()
        {
            PlayerHealthController best = null;
            float bestDistSqr = _reviveRange * _reviveRange;

            for (int i = 0; i < s_activeInstances.Count; i++)
            {
                PlayerHealthController candidate = s_activeInstances[i];
                if (candidate == null || candidate == this || !candidate.IsDowned)
                {
                    continue;
                }

                if (!CanReviveTarget(candidate))
                {
                    continue;
                }

                float d2 = (candidate.transform.position - transform.position).sqrMagnitude;
                if (d2 <= bestDistSqr)
                {
                    bestDistSqr = d2;
                    best = candidate;
                }
            }

            return best;
        }

        private bool CanReviveTarget(PlayerHealthController target)
        {
            if (target == null || target == this || !target.IsDowned)
            {
                return false;
            }

            if (NetworkMatchManager.Instance == null)
            {
                return false;
            }

            return NetworkMatchManager.Instance.CurrentGameMode == GameMode.TeamMatch &&
                   NetworkMatchManager.Instance.GetPlayerTeam(OwnerClientId) == NetworkMatchManager.Instance.GetPlayerTeam(target.OwnerClientId);
        }

        [Rpc(SendTo.Server)]
        private void SetSelfReviveHeldServerRpc(bool held)
        {
            _serverHoldingSelfRevive = held;
        }

        [Rpc(SendTo.Server)]
        private void SetTeammateReviveTargetServerRpc(ulong targetClientId)
        {
            _serverTeammateReviveTargetId = targetClientId;
        }

        private void UpdateServerLifecycle(float dt)
        {
            if (dt <= 0f || LifeState != PlayerLifeState.Downed)
            {
                return;
            }

            if (_downedGraceTimer > 0f)
            {
                _downedGraceTimer = Mathf.Max(0f, _downedGraceTimer - dt);
            }

            bool isBeingRevived = _serverHoldingSelfRevive || IsReceivingTeammateReviveOnServer();
            if (isBeingRevived)
            {
                _reviveTimer += dt;
                float progress = Mathf.Clamp01(_reviveTimer / Mathf.Max(0.1f, _reviveDuration));
                SetReviveProgressAuthoritative(progress);

                if (_reviveTimer >= _reviveDuration)
                {
                    TransitionToRevivedAuthoritative();
                }
            }
            else
            {
                if (_reviveTimer > 0f)
                {
                    _reviveTimer = 0f;
                    SetReviveProgressAuthoritative(0f);
                }

                float nextDownedHealth = Mathf.Max(0f, CurrentDownedHealthValue - _downedBleedRate * dt);
                SetDownedHealthAuthoritative(nextDownedHealth);
                if (nextDownedHealth <= 0f)
                {
                    TransitionToDeadAuthoritative();
                }
            }
        }

        private bool IsReceivingTeammateReviveOnServer()
        {
            float maxDistSqr = (_reviveRange + 0.5f) * (_reviveRange + 0.5f);
            for (int i = 0; i < s_activeInstances.Count; i++)
            {
                PlayerHealthController helper = s_activeInstances[i];
                if (helper == null || helper == this || !helper.IsAlive)
                {
                    continue;
                }

                if (helper._serverTeammateReviveTargetId != OwnerClientId)
                {
                    continue;
                }

                if (!helper.CanReviveTarget(this))
                {
                    continue;
                }

                if ((helper.transform.position - transform.position).sqrMagnitude <= maxDistSqr)
                {
                    return true;
                }
            }

            return false;
        }

        public void ApplyCombatHit(float healthDamage, float hydrationDamage, ulong attackerClientId, Vector3 hitPoint)
        {
            ApplyBodyPartHit(healthDamage, hydrationDamage, attackerClientId, hitPoint, BodyPartType.UpperTorso, 1.0f, -1);
        }

        public void ApplyBodyPartHit(
            float baseDamage,
            float hydrationDamage,
            ulong attackerClientId,
            Vector3 hitPoint,
            BodyPartType bodyPart,
            float zoneMultiplier,
            int shotId)
        {
            if (IsSpawned && !IsServer)
            {
                return;
            }

            if (baseDamage <= 0f)
            {
                return;
            }

            if (NetworkMatchManager.Instance != null && !NetworkMatchManager.Instance.CanDamage(attackerClientId, OwnerClientId))
            {
                return;
            }

            // Hard server lock: dead players never absorb additional hits or re-trigger death/respawn
            if (LifeState == PlayerLifeState.Dead)
            {
                return;
            }

            // Grace timer immediately after being knocked into DBNO prevents burst/auto overkill
            if (LifeState == PlayerLifeState.Downed && _downedGraceTimer > 0f)
            {
                return;
            }

            // Strict 1-hit-per-shot-per-player deduplication
            if (shotId >= 0)
            {
                if (_lastProcessedShotId == shotId && _lastProcessedShotAttacker == attackerClientId)
                {
                    return;
                }
                _lastProcessedShotId = shotId;
                _lastProcessedShotAttacker = attackerClientId;
            }
            else if (_lastProcessedFrame == Time.frameCount && _lastProcessedFrameAttacker == attackerClientId)
            {
                // Non-shotId callers cannot stack multiple limb colliders on the exact same frame from one attacker
                return;
            }

            _lastProcessedFrame = Time.frameCount;
            _lastProcessedFrameAttacker = attackerClientId;

            float lifeStateScale = LifeState == PlayerLifeState.Downed
                ? Mathf.Clamp(_downedDamageMultiplier, 0.1f, 1.0f)
                : 1.0f;
            float scaledDamage = Mathf.Max(0f, baseDamage * Mathf.Max(0f, zoneMultiplier) * lifeStateScale);
            if (scaledDamage <= 0f)
            {
                return;
            }

            Debug.Log($"[Combat] Player {OwnerClientId} hit in {bodyPart} ({zoneMultiplier:F2}x * {lifeStateScale:F2}x) for {scaledDamage:F1} dmg (raw {baseDamage:F1}) by Client {attackerClientId} at {hitPoint} [State={LifeState}]");

            if (LifeState == PlayerLifeState.Alive)
            {
                float newHp = Mathf.Max(0f, CurrentHealthValue - scaledDamage);
                SetHealthAuthoritative(newHp);

                if (newHp <= 0f)
                {
                    TransitionToDownedAuthoritative();
                }
            }
            else if (LifeState == PlayerLifeState.Downed)
            {
                float newDownHp = Mathf.Max(0f, CurrentDownedHealthValue - scaledDamage);
                SetDownedHealthAuthoritative(newDownHp);

                if (newDownHp <= 0f)
                {
                    TransitionToDeadAuthoritative();
                }
            }

            if (!IsSpawned)
            {
                PushHudUpdate();
            }
        }

        private void TransitionToDownedAuthoritative()
        {
            if (LifeState != PlayerLifeState.Alive)
            {
                return;
            }

            PlayerLifeState previous = LifeState;
            _downedGraceTimer = _downedGraceDuration;
            _reviveTimer = 0f;
            _serverHoldingSelfRevive = false;
            _serverTeammateReviveTargetId = ulong.MaxValue;

            SetHealthAuthoritative(0f);
            SetDownedHealthAuthoritative(_maxDownedHealth);
            SetReviveProgressAuthoritative(0f);
            SetStateAuthoritative(PlayerLifeState.Downed);

            if (IsSpawned)
            {
                NotifyStateTransitionClientRpc(PlayerLifeState.Downed);
            }
            else
            {
                NotifyTransitionLocal(previous, PlayerLifeState.Downed);
            }

            Debug.Log($"[Lifecycle] Player {OwnerClientId} entered DOWNED state (DownedHP={_maxDownedHealth:F0}, Grace={_downedGraceDuration:F2}s).");
        }

        private void TransitionToRevivedAuthoritative()
        {
            if (LifeState != PlayerLifeState.Downed)
            {
                return;
            }

            PlayerLifeState previous = LifeState;
            _reviveTimer = 0f;
            _downedGraceTimer = 0f;
            _serverHoldingSelfRevive = false;
            _clientHoldingSelfRevive = false;

            SetHealthAuthoritative(_reviveHealth);
            SetDownedHealthAuthoritative(_maxDownedHealth);
            SetReviveProgressAuthoritative(0f);
            SetStateAuthoritative(PlayerLifeState.Alive);

            if (IsSpawned)
            {
                NotifyStateTransitionClientRpc(PlayerLifeState.Alive);
            }
            else
            {
                NotifyTransitionLocal(previous, PlayerLifeState.Alive);
            }

            Debug.Log($"[Lifecycle] Player {OwnerClientId} REVIVED with {_reviveHealth:F0} HP.");
        }

        private void TransitionToDeadAuthoritative()
        {
            if (LifeState == PlayerLifeState.Dead || _isRespawning)
            {
                return;
            }

            PlayerLifeState previous = LifeState;
            _reviveTimer = 0f;
            _downedGraceTimer = 0f;
            _serverHoldingSelfRevive = false;
            _clientHoldingSelfRevive = false;
            _serverTeammateReviveTargetId = ulong.MaxValue;

            SetHealthAuthoritative(0f);
            SetDownedHealthAuthoritative(0f);
            SetReviveProgressAuthoritative(0f);
            SetStateAuthoritative(PlayerLifeState.Dead);

            if (IsSpawned)
            {
                NotifyStateTransitionClientRpc(PlayerLifeState.Dead);
            }
            else
            {
                NotifyTransitionLocal(previous, PlayerLifeState.Dead);
            }

            Debug.Log($"[Lifecycle] Player {OwnerClientId} entered DEAD state. Respawning in {_respawnDelay:F1}s...");

            if (_respawnCoroutine != null)
            {
                StopCoroutine(_respawnCoroutine);
            }
            _respawnCoroutine = StartCoroutine(RespawnSequenceCoroutine());
        }

        private IEnumerator RespawnSequenceCoroutine()
        {
            _isRespawning = true;
            yield return new WaitForSeconds(_respawnDelay);

            Vector3 spawnPos = transform.position;
            Quaternion spawnRot = transform.rotation;

            int team = -1;
            GameMode mode = GameMode.FreeForAll;
            if (NetworkMatchManager.Instance != null)
            {
                team = NetworkMatchManager.Instance.GetPlayerTeam(OwnerClientId);
                mode = NetworkMatchManager.Instance.CurrentGameMode;
            }

            if (TacticalSpawnManager.Instance != null)
            {
                TacticalSpawnManager.Instance.GetSpawn(OwnerClientId, team, mode, out spawnPos, out spawnRot);
            }

            PlayerLifeState previous = LifeState;
            _reviveTimer = 0f;
            _downedGraceTimer = 0f;
            _serverHoldingSelfRevive = false;
            _clientHoldingSelfRevive = false;
            _serverTeammateReviveTargetId = ulong.MaxValue;

            SetHealthAuthoritative(_maxHealth);
            SetDownedHealthAuthoritative(_maxDownedHealth);
            SetReviveProgressAuthoritative(0f);
            SetStateAuthoritative(PlayerLifeState.Alive);

            if (IsSpawned)
            {
                NotifyStateTransitionClientRpc(PlayerLifeState.Alive);
                RespawnTeleportOwnerRpc(spawnPos, spawnRot);
            }
            else
            {
                NotifyTransitionLocal(previous, PlayerLifeState.Alive);
                ApplyRespawnLocal(spawnPos, spawnRot);
            }

            _isRespawning = false;
            _respawnCoroutine = null;
        }

        [Rpc(SendTo.Everyone)]
        private void NotifyStateTransitionClientRpc(PlayerLifeState newState)
        {
            ApplyLifeStateEffects(newState);
            PushHudUpdate();
        }

        [Rpc(SendTo.Owner)]
        private void RespawnTeleportOwnerRpc(Vector3 spawnPos, Quaternion spawnRot)
        {
            ApplyRespawnLocal(spawnPos, spawnRot);
        }

        private void ApplyRespawnLocal(Vector3 spawnPos, Quaternion spawnRot)
        {
            if (_characterController != null)
            {
                _characterController.enabled = false;
            }

            transform.position = spawnPos;
            transform.rotation = spawnRot;

            if (_networkTransform != null && IsSpawned)
            {
                _networkTransform.Teleport(spawnPos, spawnRot, transform.localScale);
            }

            if (_characterController != null)
            {
                _characterController.enabled = true;
            }

            _localHealth = _maxHealth;
            _localDownedHealth = _maxDownedHealth;
            _localReviveProgress = 0f;
            _localIsDownedMoving = false;
            _clientHoldingSelfRevive = false;
            _clientTeammateReviveTargetId = ulong.MaxValue;

            if (IsSpawned && IsOwner)
            {
                IsDownedMoving.Value = false;
            }

            _playerMovement?.OnHealthControllerRespawned();
            _weaponController?.ResetWeaponAfterRespawn();
            _perspectiveController?.HandleLifeStateChanged(PlayerLifeState.Alive);

            ApplyLifeStateEffects(PlayerLifeState.Alive);
            PushHudUpdate();

            Debug.Log($"[Lifecycle] Player {OwnerClientId} respawned in-place at {spawnPos} with {_maxHealth:F0} HP.");
        }

        private void NotifyTransitionLocal(PlayerLifeState previous, PlayerLifeState current)
        {
            ApplyLifeStateEffects(current);
            OnLifeStateChanged?.Invoke(previous, current);
            PushHudUpdate();
        }

        private void HandleStateChanged(PlayerLifeState previous, PlayerLifeState current)
        {
            ApplyLifeStateEffects(current);
            OnLifeStateChanged?.Invoke(previous, current);
            PushHudUpdate();
        }

        private void HandleHealthValueChanged(float previous, float current)
        {
            _localHealth = current;
            PushHudUpdate();
        }

        private void HandleDownedHealthChanged(float previous, float current)
        {
            _localDownedHealth = current;
            PushHudUpdate();
        }

        private void HandleReviveProgressChanged(float previous, float current)
        {
            _localReviveProgress = current;
            PushHudUpdate();
        }

        private void ApplyLifeStateEffects(PlayerLifeState state)
        {
            bool hitboxActive = state != PlayerLifeState.Dead;
            if (_hitboxes == null || _hitboxes.Length == 0)
            {
                RefreshHitboxes();
            }

            if (_hitboxes != null)
            {
                for (int i = 0; i < _hitboxes.Length; i++)
                {
                    if (_hitboxes[i] != null)
                    {
                        _hitboxes[i].SetHitboxActive(hitboxActive);
                    }
                }
            }

            // Force immediate animation transition and movement/perspective locks on ALL clients
            _playerMovement?.NotifyLifeStateChanged(state);
            _perspectiveController?.HandleLifeStateChanged(state);
        }

        private void PushHudUpdate()
        {
            bool isLocalOwner = !IsSpawned || IsOwner;
            if (!isLocalOwner)
            {
                return;
            }

            Scorched.UI.L4DHUDManager hud = Scorched.UI.L4DHUDManager.Instance;
            if (hud != null)
            {
                hud.UpdateLifeState(
                    LifeState,
                    CurrentHealthValue,
                    _maxHealth,
                    CurrentDownedHealthValue,
                    _maxDownedHealth,
                    CurrentReviveProgress);
            }
        }

        private void SetStateAuthoritative(PlayerLifeState state)
        {
            _localState = state;
            if (IsSpawned && IsServer)
            {
                CurrentState.Value = state;
            }
        }

        private void SetHealthAuthoritative(float value)
        {
            _localHealth = value;
            if (IsSpawned && IsServer)
            {
                Health.Value = value;
            }
        }

        private void SetDownedHealthAuthoritative(float value)
        {
            _localDownedHealth = value;
            if (IsSpawned && IsServer)
            {
                DownedHealth.Value = value;
            }
        }

        private void SetReviveProgressAuthoritative(float value)
        {
            _localReviveProgress = value;
            if (IsSpawned && IsServer)
            {
                ReviveProgress.Value = value;
            }
        }
    }
}

