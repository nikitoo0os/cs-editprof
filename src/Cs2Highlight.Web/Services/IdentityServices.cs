using System.Net;
using System.Net.Mail;
using System.Text.Encodings.Web;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public interface IEmailSender
{
    Task SendAsync(string email, string subject, string htmlMessage, CancellationToken cancellationToken = default);
}

public sealed partial class DevelopmentEmailSender(ILogger<DevelopmentEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string email, string subject, string htmlMessage, CancellationToken cancellationToken = default)
    {
        LogDevelopmentEmail(logger, email, subject, htmlMessage);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Information, Message = "Development email for {Email}: {Subject}. Open: {Message}")]
    private static partial void LogDevelopmentEmail(ILogger logger, string email, string subject, string message);
}

public sealed class SmtpEmailSender(IConfiguration configuration) : IEmailSender
{
    public async Task SendAsync(
        string email,
        string subject,
        string htmlMessage,
        CancellationToken cancellationToken = default)
    {
        IConfigurationSection settings = configuration.GetSection("Email:Smtp");
        string host = Required(settings["Host"], "Email:Smtp:Host");
        string fromAddress = Required(settings["FromAddress"], "Email:Smtp:FromAddress");
        int port = settings.GetValue("Port", 587);
        bool enableSsl = settings.GetValue("EnableSsl", true);
        string? userName = settings["UserName"];
        string? password = settings["Password"];

        using MailMessage message = new()
        {
            From = new MailAddress(fromAddress, settings["FromName"] ?? "CSHighlighter"),
            Subject = subject,
            Body = $"<p><a href=\"{HtmlEncoder.Default.Encode(htmlMessage)}\">Открыть ссылку</a></p>",
            IsBodyHtml = true
        };
        message.To.Add(email);
        using SmtpClient client = new(host, port)
        {
            EnableSsl = enableSsl
        };
        if (!string.IsNullOrWhiteSpace(userName))
            client.Credentials = new NetworkCredential(userName, password);
        await client.SendMailAsync(message, cancellationToken);
    }

    private static string Required(string? value, string key) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} must be configured to send account emails.")
            : value;
}

public interface ITokenService
{
    Task<TokenTransaction> DebitAsync(GenerationDbContext db, string userId, long generationId, CancellationToken cancellationToken);
    Task<TokenTransaction?> RefundAsync(string userId, long generationId, string reason, CancellationToken cancellationToken);
    Task<TokenTransaction> CreditAsync(string userId, int amount, TokenTransactionType type, string idempotencyKey, string description, long? purchaseId, long? referralId, CancellationToken cancellationToken);
    Task<TokenTransaction> AdjustAsync(string userId, int amount, string reason, string adminUserId, CancellationToken cancellationToken);
}

public sealed class TokenService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    TimeProvider timeProvider,
    GenerationMetrics metrics) : ITokenService
{
    public async Task<TokenTransaction> DebitAsync(
        GenerationDbContext db,
        string userId,
        long generationId,
        CancellationToken cancellationToken)
    {
        string key = $"generation-debit:{generationId}";
        TokenTransaction? existing = await db.TokenTransactions.SingleOrDefaultAsync(
            value => value.UserId == userId && value.IdempotencyKey == key, cancellationToken);
        if (existing is not null) return existing;

        ApplicationUser user = await db.Users.SingleAsync(value => value.Id == userId, cancellationToken);
        if (user.TokenBalance < 1) throw new InvalidOperationException("TOKEN_BALANCE_INSUFFICIENT");
        user.TokenBalance--;
        metrics.TokensSpent.Add(1);
        DateTimeOffset now = timeProvider.GetUtcNow();
        TokenTransaction transaction = new()
        {
            UserId = userId,
            Amount = -1,
            Type = TokenTransactionType.GenerationDebit,
            Status = TokenTransactionStatus.Completed,
            GenerationId = generationId,
            IdempotencyKey = key,
            Description = "Токен списан за создание видео",
            CreatedAtUtc = now,
            CompletedAtUtc = now,
            BalanceAfter = user.TokenBalance
        };
        db.TokenTransactions.Add(transaction);
        return transaction;
    }

    public async Task<TokenTransaction?> RefundAsync(
        string userId,
        long generationId,
        string reason,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        bool wasDebited = await db.TokenTransactions.AnyAsync(
            value =>
                value.UserId == userId &&
                value.GenerationId == generationId &&
                value.Type == TokenTransactionType.GenerationDebit &&
                value.Status == TokenTransactionStatus.Completed,
            cancellationToken);
        if (!wasDebited)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        TokenTransaction? result = await CreditCoreAsync(
            db, userId, 1, TokenTransactionType.GenerationRefund,
            $"generation-refund:{generationId}", reason, null, null, generationId, cancellationToken);
        if (result is not null)
        {
            metrics.TokensRefunded.Add(1);
            Generation? generation = await db.Generations.SingleOrDefaultAsync(
                value => value.Id == generationId, cancellationToken);
            if (generation is not null) generation.TokenRefunded = true;
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<TokenTransaction> CreditAsync(
        string userId,
        int amount,
        TokenTransactionType type,
        string idempotencyKey,
        string description,
        long? purchaseId,
        long? referralId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        TokenTransaction? result = await CreditCoreAsync(
            db, userId, amount, type, idempotencyKey, description,
            purchaseId, referralId, null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result!;
    }

    public async Task<TokenTransaction> AdjustAsync(
        string userId,
        int amount,
        string reason,
        string adminUserId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("TOKEN_ADJUSTMENT_REASON_REQUIRED");
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        ApplicationUser user = await db.Users.SingleAsync(value => value.Id == userId, cancellationToken);
        if (user.TokenBalance + amount < 0) throw new InvalidOperationException("TOKEN_BALANCE_NEGATIVE");
        user.TokenBalance += amount;
        DateTimeOffset now = timeProvider.GetUtcNow();
        TokenTransaction result = new()
        {
            UserId = userId,
            Amount = amount,
            Type = TokenTransactionType.AdminAdjustment,
            Status = TokenTransactionStatus.Completed,
            IdempotencyKey = $"admin-adjustment:{adminUserId}:{Guid.NewGuid():N}",
            Description = reason[..Math.Min(reason.Length, 256)],
            CreatedAtUtc = now,
            CompletedAtUtc = now,
            BalanceAfter = user.TokenBalance
        };
        db.TokenTransactions.Add(result);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<TokenTransaction?> CreditCoreAsync(
        GenerationDbContext db,
        string userId,
        int amount,
        TokenTransactionType type,
        string idempotencyKey,
        string description,
        long? purchaseId,
        long? referralId,
        long? generationId,
        CancellationToken cancellationToken)
    {
        TokenTransaction? existing = await db.TokenTransactions.SingleOrDefaultAsync(
            value => value.UserId == userId && value.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null) return existing;
        ApplicationUser user = await db.Users.SingleAsync(value => value.Id == userId, cancellationToken);
        user.TokenBalance += amount;
        if (type == TokenTransactionType.ReferralReward) metrics.TokensReferral.Add(amount);
        if (type == TokenTransactionType.Purchase) metrics.TokensPurchased.Add(amount);
        DateTimeOffset now = timeProvider.GetUtcNow();
        TokenTransaction transaction = new()
        {
            UserId = userId,
            Amount = amount,
            Type = type,
            Status = TokenTransactionStatus.Completed,
            GenerationId = generationId,
            TokenPurchaseId = purchaseId,
            ReferralId = referralId,
            IdempotencyKey = idempotencyKey,
            Description = description[..Math.Min(description.Length, 256)],
            CreatedAtUtc = now,
            CompletedAtUtc = now,
            BalanceAfter = user.TokenBalance
        };
        db.TokenTransactions.Add(transaction);
        return transaction;
    }
}
