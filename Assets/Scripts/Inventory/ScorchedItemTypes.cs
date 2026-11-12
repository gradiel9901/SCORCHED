using System;
using UnityEngine;

namespace Scorched.Inventory
{
    public enum ItemCategory : byte
    {
        Water = 0,
        Bandage = 1,
        Food = 2,
        GunPart = 3,
        Ammo = 4,
        Weapon = 5
    }

    public enum ItemGrade : byte
    {
        None = 0,
        Poor = 1,
        Good = 2,
        Great = 3
    }

    public enum ItemId : ushort
    {
        None = 0,

        Water_Poor = 1,
        Water_Good = 2,
        Water_Great = 3,

        Bandage_Poor = 10,
        Bandage_Good = 11,
        Bandage_Great = 12,

        Food_Poor = 20,
        Food_Good = 21,
        Food_Great = 22,

        AK47_Receiver = 30,
        AK47_Barrel = 31,
        AK47_Stock = 32,
        AK47_Bolt = 33,

        Ammo_762 = 40,

        Weapon_AK47 = 50
    }

    [Serializable]
    public class ItemDefinition
    {
        public ItemId Id;
        public string DisplayName;
        public string ShortTag;
        public ItemCategory Category;
        public ItemGrade Grade;
        public int GridWidth = 1;
        public int GridHeight = 1;
        public bool IsStackable = false;
        public int MaxStack = 1;
        public float RestoreAmount = 0f;
        public ItemId UpgradesToId = ItemId.None;
        public Sprite Icon;
        public GameObject WorldPickupPrefab;

        public int SlotCount => Mathf.Max(1, GridWidth) * Mathf.Max(1, GridHeight);
        public bool IsConsumable => Category == ItemCategory.Water || Category == ItemCategory.Bandage || Category == ItemCategory.Food;
    }

    [Serializable]
    public class InventoryItemInstance
    {
        public string InstanceGuid;
        public ItemId Id;
        public int StackCount = 1;
        public int GridX = -1;
        public int GridY = -1;

        public InventoryItemInstance() { }

        public InventoryItemInstance(ItemId id, int stackCount = 1, int x = -1, int y = -1)
        {
            InstanceGuid = Guid.NewGuid().ToString("N");
            Id = id;
            StackCount = Mathf.Max(1, stackCount);
            GridX = x;
            GridY = y;
        }
    }

    public static class ScorchedInputGates
    {
        public static bool IsBackpackOpen { get; set; }
        public static bool IsContainerOpen { get; set; }
        public static bool IsInventoryOpen => IsBackpackOpen || IsContainerOpen;
        public static int LastUiClosedFrame { get; set; } = -1;
    }
}
