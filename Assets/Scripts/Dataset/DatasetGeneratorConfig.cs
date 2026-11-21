using UnityEngine;
using UnityEngine.Serialization;

namespace FightDataset
{
    [CreateAssetMenu(menuName = "Fight Dataset/Generator Config")]
    public sealed class DatasetGeneratorConfig : ScriptableObject
    {
        public AnimationCatalog catalog;
        public string outputDirectory = "DatasetOutput/Generated";
        [Min(1), FormerlySerializedAs("scenarioCount")]
        [Tooltip("Количество сценариев на каждую включённую и доступную категорию. Например, 100 с Hit и Kick = 100 ударов руками + 100 пинков. Число MP4 также умножается на Camera Views.")]
        public int scenariosPerCategory = 10;
        // Preserve existing CLI helpers; the serialized/Inspector name is explicit.
        public int scenarioCount { get => scenariosPerCategory; set => scenariosPerCategory = value; }
        public int seed = 12345;
        [Range(1, 3)] public int cameraViews = 3;
        public int width = 1280, height = 720, fps = 20;
        [Range(3f, 30f)] public float minimumDurationSeconds = 5f;
        public bool generateHit = true, generateKick = true, generateFight = true, generateNormal = true;
        [Range(2, 4)] public int fightAttackCount = 2;
        [HideInInspector] public bool allowDefensiveReactions;
        [HideInInspector] public bool rejectLargeContactError = true;
        [Range(0.002f, 0.01f)] public float maximumContactError = 0.008f;
        public Vector2 speedRange = new Vector2(0.8f, 1.2f);
        public float attackBlendIn = 0.28f, reactionBlendIn = 0.22f, blendOut = 0.4f;
        public Vector2 roomSize = new Vector2(8f, 8f);
        public float roomHeight = 3.6f;
        public Vector2 cameraHeight = new Vector2(0.25f, 3.35f);
        public Vector2 cameraFov = new Vector2(35f, 90f);
        [Header("Camera probabilities (relative weights)")]
        [Min(0)] public float highCameraWeight = 45f;
        [Min(0)] public float overheadCameraWeight = 15f;
        [Min(0)] public float wideCameraWeight = 15f;
        [Min(0)] public float eyeLevelCameraWeight = 15f;
        [Min(0)] public float shoulderCameraWeight = 5f;
        [Min(0)] public float lowCameraWeight = 5f;
        public float trainFraction = 0.8f, validationFraction = 0.1f;
        public bool saveInspectionFrames = true;
        [Tooltip("Read rendered frames asynchronously when the GPU supports it. Disable for a synchronous comparison or troubleshooting.")]
        public bool useAsyncGPUReadback = true;
        public bool exportProjectedBodyJoints;
        [Header("YOLO Pose (COCO 17)")]
        [Tooltip("Сохранять каждый кадр JPG и TXT с двумя людьми, рамками и 17 точками. Увеличивает объём данных.")]
        public bool exportYoloPose = true;
        [Range(70, 100)] public int poseJpegQuality = 90;

        public void Validate()
        {
            var weights = new[] { highCameraWeight, overheadCameraWeight, wideCameraWeight, eyeLevelCameraWeight, shoulderCameraWeight, lowCameraWeight };
            float totalWeight = 0;
            foreach (float weight in weights)
            {
                if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0) throw new System.InvalidOperationException("Invalid camera probability.");
                totalWeight += weight;
            }
            if (totalWeight <= 0 || float.IsInfinity(totalWeight)) throw new System.InvalidOperationException("At least one camera weight must be positive.");
            if (!catalog || string.IsNullOrWhiteSpace(outputDirectory) || scenarioCount < 1 || scenarioCount > 10000 || cameraViews < 1 || cameraViews > 3 || fightAttackCount < 2 || fightAttackCount > 4 ||
                width < 64 || height < 64 || width > 3840 || height > 2160 || width % 2 != 0 || height % 2 != 0 ||
                fps < 1 || fps > 60 || poseJpegQuality < 70 || poseJpegQuality > 100 || minimumDurationSeconds < 3f || minimumDurationSeconds > 30f ||
                speedRange.x < 0.5f || speedRange.y > 1.5f || speedRange.x > speedRange.y ||
                roomSize.x < 6f || roomSize.y < 6f || roomHeight < 3f ||
                cameraHeight.x < 0.15f || cameraHeight.y > roomHeight - 0.1f || cameraHeight.x > cameraHeight.y ||
                cameraFov.x < 30f || cameraFov.y > 100f || cameraFov.x > cameraFov.y ||
                attackBlendIn < 0.02f || reactionBlendIn < 0.02f || blendOut < 0.05f ||
                trainFraction < 0f || validationFraction < 0f || trainFraction + validationFraction > 1f)
                throw new System.InvalidOperationException("Invalid dataset generator configuration.");
        }
    }
}
