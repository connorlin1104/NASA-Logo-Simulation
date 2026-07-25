using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Adds the walkable layer to the scene: a primitive astronaut, a staircase, and a balcony that
    /// overlooks the logo. Same philosophy as <see cref="SceneBootstrap"/> - primitive stand-ins wired
    /// entirely by Transform reference, so the real FBX drops in later with no code changes
    /// (see <see cref="ModelImportTools"/>).
    ///
    /// This is IDEMPOTENT: it finds objects by name and updates them instead of duplicating, so it is
    /// safe to run on a scene you have already customised. <see cref="SceneBootstrap.BuildTestScene"/>
    /// calls it too, so a fresh build produces everything at once.
    /// </summary>
    public static class AstronautSetup
    {
        const string MatDir = "Assets/_Project/Materials";

        // ---- Astronaut proportions (metres) ----
        const float EyeHeight = 1.62f;
        const float ShoulderY = 1.40f;
        const float HipY = 0.85f;
        const float BodyHeight = 1.80f;

        // ---- Moon-walk feel ----
        // This menu is the single source of truth for the feel: ApplyFeel() below re-applies these to the
        // scene's components on every run, so tuning them here + re-running the menu updates an existing
        // scene without a full rebuild. (Re-running therefore also resets any hand-tweaks to these fields.)
        const float WalkSpeed = 1.7f;          // relaxed lunar stroll
        const float RunSpeed = 3.4f;           // brisk 'speed walk', not a sprint
        const float MoonGravity = -3.5f;       // low, floaty
        const float JumpSpeed = 3.4f;          // Space; ~1.6 m apex under MoonGravity
        const float GroundedStick = -2f;
        // Jump feel: forgiving input + a committed arc.
        const float CoyoteTime = 0.12f;        // jump still fires briefly after leaving an edge
        const float JumpBufferTime = 0.12f;    // a press just before landing still fires
        const float JumpCutMultiplier = 0.5f;  // release Space mid-rise for a shorter hop
        const float AirControl = 0.55f;        // reduced mid-air steering, so the hop commits
        // Jump/landing pose (visual).
        const float JumpLegTuckDeg = 35f;
        const float JumpArmRaiseDeg = 25f;
        const float LandingSquashDepth = 0.16f;
        const float LandingKneeBendDeg = 30f;
        const float StrideFrequency = 0.34f;   // slow, loping cadence (strides per metre)
        const float ArmSwingDeg = 34f;
        const float LegSwingDeg = 28f;
        const float FullAmplitudeSpeed = 1.6f; // full swing already at a walk
        const float SettleSpeed = 5f;
        // First-person arms: enough lift to see them in the lower frame, but kept low and gentle — a big
        // lift + full-amplitude swing pumps them across the middle of the visor and reads as unnatural.
        const float FirstPersonArmLift = 26f;  // was 45 (too high, arms in front of the face)
        const float FirstPersonElbowBend = 34f;// was 60 (hands rode too high)
        const float FirstPersonSwingScale = 0.55f; // was 1 (full-amplitude swing looked like pumping)

        // ---- Staircase / balcony layout ----
        // The logo auto-fits to 40 units wide and, being wider than tall, its NORTH edge lands at
        // z = +17.0 (measured from nasa_logo_clean.csv). The staircase used to start at z = 15, i.e. two
        // units INSIDE the logo, where it blocked the tractor mowing the top of the meatball. It now
        // starts at z = 19 so the whole assembly is clear of the logo and of the tractor's body as it
        // rounds the top. The floor plane spans +/-25, so the stairs still sit fully on it; the balcony
        // deck beyond them slightly overhangs the placeholder floor's north edge (it is elevated and
        // faces away from the view — the real biodome floor will extend underneath it).
        const int StepCount = 12;
        const float StairBottomZ = 19f;
        const float StairTopZ = 24f;
        const float BalconyY = 4.5f;
        const float StairWidth = 3f;
        const float BalconyWidth = 8f;
        const float BalconyDepth = 3f;

        static float StepRise => BalconyY / StepCount;                    // 0.375
        static float StepRun => (StairTopZ - StairBottomZ) / StepCount;   // 0.41667

        [MenuItem("Tools/NASA Sim/Add Astronaut && Balcony To Scene")]
        public static void AddToSceneMenu() => AddToScene(logAtEnd: true);

        public static void AddToScene(bool logAtEnd)
        {
            Material suitMat      = SceneBootstrap.MakeMat($"{MatDir}/AstronautSuit.mat",   "Universal Render Pipeline/Lit", new Color(0.90f, 0.90f, 0.87f));
            Material visorMat     = SceneBootstrap.MakeMat($"{MatDir}/AstronautVisor.mat",  "Universal Render Pipeline/Lit", new Color(0.09f, 0.11f, 0.16f));
            Material accentMat    = SceneBootstrap.MakeMat($"{MatDir}/AstronautAccent.mat", "Universal Render Pipeline/Lit", new Color(0.16f, 0.30f, 0.62f));
            Material structureMat = SceneBootstrap.MakeMat($"{MatDir}/Structure.mat",       "Universal Render Pipeline/Lit", new Color(0.55f, 0.57f, 0.60f));

            var astronaut = BuildAstronaut(suitMat, visorMat, accentMat);
            BuildStairsAndBalcony(structureMat);
            WireCamera(astronaut);

            EditorSceneManager.MarkSceneDirty(astronaut.gameObject.scene);
            if (logAtEnd)
            {
                Selection.activeGameObject = astronaut.gameObject;
                Debug.Log("[AstronautSetup] Astronaut + staircase + balcony ready. Press Play: " +
                          "WASD to walk, Shift to run, mouse to look, C toggles " +
                          "first-person / fly-cam.", astronaut);
            }
        }

        // ------------------------------------------------------------------ astronaut

        static AstronautController BuildAstronaut(Material suit, Material visor, Material accent)
        {
            var root = FindOrCreate("Astronaut", null);
            bool fresh = root.GetComponent<AstronautController>() == null;

            // Only place it on first creation, so re-running never teleports an astronaut you moved.
            // A SPAWN_Outside marker (created by BiodomeFixTools) wins over the legacy default position.
            if (fresh)
            {
                var spawn = GameObject.Find("SPAWN_Outside");
                if (spawn != null)
                    root.transform.SetPositionAndRotation(spawn.transform.position,
                        Quaternion.Euler(0f, spawn.transform.eulerAngles.y, 0f));
                else
                    root.transform.SetPositionAndRotation(new Vector3(0f, 0.1f, -22f), Quaternion.identity);
            }

            GetOrAdd<CharacterController>(root);
            var controller = GetOrAdd<AstronautController>(root);

            // CharacterController dimensions are owned by the controller's serialized cc* fields and are
            // written ONLY on first creation. After that, Normalize Astronaut Scale / hand-tuning owns
            // them, and re-running this menu (to re-apply the feel) must not clobber that.
            if (fresh)
            {
                controller.ccSlopeLimit = 50f;   // must exceed the staircase ramp angle (~42 deg)
                controller.ccStepOffset = 0.30f;
                controller.ccSkinWidth = 0.02f;
                controller.ccRadius = 0.30f;
                controller.ccHeight = 1.80f;
                controller.ccCenter = new Vector3(0f, 0.90f, 0f);
                controller.ApplyControllerTuning();
            }

            // Anchors live on the ROOT, not inside the placeholder mesh, so the camera keeps working
            // after the placeholder is deleted and an FBX takes its place. If a real model is already
            // swapped in, leave the anchor positions alone: the astronaut swap re-seats Head at the
            // model's true eye height, and re-running this menu (e.g. to re-apply the feel) must not
            // clobber that.
            bool hasModel = root.transform.Find("Astronaut_Model") != null;

            bool hadHead = root.transform.Find("Head") != null;
            var head = FindOrCreate("Head", root.transform);
            if (!hadHead || !hasModel)
            {
                head.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
                head.transform.localRotation = Quaternion.identity;
            }

            bool hadPivot = root.transform.Find("CameraPivot") != null;
            var pivot = FindOrCreate("CameraPivot", root.transform);
            if (!hadPivot || !hasModel)
            {
                pivot.transform.localPosition = new Vector3(0f, 1.40f, 0f);
                pivot.transform.localRotation = Quaternion.identity;
            }

            var visual = GetOrAdd<AstronautLocomotionVisual>(root);
            visual.controller = controller;

            ApplyFeel(controller, visual);

            // Rebuild the placeholder geometry only if it is still a placeholder. If a real model has
            // been swapped in (no "Placeholder" child), leave the visuals completely alone.
            var existing = root.transform.Find("Placeholder");
            bool isStillPlaceholder = existing != null || root.transform.Find("Astronaut_Model") == null;
            if (isStillPlaceholder)
            {
                if (existing != null) Object.DestroyImmediate(existing.gameObject);
                var ph = BuildPlaceholderBody(root.transform, suit, visor, accent);

                visual.leftArm  = ph.Find("Arm_L_Upper");
                visual.rightArm = ph.Find("Arm_R_Upper");
                visual.leftForearm  = ph.Find("Arm_L_Upper/Arm_L_Lower");
                visual.rightForearm = ph.Find("Arm_R_Upper/Arm_R_Lower");
                visual.leftLeg  = ph.Find("Leg_L_Upper");
                visual.rightLeg = ph.Find("Leg_R_Upper");
            }

            return controller;
        }

        /// <summary>Push the moon-walk / jump / first-person-arm feel onto the live components.</summary>
        static void ApplyFeel(AstronautController controller, AstronautLocomotionVisual visual)
        {
            controller.walkSpeed = WalkSpeed;
            controller.runSpeed = RunSpeed;
            controller.gravity = MoonGravity;
            controller.groundedStick = GroundedStick;
            controller.enableJump = true;
            controller.jumpSpeed = JumpSpeed;
            controller.coyoteTime = CoyoteTime;
            controller.jumpBufferTime = JumpBufferTime;
            controller.variableJumpHeight = true;
            controller.jumpCutMultiplier = JumpCutMultiplier;
            controller.airControl = AirControl;

            visual.strideFrequency = StrideFrequency;
            visual.armSwingDeg = ArmSwingDeg;
            visual.legSwingDeg = LegSwingDeg;
            visual.fullAmplitudeSpeed = FullAmplitudeSpeed;
            visual.settleSpeed = SettleSpeed;
            visual.firstPersonArmLift = FirstPersonArmLift;
            visual.firstPersonElbowBend = FirstPersonElbowBend;
            visual.firstPersonSwingScale = FirstPersonSwingScale;

            visual.jumpPose = true;
            visual.jumpLegTuckDeg = JumpLegTuckDeg;
            visual.jumpArmRaiseDeg = JumpArmRaiseDeg;
            visual.landingSquash = true;
            visual.landingSquashDepth = LandingSquashDepth;
            visual.landingKneeBendDeg = LandingKneeBendDeg;
        }

        /// <summary>
        /// The stand-in body. Arms and legs are empty pivots at the shoulder/hip with the visible mesh
        /// hanging below them - that is what lets <see cref="AstronautLocomotionVisual"/> swing them by
        /// rotating the pivot, exactly as it will rotate a real bone.
        /// </summary>
        static Transform BuildPlaceholderBody(Transform parent, Material suit, Material visor, Material accent)
        {
            var ph = new GameObject("Placeholder").transform;
            ph.SetParent(parent, worldPositionStays: false);
            ph.localPosition = Vector3.zero;

            // Torso: hips up to shoulders.
            float torsoTop = ShoulderY + 0.10f;
            Prim(PrimitiveType.Capsule, "Torso", ph, suit,
                 pos: new Vector3(0f, (HipY + torsoTop) * 0.5f, 0f),
                 scale: new Vector3(0.56f, (torsoTop - HipY) * 0.5f, 0.40f));

            // Helmet + visor.
            Prim(PrimitiveType.Sphere, "Helmet", ph, suit,
                 pos: new Vector3(0f, EyeHeight + 0.03f, 0f),
                 scale: new Vector3(0.32f, 0.32f, 0.32f));
            Prim(PrimitiveType.Sphere, "Visor", ph, visor,
                 pos: new Vector3(0f, EyeHeight + 0.02f, 0.075f),
                 scale: new Vector3(0.25f, 0.21f, 0.25f));

            // Life-support pack, so "forward" is unmistakable at a glance.
            Prim(PrimitiveType.Cube, "Backpack", ph, accent,
                 pos: new Vector3(0f, 1.18f, -0.26f),
                 scale: new Vector3(0.40f, 0.50f, 0.18f));

            BuildLimb(ph, "Arm_L", new Vector3(-0.34f, ShoulderY, 0f), 0.30f, 0.28f, 0.13f, suit);
            BuildLimb(ph, "Arm_R", new Vector3( 0.34f, ShoulderY, 0f), 0.30f, 0.28f, 0.13f, suit);
            BuildLimb(ph, "Leg_L", new Vector3(-0.14f, HipY, 0f),      0.45f, 0.40f, 0.17f, suit);
            BuildLimb(ph, "Leg_R", new Vector3( 0.14f, HipY, 0f),      0.45f, 0.40f, 0.17f, suit);

            return ph;
        }

        /// <summary>Two-segment limb: Upper pivot -> Lower pivot, each with its mesh hanging below the joint.</summary>
        static void BuildLimb(Transform parent, string prefix, Vector3 shoulder,
                              float upperLen, float lowerLen, float thickness, Material mat)
        {
            var upper = new GameObject($"{prefix}_Upper").transform;
            upper.SetParent(parent, worldPositionStays: false);
            upper.localPosition = shoulder;

            Prim(PrimitiveType.Cube, "Mesh", upper, mat,
                 pos: new Vector3(0f, -upperLen * 0.5f, 0f),
                 scale: new Vector3(thickness, upperLen, thickness));

            var lower = new GameObject($"{prefix}_Lower").transform;
            lower.SetParent(upper, worldPositionStays: false);
            lower.localPosition = new Vector3(0f, -upperLen, 0f);

            Prim(PrimitiveType.Cube, "Mesh", lower, mat,
                 pos: new Vector3(0f, -lowerLen * 0.5f, 0f),
                 scale: new Vector3(thickness * 0.92f, lowerLen, thickness * 0.92f));
        }

        // ------------------------------------------------------------------ stairs + balcony

        static void BuildStairsAndBalcony(Material structure)
        {
            var environment = FindOrCreate("Environment", null);
            var biodome = FindOrCreate("Biodome", environment.transform);

            // ---- Staircase: solid blocks rising south -> north ----
            var stairs = FindOrCreate("Staircase", biodome.transform);
            ClearChildren(stairs.transform);
            for (int i = 0; i < StepCount; i++)
            {
                float topY = (i + 1) * StepRise;
                var step = Prim(PrimitiveType.Cube, $"Step_{i:00}", stairs.transform, structure,
                                pos: new Vector3(0f, topY * 0.5f, StairBottomZ + (i + 0.5f) * StepRun),
                                scale: new Vector3(StairWidth, topY, StepRun));
                // No collider: the ramp below does all the collision work.
                var bc = step.GetComponent<Collider>();
                if (bc != null) Object.DestroyImmediate(bc);
            }

            // ---- The stair trick: one invisible ramp instead of 12 box colliders ----
            // A CharacterController walking up stacked boxes catches on every tread edge and jitters.
            // A single smooth ramp over the nosings removes that entirely, while the steps stay visible.
            var ramp = FindOrCreate("StairRamp", biodome.transform);
            var rampRenderer = ramp.GetComponent<MeshRenderer>();
            if (rampRenderer != null) Object.DestroyImmediate(rampRenderer);
            var rampFilter = ramp.GetComponent<MeshFilter>();
            if (rampFilter != null) Object.DestroyImmediate(rampFilter);

            float run = StairTopZ - StairBottomZ;
            float rise = BalconyY;
            float angleDeg = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;   // ~42 deg, under the 50 slopeLimit
            float length = Mathf.Sqrt(run * run + rise * rise);
            const float thickness = 0.5f;

            // Lift the ramp line by half a step so its surface passes through the middle of each riser -
            // i.e. level with the tread centres, so the astronaut's feet neither float nor sink.
            float lift = StepRise * 0.5f;
            Vector3 mid = new Vector3(0f, rise * 0.5f + lift, (StairBottomZ + StairTopZ) * 0.5f);
            Quaternion rot = Quaternion.Euler(-angleDeg, 0f, 0f);      // local +Z points up the slope
            ramp.transform.SetPositionAndRotation(mid - (rot * Vector3.up) * (thickness * 0.5f), rot);
            ramp.transform.localScale = Vector3.one;

            var rampCol = GetOrAdd<BoxCollider>(ramp);
            rampCol.center = Vector3.zero;
            rampCol.size = new Vector3(StairWidth * 0.96f, thickness, length);

            // ---- Balcony ----
            var balcony = FindOrCreate("Balcony", biodome.transform);
            ClearChildren(balcony.transform);

            float floorCenterZ = StairTopZ + BalconyDepth * 0.5f;
            const float slab = 0.3f;
            Prim(PrimitiveType.Cube, "BalconyFloor", balcony.transform, structure,
                 pos: new Vector3(0f, BalconyY - slab * 0.5f, floorCenterZ),
                 scale: new Vector3(BalconyWidth, slab, BalconyDepth));

            // Railings on the three closed sides. The south side stays open - that is where the stairs
            // arrive, and it is the side facing the logo, so the view stays unobstructed.
            const float railH = 1.1f, railT = 0.12f;
            float railY = BalconyY + railH * 0.5f;
            float north = floorCenterZ + BalconyDepth * 0.5f;
            Prim(PrimitiveType.Cube, "Railing_N", balcony.transform, structure,
                 pos: new Vector3(0f, railY, north),
                 scale: new Vector3(BalconyWidth, railH, railT));
            Prim(PrimitiveType.Cube, "Railing_E", balcony.transform, structure,
                 pos: new Vector3(BalconyWidth * 0.5f, railY, floorCenterZ),
                 scale: new Vector3(railT, railH, BalconyDepth));
            Prim(PrimitiveType.Cube, "Railing_W", balcony.transform, structure,
                 pos: new Vector3(-BalconyWidth * 0.5f, railY, floorCenterZ),
                 scale: new Vector3(railT, railH, BalconyDepth));

            // Support pillars.
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Prim(PrimitiveType.Cube, $"Pillar_{(sx < 0 ? "W" : "E")}{(sz < 0 ? "S" : "N")}",
                         balcony.transform, structure,
                         pos: new Vector3(sx * (BalconyWidth * 0.5f - 0.4f),
                                          (BalconyY - slab) * 0.5f,
                                          floorCenterZ + sz * (BalconyDepth * 0.5f - 0.4f)),
                         scale: new Vector3(0.3f, BalconyY - slab, 0.3f));
        }

        // ------------------------------------------------------------------ camera

        static void WireCamera(AstronautController astronaut)
        {
            var camGo = GameObject.Find("Main Camera");
            Camera cam = camGo != null ? camGo.GetComponent<Camera>() : Camera.main;
            if (cam == null)
            {
                Debug.LogWarning("[AstronautSetup] No Main Camera found; the astronaut camera rig was not added.");
                return;
            }

            var rig = GetOrAdd<AstronautCameraRig>(cam.gameObject);
            rig.astronaut = astronaut;
            rig.locomotion = astronaut.GetComponent<AstronautLocomotionVisual>();
            rig.headAnchor = astronaut.transform.Find("Head");
            rig.mode = AstronautCameraRig.ViewMode.FirstPerson;
        }

        // ------------------------------------------------------------------ helpers

        static GameObject FindOrCreate(string name, Transform parent)
        {
            Transform t = parent != null ? parent.Find(name) : null;
            if (t == null && parent == null)
            {
                var found = GameObject.Find(name);
                if (found != null && found.transform.parent == null) t = found.transform;
            }
            if (t != null) return t.gameObject;

            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, worldPositionStays: false);
            return go;
        }

        static void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                Object.DestroyImmediate(t.GetChild(i).gameObject);
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        /// <summary>Create a primitive child. Astronaut body parts get their colliders stripped - the
        /// CharacterController is the only collider the astronaut needs.</summary>
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

            if (parent != null && parent.GetComponentInParent<AstronautController>() != null)
            {
                var col = go.GetComponent<Collider>();
                if (col != null) Object.DestroyImmediate(col);
            }
            return go;
        }
    }
}
