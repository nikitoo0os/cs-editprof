using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

public sealed class CheckoutModel(
    IDbContextFactory<GenerationDbContext> dbFactory,
    PaymentService payments) : PageModel
{
    public Generation Generation { get; private set; } = null!;
    public int DemoCount { get; private set; }
    public int SelectedCount { get; private set; }
    public IReadOnlyList<string> Categories { get; private set; } = [];
    public GenerationMovieSettings? MovieSettings { get; private set; }

    public async Task<IActionResult> OnGetAsync(string publicId, CancellationToken cancellationToken)
    {
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, cancellationToken);
        if (generation is null) return NotFound();
        if (generation.Status != GenerationStatus.AwaitingPayment)
            return RedirectToPage("/Generation", new { publicId });
        Generation = generation;
        MovieSettings = await db.GenerationMovieSettings.AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.GenerationId == generation.Id,
                cancellationToken);
        DemoCount = await db.GenerationDemos.CountAsync(
            value => value.GenerationId == generation.Id,
            cancellationToken);
        Categories = await db.GenerationHighlights.AsNoTracking()
            .Where(value =>
                value.GenerationId == generation.Id &&
                value.SelectedByUser)
            .Select(value => value.Type)
            .Distinct()
            .OrderBy(value => value)
            .ToArrayAsync(cancellationToken);
        SelectedCount = await db.GenerationHighlights.CountAsync(
            value =>
                value.GenerationId == generation.Id &&
                value.SelectedByUser,
            cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string publicId, CancellationToken cancellationToken)
    {
        await payments.CreateAsync(publicId, cancellationToken);
        return RedirectToPage("/TestPayment", new { publicId });
    }
}
