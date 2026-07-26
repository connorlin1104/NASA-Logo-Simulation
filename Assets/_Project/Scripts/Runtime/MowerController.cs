using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// The mowing visual, abstracted so how the cut is DRAWN (Milestone 1 laid a flat ribbon on the
    /// floor; <see cref="MowingVisual_GrassAndFlowers"/> now cuts real blades) can change without
    /// touching the tractor or CSV code.
    /// </summary>
    public interface IMowingVisual
    {
        /// <summary>Pen down starts a new mowed stroke; pen up ends the current one (no connector line).</summary>
        void SetPenDown(bool down);

        /// <summary>Called every frame with the mower brush world position (drives the active stroke / paint).</summary>
        void UpdateAt(Vector3 worldPosition);

        /// <summary>Clear all drawn strokes (used when the run restarts).</summary>
        void ResetVisual();
    }

    /// <summary>
    /// Thin facade the <see cref="TractorPathFollower"/> talks to. Forwards every mow event to ALL
    /// <see cref="IMowingVisual"/> implementations found — the explicitly assigned ones plus anything on
    /// this GameObject or its children — so several visuals can run side by side without either knowing
    /// about the other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MowerController : MonoBehaviour
    {
        [Tooltip("A component implementing IMowingVisual (e.g. MowingVisual_GrassAndFlowers). Kept as a " +
                 "single slot for scene compatibility; anything else implementing IMowingVisual on this " +
                 "object or its children is picked up automatically as well.")]
        [SerializeField] MonoBehaviour visualBehaviour;
        [Tooltip("Additional IMowingVisual components. Every entry (and every implementation found in " +
                 "children) receives every mow event.")]
        [SerializeField] MonoBehaviour[] visualBehaviours = new MonoBehaviour[0];

        readonly System.Collections.Generic.List<IMowingVisual> _visuals =
            new System.Collections.Generic.List<IMowingVisual>();
        bool _resolveAttempted;

        void Awake() => Resolve();

        void Resolve()
        {
            _resolveAttempted = true;         // resolve (and warn) at most once, not every frame
            _visuals.Clear();
            AddVisual(visualBehaviour as IMowingVisual);
            if (visualBehaviours != null)
                foreach (var b in visualBehaviours)
                    AddVisual(b as IMowingVisual);
            foreach (var v in GetComponentsInChildren<IMowingVisual>(true))
                AddVisual(v);

            if (_visuals.Count == 0)
                Debug.LogWarning("[MowerController] No IMowingVisual assigned or found in children.", this);
        }

        void AddVisual(IMowingVisual v)
        {
            if (v != null && !_visuals.Contains(v)) _visuals.Add(v);
        }

        void EnsureResolved()
        {
            if (_visuals.Count == 0 && !_resolveAttempted) Resolve();
        }

        public void SetPenDown(bool down)
        {
            EnsureResolved();
            for (int i = 0; i < _visuals.Count; i++) _visuals[i].SetPenDown(down);
        }

        public void UpdateAt(Vector3 worldPosition)
        {
            EnsureResolved();
            for (int i = 0; i < _visuals.Count; i++) _visuals[i].UpdateAt(worldPosition);
        }

        public void ResetVisual()
        {
            EnsureResolved();
            for (int i = 0; i < _visuals.Count; i++) _visuals[i].ResetVisual();
        }
    }
}
