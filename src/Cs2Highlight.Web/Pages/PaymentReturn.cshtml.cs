using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages;

public sealed class PaymentReturnModel(PaymentService payments) : PageModel
{
    public PaymentStatus Status { get; private set; }
    public string PublicId { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        PublicId = publicId;
        try
        {
            Status = await payments.RefreshAsync(publicId, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            Status = PaymentStatus.Pending;
        }

        return Status == PaymentStatus.Succeeded
            ? RedirectToPage("/Generation", new { publicId })
            : Page();
    }
}
