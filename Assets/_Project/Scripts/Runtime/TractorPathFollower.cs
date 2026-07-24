using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    [System.Serializable] public class WaypointEvent : UnityEvent<int> { }
    [System.Serializable] public class PenEvent : UnityEvent<bool> { }

    /// <summary>
    /// Drives a tractor along a <see cref="CsvWaypointLoader"/> path to trace the NASA logo, kinematically
    /// (transform set directly) so the trace is exact and frame-rate independent. Spins the drive wheels,
    /// yaws the steer wheels, and toggles the mower pen so nothing is drawn across pen-up gaps.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TractorPathFollower : MonoBehaviour
    {
        public enum EndBehavior { Stop, Loop, PingPong }

        /// <summary>Which of a wheel's own LOCAL axes is the axle it rolls about.</summary>
        public enum WheelSpinAxis
        {
            /// <summary>The wheel mesh's longest bounding-box side.</summary>
            AutoLongestSide,
            /// <summary>The wheel mesh's shortest bounding-box side (a disc wheel's axle is usually this).</summary>
            AutoShortestSide,
            X, Y, Z,
            /// <summary>The explicit vector in <see cref="customWheelSpinAxis"/>.</summary>
            Custom,
        }

        [Header("Path source")]
        public CsvWaypointLoader loader;

        [Header("Movement")]
        [Min(0f)] public float moveSpeed = 6f;
        [Min(0f)] public float turnSpeedDeg = 240f;
        [Tooltip("Treated as reached within this distance (also snaps to the exact point).")]
        [Min(0f)] public float arriveThreshold = 0.05f;
        public EndBehavior onComplete = EndBehavior.Stop;
        [Tooltip("Instantly jump across pen-up gaps instead of driving across them.")]
        public bool teleportOnPenUp = false;
        [Tooltip("Keep the tractor body at this Y so the logo lies on the floor plane.")]
        public float fixedY = 0f;
        public bool autoStart = true;

        [Header("Wheels (visual)")]
        [Tooltip("Wheels rolled about the axle chosen by Wheel Spin Axis below.")]
        public Transform[] driveWheels;
        [Tooltip("Which of each wheel's LOCAL axes is the axle it spins about.\n" +
                 "• Auto Longest / Shortest Side — measured per wheel from its own mesh bounding box, so " +
                 "wheels mounted at different orientations each get the right axle.\n" +
                 "• X / Y / Z — one fixed local axis for every wheel.\n" +
                 "• Custom — the vector below.\n" +
                 "NOTE: a disc-shaped wheel's axle is normally its SHORTEST side (the thin direction); the " +
                 "longest sides are the diameter. If Auto Longest spins them wrong, try Auto Shortest.")]
        public WheelSpinAxis wheelSpinAxis = WheelSpinAxis.AutoLongestSide;
        [Tooltip("Used only when Wheel Spin Axis = Custom. The axle direction in the wheel's LOCAL space.")]
        public Vector3 customWheelSpinAxis = Vector3.right;
        [Tooltip("Auto-derived when Auto Wheel Radius is on: half the wheel's largest dimension PERPENDICULAR " +
                 "to the axle (i.e. the true rolling radius).")]
        [Min(0.001f)] public float wheelRadius = 0.4f;
        [Tooltip("Optional front wheels that visually yaw toward the turn direction.")]
        public Transform[] steerWheels;
        public float maxSteerAngleDeg = 28f;

        [Header("Mower")]
        public MowerController mower;
        [Tooltip("Rear-bottom brush transform; its world position drives the mowing visual. Mount it on the " +
                 "real plow/deck for the look you want — the trail is sub-sampled along the path each frame " +
                 "so a rear deck traces smoothly (a far-rear deck still rounds very sharp corners slightly).")]
        public Transform mowerAnchor;

        [Header("Model fit (keeps the tractor correct when you rescale it)")]
        [Tooltip("The visual model (e.g. Tractor_Model). If set, its lowest point is dropped onto the ground " +
                 "on each run, so shrinking or enlarging the tractor never makes it float or sink. Auto-found " +
                 "by name if left empty; the primitive stand-in needs none.")]
        public Transform visualRoot;
        [Tooltip("Re-seat the model on the ground at the start of each run (safe to leave on).")]
        public bool autoGroundModel = true;
        [Tooltip("Recompute wheel radius from the wheel size each run, so wheels spin at the right rate " +
                 "after a rescale. Turn off if you set Wheel Radius by hand.")]
        public bool autoWheelRadius = true;

        [Header("Events")]
        public UnityEvent onStarted;
        public UnityEvent onCompleted;
        public WaypointEvent onWaypointReached;
        public PenEvent onPenStateChanged;

        Vector3[] _wheelAxes;   // resolved per-wheel local axle, see ResolveWheels()
        WaypointPath _path;
        int _index;      // waypoint we are driving toward
        int _dir = 1;    // +1 forward, -1 reverse (ping-pong)
        bool _running;
        bool _penDown;

        public WaypointPath Path => _path;
        public bool IsRunning => _running;

        void Start()
        {
            if (autoStart) Begin();
        }

        /// <summary>Load the path (if needed), reset the mower, and start driving from the first waypoint.</summary>
        public void Begin()
        {
            _path = loader != null ? (loader.Current ?? loader.Load()) : null;
            if (_path == null || _path.Count < 2)
            {
                Debug.LogWarning("[TractorPathFollower] Need a loader with >= 2 waypoints to follow.", this);
                _running = false;
                return;
            }

            mower?.ResetVisual();
            FitModel();

            _dir = 1;
            _index = 0;
            SnapTo(_path.Points[0].position);
            _index = 1;                       // first drive target
            _running = true;

            _penDown = false;                 // force a fresh transition on the next EvaluatePen
            EvaluatePenForTarget();           // SetPen seeds the mower at the current brush position
            onStarted?.Invoke();
        }

        public void ResetRun() => Begin();

        /// <summary>
        /// Keep the tractor correct after the user rescales it. Grounding and wheel-spin rate both depend on
        /// the model's size, so they are re-derived here rather than baked once at swap time — resize the
        /// tractor in the Inspector and it still sits on the floor with wheels spinning at the right rate.
        /// The follower drives the ROOT along the path in world units, so the trace itself is scale-proof;
        /// only these cosmetic fits need refreshing.
        /// </summary>
        void FitModel()
        {
            ResolveWheels();

            // Drop the visual so its lowest point rests on the ground (root.y - fixedY).
            Transform model = visualRoot != null ? visualRoot : transform.Find("Tractor_Model");
            if (autoGroundModel && model != null && TryRendererBounds(model.gameObject, out Bounds b))
            {
                float ground = transform.position.y - fixedY;
                float dy = ground - b.min.y;
                if (Mathf.Abs(dy) > 1e-4f) model.position += new Vector3(0f, dy, 0f);
            }
        }

        /// <summary>
        /// Work out, per wheel, which of its LOCAL axes is the axle, and the true rolling radius. Measured
        /// from each wheel's own mesh bounding box (scaled by its transform), so it is independent of how
        /// the wheel is oriented in the world and survives rescaling the tractor.
        /// </summary>
        void ResolveWheels()
        {
            if (driveWheels == null) { _wheelAxes = null; return; }
            if (_wheelAxes == null || _wheelAxes.Length != driveWheels.Length)
                _wheelAxes = new Vector3[driveWheels.Length];

            float radius = 0f;
            for (int i = 0; i < driveWheels.Length; i++)
            {
                var w = driveWheels[i];
                if (w == null) { _wheelAxes[i] = Vector3.right; continue; }

                Vector3 size = LocalWheelSize(w);
                Vector3 axis = ResolveSpinAxis(size);
                _wheelAxes[i] = axis;
                radius = Mathf.Max(radius, HalfPerpendicularExtent(size, axis));
            }
            if (autoWheelRadius && radius > 1e-4f) wheelRadius = radius;
        }

        /// <summary>Wheel dimensions along its OWN local axes, in world units (mesh bounds x transform scale).</summary>
        static Vector3 LocalWheelSize(Transform w)
        {
            Vector3 s = Vector3.one;
            var mf = w.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) s = mf.sharedMesh.bounds.size;
            else
            {
                var smr = w.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null) s = smr.sharedMesh.bounds.size;
            }
            Vector3 sc = w.lossyScale;
            return new Vector3(Mathf.Abs(s.x * sc.x), Mathf.Abs(s.y * sc.y), Mathf.Abs(s.z * sc.z));
        }

        Vector3 ResolveSpinAxis(Vector3 size)
        {
            switch (wheelSpinAxis)
            {
                case WheelSpinAxis.X: return Vector3.right;
                case WheelSpinAxis.Y: return Vector3.up;
                case WheelSpinAxis.Z: return Vector3.forward;
                case WheelSpinAxis.Custom:
                    return customWheelSpinAxis.sqrMagnitude > 1e-6f
                        ? customWheelSpinAxis.normalized
                        : Vector3.right;
                case WheelSpinAxis.AutoShortestSide: return CardinalOfExtent(size, longest: false);
                default:                             return CardinalOfExtent(size, longest: true);
            }
        }

        /// <summary>The local cardinal axis along which the wheel is longest (or shortest).</summary>
        static Vector3 CardinalOfExtent(Vector3 size, bool longest)
        {
            int idx = 0;
            for (int k = 1; k < 3; k++)
                if (longest ? size[k] > size[idx] : size[k] < size[idx]) idx = k;
            return idx == 0 ? Vector3.right : idx == 1 ? Vector3.up : Vector3.forward;
        }

        /// <summary>Rolling radius = half the largest wheel dimension perpendicular to the axle.</summary>
        static float HalfPerpendicularExtent(Vector3 size, Vector3 axis)
        {
            int axle = 0;
            float best = Mathf.Abs(axis.x);
            if (Mathf.Abs(axis.y) > best) { best = Mathf.Abs(axis.y); axle = 1; }
            if (Mathf.Abs(axis.z) > best) axle = 2;

            float d = 0f;
            for (int k = 0; k < 3; k++)
                if (k != axle) d = Mathf.Max(d, size[k]);
            return d * 0.5f;
        }

        static bool TryRendererBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        void Update()
        {
            if (!_running || _path == null) return;

            float budget = moveSpeed * Time.deltaTime;
            float invSpeed = moveSpeed > 1e-6f ? 1f / moveSpeed : 0f;
            float totalMoved = 0f;
            int guard = 0;

            // The tractor can cross several waypoints in one frame (high move speed, or a sped-up run).
            // Rotation and the mower are therefore stepped INSIDE this loop, once per sub-move, instead of
            // once per frame — otherwise a rear-mounted mower deck turns the once-per-frame rotation lag
            // into faceted "choppy" jumps at corners. Distributing the turn over the sub-moves keeps the
            // per-frame turn budget identical on straights while tracing corners smoothly.
            while (_running && budget > 1e-6f && guard++ < 512)
            {
                Vector3 target = Flat(_path.Points[_index].position);
                Vector3 cur = Flat(transform.position);
                Vector3 to = target - cur;
                float dist = to.magnitude;

                if (!_penDown && teleportOnPenUp)
                {
                    transform.position = target;
                    if (!Advance()) break;
                    continue;                 // jump consumes no budget
                }

                float step;
                bool arrived;
                if (dist <= budget || dist <= arriveThreshold)
                {
                    transform.position = target;
                    step = dist;
                    budget -= dist;
                    arrived = true;
                }
                else
                {
                    transform.position = cur + (to / dist) * budget;
                    step = budget;
                    budget = 0f;
                    arrived = false;
                }
                totalMoved += step;

                // Turn toward this sub-move's direction, by the turn budget for the distance covered.
                if (dist > 1e-6f)
                {
                    Vector3 dir = to / dist;
                    Quaternion look = Quaternion.LookRotation(dir, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnSpeedDeg * step * invSpeed);
                    ApplySteer(dir);
                }

                SampleMower();                // record the deck position at this sub-step, on the true path

                if (arrived && !Advance()) break;
            }

            // Clamp Y (belt-and-braces; all motion above is already flat).
            Vector3 p = transform.position;
            if (!Mathf.Approximately(p.y, fixedY))
                transform.position = new Vector3(p.x, fixedY, p.z);

            SpinWheels(totalMoved);
        }

        void SampleMower()
        {
            if (_penDown && mower != null && mowerAnchor != null)
                mower.UpdateAt(MowerWorldPos());
        }

        bool Advance()
        {
            onWaypointReached?.Invoke(_index);
            int last = _path.Count - 1;

            if (_dir > 0)
            {
                if (_index >= last) return HandleEnd();
                _index++;
            }
            else
            {
                if (_index <= 0) return HandleEnd();
                _index--;
            }
            EvaluatePenForTarget();
            return true;
        }

        bool HandleEnd()
        {
            onCompleted?.Invoke();
            switch (onComplete)
            {
                case EndBehavior.Loop:
                    SetPen(false);            // lift before the wrap so no connector is drawn
                    _dir = 1;
                    _index = 0;
                    SnapTo(_path.Points[0].position);
                    _index = 1;
                    EvaluatePenForTarget();   // SetPen seeds the mower at the wrapped start position
                    return true;

                case EndBehavior.PingPong:
                    _dir = -_dir;
                    _index = Mathf.Clamp(_index + _dir, 0, _path.Count - 1);
                    EvaluatePenForTarget();
                    return true;

                default: // Stop
                    SetPen(false);
                    _running = false;
                    return false;
            }
        }

        /// <summary>Pen state of the edge we are about to travel. Edges are identified by their higher endpoint.</summary>
        void EvaluatePenForTarget()
        {
            int edgeHi = _dir > 0 ? _index : _index + 1;
            edgeHi = Mathf.Clamp(edgeHi, 1, _path.Count - 1);
            SetPen(_path.Points[edgeHi].penDown);
        }

        void SetPen(bool down)
        {
            if (_penDown == down) return;
            _penDown = down;
            // Seed the mower at the current brush position BEFORE starting a stroke, so a stroke that begins
            // mid-frame (fast traversal or a teleport across a pen-up gap) starts here, never from a stale spot.
            if (down && mower != null && mowerAnchor != null)
                mower.UpdateAt(MowerWorldPos());
            mower?.SetPenDown(down);
            onPenStateChanged?.Invoke(down);
        }

        void SnapTo(Vector3 position)
        {
            transform.position = new Vector3(position.x, fixedY, position.z);
        }

        Vector3 Flat(Vector3 v) => new Vector3(v.x, fixedY, v.z);

        /// <summary>
        /// Brush world position for the mowing visual: the anchor's ground XZ but lifted to the render-layer
        /// height baked into the waypoint we are currently drawing toward. The tractor body itself stays flat
        /// (see Flat / fixedY); only the mowed ribbon rides the layer so upper layers (letters) draw on top.
        /// </summary>
        Vector3 MowerWorldPos()
        {
            Vector3 a = mowerAnchor.position;
            int edgeHi = _dir > 0 ? _index : _index + 1;
            edgeHi = Mathf.Clamp(edgeHi, 1, _path.Count - 1);
            return new Vector3(a.x, _path.Points[edgeHi].position.y, a.z);
        }

        void SpinWheels(float distance)
        {
            if (driveWheels == null || distance <= 0f || wheelRadius <= 0f) return;
            float deg = distance / wheelRadius * Mathf.Rad2Deg;
            for (int i = 0; i < driveWheels.Length; i++)
            {
                if (driveWheels[i] == null) continue;
                Vector3 axis = (_wheelAxes != null && i < _wheelAxes.Length && _wheelAxes[i].sqrMagnitude > 1e-6f)
                    ? _wheelAxes[i]
                    : Vector3.right;
                driveWheels[i].Rotate(axis, deg, Space.Self);
            }
        }

        void ApplySteer(Vector3 worldDir)
        {
            if (steerWheels == null || steerWheels.Length == 0) return;
            Vector3 local = transform.InverseTransformDirection(worldDir);
            float steer = Mathf.Clamp(Mathf.Atan2(local.x, Mathf.Max(0.001f, local.z)) * Mathf.Rad2Deg,
                                      -maxSteerAngleDeg, maxSteerAngleDeg);
            for (int i = 0; i < steerWheels.Length; i++)
                if (steerWheels[i] != null)
                {
                    Vector3 e = steerWheels[i].localEulerAngles;
                    steerWheels[i].localEulerAngles = new Vector3(e.x, steer, e.z);
                }
        }

#if UNITY_EDITOR
        WaypointPath _gizmoCache;
        TextAsset _gizmoCacheAsset;

        void OnDrawGizmos()
        {
            // Preview the CSV path in the Scene view even before any models exist.
            WaypointPath path = _path;
            if ((path == null || path.IsEmpty) && loader != null)
            {
                if (Application.isPlaying)
                {
                    path = loader.Current;
                }
                else
                {
                    // Cache the edit-time parse; re-parse only when the assigned CSV asset changes,
                    // to avoid re-parsing (and log-spamming) on every Scene-view repaint.
                    if (_gizmoCache == null || _gizmoCacheAsset != loader.csvFile)
                    {
                        _gizmoCache = loader.Parse(loader.csvFile);
                        _gizmoCacheAsset = loader.csvFile;
                    }
                    path = _gizmoCache;
                }
            }
            if (path == null || path.Count < 1) return;

            var pts = path.Points;
            for (int i = 1; i < pts.Count; i++)
            {
                Gizmos.color = pts[i].penDown ? new Color(0.3f, 0.9f, 0.35f) : new Color(0.9f, 0.35f, 0.3f);
                if (pts[i].penDown)
                    Gizmos.DrawLine(pts[i - 1].position, pts[i].position);
                else
                    UnityEditor.Handles.DrawDottedLine(pts[i - 1].position, pts[i].position, 4f);
            }
            Gizmos.color = new Color(1f, 1f, 1f, 0.5f);
            for (int i = 0; i < pts.Count; i++)
                Gizmos.DrawSphere(pts[i].position, 0.12f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(pts[0].position, 0.4f);
            UnityEditor.Handles.Label(pts[0].position + Vector3.up * 0.5f, "start");
        }
#endif
    }
}
