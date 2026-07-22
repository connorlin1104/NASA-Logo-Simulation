#!/usr/bin/env python3
"""
Convert the raw Maya curve export (file.csv) into a clean waypoint CSV for the tractor.

Assembles the NASA "meatball" from the raw export (comma-separated, trailing tabs, no headers):

  * outline (circle)      -> the round field's edge
  * Orbit                 -> a squeezed elliptical orbit (true PCA axes, thinned + scaled to clear circle)
  * Top + bottom (vector) -> the red "vector" swoosh (zig-zag surface strips -> smooth band outlines)
  * N, A1, S, A2          -> the NASA letters (glyph outlines; the A's have an inner counter)

Each contour is closed + densified so long edges aren't mistaken for pen-up gaps; the mower lifts
between contours. Columns are (X, mayaZ, 0) so the loader's DEFAULT axis mapping lays the logo flat.

ORIENTATION: FLIP_Z negates the vertical axis (fixes the upside-down view); use FLIP_X if it comes out
mirrored instead. Re-run after re-exporting: python3 convert_maya_to_csv.py
"""
import math
import os

SRC = os.path.join(os.path.dirname(__file__), "file.csv")
OUT = os.path.join(os.path.dirname(__file__), "nasa_logo.csv")
DENSIFY_STEP = 0.06

FLIP_X = False
FLIP_Z = True

# Orbit ellipse shape. SCALE shrinks it overall (keeps it clear of the circle); SQUEEZE (<1) thins the
# minor axis so it reads as a squeezed ellipse rather than a round oval.
ORBIT_SCALE = 0.85
ORBIT_SQUEEZE = 0.80

# name, startRow, endRow, mode ("layer_run"|"strip"), splits
ELEMENTS = [
    ("outline", 2119, 2215, "layer_run", None),          # circle
    ("Top",      931, 1327, "strip",     None),          # red vector, upper band
    ("bottom",  1327, 1737, "strip",     None),          # red vector, lower band
    ("N",       2215, 2425, "layer_run", [(0, 42)]),
    ("A1",      2425, 2695, "layer_run", [(0, 38), (38, 54)]),
    ("S",       2695, 3215, "layer_run", [(0, 104)]),
    ("A2",      3215, 3485, "layer_run", [(0, 38), (38, 54)]),
]
ORBIT_ROWS = (0, 931)


def parse(path):
    rows = []
    with open(path, errors="replace") as f:
        for ln in f:
            s = ln.strip().strip("\t ,")
            if not s:
                continue
            p = [t.strip() for t in s.split(",")]
            try:
                rows.append((float(p[0]), float(p[1]), float(p[2])))
            except (ValueError, IndexError):
                pass
    return rows


def one_layer(seg, mode):
    y0 = round(seg[0][1], 3)
    if mode == "strip":
        return [r for r in seg if round(r[1], 3) == y0]
    run = 0
    for r in seg:
        if round(r[1], 3) == y0:
            run += 1
        else:
            break
    return seg[:run]


def strip_outline(pts):
    """A surface strip alternates between the two band edges; even/odd -> the two edges."""
    return pts[0::2] + pts[1::2][::-1]


def densify_closed(pts, step):
    if len(pts) < 2:
        return list(pts)
    loop = list(pts) + [pts[0]]
    out = []
    for i in range(len(loop) - 1):
        (x0, z0), (x1, z1) = loop[i], loop[i + 1]
        d = math.hypot(x1 - x0, z1 - z0)
        n = max(1, int(math.ceil(d / step)))
        for k in range(n):
            t = k / n
            out.append((x0 + (x1 - x0) * t, z0 + (z1 - z0) * t))
    out.append(loop[-1])
    return out


def orbit_ellipse(pts, scale, squeeze, n=160):
    """Clean ellipse using the orbit's TRUE principal axes (real tilt/eccentricity), shrunk by `scale`
    to clear the circle and thinned by `squeeze` on the minor axis."""
    cx = sum(p[0] for p in pts) / len(pts)
    cz = sum(p[1] for p in pts) / len(pts)
    a = b = c = 0.0
    for x, z in pts:
        dx, dz = x - cx, z - cz
        a += dx * dx; b += dx * dz; c += dz * dz
    tr = a + c
    disc = math.sqrt(max(0.0, (tr / 2) ** 2 - (a * c - b * b)))
    v1 = (tr / 2 + disc - c, b) if abs(b) > 1e-9 else ((1.0, 0.0) if a >= c else (0.0, 1.0))
    nrm = math.hypot(*v1); v1 = (v1[0] / nrm, v1[1] / nrm); v2 = (-v1[1], v1[0])
    s1 = max(abs((x - cx) * v1[0] + (z - cz) * v1[1]) for x, z in pts) * scale            # major
    s2 = max(abs((x - cx) * v2[0] + (z - cz) * v2[1]) for x, z in pts) * scale * squeeze  # minor
    return [(cx + math.cos(t) * v1[0] * s1 + math.sin(t) * v2[0] * s2,
             cz + math.cos(t) * v1[1] * s1 + math.sin(t) * v2[1] * s2)
            for t in (2 * math.pi * i / n for i in range(n))]


def main():
    rows = parse(SRC)
    contours = []

    for name, a, b, mode, splits in ELEMENTS:
        layer = one_layer(rows[a:b], mode)
        xz = [(r[0], r[2]) for r in layer]
        if mode == "strip":
            contours.append(densify_closed(strip_outline(xz), DENSIFY_STEP))
        else:
            for (s, e) in (splits if splits else [(0, len(xz))]):
                contours.append(densify_closed(xz[s:e], DENSIFY_STEP))

    orbit_xz = [(r[0], r[2]) for r in rows[ORBIT_ROWS[0]:ORBIT_ROWS[1]]]
    contours.append(densify_closed(orbit_ellipse(orbit_xz, ORBIT_SCALE, ORBIT_SQUEEZE), DENSIFY_STEP))

    def flip(pt):
        x, z = pt
        return (-x if FLIP_X else x, -z if FLIP_Z else z)
    contours = [[flip(p) for p in c] for c in contours]

    with open(OUT, "w") as f:
        f.write("x,y,z\n")
        for c in contours:
            for (x, z) in c:
                f.write(f"{x:.4f},{z:.4f},0\n")

    npts = sum(len(c) for c in contours)
    print(f"Wrote {npts} points in {len(contours)} contours -> {OUT}")


if __name__ == "__main__":
    main()
