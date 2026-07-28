using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Stocks a <see cref="WaterBody"/> with ducks, fish and lilypads.
    ///
    /// <b>The FBX is the duck.</b> Duck.fbx / Fish.fbx / Lilypad.fbx are instantiated whole, every time.
    /// Nothing in the scene is treated as authoritative, because the scene is where things go wrong: an
    /// earlier version of this tool matched model parts by NAME, and Duck.fbx has nineteen nodes carrying
    /// the Maya namespace ("Duck:mallard_body") and exactly one that lost it ("mallard_bill"). That one
    /// rename broke the "is this the whole model?" test at every level, and the tool ended up wiring a
    /// bill, a wing and a foot into the moat as three separate ducks. Matching by name cannot be made
    /// safe; instantiating the asset needs no matching at all.
    ///
    /// <b>Anything that is not the whole model is replaced.</b> Every animal already in the water is
    /// checked against the mesh set a fresh instance would have, and a duck missing so much as a foot is
    /// destroyed and built again. That is what heals a scene the old tool has already been run on —
    /// re-running fixes it rather than preserving the damage.
    ///
    /// <b>Adoption is opt-in.</b> Pulling a hand-placed duck in from elsewhere in the scene is the thing
    /// that used to go wrong, so it is off unless you ask for it.
    ///
    /// Ducks and fish are pettable (<see cref="PettableObject"/> plus a trigger on the Interactable
    /// layer); lilypads just float. Re-running ADJUSTS the population rather than duplicating it.
    /// </summary>
    public sealed class WildlifeSpawnTool : EditorWindow
    {
        public enum Kind { Duck, Fish, Lilypad }

        /// <summary>
        /// How long each kind ends up, in metres, measured across its longest axis.
        ///
        /// Passed around rather than kept in a static, so a call from the pond builder can't silently
        /// pick up whatever size this window was last left on.
        /// </summary>
        public struct Sizes
        {
            public float duck, fish, lilypad;

            /// <summary>
            /// Deliberately larger than life. A real mallard is 0.55 m and reads as a speck across a
            /// 47 m moat; these are sized to be recognisable from the balcony, which is where they are
            /// actually looked at.
            /// </summary>
            public static Sizes Default => new Sizes { duck = 1.1f, fish = 0.7f, lilypad = 1.5f };

            public float For(Kind kind)
            {
                float v = kind switch
                {
                    Kind.Duck => duck,
                    Kind.Fish => fish,
                    _ => lilypad,
                };
                // A default(Sizes) is all zeros, and a zero target length would collapse the model to
                // nothing. Fall back to the sensible number rather than to a dot on the water.
                return v > 0.01f ? v : Default.For(kind);
            }
        }

        WaterBody _target;
        int _ducks = 3;
        int _fish = 8;
        int _lilypads = 6;
        Sizes _sizes = Sizes.Default;
        bool _replacePlaceholders = true;
        bool _resizeEverything = true;
        bool _fixMaterials = true;
        bool _adopt;

        bool _scanned;
        string _brokenSummary = "";
        List<Transform> _stranded = new List<Transform>();

        [MenuItem("Tools/NASA Sim/Water/Spawn Ducks, Fish && Lilypads")]
        public static void Open()
        {
            var w = GetWindow<WildlifeSpawnTool>(true, "Ducks, Fish & Lilypads", true);
            w.minSize = new Vector2(460f, 460f);
            w.AutoTarget();
            w.Rescan();
        }

        void AutoTarget()
        {
            if (Selection.activeGameObject != null)
                _target = Selection.activeGameObject.GetComponentInParent<WaterBody>();
            if (_target == null) _target = FindAnyObjectByType<WaterBody>();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Fills a water body with wildlife. Re-run to adjust the counts — nothing duplicates.\n\n" +
                "Duck.fbx / Fish.fbx / Lilypad.fbx are instantiated whole. Anything already in the water " +
                "that is not the complete model — a lone wing left behind by an older version of this " +
                "tool — is thrown away and built again from the FBX.", MessageType.Info);

            _target = (WaterBody)EditorGUILayout.ObjectField("Water body", _target, typeof(WaterBody), true);

            EditorGUILayout.Space();
            _ducks = EditorGUILayout.IntSlider("Ducks", _ducks, 0, 20);
            _fish = EditorGUILayout.IntSlider("Fish", _fish, 0, 40);
            _lilypads = EditorGUILayout.IntSlider("Lilypads", _lilypads, 0, 40);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("How big, nose to tail (m)", EditorStyles.boldLabel);
            _sizes.duck = EditorGUILayout.Slider(
                new GUIContent("Duck", "A real mallard is 0.55 m. Across a moat this wide that is a " +
                                       "speck, so the default is roughly double life size."),
                _sizes.duck, 0.2f, 4f);
            _sizes.fish = EditorGUILayout.Slider(new GUIContent("Fish"), _sizes.fish, 0.1f, 3f);
            _sizes.lilypad = EditorGUILayout.Slider(new GUIContent("Lilypad"), _sizes.lilypad, 0.2f, 5f);
            if (GUILayout.Button("Back to the defaults", EditorStyles.miniButton))
                _sizes = Sizes.Default;

            EditorGUILayout.Space();
            _replacePlaceholders = EditorGUILayout.ToggleLeft(
                new GUIContent("Replace the placeholder ones",
                               "Deletes the primitive PH_ ducks and fish so the imported models take " +
                               "their place. This is the 'get rid of the old ones' switch."),
                _replacePlaceholders);
            _resizeEverything = EditorGUILayout.ToggleLeft(
                new GUIContent("Resize what's already there",
                               "Applies the sizes above to animals that are already in the water. Turn " +
                               "it off once you have sized one by hand and want it left alone."),
                _resizeEverything);
            _fixMaterials = EditorGUILayout.ToggleLeft(
                new GUIContent("Fix wrong materials on adopted models",
                               "Puts the FBX's own materials back. The fish is currently wearing Grass " +
                               "on its body and Fruit on its fins — a bulk recolour caught it while it " +
                               "sat loose in the scene."),
                _fixMaterials);
            _adopt = EditorGUILayout.ToggleLeft(
                new GUIContent("Also adopt hand-placed ones",
                               "Sweeps the rest of the scene for loose ducks and fish and pulls them in. " +
                               "Off by default: reading animals out of the scene is what used to take a " +
                               "duck apart, and the FBX is a better source than the scene ever was."),
                _adopt);

            EditorGUILayout.Space();
            DrawModelStatus();
            DrawBrokenCheck();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_target == null))
            {
                if (GUILayout.Button("Spawn / Adjust", GUILayout.Height(30f)))
                {
                    Spawn(_target, _ducks, _fish, _lilypads, _replacePlaceholders, _resizeEverything,
                          _sizes, quiet: false, fixMaterials: _fixMaterials, adopt: _adopt);
                    Rescan();
                }

                if (GUILayout.Button(new GUIContent("Throw them all away and build again from the FBX",
                                                    "Nothing in the water survives. Use this when the " +
                                                    "population is in a state you would rather not " +
                                                    "reason about.")))
                {
                    Spawn(_target, _ducks, _fish, _lilypads, true, true, _sizes, quiet: false,
                          fixMaterials: _fixMaterials, adopt: _adopt, rebuildAll: true);
                    Rescan();
                }
            }

            if (_target == null)
                EditorGUILayout.HelpBox("No water body. Run Tools > NASA Sim > Water > Add Square Moat " +
                                        "Around Grass first.", MessageType.Warning);
        }

        /// <summary>
        /// Names what is actually wrong in the scene right now, rather than leaving it to be discovered in
        /// play mode. A count of "3 ducks" is no comfort when one of them is a bill.
        ///
        /// Drawn from a cache and never recomputed here: finding the strays walks every renderer in a
        /// nine-thousand-object scene three times over, and OnGUI runs on every mouse move.
        /// </summary>
        void DrawBrokenCheck()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool clean = _scanned && _brokenSummary.Length == 0 && _stranded.Count == 0;
                EditorGUILayout.LabelField(clean ? "Scene check: nothing broken." : "Scene check",
                                           EditorStyles.miniLabel);
                if (GUILayout.Button("Rescan", EditorStyles.miniButton, GUILayout.Width(70f))) Rescan();
            }
            if (!_scanned) return;

            if (_brokenSummary.Length > 0)
                EditorGUILayout.HelpBox(
                    "In the water now:\n" + _brokenSummary + "\nSpawn / Adjust replaces every one of them " +
                    "with a whole one from the FBX.", MessageType.Warning);

            _stranded.RemoveAll(t => t == null);
            if (_stranded.Count == 0) return;

            var names = new StringBuilder();
            for (int i = 0; i < _stranded.Count && i < 6; i++) names.Append("\n  ").Append(_stranded[i].name);
            if (_stranded.Count > 6) names.Append($"\n  …and {_stranded.Count - 6} more");

            EditorGUILayout.HelpBox(
                $"{_stranded.Count} piece(s) of these models are sitting loose in the scene, outside any " +
                "water — the parts an older version of this tool took off a model and left behind." + names,
                MessageType.Warning);
            if (GUILayout.Button("Delete the leftover pieces"))
            {
                Undo.SetCurrentGroupName("Delete Leftover Wildlife Pieces");
                int group = Undo.GetCurrentGroup();
                int n = 0;
                foreach (Transform t in _stranded)
                    if (t != null) { Undo.DestroyObjectImmediate(t.gameObject); n++; }
                Undo.CollapseUndoOperations(group);
                EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
                Debug.Log($"[Wildlife] {n} leftover piece(s) deleted. The FBXs are untouched.");
                Rescan();
            }
        }

        void Rescan()
        {
            _scanned = true;
            _stranded = StrandedPieces();

            var sb = new StringBuilder();
            if (_target != null)
            {
                foreach (Kind k in new[] { Kind.Duck, Kind.Fish, Kind.Lilypad })
                {
                    if (LoadModel(k) == null) continue;
                    int broken = 0, total = 0;
                    HashSet<Mesh> meshes = ModelMeshes(k);
                    foreach (Transform c in _target.transform)
                    {
                        if (!c.name.StartsWith(Prefix(k))) continue;
                        total++;
                        if (!IsPlaceholder(c) && !IsWholeModel(c, meshes)) broken++;
                    }
                    if (broken > 0)
                        sb.AppendLine($"  {broken} of {total} {k}(s) are only part of the model.");
                }
            }
            _brokenSummary = sb.ToString();
        }

        /// <summary>
        /// Fragments of Duck/Fish/Lilypad lying about the scene outside any water body. A COMPLETE model
        /// somebody put somewhere on purpose is not a fragment and is never listed — only things that draw
        /// part of a model and no more.
        /// </summary>
        static List<Transform> StrandedPieces()
        {
            var result = new List<Transform>();
            foreach (Kind kind in new[] { Kind.Duck, Kind.Fish, Kind.Lilypad })
            {
                HashSet<Mesh> meshes = ModelMeshes(kind);
                if (meshes.Count == 0) continue;

                var roots = new HashSet<Transform>();
                foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
                {
                    if (r is ParticleSystemRenderer) continue;
                    if (r.GetComponentInParent<WaterBody>() != null) continue;
                    Mesh m = MeshOf(r);
                    if (m == null || !meshes.Contains(m)) continue;

                    Transform t = r.transform;
                    while (t.parent != null && t.parent.GetComponent<WaterBody>() == null
                                            && OnlyDraws(t.parent, meshes))
                        t = t.parent;
                    roots.Add(t);
                }

                foreach (Transform t in roots)
                    if (!IsWholeModel(t, meshes)) result.Add(t);
            }
            return result;
        }

        /// <summary>Is everything under here drawn from this one model?</summary>
        static bool OnlyDraws(Transform t, HashSet<Mesh> meshes)
        {
            var rends = t.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return false;
            foreach (Renderer r in rends)
            {
                if (r is ParticleSystemRenderer) continue;
                Mesh m = MeshOf(r);
                if (m == null || !meshes.Contains(m)) return false;
            }
            return true;
        }

        void DrawModelStatus()
        {
            EditorGUILayout.LabelField("Models", EditorStyles.boldLabel);
            foreach (Kind k in new[] { Kind.Duck, Kind.Fish, Kind.Lilypad })
            {
                GameObject model = LoadModel(k);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(12f);
                    var style = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = model != null ? new Color(0.45f, 0.75f, 0.45f)
                                                             : new Color(0.85f, 0.65f, 0.35f) }
                    };
                    EditorGUILayout.LabelField(
                        model != null ? $"{k}: {ModelPath(k)}"
                                      : $"{k}: not found — falling back to a primitive placeholder",
                        style);
                }
            }
        }

        // ------------------------------------------------------------------ entry points

        /// <summary>Programmatic default for the full-vision setup chain: the moat gets a full pond.</summary>
        public static void SpawnDefaults()
        {
            var moat = GameObject.Find("WATER_Moat");
            WaterBody body = moat != null ? moat.GetComponent<WaterBody>() : FindAnyObjectByType<WaterBody>();
            if (body == null)
            {
                Debug.LogWarning("[Wildlife] No WaterBody in the scene — run " +
                                 "Tools > NASA Sim > Water > Add Square Moat Around Grass first.");
                return;
            }
            Spawn(body, 3, 8, 6, true, false);
        }

        /// <summary>
        /// Kept for callers that predate lilypads. It leaves the lilypad count ALONE rather than
        /// defaulting it — an old call site asking for "3 ducks and 8 fish" should not quietly clear
        /// somebody's pads, nor invent six.
        /// </summary>
        public static void Spawn(WaterBody water, int ducks, int fish, bool quiet = false)
        {
            if (water == null) return;
            int pads = 0;
            foreach (Transform c in water.transform)
                if (c.name.StartsWith(Prefix(Kind.Lilypad))) pads++;
            Spawn(water, ducks, fish, pads, true, false, quiet);
        }

        /// <summary>Kept for callers that predate the size sliders. They get the defaults.</summary>
        public static void Spawn(WaterBody water, int ducks, int fish, int lilypads,
                                 bool replacePlaceholders, bool resizeEverything, bool quiet = false,
                                 bool fixMaterials = true) =>
            Spawn(water, ducks, fish, lilypads, replacePlaceholders, resizeEverything,
                  Sizes.Default, quiet, fixMaterials);

        public static void Spawn(WaterBody water, int ducks, int fish, int lilypads,
                                 bool replacePlaceholders, bool resizeEverything, Sizes sizes,
                                 bool quiet = false, bool fixMaterials = true, bool adopt = false,
                                 bool rebuildAll = false)
        {
            if (water == null) return;

            int adopted = 0;
            if (adopt)
                adopted = Adopt(water, Kind.Duck, fixMaterials, sizes)
                        + Adopt(water, Kind.Fish, fixMaterials, sizes)
                        + Adopt(water, Kind.Lilypad, fixMaterials, sizes);
            int removed = 0, added = 0, broken = 0;

            Adjust(water, Kind.Duck, ducks, replacePlaceholders, resizeEverything, sizes, rebuildAll,
                   ref removed, ref added, ref broken);
            Adjust(water, Kind.Fish, fish, replacePlaceholders, resizeEverything, sizes, rebuildAll,
                   ref removed, ref added, ref broken);
            Adjust(water, Kind.Lilypad, lilypads, replacePlaceholders, resizeEverything, sizes, rebuildAll,
                   ref removed, ref added, ref broken);

            water.SnapWildlifeInside();
            EditorSceneManager.MarkSceneDirty(water.gameObject.scene);

            if (quiet) return;
            Debug.Log($"[Wildlife] '{water.name}': {ducks} duck(s), {fish} fish, {lilypads} lilypad(s).\n" +
                      $"  {adopted} adopted from the scene, {added} built from the FBXs, {removed} removed" +
                      (broken > 0 ? $" — {broken} of those were only part of a model" : "") + ".\n" +
                      $"  Sized to {sizes.For(Kind.Duck):0.00} m duck, {sizes.For(Kind.Fish):0.00} m fish, " +
                      $"{sizes.For(Kind.Lilypad):0.00} m lilypad" +
                      (resizeEverything ? " — everything." : " — new and adopted ones only.") + "\n" +
                      "  Ducks and fish both take a pat — walk up and press E. Everything rides the wave " +
                      "the water shader is actually drawing, so they lean with the swell.", water);
        }

        // ------------------------------------------------------------------ adopt

        /// <summary>
        /// Pull hand-placed animals into the population. Anything already parented under A water body is
        /// left alone — including one that belongs to a DIFFERENT pond, which is somebody else's duck.
        /// </summary>
        static int Adopt(WaterBody water, Kind kind, bool fixMaterials, Sizes sizes)
        {
            GameObject asset = LoadModel(kind);
            HashSet<Mesh> meshes = ModelMeshes(kind);

            // Every mesh that belongs to this kind of model, wherever it is in the scene.
            var mine = new HashSet<Renderer>();
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                if (r is ParticleSystemRenderer) continue;
                if (r.GetComponentInParent<WaterBody>() != null) continue;      // already in some water
                if (BelongsTo(r, kind, meshes)) mine.Add(r);
            }
            if (mine.Count == 0) return 0;

            // Climb to the outermost object that contains NOTHING BUT this model. That stops at the
            // model's own root: one level higher is the group it was dropped into ("Moat Elements"),
            // which also holds the fish and the lilypad, so it fails the test and the climb ends.
            var roots = new HashSet<Transform>();
            foreach (Renderer r in mine)
            {
                Transform t = r.transform;
                while (t.parent != null && t.parent.GetComponent<WaterBody>() == null && PureModel(t.parent, mine))
                    t = t.parent;
                roots.Add(t);
            }

            // Drop anything that is merely a part of a bigger match.
            var outermost = new List<Transform>();
            foreach (Transform t in roots)
            {
                bool nested = false;
                foreach (Transform other in roots)
                    if (other != t && t.IsChildOf(other)) { nested = true; break; }
                if (!nested) outermost.Add(t);
            }

            foreach (Transform t in outermost)
            {
                if (fixMaterials) RestoreModelMaterials(t.gameObject, asset);

                // WRAPPED in a fresh root rather than having the scripts bolted straight onto it, so an
                // adopted animal ends up with exactly the same shape as a built one:
                //
                //     Duck_01  (WaterWanderer, PettableObject, AudioSource)
                //       ├ Duck_01_Model   <- the thing that was in the scene
                //       └ InteractTrigger
                //
                // That is what lets the resize scale the MODEL without dragging the trigger's radius
                // with it, and it means one code path handles both from here on.
                string name = NextFreeName(water, kind);
                var root = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(root, "Adopt Wildlife");
                root.transform.SetParent(water.transform, worldPositionStays: false);
                root.transform.SetPositionAndRotation(Seat(water, t.gameObject, kind),
                                                      Quaternion.Euler(0f, t.eulerAngles.y, 0f));

                Undo.SetTransformParent(t, root.transform, "Adopt Wildlife");
                t.name = name + "_Model";
                StripGameplay(t.gameObject);

                Wire(water, root, kind, resize: true, sizes);
            }
            return outermost.Count;
        }

        /// <summary>Where an adopted animal ends up: over the spot it was already standing on, on the water.</summary>
        static Vector3 Seat(WaterBody water, GameObject model, Kind kind)
        {
            Vector3 p = TryBounds(model, out Bounds b) ? b.center : model.transform.position;
            p.y = kind == Kind.Fish ? water.SurfaceY - 0.4f : water.SurfaceY;
            return p;
        }

        /// <summary>
        /// Take the gameplay off the model itself. Anything somebody already made pettable by hand
        /// carries its own PettableObject and InteractTrigger; left under the new root they would be a
        /// second, differently sized interactable competing for the same E press. Colliders go too — the
        /// root's trigger is the only one these want.
        /// </summary>
        static void StripGameplay(GameObject model)
        {
            foreach (Transform child in model.GetComponentsInChildren<Transform>(true))
            {
                if (child == null || child == model.transform) continue;   // destroying a parent nulls these
                if (child.name == "InteractTrigger") Undo.DestroyObjectImmediate(child.gameObject);
            }
            foreach (WaterWanderer w in model.GetComponentsInChildren<WaterWanderer>(true))
                if (w != null) Undo.DestroyObjectImmediate(w);
            foreach (PettableObject p in model.GetComponentsInChildren<PettableObject>(true))
                if (p != null) Undo.DestroyObjectImmediate(p);
            foreach (Collider c in model.GetComponentsInChildren<Collider>(true))
                if (c != null) Undo.DestroyObjectImmediate(c);
        }

        /// <summary>Is every mesh under this transform part of the same model?</summary>
        static bool PureModel(Transform t, HashSet<Renderer> mine)
        {
            var rends = t.GetComponentsInChildren<Renderer>(true);
            if (rends.Length == 0) return false;
            foreach (Renderer r in rends)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!mine.Contains(r)) return false;
            }
            return true;
        }

        /// <summary>
        /// Does this renderer belong to that model?
        ///
        /// <b>What it draws comes first, and this is the whole reason the duck used to arrive in
        /// pieces.</b> Nineteen of the twenty nodes in Duck.fbx carry the Maya namespace —
        /// "Duck:mallard_body" — but the bill is plain "mallard_bill", so a name test dropped it. The
        /// group above then contained one mesh that wasn't "ours", <see cref="PureModel"/> said no, the
        /// climb stopped dead at each individual limb, and the tool adopted a bill, a wing and a foot as
        /// three separate ducks. Matching on the mesh ASSET can't be fooled by a rename.
        ///
        /// The name test stays as a fallback for a mesh that was baked out of the FBX and no longer
        /// points at it.
        /// </summary>
        static bool BelongsTo(Renderer r, Kind kind, HashSet<Mesh> modelMeshes)
        {
            Mesh mesh = MeshOf(r);
            if (mesh != null && modelMeshes.Contains(mesh)) return true;

            string word = kind.ToString().ToLowerInvariant();
            int c = r.name.IndexOf(':');
            if (c > 0) return r.name.Substring(0, c).ToLowerInvariant().StartsWith(word);
            return BiodomeFixTools.Leaf(r.name).ToLowerInvariant().StartsWith(word);
        }

        static Mesh MeshOf(Renderer r)
        {
            if (r is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            var filter = r.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>
        /// Exactly the meshes a fresh instance of the FBX draws — taken off the asset's own renderers, not
        /// off every Mesh in the file, so it is a set an instance in the scene can be compared against.
        /// </summary>
        static HashSet<Mesh> ModelMeshes(Kind kind)
        {
            var set = new HashSet<Mesh>();
            GameObject asset = LoadModel(kind);
            if (asset == null) return set;
            foreach (Renderer r in asset.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = MeshOf(r);
                if (m != null) set.Add(m);
            }
            return set;
        }

        /// <summary>
        /// Is this the whole animal, or a piece of one?
        ///
        /// The test is the mesh set: a fresh Duck.fbx draws a known handful of meshes, and anything in the
        /// water claiming to be a duck has to draw all of them. A renamed node cannot fool it, a lone
        /// wing cannot pass it, and a duck somebody has scaled, rotated or re-materialled still does.
        /// </summary>
        static bool IsWholeModel(Transform root, HashSet<Mesh> modelMeshes)
        {
            // No FBX in the project: primitives are all there is, so nothing is judged broken.
            if (modelMeshes.Count == 0) return true;

            var have = new HashSet<Mesh>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                Mesh m = MeshOf(r);
                if (m != null) have.Add(m);
            }
            foreach (Mesh m in modelMeshes)
                if (!have.Contains(m)) return false;
            return true;
        }

        /// <summary>
        /// Put the FBX's own materials back on an instance, matched renderer-by-renderer on name.
        ///
        /// The fish in this scene arrived wearing Grass on its body and Fruit on its fins — a bulk
        /// recolour swept over it while it sat unparented in the scene. Freshly built instances never
        /// have the problem because they come straight off the asset; an adopted one does, so it gets
        /// the same treatment on the way in.
        /// </summary>
        static void RestoreModelMaterials(GameObject instance, GameObject modelAsset)
        {
            if (modelAsset == null) return;

            var byName = new Dictionary<string, Material[]>();
            foreach (Renderer r in modelAsset.GetComponentsInChildren<Renderer>(true))
                byName[r.name] = r.sharedMaterials;

            int fixedSlots = 0;
            foreach (Renderer r in instance.GetComponentsInChildren<Renderer>(true))
            {
                if (!byName.TryGetValue(r.name, out Material[] want)) continue;
                Material[] have = r.sharedMaterials;
                if (have.Length != want.Length) continue;

                bool differs = false;
                for (int i = 0; i < have.Length; i++) if (have[i] != want[i]) { differs = true; break; }
                if (!differs) continue;

                Undo.RecordObject(r, "Restore Model Materials");
                r.sharedMaterials = want;
                EditorUtility.SetDirty(r);
                fixedSlots += have.Length;
            }

            if (fixedSlots > 0)
                Debug.Log($"[Wildlife] '{instance.name}': {fixedSlots} material slot(s) put back to what " +
                          $"{modelAsset.name} ships with.", instance);
        }

        // ------------------------------------------------------------------ adjust

        static void Adjust(WaterBody water, Kind kind, int wanted, bool replacePlaceholders,
                           bool resizeEverything, Sizes sizes, bool rebuildAll,
                           ref int removed, ref int added, ref int broken)
        {
            var existing = new List<Transform>();
            foreach (Transform c in water.transform)
                if (c.name.StartsWith(Prefix(kind))) existing.Add(c);

            // Placeholders first, so shrinking a population never throws away a real model while a
            // primitive stand-in survives.
            existing.Sort((a, b) =>
            {
                int pa = IsPlaceholder(a) ? 0 : 1;
                int pb = IsPlaceholder(b) ? 0 : 1;
                return pa != pb ? pb.CompareTo(pa) : string.CompareOrdinal(a.name, b.name);
            });

            if (LoadModel(kind) != null)
            {
                // Everything that is not a whole animal goes, and the top-up at the bottom of this method
                // builds replacements straight off the FBX. A "duck" that is one wing is not a duck, and
                // no amount of re-wiring will make it one — the ONLY fix is to throw it away.
                HashSet<Mesh> meshes = ModelMeshes(kind);
                for (int i = existing.Count - 1; i >= 0; i--)
                {
                    bool placeholder = IsPlaceholder(existing[i]);
                    bool whole = !placeholder && IsWholeModel(existing[i], meshes);
                    if (!rebuildAll && whole) continue;
                    if (!rebuildAll && placeholder && !replacePlaceholders) continue;

                    if (!placeholder && !whole) broken++;
                    Undo.DestroyObjectImmediate(existing[i].gameObject);
                    existing.RemoveAt(i);
                    removed++;
                }
            }

            for (int i = existing.Count - 1; i >= wanted; i--)
            {
                Undo.DestroyObjectImmediate(existing[i].gameObject);
                existing.RemoveAt(i);
                removed++;
            }

            foreach (Transform t in existing) Wire(water, t.gameObject, kind, resizeEverything, sizes);

            for (int i = existing.Count; i < wanted; i++)
            {
                GameObject go = Build(water, kind, sizes);
                Wire(water, go, kind, resize: true, sizes);
                added++;
            }
        }

        static bool IsPlaceholder(Transform t) =>
            t.GetComponentInChildren<PlaceholderMarker>(true) != null;

        // ------------------------------------------------------------------ build

        static GameObject Build(WaterBody water, Kind kind, Sizes sizes)
        {
            var root = new GameObject(NextFreeName(water, kind));
            Undo.RegisterCreatedObjectUndo(root, "Spawn Wildlife");
            root.transform.SetParent(water.transform, worldPositionStays: false);
            root.transform.position = StartPoint(water, kind);
            root.transform.rotation = Quaternion.Euler(0f, Random.value * 360f, 0f);

            GameObject model = LoadModel(kind);
            if (model != null) AttachModel(root, model, kind, sizes);
            else AttachPlaceholder(root, kind);
            return root;
        }

        static Vector3 StartPoint(WaterBody water, Kind kind)
        {
            Vector3 p = water.RandomPointOnSurface(1f);
            if (kind == Kind.Fish) p.y = water.SurfaceY - 0.4f;
            return p;
        }

        static void AttachModel(GameObject root, GameObject model, Kind kind, Sizes sizes)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);
            instance.name = root.name + "_Model";
            instance.transform.SetParent(root.transform, worldPositionStays: false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // The root's trigger is the only collider these want; model geometry would fight the
            // wander code and be re-detected by the interaction sensor.
            foreach (Collider c in instance.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);

            Normalise(instance, kind, sizes);
        }

        /// <summary>
        /// Scale the model to a believable size and seat it on its parent's origin. Imported FBXs arrive
        /// at whatever unit the modeller worked in, so a duck can land at 40x or at 1/40th; matching a
        /// target length is the only thing that makes "3 ducks" look like three ducks.
        ///
        /// The seat height matters as much as the size. <see cref="WaterWanderer"/> puts the ROOT on the
        /// waterline, so whatever part of the model sits at the root's origin is what the water cuts
        /// through: a duck centred on its full bounds — head included — floats a third of the way under.
        /// </summary>
        static void Normalise(GameObject instance, Kind kind, Sizes sizes)
        {
            Transform t = instance.transform;
            if (!TryBounds(instance, out Bounds b)) return;

            float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            if (longest > 1e-5f) t.localScale *= sizes.For(kind) / longest;

            // Re-measure: the offset that seats it is only right at the final size.
            if (!TryBounds(instance, out Bounds after)) return;
            Vector3 origin = t.parent != null ? t.parent.position : Vector3.zero;
            Vector3 seat = new Vector3(after.center.x,
                                       after.min.y + after.size.y * WaterlineFraction(kind),
                                       after.center.z);
            t.position += origin - seat;
        }

        /// <summary>How far up the model the waterline should cut. A duck rides low; a fish is centred.</summary>
        static float WaterlineFraction(Kind kind) => kind switch
        {
            Kind.Duck => 0.35f,
            Kind.Lilypad => 0.5f,
            _ => 0.5f,
        };

        static void AttachPlaceholder(GameObject root, Kind kind)
        {
            var ph = new GameObject("PH_" + kind);
            ph.transform.SetParent(root.transform, worldPositionStays: false);
            var marker = ph.AddComponent<PlaceholderMarker>();
            marker.note = $"Import a {kind} model and re-run Tools > NASA Sim > Water > " +
                          "Spawn Ducks, Fish & Lilypads; the scripts on the parent stay put.";

            switch (kind)
            {
                case Kind.Duck:
                {
                    marker.category = PlaceholderMarker.Category.Duck;
                    Material body = SceneBootstrap.MakeMat("Assets/_Project/Materials/DuckBody.mat",
                        "Universal Render Pipeline/Lit", new Color(0.95f, 0.92f, 0.78f));
                    Material beak = SceneBootstrap.MakeMat("Assets/_Project/Materials/DuckBeak.mat",
                        "Universal Render Pipeline/Lit", new Color(0.95f, 0.55f, 0.12f));
                    Prim(PrimitiveType.Sphere, "Body", ph.transform, body,
                         new Vector3(0f, 0.12f, 0f), new Vector3(0.45f, 0.32f, 0.55f));
                    Prim(PrimitiveType.Sphere, "Head", ph.transform, body,
                         new Vector3(0f, 0.34f, 0.22f), new Vector3(0.2f, 0.2f, 0.2f));
                    Prim(PrimitiveType.Cube, "Beak", ph.transform, beak,
                         new Vector3(0f, 0.33f, 0.36f), new Vector3(0.08f, 0.04f, 0.12f));
                    break;
                }

                case Kind.Fish:
                {
                    marker.category = PlaceholderMarker.Category.Fish;
                    Material fish = SceneBootstrap.MakeMat("Assets/_Project/Materials/FishBody.mat",
                        "Universal Render Pipeline/Lit", new Color(0.9f, 0.42f, 0.15f));
                    // The capsule's long axis is Y; pitch it 90 so the fish lies along its swim
                    // direction (+Z).
                    GameObject body = Prim(PrimitiveType.Capsule, "Body", ph.transform, fish,
                                           Vector3.zero, new Vector3(0.12f, 0.16f, 0.12f));
                    body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    Prim(PrimitiveType.Cube, "Tail", ph.transform, fish,
                         new Vector3(0f, 0f, -0.2f), new Vector3(0.02f, 0.12f, 0.1f));
                    break;
                }

                default:
                {
                    marker.category = PlaceholderMarker.Category.Generic;
                    Material pad = SceneBootstrap.MakeMat("Assets/_Project/Materials/Lilypad.mat",
                        "Universal Render Pipeline/Lit", new Color(0.18f, 0.42f, 0.16f));
                    Prim(PrimitiveType.Cylinder, "Pad", ph.transform, pad,
                         Vector3.zero, new Vector3(0.8f, 0.01f, 0.8f));
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ wire

        /// <summary>
        /// Put the gameplay components on the root, whatever the visual under it turns out to be. Safe to
        /// re-run: everything is find-or-add, and settings a designer has already tuned are not stamped
        /// back over.
        /// </summary>
        static void Wire(WaterBody water, GameObject root, Kind kind, bool resize, Sizes sizes)
        {
            Undo.RegisterFullObjectHierarchyUndo(root, "Wire Wildlife");

            // BEFORE the trigger is built, not after: the trigger's radius is measured off the model's
            // bounds, so sizing it first and measuring second is the difference between a prompt that
            // hugs the duck and one sized for the duck it used to be.
            if (resize)
            {
                Transform model = FindModelChild(root);
                if (model != null) Normalise(model.gameObject, kind, sizes);
            }

            // The per-kind numbers are written ONLY when the component is new. Re-running the tool to
            // change a count must not throw away a swim speed somebody spent an afternoon tuning; the
            // wiring (which water, which mode) is always refreshed, because that is what can go stale.
            WaterWanderer wander = GetOrAdd<WaterWanderer>(root, out bool freshWander);
            wander.water = water;
            switch (kind)
            {
                case Kind.Duck:
                    wander.mode = WaterWanderer.Mode.Duck;
                    break;
                case Kind.Fish:
                    wander.mode = WaterWanderer.Mode.Fish;
                    if (freshWander)
                    {
                        wander.speed = 0.9f;
                        wander.turnRateDeg = 160f;
                        wander.idlePauseRange = new Vector2(0.3f, 1.2f);
                    }
                    break;
                default:
                    wander.mode = WaterWanderer.Mode.Lilypad;
                    if (freshWander)
                    {
                        // A pad picked up by the wave should not also be shoved around by the bob.
                        wander.bobAmplitude = 0.004f;
                        wander.floatDepth = 0f;
                    }
                    break;
            }
            EditorUtility.SetDirty(wander);

            if (kind != Kind.Lilypad)
            {
                AudioSource audio = GetOrAdd<AudioSource>(root, out bool freshAudio);
                if (freshAudio)
                {
                    audio.playOnAwake = false;
                    audio.spatialBlend = 1f;
                }
                wander.audioSource = audio;

                PettableObject pet = GetOrAdd<PettableObject>(root, out _);
                pet.wanderer = wander;
                pet.audioSource = audio;
                if (string.IsNullOrEmpty(pet.label)) pet.label = kind.ToString().ToLowerInvariant();
                EditorUtility.SetDirty(pet);

                BuildTrigger(root, kind, sizes);
            }
        }

        /// <summary>
        /// The trigger lives on a CHILD whose scale cancels the parent's, so its radius is honest metres
        /// however the model was imported. The sensor walks up from the collider to find the
        /// interactable, so the root's PettableObject is still what answers.
        /// </summary>
        static void BuildTrigger(GameObject root, Kind kind, Sizes sizes)
        {
            Transform existing = root.transform.Find("InteractTrigger");
            GameObject trigger;
            if (existing != null) trigger = existing.gameObject;
            else
            {
                trigger = new GameObject("InteractTrigger");
                Undo.RegisterCreatedObjectUndo(trigger, "Wire Wildlife");
                Undo.SetTransformParent(trigger.transform, root.transform, "Wire Wildlife");
            }

            trigger.layer = NasaLayers.Interactable;
            trigger.transform.localRotation = Quaternion.identity;
            Vector3 ls = root.transform.lossyScale;
            trigger.transform.localScale = new Vector3(1f / NonZero(ls.x), 1f / NonZero(ls.y), 1f / NonZero(ls.z));

            float radius = sizes.For(kind) * 0.8f;
            if (TryBounds(root, out Bounds b))
            {
                trigger.transform.position = b.center;
                radius = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) + 0.45f;
            }
            else
            {
                trigger.transform.localPosition = Vector3.zero;
            }

            SphereCollider col = GetOrAdd<SphereCollider>(trigger, out _);
            col.isTrigger = true;
            col.center = Vector3.zero;
            // A fish is small and underwater; a prompt you cannot reach is worse than a generous one.
            col.radius = Mathf.Max(kind == Kind.Fish ? 0.6f : 0.4f, radius);
            EditorUtility.SetDirty(col);
        }

        // ------------------------------------------------------------------ helpers

        static string Prefix(Kind kind) => kind + "_";

        static string ModelPath(Kind kind) => $"Assets/_Project/Models/{kind}.fbx";

        static GameObject LoadModel(Kind kind) =>
            AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath(kind));

        static string NextFreeName(WaterBody water, Kind kind)
        {
            for (int i = 1; i < 500; i++)
            {
                string name = $"{Prefix(kind)}{i:00}";
                if (water.transform.Find(name) == null) return name;
            }
            return Prefix(kind) + "XX";
        }

        static Transform FindModelChild(GameObject root)
        {
            foreach (Transform child in root.transform)
            {
                if (child.name == "InteractTrigger") continue;
                if (child.GetComponentInChildren<Renderer>(true) != null) return child;
            }
            return null;
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Material mat,
                               Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;
            SceneBootstrap.SetMaterial(go, mat);
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);   // the root's trigger is the only collider
            return go;
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

        static float NonZero(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

        /// <summary><paramref name="fresh"/> is true when the component had to be added — which is the
        /// only time it is safe to stamp defaults over its fields.</summary>
        static T GetOrAdd<T>(GameObject go, out bool fresh) where T : Component
        {
            var c = go.GetComponent<T>();
            fresh = c == null;
            return fresh ? Undo.AddComponent<T>(go) : c;
        }
    }
}
