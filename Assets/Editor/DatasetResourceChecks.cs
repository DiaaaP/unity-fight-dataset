using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;

public static class DatasetResourceChecks
{
    [Serializable] private sealed class Report
    {
        public bool passed;
        public string phase, error;
        public int purgedGraphs, purgedMeshes, baselineNodes, peakNodes, finalNodes, completed, deferredEmptyGraphs;
    }
    private static Report report;
    private static int index;
    private static bool Ours(PlayableGraph g) => g.IsValid() && (g.GetEditorName() == "CatalogCalibration" ||
        g.GetEditorName().StartsWith("Dataset_Actor_", StringComparison.Ordinal) || g.GetEditorName() == "Dataset_DisposalProbe");
    private static int Nodes() => UnityEditor.Playables.Utility.GetAllGraphs().Where(Ours).Sum(g => g.GetPlayableCount());

    public static string Start()
    {
        if (DatasetBatchExporter.IsRunning || DatasetContactCameraChecks.IsRunning || DatasetStrikeTimingChecks.IsRunning)
            throw new InvalidOperationException("Finish active generation/checks first.");
        report = new Report { phase = "resource_lifetime" }; index = 0;
        var liveGraphs = new HashSet<PlayableGraph>(); var liveMeshes = new HashSet<Mesh>();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var player in Resources.FindObjectsOfTypeAll<DatasetScenarioPlayer>())
        {
            var mixers = typeof(DatasetScenarioPlayer).GetField("mixers", flags).GetValue(player) as Array;
            if (mixers == null) continue;
            foreach (var mixer in mixers)
            {
                liveGraphs.Add((PlayableGraph)mixer.GetType().GetField("graph", flags).GetValue(mixer));
                var mesh = mixer.GetType().GetField("groundMesh", flags).GetValue(mixer) as Mesh;
                if (mesh) liveMeshes.Add(mesh);
            }
        }
        foreach (var current in UnityEditor.Playables.Utility.GetAllGraphs().Where(Ours))
        {
            if (liveGraphs.Contains(current)) continue;
            var graph = current;
            DatasetPlayableLifetime.Release(ref graph); report.purgedGraphs++;
        }
        foreach (var mesh in Resources.FindObjectsOfTypeAll<Mesh>().Where(m =>
            (m.name == "ActorGroundProbe" || m.name == "FallGroundProbe" || m.name == "ContactProbe") &&
            string.IsNullOrEmpty(AssetDatabase.GetAssetPath(m)) && !liveMeshes.Contains(m)))
        { UnityEngine.Object.DestroyImmediate(mesh); report.purgedMeshes++; }
        report.baselineNodes = Nodes();
        EditorApplication.update += Tick; Write();
        return "DatasetOutput/Checks/StrikeTiming_20261110/resource_lifetime.json";
    }

    private static void Tick()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var settings = AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath);
            var stage = DatasetSceneFactory.Create(settings, scene, 71011 + index);
            stage.player.plan = new DatasetScenarioPlan { duration = 3 };
            stage.player.SampleAtTime(0.5f);
            report.peakNodes = Mathf.Max(report.peakNodes, Nodes());
            DatasetSceneFactory.ClosePreviewScene(scene);
            report.finalNodes = Nodes();
            if (report.finalNodes != report.baselineNodes) throw new Exception("Playable nodes retained after closing a temporary scene.");
            report.completed = ++index;
            if (index >= 20)
            {
                report.passed = true; report.phase = "passed";
                report.deferredEmptyGraphs = UnityEditor.Playables.Utility.GetAllGraphs().Count(g => Ours(g) && g.GetPlayableCount() == 0);
                EditorApplication.update -= Tick;
            }
        }
        catch (Exception exception)
        {
            report.error = exception.ToString(); report.phase = "failed"; EditorApplication.update -= Tick;
        }
        finally { if (scene.IsValid()) DatasetSceneFactory.ClosePreviewScene(scene); Write(); }
    }

    private static void Write()
    {
        Directory.CreateDirectory("DatasetOutput/Checks/StrikeTiming_20261110");
        File.WriteAllText("DatasetOutput/Checks/StrikeTiming_20261110/resource_lifetime.json", JsonUtility.ToJson(report, true));
    }
}
