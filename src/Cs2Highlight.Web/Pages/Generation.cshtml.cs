using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

public sealed class GenerationModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    GenerationCancellationRegistry cancellations,
    GenerationWakeSignal queue,
    TimeProvider timeProvider) : PageModel
{
    public Generation Generation { get; private set; } = null!;
    public int DemoCount { get; private set; }
    public int PlayerCount { get; private set; }
    public int HighlightCount { get; private set; }
    public bool CanRetry =>
        Generation.Status == GenerationStatus.Failed &&
        Generation.SelectedSteamId is not null &&
        (Generation.PaymentStatus == PaymentStatus.Succeeded ||
         Generation.ErrorCode?.StartsWith("MUSIC_", StringComparison.Ordinal) == true);

    public async Task<IActionResult> OnGetAsync(string publicId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        Generation = generation;
        DemoCount = await db.GenerationDemos.CountAsync(value => value.GenerationId == generation.Id, cancellationToken);
        PlayerCount = await db.GenerationPlayers.CountAsync(value => value.GenerationId == generation.Id, cancellationToken);
        int selectedHighlights = await db.GenerationHighlights.CountAsync(
            value =>
                value.GenerationId == generation.Id &&
                (value.SelectedByUser || value.SelectedForCompilation),
            cancellationToken);
        HighlightCount = selectedHighlights > 0
            ? selectedHighlights
            : await db.GenerationHighlights.CountAsync(
                value => value.GenerationId == generation.Id,
                cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostCancelAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.SingleOrDefaultAsync(
            value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        if (generation.Status is GenerationStatus.Completed or GenerationStatus.CompletedWithWarnings
            or GenerationStatus.Cancelled or GenerationStatus.Failed or GenerationStatus.Expired)
            return StatusCode(StatusCodes.Status409Conflict);
        if (generation.Status is GenerationStatus.RenderingClips or GenerationStatus.ComposingVideo
            or GenerationStatus.ApplyingEffects or GenerationStatus.Analyzing
            or GenerationStatus.AnalyzingMusic or GenerationStatus.VerifyingClips
            or GenerationStatus.PlanningMusicEdit or GenerationStatus.ApplyingTimeWarp
            or GenerationStatus.MixingAudio or GenerationStatus.ApplyingColorGrade
            or GenerationStatus.RenderingHighlights or GenerationStatus.SynchronizingPeaks
            or GenerationStatus.RenderingCameraPreviews or GenerationStatus.ValidatingCameraShots
            or GenerationStatus.RenderingCinematicShots
            or GenerationStatus.ComposingCinematicTimeline
            or GenerationStatus.MixingNarrativeAudio
            or GenerationStatus.ApplyingNarrativeColor
            or GenerationStatus.BuildingHighlightCatalog
            or GenerationStatus.PreparingRenderPlan or GenerationStatus.SelectingHighlights
            or GenerationStatus.QueuedForGeneration)
        {
            GenerationStateMachine.Transition(
                generation, GenerationStatus.Cancelling, timeProvider.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            cancellations.Cancel(publicId);
        }
        else
        {
            generation.Status = GenerationStatus.Cancelled;
            generation.CurrentStage = "Cancelled";
            generation.UpdatedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
        return RedirectToPage(new { publicId });
    }

    public async Task<IActionResult> OnPostRetryAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.SingleOrDefaultAsync(
            value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        bool musicAnalysisRetry =
            generation.ErrorCode?.StartsWith(
                "MUSIC_",
                StringComparison.Ordinal) == true;
        if (generation.Status != GenerationStatus.Failed ||
            generation.SelectedSteamId is null ||
            (!musicAnalysisRetry &&
             generation.PaymentStatus != PaymentStatus.Succeeded))
            return StatusCode(StatusCodes.Status409Conflict);
        GenerationStateMachine.Transition(
            generation,
            musicAnalysisRetry
                ? GenerationStatus.AnalyzingMusic
                : GenerationStatus.QueuedForGeneration,
            timeProvider.GetUtcNow());
        generation.ErrorCode = null;
        generation.ErrorMessage = null;
        generation.CurrentStage = musicAnalysisRetry
            ? "Music analysis queued for retry"
            : "Queued for retry";
        await db.SaveChangesAsync(cancellationToken);
        queue.Wake();
        return RedirectToPage(new { publicId });
    }
}
