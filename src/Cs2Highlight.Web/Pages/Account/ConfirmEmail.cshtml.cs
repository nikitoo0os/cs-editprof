using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages.Account;

public sealed class ConfirmEmailModel(UserManager<ApplicationUser> users, IDbContextFactory<GenerationDbContext> dbFactory, ITokenService tokens, TimeProvider timeProvider) : PageModel
{
    public bool Confirmed { get; private set; }
    public async Task OnGetAsync(string? userId, string? token, CancellationToken cancellationToken)
    {
        ApplicationUser? user = userId is null ? null : await users.FindByIdAsync(userId);
        if (user is null || token is null) return;
        IdentityResult result = await users.ConfirmEmailAsync(user, token);
        if (!result.Succeeded) return;
        Confirmed = true;
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Referral? referral = await db.Referrals.SingleOrDefaultAsync(value => value.ReferredUserId == user.Id, cancellationToken);
        if (referral?.ConfirmedAtUtc is null)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (referral is not null) { referral.ConfirmedAtUtc = now; await db.SaveChangesAsync(cancellationToken); await tokens.CreditAsync(referral.ReferrerUserId, 1, TokenTransactionType.ReferralReward, $"referral:{referral.Id}", "Награда за подтверждённого приглашённого пользователя", null, referral.Id, cancellationToken); referral.RewardedAtUtc = now; await db.SaveChangesAsync(cancellationToken); }
        }
    }
}
