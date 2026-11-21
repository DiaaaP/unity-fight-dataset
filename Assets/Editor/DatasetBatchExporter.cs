using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FightDataset;
using UnityEditor;
using UnityEditor.Media;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static partial class DatasetBatchExporter
{
    public static bool IsRunning { get; private set; }
    public static bool IsPaused { get; private set; }
    public static string Status { get; private set; } = "Idle";
    public static string LastBatchDirectory { get; private set; }
    public static int CompletedScenarios { get; private set; }
    public static int FailedScenarios { get; private set; }
    public static int CurrentFrame { get; private set; }
    private static DatasetGeneratorConfig config;
    private static string[] categories;
    private static int scenarioIndex;
    private static Scene scene;
    private static DatasetSceneFactory.Stage stage;
    private static DatasetCameraView[] cameras;
    private static MediaEncoder[] encoders;
    private static StreamWriter[] poseWriters;
    private static BakedPart[] parts;
    private static DatasetPenetrationGuard penetrationGuard;
    private static DatasetYoloPose yoloPose;
    private static DatasetYoloWriter yoloWriter;
    private static IEnumerator<object> scenarioPreparation;
    public static bool IsPreparing => scenarioPreparation != null;
    public static double MaximumPreparationSliceMs { get; private set; }
    public static string SlowestPreparationPhase { get; private set; }
    private static RenderTexture target;
    private static Texture2D pixels;
    private static string[] temporaryVideos;
    private static Metadata metadata;
    private static Summary summary;
    private static readonly Dictionary<string, int> animationUsage = new Dictionary<string, int>();
    private static readonly Action signalEditorTick = BindEditorTick();

    private static Action BindEditorTick()
    {
        var method = typeof(EditorApplication).GetMethod("SignalTick", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return method == null ? null : (Action)Delegate.CreateDelegate(typeof(Action), method);
    }

    [Serializable] public sealed class ActionMetadata
    {
        public string actor, target, clip_key, clip_asset, actor_action, attacking_limb, target_landmark, planned_outcome, outcome;
        public float speed, start_seconds, end_seconds, impact_normalized_time, impact_seconds, contact_error_meters;
        public string reaction_asset, reaction_kind;
        public bool holds_fallen_pose;
        public bool mesh_contact_verified;
        public int impact_frame;
        public Vector3 attack_contact_point, target_contact_point;
        public Vector3 incoming_direction;
        public float reaction_speed, reaction_start_seconds, reaction_end_seconds, idle_return_seconds;
        public float approach_speed_mps;
        public float source_end_seconds, attack_yaw_offset_degrees, attack_blend_in;
        public float strike_source_start_seconds, strike_source_end_seconds, strike_source_peak_seconds;
        public float reaction_source_offset_seconds, reaction_blend_in, reaction_visible_seconds;
        public int reaction_visible_frame;
        public int ignored_preparation_contact_frames;
        public Vector3 attack_end_displacement, reaction_end_displacement;
    }
    [Serializable] public sealed class ActorMetadata
    {
        public string id, model_asset, neutral_asset;
        public Vector3 position, rotation_euler, scale;
        public float neutral_phase_seconds, neutral_speed;
    }
    [Serializable] public sealed class Metadata
    {
        public string schema = "fight_dataset_v1", scenario_id, group_id, split, category, event_class, unity_version;
        public string status = "encoded_pending_decode_and_visual_review";
        public int seed, width, height, fps, frame_count;
        public float duration_seconds;
        public bool requires_visual_review = true;
        public string skinning = "cpu_baked_once_per_frame_shared_across_views";
        public string view_sync = "all_views_rendered_from_same_pose_before_advancing_time";
        public string contact_validation = "main_source_stroke_first_knuckle_or_foot_contact_and_visible_reaction_v3";
        public string penetration_validation = DatasetPenetrationGuard.Contract;
        public string motion_sampling = "authored_pose_with_absolute_horizontal_endpoint_carry_v2";
        public string room = "floor_four_walls_and_ceiling";
        public string frame_readback;
        public string pose_labels, pose_visibility, pose_images, pose_annotations;
        public int pose_image_count;
        public DatasetYoloPose.FaceAnchor[] pose_face_anchors;
        public Vector2 room_size;
        public float room_height, attack_blend_in, reaction_blend_in, blend_out;
        public ActorMetadata[] actors;
        public ActionMetadata[] actions;
        public AnimationSelectionAttempt[] animation_selection;
        public DatasetCameraView[] cameras;
        public string[] videos, quality_flags;
        public float[] frame_times_seconds;
        public string[] pose_signatures;
    }
    [Serializable] public sealed class ManifestEntry
    {
        public string scenario_id, metadata, group_id, split, category;
        public int seed, scenario_index;
        public string[] videos;
        public int pose_image_count;
    }
    [Serializable] public sealed class Failure
    {
        public int scenario_index, seed;
        public string category, reason;
    }
    [Serializable] public sealed class Summary
    {
        public string schema = "fight_batch_v1", batch_id, status;
        public int seed, requested_scenarios, completed_scenarios, failed_scenarios, completed_videos;
        public int next_scenario_index;
        public string count_mode = "per_category";
        public int scenarios_per_category, total_scenario_limit;
        public string async_readback_fallback_reason;
        public string[] available_categories, skipped_categories, catalog_warnings;
        public List<ManifestEntry> scenarios = new List<ManifestEntry>();
        public List<Failure> failures = new List<Failure>();
        public List<ClipDistribution> animation_usage = new List<ClipDistribution>();
    }
    [Serializable] public sealed class ClipDistribution
    {
        public string clip_key, clip_asset, kind;
        public int completed_actions, attempted_variants, rejected_variants;
    }

    [MenuItem("Tools/Fight Dataset/Generator/3 Generate Configured Batch")]
    private static void GenerateMenu() => StartBatch();
    [MenuItem("Tools/Fight Dataset/Generator/Run Small Smoke Test")]
    private static void SmokeMenu() => StartSmokeTest();
    [MenuItem("Tools/Fight Dataset/Generator/Cancel Batch")]
    public static void Cancel() { if (IsRunning) Finish("cancelled"); }
    [MenuItem("Tools/Fight Dataset/Generator/Select Generator Config")]
    private static void SelectConfig() { DatasetCatalogTools.Refresh(); Selection.activeObject = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath); }

    public static string StartSmokeTest() => StartBatch(true);

    public static string StartBatch(bool smokeTest = false, int scenarioLimit = 0, DatasetGeneratorConfig settings = null)
    {
        RequireIdleEditor();
        try { return StartBatchCore(smokeTest, scenarioLimit, settings); }
        catch
        {
            StopAfterStorageError("Не удалось запустить генерацию.");
            throw;
        }
    }

    private static string StartBatchCore(bool smokeTest, int scenarioLimit, DatasetGeneratorConfig settings)
    {
        RequireIdleEditor();
        MaximumPreparationSliceMs = 0;
        SlowestPreparationPhase = "";
        if (!settings) DatasetCatalogTools.Refresh();
        config = UnityEngine.Object.Instantiate(settings ? settings : AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        config.hideFlags = HideFlags.HideAndDontSave;
        categories = DatasetScenarioPlanner.AvailableCategories(config);
        if (categories.Length == 0 || config.catalog.characters.Count == 0 || !config.catalog.clips.Any(c => DatasetCatalogTools.Usable(c) && c.kind == ClipKind.Idle))
        {
            UnityEngine.Object.DestroyImmediate(config);
            config = null;
            throw new InvalidOperationException("Supply a valid character, Idle and at least one available category before exporting.");
        }
        if (smokeTest) { config.scenarioCount = 1; config.cameraViews = 2; config.fightAttackCount = 2; }
        config.Validate();
        checkpoint = DatasetBatchCheckpoint.Capture(config);
        ownedCatalog = checkpoint.RestoreCatalog();
        config.catalog = ownedCatalog;
        if (categories.Length == 0) throw new InvalidOperationException("No available categories.");
        string root = Path.GetFullPath(Path.IsPathRooted(config.outputDirectory) ? config.outputDirectory : Path.Combine(Path.GetDirectoryName(Application.dataPath), config.outputDirectory));
        string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Equals(assets, StringComparison.OrdinalIgnoreCase) || root.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dataset output must be outside Assets.");
        string batchId = "batch_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        LastBatchDirectory = Path.Combine(root, batchId);
        foreach (string folder in new[] { "hit", "kick", "fight", "normal", "metadata", "inspection", "joint_projections" }) Directory.CreateDirectory(Path.Combine(LastBatchDirectory, folder));
        if (config.exportYoloPose) DatasetYoloWriter.Initialize(LastBatchDirectory);
        string[] requested = new[] { config.generateHit ? "hit" : null, config.generateKick ? "kick" : null, config.generateFight ? "fight" : null, config.generateNormal ? "normal" : null }.Where(c => c != null).ToArray();
        summary = new Summary { batch_id = batchId, seed = config.seed,
            requested_scenarios = PlannedScenarioCount(config, categories.Length, scenarioLimit),
            scenarios_per_category = config.scenarioCount, total_scenario_limit = Mathf.Max(0, scenarioLimit),
            available_categories = categories, skipped_categories = requested.Except(categories).ToArray(), catalog_warnings = config.catalog.warnings, status = "running" };
        InitializeAnimationUsage();
        File.WriteAllText(Path.Combine(LastBatchDirectory, "config.json"), JsonUtility.ToJson(config, true));
        CompletedScenarios = FailedScenarios = scenarioIndex = CurrentFrame = 0;
        elapsedRecorded = 0;
        timingStarted = EditorApplication.timeSinceStartup;
        IsRunning = true;
        IsPaused = false;
        Status = "Generating";
        RememberBatch(LastBatchDirectory);
        AttachLifecycle();
        SaveCheckpoint();
        WriteSummary();
        DatasetGeneratorWindow.ShowWindow();
        return LastBatchDirectory;
    }

    private static void Tick()
    {
        try { TickFrame(); }
        catch (Exception exception)
        {
            StopAfterStorageError("Генерация остановлена: " + exception.Message);
            Debug.LogError(Status);
        }
        finally
        {
            if (IsRunning && !IsPaused) signalEditorTick?.Invoke();
        }
    }

    private static void TickFrame()
    {
        if (!IsRunning || IsPaused) return;
        if (scenarioPreparation != null) { AdvancePreparation(); return; }
        if (stage == null)
        {
            if (scenarioIndex >= TotalScenarios) { Finish(FailedScenarios == 0 ? "completed" : "completed_with_failures"); return; }
            scenarioPreparation = BeginScenarioSteps();
            AdvancePreparation();
            return;
        }
        try
        {
            if (readbackQueue != null)
            {
                DrainReadbacks();
                var budget = System.Diagnostics.Stopwatch.StartNew();
                while (submittedFrame < metadata.frame_count && readbackQueue.CanSubmit && budget.Elapsed.TotalMilliseconds < 12)
                    SubmitFrame();
                if (CurrentFrame >= metadata.frame_count) CompleteScenario();
                else Status = $"Scenario {scenarioIndex + 1}/{TotalScenarios}, encoded {CurrentFrame}/{metadata.frame_count}, queued {submittedFrame - CurrentFrame}";
            }
            else SubmitFrame();
        }
        catch (DatasetBatchCheckpoint.CommitException) { throw; }
        catch (Exception exception) { RecordFailure(exception); }
    }

    private static void SubmitFrame()
    {
            if (readbackQueue == null && yoloWriter != null && !yoloWriter.CanQueue(cameras.Length)) return;
            int frame = submittedFrame;
            float time = frame / (float)config.fps;
            stage.player.SampleAtTime(time);
            foreach (var action in stage.player.plan.actions.Where(a => a.impactFrame == frame))
            {
                DatasetContactSolver.Verify(config, stage.player, action);
                DatasetReactionTiming.Verify(config, stage.player, action);
            }
            penetrationGuard.Verify(frame);
            foreach (var part in parts) part.Sample();
            yoloPose?.Sample();
            Bounds[] bounds = ActorBounds();
            Vector3 torsoDelta = Torso(stage.player.actors[0]) - Torso(stage.player.actors[1]);
            torsoDelta.y = 0;
            if (torsoDelta.magnitude < 0.25f) throw new InvalidOperationException("Severe torso overlap at frame " + frame);
            for (int actor = 0; actor < 2; actor++)
            {
                Vector3 low = stage.root.transform.InverseTransformPoint(bounds[actor].min);
                Vector3 high = stage.root.transform.InverseTransformPoint(bounds[actor].max);
                if (low.y < -0.015f || high.y > config.roomHeight - 0.1f || low.x < -config.roomSize.x / 2 + 0.15f ||
                    high.x > config.roomSize.x / 2 - 0.15f || low.z < -config.roomSize.y / 2 + 0.15f || high.z > config.roomSize.y / 2 - 0.15f)
                    throw new InvalidOperationException("Actor geometry crosses floor/wall bounds at frame " + frame);
            }
            metadata.frame_times_seconds[frame] = time;
            metadata.pose_signatures[frame] = PoseSignature();
            var pending = readbackQueue?.BeginFrame(frame, config.saveInspectionFrames && InspectionFrame(frame));
            for (int view = 0; view < cameras.Length; view++)
            {
                // Projection must use the actual output target, including its
                // pixel dimensions. The Editor's camera viewport can otherwise
                // introduce a subpixel Y shift in asynchronous annotations.
                var projectionTarget = stage.camera.targetTexture;
                if (pending != null) stage.camera.targetTexture = pending.targets[view];
                try
                {
                cameras[view].Apply(stage.camera);
                foreach (var actor in stage.player.actors)
                    foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
                        if (!FightDataset.Prototype.FightScenarioRandomizer.Inside(stage.camera, actor.GetBoneTransform(bone).position, 0.02f))
                            throw new InvalidOperationException("Actor cropped in " + cameras[view].id + " frame " + frame);
                string projection = poseWriters != null ? DatasetProjectedJoints.Frame(stage.player, stage.camera, bounds, frame, config.fps) : null;
                string labels = yoloPose?.Labels(stage.camera);
                if (readbackQueue != null)
                {
                    pending.projections[view] = projection;
                    pending.yoloLabels[view] = labels;
                    readbackQueue.CaptureView(pending, view, stage.camera);
                    continue;
                }
                stage.camera.Render();
                var previous = RenderTexture.active;
                try { RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, config.width, config.height), 0, 0, false); pixels.Apply(false, false); }
                finally { RenderTexture.active = previous; }
                if (!encoders[view].AddFrame(pixels)) throw new IOException("Encoder rejected frame " + frame);
                if (poseWriters != null) poseWriters[view].WriteLine(projection);
                yoloWriter?.Enqueue(cameras[view].id, frame, pixels.GetRawTextureData<byte>().ToArray(), labels);
                if (config.saveInspectionFrames && InspectionFrame(frame)) SaveInspection(view, frame);
                }
                finally { stage.camera.targetTexture = projectionTarget; }
            }
            submittedFrame++;
            if (readbackQueue != null) return;
            CurrentFrame++;
            Status = $"Scenario {scenarioIndex + 1}/{TotalScenarios}, frame {CurrentFrame}/{metadata.frame_count}";
            if (CurrentFrame >= metadata.frame_count) CompleteScenario();
    }

    public static int PlannedScenarioCount(DatasetGeneratorConfig settings, int categoryCount, int totalLimit = 0)
    {
        int count = checked(settings.scenarioCount * categoryCount);
        return totalLimit > 0 ? Mathf.Min(count, totalLimit) : count;
    }

    private static void AdvancePreparation()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Consume several cheap steps without repainting the whole Editor
            // after each one; heavy work still returns control at the next yield.
            do
            {
                if (!scenarioPreparation.MoveNext()) { CancelPreparation(); break; }
                Status = "Preparing " + (scenarioIndex + 1) + "/" + TotalScenarios + ": " + scenarioPreparation.Current;
            }
            while (elapsed.Elapsed.TotalMilliseconds < 8);
        }
        catch (Exception exception) { CancelPreparation(); RecordFailure(exception); }
        finally
        {
            if (elapsed.Elapsed.TotalMilliseconds > MaximumPreparationSliceMs)
            {
                MaximumPreparationSliceMs = elapsed.Elapsed.TotalMilliseconds;
                SlowestPreparationPhase = Status;
            }
        }
    }

    private static void CancelPreparation()
    {
        var active = scenarioPreparation;
        scenarioPreparation = null;
        active?.Dispose();
    }

    private static IEnumerator<object> PrepareAttemptSteps(string category, int seed)
    {
        scene = EditorSceneManager.NewPreviewScene();
        stage = DatasetSceneFactory.Create(config, scene, seed);
        if (config.exportYoloPose) yoloPose = new DatasetYoloPose(stage);
        stage.camera.enabled = false;
        stage.camera.scene = scene;
        yield return "Building scenario";
        using (var plan = DatasetScenarioPlanner.CreateSteps(config, stage, category, seed, value => stage.player.plan = value, animationUsage))
            while (plan.MoveNext()) yield return plan.Current;
        using (var views = DatasetCameraPlanner.CreateSteps(config, stage, seed, value => cameras = value))
            while (views.MoveNext()) yield return views.Current;
    }

    private static IEnumerator<object> BeginScenarioSteps()
    {
        ArchiveUncommittedScenario();
        string category = categories[scenarioIndex % categories.Length];
        Exception last = null;
        int chosenSeed = 0;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            chosenSeed = unchecked(config.seed + scenarioIndex * 104729 + attempt * 7919);
            bool succeeded = false;
            using (var trial = PrepareAttemptSteps(category, chosenSeed))
            {
                while (true)
                {
                    bool more;
                    try { more = trial.MoveNext(); }
                    catch (Exception exception) { last = exception; break; }
                    if (!more) { succeeded = true; break; }
                    yield return trial.Current;
                }
            }
            if (succeeded) break;
            CleanupScenario();
            yield return "Trying another placement";
        }
        if (stage == null) throw new InvalidOperationException("No acceptable " + category + " scenario after 8 attempts: " + last?.Message);
        string id = category + "_" + (scenarioIndex + 1).ToString("D5") + "_s" + chosenSeed;
        var plan = stage.player.plan;
        int frameCount = Mathf.CeilToInt(plan.duration * config.fps);
        string[] videos = cameras.Select(c => category + "/" + id + "_" + c.id + ".mp4").ToArray();
        string family = string.Join("|", stage.modelPaths.OrderBy(p => p, StringComparer.Ordinal)) + "|" +
            string.Join("|", plan.actions.Select(a => a.clipKey).Distinct().OrderBy(k => k, StringComparer.Ordinal)) + "|" +
            string.Join("|", stage.player.neutralClips.Select(AssetDatabase.GetAssetPath).Distinct().OrderBy(p => p, StringComparer.Ordinal));
        string group = Hash(family);
        uint splitValue = Convert.ToUInt32(group.Substring(0, 8), 16);
        double splitPosition = splitValue / ((double)uint.MaxValue + 1);
        string split = splitPosition < config.trainFraction ? "train" : splitPosition < config.trainFraction + config.validationFraction ? "val" : "test";
        metadata = new Metadata { scenario_id = id, seed = chosenSeed, group_id = group, split = split, category = category,
            event_class = plan.eventClass, unity_version = Application.unityVersion, width = config.width, height = config.height, fps = config.fps,
            frame_count = frameCount, duration_seconds = frameCount / (float)config.fps, cameras = cameras, videos = videos,
            room_size = config.roomSize, room_height = config.roomHeight, attack_blend_in = plan.attackBlendIn,
            reaction_blend_in = plan.reactionBlendIn, blend_out = plan.blendOut, quality_flags = plan.qualityFlags.ToArray(),
            animation_selection = plan.animationSelection.ToArray(),
            actors = stage.player.actors.Select((a, i) => new ActorMetadata { id = "actor_" + (i == 0 ? "A" : "B"), model_asset = stage.modelPaths[i],
                neutral_asset = AssetDatabase.GetAssetPath(stage.player.neutralClips[i]), position = a.transform.position, rotation_euler = a.transform.eulerAngles, scale = a.transform.localScale,
                neutral_phase_seconds = plan.neutralTimeOffsets[i], neutral_speed = plan.neutralSpeeds[i] }).ToArray(),
            actions = plan.actions.Select(a => new ActionMetadata { actor = "actor_" + (a.actor == 0 ? "A" : "B"), target = "actor_" + (a.target == 0 ? "A" : "B"),
                clip_key = a.clipKey, clip_asset = a.assetPath, actor_action = a.actorAction, attacking_limb = a.limb.ToString(),
                target_landmark = a.targetLandmark.ToString(),
                speed = a.speed, start_seconds = a.start, end_seconds = a.End, impact_normalized_time = a.impactNormalizedTime, impact_seconds = a.impactTime,
                planned_outcome = a.plannedOutcome, outcome = a.outcome, contact_error_meters = a.contactError,
                reaction_asset = a.reactionAsset, reaction_kind = a.reactionKind, reaction_speed = a.reactionSpeed,
                reaction_start_seconds = a.reactionStart, reaction_end_seconds = a.ReactionEnd,
                mesh_contact_verified = a.meshContactVerified, impact_frame = a.impactFrame,
                attack_contact_point = a.contactPoint, target_contact_point = a.targetContactPoint,
                approach_speed_mps = a.approachSpeed,
                incoming_direction = a.incomingDirection,
                source_end_seconds = a.SourceEnd, attack_yaw_offset_degrees = a.actorYawOffset, attack_blend_in = a.attackFadeIn,
                strike_source_start_seconds = a.strikeSourceStart, strike_source_end_seconds = a.strikeSourceEnd, strike_source_peak_seconds = a.strikeSourcePeak,
                reaction_source_offset_seconds = a.reactionSourceOffset, reaction_blend_in = a.reactionFadeIn,
                reaction_visible_seconds = a.reactionResponseSeconds, reaction_visible_frame = a.reactionResponseFrame,
                ignored_preparation_contact_frames = a.preparationContactFrames,
                attack_end_displacement = a.attackEndDisplacement, reaction_end_displacement = a.reactionEndDisplacement,
                holds_fallen_pose = a.HoldsFallenPose,
                idle_return_seconds = a.HoldsFallenPose ? -1f : Mathf.Max(a.End, a.ReactionEnd) + plan.blendOut }).ToArray(),
            frame_times_seconds = new float[frameCount], pose_signatures = new string[frameCount] };
        parts = stage.player.actors.SelectMany((actor, i) => actor.GetComponentsInChildren<SkinnedMeshRenderer>().Select(s => new BakedPart(s, i))).ToArray();
        if (yoloPose != null)
        {
            foreach (var part in parts) yoloPose.Bind(part.source, part.mesh);
            yoloWriter = new DatasetYoloWriter(LastBatchDirectory, id, split, config.width, config.height, config.poseJpegQuality);
            metadata.pose_labels = "yolo_pose_coco17_normalized_top_left_person_class_0";
            metadata.pose_visibility = DatasetYoloPose.Contract;
            metadata.pose_images = "yolo/images/" + split;
            metadata.pose_annotations = "yolo/labels/" + split;
            metadata.pose_face_anchors = yoloPose.faceAnchors;
        }
        penetrationGuard = new DatasetPenetrationGuard(stage.player);
        if (config.useAsyncGPUReadback && SystemInfo.supportsAsyncGPUReadback && string.IsNullOrEmpty(summary.async_readback_fallback_reason))
            readbackQueue = new DatasetFrameReadbackQueue(config.width, config.height, cameras.Length);
        else
        {
            target = new RenderTexture(config.width, config.height, 24, RenderTextureFormat.ARGB32);
            target.Create();
            stage.camera.targetTexture = target;
        }
        metadata.frame_readback = readbackQueue != null ? "async_gpu_fifo_native_rgba32" : "synchronous_readpixels";
        if (readbackQueue == null || config.saveInspectionFrames)
            pixels = new Texture2D(config.width, config.height, TextureFormat.RGBA32, false, false);
        temporaryVideos = videos.Select(v => Path.Combine(LastBatchDirectory, v.Replace(".mp4", ".partial.mp4"))).ToArray();
        encoders = new MediaEncoder[cameras.Length];
        if (config.exportProjectedBodyJoints) poseWriters = new StreamWriter[cameras.Length];
        for (int i = 0; i < cameras.Length; i++)
        {
            encoders[i] = new MediaEncoder(temporaryVideos[i], new VideoTrackAttributes { width = (uint)config.width, height = (uint)config.height,
                frameRate = new MediaRational(config.fps), includeAlpha = false, bitRateMode = VideoBitrateMode.High });
            if (poseWriters != null) poseWriters[i] = new StreamWriter(Path.Combine(LastBatchDirectory, "joint_projections", id + "_" + cameras[i].id + ".jsonl"), false, Encoding.UTF8);
        }
        CurrentFrame = submittedFrame = 0;
    }

    private static bool InspectionFrame(int frame) => frame == 0 || frame == metadata.frame_count - 1 || metadata.actions.Any(a => frame == Mathf.RoundToInt(a.impact_seconds * config.fps) || frame == Mathf.RoundToInt((a.impact_seconds + 0.25f) * config.fps));

    private static void CompleteScenario()
    {
        if (yoloWriter != null) metadata.pose_image_count = yoloWriter.Commit(metadata.frame_count * cameras.Length);
        for (int i = 0; i < encoders.Length; i++) { encoders[i]?.Dispose(); encoders[i] = null; }
        for (int i = 0; i < temporaryVideos.Length; i++)
        {
            if (new FileInfo(temporaryVideos[i]).Length < 1024) throw new IOException("Empty encoded video.");
            File.Move(temporaryVideos[i], Path.Combine(LastBatchDirectory, metadata.videos[i]));
        }
        string metadataPath = "metadata/" + metadata.scenario_id + ".json";
        File.WriteAllText(Path.Combine(LastBatchDirectory, metadataPath), JsonUtility.ToJson(metadata, true));
        var entry = new ManifestEntry { scenario_id = metadata.scenario_id, metadata = metadataPath, group_id = metadata.group_id,
            scenario_index = scenarioIndex,
            split = metadata.split, category = metadata.category, seed = metadata.seed, videos = metadata.videos,
            pose_image_count = metadata.pose_image_count };
        summary.scenarios.Add(entry);
        CompletedScenarios++;
        summary.completed_videos += metadata.videos.Length;
        scenarioIndex++;
        RecordAnimationUsage(metadata, 1);
        try { WriteSummary(); }
        catch
        {
            scenarioIndex--;
            CompletedScenarios--;
            summary.completed_videos -= entry.videos.Length;
            summary.scenarios.Remove(entry);
            RecordAnimationUsage(metadata, -1);
            throw;
        }
        CleanupScenario();
        CurrentFrame = 0;
    }

    private static void InitializeAnimationUsage()
    {
        animationUsage.Clear();
        summary.animation_usage = config.catalog.clips.Where(DatasetCatalogTools.Usable)
            .Where(c => c.kind == ClipKind.Punch || c.kind == ClipKind.Kick)
            .Select(c => new ClipDistribution { clip_key = c.key, clip_asset = c.assetPath, kind = c.kind.ToString() }).ToList();
        foreach (var row in summary.animation_usage) animationUsage[row.clip_key] = 0;
        foreach (var entry in summary.scenarios)
            RecordAnimationUsage(JsonUtility.FromJson<Metadata>(File.ReadAllText(ResolveBatchPath(LastBatchDirectory, entry.metadata))), 1);
    }

    private static void RecordAnimationUsage(Metadata data, int sign)
    {
        foreach (var row in summary.animation_usage)
        {
            row.completed_actions += sign * data.actions.Count(a => a.clip_key == row.clip_key);
            row.attempted_variants += sign * (data.animation_selection?.Count(a => a.clipKey == row.clip_key) ?? 0);
            row.rejected_variants += sign * (data.animation_selection?.Count(a => a.clipKey == row.clip_key && !a.accepted) ?? 0);
            animationUsage[row.clip_key] = row.completed_actions;
        }
    }

    private static void RecordFailure(Exception exception)
    {
        if (exception is DatasetFrameReadbackQueue.ReadbackException)
            summary.async_readback_fallback_reason = exception.Message;
        int failedSeed = metadata?.seed ?? unchecked(config.seed + scenarioIndex * 104729);
        CleanupScenario();
        ArchiveUncommittedScenario();
        summary.failures.Add(new Failure { scenario_index = scenarioIndex, seed = failedSeed,
            category = categories[scenarioIndex % categories.Length], reason = exception.Message });
        FailedScenarios++;
        scenarioIndex++;
        CurrentFrame = 0;
        WriteSummary();
    }

    private static void PlayModeChanged(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingEditMode) SaveAndStop(); }
    private static void Finish(string state)
    {
        FreezeClock();
        DetachLifecycle();
        CancelPreparation();
        CleanupScenario();
        summary.status = state;
        Status = state;
        IsRunning = false;
        IsPaused = false;
        CurrentFrame = 0;
        CanResumeLast = scenarioIndex < summary.requested_scenarios;
        try { WriteSummary(); SaveCheckpoint(); WriteAnimationReport(); }
        finally { ReleaseConfig(); }
        Debug.Log($"Dataset batch {state}: {CompletedScenarios} scenarios, {FailedScenarios} failures. {LastBatchDirectory}");
    }

    private static void WriteAnimationReport()
    {
        var lines = new StringBuilder("animation,kind,completed_actions,attempted_variants,rejected_variants\n");
        foreach (var row in summary.animation_usage.OrderBy(r => r.kind).ThenBy(r => r.clip_asset, StringComparer.Ordinal))
            lines.Append('"').Append(row.clip_asset.Replace("\"", "\"\"")).Append("\",").Append(row.kind).Append(',')
                .Append(row.completed_actions).Append(',').Append(row.attempted_variants).Append(',').Append(row.rejected_variants).Append('\n');
        DatasetBatchCheckpoint.WriteAtomic(Path.Combine(LastBatchDirectory, "animation_usage.csv"), lines.ToString());
    }

    private static void WriteSummary()
    {
        summary.completed_scenarios = CompletedScenarios;
        summary.failed_scenarios = FailedScenarios;
        summary.next_scenario_index = scenarioIndex;
        string contents = JsonUtility.ToJson(summary, true);
        string path = Path.Combine(LastBatchDirectory, "manifest.json");
        DatasetBatchCheckpoint.WriteAtomic(path, contents);
    }

    private static void CleanupScenario()
    {
        // Finish native GPU writes before releasing their buffers or render targets.
        readbackQueue?.Dispose();
        readbackQueue = null;
        yoloWriter?.Dispose();
        yoloWriter = null;
        yoloPose = null;
        submittedFrame = 0;
        if (encoders != null) foreach (var encoder in encoders) encoder?.Dispose();
        encoders = null;
        if (poseWriters != null) foreach (var writer in poseWriters) writer?.Dispose();
        poseWriters = null;
        penetrationGuard?.Dispose();
        penetrationGuard = null;
        if (stage?.camera) stage.camera.targetTexture = null;
        if (scene.IsValid()) DatasetSceneFactory.ClosePreviewScene(scene);
        if (parts != null) foreach (var part in parts) part.Dispose();
        parts = null;
        if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
        if (pixels) UnityEngine.Object.DestroyImmediate(pixels);
        target = null;
        pixels = null;
        stage = null;
        metadata = null;
        cameras = null;
    }

    private static Bounds[] ActorBounds()
    {
        var bounds = new Bounds[2];
        for (int actor = 0; actor < 2; actor++)
        {
            bool first = true;
            foreach (var part in parts.Where(p => p.actor == actor))
                if (first) { bounds[actor] = part.renderer.bounds; first = false; } else bounds[actor].Encapsulate(part.renderer.bounds);
        }
        return bounds;
    }

    private static Vector3 Torso(Animator actor) => (actor.GetBoneTransform(HumanBodyBones.Chest) ?? actor.GetBoneTransform(HumanBodyBones.Hips)).position;

    private static string PoseSignature()
    {
        var text = new StringBuilder();
        foreach (var actor in stage.player.actors)
            foreach (var bone in new[] { HumanBodyBones.Head, HumanBodyBones.Hips, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot })
            { Vector3 p = actor.GetBoneTransform(bone).position; text.Append(Mathf.RoundToInt(p.x * 10000)).Append(',').Append(Mathf.RoundToInt(p.y * 10000)).Append(',').Append(Mathf.RoundToInt(p.z * 10000)).Append(';'); }
        return Hash(text.ToString());
    }

    private static string Hash(string value)
    {
        using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(b => b.ToString("x2")));
    }

    private sealed class BakedPart : IDisposable
    {
        public readonly int actor;
        public readonly MeshRenderer renderer;
        public readonly SkinnedMeshRenderer source;
        public readonly Mesh mesh;
        public BakedPart(SkinnedMeshRenderer source, int actor)
        {
            this.source = source;
            this.actor = actor;
            mesh = new Mesh { name = source.name + "_Pose" };
            mesh.MarkDynamic();
            var go = new GameObject(source.name + "_Export", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(source.transform, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.shadowCastingMode = source.shadowCastingMode;
            renderer.receiveShadows = source.receiveShadows;
            source.enabled = false;
        }
        public void Sample() { source.BakeMesh(mesh, true); mesh.RecalculateBounds(); }
        public void Dispose() { if (mesh) UnityEngine.Object.DestroyImmediate(mesh); }
    }
}
