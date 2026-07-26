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

        /// <summary>See <see cref="steeringMode"/>.</summary>
        public enum SteeringMode { ExactPath, Realistic }

        /// <summary>How many wheels the visual list accepts.</summary>
        public const int MaxWheels = 4;

        /// <summary>Which of the axle transform's own local axes runs along the axle.</summary>
        public enum AxleAxis { X, Y, Z }

        /// <summary>See <see cref="steerWheelSelection"/>. AutoFront is the default, so a freshly
        /// imported tractor steers on its front wheels with nothing to wire up.</summary>
        public enum SteerWheelSelection { AutoFront, AutoRear, Manual }

        /// <summary>
        /// One purely-visual wheel: a mesh, and the transform it pivots about. Entries are completely
        /// independent of one another - different pivots, orientations and sizes are all fine.
        /// </summary>
        [System.Serializable]
        public class WheelVisual
        {
            [Tooltip("The wheel's visual mesh. Rolled in place; it never drives the tractor.")]
            public Transform mesh;

            [Tooltip("This wheel's pivot: the mesh spins around THIS transform's position.\n\n" +
                     "Use it when the mesh's own pivot is not at the hub (very common in exported FBX) - " +
                     "put an empty at the hub centre and drop it here, and the wheel spins on the spot " +
                     "instead of swinging in an arc.\n\n" +
                     "Leave empty to spin the mesh about its own pivot.")]
            public Transform axle;

            [Tooltip("Which of the Axle's own local axes points ALONG the axle (the direction a real axle " +
                     "rod would run, left-to-right through the wheel).\n\n" +
                     "Z = the blue arrow (default), X = red, Y = green. Watch the cyan gizmo line in the " +
                     "Scene view and pick whichever makes it run along the axle - no need to re-orient " +
                     "anything you have already built. Each wheel is set independently.")]
            public AxleAxis axleAxis = AxleAxis.Z;

            [Tooltip("Fine-tune JUST this wheel's spin rate.\n\n" +
                     "1 = true rolling speed for its measured radius. Raise it if the wheel looks like " +
                     "it's dragging, lower it if it spins too fast, and use -1 to reverse the direction. " +
                     "Multiplies with Wheel Spin Multiplier on the component. Adjustable live in Play mode.")]
            public float spinMultiplier = 1f;
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

        [Header("Steering model")]
        [Tooltip("ExactPath: the original behaviour — the body is dragged straight at each waypoint and " +
                 "the heading merely chases it, so tight corners crab-slide (but the trace is exact).\n\n" +
                 "Realistic: a pure-pursuit unicycle — the body can only move along its heading, the " +
                 "heading turns toward a look-ahead point at a capped rate, and it brakes for corners. " +
                 "Natural arcs; rounds logo corners by well under ~0.3 m at the 40 m logo size.")]
        public SteeringMode steeringMode = SteeringMode.Realistic;
        [Tooltip("Look-ahead distance at low speed (m). Smaller = tighter corner tracking.")]
        [Min(0.1f)] public float lookAheadMin = 0.6f;
        [Tooltip("Look-ahead distance approached at ~10 m/s (m). Larger = smoother at speed.")]
        [Min(0.1f)] public float lookAheadMax = 1.2f;
        [Tooltip("Steering cap: how many degrees the heading may change per metre travelled. " +
                 "60°/m ≈ a ~1 m minimum turning radius.")]
        [Min(1f)] public float maxYawPerMeterDeg = 60f;
        [Tooltip("Heading error (deg) at which the tractor slows to Corner Speed Floor.")]
        [Min(1f)] public float cornerSlowdownAngle = 50f;
        [Range(0.1f, 1f)] public float cornerSpeedFloor = 0.35f;
        [Tooltip("How strongly the body is pulled sideways back onto the path (1/m). Prevents drift " +
                 "without visible side-sliding.")]
        [Min(0f)] public float crossTrackGain = 2f;
        [Tooltip("Visual only: wheelbase used to convert path curvature into a front-wheel steer angle.")]
        [Min(0.1f)] public float wheelbase = 1.6f;
        [Tooltip("Visual only: how fast the front wheels swing to their target steer angle (deg per " +
                 "second of path time).")]
        [Min(1f)] public float steerSmoothing = 360f;

        [Header("Wheels (visual)")]
        [Tooltip("Up to 4 wheels, each with its own mesh and its own pivot/axle. Entries are independent: " +
                 "each wheel spins about its own axle at its own rate, derived from its own size.\n\n" +
                 "This is PURELY COSMETIC - the wheels follow the tractor because they are parented to it. " +
                 "Nothing here affects the path the tractor drives or the line it mows.")]
        public WheelVisual[] wheels = new WheelVisual[0];
        [Tooltip("Overall wheel spin speed, scaling EVERY wheel. 1 = true rolling speed (a wheel turns " +
                 "exactly once per circumference travelled). Raise or lower it to taste; negative reverses " +
                 "them all. Each wheel can be trimmed further with its own Spin Multiplier. Adjustable " +
                 "live while playing.")]
        public float wheelSpinMultiplier = 1f;
        [Tooltip("Scene-view only: draw each wheel's axis of rotation (cyan line through the pivot), its " +
                 "rolling circle (yellow) and a spoke that turns as the wheel spins. Never appears in the " +
                 "Game view or a build.")]
        public bool showWheelGizmos = true;

        [Header("Steering (visual)")]
        [Tooltip("Which wheels visually yaw through corners.\n\n" +
                 "Auto Front (default) picks the two wheels whose measured HUBS sit furthest forward " +
                 "along the tractor's own +Z at the start of every run, and fills Steer Wheels in with " +
                 "them — so it stays right after a rescale, a re-import or a Rotate Model 90.\n\n" +
                 "Auto Rear steers the back pair instead (forklift-style). Manual leaves Steer Wheels " +
                 "exactly as you set it.")]
        public SteerWheelSelection steerWheelSelection = SteerWheelSelection.AutoFront;
        [Tooltip("The wheel pivots that yaw toward the turn direction — the front Axle_* objects. " +
                 "Filled in automatically unless Steer Wheel Selection is Manual.")]
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

        [Header("Events")]
        public UnityEvent onStarted;
        public UnityEvent onCompleted;
        public WaypointEvent onWaypointReached;
        public PenEvent onPenStateChanged;

        /// <summary>Per-wheel state bound at the start of a run. See <see cref="ResolveWheels"/>.</summary>
        struct WheelRuntime
        {
            public Transform mesh;
            public Transform axle;        // null => spin the mesh about its own pivot
            public Vector3 localAxis;     // spin axis, in the pivot transform's LOCAL space
            public Vector3 pivotLocal;    // the rotation centre (axle geometry centre), in axle local space
            public Vector3 restPos;       // mesh offset FROM that centre, in axle local space, at bind time
            public Quaternion restRot;    // mesh rotation, relative to the axle, at bind time
            public float radius;          // this wheel's own rolling radius
            public float angle;           // accumulated spin, degrees
        }

        WheelRuntime[] _wheels;
        WaypointPath _path;
        int _index;      // waypoint we are driving toward
        int _dir = 1;    // +1 forward, -1 reverse (ping-pong)
        bool _running;
        bool _penDown;

        // ---- Realistic-steering state (arc-length parameterisation of the same polyline) ----
        float[] _cumLen;         // arc length at each waypoint, flattened to the ground plane
        float _totalLen;
        float _s;                // current arc-length position along the path
        float _sDir = 1f;        // +1 forward, -1 reverse (ping-pong)
        float _heading;          // body yaw in degrees; position may ONLY advance along this
        int _edgeHint;           // amortised O(1) segment lookup (s moves smoothly)
        int _lastCrossedIndex;   // for onWaypointReached
        int _penEdge;            // edge the pen was last evaluated on (see UpdatePenFromArc)
        float _steerAngle;       // smoothed visual front-wheel angle
        // Steer bind: rest pose plus the point to swing about. A wheel must yaw around ITS OWN HUB, and
        // an exported axle's transform origin is usually back at the model origin, metres away from it.
        Transform[] _steerBound;     // exactly which transforms the caches below describe
        Quaternion[] _steerRest;     // rest local rotation (so steering composes, not stomps)
        Vector3[] _steerRestPos;     // rest local position
        Vector3[] _steerHubLocal;    // the hub, in the steer transform's PARENT local space
        Vector3[] _steerAxisLocal;   // the steering axis (tractor up), in that same parent space

        public WaypointPath Path => _path;
        public bool IsRunning => _running;

        /// <summary>
        /// The waypoint the mower is currently drawing INTO — the same index <see cref="MowerWorldPos"/>
        /// takes the render layer from.
        ///
        /// Mowing visuals should identify the stroke being drawn from THIS rather than by counting
        /// pen-down transitions: a stroke smaller than the look-ahead (the logo's stars are ~0.3 m across
        /// against a 0.6–1.2 m look-ahead) can be leapt over entirely inside one sub-step, so the count
        /// silently runs behind the real stroke number and every colour after it is shifted.
        /// </summary>
        public int CurrentWaypointIndex { get; private set; }

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

            _penDown = false;                 // force a fresh transition on the next pen evaluation
            if (steeringMode == SteeringMode.Realistic)
            {
                BuildArcLength();
                _s = 0f;
                _sDir = 1f;
                _edgeHint = 0;
                _lastCrossedIndex = 0;
                _steerAngle = 0f;
                SnapToArc();
                SyncPenEdge();
                UpdatePenFromArc();           // SetPen seeds the mower at the current brush position
            }
            else
            {
                EvaluatePenForTarget();       // SetPen seeds the mower at the current brush position
            }
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
            // Straighten the steering with the PREVIOUS rest pose FIRST (no-op on the very first run),
            // THEN capture rest poses from the centred axles. The other order would re-capture a
            // mid-corner steer angle as the new rest — every R-restart taken in a corner would bake
            // another permanent toe offset into the front wheels.
            ApplySteerAngle(0f);
            _steerAngle = 0f;
            // ...and put the wheel MESHES back on their straightened axles before re-binding. The mesh is
            // typically a SIBLING of the axle, not a child (Wheels/FrontLeft holds [Axle, WheelParts]), so
            // straightening the axle does not move it — and ResolveWheels would then capture a mid-corner
            // steer angle as the mesh's new rest pose, welding a permanent toe offset into the front
            // wheels on every R-restart taken in a corner.
            RestoreWheelRestPose();
            ResolveSteerWheels();

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
        /// Bind each visual wheel to its axle: remember where the mesh sits relative to that axle, and
        /// measure that wheel's own rolling radius. Re-run at the start of every run, so moving an axle or
        /// rescaling the tractor is picked up automatically.
        ///
        /// The rest pose is stored RELATIVE TO THE AXLE and the spin is re-applied from it every frame
        /// (rather than accumulating Rotate/RotateAround calls), so an offset pivot can never drift.
        /// </summary>
        void ResolveWheels()
        {
            int count = wheels != null ? Mathf.Min(wheels.Length, MaxWheels) : 0;
            if (_wheels == null || _wheels.Length != count) _wheels = new WheelRuntime[count];

            for (int i = 0; i < count; i++)
            {
                var w = wheels[i];
                var rt = new WheelRuntime();
                if (w == null || w.mesh == null) { _wheels[i] = rt; continue; }

                rt.mesh = w.mesh;
                rt.axle = w.axle;
                rt.localAxis = LocalAxisOf(w.axleAxis);

                TryGetWheelAxis(w, out Vector3 pivotWorld, out _, out rt.radius, out bool measured);
                if (!measured)
                    Debug.LogWarning($"[TractorPathFollower] Wheel '{rt.mesh.name}' has no measurable mesh; " +
                                     "using a 0.4 rolling radius. Assign the wheel's mesh object, not an " +
                                     "empty parent.", rt.mesh);

                if (rt.axle != null)
                {
                    // Everything is stored in the axle's local space, so it rides along with the tractor.
                    // restPos is the mesh's offset FROM the rotation centre, which is what gets swung.
                    rt.pivotLocal = rt.axle.InverseTransformPoint(pivotWorld);
                    rt.restPos = rt.axle.InverseTransformPoint(rt.mesh.position) - rt.pivotLocal;
                    rt.restRot = Quaternion.Inverse(rt.axle.rotation) * rt.mesh.rotation;
                }
                else
                {
                    rt.restRot = rt.mesh.localRotation;   // spun about its own local X, in place
                }

                _wheels[i] = rt;
            }
        }

        /// <summary>
        /// Re-pose every already-bound wheel mesh from its axle at zero spin — exactly where the previous
        /// bind found it. Idempotent: re-binding straight afterwards reads back the same rest values, so
        /// restarting the run can never accumulate an offset (of steering OR of spin phase) into the mesh.
        /// A no-op on the first run, when nothing is bound yet.
        /// </summary>
        void RestoreWheelRestPose()
        {
            if (_wheels == null) return;
            for (int i = 0; i < _wheels.Length; i++)
            {
                var w = _wheels[i];
                if (w.mesh == null) continue;
                if (w.axle == null) w.mesh.localRotation = w.restRot;
                else
                    w.mesh.SetPositionAndRotation(w.axle.TransformPoint(w.pivotLocal + w.restPos),
                                                  w.axle.rotation * w.restRot);
                w.angle = 0f;
                _wheels[i] = w;
            }
        }

        void OnValidate()
        {
            // The inspector list is capped at MaxWheels.
            if (wheels != null && wheels.Length > MaxWheels)
                System.Array.Resize(ref wheels, MaxWheels);
        }

        /// <summary>
        /// Where a wheel pivots, which way its axis of rotation points, and how big it rolls. Shared by the
        /// runtime bind and the Scene-view gizmos, so what you see drawn is exactly what will spin.
        /// Safe to call in edit mode.
        /// </summary>
        public bool TryGetWheelAxis(WheelVisual w, out Vector3 pivot, out Vector3 axisWorld,
                                    out float radius, out bool measured)
        {
            pivot = Vector3.zero;
            axisWorld = Vector3.right;
            radius = 0.4f;
            measured = false;
            if (w == null || w.mesh == null) return false;

            // The axle is the pivot. When it is a real mesh part, rotate about the CENTRE of that geometry
            // rather than its transform origin, which often sits back at the model origin.
            Transform pivotT = w.axle != null ? w.axle : w.mesh;
            pivot = w.axle != null ? AxlePivotPoint(w.axle, w.mesh) : w.mesh.position;

            Vector3 a = w.axleAxis switch
            {
                AxleAxis.X => pivotT.right,
                AxleAxis.Y => pivotT.up,
                _          => pivotT.forward,
            };
            axisWorld = a.sqrMagnitude < 1e-8f ? Vector3.right : a.normalized;

            // Rolling radius: half the wheel's largest extent PERPENDICULAR to its axle, measured in the
            // mesh's own local space so it is independent of world orientation.
            Vector3 size = LocalWheelSize(w.mesh);
            Vector3 axisInMesh = w.mesh.InverseTransformDirection(axisWorld);
            float r = HalfPerpendicularExtent(size, axisInMesh);
            if (r > 1e-4f) { radius = r; measured = true; }
            return true;
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

        /// <summary>
        /// The centre of the axle component itself: its own renderer bounds when it is a mesh part,
        /// otherwise the combined bounds of its children (excluding the wheel mesh grouped under it), and
        /// finally its transform position when it is a plain empty.
        /// </summary>
        static Vector3 AxlePivotPoint(Transform axle, Transform wheelMesh)
        {
            var own = axle.GetComponent<Renderer>();
            if (IsMeshRenderer(own)) return own.bounds.center;

            bool any = false;
            Bounds b = default;
            foreach (var r in axle.GetComponentsInChildren<Renderer>(true))
            {
                if (!IsMeshRenderer(r)) continue;
                // Don't let the wheel grouped under the axle drag the centre off the axle itself.
                if (wheelMesh != null && (r.transform == wheelMesh || r.transform.IsChildOf(wheelMesh))) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? b.center : axle.position;
        }

        static bool IsMeshRenderer(Renderer r) =>
            r != null && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer);

        static Vector3 LocalAxisOf(AxleAxis a) => a switch
        {
            AxleAxis.X => Vector3.right,
            AxleAxis.Y => Vector3.up,
            _          => Vector3.forward,
        };

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

            if (steeringMode == SteeringMode.Realistic)
            {
                // The mode is a live Inspector switch: build the arc-length state on demand, seeded
                // from the current pose, when the run began in ExactPath mode.
                if (_cumLen == null || _cumLen.Length != _path.Count)
                    InitRealisticFromCurrentPose();
                UpdateRealistic();
            }
            else
            {
                // Live switch back: re-derive the waypoint target from the arc position so the tractor
                // continues from here instead of driving back toward the start.
                if (_cumLen != null)
                {
                    _index = Mathf.Clamp(EdgeAtArc(_s) + 1, 1, _path.Count - 1);
                    _cumLen = null;               // stale now; rebuilt on demand if switched again
                }
                UpdateExact();
            }
        }

        /// <summary>Seed the realistic-steering state mid-run (mode switched while playing).</summary>
        void InitRealisticFromCurrentPose()
        {
            BuildArcLength();
            _edgeHint = 0;
            _sDir = 1f;
            _heading = transform.eulerAngles.y;
            _steerAngle = 0f;

            // One-time global projection of the current position onto the path.
            Vector3 pos = Flat(transform.position);
            float bestS = 0f, bestSq = float.MaxValue;
            for (int e = 0; e < _path.Count - 1; e++)
            {
                float sHere = ClosestOnEdge(pos, e, out float dSq);
                if (dSq < bestSq) { bestSq = dSq; bestS = sHere; }
            }
            _s = bestS;
            _lastCrossedIndex = EdgeAtArc(_s);
            SyncPenEdge();
        }

        // ================================================================== exact path (original)

        void UpdateExact()
        {
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

        // ================================================================== realistic steering
        //
        // A pure-pursuit unicycle: the body may only advance along its heading; the heading turns toward
        // a look-ahead point on the path at a capped rate (degrees per METRE travelled, so behaviour is
        // identical at any Time.timeScale); speed drops for large heading errors (corner braking); and a
        // clamped lateral pull keeps cross-track error from accumulating. Sub-steps are capped at 0.25 m
        // so a 16x fast-forward integrates the same curve as 1x. With the default 0.6 m look-ahead a 90°
        // logo corner rounds by ~0.15 m — invisible at the 40 m logo — and SteeringMode.ExactPath brings
        // the original waypoint-exact behaviour back with one Inspector click.

        const float MaxSubStep = 0.25f;

        void UpdateRealistic()
        {
            float budget = moveSpeed * Time.deltaTime;
            float invSpeed = moveSpeed > 1e-6f ? 1f / moveSpeed : 0f;
            float totalMoved = 0f;
            int guard = 0;

            while (_running && budget > 1e-6f && guard++ < 512)
            {
                if (teleportOnPenUp && !EdgePenDown(EdgeAtArc(_s)))
                {
                    if (!SkipToNextPenDown()) break;
                    continue;                 // the jump consumes no budget
                }

                float stepRaw = Mathf.Min(budget, MaxSubStep);
                float lookAhead = Mathf.Lerp(lookAheadMin, lookAheadMax, moveSpeed * 0.1f);

                Vector3 pos = Flat(transform.position);
                Vector3 target = PointAtArc(_s + lookAhead * _sDir);
                Vector3 to = target - pos;
                float desired = to.sqrMagnitude > 1e-8f ? Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg : _heading;
                float err = Mathf.DeltaAngle(_heading, desired);

                // Corner braking: the full raw step is consumed from the budget while less ground is
                // covered — that IS the slowdown, without touching any timers.
                float speedMul = Mathf.Lerp(1f, cornerSpeedFloor,
                                            Mathf.Clamp01(Mathf.Abs(err) / cornerSlowdownAngle));
                float step = stepRaw * speedMul;

                float dYaw = Mathf.Clamp(err, -maxYawPerMeterDeg * step, maxYawPerMeterDeg * step);
                _heading += dYaw;

                Vector3 fwd = new Vector3(Mathf.Sin(_heading * Mathf.Deg2Rad), 0f,
                                          Mathf.Cos(_heading * Mathf.Deg2Rad));
                pos += fwd * step;            // position follows HEADING — no crab-sliding

                // Advance the path parameter to the new position (monotonically — pen state and waypoint
                // events key off it), then pull laterally toward the path so look-ahead corner cutting
                // can never drift into a standing offset.
                _s = ProjectOntoPath(pos, _s);
                Vector3 lateral = Vector3.ClampMagnitude(PointAtArc(_s) - pos, crossTrackGain * step);
                pos += new Vector3(lateral.x, 0f, lateral.z);

                transform.SetPositionAndRotation(new Vector3(pos.x, fixedY, pos.z),
                                                 Quaternion.Euler(0f, _heading, 0f));
                ApplySteerFromCurvature(dYaw, step, invSpeed);

                totalMoved += step;
                budget -= stepRaw;

                FireCrossedWaypoints();
                UpdatePenFromArc();
                SampleMower();

                bool atEnd = _sDir > 0 ? _s >= _totalLen - 1e-3f : _s <= 1e-3f;
                if (atEnd && !HandleEndRealistic()) break;
            }

            Vector3 p = transform.position;
            if (!Mathf.Approximately(p.y, fixedY))
                transform.position = new Vector3(p.x, fixedY, p.z);

            SpinWheels(totalMoved);
        }

        void BuildArcLength()
        {
            int n = _path.Count;
            _cumLen = new float[n];
            float total = 0f;
            Vector3 prev = Flat(_path.Points[0].position);
            for (int i = 1; i < n; i++)
            {
                Vector3 p = Flat(_path.Points[i].position);
                total += Vector3.Distance(prev, p);
                _cumLen[i] = total;
                prev = p;
            }
            _totalLen = total;
        }

        /// <summary>Index i of the edge between waypoints i and i+1 containing arc position s. The hint
        /// makes this amortised O(1) because s only moves smoothly.</summary>
        int EdgeAtArc(float s)
        {
            int n = _path.Count;
            if (s <= 0f) { _edgeHint = 0; return 0; }
            if (s >= _totalLen) { _edgeHint = n - 2; return n - 2; }
            int i = Mathf.Clamp(_edgeHint, 0, n - 2);
            while (i > 0 && _cumLen[i] > s) i--;
            while (i < n - 2 && _cumLen[i + 1] < s) i++;
            _edgeHint = i;
            return i;
        }

        /// <summary>Pen state of an edge — identified by its higher endpoint, matching
        /// <see cref="EvaluatePenForTarget"/>'s convention.</summary>
        bool EdgePenDown(int edge) => _path.Points[Mathf.Clamp(edge + 1, 1, _path.Count - 1)].penDown;

        Vector3 PointAtArc(float s)
        {
            s = Mathf.Clamp(s, 0f, _totalLen);
            int i = EdgeAtArc(s);
            float segLen = _cumLen[i + 1] - _cumLen[i];
            float t = segLen > 1e-6f ? (s - _cumLen[i]) / segLen : 0f;
            return Vector3.Lerp(Flat(_path.Points[i].position), Flat(_path.Points[i + 1].position), t);
        }

        /// <summary>
        /// Arc position of the closest point on the path near sCur, searched a few metres in the travel
        /// direction only, and never allowed to move backwards against it.
        /// </summary>
        float ProjectOntoPath(Vector3 pos, float sCur)
        {
            const float Window = 3f;
            int n = _path.Count;
            float bestS = sCur;
            float bestSq = float.MaxValue;
            int i = EdgeAtArc(sCur);

            if (_sDir > 0)
            {
                float sEnd = Mathf.Min(_totalLen, sCur + Window);
                for (; i < n - 1 && _cumLen[i] <= sEnd; i++)
                {
                    float sHere = ClosestOnEdge(pos, i, out float dSq);
                    if (sHere >= sCur - 1e-4f && dSq < bestSq) { bestSq = dSq; bestS = sHere; }
                }
                return Mathf.Max(sCur, bestS);
            }
            else
            {
                float sEnd = Mathf.Max(0f, sCur - Window);
                for (; i >= 0 && _cumLen[i + 1] >= sEnd; i--)
                {
                    float sHere = ClosestOnEdge(pos, i, out float dSq);
                    if (sHere <= sCur + 1e-4f && dSq < bestSq) { bestSq = dSq; bestS = sHere; }
                }
                return Mathf.Min(sCur, bestS);
            }
        }

        float ClosestOnEdge(Vector3 pos, int edge, out float distSq)
        {
            Vector3 a = Flat(_path.Points[edge].position);
            Vector3 b = Flat(_path.Points[edge + 1].position);
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(pos - a, ab) / len2) : 0f;
            Vector3 p = a + ab * t;
            distSq = (p - pos).sqrMagnitude;
            return _cumLen[edge] + (_cumLen[edge + 1] - _cumLen[edge]) * t;
        }

        void FireCrossedWaypoints()
        {
            if (_sDir > 0)
            {
                while (_lastCrossedIndex < _path.Count - 1 && _cumLen[_lastCrossedIndex + 1] <= _s + 1e-4f)
                {
                    _lastCrossedIndex++;
                    onWaypointReached?.Invoke(_lastCrossedIndex);
                }
            }
            else
            {
                while (_lastCrossedIndex > 0 && _cumLen[_lastCrossedIndex - 1] >= _s - 1e-4f)
                {
                    _lastCrossedIndex--;
                    onWaypointReached?.Invoke(_lastCrossedIndex);
                }
            }
        }

        /// <summary>
        /// Set the pen from the arc position, checking the WHOLE span crossed since the last evaluation
        /// rather than only the edge we landed on. The projection can leap several edges in one sub-step
        /// — a stroke shorter than the look-ahead is skipped bodily — and if a pen-up edge inside that
        /// span goes unobserved the pen never lifts, so the mower drags a connector straight across the
        /// gap and the stroke count runs behind. Collapsing the span into a single lift breaks the
        /// connector and starts the next stroke where the tractor actually is.
        /// </summary>
        void UpdatePenFromArc()
        {
            int to = EdgeAtArc(_s);
            bool down = EdgePenDown(to);

            if (down && _penEdge != to)
            {
                int lo = Mathf.Min(_penEdge, to), hi = Mathf.Max(_penEdge, to);
                for (int e = lo; e <= hi; e++)
                    if (!EdgePenDown(e)) { SetPen(false); break; }
            }

            SetPen(down);
            _penEdge = to;
        }

        /// <summary>Re-base the pen span after a deliberate jump in _s (start, wrap, pen-up skip).</summary>
        void SyncPenEdge() => _penEdge = EdgeAtArc(_s);

        /// <summary>Jump the arc position across pen-up edges (teleportOnPenUp). Returns false at path end.</summary>
        bool SkipToNextPenDown()
        {
            int n = _path.Count;
            int i = EdgeAtArc(_s);
            if (_sDir > 0)
            {
                while (i < n - 1 && !_path.Points[Mathf.Min(i + 1, n - 1)].penDown) i++;
                if (i >= n - 1) return HandleEndRealistic();
                // Nudge INTO the pen-down edge and point the hint at it. Landing exactly on
                // _cumLen[i] would make EdgeAtArc tie-break back to the pen-up edge below, re-enter
                // this method with identical state, and stall the tractor forever at the first gap.
                _s = Mathf.Min(_cumLen[i] + 1e-3f, _cumLen[i + 1]);
                _edgeHint = i;
                _lastCrossedIndex = i;
            }
            else
            {
                while (i >= 0 && !_path.Points[i + 1].penDown) i--;
                if (i < 0) return HandleEndRealistic();
                _s = Mathf.Max(_cumLen[i + 1] - 1e-3f, _cumLen[i]);   // mirrored boundary nudge
                _edgeHint = i;
                _lastCrossedIndex = i + 1;
            }
            SnapToArc();
            SyncPenEdge();                    // the jump is deliberate, not a missed lift
            UpdatePenFromArc();
            return true;
        }

        /// <summary>Place the body exactly on the path at _s, heading along it.</summary>
        void SnapToArc()
        {
            Vector3 p = PointAtArc(_s);
            Vector3 q = PointAtArc(_s + 0.25f * _sDir);
            Vector3 d = q - p;
            if (d.sqrMagnitude > 1e-8f)
                _heading = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            transform.SetPositionAndRotation(new Vector3(p.x, fixedY, p.z),
                                             Quaternion.Euler(0f, _heading, 0f));
        }

        bool HandleEndRealistic()
        {
            onCompleted?.Invoke();
            switch (onComplete)
            {
                case EndBehavior.Loop:
                    SetPen(false);            // lift before the wrap so no connector is drawn
                    _s = 0f;
                    _sDir = 1f;
                    _edgeHint = 0;
                    _lastCrossedIndex = 0;
                    SnapToArc();
                    SyncPenEdge();
                    UpdatePenFromArc();       // SetPen seeds the mower at the wrapped start position
                    return true;

                case EndBehavior.PingPong:
                    SetPen(false);
                    _sDir = -_sDir;
                    _s = Mathf.Clamp(_s, 0f, _totalLen);
                    _lastCrossedIndex = Mathf.Clamp(_lastCrossedIndex, 0, _path.Count - 1);
                    SyncPenEdge();
                    UpdatePenFromArc();
                    return true;

                default: // Stop
                    SetPen(false);
                    _running = false;
                    return false;
            }
        }

        void ApplySteerFromCurvature(float dYawDeg, float step, float invSpeed)
        {
            if (steerWheels == null || steerWheels.Length == 0) return;
            float curvature = step > 1e-5f ? dYawDeg * Mathf.Deg2Rad / step : 0f;   // 1/m
            float target = Mathf.Clamp(Mathf.Atan(wheelbase * curvature) * Mathf.Rad2Deg,
                                       -maxSteerAngleDeg, maxSteerAngleDeg);
            // Smooth in "path time" (step / speed) so the swing rate is timeScale-independent.
            _steerAngle = Mathf.MoveTowards(_steerAngle, target, steerSmoothing * step * invSpeed);
            ApplySteerAngle(_steerAngle);
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
            int edgeHi;
            if (steeringMode == SteeringMode.Realistic && _cumLen != null)
                edgeHi = EdgeAtArc(_s) + 1;
            else
                edgeHi = _dir > 0 ? _index : _index + 1;
            edgeHi = Mathf.Clamp(edgeHi, 1, _path.Count - 1);
            CurrentWaypointIndex = edgeHi;
            return new Vector3(a.x, _path.Points[edgeHi].position.y, a.z);
        }

        /// <summary>
        /// Roll each visual wheel by the distance the tractor covered. Every wheel is handled on its own:
        /// its own axle, its own radius, its own accumulated angle. Purely cosmetic - the tractor's motion
        /// is already decided by the time this runs.
        /// </summary>
        void SpinWheels(float distance)
        {
            if (_wheels == null || distance <= 0f) return;

            for (int i = 0; i < _wheels.Length; i++)
            {
                var w = _wheels[i];
                if (w.mesh == null || w.radius <= 1e-4f) continue;

                // Read the multipliers from the serialized entries rather than the cached bind, so both
                // can be dragged live in the Inspector while playing.
                float mult = wheelSpinMultiplier;
                if (wheels != null && i < wheels.Length && wheels[i] != null)
                    mult *= wheels[i].spinMultiplier;

                w.angle = Mathf.Repeat(w.angle + distance / w.radius * Mathf.Rad2Deg * mult, 360f);
                _wheels[i] = w;                                  // struct: write the angle back

                if (w.axle == null)
                {
                    // No pivot given: spin the mesh in place about its own chosen local axis, from rest.
                    w.mesh.localRotation = w.restRot * Quaternion.AngleAxis(w.angle, w.localAxis);
                    continue;
                }

                // Rebuild the pose from the axle each frame: rotate about the axle's chosen local axis,
                // applied to the rest pose captured relative to that axle. Exact, and drift-free however
                // far the mesh's own pivot sits from the hub.
                Quaternion spin = Quaternion.AngleAxis(w.angle, w.localAxis);
                w.mesh.SetPositionAndRotation(
                    w.axle.TransformPoint(w.pivotLocal + spin * w.restPos),
                    w.axle.rotation * spin * w.restRot);
            }
        }

        void ApplySteer(Vector3 worldDir)
        {
            if (steerWheels == null || steerWheels.Length == 0) return;
            Vector3 local = transform.InverseTransformDirection(worldDir);
            float steer = Mathf.Clamp(Mathf.Atan2(local.x, Mathf.Max(0.001f, local.z)) * Mathf.Rad2Deg,
                                      -maxSteerAngleDeg, maxSteerAngleDeg);
            ApplySteerAngle(steer);
        }

        /// <summary>
        /// Pick the steer wheels (unless Manual) and bind them: rest pose, so steering COMPOSES with the
        /// orientation they were authored with instead of stomping it, plus the HUB each one must swing
        /// about. Re-run at the start of every run, with the steering centred first, so wheel meshes bind
        /// their spin pose against the un-steered axle.
        /// </summary>
        void ResolveSteerWheels()
        {
            if (steerWheelSelection != SteerWheelSelection.Manual) AutoPickSteerWheels();

            int n = steerWheels != null ? steerWheels.Length : 0;
            if (_steerRest == null || _steerRest.Length != n)
            {
                _steerBound = new Transform[n];
                _steerRest = new Quaternion[n];
                _steerRestPos = new Vector3[n];
                _steerHubLocal = new Vector3[n];
                _steerAxisLocal = new Vector3[n];
            }

            for (int i = 0; i < n; i++)
            {
                Transform t = steerWheels[i];
                _steerBound[i] = t;
                if (t == null)
                {
                    _steerRest[i] = Quaternion.identity;
                    _steerRestPos[i] = Vector3.zero;
                    _steerHubLocal[i] = Vector3.zero;
                    _steerAxisLocal[i] = Vector3.up;
                    continue;
                }

                _steerRest[i] = t.localRotation;
                _steerRestPos[i] = t.localPosition;

                Transform parent = t.parent;
                Vector3 hub = SteerHubWorld(t);
                _steerHubLocal[i] = parent != null ? parent.InverseTransformPoint(hub) : hub;
                Vector3 axis = parent != null ? parent.InverseTransformDirection(transform.up) : transform.up;
                _steerAxisLocal[i] = axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.up;
            }
        }

        /// <summary>
        /// Where this steer pivot's wheel actually sits. Uses the same measurement the wheel spin does, so
        /// steering and rolling turn about exactly the same point.
        /// </summary>
        Vector3 SteerHubWorld(Transform axle)
        {
            if (wheels != null)
                foreach (var w in wheels)
                    if (w != null && w.axle == axle &&
                        TryGetWheelAxis(w, out Vector3 hub, out _, out _, out _))
                        return hub;
            return AxlePivotPoint(axle, null);
        }

        /// <summary>
        /// Fill <see cref="steerWheels"/> with the front (or rear) pair, chosen by each wheel's MEASURED
        /// hub position along the tractor's own +Z. Measured, not transform origin: an FBX exported with
        /// frozen transforms gives every wheel the same pivot back at the model origin, which makes
        /// sorting on <c>axle.position</c> a coin toss — that is how a rear wheel ends up steering.
        /// </summary>
        void AutoPickSteerWheels()
        {
            int count = wheels != null ? Mathf.Min(wheels.Length, MaxWheels) : 0;
            if (count < 2) return;

            Transform bestA = null, bestB = null;
            float zA = 0f, zB = 0f;

            for (int i = 0; i < count; i++)
            {
                var w = wheels[i];
                if (w == null || w.axle == null) continue;
                if (w.axle == bestA || w.axle == bestB) continue;   // two meshes may share one axle

                // SteerHubWorld, not TryGetWheelAxis: the latter reports failure for a wheel with no
                // mesh assigned, and dropping that wheel from the comparison would silently leave a
                // stale front/rear pair in place. The axle's own bounds are enough to place it.
                float z = transform.InverseTransformPoint(SteerHubWorld(w.axle)).z;
                if (steerWheelSelection == SteerWheelSelection.AutoRear) z = -z;

                if (bestA == null || z > zA) { bestB = bestA; zB = zA; bestA = w.axle; zA = z; }
                else if (bestB == null || z > zB) { bestB = w.axle; zB = z; }
            }

            if (bestA == null || bestB == null) return;         // not enough axles: leave the list alone
            if (steerWheels == null || steerWheels.Length != 2) steerWheels = new Transform[2];
            steerWheels[0] = bestA;
            steerWheels[1] = bestB;
        }

        /// <summary>
        /// Yaw the steer wheels about their own hubs. Rotating the pivot in place would be wrong whenever
        /// the axle's transform origin is not at the hub — the wheel would swing through an arc around
        /// the middle of the tractor instead of turning on the spot — so the pivot is rotated ABOUT the
        /// hub: its local position swings with it. That leaves the hub itself fixed in world space, which
        /// is exactly what <see cref="SpinWheels"/> already binds against.
        /// </summary>
        void ApplySteerAngle(float steerDeg)
        {
            if (steerWheels == null || _steerBound == null || _steerRest == null ||
                _steerRestPos == null || _steerHubLocal == null || _steerAxisLocal == null) return;
            int n = Mathf.Min(steerWheels.Length, _steerRest.Length);
            for (int i = 0; i < n; i++)
            {
                Transform t = steerWheels[i];
                // The caches are keyed by slot, so only touch a slot still holding the transform they
                // were measured from — otherwise a list edited between runs would have one wheel's hub
                // and rest pose written onto another.
                if (t == null || t != _steerBound[i]) continue;
                Quaternion q = Quaternion.AngleAxis(steerDeg, _steerAxisLocal[i]);
                t.localRotation = q * _steerRest[i];
                t.localPosition = _steerHubLocal[i] + q * (_steerRestPos[i] - _steerHubLocal[i]);
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Scene-view aid for setting up wheels: draws each wheel's ACTUAL axis of rotation, so a wrong
        /// axle orientation or an off-hub pivot is obvious before you press Play.
        ///
        /// <list type="bullet">
        /// <item><b>Cyan line</b> - the axis of rotation, through the pivot.</item>
        /// <item><b>Red dot</b> - the exact pivot point. If it is not at the wheel's hub, the wheel will
        /// swing in an arc instead of spinning on the spot.</item>
        /// <item><b>Yellow circle</b> - the rolling circle at the measured radius; it should sit on the
        /// rim. If it does not, the spin RATE will be off.</item>
        /// <item><b>White spoke</b> - turns as the wheel spins, so you can watch it roll in Play mode.</item>
        /// </list>
        /// </summary>
        void DrawWheelAxisGizmos()
        {
            if (!showWheelGizmos || wheels == null) return;

            int count = Mathf.Min(wheels.Length, MaxWheels);
            for (int i = 0; i < count; i++)
            {
                var w = wheels[i];
                if (!TryGetWheelAxis(w, out Vector3 pivot, out Vector3 axis, out float radius, out bool measured))
                    continue;

                // Axis of rotation: a line through the pivot, long enough to read at a glance.
                float half = Mathf.Max(radius * 1.6f, 0.2f);
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(pivot - axis * half, pivot + axis * half);

                // Pivot point.
                Gizmos.color = Color.red;
                Gizmos.DrawSphere(pivot, Mathf.Max(radius * 0.07f, 0.012f));

                // Rolling circle, in the plane the wheel actually turns in.
                Gizmos.color = measured ? Color.yellow : new Color(1f, 0.5f, 0f);
                DrawCircleGizmo(pivot, axis, radius);

                // A spoke that follows the mesh, so the rotation is visible while playing.
                Vector3 spoke = Vector3.ProjectOnPlane(w.mesh.up, axis);
                if (spoke.sqrMagnitude < 1e-6f) spoke = Vector3.ProjectOnPlane(w.mesh.forward, axis);
                if (spoke.sqrMagnitude > 1e-6f)
                {
                    Gizmos.color = Color.white;
                    Gizmos.DrawLine(pivot, pivot + spoke.normalized * radius);
                }

                float shownMult = wheelSpinMultiplier * w.spinMultiplier;
                UnityEditor.Handles.color = Color.cyan;
                UnityEditor.Handles.Label(pivot + axis * half,
                    $"{w.mesh.name}\naxle: {w.axleAxis}   r = {radius:0.###}{(measured ? "" : " (not measured)")}" +
                    $"{(Mathf.Approximately(shownMult, 1f) ? "" : $"   speed x{shownMult:0.##}")}" +
                    $"{(w.axle == null ? "\nno axle - own pivot" : "")}");
            }
        }

        static void DrawCircleGizmo(Vector3 center, Vector3 axis, float radius, int segments = 40)
        {
            Vector3 a = Vector3.Cross(axis, Vector3.up);
            if (a.sqrMagnitude < 1e-6f) a = Vector3.Cross(axis, Vector3.right);
            a = a.normalized * radius;
            Vector3 b = Vector3.Cross(axis, a).normalized * radius;

            Vector3 prev = center + a;
            for (int i = 1; i <= segments; i++)
            {
                float t = i / (float)segments * Mathf.PI * 2f;
                Vector3 p = center + a * Mathf.Cos(t) + b * Mathf.Sin(t);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }

        WaypointPath _gizmoCache;
        TextAsset _gizmoCacheAsset;

        void OnDrawGizmos()
        {
            DrawWheelAxisGizmos();

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
