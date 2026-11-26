using System;
using System.Collections.Generic;
using System.Linq;
using FightDataset;
using UnityEngine;

public static class DatasetContactSolver
{
    private sealed class Candidate
    {
        public int frame;
        public HumanBodyBones limb, target;
        public Vector3 position, velocity;
        public float score;
        public float approach;
    }

    public static float Tolerance(DatasetGeneratorConfig config) => Mathf.Clamp(config.maximumContactError, 0.002f, 0.01f);

    public static Vector3 LimbPoint(Animator actor, HumanBodyBones limb)
    {
        var bone = actor.GetBoneTransform(limb);
        bool foot = limb == HumanBodyBones.LeftFoot || limb == HumanBodyBones.RightFoot;
        var other = actor.GetBoneTransform(foot ? (limb == HumanBodyBones.LeftFoot ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes)
            : (limb == HumanBodyBones.LeftHand ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm));
        return foot ? (other ? Vector3.Lerp(bone.position, other.position, 0.55f) : bone.position)
            : bone.position + (other ? (bone.position - other.position).normalized * 0.045f : Vector3.zero);
    }

    private static void Body(Animator actor, HumanBodyBones target, float y, out Vector3 center, out float radius)
    {
        var bone = actor.GetBoneTransform(target);
        Vector3 a = bone.position, b = a;
        radius = 0.16f;
        if (target == HumanBodyBones.Head) { a += Vector3.up * 0.035f; b = a + Vector3.up * 0.07f; radius = 0.11f; }
        else if (target == HumanBodyBones.Chest) { b = actor.GetBoneTransform(HumanBodyBones.Neck).position - Vector3.up * 0.05f; }
        else if (target == HumanBodyBones.Hips) { a -= Vector3.up * 0.08f; b += Vector3.up * 0.12f; }
        else
        {
            bool left = target == HumanBodyBones.LeftUpperLeg || target == HumanBodyBones.LeftLowerLeg;
            bool upper = target == HumanBodyBones.LeftUpperLeg || target == HumanBodyBones.RightUpperLeg;
            b = actor.GetBoneTransform(upper ? (left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg)
                : (left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot)).position;
            radius = upper ? 0.105f : 0.07f;
        }
        center = Vector3.Lerp(a, b, Mathf.Abs(b.y - a.y) < 0.0001f ? 0 : Mathf.Clamp01((y - a.y) / (b.y - a.y)));
    }

    private static bool SafeStance(DatasetScenarioPlayer player, ScenarioAction action)
    {
        // Authored root displacement is baked into the skeleton. The scene root
        // remains the sampling anchor; stance distance must use the posed bodies.
        var a = player.actors[action.actor].GetBoneTransform(HumanBodyBones.Hips);
        var b = player.actors[action.target].GetBoneTransform(HumanBodyBones.Hips);
        Vector3 direction = b.position - a.position; direction.y = 0;
        return direction.magnitude >= 0.45f && direction.magnitude <= 1.65f &&
            Vector3.Dot(direction.normalized, player.transform.rotation * player.GetActorLocalRotation(action.actor) * Vector3.forward) > 0.72f &&
            Vector3.Dot(-direction.normalized, player.transform.rotation * player.GetActorLocalRotation(action.target) * Vector3.forward) > 0.65f;
    }

    public static void Calibrate(DatasetGeneratorConfig config, DatasetScenarioPlayer player, ScenarioAction action, CatalogClip entry, bool reposition)
    {
        using (var steps = CalibrateSteps(config, player, action, entry, reposition))
            while (steps.MoveNext()) { }
    }

    public static IEnumerator<object> CalibrateSteps(DatasetGeneratorConfig config, DatasetScenarioPlayer player, ScenarioAction action, CatalogClip entry, bool reposition, DatasetPenetrationGuard sharedGuard = null)
    {
        var attacker = player.actors[action.actor];
        var defender = player.actors[action.target];
        Vector3 original = player.GetActorLocalPosition(action.target);
        var profile = DatasetStrikeProfile.Get(player, action);
        if (entry.overrideImpact && entry.attackingLimb != profile.limb) throw new InvalidOperationException("Override does not select the main striking limb.");
        action.actorYawOffset = profile.yaw;
        action.sourceEnd = profile.sourceEnd;
        action.attackEndDisplacement = DatasetClipDisplacement.Get(player, action.actor, action.clip, action.SourceEnd);
        action.strikeSourceStart = profile.first; action.strikeSourceEnd = profile.last; action.strikeSourcePeak = profile.peak;
        action.attackFadeIn = Mathf.Min(player.plan.attackBlendIn, Mathf.Max(0.08f, profile.first / action.speed * 0.5f));
        var limbs = new[] { profile.limb };
        var targets = entry.overrideTargetLandmark ? new[] { entry.targetLandmark } : entry.kind == ClipKind.Kick
            ? new[] { HumanBodyBones.Head, HumanBodyBones.Chest, HumanBodyBones.Hips, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg }
            : new[] { HumanBodyBones.Head, HumanBodyBones.Chest, HumanBodyBones.Hips };
        var candidates = new List<Candidate>();
        float minimumGap = float.PositiveInfinity; int unsafeRefines = 0;
        int first = Mathf.CeilToInt((action.start + Mathf.Max(profile.first / action.speed, action.attackFadeIn + 2f / config.fps)) * config.fps);
        int last = Mathf.FloorToInt((action.start + (profile.last + 0.025f) / action.speed) * config.fps);
        if (entry.overrideImpact) first = last = Mathf.RoundToInt((action.start + entry.impactNormalizedTime * action.clip.length / action.speed) * config.fps);
        var poses = new Dictionary<int, Vector3[]>();
        for (int frame = Mathf.Max(0, first - 2); frame <= last; frame++)
        {
            player.SampleAtTime(frame / (float)config.fps);
            var points = limbs.Select(l => LimbPoint(attacker, l)).ToArray();
            poses[frame] = points;
            yield return "Sampling " + entry.displayName + " frame " + frame;
        }
        for (int frame = first; frame <= last; frame++)
        {
            yield return "Finding contact for " + entry.displayName;
            float time = frame / (float)config.fps;
            float sourceTime = (time - action.start) * action.speed;
            if (!profile.MainPhase(sourceTime, action.speed / config.fps)) continue;
            player.SampleAtTime(time);
            for (int limbIndex = 0; limbIndex < limbs.Length; limbIndex++)
            {
                Vector3 point = LimbPoint(attacker, limbs[limbIndex]);
                int beforeFrame = Mathf.Max(0, frame - 2);
                Vector3 velocity = (point - poses[beforeFrame][limbIndex]) * config.fps / (frame - beforeFrame);
                if (velocity.magnitude < Mathf.Max(0.35f, profile.peakSpeed * action.speed * 0.12f)) continue;
                foreach (var target in targets)
                {
                    Body(defender, target, point.y, out var center, out float radius);
                    float reach = radius + (entry.kind == ClipKind.Kick ? 0.075f : 0.05f);
                    Vector3 position = original;
                    if (reposition)
                    {
                        float vertical = point.y - center.y;
                        if (Mathf.Abs(vertical) >= reach) continue;
                        Vector3 normal = velocity; normal.y = 0;
                        if (normal.sqrMagnitude < 0.01f) normal = player.transform.rotation * player.GetActorLocalRotation(action.actor) * Vector3.forward;
                        normal.Normalize();
                        Vector3 desired = point + normal * Mathf.Sqrt(reach * reach - vertical * vertical);
                        Vector3 delta = desired - center; delta.y = 0;
                        position += player.transform.InverseTransformVector(delta);
                        player.SetActorLocalPosition(action.target, position);
                        bool safe = SafeStance(player, action);
                        player.SetActorLocalPosition(action.target, original);
                        if (!safe) continue;
                    }
                    else if (Vector3.Distance(point, center) > reach + 0.12f) continue;
                    Vector3 placedCenter = center + player.transform.TransformVector(position - original);
                    float approach = Vector3.Dot(velocity, (placedCenter - point).normalized);
                    if (approach < 0.12f) continue;
                    candidates.Add(new Candidate { frame = frame, limb = limbs[limbIndex], target = target, position = position,
                        velocity = velocity,
                        approach = approach,
                        score = profile.ExtensionRatio(sourceTime) - Mathf.Abs(sourceTime - profile.peak) * 0.2f + Mathf.Min(approach, 3) * 0.01f });
                }
            }
        }
        if (candidates.Count > 0)
        using (var attackMesh = new DatasetContactGeometry(attacker))
        using (var targetMesh = new DatasetContactGeometry(defender))
        using (var ownedGuard = sharedGuard == null ? new DatasetPenetrationGuard(player) : null)
        {
            var penetration = sharedGuard ?? ownedGuard;
            foreach (var candidate in candidates.OrderByDescending(c => c.score).Take(60))
            {
                Vector3 incoming = candidate.velocity; incoming.y = 0;
                if (incoming.sqrMagnitude < 0.01f)
                    incoming = player.transform.rotation * player.GetActorLocalRotation(action.actor) * Vector3.forward;
                incoming.Normalize();
                // Start separated and approach the limb. Unsigned nearest distance
                // is also zero when the knuckles are already buried in the body.
                Vector3 separated = candidate.position + player.transform.InverseTransformVector(incoming * 0.3f);
                player.SetActorLocalPosition(action.target, reposition ? separated : candidate.position);
                float travelled = 0;
                for (int refine = 0; refine < (reposition ? 20 : 1); refine++)
                {
                    yield return "Aligning " + entry.displayName;
                    player.SampleAtTime(candidate.frame / (float)config.fps);
                    var contact = targetMesh.Measure(attackMesh.LimbSurface(candidate.limb, candidate.velocity), candidate.target);
                    minimumGap = Mathf.Min(minimumGap, contact.gap);
                    if (contact.gap <= Tolerance(config))
                    {
                        if (!SafeStance(player, action) || penetration.Measure(true).samples > 0) { unsafeRefines++; break; }
                        action.limb = candidate.limb; action.targetLandmark = candidate.target;
                        action.impactFrame = candidate.frame; action.impactTime = candidate.frame / (float)config.fps;
                        action.impactNormalizedTime = (action.impactTime - action.start) * action.speed / action.clip.length;
                        action.contactError = contact.gap; action.contactPoint = contact.attack; action.targetContactPoint = contact.target;
                        action.incomingDirection = candidate.velocity.normalized;
                        using (var earlier = FindFirstMainContact(config, player, action, attackMesh, targetMesh, profile))
                            while (earlier.MoveNext()) yield return earlier.Current;
                        if (penetration.Measure(true).samples > 0) { unsafeRefines++; break; }
                        action.meshContactVerified = true; action.outcome = "main_strike_first_knuckle_or_foot_contact_verified";
                        action.approachSpeed = candidate.approach;
                        yield break;
                    }
                    if (!reposition || float.IsInfinity(contact.gap)) break;
                    float step = Mathf.Min(0.12f, contact.gap * 0.85f);
                    travelled += step;
                    if (travelled > 0.55f || incoming.sqrMagnitude < 0.1f) break;
                    player.SetActorLocalPosition(action.target, player.GetActorLocalPosition(action.target) - player.transform.InverseTransformVector(incoming * step));
                }
            }
        }
        player.SetActorLocalPosition(action.target, original);
        throw new InvalidOperationException("No visible-mesh contact for " + entry.displayName + " (candidates=" + candidates.Count +
            ", closest=" + minimumGap.ToString("F4") + ", unsafe_refinements=" + unsafeRefines + ")");
    }

    private static IEnumerator<object> FindFirstMainContact(DatasetGeneratorConfig config, DatasetScenarioPlayer player, ScenarioAction action,
        DatasetContactGeometry attackMesh, DatasetContactGeometry targetMesh, DatasetStrikeProfile profile)
    {
        int end = action.impactFrame;
        action.preparationContactFrames = 0;
        for (int frame = Mathf.CeilToInt(action.start * config.fps); frame < end; frame++)
        {
            yield return "Checking first contact at frame " + frame;
            float time = frame / (float)config.fps;
            player.SampleAtTime(time);
            Vector3 point = LimbPoint(player.actors[action.actor], action.limb);
            Body(player.actors[action.target], action.targetLandmark, point.y, out var center, out float radius);
            if (Vector3.Distance(point, center) > radius + 0.25f) continue;
            var contact = targetMesh.Measure(attackMesh.LimbSurface(action.limb), action.targetLandmark);
            if (contact.gap > Tolerance(config)) continue;
            float source = (time - action.start) * action.speed;
            if (!profile.MainPhase(source, action.speed / config.fps) || time - action.start < action.attackFadeIn + 1f / config.fps)
            { action.preparationContactFrames++; continue; }
            float previousTime = Mathf.Max(action.start, time - 2f / config.fps);
            player.SampleAtTime(previousTime);
            Vector3 previous = LimbPoint(player.actors[action.actor], action.limb);
            player.SampleAtTime(time);
            Vector3 velocity = (point - previous) / (time - previousTime);
            contact = targetMesh.Measure(attackMesh.LimbSurface(action.limb, velocity), action.targetLandmark);
            if (contact.gap > Tolerance(config)) continue;
            action.impactFrame = frame; action.impactTime = time; action.impactNormalizedTime = source / action.clip.length;
            action.contactError = contact.gap; action.contactPoint = contact.attack; action.targetContactPoint = contact.target;
            action.incomingDirection = velocity.normalized;
            break;
        }
        player.SampleAtTime(action.impactTime);
    }

    public static DatasetContactGeometry.Contact Verify(DatasetGeneratorConfig config, DatasetScenarioPlayer player, ScenarioAction action)
    {
        var profile = DatasetStrikeProfile.Get(player, action);
        float sourceTime = (action.impactTime - action.start) * action.speed;
        if (!profile.MainPhase(sourceTime, action.speed / config.fps) ||
            action.impactTime - action.start < (action.attackFadeIn > 0 ? action.attackFadeIn : player.plan.attackBlendIn) + 1f / config.fps)
            throw new InvalidOperationException("Contact is outside the main strike phase (preparation or recovery): " + action.assetPath);
        player.SampleAtTime(action.impactTime);
        using (var a = new DatasetContactGeometry(player.actors[action.actor]))
        using (var b = new DatasetContactGeometry(player.actors[action.target]))
        {
            Vector3 point = LimbPoint(player.actors[action.actor], action.limb);
            Body(player.actors[action.target], action.targetLandmark, point.y, out var center, out _);
            float before = Mathf.Max(action.start, action.impactTime - 2f / config.fps);
            if (action.impactTime - before < 0.001f) throw new InvalidOperationException("Impact has no approach interval.");
            player.SampleAtTime(before);
            Vector3 previous = LimbPoint(player.actors[action.actor], action.limb);
            player.SampleAtTime(action.impactTime);
            Vector3 velocity = (point - previous) / (action.impactTime - before);
            var result = b.Measure(a.LimbSurface(action.limb, velocity), action.targetLandmark);
            if (result.gap > Tolerance(config)) throw new InvalidOperationException("Leading knuckle/foot patch misses the target mesh: " + result.gap.ToString("F3") + " m, " + action.assetPath);
            float approach = Vector3.Dot(velocity, (center - point).normalized);
            if (float.IsNaN(approach) || float.IsInfinity(approach) || approach < 0.12f)
                throw new InvalidOperationException("Contact is on recovery/idle rather than the incoming strike: " + action.assetPath);
            action.approachSpeed = approach;
            action.incomingDirection = velocity.normalized;
            return result;
        }
    }
}
