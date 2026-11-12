using System.Collections.Generic;
using UnityEngine;

namespace Scorched.Survival
{
    /// <summary>
    /// Represents a dedicated building/shelter Safe-Zone under _ShadeCanopies_And_Cover.
    /// Tracks durability, heatwave targeting, active player crowding, and destruction visuals.
    /// Synchronized deterministically across server and clients via ShadeIndex + HeatwaveHazardManager.
    /// </summary>
    [DisallowMultipleComponent]
    public class ShadeZoneBuilding : MonoBehaviour
    {
        #region Static Registry
        private static readonly List<ShadeZoneBuilding> _allShades = new List<ShadeZoneBuilding>(16);
        public static IReadOnlyList<ShadeZoneBuilding> AllShades => _allShades;

        public static void RefreshRegistryOrder()
        {
            _allShades.RemoveAll(s => s == null);
            if (_allShades.Count == 0)
            {
                var found = Object.FindObjectsByType<ShadeZoneBuilding>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < found.Length; i++)
                {
                    if (found[i] != null && !_allShades.Contains(found[i]))
                    {
                        _allShades.Add(found[i]);
                    }
                }
            }
            _allShades.Sort((a, b) => a._shadeIndex.CompareTo(b._shadeIndex));
        }

        public static ShadeZoneBuilding GetByIndex(int shadeIndex)
        {
            for (int i = 0; i < _allShades.Count; i++)
            {
                if (_allShades[i] != null && _allShades[i]._shadeIndex == shadeIndex)
                {
                    return _allShades[i];
                }
            }
            return null;
        }
        #endregion

        #region Inspector & Public Properties
        [Header("Safe-Zone Identity & Durability")]
        [SerializeField] private int _shadeIndex = 0;
        [SerializeField] private string _customDisplayName = "";
        [SerializeField] private float _maxDurability = 100f;
        [SerializeField] private float _baseBurnRate = 2.0f;
        [SerializeField] private float _crowdingBurnPerPlayer = 3.5f;

        [Header("Trigger Volume Reference")]
        [SerializeField] private BoxCollider _triggerVolume;

        [Header("Visual Feedback")]
        [SerializeField] private Color _targetedWarningTint = new Color(1.0f, 0.45f, 0.16f, 1f);
        [SerializeField] private Color _destroyedScorchTint = new Color(0.24f, 0.19f, 0.17f, 1f);

        public int ShadeIndex
        {
            get => _shadeIndex;
            set => _shadeIndex = value;
        }

        public string BuildingDisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(_customDisplayName)) return _customDisplayName;
                string n = gameObject.name.Replace("_", " ").Trim();
                if (n.Length > 24) n = n.Substring(0, 24);
                return string.IsNullOrEmpty(n) ? $"SHELTER #{_shadeIndex + 1:D2}" : n.ToUpperInvariant();
            }
            set => _customDisplayName = value;
        }

        public float MaxDurability
        {
            get => _maxDurability;
            set => _maxDurability = Mathf.Max(1f, value);
        }

        public float CurrentDurability { get; private set; } = 100f;
        public float DurabilityNormalized => Mathf.Clamp01(CurrentDurability / Mathf.Max(1f, _maxDurability));
        public bool IsDestroyed => CurrentDurability <= 0f;
        public bool IsTargetedByHeatwave { get; private set; }
        public BoxCollider TriggerVolume => _triggerVolume;

        public readonly HashSet<ulong> OccupantClientIds = new HashSet<ulong>();
        public int OccupantCount => OccupantClientIds.Count;
        #endregion

        #region Runtime Visual State
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private Renderer[] _allRenderers;
        private readonly List<Renderer> _roofRenderers = new List<Renderer>(16);
        private readonly List<Collider> _roofColliders = new List<Collider>(16);
        private readonly List<Renderer> _structureRenderers = new List<Renderer>(64);
        private readonly Dictionary<Renderer, Color> _originalColors = new Dictionary<Renderer, Color>(64);
        private MaterialPropertyBlock _propBlock;
        private ParticleSystem _destroyedSmokeVfx;
        private bool _appliedDestroyedVisuals = false;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            CurrentDurability = _maxDurability;
            _propBlock = new MaterialPropertyBlock();

            if (!_allShades.Contains(this))
            {
                _allShades.Add(this);
                RefreshRegistryOrder();
            }

            EnsureTriggerSetup();
            CacheRenderersAndColliders();
        }

        private void OnDestroy()
        {
            _allShades.Remove(this);
        }

        private void Update()
        {
            if (IsTargetedByHeatwave && !IsDestroyed)
            {
                UpdateTargetedWarningVisuals();
            }
        }
        #endregion

        #region Trigger & Containment Queries
        public void SetTriggerVolume(BoxCollider box)
        {
            _triggerVolume = box;
        }

        /// <summary>
        /// Checks oriented local bounds containment against the child BoxCollider trigger.
        /// Works accurately even when buildings are rotated on any axis.
        /// </summary>
        public bool ContainsWorldPoint(Vector3 worldPoint)
        {
            if (_triggerVolume == null || !_triggerVolume.enabled)
            {
                return false;
            }

            Vector3 localPt = _triggerVolume.transform.InverseTransformPoint(worldPoint);
            Vector3 half = _triggerVolume.size * 0.5f;
            Vector3 delta = localPt - _triggerVolume.center;
            return Mathf.Abs(delta.x) <= half.x &&
                   delta.y >= (-half.y - 0.6f) && delta.y <= (half.y + 0.6f) &&
                   Mathf.Abs(delta.z) <= half.z;
        }

        /// <summary>
        /// Physically verifies that a solid roof / ceiling structure is overhead.
        /// Prevents false-positive safe zones when standing outside next to outer walls or in streets.
        /// </summary>
        public bool VerifyOverheadCover(Vector3 probePos)
        {
            if (IsDestroyed) return false;

            // Ensure PhysX queries backfaces so single-sided roof colliders are detected from below
            if (!Physics.queriesHitBackfaces)
            {
                Physics.queriesHitBackfaces = true;
            }

            // Exclude IgnoreRaycast (layer 2), Player (layer 8, 9), triggers
            int layerMask = ~((1 << 2) | (1 << 3) | (1 << 8) | (1 << 9));
            if (Physics.Raycast(probePos, Vector3.up, out RaycastHit hit, 35.0f, layerMask, QueryTriggerInteraction.Ignore))
            {
                // Must not be terrain and must have separation above the probe position
                if (!(hit.collider is TerrainCollider) && hit.distance >= 0.15f)
                {
                    return true;
                }
            }

            return false;
        }

        internal void HandlePlayerEntered(PlayerThermalController player)
        {
            if (player == null || IsDestroyed || !player.IsAliveForShade)
            {
                return;
            }

            ulong id = player.GetEffectiveClientId();
            OccupantClientIds.Add(id);
            player.NotifyEnteredShadeZone(this);
        }

        internal void HandlePlayerExited(PlayerThermalController player)
        {
            if (player == null)
            {
                return;
            }

            ulong id = player.GetEffectiveClientId();
            OccupantClientIds.Remove(id);
            player.NotifyExitedShadeZone(this);
        }

        public void RegisterOccupantId(ulong clientId)
        {
            if (!IsDestroyed)
            {
                OccupantClientIds.Add(clientId);
            }
        }

        public void UnregisterOccupantId(ulong clientId)
        {
            OccupantClientIds.Remove(clientId);
        }
        #endregion

        #region Server Decay & Synced State Application
        public void ResetToFullServer()
        {
            CurrentDurability = _maxDurability;
            IsTargetedByHeatwave = false;
            OccupantClientIds.Clear();
            ApplySyncState(CurrentDurability, false);
        }

        /// <summary>
        /// Called authoritatively during MatchPhase.HeatwaveActive when IsTargetedByHeatwave is true.
        /// DecayRate = baseBurnRate (3.5/s) + (OccupantCount * 4.0/s).
        /// </summary>
        public void TickDurabilityDecayServer(float dt)
        {
            if (!IsTargetedByHeatwave || IsDestroyed || dt <= 0f)
            {
                return;
            }

            float decayRate = _baseBurnRate + (OccupantClientIds.Count * _crowdingBurnPerPlayer);
            float next = Mathf.Max(0f, CurrentDurability - decayRate * dt);
            ApplySyncState(next, true);
        }

        public void ApplySyncState(float syncedDurability, bool isTargeted)
        {
            CurrentDurability = Mathf.Clamp(syncedDurability, 0f, _maxDurability);
            IsTargetedByHeatwave = isTargeted && !IsDestroyed;

            if (IsDestroyed)
            {
                OccupantClientIds.Clear();
                if (!_appliedDestroyedVisuals)
                {
                    ApplyDestroyedVisuals(true);
                }
            }
            else
            {
                if (_appliedDestroyedVisuals)
                {
                    ApplyDestroyedVisuals(false);
                }
                if (!IsTargetedByHeatwave)
                {
                    RestoreOriginalTints();
                }
            }
        }
        #endregion

        #region Visual Effects & Setup Helpers
        private void EnsureTriggerSetup()
        {
            if (_triggerVolume == null)
            {
                var existingProxy = GetComponentInChildren<ShadeTriggerProxy>(true);
                if (existingProxy != null)
                {
                    _triggerVolume = existingProxy.GetComponent<BoxCollider>();
                }
            }

            if (_triggerVolume != null)
            {
                _triggerVolume.isTrigger = true;
                var proxy = _triggerVolume.GetComponent<ShadeTriggerProxy>();
                if (proxy == null)
                {
                    proxy = _triggerVolume.gameObject.AddComponent<ShadeTriggerProxy>();
                }
                proxy.Bind(this);
            }
        }

        private void CacheRenderersAndColliders()
        {
            _roofRenderers.Clear();
            _roofColliders.Clear();
            _structureRenderers.Clear();
            _originalColors.Clear();

            _allRenderers = GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < _allRenderers.Length; i++)
            {
                Renderer r = _allRenderers[i];
                if (r == null || r is ParticleSystemRenderer)
                {
                    continue;
                }

                string lower = r.name.ToLowerInvariant();
                bool isRoof = lower.Contains("roof") || lower.Contains("canopy");

                if (isRoof)
                {
                    _roofRenderers.Add(r);
                    var c = r.GetComponent<Collider>();
                    if (c != null && !c.isTrigger)
                    {
                        _roofColliders.Add(c);
                    }
                }
                else
                {
                    _structureRenderers.Add(r);
                }

                if (r.sharedMaterial != null)
                {
                    if (r.sharedMaterial.HasProperty(BaseColorId))
                    {
                        _originalColors[r] = r.sharedMaterial.GetColor(BaseColorId);
                    }
                    else if (r.sharedMaterial.HasProperty(ColorId))
                    {
                        _originalColors[r] = r.sharedMaterial.GetColor(ColorId);
                    }
                }
            }
        }

        private void UpdateTargetedWarningVisuals()
        {
            float damage01 = 1f - DurabilityNormalized;
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * Mathf.Lerp(4f, 11f, damage01));
            float blend = Mathf.Lerp(0.18f, 0.58f, damage01) + (pulse * 0.22f);

            for (int i = 0; i < _roofRenderers.Count; i++)
            {
                Renderer r = _roofRenderers[i];
                if (r == null || !r.enabled) continue;
                Color baseCol = _originalColors.TryGetValue(r, out Color orig) ? orig : Color.white;
                Color tinted = Color.Lerp(baseCol, _targetedWarningTint, Mathf.Clamp01(blend));
                ApplyColorPropertyBlock(r, tinted);
            }
        }

        private void ApplyDestroyedVisuals(bool destroyed)
        {
            _appliedDestroyedVisuals = destroyed;

            // Remove roof/canopy cover visually & physically when shade burns away
            for (int i = 0; i < _roofRenderers.Count; i++)
            {
                if (_roofRenderers[i] != null)
                {
                    _roofRenderers[i].enabled = !destroyed;
                }
            }
            for (int i = 0; i < _roofColliders.Count; i++)
            {
                if (_roofColliders[i] != null)
                {
                    _roofColliders[i].enabled = !destroyed;
                }
            }

            if (destroyed)
            {
                // Cinematic Ash Disintegration & Interior Loot Incineration
                ScorchedAshCollapseController.TriggerCollapse(this);

                for (int i = 0; i < _structureRenderers.Count; i++)
                {
                    Renderer r = _structureRenderers[i];
                    if (r == null) continue;
                    Color baseCol = _originalColors.TryGetValue(r, out Color orig) ? orig : Color.white;
                    ApplyColorPropertyBlock(r, Color.Lerp(baseCol, _destroyedScorchTint, 0.72f));
                }
                EnsureAndPlayDestroyedVfx(true);
            }
            else
            {
                RestoreOriginalTints();
                EnsureAndPlayDestroyedVfx(false);
            }
        }

        private void RestoreOriginalTints()
        {
            foreach (var kv in _originalColors)
            {
                if (kv.Key != null)
                {
                    kv.Key.SetPropertyBlock(null);
                }
            }
        }

        private void ApplyColorPropertyBlock(Renderer r, Color c)
        {
            if (r == null || _propBlock == null) return;
            r.GetPropertyBlock(_propBlock);
            _propBlock.SetColor(BaseColorId, c);
            _propBlock.SetColor(ColorId, c);
            r.SetPropertyBlock(_propBlock);
        }

        private static Material _sharedSmokeMaterial;

        private static Material GetOrCreateSmokeMaterial()
        {
            if (_sharedSmokeMaterial != null) return _sharedSmokeMaterial;

            Shader pShader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
            if (pShader == null) return null;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "ScorchedSoftSmokeTex"
            };
            Color[] pixels = new Color[size * size];
            float center = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) / center;
                    float dy = (y - center) / center;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float falloff = Mathf.Clamp01(1f - r);
                    float alpha = falloff * falloff * (3f - 2f * falloff);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true);

            var mat = new Material(pShader)
            {
                mainTexture = tex,
                renderQueue = 3000
            };
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            _sharedSmokeMaterial = mat;
            return _sharedSmokeMaterial;
        }

        private void EnsureAndPlayDestroyedVfx(bool play)
        {
            if (play && _destroyedSmokeVfx == null)
            {
                var vfxGo = new GameObject("ScorchedSmokeVFX");
                Vector3 center = _triggerVolume != null
                    ? _triggerVolume.transform.TransformPoint(_triggerVolume.center + new Vector3(0f, _triggerVolume.size.y * 0.35f, 0f))
                    : transform.position + Vector3.up * 3.8f;
                vfxGo.transform.position = center;
                vfxGo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
                vfxGo.transform.SetParent(transform, true);

                _destroyedSmokeVfx = vfxGo.AddComponent<ParticleSystem>();
                _destroyedSmokeVfx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = _destroyedSmokeVfx.main;
                main.playOnAwake = false;
                main.duration = 2f;
                main.loop = true;
                main.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.0f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 2.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.45f, 1.1f);
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.18f, 0.16f, 0.15f, 0.38f), new Color(0.95f, 0.44f, 0.12f, 0.28f));
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = 32;

                var emission = _destroyedSmokeVfx.emission;
                emission.rateOverTime = 8f;

                var shape = _destroyedSmokeVfx.shape;
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = _triggerVolume != null ? new Vector3(_triggerVolume.size.x * 0.65f, _triggerVolume.size.z * 0.65f, 0.4f) : new Vector3(4f, 4f, 0.4f);

                var colLife = _destroyedSmokeVfx.colorOverLifetime;
                colLife.enabled = true;
                var grad = new Gradient();
                grad.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.35f, 0.32f, 0.30f), 1f) },
                    new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0f, 1f) }
                );
                colLife.color = new ParticleSystem.MinMaxGradient(grad);

                var rend = vfxGo.GetComponent<ParticleSystemRenderer>();
                if (rend != null)
                {
                    Material mat = GetOrCreateSmokeMaterial();
                    if (mat != null)
                    {
                        rend.sharedMaterial = mat;
                    }
                }
            }

            if (_destroyedSmokeVfx != null)
            {
                if (play && !_destroyedSmokeVfx.isPlaying) _destroyedSmokeVfx.Play();
                else if (!play && _destroyedSmokeVfx.isPlaying) _destroyedSmokeVfx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
        #endregion
    }
}
