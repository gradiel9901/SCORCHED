#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Scorched.Editor
{
    public static class InspectMapBounds
    {
        [MenuItem("Tools/SCORCHED/QA/Inspect Map Bounds")]
        public static void Inspect()
        {
            var map = GameObject.Find("SCORCHED_Town_Map");
            if (map != null)
            {
                var rends = map.GetComponentsInChildren<Renderer>();
                Bounds b = new Bounds(map.transform.position, Vector3.zero);
                foreach (var r in rends)
                {
                    if (r != null && !(r is ParticleSystemRenderer))
                        b.Encapsulate(r.bounds);
                }
                Debug.Log($"[MAP_BOUNDS] Center: {b.center}, Min: {b.min}, Max: {b.max}, Size: {b.size}");

                // Find street / courtyard open spots
                var shades = Object.FindObjectsByType<Scorched.Survival.ShadeZoneBuilding>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                Bounds bldgBounds = new Bounds();
                bool bInit = false;
                foreach (var s in shades)
                {
                    if (s != null && s.TriggerVolume != null)
                    {
                        Vector3 c = s.TriggerVolume.transform.TransformPoint(s.TriggerVolume.center);
                        if (!bInit) { bldgBounds = new Bounds(c, Vector3.one); bInit = true; }
                        else bldgBounds.Encapsulate(c);
                    }
                }
                Debug.Log($"[BUILDINGS_BOUNDS] Center: {bldgBounds.center}, Min: {bldgBounds.min}, Max: {bldgBounds.max}, Size: {bldgBounds.size}");
            }
            else
            {
                Debug.Log("[MAP_BOUNDS] SCORCHED_Town_Map not found, searching districts...");
                string[] districtRoots = new string[]
                {
                    "_Administrative_District",
                    "_Manufacturing_District_TL",
                    "_Manufacturing_District_BL",
                    "_Storage_District",
                    "_ShadeCanopies_And_Cover"
                };
                Bounds b = new Bounds(Vector3.zero, Vector3.zero);
                bool init = false;
                foreach (var name in districtRoots)
                {
                    var go = GameObject.Find(name);
                    if (go != null)
                    {
                        var rends = go.GetComponentsInChildren<Renderer>();
                        foreach (var r in rends)
                        {
                            if (r != null && !(r is ParticleSystemRenderer))
                            {
                                if (!init) { b = new Bounds(r.bounds.center, r.bounds.size); init = true; }
                                else b.Encapsulate(r.bounds);
                            }
                        }
                    }
                }
                Debug.Log($"[MAP_BOUNDS_FROM_DISTRICTS] Center: {b.center}, Min: {b.min}, Max: {b.max}, Size: {b.size}");
            }

            // Also check Terrain
            var terrain = Terrain.activeTerrain;
            if (terrain != null)
            {
                Debug.Log($"[TERRAIN_BOUNDS] Pos: {terrain.transform.position}, Size: {terrain.terrainData.size}");
            }

            // Also check ContainerSpawnManager
            var csm = Object.FindAnyObjectByType<Scorched.Inventory.ContainerSpawnManager>();
            if (csm != null)
            {
                Debug.Log($"[CONTAINER_SPAWN_MANAGER] Found in scene: {csm.gameObject.name}");
            }

            // Test Prime Town Drop Zones
            Vector2[] testPoints = new Vector2[]
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
                new Vector2(-12.0f, 28.0f),  // North-West Lane
                new Vector2(14.0f, 26.0f)    // North-East Lane
            };

            foreach (var pt in testPoints)
            {
                float y = 0f;
                string hitCol = "None";
                if (Physics.Raycast(new Vector3(pt.x, 90f, pt.y), Vector3.down, out RaycastHit rHit, 120f, ~0, QueryTriggerInteraction.Ignore))
                {
                    y = rHit.point.y;
                    hitCol = rHit.collider != null ? rHit.collider.name : "null";
                }
                bool inShade = false;
                foreach (var s in Scorched.Survival.ShadeZoneBuilding.AllShades)
                {
                    if (s != null && s.ContainsWorldPoint(new Vector3(pt.x, y + 0.5f, pt.y)))
                    {
                        inShade = true;
                        break;
                    }
                }
                Debug.Log($"[TEST_ZONE] ({pt.x}, {pt.y}) -> Ground Y={y:F2}, Hit={hitCol}, InShade={inShade}");
            }
        }
    }
}
#endif
