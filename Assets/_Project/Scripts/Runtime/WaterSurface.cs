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

        /// <summary>
        /// The clock the water is animated on — unscaled, and ticking in the editor as well. Anything
        /// that has to agree with the drawn surface reads it from here (<see cref="WaterBody"/> uses it to
        /// work out the wave a duck is sitting on).
        /// </summary>
        public static float Clock
        {
            get
            {
#if UNITY_EDITOR
                return Application.isPlaying
                    ? Time.unscaledTime
                    : (float)UnityEditor.EditorApplication.timeSinceStartup;
#else
                return Time.unscaledTime;
#endif
            }
        }

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

            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetFloat(WaterTimeId, Clock);
            _renderer.SetPropertyBlock(_mpb);
        }
    }
}
