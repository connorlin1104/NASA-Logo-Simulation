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
    /// <b>Fix Dome Glass</b> — the dome shell shipped with Unity's default opaque URP/Lit material
    /// ("New Material": Surface Opaque, Render Face Front). A closed hull rendered single-sided shows
    /// ONLY back faces to a camera inside it, so every triangle was culled and the dome vanished from
    /// within — while reading as a solid grey wall from outside. The fix is a purpose-built glass
    /// material: Surface Transparent + Render Face Both, low-alpha blue tint.
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

        [MenuItem("Tools/NASA Sim/Biodome/Fix Dome Glass")]
        public static void FixDomeGlass()
        {
            GameObject dome = FindBiodomeInstance();
            if (dome == null)
            {
                Debug.LogWarning("[BiodomeFix] No imported biodome found in the scene (looked for " +
                                 "COL_tunnel / NOCOL_Dome children). Drag Biodome.fbx into the scene first.");
                return;
            }

            Material glass = CreateOrRefreshGlassMaterial();
            var badGrey = AssetDatabase.LoadAssetAtPath<Material>(BadGreyMatPath);

            Undo.RegisterFullObjectHierarchyUndo(dome, "Fix Dome Glass");
            int replacedSlots = 0, shellRenderers = 0;

            foreach (var r in dome.GetComponentsInChildren<Renderer>(true))
            {
                bool isShell = r.name.ToLowerInvariant().Contains("dome") ||
                               r.name.ToLowerInvariant().Contains("biosphere");
                var mats = r.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < mats.Length; i++)
                {
                    bool isBad = mats[i] == null ||
                                 mats[i] == badGrey ||
                                 mats[i].name == "New Material";
                    if (isShell || isBad)
                    {
                        if (mats[i] != glass) { mats[i] = glass; replacedSlots++; changed = true; }
                    }
                }

                if (changed) r.sharedMaterials = mats;
                if (isShell)
                {
                    shellRenderers++;
                    // A glass dome must not plunge its own interior into shadow.
                    r.shadowCastingMode = ShadowCastingMode.Off;
                }
            }

            EditorSceneManager.MarkSceneDirty(dome.scene);
            Debug.Log($"[BiodomeFix] Dome glass fixed on '{dome.name}': {replacedSlots} material slot(s) " +
                      $"-> BiodomeGlass (transparent, double-sided), {shellRenderers} shell renderer(s) " +
                      "no longer cast shadows. The dome is now visible from inside AND see-through from outside.", dome);
        }

        static Material CreateOrRefreshGlassMaterial()
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
            mat.SetFloat("_Cull", (float)CullMode.Off);                       // Render Face Both — THE fix
            mat.SetFloat("_AlphaClip", 0f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 0.92f);
            mat.SetColor("_BaseColor", new Color(0.62f, 0.78f, 0.92f, 0.10f));
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.doubleSidedGI = true;
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
