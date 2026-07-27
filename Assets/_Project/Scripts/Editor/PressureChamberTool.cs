using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Drops a self-contained pressure cycle into one chamber of the tunnel: walk in, a hatch seals, gas
    /// rises to fill the room, the lamp goes from red to green. Walk out and it vents.
    ///
    /// The tunnel arrives from Maya as four modules strung along its axis —
    ///
    ///     OutsideDoor · CrewLock_GRP · MidHatch · EquipLock_GRP · InnerHatch · HabPlate
    ///
    /// so counting inward from the outside door, CrewLock is the first chamber and EquipLock the second.
    /// This defaults to EquipLock and lists whatever else it finds, because "second" depends on which end
    /// you walked in from and the window should not pretend otherwise.
    ///
    /// The measured bounds are the module's OUTER hull, shell included, so the gas volume is shrunk to
    /// the interior fit before it is used. Left at full size the gas emits inside the walls, and the
    /// visible result is a room that stays empty while fog leaks out of the outside of the tunnel.
    /// </summary>
    public sealed class PressureChamberTool : EditorWindow
    {
        const string RootName = "PressureChambers";

        [SerializeField] GameObject _chamber;
        [SerializeField] float _interiorFit = 0.68f;
        [SerializeField] float _heightFit = 0.80f;
        [SerializeField] float _fillSeconds = 5f;
        [SerializeField] float _ventSeconds = 3.5f;
        [SerializeField] float _sealDelay = 0.6f;
        [SerializeField] bool _addLight = true;
        [SerializeField] int _jetCount = 4;

        readonly List<Transform> _candidates = new List<Transform>();
        int _pick;

        [MenuItem("Tools/NASA Sim/Station/Pressure Chamber Gas")]
        public static void Open()
        {
            var w = GetWindow<PressureChamberTool>(true, "Pressure Chamber", true);
            w.minSize = new Vector2(500f, 520f);
            w.Rescan();
            w.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Gas fills the chamber on its own while you stand in it, and vents when you leave. No " +
                "button and no door sequencing — this is scenery with a state, not an airlock.\n\n" +
                "Presence is tested every frame rather than with trigger events, so standing perfectly " +
                "still in the middle of the room still counts.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Which chamber", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (_candidates.Count > 0)
                {
                    var names = new string[_candidates.Count];
                    for (int i = 0; i < _candidates.Count; i++)
                    {
                        Transform t = _candidates[i];
                        string leaf = Leaf(t.name);
                        names[i] = StationBuild.TryRendererBounds(t.gameObject, out Bounds cb)
                            ? $"{leaf}  ({cb.size.x:0.0} × {cb.size.y:0.0} × {cb.size.z:0.0} m)"
                            : leaf;
                    }
                    int next = EditorGUILayout.Popup("Found in the tunnel", _pick, names);
                    if (next != _pick) { _pick = next; _chamber = _candidates[_pick].gameObject; }
                }
                else
                {
                    EditorGUILayout.LabelField("Found in the tunnel", "nothing named *Lock_GRP");
                }

                if (GUILayout.Button("Rescan", GUILayout.Width(64f))) Rescan();
            }

            _chamber = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Chamber", "The module the gas fills. Its meshes are measured to size the " +
                                          "volume — you can drop any group in here."),
                _chamber, typeof(GameObject), true);

            DrawMeasurement();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fit", EditorStyles.boldLabel);
            _interiorFit = EditorGUILayout.Slider(
                new GUIContent("Interior width", "The gas volume as a fraction of the module's outer " +
                                                 "hull. The hull includes the shell, so this has to be " +
                                                 "well under 1 or the gas emits inside the walls."),
                _interiorFit, 0.3f, 1f);
            _heightFit = EditorGUILayout.Slider(
                new GUIContent("Interior height"), _heightFit, 0.3f, 1f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Timing (real seconds)", EditorStyles.boldLabel);
            _sealDelay = EditorGUILayout.Slider(new GUIContent("Seal delay"), _sealDelay, 0f, 3f);
            _fillSeconds = EditorGUILayout.Slider(new GUIContent("Fill"), _fillSeconds, 1f, 20f);
            _ventSeconds = EditorGUILayout.Slider(new GUIContent("Vent"), _ventSeconds, 1f, 20f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Fittings", EditorStyles.boldLabel);
            _jetCount = EditorGUILayout.IntSlider(
                new GUIContent("Corner vents", "Jets that puff only while the pressure is changing."),
                _jetCount, 0, 8);
            _addLight = EditorGUILayout.Toggle(
                new GUIContent("Status lamp", "A point light that runs red → green with the pressure and " +
                                              "pulses while gas is moving."),
                _addLight);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_chamber == null))
                if (GUILayout.Button("Build / update the gas fill", GUILayout.Height(34f))) Build();

            var existing = Object.FindAnyObjectByType<PressureChamber>();
            using (new EditorGUI.DisabledScope(existing == null))
            {
                if (GUILayout.Button("Remove the gas fill")) Remove();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "After building, select the PressureChamber object: the scene view draws the room's box " +
                "and shades it to 45% so you can see where the gas will sit before pressing Play.",
                MessageType.None);
        }

        void DrawMeasurement()
        {
            if (_chamber == null) return;

            if (!StationBuild.TryRendererBounds(_chamber, out Bounds b))
            {
                EditorGUILayout.HelpBox($"'{_chamber.name}' has no meshes to measure.", MessageType.Warning);
                return;
            }

            Vector3 gas = new Vector3(b.size.x * _interiorFit, b.size.y * _heightFit,
                                      b.size.z * _interiorFit);
            EditorGUILayout.HelpBox(
                $"Hull {b.size.x:0.0} × {b.size.y:0.0} × {b.size.z:0.0} m at " +
                $"({b.center.x:0.0}, {b.center.y:0.0}, {b.center.z:0.0}).\n" +
                $"Gas volume {gas.x:0.0} × {gas.y:0.0} × {gas.z:0.0} m.",
                MessageType.None);
        }

        // ================================================================== find

        /// <summary>
        /// The tunnel's chambers are the groups whose names end in "Lock_GRP" — CrewLock_GRP and
        /// EquipLock_GRP here. Matching the ending rather than containing "lock" matters: every part of
        /// this import carries "BiodomeAirlockDoor1" in its Maya namespace, so a contains-test hits all
        /// two hundred of them.
        /// </summary>
        void Rescan()
        {
            _candidates.Clear();

            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (!t.name.EndsWith("Lock_GRP", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (!StationBuild.TryRendererBounds(t.gameObject, out _)) continue;
                _candidates.Add(t);
            }

            _candidates.Sort((a, b) => string.CompareOrdinal(Leaf(a.name), Leaf(b.name)));

            // Default to the EquipLock: counting in from the outside door that is the second chamber,
            // which is what "the second chamber of the tunnel" means walking in from the moon.
            _pick = 0;
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (Leaf(_candidates[i].name).ToLowerInvariant().Contains("equip")) { _pick = i; break; }
            }

            if (_candidates.Count > 0) _chamber = _candidates[_pick].gameObject;
        }

        /// <summary>Maya namespaces stack up as "file:group:part" — only the last part is the label.</summary>
        static string Leaf(string name)
        {
            int i = name.LastIndexOf(':');
            return i >= 0 && i < name.Length - 1 ? name.Substring(i + 1) : name;
        }

        // ================================================================== build

        void Build()
        {
            if (!StationBuild.RequireSceneObject(_chamber, "chamber")) return;
            if (!StationBuild.TryRendererBounds(_chamber, out Bounds b))
            {
                Debug.LogWarning($"[Pressure] '{_chamber.name}' has no meshes, so there is nothing to " +
                                 "measure. Pick the module group itself, not an empty above it.", _chamber);
                return;
            }

            var root = StationBuild.GeneratedRoot(RootName, clearChildren: false);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            string label = $"PressureChamber_{StationBuild.Sanitize(Leaf(_chamber.name))}";
            var host = StationBuild.FindOrCreateChild(root.transform, label);
            Undo.RecordObject(host.transform, "Build pressure chamber");

            // Unrotated, unscaled and parented to an identity root ON PURPOSE. The component's size and
            // centre are in its own local space, and the runtime writes them straight into the fog
            // emitter's shape — so any scale on an ancestor (the tunnel import carries 2.08) would silently
            // divide the volume it thinks it is filling.
            host.transform.SetPositionAndRotation(b.center, Quaternion.identity);
            host.transform.localScale = Vector3.one;

            // Clear the old fittings so re-running does not stack four more jets on top of the last four.
            for (int i = host.transform.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(host.transform.GetChild(i).gameObject);

            var chamber = StationBuild.GetOrAdd<PressureChamber>(host);
            Undo.RecordObject(chamber, "Build pressure chamber");

            Vector3 size = new Vector3(b.size.x * _interiorFit, b.size.y * _heightFit,
                                       b.size.z * _interiorFit);
            chamber.size = size;
            chamber.center = Vector3.zero;
            chamber.fillSeconds = _fillSeconds;
            chamber.ventSeconds = _ventSeconds;
            chamber.sealDelay = _sealDelay;

            Material gasMat = AirlockBuilderTool.MakeGasMaterial();

            chamber.jets = BuildJets(host.transform, b.center, size, gasMat);
            chamber.fog = BuildFog(host.transform, b.center, size, gasMat);
            chamber.statusLight = _addLight ? BuildLamp(host.transform, b.center, size) : null;

            EditorUtility.SetDirty(chamber);
            EditorSceneManager.MarkSceneDirty(host.scene);
            Selection.activeGameObject = host;

            Debug.Log($"[Pressure] Gas fill built in '{Leaf(_chamber.name)}'.\n" +
                      $"  Room {size.x:0.0} × {size.y:0.0} × {size.z:0.0} m at " +
                      $"({b.center.x:0.0}, {b.center.y:0.0}, {b.center.z:0.0}), " +
                      $"{(chamber.jets != null ? chamber.jets.Length : 0)} corner vent(s)" +
                      (chamber.statusLight != null ? " and a status lamp" : string.Empty) + ".\n" +
                      $"  Seals for {_sealDelay:0.0} s, fills over {_fillSeconds:0.0} s, vents over " +
                      $"{_ventSeconds:0.0} s — all real seconds, so the 16× sim speed does not rush it.\n" +
                      "  Walk in to start it. Nothing is wired to the doors, so it cannot trap you.",
                      host);
        }

        ParticleSystem[] BuildJets(Transform parent, Vector3 centre, Vector3 size, Material gasMat)
        {
            if (_jetCount <= 0) return new ParticleSystem[0];

            var jets = new ParticleSystem[_jetCount];
            float rx = size.x * 0.42f, rz = size.z * 0.42f;
            float y = centre.y - size.y * 0.42f;         // near the floor, blowing up into the room

            for (int i = 0; i < _jetCount; i++)
            {
                float a = (i / (float)_jetCount) * Mathf.PI * 2f + Mathf.PI * 0.25f;
                var pos = new Vector3(centre.x + Mathf.Cos(a) * rx, y, centre.z + Mathf.Sin(a) * rz);

                // Aimed inward and up: a jet pointed straight at the middle throws gas across the room
                // and out the far wall, which reads as a leak rather than as a fill.
                Vector3 inward = new Vector3(centre.x - pos.x, 0f, centre.z - pos.z).normalized;
                Vector3 aim = (inward + Vector3.up * 1.6f).normalized;

                jets[i] = AirlockBuilderTool.BuildVent(parent, $"Jet_{i + 1}", pos, aim, gasMat);
                Undo.RegisterCreatedObjectUndo(jets[i].gameObject, "Build pressure chamber");
            }
            return jets;
        }

        static ParticleSystem BuildFog(Transform parent, Vector3 centre, Vector3 size, Material gasMat)
        {
            // Identity local transform relative to the chamber, because PressureChamber writes the
            // emitter's shape position and scale in THIS object's local space every frame.
            ParticleSystem fog = AirlockBuilderTool.NewParticleObject(
                parent, "Fog", centre, Quaternion.identity, gasMat);
            Undo.RegisterCreatedObjectUndo(fog.gameObject, "Build pressure chamber");

            ParticleSystem.MainModule main = fog.main;
            main.startLifetime = 3.6f;
            main.startSpeed = 0.22f;
            main.startSize = Mathf.Clamp(Mathf.Min(size.x, size.z) * 0.38f, 0.35f, 3f);
            main.maxParticles = 900;

            ParticleSystem.ShapeModule shape = fog.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(size.x * 0.85f, 0.1f, size.z * 0.85f);
            shape.position = new Vector3(0f, -size.y * 0.5f, 0f);

            AirlockBuilderTool.SetGrowAndFade(fog, 0.75f, 1.5f);
            return fog;
        }

        static Light BuildLamp(Transform parent, Vector3 centre, Vector3 size)
        {
            var go = StationBuild.FindOrCreateChild(parent, "StatusLamp");
            Undo.RecordObject(go.transform, "Build pressure chamber");
            go.transform.SetPositionAndRotation(
                new Vector3(centre.x, centre.y + size.y * 0.36f, centre.z), Quaternion.identity);

            var light = StationBuild.GetOrAdd<Light>(go);
            Undo.RecordObject(light, "Build pressure chamber");
            light.type = LightType.Point;
            light.range = Mathf.Max(size.x, size.z) * 1.4f;
            light.intensity = 2.5f;
            light.shadows = LightShadows.None;
            return light;
        }

        void Remove()
        {
            var root = GameObject.Find(RootName);
            if (root == null) return;
            Undo.DestroyObjectImmediate(root);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[Pressure] Gas fill removed. The tunnel geometry is untouched — nothing was ever " +
                      "parented into it.");
        }
    }
}
