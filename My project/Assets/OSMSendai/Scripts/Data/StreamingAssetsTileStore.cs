using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace OsmSendai.Data
{
    public sealed class StreamingAssetsTileStore
    {
        private readonly string _rootFolder;

        public StreamingAssetsTileStore(string rootFolderRelativeToStreamingAssets = "OSMSendai")
        {
            _rootFolder = rootFolderRelativeToStreamingAssets.Trim().Trim('/');
        }

        public async Task<TilesetMetadata> LoadTilesetAsync(CancellationToken cancellationToken)
        {
            var json = await ReadTextAsync(Path.Combine(_rootFolder, "tileset.json"), cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new TilesetMetadata();
            }
            return JsonUtility.FromJson<TilesetMetadata>(json) ?? new TilesetMetadata();
        }

        public async Task<TilePayload> TryLoadTileAsync(int lod, int tx, int ty, CancellationToken cancellationToken)
        {
            var filename = $"tile_{lod}_{tx}_{ty}.json";
            var json = await ReadTextAsync(Path.Combine(_rootFolder, "tiles", filename), cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<TilePayload>(json);
            }
            catch
            {
                return null;
            }
        }

        public async Task<HeightmapData> TryLoadHeightmapAsync(int lod, int tx, int ty, CancellationToken cancellationToken)
        {
            var filename = $"dem_{lod}_{tx}_{ty}.bin";
            var bytes = await ReadBytesAsync(Path.Combine(_rootFolder, "dem", filename), cancellationToken);
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            try
            {
                return ParseHeightmapBinary(bytes);
            }
            catch
            {
                return null;
            }
        }

        private static HeightmapData ParseHeightmapBinary(byte[] bytes)
        {
            // Format: [int32 gridW] [int32 gridH] [float32 × gridW*gridH]
            const int headerSize = 8; // 2 × int32
            if (bytes.Length < headerSize) return null;

            var gridW = BitConverter.ToInt32(bytes, 0);
            var gridH = BitConverter.ToInt32(bytes, 4);

            if (gridW <= 0 || gridH <= 0) return null;

            var expectedSize = headerSize + gridW * gridH * 4;
            if (bytes.Length < expectedSize) return null;

            var heights = new float[gridW * gridH];
            Buffer.BlockCopy(bytes, headerSize, heights, 0, gridW * gridH * 4);

            return new HeightmapData
            {
                GridWidth = gridW,
                GridHeight = gridH,
                Heights = heights,
            };
        }

        private static async Task<byte[]> ReadBytesAsync(string streamingAssetsRelativePath, CancellationToken cancellationToken)
        {
            var fullPath = Path.Combine(Application.streamingAssetsPath, streamingAssetsRelativePath);

            if (fullPath.Contains("://") || fullPath.Contains("jar:") || fullPath.Contains("http"))
            {
                using (var req = UnityWebRequest.Get(fullPath))
                {
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        return null;
                    }
                    return req.downloadHandler.data;
                }
            }

            if (!File.Exists(fullPath)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            return File.ReadAllBytes(fullPath);
        }

        private static async Task<string> ReadTextAsync(string streamingAssetsRelativePath, CancellationToken cancellationToken)
        {
            var fullPath = Path.Combine(Application.streamingAssetsPath, streamingAssetsRelativePath);

            // On some platforms StreamingAssets is inside a jar/opaque container; use UnityWebRequest for portability.
            if (fullPath.Contains("://") || fullPath.Contains("jar:") || fullPath.Contains("http"))
            {
                using (var req = UnityWebRequest.Get(fullPath))
                {
                    var op = req.SendWebRequest();
                    while (!op.isDone)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Yield();
                    }

                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        return null;
                    }
                    return req.downloadHandler.text;
                }
            }

            if (!File.Exists(fullPath)) return null;
            using (var reader = new StreamReader(fullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await reader.ReadToEndAsync();
            }
        }
    }
}

