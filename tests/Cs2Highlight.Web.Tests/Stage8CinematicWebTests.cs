using System.Diagnostics;
using Cs2Highlight.Analysis;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cs2Highlight.Web.Tests;

public sealed class Stage8CinematicWebTests
{
    [Fact]
    public void StateMachineAcceptsCinematicDirectorRenderPath()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Generation generation = new()
        {
            Status = GenerationStatus.PreparingRenderPlan
        };

        GenerationStateMachine.Transition(
            generation, GenerationStatus.RenderingHighlights, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.VerifyingClips, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.SynchronizingPeaks, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.ApplyingEffects, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.ComposingCinematicTimeline, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.MixingNarrativeAudio, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.ApplyingNarrativeColor, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.VerifyingCinematicMovie, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.Completed, now);

        Assert.Equal(GenerationStatus.Completed, generation.Status);
    }

    [Fact]
    public void NarrativeAudioUsesSectionGainsAndKillEnvelope()
    {
        GenerationMovieSettings settings = new()
        {
            MusicGainDb = -3,
            GameplayGainDb = -16
        };
        CinematicMoviePlan cinematic = Plan(
            effects: [],
            sound:
            [
                new SoundDesignSection(
                    "drop", -8, -4, false, true)
            ]);
        MusicEditPlan music = new(
            "2.0",
            "generation",
            "music.mp3",
            8,
            MovieStyle.CinematicDirector,
            MusicSyncIntensity.Expressive,
            [
                new MusicEditSegment(
                    1,
                    "h1",
                    HighlightType.SoloKill,
                    1,
                    0,
                    8,
                    4,
                    new MusicalAnchor(
                        "peak",
                        MusicalAnchorType.Drop,
                        4,
                        0.9,
                        0.9),
                    4,
                    4,
                    UnitTimeWarp(),
                    "Cut",
                    "Cut",
                    new MusicEditScoreBreakdown(0, 0, 0, 0, 0, 0),
                    [])
            ],
            []);

        string graph = FfmpegMovieFilterBuilder.AudioMix(
            settings,
            music,
            cinematic: cinematic);

        Assert.Contains("between(t\\,0\\,8)", graph);
        Assert.Contains("between(t\\,3.95\\,4)", graph);
        Assert.Contains("eval=frame", graph);
        Assert.Contains("alimiter", graph);
        Assert.Contains("LRA=7", graph);
    }

    [Fact]
    public void AdapterPreservesTheCompleteMotivatedEffectStack()
    {
        CinematicMoviePlan cinematic = Plan(
            effects:
            [
                new MotivatedEffectDirective(
                    "SmoothZoom",
                    MotivatedEffectReason.MusicPeak,
                    3.7,
                    4.2,
                    0.25),
                new MotivatedEffectDirective(
                    "HitStop",
                    MotivatedEffectReason.FinalKill,
                    4,
                    4.08,
                    0.4)
            ],
            sound: []);
        GenerationHighlight highlight = new()
        {
            HighlightId = "h1"
        };

        DynamicEffectPlan result = new CinematicDynamicEffectAdapter(
            new Sha256EffectSeedProvider()).Create(
                "generation",
                highlight,
                cinematic,
                EffectIntensity.Balanced);

        Assert.Equal(2, result.Effects.Count);
        EffectCue effect = Assert.Single(result.Effects.Where(value =>
            value.Type == VideoEffectType.SmoothZoom));
        Assert.Equal(
            MotivatedEffectReason.MusicPeak.ToString(),
            effect.Reason);
        Assert.Equal("peak", effect.SourceMusicalAnchorId);
        Assert.Contains(
            result.Effects,
            value => value.Type == VideoEffectType.HitStop);
    }

    [Fact]
    public void StrongCinematicAdapterKeepsTwoCompatibleAccents()
    {
        CinematicMoviePlan cinematic = Plan(
            effects:
            [
                new MotivatedEffectDirective(
                    "PunchZoom",
                    MotivatedEffectReason.MusicPeak,
                    3.8,
                    4.15,
                    0.5),
                new MotivatedEffectDirective(
                    "MicroShake",
                    MotivatedEffectReason.TimeRamp,
                    3.55,
                    3.8,
                    0.32)
            ],
            sound: []);

        DynamicEffectPlan result = new CinematicDynamicEffectAdapter(
            new Sha256EffectSeedProvider()).Create(
                "generation",
                new GenerationHighlight { HighlightId = "h1" },
                cinematic,
                EffectIntensity.Strong);

        Assert.Equal(2, result.Effects.Count);
        Assert.Contains(
            result.Effects,
            value =>
                value.Type == VideoEffectType.PunchZoom &&
                value.Role == EffectRole.Primary);
        Assert.Contains(
            result.Effects,
            value =>
                value.Type == VideoEffectType.MicroShake &&
                value.Role == EffectRole.Accent);
    }

    [Fact]
    public void CinematicAdapterUsesAContentAwareTransitionIntoFollowingShot()
    {
        CinematicMoviePlan original = Plan(
            effects:
            [
                new MotivatedEffectDirective(
                    "PunchZoom",
                    MotivatedEffectReason.MusicPeak,
                    3.8,
                    4.15,
                    0.5)
            ],
            sound: []);
        CinematicSequenceSegment highlightSegment =
            original.Segments[0] with
            {
                Role = CinematicSequenceRole.Highlight,
                OutputStartSeconds = 0,
                OutputEndSeconds = 8
            };
        CinematicSequenceSegment outro = original.Segments[0] with
        {
            Id = "outro",
            Role = CinematicSequenceRole.Outro,
            OutputStartSeconds = 8,
            OutputEndSeconds = 10,
            HighlightId = null,
            BrollCandidateId = "broll",
            Effects = []
        };
        CinematicMoviePlan cinematic = original with
        {
            TargetDurationSeconds = 10,
            Segments = [highlightSegment, outro]
        };
        GenerationHighlight highlight = new()
        {
            HighlightId = "h1",
            WeaponSequenceJson = "[\"awp\"]",
            HeadshotCount = 1
        };

        DynamicEffectPlan result = new CinematicDynamicEffectAdapter(
            new Sha256EffectSeedProvider()).Create(
                "generation",
                highlight,
                cinematic,
                EffectIntensity.Strong);

        EffectCue transition = Assert.Single(result.Effects.Where(value =>
            value.Role == EffectRole.Transition));
        Assert.Equal(VideoEffectType.FlashCut, transition.Type);
        Assert.Equal(8, transition.EndSeconds, 6);
    }

    [Theory]
    [InlineData("[\"flashbang\"]", VideoEffectType.FlashCut)]
    [InlineData("[\"smoke\"]", VideoEffectType.FadeTransition)]
    [InlineData("[\"fast_movement\"]", VideoEffectType.WhipPan)]
    public void CinematicAdapterUsesGameplayTagsForTransitions(
        string tagsJson,
        VideoEffectType expected)
    {
        CinematicMoviePlan original = Plan(
            effects:
            [
                new MotivatedEffectDirective(
                    "SmoothZoom",
                    MotivatedEffectReason.MusicPeak,
                    3.8,
                    4.15,
                    0.5)
            ],
            sound: []);
        CinematicSequenceSegment highlightSegment =
            original.Segments[0] with
            {
                Role = CinematicSequenceRole.Highlight,
                OutputStartSeconds = 0,
                OutputEndSeconds = 8
            };
        CinematicMoviePlan cinematic = original with
        {
            TargetDurationSeconds = 10,
            Segments =
            [
                highlightSegment,
                original.Segments[0] with
                {
                    Id = "outro",
                    Role = CinematicSequenceRole.Outro,
                    OutputStartSeconds = 8,
                    OutputEndSeconds = 10,
                    HighlightId = null,
                    BrollCandidateId = "broll",
                    Effects = []
                }
            ]
        };

        DynamicEffectPlan result = new CinematicDynamicEffectAdapter(
            new Sha256EffectSeedProvider()).Create(
                "generation",
                new GenerationHighlight
                {
                    HighlightId = "h1",
                    TagsJson = tagsJson
                },
                cinematic,
                EffectIntensity.Strong);

        EffectCue transition = Assert.Single(result.Effects.Where(value =>
            value.Role == EffectRole.Transition));
        Assert.Equal(expected, transition.Type);
    }

    [Fact]
    public void CinematicFinishPreservesTheFullFrame()
    {
        string finish = FfmpegMovieFilterBuilder.CinematicFinish();

        Assert.Equal(
            "eq=contrast=1.04:saturation=1.08:gamma=1.015",
            finish);
        Assert.DoesNotContain("crop=", finish);
        Assert.DoesNotContain("pad=", finish);
    }

    [Fact]
    public void IntroBoundaryUsesPairedAudioVideoFades()
    {
        CinematicMoviePlan original = Plan(effects: [], sound: []);
        CinematicSequenceSegment highlight = original.Segments[0] with
        {
            OutputStartSeconds = 3,
            OutputEndSeconds = 8
        };
        CinematicSequenceSegment broll = original.Segments[0] with
        {
            Id = "intro-broll",
            Role = CinematicSequenceRole.Intro,
            OutputStartSeconds = 0,
            OutputEndSeconds = 3,
            HighlightId = null,
            BrollCandidateId = "broll"
        };
        CinematicMoviePlan cinematic = original with
        {
            Segments = [broll, highlight]
        };

        (string outgoingVideo, string outgoingAudio) =
            FfmpegMovieFilterBuilder.CinematicIntroTransition(
                cinematic,
                0,
                3);
        (string incomingVideo, string incomingAudio) =
            FfmpegMovieFilterBuilder.CinematicIntroTransition(
                cinematic,
                1,
                5);

        Assert.Contains("fade=t=out", outgoingVideo);
        Assert.Contains("color=white", outgoingVideo);
        Assert.Contains("afade=t=out", outgoingAudio);
        Assert.Contains("fade=t=in", incomingVideo);
        Assert.Contains("color=white", incomingVideo);
        Assert.Contains("afade=t=in", incomingAudio);
    }

    [Fact(Timeout = 180_000)]
    [Trait("Category", "Stage8Ffmpeg")]
    public async Task CinematicCompositionRendersAndProbesWhenOptedIn()
    {
        string? configured =
            Environment.GetEnvironmentVariable("CS2_STAGE8_FFMPEG");
        if (string.IsNullOrWhiteSpace(configured))
            return;
        string ffmpeg = Path.GetFullPath(configured);
        string ffprobe = Path.Combine(
            Path.GetDirectoryName(ffmpeg)!,
            "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), ffmpeg);
        Assert.True(File.Exists(ffprobe), ffprobe);
        string fixtureRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable(
                "CS2_STAGE8_FIXTURE_OUTPUT") ??
            Path.Combine("artifacts", "stage8-fixtures"));
        string output = Path.Combine(
            fixtureRoot,
            $"composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(output);
        string source = Path.Combine(output, "source.mp4");
        string brollSource = Path.Combine(output, "broll.mp4");
        string music = Path.Combine(output, "music.wav");
        await RunAsync(
            ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i",
                "testsrc2=size=640x360:rate=30:duration=8",
                "-f", "lavfi", "-i",
                "sine=frequency=330:sample_rate=48000:duration=8",
                "-c:v", "libx264", "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", source
            ]);
        await RunAsync(
            ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i",
                "testsrc2=size=640x360:rate=30:duration=2",
                "-f", "lavfi", "-i",
                "sine=frequency=220:sample_rate=48000:duration=2",
                "-vf", "hue=h=35",
                "-c:v", "libx264", "-preset", "veryfast",
                "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest", brollSource
            ]);
        await RunAsync(
            ffmpeg,
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i",
                "sine=frequency=110:sample_rate=48000:duration=10",
                "-c:a", "pcm_s16le", music
            ]);
        CinematicMoviePlan cinematic = Plan(
            effects:
            [
                new MotivatedEffectDirective(
                    "SmoothZoom",
                    MotivatedEffectReason.MusicPeak,
                    3.7,
                    4.2,
                    0.25),
                new MotivatedEffectDirective(
                    "RecoilShake",
                    MotivatedEffectReason.TimeRamp,
                    3.4,
                    3.65,
                    0.3),
                new MotivatedEffectDirective(
                    "DirectionalMotionBlur",
                    MotivatedEffectReason.TimeRamp,
                    3.92,
                    4.10,
                    0.34),
                new MotivatedEffectDirective(
                    "FlashAccent",
                    MotivatedEffectReason.BassImpact,
                    3.98,
                    4.07,
                    0.22),
                new MotivatedEffectDirective(
                    "RgbSplit",
                    MotivatedEffectReason.CameraTransition,
                    3.98,
                    4.12,
                    0.20),
                new MotivatedEffectDirective(
                    "ZoomBlur",
                    MotivatedEffectReason.TimeRamp,
                    4.18,
                    4.38,
                    0.32),
                new MotivatedEffectDirective(
                    "VignettePulse",
                    MotivatedEffectReason.CameraTransition,
                    4.18,
                    4.42,
                    0.20)
            ],
            sound:
            [
                new SoundDesignSection(
                    "drop", -8, -4, false, true)
            ]);
        TimeWarpPlan visibleSlowMotion = new(
            1,
            [
                new TimeWarpSegment(0, 0.75, 1.25),
                new TimeWarpSegment(0.75, 1, 0.625),
                new TimeWarpSegment(1, 8, 1)
            ],
            true,
            ["TEST_VISIBLE_SLOW_MOTION"]);
        cinematic = cinematic with
        {
            MusicExcerpt = cinematic.MusicExcerpt with
            {
                EndSeconds = 10
            },
            TargetDurationSeconds = 10,
            Segments =
            [
                cinematic.Segments[0] with
                {
                    Id = "intro-broll",
                    Role = CinematicSequenceRole.Intro,
                    OutputStartSeconds = 0,
                    OutputEndSeconds = 2,
                    HighlightId = null,
                    BrollCandidateId = "broll",
                    Camera = cinematic.Segments[0].Camera with
                    {
                        Id = "intro-camera",
                        TargetDurationSeconds = 2,
                        EndTick = 128
                    },
                    TimeWarp = new TimeWarpPlan(
                        1,
                        [new TimeWarpSegment(0, 2, 1)],
                        false,
                        [])
                },
                cinematic.Segments[0] with
                {
                    OutputStartSeconds = 2,
                    OutputEndSeconds = 10,
                    TimeWarp = visibleSlowMotion
                }
            ]
        };
        GenerationHighlight highlight = new()
        {
            HighlightId = "h1"
        };
        DynamicEffectPlan dynamic = new CinematicDynamicEffectAdapter(
            new Sha256EffectSeedProvider()).Create(
                "generation",
                highlight,
                cinematic,
                EffectIntensity.Strong);
        MusicEditPlan edit = EditPlan() with
        {
            MusicDurationSeconds = 10
        };
        GenerationMovieSettings settings = new()
        {
            MovieStyle = MovieStyle.CinematicDirector,
            EffectIntensity = EffectIntensity.Strong,
            SyncIntensity = MusicSyncIntensity.Expressive,
            ColorGradePreset = ColorGradePreset.Natural,
            GameplayGainDb = -16,
            MusicGainDb = -3
        };
        PipelineOptions pipeline = new()
        {
            FfmpegPath = ffmpeg,
            FfprobePath = ffprobe,
            FfmpegTimeoutSeconds = 120
        };
        FfmpegCapabilities capabilities =
            await new FfmpegCapabilityScanner(
                pipeline,
                TimeProvider.System).ScanAsync(
                    CancellationToken.None);
        FfmpegHighlightCompilationService service = new(
            pipeline,
            new FfmpegEffectFilterGraphBuilder(),
            new DynamicEffectFilterGraphBuilder(),
            new TrustedLutCatalog(new TrustedLutOptions
            {
                Root = output
            }),
            NullLogger<FfmpegHighlightCompilationService>.Instance);

        CompilationResult result = await service.ComposeAsync(
            new CompilationRequest(
                [brollSource, source],
                Path.Combine(output, "result"),
                640,
                360,
                30,
                EffectPlans: [null, null],
                MusicEditPlan: edit,
                MusicPath: music,
                MovieSettings: settings,
                DynamicEffectPlans: [null, dynamic],
                FfmpegCapabilities: capabilities,
                CinematicMoviePlan: cinematic),
            null,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.OutputFile);
        Assert.True(File.Exists(result.OutputFile));
        Assert.InRange(result.DurationMilliseconds, 9_800, 10_200);
        Assert.True(result.FileSizeBytes > 10_000);
        Assert.True(File.Exists(Path.Combine(
            output,
            "result",
            "dynamic-effect-result.json")));
    }

    private static MusicEditPlan EditPlan() => new(
        "2.0",
        "generation",
        "music.wav",
        8,
        MovieStyle.CinematicDirector,
        MusicSyncIntensity.Expressive,
        [
            new MusicEditSegment(
                1,
                "h1",
                HighlightType.SoloKill,
                1,
                0,
                8,
                4,
                new MusicalAnchor(
                    "peak",
                    MusicalAnchorType.Drop,
                    4,
                    0.9,
                    0.9),
                0,
                4,
                UnitTimeWarp(),
                "Cut",
                "Cut",
                new MusicEditScoreBreakdown(0, 0, 0, 0, 0, 0),
                [])
        ],
        []);

    private static async Task RunAsync(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        Assert.True(process.Start());
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stderr = await error;
        _ = await output;
        Assert.True(
            process.ExitCode == 0,
            $"{Path.GetFileName(executable)} exited with " +
            $"{process.ExitCode}: {stderr}");
    }

    private static CinematicMoviePlan Plan(
        IReadOnlyList<MotivatedEffectDirective> effects,
        IReadOnlyList<SoundDesignSection> sound)
    {
        MusicalPeak peak = new()
        {
            Id = "peak",
            Type = MusicalPeakType.DropStart,
            TimeSeconds = 4,
            Strength = 0.9,
            Confidence = 0.9,
            SectionId = "drop"
        };
        return new CinematicMoviePlan
        {
            SchemaVersion = "1.0",
            PlannerVersion = "8.0",
            GenerationId = "generation",
            MusicExcerpt = new MusicExcerptPlan
            {
                StartSeconds = 0,
                EndSeconds = 8,
                SectionIds = ["drop"],
                Peaks = [peak],
                RequiredPeakCount = 1,
                UsablePeakCount = 1,
                Score = 1,
                IsValid = true,
                ScoreBreakdown = new Dictionary<string, double>(),
                Warnings = []
            },
            TargetDurationSeconds = 8,
            Segments =
            [
                new CinematicSequenceSegment
                {
                    Id = "segment",
                    Role = CinematicSequenceRole.PeakHighlight,
                    OutputStartSeconds = 0,
                    OutputEndSeconds = 8,
                    MusicSectionId = "drop",
                    HighlightId = "h1",
                    Camera = new CameraShotPlan
                    {
                        Id = "camera",
                        Type = CameraShotType.PlayerPov,
                        DemoId = "demo",
                        StartTick = 0,
                        EndTick = 512,
                        TargetDurationSeconds = 8,
                        Keyframes = [],
                        FovStart = 90,
                        FovEnd = 90,
                        RequiresHighFpsCapture = false,
                        FallbackShotId = "camera",
                        Warnings = []
                    },
                    TimeWarp = UnitTimeWarp(),
                    Effects = effects
                }
            ],
            HighlightMatches =
            [
                new HighlightPeakMatch
                {
                    HighlightId = "h1",
                    Peak = peak,
                    HighlightImportance = 1,
                    PlannedPeakSeconds = 4,
                    PlannedKillSeconds = 4,
                    AlignmentErrorMilliseconds = 0,
                    Score = 1,
                    Warnings = []
                }
            ],
            SoundDesign = new SoundDesignPlan(sound, true, []),
            Color = new ColorNarrativePlan(
                ColorGradePreset.Natural,
                [],
                []),
            Warnings = []
        };
    }

    private static TimeWarpPlan UnitTimeWarp() => new(
        1,
        [new TimeWarpSegment(0, 8, 1)],
        false,
        []);
}
