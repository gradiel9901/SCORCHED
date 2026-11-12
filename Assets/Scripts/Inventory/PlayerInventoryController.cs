using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Scorched.Player;
using Scorched.Survival;
using Scorched.Weapons;

namespace Scorched.Inventory
{
    /// <summary>
    /// Server-authoritative player inventory controller owning the 6x5 (30-slot) Cabal spatial grid.
    /// Syncs layout + stack counts to the owning client and drives crafting, 3-to-1 grade synthesis,
    /// consumable usage, ground dropping, and weapon reserve-ammo synchronization.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerInventoryController : NetworkBehaviour
    {
        public static PlayerInventoryController LocalInstance { get; private set; }

        [Header("Debug / Fallback Pickup Spawn")]
        [SerializeField] private float _dropForwardDistance = 1.25f;

        private readonly NetworkVariable<FixedString4096Bytes> _networkGridState = new NetworkVariable<FixedString4096Bytes>(
            new FixedString4096Bytes(),
            NetworkVariableReadPermission.Owner,
            NetworkVariableWritePermission.Server);

        private readonly CabalInventoryGrid _grid = new CabalInventoryGrid();

        private PlayerHealthController _healthController;
        private PlayerThermalController _thermalController;
        private PlayerMovement _playerMovement;
        private NetworkWeaponController _weaponController;
        private bool _suppressAmmoSync = false;

        public CabalInventoryGrid Grid => _grid;

        public event Action OnInventoryChanged;
        public event Action<string> OnInventoryNotice;

        private void Awake()
        {
            CacheComponents();
        }

        private void CacheComponents()
        {
            _healthController = GetComponent<PlayerHealthController>();
            _thermalController = GetComponent<PlayerThermalController>();
            _playerMovement = GetComponent<PlayerMovement>();
            _weaponController = GetComponent<NetworkWeaponController>();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            CacheComponents();

            if (IsOwner)
            {
                LocalInstance = this;
                _networkGridState.OnValueChanged += HandleGridStateChanged;
                ApplyNetworkGridString(_networkGridState.Value.ToString());
            }

            if (IsServer)
            {
                SyncGridToNetworkVar();
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                _networkGridState.OnValueChanged -= HandleGridStateChanged;
                if (LocalInstance == this)
                {
                    LocalInstance = null;
                }
            }
            base.OnNetworkDespawn();
        }

        private void Start()
        {
            if (!IsSpawned && LocalInstance == null)
            {
                LocalInstance = this;
            }
        }

        private void HandleGridStateChanged(FixedString4096Bytes previous, FixedString4096Bytes current)
        {
            if (IsServer) return;
            ApplyNetworkGridString(current.ToString());
        }

        private void ApplyNetworkGridString(string data)
        {
            _grid.DeserializeCompact(data);
            OnInventoryChanged?.Invoke();
        }

        private void SyncGridToNetworkVar()
        {
            if (!IsSpawned || IsServer)
            {
                if (!_suppressAmmoSync && _weaponController != null)
                {
                    int ammoTotal = _grid.CountTotalAmount(ItemId.Ammo_762);
                    _weaponController.SetReserveAmmoAuthoritative(ammoTotal);
                }

                if (IsSpawned && IsServer)
                {
                    string serialized = _grid.SerializeCompact();
                    if (serialized.Length > 4000)
                    {
                        Debug.LogWarning("[PlayerInventoryController] Grid serialization truncated near 4KB limit.");
                        serialized = serialized.Substring(0, 4000);
                    }
                    _networkGridState.Value = new FixedString4096Bytes(serialized);
                }
            }

            OnInventoryChanged?.Invoke();
        }

        public void RaiseNotice(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (!IsSpawned || IsOwner)
            {
                OnInventoryNotice?.Invoke(message);
                Scorched.UI.ScorchedHUDController.Instance?.ShowAlertBanner(message, 2.6f);
            }
            else if (IsServer)
            {
                NotifyOwnerClientRpc(message);
            }
        }

        [Rpc(SendTo.Owner)]
        private void NotifyOwnerClientRpc(string message)
        {
            OnInventoryNotice?.Invoke(message);
            Scorched.UI.ScorchedHUDController.Instance?.ShowAlertBanner(message, 2.6f);
        }

        #region Add / Consume / Query
        public bool TryAddItemLocal(ItemId id, int count = 1)
        {
            return TryAddItemAuthoritative(id, count);
        }

        public bool TryAddItemAuthoritative(ItemId id, int count = 1)
        {
            if (IsSpawned && !IsServer) return false;
            if (id == ItemId.None || count <= 0) return false;

            int remaining = count;
            if (id == ItemId.Weapon_AK47 && remaining > 0 && TryEquipAK47Authoritative())
            {
                remaining--;
                if (remaining <= 0) return true;
            }

            if (!_grid.TryAddItem(id, remaining))
            {
                RaiseNotice("INVENTORY FULL - REORGANIZE SLOTS!");
                return remaining < count;
            }

            SyncGridToNetworkVar();
            return true;
        }

        /// <summary>
        /// Snapshots every item currently in the player's backpack (including reserve ammo)
        /// and clears the grid cleanly on death so loot drops exclusively in their Death Bag.
        /// </summary>
        public List<(ItemId id, int count)> ExtractAllItemsAndClearOnDeathAuthoritative()
        {
            var extracted = new List<(ItemId id, int count)>(_grid.Items.Count);
            if (IsSpawned && !IsServer) return extracted;

            for (int i = 0; i < _grid.Items.Count; i++)
            {
                var inst = _grid.Items[i];
                if (inst == null || inst.Id == ItemId.None || inst.StackCount <= 0) continue;
                extracted.Add((inst.Id, inst.StackCount));
            }

            _suppressAmmoSync = true;
            _grid.Clear();
            SyncGridToNetworkVar();
            _suppressAmmoSync = false;
            return extracted;
        }

        public void ConsumeAmmoForReloadAuthoritative(int amount)
        {
            if (IsSpawned && !IsServer) return;
            if (amount <= 0) return;

            _suppressAmmoSync = true;
            int available = _grid.CountTotalAmount(ItemId.Ammo_762);
            int take = Mathf.Min(available, amount);
            if (take > 0)
            {
                _grid.ConsumeAmount(ItemId.Ammo_762, take);
                SyncGridToNetworkVar();
            }
            _suppressAmmoSync = false;
        }

        [Rpc(SendTo.Server)]
        public void RequestCollectWorldPickupServerRpc(ulong pickupNetworkObjectId)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(pickupNetworkObjectId, out NetworkObject obj))
            {
                return;
            }

            var pickup = obj.GetComponent<WorldItemPickup>();
            if (pickup == null || pickup.IsCollected) return;

            if (Vector3.Distance(transform.position, pickup.transform.position) > 5.0f) return;

            if (!pickup.TryConsumeOnServer(this))
            {
                RaiseNotice("INVENTORY FULL - REORGANIZE SLOTS!");
            }
        }
        #endregion

        #region Grid Move / Use / Drop
        public void RequestMoveItem(string guid, int targetX, int targetY)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (!IsSpawned || IsServer)
            {
                MoveItemAuthoritative(guid, targetX, targetY);
            }
            else
            {
                MoveItemServerRpc(guid, targetX, targetY);
            }
        }

        [Rpc(SendTo.Server)]
        private void MoveItemServerRpc(string guid, int targetX, int targetY)
        {
            MoveItemAuthoritative(guid, targetX, targetY);
        }

        private void MoveItemAuthoritative(string guid, int targetX, int targetY)
        {
            if (_grid.TryMoveItem(guid, targetX, targetY))
            {
                SyncGridToNetworkVar();
            }
        }

        public void RequestUseItem(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (!IsSpawned || IsServer)
            {
                UseItemAuthoritative(guid);
            }
            else
            {
                UseItemServerRpc(guid);
            }
        }

        [Rpc(SendTo.Server)]
        private void UseItemServerRpc(string guid)
        {
            UseItemAuthoritative(guid);
        }

        private void UseItemAuthoritative(string guid)
        {
            var inst = _grid.FindByGuid(guid);
            if (inst == null) return;

            if (_healthController != null && !_healthController.IsAlive) return;

            if (inst.Id == ItemId.Weapon_AK47)
            {
                if (TryEquipAK47Authoritative())
                {
                    _grid.RemoveInstance(inst);
                    SyncGridToNetworkVar();
                    RaiseNotice("AK-47 TACTICAL EQUIPPED!");
                }
                else
                {
                    RaiseNotice("AK-47 ALREADY EQUIPPED!");
                }
                return;
            }

            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(inst.Id) : null;
            if (def == null || !def.IsConsumable) return;

            switch (def.Category)
            {
                case ItemCategory.Water:
                    _thermalController?.RestoreHydrationAuthoritative(def.RestoreAmount);
                    break;
                case ItemCategory.Bandage:
                    _healthController?.RestoreHealthAuthoritative(def.RestoreAmount);
                    break;
                case ItemCategory.Food:
                    _playerMovement?.RestoreStaminaAuthoritative(def.RestoreAmount);
                    break;
                default:
                    return;
            }

            inst.StackCount -= 1;
            if (inst.StackCount <= 0)
            {
                _grid.RemoveInstance(inst);
            }
            SyncGridToNetworkVar();
        }

        public void RequestDropItem(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (!IsSpawned || IsServer)
            {
                DropItemAuthoritative(guid);
            }
            else
            {
                DropItemServerRpc(guid);
            }
        }

        [Rpc(SendTo.Server)]
        private void DropItemServerRpc(string guid)
        {
            DropItemAuthoritative(guid);
        }

        private void DropItemAuthoritative(string guid)
        {
            var inst = _grid.FindByGuid(guid);
            if (inst == null) return;

            ItemId dropId = inst.Id;
            int dropCount = inst.StackCount;
            _grid.RemoveInstance(inst);
            SyncGridToNetworkVar();

            Vector3 forward = _playerMovement != null && _playerMovement.CameraTransform != null
                ? Vector3.ProjectOnPlane(_playerMovement.CameraTransform.forward, Vector3.up).normalized
                : transform.forward;
            if (forward.sqrMagnitude < 0.01f) forward = transform.forward;

            Vector3 spawnPos = transform.position + forward * _dropForwardDistance + Vector3.up * 0.65f;
            if (Physics.Raycast(spawnPos + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 6f, ~0, QueryTriggerInteraction.Ignore))
            {
                spawnPos = hit.point + Vector3.up * 0.12f;
            }
            else if (Terrain.activeTerrain != null)
            {
                float ty = Terrain.activeTerrain.SampleHeight(spawnPos) + Terrain.activeTerrain.transform.position.y;
                spawnPos.y = ty + 0.15f;
            }

            SpawnWorldPickupAuthoritative(dropId, dropCount, spawnPos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
        }

        public static WorldItemPickup SpawnWorldPickupAuthoritative(ItemId id, int count, Vector3 pos, Quaternion rot)
        {
            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(id) : null;
            GameObject prefab = def != null ? def.WorldPickupPrefab : null;

            GameObject go;
            if (prefab != null)
            {
                go = Instantiate(prefab, pos, rot);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"Pickup_{id}";
                go.transform.SetPositionAndRotation(pos, rot);
                go.transform.localScale = Vector3.one * 0.35f;
                go.AddComponent<WorldItemPickup>();
            }

            var pickup = go.GetComponent<WorldItemPickup>() ?? go.AddComponent<WorldItemPickup>();
            pickup.InitializeItem(id, count);

            var netObj = go.GetComponent<NetworkObject>();
            if (netObj != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && !netObj.IsSpawned)
            {
                netObj.Spawn(true);
            }

            return pickup;
        }
        #endregion

        #region Crafting & Grade Synthesis
        public bool HasAllAK47Parts(out bool hasStock, out bool hasReceiver, out bool hasBarrel, out bool hasBolt, out int ammoCount)
        {
            hasStock = _grid.CountTotalAmount(ItemId.AK47_Stock) >= 1;
            hasReceiver = _grid.CountTotalAmount(ItemId.AK47_Receiver) >= 1;
            hasBarrel = _grid.CountTotalAmount(ItemId.AK47_Barrel) >= 1;
            hasBolt = _grid.CountTotalAmount(ItemId.AK47_Bolt) >= 1;
            ammoCount = _grid.CountTotalAmount(ItemId.Ammo_762);
            return hasStock && hasReceiver && hasBarrel && hasBolt && ammoCount >= 5;
        }

        public void RequestCraftAK47()
        {
            if (!IsSpawned || IsServer)
            {
                CraftAK47Authoritative();
            }
            else
            {
                CraftAK47ServerRpc();
            }
        }

        [Rpc(SendTo.Server)]
        private void CraftAK47ServerRpc()
        {
            CraftAK47Authoritative();
        }

        private void CraftAK47Authoritative()
        {
            if (_weaponController != null && _weaponController.IsWeaponEquipped)
            {
                RaiseNotice("AK-47 ALREADY ASSEMBLED!");
                return;
            }

            if (!HasAllAK47Parts(out _, out _, out _, out _, out int ammoTotal))
            {
                RaiseNotice("MISSING AK-47 PARTS OR < 5 AMMO!");
                return;
            }

            _grid.ConsumeAmount(ItemId.AK47_Stock, 1);
            _grid.ConsumeAmount(ItemId.AK47_Receiver, 1);
            _grid.ConsumeAmount(ItemId.AK47_Barrel, 1);
            _grid.ConsumeAmount(ItemId.AK47_Bolt, 1);

            TryEquipAK47Authoritative();
            RaiseNotice("AK-47 TACTICAL ASSEMBLED!");
        }

        private bool TryEquipAK47Authoritative()
        {
            if (_weaponController == null || _weaponController.IsWeaponEquipped) return false;

            int ammoTotal = _grid.CountTotalAmount(ItemId.Ammo_762);
            int magLoad = Mathf.Min(30, ammoTotal);
            if (magLoad > 0)
            {
                _grid.ConsumeAmount(ItemId.Ammo_762, magLoad);
            }

            int remainingReserve = _grid.CountTotalAmount(ItemId.Ammo_762);
            _suppressAmmoSync = true;
            _weaponController.EquipCraftedAK47Authoritative(magLoad + remainingReserve);
            SyncGridToNetworkVar();
            _suppressAmmoSync = false;
            return true;
        }

        public bool CanSynthesize(ItemId inputId, out ItemId upgradedId)
        {
            if (!ScorchedItemCatalog.CanSynthesizeUpgrade(inputId, out upgradedId))
            {
                return false;
            }
            return _grid.CountTotalAmount(inputId) >= 3;
        }

        public void RequestSynthesizeUpgrade(ItemId inputId)
        {
            if (inputId == ItemId.None) return;
            if (!IsSpawned || IsServer)
            {
                SynthesizeUpgradeAuthoritative(inputId);
            }
            else
            {
                SynthesizeUpgradeServerRpc((ushort)inputId);
            }
        }

        [Rpc(SendTo.Server)]
        private void SynthesizeUpgradeServerRpc(ushort rawId)
        {
            SynthesizeUpgradeAuthoritative((ItemId)rawId);
        }

        private void SynthesizeUpgradeAuthoritative(ItemId inputId)
        {
            if (!CanSynthesize(inputId, out ItemId upgradedId))
            {
                RaiseNotice("NEED 3 IDENTICAL ITEMS TO UPGRADE!");
                return;
            }

            // Snapshot compact state in case upgraded 1x2 / 2x2 doesn't fit cleanly
            string rollbackSnapshot = _grid.SerializeCompact();
            _grid.ConsumeAmount(inputId, 3);

            if (!_grid.TryAddItem(upgradedId, 1))
            {
                _grid.DeserializeCompact(rollbackSnapshot);
                SyncGridToNetworkVar();
                RaiseNotice("NOT ENOUGH SPACE FOR UPGRADED ITEM!");
                return;
            }

            SyncGridToNetworkVar();
            var upgradedDef = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(upgradedId) : null;
            string label = upgradedDef != null ? upgradedDef.DisplayName.ToUpperInvariant() : upgradedId.ToString().ToUpperInvariant();
            RaiseNotice($"UPGRADED: {label}");
        }

        public void RequestOpenContainer(LootContainerInteractable container)
        {
            if (container == null) return;
            if (!IsSpawned || IsServer)
            {
                container.TryExecuteOpenFromPlayerRpc(this);
                return;
            }

            if (container.NetworkObject != null && container.IsSpawned)
            {
                RequestOpenContainerServerRpc(container.NetworkObject);
            }
        }

        [Rpc(SendTo.Server)]
        private void RequestOpenContainerServerRpc(NetworkObjectReference containerRef)
        {
            if (containerRef.TryGet(out NetworkObject netObj) && netObj != null)
            {
                netObj.GetComponent<LootContainerInteractable>()?.TryExecuteOpenFromPlayerRpc(this);
            }
        }

        public void RequestTakeContainerItem(LootContainerInteractable container, string entryGuid)
        {
            if (container == null || string.IsNullOrEmpty(entryGuid)) return;
            if (!IsSpawned || IsServer)
            {
                container.TryExecuteTakeItemFromPlayerRpc(this, entryGuid);
                return;
            }

            if (container.NetworkObject != null && container.IsSpawned)
            {
                RequestTakeContainerItemServerRpc(container.NetworkObject, entryGuid);
            }
        }

        [Rpc(SendTo.Server)]
        private void RequestTakeContainerItemServerRpc(NetworkObjectReference containerRef, string entryGuid)
        {
            if (containerRef.TryGet(out NetworkObject netObj) && netObj != null)
            {
                netObj.GetComponent<LootContainerInteractable>()?.TryExecuteTakeItemFromPlayerRpc(this, entryGuid);
            }
        }

        public void RequestLootAllContainer(LootContainerInteractable container)
        {
            if (container == null) return;
            if (!IsSpawned || IsServer)
            {
                container.TryExecuteLootAllFromPlayerRpc(this);
                return;
            }

            if (container.NetworkObject != null && container.IsSpawned)
            {
                RequestLootAllContainerServerRpc(container.NetworkObject);
            }
        }

        [Rpc(SendTo.Server)]
        private void RequestLootAllContainerServerRpc(NetworkObjectReference containerRef)
        {
            if (containerRef.TryGet(out NetworkObject netObj) && netObj != null)
            {
                netObj.GetComponent<LootContainerInteractable>()?.TryExecuteLootAllFromPlayerRpc(this);
            }
        }
        #endregion
    }
}
