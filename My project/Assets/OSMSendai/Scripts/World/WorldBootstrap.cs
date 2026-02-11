using System.Collections.Generic;
using System.Threading;
using OsmSendai.Data;
using OsmSendai.UI;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsmSendai.World
{
    public sealed class WorldBootstrap : MonoBehaviour
    {
        [Header("References")]
        public Camera worldCamera;
        public FloatingOrigin floatingOrigin;

        [Header("Tiles")]
        public float tileSizeMeters = 1024f;
        public int lod0RadiusTiles = 2;
        public float updateIntervalSeconds = 0.25f;

        [Header("Data")]
        [Tooltip("When true, shows deterministic debug tiles for areas that don't have real tile data yet.")]
        public bool enableDebugFallback = false;

        [Tooltip("Automatically increase tile loading radius when the camera is high (zoomed out) to avoid a visible square cutoff.")]
        public bool autoRadiusByCameraHeight = true;

        [Tooltip("How aggressively to increase radius based on camera height. 0.75 means +~1 tile per ~1.3 tiles of height.")]
        public float cameraHeightRadiusScale = 0.75f;

        [Tooltip("Maximum auto radius (tiles).")]
        public int maxAutoRadiusTiles = 24;

        [Header("Debug - Layer Visibility")]
        public bool showTerrain = true;
        public bool showBuildings = true;
        public bool showRoads = true;
        public bool showWater = true;
        public bool showLandcover = true;
        public bool showVegetation = true;
        public bool showGrass = true;
        public bool showRailways = true;
        public bool showPois = true;

        [Header("Physics")]
        public bool enableTerrainCollider = true;
        public bool enableBuildingCollider = true;

        [Header("Debug - Single Tile Mode")]
        [Tooltip("Only load a single tile for debugging")]
        public bool singleTileMode = false;
        public int debugTileX = 0;
        public int debugTileY = 0;

        [Header("Materials")]
        public Material terrainMaterial;
        public Material buildingsMaterial;
        public Material roadsMaterial;
        public Material waterMaterial;
        public Material landcoverMaterial;
        public Material vegetationMaterial;
        public Material grassMaterial;
        public Material railwaysMaterial;
        public Material poiMaterial;

        [Header("Grass Instancing")]
        [Tooltip("Blade mesh for GPU-instanced grass (e.g. grass_blade.fbx). Auto-generated if null.")]
        public Mesh grassBladeMesh;
        [Tooltip("Compute shader for grass frustum culling. Auto-loaded from ThirdParty/GrassInstancer if null.")]
        public ComputeShader grassCullingShader;

        /// <summary>
        /// Runtime-created dynamic Font that supports Japanese (CJK) characters.
        /// Loaded from OS system fonts.  Shared by all label/UI code.
        /// </summary>
        public static Font JapaneseFont { get; private set; }

        private TileManager _tileManager;
        private ITileGenerator _generator;
        private CancellationTokenSource _cts;
        private PlacesData _placesData;

        private void Awake()
        {
            if (worldCamera == null) worldCamera = Camera.main;
            EnsureJapaneseFont();
            EnsureDefaultMaterials();

            // Prefer real (preprocessed) tile data from StreamingAssets. Falls back to debug visuals when no tiles exist yet.
            var store = new StreamingAssetsTileStore("OSMSendai");
            var streaming = new StreamingTileGenerator(store);
            _generator = enableDebugFallback ? new CompositeTileGenerator(streaming, new DebugTileGenerator()) : streaming;

            _tileManager = new TileManager(transform, _generator)
            {
                TileSizeMeters = tileSizeMeters,
                Lod0RadiusTiles = lod0RadiusTiles,
                TerrainMaterial = terrainMaterial,
                BuildingsMaterial = buildingsMaterial,
                RoadsMaterial = roadsMaterial,
                WaterMaterial = waterMaterial,
                LandcoverMaterial = landcoverMaterial,
                VegetationMaterial = vegetationMaterial,
                ShowTerrain = showTerrain,
                ShowBuildings = showBuildings,
                ShowRoads = showRoads,
                ShowWater = showWater,
                ShowLandcover = showLandcover,
                ShowVegetation = showVegetation,
                ShowGrass = showGrass,
                GrassMaterial = grassMaterial,
                GrassBladeMesh = grassBladeMesh,
                GrassCullingShader = grassCullingShader,
                GrassCamera = worldCamera,
                EnableTerrainCollider = enableTerrainCollider,
                EnableBuildingCollider = enableBuildingCollider,
                SingleTileMode = singleTileMode,
                DebugTileX = debugTileX,
                DebugTileY = debugTileY,
                RailwaysMaterial = railwaysMaterial,
                ShowRailways = showRailways,
                PoiMaterial = poiMaterial,
                ShowPois = showPois,
            };

            _cts = new CancellationTokenSource();

            InitUISystemsAsync(store);
        }

        private async void InitUISystemsAsync(StreamingAssetsTileStore store)
        {
            _placesData = await store.LoadPlacesAsync(_cts.Token);

            if (_placesData != null && _placesData.places.Length > 0)
            {
                var player = worldCamera != null ? worldCamera.transform : transform;
                var notification = gameObject.AddComponent<AreaNotification>();
                notification.Initialize(_placesData.places, floatingOrigin, player);
            }

            // Map overlay
            var mapBytes = await store.TryLoadMapImageAsync(_cts.Token);
            var mapMeta = await store.LoadMapMetadataAsync(_cts.Token);
            if (mapBytes != null && mapMeta != null)
            {
                var mapOverlay = gameObject.AddComponent<MapOverlay>();
                mapOverlay.Initialize(mapBytes, mapMeta, _placesData, floatingOrigin, worldCamera != null ? worldCamera.transform : transform);
            }
        }

        private void EnsureDefaultMaterials()
        {
            // Light beige/cream terrain with vertex-color landcover tinting.
            if (terrainMaterial == null)
            {
                var terrainShader = Shader.Find("OSMSendai/TerrainVertexColor");
                if (terrainShader != null)
                {
                    terrainMaterial = new Material(terrainShader);
                    terrainMaterial.SetColor("_BaseColor", new Color(0.88f, 0.86f, 0.82f, 1f));
                }
                else
                {
                    Debug.LogWarning("[WorldBootstrap] Shader 'OSMSendai/TerrainVertexColor' not found, falling back to Lit material.");
                    terrainMaterial = CreateLitMaterial(new Color(0.88f, 0.86f, 0.82f, 1f));
                }
            }
            // Light gray buildings with slight blue tint (like F4Map).
            // Render queue Geometry+3 so buildings draw after water (G+1) and roads (G+2).
            if (buildingsMaterial == null)
            {
                buildingsMaterial = CreateLitMaterial(new Color(0.85f, 0.85f, 0.90f, 1f));
                buildingsMaterial.renderQueue = 2003;
            }
            // Dark gray roads — uses depth-offset shader to avoid terrain clipping
            if (roadsMaterial == null) roadsMaterial = CreateGroundOverlayMaterial(new Color(0.35f, 0.35f, 0.38f, 1f), transparent: false);
            // Blue water — animated water surface shader with waves, depth fade, and specular.
            if (waterMaterial == null)
            {
                var waterShader = Shader.Find("OSMSendai/WaterSurface");
                if (waterShader != null)
                {
                    waterMaterial = new Material(waterShader);
                    // Load noise texture from Resources or create a procedural one.
                    var noiseTex = Resources.Load<Texture2D>("WaterNoise");
                    if (noiseTex == null)
                    {
                        noiseTex = GenerateProceduralNoiseTexture(256);
                    }
                    waterMaterial.SetTexture("_NoiseTex", noiseTex);
                }
                else
                {
                    Debug.LogWarning("[WorldBootstrap] Shader 'OSMSendai/WaterSurface' not found, falling back to GroundOverlayTransparent.");
                    waterMaterial = CreateGroundOverlayMaterial(new Color(0.3f, 0.5f, 0.85f, 0.75f), transparent: true);
                }
            }
            // Medium green landcover — same GroundOverlay shader as roads (Offset -1,-1 for depth bias).
            // renderQueue 2001: after terrain (2000), before roads (2002).
            if (landcoverMaterial == null)
            {
                landcoverMaterial = CreateGroundOverlayMaterial(new Color(0.35f, 0.55f, 0.25f, 1f), transparent: false);
                landcoverMaterial.renderQueue = 2001;
            }
            // Dark green vegetation — standard Lit material
            if (vegetationMaterial == null) vegetationMaterial = CreateLitMaterial(new Color(0.18f, 0.40f, 0.12f, 1f));
            // Warm brown railways — uses GroundOverlay shader like roads
            if (railwaysMaterial == null)
            {
                railwaysMaterial = CreateGroundOverlayMaterial(new Color(0.55f, 0.45f, 0.35f, 1f), transparent: false);
                railwaysMaterial.renderQueue = 2002;
            }
            // Red POI markers
            if (poiMaterial == null) poiMaterial = CreateLitMaterial(new Color(0.9f, 0.2f, 0.2f, 1f));
            // GPU-instanced grass via DrawMeshInstancedIndirect
            if (grassMaterial == null)
            {
                var grassShader = Shader.Find("OSMSendai/GrassIndirect");
                if (grassShader != null)
                {
                    grassMaterial = new Material(grassShader);
                    grassMaterial.SetColor("_PrimaryCol", new Color(0.25f, 0.45f, 0.15f, 1f));
                    grassMaterial.SetColor("_SecondaryCol", new Color(0.35f, 0.58f, 0.20f, 1f));
                    grassMaterial.SetColor("_TipColor", new Color(0.50f, 0.72f, 0.28f, 1f));
                    grassMaterial.SetColor("_AOColor", new Color(0.15f, 0.22f, 0.10f, 1f));
                    grassMaterial.SetFloat("_WindStrength", 3.0f);
                    grassMaterial.SetFloat("_WindNoiseScale", 2.0f);
                    grassMaterial.SetVector("_WindSpeed", new Vector4(-3f, 2f, 0f, 0f));
                    grassMaterial.renderQueue = 2001;
                }
            }
            // Auto-generate a simple blade mesh if none assigned
            if (grassBladeMesh == null)
            {
                grassBladeMesh = GenerateGrassBladeMesh();
            }
            // Auto-load culling compute shader if none assigned
            if (grassCullingShader == null)
            {
                grassCullingShader = Resources.Load<ComputeShader>("Visibility");
                if (grassCullingShader == null)
                {
                    // Try finding it in the project assets at runtime
                    #if UNITY_EDITOR
                    var guids = UnityEditor.AssetDatabase.FindAssets("Visibility t:ComputeShader");
                    if (guids.Length > 0)
                    {
                        var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                        grassCullingShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                    }
                    #endif
                }
            }
        }

        private static void EnsureJapaneseFont()
        {
            if (JapaneseFont != null) return;

            // Pick the first installed CJK-capable font.
            var installed = new HashSet<string>(Font.GetOSInstalledFontNames());
            string[] preferred =
            {
                "Hiragino Sans", "Hiragino Kaku Gothic ProN",   // macOS
                "Yu Gothic UI", "Yu Gothic", "Meiryo",          // Windows
                "Noto Sans CJK JP", "Noto Sans JP",             // Linux / cross-platform
            };

            string chosen = null;
            foreach (var name in preferred)
            {
                if (installed.Contains(name)) { chosen = name; break; }
            }

            if (chosen == null)
            {
                Debug.LogWarning("[WorldBootstrap] No Japanese system font found — CJK labels will show boxes.");
                return;
            }

            JapaneseFont = Font.CreateDynamicFontFromOSFont(chosen, 36);
            Debug.Log($"[WorldBootstrap] Loaded Japanese font: '{chosen}'");
        }

        /// <summary>
        /// Creates a material using the GroundOverlay shader (with depth offset)
        /// so that roads/water render on top of terrain without z-fighting.
        /// Falls back to a standard Lit material if the shader is not found.
        /// </summary>
        private static Material CreateGroundOverlayMaterial(Color color, bool transparent)
        {
            var shaderName = transparent ? "OSMSendai/GroundOverlayTransparent" : "OSMSendai/GroundOverlay";
            var shader = Shader.Find(shaderName);
            if (shader != null)
            {
                var mat = new Material(shader);
                mat.SetColor("_BaseColor", color);
                return mat;
            }

            // Fallback: use standard Lit material if custom shader not found.
            Debug.LogWarning($"[WorldBootstrap] Shader '{shaderName}' not found, falling back to Lit material.");
            return CreateLitMaterial(color, transparent);
        }

        /// <summary>
        /// Generates a tileable Perlin-like noise texture at runtime for the water shader.
        /// </summary>
        private static Texture2D GenerateProceduralNoiseTexture(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.R8, false);
            tex.name = "ProceduralWaterNoise";
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;

            var pixels = new Color[size * size];
            // Multi-octave value noise using Unity's Mathf.PerlinNoise.
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var fx = (float)x / size;
                    var fy = (float)y / size;
                    var v = 0f;
                    v += Mathf.PerlinNoise(fx * 4f, fy * 4f) * 0.5f;
                    v += Mathf.PerlinNoise(fx * 8f + 5.3f, fy * 8f + 7.1f) * 0.25f;
                    v += Mathf.PerlinNoise(fx * 16f + 13.7f, fy * 16f + 17.9f) * 0.125f;
                    v = Mathf.Clamp01(v / 0.875f); // normalize
                    pixels[y * size + x] = new Color(v, v, v, 1f);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true); // makeNoLongerReadable for memory savings
            return tex;
        }

        /// <summary>
        /// Generates a small procedural grass blade mesh (tapered quad strip, 7 verts).
        /// UV.y = 0 at base, 1 at tip — used by the shader for wind deformation.
        /// Origin is at the blade base.
        /// </summary>
        private static Mesh GenerateGrassBladeMesh()
        {
            const float w = 0.04f;  // half-width at base
            const float h = 0.5f;   // total height

            var verts = new Vector3[]
            {
                new Vector3(-w,      0f,    0f),   // 0: base-left
                new Vector3( w,      0f,    0f),   // 1: base-right
                new Vector3(-w*0.8f, h*0.33f, 0f), // 2: mid1-left
                new Vector3( w*0.8f, h*0.33f, 0f), // 3: mid1-right
                new Vector3(-w*0.5f, h*0.66f, 0f), // 4: mid2-left
                new Vector3( w*0.5f, h*0.66f, 0f), // 5: mid2-right
                new Vector3( 0f,     h,       0f), // 6: tip
            };

            var uvs = new Vector2[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 0.33f),
                new Vector2(1f, 0.33f),
                new Vector2(0f, 0.66f),
                new Vector2(1f, 0.66f),
                new Vector2(0.5f, 1f),
            };

            var normals = new Vector3[7];
            for (var i = 0; i < 7; i++) normals[i] = Vector3.back;

            var tris = new int[]
            {
                0, 2, 1,  1, 2, 3,  // base quad
                2, 4, 3,  3, 4, 5,  // mid quad
                4, 6, 5,            // top tri
            };

            var mesh = new Mesh { name = "GrassBlade(Procedural)" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        public static Material CreateLitMaterial(Color color, bool transparent = false)
        {
            var useUrp = GraphicsSettings.currentRenderPipeline != null &&
                         GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("UniversalRenderPipelineAsset");

            var shader = useUrp ? Shader.Find("Universal Render Pipeline/Lit") : null;
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Diffuse");

            var material = new Material(shader) { color = color };
            if (transparent && shader != null && shader.name == "Standard")
            {
                material.SetFloat("_Mode", 3f);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = 3000;
            }
            return material;
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }

        private float _nextUpdateTime;

        private void Update()
        {
            if (worldCamera == null) return;
            if (Time.unscaledTime < _nextUpdateTime) return;
            _nextUpdateTime = Time.unscaledTime + Mathf.Max(0.05f, updateIntervalSeconds);

            if (autoRadiusByCameraHeight)
            {
                var h = Mathf.Max(0f, worldCamera.transform.position.y);
                var extra = Mathf.CeilToInt((h / Mathf.Max(1f, tileSizeMeters)) * cameraHeightRadiusScale);
                _tileManager.Lod0RadiusTiles = Mathf.Clamp(lod0RadiusTiles + extra, lod0RadiusTiles, maxAutoRadiusTiles);
            }
            else
            {
                _tileManager.Lod0RadiusTiles = lod0RadiusTiles;
            }

            _tileManager.UpdateVisibleTiles(worldCamera.transform.position, _cts.Token);
        }
    }
}
