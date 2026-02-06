#!/usr/bin/env python3
"""
MVP placeholder preprocessor.

Goal:
  - Read an OSM .pbf
  - Project coordinates to a local metric CRS (recommended: EPSG:32654)
  - Cut features into 512m tiles
  - Output per-tile JSON compatible with Unity runtime:
      My project/Assets/StreamingAssets/OSMSendai/tiles/tile_<lod>_<tx>_<ty>.json

This script is intentionally incomplete until we pick the exact OSM+DEM sources and
the dependency stack (pyosmium/osmium-tool/GDAL).
"""

from __future__ import annotations

import argparse


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pbf", required=True, help="Input OSM PBF path")
    ap.add_argument("--out", required=True, help="Output tiles folder")
    ap.add_argument("--tile-size", type=float, default=512.0)
    ap.add_argument("--epsg", default="32654", help="Target EPSG (default: 32654)")
    args = ap.parse_args()

    raise SystemExit(
        "Not implemented yet. Next step is to wire pyosmium + pyproj (or osmium+GDAL) "
        "based on the data you downloaded."
    )


if __name__ == "__main__":
    raise SystemExit(main())

