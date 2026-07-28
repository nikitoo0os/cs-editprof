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
            [GenerationStatus.Analyzing] = Set(GenerationStatus.AwaitingPlayerSelection, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.AwaitingPlayerSelection] = Set(GenerationStatus.AwaitingPayment, GenerationStatus.Cancelled),
            [GenerationStatus.AwaitingPayment] = Set(GenerationStatus.PaymentProcessing, GenerationStatus.Cancelled, GenerationStatus.Expired),
            [GenerationStatus.PaymentProcessing] = Set(GenerationStatus.Paid, GenerationStatus.AwaitingPayment, GenerationStatus.Failed),
            [GenerationStatus.Paid] = Set(GenerationStatus.QueuedForGeneration),
            [GenerationStatus.QueuedForGeneration] = Set(GenerationStatus.SelectingHighlights, GenerationStatus.Cancelling),
            [GenerationStatus.SelectingHighlights] = Set(GenerationStatus.RenderingClips, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.RenderingClips] = Set(GenerationStatus.ComposingVideo, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.ComposingVideo] = Set(GenerationStatus.VerifyingOutput, GenerationStatus.Failed, GenerationStatus.Cancelling),
            [GenerationStatus.VerifyingOutput] = Set(GenerationStatus.Completed, GenerationStatus.CompletedWithWarnings, GenerationStatus.Failed),
            [GenerationStatus.Cancelling] = Set(GenerationStatus.Cancelled),
            [GenerationStatus.Failed] = Set(GenerationStatus.QueuedForGeneration)
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
