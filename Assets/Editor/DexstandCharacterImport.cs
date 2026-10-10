using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Brings a Mixamo character into the project with its own skin.
///
/// Mixamo ships a character as a single FBX with the textures embedded inside
/// it, so a plain import leaves a rigged but grey mesh: the maps exist in the
/// file but no asset points at them. Unity can only pull them out in one order
/// — rig, then textures, then materials — and each step needs its own reimport,
/// which is what this does. Untextured characters are the whole reason this
/// exists; see the arms, which never got a usable skin out of the Infima pack.
/// </summary>
public static class DexstandCharacterImport
{
    public const string ModelPath = "Assets/Models/Character/GasMask/GasMask.fbx";

    private const string Folder = "Assets/Models/Character/GasMask";
    private const string TextureFolder = Folder + "/Textures";

    // Mixamo's maps arrive at 4096 and there are several of them. The phone
    // build is already 74 MB and nothing on a handset resolves that, so they
    // are clamped on import rather than left to bloat the APK.
    private const int MaxTextureSize = 2048;

    public static void ImportCharacter()
    {
        AssetDatabase.Refresh();

        var importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"[Character] No model at {ModelPath}.");
            return;
        }

        // Humanoid. The character's clips are downloaded as humanoid motion and
        // replayed through the avatar, so a Generic rig resolves none of them and
        // the body stands in its T-pose — which is exactly what re-importing the
        // character used to do to it, silently, every time.
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = false; // the character file carries no clips

        // ImportViaMaterialDescription is the pipeline-aware path: it reads what
        // the FBX says each material is and writes a URP Lit material. The older
        // ImportStandard path writes Standard-shader materials, which render
        // magenta under URP.
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;

        // Seven of these are on screen at once on a phone — the player and six
        // patrols, 30k vertices each. Two bones per vertex is the usual mobile
        // setting and halves the skinning work; the difference shows only where
        // three bones meet, which on this character is inside the elbow.
        importer.maxBonesPerVertex = 2;
        importer.skinWeights = ModelImporterSkinWeights.Custom;
        importer.meshCompression = ModelImporterMeshCompression.Medium;
        importer.optimizeMeshVertices = true;

        importer.SaveAndReimport();

        EnsureFolder(TextureFolder);

        importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer != null && importer.ExtractTextures(TextureFolder))
            Debug.Log($"[Character] Embedded textures extracted to {TextureFolder}.");
        else
            Debug.LogWarning("[Character] ExtractTextures found nothing — " +
                             "the FBX may reference its maps instead of carrying them.");

        AssetDatabase.Refresh();
        ClampTextures();

        // External materials land in a Materials folder beside the model, where
        // they can be edited; while they live inside the FBX nothing can touch
        // them.
        importer = AssetImporter.GetAtPath(ModelPath) as ModelImporter;
        if (importer != null)
        {
            importer.materialLocation = ModelImporterMaterialLocation.External;
            importer.SaveAndReimport();
        }

        AssetDatabase.Refresh();
        LinkMaps();
        Report();
    }

    /// <summary>
    /// Hooks each material up to its own normal map and gives it a sane
    /// roughness.
    ///
    /// Unity's material description import binds the diffuse map and stops
    /// there: the normal, specular and glossiness maps Mixamo ships arrive as
    /// loose textures nothing points at, and without the normal map the
    /// character reads as a smooth plastic doll — no fabric, no straps, no
    /// mask seams. The names line up by index (Ch35_1001_Diffuse ->
    /// Ch35_1001_Normal), which is what the pairing goes on.
    /// </summary>
    private static void LinkMaps()
    {
        int linked = 0;
        int cleared = 0;
        int rebound = 0;

        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { Folder + "/Materials" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            // Only when the name really carries "Diffuse". Without this guard the
            // replacement is a no-op, the path points back at the material's own
            // colour map, and every material gets its diffuse bound as a normal
            // map — which lights the character off a texture that encodes no
            // directions at all. Mixamo names hit the rename; MPFB ones do not.
            string normalPath = mat.name.Contains("Diffuse")
                ? $"{TextureFolder}/{mat.name.Replace("Diffuse", "Normal")}.png"
                : null;
            var normal = normalPath == null
                ? null
                : AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);

            // A base map that resolves to nothing renders the whole material
            // white, and URP gives no hint why. It happens whenever Textures/ is
            // rebuilt: the extracted files get fresh GUIDs and every material
            // still points at the old ones. The extracted texture carries the
            // material's own name, so rebinding by name puts it back.
            if (mat.HasProperty("_BaseMap") && mat.GetTexture("_BaseMap") == null)
            {
                var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/{mat.name}.png")
                             ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/{mat.name}.jpg")
                             ?? AssetDatabase.LoadAssetAtPath<Texture2D>($"{TextureFolder}/{mat.name}.PNG");
                if (albedo != null)
                {
                    mat.SetTexture("_BaseMap", albedo);
                    if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);
                    rebound++;
                }
            }

            if (normal != null && mat.HasProperty("_BumpMap"))
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
                linked++;
            }
            else if (normal == null && mat.HasProperty("_BumpMap")
                     && mat.GetTexture("_BumpMap") != null)
            {
                // Re-importing does not rewrite materials that already exist, so a
                // wrong bump map set by an earlier run outlives the fix that stops
                // new ones. Clearing it here is what actually undoes the damage.
                mat.SetTexture("_BumpMap", null);
                mat.DisableKeyword("_NORMALMAP");
                cleared++;
            }

            // Kit, webbing and rubber, none of which shine. The specular and
            // glossiness maps stay on disk: URP Lit wants a metallic/smoothness
            // pair, and feeding it a specular map as if it were one makes the
            // whole character look wet.
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0.18f);

            EditorUtility.SetDirty(mat);
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[Character] {linked} material(s) linked to their normal map, " +
                  $"{cleared} had a bogus one removed, {rebound} got their base map back.");
    }

    /// <summary>
    /// Caps the extracted maps and marks the normal maps as such — extracted
    /// textures come out as plain colour textures, and a normal map read as
    /// colour lights the character inside out.
    /// </summary>
    private static void ClampTextures()
    {
        int clamped = 0, normals = 0;

        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TextureFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;

            bool changed = false;
            if (ti.maxTextureSize > MaxTextureSize)
            {
                ti.maxTextureSize = MaxTextureSize;
                changed = true;
                clamped++;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            bool looksNormal = name.Contains("normal") || name.EndsWith("_n") || name.Contains("_nrm");
            if (looksNormal && ti.textureType != TextureImporterType.NormalMap)
            {
                ti.textureType = TextureImporterType.NormalMap;
                changed = true;
                normals++;
            }

            if (changed) ti.SaveAndReimport();
        }

        Debug.Log($"[Character] Textures: {clamped} clamped to {MaxTextureSize}, " +
                  $"{normals} marked as normal maps.");
    }

    /// <summary>
    /// Prints what actually arrived: size, renderers, the material each slot
    /// ended up with and whether it has a map, plus the root and head bone
    /// names — the head bone is what PlayerSetup shrinks so the local player
    /// does not look at the inside of their own mask.
    /// </summary>
    private static void Report()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null)
        {
            Debug.LogError("[Character] Model failed to load after import.");
            return;
        }

        var instance = Object.Instantiate(model);
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[Character] {model.name} imported.");

            var bounds = new Bounds();
            bool first = true;
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);

                foreach (var m in r.sharedMaterials)
                {
                    string map = "no map";
                    if (m != null && m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null)
                        map = m.GetTexture("_BaseMap").name;
                    else if (m != null && m.HasProperty("_MainTex") && m.GetTexture("_MainTex") != null)
                        map = m.GetTexture("_MainTex").name;

                    sb.AppendLine($"  {r.name} -> {(m == null ? "NULL" : m.name)} " +
                                  $"({(m == null ? "-" : m.shader.name)}, {map})");
                }
            }

            sb.AppendLine($"  {renderers.Length} renderer(s), size {bounds.size}");

            Transform head = null, root = null;
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                if (root == null && t.name.ToLowerInvariant().Contains("hips")) root = t;
                if (head == null && t.name.EndsWith("Head", System.StringComparison.OrdinalIgnoreCase))
                    head = t;
            }

            sb.AppendLine($"  hips bone: {(root == null ? "NOT FOUND" : root.name)}");
            sb.AppendLine($"  head bone: {(head == null ? "NOT FOUND" : head.name)}");

            var avatar = DexstandCharacterBuilder.LoadAvatar(ModelPath);
            sb.AppendLine($"  avatar: {(avatar == null ? "MISSING" : avatar.name)}");

            Debug.Log(sb.ToString());
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;

        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        string leaf = System.IO.Path.GetFileName(path);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
