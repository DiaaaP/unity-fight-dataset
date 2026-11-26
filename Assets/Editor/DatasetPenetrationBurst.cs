using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// Same sampled points, nearest triangle, oriented surface test and three-ray
// parity contract as the reference guard. Only numerical work enters Burst.
// Unity objects and BakeMesh remain on the Editor thread.
public sealed class DatasetPenetrationBurst : IDisposable
{
    public struct Node { public float3 low, high; public int start, count, left, right; }
    private NativeArray<float3> vertices;
    private NativeArray<int> triangles, order, samples;
    private NativeArray<Node> nodes;
    private NativeArray<float> depths;
    private NativeArray<Vector3> limits;
    private readonly int centroidStep;
    private bool nodesDirty;
    public int Count => depths.Length;
    public NativeArray<Vector3>.ReadOnly WorldPositions => vertices.Reinterpret<Vector3>().AsReadOnly();
    public DatasetPenetrationBurst(Vector3[] positions, int[] indices, int[] sorted, Node[] topology, int[] sampled)
    {
        vertices = new NativeArray<float3>(positions.Length, Allocator.Persistent);
        triangles = new NativeArray<int>(indices, Allocator.Persistent);
        order = new NativeArray<int>(sorted, Allocator.Persistent);
        nodes = new NativeArray<Node>(topology, Allocator.Persistent);
        samples = new NativeArray<int>(sampled, Allocator.Persistent);
        centroidStep = Mathf.Max(1, Mathf.CeilToInt(sorted.Length / 600f));
        depths = new NativeArray<float>(samples.Length + (sorted.Length + centroidStep - 1) / centroidStep, Allocator.Persistent);
        limits = new NativeArray<Vector3>(2, Allocator.Persistent);
        Update(positions);
    }
    public void Update(Vector3[] positions)
    {
        vertices.Reinterpret<Vector3>().CopyFrom(positions);
        RefitNodes();
    }

    public Bounds UpdateFromMesh(Mesh mesh, Matrix4x4 matrix, Vector3[] world)
    {
        var positions = vertices.Reinterpret<Vector3>();
        using (var data = Mesh.AcquireReadOnlyMeshData(mesh)) data[0].GetVertices(positions);
        new TransformPositions { positions = positions, matrix = matrix, limits = limits }.Run();
        if (world != null) positions.CopyTo(world);
        nodesDirty = true;
        return new Bounds((limits[0] + limits[1]) * .5f, limits[1] - limits[0]);
    }

    private void RefitNodes()
    {
        new Refit { vertices = vertices, triangles = triangles, order = order, nodes = nodes }.Run();
        nodesDirty = false;
    }

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    private struct TransformPositions : IJob
    {
        public NativeArray<Vector3> positions, limits;
        public Matrix4x4 matrix;
        public void Execute()
        {
            Vector3 low = Vector3.positiveInfinity, high = Vector3.negativeInfinity;
            for (int i = 0; i < positions.Length; i++)
            {
                var p = matrix.MultiplyPoint3x4(positions[i]);
                positions[i] = p;
                low = Vector3.Min(low, p);
                high = Vector3.Max(high, p);
            }
            limits[0] = low;
            limits[1] = high;
        }
    }
    public float Depth(int index) => depths[index];
    public Bounds BoundsAt(int index)
    {
        if (nodesDirty) RefitNodes();
        var n=nodes[index];float3 low=n.low-new float3(.00002f),high=n.high+new float3(.00002f);
        float3 center=(low+high)*.5f,size=high-low;
        return new Bounds(new Vector3(center.x,center.y,center.z),new Vector3(size.x,size.y,size.z));
    }
    public Vector3 Point(int index)
    {
        float3 p = Sample(vertices, triangles, samples, centroidStep, index);
        return new Vector3(p.x, p.y, p.z);
    }
    public void Query(DatasetPenetrationBurst target)
    {
        if (target.nodesDirty) target.RefitNodes();
        // Preserve the reference's exact normalized ray constants.
        var r0 = new Vector3(1,.173f,.319f).normalized; var r1 = new Vector3(.237f,1,.419f).normalized; var r2 = new Vector3(.357f,.113f,1).normalized;
        new QueryJob { source = vertices, sourceTriangles = triangles, samples = samples, centroidStep = centroidStep,
            vertices = target.vertices, triangles = target.triangles, order = target.order, nodes = target.nodes, depths = depths,
            ray0 = new float3(r0.x,r0.y,r0.z), ray1 = new float3(r1.x,r1.y,r1.z), ray2 = new float3(r2.x,r2.y,r2.z) }.Schedule(Count, 64).Complete();
    }
    private static float3 Sample(NativeArray<float3> v, NativeArray<int> t, NativeArray<int> s, int step, int index)
    {
        if (index < s.Length) return v[s[index]];
        int triangle = (index - s.Length) * step * 3;
        return (v[t[triangle]] + v[t[triangle+1]] + v[t[triangle+2]]) / 3f;
    }
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    private struct Refit : IJob
    {
        [ReadOnly] public NativeArray<float3> vertices;
        [ReadOnly] public NativeArray<int> triangles, order;
        public NativeArray<Node> nodes;
        public void Execute()
        {
            for (int index = nodes.Length - 1; index >= 0; index--)
            {
                var n = nodes[index];
                if (n.left < 0)
                {
                    n.low = new float3(float.PositiveInfinity); n.high = new float3(float.NegativeInfinity);
                    for (int i = n.start; i < n.start+n.count; i++)
                    {
                        int t = order[i]*3;
                        for (int k=0;k<3;k++) { float3 p=vertices[triangles[t+k]]; n.low=math.min(n.low,p); n.high=math.max(n.high,p); }
                    }
                }
                else { n.low=math.min(nodes[n.left].low,nodes[n.right].low); n.high=math.max(nodes[n.left].high,nodes[n.right].high); }
                nodes[index]=n;
            }
        }
    }
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
    private struct QueryJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> source, vertices;
        [ReadOnly] public NativeArray<int> sourceTriangles, samples, triangles, order;
        [ReadOnly] public NativeArray<Node> nodes;
        public int centroidStep;
        public float3 ray0,ray1,ray2;
        [WriteOnly] public NativeArray<float> depths;
        public void Execute(int index)
        {
            float3 p=Sample(source,sourceTriangles,samples,centroidStep,index);
            var root=nodes[0];
            if (math.any(p<root.low) || math.any(p>root.high)) { depths[index]=0; return; }
            float nearest=float.PositiveInfinity; float3 closest=default, normal=default;
            var stack=new FixedList512Bytes<int>();stack.Add(0);
            while(stack.Length>0)
            {
                int ni=stack[stack.Length-1]; stack.Length--; var n=nodes[ni];
                if(Distance(n,p)>=nearest)continue;
                if(n.left>=0)
                {
                    int first=n.left,second=n.right;
                    if(Distance(nodes[first],p)>Distance(nodes[second],p)){first=n.right;second=n.left;}
                    stack.Add(second);stack.Add(first);continue;
                }
                for(int i=n.start;i<n.start+n.count;i++)
                {
                    int t=order[i]*3;float3 a=vertices[triangles[t]],b=vertices[triangles[t+1]],c=vertices[triangles[t+2]];
                    float3 candidate=Closest(p,a,b,c);float squared=math.lengthsq(candidate-p);
                    if(squared>=nearest)continue;
                    nearest=squared;closest=candidate;float3 cross=math.cross(b-a,c-a);float len=math.length(cross);
                    normal=len>1e-5f?cross/len:new float3(0);
                }
            }
            const float threshold=DatasetPenetrationGuard.MaximumDepth;
            if(nearest<=threshold*threshold || math.dot(p-closest,normal)>=-threshold){depths[index]=0;return;}
            int inside=0;
            for(int ray=0;ray<3;ray++)
            {
                float3 direction=ray==0?ray0:ray==1?ray1:ray2;
                var hits=new FixedList4096Bytes<float>(); stack.Clear();stack.Add(0);
                while(stack.Length>0)
                {
                    int ni=stack[stack.Length-1];stack.Length--;var n=nodes[ni];
                    if(!Intersects(n,p,direction))continue;
                    if(n.left>=0){stack.Add(n.right);stack.Add(n.left);continue;}
                    for(int i=n.start;i<n.start+n.count;i++)
                    {
                        int t=order[i]*3;float3 a=vertices[triangles[t]],e1=vertices[triangles[t+1]]-a,e2=vertices[triangles[t+2]]-a;
                        float3 cross=math.cross(direction,e2);float det=math.dot(e1,cross);if(math.abs(det)<1e-9f)continue;
                        float inverse=1/det;float3 delta=p-a;float u=math.dot(delta,cross)*inverse;if(u<0||u>1)continue;
                        float3 q=math.cross(delta,e1);float v=math.dot(direction,q)*inverse;if(v<0||u+v>1)continue;
                        float distance=math.dot(e2,q)*inverse;if(distance<=.00001f)continue;
                        if(hits.Length==hits.Capacity){depths[index]=-1;return;} // exact managed fallback, never silently truncate
                        hits.Add(distance);
                    }
                }
                // Usually a handful of surface crossings; identical sorted-hit deduplication.
                for(int i=1;i<hits.Length;i++){float value=hits[i];int j=i-1;while(j>=0&&hits[j]>value){hits[j+1]=hits[j];j--;}hits[j+1]=value;}
                int count=0;float last=float.NegativeInfinity;
                for(int i=0;i<hits.Length;i++)if(hits[i]-last>.0001f){count++;last=hits[i];}
                if((count&1)!=0)inside++;
                if(inside==2){depths[index]=math.sqrt(nearest);return;}
                if(ray==1&&inside==0){depths[index]=0;return;}
            }
            depths[index]=0;
        }
        private static float Distance(Node n,float3 p)
        { float3 d=math.max(math.max(n.low-p,p-n.high),new float3(0));return math.dot(d,d); }
        private static bool Intersects(Node n,float3 p,float3 direction)
        {
            float3 a=(n.low-p)/direction,b=(n.high-p)/direction;
            return math.cmax(math.min(a,b))<=math.cmin(math.max(a,b)) && math.cmin(math.max(a,b))>=0;
        }
        private static float3 Closest(float3 p,float3 a,float3 b,float3 c)
        {
            float3 ab=b-a,ac=c-a,ap=p-a;float d1=math.dot(ab,ap),d2=math.dot(ac,ap);if(d1<=0&&d2<=0)return a;
            float3 bp=p-b;float d3=math.dot(ab,bp),d4=math.dot(ac,bp);if(d3>=0&&d4<=d3)return b;
            float vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            float3 cp=p-c;float d5=math.dot(ab,cp),d6=math.dot(ac,cp);if(d6>=0&&d5<=d6)return c;
            float vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
            float va=d3*d6-d5*d4;if(va<=0&&d4-d3>=0&&d5-d6>=0)return b+(c-b)*((d4-d3)/((d4-d3)+d5-d6));
            float denominator=va+vb+vc;if(math.abs(denominator)<1e-12f)return a;
            return a+ab*(vb/denominator)+ac*(vc/denominator);
        }
    }
    public void Dispose()
    {
        if (limits.IsCreated) limits.Dispose();
        if(vertices.IsCreated)vertices.Dispose();if(triangles.IsCreated)triangles.Dispose();if(order.IsCreated)order.Dispose();
        if(nodes.IsCreated)nodes.Dispose();if(samples.IsCreated)samples.Dispose();if(depths.IsCreated)depths.Dispose();
    }
}
