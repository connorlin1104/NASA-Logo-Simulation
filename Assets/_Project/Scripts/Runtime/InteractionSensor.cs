using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// Lives on the astronaut root. Each frame it finds the nearest <see cref="IInteractable"/> within
    /// <see cref="radius"/> (trigger colliders on the Interactable layer), drives the prompt UI, and
    /// fires the interaction on E.
    ///
    /// Runs on unscaled time semantics like all player-facing systems: it polls every frame regardless
    /// of <c>Time.timeScale</c>, and hides itself while the fly-cam has the astronaut's input disabled
    /// (so E can never pat a duck from across the map).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InteractionSensor : MonoBehaviour
    {
        [Tooltip("Where proximity is measured from — the Head anchor. Falls back to this transform.")]
        public Transform origin;
        [Min(0.1f)] public float radius = 2f;
        [Tooltip("Layers searched for interactables. The setup tool sets this to the Interactable layer; " +
                 "a wider mask still works (non-interactable colliders are filtered out), just costs more.")]
        public LayerMask interactableMask = ~0;
        public AstronautController astronaut;

        /// <summary>The interactable currently in range and prompted, if any.</summary>
        public IInteractable Current { get; private set; }

        readonly Collider[] _hits = new Collider[16];

        void Awake()
        {
            if (astronaut == null) astronaut = GetComponentInParent<AstronautController>();
            if (origin == null) origin = transform;
        }

        void Update()
        {
            bool inputActive = astronaut == null || astronaut.enableInput;
            Current = inputActive ? FindNearest() : null;

            var ui = InteractionPromptUI.Instance;
            if (ui != null)
            {
                if (Current != null) ui.Show(Current.Prompt);
                else ui.Hide();
            }

            if (Current != null && InteractPressed())
                Current.Interact(this);
        }

        IInteractable FindNearest()
        {
            Vector3 p = origin.position;
            // Explicit Collide flag: interactables are triggers, and passing it here makes the sensor
            // immune to the project-wide "Queries Hit Triggers" setting.
            int n = Physics.OverlapSphereNonAlloc(p, radius, _hits, interactableMask,
                                                  QueryTriggerInteraction.Collide);
            IInteractable best = null;
            float bestSq = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = _hits[i];
                if (c == null) continue;
                var interactable = c.GetComponentInParent<IInteractable>();
                if (interactable == null || !interactable.CanInteract(this)) continue;
                float d = (c.transform.position - p).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = interactable;
                }
            }
            return best;
        }

        bool InteractPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.eKey.wasPressedThisFrame;
#else
            return false;
#endif
        }
    }
}
