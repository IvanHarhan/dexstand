using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Watches for a trigger file next to the project and runs the matching build
/// step, so tooling can drive the editor while it stays open. Writes the result
/// back to a second file.
///
/// Commands: buildall | player | scene | preview
/// </summary>
[InitializeOnLoad]
public static class DexstandAutoRun
{
    private static readonly string Root =
        Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private static readonly string TriggerPath = Path.Combine(Root, "DexstandTrigger.txt");
    private static readonly string ResultPath = Path.Combine(Root, "DexstandResult.txt");

    private const double PollSeconds = 1.0;
    private static double nextPoll;

    static DexstandAutoRun()
    {
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + PollSeconds;

        // Running a rebuild mid-play or mid-compile would fight the editor.
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!File.Exists(TriggerPath)) return;

        string command;
        try
        {
            command = File.ReadAllText(TriggerPath).Trim().ToLowerInvariant();
            File.Delete(TriggerPath);
        }
        catch (IOException)
        {
            return; // still being written; pick it up next tick
        }

        Run(command);
    }

    /// <summary>
    /// Full rebuild in dependency order — characters first, because both the
    /// player's body and the targets need the controllers and prefab it makes.
    /// Also the entry point for headless -executeMethod runs.
    /// </summary>
    public static void BuildEverything()
    {
        DexstandCharacterBuilder.BuildCharacters();

        // The body's own controller, on the humanoid rig and the rifle clips.
        // The character builder used to write this asset too, out of clips
        // authored for another skeleton, which left the player standing in their
        // bind pose with the weapon off the bottom of the screen.
        DexstandBodyAnimations.BuildFromLibrary();

        DexstandPlayerBuilder.BuildAll();
        DexstandGameplayBuilder.BuildGameplay();

        // The hands are no longer applied afterwards: BuildPlayer finishes with
        // DexstandViewmodelBuilder.BuildHands, so there is nothing left here to
        // undo or redo.
    }

    private static void Run(string command)
    {
        var started = DateTime.Now;
        try
        {
            switch (command)
            {
                case "buildall":
                    DexstandPlayerBuilder.BuildAll();
                    break;
                case "player":
                    DexstandPlayerBuilder.BuildPlayer();
                    break;
                case "scene":
                    DexstandSceneBuilder.BuildScene();
                    break;
                case "streamtiles":
                    DexstandSceneBuilder.StreamTiles();
                    break;
                case "tileinfo":
                    // where the parts of one streamed tile end up (A4 chunk check): bounds of every renderer
                    {
                        // left in the (unsaved) scene for MapShotTrigger.txt; "tileclear" takes them out again
                        var map = GameObject.Find("Map");
                        foreach (var n in new[] { "Tile_392_5648", "Tile_391_5648", "Tile_392_5647", "Tile_391_5647", "Tile_391_5649" })
                        {
                            var src = Resources.Load<GameObject>("Map/Tiles/" + n);
                            if (src != null) PrefabUtility.InstantiatePrefab(src, map.transform);
                        }
                        Debug.Log($"[TileInfo] placed, map scale {map.transform.localScale}");
                    }
                    break;
                case "a4check":
                    DexstandA4Check.Run();
                    break;
                case "tileclear":
                    {
                        var mapTf = GameObject.Find("Map").transform;
                        for (int i = mapTf.childCount - 1; i >= 0; i--)
                            if (mapTf.GetChild(i).name.StartsWith("Tile_"))
                                UnityEngine.Object.DestroyImmediate(mapTf.GetChild(i).gameObject);
                    }
                    break;
                case "streamnew":
                    DexstandSceneBuilder.StreamNewTiles();
                    break;
                case "preview":
                    // Render only. It used to rebuild the player first, which
                    // quietly replaced whatever the viewmodel builder had just
                    // put in — every screenshot showed the old arms.
                    DexstandPreview.RenderVariants();
                    break;
                case "gameplay":
                    DexstandGameplayBuilder.BuildGameplay();
                    break;
                case "bodycam":
                    DexstandGameplayBuilder.BuildBodycam();
                    break;
                case "smootharms":
                    DexstandArmsSmoother.Smooth();
                    break;
                case "consume":
                    DexstandConsumeBuilder.Build();
                    break;
                case "car":
                    DexstandGameplayBuilder.BuildCar();
                    break;
                case "carmats":
                    DexstandCarMaterials.Report();
                    break;
                case "fixaudi":
                    DexstandCarMaterials.FixAudi();
                    break;
                case "pedanim":
                    DexstandSceneBuilder.BuildPedestrianAnimator();
                    break;
                case "police":
                    DexstandGameplayBuilder.BuildPolice();
                    break;
                case "wheels":
                    DexstandGameplayBuilder.BuildWheels();
                    break;
                case "recompile":
                    // A plain Refresh only rebuilds what it thinks changed, and
                    // it can miss a newly added runtime script entirely. This
                    // forces every assembly to be rebuilt.
                    Write($"OK recompile at {started:HH:mm:ss}");
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation();
                    return;
                case "refresh":
                    // Picks up edited scripts without the window needing focus.
                    // The recompile reloads the domain, so nothing may follow.
                    Write($"OK refresh at {started:HH:mm:ss}");
                    AssetDatabase.Refresh(ImportAssetOptions.Default);
                    return;
                case "measure":
                    DexstandDiagnostics.MeasureMap();
                    break;
                case "orthodetail":
                    // the region's extracted ground/roof materials onto OrthoDetail, without reimporting 138 tiles
                    int switched = 0;
                    foreach (var guid in AssetDatabase.FindAssets("t:Material ortho_", new[] { DexstandMapImport.Folder + "/Materials" }))
                    {
                        var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                        if (m == null || !m.name.StartsWith("ortho_")) continue;
                        DexstandMapCutout.Apply(m);
                        EditorUtility.SetDirty(m);
                        switched++;
                    }
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[OrthoDetail] {switched} materials");
                    break;
                case "mapaudit":
                    // The Blender map in Assets/Maps/Import, checked before it
                    // becomes the level. Report goes to MapReport.txt.
                    DexstandMapImport.Audit();
                    break;
                case "sweep":
                    DexstandDiagnostics.SweepRifle();
                    break;
                case "grip":
                    DexstandDiagnostics.SweepGrip();
                    break;
                case "probe":
                    DexstandDiagnostics.ProbeGround();
                    break;
                case "measuretargets":
                    DexstandDiagnostics.MeasureTargets();
                    break;
                case "clips":
                    DexstandDiagnostics.InspectClips();
                    break;
                case "rig":
                    DexstandDiagnostics.VerifyRig();
                    break;
                case "models":
                    DexstandDiagnostics.InspectModels();
                    break;
                case "fp":
                    DexstandDiagnostics.InspectFirstPerson();
                    break;
                case "sample":
                    DexstandDiagnostics.SampleWalk();
                    break;
                case "viewmodel":
                    DexstandViewmodelBuilder.BuildViewmodel();
                    break;
                case "shaders":
                    DexstandDiagnostics.ShaderCheck();
                    break;
                case "audit":
                    DexstandDiagnostics.ShaderAudit();
                    break;
                case "repair":
                    DexstandGameplayBuilder.RepairRenderPipeline();
                    break;
                case "importinfima":
                    // Already downloaded to the Asset Store cache, so it can be
                    // imported without the Package Manager window.
                    AssetDatabase.ImportPackage(
                        @"C:\Users\harha\AppData\Roaming\Unity\Asset Store-5.x\Infima Games\" +
                        @"Complete ProjectsSystems\Low Poly Shooter Pack - Free Sample.unitypackage",
                        false);
                    Write($"OK importinfima at {started:HH:mm:ss}");
                    return;
                case "apk":
                    DexstandBuild.BuildAndroid();
                    break;
                case "quit":
                    // An APK has to be built by a second, headless editor, and
                    // two editors cannot hold the same project — so the open one
                    // has to go. Closing it by its window puts up "save the
                    // modified scene?" and waits for a human; this saves first
                    // and leaves, so an unattended build can start straight
                    // after. The result file is written before the exit, since
                    // nothing runs after it.
                    UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
                    AssetDatabase.SaveAssets();
                    Write($"OK quit at {started:HH:mm:ss}");
                    EditorApplication.Exit(0);
                    return;
                case "playtest":
                    // Error Pause freezes the editor on the first exception, and
                    // a frozen editor means WaitForSeconds never returns — the
                    // capture coroutine stops dead and no frames are written.
                    ClearErrorPause();
                    // The capture component reads this flag once play starts.
                    EditorPrefs.SetBool(PlaytestCapture.RequestKey, true);
                    Write($"OK playtest at {started:HH:mm:ss}");
                    EditorApplication.EnterPlaymode();
                    return;
                case "tour":
                    // the region map from street level in a few towns, and at night (MapTourCapture)
                    ClearErrorPause();
                    EditorPrefs.SetBool(MapTourCapture.TourKey, true);
                    Write($"OK tour at {started:HH:mm:ss}");
                    EditorApplication.EnterPlaymode();
                    return;
                case "portfolio":
                    // 1920x1080 frames for the GitHub page (PortfolioCapture)
                    ClearErrorPause();
                    EditorPrefs.SetBool(PortfolioCapture.Key, true);
                    Write($"OK portfolio at {started:HH:mm:ss}");
                    EditorApplication.EnterPlaymode();
                    return;
                case "walk":
                    // Same machinery as the playtest, but the component drives
                    // itself around the map and checks what it finds.
                    ClearErrorPause();
                    EditorPrefs.SetBool(PlaytestCapture.WalkKey, true);
                    Write($"OK walk at {started:HH:mm:ss}");
                    EditorApplication.EnterPlaymode();
                    return;
                case "cartouch":
                    ClearErrorPause();
                    EditorPrefs.SetBool(PlaytestCapture.CarKey, true);
                    Write($"OK cartouch at {started:HH:mm:ss}");
                    EditorApplication.EnterPlaymode();
                    return;
                case "unpause":
                    ClearErrorPause();
                    EditorApplication.isPaused = false;
                    break;
                case "characters":
                    DexstandCharacterBuilder.BuildCharacters();
                    break;
                case "bodyanim":
                    DexstandBodyAnimations.BuildBodyAnimations();
                    break;
                case "humanoid":
                    DexstandBodyAnimations.TryHumanoid();
                    break;
                case "bodyhuman":
                    DexstandBodyAnimations.BuildFromLibrary();
                    break;
                case "importcharacter":
                    DexstandCharacterImport.ImportCharacter();
                    break;
                case "bodyshot":
                    DexstandPreview.RenderBody();
                    break;
                case "everything":
                    BuildEverything();
                    break;
                default:
                    Write($"UNKNOWN COMMAND: {command}");
                    return;
            }

            Write($"OK {command} at {started:HH:mm:ss}");
            Debug.Log($"[AutoRun] {command} finished.");
        }
        catch (Exception e)
        {
            Write($"FAILED {command}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            Debug.LogError($"[AutoRun] {command} failed: {e}");
        }
    }

    /// <summary>
    /// Turns off the console's "Error Pause". There is no public API for it,
    /// so the console window's own setter is called by reflection.
    /// </summary>
    private static void ClearErrorPause()
    {
        try
        {
            var type = typeof(EditorApplication).Assembly.GetType("UnityEditor.ConsoleWindow");
            var setter = type?.GetMethod("SetConsoleErrorPause",
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            setter?.Invoke(null, new object[] { false });
            EditorApplication.isPaused = false;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AutoRun] Could not clear Error Pause: {e.Message}");
        }
    }

    private static void Write(string text)
    {
        try { File.WriteAllText(ResultPath, text); }
        catch (IOException) { /* nothing useful to do here */ }
    }
}
