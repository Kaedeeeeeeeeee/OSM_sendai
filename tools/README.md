# OSM Sendai tools (offline preprocessing)

These tools turn raw OSM/DEM data into **lightweight per-tile payloads** to ship with the Unity game.

## What you need to provide (manual download)
- OSM extract for Sendai (or larger area) as `.pbf`
- DEM tiles (10m near, plus optional 30m/90m for far LOD)

## Easiest path for MVP (no GIS dependencies)
Export Sendai-area OSM data as **GeoJSON** (e.g. via BBBike Extract), then run:

`python3 tools/geojson_to_tiles.py --geojson /path/to/export.geojson --out \"My project/Assets/StreamingAssets/OSMSendai\" --origin-lat 38.2600 --origin-lon 140.8815`

This produces:
- `.../OSMSendai/tileset.json`
- `.../OSMSendai/tiles/tile_0_<tx>_<ty>.json`

## If you already have a BBBike "shp" extract (WGS84)
If you downloaded a BBBike ShapeFile extract like:
`planet_*.osm.shp.zip` (contains `shape/buildings.shp`, `roads.shp`, etc),
you can convert it without external dependencies:

`python3 tools/shp_to_tiles.py --shape-dir planet_xxx-shp/shape --out \"My project/Assets/StreamingAssets/OSMSendai\" --tile-size 1024 --origin-lat 38.2600 --origin-lon 140.8815 --clean --clip-readme planet_xxx-shp/README.txt`

Notes:
- BBBike extracts are **always cut by a bounding rectangle**, even if you drew a polygon. `--clip-readme` clips features back to the polygon you drew (approximate, by centroid).

## Output
- `My project/Assets/StreamingAssets/OSMSendai/tileset.json`
- `My project/Assets/StreamingAssets/OSMSendai/tiles/tile_<lod>_<tx>_<ty>.json` (MVP)

## Notes
- For MVP, the Unity runtime supports JSON tiles only. We'll switch to a compact binary format later.
