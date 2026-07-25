using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Builds the two pieces of scenery that have no model to import: the star background and the
    /// lunar surface the sim sits on.
    ///
    /// Both are made in Unity rather than exported from Maya on purpose. A starfield is a texture on
    /// the inside of the sky, not geometry - a modelled sphere would be thousands of wasted triangles
    /// that still has to be scaled bigger than the far clip plane. And a Unity Terrain stores its
    /// surface as a heightmap, so it renders with built-in LOD and culling that an imported mesh of
    /// the same detail cannot match.
    ///
    /// Textures come from Assets/_Project/Textures (copied out of the Maya project's sourceimages).
    /// </summary>
    public static class MoonEnvironmentSetup
    {
        const string TextureDir = "Assets/_Project/Textures";
        const string MatDir = "Assets/_Project/Materials";
        const string DataDir = "Assets/_Project/Data";

        const string StarTexture = "8k_stars_milky_way";
        const string MoonTexture = "8k_moon";

        // The logo auto-fits to ~40 units across, so the flat middle has to comfortably clear that
        // plus the biodome (~41 units) sitting beside it.
        const float TerrainSize = 500f;      // metres square
        const float TerrainHeight = 70f;     // metres from lowest point to highest
        const float FlatPadRadius = 70f;     // metres of dead-flat ground at the centre
        const int HeightmapResolution = 513;
        const int CraterCount = 40;

        // ================================================================== space background

        [MenuItem("Tools/NASA Sim/Environment/Create Space Skybox")]
        public static void CreateSkybox()
        {
            var stars = LoadTexture(StarTexture);
            if (stars == null) return;

            // A panoramic (equirectangular) skybox takes the star map straight from Maya with no
            // conversion to a six-sided cubemap.
            var shader = Shader.Find("Skybox/Panoramic");
            if (shader == null)
            {
                Debug.LogError("[MoonEnvironment] Shader 'Skybox/Panoramic' not found.");
                return;
            }

            Directory.CreateDirectory(MatDir);
            string path = $"{MatDir}/SpaceSkybox.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetTexture("_MainTex", stars);
            material.SetFloat("_Mapping", 1f);     // latitude/longitude layout
            material.SetFloat("_ImageType", 0f);   // full 360
            material.SetFloat("_Exposure", 1f);
            EditorUtility.SetDirty(material);

            RenderSettings.skybox = material;

            // Vacuum has no atmosphere to bounce light, so the sky must not light the scene or the
            // shadows go milky grey instead of near-black.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.03f, 0.03f, 0.04f);
            RenderSettings.fog = false;

            var sun = Object.FindAnyObjectByType<Light>();
            if (sun != null && sun.type == LightType.Directional)
            {
                sun.color = Color.white;
                sun.intensity = 1.4f;
                sun.shadows = LightShadows.Hard;   // no atmosphere = knife-edged shadows
                sun.transform.rotation = Quaternion.Euler(18f, 40f, 0f);
            }

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"[MoonEnvironment] Skybox set to '{StarTexture}'. Ambient light dropped to near " +
                      "black and the sun set to hard shadows for a vacuum look.", material);
        }

        // ================================================================== moon terrain

        [MenuItem("Tools/NASA Sim/Environment/Create Moon Terrain")]
        public static void CreateTerrain()
        {
            Directory.CreateDirectory(DataDir);
            string path = $"{DataDir}/MoonTerrain.asset";

            var data = new TerrainData
            {
                heightmapResolution = HeightmapResolution,
                size = new Vector3(TerrainSize, TerrainHeight, TerrainSize),
                baseMapResolution = 1024,
            };
            data.SetDetailResolution(1024, 16);
            data.SetHeights(0, 0, BuildHeights(HeightmapResolution));

            var layer = BuildTerrainLayer();
            if (layer != null) data.terrainLayers = new[] { layer };

            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(data, path);

            var existing = Object.FindAnyObjectByType<Terrain>();
            if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "MoonTerrain";
            Undo.RegisterCreatedObjectUndo(go, "Create Moon Terrain");

            // Terrains are built from a corner, and their heights run 0..1 upward. Offset so the flat
            // pad lands exactly on y = 0, where the logo, tractor and biodome already live.
            go.transform.position = new Vector3(-TerrainSize / 2f, -PadHeight * TerrainHeight,
                                                -TerrainSize / 2f);

            var terrain = go.GetComponent<Terrain>();
            terrain.heightmapPixelError = 8f;      // aggressive LOD; the surface is smooth anyway
            terrain.drawInstanced = true;

            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(go.scene);
            Selection.activeGameObject = go;
            Debug.Log($"[MoonEnvironment] Built a {TerrainSize:0} x {TerrainSize:0} m moon terrain with " +
                      $"{CraterCount} craters and a flat {FlatPadRadius * 2f:0} m pad at the centre, " +
                      "sitting at y = 0.\n" +
                      "  The old 'Floor' plane is still there - delete it, or keep it as the mowable " +
                      "surface and let the terrain be the horizon.", go);
        }

        /// <summary>Height the flat pad sits at, as a 0..1 fraction of the terrain's height.</summary>
        const float PadHeight = 0.35f;

        /// <summary>
        /// Rolling regolith with craters punched into it, flat in the middle. Craters are a rim that
        /// rises and a bowl that drops - doing both is what makes them read as impacts rather than
        /// dents.
        /// </summary>
        static float[,] BuildHeights(int resolution)
        {
            var heights = new float[resolution, resolution];
            var random = new System.Random(20260724);

            var craterX = new float[CraterCount];
            var craterY = new float[CraterCount];
            var craterR = new float[CraterCount];
            var craterD = new float[CraterCount];
            for (int c = 0; c < CraterCount; c++)
            {
                craterX[c] = (float)random.NextDouble();
                craterY[c] = (float)random.NextDouble();
                craterR[c] = Mathf.Lerp(0.02f, 0.11f, (float)random.NextDouble());
                craterD[c] = Mathf.Lerp(0.25f, 0.85f, (float)random.NextDouble());
            }

            // Pad radius as a fraction of the terrain, plus a skirt to ease back into the hills.
            float pad = FlatPadRadius / TerrainSize;
            float skirt = pad * 1.9f;

            for (int y = 0; y < resolution; y++)
            {
                float v = (float)y / (resolution - 1);
                for (int x = 0; x < resolution; x++)
                {
                    float u = (float)x / (resolution - 1);

                    // Two octaves of gentle undulation.
                    float h = PadHeight
                              + (Mathf.PerlinNoise(u * 3.5f, v * 3.5f) - 0.5f) * 0.30f
                              + (Mathf.PerlinNoise(u * 11f, v * 11f) - 0.5f) * 0.08f;

                    for (int c = 0; c < CraterCount; c++)
                    {
                        float d = Mathf.Sqrt((u - craterX[c]) * (u - craterX[c]) +
                                             (v - craterY[c]) * (v - craterY[c])) / craterR[c];
                        if (d >= 1.6f) continue;

                        if (d < 1f) h -= craterD[c] * 0.16f * Mathf.Cos(d * Mathf.PI * 0.5f);
                        else h += craterD[c] * 0.05f * Mathf.Sin((1.6f - d) / 0.6f * Mathf.PI);
                    }

                    // Flatten the middle so the logo, tractor and biodome sit on level ground.
                    float r = Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (v - 0.5f) * (v - 0.5f));
                    float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(pad, skirt, r));
                    heights[y, x] = Mathf.Clamp01(Mathf.Lerp(PadHeight, h, blend));
                }
            }
            return heights;
        }

        static TerrainLayer BuildTerrainLayer()
        {
            var moon = LoadTexture(MoonTexture);
            if (moon == null) return null;

            string path = $"{DataDir}/MoonSurface.terrainlayer";
            var layer = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (layer == null)
            {
                layer = new TerrainLayer();
                AssetDatabase.CreateAsset(layer, path);
            }
            layer.diffuseTexture = moon;
            // The moon map is a whole-globe photo; tiling it small enough reads as regolith instead
            // of a picture of the moon printed on the ground.
            layer.tileSize = new Vector2(35f, 35f);
            layer.specular = Color.black;
            layer.metallic = 0f;
            layer.smoothness = 0f;
            EditorUtility.SetDirty(layer);
            return layer;
        }

        static Texture2D LoadTexture(string baseName)
        {
            foreach (var extension in new[] { "jpg", "png", "jpeg", "tif" })
            {
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    $"{TextureDir}/{baseName}.{extension}");
                if (texture != null) return texture;
            }
            Debug.LogError($"[MoonEnvironment] '{baseName}' not found in {TextureDir}. Copy it there " +
                           "from the Maya project's sourceimages/SpaceEnvironmentTextures folder.");
            return null;
        }
    }
}
