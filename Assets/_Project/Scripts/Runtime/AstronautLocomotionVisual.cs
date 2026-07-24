using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Makes the astronaut's arms (and legs) swing while walking. This is the component designed to
    /// survive the FBX swap: it picks its strategy at runtime, so the same setup works for the primitive
    /// placeholder today and a real rigged model tomorrow, with no re-wiring.
    ///
    /// <b>Animator path</b> - if the imported FBX brought its own walk clip (e.g. a Mixamo download) and
    /// an Animator Controller is assigned, this just feeds <c>Speed</c>/<c>IsMoving</c> parameters and
    /// lets the real animation drive the arms.
    ///
    /// <b>Procedural path</b> - otherwise it swings bone transforms directly. Bones are found in three
    /// escalating ways: a Humanoid avatar (no naming convention needed at all), then a name search
    /// covering the common spellings, then whatever is assigned in the Inspector.
    ///
    /// Uses <c>Time.unscaledDeltaTime</c> - see <see cref="AstronautController"/> for why.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AstronautLocomotionVisual : MonoBehaviour
    {
        public enum DriveMode { Auto, ForceProcedural, ForceAnimator }

        [Header("Mode")]
        [Tooltip("Auto: use the Animator if one with a controller exists, else swing bones procedurally.")]
        public DriveMode driveMode = DriveMode.Auto;

        [Header("Source")]
        public AstronautController controller;
        [Tooltip("Optional. Assigned automatically from this object or its children on the FBX swap.")]
        public Animator animator;

        [Header("Bones (leave empty to auto-detect)")]
        [Tooltip("Auto-detection order: Humanoid avatar bones -> name search -> these fields.")]
        public Transform leftArm;
        public Transform rightArm;
        public Transform leftForearm;
        public Transform rightForearm;
        public Transform leftLeg;
        public Transform rightLeg;

        // NOTE: AstronautSetup ("Add Astronaut & Balcony To Scene") re-applies the moon-walk feel to the
        // scene's component each run; the defaults here match so a fresh component looks the same.

        [Header("Procedural swing")]
        [Tooltip("Peak arm swing in degrees at full walking speed.")]
        [Range(0f, 90f)] public float armSwingDeg = 34f;
        [Tooltip("Peak leg swing in degrees at full walking speed.")]
        [Range(0f, 90f)] public float legSwingDeg = 28f;
        [Tooltip("Elbow bend, as a fraction of the arm swing. 0 = stiff arms.")]
        [Range(0f, 1f)] public float forearmFollow = 0.45f;
        [Tooltip("Strides per metre travelled. Phase advances with DISTANCE, not time, so the swing stays " +
                 "locked to the gait whether walking or running. Kept LOW for a slow, loping lunar cadence.")]
        [Min(0.01f)] public float strideFrequency = 0.34f;
        [Tooltip("Speed at which the swing reaches full amplitude (m/s). At or below walk speed, so the " +
                 "lazy walk already shows a full swing.")]
        [Min(0.01f)] public float fullAmplitudeSpeed = 1.6f;
        [Tooltip("How fast the swing settles back to the rest pose when you stop.")]
        [Min(0f)] public float settleSpeed = 5f;

        [Header("First-person arms (visible in the visor view)")]
        [Tooltip("In first person the arms are lifted forward by this many degrees so the hands sweep " +
                 "through the LOWER part of the frame instead of hanging out of view at the sides. Keep " +
                 "this modest — lifting them far enough to cross the middle of the visor looks unnatural.")]
        [Range(0f, 90f)] public float firstPersonArmLift = 26f;
        [Tooltip("Extra forward elbow bend in first person, bringing the hands into view.")]
        [Range(0f, 120f)] public float firstPersonElbowBend = 34f;
        [Tooltip("Multiplies the arm swing amplitude while in first person. Below 1 so the arms sway " +
                 "gently in view rather than pumping at full walk amplitude.")]
        [Range(0.2f, 2f)] public float firstPersonSwingScale = 0.55f;
        [Tooltip("How fast the first-person arm pose eases in/out when the camera mode changes.")]
        [Min(0.1f)] public float firstPersonBlendSpeed = 10f;
        [Tooltip("Set by AstronautCameraRig: true only while the first-person (visor) camera is active.")]
        public bool firstPerson;

        [Header("Animator parameters")]
        public string speedParameter = "Speed";
        public string movingParameter = "IsMoving";
        [Tooltip("Optional bool an Animator Controller can read to blend to a jump/fall clip. Ignored if the " +
                 "controller has no such parameter.")]
        public string groundedParameter = "IsGrounded";

        // Resolved bones + their rest pose, captured before anything is rotated.
        struct Swinger
        {
            public Transform bone;
            public Quaternion rest;
            public Vector3 axis;         // rotation axis expressed in the PARENT's space, so bone orientation
            public float sign;           // (Maya vs Mixamo vs primitives) doesn't matter
            public float weight;
            public float fpBiasDeg;      // constant forward lift/bend applied only in first person (negative = forward)
            public float fpSwingScale;   // amplitude multiplier applied only in first person
        }

        Swinger[] _swingers = new Swinger[0];
        float _phase;
        float _amplitude;
        float _fpBlend;        // 0 = third person / overview, 1 = first person (eased)
        float _groundBlend = 1f;   // 1 = grounded, eased toward 0 in the air to still the limbs mid-jump
        bool _useAnimator;
        bool _hasSpeedParam, _hasMovingParam, _hasGroundedParam;

        void Awake()
        {
            if (controller == null) controller = GetComponentInParent<AstronautController>();
            if (animator == null) animator = GetComponentInChildren<Animator>();

            _useAnimator = driveMode switch
            {
                DriveMode.ForceAnimator => animator != null,
                DriveMode.ForceProcedural => false,
                _ => animator != null && animator.runtimeAnimatorController != null,
            };

            if (_useAnimator)
            {
                _hasSpeedParam = HasParameter(speedParameter);
                _hasMovingParam = HasParameter(movingParameter);
                _hasGroundedParam = HasParameter(groundedParameter);
            }
            else
            {
                Rebind();
            }
        }

        /// <summary>
        /// Resolve the bones and cache their rest pose. Called on Awake and again by the FBX swap tool
        /// after a new model is parented in.
        /// </summary>
        public void Rebind()
        {
            ResolveBones();

            // First-person forward lift/bend. About the swing axis, "forward" is a NEGATIVE angle (a bone
            // hanging down rotates toward +Z, the body's front). So the biases are negated here, and both
            // arms use the same value so they lift together rather than counter-swinging.
            float legWeight = legSwingDeg / Mathf.Max(0.01f, armSwingDeg);

            var list = new System.Collections.Generic.List<Swinger>(6);
            //                bone,        sign, weight,     fpBiasDeg,                fpSwingScale
            AddSwinger(list, leftArm,      +1f, 1f,         -firstPersonArmLift,       firstPersonSwingScale);
            AddSwinger(list, rightArm,     -1f, 1f,         -firstPersonArmLift,       firstPersonSwingScale);
            AddSwinger(list, leftForearm,  +1f, forearmFollow, -firstPersonElbowBend,  firstPersonSwingScale);
            AddSwinger(list, rightForearm, -1f, forearmFollow, -firstPersonElbowBend,  firstPersonSwingScale);
            AddSwinger(list, leftLeg,      -1f, legWeight,   0f,                        1f);
            AddSwinger(list, rightLeg,     +1f, legWeight,   0f,                        1f);
            _swingers = list.ToArray();
        }

        void AddSwinger(System.Collections.Generic.List<Swinger> list, Transform bone, float sign,
                        float weight, float fpBiasDeg, float fpSwingScale)
        {
            if (bone == null) return;

            // Swing about the BODY's right axis, converted into the bone parent's local space. Doing it
            // this way means we never care how the bone's own axes were authored - a Mixamo upper arm, a
            // Maya joint and a primitive cube all swing forward/back correctly.
            Vector3 worldAxis = transform.right;
            Vector3 axis = bone.parent != null
                ? bone.parent.InverseTransformDirection(worldAxis)
                : worldAxis;
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.right;

            list.Add(new Swinger
            {
                bone = bone,
                rest = bone.localRotation,
                axis = axis.normalized,
                sign = sign,
                weight = weight,
                fpBiasDeg = fpBiasDeg,
                fpSwingScale = fpSwingScale,
            });
        }

        void ResolveBones()
        {
            // 1. Humanoid avatar - the robust path. Bone NAMES are irrelevant; Unity's retargeting maps them.
            if (animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid)
            {
                leftArm      = Pick(leftArm,      animator.GetBoneTransform(HumanBodyBones.LeftUpperArm));
                rightArm     = Pick(rightArm,     animator.GetBoneTransform(HumanBodyBones.RightUpperArm));
                leftForearm  = Pick(leftForearm,  animator.GetBoneTransform(HumanBodyBones.LeftLowerArm));
                rightForearm = Pick(rightForearm, animator.GetBoneTransform(HumanBodyBones.RightLowerArm));
                leftLeg      = Pick(leftLeg,      animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg));
                rightLeg     = Pick(rightLeg,     animator.GetBoneTransform(HumanBodyBones.RightUpperLeg));
            }

            // 2. Name search - for Generic rigs and hand-named Maya joints.
            if (leftArm == null)      leftArm      = FindBone("arm_l_upper", "leftarm", "upperarm_l", "arm_l", "l_arm", "shoulder_l", "leftshoulder");
            if (rightArm == null)     rightArm     = FindBone("arm_r_upper", "rightarm", "upperarm_r", "arm_r", "r_arm", "shoulder_r", "rightshoulder");
            if (leftForearm == null)  leftForearm  = FindBone("arm_l_lower", "leftforearm", "lowerarm_l", "forearm_l", "elbow_l");
            if (rightForearm == null) rightForearm = FindBone("arm_r_lower", "rightforearm", "lowerarm_r", "forearm_r", "elbow_r");
            if (leftLeg == null)      leftLeg      = FindBone("leg_l_upper", "leftupleg", "thigh_l", "leg_l", "l_leg", "upperleg_l");
            if (rightLeg == null)     rightLeg     = FindBone("leg_r_upper", "rightupleg", "thigh_r", "leg_r", "r_leg", "upperleg_r");

            // 3. Whatever is left is whatever the Inspector already had (the primitive placeholder path).
        }

        static Transform Pick(Transform existing, Transform candidate) => candidate != null ? candidate : existing;

        /// <summary>Case/separator-insensitive search over all descendants; tolerates "mixamorig:LeftArm" style prefixes.</summary>
        Transform FindBone(params string[] candidates)
        {
            var all = GetComponentsInChildren<Transform>(includeInactive: true);
            for (int c = 0; c < candidates.Length; c++)
            {
                string want = candidates[c];
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == transform) continue;
                    if (Normalize(all[i].name) == want) return all[i];
                }
            }
            return null;
        }

        static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            int colon = s.LastIndexOf(':');            // strip "mixamorig:" and similar namespaces
            if (colon >= 0 && colon < s.Length - 1) s = s.Substring(colon + 1);
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        bool HasParameter(string param)
        {
            if (animator == null || string.IsNullOrEmpty(param)) return false;
            var ps = animator.parameters;
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].name == param) return true;
            return false;
        }

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            float speed = controller != null ? controller.CurrentSpeed : 0f;
            bool grounded = controller == null || controller.IsGrounded;

            if (_useAnimator)
            {
                if (_hasSpeedParam) animator.SetFloat(speedParameter, speed);
                if (_hasMovingParam) animator.SetBool(movingParameter, speed > 0.05f);
                if (_hasGroundedParam) animator.SetBool(groundedParameter, grounded);
                return;
            }

            if (_swingers.Length == 0) return;

            // Ease the first-person arm pose and the airborne stilling so camera switches and jumps blend
            // smoothly instead of snapping.
            _fpBlend = Mathf.Lerp(_fpBlend, firstPerson ? 1f : 0f, 1f - Mathf.Exp(-firstPersonBlendSpeed * dt));
            _groundBlend = Mathf.Lerp(_groundBlend, grounded ? 1f : 0f, 1f - Mathf.Exp(-8f * dt));

            // Phase advances with distance travelled, so the swing matches the stride at any speed.
            _phase += speed * dt * strideFrequency * Mathf.PI * 2f;
            if (_phase > Mathf.PI * 2f) _phase -= Mathf.PI * 2f;

            float target = Mathf.Clamp01(speed / fullAmplitudeSpeed);
            _amplitude = settleSpeed > 0f
                ? Mathf.Lerp(_amplitude, target, 1f - Mathf.Exp(-settleSpeed * dt))
                : target;

            // In the air the limbs settle (a floaty lunar hop reads as still limbs, not a mid-swing freeze).
            float swing = Mathf.Sin(_phase) * armSwingDeg * _amplitude * _groundBlend;

            for (int i = 0; i < _swingers.Length; i++)
            {
                var s = _swingers[i];
                if (s.bone == null) continue;
                float ampScale = 1f + (s.fpSwingScale - 1f) * _fpBlend;
                float angle = swing * s.sign * s.weight * ampScale + s.fpBiasDeg * _fpBlend;
                // Pre-multiply: apply the swing in the PARENT's space, on top of the captured rest pose.
                s.bone.localRotation = Quaternion.AngleAxis(angle, s.axis) * s.rest;
            }
        }
    }
}
