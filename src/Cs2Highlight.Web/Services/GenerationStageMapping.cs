using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Services;

public enum GenerationStageState { Pending, Current, Complete, Failed, Skipped }

public sealed record GenerationStageView(string Key, string Label, GenerationStageState State);

public static class GenerationStageMapping
{
    private static readonly (string Key, string Label, GenerationStatus[] Statuses)[] Stages =
    [
        ("upload", "Загрузка", [GenerationStatus.Draft, GenerationStatus.Uploading, GenerationStatus.Uploaded]),
        ("analysis", "Анализ", [GenerationStatus.QueuedForAnalysis, GenerationStatus.Analyzing, GenerationStatus.BuildingHighlightCatalog, GenerationStatus.AwaitingPlayerSelection, GenerationStatus.AwaitingHighlightSelection]),
        ("music", "Музыка", [GenerationStatus.AwaitingMusicUpload, GenerationStatus.AnalyzingMusic, GenerationStatus.AnalyzingMusicStructure, GenerationStatus.AwaitingMovieConfiguration]),
        ("planning", "Планирование", [GenerationStatus.ValidatingMoviePlan, GenerationStatus.AwaitingPayment, GenerationStatus.PaymentProcessing, GenerationStatus.Paid, GenerationStatus.QueuedForGeneration, GenerationStatus.PreparingRenderPlan, GenerationStatus.SelectingHighlights, GenerationStatus.SelectingMusicExcerpt, GenerationStatus.AnalyzingGameplayTimeline, GenerationStatus.DetectingBroll, GenerationStatus.PlanningNarrative, GenerationStatus.PlanningCameraShots, GenerationStatus.RenderingCameraPreviews, GenerationStatus.ValidatingCameraShots]),
        ("rendering", "Рендер", [GenerationStatus.RenderingClips, GenerationStatus.RenderingHighlights, GenerationStatus.VerifyingClips, GenerationStatus.RenderingCinematicShots]),
        ("synchronization", "Синхронизация", [GenerationStatus.PlanningMusicEdit, GenerationStatus.ApplyingTimeWarp, GenerationStatus.SynchronizingPeaks, GenerationStatus.ComposingCinematicTimeline]),
        ("color", "Цвет и звук", [GenerationStatus.ApplyingEffects, GenerationStatus.ComposingVideo, GenerationStatus.MixingAudio, GenerationStatus.ApplyingColorGrade, GenerationStatus.MixingNarrativeAudio, GenerationStatus.ApplyingNarrativeColor]),
        ("verification", "Проверка", [GenerationStatus.VerifyingCinematicMovie, GenerationStatus.VerifyingOutput]),
        ("ready", "Готово", [GenerationStatus.Completed, GenerationStatus.CompletedWithWarnings])
    ];

    public static IReadOnlyList<GenerationStageView> For(
        GenerationStatus status,
        string? previousActiveStageKey = null)
    {
        int current = Array.FindIndex(Stages, stage => stage.Statuses.Contains(status));
        if (status is GenerationStatus.Failed or GenerationStatus.Cancelled or GenerationStatus.Expired)
            current = previousActiveStageKey is null
                ? Math.Max(0, current)
                : Array.FindIndex(Stages, stage => stage.Key == previousActiveStageKey);
        if (status is GenerationStatus.Completed or GenerationStatus.CompletedWithWarnings) current = Stages.Length - 1;
        return Stages.Select((stage, index) => new GenerationStageView(
            stage.Key,
            stage.Label,
            index < current ? GenerationStageState.Complete :
            index == current ? (status == GenerationStatus.Failed ? GenerationStageState.Failed : GenerationStageState.Current) :
            GenerationStageState.Pending)).ToArray();
    }
}
