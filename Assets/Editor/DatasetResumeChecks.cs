using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using FightDataset;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class DatasetResumeChecks
{
    public const string ReportPath = "DatasetOutput/Checks/progress_resume_test.json";
    [Serializable] public sealed class FileHash { public string path, hash; }
    [Serializable] public sealed class Report
    {
        public string phase, directory, interruptedScenario, error;
        public bool passed, exactFramePause, continuedAfterReload, completedFilesUnchanged, replayDeterministic, changedSourcesRejected;
        public int pausedFrame, completedBeforeReload, finalVideos, projectionFiles;
        public int pendingFramesAtPause;
        public bool asyncCapture;
        public bool attackScenarios, animationUsageRecovered;
        public string[] prefixSignatures;
        public List<FileHash> committedFiles = new List<FileHash>();
    }
    private static Report report;
    private static int phase, pausedTicks;
    private static double started;

    public static string Start(bool attackScenarios = false)
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Export already active.");
        report = new Report { phase = "running_pause_test", attackScenarios = attackScenarios };
        phase = pausedTicks = 0;
        started = EditorApplication.timeSinceStartup;
        var config = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        string saved = EditorJsonUtility.ToJson(config);
        try
        {
            config.scenarioCount = 3;
            config.cameraViews = 2;
            config.width = 320; config.height = 180; config.fps = 10;
            config.minimumDurationSeconds = 3;
            config.generateHit = attackScenarios;
            config.generateKick = config.generateFight = false;
            config.generateNormal = !attackScenarios;
            config.exportProjectedBodyJoints = true;
            config.seed = 12345;
            report.directory = DatasetBatchExporter.StartBatch();
        }
        finally { EditorJsonUtility.FromJsonOverwrite(saved, config); EditorUtility.SetDirty(config); AssetDatabase.SaveAssetIfDirty(config); }
        EditorApplication.update += BeforeReload;
        Write();
        return report.directory;
    }

    private static void BeforeReload()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 90) throw new Exception("Pause test timed out.");
            if (phase == 0 && DatasetBatchExporter.CurrentFrame >= 5)
            {
                Click("pause");
                if (!DatasetBatchExporter.IsPaused) throw new Exception("Pause button did not pause export.");
                report.pausedFrame = DatasetBatchExporter.CurrentFrame;
                report.asyncCapture = DatasetBatchExporter.UsesAsyncReadback;
                report.pendingFramesAtPause = DatasetBatchExporter.PendingReadbackFrames;
                phase = 1;
            }
            else if (phase == 1)
            {
                if (DatasetBatchExporter.CurrentFrame != report.pausedFrame) throw new Exception("Frame advanced on pause.");
                if (++pausedTicks < 8) return;
                Click("resume");
                if (DatasetBatchExporter.IsPaused) throw new Exception("Resume button did not resume export.");
                report.exactFramePause = true;
                phase = 2;
            }
            else if (phase == 2 && DatasetBatchExporter.CompletedScenarios == 1 && DatasetBatchExporter.CurrentFrame >= 9)
            {
                Click("pause");
                report.completedBeforeReload = DatasetBatchExporter.CompletedScenarios;
                var metadata = (DatasetBatchExporter.Metadata)typeof(DatasetBatchExporter).GetField("metadata", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                report.interruptedScenario = metadata.scenario_id;
                report.prefixSignatures = metadata.pose_signatures.Take(DatasetBatchExporter.CurrentFrame).ToArray();
                var manifest = JsonUtility.FromJson<DatasetBatchExporter.Summary>(File.ReadAllText(Path.Combine(report.directory, "manifest.json")));
                foreach (string relative in manifest.scenarios.SelectMany(s => s.videos.Concat(new[] { s.metadata })))
                    report.committedFiles.Add(new FileHash { path = relative, hash = Hash(Path.Combine(report.directory, relative)) });
                report.phase = "paused_ready_for_domain_reload";
                Write();
                EditorApplication.update -= BeforeReload;
            }
        }
        catch (Exception exception) { Fail(exception); }
    }

    public static string ContinueAfterReload()
    {
        report = JsonUtility.FromJson<Report>(File.ReadAllText(ReportPath));
        if (report.phase != "paused_ready_for_domain_reload" || DatasetBatchExporter.IsRunning ||
            !DatasetBatchExporter.CanResumeLast || DatasetBatchExporter.CompletedScenarios != 1 || DatasetBatchExporter.TotalScenarios != 3)
            throw new Exception("Persistent progress did not survive domain reload.");
        Click("resume");
        if (!DatasetBatchExporter.IsRunning || DatasetBatchExporter.LastBatchDirectory != report.directory || DatasetBatchExporter.TotalVideos != 6)
            throw new Exception("Restored batch or target count changed.");
        report.continuedAfterReload = true;
        report.phase = "resumed_after_domain_reload";
        started = EditorApplication.timeSinceStartup;
        EditorApplication.update += AfterReload;
        Write();
        return report.directory;
    }

    private static void AfterReload()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 90) throw new Exception("Resume test timed out.");
            if (DatasetBatchExporter.IsRunning) return;
            if (DatasetBatchExporter.CompletedScenarios != 3 || DatasetBatchExporter.FailedScenarios != 0 || DatasetBatchExporter.CanResumeLast)
                throw new Exception("Resumed batch did not finish cleanly.");
            foreach (var file in report.committedFiles)
                if (Hash(Path.Combine(report.directory, file.path)) != file.hash) throw new Exception("Completed output was overwritten: " + file.path);
            report.completedFilesUnchanged = true;
            var replay = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(Path.Combine(report.directory, "metadata", report.interruptedScenario + ".json")));
            if (!report.prefixSignatures.SequenceEqual(replay.pose_signatures.Take(report.prefixSignatures.Length)))
                throw new Exception("Restarted scenario changed its original poses.");
            report.replayDeterministic = true;
            var manifest = JsonUtility.FromJson<DatasetBatchExporter.Summary>(File.ReadAllText(Path.Combine(report.directory, "manifest.json")));
            var allActions = manifest.scenarios.SelectMany(s => JsonUtility.FromJson<DatasetBatchExporter.Metadata>(
                File.ReadAllText(Path.Combine(report.directory, s.metadata))).actions).ToArray();
            if (manifest.animation_usage.Any(row => row.completed_actions != allActions.Count(a => a.clip_key == row.clip_key)))
                throw new Exception("Animation usage counters were not restored after reload.");
            if (report.attackScenarios && allActions.Select(a => a.clip_key).Distinct().Count() != 3)
                throw new Exception("Three attack scenarios did not use three different compatible clips.");
            report.animationUsageRecovered = true;
            report.finalVideos = Directory.GetFiles(report.directory, "*.mp4", SearchOption.AllDirectories).Length;
            if (report.finalVideos != 6 || Directory.GetFiles(report.directory, "*.partial.mp4", SearchOption.AllDirectories).Length != 0)
                throw new Exception("Duplicate, missing or partial final videos.");
            foreach (string file in Directory.GetFiles(Path.Combine(report.directory, "joint_projections"), "*.jsonl"))
            {
                string[] lines = File.ReadAllLines(file);
                string name = Path.GetFileNameWithoutExtension(file);
                string scenarioName = name.Substring(0, name.LastIndexOf("_cam", StringComparison.Ordinal));
                int expectedFrames = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(
                    Path.Combine(report.directory, "metadata", scenarioName + ".json"))).frame_count;
                if (lines.Length != expectedFrames) throw new Exception("Projection frame count changed across pause/recovery.");
                for (int i = 0; i < lines.Length; i++)
                    if (JsonUtility.FromJson<FrameNumber>(lines[i]).frame != i) throw new Exception("Projection frame duplicated or skipped.");
                report.projectionFiles++;
            }
            CheckRejectedRecovery();
            report.passed = true;
            report.phase = "passed";
            Write();
            EditorApplication.update -= AfterReload;
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void CheckRejectedRecovery()
    {
        string folder = Path.Combine(report.directory, "logs", "rejected_recovery_test");
        Directory.CreateDirectory(folder);
        var checkpoint = JsonUtility.FromJson<DatasetBatchCheckpoint>(File.ReadAllText(Path.Combine(report.directory, "resume.json")));
        checkpoint.fingerprint = "intentionally_changed_source_fingerprint";
        File.WriteAllText(Path.Combine(folder, "resume.json"), JsonUtility.ToJson(checkpoint));
        string manifest = File.ReadAllText(Path.Combine(report.directory, "manifest.json"));
        File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest);
        bool rejected = false;
        try { DatasetBatchExporter.ResumeBatch(folder); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected || DatasetBatchExporter.IsRunning || File.ReadAllText(Path.Combine(folder, "manifest.json")) != manifest)
            throw new Exception("Changed inputs were not rejected before writing outputs.");
        report.changedSourcesRejected = true;
    }

    private static void Click(string name)
    {
        DatasetGeneratorWindow.ShowWindow();
        var window = Resources.FindObjectsOfTypeAll<DatasetGeneratorWindow>().First();
        window.RefreshView();
        var button = window.rootVisualElement.Q<Button>(name);
        if (button == null || !button.enabledInHierarchy) throw new Exception("UI button unavailable: " + name);
        using (var evt = NavigationSubmitEvent.GetPooled()) { evt.target = button; button.SendEvent(evt); }
        var error = window.rootVisualElement.Q<HelpBox>("error");
        if (!error.ClassListContains("hidden")) throw new Exception("UI action failed: " + error.text);
    }

    private static string Hash(string path)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)));
    }

    private static void Fail(Exception exception)
    {
        report.error = exception.ToString(); report.phase = "failed";
        EditorApplication.update -= BeforeReload;
        EditorApplication.update -= AfterReload;
        DatasetBatchExporter.SaveAndStop();
        Write();
    }

    private static void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
    }

    [Serializable] private sealed class FrameNumber { public int frame; }
}
