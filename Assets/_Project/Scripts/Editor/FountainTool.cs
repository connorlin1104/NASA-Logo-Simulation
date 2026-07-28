using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds a fountain: a stone basin with real water in it, jets that arc up and fall back, a sheet
    /// spilling off the tier, splash and ripples where it lands, and a haze over the whole thing.
    ///
    /// <b>The arcs are solved, not eyeballed.</b> Given how fast the water leaves the spout and how high
    /// the spout is above the pool, there is exactly one tilt that lands the stream at a given radius —
    /// so the tool solves for it and sets each jet's lifetime to the flight time it just computed. That
    /// is the whole difference between "water" and "sparks": droplets that follow a real parabola and die
    /// at the water line, instead of fading out in mid-air at whatever height the lifetime happened to be.
    ///
    /// <b>Stretched billboards do the rest.</b> A water droplet at speed is a streak, not a dot, so the
    /// jets and the spill render in Stretch mode scaled by velocity — fast water elongates, slow water
    /// at the top of the arc goes round. It costs nothing and it is most of why it reads as water.
    ///
    /// The pool is a real <see cref="WaterBody"/>, so it uses the project's own wave shader and its bank
    /// collider, and it can be resized afterwards from the Inspector like every other water body here.
    /// </summary>
    public sealed class FountainTool : EditorWindow
    {
        const string StoneMatPath = "Assets/_Project/Materials/FountainStone.mat";
        const string SprayMatPath = "Assets/_Project/Materials/WaterSpray.mat";
        const string WaterMatPath = "Assets/_Project/Materials/Water.mat";

        float _basinRadius = 2.2f;
        float _basinDepth = 0.35f;
        float _rimHeight = 0.55f;
        float _spoutHeight = 1.7f;
        int _ringJets = 6;
        bool _centreJet = true;
        float _jetSpeed = 3.2f;
        float _landAt = 0.62f;
        bool _tierBowl = true;
        bool _spill = true;
        bool _ripples = true;
        bool _mist = true;
        bool _glow = true;
        bool _interactive;
        Color _stone = new Color(0.70f, 0.68f, 0.63f);
        bool _atSelection;

        const float Gravity = 9.81f;

        [MenuItem("Tools/NASA Sim/Water/Make Fountain")]
        public static void Open()
        {
            var w = GetWindow<FountainTool>(true, "Make Fountain", true);
            w.minSize = new Vector2(460f, 620f);
        }

        // ------------------------------------------------------------------ GUI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Builds a fountain at the Scene view's pivot (or on whatever you have selected), dropped " +
                "onto whatever is underneath it.\n\n" +
                "Everything is live afterwards: the pool is a WaterBody, so its size is a number in the " +
                "Inspector, and the Fountain component's Flow drives every jet at once.", MessageType.Info);

            EditorGUILayout.LabelField("The basin", EditorStyles.boldLabel);
            _basinRadius = EditorGUILayout.Slider("Radius (m)", _basinRadius, 0.6f, 8f);
            _basinDepth = EditorGUILayout.Slider("Water depth (m)", _basinDepth, 0.1f, 1.2f);
            _rimHeight = EditorGUILayout.Slider(
                new GUIContent("Rim above ground (m)", "How high the waterline sits above the floor."),
                _rimHeight, 0f, 1.5f);
            _stone = EditorGUILayout.ColorField("Stone", _stone);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("The water", EditorStyles.boldLabel);
            _spoutHeight = EditorGUILayout.Slider(
                new GUIContent("Spout height (m)", "Above the waterline."), _spoutHeight, 0.3f, 6f);
            _jetSpeed = EditorGUILayout.Slider(
                new GUIContent("Jet speed (m/s)", "How hard it leaves the nozzle. Higher throws further " +
                               "and the tilt is solved to match."), _jetSpeed, 1f, 9f);
            _centreJet = EditorGUILayout.ToggleLeft(
                new GUIContent("Centre jet", "A single vertical plume up the middle."), _centreJet);
            _ringJets = EditorGUILayout.IntSlider(
                new GUIContent("Jets around it"), _ringJets, 0, 16);
            _landAt = EditorGUILayout.Slider(
                new GUIContent("Land at (fraction of radius)", "Where the arcs meet the water. 0.6 is a " +
                               "comfortable arc that clears the plinth and stays well inside the rim."),
                _landAt, 0.2f, 0.95f);

            DrawBallisticsReadout();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Extras", EditorStyles.boldLabel);
            _tierBowl = EditorGUILayout.ToggleLeft("Tier bowl", _tierBowl);
            _spill = EditorGUILayout.ToggleLeft(
                new GUIContent("Spill off the tier", "A sheet of water running off the bowl's edge."),
                _spill);
            _ripples = EditorGUILayout.ToggleLeft(
                new GUIContent("Ripples where it lands", "Flat rings that spread and fade on the surface."),
                _ripples);
            _mist = EditorGUILayout.ToggleLeft("Mist", _mist);
            _glow = EditorGUILayout.ToggleLeft(
                new GUIContent("Underwater light", "A soft point light in the basin."), _glow);
            _interactive = EditorGUILayout.ToggleLeft(
                new GUIContent("Turn it on and off with E",
                               "Adds a prompt and a trigger. Off by default — a fountain that just runs " +
                               "does not need a button in front of it."), _interactive);

            EditorGUILayout.Space();
            _atSelection = EditorGUILayout.ToggleLeft(
                new GUIContent("Put it at my selection", "Otherwise it goes at the Scene view's pivot — " +
                               "roughly the middle of what you are looking at."), _atSelection);

            EditorGUILayout.Space();
            if (GUILayout.Button("Make the fountain", GUILayout.Height(32f))) Build();
        }

        void DrawBallisticsReadout()
        {
            float want = _basinRadius * _landAt;
            float maxRange = HorizontalRange(_jetSpeed, _spoutHeight, 45f);
            if (maxRange < want)
            {
                EditorGUILayout.HelpBox(
                    $"At {_jetSpeed:0.#} m/s from {_spoutHeight:0.#} m up, the water can only reach " +
                    $"{maxRange:0.##} m — short of the {want:0.##} m you asked for. The jets will be " +
                    "built at 45°, their furthest throw. Raise the speed or lower the landing fraction.",
                    MessageType.Warning);
                return;
            }

            float tilt = SolveTilt(_jetSpeed, _spoutHeight, want);
            float flight = FlightTime(_jetSpeed, _spoutHeight, tilt);
            float apex = _spoutHeight + Mathf.Pow(_jetSpeed * Mathf.Cos(tilt * Mathf.Deg2Rad), 2f) / (2f * Gravity);
            EditorGUILayout.LabelField(
                " ", $"tilt {tilt:0.#}° from vertical · {flight:0.00} s in the air · peaks {apex:0.##} m " +
                     $"above the water · lands {want:0.##} m out",
                EditorStyles.wordWrappedMiniLabel);
        }

        // ------------------------------------------------------------------ ballistics

        /// <summary>
        /// How far out a droplet lands, launched at <paramref name="speed"/> tilted
        /// <paramref name="tiltDeg"/> off vertical from <paramref name="height"/> above the water.
        /// </summary>
        static float HorizontalRange(float speed, float height, float tiltDeg)
        {
            float r = tiltDeg * Mathf.Deg2Rad;
            float up = speed * Mathf.Cos(r);
            float across = speed * Mathf.Sin(r);
            return across * TimeToFall(up, height);
        }

        static float FlightTime(float speed, float height, float tiltDeg) =>
            TimeToFall(speed * Mathf.Cos(tiltDeg * Mathf.Deg2Rad), height);

        /// <summary>Time to come back down to a surface <paramml>height</paramml> below the launch.</summary>
        static float TimeToFall(float upSpeed, float height) =>
            (upSpeed + Mathf.Sqrt(upSpeed * upSpeed + 2f * Gravity * Mathf.Max(0f, height))) / Gravity;

        /// <summary>
        /// The tilt that lands the stream exactly where it was asked to. Range is monotonic in tilt over
        /// 0-45° (past 45 it starts falling off again), so a bisection converges without any calculus.
        /// </summary>
        static float SolveTilt(float speed, float height, float wantDistance)
        {
            float lo = 0f, hi = 45f;
            if (HorizontalRange(speed, height, hi) <= wantDistance) return hi;
            for (int i = 0; i < 24; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (HorizontalRange(speed, height, mid) < wantDistance) lo = mid;
                else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

        // ------------------------------------------------------------------ build

        void Build()
        {
            Vector3 ground = Ground(Where());

            var root = new GameObject(NextName());
            Undo.RegisterCreatedObjectUndo(root, "Make Fountain");
            // The WaterBody sits AT the waterline, so the root's Y is the water's surface height.
            root.transform.position = new Vector3(ground.x, ground.y + _rimHeight, ground.z);
            root.layer = NasaLayers.Water;

            Material stone = MakeStoneMaterial();
            Material spray = MakeSprayMaterial();

            BuildPool(root, stone);
            BuildStructure(root, stone, ground.y);

            var fountain = root.AddComponent<Fountain>();
            fountain.label = "fountain";
            fountain.interactable = _interactive;
            fountain.startsOn = true;

            var waterRoot = new GameObject("Water");
            waterRoot.transform.SetParent(root.transform, worldPositionStays: false);

            BuildJets(fountain, waterRoot.transform, spray);
            if (_spill && _tierBowl) BuildSpill(fountain, waterRoot.transform, spray);
            BuildSplash(fountain, waterRoot.transform, spray);
            if (_ripples) BuildRipples(fountain, waterRoot.transform, spray);
            if (_mist) BuildMist(fountain, waterRoot.transform, spray);
            if (_glow) BuildGlow(fountain, root.transform);

            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.loop = true;
            audio.spatialBlend = 1f;
            audio.minDistance = 2f;
            audio.maxDistance = 22f;
            fountain.audioSource = audio;

            if (_interactive) BuildTrigger(root);

            // The emitter list was filled after the component was added, so the Apply that ran on
            // OnEnable saw nothing. Without this the fountain is built and silent.
            fountain.Refresh();
            EditorUtility.SetDirty(fountain);

            EditorSceneManager.MarkSceneDirty(root.scene);
            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);

            float want = _basinRadius * _landAt;
            float tilt = SolveTilt(_jetSpeed, _spoutHeight, want);
            Debug.Log($"[Fountain] '{root.name}' built at {root.transform.position}.\n" +
                      $"  {_basinRadius:0.##} m basin, water {_rimHeight:0.##} m above the floor, " +
                      $"{_basinDepth:0.##} m deep.\n" +
                      $"  {(_centreJet ? 1 : 0) + _ringJets} jet(s) at {_jetSpeed:0.#} m/s, tilted " +
                      $"{tilt:0.#}° off vertical so they land {want:0.##} m out — " +
                      $"{FlightTime(_jetSpeed, _spoutHeight, tilt):0.00} s in the air.\n" +
                      "  Flow on the Fountain component drives the lot; drag it to 0 and it winds down " +
                      "over a second and a half. Resize the pool on the WaterBody like any other water " +
                      "here, then re-run this if you want the arcs re-solved to the new radius.",
                      root);
        }

        Vector3 Where()
        {
            if (_atSelection && Selection.activeTransform != null) return Selection.activeTransform.position;
            return SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
        }

        /// <summary>Drop it onto whatever is under it, so it never floats or buries itself.</summary>
        static Vector3 Ground(Vector3 near)
        {
            if (Physics.Raycast(near + Vector3.up * 40f, Vector3.down, out RaycastHit hit, 200f, ~0,
                                QueryTriggerInteraction.Ignore))
                return hit.point;
            return new Vector3(near.x, 0f, near.z);
        }

        static string NextName()
        {
            for (int i = 1; i < 100; i++)
                if (GameObject.Find($"FOUNTAIN_{i:00}") == null) return $"FOUNTAIN_{i:00}";
            return "FOUNTAIN_XX";
        }

        // ------------------------------------------------------------------ the pool

        WaterBody BuildPool(GameObject root, Material stone)
        {
            var mr = root.AddComponent<MeshRenderer>();
            mr.sharedMaterial = LoadWaterMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            root.AddComponent<MeshFilter>();
            root.AddComponent<WaterSurface>();

            var body = root.AddComponent<WaterBody>();
            body.shape = WaterBody.Shape.Circle;
            body.radius = _basinRadius;
            body.depth = _basinDepth;
            body.meshDensity = 3f;            // small pool, so a denser sheet costs nothing and waves better
            body.bankWidth = 0.22f;
            body.buildBasin = true;
            body.basinMaterial = stone;
            body.Rebuild();
            return body;
        }

        static Material LoadWaterMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(WaterMatPath);
            if (mat != null) return mat;

            Shader shader = Shader.Find("NasaSim/Water") ?? Shader.Find("Universal Render Pipeline/Lit");
            mat = new Material(shader) { name = "Water" };
            AssetDatabase.CreateAsset(mat, WaterMatPath);
            mat.renderQueue = BiodomeFixTools.WaterQueue;
            return mat;
        }

        // ------------------------------------------------------------------ the stonework

        Transform BuildStructure(GameObject root, Material stone, float groundY)
        {
            var structure = new GameObject("Structure");
            structure.transform.SetParent(root.transform, worldPositionStays: false);

            // A stone drum from the floor up to the basin's own floor. The WaterBody's generated basin is
            // a bowl whose wall slopes outward from there — with the stone rendering double-sided (see
            // MakeStoneMaterial) that slope is the visible outside of the fountain, and the drum closes
            // off everything below it. Without the drum you can see straight under the bowl.
            float floorY = -_basinDepth;                                  // local: the root IS the waterline
            float plinthBottom = groundY - root.transform.position.y;     // the ground, in local units
            float plinthHeight = floorY - plinthBottom;
            if (plinthHeight > 0.05f)
            {
                GameObject plinth = Cylinder("Plinth", structure.transform, stone,
                    new Vector3(0f, plinthBottom + plinthHeight * 0.5f, 0f),
                    (_basinRadius + 0.22f) * 2f, plinthHeight);
                SolidCollider(plinth);
            }

            // The column and the bowl it feeds.
            float columnHeight = _tierBowl ? _spoutHeight - 0.12f : _spoutHeight;
            GameObject column = Cylinder("Column", structure.transform, stone,
                new Vector3(0f, columnHeight * 0.5f, 0f), _basinRadius * 0.28f, columnHeight);
            // The default capsule collider is close enough for a tall thin column, and cheaper than a mesh.
            var cap = column.GetComponent<CapsuleCollider>();
            if (cap != null) cap.direction = 1;

            if (_tierBowl)
            {
                Cylinder("TierBowl", structure.transform, stone,
                         new Vector3(0f, _spoutHeight - 0.10f, 0f), _basinRadius * 0.85f, 0.09f);
                Cylinder("TierLip", structure.transform, stone,
                         new Vector3(0f, _spoutHeight - 0.04f, 0f), _basinRadius * 0.86f, 0.04f);
            }

            Cylinder("Spout", structure.transform, stone,
                     new Vector3(0f, _spoutHeight - 0.02f, 0f), 0.10f, 0.10f);
            return structure.transform;
        }

        static GameObject Cylinder(string name, Transform parent, Material mat, Vector3 localPos,
                                   float diameter, float height)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            // Unity's cylinder is 2 units tall and 1 across, so the Y scale is half the height.
            go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            SceneBootstrap.SetMaterial(go, mat);
            return go;
        }

        /// <summary>Swap a primitive's capsule collider for its own mesh — a squat drum is not a capsule.</summary>
        static void SolidCollider(GameObject go)
        {
            var cap = go.GetComponent<Collider>();
            if (cap != null) Object.DestroyImmediate(cap);
            var mc = go.AddComponent<MeshCollider>();
            mc.convex = false;
        }

        // ------------------------------------------------------------------ the water

        void BuildJets(Fountain fountain, Transform parent, Material spray)
        {
            float want = _basinRadius * _landAt;
            float tilt = Mathf.Min(45f, SolveTilt(_jetSpeed, _spoutHeight, want));
            float life = FlightTime(_jetSpeed, _spoutHeight, tilt);

            if (_centreJet)
            {
                // Straight up: it falls back down its own line, so its lifetime is the vertical case.
                ParticleSystem ps = Jet(parent, "Jet_Centre", Vector3.zero, Vector3.up, spray,
                                        FlightTime(_jetSpeed, _spoutHeight, 0f), _jetSpeed * 1.15f);
                fountain.emitters.Add(new Fountain.Emitter { system = ps, rateAtFullFlow = 110f });
            }

            for (int i = 0; i < _ringJets; i++)
            {
                float a = i * Mathf.PI * 2f / Mathf.Max(1, _ringJets);
                Vector3 outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 aim = Quaternion.AngleAxis(tilt, Vector3.Cross(Vector3.up, outward)) * Vector3.up;
                Vector3 nozzle = outward * 0.07f;
                ParticleSystem ps = Jet(parent, $"Jet_{i + 1:00}", nozzle, aim, spray, life, _jetSpeed);
                fountain.emitters.Add(new Fountain.Emitter { system = ps, rateAtFullFlow = 80f });
            }
        }

        ParticleSystem Jet(Transform parent, string name, Vector3 offset, Vector3 aim, Material spray,
                           float lifetime, float speed)
        {
            Vector3 pos = parent.TransformPoint(offset + Vector3.up * _spoutHeight);
            ParticleSystem ps = AirlockBuilderTool.NewParticleObject(
                parent, name, pos, Quaternion.LookRotation(aim.normalized, Vector3.up), spray);

            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = lifetime * 1.02f;       // a hair past the surface, so nothing pops out
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.9f, speed * 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.03f, 0.07f);
            main.gravityModifier = 1f;                   // the arc above is solved for real gravity
            main.maxParticles = 400;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.88f, 0.95f, 1f, 0.75f),
                                                               new Color(1f, 1f, 1f, 0.5f));

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 2.5f;                          // a stream, not a spray
            shape.radius = 0.012f;

            Streak(ps, 2.6f, 0.05f);
            FadeOut(ps, 0.55f);
            return ps;
        }

        void BuildSpill(Fountain fountain, Transform parent, Material spray)
        {
            float lip = _spoutHeight - 0.04f;
            ParticleSystem ps = AirlockBuilderTool.NewParticleObject(
                parent, "Spill", parent.TransformPoint(Vector3.up * lip),
                Quaternion.LookRotation(Vector3.down, Vector3.forward), spray);

            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = TimeToFall(0f, lip) * 1.05f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.05f);
            main.gravityModifier = 1f;
            main.maxParticles = 600;
            main.startColor = new Color(0.9f, 0.96f, 1f, 0.6f);

            // A ring exactly on the bowl's edge: thickness 0 means every droplet starts at the lip, which
            // is what makes it read as a sheet running over rather than a cloud under the bowl.
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = _basinRadius * 0.43f;
            shape.radiusThickness = 0f;
            shape.arc = 360f;

            Streak(ps, 2.2f, 0.06f);
            FadeOut(ps, 0.5f);
            fountain.emitters.Add(new Fountain.Emitter { system = ps, rateAtFullFlow = 160f });
        }

        void BuildSplash(Fountain fountain, Transform parent, Material spray)
        {
            float want = _basinRadius * _landAt;
            ParticleSystem ps = AirlockBuilderTool.NewParticleObject(
                parent, "Splash", parent.position, Quaternion.LookRotation(Vector3.up, Vector3.forward), spray);

            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.2f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
            main.gravityModifier = 1f;
            main.maxParticles = 300;
            main.startColor = new Color(1f, 1f, 1f, 0.55f);

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = want;
            shape.radiusThickness = 0.15f;
            shape.arc = 360f;

            Streak(ps, 1.6f, 0.04f);
            FadeOut(ps, 0.5f);
            fountain.emitters.Add(new Fountain.Emitter { system = ps, rateAtFullFlow = 70f });
        }

        void BuildRipples(Fountain fountain, Transform parent, Material spray)
        {
            ParticleSystem ps = AirlockBuilderTool.NewParticleObject(
                parent, "Ripples", parent.position + Vector3.up * 0.01f, Quaternion.identity, spray);

            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = 1.8f;
            main.startSpeed = 0f;
            main.startSize = 0.18f;
            main.gravityModifier = 0f;
            main.maxParticles = 60;
            main.startColor = new Color(1f, 1f, 1f, 0.22f);

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = _basinRadius * _landAt;
            shape.radiusThickness = 0.5f;

            // Flat to the water. A billboard ripple would stand up and face the camera, which is the one
            // thing a ripple must never do.
            var psr = ps.GetComponent<ParticleSystemRenderer>();
            psr.renderMode = ParticleSystemRenderMode.HorizontalBillboard;

            AirlockBuilderTool.SetGrowAndFade(ps, 0.4f, 5f);
            fountain.emitters.Add(new Fountain.Emitter { system = ps, rateAtFullFlow = 7f });
        }

        void BuildMist(Fountain fountain, Transform parent, Material spray)
        {
            ParticleSystem ps = AirlockBuilderTool.NewParticleObject(
                parent, "Mist", parent.position + Vector3.up * 0.3f, Quaternion.identity, spray);

            ParticleSystem.MainModule main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 4.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.25f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
            main.gravityModifier = -0.01f;               // barely rising, like spray hanging in the air
            main.maxParticles = 120;
            main.startColor = new Color(1f, 1f, 1f, 0.10f);

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = _basinRadius * 0.7f;

            AirlockBuilderTool.SetGrowAndFade(ps, 0.7f, 1.8f);
            fountain.emitters.Add(new Fountain.Emitter { system = ps, rateAtFullFlow = 6f });
        }

        void BuildGlow(Fountain fountain, Transform parent)
        {
            var go = new GameObject("Glow");
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, -_basinDepth * 0.5f, 0f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.72f, 0.9f, 1f);
            light.range = _basinRadius * 3.5f;
            light.intensity = 1.6f;
            light.shadows = LightShadows.None;           // a decorative fill light, not a shadow caster

            fountain.glow = light;
            fountain.glowIntensity = 1.6f;
        }

        static void BuildTrigger(GameObject root)
        {
            var trigger = new GameObject("InteractTrigger");
            trigger.transform.SetParent(root.transform, worldPositionStays: false);
            trigger.layer = NasaLayers.Interactable;
            trigger.transform.localPosition = Vector3.up * 0.6f;

            var col = trigger.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = 1.6f;
        }

        // ------------------------------------------------------------------ particle looks

        /// <summary>
        /// Stretch the billboard along the direction of travel and scale it by speed. Fast water becomes a
        /// streak and slow water at the top of an arc goes back to round, which is what separates a jet
        /// from a shower of dots.
        /// </summary>
        static void Streak(ParticleSystem ps, float lengthScale, float velocityScale)
        {
            var psr = ps.GetComponent<ParticleSystemRenderer>();
            psr.renderMode = ParticleSystemRenderMode.Stretch;
            psr.lengthScale = lengthScale;
            psr.velocityScale = velocityScale;
            psr.cameraVelocityScale = 0f;                // the camera moving must not stretch the water
        }

        static void FadeOut(ParticleSystem ps, float startAlpha)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(startAlpha, 0f),
                    new GradientAlphaKey(startAlpha, 0.7f),
                    new GradientAlphaKey(0f, 1f),
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }

        /// <summary>
        /// Stone, rendered on BOTH faces. The basin the WaterBody generates is a single surface — a floor
        /// and a wall sloping up and out — so its outside is a back face. Single-sided, that face is
        /// culled and you can see through the fountain from any angle below the rim.
        /// </summary>
        Material MakeStoneMaterial()
        {
            Material mat = SceneBootstrap.MakeMat(StoneMatPath, "Universal Render Pipeline/Lit", _stone);
            mat.SetColor("_BaseColor", _stone);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", _stone);
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.SetFloat("_Smoothness", 0.18f);
            mat.SetFloat("_Metallic", 0f);
            mat.doubleSidedGI = true;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
            return mat;
        }

        static Material MakeSprayMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(SprayMatPath);
            if (mat == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                                ?? Shader.Find("Universal Render Pipeline/Unlit");
                mat = new Material(shader) { name = "WaterSpray" };
                AssetDatabase.CreateAsset(mat, SprayMatPath);
            }

            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.SetColor("_BaseColor", new Color(0.92f, 0.97f, 1f, 1f));
            var soft = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
            if (soft != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", soft);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            // Above the water sheet (3000) so spray is never hidden by the pool it is falling into, and
            // below the dome glass (3100) so the dome still composites last.
            if (mat.HasProperty("_QueueOffset"))
                mat.SetFloat("_QueueOffset", BiodomeFixTools.GasQueue - 3000f);
            mat.renderQueue = BiodomeFixTools.GasQueue;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
            return mat;
        }
    }
}
