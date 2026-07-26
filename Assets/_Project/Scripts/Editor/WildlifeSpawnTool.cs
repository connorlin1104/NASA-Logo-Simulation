using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Populates a <see cref="WaterBody"/> with ducks and fish. "Easily added per pond": select a pond,
    /// open the tool, set counts, Spawn — re-running ADJUSTS the population (adds or removes) instead of
    /// duplicating. Ducks are pattable (Interactable trigger + <see cref="PettableObject"/>); fish
    /// just swim. All visuals are PH_ placeholder primitives with the scripts on the roots, so modeled
    /// FBX ducks/fish swap in later with no re-wiring.
    /// </summary>
    public sealed class WildlifeSpawnTool : EditorWindow
    {
        WaterBody target;
        int duckCount = 3;
        int fishCount = 8;

        [MenuItem("Tools/NASA Sim/Water/Spawn Ducks && Fish")]
        public static void Open()
        {
            var w = GetWindow<WildlifeSpawnTool>(true, "Spawn Ducks & Fish", true);
            w.minSize = new Vector2(380f, 220f);
            w.AutoTarget();
        }

        void AutoTarget()
        {
            if (Selection.activeGameObject != null)
                target = Selection.activeGameObject.GetComponentInParent<WaterBody>();
            if (target == null) target = FindAnyObjectByType<WaterBody>();
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Adds autonomous ducks (pattable) and fish to a water body. Re-run to adjust the counts " +
                "— existing animals are kept, extras removed, missing ones added.", MessageType.Info);

            target = (WaterBody)EditorGUILayout.ObjectField("Water body", target, typeof(WaterBody), true);
            duckCount = EditorGUILayout.IntSlider("Ducks", duckCount, 0, 20);
            fishCount = EditorGUILayout.IntSlider("Fish", fishCount, 0, 40);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(target == null))
                if (GUILayout.Button("Spawn / Adjust", GUILayout.Height(30f)))
                    Spawn(target, duckCount, fishCount);
        }

        /// <summary>Programmatic default for the full-vision setup chain: the moat gets 3 ducks + 8 fish.</summary>
        public static void SpawnDefaults()
        {
            var moat = GameObject.Find("WATER_Moat");
            WaterBody body = moat != null ? moat.GetComponent<WaterBody>() : FindAnyObjectByType<WaterBody>();
            if (body == null)
            {
                Debug.LogWarning("[Wildlife] No WaterBody in the scene — run " +
                                 "Tools > NASA Sim > Water > Add Square Moat Around Grass first.");
                return;
            }
            Spawn(body, 3, 8);
        }

        public static void Spawn(WaterBody water, int ducks, int fish, bool quiet = false)
        {
            AdjustPopulation(water, "Duck", ducks, isDuck: true);
            AdjustPopulation(water, "Fish", fish, isDuck: false);
            water.SnapWildlifeInside();          // resized pond? everyone goes back in the water
            EditorSceneManager.MarkSceneDirty(water.gameObject.scene);
            if (!quiet)
                Debug.Log($"[Wildlife] '{water.name}' population: {ducks} duck(s), {fish} fish.", water);
        }

        static void AdjustPopulation(WaterBody water, string prefix, int wanted, bool isDuck)
        {
            var existing = new List<Transform>();
            foreach (Transform c in water.transform)
                if (c.name.StartsWith(prefix + "_")) existing.Add(c);

            for (int i = existing.Count - 1; i >= wanted; i--)
                Object.DestroyImmediate(existing[i].gameObject);

            for (int i = existing.Count; i < wanted; i++)
            {
                if (isDuck) BuildDuck(water, i + 1);
                else BuildFish(water, i + 1);
            }
        }

        static void BuildDuck(WaterBody water, int index)
        {
            var root = new GameObject($"Duck_{index:00}");
            root.transform.SetParent(water.transform, worldPositionStays: false);
            root.transform.position = water.RandomPointOnSurface(1f);
            root.transform.rotation = Quaternion.Euler(0f, Random.value * 360f, 0f);
            root.layer = NasaLayers.Interactable;

            var trigger = root.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 0.6f;
            trigger.center = Vector3.up * 0.15f;

            var wander = root.AddComponent<WaterWanderer>();
            wander.mode = WaterWanderer.Mode.Duck;
            wander.water = water;

            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.spatialBlend = 1f;
            wander.audioSource = audio;

            var interactable = root.AddComponent<PettableObject>();
            interactable.wanderer = wander;
            interactable.label = "duck";
            interactable.audioSource = audio;

            Material bodyMat = SceneBootstrap.MakeMat("Assets/_Project/Materials/DuckBody.mat",
                "Universal Render Pipeline/Lit", new Color(0.95f, 0.92f, 0.78f));
            Material beakMat = SceneBootstrap.MakeMat("Assets/_Project/Materials/DuckBeak.mat",
                "Universal Render Pipeline/Lit", new Color(0.95f, 0.55f, 0.12f));

            var ph = new GameObject("PH_Duck");
            ph.transform.SetParent(root.transform, worldPositionStays: false);
            var marker = ph.AddComponent<PlaceholderMarker>();
            marker.category = PlaceholderMarker.Category.Duck;
            marker.note = "Swap with a modeled duck FBX; the wander/pat scripts live on the parent root.";

            Prim(PrimitiveType.Sphere, "Body", ph.transform, bodyMat,
                 new Vector3(0f, 0.12f, 0f), new Vector3(0.45f, 0.32f, 0.55f));
            Prim(PrimitiveType.Sphere, "Head", ph.transform, bodyMat,
                 new Vector3(0f, 0.34f, 0.22f), new Vector3(0.2f, 0.2f, 0.2f));
            Prim(PrimitiveType.Cube, "Beak", ph.transform, beakMat,
                 new Vector3(0f, 0.33f, 0.36f), new Vector3(0.08f, 0.04f, 0.12f));
        }

        static void BuildFish(WaterBody water, int index)
        {
            var root = new GameObject($"Fish_{index:00}");
            root.transform.SetParent(water.transform, worldPositionStays: false);
            Vector3 p = water.RandomPointOnSurface(1f);
            p.y = water.SurfaceY - 0.4f;
            root.transform.position = p;
            root.transform.rotation = Quaternion.Euler(0f, Random.value * 360f, 0f);

            var wander = root.AddComponent<WaterWanderer>();
            wander.mode = WaterWanderer.Mode.Fish;
            wander.water = water;
            wander.speed = 0.9f;
            wander.turnRateDeg = 160f;
            wander.idlePauseRange = new Vector2(0.3f, 1.2f);

            Material fishMat = SceneBootstrap.MakeMat("Assets/_Project/Materials/FishBody.mat",
                "Universal Render Pipeline/Lit", new Color(0.9f, 0.42f, 0.15f));

            var ph = new GameObject("PH_Fish");
            ph.transform.SetParent(root.transform, worldPositionStays: false);
            var marker = ph.AddComponent<PlaceholderMarker>();
            marker.category = PlaceholderMarker.Category.Fish;

            // Capsule's long axis is Y; pitch it 90° so the fish lies along its swim direction (+Z).
            var bodyGo = Prim(PrimitiveType.Capsule, "Body", ph.transform, fishMat,
                              Vector3.zero, new Vector3(0.12f, 0.16f, 0.12f));
            bodyGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Prim(PrimitiveType.Cube, "Tail", ph.transform, fishMat,
                 new Vector3(0f, 0f, -0.2f), new Vector3(0.02f, 0.12f, 0.1f));
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Material mat,
                               Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;
            SceneBootstrap.SetMaterial(go, mat);
            var col = go.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);   // the root's trigger is the only collider
            return go;
        }
    }
}
