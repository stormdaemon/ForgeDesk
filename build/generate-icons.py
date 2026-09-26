#!/usr/bin/env python3
"""Generates the ForgeDesk app icon and logo bitmaps from one vector definition.

The mark is an ember anvil struck by a spark, on a dark steel rounded square. The same
path data drives the bitmaps written here and the `ForgeLogoMark` DrawingImage in
src/ForgeDesk.App/Themes/Brand.xaml (print it with --xaml after changing a shape).

Usage:
    python build/generate-icons.py            # writes the .ico and logo PNGs
    python build/generate-icons.py --xaml     # prints the DrawingImage XAML instead

Requires Pillow.
"""

from __future__ import annotations

import argparse
import re
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

# --- Geometry (256 x 256 design grid) ---------------------------------------------------

DESIGN = 256.0
TILE_INSET = 8.0
TILE_RADIUS = 52.0

ANVIL = (
    "M 40 128 Q 60 116 92 114 L 212 114 L 212 142 Q 188 146 172 152 Q 160 158 158 172 "
    "L 158 180 Q 182 184 196 194 L 200 210 L 72 210 L 76 194 Q 90 184 114 180 L 114 172 "
    "Q 112 158 96 150 Q 70 140 40 128 Z"
)
ANVIL_TOP, ANVIL_BOTTOM = 114.0, 210.0
# Bright edge along the anvil face, where the hammer strikes.
ANVIL_FACE = "M 92 114 L 212 114 L 212 122 L 88 122 Q 70 124 52 128 Q 64 118 92 114 Z"
SPARK = "M 150 32 Q 154 66 184 70 Q 154 74 150 108 Q 146 74 116 70 Q 146 66 150 32 Z"
SPARK_SMALL = "M 196 36 Q 197.5 46.5 208 48 Q 197.5 49.5 196 60 Q 194.5 49.5 184 48 Q 194.5 46.5 196 36 Z"
SPARK_TOP, SPARK_BOTTOM = 32.0, 108.0
EMBER_DOT = (106.0, 58.0, 5.0)  # cx, cy, r

# --- Palette ----------------------------------------------------------------------------

STEEL_TOP = (0x45, 0x4D, 0x5C)
STEEL_BOTTOM = (0x1D, 0x21, 0x2A)
STEEL_EDGE = (255, 255, 255, 34)
EMBER_LIGHT = (0xFF, 0x9A, 0x57)
EMBER = (0xF2, 0x76, 0x2E)
EMBER_DARK = (0xC9, 0x55, 0x1A)
FACE_HIGHLIGHT = (0xFF, 0xC4, 0x94)
SPARK_CORE = (0xFF, 0xE2, 0xB8)
SPARK_EDGE = (0xFF, 0x9A, 0x57)

ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
LOGO_SIZES = [64, 256]
SUPERSAMPLE = 8

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "ForgeDesk.App" / "Assets"


# --- Path flattening --------------------------------------------------------------------

def parse_path(data: str) -> list[list[tuple[float, float]]]:
    """Flattens an absolute M/L/Q/C/Z path into polygons (one per subpath)."""
    tokens = re.findall(r"[MLQCZ]|-?\d+(?:\.\d+)?", data)
    polygons: list[list[tuple[float, float]]] = []
    current: list[tuple[float, float]] = []
    index = 0
    command = ""

    def number() -> float:
        nonlocal index
        value = float(tokens[index])
        index += 1
        return value

    while index < len(tokens):
        token = tokens[index]
        if token.isalpha():
            command = token
            index += 1
            if command == "Z":
                if current:
                    polygons.append(current)
                current = []
                continue
        if command == "M":
            if current:
                polygons.append(current)
            current = [(number(), number())]
            command = "L"
        elif command == "L":
            current.append((number(), number()))
        elif command == "Q":
            start = current[-1]
            control = (number(), number())
            end = (number(), number())
            for step in range(1, 17):
                t = step / 16
                u = 1 - t
                current.append((
                    u * u * start[0] + 2 * u * t * control[0] + t * t * end[0],
                    u * u * start[1] + 2 * u * t * control[1] + t * t * end[1],
                ))
        elif command == "C":
            start = current[-1]
            c1 = (number(), number())
            c2 = (number(), number())
            end = (number(), number())
            for step in range(1, 21):
                t = step / 20
                u = 1 - t
                current.append((
                    u ** 3 * start[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t ** 3 * end[0],
                    u ** 3 * start[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t ** 3 * end[1],
                ))
        else:
            raise ValueError(f"Unsupported path command near token {index}: {token}")
    if current:
        polygons.append(current)
    return polygons


# --- Rendering --------------------------------------------------------------------------

def vertical_gradient(size: int, top: tuple[int, ...], bottom: tuple[int, ...], y0: float = 0.0, y1: float | None = None) -> Image.Image:
    """RGBA image filled with a vertical gradient between rows y0 and y1."""
    y1 = size if y1 is None else y1
    column = Image.new("RGBA", (1, size))
    pixels = column.load()
    for y in range(size):
        t = min(1.0, max(0.0, (y - y0) / max(1.0, y1 - y0)))
        pixels[0, y] = tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + ((255,) if len(top) == 3 else ())
    return column.resize((size, size))


def shape_mask(size: int, scale: float, path: str) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(mask)
    for polygon in parse_path(path):
        draw.polygon([(x * scale, y * scale) for x, y in polygon], fill=255)
    return mask


def fill(canvas: Image.Image, mask: Image.Image, paint: Image.Image) -> None:
    canvas.alpha_composite(Image.composite(paint, Image.new("RGBA", canvas.size, (0, 0, 0, 0)), mask))


def render_mark(pixel_size: int) -> Image.Image:
    """Renders the icon at pixel_size using supersampling for clean anti-aliasing."""
    detailed = pixel_size >= 32
    size = pixel_size * SUPERSAMPLE
    scale = size / DESIGN
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # Steel tile with a faint lit edge.
    tile = Image.new("L", (size, size), 0)
    inset = TILE_INSET * scale
    radius = TILE_RADIUS * scale
    ImageDraw.Draw(tile).rounded_rectangle((inset, inset, size - inset, size - inset), radius=radius, fill=255)
    fill(canvas, tile, vertical_gradient(size, STEEL_TOP, STEEL_BOTTOM, inset, size - inset))
    edge = Image.new("L", (size, size), 0)
    ImageDraw.Draw(edge).rounded_rectangle(
        (inset, inset, size - inset, size - inset), radius=radius, outline=255, width=max(1, round(2 * scale)))
    edge_paint = Image.new("RGBA", (size, size), STEEL_EDGE)
    fill(canvas, ImageChops.multiply(edge, tile), edge_paint)

    anvil = shape_mask(size, scale, ANVIL)
    if detailed:
        # Soft contact shadow grounds the anvil on the tile.
        shadow = anvil.filter(ImageFilter.GaussianBlur(6 * scale))
        shadow = ImageChops.offset(shadow, 0, round(5 * scale))
        shadow_paint = Image.new("RGBA", (size, size), (0, 0, 0, 110))
        fill(canvas, ImageChops.multiply(shadow, tile), shadow_paint)

    fill(canvas, anvil, vertical_gradient(size, EMBER_LIGHT, EMBER_DARK, ANVIL_TOP * scale, ANVIL_BOTTOM * scale))
    if detailed:
        fill(canvas, shape_mask(size, scale, ANVIL_FACE), Image.new("RGBA", (size, size), FACE_HIGHLIGHT + (230,)))

    spark_paint = vertical_gradient(size, SPARK_CORE, SPARK_EDGE, SPARK_TOP * scale, SPARK_BOTTOM * scale)
    if detailed:
        glow = shape_mask(size, scale, SPARK).filter(ImageFilter.GaussianBlur(10 * scale))
        fill(canvas, ImageChops.multiply(glow, tile), Image.new("RGBA", (size, size), EMBER + (120,)))
    fill(canvas, shape_mask(size, scale, SPARK), spark_paint)

    if detailed:
        fill(canvas, shape_mask(size, scale, SPARK_SMALL), spark_paint)
        dot = Image.new("L", (size, size), 0)
        cx, cy, r = (v * scale for v in EMBER_DOT)
        ImageDraw.Draw(dot).ellipse((cx - r, cy - r, cx + r, cy + r), fill=255)
        fill(canvas, dot, Image.new("RGBA", (size, size), EMBER_LIGHT + (255,)))

    return canvas.resize((pixel_size, pixel_size), Image.Resampling.LANCZOS)


def write_assets() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    frames = [render_mark(size) for size in ICO_SIZES]
    largest = frames[-1]
    ico_path = ASSETS / "ForgeDesk.ico"
    largest.save(ico_path, format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=frames[:-1])
    print(f"wrote {ico_path.relative_to(ROOT)} ({', '.join(str(s) for s in ICO_SIZES)})")

    for size in LOGO_SIZES:
        logo_path = ASSETS / f"ForgeDeskLogo-{size}.png"
        render_mark(size).save(logo_path, format="PNG", optimize=True)
        print(f"wrote {logo_path.relative_to(ROOT)}")


# --- XAML -------------------------------------------------------------------------------

def hex_color(rgb: tuple[int, ...]) -> str:
    return "#" + "".join(f"{c:02X}" for c in rgb)


def print_xaml() -> None:
    inset = TILE_INSET
    extent = DESIGN - 2 * inset
    cx, cy, r = EMBER_DOT
    print(f"""<DrawingImage x:Key="ForgeLogoMark">
  <DrawingImage.Drawing>
    <DrawingGroup>
      <GeometryDrawing Geometry="M0,0 H{DESIGN:g} V{DESIGN:g} H0 Z" Brush="Transparent" />
      <GeometryDrawing>
        <GeometryDrawing.Geometry>
          <RectangleGeometry Rect="{inset:g},{inset:g},{extent:g},{extent:g}" RadiusX="{TILE_RADIUS:g}" RadiusY="{TILE_RADIUS:g}" />
        </GeometryDrawing.Geometry>
        <GeometryDrawing.Brush>
          <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
            <GradientStop Offset="0" Color="{hex_color(STEEL_TOP)}" />
            <GradientStop Offset="1" Color="{hex_color(STEEL_BOTTOM)}" />
          </LinearGradientBrush>
        </GeometryDrawing.Brush>
      </GeometryDrawing>
      <GeometryDrawing Geometry="{ANVIL}">
        <GeometryDrawing.Brush>
          <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
            <GradientStop Offset="0" Color="{hex_color(EMBER_LIGHT)}" />
            <GradientStop Offset="1" Color="{hex_color(EMBER_DARK)}" />
          </LinearGradientBrush>
        </GeometryDrawing.Brush>
      </GeometryDrawing>
      <GeometryDrawing Geometry="{ANVIL_FACE}" Brush="#E6{hex_color(FACE_HIGHLIGHT)[1:]}" />
      <GeometryDrawing Geometry="{SPARK} {SPARK_SMALL}">
        <GeometryDrawing.Brush>
          <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
            <GradientStop Offset="0" Color="{hex_color(SPARK_CORE)}" />
            <GradientStop Offset="1" Color="{hex_color(SPARK_EDGE)}" />
          </LinearGradientBrush>
        </GeometryDrawing.Brush>
      </GeometryDrawing>
      <GeometryDrawing Brush="{hex_color(EMBER_LIGHT)}">
        <GeometryDrawing.Geometry>
          <EllipseGeometry Center="{cx:g},{cy:g}" RadiusX="{r:g}" RadiusY="{r:g}" />
        </GeometryDrawing.Geometry>
      </GeometryDrawing>
    </DrawingGroup>
  </DrawingImage.Drawing>
</DrawingImage>""")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--xaml", action="store_true", help="print the ForgeLogoMark DrawingImage instead of writing files")
    args = parser.parse_args()
    if args.xaml:
        print_xaml()
    else:
        write_assets()


if __name__ == "__main__":
    main()
