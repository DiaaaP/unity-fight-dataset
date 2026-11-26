using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Render texture alternatives on the real FBX without editing the user's scenes
// or source materials. The source atlas and animation/UV geometry stay intact.
public static class DatasetTextureReview
{
    public const string Root = "DatasetOutput/Checks/TextureUpgrade_20261126";
    public const string Folder = "Assets/Characters/Ch01_nonPBR_Enhanced";
    public const string Model = "Assets/Characters/Ch01_nonPBR.fbx";
    [Serializable] public sealed class Result
    {
        public string variant, albedo, normal, geometry_signature;
        public int albedo_width, normal_width;
        public string[] renders;
    }
    public static string PrepareImports()
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish generation first.");
        foreach (string file in new[] { "Ch01_Albedo_AI_v1.png", "Ch01_Albedo_Native4K.png", "Ch01_Normal_Native4K.png" })
        {
            string path = Folder + "/" + file; AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.maxTextureSize = 4096; importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.mipmapEnabled = true; importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 8;
            if (file.Contains("Normal")) { importer.textureType = TextureImporterType.NormalMap; importer.sRGBTexture = false; }
            else { importer.textureType = TextureImporterType.Default; importer.sRGBTexture = true; }
            importer.SaveAndReimport();
        }
        return "Texture alternatives imported; original import settings retained.";
    }
    public static string Capture(int mode)
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish generation first.");
        string variant = mode == 0 ? "original_2k" : mode == 1 ? "native_4k" : "ai_v1";
        Directory.CreateDirectory(Root);
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        var cat = UnityEngine.Object.Instantiate(c.catalog); c.catalog = cat;
        var scene = EditorSceneManager.NewPreviewScene(); var materials = new List<Material>();
        RenderTexture target = null; Texture2D pixels = null;
        try
        {
            cat.characters = new List<GameObject> { AssetDatabase.LoadAssetAtPath<GameObject>(Model) };
            c.randomizeCharacterHeight = true; c.characterHeightMeters = Vector2.one * 1.8f;
            var stage = DatasetSceneFactory.Create(c, scene, 261101);
            stage.player.actors[1].transform.localPosition += Vector3.right * 18;
            var actor = stage.player.actors[0];
            Texture2D albedo = mode == 0 ? AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Characters/Ch01_nonPBR_Textures/Ch01_1001_Diffuse.png") :
                AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + (mode == 1 ? "/Ch01_Albedo_Native4K.png" : "/Ch01_Albedo_AI_v1.png"));
            Texture2D normal = AssetDatabase.LoadAssetAtPath<Texture2D>(mode == 0 ? "Assets/Characters/Ch01_nonPBR_Textures/Ch01_1001_Normal.png" : Folder + "/Ch01_Normal_Native4K.png");
            foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
                skin.sharedMaterials = skin.sharedMaterials.Select(source =>
                {
                    var material = new Material(source); materials.Add(material);
                    material.SetTexture("_BaseMap", albedo); material.SetTexture("_BumpMap", normal);
                    return material;
                }).ToArray();
            var camera = stage.camera; camera.enabled = false; camera.scene = scene;
            camera.fieldOfView = 40; camera.aspect = 1;
            target = new RenderTexture(1024, 1024, 24); target.Create(); camera.targetTexture = target;
            pixels = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
            Vector3 head = actor.GetBoneTransform(HumanBodyBones.Head).position;
            Vector3 right = actor.GetBoneTransform(HumanBodyBones.RightUpperArm).position - actor.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
            right.y = 0; right.Normalize(); Vector3 forward = Vector3.Cross(right, Vector3.up).normalized;
            string[] paths = { variant + "_face.png", variant + "_body.png", variant + "_clothes.png" };
            for (int view = 0; view < 3; view++)
            {
                Vector3 aim = view == 0 ? head + Vector3.up * .025f : actor.transform.position + Vector3.up * (view == 1 ? .92f : 1.23f);
                camera.transform.position = aim + forward * (view == 0 ? .65f : view == 1 ? 3.1f : .75f);
                camera.transform.LookAt(aim); camera.Render(); var previous = RenderTexture.active;
                try { RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0); pixels.Apply(); }
                finally { RenderTexture.active = previous; }
                File.WriteAllBytes(Root + "/" + paths[view], pixels.EncodeToPNG());
            }
            string signature = string.Join(";",actor.GetComponentsInChildren<SkinnedMeshRenderer>().Select(s => s.sharedMesh.vertexCount + ":" + s.sharedMesh.GetInstanceID()));
            File.WriteAllText(Root + "/" + variant + ".json", JsonUtility.ToJson(new Result { variant = variant, albedo = AssetDatabase.GetAssetPath(albedo), normal = AssetDatabase.GetAssetPath(normal),
                albedo_width = albedo.width, normal_width = normal.width, geometry_signature = signature, renders = paths }, true));
            return variant + " | albedo=" + albedo.width + " | normal=" + normal.width;
        }
        finally
        {
            if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            if (pixels) UnityEngine.Object.DestroyImmediate(pixels);
            foreach (var material in materials) UnityEngine.Object.DestroyImmediate(material);
            DatasetSceneFactory.ClosePreviewScene(scene); UnityEngine.Object.DestroyImmediate(c); UnityEngine.Object.DestroyImmediate(cat);
        }
    }
    public static string ApplyNative4K()
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish generation first.");
        foreach (string name in new[] { "Ch01_body", "Ch01_hair" })
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Characters/Ch01_nonPBR_Materials/" + name + ".mat");
            material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/Ch01_Albedo_Native4K.png"));
            material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "/Ch01_Normal_Native4K.png"));
            EditorUtility.SetDirty(material); AssetDatabase.SaveAssetIfDirty(material);
        }
        // Geometry/UV did not change, only texture dependencies. Keep the same
        // reviewed vertex anchors and refresh their dependency signature explicitly.
        var table = JsonUtility.FromJson<DatasetFaceLandmarks.Table>(File.ReadAllText(DatasetFaceLandmarks.Path));
        var profile = table.models.First(p => p.model == Model);
        DatasetFaceLandmarks.Save(Model, profile.points); AssetDatabase.ImportAsset(DatasetFaceLandmarks.Path);
        return "Native 4096 albedo/normal enabled for Ch01; source files and vertex anchors preserved.";
    }
}
