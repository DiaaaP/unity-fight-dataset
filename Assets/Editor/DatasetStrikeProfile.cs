using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FightDataset;
using UnityEngine;

public sealed class DatasetStrikeProfile
{
    private sealed class Stroke
    {
        public int limb, first, last, peak;
        public float score, radius;
    }
    private static readonly Dictionary<string, DatasetStrikeProfile> Cache = new Dictionary<string, DatasetStrikeProfile>();
    public HumanBodyBones limb;
    public float first, last, peak, sourceEnd, yaw, peakSpeed, radius;
    private Vector3[] points;
    private float step;

    public static DatasetStrikeProfile Get(DatasetScenarioPlayer player, ScenarioAction action)
    {
        var actor = player.actors[action.actor];
        string path = UnityEditor.AssetDatabase.GetAssetPath(action.clip);
        string revision = string.IsNullOrEmpty(path) ? "transient" : UnityEditor.AssetDatabase.GetAssetDependencyHash(path).ToString();
        string key = actor.avatar.GetInstanceID() + ":" + action.clip.GetInstanceID() + ":" + actor.transform.lossyScale.ToString("F5") + ":" + revision;
        if (!Cache.TryGetValue(key, out var result)) Cache[key] = result = Build(player, action);
        return result;
    }

    public float ExtensionRatio(float time)
    {
        float f = Mathf.Clamp(time / step, 0, points.Length - 1);
        var p = Vector3.Lerp(points[Mathf.FloorToInt(f)], points[Mathf.Min(points.Length - 1, Mathf.FloorToInt(f) + 1)], f % 1);
        return new Vector2(p.x, p.z).magnitude / radius;
    }

    public bool MainPhase(float time, float sourceFrameStep) => time >= first - 0.001f &&
        time <= last + Mathf.Min(0.025f, sourceFrameStep * 0.35f) && ExtensionRatio(time) >= 0.94f;

    private static DatasetStrikeProfile Build(DatasetScenarioPlayer player, ScenarioAction action)
    {
        var actor = player.actors[action.actor];
        var limbs = action.actorAction == "kick" ? new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }
            : new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand };
        int count = Mathf.Clamp(Mathf.CeilToInt(action.clip.length * 60) + 1, 30, 600);
        float step = action.clip.length / (count - 1);
        var points = new[] { new Vector3[count], new Vector3[count] };
        var velocities = new[] { new Vector3[count], new Vector3[count] };
        var radii = new[] { new float[count], new float[count] };
        for (int i = 0; i < count; i++)
        {
            player.SampleActorSource(action.actor, action.clip, i * step);
            var inverse = Quaternion.Inverse(actor.transform.rotation);
            for (int l = 0; l < 2; l++)
            {
                points[l][i] = inverse * (DatasetContactSolver.LimbPoint(actor, limbs[l]) - actor.transform.position);
                radii[l][i] = new Vector2(points[l][i].x, points[l][i].z).magnitude;
            }
        }
        var strokes = new List<Stroke>();
        int begin = Mathf.Max(2, Mathf.CeilToInt(count * 0.15f)), finish = Mathf.Min(count - 3, Mathf.FloorToInt(count * 0.87f));
        for (int l = 0; l < 2; l++)
        {
            for (int i = 1; i < count - 1; i++) velocities[l][i] = (points[l][i + 1] - points[l][i - 1]) / (2 * step);
            float maximum = Enumerable.Range(begin, finish - begin + 1).Max(i => velocities[l][i].z);
            if (maximum < 0.3f) continue;
            for (int i = begin; i <= finish; i++)
            {
                if (velocities[l][i].z < maximum * 0.2f) continue;
                int first = i, peakVelocity = i;
                while (i < finish && velocities[l][i + 1].z >= maximum * 0.2f)
                { i++; if (velocities[l][i].z > velocities[l][peakVelocity].z) peakVelocity = i; }
                int last = i;
                if (last - first < 1 || velocities[l][peakVelocity].z < maximum * 0.45f) continue;
                int peak = peakVelocity;
                int low = Mathf.Max(begin, peakVelocity - Mathf.CeilToInt(0.08f / step));
                int high = Mathf.Min(finish, last + Mathf.CeilToInt(0.15f / step));
                for (int j = low; j <= high; j++) if (radii[l][j] > radii[l][peak]) peak = j;
                last = Mathf.Max(peakVelocity, peak);
                float radius = radii[l][peak];
                if (radius < (action.actorAction == "kick" ? 0.40f : 0.32f)) continue;
                strokes.Add(new Stroke { limb = l, first = first, last = last, peak = peak,
                    radius = radius, score = velocities[l][peakVelocity].z * radius });
            }
        }
        if (strokes.Count == 0) throw new InvalidOperationException("No main outgoing strike phase: " + action.assetPath);
        float maximumScore = strokes.Max(s => s.score), maximumRadius = strokes.OrderByDescending(s => s.score).First().radius;
        var significant = strokes.Where(s => s.score >= maximumScore * 0.3f && s.radius >= maximumRadius * 0.7f).OrderBy(s => s.first).ToArray();
        int clusterEnd = significant[0].last;
        var cluster = new List<Stroke> { significant[0] };
        int next = 1;
        while (next < significant.Length && significant[next].first * step <= clusterEnd * step + 0.22f)
        { cluster.Add(significant[next]); clusterEnd = Mathf.Max(clusterEnd, significant[next].last); next++; }
        var main = cluster.OrderByDescending(s => s.score).First();
        float end = action.clip.length;
        if (next < significant.Length)
            end = Mathf.Min(end, (clusterEnd + significant[next].first) * step * 0.5f);
        Vector3 aim = points[main.limb][main.peak];
        var result = new DatasetStrikeProfile { limb = limbs[main.limb], first = main.first * step, last = main.last * step,
            peak = main.peak * step, sourceEnd = end, radius = main.radius, points = points[main.limb], step = step,
            yaw = -Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg,
            peakSpeed = Enumerable.Range(main.first, main.last - main.first + 1).Max(i => velocities[main.limb][i].magnitude) };
        if (Mathf.Abs(result.yaw) > 110) throw new InvalidOperationException("Strike points away from the opponent: " + action.assetPath);
        player.SampleAtTime(0);
        return result;
    }
}
