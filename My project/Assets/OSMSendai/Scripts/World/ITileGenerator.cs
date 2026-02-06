using System.Threading;
using System.Threading.Tasks;
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
        public Mesh GrassMesh;
    }

    public interface ITileGenerator
    {
        Task<TileBuildResult> BuildAsync(TileBuildRequest request, CancellationToken cancellationToken);
    }
}

