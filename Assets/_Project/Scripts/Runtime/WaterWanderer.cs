using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// Autonomous pond wildlife, transform-driven (no physics, like the tractor). One script, two modes:
    ///
    /// <b>Duck</b> — floats ON the water: it reads the height and the tilt of the wave actually being
    /// drawn underneath it (<see cref="WaterBody.SurfaceHeightAt"/>), so it rides the swell and leans with
    /// it rather than bobbing to a sine of its own. It paddles to random points on the water, weaving
    /// gently as it goes, pauses to idle, and reacts to being patted (faces the player, happy wiggle,
    /// quack hook). <b>Fish</b> — cruises a depth band below the surface with a tail-driven waggle, and
    /// occasionally darts.
    ///
    /// Whatever the shape of the water is, they stay in it: the wander target is drawn from inside the
    /// body, every step is clamped back into it, and anything that finds itself on dry land — the usual
    /// result of resizing a pond around it — is put back on <see cref="Start"/>.
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
        [Tooltip("Keep this far off the bank when picking somewhere to go (m).")]
        [Min(0f)] public float bankMargin = 0.6f;

        [Header("Sway")]
        [Tooltip("How far the heading weaves off course as it paddles (degrees).")]
        [Range(0f, 40f)] public float swayDeg = 7f;
        [Tooltip("Weaves per second. Slow reads as drifting, fast as flustered.")]
        [Min(0.01f)] public float swayFrequency = 0.35f;

        [Header("Duck")]
        [Tooltip("How deep it sits in the water (m) — how far its origin rides below the waterline.")]
        public float floatDepth = 0.02f;
        [Tooltip("Extra bob on top of the wave it is riding (m). The wave alone is often enough.")]
        [Min(0f)] public float bobAmplitude = 0.015f;
        [Min(0.01f)] public float bobFrequency = 0.7f;
        [Tooltip("How much of the wave's own tilt it takes on. 1 = lies flat along the surface.")]
        [Range(0f, 2f)] public float waveTilt = 1f;
        [Tooltip("How quickly it settles onto a new tilt. Low is sluggish and buoyant.")]
        [Min(0.5f)] public float tiltResponse = 3.5f;

        [Header("Fish")]
        [Tooltip("Swim depth below the surface (m, min..max). Clamped to the body's own depth.")]
        public Vector2 depthBand = new Vector2(0.25f, 0.6f);
        [Tooltip("Tail waggle: how far the body rolls side to side (degrees).")]
        [Range(0f, 45f)] public float waggleDeg = 14f;
        [Min(0.1f)] public float waggleFrequency = 2.2f;
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
        float _yaw;                         // steered here, so the wave tilt can be composed on top
        float _phase;                       // random per-instance so a flock never bobs in sync
        Vector3 _up = Vector3.up;

        void Start()
        {
            _phase = Random.value * 100f;
            if (water == null) water = GetComponentInParent<WaterBody>();
            _yaw = transform.eulerAngles.y;
            if (mode == Mode.Fish) _targetDepth = PickDepth();

            // A pond that was resized (or an animal dropped in by hand) can leave it on the lawn.
            if (water != null && !water.Contains(transform.position, 0.05f)) SnapIntoWater();
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
                ApplyFloat(dt);
                return;
            }

            if (_pauseTimer > 0f)
            {
                _pauseTimer -= dt;
                ApplyFloat(dt);
                return;
            }

            float speedNow = speed;
            if (mode == Mode.Fish)
            {
                if (_dartTimer > 0f) { _dartTimer -= dt; speedNow *= dartSpeedMul; }
                else if (Random.value < dartChancePerSec * dt) _dartTimer = dartSeconds;
            }

            Vector3 to = _target - transform.position;
            to.y = 0f;
            if (to.magnitude <= arriveDistance)
            {
                _pauseTimer = Random.Range(idlePauseRange.x, idlePauseRange.y);
                PickNewTarget();
                ApplyFloat(dt);
                return;
            }

            // Steer toward the target, plus a slow weave so nothing swims in a dead straight line.
            float want = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg
                       + Mathf.Sin((Time.unscaledTime + _phase) * swayFrequency * Mathf.PI * 2f) * swayDeg;
            _yaw = Mathf.MoveTowardsAngle(_yaw, want, turnRateDeg * dt);

            Vector3 fwd = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            transform.position = water.ClampInside(transform.position + fwd * (speedNow * dt), bankMargin);

            ApplyFloat(dt);
        }

        /// <summary>Sit on (or under) the water, and lean with it.</summary>
        void ApplyFloat(float dt)
        {
            Vector3 p = transform.position;
            float surface = water.SurfaceHeightAt(p);

            if (mode == Mode.Duck)
            {
                float bob = Mathf.Sin((Time.unscaledTime + _phase) * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
                p.y = surface - floatDepth + bob;
                transform.position = p;

                // Lean along the wave's own slope, eased in so the duck rocks rather than snaps.
                Vector3 want = Vector3.Slerp(Vector3.up, water.SurfaceNormalAt(p), waveTilt);
                _up = Vector3.Slerp(_up, want, 1f - Mathf.Exp(-tiltResponse * dt));
                transform.rotation = Quaternion.FromToRotation(Vector3.up, _up)
                                   * Quaternion.Euler(0f, _yaw, 0f);
            }
            else
            {
                float floor = surface - Mathf.Max(0.05f, water.depth - 0.1f);
                p.y = Mathf.MoveTowards(p.y, Mathf.Max(floor, surface - _targetDepth), 0.3f * dt);
                transform.position = p;

                float waggle = Mathf.Sin((Time.unscaledTime + _phase) * waggleFrequency * Mathf.PI * 2f) * waggleDeg;
                transform.rotation = Quaternion.Euler(0f, _yaw, waggle);
            }
        }

        void PickNewTarget()
        {
            if (water == null) return;
            _target = water.RandomPointOnSurface(bankMargin);
            if (mode == Mode.Fish) _targetDepth = PickDepth();
        }

        float PickDepth()
        {
            float max = water != null ? Mathf.Max(0.05f, water.depth - 0.1f) : depthBand.y;
            return Mathf.Min(Random.Range(depthBand.x, depthBand.y), max);
        }

        /// <summary>
        /// Drop this creature somewhere valid in its water body. Used by the editor after a pond is
        /// resized, and on Start by anything that finds itself beached.
        /// </summary>
        public void SnapIntoWater()
        {
            if (water == null) water = GetComponentInParent<WaterBody>();
            if (water == null) return;

            Vector3 p = water.RandomPointOnSurface(bankMargin);
            if (mode == Mode.Fish) p.y = water.SurfaceY - PickDepth();
            transform.position = p;
            _target = water.RandomPointOnSurface(bankMargin);
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
            float want = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg + wiggle;
            _yaw = Mathf.MoveTowardsAngle(_yaw, want, turnRateDeg * 3f * dt);
        }
    }
}
