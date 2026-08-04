using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed record SetTimelineModeRequest(
    TimelineDirectorMode Mode,
    string? ConcurrencyToken);

public sealed record TimelineConcurrencyRequest(
    string? ConcurrencyToken);

public static class TimelineDirectorApi
{
    private static readonly System.Text.Json.JsonSerializerOptions Json =
        new(System.Text.Json.JsonSerializerDefaults.Web);
    public static IEndpointRouteBuilder MapTimelineDirectorApi(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup(
            "/api/generations/{publicId}/timeline");
        group.AddEndpointFilter(async (context, next) =>
        {
            string? publicId = context.HttpContext.Request.RouteValues["publicId"] as string;
            IDbContextFactory<GenerationDbContext> factory = context.HttpContext.RequestServices
                .GetRequiredService<IDbContextFactory<GenerationDbContext>>();
            IWebHostEnvironment environment = context.HttpContext.RequestServices
                .GetRequiredService<IWebHostEnvironment>();
            await using GenerationDbContext db = await factory.CreateDbContextAsync(
                context.HttpContext.RequestAborted);
            Generation? generation = await db.Generations.AsNoTracking().SingleOrDefaultAsync(
                value => value.PublicId == publicId, context.HttpContext.RequestAborted);
            return generation is null || !GenerationAccess.CanRead(
                generation, context.HttpContext.User, environment)
                ? Results.NotFound()
                : await next(context);
        });

        group.MapGet("/", (
            string publicId,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.GetOrCreateAsync(
                publicId,
                cancellationToken)));

        group.MapPut("/mode", (
            string publicId,
            SetTimelineModeRequest request,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.SetModeAsync(
                publicId,
                request.Mode,
                request.ConcurrencyToken,
                cancellationToken)));

        group.MapPost("/anchors", (
            string publicId,
            AddTimelineAnchorRequest request,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.AddAnchorAsync(
                publicId,
                request,
                cancellationToken)));

        group.MapPut("/anchors/{anchorId}", (
            string publicId,
            string anchorId,
            UpdateTimelineAnchorRequest request,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.UpdateAnchorAsync(
                publicId,
                anchorId,
                request,
                cancellationToken)));

        group.MapDelete("/anchors/{anchorId}", (
            string publicId,
            string anchorId,
            string? concurrencyToken,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.DeleteAnchorAsync(
                publicId,
                anchorId,
                concurrencyToken,
                cancellationToken)));

        group.MapPost("/suggest", (
            string publicId,
            TimelineConcurrencyRequest request,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.SuggestAsync(
                publicId,
                request.ConcurrencyToken,
                cancellationToken)));

        group.MapPost("/undo", (
            string publicId,
            TimelineConcurrencyRequest request,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.UndoAsync(
                publicId,
                request.ConcurrencyToken,
                cancellationToken)));

        group.MapPost("/redo", (
            string publicId,
            TimelineConcurrencyRequest request,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.RedoAsync(
                publicId,
                request.ConcurrencyToken,
                cancellationToken)));

        group.MapPost("/confirm", (
            string publicId,
            TimelineConcurrencyRequest request,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.ConfirmAsync(
                publicId,
                request.ConcurrencyToken,
                cancellationToken)));

        group.MapGet("/regions/{regionId}/preview", (
            string publicId,
            string regionId,
            IInteractiveTimelineDirector director,
            CancellationToken cancellationToken) =>
            RunAsync(() => director.GetRegionPreviewAsync(
                publicId,
                regionId,
                cancellationToken)));

        group.MapGet("/regions/{regionId}/camera-preview", async (
            string publicId,
            string regionId,
            IDbContextFactory<GenerationDbContext> dbFactory,
            GenerationStorage storage,
            CancellationToken cancellationToken) =>
        {
            await using GenerationDbContext db =
                await dbFactory.CreateDbContextAsync(cancellationToken);
            Generation? generation = await db.Generations.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.PublicId == publicId,
                    cancellationToken);
            if (generation is null)
                return Results.NotFound();
            GenerationTimelinePlan? timeline =
                await db.GenerationTimelinePlans.AsNoTracking()
                    .SingleOrDefaultAsync(
                        value => value.GenerationId == generation.Id,
                        cancellationToken);
            if (timeline is null)
                return Results.NotFound();
            GenerationTimelineGap? gap =
                await db.GenerationTimelineGaps.AsNoTracking()
                    .SingleOrDefaultAsync(
                        value =>
                            value.TimelinePlanId == timeline.Id &&
                            value.GapId == regionId,
                        cancellationToken);
            LocalTimelineRegionPlan? region = gap is null
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<
                    LocalTimelineRegionPlan>(
                    gap.PlanJson,
                    Json);
            if (region is null || region.CameraShots.Count == 0)
                return Results.NotFound();
            string shotId = region.CameraShots[0].Id;
            string? path = await db.GenerationCameraShots.AsNoTracking()
                .Where(value =>
                    value.GenerationId == generation.Id &&
                    value.ShotId == shotId)
                .Select(value => value.PreviewPath)
                .SingleOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Results.NotFound();
            string full = Path.GetFullPath(path);
            storage.EnsureWithinRoot(full);
            return Results.File(
                full,
                "video/mp4",
                enableRangeProcessing: true);
        });

        return endpoints;
    }

    private static async Task<IResult> RunAsync<T>(
        Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (TimelineNotFoundException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (TimelineConflictException exception)
        {
            return Results.Conflict(new
            {
                error = exception.Message,
                recoverable = true
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new
            {
                error = "TIMELINE_REVISION_CONFLICT",
                recoverable = true
            });
        }
        catch (TimelineValidationException exception)
        {
            return Results.ValidationProblem(
                new Dictionary<string, string[]>
                {
                    ["timeline"] = [exception.Message]
                });
        }
    }
}
