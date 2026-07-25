using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// "[E] Pick fruit". Created at runtime by <see cref="FruitSpawner"/> on each FRUIT_ marker; the
    /// interaction hands the fruit to the astronaut's <see cref="FruitEatController"/>, which runs the
    /// reach / bring-to-helmet / bite sequence and reports back to the spawner for the regrow.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FruitInteractable : MonoBehaviour, IInteractable
    {
        [HideInInspector] public FruitSpawner spawner;

        public string Prompt => "[E] Pick fruit";

        public bool CanInteract(InteractionSensor sensor)
        {
            if (!gameObject.activeInHierarchy) return false;
            var eat = sensor.GetComponent<FruitEatController>();
            return eat != null && !eat.IsBusy;
        }

        public void Interact(InteractionSensor sensor)
        {
            var eat = sensor.GetComponent<FruitEatController>();
            if (eat != null) eat.BeginEat(this);
        }
    }
}
