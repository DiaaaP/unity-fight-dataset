using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Read authoritative material->texture connections. Atlas numbers do not mean
// body/hair universally: Ch08 uses 1001 skin, 1002 clothes, 1003 hair.
public static class DatasetFbxMaterials
{
    private sealed class Node { public string name; public List<object> p = new List<object>(); public List<Node> children = new List<Node>(); }
    public static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream))
        {
            if (Encoding.ASCII.GetString(reader.ReadBytes(21)) != "Kaydara FBX Binary  \0") return result;
            stream.Position = 23; bool wide = reader.ReadUInt32() >= 7500;
            var roots = new List<Node>();
            while (stream.Position < stream.Length - (wide ? 25 : 13))
            { var n = ReadNode(reader, wide); if (n == null) break; roots.Add(n); }
            var objects = roots.FirstOrDefault(n => n.name == "Objects");
            var connections = roots.FirstOrDefault(n => n.name == "Connections");
            if (objects == null || connections == null) return result;
            var lookup = objects.children.Where(n => n.name == "Material" || n.name == "Texture").ToDictionary(n => Convert.ToInt64(n.p[0]));
            foreach (var connection in connections.children)
            {
                var p = connection.p;
                if (p.Count < 4 || (string)p[0] != "OP" || !lookup.TryGetValue(Convert.ToInt64(p[1]), out var texture) ||
                    !lookup.TryGetValue(Convert.ToInt64(p[2]), out var material) || texture.name != "Texture" || material.name != "Material") continue;
                string name = ((string)material.p[1]).Split('\0')[0];
                var file = texture.children.FirstOrDefault(n => n.name == "RelativeFilename") ?? texture.children.FirstOrDefault(n => n.name == "FileName");
                if (file == null) continue;
                if (!result.TryGetValue(name, out var maps)) result[name] = maps = new Dictionary<string, string>();
                maps[(string)p[3]] = Path.GetFileName(((string)file.p[0]).Replace('\\', '/'));
            }
        }
        return result;
    }
    private static Node ReadNode(BinaryReader r, bool wide)
    {
        long end = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        long count = wide ? (long)r.ReadUInt64() : r.ReadUInt32();
        if (wide) r.ReadUInt64(); else r.ReadUInt32();
        int length = r.ReadByte(); if (end == 0) return null;
        var n = new Node { name = Encoding.UTF8.GetString(r.ReadBytes(length)) };
        for (long i = 0; i < count; i++)
        {
            char t = (char)r.ReadByte(); object value = null;
            switch (t)
            {
                case 'Y': value = r.ReadInt16(); break; case 'C': value = r.ReadByte(); break;
                case 'I': value = r.ReadInt32(); break; case 'L': value = r.ReadInt64(); break;
                case 'F': value = r.ReadSingle(); break; case 'D': value = r.ReadDouble(); break;
                case 'S': value = Encoding.UTF8.GetString(r.ReadBytes((int)r.ReadUInt32())); break;
                case 'R': r.BaseStream.Seek(r.ReadUInt32(), SeekOrigin.Current); break;
                case 'f': case 'd': case 'l': case 'i': case 'b': case 'c':
                    r.ReadUInt32(); r.ReadUInt32(); r.BaseStream.Seek(r.ReadUInt32(), SeekOrigin.Current); break;
                default: throw new InvalidDataException("Unknown FBX property " + t);
            }
            n.p.Add(value);
        }
        while (r.BaseStream.Position < end - (wide ? 25 : 13)) { var child = ReadNode(r, wide); if (child != null) n.children.Add(child); }
        r.BaseStream.Position = end; return n;
    }
    public static string RepairAll()
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Finish generation first.");
        int changes = 0; var log = new List<string>();
        var profiles = JsonUtility.FromJson<DatasetFaceLandmarks.Table>(File.ReadAllText(DatasetFaceLandmarks.Path));
        foreach (var profile in profiles.models)
        {
            var map = Read(profile.model); bool changed = false;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(profile.model);
            var materials = model.GetComponentsInChildren<SkinnedMeshRenderer>().SelectMany(s => s.sharedMaterials).Distinct();
            string folder = "Assets/Characters/" + Path.GetFileNameWithoutExtension(profile.model) + "_Textures/";
            foreach (var material in materials)
            {
                if (!map.TryGetValue(material.name, out var slots)) continue;
                foreach (var pair in new[] { new[] { "DiffuseColor", "_BaseMap" }, new[] { "NormalMap", "_BumpMap" }, new[] { "Bump", "_BumpMap" } })
                {
                    if (!slots.TryGetValue(pair[0], out string file)) continue;
                    var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + file);
                    if (!texture || material.GetTexture(pair[1]) == texture) continue;
                    // Deliberate native-resolution copies of the same source are valid.
                    var current = material.GetTexture(pair[1]);
                    if (current && AssetDatabase.GetAssetPath(current).Contains("_Enhanced/")) continue;
                    if (pair[1] == "_BumpMap")
                    {
                        var ti = (TextureImporter)AssetImporter.GetAtPath(folder + file);
                        if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); }
                        material.EnableKeyword("_NORMALMAP");
                    }
                    material.SetTexture(pair[1], texture); EditorUtility.SetDirty(material); AssetDatabase.SaveAssetIfDirty(material);
                    log.Add(material.name + " " + pair[1] + " -> " + file); changes++; changed = true;
                }
            }
            if (changed) DatasetFaceLandmarks.Save(profile.model, profile.points);
        }
        AssetDatabase.ImportAsset(DatasetFaceLandmarks.Path); DatasetFaceLandmarks.ClearCache();
        foreach (var p in profiles.models) DatasetFaceLandmarks.Find(p.model);
        Directory.CreateDirectory("DatasetOutput/Checks/Optimization_20261126");
        if(log.Count>0) File.AppendAllLines("DatasetOutput/Checks/Optimization_20261126/material_repairs.txt", log);
        return changes + " corrected texture slots\n" + string.Join("\n", log);
    }
}
