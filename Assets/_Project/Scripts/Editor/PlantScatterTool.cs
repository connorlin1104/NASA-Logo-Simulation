using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Rebuilds the greenhouse planting from the markers baked into Biodome.fbx.
    ///
    /// The source Maya scene grows its ~4,900 plants as live Paint Effects strokes, which export as
    /// nothing and cannot be opened in the Maya GUI without exhausting memory. So
    /// <c>Assets/_Project/Data/maya_headless_export.py</c> converts one stroke per plant type into a
    /// low-poly mesh (Plant_Prototypes.fbx) and writes an empty <c>PLANT_&lt;Type&gt;_&lt;n&gt;</c>
    /// marker at every stroke's transform inside Biodome.fbx itself. This tool pairs the two up.
    ///
    /// Markers travel in the same FBX as the dome deliberately: Unity applies one axis conversion to
    /// the whole file, so plants cannot land mirrored or offset relative to the building the way a
    /// separate coordinate list could.
    ///
    /// See Assets/_Project/Models/IMPORT_GUIDE.md.
    /// </summary>
    public sealed class PlantScatterTool : EditorWindow
    {
        const string MarkerPrefix = "PLANT_";
        const string PrototypePrefix = "NOCOL_Plant_";
        const string ScatterChildName = "ScatteredPlant";

        GameObject biodome;
        GameObject prototypes;
        [Range(1, 100)] int densityPercent = 100;
        bool enableInstancing = true;

        [MenuItem("Tools/NASA Sim/Biodome/Scatter Plants")]
        public static void Open()
        {
            var window = GetWindow<PlantScatterTool>(true, "Scatter Plants", true);
            window.minSize = new Vector2(420f, 260f);
            window.biodome = Selection.activeGameObject;
            window.prototypes = FindPrototypeAsset();
            window.Show();
        }

        static GameObject FindPrototypeAsset()
        {
            foreach (var guid in AssetDatabase.FindAssets("Plant_Prototypes t:GameObject"))
                return AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            return null;
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Drag Biodome.fbx into the scene first, then select it in the Hierarchy.\n" +
                "Every PLANT_<Type>_<n> marker inside it gets the matching low-poly plant.",
                MessageType.Info);

            biodome = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Biodome (in scene)", "The imported Biodome, selected in the Hierarchy."),
                biodome, typeof(GameObject), true);

            prototypes = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Plant prototypes", "Plant_Prototypes.fbx from the Project window."),
                prototypes, typeof(GameObject), false);

            densityPercent = EditorGUILayout.IntSlider(
                new GUIContent("Density %", "Plant only this share of the markers. Lower it if the " +
                                            "frame rate drops - the layout stays even."),
                densityPercent, 1, 100);

            enableInstancing = EditorGUILayout.Toggle(
                new GUIContent("GPU instancing", "Turn on instancing for the plant materials so all " +
                                                 "copies of one plant draw in a single call."),
                enableInstancing);

            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(biodome == null || prototypes == null))
            {
                if (GUILayout.Button("Scatter Plants", GUILayout.Height(30f))) Scatter();
            }
            if (GUILayout.Button("Remove Scattered Plants")) RemoveExisting(true);
        }

        void Scatter()
        {
            if (!biodome.scene.IsValid())
            {
                EditorUtility.DisplayDialog("Select the scene object",
                    "That is a Project-window asset. Drag Biodome.fbx into the scene, select it in the " +
                    "Hierarchy, then run this again.", "OK");
                return;
            }

            var library = BuildPrototypeLibrary();
            if (library.Count == 0)
            {
                EditorUtility.DisplayDialog("No prototypes",
                    $"'{prototypes.name}' has no children named {PrototypePrefix}<Type>. Re-run " +
                    "maya_headless_export.py with --mode plants.", "OK");
                return;
            }

            var markers = biodome.GetComponentsInChildren<Transform>(true)
                                 .Where(t => t.name.StartsWith(MarkerPrefix))
                                 .ToList();
            if (markers.Count == 0)
            {
                EditorUtility.DisplayDialog("No markers",
                    $"'{biodome.name}' contains no {MarkerPrefix}<Type>_<n> objects. Re-export with " +
                    "maya_headless_export.py --mode structure, which bakes them into Biodome.fbx.", "OK");
                return;
            }

            Undo.RegisterFullObjectHierarchyUndo(biodome, "Scatter Plants");
            RemoveExisting(false);

            // Deterministic even thinning: keep every Nth marker rather than a random subset, so the
            // same density always produces the same layout and no patch is left bare.
            int stride = Mathf.Max(1, Mathf.RoundToInt(100f / densityPercent));

            var placed = new Dictionary<string, int>();
            var missing = new HashSet<string>();
            int skipped = 0;

            try
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    if (i % stride != 0) { skipped++; continue; }

                    var marker = markers[i];
                    string type = TypeOf(marker.name);
                    if (!library.TryGetValue(type, out GameObject prototype))
                    {
                        missing.Add(type);
                        continue;
                    }

                    if (i % 256 == 0 &&
                        EditorUtility.DisplayCancelableProgressBar(
                            "Scattering plants", $"{placed.Values.Sum()} placed", (float)i / markers.Count))
                        break;

                    // Parent straight under the marker with an identity local transform. The marker
                    // already carries the plant's position, yaw and size, and the prototype was
                    // exported as a unit plant, so inheriting is both exact and free - computing a
                    // world transform instead would double-count any scale on the biodome root.
                    var instance = Object.Instantiate(prototype, marker);
                    instance.name = ScatterChildName;
                    instance.transform.localPosition = Vector3.zero;
                    instance.transform.localRotation = Quaternion.identity;
                    instance.transform.localScale = Vector3.one;

                    foreach (var collider in instance.GetComponentsInChildren<Collider>(true))
                        Object.DestroyImmediate(collider);

                    placed.TryGetValue(type, out int count);
                    placed[type] = count + 1;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (enableInstancing) EnableInstancing(library.Values);

            EditorSceneManager.MarkSceneDirty(biodome.scene);
            Debug.Log(Report(library, placed, skipped, missing), biodome);
        }

        /// <summary>Map `Carrots` -> the `NOCOL_Plant_Carrots` child of the prototype FBX.</summary>
        Dictionary<string, GameObject> BuildPrototypeLibrary()
        {
            var library = new Dictionary<string, GameObject>();
            foreach (Transform child in prototypes.transform)
            {
                if (!child.name.StartsWith(PrototypePrefix)) continue;
                library[child.name.Substring(PrototypePrefix.Length)] = child.gameObject;
            }
            return library;
        }

        /// <summary>`PLANT_FloweringPea_0123` -> `FloweringPea`.</summary>
        static string TypeOf(string markerName)
        {
            string body = markerName.Substring(MarkerPrefix.Length);
            int tail = body.LastIndexOf('_');
            return tail > 0 ? body.Substring(0, tail) : body;
        }

        /// <summary>Clear a previous scatter so density can be changed without stacking plants up.</summary>
        void RemoveExisting(bool log)
        {
            if (biodome == null) return;
            int removed = 0;
            foreach (var t in biodome.GetComponentsInChildren<Transform>(true).ToList())
            {
                if (t == null || t.name != ScatterChildName) continue;
                Undo.DestroyObjectImmediate(t.gameObject);
                removed++;
            }
            if (log)
            {
                EditorSceneManager.MarkSceneDirty(biodome.scene);
                Debug.Log($"[PlantScatter] Removed {removed} plant(s).");
            }
        }

        /// <summary>
        /// Without this every plant is its own draw call. With it, all copies of one plant type draw
        /// together - the difference between ~4,900 batches and about a dozen.
        /// </summary>
        static void EnableInstancing(IEnumerable<GameObject> prototypeObjects)
        {
            var done = new HashSet<Material>();
            foreach (var prototype in prototypeObjects)
            foreach (var renderer in prototype.GetComponentsInChildren<MeshRenderer>(true))
            foreach (var material in renderer.sharedMaterials)
            {
                if (material == null || !done.Add(material) || material.enableInstancing) continue;
                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
            }
        }

        static string Report(Dictionary<string, GameObject> library, Dictionary<string, int> placed,
                             int skipped, HashSet<string> missing)
        {
            var report = new StringBuilder();
            long triangles = 0;

            foreach (var pair in placed.OrderByDescending(p => p.Value))
            {
                int each = TriangleCount(library[pair.Key]);
                triangles += (long)each * pair.Value;
                report.AppendLine($"  {pair.Key,-16} {pair.Value,5} x {each,6} tris");
            }
            if (skipped > 0) report.AppendLine($"  {skipped} marker(s) skipped by the density setting.");
            if (missing.Count > 0)
                report.AppendLine($"  NO PROTOTYPE for: {string.Join(", ", missing.OrderBy(m => m))} " +
                                  "- re-run maya_headless_export.py --mode plants.");

            return $"[PlantScatter] Placed {placed.Values.Sum()} plants, " +
                   $"{triangles / 1000:N0}k triangles total.\n{report}";
        }

        static int TriangleCount(GameObject prototype)
        {
            int total = 0;
            foreach (var filter in prototype.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) total += filter.sharedMesh.triangles.Length / 3;
            return total;
        }
    }
}
