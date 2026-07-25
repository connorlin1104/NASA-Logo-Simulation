using System.Collections.Generic;
using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Grows a pickable fruit on every child named <c>FRUIT_*</c> (empty markers on a tree root, placed
    /// by the fruit-tree tool or authored on an imported tree FBX). Eaten fruit regrow after
    /// <see cref="regrowSeconds"/> with a little scale-up ease.
    ///
    /// UNSCALED time: regrowth is player-facing pacing, not part of the fast-forwarded mow.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FruitSpawner : MonoBehaviour
    {
        [Min(1f)] public float regrowSeconds = 30f;
        [Min(0.05f)] public float fruitDiameter = 0.18f;
        [Min(0.1f)] public float growInSeconds = 1.2f;
        [Tooltip("Assigned by the fruit-tree tool; falls back to a plain red material.")]
        public Material fruitMaterial;

        readonly List<FruitInteractable> _fruits = new List<FruitInteractable>();
        readonly List<float> _regrowTimers = new List<float>();   // parallel to _fruits; <= 0 = grown
        readonly List<float> _growIn = new List<float>();         // 0..1 scale-in tween

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

            var fruit = go.AddComponent<FruitInteractable>();
            fruit.spawner = this;

            var vis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            vis.name = "PH_Fruit";
            Destroy(vis.GetComponent<Collider>());
            vis.transform.SetParent(go.transform, worldPositionStays: false);
            if (fruitMaterial != null)
                vis.GetComponent<Renderer>().sharedMaterial = fruitMaterial;

            _fruits.Add(fruit);
            _regrowTimers.Add(-1f);
            _growIn.Add(1f);
        }

        /// <summary>Called by <see cref="FruitEatController"/> when the last bite is taken.</summary>
        public void NotifyEaten(FruitInteractable fruit)
        {
            int i = _fruits.IndexOf(fruit);
            if (i < 0) return;
            fruit.gameObject.SetActive(false);
            _regrowTimers[i] = regrowSeconds;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            for (int i = 0; i < _fruits.Count; i++)
            {
                if (_fruits[i] == null) continue;

                if (_regrowTimers[i] > 0f)
                {
                    _regrowTimers[i] -= dt;
                    if (_regrowTimers[i] <= 0f)
                    {
                        _fruits[i].gameObject.SetActive(true);
                        _growIn[i] = 0f;
                    }
                }

                if (_growIn[i] < 1f)
                {
                    _growIn[i] = Mathf.Min(1f, _growIn[i] + dt / growInSeconds);
                    float k = _growIn[i];
                    float s = k < 0.8f ? Mathf.Lerp(0.1f, 1.1f, k / 0.8f)
                                       : Mathf.Lerp(1.1f, 1f, (k - 0.8f) / 0.2f);
                    _fruits[i].transform.localScale = Vector3.one * (fruitDiameter * s);
                }
            }
        }
    }
}
