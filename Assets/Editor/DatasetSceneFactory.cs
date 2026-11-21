using System;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class DatasetSceneFactory
{
    static DatasetSceneFactory()
    {
        EditorSceneManager.sceneClosing += (scene, _) => ReleasePlayers(scene);
        AssemblyReloadEvents.beforeAssemblyReload += ReleaseAllPlayers;
        EditorApplication.quitting += ReleaseAllPlayers;
    }

    private static void ReleaseAllPlayers()
    {
        foreach (var player in Resources.FindObjectsOfTypeAll<DatasetScenarioPlayer>()) player.ResetPlayback();
    }

    private static void ReleasePlayers(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        foreach (var root in scene.GetRootGameObjects())
            foreach (var player in root.GetComponentsInChildren<DatasetScenarioPlayer>(true)) player.ResetPlayback();
    }

    public static void ClosePreviewScene(Scene scene)
    {
        if (!scene.IsValid()) return;
        if (!EditorSceneManager.IsPreviewScene(scene)) throw new InvalidOperationException("Expected a temporary preview scene.");
        ReleasePlayers(scene);
        EditorSceneManager.ClosePreviewScene(scene);
    }
    public const string ScenePath = "Assets/Scenes/FightDatasetPrototype.unity";
    public sealed class Stage
    {
        public GameObject root;
        public DatasetScenarioPlayer player;
        public Camera camera;
        public string[] modelPaths;
    }

    [MenuItem("Tools/Fight Dataset/Generator/2 Build Or Open Generator Room")]
    public static void BuildScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play mode first.");
        var catalog = DatasetCatalogTools.Refresh();
        var config = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        config.Validate();
        for (int i = 0; i < SceneManager.sceneCount; i++)
            if (SceneManager.GetSceneAt(i).path == ScenePath) { SceneManager.SetActiveScene(SceneManager.GetSceneAt(i)); return; }
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath))
        { SceneManager.SetActiveScene(EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive)); return; }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        var stage = Create(config, scene, config.seed);
        string category = DatasetScenarioPlanner.AvailableCategories(config).FirstOrDefault();
        if (category == null) throw new InvalidOperationException("No scenarios possible; inspect catalog warnings.");
        stage.player.plan = DatasetScenarioPlanner.Create(config, stage, category, config.seed);
        stage.player.SampleAtTime(0);
        stage.player.ResetPlayback();
        var views = DatasetScenarioPlanner.Cameras(config, stage, config.seed);
        views[0].Apply(stage.camera);
        stage.player.playAutomatically = true;
        if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Scene save failed.");
        Selection.activeGameObject = stage.root;
    }

    public static Stage Create(DatasetGeneratorConfig config, Scene scene, int seed)
    {
        if (config.catalog.characters.Count == 0) throw new InvalidOperationException("No Humanoid character models in catalog.");
        var random = new System.Random(seed);
        var root = new GameObject("Fight Dataset Generator");
        SceneManager.MoveGameObjectToScene(root, scene);
        root.transform.position = new Vector3(80, 0, 0);
        float x = config.roomSize.x, z = config.roomSize.y, h = config.roomHeight;
        Cube(root, "Floor", new Vector3(0, -0.1f, 0), new Vector3(x, 0.2f, z), Material("Floor", new Color(0.26f, 0.29f, 0.33f)));
        var wall = Material("Walls", new Color(0.64f, 0.67f, 0.7f));
        Cube(root, "Wall North", new Vector3(0, h / 2, z / 2), new Vector3(x + 0.2f, h, 0.15f), wall);
        Cube(root, "Wall South", new Vector3(0, h / 2, -z / 2), new Vector3(x + 0.2f, h, 0.15f), wall);
        Cube(root, "Wall East", new Vector3(x / 2, h / 2, 0), new Vector3(0.15f, h, z), wall);
        Cube(root, "Wall West", new Vector3(-x / 2, h / 2, 0), new Vector3(0.15f, h, z), wall);
        Cube(root, "Ceiling", new Vector3(0, h + 0.1f, 0), new Vector3(x + 0.2f, 0.2f, z + 0.2f), wall);
        root.transform.Find("Ceiling").GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        var key = new GameObject("Room Light", typeof(Light)).GetComponent<Light>();
        key.transform.SetParent(root.transform, false);
        key.transform.localPosition = new Vector3(0, h - 0.3f, 0);
        key.transform.localRotation = Quaternion.Euler(55, -25, 0);
        key.type = LightType.Directional;
        key.intensity = 1.1f + (float)random.NextDouble() * 0.4f;
        var fill = new GameObject("Fill Light", typeof(Light)).GetComponent<Light>();
        fill.transform.SetParent(root.transform, false);
        fill.transform.localRotation = Quaternion.Euler(45, 150, 0);
        fill.type = LightType.Directional;
        fill.intensity = 0.45f;
        var camera = new GameObject("Dataset Camera", typeof(Camera)).GetComponent<Camera>();
        camera.transform.SetParent(root.transform, false);
        camera.nearClipPlane = 0.08f;
        camera.farClipPlane = 20f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.1f, 0.12f, 0.15f);
        camera.depth = 20;
        camera.aspect = config.width / (float)config.height;
        var idle = config.catalog.clips.Where(DatasetCatalogTools.Usable).Where(c => c.kind == ClipKind.Idle).ToArray();
        if (idle.Length == 0) throw new InvalidOperationException("At least one Idle clip is required.");
        int first = random.Next(config.catalog.characters.Count);
        int second = config.catalog.characters.Count == 1 ? first : (first + 1 + random.Next(config.catalog.characters.Count - 1)) % config.catalog.characters.Count;
        var models = new[] { config.catalog.characters[first], config.catalog.characters[second] };
        var player = root.AddComponent<DatasetScenarioPlayer>();
        player.playAutomatically = false;
        player.actors = new Animator[2];
        player.neutralClips = new[] { idle[random.Next(idle.Length)].clip, idle[random.Next(idle.Length)].clip };
        for (int i = 0; i < 2; i++)
        {
            var actor = (GameObject)PrefabUtility.InstantiatePrefab(models[i], scene);
            actor.name = "Actor_" + (i == 0 ? "A" : "B");
            actor.transform.SetParent(root.transform, false);
            var animator = actor.GetComponent<Animator>() ?? actor.AddComponent<Animator>();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(models[i])).OfType<Avatar>().FirstOrDefault(a => a.isHuman && a.isValid);
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            Normalize(animator, player.neutralClips[i]);
            player.actors[i] = animator;
        }
        return new Stage { root = root, player = player, camera = camera, modelPaths = models.Select(AssetDatabase.GetAssetPath).ToArray() };
    }

    public static void Sample(Animator animator, AnimationClip clip, float time)
    {
        var graph = PlayableGraph.Create("CatalogCalibration");
        try
        {
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            var playable = AnimationClipPlayable.Create(graph, clip);
            playable.SetSpeed(0);
            playable.SetTime(Mathf.Clamp(time, 0, clip.length));
            AnimationPlayableOutput.Create(graph, "Pose", animator).SetSourcePlayable(playable);
            graph.Play();
            graph.Evaluate(0);
        }
        finally { DatasetPlayableLifetime.Release(ref graph); }
    }

    private static void Normalize(Animator animator, AnimationClip idle)
    {
        if (!animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman) throw new InvalidOperationException("Invalid character avatar.");
        Sample(animator, idle, 0);
        float height = animator.GetBoneTransform(HumanBodyBones.Head).position.y - animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
        if (height < 0.1f) throw new InvalidOperationException("Invalid character height.");
        animator.transform.localScale *= 1.5f / height;
        Sample(animator, idle, 0);
        float bottom = float.PositiveInfinity;
        foreach (var skin in animator.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            skin.updateWhenOffscreen = true;
            var mesh = new Mesh();
            try
            {
                skin.BakeMesh(mesh, true);
                var matrix = skin.transform.localToWorldMatrix;
                foreach (var vertex in mesh.vertices) bottom = Mathf.Min(bottom, matrix.MultiplyPoint3x4(vertex).y);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        if (float.IsInfinity(bottom)) throw new InvalidOperationException("No skinned character geometry.");
        animator.transform.position += Vector3.up * (animator.transform.parent.position.y - bottom);
    }

    private static void Cube(GameObject root, string name, Vector3 position, Vector3 scale, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(root.transform, false);
        go.transform.localPosition = position;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = material;
    }

    private static Material Material(string name, Color color)
    {
        DatasetCatalogTools.EnsureFolder("Assets/Materials/DatasetGenerator");
        string path = "Assets/Materials/DatasetGenerator/" + name + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material) return material;
        material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
        material.SetColor("_BaseColor", color);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }
}
