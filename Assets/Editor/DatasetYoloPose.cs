using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FightDataset;
using UnityEngine;

// Geometric visibility against the SAME baked meshes that produce the RGB frame.
// A joint lies inside the skin: its own anatomical surface is allowed, whereas a
// different limb, another actor or room geometry in front of it is an occluder.
public sealed class DatasetYoloPose
{
    public const string Contract = "coco17_mesh_raycast_anatomical_surface_v1";
    public static readonly string[] Names = { "nose", "left_eye", "right_eye", "left_ear", "right_ear",
        "left_shoulder", "right_shoulder", "left_elbow", "right_elbow", "left_wrist", "right_wrist",
        "left_hip", "right_hip", "left_knee", "right_knee", "left_ankle", "right_ankle" };
    private static readonly HumanBodyBones[] Bones = { HumanBodyBones.LastBone, HumanBodyBones.LeftEye, HumanBodyBones.RightEye,
        HumanBodyBones.LastBone, HumanBodyBones.LastBone, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm, HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
        HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg,
        HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
    [Serializable] public sealed class FaceAnchor
    {
        public int actor, keypoint, vertex;
        public string mesh;
    }
    private readonly DatasetScenarioPlayer player;
    private readonly List<Surface> surfaces = new List<Surface>();
    private readonly Surface[] faces;
    private readonly Surface[][] facialSurfaces;
    private readonly int[][] facialVertices;
    public readonly FaceAnchor[] faceAnchors;

    public DatasetYoloPose(DatasetSceneFactory.Stage stage)
    {
        player = stage.player;
        faces = new Surface[player.actors.Length]; facialVertices = new int[player.actors.Length][];
        facialSurfaces = new Surface[player.actors.Length][];
        var anchors = new List<FaceAnchor>();
        for (int a = 0; a < player.actors.Length; a++)
        {
            var actor = player.actors[a];
            foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (skin.sharedMesh && skin.sharedMesh.triangles.Length > 0)
                    surfaces.Add(new Surface(skin, a, actor));
            var candidates = surfaces.Where(s => s.actor == a && !s.name.ToLowerInvariant().Contains("hair") &&
                !s.name.ToLowerInvariant().Contains("lash")).OrderByDescending(s => s.HeadVertexCount).ToArray();
            if (candidates.Length == 0 || candidates[0].HeadVertexCount < 20)
                throw new InvalidOperationException("YOLO Pose: cannot fit facial landmarks for " + stage.modelPaths[a]);
            faces[a] = candidates[0];
            facialVertices[a] = FitFace(actor, faces[a]);
            facialSurfaces[a] = Enumerable.Repeat(faces[a], 5).ToArray();
            var eyes = surfaces.FirstOrDefault(s => s.actor == a && s.name.ToLowerInvariant() == "eyes");
            Vector3 right = actor.GetBoneTransform(HumanBodyBones.RightUpperArm).position - actor.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
            right.y = 0; Vector3 forward = Vector3.Cross(right.normalized, Vector3.up).normalized;
            for (int k = 1; k <= 2; k++)
            {
                var bone = actor.GetBoneTransform(Bones[k]);
                if (eyes == null || !bone) continue;
                Vector3 cornea = bone.position + forward * .012f;
                facialSurfaces[a][k] = eyes;
                facialVertices[a][k] = Enumerable.Range(0, eyes.vertices.Length).OrderBy(i => (eyes.vertices[i] - cornea).sqrMagnitude).First();
            }
            for (int k = 0; k < 5; k++) anchors.Add(new FaceAnchor { actor = a, keypoint = k, vertex = facialVertices[a][k], mesh = facialSurfaces[a][k].name });
        }
        foreach (var filter in stage.root.GetComponentsInChildren<MeshFilter>())
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            if (renderer && renderer.enabled && filter.sharedMesh && !filter.GetComponentInParent<Animator>())
                surfaces.Add(new Surface(filter.sharedMesh, filter.transform, renderer, -1, null));
        }
        faceAnchors = anchors.ToArray();
    }
    public void Bind(SkinnedMeshRenderer source, Mesh baked)
    {
        var s = surfaces.FirstOrDefault(p => p.skin == source);
        if (s == null) throw new InvalidOperationException("Pose mesh is missing: " + source.name);
        s.baked = baked;
    }
    public void Sample()
    {
        foreach (var surface in surfaces) if (surface.skin) surface.Update();
    }
    public Vector3 Point(int actor, int keypoint)
    {
        if (keypoint < 5) return facialSurfaces[actor][keypoint].vertices[facialVertices[actor][keypoint]];
        var bone = player.actors[actor].GetBoneTransform(Bones[keypoint]);
        if (!bone) throw new InvalidOperationException("Pose bone is missing: " + Bones[keypoint]);
        return bone.position;
    }
    public string Labels(Camera camera)
    {
        var text = new StringBuilder();
        for (int actor = 0; actor < player.actors.Length; actor++)
        {
            // Tight projected bounds of the deformed body, clipped to the image.
            Vector2 low = Vector2.one * float.PositiveInfinity, high = Vector2.one * float.NegativeInfinity;
            foreach (var surface in surfaces.Where(s => s.actor == actor))
                foreach (Vector3 world in surface.vertices)
                {
                    Vector3 p = camera.WorldToViewportPoint(world);
                    if (p.z < camera.nearClipPlane || p.z > camera.farClipPlane) continue;
                    low = Vector2.Min(low, new Vector2(p.x, 1 - p.y)); high = Vector2.Max(high, new Vector2(p.x, 1 - p.y));
                }
            low = Vector2.Max(Vector2.zero, low); high = Vector2.Min(Vector2.one, high);
            if (high.x <= low.x || high.y <= low.y) continue;
            text.Append('0'); Add(text, (low.x + high.x) / 2); Add(text, (low.y + high.y) / 2);
            Add(text, high.x - low.x); Add(text, high.y - low.y);
            for (int k = 0; k < 17; k++)
            {
                Vector3 world = Point(actor, k), p = camera.WorldToViewportPoint(world);
                if (!InFrame(camera, p)) { text.Append(" 0.0 0.0 0"); continue; }
                Add(text, p.x); Add(text, 1 - p.y);
                text.Append(' ').Append(Visible(camera, actor, k, world) ? '2' : '1');
            }
            text.Append('\n');
        }
        return text.ToString();
    }
    public static bool InFrame(Camera camera, Vector3 viewport) => viewport.z >= camera.nearClipPlane && viewport.z <= camera.farClipPlane &&
        viewport.x >= 0 && viewport.x <= 1 && viewport.y >= 0 && viewport.y <= 1;
    private static void Add(StringBuilder text, float value) => text.Append(' ').Append(Mathf.Clamp01(value).ToString("0.000000", CultureInfo.InvariantCulture));

    public bool Visible(Camera camera, int actor, int keypoint, Vector3 point)
    {
        // Avoid a world->viewport->world round trip: float matrix inversion at
        // the room's world offset displaced rays across tiny eyelash triangles.
        Ray ray = LandmarkRay(camera, point, out float minimum);
        float endpoint = Vector3.Dot(point - ray.origin, ray.direction);
        float distance = endpoint + 0.006f;
        Surface first = null; int triangle = -1; Vector3 bary = default;
        foreach (var surface in surfaces)
            if (surface.Enabled(camera) && surface.Cast(ray, ref distance, out int t, out Vector3 b, minimum)) { first = surface; triangle = t; bary = b; }
        if (first == null) return true;
        if (first.actor != actor) return false;
        if (keypoint < 5) return endpoint - distance <= 0.001f;
        float clearance = keypoint <= 6 ? 0.16f : keypoint <= 8 ? 0.11f : keypoint <= 10 ? 0.085f : keypoint <= 12 ? 0.19f : keypoint <= 14 ? 0.12f : 0.09f;
        return endpoint - distance <= clearance && first.RegionWeight(triangle, bary, keypoint) >= 0.2f;
    }

    private static Ray LandmarkRay(Camera camera, Vector3 point, out float minimum)
    {
        Vector3 delta = point - camera.transform.position, forward = camera.transform.forward;
        if (camera.orthographic)
        {
            minimum = camera.nearClipPlane;
            return new Ray(point - forward * Vector3.Dot(delta, forward), forward);
        }
        Vector3 direction = delta.normalized;
        minimum = camera.nearClipPlane / Mathf.Max(.00001f, Vector3.Dot(direction, forward));
        return new Ray(camera.transform.position, direction);
    }

    public string VisibilityTrace(Camera camera, int actor, int keypoint)
    {
        Vector3 point = Point(actor, keypoint);
        Ray ray = LandmarkRay(camera, point, out float minimum);
        float end = Vector3.Dot(point - ray.origin, ray.direction), distance = end + .006f;
        Surface first = null; int triangle = -1; Vector3 bary = default;
        foreach (var surface in surfaces)
            if (surface.Enabled(camera) && surface.Cast(ray, ref distance, out int t, out Vector3 b, minimum)) { first = surface; triangle = t; bary = b; }
        return "first=" + first?.name + " actor=" + first?.actor + " gap=" + (end - distance).ToString("F8", CultureInfo.InvariantCulture) + " triangle=" + triangle + " bary=" + bary.ToString("F6") + " visible=" + Visible(camera, actor, keypoint, point);
    }

    private static int[] FitFace(Animator actor, Surface face)
    {
        Vector3 right = actor.GetBoneTransform(HumanBodyBones.RightUpperArm).position - actor.GetBoneTransform(HumanBodyBones.LeftUpperArm).position;
        right.y = 0; right.Normalize();
        Vector3 forward = Vector3.Cross(right, Vector3.up).normalized;
        Vector3 origin = actor.GetBoneTransform(HumanBodyBones.Head).position;
        var indices = Enumerable.Range(0, face.vertices.Length).Where(i => face.headWeights[i] >= 0.75f).ToArray();
        var local = face.vertices.Select(p => new Vector3(Vector3.Dot(p - origin, right), (p - origin).y, Vector3.Dot(p - origin, forward))).ToArray();
        float minY = indices.Min(i => local[i].y), maxY = indices.Max(i => local[i].y);
        float half = (indices.Max(i => local[i].x) - indices.Min(i => local[i].x)) / 2;
        float cx = (indices.Max(i => local[i].x) + indices.Min(i => local[i].x)) / 2;
        float range = maxY - minY;
        var noseCandidates = indices.Where(i => Mathf.Abs(local[i].x - cx) < half * .22f && local[i].y > minY + range * .33f && local[i].y < minY + range * .64f).ToArray();
        if (noseCandidates.Length == 0) throw new InvalidOperationException("Facial mesh has no nose candidates: " + face.name);
        int nose = noseCandidates.OrderByDescending(i => local[i].z).First();
        float noseY = local[nose].y, noseZ = local[nose].z;
        int Pick(float x, float y, float z) => indices.OrderBy(i =>
            (local[i] - new Vector3(x, y, z)).sqrMagnitude).First();
        // Surface anchors (not eye-bone centres) allow the head itself to occlude them.
        return new[] { nose,
            Pick(cx - half * .38f, noseY + range * .09f, noseZ - range * .09f),
            Pick(cx + half * .38f, noseY + range * .09f, noseZ - range * .09f),
            Pick(cx - half * .94f, noseY + range * .05f, noseZ - range * .42f),
            Pick(cx + half * .94f, noseY + range * .05f, noseZ - range * .42f) };
    }

    private sealed class Surface
    {
        private sealed class Node { public Bounds bounds; public int start, count, left = -1, right = -1; }
        public readonly SkinnedMeshRenderer skin;
        public Mesh baked;
        public readonly string name;
        public readonly int actor;
        public readonly float[] headWeights;
        public int HeadVertexCount => headWeights.Count(w => w >= .75f);
        public Vector3[] vertices;
        private readonly Mesh source;
        private readonly Transform transform;
        private readonly Renderer renderer;
        private readonly int[] triangles, order, materialIndices;
        private readonly MaterialInfo[] materials;
        private readonly Vector2[] uv;
        private readonly BoneWeight[] weights;
        private readonly uint[] boneRegions;
        private readonly List<Node> nodes = new List<Node>();

        public Surface(SkinnedMeshRenderer skin, int actor, Animator animator) : this(skin.sharedMesh, skin.transform, skin, actor, animator)
        { this.skin = skin; }
        public Surface(Mesh mesh, Transform transform, Renderer renderer, int actor, Animator animator)
        {
            source = mesh; this.transform = transform; this.renderer = renderer; this.actor = actor; name = renderer.name;
            triangles = mesh.triangles; order = Enumerable.Range(0, triangles.Length / 3).ToArray();
            uv = mesh.uv; weights = actor >= 0 ? mesh.boneWeights : Array.Empty<BoneWeight>();
            materials = (renderer.sharedMaterials.Length > 0 ? renderer.sharedMaterials : new Material[] { null }).Select(m => new MaterialInfo(m)).ToArray();
            materialIndices = new int[triangles.Length / 3]; int offset = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int count = (int)mesh.GetIndexCount(s) / 3;
                for (int j = 0; j < count; j++) materialIndices[offset++] = Mathf.Min(s, materials.Length - 1);
            }
            headWeights = new float[mesh.vertexCount];
            if (animator)
            {
                var skinned = (SkinnedMeshRenderer)renderer;
                boneRegions = skinned.bones.Select(b => RegionMask(animator, b)).ToArray();
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                for (int i = 0; i < weights.Length; i++)
                {
                    var w = weights[i];
                    headWeights[i] = Head(w.boneIndex0, w.weight0) + Head(w.boneIndex1, w.weight1) + Head(w.boneIndex2, w.weight2) + Head(w.boneIndex3, w.weight3);
                }
                float Head(int index, float weight) => index >= 0 && index < skinned.bones.Length && skinned.bones[index] &&
                    (skinned.bones[index] == head || skinned.bones[index].IsChildOf(head)) ? weight : 0;
                var probe = new Mesh();
                try { skinned.BakeMesh(probe, true); vertices = probe.vertices; }
                finally { UnityEngine.Object.DestroyImmediate(probe); }
            }
            else vertices = mesh.vertices;
            ToWorld();
            var bounds = order.Select(TriangleBounds).ToArray(); var centers = bounds.Select(b => b.center).ToArray();
            if (order.Length > 0) Build(0, order.Length, bounds, centers);
        }
        public bool Enabled(Camera camera) => renderer && renderer.gameObject.activeInHierarchy &&
            (camera.cullingMask & (1 << renderer.gameObject.layer)) != 0;
        public void Update()
        {
            if (!baked || baked.vertexCount != source.vertexCount) throw new InvalidOperationException("YOLO pose was not bound to the rendered mesh.");
            vertices = baked.vertices; ToWorld(); Refit(0);
        }
        private void ToWorld()
        {
            var matrix = transform.localToWorldMatrix;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
        }
        private Bounds TriangleBounds(int triangle)
        {
            int t = triangle * 3; var b = new Bounds(vertices[triangles[t]], Vector3.zero);
            b.Encapsulate(vertices[triangles[t + 1]]); b.Encapsulate(vertices[triangles[t + 2]]); return b;
        }
        private int Build(int start, int count, Bounds[] bounds, Vector3[] centers)
        {
            int index = nodes.Count; var n = new Node { start = start, count = count }; nodes.Add(n);
            n.bounds = bounds[order[start]];
            for (int i = start + 1; i < start + count; i++) n.bounds.Encapsulate(bounds[order[i]]);
            if (count <= 12) return index;
            Vector3 size = n.bounds.size; int axis = size.x > size.y ? (size.x > size.z ? 0 : 2) : (size.y > size.z ? 1 : 2);
            Array.Sort(order, start, count, Comparer<int>.Create((a, b) => centers[a][axis].CompareTo(centers[b][axis])));
            int half = count / 2; n.left = Build(start, half, bounds, centers); n.right = Build(start + half, count - half, bounds, centers);
            return index;
        }
        private void Refit(int index)
        {
            var n = nodes[index];
            if (n.left < 0)
            {
                n.bounds = TriangleBounds(order[n.start]);
                for (int i = n.start + 1; i < n.start + n.count; i++) n.bounds.Encapsulate(TriangleBounds(order[i]));
            }
            else { Refit(n.left); Refit(n.right); n.bounds = nodes[n.left].bounds; n.bounds.Encapsulate(nodes[n.right].bounds); }
        }
        public bool Cast(Ray ray, ref float distance, out int triangle, out Vector3 bary, float minimum = 0)
        {
            triangle = -1; bary = default; if (nodes.Count == 0) return false;
            Intersect(0, ray, ref distance, ref triangle, ref bary, minimum); return triangle >= 0;
        }
        private void Intersect(int index, Ray ray, ref float distance, ref int triangle, ref Vector3 bary, float minimum)
        {
            var n = nodes[index]; if (!n.bounds.IntersectRay(ray, out float near) || near > distance) return;
            if (n.left >= 0) { Intersect(n.left, ray, ref distance, ref triangle, ref bary, minimum); Intersect(n.right, ray, ref distance, ref triangle, ref bary, minimum); return; }
            for (int i = n.start; i < n.start + n.count; i++)
            {
                int t = order[i], v = t * 3; Vector3 a = vertices[triangles[v]], e1 = vertices[triangles[v + 1]] - a, e2 = vertices[triangles[v + 2]] - a;
                Vector3 p = Vector3.Cross(ray.direction, e2); float det = Vector3.Dot(e1, p);
                var material = materials[materialIndices[t]];
                if (Mathf.Abs(det) < 1e-10f || (material.cull == 2 && det < 0) || (material.cull == 1 && det > 0)) continue;
                float inv = 1 / det; Vector3 delta = ray.origin - a; float u = Vector3.Dot(delta, p) * inv;
                if (u < -.00001f || u > 1.00001f) continue;
                Vector3 q = Vector3.Cross(delta, e1); float w = Vector3.Dot(ray.direction, q) * inv;
                if (w < -.00001f || u + w > 1.00001f) continue;
                float d = Vector3.Dot(e2, q) * inv;
                if (d < minimum || d > distance) continue;
                Vector3 b = new Vector3(1 - u - w, u, w);
                if (material.alpha != null && uv.Length == vertices.Length && !material.Opaque(uv[triangles[v]] * b.x + uv[triangles[v + 1]] * b.y + uv[triangles[v + 2]] * b.z)) continue;
                distance = d; triangle = t; bary = b;
            }
        }
        public float RegionWeight(int triangle, Vector3 bary, int keypoint)
        {
            if (weights.Length != vertices.Length) return 0;
            int t = triangle * 3;
            return Weight(weights[triangles[t]], keypoint) * bary.x + Weight(weights[triangles[t + 1]], keypoint) * bary.y + Weight(weights[triangles[t + 2]], keypoint) * bary.z;
        }
        private float Weight(BoneWeight w, int k) => Bone(w.boneIndex0, w.weight0, k) + Bone(w.boneIndex1, w.weight1, k) + Bone(w.boneIndex2, w.weight2, k) + Bone(w.boneIndex3, w.weight3, k);
        private float Bone(int index, float weight, int k) => index >= 0 && index < boneRegions.Length && (boneRegions[index] & (1u << k)) != 0 ? weight : 0;
        private static uint RegionMask(Animator actor, Transform bone)
        {
            uint result = 0;
            for (int k = 5; k < 17; k++)
            {
                bool left = k % 2 == 1;
                HumanBodyBones upperArm = left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm,
                    lowerArm = left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm,
                    hand = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand,
                    upperLeg = left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg,
                    lowerLeg = left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
                HumanBodyBones[] group = k <= 6 ? new[] { upperArm, left ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder, HumanBodyBones.Chest, HumanBodyBones.UpperChest } :
                    k <= 8 ? new[] { upperArm, lowerArm } : k <= 10 ? new[] { lowerArm, hand } :
                    k <= 12 ? new[] { upperLeg, HumanBodyBones.Hips, HumanBodyBones.Spine } : k <= 14 ? new[] { upperLeg, lowerLeg } :
                    new[] { lowerLeg, left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot, left ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes };
                if (group.Any(b => actor.GetBoneTransform(b) == bone)) result |= 1u << k;
            }
            return result;
        }
    }

    private sealed class MaterialInfo
    {
        public readonly int cull;
        public readonly Color32[] alpha;
        private readonly int width, height;
        private readonly float cutoff, opacity;
        private readonly Vector2 scale, offset;
        private readonly TextureWrapMode wrap;
        public MaterialInfo(Material m)
        {
            cull = m && m.HasProperty("_Cull") ? Mathf.RoundToInt(m.GetFloat("_Cull")) : 2;
            bool clipped = m && (m.IsKeywordEnabled("_ALPHATEST_ON") || (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > .5f));
            bool transparent = m && m.renderQueue >= 3000;
            if (!clipped && !transparent) return;
            cutoff = clipped && m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : .5f;
            opacity = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").a : m.HasProperty("_Color") ? m.GetColor("_Color").a : 1;
            var texture = m.mainTexture;
            scale = m.mainTextureScale; offset = m.mainTextureOffset;
            if (!texture) { width = height = 1; alpha = new[] { new Color32(255, 255, 255, 255) }; return; }
            width = texture.width; height = texture.height; wrap = texture.wrapMode;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try { Graphics.Blit(texture, target); RenderTexture.active = target; copy.ReadPixels(new Rect(0, 0, width, height), 0, 0); alpha = copy.GetPixels32(); }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.DestroyImmediate(copy); }
        }
        public bool Opaque(Vector2 uv)
        {
            uv = Vector2.Scale(uv, scale) + offset;
            float U(float x) => wrap == TextureWrapMode.Clamp ? Mathf.Clamp01(x) : wrap == TextureWrapMode.Mirror ? Mathf.PingPong(x, 1) : Mathf.Repeat(x, 1);
            int x = Mathf.Clamp(Mathf.FloorToInt(U(uv.x) * width), 0, width - 1), y = Mathf.Clamp(Mathf.FloorToInt(U(uv.y) * height), 0, height - 1);
            return alpha[y * width + x].a / 255f * opacity >= cutoff;
        }
    }
}
