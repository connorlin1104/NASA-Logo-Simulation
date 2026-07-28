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
    /// <b>Lilypad</b> — floats like a duck but does not swim: it turns on the spot and creeps with the
    /// current, so a pond full of them drifts instead of sitting frozen. It shares the duck's float code
    /// on purpose; a pad that ignored the swell while the ducks rode it would give the wave away.
    ///
    /// Whatever the shape of the water is, they stay in it AND keep swimming in it. Each one wanders to
    /// somewhere near itself rather than anywhere in the body (so nothing in the square moat sets off for
    /// the far side across dry land), it looks a stride ahead and turns off the banks before it reaches
    /// them (<see cref="SteerAroundBanks"/>), it abandons a leg it cannot finish, and only then is a step
    /// clamped back inside. Anything that still finds itself on land — the usual result of resizing a pond
    /// around it — is put back on <see cref="Start"/>.
    ///
    /// Runs on UNSCALED time: wildlife is watched up close by the player, and 16x scaled ducks would zip
    /// comically (see <see cref="AstronautController"/> for the project convention).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WaterWanderer : MonoBehaviour
    {
        // Appended, never reordered: scenes serialise the enum by index, so inserting Lilypad anywhere
        // but the end would turn every saved fish into a lilypad.
        public enum Mode { Duck, Fish, Lilypad }

        /// <summary>
        /// Degrees to turn the model so its nose points the way it is swimming.
        ///
        /// This steers along +Z, but Duck.fbx and Fish.fbx are both modelled facing their own <b>−X</b> —
        /// the fish's body runs 10 units along X with the eyes at the low end and the tail fin at the
        /// high end — so without this they swim sideways. Turning the visual instead of the heading keeps
        /// the movement maths honest: the creature still travels along +Z and only the picture is rotated.
        ///
        /// 90 is right for the models in this project. If a future one faces the other way, this is the
        /// one number to change (and 180 flips a model that swims backwards).
        /// </summary>
        [Header("Which way the model faces")]
        [Range(-180f, 180f)] public float modelYaw = 90f;

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
        [Tooltip("How far away it may pick its next spot (m). This keeps a fish in its own stretch of the " +
                 "moat instead of setting off for the far side across dry land. 0 = anywhere in the body.")]
        [Min(0f)] public float wanderRange = 9f;
        [Tooltip("Give up on a leg it has not finished after this long (s) and pick somewhere else.")]
        [Min(0.5f)] public float legTimeout = 9f;

        [Header("Banks")]
        [Tooltip("How far ahead it looks for the bank (m). About a second of swimming is right: shorter " +
                 "and it turns with its nose already in the mud, longer and it flinches at open water.")]
        [Min(0.1f)] public float lookAhead = 1.4f;

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

        [Header("Lilypad")]
        [Tooltip("How fast it turns on the spot (deg/sec). Slow — a pad rotates, it does not steer.")]
        [Range(-20f, 20f)] public float spinDegPerSec = 3f;
        [Tooltip("How far it creeps with the current (m/sec). Tiny; it is drift, not swimming.")]
        [Min(0f)] public float driftSpeed = 0.04f;

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
        float _legTimer;                    // how long this leg has been going
        float _stallTimer;                  // how long it has been getting nowhere
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
            FaceTarget();
        }

        /// <summary>
        /// Point the heading down the first leg. Taking it from the transform instead — which is what the
        /// spawner leaves lying around — folds the model's own <see cref="modelYaw"/> offset into the
        /// heading, and sends the creature off at right angles to the way it is facing.
        /// </summary>
        void FaceTarget()
        {
            Vector3 to = _target - transform.position;
            to.y = 0f;
            if (to.sqrMagnitude > 1e-4f) _yaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || water == null) return;

            if (mode == Mode.Lilypad)
            {
                // No target, no arrival, no pause: a pad turns where it is and slides with the current.
                _yaw += spinDegPerSec * dt;
                if (driftSpeed > 0f)
                {
                    // The drift heading weaves on the same slow sine the others sway on, so a raft of
                    // pads all lean the same way at once — which is what a current looks like.
                    float heading = Mathf.Sin((Time.unscaledTime + _phase) * swayFrequency * Mathf.PI) * 180f;
                    Vector3 current = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
                    transform.position = water.ClampInside(transform.position + current * (driftSpeed * dt),
                                                           bankMargin);
                }
                ApplyFloat(dt);
                return;
            }

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

            _legTimer += dt;

            Vector3 to = _target - transform.position;
            to.y = 0f;
            // The timeout matters as much as the arrival: a target on the far bank of a moat is one it can
            // swim at for the rest of the scene's life without ever getting there.
            if (to.magnitude <= arriveDistance || _legTimer >= legTimeout)
            {
                _pauseTimer = Random.Range(idlePauseRange.x, idlePauseRange.y);
                PickNewTarget();
                ApplyFloat(dt);
                return;
            }

            // Where it would LIKE to go: the bearing to the target, plus a slow weave so nothing swims in a
            // dead straight line. Where it actually goes is whatever of that the banks leave room for.
            float want = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg
                       + Mathf.Sin((Time.unscaledTime + _phase) * swayFrequency * Mathf.PI * 2f) * swayDeg;
            float stride = Mathf.Max(lookAhead, speedNow * 1.2f);
            _yaw = Mathf.MoveTowardsAngle(_yaw, SteerAroundBanks(want, stride), turnRateDeg * dt);

            Vector3 fwd = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
            Vector3 from = transform.position;
            Vector3 next = water.ClampInside(from + fwd * (speedNow * dt), bankMargin);

            // The clamp is the last line of defence, and it must never read as the creature swimming in
            // REVERSE: if pushing the step back into the water has undone the step itself, stay put and
            // spend the frame turning instead. (Unless it is somehow ashore, where being put back matters
            // more than how it looks getting there.)
            if (Vector3.Dot(next - from, fwd) > 0f || water.ClearanceAt(from) <= 0f) transform.position = next;

            // Pinned against something the fan could not steer it off — a corner it was shoved into, a
            // pond resized around it. Stop leaning on the bank and go somewhere else.
            float crawl = speedNow * dt * 0.3f;
            if ((transform.position - from).sqrMagnitude < crawl * crawl)
            {
                _stallTimer += dt;
                if (_stallTimer >= 0.75f) PickNewTarget();
            }
            else _stallTimer = 0f;

            ApplyFloat(dt);
        }

        // The turns tried each frame, in degrees off the way it wants to go. Ordered outward from straight
        // on, so a tie goes to the smallest correction; the ±150 pair is what turns it around at a dead end.
        static readonly float[] Deflections =
            { 0f, 20f, -20f, 42f, -42f, 66f, -66f, 92f, -92f, 122f, -122f, 150f, -150f };

        /// <summary>
        /// Choose a heading that goes roughly where we want AND has water in front of it.
        ///
        /// Steering straight at the target is what beached them. A fish in the square moat would pick a
        /// point on the far side, swim into the inner bank, and be clamped against it — sliding along the
        /// wall until a corner took even that away, at which point it sat nose-first in the mud for good,
        /// because arriving at a target was the only thing that ever chose a new one.
        ///
        /// So it looks first: a fan of turns off the direction it wants, the water sampled a stride along
        /// each, and the best compromise between "clear" and "the way I meant to go" wins. That one rule
        /// covers every quadrant of the moat and every corner in it — when the way ahead closes up, the way
        /// round is already in the fan.
        /// </summary>
        float SteerAroundBanks(float wantYaw, float stride)
        {
            // Room past this is all equally good: the job is to find a clear heading, not to hug the exact
            // centre line of the band. Penalties are scaled by it so they stay meaningful at any margin.
            float enough = Mathf.Max(0.2f, bankMargin * 2f);
            Vector3 here = transform.position;

            float bestYaw = wantYaw;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < Deflections.Length; i++)
            {
                float yaw = wantYaw + Deflections[i];
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                // Two samples, so it commits to a turn a stride early rather than at the last moment.
                float room = Mathf.Min(water.ClearanceAt(here + dir * (stride * 0.5f)),
                                       water.ClearanceAt(here + dir * stride));

                float score = Mathf.Min(room, enough)
                            - Mathf.Abs(Deflections[i]) * (0.0016f * enough)                 // go where it meant to
                            - Mathf.Abs(Mathf.DeltaAngle(yaw, _yaw)) * (0.0010f * enough);   // without whipping round
                if (score <= bestScore) continue;
                bestScore = score;
                bestYaw = yaw;
            }
            return bestYaw;
        }

        /// <summary>Sit on (or under) the water, and lean with it.</summary>
        void ApplyFloat(float dt)
        {
            Vector3 p = transform.position;
            float surface = water.SurfaceHeightAt(p);

            // Ducks and lilypads both sit ON the water and lean with it; only fish live under it.
            if (mode != Mode.Fish)
            {
                float bob = Mathf.Sin((Time.unscaledTime + _phase) * bobFrequency * Mathf.PI * 2f) * bobAmplitude;
                p.y = surface - floatDepth + bob;
                transform.position = p;

                // Lean along the wave's own slope, eased in so the duck rocks rather than snaps.
                Vector3 want = Vector3.Slerp(Vector3.up, water.SurfaceNormalAt(p), waveTilt);
                _up = Vector3.Slerp(_up, want, 1f - Mathf.Exp(-tiltResponse * dt));
                transform.rotation = Quaternion.FromToRotation(Vector3.up, _up)
                                   * Quaternion.Euler(0f, _yaw + modelYaw, 0f);
            }
            else
            {
                float floor = surface - Mathf.Max(0.05f, water.depth - 0.1f);
                p.y = Mathf.MoveTowards(p.y, Mathf.Max(floor, surface - _targetDepth), 0.3f * dt);
                transform.position = p;

                float waggle = Mathf.Sin((Time.unscaledTime + _phase) * waggleFrequency * Mathf.PI * 2f) * waggleDeg;

                // Rolled about the direction of TRAVEL, not about the transform's own Z. Euler(0, y, z)
                // applies the roll in the model's local frame, and this fish's local Z is its lateral
                // axis — so once modelYaw turns it nose-first, that same roll becomes the fish pitching
                // its nose up and down twice a second. Banking around the line it is swimming along is
                // what the waggle was always meant to be, and it does not care how the model is built.
                Vector3 travel = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
                transform.rotation = Quaternion.AngleAxis(waggle, travel)
                                   * Quaternion.Euler(0f, _yaw + modelYaw, 0f);
            }
        }

        void PickNewTarget()
        {
            if (water == null) return;
            _target = wanderRange > 0f ? water.RandomPointNear(transform.position, wanderRange, bankMargin)
                                       : water.RandomPointOnSurface(bankMargin);
            if (mode == Mode.Fish) _targetDepth = PickDepth();
            _legTimer = 0f;
            _stallTimer = 0f;
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
            PickNewTarget();
            FaceTarget();
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
