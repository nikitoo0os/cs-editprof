using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed class GenerationWorker(
    IDbContextFactory<GenerationDbContext> dbFactory,
    GenerationWakeSignal queue,
    GenerationCancellationRegistry cancellations,
    GenerationStorage storage,
    PipelineOptions pipelineOptions,
    GlobalHighlightSelector globalSelector,
    IMusicAnalyzerClient musicAnalyzer,
    IMusicalAnchorBuilder musicalAnchorBuilder,
    IMusicEditPlanner musicEditPlanner,
    IEffectPlanner effectPlanner,
    IHighlightCompilationService compilationService,
    IHubContext<GenerationHub> hub,
    TimeProvider timeProvider,
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
                value.Status == GenerationStatus.QueuedForGeneration ||
                value.Status == GenerationStatus.PreparingRenderPlan ||
                value.Status == GenerationStatus.SelectingHighlights ||
                value.Status == GenerationStatus.RenderingClips ||
                value.Status == GenerationStatus.VerifyingClips ||
                value.Status == GenerationStatus.PlanningMusicEdit ||
                value.Status == GenerationStatus.ApplyingTimeWarp ||
                value.Status == GenerationStatus.ApplyingEffects ||
                value.Status == GenerationStatus.ComposingVideo ||
                value.Status == GenerationStatus.MixingAudio ||
                value.Status == GenerationStatus.ApplyingColorGrade ||
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
            else if (generation.Status == GenerationStatus.AnalyzingMusic)
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
            exception.Message.Contains("LUT_", StringComparison.Ordinal))
        {
            string code = exception.Message.Split(':', 2)[0].Trim();
            LogGenerationFailure(logger, generation.PublicId, exception);
            await FailAsync(
                generation.PublicId,
                code,
                exception.Message,
                cancellationToken);
        }
        catch (Exception exception)
        {
            LogGenerationFailure(logger, generation.PublicId, exception);
            await FailAsync(generation.PublicId, "UNEXPECTED_ERROR", exception.Message, cancellationToken);
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
        string analysisDirectory =
            storage.EnsureDirectory(publicId, "analysis", "music");
        string logDirectory = storage.EnsureDirectory(publicId, "logs");
        string analysisPath = Path.Combine(analysisDirectory, "music-analysis.json");
        MusicAnalysis analysis;
        if (File.Exists(analysisPath))
        {
            analysis = await ReadJsonAsync<MusicAnalysis>(
                analysisPath, cancellationToken);
        }
        else
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
                .AsSplitQuery()
                .SingleAsync(value => value.PublicId == publicId, cancellationToken);
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
        await PersistSelectionAndPlanAsync(snapshot, selected, cancellationToken);
        if (snapshot.Status is GenerationStatus.QueuedForGeneration or
            GenerationStatus.PreparingRenderPlan or
            GenerationStatus.SelectingHighlights or
            GenerationStatus.RenderingClips)
        {
            await SetStatusAsync(
                publicId, GenerationStatus.RenderingClips, 40,
                $"Rendering 0/{selected.Count} clips", cancellationToken);
        }
        Dictionary<string, string> rendered = new(StringComparer.Ordinal);
        int renderedCount = 0;
        foreach (IGrouping<long, GlobalHighlightCandidate> demoGroup in selected.GroupBy(value => value.SourceDemoId))
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
                    await store.SaveAsync(item.RenderJobPath, build.RenderJobs[item.ItemId], cancellationToken);
                }
            }
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
                publicId, GenerationStatus.RenderingClips,
                40 + (int)(45d * renderedCount / selected.Count),
                $"Rendered {renderedCount}/{selected.Count} clips",
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
        int resumeStage = GenerationStageOrder(snapshot.Status);
        if (musicContext is not null)
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
            await GetOrCreateEffectPlansAsync(
                publicId,
                renderedSelection,
                snapshot.EffectPreset,
                cancellationToken);
        if (musicContext is not null)
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
        CompilationResult compilation = await compilationService.ComposeAsync(
            new CompilationRequest(
                clips,
                output,
                snapshot.Width,
                snapshot.Height,
                snapshot.Fps,
                EffectPlans: effectPlans,
                MusicEditPlan: musicContext?.Plan,
                MusicPath: musicContext?.MusicPath,
                MovieSettings: musicContext?.Settings),
            progress,
            cancellationToken);
        if (!compilation.Success || compilation.OutputFile is null)
        {
            await FailAsync(
                publicId, "COMPILATION_FAILED",
                compilation.Error ?? "Compilation failed.", cancellationToken);
            return;
        }
        if (musicContext is null &&
            snapshot.Status != GenerationStatus.VerifyingOutput)
            await SetStatusAsync(
                publicId, GenerationStatus.ComposingVideo, 97,
                "Final composition completed", cancellationToken);
        await SetStatusAsync(
            publicId, GenerationStatus.VerifyingOutput, 98,
            "Verifying output", cancellationToken);
        await CompleteAsync(publicId, selected.Count, clips.Length, compilation, cancellationToken);
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
                result.Add(saved ?? effectPlanner.Build(
                    highlight,
                    tickRates.GetValueOrDefault(candidate.SourceDemoId, 64),
                    stored.Preset));
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
        GenerationStatus.VerifyingClips => 4,
        GenerationStatus.PlanningMusicEdit => 5,
        GenerationStatus.ApplyingTimeWarp => 6,
        GenerationStatus.ApplyingEffects => 7,
        GenerationStatus.ComposingVideo => 8,
        GenerationStatus.MixingAudio => 9,
        GenerationStatus.ApplyingColorGrade => 10,
        GenerationStatus.VerifyingOutput => 11,
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
        GenerationStateMachine.Transition(generation, status, now);
        generation.ProgressPercent = 100;
        generation.GenerationCompletedAt = now;
        db.GenerationEvents.Add(new GenerationEvent
        {
            GenerationId = generation.Id,
            Stage = status.ToString(),
            Message = "Completed",
            ProgressPercent = 100,
            CreatedAt = now
        });
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
        await db.SaveChangesAsync(cancellationToken);
        generation.FinalVideoArtifactId = artifact.Id;
        string reportPath = Path.Combine(storage.EnsureDirectory(publicId, "output"), "generation-report.json");
        int validDemos = generation.Demos.Count(value =>
            value.AnalysisStatus == DemoAnalysisStatus.Succeeded);
        int skippedDemos = generation.Demos.Count(value =>
            value.AnalysisStatus is DemoAnalysisStatus.Skipped or DemoAnalysisStatus.Failed);
        string compilationResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "compilation-result.json");
        string audioMixResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "audio-mix-result.json");
        string alignmentResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "music-alignment-result.json");
        string colorGradeResultPath = Path.Combine(
            storage.EnsureDirectory(publicId, "output"), "color-grade-result.json");
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.1",
                generationId = publicId,
                paymentId = generation.PaymentId,
                price = new { amountMinor = 100, currency = "USD" },
                selectedSteamId = generation.SelectedSteamId,
                selectedPlayerName = generation.SelectedPlayerName,
                uploadedDemos = generation.Demos.Count,
                validDemos,
                skippedDemos,
                totalCandidates = generation.Highlights.Count,
                plannedHighlights = planned,
                renderedClips = included,
                includedClips = included,
                failedClips = planned - included,
                compilation.DurationMilliseconds,
                compilation.FileSizeBytes,
                finalVideo = compilation.OutputFile,
                generationStartedAt = generation.GenerationStartedAt,
                completedAt = now
            }, JsonOptions),
            cancellationToken);
        if (File.Exists(compilationResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.CompilationResult,
                compilationResultPath, cancellationToken);
        if (File.Exists(audioMixResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.AudioMixResult,
                audioMixResultPath, cancellationToken);
        if (File.Exists(alignmentResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.MusicAlignmentResult,
                alignmentResultPath, cancellationToken);
        if (File.Exists(colorGradeResultPath))
            await AddArtifactAsync(
                db, generation.Id, ArtifactType.ColorGradeResult,
                colorGradeResultPath, cancellationToken);
        await AddArtifactAsync(
            db, generation.Id, ArtifactType.GenerationReport, reportPath, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(value => value.PublicId == publicId, cancellationToken);
        generation.Status = GenerationStatus.Failed;
        generation.ErrorCode = code;
        generation.ErrorMessage = message.Length > 1024 ? message[..1024] : message;
        generation.CurrentStage = "Failed";
        generation.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
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
        if (db.GenerationArtifacts.Local.Any(value => value.StoredPath == full) ||
            await db.GenerationArtifacts.AnyAsync(
                value => value.StoredPath == full, cancellationToken)) return;
        db.GenerationArtifacts.Add(new GenerationArtifact
        {
            GenerationId = generationId,
            Type = type,
            FileName = Path.GetFileName(full),
            StoredPath = full,
            FileSizeBytes = new FileInfo(full).Length,
            ContentType = type == ArtifactType.FinalVideo ? "video/mp4" : "application/json",
            CreatedAt = timeProvider.GetUtcNow()
        });
    }

    private sealed class PlayerAggregate(string initialName)
    {
        public HashSet<string> Names { get; } = [initialName];
        public HashSet<long> DemoIds { get; } = [];
        public int Kills { get; set; }
    }
}
