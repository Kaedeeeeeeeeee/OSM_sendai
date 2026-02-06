#!/usr/bin/env python3
from __future__ import annotations

"""
Download GSI (国土地理院) DEM tiles and produce per-tile heightmap binary files
for the OSM Sendai Unity project.

Data source: GSI DEM5A PNG tiles at
  https://cyberjapandata.gsi.go.jp/xyz/dem5a_png/{z}/{x}/{y}.png

Elevation encoding: x = 2^16*R + 2^8*G + B; h = x * 0.01 (meters).
No-data sentinel: RGB(128, 0, 0).

Fallback chain: dem5a_png → dem5b_png → dem10b_png

Output per tile: dem/dem_0_{tx}_{ty}.bin
  [int32 gridW] [int32 gridH] [float32 × gridW*gridH row-major]
  Row 0 = south edge (min Z in tile-local), Col 0 = west edge (min X).

Pure Python (no external dependencies).
"""

import argparse
import math
import os
import struct
import time
import urllib.request
import zlib
from typing import Dict, List, Optional, Tuple

EARTH_RADIUS_M = 6378137.0
EARTH_CIRCUMFERENCE = 2.0 * math.pi * EARTH_RADIUS_M
NO_DATA_SENTINEL = -9999.0

# ─── WebMercator / slippy-map helpers ───────────────────────────────────────

def lonlat_to_webmercator_m(lon: float, lat: float) -> Tuple[float, float]:
    lat = max(min(lat, 85.05112878), -85.05112878)
    x = math.radians(lon) * EARTH_RADIUS_M
    y = math.log(math.tan(math.pi / 4.0 + math.radians(lat) / 2.0)) * EARTH_RADIUS_M
    return x, y


def abs_to_pixel(abs_x: float, abs_y: float, zoom: int) -> Tuple[float, float]:
    """Convert absolute WebMercator metres to pixel coords at given zoom."""
    C = EARTH_CIRCUMFERENCE
    norm_x = (abs_x + C / 2.0) / C          # [0, 1]
    norm_y = 1.0 - (abs_y + C / 2.0) / C    # [0, 1], Y flipped
    scale = 256.0 * (2 ** zoom)
    return norm_x * scale, norm_y * scale


def pixel_to_slippy(px: float, py: float) -> Tuple[int, int]:
    """Pixel coords to slippy-map tile indices."""
    return int(px) // 256, int(py) // 256


# ─── Pure-Python PNG decoder (RGB only, 8-bit) ─────────────────────────────

def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa = abs(p - a)
    pb = abs(p - b)
    pc = abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def decode_png_rgb(data: bytes) -> Tuple[int, int, List[Tuple[int, int, int]]]:
    """
    Decode an RGB PNG image from raw bytes.
    Returns (width, height, pixels) where pixels is a flat list of (R,G,B) tuples,
    row-major from top-left.
    """
    if data[:8] != b'\x89PNG\r\n\x1a\n':
        raise ValueError("Not a valid PNG file")

    pos = 8
    width = 0
    height = 0
    bit_depth = 0
    color_type = 0
    idat_chunks: List[bytes] = []

    while pos < len(data):
        chunk_len = struct.unpack(">I", data[pos:pos + 4])[0]
        chunk_type = data[pos + 4:pos + 8]
        chunk_data = data[pos + 8:pos + 8 + chunk_len]
        # skip CRC (4 bytes)
        pos += 12 + chunk_len

        if chunk_type == b'IHDR':
            width = struct.unpack(">I", chunk_data[0:4])[0]
            height = struct.unpack(">I", chunk_data[4:8])[0]
            bit_depth = chunk_data[8]
            color_type = chunk_data[9]
        elif chunk_type == b'IDAT':
            idat_chunks.append(chunk_data)
        elif chunk_type == b'IEND':
            break

    if width == 0 or height == 0:
        raise ValueError("PNG IHDR not found or invalid")

    raw = zlib.decompress(b''.join(idat_chunks))

    # Determine bytes per pixel based on color type
    if color_type == 2:       # RGB
        bpp = 3 * (bit_depth // 8)
    elif color_type == 6:     # RGBA
        bpp = 4 * (bit_depth // 8)
    elif color_type == 0:     # Grayscale
        bpp = 1 * (bit_depth // 8)
    elif color_type == 4:     # Grayscale + Alpha
        bpp = 2 * (bit_depth // 8)
    else:
        raise ValueError(f"Unsupported PNG color type: {color_type}")

    stride = width * bpp
    pixels: List[Tuple[int, int, int]] = []
    prev_row = bytes(stride)
    raw_pos = 0

    for _y in range(height):
        filter_byte = raw[raw_pos]
        raw_pos += 1
        row_raw = bytearray(raw[raw_pos:raw_pos + stride])
        raw_pos += stride

        # Apply filter
        if filter_byte == 0:
            pass  # None
        elif filter_byte == 1:
            # Sub
            for i in range(bpp, stride):
                row_raw[i] = (row_raw[i] + row_raw[i - bpp]) & 0xFF
        elif filter_byte == 2:
            # Up
            for i in range(stride):
                row_raw[i] = (row_raw[i] + prev_row[i]) & 0xFF
        elif filter_byte == 3:
            # Average
            for i in range(stride):
                a = row_raw[i - bpp] if i >= bpp else 0
                b = prev_row[i]
                row_raw[i] = (row_raw[i] + ((a + b) >> 1)) & 0xFF
        elif filter_byte == 4:
            # Paeth
            for i in range(stride):
                a = row_raw[i - bpp] if i >= bpp else 0
                b = prev_row[i]
                c = prev_row[i - bpp] if i >= bpp else 0
                row_raw[i] = (row_raw[i] + _paeth(a, b, c)) & 0xFF

        prev_row = bytes(row_raw)

        # Extract RGB values
        for x in range(width):
            offset = x * bpp
            r = row_raw[offset]
            g = row_raw[offset + 1] if bpp >= 2 else r
            b_val = row_raw[offset + 2] if bpp >= 3 else r
            pixels.append((r, g, b_val))

    return width, height, pixels


# ─── GSI DEM tile fetching ──────────────────────────────────────────────────

DEM_LAYERS = ["dem5a_png", "dem5b_png", "dem10b_png"]


def gsi_url(layer: str, z: int, x: int, y: int) -> str:
    return f"https://cyberjapandata.gsi.go.jp/xyz/{layer}/{z}/{x}/{y}.png"


def fetch_gsi_tile(
    z: int, x: int, y: int,
    cache_dir: Optional[str],
    rate_limit: float = 0.1,
) -> Optional[List[Tuple[int, int, int]]]:
    """
    Download (or load from cache) a 256x256 GSI DEM tile.
    Tries fallback layers. Returns flat list of 256*256 (R,G,B) tuples, or None.
    """
    for layer in DEM_LAYERS:
        cache_path = None
        if cache_dir:
            cache_path = os.path.join(cache_dir, layer, str(z), str(x), f"{y}.png")

        # Check cache
        if cache_path and os.path.isfile(cache_path):
            try:
                with open(cache_path, "rb") as f:
                    data = f.read()
                if len(data) > 0:
                    _w, _h, pixels = decode_png_rgb(data)
                    return pixels
            except Exception:
                pass  # Cache corrupted, re-download

        # Download
        url = gsi_url(layer, z, x, y)
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "OSMSendai-DEM/1.0"})
            with urllib.request.urlopen(req, timeout=15) as resp:
                data = resp.read()

            # Cache
            if cache_path:
                os.makedirs(os.path.dirname(cache_path), exist_ok=True)
                with open(cache_path, "wb") as f:
                    f.write(data)

            _w, _h, pixels = decode_png_rgb(data)
            if rate_limit > 0:
                time.sleep(rate_limit)
            return pixels
        except Exception:
            if rate_limit > 0:
                time.sleep(rate_limit)
            continue  # Try next layer

    return None


def rgb_to_elevation(r: int, g: int, b: int) -> float:
    """Decode GSI DEM RGB to elevation in metres. Returns NO_DATA_SENTINEL for no-data."""
    if r == 128 and g == 0 and b == 0:
        return NO_DATA_SENTINEL
    x = (r << 16) | (g << 8) | b
    # Values >= 2^23 are negative elevations
    if x >= (1 << 23):
        x -= (1 << 24)
    return x * 0.01


# ─── Heightmap generation ──────────────────────────────────────────────────

def build_heightmap_for_tile(
    tx: int, ty: int,
    tile_size: float,
    origin_x: float, origin_y: float,
    grid_size: int,
    zoom: int,
    cache_dir: Optional[str],
    rate_limit: float,
    gsi_cache: Dict[Tuple[int, int], Optional[List[Tuple[int, int, int]]]],
) -> List[float]:
    """
    Build a grid_size x grid_size heightmap for the given tile.
    Returns row-major floats: row 0 = south edge, col 0 = west edge.
    """
    half = tile_size * 0.5
    # Tile center in origin-relative WebMercator metres
    cx = tx * tile_size + half
    cy = ty * tile_size + half

    heights: List[float] = []

    for row in range(grid_size):
        # row 0 = south (min Z/Y), row grid_size-1 = north (max Z/Y)
        local_z = -half + row * tile_size / (grid_size - 1)
        abs_y = origin_y + cy + local_z

        for col in range(grid_size):
            # col 0 = west (min X), col grid_size-1 = east (max X)
            local_x = -half + col * tile_size / (grid_size - 1)
            abs_x = origin_x + cx + local_x

            # Convert to pixel coords in GSI tile space
            px, py = abs_to_pixel(abs_x, abs_y, zoom)
            stx, sty = pixel_to_slippy(px, py)

            # Fetch GSI tile (cached in memory per run)
            key = (stx, sty)
            if key not in gsi_cache:
                gsi_cache[key] = fetch_gsi_tile(zoom, stx, sty, cache_dir, rate_limit)

            gsi_pixels = gsi_cache[key]
            if gsi_pixels is None:
                heights.append(NO_DATA_SENTINEL)
                continue

            # Bilinear sample within GSI tile
            frac_x = px - stx * 256.0
            frac_y = py - sty * 256.0

            # Clamp to [0, 255]
            frac_x = max(0.0, min(frac_x, 255.0))
            frac_y = max(0.0, min(frac_y, 255.0))

            ix = int(frac_x)
            iy = int(frac_y)
            fx = frac_x - ix
            fy = frac_y - iy
            ix1 = min(ix + 1, 255)
            iy1 = min(iy + 1, 255)

            def elev(px_x: int, px_y: int) -> float:
                r, g, b = gsi_pixels[px_y * 256 + px_x]
                return rgb_to_elevation(r, g, b)

            h00 = elev(ix, iy)
            h10 = elev(ix1, iy)
            h01 = elev(ix, iy1)
            h11 = elev(ix1, iy1)

            # Bilinear, skipping no-data samples
            samples = [(h00, (1 - fx) * (1 - fy)),
                       (h10, fx * (1 - fy)),
                       (h01, (1 - fx) * fy),
                       (h11, fx * fy)]
            valid = [(h, w) for h, w in samples if h != NO_DATA_SENTINEL]
            if valid:
                total_w = sum(w for _, w in valid)
                h = sum(h * w for h, w in valid) / total_w if total_w > 0 else 0.0
            else:
                h = NO_DATA_SENTINEL

            heights.append(h)

    return heights


def fill_nodata(heights: List[float], grid_w: int, grid_h: int) -> None:
    """Fill NO_DATA_SENTINEL values with average of valid neighbours, or 0."""
    for iteration in range(3):
        changed = False
        for row in range(grid_h):
            for col in range(grid_w):
                idx = row * grid_w + col
                if heights[idx] != NO_DATA_SENTINEL:
                    continue
                total = 0.0
                count = 0
                for dr in (-1, 0, 1):
                    for dc in (-1, 0, 1):
                        if dr == 0 and dc == 0:
                            continue
                        nr, nc = row + dr, col + dc
                        if 0 <= nr < grid_h and 0 <= nc < grid_w:
                            nv = heights[nr * grid_w + nc]
                            if nv != NO_DATA_SENTINEL:
                                total += nv
                                count += 1
                if count > 0:
                    heights[idx] = total / count
                    changed = True
        if not changed:
            break

    # Final pass: set any remaining no-data to 0
    for i in range(len(heights)):
        if heights[i] == NO_DATA_SENTINEL:
            heights[i] = 0.0


def write_heightmap_bin(path: str, grid_w: int, grid_h: int, heights: List[float]) -> None:
    """Write binary heightmap: [int32 gridW] [int32 gridH] [float32 × N]."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(struct.pack("<ii", grid_w, grid_h))
        for h in heights:
            f.write(struct.pack("<f", h))


# ─── Main ──────────────────────────────────────────────────────────────────

def discover_tiles(tiles_dir: str) -> List[Tuple[int, int]]:
    """Scan tiles/ directory for existing tile_0_{tx}_{ty}.json files."""
    tiles: List[Tuple[int, int]] = []
    if not os.path.isdir(tiles_dir):
        return tiles
    for fn in os.listdir(tiles_dir):
        if not fn.startswith("tile_0_") or not fn.endswith(".json"):
            continue
        parts = fn[:-5].split("_")  # tile_0_tx_ty
        if len(parts) != 4:
            continue
        try:
            tx = int(parts[2])
            ty = int(parts[3])
            tiles.append((tx, ty))
        except ValueError:
            continue
    tiles.sort()
    return tiles


def main() -> int:
    ap = argparse.ArgumentParser(
        description="Download GSI DEM tiles and produce per-tile heightmap binaries."
    )
    ap.add_argument("--out", required=True,
                    help="OSMSendai root folder (StreamingAssets/OSMSendai)")
    ap.add_argument("--origin-lat", type=float, default=38.2600)
    ap.add_argument("--origin-lon", type=float, default=140.8815)
    ap.add_argument("--tile-size", type=float, default=1024.0)
    ap.add_argument("--grid-size", type=int, default=33,
                    help="Heightmap grid resolution per tile (default 33 = 32+1)")
    ap.add_argument("--zoom", type=int, default=15,
                    help="GSI slippy-map zoom level (default 15)")
    ap.add_argument("--cache-dir", default=".dem_cache",
                    help="Directory to cache downloaded GSI PNGs")
    ap.add_argument("--rate-limit", type=float, default=0.1,
                    help="Seconds to wait between HTTP requests (default 0.1)")
    ap.add_argument("--clean", action="store_true",
                    help="Delete existing dem/ directory before writing")
    args = ap.parse_args()

    origin_x, origin_y = lonlat_to_webmercator_m(args.origin_lon, args.origin_lat)

    tiles_dir = os.path.join(args.out, "tiles")
    dem_dir = os.path.join(args.out, "dem")

    tiles = discover_tiles(tiles_dir)
    if not tiles:
        print(f"No tiles found in {tiles_dir}. Run shp_to_tiles.py or geojson_to_tiles.py first.")
        return 1

    print(f"Found {len(tiles)} tiles. Grid size: {args.grid_size}x{args.grid_size}, zoom: {args.zoom}")

    if args.clean and os.path.isdir(dem_dir):
        import shutil
        shutil.rmtree(dem_dir)

    os.makedirs(dem_dir, exist_ok=True)

    gsi_cache: Dict[Tuple[int, int], Optional[List[Tuple[int, int, int]]]] = {}
    written = 0
    total = len(tiles)

    for i, (tx, ty) in enumerate(tiles):
        out_path = os.path.join(dem_dir, f"dem_0_{tx}_{ty}.bin")

        print(f"  [{i + 1}/{total}] tile ({tx},{ty}) ...", end=" ", flush=True)

        heights = build_heightmap_for_tile(
            tx, ty,
            args.tile_size,
            origin_x, origin_y,
            args.grid_size,
            args.zoom,
            args.cache_dir,
            args.rate_limit,
            gsi_cache,
        )
        fill_nodata(heights, args.grid_size, args.grid_size)
        write_heightmap_bin(out_path, args.grid_size, args.grid_size, heights)

        h_min = min(heights)
        h_max = max(heights)
        h_avg = sum(heights) / len(heights)
        print(f"elev [{h_min:.1f} .. {h_max:.1f}] avg={h_avg:.1f}m")
        written += 1

    print(f"\nWrote {written} heightmap files to: {dem_dir}")
    print(f"GSI tiles cached: {len(gsi_cache)} (in {args.cache_dir})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
