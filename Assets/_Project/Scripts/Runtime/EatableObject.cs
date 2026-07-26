using UnityEngine;
using UnityEngine.Events;

namespace NasaSim
{
    /// <summary>
    /// Drop this on ANY object to make it edible: "[E] Eat apple". The astronaut's
    /// <see cref="HandActionController"/> runs the same reach / carry-to-visor / stepped-bite flourish for
    /// all of them — the object is carried in the hand and shrinks one step per bite, exactly the way the
    /// fruit on the trees does, because the fruit now uses this component too.
    ///
    /// Everything that differs per object lives here: how many bites, how fast, and what happens when it
    /// is gone (regrow after a delay, stay gone, or be destroyed). Nothing else needs wiring — the
    /// astronaut finds it through <see cref="InteractionSensor"/>, so all the object needs besides this
    /// component is a trigger collider on the Interactable layer (Tools &gt; NASA Sim &gt; Interactables &gt;
    /// Make Selected Eatable does both for you).
    ///
    /// UNSCALED time throughout: eating and regrowing are player-facing pacing, not part of the
    /// fast-forwarded mow (see <see cref="AstronautController"/> for the project convention).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EatableObject : MonoBehaviour, IInteractable
    {
        public enum WhenFinished
        {
            /// <summary>Hide it, then bring it back at its original pose after <see cref="respawnSeconds"/>.</summary>
            Respawn,
            /// <summary>Hide it for good.</summary>
            StayGone,
            /// <summary>Destroy the object.</summary>
            Destroy,
        }

        [Header("Prompt")]
        [Tooltip("Verb used in the prompt: \"[E] Eat apple\", \"[E] Pick fruit\".")]
        public string verb = "Eat";
        [Tooltip("Noun used in the prompt. Blank derives it from the object's name (\"Apple_02\" -> \"apple\").")]
        public string label = "";
        [Tooltip("Replaces the whole prompt line when set, e.g. \"[E] Grab a snack\".")]
        public string promptOverride = "";

        [Header("Eating")]
        [Tooltip("How many bites it takes. The object shrinks one step per bite and is gone on the last.")]
        [Min(1)] public int bites = 3;
        [Tooltip("Seconds per bite — lift to the visor, chomp, withdraw, chew.")]
        [Min(0.1f)] public float biteInterval = 0.5f;
        [Tooltip("The transform carried and shrunk. Leave empty to use this object. Point it at a visual " +
                 "child if the trigger collider should stay behind.")]
        public Transform body;
        [Tooltip("Roughly the object's radius, so the hand grips its near side instead of its centre. " +
                 "0 measures it from the renderers.")]
        [Min(0f)] public float grabRadius = 0f;

        [Header("When it's finished")]
        public WhenFinished whenFinished = WhenFinished.Respawn;
        [Min(0f)] public float respawnSeconds = 30f;
        [Tooltip("Scale-up ease when it comes back.")]
        [Min(0.01f)] public float growInSeconds = 1.2f;

        [Header("Feedback (all optional)")]
        public AudioSource audioSource;
        public AudioClip biteClip;
        [Tooltip("Colour of the crumbs puffed out on each bite. Alpha 0 samples the object's own material.")]
        public Color debrisColor = new Color(0f, 0f, 0f, 0f);
        [Tooltip("Fired on every bite.")]
        public UnityEvent onBite;
        [Tooltip("Fired once, on the last bite.")]
        public UnityEvent onEaten;

        /// <summary>The transform actually carried and shrunk.</summary>
        public Transform Body => body != null ? body : transform;

        /// <summary>Available to be picked up: not eaten/regrowing, and not already in a hand.</summary>
        public bool CanBeEaten => isActiveAndEnabled && !_hidden && !_carried;

        /// <summary>The object's visual centre — where the hand reaches to grab it, whatever its pivot.</summary>
        public Vector3 GrabPosition => Body.TransformPoint(_centerLocal);

        /// <summary>How far short of <see cref="GrabPosition"/> the wrist stops, so it sits in the palm.</summary>
        public float GrabRadius => grabRadius > 0f ? grabRadius : _measuredRadius;

        public Color DebrisColor => debrisColor.a > 0f ? debrisColor : _sampledColor;

        public string Prompt => !string.IsNullOrEmpty(promptOverride) ? promptOverride : $"[E] {verb} {Label}";

        string Label => !string.IsNullOrEmpty(label) ? label : _derivedLabel;

        bool _captured;
        bool _carried;
        bool _hidden;
        float _respawnTimer;
        float _growIn = 1f;
        Vector3 _restLocalPos;
        Quaternion _restLocalRot;
        Vector3 _restLocalScale;
        Vector3 _centerLocal;
        float _measuredRadius = 0.05f;
        Color _sampledColor = new Color(0.95f, 0.85f, 0.7f);
        string _derivedLabel = "snack";
        Rigidbody _rigidbody;
        bool _wasKinematic;
        Renderer[] _renderers = new Renderer[0];
        Collider[] _colliders = new Collider[0];
        bool[] _colliderEnabled = new bool[0];

        void Awake() => CaptureRestPose();

        /// <summary>
        /// Remember the pose, size and colour the object should return to. Called on Awake, and again by
        /// <see cref="FruitSpawner"/> once it has finished building a fruit around this component.
        /// </summary>
        public void CaptureRestPose()
        {
            Transform t = Body;
            _restLocalPos = t.localPosition;
            _restLocalRot = t.localRotation;
            _restLocalScale = t.localScale;
            _derivedLabel = PrettyName(gameObject.name);
            _rigidbody = GetComponentInChildren<Rigidbody>();

            // Cached so hiding, restoring and carrying never fight over what a designer had switched off.
            _renderers = t.GetComponentsInChildren<Renderer>(includeInactive: true);
            _colliders = GetComponentsInChildren<Collider>(includeInactive: true);
            _colliderEnabled = new bool[_colliders.Length];
            for (int i = 0; i < _colliders.Length; i++) _colliderEnabled[i] = _colliders[i].enabled;

            Measure();
            _captured = true;
        }

        void Measure()
        {
            Transform t = Body;
            _centerLocal = Vector3.zero;
            if (_renderers.Length == 0) return;

            Bounds b = _renderers[0].bounds;
            for (int i = 1; i < _renderers.Length; i++) b.Encapsulate(_renderers[i].bounds);
            _centerLocal = t.InverseTransformPoint(b.center);
            _measuredRadius = Mathf.Max(0.01f, Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) * 0.6f);

            var mat = _renderers[0].sharedMaterial;
            if (mat == null) return;
            if (mat.HasProperty("_BaseColor")) _sampledColor = mat.GetColor("_BaseColor");
            else if (mat.HasProperty("_Color")) _sampledColor = mat.GetColor("_Color");
            _sampledColor.a = 1f;
        }

        // ---------------------------------------------------------------- interaction

        public bool CanInteract(InteractionSensor sensor)
        {
            if (!CanBeEaten) return false;
            var hand = sensor.GetComponentInParent<HandActionController>();
            return hand != null && !hand.IsBusy;
        }

        public void Interact(InteractionSensor sensor)
        {
            var hand = sensor.GetComponentInParent<HandActionController>();
            if (hand != null) hand.BeginEat(this);
        }

        // ---------------------------------------------------------------- driven by HandActionController

        /// <summary>Taken out of the world and into the hand: physics off, prompt gone.</summary>
        public void BeginCarry()
        {
            if (!_captured) CaptureRestPose();
            _carried = true;
            if (_rigidbody != null)
            {
                _wasKinematic = _rigidbody.isKinematic;
                _rigidbody.isKinematic = true;
            }
            // A snack held at the visor must not shove the CharacterController around, or be re-detected
            // by the sensor while it is in the hand.
            SetCollidersEnabled(false);
        }

        /// <summary>Put the object's visual centre on the hand's grip point, at this bite's size.</summary>
        public void SetCarryPose(Vector3 worldCenter, Quaternion rotation, float scaleMultiplier)
        {
            Transform t = Body;
            t.localScale = _restLocalScale * Mathf.Max(0f, scaleMultiplier);
            t.rotation = rotation;
            // Offset by the pivot->centre vector, so an off-centre pivot still sits IN the hand.
            t.position = worldCenter - t.TransformVector(_centerLocal);
        }

        public void NotifyBite(int biteIndex, int totalBites)
        {
            onBite?.Invoke();
            if (biteIndex >= totalBites) onEaten?.Invoke();
        }

        /// <summary>The last bite has landed: hide it and start the regrow, or destroy it outright.</summary>
        public void Consume()
        {
            _carried = false;
            RestorePose();
            if (whenFinished == WhenFinished.Destroy)
            {
                Destroy(gameObject);       // the whole interactable, trigger and all
                return;
            }
            SetVisible(false);
            _hidden = true;
            _respawnTimer = whenFinished == WhenFinished.Respawn ? Mathf.Max(0.01f, respawnSeconds) : -1f;
        }

        /// <summary>Interrupted (the astronaut was disabled, the scene reset): put it back untouched.</summary>
        public void CancelCarry()
        {
            if (!_carried) return;
            _carried = false;
            RestorePose();
        }

        void RestorePose()
        {
            Transform t = Body;
            t.localPosition = _restLocalPos;
            t.localRotation = _restLocalRot;
            t.localScale = _restLocalScale;
            if (_rigidbody != null) _rigidbody.isKinematic = _wasKinematic;
            SetCollidersEnabled(true);
        }

        // ---------------------------------------------------------------- regrow

        void Update()
        {
            if (_carried) return;
            float dt = Time.unscaledDeltaTime;

            if (_respawnTimer > 0f)
            {
                _respawnTimer -= dt;
                if (_respawnTimer <= 0f)
                {
                    _hidden = false;
                    _growIn = 0f;
                    SetVisible(true);
                }
            }

            if (_growIn < 1f)
            {
                _growIn = Mathf.Min(1f, _growIn + dt / growInSeconds);
                // Overshoot slightly, then settle, so it pops back rather than inflating.
                float s = _growIn < 0.8f ? Mathf.Lerp(0.1f, 1.1f, _growIn / 0.8f)
                                         : Mathf.Lerp(1.1f, 1f, (_growIn - 0.8f) / 0.2f);
                Body.localScale = _restLocalScale * s;
            }
        }

        /// <summary>
        /// Hide the object without deactivating it — the component has to keep ticking to run its own
        /// regrow, and the trigger has to stop being found by the sensor while it is gone.
        /// </summary>
        void SetVisible(bool visible)
        {
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null) _renderers[i].enabled = visible;
            SetCollidersEnabled(visible);
        }

        /// <summary>Restores each collider to the state it was authored in, never blanket-enables them.</summary>
        void SetCollidersEnabled(bool enable)
        {
            for (int i = 0; i < _colliders.Length; i++)
                if (_colliders[i] != null) _colliders[i].enabled = enable && _colliderEnabled[i];
        }

        /// <summary>"PH_Apple_02" -> "apple". Keeps the prompt readable without asking for a label.</summary>
        static string PrettyName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "snack";
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
            return s.Length == 0 ? "snack" : s.ToLowerInvariant();
        }
    }
}
