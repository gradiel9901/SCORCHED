using System;
using System.Collections.Generic;
using UnityEngine;

namespace Scorched.Inventory
{
    /// <summary>
    /// Cabal-Online-inspired 6x5 (30-slot) spatial inventory matrix.
    /// Supports 1x1, 1x2 (vertical), and 2x2 multi-cell items, stackable ammo,
    /// drag-and-drop reorganization, and 3-to-1 grade synthesis.
    /// </summary>
    [Serializable]
    public class CabalInventoryGrid
    {
        public const int Columns = 6;
        public const int Rows = 5;
        public const int TotalCells = Columns * Rows; // 30 slots

        private readonly InventoryItemInstance[,] _grid = new InventoryItemInstance[Columns, Rows];
        private readonly List<InventoryItemInstance> _items = new List<InventoryItemInstance>(30);

        public IReadOnlyList<InventoryItemInstance> Items => _items;

        public InventoryItemInstance GetAt(int x, int y)
        {
            if (x < 0 || x >= Columns || y < 0 || y >= Rows) return null;
            return _grid[x, y];
        }

        public void Clear()
        {
            for (int x = 0; x < Columns; x++)
            {
                for (int y = 0; y < Rows; y++)
                {
                    _grid[x, y] = null;
                }
            }
            _items.Clear();
        }

        public static void GetFootprint(ItemId id, out int width, out int height)
        {
            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(id) : null;
            if (def != null)
            {
                width = Mathf.Max(1, def.GridWidth);
                height = Mathf.Max(1, def.GridHeight);
                return;
            }

            switch (id)
            {
                case ItemId.Water_Good:
                case ItemId.Bandage_Good:
                case ItemId.Food_Good:
                    width = 1; height = 2; return;
                case ItemId.Water_Great:
                case ItemId.Bandage_Great:
                case ItemId.Food_Great:
                    width = 2; height = 2; return;
                case ItemId.Weapon_AK47:
                    width = 2; height = 1; return;
                default:
                    width = 1; height = 1; return;
            }
        }

        public bool CanPlaceAt(int startX, int startY, int width, int height, InventoryItemInstance ignoreInstance = null)
        {
            if (width <= 0 || height <= 0) return false;
            if (startX < 0 || startY < 0 || startX + width > Columns || startY + height > Rows)
            {
                return false;
            }

            for (int x = startX; x < startX + width; x++)
            {
                for (int y = startY; y < startY + height; y++)
                {
                    var occupant = _grid[x, y];
                    if (occupant != null && occupant != ignoreInstance)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Scans the 6x5 matrix row-major to locate an empty width x height block.
        /// </summary>
        public bool TryFindFreeSpace(int width, int height, out int startX, out int startY)
        {
            for (int y = 0; y <= Rows - height; y++)
            {
                for (int x = 0; x <= Columns - width; x++)
                {
                    if (CanPlaceAt(x, y, width, height))
                    {
                        startX = x;
                        startY = y;
                        return true;
                    }
                }
            }

            startX = -1;
            startY = -1;
            return false;
        }

        public bool CanAddItem(ItemId id, int count = 1)
        {
            if (id == ItemId.None || count <= 0) return false;
            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(id) : null;
            bool stackable = def != null ? def.IsStackable : (id == ItemId.Ammo_762);
            int maxStack = def != null ? Mathf.Max(1, def.MaxStack) : (id == ItemId.Ammo_762 ? 120 : 1);

            int remaining = count;
            if (stackable)
            {
                for (int i = 0; i < _items.Count && remaining > 0; i++)
                {
                    if (_items[i].Id == id && _items[i].StackCount < maxStack)
                    {
                        remaining -= Mathf.Min(remaining, maxStack - _items[i].StackCount);
                    }
                }
            }

            if (remaining <= 0) return true;

            GetFootprint(id, out int w, out int h);
            int stacksNeeded = stackable ? Mathf.CeilToInt(remaining / (float)maxStack) : remaining;
            if (stacksNeeded == 1) return TryFindFreeSpace(w, h, out _, out _);

            // Simulate multi-slot placement without mutating the live grid
            var simulated = new bool[Columns, Rows];
            for (int x = 0; x < Columns; x++)
                for (int y = 0; y < Rows; y++)
                    simulated[x, y] = _grid[x, y] != null;

            for (int n = 0; n < stacksNeeded; n++)
            {
                bool placed = false;
                for (int y = 0; y <= Rows - h && !placed; y++)
                {
                    for (int x = 0; x <= Columns - w && !placed; x++)
                    {
                        bool free = true;
                        for (int sx = x; sx < x + w && free; sx++)
                            for (int sy = y; sy < y + h; sy++)
                                if (simulated[sx, sy]) { free = false; break; }
                        if (free)
                        {
                            for (int sx = x; sx < x + w; sx++)
                                for (int sy = y; sy < y + h; sy++)
                                    simulated[sx, sy] = true;
                            placed = true;
                        }
                    }
                }
                if (!placed) return false;
            }
            return true;
        }

        public bool TryAddItem(ItemId id, int count = 1)
        {
            if (!CanAddItem(id, count)) return false;

            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(id) : null;
            bool stackable = def != null ? def.IsStackable : (id == ItemId.Ammo_762);
            int maxStack = def != null ? Mathf.Max(1, def.MaxStack) : (id == ItemId.Ammo_762 ? 120 : 1);

            int remaining = count;

            if (stackable)
            {
                // First top up existing partial stacks
                for (int i = 0; i < _items.Count && remaining > 0; i++)
                {
                    if (_items[i].Id == id && _items[i].StackCount < maxStack)
                    {
                        int add = Mathf.Min(remaining, maxStack - _items[i].StackCount);
                        _items[i].StackCount += add;
                        remaining -= add;
                    }
                }
            }

            GetFootprint(id, out int w, out int h);

            while (remaining > 0)
            {
                int amountForStack = stackable ? Mathf.Min(remaining, maxStack) : 1;
                if (!TryFindFreeSpace(w, h, out int fx, out int fy))
                {
                    return false;
                }

                var inst = new InventoryItemInstance(id, amountForStack, fx, fy);
                PlaceInstanceAt(inst, fx, fy, w, h);
                remaining -= amountForStack;
            }

            return true;
        }

        public bool TryPlaceExact(InventoryItemInstance inst, int x, int y)
        {
            if (inst == null || inst.Id == ItemId.None) return false;
            GetFootprint(inst.Id, out int w, out int h);
            if (!CanPlaceAt(x, y, w, h, inst)) return false;

            RemoveInstance(inst);
            PlaceInstanceAt(inst, x, y, w, h);
            return true;
        }

        public bool TryMoveItem(string instanceGuid, int newX, int newY)
        {
            var inst = FindByGuid(instanceGuid);
            if (inst == null) return false;
            return TryPlaceExact(inst, newX, newY);
        }

        public InventoryItemInstance FindByGuid(string instanceGuid)
        {
            if (string.IsNullOrEmpty(instanceGuid)) return null;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].InstanceGuid == instanceGuid) return _items[i];
            }
            return null;
        }

        public InventoryItemInstance FindFirstOfId(ItemId id)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id) return _items[i];
            }
            return null;
        }

        public int CountTotalAmount(ItemId id)
        {
            int total = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id) total += _items[i].StackCount;
            }
            return total;
        }

        public int CountInstanceSlotsOfId(ItemId id)
        {
            int total = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == id) total++;
            }
            return total;
        }

        public bool RemoveByGuid(string guid)
        {
            var inst = FindByGuid(guid);
            if (inst == null) return false;
            RemoveInstance(inst);
            return true;
        }

        public void RemoveInstance(InventoryItemInstance inst)
        {
            if (inst == null) return;
            for (int x = 0; x < Columns; x++)
            {
                for (int y = 0; y < Rows; y++)
                {
                    if (_grid[x, y] == inst)
                    {
                        _grid[x, y] = null;
                    }
                }
            }
            _items.Remove(inst);
        }

        public bool ConsumeAmount(ItemId id, int amount)
        {
            if (CountTotalAmount(id) < amount) return false;
            int remaining = amount;
            for (int i = _items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                if (_items[i].Id != id) continue;
                int take = Mathf.Min(remaining, _items[i].StackCount);
                _items[i].StackCount -= take;
                remaining -= take;
                if (_items[i].StackCount <= 0)
                {
                    RemoveInstance(_items[i]);
                }
            }
            return remaining == 0;
        }

        public int GetOccupiedCellCount()
        {
            int count = 0;
            for (int x = 0; x < Columns; x++)
            {
                for (int y = 0; y < Rows; y++)
                {
                    if (_grid[x, y] != null) count++;
                }
            }
            return count;
        }

        private void PlaceInstanceAt(InventoryItemInstance inst, int startX, int startY, int w, int h)
        {
            inst.GridX = startX;
            inst.GridY = startY;
            if (!_items.Contains(inst))
            {
                _items.Add(inst);
            }
            for (int x = startX; x < startX + w; x++)
            {
                for (int y = startY; y < startY + h; y++)
                {
                    _grid[x, y] = inst;
                }
            }
        }

        public string SerializeCompact()
        {
            if (_items.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder(_items.Count * 22);
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it == null || it.Id == ItemId.None) continue;
                if (sb.Length > 0) sb.Append(';');
                string shortGuid = string.IsNullOrEmpty(it.InstanceGuid)
                    ? Guid.NewGuid().ToString("N").Substring(0, 8)
                    : (it.InstanceGuid.Length > 10 ? it.InstanceGuid.Substring(0, 10) : it.InstanceGuid);
                sb.Append(shortGuid).Append(':')
                  .Append((int)it.Id).Append(':')
                  .Append(it.StackCount).Append(':')
                  .Append(it.GridX).Append(':')
                  .Append(it.GridY);
            }
            return sb.ToString();
        }

        public void DeserializeCompact(string data)
        {
            Clear();
            if (string.IsNullOrEmpty(data)) return;

            string[] entries = data.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                if (string.IsNullOrEmpty(entries[i])) continue;
                string[] parts = entries[i].Split(':');
                if (parts.Length < 5) continue;

                if (!int.TryParse(parts[1], out int rawId) ||
                    !int.TryParse(parts[2], out int count) ||
                    !int.TryParse(parts[3], out int gx) ||
                    !int.TryParse(parts[4], out int gy))
                {
                    continue;
                }

                ItemId id = (ItemId)rawId;
                if (id == ItemId.None) continue;

                var inst = new InventoryItemInstance
                {
                    InstanceGuid = parts[0],
                    Id = id,
                    StackCount = Mathf.Max(1, count),
                    GridX = gx,
                    GridY = gy
                };

                GetFootprint(id, out int w, out int h);
                if (CanPlaceAt(gx, gy, w, h))
                {
                    PlaceInstanceAt(inst, gx, gy, w, h);
                }
                else if (TryFindFreeSpace(w, h, out int fx, out int fy))
                {
                    PlaceInstanceAt(inst, fx, fy, w, h);
                }
            }
        }
    }
}
