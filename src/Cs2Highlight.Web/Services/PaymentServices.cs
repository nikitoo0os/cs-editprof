using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed class PaymentOptions
{
    public string Provider { get; set; } = "YooKassa";
    public long PriceAmountMinor { get; set; } = 100;
    public string Currency { get; set; } = "RUB";
    public string ShopId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string ReturnUrlBase { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.yookassa.ru/v3/";

    public bool UsesYooKassa =>
        Provider.Equals("YooKassa", StringComparison.OrdinalIgnoreCase);

}

public sealed record PaymentRequest(
    string GenerationPublicId,
    long AmountMinor,
    string Currency,
    string IdempotencyKey,
    string ReturnUrl,
    string Description);

public enum ProviderPaymentStatus { Pending, Succeeded, Canceled }

public sealed record PaymentSessionResult(
    bool Success,
    string ProviderPaymentId,
    string? ConfirmationUrl,
    string? ErrorCode);

public sealed record PaymentStatusResult(
    bool Success,
    string ProviderPaymentId,
    ProviderPaymentStatus Status,
    string? ErrorCode);

public sealed record PaymentLaunch(Payment Payment, string ConfirmationUrl);

public interface IPaymentProvider
{
    string Name { get; }
    Task<PaymentSessionResult> CreateSessionAsync(
        PaymentRequest request,
        CancellationToken cancellationToken);
    Task<PaymentStatusResult> GetStatusAsync(
        string providerPaymentId,
        CancellationToken cancellationToken);
}

public sealed partial class YooKassaPaymentProvider(
    HttpClient client,
    PaymentOptions options,
    ILogger<YooKassaPaymentProvider> logger) : IPaymentProvider
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public string Name => "YooKassa";

    public async Task<PaymentSessionResult> CreateSessionAsync(
        PaymentRequest request,
        CancellationToken cancellationToken)
    {
        string? configurationError = ValidateConfiguration();
        if (configurationError is not null)
            return new(false, string.Empty, null, configurationError);
        if (request.AmountMinor <= 0 || request.Currency.Length != 3)
            return new(false, string.Empty, null, "YOOKASSA_AMOUNT_INVALID");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 64)
            return new(false, string.Empty, null, "YOOKASSA_IDEMPOTENCY_KEY_INVALID");
        if (!Uri.TryCreate(request.ReturnUrl, UriKind.Absolute, out Uri? returnUrl) ||
            (returnUrl.Scheme != Uri.UriSchemeHttps && returnUrl.Scheme != Uri.UriSchemeHttp))
        {
            return new(false, string.Empty, null, "YOOKASSA_RETURN_URL_INVALID");
        }

        var body = new
        {
            amount = new
            {
                value = (request.AmountMinor / 100m).ToString("0.00", CultureInfo.InvariantCulture),
                currency = request.Currency.ToUpperInvariant()
            },
            capture = true,
            confirmation = new
            {
                type = "redirect",
                return_url = request.ReturnUrl
            },
            description = request.Description[..Math.Min(request.Description.Length, 128)],
            metadata = new { order_id = request.GenerationPublicId }
        };

        using HttpRequestMessage message = new(HttpMethod.Post, "payments");
        AddAuthentication(message);
        message.Headers.Add("Idempotence-Key", request.IdempotencyKey);
        message.Content = JsonContent.Create(body, options: JsonOptions);

        try
        {
            using HttpResponseMessage response =
                await client.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(false, string.Empty, null, await ReadErrorCodeAsync(response, cancellationToken));

            YooKassaPaymentResponse? payment = await response.Content.ReadFromJsonAsync<YooKassaPaymentResponse>(
                JsonOptions, cancellationToken);
            string? paymentId = payment?.Id;
            string? confirmationUrl = payment?.Confirmation?.ConfirmationUrl;
            if (string.IsNullOrWhiteSpace(paymentId) ||
                string.IsNullOrWhiteSpace(confirmationUrl) ||
                !Uri.TryCreate(confirmationUrl, UriKind.Absolute, out Uri? confirmationUri) ||
                confirmationUri.Scheme != Uri.UriSchemeHttps)
            {
                return new(false, string.Empty, null, "YOOKASSA_INVALID_RESPONSE");
            }

            return new(true, paymentId, confirmationUrl, null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogPaymentCreationFailed(logger, exception);
            return new(false, string.Empty, null, "YOOKASSA_UNAVAILABLE");
        }
    }

    public async Task<PaymentStatusResult> GetStatusAsync(
        string providerPaymentId,
        CancellationToken cancellationToken)
    {
        string? configurationError = ValidateConfiguration();
        if (configurationError is not null)
            return new(false, providerPaymentId, ProviderPaymentStatus.Pending, configurationError);
        if (string.IsNullOrWhiteSpace(providerPaymentId) || providerPaymentId.Length > 128)
            return new(false, providerPaymentId, ProviderPaymentStatus.Pending, "PAYMENT_ID_INVALID");

        using HttpRequestMessage message = new(
            HttpMethod.Get,
            $"payments/{Uri.EscapeDataString(providerPaymentId)}");
        AddAuthentication(message);

        try
        {
            using HttpResponseMessage response =
                await client.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(
                    false,
                    providerPaymentId,
                    ProviderPaymentStatus.Pending,
                    await ReadErrorCodeAsync(response, cancellationToken));
            }

            YooKassaPaymentResponse? payment = await response.Content.ReadFromJsonAsync<YooKassaPaymentResponse>(
                JsonOptions, cancellationToken);
            string? paymentId = payment?.Id;
            if (string.IsNullOrWhiteSpace(paymentId) || payment is null)
                return new(false, providerPaymentId, ProviderPaymentStatus.Pending, "YOOKASSA_INVALID_RESPONSE");

            ProviderPaymentStatus status = payment.Status switch
            {
                "succeeded" => ProviderPaymentStatus.Succeeded,
                "canceled" => ProviderPaymentStatus.Canceled,
                _ => ProviderPaymentStatus.Pending
            };
            return new(true, paymentId, status, null);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            LogPaymentStatusRequestFailed(logger, exception);
            return new(false, providerPaymentId, ProviderPaymentStatus.Pending, "YOOKASSA_UNAVAILABLE");
        }
    }

    private string? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(options.ShopId)) return "YOOKASSA_SHOP_ID_REQUIRED";
        if (string.IsNullOrWhiteSpace(options.SecretKey)) return "YOOKASSA_SECRET_KEY_REQUIRED";
        return null;
    }

    private void AddAuthentication(HttpRequestMessage message)
    {
        string credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{options.ShopId}:{options.SecretKey}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            YooKassaErrorResponse? error = await response.Content.ReadFromJsonAsync<YooKassaErrorResponse>(
                JsonOptions, cancellationToken);
            string? errorCode = error?.Code;
            if (!string.IsNullOrWhiteSpace(errorCode))
                return $"YOOKASSA_{errorCode.ToUpperInvariant()}";
        }
        catch (JsonException)
        {
            // Return a stable error without exposing the provider response.
        }

        return $"YOOKASSA_HTTP_{(int)response.StatusCode}";
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "YooKassa payment creation failed.")]
    private static partial void LogPaymentCreationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning, Message = "YooKassa payment status request failed.")]
    private static partial void LogPaymentStatusRequestFailed(ILogger logger, Exception exception);

    private sealed record YooKassaPaymentResponse(
        string Id,
        string Status,
        YooKassaConfirmation? Confirmation);

    private sealed record YooKassaConfirmation(
        [property: JsonPropertyName("confirmation_url")] string? ConfirmationUrl);

    private sealed record YooKassaErrorResponse(string? Code);
}

public sealed record TokenPaymentLaunch(
    TokenPurchase Purchase,
    string ConfirmationUrl);

public sealed class TokenPaymentService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    IPaymentProvider provider,
    ITokenService tokens,
    TimeProvider timeProvider)
{
    public async Task<TokenPaymentLaunch> CreateAsync(
        string userId,
        string packageCode,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        TokenPackage package = await db.TokenPackages.SingleOrDefaultAsync(
                value => value.Code == packageCode && value.IsActive,
                cancellationToken) ??
            throw new InvalidOperationException("TOKEN_PACKAGE_NOT_FOUND");
        TokenPurchase? purchase = await db.TokenPurchases
            .Where(value =>
                value.UserId == userId &&
                value.TokenPackageId == package.Id &&
                value.Status == TokenPurchaseStatus.Pending)
            // SQLite cannot translate ORDER BY DateTimeOffset. The identity
            // key is monotonic for purchases and gives the same newest-first
            // behavior without provider-specific date SQL.
            .OrderByDescending(value => value.Id)
            .FirstOrDefaultAsync(cancellationToken);
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (purchase is null)
        {
            purchase = new TokenPurchase
            {
                UserId = userId,
                TokenPackageId = package.Id,
                Provider = provider.Name,
                // YooKassa limits Idempotence-Key to 64 characters. The user is
                // already part of the purchase row, so do not embed the user id.
                IdempotencyKey = $"token-purchase:{Guid.NewGuid():N}",
                AmountMinor = package.PriceAmountMinor,
                Currency = package.Currency,
                Status = TokenPurchaseStatus.Pending,
                CreatedAtUtc = now
            };
            db.TokenPurchases.Add(purchase);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(purchase.ConfirmationUrl))
        {
            return new(purchase, purchase.ConfirmationUrl);
        }

        PaymentSessionResult session = await provider.CreateSessionAsync(
            new PaymentRequest(
                $"token-purchase-{purchase.Id}",
                purchase.AmountMinor,
                purchase.Currency,
                purchase.IdempotencyKey,
                AddPurchaseId(returnUrl, purchase.Id),
                $"CSHighlighter: пакет токенов {package.Name}"),
            cancellationToken);
        if (!session.Success || string.IsNullOrWhiteSpace(session.ConfirmationUrl))
        {
            purchase.Status = TokenPurchaseStatus.Failed;
            purchase.FailureCode = session.ErrorCode ?? "PAYMENT_CREATE_FAILED";
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(purchase.FailureCode);
        }

        purchase.Provider = provider.Name;
        purchase.ProviderPaymentId = session.ProviderPaymentId;
        purchase.ConfirmationUrl = session.ConfirmationUrl;
        purchase.Status = TokenPurchaseStatus.Pending;
        await db.SaveChangesAsync(cancellationToken);
        return new(purchase, session.ConfirmationUrl);
    }

    private static string AddPurchaseId(string returnUrl, long purchaseId) =>
        returnUrl + (returnUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?') +
        $"purchaseId={purchaseId}";

    public async Task<TokenPurchaseStatus> RefreshAsync(
        long purchaseId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        TokenPurchase purchase = await db.TokenPurchases
                .Include(value => value.TokenPackage)
                .SingleOrDefaultAsync(
                    value => value.Id == purchaseId && value.UserId == userId,
                    cancellationToken) ??
            throw new InvalidOperationException("TOKEN_PURCHASE_NOT_FOUND");
        return await RefreshAsync(db, purchase, cancellationToken);
    }

    public async Task<bool> RefreshByProviderPaymentIdAsync(
        string providerPaymentId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        TokenPurchase? purchase = await db.TokenPurchases
            .Include(value => value.TokenPackage)
            .SingleOrDefaultAsync(
                value => value.ProviderPaymentId == providerPaymentId,
                cancellationToken);
        if (purchase is null)
            return false;
        await RefreshAsync(db, purchase, cancellationToken);
        return true;
    }

    private async Task<TokenPurchaseStatus> RefreshAsync(
        GenerationDbContext db,
        TokenPurchase purchase,
        CancellationToken cancellationToken)
    {
        if (purchase.Status == TokenPurchaseStatus.Succeeded)
        {
            await tokens.CreditAsync(
                purchase.UserId,
                purchase.TokenPackage.TokenAmount,
                TokenTransactionType.Purchase,
                $"purchase:{purchase.Id}",
                $"Покупка: {purchase.TokenPackage.Name}",
                purchase.Id,
                null,
                cancellationToken);
            return purchase.Status;
        }
        if (string.IsNullOrWhiteSpace(purchase.ProviderPaymentId))
            throw new InvalidOperationException("PAYMENT_ID_MISSING");

        PaymentStatusResult status = await provider.GetStatusAsync(
            purchase.ProviderPaymentId,
            cancellationToken);
        if (!status.Success)
            throw new InvalidOperationException(
                status.ErrorCode ?? "PAYMENT_STATUS_FAILED");
        if (status.Status == ProviderPaymentStatus.Pending)
            return purchase.Status;
        if (status.Status == ProviderPaymentStatus.Canceled)
        {
            purchase.Status = TokenPurchaseStatus.Cancelled;
            purchase.FailureCode = "PAYMENT_CANCELED";
            await db.SaveChangesAsync(cancellationToken);
            return purchase.Status;
        }

        await tokens.CreditAsync(
            purchase.UserId,
            purchase.TokenPackage.TokenAmount,
            TokenTransactionType.Purchase,
            $"purchase:{purchase.Id}",
            $"Покупка: {purchase.TokenPackage.Name}",
            purchase.Id,
            null,
            cancellationToken);
        purchase.Status = TokenPurchaseStatus.Succeeded;
        purchase.PaidAtUtc = timeProvider.GetUtcNow();
        purchase.FailureCode = null;
        await db.SaveChangesAsync(cancellationToken);
        return purchase.Status;
    }
}

public sealed record YooKassaNotificationPayload(string? Id);
public sealed record YooKassaNotification(
    string? Event,
    [property: JsonPropertyName("object")] YooKassaNotificationPayload? Payload);

public sealed class PaymentService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    IPaymentProvider provider,
    TimeProvider timeProvider,
    GenerationWakeSignal queue,
    IInteractiveTimelineDirector? timelineDirector = null)
{
    public async Task<PaymentLaunch> CreateAsync(
        string publicId,
        string returnUrl,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(
            value => value.PublicId == publicId, cancellationToken);
        Payment? existing = await db.Payments.SingleOrDefaultAsync(
            value => value.GenerationId == generation.Id, cancellationToken);
        if (existing is not null &&
            existing.Status is PaymentStatus.Pending or PaymentStatus.Succeeded)
        {
            string? existingConfirmationUrl = existing.ConfirmationUrl;
            if (string.IsNullOrWhiteSpace(existingConfirmationUrl))
                throw new InvalidOperationException("PAYMENT_CONFIRMATION_URL_MISSING");
            return new(existing, existingConfirmationUrl);
        }
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

        string key = existing is null
            ? generation.PaymentIdempotencyKey ?? $"generation-{generation.PublicId}"
            : $"payment-{Guid.NewGuid():N}";

        PaymentSessionResult session = await provider.CreateSessionAsync(
            new PaymentRequest(
                publicId,
                generation.PriceAmountMinor,
                generation.PriceCurrency,
                key,
                returnUrl,
                $"CSHighlighter: создание CS2-мувика, заказ {publicId[..Math.Min(12, publicId.Length)]}"),
            cancellationToken);
        string? confirmationUrl = session.ConfirmationUrl;
        if (!session.Success || string.IsNullOrWhiteSpace(confirmationUrl))
            throw new InvalidOperationException(session.ErrorCode ?? "PAYMENT_CREATE_FAILED");

        DateTimeOffset now = timeProvider.GetUtcNow();
        Payment payment = existing ?? new Payment
        {
            GenerationId = generation.Id,
            CreatedAt = now
        };
        payment.Provider = provider.Name;
        payment.ProviderPaymentId = session.ProviderPaymentId;
        payment.ConfirmationUrl = confirmationUrl;
        payment.IdempotencyKey = key;
        payment.Status = PaymentStatus.Pending;
        payment.AmountMinor = generation.PriceAmountMinor;
        payment.Currency = generation.PriceCurrency;
        payment.UpdatedAt = now;
        payment.SucceededAt = null;
        payment.FailureCode = null;
        generation.PaymentId = session.ProviderPaymentId;
        generation.PaymentIdempotencyKey = key;
        generation.PaymentStatus = PaymentStatus.Pending;
        GenerationStateMachine.Transition(generation, GenerationStatus.PaymentProcessing, now);
        if (existing is null) db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        return new(payment, confirmationUrl);
    }

    public async Task<PaymentStatus> RefreshAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await db.Generations.SingleAsync(
            value => value.PublicId == publicId, cancellationToken);
        Payment payment = await db.Payments.SingleAsync(
            value => value.GenerationId == generation.Id, cancellationToken);
        return await RefreshAsync(db, generation, payment, cancellationToken);
    }

    public async Task<bool> RefreshByProviderPaymentIdAsync(
        string providerPaymentId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Payment? payment = await db.Payments.Include(value => value.Generation)
            .SingleOrDefaultAsync(
                value => value.ProviderPaymentId == providerPaymentId,
                cancellationToken);
        if (payment is null) return false;
        await RefreshAsync(db, payment.Generation, payment, cancellationToken);
        return true;
    }

    private async Task<PaymentStatus> RefreshAsync(
        GenerationDbContext db,
        Generation generation,
        Payment payment,
        CancellationToken cancellationToken)
    {
        if (payment.Status == PaymentStatus.Succeeded)
        {
            queue.Wake();
            return payment.Status;
        }

        PaymentStatusResult status = await provider.GetStatusAsync(
            payment.ProviderPaymentId, cancellationToken);
        if (!status.Success)
            throw new InvalidOperationException(status.ErrorCode ?? "PAYMENT_STATUS_FAILED");

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (status.Status == ProviderPaymentStatus.Pending)
            return payment.Status;
        if (status.Status == ProviderPaymentStatus.Canceled)
        {
            payment.Status = PaymentStatus.Cancelled;
            payment.FailureCode = "PAYMENT_CANCELED";
            payment.UpdatedAt = now;
            generation.PaymentStatus = PaymentStatus.Cancelled;
            if (generation.Status == GenerationStatus.PaymentProcessing)
                GenerationStateMachine.Transition(generation, GenerationStatus.AwaitingPayment, now);
            await db.SaveChangesAsync(cancellationToken);
            return payment.Status;
        }

        payment.Status = PaymentStatus.Succeeded;
        payment.SucceededAt = now;
        payment.UpdatedAt = now;
        payment.FailureCode = null;
        generation.PaymentStatus = PaymentStatus.Succeeded;
        generation.PaidAt = now;
        GenerationMovieSettings settings = await db.GenerationMovieSettings.SingleAsync(
            value => value.GenerationId == generation.Id,
            cancellationToken);
        settings.LockedAt ??= now;
        if (timelineDirector is not null)
        {
            await timelineDirector.LockAfterPaymentAsync(
                generation.Id, now, db, cancellationToken);
        }
        if (generation.Status == GenerationStatus.PaymentProcessing)
        {
            GenerationStateMachine.Transition(generation, GenerationStatus.Paid, now);
            GenerationStateMachine.Transition(
                generation, GenerationStatus.QueuedForGeneration, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        queue.Wake();
        return payment.Status;
    }
}
