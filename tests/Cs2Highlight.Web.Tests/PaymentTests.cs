using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
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
            db.Generations.Add(new Generation
            {
                PublicId = publicId,
                Status = GenerationStatus.AwaitingPayment,
                CurrentStage = "AwaitingPayment",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
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
            db.Generations.Add(new Generation
            {
                PublicId = publicId,
                Status = GenerationStatus.AwaitingPayment,
                CurrentStage = "AwaitingPayment",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
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

    private sealed class TestDbFactory(DbContextOptions<GenerationDbContext> options)
        : IDbContextFactory<GenerationDbContext>
    {
        public GenerationDbContext CreateDbContext() => new(options);
        public Task<GenerationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GenerationDbContext(options));
    }
}
