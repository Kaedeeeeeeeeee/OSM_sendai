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

            // Load heightmap (null if not available — falls back to flat terrain).
            var heightmap = await _store.TryLoadHeightmapAsync(request.TileId.Lod, request.TileId.X, request.TileId.Y, cancellationToken);

            var terrain = heightmap != null
                ? BuildSubdividedTerrain(request.TileSizeMeters, heightmap)
                : BuildFlatTerrain(request.TileSizeMeters);

            var buildingsMesh = BuildBuildings(payload, heightmap, request.TileSizeMeters);
            var roadsMesh = BuildRoads(payload, heightmap, request.TileSizeMeters);
            var waterMesh = BuildWater(payload, heightmap, request.TileSizeMeters);

            return new TileBuildResult
            {
                TerrainMesh = terrain,
                BuildingsMesh = buildingsMesh,
                RoadsMesh = roadsMesh,
                WaterMesh = waterMesh,
            };
        }

        private static Mesh BuildSubdividedTerrain(float tileSize, HeightmapData hm)
        {
            var gridW = hm.GridWidth;
            var gridH = hm.GridHeight;
            var half = tileSize * 0.5f;

            var vertCount = gridW * gridH;
            var vertices = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];

            for (var row = 0; row < gridH; row++)
            {
                var z = -half + row * tileSize / (gridH - 1);
                var vNorm = (float)row / (gridH - 1);

                for (var col = 0; col < gridW; col++)
                {
                    var x = -half + col * tileSize / (gridW - 1);
                    var uNorm = (float)col / (gridW - 1);
                    var y = hm.Heights[row * gridW + col];

                    var idx = row * gridW + col;
                    vertices[idx] = new Vector3(x, y, z);
                    uvs[idx] = new Vector2(uNorm, vNorm);
                }
            }

            // 2 triangles per grid cell, (gridW-1)*(gridH-1) cells
            var triCount = (gridW - 1) * (gridH - 1) * 6;
            var triangles = new int[triCount];
            var ti = 0;

            for (var row = 0; row < gridH - 1; row++)
            {
                for (var col = 0; col < gridW - 1; col++)
                {
                    var bl = row * gridW + col;
                    var br = bl + 1;
                    var tl = bl + gridW;
                    var tr = tl + 1;

                    // Triangle 1: bl, tl, br
                    triangles[ti++] = bl;
                    triangles[ti++] = tl;
                    triangles[ti++] = br;

                    // Triangle 2: br, tl, tr
                    triangles[ti++] = br;
                    triangles[ti++] = tl;
                    triangles[ti++] = tr;
                }
            }

            var mesh = new Mesh { name = "Terrain(DEM)" };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
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

        private static float SampleOrZero(HeightmapData hm, float localX, float localZ, float tileSize)
        {
            return hm != null ? hm.SampleHeight(localX, localZ, tileSize) : 0f;
        }

        /// <summary>
        /// Subdivides a polyline so that no segment exceeds <paramref name="maxSegment"/> metres,
        /// then samples terrain height at every resulting point.  This prevents ribbons
        /// (roads / waterways) from cutting through terrain between sparse control points.
        /// </summary>
        private static List<Vector3> SubdivideForTerrain(
            Vector2[] points, HeightmapData hm, float tileSize,
            float maxSegment, float yOffset)
        {
            var result = ListPool<Vector3>.Get();

            for (var i = 0; i < points.Length; i++)
            {
                var a = points[i];

                if (i < points.Length - 1)
                {
                    var b = points[i + 1];
                    var dx = b.x - a.x;
                    var dy = b.y - a.y;
                    var dist = Mathf.Sqrt(dx * dx + dy * dy);

                    if (dist > maxSegment)
                    {
                        var segments = Mathf.CeilToInt(dist / maxSegment);
                        for (var s = 0; s < segments; s++)
                        {
                            var t = (float)s / segments;
                            var px = a.x + dx * t;
                            var pz = a.y + dy * t;
                            var py = SampleOrZero(hm, px, pz, tileSize) + yOffset;
                            result.Add(new Vector3(px, py, pz));
                        }
                    }
                    else
                    {
                        var py = SampleOrZero(hm, a.x, a.y, tileSize) + yOffset;
                        result.Add(new Vector3(a.x, py, a.y));
                    }
                }
                else
                {
                    // Last point — always add it.
                    var py = SampleOrZero(hm, a.x, a.y, tileSize) + yOffset;
                    result.Add(new Vector3(a.x, py, a.y));
                }
            }

            return result;
        }

        private static Mesh BuildBuildings(TilePayload payload, HeightmapData hm, float tileSize)
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

                // Compute ground elevation under building footprint (average of vertex elevations).
                float baseY = 0f;
                if (hm != null)
                {
                    float sum = 0f;
                    for (var v = 0; v < b.vertices.Length; v++)
                    {
                        sum += SampleOrZero(hm, b.vertices[v].x, b.vertices[v].y, tileSize);
                    }
                    baseY = sum / b.vertices.Length;
                }

                var height = Mathf.Max(1f, b.heightMeters);
                if (!builder.TryAddExtrudedPolygon(b.vertices, baseY: baseY, heightY: height))
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
                    var center = new Vector3((minX + maxX) * 0.5f, baseY + size.y * 0.5f, (minZ + maxZ) * 0.5f);
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

        private static Mesh BuildRoads(TilePayload payload, HeightmapData hm, float tileSize)
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

                var pts = SubdivideForTerrain(r.points, hm, tileSize, maxSegment: 16f, yOffset: 0.3f);
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

        private static Mesh BuildWater(TilePayload payload, HeightmapData hm, float tileSize)
        {
            var builder = new MeshBuilder();

            // Render area water first (lakes/ponds etc).
            for (var i = 0; i < payload.waters.Length; i++)
            {
                var w = payload.waters[i];
                if (w?.vertices == null || w.vertices.Length < 3) continue;

                // Use minimum ground height across polygon vertices (with extra
                // interior samples) for a flat water surface that doesn't clip terrain.
                float waterY = 0.05f;
                if (hm != null)
                {
                    float minH = float.PositiveInfinity;
                    // Sample original vertices.
                    for (var v = 0; v < w.vertices.Length; v++)
                    {
                        var h = SampleOrZero(hm, w.vertices[v].x, w.vertices[v].y, tileSize);
                        if (h < minH) minH = h;
                    }
                    // Also sample midpoints of long edges to catch terrain dips
                    // between sparse polygon vertices.
                    const float kMaxEdge = 16f;
                    for (var v = 0; v < w.vertices.Length; v++)
                    {
                        var a = w.vertices[v];
                        var b = w.vertices[(v + 1) % w.vertices.Length];
                        var dx = b.x - a.x;
                        var dy = b.y - a.y;
                        var dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist > kMaxEdge)
                        {
                            var segs = Mathf.CeilToInt(dist / kMaxEdge);
                            for (var s = 1; s < segs; s++)
                            {
                                var t = (float)s / segs;
                                var h = SampleOrZero(hm, a.x + dx * t, a.y + dy * t, tileSize);
                                if (h < minH) minH = h;
                            }
                        }
                    }
                    waterY = minH + 0.05f;
                }

                if (!builder.TryAddFlatPolygon(w.vertices, y: waterY, normal: Vector3.up))
                {
                    // Fallback for invalid polygons.
                    builder.AddFlatPolygonAabb(w.vertices, waterY, paddingMeters: 0f);
                }
            }

            // Render linear waterways (rivers/streams) as ribbons.
            for (var i = 0; i < payload.waterways.Length; i++)
            {
                var r = payload.waterways[i];
                if (r?.points == null || r.points.Length < 2) continue;

                var pts = SubdivideForTerrain(r.points, hm, tileSize, maxSegment: 16f, yOffset: 0.1f);
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
