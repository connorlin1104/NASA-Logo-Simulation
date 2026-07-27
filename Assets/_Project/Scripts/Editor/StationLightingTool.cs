using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Lights the station. The scene is dark for three reasons at once, and this fixes all three in one
    /// press:
    ///
    /// <list type="number">
    /// <item><b>One directional light lighting an entire moon base.</b> There is a sun and nothing else,
    /// so every interior is lit only by whatever ambient there is.</item>
    /// <item><b>The dome is casting a shadow over its own contents.</b> A closed glass shell with shadow
    /// casting left on puts everything underneath it in permanent shade — you have built a very
    /// expensive lampshade. Switching shadow casting off on the shell (not its rendering) is the single
    /// biggest change here.</item>
    /// <item><b>Nothing is bouncing.</b> There is no baked GI, so surfaces facing away from the sun
    /// receive exactly the ambient colour and no more. A gradient ambient plus a shadowless fill light
    /// stands in for the bounce at no real cost.</item>
    /// </list>
    ///
    /// Then it hangs point lights over the groups you list — the dome, the tunnel, the stair platforms —
    /// and each one draws its reach in the Scene view, so a dark corner is something you can SEE rather
    /// than something you discover in Play mode.
    ///
    /// Everything it creates lives under a "StationLights" root and is rebuilt from scratch each run.
    /// </summary>
    public sealed class StationLightingTool : EditorWindow
    {
        public enum AmbientStyle { Gradient, Flat, FromSkybox }

        const string GeneratedRootName = "StationLights";

        [System.Serializable]
        public class Area
        {
            public GameObject target;
            [Min(1)] public int lights = 4;
            public float brightness = 1f;
        }

        // ---- sun ----
        [SerializeField] bool _doSun = true;
        [SerializeField] float _sunIntensity = 1.9f;
        [SerializeField] Color _sunColor = new Color(1f, 0.96f, 0.90f);
        [SerializeField] float _sunElevation = 38f;
        [SerializeField] float _sunAzimuth = 40f;
        [SerializeField] bool _sunShadows = true;

        // ---- fill ----
        [SerializeField] bool _doFill = true;
        [SerializeField] float _fillIntensity = 0.45f;
        [SerializeField] Color _fillColor = new Color(0.80f, 0.86f, 1f);

        // ---- ambient ----
        [SerializeField] bool _doAmbient = true;
        [SerializeField] AmbientStyle _ambientStyle = AmbientStyle.Gradient;
        [SerializeField] float _ambientBrightness = 0.42f;
        [SerializeField] Color _ambientTint = new Color(1f, 0.98f, 0.94f);

        // ---- shadow casters ----
        [SerializeField] bool _doShadowFix = true;
        [SerializeField] string _noShadowWords = "dome, glass, biosphere, canopy, window, shell";

        // ---- interior lights ----
        [SerializeField] List<Area> _areas = new List<Area>();
        [SerializeField] float _mountHeight = 5f;
        [SerializeField] float _lightIntensity = 4f;
        [SerializeField] float _rangeScale = 1f;
        [SerializeField] Color _lightColor = new Color(1f, 0.97f, 0.92f);
        [SerializeField] bool _lightShadows;
        [SerializeField] bool _raiseUrpLightLimit = true;

        Vector2 _scroll;

        /// <summary>Groups worth hanging lights over, and how many each usually wants.</summary>
        static readonly (string word, int lights)[] AutoAreas =
        {
            ("dome", 6),
            ("tunnel", 3),
            ("stairlooking", 3),
            ("plantbins", 4),
            ("balcony", 2),
            ("elevator", 1),
        };

        [MenuItem("Tools/NASA Sim/Station/Lighting")]
        public static void Open()
        {
            var window = GetWindow<StationLightingTool>(true, "Station Lighting", true);
            window.minSize = new Vector2(540f, 620f);
            if (window._areas.Count == 0) window.AutoFill(silent: true);
            window.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Everything dark? Press the big button. It brightens the sun, adds a shadowless fill, " +
                "raises the ambient, stops the dome shading its own interior, and hangs lights over the " +
                "groups listed below.\n\n" +
                "The Scene view is lit live, so you see the result immediately — no Play needed. Each " +
                "light also draws how far it reaches, so you can spot the gaps.",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // ---------------------------------------------------------- sun
            _doSun = EditorGUILayout.BeginToggleGroup("Sun (the directional light)", _doSun);
            EditorGUI.indentLevel++;
            _sunIntensity = EditorGUILayout.Slider(
                new GUIContent("Intensity", "1 is Unity's default and reads dim in a big scene. " +
                                            "About 2 is a bright lunar noon."),
                _sunIntensity, 0f, 6f);
            _sunColor = EditorGUILayout.ColorField(new GUIContent("Colour"), _sunColor);
            _sunElevation = EditorGUILayout.Slider(
                new GUIContent("Elevation", "Height above the horizon. Low = long dramatic shadows; " +
                                            "high = flat, even light."),
                _sunElevation, 2f, 89f);
            _sunAzimuth = EditorGUILayout.Slider(new GUIContent("Direction"), _sunAzimuth, 0f, 360f);
            _sunShadows = EditorGUILayout.Toggle(
                new GUIContent("Cast shadows", "Off is the fastest way to brighten everything, at the " +
                                               "cost of the hard vacuum shadows."),
                _sunShadows);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

            // ---------------------------------------------------------- fill
            _doFill = EditorGUILayout.BeginToggleGroup("Fill light (stands in for bounced light)", _doFill);
            EditorGUI.indentLevel++;
            _fillIntensity = EditorGUILayout.Slider(
                new GUIContent("Intensity", "A second directional light from the opposite side, with no " +
                                            "shadows. This is what stops the unlit faces of things going " +
                                            "flat black."),
                _fillIntensity, 0f, 2f);
            _fillColor = EditorGUILayout.ColorField(new GUIContent("Colour"), _fillColor);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

            // ---------------------------------------------------------- ambient
            _doAmbient = EditorGUILayout.BeginToggleGroup("Ambient (the light with no source)", _doAmbient);
            EditorGUI.indentLevel++;
            _ambientStyle = (AmbientStyle)EditorGUILayout.EnumPopup(
                new GUIContent("Style", "Gradient is brighter above than below, which reads as a lit " +
                                        "room. From Skybox uses the sky itself — correct outdoors, near " +
                                        "black once the vacuum skybox is in."),
                _ambientStyle);
            using (new EditorGUI.DisabledScope(_ambientStyle == AmbientStyle.FromSkybox))
            {
                _ambientBrightness = EditorGUILayout.Slider(
                    new GUIContent("Brightness", "The one slider that lifts EVERY dark corner at once. " +
                                                 "Too high and the scene goes flat and milky."),
                    _ambientBrightness, 0f, 1.5f);
                _ambientTint = EditorGUILayout.ColorField(new GUIContent("Tint"), _ambientTint);
            }
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

            // ---------------------------------------------------------- shadow casters
            _doShadowFix = EditorGUILayout.BeginToggleGroup(
                "Stop the dome shading its own interior", _doShadowFix);
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(
                new GUIContent("Parts whose name contains", "These stop CASTING shadows. They still " +
                                                            "render exactly as before."));
            _noShadowWords = EditorGUILayout.TextArea(_noShadowWords, GUILayout.Height(32f));
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

            // ---------------------------------------------------------- interior lights
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Lights inside", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fill from the scene")) AutoFill(silent: false);
                if (GUILayout.Button("Add empty row")) _areas.Add(new Area());
                if (GUILayout.Button("Clear list")) _areas.Clear();
            }

            for (int i = 0; i < _areas.Count; i++)
            {
                var area = _areas[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    area.target = (GameObject)EditorGUILayout.ObjectField(
                        area.target, typeof(GameObject), true, GUILayout.MinWidth(160f));
                    area.lights = Mathf.Max(1, EditorGUILayout.IntField(area.lights, GUILayout.Width(38f)));
                    area.brightness = EditorGUILayout.Slider(area.brightness, 0.1f, 4f,
                                                             GUILayout.MinWidth(90f));
                    if (GUILayout.Button("×", GUILayout.Width(22f)))
                    {
                        _areas.RemoveAt(i);
                        i--;
                    }
                }
            }
            EditorGUILayout.LabelField(" ", "group · how many lights · brightness", EditorStyles.miniLabel);

            EditorGUI.indentLevel++;
            _mountHeight = EditorGUILayout.Slider(
                new GUIContent("Mounting height (m)", "How high above the group's FLOOR the lights hang."),
                _mountHeight, 0.5f, 25f);
            _lightIntensity = EditorGUILayout.Slider(
                new GUIContent("Intensity"), _lightIntensity, 0.2f, 30f);
            _rangeScale = EditorGUILayout.Slider(
                new GUIContent("Reach", "Multiplies the range worked out from each group's size."),
                _rangeScale, 0.25f, 3f);
            _lightColor = EditorGUILayout.ColorField(new GUIContent("Colour"), _lightColor);
            _lightShadows = EditorGUILayout.Toggle(
                new GUIContent("Cast shadows", "Off by default: a dozen shadow-casting point lights is a " +
                                               "real cost, and their shadows mostly reintroduce the dark " +
                                               "you are here to remove."),
                _lightShadows);
            _raiseUrpLightLimit = EditorGUILayout.Toggle(
                new GUIContent("Raise URP's light limit", "URP caps how many extra lights may hit one " +
                                                          "object (4 by default). With lights overlapping " +
                                                          "inside the dome, that cap is what makes some " +
                                                          "objects mysteriously ignore a nearby lamp."),
                _raiseUrpLightLimit);
            EditorGUI.indentLevel--;

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            if (GUILayout.Button("Light the station", GUILayout.Height(34f))) Build();
            if (GUILayout.Button("Remove the generated lights")) RemoveGenerated();
        }

        // ================================================================== auto-fill

        void AutoFill(bool silent)
        {
            var found = new List<(Transform t, int lights)>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t.GetComponentInChildren<Renderer>(true) == null) continue;
                string n = t.name.ToLowerInvariant();
                foreach (var (word, lights) in AutoAreas)
                {
                    if (!n.Contains(word)) continue;
                    found.Add((t, lights));
                    break;
                }
            }

            // Keep the outermost of any nest: lighting a group twice just doubles the lamps.
            var kept = new List<(Transform t, int lights)>();
            foreach (var candidate in found)
            {
                bool insideAnother = false;
                foreach (var other in found)
                {
                    if (other.t == candidate.t) continue;
                    if (candidate.t.IsChildOf(other.t)) { insideAnother = true; break; }
                }
                if (!insideAnother) kept.Add(candidate);
            }

            int added = 0;
            foreach (var (t, lights) in kept)
            {
                if (_areas.Exists(a => a.target == t.gameObject)) continue;
                _areas.Add(new Area { target = t.gameObject, lights = lights });
                added++;
            }

            if (silent) return;
            Debug.Log(added == 0
                ? "[Lighting] No new areas found — everything recognised is already listed."
                : $"[Lighting] Added {added} area(s) to light.");
        }

        // ================================================================== build

        void Build()
        {
            var log = new List<string>();

            if (_doSun) log.Add(ApplySun());
            if (_doAmbient) log.Add(ApplyAmbient());
            if (_doShadowFix) log.Add(StopShellsCastingShadows());
            if (_raiseUrpLightLimit) log.Add(RaiseUrpLightLimit());

            var root = StationBuild.GeneratedRoot(GeneratedRootName, clearChildren: true);
            if (_doFill) log.Add(BuildFill(root.transform));
            log.Add(BuildAreaLights(root.transform));

            if (root.transform.childCount == 0) Undo.DestroyObjectImmediate(root);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Lighting] Done.\n  " + string.Join("\n  ", log) +
                      "\n  Look at the Scene view — it is lit live. Still too dark? Raise Ambient " +
                      "brightness first, then Fill intensity; they lift everything at once, where an " +
                      "extra lamp only lifts one room.");
        }

        string ApplySun()
        {
            Light sun = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (l.type != LightType.Directional) continue;
                if (l.GetComponentInParent<StationLightMarker>() != null) continue;   // our own fill light
                if (l.GetComponent<StationLightMarker>() != null) continue;
                sun = l;
                break;
            }

            bool created = false;
            if (sun == null)
            {
                var go = new GameObject("Directional Light");
                Undo.RegisterCreatedObjectUndo(go, "Create Sun");
                sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
                created = true;
            }

            Undo.RecordObject(sun, "Light the station");
            Undo.RecordObject(sun.transform, "Light the station");
            sun.intensity = _sunIntensity;
            sun.color = _sunColor;
            sun.shadows = _sunShadows ? LightShadows.Soft : LightShadows.None;
            sun.transform.rotation = Quaternion.Euler(_sunElevation, _sunAzimuth, 0f);
            RenderSettings.sun = sun;
            EditorUtility.SetDirty(sun);

            return $"Sun: intensity {_sunIntensity:0.0}, {_sunElevation:0}° up, shadows " +
                   (_sunShadows ? "on" : "off") + (created ? " (created — there wasn't one)" : string.Empty);
        }

        string BuildFill(Transform root)
        {
            var go = new GameObject("FillLight");
            Undo.RegisterCreatedObjectUndo(go, "Light the station");
            go.transform.SetParent(root, worldPositionStays: false);
            // From the opposite side and steeper, so it fills exactly the faces the sun leaves black.
            go.transform.rotation = Quaternion.Euler(55f, _sunAzimuth + 180f, 0f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = _fillIntensity;
            light.color = _fillColor;
            light.shadows = LightShadows.None;      // a fill that casts shadows is not a fill

            var marker = go.AddComponent<StationLightMarker>();
            marker.area = "whole scene";
            marker.showRange = false;

            return $"Fill light: {_fillIntensity:0.00}, no shadows, aimed opposite the sun";
        }

        string BuildAreaLights(Transform root)
        {
            int placed = 0, skipped = 0;

            foreach (var area in _areas)
            {
                if (area == null || area.target == null) { skipped++; continue; }
                if (!StationBuild.TryRendererBounds(area.target, out Bounds b)) { skipped++; continue; }

                float range = Mathf.Clamp(Mathf.Max(b.size.x, b.size.z) * 0.6f, 5f, 90f) * _rangeScale;
                float y = b.min.y + _mountHeight;

                int count = Mathf.Max(1, area.lights);
                int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
                int rows = Mathf.CeilToInt(count / (float)cols);

                var group = new GameObject(SafeName(area.target.name));
                Undo.RegisterCreatedObjectUndo(group, "Light the station");
                group.transform.SetParent(root, worldPositionStays: false);

                int made = 0;
                for (int r = 0; r < rows && made < count; r++)
                    for (int c = 0; c < cols && made < count; c++)
                    {
                        // Inset from the edges: lights on the boundary throw half their light at nothing.
                        float u = cols == 1 ? 0.5f : Mathf.Lerp(0.25f, 0.75f, c / (cols - 1f));
                        float v = rows == 1 ? 0.5f : Mathf.Lerp(0.25f, 0.75f, r / (rows - 1f));
                        var pos = new Vector3(Mathf.Lerp(b.min.x, b.max.x, u), y,
                                              Mathf.Lerp(b.min.z, b.max.z, v));

                        var go = new GameObject($"Light_{made + 1}");
                        Undo.RegisterCreatedObjectUndo(go, "Light the station");
                        go.transform.SetParent(group.transform, worldPositionStays: false);
                        go.transform.position = pos;

                        var light = go.AddComponent<Light>();
                        light.type = LightType.Point;
                        light.range = range;
                        light.intensity = _lightIntensity * Mathf.Max(0.01f, area.brightness);
                        light.color = _lightColor;
                        light.shadows = _lightShadows ? LightShadows.Soft : LightShadows.None;

                        var marker = go.AddComponent<StationLightMarker>();
                        marker.area = area.target.name;

                        made++;
                        placed++;
                    }
            }

            return $"Interior lights: {placed} point light(s) over {_areas.Count - skipped} group(s)" +
                   (skipped > 0 ? $" ({skipped} row(s) skipped — empty, or nothing to measure)" : string.Empty);
        }

        string ApplyAmbient()
        {
            switch (_ambientStyle)
            {
                case AmbientStyle.FromSkybox:
                    RenderSettings.ambientMode = AmbientMode.Skybox;
                    RenderSettings.ambientIntensity = Mathf.Max(0f, _ambientBrightness * 2f);
                    return $"Ambient: from the skybox at {RenderSettings.ambientIntensity:0.00}";

                case AmbientStyle.Flat:
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = _ambientTint * _ambientBrightness;
                    return $"Ambient: flat, brightness {_ambientBrightness:0.00}";

                default:
                    // Brighter from above than below — the cheap read of "this is a lit room" that a flat
                    // ambient can't give, because a flat one lights the undersides of things equally.
                    RenderSettings.ambientMode = AmbientMode.Trilight;
                    RenderSettings.ambientSkyColor = _ambientTint * _ambientBrightness;
                    RenderSettings.ambientEquatorColor = _ambientTint * (_ambientBrightness * 0.62f);
                    RenderSettings.ambientGroundColor = _ambientTint * (_ambientBrightness * 0.22f);
                    return $"Ambient: gradient, brightness {_ambientBrightness:0.00}";
            }
        }

        string StopShellsCastingShadows()
        {
            var words = new List<string>();
            foreach (string raw in (_noShadowWords ?? string.Empty).Split(','))
            {
                string w = raw.Trim().ToLowerInvariant();
                if (w.Length > 0) words.Add(w);
            }
            if (words.Count == 0) return "Shadow casters: nothing listed, left alone";

            int changed = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                if (r is ParticleSystemRenderer) continue;
                if (r.shadowCastingMode == ShadowCastingMode.Off) continue;

                // Match the part OR any group it sits in: a dome is a thousand panels called polySurface*,
                // and only their parent says "Dome".
                bool match = false;
                for (Transform t = r.transform; t != null && !match; t = t.parent)
                {
                    string n = t.name.ToLowerInvariant();
                    foreach (string w in words) if (n.Contains(w)) { match = true; break; }
                }
                if (!match) continue;

                Undo.RecordObject(r, "Light the station");
                r.shadowCastingMode = ShadowCastingMode.Off;
                EditorUtility.SetDirty(r);
                changed++;
            }

            return $"Shadow casting switched off on {changed} shell renderer(s) — they still render, they " +
                   "just stop shading what's underneath them";
        }

        /// <summary>
        /// Raise URP's per-object additional-light cap. Done through SerializedObject rather than the
        /// typed API so this compiles against any URP version — the property name has outlived several.
        /// </summary>
        string RaiseUrpLightLimit()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return "URP light limit: no render pipeline asset assigned, skipped";

            var so = new SerializedObject(asset);
            var prop = so.FindProperty("m_AdditionalLightsPerObjectLimit");
            if (prop == null) return "URP light limit: property not found on this pipeline, skipped";
            if (prop.intValue >= 8) return $"URP light limit: already {prop.intValue}";

            int was = prop.intValue;
            prop.intValue = 8;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            return $"URP light limit: {was} → 8 lights per object";
        }

        void RemoveGenerated()
        {
            var root = GameObject.Find(GeneratedRootName);
            if (root == null)
            {
                Debug.Log("[Lighting] Nothing generated to remove.");
                return;
            }
            Undo.DestroyObjectImmediate(root);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Lighting] Generated lights removed. The sun, the ambient settings and the shadow " +
                      "casting change are scene-wide, not objects — undo (Ctrl+Z) to put those back.");
        }

        static string SafeName(string s) => s.Replace(":", "_");
    }
}
