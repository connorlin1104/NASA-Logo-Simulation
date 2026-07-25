using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// Reports whether the astronaut stands inside this BoxCollider volume. Deliberately POLLED (a
    /// local-space point test) rather than OnTriggerEnter/Exit: CharacterController trigger callbacks
    /// only fire while the controller moves, so a player standing still — or teleported — can be missed
    /// by events but never by a per-frame containment test.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    public sealed class AirlockChamberSensor : MonoBehaviour
    {
        BoxCollider _box;
        AstronautController _astronaut;

        public bool PlayerInside
        {
            get
            {
                if (_box == null) _box = GetComponent<BoxCollider>();
                if (_astronaut == null) _astronaut = FindAnyObjectByType<AstronautController>();
                if (_box == null || _astronaut == null) return false;

                Vector3 probe = _astronaut.transform.position + Vector3.up * 0.4f;   // mid-body
                Vector3 local = transform.InverseTransformPoint(probe) - _box.center;
                Vector3 half = _box.size * 0.5f;
                return Mathf.Abs(local.x) <= half.x &&
                       Mathf.Abs(local.y) <= half.y &&
                       Mathf.Abs(local.z) <= half.z;
            }
        }
    }

    /// <summary>
    /// The pressure-chamber sequence, entirely on UNSCALED time (at 16x sim speed the airlock must
    /// still take its real seconds):
    ///
    /// Sealed → [button] entry door opens → player steps into the chamber → entry door closes behind
    /// them (automatic) → gas vents flood the chamber for <see cref="pressurizeSeconds"/> → exit door
    /// opens → player walks out → exit door closes → Sealed.
    ///
    /// The same machine runs both directions: the outer button starts an ingress cycle (entry = outer
    /// door), the inner button an egress cycle (entry = inner door). If the player never enters, the
    /// entry door gives up after <see cref="awaitEntryTimeout"/> and re-seals.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AirlockController : MonoBehaviour
    {
        public enum State { Sealed, EntryOpening, AwaitEntry, EntryClosing, Pressurizing, ExitOpening, AwaitExit, ExitClosing }

        [Header("Parts (wired by the airlock builder tool)")]
        public SimpleDoor outerDoor;
        public SimpleDoor innerDoor;
        public AirlockChamberSensor chamber;
        [Tooltip("Vent systems played during pressurization. Each must have main.useUnscaledTime = true " +
                 "(the builder tool sets it).")]
        public ParticleSystem[] gasVents;

        [Header("Timing (real seconds, immune to sim fast-forward)")]
        [Min(0.5f)] public float pressurizeSeconds = 4f;
        [Tooltip("Vents stop this long before the end so the last puffs fade out as the door opens.")]
        [Min(0f)] public float gasTailSeconds = 1f;
        [Tooltip("If nobody enters the chamber, the entry door gives up and re-seals after this long.")]
        [Min(2f)] public float awaitEntryTimeout = 20f;
        [Tooltip("Pause after the player leaves the chamber before the exit door closes behind them.")]
        [Min(0f)] public float exitCloseDelay = 1.5f;

        [Header("Audio hooks (optional — drop clips in later)")]
        public AudioSource audioSource;
        [Tooltip("Looped while the chamber pressurizes.")]
        public AudioClip hissClip;

        [Header("Events")]
        public UnityEvent onPressurizeStarted;
        public UnityEvent onPressurizeFinished;

        public State CurrentState { get; private set; } = State.Sealed;
        /// <summary>Buttons may start a cycle only while sealed.</summary>
        public bool CanRequest => CurrentState == State.Sealed && outerDoor != null && innerDoor != null;

        SimpleDoor _entryDoor;   // the door the player comes IN through this cycle
        SimpleDoor _exitDoor;
        float _timer;
        bool _aborting;

        /// <summary>Start a cycle. <paramref name="fromOutside"/>: true = outer button (ingress).</summary>
        public bool RequestCycle(bool fromOutside)
        {
            if (!CanRequest) return false;

            _entryDoor = fromOutside ? outerDoor : innerDoor;
            _exitDoor = fromOutside ? innerDoor : outerDoor;
            _aborting = false;
            _entryDoor.Open();
            CurrentState = State.EntryOpening;
            return true;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;

            switch (CurrentState)
            {
                case State.EntryOpening:
                    if (_entryDoor.IsOpen)
                    {
                        _timer = 0f;
                        CurrentState = State.AwaitEntry;
                    }
                    break;

                case State.AwaitEntry:
                    _timer += dt;
                    if (chamber != null && chamber.PlayerInside)
                    {
                        _entryDoor.Close();
                        CurrentState = State.EntryClosing;
                    }
                    else if (_timer >= awaitEntryTimeout)
                    {
                        _aborting = true;
                        _entryDoor.Close();
                        CurrentState = State.EntryClosing;
                    }
                    break;

                case State.EntryClosing:
                    // A player who slips in while an abort is closing the door must not be entombed
                    // between two sealed doors: they're inside now, so run the cycle for them after all.
                    if (_aborting && chamber != null && chamber.PlayerInside)
                        _aborting = false;
                    if (_entryDoor.IsClosed)
                    {
                        if (_aborting)
                        {
                            CurrentState = State.Sealed;
                        }
                        else
                        {
                            _timer = 0f;
                            SetGas(true);
                            onPressurizeStarted?.Invoke();
                            CurrentState = State.Pressurizing;
                        }
                    }
                    break;

                case State.Pressurizing:
                    _timer += dt;
                    if (_timer >= pressurizeSeconds - gasTailSeconds)
                        SetGas(false);                          // last puffs fade while the timer runs out
                    if (_timer >= pressurizeSeconds)
                    {
                        StopHiss();
                        onPressurizeFinished?.Invoke();
                        _exitDoor.Open();
                        CurrentState = State.ExitOpening;
                    }
                    break;

                case State.ExitOpening:
                    if (_exitDoor.IsOpen)
                    {
                        _timer = 0f;
                        CurrentState = State.AwaitExit;
                    }
                    break;

                case State.AwaitExit:
                    // Wait for the player to actually leave, then a courteous pause before sealing.
                    if (chamber == null || !chamber.PlayerInside)
                    {
                        _timer += dt;
                        if (_timer >= exitCloseDelay)
                        {
                            _exitDoor.Close();
                            CurrentState = State.ExitClosing;
                        }
                    }
                    else
                    {
                        _timer = 0f;
                    }
                    break;

                case State.ExitClosing:
                    // Don't shut the door on a player who steps back in mid-close.
                    if (chamber != null && chamber.PlayerInside)
                    {
                        _exitDoor.Open();
                        CurrentState = State.ExitOpening;
                    }
                    else if (_exitDoor.IsClosed)
                    {
                        CurrentState = State.Sealed;
                    }
                    break;
            }
        }

        void SetGas(bool on)
        {
            if (gasVents != null)
            {
                foreach (var ps in gasVents)
                {
                    if (ps == null) continue;
                    if (on && !ps.isPlaying) ps.Play();
                    else if (!on && ps.isPlaying) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            if (on && audioSource != null && hissClip != null)
            {
                audioSource.clip = hissClip;
                audioSource.loop = true;
                audioSource.Play();
            }
        }

        void StopHiss()
        {
            if (audioSource != null && audioSource.isPlaying && audioSource.clip == hissClip)
                audioSource.Stop();
        }
    }
}
