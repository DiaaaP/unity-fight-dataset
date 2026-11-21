using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DatasetAsyncReadbackChecks
{
    [Serializable] public sealed class Report
    {
        public bool passed, boundedQueue, pendingDispose;
        public int frames, views = 2, comparedBytes;
        public long differingBytes;
        public string error;
    }
    private static DatasetFrameReadbackQueue queue;
    private static DatasetSceneFactory.Stage stage;
    private static Scene scene;
    private static DatasetCameraView[] cameras;
    private static Texture2D referencePixels;
    private static readonly Dictionary<int, byte[][]> references = new Dictionary<int, byte[][]>();
    private static Report report;
    private static int submitted, consumed;
    private static int delayedPolls;
    private static double started;
    public static bool IsRunning { get; private set; }
    private const string ReportPath = "DatasetOutput/Checks/async_readback_pixels.json";

    public static string Start()
    {
        if (IsRunning || DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Another test or export is active.");
        report = new Report(); references.Clear(); submitted = consumed = 0;
        delayedPolls = 8;
        var config = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        try
        {
            config.width = 320; config.height = 180; config.cameraViews = 2;
            config.generateHit = true;
            scene = EditorSceneManager.NewPreviewScene();
            stage = DatasetSceneFactory.Create(config, scene, 12345);
            stage.camera.enabled = false; stage.camera.scene = scene;
            stage.player.plan = DatasetScenarioPlanner.Create(config, stage, "hit", 12345);
            cameras = DatasetScenarioPlanner.Cameras(config, stage, 12345);
            queue = new DatasetFrameReadbackQueue(320, 180, 2);
            referencePixels = new Texture2D(320, 180, TextureFormat.RGBA32, false, false);
            FillQueue();
            report.boundedQueue = !queue.CanSubmit && queue.PendingCount == 3;
            if (!report.boundedQueue) throw new Exception("GPU queue did not enforce its capacity.");
            started = EditorApplication.timeSinceStartup;
            IsRunning = true;
            EditorApplication.update += Poll;
            return ReportPath;
        }
        catch (Exception exception) { Complete(exception); return ReportPath; }
        finally { UnityEngine.Object.DestroyImmediate(config); }
    }

    private static void FillQueue()
    {
        while (submitted < 6 && queue.CanSubmit)
        {
            stage.player.SampleAtTime(submitted * 0.6f);
            var frame = queue.BeginFrame(submitted, false);
            var expected = new byte[2][];
            for (int view = 0; view < 2; view++)
            {
                cameras[view].Apply(stage.camera);
                queue.CaptureView(frame, view, stage.camera);
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = frame.targets[view];
                    referencePixels.ReadPixels(new Rect(0, 0, 320, 180), 0, 0, false);
                    expected[view] = referencePixels.GetRawTextureData<byte>().ToArray();
                }
                finally { RenderTexture.active = previous; }
            }
            references.Add(submitted++, expected);
        }
    }

    private static void Poll()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 30) throw new Exception("Readback pixel test timed out.");
            if (delayedPolls-- > 0) return;
            while (queue.TryPeekCompleted(out var frame))
            {
                if (frame.index != consumed) throw new Exception("Frame order changed.");
                for (int view = 0; view < 2; view++)
                {
                    var expected = references[frame.index][view];
                    for (int i = 0; i < expected.Length; i++)
                        if (expected[i] != frame.data[view][i]) report.differingBytes++;
                    report.comparedBytes += expected.Length;
                }
                references.Remove(frame.index);
                consumed++; report.frames++;
                queue.ReleaseHead(frame);
            }
            FillQueue();
            if (consumed < 6) return;
            if (report.differingBytes != 0) throw new Exception("Async pixels differ from ReadPixels: " + report.differingBytes + " bytes.");
            // Dispose with freshly submitted native requests, before polling completion.
            var last = queue.BeginFrame(6, false);
            for (int view = 0; view < 2; view++) queue.CaptureView(last, view, stage.camera);
            queue.Dispose(); queue = null;
            report.pendingDispose = !Resources.FindObjectsOfTypeAll<RenderTexture>().Any(t => t.name.StartsWith("DatasetReadback_", StringComparison.Ordinal));
            if (!report.pendingDispose) throw new Exception("GPU targets leaked after disposal.");
            report.passed = true;
            Complete(null);
        }
        catch (Exception exception) { Complete(exception); }
    }

    private static void Complete(Exception exception)
    {
        EditorApplication.update -= Poll;
        queue?.Dispose(); queue = null;
        if (scene.IsValid()) DatasetSceneFactory.ClosePreviewScene(scene);
        if (referencePixels) UnityEngine.Object.DestroyImmediate(referencePixels);
        references.Clear(); IsRunning = false;
        report.error = exception?.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
    }
}
