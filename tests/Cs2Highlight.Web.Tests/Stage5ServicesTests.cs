using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        Assert.Single(first.Events.Where(value => value.Type == EffectType.SmoothZoom));
        Assert.Single(first.Events.Where(value => value.Type == EffectType.HeadshotFlash));
        Assert.Contains(first.Events, value => value.Type == EffectType.VignettePulse);
        Assert.Contains(first.Events, value => value.Type == EffectType.ClipTransition);
        Assert.All(
            first.Events.Where(value => value.Type == EffectType.SmoothZoom),
            value => Assert.InRange(value.Intensity, 0, 0.08));
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
                new(EffectType.HeadshotFlash, 340, 80, 0.1),
                new(EffectType.VignettePulse, 360, 300, 0.16)
            ]);

        string video = FfmpegEffectFilterBuilder.Build(1920, 1080, 60, 8, plan);
        string audio = FfmpegEffectFilterBuilder.BuildAudio(8, plan);

        Assert.Contains("if(lt(t", video);
        Assert.Contains("vignette=PI/12", video);
        Assert.Contains("eq=brightness=0.1", video);
        Assert.Contains("fade=t=out:st=7.7:d=0.3", video);
        Assert.Contains("loudnorm", audio);
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
            Assert.Equal(GenerationStatus.AwaitingPayment, saved.Status);
            Assert.Equal(EffectPreset.Dynamic, saved.EffectPreset);
            Assert.Equal(5, saved.MaximumHighlights);
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
