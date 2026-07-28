using System.Text.Json;
using Cs2Highlight.Analysis;
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
                value.Status == GenerationStatus.QueuedForGeneration ||
                value.Status == GenerationStatus.PreparingRenderPlan ||
                value.Status == GenerationStatus.SelectingHighlights ||
                value.Status == GenerationStatus.RenderingClips ||
                value.Status == GenerationStatus.ApplyingEffects ||
                value.Status == GenerationStatus.ComposingVideo ||
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
            else
                await GenerateAsync(generation.PublicId, generationCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await MarkCancelledAsync(generation.PublicId, CancellationToken.None);
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

    private async Task AnalyzeAsync(string publicId, CancellationToken cancellationToken)
    {
        await SetStatusAsync(publicId, GenerationStatus.Analyzing, 12, "Analyzing demos", cancellationToken);
        List<GenerationDemo> demos;
        await using (GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken))
            demos = await db.GenerationDemos.Where(value => value.Generation.PublicId == publicId)
                .OrderBy(value => value.UploadOrder).ToListAsync(cancellationToken);
        int succeeded = 0;
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
                    new GoCliDemoParser(Path.GetFullPath(pipelineOptions.DemoParserPath), TimeSpan.FromMinutes(10)),
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
            await FailAsync(publicId, "ALL_DEMOS_INVALID", "No demo was analyzed successfully.", cancellationToken);
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
                new ProcessRenderAgentClient(Path.GetFullPath(pipelineOptions.RenderAgentPath)),
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
        if (snapshot.Status is not (
            GenerationStatus.ComposingVideo or
            GenerationStatus.VerifyingOutput))
            await SetStatusAsync(
                publicId, GenerationStatus.ApplyingEffects, 85,
                "Applying effects and normalizing clips", cancellationToken);
        GenerationStatus compilationStatus = snapshot.Status switch
        {
            GenerationStatus.ComposingVideo => GenerationStatus.ComposingVideo,
            GenerationStatus.VerifyingOutput => GenerationStatus.VerifyingOutput,
            _ => GenerationStatus.ApplyingEffects
        };
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
        CompilationResult compilation = await compilationService.ComposeAsync(
            new CompilationRequest(
                clips,
                output,
                snapshot.Width,
                snapshot.Height,
                snapshot.Fps,
                EffectPlans: effectPlans),
            progress,
            cancellationToken);
        if (!compilation.Success || compilation.OutputFile is null)
        {
            await FailAsync(
                publicId, "COMPILATION_FAILED",
                compilation.Error ?? "Compilation failed.", cancellationToken);
            return;
        }
        if (snapshot.Status != GenerationStatus.VerifyingOutput)
            await SetStatusAsync(
                publicId, GenerationStatus.ComposingVideo, 97,
                "Final composition completed", cancellationToken);
        await SetStatusAsync(
            publicId, GenerationStatus.VerifyingOutput, 98,
            "Verifying output", cancellationToken);
        await CompleteAsync(publicId, selected.Count, clips.Length, compilation, cancellationToken);
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
        long[] demoIds = selected.Select(value => value.SourceDemoId).Distinct().ToArray();
        Dictionary<long, int> tickRates = await db.GenerationDemos
            .Where(value => value.GenerationId == generation.Id && demoIds.Contains(value.Id))
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
