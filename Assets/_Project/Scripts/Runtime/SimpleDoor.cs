using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// A code-animated door panel — this project has no Animator assets, and a door is two poses and an
    /// ease. Slides (airlock doors sweep upward) or swings about local Y between the authored rest pose
    /// and an open pose. The panel's own collider physically blocks the way while closed.
    ///
    /// Runs on UNSCALED time: a door must take the same real seconds when the sim is fast-forwarded to
    /// 16x (see <see cref="AstronautController"/> for the timeScale convention).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SimpleDoor : MonoBehaviour
    {
        public enum Motion { SlideLocal, SwingYaw }

        [Header("Motion")]
        public Motion motion = Motion.SlideLocal;
        [Tooltip("SlideLocal: local-space offset of the OPEN pose. Airlock doors sweep straight up.")]
        public Vector3 slideOffset = new Vector3(0f, 3f, 0f);
        [Tooltip("SwingYaw: opening rotation about local Y, for hinged doors.")]
        public float swingAngleDeg = 100f;
        [Min(0.05f)] public float moveDuration = 1.8f;
        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Audio hooks (optional — drop clips in later)")]
        public AudioSource audioSource;
        public AudioClip openClip;
        public AudioClip closeClip;

        [Header("Events")]
        public UnityEvent onOpened;
        public UnityEvent onClosed;

        public bool IsOpen => !IsMoving && _target > 0.5f;
        public bool IsClosed => !IsMoving && _target < 0.5f;
        public bool IsMoving { get; private set; }

        float _t;          // 0 = closed .. 1 = open (pre-ease)
        float _target;
        Vector3 _restPos;
        Quaternion _restRot;
        bool _restCaptured;

        void Awake() => CaptureRest();

        // The rest pose is the CLOSED pose as authored in the scene; captured once, lazily, so editor
        // tools that build the door and immediately call SetImmediate get consistent behaviour.
        void CaptureRest()
        {
            if (_restCaptured) return;
            _restPos = transform.localPosition;
            _restRot = transform.localRotation;
            _restCaptured = true;
        }

        public void Open() => SetTarget(1f);
        public void Close() => SetTarget(0f);

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
            if (motion == Motion.SlideLocal)
                transform.localPosition = _restPos + slideOffset * k;
            else
                transform.localRotation = _restRot * Quaternion.Euler(0f, swingAngleDeg * k, 0f);
        }
    }
}
