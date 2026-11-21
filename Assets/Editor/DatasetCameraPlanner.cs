using System;
using System.Collections.Generic;
using System.Linq;
using FightDataset;
using UnityEngine;

public static class DatasetCameraPlanner
{
    public static readonly string[] Profiles = { "eye_level", "low_angle", "high_angle", "overhead", "over_shoulder", "wide_room" };
    private static readonly Vector2[] Heights = { new Vector2(1.4f, 1.85f), new Vector2(0.25f, 0.7f), new Vector2(2.2f, 2.8f),
        new Vector2(3.05f, 3.4f), new Vector2(1.45f, 1.85f), new Vector2(2.4f, 3.3f) };
    private static readonly Vector2[] Distances = { new Vector2(2.1f, 4.0f), new Vector2(2.6f, 4.3f), new Vector2(2.3f, 4.0f),
        new Vector2(0.12f, 0.9f), new Vector2(2.2f, 3.8f), new Vector2(3.4f, 5.0f) };
    private static readonly HumanBodyBones[] Bones = { HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.Chest,
        HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg };

    public static int ProfileIndex(int seed, int view, DatasetGeneratorConfig config = null)
    {
        unchecked
        {
            uint value = (uint)seed ^ ((uint)view * 0x9e3779b9);
            value ^= value >> 16; value *= 0x7feb352d;
            value ^= value >> 15; value *= 0x846ca68b;
            value ^= value >> 16;
            float[] weights = config ? new[] { config.eyeLevelCameraWeight, config.lowCameraWeight, config.highCameraWeight,
                config.overheadCameraWeight, config.shoulderCameraWeight, config.wideCameraWeight } : new[] { 15f, 5f, 45f, 15f, 5f, 15f };
            double pick = value / ((double)uint.MaxValue + 1) * weights.Sum(w => (double)w);
            for (int i = 0; i < weights.Length; i++) { pick -= weights[i]; if (pick < 0) return i; }
            return weights.Length - 1;
        }
    }

    public static DatasetCameraView[] Create(DatasetGeneratorConfig config, DatasetSceneFactory.Stage stage, int seed)
    {
        DatasetCameraView[] result = null;
        using (var steps = CreateSteps(config, stage, seed, views => result = views))
            while (steps.MoveNext()) { }
        return result;
    }

    public static IEnumerator<object> CreateSteps(DatasetGeneratorConfig config, DatasetSceneFactory.Stage stage, int seed, Action<DatasetCameraView[]> completed)
    {
        var random = new System.Random(seed ^ 0x54117);
        var player = stage.player;
        var points = new List<Vector3>();
        float step = Mathf.Max(0.12f, player.plan.duration / 70f);
        for (float time = 0; time <= player.plan.duration; time += step)
        {
            yield return "Checking camera framing";
            player.SampleAtTime(time);
            foreach (var actor in player.actors)
                foreach (var bone in Bones)
                {
                    Vector3 p = actor.GetBoneTransform(bone).position;
                    points.Add(p + Vector3.up * (bone == HumanBodyBones.Head ? 0.25f : 0.14f));
                    points.Add(p - Vector3.up * 0.14f);
                    points.Add(p + Vector3.right * 0.15f); points.Add(p - Vector3.right * 0.15f);
                    points.Add(p + Vector3.forward * 0.15f); points.Add(p - Vector3.forward * 0.15f);
                }
        }
        var envelope = new Bounds(points[0], Vector3.zero);
        foreach (var point in points) envelope.Encapsulate(point);
        var focus = stage.root.transform.InverseTransformPoint(envelope.center);
        var result = new DatasetCameraView[config.cameraViews];
        stage.camera.aspect = config.width / (float)config.height;
        for (int view = 0; view < result.Length; view++)
        {
            int profile = ProfileIndex(seed, view, config);
            bool accepted = false;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                yield return "Choosing camera " + (view + 1) + "/" + result.Length;
                float low = Mathf.Max(config.cameraHeight.x, Heights[profile].x), high = Mathf.Min(config.cameraHeight.y, Heights[profile].y);
                if (high < low) throw new InvalidOperationException("Camera Height excludes " + Profiles[profile]);
                float height = Pick(random, new Vector2(low, high));
                float angle = Pick(random, new Vector2(0, 360));
                if (profile == 4) angle = player.actors[0].transform.eulerAngles.y + 180 + (random.Next(2) == 0 ? -1 : 1) * Pick(random, new Vector2(35, 65));
                float radius = Pick(random, Distances[profile]);
                Vector3 local = focus + new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad) * radius, 0, Mathf.Cos(angle * Mathf.Deg2Rad) * radius);
                local.y = height;
                if (Mathf.Abs(local.x) > config.roomSize.x / 2 - 0.25f || Mathf.Abs(local.z) > config.roomSize.y / 2 - 0.25f) continue;
                Vector3 position = stage.root.transform.TransformPoint(local);
                Vector3 aim = envelope.center + new Vector3(Pick(random, new Vector2(-0.09f, 0.09f)), Pick(random, new Vector2(-0.08f, 0.08f)), Pick(random, new Vector2(-0.09f, 0.09f)));
                Quaternion rotation = Quaternion.LookRotation(aim - position) * Quaternion.Euler(0, 0, Pick(random, new Vector2(-3f, 3f)));
                stage.camera.transform.SetPositionAndRotation(position, rotation);
                float requiredTangent = 0;
                bool behind = false;
                var inverse = stage.camera.transform.worldToLocalMatrix;
                foreach (var p in points)
                {
                    Vector3 q = inverse.MultiplyPoint3x4(p);
                    if (q.z < 0.15f) { behind = true; break; }
                    requiredTangent = Mathf.Max(requiredTangent, Mathf.Abs(q.y) / q.z, Mathf.Abs(q.x) / (q.z * stage.camera.aspect));
                }
                if (behind) continue;
                int zoom = profile == 5 ? 0 : random.Next(3);
                float fill = Pick(random, zoom == 0 ? new Vector2(0.42f, 0.59f) : zoom == 1 ? new Vector2(0.64f, 0.78f) : new Vector2(0.80f, 0.88f));
                float fov = Mathf.Atan(requiredTangent / fill) * 2 * Mathf.Rad2Deg;
                if (fov > config.cameraFov.y) continue;
                fov = Mathf.Max(config.cameraFov.x, fov);
                var camera = new DatasetCameraView { id = "cam" + (view + 1).ToString("D2"), profile = Profiles[profile], framing = zoom == 0 ? "wide" : zoom == 1 ? "medium" : "close_full_body",
                    position = position, rotationEuler = rotation.eulerAngles, fov = fov, height = height,
                    distance = Vector3.Distance(position, envelope.center), azimuth = Mathf.Repeat(angle, 360),
                    elevation = Mathf.Atan2(height - focus.y, radius) * Mathf.Rad2Deg, screenOccupancy = requiredTangent / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) };
                if (result.Take(view).Any(old => Vector3.Distance(old.position, position) < 0.9f ||
                    (Mathf.Abs(Mathf.DeltaAngle(old.azimuth, camera.azimuth)) < 30 && Mathf.Abs(old.elevation - camera.elevation) < 25))) continue;
                camera.Apply(stage.camera);
                if (points.Any(p => !FightDataset.Prototype.FightScenarioRandomizer.Inside(stage.camera, p, 0.035f))) continue;
                if (!ContactsVisible(stage)) continue;
                result[view] = camera; accepted = true; break;
            }
            if (!accepted) throw new InvalidOperationException("No uncropped visible-contact camera for " + Profiles[profile]);
        }
        player.SampleAtTime(0);
        completed(result);
    }

    public static bool ContactsVisible(DatasetSceneFactory.Stage stage)
    {
        foreach (var action in stage.player.plan.actions)
        {
            stage.player.SampleAtTime(action.impactTime);
            Vector3 endpoint = (action.contactPoint + action.targetContactPoint) * 0.5f;
            Vector3 origin = stage.camera.transform.position;
            foreach (var actor in stage.player.actors)
            {
                using (var geometry = new DatasetContactGeometry(actor))
                    if (geometry.Occludes(origin, endpoint)) return false;
            }
            Vector3 headA = stage.camera.WorldToViewportPoint(stage.player.actors[0].GetBoneTransform(HumanBodyBones.Head).position);
            Vector3 headB = stage.camera.WorldToViewportPoint(stage.player.actors[1].GetBoneTransform(HumanBodyBones.Head).position);
            if (Vector2.Distance(new Vector2(headA.x, headA.y), new Vector2(headB.x, headB.y)) < 0.035f) return false;
        }
        return true;
    }

    private static float Pick(System.Random random, Vector2 range) => DatasetScenarioPlanner.Pick(random, range);
}
