using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// One-click assembly of the Milestone-1 test scene using primitive stand-ins wired entirely by
    /// Transform reference — swap in the real FBX later with no code changes. Handles the fiddly bits
    /// (kinematic Rigidbody + convex MeshCollider, wheel axle orientation, the flat-trail visual, CSV wiring).
    /// </summary>
    public static class SceneBootstrap
    {
        const string ScenePath = "Assets/_Project/Scenes/NasaLogoSim.unity";
        const string CsvPath = "Assets/_Project/Data/nasa_logo_clean.csv";
        const string MatDir = "Assets/_Project/Materials";

        [MenuItem("Tools/NASA Sim/Build Test Scene")]
        public static void BuildTestScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            Material grassMat = MakeMat($"{MatDir}/Grass.mat", "Universal Render Pipeline/Lit", new Color(0.20f, 0.42f, 0.16f));
            Material mowedMat = MakeMat($"{MatDir}/MowedGrass.mat", "Universal Render Pipeline/Unlit", new Color(0.86f, 0.78f, 0.52f));
            if (mowedMat.HasProperty("_Cull")) mowedMat.SetFloat("_Cull", 0f);   // double-sided: a flat trail ribbon must show from above
            Material bodyMat  = MakeMat($"{MatDir}/TractorBody.mat", "Universal Render Pipeline/Lit", new Color(0.85f, 0.16f, 0.16f));
            Material wheelMat = MakeMat($"{MatDir}/Wheel.mat", "Universal Render Pipeline/Lit", new Color(0.12f, 0.12f, 0.13f));

            // ---- Environment ----
            var environment = new GameObject("Environment");
            var biodome = new GameObject("Biodome");
            biodome.transform.SetParent(environment.transform);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 10x10 units at scale 1
            floor.name = "Floor";
            floor.transform.SetParent(biodome.transform);
            floor.transform.localPosition = Vector3.zero;
            floor.transform.localScale = new Vector3(5f, 1f, 5f);          // 50x50 units for a 20-unit logo
            SetMaterial(floor, grassMat);
            // Floor keeps its default (non-convex, static) MeshCollider — correct for ground/biodome.

            // ---- Tractor: a UNIT-SCALE root carries the follower + kinematic Rigidbody, and a child cube is the
            // visible body. Keeping the root at scale (1,1,1) is essential: a non-uniform (2,1,3) scale on the
            // parent would stretch and displace every child (wheels, mower anchor). Children stay in true units.
            var tractor = new GameObject("Tractor");
            tractor.transform.position = new Vector3(0f, 0.9f, 0f);        // wheels (r=0.4) touch the floor at y=0

            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(tractor.transform, worldPositionStays: false);
            body.transform.localScale = new Vector3(2f, 1f, 3f);
            SetMaterial(body, bodyMat);
            Object.DestroyImmediate(body.GetComponent<BoxCollider>());
            var mc = body.AddComponent<MeshCollider>();                    // convex hull; part of the root's compound
            mc.convex = true;

            var rb = tractor.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // Wheels: flattened cubes; local X is the axle so Rotate(Vector3.right) rolls them.
            var driveWheels = new Transform[4];
            var wheelPos = new[]
            {
                new Vector3(-1.05f, -0.5f,  1.0f), // front-left
                new Vector3( 1.05f, -0.5f,  1.0f), // front-right
                new Vector3(-1.05f, -0.5f, -1.0f), // rear-left
                new Vector3( 1.05f, -0.5f, -1.0f), // rear-right
            };
            var wheelNames = new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" };
            for (int i = 0; i < 4; i++)
            {
                var w = GameObject.CreatePrimitive(PrimitiveType.Cube);
                w.name = wheelNames[i];
                w.transform.SetParent(tractor.transform, worldPositionStays: false);
                w.transform.localScale = new Vector3(0.25f, 0.8f, 0.8f);   // 0.8 diameter (radius 0.4), thin axle
                w.transform.localPosition = wheelPos[i];
                w.transform.localRotation = Quaternion.identity;
                SetMaterial(w, wheelMat);
                Object.DestroyImmediate(w.GetComponent<BoxCollider>());    // visual only for M1
                driveWheels[i] = w.transform;
            }

            // Mower anchor under the tractor pivot at floor level. Mid-mounting it on the pivot means the
            // mowed line traces the CSV waypoints EXACTLY (a far-rear brush lags and rounds off sharp
            // letter corners). Move it back on the real FBX if you want a trailing deck.
            var mowerAnchor = new GameObject("MowerAnchor");
            mowerAnchor.transform.SetParent(tractor.transform, worldPositionStays: false);
            mowerAnchor.transform.localPosition = new Vector3(0f, -0.9f, 0f); // world y ~ 0, on the pivot

            var mowerBrush = new GameObject("MowerBrush");
            mowerBrush.transform.SetParent(mowerAnchor.transform, worldPositionStays: false);
            mowerBrush.transform.localPosition = Vector3.zero;
            var trailVisual = mowerBrush.AddComponent<MowingVisual_Trail>();
            trailVisual.trailMaterial = mowedMat;
            trailVisual.width = 0.5f;   // fallback; SimulationManager auto-scales this to the logo on Awake
            var mowerController = mowerBrush.AddComponent<MowerController>();
            SetSerialized(mowerController, "visualBehaviour", trailVisual);

            var follower = tractor.AddComponent<TractorPathFollower>();
            follower.driveWheels = driveWheels;
            follower.steerWheels = new Transform[0];   // front-steer left to the real FBX later
            // These stand-in wheels are flattened cubes (0.25 x 0.8 x 0.8): the axle is the SHORTEST side,
            // so pin the axis explicitly rather than relying on the auto-longest default.
            follower.wheelSpinAxis = TractorPathFollower.WheelSpinAxis.X;
            follower.wheelRadius = 0.4f;
            follower.mower = mowerController;
            follower.mowerAnchor = mowerAnchor.transform;
            follower.fixedY = 0.9f;
            follower.moveSpeed = 6f;
            follower.onComplete = TractorPathFollower.EndBehavior.Stop;

            // ---- Waypoints holder + CSV ----
            var holder = new GameObject("WaypointsHolder");
            holder.transform.position = Vector3.zero;
            var loaderComp = holder.AddComponent<CsvWaypointLoader>();
            loaderComp.csvFile = AssetDatabase.LoadAssetAtPath<TextAsset>(CsvPath);
            loaderComp.penSource = CsvWaypointLoader.PenSource.FourthColumn;  // the clean CSV carries explicit lifts
            loaderComp.targetWorldSize = 40f;                                 // big logo; the tractor reads as small/natural
            if (loaderComp.csvFile == null)
                Debug.LogWarning($"[SceneBootstrap] No CSV at {CsvPath}. Drop your waypoints there and assign the loader.");
            follower.loader = loaderComp;

            // ---- Manager + camera ----
            var camGo = GameObject.Find("Main Camera");
            Camera cam = camGo != null ? camGo.GetComponent<Camera>() : Camera.main;
            if (cam != null)
            {
                cam.transform.position = new Vector3(0f, 18f, -18f);
                cam.transform.rotation = Quaternion.Euler(40f, 0f, 0f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.05f, 0.06f, 0.09f);
            }

            var managerGo = new GameObject("SimulationManager");
            var manager = managerGo.AddComponent<SimulationManager>();
            manager.loader = loaderComp;
            manager.tractor = follower;
            manager.targetCamera = cam;

            // ---- Astronaut, staircase, balcony (idempotent; also runnable on its own) ----
            AstronautSetup.AddToScene(logAtEnd: false);

            // ---- Save + register scene ----
            System.IO.Directory.CreateDirectory("Assets/_Project/Scenes");
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            var buildList = EditorBuildSettings.scenes.ToList();
            if (!buildList.Any(s => s.path == ScenePath))
            {
                buildList.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = buildList.ToArray();
            }

            Selection.activeGameObject = tractor;
            Debug.Log("[SceneBootstrap] Built NasaLogoSim. Green gizmo = mowed segments, red-dotted = pen-up gaps. Press Play to watch the tractor trace the logo.");
        }

        internal static Material MakeMat(string path, string shaderName, Color color)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SceneBootstrap] Shader '{shaderName}' not found; using a default material.");
                shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            }
            var m = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            System.IO.Directory.CreateDirectory(MatDir);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        internal static void SetMaterial(GameObject go, Material m)
        {
            var r = go.GetComponent<Renderer>();
            if (r != null && m != null) r.sharedMaterial = m;
        }

        // Assign a private [SerializeField] field via SerializedObject so the wiring persists in the scene.
        internal static void SetSerialized(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
