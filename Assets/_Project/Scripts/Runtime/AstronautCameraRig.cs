using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace NasaSim
{
    /// <summary>
    /// Three-mode camera for the astronaut, driving the scene's single Main Camera by transform (rather
    /// than adding extra Cameras, which would duplicate the AudioListener and cost a second render).
    ///
    /// <list type="bullet">
    /// <item><b>ThirdPerson</b> - orbits behind the astronaut. Best view of the walk cycle and the stair climb.</item>
    /// <item><b>FirstPerson</b> - sits at the head anchor; the mouse yaws the body, arms swing in view.</item>
    /// <item><b>Overview</b> - hands the camera back to <see cref="SimulationManager.FrameCamera"/>, the
    /// original top-down framing of the logo. Set once on entry, then left alone so the two never fight
    /// over the transform.</item>
    /// </list>
    ///
    /// Uses <c>Time.unscaledDeltaTime</c> throughout - see <see cref="AstronautController"/> for why.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AstronautCameraRig : MonoBehaviour
    {
        public enum ViewMode { ThirdPerson, FirstPerson, Overview }

        [Header("Targets")]
        public AstronautController astronaut;
        [Tooltip("Drives the first-person arm pose so the arms swing into view in the visor camera. " +
                 "Auto-found from the astronaut if left empty; survives the FBX swap.")]
        public AstronautLocomotionVisual locomotion;
        [Tooltip("Orbit centre for third person (chest/shoulder height). Survives the FBX swap.")]
        public Transform cameraPivot;
        [Tooltip("Eye position for first person. Survives the FBX swap (re-anchored to the head bone).")]
        public Transform headAnchor;
        [Tooltip("Used by Overview mode to restore the logo framing. Auto-found if empty.")]
        public SimulationManager simulationManager;

        [Header("Mode")]
        public ViewMode mode = ViewMode.ThirdPerson;
        [Tooltip("Cycles ThirdPerson -> FirstPerson -> Overview.")]
        public bool enableModeKey = true;

        [Header("Look")]
        [Min(0f)] public float mouseSensitivity = 0.12f;
        public bool invertY = false;
        [Tooltip("Third-person pitch range. Negative looks down from above.")]
        public float minPitch = -35f;
        public float maxPitch = 70f;
        [Tooltip("First-person pitch range.")]
        public float minPitchFirstPerson = -80f;
        public float maxPitchFirstPerson = 80f;

        [Header("Third person")]
        [Min(0.5f)] public float distance = 4f;
        [Tooltip("Extra height above the pivot, before pitch is applied.")]
        public float shoulderHeight = 0.3f;
        [Tooltip("How quickly the camera catches up to the astronaut. 0 = rigid.")]
        [Min(0f)] public float followSmoothing = 12f;
        [Tooltip("Pull the camera in when geometry (railings, dome, stairs) would come between it and the astronaut.")]
        public bool collideWithGeometry = true;
        [Min(0.01f)] public float collisionRadius = 0.25f;
        [Tooltip("Layers the camera collides against. Exclude the astronaut's own layer.")]
        public LayerMask collisionMask = ~0;

        [Header("First person")]
        public float firstPersonNearClip = 0.05f;
        public Vector3 firstPersonOffset = new Vector3(0f, 0f, 0.06f);

        [Header("Cursor")]
        [Tooltip("Lock and hide the cursor in the walking modes. Press Escape to release it (Unity does " +
                 "this for you in the editor); click the Game view to re-lock.")]
        public bool lockCursorWhileWalking = true;

        Camera _cam;
        float _yaw;
        float _pitch;
        float _defaultNearClip;
        Vector3 _smoothedPivot;
        bool _pivotInitialised;
        ViewMode _appliedMode = (ViewMode)(-1);
        readonly RaycastHit[] _hits = new RaycastHit[16];

        void Awake()
        {
            _cam = GetComponent<Camera>();
            if (_cam == null) _cam = Camera.main;
            if (_cam != null) _defaultNearClip = _cam.nearClipPlane;
            if (simulationManager == null) simulationManager = FindAnyObjectByType<SimulationManager>();
            if (astronaut == null) astronaut = FindAnyObjectByType<AstronautController>();
            if (locomotion == null && astronaut != null)
                locomotion = astronaut.GetComponentInChildren<AstronautLocomotionVisual>();

            if (astronaut != null)
            {
                _yaw = astronaut.transform.eulerAngles.y;
                _pitch = 15f;
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
                EnterMode((ViewMode)(((int)mode + 1) % 3));

            if (mode != ViewMode.Overview)
                ReadLook();
        }

        // LateUpdate so the camera follows the position the astronaut reached this frame (no lag/jitter).
        void LateUpdate()
        {
            if (_cam == null || astronaut == null) return;

            switch (mode)
            {
                case ViewMode.ThirdPerson: UpdateThirdPerson(); break;
                case ViewMode.FirstPerson: UpdateFirstPerson(); break;
                // Overview: deliberately untouched. SimulationManager.FrameCamera placed it on entry.
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

            if (astronaut != null)
            {
                // In first person the mouse yaws the BODY; in third person it orbits the camera and the
                // body turns to face travel instead.
                astronaut.bodyRelative = (next == ViewMode.FirstPerson);
                astronaut.cameraTransform = (_cam != null) ? _cam.transform : null;

                if (next == ViewMode.FirstPerson)
                {
                    // Carry the orbit yaw over to the body so entering FP doesn't snap the view sideways.
                    _yaw = astronaut.transform.eulerAngles.y;
                    _pitch = Mathf.Clamp(_pitch, minPitchFirstPerson, maxPitchFirstPerson);
                }
                else if (next == ViewMode.ThirdPerson)
                {
                    _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);
                    _pivotInitialised = false;   // snap rather than smear in from the last mode's position
                }
            }

            if (next == ViewMode.Overview && simulationManager != null)
                simulationManager.FrameCamera();   // reuse the existing logo framing; set once

            ApplyCursorState();
        }

        void ApplyCursorState()
        {
            bool walking = mode != ViewMode.Overview;
            if (!lockCursorWhileWalking || !walking)
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

        void UpdateThirdPerson()
        {
            Transform pivotT = cameraPivot != null ? cameraPivot : astronaut.transform;
            Vector3 pivot = pivotT.position + Vector3.up * shoulderHeight;

            float dt = Time.unscaledDeltaTime;
            if (!_pivotInitialised || followSmoothing <= 0f || dt <= 0f)
            {
                _smoothedPivot = pivot;
                _pivotInitialised = true;
            }
            else
            {
                _smoothedPivot = Vector3.Lerp(_smoothedPivot, pivot, 1f - Mathf.Exp(-followSmoothing * dt));
            }

            Quaternion orbit = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 desired = _smoothedPivot + orbit * (Vector3.back * distance);

            if (collideWithGeometry)
            {
                Vector3 toCam = desired - _smoothedPivot;
                float len = toCam.magnitude;
                if (len > 1e-4f)
                {
                    float blocked = NearestBlockingDistance(_smoothedPivot, toCam / len, len);
                    if (blocked >= 0f)
                        desired = _smoothedPivot + (toCam / len) * Mathf.Max(0.15f, blocked);
                }
            }

            _cam.transform.SetPositionAndRotation(desired, orbit);
        }

        /// <summary>
        /// Nearest obstruction between the pivot and the camera, or -1 if the view is clear. The cast
        /// STARTS INSIDE the astronaut's own CharacterController, so self-hits (and zero-distance
        /// overlap hits, which Unity reports inconsistently) must be filtered out explicitly - otherwise
        /// the camera would slam into the astronaut's back on the first frame.
        /// </summary>
        float NearestBlockingDistance(Vector3 origin, Vector3 dir, float maxDistance)
        {
            int count = Physics.SphereCastNonAlloc(origin, collisionRadius, dir, _hits, maxDistance,
                                                   collisionMask, QueryTriggerInteraction.Ignore);
            Transform self = astronaut != null ? astronaut.transform : null;
            float best = -1f;

            for (int i = 0; i < count; i++)
            {
                var h = _hits[i];
                if (h.collider == null) continue;
                if (h.distance <= 1e-4f) continue;                                  // overlapping at the origin
                if (self != null && h.collider.transform.IsChildOf(self)) continue; // the astronaut itself
                if (best < 0f || h.distance < best) best = h.distance;
            }
            return best;
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
                : Mathf.Clamp(_pitch, minPitch, maxPitch);
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
