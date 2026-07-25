using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NasaSim
{
    /// <summary>
    /// The second <see cref="IMowingVisual"/> (running beside the trail ribbon via
    /// <see cref="MowerController"/>): flattens scattered grass clumps in the mower's swath, and ejects
    /// colored flowers backward off the tractor that arc under lunar gravity, land, and grow in place —
    /// so the finished field reads as the NASA logo in flowers.
    ///
    /// Runs on SCALED time deliberately (unlike the player-facing systems): the mow spectacle must keep
    /// pace at 16x fast-forward. That is 16x-safe because <see cref="UpdateAt"/> is called per SUB-STEP
    /// by the tractor, so flower spacing along the path is identical at any speed, and the ballistics
    /// just integrate faster.
    ///
    /// Flower colors: <see cref="PaletteMode.LogoAuto"/> uses <see cref="strokeColors"/> — one color per
    /// pen-down stroke in draw order, auto-filled by Tools &gt; NASA Sim &gt; Grass &gt; Auto-Assign
    /// Stroke Colors (big circle = NASA blue, wide swoosh = red, letters/orbit = white) and freely
    /// hand-editable afterwards.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MowingVisual_GrassAndFlowers : MonoBehaviour, IMowingVisual
    {
        public enum PaletteMode { LogoAuto, LogoTexture, SingleColor }

        [Header("Grass flattening")]
        [Tooltip("Parent of the scattered clumps (the GrassField object). Auto-found by name if empty.")]
        public Transform grassFieldRoot;
        [Tooltip("Swath width. Overwritten on Start by SimulationManager (1.5x the trail width).")]
        [Min(0.05f)] public float brushWidth = 0.6f;
        [Range(0.02f, 1f)] public float flattenedYScale = 0.12f;
        public Color mowedTint = new Color(0.75f, 0.72f, 0.45f);
        [Min(0.01f)] public float flattenSeconds = 0.25f;

        [Header("Flowers")]
        public PaletteMode paletteMode = PaletteMode.LogoAuto;
        public Color singleColor = new Color(0.9f, 0.2f, 0.25f);
        [Tooltip("LogoTexture mode: a READABLE texture sampled by position, normalized over the logo bounds.")]
        public Texture2D logoColorLookup;
        [Tooltip("Metres of pen-down travel between flower bursts.")]
        [Min(0.1f)] public float flowerSpacing = 0.6f;
        public Vector2Int flowersPerBurst = new Vector2Int(1, 2);
        public Vector2 ejectSpeedRange = new Vector2(2.5f, 4f);
        [Tooltip("Launch pitch above horizontal (deg, min..max).")]
        public Vector2 ejectPitchRangeDeg = new Vector2(35f, 60f);
        [Tooltip("Flowers fan out BEHIND the tractor across this yaw spread (deg).")]
        [Min(0f)] public float ejectYawSpreadDeg = 80f;
        [Tooltip("Lunar gravity for the arcs — matches the sim's -3.5 feel. Scaled time, like the tractor.")]
        public float flowerGravity = -3.5f;
        [Min(0.1f)] public float growSeconds = 1f;
        [Tooltip("Pooled: when exhausted, the oldest LANDED flower is recycled.")]
        [Min(10)] public int maxFlowers = 800;
        [Tooltip("For the backward ejection direction. Auto-found from the TractorPathFollower.")]
        public Transform tractor;
        [Tooltip("Optional modeled flower mesh; a vertex-colored cross-quad is generated when empty.")]
        public Mesh customFlowerMesh;
        [Tooltip("Layers flowers may land on. Excludes the Player layer (a flower must never 'land' on " +
                 "the astronaut's helmet); the tractor's own colliders are ignored separately.")]
        public LayerMask flowerGroundMask = ~NasaLayers.PlayerMask;

        [Header("Per-stroke colors (LogoAuto mode)")]
        public List<Color> strokeColors = new List<Color>();

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
            public Renderer r;
            public Vector3 vel;
            public float groundY;
            public bool groundSampled;   // ground is measured at the DESCENT position, not the eject point
            public int state;
            public float growT;
            public float finalScale;
        }

        readonly List<Flower> _flowers = new List<Flower>();
        readonly Queue<Flower> _landedOrder = new Queue<Flower>();
        Transform _flowerRoot;
        Mesh _sharedFlowerMesh;
        Material _flowerMaterial;
        MaterialPropertyBlock _mpb;
        CsvWaypointLoader _loader;

        bool _pen;
        bool _hasLast;
        Vector3 _lastPos;
        float _distAccum;
        int _strokeIndex = -1;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        void Awake()
        {
            _mpb = new MaterialPropertyBlock();
            _loader = FindAnyObjectByType<CsvWaypointLoader>();
            if (tractor == null)
            {
                var follower = FindAnyObjectByType<TractorPathFollower>();
                if (follower != null) tractor = follower.transform;
            }
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
            if (down)
            {
                _strokeIndex++;
                _distAccum = 0f;
            }
        }

        public void UpdateAt(Vector3 worldPosition)
        {
            if (_pen)
            {
                CutAt(worldPosition);

                if (_hasLast)
                {
                    _distAccum += Vector3.Distance(
                        new Vector3(_lastPos.x, 0f, _lastPos.z),
                        new Vector3(worldPosition.x, 0f, worldPosition.z));
                    while (_distAccum >= flowerSpacing)
                    {
                        _distAccum -= flowerSpacing;
                        SpawnFlowerBurst(worldPosition);
                    }
                }
            }

            _lastPos = worldPosition;
            _hasLast = true;
        }

        public void ResetVisual()
        {
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
        }

        // ------------------------------------------------------------------ cutting

        void CutAt(Vector3 pos)
        {
            if (_clumps.Length == 0) return;

            float radius = brushWidth * 0.5f;
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
                    f.vel.y += flowerGravity * dt;
                    f.t.position += f.vel * dt;
                    if (f.vel.y < 0f && !f.groundSampled)
                    {
                        // Sample the landing height where the flower actually comes down — an arc can
                        // carry it over the moat bank or off a ledge, far from the eject point.
                        f.groundY = GroundYAt(f.t.position, f.groundY);
                        f.groundSampled = true;
                    }
                    if (f.t.position.y <= f.groundY && f.vel.y < 0f)
                    {
                        f.t.position = new Vector3(f.t.position.x, f.groundY, f.t.position.z);
                        f.state = StateGrowing;
                        f.growT = 0f;
                        _landedOrder.Enqueue(f);
                    }
                }
                else if (f.state == StateGrowing)
                {
                    f.growT += dt / growSeconds;
                    // Grow-in with a little overshoot, then settle — reads as "sprouting".
                    float k = Mathf.Clamp01(f.growT);
                    float s = k < 0.8f ? Mathf.Lerp(0.35f, 1.15f, k / 0.8f)
                                       : Mathf.Lerp(1.15f, 1f, (k - 0.8f) / 0.2f);
                    f.t.localScale = Vector3.one * (s * f.finalScale);
                    if (k >= 1f) f.state = StateDone;
                }
            }
        }

        // ------------------------------------------------------------------ flowers

        void SpawnFlowerBurst(Vector3 at)
        {
            int count = Random.Range(flowersPerBurst.x, flowersPerBurst.y + 1);
            for (int i = 0; i < count; i++)
            {
                Flower f = GetFlower();
                if (f == null) return;

                Vector3 back = tractor != null ? -tractor.forward : -transform.forward;
                back.y = 0f;
                if (back.sqrMagnitude < 1e-6f) back = Vector3.back;
                back.Normalize();

                float yaw = Random.Range(-ejectYawSpreadDeg * 0.5f, ejectYawSpreadDeg * 0.5f);
                float pitch = Random.Range(ejectPitchRangeDeg.x, ejectPitchRangeDeg.y) * Mathf.Deg2Rad;
                Vector3 dir = Quaternion.AngleAxis(yaw, Vector3.up) * back;
                float speed = Random.Range(ejectSpeedRange.x, ejectSpeedRange.y);

                f.t.position = at + Vector3.up * 0.35f;
                f.t.rotation = Quaternion.Euler(0f, Random.value * 360f, 0f);
                f.t.localScale = Vector3.one * 0.6f;                    // tumbling size while airborne
                f.vel = dir * (Mathf.Cos(pitch) * speed) + Vector3.up * (Mathf.Sin(pitch) * speed);
                f.groundY = at.y;                                       // provisional; re-sampled on descent
                f.groundSampled = false;
                f.finalScale = Random.Range(0.85f, 1.2f);
                f.state = StateFlying;
                f.t.gameObject.SetActive(true);

                _mpb.Clear();
                _mpb.SetColor(BaseColorId, PickColor(at));
                f.r.SetPropertyBlock(_mpb);
            }
        }

        Flower GetFlower()
        {
            foreach (var f in _flowers)
                if (f.state == StatePooled) return f;

            if (_flowers.Count < maxFlowers)
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
            mf.sharedMesh = customFlowerMesh != null ? customFlowerMesh : GetSharedFlowerMesh();
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = GetFlowerMaterial();
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.SetActive(false);

            return new Flower { t = go.transform, r = mr, state = StatePooled };
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

                default: // LogoAuto
                    // Modulo, not a range check: the Loop end-behaviour re-draws the same strokes each
                    // lap without a full ResetVisual, and lap 2+ must keep its colors.
                    if (_strokeIndex >= 0 && strokeColors.Count > 0)
                        return strokeColors[_strokeIndex % strokeColors.Count];
                    return Color.white;
            }
        }

        // ------------------------------------------------------------------ generated flower assets

        Mesh GetSharedFlowerMesh()
        {
            if (_sharedFlowerMesh != null) return _sharedFlowerMesh;

            // A ~30 cm flower: two crossing vertical petal quads (white — tinted per-instance by
            // _BaseColor) on a thin dark-green stem quad. Vertex-colored so one material serves all.
            var verts = new List<Vector3>();
            var cols = new List<Color>();
            var tris = new List<int>();
            var stemColor = new Color(0.16f, 0.35f, 0.12f);

            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
            {
                int i0 = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
                tris.Add(i0); tris.Add(i0 + 1); tris.Add(i0 + 2);
                tris.Add(i0); tris.Add(i0 + 2); tris.Add(i0 + 3);
            }

            const float petalHalf = 0.11f, petalBottom = 0.14f, petalTop = 0.34f, stemHalf = 0.012f;
            // Petal cross (double-sided material, so one quad per plane suffices).
            Quad(new Vector3(-petalHalf, petalBottom, 0f), new Vector3(petalHalf, petalBottom, 0f),
                 new Vector3(petalHalf, petalTop, 0f), new Vector3(-petalHalf, petalTop, 0f), Color.white);
            Quad(new Vector3(0f, petalBottom, -petalHalf), new Vector3(0f, petalBottom, petalHalf),
                 new Vector3(0f, petalTop, petalHalf), new Vector3(0f, petalTop, -petalHalf), Color.white);
            // Stem.
            Quad(new Vector3(-stemHalf, 0f, 0f), new Vector3(stemHalf, 0f, 0f),
                 new Vector3(stemHalf, petalBottom + 0.02f, 0f), new Vector3(-stemHalf, petalBottom + 0.02f, 0f),
                 stemColor);
            Quad(new Vector3(0f, 0f, -stemHalf), new Vector3(0f, 0f, stemHalf),
                 new Vector3(0f, petalBottom + 0.02f, stemHalf), new Vector3(0f, 0f + petalBottom + 0.02f, -stemHalf),
                 stemColor);

            _sharedFlowerMesh = new Mesh { name = "Flower (generated)" };
            _sharedFlowerMesh.SetVertices(verts);
            _sharedFlowerMesh.SetColors(cols);
            _sharedFlowerMesh.SetTriangles(tris, 0);
            _sharedFlowerMesh.RecalculateNormals();
            _sharedFlowerMesh.RecalculateBounds();
            return _sharedFlowerMesh;
        }

        Material GetFlowerMaterial()
        {
            if (_flowerMaterial != null) return _flowerMaterial;
            // Particles/Unlit multiplies vertex color — exactly what the generated mesh needs. Opaque and
            // double-sided so flowers never join the transparent sorting fight.
            Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            _flowerMaterial = new Material(sh) { name = "Flower (runtime)" };
            if (_flowerMaterial.HasProperty("_Cull")) _flowerMaterial.SetFloat("_Cull", 0f);
            if (_flowerMaterial.HasProperty("_BaseColor")) _flowerMaterial.SetColor("_BaseColor", Color.white);
            return _flowerMaterial;
        }
    }
}
