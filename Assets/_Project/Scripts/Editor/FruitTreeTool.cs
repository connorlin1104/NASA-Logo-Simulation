using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Places placeholder fruit trees around the moat's outer bank. Each tree root carries a
    /// <see cref="FruitSpawner"/> and three <c>FRUIT_</c> markers (fruit themselves are grown at
    /// runtime); the visible trunk/canopy is a PH_ placeholder swappable for a modeled tree. A tree FBX
    /// exported with its own FRUIT_ empties works the same way with no tool at all — just add
    /// FruitSpawner to its root.
    /// </summary>
    public static class FruitTreeTool
    {
        [MenuItem("Tools/NASA Sim/Interactables/Add Fruit Trees At Moat")]
        public static void AddDefaultTrees() => AddTrees(4);

        public static void AddTrees(int count)
        {
            Vector3 center = Vector3.zero;
            float radius = 24f;
            var moatGo = GameObject.Find("WATER_Moat");
            var moat = moatGo != null ? moatGo.GetComponent<WaterBody>() : null;
            if (moat != null)
            {
                center = moat.transform.position;
                radius = moat.outerRadius + 1.8f;      // just beyond the outer bank
            }
            else
            {
                Debug.LogWarning("[FruitTrees] No WATER_Moat found — placing trees on a default 24 m ring. " +
                                 "Run Tools > NASA Sim > Water > Create Water Body first for bank-side trees.");
            }

            Material trunkMat = SceneBootstrap.MakeMat("Assets/_Project/Materials/TreeTrunk.mat",
                "Universal Render Pipeline/Lit", new Color(0.35f, 0.23f, 0.12f));
            Material canopyMat = SceneBootstrap.MakeMat("Assets/_Project/Materials/TreeCanopy.mat",
                "Universal Render Pipeline/Lit", new Color(0.13f, 0.35f, 0.13f));
            Material fruitMat = SceneBootstrap.MakeMat("Assets/_Project/Materials/Fruit.mat",
                "Universal Render Pipeline/Lit", new Color(0.85f, 0.20f, 0.10f));

            var group = GameObject.Find("FruitTrees");
            if (group == null)
            {
                group = new GameObject("FruitTrees");
                Undo.RegisterCreatedObjectUndo(group, "Add Fruit Trees");
            }

            int created = 0;
            for (int i = 0; i < count; i++)
            {
                string name = $"FruitTree_{i + 1:00}";
                if (group.transform.Find(name) != null) continue;   // idempotent

                float angle = (i / (float)count) * Mathf.PI * 2f + 0.4f;
                Vector3 pos = center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                float y = 0f;
                if (Physics.Raycast(pos + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 40f,
                                    ~0, QueryTriggerInteraction.Ignore))
                    y = hit.point.y;
                pos.y = y;

                var root = new GameObject(name);
                root.transform.SetParent(group.transform, worldPositionStays: false);
                root.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, Random.value * 360f, 0f));

                var spawner = root.AddComponent<FruitSpawner>();
                spawner.fruitMaterial = fruitMat;

                // Placeholder visual (components stay on the root — swap-safe).
                var ph = new GameObject("PH_FruitTree");
                ph.transform.SetParent(root.transform, worldPositionStays: false);
                var marker = ph.AddComponent<PlaceholderMarker>();
                marker.category = PlaceholderMarker.Category.FruitTree;
                marker.note = "FRUIT_ markers live on the tree ROOT, so a swapped-in model keeps them.";

                // Trunk keeps its collider (blocks walking); the canopy doesn't need one.
                var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                trunk.name = "Trunk";
                trunk.transform.SetParent(ph.transform, worldPositionStays: false);
                trunk.transform.localPosition = new Vector3(0f, 1.1f, 0f);
                trunk.transform.localScale = new Vector3(0.28f, 1.1f, 0.28f);
                SceneBootstrap.SetMaterial(trunk, trunkMat);

                var canopy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                canopy.name = "Canopy";
                canopy.transform.SetParent(ph.transform, worldPositionStays: false);
                canopy.transform.localPosition = new Vector3(0f, 2.5f, 0f);
                canopy.transform.localScale = new Vector3(1.9f, 1.5f, 1.9f);
                SceneBootstrap.SetMaterial(canopy, canopyMat);
                Object.DestroyImmediate(canopy.GetComponent<Collider>());

                // Fruit markers on the ROOT, at the canopy's fringe where a hand can reach.
                CreateMarker(root.transform, "FRUIT_1", new Vector3(0.65f, 2.05f, 0.35f));
                CreateMarker(root.transform, "FRUIT_2", new Vector3(-0.6f, 2.15f, 0.45f));
                CreateMarker(root.transform, "FRUIT_3", new Vector3(0.1f, 1.95f, -0.7f));
                created++;
            }

            EditorSceneManager.MarkSceneDirty(group.scene);
            Debug.Log($"[FruitTrees] {created} tree(s) added around the moat bank ({count - created} " +
                      "already existed). Fruit grow at the FRUIT_ markers when you press Play.", group);
        }

        static void CreateMarker(Transform root, string name, Vector3 localPos)
        {
            var m = new GameObject(name);
            m.transform.SetParent(root, worldPositionStays: false);
            m.transform.localPosition = localPos;
        }
    }
}
