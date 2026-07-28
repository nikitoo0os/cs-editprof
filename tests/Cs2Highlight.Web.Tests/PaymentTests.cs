using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Cs2Highlight.Music;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Tests;

public sealed class PaymentTests : IAsyncLifetime, IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private TestDbFactory factory = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        DbContextOptions<GenerationDbContext> options =
            new DbContextOptionsBuilder<GenerationDbContext>().UseSqlite(connection).Options;
        factory = new TestDbFactory(options);
        await using GenerationDbContext db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task ConfirmationIsIdempotentAndQueuesOnce()
    {
        string publicId = Guid.NewGuid().ToString("N");
        await using (GenerationDbContext db = await factory.CreateDbContextAsync())
        {
            await AddPayableGenerationAsync(db, publicId);
        }
        GenerationWakeSignal wake = new();
        PaymentService service = new(factory, new TestPaymentProvider(), TimeProvider.System, wake);

        Payment first = await service.CreateAsync(publicId, CancellationToken.None);
        Payment second = await service.CreateAsync(publicId, CancellationToken.None);
        await service.ConfirmAsync(publicId, true, CancellationToken.None);
        await service.ConfirmAsync(publicId, true, CancellationToken.None);

        await using GenerationDbContext verification = await factory.CreateDbContextAsync();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await verification.Payments.CountAsync());
        Assert.Equal(PaymentStatus.Succeeded, (await verification.Payments.SingleAsync()).Status);
        Assert.Equal(
            GenerationStatus.QueuedForGeneration,
            (await verification.Generations.SingleAsync()).Status);
    }

    [Fact]
    public async Task PendingTestPaymentCanBeConfirmedAfterProviderRestart()
    {
        string publicId = Guid.NewGuid().ToString("N");
        await using (GenerationDbContext db = await factory.CreateDbContextAsync())
        {
            await AddPayableGenerationAsync(db, publicId);
        }
        await new PaymentService(
            factory, new TestPaymentProvider(), TimeProvider.System, new GenerationWakeSignal())
            .CreateAsync(publicId, CancellationToken.None);

        PaymentService restarted = new(
            factory, new TestPaymentProvider(), TimeProvider.System, new GenerationWakeSignal());
        await restarted.ConfirmAsync(publicId, true, CancellationToken.None);

        await using GenerationDbContext verification = await factory.CreateDbContextAsync();
        Assert.Equal(PaymentStatus.Succeeded, (await verification.Payments.SingleAsync()).Status);
        Assert.Equal(
            GenerationStatus.QueuedForGeneration,
            (await verification.Generations.SingleAsync()).Status);
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();
    public void Dispose() => connection.Dispose();

    private static async Task AddPayableGenerationAsync(
        GenerationDbContext db,
        string publicId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Generation generation = new()
        {
            PublicId = publicId,
            Status = GenerationStatus.AwaitingPayment,
            CurrentStage = "AwaitingPayment",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Generations.Add(generation);
        await db.SaveChangesAsync();
        GenerationArtifact artifact = new()
        {
            GenerationId = generation.Id,
            Type = ArtifactType.MusicAnalysis,
            FileName = "music-analysis.json",
            StoredPath = "music-analysis.json",
            FileSizeBytes = 1,
            CreatedAt = now
        };
        db.GenerationArtifacts.Add(artifact);
        await db.SaveChangesAsync();
        db.GenerationMusic.Add(new GenerationMusic
        {
            GenerationId = generation.Id,
            OriginalFileName = "music.wav",
            StoredPath = "music.wav",
            FileSizeBytes = 1,
            Sha256 = new string('a', 64),
            DurationMilliseconds = 30_000,
            SampleRate = 48_000,
            Channels = 2,
            AnalysisArtifactId = artifact.Id,
            RightsConfirmed = true,
            RightsConfirmedAt = now,
            CreatedAt = now
        });
        db.GenerationMovieSettings.Add(new GenerationMovieSettings
        {
            GenerationId = generation.Id,
            MovieStyle = MovieStyle.Dynamic,
            SyncIntensity = MusicSyncIntensity.Expressive,
            ColorGradePreset = ColorGradePreset.Natural,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private sealed class TestDbFactory(DbContextOptions<GenerationDbContext> options)
        : IDbContextFactory<GenerationDbContext>
    {
        public GenerationDbContext CreateDbContext() => new(options);
        public Task<GenerationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GenerationDbContext(options));
    }
}
