using System.Security.Claims;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;

namespace Cs2Highlight.Web.Hubs;

public sealed class GenerationHub(
    IDbContextFactory<GenerationDbContext> dbFactory,
    IWebHostEnvironment environment) : Hub
{
    public async Task Subscribe(string publicId)
    {
        if (publicId.Length is < 20 or > 64 || publicId.Any(value => !char.IsAsciiLetterOrDigit(value)))
            throw new HubException("Invalid generation ID.");
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(Context.ConnectionAborted);
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.PublicId == publicId, Context.ConnectionAborted);
        if (generation is null || !GenerationAccess.CanRead(
                generation, Context.User ?? new ClaimsPrincipal(), environment))
            throw new HubException("Generation not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, publicId);
    }
}
