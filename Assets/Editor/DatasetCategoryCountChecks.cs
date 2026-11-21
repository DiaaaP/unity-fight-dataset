using System;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEngine;

public static class DatasetCategoryCountChecks
{
    [Serializable] public sealed class Report
    {
        public string phase, error, directory, hundredPlanDirectory;
        public bool passed, hundredEachPlanned, smokeUnchanged, totalLimitPreserved, resumed;
        public int totalScenarios, totalVideos, hitScenarios, kickScenarios;
    }
    private const string ReportPath = "DatasetOutput/Checks/per_category_count.json";
    private static Report report;
    private static double started;

    public static string Start()
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Export is already running.");
        var config = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        string original = EditorJsonUtility.ToJson(config);
        report = new Report { phase = "planning" };
        try
        {
            config.generateHit = config.generateKick = true;
            config.generateFight = config.generateNormal = false;
            config.cameraViews = 1; config.width = 320; config.height = 180; config.fps = 10;
            config.scenarioCount = 100;
            config.outputDirectory = "DatasetOutput/Checks/PerCategoryCount";
            report.hundredPlanDirectory = DatasetBatchExporter.StartBatch();
            DatasetBatchExporter.Pause();
            var plan = ReadManifest(report.hundredPlanDirectory);
            if (DatasetBatchExporter.TotalScenarios != 200 || DatasetBatchExporter.TotalVideos != 200 ||
                plan.scenarios_per_category != 100 || plan.requested_scenarios != 200 ||
                !plan.available_categories.SequenceEqual(new[] { "hit", "kick" })) throw new Exception("100 must plan 100 per category.");
            DatasetBatchExporter.SaveAndStop();
            DatasetBatchExporter.ResumeBatch(report.hundredPlanDirectory);
            DatasetBatchExporter.Pause();
            if (DatasetBatchExporter.TotalScenarios != 200) throw new Exception("Resuming changed the per-category target.");
            DatasetBatchExporter.SaveAndStop();
            report.hundredEachPlanned = true;

            DatasetBatchExporter.StartSmokeTest(); DatasetBatchExporter.Pause();
            if (DatasetBatchExporter.TotalScenarios != 2 || DatasetBatchExporter.TotalVideos != 4)
                throw new Exception("Smoke test must keep one scenario per category and two views.");
            DatasetBatchExporter.SaveAndStop(); report.smokeUnchanged = true;

            DatasetBatchExporter.StartBatch(false, 3); DatasetBatchExporter.Pause();
            if (DatasetBatchExporter.TotalScenarios != 3) throw new Exception("Diagnostic overall limit was multiplied.");
            DatasetBatchExporter.SaveAndStop(); report.totalLimitPreserved = true;

            config.scenarioCount = 2; config.seed = 29111;
            report.directory = DatasetBatchExporter.StartBatch();
            report.totalScenarios = DatasetBatchExporter.TotalScenarios;
            report.totalVideos = DatasetBatchExporter.TotalVideos;
            if (report.totalScenarios != 4 || report.totalVideos != 4) throw new Exception("2 per class must plan four videos at one view.");
            report.phase = "encoding_2_each"; started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            Write();
            return ReportPath;
        }
        catch (Exception exception) { Fail(exception); throw; }
        finally
        {
            EditorJsonUtility.FromJsonOverwrite(original, config);
            EditorUtility.SetDirty(config); AssetDatabase.SaveAssetIfDirty(config);
        }
    }

    private static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 120) throw new Exception("Per-category test timed out.");
            if (!report.resumed && DatasetBatchExporter.CompletedScenarios == 1 && DatasetBatchExporter.CurrentFrame >= 5)
            {
                DatasetBatchExporter.SaveAndStop();
                DatasetBatchExporter.ResumeBatch(report.directory);
                if (DatasetBatchExporter.TotalScenarios != 4 || DatasetBatchExporter.CompletedScenarios != 1)
                    throw new Exception("Resume lost the combined total or committed scenario.");
                report.resumed = true; Write();
            }
            if (DatasetBatchExporter.IsRunning) return;
            var manifest = ReadManifest(report.directory);
            report.hitScenarios = manifest.scenarios.Count(s => s.category == "hit");
            report.kickScenarios = manifest.scenarios.Count(s => s.category == "kick");
            if (!report.resumed || manifest.failed_scenarios != 0 || manifest.completed_videos != 4 ||
                report.hitScenarios != 2 || report.kickScenarios != 2 || manifest.next_scenario_index != 4)
                throw new Exception("Export did not produce exactly two hits and two kicks.");
            foreach (var entry in manifest.scenarios)
                foreach (string video in entry.videos)
                    if (new FileInfo(Path.Combine(report.directory, video)).Length < 1024) throw new Exception("Missing video: " + video);
            report.passed = true; report.phase = "passed";
            EditorApplication.update -= Tick; Write();
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static DatasetBatchExporter.Summary ReadManifest(string path) =>
        JsonUtility.FromJson<DatasetBatchExporter.Summary>(File.ReadAllText(Path.Combine(path, "manifest.json")));

    private static void Fail(Exception exception)
    {
        EditorApplication.update -= Tick;
        DatasetBatchExporter.SaveAndStop();
        report.error = exception.ToString(); report.phase = "failed"; Write();
    }
    private static void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
    }
}
