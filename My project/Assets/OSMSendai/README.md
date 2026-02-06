# OSM Sendai (Unity)

This folder contains the runtime world streaming scaffolding for the “OSM + DEM” map world.

## Quick start

1) Open the Unity project.
2) (If needed) Enable URP:
   - `OSM Sendai > Setup > Check URP Enabled`
   - If not enabled: `OSM Sendai > Setup > Open Render Pipeline Converter`
3) Create an empty GameObject, add:
   - `OsmSendai.World.FloatingOrigin`
   - `OsmSendai.World.WorldBootstrap`
4) Press Play.

You should see deterministic “debug tiles” (flat terrain + simple buildings) streaming in/out around the camera.

## Next steps
- Replace `DebugTileGenerator` with a generator that consumes real tile data from `StreamingAssets/OSMSendai/`.
- Add DEM decoding and feature generation (buildings/roads/water/vegetation).

