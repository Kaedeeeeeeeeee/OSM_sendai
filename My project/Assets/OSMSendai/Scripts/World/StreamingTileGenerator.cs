using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OsmSendai.Data;
using UnityEngine;

namespace OsmSendai.World
{
    public sealed class StreamingTileGenerator : ITileGenerator
    {
        private readonly StreamingAssetsTileStore _store;

        public StreamingTileGenerator(StreamingAssetsTileStore store)
        {
            _store = store;
        }

        public async Task<TileBuildResult> BuildAsync(TileBuildRequest request, CancellationToken cancellationToken)
        {
            var payload = await _store.TryLoadTileAsync(request.TileId.Lod, request.TileId.X, request.TileId.Y, cancellationToken);
            if (payload == null)
            {
                // No data yet.
                return null;
            }

            // MVP: No DEM yet. Use flat terrain and build features at y=0 with small offsets.
            var terrain = BuildFlatTerrain(request.TileSizeMeters);

            var buildingsMesh = BuildBuildings(payload);
            var roadsMesh = BuildRoads(payload);
            var waterMesh = BuildWater(payload);

            return new TileBuildResult
            {
                TerrainMesh = terrain,
                BuildingsMesh = buildingsMesh,
                RoadsMesh = roadsMesh,
                WaterMesh = waterMesh,
            };
        }

        private static Mesh BuildFlatTerrain(float tileSize)
        {
            var mesh = new Mesh { name = "Terrain(Flat)" };
            var half = tileSize * 0.5f;
            mesh.vertices = new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3( half, 0f, -half),
                new Vector3( half, 0f,  half),
                new Vector3(-half, 0f,  half),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildBuildings(TilePayload payload)
        {
            var builder = new MeshBuilder();
            int successCount = 0;
            int fallbackCount = 0;
            int skippedCount = 0;

            for (var i = 0; i < payload.buildings.Length; i++)
            {
                var b = payload.buildings[i];
                if (b?.vertices == null || b.vertices.Length < 3)
                {
                    skippedCount++;
                    continue;
                }

                // Debug: Check if vertices are all zero (deserialization failure)
                if (i == 0)
                {
                    bool allZero = true;
                    foreach (var v in b.vertices)
                    {
                        if (v.x != 0f || v.y != 0f) { allZero = false; break; }
                    }
                    if (allZero)
                    {
                        UnityEngine.Debug.LogError($"[StreamingTileGenerator] First building has all-zero vertices! JSON deserialization may have failed.");
                    }
                }

                var height = Mathf.Max(1f, b.heightMeters);
                if (!builder.TryAddExtrudedPolygon(b.vertices, baseY: 0f, heightY: height))
                {
                    fallbackCount++;
                    // Fallback: if polygon is invalid, approximate with an AABB box.
                    var minX = float.PositiveInfinity;
                    var minZ = float.PositiveInfinity;
                    var maxX = float.NegativeInfinity;
                    var maxZ = float.NegativeInfinity;
                    for (var v = 0; v < b.vertices.Length; v++)
                    {
                        var p = b.vertices[v];
                        if (p.x < minX) minX = p.x;
                        if (p.y < minZ) minZ = p.y;
                        if (p.x > maxX) maxX = p.x;
                        if (p.y > maxZ) maxZ = p.y;
                    }
                    var size = new Vector3(Mathf.Max(1f, maxX - minX), height, Mathf.Max(1f, maxZ - minZ));
                    var center = new Vector3((minX + maxX) * 0.5f, size.y * 0.5f, (minZ + maxZ) * 0.5f);
                    builder.AddBox(center, size);
                }
                else
                {
                    successCount++;
                }
            }

            // Log stats for the first tile only to avoid spam
            if (payload.tx == 0 && payload.ty == 0)
            {
                UnityEngine.Debug.Log($"[StreamingTileGenerator] Tile(0,0) Buildings: {successCount} triangulated, {fallbackCount} AABB fallback, {skippedCount} skipped");
            }

            return builder.ToMesh("Buildings(OSM)");
        }

        private static Mesh BuildRoads(TilePayload payload)
        {
            var builder = new MeshBuilder();
            int roadCount = 0;
            int skippedCount = 0;

            for (var i = 0; i < payload.roads.Length; i++)
            {
                var r = payload.roads[i];
                if (r?.points == null || r.points.Length < 2)
                {
                    skippedCount++;
                    continue;
                }

                // Debug: Check if points are all zero
                if (i == 0)
                {
                    bool allZero = true;
                    foreach (var p in r.points)
                    {
                        if (p.x != 0f || p.y != 0f) { allZero = false; break; }
                    }
                    if (allZero)
                    {
                        UnityEngine.Debug.LogError($"[StreamingTileGenerator] First road has all-zero points! JSON deserialization may have failed.");
                    }
                }

                var pts = ListPool<Vector3>.Get();
                for (var p = 0; p < r.points.Length; p++)
                {
                    var v = r.points[p];
                    // Roads at y=0.1 to ensure they render above terrain (y=0)
                    pts.Add(new Vector3(v.x, 0.1f, v.y));
                }
                builder.AddRibbon(pts, Mathf.Max(1f, r.widthMeters));
                ListPool<Vector3>.Release(pts);
                roadCount++;
            }

            // Log stats for the first tile only
            if (payload.tx == 0 && payload.ty == 0)
            {
                UnityEngine.Debug.Log($"[StreamingTileGenerator] Tile(0,0) Roads: {roadCount} generated, {skippedCount} skipped, mesh verts: {builder.VertexCount}");
            }

            return builder.ToMesh("Roads(OSM)");
        }

        private static Mesh BuildWater(TilePayload payload)
        {
            var builder = new MeshBuilder();

            // Render area water first (lakes/ponds etc).
            for (var i = 0; i < payload.waters.Length; i++)
            {
                var w = payload.waters[i];
                if (w?.vertices == null || w.vertices.Length < 3) continue;

                if (!builder.TryAddFlatPolygon(w.vertices, y: 0.02f, normal: Vector3.up))
                {
                    // Fallback for invalid polygons.
                    builder.AddFlatPolygonAabb(w.vertices, 0.02f, paddingMeters: 0f);
                }
            }

            // Render linear waterways (rivers/streams) as ribbons.
            for (var i = 0; i < payload.waterways.Length; i++)
            {
                var r = payload.waterways[i];
                if (r?.points == null || r.points.Length < 2) continue;

                var pts = ListPool<Vector3>.Get();
                for (var p = 0; p < r.points.Length; p++)
                {
                    var v = r.points[p];
                    pts.Add(new Vector3(v.x, 0.021f, v.y));
                }
                builder.AddRibbon(pts, Mathf.Clamp(r.widthMeters, 1f, 80f));
                ListPool<Vector3>.Release(pts);
            }

            return builder.ToMesh("Water(OSM)");
        }

        private static class ListPool<T>
        {
            private static readonly Stack<List<T>> Pool = new Stack<List<T>>();

            public static List<T> Get()
            {
                if (Pool.Count == 0) return new List<T>(256);
                var list = Pool.Pop();
                list.Clear();
                return list;
            }

            public static void Release(List<T> list)
            {
                if (list == null) return;
                list.Clear();
                Pool.Push(list);
            }
        }
    }
}
