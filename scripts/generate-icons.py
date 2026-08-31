#!/usr/bin/env python3
"""Generates the VisionMesh icon set.

The mark is a camera lens sitting inside a mesh of connected nodes: the camera says what this is,
the mesh says that many of them work as one system.

Design constraints that drove the shape:
  * It has to survive 16x16. That rules out aperture blades, gradients inside the lens, and any
    text. What reads at that size is a thick high-contrast ring with a solid centre.
  * The mesh lines are drawn behind the lens and fade toward the edges, so at small sizes they
    blur into a soft halo rather than turning into noise.
  * Two accents only: blue for the lens, green for the mesh, on dark graphite. More colours look
    muddy once the icon is scaled into a taskbar.

Everything is drawn at 1024 and downsampled with a high-quality filter, which produces cleaner
small sizes than drawing each size directly.

Usage:
    python scripts/generate-icons.py

Requires: Pillow
"""
import math
import os

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ASSETS = os.path.join(ROOT, 'assets', 'branding')
WEB_IMG = os.path.join(ROOT, 'web', 'dashboard', 'img')

MASTER = 1024
SIZES = [16, 32, 48, 64, 128, 256, 512]
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

BACKGROUND_OUTER = (13, 17, 23)     # --bg
BACKGROUND_INNER = (26, 34, 46)
LENS_ACCENT = (47, 157, 227)        # --accent
LENS_GLASS = (10, 18, 28)
LENS_HIGHLIGHT = (150, 214, 255)
MESH = (53, 208, 127)               # --live


def rounded_background(size):
    """Dark graphite tile with a soft radial lift toward the centre."""
    image = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    radius = int(size * 0.22)
    draw.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=BACKGROUND_OUTER + (255,))

    # A radial glow, built by stacking translucent circles rather than by generating a gradient
    # image, which keeps the edges of the rounded rectangle crisp.
    glow = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    centre = size / 2
    for step in range(28, 0, -1):
        r = size * 0.52 * (step / 28)
        alpha = int(4 + (28 - step) * 1.6)
        glow_draw.ellipse([centre - r, centre - r, centre + r, centre + r], fill=BACKGROUND_INNER + (alpha,))

    glow = glow.filter(ImageFilter.GaussianBlur(size * 0.03))

    mask = Image.new('L', (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    image.paste(Image.alpha_composite(image, glow), (0, 0), mask)
    return image


def draw_mesh(image, size):
    """Nodes around the lens, joined to each other and to the centre."""
    layer = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    centre = size / 2
    orbit = size * 0.398
    node_radius = size * 0.040
    line_width = max(1, int(size * 0.013))

    # Six nodes, rotated so none sits directly on the vertical axis where it would collide with
    # the lens highlight.
    nodes = []
    for i in range(6):
        angle = math.radians(-90 + 30 + i * 60)
        nodes.append((centre + orbit * math.cos(angle), centre + orbit * math.sin(angle)))

    # Ring connections first, so nodes sit on top of the lines.
    for i, node in enumerate(nodes):
        nxt = nodes[(i + 1) % len(nodes)]
        draw.line([node, nxt], fill=MESH + (86,), width=line_width)

    # Spokes into the centre, which is what makes it read as a mesh rather than a hexagon.
    for node in nodes:
        draw.line([node, (centre, centre)], fill=MESH + (52,), width=line_width)

    for node in nodes:
        draw.ellipse(
            [node[0] - node_radius, node[1] - node_radius, node[0] + node_radius, node[1] + node_radius],
            fill=MESH + (255,))

    return Image.alpha_composite(image, layer)


def draw_lens(image, size):
    """The camera lens: a thick accent ring, dark glass, and one highlight."""
    layer = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    centre = size / 2
    outer = size * 0.30
    ring_width = size * 0.075

    # A dark disc under the ring hides the mesh spokes where they meet the lens, so the lens
    # stays a clean shape instead of having lines apparently entering it. It is kept just inside
    # the node orbit, otherwise it clips the nodes and they read as broken crescents.
    body = outer + ring_width * 0.30
    draw.ellipse([centre - body, centre - body, centre + body, centre + body], fill=BACKGROUND_OUTER + (255,))

    draw.ellipse(
        [centre - outer, centre - outer, centre + outer, centre + outer],
        outline=LENS_ACCENT + (255,), width=int(ring_width))

    glass = outer - ring_width * 0.75
    draw.ellipse([centre - glass, centre - glass, centre + glass, centre + glass], fill=LENS_GLASS + (255,))

    # Inner ring, faint: gives the lens depth at large sizes and disappears cleanly at small ones.
    inner = glass * 0.62
    draw.ellipse(
        [centre - inner, centre - inner, centre + inner, centre + inner],
        outline=LENS_ACCENT + (110,), width=max(1, int(size * 0.012)))

    # Specular highlight, offset up and left the way a real lens catches light.
    highlight_r = glass * 0.26
    hx = centre - glass * 0.34
    hy = centre - glass * 0.34
    highlight = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(highlight).ellipse(
        [hx - highlight_r, hy - highlight_r, hx + highlight_r, hy + highlight_r],
        fill=LENS_HIGHLIGHT + (150,))
    highlight = highlight.filter(ImageFilter.GaussianBlur(size * 0.012))

    layer = Image.alpha_composite(layer, highlight)
    return Image.alpha_composite(image, layer)


def build_master():
    image = rounded_background(MASTER)
    image = draw_mesh(image, MASTER)
    image = draw_lens(image, MASTER)
    return image


def main():
    os.makedirs(ASSETS, exist_ok=True)
    os.makedirs(WEB_IMG, exist_ok=True)

    master = build_master()
    master.save(os.path.join(ASSETS, 'visionmesh-1024.png'))
    print('visionmesh-1024.png')

    for size in SIZES:
        resized = master.resize((size, size), Image.LANCZOS)

        # Small sizes lose their bite after a downsample; a light sharpen restores the ring edge.
        if size <= 48:
            resized = resized.filter(ImageFilter.UnsharpMask(radius=0.6, percent=110, threshold=0))

        name = f'visionmesh-{size}.png'
        resized.save(os.path.join(ASSETS, name))
        resized.save(os.path.join(WEB_IMG, name))
        print(name)

    ico_path = os.path.join(ASSETS, 'visionmesh.ico')
    master.save(ico_path, format='ICO', sizes=[(s, s) for s in ICO_SIZES])
    print(f'visionmesh.ico ({", ".join(str(s) for s in ICO_SIZES)})')

    # A wide mark for the README and release pages.
    banner_width, banner_height = 1280, 320
    banner = Image.new('RGBA', (banner_width, banner_height), BACKGROUND_OUTER + (255,))
    logo = master.resize((224, 224), Image.LANCZOS)
    banner.paste(logo, (72, (banner_height - 224) // 2), logo)
    banner.save(os.path.join(ASSETS, 'banner-base.png'))
    print('banner-base.png')

    print(f'\nWritten to {ASSETS}\n           {WEB_IMG}')


if __name__ == '__main__':
    main()
