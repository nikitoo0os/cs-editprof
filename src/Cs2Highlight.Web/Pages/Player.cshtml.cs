using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

public sealed class PlayerModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    HighlightSelectionService selections,
    TimeProvider timeProvider,
    IWebHostEnvironment environment) : PageModel
{
    public IReadOnlyList<GenerationPlayer> Players { get; private set; } = [];
    public GenerationDemo Demo { get; private set; } = null!;
    public int DemoNumber { get; private set; }
    public int DemoCount { get; private set; }
    [BindProperty] public string SteamId { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(
        string publicId,
        long? demoId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null || !GenerationAccess.CanRead(generation, User, environment)) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingPlayerSelection)
            return RedirectToPage("/Generation", new { publicId });
        GenerationDemo[] demos = await SelectableDemosAsync(
            db,
            generation.Id,
            cancellationToken);
        GenerationDemo? demo = demoId.HasValue
            ? demos.SingleOrDefault(value => value.Id == demoId.Value)
            : demos.FirstOrDefault(value =>
                value.SelectedSteamId is null ||
                !value.HighlightSelectionCompleted);
        demo ??= demos.FirstOrDefault();
        if (demo is null)
            return RedirectToPage("/Generation", new { publicId });
        Demo = demo;
        DemoCount = demos.Length;
        DemoNumber = Array.IndexOf(demos, demo) + 1;
        string[] steamIds = await db.GenerationHighlights.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                value.GenerationDemoId == demo.Id)
            .Select(value => value.SteamId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        GenerationPlayer[] players = await db.GenerationPlayers.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                steamIds.Contains(value.SteamId))
            .ToArrayAsync(cancellationToken);
        var perPlayer = await db.GenerationHighlights.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                value.GenerationDemoId == demo.Id)
            .GroupBy(value => value.SteamId)
            .Select(value => new
            {
                SteamId = value.Key,
                CandidateCount = value.Count(),
                TotalKills = value.Sum(item => item.KillCount)
            })
            .ToDictionaryAsync(value => value.SteamId, cancellationToken);
        Players = players.Select(value =>
        {
            var stats = perPlayer[value.SteamId];
            return new GenerationPlayer
            {
                Id = value.Id,
                GenerationId = value.GenerationId,
                SteamId = value.SteamId,
                DisplayName = value.DisplayName,
                DemoCount = 1,
                TotalKills = stats.TotalKills,
                CandidateCount = stats.CandidateCount,
                IsSelected = value.SteamId == demo.SelectedSteamId
            };
        }).OrderByDescending(value => value.CandidateCount)
            .ThenBy(value => value.SteamId)
            .ToArray();
        SteamId = demo.SelectedSteamId ?? string.Empty;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        string publicId,
        long demoId,
        CancellationToken cancellationToken)
    {
        if (SteamId.Length != 17 || !SteamId.All(char.IsAsciiDigit))
            return BadRequest();
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations
            .Include(value => value.Demos)
            .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        if (!GenerationAccess.CanRead(generation, User, environment)) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingPlayerSelection)
            return StatusCode(StatusCodes.Status409Conflict);
        GenerationDemo? demo = generation.Demos.SingleOrDefault(value =>
            value.Id == demoId &&
            value.AnalysisStatus == DemoAnalysisStatus.Succeeded);
        if (demo is null) return BadRequest("DEMO_NOT_FOUND");
        bool playerExists = await db.GenerationHighlights.AnyAsync(value =>
            value.GenerationId == generation.Id &&
            value.GenerationDemoId == demo.Id &&
            value.SteamId == SteamId,
            cancellationToken);
        GenerationPlayer? selected = await db.GenerationPlayers
            .SingleOrDefaultAsync(value =>
                value.GenerationId == generation.Id &&
                value.SteamId == SteamId,
                cancellationToken);
        if (!playerExists || selected is null)
            return BadRequest("PLAYER_NOT_FOUND_IN_DEMO");
        demo.SelectedSteamId = SteamId;
        demo.SelectedPlayerName = selected.DisplayName;
        demo.HighlightSelectionCompleted = false;
        generation.SelectedSteamId ??= SteamId;
        generation.SelectedPlayerName ??= selected.DisplayName;
        GenerationStateMachine.Transition(
            generation, GenerationStatus.AwaitingHighlightSelection, timeProvider.GetUtcNow());
        generation.ProgressPercent = 28;
        await db.SaveChangesAsync(cancellationToken);
        await selections.PrepareRecommendationsAsync(
            publicId,
            demo.Id,
            SteamId,
            cancellationToken);
        return RedirectToPage("/Highlights", new { publicId, demoId = demo.Id });
    }

    private static Task<GenerationDemo[]> SelectableDemosAsync(
        GenerationDbContext db,
        long generationId,
        CancellationToken cancellationToken) =>
        db.GenerationDemos.AsNoTracking()
            .Where(value =>
                value.GenerationId == generationId &&
                value.AnalysisStatus == DemoAnalysisStatus.Succeeded)
            .OrderBy(value => value.UploadOrder)
            .ToArrayAsync(cancellationToken);
}
