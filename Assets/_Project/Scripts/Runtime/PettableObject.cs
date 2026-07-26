using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// Drop this on ANY object to make it pattable: "[E] Pet duck". The astronaut's
    /// <see cref="HandActionController"/> runs the same reach / stroke / stroke / stroke flourish for all
    /// of them — the hand comes up into the visor, leans toward whatever is being patted, and lands a few
    /// strokes on it, each one squashing the object a little so the contact reads.
    ///
    /// The ducks use this (their <see cref="WaterWanderer"/> is wired into <see cref="wanderer"/>, so a
    /// pat still turns them to face you and quacks), but nothing here is duck-specific: an object only
    /// needs this component and a trigger collider on the Interactable layer (Tools &gt; NASA Sim &gt;
    /// Interactables &gt; Make Selected Pettable does both for you).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PettableObject : MonoBehaviour, IInteractable
    {
        [Header("Prompt")]
        [Tooltip("Verb used in the prompt: \"[E] Pet duck\", \"[E] Pat rover\".")]
        public string verb = "Pet";
        [Tooltip("Noun used in the prompt. Blank derives it from the object's name (\"Duck_01\" -> \"duck\").")]
        public string label = "";
        [Tooltip("Replaces the whole prompt line when set, e.g. \"[E] Say hello\".")]
        public string promptOverride = "";

        [Header("Petting")]
        [Tooltip("How many strokes one interaction lands.")]
        [Min(1)] public int pats = 3;
        [Tooltip("Seconds per stroke — lift, drop onto the animal, ease back up.")]
        [Min(0.1f)] public float patInterval = 0.42f;
        [Tooltip("Where the hand aims. Leave empty to use the renderers' centre — usually the animal's back.")]
        public Transform petAnchor;

        [Header("Reaction")]
        [Tooltip("Squash the object under each stroke, so the pat visibly lands.")]
        public bool squashOnPat = true;
        [Range(0f, 0.5f)] public float squashAmount = 0.14f;
        [Min(0.05f)] public float squashRecoverSeconds = 0.3f;
        [Tooltip("The transform squashed. Leave empty to use the first child that renders anything, so " +
                 "the collider and any wander script on the root are left alone.")]
        public Transform squashBody;
        [Tooltip("Optional. A duck's wanderer, so a pat also turns it to face you, wiggles and quacks.")]
        public WaterWanderer wanderer;
        public AudioSource audioSource;
        public AudioClip patClip;

        [Header("Hooks")]
        [Tooltip("Fired on every stroke.")]
        public UnityEvent onPat;
        [Tooltip("Fired once, when the last stroke has landed.")]
        public UnityEvent onPetted;

        /// <summary>Where the hand aims — the anchor if one is set, else the visual centre.</summary>
        public Vector3 PetPosition => petAnchor != null ? petAnchor.position : transform.TransformPoint(_centerLocal);

        /// <summary>Roughly the object's radius, so the hand rests on top of it rather than inside it.</summary>
        public float PetRadius => _radius;

        public bool IsBeingPetted => _petting;

        public string Prompt => !string.IsNullOrEmpty(promptOverride) ? promptOverride : $"[E] {verb} {Label}";

        string Label => !string.IsNullOrEmpty(label) ? label : _derivedLabel;

        Vector3 _centerLocal;
        float _radius = 0.2f;
        string _derivedLabel = "critter";
        Transform _squash;
        Vector3 _squashRest = Vector3.one;
        float _squashAmount;
        bool _petting;

        void Awake()
        {
            if (wanderer == null) wanderer = GetComponent<WaterWanderer>();
            _derivedLabel = PrettyName(gameObject.name);
            _squash = squashBody != null ? squashBody : ResolveVisualChild();
            if (_squash != null) _squashRest = _squash.localScale;
            Measure();
        }

        void Measure()
        {
            var rends = GetComponentsInChildren<Renderer>(includeInactive: true);
            if (rends.Length == 0) { _centerLocal = Vector3.up * 0.2f; return; }
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            _centerLocal = transform.InverseTransformPoint(b.center);
            _radius = Mathf.Max(0.02f, Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)));
        }

        /// <summary>The visual child (the PH_ placeholder, or a swapped-in model) — safe to scale.</summary>
        Transform ResolveVisualChild()
        {
            foreach (Transform child in transform)
                if (child.GetComponentInChildren<Renderer>(includeInactive: true) != null) return child;
            return null;
        }

        // ---------------------------------------------------------------- interaction

        public bool CanInteract(InteractionSensor sensor)
        {
            if (!isActiveAndEnabled || _petting) return false;
            // A duck that is still reacting to the last pat is left alone until it settles.
            if (wanderer != null && wanderer.IsReacting) return false;
            var hand = sensor.GetComponentInParent<HandActionController>();
            return hand != null && !hand.IsBusy;
        }

        public void Interact(InteractionSensor sensor)
        {
            var hand = sensor.GetComponentInParent<HandActionController>();
            if (hand != null) hand.BeginPet(this);
        }

        // ---------------------------------------------------------------- driven by HandActionController

        public void BeginPet() => _petting = true;

        /// <summary>One stroke has landed.</summary>
        public void Pat(Vector3 fromWorld, int patIndex, int totalPats)
        {
            if (squashOnPat) _squashAmount = squashAmount;
            if (wanderer != null) wanderer.ReactToPat(fromWorld);
            if (audioSource != null && patClip != null) audioSource.PlayOneShot(patClip);
            onPat?.Invoke();
            if (patIndex >= totalPats) onPetted?.Invoke();
        }

        public void EndPet() => _petting = false;

        void Update()
        {
            if (_squash == null) return;
            if (_squashAmount <= 0.0005f)
            {
                if (_squash.localScale != _squashRest) _squash.localScale = _squashRest;
                return;
            }

            _squashAmount = Mathf.Lerp(_squashAmount, 0f,
                                       1f - Mathf.Exp(-Time.unscaledDeltaTime / (squashRecoverSeconds * 0.35f)));
            // Squash down, bulge out: volume-preserving enough to read as a soft, happy animal.
            float a = _squashAmount;
            _squash.localScale = Vector3.Scale(_squashRest, new Vector3(1f + a * 0.5f, 1f - a, 1f + a * 0.5f));
        }

        /// <summary>"Duck_01" -> "duck". Keeps the prompt readable without asking for a label.</summary>
        static string PrettyName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "critter";
            string s = raw;
            if (s.StartsWith("PH_")) s = s.Substring(3);
            int u = s.LastIndexOf('_');
            if (u > 0 && u < s.Length - 1)
            {
                bool digits = true;
                for (int i = u + 1; i < s.Length; i++) if (!char.IsDigit(s[i])) { digits = false; break; }
                if (digits) s = s.Substring(0, u);
            }
            s = s.Replace('_', ' ').Trim();
            return s.Length == 0 ? "critter" : s.ToLowerInvariant();
        }
    }
}
