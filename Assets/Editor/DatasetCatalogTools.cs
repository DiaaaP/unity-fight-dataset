using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEngine;

public static class DatasetCatalogTools
{
    public const string CatalogPath = "Assets/Config/AnimationCatalog.asset";
    public const string ConfigPath = "Assets/Config/DatasetGeneratorConfig.asset";
    public static event Action CatalogChanged;
    private static bool queued, rebuilding;

    [MenuItem("Tools/Fight Dataset/Generator/1 Refresh Animation Catalog")]
    public static void RefreshMenu() => Refresh();

    public static AnimationCatalog Refresh()
    {
        if (rebuilding) return AssetDatabase.LoadAssetAtPath<AnimationCatalog>(CatalogPath);
        rebuilding = true;
        try
        {
            foreach (string folder in new[] { "Assets/Config", "Assets/Animations/Inbox", "Assets/Animations/Reactions/Block",
                "Assets/Animations/Reactions/Dodge", "Assets/Animations/Reactions/Stumble", "Assets/Animations/Reactions/Fall",
                "Assets/Animations/Reactions/GetUp" }) EnsureFolder(folder);
            var catalog = AssetDatabase.LoadAssetAtPath<AnimationCatalog>(CatalogPath);
            if (!catalog) { catalog = ScriptableObject.CreateInstance<AnimationCatalog>(); AssetDatabase.CreateAsset(catalog, CatalogPath); }
            var previous = catalog.clips.Where(c => !string.IsNullOrEmpty(c.key)).GroupBy(c => c.key).ToDictionary(g => g.Key, g => g.First());
            var entries = new List<CatalogClip>();
            var warnings = new List<string>();
            var paths = AssetDatabase.FindAssets("", new[] { "Assets/Animations" }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                .Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
            foreach (string path in paths)
            {
                EnsureMotionImport(path);
                foreach (var clip in AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                    .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal)).OrderBy(c => c.name, StringComparer.Ordinal))
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long localId);
                    string key = guid + ":" + localId;
                    var entry = previous.TryGetValue(key, out var saved) ? saved : new CatalogClip { key = key };
                    entry.clip = clip;
                    entry.durationSeconds = clip.length;
                    entry.assetPath = path;
                    entry.displayName = Path.GetFileNameWithoutExtension(path) + (clip.name == "mixamo.com" ? "" : "/" + clip.name);
                    if (!entry.manualClassification)
                    {
                        entry.kind = Classify(path + " " + clip.name, out string source);
                        entry.classificationSource = source;
                    }
                    if (!clip.isHumanMotion || clip.length <= 0.1f) warnings.Add(entry.displayName + ": no usable Humanoid motion.");
                    if (entry.kind == ClipKind.Unknown) warnings.Add(entry.displayName + ": unknown category; choose Kind and Manual Classification in catalog.");
                    if ((entry.kind == ClipKind.Fall && !entry.reviewedFall) || entry.kind == ClipKind.GetUp)
                        warnings.Add(entry.displayName + ": registered but excluded from automatic scenarios until floor/transition review.");
                    entries.Add(entry);
                }
            }
            catalog.clips = entries;
            catalog.characters = AssetDatabase.FindAssets("t:Model", new[] { "Assets/Characters" })
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>).Where(go => go && go.GetComponentsInChildren<SkinnedMeshRenderer>().Length > 0)
                .Where(go => { var avatar = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(go)).OfType<Avatar>().FirstOrDefault();
                    if (avatar && avatar.isHuman && avatar.isValid) return true;
                    warnings.Add(go.name + ": invalid Humanoid avatar; reimport the model."); return false; }).ToList();
            foreach (var kind in new[] { ClipKind.Idle, ClipKind.Punch, ClipKind.Kick, ClipKind.HitReaction, ClipKind.Block, ClipKind.Dodge })
                if (!entries.Any(e => Usable(e) && e.kind == kind)) warnings.Add("Missing " + kind + "; dependent scenarios are skipped or use documented available alternatives.");
            if (catalog.characters.Count == 0) warnings.Add("No compatible character models.");
            catalog.warnings = warnings.ToArray();
            EditorUtility.SetDirty(catalog);
            var config = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(ConfigPath);
            if (!config) { config = ScriptableObject.CreateInstance<DatasetGeneratorConfig>(); config.catalog = catalog; AssetDatabase.CreateAsset(config, ConfigPath); }
            AssetDatabase.SaveAssets();
            CatalogChanged?.Invoke();
            return catalog;
        }
        finally { rebuilding = false; }
    }

    public static bool Usable(CatalogClip entry) => entry.enabled && entry.clip && entry.clip.isHumanMotion && entry.clip.length > 0.1f && entry.kind != ClipKind.Unknown;

    public static ClipKind Classify(string value, out string source)
    {
        string text = value.Replace('\\', '/').ToLowerInvariant();
        if (text.Contains("/attacks/punch/")) { source = "folder"; return ClipKind.Punch; }
        if (text.Contains("/attacks/kick/")) { source = "folder"; return ClipKind.Kick; }
        source = "name_or_reaction_subfolder";
        if (text.Contains("getup") || text.Contains("get up") || text.Contains("getting up")) return ClipKind.GetUp;
        if (text.Contains("block")) return ClipKind.Block;
        if (text.Contains("dodge") || text.Contains("evad")) return ClipKind.Dodge;
        if (text.Contains("fall") || text.Contains("knockdown")) return ClipKind.Fall;
        if (text.Contains("stumble")) return ClipKind.Stumble;
        if (text.Contains("/reactions/") || text.Contains("hit reaction") || text.Contains("hit to") || text.Contains("hitreaction")) return ClipKind.HitReaction;
        if (text.Contains("punch") || text.Contains("jab") || text.Contains("uppercut")) return ClipKind.Punch;
        if (text.Contains("kick")) return ClipKind.Kick;
        if (text.Contains("idle")) return ClipKind.Idle;
        if (text.Contains("/neutral/") || text.Contains("walk") || text.Contains("talk") || text.Contains("greet")) return ClipKind.Neutral;
        source = "unrecognized";
        return ClipKind.Unknown;
    }

    public static void QueueRefresh()
    {
        if (queued) return;
        queued = true;
        EditorApplication.delayCall += () =>
        {
            queued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            try { Refresh(); } catch (Exception exception) { Debug.LogError("Catalog refresh: " + exception.Message); }
        };
    }

    // Existing FBXs can predate the postprocessor. Apply the same import policy
    // during refresh: manual pose sampling must retain authored translation and
    // rotation in the skeleton instead of silently discarding extracted motion.
    private static void EnsureMotionImport(string path)
    {
        if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) return;
        var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
        bool changed = false;
        foreach (var clip in clips)
        {
            bool loop = (path + clip.name).IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("/Neutral/", StringComparison.OrdinalIgnoreCase) >= 0;
            if (clip.lockRootHeightY && clip.keepOriginalPositionY && !clip.heightFromFeet &&
                clip.lockRootPositionXZ && clip.keepOriginalPositionXZ && clip.lockRootRotation && clip.keepOriginalOrientation && clip.loopTime == loop) continue;
            clip.lockRootHeightY = clip.keepOriginalPositionY = true;
            clip.heightFromFeet = false;
            clip.lockRootPositionXZ = clip.keepOriginalPositionXZ = true;
            clip.lockRootRotation = clip.keepOriginalOrientation = true;
            clip.loopTime = loop;
            changed = true;
        }
        if (!changed) return;
        importer.clipAnimations = clips;
        importer.SaveAndReimport();
    }

    public static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        EnsureFolder(path.Substring(0, slash));
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}

public sealed class DatasetModelImporter : AssetPostprocessor
{
    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith("Assets/Animations/", StringComparison.OrdinalIgnoreCase) &&
            !assetPath.StartsWith("Assets/Characters/", StringComparison.OrdinalIgnoreCase)) return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        if (!assetPath.StartsWith("Assets/Animations/", StringComparison.OrdinalIgnoreCase)) return;
        var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            string name = (assetPath + clip.name).ToLowerInvariant();
            clip.loopTime = name.Contains("idle") || assetPath.Contains("/Neutral/");
            clip.lockRootRotation = clip.keepOriginalOrientation = true;
            clip.lockRootPositionXZ = clip.keepOriginalPositionXZ = true;
            clip.lockRootHeightY = clip.keepOriginalPositionY = true;
        }
        importer.clipAnimations = clips;
    }

    private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(p =>
            (p.StartsWith("Assets/Animations/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Assets/Characters/", StringComparison.OrdinalIgnoreCase)) &&
            (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)))) DatasetCatalogTools.QueueRefresh();
    }
}
