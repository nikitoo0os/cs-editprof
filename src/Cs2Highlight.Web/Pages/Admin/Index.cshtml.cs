using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public sealed class IndexModel(
    UserManager<ApplicationUser> users,
    IDbContextFactory<GenerationDbContext> dbFactory,
    ITokenService tokens) : PageModel
{
    public int UserCount { get; private set; }
    public int ActiveGenerations { get; private set; }
    public int QueueCount { get; private set; }
    public long StorageBytes { get; private set; }
    public string? Message { get; private set; }

    [BindProperty] public string UserEmail { get; set; } = string.Empty;
    [BindProperty] public int Amount { get; set; }
    [BindProperty] public string Reason { get; set; } = string.Empty;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        UserCount = await db.Users.CountAsync(cancellationToken);
        ActiveGenerations = await db.Generations.CountAsync(value =>
            value.Status != GenerationStatus.Completed &&
            value.Status != GenerationStatus.CompletedWithWarnings &&
            value.Status != GenerationStatus.Failed &&
            value.Status != GenerationStatus.Cancelled &&
            value.Status != GenerationStatus.Expired, cancellationToken);
        QueueCount = await db.Generations.CountAsync(value =>
            value.Status == GenerationStatus.QueuedForAnalysis ||
            value.Status == GenerationStatus.QueuedForGeneration, cancellationToken);
    }

    public async Task<IActionResult> OnPostAdjustAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Reason))
        {
            ModelState.AddModelError(nameof(Reason), "Причина обязательна.");
            await OnGetAsync(cancellationToken);
            return Page();
        }
        ApplicationUser? user = await users.FindByEmailAsync(UserEmail);
        if (user is null) return NotFound();
        string adminId = users.GetUserId(User) ?? throw new InvalidOperationException("ADMIN_ID_MISSING");
        await tokens.AdjustAsync(user.Id, Amount, Reason, adminId, cancellationToken);
        Message = "Ledger обновлён.";
        await OnGetAsync(cancellationToken);
        return Page();
    }
}
