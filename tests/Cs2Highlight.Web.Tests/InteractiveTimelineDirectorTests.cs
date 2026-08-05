using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Cs2Highlight.Web.Tests;

public sealed class InteractiveTimelineDirectorTests :
    IAsyncLifetime,
    IDisposable
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection connection =
        new("Data Source=:memory:");
    private readonly string storageRoot = Path.Combine(
        Path.GetTempPath(),
        $"timeline-tests-{Guid.NewGuid():N}");
    private TestDbFactory factory = null!;
    private InteractiveTimelineDirector director = null!;
    private string publicId = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        DbContextOptions<GenerationDbContext> options =
            new DbContextOptionsBuilder<GenerationDbContext>()
                .UseSqlite(connection)
                .Options;
        factory = new TestDbFactory(options);
        await using GenerationDbContext db =
            await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        publicId = $"timeline{Guid.NewGuid():N}";
        await SeedAsync(db, publicId);
        director = new InteractiveTimelineDirector(
            factory,
            TimeProvider.System,
            new InteractiveRetimingOptions(),
            new GenerationStorage(new StorageOptions
            {
                Root = storageRoot
            }),
            new GenerationWakeSignal());
    }

    [Fact]
    public async Task ExactAndCategoryMarkersAssignDeterministically()
    {
        InteractiveTimelineView initial =
            await director.GetOrCreateAsync(
                publicId,
                CancellationToken.None);
        InteractiveTimelineView exact =
            await director.AddAnchorAsync(
                publicId,
                new AddTimelineAnchorRequest(
                    TimelineMarkerType.ExactHighlight,
                    "highlight-triple",
                    10,
                    ConcurrencyToken: initial.ConcurrencyToken),
                CancellationToken.None);
        InteractiveTimelineView category =
            await director.AddAnchorAsync(
                publicId,
                new AddTimelineAnchorRequest(
                    TimelineMarkerType.BestSolo,
                    null,
                    21,
                    ConcurrencyToken: exact.ConcurrencyToken),
                CancellationToken.None);

        Assert.Equal(2, category.Anchors.Count);
        Assert.Equal(
            "highlight-triple",
            category.Anchors[0].HighlightId);
        Assert.Equal(
            "highlight-solo",
            category.Anchors[1].HighlightId);
        Assert.DoesNotContain(
            category.Anchors,
            value =>
                value.Feasibility ==
                AnchorFeasibilityStatus.Invalid);
    }

    [Fact]
    public async Task DuplicateHighlightIsInvalidAndBlocksConfirmation()
    {
        InteractiveTimelineView initial =
            await director.GetOrCreateAsync(
                publicId,
                CancellationToken.None);
        InteractiveTimelineView first =
            await director.AddAnchorAsync(
                publicId,
                new AddTimelineAnchorRequest(
                    TimelineMarkerType.ExactHighlight,
                    "highlight-triple",
                    8,
                    ConcurrencyToken: initial.ConcurrencyToken),
                CancellationToken.None);
        InteractiveTimelineView duplicate =
            await director.AddAnchorAsync(
                publicId,
                new AddTimelineAnchorRequest(
                    TimelineMarkerType.ExactHighlight,
                    "highlight-triple",
                    20,
                    ConcurrencyToken: first.ConcurrencyToken),
                CancellationToken.None);

        Assert.Contains(
            duplicate.Anchors,
            value =>
                value.Feasibility ==
                AnchorFeasibilityStatus.Invalid &&
                value.Warnings.Contains("DUPLICATE_HIGHLIGHT"));
        await Assert.ThrowsAsync<TimelineValidationException>(() =>
            director.ConfirmAsync(
                publicId,
                duplicate.ConcurrencyToken,
                CancellationToken.None));
    }

    [Fact]
    public async Task LockedMarkerCannotMoveUntilExplicitlyUnlocked()
    {
        InteractiveTimelineView initial =
            await director.GetOrCreateAsync(
                publicId,
                CancellationToken.None);
        InteractiveTimelineView added =
            await director.AddAnchorAsync(
                publicId,
                new AddTimelineAnchorRequest(
                    TimelineMarkerType.ExactHighlight,
                    "highlight-triple",
                    12,
                    true,
                    initial.ConcurrencyToken),
                CancellationToken.None);
        UserKillAnchor anchor = Assert.Single(added.Anchors);

        await Assert.ThrowsAsync<TimelineConflictException>(() =>
            director.UpdateAnchorAsync(
                publicId,
                anchor.Id,
                new UpdateTimelineAnchorRequest(
                    13,
                    null,
                    null,
                    null,
                    added.ConcurrencyToken),
                CancellationToken.None));
        InteractiveTimelineView unlocked =
            await director.UpdateAnchorAsync(
                publicId,
                anchor.Id,
                new UpdateTimelineAnchorRequest(
                    null,
                    null,
                    null,
                    false,
                    added.ConcurrencyToken),
                CancellationToken.None);
        InteractiveTimelineView moved =
            await director.UpdateAnchorAsync(
                publicId,
                anchor.Id,
                new UpdateTimelineAnchorRequest(
                    13,
                    null,
                    null,
                    null,
                    unlocked.ConcurrencyToken),
                CancellationToken.None);

        Assert.Equal(13, Assert.Single(moved.Anchors)
            .TargetMusicTimeSeconds);
    }

    [Fact]
    public async Task UndoRedoAndOptimisticConcurrencyPreserveIntent()
    {
        InteractiveTimelineView initial =
            await director.GetOrCreateAsync(
                publicId,
                CancellationToken.None);
        InteractiveTimelineView added =
            await director.AddAnchorAsync(
                publicId,
                new AddTimelineAnchorRequest(
                    TimelineMarkerType.ExactHighlight,
                    "highlight-triple",
                    10,
                    ConcurrencyToken: initial.ConcurrencyToken),
                CancellationToken.None);
        UserKillAnchor anchor = Assert.Single(added.Anchors);
        InteractiveTimelineView moved =
            await director.UpdateAnchorAsync(
                publicId,
                anchor.Id,
                new UpdateTimelineAnchorRequest(
                    14,
                    null,
                    null,
                    null,
                    added.ConcurrencyToken),
                CancellationToken.None);

        await Assert.ThrowsAsync<TimelineConflictException>(() =>
            director.SetModeAsync(
                publicId,
                TimelineDirectorMode.Auto,
                added.ConcurrencyToken,
                CancellationToken.None));
        InteractiveTimelineView undone =
            await director.UndoAsync(
                publicId,
                moved.ConcurrencyToken,
                CancellationToken.None);
        Assert.Equal(
            10,
            Assert.Single(undone.Anchors)
                .TargetMusicTimeSeconds);
        InteractiveTimelineView redone =
            await director.RedoAsync(
                publicId,
                undone.ConcurrencyToken,
                CancellationToken.None);
        Assert.Equal(
            14,
            Assert.Single(redone.Anchors)
                .TargetMusicTimeSeconds);
    }

    [Fact]
    public async Task PaymentLockMakesPlanAndGapsImmutable()
    {
        InteractiveTimelineView initial =
            await director.GetOrCreateAsync(
                publicId,
                CancellationToken.None);
        InteractiveTimelineView added =
            await director.AddAnchorAsync(
                publicId,
                new AddTimelineAnchorRequest(
                    TimelineMarkerType.ExactHighlight,
                    "highlight-triple",
                    12,
                    ConcurrencyToken: initial.ConcurrencyToken),
                CancellationToken.None);
        UserKillAnchor anchor = Assert.Single(added.Anchors);
        await using (GenerationDbContext db =
                     await factory.CreateDbContextAsync())
        {
            long generationId = await db.Generations
                .Where(value => value.PublicId == publicId)
                .Select(value => value.Id)
                .SingleAsync();
            await director.LockAfterPaymentAsync(
                generationId,
                DateTimeOffset.UtcNow,
                db,
                CancellationToken.None);
            await db.SaveChangesAsync();
            Assert.NotNull(await db.GenerationMovieSettings
                .Where(value => value.GenerationId == generationId)
                .Select(value => value.LockedAt)
                .SingleAsync());
        }

        InteractiveTimelineView locked =
            await director.GetOrCreateAsync(
                publicId,
                CancellationToken.None);
        Assert.True(locked.IsLocked);
        Assert.All(
            locked.Gaps,
            value => Assert.Equal(
                nameof(TimelineGapState.Locked),
                value.State));
        await Assert.ThrowsAsync<TimelineConflictException>(() =>
            director.UpdateAnchorAsync(
                publicId,
                anchor.Id,
                new UpdateTimelineAnchorRequest(
                    13,
                    null,
                    null,
                    null,
                    locked.ConcurrencyToken),
                CancellationToken.None));
    }

    [Fact]
    public async Task MovingOneAnchorReplansOnlyItsAdjacentRegions()
    {
        InteractiveTimelineView initial = await director.GetOrCreateAsync(
            publicId,
            CancellationToken.None);
        InteractiveTimelineView firstAdded = await director.AddAnchorAsync(
            publicId,
            new AddTimelineAnchorRequest(
                TimelineMarkerType.ExactHighlight,
                "highlight-triple",
                8,
                ConcurrencyToken: initial.ConcurrencyToken),
            CancellationToken.None);
        InteractiveTimelineView arranged = await director.AddAnchorAsync(
            publicId,
            new AddTimelineAnchorRequest(
                TimelineMarkerType.ExactHighlight,
                "highlight-solo",
                20,
                ConcurrencyToken: firstAdded.ConcurrencyToken),
            CancellationToken.None);
        Assert.All(
            arranged.Gaps.Where(value => value.Role !=
                nameof(TimelineGapRole.Outro)),
            gap => Assert.Equal(
                nameof(TimelineGapState.Failed),
                gap.State));
        Assert.Equal(
            nameof(TimelineGapState.Planned),
            arranged.Gaps.Single(value => value.Role ==
                nameof(TimelineGapRole.Outro)).State);
        UserKillAnchor first = arranged.Anchors.Single(value =>
            value.HighlightId == "highlight-triple");
        UserKillAnchor second = arranged.Anchors.Single(value =>
            value.HighlightId == "highlight-solo");
        Dictionary<string, (long Id, DateTimeOffset UpdatedAt, string Json)>
            before = await StoredGapsAsync();

        await Task.Delay(25);
        InteractiveTimelineView moved = await director.UpdateAnchorAsync(
            publicId,
            first.Id,
            new UpdateTimelineAnchorRequest(
                10,
                null,
                null,
                null,
                arranged.ConcurrencyToken),
            CancellationToken.None);
        Dictionary<string, (long Id, DateTimeOffset UpdatedAt, string Json)>
            after = await StoredGapsAsync();

        string introId = $"gap-start-{first.Id}";
        string betweenId = $"gap-{first.Id}-{second.Id}";
        string outroId = $"gap-{second.Id}-end";
        Assert.Equal(before[introId].Id, after[introId].Id);
        Assert.Equal(before[betweenId].Id, after[betweenId].Id);
        Assert.NotEqual(before[introId].Json, after[introId].Json);
        Assert.NotEqual(before[betweenId].Json, after[betweenId].Json);
        Assert.Equal(before[outroId].Id, after[outroId].Id);
        Assert.Equal(before[outroId].UpdatedAt, after[outroId].UpdatedAt);
        Assert.True(moved.Gaps.Single(value => value.Id == outroId).Reused);
    }

    [Fact]
    public async Task TimelineReturnsPersistedRealWaveformSamples()
    {
        string directory = Path.Combine(storageRoot, publicId, "plan");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "real-waveform-envelope.json");
        RealWaveformEnvelopeArtifact artifact = new()
        {
            Available = true,
            ExcerptStartSeconds = 4,
            ExcerptEndSeconds = 34,
            SamplesPerSecond = 160,
            Peaks =
            [
                new MusicWaveformPeak(0, 0.25, 0.75),
                new MusicWaveformPeak(0.00625, 0.50, 1)
            ],
            Warnings = []
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(artifact, WebJson));
        await using (GenerationDbContext db =
                     await factory.CreateDbContextAsync())
        {
            long generationId = await db.Generations
                .Where(value => value.PublicId == publicId)
                .Select(value => value.Id)
                .SingleAsync();
            db.GenerationArtifacts.Add(new GenerationArtifact
            {
                GenerationId = generationId,
                Type = ArtifactType.RealWaveformEnvelope,
                FileName = Path.GetFileName(path),
                StoredPath = path,
                ContentType = "application/json",
                FileSizeBytes = new FileInfo(path).Length,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        InteractiveTimelineView view = await director.GetOrCreateAsync(
            publicId,
            CancellationToken.None);

        Assert.True(view.Waveform.Available);
        Assert.Equal(160, view.Waveform.SamplesPerSecond);
        Assert.Equal(artifact.Peaks, view.Waveform.Peaks);
        Assert.Equal(4, view.Waveform.ExcerptStartSeconds);
    }

    [Fact]
    public async Task UnverifiedFreeCameraRemainsScheduledForRenderPreview()
    {
        await using (GenerationDbContext db =
                     await factory.CreateDbContextAsync())
        {
            GenerationBrollCandidate[] candidates = await db
                .GenerationBrollCandidates
                .OrderBy(value => value.CandidateId)
                .ToArrayAsync();
            List<CinematicSequenceSegment> cameraPrototypes = [];
            // Only the final candidate has a renderable free-camera plan. The
            // local editor must prefer it over an earlier, otherwise
            // higher-ranked candidate that would silently fall back to POV.
            foreach (GenerationBrollCandidate candidate in candidates.TakeLast(1))
            {
                CameraKeyframe[] keyframes =
                [
                    new CameraKeyframe
                    {
                        TimeSeconds = 0,
                        Position = new GameplayVector3(0, 0, 64),
                        Rotation = new GameplayVector3(0, 0, 0),
                        Fov = 82
                    },
                    new CameraKeyframe
                    {
                        TimeSeconds = 5,
                        Position = new GameplayVector3(128, 0, 64),
                        Rotation = new GameplayVector3(0, 15, 0),
                        Fov = 76
                    }
                ];
                db.GenerationCameraShots.Add(new GenerationCameraShot
                {
                    GenerationId = candidate.GenerationId,
                    GenerationBrollCandidateId = candidate.Id,
                    ShotId = $"camera-{candidate.CandidateId}-tracking",
                    Type = CameraShotType.SideTracking,
                    StartTick = candidate.StartTick,
                    EndTick = candidate.EndTick,
                    KeyframesJson = JsonSerializer.Serialize<CameraKeyframe[]>(
                        keyframes,
                        WebJson),
                    FovStart = 82,
                    FovEnd = 76,
                    PreviewStatus = CameraPreviewStatus.NotAttempted,
                    FallbackType = CameraShotType.PlayerPov
                });
                cameraPrototypes.Add(new CinematicSequenceSegment
                {
                    Id = $"segment-{candidate.CandidateId}",
                    Role = CinematicSequenceRole.Intro,
                    OutputStartSeconds = 0,
                    OutputEndSeconds = 5,
                    MusicSectionId = "section",
                    BrollCandidateId = candidate.CandidateId,
                    Camera = new CameraShotPlan
                    {
                        Id = $"camera-{candidate.CandidateId}-tracking",
                        Type = CameraShotType.SideTracking,
                        Family = CameraShotFamily.SideTracking,
                        DemoId = candidate.GenerationDemoId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        StartTick = candidate.StartTick,
                        EndTick = candidate.EndTick,
                        TargetDurationSeconds = 5,
                        Keyframes = keyframes,
                        TargetPoints =
                        [
                            new CameraTargetPoint(
                                0,
                                new GameplayVector3(96, 0, 64),
                                ["76561198000000001"]),
                            new CameraTargetPoint(
                                5,
                                new GameplayVector3(224, 0, 64),
                                ["76561198000000001"])
                        ],
                        FovCurve = keyframes.Select(value =>
                            new CameraFovPoint(
                                value.TimeSeconds,
                                value.Fov)).ToArray(),
                        FovStart = 82,
                        FovEnd = 76,
                        RequiresHighFpsCapture = false,
                        FallbackShotId =
                            $"camera-{candidate.CandidateId}-pov",
                        Warnings = ["CAMERA_PREVIEW_PENDING"],
                        SafetyVolume = new SafeCameraVolume(
                            new GameplayVector3(-1, -1, 0),
                            new GameplayVector3(256, 1, 128)),
                        PreviewRequired = true,
                        AutomaticCalibration = true
                    },
                    TimeWarp = new TimeWarpPlan(1, [], false, []),
                    Effects = []
                });
            }
            db.GenerationCinematicPlans.Add(new GenerationCinematicPlan
            {
                GenerationId = candidates[0].GenerationId,
                PlannerVersion = "test",
                MusicExcerptJson = "{}",
                PlanJson = JsonSerializer.Serialize(
                    new CinematicMoviePlan
                    {
                        SchemaVersion = "1.0",
                        PlannerVersion = "test",
                        GenerationId = publicId,
                        MusicExcerpt = new MusicExcerptPlan
                        {
                            StartSeconds = 0,
                            EndSeconds = 30,
                            SectionIds = [],
                            Peaks = [],
                            RequiredPeakCount = 0,
                            UsablePeakCount = 0,
                            Score = 1,
                            IsValid = true,
                            ScoreBreakdown =
                                new Dictionary<string, double>(),
                            Warnings = []
                        },
                        TargetDurationSeconds = 30,
                        Segments = cameraPrototypes,
                        HighlightMatches = [],
                        SoundDesign = new SoundDesignPlan([], true, []),
                        Color = new ColorNarrativePlan(
                            ColorGradePreset.Natural,
                            [],
                            []),
                        Warnings = []
                    },
                    WebJson),
                LockedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        InteractiveTimelineView view = await director.GetOrCreateAsync(
            publicId,
            CancellationToken.None);

        TimelineGapView freeCamera = Assert.Single(
            view.Gaps.Where(value =>
                value.Camera == nameof(CameraShotFamily.SideTracking)));
        Assert.Equal("Preview pending", freeCamera.CameraVerification);
        await using GenerationDbContext verification =
            await factory.CreateDbContextAsync();
        LocalTimelineRegionPlan[] regions = (await verification
                .GenerationTimelineGaps
                .AsNoTracking()
                .ToArrayAsync())
            .Select(value => JsonSerializer.Deserialize<
                LocalTimelineRegionPlan>(value.PlanJson, WebJson)!)
            .ToArray();
        CameraShotPlan scheduled = regions
            .SelectMany(value => value.CameraShots)
            .First(value =>
                value.Family == CameraShotFamily.SideTracking);
        Assert.Contains("broll-07", scheduled.Id, StringComparison.Ordinal);
        Assert.True(scheduled.PreviewRequired);
        Assert.Contains("CAMERA_PREVIEW_PENDING", scheduled.Warnings);
        Assert.Equal(2, scheduled.TargetPoints.Count);
        Assert.NotNull(scheduled.SafetyVolume);
        Assert.True(scheduled.AutomaticCalibration);
        Assert.All(scheduled.Keyframes, value =>
            Assert.InRange(value.TimeSeconds, 0, scheduled.TargetDurationSeconds));
    }

    [Fact]
    public void ShortGapsAreAbsorbedWithoutMovingKillAnchors()
    {
        CinematicSequenceSegment[] source =
        [
            Segment("broll", null, 0, 1.5),
            Segment("h1", "highlight-triple", 1.5, 3.5),
            Segment("h2", "highlight-solo", 3.703, 5.84)
        ];
        Dictionary<string, LocalHighlightSegmentPlan> local = new()
        {
            ["highlight-triple"] = Local("a1", "highlight-triple", 1, 1),
            ["highlight-solo"] = Local("a2", "highlight-solo", 1, 1.137)
        };
        Dictionary<string, GenerationTimelineAnchor> anchors = new()
        {
            ["highlight-triple"] = new GenerationTimelineAnchor
            {
                AnchorId = "a1",
                HighlightId = "highlight-triple",
                TargetMilliseconds = 2_500
            },
            ["highlight-solo"] = new GenerationTimelineAnchor
            {
                AnchorId = "a2",
                HighlightId = "highlight-solo",
                TargetMilliseconds = 4_703
            }
        };

        CinematicSequenceSegment[] normalized =
            InteractiveTimelineDirector.NormalizeCinematicContinuity(
                source,
                local,
                anchors,
                6);

        Assert.All(
            normalized.Zip(normalized.Skip(1)),
            pair => Assert.Equal(
                pair.First.OutputEndSeconds,
                pair.Second.OutputStartSeconds,
                6));
        Assert.Equal(6, normalized[^1].OutputEndSeconds, 6);
        CinematicSequenceSegment second = normalized[^1];
        Assert.Equal(3.5, second.OutputStartSeconds, 6);
        Assert.True(second.TimeWarp.UsesLocalRamp);
        Assert.Equal(
            4.703,
            second.OutputStartSeconds +
            TimeWarpMath.MapSourceTime(second.TimeWarp, 1),
            6);
    }

    [Fact]
    public void OutroResidualIsAbsorbedBySlowingTheFinalBroll()
    {
        CinematicSequenceSegment[] normalized =
            InteractiveTimelineDirector.NormalizeCinematicContinuity(
                [Segment("outro", null, 0, 3.938)],
                new Dictionary<string, LocalHighlightSegmentPlan>(),
                new Dictionary<string, GenerationTimelineAnchor>(),
                4.4);

        CinematicSequenceSegment outro = Assert.Single(normalized);
        Assert.Equal(4.4, outro.OutputEndSeconds, 6);
        Assert.Equal(3.938 / 4.4, outro.TimeWarp.BaseSpeedFactor, 6);
        Assert.Contains(
            "OUTRO_FREECAM_DURATION_ABSORPTION",
            outro.TimeWarp.Warnings);
    }

    public async Task DisposeAsync() =>
        await connection.DisposeAsync();

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(storageRoot))
            Directory.Delete(storageRoot, recursive: true);
    }

    private async Task<Dictionary<string, (
        long Id,
        DateTimeOffset UpdatedAt,
        string Json)>> StoredGapsAsync()
    {
        await using GenerationDbContext db =
            await factory.CreateDbContextAsync();
        return await db.GenerationTimelineGaps
            .AsNoTracking()
            .ToDictionaryAsync(
                value => value.GapId,
                value => new ValueTuple<long, DateTimeOffset, string>(
                    value.Id,
                    value.UpdatedAt,
                    value.PlanJson));
    }

    private static async Task SeedAsync(
        GenerationDbContext db,
        string id)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Generation generation = new()
        {
            PublicId = id,
            Status = GenerationStatus.AwaitingPayment,
            CurrentStage = "AwaitingPayment",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Generations.Add(generation);
        await db.SaveChangesAsync();
        GenerationDemo demo = new()
        {
            GenerationId = generation.Id,
            OriginalFileName = "dust2.dem",
            StoredPath = "dust2.dem",
            FileSizeBytes = 1,
            Sha256 = new string('b', 64),
            UploadOrder = 0,
            AnalysisStatus = DemoAnalysisStatus.Succeeded,
            MapName = "de_dust2",
            TickRate = 64,
            DurationTicks = 10_000
        };
        generation.Demos.Add(demo);
        await db.SaveChangesAsync();
        generation.Music = new GenerationMusic
        {
            OriginalFileName = "music.wav",
            StoredPath = "music.wav",
            FileSizeBytes = 1,
            Sha256 = new string('a', 64),
            DurationMilliseconds = 30_000,
            SampleRate = 48_000,
            Channels = 2,
            RightsConfirmed = true,
            CreatedAt = now
        };
        generation.MovieSettings = new GenerationMovieSettings
        {
            MovieStyle = MovieStyle.CinematicDirector,
            CreatedAt = now
        };
        generation.Highlights.AddRange(
        [
            Highlight(
                generation.Id,
                demo.Id,
                "highlight-triple",
                "TripleKill",
                94,
                88),
            Highlight(
                generation.Id,
                demo.Id,
                "highlight-solo",
                "SoloKill",
                90,
                91)
        ]);
        generation.BrollCandidates.AddRange(Enumerable.Range(0, 8)
            .Select(index => new GenerationBrollCandidate
            {
                GenerationId = generation.Id,
                GenerationDemoId = demo.Id,
                CandidateId = $"broll-{index:D2}",
                Type = index % 2 == 0
                    ? Cs2Highlight.Music.BrollCandidateType.TeamSetup
                    : Cs2Highlight.Music.BrollCandidateType.TeamMovement,
                RoundNumber = 18,
                StartTick = 2_000 + index * 400,
                EndTick = 2_320 + index * 400,
                MovementScore = 0.55,
                CinematicScore = 0.75,
                ActionDensity = 0.12,
                TrajectoryJson = "{}"
            }));
        await db.SaveChangesAsync();
    }

    private static GenerationHighlight Highlight(
        long generationId,
        long generationDemoId,
        string id,
        string type,
        double total,
        double beauty) =>
        new()
        {
            GenerationId = generationId,
            GenerationDemoId = generationDemoId,
            HighlightId = id,
            SteamId = "76561198000000001",
            MapName = "de_mirage",
            Type = type,
            RoundNumber = 18,
            StartTick = 1_000,
            FirstKillTick = 1_080,
            LastKillTick = 1_180,
            PrimaryKillTick = 1_160,
            SafeEndTick = 1_260,
            EndTick = 1_300,
            TickRate = 64,
            KillCount = type == "TripleKill" ? 3 : 1,
            HeadshotCount = 2,
            BeautyScore = beauty,
            TotalScore = total,
            SelectedByUser = true,
            EstimatedDurationMilliseconds = 4_100,
            WeaponSequenceJson = "[]",
            ScoreBreakdownJson = "{}",
            TagsJson = "[]",
            KillsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static CinematicSequenceSegment Segment(
        string id,
        string? highlightId,
        double start,
        double end) =>
        new()
        {
            Id = id,
            Role = highlightId is null
                ? CinematicSequenceRole.Intro
                : CinematicSequenceRole.Highlight,
            OutputStartSeconds = start,
            OutputEndSeconds = end,
            MusicSectionId = "section",
            HighlightId = highlightId,
            BrollCandidateId = highlightId is null ? "broll" : null,
            Camera = new CameraShotPlan
            {
                Id = $"camera-{id}",
                Type = CameraShotType.PlayerPov,
                DemoId = "demo",
                StartTick = 0,
                EndTick = 128,
                TargetDurationSeconds = end - start,
                Keyframes = [],
                FovStart = 90,
                FovEnd = 90,
                RequiresHighFpsCapture = false,
                FallbackShotId = string.Empty,
                Warnings = []
            },
            TimeWarp = new TimeWarpPlan(1, [], false, []),
            Effects = []
        };

    private static LocalHighlightSegmentPlan Local(
        string anchorId,
        string highlightId,
        double pre,
        double post) =>
        new()
        {
            AnchorId = anchorId,
            HighlightId = highlightId,
            SourceStartTick = 0,
            PrimaryKillTick = 64,
            SafeEndTick = 128,
            OutputStartMilliseconds = 0,
            PrimaryKillOutputMilliseconds = 0,
            OutputEndMilliseconds = 0,
            PreRollSeconds = pre,
            PostKillSeconds = post,
            Feasibility = AnchorFeasibilityStatus.Natural
        };

    private sealed class TestDbFactory(
        DbContextOptions<GenerationDbContext> options)
        : IDbContextFactory<GenerationDbContext>
    {
        public GenerationDbContext CreateDbContext() =>
            new(options);

        public Task<GenerationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GenerationDbContext(options));
    }
}
