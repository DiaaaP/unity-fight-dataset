using System;
using FightDataset;
using UnityEngine;

public static class DatasetReactionTiming
{
    public static float Deflection(DatasetScenarioPlayer player, ScenarioAction action, float time)
    {
        var actual = ResponsePose.Sample(player, action, time);
        var reaction = action.reaction;
        ResponsePose neutral;
        try
        {
            action.reaction = null;
            neutral = ResponsePose.Sample(player, action, time);
        }
        finally { action.reaction = reaction; }
        player.SampleAtTime(time);
        return actual.Distance(neutral);
    }

    private struct ResponsePose
    {
        private Vector3 head, chest, hips;
        private Quaternion rotation;
        public static ResponsePose Sample(DatasetScenarioPlayer player, ScenarioAction action, float time)
        {
            player.SampleAtTime(time);
            var actor = player.actors[action.target];
            var h = actor.GetBoneTransform(HumanBodyBones.Head);
            return new ResponsePose { head = h.position, rotation = h.rotation,
                chest = actor.GetBoneTransform(HumanBodyBones.Chest).position,
                hips = actor.GetBoneTransform(HumanBodyBones.Hips).position };
        }
        public float Distance(ResponsePose other) => Mathf.Max(
            Mathf.Max(Vector3.Distance(head, other.head), Vector3.Distance(chest, other.chest)),
            Mathf.Max(Vector3.Distance(hips, other.hips), Quaternion.Angle(rotation, other.rotation) * .002f));
    }

    public static void Configure(DatasetGeneratorConfig config, DatasetScenarioPlayer player, ScenarioAction action)
    {
        action.reactionStart = action.impactTime;
        action.reactionFadeIn = Mathf.Clamp(player.plan.reactionBlendIn * 0.5f, 0.08f, 0.12f);
        float maximumOffset = Mathf.Min(0.6f, action.reaction.length * 0.4f);
        float t1 = action.impactTime + 1f / config.fps, t2 = action.impactTime + 2f / config.fps;
        // With this reaction disabled its source offset cannot affect either reference pose.
        ResponsePose ref1, ref2;
        var reaction = action.reaction;
        try
        {
            action.reaction = null;
            ref1 = ResponsePose.Sample(player, action, t1);
            ref2 = ResponsePose.Sample(player, action, t2);
        }
        finally { action.reaction = reaction; }
        for (float offset = 0; offset <= maximumOffset + 0.0001f; offset += 1f / 60)
        {
            action.reactionSourceOffset = offset;
            float first = ref1.Distance(ResponsePose.Sample(player, action, t1));
            if (first > 0.18f) continue;
            if (first < 0.015f && ref2.Distance(ResponsePose.Sample(player, action, t2)) < 0.03f) continue;
            if (Deflection(player, action, action.impactTime - 1f / config.fps) > 0.001f)
                throw new InvalidOperationException("Reaction moves before actual contact.");
            action.reactionResponseFrame = action.impactFrame + (first >= 0.015f ? 1 : 2);
            action.reactionResponseSeconds = action.reactionResponseFrame / (float)config.fps;
            player.SampleAtTime(action.impactTime);
            return;
        }
        throw new InvalidOperationException("No visible response within two frames after the main strike: " + action.reactionAsset);
    }

    public static void Verify(DatasetGeneratorConfig config, DatasetScenarioPlayer player, ScenarioAction action)
    {
        if (action.reactionStart < action.impactTime - 0.0001f || action.reactionStart > action.impactTime + 0.0001f ||
            Deflection(player, action, action.impactTime - 1f / config.fps) > 0.001f ||
            (Deflection(player, action, action.impactTime + 1f / config.fps) < 0.015f &&
             Deflection(player, action, action.impactTime + 2f / config.fps) < 0.03f))
            throw new InvalidOperationException("Reaction is early or visibly late relative to the actual strike.");
        player.SampleAtTime(action.impactTime);
    }
}
