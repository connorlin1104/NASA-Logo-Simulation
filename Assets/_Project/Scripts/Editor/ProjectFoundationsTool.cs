using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Project-wide foundations for the "full vision" feature set: physics layers, the astronaut
    /// root-scale fix, and the one-click chain that runs every setup menu in the right order.
    ///
    /// The scale fix matters because Unity multiplies a CharacterController's capsule — including its
    /// Step Offset — by the transform's scale. The scene astronaut was saved at root scale 0.5, which
    /// silently halved the usable step-up to ~0.17 m and made every real stair riser read as a wall.
    /// Normalizing pushes the 0.5 into the direct children (world appearance identical) so the cc*
    /// fields mean honest world metres again.
    /// </summary>
    public static class ProjectFoundationsTool
    {
        // ------------------------------------------------------------------ layers

        [MenuItem("Tools/NASA Sim/Setup/Configure Layers && Physics")]
        public static void ConfigureLayers()
        {
            bool ok = SetLayerName(NasaLayers.Player, "Player");
            ok &= SetLayerName(NasaLayers.Interactable, "Interactable");

            var astronaut = Object.FindAnyObjectByType<AstronautController>();
            if (astronaut != null)
            {
                SetLayerRecursive(astronaut.transform, NasaLayers.Player);
                EditorSceneManager.MarkSceneDirty(astronaut.gameObject.scene);
            }

            Debug.Log("[Foundations] Layers " + (ok ? "ready" : "PARTIALLY configured (see errors above)") +
                      ": 8 = Player, 9 = Interactable (4 = Water is a Unity built-in)." +
                      (astronaut != null ? " Astronaut hierarchy moved to the Player layer." : ""));
        }

        static bool SetLayerName(int index, string name)
        {
            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            var slot = layers.GetArrayElementAtIndex(index);
            if (slot.stringValue == name) return true;                       // idempotent
            if (!string.IsNullOrEmpty(slot.stringValue))
            {
                Debug.LogError($"[Foundations] Layer {index} is already named '{slot.stringValue}' but " +
                               $"'{name}' is wanted there. Free that slot in Project Settings > Tags and " +
                               "Layers (or change the constant in NasaLayers) and run this again.");
                return false;
            }
            slot.stringValue = name;
            tagManager.ApplyModifiedProperties();
            return true;
        }

        internal static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursive(t.GetChild(i), layer);
        }

        // ------------------------------------------------------------------ astronaut scale

        [MenuItem("Tools/NASA Sim/Setup/Normalize Astronaut Scale")]
        public static void NormalizeAstronautScale()
        {
            var controller = Object.FindAnyObjectByType<AstronautController>();
            if (controller == null)
            {
                Debug.LogWarning("[Foundations] No astronaut in the scene — run " +
                                 "Tools > NASA Sim > Add Astronaut & Balcony To Scene first.");
                return;
            }

            Transform root = controller.transform;
            Vector3 s = root.localScale;
            bool alreadyNormalized = Mathf.Approximately(s.x, 1f) &&
                                     Mathf.Approximately(s.y, 1f) &&
                                     Mathf.Approximately(s.z, 1f);
            if (alreadyNormalized)
            {
                // Still make sure the step-offset fix is in even when the scale needs no work.
                if (controller.ccStepOffset < 0.3f)
                {
                    Undo.RecordObject(controller, "Fix Step Offset");
                    controller.ccStepOffset = 0.3f;
                    controller.ApplyControllerTuning();
                    EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
                    Debug.Log("[Foundations] Astronaut scale already 1; raised Step Offset to 0.30 m.");
                }
                else
                {
                    Debug.Log("[Foundations] Astronaut root is already at scale 1 — nothing to do.");
                }
                return;
            }

            Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Normalize Astronaut Scale");

            // Push the root scale into the DIRECT children (position and scale) so the world appearance
            // is exactly preserved; grandchildren are untouched because their parents carry the change.
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                child.localPosition = Vector3.Scale(child.localPosition, s);
                child.localScale = Vector3.Scale(child.localScale, s);
            }
            root.localScale = Vector3.one;

            // Preserve the current WORLD capsule (Unity was scaling it by the root scale) and take the
            // opportunity to give the step offset its full intended world value — the actual bug fix.
            controller.ccHeight *= s.y;
            controller.ccCenter = Vector3.Scale(controller.ccCenter, s);
            controller.ccRadius *= Mathf.Max(s.x, s.z);
            controller.ccStepOffset = 0.30f;
            controller.ccSkinWidth = 0.02f;
            controller.ApplyControllerTuning();

            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            Debug.Log($"[Foundations] Astronaut root scale {s.x:0.###} -> 1. World appearance unchanged; " +
                      $"capsule is now {controller.ccHeight:0.##} m tall, r = {controller.ccRadius:0.###}, " +
                      "Step Offset = 0.30 m (was effectively " +
                      $"{0.35f * s.y:0.###} m — the reason stairs felt like walls).", root);
        }

        // ------------------------------------------------------------------ one-click chain

        [MenuItem("Tools/NASA Sim/Setup/Run Full Vision Setup")]
        public static void RunFullVisionSetup()
        {
            ConfigureLayers();
            NormalizeAstronautScale();
            NasaModelPostprocessor.ReimportAndRemapAll();
            BiodomeFixTools.FixDomeGlass();
            BiodomeFixTools.WireCollidersAndSpawn();
            InteractionSetupTool.AddInteractionSystem();
            AirlockBuilderTool.BuildDefault();
            ModelImportTools.WireSteerWheelsFromAxles();
            WaterBodyTool.BuildMoatDefault();
            WildlifeSpawnTool.SpawnDefaults();
            GrassFieldTool.ScatterDefault();
            GrassFieldTool.AddGrassMowingVisual();
            FruitTreeTool.AddDefaultTrees();
            // Last: the blade field probes the ground for what it may not grow through, so everything that
            // stands on the field — stairs, pillars, trunks — has to be there first.
            GrassFieldTool.BuildMowableGrass();

            Debug.Log("[Foundations] Full vision setup complete. Two steps still need your eyes:\n" +
                      "  1. Select your ground model and run Tools > NASA Sim > Environment > " +
                      "Bake Simplified Collider so the astronaut can walk the outside terrain.\n" +
                      "  2. For each spiral staircase, select it and run Tools > NASA Sim > Biodome > " +
                      "Add Spiral Stair Ramp (set Turns/Clockwise until the wireframe hugs the treads).");
        }
    }
}
