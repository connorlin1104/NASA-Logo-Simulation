using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// The Inspector for a pond or the moat. Two jobs:
    ///
    /// <b>Show only what the shape uses</b> — a square moat has an inner size and a band width, a round
    /// pond has a radius, and neither wants to see the other's fields.
    ///
    /// <b>Make it draggable</b> — cone handles on the water's edges in the Scene view, so the moat is
    /// resized by hauling its banks around rather than by typing. The mesh regenerates as you drag
    /// (<see cref="WaterBody"/> rebuilds on every change), which is the whole point: when the container
    /// model arrives, park the water in it and pull the edges out until they meet its walls.
    /// </summary>
    [CustomEditor(typeof(WaterBody))]
    [CanEditMultipleObjects]
    public sealed class WaterBodyEditor : Editor
    {
        float _fitGap = 0.5f;
        int _ducks = -1, _fish = -1, _lilypads = -1;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var body = (WaterBody)target;

            EditorGUILayout.PropertyField(serializedObject.FindProperty("shape"));
            EditorGUILayout.Space();

            switch (body.shape)
            {
                case WaterBody.Shape.RectRing:
                    Field("innerSize", "Inner size (m)", "The dry square the water runs around.");
                    Field("bandWidth", "Water width (m)", "How wide the band of water is.");
                    Field("cornerRadius", "Corner radius (m)", "0 keeps the moat a sharp square.");
                    EditorGUILayout.LabelField(" ", $"outer edge {body.OuterHalf.x * 2f:0.#} x " +
                                                    $"{body.OuterHalf.y * 2f:0.#} m", EditorStyles.miniLabel);
                    break;
                case WaterBody.Shape.Circle:
                    Field("radius", "Radius (m)", null);
                    break;
                case WaterBody.Shape.Annulus:
                    Field("innerRadius", "Inner radius (m)", null);
                    Field("outerRadius", "Outer radius (m)", null);
                    break;
                default:
                    Field("boxSize", "Size (m)", "X and Z are the pond's extents; Y is ignored.");
                    break;
            }

            EditorGUILayout.Space();
            Field("depth", null, null);
            Field("buildBasin", null, null);
            using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("buildBasin").boolValue))
            {
                Field("bankWidth", null, null);
                Field("basinMaterial", null, null);
            }
            EditorGUILayout.Space();
            Field("meshDensity", null, null);

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawActions(body);
        }

        void Field(string path, string label, string tooltip)
        {
            var prop = serializedObject.FindProperty(path);
            if (prop == null) return;
            if (label == null) EditorGUILayout.PropertyField(prop);
            else EditorGUILayout.PropertyField(prop, new GUIContent(label, tooltip ?? prop.tooltip));
        }

        void DrawActions(WaterBody body)
        {
            if (serializedObject.isEditingMultipleObjects) return;

            EditorGUILayout.LabelField("Fit & stock", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _fitGap = EditorGUILayout.FloatField(
                    new GUIContent("Gap from grass (m)", "Dry ground left between the grass patch and " +
                                                        "the water."), _fitGap);
                if (GUILayout.Button("Fit around grass", GUILayout.Width(120f)))
                    FitAroundGrass(body, _fitGap);
            }

            if (_ducks < 0) CountWildlife(body);
            using (new EditorGUILayout.HorizontalScope())
            {
                _ducks = EditorGUILayout.IntField("Ducks", _ducks);
                _fish = EditorGUILayout.IntField("Fish", _fish);
                _lilypads = EditorGUILayout.IntField("Lilypads", _lilypads);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply population"))
                {
                    WildlifeSpawnTool.Spawn(body, Mathf.Max(0, _ducks), Mathf.Max(0, _fish),
                                            Mathf.Max(0, _lilypads), true, false);
                    CountWildlife(body);
                }
                if (GUILayout.Button("Put wildlife back in the water"))
                {
                    Undo.RecordObjects(WildlifeTransforms(body), "Snap Wildlife Into Water");
                    body.SnapWildlifeInside();
                    EditorSceneManager.MarkSceneDirty(body.gameObject.scene);
                }
            }

            EditorGUILayout.HelpBox(
                "The mesh rebuilds itself whenever a number here changes, and the edges can be dragged " +
                "in the Scene view. When the container model arrives: drop this inside it, turn Build " +
                "Basin off, and pull the edges out to its walls.", MessageType.None);
        }

        void CountWildlife(WaterBody body)
        {
            _ducks = _fish = _lilypads = 0;
            foreach (Transform c in body.transform)
            {
                if (c.name.StartsWith("Duck_")) _ducks++;
                else if (c.name.StartsWith("Fish_")) _fish++;
                else if (c.name.StartsWith("Lilypad_")) _lilypads++;
            }
        }

        static Transform[] WildlifeTransforms(WaterBody body)
        {
            var wanderers = body.GetComponentsInChildren<WaterWanderer>(true);
            var transforms = new Transform[wanderers.Length];
            for (int i = 0; i < wanderers.Length; i++) transforms[i] = wanderers[i].transform;
            return transforms;
        }

        static void FitAroundGrass(WaterBody body, float gap)
        {
            Bounds grass = WaterBodyTool.GrassPatchBounds();
            Undo.RecordObject(body, "Fit Water To Grass");
            Undo.RecordObject(body.transform, "Fit Water To Grass");

            body.shape = WaterBody.Shape.RectRing;
            body.innerSize = new Vector2(grass.size.x, grass.size.z) + Vector2.one * (gap * 2f);
            body.transform.position = new Vector3(grass.center.x, body.transform.position.y, grass.center.z);
            body.Rebuild();
            body.SnapWildlifeInside();
            EditorSceneManager.MarkSceneDirty(body.gameObject.scene);
        }

        // ------------------------------------------------------------------ scene handles

        void OnSceneGUI()
        {
            var body = (WaterBody)target;
            var t = body.transform;

            Matrix4x4 old = Handles.matrix;
            Handles.matrix = Matrix4x4.TRS(t.position, t.rotation, t.lossyScale);
            Handles.color = new Color(0.35f, 0.8f, 1f);

            EditorGUI.BeginChangeCheck();
            float x, z, band;

            switch (body.shape)
            {
                case WaterBody.Shape.RectRing:
                    x = EdgeHandle(body.innerSize.x * 0.5f, Vector3.right, "inner");
                    z = EdgeHandle(body.innerSize.y * 0.5f, Vector3.forward, "inner");
                    band = EdgeHandle(body.innerSize.x * 0.5f + body.bandWidth, Vector3.right, "outer")
                           - body.innerSize.x * 0.5f;
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(body, "Resize Water");
                        body.innerSize = new Vector2(Mathf.Max(0.2f, x * 2f), Mathf.Max(0.2f, z * 2f));
                        body.bandWidth = Mathf.Max(0.2f, band);
                        body.Rebuild();
                    }
                    break;

                case WaterBody.Shape.Circle:
                    x = EdgeHandle(body.radius, Vector3.right, "radius");
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(body, "Resize Water");
                        body.radius = Mathf.Max(0.2f, x);
                        body.Rebuild();
                    }
                    break;

                case WaterBody.Shape.Annulus:
                    x = EdgeHandle(body.innerRadius, Vector3.right, "inner");
                    z = EdgeHandle(body.outerRadius, Vector3.forward, "outer");
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(body, "Resize Water");
                        body.innerRadius = Mathf.Max(0f, x);
                        body.outerRadius = Mathf.Max(body.innerRadius + 0.2f, z);
                        body.Rebuild();
                    }
                    break;

                default:
                    x = EdgeHandle(body.boxSize.x * 0.5f, Vector3.right, "size");
                    z = EdgeHandle(body.boxSize.z * 0.5f, Vector3.forward, "size");
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(body, "Resize Water");
                        body.boxSize = new Vector3(Mathf.Max(0.2f, x * 2f), 0f, Mathf.Max(0.2f, z * 2f));
                        body.Rebuild();
                    }
                    break;
            }

            Handles.matrix = old;
        }

        /// <summary>A cone you drag along one axis; returns the new distance from the centre.</summary>
        static float EdgeHandle(float distance, Vector3 axis, string label)
        {
            Vector3 p = axis * distance;
            float size = HandleUtility.GetHandleSize(p) * 0.16f;
            Handles.Label(p + Vector3.up * (size * 2f), $"{label} {distance:0.##} m");
            Vector3 moved = Handles.Slider(p, axis, size, Handles.ConeHandleCap, 0.1f);
            return Vector3.Dot(moved, axis);
        }
    }
}
