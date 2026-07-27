using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Turns chair models into chairs you can actually sit in. Drop the chair meshes in the buckets,
    /// press Make Sittable, and each one gets:
    ///
    /// <list type="bullet">
    /// <item>a <see cref="SittableChair"/> with a <b>SeatAnchor</b> (where the hips land, and which way
    /// you face) and a <b>StandAnchor</b> (where you get out) — both real child objects, so you can drag
    /// them in the Scene view afterwards;</item>
    /// <item>an <c>InteractTrigger</c> child on the Interactable layer, so "[E] Sit down" appears when
    /// you walk up;</item>
    /// <item>the blue seated figure drawn in the Scene view — the actual pose you'll be put in, visible
    /// without pressing Play.</item>
    /// </list>
    ///
    /// The astronaut gets an <see cref="AstronautSitting"/> at the same time; that is what animates the
    /// body down into the seat and folds the legs.
    ///
    /// The chairs at the half-moon platforms sit at desks, so by default the seat is aimed at the
    /// nearest desk or monitor and you step out sideways — standing up INTO the desk would be the
    /// obvious way to get this wrong.
    /// </summary>
    public sealed class SittableChairTool : EditorWindow
    {
        public enum Facing { AtNearestDesk, ChairsOwnForward }
        public enum StepOut { Right, Left, Behind, Front }

        const string TriggerName = "InteractTrigger";
        const string SeatName = "SeatAnchor";
        const string StandName = "StandAnchor";

        [SerializeField] List<GameObject> _chairs = new List<GameObject>();
        [SerializeField] float _seatHeightFraction = 0.45f;
        [SerializeField] Facing _facing = Facing.AtNearestDesk;
        [SerializeField] StepOut _stepOut = StepOut.Right;
        [SerializeField] float _stepOutDistance = 0.8f;
        [SerializeField] float _triggerMargin = 0.45f;
        [SerializeField] string _prompt = "[E] Sit down";
        [SerializeField] float _sitSeconds = 0.75f;
        [SerializeField] float _standSeconds = 0.6f;
        [SerializeField] bool _keepExistingAnchors = true;

        Vector2 _scroll;

        static readonly string[] DeskWords = { "desk", "table", "moniter", "monitor", "keyboard", "computer" };

        [MenuItem("Tools/NASA Sim/Station/Sittable Chairs")]
        public static void Open()
        {
            var window = GetWindow<SittableChairTool>(true, "Sittable Chairs", true);
            window.minSize = new Vector2(520f, 470f);
            window.PrefillFromSelection();
            window.Show();
        }

        void PrefillFromSelection()
        {
            foreach (var go in Selection.gameObjects)
                if (go != null && go.scene.IsValid() && !_chairs.Contains(go)) _chairs.Add(go);
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Assign chair meshes, press Make Sittable, then walk up to one and press E.\n\n" +
                "The blue figure in the Scene view is exactly where you'll end up sitting — if it floats " +
                "above the seat or sinks into it, change Seat height, or just drag the chair's SeatAnchor " +
                "child. No need to press Play to check.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Find chairs in the scene")) FindChairs();
                if (GUILayout.Button("Add the selection")) PrefillFromSelection();
                if (GUILayout.Button("Clear list")) _chairs.Clear();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Chairs", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(120f));
            for (int i = 0; i < _chairs.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _chairs[i] = (GameObject)EditorGUILayout.ObjectField(
                        _chairs[i], typeof(GameObject), true);
                    using (new EditorGUI.DisabledScope(_chairs[i] == null))
                        if (GUILayout.Button("Show", GUILayout.Width(46f)))
                        {
                            Selection.activeGameObject = _chairs[i];
                            EditorGUIUtility.PingObject(_chairs[i]);
                            SceneView.FrameLastActiveSceneView();
                        }
                    if (GUILayout.Button("×", GUILayout.Width(22f)))
                    {
                        _chairs.RemoveAt(i);
                        i--;
                    }
                }
            }
            if (GUILayout.Button("Add empty row")) _chairs.Add(null);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Seat", EditorStyles.boldLabel);
            _seatHeightFraction = EditorGUILayout.Slider(
                new GUIContent("Seat height", "Where the seat pan is, as a fraction of the chair's total " +
                                              "height. An office chair with a backrest is around 0.45; a " +
                                              "stool is nearer 0.9."),
                _seatHeightFraction, 0.1f, 1f);
            _facing = (Facing)EditorGUILayout.EnumPopup(
                new GUIContent("Face", "Which way you look once seated. At the desk is what you want for " +
                                       "the workstations; the chair's own forward is for chairs standing " +
                                       "on their own."),
                _facing);
            _stepOut = (StepOut)EditorGUILayout.EnumPopup(
                new GUIContent("Get out to the", "Which side you stand up on. Sideways by default — " +
                                                 "standing up 'forward' out of a desk chair would put you " +
                                                 "inside the desk."),
                _stepOut);
            _stepOutDistance = EditorGUILayout.Slider(
                new GUIContent("Step-out distance (m)"), _stepOutDistance, 0.3f, 2f);
            _triggerMargin = EditorGUILayout.Slider(
                new GUIContent("Prompt range (m)", "How far from the chair the '[E] Sit down' prompt " +
                                                   "appears, beyond the chair's own size."),
                _triggerMargin, 0.1f, 1.5f);
            _keepExistingAnchors = EditorGUILayout.Toggle(
                new GUIContent("Keep anchors I've moved", "On: re-running leaves any SeatAnchor or " +
                                                          "StandAnchor you dragged exactly where it is. " +
                                                          "Off: every anchor is snapped back to the " +
                                                          "computed position."),
                _keepExistingAnchors);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);
            _prompt = EditorGUILayout.TextField(new GUIContent("Prompt"), _prompt);
            _sitSeconds = EditorGUILayout.Slider(new GUIContent("Sit down (s)"), _sitSeconds, 0.15f, 3f);
            _standSeconds = EditorGUILayout.Slider(new GUIContent("Stand up (s)"), _standSeconds, 0.15f, 3f);

            EditorGUILayout.Space();
            int live = CountLive();
            using (new EditorGUI.DisabledScope(live == 0))
                if (GUILayout.Button($"Make {live} chair(s) sittable", GUILayout.Height(34f)))
                    Build();

            using (new EditorGUI.DisabledScope(live == 0))
                if (GUILayout.Button("Remove sitting from these chairs"))
                    Remove();
        }

        int CountLive()
        {
            int n = 0;
            foreach (var c in _chairs) if (c != null) n++;
            return n;
        }

        void FindChairs()
        {
            int added = 0;
            foreach (var t in StationBuild.FindAllContaining("chair", "stool", "seat"))
            {
                // The chair GROUP, not its individual legs and casters.
                if (t.name.ToLowerInvariant().Contains("seatanchor")) continue;
                if (t.GetComponentInParent<SittableChair>() != null &&
                    t.GetComponent<SittableChair>() == null) continue;
                if (_chairs.Contains(t.gameObject)) continue;
                _chairs.Add(t.gameObject);
                added++;
            }

            Debug.Log(added == 0
                ? "[Chairs] No new chairs found. Drag them into the buckets yourself if they're named " +
                  "something else."
                : $"[Chairs] Added {added} chair(s). Check the list before building — anything that isn't " +
                  "actually a chair should be removed with the × button.");
        }

        // ================================================================== build

        void Build()
        {
            var astronaut = Object.FindAnyObjectByType<AstronautController>();
            if (astronaut == null)
            {
                EditorUtility.DisplayDialog("No astronaut in the scene",
                    "There's nobody to sit down. Run Tools > NASA Sim > Add Astronaut & Balcony To Scene " +
                    "first, then come back.", "OK");
                return;
            }

            var sitting = StationBuild.GetOrAdd<AstronautSitting>(astronaut.gameObject);
            Undo.RecordObject(sitting, "Set up sitting");
            sitting.astronaut = astronaut;
            sitting.locomotion = astronaut.GetComponent<AstronautLocomotionVisual>();
            sitting.cameraRig = Object.FindAnyObjectByType<AstronautCameraRig>();
            sitting.sitSeconds = _sitSeconds;
            sitting.standSeconds = _standSeconds;
            EditorUtility.SetDirty(sitting);

            int done = 0;
            GameObject last = null;
            foreach (var chair in _chairs)
            {
                if (chair == null) continue;
                if (!StationBuild.RequireSceneObject(chair, "chair")) continue;
                if (!MakeSittable(chair)) continue;
                done++;
                last = chair;
            }

            if (done == 0)
            {
                Debug.LogWarning("[Chairs] Nothing was built — none of the assigned objects had a mesh to " +
                                 "measure a seat from.");
                return;
            }

            if (Object.FindAnyObjectByType<InteractionPromptUI>() == null)
                Debug.LogWarning("[Chairs] There is no interaction prompt in the scene, so you won't SEE " +
                                 "the '[E] Sit down' line (E will still work). Run " +
                                 "Tools > NASA Sim > Setup > Add Interaction System & UI.");

            EditorSceneManager.MarkSceneDirty(last.scene);
            Selection.activeGameObject = last;
            Debug.Log($"[Chairs] {done} chair(s) are now sittable. Walk up and press E; press E again to " +
                      "get up. The blue figure in the Scene view is the seated pose — drag a chair's " +
                      "SeatAnchor child to adjust it, or its StandAnchor to change where you get out.", last);
        }

        bool MakeSittable(GameObject chair)
        {
            if (!StationBuild.TryRendererBounds(chair, out Bounds bounds)) return false;

            var seatComponent = StationBuild.GetOrAdd<SittableChair>(chair);
            Undo.RecordObject(seatComponent, "Make Sittable");
            seatComponent.sitPrompt = _prompt;

            // ---- where the hips go, and which way you look ----
            Vector3 seatPos = new Vector3(bounds.center.x,
                                          bounds.min.y + bounds.size.y * _seatHeightFraction,
                                          bounds.center.z);
            Vector3 forward = ResolveFacing(chair, seatPos);
            Quaternion seatRot = Quaternion.LookRotation(forward, Vector3.up);

            Transform seat = EnsureAnchor(chair.transform, SeatName, seatPos, seatRot);
            seatComponent.seatAnchor = seat;

            // ---- where you get out ----
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            Vector3 outDir;
            switch (_stepOut)
            {
                case StepOut.Left: outDir = -right; break;
                case StepOut.Behind: outDir = -forward; break;
                case StepOut.Front: outDir = forward; break;
                default: outDir = right; break;
            }
            Vector3 standPos = new Vector3(seatPos.x, bounds.min.y, seatPos.z) + outDir * _stepOutDistance;
            seatComponent.standAnchor = EnsureAnchor(chair.transform, StandName, standPos, seatRot);

            BuildTrigger(chair, bounds);
            EditorUtility.SetDirty(seatComponent);
            return true;
        }

        /// <summary>
        /// Aim the seat. The workstation chairs have their desk, monitors and keyboard as siblings, so
        /// "the nearest desk-ish thing that isn't part of me" is a reliable target; a chair on its own
        /// falls back to its own forward axis.
        /// </summary>
        Vector3 ResolveFacing(GameObject chair, Vector3 seatPos)
        {
            if (_facing == Facing.AtNearestDesk)
            {
                Transform best = null;
                float bestSq = float.MaxValue;

                Transform scope = chair.transform.parent != null ? chair.transform.parent : chair.transform;
                foreach (var t in scope.GetComponentsInChildren<Transform>(true))
                {
                    if (t == chair.transform || t.IsChildOf(chair.transform)) continue;
                    string n = t.name.ToLowerInvariant();
                    bool isDesk = false;
                    foreach (string w in DeskWords) if (n.Contains(w)) { isDesk = true; break; }
                    if (!isDesk || !StationBuild.TryRendererBounds(t.gameObject, out Bounds db)) continue;

                    float d = (db.center - seatPos).sqrMagnitude;
                    if (d < bestSq) { bestSq = d; best = t; }
                }

                if (best != null && StationBuild.TryRendererBounds(best.gameObject, out Bounds target))
                {
                    Vector3 toDesk = target.center - seatPos;
                    toDesk.y = 0f;
                    if (toDesk.sqrMagnitude > 1e-4f) return toDesk.normalized;
                }
            }

            Vector3 own = chair.transform.forward;
            own.y = 0f;
            return own.sqrMagnitude > 1e-4f ? own.normalized : Vector3.forward;
        }

        Transform EnsureAnchor(Transform parent, string name, Vector3 worldPos, Quaternion worldRot)
        {
            Transform existing = parent.Find(name);
            if (existing != null && _keepExistingAnchors) return existing;

            GameObject go;
            if (existing != null)
            {
                go = existing.gameObject;
                Undo.RecordObject(go.transform, "Place " + name);
            }
            else
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Create " + name);
                Undo.SetTransformParent(go.transform, parent, "Create " + name);
            }

            go.transform.SetPositionAndRotation(worldPos, worldRot);
            // Cancel the model's import scale so the anchor reads as plain world metres.
            Vector3 ls = parent.lossyScale;
            go.transform.localScale = new Vector3(1f / NonZero(ls.x), 1f / NonZero(ls.y), 1f / NonZero(ls.z));
            return go.transform;
        }

        /// <summary>
        /// The proximity trigger, on a CHILD so the chair model keeps its own layer and colliders — the
        /// same arrangement <see cref="MakeInteractableTool"/> uses for fruit and ducks.
        /// </summary>
        void BuildTrigger(GameObject chair, Bounds bounds)
        {
            Transform existing = chair.transform.Find(TriggerName);
            GameObject trigger;
            if (existing != null)
            {
                trigger = existing.gameObject;
                Undo.RecordObject(trigger.transform, "Make Sittable");
            }
            else
            {
                trigger = new GameObject(TriggerName);
                Undo.RegisterCreatedObjectUndo(trigger, "Make Sittable");
                Undo.SetTransformParent(trigger.transform, chair.transform, "Make Sittable");
            }

            trigger.layer = NasaLayers.Interactable;
            trigger.transform.rotation = Quaternion.identity;
            Vector3 ls = chair.transform.lossyScale;
            trigger.transform.localScale =
                new Vector3(1f / NonZero(ls.x), 1f / NonZero(ls.y), 1f / NonZero(ls.z));
            trigger.transform.position = bounds.center;

            var col = StationBuild.GetOrAdd<SphereCollider>(trigger);
            Undo.RecordObject(col, "Make Sittable");
            col.isTrigger = true;
            col.center = Vector3.zero;
            col.radius = Mathf.Max(0.3f,
                Mathf.Max(bounds.extents.x, bounds.extents.z) + _triggerMargin);
            EditorUtility.SetDirty(col);
        }

        void Remove()
        {
            int n = 0;
            foreach (var chair in _chairs)
            {
                if (chair == null) continue;
                var sit = chair.GetComponent<SittableChair>();
                if (sit == null) continue;

                foreach (string child in new[] { TriggerName, SeatName, StandName })
                {
                    Transform t = chair.transform.Find(child);
                    if (t != null) Undo.DestroyObjectImmediate(t.gameObject);
                }
                Undo.DestroyObjectImmediate(sit);
                n++;
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[Chairs] Sitting removed from {n} chair(s). The AstronautSitting component on the " +
                      "astronaut is harmless with no chairs about, so it is left alone.");
        }

        static float NonZero(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;
    }
}
