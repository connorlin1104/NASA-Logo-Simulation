using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NasaSim
{
    /// <summary>
    /// Milestone 1 mowing visual: lays a flat "mowed grass" ribbon on the ground.
    ///
    /// A TrailRenderer is a single continuous ribbon, so to draw the DISCONNECTED strokes of the NASA logo
    /// we spawn one TrailRenderer per stroke: pen-down starts a fresh trail at the brush position, pen-up
    /// freezes it (leaves it in the scene). This guarantees no connector line is ever drawn across the gaps
    /// between letters — which toggling <c>emitting</c> alone does not, because re-enabling a single trail
    /// connects a segment back to its last vertex.
    ///
    /// The ribbon lies FLAT because each stroke transform is rotated so its local +Z points to world +Y and
    /// <see cref="LineAlignment.TransformZ"/> is used (the classic "trail stands up on the ground" gotcha).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MowingVisual_Trail : MonoBehaviour, IMowingVisual
    {
        [Header("Ribbon")]
        [Tooltip("Mowed-grass material (URP Unlit works well). If null, a fallback is created at runtime.")]
        public Material trailMaterial;
        [Tooltip("Ribbon width — match the mower deck width. Keep it small for crisp fine detail (letter counters).")]
        [Min(0.01f)] public float width = 0.4f;
        [Tooltip("Minimum spacing between trail vertices — bounds the vertex budget on long paths.")]
        [Min(0.01f)] public float minVertexDistance = 0.3f;
        [Tooltip("Lift above the floor to avoid z-fighting with the ground plane.")]
        public float heightOffset = 0.05f;
        [Tooltip("Used only when trailMaterial is null (runtime fallback material). Bold vs the grass so it's visible.")]
        public Color fallbackColor = new Color(0.86f, 0.78f, 0.52f, 1f);

        // Trails effectively never expire; large finite value avoids any Infinity edge cases.
        const float PermanentTime = 100000f;

        Transform _strokesRoot;
        TrailRenderer _active;
        Vector3 _lastWorldPos;
        bool _hasPos;
        readonly List<TrailRenderer> _strokes = new List<TrailRenderer>();

        void Awake() => EnsureRoot();

        void EnsureRoot()
        {
            if (_strokesRoot != null) return;
            var go = new GameObject("MowedStrokes");
            _strokesRoot = go.transform;      // kept at world origin, identity rotation
        }

        public void SetPenDown(bool down)
        {
            if (down) BeginStroke();
            else EndStroke();
        }

        public void UpdateAt(Vector3 worldPosition)
        {
            _lastWorldPos = worldPosition;
            _hasPos = true;
            if (_active != null)
                _active.transform.position = new Vector3(worldPosition.x, worldPosition.y + heightOffset, worldPosition.z);
        }

        void BeginStroke()
        {
            EndStroke();                      // safety: never keep two active at once
            EnsureRoot();

            Vector3 start = _hasPos ? _lastWorldPos : Vector3.zero;
            var go = new GameObject("Stroke");
            go.transform.SetParent(_strokesRoot, worldPositionStays: false);
            // +Z -> +Y so the TransformZ-aligned ribbon lies flat on the ground.
            go.transform.SetPositionAndRotation(
                new Vector3(start.x, start.y + heightOffset, start.z),
                Quaternion.Euler(-90f, 0f, 0f));

            var tr = go.AddComponent<TrailRenderer>();
            tr.alignment = LineAlignment.TransformZ;
            tr.time = PermanentTime;
            tr.autodestruct = false;
            tr.emitting = true;
            tr.minVertexDistance = minVertexDistance;
            tr.widthMultiplier = width;
            tr.numCapVertices = 4;
            tr.numCornerVertices = 4;
            tr.textureMode = LineTextureMode.Stretch;
            tr.generateLightingData = false;
            tr.shadowCastingMode = ShadowCastingMode.Off;
            tr.receiveShadows = false;
            tr.material = trailMaterial != null ? trailMaterial : GetFallbackMaterial();
            // Flat, opaque vertex color so the material shows at full strength (the default trail gradient
            // fades alpha toward the tail, which reads as "faint").
            tr.startColor = Color.white;
            tr.endColor = Color.white;
            tr.widthCurve = AnimationCurve.Constant(0f, 1f, 1f);   // uniform width, no taper

            _active = tr;
            _strokes.Add(tr);
        }

        void EndStroke()
        {
            if (_active == null) return;
            _active.emitting = false;         // freeze this stroke in place
            _active = null;
        }

        public void ResetVisual()
        {
            EndStroke();
            for (int i = 0; i < _strokes.Count; i++)
                if (_strokes[i] != null) Destroy(_strokes[i].gameObject);
            _strokes.Clear();
            _hasPos = false;
        }

        Material _fallback;
        Material GetFallbackMaterial()
        {
            if (_fallback != null) return _fallback;
            Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            _fallback = new Material(sh) { name = "MowedGrass (runtime fallback)" };
            if (_fallback.HasProperty("_BaseColor")) _fallback.SetColor("_BaseColor", fallbackColor);
            if (_fallback.HasProperty("_Color")) _fallback.SetColor("_Color", fallbackColor);
            if (_fallback.HasProperty("_Cull")) _fallback.SetFloat("_Cull", 0f);   // double-sided so it can't be culled away
            return _fallback;
        }
    }
}
