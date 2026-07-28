using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds the helmet-off moment: a helmet prop on the astronaut's head that lifts away and gets
    /// tucked at the hip when you walk into pressurized air, and goes back on when you leave.
    ///
    /// The astronaut model is one skinned mesh with no separable helmet, so what comes off is a
    /// DUPLICATE: assign your own helmet FBX (project asset or scene object — either works), or leave the
    /// bucket empty and get a placeholder visor you can swap for a modelled one later with the usual
    /// Models &gt; Swap Placeholder With Selected FBX.
    ///
    /// The visor placeholder is deliberately TRANSPARENT and double-sided, because in first person the
    /// camera is inside it: an opaque single-sided bubble would be invisible from within and the whole
    /// point is to watch it lift off in front of you.
    ///
    /// The Scene view shows the worn position, the carried position and the arc between them, plus the
    /// zone box that triggers it — so you can see the whole move before pressing Play, and the Preview
    /// buttons will park the helmet at either end for real.
    /// </summary>
    public sealed class HelmetTool : EditorWindow
    {
        const string HelmetRootName = "Helmet";
        const string PlaceholderName = "PH_Helmet";
        const string HoldAnchorName = "HelmetHoldAnchor";
        const string ZoneName = "HelmetZone";
        const string ZoneRootName = "HelmetZones";
        const string TriggerName = "HelmetTrigger";
        const string VisorMatPath = "Assets/_Project/Materials/HelmetVisor.mat";
        const string ShellMatPath = "Assets/_Project/Materials/HelmetShell.mat";

        [SerializeField] GameObject _astronaut;
        [SerializeField] GameObject _helmetMesh;

        // A LIST, because the pressurized parts of this station are not in one place: the biodome is at
        // the origin and the tunnel is 100 m west of it. One box asked to hold both holds the whole map,
        // which is how the zone ended up 184 m across and swallowing the spawn point.
        [SerializeField] List<GameObject> _areas = new List<GameObject>();

        [SerializeField] float _helmetRadius = 0.17f;
        [SerializeField] float _forwardNudge = 0.02f;
        [SerializeField] float _upNudge = 0.04f;

        [SerializeField] float _hipSideways = 0.26f;
        [SerializeField] float _hipForward = 0.04f;
        [SerializeField] float _hipDrop = 0.06f;

        [SerializeField] float _takeOffSeconds = 1.6f;
        [SerializeField] float _stowSeconds = 1.1f;
        [SerializeField] float _putOnSeconds = 1.3f;
        [SerializeField] float _liftHeight = 0.45f;
        [SerializeField] bool _reachForIt = true;
        [SerializeField] bool _enableKey = true;

        [SerializeField] bool _showItOff = true;
        [SerializeField] float _showSeconds = 1.3f;
        [SerializeField] float _showDistance = 0.45f;
        [SerializeField] float _showHeight = -0.22f;
        [SerializeField] float _showSpin = 75f;

        [SerializeField] bool _waitForPressure = true;
        [SerializeField] float _pauseAfterGreen = 0.6f;

        [SerializeField] bool _onceOnly = true;
        [SerializeField] Vector3 _triggerSize = new Vector3(4f, 3f, 4f);

        [SerializeField] bool _buildZone = true;
        [SerializeField] float _zoneShrink = 0.9f;
        [SerializeField] float _zoneHeight = 14f;
        [SerializeField] float _boundaryMargin = 0.75f;
        [SerializeField] bool _matchOnStart;

        [MenuItem("Tools/NASA Sim/Station/Helmet Off Inside")]
        public static void Open()
        {
            var window = GetWindow<HelmetTool>(true, "Helmet", true);
            window.minSize = new Vector2(540f, 560f);
            window.AutoFind();
            window.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Puts a helmet on the astronaut that comes off once, where you put the box.\n\n" +
                "Assign your helmet mesh, or leave it empty for a see-through placeholder visor. Then " +
                "Build — the Scene view will show where it sits, where it ends up, and the arc it takes " +
                "between them.",
                MessageType.Info);

            EditorGUILayout.Space();
            DrawOnceOnly();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Parts", EditorStyles.boldLabel);
            _astronaut = Bucket("Astronaut", "The astronaut root. Found automatically.", _astronaut);
            _helmetMesh = Bucket("Helmet mesh",
                "Optional. Your helmet FBX or a scene copy of it — a DUPLICATE of the helmet portion, " +
                "since the astronaut model has no separable one. Empty = a placeholder visor.",
                _helmetMesh);
            DrawAreaList();

            if (GUILayout.Button("Find these in the scene")) AutoFind();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fit", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_helmetMesh != null))
                _helmetRadius = EditorGUILayout.Slider(
                    new GUIContent("Placeholder size (m)", "Radius of the stand-in visor bubble. Ignored " +
                                                           "once you assign a real helmet mesh."),
                    _helmetRadius, 0.08f, 0.4f);
            _forwardNudge = EditorGUILayout.Slider(
                new GUIContent("Forward nudge (m)", "Shift the helmet along the way the astronaut faces. " +
                                                    "The blue 'worn' sphere in the Scene view is where it " +
                                                    "ends up."),
                _forwardNudge, -0.25f, 0.25f);
            _upNudge = EditorGUILayout.Slider(
                new GUIContent("Up nudge (m)"), _upNudge, -0.25f, 0.35f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Where it's carried", EditorStyles.boldLabel);
            _hipSideways = EditorGUILayout.Slider(
                new GUIContent("Out from the hip (m)"), _hipSideways, 0f, 0.6f);
            _hipForward = EditorGUILayout.Slider(new GUIContent("Forward (m)"), _hipForward, -0.4f, 0.4f);
            _hipDrop = EditorGUILayout.Slider(new GUIContent("Down (m)"), _hipDrop, -0.3f, 0.5f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("The move", EditorStyles.boldLabel);
            _takeOffSeconds = EditorGUILayout.Slider(
                new GUIContent("Off the head (s)", "Up off the head and out in front of you."),
                _takeOffSeconds, 0.3f, 4f);
            _stowSeconds = EditorGUILayout.Slider(
                new GUIContent("Down to the hip (s)"), _stowSeconds, 0.3f, 4f);
            _putOnSeconds = EditorGUILayout.Slider(new GUIContent("Put on (s)"), _putOnSeconds, 0.3f, 4f);
            _liftHeight = EditorGUILayout.Slider(
                new GUIContent("Lift clear (m)", "How far it rises straight up before travelling — it has " +
                                                 "to clear the head first."),
                _liftHeight, 0f, 0.8f);
            _reachForIt = EditorGUILayout.Toggle(
                new GUIContent("Reach up for it", "Pose the right arm so the hand goes up and takes it, " +
                                                  "using the same IK the eat and pet flourishes use."),
                _reachForIt);
            _enableKey = EditorGUILayout.Toggle(
                new GUIContent("H key", "Take it off / put it on anywhere, for showing it off."), _enableKey);

            EditorGUILayout.Space();
            _showItOff = EditorGUILayout.BeginToggleGroup(
                new GUIContent("Hold it up where you can see it",
                               "Brings the helmet out in front of the body and holds it there, turning, " +
                               "before it goes to the hip. Off, it slides head-to-hip and is over before " +
                               "you have registered it."),
                _showItOff);
            EditorGUI.indentLevel++;
            _showSeconds = EditorGUILayout.Slider(new GUIContent("Hold for (s)"), _showSeconds, 0f, 5f);
            _showDistance = EditorGUILayout.Slider(
                new GUIContent("Out in front (m)", "In first person this is straight down the middle of " +
                                                   "the view."),
                _showDistance, 0.1f, 1.2f);
            _showHeight = EditorGUILayout.Slider(
                new GUIContent("Up / down from the head (m)"), _showHeight, -0.8f, 0.4f);
            _showSpin = EditorGUILayout.Slider(
                new GUIContent("Turn (deg/s)", "Rotates on the spot while held, so you see all of it."),
                _showSpin, -360f, 360f);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

            EditorGUILayout.Space();
            DrawPressureGate();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_onceOnly))
                DrawZoneSection();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_astronaut == null))
                if (GUILayout.Button("Build / update the helmet", GUILayout.Height(34f)))
                    Build();

            var rig = FindRig();
            using (new EditorGUI.DisabledScope(rig == null))
            {
                EditorGUILayout.LabelField("Preview (moves the real helmet — undoable)",
                                           EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Helmet on")) Preview(rig, off: false);
                    if (GUILayout.Button("Helmet off (carried)")) Preview(rig, off: true);
                }
                if (GUILayout.Button("Remove the helmet")) Remove(rig);
            }
        }

        /// <summary>
        /// The one-time trigger box: where it is, whether it exists at all, and — the thing that decides
        /// whether stepping in is instant or not — whether it happens to sit inside a pressure chamber.
        /// </summary>
        void DrawOnceOnly()
        {
            _onceOnly = EditorGUILayout.ToggleLeft(
                new GUIContent("Once, where I put the box",
                               "Step into the box and the helmet comes off. It never goes back on for the " +
                               "rest of the run — not when you leave, not when a chamber vents, not on H."),
                _onceOnly, EditorStyles.boldLabel);

            if (!_onceOnly)
            {
                EditorGUILayout.HelpBox(
                    "Off: the old behaviour. The helmet comes off inside a pressurized zone and goes back " +
                    "ON when you leave it. Reversible, so it is no good for a single continuous take.",
                    MessageType.Warning);
                return;
            }

            EditorGUI.indentLevel++;
            _triggerSize = EditorGUILayout.Vector3Field(
                new GUIContent("Box size (m)", "How big the volume is. Big enough that you cannot stride " +
                                               "through it between two frames."), _triggerSize);
            _triggerSize = new Vector3(Mathf.Max(0.5f, _triggerSize.x),
                                       Mathf.Max(0.5f, _triggerSize.y),
                                       Mathf.Max(0.5f, _triggerSize.z));
            EditorGUI.indentLevel--;

            GameObject box = GameObject.Find(TriggerName);
            HelmetRemoval rig = FindRig();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(box == null ? "Create the trigger box" : "Move the box to a default spot"))
                    PlaceTrigger(reposition: true);
                using (new EditorGUI.DisabledScope(box == null))
                    if (GUILayout.Button("Select it (then drag it where you want)", GUILayout.Width(240f)))
                    {
                        Selection.activeGameObject = box;
                        SceneView.FrameLastActiveSceneView();
                    }
            }

            if (box == null)
            {
                EditorGUILayout.HelpBox(
                    "There is no trigger box yet, so the helmet will never come off on its own — only " +
                    "with H. Press 'Create the trigger box', then drag it in the Scene view to wherever " +
                    "the take-off should happen. It shows up as a green volume.", MessageType.Warning);
                return;
            }

            // A box parented under the greenhouse (2.808x) or the astronaut would silently be that many
            // times the size the field above claims, because the depth test works in the box's own space.
            Vector3 ls = box.transform.lossyScale;
            if (Mathf.Abs(ls.x - 1f) > 0.01f || Mathf.Abs(ls.y - 1f) > 0.01f || Mathf.Abs(ls.z - 1f) > 0.01f)
                EditorGUILayout.HelpBox(
                    $"'{TriggerName}' has a lossy scale of {ls.x:0.00}, {ls.y:0.00}, {ls.z:0.00} — so the " +
                    "box is really that many times the size above. Move it out of whatever it is parented " +
                    "to, or set the parent's scale back to 1.", MessageType.Warning);

            // Where the astronaut stands relative to the box, in metres, before you press Play. The
            // equivalent readout for the old zones is what caught a 184 m zone swallowing the spawn point,
            // and the same two failures apply here: a box you start inside, and a box you can never reach
            // because its floor is above your head.
            if (_astronaut != null && rig != null && rig.HasTrigger)
            {
                Vector3 probe = _astronaut.transform.position + Vector3.up * 0.9f;
                float depth = rig.TriggerDepth(probe);
                if (depth >= 0f)
                    EditorGUILayout.HelpBox(
                        $"The astronaut SPAWNS inside the box — {depth:0.0} m in. The helmet will come off " +
                        "in the first second, before you have walked anywhere. Move the box somewhere you " +
                        "arrive at.", MessageType.Error);
                else
                    EditorGUILayout.HelpBox(
                        $"The astronaut starts {-depth:0.0} m outside the box. Walk into it and the helmet " +
                        "comes off.", MessageType.None);
            }

            PressureChamber inChamber = null;
            foreach (PressureChamber c in Object.FindObjectsByType<PressureChamber>(FindObjectsInactive.Include))
                if (c != null && c.Contains(box.transform.position, 0f)) { inChamber = c; break; }

            if (inChamber != null && _waitForPressure)
                EditorGUILayout.HelpBox(
                    $"The box is inside '{inChamber.name}'. Stepping in starts the pressurize cycle: red " +
                    $"for {inChamber.SecondsToGreen:0.0} s, green, then the helmet comes off " +
                    $"{_pauseAfterGreen:0.0} s later — {inChamber.SecondsToGreen + _pauseAfterGreen:0.0} s " +
                    "in total. Move the box out of the chamber if you want it to happen the moment you " +
                    "walk in.", MessageType.None);
            else
                EditorGUILayout.HelpBox(
                    "The box is not inside any pressure chamber, so the moment you step in the helmet " +
                    "comes off — no waiting.", MessageType.None);

            if (rig != null && rig.helmet != null && rig.IsOff)
                EditorGUILayout.HelpBox(
                    "The helmet in the scene is currently parked at the hip by the Preview button. Press " +
                    "'Helmet on' below before you record, or you will start the take with it already off.",
                    MessageType.Warning);
        }

        void DrawZoneSection()
        {
            _buildZone = EditorGUILayout.BeginToggleGroup("Come off automatically inside", _buildZone);
            EditorGUI.indentLevel++;
            _zoneShrink = EditorGUILayout.Slider(
                new GUIContent("Zone size", "The trigger box as a fraction of the pressurized area's " +
                                            "footprint. Under 1 keeps its corners inside a round dome."),
                _zoneShrink, 0.4f, 1.2f);
            _zoneHeight = EditorGUILayout.Slider(
                new GUIContent("Zone height (m)"), _zoneHeight, 3f, 60f);
            _boundaryMargin = EditorGUILayout.Slider(
                new GUIContent("Boundary margin (m)",
                               "How far past a wall you must walk before the crossing counts. This is " +
                               "what stops the helmet coming off and going back on repeatedly while you " +
                               "walk along a boundary — without it, every dip in the ground under a " +
                               "zone's floor is another crossing."),
                _boundaryMargin, 0f, 3f);
            _matchOnStart = EditorGUILayout.Toggle(
                new GUIContent("Off already at spawn", "Start the game with the helmet already off if the " +
                                                       "astronaut spawns inside the zone. Normally left " +
                                                       "off — you want to watch it come off, not find it " +
                                                       "gone."),
                _matchOnStart);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

            DrawSpawnCheck();
        }

        /// <summary>
        /// Make the box if it isn't there, and put it somewhere defensible the first time. The default is
        /// the pressure chamber when there is one — that is where taking the helmet off actually makes
        /// sense — and a couple of metres in front of the astronaut otherwise. It is a starting point, not
        /// a decision: the whole idea is that you drag it.
        /// </summary>
        void PlaceTrigger(bool reposition, bool focus = true)
        {
            GameObject box = GameObject.Find(TriggerName);
            bool fresh = box == null;
            if (fresh)
            {
                box = new GameObject(TriggerName);
                Undo.RegisterCreatedObjectUndo(box, "Create Helmet Trigger");
            }

            if (fresh || reposition)
            {
                Undo.RecordObject(box.transform, "Place Helmet Trigger");
                // Scene root and identity scale, deliberately: the depth test works in the box's own
                // space, so a scaled parent would make "4 m" mean something else entirely.
                box.transform.SetParent(null, worldPositionStays: false);
                box.transform.localScale = Vector3.one;

                var chamber = Object.FindAnyObjectByType<PressureChamber>();
                if (chamber != null)
                {
                    box.transform.SetPositionAndRotation(
                        chamber.transform.TransformPoint(chamber.center), chamber.transform.rotation);
                }
                else if (_astronaut != null)
                {
                    box.transform.SetPositionAndRotation(
                        _astronaut.transform.position + _astronaut.transform.forward * 3f
                                                      + Vector3.up * (_triggerSize.y * 0.5f),
                        Quaternion.identity);
                }
            }

            HelmetRemoval rig = FindRig();
            if (rig != null)
            {
                Undo.RecordObject(rig, "Place Helmet Trigger");
                if (rig.trigger == null) rig.trigger = new HelmetRemoval.Zone();
                rig.trigger.label = "take the helmet off here";
                rig.trigger.center = box.transform;
                rig.trigger.size = _triggerSize;
                rig.onceOnly = _onceOnly;
                EditorUtility.SetDirty(rig);
            }

            EditorSceneManager.MarkSceneDirty(box.scene);
            if (!focus) return;
            Selection.activeGameObject = box;
            SceneView.FrameLastActiveSceneView();
        }

        GameObject Bucket(string label, string tooltip, GameObject value) =>
            (GameObject)EditorGUILayout.ObjectField(new GUIContent(label, tooltip), value,
                                                    typeof(GameObject), true);

        /// <summary>
        /// One row per pressurized building. Each gets its own box, and the helmet is off inside any of
        /// them — which is the only way to cover a biodome and a tunnel 100 m apart without also covering
        /// everything in between.
        /// </summary>
        void DrawAreaList()
        {
            EditorGUILayout.LabelField(
                new GUIContent("Pressurized areas",
                               "One per building with air in it. The trigger box is measured from each " +
                               "one's meshes."),
                EditorStyles.miniBoldLabel);

            if (_areas == null) _areas = new List<GameObject>();

            int remove = -1;
            for (int i = 0; i < _areas.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _areas[i] = (GameObject)EditorGUILayout.ObjectField(
                        GUIContent.none, _areas[i], typeof(GameObject), true);

                    if (_areas[i] != null && StationBuild.TryRendererBounds(_areas[i], out Bounds b))
                    {
                        bool huge = b.size.x > 120f || b.size.z > 120f;
                        var style = new GUIStyle(EditorStyles.miniLabel)
                        {
                            normal = { textColor = huge ? new Color(0.95f, 0.55f, 0.2f)
                                                        : EditorStyles.miniLabel.normal.textColor }
                        };
                        GUILayout.Label($"{b.size.x:0} × {b.size.z:0} m", style, GUILayout.Width(80f));
                    }
                    else
                    {
                        GUILayout.Label(string.Empty, GUILayout.Width(80f));
                    }

                    if (GUILayout.Button("−", GUILayout.Width(24f))) remove = i;
                }
            }
            if (remove >= 0) _areas.RemoveAt(remove);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add a row")) _areas.Add(null);
                if (GUILayout.Button("Find the biodome and the tunnel")) FindAreas(replace: true);
            }

            foreach (GameObject a in _areas)
            {
                if (a == null || !StationBuild.TryRendererBounds(a, out Bounds b)) continue;
                if (b.size.x <= 120f && b.size.z <= 120f) continue;
                EditorGUILayout.HelpBox(
                    $"'{a.name}' measures {b.size.x:0} × {b.size.z:0} m — that is most of the map, not " +
                    "one building. A zone this size covers the spawn point and everywhere you walk, so " +
                    "the helmet comes off and goes on more or less at random. Point this row at the " +
                    "building's own shell instead.", MessageType.Warning);
            }
        }

        /// <summary>
        /// The gas-chamber gate, and whether there is actually a chamber for it to wait on. Ticking a
        /// box that has nothing behind it is worse than not offering it, so the window says out loud
        /// which of the two cases you are in.
        /// </summary>
        void DrawPressureGate()
        {
            _waitForPressure = EditorGUILayout.Toggle(
                new GUIContent("Wait for the gas chamber",
                               "While you are standing in a pressure chamber, that chamber decides: the " +
                               "helmet stays sealed until the lamp goes green. Everywhere else the zones " +
                               "still decide."),
                _waitForPressure);

            if (!_waitForPressure) return;

            var chambers = Object.FindObjectsByType<PressureChamber>(FindObjectsInactive.Include);

            if (chambers.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "There is no pressure chamber in the scene yet, so there is nothing to wait for and " +
                    "the zones will behave exactly as they do now.\n\n" +
                    "Build one first: Tools > NASA Sim > Station > Pressure Chamber Gas. Then come back " +
                    "here and build again.", MessageType.Warning);
                return;
            }

            var sb = new System.Text.StringBuilder();
            foreach (PressureChamber c in chambers)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append($"  {c.name} — red for {c.SecondsToGreen:0.0} s, then green");
            }
            EditorGUILayout.HelpBox(
                $"Waiting on {chambers.Length} chamber(s):\n{sb}\n\n" +
                $"The hands go up {_pauseAfterGreen:0.0} s after the lamp turns.\n\n" +
                "The chamber needs to sit INSIDE one of the pressurized areas above. It only decides " +
                "while you are standing in it — step out the far side and the zone takes over again, so " +
                "a chamber outside every zone would re-seal the helmet the moment you left it.",
                MessageType.None);
            _pauseAfterGreen = EditorGUILayout.Slider(
                new GUIContent("Beat after green (s)", "A pause between the lamp turning and the helmet " +
                                                       "moving, so the two read as cause and effect."),
                _pauseAfterGreen, 0f, 3f);
        }

        /// <summary>
        /// The one thing that silently ruins this feature: a zone that already contains the spawn point.
        /// The helmet is then either off before you can look at it, or waiting for a boundary you are
        /// standing on the wrong side of. So the window says which side of the line you start on, in
        /// metres, rather than leaving you to press Play and guess.
        /// </summary>
        void DrawSpawnCheck()
        {
            // Irrelevant when one box decides: there is no boundary to spawn on the wrong side of, and
            // the helmet always starts worn.
            if (_onceOnly) return;

            var rig = FindRig();
            if (rig == null || !rig.HasAnyZone) return;

            Vector3 spawn = (_astronaut != null ? _astronaut.transform.position
                                                : rig.transform.position) + Vector3.up * 0.9f;
            float depth = rig.ZoneDepth(spawn);

            if (rig.pressurizedZone != null)
                EditorGUILayout.HelpBox(
                    "This helmet still has the old single-box zone attached as well as the list. Build " +
                    "again and it will be folded into the list and cleared.", MessageType.Warning);

            int count = rig.zones != null ? rig.zones.Count : 0;
            string where = count == 1 ? "the zone" : $"any of the {count} zones";

            if (depth >= 0f)
            {
                HelmetRemoval.Zone z = rig.ZoneAt(spawn);
                EditorGUILayout.HelpBox(
                    $"The astronaut spawns INSIDE {(z != null ? $"'{z.label}'" : "the zone")} — " +
                    $"{depth:0.0} m in from its nearest wall.\n\n" +
                    "You will start already sealed in, so nothing happens until you walk out and back in. " +
                    "Press H any time to see the move regardless.",
                    MessageType.Warning);

                // The specific, silent version of that: the helmet is not merely off-limits at spawn, it
                // is GONE at spawn. You press Play, look down, and there was never a helmet — which reads
                // as the feature not working rather than as a setting.
                if (rig.matchZoneOnStart && !rig.GateActive)
                {
                    EditorGUILayout.HelpBox(
                        "'Off already at spawn' is ticked on the helmet in the scene, so it will not just " +
                        "be un-triggerable — it will already be at the hip on frame one and you will " +
                        "never see it come off at all.", MessageType.Error);
                    if (GUILayout.Button("Fix it: start with the helmet ON"))
                    {
                        Undo.RecordObject(rig, "Start with the helmet on");
                        rig.matchZoneOnStart = false;
                        _matchOnStart = false;
                        EditorUtility.SetDirty(rig);
                        EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
                    }
                }
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"The astronaut spawns outside {where}, {-depth:0.0} m from the nearest. Walk in and " +
                    "the helmet comes off.",
                    MessageType.None);
            }
        }

        // ================================================================== find

        void AutoFind()
        {
            if (_astronaut == null)
            {
                var a = Object.FindAnyObjectByType<AstronautController>();
                if (a != null) _astronaut = a.gameObject;
            }

            if (_areas == null) _areas = new List<GameObject>();
            if (_areas.Count == 0) FindAreas(replace: false);
        }

        /// <summary>
        /// Every building that plausibly holds air: the biodome and the connecting tunnel. Each is picked
        /// as the LARGEST match under 120 m across — the size ceiling is the whole trick, because without
        /// it "dome" also matches the imported environment root that contains the dome, and the zone
        /// silently becomes the map.
        /// </summary>
        void FindAreas(bool replace)
        {
            if (_areas == null) _areas = new List<GameObject>();
            if (replace) _areas.Clear();

            AddBest("biodome", "dome", "biosphere", "greenhouse");
            AddBest("tunnel", "tunnel", "airlock", "corridor");

            if (_areas.Count == 0)
                Debug.LogWarning("[Helmet] Nothing recognisable as a pressurized building was found. Drop " +
                                 "the biodome shell and the tunnel into the rows by hand.");
        }

        void AddBest(string what, params string[] words)
        {
            Transform best = null;
            float bestSize = 0f;

            foreach (var t in StationBuild.FindAllContaining(words))
            {
                if (!StationBuild.TryRendererBounds(t.gameObject, out Bounds b)) continue;
                if (b.size.x > 120f || b.size.z > 120f) continue;    // that is the map, not a building
                float size = b.size.x * b.size.z;
                if (size > bestSize) { bestSize = size; best = t; }
            }

            if (best == null) return;
            if (_areas.Contains(best.gameObject)) return;
            _areas.Add(best.gameObject);
            Debug.Log($"[Helmet] Using '{best.name}' as the {what}.", best);
        }

        HelmetRemoval FindRig() =>
            _astronaut != null ? _astronaut.GetComponent<HelmetRemoval>()
                               : Object.FindAnyObjectByType<HelmetRemoval>();

        // ================================================================== build

        void Build()
        {
            if (!StationBuild.RequireSceneObject(_astronaut, "astronaut")) return;

            var controller = _astronaut.GetComponent<AstronautController>();
            if (controller == null)
            {
                EditorUtility.DisplayDialog("That isn't the astronaut",
                    $"'{_astronaut.name}' has no AstronautController. Assign the astronaut root — the " +
                    "object with the CharacterController on it.", "OK");
                return;
            }

            var locomotion = _astronaut.GetComponent<AstronautLocomotionVisual>();
            Transform head = ResolveBone(locomotion, HumanBodyBones.Head,
                                         "bip001head", "head", "mixamorighead");
            Transform hips = ResolveBone(locomotion, HumanBodyBones.Hips,
                                         "bip001pelvis", "pelvis", "hips", "bip001");
            if (head == null)
            {
                EditorUtility.DisplayDialog("No head to put it on",
                    "Couldn't find a head bone on the astronaut model, and there is no 'Head' child to " +
                    "fall back to. Assign the head manually on the Helmet Removal component after " +
                    "building.", "OK");
            }
            if (hips == null) hips = _astronaut.transform;

            var rig = StationBuild.GetOrAdd<HelmetRemoval>(_astronaut);
            Undo.RecordObject(rig, "Build Helmet");

            // Put the helmet back on before re-reading anything, so a build done while the Preview holds
            // it at the hip can't adopt the hip as the worn pose.
            if (rig.helmet != null)
            {
                Undo.RecordObject(rig.helmet, "Build Helmet");
                rig.SetImmediate(off: false);
            }

            // ---- the prop ----
            Transform helmet = BuildProp();
            rig.helmet = helmet;
            rig.headAnchor = head;

            // Worn: centred on the first-person EYE point rather than on the head bone, because the whole
            // point is watching it lift away in front of you — and upright with the body rather than with
            // the head bone, whose axes on a Biped are nobody's idea of "up".
            Transform eye = _astronaut.transform.Find("Head");
            Vector3 basePos = eye != null ? eye.position : (head != null ? head.position : _astronaut.transform.position);
            float eyeToBone = eye != null && head != null ? Vector3.Distance(eye.position, head.position) : 0f;
            Undo.RecordObject(helmet, "Build Helmet");
            helmet.SetPositionAndRotation(
                basePos + _astronaut.transform.forward * _forwardNudge + Vector3.up * _upNudge,
                _astronaut.transform.rotation);
            rig.CaptureWornPose();

            // ---- where it rides once it's off ----
            Transform hold = BuildHoldAnchor(hips);
            rig.holdAnchor = hold;
            rig.CaptureHeldPose(Vector3.zero, Quaternion.identity);

            // ---- what sets it off ----
            rig.onceOnly = _onceOnly;
            if (_onceOnly)
            {
                // Never repositioned on a rebuild: the placement is the one thing here that is entirely
                // the user's, and nothing about building the helmet is a reason to move it back.
                PlaceTrigger(reposition: false, focus: false);
            }

            if (_buildZone)
            {
                BuildZones(rig);
            }
            else
            {
                rig.zones.Clear();
                rig.pressurizedZone = null;
            }

            // ---- settings ----
            rig.takeOffSeconds = _takeOffSeconds;
            rig.stowSeconds = _stowSeconds;
            rig.putOnSeconds = _putOnSeconds;
            rig.liftHeight = _liftHeight;
            rig.reachForIt = _reachForIt;
            rig.enableKey = _enableKey;
            // Forced off in one-time mode rather than merely ignored, so the Inspector cannot show a
            // setting that does nothing and send someone hunting for why the helmet starts at the hip.
            rig.matchZoneOnStart = !_onceOnly && _matchOnStart;
            rig.locomotion = locomotion;
            rig.hand = _astronaut.GetComponent<HandActionController>();

            rig.showItOff = _showItOff;
            rig.showSeconds = _showSeconds;
            rig.showDistance = _showDistance;
            rig.showHeight = _showHeight;
            rig.showSpinDegPerSec = _showSpin;

            rig.waitForPressure = _waitForPressure;
            rig.pauseAfterPressurized = _pauseAfterGreen;
            // Wired here rather than left to the component's own Awake search, so what it waits on is
            // visible in the Inspector instead of being decided invisibly at startup.
            rig.chambers = new List<PressureChamber>(
                Object.FindObjectsByType<PressureChamber>(FindObjectsInactive.Include));
            if (StationBuild.TryRendererBounds(helmet.gameObject, out Bounds hb))
                rig.SetGripRadius(Mathf.Max(hb.extents.x, hb.extents.y));

            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(_astronaut.scene);
            Selection.activeGameObject = helmet.gameObject;

            Debug.Log($"[Helmet] Built on '{_astronaut.name}'.\n" +
                      $"  Worn on: {(head != null ? head.name : "(nothing — assign a head bone)")}   ·   " +
                      $"carried at: {hold.name}\n" +
                      $"  {DescribeTrigger(rig)}" +
                      (_enableKey ? ", or press H anywhere." : ".") + "\n" +
                      (rig.GateActive
                          ? $"  Gated on {rig.chambers.Count} pressure chamber(s): while you are inside " +
                            "one, the helmet stays sealed until its lamp goes green, then comes off " +
                            $"{_pauseAfterGreen:0.0} s later. Outside them the zones decide as before.\n"
                          : _waitForPressure
                              ? "  'Wait for the gas chamber' is on but there is no chamber in the scene, " +
                                "so nothing is gated. Build one with Station > Pressure Chamber Gas and " +
                                "run this again.\n"
                              : string.Empty) +
                      (_showItOff
                          ? $"  It is held out {_showDistance:0.00} m in front of you for " +
                            $"{_showSeconds:0.0} s, turning, before it goes to the hip — total " +
                            $"{_takeOffSeconds + _showSeconds + _stowSeconds:0.0} s of move.\n"
                          : string.Empty) +
                      (rig.HasAnyZone
                          ? $"  Crossings need {rig.boundaryMargin:0.00} m of travel past a wall to count, " +
                            "so walking along a boundary no longer flickers it on and off.\n" +
                            "  Spawn point is " +
                            (rig.ZoneContains(_astronaut.transform.position + Vector3.up * 0.9f)
                                ? "INSIDE a zone, so you start sealed in and see nothing until you walk " +
                                  "out and back — shrink that zone or move the spawn.\n"
                                : "outside them all, so walking in triggers the take-off.\n")
                          : string.Empty) +
                      "  Scene view: blue sphere = worn, orange sphere = carried, the yellow arc is the " +
                      "path it takes. Use the Preview buttons to park it at either end and look." +
                      (eyeToBone > 0.15f
                          ? $"\n  NOTE: it is centred on the camera's eye point, which sits {eyeToBone:0.00} m " +
                            "from the model's head bone — so it frames correctly in first person but may " +
                            "look off the head in the fly-cam. Nudge it with Up / Forward, or just drag " +
                            "the Helmet child."
                          : string.Empty) +
                      (_helmetMesh == null
                          ? "\n  The visor is a PH_ placeholder — swap it for a modelled helmet with " +
                            "Tools > NASA Sim > Models > Swap Placeholder With Selected FBX."
                          : string.Empty), helmet);
        }

        /// <summary>
        /// The helmet root, with your mesh under it or a placeholder visor. The root carries no renderer
        /// on purpose — that is the project's placeholder invariant: gameplay wiring points at the ROOT,
        /// the visual is a swappable child.
        /// </summary>
        Transform BuildProp()
        {
            var root = StationBuild.FindOrCreateChild(_astronaut.transform, HelmetRootName);

            if (_helmetMesh != null)
            {
                // Replace whatever visual is there (placeholder or a previously assigned mesh).
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                {
                    Transform child = root.transform.GetChild(i);
                    if (child.gameObject != _helmetMesh) Undo.DestroyObjectImmediate(child.gameObject);
                }

                GameObject model = _helmetMesh;
                if (!_helmetMesh.scene.IsValid())
                {
                    // A Project-window asset: bring a copy in rather than editing the asset.
                    model = (GameObject)PrefabUtility.InstantiatePrefab(_helmetMesh);
                    if (model == null) model = Object.Instantiate(_helmetMesh);
                    model.name = _helmetMesh.name;
                    Undo.RegisterCreatedObjectUndo(model, "Build Helmet");
                }

                if (model.transform.parent != root.transform)
                    Undo.SetTransformParent(model.transform, root.transform, "Build Helmet");
                Undo.RecordObject(model.transform, "Build Helmet");
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                StripColliders(model);
                return root.transform;
            }

            BuildPlaceholder(root.transform);
            return root.transform;
        }

        void BuildPlaceholder(Transform root)
        {
            Transform existing = root.Find(PlaceholderName);
            GameObject visor;
            if (existing != null)
            {
                visor = existing.gameObject;
                Undo.RecordObject(visor.transform, "Build Helmet");
            }
            else
            {
                visor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                visor.name = PlaceholderName;
                Undo.RegisterCreatedObjectUndo(visor, "Build Helmet");
                Undo.SetTransformParent(visor.transform, root, "Build Helmet");
            }

            visor.transform.localPosition = Vector3.zero;
            visor.transform.localRotation = Quaternion.identity;
            visor.transform.localScale = Vector3.one * (_helmetRadius * 2f);
            StripColliders(visor);
            SceneBootstrap.SetMaterial(visor, VisorMaterial());

            var marker = StationBuild.GetOrAdd<PlaceholderMarker>(visor);
            marker.category = PlaceholderMarker.Category.Helmet;
            marker.note = "Stand-in helmet. Select this, select your helmet FBX, then run " +
                          "Tools > NASA Sim > Models > Swap Placeholder With Selected FBX.";

            // A neck ring, so the bubble reads as a helmet rather than a soap bubble. Scaled in the
            // visor's LOCAL space, which is already the helmet's diameter — hence the fractions.
            Transform ringT = visor.transform.Find("Ring");
            GameObject ring;
            if (ringT != null)
            {
                ring = ringT.gameObject;
                Undo.RecordObject(ring.transform, "Build Helmet");
            }
            else
            {
                ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "Ring";
                Undo.RegisterCreatedObjectUndo(ring, "Build Helmet");
                Undo.SetTransformParent(ring.transform, visor.transform, "Build Helmet");
            }
            ring.transform.localPosition = new Vector3(0f, -0.36f, 0f);
            ring.transform.localRotation = Quaternion.identity;
            ring.transform.localScale = new Vector3(0.82f, 0.06f, 0.82f);
            StripColliders(ring);
            SceneBootstrap.SetMaterial(ring, SceneBootstrap.MakeMat(
                ShellMatPath, "Universal Render Pipeline/Lit", new Color(0.90f, 0.90f, 0.92f)));
        }

        Transform BuildHoldAnchor(Transform hips)
        {
            var anchor = StationBuild.FindOrCreateChild(hips, HoldAnchorName);
            Undo.RecordObject(anchor.transform, "Build Helmet");

            Transform body = _astronaut.transform;
            anchor.transform.SetPositionAndRotation(
                hips.position
                + body.right * _hipSideways
                + body.forward * _hipForward
                + Vector3.down * _hipDrop,
                // Tipped, the way a helmet sits when it's tucked against you rather than worn.
                body.rotation * Quaternion.Euler(28f, 0f, 32f));

            // Cancel any accumulated bone scale so the helmet doesn't resize when it arrives.
            Vector3 ls = hips.lossyScale;
            anchor.transform.localScale = new Vector3(1f / NonZero(ls.x), 1f / NonZero(ls.y), 1f / NonZero(ls.z));
            return anchor.transform;
        }

        void BuildZones(HelmetRemoval rig)
        {
            rig.zones.Clear();

            // The old single-box fields are cleared here rather than left dangling. They are still read at
            // runtime for scenes that were built before the list existed, so leaving one behind would mean
            // an invisible extra zone quietly overlapping the new ones.
            rig.pressurizedZone = null;

            var root = StationBuild.GeneratedRoot(ZoneRootName, clearChildren: true);
            Undo.RecordObject(root.transform, "Build Helmet");
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            int built = 0;
            foreach (GameObject area in _areas)
            {
                if (area == null || !StationBuild.TryRendererBounds(area, out Bounds b)) continue;

                var zone = StationBuild.FindOrCreateChild(
                    root.transform, $"{ZoneName}_{StationBuild.Sanitize(area.name)}");
                Undo.RecordObject(zone.transform, "Build Helmet");

                // Take the area's real height and only then clamp — a Min(slider, height) would reduce a
                // dome measured as a wide flat shell to a 3 m slab, which you can walk clean over.
                float height = Mathf.Clamp(b.size.y, 3f, _zoneHeight);
                zone.transform.SetPositionAndRotation(
                    new Vector3(b.center.x, b.min.y + height * 0.5f, b.center.z), Quaternion.identity);

                rig.zones.Add(new HelmetRemoval.Zone(
                    area.name, zone.transform,
                    new Vector3(b.size.x * _zoneShrink, height, b.size.z * _zoneShrink)));
                built++;
            }

            rig.boundaryMargin = _boundaryMargin;

            // A zone that is 120 m across means the row caught the whole import rather than the
            // pressurized part. Say so now: the symptom later is a helmet that is simply never on.
            foreach (HelmetRemoval.Zone z in rig.zones)
            {
                if (z.size.x <= 120f && z.size.z <= 120f) continue;
                Debug.LogWarning(
                    $"[Helmet] Zone '{z.label}' came out {z.size.x:0} × {z.size.z:0} m — that is most of " +
                    "the map, not one building. It will cover the spawn point and very likely everywhere " +
                    "you walk. Point that row at the building's own shell and build again.");
            }

            if (built == 0)
                Debug.LogWarning("[Helmet] No pressurized areas assigned (or none of them have meshes), " +
                                 "so the helmet will only come off with the H key. Press 'Find the " +
                                 "biodome and the tunnel', or fill the rows by hand, and build again.");
        }

        /// <summary>What will actually set it off, for the build log.</summary>
        static string DescribeTrigger(HelmetRemoval rig)
        {
            if (!rig.onceOnly) return DescribeZones(rig);
            if (!rig.HasTrigger)
                return "NO TRIGGER BOX — the helmet will only come off with H. Press 'Create the trigger " +
                       "box' and drag it where you want the take-off";
            Vector3 p = rig.trigger.center.position;
            return $"Comes off ONCE, on stepping into '{rig.trigger.center.name}' at " +
                   $"({p.x:0.0}, {p.y:0.0}, {p.z:0.0}), {rig.trigger.size.x:0.0} × " +
                   $"{rig.trigger.size.y:0.0} × {rig.trigger.size.z:0.0} m — and never goes back on";
        }

        /// <summary>A one-line summary of the zones for the build log.</summary>
        static string DescribeZones(HelmetRemoval rig)
        {
            if (rig.zones == null || rig.zones.Count == 0) return "No zones — H key only";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < rig.zones.Count; i++)
            {
                HelmetRemoval.Zone z = rig.zones[i];
                if (i > 0) sb.Append("; ");
                sb.Append($"{z.label} {z.size.x:0} × {z.size.y:0} × {z.size.z:0} m");
            }
            return $"Comes off inside {rig.zones.Count} zone(s): {sb}";
        }

        // ================================================================== preview / teardown

        void Preview(HelmetRemoval rig, bool off)
        {
            if (rig == null || rig.helmet == null) return;
            Undo.RecordObject(rig.helmet, off ? "Preview helmet off" : "Preview helmet on");
            Undo.RecordObject(rig, "Preview helmet");
            rig.SetImmediate(off);
            EditorSceneManager.MarkSceneDirty(rig.gameObject.scene);
        }

        void Remove(HelmetRemoval rig)
        {
            if (rig == null) return;
            if (rig.helmet != null)
            {
                rig.SetImmediate(off: false);
                Undo.DestroyObjectImmediate(rig.helmet.gameObject);
            }
            if (rig.holdAnchor != null) Undo.DestroyObjectImmediate(rig.holdAnchor.gameObject);

            var zoneRoot = GameObject.Find(ZoneRootName);
            if (zoneRoot != null) Undo.DestroyObjectImmediate(zoneRoot);
            if (rig.pressurizedZone != null && rig.pressurizedZone.name == ZoneName)
                Undo.DestroyObjectImmediate(rig.pressurizedZone.gameObject);

            Undo.DestroyObjectImmediate(rig);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Helmet] Helmet, hold anchor and zone removed.");
        }

        // ================================================================== helpers

        /// <summary>
        /// Same escalating search the locomotion visual uses: a Humanoid avatar first (bone names are
        /// irrelevant, Unity's retargeting maps them), then a name search covering the Biped and Mixamo
        /// spellings, then the astronaut's own "Head" anchor.
        /// </summary>
        Transform ResolveBone(AstronautLocomotionVisual locomotion, HumanBodyBones bone,
                              params string[] names)
        {
            var animator = _astronaut.GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid)
            {
                Transform t = animator.GetBoneTransform(bone);
                if (t != null) return t;
            }

            foreach (var t in _astronaut.GetComponentsInChildren<Transform>(true))
            {
                string n = Normalize(t.name);
                foreach (string want in names)
                    if (n == want) return t;
            }

            return bone == HumanBodyBones.Head ? _astronaut.transform.Find("Head") : null;
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int colon = s.LastIndexOf(':');
            if (colon >= 0 && colon < s.Length - 1) s = s.Substring(colon + 1);
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        /// <summary>A helmet must not collide with anything — it rides the head and blocks nothing.</summary>
        static void StripColliders(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                Undo.DestroyObjectImmediate(c);
        }

        /// <summary>
        /// Transparent AND double-sided, for the reason in the class summary: in first person the camera
        /// sits inside the visor, and a single-sided opaque bubble would be culled away to nothing there.
        /// </summary>
        static Material VisorMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(VisorMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "HelmetVisor" };
                AssetDatabase.CreateAsset(mat, VisorMatPath);
            }

            mat.SetFloat("_Surface", 1f);                              // Transparent
            mat.SetFloat("_Blend", 0f);                                // Alpha
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", (float)CullMode.Off);                // Render Face Both
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_Metallic", 0.1f);
            mat.SetFloat("_Smoothness", 0.95f);
            mat.SetColor("_BaseColor", new Color(0.58f, 0.74f, 0.88f, 0.22f));
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.doubleSidedGI = true;
            // Above the dome glass, so the visor still reads when you're looking out through the shell.
            if (mat.HasProperty("_QueueOffset"))
                mat.SetFloat("_QueueOffset", BiodomeFixTools.GlassQueue + 50 - 3000f);
            mat.renderQueue = BiodomeFixTools.GlassQueue + 50;
            mat.SetShaderPassEnabled("ShadowCaster", false);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
            return mat;
        }

        static float NonZero(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;
    }
}
