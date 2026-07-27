using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Wires the cargo elevator: which group is the car, which two meshes are the door panels, how far up
    /// it goes — then builds the ride zone, the floor, the call buttons and the door motion round them.
    ///
    /// The point of this window is that you can SEE the answer before you press Play. Once built, the
    /// Scene view shows the car's box at the bottom stop in green, the same box at the top stop in cyan,
    /// four corner rails between them and the travel written on the shaft; the door panels get dashed
    /// outlines where they will slide to. Change Travel Height and the cyan box moves — so lining the car
    /// up with the balcony is a drag-and-read job. The Preview buttons go further and actually park the
    /// car at the top (or open the doors) in the editor, which is the honest way to check a fit.
    /// </summary>
    public sealed class ElevatorTool : EditorWindow
    {
        const string GeneratedRootName = "StationElevators";
        const string RideZoneName = "RideZone";
        const string FloorName = "CarFloor";
        const string TriggerName = "InteractTrigger";
        const string ButtonMatPath = "Assets/_Project/Materials/ElevatorButton.mat";

        [SerializeField] GameObject _car;
        [SerializeField] GameObject _panelA;
        [SerializeField] GameObject _panelB;
        [SerializeField] GameObject _structure;

        [SerializeField] float _travelHeight = 4.7f;
        [SerializeField] float _speed = 1.2f;
        [SerializeField] bool _startWithDoorsOpen = true;

        [SerializeField] float _doorOpenScale = 1f;
        [SerializeField] float _doorSeconds = 1.1f;

        [SerializeField] bool _buildFloor = true;
        [SerializeField] float _rideHeight = 2.4f;
        [SerializeField] bool _buildButtons = true;
        [SerializeField] GameObject _bottomButtonMesh;
        [SerializeField] GameObject _topButtonMesh;
        [SerializeField] GameObject _carButtonMesh;

        [MenuItem("Tools/NASA Sim/Station/Elevator")]
        public static void Open()
        {
            var window = GetWindow<ElevatorTool>(true, "Elevator", true);
            window.minSize = new Vector2(540f, 560f);
            if (window._car == null) window.AutoFind(silent: true);
            window.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Assign the car (the 'cargo' group) and the two door panels, set how far up it travels, " +
                "press Build.\n\n" +
                "Afterwards the Scene view shows the whole run: GREEN box = the bottom stop, CYAN box = " +
                "the top stop, rails and the travel distance in between, and dashed outlines where the " +
                "door panels slide to.",
                MessageType.Info);

            if (GUILayout.Button("Find the elevator in the scene")) AutoFind(silent: false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Parts", EditorStyles.boldLabel);
            _car = Bucket("Car (cargo)", "The moving box. Anything parented under it rides along.", _car);
            _panelA = Bucket("Door panel A", "One half of the sliding door.", _panelA);
            _panelB = Bucket("Door panel B", "The other half. It slides the opposite way.", _panelB);
            _structure = Bucket("Shaft structure", "Optional — only used to measure how far the car should " +
                                                   "travel.", _structure);

            DrawMeasurements();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Travel", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _travelHeight = EditorGUILayout.FloatField(
                    new GUIContent("Travel height (m)", "How far above its current position the car rises. " +
                                                        "The cyan box in the Scene view is where that puts it."),
                    _travelHeight);
                using (new EditorGUI.DisabledScope(_car == null || _structure == null))
                    if (GUILayout.Button("Measure", GUILayout.Width(70f)))
                        MeasureTravel();
            }
            _speed = EditorGUILayout.Slider(new GUIContent("Speed (m/s)"), _speed, 0.2f, 6f);
            _startWithDoorsOpen = EditorGUILayout.Toggle(
                new GUIContent("Start with doors open", "So the car is waiting to be walked into."),
                _startWithDoorsOpen);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Doors", EditorStyles.boldLabel);
            _doorOpenScale = EditorGUILayout.Slider(
                new GUIContent("Open distance", "As a multiple of a panel's own width. 1 slides each " +
                                                "panel exactly clear of the opening."),
                _doorOpenScale, 0.2f, 2f);
            _doorSeconds = EditorGUILayout.Slider(new GUIContent("Open/close (s)"), _doorSeconds, 0.2f, 4f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Riding", EditorStyles.boldLabel);
            _buildFloor = EditorGUILayout.Toggle(
                new GUIContent("Add a floor to the car", "A thin box across the bottom of the car so you " +
                                                         "have something to stand on. Turn off if the " +
                                                         "car's own model already has a floor collider."),
                _buildFloor);
            _rideHeight = EditorGUILayout.Slider(
                new GUIContent("Ride zone height (m)", "How tall the volume is that decides who travels " +
                                                       "with the car. Stand inside it and you go up."),
                _rideHeight, 0.6f, 5f);

            EditorGUILayout.Space();
            _buildButtons = EditorGUILayout.Toggle(
                new GUIContent("Build call buttons", "One at each landing plus one inside the car."),
                _buildButtons);
            using (new EditorGUI.DisabledScope(!_buildButtons))
            {
                EditorGUI.indentLevel++;
                _bottomButtonMesh = Bucket("Bottom button mesh", "Optional. Empty = an orange marker cube.",
                                           _bottomButtonMesh);
                _topButtonMesh = Bucket("Top button mesh", "Optional.", _topButtonMesh);
                _carButtonMesh = Bucket("In-car button mesh", "Optional. Gets parented to the car so it " +
                                                              "rides with you.", _carButtonMesh);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_car == null))
                if (GUILayout.Button("Build / update the elevator", GUILayout.Height(34f)))
                    Build();

            DrawPreviewRow();
        }

        GameObject Bucket(string label, string tooltip, GameObject value) =>
            (GameObject)EditorGUILayout.ObjectField(new GUIContent(label, tooltip), value,
                                                    typeof(GameObject), true);

        void DrawMeasurements()
        {
            if (_car == null || !StationBuild.TryRendererBounds(_car, out Bounds cb))
            {
                EditorGUILayout.LabelField(" ", "Assign the car to see its measurements.",
                                           EditorStyles.miniLabel);
                return;
            }

            string doorInfo = "no door assigned";
            if (TryDoorAxis(out Vector3 dir, out float width, out _))
                doorInfo = $"panels part along {AxisWord(dir)}, {width:0.00} m wide each";

            EditorGUILayout.LabelField(" ",
                $"car {cb.size.x:0.0} × {cb.size.y:0.0} × {cb.size.z:0.0} m, floor at y = {cb.min.y:0.00}   ·   {doorInfo}",
                EditorStyles.miniLabel);
        }

        void DrawPreviewRow()
        {
            var rig = FindRig();
            using (new EditorGUI.DisabledScope(rig == null))
            {
                EditorGUILayout.LabelField("Preview (moves the real objects — undoable)",
                                           EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Car → bottom")) PreviewCar(rig, atTop: false);
                    if (GUILayout.Button("Car → top")) PreviewCar(rig, atTop: true);
                    if (GUILayout.Button("Doors closed")) PreviewDoors(rig, open: false);
                    if (GUILayout.Button("Doors open")) PreviewDoors(rig, open: true);
                }
                if (GUILayout.Button("Remove the elevator rig")) RemoveRig(rig);
            }
        }

        // ================================================================== find & measure

        void AutoFind(bool silent)
        {
            // The cargo box is the INNERMOST thing called "Elevator": this model nests the car inside an
            // assembly of the same name, alongside the doors, the structure and the button panel.
            Transform car = null;
            int deepest = -1;
            foreach (var t in StationBuild.FindAllContaining("elevator", "cargo", "lift"))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("door") || n.Contains("structure") || n.Contains("sturcture")) continue;
                if (t.GetComponentInChildren<MeshFilter>(true) == null) continue;
                int depth = Depth(t);
                if (depth > deepest) { deepest = depth; car = t; }
            }
            if (car != null) _car = car.gameObject;

            Transform doors = StationBuild.FindFirstContaining("elevatordoor");
            if (doors == null && car != null && car.parent != null)
                foreach (Transform sibling in car.parent)
                    if (sibling.name.ToLowerInvariant().Contains("door")) { doors = sibling; break; }

            if (doors != null)
            {
                var panels = doors.GetComponentsInChildren<MeshFilter>(true);
                if (panels.Length >= 2)
                {
                    _panelA = panels[0].gameObject;
                    _panelB = panels[1].gameObject;
                }
            }

            Transform structure = StationBuild.FindFirstContaining("sturcture", "structure");
            if (structure != null) _structure = structure.gameObject;

            if (_car != null && _structure != null) MeasureTravel();

            if (silent) return;
            if (_car == null)
                Debug.LogWarning("[Elevator] Couldn't find anything called Elevator / cargo / lift in the " +
                                 "scene. Drag the car group into the bucket yourself.");
            else
                Debug.Log($"[Elevator] Car: {StationBuild.PathOf(_car.transform)}\n" +
                          $"  Panels: {(_panelA != null ? _panelA.name : "—")} / " +
                          $"{(_panelB != null ? _panelB.name : "—")}\n" +
                          $"  Structure: {(_structure != null ? _structure.name : "—")}\n" +
                          "  Check these are the right objects, then Build.");
        }

        static int Depth(Transform t)
        {
            int d = 0;
            for (Transform p = t.parent; p != null; p = p.parent) d++;
            return d;
        }

        void MeasureTravel()
        {
            if (_car == null || _structure == null) return;
            if (!StationBuild.TryRendererBounds(_car, out Bounds cb)) return;
            if (!StationBuild.TryRendererBounds(_structure, out Bounds sb)) return;

            // Rise until the car's roof reaches the top of the shaft.
            _travelHeight = Mathf.Max(0.1f, sb.max.y - cb.max.y);
        }

        /// <summary>Which way the panels part, how wide one is along it, and where the doorway is.</summary>
        bool TryDoorAxis(out Vector3 dir, out float panelWidth, out Bounds doorway)
        {
            dir = Vector3.right;
            panelWidth = 0f;
            doorway = default;
            if (_panelA == null || _panelB == null) return false;
            if (!StationBuild.TryRendererBounds(_panelA, out Bounds ba)) return false;
            if (!StationBuild.TryRendererBounds(_panelB, out Bounds bb)) return false;

            Vector3 sep = bb.center - ba.center;
            if (sep.sqrMagnitude < 1e-6f) return false;
            dir = sep.normalized;

            // Support width of the panel's box along that direction — how far it must move to be clear.
            panelWidth = Mathf.Abs(dir.x) * ba.size.x +
                         Mathf.Abs(dir.y) * ba.size.y +
                         Mathf.Abs(dir.z) * ba.size.z;

            doorway = ba;
            doorway.Encapsulate(bb);
            return panelWidth > 1e-4f;
        }

        static string AxisWord(Vector3 dir)
        {
            Vector3 a = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
            if (a.x >= a.y && a.x >= a.z) return "X";
            return a.z >= a.y ? "Z" : "Y";
        }

        ElevatorController FindRig()
        {
            foreach (var c in Object.FindObjectsByType<ElevatorController>(FindObjectsInactive.Include))
                if (_car == null || c.car == _car.transform) return c;
            return null;
        }

        // ================================================================== build

        void Build()
        {
            if (!StationBuild.RequireSceneObject(_car, "car")) return;
            if (!StationBuild.TryRendererBounds(_car, out Bounds carBounds))
            {
                EditorUtility.DisplayDialog("Nothing to measure",
                    $"'{_car.name}' has no meshes, so there is no car to move. Assign the group that " +
                    "actually holds the cargo box.", "OK");
                return;
            }

            var root = StationBuild.GeneratedRoot(GeneratedRootName, clearChildren: false);
            var rigGo = StationBuild.FindOrCreateChild(root.transform, "Elevator_" + _car.name.Replace(":", "_"));
            var ctrl = StationBuild.GetOrAdd<ElevatorController>(rigGo);
            Undo.RecordObject(ctrl, "Build Elevator");
            Undo.RecordObject(_car.transform, "Build Elevator");

            // Put the car back on its bottom stop before re-reading the authored pose. Without this,
            // building while the Preview has parked the car at the top would adopt the TOP as the new
            // bottom — and the lift would climb away into the roof, one build at a time.
            if (ctrl.car != null) ctrl.SetImmediate(atTop: false);

            ctrl.car = _car.transform;
            ctrl.travelHeight = _travelHeight;
            ctrl.speed = _speed;
            ctrl.startWithDoorsOpen = _startWithDoorsOpen;
            ctrl.CaptureBottomStop();          // the car is where the model author left it: that's the bottom

            // ---- ride zone (child of the car, so it travels with it) ----
            Vector3 inv = Inverse(_car.transform.lossyScale);
            var zoneGo = StationBuild.FindOrCreateChild(_car.transform, RideZoneName);
            Undo.RecordObject(zoneGo.transform, "Build Elevator");
            zoneGo.transform.rotation = _car.transform.rotation;
            zoneGo.transform.position = new Vector3(carBounds.center.x,
                                                    carBounds.min.y + _rideHeight * 0.5f,
                                                    carBounds.center.z);
            zoneGo.transform.localScale = inv;         // honest world metres whatever the import scale was
            var zone = StationBuild.GetOrAdd<BoxCollider>(zoneGo);
            Undo.RecordObject(zone, "Build Elevator");
            zone.isTrigger = true;
            zone.center = Vector3.zero;
            zone.size = new Vector3(carBounds.size.x * 0.85f, _rideHeight, carBounds.size.z * 0.85f);
            ctrl.rideZone = zone;

            // ---- something to stand on ----
            if (_buildFloor)
            {
                var floorGo = StationBuild.FindOrCreateChild(_car.transform, FloorName);
                Undo.RecordObject(floorGo.transform, "Build Elevator");
                floorGo.transform.rotation = _car.transform.rotation;
                floorGo.transform.position = new Vector3(carBounds.center.x, carBounds.min.y + 0.05f,
                                                         carBounds.center.z);
                floorGo.transform.localScale = inv;
                var floor = StationBuild.GetOrAdd<BoxCollider>(floorGo);
                Undo.RecordObject(floor, "Build Elevator");
                floor.isTrigger = false;
                floor.center = Vector3.zero;
                floor.size = new Vector3(carBounds.size.x * 0.95f, 0.1f, carBounds.size.z * 0.95f);
            }

            // ---- doors ----
            SlidingDoorPair pair = BuildDoors(rigGo);
            ctrl.doors = pair;

            // ---- buttons ----
            int buttons = _buildButtons ? BuildButtons(rigGo, ctrl, carBounds) : 0;

            EditorUtility.SetDirty(ctrl);
            EditorSceneManager.MarkSceneDirty(rigGo.scene);
            Selection.activeGameObject = rigGo;

            string doorNote = "no doors wired (assign both panels to get them)";
            if (pair != null && TryDoorAxis(out _, out float panelWidth, out _))
                doorNote = $"doors slide {panelWidth * _doorOpenScale:0.00} m each way";
            Debug.Log($"[Elevator] Built on '{_car.name}': travels {_travelHeight:0.00} m at {_speed:0.0} m/s, " +
                      $"{doorNote}, {buttons} call button(s).\n" +
                      "  Scene view: GREEN box = bottom stop, CYAN box = top stop. If the cyan box doesn't " +
                      "line up with the balcony, change Travel Height and build again — or press " +
                      "'Car → top' to park it there and look.\n" +
                      "  Stand inside the yellow ride zone and the car carries you (a CharacterController " +
                      "is not pushed by a moving floor, so riders are moved deliberately).", rigGo);
        }

        SlidingDoorPair BuildDoors(GameObject rigGo)
        {
            if (_panelA == null || _panelB == null) return null;
            if (!TryDoorAxis(out Vector3 dir, out float panelWidth, out _)) return null;

            // The component goes on the panels' shared parent when they have one, so it travels with the
            // door assembly rather than sitting in the generated rig by itself.
            Transform host = _panelA.transform.parent != null &&
                             _panelA.transform.parent == _panelB.transform.parent
                ? _panelA.transform.parent
                : rigGo.transform;

            var pair = StationBuild.GetOrAdd<SlidingDoorPair>(host.gameObject);
            Undo.RecordObject(pair, "Build Elevator Doors");
            Undo.RecordObject(_panelA.transform, "Build Elevator Doors");
            Undo.RecordObject(_panelB.transform, "Build Elevator Doors");

            // Shut the panels before re-reading their closed pose, for the same reason the car is put back
            // on its bottom stop: a build done while the Preview holds them open would make "open" the new
            // closed, and the doors would walk further apart with every build.
            if (pair.panelA != null || pair.panelB != null) pair.SetImmediate(open: false);

            pair.panelA = _panelA.transform;
            pair.panelB = _panelB.transform;
            pair.moveDuration = _doorSeconds;
            pair.CaptureClosedPose();          // the panels are shut in the model: that's the closed pose

            float distance = panelWidth * _doorOpenScale;
            Vector3 worldA = -dir * distance;
            Vector3 worldB = dir * distance;
            pair.slideA = _panelA.transform.parent != null
                ? _panelA.transform.parent.InverseTransformVector(worldA) : worldA;
            pair.slideB = _panelB.transform.parent != null
                ? _panelB.transform.parent.InverseTransformVector(worldB) : worldB;

            EditorUtility.SetDirty(pair);
            return pair;
        }

        int BuildButtons(GameObject rigGo, ElevatorController ctrl, Bounds carBounds)
        {
            // Where the doorway is and which side you approach from. With no doors, fall back to the car's
            // own +Z face so the buttons still land somewhere sensible.
            Vector3 dir = _car.transform.right;
            Bounds doorway = carBounds;
            if (TryDoorAxis(out Vector3 measuredDir, out _, out Bounds measuredDoorway))
            {
                dir = measuredDir;
                doorway = measuredDoorway;
            }

            Vector3 approach = Vector3.Cross(Vector3.up, dir);
            if (approach.sqrMagnitude < 1e-6f) approach = Vector3.forward;
            approach.Normalize();
            Vector3 toDoor = doorway.center - carBounds.center;
            toDoor.y = 0f;
            Vector3 outward = Vector3.Dot(approach, toDoor) >= 0f ? approach : -approach;

            float doorHalf = Mathf.Abs(dir.x) * doorway.extents.x +
                             Mathf.Abs(dir.z) * doorway.extents.z;
            float buttonY = carBounds.min.y + 1.15f;
            Vector3 side = dir * (doorHalf + 0.45f);
            Vector3 landing = new Vector3(doorway.center.x, buttonY, doorway.center.z)
                              + outward * 0.55f + side;

            var mat = SceneBootstrap.MakeMat(ButtonMatPath, "Universal Render Pipeline/Lit",
                                             new Color(0.95f, 0.55f, 0.10f));
            int n = 0;

            if (MakeButton(rigGo.transform, "Button_Bottom", landing, outward, _bottomButtonMesh, mat,
                           ElevatorCallButton.Call.Bottom, ctrl)) n++;
            if (MakeButton(rigGo.transform, "Button_Top", landing + Vector3.up * _travelHeight, outward,
                           _topButtonMesh, mat, ElevatorCallButton.Call.Top, ctrl)) n++;

            // The in-car button hangs on the inside wall beside the door, parented to the car so it rides.
            float carHalf = Mathf.Abs(outward.x) * carBounds.extents.x +
                            Mathf.Abs(outward.z) * carBounds.extents.z;
            Vector3 inCar = new Vector3(carBounds.center.x, buttonY, carBounds.center.z)
                            + outward * Mathf.Max(0f, carHalf - 0.22f);
            if (MakeButton(_car.transform, "Button_Car", inCar, -outward, _carButtonMesh, mat,
                           ElevatorCallButton.Call.OtherEnd, ctrl)) n++;

            return n;
        }

        /// <summary>
        /// A call panel. With a mesh assigned the component and its trigger go ON that mesh (so your model
        /// is the button); with none, an orange marker cube stands in — swap it later by assigning the
        /// mesh and building again.
        /// </summary>
        bool MakeButton(Transform parent, string name, Vector3 pos, Vector3 face, GameObject mesh,
                        Material mat, ElevatorCallButton.Call call, ElevatorController ctrl)
        {
            GameObject host;
            if (mesh != null)
            {
                host = mesh;
                BuildInteractTrigger(host);
            }
            else
            {
                Transform existing = parent.Find(name);
                if (existing != null)
                {
                    host = existing.gameObject;
                    Undo.RecordObject(host.transform, "Build Elevator Buttons");
                }
                else
                {
                    host = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    host.name = name;
                    Undo.RegisterCreatedObjectUndo(host, "Build Elevator Buttons");
                    Undo.SetTransformParent(host.transform, parent, "Build Elevator Buttons");
                }

                host.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(face, Vector3.up));
                Vector3 inv = Inverse(parent.lossyScale);
                host.transform.localScale = inv * 0.22f;
                host.layer = NasaLayers.Interactable;
                SceneBootstrap.SetMaterial(host, mat);

                var box = StationBuild.GetOrAdd<BoxCollider>(host);
                Undo.RecordObject(box, "Build Elevator Buttons");
                box.isTrigger = true;
                box.size = Vector3.one * 4f;         // ~0.9 m world trigger: a comfortable prompt range
            }

            var button = StationBuild.GetOrAdd<ElevatorCallButton>(host);
            Undo.RecordObject(button, "Build Elevator Buttons");
            button.elevator = ctrl;
            button.call = call;
            EditorUtility.SetDirty(button);
            return true;
        }

        /// <summary>Proximity trigger on a CHILD, so an assigned model keeps its own layer and colliders.</summary>
        static void BuildInteractTrigger(GameObject host)
        {
            if (!StationBuild.TryRendererBounds(host, out Bounds b)) return;

            var trigger = StationBuild.FindOrCreateChild(host.transform, TriggerName);
            Undo.RecordObject(trigger.transform, "Build Elevator Buttons");
            trigger.layer = NasaLayers.Interactable;
            trigger.transform.rotation = Quaternion.identity;
            trigger.transform.localScale = Inverse(host.transform.lossyScale);
            trigger.transform.position = b.center;

            var col = StationBuild.GetOrAdd<SphereCollider>(trigger);
            Undo.RecordObject(col, "Build Elevator Buttons");
            col.isTrigger = true;
            col.center = Vector3.zero;
            col.radius = Mathf.Max(0.5f, Mathf.Max(b.extents.x, b.extents.z) + 0.5f);
        }

        // ================================================================== preview / teardown

        void PreviewCar(ElevatorController rig, bool atTop)
        {
            if (rig == null || rig.car == null) return;
            Undo.RecordObject(rig.car, atTop ? "Preview car at top" : "Preview car at bottom");
            Undo.RecordObject(rig, "Preview car");
            rig.SetImmediate(atTop);
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
        }

        void PreviewDoors(ElevatorController rig, bool open)
        {
            if (rig == null || rig.doors == null) return;
            var d = rig.doors;
            if (d.panelA != null) Undo.RecordObject(d.panelA, "Preview doors");
            if (d.panelB != null) Undo.RecordObject(d.panelB, "Preview doors");
            Undo.RecordObject(d, "Preview doors");
            d.SetImmediate(open);
            EditorSceneManager.MarkSceneDirty(d.gameObject.scene);
        }

        void RemoveRig(ElevatorController rig)
        {
            if (rig == null) return;

            // Put everything back where the model author had it before the rig disappears.
            if (rig.car != null)
            {
                Undo.RecordObject(rig.car, "Remove elevator");
                rig.SetImmediate(atTop: false);
            }
            if (rig.doors != null)
            {
                if (rig.doors.panelA != null) Undo.RecordObject(rig.doors.panelA, "Remove elevator");
                if (rig.doors.panelB != null) Undo.RecordObject(rig.doors.panelB, "Remove elevator");
                rig.doors.SetImmediate(open: false);
                Undo.DestroyObjectImmediate(rig.doors);
            }
            if (rig.car != null)
                foreach (string child in new[] { RideZoneName, FloorName, "Button_Car" })
                {
                    Transform t = rig.car.Find(child);
                    if (t != null) Undo.DestroyObjectImmediate(t.gameObject);
                }

            Undo.DestroyObjectImmediate(rig.gameObject);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Elevator] Rig removed and the car and doors put back at their authored poses.");
        }

        static Vector3 Inverse(Vector3 scale) =>
            new Vector3(1f / NonZero(scale.x), 1f / NonZero(scale.y), 1f / NonZero(scale.z));

        static float NonZero(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;
    }
}
