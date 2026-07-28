using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// A fountain: jets that arc up and fall back, a sheet spilling off the tier, splash and ripples
    /// where the water lands, and a haze around it all. One number drives every part of it —
    /// <see cref="flow"/> — so it can be dialled down to a trickle, switched off, or handed to a button.
    ///
    /// <b>The rates are serialized, not captured at Awake.</b> Each emitter carries the rate it should
    /// run at when the fountain is at full flow, written once by the builder tool. Reading it off the
    /// ParticleSystem at startup instead would adopt whatever an editor preview happened to leave behind,
    /// and the fountain would drift a little quieter every time the scene was rebuilt — the same reason
    /// the doors and the drone store their rest poses rather than measuring them.
    ///
    /// <b>What makes it read as water rather than as sparks</b> is set up in the builder, not here:
    /// stretched billboards scaled by velocity (so a droplet is a streak, not a dot), a gravity modifier
    /// near 1, and a lifetime computed from the ballistics so the arc lands in the pool instead of
    /// vanishing in mid-air. See FountainTool.
    ///
    /// Unscaled time throughout: the sim fast-forwards the mow to 16x, and a fountain that tracked it
    /// would fire like a pressure washer (see <see cref="AstronautController"/> for the convention).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class Fountain : MonoBehaviour, IInteractable
    {
        [Serializable]
        public sealed class Emitter
        {
            public ParticleSystem system;
            [Tooltip("Particles per second when the fountain is running at full flow.")]
            [Min(0f)] public float rateAtFullFlow = 60f;
            [Tooltip("Off for something that should keep running whatever the flow is — a permanent haze.")]
            public bool followsFlow = true;
        }

        [Header("Flow")]
        [Tooltip("How hard it runs, 0 to 1. Everything else is scaled from this.")]
        [Range(0f, 1f)] public float flow = 1f;
        [Tooltip("Seconds to spin up or wind down. A fountain has pipes; it does not snap on.")]
        [Min(0.05f)] public float spinUpSeconds = 1.4f;
        public bool startsOn = true;

        [Header("Parts")]
        public List<Emitter> emitters = new List<Emitter>();

        [Header("Extras (all optional)")]
        [Tooltip("Underwater glow. Its intensity follows the flow.")]
        public Light glow;
        [Min(0f)] public float glowIntensity = 1.6f;
        public AudioSource audioSource;
        [Tooltip("Looped while the water is running. Its volume follows the flow.")]
        public AudioClip runningClip;
        [Range(0f, 1f)] public float volume = 0.55f;

        [Header("Interaction")]
        [Tooltip("Adds an [E] prompt that turns it on and off. Needs a trigger collider on the " +
                 "Interactable layer — the builder adds one when this is ticked.")]
        public bool interactable;
        public string label = "fountain";

        [Header("Events")]
        public UnityEvent onTurnedOn;
        public UnityEvent onTurnedOff;

        /// <summary>What the emitters are actually running at right now, 0 to 1, spin-up included.</summary>
        public float CurrentFlow => _current;

        public bool IsRunning => _want;

        public string Prompt => interactable
            ? (_want ? $"[E] Turn the {label} off" : $"[E] Turn the {label} on")
            : string.Empty;

        bool _want;
        float _current;
        bool _started;
        float _applied = float.NaN;

        void OnEnable()
        {
            if (!_started) { _want = startsOn; _current = startsOn ? flow : 0f; _started = true; }
            _applied = float.NaN;
            Apply(_current);
        }

        void Update()
        {
            float target = _want ? flow : 0f;
            if (!Mathf.Approximately(_current, target))
                _current = Mathf.MoveTowards(_current, target,
                                             Time.unscaledDeltaTime / Mathf.Max(0.05f, spinUpSeconds));

            // Only when something actually moved. In edit mode this matters for more than performance:
            // writing to the emitters every editor tick would leave the scene permanently dirty, so a
            // fountain sitting in the corner would make it look like you had unsaved changes forever.
            if (!Mathf.Approximately(_applied, _current)) Apply(_current);
        }

        // Deliberately does NOT drive the emitters itself: OnValidate runs mid-deserialisation, and
        // starting or stopping a ParticleSystem there is exactly the kind of call Unity refuses. It just
        // invalidates the applied value, so the next Update pushes the change through once.
        void OnValidate()
        {
            if (!_started) _current = startsOn ? flow : 0f;
            _applied = float.NaN;
        }

        // ---------------------------------------------------------------- control

        /// <summary>
        /// Push the current flow at the emitters on the next tick regardless of whether it changed.
        /// The builder needs this: it adds this component and then fills <see cref="emitters"/>, so the
        /// Apply that ran on OnEnable saw an empty list and the jets would sit there never started.
        /// </summary>
        public void Refresh() => _applied = float.NaN;

        public void Toggle() => SetRunning(!_want);

        public void TurnOn() => SetRunning(true);
        public void TurnOff() => SetRunning(false);

        public void SetRunning(bool on)
        {
            if (_want == on) return;
            _want = on;
            _started = true;
            if (on) onTurnedOn?.Invoke(); else onTurnedOff?.Invoke();
        }

        // ---------------------------------------------------------------- drive

        void Apply(float f)
        {
            _applied = f;

            for (int i = 0; i < emitters.Count; i++)
            {
                Emitter e = emitters[i];
                if (e == null || e.system == null) continue;

                float rate = e.followsFlow ? e.rateAtFullFlow * f : e.rateAtFullFlow;

                ParticleSystem.EmissionModule emission = e.system.emission;
                emission.rateOverTime = rate;

                // Stop rather than emit at zero: a system left playing at rate 0 still ticks its whole
                // particle buffer every frame for nothing.
                if (rate <= 0.01f)
                {
                    if (e.system.isEmitting)
                        e.system.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                else if (!e.system.isEmitting)
                {
                    e.system.Play(true);
                }
            }

            if (glow != null) glow.intensity = glowIntensity * f;

            if (audioSource != null && runningClip != null)
            {
                audioSource.volume = volume * f;
                if (f > 0.02f && !audioSource.isPlaying)
                {
                    audioSource.clip = runningClip;
                    audioSource.loop = true;
                    audioSource.Play();
                }
                else if (f <= 0.02f && audioSource.isPlaying)
                {
                    audioSource.Stop();
                }
            }
        }

        // ---------------------------------------------------------------- interaction

        public bool CanInteract(InteractionSensor sensor) => interactable && isActiveAndEnabled;

        public void Interact(InteractionSensor sensor) => Toggle();

        // ---------------------------------------------------------------- scene view

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.9f);
            foreach (Emitter e in emitters)
            {
                if (e?.system == null) continue;
                Vector3 p = e.system.transform.position;
                Gizmos.DrawWireSphere(p, 0.08f);
                Gizmos.DrawLine(p, p + e.system.transform.forward * 0.5f);
            }
        }
    }
}
