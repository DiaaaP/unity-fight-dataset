using System.IO;
using UnityEngine;

public static partial class DatasetBatchExporter
{
    private static DatasetFrameReadbackQueue readbackQueue;
    private static int submittedFrame;
    public static int PendingReadbackFrames => readbackQueue?.PendingCount ?? 0;
    public static bool UsesAsyncReadback => readbackQueue != null;

    private static void DrainReadbacks()
    {
        while (readbackQueue.TryPeekCompleted(out var frame))
        {
            if (yoloWriter != null && !yoloWriter.CanQueue(cameras.Length)) return;
            if (frame.index != CurrentFrame) throw new IOException("GPU frames arrived out of sequence.");
            for (int view = 0; view < cameras.Length; view++)
            {
                if (!encoders[view].AddFrame(config.width, config.height, config.width * 4, TextureFormat.RGBA32, frame.data[view]))
                    throw new IOException("Encoder rejected asynchronous frame " + frame.index);
                if (poseWriters != null) poseWriters[view].WriteLine(frame.projections[view]);
                yoloWriter?.Enqueue(cameras[view].id, frame.index, frame.data[view].ToArray(), frame.yoloLabels[view]);
                if (frame.inspection)
                {
                    pixels.LoadRawTextureData(frame.data[view]);
                    SaveInspection(view, frame.index);
                }
            }
            CurrentFrame++;
            readbackQueue.ReleaseHead(frame);
        }
    }

    private static void SaveInspection(int view, int frame) => File.WriteAllBytes(Path.Combine(LastBatchDirectory, "inspection",
        metadata.scenario_id + "_" + cameras[view].id + "_" + frame.ToString("D5") + ".png"), pixels.EncodeToPNG());
}
