using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Unity.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

// Only the documented thread-safe array encoder and file I/O run on workers.
// Raw buffers are copied before GPU slots can be reused. The queue is bounded.
public sealed class DatasetYoloWriter : IDisposable
{
    private readonly ConcurrentQueue<byte[]> freeBuffers = new ConcurrentQueue<byte[]>();
    private readonly Queue<Task> jobs = new Queue<Task>();
    private readonly string root, pending, split, id;
    private readonly int width, height, quality;
    private int written;
    public DatasetYoloWriter(string batch, string scenario, string split, int width, int height, int quality)
    {
        root = Path.Combine(batch, "yolo"); id = scenario; this.split = split;
        this.width = width; this.height = height; this.quality = quality;
        pending = Path.Combine(root, ".pending", id);
        Directory.CreateDirectory(Path.Combine(pending, "images"));
        Directory.CreateDirectory(Path.Combine(pending, "labels"));
    }
    public static void Initialize(string batch)
    {
        string root = Path.Combine(batch, "yolo");
        foreach (string split in new[] { "train", "val", "test" })
            foreach (string kind in new[] { "images", "labels" }) Directory.CreateDirectory(Path.Combine(root, kind, split));
        // An absolute dataset root makes this YAML independent of the training process CWD.
        string yaml = "path: '" + Path.GetFullPath(root).Replace('\\', '/').Replace("'", "''") + "'\n" +
            "train: images/train\nval: images/val\ntest: images/test\n" +
            "kpt_shape: [17, 3]\nflip_idx: [0, 2, 1, 4, 3, 6, 5, 8, 7, 10, 9, 12, 11, 14, 13, 16, 15]\nnames:\n  0: person\n";
        DatasetBatchCheckpoint.WriteAtomic(Path.Combine(root, "data.yaml"), yaml);
        DatasetBatchCheckpoint.WriteAtomic(Path.Combine(root, "data_all_frames.yaml"), yaml);
    }
    public bool CanQueue(int count)
    {
        while (jobs.Count > 0 && jobs.Peek().IsCompleted) jobs.Dequeue().GetAwaiter().GetResult();
        return jobs.Count + count <= 6;
    }
    public void Enqueue(string camera, int frame, byte[] rgba, string labels)
    {
        CheckInput(rgba.Length);
        QueueWrite(camera, frame, rgba, labels, false);
    }

    public void Enqueue(string camera, int frame, NativeArray<byte> pixels, string labels)
    {
        CheckInput(pixels.Length);
        if (!freeBuffers.TryDequeue(out var buffer)) buffer = new byte[pixels.Length];
        pixels.CopyTo(buffer);
        QueueWrite(camera, frame, buffer, labels, true);
    }

    private void CheckInput(int bytes)
    {
        if (!CanQueue(1)) throw new InvalidOperationException("YOLO image queue is full.");
        if (bytes != checked(width * height * 4)) throw new InvalidDataException("Unexpected pose image size.");
    }

    private void QueueWrite(string camera, int frame, byte[] rgba, string labels, bool recycle)
    {
        string stem = id + "_" + camera + "_" + frame.ToString("D5");
        jobs.Enqueue(Task.Run(() =>
        {
            try
            {
                byte[] jpeg = ImageConversion.EncodeArrayToJPG(rgba, GraphicsFormat.R8G8B8A8_UNorm,
                    (uint)width, (uint)height, (uint)(width * 4), quality);
                if (jpeg == null || jpeg.Length == 0) throw new IOException("JPEG encoder returned no image.");
                File.WriteAllBytes(Path.Combine(pending, "images", stem + ".jpg"), jpeg);
                File.WriteAllText(Path.Combine(pending, "labels", stem + ".txt"), labels, new UTF8Encoding(false));
            }
            finally { if (recycle) freeBuffers.Enqueue(rgba); }
        }));
        written++;
    }
    public int Commit(int expected)
    {
        Flush();
        if (written != expected || Directory.GetFiles(Path.Combine(pending, "images")).Length != expected ||
            Directory.GetFiles(Path.Combine(pending, "labels")).Length != expected)
            throw new IOException("Incomplete YOLO scenario; nothing is committed to the manifest.");
        foreach (string kind in new[] { "images", "labels" })
            foreach (string file in Directory.GetFiles(Path.Combine(pending, kind)))
                File.Move(file, Path.Combine(root, kind, split, Path.GetFileName(file)));
        Directory.Delete(Path.Combine(pending, "images"));
        Directory.Delete(Path.Combine(pending, "labels"));
        Directory.Delete(pending);
        return written;
    }
    public void Flush()
    {
        Exception failure = null;
        while (jobs.Count > 0)
            try { jobs.Dequeue().GetAwaiter().GetResult(); } catch (Exception e) { failure = failure ?? e; }
        if (failure != null) throw new IOException("Failed to save YOLO image/label pair.", failure);
    }
    public void Dispose()
    {
        // Cleanup must release the Unity scene even after an I/O error. Commit/CanQueue
        // propagate worker faults during normal exporting; unfinished files stay pending.
        try { Flush(); } catch { }
    }
}
