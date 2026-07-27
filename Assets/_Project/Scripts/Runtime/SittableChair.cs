using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// A chair the astronaut can sit in. Lives on the chair model itself (or on a wrapper around it) and
    /// carries two anchors, both real child objects you can drag in the Scene view:
    ///
    /// <list type="bullet">
    /// <item><b>SeatAnchor</b> — where the HIPS land, and which way the body faces. Its blue forward
    /// arrow is the direction you'll be looking when you sit down.</item>
    /// <item><b>StandAnchor</b> — where the feet end up when you get back out.</item>
    /// </list>
    ///
    /// The seated figure drawn in the Scene view is not decoration: it is the pose
    /// <see cref="AstronautSitting"/> will actually put the astronaut in, so you can line a chair up to
    /// its desk without pressing Play. Built by Tools &gt; NASA Sim &gt; Station &gt; Sittable Chairs.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SittableChair : MonoBehaviour, IInteractable
    {
        [Header("Anchors (drag them in the Scene view)")]
        [Tooltip("Where the hips land. Its forward (blue) axis is the direction the astronaut faces.")]
        public Transform seatAnchor;
        [Tooltip("Where the astronaut stands up to. Leave empty to step out in front of the seat.")]
        public Transform standAnchor;

        [Header("Prompt")]
        public string sitPrompt = "[E] Sit down";

        [Header("Scene view")]
        public bool showGizmo = true;
        public Color gizmoColor = new Color(0.45f, 0.85f, 1f);
        [Tooltip("Size of the seated figure drawn in the Scene view, as a multiple of an adult's " +
                 "proportions. Purely a preview — it doesn't change how the astronaut is posed.")]
        [Range(0.5f, 1.6f)] public float figureScale = 1f;

        /// <summary>How far in front of the seat you land when standing up, if there is no StandAnchor.</summary>
        public const float StepOutDistance = 0.7f;

        // Rough adult proportions, metres — the drawn preview only.
        const float ThighLength = 0.44f;
        const float CalfLength = 0.46f;
        const float TorsoLength = 0.58f;

        /// <summary>The astronaut currently in this chair, if any. Set by <see cref="AstronautSitting"/>.</summary>
        public AstronautSitting Occupant { get; internal set; }

        public Vector3 SeatPosition => seatAnchor != null ? seatAnchor.position : transform.position;

        /// <summary>Facing direction of the seat, flattened to the ground plane.</summary>
        public Vector3 SeatForward
        {
            get
            {
                Vector3 f = seatAnchor != null ? seatAnchor.forward : transform.forward;
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            }
        }

        /// <summary>Where the feet go on standing up — the StandAnchor, else a step out in front.</summary>
        public Vector3 StandPosition =>
            standAnchor != null ? standAnchor.position : SeatPosition + SeatForward * StepOutDistance;

        // ------------------------------------------------------------------ interaction

        public string Prompt => sitPrompt;

        public bool CanInteract(InteractionSensor sensor)
        {
            if (seatAnchor == null || Occupant != null) return false;
            var sitter = FindSitter(sensor);
            return sitter != null && sitter.CanSit;
        }

        public void Interact(InteractionSensor sensor)
        {
            var sitter = FindSitter(sensor);
            if (sitter != null) sitter.SitOn(this);
        }

        static AstronautSitting FindSitter(InteractionSensor sensor)
        {
            if (sensor != null)
            {
                var onSensor = sensor.GetComponentInParent<AstronautSitting>();
                if (onSensor != null) return onSensor;
            }
            return FindAnyObjectByType<AstronautSitting>();
        }

        // ------------------------------------------------------------------ scene view

        void OnDrawGizmos()
        {
            if (!showGizmo || seatAnchor == null) return;

            Vector3 hips = SeatPosition;
            Vector3 fwd = SeatForward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float s = figureScale;

            Gizmos.color = gizmoColor;

            // Thighs forward, calves down, spine up, head on top — the pose AstronautSitting produces.
            for (int side = -1; side <= 1; side += 2)
            {
                Vector3 hip = hips + right * (0.11f * s * side);
                Vector3 knee = hip + fwd * (ThighLength * s);
                Vector3 foot = knee + Vector3.down * (CalfLength * s);
                Gizmos.DrawLine(hip, knee);
                Gizmos.DrawLine(knee, foot);
                Gizmos.DrawLine(foot, foot + fwd * (0.14f * s));      // toes
            }

            Vector3 neck = hips + Vector3.up * (TorsoLength * s);
            Gizmos.DrawLine(hips, neck);
            Gizmos.DrawWireSphere(neck + Vector3.up * (0.13f * s), 0.12f * s);

            // Facing arrow at eye level — the direction you'll be looking.
            Vector3 eye = neck + Vector3.up * (0.13f * s);
            Vector3 tip = eye + fwd * (0.55f * s);
            Gizmos.DrawLine(eye, tip);
            Gizmos.DrawLine(tip, tip - fwd * 0.14f + right * 0.08f);
            Gizmos.DrawLine(tip, tip - fwd * 0.14f - right * 0.08f);

            // Where you get out.
            Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.55f);
            Vector3 stand = StandPosition;
            DrawCircle(stand, 0.28f);
            Gizmos.DrawLine(hips, stand);

#if UNITY_EDITOR
            UnityEditor.Handles.color = gizmoColor;
            UnityEditor.Handles.Label(neck + Vector3.up * (0.34f * s),
                Occupant != null ? "occupied" : sitPrompt);
            UnityEditor.Handles.Label(stand + Vector3.up * 0.1f, "stand up here");
#endif
        }

        static void DrawCircle(Vector3 centre, float radius, int segments = 24)
        {
            Vector3 prev = centre + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 p = centre + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
