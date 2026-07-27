using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// A chamber that pressurizes itself when you are standing in it and vents when you leave. No button,
    /// no door sequencing, no state to get stuck in — walk into the second chamber of the tunnel and the
    /// gas comes in around you.
    ///
    /// This is deliberately NOT <see cref="AirlockController"/>. That one is a full ingress/egress cycle
    /// that owns two doors and refuses to run unless both are wired and sealed; it is the right machine
    /// for a working airlock and the wrong one for "make the middle of the tunnel feel like it has
    /// pressure in it". Here the chamber is the only thing that exists, and it cannot deadlock: whatever
    /// state it is in, being inside drives the pressure toward 1 and being outside drives it toward 0.
    ///
    /// <b>Presence is POLLED, not triggered.</b> A CharacterController only fires trigger callbacks while
    /// it is moving, so a player who walks in and stands still would be missed by OnTriggerEnter and the
    /// chamber would sit in vacuum around them. A per-frame point-in-box test cannot miss anyone —
    /// standing still, teleported in, or dropped in from above. Same reasoning as
    /// <see cref="AirlockChamberSensor"/> next door.
    ///
    /// <b>What "filling" looks like.</b> The fog's emitter box GROWS from the floor upward with the
    /// pressure rather than just emitting harder, so gas visibly rises to fill the room instead of
    /// fading in everywhere at once. The jets only run while the pressure is actually changing, which is
    /// what makes the still moment at the end read as "pressurized" rather than "still filling".
    ///
    /// Unscaled time throughout: the sim fast-forwards the mow to 16x, and a 5-second pressurization that
    /// tracked it would be over in a third of a second.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PressureChamber : MonoBehaviour
    {
        public enum State { Vacuum, Pressurizing, Pressurized, Venting }

        [Header("The volume (local to this object)")]
        public Vector3 size = new Vector3(3f, 3f, 4f);
        public Vector3 center = Vector3.zero;
        [Tooltip("Extra room around the box before you count as having left, in metres. Stops the cycle " +
                 "reversing every time you shuffle against a wall.")]
        [Min(0f)] public float exitMargin = 0.4f;

        [Header("Timing (real seconds, immune to sim fast-forward)")]
        [Min(0.5f)] public float fillSeconds = 5f;
        [Min(0.5f)] public float ventSeconds = 3.5f;
        [Tooltip("Wait this long after you step in before the gas starts, as if a hatch were sealing.")]
        [Min(0f)] public float sealDelay = 0.6f;

        [Header("Gas")]
        [Tooltip("Corner vents. They puff only while the pressure is CHANGING.")]
        public ParticleSystem[] jets;
        [Tooltip("The haze filling the room. Its emitter box rises with the pressure.")]
        public ParticleSystem fog;
        [Min(0f)] public float fogRateAtFull = 26f;

        [Header("Status light")]
        public Light statusLight;
        public Color vacuumColor = new Color(1f, 0.35f, 0.25f);
        public Color pressurizedColor = new Color(0.45f, 1f, 0.55f);
        [Min(0f)] public float lightIntensity = 2.5f;
        [Tooltip("Renderers whose emission colour follows the status light — wall lamps, gauge faces.")]
        public Renderer[] statusPanels;

        [Header("Audio (optional — drop clips in later)")]
        public AudioSource audioSource;
        [Tooltip("Looped while gas is moving, in or out.")]
        public AudioClip hissClip;
        public AudioClip sealedClip;

        [Header("Events")]
        public UnityEvent onPressurized;
        public UnityEvent onVented;

        [Header("Scene view")]
        public bool showGizmo = true;

        /// <summary>0 = vacuum, 1 = full pressure. Everything visual reads off this.</summary>
        public float Pressure01 { get; private set; }
        public State CurrentState { get; private set; } = State.Vacuum;
        public bool PlayerInside { get; private set; }

        AstronautController _astronaut;
        MaterialPropertyBlock _mpb;
        float _sealTimer;
        static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

        void Start()
        {
            ApplyVisuals();
            SetJets(false);
            if (fog != null) fog.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;

            bool wasInside = PlayerInside;
            PlayerInside = Contains(ProbePoint(), wasInside ? exitMargin : 0f);
            if (PlayerInside && !wasInside) _sealTimer = 0f;

            if (PlayerInside)
            {
                // The seal delay is charged before any gas moves, so stepping in and straight back out
                // does not leave a half-filled room behind.
                if (_sealTimer < sealDelay && Pressure01 <= 0f)
                {
                    _sealTimer += dt;
                }
                else if (Pressure01 < 1f)
                {
                    CurrentState = State.Pressurizing;
                    Pressure01 = Mathf.MoveTowards(Pressure01, 1f, dt / fillSeconds);
                    if (Pressure01 >= 1f)
                    {
                        CurrentState = State.Pressurized;
                        Play(sealedClip);
                        onPressurized?.Invoke();
                    }
                }
                else
                {
                    CurrentState = State.Pressurized;
                }
            }
            else if (Pressure01 > 0f)
            {
                CurrentState = State.Venting;
                Pressure01 = Mathf.MoveTowards(Pressure01, 0f, dt / ventSeconds);
                if (Pressure01 <= 0f)
                {
                    CurrentState = State.Vacuum;
                    onVented?.Invoke();
                }
            }
            else
            {
                CurrentState = State.Vacuum;
            }

            ApplyVisuals();
        }

        // ------------------------------------------------------------------ visuals

        void ApplyVisuals()
        {
            bool moving = CurrentState == State.Pressurizing || CurrentState == State.Venting;
            SetJets(moving);
            DriveFog();
            DriveLight();
            DriveHiss(moving);
        }

        void SetJets(bool on)
        {
            if (jets == null) return;
            foreach (ParticleSystem ps in jets)
            {
                if (ps == null) continue;
                if (on && !ps.isEmitting) ps.Play(true);
                else if (!on && ps.isEmitting) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }

        /// <summary>
        /// The fog's emitter box is resized and re-seated every frame so its TOP tracks the pressure: at
        /// 0.3 the gas occupies the bottom third of the room and nothing above it. Emitting into a
        /// full-height box at a low rate would look like thin fog everywhere, which reads as haze rather
        /// than as a room filling up.
        /// </summary>
        void DriveFog()
        {
            if (fog == null) return;

            float p = Pressure01;
            if (p <= 0.001f)
            {
                if (fog.isEmitting) fog.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                return;
            }

            if (!fog.isEmitting) fog.Play(true);

            ParticleSystem.EmissionModule emission = fog.emission;
            emission.rateOverTime = fogRateAtFull * Mathf.Max(0.15f, p);

            ParticleSystem.ShapeModule shape = fog.shape;
            float filled = Mathf.Max(0.05f, size.y * p);
            shape.scale = new Vector3(size.x * 0.85f, filled, size.z * 0.85f);
            // Seat the box on the floor: its centre rises as it grows, so the underside stays put.
            shape.position = new Vector3(center.x, center.y - size.y * 0.5f + filled * 0.5f, center.z);
        }

        void DriveLight()
        {
            Color c = Color.Lerp(vacuumColor, pressurizedColor, Pressure01);

            if (statusLight != null)
            {
                statusLight.color = c;
                // A pulse while gas moves, steady at either end — the eye reads the flicker as "working"
                // without needing a gauge to look at.
                bool moving = CurrentState == State.Pressurizing || CurrentState == State.Venting;
                float pulse = moving ? 0.72f + 0.28f * Mathf.Sin(Time.unscaledTime * 7f) : 1f;
                statusLight.intensity = lightIntensity * pulse;
            }

            if (statusPanels == null || statusPanels.Length == 0) return;
            _mpb ??= new MaterialPropertyBlock();
            foreach (Renderer r in statusPanels)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.SetColor(EmissionColorId, c * Mathf.LinearToGammaSpace(1.4f));
                r.SetPropertyBlock(_mpb);
            }
        }

        void DriveHiss(bool moving)
        {
            if (audioSource == null || hissClip == null) return;

            if (moving)
            {
                if (!audioSource.isPlaying)
                {
                    audioSource.clip = hissClip;
                    audioSource.loop = true;
                    audioSource.Play();
                }
            }
            else if (audioSource.isPlaying && audioSource.clip == hissClip)
            {
                audioSource.Stop();
            }
        }

        void Play(AudioClip clip)
        {
            if (audioSource != null && clip != null) audioSource.PlayOneShot(clip);
        }

        // ------------------------------------------------------------------ presence

        Vector3 ProbePoint()
        {
            if (_astronaut == null) _astronaut = FindAnyObjectByType<AstronautController>();
            if (_astronaut == null) return new Vector3(1e9f, 1e9f, 1e9f);   // nobody to detect
            return _astronaut.transform.position + Vector3.up * 0.4f;       // mid-body
        }

        public bool Contains(Vector3 world, float margin)
        {
            Vector3 local = transform.InverseTransformPoint(world) - center;
            Vector3 half = size * 0.5f + Vector3.one * margin;
            return Mathf.Abs(local.x) <= half.x &&
                   Mathf.Abs(local.y) <= half.y &&
                   Mathf.Abs(local.z) <= half.z;
        }

        // ------------------------------------------------------------------ scene view

        void OnDrawGizmos()
        {
            if (!showGizmo) return;

            Gizmos.matrix = transform.localToWorldMatrix;

            Gizmos.color = new Color(0.55f, 0.85f, 1f, 0.85f);
            Gizmos.DrawWireCube(center, size);

            // How full it is right now — the same box the fog emitter uses, so what you see in the scene
            // view is literally where the gas will be.
            float p = Application.isPlaying ? Pressure01 : 0.45f;
            float filled = Mathf.Max(0.02f, size.y * p);
            Gizmos.color = new Color(0.6f, 0.95f, 1f, 0.22f);
            Gizmos.DrawCube(new Vector3(center.x, center.y - size.y * 0.5f + filled * 0.5f, center.z),
                            new Vector3(size.x * 0.85f, filled, size.z * 0.85f));

            Gizmos.matrix = Matrix4x4.identity;

#if UNITY_EDITOR
            UnityEditor.Handles.color = new Color(0.55f, 0.85f, 1f);
            string label = Application.isPlaying
                ? $"{CurrentState} · {Pressure01 * 100f:0}%"
                : "gas fills in here (preview at 45%)";
            UnityEditor.Handles.Label(
                transform.TransformPoint(center + Vector3.up * (size.y * 0.5f + 0.3f)), label);
#endif
        }
    }
}
