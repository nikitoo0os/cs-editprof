using System.Globalization;
using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed record CinematicLockedPlan(
    MusicNarrative Narrative,
    CinematicMoviePlan Plan,
    CinematicAlignmentReport Alignment,
    IReadOnlyList<BrollCandidate> BrollCandidates);

public interface ICinematicPlanService
{
    Task<CinematicLockedPlan> CreateAndLockAsync(
        GenerationDbContext db,
        Generation generation,
        GenerationMusic music,
        GenerationMovieSettings settings,
        MusicAnalysis musicAnalysis,
        IReadOnlyList<SelectedHighlight> highlights,
        CancellationToken cancellationToken);
}

public sealed partial class CinematicPlanService(
    IMusicNarrativeAnalyzer narrativeAnalyzer,
    IMusicExcerptSelector excerptSelector,
    IBrollCandidateDetector brollDetector,
    ICinematicDirector director,
    IMapCameraProfileCatalog mapProfiles,
    ICinematicDurationPolicy durationPolicy,
    GenerationStorage storage,
    CinematicCameraRuntimeOptions cameraRuntime,
    TimeProvider timeProvider,
    ILogger<CinematicPlanService> logger) : ICinematicPlanService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

    [LoggerMessage(
        EventId = 8101,
        Level = LogLevel.Information,
        Message = "[Generation:{GenerationId}] Stage 8 music narrative: sections={Sections}, peaks={Peaks}, frames={Frames}")]
    private static partial void LogNarrative(
        ILogger logger,
        string generationId,
        int sections,
        int peaks,
        int frames);

    [LoggerMessage(
        EventId = 8102,
        Level = LogLevel.Information,
        Message = "[Generation:{GenerationId}] Stage 8 excerpt: {Start:F2}-{End:F2}s, usablePeaks={UsablePeaks}/{RequiredPeaks}, score={Score:F3}")]
    private static partial void LogExcerpt(
        ILogger logger,
        string generationId,
        double start,
        double end,
        int usablePeaks,
        int requiredPeaks,
        double score);

    [LoggerMessage(
        EventId = 8103,
        Level = LogLevel.Information,
        Message = "[Generation:{GenerationId}] Stage 8 gameplay timeline: frames={Frames}, B-roll candidates={Candidates}")]
    private static partial void LogBroll(
        ILogger logger,
        string generationId,
        int frames,
        int candidates);

    [LoggerMessage(
        EventId = 8104,
        Level = LogLevel.Information,
        Message = "[Generation:{GenerationId}] Stage 8 plan locked: segments={Segments}, matches={Matches}, cinematicCameras={CinematicCameras}, POV={PovShots}, warnings={Warnings}")]
    private static partial void LogPlan(
        ILogger logger,
        string generationId,
        int segments,
        int matches,
        int cinematicCameras,
        int povShots,
        int warnings);

    [LoggerMessage(
        EventId = 8105,
        Level = LogLevel.Warning,
        Message = "[Generation:{GenerationId}] Stage 8 music has insufficient high-energy peaks; using strong peaks from regular sections")]
    private static partial void LogRelaxedEnergyFallback(
        ILogger logger,
        string generationId);

    [LoggerMessage(
        EventId = 8106,
        Level = LogLevel.Error,
        Message = "[Generation:{GenerationId}] Stage 8 incomplete highlight plan: matches={Matches}/{Highlights}, warnings={Warnings}")]
    private static partial void LogIncompleteHighlightPlan(
        ILogger logger,
        string generationId,
        int matches,
        int highlights,
        string warnings);

    public async Task<CinematicLockedPlan> CreateAndLockAsync(
        GenerationDbContext db,
        Generation generation,
        GenerationMusic music,
        GenerationMovieSettings settings,
        MusicAnalysis musicAnalysis,
        IReadOnlyList<SelectedHighlight> highlights,
        CancellationToken cancellationToken)
    {
        GenerationCinematicPlan? existing =
            await db.GenerationCinematicPlans.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == generation.Id &&
                        value.LockedAt != null,
                    cancellationToken);
        if (existing is not null)
        {
            CinematicMoviePlan locked = Deserialize<CinematicMoviePlan>(
                existing.PlanJson,
                "CINEMATIC_LOCKED_PLAN_INVALID");
            MusicNarrative recoveredNarrative = narrativeAnalyzer.Analyze(musicAnalysis);
            return new CinematicLockedPlan(
                recoveredNarrative,
                locked,
                CinematicAlignmentReportBuilder.FromPlan(
                    locked,
                    recoveredNarrative.Sections),
                []);
        }
        if (highlights.Count == 0)
            throw new InvalidOperationException("CINEMATIC_HIGHLIGHTS_REQUIRED");
        if (musicAnalysis.SchemaVersion != "2.0" ||
            musicAnalysis.Frames.Count == 0 ||
            musicAnalysis.FrameHopSeconds is < 0.02 or > 0.05)
        {
            throw new InvalidOperationException(
                "CINEMATIC_MUSIC_REANALYSIS_REQUIRED");
        }

        Advance(
            db,
            generation,
            GenerationStatus.SelectingMusicExcerpt,
            31,
            "Selecting a contiguous music excerpt");
        MusicNarrative narrative = narrativeAnalyzer.Analyze(musicAnalysis);
        LogNarrative(
            logger,
            generation.PublicId,
            narrative.Sections.Count,
            narrative.Peaks.Count,
            narrative.Frames.Count);

        MovieDurationOptions durationOptions = new()
        {
            Selection = settings.CinematicDuration
        };
        MovieDurationBudget budget = durationPolicy.Calculate(
            highlights,
            durationOptions);
        MusicExcerptPlan excerpt = excerptSelector.Select(
            narrative,
            highlights,
            durationOptions);
        LogExcerpt(
            logger,
            generation.PublicId,
            excerpt.StartSeconds,
            excerpt.EndSeconds,
            excerpt.UsablePeakCount,
            excerpt.RequiredPeakCount,
            excerpt.Score);
        if (excerpt.Warnings.Contains(
                MusicExcerptSelector.RelaxedEnergyFallbackWarning,
                StringComparer.Ordinal))
        {
            LogRelaxedEnergyFallback(logger, generation.PublicId);
        }
        if (!excerpt.IsValid)
            throw new InvalidOperationException(
                excerpt.UsablePeakCount < excerpt.RequiredPeakCount
                    ? "CINEMATIC_INSUFFICIENT_HIGH_ENERGY_PEAKS"
                    : "CINEMATIC_MUSIC_EXCERPT_UNAVAILABLE");

        Advance(
            db,
            generation,
            GenerationStatus.AnalyzingGameplayTimeline,
            32,
            "Loading selected-player movement timelines");
        GenerationDemo[] demos = await db.GenerationDemos.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                value.AnalysisStatus == DemoAnalysisStatus.Succeeded)
            .OrderBy(value => value.UploadOrder)
            .ToArrayAsync(cancellationToken);
        GenerationHighlight[] selectedRows =
            await db.GenerationHighlights.AsNoTracking()
                .Where(value =>
                    value.GenerationId == generation.Id &&
                    value.SelectedByUser)
                .ToArrayAsync(cancellationToken);
        Advance(
            db,
            generation,
            GenerationStatus.DetectingBroll,
            33,
            "Detecting safe gameplay B-roll candidates");
        List<BrollCandidate> broll = [];
        int frameCount = 0;
        foreach (GenerationDemo demo in demos)
        {
            string path = Path.Combine(
                storage.GenerationRoot(generation.PublicId),
                "analysis",
                $"demo-{demo.UploadOrder:D3}",
                "demo-analysis.json");
            if (!File.Exists(path))
                continue;
            DemoAnalysis analysis = await ReadJsonAsync<DemoAnalysis>(
                path,
                cancellationToken);
            frameCount += analysis.Timeline.Count;
            GameplayInterval[] excluded = selectedRows
                .Where(value => value.GenerationDemoId == demo.Id)
                .Select(value => new GameplayInterval(
                    value.StartTick,
                    value.EndTick))
                .ToArray();
            broll.AddRange(brollDetector.Detect(new BrollDetectionContext
            {
                DemoId = demo.Id.ToString(CultureInfo.InvariantCulture),
                PlayerId = generation.SelectedSteamId ??
                    throw new InvalidOperationException(
                        "CINEMATIC_PLAYER_REQUIRED"),
                TickRate = demo.TickRate ?? analysis.Demo.TickRate,
                Frames = analysis.Timeline,
                ExcludedIntervals = excluded
            }));
        }
        BrollCandidate[] uniqueBroll = broll
            .GroupBy(value => value.Id, StringComparer.Ordinal)
            .Select(value => value.First())
            .OrderByDescending(value => value.CinematicScore)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        LogBroll(
            logger,
            generation.PublicId,
            frameCount,
            uniqueBroll.Length);
        if (budget.MaximumBrollSeconds >= 0.5 &&
            uniqueBroll.Sum(value => value.DurationSeconds) < 0.5)
        {
            throw new InvalidOperationException(
                frameCount == 0
                    ? "CINEMATIC_GAMEPLAY_TIMELINE_UNAVAILABLE_REANALYZE_DEMOS"
                    : "CINEMATIC_BROLL_INSUFFICIENT");
        }

        Advance(
            db,
            generation,
            GenerationStatus.PlanningNarrative,
            34,
            "Building the global cinematic narrative");
        string mapName = selectedRows
            .Select(value => value.MapName)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ??
            "unknown";
        MapCameraProfile? profile = settings.AutomaticCinematicCameras
            ? mapProfiles.Find(mapName)
            : null;
        HlaeCameraCapabilities capabilities = new()
        {
            Available = cameraRuntime.Enabled &&
                cameraRuntime.VerifiedMaps.Contains(
                    mapName,
                    StringComparer.OrdinalIgnoreCase),
            Version = cameraRuntime.HlaeVersion,
            SupportsCampath = cameraRuntime.Enabled,
            SupportsInput = cameraRuntime.Enabled,
            SupportsFov = cameraRuntime.Enabled,
            SupportsHighFpsCapture = cameraRuntime.Enabled,
            ManualSpikeVerified = cameraRuntime.Enabled &&
                !string.IsNullOrWhiteSpace(cameraRuntime.VerificationId),
            Warnings =
            [
                settings.AutomaticCinematicCameras &&
                cameraRuntime.Enabled
                    ? $"HLAE_CAMERA_VERIFIED:{cameraRuntime.VerificationId}"
                    : "AUTOMATIC_CINEMATIC_CAMERAS_DISABLED"
            ]
        };
        Advance(
            db,
            generation,
            GenerationStatus.PlanningCameraShots,
            35,
            "Planning fail-closed camera shots and POV fallbacks");
        CinematicMoviePlan plan = director.Create(
            narrative,
            excerpt,
            highlights,
            uniqueBroll,
            new CinematicDirectorOptions
            {
                GenerationId = generation.PublicId,
                MapName = mapName,
                Duration = durationOptions,
                Camera = new CameraPlanningContext
                {
                    MapName = mapName,
                    Profile = profile,
                    Capabilities = capabilities
                },
                TimeWarp = TimeWarpFor(
                    settings.CinematicEditIntensity),
                Effects = EffectsFor(
                    settings.CinematicEditIntensity,
                    settings.EffectIntensity),
                ColorGrade = settings.ColorGradePreset
            });
        if (plan.HighlightMatches.Count != highlights.Count)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                string planWarnings = string.Join(", ", plan.Warnings);
                LogIncompleteHighlightPlan(
                    logger,
                    generation.PublicId,
                    plan.HighlightMatches.Count,
                    highlights.Count,
                    planWarnings);
            }
            throw new InvalidOperationException(
                "CINEMATIC_INSUFFICIENT_HIGH_ENERGY_PEAKS");
        }
        CinematicSequenceSegment[] orderedSegments = plan.Segments
            .OrderBy(value => value.OutputStartSeconds)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        bool discontinuous = plan.Warnings.Any(value =>
                value.StartsWith(
                    "CINEMATIC_TIMELINE_GAP:",
                    StringComparison.Ordinal)) ||
            orderedSegments.Length == 0 ||
            orderedSegments[0].OutputStartSeconds > 0.05 ||
            orderedSegments.Zip(orderedSegments.Skip(1)).Any(pair =>
                Math.Abs(
                    pair.First.OutputEndSeconds -
                    pair.Second.OutputStartSeconds) > 0.05);
        if (discontinuous)
            throw new InvalidOperationException(
                "CINEMATIC_BROLL_INSUFFICIENT_FOR_CONTIGUOUS_TIMELINE");
        CinematicAlignmentReport alignment =
            CinematicAlignmentReportBuilder.FromPlan(
                plan,
                narrative.Sections);
        if (alignment.KillsOutsideHighEnergySections != 0)
            throw new InvalidOperationException(
                "PRIMARY_KILL_OUTSIDE_HIGH_ENERGY_SECTION");
        if (plan.TargetDurationSeconds > budget.MaximumTotalSeconds + 0.001)
            throw new InvalidOperationException(
                "CINEMATIC_DURATION_LIMIT_EXCEEDED");

        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (MusicSection section in narrative.Sections)
        {
            db.GenerationMusicSections.Add(new GenerationMusicSection
            {
                GenerationId = generation.Id,
                SectionId = section.Id,
                Type = section.Type,
                StartMilliseconds =
                    (long)Math.Round(section.StartSeconds * 1000),
                EndMilliseconds =
                    (long)Math.Round(section.EndSeconds * 1000),
                Energy = section.Energy,
                RhythmicDensity = section.RhythmicDensity,
                BassEnergy = section.BassEnergy,
                Confidence = section.Confidence
            });
        }
        HashSet<string> selectedBrollIds = plan.Segments
            .Where(value => value.BrollCandidateId is not null)
            .Select(value => value.BrollCandidateId!)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, GenerationBrollCandidate> storedBroll =
            new(StringComparer.Ordinal);
        foreach (BrollCandidate candidate in uniqueBroll)
        {
            CinematicSequenceSegment? selectedSegment =
                plan.Segments.SingleOrDefault(value =>
                    string.Equals(
                        value.BrollCandidateId,
                        candidate.Id,
                        StringComparison.Ordinal));
            GenerationBrollCandidate row = new()
            {
                GenerationId = generation.Id,
                GenerationDemoId = long.Parse(
                    candidate.DemoId,
                    CultureInfo.InvariantCulture),
                CandidateId = candidate.Id,
                Type = candidate.Type,
                RoundNumber = candidate.RoundNumber,
                StartTick = selectedSegment?.Camera.StartTick ??
                    candidate.StartTick,
                EndTick = selectedSegment?.Camera.EndTick ??
                    candidate.EndTick,
                MovementScore = candidate.MovementScore,
                CinematicScore = candidate.CinematicScore,
                ActionDensity = candidate.ActionDensity,
                TrajectoryJson = JsonSerializer.Serialize(
                    candidate.Trajectory,
                    JsonOptions),
                Selected = selectedBrollIds.Contains(candidate.Id)
            };
            storedBroll.Add(candidate.Id, row);
            db.GenerationBrollCandidates.Add(row);
        }
        foreach (CinematicSequenceSegment segment in plan.Segments)
        {
            GenerationBrollCandidate? candidate = null;
            if (segment.BrollCandidateId is not null)
                storedBroll.TryGetValue(segment.BrollCandidateId, out candidate);
            db.GenerationCameraShots.Add(new GenerationCameraShot
            {
                GenerationId = generation.Id,
                BrollCandidate = candidate,
                ShotId = segment.Camera.Id,
                Type = segment.Camera.Type,
                StartTick = segment.Camera.StartTick,
                EndTick = segment.Camera.EndTick,
                KeyframesJson = JsonSerializer.Serialize(
                    segment.Camera.Keyframes,
                    JsonOptions),
                FovStart = segment.Camera.FovStart,
                FovEnd = segment.Camera.FovEnd,
                PreviewStatus = segment.Camera.Type ==
                    CameraShotType.PlayerPov
                        ? CameraPreviewStatus.PovFallback
                        : CameraPreviewStatus.NotAttempted,
                FallbackType = CameraShotType.PlayerPov
            });
        }
        db.GenerationCinematicPlans.Add(new GenerationCinematicPlan
        {
            GenerationId = generation.Id,
            PlannerVersion = plan.PlannerVersion,
            MusicExcerptJson = JsonSerializer.Serialize(
                excerpt,
                JsonOptions),
            PlanJson = JsonSerializer.Serialize(plan, JsonOptions),
            LockedAt = now,
            CreatedAt = now
        });

        string directory = storage.EnsureDirectory(
            generation.PublicId,
            "plan");
        await WriteAtomicallyAsync(
            Path.Combine(directory, "cinematic-music-narrative.json"),
            narrative,
            cancellationToken);
        string planPath = Path.Combine(
            directory,
            "cinematic-movie-plan.json");
        await WriteAtomicallyAsync(
            planPath,
            plan,
            cancellationToken);
        string alignmentPath = Path.Combine(
            directory,
            "cinematic-alignment-report.json");
        await WriteAtomicallyAsync(
            alignmentPath,
            alignment,
            cancellationToken);
        string capabilitiesPath = Path.Combine(
            directory,
            "camera-capabilities.json");
        await WriteAtomicallyAsync(
            capabilitiesPath,
            capabilities,
            cancellationToken);
        AddArtifact(
            db,
            generation.Id,
            ArtifactType.CinematicMusicNarrative,
            Path.Combine(directory, "cinematic-music-narrative.json"),
            now);
        AddArtifact(
            db,
            generation.Id,
            ArtifactType.CinematicMoviePlan,
            planPath,
            now);
        AddArtifact(
            db,
            generation.Id,
            ArtifactType.CinematicAlignmentReport,
            alignmentPath,
            now);
        AddArtifact(
            db,
            generation.Id,
            ArtifactType.CameraCapabilities,
            capabilitiesPath,
            now);
        await db.SaveChangesAsync(cancellationToken);

        if (logger.IsEnabled(LogLevel.Information))
        {
            var cinematicSegmentCount = plan.Segments.Count(value =>
                value.Camera.Type != CameraShotType.PlayerPov);
            var playerPovSegmentCount = plan.Segments.Count(value =>
                value.Camera.Type == CameraShotType.PlayerPov);

            LogPlan(
                logger,
                generation.PublicId,
                plan.Segments.Count,
                plan.HighlightMatches.Count,
                cinematicSegmentCount,
                playerPovSegmentCount,
                plan.Warnings.Count);
        }
        return new CinematicLockedPlan(
            narrative,
            plan,
            alignment,
            uniqueBroll);
    }

    private void Advance(
        GenerationDbContext db,
        Generation generation,
        GenerationStatus status,
        int progress,
        string message)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        GenerationStateMachine.Transition(generation, status, now);
        generation.ProgressPercent = Math.Max(
            generation.ProgressPercent,
            progress);
        generation.CurrentStage = message;
        db.GenerationEvents.Add(new GenerationEvent
        {
            GenerationId = generation.Id,
            Stage = status.ToString(),
            Message = message,
            ProgressPercent = generation.ProgressPercent,
            CreatedAt = now
        });
    }

    private static CinematicTimeWarpOptions TimeWarpFor(
        CinematicEditIntensity intensity) =>
        intensity switch
        {
            CinematicEditIntensity.Calm => new CinematicTimeWarpOptions
            {
                MinimumBaseSpeed = 0.95,
                MaximumBaseSpeed = 1.05,
                MinimumLocalSpeed = 0.82,
                MaximumLocalSpeed = 1.12,
                MaximumRampDurationSeconds = 1,
                MaximumPostKillAcceleration = 1.02
            },
            CinematicEditIntensity.Dynamic => new CinematicTimeWarpOptions
            {
                MinimumBaseSpeed = 0.86,
                MaximumBaseSpeed = 1.14,
                MinimumLocalSpeed = 0.65,
                MaximumLocalSpeed = 1.30,
                MaximumRampDurationSeconds = 1.5,
                MaximumPostKillAcceleration = 1.05
            },
            _ => new CinematicTimeWarpOptions()
        };

    private static CinematicEffectPolicy EffectsFor(
        CinematicEditIntensity intensity,
        EffectIntensity effectIntensity) =>
        new()
        {
            MaximumVisibleFilterEffectsPerHighlight =
                intensity == CinematicEditIntensity.Calm
                    ? 0
                    : effectIntensity == EffectIntensity.Strong
                        ? 2
                        : 1,
            PreferCameraMotionOverFilterEffects = true
        };

    private static async Task<T> ReadJsonAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            JsonOptions,
            cancellationToken) ??
            throw new InvalidOperationException(
                $"CINEMATIC_JSON_INVALID:{Path.GetFileName(path)}");
    }

    private static T Deserialize<T>(string json, string error)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ??
                throw new InvalidOperationException(error);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(error, exception);
        }
    }

    private static async Task WriteAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(value, JsonOptions),
                cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void AddArtifact(
        GenerationDbContext db,
        long generationId,
        ArtifactType type,
        string path,
        DateTimeOffset now) =>
        db.GenerationArtifacts.Add(new GenerationArtifact
        {
            GenerationId = generationId,
            Type = type,
            FileName = Path.GetFileName(path),
            StoredPath = path,
            ContentType = "application/json",
            FileSizeBytes = new FileInfo(path).Length,
            CreatedAt = now
        });
}
