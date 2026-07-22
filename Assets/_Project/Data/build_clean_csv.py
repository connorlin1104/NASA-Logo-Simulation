#!/usr/bin/env python3
"""
Build nasa_logo_clean.csv from the user's raw Maya export nasa_logo_waypoints.csv.

Raw format:  Object_Name,X,Y,Z   (col0 = curve name "Curve"/"Curve.001"/...; X,Z = ground plane; Y ~ 0)
Clean format: x,y,z,pen,layer
    x     = raw X            -> Unity X   (loader columnX=0, mapColumnXTo=X)
    y     = raw Z            -> Unity Z   (loader columnY=1, mapColumnYTo=Z)
    z     = 0                -> flattened (loader columnZ=2, mapColumnZTo=NegY, flattenToGround)
    pen   = 0 lift / 1 draw  (loader penColumn=3)   -- auto-used because it's binary
    layer = 0/1/2 render height (loader layerColumn=4) -- letters highest so nothing crosses them

Key logic (fixes the "draws across / doesn't lift the pen" problem):
  Each Maya curve is a COMPOUND path: one or more sub-contours joined by long "move" hops.
  We split every curve into sub-contours wherever a segment is abnormally long
  (> max(ABS_JUMP, K x median-segment-of-that-curve)), then:
    * the first point of each sub-contour is pen=0 (lift into it) -- no chord is ever drawn across a hop,
    * a near-closed sub-contour gets a closing segment back to its start (clean closed outline),
    * degenerate 1-2 point sub-contours (stray move markers) are dropped.
"""
import csv, math
from collections import OrderedDict

SRC = "nasa_logo_waypoints.csv"
DST = "nasa_logo_clean.csv"

ABS_JUMP = 0.12     # a segment longer than this (and > K*median) starts a new sub-contour
K_MEDIAN = 3.0      # relative floor: scales the jump test to each curve's own point density
CLOSE_GAP = 0.06    # a sub-contour whose start/end are closer than this is closed with a final segment
MIN_SUB = 3         # sub-contours smaller than this are stray move-markers -> dropped

# Render layers (height order). Letters highest so the circle/orbit/red never cross them out.
LETTERS = {"Curve.043", "Curve.044", "Curve.045", "Curve.046"}
RED     = {"Curve.042"}
def layer_for(name):
    if name in LETTERS: return 2
    if name in RED:     return 1
    return 0            # orbit (Curve), synthesized circle, stars, everything else

# Curve.041 in the export is 3 broken petal-loops with its right side missing (not a usable circle).
# Per the user's pick ("clean circle ring"), drop it and synthesize a proper closed circle around the
# letters (inside the orbit), and close the outer orbit ellipse gap into a full loop.
DROP_CURVES     = {"Curve.041"}
FORCE_CLOSE     = {"Curve"}      # the orbit ellipse -> seal its ~0.12 gap
CIRCLE_MARGIN   = 1.08           # circle radius = (farthest letter/red point from center) * this
CIRCLE_SEGMENTS = 72             # smooth ring


def seg(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def median(v):
    s = sorted(v)
    n = len(s)
    return s[n // 2] if n % 2 else 0.5 * (s[n // 2 - 1] + s[n // 2])


def split_subcontours(pts):
    if len(pts) < 2:
        return [pts] if pts else []
    segs = [seg(pts[i - 1], pts[i]) for i in range(1, len(pts))]
    thr = max(ABS_JUMP, K_MEDIAN * median(segs))
    subs, cur = [], [pts[0]]
    for i in range(1, len(pts)):
        if segs[i - 1] > thr:
            subs.append(cur)
            cur = [pts[i]]
        else:
            cur.append(pts[i])
    subs.append(cur)
    return subs


def main():
    groups = OrderedDict()
    with open(SRC) as f:
        r = csv.reader(f)
        next(r)
        for line in r:
            if len(line) < 4:
                continue
            try:
                x = float(line[1]); z = float(line[3])
            except ValueError:
                continue
            groups.setdefault(line[0], []).append((x, z))

    out = []              # rows: (x, z, 0, pen, layer)
    lifts = closed = dropped = 0

    def emit(sub, lyr, force_close=False):
        nonlocal lifts, closed
        # lift into the first point of every sub-contour (no chord across the hop / between curves)
        out.append((sub[0][0], sub[0][1], 0.0, 0, lyr)); lifts += 1
        for p in sub[1:]:
            out.append((p[0], p[1], 0.0, 1, lyr))
        g = seg(sub[0], sub[-1])
        if (force_close or g < CLOSE_GAP) and g > 1e-6:
            out.append((sub[0][0], sub[0][1], 0.0, 1, lyr)); closed += 1  # draw the closing segment

    # Synthesized clean circle around the letters (replaces the broken Curve.041 petals).
    core = [p for n in (LETTERS | RED) for p in groups.get(n, [])]
    ccx = 0.5 * (min(p[0] for p in core) + max(p[0] for p in core))
    ccz = 0.5 * (min(p[1] for p in core) + max(p[1] for p in core))
    Rc = max(math.hypot(p[0] - ccx, p[1] - ccz) for p in core) * CIRCLE_MARGIN
    ring = [(ccx + Rc * math.cos(2 * math.pi * i / CIRCLE_SEGMENTS),
             ccz + Rc * math.sin(2 * math.pi * i / CIRCLE_SEGMENTS)) for i in range(CIRCLE_SEGMENTS)]
    ring.append(ring[0])   # closing point -> full loop

    for name, pts in groups.items():
        if name in DROP_CURVES:
            dropped += 1
            continue
        lyr = layer_for(name)
        force = name in FORCE_CLOSE
        for sub in split_subcontours(pts):
            if len(sub) < MIN_SUB:
                dropped += 1
                continue
            emit(sub, lyr, force_close=force)
        if name in FORCE_CLOSE:            # right after the orbit, lay the synthesized circle (base layer)
            emit(ring, 0)

    with open(DST, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["x", "y", "z", "pen", "layer"])
        for x, z, zz, pen, lyr in out:
            w.writerow([f"{x:.4f}", f"{z:.4f}", 0, pen, lyr])

    drawn_jumps = sum(
        1 for k in range(1, len(out))
        if out[k][3] == 1 and seg((out[k - 1][0], out[k - 1][1]), (out[k][0], out[k][1])) > ABS_JUMP
        and out[k][4] == out[k - 1][4]
    )
    print(f"wrote {DST}: {len(out)} points")
    print(f"  sub-contour lifts (pen=0): {lifts}")
    print(f"  contours closed:           {closed}")
    print(f"  degenerate subs dropped:   {dropped}")
    print(f"  layer counts: 0={sum(1 for r in out if r[4]==0)} 1={sum(1 for r in out if r[4]==1)} 2={sum(1 for r in out if r[4]==2)}")
    print(f"  drawn segments still > {ABS_JUMP} within a layer (should be ~0): {drawn_jumps}")


if __name__ == "__main__":
    main()
