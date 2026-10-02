# Generate test PNG assets for M5 (pure stdlib, no third-party deps).
# Usage: python gen_test_images.py <out_dir>
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bmp2png import write_png


def gradient(w, h):
    """Smooth blue-violet gradient (compresses well, ~100KB at 800x533)."""
    rows = []
    for y in range(h):
        row = bytearray()
        for x in range(w):
            r = 90 + (120 * x) // w
            g = 80 + (60 * y) // h
            b = 200 + (40 * (x + y)) // (w + h)
            row += bytes((r, g, b))
        rows.append(bytes(row))
    return rows


def noise(w, h):
    """Deterministic palette noise (compresses poorly, ~1MB at 1300x975)."""
    palette = [(230, 120, 140), (120, 180, 230), (250, 210, 120), (150, 220, 170),
               (200, 160, 230), (240, 240, 235), (90, 90, 110), (60, 140, 160),
               (180, 60, 90), (70, 120, 200), (230, 170, 60), (90, 180, 120),
               (160, 120, 200), (220, 220, 200), (120, 70, 140), (40, 100, 120)]
    seed = 20261002
    rows = []
    for y in range(h):
        row = bytearray()
        for x in range(w):
            seed = (seed * 1103515245 + 12345 + x * 7 + y * 13) & 0x7FFFFFFF
            row += bytes(palette[(seed >> 8) % len(palette)])
        rows.append(bytes(row))
    return rows


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    targets = [
        ("test-img-gradient.png", gradient(800, 533)),
        ("test-img-noise.png", noise(1300, 975)),
    ]
    for name, rows in targets:
        path = os.path.join(out, name)
        write_png(path, len(rows[0]) // 3, len(rows), rows)
        print(f"{name}: {os.path.getsize(path) / 1024:.0f} KB")


if __name__ == "__main__":
    main()
