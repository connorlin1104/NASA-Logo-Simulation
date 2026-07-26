using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Grows a pickable fruit on every child named <c>FRUIT_*</c> (empty markers on a tree root, placed
    /// by the fruit-tree tool or authored on an imported tree FBX).
    ///
    /// The fruit are ordinary <see cref="EatableObject"/>s — the same component you can drop on anything
    /// else to make it edible — so eating, the stepped shrink and the regrow all live there; this just
    /// builds them and hands over the tree's settings.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FruitSpawner : MonoBehaviour
    {
        [Min(1f)] public float regrowSeconds = 30f;
        [Min(0.05f)] public float fruitDiameter = 0.18f;
        [Min(0.1f)] public float growInSeconds = 1.2f;
        [Min(1)] public int bitesPerFruit = 3;
        [Tooltip("Assigned by the fruit-tree tool; falls back to a plain red material.")]
        public Material fruitMaterial;

        void Awake()
        {
            foreach (Transform child in transform)
                if (child.name.StartsWith("FRUIT_"))
                    CreateFruit(child);
        }

        void CreateFruit(Transform marker)
        {
            var go = new GameObject("Fruit");
            go.transform.SetParent(marker, worldPositionStays: false);
            go.transform.localScale = Vector3.one * fruitDiameter;
            go.layer = NasaLayers.Interactable;

            var trigger = go.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            // Local units on the fruitDiameter-scaled root: 3 x 0.18 ≈ 0.55 m world, matching the
            // duck's pat range. A plain 0.6 here would be a ~0.11 m world trigger.
            trigger.radius = 3f;

            var vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = "PH_Fruit";
            Destroy(vis.GetComponent<Collider>());
            vis.transform.SetParent(go.transform, worldPositionStays: false);
            if (fruitMaterial != null)
                vis.GetComponent<Renderer>().sharedMaterial = fruitMaterial;

            var fruit = go.AddComponent<EatableObject>();
            fruit.verb = "Pick";
            fruit.label = "fruit";
            fruit.bites = bitesPerFruit;
            fruit.whenFinished = EatableObject.WhenFinished.Respawn;
            fruit.respawnSeconds = regrowSeconds;
            fruit.growInSeconds = growInSeconds;
            // The visual only exists now, so re-measure: this is what sizes the grip and the crumbs.
            fruit.CaptureRestPose();
        }
    }
}
