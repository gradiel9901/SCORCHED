using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Scorched.Inventory;
using Scorched.Weapons;

namespace Scorched.UI
{
    /// <summary>
    /// Side-by-side Dark Tech-Brutalist Crafting & Grade Synthesis UI.
    /// Section A: Weapon Assembly (4 AK-47 Parts + >= 5 Ammo -> equips AK-47 Tactical).
    /// Section B: Consumable Grade Synthesis (3 identical consumable grades -> 1 higher grade).
    /// </summary>
    [DisallowMultipleComponent]
    public class ScorchedCraftingUI : MonoBehaviour
    {
        public static ScorchedCraftingUI Instance { get; private set; }

        private static readonly ItemId[] UpgradeableSources = new[]
        {
            ItemId.Water_Poor, ItemId.Water_Good,
            ItemId.Bandage_Poor, ItemId.Bandage_Good,
            ItemId.Food_Poor, ItemId.Food_Good
        };

        private static readonly ItemId[] AKPartOrder = new[]
        {
            ItemId.AK47_Stock, ItemId.AK47_Receiver, ItemId.AK47_Barrel, ItemId.AK47_Bolt
        };

        private static readonly string[] AKPartLabels = new[]
        {
            "STOCK", "RECEIVER", "BARREL", "BOLT+MAG"
        };

        private Font _font;
        private Sprite _frameSprite;

        private GameObject _rootPanel;
        private PlayerInventoryController _boundInventory;

        // Section A: Weapon Assembly UI
        private readonly Image[] _partBgImgs = new Image[4];
        private readonly Image[] _partIconImgs = new Image[4];
        private readonly Text[] _partStatusTexts = new Text[4];
        private Text _weaponReqStatusText;
        private Button _autoFillButton;
        private Button _craftAkButton;
        private Text _craftAkButtonText;

        // Section B: Grade Synthesis UI
        private readonly List<Button> _sourcePickButtons = new List<Button>();
        private readonly List<Image> _sourcePickBgs = new List<Image>();
        private readonly List<Text> _sourcePickCounts = new List<Text>();
        private readonly Image[] _synthInIcons = new Image[3];
        private readonly Image[] _synthInBgs = new Image[3];
        private Image _synthOutIcon;
        private Image _synthOutBg;
        private Text _synthOutLabel;
        private Button _synthesizeButton;
        private Text _synthesizeButtonText;

        private ItemId _selectedSynthSource = ItemId.Water_Poor;
        private bool _autoFilledParts = false;
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

        public void Initialize(Font font, Sprite frameSprite)
        {
            _font = font;
            _frameSprite = frameSprite;
            BuildUI();
            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            _isVisible = visible;
            if (_rootPanel != null) _rootPanel.SetActive(visible);
            if (visible)
            {
                AutoSelectBestSynthSource();
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
                AutoSelectBestSynthSource();
                RefreshAll();
            }
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

        private void AutoSelectBestSynthSource()
        {
            if (_boundInventory == null) return;
            for (int i = 0; i < UpgradeableSources.Length; i++)
            {
                if (_boundInventory.Grid.CountTotalAmount(UpgradeableSources[i]) >= 3)
                {
                    _selectedSynthSource = UpgradeableSources[i];
                    return;
                }
            }
            for (int i = 0; i < UpgradeableSources.Length; i++)
            {
                if (_boundInventory.Grid.CountTotalAmount(UpgradeableSources[i]) > 0)
                {
                    _selectedSynthSource = UpgradeableSources[i];
                    return;
                }
            }
        }

        private void BuildUI()
        {
            if (_rootPanel != null) Destroy(_rootPanel);

            // Side-by-side Right Panel (440x560)
            _rootPanel = new GameObject("CraftingPanel_Tactical", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            _rootPanel.transform.SetParent(transform, false);
            var panelRect = _rootPanel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = new Vector2(235f, 0f);
            panelRect.sizeDelta = new Vector2(440f, 560f);

            var bg = _rootPanel.GetComponent<Image>();
            bg.sprite = _frameSprite;
            bg.type = _frameSprite != null && _frameSprite.border.sqrMagnitude > 0f ? Image.Type.Sliced : Image.Type.Simple;
            bg.color = new Color(0.08f, 0.09f, 0.11f, 0.94f);

            // Header
            CreateLabel("CraftTitle", _rootPanel.transform, "WORKBENCH // ASSEMBLY & SYNTHESIS", 13, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Color(0.92f, 0.94f, 0.96f, 1f), new Vector2(22f, -18f), new Vector2(380f, 24f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            // SECTION A: WEAPON ASSEMBLY (Top band Y: -50..-295)
            CreateLabel("SecAHeader", _rootPanel.transform, "SECTION A // AK-47 FIELD ASSEMBLY", 11, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Color(0.95f, 0.72f, 0.24f, 1f), new Vector2(22f, -50f), new Vector2(396f, 20f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            var catalog = ScorchedItemCatalog.Instance;
            for (int i = 0; i < 4; i++)
            {
                float xPos = 24f + i * 100f;
                var slotGo = new GameObject($"AKPartSlot_{i}", typeof(RectTransform), typeof(Image));
                slotGo.transform.SetParent(_rootPanel.transform, false);
                var sRect = slotGo.GetComponent<RectTransform>();
                sRect.anchorMin = new Vector2(0f, 1f);
                sRect.anchorMax = new Vector2(0f, 1f);
                sRect.pivot = new Vector2(0f, 1f);
                sRect.anchoredPosition = new Vector2(xPos, -78f);
                sRect.sizeDelta = new Vector2(90f, 102f);
                _partBgImgs[i] = slotGo.GetComponent<Image>();
                _partBgImgs[i].color = new Color(0.13f, 0.15f, 0.18f, 0.95f);

                var def = catalog != null ? catalog.GetDefinition(AKPartOrder[i]) : null;
                var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGo.transform.SetParent(slotGo.transform, false);
                var iRect = iconGo.GetComponent<RectTransform>();
                iRect.anchorMin = new Vector2(0.5f, 1f);
                iRect.anchorMax = new Vector2(0.5f, 1f);
                iRect.pivot = new Vector2(0.5f, 1f);
                iRect.anchoredPosition = new Vector2(0f, -8f);
                iRect.sizeDelta = new Vector2(64f, 64f);
                _partIconImgs[i] = iconGo.GetComponent<Image>();
                _partIconImgs[i].sprite = def != null ? def.Icon : null;
                _partIconImgs[i].preserveAspect = true;
                _partIconImgs[i].raycastTarget = false;

                CreateLabel("Name", slotGo.transform, AKPartLabels[i], 9, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white,
                    new Vector2(0f, 16f), new Vector2(86f, 16f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));

                _partStatusTexts[i] = CreateLabel("State", slotGo.transform, "MISSING", 8, FontStyle.Bold, TextAnchor.MiddleCenter,
                    new Color(0.65f, 0.68f, 0.72f, 1f), new Vector2(0f, 3f), new Vector2(86f, 14f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            }

            _weaponReqStatusText = CreateLabel("WeaponReqLabel", _rootPanel.transform, "PARTS: 0/4   |   AMMO: 0 / 5 MIN", 11, FontStyle.Bold, TextAnchor.MiddleCenter,
                new Color(0.84f, 0.87f, 0.90f, 1f), new Vector2(0f, -190f), new Vector2(396f, 22f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f));

            _autoFillButton = CreateButton("AutoFillPartsBtn", _rootPanel.transform, "AUTO-FILL PARTS", new Vector2(-102f, -238f), new Vector2(188f, 38f),
                new Color(0.18f, 0.22f, 0.28f, 1f), () => { _autoFilledParts = true; RefreshAll(); });

            _craftAkButton = CreateButton("CraftAK47Btn", _rootPanel.transform, "CRAFT AK-47", new Vector2(102f, -238f), new Vector2(188f, 38f),
                new Color(0.75f, 0.45f, 0.12f, 1f), () => _boundInventory?.RequestCraftAK47());
            _craftAkButtonText = _craftAkButton.GetComponentInChildren<Text>();

            // Divider
            var div = new GameObject("Divider", typeof(RectTransform), typeof(Image));
            div.transform.SetParent(_rootPanel.transform, false);
            var dRect = div.GetComponent<RectTransform>();
            dRect.anchorMin = new Vector2(0.5f, 1f);
            dRect.anchorMax = new Vector2(0.5f, 1f);
            dRect.pivot = new Vector2(0.5f, 1f);
            dRect.anchoredPosition = new Vector2(0f, -274f);
            dRect.sizeDelta = new Vector2(396f, 1f);
            div.GetComponent<Image>().color = new Color(0.32f, 0.35f, 0.40f, 0.85f);

            // SECTION B: GRADE SYNTHESIS (Y: -286..-540)
            CreateLabel("SecBHeader", _rootPanel.transform, "SECTION B // GRADE SYNTHESIS (3 -> 1 UPGRADE)", 11, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Color(0.28f, 0.78f, 0.94f, 1f), new Vector2(22f, -286f), new Vector2(396f, 20f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            // 6 clickable source chips (2 rows of 3)
            for (int i = 0; i < UpgradeableSources.Length; i++)
            {
                ItemId srcId = UpgradeableSources[i];
                int col = i % 3;
                int row = i / 3;
                float bx = 24f + col * 133f;
                float by = -312f - row * 40f;

                var chipDef = catalog != null ? catalog.GetDefinition(srcId) : null;
                string chipLabel = chipDef != null ? chipDef.ShortTag : srcId.ToString();

                var btn = CreateTopLeftButton($"SrcChip_{srcId}", _rootPanel.transform, $"{chipLabel} (0)", new Vector2(bx, by), new Vector2(126f, 34f),
                    new Color(0.14f, 0.16f, 0.19f, 1f), () => { _selectedSynthSource = srcId; RefreshAll(); });
                _sourcePickButtons.Add(btn);
                _sourcePickBgs.Add(btn.GetComponent<Image>());
                _sourcePickCounts.Add(btn.GetComponentInChildren<Text>());
            }

            // 3 Input Slots + Output Slot row (Y: -400)
            for (int i = 0; i < 3; i++)
            {
                float sx = 24f + i * 76f;
                var inGo = new GameObject($"SynthIn_{i}", typeof(RectTransform), typeof(Image));
                inGo.transform.SetParent(_rootPanel.transform, false);
                var r = inGo.GetComponent<RectTransform>();
                r.anchorMin = new Vector2(0f, 1f);
                r.anchorMax = new Vector2(0f, 1f);
                r.pivot = new Vector2(0f, 1f);
                r.anchoredPosition = new Vector2(sx, -402f);
                r.sizeDelta = new Vector2(68f, 68f);
                _synthInBgs[i] = inGo.GetComponent<Image>();
                _synthInBgs[i].color = new Color(0.13f, 0.15f, 0.18f, 0.95f);

                var icGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                icGo.transform.SetParent(inGo.transform, false);
                var ir = icGo.GetComponent<RectTransform>();
                ir.anchorMin = Vector2.zero;
                ir.anchorMax = Vector2.one;
                ir.offsetMin = new Vector2(6f, 6f);
                ir.offsetMax = new Vector2(-6f, -6f);
                _synthInIcons[i] = icGo.GetComponent<Image>();
                _synthInIcons[i].preserveAspect = true;
                _synthInIcons[i].raycastTarget = false;
            }

            CreateLabel("Arrow", _rootPanel.transform, ">>", 16, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white,
                new Vector2(262f, -402f), new Vector2(44f, 68f), new Vector2(0f, 1f), new Vector2(0f, 1f));

            var outGo = new GameObject("SynthOutput", typeof(RectTransform), typeof(Image));
            outGo.transform.SetParent(_rootPanel.transform, false);
            var orRect = outGo.GetComponent<RectTransform>();
            orRect.anchorMin = new Vector2(0f, 1f);
            orRect.anchorMax = new Vector2(0f, 1f);
            orRect.pivot = new Vector2(0f, 1f);
            orRect.anchoredPosition = new Vector2(320f, -402f);
            orRect.sizeDelta = new Vector2(88f, 88f);
            _synthOutBg = outGo.GetComponent<Image>();
            _synthOutBg.color = new Color(0.14f, 0.22f, 0.26f, 0.95f);

            var outIcGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            outIcGo.transform.SetParent(outGo.transform, false);
            var oir = outIcGo.GetComponent<RectTransform>();
            oir.anchorMin = Vector2.zero;
            oir.anchorMax = Vector2.one;
            oir.offsetMin = new Vector2(6f, 16f);
            oir.offsetMax = new Vector2(-6f, -6f);
            _synthOutIcon = outIcGo.GetComponent<Image>();
            _synthOutIcon.preserveAspect = true;
            _synthOutIcon.raycastTarget = false;

            _synthOutLabel = CreateLabel("OutTag", outGo.transform, "UPGRADED", 9, FontStyle.Bold, TextAnchor.MiddleCenter,
                new Color(0.38f, 0.88f, 0.96f, 1f), new Vector2(0f, 2f), new Vector2(84f, 16f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));

            _synthesizeButton = CreateButton("SynthesizeBtn", _rootPanel.transform, "SYNTHESIZE / UPGRADE (3 -> 1)", new Vector2(0f, -518f), new Vector2(392f, 40f),
                new Color(0.14f, 0.48f, 0.62f, 1f), () => _boundInventory?.RequestSynthesizeUpgrade(_selectedSynthSource));
            _synthesizeButtonText = _synthesizeButton.GetComponentInChildren<Text>();
        }

        public void RefreshAll()
        {
            if (_rootPanel == null) return;

            var catalog = ScorchedItemCatalog.Instance;
            bool armed = false;
            if (_boundInventory != null)
            {
                var wpn = _boundInventory.GetComponent<NetworkWeaponController>();
                armed = wpn != null && wpn.IsWeaponEquipped;
            }

            int ownedParts = 0;
            for (int i = 0; i < 4; i++)
            {
                bool hasPart = _boundInventory != null && _boundInventory.Grid.CountTotalAmount(AKPartOrder[i]) >= 1;
                if (hasPart) ownedParts++;

                bool highlight = hasPart && (_autoFilledParts || ownedParts > 0);
                _partBgImgs[i].color = highlight ? new Color(0.16f, 0.25f, 0.18f, 0.95f) : new Color(0.12f, 0.14f, 0.17f, 0.95f);
                _partIconImgs[i].color = hasPart ? Color.white : new Color(1f, 1f, 1f, 0.24f);
                _partStatusTexts[i].text = hasPart ? "READY" : "MISSING";
                _partStatusTexts[i].color = hasPart ? new Color(0.32f, 0.92f, 0.52f, 1f) : new Color(0.65f, 0.68f, 0.72f, 1f);
            }

            int ammo = _boundInventory != null ? _boundInventory.Grid.CountTotalAmount(ItemId.Ammo_762) : 0;
            bool canCraft = !armed && ownedParts == 4 && ammo >= 5;

            if (armed)
            {
                _weaponReqStatusText.text = "AK-47 TACTICAL ASSEMBLED & EQUIPPED";
                _weaponReqStatusText.color = new Color(0.35f, 0.92f, 0.55f, 1f);
                _craftAkButton.interactable = false;
                _craftAkButtonText.text = "EQUIPPED";
            }
            else
            {
                _weaponReqStatusText.text = $"PARTS: {ownedParts}/4   |   AMMO: {ammo} / 5 REQ";
                _weaponReqStatusText.color = canCraft ? new Color(0.35f, 0.92f, 0.55f, 1f) : new Color(0.84f, 0.87f, 0.90f, 1f);
                _craftAkButton.interactable = canCraft;
                _craftAkButtonText.text = "CRAFT AK-47";
            }

            // Section B: Grade Synthesis
            for (int i = 0; i < UpgradeableSources.Length; i++)
            {
                ItemId srcId = UpgradeableSources[i];
                int cnt = _boundInventory != null ? _boundInventory.Grid.CountTotalAmount(srcId) : 0;
                var def = catalog != null ? catalog.GetDefinition(srcId) : null;
                string tag = def != null ? def.ShortTag : srcId.ToString();
                _sourcePickCounts[i].text = $"{tag} ({cnt}/3)";
                bool selected = srcId == _selectedSynthSource;
                _sourcePickBgs[i].color = selected
                    ? new Color(0.16f, 0.38f, 0.52f, 1f)
                    : (cnt >= 3 ? new Color(0.16f, 0.24f, 0.22f, 1f) : new Color(0.12f, 0.14f, 0.17f, 1f));
            }

            int selectedCount = _boundInventory != null ? _boundInventory.Grid.CountTotalAmount(_selectedSynthSource) : 0;
            var srcDef = catalog != null ? catalog.GetDefinition(_selectedSynthSource) : null;
            ScorchedItemCatalog.CanSynthesizeUpgrade(_selectedSynthSource, out ItemId outId);
            var outDef = catalog != null ? catalog.GetDefinition(outId) : null;

            for (int i = 0; i < 3; i++)
            {
                bool filled = selectedCount > i;
                _synthInIcons[i].sprite = srcDef != null ? srcDef.Icon : null;
                _synthInIcons[i].color = filled ? Color.white : new Color(1f, 1f, 1f, 0.18f);
                _synthInBgs[i].color = filled ? new Color(0.15f, 0.24f, 0.30f, 0.95f) : new Color(0.11f, 0.13f, 0.16f, 0.95f);
            }

            bool readySynth = selectedCount >= 3 && outId != ItemId.None;
            _synthOutIcon.sprite = outDef != null ? outDef.Icon : null;
            _synthOutIcon.color = readySynth ? Color.white : new Color(1f, 1f, 1f, 0.35f);
            _synthOutLabel.text = outDef != null ? outDef.ShortTag : "UPGRADED";
            _synthesizeButton.interactable = readySynth;
            _synthesizeButtonText.text = readySynth ? $"SYNTHESIZE {(_synthOutLabel.text)}" : $"NEED 3x ({selectedCount}/3)";
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

        private Button CreateButton(string name, Transform parent, string label, Vector2 anchoredPos, Vector2 size, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.color = bgColor;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            CreateLabel("Label", go.transform, label, 11, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white,
                Vector2.zero, size, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            var txtRect = go.transform.Find("Label").GetComponent<RectTransform>();
            txtRect.pivot = new Vector2(0.5f, 0.5f);
            return btn;
        }

        private Button CreateTopLeftButton(string name, Transform parent, string label, Vector2 pos, Vector2 size, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.color = bgColor;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);
            var t = CreateLabel("Label", go.transform, label, 10, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white,
                Vector2.zero, size, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            t.GetComponent<RectTransform>().pivot = new Vector2(0.5f, 0.5f);
            return btn;
        }
    }
}

