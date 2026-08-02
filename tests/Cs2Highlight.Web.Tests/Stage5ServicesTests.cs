using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Cs2Highlight.Music;

namespace Cs2Highlight.Web.Tests;

public sealed class Stage5ServicesTests
{
    [Fact]
    public void DynamicEffectPlanIsDeterministicAndBoundsOverlappingEffects()
    {
        GenerationHighlight highlight = Highlight("h1", 100, 700);
        highlight.EstimatedDurationMilliseconds = 9000;
        highlight.KillsJson = JsonSerializer.Serialize(new[]
        {
            KillDescriptor(1, 300, headshot: true),
            KillDescriptor(2, 310, headshot: true)
        });
        EffectPlanner planner = new();

        HighlightEffectPlan first = planner.Build(highlight, 64, EffectPreset.Dynamic);
        HighlightEffectPlan second = planner.Build(highlight, 64, EffectPreset.Dynamic);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(2, first.Events.Count(value => value.Type == EffectType.SmoothZoom));
        Assert.Single(first.Events.Where(value => value.Type == EffectType.HeadshotFlash));
        Assert.Contains(first.Events, value => value.Type == EffectType.ImpactShake);
        Assert.Contains(first.Events, value => value.Type == EffectType.ColorPunch);
        Assert.Contains(first.Events, value => value.Type == EffectType.VignettePulse);
        Assert.Contains(first.Events, value => value.Type == EffectType.ClipTransition);
        Assert.All(
            first.Events.Where(value => value.Type == EffectType.SmoothZoom),
            value => Assert.InRange(value.Intensity, 0, 0.12));
    }

    [Theory]
    [InlineData(EffectPreset.None, false)]
    [InlineData(EffectPreset.Clean, true)]
    [InlineData(EffectPreset.Dynamic, true)]
    public void PresetsProduceExpectedTransitionPlan(
        EffectPreset preset,
        bool hasTransition)
    {
        HighlightEffectPlan plan = new EffectPlanner().Build(
            Highlight("clip", 100, 500),
            64,
            preset);

        Assert.Equal(
            hasTransition,
            plan.Events.Any(value => value.Type == EffectType.ClipTransition));
    }

    [Fact]
    public void FilterGraphUsesOnlyStructuredPlanData()
    {
        HighlightEffectPlan plan = new(
            "1.1",
            EffectPreset.Dynamic,
            [
                new(EffectType.SmoothZoom, 100, 600, 0.08),
                new(EffectType.ImpactShake, 300, 180, 1),
                new(EffectType.ColorPunch, 320, 170, 0.2),
                new(EffectType.HeadshotFlash, 340, 80, 0.1),
                new(EffectType.VignettePulse, 360, 300, 0.16)
            ]);

        string video = FfmpegEffectFilterBuilder.Build(1920, 1080, 60, 8, plan);
        string audio = FfmpegEffectFilterBuilder.BuildAudio(8, plan);

        Assert.Contains("if(lt(t", video);
        Assert.Contains("vignette=PI/12", video);
        Assert.Contains("eq=brightness=0.1", video);
        Assert.Contains("sin(95*t)", video);
        Assert.Contains("saturation=1.4", video);
        Assert.DoesNotContain("fade=t=out", video);
        Assert.Contains("loudnorm", audio);
    }

    [Theory]
    [InlineData(ColorGradePreset.None, "null")]
    [InlineData(ColorGradePreset.Natural, "eq=contrast=1.02")]
    [InlineData(ColorGradePreset.Competitive, "contrast=1.08")]
    [InlineData(ColorGradePreset.CinematicCool, "colorbalance")]
    [InlineData(ColorGradePreset.CinematicWarm, "curves")]
    [InlineData(ColorGradePreset.HighContrast, "contrast=1.16")]
    [InlineData(ColorGradePreset.Neon, "saturation=1.18")]
    public void ColorGradeUsesTrustedPresetOnly(
        ColorGradePreset preset,
        string expected)
    {
        Assert.Contains(expected, FfmpegMovieFilterBuilder.Color(preset));
    }

    [Fact]
    public void AudioMixUsesConfiguredGainsAndLimiter()
    {
        GenerationMovieSettings settings = new()
        {
            MusicGainDb = -3,
            GameplayGainDb = -16
        };

        string graph = FfmpegMovieFilterBuilder.AudioMix(settings);

        Assert.Contains("volume='0.158489'", graph);
        Assert.Contains("volume='0.707946'", graph);
        Assert.Contains("amix=", graph);
        Assert.Contains("alimiter=limit=0.891251", graph);
        Assert.Contains("loudnorm=I=-14:TP=-1", graph);
    }

    [Fact]
    public void PiecewiseTimeWarpBuildsSynchronizedVideoAndAudioGraph()
    {
        TimeWarpPlan plan = new(
            1,
            [
                new TimeWarpSegment(0, 1, 1),
                new TimeWarpSegment(1, 2, 0.8),
                new TimeWarpSegment(2, 4, 1)
            ],
            true,
            []);

        string graph = FfmpegMovieFilterBuilder.TimeWarp(
            "fps=60",
            "aresample=48000",
            "0:a:0",
            plan);

        Assert.Contains("trim=start=1:end=2,setpts=(PTS-STARTPTS)/0.8", graph);
        Assert.Contains("atrim=start=1:end=2,asetpts=PTS-STARTPTS,atempo=0.8", graph);
        Assert.Contains("concat=n=3:v=1:a=0[warped_video]", graph);
        Assert.Contains("concat=n=3:v=0:a=1[warped_audio]", graph);
    }

    [Fact]
    public void AudioMixCreatesKillAccentWithoutMusicDuckingEnvelope()
    {
        GenerationMovieSettings settings = new()
        {
            MusicGainDb = -3,
            GameplayGainDb = -16
        };
        MusicEditPlan plan = new(
            "1.0",
            "generation",
            "music.mp3",
            30,
            MovieStyle.Dynamic,
            MusicSyncIntensity.Expressive,
            [
                new MusicEditSegment(
                    1,
                    "highlight",
                    HighlightType.SoloKill,
                    1,
                    0,
                    8,
                    5,
                    null,
                    0,
                    5,
                    new TimeWarpPlan(
                        1,
                        [new TimeWarpSegment(0, 8, 1)],
                        false,
                        []),
                    "Cut",
                    "Cut",
                    new MusicEditScoreBreakdown(0, 0, 0, 0, 0, 0),
                    [])
            ],
            []);

        string graph = FfmpegMovieFilterBuilder.AudioMix(settings, plan);

        Assert.Contains("between(t\\,4.95\\,5)", graph);
        Assert.Contains("1+(", graph);
        Assert.DoesNotContain("1-(1-", graph);
        Assert.DoesNotContain("MusicDuckOnKill", graph);
        Assert.Contains("eval=frame", graph);
        Assert.Contains(
            "atrim=duration=8,afade=t=out:st=7.25:d=0.75",
            graph);

        string exactLengthGraph = FfmpegMovieFilterBuilder.AudioMix(
            settings,
            plan with { MusicDurationSeconds = 8 });
        Assert.DoesNotContain("afade=t=out", exactLengthGraph);

        string excerptGraph = FfmpegMovieFilterBuilder.AudioMix(
            settings,
            plan with { MusicStartSeconds = 10 });
        Assert.Contains(
            "atrim=duration=8,afade=t=out:st=7.25:d=0.75",
            excerptGraph);
    }

    [Fact]
    public void LutFilterEscapesWindowsDriveSeparator()
    {
        string filter = FfmpegMovieFilterBuilder.Lut(
            Path.Combine("C:\\", "trusted", "grade.cube"));

        Assert.Equal("lut3d=file='C\\:/trusted/grade.cube'", filter);
    }

    [Fact]
    public async Task SelectionDeduplicatesIdsPersistsDurationAndLocksAfterward()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<GenerationDbContext> dbOptions =
            new DbContextOptionsBuilder<GenerationDbContext>()
                .UseSqlite(connection)
                .Options;
        TestFactory factory = new(dbOptions);
        await using (GenerationDbContext db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            Generation generation = new()
            {
                PublicId = "stage5-selection",
                Status = GenerationStatus.AwaitingHighlightSelection,
                SelectedSteamId = "76561198000000001"
            };
            generation.Highlights.Add(Highlight("h1", 100, 500));
            generation.Highlights.Add(Highlight("h2", 600, 1100));
            GenerationHighlight other = Highlight("other", 1200, 1600);
            other.SteamId = "76561198000000002";
            generation.Highlights.Add(other);
            db.Generations.Add(generation);
            await db.SaveChangesAsync();
        }
        HighlightSelectionService service = new(
            factory,
            new RecommendedSelectionOptions(),
            new EffectPlanner(),
            TimeProvider.System);

        await service.SaveSelectionAsync(
            "stage5-selection",
            ["h1", "h1", "h2"],
            EffectPreset.Dynamic,
            CancellationToken.None);

        await using (GenerationDbContext db = await factory.CreateDbContextAsync())
        {
            Generation saved = await db.Generations
                .Include(value => value.Highlights)
                .SingleAsync();
            Assert.Equal(GenerationStatus.AwaitingMusicUpload, saved.Status);
            Assert.Equal(EffectPreset.Dynamic, saved.EffectPreset);
            Assert.Equal(2, saved.MaximumHighlights);
            Assert.Equal(11700, saved.EstimatedDurationMilliseconds);
            Assert.Equal(
                ["h1", "h2"],
                saved.Highlights
                    .Where(value => value.SelectedByUser)
                    .OrderBy(value => value.SelectionOrder)
                    .Select(value => value.HighlightId));
            Assert.Equal(2, await db.GenerationEffectPlans.CountAsync());
        }
        InvalidOperationException locked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveSelectionAsync(
                "stage5-selection",
                ["h1"],
                EffectPreset.None,
                CancellationToken.None));
        Assert.Equal("GENERATION_SELECTION_LOCKED", locked.Message);
    }

    [Fact]
    public async Task EveryExplicitlySelectedHighlightIsPersistedWithoutAnArbitraryCap()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<GenerationDbContext> dbOptions =
            new DbContextOptionsBuilder<GenerationDbContext>()
                .UseSqlite(connection)
                .Options;
        TestFactory factory = new(dbOptions);
        string[] ids = Enumerable.Range(1, 12)
            .Select(index => $"h{index:D2}")
            .ToArray();
        await using (GenerationDbContext db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            Generation generation = new()
            {
                PublicId = "uncapped-selection",
                Status = GenerationStatus.AwaitingHighlightSelection,
                SelectedSteamId = "76561198000000001"
            };
            foreach ((string id, int index) in ids.Select((id, index) => (id, index)))
                generation.Highlights.Add(Highlight(id, index * 1_000, index * 1_000 + 500));
            db.Generations.Add(generation);
            await db.SaveChangesAsync();
        }
        HighlightSelectionService service = new(
            factory,
            new RecommendedSelectionOptions(),
            new EffectPlanner(),
            TimeProvider.System);

        await service.SaveSelectionAsync(
            "uncapped-selection",
            ids,
            EffectPreset.Clean,
            CancellationToken.None);

        await using GenerationDbContext verification =
            await factory.CreateDbContextAsync();
        Generation saved = await verification.Generations
            .Include(value => value.Highlights)
            .SingleAsync();
        Assert.Equal(12, saved.MaximumHighlights);
        Assert.Equal(12, saved.Highlights.Count(value => value.SelectedByUser));
        Assert.Equal(
            ids,
            saved.Highlights
                .Where(value => value.SelectedByUser)
                .OrderBy(value => value.SelectionOrder)
                .Select(value => value.HighlightId));
    }

    [Fact]
    public async Task ReselectionReusesAnalyzedMusicAndReplacesEffectPlans()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<GenerationDbContext> dbOptions =
            new DbContextOptionsBuilder<GenerationDbContext>()
                .UseSqlite(connection)
                .Options;
        TestFactory factory = new(dbOptions);
        await using (GenerationDbContext db =
                     await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            Generation generation = new()
            {
                PublicId = "stage5-reselection",
                Status = GenerationStatus.AwaitingHighlightSelection,
                SelectedSteamId = "76561198000000001"
            };
            generation.Highlights.Add(Highlight("h1", 100, 500));
            generation.Highlights.Add(Highlight("h2", 600, 1100));
            generation.Artifacts.Add(new GenerationArtifact
            {
                Type = ArtifactType.MusicAnalysis,
                FileName = "music-analysis.json",
                StoredPath = "music-analysis.json",
                ContentType = "application/json",
                CreatedAt = DateTimeOffset.UtcNow
            });
            generation.Music = new GenerationMusic
            {
                OriginalFileName = "track.mp3",
                StoredPath = "track.mp3",
                Sha256 = new string('a', 64),
                DurationMilliseconds = 30_000,
                RightsConfirmed = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Generations.Add(generation);
            await db.SaveChangesAsync();
            generation.Music.AnalysisArtifactId = generation.Artifacts.Single().Id;
            generation.EffectPlans.Add(new GenerationEffectPlan
            {
                GenerationHighlightId = generation.Highlights[0].Id,
                Preset = EffectPreset.Clean,
                TimelineJson = "[]",
                EffectPlanJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        HighlightSelectionService service = new(
            factory,
            new RecommendedSelectionOptions(),
            new EffectPlanner(),
            TimeProvider.System);

        await service.SaveSelectionAsync(
            "stage5-reselection",
            ["h2"],
            EffectPreset.Dynamic,
            CancellationToken.None);

        await using GenerationDbContext verification =
            await factory.CreateDbContextAsync();
        Generation saved = await verification.Generations
            .Include(value => value.Highlights)
            .SingleAsync();
        Assert.Equal(
            GenerationStatus.AwaitingMovieConfiguration,
            saved.Status);
        Assert.Equal(
            ["h2"],
            saved.Highlights
                .Where(value => value.SelectedByUser)
                .Select(value => value.HighlightId));
        GenerationEffectPlan effect = Assert.Single(
            await verification.GenerationEffectPlans.ToArrayAsync());
        Assert.Equal(EffectPreset.Dynamic, effect.Preset);
        Assert.Equal(
            saved.Highlights.Single(value => value.HighlightId == "h2").Id,
            effect.GenerationHighlightId);
    }

    private static GenerationHighlight Highlight(
        string id,
        long start,
        long end) =>
        new()
        {
            HighlightId = id,
            SteamId = "76561198000000001",
            Type = nameof(HighlightType.SoloKill),
            StartTick = start,
            EndTick = end,
            FirstKillTick = start + 100,
            LastKillTick = start + 100,
            EstimatedDurationMilliseconds = 6000,
            MapName = "de_test",
            WeaponSequenceJson = "[]",
            TagsJson = "[]",
            KillsJson = "[]",
            ScoreBreakdownJson = "{}"
        };

    private static KillDescriptor KillDescriptor(
        int index,
        long tick,
        bool headshot) =>
        new(index, tick, "p1", $"v{index}", "ak47", headshot);

    private sealed class TestFactory(
        DbContextOptions<GenerationDbContext> options)
        : IDbContextFactory<GenerationDbContext>
    {
        public GenerationDbContext CreateDbContext() => new(options);

        public Task<GenerationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
