using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Sits on a generated station light and draws how far it actually reaches, all the time rather than
    /// only when selected. A point light's icon tells you nothing about its range, and the usual way to
    /// discover that a room is unlit is to press Play and find it dark — this draws the reach in the
    /// Scene view so you can see the gaps between lights before that happens.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public sealed class StationLightMarker : MonoBehaviour
    {
        [Tooltip("The group this light was placed over. Informational.")]
        public string area;

        public bool showGizmo = true;
        [Tooltip("Draw the full range sphere. Off draws just a small cross, for when a dozen overlapping " +
                 "spheres make the Scene view unreadable.")]
        public bool showRange = true;

        void OnDrawGizmos()
        {
            if (!showGizmo) return;
            var light = GetComponent<Light>();
            if (light == null) return;

            Color c = light.color;
            Gizmos.color = new Color(c.r, c.g, c.b, 0.9f);

            float tick = 0.25f;
            Gizmos.DrawLine(transform.position - Vector3.up * tick, transform.position + Vector3.up * tick);
            Gizmos.DrawLine(transform.position - Vector3.right * tick, transform.position + Vector3.right * tick);
            Gizmos.DrawLine(transform.position - Vector3.forward * tick, transform.position + Vector3.forward * tick);

            if (showRange && light.type != LightType.Directional)
            {
                Gizmos.color = new Color(c.r, c.g, c.b, 0.22f);
                Gizmos.DrawWireSphere(transform.position, light.range);
            }
        }
    }
}
