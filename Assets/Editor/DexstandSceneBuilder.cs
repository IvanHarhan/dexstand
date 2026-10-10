using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Assembles the playable scene: map + colliders, NetworkManager (NetworkLauncher
/// + DesktopInputService), safety floor. The gameplay scripts and Player prefab
/// already existed — only the scene was never wired up.
/// </summary>
public static class DexstandSceneBuilder
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string PlayerPrefabPath = "Assets/Prefabs/Player.prefab";
    private const string MapPath = "Assets/map/map.fbx";

    /// <summary>
    /// The source map is authored under real-world metres: its sheds are 1.78
    /// units tall. A real shed is about 3m, which fixes the multiplier at 1.7
    /// and puts walls at 1.9m and crates at 0.45m next to a 1.8m player.
    /// Anything larger and the world reads as giant.
    /// </summary>
    private const float MapScale = 1.7f;

    [MenuItem("Dexstand/Собрать сцену")]
    public static void BuildScene()
    {
        Build();
    }

    [MenuItem("Dexstand/Собрать сцену и играть")]
    public static void BuildAndPlay()
    {
        if (Build())
            EditorApplication.EnterPlaymode();
    }

    private static bool Build()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
        if (playerPrefab == null)
        {
            Debug.LogError($"[Dexstand] Player prefab not found at {PlayerPrefabPath}");
            return false;
        }

        // --- Map -------------------------------------------------------------
        // Always rebuilt from the asset. The FBX root carries a large scale of
        // its own (the mesh itself is tiny), so MapScale has to multiply that
        // native scale — assigning it outright collapses the map to a speck.
        // By the root list, not GameObject.Find: an instance whose source FBX was
        // deleted (the single-model map, replaced by tiles) is invisible to Find
        // and was left beside the new map, as an empty "Map".
        foreach (var root in scene.GetRootGameObjects())
            if (root.name == "Map" || (root.name.StartsWith("Map") && PrefabUtility.IsPrefabAssetMissing(root)))
                Object.DestroyImmediate(root);

        // A map brought in from Blender takes over from the old one as soon as
        // it is in the import folder: the tiled region map first, else a single model.
        var imported = DexstandMapImport.CurrentPath();
        bool region = PlaceRegionMap();
        if (!region && imported != null)
        {
            PlaceImportedMap(imported);
        }
        else if (!region)
        {
            EnsureMapIsReadable();
            var mapAsset = AssetDatabase.LoadAssetAtPath<GameObject>(MapPath);
            if (mapAsset != null)
            {
                var map = (GameObject)PrefabUtility.InstantiatePrefab(mapAsset);
                map.name = "Map";
                map.transform.position = Vector3.zero;

                var native = map.transform.localScale;
                map.transform.localScale = native * MapScale;
                Debug.Log($"[Dexstand] Map native scale {native.x:F1} -> {native.x * MapScale:F1} ({MapScale}x).");

                map.isStatic = true;

                int colliders = 0;
                foreach (var mf in map.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    if (mf.GetComponent<MeshCollider>() != null) continue;
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    colliders++;
                }

                var b = GetBounds(map);
                Debug.Log($"[Dexstand] Map added: {colliders} mesh colliders, " +
                          $"bounds center {b.center}, size {b.size}");
            }
            else
            {
                Debug.LogWarning($"[Dexstand] Map not found at {MapPath} — floor only.");
            }
        }

        // --- Safety floor ----------------------------------------------------
        // Last-resort net only. It sits far below the arena: any higher and the
        // player rests on it instead of the map, which reads as being buried in
        // the ground. NetworkLauncher ignores it when it looks for the floor.
        var floor = GameObject.Find("SafetyFloor");
        if (floor == null)
            floor = new GameObject("SafetyFloor");

        floor.transform.position = new Vector3(0f, -20.5f, 0f);
        var box = floor.GetComponent<BoxCollider>();
        if (box == null)
            box = floor.AddComponent<BoxCollider>();

        // Wider than whatever map is loaded, so falling off any edge is caught.
        var placed = GameObject.Find("Map");
        var span = placed != null ? GetBounds(placed).size : Vector3.zero;
        box.size = new Vector3(Mathf.Max(400f, span.x * 1.5f), 1f, Mathf.Max(400f, span.z * 1.5f));

        // --- NetworkManager --------------------------------------------------
        var managerGo = GameObject.Find("NetworkManager");
        if (managerGo == null)
            managerGo = new GameObject("NetworkManager");

        var input = managerGo.GetComponent<DesktopInputService>();
        if (input == null)
            input = managerGo.AddComponent<DesktopInputService>();

        var launcher = managerGo.GetComponent<NetworkLauncher>();
        if (launcher == null)
            launcher = managerGo.AddComponent<NetworkLauncher>();

        launcher.playerPrefab = playerPrefab;

        var so = new SerializedObject(launcher);
        var prop = so.FindProperty("inputServiceObject");
        if (prop != null)
        {
            prop.objectReferenceValue = input;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // --- HUD ---------------------------------------------------------------
        // Builds its own canvas on Awake and finds the local player itself, so
        // there is nothing here to link and nothing to re-link when the player
        // prefab is rebuilt.
        var hudGo = GameObject.Find("HealthHud");
        if (hudGo == null) hudGo = new GameObject("HealthHud");
        if (hudGo.GetComponent<HealthHud>() == null) hudGo.AddComponent<HealthHud>();

        // --- Cameras ---------------------------------------------------------
        // Keep the template camera as a pre-spawn view, but let the player's own
        // camera render on top and own the single AudioListener.
        var mainCam = GameObject.Find("Main Camera");
        if (mainCam != null)
        {
            var cam = mainCam.GetComponent<Camera>();
            if (cam != null) cam.depth = -10f;
            var listener = mainCam.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = false;
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Dexstand] Scene assembled and saved. Press Play.");
        return true;
    }

    /// <summary>
    /// The Blender map, scaled to the player, centred on the origin and set so
    /// the ground under the middle of it is at height zero — the spawn search and
    /// the target placement both start from there.
    /// </summary>
    private static void PlaceImportedMap(string path)
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var map = (GameObject)PrefabUtility.InstantiatePrefab(asset);
        map.name = "Map";
        map.transform.position = Vector3.zero;
        map.transform.localScale *= DexstandMapImport.Scale;
        map.isStatic = true;
        foreach (var t in map.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;

        int colliders = DexstandMapImport.AddColliders(map);

        var b = GetBounds(map);
        map.transform.position -= new Vector3(b.center.x, 0f, b.center.z);

        // The ground under the middle, found by a ray from above the map rather
        // than by the bounds' floor, which is the lowest dip anywhere on it.
        Physics.SyncTransforms();
        float top = GetBounds(map).max.y + 10f;
        float ground = Physics.Raycast(new Vector3(0f, top, 0f), Vector3.down, out var hit, top * 2f + 1000f)
            ? hit.point.y
            : GetBounds(map).min.y;
        map.transform.position -= new Vector3(0f, ground, 0f);

        b = GetBounds(map);
        Debug.Log($"[Dexstand] Imported map {path} at {DexstandMapImport.Scale:F3}x: " +
                  $"{colliders} colliders, size {b.size}, ground lifted by {-ground:F2}.");
    }

    private const string FacadeMaterialPath = "Assets/Maps/Shaders/Facade.mat";

    /// <summary>
    /// The region map (Blender's export_region.py): one FBX per square kilometre,
    /// "Tile_E_N.fbx", already centred on the bus station with the ground there at
    /// zero, so nothing is recentred. Walls get the shared Dexstand/Facade material,
    /// and a MapStreamer switches far cells off behind the fog.
    /// </summary>
    private static bool PlaceRegionMap()
    {
        var tiles = AssetDatabase.FindAssets("t:Model", new[] { DexstandMapImport.Folder })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => System.IO.Path.GetFileName(p).StartsWith("Tile_"))
            .OrderBy(p => p).ToArray();
        if (tiles.Length == 0) return false;

        var map = new GameObject("Map");
        foreach (var path in tiles)
            PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), map.transform);
        map.transform.localScale = Vector3.one * DexstandMapImport.Scale;

        // Static for batching and occlusion, but not for baked GI: 60 km² of lightmaps is not wanted.
        var flags = StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic;
        foreach (var t in map.GetComponentsInChildren<Transform>(true))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, flags);

        int colliders = DexstandMapImport.AddColliders(map);

        var facade = FacadeMaterial();
        int walls = 0;
        foreach (var r in map.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || mats[i].name != "Facade") continue;
                mats[i] = facade;
                changed = true;
            }
            if (changed) { r.sharedMaterials = mats; walls++; }
            // cells far apart never share a shadow; the ground and trees need not cast one at all
            if (r.name.StartsWith("Ground_") || r.name.StartsWith("Roads_") || r.name.StartsWith("Marks_") ||
                r.name.StartsWith("Walks_") || r.name.StartsWith("Parking_") || r.name.StartsWith("Gutters_"))
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        var streamer = map.AddComponent<MapStreamer>();
        map.AddComponent<MapGrass>().material = MapMaterial("Assets/Maps/Shaders/GrassBlades.mat", "Dexstand/GrassBlades");
        map.AddComponent<MapDayNight>();
        var walkers = map.AddComponent<MapPedestrians>();
        walkers.prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/TargetCharacter.prefab");
        walkers.models = AssetDatabase.FindAssets("t:Model", new[] { DexstandNpcImport.Folder })
            .Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<GameObject>)
            .Where(m => m != null).ToArray();
        walkers.controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(PedestrianControllerPath)
                             ?? AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>("Assets/Animations/TargetController.controller");
        var trees = map.AddComponent<MapTrees>();
        trees.bark = TreeMaterial("TreeBark", null, new Color(0.2f, 0.15f, 0.1f));
        // the leaf picture is a bright cartoon green: toned down, and only a little of the shade-side glow
        trees.leaves = TreeMaterial("Cutout_Leaves", "foliage_leaves.png", new Color(0.42f, 0.48f, 0.34f));
        trees.needles = TreeMaterial("Cutout_Needles", "foliage_needles.png", new Color(0.5f, 0.54f, 0.46f));
        foreach (var m in new[] { trees.leaves, trees.needles })
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", new Color(0.1f, 0.1f, 0.1f));

        // Fog to hide where drawing stops, the colour of a hazy horizon.
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.72f, 0.78f, 0.84f);
        RenderSettings.fogStartDistance = streamer.drawDistance * 0.45f;
        RenderSettings.fogEndDistance = streamer.drawDistance;
        // past the last cell the sky's brown "ground" half showed as a band under the horizon: make it haze too
        var sky = RenderSettings.skybox;
        if (sky != null && sky.HasProperty("_GroundColor"))
        {
            const string skyPath = "Assets/Maps/Shaders/RegionSky.mat";
            var regionSky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
            if (regionSky == null)
            {
                regionSky = new Material(sky);
                AssetDatabase.CreateAsset(regionSky, skyPath);
            }
            var clouds = Shader.Find("Dexstand/CloudSky");
            if (clouds != null) regionSky.shader = clouds;  // blue sky with drifting clouds over the region
            regionSky.SetColor("_GroundColor", RenderSettings.fogColor);
            EditorUtility.SetDirty(regionSky);
            RenderSettings.skybox = regionSky;
        }

        var b = GetBounds(map);
        Debug.Log($"[Dexstand] Region map: {tiles.Length} tiles, {colliders} colliders, {walls} wall meshes, size {b.size}.");
        return true;
    }

    /// <summary>A material for MapTrees, instanced: bark in URP Lit, "Cutout_" leaves in Dexstand/TreeLeaves.</summary>
    private static Material TreeMaterial(string name, string texture, Color colour)
    {
        string path = $"Assets/Maps/Shaders/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        var leafShader = name.StartsWith("Cutout_") ? Shader.Find("Dexstand/TreeLeaves") : null;
        if (leafShader != null) mat.shader = leafShader;
        if (texture != null)
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(DexstandMapImport.Folder + "/Textures/" + texture));
        mat.SetColor("_BaseColor", colour);
        if (leafShader == null) DexstandMapCutout.Apply(mat);
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    /// <summary>A material asset for one of the map's runtime shaders, made on first use.</summary>
    private static Material MapMaterial(string path, string shader)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(shader));
            AssetDatabase.CreateAsset(mat, path);
        }
        return mat;
    }

    private static Material FacadeMaterial()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(FacadeMaterialPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Dexstand/Facade"));
            AssetDatabase.CreateAsset(mat, FacadeMaterialPath);
        }
        string tex = DexstandMapImport.Folder + "/Textures/";
        mat.SetTexture("_PlasterTex", AssetDatabase.LoadAssetAtPath<Texture2D>(tex + "painted_plaster_wall_Diffuse.jpg"));
        mat.SetTexture("_BrickTex", AssetDatabase.LoadAssetAtPath<Texture2D>(tex + "red_brick_03_Diffuse.jpg"));
        mat.SetTexture("_PlasterNormal", AssetDatabase.LoadAssetAtPath<Texture2D>(tex + "painted_plaster_wall_nor_gl.jpg"));
        mat.SetTexture("_BrickNormal", AssetDatabase.LoadAssetAtPath<Texture2D>(tex + "red_brick_03_nor_gl.jpg"));
        mat.enableInstancing = true;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }

    private static void EnsureMapIsReadable()
    {
        var importer = AssetImporter.GetAtPath(MapPath) as ModelImporter;
        if (importer == null || importer.isReadable) return;
        importer.isReadable = true;
        importer.SaveAndReimport();
    }

    private static Bounds GetBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    [System.Serializable] private class TileEntry { public string name; public float x, y, z, r; }
    [System.Serializable] private class TileManifest { public TileEntry[] tiles; }

    private const string PedestrianControllerPath = "Assets/Animations/PedestrianController.controller";

    /// <summary>
    /// The passers-by's (and police officers') animator: humanoid idle, walk and run from Animations.fbx on one
    /// "Speed" blend in m/s. They had the pop-up target's controller, whose clips are not humanoid: on the MPFB
    /// people the hips dropped to their feet — waist-deep in the pavement — and the arms held a rifle that was
    /// not there. Makes the controller and gives it to the scene's MapPedestrians.
    /// </summary>
    public static void BuildPedestrianAnimator()
    {
        var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/Models/Character/Animations.fbx").OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview")).ToDictionary(c => c.name.Substring(c.name.IndexOf('|') + 1));
        if (!clips.TryGetValue("Idle_Neutral", out var idle) || !clips.TryGetValue("Walk", out var walk) || !clips.TryGetValue("Run", out var run))
        {
            Debug.LogError("[Pedestrians] Animations.fbx has no Idle_Neutral/Walk/Run.");
            return;
        }
        AssetDatabase.DeleteAsset(PedestrianControllerPath);
        var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(PedestrianControllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        var state = controller.CreateBlendTreeInController("Locomotion", out var tree, 0);
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        tree.AddChild(idle, 0f);
        tree.AddChild(walk, 1.4f);
        tree.AddChild(run, 5f);
        controller.layers[0].stateMachine.defaultState = state;
        AssetDatabase.SaveAssets();

        var scene = EditorSceneManager.GetActiveScene();
        if (scene.path != ScenePath) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        foreach (var walkers in Object.FindObjectsByType<MapPedestrians>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            walkers.controller = controller;
            EditorUtility.SetDirty(walkers);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[Pedestrians] Humanoid controller made and given to the walkers.");
    }

    /// <summary>
    /// Splits the region map's "Tile_*" children into their own prefabs under Resources/Map/Tiles and adds
    /// MapTileStreamer, so the phone build does not keep all 138 in memory. Run this last, after "scene" and
    /// "gameplay": spawn placement, the safety floor and the parked car still raycast the map's colliders
    /// while those build, and once tiles are gone the map has none to hit. A later "scene" rebuild starts the
    /// map over from Assets/Maps/Import, so run this again after that.
    /// </summary>
    [MenuItem("Dexstand/Стриминг тайлов")]
    public static void StreamTiles()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var map = GameObject.Find("Map");
        if (map == null) { Debug.LogError("[Dexstand] StreamTiles: no Map in the scene."); return; }

        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        if (!AssetDatabase.IsValidFolder("Assets/Resources/Map")) AssetDatabase.CreateFolder("Assets/Resources", "Map");
        if (AssetDatabase.IsValidFolder("Assets/Resources/Map/Tiles")) AssetDatabase.DeleteAsset("Assets/Resources/Map/Tiles");
        AssetDatabase.CreateFolder("Assets/Resources/Map", "Tiles");

        var entries = new List<TileEntry>();
        foreach (var tileTf in map.transform.Cast<Transform>().ToList())
        {
            var tileGo = tileTf.gameObject;
            if (!tileGo.name.StartsWith("Tile_")) continue;
            var tb = GetBounds(tileGo);
            string name = tileGo.name;
            PrefabUtility.SaveAsPrefabAsset(tileGo, $"Assets/Resources/Map/Tiles/{name}.prefab");
            entries.Add(new TileEntry { name = name, x = tb.center.x, y = tb.center.y, z = tb.center.z, r = tb.extents.magnitude });
            Object.DestroyImmediate(tileGo);
        }
        System.IO.File.WriteAllText(Application.dataPath + "/Resources/Map/tiles_manifest.json",
            JsonUtility.ToJson(new TileManifest { tiles = entries.ToArray() }));
        AssetDatabase.ImportAsset("Assets/Resources/Map/tiles_manifest.json");

        if (map.GetComponent<MapTileStreamer>() == null) map.AddComponent<MapTileStreamer>();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Dexstand] StreamTiles: {entries.Count} tiles moved to Resources/Map/Tiles.");
    }

    /// <summary>
    /// Tiles in Assets/Maps/Import that have no streamed prefab yet (the A4 corridor chunks, 2026-09-27) go the
    /// way PlaceRegionMap + StreamTiles send the rest — facade material, no ground shadows, hidden collision
    /// meshes as colliders — straight into Resources/Map/Tiles and the manifest, without rebuilding the scene.
    /// Trigger "streamnew", or -executeMethod DexstandSceneBuilder.StreamNewTiles in batch mode.
    /// </summary>
    public static void StreamNewTiles()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var map = GameObject.Find("Map");
        if (map == null) { Debug.LogError("[Dexstand] StreamNewTiles: no Map in the scene."); return; }
        const string manifestPath = "Assets/Resources/Map/tiles_manifest.json";
        var manifest = JsonUtility.FromJson<TileManifest>(System.IO.File.ReadAllText(manifestPath));
        var entries = new List<TileEntry>(manifest.tiles);
        // tiles exported again (one name per line in restream.txt next to Assets) are streamed afresh
        string restream = System.IO.Path.Combine(Application.dataPath, "..", "restream.txt");
        if (System.IO.File.Exists(restream))
        {
            var again = new HashSet<string>(System.IO.File.ReadAllLines(restream).Select(l => l.Trim()).Where(l => l.Length > 0));
            entries.RemoveAll(e => again.Contains(e.name));
            foreach (var n in again) AssetDatabase.DeleteAsset($"Assets/Resources/Map/Tiles/{n}.prefab");
            System.IO.File.Delete(restream);
            Debug.Log($"[Dexstand] StreamNewTiles: {again.Count} tiles to stream again.");
        }
        // a manifest entry whose prefab is gone (a run that died half way) is streamed again too
        entries.RemoveAll(e => !System.IO.File.Exists(Application.dataPath + $"/Resources/Map/Tiles/{e.name}.prefab"));
        var have = new HashSet<string>(entries.Select(e => e.name));
        var facade = FacadeMaterial();
        int added = 0;
        void SaveManifest()
        {
            System.IO.File.WriteAllText(Application.dataPath + "/Resources/Map/tiles_manifest.json",
                JsonUtility.ToJson(new TileManifest { tiles = entries.ToArray() }));
        }
        foreach (var path in AssetDatabase.FindAssets("t:Model", new[] { DexstandMapImport.Folder })
                     .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith("Tile_") || have.Contains(name)) continue;
            var tile = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), map.transform);
            DexstandMapImport.AddColliders(tile);
            foreach (var r in tile.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                    if (mats[i] != null && mats[i].name == "Facade") { mats[i] = facade; changed = true; }
                if (changed) r.sharedMaterials = mats;
                if (r.name.StartsWith("Ground_") || r.name.StartsWith("Roads_") || r.name.StartsWith("Marks_") ||
                    r.name.StartsWith("Walks_") || r.name.StartsWith("Parking_") || r.name.StartsWith("Gutters_"))
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            var tb = GetBounds(tile);
            PrefabUtility.SaveAsPrefabAsset(tile, $"Assets/Resources/Map/Tiles/{name}.prefab");
            entries.Add(new TileEntry { name = name, x = tb.center.x, y = tb.center.y, z = tb.center.z, r = tb.extents.magnitude });
            Object.DestroyImmediate(tile);
            added++;
            // 306 tiles in one go ran a batch Unity out of memory (2026-09-28): let each few go, keep the progress
            if (added % 6 == 0)
            {
                EditorUtility.UnloadUnusedAssetsImmediate();
                System.GC.Collect();
                SaveManifest();
            }
        }
        SaveManifest();
        AssetDatabase.ImportAsset(manifestPath);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[Dexstand] StreamNewTiles: {added} new tiles, {entries.Count} in the manifest.");
    }
}
