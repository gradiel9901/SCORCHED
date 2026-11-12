using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Scorched.Player;
using Scorched.UI;
using Scorched.Weapons;

namespace Scorched.Inventory
{
    public enum HotbarSlotType : byte
    {
        Weapon = 1,
        Water = 2,
        Food = 3,
        Bandage = 4
    }

    [Serializable]
    public struct HotbarSlotState
    {
        public HotbarSlotType SlotType;
        public ItemCategory Category;
        public ItemId BaseIconItemId;
        public ItemId BoundItemId;
        public ItemGrade HighestGrade;
        public int HighestGradeCount;
        public int TotalCategoryCount;
        public string FirstInstanceGuid;

        public bool IsEmpty => HighestGrade == ItemGrade.None || HighestGradeCount <= 0;
    }

    /// <summary>
    /// 4-slot tactical quick-hotbar controller ([1] Weapon, [2] Water, [3] Food, [4] Bandage).
    /// Subscribes to PlayerInventoryController.OnInventoryChanged and automatically binds
    /// the highest-grade item per consumable category (Great > Good > Poor).
    /// Pressing 1/2/3/4 switches active slot (lowering/raising weapon viewmodel);
    /// re-pressing the active consumable key or Left-Clicking consumes 1 unit.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerHotbarController : NetworkBehaviour
    {
        private struct CategorySpec
        {
            public HotbarSlotType SlotType;
            public ItemCategory Category;
            public ItemId GreatId;
            public ItemId GoodId;
            public ItemId PoorId;
            public string EmptyNotice;
        }

        private static readonly CategorySpec[] ConsumableSpecs =
        {
            new CategorySpec
            {
                SlotType = HotbarSlotType.Water,
                Category = ItemCategory.Water,
                GreatId = ItemId.Water_Great,
                GoodId = ItemId.Water_Good,
                PoorId = ItemId.Water_Poor,
                EmptyNotice = "NO WATER IN BACKPACK"
            },
            new CategorySpec
            {
                SlotType = HotbarSlotType.Food,
                Category = ItemCategory.Food,
                GreatId = ItemId.Food_Great,
                GoodId = ItemId.Food_Good,
                PoorId = ItemId.Food_Poor,
                EmptyNotice = "NO FOOD IN BACKPACK"
            },
            new CategorySpec
            {
                SlotType = HotbarSlotType.Bandage,
                Category = ItemCategory.Bandage,
                GreatId = ItemId.Bandage_Great,
                GoodId = ItemId.Bandage_Good,
                PoorId = ItemId.Bandage_Poor,
                EmptyNotice = "NO BANDAGES IN BACKPACK"
            }
        };

        private PlayerInventoryController _inventory;
        private NetworkWeaponController _weaponController;
        private PlayerHealthController _healthController;
        private FPSPerspectiveController _perspectiveController;

        private readonly HotbarSlotState[] _consumableSlots = new HotbarSlotState[3];
        private HotbarSlotType _activeSlot = HotbarSlotType.Weapon;
        private HotbarSlotType? _pendingAutoReturnFromSlot;
        private bool _subscribedToInventory;

        public HotbarSlotType ActiveSlot => _activeSlot;
        public bool IsWeaponSlotSelected => _activeSlot == HotbarSlotType.Weapon;

        public event Action OnHotbarUpdated;

        private void Awake()
        {
            CacheComponents();
            InitializeDefaultSlots();
        }

        private void CacheComponents()
        {
            if (_inventory == null) _inventory = GetComponent<PlayerInventoryController>();
            if (_weaponController == null) _weaponController = GetComponent<NetworkWeaponController>();
            if (_healthController == null) _healthController = GetComponent<PlayerHealthController>();
            if (_perspectiveController == null) _perspectiveController = GetComponent<FPSPerspectiveController>();
        }

        private void InitializeDefaultSlots()
        {
            for (int i = 0; i < ConsumableSpecs.Length; i++)
            {
                _consumableSlots[i] = new HotbarSlotState
                {
                    SlotType = ConsumableSpecs[i].SlotType,
                    Category = ConsumableSpecs[i].Category,
                    BaseIconItemId = ConsumableSpecs[i].PoorId,
                    BoundItemId = ConsumableSpecs[i].PoorId,
                    HighestGrade = ItemGrade.None,
                    HighestGradeCount = 0,
                    TotalCategoryCount = 0,
                    FirstInstanceGuid = null
                };
            }
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            CacheComponents();
            SubscribeInventory();
            RefreshFromInventory();
        }

        public override void OnNetworkDespawn()
        {
            UnsubscribeInventory();
            base.OnNetworkDespawn();
        }

        private void Start()
        {
            CacheComponents();
            SubscribeInventory();
            RefreshFromInventory();
        }

        private void OnDestroy()
        {
            UnsubscribeInventory();
        }

        private void SubscribeInventory()
        {
            if (_subscribedToInventory || _inventory == null) return;
            _inventory.OnInventoryChanged += RefreshFromInventory;
            _subscribedToInventory = true;
        }

        private void UnsubscribeInventory()
        {
            if (!_subscribedToInventory || _inventory == null) return;
            _inventory.OnInventoryChanged -= RefreshFromInventory;
            _subscribedToInventory = false;
        }

        private void Update()
        {
            if (IsSpawned && !IsOwner) return;
            if (_healthController != null && !_healthController.IsAlive)
            {
                if (_activeSlot != HotbarSlotType.Weapon)
                {
                    _activeSlot = HotbarSlotType.Weapon;
                    _pendingAutoReturnFromSlot = null;
                    OnHotbarUpdated?.Invoke();
                }
                return;
            }

            if (ScorchedInputGates.IsInventoryOpen || MainMenuUIManager.IsMenuOpen) return;
            if (Time.frameCount == ScorchedInputGates.LastUiClosedFrame) return;

            PollHotbarInputs();
        }

        private void PollHotbarInputs()
        {
            bool pressed1 = false;
            bool pressed2 = false;
            bool pressed3 = false;
            bool pressed4 = false;
            bool pressedUseMouse = false;

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                pressed1 = keyboard.digit1Key.wasPressedThisFrame;
                pressed2 = keyboard.digit2Key.wasPressedThisFrame;
                pressed3 = keyboard.digit3Key.wasPressedThisFrame;
                pressed4 = keyboard.digit4Key.wasPressedThisFrame;
            }

            var mouse = Mouse.current;
            if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            {
                pressedUseMouse = mouse.leftButton.wasPressedThisFrame;
            }

#if ENABLE_LEGACY_INPUT_MANAGER
            if (!pressed1) pressed1 = Input.GetKeyDown(KeyCode.Alpha1);
            if (!pressed2) pressed2 = Input.GetKeyDown(KeyCode.Alpha2);
            if (!pressed3) pressed3 = Input.GetKeyDown(KeyCode.Alpha3);
            if (!pressed4) pressed4 = Input.GetKeyDown(KeyCode.Alpha4);
            if (!pressedUseMouse && Cursor.lockState == CursorLockMode.Locked)
            {
                pressedUseMouse = Input.GetMouseButtonDown(0);
            }
#endif

            if (pressed1)
            {
                SelectSlot(HotbarSlotType.Weapon);
                return;
            }

            if (pressed2)
            {
                HandleConsumableKeyPress(HotbarSlotType.Water);
                return;
            }

            if (pressed3)
            {
                HandleConsumableKeyPress(HotbarSlotType.Food);
                return;
            }

            if (pressed4)
            {
                HandleConsumableKeyPress(HotbarSlotType.Bandage);
                return;
            }

            if (pressedUseMouse && _activeSlot != HotbarSlotType.Weapon)
            {
                TryConsumeSlot(_activeSlot);
            }
        }

        public void HandleConsumableKeyPress(HotbarSlotType targetSlot)
        {
            if (targetSlot == HotbarSlotType.Weapon)
            {
                SelectSlot(HotbarSlotType.Weapon);
                return;
            }

            if (_activeSlot != targetSlot)
            {
                SelectSlot(targetSlot);
            }
            else
            {
                TryConsumeSlot(targetSlot);
            }
        }

        public void SelectSlot(HotbarSlotType slot)
        {
            _pendingAutoReturnFromSlot = null;
            bool changed = _activeSlot != slot;
            _activeSlot = slot;

            if (_weaponController == null) CacheComponents();
            if (_perspectiveController != null)
            {
                bool drawWeapon = _weaponController != null && _weaponController.IsWeaponDrawn;
                _perspectiveController.RefreshWeaponEquippedState(drawWeapon);
            }

            if (changed)
            {
                OnHotbarUpdated?.Invoke();
            }
        }

        public bool TryConsumeSlot(HotbarSlotType slot)
        {
            if (slot == HotbarSlotType.Weapon) return false;
            if (_inventory == null) CacheComponents();
            if (_inventory == null) return false;

            RefreshFromInventory();
            HotbarSlotState state = GetConsumableSlotState(slot);
            if (state.IsEmpty || string.IsNullOrEmpty(state.FirstInstanceGuid))
            {
                _inventory.RaiseNotice(GetEmptyNotice(slot));
                return false;
            }

            _pendingAutoReturnFromSlot = slot;
            _inventory.RequestUseItem(state.FirstInstanceGuid);
            return true;
        }

        public HotbarSlotState GetConsumableSlotState(HotbarSlotType slot)
        {
            int idx = (int)slot - 2;
            if (idx >= 0 && idx < _consumableSlots.Length)
            {
                return _consumableSlots[idx];
            }
            return default;
        }

        public void RefreshFromInventory()
        {
            if (_inventory == null) CacheComponents();
            IReadOnlyList<InventoryItemInstance> items = _inventory != null && _inventory.Grid != null
                ? _inventory.Grid.Items
                : null;

            for (int i = 0; i < ConsumableSpecs.Length; i++)
            {
                _consumableSlots[i] = ResolveHighestGradeSlot(ConsumableSpecs[i], items);
            }

            // After consuming the last unit of the active consumable slot, automatically raise the AK-47 if owned
            if (_pendingAutoReturnFromSlot.HasValue && _activeSlot == _pendingAutoReturnFromSlot.Value)
            {
                HotbarSlotState currentState = GetConsumableSlotState(_activeSlot);
                if (currentState.IsEmpty)
                {
                    _pendingAutoReturnFromSlot = null;
                    if (_weaponController != null && _weaponController.IsWeaponEquipped)
                    {
                        _activeSlot = HotbarSlotType.Weapon;
                        _perspectiveController?.RefreshWeaponEquippedState(true);
                    }
                }
            }

            OnHotbarUpdated?.Invoke();
        }

        private static HotbarSlotState ResolveHighestGradeSlot(CategorySpec spec, IReadOnlyList<InventoryItemInstance> items)
        {
            int greatCount = 0;
            int goodCount = 0;
            int poorCount = 0;
            string greatGuid = null;
            string goodGuid = null;
            string poorGuid = null;

            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var inst = items[i];
                    if (inst == null || inst.StackCount <= 0) continue;

                    if (inst.Id == spec.GreatId)
                    {
                        greatCount += inst.StackCount;
                        if (string.IsNullOrEmpty(greatGuid)) greatGuid = inst.InstanceGuid;
                    }
                    else if (inst.Id == spec.GoodId)
                    {
                        goodCount += inst.StackCount;
                        if (string.IsNullOrEmpty(goodGuid)) goodGuid = inst.InstanceGuid;
                    }
                    else if (inst.Id == spec.PoorId)
                    {
                        poorCount += inst.StackCount;
                        if (string.IsNullOrEmpty(poorGuid)) poorGuid = inst.InstanceGuid;
                    }
                }
            }

            var result = new HotbarSlotState
            {
                SlotType = spec.SlotType,
                Category = spec.Category,
                BaseIconItemId = spec.PoorId,
                TotalCategoryCount = greatCount + goodCount + poorCount
            };

            if (greatCount > 0)
            {
                result.BoundItemId = spec.GreatId;
                result.HighestGrade = ItemGrade.Great;
                result.HighestGradeCount = greatCount;
                result.FirstInstanceGuid = greatGuid;
            }
            else if (goodCount > 0)
            {
                result.BoundItemId = spec.GoodId;
                result.HighestGrade = ItemGrade.Good;
                result.HighestGradeCount = goodCount;
                result.FirstInstanceGuid = goodGuid;
            }
            else if (poorCount > 0)
            {
                result.BoundItemId = spec.PoorId;
                result.HighestGrade = ItemGrade.Poor;
                result.HighestGradeCount = poorCount;
                result.FirstInstanceGuid = poorGuid;
            }
            else
            {
                result.BoundItemId = spec.PoorId;
                result.HighestGrade = ItemGrade.None;
                result.HighestGradeCount = 0;
                result.FirstInstanceGuid = null;
            }

            return result;
        }

        private static string GetEmptyNotice(HotbarSlotType slot)
        {
            for (int i = 0; i < ConsumableSpecs.Length; i++)
            {
                if (ConsumableSpecs[i].SlotType == slot) return ConsumableSpecs[i].EmptyNotice;
            }
            return "SLOT EMPTY";
        }
    }
}

