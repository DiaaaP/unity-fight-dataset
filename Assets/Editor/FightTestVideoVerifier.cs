using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

/// <summary>Decode exported MP4 through Unity's native video player without displaying it.</summary>
public static class FightTestVideoVerifier
{
    public static string Status { get; private set; } = "Not started";
    public static int DecodedFrames { get; private set; }
    private static VideoPlayer player;
    private static RenderTexture target;
    private static Texture2D pixels;
    private static double started;
    private static string videoPath;
    private static Color32[] reference;
    private static float maximumChange;
    private static float firstFrameContrast;
    private static long lastFrame;
    private static bool saveInspectionFrames;

    public static void Begin(string path, bool inspectFrames = false)
    {
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Verify in Play mode.");
        if (player) throw new InvalidOperationException("Video verification already running.");
        if (!File.Exists(path)) throw new FileNotFoundException("Video missing", path);
        videoPath = path;
        saveInspectionFrames = inspectFrames;
        DecodedFrames = 0;
        maximumChange = 0f;
        reference = null;
        lastFrame = -1;
        started = EditorApplication.timeSinceStartup;
        target = new RenderTexture(1280, 720, 0, RenderTextureFormat.ARGB32);
        target.Create();
        pixels = new Texture2D(1280, 720, TextureFormat.RGBA32, false);
        var host = new GameObject("Temporary MP4 Decode Check") { hideFlags = HideFlags.HideAndDontSave };
        player = host.AddComponent<VideoPlayer>();
        player.playOnAwake = false;
        player.source = VideoSource.Url;
        player.url = path;
        player.renderMode = VideoRenderMode.RenderTexture;
        player.targetTexture = target;
        player.audioOutputMode = VideoAudioOutputMode.None;
        player.waitForFirstFrame = true;
        player.skipOnDrop = false;
        player.isLooping = false;
        player.sendFrameReadyEvents = true;
        player.prepareCompleted += Prepared;
        player.frameReady += Frame;
        player.loopPointReached += Finished;
        player.errorReceived += Error;
        EditorApplication.update += Watchdog;
        Status = "Preparing";
        player.Prepare();
    }

    private static void Prepared(VideoPlayer video)
    {
        if (video.width != 1280 || video.height != 720 || video.frameCount != 100 ||
            Math.Abs(video.frameRate - 20) > 0.01 || Math.Abs(video.length - 5) > 0.06)
        {
            EditorApplication.delayCall += () => Fail("Unexpected video dimensions, FPS, duration or frame count.");
            return;
        }
        Status = "Decoding";
        EditorApplication.delayCall += () => { if (player) player.Play(); };
    }

    private static void Frame(VideoPlayer video, long frame)
    {
        if (frame <= lastFrame) return;
        lastFrame = frame;
        DecodedFrames++;
        if (frame != 0 && frame != 10 && frame != 30 && frame != 40 && frame != 46 && frame != 60 && frame != 99) return;
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = target;
            pixels.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0, false);
            pixels.Apply(false, false);
            if (saveInspectionFrames)
            {
                string folder = Path.Combine(Path.GetDirectoryName(videoPath), Path.GetFileNameWithoutExtension(videoPath) + "_inspection");
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"frame_{frame:D3}.png"), pixels.EncodeToPNG());
            }
            var data = pixels.GetPixels32();
            if (reference == null)
            {
                reference = data;
                int minimum = 255, maximum = 0;
                for (int i = 0; i < data.Length; i += 17)
                {
                    int light = (data[i].r + data[i].g + data[i].b) / 3;
                    minimum = Math.Min(minimum, light);
                    maximum = Math.Max(maximum, light);
                }
                firstFrameContrast = maximum - minimum;
            }
            else
            {
                double difference = 0;
                int count = 0;
                for (int i = 0; i < data.Length; i += 17)
                {
                    difference += Math.Abs(data[i].r - reference[i].r) +
                        Math.Abs(data[i].g - reference[i].g) + Math.Abs(data[i].b - reference[i].b);
                    count += 3;
                }
                maximumChange = Mathf.Max(maximumChange, (float)(difference / count));
            }
        }
        catch (Exception exception) { EditorApplication.delayCall += () => Fail(exception.Message); }
        finally { RenderTexture.active = previous; }
    }

    private static void Finished(VideoPlayer video)
    {
        EditorApplication.delayCall += FinishAfterNativeCallback;
    }

    private static void FinishAfterNativeCallback()
    {
        if (DecodedFrames != 100 || firstFrameContrast < 20 || maximumChange < 0.1f)
        {
            Fail($"Decoded={DecodedFrames}; contrast={firstFrameContrast}; motion={maximumChange}");
            return;
        }
        try { FightTestVideoExporter.MarkDecodeVerified(videoPath); }
        catch (Exception exception) { Fail(exception.Message); return; }
        var report = new Report
        {
            video = Path.GetFileName(videoPath),
            decoded_frames = DecodedFrames,
            first_frame_contrast = firstFrameContrast,
            maximum_mean_pixel_change = maximumChange
        };
        string reportPath = Path.ChangeExtension(videoPath, ".validation.json");
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        Status = "Passed";
        Cleanup();
    }

    private static void Error(VideoPlayer video, string message) => EditorApplication.delayCall += () => Fail(message);
    private static void Watchdog()
    {
        if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup - started > 30)
            Fail("Video decode interrupted or timed out.");
    }
    private static void Fail(string message)
    {
        Status = "Failed: " + message;
        Debug.LogError(Status);
        Cleanup();
    }
    private static void Cleanup()
    {
        EditorApplication.update -= Watchdog;
        if (player)
        {
            player.prepareCompleted -= Prepared;
            player.frameReady -= Frame;
            player.loopPointReached -= Finished;
            player.errorReceived -= Error;
            player.targetTexture = null;
            player.Stop();
            if (EditorApplication.isPlaying) UnityEngine.Object.Destroy(player.gameObject);
            else UnityEngine.Object.DestroyImmediate(player.gameObject);
        }
        player = null;
        if (target) { if (EditorApplication.isPlaying) UnityEngine.Object.Destroy(target); else UnityEngine.Object.DestroyImmediate(target); }
        if (pixels) { if (EditorApplication.isPlaying) UnityEngine.Object.Destroy(pixels); else UnityEngine.Object.DestroyImmediate(pixels); }
        target = null;
        pixels = null;
    }

    [Serializable] private sealed class Report
    {
        public bool passed = true;
        public string video;
        public int decoded_frames, width = 1280, height = 720, fps = 20;
        public float duration_seconds = 5f, first_frame_contrast, maximum_mean_pixel_change;
    }
}
