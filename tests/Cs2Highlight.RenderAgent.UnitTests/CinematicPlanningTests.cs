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
    public void ExplicitDurationNeverOverridesShortGameplayCap()
    {
        MovieDurationBudget budget = new CinematicDurationPolicy().Calculate(
            [Highlight("solo", HighlightType.SoloKill, 6, 20)],
            new MovieDurationOptions
            {
                Selection = MovieDurationSelection.Seconds30
            });

        Assert.Equal(12, budget.MaximumTotalSeconds);
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

        Assert.Single(pov);
        Assert.Equal(MotivatedEffectReason.FinalKill, pov[0].Reason);
        Assert.Empty(camera);
        Assert.Empty(calm);
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
            [],
            DirectorOptions());

        Assert.Equal(highlights.Length, plan.HighlightMatches.Count);
        Assert.Equal(
            highlights.Length,
            plan.Segments.Count(value => value.HighlightId is not null));
        Assert.Contains("HIGHLIGHT_PEAK_TIMELINE_FALLBACK", plan.Warnings);
    }

    [Fact]
    public void DirectorBuildsContinuousTimelineWhenBrollIsSufficient()
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
        Assert.All(
            ordered.Zip(ordered.Skip(1)),
            pair => Assert.Equal(
                pair.First.OutputEndSeconds,
                pair.Second.OutputStartSeconds,
                6));
        Assert.DoesNotContain(
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
            DemoId = "demo",
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
