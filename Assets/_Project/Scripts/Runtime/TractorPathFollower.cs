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
        [Tooltip("Wheels spun about their local X axis. Requires local X to be the axle (points left/right).")]
        public Transform[] driveWheels;
        [Min(0.001f)] public float wheelRadius = 0.4f;
        [Tooltip("Optional front wheels that visually yaw toward the turn direction.")]
        public Transform[] steerWheels;
        public float maxSteerAngleDeg = 28f;

        [Header("Mower")]
        public MowerController mower;
        [Tooltip("Rear-bottom brush transform; its world position drives the mowing visual.")]
        public Transform mowerAnchor;

        [Header("Events")]
        public UnityEvent onStarted;
        public UnityEvent onCompleted;
        public WaypointEvent onWaypointReached;
        public PenEvent onPenStateChanged;

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

        void Update()
        {
            if (!_running || _path == null) return;

            float budget = moveSpeed * Time.deltaTime;
            float totalMoved = 0f;
            int guard = 0;

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

                if (dist <= budget || dist <= arriveThreshold)
                {
                    transform.position = target;
                    totalMoved += dist;
                    budget -= dist;
                    if (!Advance()) break;
                }
                else
                {
                    transform.position = cur + (to / dist) * budget;
                    totalMoved += budget;
                    budget = 0f;
                }
            }

            // Clamp Y (belt-and-braces; all motion above is already flat).
            Vector3 p = transform.position;
            if (!Mathf.Approximately(p.y, fixedY))
                transform.position = new Vector3(p.x, fixedY, p.z);

            // Face the current target.
            if (_running)
            {
                Vector3 to = Flat(_path.Points[_index].position) - Flat(transform.position);
                if (to.sqrMagnitude > 1e-6f)
                {
                    Vector3 dir = to.normalized;
                    Quaternion look = Quaternion.LookRotation(dir, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnSpeedDeg * Time.deltaTime);
                    ApplySteer(dir);
                }
            }

            SpinWheels(totalMoved);

            if (mower != null && mowerAnchor != null)
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
                if (driveWheels[i] != null)
                    driveWheels[i].Rotate(Vector3.right, deg, Space.Self);
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
