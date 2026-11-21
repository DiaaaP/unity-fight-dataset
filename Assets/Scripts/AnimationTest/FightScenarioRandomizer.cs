using System;
using UnityEngine;

namespace FightDataset.Prototype
{
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public sealed class FightScenarioRandomizer : MonoBehaviour
    {
        public bool randomizeOnPlay = true;
        public bool newSeedOnPlay = true;
        public int seed = 12345;
        public Settings settings = new Settings();
        [SerializeField, HideInInspector] private bool baselineCaptured;
        [SerializeField, HideInInspector] private Vector3 attackerPosition, receiverPosition;
        [SerializeField, HideInInspector] private Quaternion attackerRotation, receiverRotation;
        [SerializeField, HideInInspector] private Vector3 cameraPosition;
        public AppliedParameters LastApplied { get; private set; }

        [Serializable]
        public sealed class Settings
        {
            public Vector2 animationSpeed = new Vector2(0.8f, 1.2f);
            public Vector2 pairYawDegrees = new Vector2(-12f, 12f);
            public Vector2 attackerYawDegrees = new Vector2(-4f, 4f);
            public Vector2 cameraOrbitDegrees = new Vector2(-25f, 25f);
            public Vector2 cameraDistance = new Vector2(3.8f, 4.5f);
            public Vector2 cameraHeight = new Vector2(2.3f, 3.1f);
            public Vector2 cameraYawDegrees = new Vector2(-3f, 3f);
            public Vector2 cameraPitchDegrees = new Vector2(-3f, 3f);
            public Vector2 cameraFov = new Vector2(38f, 48f);
            [Range(0.01f, 0.2f)] public float viewportMargin = 0.06f;
        }

        [Serializable]
        public sealed class AppliedParameters
        {
            public int seed, camera_attempts;
            public float animation_speed, pair_yaw_degrees, attacker_yaw_degrees;
            public float camera_orbit_degrees, camera_distance, camera_height;
            public float camera_yaw_degrees, camera_pitch_degrees, camera_fov;
            public float planned_impact_seconds, head_landmark_error_meters;
            public bool framing_checked;
            public string angle_policy = "whole_actor_yaw_no_random_bone_rotations";
        }

        private void Awake()
        {
            if (Application.isPlaying && randomizeOnPlay)
                ApplySeed(newSeedOnPlay ? Guid.NewGuid().GetHashCode() : seed);
        }

        public void CaptureBaseline()
        {
            var test = GetComponent<FightAnimationTest>();
            var view = GetComponentInChildren<Camera>();
            if (!test || !view) throw new InvalidOperationException("Randomizer needs FightAnimationTest and a camera.");
            attackerPosition = test.attacker.transform.localPosition;
            receiverPosition = test.receiver.transform.localPosition;
            attackerRotation = test.attacker.transform.localRotation;
            receiverRotation = test.receiver.transform.localRotation;
            cameraPosition = view.transform.localPosition;
            baselineCaptured = true;
        }

        public AppliedParameters ApplySeed(int value)
        {
            ValidateSettings();
            if (!baselineCaptured) CaptureBaseline();
            var test = GetComponent<FightAnimationTest>();
            var view = GetComponentInChildren<Camera>();
            test.ResetPlayback();
            seed = value;
            var random = new System.Random(value);
            var applied = new AppliedParameters
            {
                seed = value,
                animation_speed = Pick(random, settings.animationSpeed),
                pair_yaw_degrees = Pick(random, settings.pairYawDegrees),
                attacker_yaw_degrees = Pick(random, settings.attackerYawDegrees)
            };
            test.attackSpeed = test.reactionSpeed = applied.animation_speed;
            var pairRotation = Quaternion.Euler(0f, applied.pair_yaw_degrees, 0f);
            var center = (attackerPosition + receiverPosition) * 0.5f;
            test.attacker.transform.localPosition = center + pairRotation * (attackerPosition - center);
            test.receiver.transform.localPosition = center + pairRotation * (receiverPosition - center);
            test.attacker.transform.localRotation = pairRotation * attackerRotation * Quaternion.Euler(0f, applied.attacker_yaw_degrees, 0f);
            test.receiver.transform.localRotation = pairRotation * receiverRotation;

            // Retarget the receiver's horizontal placement to the sampled hand trajectory.
            // This is only geometric calibration, not proof of physical contact.
            float impact = test.prepareSeconds + test.punch.length * test.impactNormalizedTime / test.attackSpeed;
            test.SampleAtTime(impact);
            Vector3 fist = test.attacker.GetBoneTransform(test.attackingHand).position;
            Vector3 landmark = test.receiver.GetBoneTransform(HumanBodyBones.Head).position + test.receiver.transform.forward * 0.1f;
            Vector3 correction = fist - landmark;
            correction.y = 0f;
            if (correction.magnitude > 0.25f)
                throw new InvalidOperationException("Randomized attack requires excessive spacing correction.");
            test.receiver.transform.position += correction;
            landmark += correction;
            test.calibratedHeadError = Vector3.Distance(fist, landmark);
            applied.head_landmark_error_meters = test.calibratedHeadError;
            applied.planned_impact_seconds = impact;

            // Cache posed bone samples before trying candidate cameras. No rerendering or
            // graph rebuild per candidate, and no random parameters are changed per frame.
            Vector3[] points = FramingPoints(test);
            Vector3 focus = (test.attacker.transform.position + test.receiver.transform.position) * 0.5f + Vector3.up;
            Vector3 baseDirection = transform.TransformDirection(new Vector3(cameraPosition.x, 0f, cameraPosition.z)).normalized;
            view.aspect = 1280f / 720f;
            bool accepted = false;
            for (int attempt = 1; attempt <= 32; attempt++)
            {
                applied.camera_attempts = attempt;
                applied.camera_orbit_degrees = Pick(random, settings.cameraOrbitDegrees);
                applied.camera_distance = Pick(random, settings.cameraDistance);
                applied.camera_height = Pick(random, settings.cameraHeight);
                applied.camera_yaw_degrees = Pick(random, settings.cameraYawDegrees);
                applied.camera_pitch_degrees = Pick(random, settings.cameraPitchDegrees);
                applied.camera_fov = Pick(random, settings.cameraFov);
                Vector3 direction = Quaternion.Euler(0f, applied.camera_orbit_degrees, 0f) * baseDirection;
                view.transform.position = transform.position + direction * applied.camera_distance + Vector3.up * applied.camera_height;
                view.transform.rotation = Quaternion.LookRotation(focus - view.transform.position) *
                    Quaternion.Euler(applied.camera_pitch_degrees, applied.camera_yaw_degrees, 0f);
                view.fieldOfView = applied.camera_fov;
                accepted = Array.TrueForAll(points, p => Inside(view, p, settings.viewportMargin));
                if (accepted) break;
            }
            test.SampleAtTime(0f);
            test.ResetPlayback();
            if (!accepted) throw new InvalidOperationException("No valid camera found in 32 deterministic attempts.");
            applied.framing_checked = true;
            LastApplied = applied;
            return applied;
        }

        public static Vector3[] FramingPoints(FightAnimationTest test)
        {
            var points = new System.Collections.Generic.List<Vector3>();
            float impact = test.prepareSeconds + test.punch.length * test.impactNormalizedTime / test.attackSpeed;
            float[] times = { 0f, test.prepareSeconds + 0.15f, impact, impact + 0.2f, impact + 0.5f, test.CycleDuration - 0.1f };
            foreach (float time in times)
            {
                test.SampleAtTime(time);
                foreach (var actor in new[] { test.attacker, test.receiver })
                    foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                        HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
                    {
                        Vector3 point = actor.GetBoneTransform(bone).position;
                        points.Add(point + Vector3.up * 0.2f);
                        points.Add(point - Vector3.up * 0.13f);
                        points.Add(point + Vector3.right * 0.12f);
                        points.Add(point - Vector3.right * 0.12f);
                    }
            }
            return points.ToArray();
        }

        public static bool Inside(Camera camera, Vector3 point, float margin)
        {
            Vector3 p = camera.WorldToViewportPoint(point);
            return p.z > camera.nearClipPlane && p.z < camera.farClipPlane &&
                p.x >= margin && p.x <= 1f - margin && p.y >= margin && p.y <= 1f - margin;
        }

        private void ValidateSettings()
        {
            if (settings == null) throw new InvalidOperationException("Missing randomization settings.");
            foreach (var range in new[] { settings.animationSpeed, settings.pairYawDegrees, settings.attackerYawDegrees,
                settings.cameraOrbitDegrees, settings.cameraDistance, settings.cameraHeight, settings.cameraYawDegrees,
                settings.cameraPitchDegrees, settings.cameraFov })
                if (float.IsNaN(range.x) || float.IsInfinity(range.x) || float.IsNaN(range.y) || float.IsInfinity(range.y) || range.x > range.y)
                    throw new InvalidOperationException("Invalid randomization range.");
            if (settings.animationSpeed.x < 0.1f || settings.cameraDistance.x < 1f || settings.cameraHeight.x < 0.5f ||
                settings.cameraFov.x < 15f || settings.cameraFov.y > 90f || settings.viewportMargin < 0.01f || settings.viewportMargin > 0.2f)
                throw new InvalidOperationException("Unsafe camera or playback bounds.");
        }

        private static float Pick(System.Random random, Vector2 range) => range.x + (range.y - range.x) * (float)random.NextDouble();
    }
}
