using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// A door that swings open on a hinge when you press E, and swings back when you press E again.
    ///
    /// Why this exists next to <see cref="SimpleDoor"/>: SimpleDoor's SwingYaw rotates a panel about its
    /// OWN origin. That is fine for a panel authored with its pivot on the hinge, and wrong for every
    /// imported one — a Maya group's pivot is wherever the modeller left it, usually the middle or the
    /// world origin, so the door pirouettes instead of swinging. This rotates about an explicit hinge
    /// LINE (a point plus an axis, both in the panel's parent space), which is what a real hinge is, and
    /// works no matter where the pivot sits.
    ///
    /// Doubles carry a second panel that swings the other way, so a two-leaf airlock hatch is one
    /// component and one E press.
    ///
    /// Unscaled time throughout: a door takes its real seconds even with the sim fast-forwarded to 16x.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HingedDoor : MonoBehaviour, IInteractable
    {
        [Header("Panels")]
        [Tooltip("The leaf that swings. Leave empty to swing this object itself.")]
        public Transform panel;
        [Tooltip("Optional second leaf for a double door — swings by Second angle, normally the negative " +
                 "of the first so the two open apart.")]
        public Transform secondPanel;

        [Header("Hinge — a point and an axis in each panel's PARENT space")]
        [Tooltip("A point ON the hinge line. The builder tool puts this on the vertical edge of the door.")]
        public Vector3 hinge;
        [Tooltip("Direction of the hinge pin. Vertical for a normal door; the tool writes true world-up " +
                 "expressed in parent space, so a tilted parent still gives an upright hinge.")]
        public Vector3 hingeAxis = Vector3.up;
        public Vector3 secondHinge;
        public Vector3 secondHingeAxis = Vector3.up;

        [Header("Swing")]
        [Tooltip("Degrees the first leaf turns when open. Negative swings the other way.")]
        [Range(-170f, 170f)] public float openAngle = 95f;
        [Range(-170f, 170f)] public float secondOpenAngle = -95f;
        [Min(0.05f)] public float moveDuration = 1.4f;
        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Behaviour")]
        public bool startOpen;
        [Tooltip("Swing shut on its own this many real seconds after finishing opening. 0 = stays open " +
                 "until you press E again.")]
        [Min(0f)] public float autoCloseSeconds;
        [Tooltip("Off makes it a one-way door: E opens it, E can't shut it again.")]
        public bool canClose = true;

        [Header("Prompt")]
        public string openPrompt = "[E] Open the door";
        public string closePrompt = "[E] Close the door";

        [Header("Audio hooks (optional — drop clips in later)")]
        public AudioSource audioSource;
        public AudioClip openClip;
        public AudioClip closeClip;

        [Header("Events")]
        public UnityEvent onOpened;
        public UnityEvent onClosed;

        [Header("Scene view")]
        public bool showGizmo = true;
        public Color closedColor = new Color(1f, 0.45f, 0.2f, 1f);
        public Color openColor = new Color(0.35f, 1f, 0.55f, 1f);

        // Serialized, never captured in Awake: the editor tool's open/closed preview moves the real
        // panels, and an Awake capture would adopt whatever the preview left behind as the new closed
        // pose — the door would walk a little further open every time you rebuilt it.
        [SerializeField, HideInInspector] Vector3 _restPos, _restPos2;
        [SerializeField, HideInInspector] Quaternion _restRot = Quaternion.identity, _restRot2 = Quaternion.identity;
        [SerializeField, HideInInspector] bool _restCaptured;
        [SerializeField, HideInInspector] float _panelRadius = 1f, _panelHeight = 2f;

        public bool IsOpen => !IsMoving && _t > 0.5f;
        public bool IsClosed => !IsMoving && _t < 0.5f;
        public bool IsMoving { get; private set; }

        float _t;        // 0 = closed .. 1 = open, pre-ease
        float _target;
        float _autoCloseAt = -1f;

        Transform Leaf => panel != null ? panel : transform;

        void Start()
        {
            if (startOpen) SetImmediate(true);
            else Apply();
        }

        // ------------------------------------------------------------------ commands

        public void Open() => SetTarget(1f);
        public void Close() => SetTarget(0f);
        public void Toggle() => SetTarget(_target > 0.5f ? 0f : 1f);

        public void SetImmediate(bool open)
        {
            _target = _t = open ? 1f : 0f;
            IsMoving = false;
            _autoCloseAt = -1f;
            Apply();
        }

        void SetTarget(float target)
        {
            if (Mathf.Approximately(_target, target)) return;
            _target = target;
            IsMoving = true;
            _autoCloseAt = -1f;

            AudioClip clip = target > 0.5f ? openClip : closeClip;
            if (audioSource != null && clip != null) audioSource.PlayOneShot(clip);
        }

        /// <summary>Record the panels' current local poses as the CLOSED pose. Editor tool only.</summary>
        public void CaptureClosedPose()
        {
            Transform leaf = Leaf;
            _restPos = leaf.localPosition;
            _restRot = leaf.localRotation;
            if (secondPanel != null)
            {
                _restPos2 = secondPanel.localPosition;
                _restRot2 = secondPanel.localRotation;
            }
            _restCaptured = true;
        }

        /// <summary>Door size, used only to scale the scene-view arc. Editor tool only.</summary>
        public void SetGizmoSize(float radius, float height)
        {
            _panelRadius = Mathf.Max(0.1f, radius);
            _panelHeight = Mathf.Max(0.1f, height);
        }

        public Vector3 ClosedLocalPosition => _restPos;
        public bool HasClosedPose => _restCaptured;

        // ------------------------------------------------------------------ interaction

        public string Prompt => _target > 0.5f ? closePrompt : openPrompt;

        public bool CanInteract(InteractionSensor sensor)
        {
            if (IsMoving) return false;
            return canClose || _target < 0.5f;   // one-way doors vanish from the prompt once open
        }

        public void Interact(InteractionSensor sensor) => Toggle();

        // ------------------------------------------------------------------ motion

        void Update()
        {
            if (IsMoving)
            {
                _t = Mathf.MoveTowards(_t, _target, Time.unscaledDeltaTime / moveDuration);
                Apply();

                if (Mathf.Approximately(_t, _target))
                {
                    IsMoving = false;
                    if (_target > 0.5f)
                    {
                        onOpened?.Invoke();
                        if (autoCloseSeconds > 0f) _autoCloseAt = Time.unscaledTime + autoCloseSeconds;
                    }
                    else onClosed?.Invoke();
                }
                return;
            }

            if (_autoCloseAt > 0f && Time.unscaledTime >= _autoCloseAt) Close();
        }

        void Apply()
        {
            if (!_restCaptured) return;
            float k = ease != null ? ease.Evaluate(_t) : _t;
            Swing(Leaf, _restPos, _restRot, hinge, hingeAxis, openAngle * k);
            Swing(secondPanel, _restPos2, _restRot2, secondHinge, secondHingeAxis, secondOpenAngle * k);
        }

        /// <summary>
        /// Rotate a panel about a hinge LINE rather than about its own origin: turn the panel, then carry
        /// its position round the same arc. Both are in the panel's parent space, so this is exact
        /// whatever the modeller did with the pivot.
        /// </summary>
        static void Swing(Transform t, Vector3 restPos, Quaternion restRot,
                          Vector3 hingePoint, Vector3 axis, float degrees)
        {
            if (t == null) return;
            Vector3 a = axis.sqrMagnitude < 1e-6f ? Vector3.up : axis.normalized;
            Quaternion q = Quaternion.AngleAxis(degrees, a);
            t.localRotation = q * restRot;
            t.localPosition = hingePoint + q * (restPos - hingePoint);
        }

        // ------------------------------------------------------------------ scene view

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            Transform leaf = Leaf;
            if (leaf == null) return;
            Transform space = leaf.parent;

            DrawLeaf(space, _restCaptured ? _restPos : leaf.localPosition, hinge, hingeAxis, openAngle);
            if (secondPanel != null)
                DrawLeaf(secondPanel.parent, _restCaptured ? _restPos2 : secondPanel.localPosition,
                         secondHinge, secondHingeAxis, secondOpenAngle);
        }

        void DrawLeaf(Transform space, Vector3 restPos, Vector3 hingePoint, Vector3 axis, float angle)
        {
            Vector3 a = axis.sqrMagnitude < 1e-6f ? Vector3.up : axis.normalized;
            Vector3 pivot = space != null ? space.TransformPoint(hingePoint) : hingePoint;
            Vector3 worldAxis = (space != null ? space.TransformDirection(a) : a).normalized;

            // The panel is drawn as the line from the hinge to its far edge — enough to read which way it
            // swings and how far, without pretending to be the mesh.
            Vector3 armLocal = restPos - hingePoint;
            Vector3 arm = space != null ? space.TransformVector(armLocal) : armLocal;
            if (arm.sqrMagnitude < 1e-4f)
            {
                // Pivot sits on top of the panel origin (a centred pivot): point the arm along whichever
                // way is most across the hinge, so there is still something to look at.
                Vector3 any = Mathf.Abs(worldAxis.y) < 0.9f ? Vector3.up : Vector3.forward;
                arm = Vector3.Cross(worldAxis, any).normalized * _panelRadius;
            }
            arm = Vector3.ProjectOnPlane(arm, worldAxis);
            if (arm.sqrMagnitude < 1e-4f) return;
            arm = arm.normalized * _panelRadius;

            float half = _panelHeight * 0.5f;
            Vector3 lo = pivot - worldAxis * half;
            Vector3 hi = pivot + worldAxis * half;

            Gizmos.color = closedColor;
            Gizmos.DrawLine(lo, hi);                       // the hinge pin
            Gizmos.DrawLine(lo, lo + arm);                 // closed silhouette
            Gizmos.DrawLine(hi, hi + arm);
            Gizmos.DrawLine(lo + arm, hi + arm);

            Vector3 openArm = Quaternion.AngleAxis(angle, worldAxis) * arm;
            Gizmos.color = openColor;
            Gizmos.DrawLine(lo, lo + openArm);             // open silhouette
            Gizmos.DrawLine(hi, hi + openArm);
            Gizmos.DrawLine(lo + openArm, hi + openArm);

            // The swept arc, at mid height.
            const int Steps = 16;
            Vector3 prev = pivot + arm;
            for (int i = 1; i <= Steps; i++)
            {
                Vector3 next = pivot + Quaternion.AngleAxis(angle * i / Steps, worldAxis) * arm;
                Gizmos.DrawLine(prev, next);
                prev = next;
            }
        }
    }
}
