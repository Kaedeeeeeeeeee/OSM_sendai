using System.IO;
using OsmSendai.Data;
using UnityEditor;
using UnityEngine;

namespace OsmSendai.Editor
{
    public static class TileDeserializationTest
    {
        [MenuItem("OSM Sendai/Debug/Test Tile Deserialization")]
        public static void TestDeserialization()
        {
            var tilePath = Path.Combine(Application.streamingAssetsPath, "OSMSendai", "tiles", "tile_0_0_0.json");

            if (!File.Exists(tilePath))
            {
                UnityEngine.Debug.LogError($"Tile not found: {tilePath}");
                return;
            }

            var json = File.ReadAllText(tilePath);
            UnityEngine.Debug.Log($"JSON length: {json.Length} characters");

            var payload = JsonUtility.FromJson<TilePayload>(json);

            if (payload == null)
            {
                UnityEngine.Debug.LogError("FAILED: JsonUtility returned null!");
                return;
            }

            UnityEngine.Debug.Log($"=== Deserialization Test Results ===");
            UnityEngine.Debug.Log($"Tile: ({payload.tx}, {payload.ty}), LOD: {payload.lod}");
            UnityEngine.Debug.Log($"Buildings array length: {payload.buildings?.Length ?? 0}");
            UnityEngine.Debug.Log($"Roads array length: {payload.roads?.Length ?? 0}");

            // Test buildings
            if (payload.buildings != null && payload.buildings.Length > 0)
            {
                var b = payload.buildings[0];
                UnityEngine.Debug.Log($"\n--- First Building ---");
                UnityEngine.Debug.Log($"  heightMeters: {b.heightMeters}");
                UnityEngine.Debug.Log($"  vertices array: {(b.vertices != null ? b.vertices.Length.ToString() : "NULL")}");

                if (b.vertices != null && b.vertices.Length > 0)
                {
                    UnityEngine.Debug.Log($"  First vertex: x={b.vertices[0].x}, y={b.vertices[0].y}");
                    UnityEngine.Debug.Log($"  Second vertex: x={b.vertices[1].x}, y={b.vertices[1].y}");

                    // Check if vertices are all zero (common deserialization issue)
                    bool allZero = true;
                    float sumX = 0, sumY = 0;
                    foreach (var v in b.vertices)
                    {
                        sumX += Mathf.Abs(v.x);
                        sumY += Mathf.Abs(v.y);
                        if (v.x != 0 || v.y != 0) allZero = false;
                    }

                    if (allZero)
                    {
                        UnityEngine.Debug.LogError("  PROBLEM: All vertices are ZERO! Deserialization failed!");
                        UnityEngine.Debug.LogError("  This means Unity's JsonUtility is not correctly deserializing Vector2 arrays.");
                    }
                    else
                    {
                        UnityEngine.Debug.Log($"  SUCCESS: Vertices deserialized correctly! Sum: ({sumX:F2}, {sumY:F2})");
                    }
                }
                else
                {
                    UnityEngine.Debug.LogError("  PROBLEM: vertices array is null or empty!");
                }
            }

            // Test roads
            if (payload.roads != null && payload.roads.Length > 0)
            {
                var r = payload.roads[0];
                UnityEngine.Debug.Log($"\n--- First Road ---");
                UnityEngine.Debug.Log($"  widthMeters: {r.widthMeters}");
                UnityEngine.Debug.Log($"  points array: {(r.points != null ? r.points.Length.ToString() : "NULL")}");

                if (r.points != null && r.points.Length > 0)
                {
                    UnityEngine.Debug.Log($"  First point: x={r.points[0].x}, y={r.points[0].y}");

                    bool allZero = true;
                    foreach (var p in r.points)
                    {
                        if (p.x != 0 || p.y != 0) allZero = false;
                    }

                    if (allZero)
                    {
                        UnityEngine.Debug.LogError("  PROBLEM: All road points are ZERO!");
                    }
                    else
                    {
                        UnityEngine.Debug.Log("  SUCCESS: Road points deserialized correctly!");
                    }
                }
            }

            UnityEngine.Debug.Log("\n=== Test Complete ===");
        }

        [MenuItem("OSM Sendai/Debug/Log Scene Tile Info")]
        public static void LogSceneTileInfo()
        {
            var tileObjects = GameObject.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);

            UnityEngine.Debug.Log($"=== Scene Mesh Info ===");
            UnityEngine.Debug.Log($"Total MeshFilters in scene: {tileObjects.Length}");

            int buildingMeshes = 0;
            int roadMeshes = 0;
            int terrainMeshes = 0;
            int totalVerts = 0;

            foreach (var mf in tileObjects)
            {
                if (mf.sharedMesh == null) continue;

                var name = mf.sharedMesh.name;
                var vertCount = mf.sharedMesh.vertexCount;
                totalVerts += vertCount;

                if (name.Contains("Building")) buildingMeshes++;
                else if (name.Contains("Road")) roadMeshes++;
                else if (name.Contains("Terrain")) terrainMeshes++;

                // Log first few significant meshes
                if (vertCount > 100 && (buildingMeshes + roadMeshes) < 5)
                {
                    UnityEngine.Debug.Log($"  {mf.gameObject.name}: {name} ({vertCount} verts)");
                }
            }

            UnityEngine.Debug.Log($"\nSummary:");
            UnityEngine.Debug.Log($"  Building meshes: {buildingMeshes}");
            UnityEngine.Debug.Log($"  Road meshes: {roadMeshes}");
            UnityEngine.Debug.Log($"  Terrain meshes: {terrainMeshes}");
            UnityEngine.Debug.Log($"  Total vertices: {totalVerts}");

            if (roadMeshes == 0)
            {
                UnityEngine.Debug.LogWarning("WARNING: No road meshes found! Roads may not be rendering.");
            }
        }
    }
}
