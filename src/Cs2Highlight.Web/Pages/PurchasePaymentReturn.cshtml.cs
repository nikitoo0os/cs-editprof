using System.Security.Claims;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages;

[Authorize]
public sealed class PurchasePaymentReturnModel(TokenPaymentService payments) : PageModel
{
    public TokenPurchaseStatus Status { get; private set; }
    [BindProperty(SupportsGet = true)] public long PurchaseId { get; set; }

    public async Task<IActionResult> OnGetAsync(
        CancellationToken cancellationToken)
    {
        string userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        try
        {
            Status = await payments.RefreshAsync(
                PurchaseId,
                userId,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            Status = TokenPurchaseStatus.Pending;
        }
        if (Status == TokenPurchaseStatus.Succeeded)
            return RedirectToPage("/Profile");
        return Page();
    }
}
