using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Adds water: the square moat around the grass patch, and the round ponds that go outside it.
    ///
    /// The tool only PLACES a body and sets its numbers — <see cref="WaterBody"/> owns the geometry and
    /// regenerates itself whenever a field changes, so everything here is also editable afterwards in the
    /// Inspector (or by dragging the Scene-view handles) with no trip back through this window.
    ///
    /// Maya workflow: when the container model arrives, park the water body inside it, turn Build Basin
    /// OFF so the modelled container is the only geometry, and nudge Inner Size / Band Width until the
    /// sheet meets its walls. A WATER_&lt;Name&gt; locator exported from Maya can also be used as the
    /// placement for a pond ("At Selected WATER_ Marker").
    /// </summary>
    public sealed class WaterBodyTool : EditorWindow
    {
        public enum Preset { SquareMoatAroundGrass, RoundPondAtSceneViewPivot, RectPondAtSceneViewPivot, AtSelectedWaterMarker }

        Preset preset = Preset.SquareMoatAroundGrass;
        float moatGap = 0.5f;
        float bandWidth = 3.5f;
        float cornerRadius = 0f;
        float pondRadius = 6f;
        Vector2 pondSize = new Vector2(8f, 8f);
        float depth = 0.8f;
        float surfaceY = -0.12f;
        float density = 2f;
        bool buildBasin = true;
        int ducks = 3;
        int fish = 8;

        const string WaterMatPath = "Assets/_Project/Materials/Water.mat";
        const string MudMatPath = "Assets/_Project/Materials/MudBasin.mat";

        [MenuItem("Tools/NASA Sim/Water/Create Water Body")]
        public static void Open()
        {
            var w = GetWindow<WaterBodyTool>(true, "Create Water Body", true);
            w.minSize = new Vector2(440f, 460f);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Places a water body — the moat, or a pond — and stocks it with ducks and fish.\n\n" +
                "Nothing here is baked: every number is live on the WaterBody component afterwards, so " +
                "resize it in the Inspector (or drag its handles in the Scene view) to fit the container " +
                "model when it lands.", MessageType.Info);

            preset = (Preset)EditorGUILayout.EnumPopup("Preset", preset);
            EditorGUILayout.Space();

            switch (preset)
            {
                case Preset.SquareMoatAroundGrass:
                    moatGap = EditorGUILayout.Slider(
                        new GUIContent("Gap from grass (m)", "Dry ground left between the edge of the " +
                                       "grass patch and the water. Negative pulls the moat inward, over " +
                                       "the grass — which is how you bring it inside the dome."),
                        moatGap, -12f, 12f);
                    bandWidth = EditorGUILayout.Slider(
                        new GUIContent("Water width (m)", "How wide the moat's water band is."),
                        bandWidth, 0.5f, 15f);
                    cornerRadius = EditorGUILayout.Slider(
                        new GUIContent("Corner radius (m)", "0 keeps the moat a sharp square."),
                        cornerRadius, 0f, 15f);
                    EditorGUILayout.LabelField(" ", MoatSummary(), EditorStyles.miniLabel);
                    break;

                case Preset.RoundPondAtSceneViewPivot:
                    pondRadius = EditorGUILayout.Slider("Pond radius (m)", pondRadius, 0.5f, 30f);
                    break;

                case Preset.RectPondAtSceneViewPivot:
                case Preset.AtSelectedWaterMarker:
                    pondSize = EditorGUILayout.Vector2Field("Pond size (m)", pondSize);
                    break;
            }

            EditorGUILayout.Space();
            depth = EditorGUILayout.Slider("Depth (m)", depth, 0.2f, 3f);
            surfaceY = EditorGUILayout.FloatField(
                new GUIContent("Surface Y offset", "Offset from the source height (the grass patch / " +
                               "Scene pivot / WATER_ marker). Slightly negative so the sheet sits under " +
                               "the bank lip."), surfaceY);
            density = EditorGUILayout.Slider(new GUIContent("Mesh density (verts/m)"), density, 0.5f, 4f);
            buildBasin = EditorGUILayout.Toggle(
                new GUIContent("Build basin", "Generated mud trench with a MeshCollider. Turn off once " +
                                              "the modelled container sits under the water."), buildBasin);

            EditorGUILayout.Space();
            ducks = EditorGUILayout.IntSlider("Ducks", ducks, 0, 20);
            fish = EditorGUILayout.IntSlider("Fish", fish, 0, 40);

            EditorGUILayout.Space();
            if (GUILayout.Button("Build / Update Water Body", GUILayout.Height(30f)))
                Build();
        }

        string MoatSummary()
        {
            Bounds grass = GrassPatchBounds();
            Vector2 inner = new Vector2(grass.size.x, grass.size.z) + Vector2.one * (moatGap * 2f);
            return $"grass {grass.size.x:0.#} x {grass.size.z:0.#} m  ->  water from {inner.x:0.#} m " +
                   $"out to {inner.x + bandWidth * 2f:0.#} m across";
        }

        void Build()
        {
            switch (preset)
            {
                case Preset.SquareMoatAroundGrass:
                    Select(BuildSquareMoat(moatGap, bandWidth, cornerRadius, depth, surfaceY,
                                           density, buildBasin, ducks, fish));
                    break;

                case Preset.RoundPondAtSceneViewPivot:
                    Select(BuildPond(NextPondName(), ScenePivot(), WaterBody.Shape.Circle, pondRadius,
                                     pondSize, depth, surfaceY, density, buildBasin, ducks, fish));
                    break;

                case Preset.RectPondAtSceneViewPivot:
                    Select(BuildPond(NextPondName(), ScenePivot(), WaterBody.Shape.Box, pondRadius,
                                     pondSize, depth, surfaceY, density, buildBasin, ducks, fish));
                    break;

                case Preset.AtSelectedWaterMarker:
                {
                    Transform marker = Selection.activeTransform;
                    if (marker == null || !marker.name.StartsWith("WATER_"))
                    {
                        EditorUtility.DisplayDialog("Select a WATER_ marker",
                            "Select a scene object whose name starts with WATER_ (a locator exported " +
                            "from Maya) and run this again.", "OK");
                        return;
                    }
                    Select(BuildPond(marker.name, marker.position, WaterBody.Shape.Box, pondRadius,
                                     pondSize, depth, surfaceY, density, buildBasin, ducks, fish));
                    break;
                }
            }
        }

        static void Select(GameObject go)
        {
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        // ------------------------------------------------------------------ one-click menu entries

        [MenuItem("Tools/NASA Sim/Water/Add Square Moat Around Grass")]
        public static void AddSquareMoatMenu() =>
            Select(BuildSquareMoat(0.5f, 3.5f, 0f, 0.8f, -0.12f, 2f, true, 3, 8));

        [MenuItem("Tools/NASA Sim/Water/Add Round Pond Here")]
        public static void AddRoundPondMenu() =>
            Select(BuildPond(NextPondName(), ScenePivot(), WaterBody.Shape.Circle, 6f,
                             new Vector2(8f, 8f), 0.8f, -0.12f, 2f, true, 2, 4));

        [MenuItem("Tools/NASA Sim/Water/Snap All Wildlife Into Water")]
        public static void SnapAllWildlife()
        {
            int bodies = 0;
            foreach (var body in Object.FindObjectsByType<WaterBody>(FindObjectsInactive.Include,
                                                                     FindObjectsSortMode.None))
            {
                body.Rebuild();
                body.SnapWildlifeInside();
                bodies++;
            }
            if (bodies > 0)
                EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log($"[Water] Put the wildlife back inside {bodies} water bod{(bodies == 1 ? "y" : "ies")}.");
        }

        /// <summary>Programmatic default for the full-vision setup chain.</summary>
        public static void BuildMoatDefault() => BuildSquareMoat(0.5f, 3.5f, 0f, 0.8f, -0.12f, 2f, true, 3, 8);

        // ------------------------------------------------------------------ builders

        public static GameObject BuildSquareMoat(float gap, float band, float corner, float depth,
                                                 float surfaceY, float density, bool basin,
                                                 int ducks, int fish)
        {
            Bounds grass = GrassPatchBounds();
            var go = FindOrCreateRoot("WATER_Moat");
            go.transform.position = new Vector3(grass.center.x, grass.max.y + surfaceY, grass.center.z);
            go.transform.rotation = Quaternion.identity;

            var body = Configure(go, depth, surfaceY, density, basin);
            body.shape = WaterBody.Shape.RectRing;
            body.innerSize = new Vector2(grass.size.x, grass.size.z) + Vector2.one * (gap * 2f);
            body.bandWidth = band;
            body.cornerRadius = corner;
            Finish(body, ducks, fish);

            Debug.Log($"[Water] Square moat around a {grass.size.x:0.#} x {grass.size.z:0.#} m grass " +
                      $"patch: water from {body.innerSize.x:0.#} m to " +
                      $"{body.innerSize.x + band * 2f:0.#} m across, {band:0.##} m wide, " +
                      $"{depth:0.##} m deep.\n  Resize it any time on the WaterBody component — Inner " +
                      "Size, Water Width and Corner Radius rebuild it live, and the ducks follow.", go);
            return go;
        }

        public static GameObject BuildPond(string name, Vector3 center, WaterBody.Shape shape,
                                           float radius, Vector2 size, float depth, float surfaceY,
                                           float density, bool basin, int ducks, int fish)
        {
            var go = FindOrCreateRoot(name);
            // surfaceY is an OFFSET from the source height, so a WATER_ marker on elevated ground
            // produces a pond at that ground, not one buried at world -0.12.
            go.transform.position = new Vector3(center.x, center.y + surfaceY, center.z);

            var body = Configure(go, depth, surfaceY, density, basin);
            body.shape = shape;
            body.radius = radius;
            body.boxSize = new Vector3(size.x, 0f, size.y);
            Finish(body, ducks, fish);

            Debug.Log($"[Water] '{name}': " +
                      (shape == WaterBody.Shape.Circle ? $"{radius:0.#} m round pond" : $"{size.x:0.#} x {size.y:0.#} m pond") +
                      $", {depth:0.##} m deep, {ducks} duck(s) and {fish} fish. Drag it anywhere — the " +
                      "wildlife is parented to it.", go);
            return go;
        }

        static WaterBody Configure(GameObject go, float depth, float surfaceY, float density, bool basin)
        {
            go.layer = NasaLayers.Water;

            var body = GetOrAdd<WaterBody>(go);
            body.depth = depth;
            body.meshDensity = density;
            body.buildBasin = basin;
            body.basinMaterial = SceneBootstrap.MakeMat(MudMatPath, "Universal Render Pipeline/Lit",
                                                        new Color(0.16f, 0.12f, 0.09f));

            var mr = GetOrAdd<MeshRenderer>(go);
            mr.sharedMaterial = MakeWaterMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            GetOrAdd<MeshFilter>(go);
            GetOrAdd<WaterSurface>(go);
            return body;
        }

        static void Finish(WaterBody body, int ducks, int fish)
        {
            body.Rebuild();
            WildlifeSpawnTool.Spawn(body, ducks, fish, quiet: true);
            body.SnapWildlifeInside();
            EditorUtility.SetDirty(body);
            EditorSceneManager.MarkSceneDirty(body.gameObject.scene);
        }

        // ------------------------------------------------------------------ placement helpers

        /// <summary>
        /// The square of grass the moat runs around: the biodome's 'Grass' plane if it is there, else the
        /// mowable blade field, else a square around the logo.
        /// </summary>
        public static Bounds GrassPatchBounds()
        {
            var plane = GameObject.Find("Grass");
            if (plane != null && plane.TryGetComponent(out Renderer r)) return r.bounds;

            var grass = Object.FindAnyObjectByType<MowableGrass>();
            if (grass != null && grass.groundRenderer != null) return grass.groundRenderer.bounds;

            Vector3 c = LogoCenter();
            float size = grass != null ? grass.fallbackFieldSize : 42f;
            return new Bounds(new Vector3(c.x, 0f, c.z), new Vector3(size, 0f, size));
        }

        static Vector3 LogoCenter()
        {
            var loader = Object.FindAnyObjectByType<CsvWaypointLoader>();
            if (loader != null && loader.csvFile != null)
            {
                var path = loader.Parse(loader.csvFile);
                if (path != null && !path.IsEmpty) return path.Bounds.center;
            }
            return Vector3.zero;
        }

        static Vector3 ScenePivot() =>
            SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;

        static string NextPondName()
        {
            for (int i = 1; i < 100; i++)
                if (GameObject.Find($"WATER_Pond_{i:00}") == null) return $"WATER_Pond_{i:00}";
            return "WATER_Pond_XX";
        }

        static Material MakeWaterMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
            if (mat == null)
            {
                var shader = Shader.Find("NasaSim/Water");
                if (shader == null)
                {
                    Debug.LogError("[Water] Shader 'NasaSim/Water' not found — check " +
                                   "Assets/_Project/Shaders/NasaSimWater.shader for compile errors.");
                    shader = Shader.Find("Universal Render Pipeline/Lit");
                }
                mat = new Material(shader) { name = "Water" };
                AssetDatabase.CreateAsset(mat, WaterMatPath);
            }
            mat.renderQueue = BiodomeFixTools.WaterQueue;   // below gas (3050) and dome glass (3100)
            return mat;
        }

        static GameObject FindOrCreateRoot(string name)
        {
            // Not GameObject.Find: that skips INACTIVE objects, which would silently duplicate a
            // deactivated water body and strand its wildlife.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                  FindObjectsSortMode.None))
                if (t.parent == null && t.name == name)
                    return t.gameObject;

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Water Body");
            return go;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }
    }
}
