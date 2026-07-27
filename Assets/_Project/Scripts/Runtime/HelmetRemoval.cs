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
    /// <b>What sets it off.</b> Crossing the boundary of the pressurized zone — EDGE-triggered, so
    /// walking into the biodome takes it off, walking out puts it back, and the H key still works
    /// anywhere without the zone immediately arguing with you.
    ///
    /// Unscaled time throughout, like everything else the player watches.
    /// </summary>
    [DefaultExecutionOrder(110)]
    [DisallowMultipleComponent]
    public sealed class HelmetRemoval : MonoBehaviour
    {
        public enum State { Worn, TakingOff, Held, PuttingOn }

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

        [Header("Pressurized zones")]
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

        [Header("Motion (real seconds, immune to sim fast-forward)")]
        [Min(0.1f)] public float takeOffSeconds = 1.5f;
        [Min(0.1f)] public float putOnSeconds = 1.3f;
        [Tooltip("How far the helmet rises straight up before it travels — it has to clear the head " +
                 "before it can go anywhere else.")]
        [Min(0f)] public float liftHeight = 0.30f;
        [Tooltip("Extra bow in the path down to the hip, so it swings rather than slides.")]
        [Min(0f)] public float arcHeight = 0.12f;

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
        public bool IsMoving => CurrentState == State.TakingOff || CurrentState == State.PuttingOn;

        float _t;              // 0 = on the head, 1 = held at the hip
        bool _wasInside;

        void Awake()
        {
            if (locomotion == null) locomotion = GetComponentInChildren<AstronautLocomotionVisual>();
            if (hand == null) hand = GetComponentInChildren<HandActionController>();
        }

        void Start()
        {
            // Seed the edge detector with where we actually are, so spawning inside the zone is not read
            // as a crossing. The helmet still starts ON unless matchZoneOnStart says otherwise: the first
            // time you cross the boundary in either direction you get the full move, which is the point.
            _wasInside = ZoneContains(Probe);
            if (matchZoneOnStart && _wasInside) SetImmediate(off: true);
            else ApplyPose();
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Lift the helmet off. Returns false if it is already off or mid-move.</summary>
        public bool TakeOff()
        {
            if (CurrentState != State.Worn || helmet == null) return false;
            CurrentState = State.TakingOff;
            Play(unsealClip);
            return true;
        }

        /// <summary>Put it back on. Returns false if it is already on or mid-move.</summary>
        public bool PutOn()
        {
            if (CurrentState != State.Held || helmet == null) return false;
            CurrentState = State.PuttingOn;
            Play(sealClip);
            return true;
        }

        public void Toggle()
        {
            if (CurrentState == State.Worn) TakeOff();
            else if (CurrentState == State.Held) PutOn();
        }

        /// <summary>Snap to either end without animating — the editor's preview, and the Start pose.</summary>
        public void SetImmediate(bool off)
        {
            _t = off ? 1f : 0f;
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

            // Edge-triggered, not level-triggered: level-triggering would undo every manual press the
            // instant it was made, because standing still outside the zone permanently "wants" it on.
            //
            // The margin is what stops it chattering. A boundary you are standing ON is crossed and
            // re-crossed by every dip in the ground, and each crossing restarts a 1.5 s animation — the
            // symptom is a helmet that comes off and goes back on continuously as you walk. You now have
            // to travel boundaryMargin metres PAST the wall before the crossing counts, in either
            // direction, which leaves a dead band twice that wide around every face of every zone.
            float depth = ZoneDepth(Probe);
            bool inside = _wasInside ? depth > -boundaryMargin : depth > boundaryMargin;
            if (inside != _wasInside)
            {
                _wasInside = inside;
                // Turning back in the doorway reverses the move rather than being ignored: the two
                // transitions share _t, so flipping the state just runs the same travel the other way.
                if (inside)
                    { if (CurrentState == State.PuttingOn) CurrentState = State.TakingOff; else TakeOff(); }
                else
                    { if (CurrentState == State.TakingOff) CurrentState = State.PuttingOn; else PutOn(); }
            }

            switch (CurrentState)
            {
                case State.TakingOff:
                    _t = Mathf.MoveTowards(_t, 1f, dt / takeOffSeconds);
                    if (_t >= 1f) { CurrentState = State.Held; onHelmetOff?.Invoke(); }
                    break;
                case State.PuttingOn:
                    _t = Mathf.MoveTowards(_t, 0f, dt / putOnSeconds);
                    if (_t <= 0f) { CurrentState = State.Worn; onHelmetOn?.Invoke(); }
                    break;
            }

            ApplyPose();
            if (IsMoving) DriveArm();
        }

        void ApplyPose()
        {
            if (helmet == null) return;

            GetPose(headAnchor, _wornLocalPos, _wornLocalRot, out Vector3 wornPos, out Quaternion wornRot);
            GetPose(holdAnchor, _heldLocalPos, _heldLocalRot, out Vector3 heldPos, out Quaternion heldRot);

            if (_t <= 0f) { helmet.SetPositionAndRotation(wornPos, wornRot); return; }
            if (_t >= 1f) { helmet.SetPositionAndRotation(heldPos, heldRot); return; }

            // Straight up off the head first, THEN across to the hip — the two overlap in the middle so
            // it flows. Lifting and travelling at once would drag the helmet through the face.
            float lift = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.45f, _t));
            float carry = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 1f, _t));

            Vector3 clear = wornPos + Vector3.up * (liftHeight * lift);
            Vector3 pos = Vector3.Lerp(clear, heldPos, carry)
                          + Vector3.up * (arcHeight * Mathf.Sin(carry * Mathf.PI));
            helmet.SetPositionAndRotation(pos, Quaternion.Slerp(wornRot, heldRot, carry));
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

            if (helmet == null) return;

            GetPose(headAnchor, _wornLocalPos, _wornLocalRot, out Vector3 wornPos, out _);
            GetPose(holdAnchor, _heldLocalPos, _heldLocalRot, out Vector3 heldPos, out _);

            // The path it will travel: up off the head, then round to the hip.
            Gizmos.color = new Color(1f, 0.85f, 0.35f, 0.95f);
            Vector3 clear = wornPos + Vector3.up * liftHeight;
            Gizmos.DrawLine(wornPos, clear);
            Vector3 prev = clear;
            for (int i = 1; i <= 12; i++)
            {
                float k = i / 12f;
                Vector3 p = Vector3.Lerp(clear, heldPos, k) + Vector3.up * (arcHeight * Mathf.Sin(k * Mathf.PI));
                Gizmos.DrawLine(prev, p);
                prev = p;
            }

            Gizmos.color = new Color(0.5f, 0.9f, 1f, 0.9f);
            Gizmos.DrawWireSphere(wornPos, _gripRadius);
            Gizmos.color = new Color(1f, 0.7f, 0.3f, 0.9f);
            Gizmos.DrawWireSphere(heldPos, _gripRadius);

#if UNITY_EDITOR
            UnityEditor.Handles.color = new Color(0.5f, 0.9f, 1f);
            UnityEditor.Handles.Label(wornPos + Vector3.up * (_gripRadius + 0.05f), "worn");
            UnityEditor.Handles.color = new Color(1f, 0.7f, 0.3f);
            UnityEditor.Handles.Label(heldPos + Vector3.up * (_gripRadius + 0.05f), "carried");
#endif
        }
    }
}
