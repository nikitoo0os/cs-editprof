using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

[EnableRateLimiting("uploads")]
public sealed class IndexModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    DemoUploadService uploads,
    GenerationWakeSignal queue,
    UploadOptions options,
    PaymentOptions paymentOptions,
    TimeProvider timeProvider,
    UserManager<ApplicationUser> userManager,
    ITokenService tokens,
    GenerationMetrics metrics) : PageModel
{
    [BindProperty] public List<IFormFile> DemoFiles { get; set; } = [];
    public int MaximumFiles => options.MaximumFilesPerGeneration;
    public long MaximumFileSizeBytes => options.MaximumFileSizeBytes;
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostUploadAsync(CancellationToken cancellationToken)
    {
        if (!User.Identity?.IsAuthenticated ?? true)
            return RedirectToPage("/Account/Login", new { returnUrl = Request.Path });
        ApplicationUser user = await userManager.GetUserAsync(User) ??
            throw new InvalidOperationException("AUTHENTICATED_USER_NOT_FOUND");
        DateTimeOffset now = timeProvider.GetUtcNow();
        Generation generation = new()
        {
            PublicId = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            PriceAmountMinor = paymentOptions.PriceAmountMinor,
            PriceCurrency = paymentOptions.Currency.ToUpperInvariant(),
            UserId = user.Id,
            QueueEnteredAtUtc = now
        };
        try
        {
            GenerationStateMachine.Transition(generation, GenerationStatus.Uploading, now);
            IReadOnlyList<StoredUpload> stored = await uploads.SaveAsync(
                generation.PublicId, DemoFiles, cancellationToken);
            await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(cancellationToken);
            db.Generations.Add(generation);
            int order = 0;
            foreach (StoredUpload upload in stored.Where(value => !value.Duplicate))
            {
                generation.Demos.Add(new GenerationDemo
                {
                    OriginalFileName = upload.OriginalFileName,
                    StoredPath = upload.StoredPath,
                    FileSizeBytes = upload.Size,
                    Sha256 = upload.Sha256,
                    UploadOrder = ++order,
                    AnalysisStatus = DemoAnalysisStatus.Pending
                });
            }
            int duplicateCount = stored.Count(value => value.Duplicate);
            if (duplicateCount > 0)
            {
                generation.Events.Add(new GenerationEvent
                {
                    Level = "Warning",
                    Stage = "Uploading",
                    Message = $"Duplicate demo skipped: {duplicateCount}.",
                    ProgressPercent = 10,
                    CreatedAt = now
                });
            }
            if (generation.Demos.Count == 0) throw new InvalidOperationException("ALL_FILES_DUPLICATE");
            GenerationStateMachine.Transition(generation, GenerationStatus.Uploaded, now);
            generation.ProgressPercent = 10;
            GenerationStateMachine.Transition(generation, GenerationStatus.QueuedForAnalysis, now);
            await db.SaveChangesAsync(cancellationToken);
            await tokens.DebitAsync(db, user.Id, generation.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            metrics.GenerationCreated.Add(1);
            queue.Wake();
            return RedirectToPage("/Generation", new { publicId = generation.PublicId });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = exception.Message;
            return Page();
        }
    }
}
