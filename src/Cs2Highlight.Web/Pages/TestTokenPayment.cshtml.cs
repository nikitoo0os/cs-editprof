using System.Security.Claims;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages;

[Authorize]
public sealed class TestTokenPaymentModel(TokenPaymentService payments) : PageModel
{
    [BindProperty(SupportsGet = true)] public long PurchaseId { get; set; }

    public async Task<IActionResult> OnPostAsync(
        string decision,
        CancellationToken cancellationToken)
    {
        string userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        await payments.ConfirmTestAsync(
            PurchaseId,
            userId,
            decision == "success",
            cancellationToken);
        return decision == "success"
            ? RedirectToPage("/Profile")
            : RedirectToPage("/Purchase");
    }
}
