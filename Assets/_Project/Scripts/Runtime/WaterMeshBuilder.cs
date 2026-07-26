using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NasaSim
{
    /// <summary>
    /// Generates every water mesh in the project — the sheet you see and the mud basin under it — from a
    /// handful of numbers, at load, into meshes that are never saved into the scene.
    ///
    /// Water can't come out of Maya: an FBX has no waves, and <c>NasaSim/Water</c> displaces vertices in
    /// the vertex shader, so the sheet has to be a dense, EVEN grid or the swell goes faceted. It is built
    /// here instead, and rebuilt the instant a number on <see cref="WaterBody"/> changes.
    ///
    /// Everything is one primitive: a closed LOOP in the XZ plane, a rounded rectangle. At corner radius 0
    /// that is a sharp rectangle (the square moat); when the radius reaches the half-extent it is a circle
    /// (a round pond, and the old circular moat). Offsetting a loop outward by <c>d</c> adds <c>d</c> to
    /// both the half-extents and the radius, which leaves the straight runs exactly as long as they were —
    /// so two loops of the same body always have matching point counts and can be bridged into a quad
    /// strip. That strip is the moat's water, and it is also each bank of the basin.
    /// </summary>
    public static class WaterMeshBuilder
    {
        /// <summary>
        /// How a body's loops are subdivided. Computed ONCE from the body's largest loop and reused for
        /// every other loop, so corresponding points line up index-for-index and can be bridged.
        /// </summary>
        public struct Topology
        {
            /// <summary>Intervals per 90° corner arc. 0 = a sharp corner (one point).</summary>
            public int cornerSegments;
            /// <summary>Intervals along the two straight runs that face ±Z / ±X.</summary>
            public int xEdgeSegments, zEdgeSegments;
            /// <summary>False when that run has zero length (a circle) — its arcs then share endpoints.</summary>
            public bool hasXEdge, hasZEdge;

            public int EdgeSegments(int corner) => (corner & 1) == 0 ? xEdgeSegments : zEdgeSegments;
            public bool EdgeAfter(int corner) => (corner & 1) == 0 ? hasXEdge : hasZEdge;

            public int PointCount
            {
                get
                {
                    int n = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        n += cornerSegments <= 0 ? 1 : cornerSegments + (EdgeAfter(k) ? 1 : 0);
                        if (EdgeAfter(k)) n += Mathf.Max(0, EdgeSegments(k) - 1);
                    }
                    return n;
                }
            }
        }

        /// <summary>Corner radius can never exceed the smaller half-extent (that shape is a circle).</summary>
        public static float ClampRadius(Vector2 half, float radius) =>
            Mathf.Clamp(radius, 0f, Mathf.Max(0f, Mathf.Min(half.x, half.y)));

        /// <summary>
        /// Pick the subdivision for a body. Feed it the body's LARGEST loop (the basin's outer lip) so
        /// every smaller loop is at least this dense.
        /// </summary>
        public static Topology TopologyFor(Vector2 half, float radius, float density)
        {
            float r = ClampRadius(half, radius);
            float cx = Mathf.Max(0f, half.x - r), cz = Mathf.Max(0f, half.y - r);
            density = Mathf.Clamp(density, 0.2f, 8f);

            return new Topology
            {
                cornerSegments = r > 1e-3f ? Mathf.Clamp(Mathf.RoundToInt(r * density * 2f), 3, 96) : 0,
                xEdgeSegments = Segments(2f * cx, density),
                zEdgeSegments = Segments(2f * cz, density),
                hasXEdge = cx > 1e-3f,
                hasZEdge = cz > 1e-3f,
            };
        }

        static int Segments(float length, float density) =>
            Mathf.Clamp(Mathf.RoundToInt(length * density), 1, 512);

        /// <summary>
        /// One closed loop, counter-clockwise seen from above, starting on the +X run. Points are laid out
        /// per the shared <paramref name="topo"/>, never per this loop's own size, so two loops built with
        /// the same topology correspond index-for-index.
        /// </summary>
        public static Vector2[] Loop(Vector2 half, float radius, in Topology topo)
        {
            float r = ClampRadius(half, radius);
            float cx = Mathf.Max(0f, half.x - r), cz = Mathf.Max(0f, half.y - r);
            var centers = new[]
            {
                new Vector2(cx, cz), new Vector2(-cx, cz), new Vector2(-cx, -cz), new Vector2(cx, -cz),
            };

            var pts = new List<Vector2>(topo.PointCount);
            for (int k = 0; k < 4; k++)
            {
                float a0 = k * 90f;
                bool edgeAfter = topo.EdgeAfter(k);

                if (topo.cornerSegments <= 0)
                {
                    pts.Add(centers[k] + Dir(a0) * r);
                }
                else
                {
                    // Include the arc's END point only when a straight run follows it; on a circle the
                    // next arc starts at exactly that spot and would duplicate the vertex.
                    int last = edgeAfter ? topo.cornerSegments : topo.cornerSegments - 1;
                    for (int s = 0; s <= last; s++)
                        pts.Add(centers[k] + Dir(a0 + 90f * s / topo.cornerSegments) * r);
                }

                if (!edgeAfter) continue;

                Vector2 from = pts[pts.Count - 1];
                Vector2 to = centers[(k + 1) & 3] + Dir(a0 + 90f) * r;
                int segs = topo.EdgeSegments(k);
                for (int s = 1; s < segs; s++) pts.Add(Vector2.Lerp(from, to, s / (float)segs));
            }
            return pts.ToArray();
        }

        static Vector2 Dir(float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }

        /// <summary>A loop pushed <paramref name="offset"/> m outward (negative = inward).</summary>
        public static Vector2[] Offset(Vector2 half, float radius, float offset, in Topology topo) =>
            Loop(half + Vector2.one * offset, radius + offset, topo);

        // ------------------------------------------------------------------ surfaces

        /// <summary>
        /// The moat sheet: the band between two loops, subdivided across its width so the wave shader has
        /// vertices to move. <paramref name="rings"/> is the number of loops laid across the band.
        /// </summary>
        public static Mesh BuildBand(Vector2[] inner, Vector2[] outer, int rings, string name)
        {
            rings = Mathf.Max(2, rings);
            var loops = new List<Vector2[]>(rings);
            var heights = new List<float>(rings);
            for (int i = 0; i < rings; i++)
            {
                float t = i / (rings - 1f);
                var loop = new Vector2[Mathf.Min(inner.Length, outer.Length)];
                for (int p = 0; p < loop.Length; p++) loop[p] = Vector2.Lerp(inner[p], outer[p], t);
                loops.Add(loop);
                heights.Add(0f);
            }
            return Loft(loops, heights, name);
        }

        /// <summary>A filled pond: concentric copies of the loop shrinking to a point at the centre.</summary>
        public static Mesh BuildFilled(Vector2[] outer, int rings, string name)
        {
            rings = Mathf.Max(2, rings);
            var verts = new List<Vector3> { Vector3.zero };            // 0 = centre
            var tris = new List<int>();

            for (int i = 1; i < rings; i++)
            {
                float t = i / (rings - 1f);
                foreach (var p in outer) verts.Add(new Vector3(p.x * t, 0f, p.y * t));
            }

            int n = outer.Length;
            for (int i = 0; i < n; i++)                                 // centre fan
            {
                tris.Add(0); tris.Add(1 + i); tris.Add(1 + (i + 1) % n);
            }
            for (int ring = 0; ring < rings - 2; ring++)                // bands between the rings
            {
                int a0 = 1 + ring * n, b0 = a0 + n;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    AddQuad(tris, verts, a0 + i, b0 + i, a0 + j, b0 + j);
                }
            }
            return FinalizeUpward(verts, tris, name);
        }

        /// <summary>An even X/Z grid — the right subdivision for a rectangular pond.</summary>
        public static Mesh BuildGrid(Vector2 size, float density, string name)
        {
            int nx = Mathf.Clamp(Mathf.RoundToInt(size.x * density), 2, 256);
            int nz = Mathf.Clamp(Mathf.RoundToInt(size.y * density), 2, 256);
            var verts = new List<Vector3>((nx + 1) * (nz + 1));
            var tris = new List<int>(nx * nz * 6);

            for (int z = 0; z <= nz; z++)
                for (int x = 0; x <= nx; x++)
                    verts.Add(new Vector3((x / (float)nx - 0.5f) * size.x, 0f,
                                          (z / (float)nz - 0.5f) * size.y));

            int cols = nx + 1;
            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    int a = z * cols + x;
                    AddQuad(tris, verts, a, a + cols, a + 1, a + cols + 1);
                }
            return FinalizeUpward(verts, tris, name);
        }

        // ------------------------------------------------------------------ basins

        /// <summary>
        /// The trench under a ring body: a lip on the inner bank, down the slope, across the floor, up to
        /// a lip on the outer bank. Given as loops so it follows a square moat exactly as well as a round one.
        /// </summary>
        public static Mesh BuildTrough(Vector2[] innerLip, Vector2[] innerFloor,
                                       Vector2[] outerFloor, Vector2[] outerLip,
                                       float lipY, float floorY, string name)
        {
            var loops = new List<Vector2[]> { innerLip, innerFloor, outerFloor, outerLip };
            var heights = new List<float> { lipY, floorY, floorY, lipY };
            return Loft(loops, heights, name);
        }

        /// <summary>The tub under a filled pond: a floor, four (or a ring of) banks, an outer lip.</summary>
        public static Mesh BuildTub(Vector2[] floor, Vector2[] lip, float lipY, float floorY, string name)
        {
            int n = Mathf.Min(floor.Length, lip.Length);
            var verts = new List<Vector3>(n * 2 + 1) { new Vector3(0f, floorY, 0f) };
            var tris = new List<int>();

            for (int i = 0; i < n; i++) verts.Add(new Vector3(floor[i].x, floorY, floor[i].y));
            for (int i = 0; i < n; i++) verts.Add(new Vector3(lip[i].x, lipY, lip[i].y));

            for (int i = 0; i < n; i++)                                  // floor fan
            {
                tris.Add(0); tris.Add(1 + i); tris.Add(1 + (i + 1) % n);
            }
            for (int i = 0; i < n; i++)                                  // banks
            {
                int j = (i + 1) % n;
                AddQuad(tris, verts, 1 + i, 1 + n + i, 1 + j, 1 + n + j);
            }
            return FinalizeUpward(verts, tris, name);
        }

        // ------------------------------------------------------------------ plumbing

        /// <summary>Bridge a stack of same-length loops into one surface, each loop at its own height.</summary>
        public static Mesh Loft(IList<Vector2[]> loops, IList<float> heights, string name)
        {
            int n = int.MaxValue;
            foreach (var loop in loops) n = Mathf.Min(n, loop.Length);
            if (loops.Count < 2 || n < 3) return new Mesh { name = name };

            var verts = new List<Vector3>(loops.Count * n);
            var tris = new List<int>(loops.Count * n * 6);

            for (int r = 0; r < loops.Count; r++)
                for (int i = 0; i < n; i++)
                    verts.Add(new Vector3(loops[r][i].x, heights[r], loops[r][i].y));

            for (int r = 0; r < loops.Count - 1; r++)
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    AddQuad(tris, verts, r * n + i, (r + 1) * n + i, r * n + j, (r + 1) * n + j);
                }
            return FinalizeUpward(verts, tris, name);
        }

        /// <summary>Two triangles, dropped when the corners collapse (a fully-rounded loop can pinch).</summary>
        static void AddQuad(List<int> tris, List<Vector3> verts, int a, int b, int c, int d)
        {
            AddTriangle(tris, verts, a, b, c);
            AddTriangle(tris, verts, c, b, d);
        }

        static void AddTriangle(List<int> tris, List<Vector3> verts, int a, int b, int c)
        {
            if ((verts[a] - verts[b]).sqrMagnitude < 1e-10f ||
                (verts[b] - verts[c]).sqrMagnitude < 1e-10f ||
                (verts[c] - verts[a]).sqrMagnitude < 1e-10f) return;
            tris.Add(a); tris.Add(b); tris.Add(c);
        }

        /// <summary>
        /// Build the mesh and guarantee it faces UP: recalc normals and, if the average points down, flip
        /// every triangle. Cheap insurance against the classic invisible-from-above generated mesh.
        /// </summary>
        static Mesh FinalizeUpward(List<Vector3> verts, List<int> tris, string name)
        {
            var mesh = new Mesh { name = name };
            if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();

            float sum = 0f;
            var normals = mesh.normals;
            for (int i = 0; i < normals.Length; i++) sum += normals[i].y;
            if (sum < 0f)
            {
                tris.Reverse();                                // reverses each triangle's winding
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
            }
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
