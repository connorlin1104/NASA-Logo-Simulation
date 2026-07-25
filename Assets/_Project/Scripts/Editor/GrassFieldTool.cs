using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Scatters GrassClump.fbx instances over the mowable field so the tractor has real grass to flatten
    /// (Tools &gt; NASA Sim &gt; Grass), wires the <see cref="MowingVisual_GrassAndFlowers"/> onto the
    /// mower, and auto-classifies the logo's pen strokes into NASA colors for the flower drops.
    ///
    /// Clumps stay linked prefab instances of the FBX, so re-exporting GrassClump.fbx from Maya updates
    /// the whole field. Their material is copied once to Assets/_Project/Materials/GrassClump.mat with
    /// GPU instancing ON (the embedded FBX material is immutable and can't have instancing enabled).
    /// </summary>
    public sealed class GrassFieldTool : EditorWindow
    {
        float density = 0.8f;        // clumps per m²
        float jitter = 0.45f;
        float clumpSize = 0.35f;     // target world footprint of one clump (m)
        bool useGrassPlaneBounds = true;
        float circleRadius = 21f;

        const string ClumpPath = "Assets/_Project/Models/GrassClump.fbx";
        const string ClumpMatPath = "Assets/_Project/Materials/GrassClump.mat";

        [MenuItem("Tools/NASA Sim/Grass/Scatter Grass Field")]
        public static void Open()
        {
            var w = GetWindow<GrassFieldTool>(true, "Scatter Grass Field", true);
            w.minSize = new Vector2(420f, 300f);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Scatters GrassClump.fbx over the mowable field (the green Grass plane by default). " +
                "Re-running clears and rescatters. Run the biodome collider wiring first so scatter " +
                "avoids stairs/pillars.", MessageType.Info);

            density = EditorGUILayout.Slider(new GUIContent("Density (clumps/m²)"), density, 0.1f, 3f);
            jitter = EditorGUILayout.Slider(new GUIContent("Jitter", "Random offset within each grid cell."), jitter, 0f, 0.5f);
            clumpSize = EditorGUILayout.Slider(new GUIContent("Clump size (m)"), clumpSize, 0.1f, 1.5f);
            useGrassPlaneBounds = EditorGUILayout.Toggle(
                new GUIContent("Fit to Grass plane", "Scatter over the green Grass plane's bounds; " +
                                                     "otherwise a circle around the logo."), useGrassPlaneBounds);
            using (new EditorGUI.DisabledScope(useGrassPlaneBounds))
                circleRadius = EditorGUILayout.Slider("Circle radius (m)", circleRadius, 5f, 40f);

            EditorGUILayout.Space();
            if (GUILayout.Button("Scatter / Rescatter", GUILayout.Height(30f)))
                Scatter(density, jitter, clumpSize, useGrassPlaneBounds, circleRadius);
            if (GUILayout.Button("Remove Grass Field"))
            {
                var root = GameObject.Find("GrassField");
                if (root != null) Undo.DestroyObjectImmediate(root);
            }
        }

        /// <summary>Programmatic default for the full-vision setup chain.</summary>
        public static void ScatterDefault() => Scatter(0.8f, 0.45f, 0.35f, true, 21f);

        public static void Scatter(float density, float jitter, float clumpSize,
                                   bool useGrassPlane, float circleRadius)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ClumpPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[GrassField] No clump model at {ClumpPath} — import GrassClump.fbx first.");
                return;
            }

            // Region: the green Grass plane's footprint, or a circle around the logo.
            Bounds region = default;
            bool circular = !useGrassPlane;
            Vector3 center = Vector3.zero;
            var grassPlane = GameObject.Find("Grass");
            if (useGrassPlane && grassPlane != null && grassPlane.TryGetComponent(out Renderer gr))
            {
                region = gr.bounds;
                center = region.center;
            }
            else
            {
                circular = true;
                var loader = Object.FindAnyObjectByType<CsvWaypointLoader>();
                if (loader != null && loader.csvFile != null)
                {
                    var path = loader.Parse(loader.csvFile);
                    if (path != null && !path.IsEmpty) center = path.Bounds.center;
                }
                region = new Bounds(center, new Vector3(circleRadius * 2f, 1f, circleRadius * 2f));
            }

            // Normalise the clump model to the requested footprint.
            float modelScale = 1f;
            var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                if (TryRendererBounds(probe, out Bounds pb))
                {
                    float largest = Mathf.Max(pb.size.x, pb.size.z);
                    if (largest > 1e-4f) modelScale = clumpSize / largest;
                }
            }
            finally { Object.DestroyImmediate(probe); }

            var root = GameObject.Find("GrassField");
            if (root == null)
            {
                root = new GameObject("GrassField");
                Undo.RegisterCreatedObjectUndo(root, "Scatter Grass Field");
            }
            for (int i = root.transform.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(root.transform.GetChild(i).gameObject);

            Material clumpMat = GetInstancedClumpMaterial(prefab);

            float spacing = 1f / Mathf.Sqrt(Mathf.Max(0.01f, density));
            var old = Random.state;
            Random.InitState(12345);          // deterministic rescatter
            int placed = 0, skipped = 0;
            int estimate = Mathf.CeilToInt(region.size.x / spacing) * Mathf.CeilToInt(region.size.z / spacing);

            try
            {
                int step = 0;
                for (float x = region.min.x; x <= region.max.x; x += spacing)
                    for (float z = region.min.z; z <= region.max.z; z += spacing)
                    {
                        if (++step % 200 == 0 &&
                            EditorUtility.DisplayCancelableProgressBar("Scattering grass",
                                $"{placed} clumps", step / (float)Mathf.Max(1, estimate)))
                            throw new System.OperationCanceledException();

                        Vector3 p = new Vector3(
                            x + Random.Range(-jitter, jitter) * spacing,
                            0f,
                            z + Random.Range(-jitter, jitter) * spacing);

                        if (circular &&
                            (p - new Vector3(center.x, 0f, center.z)).sqrMagnitude > circleRadius * circleRadius)
                            continue;

                        // Skip spots occupied by structures (stairs, pillars, dome ribs...). The probe
                        // floats 0.5 m up so the flat ground itself never triggers it.
                        if (Physics.CheckSphere(p + Vector3.up * 0.5f, 0.35f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            skipped++;
                            continue;
                        }

                        float y = 0f;
                        if (Physics.Raycast(p + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 20f,
                                            ~0, QueryTriggerInteraction.Ignore))
                            y = hit.point.y;

                        var clump = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                        clump.name = "GrassClump";
                        clump.transform.SetParent(root.transform, worldPositionStays: false);
                        clump.transform.SetPositionAndRotation(
                            new Vector3(p.x, y, p.z),
                            Quaternion.Euler(0f, Random.value * 360f, 0f));
                        clump.transform.localScale = Vector3.one * (modelScale * Random.Range(0.85f, 1.15f));

                        // Disable rather than destroy: removing a component from a LINKED prefab
                        // instance throws in edit mode the moment the FBX gains Generate Colliders.
                        foreach (var col in clump.GetComponentsInChildren<Collider>(true))
                            col.enabled = false;
                        if (clumpMat != null)
                            foreach (var r in clump.GetComponentsInChildren<Renderer>(true))
                            {
                                var mats = r.sharedMaterials;
                                for (int m = 0; m < mats.Length; m++) mats[m] = clumpMat;
                                r.sharedMaterials = mats;
                                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                            }
                        placed++;
                    }
            }
            catch (System.OperationCanceledException) { /* user cancelled — keep what was placed */ }
            finally
            {
                EditorUtility.ClearProgressBar();
                Random.state = old;
            }

            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log($"[GrassField] Scattered {placed} clumps ({skipped} spots skipped for structures). " +
                      "Now run Tools > NASA Sim > Grass > Add Grass Mowing Visual.", root);
        }

        /// <summary>
        /// The FBX's embedded material is immutable, so GPU instancing can't be enabled on it. Copy it
        /// once into a project material (which the M1 name-remap will also bind on future re-imports).
        /// </summary>
        static Material GetInstancedClumpMaterial(GameObject prefab)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(ClumpMatPath);
            if (existing != null) { existing.enableInstancing = true; return existing; }

            Material src = null;
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterial != null) { src = r.sharedMaterial; break; }
            }
            Material mat = src != null
                ? new Material(src)
                : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            mat.name = "GrassClump";
            if (src == null && mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", new Color(0.25f, 0.5f, 0.18f));
            mat.enableInstancing = true;
            AssetDatabase.CreateAsset(mat, ClumpMatPath);
            return mat;
        }

        // ------------------------------------------------------------------ mower wiring + colors

        [MenuItem("Tools/NASA Sim/Grass/Add Grass Mowing Visual")]
        public static void AddGrassMowingVisual()
        {
            var mower = Object.FindAnyObjectByType<MowerController>();
            if (mower == null)
            {
                Debug.LogWarning("[GrassField] No MowerController in the scene — build the tractor first.");
                return;
            }

            var vis = mower.GetComponent<MowingVisual_GrassAndFlowers>();
            if (vis == null) vis = mower.gameObject.AddComponent<MowingVisual_GrassAndFlowers>();

            var field = GameObject.Find("GrassField");
            vis.grassFieldRoot = field != null ? field.transform : null;
            var follower = Object.FindAnyObjectByType<TractorPathFollower>();
            vis.tractor = follower != null ? follower.transform : null;

            AutoAssignStrokeColors(vis);

            EditorUtility.SetDirty(vis);
            EditorSceneManager.MarkSceneDirty(mower.gameObject.scene);
            Debug.Log("[GrassField] Grass mowing visual wired beside the trail ribbon (MowerController " +
                      "forwards to both). Flowers use the auto-assigned per-stroke NASA colors — " +
                      "hand-edit Stroke Colors on the visual if any stroke guessed wrong.", vis);
        }

        [MenuItem("Tools/NASA Sim/Grass/Auto-Assign Stroke Colors")]
        public static void AutoAssignStrokeColorsMenu()
        {
            var vis = Object.FindAnyObjectByType<MowingVisual_GrassAndFlowers>();
            if (vis == null)
            {
                Debug.LogWarning("[GrassField] No MowingVisual_GrassAndFlowers in the scene — run " +
                                 "Add Grass Mowing Visual first.");
                return;
            }
            AutoAssignStrokeColors(vis);
            EditorUtility.SetDirty(vis);
        }

        /// <summary>
        /// Heuristic NASA-meatball classification of the CSV's pen strokes, in draw order (which is the
        /// order the runtime counts them): the near-logo-sized round stroke is the BLUE disc, a very wide
        /// but flat stroke is the WHITE orbit ellipse, other wide strokes are the RED swoosh, and small
        /// strokes are the WHITE letters. A guess, deliberately stored in an editable list.
        /// </summary>
        static void AutoAssignStrokeColors(MowingVisual_GrassAndFlowers vis)
        {
            var loader = Object.FindAnyObjectByType<CsvWaypointLoader>();
            if (loader == null || loader.csvFile == null)
            {
                Debug.LogWarning("[GrassField] No CsvWaypointLoader/CSV — stroke colors not assigned.");
                return;
            }
            var path = loader.Parse(loader.csvFile);
            if (path == null || path.IsEmpty) return;

            Color nasaBlue = new Color(0.043f, 0.239f, 0.569f, 1f);   // #0B3D91
            Color nasaRed = new Color(0.988f, 0.239f, 0.129f, 1f);    // #FC3D21

            var colors = new List<Color>();
            int blue = 0, red = 0, white = 0;
            Bounds logo = path.Bounds;

            bool inStroke = false;
            Bounds sb = default;
            void CloseStroke()
            {
                if (!inStroke) return;
                float w = sb.size.x / Mathf.Max(0.01f, logo.size.x);
                float h = sb.size.z / Mathf.Max(0.01f, logo.size.z);
                Color c;
                if (w > 0.85f && h > 0.85f) { c = nasaBlue; blue++; }          // the disc
                else if (w > 0.9f && h < 0.6f) { c = Color.white; white++; }   // the orbit ellipse
                else if (w > 0.55f || h > 0.55f) { c = nasaRed; red++; }       // the swoosh
                else { c = Color.white; white++; }                             // letters
                colors.Add(c);
                inStroke = false;
            }

            var pts = path.Points;
            for (int k = 1; k < pts.Count; k++)
            {
                if (pts[k].penDown)
                {
                    if (!inStroke)
                    {
                        sb = new Bounds(pts[k - 1].position, Vector3.zero);
                        inStroke = true;
                    }
                    sb.Encapsulate(pts[k].position);
                }
                else
                {
                    CloseStroke();
                }
            }
            CloseStroke();

            vis.strokeColors = colors;
            Debug.Log($"[GrassField] {colors.Count} strokes classified: {blue} blue (disc), {red} red " +
                      $"(swoosh), {white} white (letters/orbit). Hand-edit Stroke Colors if any look wrong.", vis);
        }

        static bool TryRendererBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }
    }
}
