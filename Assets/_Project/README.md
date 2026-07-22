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
| `Wheel_*` cubes | wheel meshes | drag into **Tractor Path Follower ▸ Drive Wheels** (spin about local **X** = axle) |
| `MowerAnchor` | the mower deck point | move it where the cut should trace; it drives the trail |
| `Floor` plane | Biodome floor mesh | keep a non-convex static `MeshCollider` |
| `Biodome` (empty) | Biodome glass FBX | parent under `Environment` |

The mower anchor is mid-mounted on the tractor pivot so the cut traces the CSV **exactly**; move it
rearward on the real model if you want a trailing deck (it will then round off sharp corners slightly).

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
| `Runtime/MowingVisual_Trail.cs` | flat per-stroke TrailRenderer ("mowed" ribbon) |
| `Runtime/SimulationManager.cs` | load / restart / camera framing |
| `Editor/SceneBootstrap.cs` | **Tools ▸ NASA Sim ▸ Build Test Scene** |

## Next milestones

- **M2 — real cut grass:** a `MowingVisual_PaintRT` (RenderTexture accumulation + a Shader Graph grass
  mask) drops in behind `IMowingVisual` with no change to the tractor or loader.
- Astronaut, interactive camera/UI, importing the real FBX.

> Decals were intentionally *not* used: the active `PC_Renderer` is in **Deferred** mode with no Decal
> Renderer Feature (screen-space decals don't render under Deferred), so the TrailRenderer path avoids
> any renderer reconfiguration.
