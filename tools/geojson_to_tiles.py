#!/usr/bin/env python3
from __future__ import annotations

"""
Convert OSM-derived GeoJSON (WGS84 lon/lat) into OSM Sendai tile JSON payloads.

MVP goals:
- No external Python dependencies.
- Uses WebMercator projection (EPSG:3857) and subtracts an origin anchor so Unity coordinates stay near (0,0).
- Cuts features into square tiles (default 512m).

Limitations (acceptable for MVP):
- Buildings/water/landcover polygons are assigned to a tile by polygon centroid (no clipping at tile borders).
- Roads are assigned to a tile by polyline midpoint (no splitting across tiles).
"""

import argparse
import json
import math
import os
from dataclasses import dataclass
from typing import Any, Dict, Iterable, List, Optional, Tuple


EARTH_RADIUS_M = 6378137.0


def lonlat_to_webmercator_m(lon: float, lat: float) -> Tuple[float, float]:
    # Clamp latitude to Mercator bounds.
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = math.radians(lon) * EARTH_RADIUS_M
    y = math.log(math.tan(math.pi / 4.0 + math.radians(lat) / 2.0)) * EARTH_RADIUS_M
    return x, y


def polygon_centroid(points: List[Tuple[float, float]]) -> Tuple[float, float]:
    # Simple polygon centroid (shoelace). Falls back to average if degenerate.
    if len(points) < 3:
        sx = sum(p[0] for p in points) / max(1, len(points))
        sy = sum(p[1] for p in points) / max(1, len(points))
        return sx, sy

    a = 0.0
    cx = 0.0
    cy = 0.0
    for i in range(len(points)):
        x0, y0 = points[i]
        x1, y1 = points[(i + 1) % len(points)]
        cross = x0 * y1 - x1 * y0
        a += cross
        cx += (x0 + x1) * cross
        cy += (y0 + y1) * cross
    if abs(a) < 1e-6:
        sx = sum(p[0] for p in points) / len(points)
        sy = sum(p[1] for p in points) / len(points)
        return sx, sy
    a *= 0.5
    cx /= (6.0 * a)
    cy /= (6.0 * a)
    return cx, cy


def polyline_midpoint(points: List[Tuple[float, float]]) -> Tuple[float, float]:
    if len(points) == 0:
        return 0.0, 0.0
    if len(points) == 1:
        return points[0]
    # Use middle vertex (good enough for MVP).
    return points[len(points) // 2]


def tile_xy(x: float, y: float, tile_size: float) -> Tuple[int, int]:
    return math.floor(x / tile_size), math.floor(y / tile_size)


def tile_center(tx: int, ty: int, tile_size: float) -> Tuple[float, float]:
    return (tx * tile_size + tile_size * 0.5), (ty * tile_size + tile_size * 0.5)


def classify_polygon(props: Dict[str, Any]) -> Optional[str]:
    # Returns: "building" | "water" | "landcover_forest" | "landcover_grass" | None
    if not props:
        return None
    if props.get("building") not in (None, "no", "0", False):
        return "building"
    natural = props.get("natural")
    landuse = props.get("landuse")
    water = props.get("water")
    waterway = props.get("waterway")

    if natural in ("water",) or water in ("lake", "pond", "reservoir") or landuse in ("reservoir", "basin") or waterway in ("riverbank",):
        return "water"

    if natural in ("wood", "forest") or landuse in ("forest",):
        return "landcover_forest"
    if landuse in ("grass", "meadow", "park") or natural in ("grassland",):
        return "landcover_grass"
    return None


def highway_width_m(props: Dict[str, Any]) -> float:
    hwy = (props or {}).get("highway")
    table = {
        "motorway": 18.0,
        "trunk": 16.0,
        "primary": 12.0,
        "secondary": 10.0,
        "tertiary": 8.0,
        "residential": 6.0,
        "unclassified": 6.0,
        "service": 4.0,
        "living_street": 4.0,
        "footway": 2.5,
        "path": 2.0,
        "pedestrian": 4.0,
        "cycleway": 2.5,
    }
    return float(table.get(hwy, 6.0))


def building_height_m(props: Dict[str, Any]) -> float:
    if not props:
        return 12.0
    h = props.get("height")
    if isinstance(h, (int, float)) and h > 0:
        return float(h)
    if isinstance(h, str):
        try:
            return float(h.replace("m", "").strip())
        except Exception:
            pass
    levels = props.get("building:levels") or props.get("levels")
    if isinstance(levels, (int, float)) and levels > 0:
        return float(levels) * 3.0
    if isinstance(levels, str):
        try:
            return float(levels.strip()) * 3.0
        except Exception:
            pass
    return 12.0


@dataclass
class TileBuckets:
    buildings: List[Dict[str, Any]]
    roads: List[Dict[str, Any]]
    waters: List[Dict[str, Any]]
    landcovers: List[Dict[str, Any]]


def ensure_dir(path: str) -> None:
    os.makedirs(path, exist_ok=True)


def iter_features(geojson: Dict[str, Any]) -> Iterable[Dict[str, Any]]:
    if geojson.get("type") == "FeatureCollection":
        for f in geojson.get("features", []):
            if isinstance(f, dict) and f.get("type") == "Feature":
                yield f
    elif geojson.get("type") == "Feature":
        yield geojson


def project_ring_lonlat_to_local_m(
    ring: List[List[float]],
    origin_x: float,
    origin_y: float,
) -> List[Tuple[float, float]]:
    out: List[Tuple[float, float]] = []
    for lon, lat in ring:
        x, y = lonlat_to_webmercator_m(float(lon), float(lat))
        out.append((x - origin_x, y - origin_y))
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--geojson", required=True, help="Input GeoJSON file (WGS84 lon/lat)")
    ap.add_argument("--out", required=True, help="Output root folder (OSMSendai). Example: My project/Assets/StreamingAssets/OSMSendai")
    ap.add_argument("--tile-size", type=float, default=512.0)
    ap.add_argument("--origin-lat", type=float, default=38.2600, help="Origin anchor latitude (default: around Sendai Station)")
    ap.add_argument("--origin-lon", type=float, default=140.8815, help="Origin anchor longitude (default: around Sendai Station)")
    ap.add_argument("--lod", type=int, default=0)
    args = ap.parse_args()

    with open(args.geojson, "r", encoding="utf-8") as f:
        gj = json.load(f)

    origin_x, origin_y = lonlat_to_webmercator_m(args.origin_lon, args.origin_lat)

    tiles: Dict[Tuple[int, int], TileBuckets] = {}

    def get_bucket(tx: int, ty: int) -> TileBuckets:
        k = (tx, ty)
        if k not in tiles:
            tiles[k] = TileBuckets(buildings=[], roads=[], waters=[], landcovers=[])
        return tiles[k]

    for feat in iter_features(gj):
        geom = feat.get("geometry") or {}
        gtype = geom.get("type")
        props = feat.get("properties") or {}

        if gtype in ("Polygon", "MultiPolygon"):
            kind = classify_polygon(props)
            if kind is None:
                continue

            polygons = geom.get("coordinates") or []
            if gtype == "Polygon":
                polygons = [polygons]

            for poly in polygons:
                if not poly or not isinstance(poly, list):
                    continue
                outer = poly[0] if len(poly) > 0 else None
                if not outer or len(outer) < 3:
                    continue
                pts = project_ring_lonlat_to_local_m(outer, origin_x, origin_y)
                cx, cy = polygon_centroid(pts)
                tx, ty = tile_xy(cx, cy, args.tile_size)
                center_x, center_y = tile_center(tx, ty, args.tile_size)
                local = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in pts]

                bucket = get_bucket(tx, ty)
                if kind == "building":
                    bucket.buildings.append({"heightMeters": float(building_height_m(props)), "vertices": local})
                elif kind == "water":
                    bucket.waters.append({"vertices": local})
                elif kind == "landcover_forest":
                    bucket.landcovers.append({"kind": "forest", "densityPerKm2": 800.0, "vertices": local})
                elif kind == "landcover_grass":
                    bucket.landcovers.append({"kind": "grass", "densityPerKm2": 250.0, "vertices": local})

        if gtype in ("LineString", "MultiLineString"):
            if "highway" not in props:
                continue
            lines = geom.get("coordinates") or []
            if gtype == "LineString":
                lines = [lines]
            for line in lines:
                if not line or len(line) < 2:
                    continue
                pts_m: List[Tuple[float, float]] = []
                for lon, lat in line:
                    x, y = lonlat_to_webmercator_m(float(lon), float(lat))
                    pts_m.append((x - origin_x, y - origin_y))
                mx, my = polyline_midpoint(pts_m)
                tx, ty = tile_xy(mx, my, args.tile_size)
                center_x, center_y = tile_center(tx, ty, args.tile_size)
                local = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in pts_m]
                get_bucket(tx, ty).roads.append({"widthMeters": float(highway_width_m(props)), "points": local})

    out_root = args.out
    out_tiles = os.path.join(out_root, "tiles")
    ensure_dir(out_tiles)

    # Write tileset.json
    tileset = {
        "projection": "LOCAL_WEBMERCATOR",
        "tileSizeMeters": args.tile_size,
        "dataVersion": 1,
        "demLods": [{"lod": 0, "resolutionMeters": 10}, {"lod": 1, "resolutionMeters": 30}, {"lod": 2, "resolutionMeters": 90}],
        "origin": {"lat": args.origin_lat, "lon": args.origin_lon},
    }
    with open(os.path.join(out_root, "tileset.json"), "w", encoding="utf-8") as f:
        json.dump(tileset, f, ensure_ascii=False, indent=2)

    # Write per-tile payloads
    for (tx, ty), bucket in tiles.items():
        payload = {
            "lod": int(args.lod),
            "tx": int(tx),
            "ty": int(ty),
            "buildings": bucket.buildings,
            "roads": bucket.roads,
            "waters": bucket.waters,
            "landcovers": bucket.landcovers,
        }
        path = os.path.join(out_tiles, f"tile_{args.lod}_{tx}_{ty}.json")
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False)

    print(f"Wrote {len(tiles)} tiles to: {out_tiles}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

