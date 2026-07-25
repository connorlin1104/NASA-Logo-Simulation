using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Kills the "my textures never survive a re-export" pain. Every model under
    /// <c>Assets/_Project/Models</c> gets its material slots auto-remapped BY NAME to project materials
    /// (<c>Assets/_Project/Materials/&lt;Name&gt;.mat</c> or anywhere else in the project) on every
    /// import. Hand-tune a material once in Unity, keep the same material name in Maya, and every future
    /// FBX re-export keeps pointing at your tuned material instead of shipping a fresh embedded copy.
    ///
    /// Workflow (see IMPORT_GUIDE.md):
    ///   1. In Maya, name materials with their FINAL Unity name (BiodomeGlass, DuckBody, ...). Avoid
    ///      default names — a material literally called "New Material" or "lambert1" will remap to
    ///      whatever project material shares that name.
    ///   2. First import: author/extract the project .mat once (same name).
    ///   3. Every later re-import: this postprocessor re-binds the slot automatically.
    /// </summary>
    public sealed class NasaModelPostprocessor : AssetPostprocessor
    {
        const string ModelsRoot = "Assets/_Project/Models";

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(ModelsRoot)) return;
            var importer = (ModelImporter)assetImporter;

            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;   // embedded, as today
            importer.importCameras = false;
            importer.importLights = false;

            // THE FIX: fill the externalObjects remap table by material NAME, searching the whole
            // project. This only ADDS matches — existing manual remaps are preserved — and once a slot
            // is remapped, every future re-import keeps pointing at the project .mat you hand-tuned.
            importer.SearchAndRemapMaterials(ModelImporterMaterialName.BasedOnMaterialName,
                                             ModelImporterMaterialSearch.Everywhere);
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!assetPath.StartsWith(ModelsRoot)) return;
            var importer = assetImporter as ModelImporter;
            if (importer == null) return;

            // Which authored material names got a project remap, and which are still embedded-only.
            var mapped = new HashSet<string>();
            foreach (var kv in importer.GetExternalObjectMap())
                if (kv.Key.type == typeof(Material) && kv.Value != null)
                    mapped.Add(kv.Key.name);

            var unmapped = new SortedSet<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && !mapped.Contains(m.name))
                        unmapped.Add(m.name);

            if (unmapped.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine($"[ModelPostprocessor] '{System.IO.Path.GetFileName(assetPath)}': " +
                          $"{mapped.Count} material slot(s) remapped to project materials, " +
                          $"{unmapped.Count} still using embedded copies:");
            foreach (string name in unmapped)
            {
                int exactMatches = CountExactMaterialAssets(name);
                sb.AppendLine(exactMatches > 1
                    ? $"  '{name}' — AMBIGUOUS: {exactMatches} project materials share this name; " +
                      "rename so exactly one matches, then Reimport."
                    : $"  '{name}' — create Assets/_Project/Materials/{name}.mat (same name) and " +
                      "Reimport to bind it permanently.");
            }
            Debug.Log(sb.ToString(), root);
        }

        static int CountExactMaterialAssets(string name)
        {
            int count = 0;
            foreach (string guid in AssetDatabase.FindAssets($"t:Material {name}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == name) count++;
            }
            return count;
        }

        /// <summary>
        /// Re-run the name remap on every model at once — use after authoring new project materials, or
        /// once after adding this postprocessor to an existing project. Deliberate action (it can change
        /// what existing scenes render), so it lives behind a menu instead of running on script reload.
        /// </summary>
        [MenuItem("Tools/NASA Sim/Models/Reimport && Remap All Models")]
        public static void ReimportAndRemapAll()
        {
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { ModelsRoot });
            int done = 0;
            try
            {
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (EditorUtility.DisplayCancelableProgressBar("Reimport & Remap Models", path,
                                                                   i / (float)Mathf.Max(1, guids.Length)))
                        break;
                    if (AssetImporter.GetAtPath(path) is ModelImporter importer)
                    {
                        importer.SearchAndRemapMaterials(ModelImporterMaterialName.BasedOnMaterialName,
                                                         ModelImporterMaterialSearch.Everywhere);
                        importer.SaveAndReimport();
                        done++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Debug.Log($"[ModelPostprocessor] Reimported & remapped {done}/{guids.Length} model(s) under {ModelsRoot}. " +
                      "Eyeball the scene once — slots whose Maya name matches a project material now use it.");
        }
    }
}
