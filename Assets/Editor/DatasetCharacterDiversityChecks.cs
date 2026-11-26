using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetCharacterDiversityChecks
{
    public static bool IsRunning { get; private set; }
    public static string Status => report?.phase;
    public const string Root = DatasetCharacterTools.ReportDirectory;
    [Serializable] public sealed class ModelResult
    {
        public string model, image, faceImage, labels, faceLabels, error;
        public float height;
        public int vertices, materials;
        public bool passed;
        public DatasetYoloPose.FaceAnchor[] faceAnchors;
    }
    [Serializable] public sealed class Report { public string phase; public bool passed; public List<ModelResult> models = new List<ModelResult>(); }
    private static Report report;
    private static Queue<string> pending;
    public static string StartAudit()
    {
        if (DatasetCharacterTools.IsRunning || DatasetBatchExporter.IsRunning || IsRunning) throw new InvalidOperationException("Finish active generation/import first.");
        report = new Report { phase = "models" };
        pending = new Queue<string>(AssetDatabase.LoadAssetAtPath<AnimationCatalog>(DatasetCatalogTools.CatalogPath).characters.Select(AssetDatabase.GetAssetPath));
        Directory.CreateDirectory(Root + "/models"); IsRunning = true; EditorApplication.update += Tick;
        return "Checking " + pending.Count + " character models";
    }
    private static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (pending.Count == 0)
        {
            IsRunning = false; EditorApplication.update -= Tick;
            report.passed = report.models.All(m => m.passed); report.phase = report.passed ? "passed" : "failed";
            Write(); return;
        }
        string path = pending.Dequeue();
        var row = new ModelResult { model = path }; report.models.Add(row);
        var scene = EditorSceneManager.NewPreviewScene();
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        var catalog = UnityEngine.Object.Instantiate(c.catalog); c.catalog = catalog;
        var meshes = new List<Mesh>(); RenderTexture rt = null; Texture2D pixels = null;
        try
        {
            c.catalog.characters = new List<GameObject> { AssetDatabase.LoadAssetAtPath<GameObject>(path) };
            c.randomizeCharacterHeight = true; c.characterHeightMeters = Vector2.one * (report.models.Count % 2 == 0 ? 1.95f : 1.65f);
            var stage = DatasetSceneFactory.Create(c, scene, 261100 + report.models.Count);
            stage.player.actors[1].transform.localPosition += Vector3.right * 15;
            using var pose = new DatasetYoloPose(stage);
            foreach (var actor in stage.player.actors)
                foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (skin.sharedMaterials.Any(m => !m || !m.mainTexture || !m.shader || !m.shader.name.StartsWith("Universal Render Pipeline/")))
                        throw new Exception("Missing URP material/diffuse texture: " + skin.name);
                    var mesh = new Mesh(); meshes.Add(mesh); skin.BakeMesh(mesh, true); pose.Bind(skin, mesh);
                }
            pose.Sample();
            row.height = DatasetSceneFactory.CharacterBounds(stage.player.actors[0]).size.y;
            if (Mathf.Abs(row.height - c.characterHeightMeters.x) > .002f) throw new Exception("Height fitting is incorrect: " + row.height);
            row.vertices = stage.player.actors[0].GetComponentsInChildren<SkinnedMeshRenderer>().Sum(s => s.sharedMesh.vertexCount);
            row.materials = stage.player.actors[0].GetComponentsInChildren<SkinnedMeshRenderer>().SelectMany(s => s.sharedMaterials).Distinct().Count();
            row.faceAnchors = pose.faceAnchors.Take(5).ToArray();
            var camera = stage.camera; camera.enabled = false; camera.scene = scene; camera.aspect = 1; camera.fieldOfView = 40;
            rt = new RenderTexture(512, 512, 24); rt.Create(); camera.targetTexture = rt;
            pixels = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            var actor0 = stage.player.actors[0]; Vector3 head = actor0.GetBoneTransform(HumanBodyBones.Head).position;
            Vector3 right = actor0.GetBoneTransform(HumanBodyBones.RightUpperArm).position - actor0.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
            right.y = 0; Vector3 forward = Vector3.Cross(right.normalized, Vector3.up).normalized;
            string stem = Path.GetFileNameWithoutExtension(path);
            Vector3 aim = actor0.transform.position + Vector3.up * row.height * .51f;
            camera.transform.position = aim + forward * (row.height * 1.7f); camera.transform.LookAt(aim);
            row.image = "models/" + stem + ".png"; row.labels = "models/" + stem + ".txt"; Save(row.image, row.labels);
            camera.transform.position = head + forward * .68f + Vector3.up * .035f; camera.transform.LookAt(head + Vector3.up * .025f);
            row.faceImage = "models/" + stem + "_face.png"; row.faceLabels = "models/" + stem + "_face.txt"; Save(row.faceImage, row.faceLabels);
            row.passed = true;
            void Save(string image, string labels)
            {
                camera.Render(); var previous = RenderTexture.active;
                try { RenderTexture.active = rt; pixels.ReadPixels(new Rect(0, 0, 512, 512), 0, 0); pixels.Apply(); }
                finally { RenderTexture.active = previous; }
                File.WriteAllBytes(Root + "/" + image, pixels.EncodeToPNG());
                File.WriteAllText(Root + "/" + labels, pose.Labels(camera));
            }
        }
        catch (Exception e) { row.error = e.ToString(); }
        finally
        {
            if (rt) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            if (pixels) UnityEngine.Object.DestroyImmediate(pixels);
            foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh);
            DatasetSceneFactory.ClosePreviewScene(scene); UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(c);
            report.phase = "Checked " + report.models.Count; Write();
        }
    }
    private static void Write() => File.WriteAllText(Root + "/model_audit.json", JsonUtility.ToJson(report, true));

    public static string StartExport(string category, bool partial, int seed, string first = null, string second = null)
    {
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        var catalog = UnityEngine.Object.Instantiate(c.catalog); c.catalog = catalog;
        try
        {
            if (!string.IsNullOrEmpty(first)) catalog.characters = new List<GameObject> { AssetDatabase.LoadAssetAtPath<GameObject>(first), AssetDatabase.LoadAssetAtPath<GameObject>(second) };
            c.scenariosPerCategory = 1; c.cameraViews = 1; c.generateHit = category == "hit"; c.generateKick = category == "kick";
            c.generateFight = false; c.generateNormal = category == "normal"; c.randomizeCharacterHeight = true;
            c.characterHeightMeters = new Vector2(1.65f, 1.95f); c.partialFramingProbability = partial ? 1 : 0;
            c.minimumPartialBodyCoverage = .45f; c.width = 640; c.height = 360; c.fps = 25; c.minimumDurationSeconds = 3;
            c.seed = seed; c.exportYoloPose = true; c.outputDirectory = Root + "/exports";
            return DatasetBatchExporter.StartBatch(settings: c);
        }
        finally { UnityEngine.Object.DestroyImmediate(catalog); UnityEngine.Object.DestroyImmediate(c); }
    }

    public static string CalibrateFace(int auditIndex, float[] pixels)
    {
        if (IsRunning || DatasetBatchExporter.IsRunning || pixels.Length != 10) throw new InvalidOperationException("Calibration needs ten face-image pixel coordinates while idle.");
        var audit = JsonUtility.FromJson<Report>(File.ReadAllText(Root + "/model_audit.json"));
        string path = audit.models[auditIndex].model;
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        var cat = UnityEngine.Object.Instantiate(c.catalog); c.catalog = cat;
        var scene = EditorSceneManager.NewPreviewScene(); RenderTexture rt = null;
        try
        {
            cat.characters = new List<GameObject> { AssetDatabase.LoadAssetAtPath<GameObject>(path) };
            c.randomizeCharacterHeight = true; c.characterHeightMeters = Vector2.one * (auditIndex % 2 == 1 ? 1.95f : 1.65f);
            var stage = DatasetSceneFactory.Create(c, scene, 261101 + auditIndex);
            stage.player.actors[1].transform.localPosition += Vector3.right * 15;
            using var pose = new DatasetYoloPose(stage, true); var actor = stage.player.actors[0];
            Vector3 head = actor.GetBoneTransform(HumanBodyBones.Head).position;
            Vector3 right = actor.GetBoneTransform(HumanBodyBones.RightUpperArm).position - actor.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
            right.y = 0; Vector3 forward = Vector3.Cross(right.normalized, Vector3.up).normalized;
            var camera = stage.camera; camera.enabled = false; camera.scene = scene; camera.aspect = 1; camera.fieldOfView = 40;
            rt = new RenderTexture(512, 512, 24); rt.Create(); camera.targetTexture = rt;
            camera.transform.position = head + forward * .68f + Vector3.up * .035f; camera.transform.LookAt(head + Vector3.up * .025f);
            var points = Enumerable.Range(0, 5).Select(k => pose.PickFaceAnchor(camera, 0, k, new Vector2(pixels[k * 2], pixels[k * 2 + 1]) / 512f)).ToArray();
            DatasetFaceLandmarks.Save(path, points); return path;
        }
        finally
        {
            if (rt) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
            DatasetSceneFactory.ClosePreviewScene(scene); UnityEngine.Object.DestroyImmediate(c); UnityEngine.Object.DestroyImmediate(cat);
        }
    }

    public static string StartCameraReview()
    {
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        try
        {
            c.scenariosPerCategory = 8; c.cameraViews = 1; c.generateNormal = true; c.generateHit = c.generateKick = c.generateFight = false;
            c.randomizeCharacterHeight = true; c.characterHeightMeters = new Vector2(1.65f, 1.95f);
            c.partialFramingProbability = .5f; c.occlusionViewProbability = .25f;
            c.targetOccludedTrainingFraction = .05f; c.poseTrainingSamplesPerSecond = 5;
            c.roomSize = new Vector2(12, 12); c.cameraDistanceMeters = new Vector2(.95f, 7.5f); c.cameraFov = new Vector2(30, 110);
            c.seed = 261126; c.width = 640; c.height = 360; c.fps = 15; c.minimumDurationSeconds = 3;
            c.exportYoloPose = true; c.outputDirectory = Root + "/camera_review";
            return DatasetBatchExporter.StartBatch(settings: c);
        }
        finally { UnityEngine.Object.DestroyImmediate(c); }
    }
}
