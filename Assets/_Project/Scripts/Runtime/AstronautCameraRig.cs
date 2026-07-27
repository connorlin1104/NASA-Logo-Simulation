using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// Two-mode camera for the astronaut, driving the scene's single Main Camera by transform (rather
    /// than adding extra Cameras, which would duplicate the AudioListener and cost a second render).
    ///
    /// <list type="bullet">
    /// <item><b>FirstPerson</b> (default) - the visor view at the head anchor. The mouse yaws the BODY;
    /// pitch stays on the camera. This is the walking mode.</item>
    /// <item><b>FlyCam</b> - a free camera: fly anywhere with WASD + Space/Ctrl (up/down), hold Shift to
    /// go fast, scroll to scale the fly speed (which is how you "zoom" anywhere). The astronaut's input
    /// is disabled while flying, so E/W/A/S/D never leak into the character.</item>
    /// </list>
    ///
    /// C toggles between the two. Uses <c>Time.unscaledDeltaTime</c> throughout - see
    /// <see cref="AstronautController"/> for why.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AstronautCameraRig : MonoBehaviour
    {
        public enum ViewMode { FirstPerson, FlyCam }

        [Header("Targets")]
        public AstronautController astronaut;
        [Tooltip("Drives the first-person arm pose so the arms swing into view in the visor camera. " +
                 "Auto-found from the astronaut if left empty; survives the FBX swap.")]
        public AstronautLocomotionVisual locomotion;
        [Tooltip("Eye position for first person. Survives the FBX swap (re-anchored to the head bone).")]
        public Transform headAnchor;

        [Header("Mode")]
        public ViewMode mode = ViewMode.FirstPerson;
        [Tooltip("C toggles FirstPerson <-> FlyCam.")]
        public bool enableModeKey = true;

        [Header("Look")]
        [Min(0f)] public float mouseSensitivity = 0.12f;
        public bool invertY = false;
        [Tooltip("First-person pitch range.")]
        public float minPitchFirstPerson = -80f;
        public float maxPitchFirstPerson = 80f;

        [Header("First person")]
        public float firstPersonNearClip = 0.05f;
        public Vector3 firstPersonOffset = new Vector3(0f, 0f, 0.06f);

        [Header("Fly cam")]
        [Min(0.1f)] public float flySpeed = 8f;
        [Tooltip("Hold Left Shift to multiply the fly speed.")]
        [Min(1f)] public float flyFastMultiplier = 4f;
        [Tooltip("Each scroll notch multiplies/divides the fly speed by this — scroll up to move (and " +
                 "therefore zoom) faster, scroll down for fine, slow framing.")]
        [Min(1.01f)] public float flySpeedScrollFactor = 1.15f;
        [Min(0.01f)] public float flyMinSpeed = 0.5f;
        [Min(1f)] public float flyMaxSpeed = 100f;

        [Header("Cursor")]
        [Tooltip("Lock and hide the cursor while a camera mode is using mouse look. Press Escape to " +
                 "release it (Unity does this for you in the editor); click the Game view to re-lock.")]
        public bool lockCursorWhileWalking = true;

        Camera _cam;
        float _yaw;
        float _pitch;
        float _defaultNearClip;
        ViewMode _appliedMode = (ViewMode)(-1);

        /// <summary>
        /// The look yaw in degrees. This — not the astronaut's transform — is the authority on which way
        /// the body faces in first person: <see cref="UpdateFirstPerson"/> re-writes the body's rotation
        /// from it every LateUpdate, so anything that wants to turn the astronaut (see
        /// <see cref="AstronautSitting"/>, which swings you round to face a chair) has to set it HERE or
        /// its rotation is undone the same frame.
        /// </summary>
        public float Yaw
        {
            get => _yaw;
            set => _yaw = value;
        }

        void Awake()
        {
            _cam = GetComponent<Camera>();
            if (_cam == null) _cam = Camera.main;
            if (_cam != null) _defaultNearClip = _cam.nearClipPlane;
            if (astronaut == null) astronaut = FindAnyObjectByType<AstronautController>();
            if (locomotion == null && astronaut != null)
                locomotion = astronaut.GetComponentInChildren<AstronautLocomotionVisual>();

            if (astronaut != null)
            {
                _yaw = astronaut.transform.eulerAngles.y;
                _pitch = 5f;
            }
        }

        void Start() => EnterMode(mode, force: true);

        void OnDisable()
        {
            // Never leave the editor with a captured cursor.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Update()
        {
            if (enableModeKey && ModeKeyPressed())
                EnterMode(mode == ViewMode.FirstPerson ? ViewMode.FlyCam : ViewMode.FirstPerson);

            ReadLook();
        }

        // LateUpdate so the camera follows the position the astronaut reached this frame (no lag/jitter).
        void LateUpdate()
        {
            if (_cam == null) return;

            switch (mode)
            {
                case ViewMode.FirstPerson:
                    if (astronaut != null) UpdateFirstPerson();
                    break;
                case ViewMode.FlyCam:
                    UpdateFlyCam();
                    break;
            }
        }

        // ---------------------------------------------------------------- modes

        public void EnterMode(ViewMode next, bool force = false)
        {
            if (!force && _appliedMode == next && mode == next) return;
            mode = next;
            _appliedMode = next;

            if (_cam != null)
                _cam.nearClipPlane = (next == ViewMode.FirstPerson) ? firstPersonNearClip : _defaultNearClip;

            // Lift the arms into view only in the visor (first-person) camera.
            if (locomotion != null)
                locomotion.firstPerson = (next == ViewMode.FirstPerson);

            // The fly cam owns the input while active: freeze the astronaut so WASD flies the camera
            // instead of walking the character (and the interaction sensor hides its prompt).
            if (astronaut != null)
                astronaut.enableInput = (next == ViewMode.FirstPerson);

            if (next == ViewMode.FirstPerson && astronaut != null)
            {
                // Carry the fly yaw over to the body so returning to FP doesn't snap the view sideways.
                astronaut.transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
                _pitch = Mathf.Clamp(_pitch, minPitchFirstPerson, maxPitchFirstPerson);
            }
            else if (next == ViewMode.FlyCam)
            {
                // Seed from the camera's current pose so switching never snaps the view.
                Vector3 e = transform.eulerAngles;
                _yaw = e.y;
                _pitch = Mathf.Clamp(NormalizePitch(e.x), -89f, 89f);
            }

            ApplyCursorState();
        }

        static float NormalizePitch(float eulerX) => eulerX > 180f ? eulerX - 360f : eulerX;

        void ApplyCursorState()
        {
            if (!lockCursorWhileWalking)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        void UpdateFirstPerson()
        {
            Transform head = headAnchor != null ? headAnchor : astronaut.transform;

            // Yaw drives the body; pitch stays on the camera so the astronaut doesn't tip over.
            Vector3 e = astronaut.transform.eulerAngles;
            astronaut.transform.rotation = Quaternion.Euler(e.x, _yaw, e.z);

            Quaternion look = Quaternion.Euler(_pitch, _yaw, 0f);
            _cam.transform.SetPositionAndRotation(head.position + look * firstPersonOffset, look);
        }

        void UpdateFlyCam()
        {
            float dt = Time.unscaledDeltaTime;
            Quaternion look = Quaternion.Euler(_pitch, _yaw, 0f);

            Vector3 move = ReadFlyMove();                       // x strafe, y world up/down, z forward
            Vector3 delta = look * new Vector3(move.x, 0f, move.z) + Vector3.up * move.y;
            if (delta.sqrMagnitude > 1f) delta.Normalize();

            float speed = flySpeed * (FastHeld() ? flyFastMultiplier : 1f);
            _cam.transform.SetPositionAndRotation(_cam.transform.position + delta * speed * dt, look);
        }

        // ---------------------------------------------------------------- input

        void ReadLook()
        {
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse == null) return;
            if (lockCursorWhileWalking && Cursor.lockState != CursorLockMode.Locked)
            {
                // Cursor released (Escape). Re-lock on click, and don't look around meanwhile.
                if (mouse.leftButton.wasPressedThisFrame) ApplyCursorState();
                return;
            }

            // Mouse delta is already per-frame; do NOT scale by delta time.
            Vector2 d = mouse.delta.ReadValue() * mouseSensitivity;
            _yaw += d.x;
            _pitch += invertY ? d.y : -d.y;
            _pitch = (mode == ViewMode.FirstPerson)
                ? Mathf.Clamp(_pitch, minPitchFirstPerson, maxPitchFirstPerson)
                : Mathf.Clamp(_pitch, -89f, 89f);

            // Scroll scales the fly speed (clamped exponent: trackpads report large per-frame deltas).
            if (mode == ViewMode.FlyCam)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    flySpeed = Mathf.Clamp(
                        flySpeed * Mathf.Pow(flySpeedScrollFactor, Mathf.Clamp(scroll, -3f, 3f)),
                        flyMinSpeed, flyMaxSpeed);
            }
#endif
        }

        Vector3 ReadFlyMove()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return Vector3.zero;
            float x = 0f, y = 0f, z = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) z -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) z += 1f;
            if (kb.spaceKey.isPressed) y += 1f;
            if (kb.leftCtrlKey.isPressed) y -= 1f;
            return new Vector3(x, y, z);
#else
            return Vector3.zero;
#endif
        }

        bool FastHeld()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.leftShiftKey.isPressed;
#else
            return false;
#endif
        }

        bool ModeKeyPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.cKey.wasPressedThisFrame;
#else
            return false;
#endif
        }
    }
}
