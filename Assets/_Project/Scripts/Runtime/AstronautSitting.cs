using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// Sitting down and getting back up, as an animation rather than a teleport. Lives on the astronaut
    /// root beside <see cref="AstronautController"/>.
    ///
    /// Pressing E at a <see cref="SittableChair"/> eases the body from wherever it stands into the
    /// chair's seat anchor over <see cref="sitSeconds"/> while
    /// <see cref="AstronautLocomotionVisual.sitBlend"/> folds the legs — one blend drives both the travel
    /// and the pose, so the astronaut arrives seated rather than snapping into a sitting statue. E again
    /// reverses it.
    ///
    /// Two things are deliberate:
    /// <list type="bullet">
    /// <item>The <b>CharacterController is switched off</b> while seated. It is a capsule that falls;
    /// left on, gravity would drag the body off the seat every frame. <see cref="AstronautController"/>
    /// skips its whole update while the controller is disabled, so nothing fights us.</item>
    /// <item>The <b>stand-up key is read here</b>, not through <see cref="InteractionSensor"/>. Seated,
    /// the astronaut's input is off (so WASD can't walk you out of the chair), which also silences the
    /// sensor — so this component owns both the prompt and the key until you are back on your feet.</item>
    /// </list>
    ///
    /// Unscaled time throughout: sitting down must take the same real second at 16x sim speed.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AstronautSitting : MonoBehaviour
    {
        public enum State { Standing, SittingDown, Seated, StandingUp }

        [Header("Wiring (auto-found if left empty)")]
        public AstronautController astronaut;
        public AstronautLocomotionVisual locomotion;
        public AstronautCameraRig cameraRig;

        [Header("Timing (real seconds, immune to sim fast-forward)")]
        [Min(0.05f)] public float sitSeconds = 0.75f;
        [Min(0.05f)] public float standSeconds = 0.6f;
        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Feel")]
        [Tooltip("Swing the view round to face the way the chair faces as you sit. Off leaves you " +
                 "looking wherever you were.")]
        public bool turnToFaceSeat = true;
        [Tooltip("Hip height above the feet, used only if the rig's thigh bone can't be found.")]
        [Min(0.2f)] public float fallbackHipHeight = 0.9f;

        [Header("Prompt")]
        public string standPrompt = "[E] Stand up";

        public State CurrentState { get; private set; } = State.Standing;
        public SittableChair Chair { get; private set; }

        /// <summary>True when a chair may start a sit — standing, and not mid-transition.</summary>
        public bool CanSit => CurrentState == State.Standing;
        public bool IsSeated => CurrentState == State.Seated;

        CharacterController _cc;
        Vector3 _fromPos, _toPos;
        float _fromYaw, _toYaw;
        float _t;

        void Awake()
        {
            if (astronaut == null) astronaut = GetComponent<AstronautController>();
            if (astronaut == null) astronaut = GetComponentInParent<AstronautController>();
            if (locomotion == null && astronaut != null)
                locomotion = astronaut.GetComponentInChildren<AstronautLocomotionVisual>();
            if (cameraRig == null) cameraRig = FindAnyObjectByType<AstronautCameraRig>();
            _cc = GetComponent<CharacterController>();
            if (_cc == null && astronaut != null) _cc = astronaut.GetComponent<CharacterController>();
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Begin sitting in <paramref name="chair"/>. Returns false if already busy or seated.</summary>
        public bool SitOn(SittableChair chair)
        {
            if (!CanSit || chair == null || chair.seatAnchor == null || chair.Occupant != null) return false;

            Chair = chair;
            chair.Occupant = this;

            // Put the ROOT (which is at the feet) low enough that the HIPS land on the seat. Measured off
            // the live rig rather than assumed, so a model imported at any scale seats itself correctly —
            // and measured against THIS transform, which is the body the CharacterController moves.
            float hipHeight = fallbackHipHeight;
            if (locomotion != null)
            {
                Transform hip = locomotion.leftLeg != null ? locomotion.leftLeg : locomotion.rightLeg;
                if (hip != null) hipHeight = Mathf.Max(0.05f, hip.position.y - transform.position.y);
            }

            _fromPos = transform.position;
            _toPos = chair.SeatPosition - Vector3.up * hipHeight;
            _fromYaw = CurrentYaw;
            _toYaw = turnToFaceSeat ? Quaternion.LookRotation(chair.SeatForward, Vector3.up).eulerAngles.y
                                    : _fromYaw;
            _t = 0f;

            // The capsule stops falling and stops colliding for the duration; the transform is ours now.
            if (_cc != null) _cc.enabled = false;
            CurrentState = State.SittingDown;
            return true;
        }

        /// <summary>Get up. Safe to call at any time — it does nothing unless seated.</summary>
        public void Stand()
        {
            if (CurrentState != State.Seated || Chair == null) return;

            _fromPos = transform.position;
            _toPos = GroundedStandPosition(Chair.StandPosition);
            _fromYaw = _toYaw = CurrentYaw;      // standing up doesn't re-aim the view
            _t = 0f;
            CurrentState = State.StandingUp;
        }

        /// <summary>Drop the stand-out point onto whatever floor is under it, so you don't step into the air.</summary>
        Vector3 GroundedStandPosition(Vector3 wanted)
        {
            // The astronaut's own capsule is disabled while seated, so this can't hit ourselves.
            if (Physics.Raycast(wanted + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 6f,
                                ~0, QueryTriggerInteraction.Ignore))
                return new Vector3(wanted.x, hit.point.y + 0.02f, wanted.z);
            return wanted;
        }

        // ------------------------------------------------------------------ loop

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            switch (CurrentState)
            {
                case State.SittingDown:
                    Advance(dt, sitSeconds, out float sitK);
                    ApplyPose(sitK, sitK);
                    if (_t >= 1f)
                    {
                        CurrentState = State.Seated;
                        _toPos = transform.position;      // the pose to hold from here on
                    }
                    break;

                case State.Seated:
                    // Re-assert every frame: pressing C for the fly-cam and back would otherwise hand
                    // walking input back to a body that is still in a chair.
                    if (astronaut != null) astronaut.enableInput = false;
                    if (locomotion != null) locomotion.sitBlend = 1f;
                    transform.position = _toPos;
                    if (StandPressed()) Stand();
                    break;

                case State.StandingUp:
                    Advance(dt, standSeconds, out float standK);
                    ApplyPose(standK, 1f - standK);
                    if (_t >= 1f) FinishStanding();
                    break;
            }
        }

        void Advance(float dt, float seconds, out float eased)
        {
            _t = Mathf.Clamp01(_t + dt / Mathf.Max(0.05f, seconds));
            eased = ease != null ? ease.Evaluate(_t) : _t;
        }

        /// <summary>Move the body along the transition and set how folded the legs are.</summary>
        void ApplyPose(float travel01, float sitBlend)
        {
            transform.position = Vector3.Lerp(_fromPos, _toPos, travel01);

            float yaw = Mathf.LerpAngle(_fromYaw, _toYaw, travel01);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            // The camera rig re-writes the body's yaw from its own value every LateUpdate, so the turn has
            // to be driven THERE or it would be undone the moment we set it here.
            if (cameraRig != null) cameraRig.Yaw = yaw;

            if (locomotion != null) locomotion.sitBlend = sitBlend;
            if (astronaut != null) astronaut.enableInput = false;
        }

        void FinishStanding()
        {
            if (Chair != null && Chair.Occupant == this) Chair.Occupant = null;
            Chair = null;
            if (locomotion != null) locomotion.sitBlend = 0f;

            // Hand the body back: re-enable the capsule at the standing position, then restore input —
            // unless the fly-cam has it, in which case the rig stays in charge.
            if (_cc != null) _cc.enabled = true;
            if (astronaut != null)
            {
                astronaut.Teleport(_toPos, transform.rotation);
                astronaut.enableInput = cameraRig == null ||
                                        cameraRig.mode == AstronautCameraRig.ViewMode.FirstPerson;
            }
            CurrentState = State.Standing;
        }

        // LateUpdate so this prompt lands AFTER InteractionSensor's own Show/Hide in Update — while
        // seated the sensor is silent anyway, but ordering it explicitly means it can never flicker.
        void LateUpdate()
        {
            if (CurrentState != State.Seated) return;
            var ui = InteractionPromptUI.Instance;
            if (ui != null) ui.Show(standPrompt);
        }

        void OnDisable()
        {
            // Never leave the astronaut welded to a chair because the component was switched off.
            if (CurrentState == State.Standing) return;
            if (Chair != null && Chair.Occupant == this) Chair.Occupant = null;
            Chair = null;
            if (locomotion != null) locomotion.sitBlend = 0f;
            if (_cc != null) _cc.enabled = true;
            if (astronaut != null) astronaut.enableInput = true;
            CurrentState = State.Standing;
        }

        float CurrentYaw => transform.eulerAngles.y;

        bool StandPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.eKey.wasPressedThisFrame;
#else
            return false;
#endif
        }
    }
}
