using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using FightDataset;
using UnityEditor;
using UnityEngine;

[Serializable]
public sealed class DatasetBatchCheckpoint
{
    public sealed class CommitException : IOException { public CommitException(string path, Exception inner) : base("Cannot commit checkpoint: " + path, inner) { } }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existing, string replacement, uint flags);
    public string schema = "fight_resume_v1";
    public string config_json, fingerprint, unity_version;
    public CatalogClip[] clips;
    public string[] characters, warnings;
    public double elapsed_seconds;
    public int camera_views;

    public static DatasetBatchCheckpoint Capture(DatasetGeneratorConfig config)
    {
        var result = new DatasetBatchCheckpoint
        {
            unity_version = Application.unityVersion,
            camera_views = config.cameraViews,
            characters = config.catalog.characters.Select(AssetDatabase.GetAssetPath).ToArray(),
            warnings = config.catalog.warnings,
            clips = config.catalog.clips.Select(entry =>
            {
                var copy = JsonUtility.FromJson<CatalogClip>(JsonUtility.ToJson(entry));
                copy.clip = null;
                return copy;
            }).ToArray()
        };
        var copyConfig = UnityEngine.Object.Instantiate(config);
        try { copyConfig.catalog = null; result.config_json = JsonUtility.ToJson(copyConfig); }
        finally { UnityEngine.Object.DestroyImmediate(copyConfig); }
        result.fingerprint = result.SourceFingerprint();
        return result;
    }

    public AnimationCatalog RestoreCatalog()
    {
        var catalog = ScriptableObject.CreateInstance<AnimationCatalog>();
        catalog.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            catalog.warnings = warnings;
            catalog.characters = characters.Select(path => AssetDatabase.LoadAssetAtPath<GameObject>(path)
                ?? throw new InvalidOperationException("Character missing: " + path)).ToList();
            catalog.clips = clips.Select(saved =>
            {
                var entry = JsonUtility.FromJson<CatalogClip>(JsonUtility.ToJson(saved));
                entry.clip = AssetDatabase.LoadAllAssetsAtPath(saved.assetPath).OfType<AnimationClip>().FirstOrDefault(clip =>
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long localId);
                    return guid + ":" + localId == saved.key;
                });
                if (!entry.clip) throw new InvalidOperationException("Animation missing or replaced: " + saved.assetPath);
                return entry;
            }).ToList();
            return catalog;
        }
        catch { UnityEngine.Object.DestroyImmediate(catalog); throw; }
    }

    public void ValidateSources()
    {
        if (schema != "fight_resume_v1" || clips == null || characters == null || string.IsNullOrEmpty(config_json))
            throw new InvalidOperationException("Unsupported or incomplete resume file.");
        if (unity_version != Application.unityVersion || SourceFingerprint() != fingerprint)
            throw new InvalidOperationException("Исходные модели, анимации, материалы или код генератора изменились. Верни исходники этого батча или создай новый пакет.");
    }

    private string SourceFingerprint()
    {
        var paths = clips.Select(c => c.assetPath).Concat(characters).Concat(new[]
        {
            "Assets/Materials/DatasetGenerator/Floor.mat", "Assets/Materials/DatasetGenerator/Walls.mat",
            "Assets/Scripts/Dataset/AnimationCatalog.cs", "Assets/Scripts/Dataset/DatasetGeneratorConfig.cs",
            "Assets/Scripts/Dataset/DatasetScenarioPlayer.cs", "Assets/Editor/DatasetSceneFactory.cs",
            "Assets/Editor/DatasetScenarioPlanner.cs", "Assets/Editor/DatasetBatchExporter.cs",
            "Assets/Editor/DatasetBatchExporter.Resume.cs", "Assets/Editor/DatasetBatchCheckpoint.cs",
            "Assets/Editor/DatasetBatchExporter.Readback.cs", "Assets/Editor/DatasetFrameReadbackQueue.cs",
            "Assets/Editor/DatasetProjectedJoints.cs"
            , "Assets/Editor/DatasetYoloPose.cs", "Assets/Editor/DatasetYoloWriter.cs"
            , "Assets/Editor/DatasetContactGeometry.cs", "Assets/Editor/DatasetContactSolver.cs", "Assets/Editor/DatasetCameraPlanner.cs"
            , "Assets/Editor/DatasetStrikeProfile.cs", "Assets/Editor/DatasetReactionTiming.cs"
            , "Assets/Editor/DatasetPenetrationGuard.cs"
            , "Assets/Editor/DatasetClipDisplacement.cs"
        }).Distinct().OrderBy(p => p, StringComparer.Ordinal);
        var value = new StringBuilder(unity_version).Append(config_json).Append(JsonUtility.ToJson(this.WithoutFingerprint()));
        foreach (string path in paths)
        {
            value.Append('|').Append(path).Append(':');
            if (!File.Exists(path)) value.Append("missing");
            else if (path.EndsWith(".cs", StringComparison.Ordinal)) value.Append(Convert.ToBase64String(File.ReadAllBytes(path)));
            else value.Append(AssetDatabase.GetAssetDependencyHash(path));
        }
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value.ToString()))).Replace("-", "");
    }

    private DatasetBatchCheckpoint WithoutFingerprint() => new DatasetBatchCheckpoint
    {
        config_json = config_json, clips = clips, characters = characters, warnings = warnings,
        unity_version = unity_version, camera_views = camera_views
    };

    public static void WriteAtomic(string path, string contents)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(path + ".tmp", contents, new UTF8Encoding(false));
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    // Same-directory rename with replacement; no copy/delete gap.
                    if (!MoveFileEx(path + ".tmp", path, 0x1 | 0x8))
                        throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
                else if (File.Exists(path)) File.Replace(path + ".tmp", path, null);
                else File.Move(path + ".tmp", path);
                return;
            }
            catch (IOException exception)
            {
                if (attempt >= 6) throw new CommitException(path, exception);
                System.Threading.Thread.Sleep(10 << attempt);
            }
        }
    }
}
