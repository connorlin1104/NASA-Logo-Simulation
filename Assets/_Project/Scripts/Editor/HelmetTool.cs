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
        const string VisorMatPath = "Assets/_Project/Materials/HelmetVisor.mat";
        const string ShellMatPath = "Assets/_Project/Materials/HelmetShell.mat";

        [SerializeField] GameObject _astronaut;
        [SerializeField] GameObject _helmetMesh;
        [SerializeField] GameObject _pressurizedArea;

        [SerializeField] float _helmetRadius = 0.17f;
        [SerializeField] float _forwardNudge = 0.02f;
        [SerializeField] float _upNudge = 0.04f;

        [SerializeField] float _hipSideways = 0.26f;
        [SerializeField] float _hipForward = 0.04f;
        [SerializeField] float _hipDrop = 0.06f;

        [SerializeField] float _takeOffSeconds = 1.5f;
        [SerializeField] float _putOnSeconds = 1.3f;
        [SerializeField] float _liftHeight = 0.30f;
        [SerializeField] bool _reachForIt = true;
        [SerializeField] bool _enableKey = true;

        [SerializeField] bool _buildZone = true;
        [SerializeField] float _zoneShrink = 0.9f;
        [SerializeField] float _zoneHeight = 14f;

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
                "Puts a helmet on the astronaut that comes off when you walk inside.\n\n" +
                "Assign your helmet mesh, or leave it empty for a see-through placeholder visor. Then " +
                "Build — the Scene view will show where it sits, where it ends up, and the arc it takes " +
                "between them. In game: walk into the biodome, or press H anywhere.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Parts", EditorStyles.boldLabel);
            _astronaut = Bucket("Astronaut", "The astronaut root. Found automatically.", _astronaut);
            _helmetMesh = Bucket("Helmet mesh",
                "Optional. Your helmet FBX or a scene copy of it — a DUPLICATE of the helmet portion, " +
                "since the astronaut model has no separable one. Empty = a placeholder visor.",
                _helmetMesh);
            _pressurizedArea = Bucket("Pressurized area",
                "The group whose size the trigger zone is taken from — normally the biodome.",
                _pressurizedArea);

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
            _takeOffSeconds = EditorGUILayout.Slider(new GUIContent("Take off (s)"), _takeOffSeconds, 0.3f, 4f);
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
            _buildZone = EditorGUILayout.BeginToggleGroup("Come off automatically inside", _buildZone);
            EditorGUI.indentLevel++;
            _zoneShrink = EditorGUILayout.Slider(
                new GUIContent("Zone size", "The trigger box as a fraction of the pressurized area's " +
                                            "footprint. Under 1 keeps its corners inside a round dome."),
                _zoneShrink, 0.4f, 1.2f);
            _zoneHeight = EditorGUILayout.Slider(
                new GUIContent("Zone height (m)"), _zoneHeight, 3f, 40f);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

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

        GameObject Bucket(string label, string tooltip, GameObject value) =>
            (GameObject)EditorGUILayout.ObjectField(new GUIContent(label, tooltip), value,
                                                    typeof(GameObject), true);

        // ================================================================== find

        void AutoFind()
        {
            if (_astronaut == null)
            {
                var a = Object.FindAnyObjectByType<AstronautController>();
                if (a != null) _astronaut = a.gameObject;
            }

            if (_pressurizedArea == null)
            {
                // The biggest thing called "dome" — the biodome shell, not a light fixture named after it.
                Transform best = null;
                float bestSize = 0f;
                foreach (var t in StationBuild.FindAllContaining("dome", "biosphere", "greenhouse"))
                {
                    if (!StationBuild.TryRendererBounds(t.gameObject, out Bounds b)) continue;
                    float size = b.size.x * b.size.z;
                    if (size > bestSize) { bestSize = size; best = t; }
                }
                if (best != null) _pressurizedArea = best.gameObject;
            }
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

            // ---- the zone ----
            if (_buildZone) BuildZone(rig);
            else rig.pressurizedZone = null;

            // ---- settings ----
            rig.takeOffSeconds = _takeOffSeconds;
            rig.putOnSeconds = _putOnSeconds;
            rig.liftHeight = _liftHeight;
            rig.reachForIt = _reachForIt;
            rig.enableKey = _enableKey;
            rig.locomotion = locomotion;
            rig.hand = _astronaut.GetComponent<HandActionController>();
            if (StationBuild.TryRendererBounds(helmet.gameObject, out Bounds hb))
                rig.SetGripRadius(Mathf.Max(hb.extents.x, hb.extents.y));

            EditorUtility.SetDirty(rig);
            EditorSceneManager.MarkSceneDirty(_astronaut.scene);
            Selection.activeGameObject = helmet.gameObject;

            Debug.Log($"[Helmet] Built on '{_astronaut.name}'.\n" +
                      $"  Worn on: {(head != null ? head.name : "(nothing — assign a head bone)")}   ·   " +
                      $"carried at: {hold.name}\n" +
                      $"  {(rig.pressurizedZone != null ? $"Comes off inside a {rig.zoneSize.x:0} × {rig.zoneSize.z:0} m zone at the biodome" : "No zone — H key only")}" +
                      (_enableKey ? ", or press H anywhere." : ".") + "\n" +
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

        void BuildZone(HelmetRemoval rig)
        {
            if (_pressurizedArea == null ||
                !StationBuild.TryRendererBounds(_pressurizedArea, out Bounds b))
            {
                rig.pressurizedZone = null;
                Debug.LogWarning("[Helmet] No pressurized area assigned (or it has no meshes), so the " +
                                 "helmet will only come off with the H key. Drop the biodome into the " +
                                 "'Pressurized area' bucket and build again.");
                return;
            }

            var zone = StationBuild.GeneratedRoot(ZoneName, clearChildren: false);
            Undo.RecordObject(zone.transform, "Build Helmet");
            float height = Mathf.Min(_zoneHeight, Mathf.Max(3f, b.size.y));
            zone.transform.position = new Vector3(b.center.x, b.min.y + height * 0.5f, b.center.z);
            zone.transform.rotation = Quaternion.identity;

            rig.pressurizedZone = zone.transform;
            rig.zoneSize = new Vector3(b.size.x * _zoneShrink, height, b.size.z * _zoneShrink);
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
