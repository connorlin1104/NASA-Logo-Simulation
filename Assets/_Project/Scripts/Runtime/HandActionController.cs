using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// The astronaut's right hand: it runs the eat flourish for any <see cref="EatableObject"/> and the
    /// pet flourish for any <see cref="PettableObject"/>. One controller, two sequences, and every object
    /// of a kind animates identically — what differs (bite count, pace, what happens afterwards) lives on
    /// the object.
    ///
    /// <b>Eat</b> — reach out and take it → swing it up to the visor on an arc → one lift-chomp-withdraw-chew
    /// beat per bite, each shrinking it a step and puffing crumbs → lower the empty hand.
    /// <b>Pet</b> — reach out toward the animal → a few strokes, each dropping onto it and squashing it →
    /// lower the hand.
    ///
    /// <b>Why you can see the arm.</b> The hand is posed by two-bone IK
    /// (<see cref="AstronautLocomotionVisual.ApplyHandTargetNow"/>) onto points defined in VIEW space as
    /// multiples of the arm's own reach — so the hand lands in the lower-right of the visor by
    /// construction, at any model scale, instead of wherever aiming the shoulder bone happened to throw
    /// it. Carried objects sit on the palm the solver actually produced, so they never drift off the hand.
    ///
    /// Runs in LateUpdate at a high execution order: the walk swing (and any Animator clip) has already
    /// written the arm by then, and the camera has already moved, so the reach is layered on last against
    /// this frame's view. Everything is UNSCALED time — see <see cref="AstronautController"/>.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class HandActionController : MonoBehaviour
    {
        public AstronautController astronaut;
        public AstronautLocomotionVisual locomotion;
        [Tooltip("The Head anchor. Only used to frame the hand if there is no Main Camera.")]
        public Transform headAnchor;

        [Header("Hand framing — view space, in multiples of the arm's reach (x right, y up, z forward)")]
        [Tooltip("Where a carried object is held: lower-right of the visor, comfortably within reach. " +
                 "Lowering y past about -0.3 drops the wrist below the bottom of the frame.")]
        public Vector3 holdOffset = new Vector3(0.20f, -0.20f, 0.60f);
        [Tooltip("Where a bite is taken — close to the visor and near its centre. A bulky object is held " +
                 "further out than this automatically, so it never clips through the camera's lens.")]
        public Vector3 mouthOffset = new Vector3(0.08f, -0.05f, 0.40f);
        [Tooltip("Where the hand drops back to as the flourish ends, on its way out of frame.")]
        public Vector3 restOffset = new Vector3(0.34f, -0.66f, 0.28f);
        [Tooltip("How much the framing follows the camera's PITCH. 1 pins the hand to the view even when " +
                 "you stare at your feet; lower values keep it anchored to the body so extreme angles " +
                 "don't fling the arm somewhere it cannot reach.")]
        [Range(0f, 1f)] public float pitchFollow = 0.6f;

        [Header("Eat timing (real seconds)")]
        [Min(0.05f)] public float reachSeconds = 0.22f;
        [Min(0.05f)] public float bringSeconds = 0.24f;
        [Min(0.05f)] public float settleSeconds = 0.28f;
        [Tooltip("How much of its own size an object keeps once it is in the hand.")]
        [Range(0.4f, 1f)] public float carryScale = 0.9f;
        [Tooltip("How long each bite's shrink takes to snap into place.")]
        [Min(0.02f)] public float biteSnapSeconds = 0.18f;

        [Header("Pluck & inspect")]
        [Tooltip("How far the hand presses INTO the plant while it strains against the roots, in " +
                 "multiples of the arm's reach. This is what makes the pull look like it costs something.")]
        [Range(0f, 0.3f)] public float pluckLoad = 0.07f;
        [Tooltip("How far past the snack the hand carries on at the moment it lets go.")]
        [Range(0f, 0.4f)] public float pluckSnap = 0.12f;
        [Tooltip("Degrees a second the snack turns while it is held up to be looked at. Brisk, because the " +
                 "look is brief — turned slowly it would read as the hand stalling rather than showing it.")]
        public float inspectSpinDegPerSec = 130f;
        [Tooltip("How far toward the visor it is brought for that look. 0 is the normal carry pose, " +
                 "1 is right up against the lens.")]
        [Range(0f, 1f)] public float inspectCloseness = 0.35f;

        [Header("Pet timing (real seconds)")]
        [Min(0.05f)] public float petReachSeconds = 0.32f;
        [Min(0.05f)] public float petSettleSeconds = 0.32f;
        [Tooltip("How far the hand leans from its framed pose toward the animal. 0 keeps it dead centre " +
                 "in the visor, 1 aims straight at the animal (and may drop it out of frame).")]
        [Range(0f, 1f)] public float petAimBias = 0.6f;

        [Header("Audio (optional — per-object clips win over these)")]
        public AudioSource audioSource;
        public AudioClip biteClip;

        enum Act { Idle, Eat, Pet }
        // Pluck and Inspect are appended rather than slotted in between Reach and Bring where they belong
        // in the sequence, to keep the habit the serialized enums in this project have to follow.
        enum Step { Reach, Bring, Bites, Pats, Settle, Pluck, Inspect }

        public bool IsBusy => _act != Act.Idle;

        Act _act = Act.Idle;
        Step _step;
        float _t;
        Vector3 _handFrom;          // where the hand started this sequence
        Vector3 _grabPoint;         // fixed anchor the carry arc starts from
        Vector3 _armTarget;         // last (clamped) target handed to the solver
        float _grip;

        EatableObject _food;
        int _bitesDone;
        bool _contactDone;          // this beat's chomp / stroke has already landed
        float _scaleFrom = 1f, _scaleTo = 1f, _scaleMul = 1f, _scaleT = 1f;
        Quaternion _spin = Quaternion.identity, _spinTarget = Quaternion.identity;

        PettableObject _pet;
        int _patsDone;

        ParticleSystem _crumbs;
        Camera _cam;

        void Awake()
        {
            if (astronaut == null) astronaut = GetComponentInParent<AstronautController>();
            if (locomotion == null && astronaut != null)
                locomotion = astronaut.GetComponentInChildren<AstronautLocomotionVisual>();
        }

        void OnDisable()
        {
            // Never leave an object stranded in a hand that has stopped updating.
            if (_food != null) _food.CancelCarry();
            if (_pet != null) _pet.EndPet();
            EndAction();
        }

        // ---------------------------------------------------------------- entry points

        public bool BeginEat(EatableObject food)
        {
            if (IsBusy || food == null || !food.CanBeEaten) return false;

            _act = Act.Eat;
            _step = Step.Reach;
            _t = 0f;
            _food = food;
            _bitesDone = 0;
            _contactDone = false;
            // Capped: a big object would otherwise ask the hand to stop further short than the arm is long.
            _grip = Mathf.Min(food.GrabRadius, Reach * 0.35f);
            _grabPoint = food.GrabPosition;
            _handFrom = HandNow();
            _scaleFrom = _scaleTo = _scaleMul = 1f;
            _scaleT = 1f;
            _spin = _spinTarget = food.Body.rotation;
            food.BeginCarry();
            return true;
        }

        public bool BeginPet(PettableObject pet)
        {
            if (IsBusy || pet == null) return false;

            _act = Act.Pet;
            _step = Step.Reach;
            _t = 0f;
            _pet = pet;
            _patsDone = 0;
            _contactDone = false;
            _grip = 0f;
            _handFrom = HandNow();
            pet.BeginPet();
            return true;
        }

        // ---------------------------------------------------------------- tick

        void LateUpdate()
        {
            if (_act == Act.Idle) return;
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            if (_act == Act.Eat) TickEat(dt);
            else TickPet(dt);
        }

        void TickEat(float dt)
        {
            // The object can vanish under us (scene reset, a Destroy on the eaten hook) — bail cleanly.
            if (_food == null && _step != Step.Settle) { _step = Step.Settle; _t = 0f; }

            switch (_step)
            {
                case Step.Reach:
                {
                    _t += dt / reachSeconds;
                    float k = Mathf.Clamp01(_t);
                    // The object stays exactly where it is — the HAND travels to it, arriving with a
                    // touch of overshoot so the grab has some snap to it.
                    _grabPoint = _food.GrabPosition;       // re-read: it may be swaying on a branch
                    DriveArm(Vector3.Lerp(_handFrom, _grabPoint, EaseOutBack(k, 0.9f)),
                             Smoother(Mathf.Clamp01(k * 1.3f)));
                    if (_t >= 1f)
                    {
                        // Anything rooted gets pulled free first. A snack lying loose on a bench has
                        // nothing to pull against, so it skips straight to being carried.
                        if (_food != null && _food.ShouldPluck)
                        {
                            _step = Step.Pluck;
                            _t = 0f;
                            _contactDone = false;
                        }
                        else StartBring();
                    }
                    break;
                }
                case Step.Pluck:
                    TickPluck(dt);
                    break;
                case Step.Inspect:
                    TickInspect(dt);
                    break;
                case Step.Bring:
                {
                    _t += dt / bringSeconds;
                    float k = Smoother(Mathf.Clamp01(_t));
                    Vector3 hold = HoldPoint();
                    // Lift the arc's control point so it swings UP to the visor instead of sliding across.
                    Vector3 mid = Vector3.Lerp(_grabPoint, hold, 0.5f) + Vector3.up * (Reach * 0.3f);
                    Vector3 along = QuadBezier(_grabPoint, mid, hold, k);
                    DriveArm(along, 1f);
                    // Fruit hangs further away than an arm is long, so early in the arc the object flies
                    // along the path itself while the hand pulls in after it; by the end the hold pose IS
                    // within reach, and object and palm arrive together.
                    CarryAt(dt, Vector3.Lerp(along, CarryPoint(), k * k));
                    if (_t >= 1f)
                    {
                        float look = _food != null ? _food.inspectSeconds : 0f;
                        _step = look > 0f ? Step.Inspect : Step.Bites;
                        _t = 0f;
                        _contactDone = false;
                    }
                    break;
                }
                case Step.Bites:
                    TickBites(dt);
                    break;
                case Step.Settle:
                {
                    _t += dt / settleSeconds;
                    float k = Smoother(Mathf.Clamp01(_t));
                    DriveArm(Vector3.Lerp(HoldPoint(), ViewPoint(restOffset), k), 1f - k);
                    if (_t >= 1f) EndAction();
                    break;
                }
            }
        }

        /// <summary>
        /// Pull it free of whatever it is growing on.
        ///
        /// The shape of it is the whole point: it gives a few millimetres the WRONG way first, then barely
        /// moves at all while the hand strains against it, and only then lets go and flies. A snack that
        /// simply slides up out of the soil at a constant speed reads as weightless — this reads as
        /// rooted. The soil comes off it at the moment it releases, not before.
        /// </summary>
        void TickPluck(float dt)
        {
            const float Load = 0.34f;   // done leaning in on it
            const float Free = 0.60f;   // it lets go here

            float seconds = _food != null ? _food.pluckSeconds : 0.3f;
            _t += dt / Mathf.Max(0.05f, seconds);
            float k = Mathf.Clamp01(_t);

            Vector3 axis = _food != null ? _food.PluckAxis : Vector3.up;
            float distance = _food != null ? _food.pluckDistance : 0f;

            float travel;
            if (k < Load)
                travel = Mathf.Lerp(0f, -0.10f, EaseOutCubic(k / Load)) * distance;
            else if (k < Free)
                travel = Mathf.Lerp(-0.10f, 0.16f, EaseInCubic((k - Load) / (Free - Load))) * distance;
            else
                travel = Mathf.Lerp(0.16f, 1f, EaseOutBack((k - Free) / (1f - Free), 1.1f)) * distance;

            Vector3 at = _grabPoint + axis * travel;

            // The wrist leads the snack: pressing in against it while it holds, overshooting past it the
            // instant it comes away.
            float lead = k < Free
                ? -pluckLoad * Mathf.Sin(k / Free * Mathf.PI)
                : pluckSnap * Mathf.Sin((k - Free) / (1f - Free) * Mathf.PI);
            DriveArm(at + axis * (lead * Reach), 1f);
            CarryAt(dt, at);

            if (!_contactDone && k >= Free)
            {
                _contactDone = true;
                EmitPluckDebris(_grabPoint, axis);
                PlaySound(_food != null ? _food.pluckClip : null);
                // A quarter turn on release, so it is already moving before the carry arc picks it up.
                _spinTarget = _spin * Quaternion.AngleAxis(30f, TumbleAxis());
            }

            if (_t >= 1f)
            {
                // The carry arc starts from where the snack ACTUALLY is now, not from the soil it came
                // out of, or it would dive back into the ground on its way up.
                _grabPoint = at;
                StartBring();
            }
        }

        /// <summary>Hold it up near the visor and turn it. This is the shot the snack was modelled for.</summary>
        void TickInspect(float dt)
        {
            float seconds = _food != null ? _food.inspectSeconds : 0f;
            _t += dt;

            DriveArm(Vector3.Lerp(HoldPoint(), MouthPoint(), inspectCloseness), 1f);
            // Advancing the TARGET rather than the pose itself keeps the existing slerp in CarryAt doing
            // the smoothing, so the turn eases rather than ticking round at a constant rate.
            _spinTarget = _spinTarget * Quaternion.AngleAxis(inspectSpinDegPerSec * dt, TumbleAxis());
            CarryAt(dt, CarryPoint());

            if (_t >= seconds) { _step = Step.Bites; _t = 0f; _contactDone = false; }
        }

        void StartBring()
        {
            _step = Step.Bring;
            _t = 0f;
            _scaleFrom = _scaleMul;      // whatever the pluck left it at, not an assumed 1
            _scaleTo = carryScale;
            _scaleT = 0f;
            _spinTarget = _spin * Quaternion.AngleAxis(45f, TumbleAxis());
        }

        // One beat per bite: lift it to the visor, chomp, withdraw, chew.
        void TickBites(float dt)
        {
            const float LiftEnd = 0.30f;    // the chomp lands here
            const float BackEnd = 0.58f;    // withdrawn again by here

            float interval = _food != null ? _food.biteInterval : 0.5f;
            int total = _food != null ? _food.bites : 3;
            _t += dt;
            float u = Mathf.Clamp01(_t / interval);

            Vector3 hold = HoldPoint();
            Vector3 mouth = MouthPoint();

            Vector3 target;
            if (u < LiftEnd)
                target = Vector3.Lerp(hold, mouth, EaseOutCubic(u / LiftEnd));
            else if (u < BackEnd)
                target = Vector3.Lerp(mouth, hold, EaseInOutCubic((u - LiftEnd) / (BackEnd - LiftEnd)));
            else
                // Chewing: drift a little rather than freezing dead still on the hold pose.
                target = hold + ViewDir(Vector3.up)
                       * (Mathf.Sin((u - BackEnd) / (1f - BackEnd) * Mathf.PI * 2f) * Reach * 0.02f);
            DriveArm(target, 1f);

            if (!_contactDone && u >= LiftEnd)
            {
                TakeBite(total);
                _contactDone = true;
            }

            CarryAt(dt, CarryPoint());     // glued to the palm from here on

            if (_bitesDone >= total)
            {
                // Finish as soon as the hand is clear of the visor AND the last morsel has shrunk away —
                // no dead beat after the final bite, and nothing pops out of existence at full size.
                if (u >= BackEnd && _scaleT >= 1f) { FinishFood(); _step = Step.Settle; _t = 0f; }
            }
            else if (_t >= interval)
            {
                _t = 0f;
                _contactDone = false;
            }
        }

        void TakeBite(int total)
        {
            _bitesDone++;

            // The stepped shrink, eased with a little overshoot so each bite snaps out of it.
            _scaleFrom = _scaleMul;
            _scaleTo = carryScale * (1f - _bitesDone / (float)total);
            _scaleT = 0f;

            // Turn a fresh side toward the visor for the next bite; the golden angle never repeats.
            _spinTarget = _spinTarget * Quaternion.AngleAxis(137.5f, TumbleAxis());

            EmitCrumbs(CarryPoint(), _bitesDone >= total ? 14 : 7);
            if (_food != null) _food.NotifyBite(_bitesDone, total);
            PlayBiteSound();
        }

        /// <summary>Advance the carried object's stepped shrink and tumble, and place it at
        /// <paramref name="worldPos"/> (its visual centre).</summary>
        void CarryAt(float dt, Vector3 worldPos)
        {
            if (_food == null) return;
            _scaleT = Mathf.Min(1f, _scaleT + dt / biteSnapSeconds);
            _scaleMul = Mathf.Lerp(_scaleFrom, _scaleTo, EaseOutBack(_scaleT, 1.4f));
            _spin = Quaternion.Slerp(_spin, _spinTarget, 1f - Mathf.Exp(-9f * dt));
            _food.SetCarryPose(worldPos, _spin, Mathf.Max(0f, _scaleMul));
        }

        void FinishFood()
        {
            if (_food != null) _food.Consume();
            _food = null;
        }

        void TickPet(float dt)
        {
            if (_pet == null && _step != Step.Settle) { _step = Step.Settle; _t = 0f; }

            switch (_step)
            {
                case Step.Reach:
                {
                    _t += dt / petReachSeconds;
                    float k = Mathf.Clamp01(_t);
                    DriveArm(Vector3.Lerp(_handFrom, PetPoint(), Smoother(k)),
                             Smoother(Mathf.Clamp01(k * 1.3f)));
                    if (_t >= 1f) { _step = Step.Pats; _t = 0f; _contactDone = false; }
                    break;
                }
                case Step.Pats:
                    TickPats(dt);
                    break;
                case Step.Settle:
                {
                    _t += dt / petSettleSeconds;
                    float k = Smoother(Mathf.Clamp01(_t));
                    DriveArm(Vector3.Lerp(PetPoint(), ViewPoint(restOffset), k), 1f - k);
                    if (_t >= 1f) EndAction();
                    break;
                }
            }
        }

        // One beat per stroke: lift, drop onto the animal (fast, so it lands), ease back up.
        void TickPats(float dt)
        {
            const float Top = 0.34f;        // top of the lift
            const float Contact = 0.54f;    // the stroke lands here
            const float LiftHeight = 0.17f; // multiples of the arm's reach
            const float Press = -0.06f;     // how far past the animal's surface the stroke presses

            float interval = _pet != null ? _pet.patInterval : 0.42f;
            int total = _pet != null ? _pet.pats : 3;
            _t += dt;
            float u = Mathf.Clamp01(_t / interval);

            float lift;
            if (u < Top) lift = Mathf.Lerp(0f, LiftHeight, EaseOutCubic(u / Top));
            else if (u < Contact) lift = Mathf.Lerp(LiftHeight, Press, EaseInCubic((u - Top) / (Contact - Top)));
            else lift = Mathf.Lerp(Press, 0f, EaseOutCubic((u - Contact) / (1f - Contact)));

            // A little forward lean on the way down, so the stroke pushes INTO the animal, not past it.
            float lean = u < Top ? 0f : Mathf.Sin((u - Top) / (1f - Top) * Mathf.PI) * 0.05f;

            DriveArm(PetPoint() + ViewDir(Vector3.up) * (lift * Reach)
                                + ViewDir(Vector3.forward) * (lean * Reach), 1f);

            if (!_contactDone && u >= Contact)
            {
                _patsDone++;
                if (_pet != null) _pet.Pat(transform.position, _patsDone, total);
                _contactDone = true;
            }

            if (_patsDone >= total)
            {
                if (u >= 0.85f) { FinishPet(); _step = Step.Settle; _t = 0f; }
            }
            else if (_t >= interval)
            {
                _t = 0f;
                _contactDone = false;
            }
        }

        void FinishPet()
        {
            if (_pet != null) _pet.EndPet();
            _pet = null;
        }

        void EndAction()
        {
            if (_food != null) { _food.CancelCarry(); _food = null; }
            if (_pet != null) { _pet.EndPet(); _pet = null; }
            _act = Act.Idle;
            _step = Step.Reach;
            _t = 0f;
        }

        // ---------------------------------------------------------------- posing

        /// <summary>Shoulder-to-wrist span in world metres — the unit every framed offset is given in.</summary>
        float Reach => locomotion != null && locomotion.ArmReach > 0.01f ? locomotion.ArmReach : 0.55f;

        Vector3 HandNow() => locomotion != null ? locomotion.WristPosition : ViewPoint(restOffset);

        /// <summary>Where a carried object sits: the palm the solver actually produced.</summary>
        Vector3 CarryPoint() =>
            locomotion != null && locomotion.HasReachArm ? locomotion.GripPoint : _armTarget;

        void DriveArm(Vector3 target, float blend)
        {
            target = PushClearOfLens(target);
            if (locomotion == null) { _armTarget = target; return; }
            // Clamp here rather than inside the solver, so the pose we animate and the pose the arm can
            // actually hit are the same point — otherwise a carried object floats off the hand.
            _armTarget = locomotion.ClampToArmReach(target, _grip);
            locomotion.ApplyHandTargetNow(_armTarget, blend, _grip);
        }

        /// <summary>
        /// Hold the target far enough down the view axis that the object in the hand stays wholly in front
        /// of the near clip plane. Without this a chunky snack brought up to the visor is sliced open by
        /// the lens — and how chunky "chunky" is depends entirely on the model's scale.
        /// </summary>
        Vector3 PushClearOfLens(Vector3 target)
        {
            Transform v = View();
            float near = _cam != null ? _cam.nearClipPlane : 0.05f;
            float minDepth = near + _grip * 1.35f + Reach * 0.05f;
            Vector3 forward = v.forward;
            float depth = Vector3.Dot(target - v.position, forward);
            return depth >= minDepth ? target : target + forward * (minDepth - depth);
        }

        Transform View()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam != null) return _cam.transform;
            return headAnchor != null ? headAnchor : transform;
        }

        /// <summary>
        /// Yaw follows the view exactly; pitch only partly, so looking sharply up or down slides the hand
        /// gently through the frame instead of hurling it out of the arm's reach.
        /// </summary>
        Quaternion ViewBasis()
        {
            Transform v = View();
            Vector3 f = v.forward;
            float flat = f.x * f.x + f.z * f.z;
            float yaw = flat > 1e-6f ? Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg : transform.eulerAngles.y;
            float pitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            return Quaternion.Euler(pitch * pitchFollow, yaw, 0f);
        }

        /// <summary>A point in front of the visor, given in multiples of the arm's reach.</summary>
        Vector3 ViewPoint(Vector3 offset) => View().position + ViewBasis() * (offset * Reach);

        Vector3 ViewDir(Vector3 direction) => ViewBasis() * direction;

        /// <summary>Where a bite is taken — as close to the visor as the object's own size allows.</summary>
        Vector3 MouthPoint() => PushClearOfLens(ViewPoint(mouthOffset));

        /// <summary>
        /// Where the object is held between bites. Pushed out far enough past the bite pose that the
        /// lift-to-the-visor still reads, even for something chunky whose bite pose had to back off the
        /// lens — otherwise a big snack would hold and bite at almost the same distance.
        /// </summary>
        Vector3 HoldPoint()
        {
            Transform v = View();
            Vector3 hold = PushClearOfLens(ViewPoint(holdOffset));
            float mouthDepth = Vector3.Dot(MouthPoint() - v.position, v.forward);
            float holdDepth = Vector3.Dot(hold - v.position, v.forward);
            float wanted = mouthDepth + Reach * 0.18f;
            return holdDepth >= wanted ? hold : hold + v.forward * (wanted - holdDepth);
        }

        /// <summary>
        /// Where the hand strokes: the framed hold pose leaned toward the animal, so the hand stays in
        /// the lower visor while unmistakably reaching for the thing you are patting.
        /// </summary>
        Vector3 PetPoint()
        {
            Vector3 framed = HoldPoint();
            if (_pet == null || locomotion == null) return framed;

            Vector3 shoulder = locomotion.ShoulderPosition;
            Vector3 top = _pet.PetPosition + Vector3.up * (_pet.PetRadius * 0.5f);
            Vector3 to = top - shoulder;
            Vector3 aimed = shoulder + (to.sqrMagnitude > 1e-6f ? to.normalized : ViewDir(Vector3.forward))
                                     * (Reach * 0.95f);
            return Vector3.Lerp(framed, aimed, petAimBias);
        }

        /// <summary>Tumble about a view-space axis, so the turn always reads on camera.</summary>
        Vector3 TumbleAxis()
        {
            Vector3 axis = ViewDir(Vector3.up) + ViewDir(Vector3.right) * 0.35f;
            return axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;
        }

        // ---------------------------------------------------------------- feedback

        void PlayBiteSound() =>
            PlaySound(_food != null && _food.biteClip != null ? _food.biteClip : biteClip);

        void PlaySound(AudioClip clip)
        {
            if (clip == null) clip = biteClip;
            AudioSource src = _food != null && _food.audioSource != null ? _food.audioSource : audioSource;
            if (src != null && clip != null) src.PlayOneShot(clip);
        }

        void EmitCrumbs(Vector3 at, int count)
        {
            if (_crumbs == null) _crumbs = BuildCrumbSystem();
            var p = new ParticleSystem.EmitParams
            {
                position = at,
                startColor = _food != null ? _food.DebrisColor : new Color(0.95f, 0.85f, 0.7f),
                startSize = Mathf.Max(0.005f, Reach * 0.05f),
            };
            _crumbs.Emit(p, count);
        }

        /// <summary>
        /// The soil coming off a root as it clears the ground. Emitted one at a time with its own velocity
        /// rather than in a single burst: a clod thrown sideways and falling is what says "that was
        /// buried", and one Emit call would give the whole lot the same direction.
        /// </summary>
        void EmitPluckDebris(Vector3 at, Vector3 axis)
        {
            if (_crumbs == null) _crumbs = BuildCrumbSystem();
            Color color = _food != null ? _food.PluckDebrisColor : new Color(0.34f, 0.26f, 0.18f);
            float speed = Reach * 0.9f;
            for (int i = 0; i < 20; i++)
            {
                var p = new ParticleSystem.EmitParams
                {
                    position = at + Random.insideUnitSphere * (Reach * 0.05f),
                    startColor = color,
                    startSize = Mathf.Max(0.004f, Reach * Random.Range(0.03f, 0.08f)),
                    velocity = Random.onUnitSphere * speed + axis * (speed * 0.55f),
                    startLifetime = Random.Range(0.7f, 1.4f),
                };
                _crumbs.Emit(p, 1);
            }
        }

        // Built in code (no particle assets in the project): tiny bits in the object's own colour that pop
        // off each bite and fall under lunar-ish gravity.
        ParticleSystem BuildCrumbSystem()
        {
            var go = new GameObject("BiteCrumbs");
            go.transform.SetParent(transform, worldPositionStays: false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.useUnscaledTime = true;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = 1.1f;
            main.startSpeed = Reach * 1.2f;
            main.startSize = 0.025f;
            main.gravityModifier = 0.35f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;   // unaffected by the parenting above
            main.maxParticles = 64;
            var emission = ps.emission;
            emission.rateOverTime = 0f;              // Emit() only
            var psr = go.GetComponent<ParticleSystemRenderer>();
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(sh) { name = "Crumbs (runtime)" };
            psr.material = mat;
            psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        // ---------------------------------------------------------------- easing

        static float Smoother(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
        static float EaseInCubic(float t) => t * t * t;
        static float EaseOutCubic(float t) { t = 1f - t; return 1f - t * t * t; }

        static float EaseInOutCubic(float t) =>
            t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;

        /// <summary>Eases to 1 and overshoots on the way — a snap, not a slide.</summary>
        static float EaseOutBack(float t, float overshoot)
        {
            float p = t - 1f;
            return 1f + (overshoot + 1f) * p * p * p + overshoot * p * p;
        }

        static Vector3 QuadBezier(Vector3 a, Vector3 b, Vector3 c, float t)
        {
            float m = 1f - t;
            return m * m * a + 2f * m * t * b + t * t * c;
        }
    }
}
