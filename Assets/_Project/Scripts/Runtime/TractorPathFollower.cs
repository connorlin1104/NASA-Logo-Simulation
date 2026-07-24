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

        /// <summary>How many wheels the visual list accepts.</summary>
        public const int MaxWheels = 4;

        /// <summary>Which of the axle transform's own local axes runs along the axle.</summary>
        public enum AxleAxis { X, Y, Z }

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
            for (int i = 0; i < steerWheels.Length; i++)
                if (steerWheels[i] != null)
                {
                    Vector3 e = steerWheels[i].localEulerAngles;
                    steerWheels[i].localEulerAngles = new Vector3(e.x, steer, e.z);
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
