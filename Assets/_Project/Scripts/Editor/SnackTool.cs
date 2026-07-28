using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Puts exactly three things in the biodome worth eating on camera: one carrot, one lettuce, one
    /// apple.
    ///
    /// <b>Why three, and why built rather than found.</b> Every crop in SettingEnvo.fbx was decimated, and
    /// what survives that is fine in a bed and terrible in your hand. Worse, the plant the greenhouse
    /// calls a unit is not what you would call one: the node wired up as "an apple" is
    /// <c>appleArch10MeshGroup</c>, which is the whole trellis arch — so pressing E grabbed an entire arch,
    /// shrank it to nothing and ate it. Three purpose-built props sidestep both problems, and three is all
    /// a take needs.
    ///
    /// <b>They are the only eatable things left.</b> "Only these three" strips the EatableObject off
    /// everything else in the scene, so no arch, bed or shrub can be picked up by mistake mid-take.
    ///
    /// <b>The carrot is buried.</b> Its mesh puts y = 0 at the soil line with the whole root below it, so
    /// until it is pulled all you can see is the greens — and the pull actually brings something up out of
    /// the ground. See <see cref="EatableObject.pluckDirection"/> and
    /// <c>HandActionController.TickPluck</c>.
    /// </summary>
    public sealed class SnackTool : EditorWindow
    {
        public enum Kind { Carrot, Lettuce, Apple }

        const string SnacksRoot = "Snacks";
        const string MeshDir = "Assets/_Project/Data/Snacks";
        const string MatDir = "Assets/_Project/Materials/Snacks";

        // ------------------------------------------------------------------ state

        Transform _anchor;
        float _carrotLength = 0.165f;
        float _lettuceWidth = 0.150f;
        float _appleWidth = 0.115f;
        float _appleHeight = 1.30f;
        float _soilLift = 0.05f;
        float _reach = 1.0f;
        float _respawnSeconds = 25f;
        float _inspectSeconds = 0.4f;
        bool _onlyThese = true;
        bool _rebuildMeshes = true;

        /// <summary>
        /// Why E does nothing, answered in one click.
        ///
        /// Eating needs six things to line up at once — the object exists, it is active, it has an
        /// EatableObject, it has an enabled trigger on the Interactable layer, that trigger is inside the
        /// sensor's sphere, and the sensor's mask includes the layer. Any one of them missing produces the
        /// same symptom: you stand there pressing E and nothing happens. Checking them by hand across two
        /// Inspectors is miserable, so this prints all six per snack, with the actual numbers.
        /// </summary>
        [MenuItem("Tools/NASA Sim/Interactables/Why Can't I Eat It?")]
        public static void Diagnose()
        {
            var sb = new StringBuilder("[Snacks] Why can't I eat it?\n");

            var sensor = Object.FindAnyObjectByType<InteractionSensor>();
            Vector3 eye = Vector3.zero;
            float reach = 0f;
            if (sensor == null) sb.AppendLine("  NO InteractionSensor in the scene — nothing can be eaten at all.");
            else
            {
                Transform o = sensor.origin != null ? sensor.origin : sensor.transform;
                eye = o.position;
                reach = sensor.radius;
                bool maskOk = (sensor.interactableMask.value & (1 << NasaLayers.Interactable)) != 0;
                sb.AppendLine($"  Sensor on '{sensor.name}', measuring from '{o.name}' at " +
                              $"({eye.x:0.0}, {eye.y:0.0}, {eye.z:0.0}), radius {reach:0.00} m.");
                sb.AppendLine(maskOk
                    ? "  Mask includes the Interactable layer. OK."
                    : "  MASK DOES NOT INCLUDE LAYER 9 — run Setup > Configure Layers & Physics.");
                if (sensor.GetComponentInParent<HandActionController>() == null)
                    sb.AppendLine("  NO HandActionController on the astronaut — EatableObject.CanInteract " +
                                  "always returns false, so every snack is inert.");
            }

            var root = GameObject.Find(SnacksRoot);
            if (root == null)
                sb.AppendLine($"  NO '{SnacksRoot}' object in the scene. The three snacks were never " +
                              "placed — press 'Build the three snacks'.");
            else
                foreach (Kind kind in new[] { Kind.Carrot, Kind.Lettuce, Kind.Apple })
                    Report(sb, root, kind, sensor, eye, reach);

            var others = Object.FindObjectsByType<EatableObject>(FindObjectsInactive.Include);
            sb.AppendLine($"  {others.Length} eatable object(s) in the scene in total.");
            Debug.Log(sb.ToString(), root);
        }

        static void Report(StringBuilder sb, GameObject root, Kind kind, InteractionSensor sensor,
                           Vector3 eye, float reach)
        {
            Transform t = root.transform.Find("Snack_" + kind);
            if (t == null) { sb.AppendLine($"  {kind,-8}  MISSING — not under '{SnacksRoot}'."); return; }

            var eat = t.GetComponent<EatableObject>();
            var col = t.GetComponentInChildren<SphereCollider>(true);
            var mr = t.GetComponent<MeshRenderer>();
            var mf = t.GetComponent<MeshFilter>();
            Vector3 p = t.position;

            sb.AppendLine($"  {kind,-8}  at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0})");
            sb.AppendLine($"      active={t.gameObject.activeInHierarchy}  " +
                          $"EatableObject={(eat != null ? (eat.enabled ? "yes" : "DISABLED") : "MISSING")}  " +
                          $"mesh={(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "MISSING")}  " +
                          $"renderer={(mr != null && mr.enabled ? "on" : "OFF")}");

            if (col == null) { sb.AppendLine("      NO trigger collider — the sensor can never see it."); return; }

            float worldRadius = col.radius * Mathf.Max(Mathf.Abs(col.transform.lossyScale.x),
                                Mathf.Max(Mathf.Abs(col.transform.lossyScale.y),
                                          Mathf.Abs(col.transform.lossyScale.z)));
            sb.AppendLine($"      trigger: layer={LayerMask.LayerToName(col.gameObject.layer)}" +
                          $"({col.gameObject.layer})  isTrigger={col.isTrigger}  " +
                          $"enabled={col.enabled}  radius={worldRadius:0.00} m world");

            if (col.gameObject.layer != NasaLayers.Interactable)
                sb.AppendLine($"      WRONG LAYER — must be {NasaLayers.Interactable} (Interactable).");

            if (sensor != null)
            {
                float d = Vector3.Distance(eye, col.transform.position + col.center);
                float need = reach + worldRadius;
                sb.AppendLine(d <= need
                    ? $"      In range from where the astronaut stands NOW ({d:0.0} m ≤ {need:0.0} m)."
                    : $"      {d:0.0} m from the astronaut — you must walk to within {need:0.0} m of it. " +
                      "That is expected unless you are already standing there.");
            }
        }

        [MenuItem("Tools/NASA Sim/Interactables/The Three Snacks (Carrot, Lettuce, Apple)")]
        public static void Open()
        {
            var w = GetWindow<SnackTool>(true, "The Three Snacks", true);
            w.minSize = new Vector2(470f, 560f);
            w.AutoAnchor();
        }

        /// <summary>
        /// Default to the POND, not the astronaut. The astronaut spawns a hundred metres up the tunnel, so
        /// measuring from there picked whichever beds happened to face that way — the far side of the
        /// greenhouse. The beds worth using are the ones you walk past on your way round the water.
        /// </summary>
        void AutoAnchor()
        {
            if (Selection.activeTransform != null) { _anchor = Selection.activeTransform; return; }

            var moat = GameObject.Find("WATER_Moat");
            if (moat != null) { _anchor = moat.transform; return; }

            var water = Object.FindAnyObjectByType<WaterBody>();
            if (water != null) { _anchor = water.transform; return; }

            var astronaut = Object.FindAnyObjectByType<AstronautController>();
            if (astronaut != null) _anchor = astronaut.transform;
        }

        // ------------------------------------------------------------------ GUI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Builds one carrot, one lettuce and one apple at proper close-up quality and drops each " +
                "one at the nearest bed of its own crop.\n\n" +
                "They are high-poly on purpose: these three are the only produce the camera ever gets " +
                "near, so they are the only three worth the polygons.", MessageType.Info);

            _anchor = (Transform)EditorGUILayout.ObjectField(
                new GUIContent("Nearest to", "Which bed of each crop gets the good one — whichever is " +
                               "closest to this. Defaults to the astronaut."),
                _anchor, typeof(Transform), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("How big (real metres)", EditorStyles.boldLabel);
            _carrotLength = EditorGUILayout.Slider(
                new GUIContent("Carrot root", "Soil line to tip. All of it starts buried."),
                _carrotLength, 0.08f, 0.35f);
            _lettuceWidth = EditorGUILayout.Slider(new GUIContent("Lettuce head"), _lettuceWidth, 0.08f, 0.35f);
            _appleWidth = EditorGUILayout.Slider(new GUIContent("Apple"), _appleWidth, 0.04f, 0.20f);
            _appleHeight = EditorGUILayout.Slider(
                new GUIContent("Apple hangs at", "Height above the soil. The apple is placed in the middle " +
                               "of the arch rather than beside it, so this is the one number that decides " +
                               "whether it sits among the arch's own apples."), _appleHeight, 0.4f, 2.4f);
            _soilLift = EditorGUILayout.Slider(
                new GUIContent("Carrot / lettuce sit up by (m)",
                               "Raises them off the bed's own bottom. The carrot's root is modelled below " +
                               "the soil line on purpose, so this is how much of it shows before you pull " +
                               "it — 0 buries the crown."), _soilLift, 0f, 0.3f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("How it plays", EditorStyles.boldLabel);
            _reach = EditorGUILayout.Slider(
                new GUIContent("Reach (m)", "How close you have to stand. Generous, because a carrot top " +
                               "is a small thing to walk into."), _reach, 0.3f, 3f);
            _inspectSeconds = EditorGUILayout.Slider(
                new GUIContent("Hold it up for (s)", "After the pull and before the first bite it is held " +
                               "near the visor, turning. A glance, not a display — long enough to see what was " +
                               "picked, short enough not to hold the player up."), _inspectSeconds, 0f, 4f);
            _respawnSeconds = EditorGUILayout.Slider(
                new GUIContent("Grows back after (s)", "Real seconds. Enough to reset between takes " +
                               "without reloading the scene."), _respawnSeconds, 3f, 180f);

            EditorGUILayout.Space();
            _rebuildMeshes = EditorGUILayout.ToggleLeft(
                new GUIContent("Rebuild the meshes and skins",
                               "Off reuses what is already in " + MeshDir + " — faster, and it keeps any " +
                               "material tweaks you have made."), _rebuildMeshes);
            _onlyThese = EditorGUILayout.ToggleLeft(
                new GUIContent("Only these three are eatable",
                               "Takes EatableObject off everything else in the scene, so nothing else " +
                               "can be picked up mid-take. This is the fix for eating a whole apple arch."),
                _onlyThese);

            int others = CountOtherEatables();
            if (others > 0)
                EditorGUILayout.HelpBox(
                    _onlyThese
                        ? $"{others} other eatable object(s) will be turned back into scenery."
                        : $"{others} other eatable object(s) will be left wired — including the apple " +
                          "arches, which eat the whole arch.",
                    _onlyThese ? MessageType.None : MessageType.Warning);

            EditorGUILayout.Space();
            if (GUILayout.Button("Build the three snacks", GUILayout.Height(32f))) Build();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(GameObject.Find(SnacksRoot) == null))
            {
                if (GUILayout.Button("Select them")) SelectSnacks();
                if (GUILayout.Button("Remove them again")) RemoveSnacks();
            }
        }

        // ------------------------------------------------------------------ build

        void Build()
        {
            Directory.CreateDirectory(MeshDir);
            Directory.CreateDirectory(MatDir);

            Undo.SetCurrentGroupName("Build The Three Snacks");
            int group = Undo.GetCurrentGroup();

            try
            {
                EditorUtility.DisplayProgressBar("The Three Snacks", "Building meshes…", 0.1f);
                Mesh carrot = MeshAsset(Kind.Carrot);
                Mesh lettuce = MeshAsset(Kind.Lettuce);
                Mesh apple = MeshAsset(Kind.Apple);

                EditorUtility.DisplayProgressBar("The Three Snacks", "Skins and materials…", 0.6f);
                Material[] carrotMats = { Mat(Kind.Carrot, 0), Mat(Kind.Carrot, 1) };
                Material[] lettuceMats = { Mat(Kind.Lettuce, 0), Mat(Kind.Lettuce, 1) };
                Material[] appleMats = { Mat(Kind.Apple, 0), Mat(Kind.Apple, 1), Mat(Kind.Apple, 2) };

                EditorUtility.DisplayProgressBar("The Three Snacks", "Placing them…", 0.85f);
                Transform root = Root();
                var report = new StringBuilder("[Snacks] Three things worth eating:\n");
                Place(root, Kind.Carrot, carrot, carrotMats, report);
                Place(root, Kind.Lettuce, lettuce, lettuceMats, report);
                Place(root, Kind.Apple, apple, appleMats, report);

                int stripped = _onlyThese ? StripOtherEatables(root) : 0;
                if (stripped > 0)
                    report.AppendLine($"  {stripped} other eatable object(s) are scenery again — nothing " +
                                      "else in the scene answers [E] Eat.");
                report.AppendLine("  Walk up and press E: the hand reaches, strains, pulls it free, holds " +
                                  $"it up for {_inspectSeconds:0.0} s turning, then eats it.");

                EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
                Debug.Log(report.ToString(), root);
                Selection.activeTransform = root;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Undo.CollapseUndoOperations(group);
            }
        }

        Transform Root()
        {
            var existing = GameObject.Find(SnacksRoot);
            if (existing != null) return existing.transform;
            var go = new GameObject(SnacksRoot);
            Undo.RegisterCreatedObjectUndo(go, "Build The Three Snacks");
            // Deliberately at the scene root and never under the greenhouse: SettingEnvo is imported at
            // 2.808x, and a snack parented into it would silently be nearly three times the size the
            // sliders above say it is.
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        // ------------------------------------------------------------------ assets

        static string MeshPath(Kind kind) => $"{MeshDir}/Snack_{kind}.asset";

        Mesh MeshAsset(Kind kind)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath(kind));
            if (existing != null && !_rebuildMeshes) return existing;

            Mesh built = kind switch
            {
                Kind.Carrot => SnackMeshBuilder.Carrot(_carrotLength),
                Kind.Lettuce => SnackMeshBuilder.Lettuce(_lettuceWidth),
                _ => SnackMeshBuilder.Apple(_appleWidth),
            };

            if (existing == null)
            {
                AssetDatabase.CreateAsset(built, MeshPath(kind));
                return built;
            }

            // Overwrite in place rather than creating a new asset, so every renderer already pointing at
            // this mesh follows the change instead of being left on an orphan.
            existing.Clear();
            existing.indexFormat = built.indexFormat;
            existing.SetVertices(built.vertices);
            existing.SetNormals(built.normals);
            existing.SetUVs(0, new List<Vector2>(built.uv));
            existing.subMeshCount = built.subMeshCount;
            for (int i = 0; i < built.subMeshCount; i++) existing.SetTriangles(built.GetTriangles(i), i);
            existing.RecalculateTangents();
            existing.RecalculateBounds();
            Object.DestroyImmediate(built);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        /// <summary>The materials, in submesh order. Names match <see cref="SnackMeshBuilder"/>'s constants.</summary>
        Material Mat(Kind kind, int slot)
        {
            switch (kind)
            {
                case Kind.Carrot:
                    return slot == SnackMeshBuilder.CarrotRoot
                        ? Textured("Snack_CarrotSkin", SnackMeshBuilder.CarrotSkin_Texture, 0.30f, 0.02f)
                        : Leaf();
                case Kind.Lettuce:
                    return slot == SnackMeshBuilder.LettuceLeaf
                        ? Textured("Snack_LettuceLeaf", SnackMeshBuilder.LettuceSkin_Texture, 0.44f, 0.03f)
                        : Flat("Snack_LettuceCore", new Color(0.86f, 0.88f, 0.66f), 0.28f);
                default:
                    if (slot == SnackMeshBuilder.AppleSkin)
                        // Glossy: the highlight sliding round the skin as it turns is most of what makes
                        // an apple look like an apple in a close-up.
                        return Textured("Snack_AppleSkin", SnackMeshBuilder.AppleSkin_Texture, 0.66f, 0.05f);
                    if (slot == SnackMeshBuilder.AppleStem)
                        return Flat("Snack_AppleStem", new Color(0.30f, 0.20f, 0.10f), 0.22f);
                    return Leaf();
            }
        }

        Material Leaf() => Flat("Snack_Leaf", new Color(0.26f, 0.47f, 0.15f), 0.40f);

        static Material Flat(string name, Color color, float smoothness)
        {
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(LitShader()) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            Set(m, color, smoothness, 0f, null);
            return m;
        }

        Material Textured(string name, System.Func<int, Texture2D> bake, float smoothness, float metallic)
        {
            string path = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(LitShader()) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            Set(m, Color.white, smoothness, metallic, SkinTexture(name, bake));
            return m;
        }

        /// <summary>
        /// Bake the skin to a PNG and let the normal importer handle it, rather than saving a Texture2D
        /// asset — that way it gets sRGB, mipmaps and compression like any other texture in the project,
        /// and it can be opened and looked at.
        /// </summary>
        Texture2D SkinTexture(string name, System.Func<int, Texture2D> bake)
        {
            string path = $"{MatDir}/{name}.png";
            if (!_rebuildMeshes)
            {
                var cached = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (cached != null) return cached;
            }

            Texture2D baked = bake(512);
            File.WriteAllBytes(path, baked.EncodeToPNG());
            Object.DestroyImmediate(baked);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 4;
                importer.maxTextureSize = 512;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static Shader LitShader() =>
            Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");

        static void Set(Material m, Color color, float smoothness, float metallic, Texture2D map)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (map != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", map);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", map);
            }
            EditorUtility.SetDirty(m);
        }

        // ------------------------------------------------------------------ placement

        void Place(Transform root, Kind kind, Mesh mesh, Material[] materials, StringBuilder report)
        {
            string name = "Snack_" + kind;
            Transform found = root.Find(name);
            GameObject go;
            if (found != null) go = found.gameObject;
            else
            {
                go = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(go, "Build The Three Snacks");
                go.transform.SetParent(root, worldPositionStays: false);
            }
            Undo.RegisterFullObjectHierarchyUndo(go, "Build The Three Snacks");

            Transform bed = NearestBed(kind, out Vector3 seat);
            go.transform.SetPositionAndRotation(seat, Quaternion.Euler(0f, Yaw(kind), 0f));
            go.transform.localScale = Vector3.one;

            var filter = GetOrAdd<MeshFilter>(go);
            filter.sharedMesh = mesh;
            var renderer = GetOrAdd<MeshRenderer>(go);
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            EditorUtility.SetDirty(renderer);

            WireEatable(go, kind);
            BuildTrigger(go, kind);

            report.AppendLine($"  {kind,-8} at ({seat.x:0.0}, {seat.y:0.0}, {seat.z:0.0})" +
                              (bed != null ? $"  — beside {bed.name}" : "  — no bed found, placed at the anchor"));
        }

        /// <summary>A little turn each, so three props built from the same maths do not line up.</summary>
        static float Yaw(Kind kind) => kind switch
        {
            Kind.Carrot => 34f,
            Kind.Lettuce => 197f,
            _ => 118f,
        };

        void WireEatable(GameObject go, Kind kind)
        {
            var eat = go.GetComponent<EatableObject>();
            if (eat == null) eat = Undo.AddComponent<EatableObject>(go);
            else Undo.RecordObject(eat, "Build The Three Snacks");

            eat.verb = "Pick";
            eat.label = kind.ToString().ToLowerInvariant();
            eat.body = null;                       // the whole prop travels; the trigger goes off with it
            eat.whenFinished = EatableObject.WhenFinished.Respawn;
            eat.respawnSeconds = _respawnSeconds;
            eat.inspectSeconds = _inspectSeconds;
            eat.pluck = true;

            switch (kind)
            {
                case Kind.Carrot:
                    eat.bites = 4;
                    eat.biteInterval = 0.55f;
                    // Straight up and far enough that the whole root clears the soil, plus a little, or it
                    // finishes the pull with the tip still in the ground.
                    eat.pluckDirection = Vector3.up;
                    eat.pluckDistance = _carrotLength * 1.15f;
                    eat.pluckSeconds = 0.42f;      // the one that should look like it takes effort
                    eat.debrisColor = new Color(0.93f, 0.50f, 0.13f, 1f);
                    eat.pluckDebrisColor = new Color(0.32f, 0.24f, 0.16f, 1f);   // soil
                    break;
                case Kind.Lettuce:
                    eat.bites = 3;
                    eat.biteInterval = 0.5f;
                    eat.pluckDirection = Vector3.up;
                    eat.pluckDistance = 0.07f;     // it only has to twist off its stump
                    eat.pluckSeconds = 0.32f;
                    eat.debrisColor = new Color(0.44f, 0.68f, 0.25f, 1f);
                    eat.pluckDebrisColor = new Color(0.36f, 0.55f, 0.20f, 1f);   // torn leaf
                    break;
                default:
                    eat.bites = 4;
                    eat.biteInterval = 0.5f;
                    // Down: an apple is picked by pulling it off the branch above it.
                    eat.pluckDirection = Vector3.down;
                    eat.pluckDistance = 0.055f;
                    eat.pluckSeconds = 0.28f;
                    eat.debrisColor = new Color(0.97f, 0.93f, 0.80f, 1f);        // white flesh
                    eat.pluckDebrisColor = new Color(0.42f, 0.55f, 0.20f, 1f);   // leaf
                    break;
            }
            EditorUtility.SetDirty(eat);
        }

        void BuildTrigger(GameObject go, Kind kind)
        {
            Transform existing = go.transform.Find("InteractTrigger");
            GameObject trigger;
            if (existing != null) trigger = existing.gameObject;
            else
            {
                trigger = new GameObject("InteractTrigger");
                Undo.RegisterCreatedObjectUndo(trigger, "Build The Three Snacks");
                Undo.SetTransformParent(trigger.transform, go.transform, "Build The Three Snacks");
            }

            trigger.layer = NasaLayers.Interactable;
            trigger.transform.localRotation = Quaternion.identity;
            trigger.transform.localScale = Vector3.one;      // the root is at identity, so metres are metres
            // Centred a little above the pivot: for the carrot the pivot is the soil line and the root is
            // below it, so a trigger on the pivot would sit half underground.
            trigger.transform.localPosition = new Vector3(0f, kind == Kind.Carrot ? 0.15f : 0.05f, 0f);

            var col = trigger.GetComponent<SphereCollider>();
            if (col == null) col = Undo.AddComponent<SphereCollider>(trigger);
            else Undo.RecordObject(col, "Build The Three Snacks");
            col.isTrigger = true;
            col.center = Vector3.zero;
            col.radius = _reach;
            EditorUtility.SetDirty(col);
        }

        // ------------------------------------------------------------------ finding a bed

        /// <summary>The name fragment the greenhouse import uses for each crop.</summary>
        static string BedToken(Kind kind) => kind switch
        {
            Kind.Carrot => "carrots",
            Kind.Lettuce => "lettuce",
            _ => "applearch",
        };

        /// <summary>
        /// The bed of this crop closest to the anchor, and where the snack should sit beside it.
        ///
        /// The seat is nudged out of the bed toward the anchor, because dropping a good carrot into the
        /// middle of a dense low-poly one is a fine way to hide it.
        /// </summary>
        Transform NearestBed(Kind kind, out Vector3 seat)
        {
            Vector3 from = _anchor != null ? _anchor.position : Vector3.zero;
            string token = BedToken(kind);

            Transform best = null;
            float bestSqr = float.MaxValue;
            Bounds bestBounds = default;

            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                if (t == null) continue;
                string lower = t.name.ToLowerInvariant();
                if (!lower.Contains("meshgroup") || !lower.Contains(token)) continue;
                if (!TryBounds(t.gameObject, out Bounds b)) continue;

                float sqr = (b.center - from).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr;
                best = t;
                bestBounds = b;
            }

            if (best == null)
            {
                seat = from + Vector3.forward;
                return null;
            }

            if (kind == Kind.Apple)
            {
                // Dead centre of the arch, not beside it. Offsetting the apple outward is what left it
                // hanging in the aisle next to the arch's own apples instead of among them; the only thing
                // that should decide where it sits is the height.
                seat = new Vector3(bestBounds.center.x, bestBounds.min.y + _appleHeight, bestBounds.center.z);
                return best;
            }

            Vector3 away = from - bestBounds.center;
            away.y = 0f;
            away = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;

            // Clamped at both ends: a bed can be metres wide, and clearing half of it would leave the
            // snack out in the aisle instead of standing in the soil it came from.
            float step = Mathf.Clamp(bestBounds.extents.x * 0.5f + 0.30f, 0.45f, 1.2f);
            Vector3 p = bestBounds.center + away * step;
            // Soil level comes from the bed's own bottom, so it lands on whatever the plants are standing
            // on rather than on a guessed y — then lifted, because that bottom is the lowest point of the
            // plant GEOMETRY and is usually a little under the visible surface.
            p.y = bestBounds.min.y + _soilLift;
            seat = p;
            return best;
        }

        // ------------------------------------------------------------------ the other 28

        int CountOtherEatables()
        {
            var root = GameObject.Find(SnacksRoot);
            int n = 0;
            foreach (EatableObject e in Object.FindObjectsByType<EatableObject>(FindObjectsInactive.Include))
                if (e != null && !IsOurs(e.transform, root != null ? root.transform : null)) n++;
            return n;
        }

        static bool IsOurs(Transform t, Transform root) => root != null && t.IsChildOf(root);

        /// <summary>
        /// Turn everything else back into scenery: the component goes, the trigger it was given goes, and
        /// the layer goes back — but only if nothing else on that object still wants to be interacted
        /// with, so a chair or a button that happens to also be eatable does not lose its collider.
        /// </summary>
        int StripOtherEatables(Transform root)
        {
            int n = 0;
            foreach (EatableObject e in Object.FindObjectsByType<EatableObject>(FindObjectsInactive.Include))
            {
                if (e == null || IsOurs(e.transform, root)) continue;
                GameObject go = e.gameObject;
                Undo.RecordObject(go, "Only These Three");
                Undo.DestroyObjectImmediate(e);

                if (go.GetComponent<IInteractable>() != null) { n++; continue; }

                foreach (Collider c in go.GetComponents<Collider>())
                    if (c != null && c.isTrigger) Undo.DestroyObjectImmediate(c);

                Transform trigger = go.transform.Find("InteractTrigger");
                if (trigger != null && trigger.GetComponent<Renderer>() == null)
                    Undo.DestroyObjectImmediate(trigger.gameObject);

                if (go.layer == NasaLayers.Interactable) go.layer = 0;
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ housekeeping

        void SelectSnacks()
        {
            var root = GameObject.Find(SnacksRoot);
            if (root == null) return;
            var picked = new List<Object>();
            foreach (Transform t in root.transform) picked.Add(t.gameObject);
            Selection.objects = picked.ToArray();
            SceneView.FrameLastActiveSceneView();
        }

        void RemoveSnacks()
        {
            var root = GameObject.Find(SnacksRoot);
            if (root == null) return;
            Undo.DestroyObjectImmediate(root);
            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            Debug.Log("[Snacks] Removed. The meshes and materials are still in " + MeshDir + " and " +
                      MatDir + " — re-run the tool to put them back.");
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : Undo.AddComponent<T>(go);
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
