using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// A baked walkable surface: one smooth collision skin laid over a modelled staircase, ramp or deck
    /// so the astronaut walks up it instead of catching on every modelled step.
    ///
    /// A <see cref="CharacterController"/> only mounts ledges shorter than its Step Offset and only walks
    /// slopes under its Slope Limit, so a stair modelled as real steps reads to it as a wall. The baker
    /// (Tools &gt; NASA Sim &gt; Station &gt; Colliders &amp; Stairs) samples the model from above and
    /// smooths the tread profile into a ramp — which works for a straight flight, a half-moon sweep or a
    /// full spiral alike, because it follows the model's own footprint rather than assuming a shape.
    ///
    /// This component exists for one reason: to DRAW that surface in the Scene view all the time, not
    /// only when it happens to be selected. Green wireframe = ground the astronaut can stand on. If the
    /// green doesn't cover your steps, re-bake with a different setting; you never have to press Play to
    /// find that out.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshCollider))]
    public sealed class WalkSurface : MonoBehaviour
    {
        [Header("What this was baked from")]
        [Tooltip("Hierarchy path of the model group this surface was baked over. Informational.")]
        public string sourcePath;
        [Tooltip("Steepest walkable slope on the baked surface, in degrees. Must stay under the " +
                 "astronaut's Slope Limit (50°) or the ramp is as unclimbable as the steps were.")]
        public float maxSlopeDeg;
        [Tooltip("Footprint area covered, in square metres. Informational.")]
        public float areaSqM;

        [Header("Scene view")]
        public bool showGizmo = true;
        public Color gizmoColor = new Color(0.32f, 1f, 0.45f, 0.9f);
        [Tooltip("Above this triangle count the wireframe is replaced by a plain outline box — drawing " +
                 "tens of thousands of gizmo lines every repaint would crawl.")]
        [Min(0)] public int wireTriangleBudget = 20000;

        MeshCollider _collider;
        Mesh _measured;
        int _triangles;

        Mesh SurfaceMesh
        {
            get
            {
                if (_collider == null) _collider = GetComponent<MeshCollider>();
                return _collider != null ? _collider.sharedMesh : null;
            }
        }

        /// <summary>
        /// Triangle count, cached per mesh. Deliberately NOT <c>mesh.triangles.Length</c>: that property
        /// copies the whole index buffer, and this runs on every Scene view repaint.
        /// </summary>
        int TriangleCount(Mesh mesh)
        {
            if (ReferenceEquals(mesh, _measured)) return _triangles;
            _measured = mesh;
            _triangles = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) _triangles += (int)(mesh.GetIndexCount(s) / 3);
            return _triangles;
        }

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            Mesh mesh = SurfaceMesh;
            if (mesh == null) return;

            Gizmos.matrix = transform.localToWorldMatrix;

            if (TriangleCount(mesh) <= wireTriangleBudget)
            {
                Gizmos.color = gizmoColor;
                Gizmos.DrawWireMesh(mesh);
            }
            else
            {
                Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 0.5f);
                Gizmos.DrawWireCube(mesh.bounds.center, mesh.bounds.size);
            }

            Gizmos.matrix = Matrix4x4.identity;

#if UNITY_EDITOR
            Vector3 label = transform.TransformPoint(mesh.bounds.center + Vector3.up * mesh.bounds.extents.y);
            UnityEditor.Handles.color = gizmoColor;
            UnityEditor.Handles.Label(label,
                $"walkable · {areaSqM:0} m² · up to {maxSlopeDeg:0}°" +
                (maxSlopeDeg > 50f ? "  ⚠ TOO STEEP" : string.Empty));
#endif
        }
    }
}
