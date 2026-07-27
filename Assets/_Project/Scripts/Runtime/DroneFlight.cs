using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// Press E near the drone and it spins up, lifts off its pad, flies a looping route and comes back
    /// when you press E again.
    ///
    /// <b>This component lives on the PAD, not on the drone.</b> That is the whole design. The
    /// interaction sensor finds an interactable by walking up from a trigger collider
    /// (<c>GetComponentInParent</c>), so the trigger has to sit under whatever implements
    /// <see cref="IInteractable"/> — and if that were the drone itself, the trigger would take off with
    /// it and there would be no way left to call it back. The pad stays on the ground holding the
    /// trigger; it moves the drone's transform from a distance. Same lesson as hanging a door's prompt on
    /// its hinge rather than on the panel.
    ///
    /// <b>The route is a closed Catmull-Rom curve through the waypoints</b>, so six markers give a smooth
    /// loop rather than a hexagon with corners the drone snaps around. Drag any marker in the scene view
    /// and the path bends with it; the drone reads their positions live, so this works in play mode too.
    /// With fewer than three markers it falls back to a plain circle overhead, which is what a
    /// hand-added component with nothing wired should do rather than sit still.
    ///
    /// The rest pose is SERIALIZED by the builder tool, never captured at Awake — the same trap the doors
    /// and the helmet have. Capturing on Awake means an editor preview left mid-flight becomes the new
    /// "parked" pose, and the drone's home creeps a little further out on every rebuild.
    ///
    /// Unscaled time throughout, like everything else the player watches: the sim fast-forwards to 16x.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DroneFlight : MonoBehaviour, IInteractable
    {
        public enum State { Docked, Ascending, ToRoute, Flying, ToHome, Descending }

        [Header("Parts (wired by the drone builder tool)")]
        [Tooltip("The drone body that actually moves. This component stays behind on the pad.")]
        public Transform body;
        [Tooltip("Rotor discs, spun about their own axis while the throttle is up.")]
        public List<Transform> rotors = new List<Transform>();
        [Tooltip("Markers the route passes through. Three or more; drag them to reshape the flight.")]
        public List<Transform> waypoints = new List<Transform>();

        [Header("Route")]
        [Tooltip("Used only when there are fewer than three waypoints — a plain circle over the pad.")]
        [Min(1f)] public float patrolRadius = 14f;
        [Min(1f)] public float patrolHeight = 8f;
        [Min(0.5f)] public float cruiseSpeed = 6f;
        [Min(0.2f)] public float climbSpeed = 2.5f;
        [Min(0.2f)] public float landSpeed = 1.6f;

        [Header("Feel")]
        [Tooltip("Seconds for the rotors to reach full speed. It will not leave the pad below 60%.")]
        [Min(0.1f)] public float spinUpSeconds = 1.8f;
        [Min(0f)] public float rotorRpm = 900f;
        public Vector3 rotorAxis = Vector3.up;
        [Tooltip("Height of the idle wobble while cruising, in metres.")]
        [Min(0f)] public float bobHeight = 0.35f;
        [Min(0.2f)] public float bobPeriod = 3.5f;
        [Tooltip("How far it rolls into a turn. 0 flies flat.")]
        [Range(0f, 60f)] public float bankDegrees = 18f;
        [Min(0.2f)] public float turnResponse = 3.5f;
        [Tooltip("Degrees to add about Y if the model's nose does not point along its own +Z.")]
        [Range(-180f, 180f)] public float yawOffset;

        [Header("Prompts")]
        public string launchPrompt = "[E] Launch the drone";
        public string recallPrompt = "[E] Call the drone back";
        [Tooltip("Shown while it is on its way home and cannot be re-tasked yet.")]
        public string busyPrompt = "[E] Launch the drone";

        [Header("Audio (optional — drop clips in later)")]
        public AudioSource audioSource;
        [Tooltip("Looped, pitched and faded with the throttle.")]
        public AudioClip rotorLoop;

        [Header("Events")]
        public UnityEvent onLaunched;
        public UnityEvent onLanded;

        [Header("Scene view")]
        public bool showGizmo = true;

        // Home is stored in the PAD's local space, not in world space: the pad is a sibling of the drone
        // under the same import, so moving or rescaling that import carries both and the parked pose
        // stays correct. A world-space home would silently drift off the pad.
        [SerializeField, HideInInspector] Vector3 _homeLocalPos;
        [SerializeField, HideInInspector] Quaternion _homeLocalRot = Quaternion.identity;
        [SerializeField, HideInInspector] bool _homeCaptured;

        public State CurrentState { get; private set; } = State.Docked;
        public bool IsFlying => CurrentState != State.Docked;
        public bool HasHome => _homeCaptured;

        public Vector3 HomePos => transform.TransformPoint(_homeLocalPos);
        public Quaternion HomeRot => transform.rotation * _homeLocalRot;
        Vector3 HoverPos => HomePos + Vector3.up * patrolHeight;

        bool _want;             // the player's switch: true = be in the air
        float _throttle;        // 0 parked, 1 rotors at speed
        int _seg;               // which span of the route we are on
        float _t;               // 0..1 across that span
        float _lastYaw;

        // ------------------------------------------------------------------ authoring

        /// <summary>Adopt the drone's current placement as its parked pose. Called by the builder tool.</summary>
        public void CaptureHome()
        {
            if (body == null) return;
            _homeLocalPos = transform.InverseTransformPoint(body.position);
            _homeLocalRot = Quaternion.Inverse(transform.rotation) * body.rotation;
            _homeCaptured = true;
        }

        /// <summary>Put the drone back on its pad with no animation — the editor's reset.</summary>
        public void SetImmediateDocked()
        {
            _want = false;
            _throttle = 0f;
            CurrentState = State.Docked;
            if (body != null && _homeCaptured) body.SetPositionAndRotation(HomePos, HomeRot);
        }

        // ------------------------------------------------------------------ interaction

        public string Prompt => _want ? recallPrompt : (CurrentState == State.Docked ? launchPrompt : busyPrompt);

        public bool CanInteract(InteractionSensor sensor) => body != null && _homeCaptured;

        public void Interact(InteractionSensor sensor) => Toggle();

        /// <summary>
        /// One switch rather than one command per state. Every state checks it each frame and turns
        /// toward the counterpart, so pressing E halfway through a landing simply flies it back up —
        /// there is no illegal ordering to guard against and no way to wedge it.
        /// </summary>
        public void Toggle()
        {
            _want = !_want;
            if (_want) onLaunched?.Invoke();
        }

        // ------------------------------------------------------------------ loop

        void Start()
        {
            if (body != null && _homeCaptured) body.SetPositionAndRotation(HomePos, HomeRot);
        }

        void Update()
        {
            if (body == null || !_homeCaptured) return;

            float dt = Time.unscaledDeltaTime;

            _throttle = Mathf.MoveTowards(_throttle, CurrentState == State.Docked ? 0f : 1f,
                                          dt / spinUpSeconds);

            switch (CurrentState)
            {
                case State.Docked: TickDocked(); break;
                case State.Ascending: TickAscending(dt); break;
                case State.ToRoute: TickToRoute(dt); break;
                case State.Flying: TickFlying(dt); break;
                case State.ToHome: TickToHome(dt); break;
                case State.Descending: TickDescending(dt); break;
            }

            SpinRotors(dt);
            DriveAudio();
        }

        void TickDocked()
        {
            body.SetPositionAndRotation(HomePos, HomeRot);
            if (_want) CurrentState = State.Ascending;
        }

        void TickAscending(float dt)
        {
            if (!_want) { CurrentState = State.Descending; return; }

            // It does not leave the ground on a cold rotor. Cheap, and it is the difference between a
            // drone taking off and a prop being teleported upward.
            if (_throttle < 0.6f) return;

            body.position = Vector3.MoveTowards(body.position, HoverPos, climbSpeed * dt);
            LevelOut(dt);
            if ((body.position - HoverPos).sqrMagnitude < 0.04f) EnterToRoute();
        }

        /// <summary>
        /// Choose the rejoin point ONCE, on the way in. Re-picking the nearest marker every frame sounds
        /// harmless and is not: as the drone closes on one marker another becomes nearer, the target
        /// jumps, and it crabs sideways between two of them instead of arriving at either.
        /// </summary>
        void EnterToRoute()
        {
            SnapToNearestSpan();
            CurrentState = State.ToRoute;
        }

        void TickToRoute(float dt)
        {
            if (!_want) { CurrentState = State.ToHome; return; }

            Vector3 target = Sample(_seg, _t);
            body.position = Vector3.MoveTowards(body.position, target, cruiseSpeed * dt);
            FaceTravel(target - body.position, dt);

            if ((body.position - target).sqrMagnitude < 0.25f) CurrentState = State.Flying;
        }

        void TickFlying(float dt)
        {
            if (!_want) { CurrentState = State.ToHome; return; }

            Advance(cruiseSpeed * dt);

            Vector3 here = Sample(_seg, _t);
            Vector3 ahead = SampleAhead(1.2f);
            body.position = here + Vector3.up * Bob();
            FaceTravel(ahead - here, dt);
        }

        void TickToHome(float dt)
        {
            if (_want) { EnterToRoute(); return; }

            body.position = Vector3.MoveTowards(body.position, HoverPos, cruiseSpeed * dt);
            FaceTravel(HoverPos - body.position, dt);
            if ((body.position - HoverPos).sqrMagnitude < 0.25f) CurrentState = State.Descending;
        }

        void TickDescending(float dt)
        {
            if (_want) { CurrentState = State.Ascending; return; }

            body.position = Vector3.MoveTowards(body.position, HomePos, landSpeed * dt);
            body.rotation = Quaternion.Slerp(body.rotation, HomeRot, 1f - Mathf.Exp(-turnResponse * dt));

            if ((body.position - HomePos).sqrMagnitude < 0.0025f)
            {
                body.SetPositionAndRotation(HomePos, HomeRot);
                CurrentState = State.Docked;
                onLanded?.Invoke();
            }
        }

        float Bob() => bobHeight * Mathf.Sin(Time.unscaledTime * (Mathf.PI * 2f / bobPeriod));

        // ------------------------------------------------------------------ orientation

        void FaceTravel(Vector3 direction, float dt)
        {
            Vector3 flat = new Vector3(direction.x, 0f, direction.z);
            if (flat.sqrMagnitude < 1e-5f) { LevelOut(dt); return; }

            float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;

            // Bank from how fast the heading is CHANGING, not from the heading itself — a drone leans
            // into a turn and stands up again on the straight. DeltaAngle keeps the ±180 wrap from
            // reading as a huge turn rate and flipping it onto its back once a lap.
            float rate = dt > 1e-5f ? Mathf.DeltaAngle(_lastYaw, yaw) / dt : 0f;
            _lastYaw = yaw;
            float roll = Mathf.Clamp(-rate * 0.35f, -bankDegrees, bankDegrees);

            Quaternion target = Quaternion.Euler(0f, yaw + yawOffset, 0f) *
                                Quaternion.AngleAxis(roll, Vector3.forward);
            body.rotation = Quaternion.Slerp(body.rotation, target, 1f - Mathf.Exp(-turnResponse * dt));
        }

        void LevelOut(float dt)
        {
            Vector3 e = body.rotation.eulerAngles;
            Quaternion target = Quaternion.Euler(0f, e.y, 0f);
            body.rotation = Quaternion.Slerp(body.rotation, target, 1f - Mathf.Exp(-turnResponse * dt));
        }

        void SpinRotors(float dt)
        {
            if (rotors == null || _throttle <= 0.001f) return;
            Vector3 axis = rotorAxis.sqrMagnitude < 1e-6f ? Vector3.up : rotorAxis.normalized;
            float degrees = rotorRpm * 6f * _throttle * dt;      // rpm -> degrees per second is x6
            for (int i = 0; i < rotors.Count; i++)
            {
                if (rotors[i] == null) continue;
                // Alternate direction: counter-rotating pairs are what a real quad does, and a set of
                // four discs all spinning the same way reads as wrong even if you cannot say why.
                rotors[i].Rotate(axis, (i % 2 == 0) ? degrees : -degrees, Space.Self);
            }
        }

        void DriveAudio()
        {
            if (audioSource == null || rotorLoop == null) return;

            if (_throttle > 0.01f)
            {
                if (!audioSource.isPlaying)
                {
                    audioSource.clip = rotorLoop;
                    audioSource.loop = true;
                    audioSource.Play();
                }
                audioSource.volume = _throttle;
                audioSource.pitch = 0.75f + 0.35f * _throttle;
            }
            else if (audioSource.isPlaying && audioSource.clip == rotorLoop)
            {
                audioSource.Stop();
            }
        }

        // ------------------------------------------------------------------ the route

        bool HasWaypoints
        {
            get
            {
                if (waypoints == null || waypoints.Count < 3) return false;
                int live = 0;
                for (int i = 0; i < waypoints.Count; i++)
                    if (waypoints[i] != null) live++;
                return live >= 3;
            }
        }

        int PointCount => HasWaypoints ? waypoints.Count : 8;

        Vector3 Point(int i)
        {
            int n = PointCount;
            i = ((i % n) + n) % n;                                    // closed loop, so wrap both ways

            if (HasWaypoints)
            {
                Transform t = waypoints[i];
                return t != null ? t.position : HoverPos;
            }

            float a = i / (float)n * Mathf.PI * 2f;
            return HoverPos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * patrolRadius;
        }

        /// <summary>Catmull-Rom through the markers: passes exactly through each one, smoothly.</summary>
        Vector3 Sample(int seg, float t)
        {
            Vector3 p0 = Point(seg - 1), p1 = Point(seg), p2 = Point(seg + 1), p3 = Point(seg + 2);
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) +
                           (-p0 + p2) * t +
                           (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                           (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        Vector3 SampleAhead(float metres)
        {
            int seg = _seg;
            float t = _t;
            float len = Mathf.Max(0.01f, Vector3.Distance(Point(seg), Point(seg + 1)));
            t += metres / len;

            int guard = 0;
            while (t >= 1f && guard++ < 64)
            {
                t -= 1f;
                seg++;
                len = Mathf.Max(0.01f, Vector3.Distance(Point(seg), Point(seg + 1)));
            }
            return Sample(seg, t);
        }

        void Advance(float metres)
        {
            // Span length is the straight-line distance between markers, so the curve is travelled a few
            // percent slower than cruiseSpeed on tight bends. Not worth an arc-length table for scenery.
            float len = Mathf.Max(0.01f, Vector3.Distance(Point(_seg), Point(_seg + 1)));
            _t += metres / len;

            int guard = 0;
            while (_t >= 1f && guard++ < 64)
            {
                _t -= 1f;
                _seg = (_seg + 1) % PointCount;
                len = Mathf.Max(0.01f, Vector3.Distance(Point(_seg), Point(_seg + 1)));
            }
        }

        /// <summary>Rejoin the route at whichever marker is closest, so a recall mid-lap is not a detour.</summary>
        void SnapToNearestSpan()
        {
            int best = 0;
            float bestSq = float.MaxValue;
            int n = PointCount;
            for (int i = 0; i < n; i++)
            {
                float d = (Point(i) - body.position).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = i; }
            }
            _seg = best;
            _t = 0f;
        }

        // ------------------------------------------------------------------ scene view

        void OnDrawGizmos()
        {
            if (!showGizmo) return;

            // The route, sampled the same way the drone flies it — so what you see is the actual path,
            // not a straight-line sketch of it.
            Gizmos.color = new Color(0.4f, 0.85f, 1f, 0.9f);
            int n = PointCount;
            Vector3 prev = Sample(0, 0f);
            for (int s = 0; s < n; s++)
            {
                for (int k = 1; k <= 10; k++)
                {
                    Vector3 p = Sample(s, k / 10f);
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
            }

            Gizmos.color = new Color(1f, 0.85f, 0.3f, 0.9f);
            for (int i = 0; i < n; i++) Gizmos.DrawWireSphere(Point(i), 0.4f);

            if (!_homeCaptured) return;

            Gizmos.color = new Color(0.5f, 1f, 0.6f, 0.95f);
            Gizmos.DrawWireSphere(HomePos, 0.5f);
            Gizmos.DrawLine(HomePos, HoverPos);

#if UNITY_EDITOR
            UnityEditor.Handles.color = new Color(0.5f, 1f, 0.6f);
            UnityEditor.Handles.Label(HomePos + Vector3.up * 0.7f,
                Application.isPlaying ? $"pad · {CurrentState}" : "pad (parked here)");
#endif
        }
    }
}
