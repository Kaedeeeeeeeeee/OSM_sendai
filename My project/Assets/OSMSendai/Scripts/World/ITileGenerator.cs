using System.Threading;
using System.Threading.Tasks;
using OsmSendai.Data;
using UnityEngine;

namespace OsmSendai.World
{
    public readonly struct TileBuildRequest
    {
        public readonly TileId TileId;
        public readonly float TileSizeMeters;
        public readonly Vector3 TileOriginWorld;

        public TileBuildRequest(TileId tileId, float tileSizeMeters, Vector3 tileOriginWorld)
        {
            TileId = tileId;
            TileSizeMeters = tileSizeMeters;
            TileOriginWorld = tileOriginWorld;
        }
    }

    public sealed class TileBuildResult
    {
        public Mesh TerrainMesh;
        public Mesh BuildingsMesh;
        public Mesh RoadsMesh;
        public Mesh WaterMesh;
        public Mesh LandcoverMesh;
        public Mesh VegetationMesh;
        public Mesh RailwaysMesh;
        public Mesh PoiMesh;
        public Poi[] Pois;

        /// <summary>Heightmap data for grass placement elevation sampling.</summary>
        public HeightmapData Heightmap;
        /// <summary>Tile size in metres — needed by TileGrassRenderer for heightmap sampling.</summary>
        public float TileSizeMeters;
    }

    public interface ITileGenerator
    {
        Task<TileBuildResult> BuildAsync(TileBuildRequest request, CancellationToken cancellationToken);
    }
}

