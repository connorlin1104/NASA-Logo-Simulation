using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Replaces any PH_* placeholder visual with an imported FBX, preserving all wiring: because every
    /// builder tool keeps gameplay components on the PARENT root and the placeholder is a pure visual
    /// child (<see cref="PlaceholderMarker"/>), the swap just deletes the child and drops the model in —
    /// bounds-matched to the placeholder's size — with zero re-wiring.
    ///
    /// Usage: select the PH_* object in the HIERARCHY and the FBX in the PROJECT window (both at once),
    /// then run the menu. "List All Placeholders" prints the remaining swap checklist.
    /// </summary>
    public static class PlaceholderSwapTool
    {
        [MenuItem("Tools/NASA Sim/Models/Swap Placeholder With Selected FBX")]
        public static void Swap()
        {
            PlaceholderMarker ph = null;
            GameObject model = null;

            foreach (var o in Selection.objects)
            {
                var go = o as GameObject;
                if (go == null) continue;
                if (go.scene.IsValid())
                {
                    var m = go.GetComponent<PlaceholderMarker>();
                    if (m == null) m = go.GetComponentInChildren<PlaceholderMarker>();
                    if (m != null) ph = m;
                }
                else if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(go)))
                {
                    model = go;
                }
            }

            if (ph == null || model == null)
            {
                EditorUtility.DisplayDialog("Select both sides of the swap",
                    "Select the PH_* placeholder in the HIERARCHY and the imported FBX in the PROJECT " +
                    "window at the same time (Cmd-click), then run this again.\n\n" +
                    "Tools > NASA Sim > Models > List All Placeholders shows what's left to swap.", "OK");
                return;
            }

            Transform root = ph.transform.parent != null ? ph.transform.parent : ph.transform;
            Undo.RegisterFullObjectHierarchyUndo(root.gameObject, "Swap Placeholder");

            Bounds phBounds = RendererBounds(ph.gameObject, out bool hasPhBounds);
            Vector3 phLocalPos = ph.transform.localPosition;
            Quaternion phLocalRot = ph.transform.localRotation;
            PlaceholderMarker.Category category = ph.category;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely,
                                               InteractionMode.AutomatedAction);
            instance.name = root.name + "_Model";
            instance.transform.SetParent(root, worldPositionStays: false);
            instance.transform.localPosition = phLocalPos;
            instance.transform.localRotation = phLocalRot;
            instance.transform.localScale = Vector3.one;

            // ---- Bounds-match the model to the placeholder ----
            if (hasPhBounds && TryBounds(instance, out Bounds mb))
            {
                bool matchHeight = category is PlaceholderMarker.Category.Duck
                                            or PlaceholderMarker.Category.Fish
                                            or PlaceholderMarker.Category.FruitTree
                                            or PlaceholderMarker.Category.DoorOuter
                                            or PlaceholderMarker.Category.DoorInner;
                float phDim = matchHeight ? phBounds.size.y : Mathf.Max(phBounds.size.x, phBounds.size.z);
                float mDim = matchHeight ? mb.size.y : Mathf.Max(mb.size.x, mb.size.z);
                if (phDim > 1e-4f && mDim > 1e-4f)
                    instance.transform.localScale = Vector3.one * (phDim / mDim);

                // Align: same footprint centre, same floor line as the placeholder occupied.
                if (TryBounds(instance, out Bounds sb))
                {
                    Vector3 offset = phBounds.center - sb.center;
                    offset.y = phBounds.min.y - sb.min.y;
                    instance.transform.position += offset;
                }
            }

            // ---- Collider policy by category ----
            switch (category)
            {
                case PlaceholderMarker.Category.Duck:
                case PlaceholderMarker.Category.Fish:
                    // The root's own trigger is all these need; model geometry must not fight the
                    // wander/interaction colliders.
                    foreach (var c in instance.GetComponentsInChildren<Collider>(true))
                        Object.DestroyImmediate(c);
                    break;

                case PlaceholderMarker.Category.FruitTree:
                {
                    // Strip the model's own colliders, but keep the trunk solid — the placeholder's
                    // trunk blocked walking, and a swapped tree must too.
                    foreach (var c in instance.GetComponentsInChildren<Collider>(true))
                        Object.DestroyImmediate(c);
                    if (TryBounds(instance, out Bounds treeB))
                    {
                        var cap = instance.AddComponent<CapsuleCollider>();
                        cap.direction = 1;                                 // Y axis
                        Vector3 ls = instance.transform.lossyScale;
                        cap.center = instance.transform.InverseTransformPoint(
                            new Vector3(treeB.center.x, treeB.min.y + treeB.size.y * 0.5f, treeB.center.z));
                        cap.height = treeB.size.y / Mathf.Max(1e-4f, Mathf.Abs(ls.y));
                        cap.radius = Mathf.Max(treeB.size.x, treeB.size.z) * 0.12f
                                     / Mathf.Max(1e-4f, Mathf.Abs(ls.x));
                    }
                    break;
                }

                case PlaceholderMarker.Category.DoorOuter:
                case PlaceholderMarker.Category.DoorInner:
                {
                    // The panel must physically block: one box matched to the model's bounds.
                    foreach (var c in instance.GetComponentsInChildren<Collider>(true))
                        Object.DestroyImmediate(c);
                    if (TryBounds(instance, out Bounds db))
                    {
                        var box = instance.AddComponent<BoxCollider>();
                        box.center = instance.transform.InverseTransformPoint(db.center);
                        Vector3 ls = instance.transform.lossyScale;
                        box.size = new Vector3(db.size.x / Mathf.Max(1e-4f, Mathf.Abs(ls.x)),
                                               db.size.y / Mathf.Max(1e-4f, Mathf.Abs(ls.y)),
                                               db.size.z / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
                    }
                    break;
                }

                default:
                    // Generic/Pond/SpiralStair/Balcony: keep whatever colliders the model brought (or
                    // run the COL_ wire-up / spiral ramp tools for walkables).
                    break;
            }

            Object.DestroyImmediate(ph.gameObject);
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            Selection.activeGameObject = root.gameObject;

            var kept = new StringBuilder();
            foreach (var comp in root.GetComponents<Component>())
                if (comp is not Transform) kept.Append(comp.GetType().Name).Append(' ');
            Debug.Log($"[PlaceholderSwap] '{root.name}': placeholder replaced by '{model.name}', " +
                      $"bounds-matched. Untouched components on the root: {kept}", root.gameObject);
        }

        [MenuItem("Tools/NASA Sim/Models/List All Placeholders")]
        public static void ListAll()
        {
            var markers = Object.FindObjectsByType<PlaceholderMarker>(FindObjectsInactive.Include,
                                                                      FindObjectsSortMode.None);
            if (markers.Length == 0)
            {
                Debug.Log("[PlaceholderSwap] No placeholders left — everything has a real model.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[PlaceholderSwap] {markers.Length} placeholder(s) awaiting models:");
            foreach (var m in markers)
                sb.AppendLine($"  [{m.category}] {ScenePath(m.transform)}" +
                              (string.IsNullOrEmpty(m.note) ? "" : $" — {m.note}"));
            Debug.Log(sb.ToString());
        }

        static string ScenePath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            while (t.parent != null)
            {
                t = t.parent;
                sb.Insert(0, t.name + "/");
            }
            return sb.ToString();
        }

        static Bounds RendererBounds(GameObject go, out bool any)
        {
            any = TryBounds(go, out Bounds b);
            return b;
        }

        static bool TryBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }
    }
}
