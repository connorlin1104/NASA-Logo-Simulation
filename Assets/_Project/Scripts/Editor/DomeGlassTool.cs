using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// The dome, with its glass exposed as numbers instead of one fixed look.
    ///
    /// It exists because "the dome is white" and "the dome is invisible from inside" are the SAME bug
    /// seen from two places, and neither is obvious from the symptom. An opaque material makes it white;
    /// a single-sided one makes it disappear the moment you step in, because a closed hull shows a camera
    /// inside it nothing but back faces, and back faces are culled. So this window shows you what it
    /// found and lets you dial the result, rather than applying one hard-coded tint and hoping.
    ///
    /// <b>Opacity is the knob that matters.</b> The dome is 55 m across and every triangle of the far
    /// side draws on top of the near side, so what looks like a reasonable 30% pane in the inspector
    /// stacks up to a milky fog across the whole biodome. 8–12% is usually where it starts reading as
    /// glass rather than as weather.
    /// </summary>
    public sealed class DomeGlassTool : EditorWindow
    {
        Color _tint = BiodomeFixTools.DefaultTint;
        float _smoothness = 0.92f;
        bool _doubleSided = true;
        bool _includeSelection;

        List<Renderer> _shells = new List<Renderer>();
        Vector2 _scroll;

        [MenuItem("Tools/NASA Sim/Biodome/Dome Glass")]
        public static void Open()
        {
            var w = GetWindow<DomeGlassTool>(true, "Dome Glass", true);
            w.minSize = new Vector2(460f, 420f);
            w.Rescan();
        }

        void Rescan() => _shells = BiodomeFixTools.FindShellRenderers();

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "White from outside and gone from inside are one bug: the shell's material is opaque and " +
                "single-sided. Transparent + Render Face Both fixes both at once.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{_shells.Count} shell renderer(s) found", EditorStyles.boldLabel);
                if (GUILayout.Button("Rescan", GUILayout.Width(70f))) Rescan();
            }

            if (_shells.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing matched. A shell is recognised by its name (Maya namespaces stripped) " +
                    "containing 'dome' or 'biosphere' — and never 'airlock', 'door' or 'hatch', since " +
                    "every airlock part here carries 'BiodomeAirlockDoor1' in its namespace and would " +
                    "otherwise turn to glass too.\n\n" +
                    "Select the shell in the Hierarchy and tick the box below to force it.",
                    MessageType.Warning);
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(72f));
                foreach (Renderer r in _shells)
                {
                    if (r == null) continue;
                    Bounds b = r.bounds;
                    if (GUILayout.Button($"{r.name}   ({b.size.x:0} × {b.size.y:0} × {b.size.z:0} m, " +
                                         $"{r.sharedMaterials.Length} slot(s))", EditorStyles.miniLabel))
                        EditorGUIUtility.PingObject(r.gameObject);
                }
                EditorGUILayout.EndScrollView();
            }

            _includeSelection = EditorGUILayout.ToggleLeft(
                new GUIContent("Also glass whatever is selected",
                               "For a shell whose name the scan can't recognise."), _includeSelection);

            EditorGUILayout.Space();
            _tint = EditorGUILayout.ColorField(new GUIContent("Tint"), _tint, true, true, false);
            float alpha = EditorGUILayout.Slider(
                new GUIContent("Opacity", "How much the pane itself shows. The far side of a 55 m dome " +
                               "draws over the near side, so this stacks — 8–12% usually reads as glass."),
                _tint.a, 0f, 0.6f);
            _tint.a = alpha;
            _smoothness = EditorGUILayout.Slider(
                new GUIContent("Smoothness", "How sharply it catches the light. High reads as clean glass."),
                _smoothness, 0f, 1f);
            _doubleSided = EditorGUILayout.ToggleLeft(
                new GUIContent("Visible from inside (Render Face Both)",
                               "Untick only if you want the dome to vanish when you walk in — which is " +
                               "the bug you came here to fix."), _doubleSided);

            if (!_doubleSided)
                EditorGUILayout.HelpBox("With this off, the dome will be invisible from inside again.",
                                        MessageType.Warning);

            if (alpha > 0.25f)
                EditorGUILayout.HelpBox(
                    $"{alpha * 100f:0}% is high for a dome this size. Every pane between you and the " +
                    "horizon adds up, so the biodome will read as fogged rather than glazed.",
                    MessageType.Warning);

            EditorGUILayout.Space();
            if (GUILayout.Button("Apply", GUILayout.Height(30f))) Apply();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "Tip: the glass draws at queue 3100, above the water (3000) and the airlock gas (3050), " +
                "so it always composites last — correct both looking out and looking in.",
                EditorStyles.wordWrappedMiniLabel);
        }

        void Apply()
        {
            int slots = BiodomeFixTools.ApplyGlass(_tint, _smoothness, _doubleSided);

            if (_includeSelection)
            {
                Material glass = BiodomeFixTools.CreateOrRefreshGlassMaterial(_tint, _smoothness, _doubleSided);
                int forced = 0;
                foreach (GameObject go in Selection.gameObjects)
                {
                    if (go == null || EditorUtility.IsPersistent(go)) continue;
                    foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r is ParticleSystemRenderer) continue;
                        Undo.RecordObject(r, "Dome Glass");
                        var mats = r.sharedMaterials;
                        for (int i = 0; i < mats.Length; i++)
                            if (mats[i] != glass) { mats[i] = glass; forced++; }
                        r.sharedMaterials = mats;
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        EditorUtility.SetDirty(r);
                    }
                }
                if (forced > 0)
                    Debug.Log($"[BiodomeFix] Plus {forced} slot(s) on the current selection.");
                slots += forced;
            }

            if (slots == 0)
                Debug.Log("[BiodomeFix] Nothing changed — the shell already carries this exact material. " +
                          "Tweaking the tint above still updates it, because the material asset itself is " +
                          "rewritten on every Apply.");
            Rescan();
        }
    }
}
