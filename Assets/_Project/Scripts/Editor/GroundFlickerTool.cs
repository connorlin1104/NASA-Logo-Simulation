using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Finds and fixes the shimmer you see on a big flat surface when you look at it from far away or at a
    /// shallow angle — riding the elevator up and looking down at the field is the worst case in this
    /// scene, and it is exactly where the artefact shows.
    ///
    /// <b>What it actually is.</b> Two surfaces occupying the SAME plane. The depth buffer stores a
    /// rounded number, so for coplanar geometry "which one is in front" comes out of floating-point noise
    /// and flips per pixel, per frame, as the camera moves. It gets worse with distance because depth
    /// precision is spent close to the camera, and worse at a shallow angle because a single pixel then
    /// covers a long stretch of ground. Close up and looking straight down, the same two surfaces look
    /// perfectly fine — which is why this reads as "flickering from the elevator" rather than
    /// "the ground is broken".
    ///
    /// Two causes are detected, because they need different fixes:
    /// <list type="bullet">
    /// <item><b>Exact duplicates</b> — the same mesh drawn twice at the same world transform. Nudging
    /// them apart is wrong; one of them simply should not be drawing. The renderer is switched off (not
    /// the GameObject, which may carry colliders something else stands on).</item>
    /// <item><b>Near-coplanar sheets</b> — different meshes whose footprints overlap and whose heights
    /// differ by less than <see cref="_coplanarEpsilon"/>. Here separation IS the fix: the upper one is
    /// lifted just enough to win the depth test at every distance.</item>
    /// </list>
    ///
    /// Nothing is deleted and everything is undoable, so it is safe to run and look at the report.
    /// </summary>
    public sealed class GroundFlickerTool : EditorWindow
    {
        /// <summary>
        /// Height difference below which two overlapping horizontal sheets will fight. 5 mm is generous:
        /// a 24-bit depth buffer over a 1 km view distance resolves far less than that at range.
        /// </summary>
        float _coplanarEpsilon = 0.005f;

        /// <summary>How far to lift the upper sheet. Small enough not to show, large enough to win.</summary>
        float _separation = 0.02f;

        /// <summary>Ignore small props — a duplicated bolt does not cause a visible shimmer.</summary>
        float _minArea = 4f;

        bool _fixDuplicates = true;
        bool _fixCoplanar = true;

        readonly List<Stack> _stacks = new List<Stack>();
        readonly List<Pair> _pairs = new List<Pair>();
        bool _scanned;
        Vector2 _scroll;

        sealed class Stack
        {
            public readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
            public MeshRenderer Keep;
            public float Area;
        }

        sealed class Pair
        {
            public MeshRenderer Lower, Upper;
            public float Gap;
        }

        [MenuItem("Tools/NASA Sim/Fix/Stop The Ground Flickering")]
        public static void Open()
        {
            var w = GetWindow<GroundFlickerTool>(true, "Stop The Ground Flickering", true);
            w.minSize = new Vector2(560f, 460f);
            w.Scan();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Flicker on a big flat surface, seen from far away or at a shallow angle, is almost always " +
                "two surfaces sharing one plane. The depth test can't separate them, so it picks a winner " +
                "from rounding noise — and the noise changes every frame as you move.\n\n" +
                "Scan lists what it found. Nothing here deletes anything.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Scan the scene", GUILayout.Height(26f))) Scan();
                using (new EditorGUI.DisabledScope(!_scanned || (_stacks.Count == 0 && _pairs.Count == 0)))
                    if (GUILayout.Button("Fix what was found", GUILayout.Height(26f))) Apply();
            }

            EditorGUILayout.Space();
            _fixDuplicates = EditorGUILayout.ToggleLeft(
                new GUIContent("Switch off duplicate renderers",
                               "The same mesh drawn twice at the same place. One of the two is redundant."),
                _fixDuplicates);
            _fixCoplanar = EditorGUILayout.ToggleLeft(
                new GUIContent("Separate near-coplanar sheets",
                               "Different meshes stacked within a few millimetres. Lifts the upper one."),
                _fixCoplanar);

            using (new EditorGUI.IndentLevelScope())
            {
                _coplanarEpsilon = EditorGUILayout.Slider(
                    new GUIContent("Counts as coplanar (m)"), _coplanarEpsilon, 0.0005f, 0.05f);
                _separation = EditorGUILayout.Slider(
                    new GUIContent("Lift by (m)", "Big enough to win the depth test at range, small " +
                                   "enough that nothing visibly floats."), _separation, 0.002f, 0.2f);
                _minArea = EditorGUILayout.Slider(
                    new GUIContent("Ignore anything under (m²)", "Small props don't shimmer noticeably."),
                    _minArea, 0.25f, 100f);
            }

            EditorGUILayout.Space();
            if (!_scanned)
            {
                EditorGUILayout.LabelField("Not scanned yet.", EditorStyles.miniLabel);
                return;
            }

            if (_stacks.Count == 0 && _pairs.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No overlapping surfaces found, so the flicker is something else.\n\n" +
                    "The next suspects, in order: shadow acne (raise the Directional Light's Bias, or its " +
                    "Normal Bias, in the URP asset's shadow settings); a camera far-clip so large that " +
                    "depth precision is starved (Main Camera > Clipping Planes — raise Near before you " +
                    "lower Far, it buys far more); or a tiling texture aliasing at distance (turn on " +
                    "mipmaps and anisotropic filtering on the ground texture).", MessageType.Warning);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (_stacks.Count > 0)
            {
                EditorGUILayout.LabelField($"{_stacks.Count} duplicate stack(s)", EditorStyles.boldLabel);
                foreach (Stack s in _stacks)
                {
                    EditorGUILayout.LabelField($"  {s.Renderers.Count} copies · {s.Area:0} m² · " +
                                               $"'{s.Renderers[0].name}'", EditorStyles.miniBoldLabel);
                    foreach (MeshRenderer r in s.Renderers)
                    {
                        bool keep = r == s.Keep;
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(24f);
                            var style = new GUIStyle(EditorStyles.miniLabel)
                            { normal = { textColor = keep ? new Color(0.45f, 0.75f, 0.45f) : new Color(0.85f, 0.5f, 0.35f) } };
                            EditorGUILayout.LabelField(keep ? "keep" : "switch off", style, GUILayout.Width(70f));
                            if (GUILayout.Button(Path(r.transform), EditorStyles.miniLabel))
                                EditorGUIUtility.PingObject(r.gameObject);
                        }
                    }
                }
                EditorGUILayout.Space();
            }

            if (_pairs.Count > 0)
            {
                EditorGUILayout.LabelField($"{_pairs.Count} near-coplanar pair(s)", EditorStyles.boldLabel);
                foreach (Pair p in _pairs)
                {
                    EditorGUILayout.LabelField($"  {p.Gap * 1000f:0.0} mm apart", EditorStyles.miniBoldLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(24f);
                        if (GUILayout.Button("lift: " + Path(p.Upper.transform), EditorStyles.miniLabel))
                            EditorGUIUtility.PingObject(p.Upper.gameObject);
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(24f);
                        if (GUILayout.Button("over: " + Path(p.Lower.transform), EditorStyles.miniLabel))
                            EditorGUIUtility.PingObject(p.Lower.gameObject);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ scan

        void Scan()
        {
            _stacks.Clear();
            _pairs.Clear();
            _scanned = true;

            var candidates = new List<MeshRenderer>();
            foreach (MeshRenderer r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include))
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                Bounds b = r.bounds;
                if (b.size.x * b.size.z < _minArea) continue;
                candidates.Add(r);
            }

            FindDuplicates(candidates);
            FindCoplanar(candidates);
        }

        /// <summary>
        /// Same mesh, same world transform. Keyed on the transform rather than on the bounds so two
        /// genuinely different objects that happen to share a bounding box are never merged.
        /// </summary>
        void FindDuplicates(List<MeshRenderer> candidates)
        {
            // Keyed by the mesh ASSET itself rather than by an id, then by the world transform written out
            // to a fixed number of decimals — two objects only collide here if they really are the same
            // geometry in the same place.
            var byMesh = new Dictionary<Mesh, Dictionary<string, Stack>>();
            foreach (MeshRenderer r in candidates)
            {
                Mesh mesh = r.GetComponent<MeshFilter>().sharedMesh;
                Transform t = r.transform;
                Vector3 p = t.position, s = t.lossyScale;
                Vector3 e = t.rotation.eulerAngles;
                string key = $"{p.x:F3},{p.y:F3},{p.z:F3}|{s.x:F4},{s.y:F4},{s.z:F4}|{e.x:F1},{e.y:F1},{e.z:F1}";

                if (!byMesh.TryGetValue(mesh, out Dictionary<string, Stack> byPose))
                {
                    byPose = new Dictionary<string, Stack>();
                    byMesh[mesh] = byPose;
                }
                if (!byPose.TryGetValue(key, out Stack stack))
                {
                    stack = new Stack();
                    byPose[key] = stack;
                }
                stack.Renderers.Add(r);
            }

            foreach (Dictionary<string, Stack> byPose in byMesh.Values)
            {
                foreach (Stack s in byPose.Values)
                {
                    if (s.Renderers.Count < 2) continue;
                    Bounds b = s.Renderers[0].bounds;
                    s.Area = b.size.x * b.size.z;
                    s.Keep = PickKeeper(s.Renderers);
                    _stacks.Add(s);
                }
            }
            _stacks.Sort((a, b) => b.Area.CompareTo(a.Area));
        }

        /// <summary>
        /// Which copy stays. A hierarchy the author has already labelled as scrap is the obvious loser —
        /// in this scene one of the two ground planes lives under "Don_t_include__Hide", which says
        /// exactly what was meant; it was simply never switched off.
        /// </summary>
        static MeshRenderer PickKeeper(List<MeshRenderer> copies)
        {
            MeshRenderer best = null;
            int bestScore = int.MinValue;

            foreach (MeshRenderer r in copies)
            {
                int score = 0;
                string path = Path(r.transform).ToLowerInvariant();
                if (path.Contains("hide") || path.Contains("don_t") || path.Contains("dont") ||
                    path.Contains("ignore") || path.Contains("_old") || path.Contains("backup") ||
                    path.Contains("unused") || path.Contains("ph_"))
                    score -= 100;

                // A copy that also carries collision is load-bearing for something.
                if (r.GetComponent<Collider>() != null) score += 30;
                if (r.GetComponentInParent<Collider>() != null) score += 10;

                // All else equal, the shallower one in the hierarchy is the deliberate placement.
                score -= path.Split('/').Length;

                if (score > bestScore) { bestScore = score; best = r; }
            }
            return best;
        }

        /// <summary>
        /// Different meshes lying on the same level. Restricted to near-horizontal sheets: a stack of
        /// millimetre-apart vertical panels is a modelling choice, but two floors at the same height is
        /// always a bug you can see from across the map.
        /// </summary>
        void FindCoplanar(List<MeshRenderer> candidates)
        {
            var flats = new List<MeshRenderer>();
            var alreadyStacked = new HashSet<MeshRenderer>();
            foreach (Stack s in _stacks)
                foreach (MeshRenderer r in s.Renderers) alreadyStacked.Add(r);

            foreach (MeshRenderer r in candidates)
            {
                if (alreadyStacked.Contains(r)) continue;
                Bounds b = r.bounds;
                // Flat: thin in Y next to its footprint.
                if (b.size.y > Mathf.Min(b.size.x, b.size.z) * 0.05f + 0.05f) continue;
                flats.Add(r);
            }

            for (int i = 0; i < flats.Count; i++)
            {
                for (int j = i + 1; j < flats.Count; j++)
                {
                    Bounds a = flats[i].bounds, b = flats[j].bounds;
                    float gap = Mathf.Abs(a.center.y - b.center.y);
                    if (gap > _coplanarEpsilon) continue;
                    if (!OverlapsInXZ(a, b)) continue;

                    // The smaller sheet is the one that was laid ON the other, so it is the one to lift.
                    bool aSmaller = a.size.x * a.size.z <= b.size.x * b.size.z;
                    _pairs.Add(new Pair
                    {
                        Upper = aSmaller ? flats[i] : flats[j],
                        Lower = aSmaller ? flats[j] : flats[i],
                        Gap = gap,
                    });
                }
            }
        }

        static bool OverlapsInXZ(Bounds a, Bounds b)
        {
            // A shared edge is not an overlap; require real area in common.
            float x = Mathf.Min(a.max.x, b.max.x) - Mathf.Max(a.min.x, b.min.x);
            float z = Mathf.Min(a.max.z, b.max.z) - Mathf.Max(a.min.z, b.min.z);
            return x > 0.1f && z > 0.1f;
        }

        // ------------------------------------------------------------------ fix

        void Apply()
        {
            var log = new StringBuilder("[Flicker] ");
            int off = 0, lifted = 0;

            if (_fixDuplicates)
            {
                foreach (Stack s in _stacks)
                {
                    foreach (MeshRenderer r in s.Renderers)
                    {
                        if (r == s.Keep || r == null) continue;
                        Undo.RecordObject(r, "Stop Ground Flickering");
                        r.enabled = false;
                        EditorUtility.SetDirty(r);
                        off++;
                        log.Append($"\n  off: {Path(r.transform)}  ({s.Area:0} m², duplicate of " +
                                   $"{Path(s.Keep.transform)})");
                    }
                }
            }

            if (_fixCoplanar)
            {
                // One lift per object however many partners it fights, so a sheet overlapping three
                // others does not get raised three times.
                var moved = new HashSet<Transform>();
                foreach (Pair p in _pairs)
                {
                    if (p.Upper == null || !moved.Add(p.Upper.transform)) continue;
                    Transform t = p.Upper.transform;
                    Undo.RecordObject(t, "Stop Ground Flickering");
                    t.position += Vector3.up * _separation;
                    EditorUtility.SetDirty(t);
                    lifted++;
                    log.Append($"\n  lifted {_separation * 1000f:0} mm: {Path(t)}  (was {p.Gap * 1000f:0.0} " +
                               $"mm from {Path(p.Lower.transform)})");
                }
            }

            if (off == 0 && lifted == 0)
            {
                Debug.Log("[Flicker] Nothing changed — both fixes are switched off above.");
                return;
            }

            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            log.Insert(10, $"{off} duplicate renderer(s) switched off, {lifted} sheet(s) lifted clear.");
            log.Append("\n  Undo puts all of it back. Nothing was deleted: a switched-off renderer keeps " +
                       "its colliders, so anything standing on it still stands on it.");
            Debug.Log(log.ToString());

            Scan();
        }

        // ------------------------------------------------------------------ helpers

        static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }
    }
}
