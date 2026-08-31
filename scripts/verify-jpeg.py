#!/usr/bin/env python3
"""Decodes the fallback JPEG encoder's output with an independent image library.

VisionMesh writes its own JPEG encoder for the Linux agent, so cameras that cannot produce MJPEG
still work without pulling in a native imaging dependency. The unit tests check that encoder
against VisionMesh's own decoder; this checks it against libjpeg, which is what every browser,
phone and video player actually uses.

Usage:
    dotnet test tests/VisionMesh.Tests --filter FullyQualifiedName~EmitJpegSamples
    python scripts/verify-jpeg.py [sample-directory]

Requires: Pillow
"""
import os
import sys
import tempfile

from PIL import Image


def check(path, expected_width, expected_height, kind):
    with Image.open(path) as image:
        image.load()
        width, height = image.size
        if (width, height) != (expected_width, expected_height):
            return f"size is {width}x{height}, expected {expected_width}x{expected_height}"

        pixels = image.convert('RGB')

        if kind == 'flat128':
            colours = pixels.getcolors(maxcolors=1 << 20) or []
            spread = max(max(c) - min(c) for _, c in colours)
            if spread > 12:
                return f"a flat frame decoded with a colour spread of {spread}"
            red, green, blue = pixels.getpixel((width // 2, height // 2))
            if not all(112 <= channel <= 144 for channel in (red, green, blue)):
                return f"flat grey decoded as {(red, green, blue)}"

        elif kind == 'gradient':
            # Red should rise left to right, green top to bottom.
            left = pixels.getpixel((8, height // 2))
            right = pixels.getpixel((width - 9, height // 2))
            top = pixels.getpixel((width // 2, 8))
            bottom = pixels.getpixel((width // 2, height - 9))
            if right[0] - left[0] < 150:
                return f"the horizontal red ramp is wrong: left {left[0]}, right {right[0]}"
            if bottom[1] - top[1] < 150:
                return f"the vertical green ramp is wrong: top {top[1]}, bottom {bottom[1]}"

        elif kind.startswith('rgb'):
            wanted = tuple(int(part) for part in kind[3:].split('-'))
            for x, y in [(2, 2), (width - 3, 2), (2, height - 3), (width - 3, height - 3), (width // 2, height // 2)]:
                got = pixels.getpixel((x, y))
                if any(abs(a - b) > 20 for a, b in zip(got, wanted)):
                    return f"pixel at {(x, y)} decoded as {got}, expected about {wanted}"

    return None


def main():
    directory = sys.argv[1] if len(sys.argv) > 1 else os.path.join(tempfile.gettempdir(), 'visionmesh-jpeg-samples')
    manifest = os.path.join(directory, 'expected.txt')

    if not os.path.exists(manifest):
        print(f"No samples found in {directory}.")
        print("Run: dotnet test tests/VisionMesh.Tests --filter FullyQualifiedName~EmitJpegSamples")
        return 2

    failures = 0
    with open(manifest, encoding='utf-8') as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            name, width, height, kind = line.split()
            problem = check(os.path.join(directory, name), int(width), int(height), kind)
            if problem:
                failures += 1
                print(f"  FAIL  {name}: {problem}")
            else:
                size = os.path.getsize(os.path.join(directory, name))
                print(f"  ok    {name} ({size} bytes, {width}x{height})")

    print()
    if failures:
        print(f"{failures} sample(s) did not decode correctly.")
        return 1

    print("Every sample decoded correctly with an independent JPEG decoder.")
    return 0


if __name__ == '__main__':
    sys.exit(main())
