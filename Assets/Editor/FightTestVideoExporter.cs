using System;
using System.IO;
using System.Linq;
using FightDataset.Prototype;
using UnityEditor;
using UnityEditor.Media;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>One deterministic smoke-test video, encoded by Unity's local MP4 encoder.</summary>
public static class FightTestVideoExporter
{
    public static bool IsRunning { get; private set; }
    public static int FramesWritten { get; private set; }
    public static string Status { get; private set; } = "Not started";
    public static string LastVideo { get; private set; }
    public static string LastMetadata { get; private set; }

    private const int Width = 1280;
    private const int Height = 720;
    private const int Fps = 20;
    private const int FrameCount = 100;
    private static Scene preview;
    private static FightAnimationTest simulation;
    private static Camera camera;
    private static RenderTexture target;
    private static Texture2D pixels;
    private static MediaEncoder encoder;
    private static Metadata metadata;
    private static string temporaryVideo;
    private static BakedSkin[] bakedSkins;

    [MenuItem("Tools/Fight Dataset/Export Randomized Test MP4")]
    private static void ExportFromMenu() => StartExport();

    [MenuItem("Tools/Fight Dataset/Export Fixed Test MP4")]
    private static void ExportFixedFromMenu() => StartExport(0, false);

    public static string StartExport(int? seed = null, bool randomize = true)
    {
        if (IsRunning) throw new InvalidOperationException("An export is already running.");
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            throw new InvalidOperationException("Export in Edit mode after compilation finishes.");
        var source = UnityEngine.Object.FindFirstObjectByType<FightAnimationTest>();
        if (!source) throw new InvalidOperationException("Open TwoCharacterPunchTest first.");
        var sourceCamera = source.GetComponentInChildren<Camera>();
        if (!sourceCamera) throw new InvalidOperationException("Test camera not found.");
        string project = Path.GetDirectoryName(Application.dataPath);
        string output = Path.Combine(project, "DatasetOutput");
        string id = "hit_test_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        Directory.CreateDirectory(Path.Combine(output, "hit"));
        Directory.CreateDirectory(Path.Combine(output, "metadata"));
        LastVideo = Path.Combine(output, "hit", id + "_cam01.mp4");
        LastMetadata = Path.Combine(output, "metadata", id + ".json");
        temporaryVideo = Path.Combine(output, "hit", id + "_cam01.partial.mp4");
        if (File.Exists(LastVideo) || File.Exists(LastMetadata) || File.Exists(temporaryVideo))
            throw new IOException("Output ID already exists.");
        FramesWritten = 0;
        Status = "Rendering";
        IsRunning = true;
        try
        {
            preview = EditorSceneManager.NewPreviewScene();
            var clone = UnityEngine.Object.Instantiate(source.gameObject);
            SceneManager.MoveGameObjectToScene(clone, preview);
            simulation = clone.GetComponent<FightAnimationTest>();
            simulation.enabled = false;
            var randomizer = clone.GetComponent<FightScenarioRandomizer>();
            if (!randomizer)
            {
                randomizer = clone.AddComponent<FightScenarioRandomizer>();
                randomizer.CaptureBaseline();
            }
            FightScenarioRandomizer.AppliedParameters parameters = null;
            if (randomize) parameters = randomizer.ApplySeed(seed ?? Guid.NewGuid().GetHashCode());
            // In Edit mode, Camera.Render does not reliably refresh every GPU skinning
            // buffer after a manually evaluated PlayableGraph. Freeze an explicit CPU
            // skin of every body/clothing part at the exact sampled animation time.
            bakedSkins = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Select(skin => new BakedSkin(skin)).ToArray();
            camera = clone.GetComponentInChildren<Camera>();
            camera.enabled = false;
            camera.scene = preview;
            camera.aspect = (float)Width / Height;
            metadata = CreateMetadata(simulation, camera, id);
            metadata.seed = parameters?.seed ?? 0;
            metadata.randomization = randomize ? "seeded_once_per_scenario" : "none_fixed_test_scene";
            metadata.randomization_parameters = parameters;
            metadata.randomization_settings = randomize ? randomizer.settings : null;
            target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1,
                name = "FightTestExport"
            };
            target.Create();
            pixels = new Texture2D(Width, Height, TextureFormat.RGBA32, false, false);
            camera.targetTexture = target;
            var video = new VideoTrackAttributes
            {
                width = Width,
                height = Height,
                frameRate = new MediaRational(Fps),
                includeAlpha = false,
                bitRateMode = VideoBitrateMode.High
            };
            encoder = new MediaEncoder(temporaryVideo, video);
            EditorApplication.update += RenderNextFrame;
            AssemblyReloadEvents.beforeAssemblyReload += CancelForReload;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            return LastVideo;
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
    }

    private static void RenderNextFrame()
    {
        if (!IsRunning) return;
        try
        {
            float time = FramesWritten / (float)Fps;
            simulation.SampleAtTime(time);
            if (metadata.randomization_parameters != null)
                foreach (var actor in new[] { simulation.attacker, simulation.receiver })
                    foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.LeftFoot,
                        HumanBodyBones.RightFoot, HumanBodyBones.LeftHand, HumanBodyBones.RightHand })
                        if (!FightScenarioRandomizer.Inside(camera, actor.GetBoneTransform(bone).position, 0.025f))
                            throw new InvalidOperationException("Character leaves the camera frame at frame " + FramesWritten);
            foreach (var skin in bakedSkins) skin.Sample();
            camera.Render();
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                pixels.Apply(false, false);
            }
            finally { RenderTexture.active = previous; }
            if (!encoder.AddFrame(pixels)) throw new IOException("MP4 encoder rejected frame " + FramesWritten);
            metadata.frames[FramesWritten] = new FrameInfo
            {
                frame = FramesWritten,
                time_seconds = time,
                phase = simulation.Phase,
                attacker_action_weight = simulation.AttackerActionWeight,
                receiver_action_weight = simulation.ReceiverActionWeight,
                attacking_hand_world = simulation.attacker.GetBoneTransform(simulation.attackingHand).position,
                receiver_head_world = simulation.receiver.GetBoneTransform(HumanBodyBones.Head).position
            };
            FramesWritten++;
            if (FramesWritten < FrameCount) return;
            encoder.Dispose();
            encoder = null;
            if (new FileInfo(temporaryVideo).Length < 1024) throw new IOException("Encoded video is empty.");
            File.Move(temporaryVideo, LastVideo);
            metadata.status = "encoded_pending_decode_check";
            File.WriteAllText(LastMetadata, JsonUtility.ToJson(metadata, true));
            Status = "Completed";
            Cleanup();
            Debug.Log("Test MP4 exported: " + LastVideo + " (100 frames, 20 FPS, 1280x720)");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode && IsRunning)
            Fail(new OperationCanceledException("Play mode interrupted export."));
    }

    public static void MarkDecodeVerified(string videoPath)
    {
        string stem = Path.GetFileNameWithoutExtension(videoPath);
        const string suffix = "_cam01";
        if (!stem.EndsWith(suffix, StringComparison.Ordinal)) throw new IOException("Unknown camera suffix.");
        string id = stem.Substring(0, stem.Length - suffix.Length);
        string path = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(videoPath)), "metadata", id + ".json");
        var saved = JsonUtility.FromJson<Metadata>(File.ReadAllText(path));
        if (saved.frame_count != FrameCount || saved.frames.Length != FrameCount || saved.fps != Fps)
            throw new IOException("Metadata frame count or FPS mismatch.");
        for (int i = 0; i < FrameCount; i++)
            if (saved.frames[i].frame != i || Mathf.Abs(saved.frames[i].time_seconds - i / (float)Fps) > 0.0001f)
                throw new IOException("Metadata timestamps do not match video frame order.");
        saved.status = "decoded_and_metadata_verified";
        File.WriteAllText(path, JsonUtility.ToJson(saved, true));
    }

    private static void CancelForReload()
    {
        if (IsRunning) Fail(new OperationCanceledException("Compilation interrupted export."));
    }

    private static void Fail(Exception exception)
    {
        Status = "Failed: " + exception.Message;
        Cleanup();
        Debug.LogError(Status + ". Any partial output is retained for diagnosis.");
    }

    private static void Cleanup()
    {
        EditorApplication.update -= RenderNextFrame;
        AssemblyReloadEvents.beforeAssemblyReload -= CancelForReload;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        encoder?.Dispose();
        encoder = null;
        if (camera) camera.targetTexture = null;
        if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
        if (bakedSkins != null) foreach (var skin in bakedSkins) skin.Dispose();
        bakedSkins = null;
        simulation = null;
        camera = null;
        if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
        if (pixels) UnityEngine.Object.DestroyImmediate(pixels);
        target = null;
        pixels = null;
        IsRunning = false;
    }

    private static Metadata CreateMetadata(FightAnimationTest test, Camera view, string id)
    {
        float impact = test.prepareSeconds + test.punch.length * test.impactNormalizedTime / test.attackSpeed;
        return new Metadata
        {
            scenario_id = id,
            scene = test.gameObject.scene.path,
            unity_version = Application.unityVersion,
            video = "hit/" + id + "_cam01.mp4",
            camera = new CameraInfo
            {
                position = view.transform.position,
                rotation_euler = view.transform.eulerAngles,
                field_of_view = view.fieldOfView
            },
            actors = new[] { Actor(test.attacker, "actor_A"), Actor(test.receiver, "actor_B") },
            idle_asset = AssetDatabase.GetAssetPath(test.idle),
            transitions = new TransitionInfo
            {
                attack_in_seconds = test.blendSeconds,
                reaction_in_seconds = test.reactionBlendSeconds,
                return_to_idle_seconds = test.returnBlendSeconds
            },
            attack = new AttackInfo
            {
                clip_asset = AssetDatabase.GetAssetPath(test.punch),
                speed = test.attackSpeed,
                hand = test.attackingHand.ToString(),
                start_seconds = test.prepareSeconds,
                end_seconds = test.prepareSeconds + test.punch.length / test.attackSpeed,
                idle_return_seconds = test.prepareSeconds + test.punch.length / test.attackSpeed + test.returnBlendSeconds,
                blend_seconds = test.blendSeconds,
                impact_normalized_time = test.impactNormalizedTime,
                planned_impact_seconds = impact
            },
            reaction = new ReactionInfo
            {
                clip_asset = AssetDatabase.GetAssetPath(test.reaction),
                speed = test.reactionSpeed,
                start_seconds = impact,
                end_seconds = impact + test.reaction.length / test.reactionSpeed,
                idle_return_seconds = impact + test.reaction.length / test.reactionSpeed + test.returnBlendSeconds
            },
            calibration_error_meters = test.calibratedHeadError,
            frames = new FrameInfo[FrameCount]
        };
    }

    private static ActorInfo Actor(Animator animator, string id) => new ActorInfo
    {
        id = id,
        model_asset = AssetDatabase.GetAssetPath(animator.avatar),
        position = animator.transform.position,
        rotation_euler = animator.transform.eulerAngles,
        scale = animator.transform.localScale
    };

    [Serializable] private sealed class Metadata
    {
        public string schema_version = "smoke_test_v4_smooth_transitions";
        public string scenario_id, scene, unity_version, video, status;
        public string scenario_category = "hit";
        public string event_label = "potential_violence";
        public string outcome = "unverified_contact";
        public bool requires_visual_review = true;
        public int seed = 0;
        public string randomization = "none_fixed_test_scene";
        public FightScenarioRandomizer.AppliedParameters randomization_parameters;
        public FightScenarioRandomizer.Settings randomization_settings;
        public int width = Width, height = Height, fps = Fps, frame_count = FrameCount;
        public float duration_seconds = FrameCount / (float)Fps;
        public string sampling = "frame_i_at_i_divided_by_fps";
        public string encoder = "UnityEditor.Media.MediaEncoder";
        public string skinning = "explicit_cpu_bake_per_frame";
        public string idle_asset;
        public TransitionInfo transitions;
        public ActorInfo[] actors;
        public CameraInfo camera;
        public AttackInfo attack;
        public ReactionInfo reaction;
        public float calibration_error_meters;
        public FrameInfo[] frames;
    }
    [Serializable] private sealed class ActorInfo
    {
        public string id, model_asset;
        public Vector3 position, rotation_euler, scale;
    }
    [Serializable] private sealed class CameraInfo
    {
        public string id = "cam01";
        public Vector3 position, rotation_euler;
        public float field_of_view;
    }
    [Serializable] private sealed class AttackInfo
    {
        public string actor = "actor_A", target = "actor_B", action = "punch", clip_asset, hand;
        public float speed, start_seconds, end_seconds, idle_return_seconds, blend_seconds, impact_normalized_time, planned_impact_seconds;
    }
    [Serializable] private sealed class ReactionInfo
    {
        public string actor = "actor_B", clip_asset;
        public float speed, start_seconds, end_seconds, idle_return_seconds;
    }
    [Serializable] private sealed class TransitionInfo
    {
        public string curve = "quintic_smootherstep";
        public string return_policy = "hold_last_action_pose_during_fade_to_running_idle";
        public float attack_in_seconds, reaction_in_seconds, return_to_idle_seconds;
    }
    [Serializable] private sealed class FrameInfo
    {
        public int frame;
        public float time_seconds, attacker_action_weight, receiver_action_weight;
        public string phase;
        public Vector3 attacking_hand_world, receiver_head_world;
    }

    private sealed class BakedSkin : IDisposable
    {
        private readonly SkinnedMeshRenderer source;
        private readonly Mesh mesh;

        public BakedSkin(SkinnedMeshRenderer source)
        {
            this.source = source;
            mesh = new Mesh { name = source.name + "_ExportPose" };
            mesh.MarkDynamic();
            var host = new GameObject(source.name + "_ExportMesh", typeof(MeshFilter), typeof(MeshRenderer));
            host.transform.SetParent(source.transform, false);
            host.layer = source.gameObject.layer;
            host.GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = host.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.shadowCastingMode = source.shadowCastingMode;
            renderer.receiveShadows = source.receiveShadows;
            var properties = new MaterialPropertyBlock();
            source.GetPropertyBlock(properties);
            renderer.SetPropertyBlock(properties);
            source.enabled = false;
        }

        public void Sample()
        {
            // Preserve renderer-local coordinates. With useScale=false Unity returns
            // the posed geometry with the actor scale already folded in, which
            // would apply that scale a second time through the child transform.
            source.BakeMesh(mesh, true);
            mesh.RecalculateBounds();
        }

        public void Dispose()
        {
            if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
