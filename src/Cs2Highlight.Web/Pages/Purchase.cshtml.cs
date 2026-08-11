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
public sealed class PurchaseModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    TokenPaymentService payments,
    PaymentOptions paymentOptions) : PageModel
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

    public async Task<IActionResult> OnPostBuyAsync(
        string code,
        CancellationToken cancellationToken)
    {
        string userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        try
        {
            TokenPaymentLaunch launch = await payments.CreateAsync(
                userId,
                code,
                BuildReturnUrl("purchase/payment-return"),
                cancellationToken);
            return Redirect(launch.ConfirmationUrl);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await OnGetAsync(cancellationToken);
            return Page();
        }
    }

    private string BuildReturnUrl(string path)
    {
        string baseUrl = paymentOptions.ReturnUrlBase.TrimEnd('/');
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? configured))
            return $"{configured.AbsoluteUri.TrimEnd('/')}/{path.TrimStart('/')}";
        return $"{Request.Scheme}://{Request.Host}/{path.TrimStart('/')}";
    }
}
