# Full Vision — Unity Setup Runbook

Everything below is driven from **Tools ▸ NASA Sim ▸ …** menu items. Run the numbered steps **in
order** (or use the one-click chain in step 1 and then do only the two manual steps). All tools are
safe to re-run.

## 0. What you should see at the end

Spawn outside the biodome facing the entrance → press **E** at the orange button → outer door slides
up → step in → it seals behind you, gas floods the chamber (~4 s, real seconds even at 16× sim speed)
→ inner door opens → inside, shin-deep grass ripples in the wind and parts around your boots, and the
tractor arcs naturally through the logo cutting a clean mown swath through it — clippings out of the
deck, NASA-colored flowers sprouting on the line just cut → pick and eat fruit at the moat trees (**E**) → pat a duck
(**E**) → walk the moat bank (ripples, foam, refraction) → climb the spiral stairs to the balcony
without jumping → **C** for the free-fly camera to admire the finished flower logo → **R** restarts.

## 1. The click order

> **One-click:** `Tools ▸ NASA Sim ▸ Setup ▸ Run Full Vision Setup` runs steps 2–13b for you.
> Steps 14–15 always need your eyes.

| # | Menu item | What it does |
|---|---|---|
| 2 | `Setup ▸ Configure Layers & Physics` | Creates the **Player** (8) and **Interactable** (9) layers; puts the astronaut on Player |
| 3 | `Setup ▸ Normalize Astronaut Scale` | Fixes the root-scale-0.5 bug that halved the step-up (why stairs felt like walls) |
| 4 | `Models ▸ Reimport & Remap All Models` | Binds every FBX material slot to same-named project materials (see IMPORT_GUIDE Part 0) |
| 5 | `Biodome ▸ Fix Dome Glass` | The big one: transparent, double-sided BiodomeGlass material — the dome is now visible from INSIDE and see-through from outside |
| 6 | `Biodome ▸ Wire Colliders & Spawn Outside` | COL_/NOCOL_ colliders on the dome + tunnel, `SPAWN_Outside` marker, astronaut moved there |
| 7 | `Setup ▸ Add Interaction System & UI` | The "[E] …" prompt UI + proximity sensor + the astronaut's hand (eat/pet flourishes); camera defaults to first person |
| 8 | `Biodome ▸ Build Airlock In Tunnel` | Doors, buttons, chamber sensor, gas vents, full pressurize cycle |
| 9 | `Tractor ▸ Wire Steer Wheels From Axles` | *(optional — the follower now does this itself)* Fills Steer Wheels in at edit time so you can see the choice, and logs every wheel's measured hub position |
| 10 | `Water ▸ Create Water Body` (Moat preset) | The moat ring (r 27–31 m) around the logo: animated water + mud basin with collider |
| 11 | `Water ▸ Spawn Ducks & Fish` | 3 pattable ducks + 8 fish in the moat (re-run per pond, counts adjustable) |
| 12 | `Grass ▸ Add Grass Mowing Visual` | Puts the grass/flower visual on the mower — the cut *is* the mark, nothing is painted on the floor |
| 13 | `Interactables ▸ Add Fruit Trees At Moat` | 4 placeholder trees on the outer bank; fruit grow at FRUIT_ markers in Play mode |
| 13b | `Grass ▸ Build Mowable Grass` | **The grass itself** — ~70,000 standing blades over the field, cut down by the tractor. Run it **last**: it probes the ground for what it may not grow through (see §3b) |
| — | `Grass ▸ Scatter Grass Tufts` / `Remove Scattered Tufts` | *Optional decoration only.* Modelled `GrassClump.fbx` tufts on top of the blade field. These are real GameObjects saved into the scene (1,500 of them is most of this scene's file size), so keep the density low — or remove them and let the blades do the work |
| — | `Interactables ▸ Make Selected Eatable` / `Make Selected Pettable` | **Any time:** select any object(s) → makes them edible/pattable (adds the component + an `InteractTrigger` child sized to the object). Same animation for every object; per-object bites, pace and respawn live on the component |
| 14 | **Manual:** select your ground model → `Environment ▸ Bake Simplified Collider` | Walkable collider baked from the uneven outside terrain (your visual ground stays untouched) |
| 15 | **Manual:** select each spiral staircase → `Biodome ▸ Add Spiral Stair Ramp` | Set Turns/Clockwise/Start Angle until the green wireframe hugs the treads, Build. "Disable tread colliders" stays ON — the smooth helicoid replaces the snaggy per-tread colliders. Settings are remembered per staircase. |

## 2. Controls

| Key | Action |
|---|---|
| WASD + mouse | Walk / look (first person) |
| Shift | Run · **Space** jump (tap = low hop, hold = full float) |
| **E** | Interact — doors, pick fruit, pat duck (prompt appears within ~2 m) |
| **C** | Toggle first-person ↔ free-fly camera |
| Fly cam | WASD + mouse, **Space/Ctrl** up/down, **Shift** fast, **scroll** = fly speed (your "zoom") |
| 1–5, [ ] | Sim speed 1–16× (tractor/flowers only — you, doors, gas, water, ducks stay real-time) |
| R | Restart the mow (the whole field of grass springs back up, flowers recycle) |

## 3. The time rule (for anyone adding scripts later)

`SimulationManager` drives `Time.timeScale` up to 16×. **Scaled** (fast-forwards): tractor, wheel
spin, the cut itself, clippings, flower ballistics. **Unscaled** (always real seconds):
astronaut, cameras, doors, airlock + gas particles, interactions, fruit sequence, ducks/fish, water
animation (script-fed `_WaterTime`), grass wind and the push-aside (script-fed `_NasaGrassTime`).
New ambience the player watches → unscaled; new mow-spectacle → scaled.

## 3b. The grass

`MowableGrass` (on the `MowableGrass` object) grows the field; `NasaSim/Grass` +
`Materials/GrassBlades.mat` draw it.

**Nothing about it is in the scene file.** The blades are generated from `Seed` when the scene loads
and thrown away when it unloads, so density is free to change and the field never bloats the YAML.
That also means they only exist while the component is enabled — deleting the object deletes the grass,
and there is nothing to clean up.

**The cut is a texture, not a mesh edit.** A single R8 mask is stretched over the field; the mower
paints its swath into it, and every blade samples the mask *at its own base* in the vertex shader and
shrinks to `Mown Height` where the tractor has been. Cutting 42 m² of grass therefore costs a few
hundred bytes a frame, and **R** stands the whole field back up instantly by clearing the mask.

| Field | What it does |
|---|---|
| `Density` | blades per m². 40 over the shipped field ≈ 70k blades / 500k verts. **`Editor Preview Fraction`** builds only a share of that outside play mode, because the editor rebuilds the field on every script recompile |
| `Blade Height` / `Blade Width` / `Blade Lean` | the blade itself. Remember the astronaut is only ~0.9 m tall, so 0.3 m grass is properly shaggy |
| `Ground Probe Spacing` | how finely the ground is raycast at build time. Grass follows what this finds, and skips anything within `Obstacle Clearance` of a stair, pillar or trunk. The tractor and the astronaut are excluded by name — otherwise wherever they were parked at build time would be a permanent bald patch |
| `Mask Resolution` / `Cut Feather` | sharpness of the cut edge (1024 over 42 m ≈ 4 cm) |
| `Walker` / `Walker Radius` | who parts the grass as they walk. Auto-finds the astronaut |
| On the **material**: `Mown Height`, `Mown Color`, wind direction/strength/speed/wavelength, `Root Shading`, `Backlight` | the look. The three colours are the *brightest* blades — each blade carries a per-blade darkening tint so the field isn't one flat sheet |

Clipping spray is measured, not faked: `Mow` counts how many mask texels were *still standing* where
the deck just passed, so a second lap over ground already cut throws nothing.

## 4. Maya → Unity export checklist (per model)

1. **Name materials their final Unity names** (`DuckBody`, `BiodomeGlass`, …) — the auto-remap keys on
   the name (IMPORT_GUIDE Part 0). Textures as PNG/TIF (never .iff/.rgb/.gif); **Embed Media ON**.
2. Y-up, forward = **+Z**, export in metres if possible (the swap tools auto-scale anyway).
3. Use the name prefixes (IMPORT_GUIDE Part 6): `COL_` walls/floors, `NOCOL_` glass/foliage,
   `WATER_<Name>` locator at a pond centre, `FRUIT_<n>` empties on a fruit tree, `SPAWN_` markers.
4. Drop the FBX into `Assets/_Project/Models/`.
5. **Swap it in:** select the `PH_*` placeholder in the Hierarchy + the FBX in the Project window →
   `Models ▸ Swap Placeholder With Selected FBX`. `Models ▸ List All Placeholders` = your to-model list.
   (Tractor/astronaut/biodome keep their dedicated swap menus from IMPORT_GUIDE Parts 1–3.)

**Water never comes from Maya.** The realism lives in the shader (depth tint, refraction, animated
waves, shore foam) and none of that survives FBX — Maya's water materials arrive as grey lumps, and a
ripple surface needs a dense even grid that's generated, not modelled. Maya supplies only basin/trench
geometry (`COL_`) and `WATER_` locators; `Water ▸ Create Water Body` does the rest. Extra ponds:
Pond preset (or a `WATER_` locator) → `Spawn Ducks & Fish` on it. Done.

## 5. Flowers: where they land and what color they are

All of this lives on the tractor's **MowerBrush → Mowing Visual_Grass And Flowers**.

**They follow the mown line.** A flower is not thrown at a random angle and left to land where it
may — the landing spot is picked first, ON the line the deck has just cut (from a rolling history of
deck positions, so it tracks curves exactly), and the throw velocity is then *solved* so the arc ends
exactly there. Three knobs shape it:

| Field | What it does |
|---|---|
| `Land Back Distance` | how far behind the deck a flower lands, measured **along** the cut line (default 0.5–1.5 m) |
| `Land Side Spread` | sideways scatter as a multiple of half the swath. `0` = a dead-straight single file, `1` = out to the swath edge, default `0.9` so flowers stay inside the cut |
| `Arc Seconds` | flight time, i.e. how high and lazy the toss looks. Changing it does **not** move the landing spot |
| `Flower Spacing` | metres of cut line between bursts — **the density dial.** `0.4` puts ~1 930 flowers on the finished logo; `0.6` was the old, dottier ~1 280 |
| `Flower Pool Limit` | `0` (default) sizes the pool from the logo — 573 m of line ÷ 0.4 m spacing = 1 430 bursts × 2 (the top of `Flowers Per Burst`) ≈ a **2 880** ceiling, against the ~1 930 actually thrown (the 42 star dots throw nothing) — so a finished mow never recycles a flower away. Set a number only to force a lower ceiling |

Measured over a full simulated run: half the flowers land within 0.14 m of the mown line and none
further than half the swath.

**The flower itself** is generated, under `Flower Shape`: a tapered stem with two lance leaves, a
centre disc, and `Petal Count` petals that widen, tilt up and droop at the tip. `Flower Height` scales
the whole thing (default 0.44 m — the astronaut is ~0.9 m tall), `Flower Size Variation` spreads it
per flower, `Flower Lean Degrees` stops a bed of them standing to attention. The material is *unlit*,
because it has to multiply vertex colours — that is what lets one mesh per colour serve every flower
of that colour and keeps ~1,900 of them batchable — so the head's depth comes from shading baked into
those vertex colours (`Petal Shading`, and the stem/leaf darkening) rather than from a light.
Edits take effect on the next **R**.

**Colors are classified by shape, every run** (`Palette Mode = Logo Auto`, the default — nothing to
set up). The shipped `nasa_logo_clean.csv` is 56 strokes:

| Stroke(s) | What it is | Test | Result |
|---|---|---|---|
| **0** | the circle | the roundest stroke ≥40 % of the logo across (radius variation 0.014, vs 0.28 for the runner-up — not close) | `Circle Color`, NASA blue `#0B3D91` |
| **41, 42, 43** | the red swoosh | every stroke ≥10 % across that **overhangs the circle**. 16–19 % of each of these three sits past the rim; every other stroke is at 0.0 % | `Swoosh Color`, NASA red `#FC3D21` |
| **44, 45, 46** | the orbit ellipse | — | `Other Color`, white |
| **47–49, 51–53, 55** | N A S A and their counters | — | `Other Color`, white |
| **1–40, 50, 54** | the 40 star dots (+2 sub-metre letter slivers) | under 4 % of the logo across — the biggest star is 3.3 %, the smallest non-star 5.6 % | **no flowers** (see below) |

The swoosh arrives as *three* strokes because the orbit and the letters cut the vector into pieces —
so this is a set, not a single winner. Beware the test that looks right and isn't: "the swoosh is the
big stroke inside the circle" picks the **orbit**, because the orbit is the one contained by the disc
and the swoosh is the one that isn't. Thickness doesn't separate them either — the thinnest swoosh
piece (0.62 m) is thinner than the fattest orbit piece (0.66 m). Overhang is the clean discriminator.

The classification is printed to the Console on the first flower of each run, so you can check it at a
glance. Because it tests shape rather than stroke number, re-exporting the CSV can't shift it.

**The star dots are mown but throw no flowers** (`Flowers On Stars` off, the default): 42 loops of
0.3–1.3 m read as speckle beside the circle and the letters, and the smallest are below the tractor's
turning radius anyway. Turn it on to flower them like everything else. The mechanism is general —
**a stroke color with alpha 0 throws nothing** — so in `Stroke List` mode you can silence any stroke by
hand by dragging its alpha to 0.

The stroke a flower belongs to comes from the tractor's **current waypoint**, not from counting pen
lifts. That matters: the logo's stars are ~0.3 m across against a 0.6–1.2 m look-ahead, so the tractor
leaps over eight of them bodily and a counter would end up eight strokes behind — painting the red
swoosh white. Related: those leaps used to skip the pen lift too, dragging mown connector lines across
the logo; the follower now checks the whole span of path it crossed, not just where it landed.

Those eight stars stay unmown either way — with a ~1 m minimum turning radius the tractor physically
cannot trace a 0.3 m circle. `Steering Mode = ExactPath` on the Tractor traces them exactly, at the
cost of the natural cornering. With `Flowers On Stars` off this is invisible in the flower logo.

Want per-stroke control? `Grass ▸ Auto-Assign Stroke Colors` bakes the same classification into an
editable `Stroke Colors` list and flips `Palette Mode` to `Stroke List`; recolor any entry by hand. Set
the mode back to `Logo Auto` to return to automatic. Also available: `Single Color`, and `Logo Texture`
(assign any readable NASA-logo image; sampled by position).

## 6. Dropping in audio later

Every sound moment already has an empty `AudioClip` slot — import a clip and drag it in:

| Sound | Where the slot is |
|---|---|
| Door servo open/close | `Airlock/DOOR_Outer` + `DOOR_Inner` → **Simple Door → Open/Close Clip** |
| Pressurization hiss (looped) | `Airlock` → **Airlock Controller → Hiss Clip** |
| Duck quack (on pat) | each `Duck_XX` → **Water Wanderer → Quack Clip** |
| Bite crunch | `Astronaut` → **Hand Action Controller → Bite Clip**, or per-object on **Eatable Object → Bite Clip** |
| Pat thump | per-object on **Pettable Object → Pat Clip** |

## 7. Decisions log (agreed 2026-07-24)

- Tractor: **Realistic** pure-pursuit steering by default (~0.2 m corner rounding on the 40 m logo);
  `Steering Mode = ExactPath` on the Tractor restores the waypoint-exact original.
- Steer wheels: `Steer Wheel Selection = Auto Front` picks the front pair by **measured hub position**
  at the start of every run. It has to be measured — this tractor's FBX was exported with frozen
  transforms, so all four axle *pivots* sit on the model origin and sorting them by position is a coin
  toss (which is how a rear wheel used to end up steering). `Auto Rear` and `Manual` are the escapes.
  The wheels also yaw **about their own hubs** now, not about the model origin, so they turn on the
  spot instead of swinging through an arc.
- Flowers: **NASA logo colors**, classified by stroke shape every run (§5). The swoosh is found by
  which strokes **overhang the circle** — the orbit ellipse is the one thing inside it, so an
  inside-the-circle test picks precisely the wrong one of the two. Star dots are mown but not flowered.
  Audio: **hooks only**.
- Environment: user's existing sky + ground kept; walkability via `Bake Simplified Collider`.
