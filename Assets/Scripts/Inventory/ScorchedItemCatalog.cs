using System.Collections.Generic;
using UnityEngine;

namespace Scorched.Inventory
{
    /// <summary>
    /// Central item catalog loaded from Resources/ScorchedItemCatalog.asset.
    /// Shared across server, clients, containers, crafting, and the Cabal 6x5 inventory grid.
    /// </summary>
    [CreateAssetMenu(fileName = "ScorchedItemCatalog", menuName = "SCORCHED/Item Catalog")]
    public class ScorchedItemCatalog : ScriptableObject
    {
        private static ScorchedItemCatalog _instance;
        public static ScorchedItemCatalog Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<ScorchedItemCatalog>("ScorchedItemCatalog");
                }
                return _instance;
            }
        }

        [Header("UI Sprites")]
        public Sprite RadialProgressRingSprite;
        public Sprite CabalSlotCellSprite;

        [Header("Item Definitions")]
        public List<ItemDefinition> Items = new List<ItemDefinition>();

        private Dictionary<ItemId, ItemDefinition> _lookup;

        private void OnEnable()
        {
            RebuildLookup();
        }

        public void RebuildLookup()
        {
            _lookup = new Dictionary<ItemId, ItemDefinition>(Items.Count);
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i] != null && Items[i].Id != ItemId.None)
                {
                    _lookup[Items[i].Id] = Items[i];
                }
            }
        }

        public ItemDefinition GetDefinition(ItemId id)
        {
            if (_lookup == null || _lookup.Count != Items.Count)
            {
                RebuildLookup();
            }
            return _lookup != null && _lookup.TryGetValue(id, out var def) ? def : null;
        }

        public static bool IsAK47Part(ItemId id)
        {
            return id == ItemId.AK47_Receiver ||
                   id == ItemId.AK47_Barrel ||
                   id == ItemId.AK47_Stock ||
                   id == ItemId.AK47_Bolt;
        }

        public static bool CanSynthesizeUpgrade(ItemId inputId, out ItemId resultId)
        {
            var inst = Instance;
            if (inst != null)
            {
                var def = inst.GetDefinition(inputId);
                if (def != null && def.UpgradesToId != ItemId.None)
                {
                    resultId = def.UpgradesToId;
                    return true;
                }
            }

            resultId = inputId switch
            {
                ItemId.Water_Poor => ItemId.Water_Good,
                ItemId.Water_Good => ItemId.Water_Great,
                ItemId.Food_Poor => ItemId.Food_Good,
                ItemId.Food_Good => ItemId.Food_Great,
                ItemId.Bandage_Poor => ItemId.Bandage_Good,
                ItemId.Bandage_Good => ItemId.Bandage_Great,
                _ => ItemId.None
            };
            return resultId != ItemId.None;
        }
    }
}

