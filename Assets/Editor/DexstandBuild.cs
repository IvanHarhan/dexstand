using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Produces an installable Android APK from the editor, so a phone test does
/// not need the build window.
/// </summary>
public static class DexstandBuild
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string OutputPath = @"C:\Users\harha\Desktop\Dexstand.apk";
    private const string WindowsPath = @"A:\DexstandBuild\Dexstand.exe";  // C: has no room for a PC build

    /// <summary>The PC game as a folder with Dexstand.exe (Mono: quick to build, no C++ toolchain needed).</summary>
    /// <summary>The build's date and time as its version, shown in the game's corner (VersionLabel).</summary>
    private static void StampVersion() => PlayerSettings.bundleVersion = System.DateTime.Now.ToString("yyyy.MM.dd-HH.mm");

    public static void BuildWindows()
    {
        PlayerSettings.companyName = "Dexstand";
        PlayerSettings.productName = "Dexstand";
        StampVersion();
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
        {
            Debug.Log("[Build] Switching to Windows — this reimports assets and takes a while.");
            EditorUserBuildSettings.SwitchActiveBuildTarget(NamedBuildTarget.Standalone, BuildTarget.StandaloneWindows64);
        }
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        Directory.CreateDirectory(Path.GetDirectoryName(WindowsPath));
        // The map tiles go out of Resources for the build and into asset bundles beside it (MapTileStreamer loads
        // them): in Resources all 306 made one resources.assets over 4 GB and the build crashed writing it.
        if (!AssetDatabase.IsValidFolder(TilesRes) && AssetDatabase.IsValidFolder(TilesOut))
            AssetDatabase.MoveAsset(TilesOut, TilesRes);  // a build that died left them moved
        string moved = AssetDatabase.MoveAsset(TilesRes, TilesOut);
        if (!string.IsNullOrEmpty(moved)) { Debug.LogError("[Build] FAILED: could not move the tiles out of Resources: " + moved); return; }
        BuildReport report;
        try
        {
            if (!BuildTileBundles(BuildTarget.StandaloneWindows64)) { Debug.LogError("[Build] FAILED: tile bundles"); return; }
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = WindowsPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });
        }
        finally
        {
            AssetDatabase.MoveAsset(TilesOut, TilesRes);
        }
        var summary = report.summary;
        if (summary.result == BuildResult.Succeeded)
        {
            string dst = Path.Combine(Path.GetDirectoryName(WindowsPath), "Dexstand_Data", "StreamingAssets", "tiles");
            if (Directory.Exists(dst)) Directory.Delete(dst, true);
            Directory.CreateDirectory(dst);
            long bytes = 0;
            foreach (var f in Directory.GetFiles(BundlesPath).Where(f => !f.EndsWith(".manifest") && Path.GetFileName(f) != Path.GetFileName(BundlesPath)))
            {
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
                bytes += new FileInfo(f).Length;
            }
            Debug.Log($"[Build] EXE OK: {WindowsPath} v{PlayerSettings.bundleVersion} ({((long)summary.totalSize + bytes) / (1024f * 1024f):F1} MB, {summary.totalTime.TotalMinutes:F1} min)");
        }
        else
            Debug.LogError($"[Build] FAILED: {summary.result}, {summary.totalErrors} errors.");
    }

    private const string TilesRes = "Assets/Resources/Map/Tiles", TilesOut = "Assets/Maps/StreamTiles";
    private const string BundlesPath = @"A:\DexstandBundles";  // kept between builds: only changed tiles are rebuilt

    /// <summary>One bundle per tile prefab, plus "shared": the map's shaders, and every material and texture of the
    /// import folder that is not one tile's own orthophoto — without it each tile would carry its own copy.</summary>
    private static bool BuildTileBundles(BuildTarget target)
    {
        var builds = Directory.GetFiles(TilesOut, "*.prefab")
            .Select(p => p.Replace('\\', '/'))
            .Select(p => new AssetBundleBuild { assetBundleName = Path.GetFileNameWithoutExtension(p).ToLowerInvariant(), assetNames = new[] { p } })
            .ToList();
        var shared = AssetDatabase.FindAssets("t:Shader t:Material t:Texture", new[] { "Assets/Maps/Shaders", DexstandMapImport.Folder })
            .Select(AssetDatabase.GUIDToAssetPath).Distinct()
            .Where(p => !Path.GetFileName(p).StartsWith("ortho_") && !p.EndsWith(".fbx"))
            .ToArray();
        builds.Add(new AssetBundleBuild { assetBundleName = "shared", assetNames = shared });
        Directory.CreateDirectory(BundlesPath);
        var manifest = BuildPipeline.BuildAssetBundles(BundlesPath, builds.ToArray(), BuildAssetBundleOptions.ChunkBasedCompression, target);
        Debug.Log($"[Build] tile bundles: {builds.Count - 1} tiles + shared ({shared.Length} assets) -> {BundlesPath}");
        return manifest != null;
    }

    public static void BuildAndroid()
    {
        PlayerSettings.companyName = "Dexstand";
        PlayerSettings.productName = "Dexstand";
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.dexstand.shooter");
        StampVersion();

        // Landscape only: the touch layout puts the stick and buttons in corners.
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

        // Photon needs the network permission declared.
        PlayerSettings.Android.forceInternetPermission = true;

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

        // An APK can be side-loaded; an App Bundle cannot.
        EditorUserBuildSettings.buildAppBundle = false;

        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
        {
            Debug.Log("[Build] Switching to Android — this reimports assets and takes a while.");
            EditorUserBuildSettings.SwitchActiveBuildTarget(NamedBuildTarget.Android, BuildTarget.Android);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        Debug.Log("[Build] Starting Android build...");
        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"[Build] APK OK: {OutputPath} " +
                      $"({summary.totalSize / (1024f * 1024f):F1} MB, " +
                      $"{summary.totalTime.TotalMinutes:F1} min)");
        }
        else
        {
            Debug.LogError($"[Build] FAILED: {summary.result}, {summary.totalErrors} errors.");
            foreach (var step in report.steps)
            {
                foreach (var msg in step.messages.Where(m => m.type == LogType.Error))
                    Debug.LogError($"[Build] {step.name}: {msg.content}");
            }
        }
    }
}
