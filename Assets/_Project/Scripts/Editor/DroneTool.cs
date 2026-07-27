using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Turns the imported drone from scenery into something you can switch on: press E near its pad and
    /// it spins up, lifts off and flies a loop until you call it back.
    ///
    /// <b>The pad is a sibling of the drone, not its parent.</b> The interaction sensor walks UP from a
    /// trigger collider to find the interactable, so the trigger has to live under the component — and if
    /// the component were on the drone, the trigger would fly away with it and you could never call it
    /// back. So the pad is created alongside the drone, under the same import, holding the trigger and
    /// the flight logic; the drone is just a transform it moves. It also means the parked pose can be
    /// stored in pad-local space, which survives the whole environment being moved or rescaled.
    ///
    /// <b>Reachability is the thing to check.</b> This drone sits 26 m above the ground, so a trigger
    /// centred on it would be a prompt nobody can walk to. The window raycasts down, reports the drop,
    /// and can put the trigger on the floor underneath instead — a call button under the drone rather
    /// than an interaction you would need the elevator and a ladder to reach.
    /// </summary>
    public sealed class DroneTool : EditorWindow
    {
        const string PadName = "DronePad";
        const string TriggerName = "InteractTrigger";
        const string RouteName = "Route";

        [SerializeField] GameObject _drone;
        [SerializeField] int _waypoints = 6;
        [SerializeField] float _radius = 14f;
        [SerializeField] float _height = 8f;
        [SerializeField] float _cruise = 6f;
        [SerializeField] float _reach = 2.5f;
        [SerializeField] bool _triggerOnGround = true;
        [SerializeField] float _rotorRpm = 900f;
        [SerializeField] string _rotorWords = "gear, rotor, prop, blade";

        [MenuItem("Tools/NASA Sim/Station/Flying Drone")]
        public static void Open()
        {
            var w = GetWindow<DroneTool>(true, "Flying Drone", true);
            w.minSize = new Vector2(500f, 560f);
            w.AutoFind();
            w.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Makes the drone flyable. Build it, then walk up to the pad and press E.\n\n" +
                "The route is a ring of draggable markers — if the drone clips the dome or a mast, move " +
                "a marker rather than tuning a radius.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Parts", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _drone = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent("Drone", "The drone body. Found automatically."),
                    _drone, typeof(GameObject), true);
                if (GUILayout.Button("Find", GUILayout.Width(52f))) AutoFind();
            }

            _rotorWords = EditorGUILayout.TextField(
                new GUIContent("Rotor names contain", "Comma-separated. This model's discs are called " +
                                                      "pGear1, one inside each of the four pipes."),
                _rotorWords);

            DrawReachCheck();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Route", EditorStyles.boldLabel);
            _waypoints = EditorGUILayout.IntSlider(
                new GUIContent("Markers", "Three or more. They are laid out in a ring to start with — " +
                                          "drag them afterwards."),
                _waypoints, 3, 12);
            _radius = EditorGUILayout.Slider(new GUIContent("Ring radius (m)"), _radius, 3f, 80f);
            _height = EditorGUILayout.Slider(
                new GUIContent("Flight height (m)", "Above the pad."), _height, 2f, 60f);
            _cruise = EditorGUILayout.Slider(new GUIContent("Cruise speed (m/s)"), _cruise, 1f, 25f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fittings", EditorStyles.boldLabel);
            _rotorRpm = EditorGUILayout.Slider(new GUIContent("Rotor rpm"), _rotorRpm, 0f, 3000f);
            _reach = EditorGUILayout.Slider(
                new GUIContent("Prompt reach (m)", "How close you must be for [E] to appear."),
                _reach, 1f, 8f);
            _triggerOnGround = EditorGUILayout.Toggle(
                new GUIContent("Call button on the ground",
                               "Put the prompt on the floor beneath the drone instead of on the drone " +
                               "itself. Leave this on unless the drone is parked somewhere you can " +
                               "actually walk up to."),
                _triggerOnGround);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_drone == null))
                if (GUILayout.Button("Build / update the drone", GUILayout.Height(34f))) Build();

            var rig = Object.FindAnyObjectByType<DroneFlight>();
            using (new EditorGUI.DisabledScope(rig == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Park it back on the pad")) ParkIt(rig);
                    if (GUILayout.Button("Remove")) Remove(rig);
                }
            }
        }

        /// <summary>
        /// A prompt you cannot walk to is the quiet way this feature fails, so the window measures the
        /// drop to whatever is underneath and says so before you build.
        /// </summary>
        void DrawReachCheck()
        {
            if (_drone == null) return;

            Vector3 from = _drone.transform.position;
            bool hit = Physics.Raycast(from + Vector3.up * 0.5f, Vector3.down, out RaycastHit info, 400f,
                                       ~0, QueryTriggerInteraction.Ignore);

            if (!hit)
            {
                EditorGUILayout.HelpBox(
                    "Nothing found underneath the drone within 400 m. The call button will be left on the " +
                    "drone itself — check you can actually reach it.", MessageType.Warning);
                return;
            }

            float drop = from.y - info.point.y;
            if (drop > 3f)
                EditorGUILayout.HelpBox(
                    $"The drone is parked {drop:0.0} m above '{info.collider.name}'. You cannot press E " +
                    "from down there, so leave 'Call button on the ground' ticked and the prompt goes at " +
                    "the foot of it instead.", MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    $"The drone sits {drop:0.0} m above '{info.collider.name}' — close enough to walk up " +
                    "to it directly.", MessageType.None);
        }

        // ================================================================== find

        void AutoFind()
        {
            if (_drone != null) return;

            // The user's own Hierarchy label has no colon in it; every imported part carries a Maya
            // namespace like "newGreenHouse_2:Drone:body1". Dropping anything with a colon picks the
            // group the user actually sees rather than one of its forty pieces.
            Transform best = null;
            foreach (Transform t in StationBuild.FindAllContaining("drone"))
            {
                if (t.name.Contains(":")) continue;
                if (best == null || t.childCount > best.childCount) best = t;
            }

            if (best != null) _drone = best.gameObject;
            else Debug.Log("[Drone] No object called 'Drone' in the scene — drop one in by hand.");
        }

        List<Transform> FindRotors(Transform root)
        {
            var found = new List<Transform>();
            string[] words = _rotorWords.Split(',');

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                string lower = t.name.ToLowerInvariant();
                foreach (string raw in words)
                {
                    string w = raw.Trim().ToLowerInvariant();
                    if (w.Length == 0 || !lower.Contains(w)) continue;
                    if (t.GetComponent<Renderer>() == null) continue;
                    found.Add(t);
                    break;
                }
            }
            return found;
        }

        // ================================================================== build

        void Build()
        {
            if (!StationBuild.RequireSceneObject(_drone, "drone")) return;

            Transform drone = _drone.transform;

            // Existing rig? Put the drone back on its pad BEFORE anything is measured. Rebuilding while
            // it is hovering mid-route would otherwise adopt that spot as the new parked pose, and the
            // pad would climb a little higher on every build — the same trap the doors have.
            var old = Object.FindAnyObjectByType<DroneFlight>();
            if (old != null && old.HasHome && old.body == drone)
            {
                Undo.RecordObject(drone, "Build drone");
                old.SetImmediateDocked();
            }

            // A SIBLING of the drone, so both live in the import's space: the parked pose is stored
            // pad-relative and survives the environment being moved, and the pad itself never flies away.
            Transform space = drone.parent;
            GameObject pad = space != null
                ? StationBuild.FindOrCreateChild(space, PadName)
                : GameObject.Find(PadName) ?? new GameObject(PadName);

            Undo.RegisterFullObjectHierarchyUndo(pad, "Build drone");
            pad.transform.SetPositionAndRotation(drone.position, drone.rotation);

            var rig = StationBuild.GetOrAdd<DroneFlight>(pad);
            Undo.RecordObject(rig, "Build drone");
            rig.body = drone;
            rig.patrolRadius = _radius;
            rig.patrolHeight = _height;
            rig.cruiseSpeed = _cruise;
            rig.rotorRpm = _rotorRpm;
            rig.rotors = FindRotors(drone);
            rig.CaptureHome();

            BuildRoute(rig, pad.transform, drone.position);
            BuildTrigger(pad.transform, drone);

            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(pad.scene);
            Selection.activeGameObject = pad;

            float drop = TriggerDrop(drone.position);

            Debug.Log($"[Drone] '{_drone.name}' is flyable.\n" +
                      $"  {rig.rotors.Count} rotor(s) found by name" +
                      (rig.rotors.Count == 0
                          ? $" — none matched '{_rotorWords}', so nothing will spin. Check what the discs " +
                            "are actually called and widen the list."
                          : ", spun in counter-rotating pairs.") + "\n" +
                      $"  {_waypoints} route markers in a {_radius:0} m ring, {_height:0} m up. Drag any " +
                      "of them; the blue curve in the scene view is the path it will actually fly.\n" +
                      $"  Prompt sits {(_triggerOnGround && drop > 3f ? $"on the ground {drop:0.0} m below the drone" : "on the drone")}, " +
                      $"reachable from {_reach:0.0} m.\n" +
                      "  Press E to launch, E again to bring it home. It lands itself.", pad);
        }

        void BuildRoute(DroneFlight rig, Transform pad, Vector3 home)
        {
            var route = StationBuild.FindOrCreateChild(pad, RouteName);
            Undo.RecordObject(route.transform, "Build drone");
            route.transform.SetPositionAndRotation(home, Quaternion.identity);

            // Rebuild from scratch: changing the marker count while keeping the old ones would leave
            // strays sitting in the middle of the ring.
            for (int i = route.transform.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(route.transform.GetChild(i).gameObject);

            rig.waypoints = new List<Transform>();
            Vector3 centre = home + Vector3.up * _height;

            for (int i = 0; i < _waypoints; i++)
            {
                float a = i / (float)_waypoints * Mathf.PI * 2f;
                var marker = new GameObject($"Waypoint_{i + 1}");
                Undo.RegisterCreatedObjectUndo(marker, "Build drone");
                marker.transform.SetParent(route.transform, worldPositionStays: false);
                marker.transform.position =
                    centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * _radius;
                rig.waypoints.Add(marker.transform);
            }
        }

        void BuildTrigger(Transform pad, Transform drone)
        {
            var trigger = StationBuild.FindOrCreateChild(pad, TriggerName);
            trigger.layer = NasaLayers.Interactable;
            Undo.RecordObject(trigger.transform, "Build drone");

            float drop = TriggerDrop(drone.position);
            Vector3 at = _triggerOnGround && drop > 3f
                ? drone.position + Vector3.down * (drop - 1f)      // head height above the floor
                : drone.position;

            trigger.transform.position = at;
            trigger.transform.rotation = Quaternion.identity;

            // Cancel the parent's scale so the radius below is in metres. This import carries 2.8, and a
            // radius that inherited it would put the prompt in reach from three houses away.
            Vector3 ls = pad.lossyScale;
            trigger.transform.localScale = new Vector3(
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.x)),
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.y)),
                1f / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));

            var sphere = StationBuild.GetOrAdd<SphereCollider>(trigger);
            Undo.RecordObject(sphere, "Build drone");
            sphere.isTrigger = true;
            sphere.center = Vector3.zero;
            sphere.radius = _reach;
        }

        static float TriggerDrop(Vector3 from)
        {
            return Physics.Raycast(from + Vector3.up * 0.5f, Vector3.down, out RaycastHit info, 400f,
                                   ~0, QueryTriggerInteraction.Ignore)
                ? from.y - info.point.y
                : 0f;
        }

        // ================================================================== teardown

        static void ParkIt(DroneFlight rig)
        {
            if (rig == null || rig.body == null) return;
            Undo.RecordObject(rig.body, "Park the drone");
            Undo.RecordObject(rig, "Park the drone");
            rig.SetImmediateDocked();
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
        }

        static void Remove(DroneFlight rig)
        {
            if (rig == null) return;
            ParkIt(rig);
            Undo.DestroyObjectImmediate(rig.gameObject);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Drone] Pad, route and trigger removed. The drone model is back where it was " +
                      "parked and is untouched otherwise.");
        }
    }
}
