using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds a working pressure chamber inside the biodome's COL_tunnel: two sliding doors at 25% and
    /// 75% along the tunnel, a chamber sensor between them, interact buttons on each approach, and gas
    /// vent particle systems for the pressurization — all wired into an <see cref="AirlockController"/>.
    ///
    /// Everything visual is a PH_ placeholder primitive with components on the parent roots, so modeled
    /// doors can be swapped in later (Tools &gt; NASA Sim &gt; Models &gt; Swap Placeholder With Selected
    /// FBX) with no re-wiring. Re-running rebuilds the generated Airlock root from scratch.
    /// </summary>
    public static class AirlockBuilderTool
    {
        const string GasMatPath = "Assets/_Project/Materials/AirlockGas.mat";
        const string DoorMatPath = "Assets/_Project/Materials/AirlockDoor.mat";
        const string ButtonMatPath = "Assets/_Project/Materials/AirlockButton.mat";

        [MenuItem("Tools/NASA Sim/Biodome/Build Airlock In Tunnel")]
        public static void BuildDefault()
        {
            Transform tunnel = FindTunnel();
            if (tunnel == null)
            {
                Debug.LogWarning("[Airlock] No COL_tunnel found in the scene. Drag Biodome.fbx in and run " +
                                 "Tools > NASA Sim > Biodome > Wire Colliders & Spawn Outside first.");
                return;
            }
            if (!TryRendererBounds(tunnel.gameObject, out Bounds tb) || tb.size.y < 0.5f)
            {
                Debug.LogWarning("[Airlock] COL_tunnel has no usable renderer bounds — cannot size the airlock.", tunnel);
                return;
            }

            // Tunnel axis = the longer horizontal side of its bounds (assumes a roughly axis-aligned
            // tunnel, which the imported biodome is); the outside end is farther from the dome's centre.
            Vector3 axis = tb.size.x >= tb.size.z ? Vector3.right : Vector3.forward;
            float halfLen = tb.size.x >= tb.size.z ? tb.extents.x : tb.extents.z;
            float width = tb.size.x >= tb.size.z ? tb.size.z : tb.size.x;
            float height = tb.size.y;

            TryRendererBounds(tunnel.root.gameObject, out Bounds shell);
            Vector3 outDir = FlatDistance(tb.center + axis * halfLen, shell.center) >=
                             FlatDistance(tb.center - axis * halfLen, shell.center) ? axis : -axis;

            // Generated content: rebuild from scratch each run.
            var old = GameObject.Find("Airlock");
            if (old != null) Object.DestroyImmediate(old);
            var rootGo = new GameObject("Airlock");
            Undo.RegisterCreatedObjectUndo(rootGo, "Build Airlock");

            Material doorMat = SceneBootstrap.MakeMat(DoorMatPath, "Universal Render Pipeline/Lit",
                                                      new Color(0.75f, 0.78f, 0.82f));
            Material buttonMat = SceneBootstrap.MakeMat(ButtonMatPath, "Universal Render Pipeline/Lit",
                                                        new Color(0.90f, 0.45f, 0.10f));

            // ---- Doors ----
            Vector3 pOuter = tb.center + outDir * (halfLen * 0.5f);
            Vector3 pInner = tb.center - outDir * (halfLen * 0.5f);
            SimpleDoor outerDoor = BuildDoor(rootGo.transform, "DOOR_Outer", pOuter, outDir, width, height,
                                             doorMat, PlaceholderMarker.Category.DoorOuter);
            SimpleDoor innerDoor = BuildDoor(rootGo.transform, "DOOR_Inner", pInner, outDir, width, height,
                                             doorMat, PlaceholderMarker.Category.DoorInner);

            // ---- Chamber sensor between the doors ----
            float gap = Vector3.Distance(pOuter, pInner);
            var chamberGo = new GameObject("Chamber");
            chamberGo.transform.SetParent(rootGo.transform, worldPositionStays: false);
            chamberGo.transform.SetPositionAndRotation(tb.center, Quaternion.LookRotation(outDir, Vector3.up));
            var box = chamberGo.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = new Vector3(width * 0.9f, height * 0.9f, gap * 0.9f);
            var chamberSensor = chamberGo.AddComponent<AirlockChamberSensor>();

            // ---- Buttons (trigger-only, Interactable layer) beside each door's approach ----
            Vector3 side = Vector3.Cross(Vector3.up, outDir);
            float buttonY = tb.min.y + 1.1f;
            AirlockButtonInteractable outerButton = BuildButton(rootGo.transform, "Button_Outer",
                new Vector3(pOuter.x, buttonY, pOuter.z) + outDir * 0.9f + side * (width * 0.42f),
                -outDir, buttonMat, isOuter: true);
            AirlockButtonInteractable innerButton = BuildButton(rootGo.transform, "Button_Inner",
                new Vector3(pInner.x, buttonY, pInner.z) - outDir * 0.9f + side * (width * 0.42f),
                outDir, buttonMat, isOuter: false);

            // ---- Gas vents: 4 ceiling jets aimed down-inward + 1 slow room fill ----
            var ventsRoot = new GameObject("GasVents");
            ventsRoot.transform.SetParent(rootGo.transform, worldPositionStays: false);
            Material gasMat = MakeGasMaterial();

            var vents = new ParticleSystem[5];
            int v = 0;
            for (int a = -1; a <= 1; a += 2)
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector3 pos = tb.center + outDir * (a * gap * 0.35f) + side * (s * width * 0.30f);
                    pos.y = tb.max.y - 0.25f;
                    Vector3 aim = (Vector3.down * 2f - side * s * 0.6f).normalized;   // down, angled inward
                    vents[v++] = BuildVent(ventsRoot.transform, $"Vent_{v}", pos, aim, gasMat);
                }
            }
            vents[4] = BuildRoomFill(ventsRoot.transform, tb.center + Vector3.down * (height * 0.35f),
                                     outDir, width, gap, gasMat);

            // ---- Controller ----
            var ctrl = rootGo.AddComponent<AirlockController>();
            ctrl.outerDoor = outerDoor;
            ctrl.innerDoor = innerDoor;
            ctrl.chamber = chamberSensor;
            ctrl.gasVents = vents;
            var audio = rootGo.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            ctrl.audioSource = audio;
            outerButton.airlock = ctrl;
            innerButton.airlock = ctrl;

            EditorSceneManager.MarkSceneDirty(rootGo.scene);
            Selection.activeGameObject = rootGo;
            Debug.Log($"[Airlock] Built in COL_tunnel: doors {gap:0.0} m apart, tunnel {width:0.0} m wide x " +
                      $"{height:0.0} m tall. Walk up, press E at the orange button, step inside; the rest " +
                      "is automatic. Placeholder panels swap for modeled doors later.", rootGo);
        }

        // ------------------------------------------------------------------ parts

        static SimpleDoor BuildDoor(Transform parent, string name, Vector3 pos, Vector3 outDir,
                                    float width, float height, Material mat, PlaceholderMarker.Category cat)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, worldPositionStays: false);
            root.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(outDir, Vector3.up));

            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.name = "PH_" + name;
            panel.transform.SetParent(root.transform, worldPositionStays: false);
            panel.transform.localScale = new Vector3(width * 1.05f, height * 1.02f, 0.15f);
            SceneBootstrap.SetMaterial(panel, mat);           // keeps its BoxCollider: the door physically blocks
            var marker = panel.AddComponent<PlaceholderMarker>();
            marker.category = cat;
            marker.note = "Swap with a modeled door: select this, select the FBX, run " +
                          "Tools > NASA Sim > Models > Swap Placeholder With Selected FBX.";

            var door = root.AddComponent<SimpleDoor>();
            door.motion = SimpleDoor.Motion.SlideLocal;
            door.slideOffset = Vector3.up * (height + 0.1f);  // sweeps clear of the opening
            door.moveDuration = 1.8f;
            var a = root.AddComponent<AudioSource>();
            a.playOnAwake = false;
            a.spatialBlend = 1f;
            door.audioSource = a;
            return door;
        }

        static AirlockButtonInteractable BuildButton(Transform parent, string name, Vector3 pos,
                                                     Vector3 face, Material mat, bool isOuter)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(face, Vector3.up));
            go.transform.localScale = Vector3.one * 0.25f;
            go.layer = NasaLayers.Interactable;
            SceneBootstrap.SetMaterial(go, mat);

            var col = go.GetComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = Vector3.one * 3f;                      // 0.75 m world trigger: comfortable prompt range

            var b = go.AddComponent<AirlockButtonInteractable>();
            b.isOuterButton = isOuter;
            return b;
        }

        internal static ParticleSystem BuildVent(Transform parent, string name, Vector3 pos, Vector3 aim, Material mat)
        {
            var ps = NewParticleObject(parent, name, pos, Quaternion.LookRotation(aim, Vector3.up), mat);

            var main = ps.main;
            main.startLifetime = 1.4f;
            main.startSpeed = 2.5f;
            main.startSize = 1f;
            var emission = ps.emission;
            emission.rateOverTime = 40f;
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 25f;
            shape.radius = 0.08f;
            SetGrowAndFade(ps, 0.25f, 1.2f);
            return ps;
        }

        static ParticleSystem BuildRoomFill(Transform parent, Vector3 pos, Vector3 outDir,
                                            float width, float gap, Material mat)
        {
            // Local +Z up (emit direction), local +Y along the tunnel axis — so the box shape's
            // gap-length extent runs BETWEEN the doors whichever world axis the tunnel lies on.
            var ps = NewParticleObject(parent, "RoomFill", pos,
                                       Quaternion.LookRotation(Vector3.up, outDir), mat);

            var main = ps.main;
            main.startLifetime = 2.2f;
            main.startSpeed = 0.5f;
            main.startSize = 1f;
            var emission = ps.emission;
            emission.rateOverTime = 12f;
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(width * 0.8f, gap * 0.8f, 0.2f);   // box is in local space; +Z is up here
            SetGrowAndFade(ps, 0.6f, 1.8f);
            return ps;
        }

        internal static ParticleSystem NewParticleObject(Transform parent, string name, Vector3 pos, Quaternion rot,
                                                Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.SetPositionAndRotation(pos, rot);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.useUnscaledTime = true;                      // pressurization runs in real seconds at 16x
            main.playOnAwake = false;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new Color(1f, 1f, 1f, 0.35f);
            main.maxParticles = 500;

            var psr = go.GetComponent<ParticleSystemRenderer>();
            psr.sharedMaterial = mat;
            psr.shadowCastingMode = ShadowCastingMode.Off;
            psr.receiveShadows = false;
            return ps;
        }

        internal static void SetGrowAndFade(ParticleSystem ps, float startScale, float endScale)
        {
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, startScale, 1f, endScale));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0.35f, 0f), new GradientAlphaKey(0.28f, 0.4f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }

        internal static Material MakeGasMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(GasMatPath);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
                mat = new Material(shader) { name = "AirlockGas" };
                AssetDatabase.CreateAsset(mat, GasMatPath);
            }
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_Cull", (float)CullMode.Off);
            mat.SetColor("_BaseColor", Color.white);
            var soft = AssetDatabase.GetBuiltinExtraResource<Texture2D>("Default-Particle.psd");
            if (soft != null && mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", soft);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            // Back the forced queue with _QueueOffset so URP's inspector validation (which recomputes
            // queue = Transparent + offset on any hand-edit) preserves the water < gas < glass order.
            if (mat.HasProperty("_QueueOffset"))
                mat.SetFloat("_QueueOffset", BiodomeFixTools.GasQueue - 3000f);
            mat.renderQueue = BiodomeFixTools.GasQueue;       // above water, below the dome glass
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // ------------------------------------------------------------------ helpers

        static Transform FindTunnel()
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                  FindObjectsSortMode.None))
                if (t.name.StartsWith("COL_tunnel")) return t;
            return null;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        static bool TryRendererBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }
    }
}
