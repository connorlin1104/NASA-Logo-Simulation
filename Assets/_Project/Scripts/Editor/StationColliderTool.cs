using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NasaSim;

namespace NasaSim.EditorTools
{
    /// <summary>
    /// Gives the imported station its collision, one labelled group at a time. Drop a group in a bucket,
    /// pick what it should be, press Build.
    ///
    /// Four kinds of collision, because a station needs four different things:
    ///
    /// <list type="bullet">
    /// <item><b>Walk surface</b> — for stairs and decks. Bakes ONE smooth skin over the group by
    /// sampling it from above and easing the tread profile into a ramp. This is the fix for the classic
    /// "I have to jump up my own staircase": a <see cref="CharacterController"/> refuses ledges taller
    /// than its Step Offset and slopes past its Slope Limit, so modelled steps read to it as a wall.
    /// Because it follows the model's own footprint rather than assuming a shape, it works on a straight
    /// flight, the half-moon sweeps and a full spiral alike.</item>
    /// <item><b>Solid mesh</b> — an exact MeshCollider per part. For the tube and the hatches, where you
    /// walk through the middle and the walls have to be where they look.</item>
    /// <item><b>Box per part</b> — one box per piece. For handrails: fifty posts and rails get fifty
    /// cheap boxes instead of fifty mesh colliders.</item>
    /// <item><b>Box hull</b> — one box round the whole group. The blunt instrument, for anything that
    /// just needs to be solid.</item>
    /// </list>
    ///
    /// Walk surfaces are generated world-space objects and live under a "StationColliders" root; the
    /// other three add components to the model's own parts, so they follow it if it moves. Everything is
    /// re-runnable: build, walk it, change a number, build again.
    /// </summary>
    public sealed class StationColliderTool : EditorWindow
    {
        public enum Mode
        {
            WalkSurface,
            SolidMesh,
            BoxPerPart,
            BoxHull,
            Skip,
        }

        [System.Serializable]
        public class Job
        {
            public GameObject target;
            public Mode mode = Mode.WalkSurface;
        }

        const string GeneratedRootName = "StationColliders";
        const int MaxSamplesPerSide = 256;

        [SerializeField] List<Job> _jobs = new List<Job>();
        [SerializeField] float _cellSize = 0.15f;
        [SerializeField] float _surfaceLift = 0.06f;
        [SerializeField] int _smoothing = 4;
        [SerializeField] bool _removeThinObstacles = true;
        [SerializeField] int _medianRadius = 2;
        [SerializeField] bool _disableSourceColliders = false;
        [SerializeField] string _excludeWords =
            "rail, handle, post, monitor, moniter, desktop, table, chair, keyboard, mouse, purifier, waterthing, bend";
        [SerializeField] bool _showTuning;

        Vector2 _scroll;

        /// <summary>Groups worth colliding, and what each one usually wants to be.</summary>
        static readonly (string word, Mode mode)[] AutoRules =
        {
            ("stair", Mode.WalkSurface),
            ("step", Mode.WalkSurface),
            ("deck", Mode.WalkSurface),
            ("guardrail", Mode.BoxPerPart),
            ("rail", Mode.BoxPerPart),
            ("tunnel", Mode.SolidMesh),
            ("tube", Mode.SolidMesh),
            ("door", Mode.SolidMesh),
            ("hatch", Mode.SolidMesh),
            ("slab", Mode.SolidMesh),
        };

        [MenuItem("Tools/NASA Sim/Station/Colliders && Stairs")]
        public static void Open()
        {
            var window = GetWindow<StationColliderTool>(true, "Station Colliders", true);
            window.minSize = new Vector2(560f, 460f);
            if (window._jobs.Count == 0) window.AutoFill(silent: true);
            window.Show();
        }

        // ================================================================== UI

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Colliders for the labelled groups — the airlock stairs, handrails, tube and doors, and " +
                "the half-moon stair platforms.\n\n" +
                "Drop a group in a bucket, choose what it should be, press Build. Walk surfaces show up " +
                "as a green wireframe in the Scene view: green = ground the astronaut can stand on, and " +
                "you can see that without pressing Play.",
                MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fill from the scene")) AutoFill(silent: false);
                if (GUILayout.Button("Add empty row")) _jobs.Add(new Job());
                if (GUILayout.Button("Clear list")) _jobs.Clear();
            }

            EditorGUILayout.Space();
            DrawJobList();

            EditorGUILayout.Space();
            _showTuning = EditorGUILayout.Foldout(_showTuning, "Walk surface tuning", true);
            if (_showTuning) DrawTuning();

            EditorGUILayout.Space();
            int live = CountLiveJobs();
            using (new EditorGUI.DisabledScope(live == 0))
                if (GUILayout.Button($"Build colliders for {live} group(s)", GUILayout.Height(34f)))
                    BuildAll();

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select the generated walk surfaces"))
                {
                    var root = GameObject.Find(GeneratedRootName);
                    if (root != null) Selection.activeGameObject = root;
                    else Debug.Log("[StationColliders] Nothing generated yet.");
                }
                if (GUILayout.Button("Delete the generated walk surfaces")) DeleteGenerated();
            }
        }

        void DrawJobList()
        {
            EditorGUILayout.LabelField("Groups", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(140f));

            for (int i = 0; i < _jobs.Count; i++)
            {
                var job = _jobs[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    job.target = (GameObject)EditorGUILayout.ObjectField(
                        job.target, typeof(GameObject), true, GUILayout.MinWidth(180f));
                    job.mode = (Mode)EditorGUILayout.EnumPopup(job.mode, GUILayout.Width(110f));

                    using (new EditorGUI.DisabledScope(job.target == null))
                        if (GUILayout.Button("Show", GUILayout.Width(46f)))
                        {
                            Selection.activeGameObject = job.target;
                            EditorGUIUtility.PingObject(job.target);
                            SceneView.FrameLastActiveSceneView();
                        }

                    if (GUILayout.Button("×", GUILayout.Width(22f)))
                    {
                        _jobs.RemoveAt(i);
                        i--;
                        continue;
                    }
                }

                if (job.target != null)
                    EditorGUILayout.LabelField(" ", StationBuild.PathOf(job.target.transform),
                                               EditorStyles.miniLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        void DrawTuning()
        {
            EditorGUI.indentLevel++;
            _cellSize = EditorGUILayout.Slider(
                new GUIContent("Sample spacing (m)", "How finely the group is sampled from above. " +
                                                     "Smaller follows the shape more closely and costs a " +
                                                     "heavier collider. 0.15 suits a staircase."),
                _cellSize, 0.04f, 0.6f);
            _smoothing = EditorGUILayout.IntSlider(
                new GUIContent("Smoothing passes", "What turns modelled steps into a walkable ramp. " +
                                                   "Raise it if the astronaut still catches on the " +
                                                   "treads; lower it if the ramp cuts a corner."),
                _smoothing, 0, 12);
            _surfaceLift = EditorGUILayout.Slider(
                new GUIContent("Surface lift (m)", "Raise the skin this far above the treads so the boots " +
                                                   "neither float nor sink."),
                _surfaceLift, 0f, 0.4f);

            _removeThinObstacles = EditorGUILayout.Toggle(
                new GUIContent("Ignore thin obstacles", "A median pass that deletes narrow spikes — " +
                                                        "handrails, posts, a lamp — from the sampled " +
                                                        "surface, so the ramp goes UNDER them instead of " +
                                                        "over them."),
                _removeThinObstacles);
            using (new EditorGUI.DisabledScope(!_removeThinObstacles))
                _medianRadius = EditorGUILayout.IntSlider(
                    new GUIContent("   Obstacle width", "How wide a thing counts as 'thin'. In samples."),
                    _medianRadius, 1, 5);

            EditorGUILayout.LabelField(
                new GUIContent("Don't stand on", "Parts whose name contains any of these words are left " +
                                                 "out of the sampling, so the surface passes beneath " +
                                                 "them. This is how a stair platform gets a floor without " +
                                                 "its desks becoming walkable furniture."));
            _excludeWords = EditorGUILayout.TextArea(_excludeWords, GUILayout.Height(34f));

            _disableSourceColliders = EditorGUILayout.Toggle(
                new GUIContent("Switch off the group's own colliders",
                               "Only if the group already HAS colliders and they are what you're " +
                               "replacing. These models arrive with none, so this normally stays off."),
                _disableSourceColliders);
            EditorGUI.indentLevel--;
        }

        int CountLiveJobs()
        {
            int n = 0;
            foreach (var j in _jobs)
                if (j != null && j.target != null && j.mode != Mode.Skip) n++;
            return n;
        }

        // ================================================================== auto-fill

        /// <summary>
        /// Find the groups YOU labelled. The imported Maya parts all carry a namespace
        /// ("newGreenHouse_2:Step1"); the groups you made in the Hierarchy do not — so a name without a
        /// colon is a reliable signal for "a human named this on purpose", which is exactly the level
        /// colliders want to be built at.
        /// </summary>
        void AutoFill(bool silent)
        {
            var found = new List<(Transform t, Mode mode)>();

            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
            {
                // A Maya part, not one of your labels. This one test is what keeps the list at the level
                // colliders want to be built at: the imported parts are all "newGreenHouse_2:Step1" and
                // friends, so matching them would bury your six groups under four hundred meshes.
                if (t.name.Contains(":")) continue;
                if (t.GetComponentInChildren<Renderer>(true) == null) continue;
                string n = t.name.ToLowerInvariant();

                foreach (var (word, mode) in AutoRules)
                {
                    if (!n.Contains(word)) continue;
                    found.Add((t, mode));
                    break;
                }
            }

            // Keep the DEEPEST label in any nest: "EvanTunnel" also contains "tunnel", but the group
            // actually called "Tunnel" inside it is the one that wants the collider.
            var kept = new List<(Transform t, Mode mode)>();
            foreach (var candidate in found)
            {
                bool isAncestorOfAnother = false;
                foreach (var other in found)
                {
                    if (other.t == candidate.t) continue;
                    if (other.t.IsChildOf(candidate.t)) { isAncestorOfAnother = true; break; }
                }
                if (!isAncestorOfAnother) kept.Add(candidate);
            }

            int added = 0;
            foreach (var (t, mode) in kept)
            {
                if (_jobs.Exists(j => j.target == t.gameObject)) continue;
                _jobs.Add(new Job { target = t.gameObject, mode = mode });
                added++;
            }

            if (silent) return;
            if (added == 0)
                Debug.Log("[StationColliders] Nothing new found. Everything matching is already listed — " +
                          "or your groups are named something this doesn't recognise, in which case drag " +
                          "them into the buckets yourself.");
            else
                Debug.Log($"[StationColliders] Added {added} group(s). Check each row's mode before " +
                          "building — the guess comes from the group's name.");
        }

        // ================================================================== build

        void BuildAll()
        {
            var holder = StationBuild.GeneratedRoot(GeneratedRootName, clearChildren: false);
            var usedNames = new HashSet<string>();
            var report = new List<string>();
            int failures = 0;

            foreach (var job in _jobs)
            {
                if (job == null || job.target == null || job.mode == Mode.Skip) continue;
                if (!StationBuild.RequireSceneObject(job.target, "group")) { failures++; continue; }

                string line;
                bool ok;
                switch (job.mode)
                {
                    case Mode.WalkSurface:
                        ok = BakeWalkSurface(job.target, holder.transform, usedNames, out line);
                        break;
                    case Mode.SolidMesh:
                        ok = AddSolidMeshColliders(job.target, out line);
                        break;
                    case Mode.BoxPerPart:
                        ok = AddBoxPerPart(job.target, out line);
                        break;
                    default:
                        ok = AddBoxHull(job.target, out line);
                        break;
                }

                if (!ok) failures++;
                report.Add((ok ? "  ✓ " : "  ✗ ") + line);
            }

            if (holder.transform.childCount == 0) Undo.DestroyObjectImmediate(holder);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            string summary = $"[StationColliders] Built {report.Count - failures} of {report.Count} group(s).\n" +
                             string.Join("\n", report) +
                             "\n  Green wireframes in the Scene view are the walk surfaces. If one doesn't " +
                             "cover your steps, raise Smoothing passes or lower Sample spacing and build again.";
            if (failures > 0) Debug.LogWarning(summary);
            else Debug.Log(summary);
        }

        void DeleteGenerated()
        {
            var root = GameObject.Find(GeneratedRootName);
            if (root == null)
            {
                Debug.Log("[StationColliders] Nothing generated to delete.");
                return;
            }
            Undo.DestroyObjectImmediate(root);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[StationColliders] Generated walk surfaces removed. Colliders added directly to " +
                      "model parts (solid mesh / box) are untouched — remove those on the parts themselves.");
        }

        // ------------------------------------------------------------------ walk surface

        bool BakeWalkSurface(GameObject target, Transform holder, HashSet<string> usedNames, out string line)
        {
            string label = StationBuild.PathOf(target.transform);

            var sampled = CollectSampleObjects(target);
            if (sampled.Count == 0)
            {
                line = $"{target.name}: nothing left to sample (everything is excluded by 'Don't stand on', " +
                       "or the group has no meshes).";
                return false;
            }

            if (!BoundsOf(sampled, out Bounds bounds))
            {
                line = $"{target.name}: no renderer bounds to measure.";
                return false;
            }

            var colliders = new List<Collider>();
            var temporary = new List<Collider>();
            foreach (var part in sampled)
            {
                var existing = part.GetComponent<Collider>();
                if (existing != null && existing.enabled && !existing.isTrigger)
                {
                    colliders.Add(existing);
                    continue;
                }
                var mf = part.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                // Cooking a collider once in the editor is free; it is removed again below.
                var temp = part.AddComponent<MeshCollider>();
                temporary.Add(temp);
                colliders.Add(temp);
            }

            if (colliders.Count == 0)
            {
                line = $"{target.name}: nothing to raycast against.";
                return false;
            }

            float cell = Mathf.Max(0.02f, _cellSize);
            int nx = Mathf.Clamp(Mathf.CeilToInt(bounds.size.x / cell) + 1, 2, MaxSamplesPerSide);
            int nz = Mathf.Clamp(Mathf.CeilToInt(bounds.size.z / cell) + 1, 2, MaxSamplesPerSide);
            float stepX = bounds.size.x / (nx - 1);
            float stepZ = bounds.size.z / (nz - 1);

            var height = new float[nx * nz];
            var valid = new bool[nx * nz];
            float rayTop = bounds.max.y + 1f;
            float rayLength = bounds.size.y + 3f;
            int hits = 0;
            bool cancelled = false;

            try
            {
                for (int z = 0; z < nz; z++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Baking walk surface", $"{target.name} — row {z + 1}/{nz}", (float)z / nz))
                    {
                        cancelled = true;
                        break;
                    }

                    float wz = bounds.min.z + stepZ * z;
                    for (int x = 0; x < nx; x++)
                    {
                        float wx = bounds.min.x + stepX * x;
                        var ray = new Ray(new Vector3(wx, rayTop, wz), Vector3.down);
                        float nearest = float.MaxValue;
                        foreach (var c in colliders)
                        {
                            if (c == null) continue;
                            if (c.Raycast(ray, out RaycastHit hit, rayLength) && hit.distance < nearest)
                                nearest = hit.distance;
                        }
                        int i = z * nx + x;
                        if (nearest < float.MaxValue)
                        {
                            height[i] = rayTop - nearest;
                            valid[i] = true;
                            hits++;
                        }
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                foreach (var t in temporary) if (t != null) DestroyImmediate(t);
            }

            if (cancelled)
            {
                line = $"{target.name}: cancelled.";
                return false;
            }
            if (hits < 4)
            {
                line = $"{target.name}: the down-rays found no surface — is this the group with the meshes in it?";
                return false;
            }

            // Thin things (a handrail, a post) are a narrow ridge in this grid; a median pass wipes them
            // out while leaving a broad tread exactly where it is. Then the blur eases the steps into a
            // ramp. Order matters: blur first and a handrail would smear into a hill.
            if (_removeThinObstacles) MedianFilter(height, valid, nx, nz, _medianRadius);
            for (int pass = 0; pass < _smoothing; pass++) BoxBlur(height, valid, nx, nz);

            var mesh = BuildSurfaceMesh(height, valid, nx, nz, bounds.min, stepX, stepZ, _surfaceLift,
                                        out float maxSlope, out int quads);
            if (mesh == null)
            {
                line = $"{target.name}: the sampled surface had no complete cell to build from.";
                return false;
            }

            string name = UniqueName(target.transform, usedNames);
            Directory.CreateDirectory(StationBuild.DataDir);
            string meshPath = $"{StationBuild.DataDir}/WalkSurface_{StationBuild.Sanitize(name)}.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = StationBuild.FindOrCreateChild(holder, "WALK_" + name);
            var col = StationBuild.GetOrAdd<MeshCollider>(go);
            col.sharedMesh = null;
            col.convex = false;                    // a staircase skin is not convex; fine for a static walkable
            col.sharedMesh = mesh;

            var marker = StationBuild.GetOrAdd<WalkSurface>(go);
            marker.sourcePath = label;
            marker.maxSlopeDeg = maxSlope;
            marker.areaSqM = quads * stepX * stepZ;

            if (_disableSourceColliders)
                foreach (var c in target.GetComponentsInChildren<Collider>(true))
                {
                    if (!c.enabled || c.isTrigger) continue;
                    Undo.RecordObject(c, "Disable source colliders");
                    c.enabled = false;
                }

            line = $"{target.name}: walk surface, {marker.areaSqM:0} m², up to {maxSlope:0}°" +
                   (maxSlope > 50f
                       ? " — STEEPER than the astronaut's 50° slope limit, so it still won't be climbable. " +
                         "Raise Smoothing passes, or raise ccSlopeLimit on AstronautController."
                       : string.Empty);
            return true;
        }

        /// <summary>Parts to sample: everything with a mesh, minus the "don't stand on" words.</summary>
        List<GameObject> CollectSampleObjects(GameObject target)
        {
            var words = new List<string>();
            foreach (string raw in (_excludeWords ?? string.Empty).Split(','))
            {
                string w = raw.Trim().ToLowerInvariant();
                if (w.Length > 0) words.Add(w);
            }

            var list = new List<GameObject>();
            foreach (var mf in target.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                if (mf.GetComponent<Renderer>() == null) continue;

                // Excluded if this part OR any group it sits inside is named as furniture — a desk's legs
                // are called pCylinder47, so only the parent name identifies them.
                bool excluded = false;
                for (Transform t = mf.transform; t != null && !excluded; t = t.parent)
                {
                    string n = t.name.ToLowerInvariant();
                    foreach (string w in words)
                        if (n.Contains(w)) { excluded = true; break; }
                    if (t == target.transform) break;
                }
                if (!excluded) list.Add(mf.gameObject);
            }
            return list;
        }

        static bool BoundsOf(List<GameObject> objects, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (var go in objects)
            {
                var r = go.GetComponent<Renderer>();
                if (r == null) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        static void MedianFilter(float[] h, bool[] valid, int nx, int nz, int radius)
        {
            var source = (float[])h.Clone();
            var window = new List<float>((2 * radius + 1) * (2 * radius + 1));

            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    int i = z * nx + x;
                    if (!valid[i]) continue;

                    window.Clear();
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        int sz = z + dz;
                        if (sz < 0 || sz >= nz) continue;
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int sx = x + dx;
                            if (sx < 0 || sx >= nx) continue;
                            int j = sz * nx + sx;
                            if (valid[j]) window.Add(source[j]);
                        }
                    }
                    if (window.Count == 0) continue;
                    window.Sort();
                    h[i] = window[window.Count / 2];
                }
        }

        static void BoxBlur(float[] h, bool[] valid, int nx, int nz)
        {
            var source = (float[])h.Clone();
            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    int i = z * nx + x;
                    if (!valid[i]) continue;

                    float sum = 0f;
                    int n = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int sz = z + dz;
                        if (sz < 0 || sz >= nz) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = x + dx;
                            if (sx < 0 || sx >= nx) continue;
                            int j = sz * nx + sx;
                            if (!valid[j]) continue;
                            sum += source[j];
                            n++;
                        }
                    }
                    if (n > 0) h[i] = sum / n;
                }
        }

        /// <summary>
        /// A quad per grid cell whose four corners all found the model — so the skin stops at the
        /// staircase's real outline instead of squaring off its bounding box.
        /// </summary>
        static Mesh BuildSurfaceMesh(float[] h, bool[] valid, int nx, int nz, Vector3 origin,
                                     float stepX, float stepZ, float lift, out float maxSlopeDeg,
                                     out int quads)
        {
            var verts = new List<Vector3>(nx * nz);
            var index = new int[nx * nz];
            for (int i = 0; i < index.Length; i++) index[i] = -1;

            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++)
                {
                    int i = z * nx + x;
                    if (!valid[i]) continue;
                    index[i] = verts.Count;
                    verts.Add(new Vector3(origin.x + stepX * x, h[i] + lift, origin.z + stepZ * z));
                }

            var tris = new List<int>();
            quads = 0;
            for (int z = 0; z < nz - 1; z++)
                for (int x = 0; x < nx - 1; x++)
                {
                    int a = index[z * nx + x];
                    int b = index[(z + 1) * nx + x];
                    int c = index[(z + 1) * nx + x + 1];
                    int d = index[z * nx + x + 1];
                    if (a < 0 || b < 0 || c < 0 || d < 0) continue;

                    // Winding matched to TerrainColliderBaker's, which produces upward-facing normals.
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(a); tris.Add(c); tris.Add(d);
                    quads++;
                }

            maxSlopeDeg = 0f;
            if (quads == 0) return null;

            for (int t = 0; t < tris.Count; t += 3)
            {
                Vector3 n = Vector3.Cross(verts[tris[t + 1]] - verts[tris[t]],
                                          verts[tris[t + 2]] - verts[tris[t]]);
                if (n.sqrMagnitude < 1e-12f) continue;
                float slope = Vector3.Angle(n.normalized, Vector3.up);
                if (slope > maxSlopeDeg) maxSlopeDeg = slope;
            }

            var mesh = new Mesh { name = "WalkSurface" };
            if (verts.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Two groups in this scene are both called "Stairs" — qualify the name with its parent.</summary>
        static string UniqueName(Transform t, HashSet<string> used)
        {
            string baseName = t.parent != null ? $"{t.parent.name}_{t.name}" : t.name;
            baseName = baseName.Replace(":", "_");
            string name = baseName;
            for (int i = 2; used.Contains(name); i++) name = $"{baseName}_{i}";
            used.Add(name);
            return name;
        }

        // ------------------------------------------------------------------ direct colliders

        static bool AddSolidMeshColliders(GameObject target, out string line)
        {
            int added = 0, reused = 0, triangles = 0;
            foreach (var mf in target.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                // GetIndexCount rather than triangles.Length: the latter copies the whole index buffer,
                // and this sweep can cross a few hundred parts.
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++)
                    triangles += (int)(mf.sharedMesh.GetIndexCount(s) / 3);

                var col = mf.GetComponent<MeshCollider>();
                if (col == null) { col = Undo.AddComponent<MeshCollider>(mf.gameObject); added++; }
                else reused++;

                Undo.RecordObject(col, "Add Solid Colliders");
                col.convex = false;
                col.sharedMesh = mf.sharedMesh;
                col.enabled = true;
                EditorUtility.SetDirty(col);
            }

            if (added + reused == 0)
            {
                line = $"{target.name}: no meshes to collide.";
                return false;
            }

            line = $"{target.name}: solid mesh on {added + reused} part(s), {triangles:N0} triangles" +
                   (triangles > 250000
                       ? " — that is a lot for physics. Use Box per part instead if it drags."
                       : string.Empty);
            return true;
        }

        static bool AddBoxPerPart(GameObject target, out string line)
        {
            int n = 0;
            foreach (var mf in target.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var box = mf.GetComponent<BoxCollider>();
                if (box == null) box = Undo.AddComponent<BoxCollider>(mf.gameObject);
                Undo.RecordObject(box, "Add Box Colliders");
                // The mesh's own local bounds — snug, and free of the fattening that transforming world
                // bounds back into local space would cause on a rotated part.
                box.center = mf.sharedMesh.bounds.center;
                box.size = mf.sharedMesh.bounds.size;
                box.enabled = true;
                EditorUtility.SetDirty(box);
                n++;
            }

            if (n == 0)
            {
                line = $"{target.name}: no meshes to box.";
                return false;
            }
            line = $"{target.name}: {n} box collider(s), one per part.";
            return true;
        }

        static bool AddBoxHull(GameObject target, out string line)
        {
            if (!StationBuild.TryLocalBounds(target, target.transform, out Bounds local))
            {
                line = $"{target.name}: nothing to measure.";
                return false;
            }

            var box = target.GetComponent<BoxCollider>();
            if (box == null) box = Undo.AddComponent<BoxCollider>(target);
            Undo.RecordObject(box, "Add Box Hull");
            box.center = local.center;
            box.size = local.size;
            box.enabled = true;
            EditorUtility.SetDirty(box);

            Vector3 world = Vector3.Scale(local.size, target.transform.lossyScale);
            line = $"{target.name}: one box hull, {world.x:0.0} × {world.y:0.0} × {world.z:0.0} m.";
            return true;
        }
    }
}
