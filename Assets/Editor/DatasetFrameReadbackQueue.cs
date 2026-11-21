using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class DatasetFrameReadbackQueue : IDisposable
{
    public sealed class ReadbackException : Exception { public ReadbackException(string message) : base(message) { } }

    public sealed class Frame
    {
        public int index;
        public bool inspection;
        public readonly string[] projections;
        public readonly string[] yoloLabels;
        public readonly NativeArray<byte>[] data;
        public readonly RenderTexture[] targets;
        internal readonly AsyncGPUReadbackRequest[] requests;
        internal readonly bool[] requested;
        internal readonly bool[] completed, errors;
        internal int generation;
        internal double submittedAt;

        internal Frame(int views)
        {
            projections = new string[views]; data = new NativeArray<byte>[views];
            yoloLabels = new string[views];
            targets = new RenderTexture[views]; requests = new AsyncGPUReadbackRequest[views]; requested = new bool[views];
            completed = new bool[views]; errors = new bool[views];
        }
    }

    private readonly List<Frame> slots = new List<Frame>();
    private readonly Stack<Frame> free = new Stack<Frame>();
    private readonly Queue<Frame> pending = new Queue<Frame>();
    private readonly int views;
    private bool disposed;
    public bool CanSubmit => !disposed && free.Count > 0;
    public int PendingCount => pending.Count;

    public DatasetFrameReadbackQueue(int width, int height, int views, int capacity = 3)
    {
        if (width < 1 || height < 1 || views < 1 || views > 3 || capacity < 1 || capacity > 4)
            throw new ArgumentOutOfRangeException();
        this.views = views;
        try
        {
            for (int i = 0; i < capacity; i++)
            {
                var frame = new Frame(views);
                slots.Add(frame);
                for (int view = 0; view < views; view++)
                {
                    frame.targets[view] = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                        { name = "DatasetReadback_" + i + "_" + view };
                    if (!frame.targets[view].Create()) throw new ReadbackException("Cannot create readback render target.");
                    frame.data[view] = new NativeArray<byte>(checked(width * height * 4), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
                free.Push(frame);
            }
        }
        catch { Dispose(); throw; }
    }

    public Frame BeginFrame(int index, bool inspection)
    {
        if (!CanSubmit) throw new InvalidOperationException("Readback queue is full.");
        var frame = free.Pop();
        frame.index = index; frame.inspection = inspection; frame.submittedAt = EditorApplication.timeSinceStartup;
        frame.generation++;
        Array.Clear(frame.requested, 0, views);
        Array.Clear(frame.completed, 0, views);
        Array.Clear(frame.errors, 0, views);
        Array.Clear(frame.projections, 0, views);
        Array.Clear(frame.yoloLabels, 0, views);
        pending.Enqueue(frame);
        return frame;
    }

    public void CaptureView(Frame frame, int view, Camera camera)
    {
        if (frame.requested[view]) throw new InvalidOperationException("View already captured.");
        var previous = camera.targetTexture;
        try
        {
            camera.targetTexture = frame.targets[view];
            camera.Render();
            int generation = frame.generation;
            frame.requests[view] = AsyncGPUReadback.RequestIntoNativeArray(ref frame.data[view], frame.targets[view], 0, TextureFormat.RGBA32, request =>
            {
                // Request handles expire after completion. Keep stable completion flags;
                // encoding and all resource lifecycle operations stay in Editor update.
                if (disposed || frame.generation != generation) return;
                frame.errors[view] = request.hasError;
                System.Threading.Volatile.Write(ref frame.completed[view], true);
            });
            frame.requested[view] = true;
            // Export is driven by Editor update; a full Player loop per request
            // needlessly redraws the open scene. Poll native requests directly.
            frame.requests[view].forcePlayerLoopUpdate = false;
        }
        finally { camera.targetTexture = previous; }
    }

    public bool TryPeekCompleted(out Frame frame)
    {
        frame = null;
        if (pending.Count == 0) return false;
        var head = pending.Peek();
        for (int view = 0; view < views; view++)
        {
            if (!head.requested[view]) throw new ReadbackException("Incomplete readback submission.");
            if (!System.Threading.Volatile.Read(ref head.completed[view])) head.requests[view].Update();
            if (!System.Threading.Volatile.Read(ref head.completed[view]))
            {
                if (EditorApplication.timeSinceStartup - head.submittedAt > 30) throw new ReadbackException("GPU readback timed out.");
                return false;
            }
            if (head.errors[view]) throw new ReadbackException("GPU readback failed; later scenarios use synchronous capture.");
        }
        frame = head;
        return true;
    }

    public void ReleaseHead(Frame frame)
    {
        if (pending.Count == 0 || !ReferenceEquals(frame, pending.Peek())) throw new InvalidOperationException("Readback order changed.");
        pending.Dequeue();
        Array.Clear(frame.requested, 0, views);
        free.Push(frame);
    }

    public void ResetTimeouts()
    {
        foreach (var frame in pending) frame.submittedAt = EditorApplication.timeSinceStartup;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var frame in slots)
            for (int view = 0; view < views; view++)
                if (frame.requested[view] && !frame.requests[view].done) frame.requests[view].WaitForCompletion();
        foreach (var frame in slots)
            for (int view = 0; view < views; view++)
            {
                if (frame.data[view].IsCreated) frame.data[view].Dispose();
                if (frame.targets[view])
                {
                    frame.targets[view].Release();
                    UnityEngine.Object.DestroyImmediate(frame.targets[view]);
                }
            }
        pending.Clear(); free.Clear(); slots.Clear();
    }
}
