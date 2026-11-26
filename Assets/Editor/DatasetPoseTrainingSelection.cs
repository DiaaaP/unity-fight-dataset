using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEngine;

// Keep full synchronized source frames. Train from explicit, reproducible image
// lists so a large pile of adjacent Idle JPGs is not treated as useful diversity.
public static class DatasetPoseTrainingSelection
{
    [Serializable] public sealed class Image
    {
        public string image, split, scenario, camera;
        public int frame, other_person_occluded_body_points;
        public bool occluded, event_frame;
    }
    [Serializable] public sealed class Report
    {
        public string schema = "pose_training_selection_v1";
        public string definition = "at_least_two_in_frame_body_keypoints_blocked_by_the_other_actor_mesh";
        public string warning;
        public bool target_met;
        public float target_fraction, actual_fraction;
        public int source_frames, candidate_frames, selected_frames, selected_occluded_frames, source_occluded_frames;
        public List<Image> selected = new List<Image>();
    }
    public static Report Write(string batch, DatasetGeneratorConfig config, DatasetBatchExporter.Summary summary)
    {
        var candidates = new List<Image>(); var result = new Report { target_fraction = config.targetOccludedTrainingFraction };
        foreach (var entry in summary.scenarios)
        {
            var meta = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(System.IO.Path.Combine(batch, entry.metadata)));
            for (int view = 0; view < meta.cameras.Length; view++)
            {
                int stride = Mathf.Max(1, Mathf.RoundToInt(meta.fps / (float)config.poseTrainingSamplesPerSecond));
                var mandatory = new HashSet<int> { 0, meta.frame_count - 1 };
                var eventFrames = new HashSet<int>();
                foreach (var action in meta.actions)
                {
                    for (int i = -2; i <= 2; i++) { int f = Mathf.Clamp(action.impact_frame + i, 0, meta.frame_count - 1); mandatory.Add(f); eventFrames.Add(f); }
                    mandatory.Add(Mathf.Clamp(action.reaction_visible_frame, 0, meta.frame_count - 1));
                }
                float[][] previous = null; int lastSelected = -meta.fps;
                for (int frame = 0; frame < meta.frame_count; frame++)
                {
                    int key = view * meta.frame_count + frame;
                    int hidden = meta.other_person_occluded_body_points != null && meta.other_person_occluded_body_points.Length > key ? meta.other_person_occluded_body_points[key] : 0;
                    bool hard = hidden >= 2; result.source_frames++; if (hard) result.source_occluded_frames++;
                    if (frame % stride != 0 && !mandatory.Contains(frame)) continue;
                    string stem = meta.scenario_id + "_" + meta.cameras[view].id + "_" + frame.ToString("D5");
                    string relative = "images/" + meta.split + "/" + stem + ".jpg";
                    string image = System.IO.Path.Combine(batch, "yolo", relative);
                    string label = System.IO.Path.Combine(batch, "yolo", "labels", meta.split, stem + ".txt");
                    if (!File.Exists(image) || !File.Exists(label)) throw new IOException("Cannot index missing committed image/label pair: " + stem);
                    var rows = File.ReadAllLines(label).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToArray();
                    bool changed = previous == null || Changed(previous, rows);
                    if (!mandatory.Contains(frame) && !changed && frame - lastSelected < meta.fps / 2) continue;
                    previous = rows; lastSelected = frame;
                    candidates.Add(new Image { image = relative, split = meta.split, scenario = meta.scenario_id, camera = meta.cameras[view].id,
                        frame = frame, occluded = hard, event_frame = eventFrames.Contains(frame), other_person_occluded_body_points = hidden });
                }
            }
        }
        result.candidate_frames = candidates.Count;
        var normal = Spread(candidates.Where(c => !c.occluded)); var hardFrames = Spread(candidates.Where(c => c.occluded));
        int numerator = Mathf.RoundToInt(config.targetOccludedTrainingFraction * 1000), denominator = 1000;
        int gcd = Gcd(numerator, denominator); int hardUnit = numerator / gcd, normalUnit = (denominator - numerator) / gcd;
        if (numerator == 0) result.selected = candidates;
        else
        {
            int units = Mathf.Min(hardFrames.Count / hardUnit, normal.Count / normalUnit);
            if (units > 0)
            {
                result.selected.AddRange(normal.Take(units * normalUnit)); result.selected.AddRange(hardFrames.Take(units * hardUnit));
            }
            else
            {
                result.selected = candidates;
                result.warning = "Insufficient real inter-person occlusions/non-occlusions to achieve the requested fraction without duplicate images. Generate more varied/occlusion views; target is not met.";
            }
        }
        result.selected = result.selected.OrderBy(i => i.split, StringComparer.Ordinal).ThenBy(i => i.image, StringComparer.Ordinal).ToList();
        result.selected_frames = result.selected.Count; result.selected_occluded_frames = result.selected.Count(i => i.occluded);
        result.actual_fraction = result.selected_frames == 0 ? 0 : result.selected_occluded_frames / (float)result.selected_frames;
        result.target_met = result.selected_frames > 0 && (numerator == 0 || Mathf.Abs(result.actual_fraction - config.targetOccludedTrainingFraction) < .000001f);
        string root = System.IO.Path.Combine(batch, "yolo");
        foreach (string split in new[] { "train", "val", "test" })
        {
            string lines = string.Join("\n", result.selected.Where(i => i.split == split).Select(i => "./" + i.image));
            DatasetBatchCheckpoint.WriteAtomic(System.IO.Path.Combine(root, split + "_images.txt"), lines.Length > 0 ? lines + "\n" : "");
        }
        DatasetBatchCheckpoint.WriteAtomic(System.IO.Path.Combine(root, "selection.json"), JsonUtility.ToJson(result, true));
        string yaml = File.ReadAllText(System.IO.Path.Combine(root, "data_all_frames.yaml"))
            .Replace("train: images/train", "train: train_images.txt").Replace("val: images/val", "val: val_images.txt").Replace("test: images/test", "test: test_images.txt");
        DatasetBatchCheckpoint.WriteAtomic(System.IO.Path.Combine(root, "data.yaml"), yaml);
        return result;
    }
    private static bool Changed(float[][] a, float[][] b)
    {
        if (a.Length != b.Length) return true;
        for (int person = 0; person < b.Length; person++)
        {
            if (a[person].Length != 56 || b[person].Length != 56) throw new InvalidDataException("Expected a COCO17 pose row.");
            for (int k = 5; k < 17; k++)
            {
                int i = 5 + k * 3;
                if (a[person][i + 2] != b[person][i + 2]) return true;
                if (b[person][i + 2] == 0) continue;
                float dx = b[person][i] - a[person][i], dy = b[person][i + 1] - a[person][i + 1];
                if (dx * dx + dy * dy >= .0001f) return true;
            }
        }
        return false;
    }
    private static List<Image> Spread(IEnumerable<Image> source)
    {
        var groups = source.GroupBy(i => i.scenario + "_" + i.camera).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new Queue<Image>(g.OrderByDescending(i => i.event_frame).ThenBy(i => unchecked((uint)i.frame * 2654435761u)))).ToArray();
        var result = new List<Image>();
        while (groups.Any(g => g.Count > 0)) foreach (var group in groups) if (group.Count > 0) result.Add(group.Dequeue());
        return result;
    }
    private static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }
}
