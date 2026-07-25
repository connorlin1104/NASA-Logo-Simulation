using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Feeds the water shader its animation clock (<c>_WaterTime</c>). The shader deliberately ignores
    /// Unity's builtin <c>_Time</c>, which is scaled by <c>Time.timeScale</c> — at the sim's 16x
    /// fast-forward the moat would froth comically. Water is scenery the player watches up close, so it
    /// runs on UNSCALED time (see <see cref="AstronautController"/> for the project convention).
    ///
    /// Uses a MaterialPropertyBlock so multiple ponds share one material asset without instancing it.
    /// [ExecuteAlways] keeps the water moving while tuning in the editor (it advances whenever the
    /// editor repaints; press Play for perfectly smooth motion).
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public sealed class WaterSurface : MonoBehaviour
    {
        static readonly int WaterTimeId = Shader.PropertyToID("_WaterTime");

        Renderer _renderer;
        MaterialPropertyBlock _mpb;

        void OnEnable()
        {
            _renderer = GetComponent<Renderer>();
            _mpb = new MaterialPropertyBlock();
        }

        void Update()
        {
            if (_renderer == null || _mpb == null) return;

            float t;
#if UNITY_EDITOR
            t = Application.isPlaying
                ? Time.unscaledTime
                : (float)UnityEditor.EditorApplication.timeSinceStartup;
#else
            t = Time.unscaledTime;
#endif
            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetFloat(WaterTimeId, t);
            _renderer.SetPropertyBlock(_mpb);
        }
    }
}
