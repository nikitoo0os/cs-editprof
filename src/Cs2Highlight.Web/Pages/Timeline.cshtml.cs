using Cs2Highlight.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Cs2Highlight.Web.Pages;

public sealed class TimelineModel(
    IInteractiveTimelineDirector timelineDirector) : PageModel
{
    public InteractiveTimelineView Timeline { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        try
        {
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
