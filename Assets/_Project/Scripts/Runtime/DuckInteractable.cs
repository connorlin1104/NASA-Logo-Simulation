using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// "[E] Pat duck". Lives on the duck root next to its <see cref="WaterWanderer"/> and trigger
    /// collider (Interactable layer); the pat hands off to the wanderer's reaction.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DuckInteractable : MonoBehaviour, IInteractable
    {
        public WaterWanderer wanderer;

        void Awake()
        {
            if (wanderer == null) wanderer = GetComponent<WaterWanderer>();
        }

        public string Prompt => "[E] Pat duck";

        public bool CanInteract(InteractionSensor sensor) => wanderer != null && !wanderer.IsReacting;

        public void Interact(InteractionSensor sensor) =>
            wanderer.ReactToPat(sensor.astronaut != null ? sensor.astronaut.transform.position
                                                         : sensor.transform.position);
    }
}
