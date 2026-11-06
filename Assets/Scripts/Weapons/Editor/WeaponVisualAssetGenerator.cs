#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Scorched.Weapons.Editor
{
    [InitializeOnLoad]
    public static class WeaponVisualAssetGenerator
    {
        public const string TextureDir = "Assets/Textures";
        public const string MaterialDir = "Assets/Materials";

        public const string BulletMarkTexturePath = "Assets/Textures/T_BulletMark.png";
        public const string BulletMarkMatPath = "Assets/Materials/M_BulletMark.mat";
        public const string ImpactSparksMatPath = "Assets/Materials/M_BulletImpactSparks.mat";
        public const string MuzzleFlashMatPath = "Assets/Materials/M_MuzzleFlash.mat";

        public const string BulletTracerMatPath = "Assets/Materials/M_BulletTracer.mat";

        static WeaponVisualAssetGenerator()
        {
            EditorApplication.delayCall += EnsureAllAssetsExist;
        }

        [MenuItem("Scorched/Weapons/Generate Visual Assets & Materials")]
        public static void EnsureAllAssetsExist()
        {
            if (!Directory.Exists(TextureDir)) Directory.CreateDirectory(TextureDir);
            if (!Directory.Exists(MaterialDir)) Directory.CreateDirectory(MaterialDir);

            EnsureBulletMarkTexture();
            EnsureBulletTracerMaterial();
            EnsureBulletMarkMaterial();
            EnsureImpactSparksMaterial();
            EnsureMuzzleFlashMaterial();

            AssetDatabase.SaveAssets();
        }

        private static void EnsureBulletTracerMaterial()
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(BulletTracerMatPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Scorched/Weapons/BulletTracer") ?? Shader.Find("Universal Render Pipeline/Lit");
                mat = new Material(shader);
                mat.name = "M_BulletTracer";
                mat.SetColor("_BaseColor", new Color(0.85f, 0.65f, 0.32f, 1f));
                mat.SetColor("_EmissionColor", new Color(2.2f, 0.85f, 0.15f, 1f));
                mat.SetFloat("_EmissionIntensity", 4.5f);
                AssetDatabase.CreateAsset(mat, BulletTracerMatPath);
                Debug.Log($"[WeaponVisualAssetGenerator] Created tracer material at {BulletTracerMatPath}");
            }
        }

        private static void EnsureBulletMarkTexture()
        {
            if (File.Exists(BulletMarkTexturePath)) return;

            const int size = 256;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            float radius = size * 0.48f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 pos = new Vector2(x, y);
                    float d = Vector2.Distance(pos, center);
                    float normD = d / radius; // 0 at center, 1 at boundary

                    if (normD >= 1.0f)
                    {
                        tex.SetPixel(x, y, new Color(0, 0, 0, 0));
                        continue;
                    }

                    float angle = Mathf.Atan2(y - center.y, x - center.x);

                    // Angular perturbations for realistic jagged concrete fracture
                    float noise1 = Mathf.Sin(angle * 7f) * 0.05f;
                    float noise2 = Mathf.Cos(angle * 13f) * 0.035f;
                    float noise3 = Mathf.Sin(angle * 23f + normD * 10f) * 0.02f;
                    float noisyD = normD + noise1 + noise2 + noise3;

                    // Micro cracks branching outwards
                    float crackFactor = Mathf.Abs(Mathf.Sin(angle * 6.0f + Mathf.Cos(angle * 11.0f) * 1.5f));
                    bool isCrack = (normD > 0.22f && normD < 0.85f && crackFactor < 0.045f);

                    Color c;
                    if (noisyD < 0.24f)
                    {
                        // Inner bullet crater core (deep blackened cavity)
                        float innerFade = Mathf.SmoothStep(0f, 0.24f, noisyD);
                        float black = Mathf.Lerp(0.04f, 0.12f, innerFade);
                        c = new Color(black, black, black, 1.0f);
                    }
                    else if (noisyD < 0.48f)
                    {
                        // Crushed pulverized concrete rim with chipped edges
                        float rimT = (noisyD - 0.24f) / 0.24f;
                        float gray = Mathf.Lerp(0.12f, 0.28f, rimT);
                        float alpha = Mathf.Lerp(1.0f, 0.85f, rimT);
                        c = new Color(gray, gray * 0.95f, gray * 0.9f, alpha);
                    }
                    else
                    {
                        // Outer soot, gunpowder burn & radial blast scorch
                        float sootT = (noisyD - 0.48f) / 0.52f;
                        float alpha = Mathf.Lerp(0.85f, 0.0f, sootT * sootT);
                        float sootColor = Mathf.Lerp(0.16f, 0.08f, sootT);
                        c = new Color(sootColor, sootColor, sootColor, alpha);
                    }

                    if (isCrack)
                    {
                        float crackAlpha = Mathf.Lerp(0.95f, 0.3f, normD);
                        c = new Color(0.05f, 0.05f, 0.05f, Mathf.Max(c.a, crackAlpha));
                    }

                    tex.SetPixel(x, y, c);
                }
            }

            tex.Apply();
            byte[] pngBytes = tex.EncodeToPNG();
            File.WriteAllBytes(BulletMarkTexturePath, pngBytes);
            AssetDatabase.ImportAsset(BulletMarkTexturePath, ImportAssetOptions.ForceUpdate);

            TextureImporter importer = AssetImporter.GetAtPath(BulletMarkTexturePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            Debug.Log($"[WeaponVisualAssetGenerator] Generated bullet mark decal texture at {BulletMarkTexturePath}");
        }

        private static void EnsureBulletMarkMaterial()
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(BulletMarkMatPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Scorched/Decals/BulletMark") ?? Shader.Find("Universal Render Pipeline/Unlit");
                mat = new Material(shader);
                mat.name = "M_BulletMark";
                Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(BulletMarkTexturePath);
                if (tex != null)
                {
                    mat.mainTexture = tex;
                    mat.SetTexture("_BaseMap", tex);
                }
                AssetDatabase.CreateAsset(mat, BulletMarkMatPath);
                Debug.Log($"[WeaponVisualAssetGenerator] Created bullet mark material at {BulletMarkMatPath}");
            }
        }

        private static void EnsureImpactSparksMaterial()
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(ImpactSparksMatPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
                mat = new Material(shader);
                mat.name = "M_BulletImpactSparks";

                // URP Particle Unlit additive setup
                mat.SetFloat("_Surface", 1.0f); // Transparent
                mat.SetFloat("_Blend", 1.0f);   // Additive
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_ZWrite", 0.0f);
                mat.SetFloat("_Cull", 0.0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_BLENDMODE_ADD");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

                mat.SetColor("_BaseColor", new Color(1.0f, 0.85f, 0.45f, 1.0f));
                mat.SetColor("_EmissionColor", new Color(3.5f, 1.8f, 0.4f, 1.0f));

                AssetDatabase.CreateAsset(mat, ImpactSparksMatPath);
                Debug.Log($"[WeaponVisualAssetGenerator] Created URP impact sparks material at {ImpactSparksMatPath}");
            }
        }

        private static void EnsureMuzzleFlashMaterial()
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(MuzzleFlashMatPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
                mat = new Material(shader);
                mat.name = "M_MuzzleFlash";

                // URP Particle Unlit additive setup
                mat.SetFloat("_Surface", 1.0f); // Transparent
                mat.SetFloat("_Blend", 1.0f);   // Additive
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_ZWrite", 0.0f);
                mat.SetFloat("_Cull", 0.0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_BLENDMODE_ADD");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

                mat.SetColor("_BaseColor", new Color(1.0f, 0.95f, 0.8f, 1.0f));
                mat.SetColor("_EmissionColor", new Color(4.5f, 2.5f, 0.6f, 1.0f));

                AssetDatabase.CreateAsset(mat, MuzzleFlashMatPath);
                Debug.Log($"[WeaponVisualAssetGenerator] Created URP muzzle flash material at {MuzzleFlashMatPath}");
            }
        }
    }
}
#endif

