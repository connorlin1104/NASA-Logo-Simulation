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
| 7b | `Setup ▸ Hide The [E] Prompts (Recording)` | *(optional)* Silences the captions for a screen recording. Proximity and **E** still work exactly as before — there is just nothing on screen. **F1** toggles it mid-take; `Setup ▸ Show The [E] Prompts Again` puts them back |
| 8 | `Biodome ▸ Build Airlock In Tunnel` | Doors, buttons, chamber sensor, gas vents, full pressurize cycle |
| 9 | `Tractor ▸ Wire Steer Wheels From Axles` | *(optional — the follower now does this itself)* Fills Steer Wheels in at edit time so you can see the choice, and logs every wheel's measured hub position |
| 10 | `Water ▸ Add Square Moat Around Grass` | The square moat: a band of animated water running around the grass patch (auto-sized to it), mud trench + collider, stocked with 3 ducks and 8 fish. Resize it any time on the WaterBody component or by dragging its edges in the Scene view |
| 11 | `Water ▸ Add Round Pond Here` | A round pond at the Scene-view pivot, with its own ducks and fish. Run it as many times as you want ponds |
| 12 | `Grass ▸ Add Grass Mowing Visual` | Puts the grass/flower visual on the mower — the cut *is* the mark, nothing is painted on the floor |
| 13 | `Interactables ▸ Add Fruit Trees At Moat` | 4 placeholder trees on the outer bank; fruit grow at FRUIT_ markers in Play mode |
| 13b | `Grass ▸ Build Mowable Grass` | **The grass itself** — ~70,000 standing blades over the field, cut down by the tractor. Run it **last**: it probes the ground for what it may not grow through (see §3b) |
| — | `Grass ▸ Scatter Grass Tufts` / `Remove Scattered Tufts` | *Optional decoration only.* Modelled `GrassClump.fbx` tufts on top of the blade field. These are real GameObjects saved into the scene (1,500 of them is most of this scene's file size), so keep the density low — or remove them and let the blades do the work |
| — | `Interactables ▸ Make Selected Eatable` / `Make Selected Pettable` | **Any time:** select any object(s) → makes them edible/pattable (adds the component + an `InteractTrigger` child sized to the object). Same animation for every object; per-object bites, pace and respawn live on the component |
| 14 | **Manual:** select your ground model → `Environment ▸ Bake Simplified Collider` | Walkable collider baked from the uneven outside terrain (your visual ground stays untouched) |
| 15 | **Manual:** select each spiral staircase → `Biodome ▸ Add Spiral Stair Ramp` | Set Turns/Clockwise/Start Angle until the green wireframe hugs the treads, Build. "Disable tread colliders" stays ON — the smooth helicoid replaces the snaggy per-tread colliders. Settings are remembered per staircase. |

## 1b. The station tools (`Tools ▸ NASA Sim ▸ Station ▸ …`)

Windows for the imported `SettingEnvo` station. Each one has **buckets you drop groups into**, a
**build button**, and — the point of them — **draws what it will do in the Scene view**, so you check
the result by looking rather than by pressing Play. All are re-runnable and undoable.

| Window | What it does | What you see before Play |
|---|---|---|
| `Station ▸ Colliders & Stairs` | Collision for the labelled groups. **Fill from the scene** finds your own labels (the ones without a Maya `namespace:` prefix) and guesses a mode for each; check them, then Build | **Green wireframe** = ground you can walk on |
| `Station ▸ Sittable Chairs` | Chair meshes → sit in them with **E**. Seat and stand-out points are draggable child objects | **Blue seated figure** = exactly where and how you'll sit, plus a ring where you get out |
| `Station ▸ Hinged Doors` | Door meshes → swing open on **E**. Singles or two-leaf doubles; each gets a collider that travels with the panel and a prompt trigger that doesn't | **Orange outline** = shut, **green outline** = open, with the swept arc between them. Preview buttons swing them for real |
| `Station ▸ Elevator` | Car + two-panel sliding door + call buttons + a ride zone that carries you | **Green box** = bottom stop, **cyan box** = top stop, rails and travel distance between them; dashed outlines where the door panels slide to. Preview buttons park the car at the top for real |
| `Station ▸ Lighting` | The fix for "everything is dark" — see below | The Scene view is lit live; each lamp draws its reach |
| `Station ▸ Helmet Off Inside` | A helmet that lifts off the head, is **held out in front of you turning** for a beat, then gets tucked at the hip — **once**, the moment you step into a box you place yourself, and never goes back on — see below | **Blue sphere** = worn, **green sphere** = held up, **orange sphere** = carried, a **yellow path** through all three, and the trigger box as a solid green volume. Preview buttons park it at either end |
| `Station ▸ Pressure Chamber Gas` | Gas fills one chamber of the tunnel while you stand in it and vents when you leave. Lamps and gas hold **red** for 4 s then snap **green**. No button, no doors, nothing to get stuck in | **Blue box** = the room, shaded to 45 % so you can see where the gas will sit, labelled with the time to green |
| `Station ▸ Flying Drone` | The parked drone → **E** launches it, **E** again calls it home. It spins up, lifts off, flies a loop and lands itself | **Blue curve** = the route it will actually fly, **yellow spheres** = draggable markers, **green sphere** = the pad |
| `Station ▸ Monitor Screens` | Puts something else on the station's twenty screens: a live camera feed of the mow, your own picture, a flat colour, or off — see below | The list of screens it found, and what else uses each material (that is how it tells a screen from its frame) |
| `Plants ▸ Colour The Produce` | Gives every crop species its own colour — see below | Nothing to preview: it changes the materials, so the Scene view *is* the result |
| `Grass ▸ Tone Down The Grass` | Puts the lawn, the blades and the scattered tufts back to a green you can read the logo against — see below | Swatches of what all three materials hold right now, so you can see the neon one |
| `Biodome ▸ Dome Glass` | The dome, with its tint and opacity as numbers. Fixes "white from outside, invisible from inside" — see below | The shell renderers it matched, with their sizes, before you touch anything |
| `Fix ▸ Stop The Ground Flickering` | Finds surfaces sharing one plane, which is what makes the field shimmer from the elevator — see below | The list of duplicate stacks and near-coplanar pairs, each row clickable |
| `Interactables ▸ The Three Snacks (Carrot, Lettuce, Apple)` | **The one to use for a take.** Builds one of each at close-up quality, puts it beside the matching bed, and makes them the only eatable things in the scene — see below | How many other eatable objects will be turned back into scenery |
| `Interactables ▸ Make The Plants Eatable` | Wires the ~1,800 crops in the biodome for **[E] Eat**. Bulk scenery, *not* for close-ups — see below | A count per crop, and how many will actually be wired after the cap and the spacing |
| `Water ▸ Spawn Ducks, Fish && Lilypads` | Instantiates Duck/Fish/Lilypad.fbx whole, and rebuilds anything in the water that is only part of a model — see below | What is broken in the scene right now, and which FBXs it found |
| `Water ▸ Make Fountain` | A stone basin with real water, jets solved to land where you tell them, spill, splash, ripples and mist — see below | A live readout of the tilt, flight time, peak height and landing distance before you build |

**The four collider modes.** A station needs different collision in different places:

- **Walk surface** — *stairs and decks.* Samples the group from above and eases the tread profile into
  one smooth ramp. This is the fix for having to jump up your own staircase: a `CharacterController`
  refuses ledges taller than its Step Offset and slopes past its Slope Limit, so modelled steps read
  to it as a wall. It follows the model's own footprint, so a straight flight, the half-moon sweeps
  and a spiral all work the same way. Baked meshes land under a `StationColliders` root.
- **Solid mesh** — *the tube and the hatches.* An exact MeshCollider per part.
- **Box per part** — *handrails.* Fifty posts get fifty cheap boxes, not fifty mesh colliders.
- **Box hull** — one box round the lot.

**"Don't stand on"** (in Walk surface tuning) is the knob that matters most. Parts whose name contains
one of those words are left out of the sampling, so the surface passes *beneath* them. It is why the
half-moon platform gets a floor without its desks, monitors and water purifiers becoming walkable
furniture — and why handrails don't turn the stairs into a ramp over the banister.

**Hinging an imported door.** A hinge is a *line*, not a pivot — which is why `Station ▸ Hinged Doors`
exists instead of a checkbox. A Maya group's origin is wherever the modeller left it, usually the
middle of the panel or the world origin, so rotating the panel about its own transform makes it
pirouette through the wall. The tool measures the panel, lays the hinge down the vertical edge at one
end, and rotates about *that*. **Flip hinge** moves it to the other edge, **Flip swing** sends it the
other way, and you judge both from the outlines without pressing Play.

Two things it handles that bite otherwise. The prompt trigger is centred **on the hinge line**, the one
spot on a door that barely moves as it opens — anywhere else it swings away and takes "[E] Close the
door" with it. And a door needs a collider *on the panel*: a walk surface baked under `StationColliders`
or a solid-mesh collider covering the doorway stays exactly where it was while the door opens away from
it, so you get an open door you still can't walk through. The build warns you by name when it finds one.

**The helmet is a duplicate, on purpose.** The astronaut model is one skinned mesh with no separable
helmet, so nothing can be "taken off" it. What comes off is a second object riding the head — your own
helmet FBX if you have one (project asset or scene copy, either works), otherwise a placeholder visor
you can swap later like any other `PH_`. That placeholder is deliberately **transparent and
double-sided**: in first person the camera sits *inside* it, and an opaque single-sided bubble would be
culled to nothing from within — which would defeat the entire point of watching it lift away in front
of you. It comes off on **crossing into** the zone (edge-triggered, so **H** still works anywhere
without the zone arguing with you), the right arm reaches up for it using the same IK as the eat and
pet flourishes, and turning back in the doorway mid-move reverses it rather than being ignored.

**If it's already off when you press Play, the zone is too big.** A zone that contains the spawn point
means you start sealed in, so there is no crossing to watch. The window says which side of the line
you begin on, in metres, and warns when a row measures more than 120 m across — that is the whole
import, not one building. **Off already at spawn** stays unticked by default for the same reason.
**H** shows the move any time regardless.

**If it comes off and goes back on everywhere, it's one zone doing two jobs.** The pressurized parts of
this station are not in one place: the biodome is at the origin and the tunnel is 100 m west of it. A
single box asked to contain both contains the moon — which is how it ended up 184 m across, swallowing
the spawn point and most of the map. So the zone is a **list**, one box per building, and the helmet is
off inside any of them. Press **Find the biodome and the tunnel** and it fills the rows, skipping
anything over 120 m so "dome" can't match the import root that merely *contains* the dome.

The second half of that bug is subtler and worth knowing about, because it bites any trigger volume:
a boundary you are standing **on** is crossed and re-crossed by every dip in the ground under you, and
each crossing restarts a 1.5-second animation. The fix is the **boundary margin** — you now have to
travel 0.75 m *past* a wall before the crossing counts, in either direction, leaving a dead band twice
that wide around every face. The Scene view draws it as a second, smaller box inside each zone.

**Gas in the tunnel.** `Station ▸ Pressure Chamber Gas` fills one module while you stand in it. It is
deliberately *not* the airlock controller: that one owns two doors and refuses to run unless both are
wired and sealed, which is right for a working airlock and wrong for "make the middle of the tunnel feel
pressurized". Here the chamber is the only thing that exists and it cannot deadlock — being inside drives
the pressure toward 1, being outside drives it toward 0, whatever state it was in. Presence is **polled
every frame, not triggered**: a `CharacterController` only fires trigger callbacks while it is *moving*,
so a player who walks in and stands still would be missed by `OnTriggerEnter` and left in vacuum. The
fog's emitter box **grows from the floor up** with the pressure rather than just emitting harder, so the
gas visibly rises to fill the room instead of fading in everywhere at once, and the jets run only while
the pressure is actually changing — which is what makes the still moment at the end read as *pressurized*
rather than *still filling*. The tunnel's chambers are the groups ending in `Lock_GRP`; counting in from
the outside door, `CrewLock` is the first and `EquipLock` the second.

**Red, then green — never orange.** The status colour is **held at red for the whole cycle and flipped**
on completion rather than cross-faded, because a blend spends most of four seconds in muddy orange, which
reads as a broken lamp rather than as a room that is not safe yet — and it gives the answer away before
the cycle has finished. Two lamps, one high and one low, each reaching the far corner: a single point
light near the ceiling lights *the ceiling*, and "the room turns red" becomes "there is a red bulb up
there". The **gas itself is dyed** the same colour, which is what actually makes the room read red — the
vapour is the biggest thing in there. Default timing is 0.5 s sealing plus 3.5 s of gas, so it goes green
**4 seconds** after you step in, with a brief flare at the changeover so you catch it without watching
the lamp.

**The helmet comes off once, where you put the box.** This is the default and it is what a single
continuous take needs. `Station ▸ Helmet Off Inside` ▸ **Once, where I put the box** ▸ *Create the
trigger box*, then drag `HelmetTrigger` in the Scene view to wherever the take-off should happen — it
draws as a solid green volume labelled **STEP HERE**. The moment the astronaut is inside it, the helmet
comes off, and after that it is off for the rest of the run.

**Off means off.** There is exactly one latch and every route back goes through `PutOn()`, which refuses
while it is set — so leaving the volume, a chamber venting, a zone boundary and the **H** key are all
dead ends rather than four separate things to remember. `H` still works as a one-way shortcut: it can
bring the moment forward, never undo it. The latch is deliberately *not* serialized, or the second time
you pressed Play the helmet would never come off at all.

There is no hysteresis here and no edge detection, and that is the point: an edge can be crossed back the
other way, and a latch cannot.

**The one wait.** If you put the box *inside* a `PressureChamber`, that chamber still has to finish
pressurizing first — red for 4 s, green, then the helmet comes off 0.6 s later — because putting the
take-off in the airlock plainly means "after it has air in it". Put the box anywhere else and stepping in
is the whole trigger, with no delay. The window says which of the two you have, with the number. Untick
*Wait for the gas chamber* to make it instant everywhere.

Before you press Play, the window tells you **how many metres the astronaut starts from the box**, and
turns red if the spawn point is inside it — that is the one placement that silently ruins the shot, since
the helmet would come off in the first second before you had walked anywhere.

**The old reversible behaviour** is still there behind *Once* = off: pressurized zones, edge-triggered,
helmet back on when you leave, and a chamber you are standing in overriding the zone. Everything in the
next few paragraphs describes that mode. It is no good for recording, because it can always undo itself.

**And you can actually see it come off.** The move is three parts, not a fade: up off the head, **out in
front of the body where the camera can see it**, held there for 1.3 s turning and tipped forward so you
can see into the bowl, and only then tucked at the hip. In first person that lands in the middle of the
view. The presented position is computed from the **body's** forward rather than the head bone's, so it
stays put in front of you while you look around — anchoring it to the head would swing the helmet round
the room every time the mouse moved. The whole journey is one number in `[0, 1]` with the presented pose
at `0.5`, which is what makes turning back in the doorway *reverse* the move instead of being ignored.

One setting silently ruins all of this in zone mode: **Off already at spawn**. With it ticked and a zone
that contains the spawn point, the helmet is at the hip on frame one and you never see it at all. The
window flags that in red with a one-click fix. In *Once* mode the setting is forced off and greyed out
entirely — a one-time take-off that has already happened before frame one is the one thing it must never
do, so it is not left as something you could tick by accident.

**The drone.** `Station ▸ Flying Drone` puts the flight logic on a **pad beside the drone, not on the
drone**. That is the whole design: the interaction sensor walks *up* from a trigger collider to find the
interactable, so the trigger has to sit under the component — and if that were the drone, the trigger
would take off with it and there would be no way left to call it back. Same lesson as hanging a door's
prompt on its hinge. The route is a closed Catmull-Rom curve through draggable markers, so if it clips
the dome you move a marker instead of tuning a radius; the blue curve in the Scene view is sampled the
same way the drone flies it, so it is the real path and not a sketch. This drone is parked 26 m up, so
the window raycasts down, reports the drop, and puts the call button on the floor beneath it.

**Why the crops all looked the same.** The imported set ships with a *single* material shared by nearly
everything green: a carrot's root, a beetroot's root, a corn stalk and a tree's leaves are literally the
same material, so no amount of editing it can pull them apart — and it can't be edited anyway, because
materials embedded in an FBX are read-only sub-assets. `Plants ▸ Colour The Produce` recognises each
plant by name and gives every species its own material per part (root / leaf / flower), each one
**copied** from whatever the import assigned so shaders and texture maps survive and only the colour
moves. The copies are ordinary `.mat` files in `Materials/Produce`, so you can open any of them and
tune it by hand afterwards. **Count what matches** tells you what the names resolve to before you
commit, and **Put the imported colours back** really does — a `ProduceTint` object records what every
renderer had, by reference, so it survives renaming and reloading. Re-running lands on the *same*
`.mat` files each time; it used to call `GenerateUniqueAssetPath`, which left the previous set behind
as `Produce_Carrot_Main 1.mat`, ` 2`, ` 3` on every pass.

**Why the grass then went neon.** That window ends with a "boost the hand-made plant materials" pass,
and `Grass.mat` was on its list — so the lawn went from a muted `(0.20, 0.42, 0.16)` to a fully
saturated `(0.10, 0.67, 0.00)` and the mown logo stopped reading. Crops and grass want *opposite*
treatments: a crop is the thing you look at, so saturation helps it; grass is the surface the logo is
drawn **on**, so every bit of saturation spent on the green is contrast taken away from the cut. Grass
is off that list now and lives in `Grass ▸ Tone Down The Grass`, which moves all three grass materials
together — the ground plane, the blade tips/roots/mown colour, and the scattered tufts. While you are
there: those tufts have been **pure white since they were made**, which is most of "too bright". The
scatter copies the FBX's own untinted material so it can turn GPU instancing on, and the green fallback
in that copy only fires when the model has no material at all — which it does have.

**Why the field shimmers from the elevator.** Two surfaces sharing one plane. The depth buffer stores a
rounded number, so for coplanar geometry "which one is in front" comes out of floating-point noise and
flips per pixel, per frame, as the camera moves. It gets worse with distance, because depth precision is
spent close to the camera; and worse at a shallow angle, because one pixel then covers a long stretch of
ground. Close up and looking straight down the same two surfaces look perfect — which is why this reads
as "flickering from the elevator" rather than as "the ground is broken". In this scene it was the
**ground plane drawn twice**: `SettingEnvo/Floor/…GROUND` and `SettingEnvo/Don_t_include__Hide/…GROUND`,
same mesh, same world transform (y = −0.1805, scale 54.204), both switched on. The group name says what
was meant; the checkbox was never unticked. `Fix ▸ Stop The Ground Flickering` finds duplicate stacks and
switches off the redundant **renderer** — not the GameObject, which may carry colliders something is
standing on — and separately lifts near-coplanar sheets apart, which is the right fix when the meshes
genuinely differ. If it finds nothing, the next suspects in order are shadow acne (raise the light's
bias), a starved depth range (raise the camera's **Near** plane before you lower Far — it buys far more),
and a tiling texture aliasing at distance.

**The dome.** "I can't see it from inside" and "from outside it's just white" are the *same* bug seen
from two places: the shell's material is opaque and single-sided. A closed hull rendered single-sided
shows a camera inside it nothing but back faces, and back faces are culled — so the dome vanishes
entirely the moment you walk in, while reading as a solid wall from outside. Transparent + **Render Face
Both** fixes both at once. The reason the old one-click `Fix Dome Glass` had stopped working is worth
knowing: it looked for `COL_tunnel` / `NOCOL_Dome`, names the current import doesn't use — the shell is
now `newGreenHouse_2:_sh01_connor_anim_v002:Biosphere2`. It now matches on the **leaf** of the name with
the Maya namespace stripped, and refuses anything containing `airlock`, `door` or `hatch`, because every
airlock part in this scene carries `BiodomeAirlockDoor1` in its namespace and would otherwise turn to
glass along with the dome. Keep the opacity low: the dome is 55 m across and the far side draws over the
near side, so a reasonable-looking 30 % pane stacks up into fog. 8–12 % reads as glass.

**The monitors.** The screens are painted by a material embedded in `SettingEnvo.fbx`, and imported
materials are read-only sub-assets — there is no field on one to change. The only way to put something
else on a screen is to make a real material and reassign the slot, which is what
`Station ▸ Monitor Screens` does. Finding *which* slot without hard-coding "slot 1": each monitor has a
frame and a screen, and the tool counts how many **non-monitor** objects use each material. The frame
material also appears on the stands and the desks, so it has outside users; the screen material is used
by monitors and nothing else. Zero outside users means it is a screen — and the window shows you that
count before you commit. The interesting channel is the **live feed**: a camera in the world renders into
a RenderTexture that all twenty screens sample, framed overhead of the logo, chasing the tractor, or
bolted to the drone. It renders **on demand at 20 fps**, not every frame — the camera is left disabled
and driven by explicit `Render()` calls, because a monitor updating at 20 fps looks identical to one
updating at 165 and costs a fifth as much. The screens use an *Unlit* shader on purpose: a display emits
its own light and should not go dark when the biodome does.

**Ducks, fish and lilypads.** `Water ▸ Spawn Ducks, Fish & Lilypads` **instantiates `Duck.fbx` /
`Fish.fbx` / `Lilypad.fbx` whole**, and falls back to the `PH_` primitives only when they are not in the
project. The gameplay scripts live on a plain root and the model is a pure visual child either way,
which is why swapping one for the other costs no re-wiring.

The FBX is the duck. Nothing in the scene is treated as authoritative, because the scene is where it
went wrong:

- **Reading models out of the scene by NAME is what broke it.** Nineteen of the twenty nodes in
  `Duck.fbx` carry the Maya namespace (`Duck:mallard_body`); the bill is plain `mallard_bill`. A name
  test dropped it, the group above then contained one mesh that wasn't "ours", the "is this the whole
  model?" climb stopped dead at every individual limb, and the tool wired a bill, a wing and a foot into
  the moat as three separate ducks — each keeping its own tiny native scale. That is not a bug with a
  clever fix; matching by name cannot be made safe, and instantiating the asset needs no matching at all.
- **Anything that is not the whole model is thrown away and rebuilt.** Every animal in the water is
  compared against the mesh set a fresh instance would have, and one missing so much as a foot is
  destroyed and built again from the FBX. This is what *heals* a scene an older version already ran on —
  re-running the tool fixes the damage instead of carefully preserving it. The window names what it
  found before you press anything; `Rescan` recounts.
- **Loose pieces elsewhere in the scene are listed, and only listed.** Fragments left stranded outside
  any water get a **Delete the leftover pieces** button. A *complete* model somebody placed on purpose is
  never offered up — only things that draw part of a model and no more.
- **Adoption is opt-in and off.** Sweeping the scene for hand-placed animals is the thing that used to go
  wrong, so you have to ask for it. When you do, an adopted animal is **wrapped** into the same shape as
  a built one — `Duck_01` holding a `Duck_01_Model` child and an `InteractTrigger` — which is what lets
  the resize scale the model without dragging the trigger's radius with it, and any `PettableObject`
  already on it by hand is stripped so there aren't two interactables competing for the same **E**.
- **The sizes are sliders**, defaulting to roughly double life size (duck 1.1 m, fish 0.7 m, lilypad
  1.5 m). A real mallard is 0.55 m and reads as a speck across a 47 m moat; these are sized for the
  distance they are actually looked at from. *Resize what's already there* is on by default.
- **It puts the FBX's materials back.** The imported fish was wearing `Grass.mat` on its body and
  `Fruit.mat` on its fins: a bulk recolour swept over it while it sat loose in the scene. Freshly built
  instances never have the problem because they come straight off the asset; an adopted one does, so it
  is repaired renderer-by-renderer on the way in.

Ducks and fish both take a pat. Lilypads use the duck's float code — they sit on the wave the water
shader is *actually drawing* and lean with it — but turn on the spot and creep with a slow current
instead of paddling; a pad that ignored the swell while the ducks rode it would give the wave away.
Placeholders always lose when the population shrinks, and **Replace the placeholder ones** clears them
out entirely so the real models take over.

**The three snacks.** `Interactables ▸ The Three Snacks (Carrot, Lettuce, Apple)` builds one carrot, one
lettuce and one apple at close-up quality and drops each beside the nearest bed of its own crop. They are
the only things in the scene that answer **[E]**, and there are two separate reasons for that:

- **The greenhouse crops are decimated.** Fine in a bed forty metres away, terrible held against the
  visor. These three are the only produce the camera ever gets near, so they are the only three worth
  spending polygons on — about 9k triangles for the apple, 14k for the carrot, 12k for the lettuce, which
  is nothing for one object each.
- **The plant the greenhouse calls a unit is not what you would call one.** The node that was wired up as
  "an apple" is `appleArch10MeshGroup` — the whole trellis arch. Pressing **E** grabbed an entire arch,
  shrank it step by step and ate it. **Only these three are eatable** strips `EatableObject` off
  everything else in the scene so nothing can be picked up by mistake mid-take.

The meshes are generated rather than modelled: lathes for the apple and the carrot through a hand-tuned
profile, and one leaf function that draws the apple leaf, the carrot's feathery tops and the lettuce's
sixteen ruffled leaves. Normals come from finite differences of the position field rather than from
`RecalculateNormals`, which is what keeps a ridged carrot and the seam of a lathe smooth. The skins are
baked to PNG and imported normally, so they get sRGB and mipmaps like any other texture.

**The carrot is buried.** Its mesh puts y = 0 at the soil line with the whole root below it, so until you
pull it all you can see is the greens. The pull itself is a beat of its own: the hand presses *into* the
plant, the carrot gives a few millimetres the wrong way, it barely moves while the arm strains, and then
it lets go and flies — soil coming off it at the moment of release, not before. It is then held up near
the visor and turned for a second or so before the first bite. All of that is on the component:
**Pluck Direction / Distance / Seconds** and **Inspect Seconds** per object, **Pluck Load / Pluck Snap /
Inspect Spin** on the astronaut's `HandActionController`.

**Eating the crops (the bulk tool).** The greenhouse names every individual plant `<species><n>MeshGroup`,
with its parts as children (`…Main`, `…Leaf`, `…Flower`). That node is the unit — not the bed and not the
meshes — and there are about eighteen hundred of them.
`Interactables ▸ Make The Plants Eatable` wires them for **[E] Eat**, taking the species from the
material name (`Produce_Cornthin_Main` → corn), which survives anything anyone renames. Only the `…Main`
part is eaten, so pulling a carrot takes the carrot and leaves the greens standing; it grows back on a
timer, so a bed is never permanently stripped. Trees are off by default — a tree is scenery, and the
apples on it are their own plants. Unlike `Make Selected Eatable`, the trigger goes **on the plant node
rather than on an `InteractTrigger` child**: that convention exists so an object keeps its own layer and
colliders, and a MeshGroup node has neither, so at this scale it would only add eighteen hundred
GameObjects to guard against a conflict that cannot happen. Use the **cap** or the **spacing** if you
would rather have a scattering of edible plants than a whole field of them.

**The fountain.** `Water ▸ Make Fountain` builds a stone basin — a real `WaterBody`, so it uses the
project's wave shader and bank collider and can be resized afterwards from the Inspector like any other
water here — with jets, a spill off the tier, splash, ripples and mist. Two details are most of why it
reads as water rather than as sparks:

- **The arcs are solved, not eyeballed.** Given the jet speed and the spout's height above the pool,
  exactly one tilt lands the stream at a given radius, so the tool bisects for it and sets each jet's
  lifetime to the flight time it just computed. Droplets follow a real parabola and die *at the water
  line*, instead of fading out in mid-air wherever the lifetime happened to run out. The window prints
  the tilt, the time in the air, the peak height and the landing distance before you build, and warns
  when the speed you asked for cannot reach the radius you asked for.
- **Stretched billboards.** A droplet at speed is a streak, not a dot, so the jets and the spill render
  in Stretch mode scaled by velocity — fast water elongates, slow water at the top of an arc goes back to
  round. Ripples render as **horizontal** billboards, because the one thing a ripple must never do is
  stand up and face the camera.

The stone is deliberately **double-sided**: the basin the `WaterBody` generates is one surface — a floor
and a wall sloping up and out — so its outside is a back face, and single-sided you would see straight
through the fountain from anywhere below the rim. `Flow` on the `Fountain` component drives every
emitter, the light and the audio at once; drag it to 0 and it winds down over a second and a half. Tick
**Turn it on and off with E** if you want a prompt on it.

**Why the scene was dark**, and what `Station ▸ Lighting` does about it: one directional light was
lighting an entire moon base; the dome was **casting a shadow over its own contents** (a closed glass
shell with shadow casting on is a very expensive lampshade); and with no baked GI nothing bounces, so
every surface facing away from the sun got exactly the ambient colour. The tool brightens the sun,
adds a shadowless fill light from the opposite side, raises ambient to a gradient, switches shadow
*casting* (not rendering) off on the shell, hangs point lights over the groups you list, and raises
URP's per-object light cap from 4 — which is what otherwise makes objects mysteriously ignore a lamp
right next to them. If it's still dark, raise **Ambient brightness** first and **Fill intensity**
second: those lift everything at once, where another lamp only lifts one room.

## 2. Controls

| Key | Action |
|---|---|
| WASD + mouse | Walk / look (first person) |
| Shift | Run · **Space** jump (tap = low hop, hold = full float) |
| **E** | Interact — **swing a door open**, pick fruit, **eat a carrot**, pat duck **or fish**, **sit in a chair**, **call the elevator**, **launch the drone** (prompt appears within ~2 m) |
| **E** (seated) | Stand up again |
| **H** | Take the helmet off early. One-way — once it is off it stays off, and it comes off by itself when you step into the trigger box |
| **C** | Toggle first-person ↔ free-fly camera |
| Fly cam | WASD + mouse, **Space/Ctrl** up/down, **Shift** fast, **scroll** = fly speed (your "zoom") |
| 1–5, [ ] | Sim speed 1–16× (tractor/flowers only — you, doors, gas, water, ducks stay real-time) |
| R | Restart the mow (the whole field of grass springs back up, flowers recycle) |

## 3. The time rule (for anyone adding scripts later)

`SimulationManager` drives `Time.timeScale` up to 16×. **Scaled** (fast-forwards): tractor, wheel
spin, the cut itself, clippings, flower ballistics. **Unscaled** (always real seconds):
astronaut, cameras, doors, airlock + gas particles, interactions, fruit sequence, ducks/fish, water
animation (script-fed `_WaterTime`), grass wind and the push-aside (script-fed `_NasaGrassTime`),
sitting down, the sliding doors and the elevator.
New ambience the player watches → unscaled; new mow-spectacle → scaled.

Two related rules the station scripts rely on:

- **A disabled `CharacterController` means someone else owns the body.** `AstronautController` skips
  its entire update in that case, which is how `AstronautSitting` can hold the astronaut in a chair
  without gravity dragging them out of it. Anything else that wants to move the body directly should
  switch the controller off the same way, and hand it back with `Teleport`.
- **A `CharacterController` is never pushed by a moving collider.** Stand in a rising lift and Unity
  will leave you in mid-air while the floor climbs past. `ElevatorController` therefore moves riders
  by the same delta the car just travelled — anything else that moves a platform must do likewise.

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
ripple surface needs a dense even grid that's generated, not modelled. Maya supplies the *container*;
Unity supplies the water in it.

So the water is built from numbers, live: **every field on `WaterBody` rebuilds the sheet, the trench
and its collider the moment you change it**, in the editor, without pressing Play. Nothing is saved to
disk, so there is no mesh asset to keep in sync and no tool to re-run.

- **The square moat** — `Water ▸ Add Square Moat Around Grass`, or the `Create Water Body` window for
  the gap/width/corner-radius up front. Afterwards: `Inner Size` (the dry square it runs around),
  `Water Width`, `Corner Radius` on the component — or drag the cone handles on its banks in the Scene
  view. **Fit around grass** re-centres and re-sizes it to the grass patch with the gap you give it.
- **Round ponds outside it** — `Water ▸ Add Round Pond Here` drops one at the Scene-view pivot; run it
  again for the next one. Same for a `WATER_` locator exported from Maya (`Create Water Body` →
  *At Selected WATER_ Marker*).
- **Ducks, fish and lilypads** live on the water body — set the counts in its Inspector and hit *Apply
  population*, or use `Water ▸ Spawn Ducks, Fish & Lilypads` for the size sliders as well. Ducks and
  lilypads ride the actual wave drawn under them and lean with its slope; fish cruise the depth band
  with a tail waggle and the odd dart.
- **When the container model lands:** park the water body inside it, turn **Build Basin off** (the
  generated trench was only ever a stand-in for it), and pull the edges out until the sheet meets its
  walls. `Water ▸ Snap All Wildlife Into Water` puts everyone back in afterwards — resizing a pond can
  leave a duck standing on the lawn, though each one also checks itself on Start.

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
| Hinge creak / latch | each swinging door panel → **Hinged Door → Open/Close Clip** |
| Pressurization hiss (looped) | `Airlock` → **Airlock Controller → Hiss Clip** |
| Chamber gas hiss (looped) + the clunk when it seals | `PressureChambers/PressureChamber_…` → **Pressure Chamber → Hiss / Sealed Clip** |
| Rotor loop (pitched and faded with the throttle) | `DronePad` → **Drone Flight → Rotor Loop** |
| Duck quack (on pat) | each `Duck_XX` → **Water Wanderer → Quack Clip** |
| Running water (looped, volume follows Flow) | `FOUNTAIN_XX` → **Fountain → Running Clip** |
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
