using Unity.Netcode;
using UnityEngine;
using Scorched.Survival;

namespace Scorched.Inventory
{
    /// <summary>
    /// Server-authoritative manager that periodically drops a `Drop Supply Box` from the sky
    /// every 120 seconds onto clear desert terrain and broadcasts an alert banner across clients.
    /// </summary>
    [DisallowMultipleComponent]
    public class ContainerSpawnManager : NetworkBehaviour
    {
        public static ContainerSpawnManager Instance { get; private set; }

        [Header("Supply Drop Settings")]
        [SerializeField] private GameObject _dropSupplyBoxPrefab;
        [SerializeField] private float _supplyDropIntervalSeconds = 120.0f;
        [SerializeField] private float _initialDropDelaySeconds = 45.0f;
        [SerializeField] private float _dropHeightAboveTerrain = 70.0f;
        [SerializeField] private float _descentSpeed = 8.0f;
        [SerializeField] private Vector2 _dropBoundsMinXZ = new Vector2(-36f, -50f);
        [SerializeField] private Vector2 _dropBoundsMaxXZ = new Vector2(38f, 50f);

        private static readonly Vector2[] PrimeTownDropZones = new Vector2[]
        {
            new Vector2(1.8f, -2.0f),    // Central Crossroads
            new Vector2(2.2f, 24.0f),    // North Boulevard
            new Vector2(2.2f, 42.0f),    // North Gate
            new Vector2(1.8f, -24.0f),   // South Boulevard Mid
            new Vector2(1.8f, -44.0f),   // South Boulevard Gate
            new Vector2(-12.0f, -2.0f),  // West Cross Street
            new Vector2(-22.0f, -8.0f),  // West Courtyard
            new Vector2(14.0f, -2.0f),   // East Cross Street
            new Vector2(26.0f, 6.0f),    // East Factory Lane
            new Vector2(-20.0f, -48.0f), // South-West Approach
            new Vector2(25.0f, -42.0f),  // South-East Staging Area
            new Vector2(-12.0f, 28.0f)   // North-West Lane
        };

        public float SupplyDropIntervalSeconds => _supplyDropIntervalSeconds;
        public float TimeUntilNextSupplyDrop { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            TimeUntilNextSupplyDrop = _initialDropDelaySeconds;
        }

        public override void OnDestroy()
        {
            if (Instance == this) Instance = null;
            base.OnDestroy();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            TimeUntilNextSupplyDrop = _initialDropDelaySeconds;
        }

        private void Update()
        {
            if (IsSpawned && !IsServer) return;

            TimeUntilNextSupplyDrop -= Time.deltaTime;
            if (TimeUntilNextSupplyDrop <= 0f)
            {
                TimeUntilNextSupplyDrop = _supplyDropIntervalSeconds;
                SpawnSupplyDropAuthoritative();
            }
        }

        public LootContainerInteractable SpawnSupplyDropAuthoritative(Vector2? explicitXZ = null)
        {
            if (IsSpawned && !IsServer) return null;

            Vector3 landingGround = explicitXZ.HasValue
                ? SampleDropGround(explicitXZ.Value.x, explicitXZ.Value.y)
                : FindClearTerrainDropPoint();

            GameObject prefab = _dropSupplyBoxPrefab;
            if (prefab == null)
            {
#if UNITY_EDITOR
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Container/Drop Supply Box.prefab");
#endif
            }

            GameObject boxGo;
            if (prefab != null)
            {
                boxGo = Instantiate(prefab, landingGround + Vector3.up * _dropHeightAboveTerrain, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            }
            else
            {
                boxGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                boxGo.name = "Drop Supply Box";
                boxGo.transform.position = landingGround + Vector3.up * _dropHeightAboveTerrain;
                boxGo.transform.localScale = Vector3.one * 1.4f;
                boxGo.AddComponent<LootContainerInteractable>();
            }

            var container = boxGo.GetComponent<LootContainerInteractable>() ?? boxGo.AddComponent<LootContainerInteractable>();
            container.BeginSkyDropAuthoritative(landingGround.y, _dropHeightAboveTerrain, _descentSpeed);

            var netObj = boxGo.GetComponent<NetworkObject>();
            if (netObj != null && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && !netObj.IsSpawned)
            {
                netObj.Spawn(true);
            }

            AnnounceSupplyDropBanner();
            return container;
        }

        private void AnnounceSupplyDropBanner()
        {
            const string msg = "SUPPLY DROP INBOUND";
            if (IsSpawned && IsServer)
            {
                AnnounceSupplyDropClientRpc(msg);
            }
            else
            {
                Scorched.UI.ScorchedHUDController.Instance?.ShowAlertBanner(msg, 4.5f);
            }
        }

        [Rpc(SendTo.Everyone)]
        private void AnnounceSupplyDropClientRpc(string message)
        {
            Scorched.UI.ScorchedHUDController.Instance?.ShowAlertBanner(message, 4.5f);
        }

        private Vector3 FindClearTerrainDropPoint()
        {
            // First 18 attempts: Try one of the verified prime town drop zones with small jitter
            for (int attempt = 0; attempt < 18; attempt++)
            {
                Vector2 baseLZ = PrimeTownDropZones[UnityEngine.Random.Range(0, PrimeTownDropZones.Length)];
                float x = baseLZ.x + UnityEngine.Random.Range(-2.0f, 2.0f);
                float z = baseLZ.y + UnityEngine.Random.Range(-2.0f, 2.0f);
                x = Mathf.Clamp(x, _dropBoundsMinXZ.x, _dropBoundsMaxXZ.x);
                z = Mathf.Clamp(z, _dropBoundsMinXZ.y, _dropBoundsMaxXZ.y);

                // Overhead clearance check: raycast straight down from sky
                if (Physics.Raycast(new Vector3(x, 90f, z), Vector3.down, out RaycastHit skyHit, 120f, ~0, QueryTriggerInteraction.Ignore))
                {
                    bool isGround = skyHit.collider is TerrainCollider ||
                                    skyHit.collider.name.IndexOf("Terrain", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    skyHit.collider.name.IndexOf("Ground", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isGround) continue; // Roof or building overhang overhead
                }

                Vector3 ground = SampleDropGround(x, z);
                if (ground.y < 0f || ground.y > 14f) continue;
                if (!IsClearOfShadeAndWalls(ground)) continue;

                return ground;
            }

            // Next 18 attempts: Random sample across town street bounds
            for (int attempt = 0; attempt < 18; attempt++)
            {
                float x = UnityEngine.Random.Range(_dropBoundsMinXZ.x, _dropBoundsMaxXZ.x);
                float z = UnityEngine.Random.Range(_dropBoundsMinXZ.y, _dropBoundsMaxXZ.y);

                if (Physics.Raycast(new Vector3(x, 90f, z), Vector3.down, out RaycastHit skyHit, 120f, ~0, QueryTriggerInteraction.Ignore))
                {
                    bool isGround = skyHit.collider is TerrainCollider ||
                                    skyHit.collider.name.IndexOf("Terrain", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    skyHit.collider.name.IndexOf("Ground", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isGround) continue;
                }

                Vector3 ground = SampleDropGround(x, z);
                if (ground.y < 0f || ground.y > 14f) continue;
                if (!IsClearOfShadeAndWalls(ground)) continue;

                return ground;
            }

            return SampleDropGround(1.8f, -2.0f);
        }

        private static bool IsClearOfShadeAndWalls(Vector3 ground)
        {
            var shades = ShadeZoneBuilding.AllShades;
            for (int s = 0; s < shades.Count; s++)
            {
                if (shades[s] != null && shades[s].ContainsWorldPoint(ground + Vector3.up * 0.5f))
                {
                    return false;
                }
            }

            // Check if there are wall or building obstacles around the landing zone, ignoring terrain
            var colliders = Physics.OverlapSphere(ground + Vector3.up * 1.5f, 1.2f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null) continue;
                if (col is TerrainCollider) continue;
                string cname = col.gameObject.name;
                if (cname.IndexOf("Terrain", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cname.IndexOf("Ground", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;

                return false;
            }

            return true;
        }

        private static Vector3 SampleDropGround(float x, float z)
        {
            float y = 0f;
            if (Physics.Raycast(new Vector3(x, 90f, z), Vector3.down, out RaycastHit hit, 120f, ~0, QueryTriggerInteraction.Ignore))
            {
                bool isGround = hit.collider is TerrainCollider ||
                                hit.collider.name.IndexOf("Terrain", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                hit.collider.name.IndexOf("Ground", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (isGround)
                {
                    y = hit.point.y;
                }
                else if (Terrain.activeTerrain != null)
                {
                    y = Terrain.activeTerrain.SampleHeight(new Vector3(x, 0f, z)) + Terrain.activeTerrain.transform.position.y;
                }
                else
                {
                    y = hit.point.y;
                }
            }
            else if (Terrain.activeTerrain != null)
            {
                y = Terrain.activeTerrain.SampleHeight(new Vector3(x, 0f, z)) + Terrain.activeTerrain.transform.position.y;
            }
            return new Vector3(x, y, z);
        }
    }
}
