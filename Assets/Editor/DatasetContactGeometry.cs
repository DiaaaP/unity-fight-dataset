using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public sealed class DatasetContactGeometry : IDisposable
{
    public struct Contact
    {
        public float gap;
        public Vector3 attack, target;
    }
    private sealed class Part
    {
        public SkinnedMeshRenderer skin;
        public Mesh baked;
        public int[] triangles;
        public BoneWeight[] weights;
        public readonly Dictionary<HumanBodyBones, int[]> limbIndices = new Dictionary<HumanBodyBones, int[]>();
        public readonly Dictionary<HumanBodyBones, int[]> targetTriangles = new Dictionary<HumanBodyBones, int[]>();
        public Vector3[] world;
        public NativeArray<Vector3> positions;
        public readonly Dictionary<int[], NativeArray<int>> indices = new Dictionary<int[], NativeArray<int>>();
    }
    private readonly Animator actor;
    private readonly Part[] parts;

    public DatasetContactGeometry(Animator actor)
    {
        this.actor = actor;
        parts = actor.GetComponentsInChildren<SkinnedMeshRenderer>().Select(s => new Part
        {
            skin = s, baked = new Mesh { name = "ContactProbe" }, triangles = s.sharedMesh.triangles,
            weights = s.sharedMesh.boneWeights
        }).ToArray();
    }

    private void Bake(Part part)
    {
        part.skin.BakeMesh(part.baked, true);
        part.world = part.baked.vertices;
        var matrix = part.skin.transform.localToWorldMatrix;
        for (int i = 0; i < part.world.Length; i++) part.world[i] = matrix.MultiplyPoint3x4(part.world[i]);
    }

    public Vector3[] LimbSurface(HumanBodyBones limb, Vector3? incomingDirection = null)
    {
        var root = actor.GetBoneTransform(limb);
        bool hand = limb == HumanBodyBones.LeftHand || limb == HumanBodyBones.RightHand;
        bool left = limb == HumanBodyBones.LeftHand;
        var knuckles = hand ? new[] { left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal,
            left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal,
            left ? HumanBodyBones.LeftRingProximal : HumanBodyBones.RightRingProximal,
            left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal }
            .Select(actor.GetBoneTransform).Where(b => b).ToArray() : new Transform[0];
        Vector3 handDirection = hand && knuckles.Length > 0 ? (knuckles.Aggregate(Vector3.zero, (sum, b) => sum + b.position) / knuckles.Length - root.position).normalized
            : hand ? (root.position - actor.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm).position).normalized : Vector3.zero;
        var result = new List<Vector3>();
        foreach (var part in parts)
        {
            if (!part.limbIndices.TryGetValue(limb, out var indices))
            {
                // Palm/proximal knuckles, not a stray fingertip or a sleeve at the wrist.
                var boneMask = part.skin.bones.Select(b => b && (hand ? b == root || knuckles.Contains(b) : b == root || b.IsChildOf(root))).ToArray();
                indices = Enumerable.Range(0, part.weights.Length).Where(i =>
                {
                    var w = part.weights[i];
                    float sum = (boneMask[w.boneIndex0] ? w.weight0 : 0) + (boneMask[w.boneIndex1] ? w.weight1 : 0) +
                        (boneMask[w.boneIndex2] ? w.weight2 : 0) + (boneMask[w.boneIndex3] ? w.weight3 : 0);
                    return sum >= 0.45f;
                }).ToArray();
                part.limbIndices[limb] = indices;
            }
            if (indices.Length == 0) continue;
            part.skin.BakeMesh(part.baked, true);
            var local = part.baked.vertices;
            var matrix = part.skin.transform.localToWorldMatrix;
            // Keep a bounded, deterministic sampling of the actual hand/foot surface.
            int step = Mathf.Max(1, indices.Length / 240);
            for (int i = 0; i < indices.Length; i += step)
            {
                var point = matrix.MultiplyPoint3x4(local[indices[i]]);
                if (!hand || Vector3.Dot(point - root.position, handDirection) >= 0.025f) result.Add(point);
            }
        }
        if (result.Count == 0) throw new InvalidOperationException("No weighted mesh surface for " + limb);
        if (incomingDirection.HasValue && incomingDirection.Value.sqrMagnitude > 0.0001f)
        {
            Vector3 direction = incomingDirection.Value.normalized;
            float low = result.Min(p => Vector3.Dot(p, direction)), high = result.Max(p => Vector3.Dot(p, direction));
            float cut = Mathf.Lerp(low, high, hand ? 0.5f : 0.65f);
            result = result.Where(p => Vector3.Dot(p, direction) >= cut).ToList();
        }
        return result.ToArray();
    }

    private static HumanBodyBones[] RegionBones(HumanBodyBones region)
    {
        switch (region)
        {
            case HumanBodyBones.Head: return new[] { HumanBodyBones.Head, HumanBodyBones.Neck, HumanBodyBones.Jaw, HumanBodyBones.LeftEye, HumanBodyBones.RightEye };
            case HumanBodyBones.Chest: return new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest };
            case HumanBodyBones.Hips: return new[] { HumanBodyBones.Hips, HumanBodyBones.Spine };
            case HumanBodyBones.LeftUpperLeg: return new[] { HumanBodyBones.LeftUpperLeg };
            case HumanBodyBones.RightUpperLeg: return new[] { HumanBodyBones.RightUpperLeg };
            case HumanBodyBones.LeftLowerLeg: return new[] { HumanBodyBones.LeftLowerLeg };
            case HumanBodyBones.RightLowerLeg: return new[] { HumanBodyBones.RightLowerLeg };
            default: throw new ArgumentOutOfRangeException(nameof(region), "Unsupported target body region.");
        }
    }

    private int[] Triangles(Part part, HumanBodyBones? region)
    {
        if (!region.HasValue) return part.triangles;
        if (part.targetTriangles.TryGetValue(region.Value, out var found)) return found;
        // Use exact humanoid bone groups. A fist held in front of the chest is not
        // a chest target; descendants would incorrectly include both arms here.
        var bones = RegionBones(region.Value).Select(actor.GetBoneTransform).Where(b => b).ToArray();
        var mask = part.skin.bones.Select(b => b && bones.Contains(b)).ToArray();
        var vertices = part.weights.Select(w =>
            (mask[w.boneIndex0] ? w.weight0 : 0) + (mask[w.boneIndex1] ? w.weight1 : 0) +
            (mask[w.boneIndex2] ? w.weight2 : 0) + (mask[w.boneIndex3] ? w.weight3 : 0) >= 0.4f).ToArray();
        var selected = new List<int>();
        for (int t = 0; t < part.triangles.Length; t += 3)
        {
            int a = part.triangles[t], b = part.triangles[t + 1], c = part.triangles[t + 2];
            if ((vertices[a] ? 1 : 0) + (vertices[b] ? 1 : 0) + (vertices[c] ? 1 : 0) < 2) continue;
            selected.Add(a); selected.Add(b); selected.Add(c);
        }
        return part.targetTriangles[region.Value] = selected.ToArray();
    }

    public Contact Measure(Vector3[] limbSurface, HumanBodyBones? region = null)
    {
        var searchBounds = new Bounds(limbSurface[0], Vector3.zero);
        foreach (var p in limbSurface) searchBounds.Encapsulate(p);
        var patchLow = searchBounds.min;
        var patchHigh = searchBounds.max;
        searchBounds.Expand(.5f);
        using var patch = new NativeArray<Vector3>(limbSurface, Allocator.TempJob);
        using var closest = new NativeArray<Contact>(new[] { new Contact { gap = float.PositiveInfinity } }, Allocator.TempJob);
        // The accumulator holds squared distance until all parts have been visited.
        foreach (var part in parts)
        {
            var indices = Triangles(part, region);
            if (indices.Length == 0) continue;
            if (!part.indices.TryGetValue(indices, out var triangles))
            {
                triangles = new NativeArray<int>(indices, Allocator.Persistent);
                part.indices.Add(indices, triangles);
            }
            part.skin.BakeMesh(part.baked, true);
            if (!part.positions.IsCreated) part.positions = new NativeArray<Vector3>(part.baked.vertexCount, Allocator.Persistent);
            using (var data = Mesh.AcquireReadOnlyMeshData(part.baked)) data[0].GetVertices(part.positions);
            new FindContact { vertices = part.positions, triangles = triangles, patch = patch,
                matrix = part.skin.transform.localToWorldMatrix, low = searchBounds.min, high = searchBounds.max,
                patchLow = patchLow, patchHigh = patchHigh, closest = closest }.Run();
        }
        var result = closest[0];
        result.gap = Mathf.Sqrt(result.gap);
        return result;
    }

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    private struct FindContact : IJob
    {
        public NativeArray<Vector3> vertices;
        [ReadOnly] public NativeArray<int> triangles;
        [ReadOnly] public NativeArray<Vector3> patch;
        public NativeArray<Contact> closest;
        public Matrix4x4 matrix;
        public Vector3 low, high, patchLow, patchHigh;

        public void Execute()
        {
            for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            var best = closest[0];
            for (int t = 0; t < triangles.Length; t += 3)
            {
                var a = vertices[triangles[t]];
                var b = vertices[triangles[t + 1]];
                var c = vertices[triangles[t + 2]];
                var min = Vector3.Min(a, Vector3.Min(b, c));
                var max = Vector3.Max(a, Vector3.Max(b, c));
                if (min.x > high.x || max.x < low.x || min.y > high.y || max.y < low.y || min.z > high.z || max.z < low.z) continue;
                if (BoxDistanceSquared(min, max, patchLow, patchHigh) >= best.gap) continue;
                for (int i = 0; i < patch.Length; i++)
                {
                    var p = patch[i];
                    if (BoxDistanceSquared(min, max, p, p) >= best.gap) continue;
                    var q = ClosestPoint(p, a, b, c);
                    float squared = (q - p).sqrMagnitude;
                    if (squared >= best.gap) continue;
                    best = new Contact { gap = squared, attack = p, target = q };
                }
            }
            closest[0] = best;
        }
    }

    internal Contact MeasureReference(Vector3[] limbSurface, HumanBodyBones? region = null)
    {
        var bounds = new Bounds(limbSurface[0], Vector3.zero);
        foreach (var p in limbSurface) bounds.Encapsulate(p);
        bounds.Expand(0.5f);
        var contact = new Contact { gap = float.PositiveInfinity };
        float bestSquared = float.PositiveInfinity;
        foreach (var part in parts)
        {
            Bake(part);
            var vertices = part.world;
            var triangles = Triangles(part, region);
            for (int triangle = 0; triangle < triangles.Length; triangle += 3)
            {
                Vector3 a = vertices[triangles[triangle]], b = vertices[triangles[triangle + 1]], c = vertices[triangles[triangle + 2]];
                Vector3 low = Vector3.Min(a, Vector3.Min(b, c)), high = Vector3.Max(a, Vector3.Max(b, c));
                if (low.x > bounds.max.x || high.x < bounds.min.x || low.y > bounds.max.y || high.y < bounds.min.y || low.z > bounds.max.z || high.z < bounds.min.z) continue;
                foreach (var p in limbSurface)
                {
                    Vector3 closest = ClosestPoint(p, a, b, c);
                    float squared = (closest - p).sqrMagnitude;
                    if (squared >= bestSquared) continue;
                    bestSquared = squared; contact.attack = p; contact.target = closest;
                }
            }
        }
        contact.gap = Mathf.Sqrt(bestSquared);
        return contact;
    }

    private static float BoxDistanceSquared(Vector3 low, Vector3 high, Vector3 otherLow, Vector3 otherHigh)
    {
        float x = low.x > otherHigh.x ? low.x - otherHigh.x : otherLow.x > high.x ? otherLow.x - high.x : 0;
        float y = low.y > otherHigh.y ? low.y - otherHigh.y : otherLow.y > high.y ? otherLow.y - high.y : 0;
        float z = low.z > otherHigh.z ? low.z - otherHigh.z : otherLow.z > high.z ? otherLow.z - high.z : 0;
        return x * x + y * y + z * z;
    }

    public bool Occludes(Vector3 origin, Vector3 endpoint, float clearance = 0.07f)
    {
        Vector3 segment = endpoint - origin;
        float limit = segment.magnitude - clearance;
        if (limit <= 0) return false;
        Vector3 direction = segment.normalized;
        foreach (var part in parts)
        {
            Bake(part);
            for (int i = 0; i < part.triangles.Length; i += 3)
            {
                Vector3 a = part.world[part.triangles[i]], b = part.world[part.triangles[i + 1]], c = part.world[part.triangles[i + 2]];
                Vector3 edge1 = b - a, edge2 = c - a, cross = Vector3.Cross(direction, edge2);
                float determinant = Vector3.Dot(edge1, cross);
                if (Mathf.Abs(determinant) < 0.0000001f) continue;
                float inverse = 1f / determinant;
                Vector3 delta = origin - a;
                float u = Vector3.Dot(delta, cross) * inverse;
                if (u < 0 || u > 1) continue;
                Vector3 q = Vector3.Cross(delta, edge1);
                float v = Vector3.Dot(direction, q) * inverse;
                if (v < 0 || u + v > 1) continue;
                float distance = Vector3.Dot(edge2, q) * inverse;
                if (distance > 0.01f && distance < limit) return true;
            }
        }
        return false;
    }

    internal static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + d5 - d6));
        float denominator = va + vb + vc;
        if (Mathf.Abs(denominator) < 1e-12f) return a;
        return a + ab * (vb / denominator) + ac * (vc / denominator);
    }

    public void Dispose()
    {
        foreach (var part in parts)
        {
            foreach (var buffer in part.indices.Values) buffer.Dispose();
            if (part.positions.IsCreated) part.positions.Dispose();
            if (part.baked) UnityEngine.Object.DestroyImmediate(part.baked);
        }
    }
}
