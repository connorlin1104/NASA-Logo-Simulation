using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NasaSim
{
    /// <summary>
    /// The mower's <see cref="IMowingVisual"/>: cuts the blade field down in the mower's swath (see
    /// <see cref="MowableGrass"/>), flattens the scattered grass clumps standing on it, and throws
    /// colored flowers out the back that arc under lunar gravity and sprout <b>on the line just cut</b> —
    /// so the finished field reads as the NASA logo in flowers.
    ///
    /// The throw is aimed, not random: each flower is given a landing spot picked ON the mown line a
    /// short way behind the deck (from a short history of deck positions, so it follows curves exactly),
    /// jittered sideways only within the swath, and its launch velocity is SOLVED so the arc ends there.
    /// That is what keeps the flower band reading as the logo instead of spraying off into the field.
    ///
    /// Runs on SCALED time deliberately (unlike the player-facing systems): the mow spectacle must keep
    /// pace at 16x fast-forward. That is 16x-safe because <see cref="UpdateAt"/> is called per SUB-STEP
    /// by the tractor (so flower spacing along the path is identical at any speed) and the flight path is
    /// evaluated analytically from a flight-time clock rather than integrated step by step.
    ///
    /// Flower colors: <see cref="PaletteMode.LogoAuto"/> (the default, zero setup) asks
    /// <see cref="LogoStrokeClassifier"/> which strokes are the meatball's circle and which are the swoosh,
    /// and paints them <see cref="circleColor"/> / <see cref="swooshColor"/> with everything else
    /// <see cref="otherColor"/>. <see cref="PaletteMode.StrokeList"/> switches to the hand-editable
    /// <see cref="strokeColors"/> list that Tools &gt; NASA Sim &gt; Grass &gt; Auto-Assign Stroke Colors
    /// fills in.
    ///
    /// A stroke color with <b>alpha 0</b> means "mow this stroke but throw no flowers on it" — which is
    /// how the star dots are silenced (see <see cref="flowersOnStars"/>), and what to set by hand on any
    /// entry of <see cref="strokeColors"/> you want to go quiet.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MowingVisual_GrassAndFlowers : MonoBehaviour, IMowingVisual
    {
        // Ordinals are serialized as raw ints, so new members go on the END — inserting one would
        // silently re-point every already-saved component at a different mode.
        public enum PaletteMode
        {
            /// <summary>Classify the strokes by shape every run — no list to maintain.</summary>
            LogoAuto = 0,
            LogoTexture = 1,
            SingleColor = 2,
            /// <summary>Use <see cref="strokeColors"/>, one entry per stroke in draw order.</summary>
            StrokeList = 3,
        }

        [Header("Grass cutting")]
        [Tooltip("The field of standing blades to cut down. Auto-found in the scene if empty; leave it " +
                 "unassigned (and with no MowableGrass in the scene) to fall back to flattening clumps only.")]
        public MowableGrass mowableGrass;
        [Tooltip("Parent of the scattered clumps (the GrassField object). Auto-found by name if empty. " +
                 "These are the modelled tufts sitting on top of the blade field; they are squashed flat " +
                 "as the deck passes.")]
        public Transform grassFieldRoot;
        [Tooltip("Swath width. Overwritten on Awake by SimulationManager's Mow Width Fraction.")]
        [Min(0.05f)] public float brushWidth = 0.6f;
        [Range(0.02f, 1f)] public float flattenedYScale = 0.12f;
        public Color mowedTint = new Color(0.75f, 0.72f, 0.45f);
        [Min(0.01f)] public float flattenSeconds = 0.25f;

        [Header("Flowers — where they land")]
        [Tooltip("Metres of pen-down travel between flower bursts — the density dial. The shipped logo " +
                 "is ~573 m of line, so 0.4 m is ~1,430 bursts and ~1,930 flowers on the ground at the " +
                 "end of a run. Raise it for a sparser, dottier logo; lower it for a denser one.")]
        [Min(0.1f)] public float flowerSpacing = 0.4f;
        public Vector2Int flowersPerBurst = new Vector2Int(1, 2);
        [Tooltip("How far BEHIND the deck a flower lands, measured back along the line just cut (m). " +
                 "Small values keep the band tight to the tractor; the path is followed exactly either way.")]
        public Vector2 landBackDistance = new Vector2(0.5f, 1.5f);
        [Tooltip("Sideways scatter, as a multiple of HALF the mown swath. 0 = dead on the centre line, " +
                 "1 = out to the swath edge, >1 = a little wider than the cut.")]
        [Range(0f, 3f)] public float landSideSpread = 0.9f;
        [Tooltip("Flight time of the throw (s, min..max). Longer = a higher, lazier arc — the landing " +
                 "spot does not change, only the shape of the arc getting there. At the default lunar " +
                 "gravity 0.7 s peaks ~0.4 m off the ground and 1.0 s peaks ~0.6 m.")]
        public Vector2 arcSeconds = new Vector2(0.7f, 1.0f);
        [Tooltip("Height above the deck the flower is thrown from (m).")]
        [Min(0f)] public float launchHeight = 0.35f;
        [Tooltip("Lunar gravity for the arcs — matches the sim's -3.5 feel. Scaled time, like the tractor.")]
        public float flowerGravity = -3.5f;
        [Min(0.1f)] public float growSeconds = 1f;
        [Tooltip("Hard cap on live flowers; when it is reached the oldest LANDED flower is recycled.\n\n" +
                 "0 (the default) sizes the pool from the logo itself — mown length ÷ Flower Spacing × the " +
                 "TOP of Flowers Per Burst — so a full mow never recycles anything and the finished field " +
                 "is the whole logo. The shipped logo is ~573 m of line: 1,430 bursts, a ceiling of " +
                 "~2,880, against ~1,930 flowers actually thrown (the 42 star dots are silenced, so their " +
                 "~59 m throws nothing). Set a number here only to force a lower ceiling.")]
        [Min(0)] public int flowerPoolLimit = 0;
        [Tooltip("For the ejection direction before any line history exists. Auto-found from the follower.")]
        public Transform tractor;
        [Tooltip("Optional modeled flower mesh; a vertex-colored cross-quad is generated when empty.")]
        public Mesh customFlowerMesh;
        [Tooltip("Layers flowers may land on. Excludes the Player layer (a flower must never 'land' on " +
                 "the astronaut's helmet); the tractor's own colliders are ignored separately.")]
        public LayerMask flowerGroundMask = ~NasaLayers.PlayerMask;

        [Header("Flower colors")]
        public PaletteMode paletteMode = PaletteMode.LogoAuto;
        [Tooltip("LogoAuto: the big round stroke — the meatball's circle.")]
        public Color circleColor = new Color(0.043f, 0.239f, 0.569f, 1f);   // NASA blue #0B3D91
        [Tooltip("LogoAuto: the strokes that overhang the circle — the red swoosh. It arrives as three " +
                 "separate contours (the orbit and the letters cut it up); all of them get this color.")]
        public Color swooshColor = new Color(0.988f, 0.239f, 0.129f, 1f);   // NASA red #FC3D21
        [Tooltip("LogoAuto: everything else — the letters and the orbit ellipse.")]
        public Color otherColor = Color.white;
        [Tooltip("Off (the default): the star dots are still mown, but throw no flowers — 42 loops of " +
                 "0.3-1.3 m read as speckle next to the circle and the letters, and the smallest are " +
                 "below the tractor's turning radius anyway. On: they flower like everything else. " +
                 "A stroke counts as a star below 4% of the logo across.")]
        public bool flowersOnStars = false;
        public Color singleColor = new Color(0.9f, 0.2f, 0.25f);
        [Tooltip("LogoTexture mode: a READABLE texture sampled by position, normalized over the logo bounds.")]
        public Texture2D logoColorLookup;
        [Tooltip("StrokeList mode: one color per pen-down stroke, in draw order. Filled in by " +
                 "Tools > NASA Sim > Grass > Auto-Assign Stroke Colors, then free to hand-edit.")]
        public List<Color> strokeColors = new List<Color>();

        [Header("Flower shape")]
        [Tooltip("Overall height of a flower, stem included (m) — the one dial the whole flower scales " +
                 "off, head included. 0.7 m puts a ~0.39 m head on a 0.9 m astronaut's chest. At the " +
                 "shipped Flower Spacing that is wider than the gap between flowers, so the heads " +
                 "overlap on purpose and the band reads as solid colour; the size variation and lean " +
                 "are what keep that reading as a flowerbed rather than as one flat sheet.")]
        [Min(0.05f)] public float flowerHeight = 0.7f;
        [Tooltip("Random size spread around that height. 0.22 = anywhere from 78% to 122%.")]
        [Range(0f, 0.6f)] public float flowerSizeVariation = 0.22f;
        [Tooltip("Petals around the head. 5-8 reads as a flower; below 5 reads as a cross.")]
        [Range(4, 10)] public int petalCount = 6;
        [Tooltip("The flower's centre — the disc the petals radiate from.")]
        public Color centerColor = new Color(0.98f, 0.80f, 0.26f, 1f);
        [Tooltip("Stem and leaves.")]
        public Color stemColor = new Color(0.19f, 0.40f, 0.15f, 1f);
        [Tooltip("Random lean off vertical, so a bed of flowers isn't a parade ground.")]
        [Range(0f, 25f)] public float flowerLeanDegrees = 9f;
        [Tooltip("How much darker a petal is at its base than at its tip. Baked into the vertex colors — " +
                 "the flower material is unlit, so this shading is what gives the head its depth.")]
        [Range(0f, 0.8f)] public float petalShading = 0.4f;

        // ------------------------------------------------------------------ clump state

        struct Clump
        {
            public Transform t;
            public Renderer r;
            public Vector3 restScale;
            public bool cut;
        }

        struct FlattenTween
        {
            public int clump;
            public float t;
            public Color baseColor;
        }

        const float CellSize = 1f;
        Clump[] _clumps = new Clump[0];
        Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        readonly List<FlattenTween> _tweens = new List<FlattenTween>();

        // ------------------------------------------------------------------ flower state

        const int StatePooled = 0, StateFlying = 1, StateGrowing = 2, StateDone = 3;

        sealed class Flower
        {
            public Transform t;
            public MeshFilter mf;
            public MeshRenderer r;
            public Vector3 launch;        // where the throw started
            public Vector3 vel;           // solved launch velocity
            public Vector3 landPoint;     // exactly where it will land, on the mown line
            public float age;             // seconds into the flight
            public float flight;          // total flight time, so age == flight IS the landing
            public Vector3 tumbleAxis;
            public float tumbleDeg;       // degrees per second of tumble while airborne
            public Quaternion rest;       // how it stands once planted: random yaw plus a slight lean
            public int state;
            public float growT;
            public float finalScale;
        }

        readonly List<Flower> _flowers = new List<Flower>();
        readonly Queue<Flower> _landedOrder = new Queue<Flower>();
        Transform _flowerRoot;
        Mesh _baseFlowerMesh;
        readonly Dictionary<int, Mesh> _tintedMeshes = new Dictionary<int, Mesh>();
        Material _flowerMaterial;
        MaterialPropertyBlock _mpb;
        CsvWaypointLoader _loader;
        TractorPathFollower _follower;

        /// <summary>Distinct flower colors that get their own pre-tinted mesh (keeps them batchable).</summary>
        const int MaxTintedMeshes = 24;

        // ---- recent deck positions: the line the flowers must follow ----
        // Entries are spaced by DISTANCE, not by sub-step, so the ring always covers at least
        // TrailCapacity * TrailMinStep = 4.8 m of line however small the sub-steps get (high frame rate,
        // or corner braking cutting each one to a third).
        const int TrailCapacity = 96;
        const float TrailMinStep = 0.05f;
        readonly Vector3[] _trail = new Vector3[TrailCapacity];
        int _trailCount, _trailHead;
        /// <summary>A deck move longer than this is a teleport between strokes, not travel.</summary>
        const float JumpDistance = 2f;

        bool _pen;
        bool _hasLast;
        Vector3 _lastPos;
        float _distAccum;
        int _strokeIndex = -1;

        Color[] _autoColors;          // one per stroke, in draw order
        int[] _strokeOfPoint;         // waypoint index -> stroke index (-1 = not on any stroke)
        bool _strokeDataBuilt;
        bool _warnedStrokeCount;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            _loader = FindAnyObjectByType<CsvWaypointLoader>();
            _follower = FindAnyObjectByType<TractorPathFollower>();
            if (tractor == null && _follower != null) tractor = _follower.transform;
            if (mowableGrass == null) mowableGrass = FindAnyObjectByType<MowableGrass>();
            BuildClumpIndex();
        }

        void BuildClumpIndex()
        {
            if (grassFieldRoot == null)
            {
                var gf = GameObject.Find("GrassField");
                if (gf != null) grassFieldRoot = gf.transform;
            }

            var list = new List<Clump>();
            _cells.Clear();
            if (grassFieldRoot != null)
            {
                foreach (Transform child in grassFieldRoot)
                {
                    var clump = new Clump
                    {
                        t = child,
                        r = child.GetComponentInChildren<Renderer>(),
                        restScale = child.localScale,
                    };
                    list.Add(clump);
                    long key = KeyFor(child.position);
                    if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = new List<int>();
                    cell.Add(list.Count - 1);
                }
            }
            _clumps = list.ToArray();
        }

        static long KeyFor(Vector3 p) => Key(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        // ------------------------------------------------------------------ IMowingVisual

        public void SetPenDown(bool down)
        {
            _pen = down;
            if (!down) return;

            _strokeIndex++;
            _distAccum = 0f;
            // A new stroke is a new line: drop the previous stroke's history so a flower can never be
            // aimed at a spot on the stroke before it. The follower seeds UpdateAt at the stroke's start
            // just before this call, so that seed becomes the first point of the new line.
            _trailCount = 0;
            _trailHead = 0;
            if (_hasLast) PushTrail(_lastPos);
        }

        public void UpdateAt(Vector3 worldPosition)
        {
            if (_pen)
            {
                float step = _hasLast
                    ? Vector2.Distance(new Vector2(_lastPos.x, _lastPos.z),
                                       new Vector2(worldPosition.x, worldPosition.z))
                    : 0f;
                bool jumped = step > JumpDistance;

                // The whole SEGMENT since the last sub-step is cut, not just the point: the tractor
                // advances up to 0.25 m between calls, and a swath stamped as isolated circles scallops
                // along its edges. A jump between strokes cuts only where it landed.
                CutAt(_hasLast && !jumped ? _lastPos : worldPosition, worldPosition);

                if (jumped)
                {
                    // Not travel — a jump. Start the line over rather than throwing a burst per metre
                    // of the gap, all from the same spot.
                    _distAccum = 0f;
                    _trailCount = 0;
                    _trailHead = 0;
                }
                else if (_hasLast)
                {
                    _distAccum += step;
                    while (_distAccum >= flowerSpacing)
                    {
                        _distAccum -= flowerSpacing;
                        SpawnFlowerBurst(worldPosition);
                    }
                }

                PushTrail(worldPosition);
            }

            _lastPos = worldPosition;
            _hasLast = true;
        }

        public void ResetVisual()
        {
            // Wipe the mow mask: the whole blade field springs back up on the next frame.
            if (mowableGrass != null) mowableGrass.ResetMow();

            // Stand every clump back up and clear any tint override.
            for (int i = 0; i < _clumps.Length; i++)
            {
                var c = _clumps[i];
                if (c.t == null) continue;
                c.t.localScale = c.restScale;
                if (c.r != null) c.r.SetPropertyBlock(null);
                c.cut = false;
                _clumps[i] = c;
            }
            _tweens.Clear();

            foreach (var f in _flowers)
            {
                f.state = StatePooled;
                if (f.t != null) f.t.gameObject.SetActive(false);
            }
            _landedOrder.Clear();

            _pen = false;
            _hasLast = false;
            _distAccum = 0f;
            _strokeIndex = -1;
            _trailCount = 0;
            _trailHead = 0;
            _poolLimit = -1;          // re-derive, so live tuning of spacing/burst size takes effect on R
            _strokeDataBuilt = false; // ...and re-classify, so edits to the colors / Flowers On Stars do too
            DiscardFlowerMeshes();    // ...and re-build the flower, so height/petals/lean do too
        }

        /// <summary>
        /// Drop the generated flower meshes so the next burst rebuilds them from the current settings.
        /// Only safe from <see cref="ResetVisual"/>, which has just pooled (deactivated) every flower —
        /// each one is handed a fresh mesh by <see cref="ApplyFlowerColor"/> when it is next thrown.
        /// </summary>
        void DiscardFlowerMeshes()
        {
            foreach (var m in _tintedMeshes.Values)
                if (m != null) Destroy(m);
            _tintedMeshes.Clear();
            if (_baseFlowerMesh != null) Destroy(_baseFlowerMesh);
            _baseFlowerMesh = null;
        }

        // ------------------------------------------------------------------ the mown line

        void PushTrail(Vector3 p)
        {
            if (_trailCount > 0)
            {
                Vector3 newest = TrailAt(0);
                if ((p.x - newest.x) * (p.x - newest.x) + (p.z - newest.z) * (p.z - newest.z)
                    < TrailMinStep * TrailMinStep) return;
            }
            _trail[_trailHead] = p;
            _trailHead = (_trailHead + 1) % TrailCapacity;
            if (_trailCount < TrailCapacity) _trailCount++;
        }

        /// <summary>Trail entry <paramref name="back"/> steps back from the newest (0 = newest).</summary>
        Vector3 TrailAt(int back) => _trail[((_trailHead - 1 - back) % TrailCapacity + TrailCapacity) % TrailCapacity];

        /// <summary>
        /// Walk back along the recorded deck line by <paramref name="distance"/> metres. Returns the point
        /// on that line and the direction of TRAVEL there — so a flower can be placed on the curve the
        /// tractor actually drove, not on a straight guess behind it.
        /// </summary>
        bool SampleLineBack(float distance, out Vector3 point, out Vector3 forward)
        {
            point = _hasLast ? _lastPos : transform.position;
            forward = tractor != null ? tractor.forward : transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
            if (_trailCount == 0) return false;

            Vector3 cur = TrailAt(0);
            point = cur;
            float remaining = Mathf.Max(0f, distance);
            bool anySegment = false;

            for (int k = 1; k < _trailCount; k++)
            {
                Vector3 prev = TrailAt(k);
                Vector3 d = prev - cur;
                float len = new Vector2(d.x, d.z).magnitude;
                if (len < 1e-5f) { cur = prev; continue; }

                anySegment = true;
                forward = new Vector3(-d.x, 0f, -d.z) / len;      // prev -> cur is the travel direction
                if (len >= remaining)
                {
                    point = cur + d * (remaining / len);
                    return true;
                }
                remaining -= len;
                cur = prev;
            }

            point = cur;                                          // ran out of history: the stroke's start
            return anySegment;
        }

        // ------------------------------------------------------------------ cutting

        void CutAt(Vector3 from, Vector3 pos)
        {
            float radius = brushWidth * 0.5f;

            // The blade field: a few hundred bytes painted into the mow mask, which the grass shader reads
            // per blade. It also tells us how much grass was actually still standing there, which is what
            // decides the clipping spray — so a second pass over cut ground throws nothing.
            if (mowableGrass != null) mowableGrass.Mow(from, pos, radius);

            if (_clumps.Length == 0) return;

            float radiusSq = radius * radius;
            int cx = Mathf.FloorToInt(pos.x / CellSize);
            int cz = Mathf.FloorToInt(pos.z / CellSize);
            int reach = Mathf.Max(1, Mathf.CeilToInt(radius / CellSize));

            for (int dx = -reach; dx <= reach; dx++)
                for (int dz = -reach; dz <= reach; dz++)
                {
                    if (!_cells.TryGetValue(Key(cx + dx, cz + dz), out var cell)) continue;
                    for (int k = 0; k < cell.Count; k++)
                    {
                        int idx = cell[k];
                        var c = _clumps[idx];
                        if (c.cut || c.t == null) continue;
                        Vector3 d = c.t.position - pos;
                        if (d.x * d.x + d.z * d.z > radiusSq) continue;

                        c.cut = true;
                        _clumps[idx] = c;
                        Color baseCol = Color.white;
                        if (c.r != null && c.r.sharedMaterial != null &&
                            c.r.sharedMaterial.HasProperty(BaseColorId))
                            baseCol = c.r.sharedMaterial.GetColor(BaseColorId);
                        _tweens.Add(new FlattenTween { clump = idx, t = 0f, baseColor = baseCol });
                    }
                }
        }

        // ------------------------------------------------------------------ per-frame animation (scaled)

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            for (int i = _tweens.Count - 1; i >= 0; i--)
            {
                var tw = _tweens[i];
                tw.t += dt / flattenSeconds;
                float k = Mathf.Clamp01(tw.t);

                var c = _clumps[tw.clump];
                if (c.t != null)
                {
                    c.t.localScale = new Vector3(
                        c.restScale.x,
                        Mathf.Lerp(c.restScale.y, c.restScale.y * flattenedYScale, k),
                        c.restScale.z);
                    if (c.r != null)
                    {
                        _mpb.Clear();
                        _mpb.SetColor(BaseColorId, Color.Lerp(tw.baseColor, mowedTint, k * 0.8f));
                        c.r.SetPropertyBlock(_mpb);
                    }
                }

                if (k >= 1f) _tweens.RemoveAt(i);
                else _tweens[i] = tw;
            }

            for (int i = 0; i < _flowers.Count; i++)
            {
                var f = _flowers[i];
                if (f.state == StateFlying)
                {
                    f.age += dt;
                    if (f.age >= f.flight)
                    {
                        // The velocity was SOLVED to arrive here, so this is a continuation, not a snap.
                        f.t.SetPositionAndRotation(f.landPoint, f.rest);
                        f.state = StateGrowing;
                        f.growT = 0f;
                        _landedOrder.Enqueue(f);
                    }
                    else
                    {
                        // Evaluated, not integrated: identical arc at 1x and at 16x, where one frame can
                        // be most of the flight.
                        float a = f.age;
                        Vector3 p = f.launch + f.vel * a +
                                    new Vector3(0f, 0.5f * flowerGravity * a * a, 0f);
                        f.t.SetPositionAndRotation(
                            p, Quaternion.AngleAxis(f.tumbleDeg * a, f.tumbleAxis) * f.rest);
                    }
                }
                else if (f.state == StateGrowing)
                {
                    f.growT += dt / growSeconds;
                    // Grow-in with a little overshoot, then settle — reads as "sprouting".
                    float k = Mathf.Clamp01(f.growT);
                    float s = k < 0.8f ? Mathf.Lerp(SeedScale, 1.15f, k / 0.8f)
                                       : Mathf.Lerp(1.15f, 1f, (k - 0.8f) / 0.2f);
                    f.t.localScale = Vector3.one * (s * f.finalScale);
                    if (k >= 1f) f.state = StateDone;
                }
            }
        }

        // ------------------------------------------------------------------ flowers

        /// <summary>Size a flower is thrown at, before it sprouts — small enough to read as a seed.</summary>
        const float SeedScale = 0.35f;

        void SpawnFlowerBurst(Vector3 at)
        {
            // A stroke whose color is fully transparent is mown but throws nothing — the star dots, by
            // default. Tested up front so a silenced stroke costs no ground raycasts either.
            if (TryStrokeColor(out Color strokeColor) && strokeColor.a <= 0f) return;

            int count = Random.Range(flowersPerBurst.x, Mathf.Max(flowersPerBurst.x, flowersPerBurst.y) + 1);
            for (int i = 0; i < count; i++)
            {
                // ---- 1. pick the landing spot ON the line just cut ----
                float back = Random.Range(Mathf.Min(landBackDistance.x, landBackDistance.y),
                                          Mathf.Max(landBackDistance.x, landBackDistance.y));
                SampleLineBack(back, out Vector3 onLine, out Vector3 forward);

                Vector3 right = Vector3.Cross(Vector3.up, forward);
                right = right.sqrMagnitude > 1e-8f ? right.normalized : Vector3.right;
                float half = brushWidth * 0.5f * landSideSpread;
                Vector3 target = onLine + right * Random.Range(-half, half);
                target.y = GroundYAt(target, onLine.y);

                // Decided before a flower is taken from the pool: recycling one and then not using it
                // would drop it out of the landed queue while it was still standing in the field.
                Color color = PickColor(target);
                if (color.a <= 0f) continue;

                Flower f = GetFlower();
                if (f == null) return;

                // ---- 2. solve the throw so the arc ENDS there ----
                Vector3 launch = at + Vector3.up * launchHeight;
                float flight = Mathf.Max(0.05f, Random.Range(Mathf.Min(arcSeconds.x, arcSeconds.y),
                                                             Mathf.Max(arcSeconds.x, arcSeconds.y)));
                Vector3 d = target - launch;
                Vector3 vel = new Vector3(
                    d.x / flight,
                    (d.y - 0.5f * flowerGravity * flight * flight) / flight,
                    d.z / flight);

                f.launch = launch;
                f.vel = vel;
                f.landPoint = target;
                f.flight = flight;
                f.age = 0f;
                // A slight lean in a random direction, so a bed of them doesn't stand to attention.
                f.rest = Quaternion.Euler(Random.Range(-flowerLeanDegrees, flowerLeanDegrees),
                                          Random.value * 360f,
                                          Random.Range(-flowerLeanDegrees, flowerLeanDegrees));
                f.tumbleAxis = right;                                  // tumbles end-over-end as thrown
                // A WHOLE number of turns over the flight, so the tumble is back at identity exactly as
                // it lands and the flower plants itself upright without a one-frame rotation snap.
                int turns = Random.Range(0, 2) == 0 ? -1 : 1;
                if (Random.value < 0.35f) turns *= 2;
                f.tumbleDeg = 360f * turns / flight;
                f.finalScale = Random.Range(1f - flowerSizeVariation, 1f + flowerSizeVariation);
                f.state = StateFlying;

                f.t.SetPositionAndRotation(launch, f.rest);
                f.t.localScale = Vector3.one * (SeedScale * f.finalScale);
                ApplyFlowerColor(f, color);
                f.t.gameObject.SetActive(true);
            }
        }

        int _poolLimit = -1;

        /// <summary>
        /// How many flowers may live at once. With <see cref="flowerPoolLimit"/> left at 0 this is derived
        /// from the logo — every burst of a full mow gets its own flower, so the earliest strokes (the
        /// circle, drawn first) are still standing when the last stroke is cut.
        /// </summary>
        int PoolLimit
        {
            get
            {
                if (_poolLimit > 0) return _poolLimit;
                _poolLimit = flowerPoolLimit > 0 ? flowerPoolLimit : EstimatePoolFromPath();
                return _poolLimit;
            }
        }

        int EstimatePoolFromPath()
        {
            const int Fallback = 1600, HardMax = 8000;
            if (_loader == null) _loader = FindAnyObjectByType<CsvWaypointLoader>();
            WaypointPath path = _loader != null ? _loader.Current : null;
            if (path == null || path.Count < 2) return Fallback;

            float len = 0f;
            var pts = path.Points;
            for (int k = 1; k < pts.Count; k++)
            {
                if (!pts[k].penDown) continue;
                Vector3 d = pts[k].position - pts[k - 1].position;
                len += new Vector2(d.x, d.z).magnitude;
            }

            int bursts = Mathf.CeilToInt(len / Mathf.Max(0.05f, flowerSpacing));
            int perBurst = Mathf.Max(1, Mathf.Max(flowersPerBurst.x, flowersPerBurst.y));
            return Mathf.Clamp(bursts * perBurst + 16, 64, HardMax);
        }

        Flower GetFlower()
        {
            foreach (var f in _flowers)
                if (f.state == StatePooled) return f;

            if (_flowers.Count < PoolLimit)
            {
                var f = CreateFlower();
                _flowers.Add(f);
                return f;
            }

            // Pool exhausted: recycle the oldest LANDED flower (never one mid-flight).
            while (_landedOrder.Count > 0)
            {
                var f = _landedOrder.Dequeue();
                if (f.state == StateGrowing || f.state == StateDone) return f;
            }
            return null;
        }

        Flower CreateFlower()
        {
            if (_flowerRoot == null)
            {
                _flowerRoot = new GameObject("Flowers").transform;   // runtime-only, world origin
            }

            var go = new GameObject("Flower");
            go.transform.SetParent(_flowerRoot, worldPositionStays: false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = customFlowerMesh != null ? customFlowerMesh : GetBaseFlowerMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetFlowerMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.SetActive(false);

            return new Flower { t = go.transform, mf = mf, r = mr, state = StatePooled };
        }

        /// <summary>
        /// Colors a flower by swapping in a mesh whose petal vertices are already that color. There are
        /// only ever a handful of logo colors, so this costs a handful of meshes and — unlike a
        /// per-renderer property block — leaves every flower batchable. Falls back to a property block
        /// for a custom mesh or an unexpected flood of colors.
        /// </summary>
        void ApplyFlowerColor(Flower f, Color color)
        {
            Mesh tinted = customFlowerMesh != null ? null : GetTintedFlowerMesh(color);
            if (tinted != null)
            {
                f.mf.sharedMesh = tinted;
                f.r.SetPropertyBlock(null);
                return;
            }

            f.mf.sharedMesh = customFlowerMesh != null ? customFlowerMesh : GetBaseFlowerMesh();
            _mpb.Clear();
            _mpb.SetColor(BaseColorId, color);
            f.r.SetPropertyBlock(_mpb);
        }

        readonly RaycastHit[] _groundHits = new RaycastHit[8];

        // Highest valid surface below — skipping the Player layer (mask) and anything on the tractor
        // itself (its body collider sits right over the mower, and a flower "landing" on the tractor
        // roof would freeze hovering in mid-air).
        float GroundYAt(Vector3 at, float fallback)
        {
            int n = Physics.RaycastNonAlloc(new Ray(at + Vector3.up * 0.5f, Vector3.down), _groundHits,
                                            30f, flowerGroundMask, QueryTriggerInteraction.Ignore);
            float best = 0f;
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                var h = _groundHits[i];
                if (h.collider == null) continue;
                if (tractor != null && h.collider.transform.IsChildOf(tractor)) continue;
                if (!any || h.point.y > best) { best = h.point.y; any = true; }
            }
            return any ? best : fallback;
        }

        // ------------------------------------------------------------------ color choice

        Color PickColor(Vector3 at)
        {
            switch (paletteMode)
            {
                case PaletteMode.SingleColor:
                    return singleColor;

                case PaletteMode.LogoTexture:
                    if (logoColorLookup != null && _loader != null && _loader.Current != null &&
                        !_loader.Current.IsEmpty)
                    {
                        Bounds b = _loader.Current.Bounds;
                        float u = Mathf.InverseLerp(b.min.x, b.max.x, at.x);
                        float v = Mathf.InverseLerp(b.min.z, b.max.z, at.z);
                        return logoColorLookup.GetPixelBilinear(u, v);
                    }
                    return singleColor;

                default: // LogoAuto, StrokeList
                    return TryStrokeColor(out Color c) ? c : otherColor;
            }
        }

        /// <summary>
        /// The color the stroke being cut throws, for the two stroke-keyed palette modes; false for the
        /// position-based ones, which have no per-stroke answer. An alpha of 0 means the stroke throws
        /// nothing — the silencing channel the star dots ride on.
        /// </summary>
        bool TryStrokeColor(out Color color)
        {
            color = otherColor;

            IReadOnlyList<Color> table;
            if (paletteMode == PaletteMode.StrokeList) table = strokeColors;
            else if (paletteMode == PaletteMode.LogoAuto) { EnsureStrokeData(); table = _autoColors; }
            else return false;
            if (table == null || table.Count == 0) return false;

            int stroke = CurrentStroke();
            if (stroke < 0) return false;

            if (paletteMode == PaletteMode.StrokeList && table.Count != StrokeCount && !_warnedStrokeCount)
            {
                _warnedStrokeCount = true;
                Debug.LogWarning($"[GrassAndFlowers] Stroke Colors has {table.Count} entries but this " +
                                 $"logo has {StrokeCount} strokes, so colors wrap and no longer line up. " +
                                 "Re-run Tools > NASA Sim > Grass > Auto-Assign Stroke Colors, or switch " +
                                 "Palette Mode to Logo Auto.", this);
            }

            // Modulo, not a range check: the Loop end-behaviour re-draws the same strokes each lap
            // without a full ResetVisual, and lap 2+ must keep its colors.
            color = table[stroke % table.Count];
            return true;
        }

        /// <summary>
        /// Which stroke is being drawn right now.
        ///
        /// Taken from the follower's current WAYPOINT, not from a count of pen-down transitions: a stroke
        /// smaller than the tractor's look-ahead is leapt over bodily inside one sub-step, so on the
        /// shipped logo eight of the little stars are never observed and a counter ends up eight behind —
        /// which paints the red swoosh (stroke 46, counted as 38) white. The waypoint index cannot drift.
        /// The counter remains the fallback for a mower driven by something other than the follower.
        /// </summary>
        int CurrentStroke()
        {
            EnsureStrokeData();
            if (_follower != null && _strokeOfPoint != null)
            {
                int k = _follower.CurrentWaypointIndex;
                if (k >= 0 && k < _strokeOfPoint.Length && _strokeOfPoint[k] >= 0)
                    return _strokeOfPoint[k];
            }
            return _strokeIndex;
        }

        int StrokeCount => _autoColors != null ? _autoColors.Length : 0;

        /// <summary>
        /// Classify the logo's strokes once per run, and build the point-index → stroke map the color
        /// lookup uses. Deferred to the first flower rather than done in Awake so the loader has certainly
        /// parsed its CSV, whatever order the scripts wake in.
        /// </summary>
        void EnsureStrokeData()
        {
            if (_strokeDataBuilt) return;
            _strokeDataBuilt = true;

            if (_loader == null) _loader = FindAnyObjectByType<CsvWaypointLoader>();
            WaypointPath path = _loader != null ? (_loader.Current ?? _loader.Load()) : null;
            if (path == null || path.IsEmpty)
            {
                Debug.LogWarning("[GrassAndFlowers] No waypoint path to classify — every flower will be " +
                                 "the 'Other' color.", this);
                return;
            }

            var strokes = LogoStrokeClassifier.Classify(path);
            _strokeOfPoint = new int[path.Count];
            for (int i = 0; i < _strokeOfPoint.Length; i++) _strokeOfPoint[i] = -1;
            foreach (var s in strokes)
                for (int k = s.firstPoint; k <= s.lastPoint && k < _strokeOfPoint.Length; k++)
                    _strokeOfPoint[k] = s.index;

            // Alpha 0 is the "throws nothing" channel; the star dots ride it unless asked otherwise.
            Color silent = new Color(otherColor.r, otherColor.g, otherColor.b, 0f);
            _autoColors = new Color[strokes.Count];
            for (int i = 0; i < strokes.Count; i++)
                _autoColors[i] = strokes[i].role switch
                {
                    LogoStrokeClassifier.Role.Circle => circleColor,
                    LogoStrokeClassifier.Role.Swoosh => swooshColor,
                    LogoStrokeClassifier.Role.Star => flowersOnStars ? otherColor : silent,
                    _ => otherColor,
                };

            if (paletteMode == PaletteMode.LogoAuto)
            {
                var circle = new List<int>();
                var swoosh = new List<int>();
                int stars = 0;
                foreach (var s in strokes)
                {
                    if (s.role == LogoStrokeClassifier.Role.Circle) circle.Add(s.index);
                    else if (s.role == LogoStrokeClassifier.Role.Swoosh) swoosh.Add(s.index);
                    else if (s.role == LogoStrokeClassifier.Role.Star) stars++;
                }
                Debug.Log($"[GrassAndFlowers] {strokes.Count} strokes: " +
                          (circle.Count > 0 ? $"#{string.Join(",", circle)} = the circle (blue)"
                                            : "no circle found") + "; " +
                          (swoosh.Count > 0 ? $"#{string.Join(",", swoosh)} = the swoosh (red)"
                                            : "no swoosh found") + "; " +
                          $"{stars} stars ({(flowersOnStars ? "flowering" : "mown, no flowers")}); " +
                          "everything else (orbit, letters) white.", this);
            }
        }

        /// <summary>Re-run the stroke classification (after swapping the CSV, or changing the colors).</summary>
        [ContextMenu("Rebuild Auto Colors")]
        public void RebuildAutoColors()
        {
            _strokeDataBuilt = false;
            _autoColors = null;
            _strokeOfPoint = null;
            EnsureStrokeData();
        }

        // ------------------------------------------------------------------ generated flower assets

        Mesh GetBaseFlowerMesh()
        {
            if (_baseFlowerMesh == null) _baseFlowerMesh = BuildFlowerMesh(Color.white);
            return _baseFlowerMesh;
        }

        Mesh GetTintedFlowerMesh(Color color)
        {
            Color32 c32 = color;
            int key = (c32.r << 24) | (c32.g << 16) | (c32.b << 8) | c32.a;
            if (_tintedMeshes.TryGetValue(key, out Mesh m) && m != null) return m;
            if (_tintedMeshes.Count >= MaxTintedMeshes) return null;   // property-block fallback
            m = BuildFlowerMesh(color);
            _tintedMeshes[key] = m;
            return m;
        }

        /// <summary>
        /// An actual flower rather than the two crossed cards this used to be: a tapered stem with a pair
        /// of lance leaves, a centre disc, and <see cref="petalCount"/> petals radiating from it — each
        /// petal a lance that widens, tilts up and droops slightly at the tip, which is what stops a head
        /// reading as a flat sticker from above.
        ///
        /// The flower material is UNLIT (it has to multiply vertex colors, which is how one mesh per color
        /// serves every flower of that color and keeps ~1,900 of them batchable). So all the shading is
        /// baked into the vertex colors here: petals darken toward their base, the stem darkens toward the
        /// ground, leaves darken where they meet it. That is what gives the head depth with no lighting.
        ///
        /// Everything scales off <see cref="flowerHeight"/>, so the whole flower is one dial.
        /// </summary>
        Mesh BuildFlowerMesh(Color petal)
        {
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();

            float h = Mathf.Max(0.05f, flowerHeight);
            Color stemCol = stemColor;
            Color petalTip = petal;
            Color petalMid = Shade(petal, 1f - petalShading * 0.35f);
            Color petalBase = Shade(petal, 1f - petalShading);

            void Tri(int a, int b, int c) { tris.Add(a); tris.Add(b); tris.Add(c); }
            int Vert(Vector3 p, Color col) { verts.Add(p); cols.Add(col); return verts.Count - 1; }

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color lower, Color upper)
            {
                int i0 = Vert(a, lower); int i1 = Vert(b, lower);
                int i2 = Vert(c, upper); int i3 = Vert(d, upper);
                Tri(i0, i1, i2); Tri(i0, i2, i3);
            }

            // ---- stem: two crossed quads, tapering, darker where it meets the ground ----
            float headY = h * 0.80f;
            float stemBase = h * 0.018f, stemTop = h * 0.009f;
            Color stemFoot = Shade(stemCol, 0.6f);
            Quad(new Vector3(-stemBase, 0f, 0f), new Vector3(stemBase, 0f, 0f),
                 new Vector3(stemTop, headY, 0f), new Vector3(-stemTop, headY, 0f), stemFoot, stemCol);
            Quad(new Vector3(0f, 0f, -stemBase), new Vector3(0f, 0f, stemBase),
                 new Vector3(0f, headY, stemTop), new Vector3(0f, headY, -stemTop), stemFoot, stemCol);

            // ---- two leaves, on opposite sides and at different heights ----
            AddLeaf(h * 0.30f, 0.6f, h);
            AddLeaf(h * 0.52f, 3.6f, h);

            void AddLeaf(float atY, float angle, float scale)
            {
                var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                var across = new Vector3(-Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                Vector3 root = new Vector3(0f, atY, 0f) + outward * (stemTop * 0.5f);
                Vector3 mid = root + outward * (scale * 0.075f) + Vector3.up * (scale * 0.035f);
                Vector3 tip = root + outward * (scale * 0.145f) + Vector3.up * (scale * 0.048f);
                float halfW = scale * 0.030f;

                int b = Vert(root, Shade(stemCol, 0.65f));
                int l = Vert(mid - across * halfW, stemCol);
                int r = Vert(mid + across * halfW, stemCol);
                int t = Vert(tip, Shade(stemCol, 1.1f));
                Tri(b, l, r);          // the wedge from the stem out to the widest point
                Tri(l, t, r);          // ...and the taper to the point
            }

            // ---- centre disc ----
            float discR = h * 0.052f;
            int centre = Vert(new Vector3(0f, headY + h * 0.012f, 0f), centerColor);
            int firstRim = verts.Count;
            const int DiscSides = 8;
            for (int i = 0; i < DiscSides; i++)
            {
                float a = i / (float)DiscSides * Mathf.PI * 2f;
                Vert(new Vector3(Mathf.Cos(a) * discR, headY, Mathf.Sin(a) * discR),
                     Shade(centerColor, 0.78f));
            }
            for (int i = 0; i < DiscSides; i++)
                Tri(centre, firstRim + i, firstRim + (i + 1) % DiscSides);

            // ---- petals ----
            int petals = Mathf.Clamp(petalCount, 4, 10);
            float r0 = discR * 0.85f, r1 = h * 0.155f, r2 = h * 0.275f;
            float w0 = h * 0.028f, w1 = h * 0.060f;
            for (int i = 0; i < petals; i++)
            {
                // Half a step of offset per flower is not possible with a shared mesh, so the petals are
                // evenly spaced; the per-flower yaw and lean supply the variety instead.
                float a = (i + 0.5f) / petals * Mathf.PI * 2f;
                var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var across = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a));

                // Up at the base, drooping past the widest point: a shallow S, which is what reads as a
                // petal rather than a paper triangle when you look down on the head.
                Vector3 pb = outward * r0 + Vector3.up * (headY + h * 0.004f);
                Vector3 pm = outward * r1 + Vector3.up * (headY + h * 0.038f);
                Vector3 pt = outward * r2 + Vector3.up * (headY + h * 0.014f);

                int bl = Vert(pb - across * w0, petalBase);
                int br = Vert(pb + across * w0, petalBase);
                int ml = Vert(pm - across * w1, petalMid);
                int mr = Vert(pm + across * w1, petalMid);
                int tp = Vert(pt, petalTip);
                Tri(bl, ml, br); Tri(br, ml, mr);
                Tri(ml, tp, mr);
            }

            var mesh = new Mesh { name = "Flower (generated)", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Scale a color's brightness without touching its alpha (alpha is the "throws nothing" channel).</summary>
        static Color Shade(Color c, float k) =>
            new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);

        Material GetFlowerMaterial()
        {
            if (_flowerMaterial != null) return _flowerMaterial;
            // Particles/Unlit multiplies vertex color — exactly what the generated mesh needs. Opaque and
            // double-sided so flowers never join the transparent sorting fight.
            Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            _flowerMaterial = new Material(sh) { name = "Flower (runtime)", hideFlags = HideFlags.HideAndDontSave };
            if (_flowerMaterial.HasProperty("_Cull")) _flowerMaterial.SetFloat("_Cull", 0f);
            if (_flowerMaterial.HasProperty("_BaseColor")) _flowerMaterial.SetColor("_BaseColor", Color.white);
            _flowerMaterial.enableInstancing = true;
            return _flowerMaterial;
        }

        void OnDestroy()
        {
            // The generated mesh/material are HideAndDontSave, so Unity will not reclaim them — take the
            // renderers using them down first, then the assets themselves.
            if (_flowerRoot != null) Destroy(_flowerRoot.gameObject);
            DiscardFlowerMeshes();
            if (_flowerMaterial != null) Destroy(_flowerMaterial);
        }
    }
}
