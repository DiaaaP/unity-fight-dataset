using System;
using System.Collections.Generic;
using System.Linq;
using FightDataset;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public static class DatasetCameraPlanner
{
    public static readonly string[] Profiles = { "eye_level", "low_angle", "high_angle", "overhead", "over_shoulder", "wide_room" };
    private static readonly Vector2[] Heights = { new Vector2(1.15f, 2.05f), new Vector2(0.2f, 1.0f), new Vector2(1.9f, 3.35f),
        new Vector2(2.95f, 3.4f), new Vector2(1.2f, 2.05f), new Vector2(2.0f, 3.35f) };
    private static readonly HumanBodyBones[] Bones = { HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.Chest,
        HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg };

    // Owned by one export attempt. Each candidate action starts a fresh capture,
    // so a rejected placement can never supply poses for the accepted one.
    public sealed class FramingSamples
    {
        private Vector3[][][] joints;
        private Bounds[][] boxes;
        private readonly bool partial;

        public FramingSamples(DatasetGeneratorConfig config, int seed) { partial = NeedsBounds(config, seed); }
        internal void BeginTrial(int frames)
        {
            joints = new Vector3[frames][][];
            boxes = partial ? new Bounds[frames][] : null;
        }
        internal void Record(int frame, DatasetScenarioPlayer player, DatasetPenetrationGuard guard)
        {
            joints[frame] = JointPositions(player);
            if (partial) boxes[frame] = guard.RenderedActorBounds();
        }
        internal bool TryGet(int frame, out Vector3[][] actors, out Bounds[] bounds)
        {
            actors = joints != null && frame < joints.Length ? joints[frame] : null;
            bounds = boxes != null && frame < boxes.Length ? boxes[frame] : null;
            return actors != null && (!partial || bounds != null);
        }
    }

    private static Vector3[][] JointPositions(DatasetScenarioPlayer player) =>
        player.actors.Select(actor => Bones.Select(b => actor.GetBoneTransform(b).position).ToArray()).ToArray();

    private static bool NeedsBounds(DatasetGeneratorConfig config, int seed) =>
        Enumerable.Range(0, config.cameraViews).Any(view =>
            new System.Random(unchecked(seed ^ (view + 1) * 0x13579B)).NextDouble() < config.partialFramingProbability);

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    private struct FitAngle : IJob
    {
        [ReadOnly] public NativeArray<Vector3> points;
        public Matrix4x4 inverse;
        public float aspect;
        public NativeArray<float> tangent;

        public void Execute()
        {
            float value = 0;
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 q = inverse.MultiplyPoint3x4(points[i]);
                if (q.z < .15f) { value = -1; break; }
                value = Mathf.Max(value, Mathf.Max(Mathf.Abs(q.y) / q.z, Mathf.Abs(q.x) / (q.z * aspect)));
            }
            tangent[0] = value;
        }
    }

    // Perspective frustum planes are linear half-spaces. If all eight corners
    // are safely inside, every sampled point in the box is also inside.
    private static bool BoxInside(Camera camera, Bounds box, float margin)
    {
        var min = box.min;
        var max = box.max;
        for (int i = 0; i < 8; i++)
        {
            var point = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y,
                (i & 4) == 0 ? min.z : max.z);
            if (!FightDataset.Prototype.FightScenarioRandomizer.Inside(camera, point, margin + .0001f)) return false;
        }
        return true;
    }

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

    public static IEnumerator<object> CreateSteps(DatasetGeneratorConfig config, DatasetSceneFactory.Stage stage, int seed, Action<DatasetCameraView[]> completed, int scenarioIndex = -1, FramingSamples captured = null)
    {
        var random = new System.Random(seed ^ 0x54117);
        var player = stage.player;
        var points = new List<Vector3>();
        var sampledActors = new List<Vector3[][]>();
        float step = Mathf.Max(0.12f, player.plan.duration / 70f);
        bool dense = config.partialFramingProbability > 0;
        int samples = dense ? Mathf.CeilToInt(player.plan.duration * config.fps) : Mathf.CeilToInt(player.plan.duration / step) + 1;
        // An oblique near plane can intersect the rendered body bounds even
        // when every joint projects in front of it. Cache the same bounds used
        // during export so the planner can retry just the camera.
        bool needsBounds = NeedsBounds(config, seed);
        var sampledBounds = new List<Bounds[]>();
        var skins = needsBounds ? player.actors.Select(actor => actor.GetComponentsInChildren<SkinnedMeshRenderer>()).ToArray() : null;
        var probe = needsBounds ? new Mesh { name = "CameraFramingProbe" } : null;
        try
        {
            for (int sampleIndex = 0; sampleIndex < samples; sampleIndex++)
            {
                float time = dense ? sampleIndex / (float)config.fps : Mathf.Min(sampleIndex * step, player.plan.duration);
                yield return "Checking camera framing";
                Vector3[][] actors;
                Bounds[] bounds;
                if (!dense || captured == null || !captured.TryGet(sampleIndex, out actors, out bounds))
                {
                    player.SampleAtTime(time);
                    actors = JointPositions(player);
                    bounds = probe ? MeshBounds(skins, probe) : null;
                }
                else if (sampleIndex == samples - 1)
                {
                    // Shoulder views start from the actor rotation at the final sample.
                    player.SampleAtTime(time);
                }
                if (needsBounds) sampledBounds.Add(bounds);
                sampledActors.Add(actors);
                foreach (var actor in actors)
                    for (int k = 0; k < Bones.Length; k++)
                    {
                        Vector3 p = actor[k];
                        points.Add(p + Vector3.up * (Bones[k] == HumanBodyBones.Head ? 0.25f : 0.14f));
                        points.Add(p - Vector3.up * 0.14f);
                        points.Add(p + Vector3.right * 0.15f); points.Add(p - Vector3.right * 0.15f);
                        points.Add(p + Vector3.forward * 0.15f); points.Add(p - Vector3.forward * 0.15f);
                    }
            }
        }
        finally { if (probe) UnityEngine.Object.DestroyImmediate(probe); }
        var envelope = new Bounds(points[0], Vector3.zero);
        foreach (var point in points) envelope.Encapsulate(point);
        var focus = stage.root.transform.InverseTransformPoint(envelope.center);
        using var fittingPoints = new NativeArray<Vector3>(points.ToArray(), Allocator.Persistent);
        using var tangent = new NativeArray<float>(1, Allocator.Persistent);
        var result = new DatasetCameraView[config.cameraViews];
        stage.camera.aspect = config.width / (float)config.height;
        for (int view = 0; view < result.Length; view++)
        {
            int profile = ProfileIndex(seed, view, config);
            var cropRandom = new System.Random(unchecked(seed ^ (view + 1) * 0x13579B));
            bool partial = cropRandom.NextDouble() < config.partialFramingProbability;
            string crop = new[] { "top", "bottom", "left", "right" }[cropRandom.Next(4)];
            bool occlusion = WantsOcclusion(config, seed, view, scenarioIndex);
            bool accepted = false;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                yield return "Choosing camera " + (view + 1) + "/" + result.Length;
                float low = Mathf.Max(config.cameraHeight.x, Heights[profile].x), high = Mathf.Min(config.cameraHeight.y, Heights[profile].y);
                if (high < low) throw new InvalidOperationException("Camera Height excludes " + Profiles[profile]);
                float height = Pick(random, new Vector2(low, high));
                float angle = Pick(random, new Vector2(0, 360));
                if (profile == 4) angle = player.actors[0].transform.eulerAngles.y + 180 + (random.Next(2) == 0 ? -1 : 1) * Pick(random, new Vector2(35, 65));
                float radius = profile == 3 ? Pick(random, new Vector2(.12f, 1.8f)) : DistanceRadius(random, config.cameraDistanceMeters, Mathf.Abs(height - focus.y));
                if (occlusion)
                {
                    Vector3 line = player.GetActorLocalPosition(1) - player.GetActorLocalPosition(0);
                    angle = Mathf.Atan2(line.x, line.z) * Mathf.Rad2Deg + (random.Next(2) == 0 ? 0 : 180) + Pick(random, new Vector2(-14, 14));
                }
                Vector3 local = focus + new Vector3(Mathf.Sin(angle * Mathf.Deg2Rad) * radius, 0, Mathf.Cos(angle * Mathf.Deg2Rad) * radius);
                local.y = height;
                if (Mathf.Abs(local.x) > config.roomSize.x / 2 - 0.25f || Mathf.Abs(local.z) > config.roomSize.y / 2 - 0.25f) continue;
                Vector3 position = stage.root.transform.TransformPoint(local);
                Vector3 aim = envelope.center + new Vector3(Pick(random, new Vector2(-.22f, .22f)), Pick(random, new Vector2(-.18f, .18f)), Pick(random, new Vector2(-.22f, .22f)));
                Quaternion rotation = Quaternion.LookRotation(aim - position) * Quaternion.Euler(0, 0, Pick(random, config.cameraRollDegrees));
                stage.camera.transform.SetPositionAndRotation(position, rotation);
                new FitAngle { points = fittingPoints, inverse = stage.camera.transform.worldToLocalMatrix,
                    aspect = stage.camera.aspect, tangent = tangent }.Run();
                float requiredTangent = tangent[0];
                if (requiredTangent < 0) continue;
                int zoom = random.Next(3);
                float fill = Pick(random, zoom == 0 ? new Vector2(.22f, .43f) : zoom == 1 ? new Vector2(.46f, .73f) : new Vector2(.78f, .93f));
                float fov = Mathf.Atan(requiredTangent / fill) * 2 * Mathf.Rad2Deg;
                if (fov > config.cameraFov.y) continue;
                fov = Mathf.Max(config.cameraFov.x, fov);
                var camera = new DatasetCameraView { id = "cam" + (view + 1).ToString("D2"), profile = Profiles[profile], framing = zoom == 0 ? "wide" : zoom == 1 ? "medium" : "close_full_body",
                    position = position, rotationEuler = rotation.eulerAngles, fov = fov, height = height, occlusionChallenge = occlusion,
                    distance = Vector3.Distance(position, envelope.center), azimuth = Mathf.Repeat(angle, 360),
                    elevation = Mathf.Atan2(height - focus.y, radius) * Mathf.Rad2Deg, screenOccupancy = requiredTangent / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) };
                if (result.Take(view).Any(old => Vector3.Distance(old.position, position) < 0.9f ||
                    (Mathf.Abs(Mathf.DeltaAngle(old.azimuth, camera.azimuth)) < 30 && Mathf.Abs(old.elevation - camera.elevation) < 25))) continue;
                camera.Apply(stage.camera);
                if (!BoxInside(stage.camera, envelope, .035f) &&
                    points.Any(p => !FightDataset.Prototype.FightScenarioRandomizer.Inside(stage.camera, p, .035f))) continue;
                if (partial)
                {
                    if (!TryPartial(stage, sampledActors, camera, crop, cropRandom, config)) continue;
                    camera.Apply(stage.camera);
                    if (sampledBounds.Any(frame => frame.Any(box => !DepthSafe(stage.camera, box, .001f)))) continue;
                }
                if (occlusion && sampledActors.Count(s => ProjectedOverlap(stage.camera, s[0], s[1]) >= .2f) < sampledActors.Count * .5f) continue;
                if (!ContactsVisible(stage, occlusion)) continue;
                result[view] = camera; accepted = true; break;
            }
            if (!accepted) throw new InvalidOperationException("No safe " + (partial ? "partially cropped" : "full-body") + " visible-contact camera for " + Profiles[profile]);
        }
        player.SampleAtTime(0);
        completed(result);
    }

    private static bool TryPartial(DatasetSceneFactory.Stage stage, List<Vector3[][]> samples, DatasetCameraView view,
        string crop, System.Random random, DatasetGeneratorConfig config)
    {
        var camera = stage.camera;
        camera.fieldOfView = Mathf.Max(config.cameraFov.x, view.fov * Pick(random, new Vector2(.8f, .98f)));
        Quaternion original = camera.transform.rotation;
        float sign = crop == "top" || crop == "right" ? 1 : -1;
        for (int shift = 0; shift < 16; shift++)
        {
            Quaternion offset = crop == "top" || crop == "bottom" ? Quaternion.Euler(sign * shift * 1.8f, 0, 0) : Quaternion.Euler(0, -sign * shift * 1.8f, 0);
            camera.transform.rotation = original * offset;
            bool cropped = false, safe = true;
            foreach (var sample in samples)
            {
                foreach (var actor in sample)
                {
                    if (!PartialPointsSafe(camera, actor, config.minimumPartialBodyCoverage, out int inside))
                    {
                        safe = false;
                        break;
                    }
                    cropped |= inside < actor.Length;
                }
                if (!safe) break;
            }
            if (!safe) break;
            if (!cropped) continue;
            // Keep the event itself in frame. Cropping may hide a head, foot or
            // body edge, but must not turn a contact video into an offscreen event.
            if (stage.player.plan.actions.Any(a => !FightDataset.Prototype.FightScenarioRandomizer.Inside(camera, (a.contactPoint + a.targetContactPoint) * .5f, .025f))) continue;
            view.rotationEuler = camera.transform.eulerAngles; view.fov = camera.fieldOfView;
            view.allowsPartialBody = true; view.minimumBodyCoverage = config.minimumPartialBodyCoverage;
            view.crop = crop; view.framing = "partial_" + crop;
            view.elevation = Mathf.DeltaAngle(0, camera.transform.eulerAngles.x);
            return true;
        }
        view.Apply(camera);
        return false;
    }

    public static bool PartialPointsSafe(Camera camera, Vector3[] points, float minimumCoverage)
        => PartialPointsSafe(camera, points, minimumCoverage, out _);

    private static bool PartialPointsSafe(Camera camera, Vector3[] points, float minimumCoverage, out int inside)
    {
        Vector2 low = Vector2.one * float.PositiveInfinity, high = Vector2.one * float.NegativeInfinity;
        inside = 0;
        float near = camera.nearClipPlane + .03f, far = camera.farClipPlane;
        foreach (Vector3 point in points)
        {
            Vector3 p = camera.WorldToViewportPoint(point);
            // Image-edge crops are supported; near/far-plane slicing is rejected.
            if (p.z <= near || p.z >= far) return false;
            Vector2 xy = new Vector2(p.x, p.y); low = Vector2.Min(low, xy); high = Vector2.Max(high, xy);
            if (p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1) inside++;
        }
        Vector2 clipped = Vector2.Max(Vector2.zero, Vector2.Min(Vector2.one, high) - Vector2.Max(Vector2.zero, low));
        Vector2 size = high - low;
        return inside >= 3 && clipped.x >= .035f && clipped.y >= .12f &&
            clipped.x * clipped.y / Mathf.Max(.000001f, size.x * size.y) >= minimumCoverage;
    }

    public static void ValidatePartialFrame(DatasetSceneFactory.Stage stage, Bounds[] bounds, float minimumCoverage)
    {
        for (int i = 0; i < stage.player.actors.Length; i++)
        {
            var actor = stage.player.actors[i];
            if (!PartialPointsSafe(stage.camera, Bones.Select(b => actor.GetBoneTransform(b).position).ToArray(), minimumCoverage))
                throw new InvalidOperationException("Partial view loses actor " + i + " at time " + stage.player.TimeSeconds);
            // The label exporter clips projected mesh bounds. Keep the complete
            // actual mesh in the supported depth range, even for cropped heads.
            if (!DepthSafe(stage.camera, bounds[i], 0))
                throw new InvalidOperationException("Partial view crosses the camera depth planes.");
        }
    }

    private static Bounds[] MeshBounds(SkinnedMeshRenderer[][] skins, Mesh probe)
    {
        var actors = new Bounds[skins.Length];
        for (int i = 0; i < skins.Length; i++)
        {
            for (int j = 0; j < skins[i].Length; j++)
            {
                var skin = skins[i][j];
                skin.BakeMesh(probe, true);
                probe.RecalculateBounds();
                var world = WorldBounds(probe.bounds, skin.transform.localToWorldMatrix);
                if (j == 0) actors[i] = world;
                else actors[i].Encapsulate(world);
            }
        }
        return actors;
    }

    internal static Bounds WorldBounds(Bounds local, Matrix4x4 matrix)
    {
        Vector3 e = local.extents;
        Vector3 radius = new Vector3(
            Mathf.Abs(matrix.m00)*e.x + Mathf.Abs(matrix.m01)*e.y + Mathf.Abs(matrix.m02)*e.z,
            Mathf.Abs(matrix.m10)*e.x + Mathf.Abs(matrix.m11)*e.y + Mathf.Abs(matrix.m12)*e.z,
            Mathf.Abs(matrix.m20)*e.x + Mathf.Abs(matrix.m21)*e.y + Mathf.Abs(matrix.m22)*e.z);
        return new Bounds(matrix.MultiplyPoint3x4(local.center), radius * 2);
    }

    private static bool DepthSafe(Camera camera, Bounds box, float margin)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            var sign = new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
            float depth = camera.WorldToViewportPoint(box.center + Vector3.Scale(box.extents, sign)).z;
            if (depth <= camera.nearClipPlane + margin || depth >= camera.farClipPlane - margin) return false;
        }
        return true;
    }

    public static float ProjectedOverlap(Camera camera, Vector3[] a, Vector3[] b)
    {
        Vector4 Box(Vector3[] points)
        {
            var p = points.Select(camera.WorldToViewportPoint).ToArray();
            return new Vector4(p.Min(v => v.x) - .025f, p.Min(v => v.y) - .025f, p.Max(v => v.x) + .025f, p.Max(v => v.y) + .025f);
        }
        Vector4 x = Box(a), y = Box(b);
        float area = Mathf.Max(0, Mathf.Min(x.z, y.z) - Mathf.Max(x.x, y.x)) * Mathf.Max(0, Mathf.Min(x.w, y.w) - Mathf.Max(x.y, y.y));
        return area / Mathf.Max(.000001f, Mathf.Min((x.z - x.x) * (x.w - x.y), (y.z - y.x) * (y.w - y.y)));
    }
    private static float DistanceRadius(System.Random random, Vector2 range, float vertical)
    {
        // Log-uniform distance gives close views a real share of draws, while
        // retaining far-room cameras instead of fitting every view to one size.
        float distance = Mathf.Exp(Pick(random, new Vector2(Mathf.Log(range.x), Mathf.Log(range.y))));
        return Mathf.Sqrt(Mathf.Max(.12f * .12f, distance * distance - vertical * vertical));
    }
    public static bool WantsOcclusion(DatasetGeneratorConfig config, int seed, int view, int scenarioIndex)
    {
        if (config.occlusionViewProbability <= 0) return false;
        if (config.occlusionViewProbability >= 1) return true;
        if (scenarioIndex < 0) return new System.Random(seed ^ (view + 1) * 0x6E31).NextDouble() < config.occlusionViewProbability;
        int period = Mathf.Max(1, Mathf.RoundToInt(1 / config.occlusionViewProbability));
        int index = scenarioIndex * config.cameraViews + view, block = index / period;
        // One randomly placed hard view per block (20 at 5%), without tying all
        // hard views to even/odd hit/kick scenario order.
        int position = new System.Random(unchecked(config.seed ^ block * 0x5EED31)).Next(period);
        return index % period == position;
    }
    public static bool ContactsVisible(DatasetSceneFactory.Stage stage, bool allowBodyOcclusion = false)
    {
        foreach (var action in stage.player.plan.actions)
        {
            stage.player.SampleAtTime(action.impactTime);
            Vector3 endpoint = (action.contactPoint + action.targetContactPoint) * 0.5f;
            if (!FightDataset.Prototype.FightScenarioRandomizer.Inside(stage.camera, endpoint, .025f)) return false;
            Vector3 origin = stage.camera.transform.position;
            if (!allowBodyOcclusion) foreach (var actor in stage.player.actors)
            {
                using (var geometry = new DatasetContactGeometry(actor))
                    if (geometry.Occludes(origin, endpoint)) return false;
            }
            Vector3 headA = stage.camera.WorldToViewportPoint(stage.player.actors[0].GetBoneTransform(HumanBodyBones.Head).position);
            Vector3 headB = stage.camera.WorldToViewportPoint(stage.player.actors[1].GetBoneTransform(HumanBodyBones.Head).position);
            if (!allowBodyOcclusion && Vector2.Distance(new Vector2(headA.x, headA.y), new Vector2(headB.x, headB.y)) < 0.035f) return false;
        }
        return true;
    }

    private static float Pick(System.Random random, Vector2 range) => DatasetScenarioPlanner.Pick(random, range);
}
