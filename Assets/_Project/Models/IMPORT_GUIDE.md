# Model Import — Do-This Checklist

Do the three parts in order: **Tractor → Astronaut → Biodome**. Each part has a **Maya/Mixamo** side
(what to do before export) and a **Unity** side (drop the `.fbx` in this folder, then click menu items
under **Tools ▸ NASA Sim**).

**Every model, no matter which part:**
1. Drop the `.fbx` into `Assets/_Project/Models/`.
2. Click it once in the **Project** window to select it — its import settings appear in the Inspector.
3. Use the matching **Tools ▸ NASA Sim** menu items below.

The scene already works with primitive stand-ins, so you can swap models in one at a time and test each.

---

## Part 1 — Tractor

### A. In Maya (before export)

- [ ] **Face +Z.** The tractor's nose should point down **+Z** (Unity forward). If not, it drives
      backwards — fixable in Unity, but easier to get right here.
- [ ] **Y-up.** (Maya's default. Unity matches it.)
- [ ] **Name every wheel with `wheel` in it** — `wheel_01`, `wheel_02`, … `wheel_06`. Each wheel a
      **separate object**, not merged into the body.
- [ ] **Wheel pivots at the hub centre**, and the wheel's **local X axis pointing left-right along the
      axle.** This is what makes them spin correctly — the sim rolls each wheel about its own local X.
      (If they end up spinning on the wrong axis in Unity, this is the thing to fix.)
- [ ] **Freeze transforms** and **delete history** (Modify ▸ Freeze Transformations, Edit ▸ Delete by
      Type ▸ History).
- [ ] **Export in metres** if you can.
- [ ] **Embed Media = ON** in the FBX export options (so textures come along — otherwise it imports
      grey). Or send the `.fbm` texture folder alongside.
- [ ] Select the tractor → **File ▸ Export Selection ▸ FBX**.

### B. In Unity

- [ ] Drop the `.fbx` in `Assets/_Project/Models/`.
- [ ] (Optional) Select it → Inspector → **Rig** tab → **Animation Type = None** (the tractor isn't a
      skinned character).
- [ ] Select the FBX → **Tools ▸ NASA Sim ▸ Tractor ▸ Validate Selected FBX**. Read the **Console**:
      it lists the wheels it found, the size, and whether textures are missing. Fix any red flags before
      swapping.
- [ ] **Tools ▸ NASA Sim ▸ Tractor ▸ Swap In Selected FBX.** This deletes the cube tractor, drops your
      model onto the same path-follower, auto-scales it, sits the wheels on the ground, wires the wheels
      to spin, and keeps the mower attached.
- [ ] **Press Play.** The tractor should drive the logo, wheels turning.
- [ ] If it drives **backwards** → **Tools ▸ NASA Sim ▸ Tractor ▸ Rotate Model 90 (fix facing)**, repeat
      until the nose leads.
### Wheels — the **Wheels (visual)** list

Select **Tractor** in the Hierarchy → **Tractor Path Follower ▸ Wheels**. It holds **up to 4** wheels,
and each entry has two slots:

| Slot | What to put in it |
|---|---|
| **Mesh** | The wheel's visual mesh object |
| **Axle** | The transform this wheel **pivots around**. If it's a mesh part, the wheel spins about the **centre of that axle geometry** (not its transform origin, which is often back at the model origin); if it's a plain empty, about the empty's position. Leave the slot empty to spin the mesh about its own pivot. |
| **Axle Axis** | Which of the Axle's own local axes runs **along** the axle: **Z** = blue arrow *(default)*, **X** = red, **Y** = green |
| **Spin Multiplier** | Fine-tunes just this wheel's speed. `1` = true rolling, `-1` reverses it. |

**Spin too fast / too slow?** `Tractor Path Follower ▸ Wheel Spin Multiplier` scales **all** wheels
(`1` = true rolling, negative reverses), and each wheel's own **Spin Multiplier** trims it further. Both
can be dragged **live while playing**, so you can dial it in without stopping.

**Wrong axis? Don't re-orient anything.** Just change **Axle Axis** and watch the cyan gizmo line snap
round — it's a 90° flip between X and Z. Each wheel is set independently.

Every entry is fully independent: its own pivot, its own orientation, its own size (each wheel's rolling
radius is measured from its own mesh, so a small front wheel spins faster than a big rear one). It's
**purely cosmetic** — the wheels follow the tractor because they're parented to it; nothing here changes
the path driven or the line mowed.

**Already made your own axles? Don't re-run the swap** (it would rebuild the model and lose your work).
Instead: select your wheels in the **Hierarchy** — either each wheel's **axle/group** object or the mesh
itself — and run **Tools ▸ NASA Sim ▸ Tractor ▸ Populate Wheels From Selection**. It only fills in the
list: nothing is deleted, re-imported or moved. Selecting the group is preferred — the mesh inside it is
found automatically and the group becomes that wheel's pivot.

*(On a fresh import, `Swap In Selected FBX` instead creates an `Axle_<wheel>` empty at each measured hub
and assigns it for you.)*

### Checking it in the Scene view

Each wheel draws its **actual axis of rotation** in the Scene view (never in the Game view or a build) —
toggle with `Tractor Path Follower ▸ Show Wheel Gizmos`:

| Gizmo | Meaning | If it looks wrong |
|---|---|---|
| **Cyan line** | The axis of rotation, through the pivot | Should run along the axle, left-to-right through the wheel. If it's 90° out, switch **Axle Axis** (Z ↔ X). |
| **Red dot** | The exact pivot point — the centre of the axle component | Must sit at the wheel's hub, or the wheel swings in an arc instead of spinning on the spot |
| **Yellow circle** | The rolling circle at the measured radius | Should land on the rim. If it's much bigger/smaller, the spin *rate* will be off. Orange = the mesh couldn't be measured. |
| **White spoke** | Turns as the wheel spins | Watch it in Play mode to confirm the wheel rolls the right way |

Then:

- [ ] Wheel **spins on the wrong axis** → change that wheel's **Axle Axis** (Z ↔ X is the usual 90° flip)
      until the cyan line runs along the axle. Only that wheel is affected.
- [ ] Wheel **swings in an arc instead of spinning in place** → its `Axle_*` isn't at the hub. Move the
      `Axle_*` object to the centre of that wheel. (This is the usual symptom when an exported mesh's
      pivot sits at the model origin rather than the hub.)
- [ ] Wheels **don't spin at all** → they weren't detected. Fill in **Wheels** by hand, or put `wheel` in
      each wheel object's name and re-run the swap.
- [ ] More than 4 wheels? The list caps at 4; the swap logs exactly which were wired and which weren't.

> The mower, path, camera, and the whole mowing sim are untouched by the swap — only the tractor's look
> changes.

**Too big / too small?** Scale the **`Tractor_Model`** child (in the Hierarchy), *not* the `Tractor`
root. The trace is driven by the root in world units, so it stays exact at any size, and the mower line
doesn't move. On the next **Play** the follower re-grounds the model and recomputes the wheel-spin rate
automatically — it can't float, sink, or spin wrong.

**Drawing the cut from the plow:** move the `MowerAnchor` object onto the rear plow/deck. The trail is
sub-sampled along the path each frame, so it stays smooth. A *far*-rear deck rounds very sharp letter
corners slightly (inherent to a trailing deck) — keep the anchor near the axle, or lower
`MowerBrush ▸ Mowing Visual_Trail ▸ Min Vertex Distance` for crisper corners.

---

## Part 2 — Astronaut

The goal: a walking astronaut whose **arms swing**, and who can **climb stairs**. Both work through the
menu below — read the two "how it works" notes at the end so you know what actually drives them.

### A. On Mixamo — get a rigged model (free, [mixamo.com](https://www.mixamo.com/))

- [ ] Sign in with a free **Adobe account**.
- [ ] **Get a character**, either:
  - **Pick one:** *Characters* tab → search `astronaut` (or any humanoid) → select it. **or**
  - **Upload your own** (e.g. the team's Maya astronaut): *Upload Character* → drop the `.fbx`/`.obj`/
    `.zip` → in the auto-rigger, place the markers (chin, wrists, elbows, knees, groin) → let it rig.
    **This is the step that turns an unrigged model into something whose arms can move.**
- [ ] **Download the character:** *Download* button → **Format = FBX for Unity (.fbx)** → **Pose =
      T-pose** → Download. This file is your astronaut.
- [ ] **(Optional — real walk animation instead of the built-in swing):** *Animations* tab → search
      `walking` → pick one that clearly swings its arms → tune **Arm-Space** / **Overdrive** if you like
      → *Download* → **Format = FBX for Unity**, **Skin = With Skin**. (You only need this if you want a
      hand-authored walk cycle; the built-in procedural swing works without it.)

### B. In Unity — import settings (these are what make arm swing possible)

- [ ] Drop the character `.fbx` in `Assets/_Project/Models/`.
- [ ] Select it → Inspector → **Rig** tab:
  - **Animation Type = Humanoid**
  - **Avatar Definition = Create From This Model**
  - **Apply**
  - *(Optional)* click **Configure…** and check every bone shows **green**, then Done.
  > Humanoid is the magic setting: Unity maps the skeleton to a standard bone set, so the code finds the
  > arms **no matter what the bones are named**. A Mixamo download is already set up for this.
- [ ] If the model looks **grey / pink**: Inspector → **Materials** tab → **Extract Textures…** and
      **Extract Materials…** (or re-export from Maya with Embed Media ON).

### C. In Unity — wire it into the scene

- [ ] Select the FBX in the **Project** window → **Tools ▸ NASA Sim ▸ Astronaut ▸ Validate Selected
      FBX.** The Console should say **"Humanoid avatar: VALID"** and list the arm bones it found. If it
      says arms not found, fix the rig before continuing.
- [ ] **Tools ▸ NASA Sim ▸ Astronaut ▸ Swap In Selected FBX.** Replaces the capsule placeholder,
      auto-scales to 1.8 m, plants the feet, binds the arms, and moves the first-person camera to the
      real head.
- [ ] **Press Play. WASD** to walk (Shift to run, **Space** to jump, **C** to switch camera). The arms
      should swing with a slow lunar cadence. Press **C** once for first-person — the arms are lifted into
      the visor view so you can see them swing.
- [ ] If it **faces the wrong way** → **Tools ▸ NASA Sim ▸ Astronaut ▸ Rotate Model 90 (fix facing)**,
      repeat until it faces where it walks.

**Too big / too small?** The swap auto-scales the model to ~1.8 m, so usually nothing to do. To resize
after that, scale the **`Astronaut` root** uniformly (collider + camera scale with it). The moon-walk
feel (speed, gravity, jump, swing cadence, first-person arm lift) is set by re-running
**Add Astronaut & Balcony To Scene**; the values are constants at the top of `AstronautSetup.cs`.

### How **arm swing** works (so you know what's required)

- With a **Humanoid** rig, arm swing is **automatic** — a built-in procedural swing rotates the arm
  bones as the astronaut moves. **No Animator setup needed.** This is the easy path.
- **To use a real Mixamo walk clip instead:** create an **Animator Controller**, drag the walk clip in,
  add a **float parameter named `Speed`**, and assign the controller to the swapped model's **Animator**.
  The code detects the controller and feeds `Speed` automatically, letting the clip drive the arms.

### How **stair climbing** works (so you know what's required)

- Climbing is handled by the **CharacterController on the astronaut root**, not by the model. It already
  works with the placeholder and keeps working after the swap. The model just must have **no colliders**
  (the swap tool strips them for you).
- **So there's nothing to do on the astronaut for stairs.** Stairs are part of the biodome (Part 3);
  when a real staircase is imported it gets a smooth ramp collider and the astronaut walks up it.

### Where to get an astronaut

- **[Mixamo](https://www.mixamo.com/)** — best option: free, auto-rigs any humanoid mesh into
  Unity-Humanoid, and its walk clips already swing the arms.
- **[Sketchfab astronauts](https://sketchfab.com/tags/astronaut)** (check each licence) ·
  **[Meshy — CC0](https://www.meshy.ai/tags/astronaut)** (no attribution) ·
  **[NASA 3D Resources](https://nasa.gov/3d-resources)** (authentic suits, unrigged → run them through
  Mixamo's auto-rigger).

---

## Part 3 — Biodome (dome, planting beds, plants)

> **You do not have to open `newGreenHouse.mb` in Maya.** It is exported by a headless script that
> never builds a viewport. This is the fix for the crash — see *Why Maya dies on "unhide all"* below.

### A. Export it (one command, no Maya window)

```sh
/Applications/Autodesk/maya2027/Maya.app/Contents/bin/mayapy \
    "Assets/_Project/Data/maya_headless_export.py" \
    --scene ~/Downloads/newGreenHouse.mb --mode all
```

Takes about a minute and writes three things:

| Output | What it is |
|---|---|
| `Models/Biodome.fbx` | Dome, tunnel and planting beds — 222,708 tris — plus 4,877 empty `PLANT_<Type>_<n>` markers, one at every plant |
| `Models/Plant_Prototypes.fbx` | One low-poly mesh per plant type, 14 of them, ~10k tris total |
| `Data/biodome_plants.csv` | The same plant positions in readable form (reference only — Unity uses the markers) |

`--mode structure`, `plants` or `scatter` run the three jobs separately.

### B. Why Maya dies on "unhide all"

It is **not** the polygon count. The whole file is **282,416 triangles**, which is nothing — Unity
draws that without noticing, and so should Maya.

The hidden `plants` group holds **4,877 live Paint Effects strokes**. Each is a procedural brush
authored for film — 78 segments around 14-sided tubes, with flowers and leaves on top — and Paint
Effects re-tessellates *all of them* on the main thread every time the viewport refreshes. Unhiding
the group is the moment Maya tries to build that geometry at once, on a machine with 8 GB of shared
CPU/GPU memory. Your teammate is not doing anything different; he is on the same hardware and getting
away with it because that much memory pressure is right at the edge — a few gigabytes free either way
is the difference between swapping (slow) and being killed (crash).

Two consequences worth knowing:

- **Paint Effects strokes export to FBX as nothing at all.** Even a Maya session that survived opening
  the file would give you a biodome with no plants in it. They have to be converted to polygons first,
  which is what `--mode plants` does.
- **Nothing is wrong with the model.** No cleanup, no retopology, no decimation of the dome is needed.

If you ever *do* need it open in the GUI: leave `plants` hidden and it opens fine.

### C. In Unity

- [ ] Drop `Biodome.fbx` and `Plant_Prototypes.fbx` in `Assets/_Project/Models/` (the script already
      puts them there).
- [ ] Select `Biodome.fbx` → Inspector → **Model** tab → check the size. The dome is **41 units** wide
      in Maya and the scene is in centimetres, so it usually arrives 100× too small. Set **Scale
      Factor** to `100` (or untick **Convert Units**) until the dome reads **~41 m** — about 23 times
      the astronaut's height. **Apply.**
- [ ] Drag `Biodome.fbx` into the Hierarchy, position **(0, 0, 0)**.
- [ ] Select it in the Hierarchy → **Tools ▸ NASA Sim ▸ Biodome ▸ Wire Up Selected Model**. The dome
      and beds are already prefixed `NOCOL_` and the tunnel `COL_`, so this needs no work in Maya.
- [ ] **Tools ▸ NASA Sim ▸ Biodome ▸ Scatter Plants.** Pick the biodome, leave the prototypes slot as
      found, press **Scatter Plants**.

The Console reports what was placed and the triangle total. At 100 % density that is ~4,900 plants and
about **2.8M triangles**, drawn in roughly a dozen batches because the tool turns on GPU instancing.
If the frame rate suffers, drop **Density %** and scatter again — the layout thins evenly and the tool
clears the previous pass first, so you can try 40 %, then 70 %, without stacking plants on top of each
other.

### D. If a plant type looks wrong

Everything is driven by two knobs at the top of `maya_headless_export.py`:

| Knob | Effect |
|---|---|
| `PROTOTYPE_TRI_BUDGET` | Triangles per plant. Raise it for chunkier plants, lower it for a faster scene. Multiply by ~4,900 to predict the scene cost. |
| `BRUSH_LIMITS` | How finely each brush is tessellated before conversion — `segments` along a tube, `tubeSections` around it. These are the numbers that were set for film. |

Re-run `--mode plants` and scatter again; the biodome and markers do not need re-exporting.

> Note the script simplifies the *brush* before converting rather than decimating the *mesh*
> afterwards. One stroke paints a whole row of carrots as hundreds of separate tube shells, and no
> mesh decimator can take a shell below one triangle — asking for an aggressive reduction deletes
> whole plants instead of simplifying them. Painting fewer, coarser tubes keeps every plant intact.

### E. Stairs, later

No staircase is modelled yet. When one exists, name the flight `STAIR_MainFlight` (one object) and the
deck `BALCONY_UpperDeck` in Maya, add them to `PREFIX_RULES` in the export script, re-export and re-run
the wire-up. The astronaut will climb it with no code or scene changes.

---

## Part 4 — Moon terrain and space background

Neither of these is imported, because neither should be a model:

- A **starfield is a texture on the inside of the sky**, not geometry. A modelled sphere is thousands
  of wasted triangles that still has to be scaled past the far clip plane.
- A **Unity Terrain** stores its surface as a heightmap and gets built-in LOD, culling and detail
  scattering that an imported mesh of the same detail cannot match.

The textures are already copied into `Assets/_Project/Textures/` from the Maya project's
`sourceimages/SpaceEnvironmentTextures/`.

- [ ] **Tools ▸ NASA Sim ▸ Environment ▸ Create Space Skybox** — builds a panoramic skybox from
      `8k_stars_milky_way.jpg`, drops ambient light to near-black and sets the sun to hard shadows.
      (Vacuum has no atmosphere to bounce light; without this the shadows look milky and the moon
      reads as an overcast day.)
- [ ] **Tools ▸ NASA Sim ▸ Environment ▸ Create Moon Terrain** — a 500 × 500 m terrain with 40 craters,
      textured with `8k_moon.jpg`, and a flat 140 m pad at the centre sitting exactly at **y = 0** so
      the logo, tractor and biodome need no repositioning.

Afterwards, decide what the old `Floor` plane is for: delete it and mow the terrain, or keep it as the
mowable surface with the terrain as the horizon around it. Sizes are constants at the top of
`MoonEnvironmentSetup.cs` if you want a bigger world or a wider flat pad.

For **Earth in the sky**, `2k_earth_daymap.jpg` is in the same folder — a sphere placed far away with
an unlit material, or a quad that always faces the camera.

---

## Part 5 — Grass

`grass3.mb` is **23,228 triangles**, so there is no performance problem to solve here — but almost all
of it is in the wrong place:

| Object | Tris | What to do |
|---|---|---|
| `pPlane1` | 20,000 | A ground plane subdivided 100 × 100 for no reason. **Do not import it** — the terrain (or the existing `Floor`) is the ground. |
| `grassBermuda1MeshGroup` | 3,228 | The actual grass clump, already converted from Paint Effects. **This is the asset.** |

Already exported for you as `Models/GrassClump.fbx`:

```sh
/Applications/Autodesk/maya2027/Maya.app/Contents/bin/mayapy \
    "Assets/_Project/Data/maya_headless_export.py" \
    --scene ~/Downloads/grass3.mb --mode structure \
    --groups "grassBermuda1MeshGroup=NOCOL_" --out-name "GrassClump.fbx"
```

**Do not scatter it as GameObjects.** One clump is fine; a lawn's worth is not — that is the mistake
that would actually cost you frames. Use it as a **Terrain detail mesh**, which Unity draws as GPU
instances with automatic distance fade:

- [ ] Select the terrain → **Paint Details** (the flower icon) → **Edit Details ▸ Add Detail Mesh**.
- [ ] **Detail Prefab** = `GrassClump`, **Render Mode** = *Vertex Lit* (or *Grass* to get wind sway),
      **Align To Ground** ≈ 1, **Noise Spread** ≈ 0.5.
- [ ] Paint it where you want lawn. **Terrain Settings ▸ Detail Distance** controls how far out it
      draws — the single biggest lever on cost.

If you would rather keep the flat `Floor` plane as the mowed area, the same clump works as a plain
prefab scattered by hand around the edges; just keep the count in the hundreds, not thousands.

**Scale reference:** the logo auto-fits to **~40 units (metres) across** on a 50×50 floor. The dome is
~41 m and encloses it. The astronaut is auto-scaled to 1.8 m and the tractor auto-grounds, so
everything lines up once the biodome's **Scale Factor** is right.


## Quick troubleshooting

| Symptom | Fix |
|---|---|
| Model is grey / pink | Textures not embedded → re-export with **Embed Media ON**, or add the `.fbm` folder / Extract Materials |
| Astronaut or biodome is giant/tiny | Maya cm vs Unity m → astronaut auto-scales; for the biodome set **Scale Factor** on import |
| Tractor / astronaut moves backwards | **Rotate Model 90 (fix facing)**, repeat as needed |
| A wheel spins on the wrong axis | Rotate that wheel's **`Axle_*`** object so its X (red) arrow runs along the axle |
| A wheel swings in an arc instead of spinning | Its **`Axle_*`** isn't at the hub → move it to the wheel's centre |
| Tractor wheels don't spin at all | Not detected → fill in **Tractor Path Follower ▸ Wheels**, or name them `wheel*` and re-run the swap |
| Arms don't swing | Rig must be **Humanoid** → run **Astronaut ▸ Validate Selected FBX** to see which bones are missing |
| Falls through the biodome floor | Floor mesh needs a `COL_` prefix → re-run **Biodome ▸ Wire Up** |
| Walks straight through the dome into space | You wanted it solid → prefix it `COL_` instead of `NOCOL_` |
| Maya crashes opening the biodome | Expected — don't open it. Export headlessly with `maya_headless_export.py` (Part 3). To open it anyway, leave the `plants` group hidden. |
| Biodome imports with no plants | Paint Effects never export to FBX. Run `--mode plants`, then **Biodome ▸ Scatter Plants**. |
| **Scatter Plants** says "no markers" | `Biodome.fbx` predates the markers → re-run `--mode structure` and re-import |
| Plants are the wrong size | The prototypes are unit plants scaled by each marker. If *all* of them are off, the biodome's **Scale Factor** is wrong, not the plants. |
| Frame rate drops after scattering | Lower **Density %** and scatter again, or lower `PROTOTYPE_TRI_BUDGET` and re-run `--mode plants` |
| Moon terrain floats above / sinks below the logo | Its flat pad is at `y = 0`; move the terrain object's **Y** to 0, don't move the logo |
| Skybox is grey | Run **Environment ▸ Create Space Skybox**; if it errors, `8k_stars_milky_way.jpg` isn't in `Assets/_Project/Textures/` |
