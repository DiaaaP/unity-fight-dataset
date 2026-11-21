using System;
using System.Linq;
using FightDataset;
using UnityEngine;

public static class DatasetReactionTiming
{
    private static readonly HumanBodyBones[] Bones = { HumanBodyBones.Head, HumanBodyBones.Chest, HumanBodyBones.Hips };

    public static float Deflection(DatasetScenarioPlayer player, ScenarioAction action, float time)
    {
        var actor = player.actors[action.target];
        var reaction = action.reaction;
        player.SampleAtTime(time);
        var positions = Bones.Select(b => actor.GetBoneTransform(b).position).ToArray();
        var rotation = actor.GetBoneTransform(HumanBodyBones.Head).rotation;
        try { action.reaction = null; player.SampleAtTime(time); }
        finally { action.reaction = reaction; }
        float delta = Bones.Select((b, i) => Vector3.Distance(positions[i], actor.GetBoneTransform(b).position)).Max();
        delta = Mathf.Max(delta, Quaternion.Angle(rotation, actor.GetBoneTransform(HumanBodyBones.Head).rotation) * 0.002f);
        player.SampleAtTime(time);
        return delta;
    }

    public static void Configure(DatasetGeneratorConfig config, DatasetScenarioPlayer player, ScenarioAction action)
    {
        action.reactionStart = action.impactTime;
        action.reactionFadeIn = Mathf.Clamp(player.plan.reactionBlendIn * 0.5f, 0.08f, 0.12f);
        float maximumOffset = Mathf.Min(0.6f, action.reaction.length * 0.4f);
        for (float offset = 0; offset <= maximumOffset + 0.0001f; offset += 1f / 60)
        {
            action.reactionSourceOffset = offset;
            float first = Deflection(player, action, action.impactTime + 1f / config.fps);
            float second = Deflection(player, action, action.impactTime + 2f / config.fps);
            if (first < 0.015f && second < 0.03f) continue;
            if (first > 0.18f) continue;
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
