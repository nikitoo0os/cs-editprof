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
    IWeaponCatalog weapons,
    IWebHostEnvironment environment) : PageModel
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
    public GenerationDemo Demo { get; private set; } = null!;
    public int DemoNumber { get; private set; }
    public int DemoCount { get; private set; }
    public IReadOnlyList<HighlightCard> Cards { get; private set; } = [];
    public int? MaximumSelectableHighlights { get; private set; }
    public long? MaximumSelectionDurationMilliseconds { get; private set; }
    [BindProperty] public List<string> HighlightIds { get; set; } = [];
    [BindProperty] public EffectPreset EffectPreset { get; set; } = EffectPreset.Dynamic;

    public async Task<IActionResult> OnGetAsync(
        string publicId,
        long? demoId,
        CancellationToken cancellationToken)
    {
        return await LoadAsync(publicId, demoId, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(
        string publicId,
        long demoId,
        CancellationToken cancellationToken)
    {
        if (!await CanReadAsync(publicId, cancellationToken)) return NotFound();
        try
        {
            await selections.SaveSelectionAsync(
                publicId,
                demoId,
                HighlightIds,
                EffectPreset,
                cancellationToken);
            await using GenerationDbContext db =
                await dbFactory.CreateDbContextAsync(cancellationToken);
            Generation generation = await db.Generations.AsNoTracking()
                .SingleAsync(value => value.PublicId == publicId,
                    cancellationToken);
            if (generation.Status == GenerationStatus.AwaitingPlayerSelection)
            {
                long? nextDemoId = await db.GenerationDemos.AsNoTracking()
                    .Where(value =>
                        value.GenerationId == generation.Id &&
                        value.AnalysisStatus == DemoAnalysisStatus.Succeeded &&
                        !value.HighlightSelectionCompleted)
                    .OrderBy(value => value.UploadOrder)
                    .Select(value => (long?)value.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                return RedirectToPage(
                    "/Player",
                    new { publicId, demoId = nextDemoId });
            }
            return RedirectToPage("/Music", new { publicId });
        }
        catch (MusicSelectionCapacityException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                UiText.HighlightRemovalRequired(
                    HighlightIds.Distinct(StringComparer.Ordinal).Count(),
                    exception.Capacity.RequiredRemovalCount));
            return await LoadAsync(publicId, demoId, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                UiText.Error(exception.Message));
            return await LoadAsync(publicId, demoId, cancellationToken);
        }
    }

    private async Task<IActionResult> LoadAsync(
        string publicId,
        long? demoId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null || !GenerationAccess.CanRead(generation, User, environment)) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingHighlightSelection)
            return RedirectToPage("/Generation", new { publicId });
        Generation = generation;
        GenerationDemo[] demos = await db.GenerationDemos.AsNoTracking()
            .Where(value => value.GenerationId == generation.Id)
            .OrderBy(value => value.UploadOrder)
            .ToArrayAsync(cancellationToken);
        GenerationDemo? demo = demoId.HasValue
            ? demos.SingleOrDefault(value => value.Id == demoId.Value)
            : demos.FirstOrDefault(value =>
                value.SelectedSteamId is not null &&
                !value.HighlightSelectionCompleted);
        demo ??= demos.FirstOrDefault(value =>
            value.SelectedSteamId is not null);
        if (demo?.SelectedSteamId is null)
            return RedirectToPage("/Player", new { publicId, demoId });
        Demo = demo;
        DemoCount = demos.Count(value =>
            value.AnalysisStatus == DemoAnalysisStatus.Succeeded);
        DemoNumber = Array.IndexOf(
            demos.Where(value =>
                    value.AnalysisStatus == DemoAnalysisStatus.Succeeded)
                .ToArray(),
            demo) + 1;
        GenerationHighlight[] highlights = await db.GenerationHighlights.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                value.GenerationDemoId == demo.Id &&
                value.SteamId == demo.SelectedSteamId)
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
                demo.OriginalFileName,
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
        GenerationMusic? music = await db.GenerationMusic.AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.GenerationId == generation.Id,
                cancellationToken);
        if (music is
            {
                RightsConfirmed: true,
                AnalysisArtifactId: not null
            })
        {
            MusicSelectionCapacity capacity =
                MusicSelectionCapacityPolicy.Calculate(
                    Cards.Select(value => value.DurationMilliseconds),
                    [],
                    music.DurationMilliseconds,
                    generation.TransitionDurationMilliseconds);
            MaximumSelectableHighlights = capacity.MaximumCount;
            MaximumSelectionDurationMilliseconds =
                capacity.MaximumTimelineMilliseconds;
        }
        return Page();
    }

    private async Task<bool> CanReadAsync(string publicId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking().SingleOrDefaultAsync(
            value => value.PublicId == publicId, cancellationToken);
        return generation is not null && GenerationAccess.CanRead(generation, User, environment);
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
