using System.Security.Claims;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Services;

public static class GenerationAccess
{
    public static bool CanRead(Generation generation, ClaimsPrincipal principal, IWebHostEnvironment environment)
    {
        if (generation.UserId is null)
            return environment.IsEnvironment("Testing") || principal.IsInRole("Admin");
        return principal.IsInRole("Admin") ||
            string.Equals(principal.FindFirstValue(ClaimTypes.NameIdentifier), generation.UserId, StringComparison.Ordinal);
    }
}
