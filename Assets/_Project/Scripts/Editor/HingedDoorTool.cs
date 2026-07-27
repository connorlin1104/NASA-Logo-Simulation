using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Makes imported door meshes swing open on E.
    ///
    /// Drop the door groups in the buckets and press Build. Each one gets a <see cref="HingedDoor"/> with
    /// a real hinge line down one vertical edge, a box collider so it blocks the way while shut, and an
    /// <c>InteractTrigger</c> so "[E] Open the door" appears when you walk up.
    ///
    /// The hinge is the whole problem with imported doors, and the reason this tool exists rather than a
    /// checkbox on the model: a Maya group's pivot is wherever the modeller left it, so rotating the
    /// panel about its own origin makes it pirouette through the wall. The tool measures the panel, puts
    /// the hinge on the vertical edge at one end, and draws the result in the Scene view — orange is
    /// shut, green is open, and the arc between them is the sweep. If it hinges on the wrong side, press
    /// Flip hinge and watch it move. No need to press Play to check.
    /// </summary>
    public sealed class HingedDoorTool : EditorWindow
    {
        [System.Serializable]
        public class Job
        {
            public GameObject panel;
            [Tooltip("Optional second leaf for a double door. Hinges at the far end and swings the " +
                     "opposite way, so the pair opens apart.")]
            public GameObject secondPanel;
            public bool hingeOtherEnd;
            public bool swingBack;
            public float angle = 95f;
        }

        const string TriggerName = "InteractTrigger";

        [SerializeField] List<Job> _jobs = new List<Job>();
        [SerializeField] float _defaultAngle = 95f;
        [SerializeField] float _seconds = 1.4f;
        [SerializeField] float _autoClose;
        [SerializeField] bool _addCollider = true;
        [SerializeField] float _triggerMargin = 1.1f;
        [SerializeField] string _openPrompt = "[E] Open the door";
        [SerializeField] string _closePrompt = "[E] Close the door";
        [SerializeField] string _excludeWords =
            "bolt, frame, hinge, screen, twist, base, clamp, pipe, rib, flange, plate, stripe, rail, " +
            "step, leg, collar, light, kp, handle, elevator";

        Vector2 _scroll;

        [MenuItem("Tools/NASA Sim/Station/Hinged Doors")]
        public static void Open()
        {
            var window = GetWindow<HingedDoorTool>(true, "Hinged Doors", true);
            window.minSize = new Vector2(560f, 520f);
            if (window._jobs.Count == 0) window.AutoFind();
            window.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Assign door meshes, press Build, then walk up to one and press E.\n\n" +
                "In the Scene view: the orange outline is the door shut, the green one is it open, and " +
                "the curve between them is the swing. Wrong side? Flip hinge. Opening into the wall? " +
                "Flip swing.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Find doors in the scene")) AutoFind();
                if (GUILayout.Button("Add the selection")) AddSelection();
                if (GUILayout.Button("Clear list")) _jobs.Clear();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Doors", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(150f));
            for (int i = 0; i < _jobs.Count; i++)
            {
                Job job = _jobs[i];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        job.panel = (GameObject)EditorGUILayout.ObjectField(
                            job.panel, typeof(GameObject), true);
                        using (new EditorGUI.DisabledScope(job.panel == null))
                            if (GUILayout.Button("Show", GUILayout.Width(46f))) Show(job.panel);
                        if (GUILayout.Button("×", GUILayout.Width(22f)))
                        {
                            _jobs.RemoveAt(i);
                            i--;
                            continue;
                        }
                    }

                    job.secondPanel = (GameObject)EditorGUILayout.ObjectField(
                        new GUIContent("Second leaf", "Optional — for a double door."),
                        job.secondPanel, typeof(GameObject), true);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        job.hingeOtherEnd = GUILayout.Toggle(job.hingeOtherEnd, "Flip hinge", "Button");
                        job.swingBack = GUILayout.Toggle(job.swingBack, "Flip swing", "Button");
                        job.angle = EditorGUILayout.Slider(job.angle, 20f, 170f);
                    }
                }
            }
            if (GUILayout.Button("Add empty row")) _jobs.Add(new Job { angle = _defaultAngle });
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("All doors", EditorStyles.boldLabel);
            _defaultAngle = EditorGUILayout.Slider(
                new GUIContent("Open angle", "Applied to new rows. Each door keeps its own value once " +
                                             "you change it above."),
                _defaultAngle, 20f, 170f);
            _seconds = EditorGUILayout.Slider(
                new GUIContent("Swing time (s)", "Real seconds — unaffected by the sim's fast-forward."),
                _seconds, 0.2f, 6f);
            _autoClose = EditorGUILayout.Slider(
                new GUIContent("Close itself after (s)", "0 leaves it open until you press E again."),
                _autoClose, 0f, 20f);
            _addCollider = EditorGUILayout.Toggle(
                new GUIContent("Add a blocking collider", "Gives the panel a box collider if it has none, " +
                                                          "so a shut door actually stops you. A collider " +
                                                          "baked under StationColliders would stay behind " +
                                                          "when the door swings — this one travels with it."),
                _addCollider);
            _triggerMargin = EditorGUILayout.Slider(
                new GUIContent("Reach past the door (m)", "How far back from the door E still works."),
                _triggerMargin, 0.2f, 4f);
            _openPrompt = EditorGUILayout.TextField("Open prompt", _openPrompt);
            _closePrompt = EditorGUILayout.TextField("Close prompt", _closePrompt);
            _excludeWords = EditorGUILayout.TextField(
                new GUIContent("Not a door", "Comma-separated. Parts whose names contain one of these are " +
                                             "skipped by Find — bolts and frames are named after the door " +
                                             "they belong to."),
                _excludeWords);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(CountValid() == 0))
                if (GUILayout.Button($"Build {CountValid()} door(s)", GUILayout.Height(34f)))
                    Build();

            EditorGUILayout.LabelField("Preview (moves the real doors — undoable)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("All shut")) PreviewAll(false);
                if (GUILayout.Button("All open")) PreviewAll(true);
            }
            if (GUILayout.Button("Remove the doors I built")) RemoveAll();
        }

        int CountValid()
        {
            int n = 0;
            foreach (Job j in _jobs) if (j != null && j.panel != null) n++;
            return n;
        }

        static void Show(GameObject go)
        {
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            SceneView.FrameLastActiveSceneView();
        }

        // ================================================================== find

        void AddSelection()
        {
            foreach (var go in Selection.gameObjects)
            {
                if (go == null || !go.scene.IsValid()) continue;
                if (_jobs.Exists(j => j.panel == go)) continue;
                _jobs.Add(new Job { panel = go, angle = _defaultAngle });
            }
        }

        /// <summary>
        /// Doors you labelled yourself, and nothing else. Two filters do the work:
        ///
        /// names containing ':' are dropped, because that colon is the Maya namespace on the imported
        /// parts ("newGreenHouse_2:OuterHatch_bolt2") while your own Hierarchy labels have none; and the
        /// "Not a door" list drops the bolts, frames and hinges that are named after the door they belong
        /// to. What is left is the group you actually want to swing.
        /// </summary>
        void AutoFind()
        {
            string[] exclude = ParseWords(_excludeWords);
            var found = new List<Transform>();

            foreach (Transform t in StationBuild.FindAllContaining("door", "hatch"))
            {
                if (t.name.Contains(":")) continue;
                if (t.GetComponentInChildren<Renderer>(true) == null) continue;

                string lower = t.name.ToLowerInvariant();
                bool skip = false;
                foreach (string w in exclude)
                    if (lower.Contains(w)) { skip = true; break; }
                if (skip) continue;

                found.Add(t);
            }

            // Keep the deepest label in any nest: a "Doors" group holding "InnerDoor" should hand you the
            // leaf, not the container, or the whole set would swing as one slab.
            var keep = new List<Transform>();
            foreach (Transform t in found)
            {
                bool hasDescendant = false;
                foreach (Transform other in found)
                    if (other != t && other.IsChildOf(t)) { hasDescendant = true; break; }
                if (!hasDescendant) keep.Add(t);
            }

            int added = 0;
            foreach (Transform t in keep)
            {
                if (_jobs.Exists(j => j.panel == t.gameObject)) continue;
                _jobs.Add(new Job { panel = t.gameObject, angle = _defaultAngle });
                added++;
            }

            Debug.Log($"[Hinged doors] Found {keep.Count} door group(s), added {added} new row(s)." +
                      (keep.Count == 0
                          ? " Nothing matched — label the door group with 'Door' or 'Hatch' in the " +
                            "Hierarchy, or drag it in and press Add the selection."
                          : "\n  " + string.Join("\n  ", keep.ConvertAll(StationBuild.PathOf))));
        }

        static string[] ParseWords(string csv)
        {
            var list = new List<string>();
            foreach (string raw in csv.Split(','))
            {
                string w = raw.Trim().ToLowerInvariant();
                if (w.Length > 0) list.Add(w);
            }
            return list.ToArray();
        }

        // ================================================================== build

        void Build()
        {
            int built = 0;
            var lines = new List<string>();

            foreach (Job job in _jobs)
            {
                if (job == null || job.panel == null) continue;
                if (!StationBuild.RequireSceneObject(job.panel, "door")) continue;
                if (job.secondPanel != null && !StationBuild.RequireSceneObject(job.secondPanel, "second leaf"))
                    continue;

                if (!StationBuild.TryRendererBounds(job.panel, out Bounds bounds))
                {
                    Debug.LogWarning($"[Hinged doors] '{job.panel.name}' has no meshes to measure — skipped.",
                                     job.panel);
                    continue;
                }

                var door = StationBuild.GetOrAdd<HingedDoor>(job.panel);
                Undo.RecordObject(door, "Build hinged door");
                Undo.RecordObject(job.panel.transform, "Build hinged door");
                if (job.secondPanel != null) Undo.RecordObject(job.secondPanel.transform, "Build hinged door");

                // Put an already-built door back on its stop BEFORE re-reading the closed pose. Without
                // this, rebuilding while the Preview has it standing open would adopt the OPEN pose as
                // the new closed one, and the door would creep further round on every build.
                if (door.HasClosedPose) door.SetImmediate(false);

                door.panel = job.panel.transform;
                door.secondPanel = job.secondPanel != null ? job.secondPanel.transform : null;

                float sign = job.swingBack ? -1f : 1f;
                door.openAngle = job.angle * sign;
                door.secondOpenAngle = -job.angle * sign;
                door.moveDuration = _seconds;
                door.autoCloseSeconds = _autoClose;
                door.openPrompt = _openPrompt;
                door.closePrompt = _closePrompt;

                MeasureHinge(job.panel.transform, bounds, job.hingeOtherEnd,
                             out Vector3 hinge, out Vector3 axis, out float radius, out float height);
                door.hinge = hinge;
                door.hingeAxis = axis;
                door.SetGizmoSize(radius, height);

                if (job.secondPanel != null &&
                    StationBuild.TryRendererBounds(job.secondPanel, out Bounds b2))
                {
                    // The far leaf hinges at the OTHER end, so the pair parts in the middle.
                    MeasureHinge(job.secondPanel.transform, b2, !job.hingeOtherEnd,
                                 out Vector3 h2, out Vector3 a2, out _, out _);
                    door.secondHinge = h2;
                    door.secondHingeAxis = a2;
                }

                door.CaptureClosedPose();

                if (_addCollider) EnsureCollider(job.panel);
                BuildTrigger(job.panel.transform, hinge, radius);

                EditorUtility.SetDirty(door);
                built++;
                lines.Add($"  {StationBuild.PathOf(job.panel.transform)} — {job.angle:0}°, " +
                          $"{radius:0.0} m wide, hinge on the {(job.hingeOtherEnd ? "far" : "near")} edge" +
                          (job.secondPanel != null ? ", double" : string.Empty));

                WarnAboutStrandedColliders(job.panel, bounds);
            }

            if (built == 0)
            {
                EditorUtility.DisplayDialog("Nothing to build",
                    "Assign at least one door mesh, then press Build.", "OK");
                return;
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[Hinged doors] Built {built} door(s).\n" + string.Join("\n", lines) +
                      "\n  Walk up and press E. In the Scene view, orange is shut and green is open — if " +
                      "a door swings into the wall, tick Flip swing; if it hinges on the wrong edge, tick " +
                      "Flip hinge.");
        }

        /// <summary>
        /// Hinge line for a panel: the vertical edge at one end of the door's widest horizontal side.
        ///
        /// Measured in world space and converted to the parent's, so a parent that is rotated or scaled
        /// (this project's imports are scaled 2.078) still gets an upright hinge in the right place.
        /// </summary>
        static void MeasureHinge(Transform panel, Bounds worldBounds, bool otherEnd,
                                 out Vector3 hingeLocal, out Vector3 axisLocal,
                                 out float radius, out float height)
        {
            bool alongX = worldBounds.size.x >= worldBounds.size.z;
            float x = worldBounds.center.x, z = worldBounds.center.z;
            if (alongX)
            {
                x = otherEnd ? worldBounds.max.x : worldBounds.min.x;
                radius = worldBounds.size.x;
            }
            else
            {
                z = otherEnd ? worldBounds.max.z : worldBounds.min.z;
                radius = worldBounds.size.z;
            }

            var hingeWorld = new Vector3(x, worldBounds.center.y, z);
            height = Mathf.Max(0.2f, worldBounds.size.y);

            Transform space = panel.parent;
            hingeLocal = space != null ? space.InverseTransformPoint(hingeWorld) : hingeWorld;
            axisLocal = space != null ? space.InverseTransformDirection(Vector3.up) : Vector3.up;
            if (axisLocal.sqrMagnitude < 1e-6f) axisLocal = Vector3.up;
        }

        /// <summary>
        /// A shut door has to stop you. Only added when the panel carries no collider of its own — and
        /// deliberately ON the panel, because a mesh collider baked under the StationColliders root would
        /// stay put while the door swung away from it.
        /// </summary>
        static void EnsureCollider(GameObject panel)
        {
            if (panel.GetComponentInChildren<Collider>(true) != null) return;
            if (!StationBuild.TryLocalBounds(panel, panel.transform, out Bounds local)) return;

            var box = Undo.AddComponent<BoxCollider>(panel);
            box.center = local.center;
            box.size = local.size;
        }

        /// <summary>
        /// The trigger that makes "[E] Open the door" appear, centred ON the hinge line. That is the one
        /// place it can go and stay put: everything else on the panel sweeps away as the door opens, and
        /// with it the prompt you need to shut the door again.
        /// </summary>
        static void BuildTrigger(Transform panel, Vector3 hingeLocal, float radius)
        {
            var trigger = StationBuild.FindOrCreateChild(panel, TriggerName);
            trigger.layer = NasaLayers.Interactable;

            Undo.RecordObject(trigger.transform, "Build hinged door");
            Transform space = panel.parent;
            Vector3 world = space != null ? space.TransformPoint(hingeLocal) : hingeLocal;
            trigger.transform.position = world;
            trigger.transform.rotation = panel.rotation;

            // Cancel the parent's scale so the radius below is in metres, not in import units.
            Vector3 ls = panel.lossyScale;
            trigger.transform.localScale = new Vector3(
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.x)),
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.y)),
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));

            var sphere = StationBuild.GetOrAdd<SphereCollider>(trigger);
            Undo.RecordObject(sphere, "Build hinged door");
            sphere.isTrigger = true;
            sphere.center = Vector3.zero;
            sphere.radius = radius * 0.9f + 1.1f;
        }

        /// <summary>
        /// A door that opens and still blocks the way is the confusing failure here, and it has one
        /// cause: a collider baked for the doorway that belongs to something else — the walk surfaces
        /// under StationColliders, or a wall the door is set into. Those stay put while the panel swings
        /// away from them. Cheaper to say so at build time than to discover it walking into thin air.
        /// </summary>
        static void WarnAboutStrandedColliders(GameObject panel, Bounds bounds)
        {
            Collider[] hits = Physics.OverlapBox(bounds.center, bounds.extents * 0.6f, Quaternion.identity,
                                                 ~0, QueryTriggerInteraction.Ignore);
            var strays = new List<string>();
            foreach (Collider c in hits)
            {
                if (c == null || c.transform.IsChildOf(panel.transform)) continue;
                strays.Add(StationBuild.PathOf(c.transform));
                if (strays.Count == 4) break;
            }
            if (strays.Count == 0) return;

            Debug.LogWarning(
                $"[Hinged doors] '{panel.name}' opens into {strays.Count} collider(s) that are not part of " +
                "the door, so they will not move with it and may still block the doorway:\n  " +
                string.Join("\n  ", strays) +
                "\n  If one of those is a baked walk surface or a solid-mesh collider covering the " +
                "doorway, delete it — the door carries its own.", panel);
        }

        // ================================================================== preview / teardown

        void PreviewAll(bool open)
        {
            int n = 0;
            foreach (Job job in _jobs)
            {
                if (job == null || job.panel == null) continue;
                var door = job.panel.GetComponent<HingedDoor>();
                if (door == null || !door.HasClosedPose) continue;

                Undo.RecordObject(door, open ? "Preview door open" : "Preview door shut");
                Undo.RecordObject(job.panel.transform, open ? "Preview door open" : "Preview door shut");
                if (door.secondPanel != null)
                    Undo.RecordObject(door.secondPanel, open ? "Preview door open" : "Preview door shut");
                door.SetImmediate(open);
                n++;
            }

            if (n == 0)
                Debug.LogWarning("[Hinged doors] Nothing to preview — press Build first.");
            else
                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        void RemoveAll()
        {
            int n = 0;
            foreach (Job job in _jobs)
            {
                if (job == null || job.panel == null) continue;
                var door = job.panel.GetComponent<HingedDoor>();
                if (door == null) continue;

                if (door.HasClosedPose) door.SetImmediate(false);   // leave the panel where you found it
                Transform trigger = job.panel.transform.Find(TriggerName);
                if (trigger != null) Undo.DestroyObjectImmediate(trigger.gameObject);
                Undo.DestroyObjectImmediate(door);
                n++;
            }

            Debug.Log($"[Hinged doors] Removed {n} door(s). The panels are back on their closed pose; " +
                      "any collider added for them is left in place.");
            if (n > 0) EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }
    }
}
