using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Turns any object you select into something the astronaut can eat or pet — the two menu items are
    /// the whole workflow. Each adds the matching component (<see cref="EatableObject"/> /
    /// <see cref="PettableObject"/>) and an <c>InteractTrigger</c> child carrying the sphere trigger on
    /// the Interactable layer, sized from the object's own renderers.
    ///
    /// The trigger lives on a CHILD on purpose: the object keeps its own layer, colliders and physics, so
    /// this is safe to run on scenery that is already doing another job. Re-running updates in place, and
    /// everything is undoable.
    /// </summary>
    public static class MakeInteractableTool
    {
        const string TriggerName = "InteractTrigger";

        /// <summary>Slack added to the object's radius so you can trigger it from arm's length.</summary>
        const float TriggerMargin = 0.3f;

        [MenuItem("Tools/NASA Sim/Interactables/Make Selected Eatable")]
        public static void MakeEatable() => Apply(eatable: true);

        [MenuItem("Tools/NASA Sim/Interactables/Make Selected Pettable")]
        public static void MakePettable() => Apply(eatable: false);

        [MenuItem("Tools/NASA Sim/Interactables/Make Selected Eatable", validate = true)]
        [MenuItem("Tools/NASA Sim/Interactables/Make Selected Pettable", validate = true)]
        static bool HasSceneSelection()
        {
            foreach (var go in Selection.gameObjects)
                if (go != null && !EditorUtility.IsPersistent(go)) return true;
            return false;
        }

        static void Apply(bool eatable)
        {
            var targets = Selection.gameObjects;
            int done = 0;
            GameObject last = null;

            foreach (var go in targets)
            {
                if (go == null || EditorUtility.IsPersistent(go))
                {
                    if (go != null)
                        Debug.LogWarning($"[Interactables] '{go.name}' is a project asset — drag it into " +
                                         "the scene first, then run this on the scene object.", go);
                    continue;
                }

                if (eatable)
                {
                    var eat = GetOrAdd<EatableObject>(go);
                    Undo.RecordObject(eat, "Make Eatable");
                    // Leave bites / respawn at their defaults — those are the per-object knobs.
                    EditorUtility.SetDirty(eat);
                }
                else
                {
                    var pet = GetOrAdd<PettableObject>(go);
                    Undo.RecordObject(pet, "Make Pettable");
                    if (pet.wanderer == null) pet.wanderer = go.GetComponent<WaterWanderer>();
                    EditorUtility.SetDirty(pet);
                }

                BuildTrigger(go);
                done++;
                last = go;
            }

            if (done == 0)
            {
                Debug.LogWarning("[Interactables] Nothing to do — select one or more objects in the scene.");
                return;
            }

            if (Object.FindAnyObjectByType<HandActionController>() == null)
                Debug.LogWarning("[Interactables] No astronaut hand in the scene — run " +
                                 "Tools > NASA Sim > Setup > Add Interaction System & UI so the eat/pet " +
                                 "flourishes have something to play on.");

            EditorSceneManager.MarkSceneDirty(last.scene);
            Debug.Log($"[Interactables] {done} object(s) are now {(eatable ? "eatable" : "pettable")}. " +
                      $"Walk up and press E. Tune the per-object settings on the " +
                      $"{(eatable ? "Eatable Object" : "Pettable Object")} component.", last);
        }

        /// <summary>
        /// Find-or-create the trigger child and size it to the object. Its localScale cancels the parent's
        /// so the radius below is honest world metres whatever the object was imported at.
        /// </summary>
        static void BuildTrigger(GameObject go)
        {
            Transform existing = go.transform.Find(TriggerName);
            GameObject trigger;
            if (existing != null)
            {
                trigger = existing.gameObject;
                Undo.RecordObject(trigger.transform, "Make Interactable");
            }
            else
            {
                trigger = new GameObject(TriggerName);
                Undo.RegisterCreatedObjectUndo(trigger, "Make Interactable");
                Undo.SetTransformParent(trigger.transform, go.transform, "Make Interactable");
            }

            trigger.layer = NasaLayers.Interactable;
            trigger.transform.localRotation = Quaternion.identity;

            Vector3 ls = go.transform.lossyScale;
            trigger.transform.localScale = new Vector3(1f / NonZero(ls.x), 1f / NonZero(ls.y), 1f / NonZero(ls.z));

            float radius = 0.5f;
            if (TryGetVisualBounds(go, out Bounds b))
            {
                trigger.transform.position = b.center;
                radius = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z)) + TriggerMargin;
            }
            else
            {
                trigger.transform.localPosition = Vector3.zero;
            }

            var col = GetOrAdd<SphereCollider>(trigger);
            Undo.RecordObject(col, "Make Interactable");
            col.isTrigger = true;
            col.center = Vector3.zero;
            col.radius = Mathf.Max(0.2f, radius);
            EditorUtility.SetDirty(col);
        }

        /// <summary>World bounds of everything the object renders, ignoring the trigger child itself.</summary>
        static bool TryGetVisualBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        static float NonZero(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : Undo.AddComponent<T>(go);
        }
    }
}
