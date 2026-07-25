using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// Autonomous pond wildlife, transform-driven (no physics, like the tractor). One script, two modes:
    ///
    /// <b>Duck</b> — floats at the surface with a gentle bob and buoyant roll, wanders to random points
    /// on the water, pauses to idle, and reacts to being patted (faces the player, happy wiggle, quack
    /// hook). <b>Fish</b> — swims in a depth band below the surface, occasionally darting.
    ///
    /// Runs on UNSCALED time: wildlife is watched up close by the player, and 16x scaled ducks would zip
    /// comically (see <see cref="AstronautController"/> for the project convention).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WaterWanderer : MonoBehaviour
    {
        public enum Mode { Duck, Fish }

        [Header("Wander")]
        public Mode mode = Mode.Duck;
        [Tooltip("The pond/moat this creature lives in. Auto-found from the parent if left empty.")]
        public WaterBody water;
        [Min(0.01f)] public float speed = 0.6f;
        [Min(1f)] public float turnRateDeg = 90f;
        [Tooltip("Idle pause between wander legs (seconds, min..max).")]
        public Vector2 idlePauseRange = new Vector2(1f, 4f);
        [Min(0.05f)] public float arriveDistance = 0.4f;

        [Header("Duck")]
        public float bobAmplitude = 0.04f;
        public float bobFrequency = 1.3f;
        [Tooltip("Buoyant roll while bobbing (degrees).")]
        public float tiltDeg = 6f;

        [Header("Fish")]
        [Tooltip("Swim depth below the surface (m, min..max).")]
        public Vector2 depthBand = new Vector2(0.25f, 0.6f);
        [Range(0f, 1f)] public float dartChancePerSec = 0.15f;
        [Min(1f)] public float dartSpeedMul = 2.5f;
        [Min(0.1f)] public float dartSeconds = 1.2f;

        [Header("Pat reaction (ducks)")]
        [Min(0.5f)] public float patReactSeconds = 2.5f;
        public UnityEvent onPatted;
        public AudioSource audioSource;
        [Tooltip("Optional quack, played on pat.")]
        public AudioClip quackClip;

        public bool IsReacting => _reactTimer > 0f;

        Vector3 _target;
        float _pauseTimer;
        float _reactTimer;
        Vector3 _reactFrom;
        float _dartTimer;
        float _targetDepth;
        float _phase;                       // random per-instance so a flock never bobs in sync

        void Start()
        {
            _phase = Random.value * 100f;
            if (water == null) water = GetComponentInParent<WaterBody>();
            if (mode == Mode.Fish) _targetDepth = Random.Range(depthBand.x, depthBand.y);
            PickNewTarget();
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || water == null) return;

            if (_reactTimer > 0f)
            {
                _reactTimer -= dt;
                UpdatePatReaction(dt);
                ApplyVerticalAndBob(dt);
                return;
            }

            if (_pauseTimer > 0f)
            {
                _pauseTimer -= dt;
                ApplyVerticalAndBob(dt);
                return;
            }

            float speedNow = speed;
            if (mode == Mode.Fish)
            {
                if (_dartTimer > 0f) { _dartTimer -= dt; speedNow *= dartSpeedMul; }
                else if (Random.value < dartChancePerSec * dt) _dartTimer = dartSeconds;
            }

            Vector3 to = _target - transform.position;
            var flatTo = new Vector3(to.x, 0f, to.z);
            if (flatTo.magnitude <= arriveDistance)
            {
                _pauseTimer = Random.Range(idlePauseRange.x, idlePauseRange.y);
                PickNewTarget();
                ApplyVerticalAndBob(dt);
                return;
            }

            var look = Quaternion.LookRotation(flatTo.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnRateDeg * dt);
            Vector3 fwd = transform.forward; fwd.y = 0f;
            transform.position += fwd.normalized * (speedNow * dt);
            transform.position = water.ClampInside(transform.position, 0.4f);

            ApplyVerticalAndBob(dt);
        }

        void ApplyVerticalAndBob(float dt)
        {
            Vector3 p = transform.position;
            if (mode == Mode.Duck)
            {
                float cycle = (Time.unscaledTime + _phase) * bobFrequency * Mathf.PI * 2f;
                p.y = water.SurfaceY + Mathf.Sin(cycle) * bobAmplitude;
                transform.position = p;

                // Buoyant lean: roll follows the bob's derivative so the duck rocks with the water.
                float roll = Mathf.Cos(cycle * 0.8f) * tiltDeg;
                Vector3 e = transform.eulerAngles;
                transform.rotation = Quaternion.Euler(0f, e.y, roll);
            }
            else
            {
                float targetY = water.SurfaceY - _targetDepth;
                p.y = Mathf.MoveTowards(p.y, targetY, 0.3f * dt);
                transform.position = p;
            }
        }

        void PickNewTarget()
        {
            if (water == null) return;
            _target = water.RandomPointOnSurface(0.6f);
            if (mode == Mode.Fish) _targetDepth = Random.Range(depthBand.x, depthBand.y);
        }

        /// <summary>Duck reaction to the pat interaction: face the patter, happy wiggle, quack hook.</summary>
        public void ReactToPat(Vector3 fromWorld)
        {
            _reactTimer = patReactSeconds;
            _reactFrom = fromWorld;
            onPatted?.Invoke();
            if (audioSource != null && quackClip != null) audioSource.PlayOneShot(quackClip);
        }

        void UpdatePatReaction(float dt)
        {
            Vector3 to = _reactFrom - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude < 1e-4f) return;

            float wiggle = Mathf.Sin((patReactSeconds - _reactTimer) * 10f) * 12f
                           * Mathf.Clamp01(_reactTimer / patReactSeconds);
            var look = Quaternion.LookRotation(to.normalized, Vector3.up) * Quaternion.Euler(0f, wiggle, 0f);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnRateDeg * 3f * dt);
        }
    }
}
