namespace NasaSim
{
    /// <summary>
    /// A proximity-gated "press E" interaction (doors, fruit, ducks). Implementations live on or under a
    /// GameObject carrying a trigger collider on the Interactable layer; <see cref="InteractionSensor"/>
    /// finds the nearest one in range, shows its <see cref="Prompt"/>, and calls
    /// <see cref="Interact"/> when E is pressed.
    /// </summary>
    public interface IInteractable
    {
        /// <summary>Prompt line shown while in range, e.g. "[E] Pat duck".</summary>
        string Prompt { get; }

        /// <summary>False hides this interactable from the sensor (busy, cooling down, already used).</summary>
        bool CanInteract(InteractionSensor sensor);

        void Interact(InteractionSensor sensor);
    }
}
