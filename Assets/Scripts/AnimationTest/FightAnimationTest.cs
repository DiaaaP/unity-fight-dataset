using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace FightDataset.Prototype
{
    /// <summary>Small deterministic two-person animation preview. No physics/contact labels.</summary>
    public sealed class FightAnimationTest : MonoBehaviour
    {
        public Animator attacker;
        public Animator receiver;
        public AnimationClip idle;
        public AnimationClip punch;
        public AnimationClip reaction;
        [Range(0.05f, 0.95f)] public float impactNormalizedTime = 0.5f;
        [Min(0.1f)] public float attackSpeed = 1f;
        [Min(0.1f)] public float reactionSpeed = 1f;
        [Min(0.1f)] public float prepareSeconds = 1.5f;
        [Min(0.1f)] public float recoverySeconds = 2f;
        [Tooltip("Wall-clock crossfade into the attack; independent of clip playback speed.")]
        [Range(0.05f, 0.5f)] public float blendSeconds = 0.28f;
        [Range(0.05f, 0.5f)] public float reactionBlendSeconds = 0.22f;
        [Range(0.1f, 0.8f)] public float returnBlendSeconds = 0.4f;
        public HumanBodyBones attackingHand = HumanBodyBones.RightHand;
        public float calibratedHeadError;

        public float ElapsedSeconds { get; private set; }
        public int CompletedCycles { get; private set; }
        public int AttacksStarted { get; private set; }
        public int ReactionsStarted { get; private set; }
        public string Phase { get; private set; } = "Idle";
        public float AttackerActionWeight { get; private set; }
        public float ReceiverActionWeight { get; private set; }
        public float CycleDuration => prepareSeconds + Mathf.Max(punch.length / attackSpeed,
            punch.length * impactNormalizedTime / attackSpeed + reaction.length / reactionSpeed) + returnBlendSeconds + recoverySeconds;

        private ActorGraph attackerGraph;
        private ActorGraph receiverGraph;
        private int lastAttackCycle = -1;
        private int lastReactionCycle = -1;

        private void Start()
        {
            if (!InitializePlayback()) enabled = false;
        }

        private bool InitializePlayback()
        {
            if (attackerGraph != null) return true;
            if (!attacker || !receiver || !idle || !punch || !reaction ||
                !attacker.isHuman || !receiver.isHuman)
            {
                Debug.LogError("Animation test needs two Humanoid avatars, Idle, Punch and Reaction.", this);
                return false;
            }
            attackerGraph = new ActorGraph(attacker, idle, punch);
            receiverGraph = new ActorGraph(receiver, idle, reaction);
            ApplyTime(0f);
            return true;
        }

        // Editor export uses the same animation path as the live preview.
        public void SampleAtTime(float seconds)
        {
            if (seconds < 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
                throw new ArgumentOutOfRangeException(nameof(seconds));
            if (!InitializePlayback()) throw new InvalidOperationException("Invalid animation test configuration.");
            ElapsedSeconds = seconds;
            ApplyTime(seconds);
        }

        private void Update()
        {
            if (attackerGraph == null) return;
            ElapsedSeconds += Time.deltaTime;
            ApplyTime(ElapsedSeconds);
        }

        private void ApplyTime(float time)
        {
            CompletedCycles = Mathf.FloorToInt(time / CycleDuration);
            float cycleTime = time % CycleDuration;
            float attackTime = cycleTime - prepareSeconds;
            float reactionTime = cycleTime - prepareSeconds - punch.length * impactNormalizedTime / attackSpeed;
            AttackerActionWeight = BlendWeight(attackTime, punch.length / attackSpeed, blendSeconds, returnBlendSeconds);
            ReceiverActionWeight = BlendWeight(reactionTime, reaction.length / reactionSpeed, reactionBlendSeconds, returnBlendSeconds);
            attackerGraph.Evaluate(time, attackTime * attackSpeed, AttackerActionWeight);
            receiverGraph.Evaluate(time, reactionTime * reactionSpeed, ReceiverActionWeight);

            if (attackTime >= 0f && lastAttackCycle != CompletedCycles)
            {
                lastAttackCycle = CompletedCycles;
                AttacksStarted++;
            }
            if (reactionTime >= 0f && lastReactionCycle != CompletedCycles)
            {
                lastReactionCycle = CompletedCycles;
                ReactionsStarted++;
            }
            Phase = cycleTime <= prepareSeconds ? "Idle" :
                AttackerActionWeight > 0f ? "Punch" :
                ReceiverActionWeight > 0f ? "Reaction" : "Recovery / Idle";
        }

        private static float BlendWeight(float time, float duration, float blendIn, float blendOut)
        {
            if (time <= 0f || time >= duration + blendOut) return 0f;
            // Keep the authored action complete, then freeze its last pose while
            // blending back to the continuously running Idle. The quintic easing
            // has zero first and second derivatives at both crossfade boundaries.
            float entry = SmootherStep(time / Mathf.Min(blendIn, duration));
            float exit = 1f - SmootherStep((time - duration) / blendOut);
            return entry * exit;
        }

        private static float SmootherStep(float value)
        {
            float t = Mathf.Clamp01(value);
            return t * t * t * (t * (t * 6f - 15f) + 10f);
        }

        private void OnDestroy()
        {
            ResetPlayback();
        }

        public void ResetPlayback()
        {
            attackerGraph?.Dispose();
            receiverGraph?.Dispose();
            attackerGraph = null;
            receiverGraph = null;
            ElapsedSeconds = 0f;
            CompletedCycles = AttacksStarted = ReactionsStarted = 0;
            lastAttackCycle = lastReactionCycle = -1;
            Phase = "Idle";
            AttackerActionWeight = ReceiverActionWeight = 0f;
        }

        private sealed class ActorGraph : IDisposable
        {
            private PlayableGraph graph;
            private AnimationClipPlayable idlePlayable;
            private AnimationClipPlayable actionPlayable;
            private AnimationMixerPlayable mixer;
            private readonly AnimationClip idleClip;
            private readonly AnimationClip actionClip;

            public ActorGraph(Animator animator, AnimationClip idle, AnimationClip action)
            {
                idleClip = idle;
                actionClip = action;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                graph = PlayableGraph.Create("FightTest_" + animator.name);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                idlePlayable = AnimationClipPlayable.Create(graph, idle);
                actionPlayable = AnimationClipPlayable.Create(graph, action);
                idlePlayable.SetSpeed(0);
                actionPlayable.SetSpeed(0);
                idlePlayable.SetApplyFootIK(true);
                actionPlayable.SetApplyFootIK(true);
                mixer = AnimationMixerPlayable.Create(graph, 2);
                graph.Connect(idlePlayable, 0, mixer, 0);
                graph.Connect(actionPlayable, 0, mixer, 1);
                var output = AnimationPlayableOutput.Create(graph, "Animation", animator);
                output.SetSourcePlayable(mixer);
                graph.Play();
            }

            public void Evaluate(float time, float actionTime, float weight)
            {
                idlePlayable.SetTime(time % idleClip.length);
                actionPlayable.SetTime(Mathf.Clamp(actionTime, 0f, actionClip.length));
                mixer.SetInputWeight(0, 1f - weight);
                mixer.SetInputWeight(1, weight);
                graph.Evaluate(0f);
            }

            public void Dispose()
            {
                if (graph.IsValid()) graph.Destroy();
            }
        }
    }
}
