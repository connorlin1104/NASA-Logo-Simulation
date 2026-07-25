using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// The pick-and-eat flourish, on the astronaut root. Phases (all UNSCALED time, ~2.7 s total):
    /// reach (the right arm blends toward the fruit via <see cref="AstronautLocomotionVisual.SetReach"/>)
    /// → the fruit arcs to just in front of the visor → three stepped bites, each shrinking the fruit
    /// and puffing crumb particles → the arm settles back and the spawner is told to regrow.
    ///
    /// Movement stays enabled — it is a short flourish, and in first person the arm+fruit read clearly
    /// in the lower frame either way.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FruitEatController : MonoBehaviour
    {
        public AstronautController astronaut;
        public AstronautLocomotionVisual locomotion;
        [Tooltip("The Head anchor; the fruit is eaten just in front of it (or of the camera when found).")]
        public Transform headAnchor;

        [Header("Timing (real seconds)")]
        [Min(0.1f)] public float reachSeconds = 0.4f;
        [Min(0.1f)] public float bringSeconds = 0.4f;
        [Min(0.05f)] public float biteInterval = 0.5f;
        [Min(1)] public int bites = 3;
        [Min(0.1f)] public float settleSeconds = 0.35f;

        [Header("Audio hook (optional — drop a clip in later)")]
        public AudioSource audioSource;
        public AudioClip biteClip;

        enum Phase { Idle, Reach, Bring, Bites, Settle }

        public bool IsBusy => _phase != Phase.Idle;

        Phase _phase = Phase.Idle;
        FruitInteractable _fruit;
        Transform _fruitT;
        Vector3 _startPos;
        Quaternion _startRot;
        Vector3 _startScale;
        float _t;
        int _bitesDone;
        ParticleSystem _crumbs;
        Camera _cam;

        public bool BeginEat(FruitInteractable fruit)
        {
            if (IsBusy || fruit == null) return false;
            _fruit = fruit;
            _fruitT = fruit.transform;
            _startPos = _fruitT.position;
            _startRot = _fruitT.rotation;
            _startScale = _fruitT.localScale;
            _phase = Phase.Reach;
            _t = 0f;
            _bitesDone = 0;
            return true;
        }

        void Update()
        {
            if (_phase == Phase.Idle) return;
            float dt = Time.unscaledDeltaTime;

            // The fruit may vanish under us (scene reset); bail out cleanly.
            if (_fruitT == null && _phase != Phase.Settle)
            {
                _phase = Phase.Settle;
                _t = 0f;
            }

            switch (_phase)
            {
                case Phase.Reach:
                {
                    _t += dt / reachSeconds;
                    locomotion?.SetReach(_fruitT.position, Mathf.Clamp01(_t));
                    if (_t >= 1f) { _phase = Phase.Bring; _t = 0f; }
                    break;
                }
                case Phase.Bring:
                {
                    _t += dt / bringSeconds;
                    float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_t));
                    Vector3 target = MouthPos();
                    // A little upward arc so the grab reads as a toss toward the visor, not a slide.
                    _fruitT.position = Vector3.Lerp(_startPos, target, k)
                                       + Vector3.up * (Mathf.Sin(k * Mathf.PI) * 0.12f);
                    _fruitT.localScale = _startScale * Mathf.Lerp(1f, 0.85f, k);
                    locomotion?.SetReach(_fruitT.position, 1f);
                    if (_t >= 1f) { _phase = Phase.Bites; _t = 0f; }
                    break;
                }
                case Phase.Bites:
                {
                    _fruitT.position = MouthPos();
                    locomotion?.SetReach(_fruitT.position, 1f);
                    _t += dt;
                    if (_t >= biteInterval)
                    {
                        _t = 0f;
                        _bitesDone++;
                        float remain = 1f - _bitesDone / (float)bites;
                        _fruitT.localScale = _startScale * (0.85f * Mathf.Max(0.001f, remain));
                        EmitCrumbs(_fruitT.position);
                        if (audioSource != null && biteClip != null) audioSource.PlayOneShot(biteClip);
                        if (_bitesDone >= bites)
                        {
                            FinishFruit();
                            _phase = Phase.Settle;
                        }
                    }
                    break;
                }
                case Phase.Settle:
                {
                    _t += dt / settleSeconds;
                    locomotion?.SetReach(MouthPos(), 1f - Mathf.Clamp01(_t));
                    if (_t >= 1f)
                    {
                        locomotion?.SetReach(Vector3.zero, 0f);
                        _phase = Phase.Idle;
                    }
                    break;
                }
            }
        }

        void FinishFruit()
        {
            if (_fruitT != null)
            {
                // Hand the fruit back to its marker pose before the spawner hides it for regrow.
                _fruitT.SetPositionAndRotation(_startPos, _startRot);
                _fruitT.localScale = _startScale;
            }
            if (_fruit != null && _fruit.spawner != null) _fruit.spawner.NotifyEaten(_fruit);
            _fruit = null;
            _fruitT = null;
        }

        Vector3 MouthPos()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam != null)
                return _cam.transform.position + _cam.transform.forward * 0.35f - _cam.transform.up * 0.08f;
            Transform head = headAnchor != null ? headAnchor : transform;
            return head.position + head.forward * 0.3f;
        }

        void EmitCrumbs(Vector3 at)
        {
            if (_crumbs == null) _crumbs = BuildCrumbSystem();
            var p = new ParticleSystem.EmitParams { position = at };
            _crumbs.Emit(p, 6);
        }

        // Built in code (no particle assets in the project): tiny pale bits that pop off each bite and
        // fall under lunar-ish gravity.
        ParticleSystem BuildCrumbSystem()
        {
            var go = new GameObject("BiteCrumbs");
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.useUnscaledTime = true;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = 1.1f;
            main.startSpeed = 0.7f;
            main.startSize = 0.025f;
            main.gravityModifier = 0.35f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 64;
            var emission = ps.emission;
            emission.rateOverTime = 0f;              // Emit() only
            var psr = go.GetComponent<ParticleSystemRenderer>();
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(sh) { name = "Crumbs (runtime)" };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", new Color(0.95f, 0.85f, 0.7f));
            psr.material = mat;
            psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }
    }
}
