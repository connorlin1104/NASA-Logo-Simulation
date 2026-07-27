using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// A two-stop cargo elevator: the car travels straight up by <see cref="travelHeight"/> and back,
    /// opening its <see cref="SlidingDoorPair"/> at each end.
    ///
    /// The Scene view shows the whole run before you ever press Play — the car's box at the BOTTOM stop
    /// in green, the same box at the TOP stop in cyan, four corner rails between them and the travel
    /// distance written on the shaft. Change Travel Height and the top box moves with it, so lining the
    /// car up with a balcony is a drag-and-read job rather than a play-test.
    ///
    /// <b>Why riders are carried by hand.</b> A <see cref="CharacterController"/> is not pushed by a
    /// moving collider — stand in a rising lift and Unity will happily leave you standing in mid-air
    /// while the floor climbs past you. So while the car moves, anyone inside the ride zone is moved by
    /// the same delta the car just travelled.
    ///
    /// Unscaled time throughout: the lift takes its real seconds even at 16x sim speed, like the
    /// airlock and the doors.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElevatorController : MonoBehaviour
    {
        public enum State { Idle, ClosingDoors, Moving, OpeningDoors }

        [Header("Parts")]
        [Tooltip("The moving car — the 'cargo' group. Everything parented under it rides along.")]
        public Transform car;
        [Tooltip("The two-panel door. Optional: with none the car just moves.")]
        public SlidingDoorPair doors;
        [Tooltip("Trigger volume that decides who rides. Built as a child of the car so it travels with " +
                 "it; anyone standing inside is carried.")]
        public BoxCollider rideZone;

        [Header("Travel")]
        [Tooltip("How far above its authored (bottom) position the car rises, in metres. The cyan box " +
                 "in the Scene view is where that puts it.")]
        public float travelHeight = 4.7f;
        [Tooltip("Metres per second. A cargo lift is unhurried.")]
        [Min(0.05f)] public float speed = 1.2f;
        [Tooltip("Open the doors as soon as the scene starts, so the car is waiting to be walked into.")]
        public bool startWithDoorsOpen = true;

        [Header("Events")]
        public UnityEvent onDeparted;
        public UnityEvent onArrived;

        [Header("Scene view")]
        public bool showGizmo = true;
        public Color bottomColor = new Color(0.35f, 1f, 0.45f);
        public Color topColor = new Color(0.35f, 0.85f, 1f);

        public State CurrentState { get; private set; } = State.Idle;
        /// <summary>0 = at the bottom stop, 1 = at the top stop.</summary>
        public float Travel01 { get; private set; }
        public bool AtTop => CurrentState == State.Idle && Travel01 > 0.999f;
        public bool AtBottom => CurrentState == State.Idle && Travel01 < 0.001f;
        /// <summary>A call may only start from a stop with the machine idle.</summary>
        public bool CanRequest => CurrentState == State.Idle && car != null;

        // The bottom stop is SERIALIZED, not captured at Awake — the station tool can park the car at the
        // top in the editor so you can check it lines up with the balcony, and a captured-at-Awake rest
        // would quietly adopt that preview as the new bottom after the next script reload.
        [SerializeField, HideInInspector] Vector3 _restLocal;
        [SerializeField, HideInInspector] bool _restCaptured;

        bool _wantTop;
        AstronautController _astronaut;

        void Awake()
        {
            CaptureRest();
            _astronaut = FindAnyObjectByType<AstronautController>();
        }

        void Start()
        {
            if (startWithDoorsOpen && doors != null) doors.SetImmediate(true);
        }

        void CaptureRest()
        {
            if (_restCaptured || car == null) return;
            CaptureBottomStop();
        }

        /// <summary>Adopt the car's current pose as the BOTTOM stop. Called by the builder tool while the
        /// car is still where the model author left it.</summary>
        public void CaptureBottomStop()
        {
            if (car == null) return;
            _restLocal = car.localPosition;
            _restCaptured = true;
        }

        /// <summary>Park the car at a stop without running the cycle — the editor's preview.</summary>
        public void SetImmediate(bool atTop)
        {
            CaptureRest();
            if (car == null) return;
            Travel01 = atTop ? 1f : 0f;
            car.localPosition = _restLocal + LocalTravel * Travel01;
            CurrentState = State.Idle;
        }

        // ------------------------------------------------------------------ commands

        /// <summary>Send the car to a stop (and open up there). Returns false if it is already busy.</summary>
        public bool Request(bool goTop)
        {
            if (!CanRequest) return false;
            CaptureRest();

            // Already at the requested end: the call just means "let me in".
            if ((goTop && Travel01 > 0.999f) || (!goTop && Travel01 < 0.001f))
            {
                if (doors != null) doors.Open();
                return true;
            }

            _wantTop = goTop;
            if (doors != null)
            {
                doors.Close();
                CurrentState = State.ClosingDoors;
            }
            else
            {
                CurrentState = State.Moving;
                onDeparted?.Invoke();
            }
            return true;
        }

        /// <summary>Send the car to whichever stop it is not at.</summary>
        public bool Toggle() => Request(!(Travel01 > 0.5f));

        // ------------------------------------------------------------------ loop

        void Update()
        {
            if (car == null) return;
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            switch (CurrentState)
            {
                case State.ClosingDoors:
                    if (doors == null || doors.IsClosed)
                    {
                        CurrentState = State.Moving;
                        onDeparted?.Invoke();
                    }
                    break;

                case State.Moving:
                    MoveCar(dt);
                    break;

                case State.OpeningDoors:
                    if (doors == null || doors.IsOpen) CurrentState = State.Idle;
                    break;
            }
        }

        void MoveCar(float dt)
        {
            float rate = speed / Mathf.Max(0.05f, Mathf.Abs(travelHeight));
            Vector3 before = car.position;

            Travel01 = Mathf.MoveTowards(Travel01, _wantTop ? 1f : 0f, rate * dt);
            car.localPosition = _restLocal + LocalTravel * Travel01;

            CarryRider(car.position - before);

            if (Mathf.Approximately(Travel01, _wantTop ? 1f : 0f))
            {
                onArrived?.Invoke();
                if (doors != null)
                {
                    doors.Open();
                    CurrentState = State.OpeningDoors;
                }
                else
                {
                    CurrentState = State.Idle;
                }
            }
        }

        /// <summary>Move anyone standing in the ride zone by the same step the car just took.</summary>
        void CarryRider(Vector3 delta)
        {
            if (delta.sqrMagnitude < 1e-10f) return;
            if (_astronaut == null) _astronaut = FindAnyObjectByType<AstronautController>();
            if (_astronaut == null) return;

            // Mid-body probe, and a POLLED containment test rather than trigger events: a
            // CharacterController only reports trigger enter/exit while it moves, so a rider standing
            // perfectly still could be missed by events but never by a per-frame point test.
            if (!Contains(_astronaut.transform.position + Vector3.up * 0.4f)) return;

            var cc = _astronaut.GetComponent<CharacterController>();
            if (cc != null && cc.enabled) cc.Move(delta);
            else _astronaut.transform.position += delta;
        }

        public bool Contains(Vector3 worldPoint)
        {
            if (rideZone != null)
            {
                Vector3 local = rideZone.transform.InverseTransformPoint(worldPoint) - rideZone.center;
                Vector3 half = rideZone.size * 0.5f;
                return Mathf.Abs(local.x) <= half.x &&
                       Mathf.Abs(local.y) <= half.y &&
                       Mathf.Abs(local.z) <= half.z;
            }

            // No ride zone built: fall back to the car's own footprint, generously tall.
            if (!TryCarBounds(out Bounds b)) return false;
            b.Expand(new Vector3(0f, 2f, 0f));
            return b.Contains(worldPoint);
        }

        Vector3 LocalTravel
        {
            get
            {
                Vector3 world = Vector3.up * travelHeight;
                return car != null && car.parent != null ? car.parent.InverseTransformVector(world) : world;
            }
        }

        bool TryCarBounds(out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            if (car == null) return false;
            foreach (var r in car.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        // ------------------------------------------------------------------ scene view

        void OnDrawGizmos()
        {
            if (!showGizmo || car == null || !TryCarBounds(out Bounds live)) return;

            // At edit time the car sits at its authored pose, which IS the bottom stop. In Play it has
            // moved, so shift the live bounds back onto the captured rest before drawing.
            Vector3 toRest = Vector3.zero;
            if (Application.isPlaying && _restCaptured)
            {
                Vector3 restWorld = car.parent != null ? car.parent.TransformPoint(_restLocal) : _restLocal;
                toRest = restWorld - car.position;
            }

            var bottom = new Bounds(live.center + toRest, live.size);
            Vector3 travel = Vector3.up * travelHeight;
            var top = new Bounds(bottom.center + travel, bottom.size);

            Gizmos.color = bottomColor;
            Gizmos.DrawWireCube(bottom.center, bottom.size);
            Gizmos.color = topColor;
            Gizmos.DrawWireCube(top.center, top.size);

            // Corner rails: the shaft the car runs in.
            Gizmos.color = new Color(topColor.r, topColor.g, topColor.b, 0.45f);
            Vector3 e = bottom.extents;
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = bottom.center + new Vector3((i & 1) == 0 ? -e.x : e.x, e.y,
                                                             (i & 2) == 0 ? -e.z : e.z);
                Gizmos.DrawLine(corner, corner + travel);
            }

            // Chevrons up the centre line, pointing the way it travels.
            Gizmos.color = topColor;
            Vector3 a = bottom.center, b = top.center;
            Gizmos.DrawLine(a, b);
            float dir = Mathf.Sign(travelHeight == 0f ? 1f : travelHeight);
            for (int i = 1; i <= 3; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / 4f);
                float w = Mathf.Min(0.35f, Mathf.Max(0.1f, e.x * 0.4f));
                Gizmos.DrawLine(p, p + new Vector3(w, -0.25f * dir, 0f));
                Gizmos.DrawLine(p, p + new Vector3(-w, -0.25f * dir, 0f));
            }

            if (rideZone != null)
            {
                Gizmos.color = new Color(1f, 0.9f, 0.25f, 0.8f);
                Gizmos.matrix = rideZone.transform.localToWorldMatrix;
                Gizmos.DrawWireCube(rideZone.center, rideZone.size);
                Gizmos.matrix = Matrix4x4.identity;
            }

#if UNITY_EDITOR
            UnityEditor.Handles.color = bottomColor;
            UnityEditor.Handles.Label(bottom.center - Vector3.up * (e.y + 0.15f),
                $"BOTTOM  y = {bottom.center.y - e.y:0.00}");
            UnityEditor.Handles.color = topColor;
            UnityEditor.Handles.Label(top.center + Vector3.up * (e.y + 0.15f),
                $"TOP  y = {top.center.y - e.y:0.00}   (travel {travelHeight:0.00} m @ {speed:0.0} m/s)");
#endif
        }
    }

    /// <summary>
    /// The panel you press to work the lift. Put one at each stop (facing the doors) and one inside the
    /// car — the in-car one is parented to the car so it rides with you. Sits on a trigger collider on
    /// the Interactable layer, like every other interactable in the project.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ElevatorCallButton : MonoBehaviour, IInteractable
    {
        public enum Call { OtherEnd, Top, Bottom }

        public ElevatorController elevator;
        [Tooltip("OtherEnd = the button inside the car (sends it to whichever stop it isn't at). " +
                 "Top / Bottom = a landing button that calls the car to this floor.")]
        public Call call = Call.OtherEnd;

        public string Prompt
        {
            get
            {
                if (elevator == null) return string.Empty;
                if (!elevator.CanRequest) return "Elevator moving…";
                switch (call)
                {
                    case Call.Top: return elevator.AtTop ? "[E] Open the doors" : "[E] Call the elevator up";
                    case Call.Bottom: return elevator.AtBottom ? "[E] Open the doors" : "[E] Call the elevator down";
                    default: return elevator.AtTop ? "[E] Go down" : "[E] Go up";
                }
            }
        }

        // Always visible in range — the prompt doubles as the lift's status display, and Interact simply
        // does nothing while a cycle runs.
        public bool CanInteract(InteractionSensor sensor) => elevator != null;

        public void Interact(InteractionSensor sensor)
        {
            if (elevator == null) return;
            switch (call)
            {
                case Call.Top: elevator.Request(goTop: true); break;
                case Call.Bottom: elevator.Request(goTop: false); break;
                default: elevator.Toggle(); break;
            }
        }
    }
}
