using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Everything under Tools &gt; NASA Sim &gt; Grass.
    ///
    /// <b>Build Mowable Grass</b> is the one that matters: it plants the field of standing blades
    /// (<see cref="MowableGrass"/>) the tractor actually cuts down. Blades are generated from a seed at
    /// load, never serialized, so the field costs nothing in the scene file however dense it is.
    ///
    /// The window below scatters GrassClump.fbx instances — the modelled TUFTS that sit on top of the
    /// blade field for variety. They are real GameObjects (1500 of them is already most of this scene's
    /// YAML), so they are optional decoration now rather than the grass itself; Remove Scattered Clumps
    /// takes them out again. Clumps stay linked prefab instances, so re-exporting GrassClump.fbx from Maya
    /// updates the whole field, and their material is copied once to
    /// Assets/_Project/Materials/GrassClump.mat with GPU instancing ON (the embedded FBX material is
    /// immutable and can't have instancing enabled).
    ///
    /// This class also wires <see cref="MowingVisual_GrassAndFlowers"/> onto the mower and auto-classifies
    /// the logo's pen strokes into NASA colors for the flower drops.
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
        const string BladeMatPath = "Assets/_Project/Materials/GrassBlades.mat";

        [MenuItem("Tools/NASA Sim/Grass/Scatter Grass Tufts")]
        public static void Open()
        {
            var w = GetWindow<GrassFieldTool>(true, "Scatter Grass Tufts", true);
            w.minSize = new Vector2(420f, 300f);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Optional decoration. The grass the tractor MOWS is the blade field — " +
                "Tools > NASA Sim > Grass > Build Mowable Grass.\n\n" +
                "This scatters GrassClump.fbx tufts on top of it for extra silhouette. They are real " +
                "GameObjects and get saved into the scene, so keep the density low. Re-running clears and " +
                "rescatters; run the biodome collider wiring first so scatter avoids stairs/pillars.",
                MessageType.Info);

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
            if (GUILayout.Button("Remove Scattered Tufts"))
                RemoveScatteredClumps();
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

        // ------------------------------------------------------------------ the blade field

        /// <summary>
        /// Plants the field of standing grass the tractor cuts down, sizes it to the biodome's Grass plane,
        /// gives it a project material you can tune, and hands it to the mowing visual. Safe to re-run —
        /// it finds the existing object and rebuilds in place.
        /// </summary>
        [MenuItem("Tools/NASA Sim/Grass/Build Mowable Grass")]
        public static void BuildMowableGrass()
        {
            var go = GameObject.Find("MowableGrass");
            if (go == null)
            {
                go = new GameObject("MowableGrass");
                Undo.RegisterCreatedObjectUndo(go, "Build Mowable Grass");
            }
            // The component reads world bounds and plants in world space; a moved or scaled root would
            // only be confusing.
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;

            var grass = go.GetComponent<MowableGrass>();
            if (grass == null) grass = Undo.AddComponent<MowableGrass>(go);
            Undo.RecordObject(grass, "Build Mowable Grass");

            if (grass.groundRenderer == null)
            {
                var plane = GameObject.Find("Grass");
                if (plane != null) grass.groundRenderer = plane.GetComponent<Renderer>();
            }
            if (grass.groundRenderer == null)
                Debug.LogWarning("[MowableGrass] No 'Grass' plane found — falling back to a " +
                                 $"{grass.fallbackFieldSize} m square around the origin. Assign Ground " +
                                 "Renderer by hand if the field is somewhere else.", grass);

            if (grass.material == null) grass.material = GetOrCreateBladeMaterial();

            var vis = Object.FindAnyObjectByType<MowingVisual_GrassAndFlowers>();
            if (vis != null)
            {
                Undo.RecordObject(vis, "Build Mowable Grass");
                vis.mowableGrass = grass;
                EditorUtility.SetDirty(vis);
            }
            else
            {
                Debug.LogWarning("[MowableGrass] No grass mowing visual on the mower — run " +
                                 "Tools > NASA Sim > Grass > Add Grass Mowing Visual, and the field will " +
                                 "be found automatically. Until then nothing will cut it.");
            }

            grass.Rebuild();
            EditorUtility.SetDirty(grass);
            EditorSceneManager.MarkSceneDirty(go.scene);

            int tufts = 0;
            var clumpRoot = GameObject.Find("GrassField");
            if (clumpRoot != null) tufts = clumpRoot.transform.childCount;

            Debug.Log($"[MowableGrass] {grass.BladeCount:N0} blades standing (editor preview is " +
                      $"{grass.editorPreviewFraction:P0} of Density — play mode builds the full field). " +
                      "The tractor cuts them as it drives; press R to stand them all back up.\n" +
                      "  Tune height, density and colours on the MowableGrass component and " +
                      $"{BladeMatPath}.\n" +
                      (tufts > 0
                          ? $"  The {tufts} scattered GrassClump tufts are still there on top. They are " +
                            "real GameObjects and most of this scene's file size — Tools > NASA Sim > " +
                            "Grass > Remove Scattered Tufts drops them if the blades are enough."
                          : "  No scattered tufts in the scene; the blade field is the whole grass."),
                      grass);
        }

        [MenuItem("Tools/NASA Sim/Grass/Remove Mowable Grass")]
        public static void RemoveMowableGrass()
        {
            var go = GameObject.Find("MowableGrass");
            if (go == null)
            {
                Debug.Log("[MowableGrass] Nothing to remove.");
                return;
            }
            Undo.DestroyObjectImmediate(go);
            Debug.Log("[MowableGrass] Blade field removed. The generated meshes go with it — nothing of " +
                      "it was ever in the scene file.");
        }

        /// <summary>
        /// A project material so the grass colours, wind and mown height are tunable (and versioned)
        /// rather than living on a runtime-only material.
        /// </summary>
        static Material GetOrCreateBladeMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(BladeMatPath);
            if (existing != null) return existing;

            Shader sh = Shader.Find("NasaSim/Grass");
            if (sh == null)
            {
                Debug.LogWarning("[MowableGrass] NasaSim/Grass shader not found — did " +
                                 "Assets/_Project/Shaders/NasaSimGrass.shader import cleanly? The field " +
                                 "will fall back to a flat unlit material that ignores the mower.");
                return null;
            }

            var mat = new Material(sh) { name = "GrassBlades" };
            AssetDatabase.CreateAsset(mat, BladeMatPath);
            return mat;
        }

        [MenuItem("Tools/NASA Sim/Grass/Remove Scattered Tufts")]
        public static void RemoveScatteredClumps()
        {
            var root = GameObject.Find("GrassField");
            if (root == null)
            {
                Debug.Log("[GrassField] No scattered tufts in the scene.");
                return;
            }
            int n = root.transform.childCount;
            Undo.DestroyObjectImmediate(root);
            Debug.Log($"[GrassField] Removed {n} scattered GrassClump tufts. The blade field " +
                      "(Build Mowable Grass) is unaffected.");
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
            vis.mowableGrass = Object.FindAnyObjectByType<MowableGrass>();
            var follower = Object.FindAnyObjectByType<TractorPathFollower>();
            vis.tractor = follower != null ? follower.transform : null;

            EditorUtility.SetDirty(vis);
            EditorSceneManager.MarkSceneDirty(mower.gameObject.scene);
            Debug.Log("[GrassField] Grass mowing visual wired to the mower. " +
                      "Palette Mode = Logo Auto classifies the strokes itself every " +
                      "run — circle blue, swoosh red, orbit and letters white, star dots mown but not " +
                      "flowered — so there is nothing to maintain. Run Grass > Auto-Assign Stroke Colors " +
                      "only if you want a per-stroke list to hand-edit.", vis);
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
            EditorSceneManager.MarkSceneDirty(vis.gameObject.scene);
        }

        /// <summary>
        /// Bake the shape-based classification (<see cref="LogoStrokeClassifier"/>) into the visual's
        /// per-stroke color list and switch it to that list, so individual strokes can be recolored by
        /// hand. Purely optional: Logo Auto does the same classification every run without a list.
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
            if (path == null || path.IsEmpty)
            {
                Debug.LogWarning("[GrassField] The CSV parsed to an empty path — stroke colors not assigned.");
                return;
            }

            Undo.RecordObject(vis, "Auto-Assign Stroke Colors");
            // Star strokes get the Other color at alpha 0 — the visual reads alpha 0 as "throw nothing",
            // so the baked list behaves exactly like Logo Auto does with Flowers On Stars off.
            Color star = vis.flowersOnStars
                ? vis.otherColor
                : new Color(vis.otherColor.r, vis.otherColor.g, vis.otherColor.b, 0f);
            vis.strokeColors = LogoStrokeClassifier.BuildStrokeColors(
                path, vis.circleColor, vis.swooshColor, vis.otherColor, star, out string report);
            vis.paletteMode = MowingVisual_GrassAndFlowers.PaletteMode.StrokeList;

            Debug.Log($"[GrassField] {report}\n  Palette Mode is now Stroke List: the list above is what " +
                      "the flowers use, and each entry is yours to edit — an entry with ALPHA 0 throws no " +
                      "flowers at all, which is how the star dots are silenced. Set Palette Mode back to " +
                      "Logo Auto to go back to classifying every run (which also survives a new CSV).", vis);
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
