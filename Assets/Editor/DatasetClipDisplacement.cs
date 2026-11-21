using System.Collections.Generic;
using System.Globalization;
using FightDataset;
using UnityEditor;
using UnityEngine;

public static class DatasetClipDisplacement
{
    private static readonly Dictionary<string, Vector3> Cache = new Dictionary<string, Vector3>();

    public static Vector3 Get(DatasetScenarioPlayer player, int actorIndex, AnimationClip clip, float sourceEnd)
    {
        var actor = player.actors[actorIndex];
        string path = AssetDatabase.GetAssetPath(clip);
        string revision = string.IsNullOrEmpty(path) ? "transient" : AssetDatabase.GetAssetDependencyHash(path).ToString();
        string key = actor.avatar.GetInstanceID() + ":" + clip.GetInstanceID() + ":" + actor.transform.lossyScale.ToString("F5") +
            ":" + sourceEnd.ToString("R", CultureInfo.InvariantCulture) + ":" + revision;
        if (Cache.TryGetValue(key, out var displacement)) return displacement;
        // Body Transform projects to the horizontal root trajectory. Sample in
        // canonical space; never integrate deltas from previously visited frames.
        using (var handler = new HumanPoseHandler(actor.avatar, actor.transform))
        {
            var pose = new HumanPose();
            player.SampleActorSource(actorIndex, clip, 0);
            handler.GetHumanPose(ref pose);
            Vector3 first = pose.bodyPosition;
            player.SampleActorSource(actorIndex, clip, sourceEnd);
            handler.GetHumanPose(ref pose);
            // HumanPose uses avatar-normalized coordinates, before root scale.
            // Canonical sampling sets world rotation to identity; convert to metres.
            displacement = actor.transform.TransformVector((pose.bodyPosition - first) * actor.humanScale);
            displacement.y = 0;
        }
        Cache[key] = displacement;
        return displacement;
    }
}
