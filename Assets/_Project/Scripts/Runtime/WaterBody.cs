using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// The logical region of a pond or the moat: where wildlife may wander and how deep the basin goes.
    /// Purely data + geometry queries; the visual surface is a sibling mesh with the water shader, built
    /// by Tools &gt; NASA Sim &gt; Water &gt; Create Water Body. Positioned at the water SURFACE height.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WaterBody : MonoBehaviour
    {
        public enum Shape { Box, Annulus }

        public Shape shape = Shape.Annulus;
        [Tooltip("Annulus: inner edge of the ring (m).")]
        [Min(0f)] public float innerRadius = 27f;
        [Tooltip("Annulus: outer edge of the ring (m).")]
        [Min(0.1f)] public float outerRadius = 31f;
        [Tooltip("Box: X/Z extents of the pond (Y ignored).")]
        public Vector3 boxSize = new Vector3(8f, 0f, 8f);
        [Tooltip("How far the basin floor sits below the surface (m).")]
        [Min(0.05f)] public float depth = 0.8f;

        /// <summary>World Y of the water surface.</summary>
        public float SurfaceY => transform.position.y;

        /// <summary>Uniform random point ON the surface (at SurfaceY), kept a margin off the banks.</summary>
        public Vector3 RandomPointOnSurface(float margin = 0.5f)
        {
            if (shape == Shape.Annulus)
            {
                float rIn = innerRadius + margin;
                float rOut = Mathf.Max(rIn + 0.01f, outerRadius - margin);
                // sqrt-lerp of the squared radii = uniform density over the ring's AREA
                float r = Mathf.Sqrt(Mathf.Lerp(rIn * rIn, rOut * rOut, Random.value));
                float a = Random.value * Mathf.PI * 2f;
                return new Vector3(transform.position.x + Mathf.Cos(a) * r,
                                   SurfaceY,
                                   transform.position.z + Mathf.Sin(a) * r);
            }

            float hx = Mathf.Max(0.01f, boxSize.x * 0.5f - margin);
            float hz = Mathf.Max(0.01f, boxSize.z * 0.5f - margin);
            return new Vector3(transform.position.x + Random.Range(-hx, hx),
                               SurfaceY,
                               transform.position.z + Random.Range(-hz, hz));
        }

        /// <summary>Clamp a world position into the region horizontally (Y is preserved).</summary>
        public Vector3 ClampInside(Vector3 p, float margin = 0.5f)
        {
            Vector3 local = p - transform.position;

            if (shape == Shape.Annulus)
            {
                var flat = new Vector2(local.x, local.z);
                float rIn = innerRadius + margin;
                float rOut = Mathf.Max(rIn + 0.01f, outerRadius - margin);
                float r = flat.magnitude;
                if (r < 1e-4f) { flat = Vector2.right; r = 1f; }
                float clamped = Mathf.Clamp(r, rIn, rOut);
                flat = flat / r * clamped;
                return new Vector3(transform.position.x + flat.x, p.y, transform.position.z + flat.y);
            }

            float hx = Mathf.Max(0.01f, boxSize.x * 0.5f - margin);
            float hz = Mathf.Max(0.01f, boxSize.z * 0.5f - margin);
            return new Vector3(transform.position.x + Mathf.Clamp(local.x, -hx, hx),
                               p.y,
                               transform.position.z + Mathf.Clamp(local.z, -hz, hz));
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.9f);
            if (shape == Shape.Annulus)
            {
                DrawCircle(innerRadius);
                DrawCircle(outerRadius);
            }
            else
            {
                Gizmos.DrawWireCube(transform.position, new Vector3(boxSize.x, 0.02f, boxSize.z));
            }
        }

        void DrawCircle(float radius, int segments = 64)
        {
            Vector3 c = transform.position;
            Vector3 prev = c + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 p = c + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
                Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
