using Cs2Highlight.Music;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using System.Text.Json;

namespace Cs2Highlight.Web.Tests;

public sealed class DynamicEffectsTests
{
    [Fact]
    public void SeedIsStableAndChangesWithEveryIdentityComponent()
    {
        Sha256EffectSeedProvider provider = new();
        int seed = provider.CreateSeed("generation", "highlight", 2, "7.0");

        Assert.Equal(
            seed,
            provider.CreateSeed("generation", "highlight", 2, "7.0"));
        Assert.NotEqual(seed, provider.CreateSeed("other", "highlight", 2, "7.0"));
        Assert.NotEqual(seed, provider.CreateSeed("generation", "other", 2, "7.0"));
        Assert.NotEqual(seed, provider.CreateSeed("generation", "highlight", 3, "7.0"));
        Assert.NotEqual(seed, provider.CreateSeed("generation", "highlight", 2, "7.1"));
    }

    [Fact]
    public void SeededVariationProducesRepeatableSequence()
    {
        DeterministicEffectRandom first = new(42);
        DeterministicEffectRandom second = new(42);

        double[] left = Enumerable.Range(0, 8).Select(_ => first.NextUnit()).ToArray();
        double[] right = Enumerable.Range(0, 8).Select(_ => second.NextUnit()).ToArray();

        Assert.Equal(left, right);
        Assert.All(left, value => Assert.InRange(value, 0, 0.9999999999999999));
    }

    [Theory]
    [InlineData(VideoEffectType.PunchZoom, VideoEffectType.CrashZoom)]
    [InlineData(VideoEffectType.RollBurst, VideoEffectType.WhipZoom)]
    public void CompatibilityRejectsHardConflicts(
        VideoEffectType acceptedType,
        VideoEffectType candidateType)
    {
        EffectCue accepted = Cue(acceptedType, EffectRole.Primary, "kill", 0.8);
        EffectCue candidate = Cue(candidateType, EffectRole.Accent, "kill", 0.8);

        EffectCompatibilityResult result = new EffectCompatibilityPolicy().Evaluate(
            new PlannedEffect(candidate),
            [new PlannedEffect(accepted)]);

        Assert.False(result.Allowed);
        Assert.NotNull(result.RejectionReason);
    }

    [Fact]
    public void CompatibilityAllowsZoomWithRgbAndZoomWithVignette()
    {
        EffectCompatibilityPolicy policy = new();
        EffectCue zoom = Cue(VideoEffectType.PunchZoom, EffectRole.Primary, "kill");

        Assert.True(policy.Evaluate(
            new PlannedEffect(Cue(VideoEffectType.RgbSplit, EffectRole.Accent, "kill")),
            [new PlannedEffect(zoom)]).Allowed);
        Assert.True(policy.Evaluate(
            new PlannedEffect(Cue(VideoEffectType.VignettePulse, EffectRole.Accent, "other")),
            [new PlannedEffect(zoom)]).Allowed);
    }

    [Fact]
    public void BudgetEnforcesRoleStrongCooldownTotalAndAffectedRatio()
    {
        EffectBudgetPolicy policy = new(
            new EffectBudgetOptions
            {
                MaximumTotalEffectsPerClip = 3,
                MaximumAffectedClipRatio = 0.3,
                MinimumStrongEffectGapSeconds = 0.65
            });
        EffectCue primary = Cue(VideoEffectType.PunchZoom, EffectRole.Primary, "kill", 0.6);

        Assert.Equal(
            "PRIMARY_EFFECT_BUDGET_EXCEEDED",
            policy.Validate(
                Cue(VideoEffectType.SmoothZoom, EffectRole.Primary, "kill", 0.4),
                [primary],
                10));
        Assert.Equal(
            "EFFECT_COOLDOWN_ACTIVE",
            policy.Validate(
                Cue(VideoEffectType.FrameEcho, EffectRole.Accent, "next", 0.8, 0.5, 0.7),
                [Cue(VideoEffectType.CrashZoom, EffectRole.Primary, "kill", 0.8, 0, 0.2)],
                10));
        Assert.Equal(
            "EFFECT_BUDGET_EXCEEDED",
            policy.Validate(
                Cue(VideoEffectType.MicroShake, EffectRole.Accent, "next", 0.5, 2, 4),
                [Cue(VideoEffectType.SmoothZoom, EffectRole.Primary, "kill", 0.5, 0, 2)],
                10));
    }

    [Fact]
    public void VarietyPenalizesRecentRepetitionAndLimitsPrimaryRun()
    {
        EffectCue first = Cue(VideoEffectType.PunchZoom, EffectRole.Primary, "1");
        EffectCue second = Cue(VideoEffectType.PunchZoom, EffectRole.Primary, "2");
        EffectVarietyPolicy policy = new();

        Assert.True(policy.Penalty(
            VideoEffectType.PunchZoom,
            VideoEffectCategory.Zoom,
            [first, second]) > policy.Penalty(
                VideoEffectType.HitStop,
                VideoEffectCategory.Time,
                [first, second]));
        Assert.True(policy.ExceedsConsecutiveLimit(
            VideoEffectType.PunchZoom,
            [first, second]));
    }

    [Fact]
    public void TimeMapperUsesPiecewiseWarpOutputTime()
    {
        TimeWarpPlan plan = new(
            1,
            [
                new TimeWarpSegment(0, 1, 1),
                new TimeWarpSegment(1, 2, 0.5),
                new TimeWarpSegment(2, 4, 1)
            ],
            true,
            []);

        EffectTimeMapping mapping = new EffectTimeMapper().Map(1, 2, plan);

        Assert.Equal(1, mapping.ProcessedStartSeconds, 6);
        Assert.Equal(3, mapping.ProcessedEndSeconds, 6);
    }

    [Fact]
    public async Task CapabilityScannerReportsMissingExecutableWithoutThrowing()
    {
        FfmpegCapabilityScanner scanner = new(
            new PipelineOptions
            {
                FfmpegPath = Path.Combine(
                    Path.GetTempPath(),
                    $"missing-ffmpeg-{Guid.NewGuid():N}.exe")
            },
            TimeProvider.System);

        FfmpegCapabilities capabilities =
            await scanner.ScanAsync(CancellationToken.None);

        Assert.False(capabilities.Available);
        Assert.Empty(capabilities.Filters);
        Assert.NotEmpty(capabilities.Warnings);
    }

    [Fact]
    public void PlannerIsDeterministicAndGenerationChangesVariation()
    {
        DynamicEffectPlanner planner = Planner();
        DynamicEffectPlanningContext context = Context(
            "generation-a",
            Highlight(
                nameof(HighlightType.SoloKill),
                [Kill(1, 64, "ak47", headshot: true)]));

        DynamicEffectPlan first = planner.Build(context);
        DynamicEffectPlan second = planner.Build(context);
        DynamicEffectPlan otherGeneration = planner.Build(context with
        {
            GenerationId = "generation-b"
        });

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.NotEqual(first.DeterministicSeed, otherGeneration.DeterministicSeed);
        Assert.Contains(first.Effects, value => value.Type == VideoEffectType.PunchZoom);
        Assert.Contains(first.Effects, value => value.Type == VideoEffectType.RgbSplit);
    }

    [Theory]
    [InlineData(MovieStyle.Clean, EffectIntensity.Minimal)]
    [InlineData(MovieStyle.Dynamic, EffectIntensity.Balanced)]
    [InlineData(MovieStyle.Cinematic, EffectIntensity.Balanced)]
    [InlineData(MovieStyle.Aggressive, EffectIntensity.Strong)]
    public void PresetsStayWithinRoleAndClipBudgets(
        MovieStyle style,
        EffectIntensity intensity)
    {
        GenerationHighlight highlight = Highlight(
            nameof(HighlightType.TripleKill),
            [
                Kill(1, 64, "ak47"),
                Kill(2, 128, "deagle", headshot: true),
                Kill(3, 192, "awp", headshot: true)
            ]);

        DynamicEffectPlan plan = Planner().Build(Context(
            "generation",
            highlight,
            style,
            intensity));

        Assert.True(plan.Effects.Count <= 12);
        Assert.True(plan.Effects.Count(value => value.Intensity >= 0.7) <= 3);
        Assert.All(
            plan.Effects
                .Where(value => value.SourceKillEventId is not null)
                .GroupBy(value => value.SourceKillEventId),
            group =>
            {
                Assert.True(group.Count(value => value.Role == EffectRole.Primary) <= 1);
                Assert.True(group.Count(value => value.Role == EffectRole.Accent) <= 1);
            });
        Assert.All(plan.Effects, value =>
        {
            Assert.InRange(value.Intensity, 0, 1);
            Assert.InRange(value.StartSeconds, 0, 6);
            Assert.InRange(value.EndSeconds, 0, 6);
        });
    }

    [Fact]
    public void AggressiveAceOnDropGetsStrongFinalPattern()
    {
        GenerationHighlight highlight = Highlight(
            nameof(HighlightType.Ace),
            [
                Kill(1, 32, "ak47"),
                Kill(2, 64, "ak47"),
                Kill(3, 96, "ak47"),
                Kill(4, 128, "deagle", headshot: true),
                Kill(5, 192, "knife")
            ]);
        MusicalAnchor drop = new(
            "drop-001",
            MusicalAnchorType.Drop,
            3,
            1,
            0.9);
        TimeWarpPlan warp = new(
            1,
            [new TimeWarpSegment(0, 6, 1)],
            false,
            []);
        MusicEditSegment edit = new(
            1,
            highlight.HighlightId,
            HighlightType.Ace,
            5,
            0,
            6,
            3,
            drop,
            0,
            3,
            warp,
            "Cut",
            "Cut",
            new MusicEditScoreBreakdown(0, 0, 0, 0, 0, 0),
            []);

        DynamicEffectPlan plan = Planner().Build(Context(
            "generation",
            highlight,
            MovieStyle.Aggressive,
            EffectIntensity.Strong) with
        {
            EditSegment = edit
        });

        Assert.Contains(plan.Effects, value =>
            value.Type == VideoEffectType.CrashZoom &&
            value.SourceMusicalAnchorId == drop.Id);
        Assert.Contains(plan.Effects, value =>
            value.SourceKillEventId == "kill-005" &&
            value.Role == EffectRole.Accent);
    }

    [Fact]
    public void DisabledRgbGroupAndCapabilitiesRemoveRgbDeterministically()
    {
        GenerationHighlight highlight = Highlight(
            nameof(HighlightType.SoloKill),
            [Kill(1, 64, "deagle", headshot: true)]);
        HashSet<string> groups = new(
            DynamicEffectGroups.All,
            StringComparer.Ordinal);
        groups.Remove(DynamicEffectGroups.RgbSplit);
        FfmpegCapabilities capabilities = new(
            "1.0",
            true,
            "fixture",
            "fixture",
            new HashSet<string>(["scale", "crop", "eq", "vignette"], StringComparer.Ordinal),
            DateTimeOffset.UnixEpoch,
            []);

        DynamicEffectPlan plan = Planner().Build(Context(
            "generation",
            highlight) with
        {
            EnabledGroups = groups,
            Capabilities = capabilities
        });

        Assert.DoesNotContain(plan.Effects, value => value.Type == VideoEffectType.RgbSplit);
        Assert.Contains(plan.RejectedEffects, value =>
            value.Type == VideoEffectType.DirectionalMotionBlur &&
            value.Reason == "UNSUPPORTED_BY_FFMPEG_BUILD");
        Assert.Contains(plan.Warnings, value =>
            value.Contains("FALLBACK_TO", StringComparison.Ordinal));
    }

    [Fact]
    public void LockedPlanRoundTripPreservesSeedVariantsAndParameters()
    {
        DynamicEffectPlan original = Planner().Build(Context(
            "generation",
            Highlight(
                nameof(HighlightType.DoubleKill),
                [
                    Kill(1, 64, "ak47"),
                    Kill(2, 128, "deagle", headshot: true)
                ])));
        string json = JsonSerializer.Serialize(original);

        DynamicEffectPlan restored =
            JsonSerializer.Deserialize<DynamicEffectPlan>(json)!;

        Assert.Equal(original.DeterministicSeed, restored.DeterministicSeed);
        Assert.Equal(
            original.Effects.Select(value => (value.Type, value.Seed)),
            restored.Effects.Select(value => (value.Type, value.Seed)));
        Assert.Equal(json, JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void PlannerMapsCueToProcessedTimeAfterSpeedRamp()
    {
        GenerationHighlight highlight = Highlight(
            nameof(HighlightType.SoloKill),
            [Kill(1, 64, "ak47", headshot: true)]);
        TimeWarpPlan warp = new(
            1,
            [
                new TimeWarpSegment(0, 1, 0.5),
                new TimeWarpSegment(1, 6, 1)
            ],
            true,
            []);
        MusicEditSegment edit = new(
            1,
            highlight.HighlightId,
            HighlightType.SoloKill,
            1,
            0,
            6,
            1,
            null,
            0,
            2,
            warp,
            "Cut",
            "Cut",
            new MusicEditScoreBreakdown(0, 0, 0, 0, 0, 0),
            []);

        DynamicEffectPlan plan = Planner().Build(Context(
            "generation",
            highlight) with
        {
            EditSegment = edit
        });

        EffectCue zoom = Assert.Single(plan.Effects.Where(value =>
            value.Type == VideoEffectType.PunchZoom));
        Assert.InRange(zoom.StartSeconds, 1.89, 1.91);
    }

    [Fact]
    public void StructuredGraphAppliesWarpThenHitStopThenOrderedEffects()
    {
        DynamicEffectPlan plan = Plan(
            [
                Cue(VideoEffectType.HitStop, EffectRole.Primary, "1", 0.7, 1, 1.067),
                Cue(VideoEffectType.PunchZoom, EffectRole.Accent, "2", 0.6, 1, 1.3)
                    with
                    {
                        Parameters = new Dictionary<string, double>
                        {
                            ["scale"] = 1.08,
                            ["centerX"] = 0.5,
                            ["centerY"] = 0.5,
                            ["peakOffsetSeconds"] = 0.12
                        }
                    },
                Cue(VideoEffectType.MicroShake, EffectRole.Accent, "3", 0.5, 1, 1.2)
                    with
                    {
                        Parameters = new Dictionary<string, double>
                        {
                            ["amplitudePixels"] = 4,
                            ["impulses"] = 3
                        }
                    },
                Cue(VideoEffectType.RgbSplit, EffectRole.Accent, "4", 0.4, 1, 1.1)
                    with
                    {
                        Parameters = new Dictionary<string, double>
                        {
                            ["redOffsetX"] = 2,
                            ["blueOffsetX"] = -2
                        }
                    }
            ]);
        TimeWarpPlan warp = new(
            1,
            [
                new TimeWarpSegment(0, 2, 0.8),
                new TimeWarpSegment(2, 4, 1)
            ],
            true,
            []);

        DynamicFfmpegFilterGraph graph =
            new DynamicEffectFilterGraphBuilder().Build(
                "0:v:0",
                "0:a:0",
                4,
                plan,
                warp,
                new VideoOutputOptions(1920, 1080, 60),
                "aresample=48000",
                ["eq=contrast=1.05"]);

        Assert.Contains("concat=n=2:v=1:a=0[effect_warped_v]", graph.FilterComplex);
        Assert.Contains("tpad=stop_mode=clone", graph.FilterComplex);
        Assert.Contains("scale=w='1920*", graph.FilterComplex);
        Assert.Contains("rgbashift=rh=2:bh=-2", graph.FilterComplex);
        Assert.True(
            graph.FilterComplex.IndexOf("effect_warped_v", StringComparison.Ordinal) <
            graph.FilterComplex.IndexOf("tpad=stop_mode=clone", StringComparison.Ordinal));
        Assert.True(
            graph.FilterComplex.IndexOf("tpad=stop_mode=clone", StringComparison.Ordinal) <
            graph.FilterComplex.IndexOf("rgbashift=", StringComparison.Ordinal));
        Assert.EndsWith("eq=contrast=1.05,format=yuv420p[effect_video]", graph.FilterComplex);
        Assert.Equal("effect_audio", graph.AudioOutputLabel);
    }

    [Fact]
    public void RendererClampsZoomRgbShakeAndRollParameters()
    {
        DynamicEffectPlan plan = Plan(
            [
                Cue(VideoEffectType.CrashZoom, EffectRole.Primary, "1", 1, 0.2, 0.5)
                    with
                    {
                        Parameters = new Dictionary<string, double>
                        {
                            ["scale"] = 3,
                            ["centerX"] = 1,
                            ["centerY"] = -1
                        }
                    },
                Cue(VideoEffectType.RgbSplit, EffectRole.Accent, "2", 1, 0.6, 0.7)
                    with
                    {
                        Parameters = new Dictionary<string, double>
                        {
                            ["redOffsetX"] = 99,
                            ["blueOffsetX"] = -99
                        }
                    },
                Cue(VideoEffectType.RollBurst, EffectRole.Accent, "3", 1, 0.8, 1)
                    with
                    {
                        Parameters = new Dictionary<string, double>
                        {
                            ["angleDegrees"] = 30
                        }
                    }
            ]);

        DynamicFfmpegFilterGraph graph =
            new DynamicEffectFilterGraphBuilder().Build(
                "0:v:0",
                "0:a:0",
                3,
                plan,
                null,
                new VideoOutputOptions(1920, 1080, 60),
                "aresample=48000");

        Assert.Contains(
            "settb=AVTB,setpts=PTS-STARTPTS[effect_base_v]",
            graph.FilterComplex);
        Assert.Contains(
            "[0:a:0]asetpts=PTS-STARTPTS,aresample=48000[effect_audio]",
            graph.FilterComplex);
        Assert.Contains("0.15*(", graph.FilterComplex);
        Assert.Contains("rgbashift=rh=8:bh=-8", graph.FilterComplex);
        Assert.Contains("0.034907*(", graph.FilterComplex);
    }

    private static EffectCue Cue(
        VideoEffectType type,
        EffectRole role,
        string kill,
        double intensity = 0.5,
        double start = 0,
        double end = 0.2) =>
        new()
        {
            Id = $"{kill}-{type}",
            Type = type,
            Category = Category(type),
            Role = role,
            StartSeconds = start,
            EndSeconds = end,
            Intensity = intensity,
            Priority = 1,
            Seed = 1,
            Parameters = new Dictionary<string, double>(),
            SourceKillEventId = kill
        };

    private static VideoEffectCategory Category(VideoEffectType type) => type switch
    {
        VideoEffectType.PunchZoom or
        VideoEffectType.CrashZoom or
        VideoEffectType.SmoothZoom => VideoEffectCategory.Zoom,
        VideoEffectType.HitStop => VideoEffectCategory.Time,
        VideoEffectType.RgbSplit => VideoEffectCategory.Color,
        VideoEffectType.VignettePulse => VideoEffectCategory.Accent,
        _ => VideoEffectCategory.Temporal
    };

    private static DynamicEffectPlanner Planner() =>
        new(
            new Sha256EffectSeedProvider(),
            new EffectCompatibilityPolicy(),
            new EffectBudgetPolicy(),
            new EffectVarietyPolicy(),
            new EffectTimeMapper());

    private static DynamicEffectPlanningContext Context(
        string generation,
        GenerationHighlight highlight,
        MovieStyle style = MovieStyle.Dynamic,
        EffectIntensity intensity = EffectIntensity.Balanced) =>
        new()
        {
            GenerationId = generation,
            Highlight = highlight,
            TickRate = 64,
            Style = style,
            Intensity = intensity
        };

    private static GenerationHighlight Highlight(
        string type,
        IReadOnlyList<KillDescriptor> kills) =>
        new()
        {
            HighlightId = $"highlight-{type}",
            Type = type,
            StartTick = 0,
            EndTick = 384,
            SafeEndTick = 384,
            PrimaryKillTick = kills[^1].Tick,
            FirstKillTick = kills[0].Tick,
            LastKillTick = kills[^1].Tick,
            TickRate = 64,
            KillCount = kills.Count,
            HeadshotCount = kills.Count(value => value.Headshot),
            BeautyScore = type == nameof(HighlightType.Ace) ? 80 : 40,
            EstimatedDurationMilliseconds = 6000,
            KillsJson = JsonSerializer.Serialize(kills),
            TagsJson = "[]",
            WeaponSequenceJson = "[]"
        };

    private static KillDescriptor Kill(
        int eventIndex,
        long tick,
        string weapon,
        bool headshot = false) =>
        new(
            eventIndex,
            tick,
            "killer",
            $"victim-{eventIndex}",
            weapon,
            headshot);

    private static DynamicEffectPlan Plan(IReadOnlyList<EffectCue> effects) =>
        new()
        {
            SchemaVersion = "1.0",
            PlannerVersion = "7.0",
            GenerationId = "generation",
            HighlightId = "highlight",
            ClipId = "highlight",
            Style = MovieStyle.Dynamic,
            Intensity = EffectIntensity.Balanced,
            DeterministicSeed = 1,
            Effects = effects,
            RejectedEffects = [],
            Warnings = []
        };
}
