using System.ComponentModel.DataAnnotations;
using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages.Account;

public sealed class LoginModel(SignInManager<ApplicationUser> signIn) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public bool Registered { get; set; }
    public sealed class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        public bool RememberMe { get; set; }
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return Page();
        Microsoft.AspNetCore.Identity.SignInResult result = await signIn.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/Profile");
        if (result.IsLockedOut) ModelState.AddModelError(string.Empty, "Вход временно заблокирован. Попробуйте позже.");
        else ModelState.AddModelError(string.Empty, "Неверный email или пароль.");
        return Page();
    }
}
