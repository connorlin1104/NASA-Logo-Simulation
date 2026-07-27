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
        [Tooltip("Shins. Only the SIT POSE uses them (a knee has to bend or sitting reads as lying " +
                 "back with the legs stuck out). Same auto-detection as every other bone.")]
        public Transform leftLowerLeg;
        public Transform rightLowerLeg;
        [Tooltip("Right wrist. Only the INTERACTION ARM uses it (to measure the forearm and find the " +
                 "palm). Auto-detected from a Humanoid avatar, then a 'hand'/'wrist' child of the " +
                 "forearm, then a name search; if nothing is found the forearm's mesh supplies the tip.")]
        public Transform rightHand;

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

        [Header("Jump pose")]
        [Tooltip("Tuck the legs up and lift the arms while airborne instead of freezing rigid in the air.")]
        public bool jumpPose = true;
        [Tooltip("How far the thighs tuck up-forward at the peak of a jump.")]
        [Range(0f, 90f)] public float jumpLegTuckDeg = 35f;
        [Tooltip("How far the arms rise while airborne.")]
        [Range(0f, 90f)] public float jumpArmRaiseDeg = 25f;
        [Tooltip("How quickly the jump pose eases in on takeoff and out on landing.")]
        [Min(0.1f)] public float jumpPoseBlendSpeed = 9f;

        [Header("Landing")]
        [Tooltip("Dip the body and bend the knees briefly on touchdown so a landing has weight.")]
        public bool landingSquash = true;
        [Tooltip("How far the body dips at the hardest landing (metres).")]
        [Range(0f, 0.5f)] public float landingSquashDepth = 0.16f;
        [Tooltip("Extra knee bend at the hardest landing (degrees).")]
        [Range(0f, 90f)] public float landingKneeBendDeg = 30f;
        [Tooltip("Downward speed that produces a full-strength squash (m/s). A normal moon-hop lands near this.")]
        [Min(0.5f)] public float landingFullSpeed = 3.5f;
        [Tooltip("How quickly the crouch recovers after landing.")]
        [Min(0.1f)] public float landingRecoverSpeed = 7f;

        [Header("Sit pose (driven by AstronautSitting)")]
        [Tooltip("0 = standing, 1 = fully folded into a chair. AstronautSitting eases this from 0 to 1 " +
                 "as it walks the body into the seat; nothing else writes it.")]
        [Range(0f, 1f)] public float sitBlend;
        [Tooltip("How far the thighs swing FORWARD when seated. 90 is a right angle at the hip.")]
        [Range(0f, 110f)] public float sitThighDeg = 76f;
        [Tooltip("How far the shins fold back at the knee when seated.")]
        [Range(0f, 120f)] public float sitKneeDeg = 82f;
        [Tooltip("Small forward lift of the upper arms when seated, so they don't hang through the chair.")]
        [Range(0f, 60f)] public float sitArmDeg = 14f;
        [Tooltip("Forearm bend when seated — this is what drops the hands onto the lap.")]
        [Range(0f, 120f)] public float sitElbowDeg = 48f;

        [Header("Interaction arm (eat / pet)")]
        [Tooltip("How far the elbow swings OUT to the side while the hand reaches for something. 0 drops " +
                 "it straight down behind the hand; higher values open the arm out so the forearm reads " +
                 "clearly across the lower visor instead of pointing at the camera.")]
        [Range(-1f, 1.5f)] public float reachElbowOut = 0.6f;

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
            public float airBiasDeg;     // forward tuck/lift applied only while airborne (jump pose; negative = forward)
            public float squashBiasDeg;  // extra bend applied only during a landing squash (legs)
            public float sitBiasDeg;     // the seated pose; the only bias that SUPPRESSES the others
        }

        Swinger[] _swingers = new Swinger[0];
        float _upperArmLen;        // world metres, shoulder -> elbow, measured off the live rig
        float _forearmLen;         // world metres, elbow -> wrist
        Vector3 _wristLocal;       // wrist position in the forearm's local space (measured or estimated)
        float _phase;
        float _amplitude;
        float _fpBlend;        // 0 = third person / overview, 1 = first person (eased)
        float _groundBlend = 1f;   // 1 = grounded, eased toward 0 in the air to still the limbs mid-jump
        float _airPose;        // 0 = grounded, 1 = airborne (eased); drives the jump tuck/lift
        float _squash;         // 0..1 landing crouch; snaps up on impact, eases back to standing
        Transform _squashRoot; // the model-root child dipped for the landing squash (never the CC root)
        Vector3 _restSquashRootPos;
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
                // The sim fast-forwards Time.timeScale up to 16x while the astronaut walks in real
                // time — a real walk clip must run on the unscaled clock too, or it races the body.
                animator.updateMode = AnimatorUpdateMode.UnscaledTime;
                _hasSpeedParam = HasParameter(speedParameter);
                _hasMovingParam = HasParameter(movingParameter);
                _hasGroundedParam = HasParameter(groundedParameter);

                // The interaction arm is solved by hand on top of the clip, so it needs the bones and
                // their measured lengths on this path too.
                ResolveBones();
                ResolveHand();
                MeasureArm();
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
            ResolveHand();
            MeasureArm();

            // First-person forward lift/bend. About the swing axis, "forward" is a NEGATIVE angle (a bone
            // hanging down rotates toward +Z, the body's front). So the biases are negated here, and both
            // arms use the same value so they lift together rather than counter-swinging.
            float legWeight = legSwingDeg / Mathf.Max(0.01f, armSwingDeg);

            // Jump pose: arms rise and thighs tuck up-forward. As with the first-person bias, "forward"
            // about the swing axis is a NEGATIVE angle, and both sides share the same value so they move
            // together rather than counter-swinging. Legs also carry the landing knee-bend.
            // The shins carry NO walk swing (weight 0) — they exist in this list purely so the sit pose
            // has a knee to bend. Everything else is unchanged.
            var list = new System.Collections.Generic.List<Swinger>(8);
            //                bone,          sign, weight,        fpBiasDeg,            fpSwingScale,          airBiasDeg,               squashBiasDeg,        sitBiasDeg
            AddSwinger(list, leftArm,        +1f, 1f,            -firstPersonArmLift,   firstPersonSwingScale, -jumpArmRaiseDeg,          0f,                  -sitArmDeg);
            AddSwinger(list, rightArm,       -1f, 1f,            -firstPersonArmLift,   firstPersonSwingScale, -jumpArmRaiseDeg,          0f,                  -sitArmDeg);
            AddSwinger(list, leftForearm,    +1f, forearmFollow, -firstPersonElbowBend, firstPersonSwingScale, -jumpArmRaiseDeg * 0.6f,   0f,                  -sitElbowDeg);
            AddSwinger(list, rightForearm,   -1f, forearmFollow, -firstPersonElbowBend, firstPersonSwingScale, -jumpArmRaiseDeg * 0.6f,   0f,                  -sitElbowDeg);
            AddSwinger(list, leftLeg,        -1f, legWeight,      0f,                    1f,                    -jumpLegTuckDeg,          -landingKneeBendDeg, -sitThighDeg);
            AddSwinger(list, rightLeg,       +1f, legWeight,      0f,                    1f,                    -jumpLegTuckDeg,          -landingKneeBendDeg, -sitThighDeg);
            AddSwinger(list, leftLowerLeg,   -1f, 0f,             0f,                    1f,                     0f,                       0f,                 +sitKneeDeg);
            AddSwinger(list, rightLowerLeg,  +1f, 0f,             0f,                    1f,                     0f,                       0f,                 +sitKneeDeg);
            _swingers = list.ToArray();

            _squashRoot = ResolveVisualRoot();
            _restSquashRootPos = _squashRoot != null ? _squashRoot.localPosition : Vector3.zero;
        }

        void AddSwinger(System.Collections.Generic.List<Swinger> list, Transform bone, float sign,
                        float weight, float fpBiasDeg, float fpSwingScale, float airBiasDeg,
                        float squashBiasDeg, float sitBiasDeg)
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
                airBiasDeg = airBiasDeg,
                squashBiasDeg = squashBiasDeg,
                sitBiasDeg = sitBiasDeg,
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
                leftLowerLeg  = Pick(leftLowerLeg,  animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg));
                rightLowerLeg = Pick(rightLowerLeg, animator.GetBoneTransform(HumanBodyBones.RightLowerLeg));
            }

            // 2. Name search - for Generic rigs and hand-named Maya joints. The bip001* spellings cover a
            // 3ds Max Biped, which is what this project's astronaut was exported as.
            if (leftArm == null)      leftArm      = FindBone("arm_l_upper", "leftarm", "upperarm_l", "arm_l", "l_arm", "shoulder_l", "leftshoulder", "bip001lupperarm");
            if (rightArm == null)     rightArm     = FindBone("arm_r_upper", "rightarm", "upperarm_r", "arm_r", "r_arm", "shoulder_r", "rightshoulder", "bip001rupperarm");
            if (leftForearm == null)  leftForearm  = FindBone("arm_l_lower", "leftforearm", "lowerarm_l", "forearm_l", "elbow_l", "bip001lforearm");
            if (rightForearm == null) rightForearm = FindBone("arm_r_lower", "rightforearm", "lowerarm_r", "forearm_r", "elbow_r", "bip001rforearm");
            if (leftLeg == null)      leftLeg      = FindBone("leg_l_upper", "leftupleg", "thigh_l", "leg_l", "l_leg", "upperleg_l", "bip001lthigh");
            if (rightLeg == null)     rightLeg     = FindBone("leg_r_upper", "rightupleg", "thigh_r", "leg_r", "r_leg", "upperleg_r", "bip001rthigh");
            if (leftLowerLeg == null)  leftLowerLeg  = FindBone("leg_l_lower", "leftleg", "calf_l", "l_calf", "shin_l", "lowerleg_l", "bip001lcalf");
            if (rightLowerLeg == null) rightLowerLeg = FindBone("leg_r_lower", "rightleg", "calf_r", "r_calf", "shin_r", "lowerleg_r", "bip001rcalf");

            // 3. Whatever is left is whatever the Inspector already had (the primitive placeholder path).
        }

        static Transform Pick(Transform existing, Transform candidate) => candidate != null ? candidate : existing;

        /// <summary>
        /// Find the right wrist. Same escalating strategy as the other bones, plus a breadth-first
        /// "hand"/"wrist" search under the forearm — which catches Biped ("Bip001 R Hand"), Mixamo and
        /// hand-named Maya joints alike without matching a finger first.
        /// </summary>
        void ResolveHand()
        {
            if (rightHand == null && animator != null && animator.isHuman
                && animator.avatar != null && animator.avatar.isValid)
                rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);

            if (rightHand == null && rightForearm != null)
                rightHand = FindDescendantContaining(rightForearm, "hand", "wrist", "palm");

            if (rightHand == null)
                rightHand = FindBone("bip001rhand", "righthand", "hand_r", "r_hand", "wrist_r", "hand_right");

            if (rightHand == rightForearm) rightHand = null;
        }

        /// <summary>
        /// Measure the right arm's segment lengths in WORLD metres off the live rig, so the reach maths
        /// works the same for the 1.8 m primitive placeholder and a model imported at any scale.
        /// </summary>
        void MeasureArm()
        {
            _upperArmLen = 0f;
            _forearmLen = 0f;
            _wristLocal = Vector3.zero;
            if (rightArm == null || rightForearm == null) return;

            _upperArmLen = Vector3.Distance(rightArm.position, rightForearm.position);
            if (_upperArmLen < 1e-4f) { _upperArmLen = 0f; return; }

            if (rightHand != null)
            {
                _wristLocal = rightForearm.InverseTransformPoint(rightHand.position);
                _forearmLen = Vector3.Distance(rightForearm.position, rightHand.position);
            }

            if (_forearmLen < 1e-4f)
            {
                _wristLocal = EstimateWristLocal();
                _forearmLen = Vector3.Distance(rightForearm.position, rightForearm.TransformPoint(_wristLocal));
            }
            if (_forearmLen < 1e-4f) { _upperArmLen = 0f; _forearmLen = 0f; }
        }

        /// <summary>
        /// No wrist bone: put the tip at twice the centroid of whatever the forearm renders (a limb mesh
        /// hangs from its joint, so its centre sits at half the segment's length), and if the forearm
        /// renders nothing, continue the upper arm's line for the same length again.
        /// </summary>
        Vector3 EstimateWristLocal()
        {
            var rends = rightForearm.GetComponentsInChildren<Renderer>();
            if (rends.Length > 0)
            {
                Bounds b = rends[0].bounds;
                for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
                Vector3 local = rightForearm.InverseTransformPoint(b.center) * 2f;
                if (local.sqrMagnitude > 1e-8f) return local;
            }
            Vector3 dir = (rightForearm.position - rightArm.position).normalized;
            return rightForearm.InverseTransformPoint(rightForearm.position + dir * _upperArmLen);
        }

        /// <summary>Breadth-first descendant search: the first bone whose name contains one of the words.</summary>
        static Transform FindDescendantContaining(Transform root, params string[] words)
        {
            var queue = new System.Collections.Generic.Queue<Transform>();
            for (int i = 0; i < root.childCount; i++) queue.Enqueue(root.GetChild(i));
            while (queue.Count > 0)
            {
                Transform t = queue.Dequeue();
                string n = Normalize(t.name);
                for (int i = 0; i < words.Length; i++)
                    if (n.Contains(words[i])) return t;
                for (int i = 0; i < t.childCount; i++) queue.Enqueue(t.GetChild(i));
            }
            return null;
        }

        /// <summary>
        /// The model-root child of the controller object - the only thing safe to dip for the landing
        /// squash. It must NOT be the controller's own transform (dipping that would fight the
        /// CharacterController's Move), so we walk up from a resolved bone to the highest ancestor that is
        /// still a direct child of this object. Returns null if the bones sit directly on the controller
        /// root (nothing safe to dip - the squash then only bends the knees).
        /// </summary>
        Transform ResolveVisualRoot()
        {
            Transform bone = leftLeg ?? rightLeg ?? leftArm ?? rightArm ?? leftForearm ?? rightForearm;
            if (bone == null) return null;
            Transform t = bone;
            while (t.parent != null && t.parent != transform) t = t.parent;
            return t.parent == transform ? t : null;
        }

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

            float sit = Mathf.Clamp01(sitBlend);
            float notSit = 1f - sit;

            if (_useAnimator)
            {
                if (_hasSpeedParam) animator.SetFloat(speedParameter, speed);
                if (_hasMovingParam) animator.SetBool(movingParameter, speed > 0.05f);
                if (_hasGroundedParam) animator.SetBool(groundedParameter, grounded);
                // No walk clip has a "sitting" pose, so fold the limbs on top of whatever it produced.
                if (sit > 0.001f) ApplySitOverClip(sit);
                return;                // the interaction arm is layered on afterwards, see ApplyHandTargetNow
            }

            if (_swingers.Length == 0) return;

            // Ease the first-person arm pose and the airborne stilling so camera switches and jumps blend
            // smoothly instead of snapping.
            _fpBlend = Mathf.Lerp(_fpBlend, firstPerson ? 1f : 0f, 1f - Mathf.Exp(-firstPersonBlendSpeed * dt));
            _groundBlend = Mathf.Lerp(_groundBlend, grounded ? 1f : 0f, 1f - Mathf.Exp(-8f * dt));

            // Jump pose eases in while airborne (tuck legs, raise arms) and out on landing. Seated, the
            // CharacterController is switched off — which reads as "not grounded" — so the sit blend has
            // to veto the jump pose or the astronaut would tuck its legs up the moment it sat down.
            float airTarget = (jumpPose && !grounded && sit < 0.5f) ? 1f : 0f;
            _airPose = Mathf.Lerp(_airPose, airTarget, 1f - Mathf.Exp(-jumpPoseBlendSpeed * dt));

            // Landing squash: snap up on the touchdown impact, then ease back to standing.
            if (landingSquash && controller != null && controller.LandingImpact > 0f)
            {
                float impact = Mathf.Clamp01(controller.LandingImpact / landingFullSpeed);
                if (impact > _squash) _squash = impact;
            }
            _squash = Mathf.Lerp(_squash, 0f, 1f - Mathf.Exp(-landingRecoverSpeed * dt));

            // Phase advances with distance travelled, so the swing matches the stride at any speed.
            _phase += speed * dt * strideFrequency * Mathf.PI * 2f;
            if (_phase > Mathf.PI * 2f) _phase -= Mathf.PI * 2f;

            float target = Mathf.Clamp01(speed / fullAmplitudeSpeed);
            _amplitude = settleSpeed > 0f
                ? Mathf.Lerp(_amplitude, target, 1f - Mathf.Exp(-settleSpeed * dt))
                : target;

            // The walk swing settles in the air (a floaty lunar hop reads as a still, tucked body, not a
            // mid-swing freeze); the jump pose above supplies the airborne shape instead.
            float swing = Mathf.Sin(_phase) * armSwingDeg * _amplitude * _groundBlend * notSit;

            for (int i = 0; i < _swingers.Length; i++)
            {
                var s = _swingers[i];
                if (s.bone == null) continue;
                float ampScale = 1f + (s.fpSwingScale - 1f) * _fpBlend;
                // Every walking bias fades out as the sit blend comes in, so the two poses cross-fade
                // instead of adding up into a seated astronaut still swinging its arms.
                float angle = swing * s.sign * s.weight * ampScale
                            + (s.fpBiasDeg * _fpBlend
                             + s.airBiasDeg * _airPose
                             + s.squashBiasDeg * _squash) * notSit
                            + s.sitBiasDeg * sit;
                // Pre-multiply: apply the swing in the PARENT's space, on top of the captured rest pose.
                s.bone.localRotation = Quaternion.AngleAxis(angle, s.axis) * s.rest;
            }

            // Dip the model root (never the controller root) for the landing crouch.
            if (_squashRoot != null)
                _squashRoot.localPosition =
                    _restSquashRootPos + Vector3.down * (_squash * landingSquashDepth * notSit);
        }

        /// <summary>
        /// The seated pose applied as a RELATIVE bend on top of an Animator clip's output — the swingers'
        /// captured rest poses are meaningless on that path, since the clip rewrites the bones every
        /// frame. Called from LateUpdate, after the Animator has run.
        /// </summary>
        void ApplySitOverClip(float sit)
        {
            BendOverClip(leftArm, -sitArmDeg * sit);
            BendOverClip(rightArm, -sitArmDeg * sit);
            BendOverClip(leftForearm, -sitElbowDeg * sit);
            BendOverClip(rightForearm, -sitElbowDeg * sit);
            BendOverClip(leftLeg, -sitThighDeg * sit);
            BendOverClip(rightLeg, -sitThighDeg * sit);
            BendOverClip(leftLowerLeg, sitKneeDeg * sit);
            BendOverClip(rightLowerLeg, sitKneeDeg * sit);
        }

        void BendOverClip(Transform bone, float degrees)
        {
            if (bone == null || Mathf.Abs(degrees) < 0.01f) return;
            Vector3 axis = bone.parent != null
                ? bone.parent.InverseTransformDirection(transform.right)
                : Vector3.right;
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.right;
            bone.localRotation = Quaternion.AngleAxis(degrees, axis.normalized) * bone.localRotation;
        }

        /// <summary>
        /// Height of the hip joint above the astronaut's feet, in world metres, measured off the live rig.
        /// <see cref="AstronautSitting"/> drops the body by exactly this much to land the hips on a seat,
        /// so a model imported at any scale seats itself correctly.
        /// </summary>
        public float HipHeightAboveRoot
        {
            get
            {
                Transform hip = leftLeg != null ? leftLeg : rightLeg;
                if (hip == null) return 0.9f;
                return Mathf.Max(0.05f, hip.position.y - transform.position.y);
            }
        }

        // ---------------------------------------------------------------- interaction arm

        /// <summary>True when the right arm can be solved as a two-bone chain (shoulder/elbow/wrist).</summary>
        public bool HasReachArm => rightArm != null && rightForearm != null && _upperArmLen > 0f && _forearmLen > 0f;

        /// <summary>Shoulder-to-wrist span in WORLD metres. Everything the hand does is expressed as a
        /// fraction of this, so poses frame the same way whatever size the model is.</summary>
        public float ArmReach => _upperArmLen + _forearmLen;

        public Vector3 ShoulderPosition => rightArm != null ? rightArm.position : transform.position;

        /// <summary>Live world position of the right wrist (the measured tip when there is no wrist bone).</summary>
        public Vector3 WristPosition =>
            rightHand != null ? rightHand.position
            : rightForearm != null ? rightForearm.TransformPoint(_wristLocal)
            : ShoulderPosition;

        /// <summary>
        /// Where a carried object sits after the last <see cref="ApplyHandTargetNow"/> — the palm, i.e.
        /// the wrist pushed forward along the forearm by the grip offset that was asked for.
        /// </summary>
        public Vector3 GripPoint { get; private set; }

        /// <summary>
        /// Pull a world point inside the arm's comfortable working envelope. Callers clamp their own
        /// targets with this so the pose they animate and the pose the arm can actually hit agree — a
        /// carried object then never drifts off the hand.
        /// </summary>
        public Vector3 ClampToArmReach(Vector3 worldPoint, float gripOffset = 0f)
        {
            if (!HasReachArm) return worldPoint;
            Vector3 shoulder = rightArm.position;
            Vector3 v = worldPoint - shoulder;
            float d = v.magnitude;
            if (d < 1e-5f) return shoulder + transform.forward * (ArmReach * 0.5f);

            float max = ArmReach * 0.97f + gripOffset;
            float min = Mathf.Abs(_upperArmLen - _forearmLen) + ArmReach * 0.22f + gripOffset;
            float clamped = Mathf.Clamp(d, min, max);
            return Mathf.Approximately(clamped, d) ? worldPoint : shoulder + v * (clamped / d);
        }

        /// <summary>
        /// Pose the right arm so its palm lands on <paramref name="worldTarget"/>, blended over whatever
        /// the walk swing (or an Animator clip) produced. IMMEDIATE MODE: call this from a LateUpdate that
        /// runs after this component's own — see <see cref="HandActionController"/>, which owns the eat
        /// and pet sequences — and simply stop calling it to hand the arm back.
        ///
        /// Two-bone analytic IK. Segment directions are measured live from world positions rather than
        /// assumed from the bones' authored axes, exactly like the walk swing, so a Biped, a Mixamo rig
        /// and the primitive placeholder all solve identically.
        /// </summary>
        /// <param name="gripOffset">How far short of the target the WRIST stops, so an object of that
        /// radius sits in the palm rather than through it.</param>
        public void ApplyHandTargetNow(Vector3 worldTarget, float blend01, float gripOffset = 0f)
        {
            float blend = Mathf.Clamp01(blend01);
            if (blend <= 0.001f || rightArm == null) return;

            Vector3 shoulder = rightArm.position;
            Vector3 to = worldTarget - shoulder;
            if (to.sqrMagnitude < 1e-8f) return;
            float dist = to.magnitude;
            Vector3 dir = to / dist;

            if (!HasReachArm)
            {
                // Single-bone fallback: aim the upper arm and let the rest of the limb follow.
                Vector3 boneDir = rightForearm != null ? rightForearm.position - shoulder : -rightArm.up;
                if (boneDir.sqrMagnitude < 1e-8f) return;
                rightArm.rotation = Quaternion.Slerp(rightArm.rotation,
                    Quaternion.FromToRotation(boneDir.normalized, dir) * rightArm.rotation, blend);
                GripPoint = WristPosition;
                return;
            }

            // The wrist stops short of the target by the grip offset; the palm is what lands on it.
            float wristDist = Mathf.Clamp(dist - gripOffset,
                                          Mathf.Abs(_upperArmLen - _forearmLen) + ArmReach * 0.05f,
                                          ArmReach * 0.999f);

            // Law of cosines: the angle between the upper arm and the shoulder->wrist line.
            float cosShoulder = (_upperArmLen * _upperArmLen + wristDist * wristDist - _forearmLen * _forearmLen)
                                / (2f * _upperArmLen * wristDist);
            float shoulderAngle = Mathf.Acos(Mathf.Clamp(cosShoulder, -1f, 1f)) * Mathf.Rad2Deg;

            // Bend plane. Rotating the shoulder->wrist direction about (dir x pole) by a POSITIVE angle
            // swings it toward the pole, so a pole of "down and out to the right" drops the elbow the way
            // a real arm folds — and keeps the forearm across the lower visor instead of edge-on.
            Vector3 pole = -transform.up + transform.right * reachElbowOut;
            Vector3 bendAxis = Vector3.Cross(dir, pole);
            if (bendAxis.sqrMagnitude < 1e-6f) bendAxis = Vector3.Cross(dir, transform.forward);
            if (bendAxis.sqrMagnitude < 1e-6f) return;
            bendAxis.Normalize();

            Vector3 upperWant = Quaternion.AngleAxis(shoulderAngle, bendAxis) * dir;
            Vector3 wristWant = shoulder + dir * wristDist;

            Vector3 upperNow = rightForearm.position - shoulder;
            if (upperNow.sqrMagnitude > 1e-10f)
                rightArm.rotation = Quaternion.Slerp(rightArm.rotation,
                    Quaternion.FromToRotation(upperNow.normalized, upperWant) * rightArm.rotation, blend);

            // Re-read the elbow: it moved with the upper arm above.
            Vector3 elbow = rightForearm.position;
            Vector3 lowerNow = WristPosition - elbow;
            Vector3 lowerWant = wristWant - elbow;
            if (lowerNow.sqrMagnitude > 1e-10f && lowerWant.sqrMagnitude > 1e-10f)
                rightForearm.rotation = Quaternion.Slerp(rightForearm.rotation,
                    Quaternion.FromToRotation(lowerNow.normalized, lowerWant.normalized) * rightForearm.rotation,
                    blend);

            // Report the palm from the pose that actually resulted, so a carried object tracks the hand
            // exactly even while the blend is still easing in.
            Vector3 wrist = WristPosition;
            Vector3 palmDir = wrist - rightForearm.position;
            GripPoint = wrist + (palmDir.sqrMagnitude > 1e-10f ? palmDir.normalized : dir) * gripOffset;
        }
    }
}
