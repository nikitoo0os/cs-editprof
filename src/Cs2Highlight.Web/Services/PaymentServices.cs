using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed record PaymentRequest(
    string GenerationPublicId,
    long AmountMinor,
    string Currency,
    string IdempotencyKey);
public sealed record PaymentSessionResult(bool Success, string ProviderPaymentId, string? ErrorCode);
public sealed record PaymentConfirmationResult(bool Success, string ProviderPaymentId, string? ErrorCode);

public interface IPaymentProvider
{
    Task<PaymentSessionResult> CreateSessionAsync(PaymentRequest request, CancellationToken cancellationToken);
    Task<PaymentConfirmationResult> ConfirmAsync(string providerPaymentId, CancellationToken cancellationToken);
}

public sealed class TestPaymentProvider : IPaymentProvider
{
    private readonly Dictionary<string, bool> sessions = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public Task<PaymentSessionResult> CreateSessionAsync(
        PaymentRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.AmountMinor != 100 || request.Currency != "USD")
        {
            return Task.FromResult(new PaymentSessionResult(false, string.Empty, "INVALID_PRICE"));
        }
        string id = $"test_{request.IdempotencyKey}";
        lock (gate) sessions.TryAdd(id, false);
        return Task.FromResult(new PaymentSessionResult(true, id, null));
    }

    public Task<PaymentConfirmationResult> ConfirmAsync(
        string providerPaymentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!sessions.ContainsKey(providerPaymentId) &&
                !providerPaymentId.StartsWith("test_generation-", StringComparison.Ordinal))
                return Task.FromResult(new PaymentConfirmationResult(false, providerPaymentId, "PAYMENT_NOT_FOUND"));
            sessions[providerPaymentId] = true;
        }
        return Task.FromResult(new PaymentConfirmationResult(true, providerPaymentId, null));
    }
}

public sealed class PaymentService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    IPaymentProvider provider,
    TimeProvider timeProvider,
    GenerationWakeSignal queue)
{
    public async Task<Payment> CreateAsync(string publicId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(value => value.PublicId == publicId, cancellationToken);
        string key = generation.PaymentIdempotencyKey ?? $"generation-{generation.PublicId}";
        Payment? existing = await db.Payments.SingleOrDefaultAsync(
            value => value.IdempotencyKey == key, cancellationToken);
        if (existing is not null) return existing;
        if (generation.Status != GenerationStatus.AwaitingPayment)
            throw new InvalidOperationException("Generation is not awaiting payment.");
        GenerationMusic? music = await db.GenerationMusic.SingleOrDefaultAsync(
            value => value.GenerationId == generation.Id, cancellationToken);
        GenerationMovieSettings? movieSettings =
            await db.GenerationMovieSettings.SingleOrDefaultAsync(
                value => value.GenerationId == generation.Id, cancellationToken);
        if (music is null || !music.RightsConfirmed || music.AnalysisArtifactId is null)
            throw new InvalidOperationException("MUSIC_NOT_READY");
        if (movieSettings is null)
            throw new InvalidOperationException("MOVIE_SETTINGS_REQUIRED");
        PaymentSessionResult session = await provider.CreateSessionAsync(
            new PaymentRequest(publicId, 100, "USD", key), cancellationToken);
        if (!session.Success) throw new InvalidOperationException(session.ErrorCode);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Payment payment = new()
        {
            GenerationId = generation.Id,
            ProviderPaymentId = session.ProviderPaymentId,
            IdempotencyKey = key,
            Status = PaymentStatus.Pending,
            AmountMinor = 100,
            Currency = "USD",
            CreatedAt = now,
            UpdatedAt = now
        };
        generation.PaymentId = session.ProviderPaymentId;
        generation.PaymentIdempotencyKey = key;
        generation.PaymentStatus = PaymentStatus.Pending;
        GenerationStateMachine.Transition(generation, GenerationStatus.PaymentProcessing, now);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        return payment;
    }

    public async Task ConfirmAsync(string publicId, bool approve, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(value => value.PublicId == publicId, cancellationToken);
        Payment payment = await db.Payments.SingleAsync(value => value.GenerationId == generation.Id, cancellationToken);
        if (payment.Status == PaymentStatus.Succeeded)
        {
            queue.Wake();
            return;
        }
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!approve)
        {
            payment.Status = PaymentStatus.Failed;
            payment.FailureCode = "TEST_PAYMENT_DECLINED";
            generation.PaymentStatus = PaymentStatus.Failed;
            GenerationStateMachine.Transition(generation, GenerationStatus.AwaitingPayment, now);
        }
        else
        {
            PaymentConfirmationResult confirmation = await provider.ConfirmAsync(
                payment.ProviderPaymentId, cancellationToken);
            if (!confirmation.Success) throw new InvalidOperationException(confirmation.ErrorCode);
            payment.Status = PaymentStatus.Succeeded;
            payment.SucceededAt = now;
            generation.PaymentStatus = PaymentStatus.Succeeded;
            generation.PaidAt = now;
            GenerationMovieSettings settings =
                await db.GenerationMovieSettings.SingleAsync(
                    value => value.GenerationId == generation.Id,
                    cancellationToken);
            settings.LockedAt ??= now;
            GenerationStateMachine.Transition(generation, GenerationStatus.Paid, now);
            GenerationStateMachine.Transition(generation, GenerationStatus.QueuedForGeneration, now);
        }
        payment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (approve) queue.Wake();
    }
}
