using System;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetFallChecks
{
    public static object Validate()
    {
        if (DatasetCatalogTools.Classify("Assets/Animations/Reactions/Stumble/Shoulder Hit And Fall.fbx", out _) != ClipKind.Fall)
            throw new Exception("A fall in the Stumble folder was misclassified.");
        var config = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        var fall = config.catalog.clips.First(c => c.assetPath.Contains("Shoulder Hit And Fall"));
        var scene = EditorSceneManager.NewPreviewScene();
        int samples = 0;
        float maximumFinalHip = 0, minimumBottom = float.PositiveInfinity, maximumFinalBottom = float.NegativeInfinity;
        try
        {
            var stage = DatasetSceneFactory.Create(config, scene, 110562311);
            foreach (float speed in new[] { 0.8f, 1f, 1.2f })
                for (int target = 0; target < 2; target++)
                {
                    stage.player.ResetPlayback();
                    var action = new ScenarioAction { actor = 1 - target, target = target, start = 100,
                        clip = stage.player.neutralClips[1 - target], reaction = fall.clip, reactionKind = "Fall", reactionStart = 1f, reactionSpeed = speed };
                    stage.player.plan = new DatasetScenarioPlan { duration = 10 };
                    stage.player.plan.actions.Add(action);
                    float end = action.ReactionEnd;
                    for (float time = 0; time <= end + 1; time += 0.05f)
                    {
                        stage.player.SampleAtTime(time);
                        float bottom = Bottom(stage.player.actors[target]);
                        if (time > action.reactionStart && bottom < -0.001f) throw new Exception("Fall penetrates the floor.");
                        minimumBottom = Mathf.Min(minimumBottom, bottom);
                        samples++;
                    }
                    stage.player.SampleAtTime(end + 0.7f);
                    Vector3 heldHip = stage.player.actors[target].GetBoneTransform(HumanBodyBones.Hips).position;
                    float finalBottom = Bottom(stage.player.actors[target]);
                    if (heldHip.y > 0.4f || Mathf.Abs(finalBottom) > 0.04f) throw new Exception("Fall floats or returns to standing.");
                    maximumFinalHip = Mathf.Max(maximumFinalHip, heldHip.y);
                    maximumFinalBottom = Mathf.Max(maximumFinalBottom, finalBottom);
                    stage.player.SampleAtTime(0);
                    stage.player.SampleAtTime(end + 0.7f);
                    if (Vector3.Distance(heldHip, stage.player.actors[target].GetBoneTransform(HumanBodyBones.Hips).position) > 0.0001f)
                        throw new Exception("Fall sample depends on sample order.");
                }
        }
        finally { DatasetSceneFactory.ClosePreviewScene(scene); }
        return new { passed = true, samples, characters = 2, speeds = 3, maximumFinalHip, minimumBottom, maximumFinalBottom };
    }

    private static float Bottom(Animator actor)
    {
        float result = float.PositiveInfinity;
        var mesh = new Mesh();
        try
        {
            foreach (var skin in actor.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                skin.BakeMesh(mesh, true);
                foreach (var vertex in mesh.vertices) result = Mathf.Min(result, skin.transform.TransformPoint(vertex).y);
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(mesh); }
        return result;
    }
}
