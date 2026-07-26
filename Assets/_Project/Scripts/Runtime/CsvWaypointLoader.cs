using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Parses a CSV of waypoints (Maya-style x,y,z rows) into a <see cref="WaypointPath"/> laid flat on
    /// the ground (XZ) plane. Drop your CSV onto the <see cref="csvFile"/> slot in the Inspector.
    ///
    /// Handles: optional header/comment lines, culture-invariant float parsing, ragged/malformed rows
    /// (skipped with a warning), Maya Y-up -> Unity ground remap, recenter/scale/auto-fit, and pen-up
    /// detection so the mower lifts between disconnected strokes (e.g. the N-A-S-A letters).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CsvWaypointLoader : MonoBehaviour
    {
        public enum Axis { X, Y, Z, NegX, NegY, NegZ }

        public enum PenSource
        {
            /// <summary>Every segment is drawn (single continuous stroke).</summary>
            AlwaysDown,
            /// <summary>Read a 4th column: 0 = pen up, non-zero = pen down.</summary>
            FourthColumn,
            /// <summary>Treat abnormally long segments as pen-up jumps between strokes.</summary>
            AutoJumpBreak,
        }

        [Header("Source — drop your CSV here")]
        [Tooltip("The waypoint CSV (imported as a TextAsset). Rows of x,y,z, optional 4th pen column.")]
        public TextAsset csvFile;
        [Tooltip("Skip the first non-empty, non-comment line (column headers).")]
        public bool hasHeaderRow = true;
        [Tooltip("Field delimiter. Use \";\" if your locale exports semicolon-separated CSV.")]
        public string delimiter = ",";

        [Header("Column indices (0-based)")]
        public int columnX = 0;
        public int columnY = 1;
        public int columnZ = 2;

        [Header("Axis remap (Maya Y-up -> Unity ground XZ)")]
        [Tooltip("Where CSV column X goes in Unity space.")]
        public Axis mapColumnXTo = Axis.X;
        [Tooltip("Where CSV column Y goes. For a flat ground logo, Maya's up-axis maps to Unity Z.")]
        public Axis mapColumnYTo = Axis.Z;
        [Tooltip("Where CSV column Z goes. Usually flattened away for a ground logo.")]
        public Axis mapColumnZTo = Axis.NegY;

        [Header("Placement")]
        [Tooltip("Force every point to Ground Y so the trace stays exactly on the floor plane.")]
        public bool flattenToGround = true;
        public float groundY = 0f;
        [Tooltip("Recenter the path so its centroid sits on this object's origin.")]
        public bool recenter = true;
        public Vector3 originOffset = Vector3.zero;
        public Vector3 scale = Vector3.one;

        [Header("Auto-fit")]
        [Tooltip("Uniformly scale so the largest ground dimension equals Target World Size. Solves arbitrary Maya units.")]
        public bool autoFitToSize = true;
        [Min(0.01f)] public float targetWorldSize = 20f;

        [Header("Pen / stroke breaks")]
        public PenSource penSource = PenSource.AutoJumpBreak;
        [Tooltip("FourthColumn only: index of the pen flag column (0 = pen up, non-zero = pen down).")]
        public int penColumn = 3;
        [Tooltip("AutoJumpBreak: optional ABSOLUTE minimum jump length (world units). The primary test is relative " +
                 "(4 x median segment) so break detection scales with the logo; 0 keeps it fully scale-independent.")]
        [Min(0f)] public float jumpBreakDistance = 0f;

        [Header("Render layers (draw order)")]
        [Tooltip("Optional column holding an integer render layer (0,1,2...). Higher layers are lifted slightly so " +
                 "they draw on top of lower ones (e.g. the NASA letters above the circle). -1 disables.")]
        public int layerColumn = 4;
        [Tooltip("World-space height added per layer, applied AFTER auto-fit so it stays a small constant offset " +
                 "regardless of Target World Size. Just enough to win the depth test against a lower layer.")]
        [Min(0f)] public float layerStep = 0.06f;

        WaypointPath _current;
        public WaypointPath Current => _current;

        void Awake() => Load();

        /// <summary>Parse (or re-parse) <see cref="csvFile"/>. Safe to call at edit time (gizmos) and at runtime.</summary>
        public WaypointPath Load()
        {
            _current = Parse(csvFile);
            if (Application.isPlaying)
                Debug.Log($"[CsvWaypointLoader] Loaded '{(csvFile != null ? csvFile.name : "<none>")}': " +
                          $"{_current.Count} points, {_current.StrokeBreakCount} pen-up lifts. " +
                          "(Lifts should be 56 for the clean logo — one per stroke; hundreds means it's " +
                          "fragmenting — point the Csv File at nasa_logo_clean.csv.)", this);
            return _current;
        }

        public WaypointPath Parse(TextAsset asset)
        {
            var emptyPath = new WaypointPath(new List<Waypoint>(), new Bounds(Vector3.zero, Vector3.zero), 0);
            if (asset == null || string.IsNullOrEmpty(asset.text))
                return emptyPath;

            char delim = (!string.IsNullOrEmpty(delimiter)) ? delimiter[0] : ',';

            // ---- Pass 1: read mapped positions, (optional) explicit pen flags, and (optional) render layer ----
            var raw = new List<Vector3>();
            var explicitPen = new List<bool>();
            var layerIndex = new List<int>();
            bool sawPenColumn = false;

            int needCols = Mathf.Max(columnX, Mathf.Max(columnY, columnZ)) + 1;
            // In FourthColumn mode a row missing the pen column is malformed for that mode: require it, so such
            // rows are skipped-with-warning instead of silently defaulting to pen-down.
            if (penSource == PenSource.FourthColumn && penColumn >= 0)
                needCols = Mathf.Max(needCols, penColumn + 1);
            string[] lines = asset.text.Split('\n');
            bool headerSkipped = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r').Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (hasHeaderRow && !headerSkipped)
                {
                    headerSkipped = true;
                    continue;
                }

                string[] tok = line.Split(delim);
                if (tok.Length < needCols)
                {
                    Debug.LogWarning($"[CsvWaypointLoader] Line {i + 1}: expected >= {needCols} columns, got {tok.Length}. Skipped.", this);
                    continue;
                }

                if (!TryParseFloat(tok[columnX], out float cx) ||
                    !TryParseFloat(tok[columnY], out float cy) ||
                    !TryParseFloat(tok[columnZ], out float cz))
                {
                    Debug.LogWarning($"[CsvWaypointLoader] Line {i + 1}: non-numeric coordinate. Skipped.", this);
                    continue;
                }

                raw.Add(MapAxes(cx, cy, cz));

                // Read an optional pen-flag column (0 = up, 1 = down). Auto-used whenever present so a CSV
                // with explicit lifts draws correctly no matter what Pen Source is set to. Only counts if
                // the values look like binary flags (0/1), to avoid mistaking some other 4th column for pen.
                bool pen = true;
                if (penColumn >= 0 && tok.Length > penColumn && TryParseFloat(tok[penColumn], out float pf))
                {
                    float a = Mathf.Abs(pf);
                    if (a < 0.01f || Mathf.Abs(a - 1f) < 0.01f)
                    {
                        sawPenColumn = true;
                        pen = a > 0.5f;
                    }
                }
                explicitPen.Add(pen);

                // Optional render-layer column: an integer height rank (0 = ground). Missing => layer 0.
                int lyr = 0;
                if (layerColumn >= 0 && tok.Length > layerColumn && TryParseFloat(tok[layerColumn], out float lf))
                    lyr = Mathf.RoundToInt(lf);
                layerIndex.Add(lyr);
            }

            if (raw.Count == 0)
            {
                Debug.LogWarning("[CsvWaypointLoader] No valid waypoints parsed.", this);
                return emptyPath;
            }

            // ---- Transform: recenter -> scale -> auto-fit -> offset -> flatten ----
            Vector3 centroid = Vector3.zero;
            for (int k = 0; k < raw.Count; k++) centroid += raw[k];
            centroid /= raw.Count;

            var work = new List<Vector3>(raw.Count);
            for (int k = 0; k < raw.Count; k++)
            {
                Vector3 p = raw[k];
                if (recenter) p -= centroid;
                p = Vector3.Scale(p, scale);
                work.Add(p);
            }

            if (autoFitToSize)
            {
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                for (int k = 0; k < work.Count; k++)
                {
                    Vector3 p = work[k];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }
                float largest = Mathf.Max(maxX - minX, maxZ - minZ);
                if (largest > 1e-5f)
                {
                    float f = targetWorldSize / largest;
                    for (int k = 0; k < work.Count; k++) work[k] *= f;
                }
            }

            var positions = new List<Vector3>(work.Count);
            Bounds bounds = default;
            for (int k = 0; k < work.Count; k++)
            {
                Vector3 p = work[k] + originOffset;
                if (flattenToGround) p.y = groundY;
                // Lift by render layer AFTER auto-fit, so it is a small constant world offset (not scaled with the
                // logo). Higher layers sit slightly above lower ones and win the depth test — the NASA letters on top.
                p.y += layerIndex[k] * layerStep;
                positions.Add(p);
                if (k == 0) bounds = new Bounds(p, Vector3.zero);
                else bounds.Encapsulate(p);
            }

            // ---- Pen state: penDown[k] == draw while travelling INTO point k ----
            var penDown = new bool[positions.Count];
            penDown[0] = false;
            int breaks = 0;

            // A valid pen-flag column is auto-used whenever present (unless AlwaysDown), so a CSV with
            // explicit lifts draws correctly regardless of the Pen Source setting. This is what keeps
            // dense/uneven exports from fragmenting into disconnected dots.
            if (sawPenColumn && penSource != PenSource.AlwaysDown)
            {
                for (int k = 1; k < positions.Count; k++)
                {
                    penDown[k] = explicitPen[k];
                    if (!explicitPen[k]) breaks++;
                }
            }
            else if (penSource == PenSource.FourthColumn)
            {
                for (int k = 1; k < positions.Count; k++) penDown[k] = true;
                Debug.LogWarning("[CsvWaypointLoader] Pen Source = Fourth Column but no valid pen column was found; drawing every segment.", this);
            }
            else if (penSource == PenSource.AlwaysDown)
            {
                for (int k = 1; k < positions.Count; k++) penDown[k] = true;
            }
            else // AutoJumpBreak
            {
                // segLen[k-1] is the length of the segment INTO point k.
                var segLen = new List<float>(positions.Count);
                for (int k = 1; k < positions.Count; k++)
                    segLen.Add(Vector3.Distance(positions[k - 1], positions[k]));
                // Primary test is relative (4 x median => scales with the logo); jumpBreakDistance is an
                // optional absolute floor. Reuse segLen instead of recomputing each distance.
                float threshold = Mathf.Max(jumpBreakDistance, 4f * Median(segLen));
                for (int k = 1; k < positions.Count; k++)
                {
                    bool down = segLen[k - 1] <= threshold;
                    penDown[k] = down;
                    if (!down) breaks++;
                }
            }

            var points = new List<Waypoint>(positions.Count);
            for (int k = 0; k < positions.Count; k++)
                points.Add(new Waypoint(positions[k], penDown[k]));

            return new WaypointPath(points, bounds, breaks);
        }

        static bool TryParseFloat(string s, out float v)
            => float.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        Vector3 MapAxes(float cx, float cy, float cz)
        {
            Vector3 r = Vector3.zero;
            ApplyAxis(mapColumnXTo, cx, ref r);
            ApplyAxis(mapColumnYTo, cy, ref r);
            ApplyAxis(mapColumnZTo, cz, ref r);
            return r;
        }

        static void ApplyAxis(Axis axis, float value, ref Vector3 r)
        {
            switch (axis)
            {
                case Axis.X: r.x += value; break;
                case Axis.Y: r.y += value; break;
                case Axis.Z: r.z += value; break;
                case Axis.NegX: r.x -= value; break;
                case Axis.NegY: r.y -= value; break;
                case Axis.NegZ: r.z -= value; break;
            }
        }

        static float Median(List<float> values)
        {
            if (values == null || values.Count == 0) return 0f;
            var copy = new List<float>(values);
            copy.Sort();
            int n = copy.Count;
            return (n % 2 == 1) ? copy[n / 2] : 0.5f * (copy[n / 2 - 1] + copy[n / 2]);
        }
    }
}
