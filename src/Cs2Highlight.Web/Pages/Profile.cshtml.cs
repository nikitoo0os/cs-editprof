using System.Security.Claims;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

[Authorize]
public sealed class ProfileModel(UserManager<ApplicationUser> users, IDbContextFactory<GenerationDbContext> dbFactory) : PageModel
{
    public ApplicationUser UserAccount { get; private set; } = null!;
    public IReadOnlyList<Generation> Generations { get; private set; } = [];
    public IReadOnlyList<TokenTransaction> Transactions { get; private set; } = [];
    public IReadOnlyList<Referral> Referrals { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostThemeAsync(string theme, CancellationToken cancellationToken)
    {
        UserAccount = await users.GetUserAsync(User) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        UserAccount.ThemePreference = theme is "printstream" ? "printstream" : "redline";
        await users.UpdateAsync(UserAccount);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        string id = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        UserAccount = await users.FindByIdAsync(id) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation[] generations = await db.Generations.AsNoTracking()
            .Where(value => value.UserId == id)
            .ToArrayAsync(cancellationToken);
        Generations = generations
            .OrderByDescending(value => value.CreatedAt)
            .Take(50)
            .ToArray();

        TokenTransaction[] transactions = await db.TokenTransactions.AsNoTracking()
            .Where(value => value.UserId == id)
            .ToArrayAsync(cancellationToken);
        Transactions = transactions
            .OrderByDescending(value => value.CreatedAtUtc)
            .Take(50)
            .ToArray();

        Referral[] referrals = await db.Referrals.AsNoTracking()
            .Where(value => value.ReferrerUserId == id)
            .ToArrayAsync(cancellationToken);
        Referrals = referrals
            .OrderByDescending(value => value.CreatedAtUtc)
            .ToArray();
    }
}
