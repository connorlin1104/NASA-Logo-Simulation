using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Bakes a cheap, low-resolution collision surface for a polygon-heavy terrain model so the astronaut
    /// can walk on it without the physics engine testing against hundreds of thousands of triangles.
    ///
    /// It works like a heightmap: a coarse grid is laid over the model's footprint and a ray is cast
    /// straight down at every grid point to read the surface height. Those heights become a light mesh
    /// (tens of thousands of triangles instead of the model's full count) that is assigned to a
    /// MeshCollider. The heavy model still renders exactly as before - only its COLLISION is simplified.
    ///
    /// Trade-off: only the top surface is captured, so overhangs, caves and cliffs that double back are
    /// flattened. For an open lunar surface that is exactly what you want to walk on.
    /// </summary>
    public sealed class TerrainColliderBaker : EditorWindow
    {
        GameObject terrain;
        int resolution = 128;             // grid cells per side; vertices = resolution + 1
        bool disableSourceColliders = true;

        const string DataDir = "Assets/_Project/Data";

        [MenuItem("Tools/NASA Sim/Environment/Bake Simplified Collider")]
        public static void Open()
        {
            var window = GetWindow<TerrainColliderBaker>(true, "Bake Simplified Collider", true);
            window.minSize = new Vector2(440f, 320f);
            window.terrain = Selection.activeGameObject;
            window.Show();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Bakes a light heightfield collider for a heavy terrain model so you can walk on it " +
                "cheaply. The model keeps rendering as-is; only its collision is simplified.\n\n" +
                "Select the terrain model, then Bake. Overhangs and caves are flattened - fine for an " +
                "open surface.",
                MessageType.Info);

            terrain = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Terrain model", "The imported terrain in the scene."),
                terrain, typeof(GameObject), true);

            resolution = EditorGUILayout.IntSlider(
                new GUIContent("Resolution", "Grid cells per side. Higher = follows the surface more " +
                                             "closely but a heavier collider. 128 is a good start " +
                                             "(~33k triangles)."),
                resolution, 16, 512);

            disableSourceColliders = EditorGUILayout.Toggle(
                new GUIContent("Disable source colliders", "Turn off any existing heavy MeshCollider on " +
                                                           "the model so physics only uses the light one."),
                disableSourceColliders);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(terrain == null))
                if (GUILayout.Button("Bake Collider", GUILayout.Height(30f)))
                    Bake();
        }

        void Bake()
        {
            if (!terrain.scene.IsValid())
            {
                EditorUtility.DisplayDialog("Select the scene object",
                    "That is a Project-window asset. Drag the terrain into the scene, select it, then run " +
                    "this again.", "OK");
                return;
            }

            if (!TryGetRenderBounds(terrain, out Bounds bounds))
            {
                EditorUtility.DisplayDialog("Nothing to measure",
                    $"'{terrain.name}' has no renderers to size the collider from.", "OK");
                return;
            }

            // We sample by raycasting the model's colliders. If it has none yet, add temporary ones just
            // for the bake (cooking a heavy mesh once in the editor is fine; the runtime cost is the point
            // we are removing).
            var sampleColliders = new List<Collider>(terrain.GetComponentsInChildren<Collider>(true));
            var temporary = new List<Collider>();
            if (sampleColliders.Count == 0)
            {
                foreach (var mf in terrain.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null) continue;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    temporary.Add(mc);
                    sampleColliders.Add(mc);
                }
                if (sampleColliders.Count == 0)
                {
                    EditorUtility.DisplayDialog("No mesh to sample",
                        $"'{terrain.name}' has renderers but no meshes to raycast against.", "OK");
                    return;
                }
            }

            int verts = resolution + 1;
            float top = bounds.max.y + 10f;
            float floor = bounds.min.y;
            var vertices = new Vector3[verts * verts];
            int hits = 0;

            try
            {
                for (int z = 0; z < verts; z++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Baking terrain collider", $"row {z + 1}/{verts}", (float)z / verts))
                        return;

                    float wz = Mathf.Lerp(bounds.min.z, bounds.max.z, z / (verts - 1f));
                    for (int x = 0; x < verts; x++)
                    {
                        float wx = Mathf.Lerp(bounds.min.x, bounds.max.x, x / (verts - 1f));
                        float y = SampleHeight(sampleColliders, new Vector3(wx, top, wz), floor, out bool hit);
                        if (hit) hits++;
                        vertices[z * verts + x] = new Vector3(wx, y, wz);   // world space
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                foreach (var mc in temporary) if (mc != null) DestroyImmediate(mc);
            }

            if (hits == 0)
            {
                EditorUtility.DisplayDialog("No surface found",
                    "The down-rays never hit the terrain. Make sure the model has meshes and that the " +
                    "object you selected is the terrain itself.", "OK");
                return;
            }

            var mesh = BuildGridMesh(vertices, verts);

            Directory.CreateDirectory(DataDir);
            string meshPath = $"{DataDir}/TerrainCollider_{Sanitize(terrain.name)}.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            if (disableSourceColliders)
                foreach (var c in terrain.GetComponentsInChildren<Collider>(true))
                    if (c is MeshCollider heavy) { Undo.RecordObject(heavy, "Disable source collider"); heavy.enabled = false; }

            string colliderName = $"TerrainCollider_{terrain.name}";
            var go = GameObject.Find(colliderName);
            if (go == null)
            {
                go = new GameObject(colliderName);
                Undo.RegisterCreatedObjectUndo(go, "Bake Terrain Collider");
            }
            else
            {
                Undo.RegisterFullObjectHierarchyUndo(go, "Rebake Terrain Collider");
            }
            go.transform.SetParent(null, worldPositionStays: true);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;

            var col = go.GetComponent<MeshCollider>();
            if (col == null) col = go.AddComponent<MeshCollider>();
            col.sharedMesh = null;
            col.convex = false;
            col.sharedMesh = mesh;

            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log($"[TerrainCollider] Baked a {resolution}x{resolution} collider ({resolution * resolution * 2:N0} " +
                      $"triangles) for '{terrain.name}' from {hits:N0} surface samples. The heavy model still " +
                      "renders; physics now uses this. Re-bake if you move or reshape the terrain.", go);
        }

        static float SampleHeight(List<Collider> colliders, Vector3 origin, float floor, out bool hit)
        {
            var ray = new Ray(origin, Vector3.down);
            float nearest = float.MaxValue;
            hit = false;
            foreach (var c in colliders)
            {
                if (c == null || !c.enabled) continue;
                if (c.Raycast(ray, out RaycastHit h, 100000f) && h.distance < nearest)
                {
                    nearest = h.distance;
                    hit = true;
                }
            }
            return hit ? origin.y - nearest : floor;
        }

        static Mesh BuildGridMesh(Vector3[] vertices, int verts)
        {
            var tris = new int[(verts - 1) * (verts - 1) * 6];
            int t = 0;
            for (int z = 0; z < verts - 1; z++)
                for (int x = 0; x < verts - 1; x++)
                {
                    int i = z * verts + x;
                    tris[t++] = i;
                    tris[t++] = i + verts;
                    tris[t++] = i + verts + 1;
                    tris[t++] = i;
                    tris[t++] = i + verts + 1;
                    tris[t++] = i + 1;
                }

            var mesh = new Mesh { name = "TerrainCollider" };
            mesh.indexFormat = vertices.Length > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = vertices;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static bool TryGetRenderBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool has = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (!has) { bounds = r.bounds; has = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return has;
        }

        static string Sanitize(string s)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '_');
            return s;
        }
    }
}
