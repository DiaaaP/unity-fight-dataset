using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetStrikeTimingChecks
{
    public const string SourceBatch = "DatasetOutput/Generated/batch_20261109_165453_010_920226";
    public const string Root = "DatasetOutput/Checks/StrikeTiming_20261110";
    public static readonly string[] Cases = { "hit_00007", "hit_00025", "hit_00053", "hit_00151", "kick_00026", "kick_00120" };
    [Serializable] public sealed class Motion { public string clip; public float length; public List<Point> points = new List<Point>(); }
    [Serializable] public sealed class Point { public float time; public Vector3 left, right; }
    [Serializable] public sealed class MotionReport { public List<Motion> motions = new List<Motion>(); }
    [Serializable] public sealed class Probe
    {
        public string id, clip, error, directory;
        public bool passed, legacyRejected;
        public float oldNormalized, newNormalized, oldKnuckleGap, gap, yaw, sourcePeak, trimmedSourceEnd, reactionOffset;
        public int oldVisibleLagFrames, responseLagFrames, impactFrame;
    }
    [Serializable] public sealed class Report { public bool passed; public string phase; public List<Probe> cases = new List<Probe>(); }
    public static bool IsRunning { get; private set; }
    private static Report report;
    private static int index;
    private static Report exportReport;
    private static int exportIndex;

    public static string CheckProfileOrigins()
    {
        int passed = 0;
        foreach (string prefix in Cases)
        {
            var original = ReadCase(prefix); var settings = CaseSettings(original); string first = null;
            try
            {
                for (int variant = 0; variant < 2; variant++)
                {
                    var scene = EditorSceneManager.NewPreviewScene();
                    try
                    {
                        var stage = DatasetSceneFactory.Create(settings, scene, original.seed);
                        var root = stage.player.actors[0].transform;
                        stage.player.SetActorLocalPosition(0, new Vector3(variant == 0 ? -0.413f : 0.297f, root.localPosition.y, variant == 0 ? 0.377f : -0.459f));
                        root.localRotation = Quaternion.Euler(0, variant == 0 ? 13.123f : 211.987f, 0);
                        var action = new ScenarioAction { actor = 0, target = 1, clip = settings.catalog.clips.First(c => c.key == original.actions[0].clip_key).clip,
                            actorAction = original.actions[0].actor_action, start = 1, speed = 1, assetPath = original.actions[0].clip_asset };
                        stage.player.plan = new DatasetScenarioPlan { duration = 5 };
                        var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                        ((System.Collections.IDictionary)typeof(DatasetStrikeProfile).GetField("Cache", flags).GetValue(null)).Clear();
                        var profile = DatasetStrikeProfile.Get(stage.player, action);
                        var points = (Vector3[])typeof(DatasetStrikeProfile).GetField("points", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(profile);
                        string signature = JsonUtility.ToJson(profile) + string.Join("|", points.Select(p => p.ToString("R")));
                        if (first == null) first = signature;
                        else if (first != signature) throw new Exception("Source profile depends on actor origin/yaw: " + prefix);
                    }
                    finally { DatasetSceneFactory.ClosePreviewScene(scene); }
                }
                passed++;
            }
            finally { UnityEngine.Object.DestroyImmediate(settings.catalog); UnityEngine.Object.DestroyImmediate(settings); }
        }
        string path = Root + "/profile_origin_independence.json";
        File.WriteAllText(path, "{\"passed\":true,\"source_clips\":" + passed + ",\"origins_each\":2,\"comparison\":\"exact source profile fields and sample coordinates\"}");
        return path;
    }

    public static string StartExports()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Checks/export already running.");
        exportReport = new Report { phase = "exporting_exact_regressions" }; exportIndex = 0; IsRunning = true;
        EditorApplication.update += ExportTick;
        return Root + "/exports.json";
    }

    private static void ExportTick()
    {
        try
        {
            if (DatasetBatchExporter.IsRunning) return;
            if (exportReport.cases.Count > 0 && exportReport.cases.Last().directory != "" && !exportReport.cases.Last().passed)
            {
                var row = exportReport.cases.Last();
                var manifest = JsonUtility.FromJson<DatasetBatchExporter.Summary>(File.ReadAllText(row.directory + "/manifest.json"));
                if (manifest.completed_scenarios != 1 || manifest.failed_scenarios != 0)
                    throw new Exception(row.id + ": " + string.Join("; ", manifest.failures.Select(f => f.reason)));
                var metadata = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(row.directory + "/" + manifest.scenarios.Single().metadata));
                var original = ReadCase(Cases[exportIndex - 1]);
                if (metadata.seed != original.seed || metadata.actions.Single().clip_key != original.actions.Single().clip_key ||
                    !metadata.actors.Select(a => a.model_asset).SequenceEqual(original.actors.Select(a => a.model_asset)))
                    throw new Exception("Regression was replaced by another seed/model/source animation.");
                var action = metadata.actions.Single();
                row.gap = action.contact_error_meters; row.newNormalized = action.impact_normalized_time;
                row.responseLagFrames = action.reaction_visible_frame - action.impact_frame;
                row.passed = row.responseLagFrames >= 1 && row.responseLagFrames <= 2;
                if (!row.passed) throw new Exception("Visible response timing failed.");
            }
            if (exportIndex == Cases.Length)
            {
                exportReport.passed = exportReport.cases.All(r => r.passed); exportReport.phase = "passed";
                IsRunning = false; EditorApplication.update -= ExportTick;
            }
            else
            {
                string prefix = Cases[exportIndex++]; var original = ReadCase(prefix);
                var settings = CaseSettings(original);
                try
                {
                    settings.outputDirectory = Root + "/exports/" + prefix;
                    string directory = DatasetBatchExporter.StartBatch(settings: settings);
                    exportReport.cases.Add(new Probe { id = original.scenario_id, clip = original.actions[0].clip_asset, directory = directory });
                }
                finally { UnityEngine.Object.DestroyImmediate(settings.catalog); UnityEngine.Object.DestroyImmediate(settings); }
            }
        }
        catch (Exception exception)
        {
            if (exportReport.cases.Count > 0) exportReport.cases.Last().error = exception.ToString();
            exportReport.phase = "failed"; IsRunning = false; EditorApplication.update -= ExportTick;
            DatasetBatchExporter.SaveAndStop();
        }
        Directory.CreateDirectory(Root); File.WriteAllText(Root + "/exports.json", JsonUtility.ToJson(exportReport, true));
    }

    public static DatasetBatchExporter.Metadata ReadCase(string prefix) => JsonUtility.FromJson<DatasetBatchExporter.Metadata>(
        File.ReadAllText(Directory.GetFiles(SourceBatch + "/metadata", prefix + "_*.json").Single()));

    public static DatasetGeneratorConfig CaseSettings(DatasetBatchExporter.Metadata data)
    {
        var settings = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        settings.catalog = UnityEngine.Object.Instantiate(settings.catalog);
        settings.scenariosPerCategory = 1; settings.cameraViews = 1; settings.seed = data.seed;
        settings.generateHit = data.category == "hit"; settings.generateKick = data.category == "kick";
        settings.generateFight = settings.generateNormal = false;
        settings.width = data.width; settings.height = data.height; settings.fps = data.fps;
        var action = data.actions[0];
        settings.speedRange = new Vector2(action.speed, action.speed);
        foreach (var entry in settings.catalog.clips)
        {
            if (entry.kind != ClipKind.Punch && entry.kind != ClipKind.Kick) continue;
            entry.enabled = entry.key == action.clip_key;
            if (entry.enabled)
            {
                entry.speedRange = settings.speedRange;
                entry.preferredReactionKey = settings.catalog.clips.First(c => c.assetPath == action.reaction_asset).key;
            }
        }
        return settings;
    }

    public static string StartProbes()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish active generation/checks first.");
        report = new Report { phase = "regression_probes" }; index = 0; IsRunning = true;
        EditorApplication.update += ProbeTick;
        return Root + "/probes.json";
    }

    private static void ProbeTick()
    {
        if (index >= Cases.Length)
        {
            report.passed = report.cases.All(c => c.passed); report.phase = report.passed ? "passed" : "failed";
            IsRunning = false; EditorApplication.update -= ProbeTick; Write(); return;
        }
        var data = ReadCase(Cases[index++]);
        var row = new Probe { id = data.scenario_id, clip = data.actions[0].clip_asset, oldNormalized = data.actions[0].impact_normalized_time };
        var settings = CaseSettings(data);
        try
        {
            var oldScene = EditorSceneManager.NewPreviewScene();
            try
            {
                var stage = DatasetSceneFactory.Create(settings, oldScene, data.seed);
                var old = LegacyPlan(stage, settings, data);
                var action = old.actions.Single();
                var profile = DatasetStrikeProfile.Get(stage.player, action);
                row.sourcePeak = profile.peak / action.clip.length;
                stage.player.SampleAtTime(action.impactTime);
                using (var a = new DatasetContactGeometry(stage.player.actors[action.actor]))
                using (var b = new DatasetContactGeometry(stage.player.actors[action.target]))
                    row.oldKnuckleGap = b.Measure(a.LimbSurface(action.limb), action.targetLandmark).gap;
                row.oldVisibleLagFrames = -1;
                for (int offset = 1; offset <= 15; offset++)
                    if (DatasetReactionTiming.Deflection(stage.player, action, action.impactTime + offset / (float)settings.fps) >= 0.015f)
                    { row.oldVisibleLagFrames = offset; break; }
                try { DatasetContactSolver.Verify(settings, stage.player, action); DatasetReactionTiming.Verify(settings, stage.player, action); }
                catch (InvalidOperationException) { row.legacyRejected = true; }
            }
            finally { DatasetSceneFactory.ClosePreviewScene(oldScene); }
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var stage = DatasetSceneFactory.Create(settings, scene, data.seed);
                var plan = DatasetScenarioPlanner.Create(settings, stage, data.category, data.seed);
                var action = plan.actions.Single();
                var contact = DatasetContactSolver.Verify(settings, stage.player, action);
                DatasetReactionTiming.Verify(settings, stage.player, action);
                row.newNormalized = action.impactNormalizedTime; row.gap = contact.gap; row.yaw = action.actorYawOffset;
                row.trimmedSourceEnd = action.SourceEnd; row.reactionOffset = action.reactionSourceOffset;
                row.responseLagFrames = action.reactionResponseFrame - action.impactFrame; row.impactFrame = action.impactFrame;
                row.passed = row.legacyRejected && row.responseLagFrames >= 1 && row.responseLagFrames <= 2;
                if (!row.legacyRejected) row.error = "Legacy bad case is still accepted by the contact contract.";
            }
            finally { DatasetSceneFactory.ClosePreviewScene(scene); }
        }
        catch (Exception exception) { row.error = exception.ToString(); }
        finally { UnityEngine.Object.DestroyImmediate(settings.catalog); UnityEngine.Object.DestroyImmediate(settings); }
        report.cases.Add(row); Write();
    }

    private static DatasetScenarioPlan LegacyPlan(DatasetSceneFactory.Stage stage, DatasetGeneratorConfig settings, DatasetBatchExporter.Metadata data)
    {
        var source = data.actions[0];
        var plan = new DatasetScenarioPlan { duration = data.duration_seconds, attackBlendIn = data.attack_blend_in,
            reactionBlendIn = data.reaction_blend_in, blendOut = data.blend_out,
            neutralTimeOffsets = data.actors.Select(a => a.neutral_phase_seconds).ToArray(), neutralSpeeds = data.actors.Select(a => a.neutral_speed).ToArray() };
        for (int i = 0; i < 2; i++)
        {
            var local = stage.root.transform.InverseTransformPoint(data.actors[i].position); local.y = stage.player.actors[i].transform.localPosition.y;
            stage.player.SetActorLocalPosition(i, local);
            stage.player.actors[i].transform.rotation = Quaternion.Euler(data.actors[i].rotation_euler);
        }
        plan.actions.Add(new ScenarioAction { actor = 0, target = 1, actorAction = source.actor_action,
            clip = settings.catalog.clips.First(c => c.key == source.clip_key).clip, assetPath = source.clip_asset,
            start = source.start_seconds, speed = source.speed, impactTime = source.impact_seconds, impactFrame = source.impact_frame,
            impactNormalizedTime = source.impact_normalized_time, limb = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), source.attacking_limb),
            targetLandmark = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), source.target_landmark),
            reaction = settings.catalog.clips.First(c => c.assetPath == source.reaction_asset).clip,
            reactionAsset = source.reaction_asset, reactionKind = source.reaction_kind, reactionStart = source.reaction_start_seconds, reactionSpeed = source.reaction_speed });
        return stage.player.plan = plan;
    }

    private static void Write()
    {
        Directory.CreateDirectory(Root); File.WriteAllText(Root + "/probes.json", JsonUtility.ToJson(report, true));
    }

    public static string DumpSourceMotion()
    {
        var config = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        var report = new MotionReport();
        try
        {
            foreach (string prefix in Cases)
            {
                var data = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(Directory.GetFiles(SourceBatch + "/metadata", prefix + "_*.json").Single()));
                var entry = config.catalog.clips.First(c => c.key == data.actions[0].clip_key);
                var scene = EditorSceneManager.NewPreviewScene();
                try
                {
                    var stage = DatasetSceneFactory.Create(config, scene, data.seed);
                    stage.player.plan = new DatasetScenarioPlan { duration = 5 };
                    var actor = stage.player.actors[0];
                    var motion = new Motion { clip = entry.assetPath, length = entry.clip.length };
                    bool kick = entry.kind == ClipKind.Kick;
                    for (float t = 0; t < entry.clip.length; t += 1f / 60)
                    {
                        stage.player.SampleActorSource(0, entry.clip, t);
                        var inverse = Quaternion.Inverse(actor.transform.rotation);
                        motion.points.Add(new Point { time = t,
                            left = inverse * (DatasetContactSolver.LimbPoint(actor, kick ? HumanBodyBones.LeftFoot : HumanBodyBones.LeftHand) - actor.transform.position),
                            right = inverse * (DatasetContactSolver.LimbPoint(actor, kick ? HumanBodyBones.RightFoot : HumanBodyBones.RightHand) - actor.transform.position) });
                    }
                    report.motions.Add(motion);
                }
                finally { DatasetSceneFactory.ClosePreviewScene(scene); }
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(config); }
        Directory.CreateDirectory(Root); File.WriteAllText(Root + "/source_motion.json", JsonUtility.ToJson(report, true));
        return Root + "/source_motion.json";
    }
}
