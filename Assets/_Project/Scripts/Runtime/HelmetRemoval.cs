using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// Taking the helmet off once you're inside, and putting it back on before you step out.
    ///
    /// The astronaut model is a single skinned mesh with no separable helmet, so the thing that comes off
    /// is a DUPLICATE prop — your own helmet FBX if you have one, otherwise a placeholder visor the
    /// builder tool makes. Either way it is a plain object riding the head, which is what lets it come
    /// off at all.
    ///
    /// <b>How it moves.</b> The prop is parented once to a stable object and its WORLD pose is written
    /// every LateUpdate, rather than being re-parented from head to hip mid-animation. Re-parenting
    /// between two bones with different accumulated scale would make the helmet visibly change size as it
    /// travelled, and this way there is nothing to get wrong. Running at execution order 110 means the
    /// walk swing and the hand controller have already posed the skeleton, so the helmet reads this
    /// frame's real head position, not last frame's.
    ///
    /// <b>What sets it off — normally, one box, once.</b> With <see cref="onceOnly"/> on (the default)
    /// there is exactly one trigger volume, you put it wherever the take-off should happen, and the moment
    /// the astronaut is inside it the helmet comes off. After that it is off for the rest of the run: the
    /// zones are not consulted, a chamber venting cannot re-seal it, and neither the H key nor anything
    /// else can put it back. One event, in a place you chose, that cannot un-happen — which is what a
    /// single continuous take needs.
    ///
    /// The one exception is deliberate: if you put the box INSIDE a <see cref="PressureChamber"/>, that
    /// chamber still has to finish pressurizing before the helmet comes off, because that is plainly what
    /// you meant by putting it there. Put the box anywhere else and stepping in is the whole trigger.
    ///
    /// <b>The old behaviour is still here</b> behind <see cref="onceOnly"/> = false: pressurized zones,
    /// EDGE-triggered, helmet back on when you leave, and a chamber you are standing in overriding the
    /// zone. Reversible, and therefore no good for a recording.
    ///
    /// <b>It is a three-part move, not a fade.</b> Off the head, out in front of the body where the
    /// camera can see it, held there turning, and only then tucked at the hip. A helmet that slides
    /// quietly from head to hip in a second and a half is over before you have registered it.
    ///
    /// Unscaled time throughout, like everything else the player watches.
    /// </summary>
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public sealed class HelmetRemoval : MonoBehaviour
    {
        // Showing and Stowing are APPENDED rather than slotted in where they belong in the sequence.
        // Nothing serialises this today, but the cost of appending is one out-of-order enum and the cost
        // of guessing wrong is every saved reference silently shifting by two.
        public enum State { Worn, TakingOff, Held, PuttingOn, Showing, Stowing }

        [Header("Parts (wired by the helmet builder tool)")]
        [Tooltip("The helmet prop. Its PH_ child is a placeholder — swap it for a modelled helmet with " +
                 "Tools > NASA Sim > Models > Swap Placeholder With Selected FBX.")]
        public Transform helmet;
        [Tooltip("The head bone the helmet sits on while worn.")]
        public Transform headAnchor;
        [Tooltip("Where the helmet rides once it's off — normally a marker at the hip, parented to the " +
                 "pelvis bone so it moves with the walk.")]
        public Transform holdAnchor;

        /// <summary>
        /// One pressurized volume. There is a LIST of these rather than a single box because the
        /// pressurized parts of this station are not in one place — the biodome sits at the origin and
        /// the tunnel is 100 m away — and a single box asked to contain both ends up containing the moon.
        /// </summary>
        [Serializable]
        public sealed class Zone
        {
            public string label = "zone";
            [Tooltip("The box's centre and rotation. The helmet comes off inside it.")]
            public Transform center;
            public Vector3 size = new Vector3(40f, 20f, 40f);

            public Zone() { }

            public Zone(string label, Transform center, Vector3 size)
            {
                this.label = label;
                this.center = center;
                this.size = size;
            }

            /// <summary>
            /// Metres to the nearest wall: positive inside, negative outside. Signed depth rather than a
            /// bool because the same number drives the hysteresis, the editor's "how far in are you"
            /// readout, and the choice of which zone you are most firmly inside.
            /// </summary>
            public float Depth(Vector3 world)
            {
                if (center == null) return float.NegativeInfinity;
                Vector3 local = center.InverseTransformPoint(world);
                Vector3 half = size * 0.5f;
                return Mathf.Min(half.x - Mathf.Abs(local.x),
                                 half.y - Mathf.Abs(local.y),
                                 half.z - Mathf.Abs(local.z));
            }
        }

        [Header("Once, where you put the box")]
        [Tooltip("Step into the trigger box below and the helmet comes off — once. It never goes back on " +
                 "for the rest of the run: not when you leave, not when a chamber vents, not on H. " +
                 "This is the setting for a recording. Turn it off to get the old reversible zones back.")]
        public bool onceOnly = true;

        [Tooltip("The box you step into. Put it wherever the take-off should happen. Keep its Transform " +
                 "at scale 1 and unparented, or the size below stops being metres.")]
        public Zone trigger = new Zone("take the helmet off here", null, new Vector3(4f, 3f, 4f));

        [Header("Pressurized zones (only used when 'Once' is off)")]
        [Tooltip("The helmet comes off inside ANY of these and goes back on outside all of them. " +
                 "Normally one for the biodome and one for the tunnel. Empty means H key only.")]
        public List<Zone> zones = new List<Zone>();

        [Tooltip("How far past a wall you must travel before the crossing counts, in metres. Without it " +
                 "a boundary you happen to be standing on flickers the helmet on and off as the ground " +
                 "rises and falls under you.")]
        [Min(0f)] public float boundaryMargin = 0.75f;

        [Header("Legacy single zone (superseded by the list above)")]
        [Tooltip("The original one-box field. Still honoured so scenes built before the list existed keep " +
                 "working; the builder tool moves it into the list and clears it.")]
        public Transform pressurizedZone;
        public Vector3 zoneSize = new Vector3(44f, 14f, 44f);
        [Tooltip("Start with the helmet already off when the astronaut spawns inside the zone. Off by " +
                 "default: a spawn point that happens to fall inside the zone would otherwise skip the " +
                 "take-off entirely — you press Play and the helmet is simply gone.")]
        public bool matchZoneOnStart;

        [Header("Wait for the gas chamber")]
        [Tooltip("While you are standing in a pressure chamber, that chamber decides — the helmet stays " +
                 "on until its lamp goes green. Outside every chamber the zones decide as usual. Leave " +
                 "the list empty and it finds them itself.")]
        public bool waitForPressure = true;
        public List<PressureChamber> chambers = new List<PressureChamber>();
        [Tooltip("A beat between the lamp turning green and the hands going up, so the two read as cause " +
                 "and effect rather than as one event.")]
        [Min(0f)] public float pauseAfterPressurized = 0.6f;

        [Header("Motion (real seconds, immune to sim fast-forward)")]
        [Tooltip("Off the head and out in front of you.")]
        [Min(0.1f)] public float takeOffSeconds = 1.6f;
        [Tooltip("From out in front down to the hip.")]
        [Min(0.1f)] public float stowSeconds = 1.1f;
        [Tooltip("The whole way back, hip to head.")]
        [Min(0.1f)] public float putOnSeconds = 1.3f;
        [Tooltip("How far the helmet rises straight up before it travels — it has to clear the head " +
                 "before it can go anywhere else.")]
        [Min(0f)] public float liftHeight = 0.45f;
        [Tooltip("Extra bow in the path down to the hip, so it swings rather than slides.")]
        [Min(0f)] public float arcHeight = 0.25f;

        [Header("Hold it up where you can see it")]
        [Tooltip("Bring it out in front of the body and hold it there before stowing it. This is the " +
                 "difference between watching the helmet come off and noticing afterwards that it has.")]
        public bool showItOff = true;
        [Tooltip("Seconds it hangs there turning.")]
        [Min(0f)] public float showSeconds = 1.3f;
        [Tooltip("How far in front of the head it is held. In first person this is straight down the " +
                 "middle of the view.")]
        [Min(0.05f)] public float showDistance = 0.45f;
        [Tooltip("How far below the head, so it does not sit on top of what you are looking at.")]
        public float showHeight = -0.22f;
        [Tooltip("Turns on the spot while held, so you see all of it rather than one side.")]
        public float showSpinDegPerSec = 75f;
        [Tooltip("Tipped forward while held, so you can see into the bowl of it.")]
        [Range(-90f, 90f)] public float showTiltDegrees = 22f;

        [Header("The reach")]
        [Tooltip("Pose the right arm so the hand goes up and gets it. Needs an AstronautLocomotionVisual " +
                 "with a solvable arm; harmless without one.")]
        public bool reachForIt = true;
        public AstronautLocomotionVisual locomotion;
        [Tooltip("Skipped while the hand is busy eating or petting, so two sequences can't fight over " +
                 "the same arm.")]
        public HandActionController hand;

        [Header("Input")]
        [Tooltip("H takes the helmet off or puts it back on, anywhere.")]
        public bool enableKey = true;

        [Header("Audio hooks (optional — drop clips in later)")]
        public AudioSource audioSource;
        public AudioClip unsealClip;
        public AudioClip sealClip;

        [Header("Events")]
        public UnityEvent onHelmetOff;
        public UnityEvent onHelmetOn;

        [Header("Scene view")]
        public bool showGizmo = true;
        public Color zoneColor = new Color(0.45f, 0.9f, 1f, 0.9f);

        // The two poses are SERIALIZED, captured by the builder tool while the helmet is where it was
        // placed — never at Awake, so the editor's on/off preview can't be adopted as the real pose.
        [SerializeField, HideInInspector] Vector3 _wornLocalPos;
        [SerializeField, HideInInspector] Quaternion _wornLocalRot = Quaternion.identity;
        [SerializeField, HideInInspector] Vector3 _heldLocalPos;
        [SerializeField, HideInInspector] Quaternion _heldLocalRot = Quaternion.identity;
        [SerializeField, HideInInspector] float _gripRadius = 0.16f;

        public State CurrentState { get; private set; } = State.Worn;
        public bool IsOff => CurrentState == State.Held;
        public bool IsMoving => CurrentState == State.TakingOff || CurrentState == State.Showing ||
                                CurrentState == State.Stowing || CurrentState == State.PuttingOn;

        /// <summary>
        /// The one-time take-off has happened. Deliberately NOT serialized: it is state about this run,
        /// and a version of it that survived into the asset would mean the second time you pressed Play
        /// the helmet never came off at all.
        /// </summary>
        public bool Fired => _fired;

        /// <summary>
        /// Off, and staying off. Every path that could put the helmet back on asks this first — the zone
        /// logic, a venting chamber, the H key — so there is one place to be right rather than four places
        /// to remember.
        /// </summary>
        public bool Locked => onceOnly && _fired;

        public bool HasTrigger => trigger != null && trigger.center != null;

        public float TriggerDepth(Vector3 worldPoint) =>
            trigger != null ? trigger.Depth(worldPoint) : float.NegativeInfinity;

        public bool TriggerContains(Vector3 worldPoint) => TriggerDepth(worldPoint) >= 0f;

        /// <summary>The chamber the trigger box sits in, if any — what the tool warns about.</summary>
        public PressureChamber ChamberAtTrigger() =>
            HasTrigger ? ChamberAt(trigger.center.position) : null;

        /// <summary>True when a chamber is wired up and allowed to have the last word.</summary>
        public bool GateActive
        {
            get
            {
                if (!waitForPressure || chambers == null) return false;
                for (int i = 0; i < chambers.Count; i++)
                    if (chambers[i] != null) return true;
                return false;
            }
        }

        // 0 = on the head, 0.5 = held up in front, 1 = tucked at the hip. One number for the whole
        // journey, so reversing halfway is just running it the other way.
        const float Presented = 0.5f;

        float _t;
        float _showTimer;
        float _spin;
        bool _wasInZone;
        bool _wasSafe;
        float _greenTimer;
        bool _fired;

        void Awake()
        {
            if (locomotion == null) locomotion = GetComponentInChildren<AstronautLocomotionVisual>();
            if (hand == null) hand = GetComponentInChildren<HandActionController>();

            // Objects, not values: finding the chambers here can't drift the way capturing a pose or a
            // rate would. The builder tool fills the list anyway; this only covers a scene where the
            // chamber was added afterwards.
            if (waitForPressure && (chambers == null || chambers.Count == 0))
                chambers = new List<PressureChamber>(
                    FindObjectsByType<PressureChamber>(FindObjectsInactive.Include));
        }

        void Start()
        {
            // Seed the edge detector with where we actually are, so spawning inside the zone is not read
            // as a crossing. The helmet still starts ON unless matchZoneOnStart says otherwise: the first
            // time you cross the boundary in either direction you get the full move, which is the point.
            _wasInZone = ZoneContains(Probe);
            _wasSafe = SafeToUnseal(ChamberAt(Probe), _wasInZone);
            _fired = false;

            // A one-time take-off ALWAYS starts worn, whatever matchZoneOnStart says: it is the one moment
            // the whole sequence exists for, and starting off means it happened before frame one. Same
            // reasoning for a gated helmet — the chamber starts in vacuum, so "already off at spawn" would
            // be unsealed in a room with no air in it.
            if (!onceOnly && matchZoneOnStart && _wasSafe && !GateActive) SetImmediate(off: true);
            else ApplyPose();
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Lift the helmet off. Returns false if it is already off or mid-move.</summary>
        public bool TakeOff()
        {
            if (CurrentState != State.Worn || helmet == null) return false;
            CurrentState = State.TakingOff;
            _showTimer = showSeconds;
            Play(unsealClip);
            return true;
        }

        /// <summary>
        /// Put it back on. Returns false if it is already on, mid-move, or — the case that matters —
        /// permanently off. This is the single choke point: the zone logic, the chamber logic and the key
        /// all come through here, so "it never goes back on" is one condition rather than four.
        /// </summary>
        public bool PutOn()
        {
            if (Locked) return false;
            if (CurrentState != State.Held || helmet == null) return false;
            CurrentState = State.PuttingOn;
            Play(sealClip);
            return true;
        }

        public void Toggle()
        {
            // In one-time mode H is a one-way switch: it can bring the moment forward, which is useful for
            // lining up a shot, but it can never undo it.
            if (CurrentState == State.Worn)
            {
                if (TakeOff() && onceOnly) _fired = true;
            }
            else if (CurrentState == State.Held) PutOn();
        }

        /// <summary>
        /// Head for "off", from wherever the move currently is. Turning back mid-move REVERSES it rather
        /// than being ignored — the whole journey is one number, so resuming is a matter of picking the
        /// leg that number is currently in.
        /// </summary>
        void HeadForOff()
        {
            if (helmet == null) return;
            switch (CurrentState)
            {
                case State.Worn:
                    TakeOff();
                    break;
                case State.PuttingOn:
                    CurrentState = _t > Presented ? State.Stowing : State.TakingOff;
                    break;
            }
        }

        void HeadForOn()
        {
            if (helmet == null || Locked) return;
            switch (CurrentState)
            {
                case State.Held:
                    PutOn();
                    break;
                case State.TakingOff:
                case State.Showing:
                case State.Stowing:
                    CurrentState = State.PuttingOn;
                    break;
            }
        }

        /// <summary>Snap to either end without animating — the editor's preview, and the Start pose.</summary>
        public void SetImmediate(bool off)
        {
            _t = off ? 1f : 0f;
            _spin = 0f;
            _showTimer = 0f;
            _fired = off;                      // previewing "worn" re-arms the trigger; "off" locks it
            CurrentState = off ? State.Held : State.Worn;
            ApplyPose();
        }

        /// <summary>Adopt the helmet's current placement as the WORN pose. Called by the builder tool.</summary>
        public void CaptureWornPose()
        {
            if (helmet == null || headAnchor == null) return;
            _wornLocalPos = headAnchor.InverseTransformPoint(helmet.position);
            _wornLocalRot = Quaternion.Inverse(headAnchor.rotation) * helmet.rotation;
        }

        /// <summary>Adopt the hold anchor's placement as the HELD pose, tipped so it reads as carried.</summary>
        public void CaptureHeldPose(Vector3 localOffset, Quaternion localTilt)
        {
            _heldLocalPos = localOffset;
            _heldLocalRot = localTilt;
        }

        /// <summary>Radius used as the hand's grip offset, so the palm lands ON the helmet, not inside it.</summary>
        public void SetGripRadius(float radius) => _gripRadius = Mathf.Max(0.02f, radius);

        // ------------------------------------------------------------------ loop

        // LateUpdate, at execution order 110: the skeleton is finished for this frame by now, so the
        // helmet can be hung off the head's FINAL position rather than trailing it.
        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;

            if (enableKey && TogglePressed()) Toggle();

            if (onceOnly) TickTrigger(dt);
            else TickZones(dt);

            if (IsMoving) _spin += showSpinDegPerSec * dt;

            switch (CurrentState)
            {
                case State.TakingOff:
                    _t = Mathf.MoveTowards(_t, Presented, Presented * dt / takeOffSeconds);
                    if (_t >= Presented)
                    {
                        // Skipping the hold when it is switched off keeps this one path rather than
                        // sprouting a second one: the show simply has zero duration.
                        CurrentState = State.Showing;
                        _showTimer = showItOff ? showSeconds : 0f;
                    }
                    break;

                case State.Showing:
                    _showTimer -= dt;
                    if (_showTimer <= 0f) CurrentState = State.Stowing;
                    break;

                case State.Stowing:
                    _t = Mathf.MoveTowards(_t, 1f, Presented * dt / stowSeconds);
                    if (_t >= 1f) { CurrentState = State.Held; onHelmetOff?.Invoke(); }
                    break;

                case State.PuttingOn:
                    _t = Mathf.MoveTowards(_t, 0f, dt / putOnSeconds);
                    if (_t <= 0f) { CurrentState = State.Worn; _spin = 0f; onHelmetOn?.Invoke(); }
                    break;
            }

            ApplyPose();
            if (IsMoving) DriveArm();
        }

        /// <summary>
        /// One box, one time. The moment the astronaut is inside it the move starts, and <c>_fired</c>
        /// latches — after which this method does nothing at all for the rest of the run, and
        /// <see cref="Locked"/> refuses every attempt to put the helmet back.
        ///
        /// There is no hysteresis and no edge detection here, and that is the point: an edge can be
        /// crossed back the other way. A latch cannot.
        ///
        /// The chamber test only applies when the box is inside a chamber. Putting the take-off in the
        /// airlock plainly means "after it has pressurized"; putting it anywhere else plainly means
        /// "when I walk in", and a helmet that silently refused to come off because of a room somewhere
        /// else in the station would be the worst possible thing to discover mid-take.
        /// </summary>
        void TickTrigger(float dt)
        {
            if (_fired || !TriggerContains(Probe)) return;

            PressureChamber here = ChamberAt(Probe);
            if (here != null)
            {
                if (!here.IsPressurized) { _greenTimer = 0f; return; }
                _greenTimer += dt;
                if (_greenTimer < pauseAfterPressurized) return;
            }

            // Latched only on success: with the helmet reference missing, TakeOff fails and this stays
            // armed rather than burning the one shot on a frame that could not have used it.
            if (TakeOff()) _fired = true;
        }

        /// <summary>The old reversible behaviour, kept whole behind <see cref="onceOnly"/> = false.</summary>
        void TickZones(float dt)
        {
            // Edge-triggered, not level-triggered: level-triggering would undo every manual press the
            // instant it was made, because standing still outside the zone permanently "wants" it on.
            //
            // The margin is what stops it chattering. A boundary you are standing ON is crossed and
            // re-crossed by every dip in the ground, and each crossing restarts the animation — the
            // symptom is a helmet that comes off and goes back on continuously as you walk. You have to
            // travel boundaryMargin metres PAST the wall before the crossing counts, in either direction,
            // which leaves a dead band twice that wide around every face of every zone.
            float depth = ZoneDepth(Probe);
            _wasInZone = _wasInZone ? depth > -boundaryMargin : depth > boundaryMargin;

            // Whichever chamber you are standing in, if any, has the last word — see the class summary.
            // No hysteresis needed here: the chamber runs its own presence test with its own exit margin,
            // and it takes seconds to change its mind.
            PressureChamber lockHere = ChamberAt(Probe);
            if (lockHere != null && lockHere.IsPressurized) _greenTimer += dt;
            else _greenTimer = 0f;

            bool safe = SafeToUnseal(lockHere, _wasInZone);
            if (safe != _wasSafe)
            {
                _wasSafe = safe;
                if (safe) HeadForOff(); else HeadForOn();
            }
        }

        /// <summary>
        /// May the helmet come off where we are standing? One method rather than the same expression
        /// written out at Start and again in the loop — the two drifting apart is exactly how you get a
        /// helmet that behaves differently on the first frame than on every frame after it.
        ///
        /// Note the explicit <see cref="PressureChamber.IsPressurized"/> test. Leaning on the timer alone
        /// would read as safe the instant you stepped into a chamber whenever the beat was set to zero,
        /// because a timer sitting at 0 does satisfy "at least 0 seconds".
        /// </summary>
        bool SafeToUnseal(PressureChamber lockHere, bool inZone) =>
            lockHere != null
                ? lockHere.IsPressurized && _greenTimer >= pauseAfterPressurized
                : inZone;

        /// <summary>The pressure chamber the astronaut is standing in, or null for "not in one".</summary>
        public PressureChamber ChamberAt(Vector3 worldPoint)
        {
            if (!waitForPressure || chambers == null) return null;
            for (int i = 0; i < chambers.Count; i++)
            {
                PressureChamber c = chambers[i];
                if (c != null && c.isActiveAndEnabled && c.Contains(worldPoint, 0f)) return c;
            }
            return null;
        }

        void ApplyPose()
        {
            if (helmet == null) return;

            GetPose(headAnchor, _wornLocalPos, _wornLocalRot, out Vector3 wornPos, out Quaternion wornRot);
            GetPose(holdAnchor, _heldLocalPos, _heldLocalRot, out Vector3 heldPos, out Quaternion heldRot);
            ShowPose(wornPos, wornRot, out Vector3 showPos, out Quaternion showRot);

            if (_t <= 0f) { helmet.SetPositionAndRotation(wornPos, wornRot); return; }
            if (_t >= 1f) { helmet.SetPositionAndRotation(heldPos, heldRot); return; }

            if (_t <= Presented)
            {
                // Straight up off the head first, THEN out in front — the two overlap in the middle so it
                // flows. Lifting and travelling at once would drag the helmet through the face.
                float k = _t / Presented;
                float lift = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.45f, k));
                float carry = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 1f, k));

                Vector3 clear = wornPos + Vector3.up * (liftHeight * lift);
                helmet.SetPositionAndRotation(Vector3.Lerp(clear, showPos, carry),
                                              Quaternion.Slerp(wornRot, showRot, carry));
                return;
            }

            // And down to the hip, bowed so it swings rather than slides.
            float c = Mathf.SmoothStep(0f, 1f, (_t - Presented) / Presented);
            helmet.SetPositionAndRotation(
                Vector3.Lerp(showPos, heldPos, c) + Vector3.up * (arcHeight * Mathf.Sin(c * Mathf.PI)),
                Quaternion.Slerp(showRot, heldRot, c));
        }

        /// <summary>
        /// Where it is held up: out in front of the body at the head's height, turning.
        ///
        /// Computed from the BODY's forward rather than the head bone's, so it stays put in front of you
        /// while you look around — anchoring it to the head would swing a helmet round the room every
        /// time the mouse moved. In first person this lands in the middle of the view, which is the whole
        /// point of the pause.
        /// </summary>
        void ShowPose(Vector3 wornPos, Quaternion wornRot, out Vector3 pos, out Quaternion rot)
        {
            if (!showItOff)
            {
                // No hold: the waypoint collapses back to "just clear of the head" and the move is the
                // two-part one it always was.
                pos = wornPos + Vector3.up * liftHeight;
                rot = wornRot;
                return;
            }

            pos = wornPos + transform.forward * showDistance + Vector3.up * showHeight;
            rot = Quaternion.AngleAxis(_spin, Vector3.up) * wornRot
                  * Quaternion.Euler(showTiltDegrees, 0f, 0f);
        }

        /// <summary>
        /// The one box, drawn solid as well as wired. It is a thing you position by eye and walk into, so
        /// it has to be findable in a crowded Scene view — a wireframe alone disappears into the
        /// greenhouse.
        /// </summary>
        void DrawTrigger()
        {
            if (!HasTrigger) return;

            var green = new Color(0.35f, 1f, 0.45f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(trigger.center.position, trigger.center.rotation, Vector3.one);
            Gizmos.color = new Color(green.r, green.g, green.b, 0.14f);
            Gizmos.DrawCube(Vector3.zero, trigger.size);
            Gizmos.color = green;
            Gizmos.DrawWireCube(Vector3.zero, trigger.size);
            Gizmos.matrix = Matrix4x4.identity;

#if UNITY_EDITOR
            UnityEditor.Handles.color = green;
            UnityEditor.Handles.Label(trigger.center.position + Vector3.up * (trigger.size.y * 0.5f + 0.5f),
                "STEP HERE → helmet comes off, once, and stays off");
#endif
        }

        void DrawZone(Zone z)
        {
            if (z == null || z.center == null) return;

            Gizmos.color = zoneColor;
            Gizmos.matrix = Matrix4x4.TRS(z.center.position, z.center.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, z.size);

            // The dead band, drawn as the inner box you must actually reach for the crossing to count.
            if (boundaryMargin > 0.01f)
            {
                Vector3 inner = new Vector3(Mathf.Max(0.1f, z.size.x - boundaryMargin * 2f),
                                            Mathf.Max(0.1f, z.size.y - boundaryMargin * 2f),
                                            Mathf.Max(0.1f, z.size.z - boundaryMargin * 2f));
                Gizmos.color = new Color(zoneColor.r, zoneColor.g, zoneColor.b, zoneColor.a * 0.35f);
                Gizmos.DrawWireCube(Vector3.zero, inner);
            }

            Gizmos.matrix = Matrix4x4.identity;
#if UNITY_EDITOR
            UnityEditor.Handles.color = zoneColor;
            UnityEditor.Handles.Label(z.center.position + Vector3.up * (z.size.y * 0.5f + 0.4f),
                $"helmet off inside: {z.label}");
#endif
        }

        static void GetPose(Transform anchor, Vector3 localPos, Quaternion localRot,
                            out Vector3 pos, out Quaternion rot)
        {
            if (anchor == null) { pos = localPos; rot = localRot; return; }
            pos = anchor.TransformPoint(localPos);
            rot = anchor.rotation * localRot;
        }

        /// <summary>Send the right hand to wherever the helmet is right now, easing in and out.</summary>
        void DriveArm()
        {
            if (!reachForIt || locomotion == null || helmet == null) return;
            if (!locomotion.HasReachArm) return;
            if (hand != null && hand.IsBusy) return;      // it's eating; leave the arm alone

            // Full commitment through the middle of the move, fading at both ends so the arm rejoins the
            // walk swing instead of snapping back to it.
            float blend = Mathf.Min(Mathf.InverseLerp(0f, 0.18f, _t),
                                    Mathf.InverseLerp(1f, 0.82f, _t));
            if (blend <= 0.001f) return;

            Vector3 target = locomotion.ClampToArmReach(helmet.position, _gripRadius);
            locomotion.ApplyHandTargetNow(target, blend, _gripRadius);
        }

        Vector3 Probe => transform.position + Vector3.up * 0.9f;   // mid-body

        // Reused rather than allocated per call: this is read every LateUpdate and it only ever mirrors
        // the two legacy fields.
        readonly Zone _legacy = new Zone("legacy", null, Vector3.zero);

        /// <summary>True if any zone at all is wired up, list or legacy.</summary>
        public bool HasAnyZone
        {
            get
            {
                if (pressurizedZone != null) return true;
                if (zones == null) return false;
                for (int i = 0; i < zones.Count; i++)
                    if (zones[i] != null && zones[i].center != null) return true;
                return false;
            }
        }

        /// <summary>
        /// Metres inside the zone this point sits deepest in, negative if it is outside every one. The
        /// deepest rather than the first, so overlapping zones behave like one merged volume instead of
        /// fighting at the seam.
        /// </summary>
        public float ZoneDepth(Vector3 worldPoint)
        {
            float best = float.NegativeInfinity;

            if (zones != null)
            {
                for (int i = 0; i < zones.Count; i++)
                {
                    if (zones[i] == null) continue;
                    best = Mathf.Max(best, zones[i].Depth(worldPoint));
                }
            }

            if (pressurizedZone != null)
            {
                _legacy.center = pressurizedZone;
                _legacy.size = zoneSize;
                best = Mathf.Max(best, _legacy.Depth(worldPoint));
            }

            return best;
        }

        public bool ZoneContains(Vector3 worldPoint) => ZoneDepth(worldPoint) >= 0f;

        /// <summary>The zone the point is deepest inside, or null. Used by the builder tool's readout.</summary>
        public Zone ZoneAt(Vector3 worldPoint)
        {
            Zone best = null;
            float bestDepth = float.NegativeInfinity;
            if (zones != null)
            {
                for (int i = 0; i < zones.Count; i++)
                {
                    if (zones[i] == null) continue;
                    float d = zones[i].Depth(worldPoint);
                    if (d > bestDepth) { bestDepth = d; best = zones[i]; }
                }
            }
            return best;
        }

        void Play(AudioClip clip)
        {
            if (audioSource != null && clip != null) audioSource.PlayOneShot(clip);
        }

        bool TogglePressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.hKey.wasPressedThisFrame;
#else
            return false;
#endif
        }

        // ------------------------------------------------------------------ scene view

        void OnDrawGizmos()
        {
            if (!showGizmo) return;

            if (onceOnly) DrawTrigger();
            else
            {
                if (zones != null)
                    for (int i = 0; i < zones.Count; i++)
                        DrawZone(zones[i]);

                if (pressurizedZone != null)
                {
                    _legacy.center = pressurizedZone;
                    _legacy.size = zoneSize;
                    _legacy.label = "legacy zone — move me into the list";
                    DrawZone(_legacy);
                }
            }

            if (helmet == null) return;

            GetPose(headAnchor, _wornLocalPos, _wornLocalRot, out Vector3 wornPos, out Quaternion wornRot);
            GetPose(holdAnchor, _heldLocalPos, _heldLocalRot, out Vector3 heldPos, out _);
            ShowPose(wornPos, wornRot, out Vector3 showPos, out _);

            // The path it will travel: up off the head, out in front, then round to the hip.
            Gizmos.color = new Color(1f, 0.85f, 0.35f, 0.95f);
            Vector3 clear = wornPos + Vector3.up * liftHeight;
            Gizmos.DrawLine(wornPos, clear);
            Gizmos.DrawLine(clear, showPos);
            Vector3 prev = showPos;
            for (int i = 1; i <= 12; i++)
            {
                float k = i / 12f;
                Vector3 p = Vector3.Lerp(showPos, heldPos, k) + Vector3.up * (arcHeight * Mathf.Sin(k * Mathf.PI));
                Gizmos.DrawLine(prev, p);
                prev = p;
            }

            Gizmos.color = new Color(0.5f, 0.9f, 1f, 0.9f);
            Gizmos.DrawWireSphere(wornPos, _gripRadius);
            Gizmos.color = new Color(1f, 0.7f, 0.3f, 0.9f);
            Gizmos.DrawWireSphere(heldPos, _gripRadius);
            if (showItOff)
            {
                Gizmos.color = new Color(0.6f, 1f, 0.6f, 0.9f);
                Gizmos.DrawWireSphere(showPos, _gripRadius * 1.15f);
            }

#if UNITY_EDITOR
            UnityEditor.Handles.color = new Color(0.5f, 0.9f, 1f);
            UnityEditor.Handles.Label(wornPos + Vector3.up * (_gripRadius + 0.05f), "worn");
            UnityEditor.Handles.color = new Color(1f, 0.7f, 0.3f);
            UnityEditor.Handles.Label(heldPos + Vector3.up * (_gripRadius + 0.05f), "carried");
            if (showItOff)
            {
                UnityEditor.Handles.color = new Color(0.6f, 1f, 0.6f);
                UnityEditor.Handles.Label(showPos + Vector3.up * (_gripRadius + 0.08f),
                                          $"held up for {showSeconds:0.0} s");
            }
#endif
        }
    }
}
