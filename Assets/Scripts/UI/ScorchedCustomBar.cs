using UnityEngine;
using UnityEngine.UI;

namespace Scorched.UI
{
    /// <summary>
    /// Custom multi-layer tech-brutalist HUD bar component (zero native Unity Slider).
    /// Renders: (1) Recessed micro-grid TrackImage, (2) Delayed GhostFillImage trail,
    /// (3) Segmented/Razor MainFillImage, and (4) 3px LeadingEdgePip tracking the fill edge.
    /// </summary>
    [DisallowMultipleComponent]
    public class ScorchedCustomBar : MonoBehaviour
    {
        #region Serialized References & Tuning
        [Header("Layered UI Images")]
        [SerializeField] private Image _trackImage;
        [SerializeField] private Image _ghostFillImage;
        [SerializeField] private Image _mainFillImage;
        [SerializeField] private RectTransform _leadingEdgePip;
        [SerializeField] private Image _leadingEdgePipImage;

        [Header("Animation Tuning")]
        [SerializeField] private float _mainFillLerpSpeed = 12f;
        [SerializeField] private float _ghostDropDelay = 0.40f;
        [SerializeField] private float _ghostDrainSpeed = 0.65f;
        [SerializeField] private bool _showLeadingPip = true;
        #endregion

        #region Runtime State
        private float _targetFill = 1f;
        private float _currentMainFill = 1f;
        private float _currentGhostFill = 1f;
        private float _ghostDelayTimer = 0f;
        private int _lastDisplayedInt = int.MinValue;
        private bool _initialized = false;
        #endregion

        public float TargetFill => _targetFill;
        public float CurrentMainFill => _currentMainFill;

        private void Awake()
        {
            EnsureLayersBuilt();
        }

        private void Update()
        {
            if (!_initialized)
            {
                return;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            // 1. Main fill smooth interpolation
            if (!Mathf.Approximately(_currentMainFill, _targetFill))
            {
                _currentMainFill = Mathf.Lerp(_currentMainFill, _targetFill, Mathf.Clamp01(dt * _mainFillLerpSpeed));
                if (Mathf.Abs(_currentMainFill - _targetFill) < 0.001f)
                {
                    _currentMainFill = _targetFill;
                }
            }

            // 2. Trailing ghost bar with 0.4s delay before catching up on downward drops
            if (_currentGhostFill < _currentMainFill)
            {
                _currentGhostFill = _currentMainFill;
                _ghostDelayTimer = 0f;
            }
            else if (_currentGhostFill > _targetFill)
            {
                if (_ghostDelayTimer > 0f)
                {
                    _ghostDelayTimer -= dt;
                }
                else
                {
                    _currentGhostFill = Mathf.MoveTowards(_currentGhostFill, _targetFill, dt * _ghostDrainSpeed);
                }
            }

            ApplyVisualFill();
        }

        /// <summary>
        /// Builds or updates all 4 bar layers programmatically with zero native Slider usage.
        /// </summary>
        public void Configure(
            Sprite trackSprite,
            Sprite mainFillSprite,
            Sprite ghostFillSprite,
            Color fillColor,
            Color ghostColor,
            Color pipColor,
            bool showLeadingPip = true,
            float initialFill01 = 1f)
        {
            EnsureLayersBuilt();
            _showLeadingPip = showLeadingPip;

            if (_trackImage != null)
            {
                _trackImage.sprite = trackSprite != null ? trackSprite : CreateWhiteSprite();
                _trackImage.type = Image.Type.Simple;
                _trackImage.color = Color.white;
            }

            if (_ghostFillImage != null)
            {
                _ghostFillImage.sprite = ghostFillSprite != null ? ghostFillSprite : CreateWhiteSprite();
                _ghostFillImage.type = Image.Type.Filled;
                _ghostFillImage.fillMethod = Image.FillMethod.Horizontal;
                _ghostFillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
                _ghostFillImage.color = ghostColor;
            }

            if (_mainFillImage != null)
            {
                _mainFillImage.sprite = mainFillSprite != null ? mainFillSprite : CreateWhiteSprite();
                _mainFillImage.type = Image.Type.Filled;
                _mainFillImage.fillMethod = Image.FillMethod.Horizontal;
                _mainFillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
                _mainFillImage.color = fillColor;
            }

            if (_leadingEdgePipImage != null)
            {
                _leadingEdgePipImage.color = pipColor;
            }

            if (_leadingEdgePip != null)
            {
                _leadingEdgePip.gameObject.SetActive(showLeadingPip);
            }

            float clamped = Mathf.Clamp01(initialFill01);
            _targetFill = clamped;
            _currentMainFill = clamped;
            _currentGhostFill = clamped;
            _ghostDelayTimer = 0f;
            _initialized = true;
            ApplyVisualFill();
        }

        public void SetFillColor(Color fillColor)
        {
            if (_mainFillImage != null && _mainFillImage.color != fillColor)
            {
                _mainFillImage.color = fillColor;
            }
        }

        public void SetPipColor(Color pipColor)
        {
            if (_leadingEdgePipImage != null && _leadingEdgePipImage.color != pipColor)
            {
                _leadingEdgePipImage.color = pipColor;
            }
        }

        /// <summary>
        /// Sets the normalized fill immediately or smoothly and updates an optional label
        /// without string allocation when the integer display value has not changed.
        /// </summary>
        public void SetValue(
            float current,
            float max,
            Text optionalLabel = null,
            string labelSuffix = "",
            bool displayAsPercentage = false,
            bool snapImmediately = false)
        {
            float safeMax = Mathf.Max(0.0001f, max);
            float normalized = Mathf.Clamp01(current / safeMax);

            if (snapImmediately)
            {
                _targetFill = normalized;
                _currentMainFill = normalized;
                _currentGhostFill = normalized;
                _ghostDelayTimer = 0f;
                ApplyVisualFill();
            }
            else
            {
                if (normalized < _targetFill - 0.002f)
                {
                    // Start/refresh ghost delay when bar drops cleanly from an impact or burst
                    if (_ghostDelayTimer <= 0f && Mathf.Abs(_currentGhostFill - _targetFill) < 0.01f)
                    {
                        _ghostDelayTimer = _ghostDropDelay;
                    }
                    else if (_ghostDelayTimer <= 0f)
                    {
                        _ghostDelayTimer = _ghostDropDelay * 0.5f;
                    }
                }
                _targetFill = normalized;
            }

            if (optionalLabel != null)
            {
                int displayInt = displayAsPercentage
                    ? Mathf.RoundToInt(normalized * 100f)
                    : Mathf.CeilToInt(Mathf.Max(0f, current));

                if (displayInt != _lastDisplayedInt)
                {
                    _lastDisplayedInt = displayInt;
                    optionalLabel.text = string.IsNullOrEmpty(labelSuffix)
                        ? (displayAsPercentage ? $"{displayInt}%" : displayInt.ToString())
                        : $"{displayInt}{labelSuffix}";
                }
            }
        }

        private void ApplyVisualFill()
        {
            if (_mainFillImage != null)
            {
                _mainFillImage.fillAmount = _currentMainFill;
            }
            if (_ghostFillImage != null)
            {
                _ghostFillImage.fillAmount = _currentGhostFill;
            }
            if (_leadingEdgePip != null)
            {
                bool visible = _showLeadingPip && _currentMainFill > 0.02f;
                if (_leadingEdgePip.gameObject.activeSelf != visible)
                {
                    _leadingEdgePip.gameObject.SetActive(visible);
                }
                if (visible)
                {
                    _leadingEdgePip.anchorMin = new Vector2(_currentMainFill, 0f);
                    _leadingEdgePip.anchorMax = new Vector2(_currentMainFill, 1f);
                    _leadingEdgePip.pivot = new Vector2(1f, 0.5f);
                    _leadingEdgePip.anchoredPosition = Vector2.zero;
                    _leadingEdgePip.sizeDelta = new Vector2(3f, 2f);
                }
            }
        }

        private void EnsureLayersBuilt()
        {
            if (_trackImage != null && _ghostFillImage != null && _mainFillImage != null && _leadingEdgePip != null)
            {
                return;
            }

            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Destroy(transform.GetChild(i).gameObject);
            }

            _trackImage = CreateFullRectImage("TrackImage");
            _ghostFillImage = CreateFullRectImage("GhostFillImage");
            _mainFillImage = CreateFullRectImage("MainFillImage");

            var pipGo = new GameObject("LeadingEdgePip", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            pipGo.transform.SetParent(transform, false);
            _leadingEdgePip = pipGo.GetComponent<RectTransform>();
            _leadingEdgePip.anchorMin = new Vector2(1f, 0f);
            _leadingEdgePip.anchorMax = new Vector2(1f, 1f);
            _leadingEdgePip.pivot = new Vector2(1f, 0.5f);
            _leadingEdgePip.anchoredPosition = Vector2.zero;
            _leadingEdgePip.sizeDelta = new Vector2(3f, 2f);

            _leadingEdgePipImage = pipGo.GetComponent<Image>();
            _leadingEdgePipImage.raycastTarget = false;
            _leadingEdgePipImage.color = Color.white;
        }

        private Image CreateFullRectImage(string childName)
        {
            var go = new GameObject(childName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            return img;
        }

        private static Sprite _whiteSprite;
        private static Sprite CreateWhiteSprite()
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = Texture2D.whiteTexture;
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            return _whiteSprite;
        }
    }
}

