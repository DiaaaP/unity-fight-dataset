using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Reviewed surface vertex anchors survive uniform height changes and animation.
// Model dependency hashes prevent silently reusing indices after a re-export.
public static class DatasetFaceLandmarks
{
    public const string Path = "Assets/Config/DatasetFaceLandmarks.json";
    [Serializable] public sealed class Entry
    {
        public string model, guid, dependencyHash;
        public bool reviewed;
        public DatasetYoloPose.FaceAnchor[] points;
    }
    [Serializable] public sealed class Table { public int version = 1; public List<Entry> models = new List<Entry>(); }
    private static Table cached;
    private static void Load() { if (cached == null) cached = File.Exists(Path) ? JsonUtility.FromJson<Table>(File.ReadAllText(Path)) : new Table(); }
    public static Entry Find(string model)
    {
        Load();
        string guid = AssetDatabase.AssetPathToGUID(model);
        var entry = cached.models.FirstOrDefault(e => e.guid == guid);
        if (entry != null && entry.dependencyHash != AssetDatabase.GetAssetDependencyHash(model).ToString())
            throw new InvalidOperationException("Face landmark profile needs review after model/material reimport: " + model);
        return entry;
    }
    public static void Save(string model, DatasetYoloPose.FaceAnchor[] points)
    {
        if (points.Length != 5) throw new InvalidOperationException("Exactly five COCO facial landmarks are required.");
        Load();
        string guid = AssetDatabase.AssetPathToGUID(model);
        cached.models.RemoveAll(e => e.guid == guid);
        cached.models.Add(new Entry { model = model, guid = guid, dependencyHash = AssetDatabase.GetAssetDependencyHash(model).ToString(), reviewed = true, points = points });
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
        File.WriteAllText(Path, JsonUtility.ToJson(cached, true));
    }
    public static void ClearCache() => cached = null;
}
