using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// The wall panel beside each airlock door. Shows what pressing E will do — or that the airlock is
    /// mid-cycle — and starts the matching cycle on interact. Lives on a trigger collider on the
    /// Interactable layer (built by the airlock builder tool).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AirlockButtonInteractable : MonoBehaviour, IInteractable
    {
        public AirlockController airlock;
        [Tooltip("True = the button on the OUTSIDE approach (starts ingress). False = inside (egress).")]
        public bool isOuterButton = true;

        public string Prompt
        {
            get
            {
                if (airlock == null) return string.Empty;
                if (!airlock.CanRequest) return "Airlock cycling…";
                return isOuterButton ? "[E] Open outer door" : "[E] Open inner door";
            }
        }

        // Always visible in range (the prompt doubles as the airlock's status display); Interact simply
        // does nothing while a cycle runs.
        public bool CanInteract(InteractionSensor sensor) => airlock != null;

        public void Interact(InteractionSensor sensor)
        {
            if (airlock != null) airlock.RequestCycle(fromOutside: isOuterButton);
        }
    }
}
