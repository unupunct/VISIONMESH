#!/usr/bin/env python3
"""Regenerates the JPEG fixtures used by the decoder tests.

The fixtures are produced by a real encoder (Pillow / libjpeg) rather than hand-written
byte arrays, because the point of the tests is to prove the decoder agrees with encoders
that actually exist in the wild. Run this from the repository root:

    python tests/fixtures/generate.py

Requires Pillow. The generated files are committed so that the test suite does not need
Python available on CI.
"""
import os
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
WIDTH, HEIGHT = 320, 240


def save(name, img, **kw):
    path = os.path.join(HERE, name)
    img.save(path, "JPEG", **kw)
    print(f"{name}: {os.path.getsize(path)} bytes")


def main():
    # A two-axis gradient exercises real DC variation in both directions.
    gradient = Image.new("RGB", (WIDTH, HEIGHT))
    gradient.putdata([
        (x * 255 // (WIDTH - 1), y * 255 // (HEIGHT - 1), 128)
        for y in range(HEIGHT)
        for x in range(WIDTH)
    ])

    save("gradient_420.jpg", gradient, quality=85)                              # 4:2:0, the common case
    save("gradient_444.jpg", gradient, quality=95, subsampling=0)               # 1x1 luma sampling
    save("gradient_restart.jpg", gradient, quality=85, restart_marker_blocks=4) # DC predictor resets
    save("flat_gray.jpg", Image.new("RGB", (160, 120), (128, 128, 128)), quality=90)
    save("odd_size.jpg", gradient.crop((0, 0, 151, 113)), quality=85)           # MCU padding
    save("progressive.jpg", gradient, quality=85, progressive=True)             # must be refused
    save("grayscale.jpg", gradient.convert("L"), quality=85)                    # single component

    # A pair that differs only in one rectangle: the motion detection case.
    base = Image.new("RGB", (WIDTH, HEIGHT), (60, 60, 60))
    save("motion_a.jpg", base, quality=85)
    moved = base.copy()
    for y in range(20, 80):
        for x in range(20, 100):
            moved.putpixel((x, y), (220, 220, 220))
    save("motion_b.jpg", moved, quality=85)

    # Ground truth for the accuracy assertions: a true 8x8 box downscale of the luma plane.
    reference = gradient.convert("L").resize((WIDTH // 8, HEIGHT // 8), Image.BOX)
    with open(os.path.join(HERE, "gradient_luma_ref.raw"), "wb") as handle:
        handle.write(bytes(reference.tobytes()))
    print(f"gradient_luma_ref.raw: {reference.size[0]}x{reference.size[1]}")


if __name__ == "__main__":
    main()
