using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Scorched.Inventory;

namespace Scorched.Survival
{
    /// <summary>
    /// Handles the cinematic ash collapse and loot incineration when a ShadeZoneBuilding's durability hits 0.
    /// Dissolves the building's geometry into glowing embers and falling black soot using ScorchedAshDisintegration,
    /// incinerates all interior loot containers, and disables physical shade protection.
    /// </summary>
    [DisallowMultipleComponent]
    public class ScorchedAshCollapseController : MonoBehaviour
    {
        private static readonly int DissolveAmountId = Shader.PropertyToID("_DissolveAmount");
        private static readonly int DissolveNoiseId = Shader.PropertyToID("_DissolveNoise");
        private static readonly int EmberColorId = Shader.PropertyToID("_EmberColor");
        private static readonly int EmberWidthId = Shader.PropertyToID("_EmberWidth");
        private static readonly int AshColorId = Shader.PropertyToID("_AshColor");
        private static readonly int AshWidthId = Shader.PropertyToID("_AshWidth");
        private static readonly int HeightMinId = Shader.PropertyToID("_HeightMin");
        private static readonly int HeightMaxId = Shader.PropertyToID("_HeightMax");

        private static Material s_dissolveShaderMaterial;
        private static Texture2D s_dissolveNoiseTexture;
        private static Material s_ashParticleMaterial;
        private static Material s_emberParticleMaterial;

        [SerializeField] private float _collapseDuration = 3.5f;

        private ShadeZoneBuilding _shadeBuilding;
        private readonly List<Renderer> _collapseRenderers = new List<Renderer>(64);
        private MaterialPropertyBlock _propBlock;
        private ParticleSystem _ashPlumeVfx;
        private bool _isCollapsing = false;

        public static void TriggerCollapse(ShadeZoneBuilding building)
        {
            if (building == null) return;
            var controller = building.GetComponent<ScorchedAshCollapseController>();
            if (controller == null)
            {
                controller = building.gameObject.AddComponent<ScorchedAshCollapseController>();
            }
            controller.StartCollapse(building);
        }

        public void StartCollapse(ShadeZoneBuilding building)
        {
            if (_isCollapsing) return;
            _isCollapsing = true;
            _shadeBuilding = building;
            StartCoroutine(CollapseRoutine());
        }

        private IEnumerator CollapseRoutine()
        {
            EnsureStaticAssets();

            if (_propBlock == null)
            {
                _propBlock = new MaterialPropertyBlock();
            }

            // 1. Collect all renderers and compute overall local height bounds
            _collapseRenderers.Clear();
            Renderer[] allRends = GetComponentsInChildren<Renderer>(true);
            float minY = float.MaxValue;
            float maxY = float.MinValue;

            for (int i = 0; i < allRends.Length; i++)
            {
                Renderer r = allRends[i];
                if (r == null || r is ParticleSystemRenderer) continue;
                _collapseRenderers.Add(r);

                Bounds b = r.bounds;
                if (b.min.y < minY) minY = b.min.y;
                if (b.max.y > maxY) maxY = b.max.y;
            }

            if (minY >= maxY)
            {
                minY = transform.position.y;
                maxY = transform.position.y + 8f;
            }

            // 2. Disable physical roof colliders and shade trigger volume immediately so sunlight floods in
            if (_shadeBuilding != null && _shadeBuilding.TriggerVolume != null)
            {
                _shadeBuilding.TriggerVolume.enabled = false;
            }

            Collider[] cols = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < cols.Length; i++)
            {
                Collider c = cols[i];
                if (c == null) continue;
                string lower = c.name.ToLowerInvariant();
                if (lower.Contains("roof") || lower.Contains("canopy") || lower.Contains("ceiling") || c.isTrigger)
                {
                    c.enabled = false;
                }
            }

            // 3. Incinerate all loot boxes / containers inside this building
            IncinerateInteriorContainers();

            // 4. Spawn massive Ash & Ember Plume VFX
            SpawnAshEruptionVFX(minY, maxY);

            // 5. Replace shader on building materials with AshDisintegration shader
            Shader ashShader = Shader.Find("Scorched/VFX/AshDisintegration");
            if (ashShader != null)
            {
                for (int i = 0; i < _collapseRenderers.Count; i++)
                {
                    Renderer r = _collapseRenderers[i];
                    if (r == null) continue;
                    Material[] mats = r.materials;
                    for (int m = 0; m < mats.Length; m++)
                    {
                        if (mats[m] != null)
                        {
                            Texture baseTex = mats[m].mainTexture;
                            Color baseCol = mats[m].HasProperty("_BaseColor") ? mats[m].GetColor("_BaseColor") : Color.white;
                            mats[m].shader = ashShader;
                            if (baseTex != null) mats[m].mainTexture = baseTex;
                            mats[m].SetColor("_BaseColor", baseCol);
                            if (s_dissolveNoiseTexture != null) mats[m].SetTexture(DissolveNoiseId, s_dissolveNoiseTexture);
                            mats[m].SetFloat(HeightMinId, minY);
                            mats[m].SetFloat(HeightMaxId, maxY);
                            mats[m].SetColor(EmberColorId, new Color(4.0f, 1.4f, 0.2f, 1.0f));
                            mats[m].SetFloat(EmberWidthId, 0.09f);
                            mats[m].SetColor(AshColorId, new Color(0.08f, 0.07f, 0.06f, 1.0f));
                            mats[m].SetFloat(AshWidthId, 0.20f);
                        }
                    }
                    r.materials = mats;
                }
            }

            // 6. Animate dissolve amount from 0.0 to 1.0 over _collapseDuration
            float elapsed = 0f;
            while (elapsed < _collapseDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / _collapseDuration);
                // Non-linear collapse curve: slow burn start, rapid crumble finish
                float dissolveVal = Mathf.Pow(t, 1.25f);

                for (int i = 0; i < _collapseRenderers.Count; i++)
                {
                    Renderer r = _collapseRenderers[i];
                    if (r == null) continue;
                    r.GetPropertyBlock(_propBlock);
                    _propBlock.SetFloat(DissolveAmountId, dissolveVal);
                    r.SetPropertyBlock(_propBlock);
                }

                yield return null;
            }

            // 7. Complete disintegration: disable renderers, stop particle emission
            for (int i = 0; i < _collapseRenderers.Count; i++)
            {
                if (_collapseRenderers[i] != null)
                {
                    _collapseRenderers[i].enabled = false;
                }
            }

            // Disable remaining structure colliders
            for (int i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null) cols[i].enabled = false;
            }

            if (_ashPlumeVfx != null)
            {
                _ashPlumeVfx.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        private void IncinerateInteriorContainers()
        {
            var containers = UnityEngine.Object.FindObjectsByType<LootContainerInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < containers.Length; i++)
            {
                LootContainerInteractable c = containers[i];
                if (c == null) continue;

                Vector3 pos = c.transform.position;
                bool isInside = false;

                if (_shadeBuilding != null && _shadeBuilding.ContainsWorldPoint(pos))
                {
                    isInside = true;
                }
                else
                {
                    // Fallback distance check to building center
                    float dist = Vector3.Distance(transform.position, pos);
                    if (dist <= 12f)
                    {
                        isInside = true;
                    }
                }

                if (isInside)
                {
                    c.IncinerateIntoAshesAuthoritative();
                }
            }
        }

        private void SpawnAshEruptionVFX(float minY, float maxY)
        {
            var vfxGo = new GameObject("ScorchedAshEruptionVFX");
            Vector3 center = transform.position + Vector3.up * ((maxY - minY) * 0.45f);
            vfxGo.transform.position = center;
            vfxGo.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);
            vfxGo.transform.SetParent(transform, true);

            _ashPlumeVfx = vfxGo.AddComponent<ParticleSystem>();
            _ashPlumeVfx.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _ashPlumeVfx.main;
            main.playOnAwake = false;
            main.duration = _collapseDuration;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.0f, 5.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.2f, 3.2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.12f, 0.10f, 0.09f, 0.75f), new Color(0.05f, 0.04f, 0.04f, 0.65f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 120;

            var emission = _ashPlumeVfx.emission;
            emission.rateOverTime = 28f;

            var shape = _ashPlumeVfx.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(8f, 8f, 1.5f);

            var colLife = _ashPlumeVfx.colorOverLifetime;
            colLife.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(new Color(0.95f, 0.38f, 0.08f), 0f), new GradientColorKey(new Color(0.12f, 0.11f, 0.10f), 0.35f), new GradientColorKey(new Color(0.05f, 0.05f, 0.05f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.85f, 0.15f), new GradientAlphaKey(0f, 1f) }
            );
            colLife.color = new ParticleSystem.MinMaxGradient(grad);

            var sizeLife = _ashPlumeVfx.sizeOverLifetime;
            sizeLife.enabled = true;
            sizeLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.4f, 1f, 1.8f));

            var rend = vfxGo.GetComponent<ParticleSystemRenderer>();
            if (rend != null && s_ashParticleMaterial != null)
            {
                rend.sharedMaterial = s_ashParticleMaterial;
            }

            // Add flying fiery embers sub-emitter
            var embersGo = new GameObject("FieryEmbers");
            embersGo.transform.SetParent(vfxGo.transform, false);
            embersGo.transform.localPosition = Vector3.zero;
            embersGo.transform.localRotation = Quaternion.identity;

            var psEmbers = embersGo.AddComponent<ParticleSystem>();
            var eMain = psEmbers.main;
            eMain.playOnAwake = false;
            eMain.duration = _collapseDuration;
            eMain.loop = false;
            eMain.startLifetime = new ParticleSystem.MinMaxCurve(1.8f, 3.2f);
            eMain.startSpeed = new ParticleSystem.MinMaxCurve(3.5f, 7.5f);
            eMain.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.32f);
            eMain.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.7f, 0.2f, 1f), new Color(1f, 0.35f, 0.05f, 1f));
            eMain.simulationSpace = ParticleSystemSimulationSpace.World;
            eMain.maxParticles = 160;

            var eEmission = psEmbers.emission;
            eEmission.rateOverTime = 40f;

            var eShape = psEmbers.shape;
            eShape.shapeType = ParticleSystemShapeType.Box;
            eShape.scale = new Vector3(8f, 8f, 1.5f);

            var eRend = embersGo.GetComponent<ParticleSystemRenderer>();
            if (eRend != null && s_emberParticleMaterial != null)
            {
                eRend.sharedMaterial = s_emberParticleMaterial;
            }

            _ashPlumeVfx.Play();
            psEmbers.Play();
        }

        private static void EnsureStaticAssets()
        {
            if (s_dissolveNoiseTexture == null)
            {
                s_dissolveNoiseTexture = Resources.Load<Texture2D>("VFX/T_VFX_AshDissolveNoise");
#if UNITY_EDITOR
                if (s_dissolveNoiseTexture == null)
                {
                    s_dissolveNoiseTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/VFX/T_VFX_AshDissolveNoise.png");
                }
#endif
            }

            if (s_ashParticleMaterial == null)
            {
                s_ashParticleMaterial = Resources.Load<Material>("VFX/M_SupplyDropSmoke");
#if UNITY_EDITOR
                if (s_ashParticleMaterial == null)
                {
                    s_ashParticleMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/VFX/M_SupplyDropSmoke.mat");
                }
#endif
            }

            if (s_emberParticleMaterial == null)
            {
                s_emberParticleMaterial = Resources.Load<Material>("VFX/M_SupplyDropEmbers");
#if UNITY_EDITOR
                if (s_emberParticleMaterial == null)
                {
                    s_emberParticleMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/VFX/M_SupplyDropEmbers.mat");
                }
#endif
            }
        }
    }
}
