using System;

namespace OsmSendai.World
{
    [Serializable]
    public readonly struct TileId : IEquatable<TileId>
    {
        public readonly int Lod;
        public readonly int X;
        public readonly int Y;

        public TileId(int lod, int x, int y)
        {
            Lod = lod;
            X = x;
            Y = y;
        }

        public bool Equals(TileId other) => Lod == other.Lod && X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is TileId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Lod, X, Y);
        public override string ToString() => $"lod={Lod} x={X} y={Y}";
    }
}

