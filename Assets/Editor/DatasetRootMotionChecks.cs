using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetRootMotionChecks
{
    public const string Root = "DatasetOutput/Checks/RootMotion_20261111";
    public const string Reported = "DatasetOutput/Checks/Penetration_20261111/review/batch_20261111_133835_637_3025c5/metadata/hit_00001_s114856200.json";
    [Serializable] public sealed class Sample { public float time; public Vector3 hips, leftFoot, rightFoot; public float leftUpper, leftLower, rightUpper, rightLower; }
    [Serializable] public sealed class Motion { public string asset, mode; public float maximumHipTravel, maximumFootSpan; public List<Sample> samples = new List<Sample>(); }
    [Serializable] public sealed class Report { public bool passed; public int importedClips, attacks; public List<Motion> motions = new List<Motion>(); }

    public static string Compare()
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish generation first.");
        var c = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        var data = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(Reported));
        var report = new Report();
        foreach (var entry in c.catalog.clips)
        {
            var s = AnimationUtility.GetAnimationClipSettings(entry.clip);
            if (!s.loopBlendOrientation || !s.loopBlendPositionXZ || !s.loopBlendPositionY || !s.keepOriginalOrientation || !s.keepOriginalPositionXZ)
                throw new Exception("Extracted motion would be lost: " + entry.assetPath);
            report.importedClips++;
        }
        foreach (var entry in c.catalog.clips.Where(e => DatasetCatalogTools.Usable(e) && (e.kind == ClipKind.Punch || e.kind == ClipKind.Kick)))
        {
            var scene = EditorSceneManager.NewPreviewScene();
            AnimationClip legacy = null;
            try
            {
                var stage = DatasetSceneFactory.Create(c, scene, data.seed);
                stage.player.plan = new DatasetScenarioPlan { duration = 10 };
                legacy = UnityEngine.Object.Instantiate(entry.clip); legacy.name = "Legacy root extraction probe";
                var s = AnimationUtility.GetAnimationClipSettings(legacy);
                s.loopBlendOrientation = s.loopBlendPositionY = s.loopBlendPositionXZ = false;
                s.keepOriginalOrientation = s.keepOriginalPositionXZ = false;
                AnimationUtility.SetAnimationClipSettings(legacy, s);
                foreach (var clip in new[] { legacy, entry.clip })
                {
                    var row = new Motion { asset = entry.assetPath, mode = clip == legacy ? "legacy_extracted_discarded" : "authored_motion_in_pose" };
                    var animator = stage.player.actors[0]; Vector3 first = default;
                    for (int i = 0; i <= 60; i++)
                    {
                        float t = i * (entry.clip.length - .0001f) / 60;
                        stage.player.SampleActorSource(0, clip, t);
                        Vector3 P(HumanBodyBones bone) => animator.GetBoneTransform(bone).position;
                        var point = new Sample { time = t, hips = P(HumanBodyBones.Hips), leftFoot = P(HumanBodyBones.LeftFoot), rightFoot = P(HumanBodyBones.RightFoot),
                            leftUpper = Vector3.Distance(P(HumanBodyBones.LeftUpperLeg), P(HumanBodyBones.LeftLowerLeg)),
                            leftLower = Vector3.Distance(P(HumanBodyBones.LeftLowerLeg), P(HumanBodyBones.LeftFoot)),
                            rightUpper = Vector3.Distance(P(HumanBodyBones.RightUpperLeg), P(HumanBodyBones.RightLowerLeg)),
                            rightLower = Vector3.Distance(P(HumanBodyBones.RightLowerLeg), P(HumanBodyBones.RightFoot)) };
                        if (i == 0) first = point.hips;
                        Vector3 delta = point.hips - first; delta.y = 0;
                        row.maximumHipTravel = Mathf.Max(row.maximumHipTravel, delta.magnitude);
                        row.maximumFootSpan = Mathf.Max(row.maximumFootSpan, Vector3.Distance(point.leftFoot, point.rightFoot));
                        if (new[] { point.leftUpper, point.leftLower, point.rightUpper, point.rightLower }.Any(v => float.IsNaN(v) || v < .2f || v > .65f))
                            throw new Exception("Invalid leg bone length: " + entry.assetPath);
                        row.samples.Add(point);
                    }
                    report.motions.Add(row);
                }
                report.attacks++;
            }
            finally { DatasetSceneFactory.ClosePreviewScene(scene); if (legacy) UnityEngine.Object.DestroyImmediate(legacy); }
        }
        var old = report.motions.Single(m => m.asset == "Assets/Animations/Attacks/Punch/Punch_7.fbx" && m.mode == "legacy_extracted_discarded");
        var fixedPose = report.motions.Single(m => m.asset == old.asset && m.mode == "authored_motion_in_pose");
        if (fixedPose.maximumHipTravel < old.maximumHipTravel + .2f) throw new Exception("Reported lunge is still suppressed.");
        report.passed = true; Directory.CreateDirectory(Root);
        File.WriteAllText(Root + "/motion_comparison.json", JsonUtility.ToJson(report, true));
        return "imports=" + report.importedClips + ", attacks=" + report.attacks + ", Punch_7 hip travel " + old.maximumHipTravel + " -> " + fixedPose.maximumHipTravel;
    }

    public static string StartReportedExport()
    {
        var data = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(Reported));
        var settings = DatasetStrikeTimingChecks.CaseSettings(data);
        try
        {
            settings.outputDirectory = Root + "/reported_export";
            string directory = DatasetBatchExporter.StartBatch(settings: settings);
            Directory.CreateDirectory(Root); File.WriteAllText(Root + "/export_directory.txt", directory);
            return directory;
        }
        finally { UnityEngine.Object.DestroyImmediate(settings.catalog); UnityEngine.Object.DestroyImmediate(settings); }
    }
}
