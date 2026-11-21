using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

public static class DatasetBatchVerifier
{
    public static string Status { get; private set; } = "Idle";
    public static bool IsRunning { get; private set; }
    public static int CompletedVideos { get; private set; }
    private sealed class Task
    {
        public string path, metadataPath;
        public DatasetBatchExporter.Metadata metadata;
    }
    [Serializable] public sealed class VideoResult
    {
        public string video;
        public int decoded_frames;
        public float contrast, mean_pixel_change;
    }
    [Serializable] public sealed class Report
    {
        public bool passed;
        public string scope = "decode_dimensions_timestamps_split_grouping_and_selected_frames_not_contact_ground_truth";
        public string error;
        public List<VideoResult> videos = new List<VideoResult>();
    }
    private static string directory;
    private static Task[] tasks;
    private static int taskIndex, frameCount;
    private static long lastFrame;
    private static VideoPlayer player;
    private static RenderTexture texture;
    private static Texture2D pixels;
    private static Color32[] reference;
    private static float contrast, difference;
    private static double started;
    private static Report report;
    private static bool preparedPending, finishedPending;
    private static string pendingError;
    private static long pendingFrame = -1, capturedFrame = -1;
    private static double nextVideoAt;

    public static void Start(string batchDirectory)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Decode verification requires Play mode.");
        if (IsRunning) throw new InvalidOperationException("Verifier already running.");
        directory = Path.GetFullPath(batchDirectory);
        var summary = JsonUtility.FromJson<DatasetBatchExporter.Summary>(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        if (summary.scenarios.Count == 0 || summary.status == "running") throw new InvalidOperationException("No completed batch to verify.");
        if (summary.scenarios.GroupBy(s => s.group_id).Any(g => g.Select(s => s.split).Distinct().Count() != 1)) throw new Exception("Split leakage between correlated scenarios.");
        var pending = new List<Task>();
        foreach (var scenario in summary.scenarios)
        {
            string metadataPath = Resolve(scenario.metadata);
            var metadata = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(metadataPath));
            if (metadata.frame_times_seconds.Length != metadata.frame_count || metadata.pose_signatures.Length != metadata.frame_count ||
                metadata.videos.Length != metadata.cameras.Length || metadata.group_id != scenario.group_id || metadata.split != scenario.split)
                throw new Exception("Metadata/manifest mismatch.");
            for (int frame = 0; frame < metadata.frame_count; frame++)
                if (Mathf.Abs(metadata.frame_times_seconds[frame] - frame / (float)metadata.fps) > 0.0001f || string.IsNullOrEmpty(metadata.pose_signatures[frame])) throw new Exception("Invalid timeline.");
            foreach (var action in metadata.actions)
                if (action.impact_seconds < action.start_seconds || action.reaction_end_seconds > metadata.duration_seconds ||
                    (action.holds_fallen_pose ? action.reaction_kind != "Fall" || action.idle_return_seconds != -1f
                    : action.idle_return_seconds > metadata.duration_seconds || action.reaction_end_seconds > action.idle_return_seconds))
                    throw new Exception("Action timing outside scenario.");
            pending.AddRange(metadata.videos.Select(video => new Task { path = Resolve(video), metadataPath = metadataPath, metadata = metadata }));
        }
        tasks = pending.ToArray();
        taskIndex = CompletedVideos = 0;
        report = new Report();
        IsRunning = true;
        EditorApplication.update += Watchdog;
        BeginVideo();
    }

    private static string Resolve(string relative)
    {
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Path escapes batch directory.");
        return path;
    }

    private static void BeginVideo()
    {
        try
        {
            var task = tasks[taskIndex];
            var metadata = task.metadata;
            if (!File.Exists(task.path)) throw new FileNotFoundException("MP4 missing", task.path);
            frameCount = 0;
            lastFrame = -1;
            pendingFrame = capturedFrame = -1;
            preparedPending = finishedPending = false;
            pendingError = null;
            reference = null;
            contrast = difference = 0;
            started = EditorApplication.timeSinceStartup;
            texture = new RenderTexture(metadata.width, metadata.height, 0, RenderTextureFormat.ARGB32);
            texture.Create();
            pixels = new Texture2D(metadata.width, metadata.height, TextureFormat.RGBA32, false);
            player = new GameObject("DatasetDecodeCheck") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<VideoPlayer>();
            player.playOnAwake = false;
            player.source = VideoSource.Url;
            player.url = task.path;
            player.renderMode = VideoRenderMode.RenderTexture;
            player.targetTexture = texture;
            player.audioOutputMode = VideoAudioOutputMode.None;
            player.skipOnDrop = false;
            player.timeUpdateMode = VideoTimeUpdateMode.UnscaledGameTime;
            player.waitForFirstFrame = true;
            player.isLooping = false;
            player.sendFrameReadyEvents = true;
            player.prepareCompleted += Prepared;
            player.frameReady += Frame;
            player.loopPointReached += Finished;
            player.errorReceived += Error;
            Status = "Decoding " + (taskIndex + 1) + "/" + tasks.Length;
            player.Prepare();
        }
        catch (Exception exception) { Fail(exception.Message); }
    }

    // Native VideoUpdate callbacks only publish state. Never Play, Stop, destroy
    // a player, read GPU textures or create its successor from inside them.
    private static void Prepared(VideoPlayer video) => preparedPending = true;

    private static void ProcessPrepared()
    {
        var metadata = tasks[taskIndex].metadata;
        if (player.width != metadata.width || player.height != metadata.height || player.frameCount != (ulong)metadata.frame_count ||
            Math.Abs(player.frameRate - metadata.fps) > 0.01 || Math.Abs(player.length - metadata.duration_seconds) > 1.1 / metadata.fps)
        { Fail("Decoded video properties disagree with metadata."); return; }
        player.Play();
    }

    private static void Frame(VideoPlayer video, long frame)
    {
        if (frame <= lastFrame) return;
        lastFrame = frame;
        frameCount++;
        pendingFrame = frame;
    }

    private static void CapturePendingFrame()
    {
        long frame = pendingFrame;
        if (frame < 0 || frame == capturedFrame || !player || player.frame != frame) return;
        capturedFrame = frame;
        var metadata = tasks[taskIndex].metadata;
        bool inspect = frame == 0 || frame == metadata.frame_count - 1 || metadata.actions.Any(a => frame == Mathf.RoundToInt(a.impact_seconds * metadata.fps) || frame == Mathf.RoundToInt((a.impact_seconds + 0.25f) * metadata.fps));
        if (!inspect) return;
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = texture;
            pixels.ReadPixels(new Rect(0, 0, metadata.width, metadata.height), 0, 0, false);
            pixels.Apply(false, false);
            string folder = Path.Combine(directory, "decoded_inspection");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, Path.GetFileNameWithoutExtension(tasks[taskIndex].path) + "_" + frame.ToString("D5") + ".png"), pixels.EncodeToPNG());
            var data = pixels.GetPixels32();
            if (reference == null)
            {
                reference = data;
                int low = 255, high = 0;
                for (int i = 0; i < data.Length; i += 17) { int value = (data[i].r + data[i].g + data[i].b) / 3; low = Math.Min(low, value); high = Math.Max(high, value); }
                contrast = high - low;
            }
            else
            {
                double sum = 0;
                int count = 0;
                for (int i = 0; i < data.Length; i += 17) { sum += Math.Abs(data[i].r - reference[i].r) + Math.Abs(data[i].g - reference[i].g) + Math.Abs(data[i].b - reference[i].b); count += 3; }
                difference = Mathf.Max(difference, (float)(sum / count));
            }
        }
        catch (Exception exception) { Fail(exception.Message); }
        finally { RenderTexture.active = previous; }
    }

    private static void Finished(VideoPlayer video) => finishedPending = true;

    private static void ProcessFinished()
    {
        var task = tasks[taskIndex];
        if (frameCount != task.metadata.frame_count || contrast < 20 || (task.metadata.actions.Length > 0 && difference < 0.08f))
        { Fail($"Invalid decoded content: frames={frameCount}, contrast={contrast}, change={difference}"); return; }
        report.videos.Add(new VideoResult { video = Path.GetFileName(task.path), decoded_frames = frameCount, contrast = contrast, mean_pixel_change = difference });
        CleanupVideo();
        CompletedVideos++;
        taskIndex++;
        if (taskIndex < tasks.Length) { nextVideoAt = EditorApplication.timeSinceStartup + 0.3; return; }
        try
        {
            foreach (var group in tasks.GroupBy(t => t.metadataPath))
            {
                var metadata = group.First().metadata;
                metadata.status = "decoded_pending_visual_and_contact_review";
                File.WriteAllText(group.Key, JsonUtility.ToJson(metadata, true));
            }
            report.passed = true;
            Status = "Passed";
            Complete();
        }
        catch (Exception exception) { Fail(exception.Message); }
    }

    private static void Error(VideoPlayer video, string error) => pendingError = error;
    private static void Watchdog()
    {
        try
        {
            if (!EditorApplication.isPlaying) { Fail("Decode interrupted by leaving Play mode."); return; }
            if (!player)
            {
                if (EditorApplication.timeSinceStartup >= nextVideoAt) BeginVideo();
                return;
            }
            if (pendingError != null) { Fail(pendingError); return; }
            if (preparedPending) { preparedPending = false; ProcessPrepared(); if (!IsRunning) return; }
            CapturePendingFrame();
            if (!IsRunning) return;
            if (finishedPending) { finishedPending = false; ProcessFinished(); return; }
            if (EditorApplication.timeSinceStartup - started > Math.Max(40, tasks[taskIndex].metadata.duration_seconds * 3)) Fail("Decode timed out.");
        }
        catch (Exception exception) { Fail(exception.Message); }
    }
    private static void Fail(string error) { report.error = error; Status = "Failed: " + error; Complete(); }
    private static void Complete()
    {
        EditorApplication.update -= Watchdog;
        CleanupVideo();
        IsRunning = false;
        File.WriteAllText(Path.Combine(directory, "verification.json"), JsonUtility.ToJson(report, true));
    }
    private static void CleanupVideo()
    {
        if (player)
        {
            player.prepareCompleted -= Prepared;
            player.frameReady -= Frame;
            player.loopPointReached -= Finished;
            player.errorReceived -= Error;
            player.targetTexture = null;
            player.Stop();
            DestroySafely(player.gameObject);
        }
        player = null;
        if (texture) DestroySafely(texture);
        if (pixels) DestroySafely(pixels);
        texture = null;
        pixels = null;
    }

    private static void DestroySafely(UnityEngine.Object value)
    {
        if (EditorApplication.isPlaying) UnityEngine.Object.Destroy(value);
        else UnityEngine.Object.DestroyImmediate(value);
    }
}
