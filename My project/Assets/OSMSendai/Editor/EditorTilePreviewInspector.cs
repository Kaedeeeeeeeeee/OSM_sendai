using OsmSendai.Data;
using OsmSendai.World;
using UnityEditor;
using UnityEngine;

namespace OsmSendai.EditorTools
{
    [CustomEditor(typeof(EditorTilePreview))]
    public sealed class EditorTilePreviewInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();

            var preview = (EditorTilePreview)target;

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Load Preview", GUILayout.Height(30)))
                {
                    LoadPreview(preview);
                }

                if (GUILayout.Button("Clear Preview", GUILayout.Height(30)))
                {
                    ClearPreview(preview);
                }
            }
        }

        private static void ClearPreview(EditorTilePreview preview)
        {
            // Destroy all DontSave children.
            for (var i = preview.transform.childCount - 1; i >= 0; i--)
            {
                var child = preview.transform.GetChild(i);
                if ((child.gameObject.hideFlags & HideFlags.DontSave) != 0)
                {
                    DestroyImmediate(child.gameObject);
                }
            }
        }

        private static void LoadPreview(EditorTilePreview preview)
        {
            ClearPreview(preview);

            var store = new StreamingAssetsTileStore(preview.dataFolder);

            // Load tileset metadata to get tile size.
            var meta = store.LoadTilesetSync();
            var tileSize = meta.tileSizeMeters;
            preview.tileSizeMeters = tileSize;

            // Ensure materials.
            if (preview.terrainMaterial == null)
            {
                var terrainShader = Shader.Find("OSMSendai/TerrainVertexColor");
                if (terrainShader != null)
                {
                    preview.terrainMaterial = new Material(terrainShader);
                    preview.terrainMaterial.SetColor("_BaseColor", new Color(0.88f, 0.86f, 0.82f, 1f));
                }
                else
                {
                    preview.terrainMaterial = WorldBootstrap.CreateLitMaterial(new Color(0.88f, 0.86f, 0.82f, 1f));
                }
            }
            if (preview.buildingsMaterial == null)
                preview.buildingsMaterial = WorldBootstrap.CreateLitMaterial(new Color(0.85f, 0.85f, 0.90f, 1f));
            if (preview.roadsMaterial == null)
                preview.roadsMaterial = WorldBootstrap.CreateLitMaterial(new Color(0.35f, 0.35f, 0.38f, 1f));
            if (preview.waterMaterial == null)
                preview.waterMaterial = WorldBootstrap.CreateLitMaterial(new Color(0.3f, 0.5f, 0.85f, 0.75f), transparent: true);
            if (preview.landcoverMaterial == null)
                preview.landcoverMaterial = WorldBootstrap.CreateLitMaterial(new Color(0.35f, 0.55f, 0.25f, 1f));
            if (preview.vegetationMaterial == null)
                preview.vegetationMaterial = WorldBootstrap.CreateLitMaterial(new Color(0.18f, 0.40f, 0.12f, 1f));
            if (preview.grassMaterial == null)
            {
                var grassShader = Shader.Find("OSMSendai/GrassGeometry");
                if (grassShader != null)
                {
                    preview.grassMaterial = new Material(grassShader);
                    preview.grassMaterial.SetColor("_Color", new Color(0.25f, 0.45f, 0.15f, 1f));
                    preview.grassMaterial.SetColor("_Color2", new Color(0.45f, 0.70f, 0.25f, 1f));
                    preview.grassMaterial.renderQueue = 2001;
                }
            }

            var r = preview.radiusTiles;
            var total = (2 * r + 1) * (2 * r + 1);
            var count = 0;

            try
            {
                for (var dx = -r; dx <= r; dx++)
                {
                    for (var dy = -r; dy <= r; dy++)
                    {
                        var tx = preview.centerTileX + dx;
                        var ty = preview.centerTileY + dy;
                        count++;

                        EditorUtility.DisplayProgressBar(
                            "Loading Tile Preview",
                            $"Tile ({tx}, {ty})  —  {count}/{total}",
                            (float)count / total);

                        var payload = store.TryLoadTileSync(0, tx, ty);
                        if (payload == null) continue;

                        var heightmap = store.TryLoadHeightmapSync(0, tx, ty);
                        var result = StreamingTileGenerator.BuildSync(payload, heightmap, tileSize);

                        CreateTileObject(preview, tx, ty, tileSize, result);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static void CreateTileObject(
            EditorTilePreview preview, int tx, int ty, float tileSize, TileBuildResult result)
        {
            var tileGo = new GameObject($"Preview Tile ({tx}, {ty})");
            tileGo.transform.SetParent(preview.transform, false);
            tileGo.transform.localPosition = new Vector3(tx * tileSize, 0f, ty * tileSize);
            tileGo.hideFlags = HideFlags.DontSave;

            if (preview.showTerrain && result.TerrainMesh != null)
                CreateSubMesh(tileGo.transform, "Terrain", result.TerrainMesh, preview.terrainMaterial);

            if (preview.showBuildings && result.BuildingsMesh != null && result.BuildingsMesh.vertexCount > 0)
                CreateSubMesh(tileGo.transform, "Buildings", result.BuildingsMesh, preview.buildingsMaterial);

            if (preview.showRoads && result.RoadsMesh != null && result.RoadsMesh.vertexCount > 0)
                CreateSubMesh(tileGo.transform, "Roads", result.RoadsMesh, preview.roadsMaterial);

            if (preview.showWater && result.WaterMesh != null && result.WaterMesh.vertexCount > 0)
                CreateSubMesh(tileGo.transform, "Water", result.WaterMesh, preview.waterMaterial);

            if (preview.showLandcover && result.LandcoverMesh != null && result.LandcoverMesh.vertexCount > 0)
                CreateSubMesh(tileGo.transform, "Landcover", result.LandcoverMesh, preview.landcoverMaterial);

            if (preview.showVegetation && result.VegetationMesh != null && result.VegetationMesh.vertexCount > 0)
                CreateSubMesh(tileGo.transform, "Vegetation", result.VegetationMesh, preview.vegetationMaterial);

            if (preview.showGrass && preview.grassMaterial != null && result.GrassMesh != null && result.GrassMesh.vertexCount > 0)
                CreateSubMesh(tileGo.transform, "Grass", result.GrassMesh, preview.grassMaterial);
        }

        private static void CreateSubMesh(Transform parent, string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.hideFlags = HideFlags.DontSave;

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
        }
    }
}
