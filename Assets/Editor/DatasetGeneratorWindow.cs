using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class DatasetGeneratorWindow : EditorWindow
{
    private ProgressBar batchProgress, frameProgress;
    private Label state, totals, current, elapsed;
    private TextField output;
    private Button start, pause, resume, saveStop, choose, open;
    private HelpBox error;
    private IVisualElementScheduledItem refresh;

    [MenuItem("Tools/Fight Dataset/Generator/0 Generation Progress")]
    public static void ShowWindow()
    {
        var window = GetWindow<DatasetGeneratorWindow>("Fight Dataset");
        window.minSize = new Vector2(510, 520);
        window.Show();
    }

    public void CreateGUI()
    {
        refresh?.Pause();
        rootVisualElement.Clear();
        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/DatasetGeneratorWindow.uxml");
        if (!tree) { rootVisualElement.Add(new Label("Не найден DatasetGeneratorWindow.uxml")); return; }
        tree.CloneTree(rootVisualElement);
        batchProgress = rootVisualElement.Q<ProgressBar>("batchProgress");
        frameProgress = rootVisualElement.Q<ProgressBar>("frameProgress");
        state = rootVisualElement.Q<Label>("state");
        totals = rootVisualElement.Q<Label>("totals");
        current = rootVisualElement.Q<Label>("current");
        elapsed = rootVisualElement.Q<Label>("elapsed");
        output = rootVisualElement.Q<TextField>("output");
        error = rootVisualElement.Q<HelpBox>("error");
        start = Bind("start", () => DatasetBatchExporter.StartBatch());
        pause = Bind("pause", DatasetBatchExporter.Pause);
        resume = Bind("resume", DatasetBatchExporter.Resume);
        saveStop = Bind("saveStop", DatasetBatchExporter.SaveAndStop);
        choose = Bind("choose", () =>
        {
            string path = EditorUtility.OpenFolderPanel("Выбери папку batch с resume.json", DatasetBatchExporter.LastBatchDirectory ?? "", "");
            if (!string.IsNullOrEmpty(path)) DatasetBatchExporter.ResumeBatch(path);
        });
        open = Bind("open", () => EditorUtility.RevealInFinder(DatasetBatchExporter.LastBatchDirectory));
        Bind("config", () => Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(DatasetCatalogTools.ConfigPath));
        refresh = rootVisualElement.schedule.Execute(RefreshView).Every(200);
        RefreshView();
    }

    private Button Bind(string name, Action action)
    {
        var button = rootVisualElement.Q<Button>(name);
        button.clicked += () =>
        {
            try { action(); error.text = ""; error.AddToClassList("hidden"); }
            catch (Exception exception) { error.text = exception.Message; error.RemoveFromClassList("hidden"); }
            RefreshView();
        };
        return button;
    }

    public void RefreshView()
    {
        if (state == null) return;
        bool running = DatasetBatchExporter.IsRunning;
        bool paused = DatasetBatchExporter.IsPaused;
        bool editMode = !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling;
        bool saved = DatasetBatchExporter.CanResumeLast;
        if (!running && DatasetBatchExporter.Status.StartsWith("Генерация остановлена:", StringComparison.Ordinal))
        {
            error.text = DatasetBatchExporter.Status;
            error.RemoveFromClassList("hidden");
        }
        int total = DatasetBatchExporter.TotalScenarios;
        state.text = running ? paused ? "На паузе — текущий кадр сохранён в памяти" : "Создаём видео…"
            : saved ? "Пакет сохранён — можно продолжить" : total > 0 ? "Генерация завершена" : "Готов к запуску";
        if (!running && DatasetBatchExporter.FailedScenarios > 0 && !saved) state.text = "Завершено с пропусками — смотри manifest.json";
        batchProgress.value = DatasetBatchExporter.Progress * 100;
        batchProgress.title = $"Весь пакет: {batchProgress.value:F1}%";
        totals.text = $"Готово видео: {DatasetBatchExporter.CompletedVideos} / {DatasetBatchExporter.TotalVideos}\n" +
            $"Готово сценариев: {DatasetBatchExporter.CompletedScenarios} / {total}    Пропущено: {DatasetBatchExporter.FailedScenarios}";
        int frames = DatasetBatchExporter.CurrentFrameCount;
        frameProgress.value = frames > 0 ? DatasetBatchExporter.CurrentFrame * 100f / frames : 0;
        frameProgress.title = frames > 0 ? $"Кадр {DatasetBatchExporter.CurrentFrame} / {frames}" : "Нет активного кодирования";
        current.text = running ? $"Сценарий {DatasetBatchExporter.CurrentScenarioNumber} / {total}   {DatasetBatchExporter.CurrentCategory}"
            : saved ? "При продолжении незавершённый сценарий запишется заново." : "Все готовые файлы находятся в папке результатов.";
        elapsed.text = "Время работы без пауз: " + TimeSpan.FromSeconds(DatasetBatchExporter.ElapsedSeconds).ToString(@"hh\:mm\:ss");
        output.SetValueWithoutNotify(DatasetBatchExporter.LastBatchDirectory ?? "");
        start.SetEnabled(editMode && !running);
        pause.SetEnabled(running && !paused);
        resume.SetEnabled(editMode && ((running && paused) || (!running && saved)));
        saveStop.SetEnabled(running);
        choose.SetEnabled(editMode && !running);
        open.SetEnabled(!string.IsNullOrEmpty(DatasetBatchExporter.LastBatchDirectory) && Directory.Exists(DatasetBatchExporter.LastBatchDirectory));
    }

    private void OnDisable() => refresh?.Pause();
}
