using UnityEngine;
using UnityEngine.UI;

namespace NasaSim
{
    /// <summary>
    /// The single on-screen interaction prompt ("[E] Open outer door"). A plain legacy-UGUI Text —
    /// TextMeshPro's essential resources are not imported in this project, and one label doesn't
    /// justify adding them. Built by Tools &gt; NASA Sim &gt; Setup &gt; Add Interaction System &amp; UI.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionPromptUI : MonoBehaviour
    {
        public static InteractionPromptUI Instance { get; private set; }

        public Text label;

        void Awake()
        {
            Instance = this;
            Hide();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Show(string prompt)
        {
            if (label == null) return;
            if (!label.enabled) label.enabled = true;
            if (label.text != prompt) label.text = prompt;
        }

        public void Hide()
        {
            if (label != null && label.enabled) label.enabled = false;
        }
    }
}
