using System;
using System.Linq;
using FightDataset.Prototype;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

public static class FightAnimationTestBuilder
{
    public const string ScenePath = "Assets/Scenes/TwoCharacterPunchTest.unity";

    [MenuItem("Tools/Fight Dataset/Create Two Character Punch Test")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play mode first.");
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath))
            throw new InvalidOperationException("Test scene already exists; open it to run the test.");
        var models = AssetDatabase.FindAssets("t:Model", new[] { "Assets/Characters" })
            .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (models.Length < 2) throw new InvalidOperationException("Two character models are required.");
        var idle = GetClip("Assets/Animations/Neutral");
        var punch = GetClip("Assets/Animations/Attacks/Punch");
        var reaction = GetClip("Assets/Animations/Reactions");
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        var root = new GameObject("Two Character Punch Test");
        // Keep the test away from objects in the user's still-open, unsaved SampleScene.
        var origin = new Vector3(30f, 0f, 0f);
        root.transform.position = origin;
        var a = Spawn(models[0], "Attacker", root.transform, Quaternion.identity, scene);
        var b = Spawn(models[1], "Receiver", root.transform, Quaternion.Euler(0f, 180f, 0f), scene);
        var test = root.AddComponent<FightAnimationTest>();
        test.attacker = a;
        test.receiver = b;
        test.idle = idle;
        test.punch = punch;
        test.reaction = reaction;
        NormalizeAndGround(a, idle);
        NormalizeAndGround(b, idle);
        Calibrate(test);

        var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "Test Floor";
        floor.transform.SetParent(root.transform);
        floor.transform.localPosition = new Vector3(0f, -0.08f, 0.5f);
        floor.transform.localScale = new Vector3(7f, 0.16f, 7f);
        EnsureFolder("Assets/Materials");
        EnsureFolder("Assets/Materials/AnimationTest");
        var floorMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        floorMaterial.name = "TestFloor";
        floorMaterial.SetColor("_BaseColor", new Color(0.22f, 0.25f, 0.3f));
        const string matPath = "Assets/Materials/AnimationTest/TestFloor.mat";
        if (AssetDatabase.LoadMainAssetAtPath(matPath))
        {
            UnityEngine.Object.DestroyImmediate(floorMaterial);
            floorMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        }
        else AssetDatabase.CreateAsset(floorMaterial, matPath);
        floor.GetComponent<Renderer>().sharedMaterial = floorMaterial;

        var light = new GameObject("Test Key Light", typeof(Light)).GetComponent<Light>();
        light.transform.SetParent(root.transform);
        light.transform.localPosition = new Vector3(0f, 4f, 0f);
        light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
        light.type = LightType.Directional;
        light.intensity = 1.5f;
        var camera = new GameObject("Test Camera", typeof(Camera)).GetComponent<Camera>();
        camera.transform.SetParent(root.transform);
        camera.transform.localPosition = new Vector3(3.6f, 2.2f, -2.5f);
        camera.transform.LookAt(origin + new Vector3(0f, 1f, 0.5f));
        camera.fieldOfView = 42f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 15f;
        camera.depth = 10f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.08f, 0.1f, 0.15f);

        Sample(a, idle, 0f);
        Sample(b, idle, 0f);
        root.AddComponent<FightScenarioRandomizer>().CaptureBaseline();
        if (!EditorSceneManager.SaveScene(scene, ScenePath))
            throw new InvalidOperationException("Could not save test scene.");
        Selection.activeGameObject = root;
        AssetDatabase.SaveAssets();
        Debug.Log($"Punch test ready: impact={test.impactNormalizedTime:F3}, hand={test.attackingHand}, " +
            $"approximate head error={test.calibratedHeadError:F3} m. Press Play to repeat.");
    }

    private static Animator Spawn(string path, string name, Transform parent, Quaternion rotation, Scene scene)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(path), scene);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localRotation = rotation;
        var animator = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
        if (!animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
            throw new InvalidOperationException("Invalid Humanoid avatar: " + path);
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        var renderers = go.GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (var renderer in renderers) renderer.updateWhenOffscreen = true;
        float lowest = renderers.Min(r => r.bounds.min.y);
        go.transform.position += Vector3.up * (parent.position.y - lowest);
        return animator;
    }

    private static AnimationClip GetClip(string folder)
    {
        var clips = AssetDatabase.FindAssets("t:AnimationClip", new[] { folder })
            .Select(AssetDatabase.GUIDToAssetPath).Distinct()
            .SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__") && c.isHumanMotion && c.length > 0.1f)
            .ToArray();
        if (clips.Length != 1) throw new InvalidOperationException("Expected one Humanoid clip in " + folder);
        return clips[0];
    }

    private static void Calibrate(FightAnimationTest test)
    {
        var animator = test.attacker;
        float bestReach = float.NegativeInfinity;
        Vector3 fist = Vector3.zero;
        for (int frame = 6; frame <= 54; frame++)
        {
            float normalized = frame / 60f;
            Sample(animator, test.punch, test.punch.length * normalized);
            foreach (var bone in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
            {
                Vector3 position = animator.GetBoneTransform(bone).position;
                float reach = animator.transform.InverseTransformPoint(position).z;
                if (reach <= bestReach) continue;
                bestReach = reach;
                fist = position;
                test.impactNormalizedTime = normalized;
                test.attackingHand = bone;
            }
        }
        Sample(test.receiver, test.idle, 0f);
        Vector3 head = test.receiver.GetBoneTransform(HumanBodyBones.Head).position;
        Vector3 delta = fist - head + Vector3.forward * 0.1f;
        delta.y = 0f;
        test.receiver.transform.position += delta;
        Vector3 local = test.receiver.transform.localPosition;
        local.z = Mathf.Clamp(local.z, 0.65f, 1.5f);
        test.receiver.transform.localPosition = local;
        head = test.receiver.GetBoneTransform(HumanBodyBones.Head).position - Vector3.forward * 0.1f;
        test.calibratedHeadError = Vector3.Distance(fist, head);
    }

    public static void RefreshCalibration()
    {
        var test = UnityEngine.Object.FindFirstObjectByType<FightAnimationTest>();
        if (!test || EditorApplication.isPlaying) throw new InvalidOperationException("Open the test scene in Edit mode.");
        test.attacker.transform.localPosition = Vector3.zero;
        test.receiver.transform.localPosition = Vector3.zero;
        NormalizeAndGround(test.attacker, test.idle);
        NormalizeAndGround(test.receiver, test.idle);
        Calibrate(test);
        Sample(test.attacker, test.idle, 0f);
        Sample(test.receiver, test.idle, 0f);
        EditorSceneManager.MarkSceneDirty(test.gameObject.scene);
        EditorSceneManager.SaveScene(test.gameObject.scene);
    }

    private static void NormalizeAndGround(Animator animator, AnimationClip idle)
    {
        Sample(animator, idle, 0f);
        float headToFoot = animator.GetBoneTransform(HumanBodyBones.Head).position.y -
            animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
        if (headToFoot <= 0.1f) throw new InvalidOperationException("Invalid character proportions.");
        animator.transform.localScale *= 1.5f / headToFoot;
        Sample(animator, idle, 0f);
        float minimumY = float.PositiveInfinity;
        foreach (var renderer in animator.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var mesh = new Mesh();
            try
            {
                renderer.BakeMesh(mesh);
                foreach (var vertex in mesh.vertices)
                    minimumY = Mathf.Min(minimumY, renderer.transform.TransformPoint(vertex).y);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        if (float.IsInfinity(minimumY)) throw new InvalidOperationException("Character has no mesh.");
        animator.transform.position += Vector3.up * (animator.transform.parent.position.y - minimumY);
    }

    private static void Sample(Animator animator, AnimationClip clip, float time)
    {
        var graph = PlayableGraph.Create("EditorClipSample");
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetTime(time);
            playable.SetSpeed(0);
            var output = AnimationPlayableOutput.Create(graph, "Sample", animator);
            output.SetSourcePlayable(playable);
            graph.Play();
            graph.Evaluate(0f);
        }
        finally { if (graph.IsValid()) graph.Destroy(); }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int slash = path.LastIndexOf('/');
        AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
    }
}
