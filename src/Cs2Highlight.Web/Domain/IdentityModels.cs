using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace Cs2Highlight.Web.Domain;

public sealed class ApplicationUser : IdentityUser
{
    public DateTimeOffset RegisteredAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int TokenBalance { get; set; }
    [MaxLength(32)] public string ReferralCode { get; set; } = string.Empty;
    [MaxLength(450)] public string? ReferrerUserId { get; set; }
    [MaxLength(32)] public string ThemePreference { get; set; } = "redline";
    public DateTimeOffset? ReferralRewardedAtUtc { get; set; }
    public List<UserConsent> Consents { get; set; } = [];
    public List<TokenTransaction> TokenTransactions { get; set; } = [];
    public List<TokenPurchase> TokenPurchases { get; set; } = [];
    public List<Generation> Generations { get; set; } = [];
}

public enum ConsentType
{
    PrivacyPolicy,
    PersonalDataProcessing
}

public sealed class UserConsent
{
    public long Id { get; set; }
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public ConsentType ConsentType { get; set; }
    [MaxLength(32)] public string DocumentVersion { get; set; } = "1.0";
    public DateTimeOffset AcceptedAtUtc { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
    [MaxLength(512)] public string? UserAgent { get; set; }
}

public enum TokenTransactionType
{
    Purchase,
    GenerationDebit,
    GenerationRefund,
    ReferralReward,
    AdminAdjustment,
    Chargeback,
    Expiration
}

public enum TokenTransactionStatus
{
    Pending,
    Completed,
    Rejected,
    Reversed
}

public sealed class TokenTransaction
{
    public long Id { get; set; }
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public int Amount { get; set; }
    public TokenTransactionType Type { get; set; }
    public TokenTransactionStatus Status { get; set; }
    public long? GenerationId { get; set; }
    public Generation? Generation { get; set; }
    public long? TokenPurchaseId { get; set; }
    public TokenPurchase? TokenPurchase { get; set; }
    public long? ReferralId { get; set; }
    public Referral? Referral { get; set; }
    [MaxLength(128)] public string IdempotencyKey { get; set; } = string.Empty;
    [MaxLength(256)] public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int BalanceAfter { get; set; }
}

public sealed class TokenPackage
{
    public long Id { get; set; }
    [MaxLength(64)] public string Code { get; set; } = string.Empty;
    [MaxLength(128)] public string Name { get; set; } = string.Empty;
    public int TokenAmount { get; set; }
    public long PriceAmountMinor { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "RUB";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public enum TokenPurchaseStatus
{
    Pending,
    Succeeded,
    Failed,
    Cancelled,
    Refunded
}

public sealed class TokenPurchase
{
    public long Id { get; set; }
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public long TokenPackageId { get; set; }
    public TokenPackage TokenPackage { get; set; } = null!;
    [MaxLength(32)] public string Provider { get; set; } = "Test";
    [MaxLength(128)] public string? ProviderPaymentId { get; set; }
    [MaxLength(1024)] public string? ConfirmationUrl { get; set; }
    [MaxLength(128)] public string IdempotencyKey { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "RUB";
    public TokenPurchaseStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PaidAtUtc { get; set; }
    [MaxLength(64)] public string? FailureCode { get; set; }
}

public sealed class Referral
{
    public long Id { get; set; }
    [MaxLength(450)] public string ReferrerUserId { get; set; } = string.Empty;
    public ApplicationUser ReferrerUser { get; set; } = null!;
    [MaxLength(450)] public string ReferredUserId { get; set; } = string.Empty;
    public ApplicationUser ReferredUser { get; set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? RewardedAtUtc { get; set; }
}

public enum CleanupStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
