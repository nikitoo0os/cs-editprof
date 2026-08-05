using System.ComponentModel.DataAnnotations;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages.Account;

public sealed class ResendConfirmationModel(
    UserManager<ApplicationUser> users,
    IEmailSender emailSender) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public bool Sent { get; private set; }

    public sealed class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();
        ApplicationUser? user = await users.FindByEmailAsync(Input.Email.Trim());
        if (user is not null && !await users.IsEmailConfirmedAsync(user))
        {
            string token = await users.GenerateEmailConfirmationTokenAsync(user);
            string link = Url.Page(
                "/Account/ConfirmEmail",
                null,
                new { userId = user.Id, token },
                Request.Scheme) ?? string.Empty;
            await emailSender.SendAsync(
                user.Email!,
                "Подтвердите email",
                link,
                cancellationToken);
        }
        Sent = true;
        return Page();
    }
}
