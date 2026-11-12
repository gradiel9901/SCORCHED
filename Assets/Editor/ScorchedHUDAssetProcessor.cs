#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Processes Nano Banana generated assets and generates crisp anti-aliased vector UI sprites
    /// into Assets/UI/ScorchedHUD/ with proper Sprite TextureImporter settings.
    /// </summary>
    public static class ScorchedHUDAssetProcessor
    {
        private const string OutputDir = "Assets/UI/ScorchedHUD";
        private const string NanoPlatePath = @"C:\Users\JM\.gemini\antigravity\brain\a36b9384-20ca-483a-b0fc-07b4e8403a64\hud_panel_plate_v2_1794483169186.jpg";
        private const string NanoFootstepPath = @"C:\Users\JM\.gemini\antigravity\brain\a36b9384-20ca-483a-b0fc-07b4e8403a64\footstep_radar_arc_1794483209733.jpg";

        [MenuItem("SCORCHED/Build HUD UI Sprites")]
        public static void BuildAllHudSpritesMenu()
        {
            BuildAllHudSprites();
        }

        private static float SmoothRange(float edge0, float edge1, float x)
        {
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        public static void BuildAllHudSprites()
        {
            if (!Directory.Exists(OutputDir))
            {
                Directory.CreateDirectory(OutputDir);
            }

            ProcessHudPanelPlate();
            ProcessFootstepArcOrange();
            GenerateSunIcon();
            GenerateShadeIcon();
            GenerateHydrationDropIcon();
            GenerateHeatstrokeVignette();

            // GDD Tech-Brutalist HUD Sprites
            GenerateGddTacticalFrame();
            GenerateCustomBarTrack();
            GenerateCustomBarFillSegmented();
            GenerateCustomBarFillRazor();
            GenerateHealthCrossIcon();

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            ConfigureSpriteImporter($"{OutputDir}/hud_panel_plate.png", new Vector4(48, 48, 48, 48));
            ConfigureSpriteImporter($"{OutputDir}/footstep_arc_orange.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/icon_status_sun.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/icon_status_shade.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/icon_hydration_drop.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/heatstroke_vignette.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/gdd_tactical_frame.png", new Vector4(12, 12, 12, 12));
            ConfigureSpriteImporter($"{OutputDir}/custom_bar_track.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/custom_bar_fill_segmented.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/custom_bar_fill_razor.png", Vector4.zero);
            ConfigureSpriteImporter($"{OutputDir}/icon_health_cross.png", Vector4.zero);

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            Debug.Log("[ScorchedHUDAssetProcessor] Successfully generated and configured all HUD sprites (including GDD tech-brutalist frames & custom bars) in Assets/UI/ScorchedHUD/.");
        }

        private static void ConfigureSpriteImporter(string assetPath, Vector4 spriteBorder)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[ScorchedHUDAssetProcessor] TextureImporter not found for '{assetPath}'.");
                return;
            }

            bool dirty = false;
            if (importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                dirty = true;
            }
            if (importer.spriteImportMode != SpriteImportMode.Single)
            {
                importer.spriteImportMode = SpriteImportMode.Single;
                dirty = true;
            }
            if (!importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = true;
                dirty = true;
            }
            if (importer.mipmapEnabled)
            {
                importer.mipmapEnabled = false;
                dirty = true;
            }
            if (importer.wrapMode != TextureWrapMode.Clamp)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                dirty = true;
            }
            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                dirty = true;
            }
            if (importer.filterMode != FilterMode.Bilinear)
            {
                importer.filterMode = FilterMode.Bilinear;
                dirty = true;
            }
            if (spriteBorder != Vector4.zero && importer.spriteBorder != spriteBorder)
            {
                importer.spriteBorder = spriteBorder;
                dirty = true;
            }

            if (dirty)
            {
                importer.SaveAndReimport();
            }
        }

        private static Texture2D LoadExternalTexture(string filePath)
        {
            if (!File.Exists(filePath)) return null;
            byte[] data = File.ReadAllBytes(filePath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(data))
            {
                return tex;
            }
            UnityEngine.Object.DestroyImmediate(tex);
            return null;
        }

        private static void ProcessHudPanelPlate()
        {
            string outPath = $"{OutputDir}/hud_panel_plate.png";
            Texture2D src = LoadExternalTexture(NanoPlatePath);
            if (src != null)
            {
                int sw = src.width;
                int sh = src.height;
                Color32[] srcPx = src.GetPixels32();

                int minX = sw, minY = sh, maxX = 0, maxY = 0;
                for (int y = 0; y < sh; y++)
                {
                    for (int x = 0; x < sw; x++)
                    {
                        Color32 c = srcPx[y * sw + x];
                        if (c.r > 12 || c.g > 12 || c.b > 12)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }

                if (maxX <= minX || maxY <= minY)
                {
                    minX = 0; minY = 0; maxX = sw - 1; maxY = sh - 1;
                }

                int tw = 512;
                int th = 256;
                var dst = new Texture2D(tw, th, TextureFormat.RGBA32, false);
                Color32[] dstPx = new Color32[tw * th];

                int cropW = maxX - minX + 1;
                int cropH = maxY - minY + 1;

                for (int y = 0; y < th; y++)
                {
                    float v = (float)y / (th - 1);
                    int sy = Mathf.Clamp(minY + Mathf.RoundToInt(v * (cropH - 1)), 0, sh - 1);
                    for (int x = 0; x < tw; x++)
                    {
                        float u = (float)x / (tw - 1);
                        int sx = Mathf.Clamp(minX + Mathf.RoundToInt(u * (cropW - 1)), 0, sw - 1);
                        Color32 c = srcPx[sy * sw + sx];

                        float dx = Mathf.Min(x, tw - 1 - x);
                        float dy = Mathf.Min(y, th - 1 - y);
                        float chamferDist = dx + dy;
                        float chamferCut = 26f;

                        float shapeAlpha = Mathf.Clamp01((chamferDist - chamferCut + 1.5f) * 0.65f);
                        float lum = Mathf.Max(c.r, Mathf.Max(c.g, c.b)) / 255f;
                        if (lum < 0.03f && shapeAlpha < 0.99f)
                        {
                            shapeAlpha = 0f;
                        }

                        bool isAmberBorder = (c.r > c.b + 25) && (c.r > 85);
                        float targetAlpha = isAmberBorder ? 1.0f : 0.80f;
                        byte finalA = (byte)Mathf.RoundToInt(Mathf.Clamp01(shapeAlpha * targetAlpha) * 255f);

                        dstPx[y * tw + x] = finalA == 0 ? new Color32(0, 0, 0, 0) : new Color32(c.r, c.g, c.b, finalA);
                    }
                }

                dst.SetPixels32(dstPx);
                dst.Apply();
                File.WriteAllBytes(outPath, dst.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(dst);
                UnityEngine.Object.DestroyImmediate(src);
                return;
            }

            GenerateProceduralPlateFallback(outPath);
        }

        private static void GenerateProceduralPlateFallback(string outPath)
        {
            int w = 512, h = 256;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float dx = Mathf.Min(x, w - 1 - x);
                    float dy = Mathf.Min(y, h - 1 - y);
                    float edge = Mathf.Min(Mathf.Min(dx, dy), (dx + dy - 24f) * 0.7071f);
                    if (edge < 0f)
                    {
                        px[y * w + x] = new Color32(0, 0, 0, 0);
                    }
                    else if (edge < 2.2f)
                    {
                        px[y * w + x] = new Color32(224, 142, 46, 240);
                    }
                    else
                    {
                        px[y * w + x] = new Color32(22, 24, 27, 204);
                    }
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void ProcessFootstepArcOrange()
        {
            string outPath = $"{OutputDir}/footstep_arc_orange.png";
            int size = 512;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[size * size];

            Texture2D src = LoadExternalTexture(NanoFootstepPath);
            Color32[] srcPx = src != null ? src.GetPixels32() : null;
            int sw = src != null ? src.width : 0;
            int sh = src != null ? src.height : 0;

            float cx = size * 0.5f;
            float cy = size * 0.5f;
            float radius = size * 0.42f;
            float halfThick = size * 0.019f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);

                    // Angle measured from top 12 o'clock (+Y)
                    float angleRad = Mathf.Atan2(Mathf.Abs(dx), dy);
                    float angleDeg = angleRad * Mathf.Rad2Deg;

                    // Crisp crescent arc at 12 o'clock spanning +/- 42 degrees with tapered ends and soft halo
                    float radialDist = Mathf.Abs(r - radius);
                    float angleMask = 1f - SmoothRange(18f, 44f, angleDeg);
                    float taperedHalf = halfThick * Mathf.Lerp(1.0f, 0.15f, SmoothRange(10f, 42f, angleDeg));
                    float coreArc = Mathf.Clamp01(1f - (radialDist - taperedHalf) * 0.42f) * angleMask;
                    float outerGlow = Mathf.Exp(-(radialDist * radialDist) / (2f * 15f * 15f)) * 0.55f * angleMask;

                    // Extract Nano Banana footprint motif from source center and place cleanly below the top arc
                    float footAlpha = 0f;
                    Color32 footColor = new Color32(255, 145, 24, 255);
                    if (srcPx != null && dy > size * 0.14f && dy < size * 0.36f && Mathf.Abs(dx) < size * 0.13f)
                    {
                        float u = 0.50f + (dx / (size * 0.26f)) * 0.19f;
                        float v = 0.495f + ((dy - size * 0.25f) / (size * 0.22f)) * 0.21f;
                        int sx = Mathf.Clamp(Mathf.RoundToInt(u * (sw - 1)), 0, sw - 1);
                        int sy = Mathf.Clamp(Mathf.RoundToInt(v * (sh - 1)), 0, sh - 1);
                        Color32 sc = srcPx[sy * sw + sx];
                        float lum = Mathf.Max(sc.r, Mathf.Max(sc.g, sc.b)) / 255f;
                        if (lum > 0.12f && sc.r > sc.b + 25)
                        {
                            footAlpha = SmoothRange(0.12f, 0.42f, lum);
                            footColor = sc;
                        }
                    }

                    float arcIntensity = Mathf.Clamp01(coreArc + outerGlow);
                    float finalA = Mathf.Clamp01(Mathf.Max(arcIntensity, footAlpha));

                    if (finalA < 0.01f)
                    {
                        px[y * size + x] = new Color32(0, 0, 0, 0);
                    }
                    else
                    {
                        Color col = new Color(1f, 0.55f, 0.08f, finalA);
                        if (footAlpha > arcIntensity)
                        {
                            col = new Color(footColor.r / 255f, footColor.g / 255f, footColor.b / 255f, finalA);
                        }
                        else if (coreArc > 0.65f)
                        {
                            col = Color.Lerp(col, new Color(1f, 0.72f, 0.22f, finalA), 0.5f);
                        }
                        px[y * size + x] = col;
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            if (src != null) UnityEngine.Object.DestroyImmediate(src);
        }

        private static void GenerateSunIcon()
        {
            int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[size * size];
            float c = size * 0.5f;
            Color sunOrange = new Color(1f, 0.55f, 0.14f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c;
                    float dy = y - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float angle = Mathf.Atan2(dy, dx);

                    // Crisp central sun disk (radius 44px)
                    float coreMask = 1f - SmoothRange(42f, 45f, r);

                    // 8 sharp geometric rays from r=60 to r=108
                    float sector = Mathf.Abs(Mathf.Repeat(angle + Mathf.PI / 8f, Mathf.PI / 4f) - Mathf.PI / 8f);
                    float tangentHalfWidth = Mathf.Lerp(10f, 3.5f, Mathf.InverseLerp(60f, 108f, r));
                    float rayPerpDist = r * Mathf.Sin(sector);
                    float rayRadial = Mathf.Min(r - 60f, 108f - r);
                    float rayMask = (rayRadial > -1.5f)
                        ? Mathf.Clamp01((rayRadial + 1.5f) * 0.55f) * Mathf.Clamp01((tangentHalfWidth - rayPerpDist + 1.2f) * 0.65f)
                        : 0f;

                    float alpha = Mathf.Clamp01(Mathf.Max(coreMask, rayMask));
                    px[y * size + x] = alpha > 0.005f
                        ? (Color32)new Color(sunOrange.r, sunOrange.g, sunOrange.b, alpha)
                        : new Color32(0, 0, 0, 0);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/icon_status_sun.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateShadeIcon()
        {
            int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[size * size];
            Color shadeTeal = new Color(0.28f, 0.90f, 0.76f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - 128f) / 128f;
                    float ny = (y - 128f) / 128f;

                    // Angled protective shelter roof overhang across top
                    float roofPeakY = 0.66f - Mathf.Abs(nx) * 0.56f;
                    bool withinRoofSpan = Mathf.Abs(nx) < 0.80f;
                    float roofDist = Mathf.Abs(ny - roofPeakY);
                    float roofAlpha = withinRoofSpan
                        ? Mathf.Clamp01((0.082f - roofDist) * 35f) * Mathf.Clamp01((0.80f - Mathf.Abs(nx)) * 30f)
                        : 0f;

                    // Crisp shield silhouette tucked safely underneath roof
                    float shieldAlpha = 0f;
                    if (ny <= 0.22f && ny >= -0.74f)
                    {
                        float halfW;
                        if (ny >= -0.10f)
                        {
                            halfW = 0.43f;
                        }
                        else
                        {
                            float t = Mathf.InverseLerp(-0.10f, -0.74f, ny);
                            halfW = 0.43f * Mathf.Cos(t * Mathf.PI * 0.5f);
                        }
                        float dEdge = Mathf.Min(halfW - Mathf.Abs(nx), 0.22f - ny);
                        shieldAlpha = Mathf.Clamp01(dEdge * 38f);
                    }

                    float alpha = Mathf.Clamp01(Mathf.Max(roofAlpha, shieldAlpha));
                    px[y * size + x] = alpha > 0.005f
                        ? (Color32)new Color(shadeTeal.r, shadeTeal.g, shadeTeal.b, alpha)
                        : new Color32(0, 0, 0, 0);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/icon_status_shade.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateHydrationDropIcon()
        {
            int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[size * size];
            Color waterBlue = new Color(0.18f, 0.66f, 1.0f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (x - 128f) / 128f;
                    float ny = (y - 128f) / 128f;

                    // Smooth continuous water drop profile: sin^0.75 from top apex (+0.78) to bottom (-0.72)
                    float alpha = 0f;
                    if (ny >= -0.72f && ny <= 0.78f)
                    {
                        float u = Mathf.InverseLerp(0.78f, -0.72f, ny); // 0 at tip, 1 at bottom
                        // Asymmetric tear width: narrow sharp top, full rounded bottom
                        float halfW = 0.54f * Mathf.Pow(Mathf.Sin(u * Mathf.PI * 0.86f), 0.85f) * Mathf.Sqrt(u);
                        if (u > 0.70f)
                        {
                            // Smooth circular bottom closure centered at ny = -0.22, R = 0.50
                            float dy = ny + 0.22f;
                            halfW = Mathf.Sqrt(Mathf.Max(0f, 0.50f * 0.50f - dy * dy));
                        }
                        else
                        {
                            // Blend seamlessly into circle tangent at u = 0.70 (ny = -0.27)
                            float t = u / 0.70f;
                            float dyT = -0.27f + 0.22f;
                            float rJoin = Mathf.Sqrt(0.50f * 0.50f - dyT * dyT);
                            halfW = rJoin * Mathf.Pow(t, 1.15f) * (1.28f - 0.28f * t);
                        }
                        alpha = Mathf.Clamp01((halfW - Mathf.Abs(nx)) * 38f);
                    }

                    if (alpha > 0.005f)
                    {
                        float specDx = nx + 0.16f;
                        float specDy = ny + 0.22f;
                        float specR = Mathf.Sqrt(specDx * specDx + specDy * specDy);
                        float spec = (specR > 0.15f && specR < 0.22f && specDx < -0.04f && specDy < 0.02f) ? 0.65f : 0f;
                        Color col = Color.Lerp(waterBlue, Color.white, spec);
                        col.a = alpha;
                        px[y * size + x] = col;
                    }
                    else
                    {
                        px[y * size + x] = new Color32(0, 0, 0, 0);
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/icon_hydration_drop.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateHeatstrokeVignette()
        {
            int w = 1024, h = 576;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            Color32[] px = new Color32[w * h];

            Color innerAmber = new Color(1.0f, 0.48f, 0.08f, 1f);
            Color outerCrimson = new Color(0.86f, 0.18f, 0.05f, 1f);

            for (int y = 0; y < h; y++)
            {
                float ny = ((float)y / (h - 1)) * 2f - 1f;
                for (int x = 0; x < w; x++)
                {
                    float nx = ((float)x / (w - 1)) * 2f - 1f;
                    float ellipR = Mathf.Sqrt(nx * nx * 0.85f + ny * ny * 0.95f);

                    // Completely transparent center (up to 0.68), smooth gradient into edges & corners
                    float t = SmoothRange(0.68f, 1.32f, ellipR);
                    float grain = Mathf.PerlinNoise(x * 0.04f, y * 0.04f) * 0.14f;
                    float alpha = Mathf.Clamp01(t * (0.86f + grain));

                    if (alpha < 0.004f)
                    {
                        px[y * w + x] = new Color32(0, 0, 0, 0);
                    }
                    else
                    {
                        Color col = Color.Lerp(innerAmber, outerCrimson, SmoothRange(0.75f, 1.30f, ellipR));
                        col.a = alpha;
                        px[y * w + x] = col;
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/heatstroke_vignette.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateGddTacticalFrame()
        {
            const int size = 128;
            const int chamfer = 5; // crisp ~4-5px 45-degree chamfer ONLY on Top-Right and Bottom-Left corners
            const int tickLen = 9; // 1px sharp L-bracket corner ticks fitting cleanly inside 12px 9-slice border

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];

            Color32 colFill = new Color32(20, 21, 24, 210);     // #141518 at ~82% opacity
            Color32 colBorder = new Color32(72, 76, 84, 245);   // #484C54 1px hairline border
            Color32 colTick = new Color32(220, 220, 220, 255);  // #DCDCDC crisp white corner accents
            Color32 colClear = new Color32(0, 0, 0, 0);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Bottom-Left chamfer: x + y < chamfer (in Unity texture space, y=0 is bottom!)
                    int blSum = x + y;
                    // Top-Right chamfer: (size - 1 - x) + (size - 1 - y) < chamfer
                    int trSum = (size - 1 - x) + (size - 1 - y);

                    if (blSum < chamfer || trSum < chamfer)
                    {
                        px[y * size + x] = colClear;
                        continue;
                    }

                    bool isChamferEdge = (blSum == chamfer) || (trSum == chamfer);
                    bool isOuterRectEdge = (x == 0 || x == size - 1 || y == 0 || y == size - 1);

                    if (isChamferEdge || isOuterRectEdge)
                    {
                        // White L-shaped corner accent ticks on Top-Left (x small, y=size-1 or x=0, y near size-1)
                        // and Bottom-Right (x near size-1, y=0 or x=size-1, y small), plus crisp corner ticks adjacent to chamfers
                        bool topLeftL = (y == size - 1 && x <= tickLen) || (x == 0 && y >= size - 1 - tickLen);
                        bool botRightL = (y == 0 && x >= size - 1 - tickLen) || (x == size - 1 && y <= tickLen);
                        bool topRightTick = (y == size - 1 && x >= size - 1 - chamfer - 4 && x <= size - 1 - chamfer) ||
                                            (x == size - 1 && y >= size - 1 - chamfer - 4 && y <= size - 1 - chamfer);
                        bool botLeftTick = (y == 0 && x >= chamfer && x <= chamfer + 4) ||
                                           (x == 0 && y >= chamfer && y <= chamfer + 4);

                        px[y * size + x] = (topLeftL || botRightL || topRightTick || botLeftTick) ? colTick : colBorder;
                    }
                    else
                    {
                        px[y * size + x] = colFill;
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/gdd_tactical_frame.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateCustomBarTrack()
        {
            const int w = 256;
            const int h = 16;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];

            Color32 colBg = new Color32(9, 10, 12, 232);         // #090A0C recessed dark track
            Color32 colHash = new Color32(22, 25, 30, 236);      // diagonal micro-grid hash marks
            Color32 colOutline = new Color32(48, 52, 60, 220);   // 1px dim technical outline

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (x == 0 || x == w - 1 || y == 0 || y == h - 1)
                    {
                        px[y * w + x] = colOutline;
                    }
                    else
                    {
                        bool isDiagHash = ((x + y) % 8) < 2;
                        px[y * w + x] = isDiagHash ? colHash : colBg;
                    }
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/custom_bar_track.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateCustomBarFillSegmented()
        {
            const int w = 256;
            const int h = 16;
            const int segments = 20;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];

            for (int y = 0; y < h; y++)
            {
                float ny = (float)y / (h - 1);
                // Crisp 45-degree slant along each segment divider & leading edge
                int slantOffset = y / 2;

                for (int x = 0; x < w; x++)
                {
                    // Sharp 45-degree chamfered right leading edge on the texture end
                    int distFromRightSlant = (w - 1 - x) - ((h - 1 - y) / 2);
                    if (distFromRightSlant < 0)
                    {
                        px[y * w + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    // 20 micro-segments with clean angled 2px notches
                    float segPos = ((x + slantOffset) / (float)w) * segments;
                    float segFrac = segPos - Mathf.Floor(segPos);
                    if (segFrac > 0.86f && x > 4 && x < w - 4)
                    {
                        px[y * w + x] = new Color32(255, 255, 255, 28);
                        continue;
                    }

                    // Subtle top specular highlight + crisp body
                    float lum = ny > 0.72f ? 1.0f : Mathf.Lerp(0.84f, 0.96f, ny);
                    byte v = (byte)Mathf.RoundToInt(lum * 255f);
                    byte a = (y == 0 || y == h - 1) ? (byte)205 : (byte)255;
                    px[y * w + x] = new Color32(v, v, v, a);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/custom_bar_fill_segmented.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateCustomBarFillRazor()
        {
            const int w = 128;
            const int h = 16;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];

            for (int y = 0; y < h; y++)
            {
                float ny = (float)y / (h - 1);
                for (int x = 0; x < w; x++)
                {
                    bool rightCap = x >= w - 3;
                    float lum = rightCap ? 1.0f : (ny > 0.70f ? 1.0f : Mathf.Lerp(0.86f, 0.96f, ny));
                    byte v = (byte)Mathf.RoundToInt(lum * 255f);
                    px[y * w + x] = new Color32(v, v, v, 255);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/custom_bar_fill_razor.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateHealthCrossIcon()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x - 31.5f);
                    float dy = Mathf.Abs(y - 31.5f);
                    bool inVert = dx <= 5.5f && dy <= 19.5f;
                    bool inHoriz = dy <= 5.5f && dx <= 19.5f;
                    px[y * size + x] = (inVert || inHoriz)
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(0, 0, 0, 0);
                }
            }

            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes($"{OutputDir}/icon_health_cross.png", tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
#endif
