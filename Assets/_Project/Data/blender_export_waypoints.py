"""
Blender -> NASA logo waypoint CSV exporter.

Run this from Blender's Scripting tab (Text Editor > Run Script) AFTER importing your
NASA-logo SVG (File > Import > Scalable Vector Graphics). It writes the exact CSV the
Unity loader expects: columns  x,y,z,pen,layer

Why this fixes "the vertices don't go all the way around the outline":
  An SVG path imports as a BEZIER curve with only a few control points. Reading those
  control points gives a sparse, gap-toothed outline. This script SAMPLES points ALONG
  every Bezier segment (RES points each), so the outline is complete and smooth.

It also does the two things you had to fix by hand before:
  * pen-up (pen=0) into the first point of every separate contour  -> no line is ever
    drawn across the gaps between letters / shapes,
  * closes every closed contour, so loops (circle, letter outlines) meet cleanly.

Layers (letters draw on top of the circle): put objects in a Blender Collection named
"Letters" (layer 2) or "Red" (layer 1); everything else is layer 0. Optional -- if you
skip it, everything is layer 0. In Blender: select the letter curves, press M, New
Collection, name it "Letters".

Orientation/size are handled in Unity (the loader auto-centers, auto-fits to Target
World Size, and can flip axes), so the raw Blender coordinates here are fine as-is.
"""
import bpy
import math
from mathutils.geometry import interpolate_bezier

# ---------------------------------------------------------------- settings
OUT = "/Users/connor/NASA Logo Simluation/Assets/_Project/Data/nasa_logo_clean.csv"
RES = 14                # points sampled per Bezier segment (raise for smoother/denser)
CLOSE_LOOPS = True      # draw the final segment back to the start of each closed contour
LAYER_BY_COLLECTION = {"Letters": 2, "Red": 1}   # collection name -> render layer
ONLY_SELECTED = False   # True = export only selected curve objects; False = all curves
START_RING_AT_BOTTOM = True   # rotate the largest closed loop (the ring) to start/seam at its lowest point
PARK_CORNER = True            # append a pen-up waypoint so the tractor drives off and parks in a corner
# --------------------------------------------------------------------------


def layer_of(obj):
    for coll in obj.users_collection:
        if coll.name in LAYER_BY_COLLECTION:
            return LAYER_BY_COLLECTION[coll.name]
    return 0


def sample_spline(spline, mw):
    """Return an ordered list of world-space points densely covering the whole contour."""
    pts = []
    if spline.type == 'BEZIER':
        bp = spline.bezier_points
        n = len(bp)
        if n < 2:
            return [mw @ p.co for p in bp]
        segs = n if spline.use_cyclic_u else n - 1
        for i in range(segs):
            a = bp[i]
            b = bp[(i + 1) % n]
            seg = interpolate_bezier(mw @ a.co, mw @ a.handle_right,
                                     mw @ b.handle_left, mw @ b.co, RES)
            pts.extend(seg[:-1])          # drop the endpoint; next segment re-adds it (no dupes)
        if not spline.use_cyclic_u:
            pts.append(mw @ bp[-1].co)    # final knot for open curves
    else:  # POLY / NURBS
        pts = [mw @ p.co.to_3d() for p in spline.points]
    return pts


def split_contours(rows):
    """Index ranges [start, end) of each pen-up-delimited contour in the row list."""
    spans, start = [], None
    for i, r in enumerate(rows):
        if r[2] == 0:                       # pen up marks a new contour start
            if start is not None:
                spans.append((start, i))
            start = i
    if start is not None:
        spans.append((start, len(rows)))
    return spans


def rotate_ring_to_bottom(rows):
    """Rotate the largest CLOSED contour (the outer ring) so it starts/seams at its lowest
    point -- a discreet seam tucked at the bottom instead of out at the side."""
    best = None                             # (span, start, end)
    for start, end in split_contours(rows):
        seg = rows[start:end]
        if len(seg) < 4:
            continue
        closed = abs(seg[0][0] - seg[-1][0]) < 1e-4 and abs(seg[0][1] - seg[-1][1]) < 1e-4
        if not closed:
            continue
        xs = [p[0] for p in seg]
        ys = [p[1] for p in seg]
        span = max(max(xs) - min(xs), max(ys) - min(ys))
        if best is None or span > best[0]:
            best = (span, start, end)
    if best is None:
        return rows
    _, start, end = best
    seg = rows[start:end]
    lyr = seg[0][3]
    loop = seg[:-1]                         # drop the closing duplicate point
    lo = min(range(len(loop)), key=lambda i: loop[i][1])   # index of the lowest point
    loop = loop[lo:] + loop[:lo]
    rebuilt = [(loop[0][0], loop[0][1], 0, lyr)]
    rebuilt += [(p[0], p[1], 1, lyr) for p in loop[1:]]
    rebuilt += [(loop[0][0], loop[0][1], 1, lyr)]          # re-close the loop
    return rows[:start] + rebuilt + rows[end:]


def append_park_corner(rows):
    """Append a pen-up waypoint at the bounding-box corner nearest the final drawn point, so the
    tractor drives off the artwork and parks in a corner. Placed ON the existing bbox corner so
    Unity's auto-fit scale is unchanged (a point far outside would shrink the whole logo)."""
    if not rows:
        return rows
    xs = [p[0] for p in rows]
    ys = [p[1] for p in rows]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    lx, ly = rows[-1][0], rows[-1][1]
    corners = [(x0, y0), (x1, y0), (x0, y1), (x1, y1)]
    cx, cy = min(corners, key=lambda c: math.hypot(c[0] - lx, c[1] - ly))
    return rows + [(cx, cy, 0, 0)]          # pen up, layer 0


def main():
    objs = [o for o in bpy.data.objects if o.type == 'CURVE'
            and (o.select_get() if ONLY_SELECTED else True)]
    # draw order: lower layers first (nice build-up animation), then by name
    objs.sort(key=lambda o: (layer_of(o), o.name))

    rows = []
    contours = 0
    for obj in objs:
        mw = obj.matrix_world
        lyr = layer_of(obj)
        for spline in obj.data.splines:
            pts = sample_spline(spline, mw)
            if len(pts) < 2:
                continue
            contours += 1
            rows.append((pts[0].x, pts[0].y, 0, lyr))          # pen UP into the contour start
            for p in pts[1:]:
                rows.append((p.x, p.y, 1, lyr))                 # pen down: draw
            if CLOSE_LOOPS and spline.use_cyclic_u:
                rows.append((pts[0].x, pts[0].y, 1, lyr))       # close the loop

    if START_RING_AT_BOTTOM:
        rows = rotate_ring_to_bottom(rows)
    if PARK_CORNER:
        rows = append_park_corner(rows)

    with open(OUT, "w") as f:
        f.write("x,y,z,pen,layer\n")
        for x, y, pen, lyr in rows:
            f.write("{:.5f},{:.5f},0,{},{}\n".format(x, y, pen, lyr))

    msg = "{} curve objects, {} contours, {} points".format(len(objs), contours, len(rows))
    print("[export] {} -> {}".format(msg, OUT))
    return msg, len(objs)


def _popup(title, msg, icon):
    def draw(self, ctx):
        for line in msg.split("\n"):
            self.layout.label(text=line)
    bpy.context.window_manager.popup_menu(draw, title=title, icon=icon)


if __name__ == "__main__":
    # Wrap so macOS users get a visible pop-up (the print() console isn't shown unless
    # Blender was launched from a terminal). Success -> info popup; error -> error popup.
    try:
        summary, n = main()
        if n == 0:
            _popup("Export: nothing to do",
                   "No CURVE objects found.\nImport your SVG first (File > Import > SVG).",
                   'ERROR')
        else:
            _popup("Export complete", summary + "\nWrote nasa_logo_clean.csv", 'CHECKMARK')
    except Exception as e:
        _popup("Export FAILED", str(e), 'ERROR')
        raise
