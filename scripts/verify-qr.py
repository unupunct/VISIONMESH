#!/usr/bin/env python3
"""Checks that the dashboard's QR encoder produces codes a real decoder can read.

VisionMesh ships its own QR encoder so the dashboard works with no internet access. That makes
verifying it important: a subtly wrong code looks perfectly fine on screen and simply never
scans. This decodes the generated matrices with OpenCV and compares the result to the input.

Note that byte-for-byte equality with another encoder is deliberately NOT the test. The QR
standard lets an encoder pick any of eight mask patterns, and different libraries score them
slightly differently, so two correct encoders routinely disagree on the bits while producing
equally scannable codes. What matters is that the code decodes back to what went in.

Usage:
    node scripts/verify-qr.mjs > qr.json
    python scripts/verify-qr.py qr.json

Requires: opencv-python-headless, numpy
"""
import json
import sys

import cv2
import numpy as np


def render(matrix, quiet=4, scale=8):
    """Renders a 0/1 matrix as a black-on-white image with the required quiet zone."""
    size = matrix.shape[0]
    canvas = np.ones((size + quiet * 2, size + quiet * 2), dtype=np.uint8)
    canvas[quiet:quiet + size, quiet:quiet + size] = 1 - matrix
    return np.kron(canvas * 255, np.ones((scale, scale), dtype=np.uint8))


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    with open(sys.argv[1], encoding='utf-8') as handle:
        samples = json.load(handle)

    detector = cv2.QRCodeDetector()
    failures = 0

    for text, matrix_text in samples.items():
        matrix = np.array([[int(c) for c in row] for row in matrix_text.split('\n')], dtype=np.uint8)
        decoded, _, _ = detector.detectAndDecode(render(matrix))

        label = text if len(text) <= 44 else text[:41] + '...'
        if decoded == text:
            print(f"  ok    {matrix.shape[0]:>3}x{matrix.shape[0]:<3} {label}")
        else:
            failures += 1
            print(f"  FAIL  {matrix.shape[0]:>3}x{matrix.shape[0]:<3} {label}")
            print(f"        decoded as: {decoded!r}")

    print()
    if failures:
        print(f"{failures} QR code(s) did not decode correctly.")
        return 1

    print(f"All {len(samples)} QR codes decoded correctly.")
    return 0


if __name__ == '__main__':
    sys.exit(main())
