using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Lays a single smooth, invisible ramp collider over a staircase so the astronaut walks up it
    /// instead of catching on every modelled step (the reason an imported staircase makes you jump to
    /// climb). This is the same "stair trick" <see cref="AstronautSetup"/> uses for the placeholder
    /// flight, generalised to any staircase you select.
    ///
    /// A <see cref="CharacterController"/> only steps up ledges shorter than its Step Offset and only
    /// walks slopes shallower than its Slope Limit; real modelled steps break both rules, so the
    /// controller treats them as walls. A single ramp over the nosings removes every edge at once.
    ///
    /// It is NON-DESTRUCTIVE - the step meshes and their colliders are left exactly as they are; the
    /// ramp simply sits a touch above the treads, so the controller rides the ramp. Re-run it after
    /// moving the stairs (the ramp is built in world space and does not follow them).
    /// </summary>
    public sealed class StairRampTool : EditorWindow
    {
        GameObject staircase;
        float surfaceLift = 0.12f;   // raise the ramp this far above the tread line (m)
        float thickness = 0.5f;      // ramp slab thickness (m)
        float widthScale = 0.96f;    // ramp width as a fraction of the stair width
        bool flip = false;           // swap top/bottom if a staircase somehow samples the wrong way
        const int Samples = 24;      // down-rays along the run to read the step profile

        [MenuItem("Tools/NASA Sim/Biodome/Add Stair Ramp")]
        public static void Open()
        {
            var window = GetWindow<StairRampTool>(true, "Add Stair Ramp", true);
            window.minSize = new Vector2(430f, 320f);
            window.staircase = Selection.activeGameObject;
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Builds one smooth, invisible ramp collider over a staircase so the astronaut can walk " +
                "up it instead of jumping. Your step meshes are left untouched.\n\n" +
                "1. Give the staircase (or its model) a Mesh Collider if it has none.\n" +
                "2. Select it in the Hierarchy.\n" +
                "3. Build.",
                MessageType.Info);

            staircase = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Staircase", "The stairs in the scene, selected in the Hierarchy."),
                staircase, typeof(GameObject), true);

            surfaceLift = EditorGUILayout.Slider(
                new GUIContent("Surface lift", "Raise the ramp this far above the tread surface so the " +
                                               "feet neither float nor sink into the steps."),
                surfaceLift, 0f, 0.5f);
            thickness = EditorGUILayout.Slider(
                new GUIContent("Thickness", "Ramp slab thickness."), thickness, 0.1f, 1.5f);
            widthScale = EditorGUILayout.Slider(
                new GUIContent("Width scale", "Ramp width as a fraction of the staircase width. Keep it " +
                                              "just under 1 so the ramp doesn't stick out past the sides."),
                widthScale, 0.5f, 1.2f);
            flip = EditorGUILayout.Toggle(
                new GUIContent("Flip direction", "Only tick this if the ramp comes out sloping the wrong way."),
                flip);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(staircase == null))
                if (GUILayout.Button("Build / Update Ramp", GUILayout.Height(30f)))
                    Build();
        }

        void Build()
        {
            if (!staircase.scene.IsValid())
            {
                EditorUtility.DisplayDialog("Select the scene object",
                    "That is a Project-window asset. Drag the staircase into the scene, select it in the " +
                    "Hierarchy, then run this again.", "OK");
                return;
            }

            var colliders = staircase.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
            {
                EditorUtility.DisplayDialog("No colliders to sample",
                    $"'{staircase.name}' has no colliders, so there is nothing to lay a ramp over. Select " +
                    "the staircase, add a Mesh Collider (Inspector > Add Component), then run this again.",
                    "OK");
                return;
            }

            // Combined world bounds. The run direction is the longer horizontal axis; the ramp width is
            // the shorter one.
            Bounds bounds = colliders[0].bounds;
            foreach (var c in colliders) bounds.Encapsulate(c.bounds);

            bool runIsZ = bounds.size.z >= bounds.size.x;
            float runMin = runIsZ ? bounds.min.z : bounds.min.x;
            float runMax = runIsZ ? bounds.max.z : bounds.max.x;
            float widthCenter = runIsZ ? bounds.center.x : bounds.center.z;
            float widthExtent = runIsZ ? bounds.size.x : bounds.size.z;
            float rayTop = bounds.max.y + 1f;

            // Read the step surface height along the run by casting straight down onto the stair's own
            // colliders (only theirs, so nothing else in the scene interferes).
            Vector3 low = Vector3.zero, high = Vector3.zero;
            bool found = false;
            for (int i = 0; i < Samples; i++)
            {
                float runPos = Mathf.Lerp(runMin, runMax, i / (Samples - 1f));
                Vector3 origin = runIsZ ? new Vector3(widthCenter, rayTop, runPos)
                                        : new Vector3(runPos, rayTop, widthCenter);
                if (!RaycastStairs(colliders, origin, out Vector3 hit)) continue;

                if (!found) { low = high = hit; found = true; continue; }
                if (hit.y < low.y) low = hit;
                if (hit.y > high.y) high = hit;
            }

            if (!found || (high.y - low.y) < 0.02f)
            {
                EditorUtility.DisplayDialog("Couldn't read the steps",
                    "The down-rays didn't find a rising surface on this object. Make sure the staircase " +
                    "has a Mesh Collider and that its steps actually change height.", "OK");
                return;
            }

            Vector3 bottom = flip ? high : low;
            Vector3 top = flip ? low : high;

            Vector3 dir = top - bottom;
            float length = dir.magnitude;
            dir /= length;

            float angle = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);   // local +Z runs up the slope
            float width = widthExtent * widthScale;

            // Place the slab so its TOP face lies along the tread line, then lift it a hair. Offsetting
            // down by half the thickness along the ramp's own up-axis keeps the walking surface exact.
            Vector3 up = rot * Vector3.up;
            Vector3 midpoint = (bottom + top) * 0.5f;
            Vector3 pos = midpoint + Vector3.up * surfaceLift - up * (thickness * 0.5f);

            // One ramp per staircase, reused on rebuild. Built in world space at scene root so a scaled
            // model parent can't distort the collider size.
            string rampName = $"STAIR_Ramp_{staircase.name}";
            var rampGo = GameObject.Find(rampName);
            if (rampGo == null)
            {
                rampGo = new GameObject(rampName);
                Undo.RegisterCreatedObjectUndo(rampGo, "Build Stair Ramp");
            }
            else
            {
                Undo.RegisterFullObjectHierarchyUndo(rampGo, "Rebuild Stair Ramp");
            }

            rampGo.transform.SetParent(null, worldPositionStays: true);
            rampGo.transform.SetPositionAndRotation(pos, rot);
            rampGo.transform.localScale = Vector3.one;

            var box = rampGo.GetComponent<BoxCollider>();
            if (box == null) box = rampGo.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = new Vector3(width, thickness, length);

            EditorSceneManager.MarkSceneDirty(rampGo.scene);
            Selection.activeGameObject = rampGo;

            string message =
                $"[StairRamp] Ramp over '{staircase.name}': {angle:0.0} deg, {length:0.00} m long, " +
                $"{width:0.00} m wide. Walk up it - the step meshes are untouched.";
            if (angle > 50f)
                Debug.LogWarning(message + $"\n  This staircase is STEEPER than the astronaut's 50 deg " +
                                 "Slope Limit, so even the ramp won't be walkable. Either make the stairs " +
                                 "shallower, or raise slopeLimit in AstronautController.ApplyControllerTuning.",
                                 rampGo);
            else
                Debug.Log(message, rampGo);
        }

        /// <summary>Nearest downward hit on the staircase's OWN colliders (ignores everything else).</summary>
        static bool RaycastStairs(Collider[] colliders, Vector3 origin, out Vector3 point)
        {
            var ray = new Ray(origin, Vector3.down);
            float nearest = float.MaxValue;
            point = default;
            bool any = false;
            foreach (var c in colliders)
            {
                if (c == null) continue;
                if (c.Raycast(ray, out RaycastHit hit, 10000f) && hit.distance < nearest)
                {
                    nearest = hit.distance;
                    point = hit.point;
                    any = true;
                }
            }
            return any;
        }
    }
}
