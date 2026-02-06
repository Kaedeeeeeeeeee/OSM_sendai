# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OSM Sendai is a Unity project for rendering real-world maps (OpenStreetMap + DEM) with streaming tile support. The project is centered around Sendai, Japan, using WebMercator projection (EPSG:3857).

## Repository Structure

- `My project/` - Unity project root (Unity 2022 LTS with URP)
- `tools/` - Python preprocessing scripts (no external dependencies)
- `planet_*-shp/` - Raw BBBike ShapeFile extracts (source data)

## Development Commands

### Preprocessing OSM Data

Convert BBBike ShapeFile extract to Unity tile JSON:
```bash
python3 tools/shp_to_tiles.py \
  --shape-dir planet_xxx-shp/shape \
  --out "My project/Assets/StreamingAssets/OSMSendai" \
  --tile-size 1024 \
  --origin-lat 38.2600 --origin-lon 140.8815 \
  --clean \
  --clip-readme planet_xxx-shp/README.txt
```

Convert GeoJSON to tile JSON:
```bash
python3 tools/geojson_to_tiles.py \
  --geojson /path/to/export.geojson \
  --out "My project/Assets/StreamingAssets/OSMSendai" \
  --origin-lat 38.2600 --origin-lon 140.8815
```

### URP Setup (Unity Editor)

Menu: `OSM Sendai > Setup > Check URP Enabled`
If needed: `OSM Sendai > Setup > Open Render Pipeline Converter`

## Architecture

### Runtime (Unity C#)

The world streaming system uses a tile-based architecture in `Assets/OSMSendai/Scripts/`:

**Core Flow:**
1. `WorldBootstrap` initializes the system, creates a `TileManager` and selects which `ITileGenerator` to use
2. `TileManager` tracks camera position and loads/unloads tiles within a configurable radius
3. `ITileGenerator` implementations build meshes from tile data (terrain, buildings, roads, water)

**Tile Generators (Strategy Pattern):**
- `StreamingTileGenerator` - Loads real tile data from `StreamingAssets/OSMSendai/tiles/`
- `DebugTileGenerator` - Generates procedural placeholder content (deterministic per-tile)
- `CompositeTileGenerator` - Tries primary generator, falls back to secondary (allows debug fallback for missing tiles)

**Data Layer (`Scripts/Data/`):**
- `StreamingAssetsTileStore` - Async loader for JSON tile payloads from StreamingAssets
- `TileSchema.cs` - Defines `TilePayload`, `Building`, `Road`, `Polygon`, `Waterway`, `Landcover` structures

**Mesh Generation (`Scripts/World/`):**
- `MeshBuilder` - Procedural mesh construction (boxes, ribbons, extruded polygons)
- `PolygonTriangulator` - Ear-clipping triangulation for simple polygons
- `FloatingOrigin` - Shifts world coordinates to avoid floating-point precision issues at large distances

### Data Format

Tiles are JSON files at `StreamingAssets/OSMSendai/tiles/tile_<lod>_<tx>_<ty>.json`:
- Coordinates are tile-local meters relative to tile center
- Features: `buildings` (extruded polygons), `roads` (ribbons), `waters` (flat polygons), `waterways` (ribbons), `landcovers`
- Metadata in `tileset.json` defines projection, tile size, and origin anchor

### Preprocessing (Python)

Tools convert raw OSM data to tile JSON. Pure Python with no dependencies:
- `shp_to_tiles.py` - BBBike ShapeFile conversion
- `geojson_to_tiles.py` - GeoJSON conversion
- Features assigned to tiles by centroid (no border clipping in MVP)
- Uses WebMercator projection with configurable origin anchor

## Scene Setup

1. Create empty GameObject
2. Add `FloatingOrigin` component
3. Add `WorldBootstrap` component
4. Assign camera reference (defaults to `Camera.main`)
5. Materials are auto-created if not assigned (URP Lit or Standard fallback)
