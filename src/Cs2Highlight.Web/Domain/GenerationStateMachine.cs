namespace Cs2Highlight.Web.Domain;

public static class GenerationStateMachine
{
    private static readonly Dictionary<GenerationStatus, HashSet<GenerationStatus>> Allowed =
        new()
        {
            [GenerationStatus.Draft] = Set(GenerationStatus.Uploading, GenerationStatus.Cancelled),
            [GenerationStatus.Uploading] = Set(GenerationStatus.Uploaded, GenerationStatus.Failed, GenerationStatus.Cancelled),
            [GenerationStatus.Uploaded] = Set(GenerationStatus.QueuedForAnalysis, GenerationStatus.Cancelled),
            [GenerationStatus.QueuedForAnalysis] = Set(GenerationStatus.Analyzing, GenerationStatus.Cancelled),
            [GenerationStatus.Analyzing] = Set(GenerationStatus.BuildingHighlightCatalog, GenerationStatus.AwaitingPlayerSelection, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.BuildingHighlightCatalog] = Set(GenerationStatus.AwaitingPlayerSelection, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.AwaitingPlayerSelection] = Set(GenerationStatus.AwaitingHighlightSelection, GenerationStatus.Cancelled),
            [GenerationStatus.AwaitingHighlightSelection] = Set(GenerationStatus.AwaitingMusicUpload, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Cancelled),
            [GenerationStatus.AwaitingMusicUpload] = Set(GenerationStatus.AnalyzingMusic, GenerationStatus.Cancelled),
            [GenerationStatus.AnalyzingMusic] = Set(GenerationStatus.AnalyzingMusicStructure, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.AnalyzingMusicStructure] = Set(GenerationStatus.AnalyzingMusic, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.AwaitingMovieConfiguration] = Set(GenerationStatus.AwaitingHighlightSelection, GenerationStatus.ValidatingMoviePlan, GenerationStatus.Cancelled),
            [GenerationStatus.ValidatingMoviePlan] = Set(GenerationStatus.SelectingMusicExcerpt, GenerationStatus.AwaitingPayment, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed),
            [GenerationStatus.SelectingMusicExcerpt] = Set(GenerationStatus.AnalyzingGameplayTimeline, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed),
            [GenerationStatus.AnalyzingGameplayTimeline] = Set(GenerationStatus.DetectingBroll, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed),
            [GenerationStatus.DetectingBroll] = Set(GenerationStatus.PlanningNarrative, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed),
            [GenerationStatus.PlanningNarrative] = Set(GenerationStatus.PlanningCameraShots, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed),
            [GenerationStatus.PlanningCameraShots] = Set(GenerationStatus.AwaitingPayment, GenerationStatus.AwaitingMovieConfiguration, GenerationStatus.Failed),
            [GenerationStatus.AwaitingPayment] = Set(GenerationStatus.PaymentProcessing, GenerationStatus.Cancelled, GenerationStatus.Expired),
            [GenerationStatus.PaymentProcessing] = Set(GenerationStatus.Paid, GenerationStatus.AwaitingPayment, GenerationStatus.Failed),
            [GenerationStatus.Paid] = Set(GenerationStatus.QueuedForGeneration),
            [GenerationStatus.QueuedForGeneration] = Set(GenerationStatus.PreparingRenderPlan, GenerationStatus.SelectingHighlights, GenerationStatus.Cancelling),
            [GenerationStatus.PreparingRenderPlan] = Set(GenerationStatus.RenderingClips, GenerationStatus.RenderingHighlights, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.SelectingHighlights] = Set(GenerationStatus.RenderingClips, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.RenderingClips] = Set(GenerationStatus.VerifyingClips, GenerationStatus.PlanningMusicEdit, GenerationStatus.ApplyingEffects, GenerationStatus.ComposingVideo, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.RenderingHighlights] = Set(GenerationStatus.VerifyingClips, GenerationStatus.SynchronizingPeaks, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.VerifyingClips] = Set(GenerationStatus.PlanningMusicEdit, GenerationStatus.SynchronizingPeaks, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.PlanningMusicEdit] = Set(GenerationStatus.ApplyingTimeWarp, GenerationStatus.ApplyingEffects, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ApplyingTimeWarp] = Set(GenerationStatus.ApplyingEffects, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ApplyingEffects] = Set(GenerationStatus.ComposingVideo, GenerationStatus.ComposingCinematicTimeline, GenerationStatus.MixingAudio, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ComposingVideo] = Set(GenerationStatus.MixingAudio, GenerationStatus.ApplyingColorGrade, GenerationStatus.VerifyingOutput, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.MixingAudio] = Set(GenerationStatus.ApplyingColorGrade, GenerationStatus.VerifyingOutput, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ApplyingColorGrade] = Set(GenerationStatus.VerifyingOutput, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.SynchronizingPeaks] = Set(GenerationStatus.RenderingCameraPreviews, GenerationStatus.ApplyingEffects, GenerationStatus.ComposingCinematicTimeline, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.RenderingCameraPreviews] = Set(GenerationStatus.ValidatingCameraShots, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ValidatingCameraShots] = Set(GenerationStatus.RenderingCinematicShots, GenerationStatus.ComposingCinematicTimeline, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.RenderingCinematicShots] = Set(GenerationStatus.ComposingCinematicTimeline, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ComposingCinematicTimeline] = Set(GenerationStatus.MixingNarrativeAudio, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.MixingNarrativeAudio] = Set(GenerationStatus.ApplyingNarrativeColor, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ApplyingNarrativeColor] = Set(GenerationStatus.VerifyingCinematicMovie, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.VerifyingCinematicMovie] = Set(GenerationStatus.Completed, GenerationStatus.CompletedWithWarnings, GenerationStatus.Failed),
            [GenerationStatus.VerifyingOutput] = Set(GenerationStatus.Completed, GenerationStatus.CompletedWithWarnings, GenerationStatus.Failed),
            [GenerationStatus.Cancelling] = Set(GenerationStatus.Cancelled),
            [GenerationStatus.Failed] = Set(
                GenerationStatus.AnalyzingMusic,
                GenerationStatus.QueuedForGeneration)
        };

    public static void Transition(Generation generation, GenerationStatus next, DateTimeOffset now)
    {
        if (!Allowed.TryGetValue(generation.Status, out HashSet<GenerationStatus>? states) ||
            !states.Contains(next))
        {
            throw new InvalidOperationException(
                $"Invalid generation transition: {generation.Status} -> {next}.");
        }
        generation.Status = next;
        generation.CurrentStage = next.ToString();
        generation.UpdatedAt = now;
    }

    private static HashSet<GenerationStatus> Set(params GenerationStatus[] values) => [.. values];
}
