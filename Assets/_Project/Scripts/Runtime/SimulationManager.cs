using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// Milestone-1 orchestrator: loads the path, frames the camera on the logo, and offers a restart.
    /// This is the entry point future interactive pieces (astronaut, UI, camera controls) will hang off.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimulationManager : MonoBehaviour
    {
        [Header("References")]
        public CsvWaypointLoader loader;
        public TractorPathFollower tractor;

        [Header("Mowing")]
        [Tooltip("The trail visual. If empty, the first one in the scene is found automatically.")]
        public MowingVisual_Trail mowingVisual;
        [Tooltip("Trail width as a FRACTION of the logo size, applied on Start so the marker stays a fine, " +
                 "consistent thickness at any Target World Size. 0 = leave the visual's own Width alone.")]
        [Range(0f, 0.1f)] public float trailWidthFraction = 0.012f;

        [Header("Camera framing")]
        public Camera targetCamera;
        public bool frameOnStart = true;
        [Tooltip("Extra headroom around the logo bounds (1 = tight fit).")]
        [Min(1f)] public float framePadding = 1.35f;
        [Tooltip("Direction the camera looks FROM, relative to the logo (an oblique top-down 3/4 view).")]
        public Vector3 viewDirection = new Vector3(0f, 1.1f, -1f);

        [Header("Speed (testing)")]
        [Tooltip("Playback speed multiplier — drag this live while playing, or use the keys below. " +
                 "Speeds up the whole drawing (uses Time.timeScale).")]
        [Range(0.25f, 32f)] public float speed = 1f;
        [Tooltip("Keys: 1/2/3/4/5 = 1x/2x/4x/8x/16x, [ and ] (or -/+) halve/double, R restarts.")]
        public bool enableSpeedKeys = true;

        [Header("Controls")]
        [Tooltip("Press R to restart the run (uses the new Input System).")]
        public bool enableResetKey = true;

        void Awake()
        {
            // Before the tractor's Start() begins drawing, so the very first stroke uses the right width.
            ApplyTrailWidth();
        }

        void Start()
        {
            if (loader != null) loader.Load();
            if (frameOnStart) FrameCamera();
        }

        void ApplyTrailWidth()
        {
            if (trailWidthFraction <= 0f) return;
            WaypointPath path = loader != null ? (loader.Current ?? loader.Load()) : null;
            if (path == null || path.IsEmpty) return;
            float size = Mathf.Max(path.Bounds.size.x, path.Bounds.size.z);

            var vis = mowingVisual != null ? mowingVisual : FindAnyObjectByType<MowingVisual_Trail>();
            if (vis != null)
            {
                vis.width = Mathf.Max(0.01f, size * trailWidthFraction);
                // Lay trail vertices at roughly half the ribbon width so corners read as smooth curves rather
                // than faceted "choppy" segments, while staying bounded on long paths. Scales with the logo.
                vis.minVertexDistance = Mathf.Clamp(vis.width * 0.5f, 0.05f, 0.5f);
            }

            // The grass/flower visual mows a slightly wider swath than the ribbon so clumps at the
            // ribbon's edge don't poke through the cut mark.
            var grass = FindAnyObjectByType<MowingVisual_GrassAndFlowers>();
            if (grass != null)
                grass.brushWidth = Mathf.Max(0.05f, size * trailWidthFraction * 1.5f);
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (enableResetKey && kb.rKey.wasPressedThisFrame) Restart();
                if (enableSpeedKeys)
                {
                    if (kb.digit1Key.wasPressedThisFrame) speed = 1f;
                    if (kb.digit2Key.wasPressedThisFrame) speed = 2f;
                    if (kb.digit3Key.wasPressedThisFrame) speed = 4f;
                    if (kb.digit4Key.wasPressedThisFrame) speed = 8f;
                    if (kb.digit5Key.wasPressedThisFrame) speed = 16f;
                    if (kb.leftBracketKey.wasPressedThisFrame || kb.minusKey.wasPressedThisFrame)
                        speed = Mathf.Max(0.25f, speed * 0.5f);
                    if (kb.rightBracketKey.wasPressedThisFrame || kb.equalsKey.wasPressedThisFrame)
                        speed = Mathf.Min(32f, speed * 2f);
                }
            }
#endif
            Time.timeScale = Mathf.Max(0.01f, speed);
        }

        void OnDisable()
        {
            Time.timeScale = 1f;   // don't leave the editor stuck in fast-forward
        }

        [ContextMenu("Restart Run")]
        public void Restart()
        {
            if (tractor != null) tractor.ResetRun();
        }

        [ContextMenu("Frame Camera On Logo")]
        public void FrameCamera()
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null) return;

            WaypointPath path = loader != null ? (loader.Current ?? loader.Load()) : null;
            if (path == null || path.IsEmpty) return;

            Bounds b = path.Bounds;
            float radius = Mathf.Max(b.extents.magnitude, 1f) * framePadding;

            Vector3 dir = viewDirection.sqrMagnitude > 1e-4f ? viewDirection.normalized : Vector3.up;
            float distance = targetCamera.orthographic
                ? radius
                : radius / Mathf.Sin(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);

            if (targetCamera.orthographic) targetCamera.orthographicSize = radius;
            targetCamera.transform.position = b.center + dir * distance;
            targetCamera.transform.rotation = Quaternion.LookRotation((b.center - targetCamera.transform.position).normalized, Vector3.up);
        }
    }
}
