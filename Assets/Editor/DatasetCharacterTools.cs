using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Each model owns its texture/material folders: similarly named Mixamo materials
// must not remap to another person's atlas (e.g. Ch37 references Ch38_body).
public static class DatasetCharacterTools
{
    public static GameObject[] DistinctModels(IEnumerable<GameObject> models)
    {
        var result = new List<GameObject>();
        foreach (var model in models)
        {
            string name = System.Text.RegularExpressions.Regex.Replace(model.name, @"\(\d+\)$", "");
            bool duplicate = result.Any(previous => System.Text.RegularExpressions.Regex.Replace(previous.name, @"\(\d+\)$", "") == name && SameCharacter(previous, model));
            if (!duplicate) result.Add(model);
        }
        return result.ToArray();
    }
    internal static bool SameCharacter(GameObject a, GameObject b)
    {
        var first = a.GetComponentsInChildren<SkinnedMeshRenderer>().OrderBy(s => s.name).ToArray();
        var second = b.GetComponentsInChildren<SkinnedMeshRenderer>().OrderBy(s => s.name).ToArray();
        if (first.Length != second.Length) return false;
        for (int i = 0; i < first.Length; i++)
        {
            var x = first[i]; var y = second[i];
            if (x.name != y.name || !x.sharedMesh.vertices.SequenceEqual(y.sharedMesh.vertices) ||
                !x.sharedMesh.triangles.SequenceEqual(y.sharedMesh.triangles) || !x.sharedMesh.uv.SequenceEqual(y.sharedMesh.uv) ||
                !x.sharedMesh.bindposes.SequenceEqual(y.sharedMesh.bindposes) || !x.sharedMesh.boneWeights.SequenceEqual(y.sharedMesh.boneWeights) ||
                x.sharedMaterials.Length != y.sharedMaterials.Length) return false;
            for (int m = 0; m < x.sharedMaterials.Length; m++)
            {
                if (x.sharedMaterials[m].color != y.sharedMaterials[m].color) return false;
                string p = AssetDatabase.GetAssetPath(x.sharedMaterials[m].mainTexture), q = AssetDatabase.GetAssetPath(y.sharedMaterials[m].mainTexture);
                if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(q)) return false;
                if (p == q) continue;
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    if (!hash.ComputeHash(File.ReadAllBytes(p)).SequenceEqual(hash.ComputeHash(File.ReadAllBytes(q)))) return false;
            }
        }
        return true;
    }
    public static bool IsRunning { get; private set; }
    public static string Status { get; private set; } = "Idle";
    private static Queue<string> pending;
    private static readonly List<string> failures = new List<string>();
    private static int completed, total;
    public const string ReportDirectory = "DatasetOutput/Checks/CharacterDiversity_20261126";

    [MenuItem("Tools/Fight Dataset/Generator/Prepare Character Materials")]
    private static void PrepareMenu() => StartPreparation();

    public static string StartPreparation()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish the active task before importing character materials.");
        pending = new Queue<string>(AssetDatabase.FindAssets("t:Model", new[] { "Assets/Characters" }).Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal));
        total = pending.Count; completed = 0; failures.Clear(); IsRunning = true;
        Directory.CreateDirectory(ReportDirectory);
        EditorApplication.update += Tick;
        return "Preparing " + total + " character models";
    }
    private static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (pending.Count == 0)
        {
            IsRunning = false; EditorApplication.update -= Tick;
            Status = "Completed " + completed + "/" + total + "; failures=" + failures.Count;
            DatasetCatalogTools.Refresh();
            File.WriteAllText(ReportDirectory + "/material_preparation.json", JsonUtility.ToJson(new Report { completed = completed, total = total, errors = failures.ToArray() }, true));
            return;
        }
        string path = pending.Dequeue(); Status = "Preparing " + (completed + 1) + "/" + total + ": " + path;
        try { PrepareModel(path); completed++; }
        catch (Exception e) { failures.Add(path + ": " + e.Message); Debug.LogError("Character material setup failed: " + path + " " + e.Message); }
    }
    [Serializable] private sealed class Report { public int total, completed; public string[] errors; }

    public static void PrepareModel(string path)
    {
        if (!path.StartsWith("Assets/Characters/", StringComparison.Ordinal) || !(AssetImporter.GetAtPath(path) is ModelImporter importer))
            throw new InvalidOperationException("Expected a character FBX.");
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        var materials = model.GetComponentsInChildren<SkinnedMeshRenderer>().SelectMany(s => s.sharedMaterials).Where(m => m).Distinct().ToArray();
        if (materials.All(m => m.shader && m.shader.name.StartsWith("Universal Render Pipeline/") && m.mainTexture)) return;
        string stem = Path.GetFileNameWithoutExtension(path), textureFolder = "Assets/Characters/" + stem + "_Textures";
        string materialFolder = "Assets/Characters/" + stem + "_Materials";
        DatasetCatalogTools.EnsureFolder(textureFolder); DatasetCatalogTools.EnsureFolder(materialFolder);
        if (!AssetDatabase.FindAssets("t:Texture", new[] { textureFolder }).Any())
        {
            importer.ExtractTextures(textureFolder);
            AssetDatabase.Refresh();
            importer.SaveAndReimport();
        }
        importer = (ModelImporter)AssetImporter.GetAtPath(path);
        var textures = AssetDatabase.FindAssets("t:Texture", new[] { textureFolder }).Select(AssetDatabase.GUIDToAssetPath)
            .Select(AssetDatabase.LoadAssetAtPath<Texture2D>).Where(t => t).ToArray();
        var diffuse = textures.Where(t => t.name.IndexOf("diffuse", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("albedo", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        if (diffuse.Length == 0) throw new InvalidOperationException("No embedded diffuse textures were found. Supply the original texture files.");
        var sourceMaps = DatasetFbxMaterials.Read(path);
        var originals = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().ToArray();
        if (originals.Length == 0) throw new InvalidOperationException("No embedded material definitions.");
        foreach (var source in originals)
        {
            Texture main = source.mainTexture;
            if (sourceMaps.TryGetValue(source.name, out var slots) && slots.TryGetValue("DiffuseColor", out string sourceFile))
                main = textures.FirstOrDefault(t => Path.GetFileName(AssetDatabase.GetAssetPath(t)) == sourceFile) ?? main;
            if (!main)
            {
                if (diffuse.Length == 1) main = diffuse[0];
                else
                {
                    throw new InvalidOperationException("Cannot resolve the source FBX diffuse connection for " + source.name + ". Refusing to guess among multiple atlases.");
                }
            }
            string materialPath = materialFolder + "/" + source.name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (!material)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = source.name };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            material.SetTexture("_BaseMap", main); material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Smoothness", .2f); material.SetFloat("_Metallic", 0);
            string texStem = main.name.Replace("Diffuse", "", StringComparison.OrdinalIgnoreCase).Replace("Albedo", "", StringComparison.OrdinalIgnoreCase).TrimEnd('_');
            var normal = textures.FirstOrDefault(t => t.name.StartsWith(texStem, StringComparison.OrdinalIgnoreCase) && t.name.IndexOf("normal", StringComparison.OrdinalIgnoreCase) >= 0);
            if (normal)
            {
                var ti = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(normal));
                if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); }
                material.SetTexture("_BumpMap", normal); material.EnableKeyword("_NORMALMAP");
            }
            bool cutout = source.name.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0 || source.name.IndexOf("lash", StringComparison.OrdinalIgnoreCase) >= 0;
            if (cutout)
            {
                material.SetFloat("_AlphaClip", 1); material.SetFloat("_Cutoff", .35f); material.SetFloat("_Cull", 0);
                material.EnableKeyword("_ALPHATEST_ON"); material.renderQueue = 2450; material.SetOverrideTag("RenderType", "TransparentCutout");
            }
            EditorUtility.SetDirty(material);
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), source.name), material);
        }
        AssetDatabase.SaveAssets(); importer.SaveAndReimport();
    }
}
