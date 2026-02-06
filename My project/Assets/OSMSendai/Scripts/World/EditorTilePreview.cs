using UnityEngine;

namespace OsmSendai.World
{
    /// <summary>
    /// Configuration component for Editor-time tile preview.
    /// Attach to any GameObject, configure in the Inspector, then click "Load Preview".
    /// </summary>
    public sealed class EditorTilePreview : MonoBehaviour
    {
        [Header("Tile Range")]
        [Tooltip("X index of the center tile.")]
        public int centerTileX = 0;

        [Tooltip("Y index of the center tile.")]
        public int centerTileY = 0;

        [Tooltip("Radius in tiles around the center (0 = single tile, 1 = 3x3, 2 = 5x5).")]
        [Range(0, 5)]
        public int radiusTiles = 1;

        [Header("Layer Visibility")]
        public bool showTerrain = true;
        public bool showBuildings = true;
        public bool showRoads = true;
        public bool showWater = true;
        public bool showLandcover = true;
        public bool showVegetation = true;

        [Header("Materials (optional — auto-created if empty)")]
        public Material terrainMaterial;
        public Material buildingsMaterial;
        public Material roadsMaterial;
        public Material waterMaterial;
        public Material landcoverMaterial;
        public Material vegetationMaterial;

        [Header("Data")]
        [Tooltip("Folder name inside StreamingAssets.")]
        public string dataFolder = "OSMSendai";

        [HideInInspector]
        public float tileSizeMeters;
    }
}
