using System.ComponentModel.DataAnnotations;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages.Account;

public sealed class RegisterModel(
    UserManager<ApplicationUser> users,
    IDbContextFactory<GenerationDbContext> dbFactory,
    LegalOptions legal,
    IEmailSender emailSender,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public string? Ref { get; set; }
    public string PrivacyVersion => legal.PrivacyPolicyVersion;
    public string PersonalDataVersion => legal.PersonalDataVersion;
    public string? Message { get; private set; }

    public sealed class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        [Required, DataType(DataType.Password), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
        public bool PrivacyAccepted { get; set; }
        public bool PersonalDataAccepted { get; set; }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!Input.PrivacyAccepted) ModelState.AddModelError(nameof(Input.PrivacyAccepted), "Нужно принять политику конфиденциальности.");
        if (!Input.PersonalDataAccepted) ModelState.AddModelError(nameof(Input.PersonalDataAccepted), "Нужно дать отдельное согласие на обработку персональных данных.");
        if (!ModelState.IsValid) return Page();
        string? referrerId = null;
        if (!string.IsNullOrWhiteSpace(Ref))
            referrerId = await users.Users.Where(value => value.ReferralCode == Ref.Trim())
                .Select(value => value.Id).SingleOrDefaultAsync(cancellationToken);
        ApplicationUser user = new()
        {
            UserName = Input.Email.Trim(), Email = Input.Email.Trim(),
            RegisteredAtUtc = timeProvider.GetUtcNow(),
            ReferralCode = await NewReferralCodeAsync(cancellationToken),
            ReferrerUserId = referrerId
        };
        IdentityResult result = await users.CreateAsync(user, Input.Password);
        if (!result.Succeeded)
        {
            foreach (IdentityError error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }
        await users.AddToRoleAsync(user, "User");
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.UserConsents.AddRange(
            new UserConsent { UserId = user.Id, ConsentType = ConsentType.PrivacyPolicy, DocumentVersion = legal.PrivacyPolicyVersion, AcceptedAtUtc = now, IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), UserAgent = Request.Headers.UserAgent.ToString()[..Math.Min(512, Request.Headers.UserAgent.ToString().Length)] },
            new UserConsent { UserId = user.Id, ConsentType = ConsentType.PersonalDataProcessing, DocumentVersion = legal.PersonalDataVersion, AcceptedAtUtc = now, IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(), UserAgent = Request.Headers.UserAgent.ToString()[..Math.Min(512, Request.Headers.UserAgent.ToString().Length)] });
        if (referrerId is not null)
            db.Referrals.Add(new Referral { ReferrerUserId = referrerId, ReferredUserId = user.Id, CreatedAtUtc = now });
        await db.SaveChangesAsync(cancellationToken);
        string token = await users.GenerateEmailConfirmationTokenAsync(user);
        string link = Url.Page("/Account/ConfirmEmail", null, new { userId = user.Id, token }, Request.Scheme) ?? string.Empty;
        await emailSender.SendAsync(user.Email, "Подтвердите email", link, cancellationToken);
        return RedirectToPage("/Account/Login", new { returnUrl = ReturnUrl, registered = true });
    }

    private async Task<string> NewReferralCodeAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            string code = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..10];
            if (!await users.Users.AnyAsync(value => value.ReferralCode == code, cancellationToken)) return code;
        }
        throw new InvalidOperationException("REFERRAL_CODE_GENERATION_FAILED");
    }
}
