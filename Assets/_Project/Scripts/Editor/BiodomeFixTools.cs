using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Repairs the imported Biodome instance.
    ///
    /// <b>Fix Dome Glass</b> — the dome shell arrives with an opaque, single-sided material (whether
    /// Unity's default grey "New Material" or the FBX's own). A closed hull rendered single-sided shows
    /// ONLY back faces to a camera inside it, so every triangle is culled and the dome vanishes from
    /// within — while reading as a solid white wall from outside. The fix is a purpose-built glass
    /// material: Surface Transparent + Render Face Both, low-alpha blue tint. See
    /// <see cref="DomeGlassTool"/> for the same thing with the tint and opacity exposed.
    ///
    /// <b>Wire Colliders &amp; Spawn Outside</b> — runs the COL_/NOCOL_/STAIR_ prefix wiring on the dome
    /// (it had no colliders at all) and drops a SPAWN_Outside marker at the far mouth of COL_tunnel so
    /// the run starts outside the biodome, facing the entrance.
    /// </summary>
    public static class BiodomeFixTools
    {
        const string GlassMatPath = "Assets/_Project/Materials/BiodomeGlass.mat";
        const string BadGreyMatPath = "Assets/_Project/Materials/New Material.mat";

        // Force the draw order among the transparents instead of trusting URP's distance sort with the
        // dome's huge bounds: water 3000 < gas 3050 < glass 3100 (the shell always draws last, which is
        // correct both inside looking out and outside looking in).
        public const int WaterQueue = 3000;
        public const int GasQueue = 3050;
        public const int GlassQueue = 3100;

        // ------------------------------------------------------------------ glass

        /// <summary>Default glass tint: a pale blue at 10% opacity, which reads as glass without hazing
        /// the view through it.</summary>
        public static readonly Color DefaultTint = new Color(0.62f, 0.78f, 0.92f, 0.10f);

        [MenuItem("Tools/NASA Sim/Biodome/Fix Dome Glass")]
        public static void FixDomeGlass() => ApplyGlass(DefaultTint, 0.92f, true);

        /// <summary>
        /// Turn every dome shell in the scene into glass. Returns how many material slots changed.
        ///
        /// This works RENDERER-FIRST rather than finding a model root and sweeping it. The dome now
        /// arrives as one mesh deep inside SettingEnvo, whose root also holds the plants, the tunnel, the
        /// terrain and eight thousand other objects — a sweep from there would repaint half the scene.
        /// </summary>
        public static int ApplyGlass(Color tint, float smoothness, bool doubleSided)
        {
            var shells = FindShellRenderers();
            if (shells.Count == 0)
            {
                Debug.LogWarning("[BiodomeFix] No dome shell found. Looked for a renderer whose name " +
                                 "(Maya namespace stripped) contains 'dome' or 'biosphere' — e.g. " +
                                 "'newGreenHouse_2:...:Biosphere2' or the older 'NOCOL_Dome'. Rename the " +
                                 "shell to include one of those words, or select it and use " +
                                 "Tools > NASA Sim > Biodome > Dome Glass.");
                return 0;
            }

            Material glass = CreateOrRefreshGlassMaterial(tint, smoothness, doubleSided);
            var badGrey = AssetDatabase.LoadAssetAtPath<Material>(BadGreyMatPath);
            int replacedSlots = 0;

            foreach (Renderer r in shells)
            {
                Undo.RecordObject(r, "Fix Dome Glass");
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != glass) { mats[i] = glass; replacedSlots++; }
                r.sharedMaterials = mats;
                // A glass dome must not plunge its own interior into shadow.
                r.shadowCastingMode = ShadowCastingMode.Off;
                EditorUtility.SetDirty(r);
            }

            // Unity's own default grey ("New Material") anywhere else is the same import accident and
            // reads as the same solid white wall, so it goes too — but only where it is that exact asset.
            if (badGrey != null)
            {
                foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
                {
                    if (r is ParticleSystemRenderer) continue;
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] == badGrey) { mats[i] = glass; replacedSlots++; changed = true; }
                    if (!changed) continue;
                    Undo.RecordObject(r, "Fix Dome Glass");
                    r.sharedMaterials = mats;
                    EditorUtility.SetDirty(r);
                }
            }

            EditorSceneManager.MarkSceneDirty(shells[0].gameObject.scene);
            Debug.Log($"[BiodomeFix] {shells.Count} dome shell renderer(s), {replacedSlots} material " +
                      $"slot(s) -> BiodomeGlass (transparent" +
                      (doubleSided ? ", double-sided" : "") + ", shadows off).\n  " +
                      "Shell: " + string.Join(", ", ShellNames(shells)) + "\n  " +
                      "It was opaque and single-sided, which is why it was a white wall from outside and " +
                      "nothing at all from inside — a closed hull rendered single-sided shows the camera " +
                      "inside it only back faces, and every one of those is culled.", shells[0]);
            return replacedSlots;
        }

        /// <summary>
        /// Every renderer that is part of a dome shell. Matched on the LEAF of the name with the Maya
        /// namespace stripped, because every airlock part in this scene carries "BiodomeAirlockDoor1" in
        /// its namespace and would otherwise match "dome" — turning the whole airlock into glass.
        /// </summary>
        public static List<Renderer> FindShellRenderers()
        {
            var found = new List<Renderer>();
            foreach (Renderer r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            {
                if (r is ParticleSystemRenderer) continue;
                if (IsShellName(r.name)) found.Add(r);
            }
            return found;
        }

        /// <summary>"newGreenHouse_2:_sh01_connor_anim_v002:Biosphere2" -> yes. "…:BiodomeAirlockDoor1:Leg" -> no.</summary>
        public static bool IsShellName(string rawName)
        {
            string leaf = Leaf(rawName).ToLowerInvariant();
            if (leaf.Contains("airlock") || leaf.Contains("door") || leaf.Contains("hatch")) return false;
            return leaf.Contains("biosphere") || leaf.Contains("dome");
        }

        /// <summary>Drop the Maya namespaces: everything up to and including the last colon.</summary>
        public static string Leaf(string name)
        {
            int c = name.LastIndexOf(':');
            return c >= 0 && c < name.Length - 1 ? name.Substring(c + 1) : name;
        }

        static IEnumerable<string> ShellNames(List<Renderer> shells)
        {
            var seen = new HashSet<string>();
            foreach (Renderer r in shells) if (seen.Add(Leaf(r.name))) yield return Leaf(r.name);
        }

        public static Material CreateOrRefreshGlassMaterial(Color tint, float smoothness, bool doubleSided)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(GlassMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "BiodomeGlass" };
                AssetDatabase.CreateAsset(mat, GlassMatPath);
            }

            // Re-applied on every run so the fix stays correct even if the asset was fiddled with.
            mat.SetFloat("_Surface", 1f);                                     // Transparent
            mat.SetFloat("_Blend", 0f);                                       // Alpha blend
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            // Render Face Both — THE fix for "I can't see it from inside".
            mat.SetFloat("_Cull", (float)(doubleSided ? CullMode.Off : CullMode.Back));
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetColor("_BaseColor", tint);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.doubleSidedGI = doubleSided;
            // _QueueOffset backs the forced queue: URP's inspector validation recomputes the queue as
            // Transparent(3000) + offset on any hand-edit, and the water < gas < glass order must survive.
            if (mat.HasProperty("_QueueOffset"))
                mat.SetFloat("_QueueOffset", GlassQueue - 3000f);
            mat.renderQueue = GlassQueue;
            mat.SetShaderPassEnabled("ShadowCaster", false);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssetIfDirty(mat);
            return mat;
        }

        // ------------------------------------------------------------------ colliders + spawn

        [MenuItem("Tools/NASA Sim/Biodome/Wire Colliders && Spawn Outside")]
        public static void WireCollidersAndSpawn()
        {
            GameObject dome = FindBiodomeInstance();
            if (dome == null)
            {
                Debug.LogWarning("[BiodomeFix] No imported biodome found in the scene — nothing to wire.");
                return;
            }

            ModelImportTools.WireUp(dome);

            Transform tunnel = FindDescendantStartingWith(dome.transform, "COL_tunnel");
            if (tunnel == null || !TryRendererBounds(tunnel.gameObject, out Bounds tb))
            {
                Debug.LogWarning("[BiodomeFix] No COL_tunnel with renderers found under the biodome — " +
                                 "colliders wired, but the outside spawn marker was not placed.", dome);
                return;
            }

            // Tunnel axis = the longer horizontal side of its bounds; the outside end is whichever end
            // sits farther from the dome's own footprint centre.
            TryRendererBounds(dome, out Bounds shell);
            Vector3 axis = tb.size.x >= tb.size.z ? Vector3.right : Vector3.forward;
            float extent = tb.size.x >= tb.size.z ? tb.extents.x : tb.extents.z;

            Vector3 endA = tb.center + axis * (extent + 2f);
            Vector3 endB = tb.center - axis * (extent + 2f);
            Vector3 outside = FlatDistance(endA, shell.center) >= FlatDistance(endB, shell.center) ? endA : endB;

            // Ground it on whatever is below (the tunnel colliders exist now; outside terrain may not
            // have a collider yet — fall back to the tunnel's floor height).
            float groundY = tb.min.y;
            if (Physics.Raycast(new Vector3(outside.x, tb.max.y + 20f, outside.z), Vector3.down,
                                out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                groundY = hit.point.y;

            var marker = GameObject.Find("SPAWN_Outside");
            if (marker == null)
            {
                marker = new GameObject("SPAWN_Outside");
                Undo.RegisterCreatedObjectUndo(marker, "Create SPAWN_Outside");
            }
            Vector3 toEntrance = tb.center - outside; toEntrance.y = 0f;
            marker.transform.SetPositionAndRotation(
                new Vector3(outside.x, groundY + 0.05f, outside.z),
                Quaternion.LookRotation(toEntrance.normalized, Vector3.up));

            var astronaut = Object.FindAnyObjectByType<AstronautController>();
            if (astronaut != null)
                astronaut.Teleport(marker.transform.position, marker.transform.rotation);

            EditorSceneManager.MarkSceneDirty(dome.scene);
            Debug.Log($"[BiodomeFix] Colliders wired on '{dome.name}'; SPAWN_Outside placed at " +
                      $"{marker.transform.position} facing the tunnel entrance" +
                      (astronaut != null ? " and the astronaut moved there." : "."), marker);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// The imported biodome carries the authored COL_tunnel / NOCOL_Dome markers; the primitive
        /// placeholder group that is ALSO named "Biodome" (under Environment) does not — so search for the
        /// markers, not the name.
        /// </summary>
        static GameObject FindBiodomeInstance()
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include,
                                                                  FindObjectsSortMode.None))
            {
                string n = t.name;
                if (n.StartsWith("COL_tunnel") || n.StartsWith("NOCOL_Dome") || n.StartsWith("NOCOL_Biosphere"))
                {
                    // The FBX instance root, not t.root: if the dome ever gets parented under a group
                    // (e.g. Environment), t.root would over-scope the material sweep to the whole group.
                    var prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject);
                    return prefabRoot != null ? prefabRoot : t.root.gameObject;
                }
            }
            return null;
        }

        static Transform FindDescendantStartingWith(Transform root, string prefix)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(prefix)) return t;
            return null;
        }

        static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        static bool TryRendererBounds(GameObject go, out Bounds bounds)
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
