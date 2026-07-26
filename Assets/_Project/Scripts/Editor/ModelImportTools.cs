using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// One-click FBX wiring. Select a model in the Project window and pick a menu item.
    ///
    /// The astronaut swap deliberately replaces only the VISUAL: the CharacterController,
    /// <see cref="AstronautController"/>, camera rig and the Head / CameraPivot anchors all live on the
    /// astronaut root and survive untouched. That is why the placeholder can be deleted without
    /// breaking anything.
    ///
    /// See Assets/_Project/Models/IMPORT_GUIDE.md for the naming/export spec these tools expect.
    /// </summary>
    public static class ModelImportTools
    {
        const float TargetAstronautHeight = 1.8f;   // metres
        const float TractorTargetLength = 3.5f;      // largest horizontal dimension, world units

        // Name prefixes recognised on an imported biodome. See IMPORT_GUIDE.md.
        const string ColliderPrefix = "COL_";
        const string StairPrefix = "STAIR_";
        const string NoColliderPrefix = "NOCOL_";
        const string SpawnPrefix = "SPAWN_";
        const string BalconyPrefix = "BALCONY_";

        // ================================================================== tractor

        [MenuItem("Tools/NASA Sim/Tractor/Validate Selected FBX")]
        public static void ValidateTractor()
        {
            var model = GetSelectedModel(out string path);
            if (model == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"=== Tractor FBX report: {model.name} ===");
            sb.AppendLine($"Path: {path}");

            var temp = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                var wheels = FindWheels(temp.transform);
                sb.AppendLine($"Wheels found ({wheels.Count}): " +
                              (wheels.Count > 0 ? string.Join(", ", wheels.Select(w => w.name)) : "-- none --"));
                if (wheels.Count == 0)
                    sb.AppendLine("  ^ No wheels detected. Put 'wheel' in each wheel object's name " +
                                  "(wheel_01, wheel_02, ...), each as its own object, or assign Drive Wheels " +
                                  "by hand on the Tractor's Tractor Path Follower.");

                if (TryGetRendererBounds(temp, out Bounds b))
                {
                    sb.AppendLine($"Bounding box: {b.size.x:0.##} wide x {b.size.y:0.##} tall x {b.size.z:0.##} long (m)");
                    float largest = Mathf.Max(b.size.x, b.size.z);
                    if (largest > TractorTargetLength * 8f)
                        sb.AppendLine("  ^ ~100x too big (exported in centimetres). The swap tool auto-corrects this.");
                    else if (largest < TractorTargetLength * 0.05f)
                        sb.AppendLine("  ^ ~100x too small. The swap tool auto-corrects this.");
                }
                else sb.AppendLine("Bounding box: no renderers found (!).");

                int missing = 0, total = 0;
                foreach (var r in temp.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials) { total++; if (m == null) missing++; }
                sb.AppendLine($"Materials: {total - missing}/{total} assigned" +
                              (missing > 0 ? " -- MISSING. Re-export with Embed Media ON, or supply the .fbm folder." : "."));
            }
            finally { Object.DestroyImmediate(temp); }

            Debug.Log(sb.ToString());
        }

        [MenuItem("Tools/NASA Sim/Tractor/Swap In Selected FBX")]
        public static void SwapInTractor()
        {
            var model = GetSelectedModel(out string path);
            if (model == null) return;

            var follower = Object.FindAnyObjectByType<TractorPathFollower>();
            if (follower == null)
            {
                EditorUtility.DisplayDialog("No tractor in scene",
                    "Run Tools > NASA Sim > Build Test Scene first, then swap the tractor FBX in.", "OK");
                return;
            }

            Transform root = follower.transform;
            Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Swap In Tractor FBX");

            // Delete the primitive visuals (Body, Wheel_*) and any prior swapped model, but KEEP MowerAnchor:
            // it carries the mower brush, its trail visual and controller, all still wired to the follower.
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var c = root.GetChild(i);
                if (c.name == "MowerAnchor") continue;
                Object.DestroyImmediate(c.gameObject);
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "Tractor_Model";
            instance.transform.SetParent(root, worldPositionStays: false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // Let the follower re-ground this model on every run, so rescaling it later never floats/sinks it.
            follower.visualRoot = instance.transform;

            // ---- Auto-scale so the largest horizontal dimension matches the primitive footprint ----
            if (TryGetRendererBounds(instance, out Bounds b))
            {
                float largest = Mathf.Max(b.size.x, b.size.z);
                if (largest > 1e-4f)
                {
                    float factor = TractorTargetLength / largest;
                    instance.transform.localScale = Vector3.one * factor;
                    Debug.Log($"[ModelImportTools] Scaled tractor '{model.name}' by {factor:0.####}.");
                }
            }

            // ---- Sit the wheels on the ground. The follower holds the body at fixedY, so ground is at
            // (root.y - fixedY); drop the model so its lowest point rests there. ----
            if (TryGetRendererBounds(instance, out Bounds scaled))
            {
                float ground = root.position.y - follower.fixedY;
                float dy = ground - scaled.min.y;
                instance.transform.position += new Vector3(0f, dy, 0f);
            }

            // ---- Wheels ----
            // Build one visual entry per wheel, each with its OWN axle. An exported wheel mesh often has
            // its pivot at the model origin rather than at the hub, which makes it swing in an arc instead
            // of spinning; so an empty is created at each wheel's measured hub centre and used as that
            // wheel's pivot. It is parented alongside the wheel so it follows any later rescale.
            var found = FindWheels(instance.transform);
            int used = Mathf.Min(found.Count, TractorPathFollower.MaxWheels);
            var visuals = new TractorPathFollower.WheelVisual[used];

            for (int i = 0; i < used; i++)
            {
                Transform wheel = found[i];
                Transform axle = null;

                if (TryGetRendererBounds(wheel.gameObject, out Bounds wb))
                {
                    var axleGo = new GameObject($"Axle_{wheel.name}");
                    axle = axleGo.transform;
                    axle.SetParent(wheel.parent != null ? wheel.parent : root, worldPositionStays: true);
                    // Hub centre, oriented to the tractor so the axle's local X runs left-right.
                    axle.SetPositionAndRotation(wb.center, root.rotation);
                }

                visuals[i] = new TractorPathFollower.WheelVisual
                {
                    mesh = wheel,
                    axle = axle,
                    // These axles are built X-along-the-axle; hand-made ones default to Z.
                    axleAxis = TractorPathFollower.AxleAxis.X,
                };
            }

            follower.wheels = visuals;
            follower.steerWheels = new Transform[0];   // front-steer left to a manual pass if wanted

            if (found.Count > used)
                Debug.LogWarning($"[ModelImportTools] Found {found.Count} wheels but the visual list holds " +
                                 $"{TractorPathFollower.MaxWheels}. Wired: " +
                                 $"{string.Join(", ", found.Take(used).Select(w => w.name))}. NOT wired: " +
                                 $"{string.Join(", ", found.Skip(used).Select(w => w.name))}.", root);

            // Model geometry is visual-only for M1 (kinematic path following); strip any colliders it brought.
            foreach (var col in instance.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);

            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            Selection.activeGameObject = root.gameObject;

            Debug.Log($"[ModelImportTools] Swapped in tractor '{model.name}'. " +
                      (used > 0
                          ? $"{used} wheel(s) wired, each with an Axle_* pivot created at its hub. Press Play; " +
                            "if a wheel spins on the wrong axis, rotate its Axle_* object so its X (red) " +
                            "arrow points along the axle."
                          : "NO wheels wired - they will not spin. Run 'Validate Selected FBX', or fill in " +
                            "Tractor > Tractor Path Follower > Wheels by hand.") +
                      " If it drives backwards, use Tractor > Rotate Model 90.", root);
        }

        /// <summary>
        /// Fill in the follower's Wheels list from what is already in the scene - no rebuild, nothing
        /// deleted, no model re-imported. Select the wheels in the Hierarchy (either each wheel's
        /// axle/group object, or the wheel mesh itself) and run this.
        /// </summary>
        [MenuItem("Tools/NASA Sim/Tractor/Populate Wheels From Selection")]
        public static void PopulateWheelsFromSelection()
        {
            var follower = Object.FindAnyObjectByType<TractorPathFollower>();
            if (follower == null)
            {
                EditorUtility.DisplayDialog("No tractor in scene",
                    "This scene has no Tractor Path Follower to fill in.", "OK");
                return;
            }

            var sel = Selection.transforms;
            if (sel == null || sel.Length == 0)
            {
                EditorUtility.DisplayDialog("Select your wheels",
                    $"In the HIERARCHY select up to {TractorPathFollower.MaxWheels} wheels - either each " +
                    "wheel's axle/group object, or the wheel mesh itself - then run this again.\n\n" +
                    "Selecting the axle/group is preferred: the mesh under it is found automatically and " +
                    "the group becomes that wheel's pivot.", "OK");
                return;
            }

            int used = Mathf.Min(sel.Length, TractorPathFollower.MaxWheels);
            var list = new List<TractorPathFollower.WheelVisual>();
            var log = new StringBuilder();

            for (int i = 0; i < used; i++)
            {
                Transform t = sel[i];
                Transform mesh, axle = null;

                if (t.GetComponent<Renderer>() != null)
                {
                    // A mesh was selected. A renderer-less parent is almost certainly the axle/group.
                    mesh = t;
                    if (t.parent != null && t.parent != follower.transform &&
                        t.parent.GetComponent<Renderer>() == null)
                        axle = t.parent;
                }
                else
                {
                    // A group/axle was selected: use it as the pivot, and find the wheel mesh beneath it.
                    axle = t;
                    mesh = LargestRendererUnder(t);
                }

                if (mesh == null)
                {
                    log.AppendLine($"  '{t.name}' - no mesh found under it, SKIPPED");
                    continue;
                }

                list.Add(new TractorPathFollower.WheelVisual { mesh = mesh, axle = axle });
                log.AppendLine($"  mesh '{mesh.name}'   axle '{(axle != null ? axle.name : "(none - own pivot)")}'");
            }

            Undo.RecordObject(follower, "Populate Wheels");
            follower.wheels = list.ToArray();
            EditorUtility.SetDirty(follower);
            EditorSceneManager.MarkSceneDirty(follower.gameObject.scene);
            Selection.activeGameObject = follower.gameObject;

            if (sel.Length > used)
                log.AppendLine($"  NOTE: {sel.Length - used} extra selected object(s) ignored " +
                               $"(the list holds {TractorPathFollower.MaxWheels}).");

            Debug.Log($"[ModelImportTools] Wheels wired ({list.Count}):\n{log}" +
                      "Check the Scene view: cyan line = axis of rotation, red dot = pivot, yellow circle " +
                      "= rolling radius.", follower);
        }

        /// <summary>The biggest renderer at or under this transform - the wheel mesh inside a group.</summary>
        static Transform LargestRendererUnder(Transform root)
        {
            Transform best = null;
            float bestSize = -1f;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                float s = r.bounds.size.sqrMagnitude;
                if (s > bestSize) { bestSize = s; best = r.transform; }
            }
            return best;
        }

        /// <summary>
        /// Wire the two FRONT wheel pivots as steer wheels, so the steering model visually yaws them
        /// through corners. Works because <c>SpinWheels</c> rebuilds each wheel mesh's pose from its
        /// axle's CURRENT rotation every frame — yawing the axle steers the wheel with no extra wiring.
        ///
        /// Front/rear is decided by each wheel's MEASURED HUB (renderer bounds), never by the axle
        /// transform's position: an FBX exported with frozen transforms parks every pivot on the model
        /// origin, so sorting on <c>axle.position</c> compares four identical numbers and hands back an
        /// arbitrary pair — which is how a rear wheel ends up doing the steering.
        ///
        /// The follower now does this itself at the start of every run (Steer Wheel Selection = Auto
        /// Front); this menu just fills the list in at edit time so you can see and check the choice.
        /// </summary>
        [MenuItem("Tools/NASA Sim/Tractor/Wire Steer Wheels From Axles")]
        public static void WireSteerWheelsFromAxles()
        {
            var follower = Object.FindAnyObjectByType<TractorPathFollower>();
            if (follower == null)
            {
                Debug.LogWarning("[ModelImportTools] No Tractor Path Follower in the scene.");
                return;
            }

            bool rear = follower.steerWheelSelection == TractorPathFollower.SteerWheelSelection.AutoRear;

            var candidates = new List<(Transform axle, float z, bool namedFront, bool namedRear)>();
            if (follower.wheels != null)
                foreach (var w in follower.wheels)
                {
                    if (w?.axle == null) continue;
                    if (candidates.Exists(c => c.axle == w.axle)) continue;   // two meshes, one axle
                    if (!follower.TryGetWheelAxis(w, out Vector3 hub, out _, out _, out _)) continue;
                    candidates.Add((w.axle,
                                    follower.transform.InverseTransformPoint(hub).z,
                                    HasNameToken(w.axle, follower.transform, "front", "fwd"),
                                    HasNameToken(w.axle, follower.transform, "back", "rear")));
                }

            if (candidates.Count < 2)
            {
                Debug.LogWarning("[ModelImportTools] Fewer than two wheels with axle pivots are wired — " +
                                 "run Tractor > Swap In Selected FBX or Populate Wheels From Selection first.",
                                 follower);
                return;
            }

            // Furthest along the tractor's own +Z is the nose; Auto Rear wants the other end.
            candidates.Sort((a, b) => rear ? a.z.CompareTo(b.z) : b.z.CompareTo(a.z));
            Undo.RecordObject(follower, "Wire Steer Wheels");
            follower.steerWheels = new[] { candidates[0].axle, candidates[1].axle };
            EditorUtility.SetDirty(follower);
            EditorSceneManager.MarkSceneDirty(follower.gameObject.scene);

            var log = new StringBuilder();
            log.AppendLine($"[ModelImportTools] Steer wheels = the two furthest {(rear ? "BACK" : "FORWARD")} " +
                           "by measured hub:");
            for (int i = 0; i < candidates.Count; i++)
                log.AppendLine($"  {(i < 2 ? "STEER " : "      ")}{ScenePathTo(candidates[i].axle, follower.transform)}" +
                               $"   hub at local z = {candidates[i].z:0.###} m");

            // The model's own naming is a free second opinion. Any disagreement — a wheel named for the
            // wrong end being picked, or one named for the right end being passed over — means the model
            // is not facing the way it drives.
            string wanted = rear ? "rear" : "front", other = rear ? "front" : "rear";
            bool pickedWrongEnd = false, passedOverRightEnd = false;
            for (int i = 0; i < candidates.Count; i++)
            {
                bool namedWanted = rear ? candidates[i].namedRear : candidates[i].namedFront;
                bool namedOther = rear ? candidates[i].namedFront : candidates[i].namedRear;
                if (i < 2 && namedOther) pickedWrongEnd = true;
                if (i >= 2 && namedWanted) passedOverRightEnd = true;
            }
            if (pickedWrongEnd || passedOverRightEnd)
                log.AppendLine($"  WARNING: the geometry and the model's own names disagree — a wheel " +
                               $"named '{other}' was chosen, or one named '{wanted}' was passed over. The " +
                               "model is probably not facing the way it drives: run Tractor > Rotate " +
                               "Model 90 until its nose leads, then re-run this.");
            if (candidates.Count > 2 && Mathf.Abs(candidates[1].z - candidates[2].z) < 0.05f)
                log.AppendLine("  WARNING: the two ends sit at almost the same z, so front and rear are " +
                               "not distinguishable — the model may be rotated sideways. Run Tractor > " +
                               "Rotate Model 90, then re-run this.");
            if (follower.steerWheelSelection != TractorPathFollower.SteerWheelSelection.Manual)
                log.AppendLine($"  NOTE: Steer Wheel Selection is {follower.steerWheelSelection}, so the " +
                               "follower re-derives this same pair at the start of every run. This menu " +
                               "only fills the list in so you can see it.");

            Debug.Log(log.ToString(), follower);
        }

        /// <summary>True if this transform or any ancestor below <paramref name="stopAt"/> is named for one
        /// of the given tokens.</summary>
        static bool HasNameToken(Transform t, Transform stopAt, params string[] tokens)
        {
            while (t != null && t != stopAt)
            {
                string n = t.name.ToLowerInvariant();
                foreach (var token in tokens)
                    if (n.Contains(token)) return true;
                t = t.parent;
            }
            return false;
        }

        static string ScenePathTo(Transform t, Transform stopAt)
        {
            var sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null && p != stopAt; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        [MenuItem("Tools/NASA Sim/Tractor/Rotate Model 90 (fix facing)")]
        public static void RotateTractorModel()
        {
            var follower = Object.FindAnyObjectByType<TractorPathFollower>();
            var model = follower != null ? follower.transform.Find("Tractor_Model") : null;
            if (model == null)
            {
                EditorUtility.DisplayDialog("No swapped model",
                    "Swap in a tractor FBX first (Tools > NASA Sim > Tractor > Swap In Selected FBX).", "OK");
                return;
            }
            Undo.RecordObject(model, "Rotate Tractor Model");
            model.localRotation *= Quaternion.Euler(0f, 90f, 0f);
            EditorSceneManager.MarkSceneDirty(model.gameObject.scene);
            Debug.Log($"[ModelImportTools] Tractor model yaw is now {model.localEulerAngles.y:0} deg. " +
                      "Run again until its nose points the way it drives.");
        }

        /// <summary>Wheel objects: any renderer whose name contains wheel / tire / tyre.</summary>
        static List<Transform> FindWheels(Transform root)
        {
            var result = new List<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root) continue;
                if (t.GetComponent<Renderer>() == null) continue;   // skip empty "Wheels" group nodes
                string n = t.name.ToLowerInvariant();
                if (n.Contains("wheel") || n.Contains("tire") || n.Contains("tyre"))
                    result.Add(t);
            }
            return result;
        }

        // ================================================================== astronaut

        [MenuItem("Tools/NASA Sim/Astronaut/Validate Selected FBX")]
        public static void ValidateAstronaut()
        {
            var model = GetSelectedModel(out string path);
            if (model == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"=== FBX report: {model.name} ===");
            sb.AppendLine($"Path: {path}");

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            var temp = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                // ---- Rig ----
                var animator = temp.GetComponentInChildren<Animator>();
                if (importer != null) sb.AppendLine($"Rig type: {importer.animationType}");
                bool humanoid = animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid;
                sb.AppendLine(humanoid
                    ? "Humanoid avatar: VALID -> bone names do not matter, the code maps them automatically."
                    : "Humanoid avatar: NO -> falling back to name search. See IMPORT_GUIDE.md for the " +
                      "expected joint names, or set Rig > Animation Type = Humanoid in the model's Inspector.");

                // ---- Bones the locomotion visual needs ----
                var probe = temp.AddComponent<AstronautLocomotionVisual>();
                probe.driveMode = AstronautLocomotionVisual.DriveMode.ForceProcedural;
                probe.animator = animator;
                probe.Rebind();
                sb.AppendLine("Bones found:");
                sb.AppendLine($"  L upper arm : {Describe(probe.leftArm)}");
                sb.AppendLine($"  R upper arm : {Describe(probe.rightArm)}");
                sb.AppendLine($"  L lower arm : {Describe(probe.leftForearm)}");
                sb.AppendLine($"  R lower arm : {Describe(probe.rightForearm)}");
                sb.AppendLine($"  L upper leg : {Describe(probe.leftLeg)}");
                sb.AppendLine($"  R upper leg : {Describe(probe.rightLeg)}");
                if (probe.leftArm == null || probe.rightArm == null)
                    sb.AppendLine("  ^ ARMS NOT FOUND - arms will not swing. Rig as Humanoid, rename the " +
                                  "joints, or assign them by hand on the Astronaut's Locomotion Visual.");

                // ---- Scale (the classic Maya-centimetres trap) ----
                if (TryGetRendererBounds(temp, out Bounds b))
                {
                    float h = b.size.y;
                    sb.AppendLine($"Measured height: {h:0.###} m (wanted ~{TargetAstronautHeight} m)");
                    if (h > TargetAstronautHeight * 10f)
                        sb.AppendLine("  ^ ~100x too big: exported in centimetres. The swap tool auto-corrects this.");
                    else if (h < TargetAstronautHeight * 0.1f)
                        sb.AppendLine("  ^ ~100x too small. The swap tool auto-corrects this.");
                }
                else sb.AppendLine("Measured height: no renderers found (!).");

                // ---- Materials / textures ----
                int missingMats = 0, totalMats = 0;
                foreach (var r in temp.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                    {
                        totalMats++;
                        if (m == null) missingMats++;
                    }
                sb.AppendLine($"Materials: {totalMats - missingMats}/{totalMats} assigned" +
                              (missingMats > 0
                                  ? " -- MISSING. Re-export with Embed Media ON, or supply the .fbm texture folder."
                                  : "."));

                // ---- Animation ----
                var clips = new List<string>();
                foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (a is AnimationClip clip && !clip.name.StartsWith("__preview__")) clips.Add(clip.name);
                sb.AppendLine(clips.Count > 0
                    ? $"Animation clips ({clips.Count}): {string.Join(", ", clips)} -> assign an Animator " +
                      "Controller and the real walk cycle drives the arms."
                    : "Animation clips: none -> the procedural arm swing will be used (this is fine).");
            }
            finally
            {
                Object.DestroyImmediate(temp);
            }

            Debug.Log(sb.ToString());
        }

        [MenuItem("Tools/NASA Sim/Astronaut/Swap In Selected FBX")]
        public static void SwapInAstronaut()
        {
            var model = GetSelectedModel(out string path);
            if (model == null) return;

            var controller = Object.FindAnyObjectByType<AstronautController>();
            if (controller == null)
            {
                EditorUtility.DisplayDialog("No astronaut in scene",
                    "Run Tools > NASA Sim > Add Astronaut & Balcony To Scene first, then swap the FBX in.", "OK");
                return;
            }

            Transform root = controller.transform;
            Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Swap In Astronaut FBX");

            // Remove whatever visual is there now (placeholder or a previously swapped model). The
            // anchors and components on the root are left alone on purpose.
            foreach (string visualName in new[] { "Placeholder", "Astronaut_Model" })
            {
                var old = root.Find(visualName);
                if (old != null) Object.DestroyImmediate(old.gameObject);
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            instance.name = "Astronaut_Model";
            instance.transform.SetParent(root, worldPositionStays: false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // ---- Auto-scale to 1.8 m, and sit the feet on the root's origin ----
            if (TryGetRendererBounds(instance, out Bounds b) && b.size.y > 1e-4f)
            {
                float factor = TargetAstronautHeight / b.size.y;
                instance.transform.localScale = Vector3.one * factor;

                if (TryGetRendererBounds(instance, out Bounds scaled))
                {
                    float footOffset = scaled.min.y - root.position.y;
                    instance.transform.localPosition -= new Vector3(0f, footOffset, 0f);
                }
                Debug.Log($"[ModelImportTools] Scaled '{model.name}' by {factor:0.####} to reach " +
                          $"{TargetAstronautHeight} m.");
            }

            // ---- Bones + animation ----
            var animator = instance.GetComponentInChildren<Animator>();
            var visual = controller.GetComponent<AstronautLocomotionVisual>();
            if (visual == null) visual = controller.gameObject.AddComponent<AstronautLocomotionVisual>();
            visual.controller = controller;
            visual.animator = animator;
            // Clear the placeholder's bone refs (now destroyed) so auto-detection runs clean.
            visual.leftArm = visual.rightArm = visual.leftForearm = visual.rightForearm = null;
            visual.leftLeg = visual.rightLeg = null;
            visual.Rebind();

            // ---- Re-anchor the first-person camera to the real head bone if we have one ----
            var head = root.Find("Head");
            if (head != null && animator != null && animator.isHuman)
            {
                var headBone = animator.GetBoneTransform(HumanBodyBones.Head);
                if (headBone != null)
                {
                    // Keep Head as a root-level anchor (it must not inherit the head bone's animation
                    // wobble), but move it to the real eye height of this model.
                    Vector3 local = root.InverseTransformPoint(headBone.position);
                    head.localPosition = new Vector3(0f, local.y + 0.08f, 0.08f);
                }
            }

            // Model geometry must never collide - the CharacterController is the astronaut's only collider.
            foreach (var col in instance.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(col);

            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            Selection.activeGameObject = root.gameObject;

            bool armsOk = visual.leftArm != null && visual.rightArm != null;
            Debug.Log($"[ModelImportTools] Swapped in '{model.name}'. " +
                      (armsOk
                          ? "Arm bones bound - press Play and walk with WASD."
                          : "WARNING: arm bones NOT found. Run 'Validate Selected FBX' for the reason, or " +
                            "assign them by hand on Astronaut > Astronaut Locomotion Visual."), root);
        }

        [MenuItem("Tools/NASA Sim/Astronaut/Rotate Model 90 (fix facing)")]
        public static void RotateAstronautModel()
        {
            var controller = Object.FindAnyObjectByType<AstronautController>();
            var model = controller != null ? controller.transform.Find("Astronaut_Model") : null;
            if (model == null)
            {
                EditorUtility.DisplayDialog("No swapped model",
                    "Swap in an astronaut FBX first (Tools > NASA Sim > Astronaut > Swap In Selected FBX).", "OK");
                return;
            }
            Undo.RecordObject(model, "Rotate Astronaut Model");
            model.localRotation *= Quaternion.Euler(0f, 90f, 0f);
            EditorSceneManager.MarkSceneDirty(model.gameObject.scene);
            Debug.Log($"[ModelImportTools] Astronaut model yaw is now {model.localEulerAngles.y:0} deg. " +
                      "Run again until it faces the way it walks.");
        }

        // ================================================================== biodome

        [MenuItem("Tools/NASA Sim/Biodome/Wire Up Selected Model")]
        public static void WireUpBiodome()
        {
            GameObject target = Selection.activeGameObject;
            if (target == null)
            {
                EditorUtility.DisplayDialog("Nothing selected",
                    "Select the imported biodome in the HIERARCHY (drag the FBX into the scene first).", "OK");
                return;
            }
            if (!target.scene.IsValid())
            {
                EditorUtility.DisplayDialog("Select the scene object",
                    "That is a Project-window asset. Drag the biodome FBX into the scene, then select it " +
                    "in the Hierarchy and run this again.", "OK");
                return;
            }

            WireUp(target);
        }

        /// <summary>
        /// Core prefix wiring (COL_/STAIR_/NOCOL_/BALCONY_/SPAWN_), extracted from the menu item so other
        /// tools (<see cref="BiodomeFixTools"/>, the full-vision setup chain) can call it programmatically.
        /// </summary>
        public static void WireUp(GameObject target)
        {
            Undo.RegisterFullObjectHierarchyUndo(target, "Wire Up Biodome");

            var all = target.GetComponentsInChildren<Transform>(true);
            int solid = 0, stairs = 0, skipped = 0, prefixed = 0;
            Transform spawn = null;
            var log = new StringBuilder();

            foreach (var t in all)
            {
                string n = t.name;
                if (n.StartsWith(NoColliderPrefix))
                {
                    prefixed++; skipped++;
                    StripColliders(t.gameObject);
                }
                else if (n.StartsWith(StairPrefix))
                {
                    prefixed++; stairs++;
                    AddMeshCollider(t.gameObject);
                    if (BuildRampFor(t)) log.AppendLine($"  ramp generated over '{n}'");
                    else log.AppendLine($"  '{n}' has no renderer bounds - ramp SKIPPED, add one by hand");
                }
                else if (n.StartsWith(ColliderPrefix) || n.StartsWith(BalconyPrefix))
                {
                    prefixed++; solid++;
                    AddMeshCollider(t.gameObject);
                }
                else if (n.StartsWith(SpawnPrefix))
                {
                    prefixed++;
                    spawn = t;
                }
            }

            // Fallback: an FBX with no naming convention still gets made walkable, just less precisely.
            if (prefixed == 0)
            {
                foreach (var r in target.GetComponentsInChildren<MeshRenderer>(true))
                {
                    AddMeshCollider(r.gameObject);
                    solid++;
                }
                log.AppendLine($"  No COL_/STAIR_/NOCOL_ prefixes found, so a MeshCollider was added to all " +
                               $"{solid} meshes. Everything is solid, including glass and foliage, and stairs " +
                               $"have no smooth ramp (expect jitter climbing them). See IMPORT_GUIDE.md to " +
                               $"name things and re-run.");
            }

            if (spawn != null)
            {
                var astronaut = Object.FindAnyObjectByType<AstronautController>();
                if (astronaut != null)
                {
                    astronaut.Teleport(spawn.position, Quaternion.Euler(0f, spawn.eulerAngles.y, 0f));
                    log.AppendLine($"  Astronaut moved to '{spawn.name}'.");
                }
            }

            EditorSceneManager.MarkSceneDirty(target.scene);
            Debug.Log($"[ModelImportTools] Wired '{target.name}': {solid} solid, {stairs} staircase(s), " +
                      $"{skipped} pass-through.\n{log}", target);
        }

        /// <summary>
        /// Generate the invisible smooth ramp over an imported staircase - the same trick the primitive
        /// staircase uses. Without it a CharacterController snags on every tread edge. The ramp spans the
        /// stair's bounds diagonally, sloping along whichever horizontal axis is longer.
        /// </summary>
        static bool BuildRampFor(Transform stair)
        {
            if (!TryGetRendererBounds(stair.gameObject, out Bounds b) || b.size.y < 1e-3f) return false;

            const string rampName = "AutoStairRamp";
            var existing = stair.Find(rampName);
            if (existing != null) Object.DestroyImmediate(existing.gameObject);

            var ramp = new GameObject(rampName);
            ramp.transform.SetParent(stair, worldPositionStays: true);

            bool alongZ = b.size.z >= b.size.x;
            float run = alongZ ? b.size.z : b.size.x;
            float rise = b.size.y;
            float width = (alongZ ? b.size.x : b.size.z) * 0.96f;
            float length = Mathf.Sqrt(run * run + rise * rise);
            float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
            const float thickness = 0.5f;

            // Slope upward along +Z (or +X). If your stairs descend that way instead, flip the sign on
            // this rotation in the Inspector - the ramp is a plain child object you can rotate by hand.
            Quaternion rot = alongZ
                ? Quaternion.Euler(-angle, 0f, 0f)
                : Quaternion.Euler(0f, 90f, 0f) * Quaternion.Euler(-angle, 0f, 0f);

            Vector3 mid = new Vector3(b.center.x, b.center.y, b.center.z);
            ramp.transform.SetPositionAndRotation(mid - (rot * Vector3.up) * (thickness * 0.5f), rot);
            ramp.transform.localScale = Vector3.one;

            var col = ramp.AddComponent<BoxCollider>();
            col.size = new Vector3(width, thickness, length);

            if (angle > 50f)
                Debug.LogWarning($"[ModelImportTools] '{stair.name}' slopes at {angle:0.#} deg, steeper than the " +
                                 "astronaut's 50 deg Slope Limit. Raise Slope Limit on the Astronaut's " +
                                 "CharacterController, or make the staircase longer.", stair);
            return true;
        }

        // ================================================================== helpers

        static void AddMeshCollider(GameObject go)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return;
            if (go.GetComponent<Collider>() != null) return;
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mf.sharedMesh;
            mc.convex = false;   // static level geometry: non-convex is exact and cheaper
        }

        static void StripColliders(GameObject go)
        {
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
        }

        static GameObject GetSelectedModel(out string path)
        {
            path = null;
            var go = Selection.activeObject as GameObject;
            if (go == null)
            {
                EditorUtility.DisplayDialog("Select a model",
                    "Select the imported FBX in the PROJECT window (not the Hierarchy), then run this again.", "OK");
                return null;
            }
            path = AssetDatabase.GetAssetPath(go);
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog("Select the asset",
                    "That looks like a scene object. Select the FBX asset in the Project window instead.", "OK");
                return null;
            }
            return go;
        }

        static bool TryGetRendererBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        static string Describe(Transform t) => t != null ? t.name : "-- not found --";
    }
}
