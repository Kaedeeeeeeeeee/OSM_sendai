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
                ? BuildSubdividedTerrain(request.TileSizeMeters, heightmap, payload.landcovers)
                : BuildFlatTerrain(request.TileSizeMeters);

            var buildingsMesh = BuildBuildings(payload, heightmap, request.TileSizeMeters);
            var roadsMesh = BuildRoads(payload, heightmap, request.TileSizeMeters);
            var waterMesh = BuildWater(payload, heightmap, request.TileSizeMeters);
            var landcoverMesh = BuildLandcovers(payload, heightmap, request.TileSizeMeters);
            var vegetationMesh = BuildVegetation(payload, heightmap, request.TileSizeMeters);
            var railwaysMesh = BuildRailways(payload, heightmap, request.TileSizeMeters);
            var poiMesh = BuildPois(payload, heightmap, request.TileSizeMeters);

            return new TileBuildResult
            {
                TerrainMesh = terrain,
                BuildingsMesh = buildingsMesh,
                RoadsMesh = roadsMesh,
                WaterMesh = waterMesh,
                LandcoverMesh = landcoverMesh,
                VegetationMesh = vegetationMesh,
                RailwaysMesh = railwaysMesh,
                PoiMesh = poiMesh,
                Pois = payload.pois,
                Heightmap = heightmap,
                TileSizeMeters = request.TileSizeMeters,
            };
        }

        /// <summary>
        /// Synchronous mesh build for Editor preview (no async I/O).
        /// </summary>
        public static TileBuildResult BuildSync(TilePayload payload, HeightmapData heightmap, float tileSizeMeters)
        {
            var terrain = heightmap != null
                ? BuildSubdividedTerrain(tileSizeMeters, heightmap, payload.landcovers)
                : BuildFlatTerrain(tileSizeMeters);
            return new TileBuildResult
            {
                TerrainMesh = terrain,
                BuildingsMesh = BuildBuildings(payload, heightmap, tileSizeMeters),
                RoadsMesh = BuildRoads(payload, heightmap, tileSizeMeters),
                WaterMesh = BuildWater(payload, heightmap, tileSizeMeters),
                LandcoverMesh = BuildLandcovers(payload, heightmap, tileSizeMeters),
                VegetationMesh = BuildVegetation(payload, heightmap, tileSizeMeters),
                RailwaysMesh = BuildRailways(payload, heightmap, tileSizeMeters),
                PoiMesh = BuildPois(payload, heightmap, tileSizeMeters),
                Pois = payload.pois,
                Heightmap = heightmap,
                TileSizeMeters = tileSizeMeters,
            };
        }

        private static Mesh BuildSubdividedTerrain(float tileSize, HeightmapData hm, Landcover[] landcovers)
        {
            var gridW = hm.GridWidth;
            var gridH = hm.GridHeight;
            var half = tileSize * 0.5f;

            var vertCount = gridW * gridH;
            var vertices = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var colors = new Color[vertCount];

            // Pre-compute AABB for each landcover polygon to skip expensive point-in-polygon tests.
            var lcCount = landcovers != null ? landcovers.Length : 0;
            var lcMinX = new float[lcCount];
            var lcMaxX = new float[lcCount];
            var lcMinZ = new float[lcCount];
            var lcMaxZ = new float[lcCount];
            for (var li = 0; li < lcCount; li++)
            {
                var lc = landcovers[li];
                if (lc?.vertices == null || lc.vertices.Length < 3)
                {
                    lcMinX[li] = float.PositiveInfinity; // will never pass AABB test
                    continue;
                }
                float mnX = float.PositiveInfinity, mxX = float.NegativeInfinity;
                float mnZ = float.PositiveInfinity, mxZ = float.NegativeInfinity;
                for (var v = 0; v < lc.vertices.Length; v++)
                {
                    var p = lc.vertices[v];
                    if (p.x < mnX) mnX = p.x;
                    if (p.x > mxX) mxX = p.x;
                    if (p.y < mnZ) mnZ = p.y;
                    if (p.y > mxZ) mxZ = p.y;
                }
                lcMinX[li] = mnX;
                lcMaxX[li] = mxX;
                lcMinZ[li] = mnZ;
                lcMaxZ[li] = mxZ;
            }

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

                    // Test vertex against landcover polygons.
                    var vertColor = Color.white;
                    var pt = new Vector2(x, z);
                    for (var li = 0; li < lcCount; li++)
                    {
                        // AABB early-out.
                        if (x < lcMinX[li] || x > lcMaxX[li] || z < lcMinZ[li] || z > lcMaxZ[li])
                            continue;
                        if (PointInPolygon(pt, landcovers[li].vertices))
                        {
                            var kind = landcovers[li].kind;
                            vertColor = kind == "forest"
                                ? new Color(0.30f, 0.50f, 0.20f)
                                : new Color(0.50f, 0.70f, 0.30f);
                            break;
                        }
                    }
                    colors[idx] = vertColor;
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
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents(); // Required by grass geometry shader
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

                var pts = SubdivideForTerrain(r.points, hm, tileSize, maxSegment: 8f, yOffset: 0.15f);
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

            // Water UV scale: 10 metres per UV unit — gives good tiling for noise textures.
            const float kWaterUvScale = 10f;

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
                    const float kMaxEdge = 8f;
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

                if (!builder.TryAddFlatPolygon(w.vertices, y: waterY, normal: Vector3.up, uvScale: kWaterUvScale))
                {
                    // Fallback for invalid polygons.
                    builder.AddFlatPolygonAabb(w.vertices, waterY, paddingMeters: 0f);
                }
            }

            // Render linear waterways (rivers/streams) as ribbons with flow UVs.
            for (var i = 0; i < payload.waterways.Length; i++)
            {
                var r = payload.waterways[i];
                if (r?.points == null || r.points.Length < 2) continue;

                var pts = SubdivideForTerrain(r.points, hm, tileSize, maxSegment: 8f, yOffset: 0.05f);
                builder.AddRibbonWithFlowUV(pts, Mathf.Clamp(r.widthMeters, 1f, 80f), kWaterUvScale);
                ListPool<Vector3>.Release(pts);
            }

            return builder.ToMesh("Water(OSM)");
        }

        /// <summary>
        /// Shoelace formula — returns unsigned area in m².
        /// </summary>
        private static float PolygonArea(Vector2[] pts)
        {
            var area = 0f;
            for (var i = 0; i < pts.Length; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Length];
                area += a.x * b.y - b.x * a.y;
            }
            return Mathf.Abs(area) * 0.5f;
        }

        /// <summary>
        /// Ray-casting point-in-polygon test (XY plane, where Y maps to Z in world).
        /// </summary>
        private static bool PointInPolygon(Vector2 point, Vector2[] polygon)
        {
            var inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var pi = polygon[i];
                var pj = polygon[j];
                if ((pi.y > point.y) != (pj.y > point.y) &&
                    point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y) + pi.x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static Mesh BuildLandcovers(TilePayload payload, HeightmapData hm, float tileSize)
        {
            // Landcover is now rendered via terrain vertex colors — no separate overlay mesh needed.
            return new Mesh { name = "Landcover(empty)" };
        }

        private static Mesh BuildVegetation(TilePayload payload, HeightmapData hm, float tileSize)
        {
            var builder = new MeshBuilder();

            for (var i = 0; i < payload.landcovers.Length; i++)
            {
                var lc = payload.landcovers[i];
                if (lc?.vertices == null || lc.vertices.Length < 3) continue;
                if (lc.kind != "forest") continue;

                var area = PolygonArea(lc.vertices);
                var areaKm2 = area / 1_000_000f;
                var density = lc.densityPerKm2 > 0f ? lc.densityPerKm2 : 800f;
                var treeCount = Mathf.RoundToInt(areaKm2 * density);
                if (treeCount <= 0) continue;
                treeCount = Mathf.Min(treeCount, 2000);

                // Compute AABB for rejection sampling.
                float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
                for (var v = 0; v < lc.vertices.Length; v++)
                {
                    var p = lc.vertices[v];
                    if (p.x < minX) minX = p.x;
                    if (p.y < minZ) minZ = p.y;
                    if (p.x > maxX) maxX = p.x;
                    if (p.y > maxZ) maxZ = p.y;
                }

                // Deterministic random seeded by tile + polygon index.
                var seed = (uint)(payload.tx * 73856093 ^ payload.ty * 19349663 ^ i * 83492791);
                var rng = new DeterministicRandom(seed);

                var placed = 0;
                var maxAttempts = treeCount * 4;
                for (var attempt = 0; attempt < maxAttempts && placed < treeCount; attempt++)
                {
                    var px = rng.Range(minX, maxX);
                    var pz = rng.Range(minZ, maxZ);
                    if (!PointInPolygon(new Vector2(px, pz), lc.vertices)) continue;

                    var groundY = SampleOrZero(hm, px, pz, tileSize);

                    // Trunk: 0.4 x 2.5 x 0.4 m box
                    var trunkCenter = new Vector3(px, groundY + 1.25f, pz);
                    builder.AddBox(trunkCenter, new Vector3(0.4f, 2.5f, 0.4f));

                    // Canopy: cone, radius 3m, height 5m, 6 sides
                    var canopyBase = new Vector3(px, groundY + 2.5f, pz);
                    builder.AddCone(canopyBase, 3f, 5f, 6);

                    placed++;
                }
            }

            return builder.ToMesh("Vegetation(OSM)");
        }

        private static Mesh BuildRailways(TilePayload payload, HeightmapData hm, float tileSize)
        {
            var builder = new MeshBuilder();
            for (var i = 0; i < payload.railways.Length; i++)
            {
                var r = payload.railways[i];
                if (r?.points == null || r.points.Length < 2) continue;

                var pts = SubdivideForTerrain(r.points, hm, tileSize, maxSegment: 8f, yOffset: 0.18f);
                builder.AddRibbon(pts, Mathf.Max(1f, r.widthMeters));
                ListPool<Vector3>.Release(pts);
            }
            return builder.ToMesh("Railways(OSM)");
        }

        private static Mesh BuildPois(TilePayload payload, HeightmapData hm, float tileSize)
        {
            var builder = new MeshBuilder();
            for (var i = 0; i < payload.pois.Length; i++)
            {
                var poi = payload.pois[i];
                if (poi == null) continue;

                var groundY = SampleOrZero(hm, poi.position.x, poi.position.y, tileSize);

                if (poi.type == "station")
                {
                    var center = new Vector3(poi.position.x, groundY + 7.5f, poi.position.y);
                    builder.AddBox(center, new Vector3(3f, 15f, 3f));
                }
                else if (poi.type == "subway_entrance")
                {
                    var center = new Vector3(poi.position.x, groundY + 2.5f, poi.position.y);
                    builder.AddBox(center, new Vector3(1.5f, 5f, 1.5f));
                }
            }
            return builder.ToMesh("POIs(OSM)");
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
