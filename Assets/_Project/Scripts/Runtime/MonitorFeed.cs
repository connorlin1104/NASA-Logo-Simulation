using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// What the station's monitors are showing. A camera somewhere in the world renders into a
    /// <see cref="RenderTexture"/>, and the screen material samples that texture — so the wall of
    /// monitors becomes a live feed of the mow instead of a picture stuck to a plane.
    ///
    /// <b>It renders on demand, not every frame.</b> The camera is left DISABLED and driven by explicit
    /// <see cref="Camera.Render"/> calls at <see cref="framesPerSecond"/>. Twenty screens all sample one
    /// texture, so the cost is one extra camera pass — and at 20 fps that pass runs a third as often as
    /// the main one. A monitor updating at 20 fps looks like a monitor; one updating at 165 looks
    /// identical and costs five times as much.
    ///
    /// <b>Framing</b> picks what the camera does: hang over the logo and turn slowly, chase the tractor,
    /// ride the drone, or stay exactly where it was put.
    ///
    /// Unscaled time throughout — the sim fast-forwards the mow to 16x, and a feed that tracked that
    /// would whip round the field (see <see cref="AstronautController"/> for the project convention).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class MonitorFeed : MonoBehaviour
    {
        public enum Framing
        {
            /// <summary>High over the logo, tilted down, turning slowly. Shows the whole pattern.</summary>
            OverheadOfTheLogo,
            /// <summary>Behind and above the tractor, following it round the route.</summary>
            ChaseTheTractor,
            /// <summary>Bolted to the drone, looking where it flies.</summary>
            FromTheDrone,
            /// <summary>Wherever you dragged it. Nothing moves it.</summary>
            Fixed,
        }

        [Header("What it looks at")]
        public Framing framing = Framing.OverheadOfTheLogo;
        [Tooltip("The camera that renders the feed. Left disabled on purpose — this script renders it.")]
        public Camera feedCamera;
        [Tooltip("The texture the screens sample.")]
        public RenderTexture output;

        [Header("Overhead")]
        [Tooltip("Centre of the pattern, in world space. Set by the Monitor Screens tool from the logo's " +
                 "own waypoints.")]
        public Vector3 lookAt = Vector3.zero;
        [Min(1f)] public float height = 55f;
        [Tooltip("How far back from straight-down. 90 is a flat map, 0 is a horizon shot; 60-70 reads as " +
                 "a camera on a mast.")]
        [Range(0f, 89f)] public float tiltDegrees = 62f;
        [Tooltip("Slow drift around the field, so the feed is never a still frame. 0 holds it steady.")]
        [Range(0f, 60f)] public float orbitDegreesPerSecond = 3f;

        [Header("Chase")]
        [Tooltip("Followed when Framing is Chase The Tractor. Found automatically if left empty.")]
        public Transform tractor;
        [Min(1f)] public float chaseDistance = 14f;
        [Min(0f)] public float chaseHeight = 7f;
        [Tooltip("How quickly the camera catches up. Low is a heavy, cinematic lag.")]
        [Min(0.1f)] public float smoothing = 2.5f;

        [Header("Drone")]
        [Tooltip("Followed when Framing is From The Drone. Found automatically if left empty.")]
        public Transform drone;
        public Vector3 droneMountOffset = new Vector3(0f, -0.4f, 0.5f);
        [Tooltip("Nose-down angle of the drone's camera.")]
        [Range(-89f, 89f)] public float dronePitch = 35f;

        [Header("Cost")]
        [Tooltip("How often the feed redraws. 20 looks like a monitor; more just costs more.")]
        [Range(1f, 60f)] public float framesPerSecond = 20f;
        [Tooltip("Redraw in the editor too, so the Scene view shows the real feed. Slower, but it is " +
                 "how you aim it without pressing Play.")]
        public bool renderInEditMode = true;

        float _nextRender;
        float _orbit;
        Vector3 _chasePos;
        bool _chaseSeeded;

        void OnEnable()
        {
            if (feedCamera == null) feedCamera = GetComponentInChildren<Camera>(true);
            Bind();
        }

        void Bind()
        {
            if (feedCamera == null) return;
            // Disabled, so Unity never renders it as part of the normal camera stack — every frame it
            // draws is one this script asked for.
            feedCamera.enabled = false;
            if (output != null) feedCamera.targetTexture = output;
        }

        void Update()
        {
            if (feedCamera == null || output == null) return;
            if (!Application.isPlaying && !renderInEditMode) return;

            float dt = Time.unscaledDeltaTime;
            Aim(dt);

            float now = Time.unscaledTime;
            if (now < _nextRender) return;
            _nextRender = now + 1f / Mathf.Max(1f, framesPerSecond);

            if (feedCamera.targetTexture != output) feedCamera.targetTexture = output;
            feedCamera.Render();
        }

        void Aim(float dt)
        {
            Transform t = feedCamera.transform;

            switch (framing)
            {
                case Framing.OverheadOfTheLogo:
                {
                    // The orbit is frozen in the editor. Otherwise the camera would creep every tick and
                    // leave the scene permanently dirty, which reads as unsaved changes you never made.
                    if (Application.isPlaying) _orbit += orbitDegreesPerSecond * dt;
                    // Tilt is measured off straight-down, so 0 hangs directly overhead and 89 lies flat.
                    float tilt = Mathf.Deg2Rad * Mathf.Clamp(tiltDegrees, 0f, 89f);
                    float back = Mathf.Tan(tilt) * height;
                    Vector3 ring = Quaternion.Euler(0f, _orbit, 0f) * Vector3.back * back;
                    Vector3 pos = lookAt + Vector3.up * height + ring;
                    SetPose(t, pos, Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up));
                    break;
                }

                case Framing.ChaseTheTractor:
                {
                    if (tractor == null) tractor = FindTractor();
                    if (tractor == null) goto case Framing.OverheadOfTheLogo;

                    Vector3 want = tractor.position
                                 - tractor.forward * chaseDistance
                                 + Vector3.up * chaseHeight;
                    if (!_chaseSeeded) { _chasePos = want; _chaseSeeded = true; }
                    _chasePos = Vector3.Lerp(_chasePos, want, 1f - Mathf.Exp(-smoothing * dt));
                    SetPose(t, _chasePos, Quaternion.LookRotation(
                        (tractor.position + Vector3.up * 1.2f - _chasePos).normalized, Vector3.up));
                    break;
                }

                case Framing.FromTheDrone:
                {
                    if (drone == null) drone = FindDrone();
                    if (drone == null) goto case Framing.OverheadOfTheLogo;
                    SetPose(t, drone.TransformPoint(droneMountOffset),
                            Quaternion.Euler(dronePitch, drone.eulerAngles.y, 0f));
                    break;
                }
            }
        }

        /// <summary>
        /// Write the pose only when it has actually moved. Assigning an identical position still counts
        /// as a change to the editor's undo/dirty tracking, so an unconditional write once a frame is
        /// enough to keep a scene marked dirty forever.
        /// </summary>
        static void SetPose(Transform t, Vector3 pos, Quaternion rot)
        {
            if ((t.position - pos).sqrMagnitude > 1e-8f || Quaternion.Angle(t.rotation, rot) > 0.01f)
                t.SetPositionAndRotation(pos, rot);
        }

        /// <summary>The tractor is whatever is driving the mow route.</summary>
        static Transform FindTractor()
        {
            var follower = FindAnyObjectByType<TractorPathFollower>();
            return follower != null ? follower.transform : null;
        }

        /// <summary>The drone's flying body, not the pad the launch button sits on.</summary>
        static Transform FindDrone()
        {
            var rig = FindAnyObjectByType<DroneFlight>();
            if (rig == null) return null;
            return rig.body != null ? rig.body : rig.transform;
        }

        void OnDrawGizmosSelected()
        {
            if (feedCamera == null) return;
            Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.9f);
            Gizmos.DrawLine(feedCamera.transform.position,
                            feedCamera.transform.position + feedCamera.transform.forward * 8f);
            if (framing == Framing.OverheadOfTheLogo)
            {
                Gizmos.DrawWireSphere(lookAt, 1.5f);
                Gizmos.DrawLine(feedCamera.transform.position, lookAt);
            }
        }
    }
}
