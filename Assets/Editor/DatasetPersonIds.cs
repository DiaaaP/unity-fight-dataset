using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Append-only numeric identities. Track 1/2 identifies the instance in a video;
// person_id identifies the same model across videos, roles, heights and batches.
public static class DatasetPersonIds
{
    public const string Path = "Assets/Config/DatasetPersonIds.json";
    [Serializable] public sealed class Person { public int person_id; public string guid, model_asset, label; }
    [Serializable] public sealed class Table { public string schema = "person_identity_v1"; public List<Person> people = new List<Person>(); }
    private static Table table;
    private static void Load() { if (table == null) table = File.Exists(Path) ? JsonUtility.FromJson<Table>(File.ReadAllText(Path)) : new Table(); }
    public static void Ensure(IEnumerable<GameObject> models)
    {
        Load(); bool changed = false;
        foreach (var model in models.OrderBy(AssetDatabase.GetAssetPath, StringComparer.Ordinal))
        {
            string path = AssetDatabase.GetAssetPath(model), guid = AssetDatabase.AssetPathToGUID(path);
            var row = table.people.FirstOrDefault(p => p.guid == guid);
            if (row != null) { if (row.model_asset != path) { row.model_asset = path; changed = true; } continue; }
            string stem = System.Text.RegularExpressions.Regex.Replace(model.name, @"\(\d+\)$", "");
            var duplicate = table.people.FirstOrDefault(p => System.Text.RegularExpressions.Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(p.model_asset), @"\(\d+\)$", "") == stem &&
                AssetDatabase.LoadAssetAtPath<GameObject>(p.model_asset) is GameObject previous && DatasetCharacterTools.SameCharacter(previous, model));
            int id = duplicate?.person_id ?? (table.people.Count == 0 ? 1 : table.people.Max(p => p.person_id) + 1);
            table.people.Add(new Person { person_id = id, guid = guid, model_asset = path, label = "person_" + id.ToString("D3") }); changed = true;
        }
        if (changed) { File.WriteAllText(Path, JsonUtility.ToJson(table, true)); AssetDatabase.ImportAsset(Path); }
    }
    public static int Get(string model)
    {
        Load(); string guid = AssetDatabase.AssetPathToGUID(model);
        return table.people.FirstOrDefault(p => p.guid == guid)?.person_id ?? throw new InvalidOperationException("Unregistered person: " + model);
    }
    public static void WriteSnapshot(string batch)
    { Load(); File.WriteAllText(System.IO.Path.Combine(batch, "people.json"), JsonUtility.ToJson(table, true)); }
    public static string Backfill(string batch)
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish generation first.");
        var catalog = AssetDatabase.LoadAssetAtPath<FightDataset.AnimationCatalog>(DatasetCatalogTools.CatalogPath); Ensure(catalog.characters);
        string backup = "Backups/BeforeOptimization_20261126/metadata_" + System.IO.Path.GetFileName(batch);
        Directory.CreateDirectory(backup); int count = 0;
        foreach (var path in Directory.GetFiles(System.IO.Path.Combine(batch, "metadata"), "*.json"))
        {
            var m = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(path));
            string copy = System.IO.Path.Combine(backup, System.IO.Path.GetFileName(path)); if (!File.Exists(copy)) File.Copy(path, copy);
            for (int i = 0; i < m.actors.Length; i++) { m.actors[i].track_id = i + 1; m.actors[i].person_id = Get(m.actors[i].model_asset); }
            foreach (var action in m.actions) { action.actor_person_id = m.actors[action.actor == "actor_A" ? 0 : 1].person_id; action.target_person_id = m.actors[action.target == "actor_A" ? 0 : 1].person_id; }
            DatasetBatchCheckpoint.WriteAtomic(path, JsonUtility.ToJson(m, true)); count++;
        }
        WriteSnapshot(batch); return count + " metadata files updated; originals backed up at " + backup;
    }
}
