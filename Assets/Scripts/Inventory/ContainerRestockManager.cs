using Unity.Netcode;
using UnityEngine;

namespace Scorched.Inventory
{
    /// <summary>
    /// Server-authoritative 30-second checker manager for indoor Supply Boxes (Inside_Chest)
    /// and outdoor Trash Containers (outside_Chest).
    /// Every check cycle:
    /// - Not looted: left untouched (no refill).
    /// - Looted: refilled with fresh random loot and restored to openable state.
    /// </summary>
    [DisallowMultipleComponent]
    public class ContainerRestockManager : NetworkBehaviour
    {
        public static ContainerRestockManager Instance { get; private set; }

        [Header("30-Second Checker Settings")]
        [SerializeField, Min(1f)] private float _checkIntervalSeconds = 30.0f;
        [SerializeField] private bool _restockInsideBoxes = true;
        [SerializeField] private bool _restockOutsideTrashCans = true;

        [Header("Live Checker Telemetry (Read-Only)")]
        [SerializeField] private float _timeUntilNextCheck = 30.0f;
        [SerializeField] private int _totalChecksRun;
        [SerializeField] private int _lastCheckedCount;
        [SerializeField] private int _lastRefilledCount;
        [SerializeField] private int _lastSkippedUnlootedCount;

        public float CheckIntervalSeconds => _checkIntervalSeconds;
        public float TimeUntilNextCheck => _timeUntilNextCheck;
        public int TotalChecksRun => _totalChecksRun;
        public int LastCheckedCount => _lastCheckedCount;
        public int LastRefilledCount => _lastRefilledCount;
        public int LastSkippedUnlootedCount => _lastSkippedUnlootedCount;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            _timeUntilNextCheck = Mathf.Max(1f, _checkIntervalSeconds);
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _timeUntilNextCheck = Mathf.Max(1f, _checkIntervalSeconds);
        }

        private void Update()
        {
            if (IsSpawned && !IsServer) return;

            _timeUntilNextCheck -= Time.deltaTime;
            if (_timeUntilNextCheck <= 0f)
            {
                float interval = Mathf.Max(1f, _checkIntervalSeconds);
                _timeUntilNextCheck = interval;
                CheckAndRestockNowAuthoritative();
            }
        }

        /// <summary>
        /// Immediately runs one checker sweep over all indoor boxes and outdoor trash cans.
        /// Returns the number of looted containers refilled.
        /// </summary>
        public int CheckAndRestockNowAuthoritative()
        {
            if (IsSpawned && !IsServer) return 0;

            var containers = FindObjectsByType<LootContainerInteractable>(FindObjectsSortMode.None);
            int checkedCount = 0;
            int refilledCount = 0;
            int skippedUnlooted = 0;

            for (int i = 0; i < containers.Length; i++)
            {
                var c = containers[i];
                if (c == null || !c.isActiveAndEnabled) continue;

                bool isEligibleType =
                    (_restockInsideBoxes && c.ContainerType == LootContainerType.InsideChest) ||
                    (_restockOutsideTrashCans && c.ContainerType == LootContainerType.OutsideChest);

                if (!isEligibleType) continue;

                checkedCount++;
                if (!c.HasBeenLooted)
                {
                    skippedUnlooted++;
                    continue;
                }

                if (c.TryRefillIfLootedAuthoritative())
                {
                    refilledCount++;
                }
            }

            _totalChecksRun++;
            _lastCheckedCount = checkedCount;
            _lastRefilledCount = refilledCount;
            _lastSkippedUnlootedCount = skippedUnlooted;

            return refilledCount;
        }
    }
}

