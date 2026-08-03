using System.Security.Claims;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

[Authorize]
public sealed class PurchaseModel(IDbContextFactory<GenerationDbContext> dbFactory, ITokenService tokens, TimeProvider timeProvider) : PageModel
{
    public IReadOnlyList<TokenPackage> Packages { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Packages = await db.TokenPackages.AsNoTracking()
            .Where(value => value.IsActive).OrderBy(value => value.SortOrder)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostBuyAsync(string code, CancellationToken cancellationToken)
    {
        string userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        TokenPackage? package = await db.TokenPackages.SingleOrDefaultAsync(
            value => value.Code == code && value.IsActive, cancellationToken);
        if (package is null) return NotFound();
        DateTimeOffset now = timeProvider.GetUtcNow();
        TokenPurchase purchase = new()
        {
            UserId = userId, TokenPackageId = package.Id, Provider = "Test",
            ProviderPaymentId = $"test_tokens_{Guid.NewGuid():N}",
            IdempotencyKey = $"token-purchase:{userId}:{Guid.NewGuid():N}",
            AmountMinor = package.PriceAmountMinor, Currency = package.Currency,
            Status = TokenPurchaseStatus.Succeeded, CreatedAtUtc = now, PaidAtUtc = now
        };
        db.TokenPurchases.Add(purchase);
        await db.SaveChangesAsync(cancellationToken);
        await tokens.CreditAsync(userId, package.TokenAmount,
            TokenTransactionType.Purchase, $"purchase:{purchase.Id}",
            $"Покупка: {package.Name}", purchase.Id, null, cancellationToken);
        return RedirectToPage("/Profile");
    }
}
