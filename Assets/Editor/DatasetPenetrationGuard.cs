using System;
using System.Collections.Generic;
using System.Linq;
using FightDataset;
using UnityEngine;

// Animation sampling bypasses the physics solver. Check the actual deformed meshes
// in both directions, including preparation, follow-through and return to idle.
public sealed class DatasetPenetrationGuard : IDisposable
{
    public const float MaximumDepth = 0.02f;
    public const string Contract = "bidirectional_sampled_mesh_inside_depth_20mm_every_output_frame_v1";
    [Serializable] public sealed class Result
    {
        public float depth;
        public int actor, samples;
        public Vector3 point;
    }
    private readonly Surface[][] actors;
    public DatasetPenetrationGuard(DatasetScenarioPlayer player)
    {
        actors = player.actors.Select(a => a.GetComponentsInChildren<SkinnedMeshRenderer>()
            .Where(s => s.sharedMesh && s.sharedMesh.vertexCount > 0).Select(s => new Surface(s)).ToArray()).ToArray();
    }

    public Result Measure(bool stopOnFirst = false)
    {
        foreach (var actor in actors) foreach (var part in actor) part.Update();
        var result = new Result();
        for (int actor = 0; actor < 2; actor++)
            foreach (var source in actors[actor])
                foreach (var target in actors[1 - actor])
                {
                    if (!source.Bounds.Intersects(target.Bounds)) continue;
                    foreach (Vector3 point in source.SamplePoints())
                    {
                        float depth = target.InsideDepth(point);
                        if (depth <= MaximumDepth) continue;
                        result.samples++;
                        if (depth > result.depth) { result.depth = depth; result.actor = actor; result.point = point; }
                        if (stopOnFirst) return result;
                    }
                }
        return result;
    }

    public void Verify(int frame)
    {
        var result = Measure(true);
        if (result.samples > 0) throw new InvalidOperationException("Actor mesh penetration at frame " + frame +
            ": actor " + result.actor + ", depth=" + result.depth.ToString("F3") + " m, samples=" + result.samples);
    }

    public static void VerifyPlan(DatasetGeneratorConfig config, DatasetScenarioPlayer player, float duration)
    {
        using (var steps = VerifyPlanSteps(config, player, duration))
            while (steps.MoveNext()) { }
    }

    public static IEnumerator<object> VerifyPlanSteps(DatasetGeneratorConfig config, DatasetScenarioPlayer player, float duration)
    {
        using (var guard = new DatasetPenetrationGuard(player))
        {
            int frames = Mathf.CeilToInt(duration * config.fps);
            // Reject obvious failures cheaply, then check every remaining frame.
            // Accepted plans still receive exactly the full output-frame coverage.
            for (int pass = 0; pass < 2; pass++)
            for (int frame = 0; frame < frames; frame += pass == 0 ? 5 : 1)
            {
                if (pass == 1 && frame % 5 == 0) continue;
                player.SampleAtTime(frame / (float)config.fps);
                guard.Verify(frame);
                yield return "Checking intersections " + (frame + 1) + "/" + frames;
            }
        }
        player.SampleAtTime(0);
    }

    public void Dispose() { foreach (var actor in actors) foreach (var part in actor) part.Dispose(); }

    // Fixed topology BVH: build once, refit after skinning. No MeshColliders or
    // Rigidbody state, so checking/replaying frames in any order gives the same pose.
    private sealed class Surface : IDisposable
    {
        private sealed class Node { public Bounds bounds; public int start, count, left = -1, right = -1; }
        private readonly SkinnedMeshRenderer skin;
        private readonly Mesh mesh;
        private readonly int[] triangles, order;
        private readonly List<Node> nodes = new List<Node>();
        private readonly List<float> hits = new List<float>();
        private static readonly Vector3[] Rays = { new Vector3(1, .173f, .319f).normalized,
            new Vector3(.237f, 1, .419f).normalized, new Vector3(.357f, .113f, 1).normalized };
        public Vector3[] vertices;
        public readonly int[] samples;
        public Bounds Bounds => nodes[0].bounds;

        public Surface(SkinnedMeshRenderer source)
        {
            skin = source; mesh = new Mesh { name = "DatasetPenetrationProbe" };
            triangles = source.sharedMesh.triangles;
            order = Enumerable.Range(0, triangles.Length / 3).ToArray();
            int step = Mathf.Max(1, Mathf.CeilToInt(source.sharedMesh.vertexCount / 1800f));
            samples = Enumerable.Range(0, source.sharedMesh.vertexCount).Where(i => i % step == 0).ToArray();
            Bake();
            // Sorting used to recompute triangle bounds on every comparison,
            // millions of times per character. These values are constant while
            // building the topology; refit still uses the live deformed mesh.
            var initialBounds = order.Select(TriangleBounds).ToArray();
            var centers = initialBounds.Select(b => b.center).ToArray();
            Build(0, order.Length, initialBounds, centers);
        }
        private void Bake()
        {
            skin.BakeMesh(mesh, true); vertices = mesh.vertices;
            var matrix = skin.transform.localToWorldMatrix;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
        }
        private Bounds TriangleBounds(int triangle)
        {
            int t = triangle * 3; var bounds = new Bounds(vertices[triangles[t]], Vector3.zero);
            bounds.Encapsulate(vertices[triangles[t + 1]]); bounds.Encapsulate(vertices[triangles[t + 2]]); return bounds;
        }
        private int Build(int start, int count, Bounds[] initialBounds, Vector3[] centers)
        {
            int index = nodes.Count; var node = new Node { start = start, count = count }; nodes.Add(node);
            node.bounds = initialBounds[order[start]];
            for (int i = start + 1; i < start + count; i++) node.bounds.Encapsulate(initialBounds[order[i]]);
            if (count <= 12) return index;
            Vector3 size = node.bounds.size; int axis = size.x > size.y ? (size.x > size.z ? 0 : 2) : (size.y > size.z ? 1 : 2);
            Array.Sort(order, start, count, Comparer<int>.Create((a, b) => centers[a][axis].CompareTo(centers[b][axis])));
            int half = count / 2; node.left = Build(start, half, initialBounds, centers); node.right = Build(start + half, count - half, initialBounds, centers);
            return index;
        }
        public void Update() { Bake(); Refit(0); }
        public IEnumerable<Vector3> SamplePoints()
        {
            foreach (int index in samples) yield return vertices[index];
            // Face interiors catch crossing polygons whose corner vertices all
            // happen to remain outside (e.g. two aligned overlapping boxes).
            int step = Mathf.Max(1, Mathf.CeilToInt(order.Length / 600f));
            for (int i = 0; i < order.Length; i += step)
            {
                int t = i * 3;
                yield return (vertices[triangles[t]] + vertices[triangles[t + 1]] + vertices[triangles[t + 2]]) / 3f;
            }
        }
        private void Refit(int index)
        {
            var node = nodes[index];
            if (node.left < 0)
            {
                node.bounds = TriangleBounds(order[node.start]);
                for (int i = node.start + 1; i < node.start + node.count; i++) node.bounds.Encapsulate(TriangleBounds(order[i]));
            }
            else { Refit(node.left); Refit(node.right); node.bounds = nodes[node.left].bounds; node.bounds.Encapsulate(nodes[node.right].bounds); }
        }
        public float InsideDepth(Vector3 point)
        {
            if (!Bounds.Contains(point)) return 0;
            float nearest = float.PositiveInfinity; Vector3 closest = default, normal = default;
            Nearest(0, point, ref nearest, ref closest, ref normal);
            // Surface contact and slight clothing compression are allowed.
            if (nearest <= MaximumDepth * MaximumDepth) return 0;
            // Open clothing meshes are not closed collision solids. Ray parity
            // alone can classify a point below an open sleeve/trouser leg as inside.
            if (Vector3.Dot(point - closest, normal) >= -MaximumDepth) return 0;
            int inside = 0;
            for (int ray = 0; ray < Rays.Length; ray++)
            {
                hits.Clear(); Intersections(0, new Ray(point, Rays[ray])); hits.Sort();
                int count = 0; float last = float.NegativeInfinity;
                foreach (float hit in hits) if (hit - last > 0.0001f) { count++; last = hit; }
                if ((count & 1) != 0) inside++;
                if (inside == 2) return Mathf.Sqrt(nearest);
                if (ray == 1 && inside == 0) return 0;
            }
            return 0;
        }
        private void Nearest(int index, Vector3 point, ref float best, ref Vector3 closest, ref Vector3 normal)
        {
            var node = nodes[index]; if (node.bounds.SqrDistance(point) >= best) return;
            if (node.left >= 0)
            {
                int first = node.left, second = node.right;
                if (nodes[first].bounds.SqrDistance(point) > nodes[second].bounds.SqrDistance(point)) { first = node.right; second = node.left; }
                Nearest(first, point, ref best, ref closest, ref normal); Nearest(second, point, ref best, ref closest, ref normal); return;
            }
            for (int i = node.start; i < node.start + node.count; i++)
            {
                int t = order[i] * 3;
                Vector3 a = vertices[triangles[t]], b = vertices[triangles[t + 1]], c = vertices[triangles[t + 2]];
                var candidate = DatasetContactGeometry.ClosestPoint(point, a, b, c);
                float squared = (candidate - point).sqrMagnitude;
                if (squared >= best) continue;
                best = squared; closest = candidate; normal = Vector3.Cross(b - a, c - a).normalized;
            }
        }
        private void Intersections(int index, Ray ray)
        {
            var node = nodes[index]; if (!node.bounds.IntersectRay(ray)) return;
            if (node.left >= 0) { Intersections(node.left, ray); Intersections(node.right, ray); return; }
            for (int i = node.start; i < node.start + node.count; i++)
            {
                int t = order[i] * 3;
                Vector3 a = vertices[triangles[t]], e1 = vertices[triangles[t + 1]] - a, e2 = vertices[triangles[t + 2]] - a;
                Vector3 cross = Vector3.Cross(ray.direction, e2); float determinant = Vector3.Dot(e1, cross);
                if (Mathf.Abs(determinant) < 1e-9f) continue;
                float inverse = 1 / determinant; Vector3 delta = ray.origin - a;
                float u = Vector3.Dot(delta, cross) * inverse; if (u < 0 || u > 1) continue;
                Vector3 q = Vector3.Cross(delta, e1); float v = Vector3.Dot(ray.direction, q) * inverse;
                if (v < 0 || u + v > 1) continue;
                float distance = Vector3.Dot(e2, q) * inverse; if (distance > 0.00001f) hits.Add(distance);
            }
        }
        public void Dispose() { if (mesh) UnityEngine.Object.DestroyImmediate(mesh); }
    }
}
