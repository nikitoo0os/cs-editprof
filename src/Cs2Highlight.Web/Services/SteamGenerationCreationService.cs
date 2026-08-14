using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed class SteamGenerationCreationService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    SteamDemoImportService imports,
    GenerationWakeSignal queue,
    PaymentOptions paymentOptions,
    TimeProvider timeProvider,
    GenerationMetrics metrics)
{
    public async Task<string> CreateAsync(
        ApplicationUser user,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        if (user.TokenBalance < 1)
            throw new InvalidOperationException("TOKEN_BALANCE_INSUFFICIENT");
        DateTimeOffset now = timeProvider.GetUtcNow();
        Generation generation = new()
        {
            PublicId = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            PriceAmountMinor = paymentOptions.PriceAmountMinor,
            PriceCurrency = paymentOptions.Currency.ToUpperInvariant(),
            UserId = user.Id,
            QueueEnteredAtUtc = now
        };
        GenerationStateMachine.Transition(generation, GenerationStatus.Uploading, now);
        IReadOnlyList<StoredUpload> stored = await imports.ImportAsync(
            generation.PublicId, codes, cancellationToken);
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        db.Generations.Add(generation);
        int order = 0;
        foreach (StoredUpload upload in stored)
        {
            generation.Demos.Add(new GenerationDemo
            {
                OriginalFileName = upload.OriginalFileName,
                StoredPath = upload.StoredPath,
                FileSizeBytes = upload.Size,
                Sha256 = upload.Sha256,
                UploadOrder = ++order,
                AnalysisStatus = DemoAnalysisStatus.Pending
            });
        }
        GenerationStateMachine.Transition(generation, GenerationStatus.Uploaded, now);
        generation.ProgressPercent = 10;
        GenerationStateMachine.Transition(generation, GenerationStatus.QueuedForAnalysis, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.GenerationCreated.Add(1);
        queue.Wake();
        return generation.PublicId;
    }
}
