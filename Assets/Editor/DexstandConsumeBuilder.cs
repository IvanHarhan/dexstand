using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Brings the Blender-made drinking clip into the pack's arms. The FBX
/// (blender_tools/export_consume.py) carries the arms' skeleton with the clip,
/// and the bottle hanging off hand_r where it sits in the hand; PackViewmodel
/// reads that placement at runtime, so the FBX lives in Resources.
///
/// The clip is copied onto the pack's own bone paths and units, and played on a
/// layer of its own: the pack's Holster layer is additive, and this clip is a
/// whole pose, so it gets an override layer on top that is off until drinking.
/// </summary>
public static class DexstandConsumeBuilder
{
    public const string SourcePath = "Assets/Resources/Consume/A_FP_PCH_Consume_Beer.fbx";
    private const string ClipPath = "Assets/Animations/Consume/Consume_Beer.anim";
    private const string ControllerPath =
        "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Animators/Character/AC_LPSP_PCH.controller";
    private const string PackCharacterPath =
        "Assets/Infima Games/Low Poly Shooter Pack - Free Sample/Prefabs/P_LPSP_FP_CH.prefab";

    [MenuItem("Dexstand/Анимация пива")]
    public static void Build()
    {
        var importer = AssetImporter.GetAtPath(SourcePath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[Consume] No model at {SourcePath}.");
            return;
        }
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.importAnimation = true;
        importer.SaveAndReimport();

        var source = AssetDatabase.LoadAllAssetsAtPath(SourcePath).OfType<AnimationClip>()
                                  .FirstOrDefault(c => !c.name.StartsWith("__preview"));
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath);
        var pack = AssetDatabase.LoadAssetAtPath<GameObject>(PackCharacterPath);
        var animator = pack != null ? pack.GetComponentInChildren<Animator>(true) : null;
        if (source == null || model == null || animator == null)
        {
            Debug.LogError($"[Consume] Missing: clip {source != null}, model {model != null}, pack animator {animator != null}.");
            return;
        }

        // Bone name -> path under the pack's animator.
        var paths = new Dictionary<string, string>();
        foreach (var t in animator.GetComponentsInChildren<Transform>(true))
            if (!paths.ContainsKey(t.name)) paths[t.name] = AnimationUtility.CalculateTransformPath(t, animator.transform);

        // Blender and the pack disagree on units; a bone's length says by how much.
        var ours = Find(model, "lowerarm_r");
        var theirs = Find(animator.gameObject, "lowerarm_r");
        float unit = ours != null && theirs != null && ours.localPosition.magnitude > 1e-6f
            ? theirs.localPosition.magnitude / ours.localPosition.magnitude : 1f;

        var clip = new AnimationClip { frameRate = source.frameRate, name = "Consume_Beer" };
        int kept = 0, dropped = 0;
        foreach (var binding in AnimationUtility.GetCurveBindings(source))
        {
            string bone = binding.path.Split('/').Last();
            if (!paths.TryGetValue(bone, out var path) || binding.propertyName.StartsWith("m_LocalScale"))
            {
                dropped++;
                continue;
            }
            var curve = AnimationUtility.GetEditorCurve(source, binding);
            if (binding.propertyName.StartsWith("m_LocalPosition"))
            {
                var keys = curve.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    keys[i].value *= unit;
                    keys[i].inTangent *= unit;
                    keys[i].outTangent *= unit;
                }
                curve.keys = keys;
            }
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, binding.type, binding.propertyName), curve);
            kept++;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ClipPath));
        var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
        if (existing != null)
        {
            EditorUtility.CopySerialized(clip, existing);
            clip = existing;
        }
        else AssetDatabase.CreateAsset(clip, ClipPath);

        AddLayer(clip);
        AssetDatabase.SaveAssets();

        var hand = Find(model, "hand_r");
        var bottle = Find(model, "GARRAFA");
        Debug.Log($"[Consume] clip {source.length:F2}s, {kept} curves kept, {dropped} dropped, units x{unit:F4}; " +
                  $"bottle under hand_r: {(hand != null && bottle != null && bottle.IsChildOf(hand))}");
    }

    private static void AddLayer(AnimationClip clip)
    {
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        var layers = controller.layers;
        int index = System.Array.FindIndex(layers, l => l.name == PackViewmodel.ConsumeLayer);
        if (index < 0)
        {
            controller.AddLayer(PackViewmodel.ConsumeLayer);
            layers = controller.layers;
            index = layers.Length - 1;
            layers[index].blendingMode = AnimatorLayerBlendingMode.Override;
            layers[index].defaultWeight = 0f;
            controller.layers = layers;
        }

        var machine = controller.layers[index].stateMachine;
        if (machine.states.All(s => s.state.name != "Empty"))
            machine.defaultState = machine.AddState("Empty");
        var state = machine.states.Select(s => s.state).FirstOrDefault(s => s.name == PackViewmodel.ConsumeBeerState)
                    ?? machine.AddState(PackViewmodel.ConsumeBeerState);
        state.motion = clip;
        EditorUtility.SetDirty(controller);
    }

    private static Transform Find(GameObject root, string name) =>
        root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name);
}
