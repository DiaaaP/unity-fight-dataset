using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DatasetPenetrationChecks
{
    public const string Root = "DatasetOutput/Checks/Penetration_20261111";
    public const string Source = "DatasetOutput/Generated/batch_20261111_130756_292_b5ed2c/metadata/hit_00043_s114856200.json";
    [Serializable] public sealed class Frame { public int frame; public float depth; public int actor, samples; public Vector3 point; }
    [Serializable] public sealed class Report
    {
        public string phase, source, error;
        public int checkedFrames, penetratingFrames, firstPenetratingFrame = -1;
        public float maximumDepth, elapsedSeconds;
        public List<Frame> frames = new List<Frame>();
    }
    private static Scene scene;
    private static DatasetSceneFactory.Stage stage;
    private static DatasetGeneratorConfig settings;
    private static DatasetBatchExporter.Metadata data;
    private static DatasetPenetrationGuard guard;
    private static Report report;
    private static System.Diagnostics.Stopwatch timer;
    [Serializable] public sealed class CatalogRow { public string asset, error; public bool accepted; public int seed; }
    [Serializable] public sealed class CatalogReport { public string phase; public int checkedClips, total, acceptedPunches, acceptedKicks; public List<CatalogRow> clips = new List<CatalogRow>(); }
    private static CatalogReport catalogReport;
    private static CatalogClip[] catalogAttacks;
    public static bool IsRunning { get; private set; }

    public static string StartCatalogChecks()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish current work first.");
        settings = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        settings.catalog = UnityEngine.Object.Instantiate(settings.catalog);
        settings.generateHit = settings.generateKick = true; settings.generateFight = settings.generateNormal = false;
        settings.speedRange = Vector2.one;
        catalogAttacks = settings.catalog.clips.Where(c => DatasetCatalogTools.Usable(c) && (c.kind == ClipKind.Punch || c.kind == ClipKind.Kick)).ToArray();
        catalogReport = new CatalogReport { phase = "checking", total = catalogAttacks.Length };
        IsRunning = true; EditorApplication.update += CatalogTick;
        return Root + "/catalog.json";
    }

    private static void CatalogTick()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        var entry = catalogAttacks[catalogReport.checkedClips];
        var row = new CatalogRow { asset = entry.assetPath, seed = 12345 + catalogReport.checkedClips * 7919 };
        try
        {
            foreach (var attack in catalogAttacks) attack.enabled = attack == entry;
            var stage = DatasetSceneFactory.Create(settings, scene, row.seed);
            DatasetScenarioPlanner.Create(settings, stage, entry.kind == ClipKind.Kick ? "kick" : "hit", row.seed);
            row.accepted = true;
            if (entry.kind == ClipKind.Punch) catalogReport.acceptedPunches++; else catalogReport.acceptedKicks++;
        }
        catch (Exception e) { row.error = e.Message; }
        finally { DatasetSceneFactory.ClosePreviewScene(scene); }
        catalogReport.clips.Add(row); catalogReport.checkedClips++;
        if (catalogReport.checkedClips == catalogReport.total)
        {
            catalogReport.phase = catalogReport.acceptedPunches > 0 && catalogReport.acceptedKicks > 0 ? "completed" : "failed";
            EditorApplication.update -= CatalogTick; IsRunning = false;
            UnityEngine.Object.DestroyImmediate(settings.catalog); UnityEngine.Object.DestroyImmediate(settings);
        }
        Directory.CreateDirectory(Root); File.WriteAllText(Root + "/catalog.json", JsonUtility.ToJson(catalogReport, true));
    }

    public static string StartReviewBatch()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish current work first.");
        var config = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        try
        {
            config.scenariosPerCategory = 2; config.cameraViews = 1;
            config.generateHit = config.generateKick = true; config.generateFight = config.generateNormal = false;
            config.seed = 114856200; config.outputDirectory = Root + "/review";
            return DatasetBatchExporter.StartBatch(settings: config);
        }
        finally { UnityEngine.Object.DestroyImmediate(config); }
    }

    public static string CheckKnownVolumes()
    {
        var root = new GameObject("Penetration synthetic checks");
        var meshes = new List<Mesh>();
        try
        {
            var player = root.AddComponent<DatasetScenarioPlayer>(); player.actors = new Animator[2];
            for (int i = 0; i < 2; i++)
            {
                var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var mesh = UnityEngine.Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
                UnityEngine.Object.DestroyImmediate(primitive); meshes.Add(mesh);
                var actor = new GameObject("cube " + i); actor.transform.SetParent(root.transform, false);
                player.actors[i] = actor.AddComponent<Animator>();
                mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, mesh.vertexCount).ToArray();
                mesh.bindposes = new[] { Matrix4x4.identity };
                var skin = actor.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
                skin.bones = new[] { actor.transform }; skin.rootBone = actor.transform;
            }
            var results = new List<string>();
            foreach (float offset in new[] { 1.1f, 1f, .99f, .9f, .6f })
            {
                player.actors[1].transform.localPosition = Vector3.right * offset;
                using (var check = new DatasetPenetrationGuard(player))
                {
                    var result = check.Measure(); bool expected = offset < .98f;
                    if ((result.samples > 0) != expected) throw new Exception("Cube penetration false classification: offset=" + offset + ", depth=" + result.depth);
                    results.Add(offset + ":" + result.depth.ToString("F3"));
                }
            }
            Directory.CreateDirectory(Root); File.WriteAllText(Root + "/known_volumes.json", "{\"passed\":true,\"cases\":5,\"results\":\"" + string.Join(";", results) + "\"}");
            return string.Join("; ", results);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); foreach (var mesh in meshes) UnityEngine.Object.DestroyImmediate(mesh); }
    }

    public static DatasetScenarioPlan Restore(DatasetSceneFactory.Stage stage, DatasetGeneratorConfig settings, DatasetBatchExporter.Metadata data)
    {
        var plan = new DatasetScenarioPlan { duration = data.duration_seconds, attackBlendIn = data.attack_blend_in,
            reactionBlendIn = data.reaction_blend_in, blendOut = data.blend_out,
            neutralTimeOffsets = data.actors.Select(a => a.neutral_phase_seconds).ToArray(), neutralSpeeds = data.actors.Select(a => a.neutral_speed).ToArray() };
        for (int i = 0; i < 2; i++)
        {
            var local = stage.root.transform.InverseTransformPoint(data.actors[i].position);
            local.y = stage.player.actors[i].transform.localPosition.y;
            stage.player.SetActorLocalPosition(i, local);
            stage.player.actors[i].transform.rotation = Quaternion.Euler(data.actors[i].rotation_euler);
        }
        foreach (var a in data.actions)
            plan.actions.Add(new ScenarioAction { actor = a.actor == "actor_A" ? 0 : 1, target = a.target == "actor_A" ? 0 : 1,
                actorAction = a.actor_action, clipKey = a.clip_key, clip = settings.catalog.clips.First(c => c.key == a.clip_key).clip, assetPath = a.clip_asset,
                start = a.start_seconds, speed = a.speed, impactTime = a.impact_seconds, impactFrame = a.impact_frame,
                impactNormalizedTime = a.impact_normalized_time, limb = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), a.attacking_limb),
                targetLandmark = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), a.target_landmark),
                reaction = settings.catalog.clips.First(c => c.assetPath == a.reaction_asset).clip,
                reactionAsset = a.reaction_asset, reactionKind = a.reaction_kind, reactionStart = a.reaction_start_seconds, reactionSpeed = a.reaction_speed,
                reactionSourceOffset = a.reaction_source_offset_seconds, reactionFadeIn = a.reaction_blend_in,
                actorYawOffset = a.attack_yaw_offset_degrees, attackFadeIn = a.attack_blend_in, sourceEnd = a.source_end_seconds,
                attackEndDisplacement = a.attack_end_displacement, reactionEndDisplacement = a.reaction_end_displacement,
                strikeSourceStart = a.strike_source_start_seconds, strikeSourceEnd = a.strike_source_end_seconds, strikeSourcePeak = a.strike_source_peak_seconds });
        return stage.player.plan = plan;
    }

    public static string StartReportedCase()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish current work first.");
        data = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(Source));
        settings = DatasetStrikeTimingChecks.CaseSettings(data);
        scene = EditorSceneManager.NewPreviewScene(); stage = DatasetSceneFactory.Create(settings, scene, data.seed);
        Restore(stage, settings, data); stage.player.SampleAtTime(0);
        guard = new DatasetPenetrationGuard(stage.player);
        report = new Report { phase = "measuring_reported_case", source = Source }; timer = System.Diagnostics.Stopwatch.StartNew();
        IsRunning = true; EditorApplication.update += Tick; return Root + "/reported_case.json";
    }
    private static void Tick()
    {
        try
        {
            int frame = report.checkedFrames;
            stage.player.SampleAtTime(frame / (float)data.fps);
            var result = guard.Measure();
            report.frames.Add(new Frame { frame = frame, depth = result.depth, actor = result.actor, samples = result.samples, point = result.point });
            if (result.samples > 0)
            {
                report.penetratingFrames++;
                if (report.firstPenetratingFrame < 0) report.firstPenetratingFrame = frame;
                report.maximumDepth = Mathf.Max(report.maximumDepth, result.depth);
            }
            report.checkedFrames++;
            if (report.checkedFrames == data.frame_count) { report.phase = "completed"; Finish(); }
        }
        catch (Exception e) { report.error = e.ToString(); report.phase = "failed"; Finish(); }
        report.elapsedSeconds = (float)timer.Elapsed.TotalSeconds;
        Directory.CreateDirectory(Root); File.WriteAllText(Root + "/reported_case.json", JsonUtility.ToJson(report, true));
    }
    private static void Finish()
    {
        EditorApplication.update -= Tick; IsRunning = false; guard?.Dispose(); guard = null;
        DatasetSceneFactory.ClosePreviewScene(scene);
        UnityEngine.Object.DestroyImmediate(settings.catalog); UnityEngine.Object.DestroyImmediate(settings);
    }
}
