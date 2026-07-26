using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NasaSim
{
    /// <summary>
    /// A field of real, standing grass that the tractor cuts down as it drives — the thing the
    /// <see cref="IMowingVisual"/> interface always said would replace Milestone 1's flat painted ribbon
    /// ("a RenderTexture grass-paint implementation later"). That ribbon is gone: the cut is the mow.
    ///
    /// Two halves:
    ///
    /// <b>The blades.</b> Tens of thousands of them, generated here as a handful of chunk MESHES (not
    /// GameObjects — 1500 scattered clumps already cost this scene 200k lines of YAML, and this needs
    /// fifty times more grass than that). Chunks are rebuilt from a seed, never serialized, so the field
    /// costs nothing in the scene file and re-tuning density is instant. Each blade is a 3-segment tapered
    /// card with its own height, width, yaw, lean and wind phase, planted on the real ground by raycast
    /// and skipped where a stair, pillar or tree trunk is in the way.
    ///
    /// <b>The mow.</b> A single R8 mask texture stretched over the field, painted white along the mower's
    /// swath. The grass shader samples it per blade, at the blade's base, and shrinks that blade to
    /// <c>_MownHeight</c> — so cutting a 42 m field is a few hundred bytes of texture per frame and the
    /// meshes are never touched. The mask is kept CPU-side, which is also what lets <see cref="Mow"/> know
    /// how much grass was ACTUALLY standing where it just cut, and throw that much clipping spray.
    ///
    /// Runs in edit mode too (<see cref="ExecuteAlways"/>) so the field is there to look at and tune
    /// without entering play; <see cref="editorPreviewFraction"/> keeps that preview cheap.
    ///
    /// Wind and the push-aside run on UNSCALED time — grass is scenery the player stands in, and the sim
    /// fast-forwards the mow to 16x (see <see cref="SimulationManager"/>).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class MowableGrass : MonoBehaviour
    {
        [Header("Field")]
        [Tooltip("The ground the grass grows on — its renderer bounds are the field. Auto-finds the " +
                 "biodome's 'Grass' plane when empty.")]
        public Renderer groundRenderer;
        [Tooltip("Used only when no ground renderer is found: a square field of this size around this object.")]
        [Min(1f)] public float fallbackFieldSize = 42f;
        [Tooltip("Keep the blades this far inside the field edge, so none hang over the rim.")]
        [Min(0f)] public float edgeInset = 0.4f;
        [Tooltip("Spacing of the ground/obstacle probe grid (m). The grass follows what this finds, so " +
                 "tighten it for bumpy ground; it costs two physics queries per sample at build time.")]
        [Min(0.15f)] public float groundProbeSpacing = 0.6f;
        [Tooltip("What counts as ground to plant on.")]
        public LayerMask groundMask = ~0;
        [Tooltip("Nothing is planted within this of a structure (stairs, pillars, trunks). 0 disables the check.")]
        [Min(0f)] public float obstacleClearance = 0.3f;

        [Header("Blades")]
        [Tooltip("Blades per square metre. 45 over the shipped 42 m field is ~78,000 blades / ~550k " +
                 "vertices and about a fifth of a second to build at load — comfortable on a desktop GPU. " +
                 "Drop it if the field gets bigger or the frame rate suffers.")]
        [Min(1f)] public float density = 45f;
        [Tooltip("Blade height range (m). The astronaut is only ~0.9 m tall, so 0.3 m is properly shaggy.")]
        public Vector2 bladeHeight = new Vector2(0.17f, 0.30f);
        [Tooltip("Blade width at the root (m). Wider blades cover the ground with fewer of them, but past " +
                 "about a fifth of the height they start reading as leaves rather than grass.")]
        [Min(0.002f)] public float bladeWidth = 0.035f;
        [Tooltip("How far the tip leans off vertical, as a fraction of the blade's height.")]
        [Range(0f, 0.7f)] public float bladeLean = 0.32f;
        [Tooltip("How far a blade may wander inside its grid cell. 0 = a visible lattice.")]
        [Range(0f, 0.5f)] public float placementJitter = 0.45f;
        [Tooltip("Change for a different — but still repeatable — scatter.")]
        public int seed = 20260725;
        [Tooltip("Fraction of Density built while NOT playing. The editor rebuilds the whole field on " +
                 "every script recompile, so the preview is deliberately lighter than the real thing.")]
        [Range(0.05f, 1f)] public float editorPreviewFraction = 0.35f;

        [Header("Mowing")]
        [Tooltip("Mask resolution across the field. 1024 over 42 m is a ~4 cm cut edge.")]
        [Min(64)] public int maskResolution = 1024;
        [Tooltip("Softness of the cut edge, as a fraction of the swath's half-width.")]
        [Range(0f, 1f)] public float cutFeather = 0.4f;
        [Tooltip("Spray clippings out of the deck where grass was actually still standing.")]
        public bool clippings = true;

        [Header("Reacts to")]
        [Tooltip("Whoever walks through the grass and parts it. Auto-finds the astronaut when empty.")]
        public Transform walker;
        [Tooltip("How wide the walker parts the grass (m). 0 turns it off.")]
        [Min(0f)] public float walkerRadius = 0.5f;

        [Header("Look")]
        [Tooltip("Material using NasaSim/Grass. One is created at runtime when empty; " +
                 "Tools > NASA Sim > Grass > Build Mowable Grass makes a project asset you can tune.")]
        public Material material;

        // ------------------------------------------------------------------ shader globals
        static readonly int MaskId = Shader.PropertyToID("_NasaMowMask");
        static readonly int FieldId = Shader.PropertyToID("_NasaMowField");
        static readonly int TimeId = Shader.PropertyToID("_NasaGrassTime");
        static readonly int PusherId = Shader.PropertyToID("_NasaGrassPusher");

        // ------------------------------------------------------------------ state
        /// <summary>Chunks are grown to roughly this across, so frustum culling has something to bite on.</summary>
        const float TargetChunkMetres = 5f;
        /// <summary>7 verts a blade, so this keeps every chunk inside a 16-bit index buffer.</summary>
        const int MaxCellsPerChunk = 90;
        /// <summary>Below this the mask texel counts as uncut — the threshold new-growth is measured against.</summary>
        const byte CutThreshold = 24;

        Vector2 _min, _size;          // field rect in world XZ
        float _probeTopY;             // ray start for the ground probe, above anything on the field
        Transform _container;         // generated, never saved
        readonly List<Mesh> _meshes = new List<Mesh>();
        Material _runtimeMaterial;

        Texture2D _mask;
        byte[] _maskData;
        int _maskRes;                 // what _maskData is actually sized for — NOT the inspector field,
        bool _maskDirty;              // which can change under a run before the rebuild catches up

        float[] _probeY;              // ground height per probe sample
        bool[] _probeOk;              // ...and whether anything may be planted there
        int _probeNX, _probeNZ;
        float _probeStepX, _probeStepZ;

        Transform _tractor, _astronaut;
        readonly RaycastHit[] _rayHits = new RaycastHit[8];
        readonly Collider[] _overlap = new Collider[16];

        bool _built;
        bool _rebuildQueued;
        bool _walkerSearched;
        int _retries;

        ParticleSystem _clippingSpray;
        Material _clippingMaterial;
        ParticleSystem.EmitParams _emit;

        /// <summary>Total blades standing in the field (0 until the first build).</summary>
        public int BladeCount { get; private set; }

        /// <summary>The field rect in world XZ — what the mow mask is stretched over.</summary>
        public Rect FieldRect => new Rect(_min, _size);

        // ------------------------------------------------------------------ lifecycle

        void OnEnable()
        {
            // Built here (not deferred) so opening the scene or recompiling shows the field straight away.
            // If the probe comes up empty — colliders not registered yet during a scene load — Update
            // retries a bounded number of times, then gives up rather than churning every frame.
            _retries = 5;
            _walkerSearched = false;
            Rebuild();
        }

        void OnDisable()
        {
            // Also runs on every script recompile. The generated meshes and the mask are HideAndDontSave,
            // so Unity will not reclaim them for us — dropping them here is what stops a recompile leaking
            // half a million vertices.
            ClearGenerated();
            DisposeMask();
            Shader.SetGlobalTexture(MaskId, Texture2D.blackTexture);
            Shader.SetGlobalVector(PusherId, Vector4.zero);
        }

        void OnValidate()
        {
            // Destroying meshes from OnValidate is illegal, so ask Update to do it.
            if (!isActiveAndEnabled) return;
            _rebuildQueued = true;
            _retries = 5;
            _walkerSearched = false;
        }

        void Update()
        {
            if (_rebuildQueued)
            {
                _rebuildQueued = false;
                Rebuild();
            }
            else if (!_built && _retries > 0)
            {
                _retries--;
                Rebuild();
                if (!_built && _retries == 0)
                    Debug.LogWarning("[MowableGrass] Nothing to plant on — the ground probe found no " +
                                     "surface anywhere in the field. Check Ground Renderer points at the " +
                                     "grass plane, that it has a collider, and that Ground Mask includes " +
                                     "its layer. No grass will grow until this is fixed.", this);
            }

            // Unscaled on purpose: the mow fast-forwards to 16x, the weather does not.
            Shader.SetGlobalFloat(TimeId, Time.realtimeSinceStartup);

            Transform w = ResolveWalker();
            Shader.SetGlobalVector(PusherId, w != null && walkerRadius > 0f
                ? new Vector4(w.position.x, w.position.y, w.position.z, walkerRadius)
                : Vector4.zero);
        }

        void LateUpdate()
        {
            // One upload per frame however many sub-steps the tractor took (it can take dozens at 16x).
            if (!_maskDirty || _mask == null) return;
            _maskDirty = false;
            _mask.SetPixelData(_maskData, 0);
            _mask.Apply(updateMipmaps: false);
        }

        // ------------------------------------------------------------------ mowing

        /// <summary>
        /// Cut the swath swept between two mower positions. A SEGMENT, not a point: the tractor advances up
        /// to 0.25 m per sub-step, and stamping isolated circles scallops the edge of the cut.
        /// </summary>
        /// <returns>Mask texels that were standing grass a moment ago — how much was actually cut.</returns>
        public int Mow(Vector3 from, Vector3 to, float radius)
        {
            if (!_built || _maskData == null) return 0;
            radius = Mathf.Max(0.02f, radius);

            var a = new Vector2(from.x, from.z);
            var b = new Vector2(to.x, to.z);

            float uPerX = _maskRes / Mathf.Max(1e-4f, _size.x);
            float uPerZ = _maskRes / Mathf.Max(1e-4f, _size.y);
            int i0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.x, b.x) - radius - _min.x) * uPerX), 0, _maskRes - 1);
            int i1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.x, b.x) + radius - _min.x) * uPerX), 0, _maskRes - 1);
            int j0 = Mathf.Clamp(Mathf.FloorToInt((Mathf.Min(a.y, b.y) - radius - _min.y) * uPerZ), 0, _maskRes - 1);
            int j1 = Mathf.Clamp(Mathf.CeilToInt((Mathf.Max(a.y, b.y) + radius - _min.y) * uPerZ), 0, _maskRes - 1);

            float inner = radius * (1f - Mathf.Clamp01(cutFeather));
            float band = Mathf.Max(1e-4f, radius - inner);
            float texelX = _size.x / _maskRes;
            float texelZ = _size.y / _maskRes;
            int fresh = 0;

            for (int j = j0; j <= j1; j++)
            {
                float wz = _min.y + (j + 0.5f) * texelZ;
                int row = j * _maskRes;
                for (int i = i0; i <= i1; i++)
                {
                    float wx = _min.x + (i + 0.5f) * texelX;
                    float d = DistanceToSegment(new Vector2(wx, wz), a, b);
                    if (d >= radius) continue;

                    byte v = (byte)Mathf.RoundToInt(Mathf.Clamp01((radius - d) / band) * 255f);
                    int idx = row + i;
                    if (v <= _maskData[idx]) continue;
                    if (_maskData[idx] < CutThreshold && v >= CutThreshold) fresh++;
                    _maskData[idx] = v;
                    _maskDirty = true;
                }
            }

            if (fresh > 0 && clippings) SprayClippings(to, to - from, fresh);
            return fresh;
        }

        /// <summary>Convenience for a single point (a stationary cut).</summary>
        public int Mow(Vector3 at, float radius) => Mow(at, at, radius);

        /// <summary>How mown this spot is, 0 (standing) to 1 (cut). Queryable because the mask is CPU-side.</summary>
        public float MownAt(Vector3 world)
        {
            if (!_built || _maskData == null) return 0f;
            int i = Mathf.FloorToInt((world.x - _min.x) / Mathf.Max(1e-4f, _size.x) * _maskRes);
            int j = Mathf.FloorToInt((world.z - _min.y) / Mathf.Max(1e-4f, _size.y) * _maskRes);
            if (i < 0 || j < 0 || i >= _maskRes || j >= _maskRes) return 0f;
            return _maskData[j * _maskRes + i] / 255f;
        }

        /// <summary>Stand the whole field back up (the run restarted).</summary>
        public void ResetMow()
        {
            if (_maskData == null) return;
            System.Array.Clear(_maskData, 0, _maskData.Length);
            _maskDirty = true;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            return Vector2.Distance(p, a + ab * t);
        }

        // ------------------------------------------------------------------ build

        [ContextMenu("Rebuild Grass")]
        public void Rebuild()
        {
            ClearGenerated();
            _built = false;
            BladeCount = 0;

            if (!ResolveField()) return;
            ResolveIgnoredRoots();
            ProbeGround();
            if (_probeOk == null) return;      // nothing to plant on yet; Update spends a retry on it

            EnsureMask();
            BuildChunks();

            _built = true;
            _retries = 0;
            PushFieldGlobals();
        }

        bool ResolveField()
        {
            if (groundRenderer == null)
            {
                var g = GameObject.Find("Grass");
                if (g != null) g.TryGetComponent(out groundRenderer);
            }

            Bounds b;
            if (groundRenderer != null)
            {
                b = groundRenderer.bounds;
            }
            else
            {
                float h = fallbackFieldSize * 0.5f;
                b = new Bounds(transform.position, new Vector3(h * 2f, 0.1f, h * 2f));
            }

            _min = new Vector2(b.min.x, b.min.z);
            _size = new Vector2(b.size.x, b.size.z);
            _probeTopY = b.max.y + 3f;
            return _size.x > 0.5f && _size.y > 0.5f;
        }

        void ResolveIgnoredRoots()
        {
            var follower = FindAnyObjectByType<TractorPathFollower>();
            _tractor = follower != null ? follower.transform : null;
            var astro = FindAnyObjectByType<AstronautController>();
            _astronaut = astro != null ? astro.transform : null;
        }

        /// <summary>
        /// Sample the ground on a grid: how high it is, and whether anything is standing there. Per-blade
        /// physics would be tens of thousands of queries; on a field this flat a 0.6 m grid is
        /// indistinguishable and costs a few thousand.
        /// </summary>
        void ProbeGround()
        {
            _probeNX = Mathf.Max(2, Mathf.CeilToInt(_size.x / groundProbeSpacing) + 1);
            _probeNZ = Mathf.Max(2, Mathf.CeilToInt(_size.y / groundProbeSpacing) + 1);
            _probeStepX = _size.x / (_probeNX - 1);
            _probeStepZ = _size.y / (_probeNZ - 1);

            int n = _probeNX * _probeNZ;
            var ys = new float[n];
            var ok = new bool[n];
            int hits = 0;

            for (int j = 0; j < _probeNZ; j++)
                for (int i = 0; i < _probeNX; i++)
                {
                    float x = _min.x + i * _probeStepX;
                    float z = _min.y + j * _probeStepZ;
                    int idx = j * _probeNX + i;

                    if (!TryGroundY(x, z, out float y)) continue;
                    ys[idx] = y;
                    ok[idx] = obstacleClearance <= 0f || IsClear(x, y, z);
                    if (ok[idx]) hits++;
                }

            if (hits == 0) { _probeOk = null; return; }
            _probeY = ys;
            _probeOk = ok;
        }

        bool TryGroundY(float x, float z, out float y)
        {
            y = 0f;
            int n = Physics.RaycastNonAlloc(new Ray(new Vector3(x, _probeTopY, z), Vector3.down),
                                            _rayHits, _probeTopY + 30f, groundMask,
                                            QueryTriggerInteraction.Ignore);
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                var h = _rayHits[i];
                if (Ignored(h.collider)) continue;
                if (!any || h.point.y > y) { y = h.point.y; any = true; }
            }
            return any;
        }

        /// <summary>Nothing standing here? The probe floats clear of the ground so the field itself never trips it.</summary>
        bool IsClear(float x, float y, float z)
        {
            var at = new Vector3(x, y + obstacleClearance + 0.05f, z);
            int n = Physics.OverlapSphereNonAlloc(at, obstacleClearance, _overlap, ~0,
                                                  QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!Ignored(_overlap[i])) return false;
            return true;
        }

        /// <summary>
        /// The tractor and the astronaut are standing ON the field when it is built, and they move — a hole
        /// carved under wherever they happened to be parked would be permanent.
        /// </summary>
        bool Ignored(Collider c)
        {
            if (c == null) return true;
            if (c.gameObject.layer == NasaLayers.Player) return true;
            if (_tractor != null && c.transform.IsChildOf(_tractor)) return true;
            if (_astronaut != null && c.transform.IsChildOf(_astronaut)) return true;
            return false;
        }

        bool SampleGround(float x, float z, out float y)
        {
            y = 0f;
            float fx = (x - _min.x) / _probeStepX;
            float fz = (z - _min.y) / _probeStepZ;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, _probeNX - 2);
            int j0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, _probeNZ - 2);
            float tx = Mathf.Clamp01(fx - i0);
            float tz = Mathf.Clamp01(fz - j0);

            // The nearest sample decides whether grass may grow here at all — blending "clear" would let
            // blades creep half a probe cell into a stair.
            int ni = tx < 0.5f ? i0 : i0 + 1;
            int nj = tz < 0.5f ? j0 : j0 + 1;
            if (!_probeOk[nj * _probeNX + ni]) return false;

            // Height, though, is bilinear over whatever neighbours are valid, so a slope stays smooth.
            float sum = 0f, weight = 0f;
            Accumulate(i0, j0, (1f - tx) * (1f - tz), ref sum, ref weight);
            Accumulate(i0 + 1, j0, tx * (1f - tz), ref sum, ref weight);
            Accumulate(i0, j0 + 1, (1f - tx) * tz, ref sum, ref weight);
            Accumulate(i0 + 1, j0 + 1, tx * tz, ref sum, ref weight);
            y = weight > 1e-4f ? sum / weight : _probeY[nj * _probeNX + ni];
            return true;
        }

        void Accumulate(int i, int j, float w, ref float sum, ref float weight)
        {
            if (w <= 0f || i < 0 || j < 0 || i >= _probeNX || j >= _probeNZ) return;
            int idx = j * _probeNX + i;
            if (!_probeOk[idx]) return;
            sum += _probeY[idx] * w;
            weight += w;
        }

        // ------------------------------------------------------------------ blade meshes

        readonly List<Vector3> _vp = new List<Vector3>();
        readonly List<Vector3> _vn = new List<Vector3>();
        readonly List<Color32> _vc = new List<Color32>();
        readonly List<Vector2> _vuv = new List<Vector2>();
        readonly List<Vector3> _vstem = new List<Vector3>();
        readonly List<Vector2> _vside = new List<Vector2>();
        readonly List<int> _vtri = new List<int>();

        /// <summary>Height fractions of the blade's four vertex rows, and their width falloff.</summary>
        static readonly float[] Rows = { 0f, 0.42f, 0.75f, 1f };
        static readonly float[] RowWidth = { 1f, 0.72f, 0.4f, 0f };

        void BuildChunks()
        {
            float effective = Mathf.Max(1f, Application.isPlaying ? density : density * editorPreviewFraction);
            float step = 1f / Mathf.Sqrt(effective);

            float usableX = Mathf.Max(0.5f, _size.x - edgeInset * 2f);
            float usableZ = Mathf.Max(0.5f, _size.y - edgeInset * 2f);
            int cellsX = Mathf.Max(1, Mathf.FloorToInt(usableX / step));
            int cellsZ = Mathf.Max(1, Mathf.FloorToInt(usableZ / step));

            int cpc = Mathf.Clamp(Mathf.RoundToInt(TargetChunkMetres / step), 4, MaxCellsPerChunk);
            int chunksX = Mathf.CeilToInt(cellsX / (float)cpc);
            int chunksZ = Mathf.CeilToInt(cellsZ / (float)cpc);

            EnsureContainer();
            Material mat = ResolveMaterial();
            float originX = _min.x + edgeInset;
            float originZ = _min.y + edgeInset;

            for (int cz = 0; cz < chunksZ; cz++)
                for (int cx = 0; cx < chunksX; cx++)
                    BuildChunk(cx, cz, cpc, cellsX, cellsZ, step, originX, originZ, mat);
        }

        void BuildChunk(int cx, int cz, int cpc, int cellsX, int cellsZ, float step,
                        float originX, float originZ, Material mat)
        {
            int gx0 = cx * cpc, gx1 = Mathf.Min(cellsX, gx0 + cpc);
            int gz0 = cz * cpc, gz1 = Mathf.Min(cellsZ, gz0 + cpc);
            if (gx1 <= gx0 || gz1 <= gz0) return;

            // Chunk origin: the meshes are stored relative to it, so vertex coordinates stay small and the
            // transform keeps identity rotation + unit scale (which the shader relies on — see the header).
            var chunkOrigin = new Vector3(originX + gx0 * step, 0f, originZ + gz0 * step);

            _vp.Clear(); _vn.Clear(); _vc.Clear(); _vuv.Clear(); _vstem.Clear(); _vside.Clear(); _vtri.Clear();
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int gz = gz0; gz < gz1; gz++)
                for (int gx = gx0; gx < gx1; gx++)
                {
                    uint rng = Hash((uint)(gx * 73856093) ^ (uint)(gz * 19349663) ^ (uint)seed);

                    float x = originX + (gx + 0.5f + (Rand(ref rng) - 0.5f) * 2f * placementJitter) * step;
                    float z = originZ + (gz + 0.5f + (Rand(ref rng) - 0.5f) * 2f * placementJitter) * step;
                    if (!SampleGround(x, z, out float y)) continue;

                    float h = Mathf.Lerp(Mathf.Min(bladeHeight.x, bladeHeight.y),
                                         Mathf.Max(bladeHeight.x, bladeHeight.y), Rand(ref rng));
                    float w = bladeWidth * 0.5f * Mathf.Lerp(0.75f, 1.25f, Rand(ref rng));   // half-width
                    float yaw = Rand(ref rng) * Mathf.PI * 2f;
                    float lean = h * bladeLean * Mathf.Lerp(0.4f, 1f, Rand(ref rng));
                    float phase = Rand(ref rng);
                    // Slightly different greens per blade, warmer or cooler, so the field is not one flat
                    // sheet. This rides in a Color32, so it can only ever DARKEN — hence the ceiling below
                    // 1: a range that clipped at white would flatten out most of the variation.
                    float bright = Mathf.Lerp(0.68f, 0.94f, Rand(ref rng));
                    float warm = Mathf.Lerp(-0.06f, 0.06f, Rand(ref rng));

                    AddBlade(new Vector3(x, y, z) - chunkOrigin, h, w, yaw, lean, phase, bright, warm);
                    if (y < minY) minY = y;
                    if (y + h > maxY) maxY = y + h;
                }

            if (_vp.Count == 0) return;

            var mesh = new Mesh
            {
                name = $"GrassChunk_{cx}_{cz}",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = _vp.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.SetVertices(_vp);
            mesh.SetNormals(_vn);
            mesh.SetColors(_vc);
            mesh.SetUVs(0, _vuv);
            mesh.SetUVs(1, _vstem);
            mesh.SetUVs(2, _vside);
            mesh.SetTriangles(_vtri, 0, calculateBounds: false);

            // Set by hand, never recalculated: the shader moves every vertex (wind, the walker), and a
            // chunk that culled against its rest pose would pop at the screen edge.
            float halfX = (gx1 - gx0) * step * 0.5f, halfZ = (gz1 - gz0) * step * 0.5f;
            mesh.bounds = new Bounds(
                new Vector3(halfX, (minY + maxY) * 0.5f - chunkOrigin.y, halfZ),
                new Vector3(halfX * 2f + 1f, Mathf.Max(1f, maxY - minY + 1f), halfZ * 2f + 1f));

            var go = new GameObject(mesh.name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(_container, worldPositionStays: false);
            go.transform.SetPositionAndRotation(chunkOrigin, Quaternion.identity);
            go.transform.localScale = Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;

            _meshes.Add(mesh);
            BladeCount += _vp.Count / 7;
        }

        /// <summary>
        /// One 3-segment tapered blade: 7 vertices, 5 triangles. Each vertex stores its offset from the
        /// blade's base split into the STEM (height + lean) and the SIDE (half-width), which is what lets
        /// the shader cut the blade down without also making it thinner. See NasaSimGrass.shader.
        /// </summary>
        void AddBlade(Vector3 basePos, float height, float halfWidth, float yaw, float lean,
                      float phase, float bright, float warm)
        {
            int v0 = _vp.Count;
            float sy = Mathf.Sin(yaw), cy = Mathf.Cos(yaw);
            var forward = new Vector3(sy, 0f, cy);          // the way the blade leans
            var side = new Vector3(cy, 0f, -sy);            // across the blade

            var tint = new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt((bright + warm) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(bright * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((bright - warm) * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(phase * 255f), 0, 255));

            for (int r = 0; r < Rows.Length; r++)
            {
                float v = Rows[r];
                Vector3 stem = Vector3.up * (height * v) + forward * (lean * v * v);
                // Tangent of that curve, so the face normal follows the bend instead of the rest pose.
                Vector3 tangent = (Vector3.up * height + forward * (2f * lean * v)).normalized;
                Vector3 normal = Vector3.Cross(side, tangent).normalized;
                Vector3 offset = side * (halfWidth * RowWidth[r]);

                int count = r == Rows.Length - 1 ? 1 : 2;   // the tip is a single vertex
                for (int s = 0; s < count; s++)
                {
                    float sign = s == 0 ? -1f : 1f;
                    Vector3 sideOff = offset * sign;
                    _vp.Add(basePos + stem + sideOff);
                    _vn.Add(normal);
                    _vc.Add(tint);
                    _vuv.Add(new Vector2(s == 0 ? 0f : 1f, v));
                    _vstem.Add(stem);
                    _vside.Add(new Vector2(sideOff.x, sideOff.z));
                }
            }

            // Rows 0-1 and 1-2 are quads, row 2 -> tip is a triangle. Winding is free: the pass is Cull Off
            // and the normals are supplied above.
            AddQuad(v0 + 0, v0 + 1, v0 + 2, v0 + 3);
            AddQuad(v0 + 2, v0 + 3, v0 + 4, v0 + 5);
            _vtri.Add(v0 + 4); _vtri.Add(v0 + 6); _vtri.Add(v0 + 5);
        }

        void AddQuad(int bl, int br, int tl, int tr)
        {
            _vtri.Add(bl); _vtri.Add(tl); _vtri.Add(br);
            _vtri.Add(br); _vtri.Add(tl); _vtri.Add(tr);
        }

        // ------------------------------------------------------------------ mask, material, container

        void EnsureMask()
        {
            int res = Mathf.Clamp(Mathf.ClosestPowerOfTwo(maskResolution), 64, 4096);
            maskResolution = res;

            if (_mask == null || _mask.width != res)
            {
                Kill(_mask);
                _mask = new Texture2D(res, res, TextureFormat.R8, mipChain: false, linear: true)
                {
                    name = "MowMask",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                _maskData = new byte[res * res];
            }
            _maskRes = res;

            System.Array.Clear(_maskData, 0, _maskData.Length);
            _mask.SetPixelData(_maskData, 0);
            _mask.Apply(updateMipmaps: false);
            _maskDirty = false;
        }

        void DisposeMask()
        {
            Kill(_mask);
            _mask = null;
            _maskDirty = false;
        }

        void PushFieldGlobals()
        {
            Shader.SetGlobalTexture(MaskId, _mask != null ? (Texture)_mask : Texture2D.blackTexture);
            Shader.SetGlobalVector(FieldId, new Vector4(_min.x, _min.y,
                                                        1f / Mathf.Max(1e-4f, _size.x),
                                                        1f / Mathf.Max(1e-4f, _size.y)));
        }

        Material ResolveMaterial()
        {
            if (material != null) return material;
            if (_runtimeMaterial != null) return _runtimeMaterial;

            Shader sh = Shader.Find("NasaSim/Grass");
            if (sh == null)
            {
                Debug.LogWarning("[MowableGrass] NasaSim/Grass shader not found — the field will render " +
                                 "with a flat fallback and will not react to the mower.", this);
                sh = Shader.Find("Universal Render Pipeline/Unlit");
            }
            _runtimeMaterial = new Material(sh)
            {
                name = "GrassBlades (runtime)",
                hideFlags = HideFlags.HideAndDontSave,
            };
            return _runtimeMaterial;
        }

        void EnsureContainer()
        {
            if (_container != null) return;
            var go = new GameObject("MowableGrass (generated)") { hideFlags = HideFlags.DontSave };
            _container = go.transform;
            // World origin, identity: the chunk transforms below must stay unrotated and unscaled.
            _container.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        /// <summary>Searched at most once per build — this is called every frame.</summary>
        Transform ResolveWalker()
        {
            if (walker != null || _walkerSearched) return walker;
            _walkerSearched = true;
            var astro = FindAnyObjectByType<AstronautController>();
            if (astro != null) walker = astro.transform;
            return walker;
        }

        void ClearGenerated()
        {
            if (_container != null) Kill(_container.gameObject);
            _container = null;
            foreach (var m in _meshes) Kill(m);
            _meshes.Clear();
            Kill(_runtimeMaterial);
            _runtimeMaterial = null;
            if (_clippingSpray != null) Kill(_clippingSpray.gameObject);
            _clippingSpray = null;
            Kill(_clippingMaterial);
            _clippingMaterial = null;
            BladeCount = 0;
        }

        static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        // ------------------------------------------------------------------ clippings

        /// <summary>
        /// Green confetti out of the deck, sized by how much grass was actually standing where the blade
        /// just passed — so a second pass over ground already cut throws nothing, which is what tells the
        /// player the field is finished.
        /// </summary>
        void SprayClippings(Vector3 at, Vector3 travel, int cutTexels)
        {
            if (!Application.isPlaying) return;
            var ps = EnsureClippingSpray();
            if (ps == null) return;

            int count = Mathf.Clamp(cutTexels / 20, 1, 8);
            Vector3 back = travel.sqrMagnitude > 1e-6f ? -travel.normalized : Vector3.back;
            Vector3 side = Vector3.Cross(Vector3.up, back);

            for (int i = 0; i < count; i++)
            {
                _emit.position = at + Vector3.up * 0.06f;
                _emit.velocity = back * Random.Range(0.5f, 1.4f)
                                 + Vector3.up * Random.Range(0.8f, 1.8f)
                                 + side * Random.Range(-0.6f, 0.6f);
                _emit.startLifetime = Random.Range(0.35f, 0.8f);
                _emit.startSize = Random.Range(0.015f, 0.04f);
                _emit.startColor = Color.Lerp(new Color(0.30f, 0.52f, 0.16f),
                                              new Color(0.55f, 0.70f, 0.24f), Random.value);
                ps.Emit(_emit, 1);
            }
        }

        ParticleSystem EnsureClippingSpray()
        {
            if (_clippingSpray != null) return _clippingSpray;

            var go = new GameObject("GrassClippings") { hideFlags = HideFlags.DontSave };
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var ps = go.AddComponent<ParticleSystem>();

            var main = ps.main;
            // Looping with emission OFF: the system runs forever but never emits on its own, so the
            // particles SprayClippings hands it are always simulated. A one-shot system would stop after
            // its duration and silently swallow everything after that.
            main.loop = true;
            main.playOnAwake = false;
            // World space and SCALED time, like the flowers: the clippings belong to the mow spectacle,
            // which is what the 16x fast-forward is speeding up.
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0.6f;
            main.startSpeed = 0f;
            main.startLifetime = 0.6f;
            main.maxParticles = 400;

            var emission = ps.emission;
            emission.enabled = false;      // emitted by hand from SprayClippings
            var shape = ps.shape;
            shape.enabled = false;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            _clippingMaterial = new Material(sh)
            {
                name = "GrassClippings (runtime)",
                hideFlags = HideFlags.HideAndDontSave,
            };
            renderer.sharedMaterial = _clippingMaterial;

            ps.Play();
            _clippingSpray = ps;
            return ps;
        }

        // ------------------------------------------------------------------ deterministic scatter

        static uint Hash(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352du;
            x ^= x >> 15; x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        /// <summary>0..1 from a cheap LCG — Unity's Random would drag the global stream into the scatter.</summary>
        static float Rand(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) * (1f / 16777216f);
        }

        void OnDrawGizmosSelected()
        {
            if (_size.x <= 0f) return;
            Gizmos.color = new Color(0.4f, 0.9f, 0.3f, 0.8f);
            Gizmos.DrawWireCube(new Vector3(_min.x + _size.x * 0.5f, _probeTopY - 3f, _min.y + _size.y * 0.5f),
                                new Vector3(_size.x, 0.02f, _size.y));
        }
    }
}
