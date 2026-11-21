using System;
using System.Linq;
using FightDataset;
using UnityEngine;

/// <summary>Optional raw rig projections, deliberately not advertised as ready YOLO labels.</summary>
public static class DatasetProjectedJoints
{
    private static readonly string[] Names = { "nose", "left_eye", "right_eye", "left_ear", "right_ear", "left_shoulder", "right_shoulder",
        "left_elbow", "right_elbow", "left_wrist", "right_wrist", "left_hip", "right_hip", "left_knee", "right_knee", "left_ankle", "right_ankle" };
    private static readonly HumanBodyBones[] Bones = { HumanBodyBones.LastBone, HumanBodyBones.LeftEye, HumanBodyBones.RightEye,
        HumanBodyBones.LastBone, HumanBodyBones.LastBone, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };

    [Serializable] private sealed class Point { public string name; public bool available, in_frame; public float x, y; public string visibility = "unknown_occlusion_unchecked"; }
    [Serializable] private sealed class Person { public string actor_id; public Point[] joints; public Rect projected_bounds; }
    [Serializable] private sealed class Projection
    {
        public string schema = "rig_projection_v1", coordinates = "normalized_top_left";
        public bool training_ready = false, occlusion_checked = false;
        public int frame;
        public float time_seconds;
        public Person[] actors;
    }

    public static string Frame(DatasetScenarioPlayer player, Camera camera, Bounds[] bounds, int frame, int fps)
    {
        var people = player.actors.Select((actor, index) => new Person
        {
            actor_id = "actor_" + (index == 0 ? "A" : "B"),
            projected_bounds = ProjectBounds(camera, bounds[index]),
            joints = Bones.Select((bone, i) =>
            {
                var transform = bone == HumanBodyBones.LastBone ? null : actor.GetBoneTransform(bone);
                var point = new Point { name = Names[i], available = transform };
                if (transform)
                {
                    Vector3 p = camera.WorldToViewportPoint(transform.position);
                    point.x = p.x;
                    point.y = 1f - p.y;
                    point.in_frame = p.z > camera.nearClipPlane && p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1;
                }
                return point;
            }).ToArray()
        }).ToArray();
        return JsonUtility.ToJson(new Projection { frame = frame, time_seconds = frame / (float)fps, actors = people });
    }

    private static Rect ProjectBounds(Camera camera, Bounds bounds)
    {
        Vector2 minimum = Vector2.one * float.PositiveInfinity, maximum = Vector2.one * float.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            Vector3 projected = camera.WorldToViewportPoint(p);
            var point = new Vector2(Mathf.Clamp01(projected.x), Mathf.Clamp01(1f - projected.y));
            minimum = Vector2.Min(minimum, point);
            maximum = Vector2.Max(maximum, point);
        }
        return Rect.MinMaxRect(minimum.x, minimum.y, maximum.x, maximum.y);
    }
}
