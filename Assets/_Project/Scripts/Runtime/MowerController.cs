using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// The mowing visual, abstracted so the flat TrailRenderer used in Milestone 1 can be swapped for a
    /// RenderTexture grass-paint implementation later without touching the tractor or CSV code.
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
    /// Thin facade the <see cref="TractorPathFollower"/> talks to. Forwards to an <see cref="IMowingVisual"/>
    /// implementation (assigned, or found on this GameObject / its children).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MowerController : MonoBehaviour
    {
        [Tooltip("A component implementing IMowingVisual (e.g. MowingVisual_Trail). If empty, one is searched for on this object.")]
        [SerializeField] MonoBehaviour visualBehaviour;

        IMowingVisual _visual;
        bool _resolveAttempted;

        void Awake() => Resolve();

        void Resolve()
        {
            _resolveAttempted = true;         // resolve (and warn) at most once, not every frame
            _visual = visualBehaviour as IMowingVisual;
            if (_visual == null) _visual = GetComponentInChildren<IMowingVisual>(true);
            if (_visual == null)
                Debug.LogWarning("[MowerController] No IMowingVisual assigned or found in children.", this);
        }

        void EnsureResolved()
        {
            if (_visual == null && !_resolveAttempted) Resolve();
        }

        public void SetPenDown(bool down)
        {
            EnsureResolved();
            _visual?.SetPenDown(down);
        }

        public void UpdateAt(Vector3 worldPosition)
        {
            EnsureResolved();
            _visual?.UpdateAt(worldPosition);
        }

        public void ResetVisual()
        {
            EnsureResolved();
            _visual?.ResetVisual();
        }
    }
}
