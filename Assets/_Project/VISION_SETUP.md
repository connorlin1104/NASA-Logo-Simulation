# Full Vision — Unity Setup Runbook

Everything below is driven from **Tools ▸ NASA Sim ▸ …** menu items. Run the numbered steps **in
order** (or use the one-click chain in step 1 and then do only the two manual steps). All tools are
safe to re-run.

## 0. What you should see at the end

Spawn outside the biodome facing the entrance → press **E** at the orange button → outer door slides
up → step in → it seals behind you, gas floods the chamber (~4 s, real seconds even at 16× sim speed)
→ inner door opens → inside, the tractor arcs naturally through the logo throwing NASA-colored flowers
out the back while grass flattens under it → pick and eat fruit at the moat trees (**E**) → pat a duck
(**E**) → walk the moat bank (ripples, foam, refraction) → climb the spiral stairs to the balcony
without jumping → **C** for the free-fly camera to admire the finished flower logo → **R** restarts.

## 1. The click order

> **One-click:** `Tools ▸ NASA Sim ▸ Setup ▸ Run Full Vision Setup` runs steps 2–13 for you.
> Steps 14–15 always need your eyes.

| # | Menu item | What it does |
|---|---|---|
| 2 | `Setup ▸ Configure Layers & Physics` | Creates the **Player** (8) and **Interactable** (9) layers; puts the astronaut on Player |
| 3 | `Setup ▸ Normalize Astronaut Scale` | Fixes the root-scale-0.5 bug that halved the step-up (why stairs felt like walls) |
| 4 | `Models ▸ Reimport & Remap All Models` | Binds every FBX material slot to same-named project materials (see IMPORT_GUIDE Part 0) |
| 5 | `Biodome ▸ Fix Dome Glass` | The big one: transparent, double-sided BiodomeGlass material — the dome is now visible from INSIDE and see-through from outside |
| 6 | `Biodome ▸ Wire Colliders & Spawn Outside` | COL_/NOCOL_ colliders on the dome + tunnel, `SPAWN_Outside` marker, astronaut moved there |
| 7 | `Setup ▸ Add Interaction System & UI` | The "[E] …" prompt UI + proximity sensor + eat controller; camera defaults to first person |
| 8 | `Biodome ▸ Build Airlock In Tunnel` | Doors, buttons, chamber sensor, gas vents, full pressurize cycle |
| 9 | `Tractor ▸ Wire Steer Wheels From Axles` | Front axles steer visually with the new realistic driving model |
| 10 | `Water ▸ Create Water Body` (Moat preset) | The moat ring (r 27–31 m) around the logo: animated water + mud basin with collider |
| 11 | `Water ▸ Spawn Ducks & Fish` | 3 pattable ducks + 8 fish in the moat (re-run per pond, counts adjustable) |
| 12 | `Grass ▸ Scatter Grass Field`, then `Grass ▸ Add Grass Mowing Visual` | ~1,700 GPU-instanced clumps over the field; mower flattens them and throws logo-colored flowers |
| 13 | `Interactables ▸ Add Fruit Trees At Moat` | 4 placeholder trees on the outer bank; fruit grow at FRUIT_ markers in Play mode |
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
| R | Restart the mow (grass stands back up, flowers recycle) |

## 3. The time rule (for anyone adding scripts later)

`SimulationManager` drives `Time.timeScale` up to 16×. **Scaled** (fast-forwards): tractor, wheel
spin, grass flattening, flower ballistics, trail. **Unscaled** (always real seconds): astronaut,
cameras, doors, airlock + gas particles, interactions, fruit sequence, ducks/fish, water animation
(script-fed `_WaterTime`). New ambience the player watches → unscaled; new mow-spectacle → scaled.

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

## 5. Flower colors

Flowers use **per-stroke NASA colors**: `Grass ▸ Auto-Assign Stroke Colors` classifies the CSV's pen
strokes (near-logo-sized round stroke → **blue disc**, very wide flat stroke → **white orbit**, other
wide strokes → **red swoosh**, small strokes → **white letters**). It's a heuristic with an editable
result: select the tractor's **MowerBrush → Mowing Visual_Grass And Flowers → Stroke Colors** and
recolor any stroke that guessed wrong. Alternatives on the same component: `SingleColor`, or
`LogoTexture` (assign any readable NASA-logo image; sampled by position).

## 6. Dropping in audio later

Every sound moment already has an empty `AudioClip` slot — import a clip and drag it in:

| Sound | Where the slot is |
|---|---|
| Door servo open/close | `Airlock/DOOR_Outer` + `DOOR_Inner` → **Simple Door → Open/Close Clip** |
| Pressurization hiss (looped) | `Airlock` → **Airlock Controller → Hiss Clip** |
| Duck quack (on pat) | each `Duck_XX` → **Water Wanderer → Quack Clip** |
| Bite crunch | `Astronaut` → **Fruit Eat Controller → Bite Clip** |

## 7. Decisions log (agreed 2026-07-24)

- Tractor: **Realistic** pure-pursuit steering by default (~0.2 m corner rounding on the 40 m logo);
  `Steering Mode = ExactPath` on the Tractor restores the waypoint-exact original.
- Flowers: **NASA logo colors** per stroke (editable list). Audio: **hooks only**.
- Environment: user's existing sky + ground kept; walkability via `Bake Simplified Collider`.
