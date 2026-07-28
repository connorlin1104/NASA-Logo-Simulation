using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds the interaction layer: the screen-space prompt canvas (one legacy-UGUI label — TMP's
    /// essential resources are not imported), the <see cref="InteractionSensor"/> +
    /// <see cref="HandActionController"/> on the astronaut, and the first-person camera defaults.
    /// Idempotent: find-or-create by name, safe to re-run.
    /// </summary>
    public static class InteractionSetupTool
    {
        [MenuItem("Tools/NASA Sim/Setup/Add Interaction System && UI")]
        public static void AddInteractionSystem()
        {
            // ---- Prompt UI ----
            var uiRoot = GameObject.Find("UI");
            if (uiRoot == null) uiRoot = new GameObject("UI");

            var canvasGo = FindOrCreateChild(uiRoot.transform, "InteractionPromptCanvas");
            var canvas = GetOrAdd<Canvas>(canvasGo);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = GetOrAdd<CanvasScaler>(canvasGo);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            // No GraphicRaycaster / EventSystem: the prompt is display-only, nothing is ever clicked.

            var labelGo = FindOrCreateChild(canvasGo.transform, "PromptLabel");
            var text = GetOrAdd<Text>(labelGo);
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 28;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.text = string.Empty;
            var outline = GetOrAdd<Outline>(labelGo);
            outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
            var rt = labelGo.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);   // bottom-center of the screen
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 120f);
            rt.sizeDelta = new Vector2(700f, 44f);

            var promptUi = GetOrAdd<InteractionPromptUI>(canvasGo);
            promptUi.label = text;

            // ---- Sensor + eat sequence on the astronaut ----
            var astronaut = Object.FindAnyObjectByType<AstronautController>();
            if (astronaut != null)
            {
                var sensor = GetOrAdd<InteractionSensor>(astronaut.gameObject);
                sensor.astronaut = astronaut;
                sensor.origin = astronaut.transform.Find("Head");
                sensor.interactableMask = NasaLayers.InteractableMask;
                sensor.radius = 2f;

                var hand = GetOrAdd<HandActionController>(astronaut.gameObject);
                hand.astronaut = astronaut;
                hand.locomotion = astronaut.GetComponent<AstronautLocomotionVisual>();
                hand.headAnchor = astronaut.transform.Find("Head");
            }
            else
            {
                Debug.LogWarning("[InteractionSetup] No astronaut in the scene — run " +
                                 "Tools > NASA Sim > Add Astronaut & Balcony To Scene first, then re-run this.");
            }

            // ---- Camera defaults: first person, and the sim no longer steals the camera on Start ----
            var rig = Object.FindAnyObjectByType<AstronautCameraRig>();
            if (rig != null) rig.mode = AstronautCameraRig.ViewMode.FirstPerson;
            var sim = Object.FindAnyObjectByType<SimulationManager>();
            if (sim != null) sim.frameOnStart = false;

            EditorSceneManager.MarkSceneDirty(uiRoot.scene);
            Debug.Log("[InteractionSetup] Prompt UI + InteractionSensor ready. Interact key: E. " +
                      "C toggles first-person / fly-cam. F1 hides the prompts for recording.", uiRoot);
        }

        // ================================================================== recording mode

        [MenuItem("Tools/NASA Sim/Setup/Hide The [E] Prompts (Recording)")]
        public static void HidePrompts() => SetPrompts(false);

        [MenuItem("Tools/NASA Sim/Setup/Show The [E] Prompts Again")]
        public static void ShowPrompts() => SetPrompts(true);

        /// <summary>
        /// Silences the captions without touching what you can interact with. The sensor still finds
        /// everything in range and E still fires — the label simply never turns on, which is what a
        /// screen recording wants and what disabling the canvas or the sensor would NOT give you.
        /// </summary>
        static void SetPrompts(bool visible)
        {
            var ui = Object.FindAnyObjectByType<InteractionPromptUI>(FindObjectsInactive.Include);
            if (ui == null)
            {
                Debug.LogWarning("[InteractionSetup] No prompt UI in the scene. Run " +
                                 "Tools > NASA Sim > Setup > Add Interaction System & UI first.");
                return;
            }

            Undo.RecordObject(ui, visible ? "Show prompts" : "Hide prompts");
            ui.showPrompts = visible;
            if (!visible && ui.label != null)
            {
                Undo.RecordObject(ui.label, "Hide prompts");
                ui.label.enabled = false;
            }

            EditorUtility.SetDirty(ui);
            EditorSceneManager.MarkSceneDirty(ui.gameObject.scene);
            Debug.Log(visible
                ? "[InteractionSetup] Prompts are back on."
                : "[InteractionSetup] Prompts hidden. Proximity and E still work exactly as before — " +
                  "there is just nothing on screen. F1 toggles it in play mode too.", ui);
        }

        static GameObject FindOrCreateChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) return t.gameObject;
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            return go;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }
    }
}
