using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace FightDataset
{
    public static class DatasetPlayableLifetime
    {
        public static void Release(ref PlayableGraph graph)
        {
            if (!graph.IsValid()) return;
            graph.Stop();
            var roots = new Playable[graph.GetRootPlayableCount()];
            for (int i = 0; i < roots.Length; i++) roots[i] = graph.GetRootPlayable(i);
            for (int i = graph.GetOutputCount() - 1; i >= 0; i--) graph.DestroyOutput(graph.GetOutput(i));
            foreach (var root in roots) if (root.IsValid()) graph.DestroySubgraph(root);
            graph.Destroy();
            graph = default;
        }
    }

    [Serializable]
    public sealed class ScenarioAction
    {
        public int actor, target;
        public AnimationClip clip;
        public string clipKey, assetPath, actorAction, plannedOutcome = "contact";
        public string outcome = "unverified_contact";
        public float start, speed = 1f, impactNormalizedTime = 0.55f, impactTime, contactError;
        public HumanBodyBones limb;
        public HumanBodyBones targetLandmark;
        public AnimationClip reaction;
        public string reactionAsset, reactionKind;
        public float reactionStart, reactionSpeed = 1f;
        public int impactFrame;
        public Vector3 contactPoint, targetContactPoint;
        public Vector3 incomingDirection;
        public bool meshContactVerified;
        public float approachSpeed;
        public float actorYawOffset, sourceEnd, strikeSourceStart, strikeSourceEnd, strikeSourcePeak, attackFadeIn;
        public float reactionSourceOffset, reactionFadeIn, reactionResponseSeconds;
        public int reactionResponseFrame;
        public int preparationContactFrames;
        // Canonical, world-metre horizontal displacement of the authored clip.
        public Vector3 attackEndDisplacement, reactionEndDisplacement;
        public float SourceEnd => sourceEnd > 0 ? Mathf.Min(sourceEnd, clip.length) : clip.length;
        public float End => start + SourceEnd / speed;
        public float ReactionEnd => reaction ? reactionStart + (reaction.length - reactionSourceOffset) / reactionSpeed : start;
        public bool HoldsFallenPose => reactionKind == "Fall";
    }

    [Serializable]
    public sealed class DatasetScenarioPlan
    {
        public int seed;
        public string category, eventClass;
        public float duration, attackBlendIn = 0.28f, reactionBlendIn = 0.22f, blendOut = 0.4f;
        public float[] neutralTimeOffsets = { 0f, 0f }, neutralSpeeds = { 1f, 1f };
        public List<ScenarioAction> actions = new List<ScenarioAction>();
        public List<string> qualityFlags = new List<string>();
        public List<AnimationSelectionAttempt> animationSelection = new List<AnimationSelectionAttempt>();
    }

    [Serializable]
    public sealed class AnimationSelectionAttempt
    {
        public string clipKey, assetPath, reason;
        public int actionIndex;
        public float speed;
        public bool accepted;
    }

    public sealed class DatasetScenarioPlayer : MonoBehaviour
    {
        public Animator[] actors;
        public AnimationClip[] neutralClips;
        public DatasetScenarioPlan plan;
        public bool playAutomatically = true;
        public float TimeSeconds { get; private set; }
        private Mixer[] mixers;

        public Vector3 GetActorLocalPosition(int actor) => mixers == null
            ? actors[actor].transform.localPosition : mixers[actor].BasePosition;
        public Quaternion GetActorLocalRotation(int actor) => mixers == null
            ? actors[actor].transform.localRotation : mixers[actor].BaseRotation;

        public void SetActorLocalPosition(int actor, Vector3 position)
        {
            actors[actor].transform.localPosition = position;
            if (mixers != null) mixers[actor].SetBasePosition(position);
        }

        public void SampleActorSource(int actor, AnimationClip clip, float sourceTime)
        {
            if (mixers == null) SampleAtTime(0);
            mixers[actor].Sample(0, clip, Mathf.Clamp(sourceTime, 0, clip.length - 0.0001f), 1, false, 0, true);
        }

        private void Update()
        {
            if (playAutomatically && plan != null)
                SampleAtTime((TimeSeconds + Time.deltaTime) % plan.duration);
        }

        public void SampleAtTime(float time)
        {
            if (time < 0 || float.IsNaN(time) || float.IsInfinity(time)) throw new ArgumentOutOfRangeException(nameof(time));
            if (mixers == null)
            {
                if (actors == null || actors.Length != 2 || neutralClips == null || neutralClips.Length != 2)
                    throw new InvalidOperationException("Two actors and neutral clips are required.");
                mixers = new[] { new Mixer(actors[0], neutralClips[0]), new Mixer(actors[1], neutralClips[1]) };
            }
            TimeSeconds = time;
            for (int actor = 0; actor < 2; actor++)
            {
                AnimationClip actionClip = null;
                float localTime = 0, weight = 0, yaw = 0;
                Vector3 positionOffset = Vector3.zero;
                foreach (var action in plan.actions)
                {
                    bool attack = action.actor == actor;
                    if (!attack && !action.reaction) continue;
                    float start = attack ? action.start : action.reactionStart;
                    var clip = attack ? action.clip : action.reaction;
                    float speed = attack ? action.speed : action.reactionSpeed;
                    float offset = attack ? 0 : action.reactionSourceOffset;
                    float duration = (attack ? action.SourceEnd : clip.length - offset) / speed;
                    bool hold = !attack && action.HoldsFallenPose;
                    if (!hold && time >= start + duration)
                    {
                        float fade = attack ? (action.attackFadeIn > 0 ? action.attackFadeIn : plan.attackBlendIn)
                            : (action.reactionFadeIn > 0 ? action.reactionFadeIn : plan.reactionBlendIn);
                        float remaining = Weight(time - start, duration, fade, plan.blendOut);
                        float fullYaw = attack ? action.actorYawOffset : 0;
                        Vector3 displacement = attack ? action.attackEndDisplacement : action.reactionEndDisplacement;
                        // The pose already contains 'remaining' of the terminal
                        // displacement. Transfer only the missing part to its anchor.
                        positionOffset += mixers[actor].BaseRotation *
                            (Quaternion.Euler(0, fullYaw, 0) * displacement -
                             remaining * (Quaternion.Euler(0, fullYaw * remaining, 0) * displacement));
                    }
                    if (time < start || (!hold && time > start + duration + plan.blendOut)) continue;
                    if (actionClip) throw new InvalidOperationException("Overlapping incompatible actor actions.");
                    actionClip = clip;
                    localTime = Mathf.Clamp(offset + (time - start) * speed, offset,
                        Mathf.Max(offset, (attack ? action.SourceEnd : clip.length) - 0.0001f));
                    float reactionFade = action.reactionFadeIn > 0 ? action.reactionFadeIn : plan.reactionBlendIn;
                    weight = hold ? Ease((time - start) / Mathf.Min(reactionFade, duration))
                        : Weight(time - start, duration, attack ? (action.attackFadeIn > 0 ? action.attackFadeIn : plan.attackBlendIn) : reactionFade, plan.blendOut);
                    if (attack) yaw = action.actorYawOffset * weight;
                }
                float idleTime = time * plan.neutralSpeeds[actor] + plan.neutralTimeOffsets[actor];
                mixers[actor].Sample(idleTime, actionClip, localTime, weight, true, yaw, false, positionOffset);
            }
        }

        public static float Weight(float time, float duration, float fadeIn, float fadeOut)
        {
            if (time <= 0 || time >= duration + fadeOut) return 0;
            return Ease(time / Mathf.Min(fadeIn, duration)) * (1f - Ease((time - duration) / fadeOut));
        }

        private static float Ease(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        public void ResetPlayback()
        {
            if (mixers != null) foreach (var mixer in mixers) mixer.Dispose();
            mixers = null;
            TimeSeconds = 0;
        }

        private void OnDestroy() => ResetPlayback();

        private sealed class Mixer : IDisposable
        {
            private PlayableGraph graph;
            private AnimationMixerPlayable mixer;
            private AnimationClipPlayable idlePlayable, actionPlayable;
            private AnimationClip current;
            private readonly AnimationClip idle;
            private readonly Animator animator;
            private Vector3 basePosition;
            public Vector3 BasePosition => basePosition;
            public Quaternion BaseRotation { get; }
            private readonly float floorY;
            private Mesh groundMesh;
            private readonly SkinnedMeshRenderer[] skins;
            private readonly List<Vector3> groundVertices = new List<Vector3>();

            public Mixer(Animator animator, AnimationClip idle)
            {
                this.idle = idle;
                this.animator = animator;
                skins = animator.GetComponentsInChildren<SkinnedMeshRenderer>();
                basePosition = animator.transform.localPosition;
                BaseRotation = animator.transform.localRotation;
                floorY = animator.transform.parent ? animator.transform.parent.position.y : 0f;
                if (!idle || !animator.avatar || !animator.isHuman) throw new InvalidOperationException("Invalid Humanoid playback inputs.");
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph = PlayableGraph.Create("Dataset_" + animator.name);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                mixer = AnimationMixerPlayable.Create(graph, 2);
                idlePlayable = AnimationClipPlayable.Create(graph, idle);
                idlePlayable.SetSpeed(0);
                graph.Connect(idlePlayable, 0, mixer, 0);
                AnimationPlayableOutput.Create(graph, "Pose", animator).SetSourcePlayable(mixer);
                graph.Play();
            }

            public void Sample(float time, AnimationClip action, float actionTime, float weight, bool correctFloor = true, float yaw = 0, bool canonicalSource = false, Vector3 positionOffset = default)
            {
                animator.transform.localPosition = basePosition + positionOffset;
                animator.transform.localRotation = BaseRotation * Quaternion.Euler(0, yaw, 0);
                if (canonicalSource) animator.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                if (action && current != action)
                {
                    if (actionPlayable.IsValid()) { graph.Disconnect(mixer, 1); graph.DestroyPlayable(actionPlayable); }
                    actionPlayable = AnimationClipPlayable.Create(graph, action);
                    actionPlayable.SetSpeed(0);
                    graph.Connect(actionPlayable, 0, mixer, 1);
                    current = action;
                }
                idlePlayable.SetTime(time % idle.length);
                if (action && actionPlayable.IsValid()) actionPlayable.SetTime(actionTime);
                mixer.SetInputWeight(0, 1f - weight);
                mixer.SetInputWeight(1, weight);
                graph.Evaluate(0);
                // Retargeted standing/kicking clips can also push the support foot
                // through the floor. Correct only penetration, using the visible
                // mesh; intentional jumps and the fall's downward motion remain.
                if (correctFloor)
                {
                    if (!groundMesh) groundMesh = new Mesh { name = "ActorGroundProbe" };
                    float bottom = float.PositiveInfinity;
                    foreach (var skin in skins)
                    {
                        skin.BakeMesh(groundMesh, true);
                        groundMesh.GetVertices(groundVertices);
                        var matrix = skin.transform.localToWorldMatrix;
                        foreach (var vertex in groundVertices)
                            bottom = Mathf.Min(bottom, matrix.MultiplyPoint3x4(vertex).y);
                    }
                    if (bottom < floorY + 0.002f)
                        animator.transform.position += Vector3.up * (floorY + 0.002f - bottom);
                }
            }

            public void SetBasePosition(Vector3 position) { basePosition = position; animator.transform.localPosition = position; }

            public void Dispose()
            {
                DatasetPlayableLifetime.Release(ref graph);
                if (animator) { animator.transform.localPosition = basePosition; animator.transform.localRotation = BaseRotation; }
                if (groundMesh)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(groundMesh);
                    else UnityEngine.Object.DestroyImmediate(groundMesh);
                }
            }
        }
    }
}
