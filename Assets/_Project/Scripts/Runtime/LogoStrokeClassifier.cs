using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Works out which pen-down strokes of the loaded logo make up the meatball's <b>circle</b>, its
    /// <b>red swoosh</b> and its <b>star dots</b>, so the flowers the tractor throws can be colored — and
    /// the stars silenced — without anyone hand-numbering 56 strokes.
    ///
    /// It classifies by SHAPE, not by index, so it survives a re-export of the CSV:
    /// <list type="bullet">
    /// <item><b>Circle</b> — of the strokes at least <see cref="MinCircleExtentFraction"/> of the logo
    /// across, the one whose points sit at the most constant distance from their own centroid. On the
    /// shipped logo that is 0.014 for the circle against 0.28 for the next best — not a close call.</item>
    /// <item><b>Swoosh</b> — every big stroke that <b>overhangs the circle</b>. That is the one thing only
    /// the red vector does: its two tips hang past the rim of the disc, while the orbit ellipse, the
    /// letters and the stars are all contained by it. On the shipped logo 16–20 % of each swoosh piece's
    /// points sit beyond the rim, against 0.0 % for every other stroke — no threshold to tune.
    /// This is a SET, not a single winner: the orbit and the letters cut the vector into three separate
    /// contours (41, 42, 43 on the shipped logo) and all of them must come out red.</item>
    /// <item><b>Star</b> — anything under <see cref="MaxStarExtentFraction"/> of the logo across: the
    /// star dots, plus the odd sub-metre sliver of a letter clipped by the swoosh. On the shipped logo
    /// the largest star is 3.3 % across and the smallest non-star 5.6 %.</item>
    /// <item>Everything else — the orbit ellipse and the letters — is <b>Other</b>.</item>
    /// </list>
    ///
    /// Beware the tempting test that does NOT work: "the swoosh is the big stroke inside the circle".
    /// The orbit ellipse is entirely inside the circle and the swoosh is the thing that is not, so that
    /// test picks exactly the wrong one of the two. Nor does thickness separate them on its own — the
    /// thinnest swoosh piece (0.62 m) is thinner than the fattest orbit piece (0.66 m).
    ///
    /// Stroke numbering matches what <see cref="MowingVisual_GrassAndFlowers"/> uses at runtime: one
    /// stroke per pen-down run, in draw order.
    /// </summary>
    public static class LogoStrokeClassifier
    {
        // Ordinals are not serialized anywhere today, but keep new members on the END regardless.
        public enum Role { Other = 0, Circle = 1, Swoosh = 2, Star = 3 }

        /// <summary>Measurements of one pen-down stroke, all on the flat ground plane (XZ).</summary>
        public struct Stroke
        {
            public int index;
            /// <summary>First point of the stroke (the point the first drawn segment starts FROM).</summary>
            public int firstPoint;
            public int lastPoint;
            public Vector3 centroid;
            public float meanRadius;
            /// <summary>Std-dev of the radius over its mean. ~0 is a perfect circle.</summary>
            public float circularity;
            /// <summary>Largest ground dimension of the stroke's bounding box.</summary>
            public float extent;
            public float arcLength;
            /// <summary>Area enclosed by the stroke read as a closed loop (every logo contour is one).</summary>
            public float area;
            /// <summary>2 x area / perimeter — how fat the shape is. A hairline outline tends to 0.</summary>
            public float thickness;
            /// <summary>Fraction of the stroke's points lying beyond the circle's rim. 0 for most.</summary>
            public float outsideFraction;
            public Role role;
        }

        /// <summary>A stroke must be at least this fraction of the logo across to be the circle.</summary>
        public const float MinCircleExtentFraction = 0.40f;
        /// <summary>...or this fraction to be considered for the swoosh at all.</summary>
        public const float MinSwooshExtentFraction = 0.10f;
        /// <summary>Fraction of a stroke's points that must hang outside the circle to call it the swoosh.</summary>
        public const float MinSwooshOutsideFraction = 0.05f;
        /// <summary>Under this fraction of the logo across, a stroke is a star dot (or a stray sliver).</summary>
        public const float MaxStarExtentFraction = 0.04f;
        /// <summary>Above this radius variation nothing is called a circle (so a logo without one gets none).</summary>
        public const float MaxCircularity = 0.15f;
        /// <summary>How far past the circle's mean radius counts as "outside" — absorbs a wobbly circle.</summary>
        const float RimMargin = 1.02f;
        /// <summary>Fewest points a stroke may have and still be considered round.</summary>
        const int MinCirclePoints = 8;
        /// <summary>Fallback only: how big a stroke must be to stand in for a swoosh that never overhangs.</summary>
        const float FallbackSwooshExtentFraction = 0.25f;

        public static readonly Color NasaBlue = new Color(0.043f, 0.239f, 0.569f, 1f);   // #0B3D91
        public static readonly Color NasaRed = new Color(0.988f, 0.239f, 0.129f, 1f);    // #FC3D21

        // ------------------------------------------------------------------ measurement

        /// <summary>Split the path into pen-down strokes and measure each. No roles assigned.</summary>
        public static List<Stroke> Analyze(WaypointPath path)
        {
            var result = new List<Stroke>();
            if (path == null || path.Count < 2) return result;

            var pts = path.Points;
            int start = -1;
            for (int k = 1; k < pts.Count; k++)
            {
                if (pts[k].penDown)
                {
                    if (start < 0) start = k - 1;      // the segment INTO k starts at k-1
                }
                else if (start >= 0)
                {
                    result.Add(Measure(pts, start, k - 1, result.Count));
                    start = -1;
                }
            }
            if (start >= 0) result.Add(Measure(pts, start, pts.Count - 1, result.Count));
            return result;
        }

        static Stroke Measure(IReadOnlyList<Waypoint> pts, int first, int last, int index)
        {
            var s = new Stroke { index = index, firstPoint = first, lastPoint = last, role = Role.Other };
            int n = last - first + 1;
            if (n <= 0) return s;

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            Vector3 sum = Vector3.zero;
            double cross = 0d;                 // shoelace, closing the loop back to `first`
            for (int i = first; i <= last; i++)
            {
                Vector3 p = pts[i].position;
                sum += new Vector3(p.x, 0f, p.z);
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
                if (i > first) s.arcLength += Flat(pts[i].position - pts[i - 1].position).magnitude;

                Vector3 q = pts[i < last ? i + 1 : first].position;
                cross += (double)p.x * q.z - (double)q.x * p.z;
            }

            s.centroid = sum / n;
            s.extent = Mathf.Max(maxX - minX, maxZ - minZ);
            s.area = (float)(System.Math.Abs(cross) * 0.5d);
            // Perimeter closes the loop; on these contours the closing segment is ~0 anyway.
            float perimeter = s.arcLength + Flat(pts[last].position - pts[first].position).magnitude;
            s.thickness = perimeter > 1e-4f ? 2f * s.area / perimeter : 0f;

            double mean = 0d;
            for (int i = first; i <= last; i++) mean += Flat(pts[i].position - s.centroid).magnitude;
            mean /= n;
            s.meanRadius = (float)mean;

            double varSum = 0d;
            for (int i = first; i <= last; i++)
            {
                double d = Flat(pts[i].position - s.centroid).magnitude - mean;
                varSum += d * d;
            }
            s.circularity = mean > 1e-6d ? (float)(System.Math.Sqrt(varSum / n) / mean) : 999f;
            return s;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // ------------------------------------------------------------------ classification

        /// <summary>Measure every stroke and tag the circle, the swoosh pieces and the star dots.</summary>
        public static List<Stroke> Classify(WaypointPath path)
        {
            var strokes = Analyze(path);
            if (strokes.Count == 0 || path == null) return strokes;

            float logo = Mathf.Max(path.Bounds.size.x, path.Bounds.size.z);
            if (logo < 1e-4f) return strokes;

            // ---- the circle: the roundest big stroke ----
            int circle = -1;
            float bestCircularity = float.MaxValue;
            for (int i = 0; i < strokes.Count; i++)
            {
                if (strokes[i].extent < logo * MinCircleExtentFraction) continue;
                // A stroke of two points has both radii equal to half its length, so its radius variance
                // is exactly 0 — a perfect score for a straight line. Demand enough points to describe
                // a curve before that number means anything.
                if (strokes[i].lastPoint - strokes[i].firstPoint + 1 < MinCirclePoints) continue;
                if (strokes[i].circularity >= bestCircularity) continue;
                bestCircularity = strokes[i].circularity;
                circle = i;
            }
            if (circle >= 0 && bestCircularity <= MaxCircularity)
            {
                var s = strokes[circle];
                s.role = Role.Circle;
                strokes[circle] = s;
            }
            else circle = -1;

            // ---- the swoosh: every big stroke that hangs off the edge of the circle ----
            int swooshCount = 0;
            if (circle >= 0)
            {
                Vector3 centre = strokes[circle].centroid;
                float rim = strokes[circle].meanRadius * RimMargin;
                for (int i = 0; i < strokes.Count; i++)
                {
                    if (i == circle) continue;
                    if (strokes[i].extent < logo * MinSwooshExtentFraction) continue;

                    var s = strokes[i];
                    s.outsideFraction = OutsideFraction(path, s, centre, rim);
                    if (s.outsideFraction >= MinSwooshOutsideFraction)
                    {
                        s.role = Role.Swoosh;
                        swooshCount++;
                    }
                    strokes[i] = s;
                }
            }

            if (swooshCount == 0)
            {
                // Either no circle at all, or a logo whose vector is tucked entirely inside it. Fall back
                // to the longest big stroke — a guess, and the report says so.
                int longest = -1;
                float bestLength = -1f;
                for (int i = 0; i < strokes.Count; i++)
                {
                    if (i == circle) continue;
                    if (strokes[i].extent < logo * FallbackSwooshExtentFraction) continue;
                    if (strokes[i].arcLength <= bestLength) continue;
                    bestLength = strokes[i].arcLength;
                    longest = i;
                }
                if (longest >= 0)
                {
                    var s = strokes[longest];
                    s.role = Role.Swoosh;
                    strokes[longest] = s;
                }
            }

            // ---- the stars: whatever is left and too small to read as a shape ----
            for (int i = 0; i < strokes.Count; i++)
            {
                if (strokes[i].role != Role.Other) continue;
                if (strokes[i].extent >= logo * MaxStarExtentFraction) continue;
                var s = strokes[i];
                s.role = Role.Star;
                strokes[i] = s;
            }

            return strokes;
        }

        static float OutsideFraction(WaypointPath path, Stroke s, Vector3 centre, float rim)
        {
            var pts = path.Points;
            float rimSq = rim * rim;
            int n = 0, outside = 0;
            for (int i = s.firstPoint; i <= s.lastPoint; i++)
            {
                n++;
                if (Flat(pts[i].position - centre).sqrMagnitude > rimSq) outside++;
            }
            return n > 0 ? outside / (float)n : 0f;
        }

        // ------------------------------------------------------------------ colors

        /// <summary>
        /// One color per stroke, in draw order — the list <see cref="MowingVisual_GrassAndFlowers"/>
        /// indexes by the stroke being cut. A returned color with <b>alpha 0</b> means "this stroke throws
        /// no flowers", which is how <paramref name="star"/> silences the star dots.
        /// </summary>
        public static List<Color> BuildStrokeColors(WaypointPath path, Color circle, Color swoosh,
                                                    Color other, Color star, out string report)
        {
            var strokes = Classify(path);
            var colors = new List<Color>(strokes.Count);
            int nCircle = 0, nSwoosh = 0, nStar = 0;
            var swooshIds = new List<int>();

            foreach (var s in strokes)
            {
                switch (s.role)
                {
                    case Role.Circle: colors.Add(circle); nCircle++; break;
                    case Role.Swoosh: colors.Add(swoosh); nSwoosh++; swooshIds.Add(s.index); break;
                    case Role.Star: colors.Add(star); nStar++; break;
                    default: colors.Add(other); break;
                }
            }

            var sb = new StringBuilder();
            sb.Append(strokes.Count).Append(" strokes classified: ")
              .Append(nCircle).Append(" circle, ")
              .Append(nSwoosh).Append(" swoosh, ")
              .Append(nStar).Append(" star, ")
              .Append(strokes.Count - nCircle - nSwoosh - nStar).Append(" other.");
            foreach (var s in strokes)
            {
                if (s.role == Role.Circle)
                    sb.Append($"\n  stroke #{s.index} = Circle: {s.extent:0.##} m across, " +
                              $"{s.arcLength:0.#} m long, roundness {s.circularity:0.###}");
                else if (s.role == Role.Swoosh)
                    sb.Append($"\n  stroke #{s.index} = Swoosh: {s.extent:0.##} m across, " +
                              $"{s.arcLength:0.#} m long, {s.outsideFraction * 100f:0.#}% of it " +
                              "hangs outside the circle");
            }
            if (nCircle == 0)
                sb.Append("\n  NOTE: no round stroke found — nothing will be blue. Check the CSV, or set " +
                          "Palette Mode = Stroke List and color the strokes by hand.");
            if (nSwoosh == 0)
                sb.Append("\n  NOTE: no swoosh stroke found — nothing will be red.");
            else if (swooshIds.Count == 1 && strokes[swooshIds[0]].outsideFraction < MinSwooshOutsideFraction)
                sb.Append($"\n  NOTE: nothing overhangs the circle, so the swoosh is a GUESS (stroke " +
                          $"#{swooshIds[0]}, the longest big stroke). Check it before trusting the red.");
            report = sb.ToString();
            return colors;
        }
    }
}
