using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds a single smooth HELICAL ramp collider that winds up a spiral staircase, so the astronaut
    /// walks up it instead of catching on every modelled step (or having to jump). A straight ramp can't
    /// do this - it would cut a chord across the central column - so a spiral needs its own helicoid.
    ///
    /// The ramp is a thin solid corkscrew: level across its width (like a real tread) and rising along
    /// its length. It is NON-DESTRUCTIVE - your step meshes are untouched; the ramp simply sits a touch
    /// above them, so the <see cref="CharacterController"/> rides the smooth surface.
    ///
    /// Centre, radius and height come from the staircase's bounds automatically. The parts a bounding box
    /// CANNOT reveal - how many turns it makes and which way it winds - are fields you set: select the
    /// ramp after building to see its green wireframe, then nudge Turns / Clockwise / Start Angle and
    /// rebuild until it sits over your steps.
    /// </summary>
    public sealed class SpiralStairRampTool : EditorWindow
    {
        GameObject staircase;
        float turns = 1f;             // how many full revolutions from bottom to top
        bool clockwise = true;        // winding direction, viewed from above
        float startAngleDeg = 0f;     // angle of the BOTTOM of the ramp
        float innerRadius = 0.25f;    // radius of the central column to clear
        float outerRadiusScale = 0.92f;  // ramp outer edge as a fraction of the stair radius
        float surfaceLift = 0.1f;     // raise the ramp this far above the tread line (m)
        float thickness = 0.3f;       // ramp slab thickness (m)
        int segmentsPerTurn = 48;     // smoothness
        bool disableTreadColliders = true;  // the ~per-tread MeshColliders are what cause snagging
        string _prefsLoadedFor;       // last-used settings persist per staircase name (EditorPrefs)

        const string DataDir = "Assets/_Project/Data";
        const string PrefsPrefix = "NasaSim.SpiralRamp.";

        [MenuItem("Tools/NASA Sim/Biodome/Add Spiral Stair Ramp")]
        public static void Open()
        {
            var window = GetWindow<SpiralStairRampTool>(true, "Add Spiral Stair Ramp", true);
            window.minSize = new Vector2(440f, 400f);
            window.staircase = Selection.activeGameObject;
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Builds a smooth helical ramp collider up a spiral staircase so the astronaut can walk " +
                "up it. Your step meshes are left untouched.\n\n" +
                "Select the staircase, set Turns and winding, Build, then select the ramp to see its " +
                "wireframe and adjust until it overlays your steps.",
                MessageType.Info);

            staircase = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Staircase", "The spiral stairs in the scene."),
                staircase, typeof(GameObject), true);

            // Recall the last-used settings for this particular staircase, so rebuilds are one click.
            if (staircase != null && _prefsLoadedFor != staircase.name)
            {
                LoadPrefs(staircase.name);
                _prefsLoadedFor = staircase.name;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shape (a bounding box can't guess these)", EditorStyles.boldLabel);
            turns = EditorGUILayout.Slider(
                new GUIContent("Turns", "How many full revolutions the stairs make from bottom to top."),
                turns, 0.25f, 5f);
            clockwise = EditorGUILayout.Toggle(
                new GUIContent("Clockwise (from above)", "Flip this if the ramp winds the wrong way."),
                clockwise);
            startAngleDeg = EditorGUILayout.Slider(
                new GUIContent("Start angle", "Rotate the whole ramp to line its bottom up with the first step."),
                startAngleDeg, 0f, 360f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fit", EditorStyles.boldLabel);
            innerRadius = EditorGUILayout.Slider(
                new GUIContent("Inner radius", "Radius of the central column to leave clear (m)."),
                innerRadius, 0f, 3f);
            outerRadiusScale = EditorGUILayout.Slider(
                new GUIContent("Outer radius scale", "Ramp outer edge as a fraction of the stair radius."),
                outerRadiusScale, 0.4f, 1.1f);
            surfaceLift = EditorGUILayout.Slider(
                new GUIContent("Surface lift", "Raise the ramp above the treads (m)."), surfaceLift, 0f, 0.5f);
            thickness = EditorGUILayout.Slider(
                new GUIContent("Thickness", "Ramp slab thickness (m)."), thickness, 0.05f, 1f);
            disableTreadColliders = EditorGUILayout.Toggle(
                new GUIContent("Disable tread colliders",
                               "Turn OFF the staircase's own colliders so the CharacterController rides " +
                               "only this smooth helicoid. Per-tread MeshColliders are what cause the " +
                               "snag-and-jitter climb; the ramp replaces them entirely."),
                disableTreadColliders);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(staircase == null))
                if (GUILayout.Button("Build / Update Spiral Ramp", GUILayout.Height(30f)))
                    Build();
        }

        void Build()
        {
            if (!staircase.scene.IsValid())
            {
                EditorUtility.DisplayDialog("Select the scene object",
                    "That is a Project-window asset. Drag the staircase into the scene, select it, then " +
                    "run this again.", "OK");
                return;
            }

            if (!TryGetBounds(staircase, out Bounds bounds))
            {
                EditorUtility.DisplayDialog("Nothing to measure",
                    $"'{staircase.name}' has no renderers or colliders to size the ramp from.", "OK");
                return;
            }

            Vector3 center = bounds.center;
            float bottomY = bounds.min.y;
            float topY = bounds.max.y;
            float stairRadius = Mathf.Max(bounds.size.x, bounds.size.z) * 0.5f;
            float outerR = Mathf.Max(0.1f, stairRadius * outerRadiusScale);
            float innerR = Mathf.Clamp(innerRadius, 0f, outerR - 0.05f);

            var mesh = BuildHelicoid(center, bottomY, topY, innerR, outerR);

            // Persist the mesh so the collider survives a reload.
            Directory.CreateDirectory(DataDir);
            string meshPath = $"{DataDir}/SpiralRamp_{Sanitize(staircase.name)}.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            string rampName = $"STAIR_SpiralRamp_{staircase.name}";
            var rampGo = GameObject.Find(rampName);
            if (rampGo == null)
            {
                rampGo = new GameObject(rampName);
                Undo.RegisterCreatedObjectUndo(rampGo, "Build Spiral Ramp");
            }
            else
            {
                Undo.RegisterFullObjectHierarchyUndo(rampGo, "Rebuild Spiral Ramp");
            }

            // Built in world space at scene root so a scaled model parent can't distort it.
            rampGo.transform.SetParent(null, worldPositionStays: true);
            rampGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            rampGo.transform.localScale = Vector3.one;

            var col = rampGo.GetComponent<MeshCollider>();
            if (col == null) col = rampGo.AddComponent<MeshCollider>();
            col.sharedMesh = null;
            col.convex = false;              // a corkscrew is not convex; fine for a static walkable
            col.sharedMesh = mesh;

            int disabled = 0;
            if (disableTreadColliders)
            {
                foreach (var c in staircase.GetComponentsInChildren<Collider>(true))
                {
                    if (!c.enabled) continue;
                    Undo.RecordObject(c, "Disable Tread Colliders");
                    c.enabled = false;
                    disabled++;
                }
            }
            SavePrefs(staircase.name);

            EditorSceneManager.MarkSceneDirty(rampGo.scene);
            Selection.activeGameObject = rampGo;

            // Steepest walkable path is the pitch at the middle of the ramp width.
            float rMid = (innerR + outerR) * 0.5f;
            float risePerTurn = (topY - bottomY) / Mathf.Max(0.01f, turns);
            float pitch = Mathf.Atan2(risePerTurn, 2f * Mathf.PI * rMid) * Mathf.Rad2Deg;

            string msg = $"[SpiralRamp] Helical ramp over '{staircase.name}': {turns:0.##} turn(s), " +
                         $"rise {topY - bottomY:0.0} m, radius {innerR:0.0}-{outerR:0.0} m, " +
                         $"~{pitch:0} deg pitch" +
                         (disabled > 0 ? $", {disabled} tread collider(s) disabled (the ramp does all collision now)" : "") +
                         ". Select it to see the wireframe; adjust Turns/Clockwise/" +
                         "Start Angle and rebuild to line it up (settings are remembered per staircase).";
            if (pitch > 50f)
                Debug.LogWarning(msg + "\n  Pitch exceeds the astronaut's 50 deg Slope Limit - add more " +
                                 "Turns (shallower), widen the radius, or raise slopeLimit in " +
                                 "AstronautController.ApplyControllerTuning.", rampGo);
            else
                Debug.Log(msg, rampGo);
        }

        /// <summary>A thin solid corkscrew: level across its width, rising along its length.</summary>
        Mesh BuildHelicoid(Vector3 center, float bottomY, float topY, float innerR, float outerR)
        {
            int rings = Mathf.Max(2, Mathf.RoundToInt(turns * segmentsPerTurn)) + 1;
            float totalAngle = turns * Mathf.PI * 2f * (clockwise ? -1f : 1f);
            float startAngle = startAngleDeg * Mathf.Deg2Rad;

            var verts = new List<Vector3>(rings * 4);
            for (int r = 0; r < rings; r++)
            {
                float t = r / (rings - 1f);
                float ang = startAngle + totalAngle * t;
                float y = Mathf.Lerp(bottomY, topY, t) + surfaceLift;
                float c = Mathf.Cos(ang), s = Mathf.Sin(ang);

                Vector3 inTop  = new Vector3(center.x + innerR * c, y, center.z + innerR * s);
                Vector3 outTop = new Vector3(center.x + outerR * c, y, center.z + outerR * s);
                verts.Add(inTop);                        // r*4 + 0
                verts.Add(outTop);                       // r*4 + 1
                verts.Add(inTop  - Vector3.up * thickness);  // r*4 + 2
                verts.Add(outTop - Vector3.up * thickness);  // r*4 + 3
            }

            var tris = new List<int>();
            // Winding must follow the wind direction: a NON-CONVEX MeshCollider is single-sided (PhysX
            // cooks faces from triangle winding; RecalculateNormals is render-only). Emitting the
            // clockwise quad order on a counterclockwise ramp would build it inside-out and the
            // CharacterController would fall straight through.
            bool flipWinding = !clockwise;
            void Quad(int a, int b, int c, int d)
            {
                if (flipWinding) { tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(a); tris.Add(d); tris.Add(c); }
                else { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(d); }
            }

            for (int r = 0; r < rings - 1; r++)
            {
                int a = r * 4, b = (r + 1) * 4;
                Quad(a + 0, a + 1, b + 1, b + 0);   // top surface
                Quad(a + 2, b + 2, b + 3, a + 3);   // underside
                Quad(a + 1, a + 3, b + 3, b + 1);   // outer wall
                Quad(a + 0, b + 0, b + 2, a + 2);   // inner wall
            }
            // End caps so the corkscrew is a closed solid.
            Quad(0, 1, 3, 2);
            int last = (rings - 1) * 4;
            Quad(last + 0, last + 1, last + 3, last + 2);

            var mesh = new Mesh { name = "SpiralRamp" };
            if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        void LoadPrefs(string name)
        {
            string k = PrefsPrefix + name + ".";
            turns = EditorPrefs.GetFloat(k + "turns", turns);
            clockwise = EditorPrefs.GetBool(k + "clockwise", clockwise);
            startAngleDeg = EditorPrefs.GetFloat(k + "startAngle", startAngleDeg);
            innerRadius = EditorPrefs.GetFloat(k + "innerRadius", innerRadius);
            outerRadiusScale = EditorPrefs.GetFloat(k + "outerRadiusScale", outerRadiusScale);
            surfaceLift = EditorPrefs.GetFloat(k + "surfaceLift", surfaceLift);
            thickness = EditorPrefs.GetFloat(k + "thickness", thickness);
            disableTreadColliders = EditorPrefs.GetBool(k + "disableTreads", disableTreadColliders);
        }

        void SavePrefs(string name)
        {
            string k = PrefsPrefix + name + ".";
            EditorPrefs.SetFloat(k + "turns", turns);
            EditorPrefs.SetBool(k + "clockwise", clockwise);
            EditorPrefs.SetFloat(k + "startAngle", startAngleDeg);
            EditorPrefs.SetFloat(k + "innerRadius", innerRadius);
            EditorPrefs.SetFloat(k + "outerRadiusScale", outerRadiusScale);
            EditorPrefs.SetFloat(k + "surfaceLift", surfaceLift);
            EditorPrefs.SetFloat(k + "thickness", thickness);
            EditorPrefs.SetBool(k + "disableTreads", disableTreadColliders);
        }

        static bool TryGetBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool has = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            if (has) return true;
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
            {
                if (!has) { bounds = c.bounds; has = true; }
                else bounds.Encapsulate(c.bounds);
            }
            return has;
        }

        static string Sanitize(string s)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '_');
            return s;
        }
    }
}
