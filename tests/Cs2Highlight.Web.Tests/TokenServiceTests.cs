using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Tests;

public sealed class TokenServiceTests
{
    [Fact]
    public async Task RefundRequiresACompletedDebitForTheGeneration()
    {
        await using SqliteConnection connection =
            new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<GenerationDbContext> options =
            new DbContextOptionsBuilder<GenerationDbContext>()
                .UseSqlite(connection)
                .Options;
        Factory factory = new(options);
        long generationId;
        const string userId = "token-user";
        await using (GenerationDbContext db =
                     await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "token@example.test",
                NormalizedUserName = "TOKEN@EXAMPLE.TEST",
                Email = "token@example.test",
                NormalizedEmail = "TOKEN@EXAMPLE.TEST",
                TokenBalance = 1,
                ReferralCode = "TOKENUSER1"
            });
            Generation generation = new()
            {
                PublicId = "token-generation",
                UserId = userId
            };
            db.Generations.Add(generation);
            await db.SaveChangesAsync();
            generationId = generation.Id;
        }

        TokenService service = new(
            factory,
            TimeProvider.System,
            new GenerationMetrics());

        TokenTransaction? absentRefund = await service.RefundAsync(
            userId,
            generationId,
            "render failed",
            CancellationToken.None);
        Assert.Null(absentRefund);

        await using (GenerationDbContext db =
                     await factory.CreateDbContextAsync())
        {
            Assert.Equal(
                1,
                await db.Users.Where(value => value.Id == userId)
                    .Select(value => value.TokenBalance)
                    .SingleAsync());
            Assert.Empty(await db.TokenTransactions.ToArrayAsync());

            await service.DebitAsync(
                db,
                userId,
                generationId,
                CancellationToken.None);
            await db.SaveChangesAsync();
        }

        TokenTransaction? actualRefund = await service.RefundAsync(
            userId,
            generationId,
            "post-publication failure",
            CancellationToken.None);
        Assert.NotNull(actualRefund);

        await using (GenerationDbContext db =
                     await factory.CreateDbContextAsync())
        {
            Assert.Equal(
                1,
                await db.Users.Where(value => value.Id == userId)
                    .Select(value => value.TokenBalance)
                    .SingleAsync());
            Assert.Equal(2, await db.TokenTransactions.CountAsync());
        }
    }

    private sealed class Factory(
        DbContextOptions<GenerationDbContext> options) :
        IDbContextFactory<GenerationDbContext>
    {
        public GenerationDbContext CreateDbContext() => new(options);

        public Task<GenerationDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
