using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OsmSendai.World
{
    public sealed class DebugTileGenerator : ITileGenerator
    {
        public Task<TileBuildResult> BuildAsync(TileBuildRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seed = (uint)(request.TileId.Lod * 73856093 ^ request.TileId.X * 19349663 ^ request.TileId.Y * 83492791);
            var rng = new DeterministicRandom(seed);

            var result = new TileBuildResult
            {
                TerrainMesh = BuildTerrain(request.TileSizeMeters),
                BuildingsMesh = BuildBuildings(request.TileSizeMeters, ref rng),
                RoadsMesh = BuildRoads(request.TileSizeMeters, ref rng),
                WaterMesh = BuildWater(request.TileSizeMeters, ref rng),
                LandcoverMesh = null,
                VegetationMesh = null,
            };

            return Task.FromResult(result);
        }

        private static Mesh BuildTerrain(float tileSize)
        {
            var mesh = new Mesh { name = "Terrain(Debug)" };
            var half = tileSize * 0.5f;
            var vertices = new[]
            {
                new Vector3(-half, 0f, -half),
                new Vector3( half, 0f, -half),
                new Vector3( half, 0f,  half),
                new Vector3(-half, 0f,  half),
            };
            var triangles = new[] { 0, 2, 1, 0, 3, 2 };
            var uvs = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.uv = uvs;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildBuildings(float tileSize, ref DeterministicRandom rng)
        {
            var mesh = new Mesh { name = "Buildings(Debug)" };

            const int buildingCount = 40;
            var vertices = new Vector3[buildingCount * 8];
            var triangles = new int[buildingCount * 36];

            var half = tileSize * 0.5f;
            for (var i = 0; i < buildingCount; i++)
            {
                var cx = rng.Range(-half + 10f, half - 10f);
                var cz = rng.Range(-half + 10f, half - 10f);
                var sx = rng.Range(6f, 24f);
                var sz = rng.Range(6f, 24f);
                var h = rng.Range(6f, 60f);

                var v0 = new Vector3(cx - sx, 0f, cz - sz);
                var v1 = new Vector3(cx + sx, 0f, cz - sz);
                var v2 = new Vector3(cx + sx, 0f, cz + sz);
                var v3 = new Vector3(cx - sx, 0f, cz + sz);
                var v4 = v0 + Vector3.up * h;
                var v5 = v1 + Vector3.up * h;
                var v6 = v2 + Vector3.up * h;
                var v7 = v3 + Vector3.up * h;

                var vo = i * 8;
                vertices[vo + 0] = v0;
                vertices[vo + 1] = v1;
                vertices[vo + 2] = v2;
                vertices[vo + 3] = v3;
                vertices[vo + 4] = v4;
                vertices[vo + 5] = v5;
                vertices[vo + 6] = v6;
                vertices[vo + 7] = v7;

                var to = i * 36;
                var t = triangles;
                // bottom
                t[to + 0] = vo + 0; t[to + 1] = vo + 1; t[to + 2] = vo + 2;
                t[to + 3] = vo + 0; t[to + 4] = vo + 2; t[to + 5] = vo + 3;
                // top
                t[to + 6] = vo + 4; t[to + 7] = vo + 6; t[to + 8] = vo + 5;
                t[to + 9] = vo + 4; t[to + 10] = vo + 7; t[to + 11] = vo + 6;
                // sides
                t[to + 12] = vo + 0; t[to + 13] = vo + 4; t[to + 14] = vo + 5;
                t[to + 15] = vo + 0; t[to + 16] = vo + 5; t[to + 17] = vo + 1;

                t[to + 18] = vo + 1; t[to + 19] = vo + 5; t[to + 20] = vo + 6;
                t[to + 21] = vo + 1; t[to + 22] = vo + 6; t[to + 23] = vo + 2;

                t[to + 24] = vo + 2; t[to + 25] = vo + 6; t[to + 26] = vo + 7;
                t[to + 27] = vo + 2; t[to + 28] = vo + 7; t[to + 29] = vo + 3;

                t[to + 30] = vo + 3; t[to + 31] = vo + 7; t[to + 32] = vo + 4;
                t[to + 33] = vo + 3; t[to + 34] = vo + 4; t[to + 35] = vo + 0;
            }

            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildRoads(float tileSize, ref DeterministicRandom rng)
        {
            var mesh = new Mesh { name = "Roads(Debug)" };
            var half = tileSize * 0.5f;

            // A couple of deterministic ribbons.
            var width = 6f;
            var x0 = rng.Range(-half * 0.8f, half * 0.8f);
            var z0 = rng.Range(-half * 0.8f, half * 0.8f);
            var x1 = rng.Range(-half * 0.8f, half * 0.8f);
            var z1 = rng.Range(-half * 0.8f, half * 0.8f);

            var a = new Vector3(x0, 0.05f, -half);
            var b = new Vector3(x1, 0.05f, half);
            var c = new Vector3(-half, 0.05f, z0);
            var d = new Vector3(half, 0.05f, z1);

            var m1 = BuildRibbon(a, b, width);
            var m2 = BuildRibbon(c, d, width);
            CombineMeshes(mesh, m1, m2);
            return mesh;
        }

        private static Mesh BuildWater(float tileSize, ref DeterministicRandom rng)
        {
            var mesh = new Mesh { name = "Water(Debug)" };
            var half = tileSize * 0.5f;
            var w = rng.Range(30f, 120f);
            var h = rng.Range(30f, 120f);
            var cx = rng.Range(-half * 0.5f, half * 0.5f);
            var cz = rng.Range(-half * 0.5f, half * 0.5f);

            var y = 0.02f;
            var v0 = new Vector3(cx - w, y, cz - h);
            var v1 = new Vector3(cx + w, y, cz - h);
            var v2 = new Vector3(cx + w, y, cz + h);
            var v3 = new Vector3(cx - w, y, cz + h);
            mesh.vertices = new[] { v0, v1, v2, v3 };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildRibbon(Vector3 start, Vector3 end, float width)
        {
            var mesh = new Mesh();
            var dir = (end - start);
            dir.y = 0f;
            dir.Normalize();
            var right = new Vector3(-dir.z, 0f, dir.x);
            var hw = width * 0.5f;

            var v0 = start - right * hw;
            var v1 = start + right * hw;
            var v2 = end + right * hw;
            var v3 = end - right * hw;

            mesh.vertices = new[] { v0, v1, v2, v3 };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void CombineMeshes(Mesh output, Mesh a, Mesh b)
        {
            var combine = new[]
            {
                new CombineInstance { mesh = a, transform = Matrix4x4.identity },
                new CombineInstance { mesh = b, transform = Matrix4x4.identity },
            };
            output.CombineMeshes(combine, true, false, false);
        }
    }
}

