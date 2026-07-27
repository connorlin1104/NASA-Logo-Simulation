using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// A two-panel door that parts in the middle — the elevator's doors, and any other bulkhead built
    /// the same way. The sibling of <see cref="SimpleDoor"/> (which is one panel): same code-animated,
    /// unscaled-time approach, because this project has no Animator assets and a door is two poses and
    /// an ease.
    ///
    /// Each panel slides along its own offset, stored in that panel's PARENT space and applied on top of
    /// the pose it was authored at. Storing it that way means the tool can measure the panels once — in
    /// world space, where "apart" is meaningful — and the result still holds if the whole elevator is
    /// moved, rotated or re-parented afterwards.
    ///
    /// The dashed outlines in the Scene view are where the panels will END UP. If they don't clear the
    /// opening, raise Open Distance before you press Play.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SlidingDoorPair : MonoBehaviour
    {
        [Header("Panels")]
        public Transform panelA;
        public Transform panelB;

        [Header("Motion")]
        [Tooltip("Panel A's open-pose offset, in its parent's space. The station tool measures this from " +
                 "the panels themselves; you can nudge it afterwards.")]
        public Vector3 slideA = Vector3.zero;
        [Tooltip("Panel B's open-pose offset, in its parent's space. Normally the mirror of Panel A's.")]
        public Vector3 slideB = Vector3.zero;
        [Min(0.05f)] public float moveDuration = 1.1f;
        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Audio hooks (optional — drop clips in later)")]
        public AudioSource audioSource;
        public AudioClip openClip;
        public AudioClip closeClip;

        [Header("Events")]
        public UnityEvent onOpened;
        public UnityEvent onClosed;

        [Header("Scene view")]
        public bool showGizmo = true;
        public Color gizmoColor = new Color(1f, 0.75f, 0.2f);

        public bool IsOpen => !IsMoving && _target > 0.5f;
        public bool IsClosed => !IsMoving && _target < 0.5f;
        public bool IsMoving { get; private set; }

        // The CLOSED pose is SERIALIZED, not captured at Awake. The station tool can shove the panels to
        // their open pose in the editor so you can eyeball the opening — and a captured-at-Awake rest
        // would quietly adopt that preview as "closed" after the next script reload.
        [SerializeField, HideInInspector] Vector3 _restA;
        [SerializeField, HideInInspector] Vector3 _restB;
        [SerializeField, HideInInspector] bool _restCaptured;

        float _t;              // 0 = closed .. 1 = open (pre-ease)
        float _target;

        void Awake() => CaptureRest();

        void CaptureRest()
        {
            if (_restCaptured) return;
            CaptureClosedPose();
        }

        /// <summary>Adopt the panels' current positions as the CLOSED pose. Called by the builder tool
        /// once the panels are assigned, while they are still where the model author left them.</summary>
        public void CaptureClosedPose()
        {
            if (panelA != null) _restA = panelA.localPosition;
            if (panelB != null) _restB = panelB.localPosition;
            _restCaptured = true;
        }

        /// <summary>The authored closed pose, for tools that need to restore it.</summary>
        public Vector3 ClosedLocalA => _restA;
        public Vector3 ClosedLocalB => _restB;

        public void Open() => SetTarget(1f);
        public void Close() => SetTarget(0f);
        public void Toggle() => SetTarget(_target > 0.5f ? 0f : 1f);

        public void SetImmediate(bool open)
        {
            CaptureRest();
            _target = _t = open ? 1f : 0f;
            IsMoving = false;
            Apply();
        }

        void SetTarget(float target)
        {
            CaptureRest();
            _target = target;
            if (Mathf.Approximately(_t, _target)) return;

            IsMoving = true;
            if (audioSource != null)
            {
                var clip = target > 0.5f ? openClip : closeClip;
                if (clip != null) audioSource.PlayOneShot(clip);
            }
        }

        void Update()
        {
            if (!IsMoving) return;

            _t = Mathf.MoveTowards(_t, _target, Time.unscaledDeltaTime / moveDuration);
            Apply();

            if (Mathf.Approximately(_t, _target))
            {
                IsMoving = false;
                if (_target > 0.5f) onOpened?.Invoke();
                else onClosed?.Invoke();
            }
        }

        void Apply()
        {
            float k = ease != null ? ease.Evaluate(_t) : _t;
            if (panelA != null) panelA.localPosition = _restA + slideA * k;
            if (panelB != null) panelB.localPosition = _restB + slideB * k;
        }

        // ------------------------------------------------------------------ scene view

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            DrawPanelPreview(panelA, slideA);
            DrawPanelPreview(panelB, slideB);
        }

        void DrawPanelPreview(Transform panel, Vector3 localSlide)
        {
            if (panel == null) return;
            if (!TryPanelBounds(panel, out Bounds closed)) return;

            Vector3 world = panel.parent != null ? panel.parent.TransformVector(localSlide) : localSlide;
            if (world.sqrMagnitude < 1e-8f) return;

            // At runtime the panel has already moved, so measure the open pose from the CLOSED rest.
            Vector3 centreClosed = closed.center;
            if (Application.isPlaying && _restCaptured && panel.parent != null)
                centreClosed += panel.parent.TransformPoint(panel == panelA ? _restA : _restB) - panel.position;

            Gizmos.color = gizmoColor;
            Gizmos.DrawWireCube(centreClosed + world, closed.size);
            Gizmos.DrawLine(centreClosed, centreClosed + world);

            Vector3 tip = centreClosed + world;
            Vector3 back = -world.normalized * Mathf.Min(0.25f, world.magnitude * 0.35f);
            Vector3 side = Vector3.Cross(world.normalized, Vector3.up) * 0.1f;
            if (side.sqrMagnitude < 1e-6f) side = Vector3.right * 0.1f;
            Gizmos.DrawLine(tip, tip + back + side);
            Gizmos.DrawLine(tip, tip + back - side);
        }

        static bool TryPanelBounds(Transform panel, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in panel.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }
    }

    /// <summary>
    /// Press E to open or shut a <see cref="SlidingDoorPair"/> directly — for door pairs that aren't part
    /// of an elevator cycle. Sits on a trigger collider on the Interactable layer, like every other
    /// interactable in the project.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SlidingDoorInteractable : MonoBehaviour, IInteractable
    {
        public SlidingDoorPair doors;
        public string openPrompt = "[E] Open door";
        public string closePrompt = "[E] Close door";

        public string Prompt
        {
            get
            {
                if (doors == null) return string.Empty;
                if (doors.IsMoving) return "Door moving…";
                return doors.IsOpen ? closePrompt : openPrompt;
            }
        }

        public bool CanInteract(InteractionSensor sensor) => doors != null;

        public void Interact(InteractionSensor sensor)
        {
            if (doors != null && !doors.IsMoving) doors.Toggle();
        }
    }
}
