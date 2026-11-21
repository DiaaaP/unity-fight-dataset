using System;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetGeneratorChecks
{
    [MenuItem("Tools/Fight Dataset/Generator/Validate Generator Without Rendering")]
    private static void Menu() => Debug.Log(JsonUtility.ToJson(Validate(), true));
    [Serializable] public sealed class Result { public bool passed; public int scenarios, poses, cameraViews; public string[] categories; }

    public static Result Validate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Validate in Edit mode.");
        var catalog = DatasetCatalogTools.Refresh();
        var config = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        config.Validate();
        if (catalog.clips.Any(e => string.IsNullOrEmpty(e.key)) || catalog.clips.GroupBy(c => c.key).Any(g => g.Count() > 1)) throw new Exception("Catalog keys not unique.");
        foreach (var item in new[] { Tuple.Create("Assets/Animations/Attacks/Punch/X.fbx", ClipKind.Punch),
            Tuple.Create("Assets/Animations/Attacks/Kick/X.fbx", ClipKind.Kick), Tuple.Create("Assets/Animations/Inbox/Jab.fbx", ClipKind.Punch),
            Tuple.Create("Assets/Animations/Reactions/Block/X.fbx", ClipKind.Block), Tuple.Create("Assets/Animations/Inbox/Dodge.fbx", ClipKind.Dodge),
            Tuple.Create("Assets/Animations/Inbox/Unclear_01.fbx", ClipKind.Unknown) })
            if (DatasetCatalogTools.Classify(item.Item1, out _) != item.Item2) throw new Exception("Classification failed: " + item.Item1);
        var result = new Result { categories = DatasetScenarioPlanner.AvailableCategories(config) };
        string firstPlan = null, firstViews = null;
        foreach (string category in result.categories)
            for (int variant = 0; variant < 4; variant++)
            {
                int seed = 12345 + variant * 7919;
                var scene = EditorSceneManager.NewPreviewScene();
                try
                {
                    var stage = DatasetSceneFactory.Create(config, scene, seed);
                    var plan = DatasetScenarioPlanner.Create(config, stage, category, seed);
                    var views = DatasetScenarioPlanner.Cameras(config, stage, seed);
                    if (category == "fight" && (plan.actions.Count < 2 || plan.actions[0].actor == plan.actions[1].actor)) throw new Exception("Fight must exchange roles.");
                    if (category == "normal" && plan.actions.Count != 0) throw new Exception("Normal scenario has an attack.");
                    for (int i = 0; i < plan.actions.Count; i++)
                    {
                        var action = plan.actions[i];
                        if (action.impactTime < action.start || action.impactTime > action.End ||
                            Mathf.Max(action.End, action.ReactionEnd) + plan.blendOut > plan.duration)
                            throw new Exception("Invalid event timeline.");
                        if (i > 0 && action.start < Mathf.Max(plan.actions[i - 1].End, plan.actions[i - 1].ReactionEnd) + plan.blendOut)
                            throw new Exception("Incompatible action overlap.");
                    }
                    for (float time = 0; time < plan.duration; time += 0.1f)
                    {
                        stage.player.SampleAtTime(time);
                        foreach (var view in views)
                        {
                            view.Apply(stage.camera);
                            foreach (var actor in stage.player.actors)
                                foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                                    if (!FightDataset.Prototype.FightScenarioRandomizer.Inside(stage.camera, actor.GetBoneTransform(bone).position, 0.02f))
                                        throw new Exception("Framing failed: " + category + " seed=" + seed);
                        }
                        result.poses++;
                    }
                    float sampleTime = plan.duration * 0.43f;
                    stage.player.SampleAtTime(sampleTime);
                    Vector3 hand = stage.player.actors[0].GetBoneTransform(HumanBodyBones.LeftHand).position;
                    stage.player.SampleAtTime(plan.duration - 0.01f);
                    stage.player.SampleAtTime(sampleTime);
                    if (Vector3.Distance(hand, stage.player.actors[0].GetBoneTransform(HumanBodyBones.LeftHand).position) > 0.0001f)
                        throw new Exception("Pose sampling depends on previous time.");
                    if (category == result.categories[0] && variant == 0)
                    { firstPlan = JsonUtility.ToJson(plan); firstViews = JsonUtility.ToJson(new Views { values = views }); }
                    result.scenarios++;
                    result.cameraViews += views.Length;
                }
                finally { DatasetSceneFactory.ClosePreviewScene(scene); }
            }
        var repeatScene = EditorSceneManager.NewPreviewScene();
        try
        {
            var repeat = DatasetSceneFactory.Create(config, repeatScene, 12345);
            var plan = DatasetScenarioPlanner.Create(config, repeat, result.categories[0], 12345);
            var views = DatasetScenarioPlanner.Cameras(config, repeat, 12345);
            if (firstPlan != JsonUtility.ToJson(plan) || firstViews != JsonUtility.ToJson(new Views { values = views })) throw new Exception("Same seed changed scenario/camera plan.");
        }
        finally { DatasetSceneFactory.ClosePreviewScene(repeatScene); }
        result.passed = true;
        return result;
    }
    [Serializable] private sealed class Views { public DatasetCameraView[] values; }
}
