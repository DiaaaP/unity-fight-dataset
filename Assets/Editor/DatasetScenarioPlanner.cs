using System;
using System.Collections.Generic;
using System.Linq;
using FightDataset;
using UnityEngine;

[Serializable]
public sealed class DatasetCameraView
{
    public string id, profile, framing;
    public Vector3 position, rotationEuler;
    public float fov, height, distance, azimuth, elevation, screenOccupancy;
    public void Apply(Camera camera) { camera.transform.SetPositionAndRotation(position, Quaternion.Euler(rotationEuler)); camera.fieldOfView = fov; }
}

public static class DatasetScenarioPlanner
{
    private static CatalogClip[] Pool(AnimationCatalog catalog, params ClipKind[] kinds) => catalog.clips
        .Where(DatasetCatalogTools.Usable).Where(c => kinds.Contains(c.kind)).OrderBy(c => c.key, StringComparer.Ordinal).ToArray();

    public static string[] AvailableCategories(DatasetGeneratorConfig config)
    {
        var result = new List<string>();
        bool reaction = Pool(config.catalog, ClipKind.HitReaction, ClipKind.Stumble).Length > 0;
        bool finalReaction = reaction || Pool(config.catalog, ClipKind.Fall).Any(c => c.reviewedFall);
        bool punch = Pool(config.catalog, ClipKind.Punch).Length > 0, kick = Pool(config.catalog, ClipKind.Kick).Length > 0;
        if (config.generateHit && punch && finalReaction) result.Add("hit");
        if (config.generateKick && kick && finalReaction) result.Add("kick");
        if (config.generateFight && (punch || kick) && reaction) result.Add("fight");
        if (config.generateNormal) result.Add("normal");
        return result.ToArray();
    }

    public static DatasetScenarioPlan Create(DatasetGeneratorConfig config, DatasetSceneFactory.Stage stage, string category, int seed,
        IReadOnlyDictionary<string, int> usage = null)
    {
        DatasetScenarioPlan result = null;
        using (var steps = CreateSteps(config, stage, category, seed, plan => result = plan, usage))
            while (steps.MoveNext()) { }
        return result;
    }

    public static IEnumerator<object> CreateSteps(DatasetGeneratorConfig config, DatasetSceneFactory.Stage stage, string category, int seed,
        Action<DatasetScenarioPlan> completed, IReadOnlyDictionary<string, int> usage = null)
    {
        if (!AvailableCategories(config).Contains(category)) throw new InvalidOperationException("Category unavailable: " + category);
        var random = new System.Random(seed);
        var player = stage.player;
        var plan = new DatasetScenarioPlan { seed = seed, category = category,
            eventClass = category == "normal" ? "normal" : category == "fight" ? "fight" : "potential_violence",
            attackBlendIn = config.attackBlendIn * Pick(random, new Vector2(0.85f, 1.15f)),
            reactionBlendIn = config.reactionBlendIn * Pick(random, new Vector2(0.85f, 1.15f)),
            blendOut = config.blendOut * Pick(random, new Vector2(0.85f, 1.15f)) };
        player.plan = plan;
        var neutrals = Pool(config.catalog, ClipKind.Idle, ClipKind.Neutral);
        for (int i = 0; i < 2; i++)
        {
            // Contact classes keep idle poses; locomotion clips are not introduced.
            var pool = category == "normal" ? neutrals : Pool(config.catalog, ClipKind.Idle);
            player.neutralClips[i] = pool[random.Next(pool.Length)].clip;
            plan.neutralTimeOffsets[i] = Pick(random, new Vector2(0, player.neutralClips[i].length));
            plan.neutralSpeeds[i] = Pick(random, new Vector2(0.9f, 1.1f));
        }
        var rotation = Quaternion.Euler(0, Pick(random, new Vector2(0, 360)), 0);
        Vector3 center = new Vector3(Pick(random, new Vector2(-0.55f, 0.55f)), 0, Pick(random, new Vector2(-0.55f, 0.55f)));
        for (int i = 0; i < 2; i++)
        {
            var actor = player.actors[i].transform;
            player.SetActorLocalPosition(i, center + rotation * new Vector3(0, actor.localPosition.y, i == 0 ? -0.45f : 0.45f));
            actor.localRotation = rotation * Quaternion.Euler(0, i == 0 ? Pick(random, new Vector2(-3, 3)) : 180, 0);
        }
        if (category == "normal")
        {
            plan.qualityFlags.Add("normal_actions_are_available_neutral_clips_only");
            plan.duration = config.minimumDurationSeconds;
            completed(plan);
            yield break;
        }
        int count = category == "fight" ? config.fightAttackCount : 1;
        var selectedCounts = new Dictionary<string, int>();
        float nextStart = Pick(random, new Vector2(0.65f, 1.55f));
        for (int index = 0; index < count; index++)
        {
            var attacks = category == "hit" ? Pool(config.catalog, ClipKind.Punch) : category == "kick" ? Pool(config.catalog, ClipKind.Kick) : Pool(config.catalog, ClipKind.Punch, ClipKind.Kick);
            var order = OrderAttacks(attacks, usage, selectedCounts, random);
            ScenarioAction accepted = null;
            Exception failure = null;
            foreach (var entry in order)
            {
                float minimumSpeed = Mathf.Max(config.speedRange.x, entry.speedRange.x), maximumSpeed = Mathf.Min(config.speedRange.y, entry.speedRange.y);
                if (minimumSpeed > maximumSpeed) continue;
                // Give an underused clip several chances before falling back to an
                // easier clip. Failed trials remain visible in scenario metadata.
                for (int variant = 0; variant < (minimumSpeed == maximumSpeed ? 1 : 3); variant++)
                {
                var action = new ScenarioAction { actor = index % 2, target = 1 - index % 2, clip = entry.clip, clipKey = entry.key,
                    assetPath = entry.assetPath, actorAction = entry.kind == ClipKind.Kick ? "kick" : "punch",
                    start = nextStart, speed = Pick(random, new Vector2(minimumSpeed, maximumSpeed)) };
                Vector3 originalTarget = player.GetActorLocalPosition(action.target);
                var selection = new AnimationSelectionAttempt { clipKey = entry.key, assetPath = entry.assetPath,
                    actionIndex = index, speed = action.speed };
                plan.animationSelection.Add(selection);
                plan.actions.Add(action);
                Exception trialFailure = null;
                bool succeeded = false;
                using (var trial = PrepareActionSteps(config, player, plan, action, entry, random, index == 0, index == count - 1))
                {
                    while (true)
                    {
                        bool more;
                        try { more = trial.MoveNext(); }
                        catch (Exception exception) { trialFailure = exception; break; }
                        if (!more) { succeeded = true; break; }
                        yield return trial.Current;
                    }
                }
                if (succeeded)
                {
                    accepted = action;
                    selection.accepted = true;
                    selectedCounts[entry.key] = selectedCounts.TryGetValue(entry.key, out int used) ? used + 1 : 1;
                    break;
                }
                failure = trialFailure; selection.reason = trialFailure?.Message;
                plan.actions.Remove(action); player.SetActorLocalPosition(action.target, originalTarget);
                }
                if (accepted != null) break;
            }
            if (accepted == null) throw new InvalidOperationException("No contact-compatible attack: " + failure?.Message);
            nextStart = Mathf.Max(accepted.End, accepted.ReactionEnd) + plan.blendOut + Pick(random, new Vector2(0.25f, 0.8f));
        }
        plan.qualityFlags.Add("contact_checked_on_actual_blended_mesh_at_output_frame");
        plan.qualityFlags.Add("mesh_contact_is_geometric_not_a_physics_simulation");
        plan.qualityFlags.Add("dominant_limb_incoming_strike_hits_named_anatomical_mesh_region");
        plan.qualityFlags.Add("least_used_compatible_animation_first_with_logged_rejections");
        plan.qualityFlags.Add("source_main_stroke_direction_and_first_knuckle_contact_checked");
        plan.qualityFlags.Add("visible_reaction_onset_within_two_output_frames_after_contact");
        plan.qualityFlags.Add("bidirectional_mesh_penetration_checked_through_preparation_contact_reaction_and_idle_return");
        plan.duration = Mathf.Max(config.minimumDurationSeconds, nextStart + 0.8f);
        if (plan.duration > 60) throw new InvalidOperationException("Scenario exceeds 60 seconds.");
        player.SampleAtTime(0);
        completed(plan);
    }

    private static IEnumerator<object> PrepareActionSteps(DatasetGeneratorConfig config, DatasetScenarioPlayer player,
        DatasetScenarioPlan plan, ScenarioAction action, CatalogClip entry, System.Random random, bool reposition, bool final)
    {
        using (var contact = DatasetContactSolver.CalibrateSteps(config, player, action, entry, reposition))
            while (contact.MoveNext()) yield return contact.Current;
        var reactions = Pool(config.catalog, ClipKind.HitReaction, ClipKind.Stumble);
        if (final) reactions = reactions.Concat(Pool(config.catalog, ClipKind.Fall).Where(r => r.reviewedFall)).ToArray();
        if (!string.IsNullOrEmpty(entry.preferredReactionKey)) reactions = reactions.Where(r => r.key == entry.preferredReactionKey).ToArray();
        else
        {
            bool head = action.targetLandmark == HumanBodyBones.Head;
            var matched = reactions.Where(r => r.kind == ClipKind.Fall || (head ? r.displayName.ToLowerInvariant().Contains("head")
                : r.displayName.ToLowerInvariant().Contains("body") || r.displayName.ToLowerInvariant().Contains("stomach"))).ToArray();
            if (matched.Length > 0) reactions = matched;
        }
        if (reactions.Length == 0) throw new InvalidOperationException("No contact reaction for " + entry.displayName);
        var standing = reactions.Where(r => r.kind != ClipKind.Fall).ToArray();
        var falls = reactions.Where(r => r.kind == ClipKind.Fall).ToArray();
        var choices = standing.Length == 0 ? falls : falls.Length > 0 && random.NextDouble() < 0.15 ? falls : standing;
        var reaction = choices[random.Next(choices.Length)];
        action.reaction = reaction.clip; action.reactionAsset = reaction.assetPath; action.reactionKind = reaction.kind.ToString();
        action.reactionSpeed = action.speed * Pick(random, new Vector2(0.92f, 1.08f));
        action.reactionEndDisplacement = DatasetClipDisplacement.Get(player, action.target, action.reaction, action.reaction.length);
        yield return "Choosing response to " + entry.displayName;
        DatasetReactionTiming.Configure(config, player, action);
        player.SampleAtTime(action.impactTime);
        DatasetContactSolver.Verify(config, player, action);
        float checkedDuration = Mathf.Max(config.minimumDurationSeconds,
            plan.actions.Max(a => Mathf.Max(a.End, a.ReactionEnd)) + plan.blendOut + 1.6f);
        using (var validation = DatasetPenetrationGuard.VerifyPlanSteps(config, player, checkedDuration))
            while (validation.MoveNext()) yield return validation.Current;
    }

    public static CatalogClip[] OrderAttacks(IEnumerable<CatalogClip> attacks, IReadOnlyDictionary<string, int> usage,
        IReadOnlyDictionary<string, int> inPlan, System.Random random) => attacks
        .Select(entry => new { entry, used = (usage != null && usage.TryGetValue(entry.key, out int a) ? a : 0) +
            (inPlan != null && inPlan.TryGetValue(entry.key, out int b) ? b : 0), tie = random.Next() })
        .OrderBy(item => item.used).ThenBy(item => item.tie).Select(item => item.entry).ToArray();

    public static DatasetCameraView[] Cameras(DatasetGeneratorConfig config, DatasetSceneFactory.Stage stage, int seed)
        => DatasetCameraPlanner.Create(config, stage, seed);

    public static float Pick(System.Random random, Vector2 range) => range.x + (range.y - range.x) * (float)random.NextDouble();
}
