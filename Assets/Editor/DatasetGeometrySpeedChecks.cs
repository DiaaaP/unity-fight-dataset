using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class DatasetGeometrySpeedChecks
{
    public const string Root = "DatasetOutput/Checks/GenerationSpeed_20261111";
    [Serializable] public sealed class Report { public bool passed; public int queries, transformedVertices; public double referenceMs, optimizedMs, transformPointMs, matrixMs; public float maximumGapError, maximumVertexError; }

    public static string Run()
    {
        if (DatasetBatchExporter.IsRunning) throw new InvalidOperationException("Stop generation before benchmark.");
        var config = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
        var report = new Report();
        string directory = File.ReadAllText("Backups/BeforeGenerationSpeedFix_20261111/production_directory.txt").Trim();
        foreach (var file in Directory.GetFiles(directory + "/metadata", "*.json").OrderBy(p => p).Take(6))
        {
            var data = JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(file));
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var stage = DatasetSceneFactory.Create(config, scene, data.seed);
                DatasetPenetrationChecks.Restore(stage, config, data);
                var action = stage.player.plan.actions[0];
                using (var attacker = new DatasetContactGeometry(stage.player.actors[action.actor]))
                using (var defender = new DatasetContactGeometry(stage.player.actors[action.target]))
                foreach (int offset in new[] { -3, 0, 3 })
                foreach (float separation in new[] { 0f, .15f, .3f })
                {
                    var original = stage.player.GetActorLocalPosition(action.target);
                    stage.player.SetActorLocalPosition(action.target, original + Vector3.forward * separation);
                    stage.player.SampleAtTime(Mathf.Max(0, action.impactFrame + offset) / (float)data.fps);
                    var patch = attacker.LimbSurface(action.limb, action.incomingDirection);
                    // Restored metadata stores no velocity for sampling; the full
                    // patch is still the same input for both exact searches.
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var expected = defender.MeasureReference(patch, action.targetLandmark);
                    report.referenceMs += clock.Elapsed.TotalMilliseconds;
                    clock.Restart(); var actual = defender.Measure(patch, action.targetLandmark);
                    report.optimizedMs += clock.Elapsed.TotalMilliseconds;
                    if (float.IsInfinity(expected.gap) != float.IsInfinity(actual.gap)) throw new Exception("Contact validity changed.");
                    if (!float.IsInfinity(expected.gap)) report.maximumGapError = Mathf.Max(report.maximumGapError, Mathf.Abs(expected.gap - actual.gap));
                    report.queries++;
                    stage.player.SetActorLocalPosition(action.target, original);
                }
                foreach (var skin in stage.player.actors.SelectMany(a => a.GetComponentsInChildren<SkinnedMeshRenderer>()))
                {
                    var mesh = new Mesh();
                    try
                    {
                        skin.BakeMesh(mesh, true); var vertices = mesh.vertices; var matrix = skin.transform.localToWorldMatrix;
                        var old = new Vector3[vertices.Length]; var optimized = new Vector3[vertices.Length];
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        for (int i = 0; i < vertices.Length; i++) old[i] = skin.transform.TransformPoint(vertices[i]);
                        report.transformPointMs += clock.Elapsed.TotalMilliseconds;
                        clock.Restart();
                        for (int i = 0; i < vertices.Length; i++) optimized[i] = matrix.MultiplyPoint3x4(vertices[i]);
                        report.matrixMs += clock.Elapsed.TotalMilliseconds;
                        for (int i = 0; i < vertices.Length; i++) report.maximumVertexError = Mathf.Max(report.maximumVertexError, Vector3.Distance(old[i], optimized[i]));
                        report.transformedVertices += vertices.Length;
                    }
                    finally { UnityEngine.Object.DestroyImmediate(mesh); }
                }
            }
            finally { DatasetSceneFactory.ClosePreviewScene(scene); }
        }
        report.passed = report.maximumGapError <= .0001f && report.maximumVertexError <= .0001f;
        Directory.CreateDirectory(Root); File.WriteAllText(Root + "/geometry_benchmark.json", JsonUtility.ToJson(report, true));
        return JsonUtility.ToJson(report);
    }
}
