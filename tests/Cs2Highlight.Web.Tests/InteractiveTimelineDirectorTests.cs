using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Tests;

public sealed class InteractiveTimelineDirectorTests :
    IAsyncLifetime,
    IDisposable
{
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
            }));
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

    public async Task DisposeAsync() =>
        await connection.DisposeAsync();

    public void Dispose()
    {
        connection.Dispose();
        if (Directory.Exists(storageRoot))
            Directory.Delete(storageRoot, recursive: true);
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
        generation.Highlights.AddRange(
        [
            Highlight(
                generation.Id,
                "highlight-triple",
                "TripleKill",
                94,
                88),
            Highlight(
                generation.Id,
                "highlight-solo",
                "SoloKill",
                90,
                91)
        ]);
        await db.SaveChangesAsync();
    }

    private static GenerationHighlight Highlight(
        long generationId,
        string id,
        string type,
        double total,
        double beauty) =>
        new()
        {
            GenerationId = generationId,
            GenerationDemoId = 0,
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
