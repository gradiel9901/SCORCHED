#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Scorched.Weapons.Editor
{
    [InitializeOnLoad]
    public static class BulletPrefabGenerator
    {
        private const string MeshAssetPath = "Assets/Models/BulletMesh.asset";
        private const string MaterialAssetPath = "Assets/Materials/M_BulletTracer.mat";
        private const string PrefabAssetPath = "Assets/Prefabs/Bullet_Projectile.prefab";

        static BulletPrefabGenerator()
        {
            EditorApplication.delayCall += EnsureBulletPrefabExists;
        }

        [MenuItem("Scorched/Weapons/Generate Bullet Prefab")]
        public static void EnsureBulletPrefabExists()
        {
            // 1. Ensure Mesh exists
            Mesh bulletMesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshAssetPath);
            if (bulletMesh == null)
            {
                bulletMesh = CreateBulletMesh();
                if (!Directory.Exists("Assets/Models"))
                {
                    Directory.CreateDirectory("Assets/Models");
                }
                AssetDatabase.CreateAsset(bulletMesh, MeshAssetPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[BulletPrefabGenerator] Generated procedural bullet mesh at {MeshAssetPath}");
            }

            // 2. Ensure Material exists
            Material bulletMat = AssetDatabase.LoadAssetAtPath<Material>(MaterialAssetPath);
            if (bulletMat == null)
            {
                Shader tracerShader = Shader.Find("Scorched/Weapons/BulletTracer");
                if (tracerShader != null)
                {
                    bulletMat = new Material(tracerShader);
                    bulletMat.name = "M_BulletTracer";
                    AssetDatabase.CreateAsset(bulletMat, MaterialAssetPath);
                    AssetDatabase.SaveAssets();
                }
            }

            // 3. Ensure Prefab exists or update it
            GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabAssetPath);
            if (existingPrefab == null)
            {
                CreateAndSaveBulletPrefab(bulletMesh, bulletMat);
            }
        }

        private static void CreateAndSaveBulletPrefab(Mesh mesh, Material mat)
        {
            var rootGo = new GameObject("Bullet_Projectile");

            // MeshFilter & MeshRenderer
            var mf = rootGo.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            var mr = rootGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            // Projectile Component
            var proj = rootGo.AddComponent<BulletProjectile>();

            // Small dynamic light for flight illumination
            var lightGo = new GameObject("BulletGlowLight");
            lightGo.transform.SetParent(rootGo.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0f, 0.02f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1.0f, 0.65f, 0.25f);
            light.intensity = 2.0f;
            light.range = 2.5f;
            light.shadows = LightShadows.None;

            // TrailRenderer child
            var trailGo = new GameObject("TracerTrail");
            trailGo.transform.SetParent(rootGo.transform, false);
            trailGo.transform.localPosition = new Vector3(0f, 0f, -0.06f);

            var trail = trailGo.AddComponent<TrailRenderer>();
            trail.time = 0.08f;
            trail.startWidth = 0.035f;
            trail.endWidth = 0.0f;
            trail.minVertexDistance = 0.04f;
            trail.autodestruct = false;
            trail.sharedMaterial = mat;

            Gradient grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 0.98f, 0.85f), 0f),
                    new GradientColorKey(new Color(1f, 0.45f, 0.05f), 0.6f),
                    new GradientColorKey(new Color(0.8f, 0.15f, 0.0f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.85f, 0.4f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            trail.colorGradient = grad;

            if (!Directory.Exists("Assets/Prefabs"))
            {
                Directory.CreateDirectory("Assets/Prefabs");
            }

            PrefabUtility.SaveAsPrefabAsset(rootGo, PrefabAssetPath);
            Object.DestroyImmediate(rootGo);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BulletPrefabGenerator] Successfully created bullet prefab at {PrefabAssetPath}");
        }

        private static Mesh CreateBulletMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = "Procedural_762_Bullet";

            int radialSegments = 16;
            // Rings defined by (zOffset, radius) along length from tail to tip
            Vector2[] rings = new Vector2[]
            {
                new Vector2(-0.08f, 0.009f), // Boat-tail base
                new Vector2(-0.05f, 0.013f), // Body start
                new Vector2( 0.00f, 0.014f), // Mid body
                new Vector2( 0.04f, 0.014f), // Shoulder
                new Vector2( 0.08f, 0.011f), // Ogive curve
                new Vector2( 0.11f, 0.006f), // Taper
                new Vector2( 0.13f, 0.002f)  // Meplat tip
            };

            int numRings = rings.Length;
            int numVertices = (numRings * (radialSegments + 1)) + 2; // +2 for center tip and base cap
            Vector3[] vertices = new Vector3[numVertices];
            Vector3[] normals = new Vector3[numVertices];
            Vector2[] uvs = new Vector2[numVertices];

            int vertIndex = 0;

            // Generate ring vertices
            for (int r = 0; r < numRings; r++)
            {
                float z = rings[r].x;
                float radius = rings[r].y;
                float v = (float)r / (numRings - 1);

                for (int s = 0; s <= radialSegments; s++)
                {
                    float u = (float)s / radialSegments;
                    float angle = u * Mathf.PI * 2f;
                    float x = Mathf.Cos(angle) * radius;
                    float y = Mathf.Sin(angle) * radius;

                    vertices[vertIndex] = new Vector3(x, y, z);
                    normals[vertIndex] = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f).normalized;
                    uvs[vertIndex] = new Vector2(u, v);
                    vertIndex++;
                }
            }

            // Tip center vertex
            int tipIndex = vertIndex++;
            vertices[tipIndex] = new Vector3(0f, 0f, 0.14f);
            normals[tipIndex] = Vector3.forward;
            uvs[tipIndex] = new Vector2(0.5f, 1f);

            // Base center vertex
            int baseIndex = vertIndex++;
            vertices[baseIndex] = new Vector3(0f, 0f, -0.08f);
            normals[baseIndex] = Vector3.back;
            uvs[baseIndex] = new Vector2(0.5f, 0f);

            // Generate triangles
            int numQuads = (numRings - 1) * radialSegments;
            int numTriangles = (numQuads * 2) + (radialSegments * 2); // Body quads + tip cap + base cap
            int[] triangles = new int[numTriangles * 3];
            int triIndex = 0;

            int stride = radialSegments + 1;

            // Body quad strips
            for (int r = 0; r < numRings - 1; r++)
            {
                for (int s = 0; s < radialSegments; s++)
                {
                    int current = r * stride + s;
                    int next = current + stride;

                    triangles[triIndex++] = current;
                    triangles[triIndex++] = next;
                    triangles[triIndex++] = current + 1;

                    triangles[triIndex++] = current + 1;
                    triangles[triIndex++] = next;
                    triangles[triIndex++] = next + 1;
                }
            }

            // Tip triangles
            int lastRingStart = (numRings - 1) * stride;
            for (int s = 0; s < radialSegments; s++)
            {
                triangles[triIndex++] = lastRingStart + s;
                triangles[triIndex++] = tipIndex;
                triangles[triIndex++] = lastRingStart + s + 1;
            }

            // Base triangles
            for (int s = 0; s < radialSegments; s++)
            {
                triangles[triIndex++] = s + 1;
                triangles[triIndex++] = baseIndex;
                triangles[triIndex++] = s;
            }

            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = triangles;

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            return mesh;
        }
    }
}
#endif
