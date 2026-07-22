using System.Collections.Generic;
using UnityEngine;

namespace NasaSim
{
    /// <summary>A single processed point on the mowing path.</summary>
    public struct Waypoint
    {
        /// <summary>Position in the same space the loader outputs (local to the WaypointsHolder / world).</summary>
        public Vector3 position;

        /// <summary>
        /// True when the mower is "down" while travelling INTO this point (draw the trail on this segment).
        /// False marks a pen-up jump between strokes — e.g. the gaps between the N-A-S-A letters.
        /// The first point is always penDown == false (there is no segment leading into it).
        /// </summary>
        public bool penDown;

        public Waypoint(Vector3 position, bool penDown)
        {
            this.position = position;
            this.penDown = penDown;
        }
    }

    /// <summary>Ordered, fully-processed waypoint path plus metadata for framing and gizmos.</summary>
    public sealed class WaypointPath
    {
        public readonly IReadOnlyList<Waypoint> Points;
        public readonly Bounds Bounds;
        public readonly int StrokeBreakCount;

        public WaypointPath(IReadOnlyList<Waypoint> points, Bounds bounds, int strokeBreakCount)
        {
            Points = points;
            Bounds = bounds;
            StrokeBreakCount = strokeBreakCount;
        }

        public int Count => Points?.Count ?? 0;
        public bool IsEmpty => Count == 0;
    }
}
