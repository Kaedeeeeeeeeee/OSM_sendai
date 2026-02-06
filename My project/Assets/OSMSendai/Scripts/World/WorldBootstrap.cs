using System.Threading;
using OsmSendai.Data;
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

        private TileManager _tileManager;
        private ITileGenerator _generator;
        private CancellationTokenSource _cts;

        private void Awake()
        {
            if (worldCamera == null) worldCamera = Camera.main;
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
                EnableTerrainCollider = enableTerrainCollider,
                EnableBuildingCollider = enableBuildingCollider,
                SingleTileMode = singleTileMode,
                DebugTileX = debugTileX,
                DebugTileY = debugTileY,
            };

            _cts = new CancellationTokenSource();
        }

        private void EnsureDefaultMaterials()
        {
            // Light beige/cream terrain (like sidewalks/ground)
            if (terrainMaterial == null) terrainMaterial = CreateLitMaterial(new Color(0.88f, 0.86f, 0.82f, 1f));
            // Light gray buildings with slight blue tint (like F4Map).
            // Render queue Geometry+3 so buildings draw after water (G+1) and roads (G+2).
            if (buildingsMaterial == null)
            {
                buildingsMaterial = CreateLitMaterial(new Color(0.85f, 0.85f, 0.90f, 1f));
                buildingsMaterial.renderQueue = 2003;
            }
            // Dark gray roads — uses depth-offset shader to avoid terrain clipping
            if (roadsMaterial == null) roadsMaterial = CreateGroundOverlayMaterial(new Color(0.35f, 0.35f, 0.38f, 1f), transparent: false);
            // Blue water — uses depth-offset transparent shader
            if (waterMaterial == null) waterMaterial = CreateGroundOverlayMaterial(new Color(0.3f, 0.5f, 0.85f, 0.75f), transparent: true);
            // Medium green landcover — same GroundOverlay shader as roads (Offset -1,-1 for depth bias).
            // renderQueue 2001: after terrain (2000), before roads (2002).
            if (landcoverMaterial == null)
            {
                landcoverMaterial = CreateGroundOverlayMaterial(new Color(0.35f, 0.55f, 0.25f, 1f), transparent: false);
                landcoverMaterial.renderQueue = 2001;
            }
            // Dark green vegetation — standard Lit material
            if (vegetationMaterial == null) vegetationMaterial = CreateLitMaterial(new Color(0.18f, 0.40f, 0.12f, 1f));
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
