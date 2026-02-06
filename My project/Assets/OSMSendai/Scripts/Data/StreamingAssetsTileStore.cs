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

