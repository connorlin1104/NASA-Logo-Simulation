using System.Collections.Generic;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds the three hero snacks — one apple, one carrot, one lettuce — as high-poly meshes with
    /// procedural skins.
    ///
    /// <b>Why generate them instead of using the greenhouse crops.</b> Every plant in SettingEnvo.fbx was
    /// decimated hard, and a decimated carrot is fine in a bed forty metres away and awful held up against
    /// the visor. These three are the only produce the camera ever gets close to, so they are the only
    /// three worth spending polygons on: roughly nine thousand triangles for the apple, fourteen for the
    /// carrot and twelve for the lettuce. That is nothing for one object each, and it is the difference
    /// between a snack and a faceted lump.
    ///
    /// <b>Normals come from the position field, not from RecalculateNormals.</b> Every surface here is a
    /// grid — a lathe, a leaf blade — so the tangents are two finite differences and the normal is their
    /// cross product. That keeps a ridged carrot, a ruffled lettuce leaf and the seam of a lathe perfectly
    /// smooth, which averaging face normals across a duplicated seam cannot do.
    ///
    /// Nothing here touches the scene or the AssetDatabase; <see cref="SnackTool"/> owns all of that.
    /// </summary>
    public static class SnackMeshBuilder
    {
        // Submesh order, which is also the material order on the renderer.
        public const int AppleSkin = 0, AppleStem = 1, AppleLeaf = 2;
        public const int CarrotRoot = 0, CarrotLeaf = 1;
        public const int LettuceLeaf = 0, LettuceCore = 1;

        // ==================================================================== accumulator

        sealed class Build
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            readonly List<List<int>> _sub = new List<List<int>>();

            public int Vertex(Vector3 p, Vector3 n, Vector2 uv)
            {
                V.Add(p); N.Add(n); UV.Add(uv);
                return V.Count - 1;
            }

            public void Tri(int sub, int a, int b, int c)
            {
                while (_sub.Count <= sub) _sub.Add(new List<int>());
                List<int> t = _sub[sub];
                t.Add(a); t.Add(b); t.Add(c);
            }

            public void Quad(int sub, int a, int b, int c, int d)
            {
                Tri(sub, a, b, c);
                Tri(sub, a, c, d);
            }

            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                // A lettuce is comfortably under 65k, but the format is decided by the data rather than by
                // a hope, because a slider in the tool can push any of these past it.
                m.indexFormat = V.Count > 65000
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                m.SetVertices(V);
                m.SetNormals(N);
                m.SetUVs(0, UV);
                m.subMeshCount = Mathf.Max(1, _sub.Count);
                for (int i = 0; i < _sub.Count; i++) m.SetTriangles(_sub[i], i);
                m.RecalculateTangents();
                m.RecalculateBounds();
                m.UploadMeshData(false);
                return m;
            }
        }

        // ==================================================================== grid emitter

        /// <summary>
        /// Lay down one quad grid, <paramref name="nu"/> across by <paramref name="nv"/> along.
        ///
        /// The normal at every vertex is cross(dP/dv, dP/du) sampled off the position function itself, so
        /// whatever the surface is actually doing — a ridge, a bend, a ruffle — the shading follows it
        /// exactly. <paramref name="wrapU"/> closes the grid into a ring and emits one extra column that
        /// repeats column zero's position and normal at u = 1, which is what gives a lathe a seamless
        /// surface and an unbroken texture at the same time.
        ///
        /// A row that collapses to a point — the well at the top of an apple, the tip of a carrot — has no
        /// usable cross product there; it inherits the nearest row that has one, walking the column from
        /// both ends.
        /// </summary>
        static void Grid(Build b, int sub, int nu, int nv, bool wrapU, bool twoSided,
                         System.Func<int, int, Vector3> pos,
                         System.Func<float, float, Vector2> uvMap = null)
        {
            Vector3 Du(int i, int j)
            {
                if (wrapU) return pos((i + 1) % nu, j) - pos((i - 1 + nu) % nu, j);
                return pos(Mathf.Min(i + 1, nu - 1), j) - pos(Mathf.Max(i - 1, 0), j);
            }
            Vector3 Dv(int i, int j) =>
                pos(i, Mathf.Min(j + 1, nv - 1)) - pos(i, Mathf.Max(j - 1, 0));

            var normal = new Vector3[nu, nv];
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    Vector3 n = Vector3.Cross(Dv(i, j), Du(i, j));
                    normal[i, j] = n.sqrMagnitude > 1e-16f ? n.normalized : Vector3.zero;
                }

            for (int i = 0; i < nu; i++)
            {
                Vector3 last = Vector3.zero;
                for (int j = 0; j < nv; j++)
                    if (normal[i, j] == Vector3.zero) normal[i, j] = last; else last = normal[i, j];
                last = Vector3.zero;
                for (int j = nv - 1; j >= 0; j--)
                    if (normal[i, j] == Vector3.zero) normal[i, j] = last; else last = normal[i, j];
                for (int j = 0; j < nv; j++)
                    if (normal[i, j] == Vector3.zero) normal[i, j] = Vector3.up;
            }

            int cols = wrapU ? nu + 1 : nu;
            int start = b.V.Count;
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < cols; i++)
                {
                    int src = wrapU ? i % nu : i;
                    float u = cols > 1 ? i / (float)(cols - 1) : 0f;
                    float v = nv > 1 ? j / (float)(nv - 1) : 0f;
                    b.Vertex(pos(src, j), normal[src, j], uvMap != null ? uvMap(u, v) : new Vector2(u, v));
                }

            // Wound so the triangle's own normal agrees with the vertex normals above: cross(dv, du) means
            // the first edge has to run ALONG v. Getting this backwards leaves a snack you can only see
            // from the inside.
            for (int j = 0; j < nv - 1; j++)
                for (int i = 0; i < cols - 1; i++)
                {
                    int a = start + j * cols + i;
                    b.Quad(sub, a, a + cols, a + cols + 1, a + 1);
                }

            if (!twoSided) return;

            // Leaves are surfaces, not solids. The back is a second copy at the same positions with the
            // normals and the winding flipped — coincident is fine and cannot z-fight, because backface
            // culling only ever keeps one of the two.
            int back = b.V.Count;
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < cols; i++)
                {
                    int src = start + j * cols + i;
                    b.Vertex(b.V[src], -b.N[src], b.UV[src]);
                }
            for (int j = 0; j < nv - 1; j++)
                for (int i = 0; i < cols - 1; i++)
                {
                    int a = back + j * cols + i;
                    b.Quad(sub, a, a + 1, a + cols + 1, a + cols);
                }
        }

        // ==================================================================== curves

        /// <summary>Catmull-Rom through the control points, clamped at both ends.</summary>
        static Vector2 Spline(Vector2[] p, float t)
        {
            int n = p.Length;
            float x = Mathf.Clamp01(t) * (n - 1);
            int i = Mathf.Clamp((int)x, 0, n - 2);
            float f = x - i;
            Vector2 p0 = p[Mathf.Max(0, i - 1)], p1 = p[i], p2 = p[i + 1], p3 = p[Mathf.Min(n - 1, i + 2)];
            return 0.5f * (2f * p1
                         + (-p0 + p2) * f
                         + (2f * p0 - 5f * p1 + 4f * p2 - p3) * f * f
                         + (-p0 + 3f * p1 - 3f * p2 + p3) * f * f * f);
        }

        // ==================================================================== apple

        /// <summary>
        /// Bottom to top, (radius, height). The two dips are the whole reason this reads as an apple
        /// rather than as a ball: the calyx well underneath and the deeper stem well on top, with the
        /// widest point sitting slightly BELOW the middle.
        /// </summary>
        static readonly Vector2[] AppleProfile =
        {
            new Vector2(0.000f, -0.860f),
            new Vector2(0.150f, -0.900f),
            new Vector2(0.330f, -0.865f),
            new Vector2(0.560f, -0.745f),
            new Vector2(0.795f, -0.525f),
            new Vector2(0.940f, -0.235f),
            new Vector2(1.000f,  0.075f),
            new Vector2(0.960f,  0.360f),
            new Vector2(0.850f,  0.600f),
            new Vector2(0.660f,  0.780f),
            new Vector2(0.430f,  0.880f),
            new Vector2(0.230f,  0.855f),
            new Vector2(0.090f,  0.740f),
            new Vector2(0.000f,  0.700f),
        };

        /// <param name="width">Across the widest point, in metres. A supermarket apple is about 0.08.</param>
        public static Mesh Apple(float width = 0.082f, int radial = 72, int rings = 56)
        {
            var b = new Build();
            float k = width * 0.5f;

            // A touch of lobing, four-fold like a real apple, so the silhouette is not a perfect circle.
            Vector3 Skin(int i, int j)
            {
                float t = j / (float)(rings - 1);
                float th = i / (float)radial * Mathf.PI * 2f;
                Vector2 p = Spline(AppleProfile, t);
                float r = p.x * (1f + 0.022f * Mathf.Cos(th * 4f) * Mathf.Sin(t * Mathf.PI));
                return new Vector3(r * Mathf.Cos(th) * k, p.y * k, r * Mathf.Sin(th) * k);
            }
            Grid(b, AppleSkin, radial, rings, wrapU: true, twoSided: false, Skin);

            // Stem: out of the well, leaning, and tapering the way a real one does — fat where it meets
            // the fruit, thin where it snapped off the branch.
            Vector3 StemSpine(float s) =>
                new Vector3(Mathf.Lerp(0f, 0.10f, s * s) * k, (0.70f + s * 0.46f) * k, 0.02f * s * s * k);
            Tube(b, AppleStem, StemSpine, s => Mathf.Lerp(0.062f, 0.040f, s) * k, 12, 10);

            // One leaf off the stem, angled out and drooping — it catches the light on the turn and it is
            // what stops the apple reading as a beach ball with a twig in it.
            Blade(b, AppleLeaf,
                  origin: StemSpine(0.72f),
                  rot: Quaternion.Euler(-24f, 34f, 12f),
                  length: 0.62f * k, width: 0.21f * k,
                  curl: 0.30f, droop: 0.34f, ruffle: 0f, ruffleFreq: 0f,
                  arc: 26f, across: 7, along: 14, roundness: 0.62f);

            return b.ToMesh("Snack_Apple");
        }

        // ==================================================================== carrot

        /// <summary>
        /// Tip to crown. y = 0 is the soil line, so the whole root sits below it and only the shoulder and
        /// the greens show until it is pulled — which is exactly what makes the pluck worth watching.
        /// </summary>
        static readonly Vector2[] CarrotProfile =
        {
            new Vector2(0.000f, -1.000f),
            new Vector2(0.014f, -0.955f),
            new Vector2(0.031f, -0.880f),
            new Vector2(0.056f, -0.740f),
            new Vector2(0.081f, -0.560f),
            new Vector2(0.101f, -0.360f),
            new Vector2(0.116f, -0.150f),
            new Vector2(0.126f,  0.020f),
            new Vector2(0.130f,  0.105f),
            new Vector2(0.124f,  0.150f),
            new Vector2(0.105f,  0.180f),
            new Vector2(0.075f,  0.198f),
            new Vector2(0.036f,  0.208f),
            new Vector2(0.000f,  0.205f),
        };

        /// <param name="rootLength">Soil line to tip, in metres. A decent carrot is about 0.16.</param>
        public static Mesh Carrot(float rootLength = 0.165f, int radial = 40, int rings = 96, int fronds = 7)
        {
            var b = new Build();
            float k = rootLength;

            // Every carrot in the ground leans. Zero at the crown so the greens still come straight up.
            Vector3 Lean(float t) => new Vector3(0.075f * (1f - t) * (1f - t) * k, 0f, 0.03f * (1f - t) * k);

            Vector3 Root(int i, int j)
            {
                float t = j / (float)(rings - 1);
                float th = i / (float)radial * Mathf.PI * 2f;
                Vector2 p = Spline(CarrotProfile, t);
                // The fine rings are the growth marks; the slow wobble stops it looking turned on a lathe,
                // which is of course exactly what it is.
                float r = p.x * (1f + 0.035f * Mathf.Sin(t * 47f) + 0.022f * Mathf.Sin(t * 7.3f + 1.1f)
                                    + 0.018f * Mathf.Cos(th * 3f) * (1f - t));
                Vector3 lean = Lean(t);
                return new Vector3(r * Mathf.Cos(th) * k + lean.x, p.y * k, r * Mathf.Sin(th) * k + lean.z);
            }
            Grid(b, CarrotRoot, radial, rings, wrapU: true, twoSided: false, Root);

            // The greens. Each frond is a bare stalk with small blades alternating up it — separate leaves
            // rather than one lobed ribbon, because feathery is the entire look of a carrot top.
            Vector2 crown = Spline(CarrotProfile, 1f);
            float crownY = crown.y * k;
            for (int f = 0; f < fronds; f++)
            {
                float a = f / (float)fronds * 360f + 13f;
                float outward = 0.30f + 0.55f * ((f * 7 % 5) / 4f);        // deterministic, not random
                float height = Mathf.Lerp(0.62f, 1.05f, ((f * 3 % 4) / 3f));
                Quaternion yaw = Quaternion.Euler(0f, a, 0f);
                Vector3 baseAt = new Vector3(0f, crownY - 0.012f * k, 0f)
                               + yaw * new Vector3(0f, 0f, 0.035f * k);

                Vector3 Spine(float s) => baseAt + yaw * new Vector3(
                    0f,
                    height * k * (s * 1.35f - 0.35f * s * s * s),          // up, then easing over
                    outward * k * (s * s * 0.85f + s * 0.15f));            // and leaning outward

                Tube(b, CarrotLeaf, Spine, s => Mathf.Lerp(0.014f, 0.005f, s) * k, 7, 12);

                const int leaflets = 6;
                for (int l = 0; l < leaflets; l++)
                {
                    float s = 0.30f + 0.68f * (l / (float)(leaflets - 1));
                    // Alternating sides, and every other one kicked up, so no two read as a matched pair.
                    float side = (l % 2 == 0) ? 1f : -1f;
                    Quaternion rot = yaw
                                   * Quaternion.Euler(-52f + l * 6f, side * 62f, side * 18f);
                    Blade(b, CarrotLeaf,
                          origin: Spine(s), rot: rot,
                          length: Mathf.Lerp(0.30f, 0.14f, s) * k,
                          width: Mathf.Lerp(0.075f, 0.035f, s) * k,
                          curl: 0.26f, droop: 0.22f, ruffle: 0.16f, ruffleFreq: 3.5f,
                          arc: 18f, across: 6, along: 9, roundness: 0.78f);
                }
            }

            return b.ToMesh("Snack_Carrot");
        }

        // ==================================================================== lettuce

        /// <param name="width">Across the head, in metres. A butterhead is about 0.15.</param>
        public static Mesh Lettuce(float width = 0.150f, int leaves = 16)
        {
            var b = new Build();
            float k = width * 0.5f;

            // Five layers, opening outward and drooping further as they go. The outermost leaves are the
            // big floppy ones you would strip off; the innermost are the tight pale heart.
            int[] perLayer = { 5, 4, 3, 2, 2 };
            int made = 0;
            for (int layer = 0; layer < perLayer.Length && made < leaves; layer++)
            {
                float f = layer / (float)(perLayer.Length - 1);            // 0 outer, 1 inner
                int count = perLayer[layer];
                for (int j = 0; j < count && made < leaves; j++, made++)
                {
                    // Golden-angle offset per layer, so no leaf ever sits directly on the one below it.
                    float yawDeg = j / (float)count * 360f + layer * 137.5f;
                    // Pitch is how far the leaf STARTS from horizontal; the arc below then curls it up
                    // along its own length. Outer leaves leave the core nearly flat and flop outward;
                    // inner ones leave it steeply and, with the tighter arc, close over the heart.
                    Quaternion rot = Quaternion.Euler(
                        Mathf.Lerp(-18f, -68f, f),
                        yawDeg,
                        Mathf.Lerp(-11f, 11f, ((made * 5) % 7) / 6f));      // a little roll, deterministic

                    Blade(b, LettuceLeaf,
                          // Each layer starts higher up the core than the one outside it. Without that
                          // rise the head is a flat rosette: the leaves fan out, nothing stacks, and it
                          // ends up twice as wide as it is tall.
                          origin: new Vector3(0f, Mathf.Lerp(0.05f, 0.68f, f) * k, 0f),
                          rot: rot,
                          // The outer layer is what sets how wide the head measures, so this number is
                          // tuned against the finished head rather than against one leaf.
                          length: Mathf.Lerp(1.30f, 0.50f, f) * k,
                          width: Mathf.Lerp(0.60f, 0.34f, f) * k,
                          // Inner leaves cup hard — that curl is what closes the head up.
                          curl: Mathf.Lerp(0.55f, 1.25f, f),
                          droop: Mathf.Lerp(0.34f, 0.05f, f),
                          ruffle: Mathf.Lerp(0.30f, 0.16f, f),
                          ruffleFreq: Mathf.Lerp(5.5f, 3.5f, f),
                          arc: Mathf.Lerp(52f, 96f, f),
                          across: 13, along: 19, roundness: 0.42f);
                }
            }

            // The stump you would cut off, so the underside is not a hole.
            Vector2[] core =
            {
                new Vector2(0.000f, -0.02f), new Vector2(0.170f, -0.02f), new Vector2(0.215f, 0.06f),
                new Vector2(0.230f, 0.20f),  new Vector2(0.195f, 0.34f),  new Vector2(0.110f, 0.42f),
                new Vector2(0.000f, 0.44f),
            };
            Grid(b, LettuceCore, 24, 14, wrapU: true, twoSided: false, (i, j) =>
            {
                float t = j / 13f;
                float th = i / 24f * Mathf.PI * 2f;
                Vector2 p = Spline(core, t);
                return new Vector3(p.x * Mathf.Cos(th) * k, p.y * k, p.x * Mathf.Sin(th) * k);
            });

            return b.ToMesh("Snack_Lettuce");
        }

        // ==================================================================== shared parts

        /// <summary>
        /// A tapered tube swept along a spine — stems and stalks.
        ///
        /// The ring frames are parallel-transported: each one carries the previous ring's orientation
        /// forward and only takes out the part that now lies along the tangent. Rebuilding the frame from
        /// a fixed world axis at every ring instead looks fine until the spine passes near that axis, at
        /// which point the frame spins through ninety degrees between two rings and the tube shears itself
        /// into a twisted ribbon — and an apple stem leaves the fruit pointing very nearly straight up.
        /// </summary>
        static void Tube(Build b, int sub, System.Func<float, Vector3> spine,
                         System.Func<float, float> radius, int sides, int segments)
        {
            var centre = new Vector3[segments];
            var right = new Vector3[segments];
            var up = new Vector3[segments];
            var wide = new float[segments];

            Vector3 carried = Vector3.zero;
            for (int j = 0; j < segments; j++)
            {
                float s = j / (float)(segments - 1);
                centre[j] = spine(s);
                wide[j] = radius(s);

                Vector3 fwd = spine(Mathf.Min(1f, s + 0.02f)) - spine(Mathf.Max(0f, s - 0.02f));
                if (fwd.sqrMagnitude < 1e-12f) fwd = Vector3.up;
                fwd.Normalize();

                if (j == 0)
                    carried = Vector3.Cross(fwd, Mathf.Abs(fwd.y) > 0.9f ? Vector3.forward : Vector3.up);
                else
                    carried -= fwd * Vector3.Dot(carried, fwd);

                if (carried.sqrMagnitude < 1e-10f)
                    carried = Vector3.Cross(fwd, Mathf.Abs(fwd.y) > 0.9f ? Vector3.forward : Vector3.up);
                carried.Normalize();

                right[j] = carried;
                up[j] = Vector3.Cross(carried, fwd);
            }

            Grid(b, sub, sides, segments, wrapU: true, twoSided: false, (i, j) =>
            {
                float th = i / (float)sides * Mathf.PI * 2f;
                return centre[j] + (right[j] * Mathf.Cos(th) + up[j] * Mathf.Sin(th)) * wide[j];
            });
        }

        /// <summary>
        /// One leaf. Built in its own frame — spine along +Z, face toward +Y — then rotated into place, so
        /// the same function draws an apple leaf, a carrot leaflet and a lettuce leaf.
        ///
        /// <paramref name="arc"/> bends the spine up and over, which is what wraps a lettuce leaf around
        /// the head. <paramref name="curl"/> cups it across its width. <paramref name="ruffle"/> waves the
        /// EDGE only — it falls off as the cube of the distance from the midrib, so the middle of the leaf
        /// stays flat and only the frill moves, the way a real one does.
        /// </summary>
        static void Blade(Build b, int sub, Vector3 origin, Quaternion rot, float length, float width,
                          float curl, float droop, float ruffle, float ruffleFreq, float arc,
                          int across, int along, float roundness)
        {
            // Floored, not just guarded against divide-by-zero: at arc = 0 the sweep below degenerates to
            // a single point rather than to the straight leaf you would expect, so a tiny angle is what
            // stands in for "no bend".
            float rad = Mathf.Max(1e-3f, arc * Mathf.Deg2Rad);

            Grid(b, sub, across, along, wrapU: false, twoSided: true, (i, j) =>
            {
                float w = across > 1 ? i / (float)(across - 1) * 2f - 1f : 0f;   // -1 .. 1
                float s = along > 1 ? j / (float)(along - 1) : 0f;               //  0 .. 1

                // Half-width: zero at the base, fattest early, tapering to a point. roundness moves where
                // the widest part sits — low is a lance, high is a spade.
                float hw = width * Mathf.Sin(Mathf.PI * Mathf.Pow(s, roundness));

                // The spine, swept up through `arc` degrees and drooping under its own weight. Dividing
                // by the angle keeps the leaf `length` long however hard it is bent.
                float a = rad * s;
                Vector3 p = new Vector3(0f,
                                        length * (1f - Mathf.Cos(a)) / rad - droop * length * s * s,
                                        length * Mathf.Sin(a) / rad);

                p.x += w * hw;
                p.y += curl * hw * w * w;                                        // cupped across
                p.y += ruffle * hw * w * w * w * Mathf.Sin(s * ruffleFreq * Mathf.PI * 2f);

                return origin + rot * p;
            });
        }

        // ==================================================================== skins

        /// <summary>
        /// Red with the green-gold streaks that run pole to pole, freckles, and the wells darkened. The
        /// streaks are what sell it: a flat red sphere reads as plastic at any polygon count.
        /// </summary>
        public static Texture2D AppleSkin_Texture(int size = 512)
        {
            return Bake(size, (u, v) =>
            {
                float streak = Fbm(u * 26f, v * 2.2f, 3);
                float mottle = Fbm(u * 8f + 31f, v * 8f, 4);

                Color deep = new Color(0.44f, 0.035f, 0.045f);
                Color bright = new Color(0.79f, 0.115f, 0.085f);
                Color blush = new Color(0.86f, 0.63f, 0.16f);

                // fbm clusters around 0.5, so the edges are set close in — a 0..1 pair would leave the
                // whole skin sitting flat in the middle of the ramp.
                Color c = Color.Lerp(deep, bright, Step(0.34f, 0.68f, mottle));
                c = Color.Lerp(c, blush, Step(0.52f, 0.80f, streak) * 0.6f);
                // Green-gold creeping up from the calyx end.
                c = Color.Lerp(c, new Color(0.62f, 0.66f, 0.20f), Step(0.20f, 0f, v) * 0.45f);

                // Freckles: sparse, pale, and only where the skin is already dark enough to show them.
                float dots = Fbm(u * 150f + 7f, v * 150f + 19f, 1);
                c = Color.Lerp(c, new Color(0.92f, 0.84f, 0.62f), Step(0.86f, 0.97f, dots) * 0.5f);

                // Both wells sit in shadow whatever the light does — and ONLY the wells.
                float well = Step(0f, 0.055f, v) * Step(1f, 0.945f, v);
                return c * Mathf.Lerp(0.42f, 1f, well);
            });
        }

        /// <summary>Orange, deepening to the tip, with the fine horizontal growth rings and rootlet marks.</summary>
        public static Texture2D CarrotSkin_Texture(int size = 512)
        {
            return Bake(size, (u, v) =>
            {
                Color deep = new Color(0.66f, 0.235f, 0.045f);
                Color bright = new Color(0.93f, 0.455f, 0.095f);

                float grain = Fbm(u * 6f, v * 30f, 3);
                Color c = Color.Lerp(deep, bright, Step(0.36f, 0.66f, grain));

                // Rings. Irregular spacing, or it looks machined.
                float rings = Mathf.Abs(Mathf.Sin(v * 190f + Fbm(u * 3f, v * 9f, 2) * 6f));
                c *= Mathf.Lerp(0.84f, 1.05f, Step(0f, 0.45f, rings));

                // Short dark dashes where the rootlets came off. Sparse — this is freckling, not a coat.
                float hairs = Fbm(u * 40f + 11f, v * 240f, 2);
                c = Color.Lerp(c, new Color(0.36f, 0.15f, 0.045f), Step(0.70f, 0.88f, hairs) * 0.40f);

                // The tip is darker and a little wet-looking. The green is the shoulder that sat in the
                // light above the soil, so it belongs to the top few percent and nowhere else.
                c *= Mathf.Lerp(0.70f, 1f, Step(0f, 0.25f, v));
                c = Color.Lerp(c, new Color(0.45f, 0.50f, 0.16f), Step(0.94f, 1f, v) * 0.75f);
                return c;
            });
        }

        /// <summary>Leaf green, pale and thick at the base, with the midrib and its branching veins.</summary>
        public static Texture2D LettuceSkin_Texture(int size = 512)
        {
            return Bake(size, (u, v) =>
            {
                Color stemPale = new Color(0.83f, 0.86f, 0.58f);
                Color midGreen = new Color(0.40f, 0.63f, 0.22f);
                Color deepGreen = new Color(0.20f, 0.42f, 0.13f);

                float blotch = Fbm(u * 9f, v * 6f, 4);
                Color c = Color.Lerp(midGreen, deepGreen, Step(0.38f, 0.68f, blotch));
                // The base of every leaf is the pale thick part; the frill at the tip is the darkest.
                c = Color.Lerp(stemPale, c, Step(0.02f, 0.42f, v));
                c = Color.Lerp(c, deepGreen, Step(0.72f, 1f, v) * 0.55f);

                // The midrib runs up u = 0.5 and tapers out; the side veins fan off it.
                float rib = Step(0.055f, 0f, Mathf.Abs(u - 0.5f)) * Step(1f, 0.25f, v);
                float side = Mathf.Abs(Mathf.Sin((u - 0.5f) * 14f + v * 17f));
                float veins = Step(0.95f, 1f, 1f - side) * Step(0.06f, 0.5f, v);
                c = Color.Lerp(c, stemPale, Mathf.Clamp01(rib * 0.85f + veins * 0.45f));
                return c;
            });
        }

        /// <summary>
        /// A 0..1 mask that ramps between two edges — GLSL's <c>smoothstep</c>.
        ///
        /// <b>Not <see cref="Mathf.SmoothStep"/>, which is a different function with a confusingly similar
        /// name.</b> Unity's takes (from, to, t) and returns a smoothed value BETWEEN from and to; it is a
        /// Lerp, not a threshold. Used as a mask it returns roughly its own edge values whatever the input
        /// is, so "tint the top 7% green" became "tint all of it green" and "darken only the two wells"
        /// became "multiply the whole apple by 0.45". Hence a green carrot and a brown apple.
        ///
        /// Edges may be given in either order; a descending pair gives a descending ramp.
        /// </summary>
        static float Step(float edge0, float edge1, float x)
        {
            if (Mathf.Abs(edge1 - edge0) < 1e-6f) return x < edge0 ? 0f : 1f;
            float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        static Texture2D Bake(int size, System.Func<float, float, Color> shade)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGB24, mipChain: true, linear: false);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float v = y / (float)(size - 1);
                for (int x = 0; x < size; x++)
                {
                    Color c = shade(x / (float)(size - 1), v);
                    px[y * size + x] = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b));
                }
            }
            tex.SetPixels(px);
            tex.Apply(updateMipmaps: true);
            return tex;
        }

        /// <summary>Plain fractal Perlin, 0..1. Enough for skin; nobody is going to count the octaves.</summary>
        static float Fbm(float x, float y, int octaves)
        {
            float sum = 0f, amp = 0.5f, total = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Mathf.PerlinNoise(x, y) * amp;
                total += amp;
                amp *= 0.5f;
                x *= 2.03f;
                y *= 1.97f;
            }
            return total > 0f ? sum / total : 0f;
        }
    }
}
