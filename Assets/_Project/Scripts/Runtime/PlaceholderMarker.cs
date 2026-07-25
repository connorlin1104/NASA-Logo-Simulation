using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Tags a PH_* placeholder visual for later replacement by a modeled FBX
    /// (Tools &gt; NASA Sim &gt; Models &gt; Swap Placeholder With Selected FBX).
    ///
    /// INVARIANT the whole placeholder system rests on: gameplay components (doors, wanderers,
    /// spawners, interactables) always live on the PARENT root; the placeholder is a pure visual child.
    /// That is what lets the swap tool delete the placeholder and drop a model in with zero re-wiring —
    /// the same pattern the astronaut and tractor swaps already use.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlaceholderMarker : MonoBehaviour
    {
        public enum Category
        {
            Generic,
            DoorOuter,
            DoorInner,
            Duck,
            Fish,
            FruitTree,
            Pond,
            SpiralStair,
            Balcony,
        }

        public Category category = Category.Generic;
        [TextArea] public string note;
    }
}
