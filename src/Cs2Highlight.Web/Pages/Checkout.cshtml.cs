using System.Text.Json;
using System.Globalization;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

public sealed class CheckoutModel(
    IDbContextFactory<GenerationDbContext> dbFactory) : PageModel
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    public Generation Generation { get; private set; } = null!;
    public int DemoCount { get; private set; }
    public int SelectedCount { get; private set; }
    public IReadOnlyList<string> Categories { get; private set; } = [];
    public GenerationMovieSettings? MovieSettings { get; private set; }
    public CinematicMoviePlan? CinematicPlan { get; private set; }
    public IReadOnlyList<CinematicTimelineItem> CinematicTimeline { get; private set; } = [];
    public string DisplayPrice =>
        $"{(Generation.PriceAmountMinor / 100m).ToString("0.00", CultureInfo.GetCultureInfo("ru-RU"))} ₽";

    public IActionResult OnGet(
        string publicId,
        CancellationToken cancellationToken) =>
        RedirectToPage("/Generation", new { publicId });

    private async Task<IActionResult> LoadAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingPayment)
            return RedirectToPage("/Generation", new { publicId });
        GenerationTimelinePlan? timeline =
            await db.GenerationTimelinePlans.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == generation.Id,
                    cancellationToken);
        if (timeline is not null &&
            timeline.State == TimelinePlanState.Draft)
        {
            return RedirectToPage("/Timeline", new { publicId });
        }
        Generation = generation;
        MovieSettings = await db.GenerationMovieSettings.AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.GenerationId == generation.Id,
                cancellationToken);
        GenerationCinematicPlan? storedCinematic =
            await db.GenerationCinematicPlans.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == generation.Id,
                    cancellationToken);
        if (storedCinematic is not null)
        {
            CinematicPlan = JsonSerializer.Deserialize<CinematicMoviePlan>(
                storedCinematic.PlanJson,
                WebJson);
            if (CinematicPlan is not null)
                CinematicTimeline = BuildTimeline(CinematicPlan);
        }
        DemoCount = await db.GenerationDemos.CountAsync(
            value => value.GenerationId == generation.Id,
            cancellationToken);
        Categories = await db.GenerationHighlights.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                value.SelectedByUser)
            .Select(value => value.Type)
            .Distinct()
            .OrderBy(value => value)
            .ToArrayAsync(cancellationToken);
        SelectedCount = await db.GenerationHighlights.CountAsync(
            value =>
                value.GenerationId == generation.Id &&
                value.SelectedByUser,
            cancellationToken);
        return Page();
    }

    private static CinematicTimelineItem[] BuildTimeline(
        CinematicMoviePlan plan)
    {
        double Duration(params CinematicSequenceRole[] roles) =>
            plan.Segments
                .Where(value => roles.Contains(value.Role))
                .Sum(value =>
                    value.OutputEndSeconds -
                    value.OutputStartSeconds);
        return new[]
        {
            new CinematicTimelineItem(
                "INTRO",
                Duration(
                    CinematicSequenceRole.Intro,
                    CinematicSequenceRole.CalmBroll)),
            new CinematicTimelineItem(
                "BUILD-UP",
                Duration(
                    CinematicSequenceRole.BuildUp,
                    CinematicSequenceRole.PreKill)),
            new CinematicTimelineItem(
                "HIGHLIGHTS",
                Duration(
                    CinematicSequenceRole.Highlight,
                    CinematicSequenceRole.PeakHighlight)),
            new CinematicTimelineItem(
                "OUTRO",
                Duration(
                    CinematicSequenceRole.Breakdown,
                    CinematicSequenceRole.Resolution,
                    CinematicSequenceRole.Outro))
        }.Where(value => value.DurationSeconds > 0.01).ToArray();
    }

    public IActionResult OnPost(string publicId) =>
        RedirectToPage("/Generation", new { publicId });
}

public sealed record CinematicTimelineItem(
    string Label,
    double DurationSeconds);
