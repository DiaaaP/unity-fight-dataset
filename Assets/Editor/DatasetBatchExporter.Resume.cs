using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FightDataset;
using UnityEditor;
using UnityEngine;

public static partial class DatasetBatchExporter
{
    // A freshly allocated scenario index has no partial files. Only resume can revisit one.
    private static bool checkInterruptedFiles;
    private static DatasetBatchCheckpoint checkpoint;
    private static AnimationCatalog ownedCatalog;
    private static double elapsedRecorded, timingStarted = -1;
    public static bool CanResumeLast { get; private set; }
    public static int TotalScenarios => summary?.requested_scenarios ?? 0;
    public static int TotalVideos => TotalScenarios * (checkpoint?.camera_views ?? 0);
    public static int CompletedVideos => summary?.completed_videos ?? 0;
    public static int CurrentScenarioNumber => Mathf.Min(scenarioIndex + 1, TotalScenarios);
    public static int CurrentFrameCount => metadata?.frame_count ?? 0;
    public static string CurrentCategory => metadata?.category ?? "";
    public static float Progress => TotalScenarios == 0 ? 0 : Mathf.Clamp01((scenarioIndex +
        (CurrentFrameCount > 0 ? CurrentFrame / (float)CurrentFrameCount : 0)) / TotalScenarios);
    public static double ElapsedSeconds => elapsedRecorded + (timingStarted >= 0 ? EditorApplication.timeSinceStartup - timingStarted : 0);

    private static string LastBatchKey
    {
        get
        {
            using (var sha = SHA256.Create()) return "FightDataset.LastBatch." +
                BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Application.dataPath))).Replace("-", "");
        }
    }

    static DatasetBatchExporter()
    {
        LastBatchDirectory = EditorPrefs.GetString(LastBatchKey, "");
        LoadStoredProgress();
    }

    private static void RememberBatch(string directory)
    {
        LastBatchDirectory = directory;
        EditorPrefs.SetString(LastBatchKey, directory);
        CanResumeLast = true;
    }

    private static void LoadStoredProgress()
    {
        if (string.IsNullOrEmpty(LastBatchDirectory)) return;
        try
        {
            summary = JsonUtility.FromJson<Summary>(File.ReadAllText(Path.Combine(LastBatchDirectory, "manifest.json")));
            checkpoint = JsonUtility.FromJson<DatasetBatchCheckpoint>(File.ReadAllText(Path.Combine(LastBatchDirectory, "resume.json")));
            scenarioIndex = summary.next_scenario_index;
            CompletedScenarios = summary.completed_scenarios;
            FailedScenarios = summary.failed_scenarios;
            elapsedRecorded = checkpoint.elapsed_seconds;
            CanResumeLast = scenarioIndex < summary.requested_scenarios;
            Status = CanResumeLast ? "Saved; ready to resume" : summary.status;
        }
        catch { CanResumeLast = false; Status = "Idle"; summary = null; checkpoint = null; }
    }

    private static void RequireIdleEditor()
    {
        if (IsRunning || FightTestVideoExporter.IsRunning) throw new InvalidOperationException("Сначала приостанови и сохрани текущий пакет.");
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("Генерация доступна в Edit Mode после завершения компиляции.");
    }

    public static void Pause()
    {
        if (!IsRunning || IsPaused) return;
        FreezeClock();
        IsPaused = true;
        summary.status = "paused";
        Status = "Paused";
        SaveCheckpoint();
        WriteSummary();
    }

    public static void Resume()
    {
        if (!IsRunning) { ResumeBatch(LastBatchDirectory); return; }
        if (!IsPaused) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("Продолжение доступно в Edit Mode.");
        checkpoint.ValidateSources();
        readbackQueue?.ResetTimeouts();
        IsPaused = false;
        timingStarted = EditorApplication.timeSinceStartup;
        summary.status = "running";
        Status = "Generating";
        WriteSummary();
    }

    public static void SaveAndStop()
    {
        if (IsRunning) Finish("suspended");
    }

    public static string ResumeBatch(string directory)
    {
        RequireIdleEditor();
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Выбери папку сохранённого пакета.");
        directory = Path.GetFullPath(directory);
        var saved = JsonUtility.FromJson<DatasetBatchCheckpoint>(File.ReadAllText(Path.Combine(directory, "resume.json")));
        saved.ValidateSources();
        var manifest = JsonUtility.FromJson<Summary>(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var restoredConfig = ScriptableObject.CreateInstance<DatasetGeneratorConfig>();
        AnimationCatalog restoredCatalog = null;
        try
        {
            JsonUtility.FromJsonOverwrite(saved.config_json, restoredConfig);
            restoredCatalog = saved.RestoreCatalog();
            restoredConfig.catalog = restoredCatalog;
            restoredConfig.hideFlags = HideFlags.HideAndDontSave;
            restoredConfig.Validate();
            ValidateStoredProgress(directory, manifest, restoredConfig);
        }
        catch
        {
            UnityEngine.Object.DestroyImmediate(restoredConfig);
            if (restoredCatalog) UnityEngine.Object.DestroyImmediate(restoredCatalog);
            throw;
        }
        config = restoredConfig;
        ownedCatalog = restoredCatalog;
        checkpoint = saved;
        summary = manifest;
        categories = summary.available_categories;
        scenarioIndex = summary.next_scenario_index;
        CompletedScenarios = summary.completed_scenarios;
        FailedScenarios = summary.failed_scenarios;
        CurrentFrame = 0;
        elapsedRecorded = checkpoint.elapsed_seconds;
        timingStarted = EditorApplication.timeSinceStartup;
        checkInterruptedFiles = true;
        RememberBatch(directory);
        InitializeAnimationUsage();
        IsRunning = true;
        IsPaused = false;
        summary.status = "running";
        Status = "Resuming; unfinished scenario restarts from frame 0";
        AttachLifecycle();
        WriteSummary();
        DatasetGeneratorWindow.ShowWindow();
        return directory;
    }

    private static void ValidateStoredProgress(string directory, Summary manifest, DatasetGeneratorConfig restored)
    {
        if (manifest == null || manifest.schema != "fight_batch_v1" || manifest.available_categories == null || manifest.available_categories.Length == 0 ||
            !manifest.available_categories.SequenceEqual(DatasetScenarioPlanner.AvailableCategories(restored)) ||
            manifest.next_scenario_index != manifest.scenarios.Count + manifest.failures.Count ||
            manifest.completed_scenarios != manifest.scenarios.Count || manifest.failed_scenarios != manifest.failures.Count ||
            manifest.count_mode != "per_category" || manifest.scenarios_per_category != restored.scenarioCount ||
            manifest.total_scenario_limit < 0 ||
            manifest.requested_scenarios != PlannedScenarioCount(restored, manifest.available_categories.Length, manifest.total_scenario_limit) || manifest.seed != restored.seed ||
            manifest.completed_videos != manifest.scenarios.Sum(s => s.videos.Length)) throw new InvalidOperationException("Неполный или повреждённый manifest.json.");
        if (manifest.next_scenario_index >= manifest.requested_scenarios) throw new InvalidOperationException("Этот пакет уже завершён.");
        var indices = manifest.scenarios.Select(s => s.scenario_index).Concat(manifest.failures.Select(f => f.scenario_index)).OrderBy(i => i);
        if (!indices.SequenceEqual(Enumerable.Range(0, manifest.next_scenario_index))) throw new InvalidOperationException("Нарушена последовательность сценариев.");
        foreach (var scenario in manifest.scenarios)
        {
            foreach (string relative in scenario.videos.Concat(new[] { scenario.metadata }))
            {
                string path = ResolveBatchPath(directory, relative);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new InvalidOperationException("Готовый файл отсутствует: " + relative);
            }
            var data = JsonUtility.FromJson<Metadata>(File.ReadAllText(ResolveBatchPath(directory, scenario.metadata)));
            if (data.scenario_id != scenario.scenario_id || !data.videos.SequenceEqual(scenario.videos) ||
                data.width != restored.width || data.height != restored.height || data.fps != restored.fps)
                throw new InvalidOperationException("Метаданные готового сценария не совпадают с пакетом.");
            if (restored.exportYoloPose)
            {
                if (data.pose_image_count != data.frame_count * data.cameras.Length || scenario.pose_image_count != data.pose_image_count)
                    throw new InvalidOperationException("Неполная YOLO Pose разметка сценария.");
                foreach (var camera in data.cameras)
                    for (int frame = 0; frame < data.frame_count; frame++)
                    {
                        string stem = data.scenario_id + "_" + camera.id + "_" + frame.ToString("D5");
                        foreach (string relative in new[] { data.pose_images + "/" + stem + ".jpg", data.pose_annotations + "/" + stem + ".txt" })
                            if (!File.Exists(ResolveBatchPath(directory, relative))) throw new InvalidOperationException("YOLO pair missing: " + relative);
                    }
            }
        }
    }

    private static string ResolveBatchPath(string directory, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File path escapes batch directory.");
        return path;
    }

    private static void ArchiveUncommittedScenario()
    {
        string prefix = categories[scenarioIndex % categories.Length] + "_" + (scenarioIndex + 1).ToString("D5") + "_s";
        string archive = "logs/interrupted/" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var folders = new[] { categories[scenarioIndex % categories.Length], "metadata", "inspection", "joint_projections" }
            .Concat(new[] { "train", "val", "test" }.SelectMany(s => new[] { "yolo/images/" + s, "yolo/labels/" + s }));
        foreach (string folder in folders)
        {
            string source = ResolveBatchPath(LastBatchDirectory, folder);
            if (!Directory.Exists(source)) continue;
            foreach (string file in Directory.GetFiles(source, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                string targetPath = ResolveBatchPath(LastBatchDirectory, archive + "/" + folder + "/" + Path.GetFileName(file) + ".interrupted");
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.Move(file, targetPath);
            }
        }
        string pending = ResolveBatchPath(LastBatchDirectory, "yolo/.pending");
        if (Directory.Exists(pending))
            foreach (string source in Directory.GetDirectories(pending, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                string target = ResolveBatchPath(LastBatchDirectory, archive + "/yolo/.pending/" + Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                // Both paths are resolved and constrained to this batch before moving.
                Directory.Move(ResolveBatchPath(LastBatchDirectory, Path.GetRelativePath(LastBatchDirectory, source)), target);
            }
    }

    private static void SaveCheckpoint()
    {
        checkpoint.elapsed_seconds = ElapsedSeconds;
        DatasetBatchCheckpoint.WriteAtomic(Path.Combine(LastBatchDirectory, "resume.json"), JsonUtility.ToJson(checkpoint, true));
    }

    private static void FreezeClock()
    {
        elapsedRecorded = ElapsedSeconds;
        timingStarted = -1;
    }

    private static void AttachLifecycle()
    {
        EditorApplication.update += Tick;
        AssemblyReloadEvents.beforeAssemblyReload += SaveAndStop;
        EditorApplication.quitting += SaveAndStop;
        EditorApplication.playModeStateChanged += PlayModeChanged;
        DatasetCatalogTools.CatalogChanged += InputsChanged;
    }

    private static void DetachLifecycle()
    {
        EditorApplication.update -= Tick;
        AssemblyReloadEvents.beforeAssemblyReload -= SaveAndStop;
        EditorApplication.quitting -= SaveAndStop;
        EditorApplication.playModeStateChanged -= PlayModeChanged;
        DatasetCatalogTools.CatalogChanged -= InputsChanged;
    }

    private static void InputsChanged() => SaveAndStop();

    private static void ReleaseConfig()
    {
        if (config) UnityEngine.Object.DestroyImmediate(config);
        config = null;
        if (ownedCatalog) UnityEngine.Object.DestroyImmediate(ownedCatalog);
        ownedCatalog = null;
    }

    private static void StopAfterStorageError(string reason)
    {
        FreezeClock();
        DetachLifecycle();
        try { CancelPreparation(); CleanupScenario(); }
        finally
        {
            ReleaseConfig();
            IsRunning = IsPaused = false;
            CurrentFrame = 0;
            LoadStoredProgress();
            Status = reason;
        }
    }
}
