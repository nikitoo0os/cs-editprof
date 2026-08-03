using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages;

public sealed class TestPaymentModel(PaymentService payments) : PageModel
{
    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(
        string publicId,
        string decision,
        CancellationToken cancellationToken)
    {
        await payments.ConfirmTestAsync(publicId, decision == "success", cancellationToken);
        return decision == "success"
            ? RedirectToPage("/Generation", new { publicId })
            : RedirectToPage("/Checkout", new { publicId });
    }
}
