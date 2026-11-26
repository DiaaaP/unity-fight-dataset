using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FightDataset;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class DatasetOptimizationChecks
{
    public const string Root="DatasetOutput/Checks/Optimization_20261126";
    [Serializable] public sealed class Report
    {
        public string phase,error; public bool passed; public int frames,cases,positiveFrames,sampleCountDifferences;
        public double referenceMs,optimizedMs,sampleMs;public float maximumDepthError;
    }
    public static bool IsRunning { get; private set; }
    private static Report report;private static Queue<string> pending;
    private static DatasetBatchExporter.Metadata data;private static DatasetSceneFactory.Stage stage;
    private static DatasetGeneratorConfig config;private static DatasetPenetrationGuard guard;private static Scene scene;
    private static int frame;private static string sourceBatch;
    public static string Start(string batch="DatasetOutput/Generated/2")
    {
        if(IsRunning||DatasetBatchExporter.IsRunning)throw new InvalidOperationException("Finish active work first.");
        sourceBatch=batch;pending=new Queue<string>(Directory.GetFiles(batch+"/metadata","*.json").OrderBy(p=>p));
        report=new Report {phase="comparing"};Directory.CreateDirectory(Root);IsRunning=true;EditorApplication.update+=Tick;
        return Root+"/differential.json";
    }
    private static void Tick()
    {
        try
        {
            if(stage==null)
            {
                if(pending.Count==0){report.passed=true;report.phase="passed";Finish();return;}
                data=JsonUtility.FromJson<DatasetBatchExporter.Metadata>(File.ReadAllText(pending.Dequeue()));
                var saved=JsonUtility.FromJson<DatasetBatchCheckpoint>(File.ReadAllText(sourceBatch+"/resume.json"));
                config=ScriptableObject.CreateInstance<DatasetGeneratorConfig>();JsonUtility.FromJsonOverwrite(saved.config_json,config);config.catalog=saved.RestoreCatalog();
                scene=EditorSceneManager.NewPreviewScene();stage=DatasetSceneFactory.Create(config,scene,data.seed);
                if(!stage.modelPaths.SequenceEqual(data.actors.Select(a=>a.model_asset)))throw new Exception("Reconstructed identities differ.");
                DatasetPenetrationChecks.Restore(stage,config,data);
                stage.player.neutralClips=data.actors.Select(a=>config.catalog.clips.First(c=>c.assetPath==a.neutral_asset).clip).ToArray();
                guard=new DatasetPenetrationGuard(stage.player);frame=0;report.cases++;
            }
            var watch=System.Diagnostics.Stopwatch.StartNew();stage.player.SampleAtTime(frame/(float)data.fps);report.sampleMs+=watch.Elapsed.TotalMilliseconds;
            Compare();frame++;
            report.phase=data.scenario_id+" "+frame+"/"+data.frame_count;
            if(frame>=data.frame_count)
            {
                // Deliberately invalid placements also need identical rejection.
                stage.player.SampleAtTime(data.actions[0].impact_frame/(float)data.fps);
                Vector3 original=stage.player.GetActorLocalPosition(1),delta=stage.player.GetActorLocalPosition(0)-original;delta.y=0;
                foreach(float shift in new[]{.12f,.25f,.4f})
                {stage.player.SetActorLocalPosition(1,original+delta.normalized*shift);stage.player.SampleAtTime(data.actions[0].impact_frame/(float)data.fps);Compare();}
                Close();
            }
            Write();
        }
        catch(Exception e){report.error=e.ToString();report.phase="failed";Finish();}
    }
    private static void Compare()
    {
        var w=System.Diagnostics.Stopwatch.StartNew();var expected=guard.MeasureReference();report.referenceMs+=w.Elapsed.TotalMilliseconds;
        w.Restart();var actual=guard.Measure();report.optimizedMs+=w.Elapsed.TotalMilliseconds;
        report.frames++;if(expected.samples>0)report.positiveFrames++;
        report.maximumDepthError=Mathf.Max(report.maximumDepthError,Mathf.Abs(expected.depth-actual.depth));
        if(expected.samples!=actual.samples)report.sampleCountDifferences++;
        if((expected.samples>0)!=(actual.samples>0)||Mathf.Abs(expected.depth-actual.depth)>.0001f)
            throw new Exception("Penetration differential at "+data.scenario_id+" frame="+frame+" reference="+JsonUtility.ToJson(expected)+" actual="+JsonUtility.ToJson(actual));
    }
    private static void Write()=>File.WriteAllText(Root+"/differential.json",JsonUtility.ToJson(report,true));
    private static void Close()
    {
        guard?.Dispose();guard=null;if(stage!=null)DatasetSceneFactory.ClosePreviewScene(scene);stage=null;
        if(config){UnityEngine.Object.DestroyImmediate(config.catalog);UnityEngine.Object.DestroyImmediate(config);}config=null;
    }
    private static void Finish(){IsRunning=false;EditorApplication.update-=Tick;Close();Write();}
    public static string StartBenchmark()
    {
        if(IsRunning||DatasetBatchExporter.IsRunning)throw new InvalidOperationException("Finish checks first.");
        var c=UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<DatasetGeneratorConfig>(DatasetCatalogTools.ConfigPath));
        var cat=UnityEngine.Object.Instantiate(c.catalog);c.catalog=cat;
        try
        {
            cat.characters=DatasetCharacterTools.DistinctModels(cat.characters).Where(m=>System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(AssetDatabase.GetAssetPath(m)),@"^Ch\d")).ToList();
            c.scenariosPerCategory=5;c.cameraViews=1;c.generateHit=c.generateKick=true;c.generateNormal=c.generateFight=false;
            c.width=1280;c.height=720;c.fps=25;c.seed=261126570;c.outputDirectory=Root+"/exports";
            c.exportYoloPose=true;c.useAsyncGPUReadback=true;
            var batch=DatasetBatchExporter.StartBatch(settings:c);File.WriteAllText(Root+"/benchmark_directory.txt",batch);return batch;
        }
        finally{UnityEngine.Object.DestroyImmediate(cat);UnityEngine.Object.DestroyImmediate(c);}
    }
}
