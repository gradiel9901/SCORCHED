using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Pool;

namespace Scorched.Weapons
{
    /// <summary>
    /// High-performance, zero-allocation client-side visual manager for bullet tracers,
    /// muzzle flashes, impact spark VFX, and persistent bullet marks.
    /// Uses UnityEngine.Pool.ObjectPool to eliminate runtime GC allocations.
    /// </summary>
    [DisallowMultipleComponent]
    public class BulletVisualPoolManager : MonoBehaviour
    {
        private static BulletVisualPoolManager s_Instance;
        public static BulletVisualPoolManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindAnyObjectByType<BulletVisualPoolManager>();
                    if (s_Instance == null)
                    {
                        var go = new GameObject("BulletVisualPoolManager");
                        s_Instance = go.AddComponent<BulletVisualPoolManager>();
                    }
                }
                return s_Instance;
            }
            private set => s_Instance = value;
        }

        #region Internal Structs (Zero GC Tracking)
        private struct ActiveTracer
        {
            public TrailRenderer Trail;
            public Vector3 StartPos;
            public Vector3 EndPos;
            public float TotalDistance;
            public float TraveledDistance;
            public float Speed;
        }

        private struct ActiveImpact
        {
            public ParticleSystem System;
            public Light Light;
            public float RemainingTime;
        }

        private struct ActiveMuzzleFlash
        {
            public GameObject Root;
            public ParticleSystem System;
            public Light Light;
            public float RemainingTime;
        }

        private struct ActiveBulletMark
        {
            public GameObject Root;
            public Transform ParentTransform;
            public float RemainingTime;
        }
        #endregion

        #region Serialized Prefabs & Settings
        [Header("Pool Configuration")]
        [Tooltip("Optional custom tracer prefab containing a TrailRenderer.")]
        [SerializeField] private TrailRenderer _tracerPrefab;

        [Tooltip("Optional custom impact VFX particle system.")]
        [SerializeField] private ParticleSystem _impactPrefab;

        [Header("Materials (Universal Render Pipeline)")]
        [Tooltip("Material used for procedural tracer visual.")]
        [SerializeField] private Material _tracerMaterial;

        [Tooltip("Additive URP particle material for ricochet impact sparks.")]
        [SerializeField] private Material _impactSparkMaterial;

        [Tooltip("Additive URP particle material for gun barrel muzzle flashes.")]
        [SerializeField] private Material _muzzleFlashMaterial;

        [Tooltip("Transparent decal material for bullet hole marks on walls/ground.")]
        [SerializeField] private Material _bulletMarkMaterial;

        [Header("Pool Capacities")]
        [SerializeField] private int _tracerDefaultCapacity = 150;
        [SerializeField] private int _tracerMaxSize = 300;
        [SerializeField] private int _impactDefaultCapacity = 80;
        [SerializeField] private int _impactMaxSize = 160;
        [SerializeField] private int _muzzleFlashDefaultCapacity = 40;
        [SerializeField] private int _muzzleFlashMaxSize = 80;
        [SerializeField] private int _bulletMarkDefaultCapacity = 100;
        [SerializeField] private int _bulletMarkPoolMaxSize = 200;

        [Header("Impact & Bullet Mark Settings")]
        [SerializeField] private float _defaultImpactLifetime = 0.45f;
        [SerializeField] private float _defaultMuzzleFlashLifetime = 0.05f;
        [SerializeField] private float _defaultBulletMarkLifetime = 30.0f;
        [SerializeField] private float _bulletMarkMinSize = 0.08f;
        [SerializeField] private float _bulletMarkMaxSize = 0.13f;
        #endregion

        #region Runtime Pools & Collections
        private ObjectPool<TrailRenderer> _tracerPool;
        private ObjectPool<ParticleSystem> _impactPool;
        private ObjectPool<GameObject> _muzzleFlashPool;
        private ObjectPool<GameObject> _bulletMarkPool;

        private readonly List<ActiveTracer> _activeTracers = new List<ActiveTracer>(300);
        private readonly List<ActiveImpact> _activeImpacts = new List<ActiveImpact>(160);
        private readonly List<ActiveMuzzleFlash> _activeMuzzleFlashes = new List<ActiveMuzzleFlash>(80);
        private readonly List<ActiveBulletMark> _activeBulletMarks = new List<ActiveBulletMark>(200);

        private Transform _poolRoot;
        private Mesh _sharedQuadMesh;
        #endregion

        #region Unity Lifecycle
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            _poolRoot = new GameObject("VisualPool_Root").transform;
            _poolRoot.SetParent(transform, false);

            EnsureMaterials();
            InitializeSharedQuadMesh();

            InitializeTracerPool();
            InitializeImpactPool();
            InitializeMuzzleFlashPool();
            InitializeBulletMarkPool();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            _tracerPool?.Clear();
            _impactPool?.Clear();
            _muzzleFlashPool?.Clear();
            _bulletMarkPool?.Clear();

            _activeTracers.Clear();
            _activeImpacts.Clear();
            _activeMuzzleFlashes.Clear();
            _activeBulletMarks.Clear();
        }

        private void Update()
        {
            UpdateActiveTracers();
            UpdateActiveImpacts();
            UpdateActiveMuzzleFlashes();
            UpdateActiveBulletMarks();
        }
        #endregion

        #region Material Initialization & Fallbacks (Anti-Pink Guarantee)
        private void EnsureMaterials()
        {
#if UNITY_EDITOR
            if (_tracerMaterial == null)
            {
                _tracerMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_BulletTracer.mat");
            }
            if (_impactSparkMaterial == null)
            {
                _impactSparkMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_BulletImpactSparks.mat");
            }
            if (_muzzleFlashMaterial == null)
            {
                _muzzleFlashMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_MuzzleFlash.mat");
            }
            if (_bulletMarkMaterial == null)
            {
                _bulletMarkMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_BulletMark.mat");
            }
#endif

            // Procedural runtime fallback for tracer material
            if (_tracerMaterial == null)
            {
                Shader s = Shader.Find("Scorched/Weapons/BulletTracer")
                        ?? Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Sprites/Default");
                _tracerMaterial = new Material(s);
                _tracerMaterial.name = "M_Runtime_Tracer";
                _tracerMaterial.SetColor("_BaseColor", new Color(1.0f, 0.9f, 0.6f, 1f));
                _tracerMaterial.SetColor("_EmissionColor", new Color(3.5f, 1.8f, 0.5f, 1f));
                _tracerMaterial.SetFloat("_EmissionIntensity", 5.0f);
            }

            // Procedural runtime fallback for impact sparks (Glowing Incandescent Sparks)
            if (_impactSparkMaterial == null)
            {
                Shader s = Shader.Find("Scorched/Particles/GlowingParticle")
                        ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                        ?? Shader.Find("Particles/Standard Unlit")
                        ?? Shader.Find("Sprites/Default");
                _impactSparkMaterial = new Material(s);
                _impactSparkMaterial.name = "M_Runtime_ImpactSparks";
                _impactSparkMaterial.SetFloat("_Surface", 1.0f); // Transparent fallback
                _impactSparkMaterial.SetFloat("_Blend", 1.0f);   // Additive fallback
                _impactSparkMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                _impactSparkMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                _impactSparkMaterial.SetFloat("_ZWrite", 0.0f);
                _impactSparkMaterial.SetFloat("_Cull", 0.0f);
                _impactSparkMaterial.SetColor("_BaseColor", new Color(1.0f, 0.85f, 0.45f, 1.0f));
                _impactSparkMaterial.SetColor("_EmissionColor", new Color(5.0f, 2.5f, 0.6f, 1.0f));
                _impactSparkMaterial.SetFloat("_EmissionIntensity", 8.0f);
            }

            // Procedural runtime fallback for muzzle flash
            if (_muzzleFlashMaterial == null)
            {
                Shader s = Shader.Find("Scorched/Particles/GlowingParticle")
                        ?? Shader.Find("Universal Render Pipeline/Particles/Unlit")
                        ?? Shader.Find("Particles/Standard Unlit")
                        ?? Shader.Find("Sprites/Default");
                _muzzleFlashMaterial = new Material(s);
                _muzzleFlashMaterial.name = "M_Runtime_MuzzleFlash";
                _muzzleFlashMaterial.SetFloat("_Surface", 1.0f);
                _muzzleFlashMaterial.SetFloat("_Blend", 1.0f);
                _muzzleFlashMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                _muzzleFlashMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                _muzzleFlashMaterial.SetFloat("_ZWrite", 0.0f);
                _muzzleFlashMaterial.SetFloat("_Cull", 0.0f);
                _muzzleFlashMaterial.SetColor("_BaseColor", new Color(1.0f, 0.95f, 0.8f, 1.0f));
                _muzzleFlashMaterial.SetColor("_EmissionColor", new Color(6.0f, 4.0f, 1.2f, 1.0f));
                _muzzleFlashMaterial.SetFloat("_EmissionIntensity", 8.0f);
            }

            // Procedural runtime fallback for bullet mark decals
            if (_bulletMarkMaterial == null)
            {
                Shader s = Shader.Find("Scorched/Decals/BulletMark")
                        ?? Shader.Find("Universal Render Pipeline/Unlit")
                        ?? Shader.Find("Sprites/Default");
                _bulletMarkMaterial = new Material(s);
                _bulletMarkMaterial.name = "M_Runtime_BulletMark";
                _bulletMarkMaterial.SetColor("_BaseColor", Color.white);
            }

#if UNITY_EDITOR
            void AssignTextureSafe(Material mat, string path)
            {
                if (mat == null) return;
                var tex = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex != null)
                {
                    if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
                }
            }

            AssignTextureSafe(_impactSparkMaterial, "Assets/Textures/T_Spark.png");
            AssignTextureSafe(_muzzleFlashMaterial, "Assets/Textures/T_MuzzleFlash.png");
            AssignTextureSafe(_bulletMarkMaterial, "Assets/Textures/T_BulletMark.png");
#endif
        }

        private void InitializeSharedQuadMesh()
        {
            if (_sharedQuadMesh != null) return;

            // Procedural unit quad facing forward (+Z normal facing camera)
            _sharedQuadMesh = new Mesh();
            _sharedQuadMesh.name = "Procedural_Quad";
            _sharedQuadMesh.vertices = new Vector3[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f)
            };
            _sharedQuadMesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f)
            };
            _sharedQuadMesh.normals = new Vector3[]
            {
                Vector3.forward,
                Vector3.forward,
                Vector3.forward,
                Vector3.forward
            };
            _sharedQuadMesh.triangles = new int[] { 0, 2, 1, 2, 3, 1 };
        }
        #endregion

        #region Pool Initializers
        private void InitializeTracerPool()
        {
            _tracerPool = new ObjectPool<TrailRenderer>(
                createFunc: CreateNewTracer,
                actionOnGet: OnGetTracer,
                actionOnRelease: OnReleaseTracer,
                actionOnDestroy: OnDestroyTracer,
                collectionCheck: false,
                defaultCapacity: _tracerDefaultCapacity,
                maxSize: _tracerMaxSize
            );
        }

        private void InitializeImpactPool()
        {
            _impactPool = new ObjectPool<ParticleSystem>(
                createFunc: CreateNewImpact,
                actionOnGet: OnGetImpact,
                actionOnRelease: OnReleaseImpact,
                actionOnDestroy: OnDestroyImpact,
                collectionCheck: false,
                defaultCapacity: _impactDefaultCapacity,
                maxSize: _impactMaxSize
            );
        }

        private void InitializeMuzzleFlashPool()
        {
            _muzzleFlashPool = new ObjectPool<GameObject>(
                createFunc: CreateNewMuzzleFlash,
                actionOnGet: go => go.SetActive(true),
                actionOnRelease: go =>
                {
                    go.SetActive(false);
                    go.transform.SetParent(_poolRoot, false);
                },
                actionOnDestroy: go => { if (go != null) Destroy(go); },
                collectionCheck: false,
                defaultCapacity: _muzzleFlashDefaultCapacity,
                maxSize: _muzzleFlashMaxSize
            );
        }

        private void InitializeBulletMarkPool()
        {
            _bulletMarkPool = new ObjectPool<GameObject>(
                createFunc: CreateNewBulletMark,
                actionOnGet: go => go.SetActive(true),
                actionOnRelease: go =>
                {
                    go.SetActive(false);
                    go.transform.SetParent(_poolRoot, false);
                },
                actionOnDestroy: go => { if (go != null) Destroy(go); },
                collectionCheck: false,
                defaultCapacity: _bulletMarkDefaultCapacity,
                maxSize: _bulletMarkPoolMaxSize
            );
        }
        #endregion

        #region Pool Creation Callbacks
        private TrailRenderer CreateNewTracer()
        {
            TrailRenderer trail;
            if (_tracerPrefab != null)
            {
                trail = Instantiate(_tracerPrefab, _poolRoot);
            }
            else
            {
                var go = new GameObject("BulletTracer_Pooled");
                go.transform.SetParent(_poolRoot, false);
                trail = go.AddComponent<TrailRenderer>();

                trail.time = 0.08f;
                trail.startWidth = 0.045f;
                trail.endWidth = 0.0f;
                trail.minVertexDistance = 0.05f;
                trail.autodestruct = false;

                if (_tracerMaterial != null)
                {
                    trail.sharedMaterial = _tracerMaterial;
                }

                Gradient grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(1f, 0.98f, 0.88f), 0f),
                        new GradientColorKey(new Color(1f, 0.45f, 0.05f), 0.5f),
                        new GradientColorKey(new Color(0.8f, 0.15f, 0.0f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0.85f, 0.4f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                trail.colorGradient = grad;
            }

            trail.gameObject.SetActive(false);
            return trail;
        }

        private void OnGetTracer(TrailRenderer trail)
        {
            trail.gameObject.SetActive(true);
            trail.Clear();
            trail.emitting = true;
        }

        private void OnReleaseTracer(TrailRenderer trail)
        {
            trail.emitting = false;
            trail.Clear();
            trail.gameObject.SetActive(false);
        }

        private void OnDestroyTracer(TrailRenderer trail)
        {
            if (trail != null) Destroy(trail.gameObject);
        }

        private ParticleSystem CreateNewImpact()
        {
            ParticleSystem ps;
            if (_impactPrefab != null)
            {
                ps = Instantiate(_impactPrefab, _poolRoot);
            }
            else
            {
                var go = new GameObject("BulletImpact_Pooled");
                go.transform.SetParent(_poolRoot, false);
                ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                var main = ps.main;
                main.playOnAwake = false;
                main.duration = 0.35f;
                main.loop = false;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.08f, 0.28f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(10f, 22f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.024f);
                main.startColor = new Color(1.0f, 1.0f, 0.95f, 1.0f);
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.gravityModifier = 2.4f;

                var emission = ps.emission;
                emission.rateOverTime = 0;
                emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 18, 30) });

                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 35f;
                shape.radius = 0.005f;

                var colorOverLifetime = ps.colorOverLifetime;
                colorOverLifetime.enabled = true;
                Gradient grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(new Color(1f, 1f, 0.95f), 0f),      // White-hot incandescent
                        new GradientColorKey(new Color(1f, 0.82f, 0.25f), 0.3f), // Molten gold
                        new GradientColorKey(new Color(1f, 0.45f, 0.05f), 0.65f),// Fiery orange
                        new GradientColorKey(new Color(0.5f, 0.12f, 0.02f), 1f)  // Burnt red ember
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(1f, 0.75f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                colorOverLifetime.color = grad;

                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.velocityScale = 0.05f;
                renderer.lengthScale = 3.2f;

                // CRITICAL: Explicitly assign glowing additive particle material
                if (_impactSparkMaterial != null)
                {
                    renderer.sharedMaterial = _impactSparkMaterial;
                }

                // Impact flash point light
                var lightGo = new GameObject("ImpactLight");
                lightGo.transform.SetParent(go.transform, false);
                lightGo.transform.localPosition = Vector3.forward * 0.04f;
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1.0f, 0.8f, 0.4f);
                light.intensity = 4.0f;
                light.range = 2.5f;
                light.shadows = LightShadows.None;
                light.enabled = false;
            }

            ps.gameObject.SetActive(false);
            return ps;
        }

        private void OnGetImpact(ParticleSystem ps)
        {
            ps.gameObject.SetActive(true);
            var light = ps.GetComponentInChildren<Light>(true);
            if (light != null) light.enabled = true;
        }

        private void OnReleaseImpact(ParticleSystem ps)
        {
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var light = ps.GetComponentInChildren<Light>(true);
            if (light != null) light.enabled = false;
            ps.gameObject.SetActive(false);
        }

        private void OnDestroyImpact(ParticleSystem ps)
        {
            if (ps != null) Destroy(ps.gameObject);
        }

        private GameObject CreateNewMuzzleFlash()
        {
            var go = new GameObject("MuzzleFlash_Pooled");
            go.transform.SetParent(_poolRoot, false);

            // Point light illuminating barrel and immediate surroundings
            var lightGo = new GameObject("FlashLight");
            lightGo.transform.SetParent(go.transform, false);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1.0f, 0.85f, 0.5f);
            light.intensity = 4.0f;
            light.range = 2.8f;
            light.shadows = LightShadows.None;

            // Flash particle burst (Compact, 2D camera-facing starburst billboard)
            var psGo = new GameObject("FlashParticles");
            psGo.transform.SetParent(go.transform, false);
            var ps = psGo.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.duration = 0.04f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.025f, 0.045f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 2.0f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.14f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new Color(1.0f, 0.98f, 0.88f, 1.0f);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.gravityModifier = 0f;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 1, 3) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.005f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 1f, 0.9f), 0f),
                    new GradientColorKey(new Color(1f, 0.75f, 0.3f), 0.7f),
                    new GradientColorKey(new Color(0.8f, 0.3f, 0.05f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = grad;

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            if (_muzzleFlashMaterial != null)
            {
                renderer.sharedMaterial = _muzzleFlashMaterial;
            }

            go.SetActive(false);
            return go;
        }

        private GameObject CreateNewBulletMark()
        {
            var go = new GameObject("BulletMark_Pooled");
            go.transform.SetParent(_poolRoot, false);

            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = _sharedQuadMesh;

            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _bulletMarkMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            go.SetActive(false);
            return go;
        }
        #endregion

        #region Public Spawning APIs
        /// <summary>
        /// Retrieves a TrailRenderer from the pool and drives it from start to end at speed without GC allocations.
        /// </summary>
        public void SpawnTracer(Vector3 start, Vector3 end, float speed)
        {
            if (_tracerPool == null) return;

            float distance = Vector3.Distance(start, end);
            if (distance < 0.01f) return;

            TrailRenderer trail = _tracerPool.Get();
            trail.transform.position = start;
            trail.Clear();

            _activeTracers.Add(new ActiveTracer
            {
                Trail = trail,
                StartPos = start,
                EndPos = end,
                TotalDistance = distance,
                TraveledDistance = 0f,
                Speed = Mathf.Max(50f, speed)
            });
        }

        /// <summary>
        /// Spawns a high-speed muzzle flash with dynamic point light and forward spark burst at the gun barrel tip.
        /// </summary>
        public void SpawnMuzzleFlash(Vector3 position, Vector3 forward, Transform parent = null)
        {
            if (_muzzleFlashPool == null) return;

            GameObject flashGo = _muzzleFlashPool.Get();
            if (parent != null)
            {
                flashGo.transform.SetParent(parent, false);
                flashGo.transform.position = position;
                flashGo.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            }
            else
            {
                flashGo.transform.position = position;
                flashGo.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            }

            var ps = flashGo.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                ps.Play(true);
            }

            var light = flashGo.GetComponentInChildren<Light>();
            if (light != null)
            {
                light.enabled = true;
            }

            _activeMuzzleFlashes.Add(new ActiveMuzzleFlash
            {
                Root = flashGo,
                System = ps,
                Light = light,
                RemainingTime = _defaultMuzzleFlashLifetime
            });
        }

        /// <summary>
        /// Retrieves an impact particle system and spawns ricochet sparks, flash light, and a bullet mark decal.
        /// </summary>
        public void SpawnImpact(Vector3 position, Vector3 normal, Collider hitCollider = null)
        {
            if (_impactPool != null)
            {
                ParticleSystem ps = _impactPool.Get();
                ps.transform.position = position + normal * 0.015f;
                ps.transform.rotation = Quaternion.LookRotation(normal, Vector3.up);
                ps.Play(true);

                var light = ps.GetComponentInChildren<Light>();
                if (light != null) light.enabled = true;

                _activeImpacts.Add(new ActiveImpact
                {
                    System = ps,
                    Light = light,
                    RemainingTime = _defaultImpactLifetime
                });
            }

            // Spawn persistent bullet hole mark
            Transform hitTransform = hitCollider != null ? hitCollider.transform : null;
            SpawnBulletMark(position, normal, hitTransform);
        }

        /// <summary>
        /// Backwards compatible overload for impact spawning.
        /// </summary>
        public void SpawnImpact(Vector3 position, Vector3 normal)
        {
            SpawnImpact(position, normal, null);
        }

        /// <summary>
        /// Spawns a bullet hole decal on the hit surface, parented to the hit object with randomized rotation.
        /// </summary>
        public void SpawnBulletMark(Vector3 position, Vector3 normal, Transform parent = null)
        {
            if (_bulletMarkPool == null) return;

            // Enforce maximum pool capacity with circular FIFO recycling
            if (_activeBulletMarks.Count >= _bulletMarkPoolMaxSize)
            {
                var oldest = _activeBulletMarks[0];
                if (oldest.Root != null)
                {
                    _bulletMarkPool.Release(oldest.Root);
                }
                _activeBulletMarks.RemoveAt(0);
            }

            GameObject markGo = _bulletMarkPool.Get();
            markGo.transform.position = position + normal * 0.0025f;

            // Face normal with random Z-rotation for varied cracks
            Quaternion lookRot = Quaternion.LookRotation(-normal, Vector3.up);
            Quaternion randomSpin = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
            markGo.transform.rotation = lookRot * randomSpin;

            float size = UnityEngine.Random.Range(_bulletMarkMinSize, _bulletMarkMaxSize);
            markGo.transform.localScale = new Vector3(size, size, 1.0f);

            if (parent != null)
            {
                markGo.transform.SetParent(parent, true);
            }

            _activeBulletMarks.Add(new ActiveBulletMark
            {
                Root = markGo,
                ParentTransform = parent,
                RemainingTime = _defaultBulletMarkLifetime
            });
        }

        /// <summary>
        /// Direct access to borrow a TrailRenderer for stepped kinematic ballistic simulation.
        /// </summary>
        public TrailRenderer GetPooledTrail(Vector3 origin)
        {
            if (_tracerPool == null) return null;
            var trail = _tracerPool.Get();
            trail.transform.position = origin;
            trail.Clear();
            return trail;
        }

        /// <summary>
        /// Releases a borrowed TrailRenderer back into the pool.
        /// </summary>
        public void ReleaseTrail(TrailRenderer trail)
        {
            if (_tracerPool != null && trail != null)
            {
                _tracerPool.Release(trail);
            }
        }
        #endregion

        #region Central Update Processing (Zero GC)
        private void UpdateActiveTracers()
        {
            float dt = Time.deltaTime;
            for (int i = _activeTracers.Count - 1; i >= 0; i--)
            {
                ActiveTracer tracer = _activeTracers[i];
                tracer.TraveledDistance += tracer.Speed * dt;

                if (tracer.TraveledDistance >= tracer.TotalDistance)
                {
                    if (tracer.Trail != null)
                    {
                        tracer.Trail.transform.position = tracer.EndPos;
                        _tracerPool.Release(tracer.Trail);
                    }

                    int last = _activeTracers.Count - 1;
                    _activeTracers[i] = _activeTracers[last];
                    _activeTracers.RemoveAt(last);
                }
                else
                {
                    float progress = tracer.TraveledDistance / tracer.TotalDistance;
                    tracer.Trail.transform.position = Vector3.Lerp(tracer.StartPos, tracer.EndPos, progress);
                    _activeTracers[i] = tracer;
                }
            }
        }

        private void UpdateActiveImpacts()
        {
            float dt = Time.deltaTime;
            for (int i = _activeImpacts.Count - 1; i >= 0; i--)
            {
                ActiveImpact impact = _activeImpacts[i];
                impact.RemainingTime -= dt;

                if (impact.RemainingTime <= 0f)
                {
                    if (impact.System != null)
                    {
                        _impactPool.Release(impact.System);
                    }

                    int last = _activeImpacts.Count - 1;
                    _activeImpacts[i] = _activeImpacts[last];
                    _activeImpacts.RemoveAt(last);
                }
                else
                {
                    if (impact.Light != null && impact.RemainingTime < _defaultImpactLifetime * 0.7f)
                    {
                        impact.Light.enabled = false;
                    }
                    _activeImpacts[i] = impact;
                }
            }
        }

        private void UpdateActiveMuzzleFlashes()
        {
            float dt = Time.deltaTime;
            for (int i = _activeMuzzleFlashes.Count - 1; i >= 0; i--)
            {
                ActiveMuzzleFlash flash = _activeMuzzleFlashes[i];
                flash.RemainingTime -= dt;

                if (flash.RemainingTime <= 0f)
                {
                    if (flash.Root != null)
                    {
                        _muzzleFlashPool.Release(flash.Root);
                    }

                    int last = _activeMuzzleFlashes.Count - 1;
                    _activeMuzzleFlashes[i] = _activeMuzzleFlashes[last];
                    _activeMuzzleFlashes.RemoveAt(last);
                }
                else
                {
                    _activeMuzzleFlashes[i] = flash;
                }
            }
        }

        private void UpdateActiveBulletMarks()
        {
            float dt = Time.deltaTime;
            for (int i = _activeBulletMarks.Count - 1; i >= 0; i--)
            {
                ActiveBulletMark mark = _activeBulletMarks[i];
                mark.RemainingTime -= dt;

                // Release if time expired or parent object was destroyed
                if (mark.RemainingTime <= 0f || (mark.ParentTransform != null && mark.Root == null))
                {
                    if (mark.Root != null)
                    {
                        _bulletMarkPool.Release(mark.Root);
                    }

                    int last = _activeBulletMarks.Count - 1;
                    _activeBulletMarks[i] = _activeBulletMarks[last];
                    _activeBulletMarks.RemoveAt(last);
                }
                else
                {
                    _activeBulletMarks[i] = mark;
                }
            }
        }
        #endregion
    }
}
