using System;
using System.Collections.Generic;
using System.IO;
using Scorched.Inventory;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace Scorched.EditorTools
{
    /// <summary>
    /// Generates realistic 3D PBR item pickup prefabs (including sliced AK-47 structural submeshes),
    /// renders crisp transparent-alpha 3D studio icon PNGs via dual-pass black/white difference matting,
    /// generates tactical UI ring & cell sprites, and populates Assets/Resources/ScorchedItemCatalog.asset.
    /// </summary>
    public static class ScorchedInventoryAssetProcessor
    {
        private const string UIAssetDir = "Assets/UI/ScorchedInventory";
        private const string PrefabDir = "Assets/Prefabs/Items";
        private const string PartsMeshDir = "Assets/Models/InventoryParts";
        private const string MaterialDir = "Assets/Materials/InventoryItems";
        private const string CatalogAssetPath = "Assets/Resources/ScorchedItemCatalog.asset";
        private const string Ak47FbxPath = "Assets/Prefabs/Asset Store/Weapons/AK47/Models/Weapon/AK47NoSubdiv.fbx";
        private const string Ak47MatPath = "Assets/Prefabs/Asset Store/Weapons/AK47/Materials/RIfle.mat";
        private const string Ak47MagMeshPath = "Assets/Prefabs/Asset Store/Weapons/AK47/Models/Weapon/AK47_Magazine.asset";
        private const int StudioLayer = 30;

        [MenuItem("SCORCHED/Build Inventory Items, Sprites & Catalog")]
        public static void BuildAllAssets()
        {
            EnsureDir("Assets/UI");
            EnsureDir(UIAssetDir);
            EnsureDir("Assets/Prefabs");
            EnsureDir(PrefabDir);
            EnsureDir("Assets/Models");
            EnsureDir(PartsMeshDir);
            EnsureDir("Assets/Materials");
            EnsureDir(MaterialDir);
            EnsureDir("Assets/Resources");

            // 1. Slice AK-47 meshes
            var partMeshes = BuildSlicedAK47PartMeshes();
            Material akMat = AssetDatabase.LoadAssetAtPath<Material>(Ak47MatPath);
            if (akMat == null) akMat = CreateLitMaterial("FallbackAK_Mat", new Color(0.22f, 0.22f, 0.24f), 0.7f, 0.45f);

            // 2. Build UI sprites (Radial ring & Cabal slot cell)
            GenerateRadialProgressRingPng(Path.Combine(UIAssetDir, "radial_progress_ring.png"));
            GenerateCabalSlotCellPng(Path.Combine(UIAssetDir, "cabal_slot_cell.png"));

            // 3. Build 3D Prefabs + Render 3D Transparent Icon PNGs
            var specs = GetItemSpecs();
            var builtPrefabs = new Dictionary<ItemId, GameObject>();

            for (int i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                GameObject modelRoot = Create3DModelForItem(spec.Id, partMeshes, akMat);
                string iconPath = $"{UIAssetDir}/{spec.FileSlug}.png";
                RenderTransparent3DIcon(modelRoot, iconPath, spec.StudioEuler, spec.StudioOrthoScale);

                string prefabPath = $"{PrefabDir}/Pickup_{spec.FileSlug}.prefab";
                GameObject prefab = SaveAsNetworkPickupPrefab(modelRoot, spec.Id, prefabPath);
                builtPrefabs[spec.Id] = prefab;

                UnityEngine.Object.DestroyImmediate(modelRoot);
            }

            AssetDatabase.Refresh();

            // 4. Configure TextureImporters as Sprites
            ConfigureSpriteImporter($"{UIAssetDir}/radial_progress_ring.png");
            ConfigureSpriteImporter($"{UIAssetDir}/cabal_slot_cell.png");
            for (int i = 0; i < specs.Count; i++)
            {
                ConfigureSpriteImporter($"{UIAssetDir}/{specs[i].FileSlug}.png");
            }
            AssetDatabase.Refresh();

            // 5. Build / Update ScorchedItemCatalog asset
            var catalog = AssetDatabase.LoadAssetAtPath<ScorchedItemCatalog>(CatalogAssetPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<ScorchedItemCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogAssetPath);
            }

            catalog.RadialProgressRingSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{UIAssetDir}/radial_progress_ring.png");
            catalog.CabalSlotCellSprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{UIAssetDir}/cabal_slot_cell.png");
            catalog.Items.Clear();

            for (int i = 0; i < specs.Count; i++)
            {
                var s = specs[i];
                var def = new ItemDefinition
                {
                    Id = s.Id,
                    DisplayName = s.DisplayName,
                    ShortTag = s.ShortTag,
                    Category = s.Category,
                    Grade = s.Grade,
                    GridWidth = s.GridW,
                    GridHeight = s.GridH,
                    IsStackable = s.Stackable,
                    MaxStack = s.MaxStack,
                    RestoreAmount = s.RestoreAmount,
                    UpgradesToId = s.UpgradesTo,
                    Icon = AssetDatabase.LoadAssetAtPath<Sprite>($"{UIAssetDir}/{s.FileSlug}.png"),
                    WorldPickupPrefab = builtPrefabs.TryGetValue(s.Id, out var pf) ? pf : null
                };
                catalog.Items.Add(def);
            }

            catalog.RebuildLookup();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ScorchedInventoryAssetProcessor] Built {specs.Count} item prefabs, 16 sprites, and {CatalogAssetPath}.");
        }

        private struct ItemSpec
        {
            public ItemId Id;
            public string FileSlug;
            public string DisplayName;
            public string ShortTag;
            public ItemCategory Category;
            public ItemGrade Grade;
            public int GridW, GridH;
            public bool Stackable;
            public int MaxStack;
            public float RestoreAmount;
            public ItemId UpgradesTo;
            public Vector3 StudioEuler;
            public float StudioOrthoScale;
        }

        private static List<ItemSpec> GetItemSpecs()
        {
            return new List<ItemSpec>
            {
                new ItemSpec { Id = ItemId.Water_Poor, FileSlug = "water_poor", DisplayName = "Dirty Water Bottle", ShortTag = "H2O I", Category = ItemCategory.Water, Grade = ItemGrade.Poor, GridW = 1, GridH = 1, MaxStack = 1, RestoreAmount = 10f, UpgradesTo = ItemId.Water_Good, StudioEuler = new Vector3(18f, -35f, -14f), StudioOrthoScale = 0.72f },
                new ItemSpec { Id = ItemId.Water_Good, FileSlug = "water_good", DisplayName = "Military Canteen", ShortTag = "H2O II", Category = ItemCategory.Water, Grade = ItemGrade.Good, GridW = 1, GridH = 2, MaxStack = 1, RestoreAmount = 30f, UpgradesTo = ItemId.Water_Great, StudioEuler = new Vector3(16f, -32f, -8f), StudioOrthoScale = 0.78f },
                new ItemSpec { Id = ItemId.Water_Great, FileSlug = "water_great", DisplayName = "Insulated Purifier Jug", ShortTag = "H2O III", Category = ItemCategory.Water, Grade = ItemGrade.Great, GridW = 2, GridH = 2, MaxStack = 1, RestoreAmount = 75f, UpgradesTo = ItemId.None, StudioEuler = new Vector3(20f, -38f, 0f), StudioOrthoScale = 0.85f },

                new ItemSpec { Id = ItemId.Bandage_Poor, FileSlug = "bandage_poor", DisplayName = "Cloth Rag Bandage", ShortTag = "MED I", Category = ItemCategory.Bandage, Grade = ItemGrade.Poor, GridW = 1, GridH = 1, MaxStack = 1, RestoreAmount = 10f, UpgradesTo = ItemId.Bandage_Good, StudioEuler = new Vector3(28f, -30f, 12f), StudioOrthoScale = 0.75f },
                new ItemSpec { Id = ItemId.Bandage_Good, FileSlug = "bandage_good", DisplayName = "Sterile Combat Gauze", ShortTag = "MED II", Category = ItemCategory.Bandage, Grade = ItemGrade.Good, GridW = 1, GridH = 2, MaxStack = 1, RestoreAmount = 30f, UpgradesTo = ItemId.Bandage_Great, StudioEuler = new Vector3(22f, -28f, -6f), StudioOrthoScale = 0.80f },
                new ItemSpec { Id = ItemId.Bandage_Great, FileSlug = "bandage_great", DisplayName = "Trauma Medkit", ShortTag = "MED III", Category = ItemCategory.Bandage, Grade = ItemGrade.Great, GridW = 2, GridH = 2, MaxStack = 1, RestoreAmount = 75f, UpgradesTo = ItemId.None, StudioEuler = new Vector3(24f, -36f, 0f), StudioOrthoScale = 0.85f },

                new ItemSpec { Id = ItemId.Food_Poor, FileSlug = "food_poor", DisplayName = "Dented Tin Beans", ShortTag = "RATS I", Category = ItemCategory.Food, Grade = ItemGrade.Poor, GridW = 1, GridH = 1, MaxStack = 1, RestoreAmount = 10f, UpgradesTo = ItemId.Food_Good, StudioEuler = new Vector3(22f, -32f, 5f), StudioOrthoScale = 0.72f },
                new ItemSpec { Id = ItemId.Food_Good, FileSlug = "food_good", DisplayName = "Military MRE Pouch", ShortTag = "RATS II", Category = ItemCategory.Food, Grade = ItemGrade.Good, GridW = 1, GridH = 2, MaxStack = 1, RestoreAmount = 30f, UpgradesTo = ItemId.Food_Great, StudioEuler = new Vector3(20f, -28f, -6f), StudioOrthoScale = 0.80f },
                new ItemSpec { Id = ItemId.Food_Great, FileSlug = "food_great", DisplayName = "High-Protein Field Ration", ShortTag = "RATS III", Category = ItemCategory.Food, Grade = ItemGrade.Great, GridW = 2, GridH = 2, MaxStack = 1, RestoreAmount = 75f, UpgradesTo = ItemId.None, StudioEuler = new Vector3(24f, -38f, 0f), StudioOrthoScale = 0.86f },

                new ItemSpec { Id = ItemId.AK47_Receiver, FileSlug = "ak47_part_receiver", DisplayName = "AK-47 Receiver Body", ShortTag = "PART:RCV", Category = ItemCategory.GunPart, Grade = ItemGrade.None, GridW = 1, GridH = 1, MaxStack = 1, StudioEuler = new Vector3(14f, -32f, 90f), StudioOrthoScale = 0.78f },
                new ItemSpec { Id = ItemId.AK47_Barrel, FileSlug = "ak47_part_barrel", DisplayName = "AK-47 Barrel & Handguard", ShortTag = "PART:BRL", Category = ItemCategory.GunPart, Grade = ItemGrade.None, GridW = 1, GridH = 1, MaxStack = 1, StudioEuler = new Vector3(12f, -35f, 90f), StudioOrthoScale = 0.80f },
                new ItemSpec { Id = ItemId.AK47_Stock, FileSlug = "ak47_part_stock", DisplayName = "AK-47 Wooden Stock", ShortTag = "PART:STK", Category = ItemCategory.GunPart, Grade = ItemGrade.None, GridW = 1, GridH = 1, MaxStack = 1, StudioEuler = new Vector3(14f, -30f, 90f), StudioOrthoScale = 0.78f },
                new ItemSpec { Id = ItemId.AK47_Bolt, FileSlug = "ak47_part_bolt", DisplayName = "AK-47 Bolt & Magazine", ShortTag = "PART:BLT", Category = ItemCategory.GunPart, Grade = ItemGrade.None, GridW = 1, GridH = 1, MaxStack = 1, StudioEuler = new Vector3(16f, -35f, 15f), StudioOrthoScale = 0.78f },

                new ItemSpec { Id = ItemId.Ammo_762, FileSlug = "ammo_762_box", DisplayName = "7.62x39mm Ammo Tin", ShortTag = "7.62", Category = ItemCategory.Ammo, Grade = ItemGrade.None, GridW = 1, GridH = 1, Stackable = true, MaxStack = 120, StudioEuler = new Vector3(24f, -38f, 0f), StudioOrthoScale = 0.76f },
            };
        }

        private static Dictionary<ItemId, Mesh> BuildSlicedAK47PartMeshes()
        {
            var dict = new Dictionary<ItemId, Mesh>();
            Mesh sourceFbxMesh = AssetDatabase.LoadAssetAtPath<Mesh>(Ak47FbxPath);
            Mesh magMesh = AssetDatabase.LoadAssetAtPath<Mesh>(Ak47MagMeshPath);

            if (sourceFbxMesh == null)
            {
                var allAssets = AssetDatabase.LoadAllAssetsAtPath(Ak47FbxPath);
                foreach (var a in allAssets)
                {
                    if (a is Mesh m) { sourceFbxMesh = m; break; }
                }
            }

            if (sourceFbxMesh != null)
            {
                // Raw AK FBX mesh runs along Y in ~0.01 scale: [-0.00522 barrel .. +0.00366 stock]
                float scale = sourceFbxMesh.bounds.size.magnitude < 0.05f ? 100f : 1f;
                dict[ItemId.AK47_Barrel] = SliceMeshByBounds(sourceFbxMesh, "AK47_BarrelPart",
                    v => (v.y * scale) < -0.11f, scale);
                dict[ItemId.AK47_Receiver] = SliceMeshByBounds(sourceFbxMesh, "AK47_ReceiverPart",
                    v => (v.y * scale) >= -0.14f && (v.y * scale) <= 0.13f, scale);
                dict[ItemId.AK47_Stock] = SliceMeshByBounds(sourceFbxMesh, "AK47_StockPart",
                    v => (v.y * scale) > 0.08f, scale);
            }

            if (magMesh != null)
            {
                float mScale = magMesh.bounds.size.magnitude < 0.05f ? 100f : 1f;
                dict[ItemId.AK47_Bolt] = SliceMeshByBounds(magMesh, "AK47_BoltMagPart", _ => true, mScale);
            }
            else if (sourceFbxMesh != null)
            {
                float scale = sourceFbxMesh.bounds.size.magnitude < 0.05f ? 100f : 1f;
                dict[ItemId.AK47_Bolt] = SliceMeshByBounds(sourceFbxMesh, "AK47_BoltMagPart",
                    v => (v.y * scale) >= -0.08f && (v.y * scale) <= 0.12f, scale);
            }

            SaveMeshAsset(dict, ItemId.AK47_Stock, $"{PartsMeshDir}/AK47_StockPart.asset");
            SaveMeshAsset(dict, ItemId.AK47_Receiver, $"{PartsMeshDir}/AK47_ReceiverPart.asset");
            SaveMeshAsset(dict, ItemId.AK47_Barrel, $"{PartsMeshDir}/AK47_BarrelPart.asset");
            SaveMeshAsset(dict, ItemId.AK47_Bolt, $"{PartsMeshDir}/AK47_BoltMagPart.asset");
            return dict;
        }

        private static void SaveMeshAsset(Dictionary<ItemId, Mesh> dict, ItemId id, string assetPath)
        {
            if (!dict.TryGetValue(id, out var mesh) || mesh == null) return;
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            if (existing != null) AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(mesh, assetPath);
        }

        private static Mesh SliceMeshByBounds(Mesh source, string meshName, Predicate<Vector3> predicate, float vertexScale = 1f)
        {
            Vector3[] srcVerts = source.vertices;
            Vector3[] srcNormals = source.normals;
            Vector2[] srcUvs = source.uv;

            var newVerts = new List<Vector3>();
            var newNormals = new List<Vector3>();
            var newUvs = new List<Vector2>();
            var newTris = new List<int>();
            var remap = new Dictionary<int, int>();

            int[] allTris = source.triangles;
            for (int i = 0; i < allTris.Length; i += 3)
            {
                int i0 = allTris[i], i1 = allTris[i + 1], i2 = allTris[i + 2];
                Vector3 centroid = (srcVerts[i0] + srcVerts[i1] + srcVerts[i2]) / 3f;
                if (!predicate(centroid)) continue;

                for (int k = 0; k < 3; k++)
                {
                    int srcIdx = allTris[i + k];
                    if (!remap.TryGetValue(srcIdx, out int dstIdx))
                    {
                        dstIdx = newVerts.Count;
                        remap[srcIdx] = dstIdx;
                        newVerts.Add(srcVerts[srcIdx] * vertexScale);
                        newNormals.Add(srcNormals != null && srcNormals.Length > srcIdx ? srcNormals[srcIdx] : Vector3.up);
                        newUvs.Add(srcUvs != null && srcUvs.Length > srcIdx ? srcUvs[srcIdx] : Vector2.zero);
                    }
                    newTris.Add(dstIdx);
                }
            }

            // Center pivot cleanly
            if (newVerts.Count > 0)
            {
                Vector3 min = newVerts[0], max = newVerts[0];
                for (int i = 1; i < newVerts.Count; i++)
                {
                    min = Vector3.Min(min, newVerts[i]);
                    max = Vector3.Max(max, newVerts[i]);
                }
                Vector3 center = (min + max) * 0.5f;
                for (int i = 0; i < newVerts.Count; i++) newVerts[i] -= center;
            }

            var sliced = new Mesh { name = meshName };
            if (newVerts.Count > 65000) sliced.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            sliced.SetVertices(newVerts);
            sliced.SetNormals(newNormals);
            sliced.SetUVs(0, newUvs);
            sliced.SetTriangles(newTris, 0);
            sliced.RecalculateBounds();
            return sliced;
        }

        private static GameObject Create3DModelForItem(ItemId id, Dictionary<ItemId, Mesh> partMeshes, Material akMat)
        {
            var root = new GameObject($"Model_{id}");

            switch (id)
            {
                case ItemId.Water_Poor:
                    // Crushed dirty translucent bottle + crinkled band + grey cap
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0f, 0f, 0f), new Vector3(0.11f, 0.14f, 0.085f), new Vector3(0f, 0f, 6f),
                        CreateLitMaterial("Mat_WaterPoorBottle", new Color(0.58f, 0.65f, 0.68f), 0.1f, 0.62f));
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0.01f, -0.01f, 0f), new Vector3(0.115f, 0.045f, 0.09f), Vector3.zero,
                        CreateLitMaterial("Mat_WaterPoorLabel", new Color(0.28f, 0.44f, 0.52f), 0.0f, 0.25f));
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0f, 0.155f, 0f), new Vector3(0.045f, 0.025f, 0.045f), Vector3.zero,
                        CreateLitMaterial("Mat_PlasticCapDark", new Color(0.22f, 0.28f, 0.34f), 0.1f, 0.4f));
                    break;

                case ItemId.Water_Good:
                    // Metallic military canteen with neck & olive pouch
                    var canteenMat = CreateLitMaterial("Mat_CanteenMetal", new Color(0.46f, 0.50f, 0.48f), 0.85f, 0.55f);
                    AddPart(root, PrimitiveType.Sphere, Vector3.zero, new Vector3(0.18f, 0.24f, 0.11f), Vector3.zero, canteenMat);
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0f, -0.02f, 0f), new Vector3(0.182f, 0.065f, 0.115f), Vector3.zero,
                        CreateLitMaterial("Mat_CanteenCanvas", new Color(0.28f, 0.34f, 0.24f), 0.05f, 0.20f));
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0f, 0.135f, 0f), new Vector3(0.045f, 0.03f, 0.045f), Vector3.zero, canteenMat);
                    break;

                case ItemId.Water_Great:
                    // Heavy-duty cyan/charcoal insulated water purifier jug
                    var jugCyan = CreateLitMaterial("Mat_JugCyan", new Color(0.14f, 0.56f, 0.72f), 0.15f, 0.48f);
                    var jugDark = CreateLitMaterial("Mat_JugCharcoal", new Color(0.16f, 0.18f, 0.21f), 0.35f, 0.45f);
                    AddPart(root, PrimitiveType.Cube, Vector3.zero, new Vector3(0.24f, 0.28f, 0.18f), Vector3.zero, jugCyan);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.145f, 0f), new Vector3(0.25f, 0.045f, 0.19f), Vector3.zero, jugDark);
                    AddPart(root, PrimitiveType.Cube, new Vector3(-0.11f, 0.185f, 0f), new Vector3(0.03f, 0.04f, 0.14f), Vector3.zero, jugDark);
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0.05f, 0.185f, 0f), new Vector3(0.06f, 0.025f, 0.06f), Vector3.zero, jugDark);
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0f, -0.06f, 0.105f), new Vector3(0.03f, 0.035f, 0.03f), new Vector3(90f, 0f, 0f),
                        CreateLitMaterial("Mat_SpigotRed", new Color(0.82f, 0.24f, 0.18f), 0.1f, 0.4f));
                    break;

                case ItemId.Bandage_Poor:
                    // Stained makeshift cloth rag roll
                    var ragMat = CreateLitMaterial("Mat_BandageRag", new Color(0.76f, 0.70f, 0.58f), 0.0f, 0.18f);
                    AddPart(root, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.13f, 0.09f, 0.12f), new Vector3(0f, 0f, 90f), ragMat);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0.02f, -0.05f, 0.04f), new Vector3(0.11f, 0.015f, 0.09f), new Vector3(0f, 22f, -12f), ragMat);
                    break;

                case ItemId.Bandage_Good:
                    // Sealed olive combat gauze pack + red cross
                    AddPart(root, PrimitiveType.Cube, Vector3.zero, new Vector3(0.16f, 0.25f, 0.065f), Vector3.zero,
                        CreateLitMaterial("Mat_GauzeOlive", new Color(0.26f, 0.33f, 0.24f), 0.08f, 0.35f));
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.02f, 0.035f), new Vector3(0.10f, 0.11f, 0.008f), Vector3.zero,
                        CreateLitMaterial("Mat_WhiteTag", new Color(0.88f, 0.89f, 0.86f), 0f, 0.3f));
                    var redCross = CreateLitMaterial("Mat_RedCross", new Color(0.85f, 0.16f, 0.14f), 0.05f, 0.45f);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.02f, 0.041f), new Vector3(0.065f, 0.022f, 0.006f), Vector3.zero, redCross);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.02f, 0.041f), new Vector3(0.022f, 0.065f, 0.006f), Vector3.zero, redCross);
                    break;

                case ItemId.Bandage_Great:
                    // Hard-case tactical crimson/charcoal trauma medkit with handle + white cross
                    var medRed = CreateLitMaterial("Mat_MedkitCrimson", new Color(0.68f, 0.14f, 0.13f), 0.2f, 0.52f);
                    var medDark = CreateLitMaterial("Mat_MedkitLatch", new Color(0.15f, 0.16f, 0.18f), 0.6f, 0.5f);
                    var medWhite = CreateLitMaterial("Mat_MedkitWhite", new Color(0.95f, 0.96f, 0.97f), 0.05f, 0.45f);
                    AddPart(root, PrimitiveType.Cube, Vector3.zero, new Vector3(0.28f, 0.22f, 0.12f), Vector3.zero, medRed);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.125f, 0f), new Vector3(0.11f, 0.025f, 0.035f), Vector3.zero, medDark);
                    AddPart(root, PrimitiveType.Cube, new Vector3(-0.08f, 0.112f, 0.055f), new Vector3(0.04f, 0.02f, 0.015f), Vector3.zero, medDark);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0.08f, 0.112f, 0.055f), new Vector3(0.04f, 0.02f, 0.015f), Vector3.zero, medDark);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0f, 0.062f), new Vector3(0.11f, 0.035f, 0.008f), Vector3.zero, medWhite);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0f, 0.062f), new Vector3(0.035f, 0.11f, 0.008f), Vector3.zero, medWhite);
                    break;

                case ItemId.Food_Poor:
                    // Rusty tin can of beans
                    var rustTin = CreateLitMaterial("Mat_TinRust", new Color(0.46f, 0.34f, 0.26f), 0.72f, 0.35f);
                    var canLabel = CreateLitMaterial("Mat_BeanLabel", new Color(0.68f, 0.26f, 0.15f), 0.05f, 0.25f);
                    AddPart(root, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.14f, 0.105f, 0.135f), Vector3.zero, rustTin);
                    AddPart(root, PrimitiveType.Cylinder, new Vector3(0f, -0.005f, 0f), new Vector3(0.143f, 0.068f, 0.138f), Vector3.zero, canLabel);
                    break;

                case ItemId.Food_Good:
                    // Military tan MRE pouch
                    var mreTan = CreateLitMaterial("Mat_MRETan", new Color(0.56f, 0.45f, 0.31f), 0.08f, 0.32f);
                    var mreStripe = CreateLitMaterial("Mat_MREDark", new Color(0.20f, 0.18f, 0.14f), 0.1f, 0.28f);
                    AddPart(root, PrimitiveType.Cube, Vector3.zero, new Vector3(0.18f, 0.26f, 0.055f), Vector3.zero, mreTan);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.04f, 0.029f), new Vector3(0.16f, 0.035f, 0.005f), Vector3.zero, mreStripe);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, -0.02f, 0.029f), new Vector3(0.12f, 0.02f, 0.005f), Vector3.zero, mreStripe);
                    break;

                case ItemId.Food_Great:
                    // Tactical field ration box
                    var rationOlive = CreateLitMaterial("Mat_RationOlive", new Color(0.28f, 0.35f, 0.22f), 0.25f, 0.38f);
                    var rationBrass = CreateLitMaterial("Mat_RationAmber", new Color(0.84f, 0.62f, 0.20f), 0.45f, 0.48f);
                    AddPart(root, PrimitiveType.Cube, Vector3.zero, new Vector3(0.28f, 0.22f, 0.16f), Vector3.zero, rationOlive);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.115f, 0f), new Vector3(0.29f, 0.025f, 0.17f), Vector3.zero, rationOlive);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.01f, 0.083f), new Vector3(0.16f, 0.045f, 0.008f), Vector3.zero, rationBrass);
                    break;

                case ItemId.AK47_Receiver:
                case ItemId.AK47_Barrel:
                case ItemId.AK47_Stock:
                case ItemId.AK47_Bolt:
                    var meshGo = new GameObject("AK47_SubMesh", typeof(MeshFilter), typeof(MeshRenderer));
                    meshGo.transform.SetParent(root.transform, false);
                    meshGo.GetComponent<MeshFilter>().sharedMesh = partMeshes.TryGetValue(id, out var m) ? m : null;
                    meshGo.GetComponent<MeshRenderer>().sharedMaterial = akMat;
                    meshGo.transform.localScale = Vector3.one * 1.15f;
                    break;

                case ItemId.Ammo_762:
                    // Olive military ammo tin + brass stripe + exposed bullet silhouette top
                    var tinOlive = CreateLitMaterial("Mat_AmmoOlive", new Color(0.22f, 0.28f, 0.18f), 0.62f, 0.42f);
                    var brassMat = CreateLitMaterial("Mat_AmmoBrass", new Color(0.86f, 0.68f, 0.25f), 0.90f, 0.65f);
                    var copperMat = CreateLitMaterial("Mat_AmmoCopper", new Color(0.76f, 0.42f, 0.22f), 0.85f, 0.60f);
                    AddPart(root, PrimitiveType.Cube, Vector3.zero, new Vector3(0.22f, 0.16f, 0.14f), Vector3.zero, tinOlive);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0.085f, 0f), new Vector3(0.23f, 0.025f, 0.15f), Vector3.zero, tinOlive);
                    AddPart(root, PrimitiveType.Cube, new Vector3(0f, 0f, 0.072f), new Vector3(0.15f, 0.03f, 0.005f), Vector3.zero, brassMat);
                    for (int b = -1; b <= 1; b++)
                    {
                        AddPart(root, PrimitiveType.Cylinder, new Vector3(b * 0.042f, 0.115f, 0f), new Vector3(0.026f, 0.03f, 0.026f), Vector3.zero, brassMat);
                        AddPart(root, PrimitiveType.Capsule, new Vector3(b * 0.042f, 0.148f, 0f), new Vector3(0.022f, 0.02f, 0.022f), Vector3.zero, copperMat);
                    }
                    break;
            }

            return root;
        }

        private static void AddPart(GameObject parent, PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = pos;
            go.transform.localEulerAngles = euler;
            go.transform.localScale = scale;
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && mat != null) mr.sharedMaterial = mat;
        }

        private static Material CreateLitMaterial(string fileName, Color baseColor, float metallic, float smoothness)
        {
            string path = $"{MaterialDir}/{fileName}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader);
            mat.color = baseColor;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", baseColor);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", smoothness);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        private static GameObject SaveAsNetworkPickupPrefab(GameObject visualSource, ItemId itemId, string prefabPath)
        {
            var pickupRoot = new GameObject($"Pickup_{itemId}");
            var visualCopy = UnityEngine.Object.Instantiate(visualSource, pickupRoot.transform);
            visualCopy.name = "Model";
            SetLayerRecursively(pickupRoot, 0);

            // Compute tight combined bounds across renderers
            Bounds b = new Bounds(Vector3.zero, Vector3.one * 0.35f);
            var renderers = pickupRoot.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            }

            // Ensure generous interactable collider size (>= 0.45m so FPS raycast catches small items easily)
            var box = pickupRoot.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = Vector3.Max(b.size * 1.15f, new Vector3(0.42f, 0.42f, 0.42f));

            pickupRoot.AddComponent<NetworkObject>();
            var pickupComp = pickupRoot.AddComponent<WorldItemPickup>();
            pickupComp.InitializeItem(itemId, itemId == ItemId.Ammo_762 ? 15 : 1);

            GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(pickupRoot, prefabPath);
            UnityEngine.Object.DestroyImmediate(pickupRoot);
            return savedPrefab;
        }

        private static void RenderTransparent3DIcon(GameObject modelRoot, string pngAssetPath, Vector3 eulerAngles, float fillScale)
        {
            const int size = 256;

            var stage = new GameObject("StudioIconStage");
            stage.transform.position = new Vector3(0f, -600f, 0f);

            var subject = UnityEngine.Object.Instantiate(modelRoot, stage.transform);
            subject.transform.localPosition = Vector3.zero;
            subject.transform.localEulerAngles = eulerAngles;
            SetLayerRecursively(stage, StudioLayer);

            Bounds bounds = new Bounds(subject.transform.position, Vector3.one * 0.25f);
            var renderers = subject.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            }

            float maxExtent = Mathf.Max(0.05f, Mathf.Max(bounds.extents.x, Mathf.Max(bounds.extents.y, bounds.extents.z)));

            var camGo = new GameObject("StudioCam", typeof(Camera));
            camGo.transform.SetParent(stage.transform, false);
            camGo.transform.position = bounds.center + new Vector3(0f, 0f, 2.5f);
            camGo.transform.LookAt(bounds.center);
            var cam = camGo.GetComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = maxExtent / Mathf.Max(0.25f, fillScale);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 20f;
            cam.cullingMask = 1 << StudioLayer;
            cam.clearFlags = CameraClearFlags.SolidColor;

            // Key + Rim lights culled to StudioLayer (illuminating +Z front emblems cleanly)
            var keyGo = new GameObject("StudioKeyLight", typeof(Light));
            keyGo.transform.SetParent(stage.transform, false);
            keyGo.transform.rotation = Quaternion.Euler(35f, 148f, 0f);
            var keyLight = keyGo.GetComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.intensity = 2.1f;
            keyLight.color = new Color(1.0f, 0.94f, 0.85f);
            keyLight.cullingMask = 1 << StudioLayer;

            var rimGo = new GameObject("StudioRimLight", typeof(Light));
            rimGo.transform.SetParent(stage.transform, false);
            rimGo.transform.rotation = Quaternion.Euler(-20f, -35f, 0f);
            var rimLight = rimGo.GetComponent<Light>();
            rimLight.type = LightType.Directional;
            rimLight.intensity = 1.25f;
            rimLight.color = new Color(0.68f, 0.85f, 1.0f);
            rimLight.cullingMask = 1 << StudioLayer;

            var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            cam.targetTexture = rt;

            // Dual-pass black/white difference matting guarantees pixel-perfect alpha transparency under any URP config
            cam.backgroundColor = Color.black;
            cam.Render();
            var blackTex = ReadRenderTexture(rt, size);

            cam.backgroundColor = Color.white;
            cam.Render();
            var whiteTex = ReadRenderTexture(rt, size);

            var outTex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32[] bPix = blackTex.GetPixels32();
            Color32[] wPix = whiteTex.GetPixels32();
            Color32[] dst = new Color32[bPix.Length];

            for (int i = 0; i < dst.Length; i++)
            {
                float br = bPix[i].r / 255f, bg = bPix[i].g / 255f, bb = bPix[i].b / 255f;
                float wr = wPix[i].r / 255f, wg = wPix[i].g / 255f, wb = wPix[i].b / 255f;

                float alpha = 1f - ((wr - br) + (wg - bg) + (wb - bb)) / 3f;
                alpha = Mathf.Clamp01(alpha);
                if (alpha < 0.02f)
                {
                    dst[i] = new Color32(0, 0, 0, 0);
                }
                else
                {
                    float r = Mathf.Clamp01(br / alpha);
                    float g = Mathf.Clamp01(bg / alpha);
                    float b = Mathf.Clamp01(bb / alpha);
                    dst[i] = new Color32(
                        (byte)Mathf.RoundToInt(r * 255f),
                        (byte)Mathf.RoundToInt(g * 255f),
                        (byte)Mathf.RoundToInt(b * 255f),
                        (byte)Mathf.RoundToInt(alpha * 255f));
                }
            }

            outTex.SetPixels32(dst);
            outTex.Apply();
            File.WriteAllBytes(pngAssetPath, outTex.EncodeToPNG());

            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(blackTex);
            UnityEngine.Object.DestroyImmediate(whiteTex);
            UnityEngine.Object.DestroyImmediate(outTex);
            UnityEngine.Object.DestroyImmediate(stage);
        }

        private static Texture2D ReadRenderTexture(RenderTexture rt, int size)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            return tex;
        }

        private static void GenerateRadialProgressRingPng(string pngPath)
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float center = (size - 1) * 0.5f;
            float outerR = size * 0.45f;
            float innerR = size * 0.31f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float edgeOuter = Mathf.Clamp01((outerR - dist) * 0.6f);
                    float edgeInner = Mathf.Clamp01((dist - innerR) * 0.6f);
                    float a = Mathf.Min(edgeOuter, edgeInner);
                    if (a <= 0.01f)
                    {
                        tex.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
                    }
                    else
                    {
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
            }
            tex.Apply();
            File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void GenerateCabalSlotCellPng(string pngPath)
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color fill = new Color(0.11f, 0.13f, 0.16f, 0.90f);
            Color innerGrid = new Color(0.14f, 0.16f, 0.20f, 0.92f);
            Color border = new Color(0.28f, 0.31f, 0.37f, 1.0f);
            Color tick = new Color(0.75f, 0.78f, 0.84f, 0.95f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool isBorder = x <= 1 || x >= size - 2 || y <= 1 || y >= size - 2;
                    bool isCornerTick =
                        ((x <= 8 || x >= size - 9) && (y <= 1 || y >= size - 2)) ||
                        ((y <= 8 || y >= size - 9) && (x <= 1 || x >= size - 2));

                    if (isCornerTick)
                    {
                        tex.SetPixel(x, y, tick);
                    }
                    else if (isBorder)
                    {
                        tex.SetPixel(x, y, border);
                    }
                    else if ((x % 16 == 0) || (y % 16 == 0))
                    {
                        tex.SetPixel(x, y, innerGrid);
                    }
                    else
                    {
                        tex.SetPixel(x, y, fill);
                    }
                }
            }
            tex.Apply();
            File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }

        private static void ConfigureSpriteImporter(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        private static void EnsureDir(string dir)
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
    }
}
