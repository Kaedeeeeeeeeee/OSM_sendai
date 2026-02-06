using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace OsmSendai.World
{
    public sealed class TileManager
    {
        public float TileSizeMeters { get; set; } = 512f;
        public int Lod0RadiusTiles { get; set; } = 2;

        public Material TerrainMaterial { get; set; }
        public Material BuildingsMaterial { get; set; }
        public Material RoadsMaterial { get; set; }
        public Material WaterMaterial { get; set; }

        // Layer visibility
        public bool ShowTerrain { get; set; } = true;
        public bool ShowBuildings { get; set; } = true;
        public bool ShowRoads { get; set; } = true;
        public bool ShowWater { get; set; } = true;

        // Physics collision toggles
        public bool EnableTerrainCollider { get; set; } = true;
        public bool EnableBuildingCollider { get; set; } = true;

        // Debug - single tile mode
        public bool SingleTileMode { get; set; } = false;
        public int DebugTileX { get; set; } = 0;
        public int DebugTileY { get; set; } = 0;

        private readonly Transform _root;
        private readonly ITileGenerator _generator;
        private readonly Dictionary<TileId, TileInstance> _active = new Dictionary<TileId, TileInstance>();
        private readonly Dictionary<TileId, float> _missingUntil = new Dictionary<TileId, float>();
        private bool _isUpdating;

        public TileManager(Transform root, ITileGenerator generator)
        {
            _root = root;
            _generator = generator;
        }

        public async void UpdateVisibleTiles(Vector3 cameraWorldPos, CancellationToken cancellationToken)
        {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
            var desired = new HashSet<TileId>();

            if (SingleTileMode)
            {
                // Only load a single debug tile
                desired.Add(new TileId(0, DebugTileX, DebugTileY));
            }
            else
            {
                var tileX = Mathf.FloorToInt(cameraWorldPos.x / TileSizeMeters);
                var tileY = Mathf.FloorToInt(cameraWorldPos.z / TileSizeMeters);

                for (var dy = -Lod0RadiusTiles; dy <= Lod0RadiusTiles; dy++)
                {
                    for (var dx = -Lod0RadiusTiles; dx <= Lod0RadiusTiles; dx++)
                    {
                        desired.Add(new TileId(0, tileX + dx, tileY + dy));
                    }
                }
            }

            // Unload tiles not desired.
            var toRemove = ListPool<TileId>.Get();
            foreach (var kv in _active)
            {
                if (!desired.Contains(kv.Key))
                {
                    toRemove.Add(kv.Key);
                }
            }

            for (var i = 0; i < toRemove.Count; i++)
            {
                _active[toRemove[i]].Dispose();
                _active.Remove(toRemove[i]);
            }
            ListPool<TileId>.Release(toRemove);

            // Load missing desired tiles.
            foreach (var id in desired)
            {
                if (_active.ContainsKey(id)) continue;
                if (_missingUntil.TryGetValue(id, out var until) && Time.unscaledTime < until) continue;
                cancellationToken.ThrowIfCancellationRequested();

                var origin = new Vector3(id.X * TileSizeMeters + TileSizeMeters * 0.5f, 0f, id.Y * TileSizeMeters + TileSizeMeters * 0.5f);
                UnityEngine.Debug.Log($"[TileManager] Loading tile ({id.X}, {id.Y}) at world position ({origin.x}, {origin.z})");

                var request = new TileBuildRequest(id, TileSizeMeters, origin);
                var build = await _generator.BuildAsync(request, cancellationToken);
                if (build == null)
                {
                    _missingUntil[id] = Time.unscaledTime + 2f;
                    continue;
                }

                var instance = CreateTileInstance(id, origin, build);
                _active.Add(id, instance);
                _missingUntil.Remove(id);
            }
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private TileInstance CreateTileInstance(TileId id, Vector3 origin, TileBuildResult build)
        {
            var go = new GameObject($"Tile ({id})");
            go.transform.SetParent(_root, false);
            go.transform.localPosition = origin;

            var terrain = ShowTerrain ? CreatePart(go.transform, "Terrain", build.TerrainMesh, TerrainMaterial, EnableTerrainCollider) : null;
            var buildings = ShowBuildings ? CreatePart(go.transform, "Buildings", build.BuildingsMesh, BuildingsMaterial, EnableBuildingCollider) : null;
            var roads = ShowRoads ? CreatePart(go.transform, "Roads", build.RoadsMesh, RoadsMaterial, addCollider: false) : null;
            var water = ShowWater ? CreatePart(go.transform, "Water", build.WaterMesh, WaterMaterial, addCollider: false) : null;

            return new TileInstance(go, terrain, buildings, roads, water);
        }

        private static GameObject CreatePart(Transform parent, string name, Mesh mesh, Material material, bool addCollider)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;

            if (addCollider && mesh != null)
            {
                var meshCollider = go.AddComponent<MeshCollider>();
                meshCollider.sharedMesh = mesh;
            }

            return go;
        }

        private sealed class TileInstance
        {
            private readonly GameObject _root;
            private readonly GameObject[] _parts;

            public TileInstance(GameObject root, params GameObject[] parts)
            {
                _root = root;
                _parts = parts;
            }

            public void Dispose()
            {
                if (_root != null)
                {
                    Object.Destroy(_root);
                }
            }
        }

        private static class ListPool<T>
        {
            private static readonly Stack<List<T>> Pool = new Stack<List<T>>();

            public static List<T> Get()
            {
                if (Pool.Count == 0) return new List<T>(64);
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
