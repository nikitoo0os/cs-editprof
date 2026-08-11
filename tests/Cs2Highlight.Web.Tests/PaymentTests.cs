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
        PaymentService service = new(factory, new StubPaymentProvider(), TimeProvider.System, wake);

        Payment first = (await service.CreateAsync(
            publicId, "https://example.test/return", CancellationToken.None)).Payment;
        Payment second = (await service.CreateAsync(
            publicId, "https://example.test/return", CancellationToken.None)).Payment;
        await service.RefreshAsync(publicId, CancellationToken.None);
        await service.RefreshAsync(publicId, CancellationToken.None);

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
            factory, new StubPaymentProvider(), TimeProvider.System, new GenerationWakeSignal())
            .CreateAsync(publicId, "https://example.test/return", CancellationToken.None);

        PaymentService restarted = new(
            factory, new StubPaymentProvider(), TimeProvider.System, new GenerationWakeSignal());
        await restarted.RefreshAsync(publicId, CancellationToken.None);

        await using GenerationDbContext verification = await factory.CreateDbContextAsync();
        Assert.Equal(PaymentStatus.Succeeded, (await verification.Payments.SingleAsync()).Status);
        Assert.Equal(
            GenerationStatus.QueuedForGeneration,
            (await verification.Generations.SingleAsync()).Status);
    }

    [Fact]
    public async Task TokenPackagePaymentCreditsOnceAndIsWebhookIdempotent()
    {
        const string userId = "token-purchase-user";
        const string packageCode = "creator";
        await using (GenerationDbContext db = await factory.CreateDbContextAsync())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "buyer@example.test",
                NormalizedUserName = "BUYER@EXAMPLE.TEST",
                Email = "buyer@example.test",
                NormalizedEmail = "BUYER@EXAMPLE.TEST",
                ReferralCode = "BUYER123"
            });
            db.TokenPackages.Add(new TokenPackage
            {
                Code = packageCode,
                Name = "15 токенов",
                TokenAmount = 15,
                PriceAmountMinor = 54900,
                Currency = "RUB"
            });
            await db.SaveChangesAsync();
        }

        TokenPaymentService service = new(
            factory,
            new StubPaymentProvider(),
            new TokenService(factory, TimeProvider.System, new GenerationMetrics()),
            TimeProvider.System);

        TokenPaymentLaunch first = await service.CreateAsync(
            userId, packageCode, "https://example.test/purchase/payment-return", CancellationToken.None);
        TokenPaymentLaunch second = await service.CreateAsync(
            userId, packageCode, "https://example.test/purchase/payment-return", CancellationToken.None);

        Assert.Equal(first.Purchase.Id, second.Purchase.Id);
        Assert.StartsWith("https://pay.example/", first.ConfirmationUrl);

        await service.RefreshAsync(first.Purchase.Id, userId, CancellationToken.None);
        Assert.True(await service.RefreshByProviderPaymentIdAsync(
            first.Purchase.ProviderPaymentId!, CancellationToken.None));

        await using GenerationDbContext verification = await factory.CreateDbContextAsync();
        Assert.Equal(15, await verification.Users
            .Where(value => value.Id == userId)
            .Select(value => value.TokenBalance)
            .SingleAsync());
        Assert.Equal(TokenPurchaseStatus.Succeeded,
            (await verification.TokenPurchases.SingleAsync()).Status);
        Assert.Equal(1, await verification.TokenTransactions
            .CountAsync(value => value.UserId == userId));
    }

    public async Task DisposeAsync() => await connection.DisposeAsync();
    public void Dispose() => connection.Dispose();

    private sealed class StubPaymentProvider : IPaymentProvider
    {
        public string Name => "YooKassa";

        public Task<PaymentSessionResult> CreateSessionAsync(
            PaymentRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PaymentSessionResult(
                true,
                $"stub_{request.IdempotencyKey}",
                $"https://pay.example/{Uri.EscapeDataString(request.IdempotencyKey)}",
                null));
        }

        public Task<PaymentStatusResult> GetStatusAsync(
            string providerPaymentId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PaymentStatusResult(
                true,
                providerPaymentId,
                ProviderPaymentStatus.Succeeded,
                null));
        }
    }

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
