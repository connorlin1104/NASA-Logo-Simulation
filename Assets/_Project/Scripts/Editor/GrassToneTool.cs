using UnityEditor;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Puts the grass back to a green you can read the logo against.
    ///
    /// The produce recolour ends with a "boost the hand-made plant materials" pass, and Grass.mat was on
    /// its list — so the lawn plane went from a muted (0.20, 0.42, 0.16) to a fully saturated
    /// (0.10, 0.67, 0.00). Crops and grass want opposite treatments: a crop is the thing you look AT, so
    /// saturation helps it; grass is the surface the logo is drawn ON, so every bit of saturation spent
    /// on the green is contrast taken away from the cut.
    ///
    /// Three materials carry the field between them and they have to move together, because a mismatch
    /// between them reads worse than the brightness ever did:
    ///
    ///   Grass.mat        the ground plane under everything        1 renderer
    ///   GrassBlades.mat  the mowable blades (NasaSim/Grass)       1 renderer, millions of blades
    ///   GrassClump.mat   the scattered tufts sitting on top       ~3,000 renderers
    ///
    /// That last one is worth knowing about: it has been pure WHITE since it was made. The tuft scatter
    /// copies the FBX's embedded material so it can turn GPU instancing on, and the green fallback in
    /// that copy only fires when the model has no material at all — which it does have, an untinted one.
    /// Three thousand white tufts over a field is most of "very bright".
    ///
    /// Nothing in here touches Materials/Produce. The crops keep the colours you just gave them.
    /// </summary>
    public sealed class GrassToneTool : EditorWindow
    {
        const string GroundPath = "Assets/_Project/Materials/Grass.mat";
        const string BladePath = "Assets/_Project/Materials/GrassBlades.mat";
        const string ClumpPath = "Assets/_Project/Materials/GrassClump.mat";

        // The dull set. Standing grass sits well below mid-grey so the mown swath can be twice its
        // brightness without the mown colour itself having to be bright.
        static readonly Color DullGround = new Color(0.150f, 0.260f, 0.120f);
        static readonly Color DullRoot = new Color(0.080f, 0.160f, 0.060f);
        static readonly Color DullTip = new Color(0.260f, 0.420f, 0.170f);
        static readonly Color DullMown = new Color(0.600f, 0.640f, 0.340f);
        static readonly Color DullTuft = new Color(0.220f, 0.380f, 0.150f);

        // What these materials held before the produce pass ran over them, straight out of the commit.
        static readonly Color AuthoredGround = new Color(0.20f, 0.42f, 0.16f);
        static readonly Color AuthoredRoot = new Color(0.11f, 0.24f, 0.08f);
        static readonly Color AuthoredTip = new Color(0.46f, 0.76f, 0.26f);
        static readonly Color AuthoredMown = new Color(0.68f, 0.82f, 0.35f);

        [SerializeField] Color _ground = DullGround;
        [SerializeField] Color _root = DullRoot;
        [SerializeField] Color _tip = DullTip;
        [SerializeField] Color _mown = DullMown;
        [SerializeField] Color _tuft = DullTuft;

        [SerializeField] float _brightness = 1f;
        [SerializeField] float _mownBleach = 0.85f;
        [SerializeField] float _mownHeight = 0.14f;
        [SerializeField] bool _doGround = true, _doBlades = true, _doTufts = true;

        Vector2 _scroll;

        [MenuItem("Tools/NASA Sim/Grass/Tone Down The Grass")]
        public static void Open()
        {
            var w = GetWindow<GrassToneTool>(true, "Tone Down The Grass");
            w.minSize = new Vector2(430f, 560f);
            w.Show();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.HelpBox(
                "The logo is read as a contrast between standing grass and cut grass, so the green wants " +
                "to be DULL and the mown colour bright — not both bright.\n\n" +
                "This only edits the three grass materials. Materials/Produce is not touched, so the " +
                "crops keep their new colours.", MessageType.Info);

            DrawCurrentState();

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("What to repaint", EditorStyles.boldLabel);
            _doGround = EditorGUILayout.ToggleLeft("The ground plane (Grass.mat)", _doGround);
            _doBlades = EditorGUILayout.ToggleLeft("The mowable blades (GrassBlades.mat)", _doBlades);
            _doTufts = EditorGUILayout.ToggleLeft("The scattered tufts (GrassClump.mat)", _doTufts);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Colours", EditorStyles.boldLabel);
            _ground = EditorGUILayout.ColorField(
                new GUIContent("Ground", "The plane under the blades. Shows through wherever the grass " +
                                         "is short, so it wants to be the darkest thing here."), _ground);
            _root = EditorGUILayout.ColorField(
                new GUIContent("Blade roots", "The bottom of each blade — deep shadow inside the field."),
                _root);
            _tip = EditorGUILayout.ColorField(
                new GUIContent("Blade tips (standing)", "Uncut grass. Every blade also carries a " +
                                                        "per-blade tint that can only darken, so the " +
                                                        "field averages about 80% of this."), _tip);
            _mown = EditorGUILayout.ColorField(
                new GUIContent("Mown", "What the tractor leaves behind. THIS is the logo — the further " +
                                       "it sits from the standing colour, the further away you can read " +
                                       "the letters."), _mown);
            _tuft = EditorGUILayout.ColorField(
                new GUIContent("Scattered tufts", "The modelled clumps on top of the blade field."),
                _tuft);

            EditorGUILayout.Space(4f);
            _brightness = EditorGUILayout.Slider(
                new GUIContent("Overall brightness ×", "Scales every swatch above at the moment it is " +
                                                       "written. 1 leaves them exactly as shown."),
                _brightness, 0.4f, 1.6f);

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("How obvious the cut is", EditorStyles.boldLabel);
            _mownBleach = EditorGUILayout.Slider(
                new GUIContent("Mown bleach", "How far a cut blade goes toward the mown colour. Turn " +
                                              "this up before you turn the mown colour up."),
                _mownBleach, 0f, 1f);
            _mownHeight = EditorGUILayout.Slider(
                new GUIContent("Mown height", "What fraction of its length a cut blade keeps. Lower is " +
                                              "a sharper, more visible stripe."),
                _mownHeight, 0.02f, 0.5f);

            EditorGUILayout.Space(10f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Dull preset")) LoadPreset(dull: true);
                if (GUILayout.Button("Original preset")) LoadPreset(dull: false);
            }

            EditorGUILayout.Space(4f);
            if (GUILayout.Button("Tone the grass down", GUILayout.Height(34f))) Apply();

            EditorGUILayout.Space(2f);
            if (GUILayout.Button("Put back exactly what the produce pass overwrote"))
                RestoreAuthored();

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "The grass is now left alone by Plants ▸ Colour The Produce, so recolouring the crops " +
                "again will not undo this.", MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        // ================================================================== readout

        /// <summary>
        /// What the three materials hold right now. Worth showing rather than describing: the neon value
        /// on the ground plane is obvious the moment you see it next to the others.
        /// </summary>
        void DrawCurrentState()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("On disk right now", EditorStyles.boldLabel);

            Material ground = Load(GroundPath), blade = Load(BladePath), clump = Load(ClumpPath);

            Swatch("Grass.mat", ground, "_BaseColor");
            Swatch("GrassBlades tip", blade, "_TipColor");
            Swatch("GrassBlades mown", blade, "_MownColor");
            Swatch("GrassClump.mat", clump, "_BaseColor");

            if (clump != null && clump.HasProperty("_BaseColor"))
            {
                Color c = clump.GetColor("_BaseColor");
                if (c.r > 0.9f && c.g > 0.9f && c.b > 0.9f)
                {
                    var field = GameObject.Find("GrassField");
                    int tufts = field != null ? field.transform.childCount : 0;
                    EditorGUILayout.HelpBox(
                        $"The scattered tufts are pure white{(tufts > 0 ? $" — all {tufts} of them" : string.Empty)}. " +
                        "That is not the produce pass; the tuft scatter copies the FBX's own untinted " +
                        "material so it can enable GPU instancing, and never tints the copy. Repainting " +
                        "them green below is most of the fix.", MessageType.Warning);
                }
            }
        }

        static void Swatch(string label, Material m, string property)
        {
            if (m == null || !m.HasProperty(property))
            {
                EditorGUILayout.LabelField(label, m == null ? "missing" : $"no {property}");
                return;
            }

            Color c = m.GetColor(property);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                Rect r = GUILayoutUtility.GetRect(38f, 14f, GUILayout.ExpandWidth(false));
                EditorGUI.DrawRect(r, new Color(c.r, c.g, c.b, 1f));
                GUILayout.Space(6f);
                GUILayout.Label($"{c.r:0.00}, {c.g:0.00}, {c.b:0.00}", EditorStyles.miniLabel);
            }
        }

        static Material Load(string path) => AssetDatabase.LoadAssetAtPath<Material>(path);

        void LoadPreset(bool dull)
        {
            _ground = dull ? DullGround : AuthoredGround;
            _root = dull ? DullRoot : AuthoredRoot;
            _tip = dull ? DullTip : AuthoredTip;
            _mown = dull ? DullMown : AuthoredMown;
            _tuft = dull ? DullTuft : new Color(0.25f, 0.50f, 0.18f);
            _brightness = 1f;
            _mownBleach = dull ? 0.85f : 0.75f;
            _mownHeight = dull ? 0.14f : 0.16f;
        }

        // ================================================================== apply

        void Apply()
        {
            int n = 0;
            var touched = new System.Collections.Generic.List<string>();

            if (_doGround)
            {
                Material m = Load(GroundPath);
                if (m != null)
                {
                    Undo.RecordObject(m, "Tone down the grass");
                    SetColor(m, "_BaseColor", Scale(_ground));
                    SetColor(m, "_Color", Scale(_ground));
                    Save(m);
                    n++; touched.Add("Grass.mat");
                }
            }

            if (_doBlades)
            {
                Material m = Load(BladePath);
                if (m != null)
                {
                    Undo.RecordObject(m, "Tone down the grass");
                    SetColor(m, "_RootColor", Scale(_root));
                    SetColor(m, "_TipColor", Scale(_tip));
                    SetColor(m, "_MownColor", Scale(_mown));
                    if (m.HasProperty("_MownBleach")) m.SetFloat("_MownBleach", _mownBleach);
                    if (m.HasProperty("_MownHeight")) m.SetFloat("_MownHeight", _mownHeight);
                    Save(m);
                    n++; touched.Add("GrassBlades.mat");
                }
            }

            if (_doTufts)
            {
                Material m = Load(ClumpPath);
                if (m != null)
                {
                    Undo.RecordObject(m, "Tone down the grass");
                    SetColor(m, "_BaseColor", Scale(_tuft));
                    SetColor(m, "_Color", Scale(_tuft));
                    // The tufts were copied off the FBX purely to get instancing; keep that switched on
                    // or 3,000 draw calls come back with the colour.
                    m.enableInstancing = true;
                    Save(m);
                    n++; touched.Add("GrassClump.mat");
                }
            }

            AssetDatabase.SaveAssets();

            if (n == 0)
            {
                Debug.LogWarning("[Grass] Nothing to repaint — none of the three grass materials are in " +
                                 "Assets/_Project/Materials. Build the field first with " +
                                 "Tools > NASA Sim > Grass > Build Mowable Grass.");
                return;
            }

            Debug.Log($"[Grass] Toned down {n} material(s): {string.Join(", ", touched)}.\n" +
                      $"  Standing {Fmt(Scale(_tip))} against mown {Fmt(Scale(_mown))} — the mown swath " +
                      $"is about {Luma(Scale(_mown)) / Mathf.Max(0.001f, Luma(Scale(_tip))):0.0}× as " +
                      "bright as the grass around it, which is what makes the letters readable from the " +
                      "air.\n  No crop material was touched.");
        }

        /// <summary>
        /// The three colours the produce pass actually overwrote, put back bit-for-bit. Useful as a
        /// baseline to tune from when the dull preset has gone too far the other way.
        /// </summary>
        void RestoreAuthored()
        {
            Material ground = Load(GroundPath), blade = Load(BladePath);
            if (ground != null)
            {
                Undo.RecordObject(ground, "Restore grass");
                SetColor(ground, "_BaseColor", AuthoredGround);
                SetColor(ground, "_Color", AuthoredGround);
                Save(ground);
            }
            if (blade != null)
            {
                Undo.RecordObject(blade, "Restore grass");
                SetColor(blade, "_RootColor", AuthoredRoot);
                SetColor(blade, "_TipColor", AuthoredTip);
                SetColor(blade, "_MownColor", AuthoredMown);
                Save(blade);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[Grass] Ground plane and blades put back to the values they held before the " +
                      "produce recolour. The white tufts are unchanged — they were white before it too.");
        }

        // ================================================================== helpers

        Color Scale(Color c)
        {
            if (Mathf.Approximately(_brightness, 1f)) return c;
            Color.RGBToHSV(c, out float h, out float s, out float v);
            Color scaled = Color.HSVToRGB(h, s, Mathf.Clamp01(v * _brightness));
            scaled.a = c.a;
            return scaled;
        }

        static void SetColor(Material m, string property, Color c)
        {
            if (m.HasProperty(property)) m.SetColor(property, c);
        }

        static void Save(Material m)
        {
            EditorUtility.SetDirty(m);
        }

        static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        static string Fmt(Color c) => $"({c.r:0.00}, {c.g:0.00}, {c.b:0.00})";
    }
}
