# StreamingAssets/OSMSendai

This folder is where the “light tile data” will live (OSM vectors + DEM tiles), shipped with the game build.

- `tileset.json`: tileset metadata (projection, tile size, DEM LODs, version).
- `tiles/`: per-tile payload files, e.g. `tile_<lod>_<tx>_<ty>.bin.zst`.

The runtime generator will read from here, build meshes, then write generated caches to
`Application.persistentDataPath`.

## MVP tile payload (JSON)
For the current MVP generator, tiles can be provided as JSON:
- Path: `tiles/tile_<lod>_<tx>_<ty>.json`
- Coordinates: tile-local meters relative to tile origin (center).
- Features: `buildings`, `roads`, `waters` (area), `waterways` (linear), `landcovers`

See `tiles/tile_0_0_0.json` for a minimal example.
