using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Cs2Highlight.Web.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

public sealed class MusicModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    MusicUploadService uploads,
    GenerationStorage storage,
    IMusicEditPlanner musicEditPlanner,
    ICinematicMusicEditPlanAdapter cinematicMusicEditPlanAdapter,
    ICinematicPlanService cinematicPlans,
    IEffectSeedProvider effectSeedProvider,
    TrustedLutCatalog trustedLuts,
    GenerationWakeSignal queue,
    TimeProvider timeProvider) : PageModel
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedWebJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Generation Generation { get; private set; } = null!;
    public GenerationMusic? Music { get; private set; }
    public int BeatCount { get; private set; }
    public int StrongAnchorCount { get; private set; }
    public int DropCount { get; private set; }
    public string EstimatedDurationText { get; private set; } = "—";
    public bool ShowHighlightSelectionReturn { get; private set; }
    public IReadOnlyList<string> AvailableLuts => trustedLuts.Keys;

    [BindProperty] public IFormFile? MusicFile { get; set; }
    [BindProperty] public bool RightsConfirmed { get; set; }
    [BindProperty] public MovieStyle MovieStyle { get; set; } = MovieStyle.Dynamic;
    [BindProperty] public MusicSyncIntensity SyncIntensity { get; set; } = MusicSyncIntensity.Aggressive;
    [BindProperty] public EffectIntensity EffectIntensity { get; set; } = EffectIntensity.Balanced;
    [BindProperty] public List<string> EnabledEffectGroups { get; set; } =
        DynamicEffectGroups.All.OrderBy(value => value, StringComparer.Ordinal).ToList();
    [BindProperty] public ColorGradePreset ColorGrade { get; set; } = ColorGradePreset.Competitive;
    [BindProperty] public string? LutAssetKey { get; set; }
    [BindProperty] public int GameplayGainPercent { get; set; } = 16;
    [BindProperty] public int MusicGainPercent { get; set; } = 71;
    [BindProperty] public MovieDurationSelection CinematicDuration { get; set; } =
        MovieDurationSelection.Auto;
    [BindProperty] public bool AutomaticCinematicCameras { get; set; } = true;
    [BindProperty] public CinematicEditIntensity CinematicEditIntensity { get; set; } =
        CinematicEditIntensity.Balanced;

    public async Task<IActionResult> OnGetAsync(string publicId, CancellationToken cancellationToken) =>
        await LoadAsync(publicId, cancellationToken);

    public async Task<IActionResult> OnPostUploadAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        if (MusicFile is null)
        {
            ModelState.AddModelError(
                string.Empty,
                UiText.Error("MUSIC_FILE_REQUIRED"));
            return await LoadAsync(publicId, cancellationToken);
        }
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.SingleOrDefaultAsync(
            value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingMusicUpload)
            return StatusCode(StatusCodes.Status409Conflict);
        try
        {
            StoredMusicUpload stored = await uploads.SaveAsync(
                publicId, MusicFile, RightsConfirmed, cancellationToken);
            DateTimeOffset now = timeProvider.GetUtcNow();
            db.GenerationMusic.Add(new GenerationMusic
            {
                GenerationId = generation.Id,
                OriginalFileName = stored.OriginalFileName,
                StoredPath = stored.StoredPath,
                FileSizeBytes = stored.Size,
                Sha256 = stored.Sha256,
                ContentType = stored.ContentType,
                DurationMilliseconds = (long)Math.Round(stored.Metadata.DurationSeconds * 1000),
                SampleRate = stored.Metadata.SampleRate,
                Channels = stored.Metadata.Channels,
                RightsConfirmed = true,
                RightsConfirmedAt = now,
                CreatedAt = now
            });
            db.GenerationArtifacts.Add(new GenerationArtifact
            {
                GenerationId = generation.Id,
                Type = ArtifactType.MusicUpload,
                FileName = Path.GetFileName(stored.StoredPath),
                StoredPath = stored.StoredPath,
                ContentType = stored.ContentType,
                FileSizeBytes = stored.Size,
                CreatedAt = now
            });
            GenerationStateMachine.Transition(
                generation, GenerationStatus.AnalyzingMusic, now);
            generation.ProgressPercent = Math.Max(generation.ProgressPercent, 25);
            await db.SaveChangesAsync(cancellationToken);
            queue.Wake();
            return RedirectToPage("/Generation", new { publicId });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                UiText.Error(exception.Message));
            return await LoadAsync(publicId, cancellationToken);
        }
    }

    public async Task<IActionResult> OnPostConfigureAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        if (GameplayGainPercent is < 0 or > 100 || MusicGainPercent is < 0 or > 100)
            return BadRequest();
        if (EnabledEffectGroups.Any(value => !DynamicEffectGroups.All.Contains(value)))
            return BadRequest();
        try
        {
            _ = trustedLuts.Resolve(LutAssetKey);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                UiText.Error(exception.Message));
            return await LoadAsync(publicId, cancellationToken);
        }
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(
            value => value.PublicId == publicId, cancellationToken);
        GenerationMusic music = await db.GenerationMusic.SingleAsync(
            value => value.GenerationId == generation.Id, cancellationToken);
        if (generation.Status != GenerationStatus.AwaitingMovieConfiguration ||
            !music.RightsConfirmed ||
            music.AnalysisArtifactId is null)
            return StatusCode(StatusCodes.Status409Conflict);
        long safeDuration = await db.GenerationHighlights
            .Where(value => value.GenerationId == generation.Id && value.SelectedByUser)
            .SumAsync(value => value.EstimatedDurationMilliseconds, cancellationToken);
        if (safeDuration / 1.3 > music.DurationMilliseconds)
        {
            ShowHighlightSelectionReturn = true;
            ModelState.AddModelError(
                string.Empty,
                UiText.Error("MUSIC_TOO_SHORT_FOR_SELECTION"));
            return await LoadAsync(publicId, cancellationToken);
        }
        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        GenerationStateMachine.Transition(
            generation, GenerationStatus.ValidatingMoviePlan, now);
        GenerationMovieSettings movieSettings = new()
        {
            GenerationId = generation.Id,
            MovieStyle = MovieStyle,
            EffectIntensity = EffectIntensity,
            EnabledEffectGroupsJson = JsonSerializer.Serialize(
                EnabledEffectGroups.Distinct(StringComparer.Ordinal).OrderBy(
                    value => value,
                    StringComparer.Ordinal)),
            EffectPlannerVersion = DynamicEffectPlanner.PlannerVersion,
            EffectSeed = effectSeedProvider.CreateSeed(
                publicId,
                "movie-settings",
                -1,
                DynamicEffectPlanner.PlannerVersion),
            SyncIntensity = SyncIntensity,
            ColorGradePreset = ColorGrade,
            LutAssetKey = string.IsNullOrWhiteSpace(LutAssetKey)
                ? null
                : LutAssetKey,
            GameplayGainDb = PercentToDb(GameplayGainPercent),
            MusicGainDb = PercentToDb(MusicGainPercent),
            CinematicDuration = CinematicDuration,
            AutomaticCinematicCameras = AutomaticCinematicCameras,
            CinematicEditIntensity = CinematicEditIntensity,
            CreatedAt = now
        };
        db.GenerationMovieSettings.Add(movieSettings);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await CreateLockedPlanAsync(
                db,
                generation,
                music,
                movieSettings,
                cancellationToken);
            if (movieSettings.MovieStyle == MovieStyle.CinematicDirector)
            {
                GenerationCinematicPlan? lockedCinematic =
                    db.GenerationCinematicPlans.Local.SingleOrDefault(value =>
                        value.GenerationId == generation.Id) ??
                    await db.GenerationCinematicPlans.SingleOrDefaultAsync(
                        value => value.GenerationId == generation.Id,
                        cancellationToken);
                CinematicMoviePlan? cinematicPlan = lockedCinematic is null
                    ? null
                    : JsonSerializer.Deserialize<CinematicMoviePlan>(
                        lockedCinematic.PlanJson,
                        WebJson);
                if (cinematicPlan is null)
                {
                    throw new InvalidOperationException(
                        "CINEMATIC_LOCKED_PLAN_INVALID");
                }
                generation.EstimatedDurationMilliseconds =
                    (long)Math.Round(
                        cinematicPlan.TargetDurationSeconds * 1000);
            }
            GenerationStateMachine.Transition(
                generation, GenerationStatus.AwaitingPayment, now);
            generation.ProgressPercent = Math.Max(
                generation.ProgressPercent,
                35);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            Generation resetGeneration = await db.Generations.SingleAsync(
                value => value.Id == generation.Id,
                cancellationToken);
            resetGeneration.Status =
                GenerationStatus.AwaitingMovieConfiguration;
            resetGeneration.CurrentStage =
                GenerationStatus.AwaitingMovieConfiguration.ToString();
            resetGeneration.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            ShowHighlightSelectionReturn = exception.Message ==
                "MUSIC_TOO_SHORT_FOR_SELECTION";
            ModelState.AddModelError(
                string.Empty,
                UiText.Error(exception.Message));
            return await LoadAsync(publicId, cancellationToken);
        }
        return MovieStyle == MovieStyle.CinematicDirector
            ? RedirectToPage("/Timeline", new { publicId })
            : RedirectToPage("/Checkout", new { publicId });
    }

    public async Task<IActionResult> OnPostReturnToHighlightsAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.SingleOrDefaultAsync(
            value => value.PublicId == publicId,
            cancellationToken);
        if (generation is null)
            return NotFound();
        if (generation.Status != GenerationStatus.AwaitingMovieConfiguration)
            return StatusCode(StatusCodes.Status409Conflict);

        GenerationStateMachine.Transition(
            generation,
            GenerationStatus.AwaitingHighlightSelection,
            timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return RedirectToPage("/Highlights", new { publicId });
    }

    private async Task<IActionResult> LoadAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        if (generation.Status is not (
            GenerationStatus.AwaitingMusicUpload or
            GenerationStatus.AnalyzingMusic or
            GenerationStatus.AwaitingMovieConfiguration))
            return RedirectToPage("/Generation", new { publicId });
        Generation = generation;
        Music = await db.GenerationMusic.AsNoTracking().SingleOrDefaultAsync(
            value => value.GenerationId == generation.Id, cancellationToken);
        GenerationMusicAnchor[] anchors = await db.GenerationMusicAnchors.AsNoTracking()
            .Where(value => value.GenerationId == generation.Id)
            .ToArrayAsync(cancellationToken);
        BeatCount = anchors.Count(value =>
            value.Type is MusicalAnchorType.Beat or MusicalAnchorType.StrongBeat);
        StrongAnchorCount = anchors.Count(value =>
            value.Type is not MusicalAnchorType.Beat);
        DropCount = anchors.Count(value => value.Type == MusicalAnchorType.Drop);
        long duration = await db.GenerationHighlights
            .Where(value => value.GenerationId == generation.Id && value.SelectedByUser)
            .SumAsync(value => value.EstimatedDurationMilliseconds, cancellationToken);
        EstimatedDurationText =
            $"{TimeSpan.FromMilliseconds(duration / 1.1):m\\:ss}–{TimeSpan.FromMilliseconds(duration / 0.9):m\\:ss}";
        return Page();
    }

    private static double PercentToDb(int percent) =>
        percent <= 0 ? -60 : 20 * Math.Log10(percent / 100d);

    private async Task CreateLockedPlanAsync(
        GenerationDbContext db,
        Generation generation,
        GenerationMusic music,
        GenerationMovieSettings settings,
        CancellationToken cancellationToken)
    {
        GenerationArtifact artifact = await db.GenerationArtifacts.SingleAsync(
            value => value.Id == music.AnalysisArtifactId &&
                value.GenerationId == generation.Id,
            cancellationToken);
        await using FileStream stream = System.IO.File.OpenRead(artifact.StoredPath);
        MusicAnalysis analysis =
            await JsonSerializer.DeserializeAsync<MusicAnalysis>(
                stream,
                WebJson,
                cancellationToken) ??
            throw new InvalidOperationException("MUSIC_ANALYSIS_INVALID");
        GenerationHighlight[] stored = await db.GenerationHighlights
            .Where(value => value.GenerationId == generation.Id && value.SelectedByUser)
            .OrderBy(value => value.SelectionOrder)
            .ThenBy(value => value.HighlightId)
            .ToArrayAsync(cancellationToken);
        List<SelectedHighlight> selected = [];
        foreach (GenerationHighlight value in stored)
        {
            int tickRate = value.TickRate > 0 ? value.TickRate : 64;
            double Seconds(long tick) => tick / (double)tickRate;
            KillDescriptor[] kills = Deserialize<KillDescriptor[]>(value.KillsJson, []);
            HighlightCandidate candidate = new(
                value.HighlightId,
                Enum.Parse<HighlightType>(value.Type),
                value.SteamId,
                generation.SelectedPlayerName ?? value.SteamId,
                value.RoundNumber,
                value.FirstKillTick,
                value.LastKillTick,
                value.StartTick,
                value.EndTick,
                value.KillCount,
                value.HeadshotCount,
                value.TotalScore,
                Deserialize(
                    value.ScoreBreakdownJson,
                    new ScoreBreakdown(value.TotalScore, 0, 0, 0, 0, 0, value.TotalScore)),
                kills.Select(item => item.EventIndex).ToArray(),
                Deserialize<string[]>(value.TagsJson, []))
            {
                BeautyScore = value.BeautyScore,
                Kills = kills,
                WeaponSequence = Deserialize<WeaponSequenceSegment[]>(
                    value.WeaponSequenceJson, [])
            };
            selected.Add(new SelectedHighlight(
                value.HighlightId,
                candidate,
                new SafeClipBounds(
                    Seconds(value.StartTick),
                    Seconds(value.StartTick),
                    Seconds(value.PrimaryKillTick > 0
                        ? value.PrimaryKillTick
                        : value.LastKillTick),
                    Seconds(value.LastKillTick),
                    Seconds(value.SafeEndTick > 0
                        ? value.SafeEndTick
                        : value.EndTick),
                    Seconds(value.EndTick)),
                value.SelectionOrder ?? selected.Count + 1));
        }
        CinematicLockedPlan? cinematic = null;
        if (settings.MovieStyle == MovieStyle.CinematicDirector)
        {
            cinematic = await cinematicPlans.CreateAndLockAsync(
                db,
                generation,
                music,
                settings,
                analysis,
                selected,
                cancellationToken);
        }
        MusicEditPlan plan = cinematic is null
            ? musicEditPlanner.Create(
                generation.PublicId,
                music.StoredPath,
                analysis,
                selected,
                new MusicEditOptions
                {
                    Style = settings.MovieStyle,
                    SyncIntensity = settings.SyncIntensity
                })
            : cinematicMusicEditPlanAdapter.Create(
                generation.PublicId,
                music.StoredPath,
                cinematic.Plan,
                selected);
        string directory = storage.EnsureDirectory(generation.PublicId, "plan");
        string path = Path.Combine(directory, "music-edit-plan.json");
        string temporary = path + ".tmp";
        if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary);
        await System.IO.File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(
                plan,
                IndentedWebJson),
            cancellationToken);
        System.IO.File.Move(temporary, path, true);
        Dictionary<string, GenerationHighlight> byId =
            stored.ToDictionary(value => value.HighlightId, StringComparer.Ordinal);
        foreach (MusicEditSegment segment in plan.Segments)
        {
            GenerationHighlight highlight = byId[segment.HighlightId];
            db.GenerationEditSegments.Add(new GenerationEditSegment
            {
                GenerationId = generation.Id,
                GenerationHighlightId = highlight.Id,
                Sequence = segment.Index,
                MusicalAnchorId = segment.TargetMusicAnchor?.Id,
                OutputStartMilliseconds =
                    (long)Math.Round(segment.OutputStartSeconds * 1000),
                PrimaryKillOutputMilliseconds =
                    (long)Math.Round(segment.PrimaryKillOutputTimeSeconds * 1000),
                BaseSpeedFactor = segment.TimeWarp.BaseSpeedFactor,
                TimeWarpPlanJson = JsonSerializer.Serialize(segment.TimeWarp),
                TransitionIn = segment.TransitionIn,
                TransitionOut = segment.TransitionOut,
                MatchScore = segment.ScoreBreakdown.Total,
                ScoreBreakdownJson = JsonSerializer.Serialize(segment.ScoreBreakdown),
                WarningsJson = JsonSerializer.Serialize(segment.Warnings)
            });
        }
        db.GenerationArtifacts.Add(new GenerationArtifact
        {
            GenerationId = generation.Id,
            Type = ArtifactType.MusicEditPlan,
            FileName = "music-edit-plan.json",
            StoredPath = path,
            ContentType = "application/json",
            FileSizeBytes = new FileInfo(path).Length,
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static T Deserialize<T>(string json, T fallback)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(
                json,
                WebJson) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}
