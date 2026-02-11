#!/usr/bin/env python3
from __future__ import annotations

"""
Convert BBBike "shp" extracts (WGS84 shapefiles) into OSM Sendai tile JSON payloads.

Inputs expected (inside --shape-dir):
  - buildings.shp/.dbf (Polygon)
  - roads.shp/.dbf (PolyLine)
  - landuse.shp/.dbf (Polygon)
  - natural.shp/.dbf (Polygon)
  - waterways.shp/.dbf (PolyLine) [optional; currently ignored for MVP]

No third-party dependencies (pure Python). This is an MVP converter:
  - polygons are simplified to their AABB footprint (4 points) to keep output compact
  - features are assigned to a tile by their centroid/midpoint (no border clipping)
  - height information is not present in BBBike DBFs; we use type-based defaults
"""

import argparse
import json
import math
import os
import shutil
import struct
import urllib.parse
from collections import OrderedDict
from dataclasses import dataclass
from typing import Any, Dict, Iterable, Iterator, List, Optional, Tuple


EARTH_RADIUS_M = 6378137.0


def lonlat_to_webmercator_m(lon: float, lat: float) -> Tuple[float, float]:
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = math.radians(lon) * EARTH_RADIUS_M
    y = math.log(math.tan(math.pi / 4.0 + math.radians(lat) / 2.0)) * EARTH_RADIUS_M
    return x, y


def tile_xy(x: float, y: float, tile_size: float) -> Tuple[int, int]:
    return math.floor(x / tile_size), math.floor(y / tile_size)


def tile_center(tx: int, ty: int, tile_size: float) -> Tuple[float, float]:
    return (tx * tile_size + tile_size * 0.5), (ty * tile_size + tile_size * 0.5)


def ensure_dir(path: str) -> None:
    os.makedirs(path, exist_ok=True)


def read_dbf_records(path: str, encoding: str = "utf-8") -> Iterator[Dict[str, Any]]:
    with open(path, "rb") as f:
        header = f.read(32)
        if len(header) != 32:
            raise ValueError(f"Invalid DBF header: {path}")
        num_records = struct.unpack("<I", header[4:8])[0]
        header_len = struct.unpack("<H", header[8:10])[0]
        record_len = struct.unpack("<H", header[10:12])[0]

        fields: List[Tuple[str, str, int]] = []
        while True:
            desc = f.read(32)
            if not desc:
                raise ValueError(f"Unexpected EOF in DBF field list: {path}")
            if desc[0] == 0x0D:
                break
            name = desc[:11].split(b"\x00", 1)[0].decode("ascii", "ignore").strip()
            ftype = chr(desc[11])
            flen = int(desc[16])
            fields.append((name, ftype, flen))

        # Skip to start of records (some DBFs have extra header bytes)
        f.seek(header_len, os.SEEK_SET)

        for _ in range(num_records):
            rec = f.read(record_len)
            if len(rec) != record_len:
                break
            if rec[:1] == b"*":
                continue
            out: Dict[str, Any] = {}
            offset = 1
            for (name, ftype, flen) in fields:
                raw = rec[offset : offset + flen]
                offset += flen
                if ftype in ("N", "F"):
                    s = raw.decode("ascii", "ignore").strip()
                    out[name] = s
                else:
                    out[name] = raw.decode(encoding, "ignore").strip()
            yield out


@dataclass
class ShpRecord:
    shape_type: int
    parts: List[List[Tuple[float, float]]]  # lon/lat points per part


def iter_shp_records(path: str) -> Iterator[ShpRecord]:
    with open(path, "rb") as f:
        header = f.read(100)
        if len(header) != 100:
            raise ValueError(f"Invalid SHP header: {path}")

        while True:
            rec_header = f.read(8)
            if not rec_header:
                break
            if len(rec_header) != 8:
                break
            # record number (big endian), content length in 16-bit words (big endian)
            _rec_no, content_len_words = struct.unpack(">2i", rec_header)
            content_len_bytes = content_len_words * 2
            content = f.read(content_len_bytes)
            if len(content) != content_len_bytes:
                break

            if content_len_bytes < 4:
                continue
            shape_type = struct.unpack("<i", content[:4])[0]
            if shape_type == 0:
                continue

            # Support PolyLine (3) / Polygon (5) + Z variants (13/15)
            if shape_type not in (3, 5, 13, 15):
                continue

            # Common structure:
            #   int32 shapeType
            #   double bbox[4]
            #   int32 numParts
            #   int32 numPoints
            #   int32 parts[numParts]
            #   Point points[numPoints] (double x,y)
            # For Z variants, extra Z/M arrays follow; we ignore them.
            if len(content) < 44:
                continue
            num_parts = struct.unpack("<i", content[36:40])[0]
            num_points = struct.unpack("<i", content[40:44])[0]
            parts_idx = 44
            points_idx = parts_idx + 4 * num_parts
            if points_idx > len(content):
                continue
            part_starts = list(struct.unpack("<" + "i" * num_parts, content[parts_idx:points_idx])) if num_parts > 0 else [0]

            points: List[Tuple[float, float]] = []
            for i in range(num_points):
                off = points_idx + i * 16
                if off + 16 > len(content):
                    break
                x, y = struct.unpack("<2d", content[off : off + 16])
                points.append((float(x), float(y)))

            parts: List[List[Tuple[float, float]]] = []
            if not part_starts:
                part_starts = [0]
            for p in range(len(part_starts)):
                start = part_starts[p]
                end = part_starts[p + 1] if p + 1 < len(part_starts) else len(points)
                seg = points[start:end]
                if len(seg) >= 2:
                    parts.append(seg)

            yield ShpRecord(shape_type=shape_type, parts=parts)


def iter_shp_point_records(path: str) -> Iterator[Tuple[float, float]]:
    """Yield (lon, lat) for Point (type 1), PointZ (11), or PointM (21) shapes."""
    with open(path, "rb") as f:
        header = f.read(100)
        if len(header) != 100:
            raise ValueError(f"Invalid SHP header: {path}")

        while True:
            rec_header = f.read(8)
            if not rec_header or len(rec_header) != 8:
                break
            _rec_no, content_len_words = struct.unpack(">2i", rec_header)
            content_len_bytes = content_len_words * 2
            content = f.read(content_len_bytes)
            if len(content) != content_len_bytes:
                break
            if content_len_bytes < 4:
                continue
            shape_type = struct.unpack("<i", content[:4])[0]
            if shape_type == 0:
                continue
            if shape_type not in (1, 11, 21):
                continue
            if len(content) < 20:
                continue
            x, y = struct.unpack("<2d", content[4:20])
            yield (float(x), float(y))


class LruAppendWriter:
    def __init__(self, max_open: int = 128) -> None:
        self._max_open = max_open
        self._files: "OrderedDict[str, Any]" = OrderedDict()

    def append_line(self, path: str, line: str) -> None:
        f = self._files.get(path)
        if f is None:
            ensure_dir(os.path.dirname(path))
            f = open(path, "a", encoding="utf-8")
            self._files[path] = f
        else:
            self._files.move_to_end(path)
        f.write(line)
        f.write("\n")
        if len(self._files) > self._max_open:
            old_path, old_file = self._files.popitem(last=False)
            old_file.close()

    def close(self) -> None:
        for f in self._files.values():
            f.close()
        self._files.clear()


def building_height_default(building_type: str) -> float:
    t = (building_type or "").lower()
    table = {
        "apartments": 24.0,
        "residential": 12.0,
        "house": 9.0,
        "detached": 9.0,
        "commercial": 18.0,
        "retail": 15.0,
        "office": 30.0,
        "industrial": 12.0,
        "warehouse": 10.0,
        "hospital": 24.0,
        "school": 15.0,
        "university": 18.0,
        "hotel": 30.0,
    }
    return float(table.get(t, 12.0))


def road_width_default(road_type: str) -> float:
    t = (road_type or "").lower()
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
    return float(table.get(t, 6.0))


def interpolate_point(p1: Tuple[float, float], p2: Tuple[float, float], t: float) -> Tuple[float, float]:
    """Interpolate between two points at parameter t (0 to 1)."""
    return (p1[0] + t * (p2[0] - p1[0]), p1[1] + t * (p2[1] - p1[1]))


def split_polyline_by_tiles(pts: List[Tuple[float, float]], tile_size: float) -> Dict[Tuple[int, int], List[List[Tuple[float, float]]]]:
    """
    Split a polyline into segments, one per tile it passes through.
    Also interpolates crossing points at tile boundaries for clean splits.
    Returns a dict: {(tx, ty): [list of point sequences for this tile]}
    """
    if len(pts) < 2:
        return {}

    result: Dict[Tuple[int, int], List[List[Tuple[float, float]]]] = {}

    def add_segment(tile: Tuple[int, int], segment: List[Tuple[float, float]]) -> None:
        if len(segment) >= 2:
            if tile not in result:
                result[tile] = []
            result[tile].append(segment)

    current_segment: List[Tuple[float, float]] = [pts[0]]
    current_tile = tile_xy(pts[0][0], pts[0][1], tile_size)

    for i in range(1, len(pts)):
        prev_pt = pts[i - 1]
        curr_pt = pts[i]
        curr_tile = tile_xy(curr_pt[0], curr_pt[1], tile_size)

        if curr_tile == current_tile:
            # Same tile, just add point
            current_segment.append(curr_pt)
        else:
            # Crossed tile boundary - need to find crossing point(s)
            # For simplicity, we'll add intermediate points along the segment
            dx = curr_pt[0] - prev_pt[0]
            dy = curr_pt[1] - prev_pt[1]
            dist = math.hypot(dx, dy)

            if dist > tile_size * 0.5:
                # Long segment - interpolate points
                num_steps = max(2, int(dist / (tile_size * 0.4)))
                for step in range(1, num_steps):
                    t = step / num_steps
                    interp_pt = interpolate_point(prev_pt, curr_pt, t)
                    interp_tile = tile_xy(interp_pt[0], interp_pt[1], tile_size)

                    if interp_tile != current_tile:
                        # Finish current segment
                        current_segment.append(interp_pt)
                        add_segment(current_tile, current_segment)

                        # Start new segment
                        current_segment = [interp_pt]
                        current_tile = interp_tile

            # Add the actual point
            if curr_tile != current_tile:
                current_segment.append(curr_pt)
                add_segment(current_tile, current_segment)
                current_segment = [curr_pt]
                current_tile = curr_tile
            else:
                current_segment.append(curr_pt)

    # Don't forget the last segment
    add_segment(current_tile, current_segment)

    return result


def classify_polygon(poly_type: str) -> Optional[Tuple[str, Optional[str]]]:
    t = (poly_type or "").lower()
    # BBBike's `natural.shp` typically uses just `type=water` (no lake/pond distinction).
    # We still classify it as water-area, but later we may skip long-narrow polygons (riverbanks)
    # and rely on linear waterways for rivers/streams.
    if t in ("water", "reservoir", "riverbank", "basin", "lake", "pond", "wetland"):
        return ("water", None)
    if t in ("wood", "forest", "scrub"):
        return ("landcover", "forest")
    if t in ("grass", "meadow", "park", "recreation_ground", "village_green"):
        return ("landcover", "grass")
    return None


def polygon_area(points: List[Tuple[float, float]]) -> float:
    if len(points) < 3:
        return 0.0
    a = 0.0
    for i in range(len(points)):
        x0, y0 = points[i]
        x1, y1 = points[(i + 1) % len(points)]
        a += x0 * y1 - x1 * y0
    return abs(a) * 0.5


def point_line_distance(p: Tuple[float, float], a: Tuple[float, float], b: Tuple[float, float]) -> float:
    (px, py) = p
    (ax, ay) = a
    (bx, by) = b
    dx = bx - ax
    dy = by - ay
    if (dx * dx + dy * dy) < 1e-12:
        return math.hypot(px - ax, py - ay)
    t = ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)
    t = 0.0 if t < 0.0 else 1.0 if t > 1.0 else t
    cx = ax + t * dx
    cy = ay + t * dy
    return math.hypot(px - cx, py - cy)


def simplify_douglas_peucker(points: List[Tuple[float, float]], epsilon: float) -> List[Tuple[float, float]]:
    if len(points) < 4:
        return points

    # If it's a closed ring, remove the duplicated last point for simplification.
    closed = points[0] == points[-1]
    pts = points[:-1] if closed else points
    n = len(pts)
    if n < 4:
        return points

    keep = [False] * n
    keep[0] = True
    keep[-1] = True

    stack = [(0, n - 1)]
    while stack:
        start, end = stack.pop()
        max_dist = -1.0
        idx = -1
        a = pts[start]
        b = pts[end]
        for i in range(start + 1, end):
            d = point_line_distance(pts[i], a, b)
            if d > max_dist:
                max_dist = d
                idx = i
        if max_dist > epsilon and idx != -1:
            keep[idx] = True
            stack.append((start, idx))
            stack.append((idx, end))

    out = [p for i, p in enumerate(pts) if keep[i]]
    if closed:
        out.append(out[0])
    return out


def ring_without_duplicate_closure(points: List[Tuple[float, float]]) -> List[Tuple[float, float]]:
    if len(points) >= 2 and points[0] == points[-1]:
        return points[:-1]
    return points


def point_in_polygon(p: Tuple[float, float], poly: List[Tuple[float, float]]) -> bool:
    # Ray casting, poly is a ring without duplicated closure.
    x, y = p
    inside = False
    n = len(poly)
    if n < 3:
        return False
    j = n - 1
    for i in range(n):
        xi, yi = poly[i]
        xj, yj = poly[j]
        intersect = ((yi > y) != (yj > y)) and (x < (xj - xi) * (y - yi) / (yj - yi + 1e-30) + xi)
        if intersect:
            inside = not inside
        j = i
    return inside


def parse_clip_polygon_from_bbbike_readme(readme_path: str) -> Optional[List[Tuple[float, float]]]:
    # BBBike README.txt includes a "Script URL" containing coords=lon,lat|lon,lat|...
    if not os.path.isfile(readme_path):
        return None
    with open(readme_path, "r", encoding="utf-8", errors="ignore") as f:
        text = f.read()
    for line in text.splitlines():
        if "Script URL:" in line:
            url = line.split("Script URL:", 1)[1].strip()
            parsed = urllib.parse.urlparse(url)
            qs = urllib.parse.parse_qs(parsed.query)
            coords_val = qs.get("coords", [None])[0]
            if not coords_val:
                return None
            coords_val = urllib.parse.unquote(coords_val)
            pairs = coords_val.split("|")
            poly: List[Tuple[float, float]] = []
            for pair in pairs:
                parts = pair.split(",")
                if len(parts) != 2:
                    continue
                try:
                    lon = float(parts[0])
                    lat = float(parts[1])
                except Exception:
                    continue
                x, y = lonlat_to_webmercator_m(lon, lat)
                poly.append((x, y))
            poly = ring_without_duplicate_closure(poly)
            return poly if len(poly) >= 3 else None
    return None


def pca_obb(points: List[Tuple[float, float]]) -> Optional[List[Tuple[float, float]]]:
    # Returns 4 corners (not necessarily minimal area, but stable and close) or None if degenerate.
    n = len(points)
    if n < 3:
        return None

    mx = sum(p[0] for p in points) / n
    my = sum(p[1] for p in points) / n

    cxx = 0.0
    cxy = 0.0
    cyy = 0.0
    for x, y in points:
        dx = x - mx
        dy = y - my
        cxx += dx * dx
        cxy += dx * dy
        cyy += dy * dy
    cxx /= n
    cxy /= n
    cyy /= n

    # Degenerate: fallback to axis-aligned.
    if (cxx + cyy) < 1e-6:
        return None

    # Eigenvector for the largest eigenvalue of 2x2 covariance matrix.
    trace = cxx + cyy
    det = cxx * cyy - cxy * cxy
    term = max(0.0, (trace * trace) * 0.25 - det)
    sqrt_term = math.sqrt(term)
    eig1 = trace * 0.5 + sqrt_term

    if abs(cxy) > 1e-9:
        ux = eig1 - cyy
        uy = cxy
    else:
        # Covariance is diagonal.
        if cxx >= cyy:
            ux, uy = 1.0, 0.0
        else:
            ux, uy = 0.0, 1.0

    ulen = math.hypot(ux, uy)
    if ulen < 1e-9:
        return None
    ux /= ulen
    uy /= ulen
    vx = -uy
    vy = ux

    min_u = float("inf")
    max_u = float("-inf")
    min_v = float("inf")
    max_v = float("-inf")
    for x, y in points:
        dx = x - mx
        dy = y - my
        pu = dx * ux + dy * uy
        pv = dx * vx + dy * vy
        if pu < min_u:
            min_u = pu
        if pu > max_u:
            max_u = pu
        if pv < min_v:
            min_v = pv
        if pv > max_v:
            max_v = pv

    if (max_u - min_u) < 0.2 or (max_v - min_v) < 0.2:
        return None

    # Corners in (u,v) order: min-min, max-min, max-max, min-max
    corners_uv = [
        (min_u, min_v),
        (max_u, min_v),
        (max_u, max_v),
        (min_u, max_v),
    ]

    corners: List[Tuple[float, float]] = []
    for pu, pv in corners_uv:
        x = mx + ux * pu + vx * pv
        y = my + uy * pu + vy * pv
        corners.append((x, y))
    return corners


def pca_extents(points: List[Tuple[float, float]]) -> Optional[Tuple[float, float]]:
    # Returns (extent_u, extent_v) of principal axes bounding box.
    n = len(points)
    if n < 3:
        return None

    mx = sum(p[0] for p in points) / n
    my = sum(p[1] for p in points) / n

    cxx = 0.0
    cxy = 0.0
    cyy = 0.0
    for x, y in points:
        dx = x - mx
        dy = y - my
        cxx += dx * dx
        cxy += dx * dy
        cyy += dy * dy
    cxx /= n
    cxy /= n
    cyy /= n

    if (cxx + cyy) < 1e-6:
        return None

    trace = cxx + cyy
    det = cxx * cyy - cxy * cxy
    term = max(0.0, (trace * trace) * 0.25 - det)
    sqrt_term = math.sqrt(term)
    eig1 = trace * 0.5 + sqrt_term

    if abs(cxy) > 1e-9:
        ux = eig1 - cyy
        uy = cxy
    else:
        if cxx >= cyy:
            ux, uy = 1.0, 0.0
        else:
            ux, uy = 0.0, 1.0

    ulen = math.hypot(ux, uy)
    if ulen < 1e-9:
        return None
    ux /= ulen
    uy /= ulen
    vx = -uy
    vy = ux

    min_u = float("inf")
    max_u = float("-inf")
    min_v = float("inf")
    max_v = float("-inf")
    for x, y in points:
        dx = x - mx
        dy = y - my
        pu = dx * ux + dy * uy
        pv = dx * vx + dy * vy
        if pu < min_u:
            min_u = pu
        if pu > max_u:
            max_u = pu
        if pv < min_v:
            min_v = pv
        if pv > max_v:
            max_v = pv
    return (max_u - min_u, max_v - min_v)


def write_tile_jsons(tmp_dir: str, tiles_dir: str) -> int:
    # Gather per-tile jsonl fragments.
    tiles: Dict[Tuple[int, int], Dict[str, str]] = {}
    for fn in os.listdir(tmp_dir):
        if not fn.endswith(".jsonl"):
            continue
        # category_tx_ty.jsonl
        parts = fn[:-6].split("_")
        if len(parts) < 3:
            continue
        category = parts[0]
        tx = int(parts[1])
        ty = int(parts[2])
        tiles.setdefault((tx, ty), {})[category] = os.path.join(tmp_dir, fn)

    ensure_dir(tiles_dir)
    written = 0
    for (tx, ty), cat_files in tiles.items():
        payload: Dict[str, Any] = {
            "lod": 0,
            "tx": tx,
            "ty": ty,
            "buildings": [],
            "roads": [],
            "waters": [],
            "waterways": [],
            "landcovers": [],
            "railways": [],
            "pois": [],
        }
        for category, path in cat_files.items():
            key = {
                "buildings": "buildings",
                "roads": "roads",
                "waters": "waters",
                "waterways": "waterways",
                "landcovers": "landcovers",
                "railways": "railways",
                "pois": "pois",
            }.get(category)
            if key is None:
                continue
            with open(path, "r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    payload[key].append(json.loads(line))

        out_path = os.path.join(tiles_dir, f"tile_0_{tx}_{ty}.json")
        with open(out_path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False)
        written += 1
    return written


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--shape-dir", required=True, help="Path to BBBike 'shape' folder (contains *.shp/*.dbf)")
    ap.add_argument("--out", required=True, help="Output OSMSendai root folder (StreamingAssets/OSMSendai)")
    ap.add_argument("--tile-size", type=float, default=1024.0)
    ap.add_argument("--origin-lat", type=float, default=38.2600)
    ap.add_argument("--origin-lon", type=float, default=140.8815)
    ap.add_argument("--clean", action="store_true", help="Delete existing tiles/ and tmp/ before writing")
    ap.add_argument("--keep-tmp", action="store_true", help="Keep intermediate *_jsonl fragments (debugging only)")
    ap.add_argument("--min-building-area", type=float, default=12.0, help="Skip buildings smaller than this area (m^2)")
    ap.add_argument("--max-building-extent", type=float, default=500.0, help="Skip buildings with extent larger than this (m) - filters misclassified infrastructure")
    ap.add_argument("--simplify-meters", type=float, default=0.8, help="Douglas-Peucker tolerance for polygon simplification (meters)")
    ap.add_argument("--rect-buildings", action="store_true", help="Export buildings as rectangles (debug/legacy)")
    ap.add_argument("--clip-readme", default=None, help="Path to BBBike README.txt to clip to the drawn polygon (instead of the bounding rectangle)")
    ap.add_argument("--max-water-area-km2", type=float, default=200.0, help="Skip water polygons larger than this (km^2) to avoid oceans dominating the view")
    ap.add_argument("--max-landcover-area-km2", type=float, default=500.0, help="Skip landcover polygons larger than this (km^2)")
    ap.add_argument("--skip-riverbank-aspect", type=float, default=12.0, help="Skip long-narrow water polygons with aspect ratio above this (likely riverbanks)")
    args = ap.parse_args()

    origin_x, origin_y = lonlat_to_webmercator_m(args.origin_lon, args.origin_lat)
    clip_poly_abs = parse_clip_polygon_from_bbbike_readme(args.clip_readme) if args.clip_readme else None

    out_root = args.out
    tiles_dir = os.path.join(out_root, "tiles")
    tmp_dir = os.path.join(out_root, "_tmp_jsonl")

    if args.clean:
        if os.path.isdir(tiles_dir):
            shutil.rmtree(tiles_dir)
        if os.path.isdir(tmp_dir):
            shutil.rmtree(tmp_dir)

    ensure_dir(out_root)
    ensure_dir(tmp_dir)

    writer = LruAppendWriter(max_open=128)

    def append(category: str, tx: int, ty: int, obj: Dict[str, Any]) -> None:
        path = os.path.join(tmp_dir, f"{category}_{tx}_{ty}.jsonl")
        writer.append_line(path, json.dumps(obj, ensure_ascii=False))

    def keep_ring_by_clip(ring_local: List[Tuple[float, float]]) -> bool:
        if clip_poly_abs is None:
            return True
        # Keep if centroid or any vertex is inside the drawn polygon (in absolute WebMercator meters).
        cx_abs = (sum(p[0] for p in ring_local) / len(ring_local)) + origin_x
        cy_abs = (sum(p[1] for p in ring_local) / len(ring_local)) + origin_y
        if point_in_polygon((cx_abs, cy_abs), clip_poly_abs):
            return True
        for x, y in ring_local[:: max(1, len(ring_local) // 8)]:
            if point_in_polygon((x + origin_x, y + origin_y), clip_poly_abs):
                return True
        return False

    def keep_line_by_clip(points_local: List[Tuple[float, float]]) -> bool:
        if clip_poly_abs is None:
            return True
        # Keep if any point is inside (sample up to 16 points).
        step = max(1, len(points_local) // 16)
        for x, y in points_local[::step]:
            if point_in_polygon((x + origin_x, y + origin_y), clip_poly_abs):
                return True
        return False

    # buildings (Polygon)
    b_shp = os.path.join(args.shape_dir, "buildings.shp")
    b_dbf = os.path.join(args.shape_dir, "buildings.dbf")
    if os.path.isfile(b_shp) and os.path.isfile(b_dbf):
        for shp_rec, dbf_rec in zip(iter_shp_records(b_shp), read_dbf_records(b_dbf)):
            btype = dbf_rec.get("type", "")
            h = building_height_default(btype)
            for part in shp_rec.parts:
                if len(part) < 3:
                    continue
                pts: List[Tuple[float, float]] = []
                for lon, lat in part:
                    x, y = lonlat_to_webmercator_m(lon, lat)
                    pts.append((x - origin_x, y - origin_y))

                # Filter tiny buildings to reduce clutter (sheds etc.)
                if polygon_area(pts) < args.min_building_area:
                    continue

                # Filter abnormally large "buildings" (likely misclassified railways/infrastructure)
                xs = [p[0] for p in pts]
                ys = [p[1] for p in pts]
                extent_x = max(xs) - min(xs)
                extent_y = max(ys) - min(ys)
                max_extent = max(extent_x, extent_y)
                if max_extent > args.max_building_extent:
                    continue

                if args.rect_buildings:
                    corners = pca_obb(pts)
                    if corners is None:
                        xs = [p[0] for p in pts]
                        ys = [p[1] for p in pts]
                        minx, maxx = min(xs), max(xs)
                        miny, maxy = min(ys), max(ys)
                        corners = [(minx, miny), (maxx, miny), (maxx, maxy), (minx, maxy)]
                    cx = sum(p[0] for p in corners) * 0.25
                    cy = sum(p[1] for p in corners) * 0.25
                    tx, ty = tile_xy(cx, cy, args.tile_size)
                    center_x, center_y = tile_center(tx, ty, args.tile_size)
                    vertices = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in corners]
                    append("buildings", tx, ty, {"heightMeters": float(h), "vertices": vertices})
                else:
                    ring = simplify_douglas_peucker(pts, args.simplify_meters)
                    ring = ring_without_duplicate_closure(ring)
                    if len(ring) < 3:
                        continue
                    if not keep_ring_by_clip(ring):
                        continue
                    cx = sum(p[0] for p in ring) / len(ring)
                    cy = sum(p[1] for p in ring) / len(ring)
                    tx, ty = tile_xy(cx, cy, args.tile_size)
                    center_x, center_y = tile_center(tx, ty, args.tile_size)
                    vertices = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in ring]
                    append("buildings", tx, ty, {"heightMeters": float(h), "vertices": vertices})

    # roads (PolyLine) - split by tile boundaries
    r_shp = os.path.join(args.shape_dir, "roads.shp")
    r_dbf = os.path.join(args.shape_dir, "roads.dbf")
    if os.path.isfile(r_shp) and os.path.isfile(r_dbf):
        for shp_rec, dbf_rec in zip(iter_shp_records(r_shp), read_dbf_records(r_dbf)):
            rtype = dbf_rec.get("type", "")
            width = road_width_default(rtype)
            for part in shp_rec.parts:
                if len(part) < 2:
                    continue
                pts_m: List[Tuple[float, float]] = []
                for lon, lat in part:
                    x, y = lonlat_to_webmercator_m(lon, lat)
                    pts_m.append((x - origin_x, y - origin_y))
                if not keep_line_by_clip(pts_m):
                    continue

                # Split road by tile boundaries
                tile_segments = split_polyline_by_tiles(pts_m, args.tile_size)
                for (tx, ty), segments in tile_segments.items():
                    center_x, center_y = tile_center(tx, ty, args.tile_size)
                    for seg in segments:
                        if len(seg) < 2:
                            continue
                        points = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in seg]
                        append("roads", tx, ty, {"widthMeters": float(width), "points": points})

    # natural/landuse polygons -> water/landcover
    for base in ("natural", "landuse"):
        shp_path = os.path.join(args.shape_dir, f"{base}.shp")
        dbf_path = os.path.join(args.shape_dir, f"{base}.dbf")
        if not (os.path.isfile(shp_path) and os.path.isfile(dbf_path)):
            continue
        for shp_rec, dbf_rec in zip(iter_shp_records(shp_path), read_dbf_records(dbf_path)):
            ptype = dbf_rec.get("type", "")
            classified = classify_polygon(ptype)
            if classified is None:
                continue
            target, kind = classified
            for part in shp_rec.parts:
                if len(part) < 3:
                    continue
                pts: List[Tuple[float, float]] = []
                for lon, lat in part:
                    x, y = lonlat_to_webmercator_m(lon, lat)
                    pts.append((x - origin_x, y - origin_y))

                ring = simplify_douglas_peucker(pts, args.simplify_meters)
                ring = ring_without_duplicate_closure(ring)
                if len(ring) < 3:
                    continue
                if not keep_ring_by_clip(ring):
                    continue

                area_m2 = polygon_area(ring)
                if target == "water" and area_m2 > (args.max_water_area_km2 * 1_000_000.0):
                    continue
                if target == "landcover" and area_m2 > (args.max_landcover_area_km2 * 1_000_000.0):
                    continue

                if target == "water":
                    ext = pca_extents(ring)
                    if ext is not None:
                        a, b = ext
                        mn = min(a, b)
                        mx = max(a, b)
                        if mn > 1e-3 and (mx / mn) >= args.skip_riverbank_aspect:
                            # Likely a riverbank area; render rivers from linear waterways instead.
                            continue

                cx = sum(p[0] for p in ring) / len(ring)
                cy = sum(p[1] for p in ring) / len(ring)
                tx, ty = tile_xy(cx, cy, args.tile_size)
                center_x, center_y = tile_center(tx, ty, args.tile_size)
                vertices = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in ring]
                if target == "water":
                    append("waters", tx, ty, {"vertices": vertices})
                else:
                    density = 800.0 if kind == "forest" else 250.0
                    append("landcovers", tx, ty, {"kind": kind, "densityPerKm2": density, "vertices": vertices})

    # waterways (PolyLine) -> linear rivers/streams - split by tile boundaries
    w_shp = os.path.join(args.shape_dir, "waterways.shp")
    w_dbf = os.path.join(args.shape_dir, "waterways.dbf")
    if os.path.isfile(w_shp) and os.path.isfile(w_dbf):
        for shp_rec, dbf_rec in zip(iter_shp_records(w_shp), read_dbf_records(w_dbf)):
            wtype = (dbf_rec.get("type", "") or "stream").lower()
            width_s = (dbf_rec.get("width", "") or "").strip()
            width = None
            if width_s:
                try:
                    width = float(width_s)
                except Exception:
                    width = None
            if width is None or width <= 0:
                width = 10.0 if wtype == "river" else 5.0 if wtype == "canal" else 3.0

            for part in shp_rec.parts:
                if len(part) < 2:
                    continue
                pts_m: List[Tuple[float, float]] = []
                for lon, lat in part:
                    x, y = lonlat_to_webmercator_m(lon, lat)
                    pts_m.append((x - origin_x, y - origin_y))
                if not keep_line_by_clip(pts_m):
                    continue
                pts_m = simplify_douglas_peucker(pts_m, max(0.5, args.simplify_meters))
                pts_m = ring_without_duplicate_closure(pts_m)
                if len(pts_m) < 2:
                    continue

                # Split waterway by tile boundaries
                tile_segments = split_polyline_by_tiles(pts_m, args.tile_size)
                for (tx, ty), segments in tile_segments.items():
                    center_x, center_y = tile_center(tx, ty, args.tile_size)
                    for seg in segments:
                        if len(seg) < 2:
                            continue
                        points = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in seg]
                        append("waterways", tx, ty, {"kind": wtype, "widthMeters": float(width), "points": points})

    # railways (PolyLine) — only type=rail surface tracks
    rw_shp = os.path.join(args.shape_dir, "railways.shp")
    rw_dbf = os.path.join(args.shape_dir, "railways.dbf")
    if os.path.isfile(rw_shp) and os.path.isfile(rw_dbf):
        for shp_rec, dbf_rec in zip(iter_shp_records(rw_shp), read_dbf_records(rw_dbf)):
            rtype = (dbf_rec.get("type", "") or "").lower()
            if rtype != "rail":
                continue
            # Skip unnamed tracks (yard sidings, service tracks) to avoid visual clutter
            rname = (dbf_rec.get("name", "") or "").strip()
            if not rname:
                continue
            for part in shp_rec.parts:
                if len(part) < 2:
                    continue
                pts_m: List[Tuple[float, float]] = []
                for lon, lat in part:
                    x, y = lonlat_to_webmercator_m(lon, lat)
                    pts_m.append((x - origin_x, y - origin_y))
                if not keep_line_by_clip(pts_m):
                    continue
                tile_segments = split_polyline_by_tiles(pts_m, args.tile_size)
                for (tx, ty), segments in tile_segments.items():
                    center_x, center_y = tile_center(tx, ty, args.tile_size)
                    for seg in segments:
                        if len(seg) < 2:
                            continue
                        points = [{"x": float(x - center_x), "y": float(y - center_y)} for (x, y) in seg]
                        append("railways", tx, ty, {"widthMeters": 4.0, "points": points})

    # POIs (Point) — station + subway_entrance from points.shp
    poi_shp = os.path.join(args.shape_dir, "points.shp")
    poi_dbf = os.path.join(args.shape_dir, "points.dbf")
    if os.path.isfile(poi_shp) and os.path.isfile(poi_dbf):
        for (lon, lat), dbf_rec in zip(iter_shp_point_records(poi_shp), read_dbf_records(poi_dbf)):
            ptype = (dbf_rec.get("type", "") or "").lower()
            if ptype not in ("station", "subway_entrance"):
                continue
            x, y = lonlat_to_webmercator_m(lon, lat)
            lx = x - origin_x
            ly = y - origin_y
            if clip_poly_abs is not None and not point_in_polygon((x, y), clip_poly_abs):
                continue
            tx, ty = tile_xy(lx, ly, args.tile_size)
            center_x, center_y = tile_center(tx, ty, args.tile_size)
            name = dbf_rec.get("name", "") or ""
            append("pois", tx, ty, {"type": ptype, "name": name, "position": {"x": float(lx - center_x), "y": float(ly - center_y)}})

    # places (Point) — neighbourhood/quarter/suburb/city for area notifications
    pl_shp = os.path.join(args.shape_dir, "places.shp")
    pl_dbf = os.path.join(args.shape_dir, "places.dbf")
    if os.path.isfile(pl_shp) and os.path.isfile(pl_dbf):
        places_list: List[Dict[str, Any]] = []
        for (lon, lat), dbf_rec in zip(iter_shp_point_records(pl_shp), read_dbf_records(pl_dbf)):
            ptype = (dbf_rec.get("type", "") or "").lower()
            if ptype not in ("neighbourhood", "quarter", "suburb", "city"):
                continue
            name = dbf_rec.get("name", "") or ""
            if not name:
                continue
            x, y = lonlat_to_webmercator_m(lon, lat)
            lx = x - origin_x
            ly = y - origin_y
            places_list.append({"type": ptype, "name": name, "x": float(lx), "y": float(ly)})
        if places_list:
            places_path = os.path.join(out_root, "places.json")
            with open(places_path, "w", encoding="utf-8") as f:
                json.dump(places_list, f, ensure_ascii=False)
            print(f"Wrote {len(places_list)} place entries to: {places_path}")

    writer.close()

    ensure_dir(tiles_dir)
    written = write_tile_jsons(tmp_dir, tiles_dir)

    # tileset.json
    tileset = {
        "projection": "LOCAL_WEBMERCATOR",
        "tileSizeMeters": args.tile_size,
        "dataVersion": 1,
        "demLods": [{"lod": 0, "resolutionMeters": 10}, {"lod": 1, "resolutionMeters": 30}, {"lod": 2, "resolutionMeters": 90}],
        "origin": {"lat": args.origin_lat, "lon": args.origin_lon},
    }
    with open(os.path.join(out_root, "tileset.json"), "w", encoding="utf-8") as f:
        json.dump(tileset, f, ensure_ascii=False, indent=2)

    print(f"Wrote {written} tiles to: {tiles_dir}")
    if args.keep_tmp:
        print(f"Temporary fragments kept at: {tmp_dir}")
    else:
        shutil.rmtree(tmp_dir, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
