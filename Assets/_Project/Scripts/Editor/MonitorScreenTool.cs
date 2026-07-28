using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Changes what the station's monitors are showing.
    ///
    /// <b>Why it needs a tool at all.</b> The screens are painted by a material that lives INSIDE
    /// SettingEnvo.fbx. Imported materials are read-only sub-assets — you cannot edit one in the
    /// Inspector, and there is no field on it to change — so the only way to put something else on a
    /// screen is to make a real material asset and reassign the slot. That is what this does.
    ///
    /// <b>Finding the screens without guessing.</b> Each monitor renderer has two material slots: a frame
    /// and a screen. Rather than hard-coding "slot 1", the tool counts how many NON-monitor objects use
    /// each material. The frame material also appears on the stands and the desks, so it has other users;
    /// the screen material is used by monitors and nothing else. Zero outside users means it is a screen.
    /// The window shows you that count before you commit to anything.
    ///
    /// Channels: a live camera feed (see <see cref="MonitorFeed"/>), any texture you drag in, a flat
    /// colour, or off.
    /// </summary>
    public sealed class MonitorScreenTool : EditorWindow
    {
        public enum Channel
        {
            /// <summary>A camera in the world, rendered live onto every screen.</summary>
            LiveFeed,
            /// <summary>Whatever image you drop in.</summary>
            YourOwnPicture,
            /// <summary>A flat colour — good for "powered on, nothing to report".</summary>
            SolidColour,
            /// <summary>Black.</summary>
            Off,
        }

        const string ScreenMatPath = "Assets/_Project/Materials/MonitorScreen.mat";
        const string FeedTexPath = "Assets/_Project/Data/MonitorFeed.renderTexture";
        const string FeedRootName = "MonitorFeed";

        Channel _channel = Channel.LiveFeed;
        MonitorFeed.Framing _framing = MonitorFeed.Framing.OverheadOfTheLogo;
        Texture _picture;
        Color _solid = new Color(0.06f, 0.35f, 0.30f);
        Color _tint = Color.white;
        bool _flipHorizontally;
        bool _includeKeypads;
        bool _selectionOnly;
        int _feedWidth = 640;
        float _fps = 20f;

        readonly List<Slot> _slots = new List<Slot>();
        bool _scanned;
        Vector2 _scroll;

        struct Slot
        {
            public Renderer Renderer;
            public int Index;
            public Material Current;
            public int OutsideUsers;      // non-monitor renderers sharing this material
        }

        [MenuItem("Tools/NASA Sim/Station/Monitor Screens")]
        public static void Open()
        {
            var w = GetWindow<MonitorScreenTool>(true, "Monitor Screens", true);
            w.minSize = new Vector2(500f, 560f);
            w.Scan();
        }

        // ------------------------------------------------------------------ GUI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "The screens are painted by a material embedded in SettingEnvo.fbx, which is read-only. " +
                "This makes a real material and puts it on the screen slots — the frames are left alone.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(_scanned ? $"{_slots.Count} screen slot(s) found" : "Not scanned",
                                           EditorStyles.boldLabel);
                if (GUILayout.Button("Rescan", GUILayout.Width(70f))) Scan();
            }

            bool keypads = EditorGUILayout.ToggleLeft(
                new GUIContent("Include the airlock keypads",
                               "The little kpScreen panels on the hatches. Off by default — they read as " +
                               "keypads, not displays."), _includeKeypads);
            if (keypads != _includeKeypads) { _includeKeypads = keypads; Scan(); }
            _selectionOnly = EditorGUILayout.ToggleLeft(
                new GUIContent("Only what I have selected", "Leave the rest of the monitors alone."),
                _selectionOnly);

            DrawFoundList();

            EditorGUILayout.Space();
            _channel = (Channel)EditorGUILayout.EnumPopup("Show", _channel);

            switch (_channel)
            {
                case Channel.LiveFeed:
                    using (new EditorGUI.IndentLevelScope())
                    {
                        _framing = (MonitorFeed.Framing)EditorGUILayout.EnumPopup(
                            new GUIContent("Camera", "What the feed camera does."), _framing);
                        // Width in pixels; the height follows at 16:9. Every screen samples this one texture.
                        _feedWidth = EditorGUILayout.IntPopup(
                            "Resolution", _feedWidth,
                            new[] { "320 (grainy)", "640", "1280 (sharp)" },
                            new[] { 320, 640, 1280 });
                        _fps = EditorGUILayout.Slider(
                            new GUIContent("Refresh (fps)", "How often the feed redraws. 20 already looks " +
                                           "like a monitor."), _fps, 1f, 60f);
                    }
                    EditorGUILayout.LabelField(
                        FramingBlurb(_framing), EditorStyles.wordWrappedMiniLabel);
                    break;

                case Channel.YourOwnPicture:
                    using (new EditorGUI.IndentLevelScope())
                        _picture = (Texture)EditorGUILayout.ObjectField(
                            new GUIContent("Picture", "Any texture in the project."),
                            _picture, typeof(Texture), false);
                    if (_picture == null)
                        EditorGUILayout.HelpBox("Drag a texture in, or the screens go white.",
                                                MessageType.Warning);
                    break;

                case Channel.SolidColour:
                    using (new EditorGUI.IndentLevelScope())
                        _solid = EditorGUILayout.ColorField("Colour", _solid);
                    break;
            }

            EditorGUILayout.Space();
            _tint = EditorGUILayout.ColorField(
                new GUIContent("Tint", "Multiplied over whatever is showing. Dim it here if the screens " +
                               "are blowing out the room."), _tint);
            _flipHorizontally = EditorGUILayout.ToggleLeft(
                new GUIContent("Flip horizontally", "If the model's screen UVs are mirrored and the feed " +
                               "comes out backwards."), _flipHorizontally);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!_scanned || _slots.Count == 0))
                if (GUILayout.Button("Put it on the screens", GUILayout.Height(30f)))
                    Apply();

            if (GUILayout.Button("Put the model's own screens back"))
                Restore();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                "The screens are lit by an Unlit shader on purpose: a display emits its own light, so it " +
                "should not go dark when the biodome does.", EditorStyles.wordWrappedMiniLabel);
        }

        void DrawFoundList()
        {
            if (!_scanned) return;

            if (_slots.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No screens found. A screen is a material slot on an object whose name contains " +
                    "'monitor'/'moniter'/'screen' AND which no other object in the scene uses — that " +
                    "second half is what tells a screen apart from its frame.", MessageType.Warning);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(90f));
            var shown = new HashSet<string>();
            foreach (Slot s in _slots)
            {
                if (s.Renderer == null) continue;
                string key = $"{s.Renderer.name}|{s.Index}";
                if (!shown.Add(key)) continue;
                if (GUILayout.Button($"{s.Renderer.name}  slot {s.Index}  " +
                                     $"({(s.Current != null ? s.Current.name : "none")}, " +
                                     $"{s.OutsideUsers} other user(s))", EditorStyles.miniLabel))
                    EditorGUIUtility.PingObject(s.Renderer.gameObject);
            }
            EditorGUILayout.EndScrollView();
        }

        static string FramingBlurb(MonitorFeed.Framing f)
        {
            switch (f)
            {
                case MonitorFeed.Framing.OverheadOfTheLogo:
                    return "Hangs over the pattern and turns slowly, so the mow reads as it happens.";
                case MonitorFeed.Framing.ChaseTheTractor:
                    return "Rides behind the tractor. Best screen in the station while the mow is running.";
                case MonitorFeed.Framing.FromTheDrone:
                    return "Mounted on the drone — the monitors only show anything once you launch it.";
                default:
                    return "Stays exactly where you drag the MonitorFeed object in the Scene view.";
            }
        }

        // ------------------------------------------------------------------ scan

        void Scan()
        {
            _slots.Clear();
            _scanned = true;

            var monitors = new List<Renderer>();
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                if (r is ParticleSystemRenderer) continue;
                if (IsMonitor(r.name)) monitors.Add(r);
            }
            if (monitors.Count == 0) return;

            var monitorSet = new HashSet<Renderer>(monitors);

            // How many objects that are NOT monitors use each material. A frame shared with the desk has
            // outside users; a screen has none.
            var outsideUse = new Dictionary<Material, int>();
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                if (r is ParticleSystemRenderer || monitorSet.Contains(r)) continue;
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    outsideUse.TryGetValue(m, out int n);
                    outsideUse[m] = n + 1;
                }
            }

            Material screenMat = AssetDatabase.LoadAssetAtPath<Material>(ScreenMatPath);

            foreach (Renderer r in monitors)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    int outside = 0;
                    if (m != null) outsideUse.TryGetValue(m, out outside);
                    // A slot we already own counts as a screen however many monitors share it.
                    bool isScreen = m == null || m == screenMat || outside == 0;
                    if (!isScreen) continue;
                    _slots.Add(new Slot { Renderer = r, Index = i, Current = m, OutsideUsers = outside });
                }
            }
        }

        bool IsMonitor(string rawName)
        {
            string leaf = BiodomeFixTools.Leaf(rawName).ToLowerInvariant();
            bool keypad = leaf.Contains("kpscreen") || leaf.Contains("keypad");
            if (keypad) return _includeKeypads;
            return leaf.Contains("monitor") || leaf.Contains("moniter") || leaf.Contains("screen");
        }

        // ------------------------------------------------------------------ apply

        void Apply()
        {
            Material screen = MakeScreenMaterial();
            MonitorFeed feed = _channel == Channel.LiveFeed ? BuildFeed() : null;

            // Remember what was there, once, so "put it back" has something to put back.
            if (feed == null) feed = Object.FindAnyObjectByType<MonitorFeed>();

            Material previous = null;
            foreach (Slot s in _slots)
                if (s.Current != null && s.Current != screen) { previous = s.Current; break; }

            int changed = 0;
            var selection = new HashSet<Renderer>();
            if (_selectionOnly)
                foreach (GameObject go in Selection.gameObjects)
                    if (go != null)
                        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                            selection.Add(r);

            foreach (Slot s in _slots)
            {
                if (s.Renderer == null) continue;
                if (_selectionOnly && !selection.Contains(s.Renderer)) continue;

                Undo.RecordObject(s.Renderer, "Monitor Screens");
                var mats = s.Renderer.sharedMaterials;
                if (s.Index >= mats.Length) continue;
                if (mats[s.Index] == screen) continue;
                mats[s.Index] = screen;
                s.Renderer.sharedMaterials = mats;
                EditorUtility.SetDirty(s.Renderer);
                changed++;
            }

            if (previous != null)
            {
                var holder = MonitorRoot();
                var memory = holder.GetComponent<MonitorScreenMemory>();
                if (memory == null) memory = Undo.AddComponent<MonitorScreenMemory>(holder);
                if (memory.originalScreenMaterial == null)
                {
                    Undo.RecordObject(memory, "Monitor Screens");
                    memory.originalScreenMaterial = previous;
                    EditorUtility.SetDirty(memory);
                }
            }

            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            string what = _channel switch
            {
                Channel.LiveFeed => $"a live {_framing} feed at {_feedWidth}px / {_fps:0} fps",
                Channel.YourOwnPicture => _picture != null ? $"'{_picture.name}'" : "a blank picture",
                Channel.SolidColour => "a flat colour",
                _ => "nothing (off)",
            };
            Debug.Log($"[Monitors] {changed} screen slot(s) now show {what}. " +
                      (changed == 0 ? "(Everything already had this material — the material itself was " +
                                      "still updated, so the picture changed anyway.) " : "") +
                      "Frames and stands were left alone: those materials have users outside the monitors.",
                      feed != null ? (Object)feed : null);

            Scan();
        }

        void Restore()
        {
            var memory = Object.FindAnyObjectByType<MonitorScreenMemory>();
            if (memory == null || memory.originalScreenMaterial == null)
            {
                Debug.LogWarning("[Monitors] Nothing recorded to restore — either the screens were never " +
                                 "changed, or the MonitorFeed object that remembered the original was " +
                                 "deleted. Undo (Cmd-Z) still works within this session.");
                return;
            }

            int changed = 0;
            foreach (Slot s in _slots)
            {
                if (s.Renderer == null) continue;
                Undo.RecordObject(s.Renderer, "Restore Monitor Screens");
                var mats = s.Renderer.sharedMaterials;
                if (s.Index >= mats.Length) continue;
                mats[s.Index] = memory.originalScreenMaterial;
                s.Renderer.sharedMaterials = mats;
                EditorUtility.SetDirty(s.Renderer);
                changed++;
            }

            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log($"[Monitors] {changed} slot(s) back to '{memory.originalScreenMaterial.name}'.");
            Scan();
        }

        // ------------------------------------------------------------------ the material

        Material MakeScreenMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(ScreenMatPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
                mat = new Material(shader) { name = "MonitorScreen" };
                AssetDatabase.CreateAsset(mat, ScreenMatPath);
            }

            Texture tex = null;
            Color colour = _tint;

            switch (_channel)
            {
                case Channel.LiveFeed:
                    tex = MakeFeedTexture();
                    break;
                case Channel.YourOwnPicture:
                    tex = _picture;
                    break;
                case Channel.SolidColour:
                    colour = _solid * _tint;
                    colour.a = 1f;
                    break;
                case Channel.Off:
                    colour = new Color(0.02f, 0.02f, 0.025f, 1f);
                    break;
            }

            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", colour);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", colour);

            // Mirrored screen UVs are common on imported furniture; flipping the scale is cheaper than
            // asking anyone to re-unwrap a monitor.
            Vector2 scale = _flipHorizontally ? new Vector2(-1f, 1f) : Vector2.one;
            Vector2 offset = _flipHorizontally ? new Vector2(1f, 0f) : Vector2.zero;
            if (mat.HasProperty("_BaseMap")) { mat.SetTextureScale("_BaseMap", scale); mat.SetTextureOffset("_BaseMap", offset); }

            mat.SetFloat("_Surface", 0f);                 // opaque: a screen is not a window
            mat.renderQueue = -1;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
            return mat;
        }

        RenderTexture MakeFeedTexture()
        {
            int h = Mathf.Max(2, Mathf.RoundToInt(_feedWidth * 9f / 16f));
            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(FeedTexPath);

            if (rt != null && (rt.width != _feedWidth || rt.height != h))
            {
                // Resizing needs the texture released first; Unity will not resize a created RT.
                rt.Release();
                rt.width = _feedWidth;
                rt.height = h;
                EditorUtility.SetDirty(rt);
            }

            if (rt == null)
            {
                rt = new RenderTexture(_feedWidth, h, 24, RenderTextureFormat.DefaultHDR)
                {
                    name = "MonitorFeed",
                    antiAliasing = 1,
                    useMipMap = false,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                AssetDatabase.CreateAsset(rt, FeedTexPath);
            }

            AssetDatabase.SaveAssetIfDirty(rt);
            return rt;
        }

        // ------------------------------------------------------------------ the feed camera

        GameObject MonitorRoot()
        {
            var existing = GameObject.Find("Monitors");
            if (existing != null) return existing;
            var go = new GameObject("Monitors");
            Undo.RegisterCreatedObjectUndo(go, "Monitor Screens");
            return go;
        }

        MonitorFeed BuildFeed()
        {
            GameObject root = MonitorRoot();

            Transform t = root.transform.Find(FeedRootName);
            GameObject feedGo;
            if (t != null) feedGo = t.gameObject;
            else
            {
                feedGo = new GameObject(FeedRootName);
                Undo.RegisterCreatedObjectUndo(feedGo, "Monitor Screens");
                Undo.SetTransformParent(feedGo.transform, root.transform, "Monitor Screens");
            }

            var cam = feedGo.GetComponentInChildren<Camera>(true);
            if (cam == null)
            {
                var camGo = new GameObject("FeedCamera");
                Undo.RegisterCreatedObjectUndo(camGo, "Monitor Screens");
                Undo.SetTransformParent(camGo.transform, feedGo.transform, "Monitor Screens");
                cam = camGo.AddComponent<Camera>();
            }

            // A second AudioListener in a scene is a warning every time you press Play.
            var listener = cam.GetComponent<AudioListener>();
            if (listener != null) Object.DestroyImmediate(listener);

            cam.enabled = false;                       // MonitorFeed renders it by hand
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 900f;
            cam.targetTexture = MakeFeedTexture();

            var feed = feedGo.GetComponent<MonitorFeed>();
            if (feed == null) feed = Undo.AddComponent<MonitorFeed>(feedGo);
            Undo.RecordObject(feed, "Monitor Screens");
            feed.feedCamera = cam;
            feed.output = cam.targetTexture;
            feed.framing = _framing;
            feed.framesPerSecond = _fps;
            feed.lookAt = LogoCentre();
            EditorUtility.SetDirty(feed);

            return feed;
        }

        /// <summary>
        /// Where the pattern actually is, taken from the logo's own waypoints rather than assumed to be
        /// the world origin — the CSV is the only thing that knows.
        /// </summary>
        static Vector3 LogoCentre()
        {
            var loader = Object.FindAnyObjectByType<CsvWaypointLoader>();
            if (loader != null && loader.csvFile != null)
            {
                var path = loader.Parse(loader.csvFile);
                if (path != null && !path.IsEmpty)
                {
                    Vector3 c = path.Bounds.center;
                    c.y = path.Bounds.min.y;
                    return c;
                }
            }
            return Vector3.zero;
        }
    }
}
