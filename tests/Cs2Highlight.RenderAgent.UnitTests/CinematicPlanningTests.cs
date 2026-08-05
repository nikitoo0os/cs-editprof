using Cs2Highlight.Analysis;
using Cs2Highlight.Music;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class CinematicPlanningTests
{
    [Theory]
    [InlineData("calm", MusicSectionType.Calm)]
    [InlineData("build", MusicSectionType.BuildUp)]
    [InlineData("drop", MusicSectionType.Drop)]
    [InlineData("high", MusicSectionType.HighEnergy)]
    [InlineData("breakdown", MusicSectionType.Breakdown)]
    public void SectionClassifierRecognizesExplainablePatterns(
        string pattern,
        MusicSectionType expected)
    {
        MusicAnalysis analysis = PatternMusic(pattern);

        MusicSection section = Assert.Single(Classifier().Classify(analysis));

        Assert.Equal(expected, section.Type);
        Assert.NotEmpty(section.ScoreBreakdown);
        Assert.InRange(section.Confidence, 0.35, 0.98);
    }

    [Fact]
    public void LoudFrameWithoutBassOnsetOrSlopeIsNotDrop()
    {
        MusicAnalysis analysis = Analysis(
            [RawSection(1, 5, 9, 0.9)],
            Frames(5, 9, _ => Frame(0.92, 0.05, 0.05, 0.05, 0.05, 0.1, 0.05)));

        MusicSection section = Assert.Single(Classifier().Classify(analysis));

        Assert.NotEqual(MusicSectionType.Drop, section.Type);
    }

    [Fact]
    public void SectionClassificationIsDeterministic()
    {
        MusicAnalysis analysis = PatternMusic("drop");
        MusicSectionClassifier classifier = Classifier();

        MusicSection first = Assert.Single(classifier.Classify(analysis));
        MusicSection second = Assert.Single(classifier.Classify(analysis));

        Assert.Equal(first.Type, second.Type);
        Assert.Equal(first.ScoreBreakdown, second.ScoreBreakdown);
        Assert.Equal(first.Confidence, second.Confidence);
    }

    [Fact]
    public void NarrativeReportsLowSignalAndMissingStructure()
    {
        MusicAnalysis analysis = Analysis(
            [RawSection(1, 5, 7, 0.5)],
            []);

        MusicNarrative narrative = NarrativeAnalyzer().Analyze(analysis);

        Assert.Contains("MUSIC_BUILDUP_NOT_DETECTED", narrative.Warnings);
        Assert.Contains("MUSIC_FRAME_TIMELINE_UNAVAILABLE", narrative.Warnings);
    }

    [Fact]
    public void PeakDetectorDoesNotPromoteCalmBeatToDrop()
    {
        MusicAnalysis analysis = Analysis(
            [RawSection(1, 5, 9, 0.2)],
            Frames(5, 9, _ => Frame(0.2, 0.1, 0.1, 0.1, 0.1, 0.1, 0.1)),
            beats: [new MusicBeat(1, 6, 0.9, 0.9)]);
        IReadOnlyList<MusicSection> sections = Classifier().Classify(analysis);

        IReadOnlyList<MusicalPeak> peaks =
            new MusicalPeakDetector().Detect(analysis, sections);

        Assert.DoesNotContain(peaks, value =>
            value.Type == MusicalPeakType.DropStart);
    }

    [Fact]
    public void DurationPolicyCapsShortMovieAndBroll()
    {
        SelectedHighlight highlight = Highlight(
            "solo",
            HighlightType.SoloKill,
            duration: 8,
            importance: 20);

        MovieDurationBudget budget = new CinematicDurationPolicy().Calculate(
            [highlight],
            new MovieDurationOptions());

        Assert.Equal(8, budget.HighlightDurationSeconds);
        Assert.Equal(8, budget.MaximumBrollSeconds);
        Assert.Equal(16, budget.MaximumTotalSeconds);
        Assert.True(budget.MaximumTotalSeconds <= 30);
    }

    [Fact]
    public void ExplicitDurationIsAnExactTargetEvenForShortGameplay()
    {
        MovieDurationBudget budget = new CinematicDurationPolicy().Calculate(
            [Highlight("solo", HighlightType.SoloKill, 6, 20)],
            new MovieDurationOptions
            {
                Selection = MovieDurationSelection.Seconds30
            });

        Assert.Equal(30, budget.MaximumTotalSeconds);
        Assert.Equal(30, budget.TargetSeconds);
        Assert.Equal(24, budget.MaximumBrollSeconds);
    }

    [Fact]
    public void ExplicitFortyFiveSecondExcerptIsNotReplacedByShortSectionWindow()
    {
        MusicExcerptPlan excerpt = new MusicExcerptSelector(
            new CinematicDurationPolicy()).Select(
            ExcerptNarrative(duration: 60),
            [
                Highlight("h1", HighlightType.DoubleKill, 4, 30),
                Highlight("h2", HighlightType.Ace, 4, 80)
            ],
            new MovieDurationOptions
            {
                Selection = MovieDurationSelection.Seconds45
            });

        Assert.True(excerpt.IsValid);
        Assert.Equal(45, excerpt.DurationSeconds, 6);
    }

    [Fact]
    public void ExcerptContainsBuildUpDropAndEnoughPeaks()
    {
        MusicNarrative narrative = ExcerptNarrative();
        SelectedHighlight[] highlights =
        [
            Highlight("h1", HighlightType.DoubleKill, 4, 30),
            Highlight("h2", HighlightType.TripleKill, 4, 45)
        ];

        MusicExcerptPlan plan = new MusicExcerptSelector(
            new CinematicDurationPolicy()).Select(
                narrative,
                highlights,
                new MovieDurationOptions());

        Assert.True(plan.IsValid);
        Assert.Equal(2, plan.RequiredPeakCount);
        Assert.True(plan.UsablePeakCount >= 2);
        Assert.True(plan.DurationSeconds <= 16);
        Assert.Contains(
            plan.StartSeconds,
            narrative.Sections.Select(value => value.StartSeconds));
    }

    [Fact]
    public void ExcerptWarnsWhenStrongPeaksAreInsufficient()
    {
        MusicNarrative narrative = ExcerptNarrative() with
        {
            Peaks = []
        };

        MusicExcerptPlan plan = new MusicExcerptSelector(
            new CinematicDurationPolicy()).Select(
                narrative,
                [Highlight("h1", HighlightType.TripleKill, 4, 50)],
                new MovieDurationOptions());

        Assert.False(plan.IsValid);
        Assert.Contains("MUSIC_EXCERPT_INSUFFICIENT_PEAKS", plan.Warnings);
    }

    [Fact]
    public void ExcerptUsesStrongVersePeakWhenHighEnergyPeakIsUnavailable()
    {
        MusicNarrative narrative = new()
        {
            DurationSeconds = 8,
            Sections =
            [
                DetailedSection(
                    "verse",
                    MusicSectionType.Verse,
                    0,
                    8,
                    0.7)
            ],
            Peaks =
            [
                new MusicalPeak
                {
                    Id = "verse-downbeat",
                    Type = MusicalPeakType.Downbeat,
                    TimeSeconds = 4,
                    Strength = 0.85,
                    Confidence = 0.9,
                    SectionId = "verse"
                }
            ],
            Frames = [],
            Warnings = []
        };
        SelectedHighlight highlight = Highlight(
            "h1",
            HighlightType.SoloKill,
            4,
            30);

        MusicExcerptPlan excerpt = new MusicExcerptSelector(
            new CinematicDurationPolicy()).Select(
            narrative,
            [highlight],
            new MovieDurationOptions());
        CinematicMoviePlan movie = Director().Create(
            narrative,
            excerpt,
            [highlight],
            [],
            DirectorOptions());
        CinematicAlignmentReport alignment =
            CinematicAlignmentReportBuilder.FromPlan(
                movie,
                narrative.Sections);

        Assert.True(excerpt.IsValid);
        Assert.Contains(
            MusicExcerptSelector.RelaxedEnergyFallbackWarning,
            excerpt.Warnings);
        Assert.Single(movie.HighlightMatches);
        Assert.Equal(0, alignment.KillsOutsideHighEnergySections);
    }

    [Fact]
    public void ExcerptCropsLongVerseAroundStrongPeak()
    {
        MusicNarrative narrative = new()
        {
            DurationSeconds = 28,
            Sections =
            [
                DetailedSection(
                    "long-verse",
                    MusicSectionType.Verse,
                    0,
                    28,
                    0.7)
            ],
            Peaks =
            [
                new MusicalPeak
                {
                    Id = "long-verse-downbeat",
                    Type = MusicalPeakType.Downbeat,
                    TimeSeconds = 14,
                    Strength = 0.9,
                    Confidence = 0.9,
                    SectionId = "long-verse"
                }
            ],
            Frames = [],
            Warnings = []
        };

        MusicExcerptPlan excerpt = new MusicExcerptSelector(
            new CinematicDurationPolicy()).Select(
            narrative,
            [Highlight("h1", HighlightType.SoloKill, 5, 30)],
            new MovieDurationOptions());

        Assert.True(excerpt.IsValid);
        Assert.Equal(10, excerpt.DurationSeconds, 6);
        Assert.True(excerpt.StartSeconds > 0);
        Assert.True(excerpt.EndSeconds < narrative.DurationSeconds);
        Assert.Contains(
            MusicExcerptSelector.RelaxedEnergyFallbackWarning,
            excerpt.Warnings);
    }

    [Fact]
    public void ExcerptDoesNotExpandToFullSong()
    {
        MusicNarrative narrative = ExcerptNarrative(duration: 60);

        MusicExcerptPlan plan = new MusicExcerptSelector(
            new CinematicDurationPolicy()).Select(
                narrative,
                [Highlight("h1", HighlightType.TripleKill, 4, 50)],
                new MovieDurationOptions());

        Assert.True(plan.DurationSeconds < narrative.DurationSeconds);
    }

    [Theory]
    [InlineData("UtilityPreparation", BrollCandidateType.UtilityPreparation)]
    [InlineData("WeaponReload", BrollCandidateType.WeaponReload)]
    [InlineData(null, BrollCandidateType.PlayerApproach)]
    public void BrollDetectorClassifiesUsefulMovement(
        string? eventType,
        BrollCandidateType expected)
    {
        GameplayTimelineFrame[] frames = GameplayFrames(
            eventType,
            alive: true,
            freeze: false,
            nearKill: false,
            speed: 120);

        BrollCandidate candidate = Assert.Single(
            new BrollCandidateDetector().Detect(BrollContext(frames)));

        Assert.Equal(expected, candidate.Type);
        Assert.True(candidate.CinematicScore > 0);
    }

    [Fact]
    public void BrollDetectorMarksPlayerJumpAndItsFocusTick()
    {
        GameplayTimelineFrame[] frames = GameplayFrames(
                null,
                alive: true,
                freeze: false,
                nearKill: false,
                speed: 150)
            .Select((frame, index) => frame with
            {
                Player = frame.Player with
                {
                    Position = frame.Player.Position with
                    {
                        Z = 64 + (index <= 3 ? index * 10 : (6 - index) * 10)
                    },
                    Velocity = frame.Player.Velocity with
                    {
                        Z = index == 2 ? 180 : index == 4 ? -160 : 0
                    }
                }
            })
            .ToArray();

        BrollCandidate candidate = Assert.Single(
            new BrollCandidateDetector().Detect(BrollContext(frames)));

        Assert.Equal(BrollCandidateType.PlayerJump, candidate.Type);
        Assert.Equal(frames[2].Tick, candidate.FocusTick);
    }

    [Theory]
    [InlineData(false, false, false, 120)]
    [InlineData(true, true, false, 120)]
    [InlineData(true, false, true, 120)]
    [InlineData(true, false, false, 0)]
    public void BrollDetectorRejectsDeadFreezeKillOverlapOrIdle(
        bool alive,
        bool freeze,
        bool nearKill,
        double speed)
    {
        GameplayTimelineFrame[] frames = GameplayFrames(
            null,
            alive,
            freeze,
            nearKill,
            speed);

        IReadOnlyList<BrollCandidate> candidates =
            new BrollCandidateDetector().Detect(BrollContext(frames));

        Assert.Empty(candidates);
    }

    [Fact]
    public void BrollDetectorRejectsExcludedAndDuplicateIntervals()
    {
        GameplayTimelineFrame[] frames = GameplayFrames(
            null,
            alive: true,
            freeze: false,
            nearKill: false,
            speed: 120);
        BrollDetectionContext context = BrollContext(frames) with
        {
            ExcludedIntervals = [new GameplayInterval(100, 400)]
        };

        Assert.Empty(new BrollCandidateDetector().Detect(context));
    }

    [Fact]
    public void CameraPathHasFourOrderedDistinctSafeKeyframes()
    {
        BrollCandidate candidate = Broll();
        CameraPlanningContext context = VerifiedCameraContext();

        CameraShotPlan plan = new CameraPathPlanner().Create(
            candidate,
            context);

        Assert.NotEqual(CameraShotType.PlayerPov, plan.Type);
        Assert.Equal(4, plan.Keyframes.Count);
        Assert.All(
            plan.Keyframes.Zip(plan.Keyframes.Skip(1)),
            pair =>
            {
                Assert.True(pair.First.TimeSeconds < pair.Second.TimeSeconds);
                Assert.True(
                    pair.First.Position.DistanceTo(pair.Second.Position) > 0.05);
            });
        Assert.All(plan.Keyframes, value => Assert.InRange(value.Fov, 70, 100));
    }

    [Fact]
    public void CameraRouteIsSlowSinglePathEndingAtNextHighlight()
    {
        BrollCandidate candidate = Broll();
        GameplayVector3 destination = new(900, 240, 64);
        CameraPlanningContext context = VerifiedCameraContext() with
        {
            DestinationSubjectPosition = destination,
            MaximumCameraSpeedUnitsPerSecond = 80
        };

        CameraShotPlan plan = new CameraPathPlanner().Create(
            candidate,
            context);

        Assert.NotEqual(CameraShotType.PlayerPov, plan.Type);
        Assert.Equal(destination, plan.TargetPoints[^1].Position);
        Assert.Contains(
            "CAMERA_ROUTE_B_ANCHORED_TO_NEXT_HIGHLIGHT",
            plan.Warnings);
        double routeDistance = plan.Keyframes[0].Position.DistanceTo(
            plan.Keyframes[^1].Position);
        Assert.InRange(
            routeDistance,
            0,
            candidate.DurationSeconds *
                context.MaximumCameraSpeedUnitsPerSecond + 0.001);
        GameplayVector3 start = plan.Keyframes[0].Position;
        GameplayVector3 end = plan.Keyframes[^1].Position;
        foreach (CameraKeyframe keyframe in plan.Keyframes.Skip(1).SkipLast(1))
        {
            double cross =
                (keyframe.Position.X - start.X) * (end.Y - start.Y) -
                (keyframe.Position.Y - start.Y) * (end.X - start.X);
            Assert.InRange(Math.Abs(cross), 0, 0.001);
        }
    }

    [Fact]
    public void Dust2CatalogContainsVerifiedStage81Campath()
    {
        MapCameraProfile? profile =
            new MapCameraProfileCatalog().Find("de_dust2");

        Assert.NotNull(profile);
        Assert.True(profile.ManuallyVerified);
        EstablishingCameraPreset preset =
            Assert.Single(profile.EstablishingShots);
        Assert.Equal(4, preset.Keyframes.Count);
        Assert.All(
            preset.Keyframes,
            keyframe => Assert.Contains(
                profile.SafeVolumes,
                volume => volume.Contains(keyframe.Position)));
    }

    [Fact]
    public void AutomaticCalibratorBuildsTrajectoryVolumesForUnknownMap()
    {
        GameplayTimelineFrame[] frames = GameplayFrames(
            null,
            alive: true,
            freeze: false,
            nearKill: false,
            speed: 120);

        AutomaticCameraCalibrationResult result =
            new AutomaticMapCameraCalibrator().Calibrate(
                "de_newmap",
                frames,
                64);

        Assert.True(result.Profile.AutomaticallyCalibrated);
        Assert.False(result.Profile.ManuallyVerified);
        Assert.NotEmpty(result.Profile.SafeVolumes);
        Assert.Equal(
            result.Profile.SafeVolumes.Count,
            result.Profile.EstablishingShots.Count);
        Assert.All(
            frames,
            frame => Assert.Contains(
                result.Profile.SafeVolumes,
                volume => volume.Contains(frame.Player.Position)));
    }

    [Fact]
    public void AutomaticProfileProducesCalibrationSpikeCameraPlan()
    {
        GameplayTimelineFrame[] frames = GameplayFrames(
            null,
            alive: true,
            freeze: false,
            nearKill: false,
            speed: 120);
        MapCameraProfile profile = new AutomaticMapCameraCalibrator()
            .Calibrate("de_newmap", frames, 64)
            .Profile;
        BrollCandidate candidate = Broll() with
        {
            Trajectory = new PlayerTrajectory(
                frames.Select(value => new PlayerTransformSample(
                    value.Tick,
                    value.Player.Position,
                    value.Player.ViewAngles)).ToArray()),
            StartTick = frames[0].Tick,
            EndTick = frames[^1].Tick,
            DurationSeconds = (frames[^1].Tick - frames[0].Tick) / 64d
        };
        CameraPlanningContext context = VerifiedCameraContext() with
        {
            MapName = "de_newmap",
            Profile = profile
        };

        CameraShotPlan plan = new CameraPathPlanner().Create(
            candidate,
            context);

        Assert.NotEqual(CameraShotType.PlayerPov, plan.Type);
        Assert.True(plan.AutomaticCalibration);
        Assert.NotNull(plan.SafetyVolume);
        Assert.All(
            plan.Keyframes,
            keyframe => Assert.True(
                plan.SafetyVolume!.Contains(keyframe.Position)));
    }

    [Fact]
    public void AutomaticCalibratorRejectsUnobservedSpace()
    {
        GameplayTimelineFrame[] frames = GameplayFrames(
            null,
            alive: false,
            freeze: false,
            nearKill: false,
            speed: 120);

        AutomaticCameraCalibrationResult result =
            new AutomaticMapCameraCalibrator().Calibrate(
                "de_newmap",
                frames,
                64);

        Assert.False(result.Profile.AutomaticallyCalibrated);
        Assert.Empty(result.Profile.SafeVolumes);
        Assert.Empty(result.Profile.EstablishingShots);
    }

    [Fact]
    public void EmptyConfiguredCameraCatalogStillLoadsBuiltInProfiles()
    {
        MapCameraProfile? profile =
            new MapCameraProfileCatalog([]).Find("de_dust2");

        Assert.NotNull(profile);
        Assert.True(profile.ManuallyVerified);
    }

    [Fact]
    public void VerifiedDust2FallbackCampathTracksNearbyPlayer()
    {
        MapCameraProfile profile =
            Assert.IsType<MapCameraProfile>(
                new MapCameraProfileCatalog().Find("de_dust2"));
        BrollCandidate candidate = Broll() with
        {
            Trajectory = new PlayerTrajectory(
            [
                new PlayerTransformSample(
                    0,
                    new GameplayVector3(-260, 2190, -128),
                    GameplayVector3.Zero),
                new PlayerTransformSample(
                    64,
                    new GameplayVector3(-80, 2200, -128),
                    GameplayVector3.Zero),
                new PlayerTransformSample(
                    128,
                    new GameplayVector3(20, 2205, -128),
                    GameplayVector3.Zero)
            ])
        };
        CameraPlanningContext context = VerifiedCameraContext() with
        {
            MapName = "de_dust2",
            Profile = profile
        };

        CameraShotPlan plan =
            new CameraPathPlanner().Create(candidate, context);

        Assert.Equal(CameraShotType.SideTracking, plan.Type);
        Assert.Equal(4, plan.Keyframes.Count);
        Assert.All(
            plan.Keyframes,
            keyframe => Assert.Contains(
                profile.SafeVolumes,
                volume => volume.Contains(keyframe.Position)));
        Assert.Contains(
            plan.Keyframes,
            keyframe =>
                Math.Abs(keyframe.Rotation.X) > 1 ||
                Math.Abs(keyframe.Rotation.Y) > 1);
    }

    [Fact]
    public void UnsupportedMapOrUnverifiedHlaeFallsBackToPov()
    {
        CameraPlanningContext context = VerifiedCameraContext() with
        {
            Profile = null,
            Capabilities = VerifiedCameraContext().Capabilities with
            {
                ManualSpikeVerified = false
            }
        };

        CameraShotPlan plan = new CameraPathPlanner().Create(Broll(), context);

        Assert.Equal(CameraShotType.PlayerPov, plan.Type);
        Assert.Contains(
            "HLAE_CAMERA_CAPABILITY_UNVERIFIED",
            plan.Warnings);
    }

    [Fact]
    public void VeryShortBrollFallsBackToPovBeforeCampathPlanning()
    {
        BrollCandidate candidate = Broll() with
        {
            EndTick = 105,
            DurationSeconds = 0.078
        };

        CameraShotPlan plan = new CameraPathPlanner().Create(
            candidate,
            VerifiedCameraContext());

        Assert.Equal(CameraShotType.PlayerPov, plan.Type);
        Assert.Contains("CAMERA_SHOT_TOO_SHORT_FOR_CAMPATH", plan.Warnings);
    }

    [Fact]
    public void PreviewAnalyzerRejectsBlackJumpingOrInvalidCampath()
    {
        CameraShotPlan shot = CameraPathPlanner.Pov(Broll(), []) with
        {
            Type = CameraShotType.LinearCampath
        };
        CameraPreviewMetrics metrics = new(
            2,
            0.01,
            0.4,
            0.1,
            0.2,
            0.8,
            0.9,
            true);

        IReadOnlyList<string> warnings =
            new CameraShotQualityAnalyzer().Validate(shot, metrics);

        Assert.Contains("CAMERA_PREVIEW_BLACK_FRAMES", warnings);
        Assert.Contains("CAMERA_PREVIEW_ABRUPT_JUMP", warnings);
        Assert.Contains("CAMERA_CAMPATH_KEYFRAME_COUNT_INVALID", warnings);
    }

    [Fact]
    public void GroupWideWithSingleTrackedSubjectDoesNotRequireGroupCoverage()
    {
        CameraShotPlan shot = CameraPathPlanner.Pov(Broll(), []) with
        {
            Type = CameraShotType.GroupWide,
            Family = CameraShotFamily.GroupWide,
            SubjectIds = ["player-1"]
        };
        CameraPreviewMetrics metrics = new(
            2,
            0.5,
            0,
            0.2,
            0.2,
            0,
            0.1,
            true)
        {
            SubjectVisibleRatio = 1,
            SubjectCenterDistance = 0.1,
            SubjectLossDurationSeconds = 0,
            SubjectClippingRatio = 0,
            GroupCoverageRatio = 0
        };

        IReadOnlyList<string> warnings =
            new CameraShotQualityAnalyzer().Validate(shot, metrics);

        Assert.DoesNotContain("CAMERA_PREVIEW_GROUP_COVERAGE_LOW", warnings);
    }

    [Fact]
    public void StrongestHighlightReceivesStrongestPeakDeterministically()
    {
        MusicExcerptPlan excerpt = ValidExcerpt();
        SelectedHighlight ace = Highlight("ace", HighlightType.Ace, 4, 80);
        SelectedHighlight solo = Highlight("solo", HighlightType.SoloKill, 4, 20);
        HighlightPeakMatcher matcher = new(
            new HighlightImportanceCalculator());

        HighlightPeakMatchPlan first = matcher.Match(
            [solo, ace],
            excerpt,
            new HighlightPeakMatchingOptions());
        HighlightPeakMatchPlan second = matcher.Match(
            [solo, ace],
            excerpt,
            new HighlightPeakMatchingOptions());

        HighlightPeakMatch aceMatch =
            first.Matches.Single(value => value.HighlightId == "ace");
        Assert.Equal(MusicalPeakType.DropStart, aceMatch.Peak.Type);
        Assert.Equal(
            first.Matches.Select(value => value.Peak.Id),
            second.Matches.Select(value => value.Peak.Id));
    }

    [Fact]
    public void MotivatedEffectsNeverUseRandomReasonAndPreferCamera()
    {
        MotivatedEffectPlanner planner = new();
        MusicSection section = DetailedSection(
            "drop",
            MusicSectionType.Drop,
            6,
            12,
            0.9);
        HighlightPeakMatch match = Match("h", DropPeak("drop", 8, 1));

        IReadOnlyList<MotivatedEffectDirective> pov = planner.Plan(
            CinematicSequenceRole.PeakHighlight,
            section,
            match,
            CameraPathPlanner.Pov(Broll(), []),
            4,
            new CinematicEffectPolicy(),
            finalHighlight: true);
        CameraShotPlan moving = CameraPathPlanner.Pov(Broll(), []) with
        {
            Type = CameraShotType.LinearCampath,
            Keyframes = VerifiedCameraPath()
        };
        IReadOnlyList<MotivatedEffectDirective> camera = planner.Plan(
            CinematicSequenceRole.Highlight,
            section,
            match,
            moving,
            4,
            new CinematicEffectPolicy(),
            finalHighlight: false);
        IReadOnlyList<MotivatedEffectDirective> calm = planner.Plan(
            CinematicSequenceRole.Highlight,
            section,
            match,
            CameraPathPlanner.Pov(Broll(), []),
            4,
            new CinematicEffectPolicy
            {
                MaximumVisibleFilterEffectsPerHighlight = 0
            },
            finalHighlight: false);
        IReadOnlyList<IReadOnlyList<MotivatedEffectDirective>> treatments =
            Enumerable.Range(0, 7)
                .Select(index => planner.Plan(
                    CinematicSequenceRole.Highlight,
                    section,
                    match,
                    CameraPathPlanner.Pov(Broll(), []),
                    4,
                    new CinematicEffectPolicy
                    {
                        MaximumVisibleFilterEffectsPerHighlight = 5
                    },
                    finalHighlight: false,
                    sequenceIndex: index))
                .ToArray();

        Assert.Single(pov);
        Assert.Equal(MotivatedEffectReason.FinalKill, pov[0].Reason);
        Assert.Empty(camera);
        Assert.Empty(calm);
        Assert.All(treatments, value => Assert.Single(value));
        Assert.All(treatments, value =>
            Assert.Equal("PunchZoom", value[0].EffectType));
        Assert.Single(treatments
            .Select(value => value[0].EffectType)
            .Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void DirectorPlacesCalmBrollAndKillsOnlyInHighEnergy()
    {
        MusicNarrative narrative = ExcerptNarrative();
        MusicExcerptPlan excerpt = ValidExcerpt();
        SelectedHighlight[] highlights =
        [
            Highlight("h1", HighlightType.DoubleKill, 4, 30),
            Highlight("h2", HighlightType.Ace, 4, 80)
        ];

        CinematicMoviePlan plan = Director().Create(
            narrative,
            excerpt,
            highlights,
            [Broll()],
            DirectorOptions());

        Assert.Contains(plan.Segments, value =>
            value.BrollCandidateId is not null);
        Assert.All(
            plan.Segments.Where(value => value.HighlightId is not null),
            value =>
            {
                MusicSection section = narrative.Sections.Single(
                    item => item.Id == value.MusicSectionId);
                Assert.True(
                    MusicalPeakDetector.IsAllowedPrimaryKillSection(
                        section.Type));
            });
        Assert.True(plan.TargetDurationSeconds <= 16);
        Assert.True(plan.Segments
            .Where(value => value.BrollCandidateId is not null)
            .Sum(value => value.OutputEndSeconds - value.OutputStartSeconds) <= 8);
        CinematicSequenceSegment firstHighlight = plan.Segments
            .Where(value => value.HighlightId is not null)
            .OrderBy(value => value.OutputStartSeconds)
            .First();
        CinematicSequenceSegment lastHighlight = plan.Segments
            .Where(value => value.HighlightId is not null)
            .OrderBy(value => value.OutputStartSeconds)
            .Last();
        Assert.True(firstHighlight.OutputStartSeconds > 0);
        Assert.Equal("h2", lastHighlight.HighlightId);
        Assert.Equal(16, plan.TargetDurationSeconds, 6);
        Assert.True(
            plan.TargetDurationSeconds >= lastHighlight.OutputEndSeconds);
        Assert.DoesNotContain(
            plan.Segments,
            value =>
                value.BrollCandidateId is not null &&
                value.OutputStartSeconds >=
                    lastHighlight.OutputEndSeconds - 0.000001);
        Assert.All(
            plan.Segments.Where(value =>
                value.OutputEndSeconds <=
                    firstHighlight.OutputStartSeconds + 0.000001),
            value => Assert.Null(value.HighlightId));
        Assert.All(
            plan.Segments.Where(value => value.BrollCandidateId is not null),
            value => Assert.True(
                value.OutputEndSeconds - value.OutputStartSeconds >= 1.5));
    }

    [Fact]
    public void DirectorUsesMusicMotivatedSlowMotionOnlyOnHighlightFireWindow()
    {
        MusicNarrative narrative = ExcerptNarrative() with
        {
            Frames = Enumerable.Range(0, 81)
                .Select(index => Frame(
                    Math.Max(0, 1 - index / 70d),
                    0.5,
                    0.95,
                    0.2,
                    0.3,
                    0.2,
                    0.2) with
                {
                    TimeSeconds = index * 0.25
                })
                .ToArray()
        };

        CinematicMoviePlan plan = Director().Create(
            narrative,
            ValidExcerpt(),
            [
                Highlight("h1", HighlightType.DoubleKill, 4, 30),
                Highlight("h2", HighlightType.Ace, 4, 80)
            ],
            [Broll()],
            DirectorOptions());

        CinematicSequenceSegment broll = Assert.Single(plan.Segments.Where(
            value => value.BrollCandidateId is not null));
        Assert.False(broll.TimeWarp.UsesLocalRamp);
        Assert.DoesNotContain(
            "MUSIC_ENERGY_CHANGE_FIRE_SLOW_MOTION",
            broll.TimeWarp.Warnings);
        CinematicSequenceSegment highlight = Assert.Single(
            plan.Segments.Where(value => value.HighlightId == "h1"));
        Assert.True(highlight.TimeWarp.UsesLocalRamp);
        Assert.Contains(
            "MUSIC_ENERGY_CHANGE_FIRE_SLOW_MOTION",
            highlight.TimeWarp.Warnings);
        Assert.Contains(highlight.TimeWarp.Segments, value => value.Speed < 1);
    }

    [Fact]
    public void ExplicitFortyFiveSecondDirectorFillsLongGapsWithBroll()
    {
        MusicSection[] sections =
        [
            DetailedSection("intro-45", MusicSectionType.Intro, 0, 6, 0.2),
            DetailedSection("build-45", MusicSectionType.BuildUp, 6, 10, 0.5),
            DetailedSection("drop-45", MusicSectionType.Drop, 10, 40, 0.9),
            DetailedSection("outro-45", MusicSectionType.Outro, 40, 45, 0.2)
        ];
        MusicalPeak[] peaks =
        [
            DropPeak("drop-45", 15, 1),
            DropPeak("drop-45", 35, 0.9)
        ];
        MusicNarrative narrative = new()
        {
            DurationSeconds = 60,
            Sections = sections,
            Peaks = peaks,
            Frames = [],
            Warnings = []
        };
        MusicExcerptPlan excerpt = new()
        {
            StartSeconds = 0,
            EndSeconds = 45,
            SectionIds = sections.Select(value => value.Id).ToArray(),
            Peaks = peaks,
            RequiredPeakCount = 2,
            UsablePeakCount = 2,
            Score = 100,
            IsValid = true,
            ScoreBreakdown = new Dictionary<string, double>(),
            Warnings = []
        };
        BrollCandidate[] broll = Enumerable.Range(0, 20)
            .Select(index => Broll() with
            {
                Id = $"broll-long-{index:D2}",
                StartTick = index * 256,
                EndTick = index * 256 + 192
            })
            .ToArray();

        CinematicMoviePlan plan = Director().Create(
            narrative,
            excerpt,
            [
                Highlight("h1", HighlightType.DoubleKill, 4, 30),
                Highlight("h2", HighlightType.Ace, 4, 80)
            ],
            broll,
            DirectorOptions() with
            {
                Duration = new MovieDurationOptions
                {
                    Selection = MovieDurationSelection.Seconds45
                }
            });

        CinematicSequenceSegment[] ordered = plan.Segments
            .OrderBy(value => value.OutputStartSeconds)
            .ToArray();
        Assert.Equal(45, plan.TargetDurationSeconds, 6);
        Assert.Equal(0, ordered[0].OutputStartSeconds, 6);
        Assert.Equal(45, ordered[^1].OutputEndSeconds, 6);
        Assert.DoesNotContain(
            plan.Warnings,
            value => value.StartsWith(
                "CINEMATIC_TIMELINE_GAP:",
                StringComparison.Ordinal));
        Assert.True(ordered.Count(value =>
            value.BrollCandidateId is not null) >= 8);
        Assert.All(
            ordered.Where(value => value.BrollCandidateId is not null),
            value =>
            {
                Assert.NotEqual(CameraShotType.PlayerPov, value.Camera.Type);
                Assert.NotEqual(
                    CameraShotFamily.PlayerPov,
                    value.Camera.Family);
                Assert.True(
                    value.OutputEndSeconds - value.OutputStartSeconds >= 1.5);
            });
    }

    [Fact]
    public void ExplicitDirectorDistributesKillsAcrossTheWholeMovie()
    {
        MusicSection section = DetailedSection(
            "drop-spread",
            MusicSectionType.Drop,
            0,
            45,
            0.9);
        MusicalPeak[] peaks = Enumerable.Range(0, 39)
            .Select(index => new MusicalPeak
            {
                Id = $"spread-peak-{index:D2}",
                Type = MusicalPeakType.BassImpact,
                TimeSeconds = 4 + index,
                Strength = 1 - index * 0.01,
                Confidence = 0.9,
                SectionId = section.Id
            })
            .ToArray();
        MusicNarrative narrative = new()
        {
            DurationSeconds = 45,
            Sections = [section],
            Peaks = peaks,
            Frames = [],
            Warnings = []
        };
        MusicExcerptPlan excerpt = new()
        {
            StartSeconds = 0,
            EndSeconds = 45,
            SectionIds = [section.Id],
            Peaks = peaks,
            RequiredPeakCount = 8,
            UsablePeakCount = peaks.Length,
            Score = 100,
            IsValid = true,
            ScoreBreakdown = new Dictionary<string, double>(),
            Warnings = []
        };

        CinematicMoviePlan plan = Director().Create(
            narrative,
            excerpt,
            Enumerable.Range(0, 8)
                .Select(index => Highlight(
                    $"spread-h{index}",
                    HighlightType.SoloKill,
                    2,
                    30 - index))
                .ToArray(),
            Enumerable.Range(0, 24)
                .Select(index => Broll() with
                {
                    Id = $"spread-broll-{index:D2}",
                    StartTick = index * 256,
                    EndTick = index * 256 + 192
                })
                .ToArray(),
            DirectorOptions() with
            {
                Duration = new MovieDurationOptions
                {
                    Selection = MovieDurationSelection.Seconds45
                }
            });

        double[] kills = plan.HighlightMatches
            .OrderBy(value => value.PlannedKillSeconds)
            .Select(value => value.PlannedKillSeconds)
            .ToArray();
        Assert.Equal(8, kills.Length);
        Assert.InRange(kills[0], 3, 8);
        Assert.InRange(kills[^1], 36, 42);
        Assert.All(kills.Zip(kills.Skip(1)), pair =>
            Assert.InRange(pair.Second - pair.First, 3, 8));
    }

    [Fact]
    public void RelaxedDirectorSelectsTimelineSafePeaksForEveryHighlight()
    {
        MusicSection verse = DetailedSection(
            "verse",
            MusicSectionType.Verse,
            0,
            25,
            0.7);
        MusicalPeak[] peaks =
        [
            .. Enumerable.Range(0, 5).Select(index => new MusicalPeak
            {
                Id = $"cluster-{index}",
                Type = MusicalPeakType.Downbeat,
                TimeSeconds = 1 + index * 0.5,
                Strength = 0.95,
                Confidence = 0.95,
                SectionId = verse.Id
            }),
            .. Enumerable.Range(0, 5).Select(index =>
                new MusicalPeak
                {
                    Id = $"safe-{index}",
                    Type = MusicalPeakType.StrongBeat,
                    TimeSeconds = 5 + index * 4,
                    Strength = 0.7,
                    Confidence = 0.8,
                    SectionId = verse.Id
                })
        ];
        MusicNarrative narrative = new()
        {
            DurationSeconds = 25,
            Sections = [verse],
            Peaks = peaks,
            Frames = [],
            Warnings = []
        };
        MusicExcerptPlan excerpt = new()
        {
            StartSeconds = 0,
            EndSeconds = 25,
            SectionIds = [verse.Id],
            Peaks = peaks,
            RequiredPeakCount = 5,
            UsablePeakCount = peaks.Length,
            Score = 1,
            IsValid = true,
            ScoreBreakdown = new Dictionary<string, double>(),
            Warnings = [MusicExcerptSelector.RelaxedEnergyFallbackWarning]
        };
        SelectedHighlight[] highlights = Enumerable.Range(0, 5)
            .Select(index => Highlight(
                    $"h{index}",
                    HighlightType.SoloKill,
                    3,
                    30 - index) with
                {
                    SelectionOrder = index
                })
            .ToArray();

        CinematicMoviePlan plan = Director().Create(
            narrative,
            excerpt,
            highlights,
            Enumerable.Range(0, 12)
                .Select(index => Broll() with
                {
                    Id = $"timeline-broll-{index}"
                })
                .ToArray(),
            DirectorOptions());

        Assert.Equal(highlights.Length, plan.HighlightMatches.Count);
        Assert.Equal(
            highlights.Length,
            plan.Segments.Count(value => value.HighlightId is not null));
        Assert.Contains("HIGHLIGHT_PEAK_TIMELINE_FALLBACK", plan.Warnings);
        Assert.Contains(
            plan.Warnings,
            value => value.StartsWith(
                "CINEMATIC_TIMELINE_GAP:",
                StringComparison.Ordinal));
    }

    [Fact]
    public void MicroGapSnappingDoesNotDiscardTimelineSafeHighlight()
    {
        MusicSection verse = DetailedSection(
            "verse",
            MusicSectionType.Verse,
            0,
            56,
            0.7);
        double[] peakTimes =
        [
            0,
            7.36,
            10.48,
            13.64,
            15.76,
            17.92,
            21.28,
            24.72,
            30.48,
            41.36,
            52.64
        ];
        MusicalPeak[] peaks = peakTimes
            .Select((time, index) => new MusicalPeak
            {
                Id = $"peak-{index:D2}",
                Type = MusicalPeakType.StrongBeat,
                TimeSeconds = time,
                Strength = 0.8,
                Confidence = 0.9,
                SectionId = verse.Id
            })
            .ToArray();
        MusicNarrative narrative = new()
        {
            DurationSeconds = 56,
            Sections = [verse],
            Peaks = peaks,
            Frames = [],
            Warnings = []
        };
        MusicExcerptPlan excerpt = new()
        {
            StartSeconds = 0,
            EndSeconds = 55.469,
            SectionIds = [verse.Id],
            Peaks = peaks,
            RequiredPeakCount = 11,
            UsablePeakCount = peaks.Length,
            Score = 1,
            IsValid = true,
            ScoreBreakdown = new Dictionary<string, double>(),
            Warnings = [MusicExcerptSelector.RelaxedEnergyFallbackWarning]
        };
        (HighlightType Type, double Duration, double KillOffset)[] timing =
        [
            (HighlightType.SoloKill, 2, 1),
            (HighlightType.SoloKill, 2, 1),
            (HighlightType.SoloKill, 2, 1),
            (HighlightType.SoloKill, 2, 1),
            (HighlightType.SoloKill, 2, 1),
            (HighlightType.SoloKill, 2, 1),
            (HighlightType.DoubleKill, 3.140625, 2.140625),
            (HighlightType.DoubleKill, 3.171875, 2.171875),
            (HighlightType.DoubleKill, 5.265625, 4.265625),
            (HighlightType.TripleKill, 10.84375, 9.84375),
            (HighlightType.QuadKill, 11.046875, 10.046875)
        ];
        SelectedHighlight[] highlights = timing
            .Select((value, index) =>
                Highlight(
                    $"h{index:D2}",
                    value.Type,
                    value.Duration,
                    100 - index) with
                {
                    Bounds = new SafeClipBounds(
                        0,
                        0,
                        value.KillOffset,
                        value.KillOffset,
                        value.Duration,
                        value.Duration),
                    SelectionOrder = index + 1
                })
            .ToArray();

        CinematicMoviePlan plan = Director().Create(
            narrative,
            excerpt,
            highlights,
            [],
            DirectorOptions());

        Assert.Equal(highlights.Length, plan.HighlightMatches.Count);
        Assert.Equal(
            highlights.Length,
            plan.Segments.Count(value => value.HighlightId is not null));
        Assert.DoesNotContain(
            plan.Warnings,
            value => value.StartsWith(
                "HIGHLIGHT_PEAK_SPACING_INSUFFICIENT:",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RelaxedDirectorKeepsIntroCombatFreeAndStartsWithSoloKill()
    {
        MusicSection intro = DetailedSection(
            "intro",
            MusicSectionType.Intro,
            0,
            4,
            0.2);
        MusicSection verse = DetailedSection(
            "verse",
            MusicSectionType.Verse,
            4,
            20,
            0.7);
        MusicalPeak[] peaks =
        [
            new MusicalPeak
            {
                Id = "first-impact",
                Type = MusicalPeakType.StrongBeat,
                TimeSeconds = 5.2,
                Strength = 0.8,
                Confidence = 0.9,
                SectionId = verse.Id
            },
            new MusicalPeak
            {
                Id = "middle-impact",
                Type = MusicalPeakType.Downbeat,
                TimeSeconds = 9,
                Strength = 0.8,
                Confidence = 0.9,
                SectionId = verse.Id
            },
            new MusicalPeak
            {
                Id = "hero-impact",
                Type = MusicalPeakType.BassImpact,
                TimeSeconds = 14,
                Strength = 0.9,
                Confidence = 0.9,
                SectionId = verse.Id
            }
        ];
        MusicNarrative narrative = new()
        {
            DurationSeconds = 20,
            Sections = [intro, verse],
            Peaks = peaks,
            Frames = [],
            Warnings = []
        };
        MusicExcerptPlan excerpt = new()
        {
            StartSeconds = 0,
            EndSeconds = 20,
            SectionIds = [intro.Id, verse.Id],
            Peaks = peaks,
            RequiredPeakCount = 2,
            UsablePeakCount = peaks.Length,
            Score = 1,
            IsValid = true,
            ScoreBreakdown = new Dictionary<string, double>(),
            Warnings = [MusicExcerptSelector.RelaxedEnergyFallbackWarning]
        };
        SelectedHighlight multi = Highlight(
            "multi",
            HighlightType.DoubleKill,
            6,
            80) with
        {
            Bounds = new SafeClipBounds(0, 0, 5, 5, 6, 6),
            SelectionOrder = 1
        };
        SelectedHighlight solo = Highlight(
            "solo",
            HighlightType.SoloKill,
            2,
            50) with
        {
            SelectionOrder = 2
        };

        CinematicMoviePlan plan = Director().Create(
            narrative,
            excerpt,
            [multi, solo],
            Enumerable.Range(0, 6)
                .Select(index => Broll() with
                {
                    Id = $"intro-broll-{index}"
                })
                .ToArray(),
            DirectorOptions());

        CinematicSequenceSegment firstHighlight = plan.Segments
            .Where(value => value.HighlightId is not null)
            .OrderBy(value => value.OutputStartSeconds)
            .First();
        Assert.Equal("solo", firstHighlight.HighlightId);
        Assert.True(firstHighlight.OutputStartSeconds >= 4);
        Assert.All(
            plan.Segments.Where(value =>
                value.OutputStartSeconds <
                firstHighlight.OutputStartSeconds),
            value => Assert.Null(value.HighlightId));
        Assert.Contains(
            plan.Segments,
            value =>
                value.Role == CinematicSequenceRole.Intro &&
                value.Camera.Type != CameraShotType.PlayerPov);
    }

    [Fact]
    public void RelaxedDirectorFitsEightSolosAndTwoLongMultikills()
    {
        MusicSection intro = DetailedSection(
            "intro",
            MusicSectionType.Intro,
            0,
            4.88,
            0.1);
        MusicSection verse = DetailedSection(
            "verse",
            MusicSectionType.Verse,
            4.88,
            39.375,
            0.5);
        double[] peakTimes =
        [
            6.04,
            9.36,
            11.72,
            14.04,
            16.84,
            18.92,
            21.64,
            23.64,
            29.68,
            38.08
        ];
        MusicalPeak[] peaks = peakTimes
            .Select((time, index) => new MusicalPeak
            {
                Id = $"peak-{index:D2}",
                Type = index % 2 == 0
                    ? MusicalPeakType.BassImpact
                    : MusicalPeakType.Downbeat,
                TimeSeconds = time,
                Strength = index switch
                {
                    5 => 0.28,
                    8 => 0.32,
                    _ => 0.55
                },
                Confidence = 0.8,
                SectionId = verse.Id
            })
            .ToArray();
        MusicNarrative narrative = new()
        {
            DurationSeconds = 39.375,
            Sections = [intro, verse],
            Peaks = peaks,
            Frames = [],
            Warnings = []
        };
        MusicExcerptPlan excerpt = new()
        {
            StartSeconds = 0,
            EndSeconds = 39.375,
            SectionIds = [intro.Id, verse.Id],
            Peaks = peaks,
            RequiredPeakCount = 10,
            UsablePeakCount = peaks.Length,
            Score = 1,
            IsValid = true,
            ScoreBreakdown = new Dictionary<string, double>(),
            Warnings = [MusicExcerptSelector.RelaxedEnergyFallbackWarning]
        };
        List<SelectedHighlight> highlights =
        [
            Highlight(
                "long-multi",
                HighlightType.DoubleKill,
                7.8,
                100) with
            {
                Bounds = new SafeClipBounds(
                    0,
                    0,
                    6.797,
                    6.797,
                    7.8,
                    7.8),
                SelectionOrder = 1
            },
            Highlight(
                "short-multi",
                HighlightType.DoubleKill,
                5.6,
                90) with
            {
                Bounds = new SafeClipBounds(
                    0,
                    0,
                    4.578,
                    4.578,
                    5.6,
                    5.6),
                SelectionOrder = 3
            }
        ];
        highlights.AddRange(
            Enumerable.Range(0, 8)
                .Select(index => Highlight(
                        $"solo-{index}",
                        HighlightType.SoloKill,
                        2,
                        80 - index) with
                    {
                        SelectionOrder = index + 2
                    }));

        CinematicMoviePlan plan = Director().Create(
            narrative,
            excerpt,
            highlights,
            Enumerable.Range(0, 20)
                .Select(index => Broll() with
                {
                    Id = $"broll-{index:D2}"
                })
                .ToArray(),
            DirectorOptions());

        Assert.Equal(10, plan.HighlightMatches.Count);
        CinematicSequenceSegment[] combat = plan.Segments
            .Where(value => value.HighlightId is not null)
            .OrderBy(value => value.OutputStartSeconds)
            .ToArray();
        Assert.Equal(10, combat.Length);
        Assert.True(combat[0].OutputStartSeconds >= 4.88);
        Assert.Equal("long-multi", combat[^1].HighlightId);
        Assert.True(combat[^1].OutputEndSeconds <= 39.375);
        Assert.Contains(
            plan.Warnings,
            value => value.StartsWith(
                "CINEMATIC_TIMELINE_GAP:",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DirectorDoesNotRepeatSourceIntervalsToHideTimelineGaps()
    {
        SelectedHighlight[] highlights =
        [
            Highlight("h1", HighlightType.DoubleKill, 4, 30),
            Highlight("h2", HighlightType.Ace, 4, 80)
        ];
        CinematicMoviePlan plan = Director().Create(
            ExcerptNarrative(),
            ValidExcerpt(),
            highlights,
            [
                Broll() with { Id = "broll-1" },
                Broll() with { Id = "broll-2" },
                Broll() with { Id = "broll-3" }
            ],
            DirectorOptions());
        CinematicSequenceSegment[] ordered = plan.Segments
            .OrderBy(value => value.OutputStartSeconds)
            .ToArray();

        Assert.Equal(0, ordered[0].OutputStartSeconds, 6);
        Assert.Single(ordered.Where(value =>
            value.BrollCandidateId is not null));
        Assert.Contains(
            plan.Warnings,
            value => value.StartsWith(
                "CINEMATIC_TIMELINE_GAP:",
                StringComparison.Ordinal));
        CinematicSequenceSegment finalHighlight = ordered
            .Where(value => value.HighlightId is not null)
            .Last();
        Assert.Equal(
            MotivatedEffectReason.FinalKill,
            Assert.Single(finalHighlight.Effects).Reason);
    }

    [Fact]
    public void DirectorReportsTimelineGapInsteadOfInventingBroll()
    {
        CinematicMoviePlan plan = Director().Create(
            ExcerptNarrative(),
            ValidExcerpt(),
            [
                Highlight("h1", HighlightType.DoubleKill, 4, 30),
                Highlight("h2", HighlightType.Ace, 4, 80)
            ],
            [Broll()],
            DirectorOptions());

        Assert.Contains(
            plan.Warnings,
            value => value.StartsWith(
                "CINEMATIC_TIMELINE_GAP:",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DirectorCompactsTimelineWhenWebFlowAllowsMusicTrim()
    {
        CinematicMoviePlan plan = Director().Create(
            ExcerptNarrative(),
            ValidExcerpt(),
            [
                Highlight("h1", HighlightType.DoubleKill, 4, 30),
                Highlight("h2", HighlightType.Ace, 4, 80)
            ],
            [Broll()],
            DirectorOptions() with
            {
                CompactTimelineWhenMaterialIsInsufficient = true
            });

        CinematicSequenceSegment[] ordered = plan.Segments
            .OrderBy(value => value.OutputStartSeconds)
            .ToArray();
        Assert.Equal(0, ordered[0].OutputStartSeconds, 6);
        Assert.All(
            ordered.Zip(ordered.Skip(1)),
            pair => Assert.Equal(
                pair.First.OutputEndSeconds,
                pair.Second.OutputStartSeconds,
                6));
        Assert.Equal(ordered[^1].OutputEndSeconds, plan.TargetDurationSeconds, 6);
        Assert.Contains(
            "CINEMATIC_TIMELINE_COMPACTED_FOR_AVAILABLE_MATERIAL",
            plan.Warnings);
        Assert.Contains("MUSIC_TRIMMED_TO_COMPACT_TIMELINE", plan.Warnings);
        Assert.DoesNotContain(
            plan.Warnings,
            value => value.StartsWith(
                "CINEMATIC_TIMELINE_GAP:",
                StringComparison.Ordinal));
    }

    [Fact]
    public void DirectorRejectsHighlightThatWouldOverrunExcerpt()
    {
        MusicExcerptPlan shortExcerpt = ValidExcerpt() with
        {
            EndSeconds = 16,
            SectionIds = ["build", "drop"]
        };

        CinematicMoviePlan plan = Director().Create(
            ExcerptNarrative(),
            shortExcerpt,
            [
                Highlight("h1", HighlightType.DoubleKill, 4, 30),
                Highlight("h2", HighlightType.Ace, 4, 80)
            ],
            [
                Broll() with { Id = "broll-1" },
                Broll() with { Id = "broll-2" },
                Broll() with { Id = "broll-3" }
            ],
            DirectorOptions());

        Assert.Contains(
            "HIGHLIGHT_EXCEEDS_MUSIC_EXCERPT:h1",
            plan.Warnings);
        Assert.Single(plan.HighlightMatches);
        Assert.All(
            plan.Segments,
            value => Assert.True(
                value.OutputEndSeconds <=
                shortExcerpt.DurationSeconds + 0.001));
    }

    [Fact]
    public void CinematicMusicAdapterUsesExcerptRelativePeakTimes()
    {
        SelectedHighlight[] highlights =
        [
            Highlight("h1", HighlightType.DoubleKill, 4, 30),
            Highlight("h2", HighlightType.Ace, 4, 80)
        ];
        CinematicMoviePlan cinematic = Director().Create(
            ExcerptNarrative(),
            ValidExcerpt(),
            highlights,
            [
                Broll() with { Id = "broll-1" },
                Broll() with { Id = "broll-2" },
                Broll() with { Id = "broll-3" }
            ],
            DirectorOptions());

        MusicEditPlan plan = new CinematicMusicEditPlanAdapter().Create(
            "generation",
            "track.wav",
            cinematic,
            highlights);

        Assert.Equal(4, plan.MusicStartSeconds, 6);
        Assert.Equal(16, plan.MusicDurationSeconds, 6);
        Assert.Equal(
            cinematic.HighlightMatches
                .OrderBy(value => value.PlannedPeakSeconds)
                .Select(value => value.PlannedPeakSeconds),
            plan.Segments
                .OrderBy(value => value.OutputStartSeconds)
                .Select(value =>
                    value.TargetMusicAnchor!.TimeSeconds));
    }

    [Fact]
    public void CinematicRetimingPreservesSafeEndAndPostKillSpeed()
    {
        SelectedHighlight highlight = Highlight(
            "h1",
            HighlightType.TripleKill,
            8,
            50);
        HighlightPeakMatch match = Match(
            "h1",
            DropPeak("drop", 5.1, 1));

        TimeWarpPlan plan = new CinematicTimeWarpPolicy(
            new TimeWarpPlanner()).Create(
                highlight,
                match,
                0,
                new CinematicTimeWarpOptions());

        double killOffset =
            highlight.Bounds.PrimaryKillSeconds -
            highlight.Bounds.SafeStartSeconds;
        Assert.Equal(
            highlight.Bounds.SafeEndSeconds -
            highlight.Bounds.SafeStartSeconds,
            plan.Segments.Max(value => value.SourceEndSeconds),
            6);
        Assert.All(
            plan.Segments.Where(value =>
                value.SourceStartSeconds >= killOffset),
            value => Assert.True(value.Speed <= 1.05));
        Assert.Contains(plan.Segments, value => value.Speed < 0.8);
        Assert.Contains(plan.Segments, value => value.Speed > 1.1);
        Assert.Equal(
            killOffset,
            TimeWarpMath.MapSourceTime(plan, killOffset),
            6);
        Assert.Equal(
            highlight.Bounds.SafeEndSeconds -
            highlight.Bounds.SafeStartSeconds,
            TimeWarpMath.OutputDuration(
                plan,
                highlight.Bounds.SafeEndSeconds -
                highlight.Bounds.SafeStartSeconds),
            6);
    }

    private static MusicSectionClassifier Classifier() =>
        new(new MusicalAnchorBuilder());

    private static MusicNarrativeAnalyzer NarrativeAnalyzer() =>
        new(Classifier(), new MusicalPeakDetector());

    private static MusicAnalysis PatternMusic(string pattern)
    {
        Func<int, MusicFrame> frame = pattern switch
        {
            "calm" => _ => Frame(0.18, 0.10, 0.05, 0.08, 0.1, 0.1, 0.05),
            "build" => index => Frame(
                index < 10 ? 0.20 : 0.65,
                0.20,
                0.65,
                0.45,
                0.75,
                0.55,
                0.35),
            "drop" => index => Frame(
                index < 10 ? 0.20 : 0.90,
                0.90,
                0.85,
                0.75,
                0.85,
                0.85,
                0.45),
            "high" => _ => Frame(0.85, 0.75, 0.65, 0.55, 0.65, 0.45, 0.35),
            _ => index => Frame(
                index < 10 ? 0.80 : 0.20,
                0.20,
                0.10,
                0.20,
                0.10,
                0.25,
                0.80)
        };
        IReadOnlyList<MusicBeat> downbeats = pattern == "drop"
            ? [new MusicBeat(1, 5, 1, 0.9)]
            : [];
        return Analysis(
            [RawSection(1, 5, 9, 0.5)],
            Frames(5, 9, frame),
            downbeats: downbeats);
    }

    private static MusicFrame Frame(
        double energy,
        double bass,
        double onset,
        double flux,
        double rhythm,
        double novelty,
        double harmonic) =>
        new()
        {
            TimeSeconds = 0,
            Energy = energy,
            BassEnergy = bass,
            OnsetStrength = onset,
            SpectralFlux = flux,
            SpectralBrightness = flux,
            Novelty = novelty,
            RhythmicDensity = rhythm,
            HarmonicChange = harmonic
        };

    private static MusicFrame[] Frames(
        double start,
        double end,
        Func<int, MusicFrame> factory)
    {
        const int count = 20;
        return Enumerable.Range(0, count)
            .Select(index => factory(index) with
            {
                TimeSeconds =
                    start + (end - start) * index / count
            })
            .ToArray();
    }

    private static MusicSection RawSection(
        int index,
        double start,
        double end,
        double energy) =>
        new(index, start, end, "fixture", energy)
        {
            Id = $"s{index}"
        };

    private static MusicAnalysis Analysis(
        IReadOnlyList<MusicSection> sections,
        IReadOnlyList<MusicFrame> frames,
        IReadOnlyList<MusicBeat>? beats = null,
        IReadOnlyList<MusicBeat>? downbeats = null) =>
        new(
            "2.0",
            new MusicAnalyzerInfo("fixture", "2", "test"),
            new MusicAudioInfo("track.wav", 60, 48000, 2, 120, 0.9, null),
            beats ?? [],
            downbeats ?? [],
            [],
            sections,
            [],
            [])
        {
            Frames = frames
        };

    private static MusicNarrative ExcerptNarrative(double duration = 30)
    {
        MusicSection[] sections =
        [
            DetailedSection("intro", MusicSectionType.Intro, 0, 4, 0.2),
            DetailedSection("build", MusicSectionType.BuildUp, 4, 8, 0.55),
            DetailedSection("drop", MusicSectionType.Drop, 8, 16, 0.9),
            DetailedSection("outro", MusicSectionType.Outro, 16, 20, 0.2)
        ];
        return new MusicNarrative
        {
            DurationSeconds = duration,
            Sections = sections,
            Peaks =
            [
                DropPeak("drop", 10, 1),
                new MusicalPeak
                {
                    Id = "strong-1",
                    Type = MusicalPeakType.Downbeat,
                    TimeSeconds = 14,
                    Strength = 0.85,
                    Confidence = 0.9,
                    SectionId = "drop"
                }
            ],
            Frames = [],
            Warnings = []
        };
    }

    private static MusicSection DetailedSection(
        string id,
        MusicSectionType type,
        double start,
        double end,
        double energy) =>
        new(1, start, end, type.ToString(), energy)
        {
            Id = id,
            Type = type,
            BassEnergy = energy,
            RhythmicDensity = energy,
            Confidence = 0.9,
            Anchors =
            [
                new MusicalAnchor(
                    $"{id}-boundary",
                    MusicalAnchorType.SectionBoundary,
                    start,
                    energy,
                    0.9)
            ]
        };

    private static MusicalPeak DropPeak(
        string section,
        double time,
        double strength) =>
        new()
        {
            Id = $"drop-{time}",
            Type = MusicalPeakType.DropStart,
            TimeSeconds = time,
            Strength = strength,
            Confidence = 0.95,
            SectionId = section
        };

    private static MusicExcerptPlan ValidExcerpt() =>
        new()
        {
            StartSeconds = 4,
            EndSeconds = 20,
            SectionIds = ["build", "drop", "outro"],
            Peaks =
            [
                DropPeak("drop", 10, 1),
                new MusicalPeak
                {
                    Id = "secondary",
                    Type = MusicalPeakType.StrongBeat,
                    TimeSeconds = 14,
                    Strength = 0.75,
                    Confidence = 0.9,
                    SectionId = "drop"
                }
            ],
            RequiredPeakCount = 2,
            UsablePeakCount = 2,
            Score = 100,
            IsValid = true,
            ScoreBreakdown = new Dictionary<string, double>(),
            Warnings = []
        };

    private static SelectedHighlight Highlight(
        string id,
        HighlightType type,
        double duration,
        double importance)
    {
        HighlightCandidate candidate = new(
            id,
            type,
            "player",
            "Player",
            1,
            64,
            64,
            0,
            (long)(duration * 64),
            type == HighlightType.SoloKill ? 1 : 3,
            1,
            importance,
            new ScoreBreakdown(
                importance,
                0,
                0,
                0,
                0,
                0,
                importance),
            [1],
            [])
        {
            SourceDemoId = "demo",
            TickRate = 64,
            PrimaryKillTick = 64,
            SafeEndTick = (long)(duration * 64),
            BeautyScore = importance,
            Kills =
            [
                new KillDescriptor(
                    1,
                    64,
                    "player",
                    "victim",
                    "ak47",
                    true)
            ]
        };
        return new SelectedHighlight(
            id,
            candidate,
            new SafeClipBounds(0, 0, 1, 1, duration, duration),
            1);
    }

    private static GameplayTimelineFrame[] GameplayFrames(
        string? eventType,
        bool alive,
        bool freeze,
        bool nearKill,
        double speed) =>
        Enumerable.Range(0, 7)
            .Select(index => new GameplayTimelineFrame(
                100 + index * 32,
                1,
                new PlayerTransform(
                    "player",
                    new GameplayVector3(index * 40, 0, 64),
                    new GameplayVector3(speed, 0, 0),
                    new GameplayVector3(0, 0, 0)),
                speed,
                speed <= 0 ? 0.01 : 0.25,
                alive,
                freeze,
                nearKill,
                eventType is not null && index == 2
                    ? [new GameplayEventReference(eventType, 164)]
                    : []))
            .ToArray();

    private static BrollDetectionContext BrollContext(
        GameplayTimelineFrame[] frames) =>
        new()
        {
            DemoId = "demo",
            PlayerId = "player",
            TickRate = 64,
            Frames = frames,
            ExcludedIntervals = []
        };

    private static BrollCandidate Broll() =>
        new()
        {
            Id = "broll-1",
            DemoId = "broll-demo",
            RoundNumber = 1,
            Type = BrollCandidateType.PlayerApproach,
            StartTick = 0,
            EndTick = 192,
            DurationSeconds = 3,
            MovementScore = 0.8,
            CinematicScore = 0.9,
            ActionDensity = 0.25,
            Trajectory = new PlayerTrajectory(
                Enumerable.Range(0, 5)
                    .Select(index => new PlayerTransformSample(
                        index * 48,
                        new GameplayVector3(index * 100, 0, 64),
                        new GameplayVector3(0, 0, 0)))
                    .ToArray()),
            Tags = ["SELECTED_PLAYER"]
        };

    private static CameraPlanningContext VerifiedCameraContext() =>
        new()
        {
            MapName = "fixture",
            Profile = new MapCameraProfile
            {
                MapName = "fixture",
                SafeVolumes =
                [
                    new SafeCameraVolume(
                        new GameplayVector3(-1000, -1000, -1000),
                        new GameplayVector3(2000, 2000, 2000))
                ],
                EstablishingShots = [],
                RestrictedVolumes = [],
                ManuallyVerified = true
            },
            Capabilities = new HlaeCameraCapabilities
            {
                Available = true,
                Version = "fixture",
                SupportsCampath = true,
                SupportsInput = true,
                SupportsFov = true,
                SupportsHighFpsCapture = true,
                ManualSpikeVerified = true,
                Warnings = []
            }
        };

    private static CameraKeyframe[] VerifiedCameraPath() =>
        Enumerable.Range(0, 4)
            .Select(index => new CameraKeyframe
            {
                TimeSeconds = index,
                Position = new GameplayVector3(index * 10, 0, 64),
                Rotation = GameplayVector3.Zero,
                Fov = 80
            })
            .ToArray();

    private static HighlightPeakMatch Match(
        string highlightId,
        MusicalPeak peak) =>
        new()
        {
            HighlightId = highlightId,
            Peak = peak,
            HighlightImportance = 10,
            PlannedPeakSeconds = peak.TimeSeconds,
            PlannedKillSeconds = peak.TimeSeconds,
            AlignmentErrorMilliseconds = 0,
            Score = 10,
            Warnings = []
        };

    private static CinematicDirector Director() =>
        new(
            new HighlightPeakMatcher(new HighlightImportanceCalculator()),
            new CameraPathPlanner(),
            new CinematicTimeWarpPolicy(new TimeWarpPlanner()),
            new MotivatedEffectPlanner(),
            new SoundDesignPlanner(),
            new ColorNarrativePlanner(),
            new CinematicDurationPolicy());

    private static CinematicDirectorOptions DirectorOptions() =>
        new()
        {
            GenerationId = "generation",
            MapName = "fixture",
            Duration = new MovieDurationOptions(),
            Camera = VerifiedCameraContext()
        };
}
