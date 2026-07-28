using UnityEngine;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// The single on-screen interaction prompt ("[E] Open outer door"). A plain legacy-UGUI Text —
    /// TextMeshPro's essential resources are not imported in this project, and one label doesn't
    /// justify adding them. Built by Tools &gt; NASA Sim &gt; Setup &gt; Add Interaction System &amp; UI.
    ///
    /// <b>Recording mode.</b> Turn <see cref="showPrompts"/> off and the label never appears, but nothing
    /// else changes: <see cref="InteractionSensor"/> still finds what you are standing next to and E still
    /// works. The switch is HERE rather than on the sensor deliberately — the sensor is what decides what
    /// you can touch, and a screen-recording setting has no business reaching into that. This only ever
    /// silences the caption.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionPromptUI : MonoBehaviour
    {
        public static InteractionPromptUI Instance { get; private set; }

        public Text label;

        [Header("Recording")]
        [Tooltip("Off hides every prompt without disabling anything. Walking up to a duck and pressing E " +
                 "still pets it; there is just no caption telling you so.")]
        public bool showPrompts = true;

        [Tooltip("Toggle the prompts mid-take with F1, so you don't have to stop and find this object.")]
        public bool f1Toggles = true;

        void Awake()
        {
            Instance = this;
            Hide();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (f1Toggles && TogglePressed()) showPrompts = !showPrompts;
            // Nothing to un-hide here: Show() is called every frame by the sensor while something is in
            // range, so flipping the switch back on re-appears on the very next frame by itself.
            if (!showPrompts) Hide();
        }

        public void Show(string prompt)
        {
            if (label == null) return;
            if (!showPrompts) { Hide(); return; }
            if (!label.enabled) label.enabled = true;
            if (label.text != prompt) label.text = prompt;
        }

        public void Hide()
        {
            if (label != null && label.enabled) label.enabled = false;
        }

        /// <summary>Silence (or restore) the captions from anywhere — a menu item, a cutscene, a hotkey.</summary>
        public static void SetVisible(bool visible)
        {
            if (Instance == null) return;
            Instance.showPrompts = visible;
            if (!visible) Instance.Hide();
        }

        static bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            Keyboard kb = Keyboard.current;
            return kb != null && kb.f1Key.wasPressedThisFrame;
#else
            return false;
#endif
        }
    }
}
