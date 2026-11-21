using System;
using FightDataset.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class FightRandomizationChecks
{
    public static object Validate()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Run checks in Edit mode.");
        var source = UnityEngine.Object.FindFirstObjectByType<FightAnimationTest>();
        if (!source) throw new InvalidOperationException("Test scene not loaded.");
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var clone = UnityEngine.Object.Instantiate(source.gameObject);
            SceneManager.MoveGameObjectToScene(clone, preview);
            var test = clone.GetComponent<FightAnimationTest>();
            var randomizer = clone.GetComponent<FightScenarioRandomizer>() ?? clone.AddComponent<FightScenarioRandomizer>();
            var camera = clone.GetComponentInChildren<Camera>();
            camera.enabled = false;
            if (!clone.GetComponent<FightScenarioRandomizer>()) throw new InvalidOperationException("Randomizer missing.");
            var first = randomizer.ApplySeed(12345);
            string firstJson = JsonUtility.ToJson(first);
            Vector3 firstCamera = camera.transform.position;
            Vector3 firstAttacker = test.attacker.transform.position;
            Vector3 firstReceiver = test.receiver.transform.position;
            test.SampleAtTime(2.1f);
            Vector3 firstHand = test.attacker.GetBoneTransform(test.attackingHand).position;
            randomizer.ApplySeed(98765);
            string repeatJson = JsonUtility.ToJson(randomizer.ApplySeed(12345));
            test.SampleAtTime(2.1f);
            if (firstJson != repeatJson || Vector3.Distance(firstCamera, camera.transform.position) > 0.00001f ||
                Vector3.Distance(firstAttacker, test.attacker.transform.position) > 0.00001f ||
                Vector3.Distance(firstReceiver, test.receiver.transform.position) > 0.00001f ||
                Vector3.Distance(firstHand, test.attacker.GetBoneTransform(test.attackingHand).position) > 0.00001f)
                throw new Exception("Same seed does not reproduce the same scenario.");

            float minimumSpeed = float.PositiveInfinity, maximumSpeed = 0f;
            float minimumImpact = float.PositiveInfinity, maximumImpact = 0f;
            for (int index = 0; index < 12; index++)
            {
                var value = randomizer.ApplySeed(1000 + index * 7919);
                if (test.attackSpeed < 0.8f || test.attackSpeed > 1.2f || test.reactionSpeed != test.attackSpeed)
                    throw new Exception("Unexpected animation speed.");
                minimumSpeed = Mathf.Min(minimumSpeed, test.attackSpeed);
                maximumSpeed = Mathf.Max(maximumSpeed, test.attackSpeed);
                minimumImpact = Mathf.Min(minimumImpact, value.planned_impact_seconds);
                maximumImpact = Mathf.Max(maximumImpact, value.planned_impact_seconds);
                Vector3 cameraPosition = camera.transform.position;
                Quaternion cameraRotation = camera.transform.rotation;
                for (int frame = 0; frame < 100; frame++)
                {
                    test.SampleAtTime(frame / 20f);
                    foreach (var actor in new[] { test.attacker, test.receiver })
                        foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.LeftFoot,
                            HumanBodyBones.RightFoot, HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
                            if (!FightScenarioRandomizer.Inside(camera, actor.GetBoneTransform(bone).position, 0.025f))
                                throw new Exception($"Seed {value.seed}, frame {frame}: actor outside camera.");
                    if (Vector3.Distance(cameraPosition, camera.transform.position) > 0.00001f ||
                        Quaternion.Angle(cameraRotation, camera.transform.rotation) > 0.001f)
                        throw new Exception("Camera changed inside one scenario.");
                }
                test.SampleAtTime(value.planned_impact_seconds - 0.02f);
                if (test.ReceiverActionWeight != 0f) throw new Exception("Reaction begins before impact.");
                test.SampleAtTime(value.planned_impact_seconds + 0.05f);
                if (test.ReceiverActionWeight <= 0f) throw new Exception("Reaction not retimed with speed.");
                test.SampleAtTime(4.9f);
                if (test.AttackerActionWeight != 0f || test.ReceiverActionWeight != 0f)
                    throw new Exception("Actors did not return to Idle inside the test clip.");
            }
            if (maximumSpeed - minimumSpeed < 0.1f || maximumImpact - minimumImpact < 0.1f)
                throw new Exception("Different seeds did not vary speed/timing.");
            return new { passed = true, seeds = 12, checkedFrames = 1200, reproducible = true,
                fixedCameraWithinEachClip = true, speedMin = minimumSpeed, speedMax = maximumSpeed,
                impactMin = minimumImpact, impactMax = maximumImpact };
        }
        finally { EditorSceneManager.ClosePreviewScene(preview); }
    }
}
