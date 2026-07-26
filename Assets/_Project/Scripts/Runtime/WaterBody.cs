using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// A pond, or the moat. One component owns the whole thing: the numbers that define its outline, the
    /// water sheet built from them, the mud basin under it, and the region the ducks and fish are allowed
    /// to swim in.
    ///
    /// <b>It is meant to be dragged around.</b> Every mesh is regenerated the moment a field changes — in
    /// the editor, without pressing Play — and nothing is saved to disk, so resizing the moat to fit the
    /// container model when it arrives is a matter of typing a new number (or hauling the handles in the
    /// Scene view; see WaterBodyEditor). Move, rotate or scale the transform and the water, the basin and
    /// the wildlife all follow, because every query below runs in this object's local space.
    ///
    /// Four outlines, all the same rounded rectangle underneath (see <see cref="WaterMeshBuilder"/>):
    /// <list type="bullet">
    /// <item><b>RectRing</b> — the square moat: a band around a square hole, sized to run just outside the
    /// grass patch.</item>
    /// <item><b>Circle</b> — a round pond, for the ones dotted outside the moat.</item>
    /// <item><b>Annulus</b> — a round moat (what the first pass built).</item>
    /// <item><b>Box</b> — a rectangular pond.</item>
    /// </list>
    ///
    /// The object sits AT the water surface, so <c>transform.position.y</c> is the waterline;
    /// <see cref="SurfaceHeightAt"/> adds the shader's own swell on top of it, which is what lets a duck
    /// ride the actual wave it is floating on rather than a bob of its own invention.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class WaterBody : MonoBehaviour
    {
        // Order is frozen: Box/Annulus were 0/1 in the first pass and scenes serialise the index.
        public enum Shape { Box = 0, Annulus = 1, RectRing = 2, Circle = 3 }

        [Tooltip("RectRing = the square moat. Circle = a round pond. Annulus = a round moat. " +
                 "Box = a rectangular pond.")]
        public Shape shape = Shape.RectRing;

        [Header("Square moat")]
        [Tooltip("The dry square the moat runs around — set it a little larger than the grass patch. " +
                 "The water starts here.")]
        public Vector2 innerSize = new Vector2(46f, 46f);
        [Tooltip("How wide the water band is (m). The outer edge is Inner Size + 2 x this.")]
        [Min(0.2f)] public float bandWidth = 3.5f;
        [Tooltip("Rounds the moat's corners (m). 0 = a sharp square.")]
        [Min(0f)] public float cornerRadius = 0f;

        [Header("Round pond")]
        [Min(0.2f)] public float radius = 6f;

        [Header("Round moat")]
        [Min(0f)] public float innerRadius = 27f;
        [Min(0.1f)] public float outerRadius = 31f;

        [Header("Rectangular pond")]
        [Tooltip("X/Z extents of the pond (Y is ignored).")]
        public Vector3 boxSize = new Vector3(8f, 0f, 8f);

        [Header("Basin")]
        [Tooltip("How far the floor sits below the surface (m). Fish swim between the surface and this.")]
        [Min(0.05f)] public float depth = 0.8f;
        [Tooltip("Generates the mud trench and its collider, so the banks are solid and the water reads " +
                 "as depth. Turn OFF once the modelled container is in place under the water.")]
        public bool buildBasin = true;
        [Tooltip("How far the bank slopes out past the waterline (m).")]
        [Min(0.05f)] public float bankWidth = 0.6f;
        [Tooltip("Material for the generated basin. Set by the Create Water Body tool; swap it for " +
                 "whatever the container model uses.")]
        public Material basinMaterial;

        [Header("Mesh")]
        [Tooltip("Vertices per metre. The wave shader moves vertices, so too few makes the swell " +
                 "faceted; 2 is plenty for a moat you walk past.")]
        [Range(0.5f, 4f)] public float meshDensity = 2f;

        /// <summary>World Y of the still waterline (the swell rides on top of it).</summary>
        public float SurfaceY => transform.position.y;

        public bool IsRing => shape == Shape.RectRing || shape == Shape.Annulus;

        /// <summary>Widest half-extent of the water itself, in local units — used to frame and to sample.</summary>
        public Vector2 OuterHalf
        {
            get
            {
                switch (shape)
                {
                    case Shape.RectRing: return innerSize * 0.5f + Vector2.one * bandWidth;
                    case Shape.Annulus:  return Vector2.one * Mathf.Max(outerRadius, innerRadius + 0.1f);
                    case Shape.Circle:   return Vector2.one * radius;
                    default:             return new Vector2(boxSize.x, boxSize.z) * 0.5f;
                }
            }
        }

        float OuterCorner
        {
            get
            {
                switch (shape)
                {
                    case Shape.RectRing: return cornerRadius > 0f ? cornerRadius + bandWidth : 0f;
                    case Shape.Annulus:  return Mathf.Max(outerRadius, innerRadius + 0.1f);
                    case Shape.Circle:   return radius;
                    default:             return 0f;
                }
            }
        }

        Vector2 InnerHalf => shape == Shape.RectRing ? innerSize * 0.5f
                           : shape == Shape.Annulus  ? Vector2.one * innerRadius
                           : Vector2.zero;

        float InnerCorner => shape == Shape.RectRing ? Mathf.Min(cornerRadius, Mathf.Min(innerSize.x, innerSize.y) * 0.5f)
                           : shape == Shape.Annulus  ? innerRadius
                           : 0f;

        /// <summary>Half the band's width — the most a point can be pushed before it leaves the water.</summary>
        float MaxMargin => IsRing
            ? Mathf.Max(0.02f, (shape == Shape.RectRing ? bandWidth : outerRadius - innerRadius) * 0.4f)
            : Mathf.Max(0.02f, Mathf.Min(OuterHalf.x, OuterHalf.y) * 0.4f);

        float HorizontalScale
        {
            get
            {
                Vector3 s = transform.lossyScale;
                return Mathf.Max(0.01f, (Mathf.Abs(s.x) + Mathf.Abs(s.z)) * 0.5f);
            }
        }

        // ------------------------------------------------------------------ region queries

        /// <summary>Is this world point in the water (kept <paramref name="margin"/> m off the banks)?</summary>
        public bool Contains(Vector3 world, float margin = 0f) => ContainsLocal(ToLocal(world), LocalMargin(margin));

        bool ContainsLocal(Vector2 p, float localMargin)
        {
            if (SignedDistance(p, false) > -localMargin) return false;
            return !IsRing || SignedDistance(p, true) >= localMargin;
        }

        /// <summary>
        /// Uniform random point on the surface, kept a margin off the banks. Rejection-sampled, which
        /// stays uniform whatever the outline is — a thin moat band included.
        /// </summary>
        public Vector3 RandomPointOnSurface(float margin = 0.5f)
        {
            Vector2 half = OuterHalf;
            float m = LocalMargin(margin);
            Vector2 p = Vector2.zero;
            for (int i = 0; i < 48; i++)
            {
                p = new Vector2(Random.Range(-half.x, half.x), Random.Range(-half.y, half.y));
                if (ContainsLocal(p, m)) return ToWorld(p, SurfaceY);
            }
            // Very thin band + unlucky draws: shove the last sample into the water instead.
            return ClampInside(ToWorld(p, SurfaceY), margin);
        }

        /// <summary>Pull a world position back into the water horizontally (its Y is left alone).</summary>
        public Vector3 ClampInside(Vector3 world, float margin = 0.5f)
        {
            Vector2 p = ToLocal(world);
            float m = LocalMargin(margin);

            if (SignedDistance(p, false) > -m) p = ProjectTo(p, false, -m);
            if (IsRing && SignedDistance(p, true) < m) p = ProjectTo(p, true, m);

            return ToWorld(p, world.y);
        }

        /// <summary>
        /// Signed distance to the inner or outer outline: negative inside it, in local units. Exact for a
        /// rounded rectangle, which is every shape here.
        /// </summary>
        float SignedDistance(Vector2 p, bool inner)
        {
            Vector2 half = inner ? InnerHalf : OuterHalf;
            float r = WaterMeshBuilder.ClampRadius(half, inner ? InnerCorner : OuterCorner);
            Vector2 d = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - (half - Vector2.one * r);
            float outside = new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f)).magnitude;
            return outside + Mathf.Min(Mathf.Max(d.x, d.y), 0f) - r;
        }

        /// <summary>Walk a point onto the outline's <paramref name="target"/> level set, along the gradient.</summary>
        Vector2 ProjectTo(Vector2 p, bool inner, float target)
        {
            const float eps = 0.02f;
            for (int i = 0; i < 3; i++)
            {
                float d = SignedDistance(p, inner);
                if (Mathf.Abs(d - target) < 1e-3f) break;
                var grad = new Vector2(SignedDistance(p + Vector2.right * eps, inner) - d,
                                       SignedDistance(p + Vector2.up * eps, inner) - d);
                if (grad.sqrMagnitude < 1e-8f) { p += Vector2.right * (target - d); break; }
                p -= grad.normalized * (d - target);
            }
            return p;
        }

        Vector2 ToLocal(Vector3 world)
        {
            Vector3 l = transform.InverseTransformPoint(world);
            return new Vector2(l.x, l.z);
        }

        Vector3 ToWorld(Vector2 local, float worldY)
        {
            Vector3 w = transform.TransformPoint(new Vector3(local.x, 0f, local.y));
            w.y = worldY;
            return w;
        }

        float LocalMargin(float margin) => Mathf.Min(margin, MaxMargin * HorizontalScale) / HorizontalScale;

        // ------------------------------------------------------------------ the swell

        // Mirrors NasaSimWater.shader's WaveHeightAndNormal exactly: three summed directional sines in
        // WORLD xz. Ducks read it instead of inventing their own bob, so they sit on the wave that is
        // actually drawn under them — and retuning the material's waves retunes the bobbing for free.
        static readonly Vector2 Dir0 = new Vector2(0.80f, 0.60f);
        static readonly Vector2 Dir1 = new Vector2(-0.62f, 0.78f);
        static readonly Vector2 Dir2 = new Vector2(0.35f, -0.94f);

        float _waveAmp = 0.035f, _waveFreq = 1f, _waveSpeed = 1f;

        /// <summary>World height of the water surface under a point, swell included.</summary>
        public float SurfaceHeightAt(Vector3 world) => SurfaceY + Wave(world, out _);

        /// <summary>Which way the surface is tilted under a point — a floating duck leans with it.</summary>
        public Vector3 SurfaceNormalAt(Vector3 world)
        {
            Wave(world, out Vector3 n);
            return n;
        }

        float Wave(Vector3 world, out Vector3 normal)
        {
            float t = WaterSurface.Clock * _waveSpeed;
            var xz = new Vector2(world.x, world.z);
            float a0 = _waveAmp, a1 = _waveAmp * 0.5f, a2 = _waveAmp * 0.25f;
            float w0 = 0.9f * _waveFreq, w1 = 1.6f * _waveFreq, w2 = 2.6f * _waveFreq;

            float p0 = Vector2.Dot(Dir0, xz) * w0 + t * 1.00f;
            float p1 = Vector2.Dot(Dir1, xz) * w1 + t * 1.35f;
            float p2 = Vector2.Dot(Dir2, xz) * w2 + t * 1.70f;

            float dhdx = a0 * w0 * Dir0.x * Mathf.Cos(p0) + a1 * w1 * Dir1.x * Mathf.Cos(p1)
                       + a2 * w2 * Dir2.x * Mathf.Cos(p2);
            float dhdz = a0 * w0 * Dir0.y * Mathf.Cos(p0) + a1 * w1 * Dir1.y * Mathf.Cos(p1)
                       + a2 * w2 * Dir2.y * Mathf.Cos(p2);
            normal = new Vector3(-dhdx, 1f, -dhdz).normalized;

            return a0 * Mathf.Sin(p0) + a1 * Mathf.Sin(p1) + a2 * Mathf.Sin(p2);
        }

        void ReadWaveSettings()
        {
            var mat = _renderer != null ? _renderer.sharedMaterial : null;
            if (mat == null || !mat.HasProperty(WaveAmpId)) return;
            _waveAmp = mat.GetFloat(WaveAmpId);
            _waveFreq = mat.GetFloat(WaveFreqId);
            _waveSpeed = mat.GetFloat(WaveSpeedId);
        }

        static readonly int WaveAmpId = Shader.PropertyToID("_WaveAmp");
        static readonly int WaveFreqId = Shader.PropertyToID("_WaveFreq");
        static readonly int WaveSpeedId = Shader.PropertyToID("_WaveSpeed");

        // ------------------------------------------------------------------ generated geometry

        [System.NonSerialized] Mesh _surfaceMesh, _basinMesh;
        [System.NonSerialized] MeshRenderer _renderer;
        [System.NonSerialized] bool _dirty = true;
        [System.NonSerialized] Vector3 _builtScale;

        void OnEnable() { _dirty = true; }
        void Start() { Rebuild(); }
        void OnValidate() { _dirty = true; }          // deferred: OnValidate can't safely spawn the basin

        void Update()
        {
            // The transform's scale is baked into nothing — but a squashed sheet would make the waves
            // stretch, so a rescale is worth a rebuild too.
            if (transform.lossyScale != _builtScale) _dirty = true;
            if (_dirty) Rebuild();
            ReadWaveSettings();
        }

        /// <summary>
        /// Regenerate the water sheet and the basin from the current numbers. Cheap (a few thousand
        /// vertices) and called on every edit, so the Scene view always shows the real thing.
        /// </summary>
        public void Rebuild()
        {
            _dirty = false;
            _builtScale = transform.lossyScale;
            innerSize = new Vector2(Mathf.Max(0.2f, innerSize.x), Mathf.Max(0.2f, innerSize.y));
            outerRadius = Mathf.Max(outerRadius, innerRadius + 0.2f);
            cornerRadius = Mathf.Clamp(cornerRadius, 0f, Mathf.Min(innerSize.x, innerSize.y) * 0.5f);

            Vector2 outerHalf = OuterHalf;
            float outerCorner = OuterCorner;
            // One topology for every loop, sized off the widest (the basin's outer lip).
            var topo = WaterMeshBuilder.TopologyFor(outerHalf + Vector2.one * bankWidth,
                                                    outerCorner + bankWidth, meshDensity);

            Vector2[] outer = WaterMeshBuilder.Loop(outerHalf, outerCorner, topo);
            Vector2[] inner = IsRing ? WaterMeshBuilder.Loop(InnerHalf, InnerCorner, topo) : null;

            _surfaceMesh = BuildSurface(inner, outer);
            _surfaceMesh.hideFlags = HideFlags.DontSave;

            var filter = GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = _surfaceMesh;
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            ReadWaveSettings();

            RebuildBasin(outerHalf, outerCorner, topo);
        }

        Mesh BuildSurface(Vector2[] inner, Vector2[] outer)
        {
            if (_surfaceMesh != null) DestroyMesh(ref _surfaceMesh);

            switch (shape)
            {
                case Shape.RectRing:
                case Shape.Annulus:
                {
                    float width = shape == Shape.RectRing ? bandWidth : outerRadius - innerRadius;
                    int rings = Mathf.Clamp(Mathf.RoundToInt(width * meshDensity) + 1, 2, 48);
                    return WaterMeshBuilder.BuildBand(inner, outer, rings, "WaterSurface");
                }
                case Shape.Circle:
                {
                    int rings = Mathf.Clamp(Mathf.RoundToInt(radius * meshDensity) + 1, 2, 64);
                    return WaterMeshBuilder.BuildFilled(outer, rings, "WaterSurface");
                }
                default:
                    return WaterMeshBuilder.BuildGrid(new Vector2(boxSize.x, boxSize.z),
                                                      meshDensity, "WaterSurface");
            }
        }

        void RebuildBasin(Vector2 outerHalf, float outerCorner, in WaterMeshBuilder.Topology topo)
        {
            var existing = transform.Find("Basin");
            if (!buildBasin)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            if (_basinMesh != null) DestroyMesh(ref _basinMesh);
            float floorY = -depth;
            float lipY = 0.06f;                        // a hair above the waterline, so no seam shows
            float shelf = Mathf.Min(0.3f, depth * 0.4f);

            if (IsRing)
            {
                Vector2 innerHalf = InnerHalf;
                float innerCorner = InnerCorner;
                // The inner lip eats into the dry middle, so never let it eat past the middle's centre.
                float innerLip = Mathf.Min(bankWidth, Mathf.Min(innerHalf.x, innerHalf.y) * 0.4f);
                _basinMesh = WaterMeshBuilder.BuildTrough(
                    WaterMeshBuilder.Loop(innerHalf - Vector2.one * innerLip, innerCorner - innerLip, topo),
                    WaterMeshBuilder.Loop(innerHalf + Vector2.one * shelf, innerCorner + shelf, topo),
                    WaterMeshBuilder.Loop(outerHalf - Vector2.one * shelf, outerCorner - shelf, topo),
                    WaterMeshBuilder.Loop(outerHalf + Vector2.one * bankWidth, outerCorner + bankWidth, topo),
                    lipY, floorY, "WaterBasin");
            }
            else
            {
                _basinMesh = WaterMeshBuilder.BuildTub(
                    WaterMeshBuilder.Loop(outerHalf - Vector2.one * shelf, outerCorner - shelf, topo),
                    WaterMeshBuilder.Loop(outerHalf + Vector2.one * bankWidth, outerCorner + bankWidth, topo),
                    lipY, floorY, "WaterBasin");
            }
            _basinMesh.hideFlags = HideFlags.DontSave;

            if (existing == null)
            {
                var go = new GameObject("Basin");
                go.transform.SetParent(transform, worldPositionStays: false);
                existing = go.transform;
            }
            existing.gameObject.SetActive(true);
            existing.gameObject.layer = 0;                       // solid ground, not Water
            existing.localPosition = Vector3.zero;
            existing.localRotation = Quaternion.identity;
            existing.localScale = Vector3.one;

            GetOrAdd<MeshFilter>(existing.gameObject).sharedMesh = _basinMesh;

            var br = GetOrAdd<MeshRenderer>(existing.gameObject);
            br.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (basinMaterial != null) br.sharedMaterial = basinMaterial;

            var bc = GetOrAdd<MeshCollider>(existing.gameObject);
            bc.convex = false;
            bc.sharedMesh = null;                                // force the collider to re-cook
            bc.sharedMesh = _basinMesh;
        }

        void OnDisable()
        {
            DestroyMesh(ref _surfaceMesh);
            DestroyMesh(ref _basinMesh);
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        static void DestroyMesh(ref Mesh mesh)
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
            mesh = null;
        }

        // ------------------------------------------------------------------ wildlife

        /// <summary>
        /// Put every duck and fish that lives here back in the water. Resizing a pond can leave them
        /// standing on the lawn, so the editor calls this after a rebuild and each animal also checks
        /// itself on Start.
        /// </summary>
        public void SnapWildlifeInside()
        {
            foreach (var w in GetComponentsInChildren<WaterWanderer>(true))
            {
                if (w.water == null) w.water = this;
                if (w.water != this) continue;
                w.SnapIntoWater();
            }
        }

        // ------------------------------------------------------------------ gizmos

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.9f);
            var topo = WaterMeshBuilder.TopologyFor(OuterHalf, OuterCorner, 0.8f);
            DrawLoop(WaterMeshBuilder.Loop(OuterHalf, OuterCorner, topo));
            if (IsRing) DrawLoop(WaterMeshBuilder.Loop(InnerHalf, InnerCorner, topo));
        }

        void DrawLoop(Vector2[] loop)
        {
            if (loop == null || loop.Length < 2) return;
            Vector3 prev = ToWorld(loop[loop.Length - 1], SurfaceY);
            foreach (var p in loop)
            {
                Vector3 w = ToWorld(p, SurfaceY);
                Gizmos.DrawLine(prev, w);
                prev = w;
            }
        }
    }
}
