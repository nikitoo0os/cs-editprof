using System.ComponentModel.DataAnnotations;
using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages.Account;

[Authorize]
public sealed class ChangePasswordModel(UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public sealed class InputModel { [Required, DataType(DataType.Password)] public string CurrentPassword { get; set; } = string.Empty; [Required, StringLength(100, MinimumLength = 10), DataType(DataType.Password)] public string NewPassword { get; set; } = string.Empty; [Required, DataType(DataType.Password), Compare(nameof(NewPassword))] public string ConfirmPassword { get; set; } = string.Empty; }
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();
        ApplicationUser user = await users.GetUserAsync(User) ?? throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        IdentityResult result = await users.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded) { foreach (IdentityError error in result.Errors) ModelState.AddModelError(string.Empty, error.Description); return Page(); }
        return RedirectToPage("/Profile");
    }
}
