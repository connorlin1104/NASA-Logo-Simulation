# NASA Logo Mowing Simulation — Milestone 1

A tractor drives waypoints loaded from a CSV and traces the NASA logo, laying a flat "mowed grass"
trail on the ground. Built with primitive stand-ins so it runs *now*; swap in your FBX models later
with no code changes.

- **Unity** 6000.5.0f1 · **URP** 17.5.0 · IDE: JetBrains Rider (already connected)

## Quick start

1. Open the project in Unity. Let it compile (`Assets/_Project/Scripts`).
2. Menu: **Tools ▸ NASA Sim ▸ Build Test Scene**. This creates and opens
   `Assets/_Project/Scenes/NasaLogoSim.unity`, wired and ready.
3. **Before pressing Play**, look at the Scene view: the loaded logo is drawn as gizmos —
   **green** = mowed segments, **red-dotted** = pen-up gaps between letters, **yellow** = start.
   This is your fast CSV sanity check (no Play needed).
4. Press **Play**. The cube tractor drives the path, wheels spin, and a flat trail forms the logo.
   Press **R** to restart the run.

## Controls

You play as an astronaut walking around the biodome while the tractor mows.

| Key | Action |
|---|---|
| **W A S D** (or arrows) | Walk (a relaxed lunar pace) |
| **Shift** (hold) | Run — a brisk "speed walk on the moon" |
| **Space** | Jump (low lunar gravity ⇒ a slow, floaty hop) |
| **Mouse** | Look / orbit |
| **C** | Cycle camera: third-person → first-person → overview |
| **Esc** | Release the mouse cursor (click the Game view to re-capture it) |

The astronaut moves with a slow, loping **moon-walk** cadence: gravity is low, the arm/leg swing is
languid, and jumps hang. In **first-person** (press **C** once) the arms are lifted into the visor view
so you can see them swinging as you walk.

Walk north into the staircase to climb to the balcony and watch the logo being mowed from above. The
staircase sits at z **+19 → +24**, clear of the logo's north edge (z +17) so it never blocks the tractor.

**Wheels (visual):** `Tractor Path Follower ▸ Wheels` takes **up to 4** entries — a **Mesh**, an **Axle**
(that wheel's pivot; when it's a mesh part the wheel spins about the **centre of that axle geometry**,
not its transform origin) and an **Axle Axis** (which of the axle's local axes runs along the axle:
**Z**/blue by default, or X/red, Y/green). Entries are fully
independent (own pivot, own axis, own radius measured from its own mesh), and the whole thing is
**cosmetic** — wheels follow the tractor because they're parented to it; nothing here affects the path
driven or the line mowed.

**Spin speed** is adjustable: `Wheel Spin Multiplier` on the component scales every wheel (`1` = true
rolling, negative reverses), and each wheel's own `Spin Multiplier` trims it individually. Both can be
dragged live in Play mode.

Leaving **Axle** empty spins the mesh about its own pivot. Assign one when the mesh's pivot isn't at the
hub — common in exported FBX, where it makes the wheel swing in an arc instead of rotating on the spot.
If a wheel spins on the wrong axis, flip its **Axle Axis** (Z ↔ X is the usual 90° difference) rather
than re-orienting the axle object.

Already built your own axles? Select them in the Hierarchy and run **Tractor ▸ Populate Wheels From
Selection** — it only fills in the list, with no rebuild or re-import. (A fresh `Swap In Selected FBX`
creates an `Axle_<wheel>` at each measured hub instead.)

Each wheel draws its **axis of rotation** in the Scene view for setup — cyan line = the axis, red dot =
the pivot, yellow circle = the measured rolling radius, white spoke = turns as it spins. Toggle with
**Show Wheel Gizmos**; it never renders in the Game view or a build.

> **Tuning the feel:** all of the moon-walk values (walk/run speed, gravity, jump height, swing cadence,
> first-person arm lift) live as named constants at the top of
> `Assets/_Project/Scripts/Editor/AstronautSetup.cs`. Edit them and re-run
> **Tools ▸ NASA Sim ▸ Add Astronaut & Balcony To Scene** to apply — that menu re-pushes the feel onto
> the scene's components, so you don't need to rebuild. (Re-running also resets any hand-tweaks you made
> to those same fields in the Inspector.)

Mowing-run keys (unchanged): **R** restart · **1**–**5** set 1×/2×/4×/8×/16× speed · **[** **]** halve
and double it. Astronaut movement deliberately ignores this multiplier — it runs on unscaled time, so
you walk at the same pace no matter how fast you fast-forward the tractor.

The astronaut, staircase and balcony are primitive stand-ins, built by
**Tools ▸ NASA Sim ▸ Add Astronaut & Balcony To Scene** (also run automatically by *Build Test Scene*).
It is safe to re-run on an existing scene — it updates rather than duplicating.

## Inserting your own models

Drop `.fbx` files into `Assets/_Project/Models/` and use the **Tools ▸ NASA Sim** menu. Full
step-by-step (Maya/Mixamo export settings included):
[`Assets/_Project/Models/IMPORT_GUIDE.md`](Models/IMPORT_GUIDE.md).

- **Tractor ▸ Validate / Swap In Selected FBX** — drops your tractor onto the existing path-follower,
  auto-scales it, and wires the wheels (any object named `wheel*`) to spin.
- **Astronaut ▸ Validate Selected FBX** — reports rig type, which arm bones were found, height in
  metres, missing textures, and animation clips. Run this first.
- **Astronaut ▸ Swap In Selected FBX** — replaces the placeholder, auto-scales to 1.8 m, re-binds the
  arm bones and the first-person camera anchor. The controller and camera rig are untouched.
- **Astronaut ▸ Rotate Model 90 (fix facing)** — for models that don't face +Z.
- **Biodome ▸ Wire Up Selected Model** — adds colliders by name prefix and generates smooth ramp
  colliders over staircases so the astronaut climbs without jitter.

**Rig the astronaut as Humanoid and no naming convention is needed at all** — Unity's avatar maps the
bones whatever they're called. Full spec, prefix table, export checklist and troubleshooting:
[`Assets/_Project/Models/IMPORT_GUIDE.md`](Models/IMPORT_GUIDE.md).

## Resizing models & world scale

The whole sim works in **metres**, anchored to the logo. The logo auto-fits to **40 units** across
(`CsvWaypointLoader ▸ Target World Size`) on a 50×50 floor, so a 1.8 m astronaut reads as small and the
tractor as a vehicle. Keep that logo size fixed and scale everything else to sit around it, and nothing
drifts out of proportion. Anything derived from size (mowed-trail width, stair ramps) is expressed as a
fraction or measured from bounds, so it follows the scale automatically.

**Shrinking the tractor** (to fit the logo better): scale the **`Tractor_Model`** child, *not* the
`Tractor` root. The root drives the path in world units, so the trace stays exact at any size; the mower
anchor is a sibling of the model, so the cut line doesn't move either. On the next Play the follower
**re-grounds the model and recomputes the wheel-spin rate** from its new size (`Tractor Path Follower ▸
Auto Ground Model / Auto Wheel Radius`), so it can't float, sink, or spin wrong. Re-running
**Tractor ▸ Swap In Selected FBX** also re-fits it.

**Resizing the astronaut:** scale the **`Astronaut` root** uniformly — the CharacterController, the
Head/CameraPivot camera anchors and the visual all scale together, so collision and camera stay
consistent. (Walk/run speed stay in metres per second; lower them in `Astronaut Controller` if a smaller
astronaut should also step slower.) Or just change `TargetAstronautHeight` in `ModelImportTools.cs` and
re-run **Astronaut ▸ Swap In Selected FBX**.

**Grass & biodome (importing soon):** import them at metric scale (or set **Scale Factor** on the FBX so
they read in metres), sized to enclose the ~40 m logo. Because the astronaut is auto-scaled to 1.8 m and
the tractor auto-grounds, they'll sit correctly against a real-scale biodome with no extra tuning. The
**Biodome ▸ Wire Up** colliders and stair ramps are all measured from bounds, so they're scale-proof.

## Inserting your own CSV

1. Drop your waypoint CSV into `Assets/_Project/Data/` (replace or sit beside `nasa_logo.csv`).
2. Select **WaypointsHolder** in the Hierarchy → **Csv Waypoint Loader** → drag your file onto
   the **Csv File** slot. Make sure it points at **`nasa_logo.csv`** (the clean, converted path) —
   not `file.csv` (the raw multi-object Maya dump, see below).
3. Watch the gizmo preview and tune the loader fields until the logo reads correctly and lies flat
   on the ground (XZ). Then press Play.

### Converting a raw Maya curve export

`file.csv` is the raw Maya dump: it concatenates **every** exported object, each as an **outline
sampled at several extrusion layers**, with no section headers. Feeding it directly makes the tractor
trace the whole scene. The converter `Assets/_Project/Data/convert_maya_to_csv.py` assembles the NASA
**meatball** into the clean `nasa_logo.csv`:

- **circle** (`outline`) + **orbit ellipse** (`Orbit`) + **red vector swoosh** (`Top` + `bottom`) +
  **NASA letters** (N, A1, S, A2);
- **circle** (`outline`) + **orbit ellipse** (`Orbit`) + **red vector swoosh** (`Top` + `bottom`) +
  **NASA letters** (N, A1, S, A2), all as thin outlines;
- the orbit is a clean ellipse using the orbit's true (tilted) principal axes, shrunk by `ORBIT_SCALE`
  to clear the circle and thinned by `ORBIT_SQUEEZE` on the minor axis;
- the vector's raw points are a zig-zag surface strip, so even/odd indices are split into the two band
  edges and traced as a smooth outline (no squiggle);
- splits each glyph into its true contours (e.g. the A's inner counter) and densifies so long edges
  aren't mistaken for pen-up gaps;
- writes `(X, mayaZ, 0)` so the loader's **default** axis mapping lays the whole logo flat on the
  ground at one consistent scale (~1969 points, 10 contours, 9 pen-up lifts).

**Orientation:** `FLIP_Z` (top of the script) negates the vertical axis to fix the upside-down view.
If the logo instead comes out mirrored left-right, set `FLIP_X = True` (and/or `FLIP_Z = False`). You
can also flip it live in Unity via the loader's **Map Column X/Y To → Neg X / Neg Z** — no re-run needed.

Re-run after re-exporting: `python3 Assets/_Project/Data/convert_maya_to_csv.py`; update the `ELEMENTS`
row-ranges if the export's order/sizes change.

> Tip: the letters are traced as **outlines**. With the default `Target World Size` (20) and a
> Trail `Width` of 1.5 the strokes are fairly bold; for crisper letters, increase Target World Size
> (e.g. 30–40) on the loader, and/or lower the Trail Width on **MowerBrush → Mowing Visual_Trail**.

### CSV format & loader options

- Rows of `x,y,z` (a header row is skipped by default; `#` lines are comments). Extra columns are ignored
  unless you use a pen column.
- **Axis remap** — Maya is Y-up; the loader maps CSV column *Y* → Unity *Z* so the logo lies on the ground.
  Change `Map Column * To` if your export uses a different plane.
- **Auto-fit** (on by default) uniformly scales the path so its largest ground dimension equals
  `Target World Size` (default 20) — handles arbitrary Maya units without hand-tuning `scale`.
- **Pen / stroke breaks** — how the mower knows to lift between disconnected strokes (e.g. the N-A-S-A letters):
  - `AutoJumpBreak` *(default)* — a segment longer than `4 × median segment` is treated as a pen-up jump.
    This is relative, so detection scales with the logo at any `Target World Size`. Works on a plain
    `x,y,z` path with no flags. `Jump Break Distance` (default 0) adds an optional absolute floor.
  - `FourthColumn` — read a 4th column (`0` = pen up, non-zero = pen down) for exact control.
  - `AlwaysDown` — single continuous stroke, never lift.

## Swapping in your FBX models

Everything references **Transforms**, never object names, so replacing primitives is drag-and-drop:

| Stand-in | Replace with | Re-wire |
| --- | --- | --- |
| `Tractor` cube | Tractor FBX (chassis) | keep `Rigidbody` (kinematic) + convex `MeshCollider`; re-assign wheels/anchor below |
| `Wheel_*` cubes | wheel meshes | add to **Tractor Path Follower ▸ Wheels** (Mesh + Axle per wheel, see below) |
| `MowerAnchor` | the mower deck point | move it where the cut should trace; it drives the trail |
| `Floor` plane | Biodome floor mesh | keep a non-convex static `MeshCollider` |
| `Biodome` (empty) | Biodome glass FBX | parent under `Environment` |

The mower anchor is mid-mounted on the tractor pivot so the cut traces the CSV **exactly**. To draw the
cut from a rear **plow/deck** instead, move `MowerAnchor` there: the follower **sub-samples the deck
position along the path within each frame**, so the swath stays smooth (no scalloped edges at corners)
even when the tractor crosses several waypoints per frame or the run is sped up. A far-rear deck still
rounds *very* sharp letter corners slightly — that's inherent to a trailing deck; keep the anchor as
close to the axle as the look allows, or narrow **Simulation Manager ▸ Mow Width Fraction** for crisper
corners.

## Colliders

- **Tractor:** kinematic `Rigidbody` + **convex** `MeshCollider` (low-poly ⇒ one hull is cheap).
  Movement is kinematic/deterministic, so colliders aren't needed for the trace — they're for later
  interaction (astronaut, biodome walls).
- **Floor / biodome:** static, **non-convex** `MeshCollider`.
- Rule: a non-convex `MeshCollider` may only be static or kinematic; a dynamic Rigidbody needs a convex one.

## Scripts (`Assets/_Project/Scripts`)

| File | Role |
| --- | --- |
| `Runtime/WaypointPath.cs` | data model (`Waypoint`, `WaypointPath`) |
| `Runtime/CsvWaypointLoader.cs` | parse CSV → path (remap, auto-fit, pen detection) |
| `Runtime/TractorPathFollower.cs` | kinematic driving, wheels, pen control, gizmo preview |
| `Runtime/MowerController.cs` | `IMowingVisual` facade (swap the visual without touching the tractor) |
| `Runtime/MowableGrass.cs` | the standing blade field + the mow mask the tractor cuts into it |
| `Runtime/MowingVisual_GrassAndFlowers.cs` | cuts the blades, flattens clumps, throws the logo's flowers |
| `Runtime/SimulationManager.cs` | load / restart / camera framing |
| `Editor/SceneBootstrap.cs` | **Tools ▸ NASA Sim ▸ Build Test Scene** |

## Next milestones

- **M2 — real cut grass:** a `MowingVisual_PaintRT` (RenderTexture accumulation + a Shader Graph grass
  mask) drops in behind `IMowingVisual` with no change to the tractor or loader.
- Astronaut, interactive camera/UI, importing the real FBX.

> Decals were intentionally *not* used: the active `PC_Renderer` is in **Deferred** mode with no Decal
> Renderer Feature (screen-space decals don't render under Deferred), so the TrailRenderer path avoids
> any renderer reconfiguration.
