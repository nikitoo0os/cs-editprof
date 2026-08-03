using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Pages;

public sealed class TimelineModel(
    IInteractiveTimelineDirector timelineDirector,
    IDbContextFactory<GenerationDbContext> dbFactory,
    IWebHostEnvironment environment) : PageModel
{
    public InteractiveTimelineView Timeline { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using GenerationDbContext db =
                await dbFactory.CreateDbContextAsync(cancellationToken);
            Generation? generation = await db.Generations.AsNoTracking().SingleOrDefaultAsync(
                value => value.PublicId == publicId, cancellationToken);
            if (generation is null || !GenerationAccess.CanRead(generation, User, environment))
                return NotFound();
            Timeline = await timelineDirector.GetOrCreateAsync(
                publicId,
                cancellationToken);
            return Page();
        }
        catch (TimelineNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return RedirectToPage("/Generation", new { publicId });
        }
    }
}
