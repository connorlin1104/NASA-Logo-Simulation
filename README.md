# NASA Logo Mowing Simulation

A Unity 6 (URP) interactive simulation where a tractor "mows" a lawn into the NASA
"meatball" logo by following waypoints loaded from a CSV.

## Status

**Milestone 1** — the tractor traces the full logo (orbit ring, blue circle, red vector,
and NASA letters) from CSV waypoints, laying a flat "mowed" trail. Movement is
kinematic/deterministic; the pen lifts between disconnected strokes; render layers keep
the letters drawing on top. It then parks in a corner so it doesn't block the finished logo.

## Project layout

- `Assets/_Project/Scripts/Runtime` — CSV loader, kinematic path follower, mowing visual (`IMowingVisual` + `MowingVisual_Trail`).
- `Assets/_Project/Scripts/Editor` — one-click scene builder: **Tools ▸ NASA Sim ▸ Build Test Scene**.
- `Assets/_Project/Data` — waypoint CSVs plus the Blender/Python exporters.
- `Assets/_Project/README.md` — full architecture notes.

## Waypoint pipeline

1. Design the logo as an SVG and import it into Blender (File ▸ Import ▸ SVG).
2. Run `Assets/_Project/Data/blender_export_waypoints.py` from Blender's Scripting tab.
   It resamples every curve, lifts the pen between contours, closes loops, tags render
   layers, seams the ring at the bottom, and appends a corner-park waypoint — writing
   `nasa_logo_clean.csv`.
3. Assign that CSV to the scene's `CsvWaypointLoader` and press Play.

## Requirements

Unity **6000.5.0f1** with the Universal Render Pipeline. Open the folder in Unity Hub.
