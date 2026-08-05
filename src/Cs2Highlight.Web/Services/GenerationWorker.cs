using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed partial class GenerationWorker(
    IDbContextFactory<GenerationDbContext> dbFactory,
    GenerationWakeSignal queue,
    GenerationCancellationRegistry cancellations,
    GenerationStorage storage,
    PipelineOptions pipelineOptions,
    GlobalHighlightSelector globalSelector,
    IMusicAnalyzerClient musicAnalyzer,
    IMusicalAnchorBuilder musicalAnchorBuilder,
    IMusicEditPlanner musicEditPlanner,
    ICinematicMusicEditPlanAdapter cinematicMusicEditPlanAdapter,
    IMapCameraProfileCatalog mapCameraProfiles,
    CinematicCameraRuntimeOptions cameraRuntime,
    AutomaticCameraCalibrationStore automaticCalibrationStore,
    IEffectPlanner effectPlanner,
    IDynamicEffectPlanner dynamicEffectPlanner,
    ICinematicDynamicEffectAdapter cinematicEffectAdapter,
    IFfmpegCapabilityScanner capabilityScanner,
    IHighlightCompilationService compilationService,
    IHubContext<GenerationHub> hub,
    TimeProvider timeProvider,
    RetentionOptions retentionOptions,
    ITokenService tokenService,
    GenerationMetrics metrics,
    ILoggerFactory loggerFactory,
    ILogger<GenerationWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogIterationFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4001, nameof(LogIterationFailure)),
            "Generation worker iteration failed.");
    private static readonly Action<ILogger, string, Exception?> LogGenerationFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(4002, nameof(LogGenerationFailure)),
            "Generation {PublicId} failed.");
    private static readonly Action<ILogger, string, GenerationStatus, int, string, Exception?>
        LogGenerationStage =
            LoggerMessage.Define<string, GenerationStatus, int, string>(
                LogLevel.Information,
                new EventId(4003, nameof(LogGenerationStage)),
                "[Generation:{GenerationId}] {Status} {Progress}% — {Stage}");
    [LoggerMessage(EventId = 4004, Level = LogLevel.Information, Message = "[Generation:{GenerationId}] Scanning FFmpeg capabilities")]
    private static partial void LogCapabilityScan(ILogger logger, string generationId);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Information, Message = "[Generation:{GenerationId}] FFmpeg {Version}; available={Available}; filters={FilterCount}; warnings={WarningCount}")]
    private static partial void LogCapabilities(ILogger logger, string generationId, string? version, bool available, int filterCount, int warningCount);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Information, Message = "[Generation:{GenerationId}] Reusing locked effect plan for {HighlightId}: seed={Seed}, effects={EffectCount}")]
    private static partial void LogLockedPlan(ILogger logger, string generationId, string highlightId, long seed, int effectCount);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Information, Message = "[Generation:{GenerationId}] Planned effects for {HighlightId}: style={Style}, intensity={Intensity}, seed={Seed}, accepted={Accepted}, rejected={Rejected}")]
    private static partial void LogEffectPlan(ILogger logger, string generationId, string highlightId, MovieStyle style, EffectIntensity intensity, long seed, int accepted, int rejected);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool processed = await ProcessNextAsync(stoppingToken);
                if (!processed)
                {
                    using CancellationTokenSource poll = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    poll.CancelAfter(TimeSpan.FromSeconds(5));
                    try { await queue.WaitAsync(poll.Token); }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                LogIterationFailure(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation[] pending = await db.Generations
            .Where(value =>
                value.Status == GenerationStatus.QueuedForAnalysis ||
                value.Status == GenerationStatus.Analyzing ||
                value.Status == GenerationStatus.AnalyzingMusic ||
                value.Status == GenerationStatus.AnalyzingMusicStructure ||
                value.Status == GenerationStatus.QueuedForGeneration ||
                value.Status == GenerationStatus.PreparingRenderPlan ||
                value.Status == GenerationStatus.SelectingHighlights ||
                value.Status == GenerationStatus.RenderingClips ||
                value.Status == GenerationStatus.RenderingHighlights ||
                value.Status == GenerationStatus.VerifyingClips ||
                value.Status == GenerationStatus.PlanningMusicEdit ||
                value.Status == GenerationStatus.ApplyingTimeWarp ||
                value.Status == GenerationStatus.ApplyingEffects ||
                value.Status == GenerationStatus.ComposingVideo ||
                value.Status == GenerationStatus.MixingAudio ||
                value.Status == GenerationStatus.ApplyingColorGrade ||
                value.Status == GenerationStatus.SynchronizingPeaks ||
                value.Status == GenerationStatus.RenderingCameraPreviews ||
                value.Status == GenerationStatus.ValidatingCameraShots ||
                value.Status == GenerationStatus.RenderingCinematicShots ||
                value.Status == GenerationStatus.ComposingCinematicTimeline ||
                value.Status == GenerationStatus.MixingNarrativeAudio ||
                value.Status == GenerationStatus.ApplyingNarrativeColor ||
                value.Status == GenerationStatus.VerifyingCinematicMovie ||
                value.Status == GenerationStatus.VerifyingOutput ||
                value.Status == GenerationStatus.Cancelling)
            .ToArrayAsync(cancellationToken);
        Generation? generation = pending
            .OrderBy(value => value.CreatedAt)
            .ThenBy(value => value.Id)
            .FirstOrDefault();
        if (generation is null) return false;
        if (generation.Status == GenerationStatus.Cancelling)
        {
            await MarkCancelledAsync(generation.PublicId, cancellationToken);
            return true;
        }
        using CancellationTokenSource generationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cancellations.TokenFor(generation.PublicId));
        try
        {
            if (generation.Status is GenerationStatus.QueuedForAnalysis or GenerationStatus.Analyzing)
                await AnalyzeAsync(generation.PublicId, generationCancellation.Token);
            else if (generation.Status is
                GenerationStatus.AnalyzingMusic or
                GenerationStatus.AnalyzingMusicStructure)
                await AnalyzeMusicAsync(generation.PublicId, generationCancellation.Token);
            else
                await GenerateAsync(generation.PublicId, generationCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkCancelledAsync(generation.PublicId, CancellationToken.None);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("MUSIC_", StringComparison.Ordinal) ||
            exception.Message.StartsWith("CINEMATIC_", StringComparison.Ordinal) ||
            exception.Message.StartsWith("PRIMARY_KILL_", StringComparison.Ordinal) ||
            exception.Message.Contains("LUT_", StringComparison.Ordinal))
        {
            string code = exception.Message.Split(':', 2)[0].Trim();
            LogGenerationFailure(logger, generation.PublicId, exception);
            await FailAsync(
                generation.PublicId,
                code,
                exception.Message,
                cancellationToken,
                refundToken: false);
        }
        catch (Exception exception)
        {
            LogGenerationFailure(logger, generation.PublicId, exception);
            await FailAsync(generation.PublicId, "UNEXPECTED_ERROR", exception.Message, cancellationToken, refundToken: true);
        }
        finally
        {
            cancellations.Complete(generation.PublicId);
        }
        return true;
    }

    private async Task AnalyzeMusicAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await SetStatusAsync(
            publicId, GenerationStatus.AnalyzingMusic, 28,
            "Analyzing music rhythm and strong accents", cancellationToken);
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(
            value => value.PublicId == publicId, cancellationToken);
        GenerationMusic music = await db.GenerationMusic.SingleAsync(
            value => value.GenerationId == generation.Id, cancellationToken);
        if (!music.RightsConfirmed)
        {
            await FailAsync(
                publicId,
                "MUSIC_RIGHTS_CONFIRMATION_REQUIRED",
                "Music rights confirmation is required.",
                cancellationToken);
            return;
        }
        await SetStatusAsync(
            publicId,
            GenerationStatus.AnalyzingMusicStructure,
            30,
            "Analyzing frame-level music structure and peaks",
            cancellationToken);
        await db.Entry(generation).ReloadAsync(cancellationToken);
        string analysisDirectory =
            storage.EnsureDirectory(publicId, "analysis", "music");
        string logDirectory = storage.EnsureDirectory(publicId, "logs");
        string analysisPath = Path.Combine(analysisDirectory, "music-analysis.json");
        MusicAnalysis? analysis = null;
        if (File.Exists(analysisPath))
        {
            analysis = await ReadJsonAsync<MusicAnalysis>(
                analysisPath, cancellationToken);
        }
        if (analysis?.SchemaVersion is not ("2.0" or "2.1") ||
            analysis.Frames.Count == 0 ||
            analysis.FrameHopSeconds is < 0.02 or > 0.05)
        {
            analysis = await musicAnalyzer.AnalyzeAsync(
                music.StoredPath,
                analysisPath,
                Path.Combine(logDirectory, "music-analyzer.log"),
                cancellationToken);
        }
        music.DurationMilliseconds =
            (long)Math.Round(analysis.Audio.DurationSeconds * 1000);
        music.SampleRate = analysis.Audio.SampleRate;
        music.Channels = analysis.Audio.Channels;
        music.TempoBpm = analysis.Audio.TempoBpm;
        music.TempoConfidence = analysis.Audio.TempoConfidence;
        music.AnalyzerName = analysis.Analyzer.Name;
        music.AnalyzerVersion = analysis.Analyzer.Version;
        music.AnalysisSchemaVersion = analysis.SchemaVersion;
        if (!await db.GenerationMusicAnchors.AnyAsync(
                value => value.GenerationId == generation.Id,
                cancellationToken))
        {
            foreach (MusicalAnchor anchor in musicalAnchorBuilder.Build(analysis))
            {
                db.GenerationMusicAnchors.Add(new GenerationMusicAnchor
                {
                    GenerationId = generation.Id,
                    AnchorId = anchor.Id,
                    Type = anchor.Type,
                    TimeMilliseconds = (long)Math.Round(anchor.TimeSeconds * 1000),
                    Strength = anchor.Strength,
                    Confidence = anchor.Confidence
                });
            }
        }
        GenerationArtifact artifact = new()
        {
            GenerationId = generation.Id,
            Type = ArtifactType.MusicAnalysis,
            FileName = "music-analysis.json",
            StoredPath = analysisPath,
            ContentType = "application/json",
            FileSizeBytes = new FileInfo(analysisPath).Length,
            CreatedAt = timeProvider.GetUtcNow()
        };
        db.GenerationArtifacts.Add(artifact);
        GenerationStateMachine.Transition(
            generation,
            GenerationStatus.AwaitingMovieConfiguration,
            timeProvider.GetUtcNow());
        generation.ProgressPercent = Math.Max(generation.ProgressPercent, 35);
        await db.SaveChangesAsync(cancellationToken);
        music.AnalysisArtifactId = artifact.Id;
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(
            publicId,
            GenerationStatus.AwaitingMovieConfiguration,
            35,
            "Music analysis completed",
            cancellationToken);
    }

    private async Task AnalyzeAsync(string publicId, CancellationToken cancellationToken)
    {
        await SetStatusAsync(publicId, GenerationStatus.Analyzing, 12, "Analyzing demos", cancellationToken);
        List<GenerationDemo> demos;
        await using (GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
            demos = await db.GenerationDemos.Where(value => value.Generation.PublicId == publicId)
                .OrderBy(value => value.UploadOrder).ToListAsync(cancellationToken);
        int succeeded = 0;
        string? firstFailure = null;
        for (int index = 0; index < demos.Count; index++)
        {
            GenerationDemo demo = demos[index];
            if (demo.AnalysisStatus == DemoAnalysisStatus.Succeeded) { succeeded++; continue; }
            await SetStatusAsync(
                publicId,
                GenerationStatus.Analyzing,
                12 + (int)(12d * index / Math.Max(1, demos.Count)),
                $"Analyzing demo {index + 1}/{demos.Count}: {demo.OriginalFileName}",
                cancellationToken);
            string output = storage.EnsureDirectory(publicId, "analysis", $"demo-{demo.UploadOrder:D3}");
            if (Directory.EnumerateFileSystemEntries(output).Any())
            {
                string analysis = Path.Combine(output, "demo-analysis.json");
                string highlights = Path.Combine(output, "highlights.json");
                if (File.Exists(analysis) && File.Exists(highlights))
                {
                    await PersistAnalysisAsync(publicId, demo.Id, analysis, highlights, cancellationToken);
                    succeeded++;
                    continue;
                }
                string interrupted = $"{output}.interrupted-{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}";
                storage.EnsureWithinRoot(interrupted);
                Directory.Move(output, interrupted);
                Directory.CreateDirectory(output);
            }
            try
            {
                AnalysisPipeline analysisPipeline = new(
                    new GoCliDemoParser(
                        PipelinePathResolver.Resolve(pipelineOptions.DemoParserPath) ??
                            throw new InvalidOperationException(
                                $"DEMO_PARSER_NOT_FOUND: {pipelineOptions.DemoParserPath}"),
                        TimeSpan.FromMinutes(10)),
                    new RuleBasedHighlightDetector(),
                    new BestHighlightSelector(),
                    new RenderJobBuilder(),
                    timeProvider,
                    loggerFactory.CreateLogger<AnalysisPipeline>());
                AnalysisArtifacts artifacts = await analysisPipeline.RunAsync(
                    demo.StoredPath,
                    output,
                    new HighlightDetectionOptions(),
                    new RenderJobBuildOptions { OutputRoot = output },
                    cancellationToken);
                await PersistAnalysisAsync(
                    publicId, demo.Id, artifacts.DemoAnalysisPath, artifacts.HighlightsPath, cancellationToken);
                succeeded++;
            }
            catch (Exception exception) when (
                pipelineOptions.DemoFailurePolicy == DemoFailurePolicy.SkipInvalidDemo)
            {
                firstFailure ??= $"{demo.OriginalFileName}: {exception.Message}";
                await MarkDemoFailedAsync(demo.Id, exception.Message, cancellationToken);
            }
            await PublishAsync(
                publicId, GenerationStatus.Analyzing,
                10 + (int)(15d * (index + 1) / demos.Count),
                $"Analyzed {index + 1}/{demos.Count} demos",
                cancellationToken);
        }
        if (succeeded == 0)
        {
            string message = firstFailure is null
                ? "No demo was analyzed successfully."
                : $"No demo was analyzed successfully. First error: {firstFailure}";
            await FailAsync(publicId, "ALL_DEMOS_INVALID", message, cancellationToken);
            return;
        }
        await SetStatusAsync(
            publicId, GenerationStatus.BuildingHighlightCatalog, 24,
            "Building highlight catalog", cancellationToken);
        await AggregatePlayersAsync(publicId, cancellationToken);
        await SetStatusAsync(
            publicId, GenerationStatus.AwaitingPlayerSelection, 25,
            "Select a player", cancellationToken);
    }

    private async Task PersistAnalysisAsync(
        string publicId,
        long demoId,
        string analysisPath,
        string highlightsPath,
        CancellationToken cancellationToken)
    {
        DemoAnalysis analysis = await ReadJsonAsync<DemoAnalysis>(analysisPath, cancellationToken);
        HighlightsDocument highlights = await ReadJsonAsync<HighlightsDocument>(highlightsPath, cancellationToken);
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        GenerationDemo demo = await db.GenerationDemos.SingleAsync(value => value.Id == demoId, cancellationToken);
        demo.AnalysisStatus = DemoAnalysisStatus.Succeeded;
        demo.MapName = analysis.Demo.MapName;
        demo.TickRate = analysis.Demo.TickRate;
        demo.DurationTicks = analysis.Demo.DurationTicks;
        if (!await db.GenerationHighlights.AnyAsync(value => value.GenerationDemoId == demoId, cancellationToken))
        {
            foreach (HighlightCandidate item in highlights.Candidates)
            {
                db.GenerationHighlights.Add(new GenerationHighlight
                {
                    GenerationId = demo.GenerationId,
                    GenerationDemoId = demoId,
                    HighlightId = $"demo-{demo.UploadOrder:D3}-{item.Id}",
                    SteamId = item.PlayerId,
                    MapName = string.IsNullOrWhiteSpace(item.MapName)
                        ? analysis.Demo.MapName
                        : item.MapName,
                    Type = item.Type.ToString(),
                    Score = item.Score,
                    RoundNumber = item.RoundNumber,
                    StartTick = item.StartTick,
                    EndTick = item.EndTick,
                    FirstKillTick = item.FirstKillTick,
                    LastKillTick = item.LastKillTick,
                    TickRate = item.TickRate > 0 ? item.TickRate : analysis.Demo.TickRate,
                    RoundStartTick = item.RoundStartTick,
                    PrimaryKillTick = item.PrimaryKillTick > 0
                        ? item.PrimaryKillTick
                        : item.LastKillTick,
                    SafeEndTick = item.SafeEndTick > 0
                        ? item.SafeEndTick
                        : item.EndTick,
                    KillCount = item.KillCount,
                    HeadshotCount = item.HeadshotCount,
                    CombatScore = item.CombatScore,
                    BeautyScore = item.BeautyScore,
                    TotalScore = item.TotalScore,
                    EstimatedDurationMilliseconds = item.EstimatedDurationMilliseconds,
                    WeaponSequenceJson = JsonSerializer.Serialize(item.WeaponSequence, JsonOptions),
                    ScoreBreakdownJson = JsonSerializer.Serialize(item.ScoreBreakdown, JsonOptions),
                    TagsJson = JsonSerializer.Serialize(item.Tags, JsonOptions),
                    KillsJson = JsonSerializer.Serialize(item.Kills, JsonOptions),
                    CreatedAt = timeProvider.GetUtcNow()
                });
            }
        }
        await AddArtifactAsync(
            db, demo.GenerationId, ArtifactType.DemoAnalysis, analysisPath, cancellationToken);
        await AddArtifactAsync(
            db, demo.GenerationId, ArtifactType.Highlights, highlightsPath, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AggregatePlayersAsync(string publicId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations
            .Include(value => value.Demos)
            .Include(value => value.Highlights)
            .Include(value => value.Players)
            .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        db.GenerationPlayers.RemoveRange(generation.Players);
        Dictionary<string, PlayerAggregate> aggregates = new(StringComparer.Ordinal);
        foreach (GenerationDemo demo in generation.Demos.Where(value =>
                     value.AnalysisStatus == DemoAnalysisStatus.Succeeded))
        {
            string path = Path.Combine(
                storage.GenerationRoot(publicId), "analysis", $"demo-{demo.UploadOrder:D3}", "demo-analysis.json");
            DemoAnalysis analysis = await ReadJsonAsync<DemoAnalysis>(path, cancellationToken);
            foreach (DemoPlayer player in analysis.Players.Where(value => value.SteamId is not null))
            {
                string steamId = player.SteamId!;
                if (!aggregates.TryGetValue(steamId, out PlayerAggregate? aggregate))
                    aggregates[steamId] = aggregate = new PlayerAggregate(player.Name);
                aggregate.Names.Add(player.Name);
                aggregate.DemoIds.Add(demo.Id);
                aggregate.Kills += analysis.Kills.Count(kill => kill.KillerPlayerId == steamId);
            }
        }
        foreach ((string steamId, PlayerAggregate aggregate) in aggregates)
        {
            db.GenerationPlayers.Add(new GenerationPlayer
            {
                GenerationId = generation.Id,
                SteamId = steamId,
                DisplayName = aggregate.Names.LastOrDefault() ?? steamId,
                DemoCount = aggregate.DemoIds.Count,
                TotalKills = aggregate.Kills,
                CandidateCount = generation.Highlights.Count(value => value.SteamId == steamId)
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task GenerateAsync(string publicId, CancellationToken cancellationToken)
    {
        Generation snapshot;
        await using (GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
            snapshot = await db.Generations
                .Include(value => value.Demos)
                .Include(value => value.Highlights)
                .Include(value => value.MovieSettings)
                .AsSplitQuery()
                .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        bool cinematicDirector =
            snapshot.MovieSettings?.MovieStyle == MovieStyle.CinematicDirector;
        if (snapshot.PaymentStatus != PaymentStatus.Succeeded)
        {
            await FailAsync(publicId, "PAYMENT_REQUIRED", "Successful payment is required.", cancellationToken);
            return;
        }
        if (snapshot.SelectedSteamId is null)
        {
            await FailAsync(publicId, "PLAYER_NOT_FOUND", "A player was not selected.", cancellationToken);
            return;
        }
        if (snapshot.Status is GenerationStatus.QueuedForGeneration or
            GenerationStatus.PreparingRenderPlan or
            GenerationStatus.SelectingHighlights)
        {
            await SetStatusAsync(
                publicId, GenerationStatus.PreparingRenderPlan, 38,
                "Preparing immutable render plan", cancellationToken);
            if (snapshot.GenerationStartedAt is null)
            {
                await using GenerationDbContext startedDb =
                    await dbFactory.CreateDbContextAsync(cancellationToken);
                Generation started = await startedDb.Generations.SingleAsync(
                    value => value.PublicId == publicId, cancellationToken);
                started.GenerationStartedAt ??= timeProvider.GetUtcNow();
                await startedDb.SaveChangesAsync(cancellationToken);
            }
        }
        Dictionary<long, GenerationDemo> demos = snapshot.Demos.ToDictionary(value => value.Id);
        GenerationHighlight[] manualSelection = snapshot.Highlights
            .Where(value =>
                value.SelectedByUser &&
                value.SteamId == snapshot.SelectedSteamId)
            .OrderBy(value => value.SelectionOrder)
            .ThenBy(value => value.HighlightId, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<GlobalHighlightCandidate> selected = manualSelection.Length > 0
            ? manualSelection.Select(value => new GlobalHighlightCandidate(
                value.GenerationDemoId,
                demos[value.GenerationDemoId].StoredPath,
                demos[value.GenerationDemoId].UploadOrder,
                ToCandidate(value, snapshot.SelectedPlayerName))).ToArray()
            : globalSelector.Select(
                snapshot.Highlights.Select(value => new GlobalHighlightCandidate(
                    value.GenerationDemoId,
                    demos[value.GenerationDemoId].StoredPath,
                    demos[value.GenerationDemoId].UploadOrder,
                    ToCandidate(value, snapshot.SelectedPlayerName))),
                snapshot.SelectedSteamId,
                snapshot.MaximumHighlights,
                snapshot.MinimumScore,
                snapshot.OutputOrder);
        if (selected.Count == 0)
        {
            await FailAsync(publicId, "NO_HIGHLIGHTS_FOUND", "No highlights matched the settings.", cancellationToken);
            return;
        }
        CinematicMoviePlan? cinematicPlan = null;
        IReadOnlyList<GlobalHighlightCandidate> renderSelection = selected;
        if (cinematicDirector)
        {
            await using GenerationDbContext cinematicDb =
                await dbFactory.CreateDbContextAsync(cancellationToken);
            GenerationCinematicPlan locked =
                await cinematicDb.GenerationCinematicPlans.AsNoTracking()
                    .SingleAsync(
                        value =>
                            value.GenerationId == snapshot.Id &&
                            value.LockedAt != null,
                        cancellationToken);
            cinematicPlan = JsonSerializer.Deserialize<CinematicMoviePlan>(
                locked.PlanJson,
                JsonOptions) ??
                throw new InvalidOperationException(
                    "CINEMATIC_LOCKED_PLAN_INVALID");
            CinematicSequenceSegment[] invalidBroll = cinematicPlan.Segments
                .Where(value => value.BrollCandidateId is not null &&
                    (value.Camera.Type == CameraShotType.PlayerPov ||
                     value.Camera.Family == CameraShotFamily.PlayerPov ||
                     value.OutputEndSeconds - value.OutputStartSeconds < 1.5))
                .ToArray();
            if (invalidBroll.Length > 0)
            {
                throw new InvalidOperationException(
                    "CINEMATIC_FILM_CONTRACT_INVALID_BROLL:" +
                    string.Join(',', invalidBroll.Select(value => value.Id)));
            }
            string[] lockedBrollIds = cinematicPlan.Segments
                .Where(value => value.BrollCandidateId is not null)
                .Select(value => value.BrollCandidateId!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            GenerationBrollCandidate[] availableBrollRows =
                await cinematicDb.GenerationBrollCandidates.AsNoTracking()
                    .Where(value => value.GenerationId == snapshot.Id)
                    .ToArrayAsync(cancellationToken);
            HashSet<string> lockedBrollIdSet = lockedBrollIds
                .ToHashSet(StringComparer.Ordinal);
            GenerationBrollCandidate[] brollRows = availableBrollRows
                .Where(value => lockedBrollIdSet.Contains(value.CandidateId))
                .OrderBy(value => value.CandidateId, StringComparer.Ordinal)
                .ToArray();
            if (brollRows.Length != lockedBrollIds.Length)
            {
                string[] found = brollRows
                    .Select(value => value.CandidateId)
                    .ToArray();
                string[] missing = lockedBrollIds
                    .Except(found, StringComparer.Ordinal)
                    .ToArray();
                throw new InvalidOperationException(
                    $"CINEMATIC_LOCKED_BROLL_MISSING:{string.Join(',', missing)}");
            }
            renderSelection =
            [
                .. selected,
                .. brollRows.Select(value => ToBrollRenderCandidate(
                    value,
                    demos[value.GenerationDemoId],
                    snapshot.SelectedSteamId,
                    snapshot.SelectedPlayerName))
            ];
        }
        await PersistSelectionAndPlanAsync(snapshot, selected, cancellationToken);
        if (snapshot.Status is GenerationStatus.QueuedForGeneration or
            GenerationStatus.PreparingRenderPlan or
            GenerationStatus.SelectingHighlights or
            GenerationStatus.RenderingClips or
            GenerationStatus.RenderingHighlights)
        {
            await SetStatusAsync(
                publicId,
                cinematicDirector
                    ? GenerationStatus.RenderingHighlights
                    : GenerationStatus.RenderingClips,
                40,
                $"Rendering 0/{renderSelection.Count} cinematic sources",
                cancellationToken);
        }
        GenerationStatus renderingStatus = cinematicDirector
            ? GenerationStatus.RenderingHighlights
            : GenerationStatus.RenderingClips;
        Dictionary<string, string> rendered = new(StringComparer.Ordinal);
        Dictionary<string, (CinematicSequenceSegment Segment, string Path)>
            cameraOnlySources = new(StringComparer.Ordinal);
        Dictionary<string, string> renderJobPaths =
            new(StringComparer.Ordinal);
        IReadOnlyList<CameraPreviewResult> persistedCameraFallbacks = [];
        bool cameraStagesCompleted = false;
        int renderedCount = 0;
        foreach (IGrouping<long, GlobalHighlightCandidate> demoGroup in
                 renderSelection.GroupBy(value => value.SourceDemoId))
        {
            GenerationDemo demo = demos[demoGroup.Key];
            string batchRoot = storage.EnsureDirectory(
                publicId, "rendered-clips", $"demo-{demo.UploadOrder:D3}");
            JsonBatchStateStore store = new();
            BatchRenderPlan plan;
            BatchRenderState? state = null;
            string planPath = Path.Combine(batchRoot, "batch-plan.json");
            string statePath = Path.Combine(batchRoot, "batch-state.json");
            if (File.Exists(planPath))
            {
                plan = await store.LoadAsync<BatchRenderPlan>(planPath, cancellationToken);
                if (File.Exists(statePath))
                    state = await store.LoadAsync<BatchRenderState>(statePath, cancellationToken);
            }
            else
            {
                BatchPlanBuildResult build = new BatchPlanBuilder(new RenderJobBuilder(), timeProvider).Build(
                    demo.StoredPath,
                    batchRoot,
                    snapshot.SelectedSteamId,
                    demoGroup.Select(value => value.Highlight).ToArray(),
                    new BatchRenderOptions
                    {
                        ContinueOnError = true,
                        UseSharedCs2Session = true,
                        MaximumClips = null,
                        Width = snapshot.Width,
                        Height = snapshot.Height,
                        Fps = snapshot.Fps
                    });
                plan = build.Plan;
                await store.SaveAsync(planPath, plan, cancellationToken);
                foreach (BatchRenderItem item in plan.Items)
                {
                    Directory.CreateDirectory(Path.Combine(item.OutputDirectory, "logs"));
                    Cs2Highlight.RenderAgent.Application.RenderJob job =
                        build.RenderJobs[item.ItemId];
                    if (item.HighlightId.StartsWith(
                            "broll-",
                            StringComparison.Ordinal))
                    {
                        job = job with
                        {
                            CaptureUi = Cs2Highlight.RenderAgent.Application
                                .CaptureUiProfile.Cinematic,
                            PresentationMode = Cs2Highlight.RenderAgent.Application
                                .CapturePresentationMode.CinematicBroll,
                            ContainsFirstPersonWeaponFire = false
                        };
                    }
                    CinematicSequenceSegment? cinematicSource =
                        cinematicPlan?.Segments.FirstOrDefault(value =>
                            string.Equals(
                                value.HighlightId ??
                                value.BrollCandidateId,
                                item.HighlightId,
                                StringComparison.Ordinal));
                    if (cinematicSource?.Camera.RequiresHighFpsCapture == true)
                    {
                        job = job with
                        {
                            Video = job.Video with
                            {
                                Fps = Math.Max(snapshot.Fps, 120)
                            }
                        };
                    }
                    if (cinematicSource is not null &&
                        cinematicSource.Camera.Type !=
                            CameraShotType.PlayerPov)
                    {
                        string mapName = demo.MapName ?? string.Empty;
                        MapCameraProfile? profile =
                            mapCameraProfiles.Find(mapName);
                        job = job with
                        {
                            CaptureUi = Cs2Highlight.RenderAgent.Application
                                .CaptureUiProfile.Cinematic,
                            PresentationMode = Cs2Highlight.RenderAgent.Application
                                .CapturePresentationMode.CinematicBroll,
                            ContainsFirstPersonWeaponFire = false,
                            Camera = BuildRenderCameraPlan(
                                cinematicSource.Camera,
                                mapName,
                                profile?.ManuallyVerified == true,
                                job,
                                cameraRuntime)
                        };
                    }
                    else if (cinematicSource is not null)
                    {
                        job = job with
                        {
                            CaptureUi = Cs2Highlight.RenderAgent.Application
                                .CaptureUiProfile.Gameplay,
                            PresentationMode = Cs2Highlight.RenderAgent.Application
                                .CapturePresentationMode.PovCombat,
                            Camera = Cs2Highlight.RenderAgent.Application
                                .RenderCameraPlan.PlayerPov
                        };
                    }
                    await store.SaveAsync(
                        item.RenderJobPath,
                        job,
                        cancellationToken);
                }
            }
            foreach (BatchRenderItem item in plan.Items)
                renderJobPaths[item.HighlightId] = item.RenderJobPath;
            BatchExecutionResult result = await new BatchRenderOrchestrator(
                new ProcessRenderAgentClient(
                    PipelinePathResolver.Resolve(pipelineOptions.RenderAgentPath) ??
                        throw new InvalidOperationException(
                            $"RENDER_AGENT_NOT_FOUND: {pipelineOptions.RenderAgentPath}")),
                store,
                timeProvider).RunAsync(plan, batchRoot, state, cancellationToken);
            foreach ((BatchRenderItem item, BatchRenderReportItem reportItem) in
                     plan.Items.Zip(result.Report.Items))
            {
                if (reportItem.Status == BatchRenderItemStatus.Succeeded &&
                    reportItem.OutputFile is not null)
                    rendered[item.HighlightId] = reportItem.OutputFile;
            }
            renderedCount += result.Report.Summary.Succeeded;
            await PublishAsync(
                publicId, renderingStatus,
                40 + (int)(45d * renderedCount / renderSelection.Count),
                $"Rendered {renderedCount}/{renderSelection.Count} cinematic sources",
                cancellationToken);
        }
        if (cinematicPlan is not null)
        {
            foreach (CinematicSequenceSegment segment in cinematicPlan.Segments
                         .Where(value => value.Camera.Family !=
                             CameraShotFamily.PlayerPov))
            {
                string sourceId = segment.HighlightId ??
                    segment.BrollCandidateId ?? segment.Id;
                if (rendered.TryGetValue(sourceId, out string? path))
                    cameraOnlySources[segment.Id] = (segment, path);
            }
        }
        // Cinematic Director never reuses historical POV substitutions for
        // B-roll. A route must pass the current camera preview gate.
        if (cinematicPlan is not null && cinematicPlan.Segments.Any(value =>
                value.Camera.Family != CameraShotFamily.PlayerPov))
        {
            await SetStatusAsync(
                publicId,
                GenerationStatus.VerifyingClips,
                85,
                "Verifying rendered cinematic source boundaries",
                cancellationToken);
            await SetStatusAsync(
                publicId,
                GenerationStatus.SynchronizingPeaks,
                86,
                "Synchronizing locked kill anchors before camera preview",
                cancellationToken);
            await SetStatusAsync(
                publicId,
                GenerationStatus.RenderingCameraPreviews,
                87,
                "Analyzing rendered non-POV camera previews",
                cancellationToken);
            CameraPreviewMediaAnalyzer previewAnalyzer = new(pipelineOptions);
            CameraShotQualityAnalyzer qualityAnalyzer = new();
            List<CameraPreviewResult> previews =
                [.. persistedCameraFallbacks];
            Dictionary<string, CameraShotPlan> effectiveCameras =
                new(StringComparer.Ordinal);
            foreach (CinematicSequenceSegment source in cinematicPlan.Segments
                         .Where(value =>
                             value.Camera.Family !=
                             CameraShotFamily.PlayerPov)
                         .OrderBy(value => value.OutputStartSeconds)
                         .ThenBy(value => value.Id, StringComparer.Ordinal))
            {
                string sourceId = source.HighlightId ??
                    source.BrollCandidateId ?? source.Id;
                if (!rendered.TryGetValue(sourceId, out string? previewPath))
                    continue;
                CameraPreviewMetrics metrics =
                    await previewAnalyzer.AnalyzeAsync(
                        previewPath,
                        source.Camera,
                        cancellationToken);
                IReadOnlyList<string> warnings = qualityAnalyzer.Validate(
                    source.Camera,
                    metrics);
                if (warnings.Count == 0)
                {
                    CameraShotPlan accepted = source.Camera with
                    {
                        FramingIntent =
                            "preview-verified persisted camera composition",
                        Warnings = source.Camera.Warnings
                            .Where(value => value !=
                                "CAMERA_PREVIEW_PENDING")
                            .ToArray()
                    };
                    effectiveCameras[source.Id] = accepted;
                    previews.Add(new CameraPreviewResult
                    {
                        CameraShotId = source.Camera.Id,
                        Status = CameraPreviewStatus.Passed,
                        PreviewPath = previewPath,
                        Metrics = metrics,
                        EffectiveShot = accepted,
                        Attempt = 1,
                        Warnings = []
                    });
                    if (accepted.AutomaticCalibration)
                    {
                        string calibrationMap =
                            accepted.Signature?.MapName ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(calibrationMap))
                        {
                            await automaticCalibrationStore.MergeAcceptedAsync(
                                calibrationMap,
                                cameraRuntime.HlaeVersion,
                                [accepted],
                                cancellationToken);
                        }
                    }
                    continue;
                }
                if (!renderJobPaths.TryGetValue(
                        sourceId,
                        out string? renderJobPath))
                {
                    throw new InvalidOperationException(
                        $"CINEMATIC_FREECAM_PREVIEW_FAILED:{sourceId}:" +
                        string.Join(',', warnings));
                }
                JsonBatchStateStore tripodStore = new();
                Cs2Highlight.RenderAgent.Application.RenderJob original =
                    await tripodStore.LoadAsync<
                        Cs2Highlight.RenderAgent.Application.RenderJob>(
                        renderJobPath,
                        cancellationToken);
                (CameraShotPlan tripodShot,
                    Cs2Highlight.RenderAgent.Application.RenderCameraPlan
                        subjectCamera) = SubjectLockedFallback(
                            source.Camera,
                            original,
                            warnings,
                            cameraRuntime.HlaeVersion);
                string tripodDirectory = Path.Combine(
                    original.OutputDirectory,
                    "subject-route-fallback-v2");
                Directory.CreateDirectory(tripodDirectory);
                Cs2Highlight.RenderAgent.Application.RenderJob tripodJob =
                    original with
                    {
                        JobId = original.JobId +
                            "-subject-route-fallback-v2",
                        OutputDirectory = tripodDirectory,
                        Camera = subjectCamera
                    };
                string tripodJobPath = Path.Combine(
                    tripodDirectory,
                    "render-job.json");
                await tripodStore.SaveAsync(
                    tripodJobPath,
                    tripodJob,
                    cancellationToken);
                ProcessRenderAgentClient tripodClient = new(
                    PipelinePathResolver.Resolve(
                        pipelineOptions.RenderAgentPath) ??
                    throw new InvalidOperationException(
                        $"RENDER_AGENT_NOT_FOUND: {pipelineOptions.RenderAgentPath}"));
                RenderInvocationResult tripod = await tripodClient.RenderAsync(
                    tripodJobPath,
                    1,
                    cancellationToken);
                string? tripodOutputPath = tripod.Result?.OutputFile;
                bool tripodOutputReady = tripod.Result?.Success == true &&
                    tripodOutputPath is not null &&
                    File.Exists(tripodOutputPath) &&
                    new FileInfo(tripodOutputPath).Length > 0;
                if (!tripodOutputReady)
                {
                    string errorCode = tripod.Error?.Code ??
                        tripod.Result?.Error?.Code ?? "TRIPOD_RENDER_FAILED";
                    string errorMessage = tripod.Error?.Message ??
                        tripod.Result?.Error?.Message ??
                        "Render Agent returned no valid output file.";
                    throw new InvalidOperationException(
                        $"CINEMATIC_TRIPOD_FALLBACK_FAILED:{sourceId}:" +
                        $"code={errorCode};message={errorMessage};" +
                        $"exitCode={tripod.ExitCode};" +
                        $"renderResult={tripod.RenderResultPath};" +
                        $"job={tripodJobPath}");
                }
                CameraPreviewMetrics tripodMetrics =
                    await previewAnalyzer.AnalyzeAsync(
                        tripodOutputPath!,
                        tripodShot,
                        cancellationToken);
                IReadOnlyList<string> tripodWarnings =
                    qualityAnalyzer.Validate(tripodShot, tripodMetrics);
                if (tripodWarnings.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"CINEMATIC_FREECAM_PREVIEW_FAILED:{sourceId}:" +
                        string.Join(',', warnings.Concat(tripodWarnings)));
                }
                rendered[sourceId] = tripodOutputPath!;
                effectiveCameras[source.Id] = tripodShot;
                previews.Add(new CameraPreviewResult
                {
                    CameraShotId = source.Camera.Id,
                    Status = CameraPreviewStatus.Passed,
                    PreviewPath = tripodOutputPath!,
                    Metrics = tripodMetrics,
                    EffectiveShot = tripodShot,
                    Attempt = 2,
                    Warnings = tripodShot.Warnings
                });
            }
            await SetStatusAsync(
                publicId,
                GenerationStatus.ValidatingCameraShots,
                88,
                "Resolving camera preview validation and fallbacks",
                cancellationToken);
            cinematicPlan = cinematicPlan with
            {
                Segments = cinematicPlan.Segments.Select(value =>
                    effectiveCameras.TryGetValue(
                        value.Id,
                        out CameraShotPlan? camera)
                            ? value with { Camera = camera }
                            : value).ToArray(),
                CameraDiversity = ShotDiversityPolicy.AnalyzeFilm(
                    cinematicPlan.Segments.Select(value =>
                        effectiveCameras.GetValueOrDefault(
                            value.Id,
                            value.Camera)).ToArray(),
                    cinematicPlan.TargetDurationSeconds)
            };
            await PersistCameraPreviewArtifactsAsync(
                snapshot.Id,
                publicId,
                cinematicPlan,
                previews,
                cancellationToken);
            await SetStatusAsync(
                publicId,
                GenerationStatus.RenderingCinematicShots,
                89,
                "Camera previews accepted without POV substitutions",
                cancellationToken);
            await SetStatusAsync(
                publicId,
                GenerationStatus.ComposingCinematicTimeline,
                90,
                "Using preview-validated cinematic sources",
                cancellationToken);
            cameraStagesCompleted = true;
        }
        else if (cinematicPlan is not null)
        {
            await PersistCameraPreviewArtifactsAsync(
                snapshot.Id,
                publicId,
                cinematicPlan,
                persistedCameraFallbacks,
                cancellationToken);
        }
        GlobalHighlightCandidate[] renderedSelection = selected
            .Where(value => rendered.ContainsKey(value.Highlight.Id))
            .ToArray();
        string[] clips = renderedSelection
            .Select(value => rendered[value.Highlight.Id])
            .ToArray();
        if (clips.Length == 0)
        {
            await FailAsync(publicId, "NO_CLIPS_RENDERED", "No selected clip rendered successfully.", cancellationToken);
            return;
        }
        MusicMovieContext? musicContext = await GetOrCreateMusicEditPlanAsync(
            publicId,
            renderedSelection,
            cancellationToken);
        if (musicContext?.Settings.MovieStyle == MovieStyle.CinematicDirector)
        {
            if (cinematicPlan is null)
                throw new InvalidOperationException(
                    "CINEMATIC_LOCKED_PLAN_INVALID");
            Dictionary<string, int> cinematicOrder =
                musicContext.Plan.Segments.ToDictionary(
                    value => value.HighlightId,
                    value => value.Index,
                    StringComparer.Ordinal);
            renderedSelection = renderedSelection
                .OrderBy(value => cinematicOrder.GetValueOrDefault(
                    value.Highlight.Id,
                    int.MaxValue))
                .ThenBy(value => value.Highlight.Id, StringComparer.Ordinal)
                .ToArray();
            CinematicSequenceSegment[] orderedSegments =
                cinematicPlan.Segments
                    .OrderBy(value => value.OutputStartSeconds)
                    .ThenBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray();
            string[] missingSources = orderedSegments
                .Select(value =>
                    value.HighlightId ?? value.BrollCandidateId ??
                    string.Empty)
                .Where(value =>
                    string.IsNullOrWhiteSpace(value) ||
                    !rendered.ContainsKey(value))
                .ToArray();
            if (missingSources.Length > 0)
                throw new InvalidOperationException(
                    $"CINEMATIC_SOURCE_RENDER_MISSING:{string.Join(',', missingSources)}");
            clips = orderedSegments
                .Select(value => rendered[
                    value.HighlightId ??
                    value.BrollCandidateId!])
                .ToArray();
        }
        int resumeStage = GenerationStageOrder(snapshot.Status);
        if (cameraStagesCompleted)
        {
            resumeStage = GenerationStageOrder(
                GenerationStatus.ComposingCinematicTimeline);
        }
        if (snapshot.Status is
            GenerationStatus.RenderingCameraPreviews or
            GenerationStatus.ValidatingCameraShots or
            GenerationStatus.RenderingCinematicShots)
        {
            if (cinematicPlan is null ||
                cinematicPlan.Segments.Any(value =>
                    value.Camera.Type != CameraShotType.PlayerPov))
            {
                throw new InvalidOperationException(
                    "CINEMATIC_CAMERA_RECOVERY_REQUIRES_MANUAL_REVIEW");
            }
            if (snapshot.Status == GenerationStatus.RenderingCameraPreviews)
            {
                await SetStatusAsync(
                    publicId,
                    GenerationStatus.ValidatingCameraShots,
                    87,
                    "Recovered camera preview stage; locked plan uses POV fallback",
                    cancellationToken);
            }
            await SetStatusAsync(
                publicId,
                GenerationStatus.ComposingCinematicTimeline,
                88,
                "Recovered locked POV cinematic plan",
                cancellationToken);
            resumeStage = GenerationStageOrder(
                GenerationStatus.ComposingCinematicTimeline);
        }
        if (musicContext is not null &&
            musicContext.Settings.MovieStyle == MovieStyle.CinematicDirector)
        {
            if (resumeStage <= GenerationStageOrder(GenerationStatus.VerifyingClips))
                await SetStatusAsync(
                    publicId,
                    GenerationStatus.VerifyingClips,
                    85,
                    "Verifying safe highlight and post-kill boundaries",
                    cancellationToken);
            if (resumeStage <= GenerationStageOrder(GenerationStatus.SynchronizingPeaks))
                await SetStatusAsync(
                    publicId,
                    GenerationStatus.SynchronizingPeaks,
                    87,
                    "Synchronizing primary kills with locked high-energy peaks",
                    cancellationToken);
        }
        else if (musicContext is not null)
        {
            if (resumeStage <= GenerationStageOrder(GenerationStatus.VerifyingClips))
                await SetStatusAsync(
                    publicId, GenerationStatus.VerifyingClips, 85,
                    "Verifying safe clip boundaries", cancellationToken);
            if (resumeStage <= GenerationStageOrder(GenerationStatus.PlanningMusicEdit))
                await SetStatusAsync(
                    publicId, GenerationStatus.PlanningMusicEdit, 86,
                    "Synchronizing highlights with musical accents", cancellationToken);
            if (resumeStage <= GenerationStageOrder(GenerationStatus.ApplyingTimeWarp))
                await SetStatusAsync(
                    publicId, GenerationStatus.ApplyingTimeWarp, 87,
                    "Applying bounded gameplay time warp", cancellationToken);
        }
        if (resumeStage <= GenerationStageOrder(GenerationStatus.ApplyingEffects))
            await SetStatusAsync(
                publicId, GenerationStatus.ApplyingEffects, 85,
                "Applying effects and normalizing clips", cancellationToken);
        GenerationStatus compilationStatus =
            resumeStage >= GenerationStageOrder(GenerationStatus.ApplyingEffects)
                ? snapshot.Status
                : GenerationStatus.ApplyingEffects;
        string output = storage.EnsureDirectory(publicId, "output");
        Progress<CompilationProgress> progress = new(value =>
            _ = PublishAsync(
                publicId, compilationStatus,
                85 + (int)(value.Percent * 0.12),
                value.Stage,
                CancellationToken.None));
        IReadOnlyList<HighlightEffectPlan> effectPlans =
            cinematicPlan is null
                ? await GetOrCreateEffectPlansAsync(
                    publicId,
                    renderedSelection,
                    snapshot.EffectPreset,
                    cancellationToken)
                : [];
        FfmpegCapabilities? capabilities = null;
        IReadOnlyList<DynamicEffectPlan>? dynamicEffectPlans = null;
        GlobalHighlightCandidate[] dynamicSelection = renderedSelection;
        if (cinematicPlan is not null)
        {
            HashSet<string> cinematicHighlightIds = cinematicPlan.Segments
                .Where(value => value.HighlightId is not null)
                .Select(value => value.HighlightId!)
                .ToHashSet(StringComparer.Ordinal);
            dynamicSelection = renderedSelection
                .Where(value => cinematicHighlightIds.Contains(
                    value.Highlight.Id))
                .ToArray();
        }
        if (musicContext is not null &&
            (snapshot.EffectPreset != EffectPreset.None ||
             cinematicPlan is not null))
        {
            capabilities = await GetOrCreateCapabilitiesAsync(
                publicId,
                cancellationToken);
            dynamicEffectPlans = await GetOrCreateDynamicEffectPlansAsync(
                publicId,
                dynamicSelection,
                musicContext,
                capabilities,
                cancellationToken);
        }
        if (musicContext is not null &&
            musicContext.Settings.MovieStyle == MovieStyle.CinematicDirector)
        {
            if (resumeStage <= GenerationStageOrder(
                    GenerationStatus.ComposingCinematicTimeline))
                await SetStatusAsync(
                    publicId,
                    GenerationStatus.ComposingCinematicTimeline,
                    88,
                    "Composing locked cinematic timeline",
                    cancellationToken);
            if (resumeStage <= GenerationStageOrder(
                    GenerationStatus.MixingNarrativeAudio))
                await SetStatusAsync(
                    publicId,
                    GenerationStatus.MixingNarrativeAudio,
                    92,
                    "Mixing section-aware music and gameplay audio",
                    cancellationToken);
            if (resumeStage <= GenerationStageOrder(
                    GenerationStatus.ApplyingNarrativeColor))
                await SetStatusAsync(
                    publicId,
                    GenerationStatus.ApplyingNarrativeColor,
                    96,
                    "Applying restrained narrative color grade",
                    cancellationToken);
        }
        else if (musicContext is not null)
        {
            if (resumeStage <= GenerationStageOrder(GenerationStatus.ComposingVideo))
                await SetStatusAsync(
                    publicId, GenerationStatus.ComposingVideo, 88,
                    "Composing music-driven video timeline", cancellationToken);
            if (resumeStage <= GenerationStageOrder(GenerationStatus.MixingAudio))
                await SetStatusAsync(
                    publicId, GenerationStatus.MixingAudio, 92,
                    "Mixing music and gameplay audio", cancellationToken);
            if (resumeStage <= GenerationStageOrder(GenerationStatus.ApplyingColorGrade))
                await SetStatusAsync(
                    publicId, GenerationStatus.ApplyingColorGrade, 96,
                    "Applying consistent color grade", cancellationToken);
        }
        IReadOnlyList<HighlightEffectPlan?> compilationEffectPlans =
            effectPlans.Select(value => (HighlightEffectPlan?)value).ToArray();
        IReadOnlyList<DynamicEffectPlan?>? compilationDynamicEffectPlans =
            dynamicEffectPlans?
                .Select(value => (DynamicEffectPlan?)value)
                .ToArray();
        if (cinematicPlan is not null)
        {
            Dictionary<string, DynamicEffectPlan> dynamicByHighlight =
                dynamicEffectPlans is null
                    ? new Dictionary<string, DynamicEffectPlan>(
                        StringComparer.Ordinal)
                    : dynamicSelection
                        .Zip(dynamicEffectPlans)
                        .ToDictionary(
                            value => value.First.Highlight.Id,
                            value => value.Second,
                            StringComparer.Ordinal);
            CinematicSequenceSegment[] orderedSegments =
                cinematicPlan.Segments
                    .OrderBy(value => value.OutputStartSeconds)
                    .ThenBy(value => value.Id, StringComparer.Ordinal)
                    .ToArray();
            compilationEffectPlans = orderedSegments
                .Select(_ => (HighlightEffectPlan?)null)
                .ToArray();
            compilationDynamicEffectPlans = orderedSegments
                .Select(value =>
                    value.HighlightId is null
                        ? null
                        : dynamicByHighlight.GetValueOrDefault(
                            value.HighlightId))
                .ToArray();
        }
        CompilationResult compilation = await compilationService.ComposeAsync(
            new CompilationRequest(
                clips,
                output,
                snapshot.Width,
                snapshot.Height,
                snapshot.Fps,
                EffectPlans: compilationEffectPlans,
                MusicEditPlan: musicContext?.Plan,
                MusicPath: musicContext?.MusicPath,
                MovieSettings: musicContext?.Settings,
                DynamicEffectPlans: compilationDynamicEffectPlans,
                FfmpegCapabilities: capabilities,
                CinematicMoviePlan: cinematicPlan),
            progress,
            cancellationToken);
        if (!compilation.Success || compilation.OutputFile is null)
        {
            await FailAsync(
                publicId, "COMPILATION_FAILED",
                compilation.Error ?? "Compilation failed.", cancellationToken);
            return;
        }
        if (cinematicPlan is not null)
        {
            if (compilation.IncludedClips != cinematicPlan.Segments.Count)
            {
                await FailAsync(
                    publicId,
                    "CINEMATIC_COMPOSITION_INCOMPLETE",
                    $"Expected {cinematicPlan.Segments.Count} cinematic " +
                    $"segments, compiled {compilation.IncludedClips}.",
                    cancellationToken);
                return;
            }
            double actualDuration = compilation.DurationMilliseconds / 1000d;
            if (Math.Abs(
                    actualDuration -
                    cinematicPlan.TargetDurationSeconds) >
                Math.Max(0.05, 2d / Math.Max(1, snapshot.Fps)))
            {
                await FailAsync(
                    publicId,
                    "CINEMATIC_OUTPUT_DURATION_MISMATCH",
                    $"Planned {cinematicPlan.TargetDurationSeconds:F3}s, " +
                    $"rendered {actualDuration:F3}s.",
                    cancellationToken);
                return;
            }
        }
        if (musicContext is null &&
            snapshot.Status != GenerationStatus.VerifyingOutput)
            await SetStatusAsync(
                publicId, GenerationStatus.ComposingVideo, 97,
                "Final composition completed", cancellationToken);
        CompilationResult? cameraOnlyCompilation = null;
        if (cinematicPlan is not null && cameraOnlySources.Count > 0)
        {
            CinematicSequenceSegment[] cameraSegments = cameraOnlySources
                .Values
                .Select(value => value.Segment)
                .OrderBy(value => value.OutputStartSeconds)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToArray();
            CinematicMoviePlan cameraOnlyPlan =
                CameraOnlyVariantPlanner.Create(cinematicPlan, cameraSegments);
            string[] cameraClips = cameraSegments
                .Select(value => cameraOnlySources[value.Id].Path)
                .ToArray();
            MusicEditPlan? sourceMusicPlan = musicContext?.Plan;
            MusicEditPlan? cameraMusicPlan = sourceMusicPlan is null
                ? null
                : sourceMusicPlan with
                {
                    Segments = [],
                    Warnings = sourceMusicPlan.Warnings.Concat(
                        ["CAMERA_ONLY_VARIANT"])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
            CompilationResult candidate =
                await compilationService.ComposeAsync(
                    new CompilationRequest(
                        cameraClips,
                        storage.EnsureDirectory(
                            publicId,
                            "output",
                            "camera-only"),
                        snapshot.Width,
                        snapshot.Height,
                        snapshot.Fps,
                        MusicEditPlan: cameraMusicPlan,
                        MusicPath: musicContext?.MusicPath,
                        MovieSettings: musicContext?.Settings,
                        FfmpegCapabilities: capabilities,
                        CinematicMoviePlan: cameraOnlyPlan),
                    progress: null,
                    cancellationToken);
            if (candidate.Success &&
                candidate.OutputFile is not null &&
                candidate.IncludedClips == cameraOnlyPlan.Segments.Count)
            {
                cameraOnlyCompilation = candidate;
            }
        }
        await SetStatusAsync(
            publicId,
            cinematicDirector
                ? GenerationStatus.VerifyingCinematicMovie
                : GenerationStatus.VerifyingOutput,
            98,
            cinematicDirector
                ? "Verifying cinematic movie and alignment artifacts"
                : "Verifying output",
            cancellationToken);
        await CompleteAsync(
            publicId,
            selected.Count,
            renderedSelection.Length,
            compilation,
            cameraOnlyCompilation,
            cancellationToken);
    }

    private async Task<MusicMovieContext?> GetOrCreateMusicEditPlanAsync(
        string publicId,
        GlobalHighlightCandidate[] selected,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(
            value => value.PublicId == publicId, cancellationToken);
        GenerationMusic? music = await db.GenerationMusic.SingleOrDefaultAsync(
            value => value.GenerationId == generation.Id, cancellationToken);
        GenerationMovieSettings? settings =
            await db.GenerationMovieSettings.SingleOrDefaultAsync(
                value => value.GenerationId == generation.Id, cancellationToken);
        if (music is null || settings is null)
            return null;
        if (!music.RightsConfirmed || settings.LockedAt is null ||
            music.AnalysisArtifactId is null)
            throw new InvalidOperationException("MUSIC_PLAN_NOT_LOCKED");
        GenerationArtifact analysisArtifact = await db.GenerationArtifacts.SingleAsync(
            value => value.Id == music.AnalysisArtifactId.Value &&
                value.GenerationId == generation.Id,
            cancellationToken);
        MusicAnalysis analysis = await ReadJsonAsync<MusicAnalysis>(
            analysisArtifact.StoredPath, cancellationToken);
        string planDirectory = storage.EnsureDirectory(publicId, "plan");
        string planPath = Path.Combine(planDirectory, "music-edit-plan.json");
        MusicEditPlan plan;
        if (File.Exists(planPath))
        {
            plan = await ReadJsonAsync<MusicEditPlan>(planPath, cancellationToken);
        }
        else
        {
            List<SelectedHighlight> inputs = [];
            for (int index = 0; index < selected.Length; index++)
            {
                GlobalHighlightCandidate candidate = selected[index];
                GenerationHighlight stored = await db.GenerationHighlights.SingleAsync(
                    value =>
                        value.GenerationId == generation.Id &&
                        value.GenerationDemoId == candidate.SourceDemoId &&
                        value.HighlightId == candidate.Highlight.Id,
                    cancellationToken);
                int tickRate = stored.TickRate > 0 ? stored.TickRate : 64;
                double Seconds(long tick) => tick / (double)tickRate;
                SafeClipBounds bounds = new(
                    Seconds(stored.StartTick),
                    Seconds(stored.StartTick),
                    Seconds(stored.PrimaryKillTick > 0
                        ? stored.PrimaryKillTick
                        : stored.LastKillTick),
                    Seconds(stored.LastKillTick),
                    Seconds(stored.SafeEndTick > 0
                        ? stored.SafeEndTick
                        : stored.EndTick),
                    Seconds(stored.EndTick));
                inputs.Add(new SelectedHighlight(
                    stored.HighlightId,
                    candidate.Highlight,
                    bounds,
                    stored.SelectionOrder ?? index + 1));
            }
            if (settings.MovieStyle == MovieStyle.CinematicDirector)
            {
                GenerationCinematicPlan locked =
                    await db.GenerationCinematicPlans.AsNoTracking()
                        .SingleAsync(
                            value =>
                                value.GenerationId == generation.Id &&
                                value.LockedAt != null,
                            cancellationToken);
                CinematicMoviePlan cinematic =
                    JsonSerializer.Deserialize<CinematicMoviePlan>(
                        locked.PlanJson,
                        JsonOptions) ??
                    throw new InvalidOperationException(
                        "CINEMATIC_LOCKED_PLAN_INVALID");
                plan = cinematicMusicEditPlanAdapter.Create(
                    publicId,
                    music.StoredPath,
                    cinematic,
                    inputs);
            }
            else
            {
                plan = musicEditPlanner.Create(
                    publicId,
                    music.StoredPath,
                    analysis,
                    inputs,
                    new MusicEditOptions
                    {
                        Style = settings.MovieStyle,
                        SyncIntensity = settings.SyncIntensity
                    });
            }
            string temporary = planPath + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(plan, JsonOptions),
                cancellationToken);
            File.Move(temporary, planPath);
            Dictionary<string, GenerationHighlight> highlights =
                await db.GenerationHighlights
                    .Where(value => value.GenerationId == generation.Id)
                    .ToDictionaryAsync(
                        value => value.HighlightId,
                        StringComparer.Ordinal,
                        cancellationToken);
            foreach (MusicEditSegment segment in plan.Segments)
            {
                GenerationHighlight highlight = highlights[segment.HighlightId];
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
                    TimeWarpPlanJson = JsonSerializer.Serialize(
                        segment.TimeWarp, JsonOptions),
                    TransitionIn = segment.TransitionIn,
                    TransitionOut = segment.TransitionOut,
                    MatchScore = segment.ScoreBreakdown.Total,
                    ScoreBreakdownJson = JsonSerializer.Serialize(
                        segment.ScoreBreakdown, JsonOptions),
                    WarningsJson = JsonSerializer.Serialize(
                        segment.Warnings, JsonOptions)
                });
            }
            db.GenerationArtifacts.Add(new GenerationArtifact
            {
                GenerationId = generation.Id,
                Type = ArtifactType.MusicEditPlan,
                FileName = "music-edit-plan.json",
                StoredPath = planPath,
                ContentType = "application/json",
                FileSizeBytes = new FileInfo(planPath).Length,
                CreatedAt = timeProvider.GetUtcNow()
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        return new MusicMovieContext(plan, music.StoredPath, settings);
    }

    private async Task<IReadOnlyList<HighlightEffectPlan>> GetOrCreateEffectPlansAsync(
        string publicId,
        GlobalHighlightCandidate[] selected,
        EffectPreset preset,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations
            .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        Dictionary<long, int> tickRates = await db.GenerationDemos
            .Where(value => value.GenerationId == generation.Id)
            .ToDictionaryAsync(
                value => value.Id,
                value => value.TickRate ?? 64,
                cancellationToken);
        GenerationEffectPlan[] existing = await db.GenerationEffectPlans
            .Where(value => value.GenerationId == generation.Id)
            .ToArrayAsync(cancellationToken);
        Dictionary<long, GenerationEffectPlan> existingByHighlight =
            existing.ToDictionary(value => value.GenerationHighlightId);
        List<HighlightEffectPlan> result = new(selected.Length);
        foreach (GlobalHighlightCandidate candidate in selected)
        {
            GenerationHighlight highlight = await db.GenerationHighlights.SingleAsync(
                value =>
                    value.GenerationId == generation.Id &&
                    value.GenerationDemoId == candidate.SourceDemoId &&
                    value.HighlightId == candidate.Highlight.Id,
                cancellationToken);
            if (existingByHighlight.TryGetValue(highlight.Id, out GenerationEffectPlan? stored))
            {
                HighlightEffectPlan? saved =
                    JsonSerializer.Deserialize<HighlightEffectPlan>(
                        stored.EffectPlanJson,
                        JsonOptions);
                if (saved?.SchemaVersion == EffectPlanner.SchemaVersion)
                {
                    result.Add(saved);
                    continue;
                }
                HighlightEffectPlan upgraded = effectPlanner.Build(
                    highlight,
                    tickRates.GetValueOrDefault(candidate.SourceDemoId, 64),
                    stored.Preset);
                stored.TimelineJson =
                    JsonSerializer.Serialize(upgraded.Events, JsonOptions);
                stored.EffectPlanJson =
                    JsonSerializer.Serialize(upgraded, JsonOptions);
                stored.CreatedAt = timeProvider.GetUtcNow();
                result.Add(upgraded);
                continue;
            }

            HighlightEffectPlan plan = effectPlanner.Build(
                highlight,
                tickRates.GetValueOrDefault(candidate.SourceDemoId, 64),
                preset);
            db.GenerationEffectPlans.Add(new GenerationEffectPlan
            {
                GenerationId = generation.Id,
                GenerationHighlightId = highlight.Id,
                Preset = preset,
                TimelineJson = JsonSerializer.Serialize(plan.Events, JsonOptions),
                EffectPlanJson = JsonSerializer.Serialize(plan, JsonOptions),
                CreatedAt = timeProvider.GetUtcNow()
            });
            result.Add(plan);
        }
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<FfmpegCapabilities> GetOrCreateCapabilitiesAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        string directory = storage.EnsureDirectory(publicId, "plan");
        string path = Path.Combine(directory, "ffmpeg-capabilities.json");
        FfmpegCapabilities capabilities;
        if (File.Exists(path))
        {
            capabilities = await ReadJsonAsync<FfmpegCapabilities>(
                path,
                cancellationToken);
        }
        else
        {
            LogCapabilityScan(logger, publicId);
            capabilities = await capabilityScanner.ScanAsync(cancellationToken);
            await capabilityScanner.WriteAsync(
                capabilities,
                path,
                cancellationToken);
        }
        LogCapabilities(
            logger,
            publicId,
            capabilities.Version,
            capabilities.Available,
            capabilities.Filters.Count,
            capabilities.Warnings.Count);
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        long generationId = await db.Generations
            .Where(value => value.PublicId == publicId)
            .Select(value => value.Id)
            .SingleAsync(cancellationToken);
        await AddArtifactAsync(
            db,
            generationId,
            ArtifactType.FfmpegCapabilities,
            path,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return capabilities;
    }

    private async Task<IReadOnlyList<DynamicEffectPlan>>
        GetOrCreateDynamicEffectPlansAsync(
            string publicId,
            GlobalHighlightCandidate[] selected,
            MusicMovieContext musicContext,
            FfmpegCapabilities capabilities,
            CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(
            value => value.PublicId == publicId,
            cancellationToken);
        GenerationEffectPlan[] storedPlans = await db.GenerationEffectPlans
            .Where(value => value.GenerationId == generation.Id)
            .ToArrayAsync(cancellationToken);
        Dictionary<long, GenerationEffectPlan> storedByHighlight =
            storedPlans.ToDictionary(value => value.GenerationHighlightId);
        IReadOnlySet<string> enabledGroups = ParseEnabledGroups(
            musicContext.Settings.EnabledEffectGroupsJson);
        Dictionary<string, MusicEditSegment> editByHighlight =
            musicContext.Plan.Segments.ToDictionary(
                value => value.HighlightId,
                StringComparer.Ordinal);
        CinematicMoviePlan? cinematic = null;
        string expectedPlannerVersion = DynamicEffectPlanner.PlannerVersion;
        if (musicContext.Settings.MovieStyle == MovieStyle.CinematicDirector)
        {
            GenerationCinematicPlan locked =
                await db.GenerationCinematicPlans.AsNoTracking()
                    .SingleAsync(
                        value =>
                            value.GenerationId == generation.Id &&
                            value.LockedAt != null,
                        cancellationToken);
            cinematic = JsonSerializer.Deserialize<CinematicMoviePlan>(
                locked.PlanJson,
                JsonOptions) ??
                throw new InvalidOperationException(
                    "CINEMATIC_LOCKED_PLAN_INVALID");
            expectedPlannerVersion =
                CinematicDynamicEffectAdapter.PlannerVersion;
        }
        List<DynamicEffectPlan> result = new(selected.Length);
        foreach (GlobalHighlightCandidate candidate in selected)
        {
            GenerationHighlight highlight = await db.GenerationHighlights.SingleAsync(
                value =>
                    value.GenerationId == generation.Id &&
                    value.GenerationDemoId == candidate.SourceDemoId &&
                    value.HighlightId == candidate.Highlight.Id,
                cancellationToken);
            if (storedByHighlight.TryGetValue(
                    highlight.Id,
                    out GenerationEffectPlan? stored) &&
                stored.LockedAt is not null &&
                stored.PlannerVersion == expectedPlannerVersion)
            {
                DynamicEffectPlan? locked = JsonSerializer.Deserialize<DynamicEffectPlan>(
                    stored.DynamicEffectPlanJson,
                    JsonOptions);
                if (locked?.PlannerVersion == expectedPlannerVersion)
                {
                    LogLockedPlan(
                        logger,
                        publicId,
                        highlight.HighlightId,
                        locked.DeterministicSeed,
                        locked.Effects.Count);
                    result.Add(locked);
                    continue;
                }
            }
            DynamicEffectPlan plan = cinematic is null
                ? dynamicEffectPlanner.Build(
                    new DynamicEffectPlanningContext
                    {
                        GenerationId = publicId,
                        Highlight = highlight,
                        TickRate = Math.Max(1, highlight.TickRate),
                        Style = generation.EffectPreset == EffectPreset.Clean
                            ? MovieStyle.Clean
                            : musicContext.Settings.MovieStyle,
                        Intensity = musicContext.Settings.EffectIntensity,
                        EditSegment = editByHighlight.GetValueOrDefault(
                            highlight.HighlightId),
                        EnabledGroups = enabledGroups,
                        Capabilities = capabilities
                    })
                : cinematicEffectAdapter.Create(
                    publicId,
                    highlight,
                    cinematic,
                    musicContext.Settings.EffectIntensity);
            LogEffectPlan(
                logger,
                publicId,
                highlight.HighlightId,
                plan.Style,
                plan.Intensity,
                plan.DeterministicSeed,
                plan.Effects.Count,
                plan.RejectedEffects.Count);
            if (!storedByHighlight.TryGetValue(
                    highlight.Id,
                    out GenerationEffectPlan? entity))
            {
                entity = new GenerationEffectPlan
                {
                    GenerationId = generation.Id,
                    GenerationHighlightId = highlight.Id,
                    Preset = generation.EffectPreset,
                    CreatedAt = timeProvider.GetUtcNow()
                };
                db.GenerationEffectPlans.Add(entity);
                storedByHighlight.Add(highlight.Id, entity);
            }
            entity.DynamicEffectPlanJson = JsonSerializer.Serialize(plan, JsonOptions);
            entity.PlannerVersion = plan.PlannerVersion;
            entity.DeterministicSeed = plan.DeterministicSeed;
            entity.LockedAt = musicContext.Settings.LockedAt ??
                timeProvider.GetUtcNow();
            result.Add(plan);
        }
        string directory = storage.EnsureDirectory(publicId, "plan");
        string path = Path.Combine(directory, "dynamic-effect-plan.json");
        string temporary = path + ".tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(new
            {
                schemaVersion = DynamicEffectPlanner.SchemaVersion,
                plannerVersion = expectedPlannerVersion,
                generationId = publicId,
                plans = result
            }, JsonOptions),
            cancellationToken);
        File.Move(temporary, path, true);
        await AddArtifactAsync(
            db,
            generation.Id,
            ArtifactType.DynamicEffectPlan,
            path,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static IReadOnlySet<string> ParseEnabledGroups(string json)
    {
        string[] values = DeserializeJson<string[]>(json, []);
        return values.Length == 0
            ? DynamicEffectGroups.All
            : new HashSet<string>(
                values.Where(DynamicEffectGroups.All.Contains),
                StringComparer.Ordinal);
    }

    private sealed record MusicMovieContext(
        MusicEditPlan Plan,
        string MusicPath,
        GenerationMovieSettings Settings);

    private static int GenerationStageOrder(GenerationStatus status) => status switch
    {
        GenerationStatus.QueuedForGeneration => 0,
        GenerationStatus.PreparingRenderPlan => 1,
        GenerationStatus.SelectingHighlights => 2,
        GenerationStatus.RenderingClips => 3,
        GenerationStatus.RenderingHighlights => 3,
        GenerationStatus.VerifyingClips => 4,
        GenerationStatus.PlanningMusicEdit => 5,
        GenerationStatus.ApplyingTimeWarp => 6,
        GenerationStatus.SynchronizingPeaks => 7,
        GenerationStatus.RenderingCameraPreviews => 8,
        GenerationStatus.ValidatingCameraShots => 9,
        GenerationStatus.RenderingCinematicShots => 10,
        GenerationStatus.ApplyingEffects => 11,
        GenerationStatus.ComposingVideo => 12,
        GenerationStatus.ComposingCinematicTimeline => 12,
        GenerationStatus.MixingAudio => 13,
        GenerationStatus.MixingNarrativeAudio => 13,
        GenerationStatus.ApplyingColorGrade => 14,
        GenerationStatus.ApplyingNarrativeColor => 14,
        GenerationStatus.VerifyingOutput => 15,
        GenerationStatus.VerifyingCinematicMovie => 15,
        _ => 0
    };

    private async Task PersistSelectionAndPlanAsync(
        Generation generation,
        IReadOnlyList<GlobalHighlightCandidate> selected,
        CancellationToken cancellationToken)
    {
        string planDirectory = storage.EnsureDirectory(generation.PublicId, "plan");
        string planPath = Path.Combine(planDirectory, "generation-plan.json");
        if (!File.Exists(planPath))
        {
            var plan = new
            {
                schemaVersion = "1.1",
                generationId = generation.PublicId,
                selectedSteamId = generation.SelectedSteamId,
                price = new { amountMinor = generation.PriceAmountMinor, currency = generation.PriceCurrency },
                settings = new
                {
                    maximumHighlights = generation.MaximumHighlights,
                    aspectRatio = generation.AspectRatio,
                    generation.Width,
                    generation.Height,
                    generation.Fps,
                    order = generation.OutputOrder,
                    generation.EffectPreset,
                    generation.EstimatedDurationMilliseconds
                },
                sourceDemos = generation.Demos.OrderBy(value => value.UploadOrder).Select(value =>
                    new { demoId = value.Id, fileName = value.OriginalFileName, value.Sha256 }),
                selectedHighlights = selected.Select((value, index) => new
                {
                    index = index + 1,
                    sourceDemoId = value.SourceDemoId,
                    highlightId = value.Highlight.Id,
                    type = value.Highlight.Type,
                    score = value.Highlight.Score,
                    combatScore = value.Highlight.CombatScore,
                    beautyScore = value.Highlight.BeautyScore,
                    startTick = value.Highlight.StartTick,
                    endTick = value.Highlight.EndTick,
                    weaponSequence = value.Highlight.WeaponSequence,
                    tags = value.Highlight.Tags
                })
            };
            await File.WriteAllTextAsync(
                planPath, JsonSerializer.Serialize(plan, JsonOptions), cancellationToken);
        }
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation current = await db.Generations
            .Include(value => value.Highlights)
            .SingleAsync(value => value.PublicId == generation.PublicId, cancellationToken);
        Dictionary<string, int> orderById = selected
            .Select((value, index) => (value.Highlight.Id, Order: index + 1))
            .ToDictionary(value => value.Id, value => value.Order, StringComparer.Ordinal);
        foreach (GenerationHighlight highlight in current.Highlights)
        {
            highlight.SelectedForCompilation = orderById.TryGetValue(
                highlight.HighlightId, out int compilationOrder);
            highlight.CompilationOrder = highlight.SelectedForCompilation ? compilationOrder : null;
        }
        await AddArtifactAsync(
            db, current.Id, ArtifactType.GenerationPlan, planPath, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CompleteAsync(
        string publicId,
        int planned,
        int included,
        CompilationResult compilation,
        CompilationResult? cameraOnlyCompilation,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations
            .Include(value => value.Demos)
            .Include(value => value.Highlights)
            .AsSplitQuery()
            .SingleAsync(value => value.PublicId == publicId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        GenerationStatus status = included < planned
            ? GenerationStatus.CompletedWithWarnings
            : GenerationStatus.Completed;
        GenerationArtifact artifact = new()
        {
            GenerationId = generation.Id,
            Type = ArtifactType.FinalVideo,
            FileName = "final-highlights.mp4",
            StoredPath = compilation.OutputFile!,
            ContentType = "video/mp4",
            FileSizeBytes = compilation.FileSizeBytes,
            CreatedAt = now
        };
        db.GenerationArtifacts.Add(artifact);
        if (cameraOnlyCompilation?.OutputFile is not null)
        {
            db.GenerationArtifacts.Add(new GenerationArtifact
            {
                GenerationId = generation.Id,
                Type = ArtifactType.CameraOnlyVideo,
                FileName = "camera-only.mp4",
                StoredPath = cameraOnlyCompilation.OutputFile,
                ContentType = "video/mp4",
                FileSizeBytes = cameraOnlyCompilation.FileSizeBytes,
                CreatedAt = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        string reportPath = Path.Combine(storage.EnsureDirectory(publicId, "output"), "generation-report.json");
        int validDemos = generation.Demos.Count(value =>
            value.AnalysisStatus == DemoAnalysisStatus.Succeeded);
        int skippedDemos = generation.Demos.Count(value =>
            value.AnalysisStatus is DemoAnalysisStatus.Skipped or DemoAnalysisStatus.Failed);
        int cinematicBrollClips =
            await db.GenerationBrollCandidates.CountAsync(
                value =>
                    value.GenerationId == generation.Id &&
                    value.Selected,
                cancellationToken);
        string compilationResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "compilation-result.json");
        string audioMixResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "audio-mix-result.json");
        string musicGainEnvelopePath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "music-gain-envelope.json");
        string gameplayAudioEnvelopePath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "gameplay-audio-envelope.json");
        string alignmentResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "music-alignment-result.json");
        string colorGradeResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "color-grade-result.json");
        string dynamicEffectResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "dynamic-effect-result.json");
        string frameContinuityReportPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "frame-continuity-report.json");
        string demoUiDetectionReportPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "demo-ui-detection-report.json");
        string transitionBoundaryReportPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "transition-boundary-report.json");
        string cinematicAcceptanceReportPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "cinematic-acceptance-report.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.1",
                generationId = publicId,
                paymentId = generation.PaymentId,
                price = new
                {
                    amountMinor = generation.PriceAmountMinor,
                    currency = generation.PriceCurrency
                },
                selectedSteamId = generation.SelectedSteamId,
                selectedPlayerName = generation.SelectedPlayerName,
                uploadedDemos = generation.Demos.Count,
                validDemos,
                skippedDemos,
                totalCandidates = generation.Highlights.Count,
                plannedHighlights = planned,
                renderedHighlights = included,
                cinematicBrollClips,
                renderedSources = compilation.IncludedClips,
                renderedClips = compilation.IncludedClips,
                includedClips = compilation.IncludedClips,
                failedClips = planned - included,
                compilation.DurationMilliseconds,
                compilation.FileSizeBytes,
                finalVideo = compilation.OutputFile,
                generationStartedAt = generation.GenerationStartedAt,
                completedAt = now
            }, JsonOptions),
            cancellationToken);
        GenerationMovieSettings? movieSettings =
            await db.GenerationMovieSettings.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == generation.Id,
                    cancellationToken);
        if (movieSettings?.MovieStyle == MovieStyle.CinematicDirector)
        {
            string planDirectory = storage.EnsureDirectory(publicId, "plan");
            string sourceReusePath = Path.Combine(
                planDirectory,
                "source-interval-reuse-report.json");
            string cameraDiversityPath = Path.Combine(
                planDirectory,
                "camera-shot-diversity-report.json");
            string effectRarityPath = Path.Combine(
                planDirectory,
                "effect-rarity-report.json");
            int? reuseCount = await ReadReportValueAsync<int>(
                sourceReusePath,
                "reuseCount",
                cancellationToken);
            bool? frameValid = await ReadReportValueAsync<bool>(
                frameContinuityReportPath,
                "isValid",
                cancellationToken);
            bool? demoStrip = await ReadReportValueAsync<bool>(
                demoUiDetectionReportPath,
                "demoPlaybackStripDetected",
                cancellationToken);
            bool? musicDucking = await ReadReportValueAsync<bool>(
                audioMixResultPath,
                "musicDucking",
                cancellationToken);
            string[] cameraViolations =
                await ReadReportValueAsync<string[]>(
                    cameraDiversityPath,
                    "violations",
                    cancellationToken) ?? [];
            string[] effectViolations =
                await ReadReportValueAsync<string[]>(
                    effectRarityPath,
                    "violations",
                    cancellationToken) ?? [];
            List<string> limitations = [];
            if (reuseCount is null)
                limitations.Add("SOURCE_INTERVAL_REUSE_NOT_MEASURED");
            if (cameraViolations.Length > 0)
                limitations.AddRange(cameraViolations);
            if (effectViolations.Length > 0)
                limitations.AddRange(effectViolations);
            bool technicalAcceptance =
                reuseCount == 0 &&
                frameValid == true &&
                demoStrip == false &&
                musicDucking == false;
            await File.WriteAllTextAsync(
                cinematicAcceptanceReportPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    generationId = publicId,
                    evaluatedFromRenderedMedia = true,
                    technicalAcceptance,
                    artisticAcceptance = "Requires full manual review",
                    sourceIntervalReuseCount = reuseCount,
                    frameContinuityPassed = frameValid,
                    demoPlaybackStripDetected = demoStrip,
                    musicKillDuckingEnabled = musicDucking,
                    cameraDiversityViolations = cameraViolations,
                    effectRarityViolations = effectViolations,
                    limitations = limitations
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    evidence = new
                    {
                        frameContinuityReportPath,
                        demoUiDetectionReportPath,
                        audioMixResultPath,
                        sourceReusePath,
                        cameraDiversityPath,
                        effectRarityPath
                    }
                }, JsonOptions),
                cancellationToken);
        }
        if (File.Exists(compilationResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.CompilationResult,
                compilationResultPath, cancellationToken);
        if (File.Exists(audioMixResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.AudioMixResult,
                audioMixResultPath, cancellationToken);
        if (File.Exists(musicGainEnvelopePath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.MusicGainEnvelope,
                musicGainEnvelopePath, cancellationToken);
        if (File.Exists(gameplayAudioEnvelopePath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.GameplayAudioEnvelope,
                gameplayAudioEnvelopePath, cancellationToken);
        if (File.Exists(alignmentResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.MusicAlignmentResult,
                alignmentResultPath, cancellationToken);
        if (File.Exists(colorGradeResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.ColorGradeResult,
                colorGradeResultPath, cancellationToken);
        if (File.Exists(dynamicEffectResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.DynamicEffectResult,
                dynamicEffectResultPath, cancellationToken);
        string cinematicContractPlanReportPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "cinematic-contract-plan-report.json");
        string cinematicContractRenderReportPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"),
            "cinematic-contract-render-report.json");
        if (File.Exists(cinematicContractPlanReportPath))
            await AddArtifactAsync(
                db,
                generation.Id,
                ArtifactType.CinematicContractPlanReport,
                cinematicContractPlanReportPath,
                cancellationToken);
        if (File.Exists(cinematicContractRenderReportPath))
            await AddArtifactAsync(
                db,
                generation.Id,
                ArtifactType.CinematicContractRenderReport,
                cinematicContractRenderReportPath,
                cancellationToken);
        if (File.Exists(frameContinuityReportPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.FrameContinuityReport,
                frameContinuityReportPath, cancellationToken);
        if (File.Exists(demoUiDetectionReportPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.DemoUiDetectionReport,
                demoUiDetectionReportPath, cancellationToken);
        if (File.Exists(transitionBoundaryReportPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.TransitionBoundaryReport,
                transitionBoundaryReportPath, cancellationToken);
        if (File.Exists(cinematicAcceptanceReportPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.CinematicAcceptanceReport,
                cinematicAcceptanceReportPath, cancellationToken);
        await AddArtifactAsync(
            db, generation.Id, ArtifactType.GenerationReport, reportPath, cancellationToken);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        if (generation.UserId is null)
            throw new InvalidOperationException("GENERATION_OWNER_MISSING");
        await tokenService.DebitAsync(
            db, generation.UserId, generation.Id, cancellationToken);
        GenerationStateMachine.Transition(generation, status, now);
        generation.ProgressPercent = 100;
        generation.GenerationCompletedAt = now;
        generation.ExpiresAtUtc = now.AddDays(
            Math.Max(1, retentionOptions.CompletedGenerationDays));
        generation.CleanupStatus = CleanupStatus.Pending;
        generation.ProcessingDurationMilliseconds = generation.GenerationStartedAt is null
            ? 0
            : Math.Max(
                0,
                (long)(now - generation.GenerationStartedAt.Value).TotalMilliseconds);
        generation.FinalVideoArtifactId = artifact.Id;
        db.GenerationEvents.Add(new GenerationEvent
        {
            GenerationId = generation.Id,
            Stage = status.ToString(),
            Message = "Completed and token debited",
            ProgressPercent = 100,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.GenerationCompleted.Add(1);
        metrics.GenerationDurationSeconds.Record(
            generation.ProcessingDurationMilliseconds / 1000d);
        await PublishAsync(publicId, status, 100, "Completed", cancellationToken);
    }

    private async Task SetStatusAsync(
        string publicId,
        GenerationStatus status,
        int progress,
        string stage,
        CancellationToken cancellationToken)
    {
        LogGenerationStage(logger, publicId, status, progress, stage, null);
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation.Status != status)
            GenerationStateMachine.Transition(generation, status, timeProvider.GetUtcNow());
        generation.ProgressPercent = Math.Max(generation.ProgressPercent, progress);
        generation.CurrentStage = stage;
        generation.ActiveStageKey = GenerationStageMapping.For(status)
            .FirstOrDefault(value => value.State == GenerationStageState.Current)?.Key;
        generation.UpdatedAt = timeProvider.GetUtcNow();
        db.GenerationEvents.Add(new GenerationEvent
        {
            GenerationId = generation.Id,
            Stage = status.ToString(),
            Message = stage,
            ProgressPercent = generation.ProgressPercent,
            CreatedAt = timeProvider.GetUtcNow()
        });
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(publicId, status, generation.ProgressPercent, stage, cancellationToken);
    }

    private Task PublishAsync(
        string publicId,
        GenerationStatus status,
        int progress,
        string stage,
        CancellationToken cancellationToken) =>
        hub.Clients.Group(publicId).SendAsync(
            "progress",
            new { generationId = publicId, status, progressPercent = progress, stage },
            cancellationToken);

    private async Task FailAsync(
        string publicId,
        string code,
        string message,
        CancellationToken cancellationToken,
        bool refundToken = false)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(value => value.PublicId == publicId, cancellationToken);
        generation.Status = GenerationStatus.Failed;
        generation.ErrorCode = code;
        generation.ErrorMessage = message.Length > 1024 ? message[..1024] : message;
        generation.CurrentStage = "Failed";
        generation.ErrorCategory = code.StartsWith("MUSIC_", StringComparison.Ordinal) ? "UserInput" : "Platform";
        generation.UpdatedAt = timeProvider.GetUtcNow();
        metrics.GenerationFailed.Add(1);
        string outputDirectory = storage.EnsureDirectory(publicId, "output");
        string contractPlanReport = Path.Combine(
            outputDirectory,
            "cinematic-contract-plan-report.json");
        string contractRenderReport = Path.Combine(
            outputDirectory,
            "cinematic-contract-render-report.json");
        if (File.Exists(contractPlanReport))
            await AddArtifactAsync(
                db,
                generation.Id,
                ArtifactType.CinematicContractPlanReport,
                contractPlanReport,
                cancellationToken);
        if (File.Exists(contractRenderReport))
            await AddArtifactAsync(
                db,
                generation.Id,
                ArtifactType.CinematicContractRenderReport,
                contractRenderReport,
                cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (refundToken && generation.UserId is not null)
            await tokenService.RefundAsync(
                generation.UserId,
                generation.Id,
                $"Возврат токена: {code}",
                cancellationToken);
        await PublishAsync(publicId, GenerationStatus.Failed, generation.ProgressPercent, code, cancellationToken);
    }

    private async Task MarkDemoFailedAsync(long demoId, string message, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        GenerationDemo demo = await db.GenerationDemos.SingleAsync(value => value.Id == demoId, cancellationToken);
        demo.AnalysisStatus = DemoAnalysisStatus.Skipped;
        demo.ErrorCode = "INVALID_DEMO";
        demo.ErrorMessage = message.Length > 1024 ? message[..1024] : message;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkCancelledAsync(string publicId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(
            value => value.PublicId == publicId, cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (generation.Status != GenerationStatus.Cancelling)
            GenerationStateMachine.Transition(generation, GenerationStatus.Cancelling, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.Cancelled, now);
        generation.ErrorCode = "GENERATION_CANCELLED";
        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(publicId, GenerationStatus.Cancelled, generation.ProgressPercent, "Cancelled", cancellationToken);
    }

    private static HighlightCandidate ToCandidate(
        GenerationHighlight value,
        string? playerName)
    {
        double total = value.TotalScore != 0 ? value.TotalScore : value.Score;
        ScoreBreakdown breakdown = DeserializeJson(
            value.ScoreBreakdownJson,
            new ScoreBreakdown(total, 0, 0, 0, 0, 0, total)
            {
                CombatScore = value.CombatScore != 0 ? value.CombatScore : total,
                BeautyScore = value.BeautyScore
            });
        KillDescriptor[] kills = DeserializeJson<KillDescriptor[]>(value.KillsJson, []);
        WeaponSequenceSegment[] sequence = DeserializeJson<WeaponSequenceSegment[]>(
            value.WeaponSequenceJson, []);
        string[] tags = DeserializeJson<string[]>(value.TagsJson, []);
        return new HighlightCandidate(
            value.HighlightId,
            Enum.Parse<HighlightType>(value.Type),
            value.SteamId,
            playerName ?? value.SteamId,
            value.RoundNumber,
            value.FirstKillTick,
            value.LastKillTick,
            value.StartTick,
            value.EndTick,
            value.KillCount,
            value.HeadshotCount,
            total,
            breakdown,
            kills.Select(item => item.EventIndex).ToArray(),
            tags)
        {
            MapName = value.MapName,
            CombatScore = value.CombatScore != 0 ? value.CombatScore : total,
            BeautyScore = value.BeautyScore,
            Kills = kills,
            WeaponSequence = sequence,
            TickRate = value.TickRate,
            RoundStartTick = value.RoundStartTick,
            PrimaryKillTick = value.PrimaryKillTick > 0
                ? value.PrimaryKillTick
                : value.LastKillTick,
            SafeEndTick = value.SafeEndTick > 0
                ? value.SafeEndTick
                : value.EndTick,
            EstimatedDurationMilliseconds = value.EstimatedDurationMilliseconds
        };
    }

    private static GlobalHighlightCandidate ToBrollRenderCandidate(
        GenerationBrollCandidate value,
        GenerationDemo demo,
        string playerSteamId,
        string? playerName)
    {
        double score = Math.Clamp(value.CinematicScore, 0, 1) * 100;
        HighlightCandidate candidate = new(
            value.CandidateId,
            HighlightType.SoloKill,
            playerSteamId,
            playerName ?? playerSteamId,
            value.RoundNumber,
            value.StartTick,
            value.StartTick,
            value.StartTick,
            value.EndTick,
            1,
            0,
            score,
            new ScoreBreakdown(
                score,
                0,
                0,
                0,
                0,
                0,
                score),
            [],
            ["CINEMATIC_BROLL"])
        {
            SourceDemoId = demo.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            MapName = demo.MapName ?? string.Empty,
            TickRate = demo.TickRate ?? 64,
            PrimaryKillTick = value.StartTick,
            SafeEndTick = value.EndTick,
            BeautyScore = score,
            Kills = []
        };
        return new GlobalHighlightCandidate(
            demo.Id,
            demo.StoredPath,
            demo.UploadOrder,
            candidate);
    }

    private static Cs2Highlight.RenderAgent.Application.RenderCameraPlan
        BuildRenderCameraPlan(
            CameraShotPlan camera,
            string mapName,
            bool manuallyVerifiedProfile,
            Cs2Highlight.RenderAgent.Application.RenderJob job,
            CinematicCameraRuntimeOptions runtime)
    {
        bool automaticCalibration = camera.AutomaticCalibration &&
            runtime.AutomaticCalibrationEnabled;
        if ((!manuallyVerifiedProfile && !automaticCalibration) ||
            !runtime.Enabled ||
            string.IsNullOrWhiteSpace(runtime.HlaeVersion) ||
            (manuallyVerifiedProfile &&
             string.IsNullOrWhiteSpace(runtime.VerificationId)))
        {
            throw new InvalidOperationException(
                "CINEMATIC_CAMERA_RUNTIME_UNVERIFIED");
        }
        SafeCameraVolume? safeVolume = camera.SafetyVolume;
        if (safeVolume is null)
        {
            throw new InvalidOperationException(
                "CINEMATIC_CAMERA_PATH_OUTSIDE_VERIFIED_VOLUME");
        }
        int tickRate = job.Segment.TickRate ??
            throw new InvalidOperationException(
                "CINEMATIC_CAMERA_TICK_RATE_MISSING");
        Cs2Highlight.RenderAgent.Application.RenderCameraKeyframe[] keyframes =
            camera.Keyframes
                .OrderBy(value => value.TimeSeconds)
                .Select(value =>
                    new Cs2Highlight.RenderAgent.Application
                        .RenderCameraKeyframe(
                            Math.Clamp(
                                camera.StartTick + (long)Math.Round(
                                    value.TimeSeconds * tickRate,
                                    MidpointRounding.AwayFromZero),
                                job.Segment.StartTick,
                                job.Segment.EndTick),
                            new Cs2Highlight.RenderAgent.Application.RenderVector3(
                                value.Position.X,
                                value.Position.Y,
                                value.Position.Z),
                            new Cs2Highlight.RenderAgent.Application.RenderVector3(
                                value.Rotation.X,
                                value.Rotation.Y,
                                value.Rotation.Z),
                            value.Fov))
                .ToArray();
        return new Cs2Highlight.RenderAgent.Application.RenderCameraPlan
        {
            Mode = keyframes.Length == 1
                ? Cs2Highlight.RenderAgent.Application.RenderCameraMode.Static
                : Cs2Highlight.RenderAgent.Application.RenderCameraMode.Campath,
            MapName = mapName,
            Keyframes = keyframes,
            SafeVolume =
                new Cs2Highlight.RenderAgent.Application.RenderCameraBounds(
                    new Cs2Highlight.RenderAgent.Application.RenderVector3(
                        safeVolume.Minimum.X,
                        safeVolume.Minimum.Y,
                        safeVolume.Minimum.Z),
                    new Cs2Highlight.RenderAgent.Application.RenderVector3(
                        safeVolume.Maximum.X,
                        safeVolume.Maximum.Y,
                        safeVolume.Maximum.Z)),
            ManualSpikeVerified = manuallyVerifiedProfile &&
                !automaticCalibration,
            CalibrationSpike = automaticCalibration,
            VerificationId = automaticCalibration
                ? $"auto-{mapName}-{camera.Id}"
                : runtime.VerificationId,
            HlaeVersionPrefix = runtime.HlaeVersion
        };
    }

    private static (CameraShotPlan Shot,
        Cs2Highlight.RenderAgent.Application.RenderCameraPlan RenderPlan)
        SubjectLockedFallback(
            CameraShotPlan source,
            Cs2Highlight.RenderAgent.Application.RenderJob job,
            IReadOnlyList<string> rejectedWarnings,
            string hlaeVersion)
    {
        CameraTargetPoint[] targets = source.TargetPoints
            .OrderBy(value => value.TimeSeconds)
            .ToArray();
        if (targets.Length < 2)
        {
            throw new InvalidOperationException(
                $"CINEMATIC_SUBJECT_ROUTE_TARGETS_MISSING:{source.Id}");
        }
        GameplayVector3 first = targets[0].Position;
        GameplayVector3 last = targets[^1].Position;
        double dx = last.X - first.X;
        double dy = last.Y - first.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double sideX = length > 0.001 ? -dy / length : 0;
        double sideY = length > 0.001 ? dx / length : 1;
        CameraKeyframe[] keyframes = targets.Select(value =>
        {
            GameplayVector3 position = new(
                value.Position.X + sideX * 112,
                value.Position.Y + sideY * 112,
                value.Position.Z + 68);
            return new CameraKeyframe
            {
                TimeSeconds = value.TimeSeconds,
                Position = position,
                Rotation = CameraLookAt(
                    position,
                    new GameplayVector3(
                        value.Position.X,
                        value.Position.Y,
                        value.Position.Z + 48)),
                Fov = 82
            };
        }).ToArray();
        SafeCameraVolume volume = new(
            new GameplayVector3(
                keyframes.Min(value => value.Position.X) - 48,
                keyframes.Min(value => value.Position.Y) - 48,
                keyframes.Min(value => value.Position.Z) - 48),
            new GameplayVector3(
                keyframes.Max(value => value.Position.X) + 48,
                keyframes.Max(value => value.Position.Y) + 48,
                keyframes.Max(value => value.Position.Z) + 48));
        CameraShotPlan shot = CameraShotSignatureBuilder.Attach(
            source with
            {
                Id = source.Id + "-subject-route-fallback",
                Type = CameraShotType.SideTracking,
                Family = CameraShotFamily.SideTracking,
                Keyframes = keyframes,
                FovCurve = keyframes.Select(value =>
                    new CameraFovPoint(value.TimeSeconds, value.Fov)).ToArray(),
                FovStart = 82,
                FovEnd = 82,
                FramingIntent =
                    "automatically calibrated subject-locked side route",
                SafetyVolume = volume,
                AutomaticCalibration = true,
                VerifiedPresetId = null,
                Warnings = source.Warnings
                    .Where(value => value != "CAMERA_PREVIEW_PENDING")
                    .Concat(rejectedWarnings.Select(value =>
                        $"ROUTE_REJECTED:{value}"))
                    .Append("AUTOMATIC_SUBJECT_ROUTE_ALTERNATIVE")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            },
            source.Signature?.MapName ?? string.Empty);
        int tickRate = job.Segment.TickRate ?? 64;
        Cs2Highlight.RenderAgent.Application.RenderCameraKeyframe[] render =
            keyframes.Select(value =>
                new Cs2Highlight.RenderAgent.Application.RenderCameraKeyframe(
                    Math.Clamp(
                        source.StartTick + (long)Math.Round(
                            value.TimeSeconds * tickRate,
                            MidpointRounding.AwayFromZero),
                        job.Segment.StartTick,
                        job.Segment.EndTick),
                    new Cs2Highlight.RenderAgent.Application.RenderVector3(
                        value.Position.X,
                        value.Position.Y,
                        value.Position.Z),
                    new Cs2Highlight.RenderAgent.Application.RenderVector3(
                        value.Rotation.X,
                        value.Rotation.Y,
                        value.Rotation.Z),
                    value.Fov)).ToArray();
        return (
            shot,
            new Cs2Highlight.RenderAgent.Application.RenderCameraPlan
            {
                Mode = Cs2Highlight.RenderAgent.Application
                    .RenderCameraMode.Campath,
                MapName = source.Signature?.MapName ?? string.Empty,
                Keyframes = render,
                SafeVolume = new Cs2Highlight.RenderAgent.Application
                    .RenderCameraBounds(
                        new Cs2Highlight.RenderAgent.Application.RenderVector3(
                            volume.Minimum.X,
                            volume.Minimum.Y,
                            volume.Minimum.Z),
                        new Cs2Highlight.RenderAgent.Application.RenderVector3(
                            volume.Maximum.X,
                            volume.Maximum.Y,
                            volume.Maximum.Z)),
                ManualSpikeVerified = false,
                CalibrationSpike = true,
                VerificationId = "auto-subject-" + source.Id,
                HlaeVersionPrefix = hlaeVersion
            });
    }

    private static GameplayVector3 CameraLookAt(
        GameplayVector3 camera,
        GameplayVector3 target)
    {
        double x = target.X - camera.X;
        double y = target.Y - camera.Y;
        double z = target.Z - camera.Z;
        double horizontal = Math.Sqrt(x * x + y * y);
        return new GameplayVector3(
            -Math.Atan2(z, Math.Max(0.000001, horizontal)) * 180 / Math.PI,
            Math.Atan2(y, x) * 180 / Math.PI,
            0);
    }

    private static CameraShotPlan PovFallback(
        CameraShotPlan source,
        IReadOnlyList<string> warnings) =>
        CameraShotSignatureBuilder.Attach(
            source with
            {
                Id = source.Id + "-pov-fallback",
                Type = CameraShotType.PlayerPov,
                Family = CameraShotFamily.PlayerPov,
                Keyframes = [],
                TargetPoints = [],
                FovCurve =
                [
                    new CameraFovPoint(0, 90),
                    new CameraFovPoint(source.TargetDurationSeconds, 90)
                ],
                FovStart = 90,
                FovEnd = 90,
                FramingIntent = "selected player POV fallback",
                PreviewRequired = false,
                VerifiedPresetId = null,
                FallbackShotId = string.Empty,
                FallbackChain = [],
                Warnings = warnings
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Signature = null
            },
            source.Signature?.MapName ?? string.Empty);

    private async Task<PersistedCameraFallbackReuse>
        ReusePersistedCameraFallbacksAsync(
            long generationId,
            CinematicMoviePlan source,
            Dictionary<string, string> rendered,
            CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        GenerationCameraShot[] rows = await db.GenerationCameraShots
            .AsNoTracking()
            .Where(value =>
                value.GenerationId == generationId &&
                value.PreviewStatus == CameraPreviewStatus.PovFallback &&
                value.PreviewPath != null)
            .ToArrayAsync(cancellationToken);
        Dictionary<string, GenerationCameraShot> byShotId = rows
            .Where(value => File.Exists(value.PreviewPath))
            .ToDictionary(value => value.ShotId, StringComparer.Ordinal);
        if (byShotId.Count == 0)
            return new PersistedCameraFallbackReuse(source, []);
        const string PovFallbackSuffix = "-pov-fallback";
        List<CameraPreviewResult> reused = [];
        CinematicSequenceSegment[] segments = source.Segments
            .Select(segment =>
            {
                string persistedShotId = segment.Camera.Id.EndsWith(
                        PovFallbackSuffix,
                        StringComparison.Ordinal)
                    ? segment.Camera.Id[..^PovFallbackSuffix.Length]
                    : segment.Camera.Id;
                if (!byShotId.TryGetValue(
                        persistedShotId,
                        out GenerationCameraShot? row))
                    return segment;
                string sourceId = segment.HighlightId ??
                    segment.BrollCandidateId ?? segment.Id;
                rendered[sourceId] = row.PreviewPath!;
                CameraShotPlan effective = segment.Camera.Family ==
                        CameraShotFamily.PlayerPov
                    ? segment.Camera with
                    {
                        Warnings = segment.Camera.Warnings.Concat(
                        [
                            "PERSISTED_POV_FALLBACK_REUSED",
                            "LOCKED_CAMERA_CANDIDATE_REJECTED_BY_PREVIEW"
                        ]).Distinct(StringComparer.Ordinal).ToArray()
                    }
                    : PovFallback(
                        segment.Camera,
                        [
                            "PERSISTED_POV_FALLBACK_REUSED",
                            "LOCKED_CAMERA_CANDIDATE_REJECTED_BY_PREVIEW"
                        ]);
                reused.Add(new CameraPreviewResult
                {
                    CameraShotId = segment.Camera.Id,
                    Status = CameraPreviewStatus.PovFallback,
                    PreviewPath = row.PreviewPath,
                    Metrics = null,
                    EffectiveShot = effective,
                    Attempt = Math.Max(1, row.PreviewAttempts),
                    Warnings = effective.Warnings
                });
                return segment with { Camera = effective };
            })
            .ToArray();
        return new PersistedCameraFallbackReuse(
            source with
            {
                Segments = segments,
                CameraDiversity = ShotDiversityPolicy.AnalyzeFilm(
                    segments.Select(value => value.Camera).ToArray(),
                    source.TargetDurationSeconds)
            },
            reused);
    }

    private async Task PersistCameraPreviewArtifactsAsync(
        long generationId,
        string publicId,
        CinematicMoviePlan effectivePlan,
        IReadOnlyList<CameraPreviewResult> previews,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        GenerationCameraShot[] rows = await db.GenerationCameraShots
            .Where(value => value.GenerationId == generationId)
            .ToArrayAsync(cancellationToken);
        foreach (CameraPreviewResult preview in previews)
        {
            GenerationCameraShot? row = rows.SingleOrDefault(value =>
                value.ShotId == preview.CameraShotId);
            if (row is null)
                continue;
            row.PreviewStatus = preview.Status;
            row.PreviewAttempts = preview.Attempt;
            row.PreviewPath = preview.PreviewPath;
        }
        string directory = storage.EnsureDirectory(publicId, "plan");
        GenerationCinematicPlan? storedPlan =
            await db.GenerationCinematicPlans.SingleOrDefaultAsync(
                value => value.GenerationId == generationId,
                cancellationToken);
        if (storedPlan is not null)
        {
            storedPlan.PlanJson = JsonSerializer.Serialize(
                effectivePlan,
                JsonOptions);
            string planPath = Path.Combine(
                directory,
                "cinematic-movie-plan.json");
            string temporaryPlanPath = planPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPlanPath,
                storedPlan.PlanJson,
                cancellationToken);
            File.Move(temporaryPlanPath, planPath, true);
            await AddArtifactAsync(
                db,
                generationId,
                ArtifactType.CinematicMoviePlan,
                planPath,
                cancellationToken);
        }
        Dictionary<string, (ArtifactType Type, object Content)> artifacts =
            new(StringComparer.Ordinal)
            {
                ["camera-shot-candidates.json"] = (
                    ArtifactType.CameraShotCandidates,
                    new
                    {
                        schemaVersion = "2.0",
                        candidates = effectivePlan.Segments.Select(value =>
                            new
                            {
                                value.Id,
                                value.HighlightId,
                                value.BrollCandidateId,
                                value.Camera
                            }).ToArray()
                    }),
                ["camera-shot-selection-report.json"] = (
                    ArtifactType.CameraShotSelectionReport,
                    new
                    {
                        schemaVersion = "1.0",
                        previewValidationRequired = true,
                        selections = previews.Select(value => new
                        {
                            value.CameraShotId,
                            value.Status,
                            effectiveFamily = value.EffectiveShot.Family,
                            effectiveSignature = value.EffectiveShot.Signature,
                            value.Attempt,
                            value.Warnings
                        }).ToArray()
                    }),
                ["camera-shot-diversity-report.json"] = (
                    ArtifactType.CameraShotDiversityReport,
                    effectivePlan.CameraDiversity ??
                    ShotDiversityPolicy.AnalyzeFilm(
                        effectivePlan.Segments.Select(value =>
                            value.Camera).ToArray(),
                        effectivePlan.TargetDurationSeconds)),
                ["camera-preview-quality-report.json"] = (
                    ArtifactType.CameraPreviewQualityReport,
                    new
                    {
                        schemaVersion = "1.0",
                        previews
                    })
            };
        if (effectivePlan.EffectRarity is not null)
        {
            artifacts["effect-rarity-report.json"] = (
                ArtifactType.EffectRarityReport,
                effectivePlan.EffectRarity);
        }
        foreach ((string fileName, (ArtifactType type, object content)) in
                 artifacts)
        {
            string path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(content, JsonOptions),
                cancellationToken);
            await AddArtifactAsync(
                db,
                generationId,
                type,
                path,
                cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<T?> ReadReportValueAsync<T>(
        string path,
        string property,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return default;
        await using FileStream stream = File.OpenRead(path);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(
                property,
                out JsonElement value))
            return default;
        return value.Deserialize<T>(JsonOptions);
    }

    private static T DeserializeJson<T>(string json, T fallback)
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

    private sealed record PersistedCameraFallbackReuse(
        CinematicMoviePlan Plan,
        IReadOnlyList<CameraPreviewResult> Results);

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken) ??
            throw new InvalidDataException($"JSON is empty: {path}");
    }

    private async Task AddArtifactAsync(
        GenerationDbContext db,
        long generationId,
        ArtifactType type,
        string path,
        CancellationToken cancellationToken)
    {
        string full = Path.GetFullPath(path);
        storage.EnsureWithinRoot(full);
        GenerationArtifact? artifact =
            db.GenerationArtifacts.Local.FirstOrDefault(value =>
                value.GenerationId == generationId &&
                value.StoredPath == full) ??
            await db.GenerationArtifacts.FirstOrDefaultAsync(
                value => value.GenerationId == generationId &&
                    value.StoredPath == full,
                cancellationToken);
        if (artifact is null)
        {
            artifact = new GenerationArtifact
            {
                GenerationId = generationId,
                StoredPath = full,
                CreatedAt = timeProvider.GetUtcNow()
            };
            db.GenerationArtifacts.Add(artifact);
        }
        artifact.Type = type;
        artifact.FileName = Path.GetFileName(full);
        artifact.FileSizeBytes = new FileInfo(full).Length;
        artifact.ContentType = type is ArtifactType.FinalVideo or
            ArtifactType.CameraOnlyVideo
            ? "video/mp4"
            : "application/json";
    }

    private sealed class PlayerAggregate(string initialName)
    {
        public HashSet<string> Names { get; } = [initialName];
        public HashSet<long> DemoIds { get; } = [];
        public int Kills { get; set; }
    }
}
