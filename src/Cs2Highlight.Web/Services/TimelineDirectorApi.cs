using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed record SetTimelineModeRequest(
    TimelineDirectorMode Mode,
    string? ConcurrencyToken);

public sealed record TimelineConcurrencyRequest(
    string? ConcurrencyToken);

public static class TimelineDirectorApi
{
    public static IEndpointRouteBuilder MapTimelineDirectorApi(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup(
            "/api/generations/{publicId}/timeline");

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

        return endpoints;
    }

    private static async Task<IResult> RunAsync(
        Func<Task<InteractiveTimelineView>> action)
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
