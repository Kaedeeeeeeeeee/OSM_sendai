using System.IO;
using OsmSendai.Data;
using UnityEngine;

namespace OsmSendai.Debugging
{
    /// <summary>
    /// Attach to the same GameObject as WorldBootstrap to get debug info in the console.
    /// </summary>
    public class TileDebugger : MonoBehaviour
    {
        [Header("Debug Options")]
        public bool logOnStart = true;
        public bool testSingleTile = true;
        public int testTileX = 0;
        public int testTileY = 0;

        private void Start()
        {
            if (logOnStart)
            {
                LogTileInfo();
            }
        }

        [ContextMenu("Log Tile Info")]
        public void LogTileInfo()
        {
            var tilePath = Path.Combine(Application.streamingAssetsPath, "OSMSendai", "tiles", $"tile_0_{testTileX}_{testTileY}.json");

            if (!File.Exists(tilePath))
            {
                UnityEngine.Debug.LogError($"Tile not found: {tilePath}");
                return;
            }

            var json = File.ReadAllText(tilePath);
            var payload = JsonUtility.FromJson<TilePayload>(json);

            if (payload == null)
            {
                UnityEngine.Debug.LogError("Failed to deserialize tile payload!");
                return;
            }

            UnityEngine.Debug.Log($"=== Tile ({testTileX}, {testTileY}) Debug Info ===");
            UnityEngine.Debug.Log($"Buildings: {payload.buildings?.Length ?? 0}");
            UnityEngine.Debug.Log($"Roads: {payload.roads?.Length ?? 0}");
            UnityEngine.Debug.Log($"Waters: {payload.waters?.Length ?? 0}");
            UnityEngine.Debug.Log($"Waterways: {payload.waterways?.Length ?? 0}");

            // Test building deserialization
            if (payload.buildings != null && payload.buildings.Length > 0)
            {
                var b = payload.buildings[0];
                UnityEngine.Debug.Log($"\nFirst building:");
                UnityEngine.Debug.Log($"  Height: {b.heightMeters}m");
                UnityEngine.Debug.Log($"  Vertices: {b.vertices?.Length ?? 0}");

                if (b.vertices != null && b.vertices.Length > 0)
                {
                    UnityEngine.Debug.Log($"  First vertex: ({b.vertices[0].x}, {b.vertices[0].y})");

                    // Check if vertices are zero (deserialization failure)
                    bool allZero = true;
                    foreach (var v in b.vertices)
                    {
                        if (v.x != 0 || v.y != 0)
                        {
                            allZero = false;
                            break;
                        }
                    }

                    if (allZero)
                    {
                        UnityEngine.Debug.LogError("PROBLEM: All vertices are zero! JSON deserialization failed.");
                    }
                    else
                    {
                        UnityEngine.Debug.Log("  Vertices deserialized correctly!");
                    }
                }
            }

            // Test road deserialization
            if (payload.roads != null && payload.roads.Length > 0)
            {
                var r = payload.roads[0];
                UnityEngine.Debug.Log($"\nFirst road:");
                UnityEngine.Debug.Log($"  Width: {r.widthMeters}m");
                UnityEngine.Debug.Log($"  Points: {r.points?.Length ?? 0}");

                if (r.points != null && r.points.Length > 0)
                {
                    UnityEngine.Debug.Log($"  First point: ({r.points[0].x}, {r.points[0].y})");

                    bool allZero = true;
                    foreach (var p in r.points)
                    {
                        if (p.x != 0 || p.y != 0)
                        {
                            allZero = false;
                            break;
                        }
                    }

                    if (allZero)
                    {
                        UnityEngine.Debug.LogError("PROBLEM: All road points are zero! JSON deserialization failed.");
                    }
                    else
                    {
                        UnityEngine.Debug.Log("  Road points deserialized correctly!");
                    }
                }
            }

            // Test triangulation on buildings
            int triangulationSuccess = 0;
            int triangulationFailed = 0;

            if (payload.buildings != null)
            {
                foreach (var b in payload.buildings)
                {
                    if (b?.vertices == null || b.vertices.Length < 3)
                    {
                        triangulationFailed++;
                        continue;
                    }

                    if (World.PolygonTriangulator.TryTriangulate(b.vertices, out _))
                    {
                        triangulationSuccess++;
                    }
                    else
                    {
                        triangulationFailed++;
                    }
                }
            }

            UnityEngine.Debug.Log($"\nTriangulation results:");
            UnityEngine.Debug.Log($"  Success: {triangulationSuccess}");
            UnityEngine.Debug.Log($"  Failed (using AABB fallback): {triangulationFailed}");

            float failRate = payload.buildings.Length > 0
                ? (triangulationFailed / (float)payload.buildings.Length) * 100f
                : 0f;

            if (failRate > 20f)
            {
                UnityEngine.Debug.LogWarning($"  High failure rate: {failRate:F1}% - this may cause visual issues!");
            }
        }
    }
}
