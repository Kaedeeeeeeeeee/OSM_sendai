#!/usr/bin/env python3
"""
Generate a top-down PNG map image from tile JSON data.

Pure Python — no external dependencies (uses stdlib zlib for PNG compression).
Writes a 24-bit PNG and a companion map_metadata.json that maps pixel
coordinates to world metres.

Usage:
    python3 tools/generate_map_image.py \
        --tiles-dir "My project/Assets/StreamingAssets/OSMSendai/tiles" \
        --out "My project/Assets/StreamingAssets/OSMSendai" \
        --pixels-per-tile 64 --tile-size 1024
"""

from __future__ import annotations

import argparse
import json
import math
import os
import struct
import zlib
from typing import Any, Dict, List, Tuple


def write_png(path: str, width: int, height: int, pixels: bytearray) -> None:
    """Write a 24-bit RGB PNG.  `pixels` is RGB top-down (row 0 = top)."""

    def _chunk(chunk_type: bytes, data: bytes) -> bytes:
        c = chunk_type + data
        crc = struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)
        return struct.pack(">I", len(data)) + c + crc

    # Build raw image data with filter byte 0 (None) per row.
    raw = bytearray()
    for y in range(height):
        raw.append(0)  # filter type: None
        row_start = y * width * 3
        raw.extend(pixels[row_start : row_start + width * 3])

    compressed = zlib.compress(bytes(raw), 9)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")  # PNG signature
        f.write(_chunk(b"IHDR", ihdr))
        f.write(_chunk(b"IDAT", compressed))
        f.write(_chunk(b"IEND", b""))


def set_pixel(pixels: bytearray, width: int, x: int, y: int, r: int, g: int, b: int) -> None:
    """Set a pixel.  RGB order, y=0 is the top row."""
    if 0 <= x < width:
        idx = (y * width + x) * 3
        pixels[idx] = r
        pixels[idx + 1] = g
        pixels[idx + 2] = b


def draw_line(pixels: bytearray, width: int, height: int,
              x0: int, y0: int, x1: int, y1: int,
              r: int, g: int, b: int) -> None:
    """Bresenham line drawing."""
    dx = abs(x1 - x0)
    dy = abs(y1 - y0)
    sx = 1 if x0 < x1 else -1
    sy = 1 if y0 < y1 else -1
    err = dx - dy
    while True:
        if 0 <= x0 < width and 0 <= y0 < height:
            set_pixel(pixels, width, x0, y0, r, g, b)
        if x0 == x1 and y0 == y1:
            break
        e2 = 2 * err
        if e2 > -dy:
            err -= dy
            x0 += sx
        if e2 < dx:
            err += dx
            y0 += sy


def draw_thick_line(pixels: bytearray, width: int, height: int,
                    x0: int, y0: int, x1: int, y1: int,
                    r: int, g: int, b: int, thickness: int) -> None:
    """Draw a line with the given thickness by drawing multiple parallel lines."""
    if thickness <= 1:
        draw_line(pixels, width, height, x0, y0, x1, y1, r, g, b)
        return
    half = thickness // 2
    dx = x1 - x0
    dy = y1 - y0
    length = math.sqrt(dx * dx + dy * dy)
    if length < 0.001:
        return
    # Normal vector perpendicular to line direction
    nx = -dy / length
    ny = dx / length
    for offset in range(-half, half + 1):
        ox = round(nx * offset)
        oy = round(ny * offset)
        draw_line(pixels, width, height,
                  x0 + ox, y0 + oy, x1 + ox, y1 + oy, r, g, b)


def fill_rect(pixels: bytearray, width: int, height: int,
              cx: int, cy: int, radius: int,
              r: int, g: int, b: int) -> None:
    """Fill a small square of side 2*radius+1 centered at (cx, cy)."""
    for dy in range(-radius, radius + 1):
        for dx in range(-radius, radius + 1):
            px, py = cx + dx, cy + dy
            if 0 <= px < width and 0 <= py < height:
                set_pixel(pixels, width, px, py, r, g, b)


def fill_scanline(pixels: bytearray, width: int, height: int,
                  points: List[Tuple[int, int]], r: int, g: int, b: int) -> None:
    """Simple scanline polygon fill."""
    if len(points) < 3:
        return
    min_y = max(0, min(p[1] for p in points))
    max_y = min(height - 1, max(p[1] for p in points))
    n = len(points)
    for y in range(min_y, max_y + 1):
        intersections: List[int] = []
        for i in range(n):
            j = (i + 1) % n
            yi, yj = points[i][1], points[j][1]
            if yi == yj:
                continue
            if yi > yj:
                yi, yj = yj, yi
                xi, xj = points[j][0], points[i][0]
            else:
                xi, xj = points[i][0], points[j][0]
            if yi <= y < yj:
                x = xi + (y - yi) * (xj - xi) // (yj - yi)
                intersections.append(x)
        intersections.sort()
        for k in range(0, len(intersections) - 1, 2):
            xa = max(0, intersections[k])
            xb = min(width - 1, intersections[k + 1])
            for x in range(xa, xb + 1):
                set_pixel(pixels, width, x, y, r, g, b)


def main() -> int:
    ap = argparse.ArgumentParser(description="Generate top-down PNG map from tile JSON data.")
    ap.add_argument("--tiles-dir", required=True, help="Path to tiles/ directory")
    ap.add_argument("--out", required=True, help="Output directory for PNG + metadata")
    ap.add_argument("--pixels-per-tile", type=int, default=64)
    ap.add_argument("--tile-size", type=float, default=1024.0)
    args = ap.parse_args()

    ppt = args.pixels_per_tile
    tile_size = args.tile_size
    scale = max(1, ppt // 64)  # feature thickness scales with resolution

    # Scan tiles to find bounding box
    tile_files: List[Tuple[int, int, str]] = []
    for fn in os.listdir(args.tiles_dir):
        if not fn.startswith("tile_0_") or not fn.endswith(".json"):
            continue
        parts = fn[:-5].split("_")  # tile_0_tx_ty
        if len(parts) < 4:
            continue
        tx = int(parts[2])
        ty = int(parts[3])
        tile_files.append((tx, ty, os.path.join(args.tiles_dir, fn)))

    if not tile_files:
        print("No tile files found!")
        return 1

    min_tx = min(t[0] for t in tile_files)
    max_tx = max(t[0] for t in tile_files)
    min_ty = min(t[1] for t in tile_files)
    max_ty = max(t[1] for t in tile_files)

    tiles_w = max_tx - min_tx + 1
    tiles_h = max_ty - min_ty + 1
    img_w = tiles_w * ppt
    img_h = tiles_h * ppt

    print(f"Tile range: tx=[{min_tx},{max_tx}] ty=[{min_ty},{max_ty}]")
    print(f"Image size: {img_w} x {img_h} pixels")

    # PNG is top-down; tile ty increases northward (up), so we flip:
    # pixel row 0 = max_ty (north), pixel row max = min_ty (south)

    # Background: light beige (RGB)
    pixels = bytearray(img_w * img_h * 3)
    bg_r, bg_g, bg_b = 225, 220, 210
    for i in range(img_w * img_h):
        pixels[i * 3] = bg_r
        pixels[i * 3 + 1] = bg_g
        pixels[i * 3 + 2] = bg_b

    def world_to_pixel(wx: float, wz: float) -> Tuple[int, int]:
        """Convert world-space (origin-relative) coords to pixel coords (top-down)."""
        px = int((wx - min_tx * tile_size) / tile_size * ppt)
        # Flip Y: world-Z increases northward, pixel-Y increases downward
        py = img_h - 1 - int((wz - min_ty * tile_size) / tile_size * ppt)
        return px, py

    # Render each tile
    for tx, ty, path in tile_files:
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
        except Exception:
            continue

        # Tile center in world coords
        cx = tx * tile_size + tile_size * 0.5
        cy = ty * tile_size + tile_size * 0.5

        # Landcover polygons — green fill (grass/forest/park)
        for lc in data.get("landcovers", []):
            verts = lc.get("vertices", [])
            if len(verts) < 3:
                continue
            pts = []
            for v in verts:
                px, py = world_to_pixel(cx + v["x"], cy + v["y"])
                pts.append((px, py))

            kind = str(lc.get("kind", "grass")).lower()
            if kind == "forest":
                fill_scanline(pixels, img_w, img_h, pts, 118, 150, 96)
            else:
                # grass / park / meadow and unknown kinds default here
                fill_scanline(pixels, img_w, img_h, pts, 150, 182, 120)

        # Water polygons — blue fill
        for w in data.get("waters", []):
            verts = w.get("vertices", [])
            if len(verts) < 3:
                continue
            pts = []
            for v in verts:
                px, py = world_to_pixel(cx + v["x"], cy + v["y"])
                pts.append((px, py))
            fill_scanline(pixels, img_w, img_h, pts, 100, 140, 210)

        # Roads — dark gray lines
        for road in data.get("roads", []):
            points = road.get("points", [])
            if len(points) < 2:
                continue
            for i in range(len(points) - 1):
                x0, y0 = world_to_pixel(cx + points[i]["x"], cy + points[i]["y"])
                x1, y1 = world_to_pixel(cx + points[i + 1]["x"], cy + points[i + 1]["y"])
                draw_thick_line(pixels, img_w, img_h, x0, y0, x1, y1, 90, 90, 95, scale)

        # Railways — brown lines
        for rw in data.get("railways", []):
            points = rw.get("points", [])
            if len(points) < 2:
                continue
            for i in range(len(points) - 1):
                x0, y0 = world_to_pixel(cx + points[i]["x"], cy + points[i]["y"])
                x1, y1 = world_to_pixel(cx + points[i + 1]["x"], cy + points[i + 1]["y"])
                draw_thick_line(pixels, img_w, img_h, x0, y0, x1, y1, 140, 115, 90, scale)

        # Buildings — light gray dots at centroids
        bld_radius = max(1, scale)
        for bld in data.get("buildings", []):
            verts = bld.get("vertices", [])
            if not verts:
                continue
            bx = sum(v["x"] for v in verts) / len(verts)
            by = sum(v["y"] for v in verts) / len(verts)
            px, py = world_to_pixel(cx + bx, cy + by)
            if 0 <= px < img_w and 0 <= py < img_h:
                fill_rect(pixels, img_w, img_h, px, py, bld_radius, 180, 180, 185)

    # Write PNG
    os.makedirs(args.out, exist_ok=True)
    png_path = os.path.join(args.out, "map_overview.png")
    write_png(png_path, img_w, img_h, pixels)
    print(f"Wrote map image: {png_path}")

    # Write metadata
    world_min_x = min_tx * tile_size
    world_min_z = min_ty * tile_size
    world_max_x = (max_tx + 1) * tile_size
    world_max_z = (max_ty + 1) * tile_size

    metadata = {
        "imageWidth": img_w,
        "imageHeight": img_h,
        "worldMinX": world_min_x,
        "worldMinZ": world_min_z,
        "worldMaxX": world_max_x,
        "worldMaxZ": world_max_z,
    }
    meta_path = os.path.join(args.out, "map_metadata.json")
    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(metadata, f, indent=2)
    print(f"Wrote map metadata: {meta_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
