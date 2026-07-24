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

## Part 3 — Biodome (dome + plants now; stairs later)

No staircase modelled yet, so for now this is **static scenery**: the dome and some plants. The tool
below makes it walkable by reading a **name prefix** off each object.

### A. In Maya — name objects by prefix

Prefix each object's name so the importer knows what to do with it:

| Put this prefix on… | Prefix | Effect |
|---|---|---|
| The **glass dome** | `NOCOL_` | Renderer only — you walk through it (use `COL_` if you want it solid) |
| **Plants / decoration** | `NOCOL_` | No collider — walk past them freely |
| A **floor / ground** slab | `COL_` | Solid — walkable surface |
| **Walls** you shouldn't pass | `COL_` | Solid |
| *(Later)* a **staircase flight** | `STAIR_` | Solid **+ auto smooth ramp** — as **one object** per flight |
| *(Later)* the **balcony deck** | `BALCONY_` | Solid |
| *(Optional)* an empty at floor level | `SPAWN_` | Astronaut starts here, facing its +Z |

Example: `NOCOL_GlassDome`, `NOCOL_Plant_01`, `NOCOL_Plant_02`, `COL_FloorSlab`, `SPAWN_Start`.

Also:
- [ ] **Convert Paint Effects / XGen plants to polygons first** (Modify ▸ Convert ▸ Paint Effects to
      Polygons) — otherwise they export as nothing. Keep them **small/light**; a converted field can be
      hundreds of thousands of triangles.
- [ ] **Embed Media = ON**, **Y-up**, **metres**, **freeze transforms**, delete history.
- [ ] Export.

### B. In Unity

- [ ] Drop the biodome `.fbx` in `Assets/_Project/Models/`.
- [ ] Drag it **from the Project window into the Hierarchy** (into the scene) and set its **Position to
      (0, 0, 0)**.
- [ ] If it's **way too big**, select the FBX in the Project window → Inspector → **Model** tab →
      **Scale Factor** (e.g. `0.01` if it was modelled in centimetres) → **Apply**.
- [ ] Select the biodome **in the Hierarchy** → **Tools ▸ NASA Sim ▸ Biodome ▸ Wire Up Selected Model.**
      The Console reports how many pieces were made solid vs. pass-through.
  - If it says **"no prefixes found,"** it made *everything* solid (including glass) — go back to Maya,
    add the prefixes, re-export, and run it again.
- [ ] **Press Play.** Walk around: you shouldn't fall through the floor, and you should be able to walk
      past the plants and (if you set the dome to `NOCOL_`) through the glass.

> When the staircase is modelled later, name the flight `STAIR_MainFlight` (one object) and the deck
> `BALCONY_UpperDeck`, re-run the wire-up, and the astronaut will climb it — no code or scene changes.

**Scale reference:** the logo auto-fits to **~40 units (metres) across** on a 50×50 floor. Size the
biodome and grass to enclose that (a dome roughly 45–60 m wide), and import them at metric scale (or set
**Scale Factor** on the FBX until they read in metres). Because the astronaut is auto-scaled to 1.8 m and
the tractor auto-grounds, they'll sit correctly against the biodome with no extra tuning. Collider and
stair-ramp generation both measure from bounds, so they're scale-proof. If the grass patch is a *tile*,
keep it small and repeat it rather than exporting one giant field mesh.

---

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
