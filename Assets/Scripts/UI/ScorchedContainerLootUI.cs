using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Scorched.Inventory;

namespace Scorched.UI
{
    /// <summary>
    /// Compact tactical loot popup shown when pressing E on a Supply Box, Trash Container, or Supply Crate.
    /// Players can right-click individual items to transfer them to their backpack or click "LOOT ALL".
    /// </summary>
    [DisallowMultipleComponent]
    public class ScorchedContainerLootUI : MonoBehaviour
    {
        public static ScorchedContainerLootUI Instance { get; private set; }

        private Font _font;
        private Sprite _frameSprite;

        private GameObject _rootPanel;
        private RectTransform _panelRect;
        private Text _titleText;
        private Text _countText;
        private Text _emptyStatusText;
        private RectTransform _listContainer;
        private RectTransform _contentContainer;
        private Button _lootAllButton;

        private readonly List<GameObject> _spawnedRows = new List<GameObject>();

        private LootContainerInteractable _boundContainer;
        private PlayerInventoryController _boundInventory;
        private bool _isVisible;

        public bool IsVisible => _isVisible;
        public LootContainerInteractable ActiveContainer => _boundContainer;

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
            UnbindContainer();
        }

        public void Initialize(Font font, Sprite frameSprite)
        {
            _font = font;
            _frameSprite = frameSprite;
            BuildUI();
            SetContainer(null);
        }

        public void SetContainer(LootContainerInteractable container)
        {
            UnbindContainer();
            _boundContainer = container;
            _isVisible = container != null;

            if (_rootPanel != null)
            {
                _rootPanel.SetActive(_isVisible);
            }

            if (_isVisible)
            {
                EnsureEventSystemExists();
                _boundInventory = PlayerInventoryController.LocalInstance;
                if (_boundContainer != null)
                {
                    _boundContainer.OnLootChanged += HandleContainerLootChanged;
                }
                UpdatePanelPlacement();
                RefreshAll();
            }
        }

        public void UpdatePanelPlacement()
        {
            if (_panelRect == null) return;
            // Side-by-side with Backpack (-265) when B is open; centered cleanly when only looting.
            _panelRect.anchoredPosition = ScorchedInputGates.IsBackpackOpen
                ? new Vector2(270f, 0f)
                : Vector2.zero;
        }

        private void Update()
        {
            if (!_isVisible) return;

            if (_boundInventory == null)
            {
                _boundInventory = PlayerInventoryController.LocalInstance;
            }

            UpdatePanelPlacement();

            if (_boundContainer == null)
            {
                PlayerScavengeInteractor.CloseActiveContainer();
                return;
            }
        }

        private void UnbindContainer()
        {
            if (_boundContainer != null)
            {
                _boundContainer.OnLootChanged -= HandleContainerLootChanged;
                _boundContainer = null;
            }
        }

        private void HandleContainerLootChanged()
        {
            if (!_isVisible || _boundContainer == null) return;

            if (_boundContainer.HasRolledLoot && _boundContainer.LootItems.Count == 0)
            {
                PlayerScavengeInteractor.CloseActiveContainer();
                return;
            }

            RefreshAll();
        }

        private void BuildUI()
        {
            if (_rootPanel != null) Destroy(_rootPanel);

            _rootPanel = new GameObject("ContainerLootPanel", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            _rootPanel.transform.SetParent(transform, false);
            _panelRect = _rootPanel.GetComponent<RectTransform>();
            _panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            _panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            _panelRect.pivot = new Vector2(0.5f, 0.5f);
            _panelRect.anchoredPosition = Vector2.zero;
            _panelRect.sizeDelta = new Vector2(348f, 368f);

            var bg = _rootPanel.GetComponent<Image>();
            bg.sprite = _frameSprite;
            bg.type = _frameSprite != null && _frameSprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
            bg.color = new Color(0.08f, 0.09f, 0.11f, 0.95f);

            // Header
            _titleText = CreateLabel("Title", _rootPanel.transform, "SUPPLY BOX", 13, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Color(0.95f, 0.78f, 0.30f, 1f), new Vector2(18f, -14f), new Vector2(210f, 24f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            _countText = CreateLabel("Count", _rootPanel.transform, "0 ITEMS", 10, FontStyle.Bold, TextAnchor.MiddleRight,
                new Color(0.72f, 0.76f, 0.82f, 1f), new Vector2(-48f, -14f), new Vector2(80f, 24f), new Vector2(1f, 1f), new Vector2(1f, 1f));

            // Close [X] Button
            var closeGo = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
            closeGo.transform.SetParent(_rootPanel.transform, false);
            var closeRect = closeGo.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1f, 1f);
            closeRect.anchorMax = new Vector2(1f, 1f);
            closeRect.pivot = new Vector2(1f, 1f);
            closeRect.anchoredPosition = new Vector2(-14f, -14f);
            closeRect.sizeDelta = new Vector2(24f, 24f);
            closeGo.GetComponent<Image>().color = new Color(0.22f, 0.14f, 0.14f, 0.92f);
            closeGo.GetComponent<Button>().onClick.AddListener(PlayerScavengeInteractor.CloseActiveContainer);
            CreateLabel("X", closeGo.transform, "X", 11, FontStyle.Bold, TextAnchor.MiddleCenter,
                Color.white, Vector2.zero, new Vector2(24f, 24f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            // Item list frame (scrollable for multi-item containers and Death Bags)
            var listBg = new GameObject("ItemsListContainer", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
            listBg.transform.SetParent(_rootPanel.transform, false);
            _listContainer = listBg.GetComponent<RectTransform>();
            _listContainer.anchorMin = new Vector2(0.5f, 1f);
            _listContainer.anchorMax = new Vector2(0.5f, 1f);
            _listContainer.pivot = new Vector2(0.5f, 1f);
            _listContainer.anchoredPosition = new Vector2(0f, -48f);
            _listContainer.sizeDelta = new Vector2(316f, 232f);
            listBg.GetComponent<Image>().color = new Color(0.05f, 0.06f, 0.08f, 0.86f);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(_listContainer, false);
            _contentContainer = contentGo.GetComponent<RectTransform>();
            _contentContainer.anchorMin = new Vector2(0f, 1f);
            _contentContainer.anchorMax = new Vector2(1f, 1f);
            _contentContainer.pivot = new Vector2(0.5f, 1f);
            _contentContainer.anchoredPosition = Vector2.zero;
            _contentContainer.sizeDelta = new Vector2(0f, 232f);

            var scroll = listBg.GetComponent<ScrollRect>();
            scroll.viewport = _listContainer;
            scroll.content = _contentContainer;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;

            _emptyStatusText = CreateLabel("EmptyStatus", _listContainer, "OPENING CONTAINER...", 11, FontStyle.Bold, TextAnchor.MiddleCenter,
                new Color(0.65f, 0.70f, 0.76f, 1f), Vector2.zero, new Vector2(290f, 40f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            // Loot All button
            var btnGo = new GameObject("LootAllButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            btnGo.transform.SetParent(_rootPanel.transform, false);
            var btnRect = btnGo.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0.5f, 0f);
            btnRect.anchorMax = new Vector2(0.5f, 0f);
            btnRect.pivot = new Vector2(0.5f, 0f);
            btnRect.anchoredPosition = new Vector2(0f, 36f);
            btnRect.sizeDelta = new Vector2(316f, 34f);

            var btnImg = btnGo.GetComponent<Image>();
            btnImg.color = new Color(0.16f, 0.44f, 0.27f, 0.96f);

            var btnOutline = btnGo.GetComponent<Outline>();
            btnOutline.effectColor = new Color(0.32f, 0.82f, 0.48f, 0.80f);
            btnOutline.effectDistance = new Vector2(1f, -1f);

            _lootAllButton = btnGo.GetComponent<Button>();
            var colors = _lootAllButton.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            _lootAllButton.colors = colors;
            _lootAllButton.onClick.AddListener(HandleLootAllClicked);

            CreateLabel("LootAllLabel", btnGo.transform, "LOOT ALL", 12, FontStyle.Bold, TextAnchor.MiddleCenter,
                Color.white, Vector2.zero, new Vector2(300f, 30f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            // Hint footer
            CreateLabel("FooterHint", _rootPanel.transform, "R-CLICK: TAKE ITEM   |   B: BACKPACK   |   E / ESC: CLOSE",
                9, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.62f, 0.66f, 0.72f, 1f),
                new Vector2(0f, 12f), new Vector2(328f, 18f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
        }

        public void RefreshAll()
        {
            for (int i = 0; i < _spawnedRows.Count; i++)
            {
                if (_spawnedRows[i] != null) Destroy(_spawnedRows[i]);
            }
            _spawnedRows.Clear();

            if (_boundContainer == null)
            {
                if (_emptyStatusText != null) _emptyStatusText.gameObject.SetActive(false);
                return;
            }

            if (_titleText != null) _titleText.text = _boundContainer.GetContainerTitle();

            var items = _boundContainer.LootItems;
            int count = items != null ? items.Count : 0;
            if (_countText != null)
            {
                _countText.text = count == 1 ? "1 ITEM" : $"{count} ITEMS";
            }

            if (!_boundContainer.HasRolledLoot)
            {
                if (_emptyStatusText != null)
                {
                    _emptyStatusText.text = "OPENING CONTAINER...";
                    _emptyStatusText.gameObject.SetActive(true);
                }
                if (_lootAllButton != null) _lootAllButton.interactable = false;
                return;
            }

            if (count == 0)
            {
                if (_emptyStatusText != null)
                {
                    _emptyStatusText.text = "EMPTY";
                    _emptyStatusText.gameObject.SetActive(true);
                }
                if (_lootAllButton != null) _lootAllButton.interactable = false;
                return;
            }

            if (_emptyStatusText != null) _emptyStatusText.gameObject.SetActive(false);
            if (_lootAllButton != null) _lootAllButton.interactable = true;

            float rowH = 44f;
            float rowGap = 4f;
            if (_contentContainer != null)
            {
                float totalH = Mathf.Max(232f, 8f + count * (rowH + rowGap));
                _contentContainer.sizeDelta = new Vector2(0f, totalH);
                _contentContainer.anchoredPosition = Vector2.zero;
            }
            for (int i = 0; i < count; i++)
            {
                CreateLootRow(items[i], i, rowH, rowGap);
            }
        }

        private void CreateLootRow(ContainerLootEntry entry, int index, float rowH, float rowGap)
        {
            var def = ScorchedItemCatalog.Instance != null ? ScorchedItemCatalog.Instance.GetDefinition(entry.Id) : null;
            CabalInventoryGrid.GetFootprint(entry.Id, out int gw, out int gh);
            Color accent = GetGradeColor(def);

            var rowGo = new GameObject($"LootRow_{index}_{entry.Id}", typeof(RectTransform), typeof(Image), typeof(Outline));
            rowGo.transform.SetParent(_contentContainer != null ? _contentContainer : _listContainer, false);
            var rect = rowGo.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -(4f + index * (rowH + rowGap)));
            rect.sizeDelta = new Vector2(304f, rowH);

            var bg = rowGo.GetComponent<Image>();
            bg.color = new Color(accent.r * 0.14f + 0.08f, accent.g * 0.14f + 0.09f, accent.b * 0.14f + 0.12f, 0.94f);

            var outline = rowGo.GetComponent<Outline>();
            outline.effectColor = new Color(accent.r, accent.g, accent.b, 0.65f);
            outline.effectDistance = new Vector2(1f, -1f);

            // Icon
            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGo.transform.SetParent(rowGo.transform, false);
            var iRect = iconGo.GetComponent<RectTransform>();
            iRect.anchorMin = new Vector2(0f, 0.5f);
            iRect.anchorMax = new Vector2(0f, 0.5f);
            iRect.pivot = new Vector2(0f, 0.5f);
            iRect.anchoredPosition = new Vector2(6f, 0f);
            iRect.sizeDelta = new Vector2(34f, 34f);
            var iImg = iconGo.GetComponent<Image>();
            iImg.sprite = def != null ? def.Icon : null;
            iImg.preserveAspect = true;
            iImg.color = Color.white;
            iImg.raycastTarget = false;

            string displayName = def != null ? def.DisplayName : entry.Id.ToString();
            if (entry.Count > 1) displayName = $"{displayName}  x{entry.Count}";

            CreateLabel("Name", rowGo.transform, displayName, 11, FontStyle.Bold, TextAnchor.MiddleLeft,
                Color.white, new Vector2(46f, 7f), new Vector2(180f, 20f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));

            string meta = $"{gw}x{gh} SLOT{(gw * gh > 1 ? "S" : "")}";
            CreateLabel("Meta", rowGo.transform, meta, 9, FontStyle.Bold, TextAnchor.MiddleLeft,
                accent, new Vector2(46f, -9f), new Vector2(160f, 16f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f));

            CreateLabel("TakeHint", rowGo.transform, "R-CLICK", 9, FontStyle.Bold, TextAnchor.MiddleRight,
                new Color(0.75f, 0.80f, 0.86f, 0.90f), new Vector2(-10f, 0f), new Vector2(64f, 20f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f));

            var handler = rowGo.AddComponent<ContainerLootRowHandler>();
            handler.Bind(this, entry.EntryGuid, bg, bg.color);

            _spawnedRows.Add(rowGo);
        }

        private void HandleRowRightClick(string entryGuid)
        {
            if (_boundContainer == null || _boundInventory == null || string.IsNullOrEmpty(entryGuid)) return;
            _boundInventory.RequestTakeContainerItem(_boundContainer, entryGuid);
        }

        private void HandleLootAllClicked()
        {
            if (_boundContainer == null || _boundInventory == null) return;
            _boundInventory.RequestLootAllContainer(_boundContainer);
        }

        private static Color GetGradeColor(ItemDefinition def)
        {
            if (def == null) return new Color(0.68f, 0.72f, 0.78f, 1f);
            if (def.Category == ItemCategory.Weapon) return new Color(0.95f, 0.36f, 0.24f, 1f);
            if (def.Category == ItemCategory.GunPart) return new Color(0.92f, 0.56f, 0.22f, 1f);
            if (def.Category == ItemCategory.Ammo) return new Color(0.84f, 0.73f, 0.30f, 1f);
            return def.Grade switch
            {
                ItemGrade.Great => new Color(0.95f, 0.76f, 0.24f, 1f),
                ItemGrade.Good => new Color(0.26f, 0.78f, 0.92f, 1f),
                _ => new Color(0.68f, 0.71f, 0.76f, 1f)
            };
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

        private class ContainerLootRowHandler : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
        {
            private ScorchedContainerLootUI _owner;
            private string _entryGuid;
            private Image _bg;
            private Color _baseColor;

            public void Bind(ScorchedContainerLootUI owner, string entryGuid, Image bg, Color baseColor)
            {
                _owner = owner;
                _entryGuid = entryGuid;
                _bg = bg;
                _baseColor = baseColor;
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button == PointerEventData.InputButton.Right)
                {
                    _owner?.HandleRowRightClick(_entryGuid);
                }
            }

            public void OnPointerEnter(PointerEventData eventData)
            {
                if (_bg != null) _bg.color = _baseColor * 1.35f;
            }

            public void OnPointerExit(PointerEventData eventData)
            {
                if (_bg != null) _bg.color = _baseColor;
            }
        }
    }
}

