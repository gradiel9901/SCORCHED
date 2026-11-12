using Unity.Netcode;
using UnityEngine;

namespace Scorched.Inventory
{
    /// <summary>
    /// Networked 3D world item pickup dropped from full containers or discarded from a player's inventory.
    /// Players look at it within 3.0m and press E to collect it into their 30-slot spatial grid.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Collider))]
    public class WorldItemPickup : NetworkBehaviour
    {
        [Header("Item Identity")]
        [SerializeField] private ItemId _itemId = ItemId.None;
        [SerializeField] private int _stackCount = 1;

        public readonly NetworkVariable<ushort> NetworkItemId = new NetworkVariable<ushort>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<int> NetworkStackCount = new NetworkVariable<int>(
            1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> NetworkCollected = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private bool _localCollected = false;

        public ItemId EffectiveItemId => IsSpawned && NetworkItemId.Value != 0 ? (ItemId)NetworkItemId.Value : _itemId;
        public int EffectiveStackCount => IsSpawned ? Mathf.Max(1, NetworkStackCount.Value) : Mathf.Max(1, _stackCount);
        public bool IsCollected => IsSpawned ? NetworkCollected.Value : _localCollected;

        public void InitializeItem(ItemId id, int count)
        {
            _itemId = id;
            _stackCount = Mathf.Max(1, count);
            if (IsSpawned && IsServer)
            {
                NetworkItemId.Value = (ushort)id;
                NetworkStackCount.Value = _stackCount;
                NetworkCollected.Value = false;
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            if (IsServer)
            {
                if (NetworkItemId.Value == 0 && _itemId != ItemId.None)
                {
                    NetworkItemId.Value = (ushort)_itemId;
                }
                if (NetworkStackCount.Value <= 0)
                {
                    NetworkStackCount.Value = Mathf.Max(1, _stackCount);
                }
            }
        }

        public string GetPromptLabel()
        {
            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(EffectiveItemId) : null;
            string name = def != null ? def.DisplayName : EffectiveItemId.ToString();
            return EffectiveStackCount > 1
                ? $"[E] Pick up {name} x{EffectiveStackCount}"
                : $"[E] Pick up {name}";
        }

        public void RequestPickup(PlayerInventoryController playerInventory)
        {
            if (playerInventory == null || IsCollected) return;

            if (!IsSpawned)
            {
                if (playerInventory.TryAddItemLocal(EffectiveItemId, EffectiveStackCount))
                {
                    _localCollected = true;
                    gameObject.SetActive(false);
                    Destroy(gameObject, 0.25f);
                }
                return;
            }

            if (NetworkObject != null)
            {
                playerInventory.RequestCollectWorldPickupServerRpc(NetworkObject.NetworkObjectId);
            }
        }

        public bool TryConsumeOnServer(PlayerInventoryController playerInventory)
        {
            if (IsSpawned && !IsServer) return false;
            if (IsCollected || playerInventory == null) return false;

            if (playerInventory.TryAddItemAuthoritative(EffectiveItemId, EffectiveStackCount))
            {
                _localCollected = true;
                if (IsSpawned)
                {
                    NetworkCollected.Value = true;
                    if (NetworkObject != null && NetworkObject.IsSpawned)
                    {
                        NetworkObject.Despawn(true);
                    }
                }
                else
                {
                    Destroy(gameObject);
                }
                return true;
            }
            return false;
        }
    }
}

