using System;
using UnityEngine;

namespace OsmSendai.Data
{
    [Serializable]
    public sealed class TilesetMetadata
    {
        public string projection = "EPSG:32654";
        public float tileSizeMeters = 512f;
        public int dataVersion = 1;
        public DemLod[] demLods = Array.Empty<DemLod>();
        public OriginAnchor origin;
    }

    [Serializable]
    public struct DemLod
    {
        public int lod;
        public float resolutionMeters;
    }

    [Serializable]
    public struct OriginAnchor
    {
        public float lat;
        public float lon;
    }

    [Serializable]
    public sealed class TilePayload
    {
        public int lod;
        public int tx;
        public int ty;

        public Building[] buildings = Array.Empty<Building>();
        public Road[] roads = Array.Empty<Road>();
        public Polygon[] waters = Array.Empty<Polygon>();
        public Waterway[] waterways = Array.Empty<Waterway>();
        public Landcover[] landcovers = Array.Empty<Landcover>();
    }

    [Serializable]
    public sealed class Building
    {
        // Tile-local coordinates in meters, relative to tile origin (center).
        public Vector2[] vertices = Array.Empty<Vector2>();
        public float heightMeters = 12f;
    }

    [Serializable]
    public sealed class Road
    {
        public Vector2[] points = Array.Empty<Vector2>();
        public float widthMeters = 6f;
    }

    [Serializable]
    public sealed class Polygon
    {
        public Vector2[] vertices = Array.Empty<Vector2>();
    }

    [Serializable]
    public sealed class Waterway
    {
        public string kind = "river"; // river/stream/canal/etc
        public Vector2[] points = Array.Empty<Vector2>();
        public float widthMeters = 8f;
    }

    [Serializable]
    public sealed class Landcover
    {
        public string kind = "forest"; // forest/grass/park/etc
        public Vector2[] vertices = Array.Empty<Vector2>();
        public float densityPerKm2 = 600f;
    }
}
