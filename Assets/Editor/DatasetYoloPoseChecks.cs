using System;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetYoloPoseChecks
{
    public const string Root = "DatasetOutput/Checks/YoloPose_20261118";
    [Serializable] public sealed class Probe { public DatasetYoloPose.FaceAnchor[] anchors; public Vector3[] points; public string[] models; }
    public static string Geometry()
    {
        Directory.CreateDirectory(Root);
        var c = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        var scene = EditorSceneManager.NewPreviewScene();
        var meshes = new System.Collections.Generic.List<Mesh>();
        Texture2D pixels = null; RenderTexture target = null;
        try
        {
            var stage = DatasetSceneFactory.Create(c, scene, 114856200);
            stage.player.actors[0].transform.position += Vector3.left * .6f;
            stage.player.actors[1].transform.position += Vector3.right * .6f;
            var blocker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            blocker.name = "Visibility probe"; blocker.transform.SetParent(stage.root.transform, false);
            blocker.transform.localPosition = new Vector3(0, 1.2f, 1.5f);
            blocker.transform.localScale = new Vector3(6, 4, .15f);
            var pose = new DatasetYoloPose(stage);
            blocker.SetActive(false);
            foreach (var actor in stage.player.actors)
                foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var mesh = new Mesh(); meshes.Add(mesh); skin.BakeMesh(mesh, true); pose.Bind(skin, mesh);
                }
            pose.Sample();
            var camera = stage.camera; camera.enabled = false; camera.scene = scene;
            camera.aspect = 1; camera.fieldOfView = 40;
            target = new RenderTexture(768, 768, 24); target.Create(); camera.targetTexture = target;
            pixels = new Texture2D(768, 768, TextureFormat.RGBA32, false);
            Vector3 center = default;
            for (int view = 0; view < 8; view++)
            {
                int a = view / 4, angle = view % 4;
                center = stage.player.actors[a].GetBoneTransform(HumanBodyBones.Head).position;
                Vector3 right = stage.player.actors[a].GetBoneTransform(HumanBodyBones.RightUpperArm).position - stage.player.actors[a].GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
                right.y = 0; right.Normalize(); Vector3 forward = Vector3.Cross(right, Vector3.up).normalized;
                camera.transform.position = center + (angle == 0 ? forward : angle == 1 ? -forward : angle == 2 ? right : Vector3.up + forward * .1f) * 2.2f;
                camera.transform.LookAt(center + Vector3.down * .2f);
                if (angle == 0 && !pose.Visible(camera, a, 0, pose.Point(a, 0))) throw new Exception("Unoccluded nose was not visible.");
                if (angle == 1 && pose.Visible(camera, a, 0, pose.Point(a, 0))) throw new Exception("Back of head did not occlude the nose.");
                Save("neutral_" + view);
            }
            // Other-person occlusion, using skinned triangles, without colliders.
            center = stage.player.actors[0].GetBoneTransform(HumanBodyBones.Head).position;
            camera.transform.position = center + Vector3.forward * 2.7f; camera.transform.LookAt(center);
            Vector3 original = stage.player.actors[1].transform.position;
            stage.player.actors[1].transform.position = stage.player.actors[0].transform.position + Vector3.forward * .45f;
            pose.Sample();
            if (pose.Visible(camera, 0, 0, pose.Point(0, 0))) throw new Exception("Another actor did not occlude the nose.");
            stage.player.actors[1].transform.position = original; pose.Sample();
            // A large opaque plane between camera and every joint must mark all
            // in-frame points as occluded, without erasing their coordinates.
            camera.transform.position = center + Vector3.forward * 2.7f; camera.transform.LookAt(center);
            blocker.SetActive(true);
            string blocked = pose.Labels(camera); File.WriteAllText(Root + "/blocked.txt", blocked);
            if (blocked.Split('\n').Where(s => s.Length > 0).SelectMany(s => s.Split(' ').Skip(5).Where((v, i) => i % 3 == 2)).Any(v => v == "2"))
                throw new Exception("Opaque occluder was ignored.");
            blocker.SetActive(false);
            File.WriteAllText(Root + "/geometry.json", JsonUtility.ToJson(new Probe { anchors = pose.faceAnchors, models = stage.modelPaths,
                points = Enumerable.Range(0, 2).SelectMany(a => Enumerable.Range(0, 17).Select(k => pose.Point(a, k))).ToArray() }, true));
            return Root;
            void Save(string stem)
            {
                camera.Render(); var previous = RenderTexture.active;
                try { RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); pixels.Apply(); }
                finally { RenderTexture.active = previous; }
                File.WriteAllBytes(Root + "/" + stem + ".png", pixels.EncodeToPNG());
                File.WriteAllText(Root + "/" + stem + ".txt", pose.Labels(camera));
            }
        }
        finally
        {
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
            if (pixels) UnityEngine.Object.DestroyImmediate(pixels);
            if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            DatasetSceneFactory.ClosePreviewScene(scene);
        }
    }
    public static string Start(bool asyncReadback, bool kick = false, int views = 2)
    {
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        c.catalog = UnityEngine.Object.Instantiate(c.catalog);
        // Instantiate ScriptableObject copies the managed list shallowly. Clone
        // entries too so a probe cannot toggle the user's animation selection.
        c.catalog.clips = c.catalog.clips.Select(v => JsonUtility.FromJson<CatalogClip>(JsonUtility.ToJson(v))).ToList();
        for (int i = 0; i < c.catalog.clips.Count; i++) c.catalog.clips[i].clip = AssetDatabase.LoadAllAssetsAtPath(c.catalog.clips[i].assetPath).OfType<AnimationClip>().FirstOrDefault(a =>
        { AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string guid, out long local); return guid + ":" + local == c.catalog.clips[i].key; });
        try
        {
            foreach (var entry in c.catalog.clips)
                if (entry.kind == ClipKind.Punch || entry.kind == ClipKind.Kick) entry.enabled = entry.assetPath.EndsWith(kick ? "/Kick_5.fbx" : "/Punch_9.fbx", StringComparison.OrdinalIgnoreCase);
            c.scenariosPerCategory = 1; c.generateHit = !kick; c.generateKick = kick; c.generateFight = c.generateNormal = false;
            c.speedRange = Vector2.one; c.seed = kick ? 99454 : 202401; c.cameraViews = views;
            c.width = 640; c.height = 360; c.fps = 25; c.saveInspectionFrames = true;
            c.exportYoloPose = true; c.useAsyncGPUReadback = asyncReadback;
            c.outputDirectory = Root + (asyncReadback ? "/async" : "/sync");
            return DatasetBatchExporter.StartBatch(settings: c);
        }
        finally { UnityEngine.Object.DestroyImmediate(c.catalog); UnityEngine.Object.DestroyImmediate(c); }
    }

    public static string AlphaOcclusion()
    {
        var c = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        var scene = EditorSceneManager.NewPreviewScene();
        Material material = null; Texture2D texture = null;
        try
        {
            var stage = DatasetSceneFactory.Create(c, scene, 114856200);
            stage.player.actors[1].transform.position += Vector3.right * 2;
            var head = stage.player.actors[0].GetBoneTransform(HumanBodyBones.Head).position;
            stage.camera.transform.position = head + Vector3.forward * 2.5f; stage.camera.transform.LookAt(head);
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.transform.SetParent(stage.root.transform, false);
            cube.transform.position = head + Vector3.forward; cube.transform.localScale = new Vector3(2, 2, .1f);
            material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.SetFloat("_AlphaClip", 1); material.SetFloat("_Cutoff", .5f); material.EnableKeyword("_ALPHATEST_ON");
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixels(Enumerable.Repeat(Color.clear, 4).ToArray()); texture.Apply(); material.mainTexture = texture;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            var transparent = new DatasetYoloPose(stage);
            if (!transparent.Visible(stage.camera, 0, 0, transparent.Point(0, 0))) throw new Exception("Alpha-cutout hole incorrectly occludes nose.");
            texture.SetPixels(Enumerable.Repeat(Color.white, 4).ToArray()); texture.Apply();
            var opaque = new DatasetYoloPose(stage);
            if (opaque.Visible(stage.camera, 0, 0, opaque.Point(0, 0))) throw new Exception("Opaque alpha-cutout texel was ignored.");
            File.WriteAllText(Root + "/alpha_check.json", "{\"transparent_hole_visible\":true,\"opaque_texel_occluded\":true}");
            return "alpha cutout passed";
        }
        finally
        {
            if (material) UnityEngine.Object.DestroyImmediate(material);
            if (texture) UnityEngine.Object.DestroyImmediate(texture);
            DatasetSceneFactory.ClosePreviewScene(scene);
        }
    }
    public static string Trace()
    {
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        c.catalog = UnityEngine.Object.Instantiate(c.catalog);
        c.catalog.clips = c.catalog.clips.Where(e => e.kind != ClipKind.Kick && (e.kind != ClipKind.Punch || e.assetPath.EndsWith("/Punch_9.fbx"))).ToList();
        c.speedRange = Vector2.one; c.seed = 202401; c.cameraViews = 2; c.width = 640; c.height = 360; c.fps = 25;
        var scene = EditorSceneManager.NewPreviewScene(); var meshes = new System.Collections.Generic.List<Mesh>();
        try
        {
            var stage = DatasetSceneFactory.Create(c, scene, c.seed); var pose = new DatasetYoloPose(stage);
            stage.player.plan = DatasetScenarioPlanner.Create(c, stage, "hit", c.seed);
            var views = DatasetScenarioPlanner.Cameras(c, stage, c.seed);
            var skins = stage.player.actors.SelectMany(a => a.GetComponentsInChildren<SkinnedMeshRenderer>()).ToArray();
            foreach (var skin in skins) { var mesh = new Mesh(); meshes.Add(mesh); pose.Bind(skin, mesh); }
            var lines = new System.Collections.Generic.List<string>();
            foreach (int frame in new[] { 2, 26, 27, 32, 36, 38, 41, 73, 78 })
            {
                stage.player.SampleAtTime(frame / 25f);
                for (int i = 0; i < skins.Length; i++) skins[i].BakeMesh(meshes[i], true);
                pose.Sample(); views[0].Apply(stage.camera);
                lines.Add(frame + " " + pose.VisibilityTrace(stage.camera, 1, 1));
            }
            return string.Join("\n", lines);
        }
        finally
        {
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
            DatasetSceneFactory.ClosePreviewScene(scene); UnityEngine.Object.DestroyImmediate(c.catalog); UnityEngine.Object.DestroyImmediate(c);
        }
    }
}
