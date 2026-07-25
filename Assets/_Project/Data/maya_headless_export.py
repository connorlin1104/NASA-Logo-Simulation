"""Headless Maya -> Unity export for the biodome / greenhouse.

Runs in `mayapy` with no GUI, so Maya never tessellates Paint Effects for a
viewport. That is the whole point: `newGreenHouse.mb` crashes the interactive
Maya on "unhide all" because the hidden `plants` group holds ~4,900 live Paint
Effects strokes, not because the polygon count is high (it is only ~280k tris).

Three jobs, run with --mode:

  structure   the real polygons (dome, tunnel, planting beds) -> one FBX,
              renamed with the COL_ / NOCOL_ prefixes that
              `Tools > NASA Sim > Biodome > Wire Up Selected Model` reads.
  plants      one Paint Effects stroke per plant type, converted to polygons
              and decimated -> one FBX of low-poly prototypes.
  scatter     every stroke's world position/rotation/scale -> CSV, so Unity can
              re-scatter the prototypes instead of importing 4,900 plants.
  all         all three.

Usage:

  /Applications/Autodesk/maya2027/Maya.app/Contents/bin/mayapy \\
      maya_headless_export.py --scene ~/Downloads/newGreenHouse.mb --mode all

Outputs land in Assets/_Project/Models/ (FBX) and Assets/_Project/Data/ (CSV).
"""

import argparse
import collections
import csv
import os
import re
import sys

import maya.standalone

maya.standalone.initialize(name="python")

import maya.cmds as cmds  # noqa: E402  (must follow standalone.initialize)
from maya import mel  # noqa: E402


# Top-level groups to export as solid geometry, and the collider prefix the
# Unity wire-up tool should give them. Edit this when the scene changes.
PREFIX_RULES = [
    ("Dome", "NOCOL_"),              # glass shell - walk through it
    ("tunnel", "COL_"),              # solid, walkable
    ("Otherside_Plants4", "NOCOL_"),  # planting beds - walk past them
]

# Never export these: already in the Unity scene, or not geometry at all.
SKIP_GROUPS = {"Astronaut", "tractor", "plants", "others",
               "persp", "top", "front", "side"}

# Decimate each converted plant prototype down to roughly this many triangles.
# There are ~4,900 instances, so this number times 4,900 is what the scene
# costs if every plant is drawn at once.
PROTOTYPE_TRI_BUDGET = 600

# Paint Effects brushes here are authored for film: 78 lengthwise segments and
# 14 sections around each tube. Coarsening the brush *before* converting gives
# a clean low-poly plant that still has the right silhouette - far better than
# converting at full density and decimating the result, which shatters the
# disconnected leaf shells these brushes produce.
BRUSH_LIMITS = {
    "segments": 8,
    "tubeSections": 4,
    "leafSegments": 3,
    "petalSegments": 3,
    "flowerSegments": 3,
    "subSegments": 1,
}

# Refuse to convert a stroke that explodes past this, rather than run the
# machine out of memory the way the GUI does.
CONVERSION_TRI_CEILING = 400000


def log(msg):
    print(msg, flush=True)


def open_scene(path):
    log("Opening %s (headless, no viewport)" % path)
    cmds.file(path, open=True, force=True, ignoreVersion=True, prompt=False)
    # Bring references in so their nodes can be renamed and exported.
    for ref in cmds.file(query=True, reference=True) or []:
        try:
            cmds.file(ref, importReference=True)
        except Exception as exc:
            log("  could not import reference %s: %s" % (ref, exc))
    log("Opened.")


def strip_namespaces():
    """Flatten `ns:name` to `name` - colons are illegal in Unity object names."""
    for ns in reversed(sorted(cmds.namespaceInfo(listOnlyNamespaces=True,
                                                 recurse=True) or [])):
        if ns in ("UI", "shared"):
            continue
        try:
            cmds.namespace(removeNamespace=ns, mergeNamespaceWithRoot=True)
        except Exception:
            pass


def setup_fbx():
    cmds.loadPlugin("fbxmaya", quiet=True)
    mel.eval("FBXResetExport")
    mel.eval("FBXExportFileVersion -v FBX202000")
    mel.eval("FBXExportInAscii -v false")
    mel.eval("FBXExportSmoothingGroups -v true")
    mel.eval("FBXExportTangents -v true")
    mel.eval("FBXExportEmbeddedTextures -v true")
    mel.eval("FBXExportInputConnections -v false")
    mel.eval("FBXExportUpAxis y")
    mel.eval("FBXExportTriangulate -v true")


def export_selection(out_path):
    setup_fbx()
    safe = out_path.replace("\\", "/").replace('"', '\\"')
    mel.eval('FBXExport -f "%s" -s' % safe)
    size = os.path.getsize(out_path) / 1024.0 / 1024.0
    log("  wrote %s (%.1f MB)" % (out_path, size))


def tri_count(nodes):
    total = 0
    for shape in cmds.ls(nodes, dag=True, type="mesh",
                         long=True, noIntermediate=True) or []:
        try:
            total += cmds.polyEvaluate(shape, t=True)
        except Exception:
            pass
    return total


# --------------------------------------------------------------------------
# structure
# --------------------------------------------------------------------------

def build_plant_markers():
    """An empty transform per stroke, named `PLANT_<Type>_<n>`.

    These ride inside the same FBX as the dome, so Unity's importer applies the
    identical axis conversion to markers and geometry and the plants cannot
    land mirrored or offset relative to the building. The Unity side just
    parents the matching prototype under each marker and inherits its
    position, yaw and scale for free.
    """
    strokes = cmds.ls(type="stroke", long=True) or []
    if not strokes:
        return None

    group = cmds.group(empty=True, name="PlantPoints")
    for index, shape in enumerate(strokes):
        parents = cmds.listRelatives(shape, parent=True, fullPath=True)
        if not parents:
            continue
        matrix = cmds.xform(parents[0], query=True, worldSpace=True,
                            matrix=True)
        marker = cmds.group(empty=True, parent=group,
                            name="PLANT_%s_%04d" % (plant_type(shape), index))
        cmds.xform(marker, worldSpace=True, matrix=matrix)
        try:
            # Paint Effects sizes the plant with globalScale, not the transform.
            scale = cmds.getAttr(shape + ".globalScale")
            cmds.xform(marker, relative=True, scale=[scale] * 3)
        except Exception:
            pass

    log("  %d plant markers" % len(cmds.listRelatives(group,
                                                      children=True) or []))
    return group


def export_structure(out_dir, rules=None, out_name="Biodome.fbx"):
    strip_namespaces()
    exported = []
    for group, prefix in (rules or PREFIX_RULES):
        matches = [n for n in cmds.ls(group, long=True, assemblies=True) or []]
        if not matches:
            log("  group '%s' not found - skipping" % group)
            continue
        node = matches[0]
        # Prefix the group and every mesh transform inside it, so the Unity
        # wire-up tool can tell solid from pass-through.
        meshes = cmds.ls(node, dag=True, type="mesh",
                         long=True, noIntermediate=True) or []
        for shape in meshes:
            xform = cmds.listRelatives(shape, parent=True, fullPath=True)[0]
            short = xform.split("|")[-1]
            if not short.startswith(("COL_", "NOCOL_", "STAIR_", "BALCONY_")):
                try:
                    cmds.rename(xform, prefix + short)
                except Exception:
                    pass
        node = cmds.ls(group, long=True, assemblies=True)[0]
        try:
            node = cmds.rename(node, prefix + group)
        except Exception:
            pass
        exported.append(node)
        log("  %-22s %7d tris  -> %s" % (group, tri_count(node), prefix + group))

    markers = build_plant_markers()
    if markers:
        exported.append(markers)

    if not exported:
        log("  nothing to export")
        return

    cmds.select(exported, replace=True)
    log("  total %d tris" % tri_count(exported))
    export_selection(os.path.join(out_dir, out_name))


# --------------------------------------------------------------------------
# plants
# --------------------------------------------------------------------------

def plant_type(stroke_transform):
    """`strokeFloweringPea123` -> `FloweringPea`."""
    name = stroke_transform.split("|")[-1]
    name = re.sub(r"^stroke(Shape)?", "", name)
    return re.sub(r"\d+$", "", name) or "Plant"


def one_stroke_per_type():
    picked = {}
    for shape in cmds.ls(type="stroke", long=True) or []:
        xform = cmds.listRelatives(shape, parent=True, fullPath=True)
        if not xform:
            continue
        picked.setdefault(plant_type(shape), xform[0])
    return picked


def coarsen_brush(stroke):
    """Drop a stroke's brush to game-asset density before it is tessellated.

    Each stroke owns its own brush node here, and the scene is never saved, so
    editing in place is safe.
    """
    shapes = cmds.listRelatives(stroke, shapes=True, fullPath=True) or []
    for shape in shapes:
        for brush in cmds.listConnections(shape + ".brush", source=True,
                                          destination=False) or []:
            for attr, limit in BRUSH_LIMITS.items():
                plug = "%s.%s" % (brush, attr)
                try:
                    if cmds.getAttr(plug) > limit:
                        cmds.setAttr(plug, limit)
                except Exception:
                    pass


def stroke_copy(stroke, ratio):
    """A fresh, independent stroke ready to tessellate.

    Converting consumes a stroke - it cannot be converted twice - so each
    attempt works on its own duplicate. `upstreamNodes` gives the copy its own
    brush, otherwise thinning one copy would alter every other stroke sharing
    that brush.
    """
    copy = cmds.duplicate(stroke, upstreamNodes=True,
                          returnRootsOnly=True)[0]
    copy = cmds.ls(copy, long=True)[0]

    # Normalise to a "unit" plant. Each stroke carries its own globalScale and
    # transform scale, and the scatter CSV records those per instance, so the
    # prototype has to be built with them at 1 or instances get scaled twice.
    try:
        cmds.setAttr(copy + ".scale", 1, 1, 1, type="double3")
    except Exception:
        pass
    for shape in cmds.listRelatives(copy, shapes=True, fullPath=True) or []:
        try:
            cmds.setAttr(shape + ".globalScale", 1.0)
        except Exception:
            pass

    coarsen_brush(copy)
    if ratio < 1.0:
        thin_tubes(copy, ratio)
    return copy


def convert_within_budget(stroke, name):
    """Convert a stroke, thinning its tubes until it fits the tri budget.

    Decimating afterwards does not work on these plants: one stroke paints a
    whole row of carrots as hundreds of separate tube shells, and polyReduce
    cannot take a shell below one triangle, so an aggressive percentage deletes
    entire plants instead of simplifying them. Painting fewer tubes in the
    first place keeps every plant intact and just makes the row less dense.
    """
    ratio = 1.0
    first = 0
    best, best_raw = [], 0

    for attempt in range(4):
        copy = stroke_copy(stroke, ratio)
        cmds.select(copy, replace=True)
        before = set(cmds.ls(assemblies=True, long=True) or [])
        try:
            mel.eval("doPaintEffectsToPoly(1,0,0,1,100000)")
        except Exception as exc:
            log("  %-16s conversion failed: %s" % (name, exc))
            cmds.delete(copy)
            break

        made = [n for n in (cmds.ls(assemblies=True, long=True) or [])
                if n not in before and n != copy]
        raw = tri_count(made)
        cmds.delete(copy)
        if attempt == 0:
            first = raw

        if not made or raw == 0 or raw > CONVERSION_TRI_CEILING:
            # This attempt thinned the brush too far (or blew up). Keep
            # whatever the previous, heavier attempt produced.
            if made:
                cmds.delete(made)
            break

        if best and raw < PROTOTYPE_TRI_BUDGET * 0.15:
            # Overshot: thinning a tree's branch and twig counts is coarse, and
            # one step can take it from a full canopy to a bare stick. A plant
            # somewhat over budget beats one that no longer reads as a plant.
            cmds.delete(made)
            break

        if best:
            cmds.delete(best)
        best, best_raw = made, raw

        if raw <= PROTOTYPE_TRI_BUDGET:
            break

        # Too heavy: paint proportionally fewer tubes and convert a fresh copy.
        ratio *= max(0.05, float(PROTOTYPE_TRI_BUDGET) / raw)

    if not best:
        log("  %-16s produced no polygons" % name)
        return [], 0

    log("  %-16s %7d tris -> %6d tris" % (name, first, best_raw))
    return best, best_raw


def thin_tubes(stroke, ratio):
    """Scale down how many tubes a brush paints along its stroke.

    Integer counts floor at 1: a brush whose twigs or leaves round to zero
    stops producing geometry altogether, which is how grapes and rosemary end
    up exporting as nothing.
    """
    changed = False
    for shape in cmds.listRelatives(stroke, shapes=True, fullPath=True) or []:
        for brush in cmds.listConnections(shape + ".brush", source=True,
                                          destination=False) or []:
            for attr in ("tubesPerStep", "numBranches", "numTwigs",
                         "numLeaves", "numFlowers"):
                plug = "%s.%s" % (brush, attr)
                try:
                    value = cmds.getAttr(plug)
                    kind = cmds.getAttr(plug, type=True)
                except Exception:
                    continue
                if not value or value <= 0:
                    continue
                if kind in ("long", "short", "byte"):
                    new = max(1, int(round(value * ratio)))
                else:
                    new = max(value * ratio, value * 0.05)
                try:
                    cmds.setAttr(plug, new)
                    changed = True
                except Exception:
                    pass
    return changed


def export_plants(out_dir):
    picked = one_stroke_per_type()
    if not picked:
        log("  no Paint Effects strokes in this scene")
        return
    log("  %d plant types found" % len(picked))

    prototypes = []
    for name in sorted(picked):
        stroke = picked[name]
        if not cmds.objExists(stroke):
            continue

        made, raw = convert_within_budget(stroke, name)
        if not made:
            continue

        group = cmds.group(made, name="NOCOL_Plant_" + name)
        cmds.xform(group, centerPivots=True)
        prototypes.append(group)

    if not prototypes:
        log("  nothing converted")
        return
    cmds.select(prototypes, replace=True)
    export_selection(os.path.join(out_dir, "Plant_Prototypes.fbx"))


# --------------------------------------------------------------------------
# scatter
# --------------------------------------------------------------------------

def export_scatter(out_path):
    strokes = cmds.ls(type="stroke", long=True) or []
    if not strokes:
        log("  no strokes to scatter")
        return

    rows = []
    counts = collections.Counter()
    for shape in strokes:
        parents = cmds.listRelatives(shape, parent=True, fullPath=True)
        if not parents:
            continue
        xform = parents[0]
        name = plant_type(shape)

        # Each stroke transform already sits where its plant grows - the path
        # curves live in a separate `others` group and are shared, so reading
        # positions off the curves collapses hundreds of plants onto one point.
        pos = cmds.xform(xform, query=True, worldSpace=True, translation=True)
        rot = cmds.xform(xform, query=True, worldSpace=True, rotation=True)

        scale = cmds.xform(xform, query=True, worldSpace=True,
                           scale=True, relative=True)[0]
        try:
            # Paint Effects sizes the plant itself with globalScale.
            scale *= cmds.getAttr(shape + ".globalScale")
        except Exception:
            pass

        # Maya is right-handed +X right, Unity left-handed: negate X.
        rows.append([name,
                     "%.4f" % -pos[0], "%.4f" % pos[1], "%.4f" % pos[2],
                     "%.2f" % rot[1], "%.4f" % scale])
        counts[name] += 1

    with open(out_path, "w", newline="") as handle:
        writer = csv.writer(handle)
        writer.writerow(["type", "x", "y", "z", "yaw", "scale"])
        writer.writerows(rows)

    log("  wrote %s" % out_path)
    for name, count in counts.most_common():
        log("    %-16s %5d" % (name, count))
    log("  %d instances total" % len(rows))


# --------------------------------------------------------------------------

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    project = os.path.dirname(here)  # Assets/_Project

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scene", required=True, help="path to the .mb/.ma")
    parser.add_argument("--mode", default="all",
                        choices=["structure", "plants", "scatter", "all"])
    parser.add_argument("--models-dir", default=os.path.join(project, "Models"))
    parser.add_argument("--data-dir", default=os.path.join(project, "Data"))
    parser.add_argument("--groups", default=None,
                        help="override PREFIX_RULES, e.g. "
                             "'grassBermuda1MeshGroup=NOCOL_,ground=COL_'")
    parser.add_argument("--out-name", default="Biodome.fbx",
                        help="filename for --mode structure")
    args = parser.parse_args()

    rules = None
    if args.groups:
        rules = []
        for pair in args.groups.split(","):
            group, _, prefix = pair.partition("=")
            rules.append((group.strip(), prefix.strip() or "NOCOL_"))

    scene = os.path.expanduser(args.scene)
    if not os.path.isfile(scene):
        sys.exit("no such scene: %s" % scene)
    for folder in (args.models_dir, args.data_dir):
        if not os.path.isdir(folder):
            os.makedirs(folder)

    open_scene(scene)

    # Order matters: scatter reads the strokes, plants destroys some of them,
    # structure renames everything.
    if args.mode in ("scatter", "all"):
        log("[scatter]")
        export_scatter(os.path.join(args.data_dir, "biodome_plants.csv"))
    if args.mode in ("plants", "all"):
        log("[plants]")
        export_plants(args.models_dir)
    if args.mode in ("structure", "all"):
        log("[structure]")
        export_structure(args.models_dir, rules, args.out_name)

    log("Done.")
    maya.standalone.uninitialize()


if __name__ == "__main__":
    main()
