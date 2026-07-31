using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Cs2Highlight.Web.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

public sealed record HighlightCard(
    string Id,
    string Type,
    bool Recommended,
    int RoundNumber,
    long FirstKillTick,
    string MapName,
    string DemoFileName,
    double TotalScore,
    double BeautyScore,
    long DurationMilliseconds,
    int KillCount,
    int HeadshotCount,
    IReadOnlyList<WeaponSequenceSegment> Weapons,
    IReadOnlyList<string> Tags,
    ScoreBreakdown ScoreBreakdown);

public sealed class HighlightsModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    HighlightSelectionService selections,
    IWeaponCatalog weapons) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> KnownTags = new(StringComparer.Ordinal)
    {
        "HEADSHOT",
        "HEADSHOT_STREAK",
        "WALLBANG",
        "ONE_TAP",
        "KNIFE",
        "ZEUS",
        "NO_SCOPE",
        "THROUGH_SMOKE",
        "LOW_HP",
        "LONG_DISTANCE",
        "ROUND_ENDING_KILL",
        "LAST_ENEMY",
        "WEAPON_SWAP",
        "ROUND_WIN",
        "FAST_SEQUENCE"
    };

    public Generation Generation { get; private set; } = null!;
    public IReadOnlyList<HighlightCard> Cards { get; private set; } = [];
    [BindProperty] public List<string> HighlightIds { get; set; } = [];
    [BindProperty] public EffectPreset EffectPreset { get; set; } = EffectPreset.Dynamic;

    public async Task<IActionResult> OnGetAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        return await LoadAsync(publicId, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        try
        {
            await selections.SaveSelectionAsync(
                publicId, HighlightIds, EffectPreset, cancellationToken);
            return RedirectToPage("/Music", new { publicId });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                UiText.Error(exception.Message));
            return await LoadAsync(publicId, cancellationToken);
        }
    }

    private async Task<IActionResult> LoadAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingHighlightSelection)
            return RedirectToPage("/Generation", new { publicId });
        Generation = generation;
        Dictionary<long, string> demos = await db.GenerationDemos.AsNoTracking()
            .Where(value => value.GenerationId == generation.Id)
            .ToDictionaryAsync(value => value.Id, value => value.OriginalFileName, cancellationToken);
        GenerationHighlight[] highlights = await db.GenerationHighlights.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                value.SteamId == generation.SelectedSteamId)
            .OrderByDescending(value => value.TotalScore)
            .ThenBy(value => value.RoundNumber)
            .ThenBy(value => value.FirstKillTick)
            .ToArrayAsync(cancellationToken);
        Cards = highlights.Select(value =>
        {
            WeaponSequenceSegment[] stored = Deserialize<WeaponSequenceSegment[]>(
                value.WeaponSequenceJson, []);
            WeaponSequenceSegment[] safeWeapons = stored.Select(segment =>
            {
                WeaponMetadata metadata = weapons.Resolve(segment.WeaponCode);
                return segment with
                {
                    WeaponCode = metadata.Code,
                    DisplayName = metadata.DisplayName,
                    IconPath = metadata.IconPath
                };
            }).ToArray();
            return new HighlightCard(
                value.HighlightId,
                value.Type,
                value.Recommended,
                value.RoundNumber,
                value.FirstKillTick,
                value.MapName,
                demos.GetValueOrDefault(value.GenerationDemoId, "demo.dem"),
                value.TotalScore,
                value.BeautyScore,
                value.EstimatedDurationMilliseconds,
                value.KillCount,
                value.HeadshotCount,
                safeWeapons,
                Deserialize<string[]>(value.TagsJson, [])
                    .Where(KnownTags.Contains)
                    .ToArray(),
                Deserialize(
                    value.ScoreBreakdownJson,
                    new ScoreBreakdown(
                        value.CombatScore,
                        0,
                        0,
                        0,
                        0,
                        0,
                        value.TotalScore)
                    {
                        CombatScore = value.CombatScore,
                        BeautyScore = value.BeautyScore
                    }));
        }).ToArray();
        return Page();
    }

    private static T Deserialize<T>(string json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
