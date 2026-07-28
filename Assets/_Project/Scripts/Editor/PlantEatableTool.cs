using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Makes the crops in the biodome edible — walk up to a carrot, press E, and the astronaut picks it
    /// and eats it, the same flourish the fruit trees already use.
    ///
    /// <b>What counts as one plant.</b> The imported greenhouse names every individual plant
    /// <c>&lt;species&gt;&lt;n&gt;MeshGroup</c>, with its parts as children: <c>…Main</c> (the vegetable),
    /// <c>…Leaf</c> and <c>…Flower</c>. That node is the unit — not the bed it sits in, and not the
    /// individual meshes. There are around eighteen hundred of them.
    ///
    /// <b>Only the Main part gets eaten.</b> <see cref="EatableObject.body"/> points at the <c>…Main</c>
    /// child, so pulling a carrot takes the carrot and leaves the greens standing in the soil. It grows
    /// back on a timer, so a bed is never permanently stripped.
    ///
    /// <b>The trigger goes ON the plant node, not on a child.</b> The usual convention
    /// (<see cref="MakeInteractableTool"/>) adds an InteractTrigger child so an object keeps its own
    /// layer and colliders — but a MeshGroup node has neither: no renderer, no collider, nothing to
    /// protect. At this scale that convention would add eighteen hundred GameObjects to a scene file that
    /// is already 26 MB, to guard against a conflict that cannot happen here.
    ///
    /// The species table below decides the prompt wording and how many bites each crop takes. Trees are
    /// off by default — a tree is scenery, and the apples on it are their own plants.
    /// </summary>
    public sealed class PlantEatableTool : EditorWindow
    {
        // --------------------------------------------------------------- species

        struct Species
        {
            public string Label;      // what the prompt says
            public int Bites;
            public bool EdibleByDefault;
            public Species(string label, int bites, bool edible = true)
            { Label = label; Bites = bites; EdibleByDefault = edible; }
        }

        // Keyed on the token the importer left in the material name: "Produce_Applearch_Main" -> applearch.
        static readonly Dictionary<string, Species> Table = new Dictionary<string, Species>
        {
            { "applearch",    new Species("apple", 3) },
            { "carrot",       new Species("carrot", 2) },
            { "cornthin",     new Species("corn", 4) },
            { "cortinarius",  new Species("mushroom", 2) },
            { "floweringpea", new Species("peas", 2) },
            { "lettuce",      new Species("lettuce", 3) },
            { "onion",        new Species("onion", 3) },
            { "redbeet",      new Species("beetroot", 3) },
            { "rosemary",     new Species("sprig of rosemary", 1) },
            { "treesimple",   new Species("tree", 5, edible: false) },
        };

        sealed class Plant
        {
            public Transform Node;
            public Transform Main;
            public string Species;
            public Vector3 Position;
        }

        // --------------------------------------------------------------- state

        readonly List<Plant> _plants = new List<Plant>();
        readonly Dictionary<string, bool> _enabled = new Dictionary<string, bool>();
        readonly Dictionary<string, int> _counts = new Dictionary<string, int>();

        int _maxPerSpecies;                 // 0 = no cap
        float _minSpacing;                  // 0 = every plant
        float _respawnSeconds = 45f;
        float _triggerRadius = 0.6f;
        bool _scanned;
        Vector2 _scroll;

        [MenuItem("Tools/NASA Sim/Interactables/Make The Plants Eatable")]
        public static void Open()
        {
            var w = GetWindow<PlantEatableTool>(true, "Make The Plants Eatable", true);
            w.minSize = new Vector2(480f, 560f);
            w.Scan();
        }

        // --------------------------------------------------------------- GUI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Wires every crop in the biodome for [E] Eat. Only the plant's Main part is picked — the " +
                "leaves stay in the ground and the crop grows back on a timer.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(_scanned ? $"{_plants.Count} plant(s) in the scene" : "Not scanned",
                                           EditorStyles.boldLabel);
                if (GUILayout.Button("Rescan", GUILayout.Width(70f))) Scan();
            }

            if (_scanned && _plants.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No plants found. This looks for objects named '…MeshGroup' with renderer children — " +
                    "the shape the greenhouse import produces.", MessageType.Warning);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Which crops", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(150f));
            var species = new List<string>(_counts.Keys);
            species.Sort(System.StringComparer.Ordinal);
            foreach (string key in species)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool on = _enabled.TryGetValue(key, out bool v) && v;
                    bool now = EditorGUILayout.ToggleLeft($"{Label(key)}", on, GUILayout.Width(220f));
                    if (now != on) _enabled[key] = now;
                    EditorGUILayout.LabelField($"{_counts[key]}  ({key})", EditorStyles.miniLabel);
                }
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("All", GUILayout.Width(60f))) SetAll(true);
                if (GUILayout.Button("None", GUILayout.Width(60f))) SetAll(false);
                if (GUILayout.Button("Just the food", GUILayout.Width(110f))) SetAll(null);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("How many", EditorStyles.boldLabel);
            _maxPerSpecies = EditorGUILayout.IntSlider(
                new GUIContent("Cap per crop", "0 wires every one. Lower it if you would rather have a " +
                               "scattering of edible plants than a whole field of them."),
                _maxPerSpecies, 0, 400);
            _minSpacing = EditorGUILayout.Slider(
                new GUIContent("Keep them apart by (m)", "Skips a plant that is closer than this to one " +
                               "already wired, so a dense bed does not become a wall of prompts. 0 wires " +
                               "the lot."), _minSpacing, 0f, 3f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("How it eats", EditorStyles.boldLabel);
            _respawnSeconds = EditorGUILayout.Slider(
                new GUIContent("Grows back after (s)", "Real seconds — the mow's 16x fast-forward does " +
                               "not apply."), _respawnSeconds, 2f, 300f);
            _triggerRadius = EditorGUILayout.Slider(
                new GUIContent("Reach (m)", "How close you have to stand. A carrot is small; a trigger " +
                               "matched to its actual size is almost impossible to hit."),
                _triggerRadius, 0.2f, 2f);

            int selected = CountSelected(out int capped);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                capped == selected
                    ? $"{selected} plant(s) will be wired."
                    : $"{capped} of {selected} plant(s) will be wired (the rest fall outside the cap or " +
                      "the spacing).",
                capped > 900 ? MessageType.Warning : MessageType.None);

            if (capped > 900)
                EditorGUILayout.LabelField(
                    "That is a lot of components and colliders. It runs fine — each one is two float " +
                    "compares a frame when nothing is happening — but it will add a few MB to the scene " +
                    "file. Use the cap or the spacing if you would rather not.",
                    EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(capped == 0))
                if (GUILayout.Button("Make them eatable", GUILayout.Height(30f))) Apply();

            if (GUILayout.Button("Take it all back off"))
                Remove();
        }

        void SetAll(bool? value)
        {
            var keys = new List<string>(_enabled.Keys);
            foreach (string k in keys)
                _enabled[k] = value ?? (Table.TryGetValue(k, out Species s) ? s.EdibleByDefault : true);
        }

        // --------------------------------------------------------------- scan

        void Scan()
        {
            _plants.Clear();
            _counts.Clear();
            _previewKey = null;
            _scanned = true;

            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t == null || !t.name.Contains("MeshGroup")) continue;

                Transform main = null;
                bool anyRenderer = false;
                foreach (Transform c in t)
                {
                    if (c.GetComponent<Renderer>() == null) continue;
                    anyRenderer = true;
                    if (c.name.EndsWith("Main")) main = c;
                }
                if (!anyRenderer) continue;

                string species = SpeciesOf(t, main);
                _plants.Add(new Plant { Node = t, Main = main, Species = species, Position = t.position });
                _counts.TryGetValue(species, out int n);
                _counts[species] = n + 1;
            }

            foreach (string key in _counts.Keys)
                if (!_enabled.ContainsKey(key))
                    _enabled[key] = !Table.TryGetValue(key, out Species s) || s.EdibleByDefault;
        }

        /// <summary>
        /// Which crop this is. The material name is the source of truth — the recolour pass writes
        /// "Produce_Cornthin_Main", which survives whatever anyone renames the object to. The object name
        /// is only the fallback.
        /// </summary>
        static string SpeciesOf(Transform node, Transform main)
        {
            if (main != null && main.TryGetComponent(out Renderer r) && r.sharedMaterial != null)
            {
                string m = r.sharedMaterial.name;
                if (m.StartsWith("Produce_"))
                {
                    string[] bits = m.Split('_');
                    if (bits.Length >= 2) return bits[1].ToLowerInvariant();
                }
            }

            // "newGreenHouse_2:appleArch9MeshGroup" -> "applearch"
            string leaf = BiodomeFixTools.Leaf(node.name);
            int cut = leaf.IndexOf("MeshGroup", System.StringComparison.Ordinal);
            if (cut > 0) leaf = leaf.Substring(0, cut);
            leaf = leaf.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return leaf.Length == 0 ? "plant" : leaf.ToLowerInvariant();
        }

        static string Label(string species) =>
            Table.TryGetValue(species, out Species s) ? s.Label : species;

        static int Bites(string species) =>
            Table.TryGetValue(species, out Species s) ? s.Bites : 3;

        // --------------------------------------------------------------- selection

        /// <summary>
        /// The plants that pass the crop toggles, then the cap and the spacing. Spacing is greedy and
        /// per-crop: a carrot is only crowded out by another carrot, so thinning a dense bed never wipes
        /// out a crop that happens to grow next to it.
        /// </summary>
        List<Plant> Choose()
        {
            var bySpecies = new Dictionary<string, List<Plant>>();
            foreach (Plant p in _plants)
            {
                if (!_enabled.TryGetValue(p.Species, out bool on) || !on) continue;
                if (!bySpecies.TryGetValue(p.Species, out List<Plant> list))
                    bySpecies[p.Species] = list = new List<Plant>();
                list.Add(p);
            }

            var chosen = new List<Plant>();
            foreach (List<Plant> list in bySpecies.Values)
            {
                // Deterministic order, so re-running picks the same plants and does not shuffle the
                // scene about. Position breaks the ties, because a bed's plants often share a name.
                list.Sort((a, b) =>
                {
                    int byName = string.CompareOrdinal(a.Node.name, b.Node.name);
                    if (byName != 0) return byName;
                    int byX = a.Position.x.CompareTo(b.Position.x);
                    return byX != 0 ? byX : a.Position.z.CompareTo(b.Position.z);
                });

                var kept = new List<Plant>();
                float sqr = _minSpacing * _minSpacing;
                foreach (Plant p in list)
                {
                    if (_maxPerSpecies > 0 && kept.Count >= _maxPerSpecies) break;
                    if (_minSpacing > 0f)
                    {
                        bool crowded = false;
                        foreach (Plant q in kept)
                            if ((q.Position - p.Position).sqrMagnitude < sqr) { crowded = true; break; }
                        if (crowded) continue;
                    }
                    kept.Add(p);
                }
                chosen.AddRange(kept);
            }
            return chosen;
        }

        // The spacing filter is O(n^2) over eighteen hundred plants, which is fine once and awful on
        // every repaint — so the preview is recomputed only when something that feeds it changes.
        string _previewKey;
        int _previewTotal, _previewKept;

        int CountSelected(out int afterLimits)
        {
            var key = new StringBuilder();
            key.Append(_plants.Count).Append('|').Append(_maxPerSpecies).Append('|').Append(_minSpacing);
            foreach (KeyValuePair<string, bool> kv in _enabled)
                if (kv.Value) key.Append('|').Append(kv.Key);

            if (key.ToString() != _previewKey)
            {
                _previewKey = key.ToString();
                _previewTotal = 0;
                foreach (Plant p in _plants)
                    if (_enabled.TryGetValue(p.Species, out bool on) && on) _previewTotal++;
                _previewKept = Choose().Count;
            }

            afterLimits = _previewKept;
            return _previewTotal;
        }

        // --------------------------------------------------------------- apply

        void Apply()
        {
            List<Plant> chosen = Choose();
            if (chosen.Count == 0) return;

            var perSpecies = new Dictionary<string, int>();
            Undo.SetCurrentGroupName("Make The Plants Eatable");
            int group = Undo.GetCurrentGroup();

            try
            {
                for (int i = 0; i < chosen.Count; i++)
                {
                    if (i % 64 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            "Make The Plants Eatable", $"{i} / {chosen.Count}", i / (float)chosen.Count))
                        break;

                    Plant p = chosen[i];
                    WirePlant(p);
                    perSpecies.TryGetValue(p.Species, out int n);
                    perSpecies[p.Species] = n + 1;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());

            var sb = new StringBuilder($"[Plants] {chosen.Count} plant(s) are now eatable.\n");
            foreach (KeyValuePair<string, int> kv in perSpecies)
                sb.AppendLine($"  {kv.Value,5}  {Label(kv.Key)}  ([E] Eat {Label(kv.Key)}, " +
                              $"{Bites(kv.Key)} bite(s))");
            sb.AppendLine($"  Each grows back {_respawnSeconds:0} s after it is finished. Only the Main " +
                          "part is taken — the leaves stay in the ground.");
            Debug.Log(sb.ToString());

            Scan();
        }

        void WirePlant(Plant p)
        {
            GameObject go = p.Node.gameObject;
            // RecordObject, not RegisterFullObjectHierarchyUndo: the heavy one snapshots the whole
            // subtree, and eighteen hundred snapshots is a lot of memory to hold for a layer change and
            // two components. Undo.AddComponent registers the components itself.
            Undo.RecordObject(go, "Make The Plants Eatable");

            var eat = go.GetComponent<EatableObject>();
            if (eat == null) eat = Undo.AddComponent<EatableObject>(go);
            else Undo.RecordObject(eat, "Make The Plants Eatable");
            eat.verb = "Eat";
            eat.label = Label(p.Species);
            eat.bites = Bites(p.Species);
            eat.body = p.Main != null ? p.Main : p.Node;
            eat.whenFinished = EatableObject.WhenFinished.Respawn;
            eat.respawnSeconds = _respawnSeconds;
            EditorUtility.SetDirty(eat);

            // On the node itself: it has no renderer and no collider of its own, so there is nothing for
            // an InteractTrigger child to keep separate. See the class comment.
            go.layer = NasaLayers.Interactable;
            var col = go.GetComponent<SphereCollider>();
            if (col == null) col = Undo.AddComponent<SphereCollider>(go);
            else Undo.RecordObject(col, "Make The Plants Eatable");
            col.isTrigger = true;

            // The greenhouse is imported at 2.8x, so a radius typed in local units would be almost three
            // times what it says. Divide it back out and the number in the window is honest metres.
            Vector3 ls = p.Node.lossyScale;
            float scale = Mathf.Max(1e-4f, (Mathf.Abs(ls.x) + Mathf.Abs(ls.y) + Mathf.Abs(ls.z)) / 3f);
            col.radius = _triggerRadius / scale;

            // Centre it on what you can actually see, not on the pivot, which imports at ground level.
            if (TryBounds(go, out Bounds b)) col.center = p.Node.InverseTransformPoint(b.center);
            else col.center = Vector3.zero;

            EditorUtility.SetDirty(col);
        }

        void Remove()
        {
            int removed = 0;
            Undo.SetCurrentGroupName("Take The Plants Back Off");
            int group = Undo.GetCurrentGroup();

            foreach (EatableObject eat in Object.FindObjectsByType<EatableObject>(FindObjectsInactive.Include))
            {
                if (eat == null || !eat.name.Contains("MeshGroup")) continue;
                GameObject go = eat.gameObject;
                Undo.RecordObject(go, "Take The Plants Back Off");
                go.layer = 0;
                var col = go.GetComponent<SphereCollider>();
                if (col != null) Undo.DestroyObjectImmediate(col);
                Undo.DestroyObjectImmediate(eat);
                removed++;
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log($"[Plants] {removed} plant(s) are back to being scenery. Fruit on the trees and " +
                      "anything you made eatable by hand is untouched — only '…MeshGroup' nodes were " +
                      "considered.");
            Scan();
        }

        static bool TryBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }
    }
}
