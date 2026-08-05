using System.ComponentModel.DataAnnotations;
using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages.Account;

public sealed class ResetPasswordModel(UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? UserId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }
    public bool Completed { get; private set; }
    public sealed class InputModel { [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = string.Empty; [Required, DataType(DataType.Password), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty; }
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Token)) return Page();
        ApplicationUser? user = await users.FindByIdAsync(UserId);
        if (user is null) { ModelState.AddModelError(string.Empty, "Ссылка недействительна."); return Page(); }
        IdentityResult result = await users.ResetPasswordAsync(user, Token, Input.Password);
        if (!result.Succeeded) { foreach (IdentityError error in result.Errors) ModelState.AddModelError(string.Empty, error.Description); return Page(); }
        Completed = true;
        return Page();
    }
}
