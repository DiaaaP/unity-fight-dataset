using System;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEngine;

public static class DatasetResponsivenessChecks
{
    public const string Root = "DatasetOutput/Checks/EditorResponsive_20261111";
    [Serializable] public sealed class Report
    {
        public string phase, directory, cancelledDirectory, error;
        public bool passed, pausedDuringPreparation, cancelledCleanly;
        public int editorUpdates, preparingUpdates, warnings, completedVideos;
        public double maximumEditorSliceMs;
        public double maximumPreparationSliceMs;
        public string slowestPreparationPhase;
    }
    private static Report report;
    private static readonly System.Diagnostics.Stopwatch slice = new System.Diagnostics.Stopwatch();
    private static int pausedUpdates;
    private static bool resumeThenCancel;
    public static bool IsRunning { get; private set; }

    public static string Start()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish active generation first.");
        Directory.CreateDirectory(Root);
        report = new Report { phase = "pause_cancel_probe" }; pausedUpdates = 0; resumeThenCancel = false;
        IsRunning = true; EditorApplication.update += Before;
        Application.logMessageReceived += Log;
        report.directory = StartOne();
        EditorApplication.update += After;
        Write(); return Root + "/report.json";
    }

    private static string StartOne()
    {
        var c = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        c.catalog = UnityEngine.Object.Instantiate(c.catalog);
        try
        {
            foreach (var entry in c.catalog.clips)
                if (entry.kind == ClipKind.Punch || entry.kind == ClipKind.Kick) entry.enabled = entry.assetPath.EndsWith("/Punch_9.fbx");
            c.scenariosPerCategory = 1; c.generateHit = true; c.generateKick = c.generateFight = c.generateNormal = false;
            c.speedRange = Vector2.one; c.seed = 202401; c.cameraViews = 1;
            c.width = 640; c.height = 360; c.fps = 25; c.saveInspectionFrames = false; c.outputDirectory = Root + "/exports";
            return DatasetBatchExporter.StartBatch(settings: c);
        }
        finally { UnityEngine.Object.DestroyImmediate(c.catalog); UnityEngine.Object.DestroyImmediate(c); }
    }

    private static void Before() => slice.Restart();
    private static void After()
    {
        if (!IsRunning) return;
        report.maximumEditorSliceMs = Math.Max(report.maximumEditorSliceMs, slice.Elapsed.TotalMilliseconds);
        report.maximumPreparationSliceMs = Math.Max(report.maximumPreparationSliceMs, DatasetBatchExporter.MaximumPreparationSliceMs);
        report.slowestPreparationPhase = DatasetBatchExporter.SlowestPreparationPhase;
        report.editorUpdates++;
        if (DatasetBatchExporter.IsPreparing) report.preparingUpdates++;
        try
        {
            if (report.phase == "pause_cancel_probe")
            {
                if (DatasetBatchExporter.Status.Contains("Checking intersections") && !resumeThenCancel && !DatasetBatchExporter.IsPaused)
                {
                    DatasetBatchExporter.Pause(); report.pausedDuringPreparation = DatasetBatchExporter.IsPreparing && DatasetBatchExporter.IsPaused;
                    pausedUpdates = report.editorUpdates;
                }
                if (DatasetBatchExporter.IsPaused && report.editorUpdates - pausedUpdates >= 4)
                {
                    DatasetBatchExporter.Resume(); resumeThenCancel = true; pausedUpdates = report.editorUpdates;
                }
                if (resumeThenCancel && report.editorUpdates - pausedUpdates >= 4)
                {
                    DatasetBatchExporter.SaveAndStop();
                    report.cancelledCleanly = !DatasetBatchExporter.IsPreparing && ProbeMeshes() == 0;
                    if (!report.pausedDuringPreparation || !report.cancelledCleanly) throw new Exception("Preparation pause/cancel cleanup failed.");
                    report.cancelledDirectory = report.directory;
                    EditorApplication.update -= After;
                    report.directory = StartOne(); // append Tick before the timing observer
                    EditorApplication.update += After;
                    report.phase = "exporting";
                }
            }
            else if (!DatasetBatchExporter.IsRunning)
            {
                var summary = JsonUtility.FromJson<DatasetBatchExporter.Summary>(File.ReadAllText(report.directory + "/manifest.json"));
                report.completedVideos = summary.completed_videos;
                if (summary.completed_scenarios != 1 || summary.failed_scenarios != 0 || report.warnings != 0 || ProbeMeshes() != 0)
                    throw new Exception("Native export, warning or cleanup regression.");
                // Other update callbacks include CLI C# compilation. Measure the
                // generator itself rather than attributing those pauses to it.
                if (report.maximumPreparationSliceMs > 2000) throw new Exception("A preparation slice still exceeds two seconds.");
                report.passed = true; report.phase = "passed"; Stop();
            }
        }
        catch (Exception e) { report.error = e.ToString(); report.phase = "failed"; DatasetBatchExporter.SaveAndStop(); Stop(); }
        Write();
    }
    private static int ProbeMeshes() => Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m.name == "DatasetPenetrationProbe" || m.name == "ActorGroundProbe" || m.name == "ContactProbe");
    private static void Log(string message, string stack, LogType type) { if (type == LogType.Warning) report.warnings++; }
    private static void Stop()
    {
        IsRunning = false; EditorApplication.update -= Before; EditorApplication.update -= After;
        Application.logMessageReceived -= Log;
    }
    private static void Write() => File.WriteAllText(Root + "/report.json", JsonUtility.ToJson(report, true));
}
