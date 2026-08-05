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
    [BindProperty] public string SteamId { get; set; } = string.Empty;
    [BindProperty] public string AspectRatio { get; set; } = "16:9";
    [BindProperty] public OutputOrder OutputOrder { get; set; }
    [BindProperty] public double MinimumScore { get; set; }

    public async Task<IActionResult> OnGetAsync(string publicId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null || !GenerationAccess.CanRead(generation, User, environment)) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingPlayerSelection)
            return RedirectToPage("/Generation", new { publicId });
        Players = await db.GenerationPlayers.AsNoTracking()
            .Where(value => value.GenerationId == generation.Id)
            .OrderByDescending(value => value.CandidateCount)
            .ThenBy(value => value.SteamId)
            .ToListAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string publicId, CancellationToken cancellationToken)
    {
        if (SteamId.Length != 17 || !SteamId.All(char.IsAsciiDigit) ||
            AspectRatio is not ("16:9" or "9:16" or "4:3") ||
            MinimumScore is < 0 or > 1000)
            return BadRequest();
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations
            .Include(value => value.Players)
            .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        if (!GenerationAccess.CanRead(generation, User, environment)) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingPlayerSelection)
            return StatusCode(StatusCodes.Status409Conflict);
        GenerationPlayer? selected = generation.Players.SingleOrDefault(value => value.SteamId == SteamId);
        if (selected is null) return BadRequest("PLAYER_NOT_FOUND");
        foreach (GenerationPlayer player in generation.Players) player.IsSelected = player == selected;
        generation.SelectedSteamId = SteamId;
        generation.SelectedPlayerName = selected.DisplayName;
        generation.MinimumScore = MinimumScore;
        generation.OutputOrder = OutputOrder;
        generation.AspectRatio = AspectRatio;
        (generation.Width, generation.Height) = AspectRatio switch
        {
            "16:9" => (1920, 1080),
            "9:16" => (1080, 1920),
            "4:3" => (1920, 1440),
            _ => throw new InvalidOperationException("Unsupported aspect ratio.")
        };
        generation.Fps = 60;
        GenerationStateMachine.Transition(
            generation, GenerationStatus.AwaitingHighlightSelection, timeProvider.GetUtcNow());
        generation.ProgressPercent = 28;
        await db.SaveChangesAsync(cancellationToken);
        await selections.PrepareRecommendationsAsync(publicId, SteamId, cancellationToken);
        return RedirectToPage("/Highlights", new { publicId });
    }
}
