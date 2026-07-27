using System;
using System.Collections.Generic;
using UnityEngine;

namespace NasaSim
{
    /// <summary>
    /// Remembers which material each produce renderer had before it was recoloured, so the whole thing
    /// is one button away from being undone even after the scene has been saved and reopened.
    ///
    /// One component holding parallel records rather than a marker on each of several thousand
    /// renderers: the crops are individually modelled, so the per-renderer version would add ~4,000
    /// components to the scene for pure bookkeeping.
    ///
    /// It stores references, not names — moving or renaming a plant afterwards cannot break the revert.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ProduceTintLog : MonoBehaviour
    {
        [Serializable]
        public sealed class Entry
        {
            public Renderer renderer;
            public Material[] originals;
        }

        [Tooltip("Read-only bookkeeping. Rebuilt every time the produce is recoloured.")]
        public List<Entry> entries = new List<Entry>();

        public int Count => entries.Count;

        public void Record(Renderer renderer, Material[] originals)
        {
            if (renderer == null || originals == null) return;
            entries.Add(new Entry { renderer = renderer, originals = (Material[])originals.Clone() });
        }

        /// <summary>Put every recorded renderer back on its imported material. Returns how many changed.</summary>
        public int Revert()
        {
            int n = 0;
            foreach (Entry e in entries)
            {
                if (e == null || e.renderer == null || e.originals == null) continue;
                e.renderer.sharedMaterials = e.originals;
                n++;
            }
            entries.Clear();
            return n;
        }
    }
}
