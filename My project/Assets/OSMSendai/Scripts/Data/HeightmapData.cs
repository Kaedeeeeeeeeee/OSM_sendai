using UnityEngine;

namespace OsmSendai.Data
{
    public sealed class HeightmapData
    {
        public int GridWidth;
        public int GridHeight;
        public float[] Heights; // row-major: [row * GridWidth + col]

        /// <summary>
        /// Bilinear interpolation of elevation at tile-local coordinates.
        /// localX: [-tileSize/2 .. +tileSize/2]  (west to east)
        /// localZ: [-tileSize/2 .. +tileSize/2]  (south to north)
        /// </summary>
        public float SampleHeight(float localX, float localZ, float tileSize)
        {
            if (Heights == null || Heights.Length == 0) return 0f;

            var half = tileSize * 0.5f;
            // Map tile-local coords to grid coords [0 .. GridWidth-1]
            var gx = (localX + half) / tileSize * (GridWidth - 1);
            var gz = (localZ + half) / tileSize * (GridHeight - 1);

            // Clamp to grid bounds
            gx = Mathf.Clamp(gx, 0f, GridWidth - 1f);
            gz = Mathf.Clamp(gz, 0f, GridHeight - 1f);

            var ix = (int)gx;
            var iz = (int)gz;
            var fx = gx - ix;
            var fz = gz - iz;

            // Clamp indices for edge cases
            var ix1 = Mathf.Min(ix + 1, GridWidth - 1);
            var iz1 = Mathf.Min(iz + 1, GridHeight - 1);

            var h00 = Heights[iz * GridWidth + ix];
            var h10 = Heights[iz * GridWidth + ix1];
            var h01 = Heights[iz1 * GridWidth + ix];
            var h11 = Heights[iz1 * GridWidth + ix1];

            // Bilinear interpolation
            var h0 = h00 + (h10 - h00) * fx;
            var h1 = h01 + (h11 - h01) * fx;
            return h0 + (h1 - h0) * fz;
        }
    }
}
