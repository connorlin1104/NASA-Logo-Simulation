using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds water bodies: the moat ring around the logo, or standalone ponds. Generates the subdivided
    /// water sheet (the water shader displaces vertices, so it must be a dense, even grid — one reason
    /// water is never exported from Maya), a matching mud basin WITH a MeshCollider so the astronaut can
    /// stand on the banks, and wires <see cref="WaterSurface"/> + <see cref="WaterBody"/>. Meshes are
    /// saved as assets (same persistence pattern as SpiralStairRampTool) so they survive reloads.
    ///
    /// Maya workflow: model basins/trenches as COL_ geometry if you want custom banks, and drop a
    /// WATER_&lt;Name&gt; locator where a pond surface belongs — then use the "At Selected WATER_ Marker"
    /// preset. Water surfaces themselves are always generated here.
    /// </summary>
    public sealed class WaterBodyTool : EditorWindow
    {
        public enum Preset { MoatAroundLogo, PondAtSceneViewPivot, AtSelectedWaterMarker }

        Preset preset = Preset.MoatAroundLogo;
        float innerRadius = 27f;
        float outerRadius = 31f;
        Vector2 pondSize = new Vector2(8f, 8f);
        float depth = 0.8f;
        float surfaceY = -0.12f;
        float density = 2f;
        bool buildBasin = true;

        const string DataDir = "Assets/_Project/Data";
        const string WaterMatPath = "Assets/_Project/Materials/Water.mat";
        const string MudMatPath = "Assets/_Project/Materials/MudBasin.mat";

        [MenuItem("Tools/NASA Sim/Water/Create Water Body")]
        public static void Open()
        {
            var w = GetWindow<WaterBodyTool>(true, "Create Water Body", true);
            w.minSize = new Vector2(430f, 380f);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Generates a water surface (NasaSim/Water shader), a mud basin with collider, and the " +
                "WaterBody region wildlife wanders in.\n\n" +
                "Moat preset: an annulus centred on the logo. Pond presets: a box pond at the Scene-view " +
                "pivot or at a selected WATER_ locator imported from Maya.", MessageType.Info);

            preset = (Preset)EditorGUILayout.EnumPopup("Preset", preset);
            if (preset == Preset.MoatAroundLogo)
            {
                innerRadius = EditorGUILayout.FloatField(
                    new GUIContent("Inner radius (m)", "27 clears the 40 m logo's corner half-diagonal (~26.3 m)."),
                    innerRadius);
                outerRadius = EditorGUILayout.FloatField("Outer radius (m)", outerRadius);
            }
            else
            {
                pondSize = EditorGUILayout.Vector2Field("Pond size (m)", pondSize);
            }
            depth = EditorGUILayout.Slider("Depth (m)", depth, 0.2f, 3f);
            surfaceY = EditorGUILayout.FloatField(
                new GUIContent("Surface Y offset", "Offset from the source height (logo centre / Scene " +
                               "pivot / WATER_ marker). Slightly negative so the sheet sits under the bank lip."),
                surfaceY);
            density = EditorGUILayout.Slider(new GUIContent("Mesh density (verts/m)"), density, 0.5f, 4f);
            buildBasin = EditorGUILayout.Toggle(
                new GUIContent("Build basin", "Generated mud basin with a MeshCollider. Turn off when a " +
                                              "Maya-modelled COL_ basin already exists."), buildBasin);

            EditorGUILayout.Space();
            if (GUILayout.Button("Build / Update Water Body", GUILayout.Height(30f)))
                Build();
        }

        void Build()
        {
            switch (preset)
            {
                case Preset.MoatAroundLogo:
                    BuildAnnulusBody("WATER_Moat", LogoCenter(), innerRadius, outerRadius,
                                     depth, surfaceY, density, buildBasin);
                    break;

                case Preset.PondAtSceneViewPivot:
                {
                    Vector3 c = SceneView.lastActiveSceneView != null
                        ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
                    BuildBoxBody(NextPondName(), c, pondSize, depth, surfaceY, density, buildBasin);
                    break;
                }

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
                    BuildBoxBody(marker.name, marker.position, pondSize, depth, surfaceY, density, buildBasin);
                    break;
                }
            }
        }

        /// <summary>Programmatic default for the full-vision setup chain.</summary>
        public static void BuildMoatDefault()
        {
            BuildAnnulusBody("WATER_Moat", LogoCenter(), 27f, 31f, 0.8f, -0.12f, 2f, true);
        }

        // ------------------------------------------------------------------ builders

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

        static string NextPondName()
        {
            for (int i = 1; i < 100; i++)
                if (GameObject.Find($"WATER_Pond_{i:00}") == null) return $"WATER_Pond_{i:00}";
            return "WATER_Pond_XX";
        }

        static GameObject BuildAnnulusBody(string name, Vector3 center, float rIn, float rOut,
                                           float depth, float surfaceY, float density, bool basin)
        {
            var go = FindOrCreateRoot(name);
            // surfaceY is an OFFSET from the source height, so a WATER_ marker on elevated ground
            // produces a pond at that ground, not one buried at world -0.12.
            go.transform.position = new Vector3(center.x, center.y + surfaceY, center.z);
            go.layer = NasaLayers.Water;

            Mesh mesh = SaveMesh(BuildAnnulusMesh(rIn, rOut, density),
                                 $"{DataDir}/WaterMesh_{Sanitize(name)}.asset");
            AttachWater(go, mesh);

            var body = GetOrAdd<WaterBody>(go);
            body.shape = WaterBody.Shape.Annulus;
            body.innerRadius = rIn;
            body.outerRadius = rOut;
            body.depth = depth;

            if (basin)
            {
                float bottomY = surfaceY - depth;
                var profile = new List<Vector2>
                {
                    new Vector2(rIn - 0.6f, 0.06f),        // lip on the inner bank
                    new Vector2(rIn + 0.3f, bottomY),
                    new Vector2(rOut - 0.3f, bottomY),
                    new Vector2(rOut + 0.6f, 0.06f),       // lip on the outer bank
                };
                Mesh basinMesh = LoftProfile(profile, 96, "Basin");
                BuildBasin(go, basinMesh, surfaceY);
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log($"[Water] Built '{name}': ring {rIn:0.#}–{rOut:0.#} m, depth {depth:0.##} m. " +
                      "Spawn wildlife with Tools > NASA Sim > Water > Spawn Ducks & Fish.", go);
            return go;
        }

        static GameObject BuildBoxBody(string name, Vector3 center, Vector2 size,
                                       float depth, float surfaceY, float density, bool basin)
        {
            var go = FindOrCreateRoot(name);
            go.transform.position = new Vector3(center.x, center.y + surfaceY, center.z);
            go.layer = NasaLayers.Water;

            Mesh mesh = SaveMesh(BuildBoxMesh(size, density),
                                 $"{DataDir}/WaterMesh_{Sanitize(name)}.asset");
            AttachWater(go, mesh);

            var body = GetOrAdd<WaterBody>(go);
            body.shape = WaterBody.Shape.Box;
            body.boxSize = new Vector3(size.x, 0f, size.y);
            body.depth = depth;

            if (basin)
            {
                Mesh basinMesh = BuildBoxBasinMesh(size, surfaceY - depth);
                BuildBasin(go, basinMesh, surfaceY);
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            Debug.Log($"[Water] Built '{name}': {size.x:0.#} x {size.y:0.#} m pond, depth {depth:0.##} m.", go);
            return go;
        }

        static void AttachWater(GameObject go, Mesh mesh)
        {
            var mf = GetOrAdd<MeshFilter>(go);
            mf.sharedMesh = mesh;
            var mr = GetOrAdd<MeshRenderer>(go);
            mr.sharedMaterial = MakeWaterMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            GetOrAdd<WaterSurface>(go);
        }

        static void BuildBasin(GameObject waterGo, Mesh basinMesh, float surfaceY)
        {
            basinMesh = SaveMesh(basinMesh, $"{DataDir}/WaterBasin_{Sanitize(waterGo.name)}.asset");
            var basin = FindOrCreateChild(waterGo.transform, "Basin");
            basin.layer = 0;                                   // solid ground, not Water
            basin.transform.localPosition = new Vector3(0f, -surfaceY, 0f);   // basin lip at ground level
            var mf = GetOrAdd<MeshFilter>(basin);
            mf.sharedMesh = basinMesh;
            var mr = GetOrAdd<MeshRenderer>(basin);
            mr.sharedMaterial = SceneBootstrap.MakeMat(MudMatPath, "Universal Render Pipeline/Lit",
                                                       new Color(0.16f, 0.12f, 0.09f));
            var mc = GetOrAdd<MeshCollider>(basin);
            mc.sharedMesh = null;
            mc.sharedMesh = basinMesh;
            mc.convex = false;
        }

        // ------------------------------------------------------------------ meshes

        static Mesh BuildAnnulusMesh(float rIn, float rOut, float density)
        {
            int segments = Mathf.Clamp(Mathf.RoundToInt(Mathf.PI * (rIn + rOut) * density * 0.5f), 48, 384);
            int rings = Mathf.Clamp(Mathf.RoundToInt((rOut - rIn) * density) + 1, 2, 64);
            var profile = new List<Vector2>(rings);
            for (int r = 0; r < rings; r++)
                profile.Add(new Vector2(Mathf.Lerp(rIn, rOut, r / (rings - 1f)), 0f));
            return LoftProfile(profile, segments, "WaterSurface");
        }

        static Mesh BuildBoxMesh(Vector2 size, float density)
        {
            int nx = Mathf.Clamp(Mathf.RoundToInt(size.x * density), 2, 128);
            int nz = Mathf.Clamp(Mathf.RoundToInt(size.y * density), 2, 128);
            var verts = new List<Vector3>((nx + 1) * (nz + 1));
            var tris = new List<int>(nx * nz * 6);
            for (int z = 0; z <= nz; z++)
                for (int x = 0; x <= nx; x++)
                    verts.Add(new Vector3((x / (float)nx - 0.5f) * size.x, 0f,
                                          (z / (float)nz - 0.5f) * size.y));
            int cols = nx + 1;
            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    int a = z * cols + x, b = a + 1, c = a + cols, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            return FinalizeUpwardMesh(verts, tris, "WaterSurface");
        }

        /// <summary>Revolve a (radius, height) profile around Y — flat rings for water, banked trench
        /// cross-sections for basins.</summary>
        static Mesh LoftProfile(List<Vector2> profile, int segments, string name)
        {
            var verts = new List<Vector3>(profile.Count * (segments + 1));
            var tris = new List<int>((profile.Count - 1) * segments * 6);
            int cols = segments + 1;

            for (int r = 0; r < profile.Count; r++)
                for (int s = 0; s <= segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    verts.Add(new Vector3(Mathf.Cos(a) * profile[r].x, profile[r].y,
                                          Mathf.Sin(a) * profile[r].x));
                }

            for (int r = 0; r < profile.Count - 1; r++)
                for (int s = 0; s < segments; s++)
                {
                    int a = r * cols + s, b = a + 1, c = a + cols, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }

            return FinalizeUpwardMesh(verts, tris, name);
        }

        /// <summary>Open-top tub: a bottom rectangle with four banks sloping up to an outer lip. Faces
        /// are wound so their fronts point up/inward (verified for the bottom, symmetric for banks).</summary>
        static Mesh BuildBoxBasinMesh(Vector2 size, float bottomY)
        {
            float lx = size.x * 0.5f + 0.6f, lz = size.y * 0.5f + 0.6f;
            float bx = Mathf.Max(0.3f, size.x * 0.5f - 0.3f), bz = Mathf.Max(0.3f, size.y * 0.5f - 0.3f);

            var verts = new List<Vector3>
            {
                new Vector3(-bx, bottomY, -bz), new Vector3(bx, bottomY, -bz),   // bottom loop, CCW from above
                new Vector3(bx, bottomY, bz), new Vector3(-bx, bottomY, bz),
                new Vector3(-lx, 0.06f, -lz), new Vector3(lx, 0.06f, -lz),       // lip loop, same order
                new Vector3(lx, 0.06f, lz), new Vector3(-lx, 0.06f, lz),
            };

            var tris = new List<int> { 0, 2, 1, 0, 3, 2 };                       // bottom, front side up
            for (int k = 0; k < 4; k++)                                          // four banks
            {
                int a = k, b = (k + 1) % 4, A = a + 4, B = b + 4;
                tris.Add(A); tris.Add(b); tris.Add(B);
                tris.Add(A); tris.Add(a); tris.Add(b);
            }
            return FinalizeUpwardMesh(verts, tris, "Basin");
        }

        /// <summary>
        /// Build the mesh and guarantee it faces UP: recalc normals and, if the average points down (the
        /// winding convention came out inverted), flip every triangle. Cheap insurance against the classic
        /// invisible-from-above generated mesh.
        /// </summary>
        static Mesh FinalizeUpwardMesh(List<Vector3> verts, List<int> tris, string name)
        {
            var mesh = new Mesh { name = name };
            if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();

            float sum = 0f;
            var normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++) sum += normals[i].y;
            if (sum < 0f)
            {
                tris.Reverse();                            // reverses each triangle's winding
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
            }
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------------ assets & helpers

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

        /// <summary>Persist a generated mesh GUID-stably: overwrite an existing asset in place (so any
        /// scene reference from a previous build survives) instead of delete+recreate.</summary>
        static Mesh SaveMesh(Mesh mesh, string path)
        {
            Directory.CreateDirectory(DataDir);
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null)
            {
                existing.Clear();
                EditorUtility.CopySerialized(mesh, existing);
                Object.DestroyImmediate(mesh);
                AssetDatabase.SaveAssets();
                return existing;
            }
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static GameObject FindOrCreateRoot(string name)
        {
            // Not GameObject.Find: that skips INACTIVE objects, which would silently duplicate a
            // deactivated water body and strand its mesh references.
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                  FindObjectsSortMode.None))
                if (t.parent == null && t.name == name)
                    return t.gameObject;

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create Water Body");
            return go;
        }

        static GameObject FindOrCreateChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) return t.gameObject;
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            return go;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        static string Sanitize(string s)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '_');
            return s;
        }
    }
}
