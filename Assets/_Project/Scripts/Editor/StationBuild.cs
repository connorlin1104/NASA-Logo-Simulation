using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Measuring and wiring helpers shared by the four station tools (colliders, chairs, elevator,
    /// lighting). They all do the same three things to an imported Maya group — measure it, find or
    /// create a child on it, and park generated objects under a named scene root — so those live here
    /// once instead of four times.
    /// </summary>
    internal static class StationBuild
    {
        public const string DataDir = "Assets/_Project/Data";

        // ------------------------------------------------------------------ measuring

        /// <summary>World-space bounds of everything the object renders. Particles are ignored (their
        /// bounds swell with live particles and would blow up every measurement that uses this).</summary>
        public static bool TryRendererBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            if (go == null) return false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        /// <summary>
        /// Bounds expressed in <paramref name="space"/>'s LOCAL coordinates — what a BoxCollider needs.
        /// Built from each mesh's own local bounds pushed through the mesh→space matrix, so a group whose
        /// parts are rotated 30° gets a snug box instead of the fat axis-aligned hull that transforming
        /// world bounds would give.
        /// </summary>
        public static bool TryLocalBounds(GameObject go, Transform space, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            if (go == null || space == null) return false;
            Matrix4x4 toSpace = space.worldToLocalMatrix;

            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Encapsulate(ref bounds, ref any, toSpace * mf.transform.localToWorldMatrix,
                            mf.sharedMesh.bounds);
            }
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                Encapsulate(ref bounds, ref any, toSpace * smr.transform.localToWorldMatrix,
                            smr.sharedMesh.bounds);
            }

            // Nothing with a mesh (a group of empties, or a renderer type we don't know): fall back to
            // the world bounds' eight corners, which is correct if coarse.
            if (!any && TryRendererBounds(go, out Bounds world))
                Encapsulate(ref bounds, ref any, toSpace, world);

            return any;
        }

        static void Encapsulate(ref Bounds bounds, ref bool any, Matrix4x4 matrix, Bounds local)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = matrix.MultiplyPoint3x4(corner);
                if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                else bounds.Encapsulate(p);
            }
        }

        // ------------------------------------------------------------------ scene plumbing

        /// <summary>
        /// Find-or-create a top-level holder for generated content. Generated objects always live at the
        /// scene root under a known name so re-running a tool can clear its own output without touching
        /// anything you made by hand.
        /// </summary>
        public static GameObject GeneratedRoot(string name, bool clearChildren)
        {
            var root = GameObject.Find(name);
            if (root == null)
            {
                root = new GameObject(name);
                Undo.RegisterCreatedObjectUndo(root, "Create " + name);
            }
            else if (clearChildren)
            {
                for (int i = root.transform.childCount - 1; i >= 0; i--)
                    Undo.DestroyObjectImmediate(root.transform.GetChild(i).gameObject);
            }
            root.transform.SetParent(null, worldPositionStays: true);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;
            return root;
        }

        public static GameObject FindOrCreateChild(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null) return existing.gameObject;
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Undo.SetTransformParent(go.transform, parent, "Create " + name);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        public static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : Undo.AddComponent<T>(go);
        }

        // ------------------------------------------------------------------ searching

        /// <summary>
        /// Every transform in the open scene whose name contains one of the words (case-insensitive).
        /// The imported groups carry Maya namespaces ("newGreenHouse_2:ElevatorDoor"), so a contains
        /// match is the only search that finds them without you typing the prefix.
        /// </summary>
        public static List<Transform> FindAllContaining(params string[] words)
        {
            var found = new List<Transform>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                string n = t.name.ToLowerInvariant();
                foreach (string w in words)
                {
                    if (string.IsNullOrEmpty(w) || !n.Contains(w.ToLowerInvariant())) continue;
                    found.Add(t);
                    break;
                }
            }
            return found;
        }

        public static Transform FindFirstContaining(params string[] words)
        {
            var all = FindAllContaining(words);
            return all.Count > 0 ? all[0] : null;
        }

        /// <summary>Hierarchy path, for log lines that have to distinguish three groups all called "Stairs".</summary>
        public static string PathOf(Transform t)
        {
            if (t == null) return "(none)";
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        public static string Sanitize(string s)
        {
            foreach (char bad in Path.GetInvalidFileNameChars()) s = s.Replace(bad, '_');
            return s;
        }

        /// <summary>Refuse to work on a Project-window asset — every tool here edits scene objects.</summary>
        public static bool RequireSceneObject(GameObject go, string what)
        {
            if (go == null) return false;
            if (go.scene.IsValid()) return true;
            EditorUtility.DisplayDialog("That is a Project asset",
                $"'{go.name}' is a file in the Project window, not an object in the scene. Drag it into " +
                $"the scene (or pick the copy that is already there in the Hierarchy) and assign that as " +
                $"the {what}.", "OK");
            return false;
        }
    }
}
