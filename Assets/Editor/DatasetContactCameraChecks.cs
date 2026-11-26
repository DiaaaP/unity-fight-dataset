using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetContactCameraChecks
{
    [Serializable] public sealed class StrikeResult { public string asset, limb, target, error; public float speed, gap, approachSpeed; public int frame; public bool passed; }
    [Serializable] public sealed class Report
    {
        public bool passed;
        public string phase, error;
        public int completed, total, cameraScenarios, framingSamples;
        public int distributionSamples, anatomicalFilterChecks;
        public int[] profileCounts;
        public bool weightedDistributionPassed, balancedOrderingPassed;
        public List<StrikeResult> strikes = new List<StrikeResult>();
        public List<DatasetCameraView> cameras = new List<DatasetCameraView>();
    }
    public static bool IsRunning { get; private set; }
    public static string Status => report?.phase;
    private static Report report;
    private static DatasetGeneratorConfig config;
    private static AnimationCatalog catalog;
    private static CatalogClip[] attacks;
    private static int index;
    private const int CameraTests = 48;
    private const string ReportPath = "DatasetOutput/Checks/contact_camera_checks.json";

    public static string DiagnoseRejectedFrames()
    {
        string directory = File.ReadAllText("DatasetOutput/Checks/weighted_camera_batch_directory.txt").Trim();
        var summary = JsonUtility.FromJson<DatasetBatchExporter.Summary>(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var saved = JsonUtility.FromJson<DatasetBatchCheckpoint>(File.ReadAllText(Path.Combine(directory, "resume.json")));
        var c = ScriptableObject.CreateInstance<DatasetGeneratorConfig>(); JsonUtility.FromJsonOverwrite(saved.config_json, c);
        var cat = saved.RestoreCatalog(); c.catalog = cat;
        var lines = new List<string>();
        try
        {
            foreach (var failure in summary.failures.Take(4))
            {
                var scene = EditorSceneManager.NewPreviewScene();
                try
                {
                    var used = summary.scenarios.Where(s => s.scenario_index < failure.scenario_index).SelectMany(s =>
                        JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(Path.Combine(directory, s.metadata))).actions)
                        .GroupBy(a => a.clip_key).ToDictionary(g => g.Key, g => g.Count());
                    var stage = DatasetSceneFactory.Create(c, scene, failure.seed);
                    var plan = DatasetScenarioPlanner.Create(c, stage, failure.category, failure.seed, used);
                    int frame = int.Parse(failure.reason.Substring(failure.reason.LastIndexOf(' ') + 1));
                    stage.player.SampleAtTime(frame / (float)c.fps);
                    foreach (var actor in stage.player.actors)
                    {
                        var mesh = new Mesh(); var bounds = new Bounds(); bool first = true;
                        try
                        {
                            foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
                            {
                                skin.BakeMesh(mesh, true);
                                foreach (var p in mesh.vertices)
                                {
                                    var local = stage.root.transform.InverseTransformPoint(skin.transform.TransformPoint(p));
                                    if (first) { bounds = new Bounds(local, Vector3.zero); first = false; } else bounds.Encapsulate(local);
                                }
                            }
                        }
                        finally { UnityEngine.Object.DestroyImmediate(mesh); }
                        lines.Add(failure.scenario_index + " " + plan.actions[0].assetPath + " " + actor.name + " frame=" + frame + " min=" + bounds.min.ToString("F4") + " max=" + bounds.max.ToString("F4"));
                    }
                }
                finally { DatasetSceneFactory.ClosePreviewScene(scene); }
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(c); UnityEngine.Object.DestroyImmediate(cat); }
        return string.Join("\n", lines);
    }

    public static string StartReviewBatch()
    {
        if (IsRunning) throw new InvalidOperationException("Finish contact checks first.");
        var savedConfig = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        string saved = EditorJsonUtility.ToJson(savedConfig);
        try
        {
            savedConfig.outputDirectory = "DatasetOutput/WeightedCameraReview_20261109";
            savedConfig.seed = 90911;
            savedConfig.scenarioCount = 20;
            savedConfig.cameraViews = 1;
            savedConfig.generateHit = savedConfig.generateKick = true;
            savedConfig.generateFight = savedConfig.generateNormal = false;
            string path = DatasetBatchExporter.StartBatch();
            File.WriteAllText("DatasetOutput/Checks/weighted_camera_batch_directory.txt", path);
            return path;
        }
        finally
        {
            EditorJsonUtility.FromJsonOverwrite(saved, savedConfig);
            EditorUtility.SetDirty(savedConfig); AssetDatabase.SaveAssetIfDirty(savedConfig);
        }
    }

    public static string Start()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning || EditorApplication.isPlaying) throw new InvalidOperationException("Checks require idle Edit mode.");
        config = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        config.generateHit = config.generateKick = config.generateFight = config.generateNormal = true;
        catalog = UnityEngine.Object.Instantiate(config.catalog); config.catalog = catalog;
        attacks = catalog.clips.Where(c => DatasetCatalogTools.Usable(c) && (c.kind == ClipKind.Punch || c.kind == ClipKind.Kick)).ToArray();
        report = new Report { phase = "per_clip_contact", total = attacks.Length * 3 + CameraTests + 8 };
        CheckDistributions();
        index = 0; IsRunning = true;
        EditorApplication.update += Tick;
        Write();
        return ReportPath;
    }

    private static void CheckDistributions()
    {
        report.profileCounts = new int[6]; report.distributionSamples = 30000;
        for (int i = 0; i < report.distributionSamples; i++)
            report.profileCounts[DatasetCameraPlanner.ProfileIndex(unchecked(110457582 + (i / 3) * 104729), i % 3, config)]++;
        float upper = (report.profileCounts[2] + report.profileCounts[3] + report.profileCounts[5]) / (float)report.distributionSamples;
        float low = report.profileCounts[1] / (float)report.distributionSamples;
        if (upper < 0.72f || upper > 0.78f || low < 0.035f || low > 0.065f) throw new Exception("Camera probability regression: " + upper + "/" + low);
        report.weightedDistributionPassed = true;
        foreach (var kind in new[] { ClipKind.Punch, ClipKind.Kick })
        {
            var pool = attacks.Where(a => a.kind == kind).ToArray();
            var used = pool.ToDictionary(a => a.key, _ => 0);
            var random = new System.Random(10911);
            for (int i = 0; i < pool.Length * 8; i++)
            {
                var next = DatasetScenarioPlanner.OrderAttacks(pool, used, null, random).First();
                used[next.key]++;
                if (used.Values.Max() - used.Values.Min() > 1) throw new Exception("Animation ordering repeatedly favors a clip.");
            }
        }
        report.balancedOrderingPassed = true;
    }

    public static string ContinueSequences()
    {
        string previous = File.ReadAllText(ReportPath);
        Start();
        report = JsonUtility.FromJson<Report>(previous); report.error = ""; report.phase = "fight_contact_and_cameras";
        foreach (var attack in attacks) attack.enabled = true;
        config.speedRange = new Vector2(0.8f, 1.2f); config.cameraViews = 3;
        index = attacks.Length * 3 + CameraTests;
        return ReportPath;
    }

    public static string ContinueCameras()
    {
        string previous = File.ReadAllText(ReportPath);
        Start(); report = JsonUtility.FromJson<Report>(previous);
        report.error = ""; report.passed = false; report.cameras.Clear(); report.cameraScenarios = report.framingSamples = 0;
        foreach (var attack in attacks) attack.enabled = true;
        config.speedRange = new Vector2(0.8f, 1.2f); config.cameraViews = 3;
        index = attacks.Length * 3; report.phase = "camera_diversity";
        return ReportPath;
    }

    private static void Tick()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            if (index < attacks.Length * 3) CheckStrike(scene);
            else if (index < attacks.Length * 3 + CameraTests) CheckCamera(scene);
            else if (index < report.total) CheckSequence(scene);
            else
            {
                int passed = report.strikes.Count(s => s.passed);
                if (passed < attacks.Length * 2) throw new Exception("Too few source attacks passed contact calibration: " + passed);
                if (report.cameras.Select(c => c.profile).Distinct().Count() != 6) throw new Exception("Not all six camera families were generated.");
                if (report.cameras.Max(c => c.height) - report.cameras.Min(c => c.height) < 2.5f) throw new Exception("Camera height diversity is too small.");
                if (report.cameras.Max(c => c.fov) - report.cameras.Min(c => c.fov) < 20) throw new Exception("Camera zoom diversity is too small.");
                report.passed = true; report.phase = "passed"; Stop();
                return;
            }
            index++; report.completed = index; Write();
        }
        catch (Exception exception) { report.error = exception.ToString(); report.phase = "failed"; Stop(); }
        finally { DatasetSceneFactory.ClosePreviewScene(scene); }
    }

    private static void CheckStrike(UnityEngine.SceneManagement.Scene scene)
    {
        var entry = attacks[index / 3];
        float speed = new[] { 0.8f, 1f, 1.2f }[index % 3];
        report.phase = "contact " + (index + 1) + "/" + attacks.Length * 3;
        foreach (var attack in attacks) attack.enabled = attack == entry;
        config.speedRange = new Vector2(speed, speed);
        var result = new StrikeResult { asset = entry.assetPath, speed = speed };
        try
        {
            int seed = 12345 + index * 7919;
            var stage = DatasetSceneFactory.Create(config, scene, seed);
            var baseY = stage.player.actors.Select(a => a.transform.localPosition.y).ToArray();
            var plan = DatasetScenarioPlanner.Create(config, stage, entry.kind == ClipKind.Kick ? "kick" : "hit", seed);
            var action = plan.actions.Single();
            stage.player.SampleAtTime(action.impactTime);
            var contact = DatasetContactSolver.Verify(config, stage.player, action);
            Vector3 point = stage.player.actors[action.actor].GetBoneTransform(action.limb).position;
            stage.player.SampleAtTime(plan.duration - 0.01f);
            stage.player.SampleAtTime(action.impactTime);
            if (Vector3.Distance(point, stage.player.actors[action.actor].GetBoneTransform(action.limb).position) > 0.0001f)
                throw new Exception("Impact depends on sample order.");
            if (Mathf.Abs(action.impactTime - action.impactFrame / (float)config.fps) > 0.0001f || Mathf.Abs(action.reactionStart - action.impactTime) > 0.0001f)
                throw new Exception("Impact frame or reaction timing is invalid.");
            DatasetReactionTiming.Verify(config, stage.player, action);
            if (action.approachSpeed < 0.06f) throw new Exception("Accepted contact has no incoming strike motion.");
            for (int actor = 0; actor < 2; actor++)
                if (Mathf.Abs(stage.player.GetActorLocalPosition(actor).y - baseY[actor]) > 0.0001f)
                    throw new Exception("Ground correction was accidentally committed as the standing base pose.");
            using (var body = new DatasetContactGeometry(stage.player.actors[1]))
            {
                Vector3 head = stage.player.actors[1].GetBoneTransform(HumanBodyBones.Head).position;
                Vector3 hand = body.LimbSurface(HumanBodyBones.RightHand).OrderByDescending(p => Vector3.SqrMagnitude(p - head)).First();
                if (body.Measure(new[] { hand }).gap > 0.0001f || body.Measure(new[] { hand }, HumanBodyBones.Head).gap < 0.02f)
                    throw new Exception("A target hand is incorrectly counted as a head hit.");
                Vector3 chest = stage.player.actors[1].GetBoneTransform(HumanBodyBones.Chest).position;
                if (!body.Occludes(chest + Vector3.forward * 2, chest - Vector3.forward * 2) ||
                    body.Occludes(chest + Vector3.up * 5, chest + Vector3.up * 6))
                    throw new Exception("Actual mesh line-of-sight test is invalid.");
                report.anatomicalFilterChecks++;
            }
            result.passed = true; result.gap = contact.gap; result.frame = action.impactFrame; result.limb = action.limb.ToString(); result.target = action.targetLandmark.ToString(); result.approachSpeed = action.approachSpeed;
        }
        catch (Exception exception) { result.error = exception.Message; }
        report.strikes.Add(result);
    }

    private static void CheckCamera(UnityEngine.SceneManagement.Scene scene)
    {
        foreach (var attack in attacks) attack.enabled = true;
        config.speedRange = new Vector2(0.8f, 1.2f); config.cameraViews = 3;
        int seed = 196003 + (index - attacks.Length * 3) * 7919;
        var stage = DatasetSceneFactory.Create(config, scene, seed);
        DatasetScenarioPlanner.Create(config, stage, "normal", seed);
        var cameras = DatasetScenarioPlanner.Cameras(config, stage, seed);
        CheckFraming(stage, cameras);
        report.cameras.AddRange(cameras); report.cameraScenarios++; report.phase = "camera_diversity";
    }

    private static void CheckSequence(UnityEngine.SceneManagement.Scene scene)
    {
        Exception failure = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var attemptScene = EditorSceneManager.NewPreviewScene();
            try
            {
                int seed = 12345 + (index - attacks.Length * 3 - CameraTests) * 104729 + attempt * 7919;
                var stage = DatasetSceneFactory.Create(config, attemptScene, seed);
                var plan = DatasetScenarioPlanner.Create(config, stage, "fight", seed);
                foreach (var action in plan.actions)
                {
                    stage.player.SampleAtTime(action.impactTime);
                    DatasetContactSolver.Verify(config, stage.player, action);
                }
                var views = DatasetScenarioPlanner.Cameras(config, stage, seed);
                CheckFraming(stage, views);
                report.phase = "fight_contact_and_cameras";
                return;
            }
            catch (Exception exception) { failure = exception; }
            finally { DatasetSceneFactory.ClosePreviewScene(attemptScene); }
        }
        throw new Exception("No valid fight after the exporter's eight attempts.", failure);
    }

    private static void CheckFraming(DatasetSceneFactory.Stage stage, DatasetCameraView[] cameras)
    {
        for (float time = 0; time < stage.player.plan.duration; time += 0.13f)
        {
            stage.player.SampleAtTime(time);
            foreach (var camera in cameras)
            {
                camera.Apply(stage.camera);
                if (camera.allowsPartialBody)
                {
                    foreach (var actor in stage.player.actors)
                        if (!DatasetCameraPlanner.PartialPointsSafe(stage.camera, new[] { HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.Chest,
                            HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                            HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg }
                            .Select(b => actor.GetBoneTransform(b).position).ToArray(), camera.minimumBodyCoverage)) throw new Exception("Partial camera loses a character.");
                }
                else foreach (var actor in stage.player.actors)
                    foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                        if (!FightDataset.Prototype.FightScenarioRandomizer.Inside(stage.camera, actor.GetBoneTransform(bone).position, 0.02f))
                            throw new Exception("Camera clips a character: " + camera.profile);
                report.framingSamples++;
            }
        }
        foreach (var camera in cameras)
        {
            camera.Apply(stage.camera);
            if (!DatasetCameraPlanner.ContactsVisible(stage, camera.occlusionChallenge)) throw new Exception("Camera obscures contact.");
        }
    }

    private static void Stop()
    {
        EditorApplication.update -= Tick; IsRunning = false;
        if (config) UnityEngine.Object.DestroyImmediate(config);
        if (catalog) UnityEngine.Object.DestroyImmediate(catalog);
        Write();
    }
    private static void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
    }
}
