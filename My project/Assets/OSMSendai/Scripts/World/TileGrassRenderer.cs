using OsmSendai.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace OsmSendai.World
{
    /// <summary>
    /// GPU-instanced grass renderer for a single tile.
    /// Extracts grass positions from terrain mesh vertex colors (non-white = landcover),
    /// scatters blade instances around those positions, uploads TRS matrices to a
    /// ComputeBuffer, and renders every frame via DrawMeshInstancedIndirect.
    ///
    /// Visibility culling is done by a compute shader (chunk-based frustum culling).
    ///
    /// Attach to a tile GameObject; call Initialize() after the terrain mesh is ready.
    /// Call Dispose() (or let OnDestroy handle it) when the tile is unloaded.
    /// </summary>
    public sealed class TileGrassRenderer : MonoBehaviour
    {
        // ── configuration (set via Initialize) ──
        private Material _material;       // per-tile instance (clone)
        private Mesh _bladeMesh;
        private ComputeShader _cullingShader;  // per-tile instance (clone)
        private Camera _camera;

        // ── GPU buffers ──
        private ComputeBuffer _trsBuffer;
        private ComputeBuffer _visibleBuffer;
        private ComputeBuffer _argsBuffer;
        private ComputeBuffer _readBackBuffer;
        private ComputeBuffer _chunkBuffer;

        // ── state ──
        private int _instanceCount;
        private int _numChunks;
        private Bounds _renderBounds;
        private int _kernelChunkRender;
        private uint _threadGroupSize;
        private bool _initialized;

        // ── tuning constants ──
        private const int BladesPerVertex = 12;
        private const float SpreadRadius = 14f;
        private const int ChunkSize = 32;
        private const float MaxViewDistance = 600f;

        private static readonly Vector3 ScaleMin = new Vector3(0.8f, 0.6f, 0.8f);
        private static readonly Vector3 ScaleMax = new Vector3(1.2f, 1.2f, 1.2f);

        /// <summary>
        /// Set up the renderer for one tile.
        /// </summary>
        /// <param name="terrainMesh">Terrain mesh with vertex colors marking landcover.</param>
        /// <param name="heightmap">Heightmap for elevation sampling (may be null).</param>
        /// <param name="tileSize">Tile size in metres.</param>
        /// <param name="material">Instanced grass material (OSMSendai/GrassIndirect).</param>
        /// <param name="bladeMesh">Small blade mesh (e.g. from FBX).</param>
        /// <param name="cullingShader">Visibility.compute for frustum culling.</param>
        /// <param name="camera">Main camera for culling.</param>
        public void Initialize(
            Mesh terrainMesh,
            HeightmapData heightmap,
            float tileSize,
            Material material,
            Mesh bladeMesh,
            ComputeShader cullingShader,
            Camera camera)
        {
            _bladeMesh = bladeMesh;
            _camera = camera;

            if (terrainMesh == null || _bladeMesh == null || material == null || cullingShader == null)
                return;

            // Each tile needs its own material and compute shader instances
            // because they bind different StructuredBuffers.
            _material = new Material(material);
            _cullingShader = Instantiate(cullingShader);

            var matrices = GatherGrassMatrices(terrainMesh, heightmap, tileSize);
            if (matrices == null || matrices.Length == 0)
                return;

            _instanceCount = matrices.Length;

            // ── Build chunks ──
            BuildChunks(matrices, tileSize, out var chunks);

            // ── Upload to GPU ──
            _trsBuffer = new ComputeBuffer(_instanceCount, 4 * 4 * sizeof(float));
            _trsBuffer.SetData(matrices);

            _visibleBuffer = new ComputeBuffer(_instanceCount, 4 * 4 * sizeof(float), ComputeBufferType.Append);

            _chunkBuffer = new ComputeBuffer(_numChunks, 3 * sizeof(float) + 2 * sizeof(int));
            _chunkBuffer.SetData(chunks);

            _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
            _readBackBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);

            // ── Configure compute shader ──
            _kernelChunkRender = _cullingShader.FindKernel("ChunkRender");
            _cullingShader.GetKernelThreadGroupSizes(_kernelChunkRender, out _threadGroupSize, out _, out _);

            _cullingShader.SetBuffer(_kernelChunkRender, "trsBuffer", _trsBuffer);
            _cullingShader.SetBuffer(_kernelChunkRender, "visibleList", _visibleBuffer);
            _cullingShader.SetBuffer(_kernelChunkRender, "chunkBuffer", _chunkBuffer);
            _cullingShader.SetInt("chunkSize", ChunkSize);
            _cullingShader.SetInt("numChunks", _numChunks);
            _cullingShader.SetInt("instanceCount", _instanceCount);

            // ── Bind visible buffer to material ──
            _material.SetBuffer("visibleList", _visibleBuffer);

            // ── Render bounds (large enough to cover tile + some margin) ──
            _renderBounds = new Bounds(transform.position, new Vector3(tileSize + 20f, 200f, tileSize + 20f));

            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized || _camera == null) return;

            Render();
        }

        private void Render()
        {
            // Update frustum planes
            var planes = GeometryUtility.CalculateFrustumPlanes(_camera);
            var planeVec = new Vector4[6];
            for (var i = 0; i < 6; i++)
                planeVec[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);

            _cullingShader.SetVector("camPos", _camera.transform.position);
            _cullingShader.SetFloat("maxViewDistance", MaxViewDistance);
            _cullingShader.SetVectorArray("viewFrustumPlanes", planeVec);

            // Reset append buffer
            _visibleBuffer.SetCounterValue(0);

            // Dispatch compute
            var groups = Mathf.CeilToInt((float)_numChunks / _threadGroupSize);
            if (groups < 1) groups = 1;
            _cullingShader.Dispatch(_kernelChunkRender, groups, 1, 1);

            // Read back visible count
            ComputeBuffer.CopyCount(_visibleBuffer, _readBackBuffer, 0);
            var countArr = new int[1];
            _readBackBuffer.GetData(countArr);
            var visibleCount = countArr[0];

            if (visibleCount <= 0) return;

            // Set args: (indexCount, instanceCount, startIndex, baseVertex, startInstance)
            _argsBuffer.SetData(new uint[5]
            {
                _bladeMesh.GetIndexCount(0),
                (uint)visibleCount,
                _bladeMesh.GetIndexStart(0),
                _bladeMesh.GetBaseVertex(0),
                0
            });

            Graphics.DrawMeshInstancedIndirect(
                _bladeMesh, 0, _material, _renderBounds, _argsBuffer,
                0, null, ShadowCastingMode.Off);
        }

        /// <summary>
        /// Scans terrain vertex colors and scatters grass blade TRS matrices around
        /// non-white (landcover) vertices.
        /// </summary>
        private Matrix4x4[] GatherGrassMatrices(Mesh terrainMesh, HeightmapData hm, float tileSize)
        {
            var verts = terrainMesh.vertices;
            var colors = terrainMesh.colors;
            if (colors == null || colors.Length == 0) return null;

            // Count grass-eligible vertices
            var grassCount = 0;
            for (var i = 0; i < colors.Length; i++)
            {
                var c = colors[i];
                if (c.r + c.g + c.b < 2.8f) grassCount++;
            }
            if (grassCount == 0) return null;

            // Tile world offset — terrain vertex positions are tile-local (centered),
            // but DrawMeshInstancedIndirect renders in world space, so we need to offset.
            var tileWorldPos = transform.localPosition;

            var totalBlades = grassCount * BladesPerVertex;
            var matrices = new Matrix4x4[totalBlades];
            var idx = 0;

            for (var i = 0; i < verts.Length; i++)
            {
                var c = colors[i];
                if (c.r + c.g + c.b >= 2.8f) continue;

                var basePos = verts[i];
                var seed = (uint)(basePos.x * 73856.093f + basePos.z * 19349.663f + i * 83492.791f);
                if (seed == 0) seed = 0x9E3779B9u;
                var rng = new DeterministicRandom(seed);

                for (var b = 0; b < BladesPerVertex; b++)
                {
                    var ox = rng.Range(-SpreadRadius, SpreadRadius);
                    var oz = rng.Range(-SpreadRadius, SpreadRadius);

                    var localX = basePos.x + ox;
                    var localZ = basePos.z + oz;
                    var localY = hm != null ? hm.SampleHeight(localX, localZ, tileSize) : basePos.y;

                    // Convert tile-local to world position
                    var pos = new Vector3(
                        tileWorldPos.x + localX,
                        tileWorldPos.y + localY,
                        tileWorldPos.z + localZ);

                    // Random Y rotation
                    var yaw = rng.Range(0f, 360f);
                    var rot = Quaternion.Euler(0f, yaw, 0f);

                    // Random scale
                    var sx = rng.Range(ScaleMin.x, ScaleMax.x);
                    var sy = rng.Range(ScaleMin.y, ScaleMax.y);
                    var sz = rng.Range(ScaleMin.z, ScaleMax.z);
                    var scale = new Vector3(sx, sy, sz);

                    matrices[idx++] = Matrix4x4.TRS(pos, rot, scale);
                }
            }

            // Trim if some were skipped (shouldn't happen, but safety)
            if (idx < totalBlades)
            {
                var trimmed = new Matrix4x4[idx];
                System.Array.Copy(matrices, trimmed, idx);
                return trimmed;
            }

            return matrices;
        }

        /// <summary>
        /// Divides grass instances into spatial chunks for GPU frustum culling.
        /// </summary>
        private void BuildChunks(Matrix4x4[] matrices, float tileSize, out ChunkData[] chunks)
        {
            var half = tileSize * 0.5f;
            var chunksPerAxis = Mathf.CeilToInt(tileSize / ChunkSize);
            _numChunks = chunksPerAxis * chunksPerAxis;

            chunks = new ChunkData[_numChunks];

            var wp = transform.localPosition;

            // Initialize chunk positions in world space
            for (var row = 0; row < chunksPerAxis; row++)
            {
                for (var col = 0; col < chunksPerAxis; col++)
                {
                    var ci = row * chunksPerAxis + col;
                    var cx = -half + (col + 0.5f) * ChunkSize;
                    var cz = -half + (row + 0.5f) * ChunkSize;
                    chunks[ci].positionX = wp.x + cx;
                    chunks[ci].positionY = wp.y;
                    chunks[ci].positionZ = wp.z + cz;
                    chunks[ci].instanceStartIndex = 0;
                    chunks[ci].instanceCount = 0;
                }
            }

            // Sort instances into chunks
            // First pass: count per chunk
            var chunkIndex = new int[matrices.Length];
            for (var i = 0; i < matrices.Length; i++)
            {
                var pos = matrices[i].GetColumn(3);
                // Convert world position back to tile-local for chunk assignment
                var lx = pos.x - wp.x;
                var lz = pos.z - wp.z;
                var col2 = Mathf.Clamp(Mathf.FloorToInt((lx + half) / ChunkSize), 0, chunksPerAxis - 1);
                var row2 = Mathf.Clamp(Mathf.FloorToInt((lz + half) / ChunkSize), 0, chunksPerAxis - 1);
                var ci = row2 * chunksPerAxis + col2;
                chunkIndex[i] = ci;
                chunks[ci].instanceCount++;
            }

            // Compute start indices
            var offset = 0;
            for (var ci = 0; ci < _numChunks; ci++)
            {
                chunks[ci].instanceStartIndex = offset;
                offset += chunks[ci].instanceCount;
            }

            // Second pass: reorder matrices by chunk
            var sorted = new Matrix4x4[matrices.Length];
            var writeIdx = new int[_numChunks];
            for (var ci = 0; ci < _numChunks; ci++)
                writeIdx[ci] = chunks[ci].instanceStartIndex;

            for (var i = 0; i < matrices.Length; i++)
            {
                var ci = chunkIndex[i];
                sorted[writeIdx[ci]++] = matrices[i];
            }

            // Copy back to matrices array (which will be uploaded to GPU)
            System.Array.Copy(sorted, matrices, matrices.Length);

            // Compute average Y per chunk for better frustum culling
            for (var ci = 0; ci < _numChunks; ci++)
            {
                if (chunks[ci].instanceCount == 0) continue;
                var sumY = 0f;
                var start = chunks[ci].instanceStartIndex;
                var end = start + chunks[ci].instanceCount;
                for (var i = start; i < end; i++)
                    sumY += matrices[i].GetColumn(3).y;
                chunks[ci].positionY = sumY / chunks[ci].instanceCount;
            }
        }

        public void Dispose()
        {
            _initialized = false;
            _trsBuffer?.Release();
            _visibleBuffer?.Release();
            _argsBuffer?.Release();
            _readBackBuffer?.Release();
            _chunkBuffer?.Release();
            _trsBuffer = null;
            _visibleBuffer = null;
            _argsBuffer = null;
            _readBackBuffer = null;
            _chunkBuffer = null;

            if (_material != null) { Destroy(_material); _material = null; }
            if (_cullingShader != null) { Destroy(_cullingShader); _cullingShader = null; }
        }

        private void OnDestroy()
        {
            Dispose();
        }

        /// <summary>
        /// GPU-side chunk layout matching the compute shader struct.
        /// Must match exactly: float3 position + int startIndex + int count = 20 bytes.
        /// </summary>
        private struct ChunkData
        {
            public float positionX;
            public float positionY;
            public float positionZ;
            public int instanceStartIndex;
            public int instanceCount;
        }
    }
}
