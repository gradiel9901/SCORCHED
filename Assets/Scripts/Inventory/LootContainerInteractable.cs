using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Scorched.Inventory
{
    public enum LootContainerType : byte
    {
        InsideChest = 0,
        OutsideChest = 1,
        DropSupplyBox = 2,
        DeathBag = 3
    }

    [Serializable]
    public struct ContainerLootEntry
    {
        public string EntryGuid;
        public ItemId Id;
        public int Count;
    }

    /// <summary>
    /// Networked scavengeable loot container (`Inside_Chest`, `outside_Chest`, `Drop Supply Box`).
    /// Press E within 3.5m to open the container loot window (server-rolled loot replicated to clients).
    /// Supports right-clicking individual items to take them, Loot All, and sky-drop descent.
    /// </summary>
    [DisallowMultipleComponent]
    public class LootContainerInteractable : NetworkBehaviour
    {
        [Header("Container Configuration")]
        [SerializeField] private LootContainerType _containerType = LootContainerType.InsideChest;
        [SerializeField] private float _scavengeHoldDuration = 0f;

        [Header("Sky Drop Descent (DropSupplyBox Only)")]
        [SerializeField] private bool _startsDescending = false;
        [SerializeField] private float _descentSpeed = 8.0f;
        [SerializeField] private float _targetGroundY = 0f;
        public float TargetGroundY => _targetGroundY;

        public readonly NetworkVariable<bool> NetworkIsOpened = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> NetworkHasLanded = new NetworkVariable<bool>(
            true,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<bool> NetworkLootRolled = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public readonly NetworkVariable<FixedString4096Bytes> NetworkLootState = new NetworkVariable<FixedString4096Bytes>(
            new FixedString4096Bytes(),
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly List<ContainerLootEntry> _lootItems = new List<ContainerLootEntry>(8);

        private bool _localOpened = false;
        private bool _localLootRolled = false;
        private bool _hasItemsTaken = false;
        private int _rollGeneration = 0;
        private bool _descendingActive = false;
        private ParticleSystem _smokePlume;
        private GameObject _beaconColumn;
        private Collider[] _colliders;
        private readonly Dictionary<Material, Color> _originalBaseColors = new Dictionary<Material, Color>();

        public LootContainerType ContainerType => _containerType;
        public float ScavengeHoldDuration => _scavengeHoldDuration;
        public bool IsOpened => IsSpawned ? NetworkIsOpened.Value : _localOpened;
        public bool HasLanded => IsSpawned ? NetworkHasLanded.Value : !_descendingActive;
        public bool HasRolledLoot => IsSpawned ? NetworkLootRolled.Value : _localLootRolled;
        public bool HasBeenLooted => IsOpened || _hasItemsTaken;
        public bool CanScavenge => !IsOpened && HasLanded;
        public IReadOnlyList<ContainerLootEntry> LootItems => _lootItems;

        public event Action OnLootChanged;

        public void ConfigureType(LootContainerType containerType)
        {
            _containerType = containerType;
        }

        public void BeginSkyDropAuthoritative(float groundY, float dropHeightAboveGround = 70f, float descentSpeed = 8f)
        {
            _containerType = LootContainerType.DropSupplyBox;
            _descentSpeed = Mathf.Max(1f, descentSpeed);
            _targetGroundY = groundY + ComputeBottomPivotOffset();
            _startsDescending = true;
            _descendingActive = true;

            Vector3 pos = transform.position;
            pos.y = _targetGroundY + Mathf.Max(5f, dropHeightAboveGround);
            transform.position = pos;

            if (IsSpawned && IsServer)
            {
                NetworkHasLanded.Value = false;
                NetworkIsOpened.Value = false;
            }

            EnsureSupplyDropVisuals();
        }

        private float ComputeBottomPivotOffset()
        {
            if (_colliders == null) _colliders = GetComponentsInChildren<Collider>();
            if (_colliders == null || _colliders.Length == 0) return 0.7f;

            Bounds combined = new Bounds(transform.position, Vector3.zero);
            bool any = false;
            for (int i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] == null || _colliders[i].isTrigger) continue;
                if (!any) { combined = _colliders[i].bounds; any = true; }
                else combined.Encapsulate(_colliders[i].bounds);
            }
            if (!any) return 0.7f;
            float pivotMinusBottom = transform.position.y - combined.min.y;
            return Mathf.Max(0f, pivotMinusBottom);
        }

        private void Awake()
        {
            _colliders = GetComponentsInChildren<Collider>();
            if (_containerType == LootContainerType.DropSupplyBox && _startsDescending)
            {
                _descendingActive = true;
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();

            if (IsServer)
            {
                if (_startsDescending)
                {
                    NetworkHasLanded.Value = false;
                    _descendingActive = true;
                }
                else
                {
                    NetworkHasLanded.Value = true;
                    _descendingActive = false;
                }

                if (_localLootRolled)
                {
                    NetworkLootRolled.Value = true;
                    NetworkIsOpened.Value = _localOpened;
                    SyncLootStateAuthoritative();
                }
            }

            NetworkIsOpened.OnValueChanged += HandleOpenedStateChanged;
            NetworkHasLanded.OnValueChanged += HandleLandedStateChanged;
            NetworkLootRolled.OnValueChanged += HandleLootRolledChanged;
            NetworkLootState.OnValueChanged += HandleLootStateChanged;

            if (!IsServer && NetworkLootRolled.Value)
            {
                ApplySerializedLootState(NetworkLootState.Value.ToString());
            }

            ApplyVisualState();
            if (_containerType == LootContainerType.DropSupplyBox)
            {
                EnsureSupplyDropVisuals();
            }
        }

        public override void OnNetworkDespawn()
        {
            NetworkIsOpened.OnValueChanged -= HandleOpenedStateChanged;
            NetworkHasLanded.OnValueChanged -= HandleLandedStateChanged;
            NetworkLootRolled.OnValueChanged -= HandleLootRolledChanged;
            NetworkLootState.OnValueChanged -= HandleLootStateChanged;
            base.OnNetworkDespawn();
        }

        private void Start()
        {
            if (!IsSpawned && _containerType == LootContainerType.DropSupplyBox && _startsDescending)
            {
                _descendingActive = true;
                EnsureSupplyDropVisuals();
            }
        }

        private void Update()
        {
            if (_containerType == LootContainerType.DropSupplyBox && _descendingActive)
            {
                if (!IsSpawned || IsServer)
                {
                    Vector3 p = transform.position;
                    p.y = Mathf.MoveTowards(p.y, _targetGroundY, _descentSpeed * Time.deltaTime);
                    transform.position = p;

                    if (p.y <= _targetGroundY + 0.01f)
                    {
                        p.y = _targetGroundY;
                        transform.position = p;
                        _descendingActive = false;
                        if (IsSpawned && IsServer)
                        {
                            NetworkHasLanded.Value = true;
                        }
                        OnLandedEffect();
                    }
                }
            }
        }

        private void HandleOpenedStateChanged(bool previous, bool current)
        {
            ApplyVisualState();
        }

        private void HandleLandedStateChanged(bool previous, bool current)
        {
            if (!previous && current)
            {
                _descendingActive = false;
                OnLandedEffect();
            }
        }

        private void OnLandedEffect()
        {
            if (_beaconColumn != null)
            {
                var r = _beaconColumn.GetComponent<Renderer>();
                if (r != null && r.material != null)
                {
                    Color c = r.material.color;
                    c.a = 0.14f;
                    r.material.color = c;
                }
            }
        }

        private void ApplyVisualState()
        {
            if (IsOpened)
            {
                if (_smokePlume != null) _smokePlume.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                if (_beaconColumn != null) _beaconColumn.SetActive(false);
            }

            // Subtle dim tint on opened/looted renderers; restore cached original color when refilled
            var renderers = GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || renderers[i] is ParticleSystemRenderer) continue;
                foreach (var mat in renderers[i].materials)
                {
                    if (mat == null || !mat.HasProperty("_BaseColor")) continue;
                    if (!_originalBaseColors.TryGetValue(mat, out Color originalColor))
                    {
                        originalColor = mat.GetColor("_BaseColor");
                        _originalBaseColors[mat] = originalColor;
                    }

                    mat.SetColor("_BaseColor", IsOpened ? originalColor * 0.55f : originalColor);
                }
            }
        }

        private void HandleLootRolledChanged(bool previous, bool current)
        {
            if (current && !IsServer)
            {
                ApplySerializedLootState(NetworkLootState.Value.ToString());
            }
            OnLootChanged?.Invoke();
        }

        private void HandleLootStateChanged(FixedString4096Bytes previous, FixedString4096Bytes current)
        {
            if (IsServer) return;
            ApplySerializedLootState(current.ToString());
            OnLootChanged?.Invoke();
        }

        public string GetContainerTitle()
        {
            return _containerType switch
            {
                LootContainerType.DropSupplyBox => "SUPPLY CRATE",
                LootContainerType.OutsideChest => "TRASH CONTAINER",
                LootContainerType.DeathBag => "DEATH BAG",
                _ => "SUPPLY BOX"
            };
        }

        public string GetPromptLabel()
        {
            if (IsOpened) return "LOOTED (EMPTY)";
            if (!HasLanded) return "SUPPLY DROP DESCENDING...";
            string title = _containerType switch
            {
                LootContainerType.DropSupplyBox => "Supply Crate",
                LootContainerType.OutsideChest => "Trash Container",
                LootContainerType.DeathBag => "Death Bag",
                _ => "Supply Box"
            };
            return $"[E] Open {title}";
        }

        public void RequestOpen(PlayerInventoryController playerInventory)
        {
            if (!CanScavenge || playerInventory == null) return;

            if (!IsSpawned)
            {
                TryExecuteOpenFromPlayerRpc(playerInventory);
                return;
            }

            if (playerInventory.IsSpawned)
            {
                playerInventory.RequestOpenContainer(this);
                return;
            }

            if (NetworkObject != null)
            {
                ScavengeContainerServerRpc(playerInventory.OwnerClientId);
            }
        }

        public void RequestScavenge(PlayerInventoryController playerInventory)
        {
            RequestOpen(playerInventory);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void ScavengeContainerServerRpc(ulong requesterClientId)
        {
            if (!CanScavenge) return;

            PlayerInventoryController inv = null;
            if (NetworkManager.Singleton != null &&
                NetworkManager.Singleton.ConnectedClients.TryGetValue(requesterClientId, out var client) &&
                client.PlayerObject != null)
            {
                inv = client.PlayerObject.GetComponent<PlayerInventoryController>();
            }
            if (inv == null) return;

            TryExecuteOpenFromPlayerRpc(inv);
        }

        internal void TryExecuteOpenFromPlayerRpc(PlayerInventoryController inv)
        {
            if (!CanScavenge || inv == null) return;
            if (!IsWithinReach(inv.transform.position, 4.75f)) return;

            EnsureLootRolledAuthoritative();
            if (_lootItems.Count == 0)
            {
                MarkContainerEmptyAuthoritative();
            }
        }

        internal void TryExecuteTakeItemFromPlayerRpc(PlayerInventoryController inv, string entryGuid)
        {
            if (!HasLanded || inv == null || string.IsNullOrEmpty(entryGuid)) return;
            if (!IsWithinReach(inv.transform.position, 4.75f)) return;

            EnsureLootRolledAuthoritative();

            int foundIdx = -1;
            for (int i = 0; i < _lootItems.Count; i++)
            {
                if (_lootItems[i].EntryGuid == entryGuid)
                {
                    foundIdx = i;
                    break;
                }
            }
            if (foundIdx < 0) return;

            var entry = _lootItems[foundIdx];
            if (!inv.TryAddItemAuthoritative(entry.Id, entry.Count))
            {
                return;
            }

            _hasItemsTaken = true;
            _lootItems.RemoveAt(foundIdx);
            SyncLootStateAuthoritative();

            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(entry.Id) : null;
            string itemName = def != null ? def.DisplayName.ToUpperInvariant() : entry.Id.ToString().ToUpperInvariant();
            inv.RaiseNotice(entry.Count > 1 ? $"LOOTED: {itemName} x{entry.Count}" : $"LOOTED: {itemName}");

            if (_lootItems.Count == 0)
            {
                MarkContainerEmptyAuthoritative();
            }
        }

        internal void TryExecuteLootAllFromPlayerRpc(PlayerInventoryController inv)
        {
            if (!HasLanded || inv == null) return;
            if (!IsWithinReach(inv.transform.position, 4.75f)) return;

            EnsureLootRolledAuthoritative();
            if (_lootItems.Count == 0)
            {
                MarkContainerEmptyAuthoritative();
                return;
            }

            int taken = 0;
            // Two passes: take non-weapon items first so looted 7.62mm ammo loads right into an equipped AK-47
            for (int pass = 0; pass < 2; pass++)
            {
                bool takeWeaponsPass = (pass == 1);
                for (int i = 0; i < _lootItems.Count;)
                {
                    var entry = _lootItems[i];
                    bool isWeapon = entry.Id == ItemId.Weapon_AK47;
                    if (isWeapon != takeWeaponsPass)
                    {
                        i++;
                        continue;
                    }

                    if (inv.TryAddItemAuthoritative(entry.Id, entry.Count))
                    {
                        _lootItems.RemoveAt(i);
                        taken++;
                    }
                    else
                    {
                        i++;
                    }
                }
            }

            SyncLootStateAuthoritative();

            if (taken > 0)
            {
                _hasItemsTaken = true;
                inv.RaiseNotice(_lootItems.Count == 0
                    ? $"LOOTED {taken} ITEM{(taken > 1 ? "S" : "")}"
                    : $"LOOTED {taken} ITEM{(taken > 1 ? "S" : "")} (BACKPACK FULL)");
            }

            if (_lootItems.Count == 0)
            {
                MarkContainerEmptyAuthoritative();
            }
        }

        public bool IsWithinReach(Vector3 playerWorldPos, float maxSurfaceDistance = 4.75f)
        {
            if (_colliders == null || _colliders.Length == 0)
            {
                _colliders = GetComponentsInChildren<Collider>(true);
            }

            float minDist = float.MaxValue;
            if (_colliders != null)
            {
                for (int i = 0; i < _colliders.Length; i++)
                {
                    var col = _colliders[i];
                    if (col == null || !col.enabled || col.isTrigger) continue;
                    if (col is MeshCollider mc && mc.sharedMesh == null) continue;

                    Vector3 closest = col.ClosestPoint(playerWorldPos);
                    float d = Vector3.Distance(closest, playerWorldPos);
                    if (d < minDist) minDist = d;
                }
            }

            if (minDist < float.MaxValue)
            {
                return minDist <= maxSurfaceDistance;
            }

            return Vector3.Distance(playerWorldPos, transform.position) <= maxSurfaceDistance + 2.5f;
        }

        private void EnsureLootRolledAuthoritative()
        {
            if (HasRolledLoot) return;
            if (_containerType == LootContainerType.DeathBag)
            {
                _localLootRolled = true;
                if (IsSpawned && IsServer)
                {
                    NetworkLootRolled.Value = true;
                }
                SyncLootStateAuthoritative();
                return;
            }
            RollNewLootAuthoritative();
        }

        /// <summary>
        /// Populates a container (such as a spawned Death Bag) with an explicit list of items
        /// instead of rolling the random loot table.
        /// </summary>
        public void SetCustomLootAuthoritative(List<(ItemId id, int count)> items)
        {
            if (IsSpawned && !IsServer) return;

            _rollGeneration++;
            _hasItemsTaken = false;
            _localOpened = false;
            _lootItems.Clear();

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].id == ItemId.None || items[i].count <= 0) continue;
                    _lootItems.Add(new ContainerLootEntry
                    {
                        EntryGuid = $"{_rollGeneration}_{i}_{((ushort)items[i].id)}_{UnityEngine.Random.Range(1000, 9999)}",
                        Id = items[i].id,
                        Count = items[i].count
                    });
                }
            }

            _localLootRolled = true;
            if (IsSpawned && IsServer)
            {
                NetworkIsOpened.Value = false;
                NetworkLootRolled.Value = true;
            }
            SyncLootStateAuthoritative();

            if (_lootItems.Count == 0)
            {
                MarkContainerEmptyAuthoritative();
            }
            else
            {
                ApplyVisualState();
            }
        }

        /// <summary>
        /// Refills this container with fresh random loot only if it has been looted.
        /// Containers that were not looted are left untouched.
        /// </summary>
        public bool TryRefillIfLootedAuthoritative()
        {
            if (IsSpawned && !IsServer) return false;
            if (!HasLanded || !HasBeenLooted) return false;

            _hasItemsTaken = false;
            _localOpened = false;
            if (IsSpawned && IsServer)
            {
                NetworkIsOpened.Value = false;
            }

            RollNewLootAuthoritative();
            ApplyVisualState();
            return true;
        }

        private void RollNewLootAuthoritative()
        {
            _rollGeneration++;
            _lootItems.Clear();
            List<(ItemId id, int count)> rolled = RollLootTable(_containerType);
            for (int i = 0; i < rolled.Count; i++)
            {
                _lootItems.Add(new ContainerLootEntry
                {
                    EntryGuid = $"{_rollGeneration}_{i}_{((ushort)rolled[i].id)}_{UnityEngine.Random.Range(1000, 9999)}",
                    Id = rolled[i].id,
                    Count = Mathf.Max(1, rolled[i].count)
                });
            }

            _localLootRolled = true;
            if (IsSpawned && IsServer)
            {
                NetworkLootRolled.Value = true;
            }
            SyncLootStateAuthoritative();
        }

        private void MarkContainerEmptyAuthoritative()
        {
            _localOpened = true;
            if (IsSpawned && IsServer)
            {
                NetworkIsOpened.Value = true;
            }
            ApplyVisualState();
        }

        private void SyncLootStateAuthoritative()
        {
            if (IsSpawned && IsServer)
            {
                string packed = SerializeLootState(_lootItems);
                if (packed.Length > 4000) packed = packed.Substring(0, 4000);
                NetworkLootState.Value = new FixedString4096Bytes(packed);
            }
            OnLootChanged?.Invoke();
        }

        private static string SerializeLootState(List<ContainerLootEntry> items)
        {
            if (items == null || items.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder(128);
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(items[i].EntryGuid);
                sb.Append(':');
                sb.Append((ushort)items[i].Id);
                sb.Append(':');
                sb.Append(items[i].Count);
            }
            return sb.ToString();
        }

        private void ApplySerializedLootState(string packed)
        {
            _lootItems.Clear();
            if (string.IsNullOrEmpty(packed)) return;

            string[] tokens = packed.Split(';');
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                if (string.IsNullOrEmpty(t)) continue;
                string[] parts = t.Split(':');
                if (parts.Length < 3) continue;
                if (ushort.TryParse(parts[1], out ushort rawId) && int.TryParse(parts[2], out int cnt))
                {
                    _lootItems.Add(new ContainerLootEntry
                    {
                        EntryGuid = parts[0],
                        Id = (ItemId)rawId,
                        Count = Mathf.Max(1, cnt)
                    });
                }
            }
        }

        private static List<(ItemId id, int count)> RollLootTable(LootContainerType type)
        {
            var results = new List<(ItemId, int)>(6);

            if (type == LootContainerType.DropSupplyBox)
            {
                // Guaranteed 2-3 Good/Great consumables
                int consumableCount = UnityEngine.Random.Range(2, 4);
                for (int i = 0; i < consumableCount; i++)
                {
                    bool great = UnityEngine.Random.value < 0.35f;
                    results.Add((PickRandomConsumable(great ? ItemGrade.Great : ItemGrade.Good), 1));
                }

                // 75% chance for an AK-47 Gun Part
                if (UnityEngine.Random.value < 0.75f)
                {
                    results.Add((PickRandomAK47Part(), 1));
                }

                // 15-30 rounds of 7.62 Ammo
                results.Add((ItemId.Ammo_762, UnityEngine.Random.Range(15, 31)));
                return results;
            }

            // Inside_Chest & outside_Chest:
            // 50% chance for a Poor consumable (rare 15% Good grade), 30% chance for an AK-47 Part & 5-20 rounds of Ammo
            if (UnityEngine.Random.value < 0.50f)
            {
                bool rareGood = UnityEngine.Random.value < 0.15f;
                results.Add((PickRandomConsumable(rareGood ? ItemGrade.Good : ItemGrade.Poor), 1));
            }

            if (UnityEngine.Random.value < 0.30f)
            {
                results.Add((PickRandomAK47Part(), 1));
                results.Add((ItemId.Ammo_762, UnityEngine.Random.Range(5, 21)));
            }

            // Prevent completely empty chests: small salvage roll if neither primary bucket triggered
            if (results.Count == 0)
            {
                if (UnityEngine.Random.value < 0.55f)
                {
                    results.Add((PickRandomConsumable(ItemGrade.Poor), 1));
                }
                else
                {
                    results.Add((ItemId.Ammo_762, UnityEngine.Random.Range(5, 11)));
                }
            }

            return results;
        }

        private static ItemId PickRandomConsumable(ItemGrade grade)
        {
            int cat = UnityEngine.Random.Range(0, 3);
            return cat switch
            {
                0 => grade == ItemGrade.Great ? ItemId.Water_Great : (grade == ItemGrade.Good ? ItemId.Water_Good : ItemId.Water_Poor),
                1 => grade == ItemGrade.Great ? ItemId.Bandage_Great : (grade == ItemGrade.Good ? ItemId.Bandage_Good : ItemId.Bandage_Poor),
                _ => grade == ItemGrade.Great ? ItemId.Food_Great : (grade == ItemGrade.Good ? ItemId.Food_Good : ItemId.Food_Poor)
            };
        }

        private static ItemId PickRandomAK47Part()
        {
            return UnityEngine.Random.Range(0, 4) switch
            {
                0 => ItemId.AK47_Receiver,
                1 => ItemId.AK47_Barrel,
                2 => ItemId.AK47_Stock,
                _ => ItemId.AK47_Bolt
            };
        }

        private void SpawnOverflowPickup(ItemId id, int count, int index)
        {
            float angle = (index * 72f + UnityEngine.Random.Range(-15f, 15f)) * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.95f;
            Vector3 pos = transform.position + offset + Vector3.up * 0.5f;
            if (Physics.Raycast(pos + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
            {
                pos = hit.point + Vector3.up * 0.1f;
            }
            PlayerInventoryController.SpawnWorldPickupAuthoritative(id, count, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
        }

        #region Incineration into Ashes
        public bool IsIncinerated { get; private set; }

        public void IncinerateIntoAshesAuthoritative()
        {
            if (IsIncinerated) return;
            IsIncinerated = true;
            _hasItemsTaken = true;
            _localOpened = true;
            _lootItems.Clear();

            if (IsSpawned && IsServer)
            {
                NetworkIsOpened.Value = true;
                IncinerateIntoAshesClientRpc();
            }
            else
            {
                StartCoroutine(IncinerateAshesRoutine());
            }
        }

        [Rpc(SendTo.Everyone)]
        private void IncinerateIntoAshesClientRpc()
        {
            StartCoroutine(IncinerateAshesRoutine());
        }

        private System.Collections.IEnumerator IncinerateAshesRoutine()
        {
            IsIncinerated = true;
            _lootItems.Clear();

            // 1. Disable colliders so it cannot be looted anymore
            if (_colliders != null)
            {
                for (int i = 0; i < _colliders.Length; i++)
                {
                    if (_colliders[i] != null) _colliders[i].enabled = false;
                }
            }

            // 2. Hide drop beacon and smoke if active
            if (_smokePlume != null) _smokePlume.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (_beaconColumn != null) _beaconColumn.SetActive(false);

            // 3. Spawn ash burst VFX
            SpawnContainerAshBurst();

            // 4. Dissolve container mesh into ash over 2 seconds
            Shader ashShader = Shader.Find("Scorched/VFX/AshDisintegration");
            Texture2D noiseTex = null;
#if UNITY_EDITOR
            noiseTex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/VFX/T_VFX_AshDissolveNoise.png");
#endif
            var renderers = GetComponentsInChildren<Renderer>(true);
            MaterialPropertyBlock propBlock = new MaterialPropertyBlock();

            if (ashShader != null)
            {
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null || renderers[i] is ParticleSystemRenderer) continue;
                    var mats = renderers[i].materials;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        if (mats[m] != null)
                        {
                            Texture baseTex = mats[m].mainTexture;
                            mats[m].shader = ashShader;
                            if (baseTex != null) mats[m].mainTexture = baseTex;
                            if (noiseTex != null) mats[m].SetTexture("_DissolveNoise", noiseTex);
                            mats[m].SetColor("_EmberColor", new Color(4.0f, 1.4f, 0.2f, 1.0f));
                            mats[m].SetFloat("_EmberWidth", 0.08f);
                            mats[m].SetColor("_AshColor", new Color(0.08f, 0.07f, 0.06f, 1.0f));
                            mats[m].SetFloat("_AshWidth", 0.18f);
                            mats[m].SetFloat("_HeightMin", transform.position.y);
                            mats[m].SetFloat("_HeightMax", transform.position.y + 2.0f);
                        }
                    }
                    renderers[i].materials = mats;
                }
            }

            float elapsed = 0f;
            float duration = 2.0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float dissolve = Mathf.Clamp01(elapsed / duration);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null || renderers[i] is ParticleSystemRenderer) continue;
                    renderers[i].GetPropertyBlock(propBlock);
                    propBlock.SetFloat("_DissolveAmount", dissolve);
                    renderers[i].SetPropertyBlock(propBlock);
                }
                yield return null;
            }

            // 5. Hide container completely
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null && !(renderers[i] is ParticleSystemRenderer))
                {
                    renderers[i].enabled = false;
                }
            }
        }

        private void SpawnContainerAshBurst()
        {
            var burstGo = new GameObject("ContainerAshBurst");
            burstGo.transform.position = transform.position + Vector3.up * 0.5f;
            burstGo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            var ps = burstGo.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.duration = 1.5f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.6f, 1.4f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.12f, 0.10f, 0.09f, 0.85f), new Color(0.95f, 0.40f, 0.08f, 0.65f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 48;

            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 32) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.5f;

            var colLife = ps.colorOverLifetime;
            colLife.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(0.95f, 0.45f, 0.1f), 0f), new GradientColorKey(new Color(0.15f, 0.14f, 0.12f), 0.3f), new GradientColorKey(Color.black, 1f) },
                new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.7f, 0.2f), new GradientAlphaKey(0f, 1f) }
            );
            colLife.color = new ParticleSystem.MinMaxGradient(grad);

            var rend = burstGo.GetComponent<ParticleSystemRenderer>();
            if (rend != null)
            {
#if UNITY_EDITOR
                var smokeMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/VFX/M_SupplyDropSmoke.mat");
                if (smokeMat != null) rend.sharedMaterial = smokeMat;
#endif
            }

            ps.Play();
            Destroy(burstGo, 3.5f);
        }
        #endregion

        #region Custom Supply Drop Visuals & Signal Beacon
        private static Material s_supplySmokeMat;
        private static Material s_supplyEmbersMat;
        private static Material s_supplyBeaconMat;

        private static void LoadSupplyVfxMaterials()
        {
            if (s_supplySmokeMat == null)
            {
                s_supplySmokeMat = Resources.Load<Material>("VFX/M_SupplyDropSmoke");
                s_supplyEmbersMat = Resources.Load<Material>("VFX/M_SupplyDropEmbers");
                s_supplyBeaconMat = Resources.Load<Material>("VFX/M_SupplyBeacon");
#if UNITY_EDITOR
                if (s_supplySmokeMat == null) s_supplySmokeMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/VFX/M_SupplyDropSmoke.mat");
                if (s_supplyEmbersMat == null) s_supplyEmbersMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/VFX/M_SupplyDropEmbers.mat");
                if (s_supplyBeaconMat == null) s_supplyBeaconMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/VFX/M_SupplyBeacon.mat");
#endif
            }
        }

        private void EnsureSupplyDropVisuals()
        {
            LoadSupplyVfxMaterials();

            if (_smokePlume == null)
            {
                var smokeGo = new GameObject("SupplyDropSmoke");
                smokeGo.transform.SetParent(transform, false);
                smokeGo.transform.localPosition = new Vector3(0f, 0.85f, 0f);

                _smokePlume = smokeGo.AddComponent<ParticleSystem>();
                var main = _smokePlume.main;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(2.8f, 4.2f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.8f, 3.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.75f, 1.8f);
                main.startRotation = new ParticleSystem.MinMaxCurve(-180f * Mathf.Deg2Rad, 180f * Mathf.Deg2Rad);
                main.maxParticles = 80;
                main.simulationSpace = ParticleSystemSimulationSpace.World;

                var emission = _smokePlume.emission;
                emission.rateOverTime = 18f;

                var shape = _smokePlume.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 12f;
                shape.radius = 0.25f;

                var colLife = _smokePlume.colorOverLifetime;
                colLife.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(
                    new[] { 
                        new GradientColorKey(new Color(1.0f, 0.88f, 0.52f), 0.0f),  // Incandescent yellow core
                        new GradientColorKey(new Color(1.0f, 0.42f, 0.06f), 0.18f), // Hot solar orange flare
                        new GradientColorKey(new Color(0.24f, 0.21f, 0.20f), 0.55f), // Dark billowing smoke
                        new GradientColorKey(new Color(0.12f, 0.11f, 0.10f), 1.0f)   // Dusky ash tail
                    },
                    new[] { 
                        new GradientAlphaKey(0.0f, 0.0f), 
                        new GradientAlphaKey(0.78f, 0.15f), 
                        new GradientAlphaKey(0.45f, 0.65f), 
                        new GradientAlphaKey(0.0f, 1.0f) 
                    }
                );
                colLife.color = new ParticleSystem.MinMaxGradient(grad);

                var sizeLife = _smokePlume.sizeOverLifetime;
                sizeLife.enabled = true;
                sizeLife.size = new ParticleSystem.MinMaxCurve(1.0f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 2.2f));

                var rotLife = _smokePlume.rotationOverLifetime;
                rotLife.enabled = true;
                rotLife.z = new ParticleSystem.MinMaxCurve(-0.8f, 0.8f);

                var psr = smokeGo.GetComponent<ParticleSystemRenderer>();
                if (psr != null && s_supplySmokeMat != null)
                {
                    psr.sharedMaterial = s_supplySmokeMat;
                }

                // Add child glowing embers / sparks emitter
                var embersGo = new GameObject("SupplyDropEmbers");
                embersGo.transform.SetParent(smokeGo.transform, false);
                embersGo.transform.localPosition = Vector3.zero;

                var psEmbers = embersGo.AddComponent<ParticleSystem>();
                var eMain = psEmbers.main;
                eMain.loop = true;
                eMain.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 2.8f);
                eMain.startSpeed = new ParticleSystem.MinMaxCurve(2.8f, 5.5f);
                eMain.startSize = new ParticleSystem.MinMaxCurve(0.14f, 0.32f);
                eMain.maxParticles = 50;
                eMain.simulationSpace = ParticleSystemSimulationSpace.World;

                var eEmission = psEmbers.emission;
                eEmission.rateOverTime = 22f;

                var eShape = psEmbers.shape;
                eShape.shapeType = ParticleSystemShapeType.Cone;
                eShape.angle = 16f;
                eShape.radius = 0.20f;

                var eColLife = psEmbers.colorOverLifetime;
                eColLife.enabled = true;
                var eGrad = new Gradient();
                eGrad.SetKeys(
                    new[] { new GradientColorKey(new Color(1f, 0.9f, 0.5f), 0f), new GradientColorKey(new Color(1f, 0.45f, 0.08f), 0.6f), new GradientColorKey(new Color(0.9f, 0.15f, 0.05f), 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.65f), new GradientAlphaKey(0f, 1f) }
                );
                eColLife.color = new ParticleSystem.MinMaxGradient(eGrad);

                var eRend = embersGo.GetComponent<ParticleSystemRenderer>();
                if (eRend != null && s_supplyEmbersMat != null)
                {
                    eRend.sharedMaterial = s_supplyEmbersMat;
                }
            }

            if (_beaconColumn == null)
            {
                _beaconColumn = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _beaconColumn.name = "SupplyDropBeacon";
                _beaconColumn.transform.SetParent(transform, false);
                _beaconColumn.transform.localPosition = new Vector3(0f, 22f, 0f);
                _beaconColumn.transform.localScale = new Vector3(0.55f, 22f, 0.55f);
                var col = _beaconColumn.GetComponent<Collider>();
                if (col != null) Destroy(col);

                var r = _beaconColumn.GetComponent<Renderer>();
                if (r != null)
                {
                    if (s_supplyBeaconMat != null)
                    {
                        r.sharedMaterial = s_supplyBeaconMat;
                    }
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
            }
        }
        #endregion
    }
}
