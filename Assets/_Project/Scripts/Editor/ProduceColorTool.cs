using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Gives the crops their own colours.
    ///
    /// The produce all looks the same dull green-grey for a concrete reason: the imported set ships with
    /// one material shared by nearly everything, so a carrot's root, a beetroot's root, a corn stalk and
    /// a tree's leaves are literally the same material. No amount of editing that one material can pull
    /// them apart — and it cannot be edited anyway, because materials embedded in an FBX are read-only
    /// sub-assets.
    ///
    /// So this recognises each plant by name, and gives every species its own material per part — root,
    /// leaf, flower — copied from whatever the import assigned, so shaders and any texture maps survive
    /// and only the colour changes. The copies are ordinary .mat files in Materials/Produce, editable in
    /// the Inspector afterwards like anything else.
    ///
    /// A <see cref="ProduceTintLog"/> records what each renderer had before, so Put the imported colours
    /// back really does.
    /// </summary>
    public sealed class ProduceColorTool : EditorWindow
    {
        const string LogRootName = "ProduceTint";
        const string MaterialDir = "Assets/_Project/Materials/Produce";

        enum Part { Main, Leaf, Flower }

        [System.Serializable]
        class Species
        {
            public string key;          // matched against the object name, lower-case, "contains"
            public string label;
            public bool on = true;
            public Color main, leaf, flower;

            // Unity rebuilds serialized lists through a parameterless constructor on every domain
            // reload, so the palette needs one alongside the readable one below.
            public Species() { }

            public Species(string key, string label, string main, string leaf, string flower)
            {
                this.key = key;
                this.label = label;
                this.main = Hex(main);
                this.leaf = Hex(leaf);
                this.flower = Hex(flower);
            }

            public Color For(Part part) => part == Part.Leaf ? leaf : part == Part.Flower ? flower : main;
        }

        static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out Color c);
            return c;
        }

        /// <summary>
        /// Longest key first, because these are substring matches and the names overlap: "redBeet" has to
        /// be tested before "beet", "cornThin" before "corn", "treeSimple" and "cherryTree" before "tree".
        /// </summary>
        [SerializeField] List<Species> _species = new List<Species>
        {
            //             key             label              root/stem  leaf       flower/fruit
            new Species("floweringpea", "Flowering peas",  "#4E9A3D", "#6BBF59", "#C86FD1"),
            new Species("cortinarius",  "Mushrooms",       "#8A5A34", "#7A5A3C", "#C0663A"),
            new Species("treesimple",   "Simple trees",    "#5C4228", "#3D8B41", "#F2F0DC"),
            new Species("cherrytree",   "Cherry trees",    "#6E4A2E", "#5FA85C", "#F4B6C9"),
            new Species("applearch",    "Apple arches",    "#6E4A2E", "#3F8F45", "#E23E3E"),
            new Species("curvetree",    "Leafy trees",     "#5C4228", "#4C9A4F", "#F2F0DC"),
            new Species("mushroom",     "Mushroom beds",   "#C9A87C", "#7A5A3C", "#A8552E"),
            new Species("rosemary",     "Rosemary",        "#5E6B4A", "#7FA07A", "#C9B6E0"),
            new Species("cornthin",     "Corn",            "#7FB539", "#9ACD4E", "#F6C90E"),
            new Species("lettuce",      "Lettuce",         "#86D34A", "#8FDC52", "#F4F7E8"),
            new Species("redbeet",      "Beetroot",        "#B01455", "#4E9A3D", "#D8437E"),
            new Species("carrot",       "Carrots",         "#F2701B", "#57A83A", "#FDF6E3"),
            new Species("clover",       "Clover",          "#4E9A3D", "#5FBF4A", "#F6F2E0"),
            new Species("sakura",       "Sakura",          "#6E4A2E", "#5FA85C", "#F4B6C9"),
            new Species("onion",        "Onions",          "#D9A441", "#6FAE3A", "#E4D8F2"),
            new Species("apple",        "Apples",          "#6E4A2E", "#3F8F45", "#E23E3E"),
            new Species("beet",         "Beet beds",       "#B01455", "#4E9A3D", "#D8437E"),
            new Species("corn",         "Corn beds",       "#7FB539", "#9ACD4E", "#F6C90E"),
        };

        [SerializeField] GameObject _root;
        [SerializeField] float _strength = 0.95f;
        [SerializeField] float _lift;
        [SerializeField] bool _dropTextures;
        [SerializeField] bool _alsoHandMade = true;
        [SerializeField] float _handMadeBoost = 1.3f;

        Vector2 _scroll;

        [MenuItem("Tools/NASA Sim/Plants/Colour The Produce")]
        public static void Open()
        {
            var window = GetWindow<ProduceColorTool>(true, "Produce Colours", true);
            window.minSize = new Vector2(540f, 620f);
            window.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "The crops share one imported material, which is why they all look the same. This gives " +
                "each species its own — carrots orange, beetroot magenta, corn yellow — and each part of " +
                "a plant its own too, so leaves stay green.\n\n" +
                "The new materials are real .mat files in Materials/Produce. Recolour a species here, or " +
                "open the material afterwards and tune it by hand.",
                MessageType.Info);

            _root = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Only under", "Optional. Restrict the sweep to one group — leave empty to " +
                                             "do every plant in the scene."),
                _root, typeof(GameObject), true);

            EditorGUILayout.Space();
            _strength = EditorGUILayout.Slider(
                new GUIContent("Strength", "How far to move from the imported colour to the one below. 1 " +
                                           "is the full palette; lower keeps some of the model's own tone."),
                _strength, 0f, 1f);
            _lift = EditorGUILayout.Slider(
                new GUIContent("Brighten", "Extra brightness on top, for a greenhouse that is still dim."),
                _lift, 0f, 0.5f);
            _dropTextures = EditorGUILayout.Toggle(
                new GUIContent("Ignore imported maps", "Clear the base texture so the colour reads flat " +
                                                       "and clean. Leave off if a crop has real artwork " +
                                                       "on it — the colour multiplies into the texture."),
                _dropTextures);

            EditorGUILayout.Space();
            _alsoHandMade = EditorGUILayout.BeginToggleGroup(
                new GUIContent("Also punch up the hand-made plant materials",
                               "Fruit and TreeCanopy — the plant materials this project made itself, " +
                               "edited in place. The grass is deliberately left out: it is the surface " +
                               "the mown logo is drawn on, so saturating it hides the letters. Use " +
                               "Grass > Tone Down The Grass for that."),
                _alsoHandMade);
            EditorGUI.indentLevel++;
            _handMadeBoost = EditorGUILayout.Slider(
                new GUIContent("Saturation ×"), _handMadeBoost, 1f, 2.5f);
            EditorGUI.indentLevel--;
            EditorGUILayout.EndToggleGroup();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Palette", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(" ", GUILayout.Width(18f));
                EditorGUILayout.LabelField("Species", GUILayout.Width(120f));
                EditorGUILayout.LabelField("Root / stem", GUILayout.MinWidth(60f));
                EditorGUILayout.LabelField("Leaf", GUILayout.MinWidth(60f));
                EditorGUILayout.LabelField("Flower / fruit", GUILayout.MinWidth(60f));
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(220f));
            foreach (Species s in _species)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    s.on = EditorGUILayout.Toggle(s.on, GUILayout.Width(18f));
                    using (new EditorGUI.DisabledScope(!s.on))
                    {
                        EditorGUILayout.LabelField(new GUIContent(s.label, $"Matches names containing '{s.key}'"),
                                                   GUILayout.Width(120f));
                        s.main = EditorGUILayout.ColorField(GUIContent.none, s.main, false, false, false,
                                                            GUILayout.MinWidth(60f));
                        s.leaf = EditorGUILayout.ColorField(GUIContent.none, s.leaf, false, false, false,
                                                            GUILayout.MinWidth(60f));
                        s.flower = EditorGUILayout.ColorField(GUIContent.none, s.flower, false, false, false,
                                                              GUILayout.MinWidth(60f));
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("All on")) foreach (Species s in _species) s.on = true;
                if (GUILayout.Button("All off")) foreach (Species s in _species) s.on = false;
                if (GUILayout.Button("Count what matches")) Report();
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Colour the produce", GUILayout.Height(34f))) Apply();

            var log = FindLog();
            using (new EditorGUI.DisabledScope(log == null || log.Count == 0))
                if (GUILayout.Button(log != null && log.Count > 0
                                         ? $"Put the imported colours back ({log.Count} plants)"
                                         : "Put the imported colours back"))
                    Revert();
        }

        // ================================================================== classify

        static ProduceTintLog FindLog() => Object.FindAnyObjectByType<ProduceTintLog>();

        /// <summary>
        /// Which species a renderer belongs to, and which part of the plant it is.
        ///
        /// The name is checked first, then each ancestor: an individual crop is named for its species
        /// ("carrots57Main"), but a whole bed is named on the group above ("newMushroomsBed") with plain
        /// parts underneath, and both have to land in the right bucket.
        /// </summary>
        Species Classify(Renderer renderer, out Part part)
        {
            part = Part.Main;

            for (Transform t = renderer.transform; t != null; t = t.parent)
            {
                string name = t.name;
                string lower = name.ToLowerInvariant();

                foreach (Species s in _species)
                {
                    if (!s.on || !lower.Contains(s.key)) continue;

                    // The part comes from the renderer's OWN name — an ancestor match tells you the
                    // species, never whether this particular mesh is the leaf or the root.
                    part = PartOf(renderer.name);
                    return s;
                }
            }
            return null;
        }

        /// <summary>
        /// Which bit of the plant a mesh is. The set names the parts with a suffix — "carrots57Main",
        /// "carrots57Leaf", "carrots57Flower" — with "Leafy" turning up on the trees.
        /// </summary>
        static Part PartOf(string name)
        {
            const System.StringComparison Loose = System.StringComparison.OrdinalIgnoreCase;
            if (name.EndsWith("Leaf", Loose) || name.EndsWith("Leafy", Loose) ||
                name.EndsWith("Leaves", Loose)) return Part.Leaf;
            if (name.EndsWith("Flower", Loose) || name.EndsWith("Fruit", Loose)) return Part.Flower;
            return Part.Main;
        }

        IEnumerable<Renderer> Candidates()
        {
            if (_root != null)
            {
                foreach (var r in _root.GetComponentsInChildren<Renderer>(true))
                    if (!(r is ParticleSystemRenderer)) yield return r;
                yield break;
            }

            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
                if (!(r is ParticleSystemRenderer) && r.gameObject.scene.IsValid()) yield return r;
        }

        void Report()
        {
            var counts = new Dictionary<string, int>();
            int total = 0;
            foreach (Renderer r in Candidates())
            {
                Species s = Classify(r, out Part part);
                if (s == null) continue;
                string key = $"{s.label} · {part}";
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
                total++;
            }

            if (total == 0)
            {
                Debug.LogWarning("[Produce] Nothing matched. Either the plants are named differently in " +
                                 "this scene, or the 'Only under' group has no crops in it.");
                return;
            }

            var lines = new List<string>();
            foreach (var kv in counts) lines.Add($"  {kv.Value,5}  {kv.Key}");
            lines.Sort();
            Debug.Log($"[Produce] {total} plant part(s) would be recoloured:\n" + string.Join("\n", lines));
        }

        // ================================================================== apply

        void Apply()
        {
            // Revert first. Re-applying over an already-tinted scene would otherwise record the tinted
            // materials as the "imported" ones, and the way back would be lost.
            ProduceTintLog log = FindLog();
            if (log != null && log.Count > 0)
            {
                Undo.RecordObject(log, "Colour the produce");
                log.Revert();
            }

            Directory.CreateDirectory(MaterialDir);

            var made = new Dictionary<string, Material>();   // species|part|sourceKey -> material
            var variants = new Dictionary<string, int>();    // species|part -> how many sources so far
            int touched = 0, slots = 0;

            var renderers = new List<Renderer>();
            foreach (Renderer r in Candidates()) renderers.Add(r);

            try
            {
                AssetDatabase.StartAssetEditing();

                for (int i = 0; i < renderers.Count; i++)
                {
                    Renderer r = renderers[i];
                    if (i % 200 == 0 &&
                        EditorUtility.DisplayCancelableProgressBar(
                            "Colouring the produce", $"{i} / {renderers.Count}",
                            renderers.Count == 0 ? 1f : (float)i / renderers.Count))
                        break;

                    Species s = Classify(r, out Part part);
                    if (s == null) continue;

                    Material[] originals = r.sharedMaterials;
                    if (originals == null || originals.Length == 0) continue;

                    var replacement = new Material[originals.Length];
                    bool changed = false;
                    for (int m = 0; m < originals.Length; m++)
                    {
                        Material src = originals[m];
                        if (src == null) { replacement[m] = null; continue; }

                        Material tinted = GetOrCreate(made, variants, s, part, src);
                        replacement[m] = tinted;
                        if (tinted != src) { changed = true; slots++; }
                    }
                    if (!changed) continue;

                    log = EnsureLog(ref log);
                    log.Record(r, originals);
                    Undo.RecordObject(r, "Colour the produce");
                    r.sharedMaterials = replacement;
                    touched++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            int handMade = _alsoHandMade ? BoostHandMade() : 0;

            if (touched == 0)
            {
                EditorUtility.DisplayDialog("Nothing matched",
                    "No plants were recognised. Check the species list has the crops in this scene " +
                    "ticked, and that 'Only under' is either empty or pointing at a group with crops in " +
                    "it. Press Count what matches to see what the names resolve to.", "OK");
                return;
            }

            if (log != null) EditorUtility.SetDirty(log);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Debug.Log($"[Produce] Recoloured {touched} plant part(s) across {slots} material slot(s), " +
                      $"using {made.Count} new material(s) in {MaterialDir}." +
                      (handMade > 0 ? $"\n  Also boosted {handMade} hand-made plant material(s)." : string.Empty) +
                      "\n  Every one is a copy of the material the import assigned, so shaders and maps " +
                      "are untouched and only the colour moved. Open any of them to tune it." +
                      "\n  'Put the imported colours back' undoes the lot, now or after a reload.");
        }

        ProduceTintLog EnsureLog(ref ProduceTintLog log)
        {
            if (log != null) return log;
            var root = StationBuild.GeneratedRoot(LogRootName, clearChildren: false);
            log = StationBuild.GetOrAdd<ProduceTintLog>(root);
            return log;
        }

        /// <summary>
        /// One material per species, part and source material. Keyed on the source too, so a species
        /// whose parts came in on two different imported materials keeps them apart instead of flattening
        /// both onto one.
        /// </summary>
        Material GetOrCreate(Dictionary<string, Material> made, Dictionary<string, int> variants,
                             Species s, Part part, Material src)
        {
            string key = $"{s.key}|{part}|{SourceKey(src)}";
            if (made.TryGetValue(key, out Material existing)) return existing;

            Color target = s.For(part);
            Color from = ReadColor(src);
            Color final = Color.Lerp(from, target, _strength);
            if (_lift > 0f)
            {
                Color.RGBToHSV(final, out float h, out float sat, out float v);
                final = Color.HSVToRGB(h, sat, Mathf.Clamp01(v + _lift));
            }
            final.a = from.a;

            // A path derived from the RUN, and reused. This used to call GenerateUniqueAssetPath, which
            // meant every re-run left the previous set behind as "Produce_Carrot_Main 1.mat", " 2", " 3" —
            // orphans nothing referenced, that only the newest revert log could undo. Recolouring is
            // something you do repeatedly while tuning, so it has to land on the same files each time.
            //
            // A species can still legitimately need more than one output for the same part, when its
            // pieces came in on two different import materials. That gets a _b, _c suffix counted within
            // this run, which is stable across runs because the renderers are walked in scene order.
            string family = $"{s.key}|{part}";
            variants.TryGetValue(family, out int nth);
            variants[family] = nth + 1;

            string stem = $"{MaterialDir}/Produce_{Capitalise(s.key)}_{part}";
            string path = nth == 0 ? $"{stem}.mat" : $"{stem}_{(char)('b' + nth - 1)}.mat";
            string niceName = Path.GetFileNameWithoutExtension(path);

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null)
            {
                // Re-seat it on the source material first: the species may now be classified onto a
                // different import material than last run, and CopyPropertiesFromMaterial brings the
                // shader and every map across with it.
                if (mat.shader != src.shader) mat.shader = src.shader;
                mat.CopyPropertiesFromMaterial(src);
            }
            else
            {
                mat = new Material(src) { name = niceName };
            }

            WriteColor(mat, final);
            if (_dropTextures)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", null);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", null);
            }

            if (AssetDatabase.LoadAssetAtPath<Material>(path) == null) AssetDatabase.CreateAsset(mat, path);
            else EditorUtility.SetDirty(mat);

            made[key] = mat;
            return mat;
        }

        static string Capitalise(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        /// <summary>
        /// A stable identity for a source material. GUID + local file id rather than the instance id,
        /// because these live INSIDE an FBX as sub-assets: the pair identifies one of them exactly, and
        /// unlike an instance id it means the same thing after a domain reload.
        /// </summary>
        static string SourceKey(Material src)
        {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(src, out string guid, out long localId))
                return $"{guid}:{localId}";
            return src.name;
        }

        // URP Lit uses _BaseColor; the built-in and a few imported shaders use _Color. Both are checked
        // so this works whatever the FBX importer picked.
        static Color ReadColor(Material m)
        {
            if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
            if (m.HasProperty("_Color")) return m.GetColor("_Color");
            return Color.white;
        }

        static void WriteColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        /// <summary>
        /// The project's own plant materials are ordinary assets, so these really can just have their
        /// colour edited — no copy needed. Saturation up, brightness up a little.
        /// </summary>
        int BoostHandMade()
        {
            // Grass is deliberately NOT on this list. It used to be, and boosting it pushed the lawn
            // plane to a fully saturated green that swallowed the mown logo — grass is the surface the
            // logo is drawn on, so it wants the opposite of what a crop wants. It lives in its own
            // window now: Tools > NASA Sim > Grass > Tone Down The Grass.
            string[] names = { "Fruit", "TreeCanopy" };
            int n = 0;

            foreach (string name in names)
            {
                string path = $"Assets/_Project/Materials/{name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null) continue;

                Color c = ReadColor(mat);
                Color.RGBToHSV(c, out float h, out float s, out float v);
                Color boosted = Color.HSVToRGB(h, Mathf.Clamp01(s * _handMadeBoost),
                                               Mathf.Clamp01(v + _lift * 0.5f));
                boosted.a = c.a;

                Undo.RecordObject(mat, "Colour the produce");
                WriteColor(mat, boosted);
                EditorUtility.SetDirty(mat);
                n++;
            }

            if (n > 0) AssetDatabase.SaveAssets();
            return n;
        }

        // ================================================================== revert

        void Revert()
        {
            ProduceTintLog log = FindLog();
            if (log == null || log.Count == 0)
            {
                Debug.LogWarning("[Produce] Nothing to put back — no record of a recolour in this scene.");
                return;
            }

            Undo.RecordObject(log, "Restore imported produce colours");
            foreach (ProduceTintLog.Entry e in log.entries)
                if (e != null && e.renderer != null) Undo.RecordObject(e.renderer, "Restore imported produce colours");

            int n = log.Revert();
            EditorUtility.SetDirty(log);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Debug.Log($"[Produce] Put {n} plant part(s) back on their imported materials. The generated " +
                      $"materials are still in {MaterialDir} — delete the folder if you want them gone.");
        }
    }
}
