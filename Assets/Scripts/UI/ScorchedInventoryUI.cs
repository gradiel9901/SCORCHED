using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Scorched.Inventory;
using Scorched.Weapons;

namespace Scorched.UI
{
    /// <summary>
    /// Cabal-Online-style 6x5 (30-slot) spatial backpack UI with multi-slot footprint widgets (1x1, 1x2, 2x2),
    /// drag-and-drop cell preview (green valid / red invalid), right-click consumable usage,
    /// drag-outside world dropping, and a top Weapon Slot header.
    /// </summary>
    [DisallowMultipleComponent]
    public class ScorchedInventoryUI : MonoBehaviour
    {
        public static ScorchedInventoryUI Instance { get; private set; }

        private const float CellSize = 72f;
        private const float CellGap = 4f;

        private Canvas _canvas;
        private Font _font;
        private Sprite _frameSprite;
        private Sprite _cellSprite;

        private GameObject _rootPanel;
        private Text _weaponHeaderText;
        private Text _slotUsageText;
        private RectTransform _gridContainer;
        private RectTransform _itemsLayer;
        private RectTransform _previewLayer;

        private readonly Image[,] _previewCells = new Image[CabalInventoryGrid.Columns, CabalInventoryGrid.Rows];
        private readonly List<GameObject> _spawnedItemWidgets = new List<GameObject>();

        private PlayerInventoryController _boundInventory;
        private GameObject _dragGhost;
        private InventoryItemInstance _draggedItem;

        private bool _isVisible;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            UnbindInventory();
        }

        public void Initialize(Canvas canvas, Font font, Sprite frameSprite, Sprite cellSprite)
        {
            _canvas = canvas;
            _font = font;
            _frameSprite = frameSprite;
            _cellSprite = cellSprite;
            BuildUI();
            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            _isVisible = visible;
            if (_rootPanel != null)
            {
                _rootPanel.SetActive(visible);
            }
            if (!visible)
            {
                ClearDragGhost();
                HidePreviewCells();
            }
            else
            {
                EnsureEventSystemExists();
                RefreshAll();
            }
        }

        private void Update()
        {
            if (!_isVisible) return;

            var localInv = PlayerInventoryController.LocalInstance;
            if (localInv != _boundInventory)
            {
                BindInventory(localInv);
                RefreshAll();
            }
            UpdateWeaponHeader();
        }

        private void BindInventory(PlayerInventoryController inv)
        {
            UnbindInventory();
            _boundInventory = inv;
            if (_boundInventory != null)
            {
                _boundInventory.OnInventoryChanged += RefreshAll;
            }
        }

        private void UnbindInventory()
        {
            if (_boundInventory != null)
            {
                _boundInventory.OnInventoryChanged -= RefreshAll;
                _boundInventory = null;
            }
        }

        private void BuildUI()
        {
            if (_rootPanel != null) Destroy(_rootPanel);

            // Side-by-side Left Panel (520x560)
            _rootPanel = new GameObject("InventoryPanel_Cabal6x5", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            _rootPanel.transform.SetParent(transform, false);
            var panelRect = _rootPanel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = new Vector2(-265f, 0f);
            panelRect.sizeDelta = new Vector2(520f, 560f);

            var bg = _rootPanel.GetComponent<Image>();
            bg.sprite = _frameSprite;
            bg.type = _frameSprite != null && _frameSprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
            bg.color = new Color(0.08f, 0.09f, 0.11f, 0.94f);

            // Title Row
            CreateLabel("Title", _rootPanel.transform, "FIELD BACKPACK // 30 SLOTS", 13, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.94f, 0.96f, 1f), new Vector2(22f, -18f), new Vector2(300f, 24f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            _slotUsageText = CreateLabel("SlotCount", _rootPanel.transform, "0 / 30", 11, FontStyle.Bold, TextAnchor.MiddleRight,
                new Color(0.75f, 0.78f, 0.84f, 1f), new Vector2(-22f, -18f), new Vector2(120f, 24f), new Vector2(1f, 1f), new Vector2(1f, 1f));

            // Weapon Header Box
            var weaponBox = new GameObject("WeaponSlotHeader", typeof(RectTransform), typeof(Image));
            weaponBox.transform.SetParent(_rootPanel.transform, false);
            var wRect = weaponBox.GetComponent<RectTransform>();
            wRect.anchorMin = new Vector2(0.5f, 1f);
            wRect.anchorMax = new Vector2(0.5f, 1f);
            wRect.pivot = new Vector2(0.5f, 1f);
            wRect.anchoredPosition = new Vector2(0f, -48f);
            wRect.sizeDelta = new Vector2(468f, 54f);
            var wImg = weaponBox.GetComponent<Image>();
            wImg.color = new Color(0.12f, 0.14f, 0.17f, 0.95f);

            _weaponHeaderText = CreateLabel("WeaponHeaderLabel", weaponBox.transform, "[ UNARMED // NO WEAPON ]", 14, FontStyle.Bold, TextAnchor.MiddleCenter,
                new Color(0.86f, 0.88f, 0.91f, 1f), Vector2.zero, new Vector2(448f, 44f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            // 6x5 Spatial Grid Container (452 x 376)
            float gridW = CabalInventoryGrid.Columns * CellSize + (CabalInventoryGrid.Columns - 1) * CellGap;
            float gridH = CabalInventoryGrid.Rows * CellSize + (CabalInventoryGrid.Rows - 1) * CellGap;

            var gridObj = new GameObject("GridMatrix6x5", typeof(RectTransform), typeof(Image));
            gridObj.transform.SetParent(_rootPanel.transform, false);
            _gridContainer = gridObj.GetComponent<RectTransform>();
            _gridContainer.anchorMin = new Vector2(0.5f, 1f);
            _gridContainer.anchorMax = new Vector2(0.5f, 1f);
            _gridContainer.pivot = new Vector2(0.5f, 1f);
            _gridContainer.anchoredPosition = new Vector2(0f, -118f);
            _gridContainer.sizeDelta = new Vector2(gridW, gridH);
            gridObj.GetComponent<Image>().color = new Color(0.05f, 0.06f, 0.07f, 0.82f);

            Sprite cellTex = _cellSprite ?? (ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.CabalSlotCellSprite : null);

            for (int y = 0; y < CabalInventoryGrid.Rows; y++)
            {
                for (int x = 0; x < CabalInventoryGrid.Columns; x++)
                {
                    var cellGo = new GameObject($"Cell_{x}_{y}", typeof(RectTransform), typeof(Image));
                    cellGo.transform.SetParent(_gridContainer, false);
                    var cRect = cellGo.GetComponent<RectTransform>();
                    cRect.anchorMin = new Vector2(0f, 1f);
                    cRect.anchorMax = new Vector2(0f, 1f);
                    cRect.pivot = new Vector2(0f, 1f);
                    cRect.anchoredPosition = CellToLocalTopLeft(x, y);
                    cRect.sizeDelta = new Vector2(CellSize, CellSize);

                    var cImg = cellGo.GetComponent<Image>();
                    cImg.sprite = cellTex;
                    cImg.color = cellTex != null ? new Color(0.85f, 0.88f, 0.92f, 0.90f) : new Color(0.14f, 0.16f, 0.19f, 0.92f);
                    cImg.raycastTarget = false;
                }
            }

            // Preview Layer
            var prevObj = new GameObject("PreviewLayer", typeof(RectTransform));
            prevObj.transform.SetParent(_gridContainer, false);
            _previewLayer = prevObj.GetComponent<RectTransform>();
            _previewLayer.anchorMin = Vector2.zero;
            _previewLayer.anchorMax = Vector2.one;
            _previewLayer.offsetMin = Vector2.zero;
            _previewLayer.offsetMax = Vector2.zero;

            for (int y = 0; y < CabalInventoryGrid.Rows; y++)
            {
                for (int x = 0; x < CabalInventoryGrid.Columns; x++)
                {
                    var pGo = new GameObject($"Prev_{x}_{y}", typeof(RectTransform), typeof(Image));
                    pGo.transform.SetParent(_previewLayer, false);
                    var pRect = pGo.GetComponent<RectTransform>();
                    pRect.anchorMin = new Vector2(0f, 1f);
                    pRect.anchorMax = new Vector2(0f, 1f);
                    pRect.pivot = new Vector2(0f, 1f);
                    pRect.anchoredPosition = CellToLocalTopLeft(x, y);
                    pRect.sizeDelta = new Vector2(CellSize, CellSize);
                    var pImg = pGo.GetComponent<Image>();
                    pImg.raycastTarget = false;
                    pImg.enabled = false;
                    _previewCells[x, y] = pImg;
                }
            }

            // Items Layer
            var itemsObj = new GameObject("ItemsLayer", typeof(RectTransform));
            itemsObj.transform.SetParent(_gridContainer, false);
            _itemsLayer = itemsObj.GetComponent<RectTransform>();
            _itemsLayer.anchorMin = Vector2.zero;
            _itemsLayer.anchorMax = Vector2.one;
            _itemsLayer.offsetMin = Vector2.zero;
            _itemsLayer.offsetMax = Vector2.zero;

            // Footer Help Bar
            CreateLabel("FooterHint", _rootPanel.transform, "DRAG: MOVE   |   R-CLICK: USE   |   DROP OUTSIDE: DISCARD   |   B / ESC: CLOSE",
                9, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.60f, 0.64f, 0.70f, 1f),
                new Vector2(0f, 18f), new Vector2(480f, 22f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
        }

        private static Vector2 CellToLocalTopLeft(int x, int y)
        {
            return new Vector2(x * (CellSize + CellGap), -(y * (CellSize + CellGap)));
        }

        private void UpdateWeaponHeader()
        {
            if (_weaponHeaderText == null) return;

            NetworkWeaponController wpn = null;
            if (_boundInventory != null)
            {
                wpn = _boundInventory.GetComponent<NetworkWeaponController>();
            }
            if (wpn == null)
            {
                wpn = FindFirstLocalWeapon();
            }

            if (wpn != null && wpn.IsWeaponEquipped)
            {
                _weaponHeaderText.text = $"[ AK-47 TACTICAL ]  //  MAG {wpn.CurrentAmmoInMag}  |  RESERVE {wpn.CurrentReserveAmmo}";
                _weaponHeaderText.color = new Color(0.95f, 0.74f, 0.25f, 1f);
            }
            else
            {
                int reserve = _boundInventory != null ? _boundInventory.Grid.CountTotalAmount(ItemId.Ammo_762) : 0;
                _weaponHeaderText.text = reserve > 0
                    ? $"[ UNARMED // NO WEAPON ]  (7.62MM AMMO: {reserve})"
                    : "[ UNARMED // NO WEAPON ]";
                _weaponHeaderText.color = new Color(0.75f, 0.79f, 0.85f, 1f);
            }
        }

        private static NetworkWeaponController FindFirstLocalWeapon()
        {
            var inv = PlayerInventoryController.LocalInstance;
            return inv != null ? inv.GetComponent<NetworkWeaponController>() : null;
        }

        public void RefreshAll()
        {
            for (int i = 0; i < _spawnedItemWidgets.Count; i++)
            {
                if (_spawnedItemWidgets[i] != null) Destroy(_spawnedItemWidgets[i]);
            }
            _spawnedItemWidgets.Clear();
            UpdateWeaponHeader();

            if (_boundInventory == null)
            {
                if (_slotUsageText != null) _slotUsageText.text = "0 / 30";
                return;
            }

            if (_slotUsageText != null)
            {
                _slotUsageText.text = $"{_boundInventory.Grid.GetOccupiedCellCount()} / {CabalInventoryGrid.TotalCells}";
            }

            var items = _boundInventory.Grid.Items;
            for (int i = 0; i < items.Count; i++)
            {
                CreateItemWidget(items[i]);
            }
        }

        private void CreateItemWidget(InventoryItemInstance inst)
        {
            if (inst == null || _itemsLayer == null) return;

            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(inst.Id) : null;
            CabalInventoryGrid.GetFootprint(inst.Id, out int w, out int h);

            float pxW = w * CellSize + (w - 1) * CellGap;
            float pxH = h * CellSize + (h - 1) * CellGap;

            var go = new GameObject($"Item_{inst.Id}", typeof(RectTransform), typeof(Image), typeof(Outline));
            go.transform.SetParent(_itemsLayer, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = CellToLocalTopLeft(inst.GridX, inst.GridY);
            rect.sizeDelta = new Vector2(pxW, pxH);

            Color gradeAccent = GetGradeColor(def);
            var bg = go.GetComponent<Image>();
            bg.color = new Color(gradeAccent.r * 0.22f + 0.08f, gradeAccent.g * 0.22f + 0.09f, gradeAccent.b * 0.22f + 0.11f, 0.92f);

            var outline = go.GetComponent<Outline>();
            outline.effectColor = new Color(gradeAccent.r, gradeAccent.g, gradeAccent.b, 0.78f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            // 3D Icon Image
            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGo.transform.SetParent(go.transform, false);
            var iRect = iconGo.GetComponent<RectTransform>();
            iRect.anchorMin = Vector2.zero;
            iRect.anchorMax = Vector2.one;
            iRect.offsetMin = new Vector2(6f, 10f);
            iRect.offsetMax = new Vector2(-6f, -10f);
            var iImg = iconGo.GetComponent<Image>();
            iImg.sprite = def != null ? def.Icon : null;
            iImg.preserveAspect = true;
            iImg.color = Color.white;
            iImg.raycastTarget = false;

            // Top-left tag
            string shortTag = def != null && !string.IsNullOrEmpty(def.ShortTag) ? def.ShortTag : inst.Id.ToString();
            CreateLabel("Tag", go.transform, shortTag, 9, FontStyle.Bold, TextAnchor.UpperLeft, gradeAccent,
                new Vector2(4f, -3f), new Vector2(pxW - 8f, 16f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            // Bottom-right count if > 1
            if (inst.StackCount > 1)
            {
                CreateLabel("Stack", go.transform, $"x{inst.StackCount}", 11, FontStyle.Bold, TextAnchor.LowerRight, Color.white,
                    new Vector2(-4f, 3f), new Vector2(pxW - 8f, 18f), new Vector2(1f, 0f), new Vector2(1f, 0f));
            }

            var widget = go.AddComponent<InventoryItemWidgetHandler>();
            widget.Bind(this, inst);
            _spawnedItemWidgets.Add(go);
        }

        private static Color GetGradeColor(ItemDefinition def)
        {
            if (def == null) return new Color(0.65f, 0.68f, 0.72f, 1f);
            if (def.Category == ItemCategory.GunPart) return new Color(0.92f, 0.56f, 0.22f, 1f);
            if (def.Category == ItemCategory.Ammo) return new Color(0.84f, 0.73f, 0.30f, 1f);
            return def.Grade switch
            {
                ItemGrade.Great => new Color(0.95f, 0.76f, 0.24f, 1f),
                ItemGrade.Good => new Color(0.26f, 0.78f, 0.92f, 1f),
                _ => new Color(0.68f, 0.71f, 0.76f, 1f)
            };
        }

        public void HandleItemRightClick(InventoryItemInstance inst)
        {
            if (inst == null || _boundInventory == null) return;
            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(inst.Id) : null;
            if (def != null && def.IsConsumable)
            {
                _boundInventory.RequestUseItem(inst.InstanceGuid);
            }
        }

        public void HandleBeginDrag(InventoryItemInstance inst, PointerEventData eventData)
        {
            if (inst == null) return;
            _draggedItem = inst;

            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(inst.Id) : null;
            CabalInventoryGrid.GetFootprint(inst.Id, out int w, out int h);

            ClearDragGhost();
            _dragGhost = new GameObject("DragGhost", typeof(RectTransform), typeof(Image));
            _dragGhost.transform.SetParent(_canvas != null ? _canvas.transform : transform, false);
            _dragGhost.transform.SetAsLastSibling();
            var gRect = _dragGhost.GetComponent<RectTransform>();
            gRect.pivot = new Vector2(0.5f, 0.5f);
            gRect.sizeDelta = new Vector2(w * CellSize + (w - 1) * CellGap, h * CellSize + (h - 1) * CellGap);
            gRect.position = eventData.position;

            var gImg = _dragGhost.GetComponent<Image>();
            gImg.sprite = def != null ? def.Icon : null;
            gImg.preserveAspect = true;
            gImg.color = new Color(1f, 1f, 1f, 0.84f);
            gImg.raycastTarget = false;
        }

        public void HandleDrag(PointerEventData eventData)
        {
            if (_draggedItem == null) return;

            bool insideGrid = TryScreenPosToGridCell(eventData.position, _draggedItem.Id, out int gx, out int gy);

            if (_dragGhost != null)
            {
                _dragGhost.GetComponent<RectTransform>().position = eventData.position;
                var ghostImg = _dragGhost.GetComponent<Image>();
                if (ghostImg != null)
                {
                    ghostImg.color = insideGrid
                        ? new Color(1f, 1f, 1f, 0.84f)
                        : new Color(1f, 0.45f, 0.25f, 0.86f);
                }
            }

            if (insideGrid)
            {
                ShowPreviewAt(gx, gy, _draggedItem);
            }
            else
            {
                HidePreviewCells();
            }
        }

        public void HandleEndDrag(PointerEventData eventData)
        {
            HidePreviewCells();
            ClearDragGhost();

            if (_draggedItem == null || _boundInventory == null)
            {
                _draggedItem = null;
                return;
            }

            var inst = _draggedItem;
            _draggedItem = null;

            if (TryScreenPosToGridCell(eventData.position, inst.Id, out int gx, out int gy))
            {
                CabalInventoryGrid.GetFootprint(inst.Id, out int w, out int h);
                if (_boundInventory.Grid.CanPlaceAt(gx, gy, w, h, inst))
                {
                    _boundInventory.RequestMoveItem(inst.InstanceGuid, gx, gy);
                }
                return;
            }

            // Released outside the 6x5 inventory grid -> drop onto ground as WorldItemPickup
            _boundInventory.RequestDropItem(inst.InstanceGuid);
        }

        private bool TryScreenPosToGridCell(Vector2 screenPos, ItemId id, out int gx, out int gy)
        {
            gx = -1;
            gy = -1;
            if (_gridContainer == null) return false;

            Camera cam = _canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay ? _canvas.worldCamera : null;
            if (!RectTransformUtility.RectangleContainsScreenPoint(_gridContainer, screenPos, cam))
            {
                return false;
            }

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_gridContainer, screenPos, cam, out Vector2 localPoint))
            {
                return false;
            }

            // Use _gridContainer.rect directly so top-left origin works accurately regardless of RectTransform pivot.
            float topLeftX = localPoint.x - _gridContainer.rect.xMin;
            float topLeftY = _gridContainer.rect.yMax - localPoint.y;

            CabalInventoryGrid.GetFootprint(id, out int fw, out int fh);
            float step = CellSize + CellGap;
            gx = Mathf.FloorToInt((topLeftX - ((fw - 1) * step * 0.5f)) / step);
            gy = Mathf.FloorToInt((topLeftY - ((fh - 1) * step * 0.5f)) / step);

            gx = Mathf.Clamp(gx, 0, CabalInventoryGrid.Columns - fw);
            gy = Mathf.Clamp(gy, 0, CabalInventoryGrid.Rows - fh);
            return true;
        }

        private void ShowPreviewAt(int startX, int startY, InventoryItemInstance inst)
        {
            HidePreviewCells();
            CabalInventoryGrid.GetFootprint(inst.Id, out int w, out int h);
            bool valid = _boundInventory != null && _boundInventory.Grid.CanPlaceAt(startX, startY, w, h, inst);
            Color tint = valid ? new Color(0.18f, 0.85f, 0.42f, 0.42f) : new Color(0.92f, 0.22f, 0.22f, 0.42f);

            for (int x = startX; x < startX + w; x++)
            {
                for (int y = startY; y < startY + h; y++)
                {
                    if (x >= 0 && x < CabalInventoryGrid.Columns && y >= 0 && y < CabalInventoryGrid.Rows)
                    {
                        _previewCells[x, y].color = tint;
                        _previewCells[x, y].enabled = true;
                    }
                }
            }
        }

        private void HidePreviewCells()
        {
            for (int x = 0; x < CabalInventoryGrid.Columns; x++)
            {
                for (int y = 0; y < CabalInventoryGrid.Rows; y++)
                {
                    if (_previewCells[x, y] != null) _previewCells[x, y].enabled = false;
                }
            }
        }

        private void ClearDragGhost()
        {
            if (_dragGhost != null)
            {
                Destroy(_dragGhost);
                _dragGhost = null;
            }
        }

        private Text CreateLabel(string name, Transform parent, string content, int size, FontStyle style, TextAnchor align, Color col,
            Vector2 pos, Vector2 sizeDelta, Vector2 anchorMin, Vector2 anchorMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = anchorMin;
            rect.anchoredPosition = pos;
            rect.sizeDelta = sizeDelta;
            var txt = go.GetComponent<Text>();
            txt.font = _font;
            txt.text = content;
            txt.fontSize = size;
            txt.fontStyle = style;
            txt.alignment = align;
            txt.color = col;
            txt.raycastTarget = false;
            return txt;
        }

        private static void EnsureEventSystemExists()
        {
            if (EventSystem.current == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
                DontDestroyOnLoad(es);
            }
        }

        private class InventoryItemWidgetHandler : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            private ScorchedInventoryUI _owner;
            private InventoryItemInstance _inst;

            public void Bind(ScorchedInventoryUI owner, InventoryItemInstance inst)
            {
                _owner = owner;
                _inst = inst;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Right)
                {
                    _owner?.HandleItemRightClick(_inst);
                }
            }

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left)
                {
                    _owner?.HandleBeginDrag(_inst, eventData);
                }
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left)
                {
                    _owner?.HandleDrag(eventData);
                }
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Left)
                {
                    _owner?.HandleEndDrag(eventData);
                }
            }
        }
    }
}

